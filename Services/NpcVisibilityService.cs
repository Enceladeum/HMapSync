using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace HMSync.Services;

/// <summary>
/// Host-authoritative NPC scene-cleanup for RP (S328aa). Two independent modes the host can set; the mode is broadcast
/// on the map-state backbone so every session member (and late joiners) sees the same picture, and it works in solo.
///
///   • <b>DespawnNpcs</b> - hide EVENT NPCs entirely (RenderFlags 0x02, the Penumbra-safe hide that keeps the DrawObject
///     intact - same mechanism as the player-hide in ActorVisibilityService). BATTLE NPCs are KEPT, so striking dummies
///     (BattleNpc) survive. Also suppresses the over-head quest marker so a hidden NPC never leaves a floating icon.
///   • <b>HideQuestSigns</b> - keep the NPC bodies (ambience) but null the over-head quest markers (the !/? balloons via
///     GameObject.NamePlateIconId @0x110). A subtler RP-cleanliness option: drop the gamey quest UI, keep the world alive.
///
/// Both modes reuse the furniture/player persistent-watch discipline: NPCs stream in by proximity exactly like furniture,
/// so a one-shot pass misses late arrivals. The throttled Update() re-asserts on everything in range every ~0.5s, and
/// Stop() restores every field we touched (RenderFlags + NamePlateIconId) so nothing leaks past a session.
///
/// EventNpc = ObjectKind 3, BattleNpc = 2 (kept). NamePlateIconId 0 = no marker.
/// </summary>
public unsafe class NpcVisibilityService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private const uint InvisibleFlag = 0x02;

    // The two host modes. Despawn implies hiding the marker too (a hidden NPC's marker would otherwise float). Setting
    // either re-scans; clearing both restores everything. HideQuestSigns is ignored while DespawnNpcs is on (the NPC is
    // already gone, marker with it).
    private bool despawnNpcs;
    private bool hideQuestSigns;

    // NB-20: granular hide - a set of ENpc DataIds (GameObject.BaseId) the host chose to remove. Any live EventNpc whose
    // BaseId is in this set gets the same full hide as DespawnNpcs (body + marker), but selectively. Keying on BaseId
    // (the ENpcResident row, stable across visits/characters/peers) means hiding one id hides every live copy of that
    // NPC kind on the map - which is exactly the wanted behaviour for the recurring-duplicate case, and is what lets the
    // choice sync (object indices are transient table slots and can't). Independent of despawnNpcs; composes with it.
    private readonly HashSet<uint> hiddenDataIds = new();

    private bool active;

    // Restore tracking. For each EventNpc we touched: the object index → its original NamePlateIconId (so HideQuestSigns
    // restores the exact marker, not a guess). RenderFlags restore is a simple bit-clear (we only ever SET 0x02), so a
    // hidden-set is enough to know to clear it. Keyed by object index; restore re-fetches the live object (never derefs
    // a saved pointer - objects stream/re-index).
    private readonly HashSet<ushort> hiddenIndices = new();              // NPCs we RenderFlags-hid
    private readonly Dictionary<ushort, uint> savedNamePlateIcon = new(); // index → original NamePlateIconId (marker hide)

    public NpcVisibilityService(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    /// <summary>Set the host modes (from config/wire). Applies live if a session is active; re-scans on any change.
    /// <paramref name="granularHidden"/> is the per-map set of ENpc DataIds to selectively remove (NB-20); pass null to
    /// leave it empty. Any change to the modes OR the granular set forces a restore-then-reapply so an un-hidden NPC
    /// comes back.</summary>
    public void SetModes(bool despawn, bool questSigns, IEnumerable<uint>? granularHidden = null)
    {
        bool granularChanged = !SameSet(granularHidden);
        bool changed = despawn != despawnNpcs || questSigns != hideQuestSigns || granularChanged;
        despawnNpcs = despawn;
        hideQuestSigns = questSigns;
        hiddenDataIds.Clear();
        if (granularHidden != null)
            foreach (var id in granularHidden) hiddenDataIds.Add(id);
        if (!active || !changed) return;

        // A mode may have turned OFF (or an id removed from the set) - restore anything we touched before re-applying,
        // so a now-unhidden NPC re-appears rather than staying stuck hidden.
        RestoreAll();
        ApplyAll();
    }

    // True if the incoming granular set is identical to the one we already hold (so we can skip a needless re-scan).
    private bool SameSet(IEnumerable<uint>? incoming)
    {
        var next = incoming as ICollection<uint> ?? (incoming == null ? System.Array.Empty<uint>() : new List<uint>(incoming));
        if (next.Count != hiddenDataIds.Count) return false;
        foreach (var id in next) if (!hiddenDataIds.Contains(id)) return false;
        return true;
    }

    /// <summary>Begin acting on NPCs per the current modes. Idempotent.</summary>
    public void Start()
    {
        if (active) return;
        active = true;
        hiddenIndices.Clear();
        savedNamePlateIcon.Clear();
        ApplyAll();
        log.Information("[HMSync] NpcVisibility started (despawn=" + despawnNpcs + ", questSigns=" + hideQuestSigns + ")");
    }

    /// <summary>Stop and restore every NPC field we touched.</summary>
    public void Stop()
    {
        if (!active) return;
        active = false;
        RestoreAll();
        log.Information("[HMSync] NpcVisibility stopped - NPCs restored");
    }

    /// <summary>
    /// Persistent re-assert (the proximity re-scan). NPCs stream in by proximity like furniture, so re-apply on a
    /// throttle so late-arriving NPCs get the same treatment. Also re-hides any NPC whose RenderFlags/marker the game
    /// or another plugin reset. ~0.5s cadence (mirrors ActorVisibility.Update / the furniture persistent scan).
    /// </summary>
    private int throttle;
    public void Update()
    {
        if (!active) return;
        if (!despawnNpcs && !hideQuestSigns && hiddenDataIds.Count == 0) return;   // nothing to maintain
        throttle++;
        if (throttle < 30) return;
        throttle = 0;
        ApplyAll();
    }

    // Apply the current modes to every EventNpc in range. Idempotent (re-setting an already-set flag is harmless; the
    // saved-original is only captured the FIRST time we touch an NPC, so re-runs never clobber the true original).
    private void ApplyAll()
    {
        foreach (var obj in objectTable)
        {
            var native = (GameObject*)obj.Address;
            if (native == null) continue;
            if (native->ObjectKind != ObjectKind.EventNpc) continue;   // KEEP BattleNpc (striking dummies) and everything else
            var idx = (ushort)obj.ObjectIndex;

            // Full hide if either: DespawnNpcs (all), OR this NPC's DataId is in the granular hidden set (NB-20). The
            // granular check keys on BaseId so every live instance of that ENpc kind is hidden together.
            bool fullHide = despawnNpcs || (hiddenDataIds.Count > 0 && hiddenDataIds.Contains(native->BaseId));

            if (fullHide)
            {
                // Full hide. Suppress the marker too (a hidden NPC must not leave a floating icon), and hide the body.
                CaptureMarker(idx, native);
                if (native->NamePlateIconId != 0) native->NamePlateIconId = 0;
                if ((native->RenderFlags & (VisibilityFlags)InvisibleFlag) == 0)
                {
                    native->RenderFlags |= (VisibilityFlags)InvisibleFlag;
                    hiddenIndices.Add(idx);
                }
            }
            else if (hideQuestSigns)
            {
                // Keep the body, drop just the over-head marker.
                CaptureMarker(idx, native);
                if (native->NamePlateIconId != 0) native->NamePlateIconId = 0;
            }
        }
    }

    // Save an NPC's original NamePlateIconId the first time we touch it, so Stop() restores the exact marker. Only the
    // first capture sticks (TryAdd), so a re-scan that sees the icon already zeroed by us doesn't overwrite the saved
    // original with 0.
    private void CaptureMarker(ushort idx, GameObject* native)
    {
        if (!savedNamePlateIcon.ContainsKey(idx))
            savedNamePlateIcon[idx] = native->NamePlateIconId;
    }

    // Restore every field we touched. Re-fetch each object live (objects stream/re-index - never deref a saved pointer).
    private void RestoreAll()
    {
        foreach (var idx in hiddenIndices)
        {
            var obj = objectTable[(int)idx];
            if (obj == null) continue;
            var native = (GameObject*)obj.Address;
            if (native == null) continue;
            native->RenderFlags &= ~(VisibilityFlags)InvisibleFlag;
        }
        hiddenIndices.Clear();

        foreach (var kvp in savedNamePlateIcon)
        {
            var obj = objectTable[(int)kvp.Key];
            if (obj == null) continue;
            var native = (GameObject*)obj.Address;
            if (native == null) continue;
            native->NamePlateIconId = kvp.Value;   // restore the exact original marker
        }
        savedNamePlateIcon.Clear();
    }

    public void Dispose() => Stop();
}
