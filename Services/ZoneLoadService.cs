using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BGColliderType = FFXIVClientStructs.FFXIV.Common.Component.BGCollision.ColliderType;
using FFXIVClientStructs.FFXIV.Common.Math;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

public unsafe class ZoneLoadService : IDisposable
{
    private readonly IObjectTable objectTable;

    // S286: optional chat sink so origin/return diagnostics reach the USER (not just /xllog, which the
    // user can't see). Set by the plugin to chat.Print. Null-safe - log always fires regardless.
    public Action<string>? StatusReport { get; set; }
    private void Report(string msg) { log.Information(msg); StatusReport?.Invoke(msg); }
    // v0.7.259: notification hygiene. Non-essential status (saved origin, map hop, internal restore diagnostics) is
    // coordinate-level debug detail with no value to a normal player mid-session. ReportDebug logs it always but only
    // prints to chat when debug mode is on. Set by the plugin alongside the other debug-trace flags.
    public bool DebugMode { get; set; } = false;
    private void ReportDebug(string msg) { log.Information(msg); if (DebugMode) StatusReport?.Invoke(msg); }

    // S291: fired by the deferred home-restore poll the moment the home position is locked in. The
    // plugin uses this to disable the packet filter AFTER the actor is settled at home - NOT during the
    // reload. This mirrors Hyperborea, which keeps its packet firewall up through the entire revert and
    // only drops it once home; opening the filter mid-restore lets the SERVER's stale foreign-zone
    // position flood back and snap the actor away from home (the air-stop fling: the server never saw
    // the local flight, so its authoritative position is the pre-flight spot, far from where we landed).
    public System.Action? OnHomeRestoreComplete { get; set; }

    // S286: on-demand origin readout for /hms origin - prints the currently-recorded return target.
    public string DescribeOrigin()
    {
        if (savedZoneId == null)
            return "[HMSync] No origin recorded (not in an HMS session - nothing to return to).";
        var p = savedPosition.HasValue
            ? $"({savedPosition.Value.X:F2}, {savedPosition.Value.Y:F2}, {savedPosition.Value.Z:F2})"
            : "NULL (!)";
        return $"[HMSync] Origin: zone={savedZoneId} coords={p} | IsZoneLoaded={IsZoneLoaded} CurrentLoadedZone={CurrentLoadedZone}";
    }
    private readonly IPluginLog log;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider hookProvider;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;

    private delegate nint LoadZoneDelegate(nint a1, uint a2, int a3, byte a4, byte a5, byte a6);
    private Hook<LoadZoneDelegate>? loadZoneHook;

    // S320: fired the instant ANY zone change begins - the HMS-driven LoadZone (/hms load), the
    // load detour (a normal teleport / zone line / login), so subscribers can sanitise state that
    // must not carry across a zone load. The carpet subscribes its Disable() here. HMS-lifecycle
    // driven by design - NOT the Dalamud TerritoryChanged event (which fires after the fact).
    public event System.Action? ZoneWillChange;

    private delegate nint SetupTerritoryTypeDelegate(void* eventFramework, ushort territoryType);
    private nint setupTerritoryTypeAddr;
    // Cutscene direct-load (Strategy B1b - TitleEdit's CreateScene seam). CreateScene(path, ...) builds a walkable
    // scene from a level path; you override the scene by swapping the path arg (exactly TitleEdit's one-liner). We
    // hook the FUNCTION - resolved from the call-site's rel32 so it catches in-game callers, not just the lobby -
    // and while a stage load is in flight, substitute the stage bg for the path. Consume-once. No file redirect.
    private delegate int CreateSceneDelegate(string territoryPath, uint territoryId, nint p3, uint layerFilterKey, nint festivals, int p6, uint cfcId);
    private Hook<CreateSceneDelegate>? createSceneHook;
    public string? PendingStageBg;
    // v0.7.227: the swap stage currently live, kept AFTER the load (unlike PendingStageBg, which is consumed+nulled by
    // the CreateScene detour). This is the persistent "which stage are we in" record the spawn resolver and user-capture
    // key on - swap stages share a donor territory id, so CurrentLoadedZone is the DONOR, not the stage. Keying spawns by
    // CurrentLoadedZone leaked one stage's spawn to every co-donor stage (the bug). Set by LoadStage on a swap load,
    // cleared to null on a real (non-swap) load and on Revert. bg-string identity is unique per stage.
    public string? ActiveStageBg;
    private bool cutsceneSceneActive;   // a swapped cutscene scene is live under the origin's territory id

    // NB-18-RETEST (2026-08-02, Fable): one-shot layer-filter-key override consumed by CreateSceneDetour. Per the
    // disassembly, FindFilter (0x717930) consults the key ONLY when the layout TT is 0; the game zeroes the TT upstream
    // in SetTerritory's cmove (0x6034FF) when key!=0, so CreateScene's territoryId arg IS the value FindFilter keys on.
    // The earlier NB-18 test overrode only the key and left TT=919 → FindFilter(919) → base cast for every key (the
    // confound). When armed we reproduce the native key!=0 argument set at the seam: pass the override key AND zero the
    // territoryId arg (geometry still streams from territoryPath). Consume-once. Dual use: (a) the /hms casttest debug
    // harness arms it manually; (b) NB-18-SHIP arms it from ForcedCastKeys inside LoadZone (see below).
    public uint? PendingCastKeyOverride;

    // NB-18-SHIP (2026-08-02): per-zone forced layer-filter key for HMS virtual loads. The b10 retest CONFIRMED the
    // layer key drives layer-filtered LAYOUT/geometry (e.g. Terncliff 919's war-memorial cenotaph) independent of quest
    // state; it does NOT touch the ENPC cast (director/quest-owned). Baking the latest/finale key here makes an HMS
    // virtual load render that geometry deterministically on EVERY client — a static constant, so peers converge with
    // NO broadcast. Armed by LoadZone right before the native loader (so it fires ONLY on HMS's own loader, NEVER on a
    // real game visit to the same zone) and consumed by the synchronous CreateScene. Discipline: ONLY keys confirmed
    // in-game via /hms casttest belong here — measure, don't guess (same rule as the chat whitelist).
    private static readonly Dictionary<uint, uint> ForcedCastKeys = new()
    {
        { 919, 257010 },   // Terncliff — max/finale key: war-memorial cenotaph present (in-game validated 2026-08-02)
        // 1073 Elysion (u5e3): FILTER-KEY zone, NOT resident-hidden — the built town is gated behind a layer-filter key,
        // so DriveAdv759's IsVisible ratchet found nothing to reveal (b66 "nothing happens"). Offline the territory
        // exposes 8 layer-set keys (v0..v6); v6 fully-built town = 268557 (u5e3_v6_gmc00/env_location_v6/env_set_v6 match
        // ONLY this key). in-game validated via `hmst casttest 1073 268557` (2026-08-06) -> full complete map on cold load.
        { 1073, 268557 },  // Elysion — max/finished key: fully-built "funko-pop" town (v6)
        // 925 Terncliff (ec022): a separate instanced TerritoryType on the SHARED n4eb map (same LGB as 919/926). Its
        // NATIVE key 254213 is the ONE composition that filters every structure layer OFF (n4eb_building/object/pillar/
        // roof/tree/... are all FilterOp=NoMatch keys=254213) → an empty ocean frame; the geometry loads (collision is
        // present) but filtered-off layers get no draw objects, so DriveAdv759's per-instance IsVisible ratchet can't
        // reveal it. Fix is the SAME Gate-1 lever, borrowing a key that does NOT hide the buildings: 254214 is 926's
        // built-twin composition. in-game validated via `hmst casttest 925 254214` (2026-08-06) → full Terncliff renders
        // inside the 925 instance (no NPCs — event-instance populace not spawned; acceptable).
        { 925, 254214 },   // Terncliff (925 instance) — borrow 926's built key so the structures aren't filtered off
    };

    // NB-19 Phase 1: quest-populace spoof lifecycle callbacks (wired by the plugin to QuestSpoofService). ArmQuestSpoof
    // is invoked with the target territoryId immediately BEFORE the native loader (Gate 2 populace visibility is decided
    // during the load phases, so the spoof MUST be live before the load runs). DisarmQuestSpoof fires on Revert. Arming
    // inside HMS's own loader is the gate that keeps the spoof off real game visits — same discipline as ForcedCastKeys.
    public System.Action<uint>? ArmQuestSpoof;
    public System.Action? DisarmQuestSpoof;

    private int CreateSceneDetour(string territoryPath, uint territoryId, nint p3, uint layerFilterKey, nint festivals, int p6, uint cfcId)
    {
        uint effTT = territoryId;
        uint effKey = layerFilterKey;
        if (PendingCastKeyOverride.HasValue)
        {
            effKey = PendingCastKeyOverride.Value;
            effTT = 0;   // FindFilter consults the key only when TT==0 (mirrors the native key!=0 flow)
            PendingCastKeyOverride = null;   // consume-once
            log.Information("[HMSync] [CASTTEST] CreateScene terr " + territoryId + "->0  key " + layerFilterKey + "->" + effKey + "  path=" + territoryPath);
        }

        if (!string.IsNullOrEmpty(PendingStageBg))
        {
            var stageBg = PendingStageBg!;
            PendingStageBg = null;   // consume-once - keep origin's territoryId/layerFilterKey so layer resolution stays sane
            cutsceneSceneActive = true;
            log.Information("[CSS] CreateScene swap: " + territoryPath + " -> " + stageBg + " (terr=" + territoryId + " lfk=" + layerFilterKey + ")");
            return createSceneHook!.Original(stageBg, territoryId, p3, layerFilterKey, festivals, p6, cfcId);
        }
        cutsceneSceneActive = false;   // a real, un-swapped scene is now live (incl. the return reload)
        return createSceneHook!.Original(territoryPath, effTT, p3, effKey, festivals, p6, cfcId);
    }
    // S223: the MapEffect apply function (the game's door/wall/barrier toggle). Hyperborea sig.
    // Args: (mapEffectModule, layoutId:uint, state:ushort, flags:ushort). Driving this is how the
    // entry-ring barrier is released (BARRIERLIFE: barrier items flip State 0→4 on director seq 0→1).
    private delegate void MapEffectDelegate(nint module, uint layoutId, ushort state, ushort flags);
    private nint mapEffectAddr;
    // S224: instance-content setup/teardown (Hyperborea sigs). Without SetupInstanceContent, a
    // client-side load has no InstanceContentDirector → no MapEffects → the entry barrier can't be
    // released. Setup spins it up (creates director, populates MapEffects); Finalize tears it down.
    // ContentId = TerritoryType.ContentFinderCondition.Content.RowId; called as (EF, 0x80030000+id, id, 0).
    private delegate nint SetupInstanceContentDelegate(nint eventFramework, uint eventId, uint contentId, uint a4);
    private delegate byte FinalizeInstanceContentDelegate(nint eventFramework, uint eventId);
    private nint setupInstanceContentAddr;
    private nint finalizeInstanceContentAddr;
    private uint? lastInstanceContentId; // what we set up, so load/revert can finalize it

    // Saved state for revert
    private uint? savedZoneId;
    // S132: RE-ENABLED with the GetGraphics leaf-render-flag approach (no SetActive).
    // S128-S130 crashed because ILayoutInstance.SetActive corrupts instance lifecycle
    // (Deinit AV). S132 instead reaches the instance's REAL graphics object via
    // GetGraphics() and flips only DrawObject.IsVisible - a leaf render bit with no
    // lifecycle bookkeeping. Structurally cannot trip the teardown path that crashed.
    // Still gated so it can be killed instantly if testing shows any instability.
    private const bool EnableFurnitureDeDraw = true;

    // ──────────────────────────────────────────────────────────────────────────
    // S145 HOUSINGDIAG (read-only, default OFF): decoration-layer information extraction.
    //
    // GOAL: learn WHY the decoration layer (wallpaper/flooring materials, light level)
    // doesn't visually return on our faux Revert, by comparing the housing system's state
    // on a REAL entry vs. what our faux revert produces. We do NOT write any housing values
    // (that would only work on our own property and would mean writing onto a peer's apt -
    // out of scope). The fix we're hunting is "trigger the game's OWN re-apply path," so
    // first we must SEE that path. This is pure observation.
    //
    // The decoration layer lives in TWO places, both readable:
    //   1. DATA:   LayoutManager.IndoorAreaData (IndoorAreaLayoutData) - Floor0/1/2 part IDs
    //              (= wallpaper + flooring), Exterior (windows/door) + stains, and LightLevel.
    //   2. RENDER: IndoorTerritory.Brightness* (Current/Target/SavedInverted) + the furniture
    //              manager's 1462 HousingFurniture slots (Id/Position/Rotation/Stain/Index).
    // If DATA is empty after our revert, the EXD-load step (HousingTerritory vf3-vf5) didn't
    // run. If DATA is populated but RENDER is default, an apply/push step didn't run. Either
    // way the dump tells us exactly where the chain breaks - and which game call to trigger.
    //
    // USAGE: run "/hms housingdiag" (read-only; command-gated, no recompile flag needed),
    // then perform the test sequence. Dumps a labelled snapshot every PollInterval frames
    // WHEN STATE CHANGES, so you can walk in and watch DATA/RENDER populate across entry.
    // Each snapshot is tagged with territory id, IsLoaded, and territory type. Auto-stops.
    // Grep [HOUSINGDIAG].
    private bool housingDiagRunning;
    // S151: deferred de-draw poll state. Furniture streams in WAVES across many frames on a
    // different-Bg reload (count goes 0→15→pause→27→pause→31). S150 fired on the first
    // 1-frame plateau (caught the 15, missed later waves → partial hide / fragmented
    // furniture). S151: (a) require a real stability window (count unchanged for N frames =
    // streaming genuinely done, not a mid-stream pause), and (b) keep watching after the
    // first de-draw and RE-FIRE if the count grows again (late waves), until fully settled.
    // De-draw is idempotent (re-hiding already-hidden is a harmless flag re-set; lists clear
    // each pass), so re-firing is safe. Same-Bg hops don't reload geometry so this is a
    // fast no-op there (count already stable from prior residency).
    private bool deferredDeDrawArmed;
    private int deferredDeDrawFrames;        // overall backstop countdown (initial-window length)
    private bool deferredDeDrawFiredOnce;    // have we run de-draw at least once this load?
    // S322: settle delay before the FIRST de-draw. Furniture streams in over several seconds on load, and
    // its collision/textures lag the geometry; firing the instant any furniture is visible could catch the
    // zone half-loaded and leave it stripped with missing collision/textures (a rare load-race that wiped the
    // host's own apartment; a re-entry - i.e. a clean load - fixed it). Hold the initial fire until the zone
    // has had time to populate. Only gates the FIRST fire; late waves and the persistent scan re-fire as
    // before, so nothing that streams in afterwards is missed.
    private int deDrawSettleFrames;
    private const int DeDrawSettleFrames = 300;   // S329: MAX backstop (~5s) - only used if furniture never settles/shows
    private int deDrawFloorFrames;                 // short hard floor before the first fire (collision/texture lag)
    private const int DeDrawFloorFrames = 30;      // ~0.5s minimum before we'll fire (guards the half-loaded strip)
    private int deDrawStablePresent;               // frames furniture has been CONTINUOUSLY visible (stability signal)
    private const int DeDrawStableFrames = 15;     // ~0.25s of sustained-visible = the wave has arrived + settled
    // S314: after the initial post-load window lapses, the poll stays armed for the whole session but
    // scans on a throttled cadence (catches furniture that streams in by proximity long after load -
    // e.g. leaked apartment furniture anchored far from the dungeon spawn). ~0.5s between scans.
    private int persistentScanTick;
    private bool quietDeDraw;              // v0.7.427: suppress per-pass diag dumps on cadence runs
    private bool deDrawRunLogged;         // v0.7.454: edge-latch for the "DeDraw running" line (log start, not every tick)
    private int newlyHiddenThisPass;       // v0.7.428: writes that flipped a TRUE→false this pass (real catches, not idempotent re-sets)
    private int catchLinesThisPass;        // v0.7.428: cap on [CADENCE-CATCH] attribution lines per pass
    private int hotWindowFrames;           // v0.7.428: fast-sweep window armed after any pass that newly hid something
    private const int PersistentScanInterval = 30;
    private int housingDiagFrameCounter;
    private int housingDiagSnapshotsLeft;
    private const int HousingDiagPollInterval = 30; // ~0.5s between snapshots at 60fps
    private const int HousingDiagMaxSnapshots = 120;  // S160: ~60s - time to walk to the door + exit
    private string housingDiagLastSig = "";          // only dump when something CHANGED

    // S125/S128: track what we hid so Revert/ReloadZone can restore it.
    private readonly HashSet<ushort> hiddenObjectIndices = new();
    private readonly List<nint> hiddenLayoutInstances = new();
    // S166: orphan re-hide. The census (S165) proved a partition hidden on 1011 (addr
    // 2570890093472) survives the hop to 1012 as the SAME live pointer, still rendering, with its
    // IsVisible reset by the transition - and it's unreachable via 1012's layout walk, so the
    // per-zone de-draw misses it. We carry the PREVIOUS load's hidden pointers forward exactly ONE
    // hop and re-assert IsVisible=false on them. One hop only because testing shows the orphan's
    // lifetime is a single transition (e.g. 1011→1012 carries it; the next hop frees/hides it) -
    // holding pointers longer risks dereferencing a freed instance (the streaming-zone AV the S146
    // restore path was written to avoid). Deref is guarded, but we also bound the lifetime so a
    // stale pointer is never carried past the one frame where it's known-live.
    private readonly List<nint> prevZoneHiddenInstances = new();
    // S168: original RenderFlags of furniture GameObjects we draw-suppressed (keyed by GameObject*),
    // for restore on /hms stop. Re-fetched/re-applied each de-draw; keyed by pointer only for the
    // saved-original (restore re-walks the live furniture-manager array, so no dead-ptr deref).
    private readonly Dictionary<nint, uint> furnitureRenderFlagSaved = new();
    // S146: keyed by owner InstanceKey (not raw Collider*) so restore can re-walk the LIVE
    // Scene and match by key, never dereferencing a saved pointer that streaming may have freed.
    private readonly Dictionary<uint, (byte vf, ulong layerMask)> colliderSavedState = new();
    private readonly HashSet<uint> hiddenInstanceKeys = new();
    private readonly List<(ushort idx, byte flags)> untargetedObjects = new();
    private Vector3? savedPosition;
    // S284: return state is just savedZoneId + savedPosition (captured at first LoadZone). Return =
    // reload savedZoneId + restore savedPosition. (The old EntrySpawn foreign-zone anchor was removed -
    // see Revert; it was redundant with this and caused the OOB-on-stop bug.)
    private float? savedRotation;

    public bool IsZoneLoaded { get; private set; }
    // True while LoadZone or Revert is actively running (objects may be mid-
    // teardown/rebuild). Consumers that write to actor/world state each frame
    // should stay inert while this is set. IsZoneLoaded is NOT sufficient - it
    // stays true through most of Revert's teardown.
    public bool IsTransitioning { get; private set; }
    public uint CurrentLoadedZone { get; private set; }

    // S262: development/research mode (off by default, toggled at runtime via /hms debug - NO
    // recompile needed). When ON, LoadZone sets up the InstanceContentDirector (SetupInstanceContentForZone)
    // before the native load, exactly as the old shipping path did. This re-enables the MapEffect /
    // director-update machinery for live investigation of the explorer-mode scenario-walk (the parked
    // research track). When OFF (default, the shipping behaviour) the director is NOT created, so the
    // "Duty Information" HUD never appears and the map loads clean. The director setup is preserved (sig
    // resolution intact); this flag is the documented switch that arms it. Default false. Not persisted -
    // a fresh session always starts in the clean shipping mode; you opt into research per-session.
    public bool ResearchMode { get; set; } = false;

    // S288: deferred home-position restore. The synchronous SetPosition inside Revert is CLOBBERED by
    // the zone-load's ASYNC settle (which fires a few frames AFTER Revert returns - confirmed: the
    // post-write readback was correct, but /hms here moments later showed the actor flung back to the
    // foreign coords). So instead we ARM a poll that waits until the actor has actually settled in the
    // home territory, THEN writes the home position - and reasserts it for a short window to win against
    // any late settle write. This is the real fix; ordering inside Revert was a red herring.
    private bool homeRestoreArmed;
    // S301: lets the leave/stop path know whether the deferred restore poll is live and will own the
    // filter-disable (via OnHomeRestoreComplete). When it's NOT armed, the caller must disable the
    // filter inline - otherwise a re-entrant leave can orphan the filter UP (the double-stop bug).
    public bool HomeRestoreArmed => homeRestoreArmed;
    private int homeRestoreTicks;
    private uint homeRestoreZone;
    private Vector3 homeRestorePos;
    private float homeRestoreRot;
    private bool homeRestoreHasRot;
    private int homeRestoreStable; // S293: consecutive frames the home write has HELD (drift < 1y)

    // S287: gated diagnostic logging. The [WRECK]/[FURNMGR]/[HOUSINGIDS]/[GFXRESOLVE] traces are
    // development noise (hundreds of lines per load) - emit them ONLY in research mode. Errors are NOT
    // routed through this (they always fire via log.Error). Toggle with /hms debug.
    private void DiagLog(string msg) { if (ResearchMode) log.Information(msg); }

    // Curated spawn points from Hyperborea's data - loaded at init
    // For Phase 2 we embed a small set; later read from data.yaml
    // v0.7.242: territories whose LGB PopRange is OOB but which have a good arena-bounding volume (MapRange /
    // CollisionBox) - for these, skip the OOB PopRange and use the arena centre (curation-by-reference: points at
    // authored arena data instead of hardcoded coords, so it survives map updates). Populate as V confirms them
    // in-game (the system can't cheaply tell an OOB PopRange from a valid one without raycast machinery). The arena
    // centre XZ centres you in the arena; the engine ground-clamp settles Y on first movement.
    private readonly HashSet<uint> preferArenaCenter = new() { 824, 369, 128, 181, 409, 474, 1319 };
    // NB-12: 1319 (Cosmic Exploration - Sinus Ardorum). The authored LGB PopRange sits MID-AIR (you spawn falling).
    // It's a large open field with an arena-bounding volume, so route it through the same alternative-from-LGB pick
    // the other IDs use: skip the airborne PopRange, take the MapRange/CollisionBox centre (XZ mid-field; the engine
    // ground-clamp settles Y on first movement). Weakly dominant vs today - if no MapRange exists it falls back to the
    // normal chain (unchanged). PROVISIONAL: open-field zones can carry several MapRanges, so mapRanges[0] may not be
    // the ideal spot. If it lands poorly, run /hms lgbdump 1319, read the good PopRange/MapRange coords, and promote to
    // a hardcoded curatedSpawns entry (measured, not guessed).
    // v0.7.244: 128/181/409/474 = Limsa Lominsa Upper Decks (s1t1) cluster - the entrance discriminator was matching a
    // table-adjacent EventObject (the "spawn on the table" bug). Comb for a MapRange/CollisionBox centre instead.
    // NOTE: cities have many MapRanges, so the combed spot may need checking; if it's bad, these get a hardcode.
    // (v0.7.246: 1144 moved to a hardcode - multiple boss arenas meant arena-centre grabbed the wrong cylinder.)

    private readonly Dictionary<uint, Vector3> curatedSpawns = new()
    {
        // S264: 1345 (The Clyteum). The LGB ENTRANCE EventObject reads Y=0.0, but that's ankle-deep
        // in a small snow mound at the entrance - the true collision surface is Y=0.3 (the char
        // reasserts to 0.3 on first movement). Curate it so you spawn ON the pile, not in it.
        { 1345, new Vector3(-805.0f, 0.3f, 864.1f) },
        // v0.7.236: flagship / art hand-curates (coords supplied by V in GUI X,Z,Y order → stored X,Y,Z here).
        // v0.7.240: 670 reset to resolver default (the fringes were the wrong map). The royal airship landing is 679.
        { 679,  new Vector3(0.0f, -380.0f, 0.0f) },      // royal airship landing platform - X/Z both 0, Y -380
        { 338,  new Vector3(678.0f, 0.297f, -675.0f) },  // two-segment map; real entry across the void (tunnel↔arena)
        { 1010, new Vector3(2.7f, 0.2f, -128.0f) },      // Magna Glacies - manual curate (also needs collision-wall teardown)
        { 1012, new Vector3(2.7f, 0.2f, -128.0f) },      // 1010's layer-twin - same curated spawn
        { 898,  new Vector3(-100.0f, 103.6f, 360.0f) },  // Anamnesis Anyder - confirmed good
        // v0.7.244: batch from testing. Coords supplied in GUI X,Z,Y order → stored X,Y,Z.
        { 1295, new Vector3(100.0f, -410.0f, -100.0f) },  // very OOB → hardcode
        { 151,  new Vector3(0.0f, 1.0f, 460.0f) },        // huge map, spawn relocated
        { 1097, new Vector3(-23.0f, 389.154f, -640.0f) }, // boring boss arena → better spot
        { 1119, new Vector3(-23.0f, 389.154f, -640.0f) }, // 1097's twin - same spot
        { 1021, new Vector3(-70.0f, 32.057f, -390.0f) },  // weird spawn → hardcode
        { 1144, new Vector3(345.0f, 16.751f, 145.0f) },   // e3d3 - multiple boss arenas, hardcode the entry
        // HoH floors - parked from resolver (procedural, no authored spawn), but V supplied usable coords:
        { 773,  new Vector3(300.0f, 0.0f, 300.0f) },      // Heaven-on-High floor
        { 784,  new Vector3(300.0f, 0.0f, 300.0f) },      // HoH floor (shares 773's template spot)
        { 774,  new Vector3(244.0f, 0.0f, -181.0f) },     // HoH floor
        { 783,  new Vector3(-300.0f, 0.100f, -300.0f) },  // HoH floor
        // v1.0.0 post-release curates (coords supplied X,Y,Z):
        { 1279, new Vector3(0.0f, 0.0f, 100.0f) },        // curated spawn
        { 389,  new Vector3(476.0f, 16.4f, 449.2f) },     // curated spawn
        // NB-6: Bayside Battleground (PvP). Resolver spot was poor; fast+reliable hardcode beats the smarter
        // LGB alt-spawn read here. Centre-ish flat ground.
        { 1293, new Vector3(100.0f, 0.5f, 100.0f) },      // Bayside Battleground
    };

    public ZoneLoadService(
        IObjectTable objectTable,
        IPluginLog log,
        ISigScanner sigScanner,
        IGameInteropProvider hookProvider,
        IFramework framework,
        IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.log = log;
        this.sigScanner = sigScanner;
        this.hookProvider = hookProvider;
        this.framework = framework;
        this.dataManager = dataManager;
    }

    public void Initialize()
    {
        try
        {
            var loadZoneAddr = sigScanner.ScanText(
                "40 55 56 41 54 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24");
            loadZoneHook = hookProvider.HookFromAddress<LoadZoneDelegate>(loadZoneAddr, LoadZoneDetour);
            loadZoneHook.Enable();
            try
            {
                // HookFromSignature resolves the call itself - no hand rel32 arithmetic (that was the crash).
                createSceneHook = hookProvider.HookFromSignature<CreateSceneDelegate>("E8 ?? ?? ?? ?? 66 89 3D ?? ?? ?? ?? E9", CreateSceneDetour);
                createSceneHook.Enable();
                log.Information("[HMSync] CreateScene hooked via signature");
            }
            catch (Exception ex) { log.Error("[HMSync] CreateScene hook failed: " + ex.Message); }
            try
            {
                createCutHook = hookProvider.HookFromSignature<CreateCutDelegate>("E8 ?? ?? ?? ?? 48 8B D8 48 8B 03 48 8B CB", CreateCutDetour);
                createCutHook.Enable();
                log.Information("[HMSync] CreateCutSceneController hooked (cutscene capture)");
            }
            catch (Exception ex) { log.Error("[HMSync] CreateCutSceneController hook failed: " + ex.Message); }
            log.Information("[HMSync] LoadZone hook created");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] LoadZone hook failed: " + ex.Message);
        }

        try
        {
            setupTerritoryTypeAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 48 8B D9 48 89 6C 24");
            log.Information("[HMSync] SetupTerritoryType resolved");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] SetupTerritoryType failed: " + ex.Message);
        }

        try
        {
            // S225b: MapEffect apply function. CORRECTION from S223 - the prior sig was actually
            // TargetSystem_InteractWithObject (byte-identical, wrong line grabbed from Hyperborea).
            // This is the REAL MapEffect sig, verified against ECommons MapEffect.cs (the
            // ProcessMapEffect function: long(long module, uint layoutId, ushort state, ushort flags),
            // resolved directly via ScanText - no call-following).
            mapEffectAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B FA 41 0F B7 E8");
            log.Information("[HMSync] MapEffect: resolved=0x" + mapEffectAddr.ToString("X") +
                " inModule=" + IsAddressInMainModule(mapEffectAddr));
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] MapEffect scan failed: " + ex.Message);
        }

        try
        {
            // S225b: SetupInstanceContent. CORRECTION from S224 CTD - verified against ECommons
            // EzHook/EzDelegate source: the EzHook bool flag is `autoEnable`, NOT "call-site". ECommons
            // uses ScanText(sig) DIRECTLY as the function address (Marshal.GetDelegateForFunctionPointer
            // on the raw match). S224's +5+rel32 "call-site resolution" was invented and produced a
            // garbage pointer → the CTD. Use the match address directly, exactly like Finalize below.
            setupInstanceContentAddr = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 54 24 70 48 8B C8 E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 54 24");
            log.Information("[HMSync] SetupInstanceContent: resolved=0x" + setupInstanceContentAddr.ToString("X") +
                " inModule=" + IsAddressInMainModule(setupInstanceContentAddr));
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] SetupInstanceContent scan failed: " + ex.Message);
        }

        try
        {
            // S224: FinalizeInstanceContent - direct function sig (Hyperborea).
            finalizeInstanceContentAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 70 48 8D B1");
            log.Information("[HMSync] FinalizeInstanceContent resolved");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] FinalizeInstanceContent scan failed: " + ex.Message);
        }

        LoadCuratedSpawns();
    }

    /// <summary>
    /// S225: validate a resolved function pointer is inside the game's main module image before we
    /// ever CALL it. A mis-resolved sig (esp. a hand-followed call-site) can yield a garbage address;
    /// invoking it is an uncatchable native CTD (S224's crash). This makes a bad resolve a logged
    /// no-op instead. Conservative bounds check against the process main module.
    /// </summary>
    private bool IsAddressInMainModule(nint addr)
    {
        if (addr == 0) return false;
        try
        {
            var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            if (mod == null) return false;
            nint baseAddr = mod.BaseAddress;
            nint end = baseAddr + mod.ModuleMemorySize;
            return addr >= baseAddr && addr < end;
        }
        catch { return false; }
    }


    public bool IsValidTerritory(uint territoryId)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) return false;
            var row = sheet.GetRowOrDefault(territoryId);
            return row != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the zone name for a territory ID.
    /// </summary>
    public string GetZoneName(uint territoryId)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) return "Unknown";
            var row = sheet.GetRowOrDefault(territoryId);
            if (row == null) return "Unknown";
            return row.Value.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Resolve spawn coordinates for a territory.
    /// Priority: curated override → LGB game data → fallback 0,0,0
    /// </summary>
    public Vector3 ResolveSpawnPoint(uint territoryId)
    {
        // 1. Check curated overrides (for zones where game data is wrong)
        if (curatedSpawns.TryGetValue(territoryId, out var curated))
        {
            log.Information("[HMSync] Using curated spawn for " + territoryId);
            return curated;
        }

        // 2. Try to read from game LGB data
        try
        {
            var spawn = ResolveFromLgb(territoryId);
            if (spawn.HasValue)
            {
                log.Information("[HMSync] Using LGB spawn for " + territoryId + 
                    " pos=(" + spawn.Value.X.ToString("F1") + ", " + 
                    spawn.Value.Y.ToString("F1") + ", " + 
                    spawn.Value.Z.ToString("F1") + ")");
                return spawn.Value;
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] LGB spawn resolution failed: " + ex.Message);
        }

        // 3. Fallback
        log.Information("[HMSync] No spawn data for " + territoryId + ", using origin");
        return new Vector3(0, 0, 0);
    }

    /// <summary>
    /// Read spawn coordinates from the zone's LGB (level group) file.
    /// Scans layers for PopRange, ExitRange, Aetheryte, EventNpc entries
    /// and returns the first valid position found.
    /// </summary>
    private Vector3? ResolveFromLgb(uint territoryId)
    {
        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        if (sheet == null) return null;

        var territory = sheet.GetRowOrDefault(territoryId);
        if (territory == null) return null;

        var bg = territory.Value.Bg.ToString();
        if (string.IsNullOrEmpty(bg)) return null;

        // The bg path from TerritoryType looks like: ffxiv/sea_s1/twn/s1t2/level/s1t2
        // The LGB file is at: bg/{bg_path_up_to_level}/level/bg.lgb
        // We need to construct the correct path
        var lastSlash = bg.LastIndexOf('/');
        var levelPath = lastSlash >= 0 ? bg[..lastSlash] : bg;
        var lgbPath = "bg/" + levelPath + "/bg.lgb";

        log.Debug("[HMSync] Loading LGB: " + lgbPath);

        // bg.lgb only contains BG geometry. Spawn-related objects (NPCs, Aetherytes,
        // PopRange, exits) are in planevent.lgb for EVENT-INSTANCES, but in planmap.lgb for
        // DUNGEONS (S217: dungeon planevent.lgb is EMPTY; planmap.lgb holds PopRange=8 etc.).
        // Search order: planevent (event-instances) → planmap (dungeons) → bg (geometry fallback).
        var planEventPath = "bg/" + levelPath + "/planevent.lgb";
        var planMapPath = "bg/" + levelPath + "/planmap.lgb";
        log.Debug("[HMSync] Loading planevent LGB: " + planEventPath);

        var lgbFile = dataManager.GetFile<Lumina.Data.Files.LgbFile>(planEventPath);
        // Fall through to planmap.lgb (the dungeon spawn source) when planevent is missing, fully empty, OR has no
        // PopRange of its own. v0.7.243: the last condition is the _re / duty-support fix. Stone Vigil r1d1_re (1042)
        // has a planevent.lgb with a single stray EventObject at (-7.8,9.5,-285.7) - OOB, behind a wall - and NO
        // PopRange, while planmap.lgb holds the 9 real dungeon PopRanges including the entry pen at (0,0,118). The old
        // "all layers empty" test saw the 1 stray object, treated planevent as valid, and never opened planmap. Now:
        // if planevent carries no PopRange but planmap does, planmap wins. Event instances (PopRange in planevent) are
        // unaffected - they keep using planevent. Likely also fixes the HoH floors and other _re OOB maps.
        bool PlanevHasPop(Lumina.Data.Files.LgbFile? f) => f != null && f.Layers.Any(l => l.InstanceObjects != null
            && l.InstanceObjects.Any(o => o.AssetType == Lumina.Data.Parsing.Layer.LayerEntryType.PopRange));
        bool planevEmpty = lgbFile == null || lgbFile.Layers.All(l => l.InstanceObjects == null || l.InstanceObjects.Length == 0);
        if (planevEmpty || !PlanevHasPop(lgbFile))
        {
            var planMap = dataManager.GetFile<Lumina.Data.Files.LgbFile>(planMapPath);
            // Switch to planmap only if it's non-empty AND (when planevent had something non-PopRange) actually has a
            // PopRange to offer - otherwise there's nothing better there and we keep planevent's data.
            bool planMapUsable = planMap != null && planMap.Layers.Any(l => l.InstanceObjects != null && l.InstanceObjects.Length > 0)
                && (planevEmpty || PlanevHasPop(planMap));
            if (planMapUsable)
            {
                log.Debug("[HMSync] planevent " + (planevEmpty ? "empty/missing" : "has no PopRange") +
                    " - using planmap.lgb (dungeon spawn source)");
                lgbFile = planMap;
            }
        }
        if (lgbFile == null)
        {
            // Fallback: try bg.lgb in case some zones don't have planevent or planmap
            lgbFile = dataManager.GetFile<Lumina.Data.Files.LgbFile>(lgbPath);
        }

        if (lgbFile == null)
        {
            log.Debug("[HMSync] No LGB files found for " + territoryId);
            return null;
        }

        log.Debug("[HMSync] LGB loaded, layers=" + lgbFile.Layers.Length);

        // Diagnostic: log unique types found
        var typeSet = new HashSet<string>();
        int totalEntries = 0;
        foreach (var layer in lgbFile.Layers)
        {
            if (layer.InstanceObjects == null) continue;
            foreach (var obj in layer.InstanceObjects)
            {
                totalEntries++;
                typeSet.Add(obj.AssetType.ToString());
            }
        }
        log.Debug("[HMSync] LGB types: " + string.Join(", ", typeSet) + " total=" + totalEntries);

        // Priority scan: look for specific entry types that indicate valid spawn points
        // Type IDs in LGB InstanceObjects:
        //   PopRange (40) - explicit spawn/pop areas
        //   ExitRange (43/57) - zone exits (near entrances)
        //   Aetheryte (12) - always walkable
        //   EventNpc (9) - NPCs standing on floor
        //   PositionMarker (6) - marked positions

        Vector3? popRange = null;
        Vector3? exitRange = null;
        Vector3? aetheryte = null;
        Vector3? eventNpc = null;
        Vector3? posMarker = null;

        // S220: collect ALL EventObjects/EventRanges/PopRanges to identify the dungeon ENTRANCE.
        // S219 dump proved: the entrance is an EventObject co-located with an EventRange (the
        // leave-dungeon portal + its trigger) but SEPARATED from every boss-arena PopRange (you
        // arrive at the entrance from zone-in, you don't "pop" there). Boss arenas have
        // EventObject+EventRange+PopRange clustered; the entrance has EventObject+EventRange, no PopRange.
        var allEventObjects = new List<Vector3>();
        var allEventRanges = new List<Vector3>();
        var allPopRanges = new List<Vector3>();
        // v0.7.239: PopRanges in a GIMMICK/PHASE layer are authored checkpoints (post-boss respawns, explorer-mode
        // teleport shortcuts) - in-bounds, correct-Y. PopRanges in Route_Basedata are the OOB-prone stock points behind
        // the tagged "spawned OOB / under textures" cluster. Bucket gimmick-layer PopRanges separately and prefer them.
        // Field name VERIFIED: LayerCommon.Layer.Name (public string), confirmed against Lumina 7.5.0 source
        // (src/Lumina/Data/Parsing/Layer/LayerCommon.cs, field #2, set via ReadStringOffset) AND the installed 7.5.0 DLL.
        var gimmickPopRanges = new List<Vector3>();
        // v0.7.241: arena-bounding volumes. When a zone has no entrance EventObject and no in-bounds PopRange (the OOB
        // cluster - PvP maps, boss arenas with only OOB stock PopRanges), the CENTRE of the arena's bounding volume is
        // a valid on-floor spawn locale (V confirmed in-game: the green MapRange cylinder / the CollisionBox ring frame
        // the playable arena; their centre sits on the walkable floor). XZ centres you in the arena; the Y is the
        // volume origin (may sit above the floor) but the engine's ground-clamp settles you down on first movement,
        // same as the 1345 case. MapRange ranked above CollisionBox (gameplay bound vs physics volume - MapRange is
        // more reliably floor-centred; CollisionBox can be a wall). PvP maps have several - first is fine (V confirmed).
        var mapRanges = new List<Vector3>();
        var collisionBoxes = new List<Vector3>();

        foreach (var layer in lgbFile.Layers)
        {
            foreach (var obj in layer.InstanceObjects)
            {
                var pos = new Vector3(obj.Transform.Translation.X, 
                                      obj.Transform.Translation.Y, 
                                      obj.Transform.Translation.Z);

                // Skip zero/near-zero positions
                if (System.Math.Abs(pos.X) < 0.01f && 
                    System.Math.Abs(pos.Y) < 0.01f && 
                    System.Math.Abs(pos.Z) < 0.01f)
                    continue;

                switch (obj.AssetType)
                {
                    case Lumina.Data.Parsing.Layer.LayerEntryType.PopRange:
                        popRange ??= pos;
                        allPopRanges.Add(pos);
                        // layer.Name is the LGB layer name, e.g. "LVD_Phase01_Gimmick" vs "LVD_Route_Basedata".
                        var lname = layer.Name ?? "";
                        if (lname.IndexOf("Gimmick", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || lname.IndexOf("Phase", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            gimmickPopRanges.Add(pos);
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.ExitRange:
                        exitRange ??= pos;
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.Aetheryte:
                        aetheryte ??= pos;
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.EventNPC:
                        eventNpc ??= pos;
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.PositionMarker:
                        posMarker ??= pos;
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.EventObject:
                        allEventObjects.Add(pos);
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.EventRange:
                        allEventRanges.Add(pos);
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.MapRange:
                        mapRanges.Add(pos);
                        break;
                    case Lumina.Data.Parsing.Layer.LayerEntryType.CollisionBox:
                        collisionBoxes.Add(pos);
                        break;
                }
                // NOTE: no early-exit - we need the full sets for the entrance discriminator below.
            }
        }

        // ENTRANCE discriminator (top priority): the EventObject with an EventRange within 25u
        // (its trigger volume) AND no PopRange within 50u (not a boss arena). Matched empirically
        // to (-805,0,864) on Clyteum = the exact spawn. 2D distance (X/Z) - Y varies by ramp.
        // v0.7.244: skipped for preferArenaCenter territories - on those (e.g. Limsa s1t1) the entrance discriminator
        // matches the wrong EventObject (a table), so we want the arena-centre gate below to handle them instead.
        if (!preferArenaCenter.Contains(territoryId) && allEventObjects.Count > 0 && allEventRanges.Count > 0)
        {
            foreach (var eo in allEventObjects)
            {
                float nearEvt = float.MaxValue, nearPop = float.MaxValue;
                foreach (var er in allEventRanges) { var dx = eo.X - er.X; var dz = eo.Z - er.Z; var d = MathF.Sqrt(dx * dx + dz * dz); if (d < nearEvt) nearEvt = d; }
                foreach (var pr in allPopRanges) { var dx = eo.X - pr.X; var dz = eo.Z - pr.Z; var d = MathF.Sqrt(dx * dx + dz * dz); if (d < nearPop) nearPop = d; }
                if (nearEvt < 25f && nearPop > 50f)
                {
                    log.Information("[HMSync] Spawn from ENTRANCE EventObject (" +
                        eo.X.ToString("F1") + "," + eo.Y.ToString("F1") + "," + eo.Z.ToString("F1") +
                        ") [nearEvtRange=" + nearEvt.ToString("F1") + " nearPopRange=" + nearPop.ToString("F1") + "]");
                    return eo;
                }
            }
            log.Debug("[HMSync] no entrance EventObject matched discriminator - falling back to PopRange");
        }

        // Return by priority (fallback: event-instances, and zones with no entrance EventObject).
        // v0.7.242: for territories flagged preferArenaCenter, the LGB PopRange is known-OOB (V-confirmed in-game),
        // so skip straight to the arena-bounding-volume centre BEFORE the PopRange grab. MapRange first, else
        // CollisionBox. Only these specific IDs are affected - every other zone keeps the normal priority chain.
        if (preferArenaCenter.Contains(territoryId))
        {
            if (mapRanges.Count > 0)
            {
                var m = mapRanges[0];
                log.Information("[HMSync] Spawn from MapRange centre (" +
                    m.X.ToString("F1") + "," + m.Y.ToString("F1") + "," + m.Z.ToString("F1") +
                    ") [preferArenaCenter " + territoryId + ": skipping OOB PopRange; ground-clamp settles Y]");
                return m;
            }
            if (collisionBoxes.Count > 0)
            {
                var cb = collisionBoxes[0];
                log.Information("[HMSync] Spawn from CollisionBox centre (" +
                    cb.X.ToString("F1") + "," + cb.Y.ToString("F1") + "," + cb.Z.ToString("F1") +
                    ") [preferArenaCenter " + territoryId + ": skipping OOB PopRange; ground-clamp settles Y]");
                return cb;
            }
            log.Debug("[HMSync] preferArenaCenter " + territoryId + " but no MapRange/CollisionBox - normal chain");
        }
        // v0.7.239: prefer a GIMMICK/PHASE-layer PopRange over the plain first-PopRange grab (which takes whatever
        // PopRange is first in iteration order - often a Route_Basedata stock point that sits OOB / under the mesh).
        // Only affects zones that HAVE a gimmick PopRange and no entrance match, so entrance-resolved and
        // single-PopRange zones (the near-flawless majority) are untouched.
        if (gimmickPopRanges.Count > 0)
        {
            var g = gimmickPopRanges[0];
            log.Information("[HMSync] Spawn from GIMMICK PopRange (" +
                g.X.ToString("F1") + "," + g.Y.ToString("F1") + "," + g.Z.ToString("F1") +
                ") [preferred over " + allPopRanges.Count + " PopRanges to avoid OOB stock points]");
            return g;
        }
        if (popRange.HasValue) { log.Debug("[HMSync] Spawn from PopRange"); return popRange; }
        if (exitRange.HasValue) { log.Debug("[HMSync] Spawn from ExitRange"); return exitRange; }
        if (aetheryte.HasValue) { log.Debug("[HMSync] Spawn from Aetheryte"); return aetheryte; }
        if (eventNpc.HasValue) { log.Debug("[HMSync] Spawn from EventNpc"); return eventNpc; }
        if (posMarker.HasValue) { log.Debug("[HMSync] Spawn from PositionMarker"); return posMarker; }

        // v0.7.241: last resort before giving up - the centre of an arena-bounding volume. Catches zones with NO
        // usable point instance at all (PvP maps that are just CollisionBox+EventRange; some arenas). MapRange first
        // (gameplay bound, floor-centred), then CollisionBox. XZ centres you in the arena; ground-clamp fixes Y. This
        // only fires where we would otherwise return null (origin fallback → 0,0,0), so it can't regress any zone that
        // already resolves.
        if (mapRanges.Count > 0)
        {
            var m = mapRanges[0];
            log.Information("[HMSync] Spawn from MapRange centre (" +
                m.X.ToString("F1") + "," + m.Y.ToString("F1") + "," + m.Z.ToString("F1") +
                ") [arena-bound fallback, " + mapRanges.Count + " MapRange(s); ground-clamp settles Y]");
            return m;
        }
        if (collisionBoxes.Count > 0)
        {
            var cb = collisionBoxes[0];
            log.Information("[HMSync] Spawn from CollisionBox centre (" +
                cb.X.ToString("F1") + "," + cb.Y.ToString("F1") + "," + cb.Z.ToString("F1") +
                ") [arena-bound fallback, " + collisionBoxes.Count + " CollisionBox(es); ground-clamp settles Y]");
            return cb;
        }

        return null;
    }

    /// <summary>
    /// Load a zone. Only preserves objects in the sessionPeerIndices set.
    /// </summary>
    /// <summary>
    /// De-draw housing furnishings (placed furniture, partitions, doors, etc.) in the
    /// current indoor/outdoor territory.
    ///
    /// Housing furnishings are NOT in Dalamud's object table - the blanket
    /// `foreach (var obj in objectTable)` de-draw in LoadZone never touches them. They live
    /// in the housing system's own FurnitureManager object array, reached via
    /// HousingManager.CurrentTerritory. This walk mirrors Meddle's LayoutService
    /// (ParseTerritoryFurniture): for each furniture entry, resolve its HousingObject* from
    /// the manager's object array and DisableDraw() it - the same one-shot DisableDraw the
    /// object-table loop and Hyperborea use, just over the housing array.
    ///
    /// Entirely best-effort: every pointer is null-checked and the whole walk is wrapped, so
    /// if the housing structs shift between game/CS versions this degrades to "furniture not
    /// hidden" with a log line, never a crash. (FFXIVClientStructs has had breaking changes
    /// to HousingManager/IndoorTerritory across patches - see 7.2 notes - so the member names
    /// below are the most likely failure point; a wrong name is a compile error, not a CTD.)
    /// </summary>
    // S153: arm the deferred de-draw. Clears tracking lists ONCE per load (fresh state for
    // this load's initial-fire + re-fires), resets poll state, subscribes (single subscription
    // even on rapid re-load).
    // v0.7.320: idempotent guarantee that the de-draw poll is running, WITHOUT resetting the per-load tracking
    // (unlike ArmDeferredDeDraw, which clears state and re-arms the settle window). Called from the plugin tick
    // whenever a session + virtual map is active, so the poll runs on EVERY client regardless of how they loaded
    // in - a peer pulled into the host's map by any path (not just the LoadZone arm) still gets furniture de-draw.
    // Because the trigger (AnyVisibleHousingFurniture) is role-agnostic and re-fires on re-streams, once the poll
    // is subscribed the peer catches furniture exactly like the host: fire whenever it's visible, no matter who
    // approached. Furniture may render one frame before the throttled poll catches it - that's inherent to the
    // stream-then-react model, same as the host - but it IS caught on every client. Safe to call every frame.
    public void EnsureDeDrawPollRunning()
    {
        // (EnableFurnitureDeDraw is a compile-time const; when it's flipped off this whole feature is compiled out.)
        if (!deferredDeDrawArmed)
        {
            deferredDeDrawArmed = true;
            framework.Update += PollDeferredDeDraw;
            // Give the poll a live initial window so the stability gate behaves like a fresh load (it wasn't armed
            // by LoadZone on this client). Don't touch the hidden-instance tracking - nothing to preserve yet.
            deferredDeDrawFrames = 600;
            deDrawSettleFrames = DeDrawSettleFrames;
            deDrawFloorFrames = DeDrawFloorFrames;
            deDrawStablePresent = 0;
            deferredDeDrawFiredOnce = false;
        }
    }

    private void ArmDeferredDeDraw()
    {
        // S166: snapshot the PREVIOUS load's hidden instances into the one-hop carry BEFORE
        // clearing, so this load's de-draw can re-hide any that persist as orphans (the 1011→1012
        // partition). Replaced each load (one-hop lifetime - never carried past known-live).
        prevZoneHiddenInstances.Clear();
        prevZoneHiddenInstances.AddRange(hiddenLayoutInstances);

        // Per-load clear (was per-pass in DeDrawHousingFurniture - see S153 note there).
        hiddenLayoutInstances.Clear();
        hiddenInstanceKeys.Clear();
        colliderSavedState.Clear();
        untargetedObjects.Clear();

        deferredDeDrawFrames = 600;          // ~10s initial high-frequency window (waves can be slow)
        deferredDeDrawFiredOnce = false;
        deDrawSettleFrames = DeDrawSettleFrames;   // S329: max backstop (only if furniture never settles/shows)
        deDrawFloorFrames = DeDrawFloorFrames;     // S329: short hard floor before the first fire
        deDrawStablePresent = 0;                   // S329: reset the continuous-visible stability counter
        persistentScanTick = 0;              // S314: reset throttle for the new load's persistent phase
        barrierDiagDumped = false;           // v0.7.265: re-dump the barrier funnel diagnostic for this load
        barrierDiagTick = 0;
        barrierDiagMaxBox = 0;
        wepDiagDumped = false;               // v0.7.269: re-dump the wep-hide funnel diagnostic for this load
        wepDoneDumped = false;
        wepDiagTick = 0;
        persistentBarrierTick = 0;
        if (!deferredDeDrawArmed)
        {
            deferredDeDrawArmed = true;
            framework.Update += PollDeferredDeDraw;
        }
    }

    // S153/S313/S314/S318: post-load watch. Fire the de-draw whenever housing furniture is VISIBLE in any
    // resident layout (AnyVisibleHousingFurniture - see below). High-frequency for the initial window, then
    // a throttled persistent scan for the whole session (the leak streams in by proximity long after load).
    // Self-limiting: after a successful hide the signal goes false. Disarmed only on stop/leave.
    private void PollDeferredDeDraw(IFramework fw)
    {
        deferredDeDrawFrames--;

        // v0.7.428 - HOT WINDOW. After any pass that NEWLY hid something (a real catch, not an
        // idempotent re-set), stragglers from the same stream-in cluster tend to bind within the
        // next few frames (mesh first, flame/smoke ignition after - the VFX-item pattern). Run the
        // quiet full hide every 10 frames for ~1s; each further catch re-arms the window. Worst-case
        // visible flash inside a cluster drops from one scan tick (~0.5s) to ~0.17s. Gated on the
        // settle-managed initial fire so it can never pre-empt it.
        if (deferredDeDrawFiredOnce && hotWindowFrames > 0)
        {
            hotWindowFrames--;
            if (hotWindowFrames % 10 == 0)
            {
                quietDeDraw = true;
                try { DeDrawHousingFurniture(); } finally { quietDeDraw = false; }
                if (newlyHiddenThisPass > 0) hotWindowFrames = 60;   // cluster still active - stay hot
            }
            return;
        }

        // S322 + S329: settle gate before the FIRST de-draw. Furniture (and its collision/textures) streams in over
        // several seconds; firing mid-stream could strip a half-loaded apartment. The OLD gate was a blind flat ~2s
        // wait applied to every load - slow on the common case (light maps settle far faster). NEW: a short minimum
        // floor, then fire once furniture has been CONTINUOUSLY VISIBLE for a brief stability window (streaming
        // brings furniture in progressively, so a sustained-present signal means the wave has arrived and settled).
        // Light maps fire in ~floor+stability (~0.75s) instead of a flat 2s; heavy/slow maps still wait for the
        // furniture to actually show before firing, and late waves + the persistent scan re-fire (idempotent) so
        // nothing streamed-in later is missed. The old flat value is demoted to a MAX backstop.
        if (!deferredDeDrawFiredOnce)
        {
            if (deDrawFloorFrames > 0) { deDrawFloorFrames--; return; }   // short hard floor (collision/tex lag)

            bool visNow = AnyVisibleHousingFurniture() || AnyVisibleFurnitureManagerObjects();
            if (visNow) deDrawStablePresent++;
            else { deDrawStablePresent = 0; deDrawSettleFrames--; }       // not settled yet - keep the backstop ticking

            // Fire when furniture has been continuously visible for the stability window - OR the max backstop lapses
            // (pathological slow stream): fire on whatever's visible then, late waves re-fire the rest.
            bool stableEnough = deDrawStablePresent >= DeDrawStableFrames;
            bool backstopLapsed = deDrawSettleFrames <= 0;
            if (!stableEnough && !backstopLapsed) return;
            if (!visNow && !backstopLapsed) return;   // backstop not lapsed and nothing visible → keep waiting
        }

        // S313/S314/S318: the trigger is "is housing furniture currently VISIBLE in any resident layout".
        // History: S313 widened the de-draw to walk all layouts; S314 made the watch persistent (the leak
        // streams in by proximity long after load, far from spawn); S318 replaced the total-BgPart-count
        // GROWTH heuristic - which the zone's own streaming geometry masked, causing the intermittent
        // pillar/door leak - with AnyVisibleHousingFurniture(): housing-path (bgcommon/hou/) only, visible
        // only, recursing SharedGroups so it catches pillars (top-level BgPart) AND doors (nested leaves).
        // Fire the de-draw whenever furniture is visible; after a successful hide the signal goes false, so
        // it self-limits. Persistent phase scans on a throttle; disarmed only on stop/leave.
        bool inPersistentPhase = deferredDeDrawFrames <= 0;
        if (inPersistentPhase)
        {
            persistentScanTick++;
            if (persistentScanTick < PersistentScanInterval) return;
            persistentScanTick = 0;
        }

        bool visibleFurniture = AnyVisibleHousingFurniture() || AnyVisibleFurnitureManagerObjects();
        if (!visibleFurniture)
        {
            deDrawRunLogged = false;   // v0.7.454: furniture gone → arm the log for the next real wave's rising edge
            // v0.7.428 - DETECTION-CLEAN SAFETY PASS. The .427 stove datum: with all six buckets
            // AND the FM chain polled, the stove's reappearance was STILL invisible to detection and
            // died only on the blind cadence tick. Its renderable lives on a surface none of our
            // reads return. So in the persistent phase, "detection says clean" no longer means
            // "do nothing" - it means run the quiet idempotent full hide anyway, every scan tick
            // (~0.5s). Detection is demoted to a logging/fast-path signal; the safety net runs
            // regardless. Each pass tracks newly-hidden (true→false flips); any catch arms the hot
            // window and emits [CADENCE-CATCH] lines naming the instance type/path/slot detection
            // missed - the mechanism evidence self-collects during normal play.
            if (inPersistentPhase && deferredDeDrawFiredOnce)
            {
                quietDeDraw = true;
                try { DeDrawHousingFurniture(); } finally { quietDeDraw = false; }
                if (newlyHiddenThisPass > 0) hotWindowFrames = 60;
            }
            return;   // initial window keeps ticking via the settle logic above
        }

        if (!deferredDeDrawFiredOnce)
        {
            DiagLog("[HMSync] Deferred de-draw firing (initial): visible housing furniture present");
            DeDrawHousingFurniture();
            deferredDeDrawFiredOnce = true;
            if (newlyHiddenThisPass > 0) hotWindowFrames = 60;   // v0.7.428
            return;
        }

        DiagLog("[HMSync] Deferred de-draw RE-FIRE (" + (inPersistentPhase ? "persistent scan" : "late wave") +
            "): visible housing furniture present");
        DeDrawHousingFurniture();
        if (newlyHiddenThisPass > 0) hotWindowFrames = 60;   // v0.7.428

        // S314: no settle-disarm. Once armed we stay armed (throttled in the persistent phase) for the
        // whole session so newly-streamed furniture is always caught. Handler removal happens on stop/leave.
    }

    // S155 DIAG [BGDUMP]: read-only per-instance-TYPE dump. S154 proved BgPart is hidden
    // (visible=0) on BOTH the clean and broken hops - so the visible furniture is NOT a BgPart.
    // This widens the dump to ALL FOUR types we de-draw (SharedGroup, BgPart, Vfx, Light) and
    // reports visible-count per type, so we see WHICH layer has visible>0 on the broken
    // 1011→1012 hop. inHiddenSet is tracked per instance (keys are shared across our hidden set).
    private void DumpBgPartInstances(string when)
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null || lw->GlobalLayout == null) { log.Information("[BGDUMP] (" + when + ") GlobalLayout NULL"); return; }

            var layout = lw->GlobalLayout;
            var sb = new System.Text.StringBuilder();
            sb.Append("[BGDUMP] (").Append(when).Append(") ");
            foreach (var typeKey in new[] { InstanceType.SharedGroup, InstanceType.BgPart,
                                            InstanceType.Vfx, InstanceType.Light,
                                            InstanceType.IndoorObject, InstanceType.OutdoorObject })
            {
                int total = 0, visible = 0, visInSet = 0, visNotInSet = 0;
                if (layout->InstancesByType.TryGetValuePointer(typeKey, out var m) && m != null && m->Value != null)
                {
                    foreach (var kv in *m->Value)
                    {
                        var inst = kv.Item2.Value;
                        if (inst == null) continue;
                        total++;
                        if (inst->HavePrimary())
                        {
                            var gfx = inst->GetGraphics();
                            if (gfx != null && ((DrawObject*)gfx)->IsVisible)
                            {
                                visible++;
                                if (hiddenInstanceKeys.Contains(inst->Id.InstanceKey)) visInSet++;
                                else visNotInSet++;
                            }
                        }
                    }
                }
                sb.Append(typeKey).Append("[tot=").Append(total).Append(" vis=").Append(visible);
                if (visible > 0) sb.Append(" inSet=").Append(visInSet).Append(" notInSet=").Append(visNotInSet);
                sb.Append("] ");
            }
            sb.Append("hiddenSetSize=").Append(hiddenInstanceKeys.Count);
            log.Information(sb.ToString());
        }
        catch (Exception ex) { log.Error("[BGDUMP] (" + when + ") threw: " + ex.Message); }
    }

    // S312: a zone is instanced content (duty/dungeon/trial) iff it has a ContentFinderCondition
    // with a non-zero Content row - the SAME discriminator SetupInstanceContentForZone uses (content==0
    // ⇒ city/overworld/residential). Cities, overworld, and RESIDENTIAL WARDS all return false here.
    // The barrier-drop's direct SharedGroup write only makes sense in instanced content (transient duty
    // geometry); in a streaming residential ward those SharedGroups are estate prefabs that stream in/out,
    // and writing their PrefabFlags2 corrupts their lifecycle → SharedGroupLayoutInstance.Deinit AV on the
    // next stream-out (the 1010-load crash). Gating on this keeps the barrier-drop confined to duties.
    private bool IsInstancedContent(uint territoryId)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            var row = sheet?.GetRowOrDefault(territoryId);
            if (row == null) return false;
            return (row.Value.ContentFinderCondition.ValueNullable?.Content.RowId ?? 0) != 0;
        }
        catch { return false; }
    }

    // S224: spin up the instance-content layer for a zone (so the director + MapEffects exist,
    // enabling barrier release). Content ID from TerritoryType.ContentFinderCondition.Content.RowId.
    // Returns true if content was set up. Mirrors Hyperborea's LoadZone setup step.
    private unsafe bool SetupInstanceContentForZone(uint territoryId)
    {
        try
        {
            if (setupInstanceContentAddr == 0) return false;
            if (!IsAddressInMainModule(setupInstanceContentAddr))
            {
                log.Error("[HMSync] SetupInstanceContent addr 0x" + setupInstanceContentAddr.ToString("X") +
                    " outside main module - SKIPPING call (bad sig resolution). Barrier release disabled.");
                return false;
            }
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            var row = sheet?.GetRowOrDefault(territoryId);
            if (row == null) return false;
            uint content = row.Value.ContentFinderCondition.ValueNullable?.Content.RowId ?? 0;
            if (content == 0) return false; // not instanced content (city/overworld) - nothing to set up

            var setup = Marshal.GetDelegateForFunctionPointer<SetupInstanceContentDelegate>(setupInstanceContentAddr);

            // S259: always raw init (flags=0). The explorer-mode branch was removed - flags=1/Tourism
            // brought up the explorer HUD but never a clean map (the cleared-map state is Lua-sequence
            // driven, not flag-driven; see the architecture doc §14-18). Map load does NOT depend on
            // this director step - the actual load is loadZoneHook.Original below; this setup exists to
            // create the InstanceContentDirector + populate MapEffects so barrier release works.
            uint flags = 0;
            setup((nint)EventFramework.Instance(), 0x80030000 + content, content, flags);
            lastInstanceContentId = content;
            log.Information("[HMSync] SetupInstanceContent: content=" + content + " (territory " + territoryId + ") [raw]");
            return true;
        }
        catch (Exception ex) { log.Error("[HMSync] SetupInstanceContent threw: " + ex.Message); return false; }
    }

    // S224: tear down whatever instance content we previously set up (before loading a new zone or
    // returning home). Idempotent - no-op if we set nothing up.
    private unsafe void FinalizeCurrentInstanceContent()
    {
        try
        {
            // S262: clear any stale "Duty Information" HUD (name + clock). AnoMech does this same
            // hide on teardown. One-shot - not a per-frame suppressor; we no longer create a director,
            // so this only catches a leftover from an older build or a prior real duty.
            var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            if (uiState != null && uiState->DirectorTodo.IsShown)
                uiState->DirectorTodo.IsShown = false;

            if (lastInstanceContentId == null || finalizeInstanceContentAddr == 0) { lastInstanceContentId = null; return; }
            if (!IsAddressInMainModule(finalizeInstanceContentAddr))
            {
                log.Error("[HMSync] FinalizeInstanceContent addr outside main module - SKIPPING.");
                lastInstanceContentId = null; return;
            }
            var fin = Marshal.GetDelegateForFunctionPointer<FinalizeInstanceContentDelegate>(finalizeInstanceContentAddr);
            fin((nint)EventFramework.Instance(), 0x80030000 + lastInstanceContentId.Value);
            log.Information("[HMSync] FinalizeInstanceContent: content=" + lastInstanceContentId.Value);
            lastInstanceContentId = null;
        }
        catch (Exception ex) { log.Error("[HMSync] FinalizeInstanceContent threw: " + ex.Message); lastInstanceContentId = null; }
    }




    // S228: BARRIER DROP via SharedGroup collider deactivation - AnoMech's proven mechanism.
    // The duty pre-pull spawn ring is a set of SharedGroup ILayoutInstances (NOT a MapEffect, NOT a
    // CollisionBox, NOT a director-update - all ruled out S222-S227). In a real duty the server's
    // Commence clears their collision; client-side we do it ourselves: SetColliderActive(false) (vfunc 37,
    // the physics off-switch). (S312: the old companion `clear PrefabFlags2&0x8` write was removed - see
    // DisableSpawnAreaColliders; direct lifecycle-flag writes corrupt the SharedGroup and crash Deinit.)
    // Returns count disabled. 0 = normal during async streaming (caller retries each frame).
    private unsafe int DisableSpawnAreaColliders(Vector3 center, float radius)
    {
        var lw = LayoutWorld.Instance();
        if (lw == null || lw->ActiveLayout == null) return 0;
        float r2 = radius * radius;
        int disabled = 0;
        foreach (var layerKv in lw->ActiveLayout->Layers)
        {
            var layer = layerKv.Item2.Value;
            if (layer == null) continue;
            foreach (var instKv in layer->Instances)
            {
                var inst = instKv.Item2.Value;
                if (inst == null) continue;
                if (inst->Id.Type != InstanceType.SharedGroup) continue;

                var sg = (SharedGroupLayoutInstance*)inst;
                var pos = sg->Transform.Translation;
                float dx = pos.X - center.X, dz = pos.Z - center.Z;
                if (dx * dx + dz * dz > r2) continue;

                // S312: drop the collider via the game's own vfunc only. The previous
                // `sg->PrefabFlags2 &= ~0x8u` direct write was removed - writing SharedGroup lifecycle
                // fields directly is exactly what tripped the Deinit AV (the S132 de-draw lesson:
                // touch leaf state via the game's accessors, never the instance's bookkeeping flags).
                // SetColliderActive(false) (vfunc 37) is the physics off-switch with proper bookkeeping;
                // it's sufficient to clear the entry-ring collision on its own.
                inst->SetColliderActive(false);
                disabled++;
            }
        }
        if (disabled > 0)
            DiagLog("[BARRIERDROP] disabled " + disabled + " SharedGroups within r" + radius +
                " of (" + center.X.ToString("F1") + "," + center.Y.ToString("F1") + "," + center.Z.ToString("F1") + ")");
        return disabled;
    }


    // inits, so poll until ItemCount>0 then release. Backstop ~5s so a contentless/odd zone disarms.
    // S228: arm the deferred barrier drop. The ring is SharedGroup colliders that stream in over
    // several frames post-load, so retry DisableSpawnAreaColliders each frame until ≥1 is found near
    // spawn (AnoMech's pattern), then stop. ~5s backstop. Radius 10 matches AnoMech's spawn-ring drop.
    private bool barrierReleaseArmed;
    private int barrierReleaseFrames;
    private Vector3 barrierDropCenter;
    private void ArmBarrierRelease(Vector3 spawnCenter)
    {
        barrierReleaseFrames = 300; // ~5s backstop
        barrierDropCenter = spawnCenter;
        if (!barrierReleaseArmed) { barrierReleaseArmed = true; framework.Update += PollBarrierRelease; }
    }

    // v0.7.265: hide the graphics of specific wreck models by path substring (wep02/wep05 for 1345), and suppress
    // their collision meshes. Graphics hide = cosmetic (no floating wreck); collision suppress = removes their bump
    // collision. Tracked in hiddenInstanceKeys so the existing KillHiddenColliders pass zeroes their mesh colliders.
    // NB-15: m6d2_a3_nat01/_nat02 are combat-event visual clutter (debris no longer live in-lore for a free-roam
    // visit). Same treatment as the wrecks - hide graphics + suppress their collision. m6d2 prefix keeps the match
    // scoped to 1345 (the pass is 1345-gated anyway, but the full path stem is self-documenting and future-proof).
    private static readonly string[] BarrierModelMarkers = { "wep01", "wep06", "m6d2_a3_nat01", "m6d2_a3_nat02" };

    // v0.7.277: PRECISION model+collision suppression for a SINGLE instance of a repeated asset. Name/pcb matching
    // hits ALL instances (all 4 arch gates); to isolate ONE (gate #1 only), match by world POSITION - the same
    // precision identity the barrier boxes use. Each target hides the BgPart model at its position AND suppresses
    // the collider at that position (both matched within tolerance). Marker disambiguates when assets overlap.
    // v0.7.278: optional MoveTo - if set, RELOCATE the instance (model + collider together) instead of hiding it.
    // Gate #1 raised to Y=11 so the grate lifts above head height: gate stays visible, you walk under it.
    private struct PrecisionTarget
    {
        public System.Numerics.Vector3 Pos;    // where the instance currently is (match key)
        public string Marker;                   // path substring to disambiguate overlapping assets ("" = any)
        public System.Numerics.Vector3? MoveTo; // if set, relocate the MODEL here; else hide the model
        public bool SuppressCollision;          // if true, zero the collider (LayerMask=0) instead of moving it with the model
        public float? RotateYDeg;               // v0.7.342: if set, rotate the MODEL to this ABSOLUTE Y-axis angle (deg) -
                                                // swings a door open. Cosmetic only; pair with SuppressCollision for passage.
        public float? Radius;                   // v0.7.348: per-target match radius override (default PrecisionMatchRadius).
                                                // Tight radius needed when a target must discriminate between paired leaves
                                                // ~1.4-1.7y apart that share an asset - a wide radius grabs the wrong leaf.
    }
    private static readonly PrecisionTarget[] PrecisionSuppress1345 = new[]
    {
        // Arch gate #1 ONLY (leftmost). Raise the MODEL to Y=11 (gate floats up, visible) but SUPPRESS its
        // collision (zero LayerMask) rather than move it - moving a flat-grate Mesh collider by translation alone
        // doesn't reliably relocate the collision geometry (the World matrix wants a full transform rebuild), and
        // suppressing is the clean way to make the doorway passable. Gates 2/3/4 (same arf20 asset) untouched.
        new PrecisionTarget {
            Pos = new System.Numerics.Vector3(552.7f, 8.1f, -332.9f),
            Marker = "arf20",
            MoveTo = new System.Numerics.Vector3(552.7f, 11.0f, -332.9f),
            SuppressCollision = true,
        },
    };
    private const float PrecisionMatchRadius = 4.0f;  // 4y so a raised instance (Y 8.1→11 = 2.9y) stays matched for re-apply

    // v0.7.342: o1e1 (ffxiv/ocn_o1/evt/o1e1 - the seaship interior cutscene stage) doors. Rotate each door leaf open
    // about its Y axis (cosmetic swing) + suppress its collision (passage). Matched by the shared door mdl
    // (w_sip_002_11a) at each leaf's world position. Gated to the o1e1 stage bg (below) so it never touches 1345.
    private const string O1E1StageBg = "ffxiv/ocn_o1/evt/o1e1/level/o1e1";
    private static readonly PrecisionTarget[] PrecisionDoorsO1E1 = new[]
    {
        // Door wing 1 - swing to -76°, suppress collision. Position is the ACTUAL GetTranslation value from /hms
        // doordump (1.360, 10.352, -16.194) - the earlier (0.583,…) was the mdl-space coord, not what the instance
        // reports; matching needs the runtime translation.
        new PrecisionTarget {
            Pos = new System.Numerics.Vector3(1.360f, 10.352f, -16.194f),
            Marker = "w_sip_002_11a",
            RotateYDeg = 76f,
            SuppressCollision = true,
            Radius = 0.7f,
        },
        // Door wing 2 (same asset, other leaf) - swing to -107°, suppress collision. Real translation (-1.359, …).
        new PrecisionTarget {
            Pos = new System.Numerics.Vector3(-1.359f, 10.352f, -16.194f),
            Marker = "w_sip_002_11a",
            RotateYDeg = 107f,
            SuppressCollision = true,
            Radius = 0.7f,
        },
        // v0.7.350: remaining ship doors - positions corrected to the REAL GetTranslation values from /hms doordump
        // (the earlier gizmo-space coords were ~0.8-1.1y off and missed the 0.7y radius entirely). Each pair faces its
        // own direction → its own absolute angle. Collision suppressed for passage. Tight radius (0.7y) is safe - the
        // dump confirmed every wanted leaf is >1.5y from any other w_sip_002_11a instance.
        // Set A - a full pair (the negative-X pair; the positive-X pair at (2.963/5.681,…) is left closed).
        new PrecisionTarget { Pos = new System.Numerics.Vector3(-2.963f, 7.352f,  7.281f), Marker = "w_sip_002_11a", RotateYDeg =   85f, SuppressCollision = true, Radius = 0.7f },  // left
        new PrecisionTarget { Pos = new System.Numerics.Vector3(-5.682f, 7.352f,  7.281f), Marker = "w_sip_002_11a", RotateYDeg =   98f, SuppressCollision = true, Radius = 0.7f },  // right
        // Set B - upper deck, RIGHT leaf only (left leaf at (-1.359,11.852,0.896) left closed).
        new PrecisionTarget { Pos = new System.Numerics.Vector3( 1.360f, 11.852f, 0.896f), Marker = "w_sip_002_11a", RotateYDeg =  -85f, SuppressCollision = true, Radius = 0.7f },
        // Set C - observation deck, RIGHT leaf only (left leaf at (-1.359,4.613,-25.461) left closed).
        new PrecisionTarget { Pos = new System.Numerics.Vector3( 1.359f, 4.613f, -25.461f), Marker = "w_sip_002_11a", RotateYDeg = -100f, SuppressCollision = true, Radius = 0.7f },
    };
    // Restore-on-stop for rotated doors: original quaternion per instance key, and the set of instances we rotated.
    private readonly System.Collections.Generic.Dictionary<uint, System.Numerics.Quaternion> precisionSavedRot = new();
    private readonly System.Collections.Generic.HashSet<nint> precisionRotatedInstances = new();
    private bool o1e1DoorsLogged;   // v0.7.342: one-shot "doors applied to N" confirmation in the log

    // v0.7.353: 1345 road-mesh clones - fill the two pit-gaps at the crossroads near spawn by CLONING a nearby road
    // mesh's collider (m6d2_a1_flo01.mdl, grooved/varying-elevation) and placing it shifted into each gap. Ported from
    // HCollider's proven CopyMesh flow: read the source ColliderMesh's pcb Resource path + its Translation, then
    // AddColliderMesh(pcbPath, sourceTranslation + shiftDelta, sourceRotation, sourceScale). The groove profile travels
    // rigidly with a pure translation, so no stretch - just a shift along the road (X) axis.
    private struct MeshCloneTarget
    {
        public string SourceMdl;                  // the .mdl path substring of the source road mesh
        public System.Numerics.Vector3 SourceNear; // approx world pos of the SOURCE instance (to pick the right one)
        public System.Numerics.Vector3 Shift;      // v0.7.355: shift the clone by this vector (was X-only). Roads on the
                                                   // E-W lane shift in X; roads on the N-S lane (x≈-805) shift in Z.
    }
    private const uint RoadCloneTerritory = 1345;
    // v0.7.354: the /hms roaddump census revealed the road is TILED - each 24u segment is its own m6d2_a1_flo01.mdl
    // BgPart, and the two pits are the specific tiles at x=-830 and x=-780 (z=800) that have coll=no-collider (visual
    // road, but NO collision floor - which is exactly why they block passage). Their immediate neighbors DO carry a
    // Mesh collider. So: clone the Mesh-collider neighbor and shift it one tile (±24u) onto the pit. Sources are matched
    // by mdl-path + being the nearest MESH-collider instance to SourceNear (a no-collider tile at the same spot is
    // skipped - see the Mesh-only filter in ApplyRoadClones1345).
    private static readonly MeshCloneTarget[] RoadClones1345 = new[]
    {
        // v0.7.355: bridge the crossroads pits AND extend both road lanes. Grid is a 24u lattice; each target clones a
        // known Mesh-collider tile shifted onto a no-collider tile position. E-W lane (z≈800) shifts in X; N-S lane
        // (x≈-805) shifts in Z. Sources verified via /hms roaddump.
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-854.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(24.0f, 0.0f, 0.0f) },  // West pit x=-830
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-756.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-24.0f, 0.0f, 0.0f) },  // East pit x=-780
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-732.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(22.0f, 0.0f, 0.0f) },  // Gap1 x=-710
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-732.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(46.0f, 0.0f, 0.0f) },  // Gap1 x=-686
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-732.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(70.0f, 0.0f, 0.0f) },  // Gap1 x=-662
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-732.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(94.0f, 0.0f, 0.0f) },  // Gap1 x=-638
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-24.0f, 0.0f, 0.0f) },  // Gap2 x=-902
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-48.0f, 0.0f, 0.0f) },  // Gap2 x=-926
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-72.0f, 0.0f, 0.0f) },  // Gap2 x=-950
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-96.0f, 0.0f, 0.0f) },  // Gap2 x=-974
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-120.0f, 0.0f, 0.0f) },  // Gap2 x=-998
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-144.0f, 0.0f, 0.0f) },  // Gap2 x=-1022
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-168.0f, 0.0f, 0.0f) },  // Gap2 x=-1046
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-192.0f, 0.0f, 0.0f) },  // Gap2 x=-1070
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-878.0f, 0.0f, 800.0f), Shift = new System.Numerics.Vector3(-216.0f, 0.0f, 0.0f) },  // Gap2 x=-1094
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-805.0f, 0.0f, 727.0f), Shift = new System.Numerics.Vector3(0.0f, 0.0f, -25.0f) },  // Gap3 north pit z=702
        new MeshCloneTarget { SourceMdl = "m6d2_a1_flo01.mdl", SourceNear = new System.Numerics.Vector3(-805.0f, 0.0f, 727.0f), Shift = new System.Numerics.Vector3(0.0f, 0.0f, -120.0f) },  // Gap4 z=607 (full 24u gap; lattice-aligned, abuts proper road at ~596)
    };
    private readonly System.Collections.Generic.List<nint> roadCloneColliders = new();
    private bool roadClonesApplied;
    private readonly bool[] roadCloneDone = new bool[RoadClones1345.Length];  // per-target: already placed?

    // Apply the 1345 road-mesh clones (idempotent, per-target). Walks BgParts, finds each source by mdl-path + nearest
    // MESH-collider position, reads its pcb path + transform, and clones it shifted onto the pit via AddColliderMesh.
    // Road tiles stream in a few frames post-load, so a target that isn't found yet is left for a later frame; only
    // when ALL targets are placed does the pass mark itself done.
    private unsafe void ApplyRoadClones1345()
    {
        if (roadClonesApplied) return;
        var lw = LayoutWorld.Instance();
        if (lw == null) return;

        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var sw = fw != null && fw->BGCollisionModule != null && fw->BGCollisionModule->SceneManager != null
            ? fw->BGCollisionModule->SceneManager->FirstScene : null;
        if (sw == null) return;

        int made = 0;
        for (int ti = 0; ti < RoadClones1345.Length; ti++)
        {
            if (roadCloneDone[ti]) continue;   // this pit already bridged on an earlier frame
            var tgt = RoadClones1345[ti];
            // find the source instance: BgPart whose path contains the mdl AND is nearest tgt.SourceNear
            ILayoutInstance* bestInst = null; float bestDist = float.MaxValue;
            foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
            {
                if (layout == null) continue;
                if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null) continue;
                foreach (var kv in *m->Value)
                {
                    var inst = kv.Item2.Value;
                    if (inst == null) continue;
                    string path = "";
                    try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                    if (!path.Contains(tgt.SourceMdl)) continue;
                    // MUST have a Mesh collider to be a clonable source - the road is tiled and many m6d2_a1_flo01
                    // instances at the same X/Z are coll=no-collider (visual only). Skip those; only a Mesh tile clones.
                    var bp = (BgPartsLayoutInstance*)inst;
                    if (bp->Collider == null || bp->Collider->GetColliderType() != BGColliderType.Mesh) continue;
                    System.Numerics.Vector3 ip; inst->GetTranslation(&ip);
                    var d = System.Numerics.Vector3.Distance(ip, tgt.SourceNear);
                    if (d < bestDist) { bestDist = d; bestInst = inst; }
                }
            }
            if (bestInst == null || bestDist > 6f) continue;   // source not streamed yet - retry a later frame (quiet)

            var bgp = (BgPartsLayoutInstance*)bestInst;
            var coll = bgp->Collider;
            if (coll == null || coll->GetColliderType() != BGColliderType.Mesh) continue;
            var mesh = (ColliderMesh*)coll;
            if (mesh->Resource == null) continue;
            string pcb = "";
            try { pcb = mesh->Resource->GetPath().ToString(); } catch { }
            if (string.IsNullOrEmpty(pcb)) continue;

            // clone at source translation + shift (pure X translation; groove travels rigidly). Use the source's own
            // rotation/scale so the copy matches the road exactly.
            var pos = mesh->Translation; pos.X += tgt.Shift.X; pos.Y += tgt.Shift.Y; pos.Z += tgt.Shift.Z;
            var rot = mesh->Rotation;
            var scl = mesh->Scale; if (scl.X == 0) scl.X = 1; if (scl.Y == 0) scl.Y = 1; if (scl.Z == 0) scl.Z = 1;
            var bytes = System.Text.Encoding.UTF8.GetBytes(pcb + "\0");
            fixed (byte* p = bytes)
            {
                var c = (Collider*)sw->AddColliderMesh(1ul, p, false, &pos, &rot, &scl);
                if (c != null)
                {
                    roadCloneColliders.Add((nint)c); made++; roadCloneDone[ti] = true;
                    log.Information("[HMSync] [ROADCLONE] pit " + ti + " bridged: cloned " + pcb + " from (" +
                        mesh->Translation.X.ToString("F1") + ",_," + mesh->Translation.Z.ToString("F1") + ") shift(" +
                        tgt.Shift.X.ToString("F0") + "," + tgt.Shift.Z.ToString("F0") + ") → (" +
                        pos.X.ToString("F1") + ",_," + pos.Z.ToString("F1") + ").");
                }
            }
        }
        // Mark the pass done only when EVERY target is placed (tiles stream in over several frames; a target not yet
        // found is retried next frame). This replaced an unconditional one-shot that gave up on the first early miss.
        bool allDone = true;
        foreach (var d in roadCloneDone) if (!d) { allDone = false; break; }
        if (allDone) roadClonesApplied = true;
    }

    private unsafe void RemoveRoadClones()
    {
        if (roadCloneColliders.Count == 0) { roadClonesApplied = false; System.Array.Clear(roadCloneDone, 0, roadCloneDone.Length); return; }
        var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var sw = fw != null && fw->BGCollisionModule != null && fw->BGCollisionModule->SceneManager != null
            ? fw->BGCollisionModule->SceneManager->FirstScene : null;
        if (sw != null)
            foreach (var pc in roadCloneColliders)
            {
                var coll = (Collider*)pc;
                bool live = false;
                foreach (var c in sw->Scene->Colliders) if (c == coll) { live = true; break; }
                if (live) sw->RemoveCollider(coll);
            }
        roadCloneColliders.Clear();
        roadClonesApplied = false;
        System.Array.Clear(roadCloneDone, 0, roadCloneDone.Length);
    }
    private unsafe int HideBarrierModels()
    {
        int hid = 0;
        int bgPartsWalked = 0, wepMatched = 0, gfxHidden = 0;
        int wepGoNull = 0, wepMrhNull = 0, wepNotLoaded = 0, wepGoOk = 0;
        var sampleWepPaths = new System.Collections.Generic.List<string>();
        var lw = LayoutWorld.Instance();
        if (lw == null) return 0;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null)
                continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                bgPartsWalked++;
                string path = "";
                try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                bool match = false;
                foreach (var mk in BarrierModelMarkers) if (path.Contains(mk)) { match = true; break; }
                if (!match) continue;
                wepMatched++;
                if (sampleWepPaths.Count < 4 && !sampleWepPaths.Contains(path)) sampleWepPaths.Add(path);
                // Instrument WHY graphics resolution fails for BgPart weps.
                if (inst->Id.Type == InstanceType.BgPart)
                {
                    var bgd = (BgPartsLayoutInstance*)inst;
                    var god = bgd->GraphicsObject;
                    if (god == null) wepGoNull++;
                    else if (god->ModelResourceHandle == null) wepMrhNull++;
                    else if (god->ModelResourceHandle->LoadState < 7) wepNotLoaded++;
                    else wepGoOk++;
                }
                var gfx = GetEffectiveGraphics(inst);
                if (gfx != null) { gfx->IsVisible = false; hiddenLayoutInstances.Add((nint)inst); hid++; gfxHidden++; }
                hiddenInstanceKeys.Add(inst->Id.InstanceKey);   // KillHiddenColliders zeroes its mesh collider
            }
        }
        // Re-dump while weps are matched but not yet hidden (so we see the state AFTER models finish loading).
        // v0.7.447: per-tick, so route through DiagLog (research-mode gated) - this was flooding the log via
        // ReportDebug on every barrier-suppress scan. The one-shot "ALL HIDDEN" line stays informational.
        if (wepMatched > 0 && gfxHidden < wepMatched && (wepDiagTick % 60 == 0 || !wepDiagDumped))
        {
            wepDiagDumped = true;
            DiagLog("[HMSync] [WEP] f" + wepDiagTick + " bgParts=" + bgPartsWalked + " wepMatch=" + wepMatched +
                " gfxHidden=" + gfxHidden + " | goNull=" + wepGoNull + " mrhNull=" + wepMrhNull +
                " notLoaded=" + wepNotLoaded + " goOk=" + wepGoOk);
        }
        else if (wepMatched > 0 && gfxHidden >= wepMatched && !wepDoneDumped)
        {
            wepDoneDumped = true;
            DiagLog("[HMSync] [WEP] ALL HIDDEN: " + gfxHidden + " wep instances at f" + wepDiagTick);
        }
        wepDiagTick++;
        return hid;
    }
    private bool wepDiagDumped;

    // v0.7.285: 925 Terncliff hidden-city - INVESTIGATION PARKED (see investigation note). Diagnosis: 925 is the
    // event/cutscene layout (bg/ex3/01_nvt_n4/evt/n4eb). The loader parses the LGB (18 layers, 1080 instances) but
    // only INSTANTIATES 162 - the ~918 Visible=0 city models are NOT spawned (live InstancesByType[BgPart] matched=1,
    // not ~978). So a visibility flip (IsVisible=true) has nothing to flip. HCollider's collision net is the
    // collision system streaming the .pcb meshes independently of layout instantiation. The real quest cutscene
    // (planevent QST_LucKyw* scripts) reveals the city via the event/timeline system - NOT replicated by the bg-swap
    // in CutsceneStageService (which only redirects which bg.lgb loads; the file already loads here). Two forward
    // paths, both deferred: (1) CLONE the models (BgObject.Create per bg.csv entry + collision) - world-editor
    // instantiation scope; (2) LayerSet ACTIVATION via LayoutManager - a clean toggle IF the city sits in an
    // inactive instantiate-on-activate layer-set; needs an in-game probe to confirm the mechanism exists.

    // v0.7.353b diagnostic (/hms roaddump <term>): why did the road-clone find no source mesh? List every BgPart whose
    // path contains <term> (default "flo01"), with real GetPrimaryPath + GetTranslation + collider type. Reveals the
    // actual path string + position so the clone match can be fixed - the play that cracked the o1e1 doors.
    public unsafe void DumpRoads1345(string term)
    {
        if (string.IsNullOrEmpty(term)) term = "flo01";
        var lw = LayoutWorld.Instance();
        if (lw == null) { log.Information("[HMSync] [ROADDUMP] LayoutWorld null"); return; }
        int found = 0;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null) continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                string path = "";
                try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                if (!path.Contains(term)) continue;
                System.Numerics.Vector3 ip; inst->GetTranslation(&ip);
                var bgp = (BgPartsLayoutInstance*)inst;
                var coll = bgp->Collider;
                string ctype = coll == null ? "no-collider" : coll->GetColliderType().ToString();
                found++;
                log.Information("[HMSync] [ROADDUMP] #" + found + " pos=(" + ip.X.ToString("F3") + "," + ip.Y.ToString("F3") + "," +
                    ip.Z.ToString("F3") + ") coll=" + ctype + " path=" + path);
            }
        }
        log.Information("[HMSync] [ROADDUMP] total BgParts containing '" + term + "' = " + found);
    }

    // v0.7.349 diagnostic (/hms doordump): now dumps EVERY w_sip_002_11a instance in the stage with its exact
    // GetTranslation, and its distance to each of the 6 configured door targets - so we see immediately whether a
    // non-matching door is (a) at coords offset from the configured target (gizmo-space vs GetTranslation), or (b)
    // present but outside the 0.7y match radius.
    public unsafe void DumpDoorsO1E1()
    {
        log.Information("[HMSync] [DOORDUMP] gate: ActiveStageBg='" + (ActiveStageBg ?? "<null>") + "'  expected='" + O1E1StageBg +
            "'  match=" + ((ActiveStageBg ?? "") == O1E1StageBg));
        var lw = LayoutWorld.Instance();
        if (lw == null) { log.Information("[HMSync] [DOORDUMP] LayoutWorld null"); return; }

        // Report every w_sip_002_11a instance + its nearest configured target and that distance.
        int found = 0;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null) continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                string path = "";
                try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                if (!path.Contains("w_sip_002_11a")) continue;
                System.Numerics.Vector3 ip; inst->GetTranslation(&ip);
                // nearest configured target + distance
                float best = float.MaxValue; float bestAng = 0; int bestIdx = -1;
                for (int ti = 0; ti < PrecisionDoorsO1E1.Length; ti++)
                {
                    var d = System.Numerics.Vector3.Distance(ip, PrecisionDoorsO1E1[ti].Pos);
                    if (d < best) { best = d; bestAng = PrecisionDoorsO1E1[ti].RotateYDeg ?? 0; bestIdx = ti; }
                }
                var g = GetEffectiveGraphics(inst);
                found++;
                log.Information("[HMSync] [DOORDUMP] 11a #" + found +
                    " pos=(" + ip.X.ToString("F3") + "," + ip.Y.ToString("F3") + "," + ip.Z.ToString("F3") + ")" +
                    " nearestTarget#" + bestIdx + " dist=" + best.ToString("F2") + " (ang " + bestAng + ", radius 0.7)" +
                    (best <= 0.7f ? " MATCH" : " MISS") + (g == null ? " gfx=NULL" : ""));
            }
        }
        log.Information("[HMSync] [DOORDUMP] total w_sip_002_11a instances = " + found);
    }

    // v0.7.277: precision suppress - hide the BgPart model AND suppress the collider at each target's exact world
    // position (single instance of a repeated asset). Model: walk BgPart, position+marker match → IsVisible=false.
    // Collision: walk colliders, position match (any kind - the arf20 grate is a thin Mesh) → LayerMask=0 (saved
    // for restore). Persistent (models re-stream). Returns model-hide count.
    private unsafe int PrecisionSuppress() => PrecisionApply(PrecisionSuppress1345);

    // v0.7.342: generalized precision pass - apply an arbitrary target set (1345 arch gate, o1e1 doors, …). Model:
    // walk BgPart, position+marker match → hide, move, OR rotate-to-angle. Collision: the fused collider → suppress or
    // move. Persistent (models re-stream). Returns model-affected count.
    private unsafe int PrecisionApply(PrecisionTarget[] targets)
    {
        int hid = 0;
        var lw = LayoutWorld.Instance();
        if (lw == null) return 0;
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null)
                continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                System.Numerics.Vector3 ip; inst->GetTranslation(&ip);
                // match a precision target (position within tolerance + marker in path)
                bool matched = false; System.Numerics.Vector3? moveTo = null; bool suppressColl = false; float? rotY = null;
                foreach (var tgt in targets)
                {
                    if (System.Numerics.Vector3.Distance(ip, tgt.Pos) > (tgt.Radius ?? PrecisionMatchRadius)) continue;
                    if (tgt.Marker.Length > 0)
                    {
                        string path = "";
                        try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                        if (!path.Contains(tgt.Marker)) continue;
                    }
                    matched = true; moveTo = tgt.MoveTo; suppressColl = tgt.SuppressCollision; rotY = tgt.RotateYDeg; break;
                }
                if (!matched) continue;

                var bgp = (BgPartsLayoutInstance*)inst;
                var coll = bgp->Collider;        // its own collider (the fusion - same gate's pair)
                // Use GetEffectiveGraphics (NOT raw GraphicsObject): it returns null while the model is still
                // streaming (ModelResourceHandle null or LoadState<7). Calling UpdateTransforms on a not-yet-loaded
                // model faults inside BGObject.UpdateCulling (the crash). Skip this frame; the poll retries once
                // loaded. HCollider only moves on a user click (always loaded); our poll runs during streaming.
                var dgfx = GetEffectiveGraphics(inst);

                // --- MODEL: rotate (swing open), move (raise), or hide ---
                if (rotY.HasValue)
                {
                    if (dgfx != null)
                    {
                        var o = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)dgfx;
                        // Absolute Y-axis rotation to the target angle (degrees → radians → quaternion). Save the
                        // original rotation once so session-end can restore the door to closed.
                        uint rk = inst->Id.InstanceKey;
                        if (!precisionSavedRot.ContainsKey(rk)) precisionSavedRot[rk] = o->Rotation;
                        o->Rotation = System.Numerics.Quaternion.CreateFromAxisAngle(
                            System.Numerics.Vector3.UnitY, rotY.Value * (MathF.PI / 180f));
                        dgfx->UpdateTransforms(false); dgfx->UpdateCulling();
                        precisionRotatedInstances.Add((nint)inst);
                        hid++;
                    }
                }
                else if (moveTo.HasValue)
                {
                    if (dgfx != null)
                    {
                        var o = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)dgfx;
                        o->Position = moveTo.Value;
                        dgfx->UpdateTransforms(false); dgfx->UpdateCulling();
                        hid++;
                    }
                }
                else
                {
                    if (dgfx != null) { dgfx->IsVisible = false; hiddenLayoutInstances.Add((nint)inst); hid++; }
                    hiddenInstanceKeys.Add(inst->Id.InstanceKey);
                }

                // --- COLLISION: suppress (zero), or move with the model, or (for a plain hide) suppress too ---
                if (coll != null)
                {
                    if (suppressColl || !moveTo.HasValue)
                    {
                        // Zero it - the reliable way to clear the doorway (moving a flat-grate Mesh collider by
                        // translation alone doesn't relocate the collision geometry). Also the collision half of a
                        // plain hide.
                        uint ownerKey = (uint)(coll->LayoutObjectId & 0xFFFFFFFF);
                        if (!barrierSavedState.ContainsKey(ownerKey))
                            barrierSavedState[ownerKey] = (coll->VisibilityFlags, coll->LayerMask);
                        coll->VisibilityFlags &= unchecked((byte)~0x1);
                        coll->LayerMask = 0;
                    }
                    else if (moveTo.HasValue && dgfx != null)
                    {
                        // Move the collider WITH the model. Set all three transform components (like HCollider's
                        // MoveNativePart) so the collider's World matrix rebuilds - SetTranslation alone can leave
                        // the collision geometry at the old spot.
                        System.Numerics.Vector3 t = moveTo.Value;
                        System.Numerics.Vector3 er; coll->GetRotation(&er);
                        System.Numerics.Vector3 sc; coll->GetScale(&sc);
                        coll->SetTranslation(&t);
                        coll->SetRotation(&er);
                        coll->SetScale(&sc);
                    }
                }
            }
        }
        return hid;
    }
    private bool wepDoneDumped;
    private int wepDiagTick;

    // v0.7.265: arm the barrier suppression pass (1345, and later other duties). Barriers + wreck-model colliders
    // stream in over several frames post-load, so retry each frame until the Box barriers are found + dropped, then
    // stop. ~5s backstop. Mirrors ArmBarrierRelease's deferred-retry shape.
    private bool barrierSuppressArmed;
    private int barrierSuppressFrames;
    private uint barrierPassTerritory;
    private void ArmBarrierSuppress(uint territoryId)
    {
        barrierPassTerritory = territoryId;
        barrierSuppressFrames = 1800; // ~30s backstop - dungeon collision streams in well after the zone-load event
        if (!barrierSuppressArmed) { barrierSuppressArmed = true; framework.Update += PollBarrierSuppress; }
    }
    private unsafe void PollBarrierSuppress(IFramework fw)
    {
        barrierSuppressFrames--;
        bool inPersistentPhase = barrierSuppressFrames <= 0;
        // v0.7.468: LineVFX (type 59) re-assertion runs EVERY frame, deliberately ahead of the throttle below.
        // The boss-barrier lines re-stream on player movement, and at the 30-frame persistent interval a line
        // would be visible for up to half a second each time it returned. It's gated on HavePrimary() internally,
        // so a frame with nothing to do costs a map lookup and 13 pointer reads.
        if (lineVfxAuto) { try { SuppressLineVfxCadence(); } catch { } }
        // Initial phase: run every frame to catch barriers + weps as they stream in (~30s backstop).
        // Persistent phase: throttle, but NEVER disarm - the wep MODELS re-stream / their DrawObject gets
        // recreated as the player moves, so IsVisible=false must be re-applied continuously (this is exactly
        // how HCollider keeps a hidden model hidden, and how HMSync's furniture de-draw stays persistent).
        // The barrier COLLIDERS, once LayerMask=0, stay suppressed - but re-running the suppress is cheap and
        // covers any collider that re-streams, so we keep calling it too.
        if (inPersistentPhase)
        {
            persistentBarrierTick++;
            if (persistentBarrierTick < PersistentScanInterval) return;
            persistentBarrierTick = 0;
        }
        try
        {
            // All-maps pass + map-specific curtains. Map-specific patterns exist for 1345 (fire/void) and 893
            // (qic border curtains); MapSpecificVfxPatterns returns empty for others, so this is safe everywhere.
            HideBarrierVfx(includeMapSpecific: true);
            // Barrier colliders: the Plane-material (0x2400/0x4400) match is UNIVERSAL (self-identifying zone-wide),
            // so this runs on all instanced content. The Box position-match inside is naturally self-gating - the
            // 1345 position list only matches in 1345 - so calling it everywhere is safe and adds the barrier-Plane
            // drop for every instanced map (e.g. 893's boundary walls).
            SuppressBarrierColliders();
            // 1345-specific passes (terrain walls, wreck models) - gated to 1345 until each map's data is added.
            if (barrierPassTerritory == BarrierSuppressTerritory)
            {
                SuppressTerrainColliders();   // tr* Mesh colliders by pcb-name (1345 navigable walls)
                HideBarrierModels();          // wep01/wep06 graphics - re-applied every scan (they re-stream)
                PrecisionSuppress();          // single-instance model+collision by position (arch gate #1)
                KillHiddenColliders();        // zero the wep mesh colliders just keyed
                ApplyRoadClones1345();        // v0.7.353: clone road meshes to bridge the two pit-gaps (once)
            }
            // v0.7.342: o1e1 seaship-interior doors - gated by the active STAGE bg (cutscene stage, no TT of its own),
            // not territoryId. Rotate the two door leaves open + suppress their collision. Re-applied every scan.
            if ((ActiveStageBg ?? "") == O1E1StageBg)
            {
                int doorHits = PrecisionApply(PrecisionDoorsO1E1);
                if (!o1e1DoorsLogged && doorHits > 0) { o1e1DoorsLogged = true; log.Information("[HMSync] [O1E1DOORS] applied to " + doorHits + " door leaf/leaves."); }
                KillHiddenColliders();        // zero the suppressed door colliders just keyed
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] PollBarrierSuppress threw: " + ex.Message); }
        // No disarm here - the handler runs for the whole session (like PollDeferredDeDraw). Removed on
        // stop/leave and Dispose. This keeps re-streamed wep models hidden for the entire session.
    }
    private int persistentBarrierTick;

    private unsafe void PollBarrierRelease(IFramework fw)
    {
        barrierReleaseFrames--;
        bool done = false;
        try
        {
            int disabled = DisableSpawnAreaColliders(barrierDropCenter, 10f);
            if (disabled > 0) done = true; // ring colliders found + dropped
        }
        catch (Exception ex) { log.Error("[HMSync] PollBarrierRelease threw: " + ex.Message); done = true; }

        if (done || barrierReleaseFrames <= 0)
        {
            framework.Update -= PollBarrierRelease;
            barrierReleaseArmed = false;
            if (!done) DiagLog("[HMSync] [BARRIERDROP] backstop hit - no SharedGroups found near spawn.");
        }
    }

    // that skipped the in-resolve [LGBCBOX] test - 1345 is curated so ResolveFromLgb never ran). Dumps
    // all PopRange + CollisionBox + the full type set so we can compare a DUNGEON (OOB PopRange, e.g.
    // 1345) vs a WORKING zone (inns/Senatus/Command Room - spawn correctly, so their PopRange is sane).
    // The comparison shows whether the fix is "use the CollisionBox" or "pick a better PopRange".
    public void DumpLgb(uint territoryId)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            var territory = sheet?.GetRowOrDefault(territoryId);
            if (territory == null) { log.Information("[LGBDUMP] no TerritoryType row for " + territoryId); return; }
            var bg = territory.Value.Bg.ToString();
            if (string.IsNullOrEmpty(bg)) { log.Information("[LGBDUMP] empty Bg for " + territoryId); return; }
            var lastSlash = bg.LastIndexOf('/');
            var levelPath = lastSlash >= 0 ? bg[..lastSlash] : bg;

            log.Information("[LGBDUMP] === " + territoryId + " (" + bg + ") ===");
            // The data showed dungeon planevent.lgb is EMPTY while event-instances have PopRanges there.
            // So dungeon spawn/barrier lives in a DIFFERENT level LGB - probe ALL variants.
            foreach (var variant in new[] { "planevent", "bg", "planmap", "planner", "planlive", "vfx", "sound" })
            {
                var path = "bg/" + levelPath + "/" + variant + ".lgb";
                Lumina.Data.Files.LgbFile? lgb = null;
                try { lgb = dataManager.GetFile<Lumina.Data.Files.LgbFile>(path); } catch { }
                if (lgb == null) { log.Information("[LGBDUMP] " + variant + ".lgb: (not found)"); continue; }

                var typeCounts = new Dictionary<string, int>();
                int total = 0;
                // Spawn-relevant types we want FULL detail on (Name + index + pos) - to find the
                // ENTRANCE/EXIT/sub-portal markers distinct from boss-arena PopRanges (V's insight).
                var detailTypes = new HashSet<string> { "PopRange", "ExitRange", "EventRange",
                    "EventObject", "Aetheryte", "PositionMarker", "MapRange" };
                var detail = new List<string>();
                foreach (var layer in lgb.Layers)
                {
                    if (layer.InstanceObjects == null) continue;
                    foreach (var obj in layer.InstanceObjects)
                    {
                        total++;
                        var t = obj.AssetType.ToString();
                        typeCounts[t] = typeCounts.TryGetValue(t, out var n) ? n + 1 : 1;
                        if (detailTypes.Contains(t) || t.IndexOf("Collision", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var p = obj.Transform.Translation;
                            string nm = "";
                            try { nm = obj.Name ?? ""; } catch { }
                            detail.Add(t + " name='" + nm + "' (" +
                                p.X.ToString("F1") + "," + p.Y.ToString("F1") + "," + p.Z.ToString("F1") + ")");
                        }
                    }
                }
                log.Information("[LGBDUMP] " + variant + ".lgb: layers=" + lgb.Layers.Length + " objects=" + total +
                    " types=[" + string.Join(",", typeCounts.Select(kv => kv.Key + "=" + kv.Value)) + "]");
                foreach (var d in detail)
                    log.Information("[LGBDUMP]    " + d);
            }
            log.Information("[LGBDUMP] done.");
        }
        catch (Exception ex) { log.Error("[LGBDUMP] threw: " + ex.Message); }
    }


    // S214: disable the dungeon entry-ring barrier collider(s) at the spawn point. The barrier is a
    // CollisionBox whose collider stays active because the entry-trigger that normally releases it
    // never fires on a client-side load. We find CollisionBox instances within BarrierRadius of the
    // spawn, collect their InstanceKeys, then zero their colliders' LayerMask + raycast bit via the
    // BGCollision Scene walk (the proven furniture-collider mechanism). Only the spawn-centered
    // barrier is touched; other CollisionBoxes (real dungeon walls/edges) are far away and untouched.
    private const float BarrierRadius = 6.0f; // ring is small; nearest real CollisionBox was 127u away
    public unsafe void DisableSpawnBarrier(Vector3 spawn)
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null || lw->ActiveLayout == null) return;
            var layout = lw->ActiveLayout;

            // 1. Find CollisionBox instances centered on the spawn → collect their keys.
            var barrierKeys = new HashSet<uint>();
            if (layout->InstancesByType.TryGetValuePointer(InstanceType.CollisionBox, out var m) && m != null && m->Value != null)
            {
                foreach (var kv in *m->Value)
                {
                    var inst = kv.Item2.Value;
                    if (inst == null) continue;
                    var tb = (FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.TriggerBoxLayoutInstance*)inst;
                    if (tb->Collider == null) continue;
                    var t = tb->Transform.Translation;
                    float dx = t.X - spawn.X, dy = t.Y - spawn.Y, dz = t.Z - spawn.Z;
                    if (MathF.Sqrt(dx * dx + dy * dy + dz * dz) <= BarrierRadius)
                        barrierKeys.Add(inst->Id.InstanceKey);
                }
            }
            if (barrierKeys.Count == 0)
            {
                DiagLog("[HMSync] [BARRIER] no spawn-centered CollisionBox found (none within " +
                    BarrierRadius + "u) - nothing to disable.");
                return;
            }

            // 2. Disable those colliders via the BGCollision Scene walk (furniture-collider mechanism).
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var bgModule = fw != null ? fw->BGCollisionModule : null;
            var sceneMgr = bgModule != null ? bgModule->SceneManager : null;
            var sceneWrapper = sceneMgr != null ? sceneMgr->FirstScene : null;
            var scene = sceneWrapper != null ? sceneWrapper->Scene : null;
            int hit = 0;
            if (scene != null)
            {
                foreach (var col in scene->Colliders)
                {
                    uint ownerKey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                    if (barrierKeys.Contains(ownerKey))
                    {
                        col->VisibilityFlags &= unchecked((byte)~0x1); // ignore for raycasts/containment
                        col->LayerMask = 0;                            // participate in no collision layers
                        hit++;
                    }
                }
            }
            DiagLog("[HMSync] [BARRIER] disabled spawn barrier: " + barrierKeys.Count +
                " instance(s), " + hit + " collider(s) at spawn (" +
                spawn.X.ToString("F1") + "," + spawn.Y.ToString("F1") + "," + spawn.Z.ToString("F1") + ")");
        }
        catch (Exception ex) { log.Error("[HMSync] [BARRIER] DisableSpawnBarrier threw: " + ex.Message); }
    }

    // S156 DIAG [FURNMGR]: the GlobalLayout dump proves GlobalLayout is fully hidden (vis=0) on
    // both clean and broken hops, yet the user sees their APARTMENT furniture at apartment-
    // relative coords on a hop. Hypothesis: the visible pieces are the placed-furniture
    // GameObjects in HousingFurnitureManager.ObjectManager.ObjectArray - a SEPARATE object set
    // from GlobalLayout's ILayoutInstance graph, persisting because the client is still anchored
    // to the apartment as the real territory under the HMS scene. This walks that array and
    // reports, per furniture object: index, ObjectKind, position, DrawObject null?, and if
    // non-null its IsVisible. If we see populated objects with visible DrawObjects here while
    // GlobalLayout is vis=0, THIS is the layer leaking and the de-draw must also hide it.
    // Read-only. Position lets us match against the on-screen pieces (right in front of player).
    private void DumpFurnitureManagerObjects(string when)
    {
        try
        {
            var hm = HousingManager.Instance();
            if (hm == null) { DiagLog("[FURNMGR] (" + when + ") HousingManager NULL"); return; }
            var fm = hm->GetFurnitureManager();
            if (fm == null) { DiagLog("[FURNMGR] (" + when + ") FurnitureManager NULL (not indoor-anchored)"); return; }

            ref var arr = ref fm->ObjectManager.ObjectArray;
            int count = arr.ObjectCount;
            int withObj = 0, withDraw = 0, visible = 0;
            var sample = new System.Text.StringBuilder();
            int sampled = 0;
            var objs = arr.Objects;   // generated span from [FixedSizeArray] _objects
            for (int i = 0; i < count && i < objs.Length; i++)
            {
                var go = objs[i].Value;
                if (go == null) continue;
                withObj++;
                var draw = go->DrawObject;
                bool hasDraw = draw != null;
                bool isVis = false;
                if (hasDraw) { withDraw++; isVis = draw->IsVisible; if (isVis) visible++; }
                // Sample the first few present objects with position for on-screen matching.
                if (sampled < 8)
                {
                    var p = go->Position;
                    sample.Append(" [#").Append(i).Append(" kind=").Append(go->ObjectKind)
                          .Append(" pos=(").Append(p.X.ToString("F1")).Append(",").Append(p.Y.ToString("F1"))
                          .Append(",").Append(p.Z.ToString("F1")).Append(")")
                          .Append(" draw=").Append(hasDraw ? (isVis ? "VISIBLE" : "hidden") : "null").Append("]");
                    sampled++;
                }
            }
            DiagLog("[FURNMGR] (" + when + ") ObjectCount=" + count + " present=" + withObj +
                " withDrawObject=" + withDraw + " VISIBLE=" + visible + sample.ToString());
        }
        catch (Exception ex) { log.Error("[FURNMGR] (" + when + ") threw: " + ex.Message); }
    }

    // v0.7.422 DIAG [FURNDIAG]: the full furniture-object instrument. Dumps EVERY object in the
    // FurnitureManager's ObjectArray AND every GOM slot in the EventObjectManager range (440-500):
    // GOM index, ObjectKind (as raw byte + enum name), address, RenderFlags (raw hex), DrawObject
    // pointer, DrawObject.IsVisible, position. Purpose: the leaked tabletop items render from an
    // object our sweeps skip - GOMFURNHIDE suppressed only 2 of the manager's 17 (the other 15
    // report a DIFFERENT ObjectKind and are skipped by the kind filter). This names the rendering
    // object and its kind, so the fix is a widened filter, not a guess. Read-only; live pointers
    // fetched fresh, nothing stored.
    public void DumpFurnDiag()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var hm = HousingManager.Instance();
            sb.Append("[FURNDIAG] HousingManager=").Append(hm == null ? "NULL" : "ok");
            if (hm != null)
            {
                sb.Append(" IndoorTerritory=").Append((nint)hm->IndoorTerritory)
                  .Append(" CurrentTerritory=").Append((nint)hm->CurrentTerritory);
                var fm = hm->GetFurnitureManager();
                sb.Append(" FurnitureManager=").Append(fm == null ? "NULL" : ((nint)fm).ToString("X"));
                DiagLog(sb.ToString()); sb.Clear();

                if (fm != null)
                {
                    ref var arr = ref fm->ObjectManager.ObjectArray;
                    int count = arr.ObjectCount;
                    var objs = arr.Objects;
                    DiagLog("[FURNDIAG] === FurnitureManager.ObjectArray count=" + count + " ===");
                    for (int i = 0; i < count && i < objs.Length; i++)
                    {
                        var go = objs[i].Value;
                        if (go == null) continue;
                        var draw = go->DrawObject;
                        var p = go->Position;
                        // v0.7.424: SGL chain state - the layer the hide actually writes. sgl=null means the
                        // graphics hang somewhere else entirely (the estate stove/ashtray hypothesis).
                        var sgl = go->SharedGroupLayoutInstance;
                        string sglState = "null";
                        if (sgl != null)
                        {
                            var sglInst = (ILayoutInstance*)sgl;
                            var sglGfx = GetEffectiveGraphics(sglInst);
                            bool selfVis = sglGfx != null && sglGfx->IsVisible;
                            bool childVis = SharedGroupHasVisibleChild((SharedGroupLayoutInstance*)sgl, 0);
                            sglState = ((nint)sgl).ToString("X") + (selfVis ? "/SELFVIS" : "") + (childVis ? "/CHILDVIS" : "")
                                + (!selfVis && !childVis ? "/hidden" : "");
                        }
                        DiagLog("[FURNDIAG] FM[" + i + "] kind=" + go->ObjectKind + "(" + (byte)go->ObjectKind + ")"
                            + " addr=" + ((nint)go).ToString("X")
                            + " rflags=0x" + ((uint)go->RenderFlags).ToString("X")
                            + " draw=" + (draw == null ? "null" : ((nint)draw).ToString("X") + (draw->IsVisible ? "/VISIBLE" : "/hidden"))
                            + " sgl=" + sglState
                            + " pos=(" + p.X.ToString("F1") + "," + p.Y.ToString("F1") + "," + p.Z.ToString("F1") + ")");
                        // v0.7.426 - three-slot anomaly dump for the bound SGL subtree (SLOT rows).
                        if (sgl != null) DumpSglSlotAnomalies(i, (SharedGroupLayoutInstance*)sgl);
                    }
                }
            }
            else DiagLog(sb.ToString());

            // GOM sweep: the EventObjectManager range per the CS comment (449-488) widened to 440-500.
            var gom = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
            if (gom == null) { DiagLog("[FURNDIAG] GameObjectManager NULL"); return; }
            DiagLog("[FURNDIAG] === GOM slots 440-500 ===");
            var gArr = gom->Objects.IndexSorted;
            int printed = 0;
            for (int i = 440; i < 500 && i < gArr.Length; i++)
            {
                var go = gArr[i].Value;
                if (go == null) continue;
                var draw = go->DrawObject;
                var p = go->Position;
                DiagLog("[FURNDIAG] GOM[" + i + "] kind=" + go->ObjectKind + "(" + (byte)go->ObjectKind + ")"
                    + " addr=" + ((nint)go).ToString("X")
                    + " rflags=0x" + ((uint)go->RenderFlags).ToString("X")
                    + " draw=" + (draw == null ? "null" : ((nint)draw).ToString("X") + (draw->IsVisible ? "/VISIBLE" : "/hidden"))
                    + " pos=(" + p.X.ToString("F1") + "," + p.Y.ToString("F1") + "," + p.Z.ToString("F1") + ")");
                printed++;
            }
            DiagLog("[FURNDIAG] done - GOM range printed=" + printed);

            // v0.7.425 - FULL-GOM visible-draw sweep. Prints ONLY objects with a non-null, VISIBLE
            // DrawObject anywhere in the manager (all ~600 slots). Purpose: locate renderers that
            // are neither layout instances nor furniture-manager SGLs - e.g. aquarium FISH, which
            // draw per-aquarium from stocked-fish data (models under bgcommon/hou/indoor/gyo/).
            // Output is tiny by construction (players/NPCs plus whatever leaks).
            int visDraws = 0;
            for (int i = 0; i < gArr.Length; i++)
            {
                var go = gArr[i].Value;
                if (go == null) continue;
                var draw = go->DrawObject;
                if (draw == null || !draw->IsVisible) continue;
                var p = go->Position;
                DiagLog("[FURNDIAG] VISDRAW GOM[" + i + "] kind=" + go->ObjectKind + "(" + (byte)go->ObjectKind + ")"
                    + " addr=" + ((nint)go).ToString("X")
                    + " rflags=0x" + ((uint)go->RenderFlags).ToString("X")
                    + " draw=" + ((nint)draw).ToString("X")
                    + " pos=(" + p.X.ToString("F1") + "," + p.Y.ToString("F1") + "," + p.Z.ToString("F1") + ")");
                visDraws++;
            }
            DiagLog("[FURNDIAG] visible-draw sweep done - " + visDraws + " object(s)");

            // v0.7.426 - PROXIMITY SWEEP: every layout instance within 30u of the player that has ANY
            // visible graphics slot (BgPart field, GetGraphics vf23, GetGraphics2 vf24), regardless of
            // path. Walks all layouts (Global/Active/Prefetch/Loaded) × all six type buckets × the
            // layer hierarchy. Purpose: the five stragglers render on screen while every surface we
            // read reports "hidden" - this names their ACTUAL renderer, its container, its slot, and
            // its path in one capture, convicting between the multi-slot, unwalked-container, and
            // path-gate-miss theories.
            try
            {
                var lp = objectTable.LocalPlayer;
                if (lp == null) DiagLog("[FURNDIAG] proximity sweep skipped (no player)");
                else
                {
                    var ppos = lp.Position;
                    var lw = LayoutWorld.Instance();
                    int hits = 0;
                    if (lw != null)
                    {
                        var seenLm = new HashSet<nint>();
                        var seenInst = new HashSet<nint>();
                        var layouts = new System.Collections.Generic.List<nint>();
                        foreach (var lm0 in new[] { lw->GlobalLayout, lw->ActiveLayout, lw->PrefetchLayout })
                            if (lm0 != null && seenLm.Add((nint)lm0)) layouts.Add((nint)lm0);
                        try
                        {
                            foreach (var lkv in lw->LoadedLayouts)
                            { var lm0 = lkv.Item2.Value; if (lm0 != null && seenLm.Add((nint)lm0)) layouts.Add((nint)lm0); }
                        }
                        catch { }
                        foreach (var lmN in layouts)
                        {
                            var layout = (LayoutManager*)lmN;
                            foreach (var typeKey in new[] { InstanceType.SharedGroup, InstanceType.BgPart,
                                                            InstanceType.Vfx, InstanceType.Light,
                                                            InstanceType.IndoorObject, InstanceType.OutdoorObject })
                            {
                                if (!layout->InstancesByType.TryGetValuePointer(typeKey, out var mp) || mp == null) continue;
                                var im = mp->Value; if (im == null) continue;
                                foreach (var kv in *im)
                                {
                                    var inst = kv.Item2.Value;
                                    if (inst == null || !seenInst.Add((nint)inst)) continue;
                                    hits += ProxProbe(inst, ppos);
                                    if (hits >= 40) break;
                                }
                                if (hits >= 40) break;
                            }
                            if (hits < 40 && layout->Layers.Count > 0)
                            {
                                foreach (var lkv in layout->Layers)
                                {
                                    var lmm = lkv.Item2.Value; if (lmm == null) continue;
                                    foreach (var ikv in lmm->Instances)
                                    {
                                        var inst = ikv.Item2.Value;
                                        if (inst == null || !seenInst.Add((nint)inst)) continue;
                                        hits += ProxProbe(inst, ppos);
                                        if (hits >= 40) break;
                                    }
                                    if (hits >= 40) break;
                                }
                            }
                            if (hits >= 40) break;
                        }
                    }
                    DiagLog("[FURNDIAG] proximity sweep done - " + hits + " visible instance(s) within 30u"
                        + (hits >= 40 ? " (CAPPED)" : ""));
                }
            }
            catch (Exception ex) { DiagLog("[FURNDIAG] proximity sweep threw: " + ex.Message); }
        }
        catch (Exception ex) { log.Error("[FURNDIAG] threw: " + ex.Message); }
    }

    // v0.7.426 DIAG - three-slot subtree dump for a bound SGL. GetEffectiveGraphics returns ONE
    // graphics object per instance, but an instance can carry THREE: the BgPart GraphicsObject
    // FIELD, GetGraphics() (vf23), and GetGraphics2() (vf24). Both the hide and the detection only
    // ever touch the first non-null - anything rendering from another slot is invisible to both,
    // with perfectly consistent "hidden" readback (the straggler signature). Prints only anomalous
    // nodes: non-BgPart child types, >1 distinct populated slot, or any visible slot.
    private void DumpSglSlotAnomalies(int fmIndex, SharedGroupLayoutInstance* sg)
    {
        try { DumpSlotNode(fmIndex, (ILayoutInstance*)sg, 0); } catch { }
    }

    private void DumpSlotNode(int fmIndex, ILayoutInstance* inst, int depth)
    {
        if (inst == null || depth >= 4) return;
        try
        {
            DrawObject* fld = null; int ls = -1;
            if (inst->Id.Type == InstanceType.BgPart)
            {
                var bg = (BgPartsLayoutInstance*)inst;
                var goP = bg->GraphicsObject;
                if (goP != null)
                {
                    fld = (DrawObject*)goP;
                    ls = goP->ModelResourceHandle != null ? goP->ModelResourceHandle->LoadState : -2;
                }
            }
            DrawObject* g23 = null, g24 = null;
            try { g23 = (DrawObject*)inst->GetGraphics(); } catch { }
            try { g24 = (DrawObject*)inst->GetGraphics2(); } catch { }

            int distinct = 0;
            if (fld != null) distinct++;
            if (g23 != null && g23 != fld) distinct++;
            if (g24 != null && g24 != fld && g24 != g23) distinct++;
            bool anyVis = (fld != null && fld->IsVisible) || (g23 != null && g23->IsVisible)
                       || (g24 != null && g24->IsVisible);
            bool anomalous = anyVis || distinct > 1
                          || (depth > 0 && inst->Id.Type != InstanceType.BgPart);
            if (anomalous)
            {
                DiagLog("[FURNDIAG] SLOT FM[" + fmIndex + "]" + new string('.', depth)
                    + " t=" + inst->Id.Type
                    + " F=" + (fld == null ? "-" : ((nint)fld).ToString("X") + (fld->IsVisible ? "/V" : "/h") + "/ls" + ls)
                    + " 23=" + (g23 == null ? "-" : ((nint)g23).ToString("X") + (g23->IsVisible ? "/V" : "/h"))
                    + " 24=" + (g24 == null ? "-" : ((nint)g24).ToString("X") + (g24->IsVisible ? "/V" : "/h")));
            }

            // Descend SharedGroup-SHAPED children (SharedGroup, IndoorObject, OutdoorObject - the
            // .422 finding: type label differs, shape is the same).
            if (inst->Id.Type == InstanceType.SharedGroup || inst->Id.Type == InstanceType.IndoorObject
                || inst->Id.Type == InstanceType.OutdoorObject)
            {
                var vec = ((SharedGroupLayoutInstance*)inst)->Instances.Instances;
                for (long i = 0; i < vec.LongCount; i++)
                {
                    var child = vec[i].Value;
                    if (child == null) continue;
                    var ci = child->Instance;
                    if (ci == null) continue;
                    DumpSlotNode(fmIndex, ci, depth + 1);
                }
            }
        }
        catch { }
    }

    // v0.7.426 DIAG - proximity probe: if any of the instance's three graphics slots is visible and
    // its scene position is within 30u of the player, print type/slots/path/address. Path is printed
    // RAW (no IsHousingPath gate) - a bg/... path here convicts the path-gate-miss theory directly.
    private int ProxProbe(ILayoutInstance* inst, System.Numerics.Vector3 ppos)
    {
        try
        {
            DrawObject* fld = null; int ls = -1;
            if (inst->Id.Type == InstanceType.BgPart)
            {
                var bg = (BgPartsLayoutInstance*)inst;
                var goP = bg->GraphicsObject;
                if (goP != null)
                {
                    fld = (DrawObject*)goP;
                    ls = goP->ModelResourceHandle != null ? goP->ModelResourceHandle->LoadState : -2;
                }
            }
            DrawObject* g23 = null, g24 = null;
            try { g23 = (DrawObject*)inst->GetGraphics(); } catch { }
            try { g24 = (DrawObject*)inst->GetGraphics2(); } catch { }

            DrawObject* visSlot = null; string tag = "";
            if (fld != null && fld->IsVisible) { visSlot = fld; tag = "F"; }
            else if (g23 != null && g23->IsVisible) { visSlot = g23; tag = "23"; }
            else if (g24 != null && g24->IsVisible) { visSlot = g24; tag = "24"; }
            if (visSlot == null) return 0;

            var sp = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)visSlot)->Position;
            float dx = sp.X - ppos.X, dy = sp.Y - ppos.Y, dz = sp.Z - ppos.Z;
            if (dx * dx + dy * dy + dz * dz > 900f) return 0;

            string path = "(nopath)";
            try
            {
                var pp = inst->GetPrimaryPath();
                if (pp.HasValue)
                {
                    var span = pp.AsSpan();
                    path = System.Text.Encoding.UTF8.GetString(span.Slice(0, Math.Min(span.Length, 60)));
                }
            }
            catch { }

            DiagLog("[FURNDIAG] PROX t=" + inst->Id.Type + " slot=" + tag
                + " F=" + (fld == null ? "-" : (fld->IsVisible ? "V" : "h") + "/ls" + ls)
                + " 23=" + (g23 == null ? "-" : (g23->IsVisible ? "V" : "h"))
                + " 24=" + (g24 == null ? "-" : (g24->IsVisible ? "V" : "h"))
                + " pos=(" + sp.X.ToString("F1") + "," + sp.Y.ToString("F1") + "," + sp.Z.ToString("F1") + ")"
                + " path=" + path + " @" + ((nint)inst).ToString("X"));
            return 1;
        }
        catch { return 0; }
    }

    // S157 DIAG [LAYOUTS]: FURNMGR proved the visible furniture isn't a furniture-manager    // GameObject (all draw=null shells). The apartment furniture MESHES are ILayoutInstances -
    // and LayoutWorld.LoadedLayouts (StdMap @ 0x080, key=(LvbCrc<<32)|TerritoryTypeId) holds
    // EVERY resident layout, not just GlobalLayout. Hypothesis: on a hop the apartment's
    // LayoutManager lingers in LoadedLayouts (client never tore the apartment down) with its
    // furniture instances still VISIBLE, while our de-draw only ever walked GlobalLayout. This
    // enumerates every loaded layout with its TerritoryTypeId, InitState, HousingType, and
    // BgPart visible-count. PREDICTION: on broken 1011→1012, a SECOND layout (apartment, likely
    // HousingType>0 / a residential TerritoryTypeId) shows BgPart vis>0. If so → fix is "walk
    // all LoadedLayouts in de-draw, not just GlobalLayout." Read-only.
    private void DumpLoadedLayouts(string when)
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[LAYOUTS] (" + when + ") LayoutWorld NULL"); return; }
            var globalPtr = (nint)lw->GlobalLayout;
            var sb = new System.Text.StringBuilder();
            sb.Append("[LAYOUTS] (").Append(when).Append(") ");
            int n = 0;
            foreach (var kv in lw->LoadedLayouts)
            {
                var lm = kv.Item2.Value;
                if (lm == null) continue;
                n++;
                int vis = 0, tot = 0;
                if (lm->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) && m != null && m->Value != null)
                {
                    foreach (var ikv in *m->Value)
                    {
                        var inst = ikv.Item2.Value;
                        if (inst == null) continue;
                        tot++;
                        if (inst->HavePrimary())
                        {
                            var gfx = inst->GetGraphics();
                            if (gfx != null && ((DrawObject*)gfx)->IsVisible) vis++;
                        }
                    }
                }
                sb.Append("{terr=").Append(lm->TerritoryTypeId)
                  .Append(" init=").Append(lm->InitState)
                  .Append(" housing=").Append(lm->HousingType)
                  .Append(" bgTot=").Append(tot).Append(" bgVis=").Append(vis)
                  .Append((nint)lm == globalPtr ? " <-GLOBAL" : "").Append("} ");
            }
            sb.Append("count=").Append(n);
            log.Information(sb.ToString());
        }
        catch (Exception ex) { log.Error("[LAYOUTS] (" + when + ") threw: " + ex.Message); }
    }

    // S318: count VISIBLE housing furniture across every resident layout - the reinforcing trigger that
    // replaces the old "total BgPart count growth" heuristic. WHY: total-count growth was masked by the
    // zone's OWN streaming geometry (hundreds of bg/... BgParts churning in/out by proximity), so a
    // handful of bgcommon/hou/ furniture instances streaming in often didn't push the net count past its
    // high-water mark → no fire → the intermittent leak ("circle back and it clears"). This signal is
    // immune to that: it counts ONLY housing-path (bgcommon/hou/) instances that are currently VISIBLE,
    // so zone geometry (bg/ path) never registers, and it catches BOTH pillars (top-level BgPart) and
    // doors (BgPart leaves nested inside housing SharedGroups - recursed, same as the hide walk). The
    // poll fires the de-draw whenever this is > 0; after a successful hide the visible count drops to 0,
    // so it self-limits. Short-circuits on the FIRST visible housing instance (cheap in the common case).
    // Same safe live traversal as WalkLayoutAndHideFurniture - instances fetched fresh from the container
    // this frame, no held pointers (the S175 AV lesson). `limit` caps work on furniture-less zones.
    private bool AnyVisibleHousingFurniture()
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) return false;
            var seen = new HashSet<nint>();
            foreach (var lm in new[] { lw->GlobalLayout, lw->ActiveLayout, lw->PrefetchLayout })
            {
                if (lm == null || !seen.Add((nint)lm)) continue;
                if (LayoutHasVisibleHousing(lm)) return true;
            }
            try
            {
                foreach (var lkv in lw->LoadedLayouts)
                {
                    var lm = lkv.Item2.Value;
                    if (lm == null || !seen.Add((nint)lm)) continue;
                    if (LayoutHasVisibleHousing(lm)) return true;
                }
            }
            catch { /* LoadedLayouts mid-mutation - named-layout result still valid */ }
        }
        catch { return false; }
        return false;
    }

    // v0.7.423 - True if any furniture-manager object's SharedGroupLayoutInstance GRAPHICS are
    // visible. FURNDIAG (two-run diff, visible vs hidden) proved the GameObject layer is byte-for-
    // byte identical in both states: same addresses (no recreation on hop), same RenderFlags (our
    // 0x2 bit persists everywhere), all draw=null. The respawn is purely graphics-side - the hop
    // rebuilds the furniture objects' SGL graphics (the S177 path, reached via the furniture
    // manager, NOT via any layout container - which is also why Meddle/HCollider see nothing) and
    // the old IsVisible=false dies with the old graphics. The previous RenderFlags-based check here
    // therefore always returned false after a hop and the re-fire never triggered. This version
    // checks the EXACT chain the hide (HideFurnitureManagerObjects) writes: go->
    // SharedGroupLayoutInstance → effective graphics + descendant leaves → IsVisible. Any visible
    // leaf ⇒ re-fire ⇒ FURNMGRHIDE re-hides. Live pointers fetched fresh per call, nothing stored.
    private bool AnyVisibleFurnitureManagerObjects()
    {
        try
        {
            var hm = HousingManager.Instance();
            if (hm == null) return false;
            var fm = hm->GetFurnitureManager();
            if (fm == null) return false;
            ref var arr = ref fm->ObjectManager.ObjectArray;
            int count = arr.ObjectCount;
            var objs = arr.Objects;
            for (int i = 0; i < count && i < objs.Length; i++)
            {
                var go = objs[i].Value;
                if (go == null) continue;
                var sg = go->SharedGroupLayoutInstance;
                if (sg == null) continue;
                var inst = (ILayoutInstance*)sg;
                var gfx = GetEffectiveGraphics(inst);
                if (gfx != null && gfx->IsVisible) return true;
                if (SharedGroupHasVisibleChild((SharedGroupLayoutInstance*)sg, 0)) return true;
            }
        }
        catch { return false; }
        return false;
    }

    // True if this layout has any VISIBLE housing-path instance (BgPart leaf or SharedGroup-nested leaf).
    // v0.7.420: ALSO walks the layer hierarchy (Layers → LayerManager.Instances), not just InstancesByType.
    // Some furniture (tabletop items, crafting stations) lives only in the layer index and is invisible to
    // the InstancesByType walk - so the re-fire trigger never saw them restream and the persistent guard
    // never fired a re-hide. The hide walk in WalkLayoutAndHideFurniture already covered both paths; the
    // detection was the gap.
    private bool LayoutHasVisibleHousing(LayoutManager* layout)
    {
        if (layout == null) return false;
        try
        {
            // Walk 1: InstancesByType (the flat type-bucketed index)
            // v0.7.421: IndoorObject/OutdoorObject added - runtime-placed furniture registers here.
            // v0.7.427: Vfx/Light added - the LAST diff between the hide's type list and this one.
            // Detection must mirror every bucket the hide touches (the recurring bug class of this
            // entire arc); the VFX stragglers' flame/smoke renderables live in buckets the hide
            // swept but detection never watched.
            foreach (var typeKey in new[] { InstanceType.SharedGroup, InstanceType.BgPart,
                                            InstanceType.Vfx, InstanceType.Light,
                                            InstanceType.IndoorObject, InstanceType.OutdoorObject })
            {
                if (!layout->InstancesByType.TryGetValuePointer(typeKey, out var mapPtrPtr) || mapPtrPtr == null)
                    continue;
                var innerMap = mapPtrPtr->Value;
                if (innerMap == null) continue;

                foreach (var kv in *innerMap)
                {
                    var inst = kv.Item2.Value;
                    if (inst == null) continue;
                    if (!IsHousingPath(inst->GetPrimaryPath())) continue;

                    if (typeKey == InstanceType.BgPart || typeKey == InstanceType.Vfx
                        || typeKey == InstanceType.Light)
                    {
                        var gfx = GetEffectiveGraphics(inst);
                        if (gfx != null && gfx->IsVisible) return true;
                    }
                    else // SharedGroup-shaped (incl. IndoorObject/OutdoorObject): check nested leaves
                    {
                        if (SharedGroupHasVisibleChild((SharedGroupLayoutInstance*)inst, 0)) return true;
                    }
                }
            }

            // Walk 2: layer hierarchy (Layers → LayerManager.Instances). Mirrors the layer walk
            // in WalkLayoutAndHideFurniture. Some housing instances live here and NOT in InstancesByType
            // (empirically: tabletop items, crafting stations like Masonwork Stove). Without this walk,
            // the re-fire trigger never detects their restream → they persist visible on map hops.
            if (layout->Layers.Count > 0)
            {
                foreach (var lkv in layout->Layers)
                {
                    var lm = lkv.Item2.Value;
                    if (lm == null) continue;
                    foreach (var ikv in lm->Instances)
                    {
                        var inst = ikv.Item2.Value;
                        if (inst == null) continue;
                        if (!IsHousingPath(inst->GetPrimaryPath())) continue;

                        // v0.7.427: Vfx/Light gfx-checked; IndoorObject/OutdoorObject descended
                        // (the layer branch previously only handled BgPart + SharedGroup - the
                        // same one-family narrowing as everywhere else in this arc).
                        if (inst->Id.Type == InstanceType.BgPart || inst->Id.Type == InstanceType.Vfx
                            || inst->Id.Type == InstanceType.Light)
                        {
                            var gfx = GetEffectiveGraphics(inst);
                            if (gfx != null && gfx->IsVisible) return true;
                        }
                        else if (inst->Id.Type == InstanceType.SharedGroup
                              || inst->Id.Type == InstanceType.IndoorObject
                              || inst->Id.Type == InstanceType.OutdoorObject)
                        {
                            if (SharedGroupHasVisibleChild((SharedGroupLayoutInstance*)inst, 0)) return true;
                        }
                    }
                }
            }
        }
        catch { return false; }
        return false;
    }

    // Recurse a housing SharedGroup's children for any VISIBLE leaf graphic (mirrors HideSharedGroupChildren,
    // read-only). Depth-capped at 4 like the hide walk.
    private bool SharedGroupHasVisibleChild(SharedGroupLayoutInstance* sg, int depth)
    {
        if (sg == null || depth >= 4) return false;
        try
        {
            var vec = sg->Instances.Instances;
            for (long i = 0; i < vec.LongCount; i++)
            {
                var child = vec[i].Value;
                if (child == null) continue;
                var childInst = child->Instance;
                if (childInst == null) continue;

                var gfx = GetEffectiveGraphics(childInst);
                if (gfx != null && gfx->IsVisible) return true;

                if (childInst->Id.Type == InstanceType.SharedGroup
                    && SharedGroupHasVisibleChild((SharedGroupLayoutInstance*)childInst, depth + 1)) return true;
            }
        }
        catch { return false; }
        return false;
    }

    // S158: returns true if a layout instance's primary path is a PLACED HOUSING FURNITURE
    // path ("bgcommon/hou/"), distinguishing it from territory world geometry ("bg/<exp>/...").
    // Meddle-confirmed: every placed furnishing (partitions, pillars, chairs, etc.) resolves
    // under bgcommon/hou/..., while lampposts/tiles/walls resolve under bg/ex4|ffxiv|.../...
    // Byte-span prefix match - no allocation (runs per instance across thousands).
    private static ReadOnlySpan<byte> HousingPathPrefix => "bgcommon/hou/"u8;
    private static bool IsHousingPath(CStringPointer path)
    {
        if (!path.HasValue) return false;
        var span = path.AsSpan();
        return span.Length >= HousingPathPrefix.Length
            && span.Slice(0, HousingPathPrefix.Length).SequenceEqual(HousingPathPrefix);
    }

    // S159: recursively hide the graphics of a housing SharedGroup's nested child instances.
    // The visible furniture mesh is a BgPart child inside the SharedGroup's ChildNodeContainer
    // (SharedGroupLayoutInstance.Instances @ 0x080), not a top-level InstancesByType entry.
    // Children can themselves be SharedGroups (nested up to 4 deep per the struct docs), so we
    // recurse with a depth cap. Each child's graphics IsVisible is cleared and the instance is
    // tracked in hiddenLayoutInstances for restore-on-stop. Idempotent (re-clearing an already-
    // hidden child is harmless) so it's safe under the re-fire-on-wave logic. Returns count hidden.
    // S181: recursively dump a SharedGroup's descendant tree (type+pos+addr) into gfxResolveDiag,
    // to locate the rendering BgPart cluster we never hide.
    private void DumpSharedGroupTree(SharedGroupLayoutInstance* sg, int depth)
    {
        if (sg == null || depth >= 5) return;
        try
        {
            var vec = sg->Instances.Instances;
            for (long i = 0; i < vec.LongCount; i++)
            {
                var child = vec[i].Value;
                if (child == null) continue;
                var ci = child->Instance;
                if (ci == null) continue;
                string t = ci->Id.Type.ToString();
                string pos = "";
                if (ci->Id.Type == InstanceType.BgPart)
                {
                    var f = ((BgPartsLayoutInstance*)ci)->GraphicsObject;
                    pos = f != null ? "(" + f->Position.X.ToString("F2") + "," + f->Position.Z.ToString("F2") + ")" : "(?)";
                }
                gfxResolveDiag.Add(new string('.', depth + 1) + t + pos + "@" + (nint)ci);
                if (ci->Id.Type == InstanceType.SharedGroup)
                    DumpSharedGroupTree((SharedGroupLayoutInstance*)ci, depth + 1);
            }
        }
        catch { }
    }

    // NB-21 (2026-08-05): read-only runtime LAYER-STATE scanner for the geometry-advancement PoC (759 Doman Enclave,
    // 1189 Yak T'el/Mamook). The territory-keys cheat sheet (xivtool/dumps/territory-keys/NOTES.md) established that the
    // quest-progressive GEOMETRY in these two zones is NOT selectable by a layer-filter key (unlike Terncliff 919):
    //   • 759 rebuild stages are op=None bg layers the SERVER toggles by LayerId (Gate 3a). Known rebuilt-stage LayerIds:
    //       141903 (4.3_minka), 140494 (minka), 152867 (4.4_jikei_div3), 140505 (4.4_terakoya_div1), 162823 (terakoya_isu),
    //       140451 (teien_div1), 162824 (teien_isu), 140188 (4.5_tower), 140104 (kozakura_4.3), 140150 (kozakura_4),
    //       141938 (works_), 142083 (paper_).  Destruction LayerIds (want HIDDEN in the finished state):
    //       138746 (dst02), 139860 (dst02_low), 140214 (dst04), 152074 (dst44).
    //   • 1189 Mamook is one SharedGroup (sgbg_y6f3_v0_mamu0.sgb) with INTERNAL states step0..step5 (Gate 3b), mapped 1:1
    //       to the six orphan keys 290976..290981. Advancing = driving that SGB's state, not a layer toggle.
    // Neither lever is built yet, and the GO/NO-GO question is empirical: on an HMS virtual load, do those layers/SGBs even
    // STREAM IN (present-but-hidden ⇒ our proven GetEffectiveGraphics/IsVisible lever can un-hide them) or are they never
    // instantiated (⇒ a heavier lever is needed)? This scan answers exactly that before any write path is built — measure
    // first (skill Principle 2). PURE READ. Usage: `/hms layerscan` (per-layer summary) or `/hms layerscan <pathSubstr>`
    // (also lists matching instances + dumps SharedGroup trees, e.g. `/hms layerscan mamu0`). Grep [LAYERSCAN].
    public void LayerScan(string pathFilter)
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [LAYERSCAN] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [LAYERSCAN] no ActiveLayout/GlobalLayout"); return; }

            bool filt = !string.IsNullOrWhiteSpace(pathFilter);
            log.Information("[HMSync] [LAYERSCAN] terr=" + CurrentLoadedZone + " layout=" + ((nint)layout).ToString("X")
                + " layers=" + layout->Layers.Count + (filt ? "  pathFilter='" + pathFilter + "'" : "  (per-layer summary; pass a path substring for instance detail)"));

            // Collect one summary line per non-empty layer so the LayerIds from the cheat sheet can be matched by eye.
            // NB-21b: the runtime Layers-map key is NOT the LGB file LayerId (the cheat sheet's 138746 etc.), so we ALSO
            // capture a representative BgPart asset filename per layer - the model stems encode the stage (e3ec_dst02,
            // e3ec_4.4_terakoya_div1, teien_isu, ...), which is the version-proof bridge from a runtime layer to a
            // cheat-sheet stage name. That's what tells us which drawn layers are destruction (hide) vs rebuilt (keep).
            var rows = new List<(uint id, int total, int drawn, int hidden, string types, string sample)>();
            int matchInstances = 0;
            foreach (var lkv in layout->Layers)
            {
                uint layerId = lkv.Item1;
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                int total = 0, drawn = 0, hidden = 0;
                var typeCounts = new Dictionary<string, int>();
                string sample = "";
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    total++;
                    string ty = inst->Id.Type.ToString();
                    typeCounts[ty] = typeCounts.TryGetValue(ty, out var c) ? c + 1 : 1;
                    DrawObject* gfx = null;
                    try { gfx = GetEffectiveGraphics(inst); } catch { }
                    if (gfx != null) { if (gfx->IsVisible) drawn++; else hidden++; }

                    // First streamable asset filename (BgPart/SharedGroup) → the layer's stage label.
                    if (sample.Length == 0 && (inst->Id.Type == InstanceType.BgPart || inst->Id.Type == InstanceType.SharedGroup))
                    {
                        try
                        {
                            var pp = inst->GetPrimaryPath();
                            if (pp.HasValue)
                            {
                                string p = pp.ToString() ?? "";
                                int sl = p.LastIndexOf('/');
                                if (p.Length > 0) sample = sl >= 0 && sl < p.Length - 1 ? p.Substring(sl + 1) : p;
                            }
                        }
                        catch { }
                    }

                    if (filt)
                    {
                        string path = "";
                        try { var pp = inst->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
                        if (!string.IsNullOrEmpty(path) && path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            matchInstances++;
                            bool vis = gfx != null && gfx->IsVisible;
                            log.Information("[HMSync] [LAYERSCAN]   MATCH layer=" + layerId + " type=" + ty
                                + " key=" + inst->Id.InstanceKey + " gfx=" + (gfx == null ? "null(not-streamed)" : (vis ? "VISIBLE" : "hidden"))
                                + " path=" + path);
                            if (inst->Id.Type == InstanceType.SharedGroup)
                            {
                                gfxResolveDiag.Clear();
                                DumpSharedGroupTree((SharedGroupLayoutInstance*)inst, 0);
                                foreach (var e in gfxResolveDiag)
                                    log.Information("[HMSync] [LAYERSCAN]     SGTREE " + e);
                                gfxResolveDiag.Clear();
                            }
                        }
                    }
                }
                if (total > 0)
                {
                    string tb = string.Join(",", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + ":" + kv.Value));
                    rows.Add((layerId, total, drawn, hidden, tb, sample));
                }
            }

            foreach (var r in rows.OrderBy(r => r.id))
                log.Information("[HMSync] [LAYERSCAN] layer=" + r.id + " instances=" + r.total
                    + " drawn=" + r.drawn + " hidden=" + r.hidden + " streamedGfx=" + (r.drawn + r.hidden) + " types=" + r.types
                    + (r.sample.Length > 0 ? " asset=" + r.sample : ""));

            log.Information("[HMSync] [LAYERSCAN] done: " + rows.Count + " non-empty layers"
                + (filt ? ", " + matchInstances + " instances matched '" + pathFilter + "'" : "")
                + ". Cross-ref LayerIds against xivtool/dumps/territory-keys/NOTES.md. present-but-hidden ⇒ the IsVisible "
                + "lever can un-hide; gfx=null(not-streamed) ⇒ the layer never instantiated (heavier lever needed).");
        }
        catch (Exception ex) { log.Error("[HMSync] [LAYERSCAN] threw: " + ex.Message); }
    }

    // NB-22: TESTING-BUILD PoC lever for the Gate-3a "server-toggled geometry" zones (Doma Enclave 759,
    // and any zone whose stage geometry is op=None bg layers streamed+drawn with no filter key). The
    // /hms layerscan measurement proved 759 streams EVERY reconstruction stage and DRAWS all of them
    // (hidden=0) - so the advance is SUBTRACTIVE: hide the earlier-stage assets, leave the final set.
    //
    // The bridge that makes this precise WITHOUT a runtime→file LayerId join: the cheat-sheet stage
    // names ARE substrings of the asset filenames (e3ec_dst02, e3ec_4.4_terakoya_div1, teien_isu...),
    // and LayerScan's pathFilter already matches GetPrimaryPath() by substring. So this lever reuses
    // the exact same match and calls the PROVEN hide primitive on every hit. Example advance recipe:
    //   /hms layerhide dst      → hide all four destruction stages (dst02/dst02_low/dst04/dst44)
    // Restore: /hms layershow <substr>, or /hms stop (re-walks live layout by hiddenInstanceKeys).
    // Matched instances are tracked in hiddenInstanceKeys/hiddenLayoutInstances so the existing
    // crash-safe restore + KillHiddenColliders passes cover them for free. One-shot (not per-frame):
    // if native streaming re-shows a hidden stage we'll learn that from the test and promote it into
    // the persistent de-draw pass. Read side-effects only on the matched substring - inert otherwise.
    public void LayerSet(string substr, bool hide)
    {
        if (string.IsNullOrWhiteSpace(substr)) { log.Information("[HMSync] [LAYERSET] no substring given"); return; }
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [LAYERSET] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [LAYERSET] no ActiveLayout/GlobalLayout"); return; }

            string verb = hide ? "HIDE" : "SHOW";
            int matched = 0, acted = 0, sgChildren = 0, logged = 0;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    string path = "";
                    try { var pp = inst->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
                    if (string.IsNullOrEmpty(path) || !path.Contains(substr, StringComparison.OrdinalIgnoreCase)) continue;
                    matched++;

                    DrawObject* gfx = null;
                    try { gfx = GetEffectiveGraphics(inst); } catch { }
                    if (gfx != null)
                    {
                        if (hide)
                        {
                            HideGfx(gfx, inst, "LSET");
                            hiddenLayoutInstances.Add((nint)inst);
                            hiddenInstanceKeys.Add(inst->Id.InstanceKey);   // KillHiddenColliders + restore pick these up
                        }
                        else
                        {
                            gfx->IsVisible = true;
                        }
                        acted++;
                    }
                    // Reconstruction prefabs sometimes wrap stage geometry in a SharedGroup: recurse leaves.
                    if (inst->Id.Type == InstanceType.SharedGroup && hide)
                        sgChildren += HideSharedGroupChildren((SharedGroupLayoutInstance*)inst, 0);

                    if (logged < 12)
                    {
                        logged++;
                        log.Information("[HMSync] [LAYERSET] " + verb + " layer=" + lkv.Item1 + " type=" + inst->Id.Type
                            + " key=" + inst->Id.InstanceKey + " gfx=" + (gfx == null ? "null(not-streamed)" : "ok") + " path=" + path);
                    }
                }
            }
            log.Information("[HMSync] [LAYERSET] " + verb + " '" + substr + "' terr=" + CurrentLoadedZone
                + ": matched=" + matched + " acted=" + acted + (hide ? " sgChildren=" + sgChildren : "")
                + ". " + (hide ? "Use /hms layershow " + substr + " or /hms stop to restore." : "Restored via IsVisible=true.")
                + (matched == 0 ? " (no asset path contained the substring - check spelling / that the zone is loaded)" : ""));
        }
        catch (Exception ex) { log.Error("[HMSync] [LAYERSET] threw: " + ex.Message); }
    }

    // NB-24 (2026-08-06): the InstanceKey BRIDGE lever. The layerhide test proved 759's stage label lives ONLY in the LGB
    // layer NAME (e3ec_dst02 = destroyed rubble, 4.3_minka = rebuilt houses, ...), which the runtime STRIPS - so neither
    // GetPrimaryPath() substrings nor the runtime Layers-map key (a ushort, not the LGB LayerId) can tell a "hide" stage
    // from a "keep" stage. But every instance carries an Id.InstanceKey (uint), and the identity rule (TerritoryId, LgbFile,
    // LayerId, InstanceId) says that key SHOULD equal the LGB file InstanceId. xivtool's offline `lgb 759` dump gives, per
    // stage LayerName, the exact InstanceId set. If InstanceKey==InstanceId, we can hide a stage PRECISELY by its file
    // InstanceId set with zero asset-path ambiguity (dst rubble reuses the same generic box/itm models as rebuilt stages,
    // so nothing else discriminates them).
    //
    // NB-25 (2026-08-06): b48 measured WHY b47's idhide did nothing. b47 confirmed the bridge (InstanceKey==file
    // InstanceId, every id resolved) but hiding all 42 dst02 pieces via GetEffectiveGraphics+IsVisible=false produced
    // ZERO visible change. RE-handbook §4.3/§4.3a give two candidate causes, which b48 distinguishes by INSTRUMENT:
    //   (a) one-shot insufficient — streaming/a maintainer re-asserts IsVisible=true next frame (§4.3 lifetime rule:
    //       these hides must be re-applied PER FRAME). HMS's existing DeDraw poll only re-applies to HOUSING furniture,
    //       not arbitrary field bg → my ids were never re-held.
    //   (b) wrong render surface — the "straggler signature" (§ DumpSglSlotAnomalies): an instance carries up to THREE
    //       graphics (BgPart GraphicsObject FIELD, GetGraphics() vf23, GetGraphics2() vf24); GetEffectiveGraphics touches
    //       only the first non-null, so if the piece renders from another slot the hide misses it with consistent
    //       "hidden" readback. 759 field/terrain bg is exactly where field-vs-vfunc DIVERGE (S182).
    // So b48's idprobe dumps ALL THREE slots + Flags@0x88 + IsVisible per id (pure read), and idhide flips ALL non-null
    // slots AND registers the id set into a PER-FRAME hold (PollHeldHide) that re-applies every frame. If the pieces now
    // vanish → it was (a)/(b) and 759 is solved by all-slot per-frame hold. If they STILL render → these parts use a
    // non-DrawObject pipeline (streamed terrain, §12.4) and we pivot to the streaming-radius / ForceUpdateAllStreaming lever.

    // Per-frame hold set for the 759 subtractive advance (§4.3 lifetime rule). Keyed by InstanceKey (stable across
    // streaming); re-resolved live each frame. Disarmed by idshow / /hms stop.
    private readonly HashSet<uint> heldHideKeys = new();
    private bool heldHideArmed;

    // b56: before/after snapshot for the 759 CYCLE-mechanism diff (adv759diff). Keyed by InstanceKey; per instance we keep
    // the primary draw-slot pointer, IsVisible, and primary path. Captured by the first adv759diff call; diffed against the
    // live layout on the second call (the user runs the proven idnear500→idshowall cycle in between).
    private Dictionary<uint, (nint draw, byte flags, bool anyVis, string path, byte type)>? adv759Snap;

    // b60: per-facility CHILD snapshot for adv759sgstate's two-call diff. facilityKey → (childKey → (model path, visUnion)).
    // The 8 facilities' CONTAINER fields don't move across the cycle (proven b60), so the visible ruined→finished swap must
    // be in which CHILD meshes are drawn — this captures exactly that, scoped to the 8 buildings so the signal isn't drowned.
    private Dictionary<uint, Dictionary<uint, (string path, bool vis)>>? adv759ChildSnap;

    private static string DrawSlotStr(DrawObject* d, int loadState)
    {
        if (d == null) return "-";
        return ((nint)d).ToString("X") + "/f" + d->Flags.ToString("X2") + (d->IsVisible ? "/VIS" : "/hid")
            + (loadState >= 0 ? "/ls" + loadState : "");
    }

    // Returns (fld, g23, g24) for an instance — the three render surfaces §4.3a warns diverge.
    private static void GetAllSlots(ILayoutInstance* inst, out DrawObject* fld, out int fldLoad, out DrawObject* g23, out DrawObject* g24)
    {
        fld = null; fldLoad = -1; g23 = null; g24 = null;
        if (inst == null) return;
        if (inst->Id.Type == InstanceType.BgPart)
        {
            var go = ((BgPartsLayoutInstance*)inst)->GraphicsObject;
            if (go != null) { fld = (DrawObject*)go; fldLoad = go->ModelResourceHandle != null ? go->ModelResourceHandle->LoadState : -2; }
        }
        try { g23 = (DrawObject*)inst->GetGraphics(); } catch { }
        try { g24 = (DrawObject*)inst->GetGraphics2(); } catch { }
    }

    // Flip IsVisible on ALL distinct non-null render slots. Returns how many were touched.
    private static int SetAllSlotsVisible(ILayoutInstance* inst, bool visible)
    {
        GetAllSlots(inst, out var fld, out _, out var g23, out var g24);
        int n = 0;
        if (fld != null) { fld->IsVisible = visible; n++; }
        if (g23 != null && g23 != fld) { g23->IsVisible = visible; n++; }
        if (g24 != null && g24 != fld && g24 != g23) { g24->IsVisible = visible; n++; }
        return n;
    }

    // idprobe (read-only): per id dump ALL THREE render slots (F=BgPart field, 23=vf23, 24=vf24) with addr / Flags@0x88 /
    // IsVisible / model LoadState. This is what tells us whether a "hidden"-reading piece is actually rendering from a
    // slot we never touched (the straggler signature), the key unknown after b47.
    public void IdProbe(string csvIds)
    {
        var want = ParseIdCsv(csvIds);
        if (want.Count == 0) { log.Information("[HMSync] [IDPROBE] no numeric ids parsed from '" + csvIds + "'"); return; }
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [IDPROBE] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [IDPROBE] no ActiveLayout/GlobalLayout"); return; }

            var found = new HashSet<uint>();
            int totalInst = 0;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    totalInst++;
                    uint key = inst->Id.InstanceKey;
                    if (!want.Contains(key)) continue;
                    found.Add(key);
                    string path = "";
                    try { var pp = inst->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
                    GetAllSlots(inst, out var fld, out var fldLoad, out var g23, out var g24);
                    log.Information("[HMSync] [IDPROBE]   HIT id=" + key + " runtimeLayer=" + lkv.Item1 + " type=" + inst->Id.Type
                        + " F=" + DrawSlotStr(fld, fldLoad) + " 23=" + DrawSlotStr(g23, -1) + " 24=" + DrawSlotStr(g24, -1)
                        + " path=" + path);
                }
            }
            var miss = new List<uint>();
            foreach (var w in want) if (!found.Contains(w)) miss.Add(w);
            log.Information("[HMSync] [IDPROBE] terr=" + CurrentLoadedZone + " scanned=" + totalInst + " instances; asked=" + want.Count
                + " found=" + found.Count + " missing=" + miss.Count
                + (miss.Count > 0 ? " missingIds=" + string.Join(",", miss.Take(20)) : "")
                + ". Slots: F=BgPart GraphicsObject field, 23=GetGraphics(), 24=GetGraphics2(); fNN=Flags@0x88, VIS/hid=IsVisible.");
        }
        catch (Exception ex) { log.Error("[HMSync] [IDPROBE] threw: " + ex.Message); }
    }

    // idhide/idshow (MUTATING): hide/show every instance whose Id.InstanceKey is in the set — flipping ALL non-null render
    // slots (not just the first), and ARMING a per-frame hold so streaming can't re-assert. This is the real 759 advance:
    // hide the destruction-stage InstanceId set, leaving the cumulative rebuilt buildings. hiddenInstanceKeys tracks the
    // collider side; heldHideKeys tracks the per-frame render hold. idshow / /hms stop restore + disarm.
    public void IdSet(string csvIds, bool hide)
    {
        var want = ParseIdCsv(csvIds);
        if (want.Count == 0) { log.Information("[HMSync] [IDSET] no numeric ids parsed from '" + csvIds + "'"); return; }
        ApplyIdSet(want, hide, "IDSET");
    }

    // Shared core for every InstanceKey-driven hide/show (IdSet, Advance759). Walks the active layout, flips ALL render
    // slots for each matched instance, arms/disarms the per-frame hold, and hides SharedGroup children on hide. Keeps a
    // single source of truth so /hms advance759 and /hms idhide behave identically.
    public void ApplyIdSet(HashSet<uint> want, bool hide, string tag)
    {
        if (want.Count == 0) { log.Information("[HMSync] [" + tag + "] empty id set"); return; }
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [" + tag + "] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [" + tag + "] no ActiveLayout/GlobalLayout"); return; }

            string verb = hide ? "HIDE" : "SHOW";
            int matched = 0, slotsActed = 0, sgChildren = 0, logged = 0;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    uint key = inst->Id.InstanceKey;
                    if (!want.Contains(key)) continue;
                    matched++;

                    int n = SetAllSlotsVisible(inst, !hide);
                    slotsActed += n;
                    if (hide)
                    {
                        hiddenLayoutInstances.Add((nint)inst);
                        hiddenInstanceKeys.Add(key);   // collider pass + legacy restore
                        heldHideKeys.Add(key);         // per-frame render hold
                    }
                    else heldHideKeys.Remove(key);

                    if (inst->Id.Type == InstanceType.SharedGroup && hide)
                        sgChildren += HideSharedGroupChildren((SharedGroupLayoutInstance*)inst, 0);

                    if (logged < 8)
                    {
                        logged++;
                        string path = "";
                        try { var pp = inst->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
                        GetAllSlots(inst, out var fld, out var fldLoad, out var g23, out var g24);
                        log.Information("[HMSync] [" + tag + "] " + verb + " id=" + key + " runtimeLayer=" + lkv.Item1 + " type=" + inst->Id.Type
                            + " slots=" + n + " F=" + DrawSlotStr(fld, fldLoad) + " 23=" + DrawSlotStr(g23, -1) + " 24=" + DrawSlotStr(g24, -1)
                            + " path=" + path);
                    }
                }
            }

            if (hide && heldHideKeys.Count > 0 && !heldHideArmed)
            {
                heldHideArmed = true;
                framework.Update += PollHeldHide;
            }
            if (!hide && heldHideKeys.Count == 0 && heldHideArmed)
            {
                heldHideArmed = false;
                framework.Update -= PollHeldHide;
            }

            log.Information("[HMSync] [" + tag + "] " + verb + " terr=" + CurrentLoadedZone + ": asked=" + want.Count
                + " matched=" + matched + " slotsActed=" + slotsActed + (hide ? " sgChildren=" + sgChildren : "")
                + " heldSet=" + heldHideKeys.Count + " perFrameHold=" + (heldHideArmed ? "ON" : "off")
                + ". " + (hide ? "Per-frame hold armed; /hms idshowall or /hms stop to restore." : "Restored + unheld.")
                + (matched == 0 ? " (no live instance carried any of those InstanceKeys - is the zone loaded? did idprobe HIT them?)" : ""));
        }
        catch (Exception ex) { log.Error("[HMSync] [" + tag + "] threw: " + ex.Message); }
    }

    // /hmst advance759 — the automated Doma Enclave geometry-advance. AUTOMATES THE PROVEN RECIPE, not the (disproven)
    // subtractive-hold model: hiding a curated destruction id-set and HOLDING it hidden rendered NO visible change
    // in-game (b51). What DOES produce the finished enclave — measured, reproducible — is the hide-EVERYTHING → then
    // show-EVERYTHING CYCLE: `/hmst idnear 500` (hide the whole map around the player) immediately followed by
    // `/hmst idshowall` (the both-layout restore sweep). The re-stream *between* hide and show is what resolves the
    // "soup" (all reconstruction stages drawn at once) down to the current/final geometry — restoring re-shows the
    // tracked instances and the destruction strata come back at their finished state, not the rubble. So this command
    // just fires that two-beat sequence for you: IdNear(radius, hide) now, then ShowAllHidden() after a short settle
    // delay (the human gap between the two manual commands is load-bearing — the streaming needs a moment). Restore is
    // implicit (the showall IS the restore); if geometry looks wrong, just re-run. TESTING-tree tool (§4.9a case B).
    public void Advance759(float radius = 1000f, int settleMs = 1500)
    {
        if (CurrentLoadedZone != 759)
            log.Information("[HMSync] [ADVANCE759] note: current loaded zone is " + CurrentLoadedZone + " (recipe authored for 759); running the hide→show cycle anyway.");
        log.Information("[HMSync] [ADVANCE759] beat 1/2: hiding everything within " + radius.ToString("F0") + "u of the player (idnear " + radius.ToString("F0") + ")...");
        IdNear(radius, true);
        log.Information("[HMSync] [ADVANCE759] beat 1 done; scheduling beat 2/2 (idshowall restore) in " + settleMs + "ms to let the map re-stream.");
        _ = framework.RunOnTick(() =>
        {
            log.Information("[HMSync] [ADVANCE759] beat 2/2: idshowall (both-layout restore sweep) → the rebuilt enclave should now be current.");
            ShowAllHidden();
        }, delay: TimeSpan.FromMilliseconds(settleMs));
    }

    // b53: per-zone auto-advance config for reconstruction zones. radius = idnear reach (≥ map so player position is
    // irrelevant); settleMs = gap between the hide beat and the idshowall restore beat; streamInMs = how long to wait
    // after the zone-load completes for geometry to stream in BEFORE running the cycle (the cycle can only hide what's
    // already streamed). Tunable per zone; add a row to enable auto-advance for a new reconstruction map.
    private static readonly Dictionary<uint, (float radius, int settleMs, int streamInMs)> AutoAdvanceZones = new()
    {
        // b62/b64: auto-advance backed by the CLEAN DriveAdv759 (layout-wide DrawObject->IsVisible ratchet) — NOT the old
        // idnear→idshowall cycle. radius/settleMs are unused by the driver; only streamInMs matters (how long to wait
        // after load for the reconstruction geometry to stream in before we force-show it). See MaybeAutoAdvance.
        { 759, (0f, 0, 3000) },   // Doma Enclave (rebuilt) — SOLVED b64
        // b65/PROD: Yak T'el / Mamook (1189) — same reconstruction shape (mamu0 SGB step0..5), driven by the same
        // zone-agnostic Pass 2. DEFERRED — NOT shipped: on this large field zone the layout-wide force-show is a no-op
        // for the rebuild and risks revealing unrelated far/LOD geometry, so 759 is the only shipped reconstruction zone.
        // Re-enable the row below if/when 1189's geometry advance is solved and validated in-game.
        // { 1189, (0f, 0, 3000) },  // Mamook (rebuilt) — deferred (see above)
        // b66 attempted Elysion 1073 here and it did NOTHING: 1073 is a FILTER-KEY zone (Terncliff family), not a
        // resident-hidden one — the built town is not loaded under the default key, so the ratchet has nothing to reveal.
        // It's handled by ForcedCastKeys{1073,268557} instead (b67). Do NOT re-add 1073 here.
    };

    // b53: called at the tail of a completed HMS virtual load. If the loaded territory is a configured reconstruction
    // zone, arm the advance cycle to fire once the geometry has streamed in — so the user pops straight into the
    // finished enclave without typing anything. Re-checks CurrentLoadedZone when the timer fires so a fast
    // leave/reload before stream-in doesn't run the cycle on the wrong (or no) zone.
    private void MaybeAutoAdvance(uint territoryId)
    {
        if (!AutoAdvanceZones.TryGetValue(territoryId, out var cfg)) return;
        log.Information("[HMSync] [ADVANCE759] auto-advance armed for zone " + territoryId + ": waiting " + cfg.streamInMs
            + "ms for stream-in, then the CLEAN PlayTimeline step-advance (DriveAdv759, no hide→show cycle).");
        _ = framework.RunOnTick(() =>
        {
            if (CurrentLoadedZone != territoryId)
            {
                log.Information("[HMSync] [ADVANCE759] auto-advance aborted (zone changed from " + territoryId + " to " + CurrentLoadedZone + " before stream-in settled).");
                return;
            }
            log.Information("[HMSync] [ADVANCE759] auto-advance: stream-in wait elapsed, driving zone " + territoryId + " to finished.");
            DriveAdv759("ADV759DRIVE-AUTO");
        }, delay: TimeSpan.FromMilliseconds(cfg.streamInMs));
    }

    // b54: load an embedded newline/comma-delimited InstanceId set by resource-name suffix. b55: drop whole comment lines
    // (trimmed start == '#') BEFORE parsing — a bare number in a comment header ("# 759 FINISHED...") otherwise parses as
    // a bogus InstanceId, since ParseIdCsv only filters by uint.TryParse and "759" is a valid uint. Shared by adv759 probe.
    private HashSet<uint> LoadEmbeddedIds(string suffix)
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                log.Error("[HMSync] [ADV759PROBE] embedded '" + suffix + "' NOT found. Resources: " + string.Join(", ", asm.GetManifestResourceNames()));
                return new HashSet<uint>();
            }
            using var stream = asm.GetManifestResourceStream(resName);
            using var reader = new System.IO.StreamReader(stream!);
            var set = new HashSet<uint>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                foreach (var id in ParseIdCsv(line)) set.Add(id);
            }
            return set;
        }
        catch (Exception ex) { log.Error("[HMSync] [ADV759PROBE] load '" + suffix + "' threw: " + ex.Message); return new HashSet<uint>(); }
    }

    // hmst adv759probe — THE DECISION TEST (b54). Answers whether 759 can be advanced by a clean load-time subtractive
    // hide (no hide-everything cycle, no blank flash) or whether the cycle is fundamentally required. Two beats, ONE
    // command:
    //   READ (non-mutating): tally the FINISHED rebuilt-building instance-ids (ground truth, embedded) that are already
    //     present / drawn / visible in the RAW virtual load. If the rebuilt buildings are already instantiated and
    //     visible, then hiding the destruction strata SHOULD reveal them — path 1 is reachable. If they're absent, the
    //     finished geometry only exists after the cycle's re-stream and subtractive can never work.
    //   ACT (mutating): hide ONLY the authoritative destruction stage-ids (dst* + scaffolding) — all-slot + per-frame
    //     held, via the shared ApplyIdSet core. NO hide-everything, so there is no blank period to watch through. LOOK:
    //     does the enclave look finished/clean? Restore with `hmst idshowall`.
    // This is the corrected subtractive attempt using ground-truth ids (the b51 null was a suspect id list); it decides
    // the proper-advance design. Read-heavy; the only mutation is the destruction hide, fully reverted by idshowall.
    public void Adv759Probe()
    {
        var finished = LoadEmbeddedIds("adv759.finished.txt");
        var destruction = LoadEmbeddedIds("adv759.destruction.txt");
        if (finished.Count == 0 && destruction.Count == 0) { log.Information("[HMSync] [ADV759PROBE] both id sets empty - embed missing?"); return; }
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [ADV759PROBE] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [ADV759PROBE] no ActiveLayout/GlobalLayout"); return; }

            int finPresent = 0, finDrawn = 0, finVisible = 0, dstPresent = 0, logged = 0;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    uint key = inst->Id.InstanceKey;
                    if (finished.Contains(key))
                    {
                        finPresent++;
                        GetAllSlots(inst, out var fld, out _, out var g23, out var g24);
                        var d = fld != null ? fld : (g23 != null ? g23 : g24);
                        if (d != null)
                        {
                            finDrawn++;
                            if (d->IsVisible) finVisible++;
                            if (logged < 10) { logged++; log.Information("[HMSync] [ADV759PROBE] finished id=" + key + " type=" + inst->Id.Type + " " + DrawSlotStr(d, -1)); }
                        }
                    }
                    else if (destruction.Contains(key)) dstPresent++;
                }
            }
            log.Information("[HMSync] [ADV759PROBE] terr=" + CurrentLoadedZone
                + " FINISHED: embedded=" + finished.Count + " present=" + finPresent + " drawn=" + finDrawn + " visible=" + finVisible
                + "  |  DESTRUCTION: embedded=" + destruction.Count + " present=" + dstPresent);
            log.Information("[HMSync] [ADV759PROBE] READ verdict: if FINISHED present≈embedded and visible>0 → the rebuilt buildings are ALREADY in the raw load → clean subtractive (path 1) should work.");

            // ACT: hide ONLY destruction (held). No hide-everything → no blank period. Look at the enclave now.
            ApplyIdSet(destruction, true, "ADV759PROBE");
            log.Information("[HMSync] [ADV759PROBE] destruction hidden (held). LOOK NOW: is the enclave clean/finished with NO blank flash? Then `hmst idshowall` to restore. This decides path 1 (clean) vs cycle-required.");
        }
        catch (Exception ex) { log.Error("[HMSync] [ADV759PROBE] threw: " + ex.Message); }
    }

    // Snapshot the WHOLE live layout into a key→state map (effective-graphics draw ptr, IsVisible, primary path, type). b56.1:
    // DESCENDS into SharedGroup children (sg->Instances) — the first b56 diff was blind because 759's finished buildings are SG
    // CHILDREN (the cycle touched sgChildren=2497 while top-level was byte-identical). Uses GetEffectiveGraphics (the renderable
    // idnear/idshowall actually act on) so the snapshot's visibility matches what the cycle mutates. Used by adv759diff.
    private Dictionary<uint, (nint draw, byte flags, bool anyVis, string path, byte type)> SnapshotLayout(LayoutManager* layout)
    {
        var snap = new Dictionary<uint, (nint, byte, bool, string, byte)>();
        int topLevel = 0, sgSeen = 0, childVisited = 0, childNew = 0;
        foreach (var lkv in layout->Layers)
        {
            var lm = lkv.Item2.Value;
            if (lm == null) continue;
            foreach (var ikv in lm->Instances)
            {
                topLevel++;
                SnapInstance(ikv.Item2.Value, snap, 0, ref sgSeen, ref childVisited, ref childNew);
            }
        }
        log.Information("[HMSync] [ADV759DIFF] snapshot traversal: topLevel=" + topLevel + " SGs=" + sgSeen
            + " childrenVisited=" + childVisited + " childrenNewKeys=" + childNew + " uniqueTotal=" + snap.Count);
        return snap;
    }

    // Record one instance's render state into snap, then recurse into SharedGroup children (nested ≤4 deep, mirrors
    // HideSharedGroupChildren's traversal). Keyed by InstanceKey (unique LGB InstanceId, distinct for children).
    private void SnapInstance(ILayoutInstance* inst, Dictionary<uint, (nint, byte, bool, string, byte)> snap, int depth,
        ref int sgSeen, ref int childVisited, ref int childNew)
    {
        if (inst == null || depth >= 4) return;
        uint key = inst->Id.InstanceKey;
        if (depth > 0) { childVisited++; if (!snap.ContainsKey(key)) childNew++; }
        var d = GetEffectiveGraphics(inst);
        // Capture raw Flags@0x88 of the effective draw (catches non-IsVisible bit flips) + the VISIBILITY UNION across all
        // three render slots (BgPart field, vf23, vf24) — §4.3a warns the slot that actually renders can diverge from
        // GetEffectiveGraphics, so a change on a divergent slot would be invisible to a single-slot read.
        byte flags = d != null ? d->Flags : (byte)0;
        GetAllSlots(inst, out var fld, out _, out var g23, out var g24);
        bool anyVis = (d != null && d->IsVisible) || (fld != null && fld->IsVisible) || (g23 != null && g23->IsVisible) || (g24 != null && g24->IsVisible);
        string path = "";
        try { var pp = inst->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
        snap[key] = ((nint)d, flags, anyVis, path, (byte)inst->Id.Type);

        if (inst->Id.Type == InstanceType.SharedGroup)
        {
            sgSeen++;
            try
            {
                var vec = ((SharedGroupLayoutInstance*)inst)->Instances.Instances;
                for (long i = 0; i < vec.LongCount; i++)
                {
                    var child = vec[i].Value;
                    if (child == null) continue;
                    SnapInstance(child->Instance, snap, depth + 1, ref sgSeen, ref childVisited, ref childNew);
                }
            }
            catch { }
        }
    }

    // hmst adv759diff — MEASURE what the proven idnear500→idshowall CYCLE actually does (b56). The b55 probe proved the raw
    // load is NOT the finished enclave (tower+decorations only) and our offline finished/destruction id-sets don't map to the
    // cycle's effect — so stop guessing sets and DIFF the live layout across the cycle. Two-call protocol:
    //   1) `hmst adv759diff` on a fresh RAW 759 → captures baseline (every instance: primary draw ptr, IsVisible, path).
    //   2) run the PROVEN cycle: `hmst idnear 500` then `hmst idshowall` (yields the true finished enclave).
    //   3) `hmst adv759diff` again → diffs live-vs-baseline and classifies every change: SPAWNED (key new), DESPAWNED (gone),
    //      DRAW-ON (null→ptr), DRAW-OFF (ptr→null), PATH-CHANGED (model swap = stage change), REDRAW (ptr changed, same path),
    //      VIS-ON/VIS-OFF (IsVisible flip only).
    // Category profile decides the mechanism: mostly VIS-flips ⇒ pure visibility, the "finished set" is just what turned
    // visible and a clean subtractive/additive lever exists; DRAW-ON / PATH-CHANGED / REDRAW ⇒ the cycle re-streams or swaps
    // models (explains why a flag-only subtractive can't reproduce it, and points at the streaming/LOD lever instead).
    // Read-only: the only mutation is the user's own cycle commands run between the two calls.
    public void Adv759Diff()
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [ADV759DIFF] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [ADV759DIFF] no ActiveLayout/GlobalLayout"); return; }

            if (adv759Snap == null)
            {
                adv759Snap = SnapshotLayout(layout);
                log.Information("[HMSync] [ADV759DIFF] BASELINE captured: " + adv759Snap.Count + " instances (terr=" + CurrentLoadedZone
                    + "). NOW run the proven cycle: `hmst idnear 500` then `hmst idshowall`, then `hmst adv759diff` again to see the delta.");
                return;
            }

            var before = adv759Snap;
            var after = SnapshotLayout(layout);
            adv759Snap = null; // consume; next call re-baselines

            int spawned = 0, despawned = 0, drawOn = 0, drawOff = 0, pathChg = 0, redraw = 0, visOn = 0, visOff = 0, flagChg = 0, unchanged = 0;
            int logCap = 20; var logged = new Dictionary<string, int>();
            void Sample(string cat, uint key, string detail)
            {
                logged.TryGetValue(cat, out var c);
                if (c >= logCap) return;
                logged[cat] = c + 1;
                log.Information("[HMSync] [ADV759DIFF]   " + cat + " id=" + key + " " + detail);
            }

            foreach (var kv in after)
            {
                if (!before.TryGetValue(kv.Key, out var b)) { spawned++; Sample("SPAWNED", kv.Key, "type=" + kv.Value.type + " draw=" + (kv.Value.draw != 0) + " vis=" + kv.Value.anyVis + " path=" + kv.Value.path); continue; }
                var a = kv.Value;
                bool bHad = b.draw != 0, aHad = a.draw != 0;
                if (!bHad && aHad) { drawOn++; Sample("DRAW-ON", kv.Key, "type=" + a.type + " vis=" + a.anyVis + " path=" + a.path); }
                else if (bHad && !aHad) { drawOff++; Sample("DRAW-OFF", kv.Key, "type=" + a.type + " wasPath=" + b.path); }
                else if (bHad && aHad && !string.Equals(a.path, b.path, StringComparison.OrdinalIgnoreCase)) { pathChg++; Sample("PATH-CHANGED", kv.Key, "type=" + a.type + " " + b.path + " -> " + a.path); }
                else if (bHad && aHad && a.draw != b.draw) { redraw++; Sample("REDRAW", kv.Key, "type=" + a.type + " ptr moved, path=" + a.path); }
                else if (b.anyVis && !a.anyVis) { visOff++; Sample("VIS-OFF", kv.Key, "type=" + a.type + " path=" + a.path); }
                else if (!b.anyVis && a.anyVis) { visOn++; Sample("VIS-ON", kv.Key, "type=" + a.type + " path=" + a.path); }
                else if (a.flags != b.flags) { flagChg++; Sample("FLAGS-CHG", kv.Key, "type=" + a.type + " f" + b.flags.ToString("X2") + "->f" + a.flags.ToString("X2") + " path=" + a.path); }
                else unchanged++;
            }
            foreach (var kv in before)
                if (!after.ContainsKey(kv.Key)) { despawned++; Sample("DESPAWNED", kv.Key, "type=" + kv.Value.type + " wasPath=" + kv.Value.path); }

            log.Information("[HMSync] [ADV759DIFF] terr=" + CurrentLoadedZone + " before=" + before.Count + " after=" + after.Count
                + " | SPAWNED=" + spawned + " DESPAWNED=" + despawned + " DRAW-ON=" + drawOn + " DRAW-OFF=" + drawOff
                + " PATH-CHANGED=" + pathChg + " REDRAW=" + redraw + " VIS-ON=" + visOn + " VIS-OFF=" + visOff + " FLAGS-CHG=" + flagChg + " unchanged=" + unchanged);
            log.Information("[HMSync] [ADV759DIFF] READ: dominant VIS-ON/FLAGS-CHG ⇒ finished stage is instance render-state (a clean lever exists). Near-ZERO across all cats (given a CONFIRMED ruined→finished cycle) ⇒ the transform is NOT in these instances → §12.4 streamed-terrain pipeline; pursue the streaming lever.");
        }
        catch (Exception ex) { log.Error("[HMSync] [ADV759DIFF] threw: " + ex.Message); }
    }

    // Per-frame re-assertion of the held hide set (§4.3 lifetime rule). Re-resolves live instances by key each frame
    // (never stores pointers) and flips all their slots hidden. Cheap: one layout walk, small held set.
    private void PollHeldHide(IFramework fw)
    {
        if (heldHideKeys.Count == 0) return;
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) return;
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) return;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    if (!heldHideKeys.Contains(inst->Id.InstanceKey)) continue;
                    SetAllSlotsVisible(inst, false);
                }
            }
        }
        catch { }
    }

    // Called by /hms stop teardown: disarm the per-frame hold + clear the set (visibility restore rides the existing
    // hiddenInstanceKeys restore sweep).
    public void ClearHeldHide()
    {
        heldHideKeys.Clear();
        if (heldHideArmed) { heldHideArmed = false; framework.Update -= PollHeldHide; }
    }

    // b49: idnear — the coordinate-guess KILLER. Two clean idhides (far dst02 rubble + near-origin e3ec_wood scaffold)
    // both landed at the flag level (0x4F→0x46, IsVisible=hid, held, single pointer) yet changed NOTHING on screen. But
    // both id sets were picked from the OFFLINE dump and only ASSUMED to be in the player's view — the last unverified
    // confound. This hides every BgPart whose RENDERED DrawObject is within <radius> horizontal units of the LIVE player
    // position (measured, not guessed). Decisive: if a bubble around the player visibly empties → IsVisible DOES de-render
    // 759 bg and we just need the right ids (build the /hms advance759 embed). If hundreds hide with the flags reading
    // `hid`, held every frame, and STILL zero visible change → IsVisible is categorically INERT for these streamed bg
    // parts and we pivot to §12.4 (streaming-radius / ForceUpdateAllStreaming / a different render gate). Restore via
    // /hms idshowall (session-independent) or /hms stop. hide=false = preview (count + nearest only, no mutate).
    public void IdNear(float radius, bool hide)
    {
        var lp = objectTable.LocalPlayer;
        if (lp == null) { log.Information("[HMSync] [IDNEAR] no LocalPlayer - are you in the zone?"); return; }
        var pp = lp.Position;
        float r2 = radius * radius;
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [IDNEAR] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [IDNEAR] no ActiveLayout/GlobalLayout"); return; }

            int inRange = 0, acted = 0, logged = 0, sgHit = 0, sgChildren = 0;
            float nearest = float.MaxValue; string nearestPath = ""; uint nearestKey = 0;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    var it = inst->Id.Type;
                    bool isBg = it == InstanceType.BgPart;
                    bool isSg = it == InstanceType.SharedGroup;
                    if (!isBg && !isSg) continue;

                    // b50: range by the RENDERED DrawObject for BgParts, by the SharedGroup Transform for SGs (the
                    // wooden scaffolds survived the BgPart-only sweep because they're SharedGroups — cover them too).
                    Vector3 p;
                    if (isBg)
                    {
                        GetAllSlots(inst, out var fld, out _, out var g23, out var g24);
                        var d = fld != null ? fld : (g23 != null ? g23 : g24);
                        if (d == null) continue;
                        p = new Vector3(d->Position.X, d->Position.Y, d->Position.Z);
                    }
                    else
                    {
                        var sgt = ((SharedGroupLayoutInstance*)inst)->Transform.Translation;
                        p = new Vector3(sgt.X, sgt.Y, sgt.Z);
                    }
                    float dx = p.X - pp.X, dz = p.Z - pp.Z;
                    float dd2 = dx * dx + dz * dz;
                    if (dd2 > r2) continue;
                    inRange++;
                    float dist = MathF.Sqrt(dd2);
                    if (dist < nearest)
                    {
                        nearest = dist; nearestKey = inst->Id.InstanceKey;
                        try { var np = inst->GetPrimaryPath(); nearestPath = np.HasValue ? (np.ToString() ?? "") : ""; } catch { }
                    }
                    if (hide)
                    {
                        uint key = inst->Id.InstanceKey;
                        hiddenLayoutInstances.Add((nint)inst);
                        hiddenInstanceKeys.Add(key);
                        if (isBg)
                        {
                            acted += SetAllSlotsVisible(inst, false);
                            heldHideKeys.Add(key);   // per-frame hold (SG children are one-shot; see below)
                        }
                        else
                        {
                            // SharedGroup: recursively hide its child BgParts via the proven IsVisible walk (adds each
                            // child key to hiddenInstanceKeys for restore). One-shot — enough to SEE the effect while
                            // stationary; movement-driven re-stream is out of scope for this look-test.
                            sgHit++;
                            sgChildren += HideSharedGroupChildren((SharedGroupLayoutInstance*)inst, 0);
                        }
                    }
                    if (logged < 12)
                    {
                        logged++;
                        string path = "";
                        try { var pp2 = inst->GetPrimaryPath(); path = pp2.HasValue ? (pp2.ToString() ?? "") : ""; } catch { }
                        log.Information("[HMSync] [IDNEAR] " + (hide ? "hid" : "near") + " " + it + " id=" + inst->Id.InstanceKey
                            + " dist=" + dist.ToString("F1") + "u at=(" + p.X.ToString("F1") + "," + p.Z.ToString("F1") + ") path=" + path);
                    }
                }
            }
            if (hide && heldHideKeys.Count > 0 && !heldHideArmed) { heldHideArmed = true; framework.Update += PollHeldHide; }
            log.Information("[HMSync] [IDNEAR] player=(" + pp.X.ToString("F1") + "," + pp.Z.ToString("F1") + ") radius=" + radius
                + " inRange=" + inRange + " (SG=" + sgHit + " sgChildren=" + sgChildren + ")"
                + (hide ? " slotsActed=" + acted + " heldSet=" + heldHideKeys.Count + " perFrameHold=" + (heldHideArmed ? "ON" : "off")
                        + " → if the world around you did NOT change, IsVisible is inert for 759 bg (§12.4 pivot)."
                        : " (PREVIEW - nothing hidden)")
                + " nearest=" + (nearestKey != 0 ? nearestKey + "@" + nearest.ToString("F1") + "u " + nearestPath : "none"));
        }
        catch (Exception ex) { log.Error("[HMSync] [IDNEAR] threw: " + ex.Message); }
    }

    // Session-independent restore for the idnear/idhide research levers: disarm the per-frame hold and re-show every
    // instance we hid (reuses the crash-safe RestoreHiddenObjects sweep, which clears the tracking sets).
    public void ShowAllHidden()
    {
        RestoreHiddenObjects();   // calls ClearHeldHide() first, then re-shows by hiddenInstanceKeys
        log.Information("[HMSync] [IDNEAR] restore sweep run (per-frame hold disarmed, hidden instances re-shown).");
    }

    // Splits on comma/space/semicolon/tab AND newlines (\n \r) — the newline case matters for embedded one-id-per-line
    // files; omitting it (pre-b55 bug) made ParseIdCsv treat a whole newline-delimited file as one unparseable token and
    // silently return ~0 ids, which is why the b51 subtractive advance appeared to "do nothing" (it never loaded its set).
    private static HashSet<uint> ParseIdCsv(string csv)
    {
        var set = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var tok in csv.Split(new[] { ',', ' ', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(tok.Trim(), out var v)) set.Add(v);
        return set;
    }

    // NB-23 (2026-08-06): read-only SHARED-GROUP TIMELINE dumper for the geometry-advance PoC. The layerhide test proved
    // 759's reconstruction is NOT hide-able by asset substring: every op=None bg layer is "always on", so a virtual load
    // (no server) draws ALL stages at once (b44: hidden=0 everywhere) - and the stage label lives in the LGB layer NAME,
    // not the asset path (so `dst` matched 0). The user's observation - the enclave rebuilds "building by building,
    // sequentially" as the quest advances - points at the SAME construct 1189 uses: each building is a SharedGroup
    // (sgbg_*.sgb) whose progressive state is a TIMELINE (step0..stepN), driven server-side by EventObjAnimation /
    // ActorControl (the _timelineIndices lookup @0x14C). In a virtual load those SGBs sit at their default (step0) state.
    // So the lever is DRIVE-THE-SGB-TIMELINE, identical for 759 and 1189 - not layer hiding. Before building the driver
    // we must read the actual timeline vocabulary. This dumps, per SharedGroup (optionally filtered by asset substring):
    //   • asset path, PrefabFlags1/2, child-instance count
    //   • every TimeLineLayoutInstance: PathCrc resolved to a NAME via LayoutManager.CrcToPath (this is where step0/step1
    //     surface), plus the timeline's own GetPrimaryPath
    //   • the raw _timelineIndices[16] bytes @0x14C (the active-state lookup the ActorControl packet writes)
    //   • the TimelineObject.SubIdToInstance count / ActionController AnimationType
    // PURE READ. Usage: `/hms sgdump` (all SGBs) or `/hms sgdump <substr>` (e.g. `mamu0`, or a 759 building code). Grep [SGDUMP].
    public void SharedGroupDump(string substr)
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [SGDUMP] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [SGDUMP] no ActiveLayout/GlobalLayout"); return; }

            bool filt = !string.IsNullOrWhiteSpace(substr);
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.SharedGroup, out var mapPtrPtr) || mapPtrPtr == null)
            { log.Information("[HMSync] [SGDUMP] no SharedGroup instances in this zone"); return; }
            var innerMap = mapPtrPtr->Value;
            if (innerMap == null) { log.Information("[HMSync] [SGDUMP] SharedGroup map NULL"); return; }

            int total = 0, shown = 0, withTimelines = 0;
            log.Information("[HMSync] [SGDUMP] terr=" + CurrentLoadedZone + (filt ? " filter='" + substr + "'" : " (all SharedGroups)"));
            foreach (var kv in *innerMap)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                total++;
                string path = "";
                try { var pp = inst->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
                if (filt && (string.IsNullOrEmpty(path) || !path.Contains(substr, StringComparison.OrdinalIgnoreCase))) continue;

                var sg = (SharedGroupLayoutInstance*)inst;

                // Timeline vector (the step-state list).
                int tlCount = 0; var tlNames = new List<string>();
                try
                {
                    var vec = sg->TimeLineContainer.Instances;
                    tlCount = (int)vec.LongCount;
                    for (long i = 0; i < vec.LongCount && i < 32; i++)
                    {
                        var tl = vec[i].Value;
                        if (tl == null) { tlNames.Add("(null)"); continue; }
                        uint crc = tl->PathCrc;
                        string nm = ResolveLayoutCrc(layout, crc);
                        if (string.IsNullOrEmpty(nm))
                        {
                            try { var tp = ((ILayoutInstance*)tl)->GetPrimaryPath(); if (tp.HasValue) nm = tp.ToString() ?? ""; } catch { }
                        }
                        tlNames.Add((string.IsNullOrEmpty(nm) ? "crc" : nm) + "#" + crc.ToString("X8"));
                    }
                }
                catch { }
                if (tlCount > 0) withTimelines++;

                // Active-state lookup table (_timelineIndices[16] @0x14C) + prefab flags + child count.
                string idxBytes = "";
                try
                {
                    byte* ti = (byte*)sg + 0x14C;
                    var sb2 = new System.Text.StringBuilder();
                    for (int i = 0; i < 16; i++) { if (i > 0) sb2.Append(' '); sb2.Append(ti[i].ToString("X2")); }
                    idxBytes = sb2.ToString();
                }
                catch { }
                uint pf1 = 0, pf2 = 0; int children = 0, subIds = 0; uint ac1 = 0, ac2 = 0;
                try { pf1 = sg->PrefabFlags1; pf2 = sg->PrefabFlags2; } catch { }
                try { children = (int)sg->Instances.Instances.LongCount; } catch { }
                try { if (sg->TimelineObject != null) subIds = (int)sg->TimelineObject->SubIdToInstance.Count; } catch { }
                try { if (sg->ActionController1 != null) ac1 = sg->ActionController1->AnimationType; } catch { }
                try { if (sg->ActionController2 != null) ac2 = sg->ActionController2->AnimationType; } catch { }

                shown++;
                string stem = path;
                int sl = path.LastIndexOf('/'); if (sl >= 0 && sl < path.Length - 1) stem = path.Substring(sl + 1);
                log.Information("[HMSync] [SGDUMP] sg=" + stem + " key=" + inst->Id.InstanceKey
                    + " timelines=" + tlCount + " children=" + children + " subIds=" + subIds
                    + " prefabFlags=" + pf1.ToString("X") + "/" + pf2.ToString("X")
                    + " ac=" + ac1 + "/" + ac2 + " @" + ((nint)sg).ToString("X"));
                if (tlNames.Count > 0)
                    log.Information("[HMSync] [SGDUMP]     steps=[" + string.Join(", ", tlNames) + "]");
                log.Information("[HMSync] [SGDUMP]     _timelineIndices=" + idxBytes + "  path=" + path);
            }

            log.Information("[HMSync] [SGDUMP] done: " + total + " SharedGroups total, " + shown + " reported, "
                + withTimelines + " have timelines. Timelines with step-like names ⇒ SGB-state advance lever (drive the "
                + "step); none/1 ⇒ this building isn't SGB-state driven. Cross-ref against territory-keys/NOTES.md.");
        }
        catch (Exception ex) { log.Error("[HMSync] [SGDUMP] threw: " + ex.Message); }
    }

    // NB-23: resolve an LGB path CRC to its string via LayoutManager.CrcToPath (@0x278). RefCountedString stores the
    // NumRefs int at 0x00 then the null-terminated path bytes at 0x04 (FixedSizeArray260 isString). Read-only.
    private static string ResolveLayoutCrc(LayoutManager* layout, uint crc)
    {
        try
        {
            if (layout->CrcToPath.TryGetValuePointer(crc, out var rp) && rp != null)
            {
                var rcs = rp->Value;
                if (rcs != null)
                {
                    byte* p = (byte*)rcs + 4;
                    int n = 0; while (n < 256 && p[n] != 0) n++;
                    return System.Text.Encoding.UTF8.GetString(p, n);
                }
            }
        }
        catch { }
        return "";
    }

    // ── 759 SGB-STATE lever (consult reply 2026-08-05) ─────────────────────────────────────────────────────────
    // The terrain theory is DEAD: 759's terrain.tera is a single 160-byte ground mesh, no stage variants — a re-stream
    // can't select a stage. The ruined↔finished difference lives INSIDE eight master SharedGroups as internal named
    // step states (residence_step0..2, smith_step0..5, tower_step0..3, ...), exactly like Mamook's mamu0. That's why
    // adv759diff showed ~0 change: a state swap inside an SGB spawns/despawns/reflags NOTHING at the container level.
    // The eight facilities (InstanceKey → label/SGB stem), from the offline extract of the e3ec LGBs:
    private static readonly (uint key, string label)[] Adv759Facilities =
    {
        (7380934u, "minka/min00 residence_step0..2"),
        (7385932u, "works/wor0 smith_step0..5"),
        (7387118u, "paper/pap00 craft_step0..5"),
        (7626587u, "jikei/jik00 security_step0..3"),
        (7635836u, "terakoya/ter00 school_step0..4"),
        (7262591u, "tower/tew00 tower_step0..3"),
        (7630164u, "teien/tei00 park_step0..2"),
        (7509016u, "kozakura/koz00 kozakura_step0..2"),
    };

    // b62: per-facility FINISHED step index — the highest reconstruction stage each master SharedGroup carries (from the
    // step0..N naming the consult decoded). This is the target we drive PlayTimeline() to at load so the enclave renders
    // fully rebuilt with NO hide→show cycle. Clamped at runtime to the facility's actual timeline count + IsTimelineIndexValid,
    // so a wrong number here can only under-drive, never fault. Same pattern will crack Mamook 1189 (step0..5).
    private static readonly Dictionary<uint, int> Adv759MaxStep = new()
    {
        { 7380934u, 2 }, // minka    residence_step0..2
        { 7385932u, 5 }, // works    smith_step0..5
        { 7387118u, 5 }, // paper    craft_step0..5
        { 7626587u, 3 }, // jikei    security_step0..3
        { 7635836u, 4 }, // terakoya school_step0..4
        { 7262591u, 3 }, // tower    tower_step0..3
        { 7630164u, 2 }, // teien    park_step0..2
        { 7509016u, 2 }, // kozakura kozakura_step0..2
    };

    // hmst adv759sgstate — READ-ONLY dump of the internal timeline/step state of the eight reconstruction SharedGroups.
    // Run it on a RAW 759, then run the proven idnear500→idshowall cycle, then run it AGAIN and compare: if
    // PlayingTimelineIndex (or which timeline IsTimelinePlaying) moves step0→stepN across the cycle, the mechanism is
    // closed and the next step is to drive that state at first load (PlayTimeline) instead of the jarring cycle. Uses
    // CS accessors (PlayingTimelineIndex @0x16D, TimelineIndices[16] @0x154, TimeLineContainer @0xD0) — NOT the stale
    // 0x14C literal SGDUMP hardcodes. Pure read; the only native calls are IsTimelinePlaying (a bool query), guarded.
    public void Adv759SgState()
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [ADV759SG] LayoutWorld NULL"); return; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [ADV759SG] no ActiveLayout/GlobalLayout"); return; }

            // Build key→SharedGroup* index by walking the SharedGroup type map once (matches SGDUMP traversal).
            var found = new Dictionary<uint, nint>();
            if (layout->InstancesByType.TryGetValuePointer(InstanceType.SharedGroup, out var mapPtrPtr) && mapPtrPtr != null)
            {
                var innerMap = mapPtrPtr->Value;
                if (innerMap != null)
                    foreach (var kv in *innerMap)
                    {
                        var inst = kv.Item2.Value;
                        if (inst == null) continue;
                        uint k = inst->Id.InstanceKey;
                        for (int i = 0; i < Adv759Facilities.Length; i++)
                            if (Adv759Facilities[i].key == k && !found.ContainsKey(k)) found[k] = (nint)inst;
                    }
            }

            log.Information("[HMSync] [ADV759SG] terr=" + CurrentLoadedZone + " — SGB internal step state of the 8 facilities "
                + "(run RAW, then cycle, then again; watch PlayingTimelineIndex / *PLAYING* move). found="
                + found.Count + "/" + Adv759Facilities.Length);

            foreach (var (key, label) in Adv759Facilities)
            {
                if (!found.TryGetValue(key, out var addr))
                {
                    log.Information("[HMSync] [ADV759SG] key=" + key + " (" + label + ") — NOT PRESENT in this layout");
                    continue;
                }
                var sg = (SharedGroupLayoutInstance*)addr;

                byte playing = 0; string idxBytes = "";
                try { playing = sg->PlayingTimelineIndex; } catch { }
                try
                {
                    var ti = sg->TimelineIndices;   // CS Span<byte>, len 16 @0x154
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < ti.Length && i < 16; i++) { if (i > 0) sb.Append(' '); sb.Append(ti[i].ToString("X2")); }
                    idxBytes = sb.ToString();
                }
                catch { }
                uint pf1 = 0, pf2 = 0;
                try { pf1 = sg->PrefabFlags1; pf2 = sg->PrefabFlags2; } catch { }

                // Timeline (step) list + which index reports PLAYING.
                var steps = new List<string>();
                try
                {
                    var vec = sg->TimeLineContainer.Instances;
                    for (long i = 0; i < vec.LongCount && i < 32; i++)
                    {
                        var tl = vec[i].Value;
                        string nm = "(null)";
                        if (tl != null)
                        {
                            uint crc = tl->PathCrc;
                            nm = ResolveLayoutCrc(layout, crc);
                            if (string.IsNullOrEmpty(nm))
                            {
                                try { var tp = ((ILayoutInstance*)tl)->GetPrimaryPath(); if (tp.HasValue) nm = tp.ToString() ?? ""; } catch { }
                            }
                            if (string.IsNullOrEmpty(nm)) nm = "crc#" + crc.ToString("X8");
                        }
                        bool isPlaying = false;
                        try { isPlaying = sg->IsTimelinePlaying((uint)i); } catch { }
                        int slash = nm.LastIndexOf('/'); if (slash >= 0 && slash < nm.Length - 1) nm = nm.Substring(slash + 1);
                        steps.Add("[" + i + "]" + nm + (isPlaying ? " *PLAYING*" : ""));
                    }
                }
                catch { }

                log.Information("[HMSync] [ADV759SG] key=" + key + " (" + label + ") PlayingTimelineIndex=" + playing
                    + " prefabFlags=" + pf1.ToString("X") + "/" + pf2.ToString("X") + " _timelineIndices=[" + idxBytes + "] @" + addr.ToString("X"));
                if (steps.Count > 0)
                    log.Information("[HMSync] [ADV759SG]     steps=" + string.Join("  ", steps));
            }
            // ── Per-facility CHILD diff (two-call) ──────────────────────────────────────────────────────────────
            // b60 proved the container fields are inert across the cycle, yet the enclave visibly goes ruined→finished.
            // So the step swap lives in the CHILD instances. Snapshot each facility's child meshes (key→path,visUnion);
            // first call = baseline, second call = diff. Reveals WHICH child meshes turn on / swap model = the real state.
            var childNow = new Dictionary<uint, Dictionary<uint, (string path, bool vis)>>();
            foreach (var (key, _) in Adv759Facilities)
            {
                var d = new Dictionary<uint, (string, bool)>();
                if (found.TryGetValue(key, out var addr)) CollectFacilityChildren((SharedGroupLayoutInstance*)addr, d, 0);
                childNow[key] = d;
            }

            if (adv759ChildSnap == null)
            {
                adv759ChildSnap = childNow;
                int tot = 0; foreach (var kv in childNow) tot += kv.Value.Count;
                log.Information("[HMSync] [ADV759SG] CHILD BASELINE captured: " + tot + " child instances across the 8 facilities. "
                    + "NOW run `hmst idnear 500` → `hmst idshowall`, CONFIRM the enclave looks FINISHED, then `hmst adv759sgstate` again to DIFF.");
                return;
            }

            log.Information("[HMSync] [ADV759SG] CHILD DIFF (baseline → now) — which child meshes changed across the cycle:");
            int gVisOn = 0, gPathChg = 0, gSpawn = 0, gDespawn = 0;
            foreach (var (key, label) in Adv759Facilities)
            {
                var a = adv759ChildSnap.TryGetValue(key, out var av) ? av : new Dictionary<uint, (string path, bool vis)>();
                var b = childNow.TryGetValue(key, out var bv) ? bv : new Dictionary<uint, (string path, bool vis)>();
                int visOn = 0, visOff = 0, pathChg = 0, spawned = 0, despawned = 0;
                var samples = new List<string>();
                foreach (var kv in b)
                {
                    if (!a.TryGetValue(kv.Key, out var old)) { spawned++; if (samples.Count < 5) samples.Add("SPAWN[" + kv.Key + "]" + Stem(kv.Value.path)); continue; }
                    if (!old.vis && kv.Value.vis) { visOn++; if (samples.Count < 5) samples.Add("VIS+" + Stem(kv.Value.path)); }
                    else if (old.vis && !kv.Value.vis) visOff++;
                    if (old.path != kv.Value.path) { pathChg++; if (samples.Count < 5) samples.Add("PATH " + Stem(old.path) + "→" + Stem(kv.Value.path)); }
                }
                foreach (var kv in a) if (!b.ContainsKey(kv.Key)) despawned++;
                gVisOn += visOn; gPathChg += pathChg; gSpawn += spawned; gDespawn += despawned;
                log.Information("[HMSync] [ADV759SG]   " + label + ": children a=" + a.Count + " b=" + b.Count
                    + " | VIS-ON=" + visOn + " VIS-OFF=" + visOff + " PATH-CHG=" + pathChg + " SPAWNED=" + spawned + " DESPAWNED=" + despawned
                    + (samples.Count > 0 ? "  e.g. " + string.Join(", ", samples) : ""));
            }
            adv759ChildSnap = null;
            log.Information("[HMSync] [ADV759SG] CHILD DIFF totals: VIS-ON=" + gVisOn + " PATH-CHG=" + gPathChg + " SPAWNED=" + gSpawn
                + " DESPAWNED=" + gDespawn + ". Non-zero here = the step lever is child visibility/model-swap; drive THOSE at load.");
            log.Information("[HMSync] [ADV759SG] done. If PlayingTimelineIndex / *PLAYING* differs RAW vs post-cycle, drive PlayTimeline(maxStep) at load = clean native lever (same path for Mamook 1189).");
        }
        catch (Exception ex) { log.Error("[HMSync] [ADV759SG] threw: " + ex.Message); }
    }

    // b63: THE CLEAN LOAD-TIME GEOMETRY ADVANCE for 759 — replaces the jarring idnear→idshowall cycle. b62's PlayTimeline
    // approach is DEAD: it returned OK on all 8 but PlayingTimelineIndex stayed 0 and nothing rendered — the SGB timeline is
    // the EventObjAnimation channel (doors/flags), NOT the mesh draw-gate. Reading the cycle's own code proved the real
    // mechanism: hide and restore are pure DrawObject->IsVisible flips on ALREADY-RESIDENT instances (no streaming). The
    // finished buildings are loaded in the raw serverless scene but left UNDRAWN; the cycle's restore re-shows every child
    // key it recorded during hide (RestoreHiddenObjects → SetAllSlotsVisible(true)), and because the hide records finished-
    // step children that had no gfx to hide, the restore RATCHETS them on. This driver isolates that ratchet: force-show
    // every child of the 8 facility SharedGroups directly, with NO hide beat. Must run on the framework thread (touches
    // layout/render state); command handlers and RunOnTick callbacks already are. Returns the number of facilities touched.
    // If this alone rebuilds the enclave, child visibility is the clean lever; if it yields SOUP, the hide beat is required
    // (the resolver must evict step0 first) and we drive the finished-child SUBSET instead. Same path targets Mamook 1189.
    public int DriveAdv759(string tag = "ADV759DRIVE")
    {
        int driven = 0;
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [" + tag + "] LayoutWorld NULL"); return 0; }
            var layout = lw->ActiveLayout != null ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [" + tag + "] no ActiveLayout/GlobalLayout"); return 0; }

            var found = new Dictionary<uint, nint>();
            if (layout->InstancesByType.TryGetValuePointer(InstanceType.SharedGroup, out var mapPtrPtr) && mapPtrPtr != null)
            {
                var innerMap = mapPtrPtr->Value;
                if (innerMap != null)
                    foreach (var kv in *innerMap)
                    {
                        var inst = kv.Item2.Value;
                        if (inst == null) continue;
                        uint k = inst->Id.InstanceKey;
                        if (Adv759MaxStep.ContainsKey(k) && !found.ContainsKey(k)) found[k] = (nint)inst;
                    }
            }

            log.Information("[HMSync] [" + tag + "] terr=" + CurrentLoadedZone + " — layout-wide force-show reconstruction geometry"
                + " (DrawObject->IsVisible ratchet, no hide beat, no PlayTimeline). named-facilities present=" + found.Count + "/" + Adv759MaxStep.Count);

            // ── Pass 1: the 8 named 759 facilities (detailed per-facility attribution; skipped on zones without them) ──
            int totalChildren = 0, totalSlotsOn = 0;
            var doneKeys = new HashSet<uint>();
            if (found.Count > 0)
            {
                foreach (var (key, label) in Adv759Facilities)
                {
                    if (!found.TryGetValue(key, out var addr)) continue;
                    var sg = (SharedGroupLayoutInstance*)addr;
                    doneKeys.Add(key);

                    totalSlotsOn += SetAllSlotsVisible((ILayoutInstance*)sg, true);
                    int slotsOn = 0;
                    int walked = ShowSharedGroupChildren(sg, 0, ref slotsOn);
                    totalChildren += walked; totalSlotsOn += slotsOn;
                    if (walked > 0) driven++;

                    log.Information("[HMSync] [" + tag + "] key=" + key + " (" + label + ") children=" + walked
                        + " slotsShown=" + slotsOn);
                }
            }
            else
            {
                log.Information("[HMSync] [" + tag + "] no named facilities in this zone — universal layout-wide sweep only (Pass 2).");
            }

            // ── Pass 2: layout-wide sweep (faithful clean replica of the cycle's in-radius show) ───────────────────
            // The 8 facilities left "a few areas" ruined because finished geometry also lives OUTSIDE their child trees
            // (other SharedGroups; top-level BgParts). The cycle catches those (idnear hides+shows EVERYTHING in radius);
            // our scoped pass didn't. Force-showing resident meshes can only ADD finished geometry (rubble is already
            // drawn), so this is safe — it can't make the scene worse, only fill the gaps. Show every OTHER SharedGroup's
            // children and reveal every currently-HIDDEN top-level BgPart. Counts are broken out so we can see which class
            // filled the gap.
            int otherSg = 0, otherSgChildren = 0, bgRevealed = 0, bgSlots = 0;
            foreach (var lkv in layout->Layers)
            {
                var lm = lkv.Item2.Value;
                if (lm == null) continue;
                foreach (var ikv in lm->Instances)
                {
                    var inst = ikv.Item2.Value;
                    if (inst == null) continue;
                    var it = inst->Id.Type;
                    if (it == InstanceType.SharedGroup)
                    {
                        if (doneKeys.Contains(inst->Id.InstanceKey)) continue;
                        otherSg++;
                        totalSlotsOn += SetAllSlotsVisible(inst, true);
                        int slotsOn = 0;
                        otherSgChildren += ShowSharedGroupChildren((SharedGroupLayoutInstance*)inst, 0, ref slotsOn);
                        totalSlotsOn += slotsOn;
                    }
                    else if (it == InstanceType.BgPart)
                    {
                        // Only count as "revealed" if a slot was actually hidden (rubble that's already drawn = no-op).
                        GetAllSlots(inst, out var fld, out _, out var g23, out var g24);
                        bool wasHidden = (fld != null && !fld->IsVisible) || (g23 != null && !g23->IsVisible) || (g24 != null && !g24->IsVisible);
                        int n = SetAllSlotsVisible(inst, true);
                        totalSlotsOn += n;
                        if (wasHidden) { bgRevealed++; bgSlots += n; }
                    }
                }
            }

            log.Information("[HMSync] [" + tag + "] done: facilities " + driven + "/" + found.Count + " (children=" + totalChildren
                + "); layout-wide swept otherSGs=" + otherSg + " (children=" + otherSgChildren + "), BgParts revealed=" + bgRevealed
                + " (" + bgSlots + " slots). totalSlotsShown=" + totalSlotsOn + ". If gaps remain, the missing finished geometry "
                + "is not resident-hidden in this layout (needs a stream trigger) — compare which class (otherSG vs BgPart) was non-zero.");
        }
        catch (Exception ex) { log.Error("[HMSync] [" + tag + "] threw: " + ex.Message); }
        return driven;
    }

    // Last path segment (file stem) for compact diff logging.
    private static string Stem(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(nopath)";
        int s = path.LastIndexOf('/');
        return s >= 0 && s < path.Length - 1 ? path.Substring(s + 1) : path;
    }

    // Walk a facility SharedGroup's child instances (recursive, ≤4 deep like the hide path), recording each child's
    // InstanceKey → (model path, IsVisible union across all 3 render slots). Read-only.
    private void CollectFacilityChildren(SharedGroupLayoutInstance* sg, Dictionary<uint, (string path, bool vis)> into, int depth)
    {
        if (sg == null || depth >= 4) return;
        try
        {
            var vec = sg->Instances.Instances;
            for (long i = 0; i < vec.LongCount; i++)
            {
                var child = vec[i].Value;
                if (child == null) continue;
                var ci = child->Instance;
                if (ci == null) continue;
                uint k = ci->Id.InstanceKey;
                string path = "";
                try { var pp = ci->GetPrimaryPath(); if (pp.HasValue) path = pp.ToString() ?? ""; } catch { }
                GetAllSlots(ci, out var fld, out _, out var g23, out var g24);
                bool vis = (fld != null && fld->IsVisible) || (g23 != null && g23->IsVisible) || (g24 != null && g24->IsVisible);
                into[k] = (path, vis);
                if (ci->Id.Type == InstanceType.SharedGroup)
                    CollectFacilityChildren((SharedGroupLayoutInstance*)ci, into, depth + 1);
            }
        }
        catch { }
    }

    // v0.7.428 - the single hide primitive: flips IsVisible=false and tracks whether this was a
    // REAL catch (flag was true) vs an idempotent re-set. On quiet passes, a real catch is exactly
    // the evidence we've been hunting: a renderable that detection had just declared clean. Emit a
    // capped [CADENCE-CATCH] line naming the instance type, which slot the graphics came from
    // (F=BgPart field, 23/24=vfuncs), and its raw path - the mechanism answer self-collects.
    private void HideGfx(DrawObject* gfx, ILayoutInstance* inst, string site)
    {
        if (gfx == null) return;
        if (gfx->IsVisible)
        {
            newlyHiddenThisPass++;
            if (quietDeDraw && catchLinesThisPass < 6 && inst != null)
            {
                catchLinesThisPass++;
                string slot = "?";
                try
                {
                    if (inst->Id.Type == InstanceType.BgPart
                        && (DrawObject*)((BgPartsLayoutInstance*)inst)->GraphicsObject == gfx) slot = "F";
                    else if ((DrawObject*)inst->GetGraphics() == gfx) slot = "23";
                    else if ((DrawObject*)inst->GetGraphics2() == gfx) slot = "24";
                }
                catch { }
                string path = "(nopath)";
                try
                {
                    var pp = inst->GetPrimaryPath();
                    if (pp.HasValue)
                    {
                        var sp = pp.AsSpan();
                        path = System.Text.Encoding.UTF8.GetString(sp.Slice(0, Math.Min(sp.Length, 60)));
                    }
                }
                catch { }
                DiagLog("[HMSync] [CADENCE-CATCH] " + site + " t=" + inst->Id.Type + " slot=" + slot
                    + " path=" + path + " @" + ((nint)inst).ToString("X"));
            }
        }
        gfx->IsVisible = false;
    }

    // b63: the SHOW mirror of HideSharedGroupChildren — force every child of a facility SharedGroup to DRAWN. This is the
    // "spawn" half of the proven idnear→idshowall cycle, isolated: the cycle's restore (RestoreHiddenObjects) re-shows every
    // recorded child key via SetAllSlotsVisible(true), and because the hide records finished-step children that the raw
    // serverless load left resident-but-undrawn, the restore ratchets them ON. This walk does exactly that ratchet, scoped
    // to one facility, WITHOUT the disruptive hide-everything beat. Counts: total children walked, slots flipped on. If
    // force-showing resident children alone rebuilds the enclave, the clean load-time lever is child visibility (no cycle,
    // no PlayTimeline). Mirrors the hide traversal exactly (same vector, same ≤4 recursion) so it reaches the real children.
    private int ShowSharedGroupChildren(SharedGroupLayoutInstance* sg, int depth, ref int slotsOn)
    {
        if (sg == null || depth >= 4) return 0;
        int walked = 0;
        try
        {
            var vec = sg->Instances.Instances;
            for (long i = 0; i < vec.LongCount; i++)
            {
                var child = vec[i].Value;
                if (child == null) continue;
                var childInst = child->Instance;
                if (childInst == null) continue;
                walked++;

                var gfx = GetEffectiveGraphics(childInst);
                if (gfx != null) { gfx->IsVisible = true; slotsOn++; }
                slotsOn += SetAllSlotsVisible(childInst, true);

                if (childInst->Id.Type == InstanceType.SharedGroup)
                    walked += ShowSharedGroupChildren((SharedGroupLayoutInstance*)childInst, depth + 1, ref slotsOn);
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] SharedGroup child-show skipped at depth " + depth + ": " + ex.Message); }
        return walked;
    }

    private int HideSharedGroupChildren(SharedGroupLayoutInstance* sg, int depth)
    {
        if (sg == null || depth >= 4) return 0;
        int hidden = 0;
        try
        {
            var vec = sg->Instances.Instances; // StdVector<Pointer<ChildNodeInstance>>
            for (long i = 0; i < vec.LongCount; i++)
            {
                var child = vec[i].Value;
                if (child == null) continue;
                var childInst = child->Instance;
                if (childInst == null) continue;

                // Hide this child's graphics (the furniture mesh leaf). S177: use the full
                // GetEffectiveGraphics (vfuncs → BgParts GraphicsObject field, validity-gated) - the
                // partition/pillar leaves are BgPart instances whose vfuncs return null and whose
                // renderable is in the GraphicsObject field (the whole S173 finding). childInst is
                // fresh from the live SharedGroup vector this frame (safe).
                var gfx = GetEffectiveGraphics(childInst);
                if (gfx != null)
                {
                    HideGfx(gfx, childInst, "SGC");   // v0.7.428
                    hiddenLayoutInstances.Add((nint)childInst);
                    hidden++;
                    if (childInst->Id.Type == InstanceType.BgPart)
                    {
                        var cfld = ((BgPartsLayoutInstance*)childInst)->GraphicsObject;
                        string cpos = cfld != null ? "(" + cfld->Position.X.ToString("F2") + "," + cfld->Position.Z.ToString("F2") + ")" : "(?)";
                        gfxResolveDiag.Add("SGC" + cpos + "hid@" + (nint)childInst + ">sg" + (nint)cfld);
                    }
                }
                // Track its key so the collider pass disables its physics too.
                hiddenInstanceKeys.Add(childInst->Id.InstanceKey);

                // Recurse if the child is itself a SharedGroup (nested prefab).
                if (childInst->Id.Type == InstanceType.SharedGroup)
                    hidden += HideSharedGroupChildren((SharedGroupLayoutInstance*)childInst, depth + 1);
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] SharedGroup child-hide skipped at depth " + depth + ": " + ex.Message); }
        return hidden;
    }


    private static DrawObject* GetEffectiveGraphics(ILayoutInstance* inst)
    {
        if (inst == null) return null;
        // S182: for BgPart, PREFER the concrete GraphicsObject FIELD over GetGraphics() (vfunc 23).
        // The tree dump proved there's exactly ONE BgPart per furniture position and we hide it -
        // yet it renders and Meddle reads it Visible. Meddle reads bgPart->GraphicsObject (the
        // FIELD); we were writing to GetGraphics()'s RETURN (vf23 tag in the diag). On field-like
        // maps these DIVERGE - vfunc returns one object, the field holds the one that actually
        // renders. So write to the field for BgPart; fall back to the vfuncs only for non-BgPart.
        if (inst->Id.Type == InstanceType.BgPart)
        {
            var bg = (BgPartsLayoutInstance*)inst;
            var go = bg->GraphicsObject;
            if (go != null)
            {
                if (go->ModelResourceHandle == null) return null;
                if (go->ModelResourceHandle->LoadState < 7) return null;
                return (DrawObject*)go;
            }
        }
        var gfx = inst->GetGraphics();
        if (gfx == null) gfx = inst->GetGraphics2();
        if (gfx != null) return (DrawObject*)gfx;
        return null;
    }

    private void DeDrawHousingFurniture()
    {
        // S153: list clears MOVED to ArmDeferredDeDraw (once per load), NOT here. With S151's
        // re-fire-on-late-wave, DeDrawHousingFurniture runs MULTIPLE times per load (initial +
        // re-fires). Clearing per-pass would break the save-original invariant: a re-fire would
        // clear colliderSavedState/untargetedObjects, then re-save the ALREADY-MODIFIED values
        // (LayerMask=0, IsTargetable cleared) as "originals" → restore puts back the broken
        // values. So clears happen once per load (fresh state), and each de-draw pass is purely
        // ADDITIVE: save-original-only-if-not-already-tracked (colliders + arrows), idempotent
        // visibility re-set (meshes). Restore (on stop/leave) clears everything.
        newlyHiddenThisPass = 0;    // v0.7.428: per-pass real-catch counter (true→false flips only)
        catchLinesThisPass = 0;     // v0.7.428: per-pass [CADENCE-CATCH] line cap
        try
        {
            // v0.7.454: EDGE-TRIGGERED + non-quiet only. In the persistent phase this method runs every
            // scan tick while furniture is present (and the quiet safety-pass runs even when detection is
            // clean), so logging here every call floods research-mode logs (~2/s forever) and buries other
            // diagnostics. Log only on the RISING edge of a REAL (non-quiet) detection-driven fire; the
            // quiet cadence/safety passes never log. Latch resets when furniture goes absent (below).
            if (!quietDeDraw)
            {
                var lwDiag = LayoutWorld.Instance();
                if (lwDiag != null && lwDiag->GlobalLayout != null && !deDrawRunLogged)
                {
                    DiagLog("[HMSync] DeDraw running (furniture present, deferred-fire)");
                    deDrawRunLogged = true;
                }
            }
            // S128: hide furniture through the LAYOUT ENGINE, not GameObjects. Furniture
            // proved every furniture GameObject has DrawObject==null - the GameObject is
            // a logic shell; the actual rendered mesh is an ILayoutInstance owned by the
            // layout engine. Walk LayoutWorld->ActiveLayout->InstancesByType for the
            // SharedGroup (furniture) + BgPart (room box/fixtures) instances and
            // SetActive(false) - the engine's own API, so it isn't reasserted. Collider
            // is a SEPARATE toggle (SetColliderActive) left untouched here: no phantom
            // walls, no change to current collision behavior.
            var layoutWorld = LayoutWorld.Instance();
            if (layoutWorld == null) { log.Debug("[HMSync] Furniture: LayoutWorld null"); return; }

            // S161: walk BOTH GlobalLayout AND ActiveLayout. The TEARDOWN probe (S160) revealed
            // the host's APARTMENT furniture persists in ActiveLayout, which stays bound to the
            // real home territory (the log showed Active{HousingType=2 Terr=999=apartment} while
            // Global{Terr=<HMS map>}). The furniture is durable + position-anchored at apartment
            // (0,0) - it never tears down, it just renders wherever apt-origin lands in the new
            // map (usually underground/in terrain, but on 1012 it lands in the open → visible).
            // Our de-draw walked only GlobalLayout (correct for event-map furniture in S132), so
            // it never touched the apartment furniture in ActiveLayout → the 1012 leak. Walking
            // both layers catches it. The bgcommon/hou/ housing-path gate (and HavePrimary for
            // GlobalLayout's own furniture) keeps us from hiding apartment STRUCTURE/world geo -
            // we only hide furniture-class instances. De-dupe across layers is automatic: the
            // hidden-sets are HashSets keyed by instance, and re-hiding an already-hidden leaf is
            // idempotent.
            int hidden = 0;
            // S163: walk EVERY LayoutManager in LoadedLayouts - not just GlobalLayout/ActiveLayout.
            // Meddle's LayoutService enumerates layoutWorld->LoadedLayouts (all members) and parses
            // each one's Layers; that's how it sees the persistent apartment furniture. On a hop the
            // apartment's LayoutManager persists as its OWN entry in LoadedLayouts, distinct from the
            // displayed map's GlobalLayout/ActiveLayout - the furniture rides in THAT layout's layers,
            // which is why walking only Global+Active (S161/S162) hid extra instances (count jumped
            // 27→89) but left the apartment furniture visible: it's in the third layout we never
            // walked. Iterate them all. De-dup is automatic (HashSet-keyed hidden-sets; idempotent
            // re-hide). Still housing-path gated so we only touch furniture-class instances.
            var lwInst = LayoutWorld.Instance();
            var seen = new HashSet<nint>();
            housingIdCensus.Clear();   // S165 diag
            gfxResolveDiag.Clear();    // S176 diag
            if (lwInst != null)
            {
                // Named pointers first (in case they're not in LoadedLayouts).
                if (lwInst->GlobalLayout != null) { seen.Add((nint)lwInst->GlobalLayout); hidden += WalkLayoutAndHideFurniture(lwInst->GlobalLayout, "Global"); }
                if (lwInst->ActiveLayout != null && seen.Add((nint)lwInst->ActiveLayout)) hidden += WalkLayoutAndHideFurniture(lwInst->ActiveLayout, "Active");
                // S167: PrefetchLayout (0x030) - the layout the engine streams AHEAD for the next
                // zone. The orphan furniture "carries across / prefetches into the next map" - its
                // census key (4294639619) on 1011 was gone from 1012's GlobalLayout walk, i.e. it
                // detached from the walked layouts. PrefetchLayout is the un-walked 4th named
                // pointer and a strong candidate home. Walking it is a SAFE live enumeration (same
                // as Active) - NO stored-pointer deref (that was the S166 AV). If the orphan rides
                // here, this hides it crash-free.
                if (lwInst->PrefetchLayout != null && seen.Add((nint)lwInst->PrefetchLayout)) hidden += WalkLayoutAndHideFurniture(lwInst->PrefetchLayout, "Prefetch");
                try
                {
                    foreach (var lkv in lwInst->LoadedLayouts)
                    {
                        var lm = lkv.Item2.Value;
                        if (lm == null) continue;
                        if (!seen.Add((nint)lm)) continue; // already did Global/Active
                        hidden += WalkLayoutAndHideFurniture(lm, "Loaded:" + lm->TerritoryTypeId);
                    }
                }
                catch (Exception ex) { log.Warning("[HMSync] LoadedLayouts walk skipped: " + ex.Message); }
            }
            // S165: census of every housing-instance Id our walk ENCOUNTERED (whether hidden or
            // not). Cross-reference against the visible-partition Ids Meddle reports: if a visible
            // partition's Id is HERE, we reach it (hide failing); if ABSENT, it's in a container we
            // still don't enumerate. The census logs both the Id list and which layout-member each
            // came from, so we can see exactly where the reachable furniture lives vs the gap.
            if (!quietDeDraw && housingIdCensus.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[HOUSINGIDS] encountered ").Append(housingIdCensus.Count).Append(" housing instances: ");
                foreach (var e in housingIdCensus) sb.Append(e).Append(' ');
                DiagLog("[HMSync] " + sb.ToString());
            }
            // S176: per-instance graphics-resolution path for every BgPart housing instance - shows
            // EXACTLY which render-Visible instances we failed to resolve (NULL = GraphicsObject
            // field also null at walk time → skipped → renders). Cross-ref the @addr against Meddle's
            // Visible partition to confirm which cluster renders and why GetEffectiveGraphics missed it.
            if (!quietDeDraw && gfxResolveDiag.Count > 0)
            {
                var sb2 = new System.Text.StringBuilder();
                sb2.Append("[GFXRESOLVE] ").Append(gfxResolveDiag.Count).Append(" BgPart housing instances: ");
                foreach (var e in gfxResolveDiag) sb2.Append(e).Append(" | ");
                DiagLog("[HMSync] " + sb2.ToString());
            }
            DeDrawFinishColliders(hidden);

            // S164: hide furniture at its OWNING object, via the HousingFurnitureManager - not just
            // the layout-index copy. Meddle's ParseTerritoryFurniture reads furniture from
            // FurnitureManager.ObjectManager.ObjectArray.Objects[index], casts each to HousingObject,
            // and uses ->SharedGroupLayoutInstance as the renderable. The persistent bare-Housing
            // partitions/pillars/chair render through THAT manager-owned instance and ignore the
            // IsVisible we set on the layout-index shadow copy (Meddle showed them "Hidden" yet they
            // drew). The SharedGroup-wrapped doors hid because SharedGroup recursion already reached
            // their real instance. So walk the furniture manager's object array directly and hide
            // each furniture object's SharedGroupLayoutInstance (+ recurse its children). This is the
            // furniture's authoritative render object.
            HideFurnitureManagerObjects();
            return;
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] Housing furniture de-draw skipped: " + ex.Message);
        }
    }

    // S161: walk one layout's instance buckets and hide furniture-class instances. Extracted
    // from DeDrawHousingFurniture so it can run over both GlobalLayout and ActiveLayout. Returns
    // count hidden. Adds each hidden instance's key to hiddenInstanceKeys for the collider pass.
    // S164: walk the HousingFurnitureManager's object array and hide each furniture object's
    // SharedGroupLayoutInstance (the manager-owned renderable, per Meddle's furniture parse).
    // This catches the persistent bare-Housing furniture that ignores the layout-index IsVisible.
    // S185: deliberate IndoorTerritory teardown experiment, triggered by /hms teardownhousing.
    // The furniture is ACTIVELY MAINTAINED (S183 proved severing memory just makes it re-instantiate
    // - manager 9→2 but meshes respawn as a new cluster). So the lever isn't clearing data; it's
    // stopping the maintainer = releasing the live IndoorTerritory, the way the front-door exit does.
    // HousingTerritory.Dtor (vfunc 0) is the destructor. Staged + logged so a crash pinpoints the line.
    // KNOWN RISK: HousingManager still holds CurrentTerritory/IndoorTerritory pointers; after Dtor they
    // dangle, so we null them too. The game may or may not survive us tearing this down out from under it.
    public void TeardownHousing()
    {
        try
        {
            var hm = HousingManager.Instance();
            if (hm == null) { log.Warning("[HMSync] [TEARDOWN] HousingManager null"); return; }

            var indoor = hm->IndoorTerritory;
            var current = hm->CurrentTerritory;
            log.Information("[HMSync] [TEARDOWN] pre: IndoorTerritory=" + (nint)indoor + " CurrentTerritory=" + (nint)current
                + " IsInside=" + hm->IsInside());

            if (indoor == null) { log.Warning("[HMSync] [TEARDOWN] IndoorTerritory already null - nothing to tear down"); return; }

            // Snapshot furniture count before, for confirmation.
            var fmPre = hm->GetFurnitureManager();
            int objPre = fmPre != null ? fmPre->ObjectManager.ObjectArray.ObjectCount : -1;
            log.Information("[HMSync] [TEARDOWN] furniture ObjectCount pre=" + objPre);

            // Null the manager's pointers FIRST, so if the next housing Update fires before/after
            // Dtor it doesn't deref the (about-to-be) freed territory. CurrentTerritory == indoor
            // when inside an apartment; null both if they match.
            log.Information("[HMSync] [TEARDOWN] nulling HousingManager territory pointers");
            if (hm->CurrentTerritory == (HousingTerritory*)indoor) hm->CurrentTerritory = null;
            hm->IndoorTerritory = null;

            // S186/S187/S189: suppress the target scan across the teardown so its per-frame walk
            // can't deref a freed housing object. Windowed (not one-shot) since the scan rebuilds.
            SuppressTargetScanUntilReady();

            // Now destruct the territory. freeFlags=1 (destruct + free), matching ECommons' Dtor(true).
            log.Information("[HMSync] [TEARDOWN] calling IndoorTerritory->Dtor(1)");
            ((HousingTerritory*)indoor)->Dtor(1);
            log.Information("[HMSync] [TEARDOWN] Dtor returned - survived. Furniture should be gone.");
        }
        catch (Exception ex) { log.Error("[HMSync] [TEARDOWN] managed exception: " + ex.Message); }
    }

    // S189/S190: keep the target scan arrays empty until the reloaded scene is actually READY,
    // not for a fixed frame count. The crash is the scan calling GetPosition on an object whose
    // DrawObject is still rebuilding during the reload - so the correct release condition is
    // "scene finished rebuilding", detected via the local player's IsReadyToDraw() + non-null
    // DrawObject (the same readiness signal ReEnablePreservedObjects trusts). A fixed window
    // (S189's 90 frames) was too short for dense zones like 128 Limsa (huge aetheryte plaza +
    // many objects = longer rebuild); waiting for ready scales to any map/machine. The frame
    // counter is demoted to a SAFETY CAP so a missing ready-signal can't suppress forever.
    private int targetScanSuppressCap;
    private bool targetScanSuppressArmed;
    private const int TargetScanSuppressCapFrames = 600; // ~10s backstop only
    // S241: post-ready settle margin - conditions must hold for this many consecutive frames
    // before suppression lifts, so the reload's foreign-object teardown is definitely finished.
    private int targetScanSettleHold;
    private const int TargetScanSettleFrames = 30;       // ~0.5s settle past scene-ready

    private void SuppressTargetScanUntilReady()
    {
        SanitizeTargetSystem();   // clear immediately
        targetScanSuppressCap = TargetScanSuppressCapFrames;
        if (targetScanSuppressArmed) return;
        targetScanSuppressArmed = true;
        framework.Update += PollTargetScanSuppress;
        DiagLog("[HMSync] [SANITIZE] target-scan suppression armed (release on scene-ready)");
    }

    private void PollTargetScanSuppress(IFramework fw)
    {
        // Keep the scan empty this frame regardless.
        try
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts != null)
            {
                byte* tsb = (byte*)ts;
                *(int*)(tsb + 0x148) = 0;
                *(int*)(tsb + 0x2178) = 0;
                *(int*)(tsb + 0x3B18) = 0;
                *(int*)(tsb + 0x54B8) = 0;
            }
        }
        catch { /* transient during teardown; ignore */ }

        // Release when the scene is FULLY ready, or when the safety cap expires.
        // S241: the old condition released on LOCAL-PLAYER readiness alone - too early. The
        // GetPosition+0x24 crash is the scan hitting a FOREIGN object (Kugane's Aetheryte) whose
        // DrawObject is mid-teardown during the home reload. The local player can be ready-to-draw
        // while those foreign objects are still being destroyed by the in-flight reload, so the
        // suppressor lifted and the scan resumed straight into a torn Aetheryte. Now we hold until
        // ALL of: (a) not mid-transition (IsTransitioning false - the Revert/Load call has fully
        // returned), (b) local player ready, AND (c) a short settle hold past that, so the reload's
        // object teardown is definitely complete before the scan is allowed to walk the table again.
        bool playerReady = false;
        try
        {
            var lp = objectTable.LocalPlayer;
            if (lp != null)
            {
                var native = (GameObject*)lp.Address;
                playerReady = native->DrawObject != null && native->IsReadyToDraw();
            }
        }
        catch { playerReady = false; }

        bool sceneSettled = !IsTransitioning && playerReady;
        if (sceneSettled)
            targetScanSettleHold--;     // count down the post-ready settle margin
        else
            targetScanSettleHold = TargetScanSettleFrames;   // reset margin until conditions hold

        bool capHit = --targetScanSuppressCap <= 0;
        if ((sceneSettled && targetScanSettleHold <= 0) || capHit)
        {
            targetScanSuppressArmed = false;
            framework.Update -= PollTargetScanSuppress;
            DiagLog("[HMSync] [SANITIZE] target-scan suppression released ("
                + (capHit ? "SAFETY-CAP - ready signal never arrived, investigate" : "scene-settled") + ")");
        }
    }

    // S189: the GetPosition+0x24 UAF is the target system's per-frame on-screen SCAN
    // (TargetSystem.Update -> sub_140624AA0) iterating its TargetableObjectsOnScreen /
    // ObjectFilterArray entries and calling GetPosition on one whose DrawObject is being torn
    // down by the in-progress zone reload. Clearing the single-target POINTERS (S187) didn't help
    // because the scan walks these ARRAYS, not the pointers, and rebuilds them every frame.
    // The fix: zero each array's Length (offset 0x00) right before we trigger a reload, so the
    // scan has zero entries to walk during the danger window. The game repopulates Length from the
    // live object table on its next scan, so this is self-healing. We also clear the single-target
    // slots (cheap, correct defense-in-depth). Confirmed offsets: GameObjectArray.Length @ 0x00;
    // TargetableObjectsOnScreen @ 0x148, ObjectFilterArray1/2/3 @ 0x2178/0x3B18/0x54B8.
    private void SanitizeTargetSystem()
    {
        try
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts == null) return;

            // Clear single-target slots via the game's setters + direct nulls.
            ts->SetHardTarget(null);
            ts->SetSoftTarget(null);
            ts->Target = null;
            ts->SoftTarget = null;
            ts->GPoseTarget = null;
            ts->MouseOverTarget = null;
            ts->MouseOverNameplateTarget = null;
            ts->FocusTarget = null;
            ts->PreviousTarget = null;
            ts->TargetObjectId = default;

            // THE FIX: empty the scan arrays so TargetSystem.Update's walk has nothing to deref
            // during the reload. Length is the first field (0x00) of each GameObjectArray.
            byte* tsb = (byte*)ts;
            *(int*)(tsb + 0x148) = 0;   // TargetableObjectsOnScreen.Length
            *(int*)(tsb + 0x2178) = 0;  // ObjectFilterArray1.Length
            *(int*)(tsb + 0x3B18) = 0;  // ObjectFilterArray2.Length
            *(int*)(tsb + 0x54B8) = 0;  // ObjectFilterArray3.Length

            DiagLog("[HMSync] [SANITIZE] target scan arrays emptied + targets cleared (pre-reload)");
        }
        catch (Exception ex) { log.Error("[HMSync] [SANITIZE] error: " + ex.Message); }
    }

    private void HideFurnitureManagerObjects()
    {
        try
        {
            var hm = HousingManager.Instance();
            if (hm == null) return;
            var fm = hm->GetFurnitureManager();
            if (fm == null) return;

            int count = fm->ObjectManager.ObjectArray.ObjectCount;
            int hitObjs = 0, hidGfx = 0;
            for (int i = 0; i < count; i++)
            {
                var go = fm->ObjectManager.ObjectArray.Objects[i].Value;
                if (go == null) continue;

                // S168: suppress draw on the furniture GAMEOBJECT itself, not just its layout
                // instance. The census proved we hide every layout-instance copy (both duplicate
                // sets) and Meddle reads them "Hidden" - yet the bare-Housing pieces still render.
                // So DrawObject.IsVisible on the layout instance is NOT the lever for these. Brio
                // and Hyperborea suppress GameObject draw via RenderFlags / DisableDraw - a proven
                // lever we never applied to the furniture objects. RenderFlags |= 0x02 hides without
                // tearing down the DrawObject (gentler than DisableDraw(); avoids a rebuild race).
                // Safe: re-fetched by index each call (no stored ptr), furniture objects are not
                // Penumbra-managed characters. Original flags tracked for restore.
                if (!furnitureRenderFlagSaved.ContainsKey((nint)go))
                    furnitureRenderFlagSaved[(nint)go] = (uint)go->RenderFlags;
                go->RenderFlags |= (VisibilityFlags)0x02;

                var sg = go->SharedGroupLayoutInstance;
                if (sg == null) continue;
                hitObjs++;

                var inst = (ILayoutInstance*)sg;
                var gfx = inst->GetGraphics();
                if (gfx == null) gfx = inst->GetGraphics2();   // S172
                if (gfx != null)
                {
                    HideGfx((DrawObject*)gfx, inst, "FMSELF");   // v0.7.428
                    hiddenLayoutInstances.Add((nint)inst);
                    hidGfx++;
                }

                // S177: descend the furniture object's SharedGroupLayoutInstance to hide its BgPart
                // CHILDREN - the actual partition/pillar meshes. Meddle shows the rendering pieces as
                // BgPart[1] instances at addresses NONE of our layout walks encounter (e.g. 134's
                // visible partitions 1707199432032 / 1705214576224 were absent from both the census
                // and GFXRESOLVE). They are children of the furniture object's SharedGroup, reached
                // via the furniture manager - NOT via the layout-instance graph we walk. We were
                // hiding the SharedGroup's own graphics but never recursing into its BgPart children
                // here (HideSharedGroupChildren only ran for SharedGroups found in the layout walk).
                // inst is fresh-from-the-manager this frame (safe, by-index, no stored ptr).
                hidGfx += HideSharedGroupChildren((SharedGroupLayoutInstance*)sg, 0);
                hiddenInstanceKeys.Add(inst->Id.InstanceKey);
                hidGfx += HideSharedGroupChildren(sg, 0);
            }
            if (hitObjs > 0)
                if (!quietDeDraw) DiagLog("[HMSync] [FURNMGRHIDE] furniture-manager objects=" + hitObjs + " gfx-hidden=" + hidGfx);

            // S169: sweep the GLOBAL GameObjectManager for ALL HousingEventObject GameObjects and
            // RenderFlags-suppress every one. The S168 furniture-manager array is a SUBSET (its 9
            // objects). The census proved DUPLICATE furniture instance-clusters exist; on 134 the
            // manager's 9 are a NON-rendering copy while a second copy renders (doors included),
            // which is why S168 reported success yet furniture stayed visible underground. Furniture
            // GameObjects live in GameObjectManager indices 449-488 (EventObjectManager range -
            // HousingObject/HousingCombinedObject; this is why they're absent from Dalamud's
            // IObjectTable). Walking the global manager by index (SAFE - re-fetched each call, no
            // stored ptr) catches BOTH clusters. Logs the count: 18 ⇒ two clusters of 9 (theory
            // confirmed); 9 ⇒ rendering copy isn't a separate GameObject (different problem).
            var gom = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
            if (gom != null)
            {
                int housingGos = 0;
                var arr = gom->Objects.IndexSorted;
                for (int i = 0; i < arr.Length; i++)
                {
                    var go = arr[i].Value;
                    if (go == null) continue;
                    if (go->ObjectKind != ObjectKind.HousingEventObject) continue;
                    housingGos++;
                    if (!furnitureRenderFlagSaved.ContainsKey((nint)go))
                        furnitureRenderFlagSaved[(nint)go] = (uint)go->RenderFlags;
                    go->RenderFlags |= (VisibilityFlags)0x02;
                }
                if (housingGos > 0)
                    if (!quietDeDraw) DiagLog("[HMSync] [GOMFURNHIDE] global HousingEventObject GameObjects suppressed=" + housingGos);
            }

            // S170 DIAG: dump what the furniture-manager's 9 objects ACTUALLY are on this map -
            // ObjectKind, DrawObject-null, visible, position. On 134 the manager reports 9 but the
            // GOM has only 2 HousingEventObjects and furniture renders underground despite all
            // levers firing. This shows whether the manager's 9 have DrawObjects / are visible /
            // where they are - distinguishing "manager owns non-rendering ghosts" from "manager
            // owns the real meshes but a flag is overridden by the game's draw loop."
            if (!quietDeDraw) DumpFurnitureManagerObjects("dedraw");
        }
        catch (Exception ex) { log.Warning("[HMSync] Furniture-manager hide skipped: " + ex.Message); }
    }

    private int WalkLayoutAndHideFurniture(LayoutManager* layout, string label)
    {
        if (layout == null) { log.Debug("[HMSync] Furniture: " + label + "Layout null"); return 0; }
        int hidden = 0;
        try
        {
            // v0.7.421 - IndoorObject (76) + OutdoorObject (77) added. Runtime-placed furniture
            // registers in InstancesByType under these keys, NOT under SharedGroup (6) - the
            // furniture manager's placed items were invisible to every prior walk (and to Meddle,
            // which walks the same buckets - hence no dot on the visible stove). The instances are
            // SharedGroup-SHAPED (FURNMGRHIDE reads go->SharedGroupLayoutInstance and descends it -
            // same object), so they cast and descend like SharedGroups below.
            foreach (var typeKey in new[] { InstanceType.SharedGroup, InstanceType.BgPart,
                                            InstanceType.Vfx, InstanceType.Light,
                                            InstanceType.IndoorObject, InstanceType.OutdoorObject })
            {
                if (!layout->InstancesByType.TryGetValuePointer(typeKey, out var mapPtrPtr)
                    || mapPtrPtr == null)
                    continue;
                var innerMap = mapPtrPtr->Value;
                if (innerMap == null) continue;

                foreach (var kv in *innerMap)
                {
                    var inst = kv.Item2.Value;
                    if (inst == null) continue;

                    // Only the DISPLAYED map's GlobalLayout hides event-map furniture via
                    // HavePrimary. ALL other layouts (Active + every other LoadedLayouts member,
                    // incl. the persistent apartment) hide ONLY housing assets (bgcommon/hou/) -
                    // never their room-box/structure or world geometry. The path gate is what makes
                    // walking foreign layouts safe.
                    bool isHousingAsset = IsHousingPath(inst->GetPrimaryPath());
                    if (isHousingAsset) housingIdCensus.Add(label + "#" + (ulong)inst->Id.InstanceKey + "@" + (nint)inst);
                    bool hideThis = label == "Global" ? (inst->HavePrimary() || isHousingAsset)
                                                      : isHousingAsset;
                    if (hideThis)
                    {
                        // S173/S175: read the REAL renderable via GetEffectiveGraphics (vfuncs, then
                        // the BgParts GraphicsObject field). Safe here - inst is fresh from the live
                        // container this frame. If null, skip (no held pointer, no retry - S174 AV).
                        // S176 DIAG: record per-instance resolution so we can see EXACTLY which
                        // instances render-Visible-but-unhidden. For each BgPart housing instance,
                        // log address + which path resolved (vfunc23 / vfunc24 / field / NULL).
                        if (inst->Id.Type == InstanceType.BgPart && isHousingAsset)
                        {
                            // S179: log POSITION (load-stable, comparable to Meddle across loads -
                            // addresses churn every load via reallocation, which invalidated all
                            // prior address cross-refs). Rendering partitions are always at
                            // (-1.57,0,0.39) and (2.30,0,0.36) per Meddle. If we hide instances at
                            // those positions and they still render -> wrong object / re-assert. If
                            // no hidden instance sits there -> genuine container gap.
                            var v1 = inst->GetGraphics();
                            var fld = ((BgPartsLayoutInstance*)inst)->GraphicsObject;
                            string how = v1 != null ? "vf23" : fld != null ? "FIELD" : "NULL";
                            string pos = fld != null ? "(" + fld->Position.X.ToString("F2") + "," + fld->Position.Z.ToString("F2") + ")" : "(?)";
                            gfxResolveDiag.Add("IBT" + pos + how + "@" + (nint)inst);
                        }
                        var gfx = GetEffectiveGraphics(inst);
                        if (gfx != null)
                        {
                            HideGfx(gfx, inst, "IBT");   // v0.7.428
                            hiddenLayoutInstances.Add((nint)inst);
                            hidden++;
                        }
                    }

                    // Descend housing SharedGroups to reach nested furniture meshes (S159).
                    // v0.7.421: IndoorObject/OutdoorObject instances are SharedGroup-shaped - descend them too.
                    if ((typeKey == InstanceType.SharedGroup || typeKey == InstanceType.IndoorObject
                         || typeKey == InstanceType.OutdoorObject) && isHousingAsset)
                        hidden += HideSharedGroupChildren((SharedGroupLayoutInstance*)inst, 0);

                    if (hideThis || isHousingAsset)
                        hiddenInstanceKeys.Add(inst->Id.InstanceKey);
                }
            }

            // S162: ALSO walk the LGB LAYER index (LayoutManager.Layers → LayerManager.Instances),
            // not just the flattened InstancesByType. Six InstancesByType walks (both layouts)
            // never touched the persistent apartment furniture, yet Meddle enumerates it - because
            // Meddle walks the layer hierarchy. The placed furniture rides in a LayerManager's
            // Instances map (StdMap<uint, ILayoutInstance*>), which InstancesByType doesn't fully
            // reflect. Housing-path gated, so we only hide furniture-class instances. Logs a
            // furniture-hit count so we can confirm whether the leak lives here.
            int layerFurnHits = 0;
            if (layout->Layers.Count > 0)
            {
                foreach (var lkv in layout->Layers)
                {
                    var lm = lkv.Item2.Value;
                    if (lm == null) continue;
                    foreach (var ikv in lm->Instances)
                    {
                        var inst = ikv.Item2.Value;
                        if (inst == null) continue;
                        if (!IsHousingPath(inst->GetPrimaryPath())) continue;
                        housingIdCensus.Add(label + "/layer#" + (ulong)inst->Id.InstanceKey + "@" + (nint)inst);
                        // S178 DIAG: tag layer-walk resolution so we can see whether the RENDERING
                        // instances (e.g. 134's 1706672314928, which IS in this layer census but
                        // renders Visible) resolve here and via which path.
                        if (inst->Id.Type == InstanceType.BgPart)
                        {
                            var lv1 = inst->GetGraphics();
                            var lfld = ((BgPartsLayoutInstance*)inst)->GraphicsObject;
                            string lhow = lv1 != null ? "vf23" : lfld != null ? "FIELD" : "NULL";
                            string lpos = lfld != null ? "(" + lfld->Position.X.ToString("F2") + "," + lfld->Position.Z.ToString("F2") + ")" : "(?)";
                            gfxResolveDiag.Add("LYR" + lpos + lhow + "@" + (nint)inst);
                        }
                        var gfx = GetEffectiveGraphics(inst);   // S173
                        if (gfx != null)
                        {
                            HideGfx(gfx, inst, "LYR");   // v0.7.428
                            hiddenLayoutInstances.Add((nint)inst);
                            hidden++; layerFurnHits++;
                        }
                        hiddenInstanceKeys.Add(inst->Id.InstanceKey);
                        if (inst->Id.Type == InstanceType.SharedGroup)
                        {
                            // S181: this layer instance is a housing SharedGroup. Dump its FULL
                            // descendant tree (type+pos+addr) so we can see whether the rendering
                            // BgPart (the co-located layer cluster we never hide) lives inside it.
                            gfxResolveDiag.Add("LYRSG@" + (nint)inst + "{");
                            DumpSharedGroupTree((SharedGroupLayoutInstance*)inst, 0);
                            gfxResolveDiag.Add("}");
                            hidden += HideSharedGroupChildren((SharedGroupLayoutInstance*)inst, 0);
                        }
                    }
                }
            }
            if (layerFurnHits > 0)
                DiagLog("[HMSync] [LAYERWALK] " + label + ": hid " + layerFurnHits + " housing instances from the LAYER index");
        }
        catch (Exception ex) { log.Warning("[HMSync] Furniture walk (" + label + ") skipped: " + ex.Message); }
        return hidden;
    }

    // S161: collider disable + arrow suppression, run once after both layouts are walked.
    // hidden = count from the visual passes (for the log line). Uses hiddenInstanceKeys
    // (populated by WalkLayoutAndHideFurniture over both layouts) to match Scene colliders.
    // S266: kill collision on every collider whose owning instance key is in hiddenInstanceKeys.
    // Walks the BGCollisionScene's collider list, matches LayoutObjectId (low dword = InstanceKey),
    // and zeroes LayerMask + clears the visibility bit (the documented raycast-ignore). Saves prior
    // values for exact restore. Returns count newly disabled. Extracted so the deferred collider pass
    // can call it after late-streaming instances arrive (the furniture pass runs once, too early for
    // instances that stream in later).
    // v0.7.265: RULE-BASED BARRIER SUPPRESSION. Duty checkpoint/boss-arena barriers are Box-kind colliders in the
    // physics scene, parented to gimmick (gmc*) or level-device (w_lvd*) SharedGroups. Confirmed against xivtool
    // collision dumps for 1345: all 11 barriers are Box colliders under gmc/w_lvd parents; the one unparented flat
    // Box is floor (kept). We zero LayerMask on the matched Box colliders directly (HCollider's mechanism) - this
    // neutralises ONLY that Box, leaving the parent SharedGroup's other colliders (the wreck meshes) intact. No
    // hardcoded coordinates: a structural rule that auto-scales to the whole map (both halves) and other duties with
    // the same SE convention. Saved for restore-on-stop. Territory-gated by the caller.
    private const uint BarrierSuppressTerritory = 1345;   // v0.7.265: gated to 1345 until validated, then scale up

    // v0.7.268: match barrier Box colliders by WORLD POSITION, not parent path. The physics scene is a FLAT
    // collider list - the SharedGroup hierarchy (the gmc/w_lvd parentage visible in the LGB) is NOT preserved at
    // runtime, and the collider's LayoutObjectId does not reliably resolve back to the owning group (diagnostic
    // f31: box=43, pathResolved=43, but every barrier box's GetPrimaryPath came back empty). Position is the one
    // thing that round-trips cleanly between the LGB/collision dump and the live scene. These are the 11 barrier
    // box world centres from the 1345 collision dump (gmc + w_lvd parented boxes; the one unparented flat floor
    // box is excluded). Matched within a tolerance radius against each Box collider's GetTranslation.
    private static readonly System.Numerics.Vector3[] BarrierBoxPositions1345 = new[]
    {
        new System.Numerics.Vector3(-804.1f, 4.0f, 736.2f),   // gmc03  (B1 entrance wreck)
        new System.Numerics.Vector3(-616.5f, 4.0f, 690.6f),   // gmc04  (B2)
        new System.Numerics.Vector3(-615.1f, 2.5f, 609.8f),   // w_lvd_b0118 (B3 screen)
        new System.Numerics.Vector3(-615.0f, 2.5f, 540.0f),   // w_lvd_b0118 (B4 screen)
        new System.Numerics.Vector3( 642.5f, -8.0f, -153.5f), // gmc06  (2nd half)
        new System.Numerics.Vector3( 660.0f,  6.8f,  -51.4f), // gmc05
        new System.Numerics.Vector3( 569.0f, -0.4f,   69.0f), // gmc04 (2nd)
        new System.Numerics.Vector3( 760.0f, 83.0f, -718.4f), // gmc08
        new System.Numerics.Vector3( 525.0f, 15.0f, -314.0f), // gmc07
        new System.Numerics.Vector3( 760.0f, 63.5f, -778.0f), // w_lvd_b0118 (2nd)
        new System.Numerics.Vector3( 660.4f, 11.4f,  -67.3f), // w_lvd_b0250
    };
    private const float BarrierMatchRadius = 3.0f;   // world-pos tolerance (dump vs runtime centre)

    private unsafe int SuppressBarrierColliders()
    {
        int hit = 0;
        int totalColliders = 0, boxColliders = 0, posMatches = 0;
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var bgModule = fw != null ? fw->BGCollisionModule : null;
            var sceneMgr = bgModule != null ? bgModule->SceneManager : null;
            if (sceneMgr == null) { if (!barrierDiagDumped) ReportDebug("[HMSync] [BARRIER] sceneMgr NULL (streaming)"); return 0; }
            foreach (var sw in sceneMgr->Scenes)
            {
                var scene = sw != null ? sw->Scene : null;
                if (scene == null) continue;
                foreach (var col in scene->Colliders)
                {
                    if (col == null) continue;
                    totalColliders++;
                    var ctype = col->GetColliderType();

                    // v0.7.319: zone-border barrier Planes (per HCollider spec). A Plane/PlaneTwoSided whose
                    // ObjectMaterialValue is an LVD boundary material (0x2400 = boundary|zone-wall, or 0x4400) is a
                    // hard-boundary barrier - self-identifying by material, NO co-location gate (most barrier planes
                    // have no visible star-string but still block; gating on a nearby LineVfx missed ~14/19 on 893).
                    // Suppress via LayerMask=0 using the SAME save/restore keying as the Box barriers below. The
                    // visible glow (LineVfx) is intentionally NOT touched - confirmed not suppressible from the
                    // instance (no reachable renderable handle); collision-only, glow stays. Runs on ALL instanced
                    // content (material is definitive zone-wide), not gated to a territory list.
                    if (ctype == BGColliderType.Plane || ctype == BGColliderType.PlaneTwoSided)
                    {
                        ulong mat = col->ObjectMaterialValue;
                        if (mat == 0x2400 || mat == 0x4400)
                        {
                            uint bkey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                            if (!barrierSavedState.ContainsKey(bkey))
                                barrierSavedState[bkey] = (col->VisibilityFlags, col->LayerMask);
                            if (col->LayerMask != 0 || (col->VisibilityFlags & 0x1) != 0)
                            {
                                col->VisibilityFlags &= unchecked((byte)~0x1);
                                col->LayerMask = 0;
                                hit++;
                            }
                        }
                        continue;   // handled (or skipped) - a barrier Plane is never a Box position-match
                    }

                    if (ctype != BGColliderType.Box) continue;
                    boxColliders++;
                    System.Numerics.Vector3 pos; col->GetTranslation(&pos);
                    bool isBarrier = false;
                    foreach (var bp in BarrierBoxPositions1345)
                        if (System.Numerics.Vector3.Distance(pos, bp) <= BarrierMatchRadius) { isBarrier = true; break; }
                    if (!isBarrier) continue;
                    posMatches++;
                    uint ownerKey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                    if (!barrierSavedState.ContainsKey(ownerKey))
                        barrierSavedState[ownerKey] = (col->VisibilityFlags, col->LayerMask);
                    if (col->LayerMask != 0 || (col->VisibilityFlags & 0x1) != 0)
                    {
                        col->VisibilityFlags &= unchecked((byte)~0x1);
                        col->LayerMask = 0;
                        hit++;
                    }
                }
            }
            if (posMatches > barrierDiagMaxBox)
            {
                barrierDiagMaxBox = posMatches;
                DiagLog("[HMSync] [BARRIER] f" + barrierDiagTick + " colliders=" + totalColliders + " box=" + boxColliders +
                    " posMatch=" + posMatches + " suppressed=" + hit);   // v0.7.447: research-mode gated (was spamming via ReportDebug)
                if (posMatches >= BarrierBoxPositions1345.Length) barrierDiagDumped = true;
            }
            barrierDiagTick++;
        }
        catch (Exception ex) { log.Warning("[HMSync] SuppressBarrierColliders skipped: " + ex.Message); }
        return hit;
    }
    private bool barrierDiagDumped;
    private int barrierDiagTick;
    private int barrierDiagMaxBox;

    // v0.7.273: suppress specific terrain (tr*.pcb) colliders by pcb filename. These are Mesh/Terrain-kind
    // colliders (the pcb path is read directly off the collider via ColliderMesh->Resource, no severed-instance
    // lookup needed). CAUTION: tr* are the floor-welded terrain type - suppressing one removes collision for its
    // whole volume (walls AND floor). Only the pcbs V has manually verified as navigable-if-removed go here.
    // Zeroes LayerMask (restored on stop via barrierSavedState, shared with the Box suppress).
    private static readonly string[] TerrainSuppress1345 = {
        "tr0329", "tr0328", "tr0327", "tr0627", "tr0527", "tr0626",   // v0.7.273 first batch
        "tr2413", "tr2412", "tr2511", "tr2411",                        // v0.7.275 second batch (non-welded)
        "tr2410",                                                      // v0.7.276 shared invisible wall for arch gates 1+2
        "tr2704", "tr2604", "tr2806", "tr2906",                        // v0.7.279 fourth batch
    };

    private unsafe int SuppressTerrainColliders()
    {
        int hit = 0;
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var bgModule = fw != null ? fw->BGCollisionModule : null;
            var sceneMgr = bgModule != null ? bgModule->SceneManager : null;
            if (sceneMgr == null) return 0;
            foreach (var sw in sceneMgr->Scenes)
            {
                var scene = sw != null ? sw->Scene : null;
                if (scene == null) continue;
                foreach (var col in scene->Colliders)
                {
                    if (col == null) continue;
                    if (col->GetColliderType() != BGColliderType.Mesh) continue;   // tr* are Mesh/Terrain-kind
                    var cm = (ColliderMesh*)col;
                    if (cm->Resource == null) continue;
                    string path;
                    try { path = cm->Resource->GetPath().ToString(); } catch { continue; }
                    if (string.IsNullOrEmpty(path)) continue;
                    bool match = false;
                    foreach (var tr in TerrainSuppress1345) if (path.Contains(tr)) { match = true; break; }
                    if (!match) continue;
                    uint ownerKey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                    if (!barrierSavedState.ContainsKey(ownerKey))
                        barrierSavedState[ownerKey] = (col->VisibilityFlags, col->LayerMask);
                    if (col->LayerMask != 0 || (col->VisibilityFlags & 0x1) != 0)
                    {
                        col->VisibilityFlags &= unchecked((byte)~0x1);
                        col->LayerMask = 0;
                        hit++;
                    }
                }
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] SuppressTerrainColliders skipped: " + ex.Message); }
        return hit;
    }

    // v0.7.273: hide environmental/placed VFX by path substring (avfx name match). Walks InstanceType.Vfx in both
    // layouts, hides via GetGraphics()->IsVisible=false (HCollider's VfxSetVisible pattern). Persistent re-apply
    // (VFX re-stream like models). Patterns are substrings: "fire" catches fireab/fire*, "void", "dext" (the
    // dungeon-exit purple curtain that also shows at dungeon start).
    private static readonly string[] VfxHide1345 = { "fireab", "void", "fire" };  // 1345-specific
    private static readonly string[] VfxHide893 = { "qic" };                       // 893 Imperial Palace border curtains
    // v0.7.469: two more cinematic maps whose baked-in ambient effects read as distraction in RP, not atmosphere.
    // ⚠ "wall" is a BROAD substring - broader than the others here. It is safe only because these lists are
    // MAP-SCOPED (MapSpecificVfxPatterns returns them for 1137 alone), so its blast radius is one territory. If a
    // wanted effect on 1137 ever disappears, this is the first suspect: run `/hms vfxdump wall` there and read
    // which paths carry MATCH. Do NOT promote "wall" to VfxHideAllMaps - game-wide it would be a shotgun.
    private static readonly string[] VfxHide1137 = { "wall" };                     // 1137 - *wall*.avfx
    private static readonly string[] VfxHide1155 = { "maho" };                     // 1155 - *maho*.avfx
    // All-maps patterns. Plain SUBSTRING matches against the instance's primary .avfx path - there is no
    // glob engine here, so a request for "*eext_y*.avfx" is expressed simply as "eext_y" (the leading and
    // trailing wildcards are implicit in Contains; the .avfx extension is redundant because
    // InstanceType.Vfx only ever yields .avfx paths).
    //   "dext"   - dungeon exit/entry purple curtain
    //   "eext_y" - the eext_y* family, every HMS-loaded map
    private static readonly string[] VfxHideAllMaps = { "dext", "eext_y" };

    // Map-specific VFX patterns for the CURRENT territory (empty if the map has none). Keeps HideBarrierVfx generic.
    private string[] MapSpecificVfxPatterns(uint territory) => territory switch
    {
        1345 => VfxHide1345,
        893  => VfxHide893,
        1137 => VfxHide1137,
        1155 => VfxHide1155,
        _    => System.Array.Empty<string>(),
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════
    // v0.7.466 - LineVFX (boss-barrier line) SUPPRESSION. InstanceType 59 (0x3B) = LineVfxLayoutInstance: the
    // red/white and blue "do not cross" curtains with the pulsing stars. HMS already kills their COLLIDERS
    // (SetColliderActive, vf37 - see DisableSpawnAreaColliders); the visible line is a DIFFERENT object (the
    // "primary"), which is exactly why collision-off left the curtain drawn.
    //
    // Why the existing VFX machinery can't reach these: HideBarrierVfx sweeps InstanceType.Vfx and matches on
    // the instance's .avfx path. A LineVfx instance is not type Vfx and carries NO path at all - the client
    // synthesizes the line procedurally from the instance Transform. So there is nothing for a substring pattern
    // to match, and 893's existing "qic" pattern was never going to catch it.
    //
    // ⚠ DIAGNOSTIC-FIRST BY DESIGN. Nothing here runs on the map-load path yet. THREE candidate mechanisms
    // exist and they are NOT equally safe. Which one applies is an empirical question that one scan answers, so
    // the scan ships before any auto-apply:
    //
    //   1. GetGraphics()->IsVisible = false - HMS's PROVEN lever (S132 / HideGfx). A leaf render bit with no
    //      lifecycle bookkeeping; structurally cannot trip a Deinit teardown. PREFERRED IF THE LEAF EXISTS.
    //   2. SetActive(false) (vf63) - controls the primary directly. But S128–S130 crashed HMS with an AV in
    //      SharedGroupLayoutInstance.Deinit calling exactly this. The reconciliation is that the crash was
    //      CONTAINER-specific (a SharedGroup tears down its child hierarchy) and LineVfx is a bare 0xA0 struct
    //      with one field and no children - a well-supported hypothesis from struct layout, NOT an in-game fact.
    //   3. DestroyPrimary() (vf28) - drops the graphics object without touching the active flag. Fallback if 2
    //      crashes.
    //
    // Run `/hms linevfx` (scan) FIRST. If it reports a reachable graphics leaf, mechanism 1 applies and we never
    // take the SetActive risk at all. If the leaf is null on every instance - which is what the standing note
    // "LineVfxLayoutInstance has no DrawObject of its own, consumed by an external renderer" predicts - then 2/3
    // are the only options, and `one` exists so the first live SetActive costs ONE call, not thirteen.
    //
    // A perfect negative is data: 0 instances found means the ENUMERATION is wrong (wrong layout, wrong map
    // entry), not that the map has no lines. That's why the scan reports GlobalLayout and ActiveLayout counts
    // separately AND cross-checks with a Layers walk - three numbers that disagree localise the fault.
    private const int LineVfxTypeRaw = 59;

    // Keys we suppressed, so restore can re-enumerate and re-show only what we touched. Keys, never pointers -
    // streaming frees instances, so a stored pointer is a use-after-free waiting for the next map (the same
    // discipline as the VFX/NPC restore paths).
    private readonly HashSet<uint> suppressedLineVfx = new();

    /// <summary>LineStyle @0x70 (Red=1, Blue=2, RedFar=3). RAW OFFSET READ - LineVfxLayoutInstance may not be
    /// bound in the installed FFXIVClientStructs, and an unproven type cast is worse than a documented offset.
    /// If this returns nonsense, the offset is wrong; it is reported, never acted on.</summary>
    private static unsafe int LineVfxStyle(ILayoutInstance* inst) => *(int*)((byte*)inst + 0x70);

    /// <summary>Transform.Translation - RangeLayoutInstance.Transform @0x30, translation first. Also a raw read,
    /// and also only ever REPORTED, so a bad offset shows up as absurd coordinates instead of a silent lie.</summary>
    private static unsafe Vector3 LineVfxPos(ILayoutInstance* inst) => *(Vector3*)((byte*)inst + 0x30);

    /// <summary>Count type-59 instances in one layout, for the enumeration cross-check.</summary>
    private unsafe int CountLineVfxIn(LayoutManager* layout)
    {
        if (layout == null) return -1;   // -1 = "no such layout", distinct from 0 = "layout present, no lines"
        int n = 0;
        if (layout->InstancesByType.TryGetValuePointer((InstanceType)LineVfxTypeRaw, out var m) && m != null && m->Value != null)
            foreach (var kv in *m->Value)
                if (kv.Item2.Value != null) n++;
        return n;
    }

    /// <summary>STEP 1 - the instrument. Read-only. Reports per-instance whether a graphics leaf is reachable,
    /// whether the primary has streamed in, the LineStyle, and the position; plus the three enumeration counts.
    /// This single dump decides which of the three mechanisms is usable, so it runs before any of them.</summary>
    public unsafe void DumpLineVfx()
    {
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) { log.Information("[HMSync] [LINEVFX] LayoutWorld NULL"); return; }

            int inGlobal = CountLineVfxIn(lw->GlobalLayout);
            int inActive = CountLineVfxIn(lw->ActiveLayout);

            // Independent cross-check by a different route: the Layers walk (the idiom DisableSpawnAreaColliders
            // uses). If this disagrees with the InstancesByType counts, the map is fine and the LOOKUP is wrong.
            int viaLayers = 0;
            if (lw->ActiveLayout != null)
                foreach (var layerKv in lw->ActiveLayout->Layers)
                {
                    var layer = layerKv.Item2.Value;
                    if (layer == null) continue;
                    foreach (var instKv in layer->Instances)
                    {
                        var li = instKv.Item2.Value;
                        if (li != null && (int)li->Id.Type == LineVfxTypeRaw) viaLayers++;
                    }
                }

            log.Information("[HMSync] [LINEVFX] counts: GlobalLayout=" + (inGlobal < 0 ? "(null)" : inGlobal.ToString())
                + " ActiveLayout=" + (inActive < 0 ? "(null)" : inActive.ToString())
                + " viaLayersWalk=" + viaLayers
                + "  (893 expected ~13; all three disagreeing localises the fault to the lookup, not the map)");

            // Per-instance detail from whichever layout actually has them - prefer ActiveLayout, fall back to Global.
            var layout = inActive > 0 ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) { log.Information("[HMSync] [LINEVFX] no layout holds type 59 - stop here, fix enumeration."); return; }
            if (!layout->InstancesByType.TryGetValuePointer((InstanceType)LineVfxTypeRaw, out var mm) || mm == null || mm->Value == null)
            {
                log.Information("[HMSync] [LINEVFX] InstancesByType has no type-59 bucket. PERFECT NEGATIVE - the bucket's "
                    + "absence means this layout never instantiated any, so the suppression target is elsewhere.");
                return;
            }

            int i = 0, leafReachable = 0, primaries = 0;
            foreach (var kv in *mm->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                i++;

                nint g23 = 0, g24 = 0;
                try { g23 = (nint)inst->GetGraphics(); } catch { }
                try { g24 = (nint)inst->GetGraphics2(); } catch { }
                bool have = false;
                try { have = inst->HavePrimary(); } catch { }
                // v0.7.468: the `visible` column is GONE. It was only read when the graphics pointer was
                // non-null, so on LineVfx - where the leaf is always null - it printed its default `False` and
                // read as "this line isn't drawn", which is the opposite of the truth. A field that can only
                // report one value is not evidence; removing it is better than footnoting it.
                if (g23 != 0 || g24 != 0) leafReachable++;
                if (have) primaries++;

                int style = 0; Vector3 p = default;
                try { style = LineVfxStyle(inst); } catch { }
                try { p = LineVfxPos(inst); } catch { }
                string styleName = style switch { 1 => "Red", 2 => "Blue", 3 => "RedFar", _ => "?" + style };

                log.Information("[HMSync] [LINEVFX] #" + i + " key=" + inst->Id.InstanceKey
                    + " style=" + styleName + " havePrimary=" + have
                    + " gfx23=" + (g23 == 0 ? "null" : g23.ToString("X")) + " gfx24=" + (g24 == 0 ? "null" : g24.ToString("X"))
                    + " pos=(" + p.X.ToString("F1") + "," + p.Y.ToString("F1") + "," + p.Z.ToString("F1") + ")");
            }

            // v0.7.468: guidance updated from what 893 actually proved, rather than what the plan predicted.
            // SETTLED ON 893: leaf always null (mechanism 1 dead) · SetActive(false) is SAFE but does NOT remove
            // an already-streamed primary · DestroyPrimary() DOES · the line re-streams on movement, so the
            // cadence is mandatory, not optional. A map that reports a reachable leaf would be genuinely new
            // information and is called out as such rather than silently taking the same branch.
            log.Information("[HMSync] [LINEVFX] total=" + i + " withReachableLeaf=" + leafReachable + " withPrimary=" + primaries
                + " autoCadence=" + (lineVfxAuto ? "ON" : "OFF")
                + "  →  " + (leafReachable > 0
                    ? "UNEXPECTED: a reachable leaf on " + leafReachable + "/" + i + " - 893 had none. `/hms linevfx gfx` may work here; report this, it is new."
                    : "Leaf null as expected. DestroyPrimary + the every-frame cadence is the working mechanism; nothing to do if autoCadence=ON."));
            if (primaries == 0 && i > 0)
                log.Information("[HMSync] [LINEVFX] NOTE: no primaries streamed yet - instances exist but no line has rendered. "
                    + "Walk toward a barrier and re-scan to see the leaf appear (that is also the re-assertion test).");
        }
        catch (Exception ex) { log.Error("[HMSync] [LINEVFX] scan threw: " + ex.Message); }
    }

    /// <summary>STEP 2 - suppress. <paramref name="mode"/>: "gfx" (IsVisible=false, proven), "setactive"
    /// (vf63), "destroy" (DestroyPrimary vf28). <paramref name="limit"/> 0 = all, N = at most N instances, so
    /// the first live test of a risky mechanism costs one call. Returns the count acted on.</summary>
    /// <summary>When true, <paramref name="limit"/> selects the instance NEAREST the player rather than the
    /// first in map order. v0.7.468: `one` on 893 hit a line 240 units away, which proved the call didn't crash
    /// and nothing else - a single-instance test you cannot SEE only answers half the question.</summary>
    public unsafe int SuppressLineVfx(string mode, int limit) => SuppressLineVfx(mode, limit, nearest: false);

    public unsafe int SuppressLineVfx(string mode, int limit, bool nearest)
    {
        int acted = 0;
        uint nearestKey = 0;
        if (nearest && limit > 0)
        {
            nearestKey = NearestLineVfxKey();
            if (nearestKey == 0) { log.Information("[HMSync] [LINEVFX] no instance found to target"); return 0; }
        }
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) return 0;
            var layout = CountLineVfxIn(lw->ActiveLayout) > 0 ? lw->ActiveLayout : lw->GlobalLayout;
            if (layout == null) return 0;
            if (!layout->InstancesByType.TryGetValuePointer((InstanceType)LineVfxTypeRaw, out var m) || m == null || m->Value == null)
                return 0;

            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                if (nearestKey != 0 && inst->Id.InstanceKey != nearestKey) continue;
                if (limit > 0 && acted >= limit) break;

                switch (mode)
                {
                    case "gfx":
                    {
                        // Mechanism 1 - the proven leaf-flag hide, routed through the SAME HideGfx primitive the
                        // furniture/VFX passes use so a catch here shows up in [CADENCE-CATCH] like any other.
                        var g = (DrawObject*)inst->GetGraphics();
                        if (g == null) g = (DrawObject*)inst->GetGraphics2();
                        if (g == null) continue;   // nothing to hide on this instance - not an error, just report via scan
                        HideGfx(g, inst, "LINEVFX");
                        break;
                    }
                    case "setactive":
                        // Mechanism 2 - THE RISKY ONE on first use. If the client dies here it dies inside this
                        // call; a managed try/catch cannot save an access violation, which is exactly why `limit`
                        // exists and why the operator is told to run limit=1 first.
                        inst->SetActive(false);
                        break;
                    case "destroy":
                        inst->DestroyPrimary();   // Mechanism 3 - fallback; never touches the active flag
                        break;
                    default:
                        return 0;
                }

                suppressedLineVfx.Add(inst->Id.InstanceKey);
                // Kill the coupled collider too, so the line and its "do not cross" wall go together - this is
                // the whole point of the comprehensive removal, and it uses the vfunc HMS already trusts.
                try { inst->SetColliderActive(false); } catch { }
                acted++;
            }
            log.Information("[HMSync] [LINEVFX] " + mode + ": acted on " + acted + " instance(s)"
                + (limit > 0 ? " (limit " + limit + ")" : "") + "; tracked=" + suppressedLineVfx.Count);
        }
        catch (Exception ex) { log.Error("[HMSync] [LINEVFX] suppress(" + mode + ") threw: " + ex.Message); }
        return acted;
    }

    /// <summary>Key of the type-59 instance closest to the player in XZ, or 0 if none. Used by `near` so a
    /// single-instance test lands on a barrier the operator can actually look at.</summary>
    private unsafe uint NearestLineVfxKey()
    {
        var lp = objectTable.LocalPlayer;
        if (lp == null) return 0;
        var pp = lp.Position;
        var lw = LayoutWorld.Instance();
        if (lw == null || lw->ActiveLayout == null) return 0;
        if (!lw->ActiveLayout->InstancesByType.TryGetValuePointer((InstanceType)LineVfxTypeRaw, out var m)
            || m == null || m->Value == null) return 0;
        uint best = 0; float bestD2 = float.MaxValue;
        foreach (var kv in *m->Value)
        {
            var inst = kv.Item2.Value;
            if (inst == null) continue;
            Vector3 p; try { p = LineVfxPos(inst); } catch { continue; }
            float dx = p.X - pp.X, dz = p.Z - pp.Z;
            float d2 = dx * dx + dz * dz;
            if (d2 < bestD2) { bestD2 = d2; best = inst->Id.InstanceKey; }
        }
        if (best != 0)
            log.Information("[HMSync] [LINEVFX] nearest instance key=" + best + " at " + MathF.Sqrt(bestD2).ToString("F1") + " units");
        return best;
    }

    /// <summary>STEP 4 - the re-assertion cadence. PROVEN NECESSARY on 893: DestroyPrimary removes the line, and
    /// the layout re-streams it on player movement, so a one-shot pass can't hold. This is the "out-cadence the
    /// untouchable" pattern HMS already uses for wep models and barrier VFX.
    ///
    /// Runs EVERY frame (not on the persistent-phase throttle) because the re-stream is movement-driven: at the
    /// 30-frame throttle a line would be visible for up to half a second each time it came back. It stays cheap
    /// by gating on HavePrimary() - an instance with no primary is skipped, so a quiet frame costs 13 pointer
    /// reads and no calls.
    ///
    /// MEASURED 2026-07-27 on TT 893: **7800 catches per 600 frames - exactly 13/frame, sustained.** This is
    /// CONSTANT CHURN, not a top-up: every frame all 13 have a primary, we destroy all 13, and the layout rebuilds
    /// all 13 before the next tick. ~780 DestroyPrimary + 780 SetColliderActive calls per second.
    ///
    /// ⚠ WHAT THAT MEANS, stated plainly so nobody mis-reads the working result: the barrier is invisible because
    /// our poll lands before the render each frame. That is a race we are winning, NOT a suppression that holds.
    /// Any frame where this poll is skipped or runs late is a visible flash. It also refutes an easy assumption -
    /// SetActive(false) is NOT preventing creation, so either the layout re-asserts IsActive on every streaming
    /// update, or IsActive simply doesn't gate primary creation for this type.
    ///
    /// ACCEPTED AS-IS (V, 2026-07-27): no measurable cost on real hardware, no observed restream, so the churn is
    /// filed rather than fixed. **If LineVFX suppression ever breaks or starts flashing, start here**, with two
    /// cheap READ-ONLY probes that were never run:
    ///   1. Read the IsActive bit in Flags3 before our SetActive(false), immediately after, and one frame later.
    ///      Comes back true ⇒ the layout re-asserts and the fight is at streaming level. Stays false while the
    ///      primary is rebuilt anyway ⇒ the flag doesn't gate creation and SetActive is the wrong lever entirely.
    ///   2. Dump the suspected StreamingRadiusPerType array read-only and look for a plausible float table with
    ///      40.0f at/near index 59 (CS notes LineStyle.RedFar bumps the radius to 40.0f - that's the tell). A hit
    ///      confirms the offset empirically and makes zeroing it safe, which would stop the primaries streaming at
    ///      source instead of destroying them 780×/s. A miss costs nothing and proves the offset wrong BEFORE a
    ///      write. Never write to that offset on the strength of the brief alone - it was flagged unverified.</summary>
    private bool lineVfxAuto = true;
    private int lineVfxWindowCatches;
    private int lineVfxWindowPasses;

    private unsafe void SuppressLineVfxCadence()
    {
        var lw = LayoutWorld.Instance();
        if (lw == null || lw->ActiveLayout == null) return;
        if (!lw->ActiveLayout->InstancesByType.TryGetValuePointer((InstanceType)LineVfxTypeRaw, out var m)
            || m == null || m->Value == null) return;   // map has no lines - the common case, costs one lookup

        int caught = 0;
        foreach (var kv in *m->Value)
        {
            var inst = kv.Item2.Value;
            if (inst == null) continue;
            bool have = false;
            try { have = inst->HavePrimary(); } catch { }
            if (!have) continue;   // already clean - this is what makes the every-frame cadence affordable
            inst->DestroyPrimary();
            try { inst->SetColliderActive(false); } catch { }
            suppressedLineVfx.Add(inst->Id.InstanceKey);
            caught++;
        }

        lineVfxWindowCatches += caught;
        if (++lineVfxWindowPasses >= 600)   // ~10s at 60fps
        {
            if (lineVfxWindowCatches > 0)
                DiagLog("[HMSync] [LINEVFX-CADENCE] " + lineVfxWindowCatches + " re-stream catches in the last "
                    + lineVfxWindowPasses + " frames (13/frame ⇒ constant churn; a handful ⇒ movement-triggered top-up)");
            lineVfxWindowCatches = 0;
            lineVfxWindowPasses = 0;
        }
    }

    /// <summary>Enable/disable the cadence. Restore MUST disable it first or the next frame undoes the restore.</summary>
    public void SetLineVfxAuto(bool on) => lineVfxAuto = on;
    public bool LineVfxAuto => lineVfxAuto;

    /// <summary>STEP 3 - restore. Re-enumerates and re-shows only the keys we suppressed. Fresh pointers, never
    /// stored ones. DestroyPrimary is NOT reversible here - a destroyed primary comes back only by streaming, so
    /// the honest restore for that mode is a map reload, and this says so rather than pretending.</summary>
    public unsafe int RestoreLineVfx()
    {
        int restored = 0;
        if (suppressedLineVfx.Count == 0) return 0;
        try
        {
            var lw = LayoutWorld.Instance();
            if (lw == null) return 0;
            foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
            {
                if (layout == null) continue;
                if (!layout->InstancesByType.TryGetValuePointer((InstanceType)LineVfxTypeRaw, out var m) || m == null || m->Value == null)
                    continue;
                foreach (var kv in *m->Value)
                {
                    var inst = kv.Item2.Value;
                    if (inst == null || !suppressedLineVfx.Contains(inst->Id.InstanceKey)) continue;
                    try { inst->SetActive(true); } catch { }
                    var g = (DrawObject*)inst->GetGraphics();
                    if (g != null) g->IsVisible = true;
                    try { inst->SetColliderActive(true); } catch { }
                    restored++;
                }
            }
            log.Information("[HMSync] [LINEVFX] restored " + restored + " of " + suppressedLineVfx.Count
                + " tracked (a primary destroyed via `destroy` returns only on re-stream / map reload)");
            suppressedLineVfx.Clear();
        }
        catch (Exception ex) { log.Error("[HMSync] [LINEVFX] restore threw: " + ex.Message); }
        return restored;
    }

    // v0.7.380: dump every VFX instance in the LAYOUT GRAPH for the current zone, with whether the current
    // suppression patterns would match it, whether its graphics leaf is reachable, and whether it is visible.
    //
    // WHAT THIS DISCRIMINATES (handbook §4.3 - "VFX is two different systems, and only one is a layout instance"):
    //   • Effect IS listed        -> layout VFX. Hideable here. If it's listed but not MATCH, the correct
    //                               substring is in the printed path - that's the pattern to add.
    //   • Effect NOT listed       -> actor/system VFX (VfxManager-driven, bound to actors/abilities). It has NO
    //                               layout-graph presence, so HideBarrierVfx can NEVER hide it, whatever the
    //                               pattern. That is a different mechanism and a separate piece of work.
    //   • Listed but gfx=NULL     -> layout instance whose graphics leaf isn't reachable; IsVisible can't be set,
    //                               so this path can't hide it either. Also reported rather than silently skipped.
    //
    // Empty-path instances are REPORTED (as "(no path)") rather than skipped - a silent skip would look
    // identical to "not in the layout graph" and would send the next fix down the wrong branch.
    public unsafe void DumpVfxPaths(string term)
    {
        var lw = LayoutWorld.Instance();
        if (lw == null) { log.Information("[HMSync] [VFXDUMP] LayoutWorld null"); return; }
        int total = 0, shown = 0, matched = 0, noPath = 0, noGfx = 0;
        log.Information("[HMSync] [VFXDUMP] ===== term=\"" + (string.IsNullOrEmpty(term) ? "(all)" : term) +
            "\" activePatterns=[" + string.Join(",", VfxHideAllMaps) + "] + map=[" +
            string.Join(",", MapSpecificVfxPatterns(barrierPassTerritory)) + "] =====");
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.Vfx, out var m) || m == null || m->Value == null)
                continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                total++;
                string path = "";
                try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                bool pathless = path.Length == 0;
                if (pathless) { path = "(no path)"; noPath++; }
                // A term filter can't match "(no path)", so pathless instances are always shown - they're
                // exactly the ones that would otherwise disappear and be misread as "not a layout VFX".
                if (!pathless && !string.IsNullOrEmpty(term) && !path.Contains(term)) continue;

                bool wouldHide = false;
                if (!pathless)
                {
                    foreach (var mk in VfxHideAllMaps) if (path.Contains(mk)) { wouldHide = true; break; }
                    if (!wouldHide)
                        foreach (var mk in MapSpecificVfxPatterns(barrierPassTerritory))
                            if (path.Contains(mk)) { wouldHide = true; break; }
                }
                if (wouldHide) matched++;

                var gfx = inst->GetGraphics();
                string visTxt;
                if (gfx == null) { visTxt = "gfx=NULL"; noGfx++; }
                else visTxt = ((DrawObject*)gfx)->IsVisible ? "visible " : "hidden  ";

                shown++;
                log.Information("[HMSync] [VFXDUMP] " + (wouldHide ? "MATCH  " : "       ") +
                    visTxt + " " + path);
            }
        }
        log.Information("[HMSync] [VFXDUMP] ===== " + total + " layout-VFX instances, " + shown +
            " listed, " + matched + " matched, " + noPath + " pathless, " + noGfx + " with null graphics.");
        log.Information("[HMSync] [VFXDUMP] READ: effect listed + not MATCH -> copy a substring of its path. " +
            "Effect listed but gfx=NULL -> layout instance whose leaf can't be flagged. " +
            "Effect NOT listed at all -> it is ACTOR/SYSTEM VFX (VfxManager), which has no layout-graph " +
            "presence; this suppression path can never hide it and a different mechanism is required. =====");
    }

    private unsafe int HideBarrierVfx(bool includeMapSpecific)
    {
        int hid = 0;
        var lw = LayoutWorld.Instance();
        if (lw == null) return 0;
        var mapPatterns = includeMapSpecific ? MapSpecificVfxPatterns(barrierPassTerritory) : System.Array.Empty<string>();
        foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
        {
            if (layout == null) continue;
            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.Vfx, out var m) || m == null || m->Value == null)
                continue;
            foreach (var kv in *m->Value)
            {
                var inst = kv.Item2.Value;
                if (inst == null) continue;
                string path = "";
                try { var cs = inst->GetPrimaryPath(); if (cs.HasValue) path = cs.ToString(); } catch { }
                if (path.Length == 0) continue;
                bool match = false;
                foreach (var mk in VfxHideAllMaps) if (path.Contains(mk)) { match = true; break; }
                if (!match)
                    foreach (var mk in mapPatterns) if (path.Contains(mk)) { match = true; break; }
                if (!match) continue;
                var gfx = inst->GetGraphics();
                if (gfx != null) { ((DrawObject*)gfx)->IsVisible = false; hid++; }
            }
        }
        return hid;
    }

    private readonly Dictionary<uint, (byte vf, ulong layerMask)> barrierSavedState = new();

    // Restore all barrier colliders to their saved LayerMask/VisibilityFlags (on stop/leave/zone-change).
    private unsafe void RestoreBarrierColliders()
    {
        if (barrierSavedState.Count == 0 && precisionSavedRot.Count == 0 && roadCloneColliders.Count == 0) return;
        try
        {
            if (barrierSavedState.Count > 0)
            {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var bgModule = fw != null ? fw->BGCollisionModule : null;
            var sceneMgr = bgModule != null ? bgModule->SceneManager : null;
            var sceneWrapper = sceneMgr != null ? sceneMgr->FirstScene : null;
            var scene = sceneWrapper != null ? sceneWrapper->Scene : null;
            if (scene != null)
            {
                foreach (var col in scene->Colliders)
                {
                    if (col == null) continue;
                    uint ownerKey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                    if (barrierSavedState.TryGetValue(ownerKey, out var saved))
                    {
                        col->VisibilityFlags = saved.vf;
                        col->LayerMask = saved.layerMask;
                    }
                }
            }
            }   // end if (barrierSavedState.Count > 0)
        }
        catch (Exception ex) { log.Warning("[HMSync] RestoreBarrierColliders skipped: " + ex.Message); }

        // v0.7.342: restore any rotated doors (o1e1) to their saved rotation. Independent of the collider guard above
        // (rotation is tracked in precisionSavedRot/precisionRotatedInstances, a separate set). Walk BgParts, match by
        // instance key, write the original quaternion back, and re-tick the transform.
        if (precisionSavedRot.Count > 0)
        {
            try
            {
                var lw = LayoutWorld.Instance();
                if (lw != null)
                {
                    foreach (var layout in new[] { lw->ActiveLayout, lw->GlobalLayout })
                    {
                        if (layout == null) continue;
                        if (!layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var m) || m == null || m->Value == null)
                            continue;
                        foreach (var kv in *m->Value)
                        {
                            var inst = kv.Item2.Value;
                            if (inst == null) continue;
                            if (!precisionRotatedInstances.Contains((nint)inst)) continue;
                            if (!precisionSavedRot.TryGetValue(inst->Id.InstanceKey, out var origRot)) continue;
                            var dgfx = GetEffectiveGraphics(inst);
                            if (dgfx == null) continue;
                            var o = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)dgfx;
                            o->Rotation = origRot;
                            dgfx->UpdateTransforms(false); dgfx->UpdateCulling();
                        }
                    }
                }
            }
            catch (Exception ex) { log.Warning("[HMSync] door-rotation restore skipped: " + ex.Message); }
            precisionSavedRot.Clear();
            precisionRotatedInstances.Clear();
        }

        barrierSavedState.Clear();
        RemoveRoadClones();   // v0.7.353: drop the 1345 road-mesh clones on stop
    }

    private int KillHiddenColliders()
    {
        int colHit = 0;
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var bgModule = fw != null ? fw->BGCollisionModule : null;
            var sceneMgr = bgModule != null ? bgModule->SceneManager : null;
            var sceneWrapper = sceneMgr != null ? sceneMgr->FirstScene : null;
            var scene = sceneWrapper != null ? sceneWrapper->Scene : null;
            if (scene != null)
            {
                foreach (var col in scene->Colliders)
                {
                    uint ownerKey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                    if (hiddenInstanceKeys.Contains(ownerKey))
                    {
                        if (!colliderSavedState.ContainsKey(ownerKey))
                            colliderSavedState[ownerKey] = (col->VisibilityFlags, col->LayerMask);
                        // only count + act if not already zeroed (avoids re-counting on every re-fire)
                        if (col->LayerMask != 0 || (col->VisibilityFlags & 0x1) != 0)
                        {
                            col->VisibilityFlags &= unchecked((byte)~0x1);
                            col->LayerMask = 0;
                            colHit++;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] KillHiddenColliders skipped: " + ex.Message); }
        return colHit;
    }

    private void DeDrawFinishColliders(int hidden)
    {
        try
        {
            int colHit = KillHiddenColliders();
            if (!quietDeDraw) DiagLog("[HMSync] Furniture de-draw: " + hidden + " hidden, " + colHit + " colliders disabled");

            // S152: suppress the white interaction arrows in the SAME deferred pass as the
            // mesh hide (was previously in LoadZone's synchronous pre-load loop, which desynced
            // from the deferred mesh-hide on map-hops → gates' arrows reappeared). Runs here so
            // mesh + arrow suppression happen together on settled object state, and re-fires
            // with late waves.
            SuppressHousingArrows();
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] Housing furniture de-draw skipped: " + ex.Message);
        }
    }

    // S152: clear the IsTargetable bit on housing EventObjects (drops the white interaction
    // arrows). Moved out of LoadZone's pre-load loop into the deferred de-draw (see S152 note
    // there). RE-FIRE-SAFE: only records an object's ORIGINAL flags if we haven't already saved
    // them - otherwise a re-fire (late wave) after suppression would save the already-cleared
    // value as "original" and restore would put back 0, permanently breaking targetability.
    // We key the saved-set by object index.
    private void SuppressHousingArrows()
    {
        try
        {
            var localPlayer = objectTable.LocalPlayer;
            foreach (var obj in objectTable)
            {
                if (localPlayer != null && obj == localPlayer) continue;
                var native = (GameObject*)obj.Address;
                if (native == null) continue;
                if (native->ObjectKind != ObjectKind.HousingEventObject) continue;
                if (((byte)native->TargetableStatus & (byte)ObjectTargetableFlags.IsTargetable) == 0)
                    continue; // already not targetable (or already suppressed by us)

                ushort idx = (ushort)obj.ObjectIndex;
                // Only save the original if we don't already have it (re-fire safety).
                bool alreadyTracked = false;
                foreach (var (uidx, _) in untargetedObjects)
                    if (uidx == idx) { alreadyTracked = true; break; }
                if (!alreadyTracked)
                    untargetedObjects.Add((idx, (byte)native->TargetableStatus));

                native->TargetableStatus = (ObjectTargetableFlags)(
                    (byte)native->TargetableStatus & ~(byte)ObjectTargetableFlags.IsTargetable);
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] Arrow suppression skipped: " + ex.Message);
        }
    }

    // v0.7.335: a peer that binds AFTER the load sweep already hid them (the late-join case: they were a surrounding
    // real player when we loaded, so LoadZone's non-peer hide added them to hiddenObjectIndices with RenderFlags 0x02).
    // ActorVisibility.RegisterPeer clears its OWN set but not this one, so the load-sweep hide bit was never cleared →
    // the joiner stayed invisible on an existing member despite binding+syncing fine. Clear the bit + drop the index so
    // the later restore doesn't touch them. Idempotent; no-op if they weren't hidden by the load sweep.
    public unsafe void UnhidePreservedObject(ushort idx)
    {
        if (!hiddenObjectIndices.Remove(idx)) return;
        var obj = objectTable[(int)idx];
        if (obj == null) return;
        var native = (GameObject*)obj.Address;
        native->RenderFlags &= ~(VisibilityFlags)0x02;
        log.Information("[HMSync] Late-join: cleared load-sweep hide on [" + idx + "] " + obj.Name);
    }

    public void LoadZone(uint territoryId, HashSet<ushort> sessionPeerIndices, Vector3? spawnOverride = null, float? facingOverride = null)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            log.Error("[HMSync] Cannot load zone: no local player");
            return;
        }

        IsTransitioning = true;
        ZoneWillChange?.Invoke();   // S320: sanitise carry-across state (carpet off) before the HMS load
        // S187/S189: a hop frees the prior map's scene; suppress the target scan across the
        // transition window so TargetSystem.Update can't walk a freed on-screen object.
        SuppressTargetScanUntilReady();

        // S285: capture ORIGIN state once per session - gated on savedZoneId == null (the direct "no
        // origin recorded yet" signal), NOT on a rendering flag, so the origin can't be clobbered by a
        // mid-session map hop (2nd+ /hms load finds savedZoneId already set → skips) and can't drift
        // from the IsZoneLoaded flag. This is what guarantees: start in 983 → hop 1345 → hop 1161 →
        // /hms stop returns to 983, not 1161. Position is read from the NATIVE GameObject.Position (the
        // Dalamud wrapper can read stale/zero mid-transition), captured HERE before the foreign zone
        // load + spawn SetPosition below - so it's your true origin coords while you're still standing
        // in the origin zone.
        if (savedZoneId == null)
        {
            savedZoneId = GetCurrentTerritoryId();
            var lpNative = (GameObject*)localPlayer.Address;
            var np = lpNative->Position;
            savedPosition = new Vector3(np.X, np.Y, np.Z);
            savedRotation = localPlayer.Rotation;
            ReportDebug("[HMSync] Saved ORIGIN: zone=" + savedZoneId + " pos=(" +
                np.X.ToString("F2") + ", " + np.Y.ToString("F2") + ", " + np.Z.ToString("F2") + ")");
        }
        else
        {
            ReportDebug("[HMSync] Map hop (origin retained: zone=" + savedZoneId + ") - origin coords NOT updated.");
        }
        // S153: a map-hop (consecutive /hms load in-session) is now treated IDENTICALLY to a
        // fresh load - NO restore-before-redraw. Previously we restored the previous map's
        // furniture first, then re-de-drew. That un-hide/re-hide round-trip was the source of
        // the shared-Bg residual bug: 1011→1012 (different Bg) un-hid, then the new de-draw had
        // to re-catch furniture mid-stream, and in the shared-residency window pieces slipped
        // through (1011→1012 partial reappearance). A hop never goes "home" - it goes to another
        // de-drawn map and will hide furniture again immediately - so restoring first is wasted
        // work that only creates the race. We restore ONLY on Revert (actual /hms stop / leave).
        // Tracking lists are cleared at the top of each DeDrawHousingFurniture pass, so dropping
        // the hop-restore loses nothing (no list bloat, no orphaned tracking). savedZoneId is
        // preserved on a hop (still in-session; only the displayed map changes).

        // Disable draw on everything except self and session peers
        // S125: track what we hide (by object index) so Revert can restore fixtures -
        // house boxes, wall partitions, flooring, lighting, portraits, wallpapers all
        // come through here. The old DisableDraw destroyed their DrawObjects and revert
        // only re-enabled session PEERS, so fixtures came back stripped ("houses
        // unbuilt", blank interiors). RenderFlags hide + flag-clear restore fixes both.
        // Disable draw on everything except self and session peers (NPCs, players,
        // event objects - these have real DrawObjects and DisableDraw works on them,
        // as it always did pre-S124). Housing furniture/fixtures are NOT handled here
        // (their GameObjects have null DrawObjects); that's gated off in S131.
        // S152: arrow suppression (the HousingEventObject IsTargetable clear) MOVED OUT of
        // this synchronous pre-load loop into the DEFERRED de-draw (SuppressHousingArrows,
        // called from DeDrawHousingFurniture). Reason: S150/S151 made the furniture MESH
        // de-draw deferred (post-load, wave-aware), but the arrow suppression stayed here
        // (pre-load) - so on a map-hop they DESYNCED: meshes hidden but the two interactable
        // gates' white arrows reappeared (the partial-reappearance bug). Running both in the
        // same deferred pass keeps mesh-hide and arrow-suppress together on settled object
        // state, and the re-fire-on-late-wave catches arrows in later waves too.
        // S189: hide non-peer objects via DisableDraw - this matches Hyperborea (which uses the
        // same DisableDraw sweep and does NOT crash), so DisableDraw is NOT the cause of the
        // GetPosition+0x24 UAF. (S188's RenderFlags swap was a mis-diagnosis and is reverted.) The
        // real cause is the target system's per-frame on-screen scan walking an object whose
        // DrawObject is being torn down during the zone reload - addressed by ClearTargetScan()
        // at the reload sites, not by changing the hide method here.
        // S191: track every object we DisableDraw() so Revert can re-ENABLE their draw BEFORE the
        // home reload. ROOT CAUSE of the GetPosition+0x24 crashes: DisableDraw destroys an object's
        // DrawObject but leaves it in the live object table; on the stop-reload, EVERY per-frame
        // system that walks the table (TargetSystem, UI3DModule nameplates, …) calls GetPosition on
        // the torn-down object and derefs its null DrawObject. We were suppressing each *reader* one
        // at a time (target scan, then UI3D, …) - whack-a-mole. The fix is to restore the *objects*
        // to a valid drawable state before the reload, so no reader can fault. Hyperborea avoids
        // this only because its revert skips the reload when already home; HMS does a real reload
        // and so must re-enable first.
        // S310: hide non-peer objects via RenderFlags, NOT DisableDraw. ROOT CAUSE of the recurring
        // GetPosition+0x24 crash (confirmed by the latest dump: UI3DModule.UpdateGameObjects → GetPosition
        // on an Aetheryte, faulting on a garbage DrawObject pointer): DisableDraw() DESTROYS the object's
        // DrawObject but leaves the object live in the table. The "re-enable before reload" scheme meant to
        // undo this was stripped in the S192 lean revert and never reinstated (ReEnablePreservedObjects has
        // had no caller since), so on a map-hop EVERY non-peer object - Kugane's Aetheryte included - was
        // left with a freed DrawObject, and the next per-frame system to walk it (UI3DModule, target scan)
        // dereferenced the dangling pointer on the stop-reload. RenderFlags hiding (bit 0x02) hides the
        // object WITHOUT tearing down its DrawObject - the exact reason ActorVisibilityService chose it
        // over DisableDraw - so GetPosition stays valid and no reader can fault. Restored via the existing
        // RenderFlags restore loop over hiddenObjectIndices in Revert; no draw-rebuild race, nothing to
        // re-enable. (Saving the original flag isn't needed: 0x00 = visible is the universal default for
        // these objects, matching ActorVisibilityService's clear-bit Show().)
        hiddenObjectIndices.Clear();
        foreach (var obj in objectTable)
        {
            if (obj == localPlayer) continue;

            var idx = (ushort)obj.ObjectIndex;
            if (sessionPeerIndices.Contains(idx))
            {
                log.Debug("[HMSync] Preserving session peer [" + idx + "] " + obj.Name);
                continue;
            }

            var native = (GameObject*)obj.Address;
            native->RenderFlags |= (VisibilityFlags)0x02; // hide WITHOUT destroying the DrawObject
            hiddenObjectIndices.Add(idx);
        }

        // S149 proved the ordering bug: when LoadZone ran de-draw BEFORE loadZoneHook, on a
        // map-hop GlobalLayout was EMPTY mid-transition (SharedGroup=0 BgPart=0), so de-draw
        // hid nothing; the new map's furniture then streamed in ~40 frames LATER, un-hidden
        // (visibleBgPart=15 - the "surprise furniture"). The timing varied by map, which is
        // why the respawn was inconsistent (1012/1016/1017 yes, 1018 no). Fix: don't de-draw
        // here - arm a poll (below, after the load) that fires de-draw the instant furniture
        // is actually present in GlobalLayout. Correct for ANY streaming speed; also fixes the
        // original intermittent 1010 persist (same root). First-load still works: the poll
        // catches already-resident furniture on frame one.

        // Load zone
        var gameMain = GameMain.Instance();

        // S262: do NOT set up the InstanceContentDirector. Creating it is what brought up the
        // "Duty Information" HUD (name + clock) on a fresh load - the HUD keys off an active content
        // director, not off a Commence. We removed the trigger rather than suppressing the UI.
        // Nothing we ship needs the director: the native load is loadZoneHook.Original below; the
        // AnoMech entry-circle drop (DisableSpawnAreaColliders) and the furniture/wreck de-draw both
        // walk LayoutWorld directly and never touch the director; and the MapEffect-clear path was
        // abandoned (it drives the barriers, not the wreck - see architecture doc §21). AnoMech
        // confirms the native LoadZone does not require InitDirector (its Step 3 is skippable).
        // FinalizeCurrentInstanceContent stays: it tears down any LEFTOVER director (from an older
        // build or a real duty) and hides a stale duty-info HUD, one-shot, on load.
        FinalizeCurrentInstanceContent();

        // S262: director setup is gated behind the runtime research-mode flag (default OFF). OFF =
        // shipping behaviour: no director, no Duty-Info HUD, clean map. ON (/hms debug) = create the
        // director before the native load so the MapEffect/director-update machinery is live for
        // explorer-mode investigation. Toggle without a recompile; preserved capability, not dead code.
        if (ResearchMode)
        {
            SetupInstanceContentForZone(territoryId);
            log.Information("[HMSync] [RESEARCH] InstanceContentDirector set up (research mode ON) - Duty-Info HUD will show.");
        }

        // S320d CRASH A/C FIX (forward path) - DisableDraw every NON-PEER object immediately before the
        // reload, mirroring the S311 fix already present in Revert. The forward LoadZone previously did ONLY
        // the S310 RenderFlags hide-sweep (which cured crash B by PRESERVING DrawObjects) but NOT the
        // DisableDraw-before-reload that crash A/C needs - so the foreign-zone teardown could still corrupt a
        // half-live object as the native unload ran with no loading screen to suspend object updates. That is
        // the recurrence of the SharedGroupLayoutInstance.Deinit AV (StandObjectManager.Update → vf19 →
        // Deinit) seen on a SESSION JOIN from a populated zone under multibox CPU load - same crash class as
        // A/C, but on the forward path, which Revert was already hardened against and a sparse solo
        // /hms load never stressed. Removing every DrawObject CLEANLY first leaves the unload nothing
        // half-built to corrupt; the reload rebuilds the whole set immediately after, so there is no
        // dangling-DrawObject window (no crash-B regression). Peers + the local player are SKIPPED: they're
        // preserved across the load (peers via RenderFlags + ReEnablePreservedObjects; the player persists),
        // they are not part of the zone-layout teardown that races, and DisableDraw'ing them would drop their
        // render with no rebuild.
        foreach (var obj in objectTable)
        {
            if (obj == null || obj == localPlayer) continue;
            if (sessionPeerIndices.Contains((ushort)obj.ObjectIndex)) continue;
            ((GameObject*)obj.Address)->DisableDraw();
        }

        // NB-18-SHIP: arm the per-zone forced layer-filter key (if any) for THIS load only, immediately before the
        // native loader so the synchronous CreateScene consumes it (TT->0 + forced key). Arming here — inside HMS's own
        // loader — is the gate that keeps it off real game visits. De-armed right after as belt-and-suspenders: once the
        // load's CreateScene has consumed it this is a no-op, but it guarantees a stale key can never leak into a later
        // real load even if a given load skipped scene creation. The /hms casttest harness arms the same field manually.
        if (!PendingCastKeyOverride.HasValue && ForcedCastKeys.TryGetValue(territoryId, out var forcedCastKey))
        {
            PendingCastKeyOverride = forcedCastKey;
            log.Information("[HMSync] [CAST] forcing layer-filter key " + forcedCastKey + " for zone " + territoryId + " (TT->0)");
        }

        // NB-19 Phase 1: arm the quest-populace read-spoof for THIS zone (if a policy exists). Unlike the cast key —
        // consumed synchronously inside CreateScene — Gate 2 populace visibility is evaluated across the ASYNC load
        // phases (LoadState 0→6) that run over the following frames, so the spoof must stay live until Revert (it is
        // disarmed there). No-op when the zone has no policy. Same virtual-load-only gate: armed only inside LoadZone.
        ArmQuestSpoof?.Invoke(territoryId);

        loadZoneHook!.Original((nint)gameMain, territoryId, 0, 0, 1, 1);

        PendingCastKeyOverride = null;   // NB-18-SHIP de-arm: CreateScene consumed it above; never leak to a real load

        var setupFunc = Marshal.GetDelegateForFunctionPointer<SetupTerritoryTypeDelegate>(setupTerritoryTypeAddr);
        setupFunc(EventFramework.Instance(), (ushort)territoryId);

        // S150: arm the deferred de-draw poll (only when the feature is on). Fires de-draw
        // once GlobalLayout furniture is present + stable after the load settles.
        if (EnableFurnitureDeDraw)
            ArmDeferredDeDraw();

        // v0.7.263: 1345 wreck-de-draw PURGED. The Meddle-era hand-picked wreck-hiding is gone; 1345 now
        // loads with native geometry (wrecks present, all collision intact). Cleaner identity-based suppression
        // of specific blockers comes later, driven by collider inspection. (ArmWreckRetry no longer called.)

        // Set position. ResolveSpawnPoint is authoritative: curated overrides → planevent.lgb scan
        // (PopRange → ExitRange → Aetheryte → EventNpc → PositionMarker, first valid non-zero wins)
        // → origin. This is what the game itself reads and works for every zone type with no
        // maintenance for new content. (S209's DefaultPosition read was a regression - it returned
        // zero and demoted this working resolver; reverted.)
        var spawn = spawnOverride ?? ResolveSpawnPoint(territoryId);
        var playerNative = (GameObject*)localPlayer.Address;

        // S308: ground the actor BEFORE positioning. If we hop zones mid-mount-flight the actor is in
        // MovementState==Flying, and (proven by the return-bug saga) SetPosition is REJECTED while Flying -
        // so the curated spawn write was silently dropped and the actor kept its flight trajectory into the
        // new zone, snapping out of bounds. Same fix as the stop/return path (S299): briefly mount then
        // instant-dismount to force the native dismount→land transition that exits Flying coherently, so
        // the SetPosition below actually sticks. Only when airborne; harmless coherent dismount otherwise.
        {
            var pc = (Character*)localPlayer.Address;
            if (pc->MoveController.MovementState == MovementStateOptions.Flying)
            {
                pc->Mount.CreateAndSetupMount(1, 0, 0, 0, 0, 0, 0);
                pc->Mount.Flags = 4; // instant delete on dismount (skip animation)
                pc->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0); // native dismount→land, exits Flying
            }
            else if (pc->Mount.MountId != 0)
            {
                pc->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0); // coherent dismount if grounded-but-mounted
            }
        }

        playerNative->SetPosition(spawn.X, spawn.Y, spawn.Z);
        // Apply the user spawn's saved facing, if any. Same proven mechanism as the home-restore SetRotation
        // (S288, below) - an immediate write right after the accepted SetPosition. A synced/curated load passes
        // no facing (null), so behaviour on those paths is unchanged.
        if (facingOverride.HasValue)
            playerNative->SetRotation(facingOverride.Value);


        // S228: drop the dungeon entry-ring barrier. The ring is SharedGroup colliders near spawn
        // (AnoMech-proven mechanism - NOT MapEffect/CollisionBox/director-update, all ruled out).
        // DEFERRED: they stream in over several frames, so retry each frame near the spawn center
        // until found + dropped (PrefabFlags2 & ~0x8, SetColliderActive(false) vfunc 37).
        // S312: ONLY in instanced content. Residential wards (1010-1012) and overworld have no duty
        // barrier, and their near-spawn SharedGroups are streaming estate prefabs - writing their
        // PrefabFlags2 corrupts the instance lifecycle and crashes Deinit when the plot streams out
        // (the /hms load 1010 crash: StandObjectManager.Update → EventObject.vf19 → SharedGroupLayoutInstance.Deinit).
        if (IsInstancedContent(territoryId))
            ArmBarrierRelease(new Vector3(spawn.X, spawn.Y, spawn.Z));

        // v0.7.274: arm the suppression poll on ALL instanced content. The all-maps VFX pass (dext dungeon-exit
        // curtain) runs everywhere; the 1345-specific barrier/terrain/model passes self-gate inside the poll by
        // territory. Adding a new map's barriers = adding its data to the per-map tables, no arm change.
        // v0.7.345: ALSO arm for a cutscene swap stage. A cutscene loads via a DONOR territory (often an apartment,
        // which isn't instanced content), so IsInstancedContent(territoryId) is false and the poll never armed - which
        // is why the o1e1 door pass never ran. The o1e1 branch inside the poll self-gates by ActiveStageBg, so arming
        // it here is safe for every stage.
        // v0.7.380: arm for EVERY HMS-loaded zone, not just instanced content. The VfxHideAllMaps list is
        // meant to apply to any zone HMS loads, but the poll that applies it only armed when
        // IsInstancedContent - so loading a city or open-world map (which HMS does routinely) never ran
        // HideBarrierVfx and no pattern could match. Still HMS-exclusive: LoadZone is HMS's own loader,
        // reachable only via DoLoad (session-gated) or a relay handler, so this never touches the live world
        // map during ordinary play - per the Maintenance Manual §1, riskier levers stay confined to
        // client-only synthetic maps. Heavier passes inside the poll self-gate (terrain/models by
        // territory==1345, o1e1 by ActiveStageBg).
        ArmBarrierSuppress(territoryId);

        IsZoneLoaded = true;
        CurrentLoadedZone = territoryId;
        log.Information("[HMSync] Loaded zone " + territoryId + " at (" +
            spawn.X.ToString("F1") + ", " + spawn.Y.ToString("F1") + ", " + spawn.Z.ToString("F1") + ")");

        // NB-10: restore the REAL zone's chat rules. SetupTerritoryType (above) set GameMain.CurrentTerritoryIntendedUseId
        // (+0x410C) to the VIRTUAL territory's intended-use. The client re-resolves per-map chat permissions from that
        // byte on EVERY chat send (IntendedUse → TerritoryIntendedUse.ChatRule → TerritoryChatRule → per-channel bytes)
        // and refuses to send client-side - the server blocks nothing. So a virtual duty/gaol load INHERITS the zone's
        // restriction ("/tell unavailable while bound by duty", gaol shout/party lockdown). Writing the byte back to the
        // REAL origin zone's intended-use reverts every channel to real-zone rules instantly - and that's the CORRECT
        // semantics, not a bypass: the server routes chat by the REAL zone, so aligning the client removes a FALSE
        // restriction. ID-only write (0x410C); chat reads the ID (disasm-verified). Mechanism briefing: 2026-08-01.
        RestoreRealChatRules();

        // Re-enable draw on session peers after zone load
        ReEnablePreservedObjects(sessionPeerIndices);

        IsTransitioning = false;

        // b53: auto-advance quest-progressive reconstruction zones (759 Doma Enclave) on load, so a virtual load pops
        // the user straight into the FINISHED enclave with no command. Deferred behind a stream-in delay because the
        // advance is the hide→show cycle and needs the geometry present to hide it (SetPosition above just fired; the
        // game streams the area in over the next few seconds). See MaybeAutoAdvance.
        MaybeAutoAdvance(territoryId);
    }

    /// <summary>
    /// S125: reverse the RenderFlags hide on everything we hid (object-table fixtures
    /// + housing furniture). Clears the 0x02 visibility bit; no DrawObject rebuild.
    /// </summary>
    private void RestoreHiddenObjects()
    {
        // b48: disarm the 759 per-frame hold FIRST, so PollHeldHide can't re-hide the very instances
        // this sweep is about to re-show (it re-resolves the same keys each frame). Idempotent.
        ClearHeldHide();
        int restored = 0;
        foreach (var idx in hiddenObjectIndices)
        {
            var obj = objectTable[(int)idx];
            if (obj == null) continue;
            var native = (GameObject*)obj.Address;
            native->RenderFlags &= ~(VisibilityFlags)0x02;
            var draw = native->DrawObject;
            if (draw != null)
                draw->IsVisible = true;
            restored++;
        }
        // S146: STREAMING-SAFE restore. Previously this loop dereferenced the saved raw
        // instance pointers in hiddenLayoutInstances and called HavePrimary() on each. That
        // crashes when the game has freed an instance between hide and restore - which happens
        // in OUTDOOR/streaming zones (residential districts stream estate plots in/out by
        // proximity), leaving our saved pointers dangling. HavePrimary() on a freed pointer
        // derefs garbage → CLR AV on /hms stop (the residential-district crash).
        //
        // Fix: never deref a saved pointer. Re-walk the LIVE GlobalLayout by the instance keys
        // we recorded (hiddenInstanceKeys) and re-show only instances STILL PRESENT in the
        // layout this frame - using freshly-fetched, guaranteed-valid pointers. Instances the
        // game has streamed out simply won't be in the walk, so they're skipped (correct: the
        // game owns their visibility now). Mirrors the exact safe walk DeDrawHousingFurniture
        // uses. Makes /hms stop crash-proof in every zone class, streaming or not.
        // b50: re-show over BOTH ActiveLayout AND GlobalLayout (matches the de-draw hide sweep, which walks
        // `new[]{ ActiveLayout, GlobalLayout }` at lines ~1349/1432/…). RestoreHiddenObjects previously walked ONLY
        // GlobalLayout, so a hide that landed in ActiveLayout (the idnear/idhide research levers prefer ActiveLayout,
        // and on a real streamed zone like 759 ActiveLayout != GlobalLayout) never got re-shown → /hms idshowall and
        // /hms stop left the map torn up. Re-showing an already-visible instance is a no-op, so walking both is safe.
        // Also flip ALL render slots (SetAllSlotsVisible), not just GetGraphics(), to match the all-slot hide.
        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld != null && hiddenInstanceKeys.Count > 0)
        {
            foreach (var layout in new[] { layoutWorld->ActiveLayout, layoutWorld->GlobalLayout })
            {
                if (layout == null) continue;
                foreach (var typeKey in new[] { InstanceType.SharedGroup, InstanceType.BgPart,
                                                InstanceType.Vfx, InstanceType.Light,
                                                InstanceType.IndoorObject, InstanceType.OutdoorObject })
                {
                    if (!layout->InstancesByType.TryGetValuePointer(typeKey, out var mapPtrPtr)
                        || mapPtrPtr == null)
                        continue;
                    var innerMap = mapPtrPtr->Value;
                    if (innerMap == null) continue;
                    foreach (var kv in *innerMap)
                    {
                        var inst = kv.Item2.Value;
                        if (inst == null) continue;
                        // Only re-show instances we actually hid (key matches our hidden set).
                        if (!hiddenInstanceKeys.Contains(inst->Id.InstanceKey)) continue;
                        if (!inst->HavePrimary()) continue;
                        if (SetAllSlotsVisible(inst, true) > 0) restored++;
                    }
                }
            }
        }
        // S146: STREAMING-SAFE collider restore. Previously wrote saved flags to saved raw
        // Collider* pointers - a use-after-free WRITE if streaming freed the collider (even
        // worse than a stale read: it corrupts whatever now owns that memory). Now we re-walk
        // the LIVE BGCollision Scene and restore flags only on colliders STILL PRESENT whose
        // owner key we recorded. Freed colliders aren't in the walk → skipped safely.
        int collidersRestored = 0;
        if (colliderSavedState.Count > 0)
        {
            var nativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var bgModule = nativeFramework != null ? nativeFramework->BGCollisionModule : null;
            var sceneMgr = bgModule != null ? bgModule->SceneManager : null;
            var sceneWrapper = sceneMgr != null ? sceneMgr->FirstScene : null;
            var scene = sceneWrapper != null ? sceneWrapper->Scene : null;
            if (scene != null)
            {
                foreach (var col in scene->Colliders)
                {
                    uint ownerKey = (uint)(col->LayoutObjectId & 0xFFFFFFFF);
                    if (colliderSavedState.TryGetValue(ownerKey, out var saved))
                    {
                        col->VisibilityFlags = saved.vf;
                        col->LayerMask = saved.layerMask;
                        collidersRestored++;
                    }
                }
            }
        }
        // v0.7.265: restore the rule-based barrier colliders on the same sweep.
        RestoreBarrierColliders();
        // S144: restore the saved TargetableStatus on the EventObjects we untargeted
        // (re-enabling the interaction arrows).
        foreach (var (uidx, uflags) in untargetedObjects)
        {
            var obj = objectTable[(int)uidx];
            if (obj == null) continue;
            ((GameObject*)obj.Address)->TargetableStatus = (ObjectTargetableFlags)uflags;
        }
        untargetedObjects.Clear();
        hiddenObjectIndices.Clear();
        hiddenLayoutInstances.Clear();
        prevZoneHiddenInstances.Clear();   // S166

        // S168/S169: restore furniture GameObject RenderFlags. Re-walk the LIVE furniture-manager
        // array AND the global GameObjectManager (safe; no stored-pointer deref) and restore any
        // saved original flags by GameObject*.
        if (furnitureRenderFlagSaved.Count > 0)
        {
            var hmR = HousingManager.Instance();
            var fmR = hmR != null ? hmR->GetFurnitureManager() : null;
            if (fmR != null)
            {
                int rc = fmR->ObjectManager.ObjectArray.ObjectCount;
                for (int i = 0; i < rc; i++)
                {
                    var go = fmR->ObjectManager.ObjectArray.Objects[i].Value;
                    if (go == null) continue;
                    if (furnitureRenderFlagSaved.TryGetValue((nint)go, out var savedFlags))
                        go->RenderFlags = (VisibilityFlags)savedFlags;
                }
            }
            // S169: global manager sweep for the duplicate-cluster furniture GameObjects.
            var gomR = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
            if (gomR != null)
            {
                var arr = gomR->Objects.IndexSorted;
                for (int i = 0; i < arr.Length; i++)
                {
                    var go = arr[i].Value;
                    if (go == null) continue;
                    if (furnitureRenderFlagSaved.TryGetValue((nint)go, out var savedFlags))
                        go->RenderFlags = (VisibilityFlags)savedFlags;
                }
            }
            furnitureRenderFlagSaved.Clear();
        }
        hiddenInstanceKeys.Clear();
        colliderSavedState.Clear();
        log.Debug("[HMSync] Restored " + restored + " hidden, " +
            collidersRestored + " colliders re-enabled");
    }

    /// <summary>
    /// Reload current zone for this client only. Teleport to spawn.
    /// </summary>
    public void ReloadZone(HashSet<ushort> sessionPeerIndices)
    {
        if (!IsZoneLoaded) return;
        LoadZone(CurrentLoadedZone, sessionPeerIndices);
    }

    /// <summary>
    /// Revert to original FC room zone.
    /// </summary>

    // P1 recipe-capture: CreateCutSceneController is a FACTORY - retail playback is driven by the EventScene Play task
    // (Prepare=8 -> Play=7 -> Post=9), so cold-firing spins. Instead we HOOK the factory and let the game replay a
    // cutscene legitimately (Unending Journey in an inn); the hook records the controller and dumps the banked float
    // candidates during REAL playback, so the clock (advances) and speed (the 1.00 that matters) are found empirically.
    private uint captureTargetId;
    private nint capturedController;
    private int cutDumpFrames;
    private unsafe delegate nint CreateCutDelegate(nint self, byte* path, uint id, byte a4);
    private Hook<CreateCutDelegate>? createCutHook;

    public void FireCutscene(uint id)
    {
        captureTargetId = id;
        log.Information("[CUT] armed capture for cutscene " + id + " - now REPLAY it (Unending Journey / an inn). The hook dumps [CUT] floats during playback.");
    }

    private unsafe nint CreateCutDetour(nint self, byte* path, uint id, byte a4)
    {
        var ctrl = createCutHook!.Original(self, path, id, a4);
        log.Information("[CUT] CreateCutSceneController: id=" + id + " a4=" + a4 + " ctrl=0x" + ctrl.ToString("X"));
        if (id == captureTargetId && ctrl != 0)
        {
            capturedController = ctrl; cutDumpFrames = 0;
            framework.Update -= PollCutDump; framework.Update += PollCutDump;
            log.Information("[CUT] CAPTURED target " + id + " - dumping banked float candidates through playback");

            // v0.7.225 (P1 vtable recon): one-shot dump of the controller's vtable so the NEXT build can hook
            // specific slots BY INDEX (the RE-recommended "log every call the game makes on the returned controller").
            // Raw addresses are session-local (ASLR) - their only job here is (a) confirm a vtable exists, (b) tell us
            // slot COUNT, (c) let us pick indices to hook next round. We do NOT hook here - capture-and-observe only.
            try
            {
                var vtbl = *(nint*)ctrl;   // first qword of any polymorphic C++ object = vtable pointer
                if (vtbl != 0)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < 40; i++)
                    {
                        var slot = *(nint*)(vtbl + i * 8);
                        if (slot == 0) break;   // past the end of the vtable
                        sb.Append("[" + i + "]=0x" + slot.ToString("X") + " ");
                    }
                    log.Information("[CUT] VTBL ctrl=0x" + ctrl.ToString("X") + " vtbl=0x" + vtbl.ToString("X") + " slots: " + sb);
                }
                else log.Information("[CUT] VTBL: controller has no vtable pointer (0x0) - not polymorphic?");
            }
            catch (Exception ex) { log.Error("[CUT] VTBL dump failed: " + ex.Message); }
        }
        return ctrl;
    }

    public unsafe void CutStop()
    {
        framework.Update -= PollCutDump;
        var sm = ScheduleManagement.Instance();
        if (sm == null) return;
        var ctrl = sm->CutSceneController;
        sm->CutSceneController = null;                 // unregister first
        if (ctrl != null) { try { ctrl->Dtor(1); } catch { } }   // vf0 Dtor(1) - dispose the stuck controller
        log.Information("[CUT] cutstop: unregistered + disposed controller");
    }

    private unsafe void PollCutDump(IFramework fw)
    {
        cutDumpFrames++;
        if (cutDumpFrames > 1800) { framework.Update -= PollCutDump; log.Information("[CUT] dump ended"); return; }
        if (cutDumpFrames % 10 != 0 || capturedController == 0) return;
        var sm = ScheduleManagement.Instance();
        var b = (byte*)capturedController;
        var sb = new System.Text.StringBuilder();
        for (int off = 0x160; off <= 0x290; off += 4)
        {
            float f = *(float*)(b + off);
            if (f > 0.001f && f < 1000000f && !float.IsNaN(f) && !float.IsInfinity(f))
                sb.Append("0x" + off.ToString("X") + "=" + f.ToString("F3") + " ");
        }
        // v0.7.225 (P1): answer the RE instance's +0x38 question EMPIRICALLY instead of arguing it. Log whether the
        // schedule manager's CutSceneController is registered and whether it's OUR captured pointer. During a legit
        // Unending Journey replay we EXPECT the game to set this to our controller and playing→True (recipe = "the game
        // drives it, we observe"). If it stays null while our pointer is live, the game drives a DIFFERENT controller
        // than the factory returned - a real finding that redirects the whole approach.
        nint smCtrl = sm != null ? (nint)sm->CutSceneController : 0;
        string reg = smCtrl == 0 ? "null" : (smCtrl == capturedController ? "OURS" : "other=0x" + smCtrl.ToString("X"));
        // Named clock candidates (RE-banked): four copies of the session clock at creation; expect ONE to advance
        // during real playback = the clock. Break them out so the climber is obvious at a glance vs the float soup.
        float c218 = *(float*)(b + 0x218), c220 = *(float*)(b + 0x220), c248 = *(float*)(b + 0x248), c280 = *(float*)(b + 0x280);
        float m214 = *(float*)(b + 0x214);   // master-speed candidate
        log.Information("[CUT] f=" + cutDumpFrames
            + " playing=" + (sm != null && sm->IsCutScenePlaying())
            + " +0x38=" + reg
            + " clocks[218/220/248/280]=" + c218.ToString("F2") + "/" + c220.ToString("F2") + "/" + c248.ToString("F2") + "/" + c280.ToString("F2")
            + " m214=" + m214.ToString("F2")
            + " | " + sb);
    }

    public void Revert()
    {
        ActiveStageBg = null;   // v0.7.227: leaving any swap stage - spawns key by territoryId again
        if (!IsZoneLoaded || savedZoneId == null)
        {
            DiagLog($"[HMSync] [RETURN] Nothing to revert (IsZoneLoaded={IsZoneLoaded}, savedZoneId={(savedZoneId.HasValue ? savedZoneId.Value.ToString() : "null")}).");
            return;
        }

        IsTransitioning = true;
        DisarmQuestSpoof?.Invoke();   // NB-19 Phase 1: drop the quest-populace spoof before reloading the real origin zone
        // S284: SINGLE CLEAN RETURN (matched to Hyperborea's Revert) - return = reload the ORIGIN zone
        // + restore the ORIGIN coords. Nothing else. The old EntrySpawn mechanism (snap to the foreign
        // zone's in-bounds spawn pre-reload) was removed: it's unnecessary (your foreign-zone position
        // is irrelevant once we reload the origin zone and set origin coords) and it was the source of
        // the OOB-on-stop bug (its DrawObject gate silently skipped while airborne). savedZoneId +
        // savedPosition (captured at first LoadZone) are the only state the return needs. The mount
        // dismount below is the one guard Hyperborea also keeps (prevents the airborne-mount plunge
        // during the reload frame).
        {
            var lpDiag = objectTable.LocalPlayer;
            Vector3 curPos = lpDiag != null ? lpDiag.Position : default;
            DiagLog($"[HMSync] [RETURN] Revert: cur=({curPos.X:F1},{curPos.Y:F1},{curPos.Z:F1}) → origin zone {savedZoneId} coords " +
                (savedPosition.HasValue ? $"({savedPosition.Value.X:F1},{savedPosition.Value.Y:F1},{savedPosition.Value.Z:F1})" : "NULL"));
        }
        // S299: GROUND an on-foot flyer the way the game grounds a MOUNTED flyer - via the native
        // dismount sequence. Confirmed root cause: while MovementState==Flying, SetPosition is REJECTED
        // (writes never stick - 10s timeout proved it). Hyperborea has the identical bug and "solves" it
        // only by dismounting (CreateAndSetupMount(0)) - which works for MOUNTED flight because the
        // dismount runs the coherent flight→ground transition. On foot there's no mount, so dismount is a
        // no-op and we stay Flying forever. Fix: briefly put the player ON a mount, then instant-dismount
        // (Flags=4) - forcing the native dismount→land transition that exits Flying coherently (state +
        // velocity + flags all cleared by the GAME, no manual poking → no paralysis). Then the reload +
        // SetPosition land cleanly. Only do this if actually airborne.
        try
        {
            var lpEarly = objectTable.LocalPlayer;
            if (lpEarly != null)
            {
                var pNative = (Character*)lpEarly.Address;
                var mvBefore = pNative->MoveController.MovementState;
                DiagLog($"[HMSync] [RETURN] pre-reload: MovementState={mvBefore} MountId={pNative->Mount.MountId} Mode={pNative->Mode}.");
                if (mvBefore == MovementStateOptions.Flying)
                {
                    // Mount (any flyable mount id; 1 = company chocobo, universally owned) then instant-dismount.
                    pNative->Mount.CreateAndSetupMount(1, 0, 0, 0, 0, 0, 0);
                    pNative->Mount.Flags = 4; // instant delete mount on dismount (skip animation)
                    pNative->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0); // native dismount→land transition
                    DiagLog($"[HMSync] [RETURN] forced mount→instant-dismount to ground; MovementState now {pNative->MoveController.MovementState}.");
                }
                else
                {
                    pNative->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0); // harmless coherent dismount if mounted
                }
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] [RETURN] pre-reload grounding threw: " + ex.Message); }

        // S192: LEAN REVERT - matched to Hyperborea's shape, which reverts foreign zones without
        // the GetPosition+0x24 crash and without any EnableDraw gymnastics. Hyperborea's revert is
        // just: (optional) clear mount, SetPosition home, then reload if not already home. The
        // extra object-manipulation HMS did here (re-enable loops, etc.) was the likely source of
        // the broken-object window. We keep ONLY: target-scan suppression (cheap, proven to help),
        // disarm pending de-draw, the reload + setupTerritory, and position/rotation restore.
        // Stripped: the S191 EnableDraw loop and the per-object churn. The home reload rebuilds the
        // whole object set itself (as it does for Hyperborea), so the DisableDraw'd foreign objects
        // are destroyed by the zone change - no dangling DrawObjects to fault on.

        // S243 HYPOTHESIS TEST - target-scan suppression REMOVED from revert.
        // New diagnosis: the suppressor may be the CAUSE, not a cure. It zeroes
        // TargetableObjectsOnScreen.Length (and the 3 filter arrays) EVERY FRAME. But Length=0
        // doesn't mean "skip the scan" - it means "the cached on-screen list is empty, REBUILD
        // it", which forces the game's scan to re-walk the ENTIRE live object table through
        // SpaceFilter every frame. During the home-reload's construction window that table
        // contains mid-construction incoming Aetherytes (destination-density dependent - Limsa's
        // huge plaza crashes where lighter zones don't), and SpaceFilter's GetPosition derefs
        // their half-built DrawObject → the exact crash. Hyperborea does the SAME DisableDraw and
        // the SAME SetupTerritory reload but NEVER touches the TargetSystem arrays - and doesn't
        // crash. So HMS's per-frame Length-zeroing is the likely accelerant: it forces a full
        // re-scan at the worst moment. Old builds crash too because this suppressor is old (S189).
        //
        // Fix-to-test: do NOT arm suppression on revert. Make HMS revert match Hyperborea's shape
        // exactly - no TargetSystem manipulation - and let the game's natural scan cadence handle
        // the reload. Also actively DISARM any suppression still running from the load side, so it
        // can't keep zeroing arrays into the reload window. If the suppressor was the accelerant,
        // this fixes it; if not, we've lost nothing (it demonstrably wasn't preventing this crash).
        if (targetScanSuppressArmed)
        {
            targetScanSuppressArmed = false;
            framework.Update -= PollTargetScanSuppress;
            log.Information("[HMSync] [REVERT] DISARMED target-scan suppression (S243: testing it as the crash accelerant).");
        }
        // Clear single-target pointer slots once (cheap, safe - does NOT zero array Lengths, so it
        // does NOT force a re-scan). This is the Hyperborea-safe subset: null the targets, touch
        // nothing that makes the scan re-walk the table.
        try
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts != null)
            {
                ts->Target = null;
                ts->SoftTarget = null;
                ts->GPoseTarget = null;
                ts->MouseOverTarget = null;
                ts->MouseOverNameplateTarget = null;
                ts->FocusTarget = null;
                ts->PreviousTarget = null;
            }
        }
        catch (Exception ex) { log.Debug("[HMSync] [REVERT] target-pointer clear skipped: " + ex.Message); }


        // Disarm any pending deferred de-draw so it can't hide furniture in the reverted home zone.
        if (deferredDeDrawArmed)
        {
            deferredDeDrawArmed = false;
            framework.Update -= PollDeferredDeDraw;
        }

        // v0.7.270: disarm the persistent barrier/wep suppress (it no longer self-disarms - it runs the whole
        // session to keep re-streamed wep models hidden). Must stop on leave so it doesn't fire in the home zone.
        if (barrierSuppressArmed)
        {
            barrierSuppressArmed = false;
            framework.Update -= PollBarrierSuppress;
        }

        // Restore RenderFlag-hidden fixtures (cheap, idempotent).
        RestoreHiddenObjects();

        // S242 CRASH FIX - the actual root cause, finally. The GetPosition+0x24 fault is
        // TargetSystem.Update → SpaceFilter (the targeting frustum walk) calling GetPosition on
        // a foreign object whose DrawObject HMS destroyed via DisableDraw on load. The previous
        // theory (target-scan ARRAY suppression) targets the wrong mechanism: SpaceFilter walks
        // the live object table through the targetable filter, NOT the TargetableObjectsOnScreen
        // array we were zeroing - which is why no amount of array-poking or settle-hold tuning
        // stopped it (six builds). The dump's stack (SpaceFilter.vf1 → IsMountOrOrnament →
        // GetPosition, RDI = Aetheryte vtable) makes this explicit.
        // S310: the hide-sweep now uses RenderFlags (restored by RestoreHiddenObjects above), not
        // DisableDraw - so there is no destroyed-DrawObject state to undo here before the reload.

        // The reload - genuine zone load back home. Rebuilds the home territory + object set.
        var gameMain = GameMain.Instance();

        // S224: tear down the dungeon's instance content before returning home (home has no
        // content ID, so no setup needed). Prevents instance-content leaking across the return.
        FinalizeCurrentInstanceContent();

        var layoutBefore = LayoutWorld.Instance()->ActiveLayout;
        uint ttBefore = layoutBefore != null ? layoutBefore->TerritoryTypeId : 0;

        // S288: capture restore target BEFORE we null the saved state, then DEFER the actual SetPosition
        // to a poll that fires once the home zone has settled (see ArmHomeRestore). The synchronous
        // write here was always clobbered by the load's async settle.
        uint restoreZone = savedZoneId.Value;
        Vector3? restorePos = savedPosition;
        float? restoreRot = savedRotation;

        // The reload - genuine zone load back home. Rebuilds the home territory + object set. Normally only if
        // we're not already in the target zone - BUT a cutscene stage borrows the origin's territory id, so
        // ttBefore == restoreZone even though the live SCENE is the stage. Force the reload in that case so the
        // origin's CreateScene fires and the stage geometry is actually torn down.
        if (ttBefore != restoreZone || cutsceneSceneActive)
        {
            DiagLog("[HMSync] [RETURN] reloading home zone " + restoreZone + " (TT before = " + ttBefore + ")");

            // S311 CRASH A FIX - DisableDraw EVERY object immediately before the reload, exactly as
            // Hyperborea does in Utils.LoadZone. This is the counterpart to the S310 RenderFlags change,
            // and the two solve DIFFERENT crashes:
            //   • Crash B (FIXED by S310): a per-frame READER (UI3DModule/TargetSystem) calling GetPosition
            //     on an object whose DrawObject HMS had DESTROYED via the load-time DisableDraw sweep.
            //     Cured by hiding via RenderFlags instead (DrawObject preserved → readers safe).
            //   • Crash A (THIS fix): GameObjectManager.Update → GameObject.Update → vf38 (draw-setup)
            //     dereferencing a draw SUB-component the game's own zone-unload had already nulled, because
            //     the reload tears down draw state in the SAME tick the object is still being updated
            //     (RAX=0, fault at [null+0xA8], RSI=Aetheryte vtable). A normal teleport avoids this via the
            //     loading-screen state that suspends object updates; a direct LoadZone call does not.
            // The cure is to remove every DrawObject CLEANLY (DisableDraw) right before the teardown, so
            // when GameObjectManager.Update runs vf38 during the unload there is no live draw state left to
            // half-null. RenderFlags-hiding (S310) deliberately PRESERVED the DrawObject, which fixed the
            // reader crash but left the draw state intact for the unload to corrupt - hence A persisted.
            // DisableDraw here is safe: the reload rebuilds the entire object/draw set immediately after,
            // and the home-restore poll re-establishes the actor; nothing reads these DrawObjects between
            // this call and the rebuild (the readers that caused B run AFTER the rebuild, on fresh objects).
            foreach (var obj in objectTable)
            {
                if (obj == null) continue;
                ((GameObject*)obj.Address)->DisableDraw();
            }

            loadZoneHook!.Original((nint)gameMain, restoreZone, 0, 0, 1, 1);
            var setupFunc = Marshal.GetDelegateForFunctionPointer<SetupTerritoryTypeDelegate>(setupTerritoryTypeAddr);
            setupFunc(EventFramework.Instance(), (ushort)restoreZone);
        }
        else
        {
            DiagLog("[HMSync] [RETURN] already in home zone " + restoreZone + " - no reload needed.");
        }

        // S288: arm the deferred restore - waits until the actor is settled in restoreZone, then writes
        // the home position and reasserts it for a short window to beat the load's late settle write.
        if (restorePos.HasValue)
            ArmHomeRestore(restoreZone, restorePos.Value, restoreRot);
        else
            ReportDebug("[HMSync] [RETURN] restore SKIPPED: savedPosition NULL → actor left at reload's default spawn (the OOB symptom).");

        IsZoneLoaded = false;
        CurrentLoadedZone = 0;
        savedZoneId = null;
        savedPosition = null;
        savedRotation = null;

        IsTransitioning = false;
        DiagLog("[HMSync] [RETURN] reload issued - applying home position once zone settles.");
    }

    // S288: deferred home-position restore. Polls each frame after Revert until the actor has settled in
    // the home territory (ActiveLayout TT == target AND LocalPlayer present), then SetPositions home and
    // REASSERTS for a short window (the load's async settle can write one more time after we land). This
    // beats the clobber that made every synchronous restore fail.
    private void ArmHomeRestore(uint zone, Vector3 pos, float? rot)
    {
        homeRestoreZone = zone;
        homeRestorePos = pos;
        homeRestoreHasRot = rot.HasValue;
        homeRestoreRot = rot ?? 0f;
        homeRestoreTicks = 0;
        if (homeRestoreArmed) return;
        homeRestoreArmed = true;
        framework.Update += PollHomeRestore;
    }

    private int homeRestoreWrites;
    private void PollHomeRestore(IFramework fw)
    {
        homeRestoreTicks++;
        // Hard timeout ~5s (300 frames @60fps) so we never poll forever.
        if (homeRestoreTicks > 600)
        {
            homeRestoreArmed = false;
            homeRestoreWrites = 0;
            homeRestoreStable = 0;
            framework.Update -= PollHomeRestore;
            ReportDebug($"[HMSync] [RETURN] home-restore TIMED OUT after {homeRestoreTicks}f - writes never stuck (something still pinning position).");
            OnHomeRestoreComplete?.Invoke(); // open the packet filter even on timeout (never leave it stuck on)
            return;
        }
        // Wait until we're actually in the home territory with a live actor.
        uint tt = GetCurrentTerritoryId();
        var lp = objectTable.LocalPlayer;
        if (tt != homeRestoreZone || lp == null) return;

        // S299: the pre-reload mount→instant-dismount grounds us coherently (exits Flying). It may take a
        // few frames for the dismount transition to land, so wait for MovementState==Normal (read-only,
        // never written), THEN SetPosition - write sticks AND controller is healthy (legs work).
        var mv = ((Character*)lp.Address)->MoveController.MovementState;
        if (mv != MovementStateOptions.Normal)
        {
            if (homeRestoreTicks % 30 == 0)
                DiagLog($"[HMSync] [RETURN] f{homeRestoreTicks}: waiting to ground (MovementState={mv}).");
            return;
        }

        try
        {
            var player = (GameObject*)lp.Address;
            var before = player->Position;
            float dx = before.X - homeRestorePos.X, dy = before.Y - homeRestorePos.Y, dz = before.Z - homeRestorePos.Z;
            float drift = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);

            if (drift < 1.0f) homeRestoreStable++;
            else homeRestoreStable = 0;

            if (homeRestoreWrites == 0 || homeRestoreTicks % 30 == 0)
                DiagLog($"[HMSync] [RETURN] f{homeRestoreTicks}: grounded, drift={drift:F1} stable={homeRestoreStable} (actor at {before.X:F1},{before.Y:F1},{before.Z:F1}).");

            player->SetPosition(homeRestorePos.X, homeRestorePos.Y, homeRestorePos.Z);
            if (homeRestoreHasRot) player->SetRotation(homeRestoreRot);
            homeRestoreWrites++;
        }
        catch (Exception ex) { log.Warning("[HMSync] [RETURN] home-restore write threw: " + ex.Message); }

        // S293: lock in only once the position has actually HELD for ~10 consecutive frames (drift < 1y).
        // That means whatever was overwriting us (flight physics / transition) has stopped and our write
        // is finally authoritative. ONLY THEN open the packet filter (Hyperborea firewall ordering).
        if (homeRestoreStable >= 10)
        {
            var finalState = ((Character*)lp.Address)->MoveController.MovementState;
            homeRestoreArmed = false;
            homeRestoreWrites = 0;
            homeRestoreStable = 0;
            framework.Update -= PollHomeRestore;
            DiagLog($"[HMSync] [RETURN] home position HELD ({homeRestoreTicks}f, mvState={finalState}) - settled. Opening packet filter.");
            OnHomeRestoreComplete?.Invoke();
        }
    }

    // S192: post-revert housing-state probe. Logs IndoorTerritory presence + furniture ObjectCount
    // each second for ~6s so we can see whether furniture repopulates after the home reload.

    private void ReEnablePreservedObjects(HashSet<ushort> indices)
    {
        int attempts = 0;
        const int maxAttempts = 300;

        void PollDraw(IFramework fw)
        {
            attempts++;
            bool allDone = true;

            foreach (var idx in indices)
            {
                var obj = objectTable[(int)idx];
                if (obj == null) continue;

                var native = (GameObject*)obj.Address;
                if (native->DrawObject == null)
                {
                    if (native->IsReadyToDraw())
                    {
                        native->EnableDraw();
                        log.Debug("[HMSync] Re-enabled draw on [" + idx + "]");
                    }
                    else
                    {
                        allDone = false;
                    }
                }
            }

            if (allDone || attempts >= maxAttempts)
            {
                framework.Update -= PollDraw;
                if (attempts >= maxAttempts)
                    log.Warning("[HMSync] Timed out re-enabling preserved objects");
            }
        }

        framework.Update += PollDraw;
    }

    private nint LoadZoneDetour(nint a1, uint a2, int a3, byte a4, byte a5, byte a6)
    {
        // S320: a zone change NOT initiated by HMS (normal teleport, zone line, login). HMS-driven loads
        // call loadZoneHook.Original directly and bypass this detour, so firing here catches exactly the
        // external transitions - carpet (and any future carry-across state) gets sanitised before arrival.
        ZoneWillChange?.Invoke();
        return loadZoneHook!.Original(a1, a2, a3, a4, a5, a6);
    }


    // ──────────────────────────────────────────────────────────────────────────
    // S145 HOUSINGDIAG [HOUSINGDIAG] - read-only decoration-state dump.
    // Call StartHousingDiag() from a command, then walk into the FC room. Polls every
    // HousingDiagPollInterval frames and dumps a labelled snapshot WHEN STATE CHANGES, so
    // the log is a clean timeline of how the housing system populates on a real entry. No
    // value writes anywhere. Auto-stops after HousingDiagMaxSnapshots.
    public void StartHousingDiag()
    {
        if (housingDiagRunning)
        {
            log.Information("[HOUSINGDIAG] Already running.");
            return;
        }
        housingDiagRunning = true;
        housingDiagFrameCounter = 0;
        housingDiagSnapshotsLeft = HousingDiagMaxSnapshots;
        housingDiagLastSig = "";
        framework.Update += PollHousingDiag;
        log.Information("[HOUSINGDIAG] ===== STARTED. Now perform the test sequence " +
            "(walk into the FC room / fire faux load). Dumps on every state change, auto-stops in ~" +
            (HousingDiagMaxSnapshots * HousingDiagPollInterval / 60) + "s. =====");
    }

    public void StopHousingDiag()
    {
        if (!housingDiagRunning) return;
        housingDiagRunning = false;
        framework.Update -= PollHousingDiag;
        log.Information("[HOUSINGDIAG] ===== STOPPED. =====");
    }

    private void PollHousingDiag(IFramework fw)
    {
        if (!housingDiagRunning) { framework.Update -= PollHousingDiag; return; }

        if (++housingDiagFrameCounter < HousingDiagPollInterval) return;
        housingDiagFrameCounter = 0;

        if (--housingDiagSnapshotsLeft < 0) { StopHousingDiag(); return; }

        try
        {
            DumpHousingState();
            DumpTeardownState();   // S160: front-door-exit teardown probe
        }
        catch (Exception ex)
        {
            log.Error("[HOUSINGDIAG] Threw: " + ex.Message);
        }
    }

    // S160 [TEARDOWN]: focused read-only probe for the front-door-exit teardown question.
    // Logs the indoor-context + furniture-manager state whose BEFORE→AFTER delta across a
    // REAL front-door exit reveals what the game's teardown actually does - so we can replicate
    // that delta on a map-hop instead of intercepting re-spawns. Run /hms housingdiag while
    // standing in the apartment, then walk OUT the front door; the fields that flip are the
    // teardown. Emits only on change (signature) to keep a clean timeline. Grep [TEARDOWN].
    private string lastTeardownSig = "";
    private readonly List<string> housingIdCensus = new();   // S165: housing-instance Id census per de-draw
    private readonly List<string> gfxResolveDiag = new();     // S176: per-instance graphics-resolution path (vfunc/field/null)
    private void DumpTeardownState()
    {
        var hm = HousingManager.Instance();
        var lw = LayoutWorld.Instance();

        bool inside = false; short room = -99; ulong houseId = 0, indoorHouseId = 0;
        int furnCount = -1; bool furnMgrNull = true;
        if (hm != null)
        {
            try { inside = hm->IsInside(); } catch { }
            try { room = hm->GetCurrentRoom(); } catch { }
            try { houseId = hm->GetCurrentHouseId().Id; } catch { }
            try { indoorHouseId = hm->GetCurrentIndoorHouseId().Id; } catch { }
            var fm = hm->GetFurnitureManager();
            furnMgrNull = fm == null;
            if (fm != null)
            {
                try { furnCount = fm->ObjectManager.ObjectArray.ObjectCount; } catch { }
            }
        }

        // Layout side: how many resident layouts, and is an indoor/housing one present?
        int layoutCount = 0; bool anyHousingLayout = false; uint globalTerr = 0;
        if (lw != null)
        {
            if (lw->GlobalLayout != null) globalTerr = lw->GlobalLayout->TerritoryTypeId;
            try
            {
                foreach (var kv in lw->LoadedLayouts)
                {
                    var lm = kv.Item2.Value;
                    if (lm == null) continue;
                    layoutCount++;
                    if (lm->HousingType != 0 || lm->IndoorAreaData != null) anyHousingLayout = true;
                }
            }
            catch { }
        }

        string sig = $"in={inside} room={room} house={houseId} indoor={indoorHouseId} fmNull={furnMgrNull} furn={furnCount} layouts={layoutCount} housingLayout={anyHousingLayout} gTerr={globalTerr}";
        if (sig == lastTeardownSig) return;
        lastTeardownSig = sig;
        log.Information("[TEARDOWN] " + sig);
    }

    // The actual read-only snapshot. Reads both the DATA layer (LayoutManager.IndoorAreaData:
    // wallpaper/floor part IDs + light level) and the RENDER layer (IndoorTerritory brightness
    // + furniture-manager slot count). Builds a short signature first; only emits a full dump
    // when the signature changed since last poll, to keep the log a clean change-timeline.
    private void DumpHousingState()
    {
        var hm = HousingManager.Instance();
        var lw = LayoutWorld.Instance();
        var lm = lw != null ? lw->ActiveLayout : null;

        // ── territory header ──
        string terrType = "none";
        string terrLoaded = "?";
        uint terrId = lm != null ? lm->TerritoryTypeId : 0u;
        HousingTerritory* terr = null;
        if (hm != null)
        {
            terr = hm->CurrentTerritory;
            if (terr != null)
            {
                terrType = terr->GetTerritoryType().ToString();
                terrLoaded = terr->IsLoaded().ToString();
            }
        }

        // ── DATA layer: IndoorAreaLayoutData (wallpaper/flooring/light level) ──
        // Lives on LayoutManager.IndoorAreaData (pointer @ 0x0B0). We read it from BOTH
        // ActiveLayout AND GlobalLayout - we don't yet know which carries it after our faux
        // reload, and that's exactly what the dump should reveal. Also dump the game's own
        // HousingLayoutDataUpdatePending flag (@0x104) - that's the signal the game sets when
        // housing layout data needs (re)applying; if our revert leaves it false while a real
        // entry sets it true, that flag may BE the lever.
        string dataLine = DumpIndoorAreaData("ActiveLayout", lm);
        string dataLineG = DumpIndoorAreaData("GlobalLayout", lw != null ? lw->GlobalLayout : null);
        string sigData = dataLine + "||" + dataLineG;

        // ── RENDER layer: IndoorTerritory brightness + furniture slots ──
        string renderLine = "IndoorTerritory=NULL (not indoor or not loaded)";
        string sigRender = "r:null";
        if (terr != null && terr->GetTerritoryType() == HousingTerritoryType.Indoor)
        {
            var indoor = (IndoorTerritory*)terr;
            int furnitureCount = 0;
            var fm = hm->GetFurnitureManager();
            if (fm != null) furnitureCount = (int)fm->FurnitureVector.LongCount;
            renderLine = "IndoorTerritory: Brightness Current=" + indoor->BrightnessCurrent.ToString("F3") +
                " Target=" + indoor->BrightnessTarget.ToString("F3") +
                " Inverted=" + indoor->InvertedBrightness +
                " SavedInverted=" + indoor->SavedInvertedBrightness +
                " Transitioning=" + indoor->IsBrightnessTransitioning +
                " | GetBrightness=" + hm->GetBrightness() +
                " | FurnitureSlots(populated)=" + furnitureCount;
            sigRender = "r:" + indoor->BrightnessTarget.ToString("F1") + ":" + indoor->InvertedBrightness + ":" + furnitureCount;
        }

        // ── housing-layout dirty flag (scalar read only) ──
        // S145b: REMOVED the SharedGroup/BgPart/Vfx/Light instance-count walk that lived here.
        // CountInstances enumerated InstancesByType and dereferenced instance pointers; run
        // across /hms stop, it read a VfxLayoutInstance mid-teardown and AV'd (vf60 near-null,
        // native crash straight through the managed try/catch). Instance counts were only a
        // cross-reference and we already have them from the first HOUSINGDIAG pass - not worth
        // re-walking a volatile collection during the one window it's being freed. We keep only
        // the dirty-flag, a single bool field read with no traversal - safe at any time.
        string instLine = "GlobalLayout=NULL";
        string sigInst = "i:null";
        if (lw != null && lw->GlobalLayout != null)
        {
            bool updatePending = lw->GlobalLayout->HousingLayoutDataUpdatePending;
            instLine = "HousingLayoutDataUpdatePending=" + updatePending;
            sigInst = "i:" + updatePending;
        }

        // ── BIND check: does SetInteriorFixture have a layout to bind to? ──
        // LayoutWorld.SetInteriorFixture returns 2 (no-op) when there's no ActiveLayout or
        // ActiveLayout->HousingType is unset. So the fixture-replay fix depends entirely on
        // ActiveLayout being the housing layout with HousingType set after revert. This line
        // shows, for BOTH layouts: HousingType (0=none), InitState (7=fully loaded/ready),
        // and TerritoryTypeId - so we can see which layout is active, whether it's the house,
        // and whether SetInteriorFixture would bind or no-op.
        string bindLine = "BIND: (no layouts)";
        string sigBind = "b:null";
        if (lw != null)
        {
            var a = lw->ActiveLayout;
            var g = lw->GlobalLayout;
            string aStr = a == null ? "Active=NULL"
                : "Active{HousingType=" + a->HousingType + " InitState=" + a->InitState + " Terr=" + a->TerritoryTypeId + "}";
            string gStr = g == null ? "Global=NULL"
                : "Global{HousingType=" + g->HousingType + " InitState=" + g->InitState + " Terr=" + g->TerritoryTypeId + "}";
            bindLine = "BIND: " + aStr + " " + gStr;
            sigBind = "b:" + (a == null ? "n" : a->HousingType + "/" + a->InitState + "/" + a->TerritoryTypeId)
                + ":" + (g == null ? "n" : g->HousingType + "/" + g->InitState + "/" + g->TerritoryTypeId);
        }

        // Only dump if something changed (keeps the timeline readable).
        string sig = terrId + "|" + terrType + "|" + terrLoaded + "|" + sigData + "|" + sigRender + "|" + sigInst + "|" + sigBind;
        if (sig == housingDiagLastSig) return;
        housingDiagLastSig = sig;

        log.Information("[HOUSINGDIAG] --- snapshot (terr=" + terrId + " type=" + terrType + " loaded=" + terrLoaded + ") ---");
        log.Information("[HOUSINGDIAG]   DATA(A): " + dataLine);
        log.Information("[HOUSINGDIAG]   DATA(G): " + dataLineG);
        log.Information("[HOUSINGDIAG]   RENDER : " + renderLine);
        log.Information("[HOUSINGDIAG]   INST   : " + instLine);
        log.Information("[HOUSINGDIAG]   BIND   : " + bindLine);
        DumpAppearanceContainer();
    }

    // S145c [HOUSINGDIAG]: dump the HousingInteriorAppearance inventory container (25002) -
    // the PERSISTENT source of interior fixture assignments (walls/windows/door/floor/light
    // per floor). Unlike IndoorAreaData (transient, NULL in steady state), this inventory is
    // resident and stable, so it's the real source to replay from via SetInteriorFixture.
    // This dump confirms the slot→fixture mapping with live data BEFORE we build the replay:
    // we read each slot's ItemId + stain and cross-reference against what's actually on the
    // walls. Inventory read only - no layout traversal, safe across teardown.
    // Expected slot layout (to be CONFIRMED by this dump): per floor (Ground/Second/Cellar/
    // Exterior) the parts Walls/Windows/Door/Floor/Light - matching SetInteriorFixture(floor,part).
    private void DumpAppearanceContainer()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) { log.Information("[HOUSINGDIAG]   APPEAR : InventoryManager=NULL"); return; }

            var c = im->GetInventoryContainer(InventoryType.HousingInteriorAppearance);
            if (c == null) { log.Information("[HOUSINGDIAG]   APPEAR : container=NULL"); return; }
            if (!c->IsLoaded) { log.Information("[HOUSINGDIAG]   APPEAR : container not loaded (Size=" + c->Size + ")"); return; }

            int size = c->Size;
            var sb = new System.Text.StringBuilder();
            sb.Append("APPEAR : HousingInteriorAppearance Size=").Append(size).Append(" [");
            for (int i = 0; i < size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null) { sb.Append(i).Append(":null "); continue; }
                uint id = item->ItemId;
                byte stain = item->GetStain(0);
                sb.Append(i).Append(":id=").Append(id);
                if (stain != 0) sb.Append(",stain=").Append(stain);
                sb.Append(' ');
            }
            sb.Append(']');
            log.Information("[HOUSINGDIAG]   " + sb.ToString());
        }
        catch (Exception ex)
        {
            log.Error("[HOUSINGDIAG]   APPEAR : threw " + ex.Message);
        }
    }

    // Read-only formatter for IndoorAreaLayoutData off a given LayoutManager. Returns a
    // compact one-liner whether the pointer is null or populated, so the dump shows the
    // null→populated transition clearly. Floor0 part IDs = wallpaper/flooring; LightLevel
    // is the data-side light level (distinct from IndoorTerritory's render-side brightness).
    private string DumpIndoorAreaData(string label, LayoutManager* lm)
    {
        if (lm == null) return label + ": LayoutManager=NULL";
        if (lm->IndoorAreaData == null) return label + ": IndoorAreaData=NULL";
        var d = lm->IndoorAreaData;
        var f0 = d->Floor0;
        var f1 = d->Floor1;
        var fExt = d->Exterior;
        return label + ": LightLevel=" + d->LightLevel.ToString("F3") +
            " Floor0=[" + f0.Part0 + "," + f0.Part1 + "," + f0.Part2 + "," + f0.Part3 + "," + f0.Part4 + "]" +
            " Floor1=[" + f1.Part0 + "," + f1.Part1 + "," + f1.Part2 + "," + f1.Part3 + "," + f1.Part4 + "]" +
            " Exterior=[" + fExt.Part0 + "," + fExt.Part1 + "," + fExt.Part2 + "," + fExt.Part3 + "," + fExt.Part4 + "]";
    }

    private uint GetCurrentTerritoryId()
    {
        try
        {
            var layout = FFXIVClientStructs.FFXIV.Client.LayoutEngine.LayoutWorld.Instance()->ActiveLayout;
            if (layout != null) return layout->TerritoryTypeId;
        }
        catch (Exception ex) { log.Debug("[HMSync] GetCurrentTerritoryId failed: " + ex.Message); }
        return 0;
    }

    // NB-10: align GameMain.CurrentTerritoryIntendedUseId with the REAL origin zone so per-map chat restrictions
    // (tells-in-duty, gaol lockdown) don't leak onto virtual loads. See the LoadZone call site for the mechanism.
    // One-shot ID write; if a late load-settle re-clobbers the byte (watch the [CHATRULE] "CLOBBERED" line in-game),
    // escalate to a short reassert poll like ArmHomeRestore. ID-only by design - the cached row ptr (+0x4148) drives
    // non-chat systems and is left alone unless a restriction survives the ID write (then point it at the real row too).
    private unsafe void RestoreRealChatRules()
    {
        try
        {
            if (savedZoneId == null) return;                 // no origin captured yet - nothing to restore to
            var gm = GameMain.Instance();
            if (gm == null) return;

            byte virtualUse = (byte)gm->CurrentTerritoryIntendedUseId;

            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            var row = sheet?.GetRowOrDefault(savedZoneId.Value);
            byte realUse = row != null ? (byte)row.Value.TerritoryIntendedUse.RowId : (byte)1;   // 1 = overworld fallback

            gm->CurrentTerritoryIntendedUseId = (FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse)realUse;
            byte afterUse = (byte)gm->CurrentTerritoryIntendedUseId;

            log.Information("[HMSync] [CHATRULE] IntendedUse virtual=" + virtualUse + " → real=" + realUse +
                " (origin zone " + savedZoneId.Value + "); after write=" + afterUse +
                (afterUse == realUse ? " OK" : " CLOBBERED"));
        }
        catch (Exception ex) { log.Error("[HMSync] [CHATRULE] restore failed: " + ex.Message); }
    }

    /// <summary>
    /// Load curated spawn points. For now, embedded.
    /// Later: parse Hyperborea's data.yaml.
    /// </summary>
    private void LoadCuratedSpawns()
    {
        // S220: dungeon spawns now resolve from planmap.lgb via the ENTRANCE EventObject discriminator
        // (near-flawless across tested dungeons). Hand-written dungeon overrides removed.
        // Cities resolve to semi-random-but-valid in-zone spots (kept - they're fine).
        //
        // NOTE: 128 (Limsa Upper Decks) was previously curated for a multi-level wrong-elevation issue.
        // Removed per cleanup; the entrance-EventObject logic is dungeon-targeted, so a multi-level CITY
        // falls through to the PopRange path. VERIFY Limsa Upper Decks spawns sanely; if it regresses to
        // wrong elevation, either re-add this override or improve the city PopRange elevation pick.
        //
        // (curatedSpawns intentionally left empty - resolver handles all tested zones.)

        log.Information("[HMSync] Curated spawn overrides: " + curatedSpawns.Count);
    }

    public void Dispose()
    {
        if (housingDiagRunning)
        {
            housingDiagRunning = false;
            framework.Update -= PollHousingDiag;
        }
        if (deferredDeDrawArmed)
        {
            deferredDeDrawArmed = false;
            framework.Update -= PollDeferredDeDraw;
        }
        if (targetScanSuppressArmed)
        {
            targetScanSuppressArmed = false;
            framework.Update -= PollTargetScanSuppress;
        }
        if (barrierSuppressArmed)
        {
            barrierSuppressArmed = false;
            framework.Update -= PollBarrierSuppress;
        }
        if (homeRestoreArmed)
        {
            homeRestoreArmed = false;
            framework.Update -= PollHomeRestore;
        }
        createSceneHook?.Dispose();
        createCutHook?.Dispose();
        createCutHook?.Dispose();
        loadZoneHook?.Dispose();
    }
}
