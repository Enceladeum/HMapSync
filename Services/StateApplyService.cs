using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using HMSync.Sync;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

/// <summary>
/// Single authority for applying all peer actor state: position, rotation,
/// locomotion, emotes, weapon draw, and gaze. Reads from the unified
/// TransformData snapshot — no race conditions between channels.
/// </summary>
public class StateApplyService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;

    // S320e: render-behind interpolation delay. We display peers this far in the past so the two snapshots
    // bracketing the render time are always already received — i.e. we interpolate (smooth, constant-velocity)
    // rather than extrapolate or freeze. ~1.5 × the 100 ms send interval: enough jitter buffer to avoid
    // holds, low enough latency to be socially imperceptible. Larger = more robust to packet loss but laggier.
    private const float PeerInterpDelay = 0.15f;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    // S329b: receiver-side locomotion diagnostic. Null-safe; set by the plugin. Zero cost when not armed.
    public LocoDiagService? LocoDiag { get; set; }

    // S329b: gates the verbose receiver traces (EMOTETRACE/HOLDTRACE/emote-one-shot). Set from the debug-mode config so
    // these only fire when the PLUGIN's debug mode is on — previously they were bare log.Debug() calls, visible whenever
    // DALAMUD's log level was Debug (a separate setting), which is why they showed up unexpectedly. Off by default.
    public bool DebugTrace { get; set; }
    private readonly IDalamudPluginInterface pluginInterface;

    private bool active;

    private readonly ConcurrentDictionary<string, PeerInterpolationState> peerStates = new();
    private readonly ConcurrentDictionary<string, PeerInfo> peerInfos = new();

    // ── Tier 2: Penumbra redraw-lifecycle coordination ──
    // Chair-sit on a Penumbra-managed (glamoured/Mare-synced) actor triggers a full
    // CharacterBase teardown+rebuild (confirmed in xllog: "[Penumbra] [Create CharacterBase]"
    // preceded by a wall of [DEST] lines). During that window the draw object is freed and
    // recreated; writing into it is a native access violation a C# try/catch cannot catch.
    // We subscribe to Penumbra's Creating/Created CharacterBase events (raw IPC by label,
    // no package dependency — same pattern shipping plugins use) and suppress draw-object
    // writes for that specific actor between Creating and Created. Keyed per game-object
    // address so one peer rebuilding never blocks writes to another (matters at N peers).
    // Labels/signatures verbatim from Ottermandias/Penumbra.Api IpcSubscribers/GameState.cs.
    private ICallGateSubscriber<nint, Guid, nint, nint, nint, object?>? penumbraCreatingCharacterBase;
    private ICallGateSubscriber<nint, Guid, nint, object?>? penumbraCreatedCharacterBase;
    private readonly HashSet<nint> rebuildingActors = new();

    // Body-offset write threshold: only re-write the peer's DrawOffset when the desired
    // value differs from the current one by more than this. Collapses per-frame churn so we
    // don't feed Penumbra's redraw watch. (SimpleHeels uses 0.00001f; we keep a slightly
    // looser value — the offset is broadcast at epoch granularity, not sub-mm precision.)
    private const float BodyOffsetWriteEpsilon = 0.01f;

    public IReadOnlyDictionary<string, PeerInfo> Peers => peerInfos;

    // v0.7.357: optional diagnostic hook — when the gpose mount probe is armed, every mount-clear site reports itself
    // so the log shows whether an HMS call coincides with the mount vanishing in gpose. Null in normal operation.
    public GPoseMountProbe? GPoseProbe;

    // COSM_1_016: set by the plugin → SkillSyncService.ReplayOn. Kept as a delegate so StateApplyService doesn't take
    // a new constructor dependency (and so HDM can later point it at the same primitive for NPCs).
    public unsafe delegate void SkillReplayDelegate(Character* caster, uint actionId, byte actionType, System.Numerics.Vector3 targetPos, Character* target);
    public SkillReplayDelegate? SkillReplay;

    // v0.7.367: find OUR local character object carrying this stable ContentId — the peer's puppet, or the local
    // player. Used to point a replayed action's animation at the right person; ContentId is the identity the peer
    // roster already binds on, so it translates across clients where a raw entity id cannot.
    private unsafe Character* FindCharacterByContentId(ulong contentId)
    {
        if (contentId == 0) return null;
        try
        {
            foreach (var obj in objectTable)
            {
                if (obj == null) continue;
                var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                if (go == null || go->ObjectKind != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc) continue;
                var ch = (Character*)obj.Address;
                if (ch->ContentId == contentId) return ch;
            }
        }
        catch { }
        return null;
    }

    // Unconditionally drop the roster. Called on entering a fresh room (RoomJoined) and on teardown — safe whether or
    // not the apply loop is active (unlike Stop's clear, which is active-gated and misses the two-phase lobby).
    public void ClearRoster() { peerInfos.Clear(); departedOrigins.Clear(); }   // v0.7.370: departed origins are session-scoped

    // S322j: user-facing chat sink (wired to chat.Print by the plugin, like the other services' StatusReport).
    public Action<string>? Notify { get; set; }

    // S326: host map-state apply. The plugin wires this to apply weather/time/BGM/NPC from the host's stream on the
    // framework thread. lastAppliedMapEpoch gates it to once per epoch (the host bumps the epoch on each change).
    public Action<TransformData>? ApplyMapState { get; set; }

    // S330c (Stage 2b, HOST-epoch-continuity): the last map-state block this client APPLIED as a guest (values + epoch).
    // When this client is promoted to host, it must INHERIT this — both the epoch (so its outbound HostUpdates continue
    // the sequence rather than restarting) AND the values (so it doesn't stamp default/empty map-state and wipe the
    // scene's weather/time/BGM for everyone). Null until a HostUpdate has been applied. Read at promotion.
    public TransformData? LastAppliedMapState { get; private set; }

    // S327j: force the NEXT received transform to re-apply the host's full map-state (weather/time/BGM), by clearing
    // the last-applied epoch. Used after a guest zone-load so the freshly-loaded map picks up the host's held time even
    // if the host's epoch hasn't changed since (otherwise the new map runs on the real clock until the next host edit).
    public void ForceMapStateReapply() => lastAppliedMapEpoch = 0;

    // S327: fires when a peer is newly bound to a local object index (via the per-frame ContentId resolve). The plugin
    // wires this to actorVisibility.RegisterPeer so a freshly-bound puppet becomes visible immediately — binding now
    // happens continuously (transform-stream-driven), not just at join, so visibility must be registered at bind time.
    public Action<ushort>? OnPeerBound { get; set; }
    // S328x: apply a peer's chosen nameplate name to their puppet (objectIndex, name, hideFc). Wired to MonikerService
    // by the plugin; null if Moniker isn't present (then the call is simply skipped).
    public Action<ushort, string, bool, bool, bool>? ApplyMonikerName { get; set; }   // (objIdx, name, hideFc, hideName, forceRedraw)
    private uint lastAppliedMapEpoch;

    public unsafe HashSet<ushort> GetPeerObjectIndices()
    {
        var indices = new HashSet<ushort>();
        foreach (var (_, info) in peerInfos)
        {
            if (!info.ObjectIndex.HasValue)
                ResolvePeerObjectIndex(info);   // bind now if a co-located peer isn't bound yet (e.g. a lobby-registered peer)
            if (info.ObjectIndex.HasValue)
                indices.Add(info.ObjectIndex.Value);
        }
        return indices;
    }

    // S148: per-peer mount apply, driven by the synced MountId (the real path; TestApplyMount
    // was the probe). Only acts on CHANGE (tracked via PeerInfo.LastAppliedMountId) so we don't
    // re-call CreateAndSetupMount every frame. MountId>0 spawns that mount on the puppet;
    // 0 dismounts. Mount persists across /hms load (in-session map-hop keeps you mounted) and
    // is cleared on session exit via SanitizePeerStates.
    private unsafe void ApplyMountState(Character* character, TransformData data, PeerInfo info, ushort effectiveMountId)
    {
        if (effectiveMountId == info.LastAppliedMountId) return; // no change

        if (effectiveMountId > 0)
        {
            // S195b: validate the wire MountId before applying (same crash class as MountSelf — an
            // invalid ID faults Penumbra's mount-animation hook on the next UpdateAnimations frame,
            // and this is the RECEIVER, so a buggy/malicious peer broadcasting a bad ID would crash
            // everyone in the room). Guard here protects the whole room, not just the sender.
            var mountSheet = dataManager.GetExcelSheet<Mount>();
            if (!mountSheet.HasRow((uint)effectiveMountId))
            {
                log.Warning("[HMSync] ApplyMountState: wire mount ID " + effectiveMountId +
                    " has no sheet row — ignoring (would crash the animation update) for " + info.CharacterName);
                info.LastAppliedMountId = effectiveMountId; // mark seen so we don't re-check every frame
                return;
            }

            // S231: INSTANT seat on the puppet (animated mount-up parked — S230 confirmed PlayTimeline(165)
            // carries neither sound nor body-rise; the native climb is in the action-execution path, a
            // separate dig, not worth blocking on). Spawn the mount model + seat in one go.
            character->Mount.CreateAndSetupMount((short)effectiveMountId, 0, 0, 0, 0, 0, 0);
            character->Mode = CharacterModes.Mounted;   // S193: native cpose-suppress gate keys off this
            character->ModeParam = 0;
            character->Timeline.BaseOverride = 0;        // S195: drop stale on-foot override
            info.LastAppliedAnim = 0;
        }
        else
        {
            // S197f: DISMOUNT on the puppet from a wire mountId=0 — play the native dismiss animation.
            // Call CreateAndSetupMount(0) and let the native dismount sequence play out and transition
            // Mode itself (rider drops, mount runs off/fades), instead of forcing Mode=Normal here which
            // cancels the dismiss mid-play. Symmetric with the MountSelf self-dismount fix: A's dismiss
            // now plays on EVERY screen — A sees his own (MountSelf), peers see A's puppet's (here).
            // The native machine clears Mode→Normal as the dismount completes; the per-frame apply's
            // early-return (effectiveMountId==LastAppliedMountId) prevents re-entry. (The HARD teardown
            // in SanitizePeerStates keeps its immediate Mode=Normal — clean cut on exit, no animation.)
            GPoseProbe?.NoteClear("ApplyMountState-dismiss", 0, character->Mount.MountId);
            character->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
            log.Debug("[HMSync] Mount cleared (native dismiss plays) on " + info.CharacterName);
        }
        info.LastAppliedMountId = effectiveMountId;
    }

    // S322: minion (companion) channel — direct mirror of ApplyMountState. Summon/dismiss the minion on the
    // puppet from the synced MinionId. Validated against the Companion sheet first (a bad wire id from a
    // buggy/malicious peer must not fault the animation update for the whole room). Self-limiting: acts only
    // on a change (effectiveMinionId != LastAppliedMinionId). Cleared on session exit via SanitizePeerStates.
    private unsafe void ApplyMinionState(Character* character, TransformData data, PeerInfo info, ushort effectiveMinionId)
    {
        if (effectiveMinionId == info.LastAppliedMinionId) return; // no change

        if (effectiveMinionId > 0)
        {
            var companionSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
            if (!companionSheet.HasRow((uint)effectiveMinionId))
            {
                log.Warning("[HMSync] ApplyMinionState: wire minion ID " + effectiveMinionId +
                    " has no sheet row — ignoring for " + info.CharacterName);
                info.LastAppliedMinionId = effectiveMinionId; // mark seen so we don't re-check every frame
                return;
            }

            // Summon the minion on the puppet. SetupCompanion handles both the mobile (bob/wander) and the
            // static-with-VFX minions natively, exactly as the game does. param 0 = default behaviour.
            character->CompanionData.SetupCompanion((short)effectiveMinionId, 0);
            log.Debug("[HMSync] Peer summoned minion " + effectiveMinionId + " on " + info.CharacterName);
        }
        else
        {
            character->CompanionData.SetupCompanion(0, 0);
            log.Debug("[HMSync] Minion dismissed on " + info.CharacterName);
        }
        info.LastAppliedMinionId = effectiveMinionId;
    }

    // S322: summon a minion on the LOCAL player — the production path for /hms minion <id>. Your own client
    // renders it natively; the detector then reads CompanionData.CompanionObject->BaseId each frame and
    // broadcasts it, so peers replicate via ApplyMinionState (the exact mount self-summon model). id 0
    // dismisses. Returns false on no-local-player or an invalid sheet id (caller messages the user). Cleared
    // on exit by Sanitize.
    public unsafe bool SummonMinionSelf(short minionId)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
        {
            log.Warning("[HMSync] SummonMinionSelf: no local player");
            return false;
        }
        var character = (Character*)localPlayer;

        if (minionId > 0)
        {
            var companionSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
            if (!companionSheet.HasRow((uint)minionId))
            {
                log.Warning("[HMSync] SummonMinionSelf: minion ID " + minionId + " has no sheet row — ignoring.");
                return false;
            }
            character->CompanionData.SetupCompanion(minionId, 0);
            log.Information("[HMSync] Summoned minion " + minionId + " on self.");
        }
        else
        {
            character->CompanionData.SetupCompanion(0, 0);
            log.Information("[HMSync] Dismissed own minion.");
        }
        return true;
    }

    // S322k: fashion accessory (ornament) channel — direct mirror of ApplyMinionState. Equip/remove the
    // ornament on the puppet from the synced OrnamentId. Validated against the Ornament sheet first (a bad wire
    // id must not fault the room). Self-limiting (acts only on change). Ornaments are skeletally attached, so
    // once SetupOrnament seats one it rides the puppet's skeleton natively — no follow/offset/animation drive.
    private unsafe void ApplyOrnamentState(Character* character, PeerInfo info, ushort effectiveOrnamentId)
    {
        if (effectiveOrnamentId == info.LastAppliedOrnamentId) return; // no change

        if (effectiveOrnamentId > 0)
        {
            var ornamentSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Ornament>();
            if (!ornamentSheet.HasRow((uint)effectiveOrnamentId))
            {
                log.Warning("[HMSync] ApplyOrnamentState: wire ornament ID " + effectiveOrnamentId +
                    " has no sheet row — ignoring for " + info.CharacterName);
                info.LastAppliedOrnamentId = effectiveOrnamentId; // mark seen so we don't re-check every frame
                return;
            }
            character->OrnamentData.SetupOrnament((short)effectiveOrnamentId, 0);
            log.Debug("[HMSync] Peer equipped ornament " + effectiveOrnamentId + " on " + info.CharacterName);
        }
        else
        {
            character->OrnamentData.SetupOrnament(0, 0);
            log.Debug("[HMSync] Ornament removed on " + info.CharacterName);
        }
        info.LastAppliedOrnamentId = effectiveOrnamentId;
    }

    // S322k: equip an ornament on the LOCAL player — production path for /hms accessory <id>. Your own client
    // renders it natively; the detector reads OrnamentData.OrnamentObject->OrnamentId each frame and broadcasts
    // it, so peers replicate via ApplyOrnamentState. id 0 removes. Cleared on exit by Sanitize.
    public unsafe bool SummonOrnamentSelf(short ornamentId)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
        {
            log.Warning("[HMSync] SummonOrnamentSelf: no local player");
            return false;
        }
        var character = (Character*)localPlayer;

        if (ornamentId > 0)
        {
            var ornamentSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Ornament>();
            if (!ornamentSheet.HasRow((uint)ornamentId))
            {
                log.Warning("[HMSync] SummonOrnamentSelf: ornament ID " + ornamentId + " has no sheet row — ignoring.");
                return false;
            }
            character->OrnamentData.SetupOrnament(ornamentId, 0);
            log.Information("[HMSync] Equipped ornament " + ornamentId + " on self.");
        }
        else
        {
            character->OrnamentData.SetupOrnament(0, 0);
            log.Information("[HMSync] Removed own ornament.");
        }
        return true;
    }

    // S148: single-sweep teardown of all HMS-imposed peer state. Called from the
    // DoLeaveInternal chokepoint on every session exit (leave / stop / disconnect / session-
    // end). The general "clear everything we imposed" function — add any future persistent
    // state (minions, appearance overrides) here so it's cleared in one place rather than
    // needing a per-feature teardown discovered later (which is how the mount-persist bug
    // happened). Does NOT touch state the game/Penumbra owns — only what HMS sets.
    public unsafe void SanitizePeerStates()
    {
        foreach (var (_, info) in peerInfos)
        {
            if (!info.ObjectIndex.HasValue) continue;
            var obj = objectTable[(int)info.ObjectIndex.Value];
            if (obj == null) continue;
            var character = (Character*)obj.Address;
            if (character == null) continue;

            // S328x: clear any applied Moniker nameplate name so the peer's real name returns on session end.
            if (!string.IsNullOrEmpty(info.LastAppliedMonikerName) || info.LastAppliedMonikerHideFc || info.LastAppliedMonikerHideName)
            {
                ApplyMonikerName?.Invoke(info.ObjectIndex.Value, "", false, false, false);
                info.LastAppliedMonikerName = "";
                info.LastAppliedMonikerHideFc = false;
                info.LastAppliedMonikerHideName = false;
            }

            // Mount: dismount any synthetic mount we set, AND restore Normal mode (S193 — the
            // mount-apply sets Mode=Mounted for the native cpose gate, so the sweep must undo both,
            // or the peer is left stuck in Mounted mode after the model is gone).
            // S194: clear the test override too, and dismount if EITHER a wire mount or a test mount
            // was applied (LastAppliedMountId tracks both now, since the test routes through the real
            // ApplyMountState path).
            info.TestMountId = 0;
            // S197c: dismount if EITHER our tracked wire-mount applied OR the puppet is actually in
            // Mounted mode. The S147-pure TestApplyMount applies the mount directly (without setting
            // LastAppliedMountId), so checking real mode is what guarantees a direct testmount is also
            // torn down — no synthetic mount left on a puppet after the peer leaves / on /hms stop.
            if (info.LastAppliedMountId != 0 || character->Mode == CharacterModes.Mounted)
            {
                GPoseProbe?.NoteClear("TransformApply-dismount", 0, character->Mount.MountId);
                character->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
                if (character->Mode == CharacterModes.Mounted)
                {
                    character->Mode = CharacterModes.Normal;
                    character->ModeParam = 0;
                }
                info.LastAppliedMountId = 0;
            }
            // S322: minion clear — dismiss any minion we summoned on this puppet, so a peer isn't left
            // with a synthetic minion after they leave / on /hms stop. Mirrors the mount clear above.
            // CompanionObject (not CompanionId — that field reads 0) is the live "minion out" test.
            if (info.LastAppliedMinionId != 0 || character->CompanionData.CompanionObject != null)
            {
                character->CompanionData.SetupCompanion(0, 0);
                info.LastAppliedMinionId = 0;
            }
            info.MinionObjectSeen = false;
            info.MinionSpawnWaitFrames = 0;
            // S322k: ornament clear — remove any fashion accessory we equipped on this puppet, so a peer isn't
            // left with a synthetic ornament after they leave / on /hms stop. Mirrors the minion clear.
            if (info.LastAppliedOrnamentId != 0 || character->OrnamentData.OrnamentObject != null)
            {
                character->OrnamentData.SetupOrnament(0, 0);
                info.LastAppliedOrnamentId = 0;
            }
            info.OrnamentObjectSeen = false;
            info.OrnamentSpawnWaitFrames = 0;
            info.LastOrnActionEpoch = 0;
            // v0.7.420 — POSTURE CLEAR (moved from Stop(), which ran AFTER peerInfos.Clear() and
            // therefore iterated nothing — peers were never posture-cleaned). Here the roster is
            // still populated, so the cleanup actually reaches the actors. Full clear: EmoteId,
            // Mode, BaseOverride, DrawOffset, base-lane clip — same shape as SanitiseLocalPosture.
            // Covers both InPositionLoop and EmoteLoop.
            if (character->Mode == CharacterModes.InPositionLoop
             || character->Mode == CharacterModes.EmoteLoop)
            {
                character->EmoteController.EmoteId = 0;
                character->SetMode(CharacterModes.Normal, 0);
                character->Timeline.BaseOverride = 0;
                ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->SetDrawOffset(0, 0, 0);
                character->Timeline.TimelineSequencer.PlayTimeline(3);
            }
            // (future: appearance-override revert, etc. go here)
        }

        // S195: self-dismount. The peer sweep above clears mounts we applied to OTHER actors'
        // puppets; self-mount (MountSelf) mounts the LOCAL player, so the sweep must also clear
        // our own mount or we're left mounted after /hms stop / leave. Fetch the local player
        // live (never a stored pointer — S146 requery-by-key discipline).
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer != null)
        {
            var self = (Character*)localPlayer;
            if (self->Mount.MountId != 0 || self->Mode == CharacterModes.Mounted)
            {
                GPoseProbe?.NoteClear("SanitizeSelf", 0, self->Mount.MountId);
                self->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
                if (self->Mode == CharacterModes.Mounted)
                {
                    self->Mode = CharacterModes.Normal;
                    self->ModeParam = 0;
                }
            }
            // S322: clear a minion we summoned on ourselves via /hms minion, same as the self-dismount.
            // CompanionObject (not CompanionId — that reads 0) is the live test.
            if (self->CompanionData.CompanionObject != null)
                self->CompanionData.SetupCompanion(0, 0);
            // S322k: clear a fashion accessory we equipped on ourselves via /hms accessory.
            if (self->OrnamentData.OrnamentObject != null)
                self->OrnamentData.SetupOrnament(0, 0);
        }

        log.Debug("[HMSync] Peer states sanitized (mounts + minions cleared, self dismounted)");

        // T16: drop the roster itself. SanitizePeerStates runs only on teardown (leave/stop/crash) and the visual
        // cleanup above is done, so clearing the dict now stops stale peers bleeding into the next host/join/solo
        // (which was showing phantom A+B on every subsequent session, including solo).
        peerInfos.Clear();
        departedOrigins.Clear();   // v0.7.370: session-scoped — never leak an origin into a later session
    }

    // S195 SELF-MOUNT: mount the LOCAL player. This is the production path for "/hms mount <id>"
    // (replacing the S194 TestApplyMount peer-probe). The insight: CreateAndSetupMount is a
    // synthetic model-setup call that works on ANY actor — proven on peer puppets (mount-IV:
    // renders a chocobo indoors where native mounting is impossible). The local player is just
    // another actor, so self-mount is literally the same call pointed at Control.GetLocalPlayer().
    //
    // Propagation is then FREE: the sender (LocalStateDetector) reads our Mount.MountId whenever
    // Mode==Mounted, regardless of HOW we mounted (dual-track, line 149) — so flipping our own
    // Mode=Mounted here makes the sender capture MountId and broadcast it, and each peer's
    // ApplyMountState mounts our puppet on their client. "I mount → everyone sees me mounted",
    // the exact mirror of the already-proven peer→me path. No new wire code.
    //
    // Mode-flip rationale identical to ApplyMountState (S193): CreateAndSetupMount spawns the
    // model but does NOT set CharacterModes; setting Mode=Mounted engages the game's native
    // "ignore /cpose while mounted" gate so the saddle isn't corrupted. ModeParam=0 for Mounted.
    // mountId=0 dismounts. Command callbacks run on the framework thread, so direct call is safe.
    // S195e: returns a result so the command can give correct chat feedback (invalid ID → tell the
    // user it's invalid, rather than the old silent no-op that looked like success).
    public enum MountResult { Mounted, Dismounted, InvalidId, NoLocalPlayer }

    // S231: ANIMATED MOUNT-UP PARKED — mount is INSTANT on both self and peers (CreateAndSetupMount +
    // Mode=Mounted, no lead-in). S230 isolation test established PlayTimeline(165) = "mount/mount_start"
    // carries NEITHER the whistle sound NOR the body-rise (bare 165 plays silent, at whatever elevation
    // mounted-state sets, id-independent). The native climb+rise+sound is produced by the mount-ACTION
    // execution path (above the timeline/container layer), not by PlayTimeline or CreateAndSetupMount —
    // a separate dig if ever revisited. Brio & ARR both instant-seat only (no reference to copy). The
    // AnimLock/base-override route was rejected: it'd hold 165 cleanly but still silent + non-rising =
    // hand-assembly that fights the engine. The S229 deferred-seat machinery (PlayMountSummon, the
    // Pending/pendingSelfSeat timers, MountSummonDelayMs) and the S230 [MOUNTANIMTEST] command were
    // reverted here. (Native DISMOUNT remains free via CreateAndSetupMount(0) — DismountTimer @0x1C
    // drives that client-side; there is no mount-up symmetry.)


    public unsafe MountResult MountSelf(short mountId)
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
        {
            log.Warning("[HMSync] MountSelf: no local player");
            return MountResult.NoLocalPlayer;
        }
        var character = (Character*)localPlayer;

        if (mountId > 0)
        {
            // S195b: VALIDATE the mount ID against the sheet before CreateAndSetupMount. An
            // invalid/nonexistent ID (e.g. a typo'd 429) sets up a MountContainer whose animation
            // resources never resolve; the very next CharacterManager.UpdateAnimations frame walks
            // MountContainer.vf4, Penumbra's SomeMountAnimation hook tries to load the mount .pap
            // against the draw object, and derefs null → native AV (C0000005, uncatchable). The old
            // TestApplyMount probe dodged this by accident (sticky TestMountId applied indirectly);
            // self-mount calls the primitive directly on us, so the bad mount is live instantly and
            // faults next frame. Cheap guard: the row must exist. (Mount 0 is dismount, handled below.)
            var mountSheet = dataManager.GetExcelSheet<Mount>();
            if (!mountSheet.HasRow((uint)mountId))
            {
                log.Warning("[HMSync] MountSelf: mount ID " + mountId + " has no sheet row — ignoring (would crash the animation update).");
                return MountResult.InvalidId;
            }

            // S231: INSTANT self-seat (animated mount-up parked — see the parked-feature note above).
            // We mount YOU locally; your own client renders the mount natively (self-illusion). The
            // sender (LocalStateDetector) then reads Mount.MountId each frame and broadcasts it, so peers
            // apply your mount to your puppet. (Mount 0 dismount is handled in the else branch below.)
            character->Mount.CreateAndSetupMount(mountId, 0, 0, 0, 0, 0, 0);
            character->Mode = CharacterModes.Mounted;
            character->ModeParam = 0;
            log.Information("[HMSync] Self-mount " + mountId + " (instant). Sender will broadcast to peers.");
            return MountResult.Mounted;
        }
        else
        {
            // S197f: SELF-DISMOUNT via /hms mount 0 — play the native dismount/dismiss animation.
            // Call CreateAndSetupMount(0) and let the native dismount sequence play out and transition
            // Mode itself (exactly as a real in-game dismount does — rider drops, mount runs off/fades).
            // Do NOT force Mode=Normal here: that immediate flip cancels the dismiss mid-play (the same
            // bug we fixed for peer puppets in TestApplyMount). The sender reads currentMode each frame,
            // so once native clears Mode→Normal, mountId broadcasts 0 and peers dismount with their own
            // dismiss too. (The HARD teardown in SanitizePeerStates — stop/leave/disconnect/crash — keeps
            // its immediate Mode=Normal force: that's a clean cut on exit where no animation is wanted.)
            GPoseProbe?.NoteClear("MountSelf-dismount", 0, character->Mount.MountId);
            character->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
            log.Information("[HMSync] Self-dismount requested (native dismiss plays; mode clears natively).");
            return MountResult.Dismounted;
        }
    }

    // S194 MOUNT TEST [MOUNTTEST]: drove the receiver path via a sticky per-peer TestMountId.
    // SUPERSEDED by MountSelf (S195) for the /hms mount command — kept here because the per-frame
    // ApplyTransform still merges TestMountId into effectiveMountId (the receiver path that carries
    // a real peer's wire MountId is unchanged). No command calls this now; harmless dead weight.
    // S197c: restored to the S147 PURE form — direct CreateAndSetupMount on each peer puppet, with
    // NO Mode force and NO routing through ApplyMountState. This is the exact S147 baseline that
    // animated flawlessly (run/strafe/turn/jump/wing-flap) AND gave the native DISMOUNT/dismiss
    // animation for free (e.g. chocobo: rider drops, bird runs off and fades). The later
    // TestMountId→ApplyMountState routing added a Mode=Normal force on dismount that cancels the
    // native dismount sequence mid-play (killing the dismiss anim), and a Mode=Mounted force for
    // cpose-suppression. We start the async rebuild from THIS pure reference and re-introduce only
    // what's needed, deliberately. mountId=0 dismounts (and now plays the native dismiss). Grep [MOUNTTEST].
    public unsafe void TestApplyMount(short mountId)
    {
        int applied = 0;
        foreach (var (_, info) in peerInfos)
        {
            if (!info.ObjectIndex.HasValue) continue;
            var obj = objectTable[(int)info.ObjectIndex.Value];
            if (obj == null) continue;
            var character = (Character*)obj.Address;
            if (character == null) continue;

            // Pure native call: mountId>0 spawns+attaches that mount; mountId=0 plays the native
            // dismount. Barding fields 0. No Mode write — the native machine handles mode itself,
            // exactly as for a real rider, which is what lets the dismount animation play out.
            character->Mount.CreateAndSetupMount(mountId, 0, 0, 0, 0, 0, 0);
            applied++;
            log.Information("[MOUNTTEST] CreateAndSetupMount(" + mountId + ") on " +
                info.CharacterName + " (idx=" + info.ObjectIndex.Value + ")");
        }
        log.Information("[MOUNTTEST] Applied mount " + mountId + " to " + applied + " peer(s) [S147 pure baseline]. " +
            "Watch: animates? dismount/dismiss anim on mountId=0?");
    }

    public unsafe StateApplyService(
        IObjectTable objectTable,
        IFramework framework,
        IDataManager dataManager,
        IPluginLog log,
        IDalamudPluginInterface pluginInterface,
        ISigScanner sigScanner)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.dataManager = dataManager;
        this.log = log;
        this.pluginInterface = pluginInterface;
        // Resolve the native look-at update function (Brio's _updateLookAt). This is the function the game runs for
        // the local self-actor to turn look-at target params into head movement — it does NOT auto-run for puppets,
        // so we call it manually per-frame. Sig from Brio's ActorLookAtService.
        try
        {
            var addr = sigScanner.ScanText("E8 ?? ?? ?? ?? 8B D7 48 8B CB E8 ?? ?? ?? ?? 41 ?? ?? 8B D7 48 ?? ?? 48 ?? ?? ?? ?? 48 83 ?? ?? 5F");
            updateLookAt = (delegate* unmanaged<CharacterLookAtController*, LookAtTargetNative*, uint, nint, void>)addr;
            log.Information("[HMSync] look-at update fn resolved @ " + addr.ToString("X"));
        }
        catch (Exception e) { log.Warning("[HMSync] look-at update fn sig failed: " + e.Message); }
    }

    // Brio's LookAtTarget: LookMode @0x08 (3=Position), Position @0x10 (Vector3). Passed to updateLookAt per slot.
    [StructLayout(LayoutKind.Explicit)]
    private struct LookAtTargetNative
    {
        [FieldOffset(0x08)] public int LookMode;   // 3 = Position
        [FieldOffset(0x10)] public FFXIVClientStructs.FFXIV.Common.Math.Vector3 Position;
    }
    private unsafe delegate* unmanaged<CharacterLookAtController*, LookAtTargetNative*, uint, nint, void> updateLookAt;

    public void Start()
    {
        if (active) return;
        active = true;
        SubscribePenumbraRedraw();
        framework.Update += OnFrameworkUpdate;
        log.Information("[HMSync] State apply started");

        // v0.7.416: THIS is the moment a peer's posture stops being their business and starts being
        // ours — the zone load. Everyone bound during the lobby is reconciled here, once. Before this
        // point the lobby is live and their real pose is the truth, so we leave it entirely alone.
        ReconcileAllInheritedPoses();
    }

    // Subscribe to Penumbra's CharacterBase create lifecycle. Raw IPC by label so we carry
    // no Penumbra package dependency. If Penumbra is absent or the API has moved, this
    // degrades to "no redraw suppression" with a clear log line — Tier 1's type-guard still
    // protects every write, so a missing subscription is a softer-safety loss, not a crash.
    private void SubscribePenumbraRedraw()
    {
        try
        {
            // Penumbra.CreatingCharacterBase.V5 — fires BEFORE the draw object is rebuilt
            // (gameObject, collection, modelPtr, customizePtr, equipPtr). Window opens.
            penumbraCreatingCharacterBase =
                pluginInterface.GetIpcSubscriber<nint, Guid, nint, nint, nint, object?>(
                    "Penumbra.CreatingCharacterBase.V5");
            penumbraCreatingCharacterBase.Subscribe(OnPenumbraCreatingCharacterBase);

            // Penumbra.CreatedCharacterBase.V5 — fires AFTER the rebuild completes
            // (gameObject, collection, drawObject). Window closes.
            penumbraCreatedCharacterBase =
                pluginInterface.GetIpcSubscriber<nint, Guid, nint, object?>(
                    "Penumbra.CreatedCharacterBase.V5");
            penumbraCreatedCharacterBase.Subscribe(OnPenumbraCreatedCharacterBase);

            log.Information("[HMSync] Subscribed to Penumbra redraw lifecycle (draw-offset suppression active)");
        }
        catch (Exception ex)
        {
            penumbraCreatingCharacterBase = null;
            penumbraCreatedCharacterBase = null;
            log.Warning("[HMSync] Could not subscribe to Penumbra redraw events — draw-offset " +
                "suppression disabled (Tier 1 guard still active). Reason: " + ex.Message);
        }
    }

    private void UnsubscribePenumbraRedraw()
    {
        try { penumbraCreatingCharacterBase?.Unsubscribe(OnPenumbraCreatingCharacterBase); }
        catch (Exception ex) { log.Debug("[HMSync] Penumbra Creating unsubscribe failed: " + ex.Message); }
        try { penumbraCreatedCharacterBase?.Unsubscribe(OnPenumbraCreatedCharacterBase); }
        catch (Exception ex) { log.Debug("[HMSync] Penumbra Created unsubscribe failed: " + ex.Message); }
        penumbraCreatingCharacterBase = null;
        penumbraCreatedCharacterBase = null;
        rebuildingActors.Clear();
    }

    // Penumbra events arrive on the framework thread (same thread as OnFrameworkUpdate), so
    // the rebuildingActors set needs no locking — reads and writes are serialized by the
    // framework loop. A peer mid-rebuild is added here and removed on completion.
    private void OnPenumbraCreatingCharacterBase(nint gameObject, Guid collection, nint model, nint customize, nint equip)
    {
        rebuildingActors.Add(gameObject);
    }

    private void OnPenumbraCreatedCharacterBase(nint gameObject, Guid collection, nint drawObject)
    {
        rebuildingActors.Remove(gameObject);
        // The draw object was just recreated; any peer offset must be re-imposed. The
        // per-frame apply (write-on-difference, Tier 1) does this automatically on the next
        // clean frame — no explicit reassert needed because we write from a stored target,
        // not from the (now-reset) live draw delta.
    }

    // v0.7.328: write a captured origin position back onto a peer's (preserved, frozen) actor by object index. Undoes
    // the synthetic-coord freeze on return — the actor is a continuously-present real player (firewall-pinned), so its
    // true position is the session-start spot we captured. Direct local write; needs no network or live apply loop
    // (which is why the earlier broadcast approach failed under mutual teardown — this fixes each client's own view).
    // v0.7.416: reconcile every already-bound peer at engage. Peers that bind LATER go through the
    // guarded call in the bind path instead. PoseReconciled makes it once-per-peer-per-session either way.
    private unsafe void ReconcileAllInheritedPoses()
    {
        foreach (var (_, info) in peerInfos)
        {
            if (info.PoseReconciled || !info.ObjectIndex.HasValue) continue;
            var obj = objectTable[(int)info.ObjectIndex.Value];
            if (obj == null) continue;
            var ch = (Character*)obj.Address;
            if (ch == null) continue;
            info.PoseReconciled = true;
            ReconcileInheritedPose(ch, info);
        }
    }

    // v0.7.415: drop a posture the puppet arrived with. See the call site in the first-bind guard.
    // Mirrors the sender's SanitiseLocalPosture so both ends leave the actor in the same shape.
    // v0.7.420: widened to EmoteLoop (lean, dance) — the same one-family guard bug that bit the
    // sender sanitise. Added PlayTimeline(3) to clear the base-lane clip (the emote-95 trap).
    private unsafe void ReconcileInheritedPose(Character* chara, PeerInfo info)
    {
        bool isPosture = chara->Mode == CharacterModes.InPositionLoop
                      || chara->Mode == CharacterModes.EmoteLoop;
        if (!isPosture) return;                                        // nothing inherited
        if (info.EmoteActive) return;                                  // WE put them there — leave it to the emote path

        var wasMode = chara->Mode;
        byte wasParam = chara->ModeParam;
        chara->EmoteController.EmoteId = 0;
        chara->SetMode(CharacterModes.Normal, 0);
        chara->Timeline.BaseOverride = 0;
        ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chara)->SetDrawOffset(0f, 0f, 0f);
        chara->Timeline.TimelineSequencer.PlayTimeline(3);

        log.Debug("[HMSync] [POSE] bind: cleared inherited posture (was " + wasMode + "/" + wasParam +
            ") on " + (string.IsNullOrEmpty(info.CharacterName) ? "peer" : info.CharacterName));
    }

    public unsafe void WritePeerPosition(ushort idx, System.Numerics.Vector3 pos)
    {
        var obj = objectTable[(int)idx];
        if (obj == null) return;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        go->SetPosition(pos.X, pos.Y, pos.Z);
    }

    // v0.7.419 — post-settle peer posture cleanup. The Stop() cleanup ran before the zone reload,
    // but the reload rebuilt peer actors from cached HMS state (the same problem as self). This
    // re-clears every nearby Pc actor to standing/idle after the reload settles. Walks the object
    // table directly (the roster was cleared by SanitizePeerStates before Stop), targeting any
    // non-self Pc that is in a posture mode. The server's natural update cadence will repaint
    // peers with their real state once both sides' filters are down.
    public unsafe void SanitisePeerPostures()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null) return;
        var selfAddr = localPlayer.Address;
        int cleared = 0;
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.Address == selfAddr) continue;
            if ((byte)obj.ObjectKind != 1) continue;   // Pc only (ObjectKind.Pc = 1)
            var ch = (Character*)obj.Address;
            if (ch->Mode == CharacterModes.InPositionLoop || ch->Mode == CharacterModes.EmoteLoop)
            {
                ch->EmoteController.EmoteId = 0;
                ch->SetMode(CharacterModes.Normal, 0);
                ch->Timeline.BaseOverride = 0;
                ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->SetDrawOffset(0, 0, 0);
                ch->Timeline.TimelineSequencer.PlayTimeline(3);
                cleared++;
            }
        }
        if (cleared > 0)
            log.Debug("[HMSync] [POSE] post-settle: cleared posture on " + cleared + " peer(s)");
    }

    // Snapshot (idx, originPos) for every bound peer that has a captured origin — taken BEFORE SanitizePeerStates
    // clears the roster, so teardown can restore positions after the return settles.
    public System.Collections.Generic.List<(ushort idx, System.Numerics.Vector3 pos)> SnapshotPeerOrigins()
    {
        var list = new System.Collections.Generic.List<(ushort, System.Numerics.Vector3)>();
        foreach (var (_, info) in peerInfos)
            if (info.ObjectIndex.HasValue && info.OriginPosition.HasValue)
                list.Add((info.ObjectIndex.Value, info.OriginPosition.Value));
        // v0.7.370: include peers who ALREADY LEFT this session. Their roster entry is gone (UnregisterPeer removes
        // it), so iterating peerInfos alone silently skipped them — which is why the 70-unit freeze came back in the
        // leave-then-stop order only. They were restored at departure; re-asserting here covers a later settle-write.
        foreach (var (idx, pos) in departedOrigins)
            if (!list.Exists(e => e.Item1 == idx))
                list.Add((idx, pos));
        return list;
    }

    // v0.7.370: origins of peers who left DURING the session, retained past their roster removal so the session-end
    // restore still covers their actors. Cleared with the roster on session teardown.
    private readonly System.Collections.Generic.Dictionary<ushort, System.Numerics.Vector3> departedOrigins = new();

    public unsafe void Stop()
    {
        if (!active) return;
        active = false;
        framework.Update -= OnFrameworkUpdate;
        UnsubscribePenumbraRedraw();

        // v0.7.420: the peer cleanup loop that was here iterated peerInfos, which SanitizePeerStates
        // (called earlier in DoLeaveInternal) had already Clear()'d — so it ran over an empty roster
        // and cleaned nothing. All peer cleanup (mounts, minions, ornaments, posture) now lives in
        // SanitizePeerStates itself, before the Clear(). The post-settle SanitisePeerPostures() pass
        // catches anything the reload rebuilds from cached state.

        peerStates.Clear();
        peerInfos.Clear();
        departedOrigins.Clear();   // v0.7.370: session-scoped — never leak an origin into a later session
        log.Information("[HMSync] State apply stopped");
    }

    private long joinSequenceCounter;   // S326h: monotonic, stamps each peer's join order for the participant #-column

    public void RegisterPeer(string peerId, ulong contentId, uint entityId, string characterName)
    {
        // Keep a peer's original join order if it's already registered (re-register on re-resolve) — only assign a new
        // sequence to a genuinely new peer, so the #-column order is stable and reflects true arrival order.
        long seq = peerInfos.TryGetValue(peerId, out var existing) && existing.JoinSequence > 0
            ? existing.JoinSequence
            : System.Threading.Interlocked.Increment(ref joinSequenceCounter);
        var info = new PeerInfo
        {
            PeerId = peerId,
            ContentId = contentId,
            EntityId = entityId,
            CharacterName = characterName,
            JoinSequence = seq,
        };
        ResolvePeerObjectIndex(info);
        peerInfos[peerId] = info;
        // v0.7.337: THE late-join fix. RegisterPeer binds via ResolvePeerObjectIndex but historically never fired
        // OnPeerBound — that invoke lived ONLY in the resolve loop, gated behind `!ObjectIndex.HasValue`. A late joiner
        // co-located in the lobby binds HERE (identity known at join), so ObjectIndex is already set by the time the
        // resolve loop runs → its guard is false → OnPeerBound never fired → the joiner was bound + synced but never
        // un-hidden/made visible on an existing member. The legacy start→load path bound in the resolve loop (peer not
        // co-located at join), so it DID fire. Fire it here too when the bind succeeds.
        if (info.ObjectIndex.HasValue)
            OnPeerBound?.Invoke(info.ObjectIndex.Value);
        log.Information("[HMSync] Registered peer " + peerId[..6] + " as " + characterName +
            " (content=" + contentId + ", entity=" + entityId + ", idx=" + (info.ObjectIndex?.ToString() ?? "?") + ", seq=" + seq + ")");
    }

    public unsafe void UnregisterPeer(string peerId)
    {
        if (peerInfos.TryRemove(peerId, out var info))
        {
            if (info.ObjectIndex.HasValue)
            {
                var obj = objectTable[(int)info.ObjectIndex.Value];
                if (obj != null)
                {
                    var native = (GameObject*)obj.Address;
                    var character = (Character*)obj.Address;

                    // S197g: DISMOUNT before despawn. If the peer left while mounted, the synthetic
                    // mount model + Mode=Mounted live on this object slot. DisableDraw only hides the
                    // CHARACTER — the mount is a separate object and the stale mounted state lingers on
                    // the slot, re-materializing as a phantom (e.g. host sees the departed peer still
                    // mounted on /hms stop's scene teardown). Clear it as part of departure, the same
                    // guaranteed-teardown we apply to our own mounts. Hard clear (immediate Mode=Normal,
                    // no dismiss) — the peer is gone, there's no view to animate for.
                    if (character != null && (character->Mount.MountId != 0 || character->Mode == CharacterModes.Mounted))
                    {
                        GPoseProbe?.NoteClear("PeerDeparture", 0, character->Mount.MountId);
                        character->Mount.CreateAndSetupMount(0, 0, 0, 0, 0, 0, 0);
                        if (character->Mode == CharacterModes.Mounted)
                        {
                            character->Mode = CharacterModes.Normal;
                            character->ModeParam = 0;
                        }
                        // S197h: the hard clear skips the native dismount that normally unwinds the
                        // rider's body from SADDLE HEIGHT back to ground. Without it the puppet is left
                        // clamped at saddle elevation (the model treats it as floor; doesn't rubberband
                        // on move, needs re-entry to reset) — visible when host stop re-renders this
                        // slot. Reset the draw offset AND the seated/mount Y so the body returns to
                        // ground. (Matches Stop()'s SetDrawOffset(0) pattern; also zero BaseOverride so
                        // no stale mounted-idle anim lingers on the slot.)
                        native->SetDrawOffset(0, 0, 0);
                        character->Timeline.BaseOverride = 0;
                    }

                    // Defensive: drop any stale rebuild-suppression entry for this actor so a
                    // Created event we never received can't leave the address stuck-suppressed.
                    rebuildingActors.Remove((nint)native);

                    // v0.7.370: undo the synthetic-coord freeze for THIS peer as they leave. The firewall pins a peer's
                    // real actor at its session-start spot server-side, so once they're no longer a puppet the actor
                    // must be put back there — otherwise it sits at the last synthetic position (the "peer is ~70u
                    // away until they move" bug). This previously only happened at session END, via
                    // SnapshotPeerOrigins() — which iterates the LIVE roster, so a peer who left FIRST was already
                    // removed and contributed nothing. Hence the bug only showed in the leave-then-stop order.
                    if (info.OriginPosition.HasValue)
                    {
                        WritePeerPosition(info.ObjectIndex.Value, info.OriginPosition.Value);
                        // Retain it so the session-end restore (and its re-assert window) still covers this actor if a
                        // late settle-write disturbs the position after we've dropped the roster entry.
                        departedOrigins[info.ObjectIndex.Value] = info.OriginPosition.Value;
                        log.Debug("[HMSync] Restored origin for departing peer " + info.CharacterName + ".");
                    }

                    native->DisableDraw();
                    log.Information("[HMSync] Despawned peer " + info.CharacterName + " (mount cleared)");
                }
            }
        }
        peerStates.TryRemove(peerId, out _);
    }

    public void OnTransformReceived(string peerId, TransformData transform, bool isHotLane = true)
    {
        var state = peerStates.GetOrAdd(peerId, _ => new PeerInterpolationState());

        // S322j: protocol gate. A peer on a mismatched wire version is refused here — never buffered, never
        // applied — so a format skew can't desync or crash us. Warn once (chat + log). Pre-S322w clients have
        // no version field, so Protocol reads 0 ⇒ also refused. NOT applied = no puppet spawns for them.
        // Version gate. With sync lanes, the wire version rides the HOT lane (the always-flowing lane) and the legacy
        // monolith TransformUpdate; WARM/COLD/HOST carry no version. So the composite's Protocol is 0 UNTIL a HOT (or a
        // monolith) has merged. Protocol == 0 therefore means "not yet known" (a WARM/COLD arrived before this peer's
        // first HOT) — DEFER, don't refuse. A genuinely old pre-v3 peer sends a MONOLITH carrying its real old version
        // (1/2), which merges a non-zero Protocol and IS refused here. So: refuse a real mismatch, defer the unknown.
        if (transform.Protocol == 0)
            return;   // version not yet established for this peer (pre-HOT) — wait for a versioned message; don't spawn yet
        if (transform.Protocol != SyncProtocol.Version)
        {
            if (!state.ProtocolWarned)
            {
                state.ProtocolWarned = true;
                var who = peerInfos.TryGetValue(peerId, out var pi) && !string.IsNullOrEmpty(pi.CharacterName)
                    ? pi.CharacterName : "A peer";
                // Directional guidance: tell the user WHICH side is behind so the fix is obvious. The wire is
                // incompatible either way (the frame can't be applied), so the peer isn't synced — but knowing whether
                // to update yourself or nudge them is the actionable part. Both should be on the latest build.
                string direction = transform.Protocol < SyncProtocol.Version
                    ? who + " is on an OLDER HMSync version (their protocol v" + transform.Protocol +
                      ", yours v" + SyncProtocol.Version + ") — they need to update."
                    : who + " is on a NEWER HMSync version (their protocol v" + transform.Protocol +
                      ", yours v" + SyncProtocol.Version + ") — you need to update.";
                var msg = "[HMSync] " + direction + " They won't be synced until everyone's on the latest build.";
                log.Warning(msg);
                // OnTransformReceived runs on the relay thread; bounce the chat print to the framework thread.
                _ = framework.RunOnFrameworkThread(() => Notify?.Invoke(msg));
            }
            return;
        }

        // Reject out-of-order frames — async fire-and-forget sends can complete out of order.
        // v0.7.461 (P1, Codex QA): the Seq ordering guarantee applies ONLY to the HOT lane. Seq rides HOT
        // exclusively (WarmPayload/ColdPayload/HostPayload carry no Seq; MergeWarmWire/Cold/Host don't advance
        // it), so a WARM/COLD/HOST snapshot inherits the composite's LAST HOT Seq. Gating those on `Seq <=`
        // wrongly REJECTED them whenever they arrived right after a HOT frame that had already established the
        // same Seq — dropping stationary emotes/mounts/monikers/map-state (no HOT change to bump the Seq past
        // the rejection). Those lanes are change-gated at send and epoch/state-gated on apply, so they don't
        // need Seq ordering — only HOT does. Reject out-of-order HOT only; always let non-HOT lanes through.
        if (isHotLane && state.Current != null && transform.Seq <= state.Current.Data.Seq)
            return;

        // S327: TRANSFORM-STREAM-DRIVEN BINDING. Every transform carries the sender's stable ContentId. This is now the
        // authoritative way a peer becomes bindable — no positional guessing at join. Ensure a peerInfos entry exists
        // for this peerId and carries the ContentId; the per-frame resolve loop then binds it to the right local object
        // (by ContentId match) the moment that character is in render range. Self-healing: works for late joiners,
        // reconnects, and anyone who wasn't loaded near us when they joined.
        if (transform.SenderContentId != 0)
        {
            if (!peerInfos.TryGetValue(peerId, out var pinfo))
            {
                // First time we've heard from this peer with an identity → create the entry (unbound; binds later).
                long seq = System.Threading.Interlocked.Increment(ref joinSequenceCounter);
                pinfo = new PeerInfo { PeerId = peerId, ContentId = transform.SenderContentId, JoinSequence = seq };
                peerInfos[peerId] = pinfo;
            }
            else if (pinfo.ContentId != transform.SenderContentId)
            {
                // Identity changed/arrived (was 0, or peer re-identified) → update and force a re-resolve.
                pinfo.ContentId = transform.SenderContentId;
                pinfo.ObjectIndex = null;
            }
        }

        var newState = new TransformSnapshot
        {
            Data = transform,
            ReceiveTime = DateTime.UtcNow,
        };
        state.Previous = state.Current;
        state.Current = newState;

        // ── S326: host map-state apply ── the host stamps environment state (weather/time/BGM/NPC + an epoch) on
        // every outbound snapshot. Peers apply it when the epoch advances. A nonzero epoch only comes from the host
        // (peers leave it 0), so gating on "epoch advanced past what we last applied" needs no explicit host check.
        // The actual EnvManager/time writes must run on the framework thread — bounce via the plugin-wired callback.
        // THIS IS THE SINGLE TIME/WEATHER SYNC PATH. (S327g removed the redundant per-frame MirrorHostTime that fought
        // this — the host now bumps the epoch on every meaningful change, incl. during a time drag, so this carries it.)
        if (transform.MapStateEpoch != 0 && transform.MapStateEpoch != lastAppliedMapEpoch)
        {
            lastAppliedMapEpoch = transform.MapStateEpoch;
            LastAppliedMapState = transform;   // S330c: remember the map-state we applied, for host-promotion inheritance
            var td = transform;   // capture for the closure
            _ = framework.RunOnFrameworkThread(() => ApplyMapState?.Invoke(td));
        }

        // S320e: feed the render-behind interpolation buffer. On a large jump (teleport/zone) clear the
        // history so the puppet snaps instead of gliding across the gap. Trim to ~1s (cap count as a guard).
        lock (state.HistoryLock)
        {
            if (state.History.Count > 0)
            {
                var last = state.History[^1].Data;
                float jx = transform.X - last.X, jy = transform.Y - last.Y, jz = transform.Z - last.Z;
                if (jx * jx + jy * jy + jz * jz > 64f) state.History.Clear(); // >8 yalms — teleport, snap
            }
            state.History.Add(newState);
            var cutoff = DateTime.UtcNow.AddSeconds(-1.0);
            while (state.History.Count > 2 && state.History[0].ReceiveTime < cutoff)
                state.History.RemoveAt(0);
            if (state.History.Count > 16) state.History.RemoveAt(0);
        }
    }

    // S327: bind a peer to its local object-table slot by the STABLE ContentId (Character.ContentId @0x2358), not the
    // ephemeral/client-local EntityId and NOT the collision-prone name. ContentId is globally unique + survives
    // zone/render changes + is world-travel-proof, so a match is always the RIGHT body. Only resolvable when the peer's
    // character is in render range (ContentId reads 0 otherwise) — which is exactly when binding is possible anyway
    // (Mare appearance needs the same proximity), so we simply stay unbound until they're in range, then bind.
    private unsafe void ResolvePeerObjectIndex(PeerInfo info)
    {
        if (info.ContentId == 0) return;   // no stable key to match on yet (peer identity not populated)
        var localPlayer = objectTable.LocalPlayer;

        foreach (var pc in objectTable.PlayerObjects)
        {
            if (pc == localPlayer) continue;
            var chara = (Character*)pc.Address;
            if (chara == null) continue;
            if (chara->ContentId == info.ContentId)
            {
                info.ObjectIndex = (ushort)pc.ObjectIndex;
                info.EntityId = pc.EntityId;   // refresh the ephemeral handle for this session's loaded actor
                // v0.7.328: capture the peer's REAL position ONCE, at first bind — this is their true origin (the
                // firewall pins them here server-side all session). Restored on teardown to undo the synthetic freeze.
                // Guard: only capture once, and only before HMS has moved them (first bind is in the origin zone).
                if (!info.OriginCaptured)
                {
                    var bp = chara->Position;
                    info.OriginPosition = new System.Numerics.Vector3(bp.X, bp.Y, bp.Z);
                    info.OriginCaptured = true;
                }

                // v0.7.416: reconcile an inherited posture — but ONLY once the session is engaged.
                // It used to sit inside the first-bind block, and RegisterPeer is NOT gated on `active`
                // — it runs the moment a peer joins the LOBBY. So it fired while the peer was GENUINELY
                // seated, stood their puppet up wrongly, the server's next real update re-seated them,
                // and by zone-load time OriginCaptured was already true so it never ran again. Both of
                // the reported symptoms, one cause.
                //
                // The lobby is a live environment: people move about freely and their real posture is
                // the truth. Nothing should touch it until a zone actually loads. `active` is set by
                // Start(), which runs at engage — so this now covers only a LATE JOIN into a running
                // session; everyone present at engage is handled by ReconcileAllInheritedPoses().
                if (active && !info.PoseReconciled)
                {
                    info.PoseReconciled = true;
                    ReconcileInheritedPose(chara, info);
                }
                // S328w: populate CharacterName from the resolved object. Peers are created from the transform stream
                // (by ContentId) with NO name, so info.CharacterName was empty for everyone — which made the say-filter
                // treat every session member as a non-member and HIDE their /say. The participants list masked this by
                // falling back to pc.Name.TextValue when bound. Set it here so member-matching (and anything else keyed
                // on the name) works the moment the peer binds.
                if (string.IsNullOrEmpty(info.CharacterName))
                    info.CharacterName = pc.Name.TextValue;
                return;
            }
        }
        // Not in render range yet → leave ObjectIndex null. The per-frame resolve loop retries; it binds the moment
        // the peer's character streams in. Never bind by name (a same-named world-visitor would be the wrong body).
    }

    private int resolveThrottle;

    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        if (!active) return;

        resolveThrottle++;
        if (resolveThrottle >= 60)
        {
            resolveThrottle = 0;
            foreach (var (_, info) in peerInfos)
            {
                if (!info.ObjectIndex.HasValue)
                {
                    ResolvePeerObjectIndex(info);
                    // Newly bound this pass → make the puppet visible (binding is continuous now, so visibility must be
                    // registered here rather than only at join).
                    if (info.ObjectIndex.HasValue)
                        OnPeerBound?.Invoke(info.ObjectIndex.Value);
                }
            }
        }

        foreach (var (peerId, state) in peerStates)
        {
            var current = state.Current;
            if (current == null) continue;

            if (!peerInfos.TryGetValue(peerId, out var info) || !info.ObjectIndex.HasValue)
                continue;

            var obj = objectTable[(int)info.ObjectIndex.Value];
            if (obj == null) continue;

            var native = (GameObject*)obj.Address;
            var character = (Character*)obj.Address;
            var data = current.Data;

            // ── S61: standup grace period — get out of the puppet's way ──
            //
            // Brio proves TL 644 plays correctly on a puppet when nothing fights it.
            // HMS's per-frame writes (SetPosition, MirrorPoseState, ApplyBodyDrawOffset)
            // were the source of every S54–S60 failure: they yanked position, overwrote
            // pose, and cleared offsets while the animation was trying to blend.
            //
            // Grace period: play TL 644 (the game's native get-up), then STOP all per-
            // frame management for 0.7s. The native InPositionLoop handler holds
            // PosY=0.524 / DrawY=-0.500 (the correct seated context) while the animation
            // runs. After grace, SetMode(Normal,0) releases the handler, per-frame writes
            // resume, and the character snaps to floor — but the body is already standing.

            // Grace expiry: finalize standup after animation completes
            if (info.StandupGraceUntil != default(DateTime) && DateTime.UtcNow > info.StandupGraceUntil)
            {
                character->SetMode(CharacterModes.Normal, 0);
                character->Timeline.BaseOverride = 0;
                info.StandupGraceUntil = default;
                log.Debug("[HMSync] Grace expired → SetMode(Normal) on " + info.CharacterName);
            }

            // COSM_1_016: SKILLS — replay a peer's cast on their puppet. Fire-and-forget on the epoch, same idiom as
            // mount/ornament actions. The first epoch seen for a peer is only LATCHED, never replayed: a late joiner
            // would otherwise fire whatever cast the peer last made before they arrived.
            if (data.ActionEpoch != 0)
            {
                if (!info.ActionEpochSeen)
                {
                    info.ActionEpochSeen = true;
                    info.LastActionEpoch = data.ActionEpoch;   // catch up — don't replay a pre-join cast
                }
                else if (data.ActionEpoch != info.LastActionEpoch)
                {
                    info.LastActionEpoch = data.ActionEpoch;
                    if (data.ActionId > 0)
                    {
                        // Resolve the broadcast target ContentId to OUR copy of that character (a peer's puppet, or
                        // the local player). 0 / not-found → null → the replay animates on the caster, which is the
                        // right behaviour for self-casts and ground AoE.
                        Character* tgtChar = data.ActionTgtCid != 0 ? FindCharacterByContentId(data.ActionTgtCid) : null;
                        SkillReplay?.Invoke(character, data.ActionId, data.ActionType,
                            new System.Numerics.Vector3(data.ActionTgtX, data.ActionTgtY, data.ActionTgtZ), tgtChar);
                        log.Debug("[HMSync] [SKILL] replayed action " + data.ActionId + " on " + info.CharacterName
                            + (tgtChar != null ? " → target cid " + data.ActionTgtCid : " (self/AoE)") + ".");
                    }
                }
            }

            // Detect new standup signal (can start a grace)
            if (data.StandupEpoch != 0 && data.StandupEpoch != info.LastStandupEpoch)
            {
                info.LastStandupEpoch = data.StandupEpoch;

                if (data.StandupTimelineId > 0 && info.EmoteActive)
                {
                    info.EmoteActive = false;

                    if (data.StandupTimelineId == 644)
                    {
                        // S69: CHAIR ONLY — grace period. Chair has a 0.524 phantom rise;
                        // the grace lets TL 644 play from the correct seated context
                        // while the native handler holds PosY/DrawY. Groundsit doesn't
                        // need this (PosY is floor, no discontinuity).
                        character->Timeline.BaseOverride = 644;
                        character->Timeline.TimelineSequencer.PlayTimeline(644);
                        info.StandupGraceUntil = DateTime.UtcNow.AddSeconds(0.7);
                        log.Debug("[HMSync] Standup grace (chair TL 644) on " + info.CharacterName);
                    }
                    else
                    {
                        // Non-chair (groundsit, etc.) — bare SetMode. The native get-up
                        // handles everything at floor level.
                        character->SetMode(CharacterModes.Normal, 0);
                        // S70b: prevent MirrorPoseState from playing the get-up timeline
                        // again on this frame (it sees PoseType Sit→Idle as a change).
                        info.SkipNextPoseTimeline = true;
                        log.Debug("[HMSync] Standup (bare, TL " + data.StandupTimelineId +
                            ") on " + info.CharacterName);
                    }
                }
            }

            // During grace: skip ALL per-frame management, only maintain gaze.
            if (info.StandupGraceUntil != default(DateTime) && DateTime.UtcNow < info.StandupGraceUntil)
            {
                ApplyGazeTarget(character, data.TargetEntityId, info);
                ApplyFaceCamera(character, data.FaceCamera, info, data.FaceCamX, data.FaceCamY, data.FaceCamZ);
                ApplyGazeControl(character, data, info);
                continue;
            }

            // ── Position / rotation (render-behind snapshot interpolation) ──
            float x = data.X, y = data.Y, z = data.Z, rot = data.Rotation;  // default: newest (fallback)
            // S322h: minion offset/facing ride the SAME render-behind interpolation as the body, so the copied
            // minion is as smooth as the puppet and doesn't step on the packet rate. Default to newest.
            float minOffX = data.MinionOffX, minOffY = data.MinionOffY, minOffZ = data.MinionOffZ, minRot = data.MinionRot;
            bool isMoving = false;
            var previous = state.Previous;
            if (previous != null)
            {
                // S320e: this is what the game does for remote players. Display the puppet at
                // (now - PeerInterpDelay) and LINEARLY interpolate between the two buffered snapshots that
                // bracket that render time. Linear interp between real samples = CONSTANT velocity within a
                // segment = NO ripple. (The S320b exponential ease toward the latest packet decelerated
                // between packets and jumped on arrival, pulsing velocity at the 10 Hz packet rate — the
                // continuous side-by-side stutter.) Rendering ~1.5 ticks in the past gives a jitter buffer
                // so we interpolate rather than extrapolate/freeze. Cost: ~150 ms display latency, socially
                // imperceptible. Segment velocity only changes at snapshot boundaries — i.e. when the peer's
                // ACTUAL motion changed — so steady flight is perfectly smooth.
                DateTime renderAt = DateTime.UtcNow - TimeSpan.FromSeconds(PeerInterpDelay);
                lock (state.HistoryLock)
                {
                    var h = state.History;
                    if (h.Count >= 2)
                    {
                        DateTime oldest = h[0].ReceiveTime, newest = h[^1].ReceiveTime;
                        DateTime rt = renderAt < oldest ? oldest : (renderAt > newest ? newest : renderAt);
                        for (int i = h.Count - 1; i >= 1; i--)
                        {
                            if (rt >= h[i - 1].ReceiveTime)
                            {
                                var a = h[i - 1]; var b = h[i];
                                double seg = (b.ReceiveTime - a.ReceiveTime).TotalSeconds;
                                float t = seg > 1e-5 ? (float)((rt - a.ReceiveTime).TotalSeconds / seg) : 1f;
                                x = Lerp(a.Data.X, b.Data.X, t);
                                y = Lerp(a.Data.Y, b.Data.Y, t);
                                z = Lerp(a.Data.Z, b.Data.Z, t);
                                rot = LerpAngle(a.Data.Rotation, b.Data.Rotation, t);
                                minOffX = Lerp(a.Data.MinionOffX, b.Data.MinionOffX, t);
                                minOffY = Lerp(a.Data.MinionOffY, b.Data.MinionOffY, t);
                                minOffZ = Lerp(a.Data.MinionOffZ, b.Data.MinionOffZ, t);
                                minRot = LerpAngle(a.Data.MinionRot, b.Data.MinionRot, t);
                                break;
                            }
                        }
                    }
                }

                // S89: derive movement from the position delta between PACKETS (not the interpolated
                // display), so the walk/run animation gate is unaffected by the interpolation latency.
                float dx = data.X - previous.Data.X;
                float dy = data.Y - previous.Data.Y;
                float dz = data.Z - previous.Data.Z;
                float distSq = dx * dx + dy * dy + dz * dz;
                isMoving = distSq > 0.0001f; // ~1cm between packets
            }

            // ── Weapon state ──
            // S91b: draw needs an aggressive signal (bytes alone waited for movement),
            // but NOT battle_start (LocomotionData.WeaponDraw=1) — that's the universal
            // one-handed grab, wrong for two-handers (the invalid grip we saw). The
            // sender reports the real weapon idle base as data.TimelineId on the draw
            // transit. Blend toward IsWeaponDrawn + the sender's timeline; the game
            // resolves the correct class-specific draw from the weapon flag.
            if (character->Timeline.IsWeaponDrawn != data.WeaponDrawn)
            {
                character->Timeline.IsWeaponDrawn = data.WeaponDrawn;

                if (data.WeaponDrawn)
                {
                    if (data.PoseType == 0xFF)
                    {
                        character->EmoteController.CurrentPoseType =
                            (EmoteController.PoseType)0xFF;
                        character->EmoteController.CPoseState = 0;
                    }

                    if (info.HeldIdleTimeline > 0)
                    {
                        character->Timeline.TimelineSequencer.PlayTimeline(34);
                        info.HeldIdleTimeline = 0;
                        info.LastIdleCpose = 0;
                        log.Debug("[HMSync] Draw evicted idle pose loop on " +
                            info.CharacterName);
                    }

                    // S121: battle_start (tl 1), re-tested in the clean architecture.
                    // The sheet: Slot=1 (UPPERBODY), ResidentPap=2 — it resolves
                    // per-class from the resident bt_[weapon] set, exactly like the
                    // sheathe (also Slot=1). The S91-era "universal one-handed grab"
                    // verdict was confounded by the AnimLock contention wars and a
                    // byte-flip ordering problem. Byte FIRST (resident set switches),
                    // THEN the flourish on the upper-body lane.
                    // S122: base eviction BEFORE the flourish. battle_start lives on
                    // the upper lane and doesn't displace a playing emote on base —
                    // which silently broke "draw interrupts emote." PlayTimeline(34)
                    // evicts the base (the interrupt); battle_start supplies the
                    // class-resolved flourish on top. The S107-era "34 kills the
                    // flourish" problem is gone because the flourish no longer comes
                    // from the base lane.
                    character->Timeline.TimelineSequencer.PlayTimeline(34);
                    character->Timeline.TimelineSequencer.PlayTimeline(
                        LocomotionData.WeaponDraw);
                }
                else
                {
                    // S123: symmetric with the draw (S122c). battle_end is UpperBody
                    // (Slot=1) and can't displace a base-lane emote — sheathing mid-
                    // emote didn't interrupt. Evict the base to the sheathed idle (3)
                    // first, then the sheathe animation on top. Proven lanes (S98).
                    character->Timeline.TimelineSequencer.PlayTimeline(3);
                    character->Timeline.TimelineSequencer.PlayTimeline(
                        LocomotionData.WeaponSheathe);
                }

                log.Debug("[HMSync] Weapon " + (data.WeaponDrawn ? "drawn" : "sheathed") +
                    " on " + info.CharacterName);
            }

            // ── Cosmetic display toggles (S244/S245) — mirror the sender's visor + headgear-
            // hidden state on the puppet. Weapon hide/show is SENDER-ONLY (not synced) per V.
            // Idempotent: only write on change. ──
            if (character->DrawData.IsVisorToggled != data.VisorToggled)
            {
                character->DrawData.SetVisor(data.VisorToggled);
                character->DrawData.IsVisorToggled = data.VisorToggled;
            }
            if (character->DrawData.IsHatHidden != data.HatHidden)
            {
                // Method only — HideHeadgear owns the bit; pre-setting it no-ops the redraw.
                character->DrawData.HideHeadgear(0, data.HatHidden);
            }

            // ── Mount channel (S148) — MUST run before the pose channel ──
            // Spawn/clear the mount model on the puppet from the synced MountId. The puppet
            // becomes genuinely mounted (game's native mounted-state machine runs on it —
            // seat positioning, wing-flaps, locomotion all handled natively, proven S147).
            // S194: effectiveMountId merges the real wire MountId with the /hms mount test override
            // so the test command exercises this exact gated path (not the old raw-primitive probe).
            ushort effectiveMountId = data.MountId != 0 ? data.MountId : info.TestMountId;
            ApplyMountState(character, data, info, effectiveMountId);

            // ── Minion channel (S322) ── summon/clear the minion model on the puppet from the synced
            // MinionId. Independent of mount/emote; the game's minion AI (idle bob, wander, VFX) runs
            // natively on the puppet once SetupCompanion seats it. No test override — the local summon
            // (game UI or /hms minion) is captured by the detector and rides the wire like the mount.
            ApplyMinionState(character, data, info, data.MinionId);

            // ── Fashion accessory channel (S322k) ── equip/remove the ornament on the puppet from the synced
            // OrnamentId. Skeletally attached, so it just rides the puppet once seated — no follow/offset.
            ApplyOrnamentState(character, info, data.OrnamentId);

            // ── Moniker nameplate channel (S328x) ── apply the sender's chosen nameplate name to this puppet via
            // Moniker's IPC, on CHANGE only (name string differs from last applied). Empty clears. Decoupled through
            // the ApplyMonikerName callback so this service doesn't depend on the Moniker IPC directly.
            if (info.ObjectIndex.HasValue)
            {
                string wireName = data.MonikerName ?? "";
                bool wireHideFc = data.MonikerHideFc;
                bool wireHideName = data.MonikerHideName;
                if (wireName != info.LastAppliedMonikerName || wireHideFc != info.LastAppliedMonikerHideFc || wireHideName != info.LastAppliedMonikerHideName)
                {
                    // v0.7.369: `hadApplied` is retained as a hint only. It used to mean "clear-then-set to force a
                    // repaint", but Moniker's IPC handlers now call RequestNameplateRedraw() themselves, so a plain set
                    // repaints correctly. (The old bug: Set/Clear mutated Moniker's peer-name dictionary without
                    // dirtying the plate, and a peer's plate is never organically dirty — so a flag-only change waited
                    // for the next natural rebuild while a name-string change repainted immediately.)
                    bool hadApplied = !string.IsNullOrEmpty(info.LastAppliedMonikerName) || info.LastAppliedMonikerHideFc || info.LastAppliedMonikerHideName;
                    ApplyMonikerName?.Invoke(info.ObjectIndex.Value, wireName, wireHideFc, wireHideName, hadApplied);
                    info.LastAppliedMonikerName = wireName;
                    info.LastAppliedMonikerHideFc = wireHideFc;
                    info.LastAppliedMonikerHideName = wireHideName;
                }
            }

            // S148: when mounted, the native mounted-state machine OWNS the body. Our pose/
            // cpose writes fight it — writing cpose pops the puppet out of the saddle and it
            // treats the saddle as floor (the S147 corruption). The game itself ignores /cpose
            // while mounted; we mirror that by SKIPPING the pose channel entirely when mounted.
            // Emote channel still runs below (mounted emotes like /point come through there,
            // a separate channel — they keep working). Same "defer to native when native owns
            // the body" pattern as the chair-standup grace period.
            // S197c: key this off the puppet's ACTUAL mounted state, not the wire effectiveMountId.
            // Under the S147-pure testmount the wire says on-foot (effectiveMountId=0) while the
            // puppet is genuinely mounted via direct CreateAndSetupMount — so reading real state is
            // what keeps the pose channel from fighting the native saddle. (When the async sender
            // later broadcasts MountId, effectiveMountId>0 too and this stays consistent.)
            bool peerMounted = effectiveMountId > 0 || character->Mode == CharacterModes.Mounted;

            // ── Pose channel — MUST run before SetPosition (moonwalk guard) ──
            // S95: MirrorPoseState releases the held weapon-drawn pose the instant
            // the peer starts moving (predicate !isMoving), before position is applied,
            // so the pose hands off cleanly to locomotion.
            if (!peerMounted)
                MirrorPoseState(character, data, info, isMoving);

            // ── Position / rotation ──
            native->SetPosition(x, y, z);
            // S322j: lean the heading toward actual travel on a diagonal. In standard movement, strafing keeps the
            // GameObject rotation forward while the body slides at 45°, so the sent heading alone leaves the puppet
            // square-on while it moves diagonally. Compute a target OFFSET toward the movement (forward bin → toward
            // travel; backward bin → away from it, so a back-pedal tracks its diagonal; strafe → none), and smooth
            // ONLY the offset (exponential) so the lean ramps in/out instead of snapping (the S322t snap applied it
            // instantly). The base stays the sent heading, so ordinary turning keeps zero lag — only the lean eases.
            float faceTargetOffset = 0f;
            if (!peerMounted && data.MoveState != 0 && state.Previous != null)
            {
                float fdx = data.X - state.Previous.Data.X, fdz = data.Z - state.Previous.Data.Z;
                if (fdx * fdx + fdz * fdz > 0.001f)
                {
                    float moveAngle = MathF.Atan2(fdx, fdz);
                    float rel = LocomotionData.WrapAngle(moveAngle - data.Rotation);
                    if (info.LastMoveDir == LocomotionData.DirForward)
                        faceTargetOffset = rel;                                          // face travel
                    else if (info.LastMoveDir == LocomotionData.DirBackward)
                        faceTargetOffset = LocomotionData.WrapAngle(rel + MathF.PI);     // face away (back-pedal)
                    // strafe (Left/Right) → 0: side-step facing forward
                }
            }
            info.FacingOffset = LocomotionData.WrapAngle(
                info.FacingOffset + LocomotionData.WrapAngle(faceTargetOffset - info.FacingOffset) * 0.25f);
            native->SetRotation(peerMounted ? rot : LocomotionData.WrapAngle(rot + info.FacingOffset));

            // S206 PITCH: replicate the mount's nose tilt on climb/dive. The sender extracted pitch
            // from its mount DrawObject quaternion (asin(2*(w*x-y*z))); apply it to THIS peer's mount
            // DrawObject so a flying mount tilts instead of staying level. We compose the peer's yaw
            // (rot, which the mount inherits) with the received pitch into a quaternion and write it to
            // the mount DrawObject rotation (GameObject+0x100 → Object+0x60). Gated on flying so ground
            // mounts are untouched. CONFIRMED (S207): the write HOLDS — the native flight animation
            // does not re-assert level, so the peer's mount tilts its nose on the sender's climb/dive.
            if (peerMounted && data.MoveMode == LocomotionData.ModeFlyMount)
            {
                var mountObj = character->Mount.MountObject;
                if (mountObj != null)
                {
                    var mdraw = mountObj->DrawObject;
                    if (mdraw != null)
                    {
                        // Quaternion = yaw(+Y) ∘ pitch(local X). Half-angles:
                        float hy = rot * 0.5f, hp = data.MountPitch * 0.5f;
                        float sy = MathF.Sin(hy), cy = MathF.Cos(hy);
                        float sp = MathF.Sin(hp), cp = MathF.Cos(hp);
                        // q = qYaw * qPitch, with qYaw=(0,sy,0,cy), qPitch=(sp,0,0,cp)
                        var q = mdraw->Rotation;
                        q.X = cy * sp;
                        q.Y = sy * cp;
                        q.Z = -sy * sp;
                        q.W = cy * cp;
                        mdraw->Rotation = q;
                    }
                }
            }

            // (Standup channel moved to grace-period block above position writes)

            // ── Minion follow + spawn-reconcile (S322b/d) ──
            // First make sure the companion SPAWNS: SetupCompanion's spawn is NOT synchronous and the one-shot
            // call in ApplyMinionState can land before the puppet is ready, so re-issue it a bounded number of
            // times. Once it exists: STATIONARY minions (e.g. 414 campfire) keep the engine's behaviour — they
            // stay dropped. But the engine's CONTINUOUS follow does NOT run for a puppet-owned companion (only
            // its far-distance teleport leash fires), so a FOLLOWER minion just sits at its spawn point. Drive
            // the non-stationary ones toward the owner ourselves: step toward the puppet each frame, leaving a
            // standoff gap so it trails and catches up (the natural follow delay) without overlapping the body,
            // and face the step direction. (S322b's HARD snap pinned a FIXED owner-offset every frame, so the
            // idle-wander walked in place and facing never updated; stepping through real ground fixes both.)
            if (data.MinionId != 0)
            {
                var companion = character->CompanionData.CompanionObject;
                if (companion != null)
                {
                    if (!info.MinionObjectSeen)
                    {
                        info.MinionObjectSeen = true;
                        info.MinionSpawnWaitFrames = 0;
                        log.Debug("[HMSync] Minion object up on " + info.CharacterName + " (id " +
                            data.MinionId + ").");
                    }
                    // Followers only — leave Stationary minions where they dropped. The puppet's OWN companion
                    // never gets Behaviour set (only the sender's is correct), so gate on the wire value. 3 =
                    // CompanionMove.Stationary.
                    if (data.MinionBehaviour != (byte)FFXIVClientStructs.FFXIV.Client.Game.Character.CompanionMove.Stationary)
                    {
                        // S322h: place a perfect copy of the sender's minion — owner position + the (interpolated)
                        // captured offset, plus its facing. Position now ORIGINATES from the sender, in lockstep
                        // with the replayed animation, so there's no local follow lag/slide. The old per-frame
                        // catch-up drive (compute distance, ease toward owner) is gone — we no longer guess.
                        var cgo = (GameObject*)companion;
                        float mx = x + minOffX, my = y + minOffY, mz = z + minOffZ;
                        cgo->SetPosition(mx, my, mz);
                        cgo->SetRotation(minRot);

                        // Animate only while the copy is actually translating; when it settles, release to the
                        // NATIVE idle (forcing the clip every frame fought the native idle and jittered). The
                        // smooth offset interpolation keeps this delta stable, so it doesn't flicker on packets.
                        float mdx = mx - info.LastMinionWorldX, mdz = mz - info.LastMinionWorldZ;
                        bool minionMoving = (mdx * mdx + mdz * mdz) > 0.0004f;   // ~0.02 yalm/frame
                        info.LastMinionWorldX = mx;
                        info.LastMinionWorldZ = mz;
                        if (minionMoving && data.MinionAnim != 0)
                        {
                            companion->Timeline.BaseOverride = data.MinionAnim;
                            if (data.MinionAnim != info.LastMinionAnim)
                            {
                                companion->Timeline.TimelineSequencer.PlayTimeline(data.MinionAnim);
                                info.LastMinionAnim = data.MinionAnim;
                            }
                        }
                        else if (!minionMoving && info.LastMinionAnim != 0)
                        {
                            companion->Timeline.BaseOverride = 0;
                            info.LastMinionAnim = 0;
                        }
                    }
                }
                else if (!info.MinionObjectSeen)
                {
                    info.MinionSpawnWaitFrames++;
                    if (info.MinionSpawnWaitFrames % 30 == 0 && info.MinionSpawnWaitFrames <= 300)
                        character->CompanionData.SetupCompanion((short)data.MinionId, 0);
                    if (info.MinionSpawnWaitFrames == 300)
                        log.Warning("[HMSync] Minion id " + data.MinionId + " never spawned on " +
                            info.CharacterName + " after retries — puppet can't host a companion via " +
                            "SetupCompanion; a synthetic spawn will be needed.");
                }
            }
            else if (info.MinionObjectSeen || info.MinionSpawnWaitFrames != 0)
            {
                info.MinionObjectSeen = false;
                info.MinionSpawnWaitFrames = 0;
                info.LastMinionAnim = 0;
                info.LastMinionWorldX = 0f;
                info.LastMinionWorldZ = 0f;
            }

            // ── Fashion accessory spawn-reconcile (S322k) ── SetupOrnament's spawn isn't synchronous either, so
            // the one-shot in ApplyOrnamentState can land before the puppet is ready and silently miss (this was
            // the missing receiver sync — exactly the minion spawn-race). Re-issue a bounded number of times until
            // the ornament object exists. Once up it's skeletally attached and rides the puppet natively — nothing
            // else to drive (no follow/offset/anim, unlike minions).
            if (data.OrnamentId != 0)
            {
                if (character->OrnamentData.OrnamentObject != null)
                {
                    if (!info.OrnamentObjectSeen)
                    {
                        info.OrnamentObjectSeen = true;
                        info.OrnamentSpawnWaitFrames = 0;
                        info.LastOrnActionEpoch = data.OrnamentActionEpoch; // catch up — don't replay a pre-join action
                        log.Debug("[HMSync] Ornament object up on " + info.CharacterName + " (id " + data.OrnamentId + ").");
                    }
                    // NOTE: no held-ornament cpose re-seat exists anymore. The S324a–c SetupOrnament re-attach approach
                    // was removed in S324e — the ATTACHTRACE proved the parasol stays parented (exec=3) through every
                    // stance, and the re-seat was itself what hid it (respawn → IsVisible=0). See the post-resolver
                    // comment block for the full reasoning. The spawn-retry below is the ONLY SetupOrnament call left.
                }
                else if (!info.OrnamentObjectSeen)
                {
                    info.OrnamentSpawnWaitFrames++;
                    if (info.OrnamentSpawnWaitFrames % 30 == 0 && info.OrnamentSpawnWaitFrames <= 300)
                        character->OrnamentData.SetupOrnament((short)data.OrnamentId, 0);
                    if (info.OrnamentSpawnWaitFrames == 300)
                        log.Warning("[HMSync] Ornament id " + data.OrnamentId + " never spawned on " +
                            info.CharacterName + " after retries.");
                }
            }
            else if (info.OrnamentObjectSeen || info.OrnamentSpawnWaitFrames != 0)
            {
                info.OrnamentObjectSeen = false;
                info.OrnamentSpawnWaitFrames = 0;
            }

            // ── Ornament ACTION one-shot: RETIRED (S323v) ── this used to replay data.OrnamentActionTimeline once
            // per epoch via PlayTimeline. It's now redundant AND harmful: the resolver's ornament tier
            // (ComputeOrnamentTimeline) already returns the action timelines (dig 13383, torch 8194, etc.) via its
            // stationary path and the resolver PlayTimelines them — while this channel ALSO caught the directional
            // walks (7370–7373) and cpose stances (8062–8068) as bogus one-shots and double-fired them. Removing it
            // makes the resolver the single ornament tl0 writer. (The OrnamentActionTimeline/Epoch wire fields +
            // detector latch are now dead; a later cleanup can drop them — left dormant here to keep this diff small.)

            // ── Mount ACTION one-shot (S323j) ── the ornament-action pattern re-pointed at the mount: mount-hotbar
            // actions (mount-17 spells 1752/1753, Fenrir howl, mount music, etc.) are tl0 one-shots on the MOUNT
            // OBJECT's slot 0 (confirmed by MOUNTACTIONTRACE; the rider stays seated). The sender latches the action
            // timeline + epoch, gated by AllLocomotionTimelines so nothing in the mount's motion set fires. Replay it
            // here ONCE per epoch on the puppet's OWN mount object. Catch the epoch up the frame the puppet's mount
            // first appears so a pre-join action can't fire on mount-up; after it plays the mount falls back to its
            // native idle (3). "If it plays on self, it plays on others."
            if (data.MountId != 0)
            {
                var mtObj = character->Mount.MountObject;
                if (mtObj != null)
                {
                    if (!info.MountObjectSeen)
                    {
                        info.MountObjectSeen = true;
                        info.LastMountActionEpoch = data.MountActionEpoch; // catch up — don't replay a pre-join action
                    }
                    if (data.MountActionEpoch != info.LastMountActionEpoch)
                    {
                        if (data.MountActionTimeline > 0)
                        {
                            mtObj->Timeline.TimelineSequencer.PlayTimeline(data.MountActionTimeline);
                            log.Debug("[HMSync] Mount action tl=" + data.MountActionTimeline + " on " + info.CharacterName);
                        }
                        info.LastMountActionEpoch = data.MountActionEpoch;
                    }
                }
            }
            else if (info.MountObjectSeen)
            {
                info.MountObjectSeen = false;
                info.LastMountActionEpoch = 0;
            }

            // NOTE (v0.7.451/.452 diagnostic, since removed): the ~1-2 frame peer-mount flicker on mount-up is a
            // Penumbra CreateCharacterBase rebuild of the MOUNT OBJECT's draw (the mount is a Penumbra-managed
            // drawable with its own actor address, distinct from the rider). Its DrawObject goes valid→null→rebuilt,
            // straddled by Penumbra Creating/Created; fires at spawn and again on Penumbra's own redraw cadence.
            // The mount object pointer is stable (no engine re-mount). Not HMS-triggered — accepted as-is (self is
            // masked by the mount-up animation, so only puppets show it). Prevention is Penumbra's domain.

            // ── Emote state (epoch-based, no race) ──
            ApplyEmoteState(character, data, info);

            // ── Animation resolution (S323s, Phase 1) ──
            // Single entry point for the puppet's locomotion/pose animation. Currently a dispatch shell over the
            // existing tier functions (see ResolvePuppetAnimation); Phase 2 folds the tier writes in so it becomes
            // the sole tl0/BaseOverride writer and MirrorPoseState + the action channel collapse into it.
            ResolvePuppetAnimation(character, data, state.Previous?.Data, info);

            // ── Held-ornament cpose stances: KNOWN LIMITATION, gracefully degraded (S324i) ─────────────────────────
            // Held-in-hand ornament cpose stances (parasol/shovel/torch, CPoseState 1/2/3 → clips 8062–8068) do NOT
            // replicate the grip on an externally-driven puppet. This is a genuine engine limitation, not our bug:
            // the ornament is a separate Monster actor hand-bone-parented to the rider, and the native /cpose path
            // spawns/drives the pose-specific GRIP on that ornament actor through machinery the public timeline
            // primitive doesn't reach. TRIANGULATED: HMS, A Realm Repopulated (spawns NPC + plays 8065 → holds air),
            // AND Brio (spawns clone + torch-wave → holds air, using chara->Timeline.BaseOverride + PlayTimeline, the
            // IDENTICAL primitive to ours) all reproduce it. No plugin in the reference set solves it.
            //
            // GRACEFUL DEGRADATION (the good failure mode): ComputeOrnamentTimeline returns the ornament IDLE (7367)
            // for the stance range rather than the real stance clip, so the puppet holds the idle that KEEPS THE
            // PARASOL DRAWN. Result: parasol stays visible, sender's cpose cycles, receiver holds idle — "umbrella
            // stays, pose doesn't cycle," which reads far better in a housing scene than a vanishing parasol.
            // Attempting the real clip (S324b–h) either dropped the accessory or crashed the host on the diagnostic.
            // MARKED FOR FOLLOW-UP: the fix, if ever, is driving the ornament actor's own grip — likely via the same
            // path the native cpose uses (spawn-time pose attach), read safely through Brio's Skeleton→Bone graph
            // (NEVER the raw Attachment array — that AV'd the host at S324h). No deliberate gating added here; the
            // idle-return in ComputeOrnamentTimeline IS the degradation.

            // ── Gaze ──
            ApplyGazeTarget(character, data.TargetEntityId, info);
            ApplyFaceCamera(character, data.FaceCamera, info, data.FaceCamX, data.FaceCamY, data.FaceCamZ);
                ApplyGazeControl(character, data, info);

            // ── Visual body offset (applied LAST: it's a render-layer correction,
            // and anything above — position, mode/timeline, weapon — can mutate the
            // draw object, so we close the gap after they've run). ──
            ApplyBodyDrawOffset(native, data, info);

            // ── ORNPEER trace (S323m): puppet-side ornament diagnostic. The sender-side ORNAMENTTRACE shows the
            // sender holding tl0=7367 (its ornament idle) with pose 5 / cpose N. THIS shows what the PUPPET ends up
            // with after ALL applies — crucially after the resolver (ResolvePuppetAnimation, above), which sets the base idle and may
            // be overwriting the ornament idle. Two questions it answers: is the accessory OBJECT still on the puppet
            // (ornObj) — i.e. despawn vs render-state — and what timeline is the puppet REALLY playing (peerTl): if
            // it's a base locomotion value (3 idle) while the sender is at 7367, that confirms the override theory.
            // INF + change-gated so it's readable. REMOVE with the other traces once the hold is fixed.
            if (data.OrnamentId != 0)
            {
                ushort peerTl = (ushort)character->Timeline.TimelineSequencer.TimelineIds[0];
                ushort peerBO = (ushort)character->Timeline.BaseOverride;
                bool ornObj = character->OrnamentData.OrnamentObject != null;
                byte pPose = (byte)character->EmoteController.CurrentPoseType;
                byte pCpose = character->EmoteController.CPoseState;
                float peerRot = character->Rotation;
                var opSig = peerTl + "|" + peerBO + "|" + ornObj + "|" + pPose + "|" + pCpose + "|" + data.PoseType + "|" + data.CPoseState + "|" + isMoving + "|" + data.OrnamentTimeline;
                if (opSig != info.LastOrnPeerSig)
                {
                    // Gated behind debug/verbose mode (/hms debug) — off by default, available for the marked
                    // held-ornament-cpose follow-up. The ATTACHTRACE fields (Attach.ExecuteType/OwnerCharacter/
                    // AttachmentCount + the DrawObject vis/LoadState/RenderFlags reads, and the raw ChildTransform read
                    // that AV'd the host at S324h) were removed once they'd answered the diagnosis: the ornament stays
                    // parented (exec=3) and draw-visible (vis=1) yet renders invisible in the stance — a genuine
                    // externally-driven-actor limitation, not a field we can flip. See ComputeOrnamentTimeline.
                    if (LocalStateDetector.Verbose)
                        log.Information("[HMSync][ORNPEER] " + info.CharacterName + " peerTl=" + peerTl + " peerBO=" + peerBO + " ornObj=" + ornObj +
                            " peerPose=" + pPose + " peerCpose=" + pCpose + " peerRot=" + peerRot.ToString("F2") + " moving=" + isMoving +
                            " | wireTl=" + data.OrnamentTimeline + " wireRot=" + data.Rotation.ToString("F2") +
                            " wirePose=" + data.PoseType + " wireCpose=" + data.CPoseState + " orn=" + data.OrnamentId);
                    info.LastOrnPeerSig = opSig;
                }
            }
        }
    }

    /// <summary>
    /// Replicate the sender's measured visual body offset (DrawObject − Position, 3-axis)
    /// on the peer mannequin. The sender broadcasts the value every transform; the epoch
    /// flags when it changed. We react on epoch change (update target + active flag) and
    /// write the offset ABSOLUTELY (the peer is a puppet — its only draw offset is the one
    /// we impose, so the broadcast target IS the value to write; there is no base to add).
    ///
    /// Crash-safety (validated against SimpleHeels, which does this exact operation on
    /// Penumbra-managed actors at scale without crashing):
    ///  • Tier 2 — if Penumbra is mid-rebuild on this actor (between Creating/Created
    ///    CharacterBase), skip entirely. The draw object is being freed/recreated.
    ///  • Tier 1 — never write unless the draw object is non-null AND a valid CharacterBase.
    ///    A rebuild leaves the pointer briefly non-null-but-invalid; the type-check rejects
    ///    exactly that window (the native AV a null-check alone misses). We do NOT read
    ///    DrawObject->Object.Position to compute the write (that read is what faulted) — the
    ///    value comes only from the broadcast target, and DrawOffset is a GameObject field
    ///    (always-valid struct read) used solely to decide whether a write is needed.
    /// No clamp by design (see handover): a corrupt value should fail loudly during dev.
    /// </summary>
    private unsafe void ApplyBodyDrawOffset(GameObject* native, TransformData data, PeerInfo info)
    {
        if (data.BodyDrawOffsetEpoch != info.LastBodyDrawOffsetEpoch)
        {
            info.LastBodyDrawOffsetEpoch = data.BodyDrawOffsetEpoch;
            info.TargetBodyOffsetX = data.BodyDrawOffsetX;
            info.TargetBodyOffsetY = data.BodyDrawOffsetY;
            info.TargetBodyOffsetZ = data.BodyDrawOffsetZ;
            info.BodyOffsetActive =
                MathF.Abs(data.BodyDrawOffsetX) > BodyOffsetWriteEpsilon
                || MathF.Abs(data.BodyDrawOffsetY) > BodyOffsetWriteEpsilon
                || MathF.Abs(data.BodyDrawOffsetZ) > BodyOffsetWriteEpsilon;
        }

        // ── Seated-mode draw-offset handling (split by mode, see S53 comment below) ──
        var peerMode = (CharacterModes)data.CharMode;

        // S53 — CHAIR FIX: split the seated gate by mode.
        //
        // InPositionLoop (chair-sit, param=2): the native chair handler applies its own
        // DrawOffset (≈ −0.5 Y) to compensate the phantom PosY=0.524 rise. In earlier
        // builds that had no body-offset system, this compensation survived and chairs
        // looked correct. The unified seated gate introduced in the swim-offset work
        // actively cleared DrawOffset to 0 every frame, destroying the native compensation
        // → peer floats 0.5 units high ("above and behind"). Fix: just return — don't
        // apply OUR offset (swim etc.) but also don't clear the native handler's offset.
        //
        // EmoteLoop (groundsit/doze): native handler keeps PosY at floor (~0.019), no
        // phantom rise, no draw-offset compensation needed. Clearing residual offsets here
        // is still correct and safe (prevents stale swim offset from contaminating).
        if (peerMode == CharacterModes.InPositionLoop)
        {
            // Let native chair handler own DrawOffset entirely. Do not clear, do not write.
            // S55: during the standup window, the sender's data still shows InPositionLoop
            // (mode hasn't caught up yet) but the receiver has already exited the mode. The
            // sender's transitioning DrawOffset must flow through to track the descent.
            return;
        }

        if (peerMode == CharacterModes.EmoteLoop)
        {
            // Clear any residual offset so native placement isn't displaced.
            if (info.BodyOffsetActive || native->DrawOffset.Y != 0f
                || native->DrawOffset.X != 0f || native->DrawOffset.Z != 0f)
            {
                var dz = native->DrawObject;
                if (dz != null &&
                    dz->Object.GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
                {
                    native->SetDrawOffset(0, 0, 0);
                }
            }
            return;
        }

        // Tier 2: Penumbra is rebuilding this actor's body right now — do not touch the
        // draw object at all. Reasserted automatically once Created fires (next clean frame).
        if (rebuildingActors.Contains((nint)native)) return;

        try
        {
            if (info.BodyOffsetActive)
            {
                // Tier 1: validate the draw object before ANY access. Non-null is not
                // enough — mid-rebuild it can be non-null-but-not-yet-CharacterBase, which
                // is precisely the native-AV window. SimpleHeels gates on this exact check.
                var drawObj = native->DrawObject;
                if (drawObj == null) return;
                if (drawObj->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase) return;

                // Absolute write from the broadcast target, ALL THREE AXES.
                //
                // SEATDIAG proved: the peer has NO native seat-snap of its own (peerOwnDelta
                // horizontal stays 0), and the sender's horizontal seat placement is real
                // (senderDelta ≈ 0.42 on a chair). So we MUST supply the full horizontal —
                // dropping it (S10) left the peer off the cushion; putting it raw into
                // SetDrawOffset (pre-S10) landed it in the wrong axis (world +X showed up as
                // local +Z) because SetDrawOffset operates in the actor's LOCAL/model frame,
                // which is rotated from world by the peer's facing.
                //
                // Fix: rotate the world-space horizontal delta into the actor's local frame
                // by -facing before writing, so when the model frame re-applies +facing the
                // offset lands world-correct. Y is yaw-invariant — pass through unchanged.
                float yaw = native->Rotation; // FFXIV actor yaw, radians, about +Y
                float cos = MathF.Cos(yaw);
                float sin = MathF.Sin(yaw);
                float wx = info.TargetBodyOffsetX;
                float wz = info.TargetBodyOffsetZ;
                // World -> local (rotate by -yaw). NOTE sign: calibrated against live test —
                // the initial (+sin/−sin) form doubled the error on front/back facings while
                // sides stayed correct (the signature of an inverted rotation: error scales
                // with sin(yaw)). Flipping the sin terms lands it correctly on all facings.
                float localX = wx * cos - wz * sin;
                float localZ = wx * sin + wz * cos;

                var off = native->DrawOffset;
                if (MathF.Abs(localX - off.X) > BodyOffsetWriteEpsilon
                    || MathF.Abs(info.TargetBodyOffsetY - off.Y) > BodyOffsetWriteEpsilon
                    || MathF.Abs(localZ - off.Z) > BodyOffsetWriteEpsilon)
                {
                    native->SetDrawOffset(localX, info.TargetBodyOffsetY, localZ);
                }
            }
            else
            {
                // Self-heal: residual offset with no active state (e.g. session ended while
                // swimming). Clears once; fires only when non-zero, so it's not a per-frame
                // hammer. DrawOffset is a GameObject field — safe to read/clear without the
                // draw object being valid.
                var off = native->DrawOffset;
                if (off.X != 0f || off.Y != 0f || off.Z != 0f)
                {
                    native->SetDrawOffset(0, 0, 0);
                }
            }
        }
        catch (Exception ex)
        {
            // Tier 1 should make this unreachable for the draw-object case, but kept as the
            // last-resort safeguard: a managed fault degrades to a logged miss, not a CTD.
            // A native AV will NOT be caught here — if the log shows nothing before a crash,
            // the type-guard was bypassed and the fix needs revisiting (escalate to Tier 3,
            // hooking SetDrawOffset — see handover).
            log.Debug("[HMSync] Body draw offset apply skipped: " + ex.Message);
        }
    }

    /// <summary>
    /// Apply emote state from the unified snapshot. Uses emote epoch to prevent
    /// stale data from stomping emotes — no grace period needed.
    /// </summary>
    private unsafe void ApplyEmoteState(Character* character, TransformData data, PeerInfo info)
    {
        var newEpoch = data.EmoteEpoch;

        // No epoch change — no emote state change
        if (newEpoch == info.LastEmoteEpoch) return;

        // Epoch advanced — apply the new emote state
        info.LastEmoteEpoch = newEpoch;

        var emoteId = data.EmoteId;
        var timelineId = data.TimelineId;
        var charMode = (CharacterModes)data.CharMode;

        // ── Standup / emote end ──
        // The dedicated standup channel (StandupEpoch) handles chair and groundsit
        // standups with their own logic (grace period for chair, bare SetMode for
        // groundsit). This fallback catches any standup the dedicated channel missed.
        if (emoteId == 0 && info.EmoteActive)
        {
            info.EmoteActive = false;
            character->SetMode(CharacterModes.Normal, 0);
            log.Debug("[HMSync] Standup (fallback) on " + info.CharacterName);
            return;
        }

        // ── New emote start ──
        if (emoteId > 0 && emoteId != info.LastEmoteId)
        {
            info.LastEmoteId = emoteId;

            if (info.CposeInFlight)
            {
                info.CposeInFlight = false;
                log.Debug("[HMSync] Skipped emote replay (cpose in flight) for " +
                    emoteId + " on " + info.CharacterName);
                return;
            }

            // S322: forced loop → one-shot interrupt. If a persistent emote is still SetMode'd on this peer
            // and the incoming (different) emote arrives from a cleared sender state (charMode Normal), the
            // sender broke the old loop and fired a new emote. Drop the old mode first — otherwise a one-shot
            // new emote plays as an overlay and the still-resident loop resumes under it (the peer never sees
            // a clean interrupt). A natural seated overlay keeps emoteId == LastEmoteId and goes through the
            // seated-overlay branch below, not here; a persistent→persistent swap arrives with charMode !=
            // Normal (the new emote's own loop mode) and re-SetModes in ApplyEmoteFromSheet, so neither is
            // affected.
            if (info.EmoteActive && charMode == CharacterModes.Normal)
            {
                info.EmoteActive = false;
                character->SetMode(CharacterModes.Normal, 0);
                character->Timeline.BaseOverride = 0;
            }

            // S109: the S100 blanket gate (skip ALL emotes while armed+cpose>0) is
            // GONE — it ate every emote in that state (/wave completely ignored).
            // The S106 timeline-identity gate in ApplyEmoteFromSheet does the original
            // job precisely: it blocks only the cpose loop's own follow-up emote.
            if (DebugTrace) log.Debug("[EMOTETRACE] epoch emote=" + emoteId +
                " pose=" + data.PoseType + " cpose=" + data.CPoseState +
                " wpn=" + data.WeaponDrawn + " on " + info.CharacterName);
            ApplyEmoteFromSheet(character, emoteId, info);
            return;
        }

        // ── S113/S196: seated OR mounted sub-animation (overlay emote: /wave, /point, /yes) ──
        // CHECKED BEFORE the one-shot replay branch (S196): it's the more specific case (requires a
        // NEW overlay timelineId distinct from the held pose), and when mounted the replay branch's
        // !EmoteActive condition would otherwise steal this and route it through ApplyEmoteFromSheet
        // (which plays the STANDING emote and gets gated → the mounted /wave was silently dropped).
        // The sender's seated/mounted overlay bumps the epoch with the TIMELINE only (the emoteId
        // stays the underlying sit/mount emote). Upper-body overlay timelines (slot 1, e.g. 681 for
        // a mounted /wave) coexist with the base pose — sit loop OR mount idle (166) — so a plain
        // PlayTimeline overlays correctly without disturbing the base. Fire for both the seated case
        // (EmoteActive) and the mounted case (LastAppliedMountId != 0). Identity-gated against our own
        // seated pose stream for safety (harmless when mounted — HeldSeatedTimeline is 0 there).
        if (emoteId > 0 && emoteId == info.LastEmoteId
            && (info.EmoteActive || info.LastAppliedMountId != 0)
            && timelineId > 0
            && timelineId != info.HeldSeatedTimeline
            && timelineId != (ushort)(info.HeldSeatedTimeline + 1))
        {
            character->Timeline.TimelineSequencer.PlayTimeline(timelineId);
            return;
        }

        // ── One-shot replay (same emote ID, new epoch) ──
        if (emoteId > 0 && emoteId == info.LastEmoteId && !info.EmoteActive)
        {
            if (DebugTrace) log.Debug("[EMOTETRACE] replay emote=" + emoteId +
                " pose=" + data.PoseType + " cpose=" + data.CPoseState +
                " wpn=" + data.WeaponDrawn + " on " + info.CharacterName);
            ApplyEmoteFromSheet(character, emoteId, info);
            return;
        }

        // ── Movement cancelled emote (sender moving, no emote) ──
        if (emoteId == 0 && data.MoveState != 0 && info.EmoteActive)
        {
            info.EmoteActive = false;
            character->SetMode(CharacterModes.Normal, 0);
            character->Timeline.BaseOverride = 0;
            log.Debug("[HMSync] Emote cancelled by movement for " + info.CharacterName);
        }
    }

    /// <summary>
    /// Independent pose channel: mirror the sender's CPoseState/PoseType onto the peer.
    /// Runs every tick, orthogonal to emote/mode. The game renders the correct pose from
    /// CPoseState plus the stance it already tracks (mode, weapon), and blends the change
    /// itself — so we neither replay transition timelines nor special-case families. We only
    /// write when the value actually differs from the peer's current state, to avoid
    /// re-firing per-family animations (the weapon re-grip twitch). 255 (0xFF) is the
    /// game's weapon transit sentinel — the weapon handler owns that state; MirrorPoseState
    /// skips entirely when it sees 255.
    /// </summary>
    // S118: reverse map timeline → emote row. Covers BOTH slots: AT[1] (intro — what
    // the wire streams on a pose change) and AT[0] (loop — what the wire carries on a
    // revert to base, e.g. 643→emote 50). Lazy-built once from the Emote sheet.
    private Dictionary<ushort, (ushort emoteId, bool isIntro, ushort loopTimeline)>? timelineToEmote;

    private (ushort emoteId, bool isIntro, ushort loopTimeline) FindEmoteByTimeline(ushort timeline)
    {
        if (timelineToEmote == null)
        {
            timelineToEmote = new Dictionary<ushort, (ushort, bool, ushort)>();
            var sheet = dataManager.GetExcelSheet<Emote>();
            foreach (var row in sheet)
            {
                var loop = (ushort)row.ActionTimeline[0].RowId;
                var intro = (ushort)row.ActionTimeline[1].RowId;
                if (intro > 0 && !timelineToEmote.ContainsKey(intro))
                    timelineToEmote[intro] = ((ushort)row.RowId, true, loop);
                if (loop > 0 && !timelineToEmote.ContainsKey(loop))
                    timelineToEmote[loop] = ((ushort)row.RowId, false, loop);
            }
        }
        return timelineToEmote.TryGetValue(timeline, out var hit)
            ? hit : ((ushort)0, false, (ushort)0);
    }

    private unsafe void MirrorPoseState(Character* character, TransformData data, PeerInfo info, bool isMoving)
    {
        // S95: byte-mirror, no AnimLock. The S86 approach (let the game's resolver
        // slide into the pose from the bytes) was the cleanest visual — no twitch —
        // but decayed because the OLD sender sent the pose as a one-shot pulse.
        // The S92 sender fix now STREAMS the held timeline continuously, so the pose
        // no longer decays: we just re-assert the pose bytes every frame the sender
        // holds them. ONE writer (the game's resolver), fed a sustained signal —
        // nothing to row against, so the handoff race that plagued the AnimLock
        // approach (S88–S94) cannot occur by construction.
        byte wantPose = data.PoseType == 0xFF ? (byte)0 : data.PoseType;
        byte wantCpose = data.CPoseState;

        bool isWeaponDrawnPose = wantPose == (byte)EmoteController.PoseType.WeaponDrawn;

        // ── Weapon-drawn ALTERNATIVE cpose: re-assert bytes every frame while held ──
        // S97: wantCpose > 0 is REQUIRED — at default stance the sender still reports
        // PoseType=1 (weapon drawn) with CPose=0, and without this check the hold
        // branch claimed the revert frame, zeroed the tracking, and made the revert
        // clear (#3) unreachable — which also broke the sheathe clear (#4) downstream.
        if (isWeaponDrawnPose && data.WeaponDrawn && wantCpose > 0 && !isMoving)
        {
            // S108: suspended while a one-shot emote plays out (the hold's per-frame
            // byte re-assert was yanking the emote mid-play). The sender re-streams the
            // pose intro after its emote finishes — TimelineId>0 is the resume signal.
            if (info.PoseHoldSuspended)
            {
                if (data.TimelineId > 0)
                {
                    info.PoseHoldSuspended = false;
                    log.Debug("[HMSync] Pose hold resumed (tl=" + data.TimelineId +
                        ") on " + info.CharacterName);
                    // fall through — this frame re-enters the hold and PATs the intro
                }
                else
                {
                    return; // emote still in flight; stay hands-off
                }
            }

            if (character->Timeline.IsWeaponDrawn != data.WeaponDrawn)
                character->Timeline.IsWeaponDrawn = data.WeaponDrawn;
            character->EmoteController.CurrentPoseType = (EmoteController.PoseType)wantPose;
            character->EmoteController.CPoseState = wantCpose;

            // S100: FOLLOW the sender's timeline stream. The sender (S92) streams the
            // intro (3127) for its full duration, then flips to the loop (3128). We
            // mirror each change with PlayTimeline. Seams match by animation design:
            // the intro's first frame starts from idle stance (clean entry), the
            // intro's last frame == the loop's first frame (clean handoff).
            // S105: gap-free handoff via the native API. HOLDTRACE proved reactive
            // repair can't win: the intro lapses to 34 for ONE frame and the game's
            // cross-fade blending amplifies that into the visible rubber-band (blend
            // toward default + blend back). PlayActionTimeline(intro, intro+1) hands
            // off internally — no gap exists to amplify — and holds the loop natively
            // (no lapse, no replay guard needed).
            // Safe now, unlike the S83 era: sustained wire (S92), gated emote replay,
            // and explicit exit transitions (#3 plays 34, #4 evicts with 3) mean the
            // persistent loop is always REPLACED on exit, never left to go stale.
            // Wire-lag guard kept: ignore the intro packet of a loop we already hold.
            bool wireIsLagging =
                info.HeldWeaponTimeline == (ushort)(data.TimelineId + 1);

            if (!wireIsLagging
                && info.HeldWeaponTimeline != data.TimelineId && data.TimelineId > 0)
            {
                bool isLoopFlip = data.TimelineId == (ushort)(info.HeldWeaponTimeline + 1);
                if (isLoopFlip)
                {
                    // Sender's intro→loop flip: PAT already handed off natively.
                    // Just track; playing anything would restart the loop.
                    info.HeldWeaponTimeline = data.TimelineId;
                }
                else
                {
                    // New pose entry: intro with native handoff to intro+1.
                    character->Timeline.PlayActionTimeline(data.TimelineId,
                        (ushort)(data.TimelineId + 1));
                    info.HeldWeaponTimeline = data.TimelineId;
                    if (DebugTrace) log.Debug("[HOLDTRACE] PAT " + data.TimelineId + "→" +
                        (data.TimelineId + 1) + " on " + info.CharacterName);
                }
            }

            info.LastWeaponCpose = wantCpose;
            info.LastWeaponDrawn = true;
            return;
        }

        // ── S96 #3: cpose→default revert clear ──
        // Returning to the DEFAULT weapon stance (still drawn, CPose back to 0) doesn't
        // register from the byte change alone — the alternative pose's animation is still
        // resident on TL[0]. A one-shot blend to the weapon idle base (34) clears it.
        // Transition-only (not held) → no rowing.
        if (isWeaponDrawnPose && data.WeaponDrawn && wantCpose == 0
            && info.LastWeaponCpose > 0)
        {
            character->EmoteController.CurrentPoseType = (EmoteController.PoseType)wantPose;
            character->EmoteController.CPoseState = 0;
            character->Timeline.TimelineSequencer.PlayTimeline(34); // weapon idle base
            info.LastWeaponCpose = 0;
            info.HeldWeaponTimeline = 0;
            log.Debug("[HMSync] Weapon cpose→default revert clear on " + info.CharacterName);
            return;
        }

        // ── S96 #4 / S98: sheathe-from-pose clear ──
        // The phantom grip is the resident cpose loop (3128) on TL[0] BASE slot —
        // byte clears can't evict it (TL[0] drives the visual). The sheathe animation
        // (battle_end, tl 2) plays on the UPPERBODY slot (per the timeline sheet), so
        // playing the neutral idle (3) on base does NOT stomp it — different lanes.
        // Byte clear + base-slot evict together.
        if (!data.WeaponDrawn && info.LastWeaponDrawn && info.LastWeaponCpose > 0)
        {
            character->EmoteController.CurrentPoseType = (EmoteController.PoseType)0; // Idle
            character->EmoteController.CPoseState = 0;
            character->Timeline.TimelineSequencer.PlayTimeline(3); // evict loop from base
            info.LastWeaponCpose = 0;
            info.HeldWeaponTimeline = 0;
            info.LastWeaponDrawn = false;
            log.Debug("[HMSync] Sheathe-from-pose clear (bytes + base evict) on " +
                info.CharacterName);
            return;
        }
        else if (!data.WeaponDrawn)
        {
            info.LastWeaponDrawn = false;
            info.LastWeaponCpose = 0;
            info.HeldWeaponTimeline = 0;
        }

        // ── S106: IDLE STANDING cpose — parallel branch, same template as weapon ──
        // Mutually exclusive with the weapon branch by PoseType (0 vs 1); shares no
        // tracking fields with it. CharMode==Normal excludes seated/doze families
        // (those come in later builds with their own branches).
        bool isNormalMode = data.CharMode == (byte)CharacterModes.Normal;

        // ── S323e/f: ORNAMENT hold cpose — fashion-accessory pose families ──
        // Ornament holds live in TWO pose families: Umbrella=5 (parasol/shovel — held-in-hand, cpose-cycled) and
        // Accessory=6 (back/head/other). Neither matched the weapon (1) or idle (0) branch, so they fell through
        // and never reached the puppet — that's why hold cycling didn't sync. Byte-mirror the family + index every
        // frame. Unlike standing/weapon we do NOT replay an intro→loop timeline: the hold's idle loop (e.g. shovel
        // 7367) comes for free from the equipped ornament, and the held POSE renders from CPoseState, so streaming
        // the two bytes (which the sender holds continuously) is enough. Gated on an ornament being equipped so it
        // can't collide with the idle/weapon families. One-shot ACTION animations (dig / parasol open-close) are
        // NOT poses — they ride a separate channel.
        bool isOrnamentPose = wantPose == 5 || wantPose == 6;   // 5=Umbrella, 6=Accessory (EmoteController.PoseType)
        bool puppetInOrnamentPose =
            character->EmoteController.CurrentPoseType == (EmoteController.PoseType)5
            || character->EmoteController.CurrentPoseType == (EmoteController.PoseType)6;

        if (data.OrnamentId != 0 && isOrnamentPose && isNormalMode && !isMoving)
        {
            if (character->EmoteController.CurrentPoseType != (EmoteController.PoseType)wantPose)
                character->EmoteController.CurrentPoseType = (EmoteController.PoseType)wantPose;
            if (character->EmoteController.CPoseState != wantCpose)
                character->EmoteController.CPoseState = wantCpose;
            info.LastIdleCpose = wantCpose; // reuse idle tracking so a later revert-to-base clears cleanly
            return;
        }

        // S323e/f: ornament pose revert — the sender left the ornament pose families (cpose back to base, or the
        // ornament was put away) but the puppet is still parked in Umbrella/Accessory. Clear the bytes once so it
        // drops back to the ornament's default idle (or to neutral if the ornament's already gone). No return —
        // let the idle/other branches below still run this frame.
        if (!isOrnamentPose && puppetInOrnamentPose)
        {
            character->EmoteController.CurrentPoseType = (EmoteController.PoseType)0;
            character->EmoteController.CPoseState = 0;
        }

        if (wantPose == 0 && !data.WeaponDrawn && wantCpose > 0 && isNormalMode && !isMoving)
        {
            character->EmoteController.CurrentPoseType = (EmoteController.PoseType)0;
            character->EmoteController.CPoseState = wantCpose;

            bool idleWireLagging =
                info.HeldIdleTimeline == (ushort)(data.TimelineId + 1)
                && wantCpose == info.LastIdleCpose;

            if (!idleWireLagging
                && info.HeldIdleTimeline != data.TimelineId && data.TimelineId > 0)
            {
                // S107: a NEW POSE always changes CPoseState; a loop flip never does.
                // Arithmetic alone misclassifies here: standing intros are spaced 2
                // apart (3123/4, 3125/6 ...), so pose B's intro (3125) == pose A's
                // loop (3124)+1 — the "+1 = flip" shortcut swallowed every other
                // /cpose. CPose delta is the authoritative signal.
                bool isNewPose = wantCpose != info.LastIdleCpose;
                bool isLoopFlip = !isNewPose
                    && data.TimelineId == (ushort)(info.HeldIdleTimeline + 1);

                if (isLoopFlip)
                {
                    info.HeldIdleTimeline = data.TimelineId;
                }
                else
                {
                    character->Timeline.PlayActionTimeline(data.TimelineId,
                        (ushort)(data.TimelineId + 1));
                    info.HeldIdleTimeline = data.TimelineId;
                    if (DebugTrace) log.Debug("[HOLDTRACE] idle PAT " + data.TimelineId + "→" +
                        (data.TimelineId + 1) + " on " + info.CharacterName);
                }
            }

            info.LastIdleCpose = wantCpose;
            return;
        }

        // ── S106: idle cpose→default revert ──
        // The wire carries the family base on revert (tl=3 for standing idle) — no
        // hardcoded table needed. One-shot, transition-only.
        if (wantPose == 0 && !data.WeaponDrawn && wantCpose == 0
            && info.LastIdleCpose > 0)
        {
            character->EmoteController.CurrentPoseType = (EmoteController.PoseType)0;
            character->EmoteController.CPoseState = 0;
            ushort baseTl = data.TimelineId > 0 ? data.TimelineId : (ushort)3;
            character->Timeline.TimelineSequencer.PlayTimeline(baseTl);
            info.LastIdleCpose = 0;
            info.HeldIdleTimeline = 0;
            return;
        }

        // ── S112: SEATED/DOZE cpose — chair, groundsit, bed (one branch, shared mode
        // space). Same template, two seated-specific rules: (1) NEVER touch CharMode —
        // sit-down/standup machinery owns it; we only play timelines and mirror the
        // cpose byte. (2) Revert (cpose→0) plays the wire timeline PLAIN — the seated
        // base is a resident loop and +1 from it is unverified territory.
        var seatedMode = (CharacterModes)data.CharMode;
        bool isSeatedFamily = seatedMode == CharacterModes.InPositionLoop
            || seatedMode == CharacterModes.EmoteLoop;

        if (isSeatedFamily && info.EmoteActive && !isMoving)
        {
            // S113 DIAG: catch the revert in the act. The wire is proven correct
            // (intro/sustain stream clean); something on the PUPPET reverts the pose
            // after the PAT blend plays. Suspects: (a) the InPositionLoop mode
            // machinery re-resolving its own loop over ours, (b) PAT disturbing the
            // CPose byte, (c) TL0 lapse. Change-gated — logs transitions only.
            {
                ushort sTl0 = (ushort)character->Timeline.TimelineSequencer.TimelineIds[0];
                byte sCpose = character->EmoteController.CPoseState;
                byte sMode = (byte)character->Mode;
                if (sTl0 != info.SeatDiagTl0 || sCpose != info.SeatDiagCpose
                    || sMode != info.SeatDiagMode)
                {
                    log.Debug("[SEATDIAG] TL0=" + info.SeatDiagTl0 + "→" + sTl0 +
                        " cposeByte=" + info.SeatDiagCpose + "→" + sCpose +
                        " mode=" + info.SeatDiagMode + "→" + sMode +
                        " wireCpose=" + wantCpose + " wireTl=" + data.TimelineId +
                        " held=" + info.HeldSeatedTimeline +
                        " on " + info.CharacterName);
                    info.SeatDiagTl0 = sTl0;
                    info.SeatDiagCpose = sCpose;
                    info.SeatDiagMode = sMode;
                }
            }

            // Mirror the pose state (the resolver reads the TRIPLET: EmoteId at 0x14,
            // CurrentPoseType at 0x20, CPoseState at 0x21 — EMOCTLDUMP proved these
            // are the only bytes that track seated pose changes on the sender; S118
            // failed because we wrote only two of the three legs).
            if ((byte)character->EmoteController.CurrentPoseType != wantPose)
                character->EmoteController.CurrentPoseType =
                    (EmoteController.PoseType)wantPose;
            if (character->EmoteController.CPoseState != wantCpose)
                character->EmoteController.CPoseState = wantCpose;

            bool seatedWireLagging =
                info.HeldSeatedTimeline == (ushort)(data.TimelineId + 1)
                && wantCpose == info.LastSeatedCpose;

            if (!seatedWireLagging
                && info.HeldSeatedTimeline != data.TimelineId && data.TimelineId > 0)
            {
                bool isNewPose = wantCpose != info.LastSeatedCpose;
                bool isLoopFlip = !isNewPose
                    && data.TimelineId == (ushort)(info.HeldSeatedTimeline + 1);

                if (isLoopFlip)
                {
                    info.HeldSeatedTimeline = data.TimelineId;
                }
                else if (isNewPose)
                {
                    // S118: THE FIX — the resolver's true input is EmoteController.EmoteId.
                    // Seated cpose variants are HIDDEN EMOTE ROWS (chair: 95/96/254/255,
                    // base sit: 50), exactly like the standing ones (91/92/107...).
                    // The InPositionLoop resolver reads EmoteId → that emote's AT[0]
                    // (the loop). Our sit-down wrote EmoteId=50, so it eternally
                    // resolved 643 over everything we played. Write the variant's
                    // EmoteId and the resolver is on OUR side. Loop comes from the
                    // SHEET (AT[0]) — never +1 arithmetic (revert tl 643+1 = 644 =
                    // chair STANDUP; the shortcut would fire a standup).
                    var match = FindEmoteByTimeline(data.TimelineId);
                    if (match.emoteId > 0)
                    {
                        character->EmoteController.EmoteId = match.emoteId;
                        character->EmoteController.CPoseState = wantCpose;

                        if (match.isIntro && match.loopTimeline > 0)
                        {
                            // Wire carries the intro: blend intro → sheet loop.
                            character->Timeline.PlayActionTimeline(data.TimelineId,
                                match.loopTimeline);
                        }
                        else
                        {
                            // Wire carries the loop itself (e.g. revert to base 643):
                            // play it plain; the resolver now holds it natively.
                            character->Timeline.TimelineSequencer.PlayTimeline(
                                data.TimelineId);
                        }

                        info.HeldSeatedTimeline = data.TimelineId;
                        log.Debug("[HMSync] Seated cpose emote=" + match.emoteId +
                            " tl=" + data.TimelineId + " loop=" + match.loopTimeline +
                            " on " + info.CharacterName);
                    }
                    else
                    {
                        // S121: NO sheet match = this is NOT a pose — it's an emote
                        // interrupting the cpose (e.g. /wave from a seated cpose flips
                        // CPose→0, so isNewPose fires with the WAVE's timeline). Do NOT
                        // claim it in HeldSeatedTimeline — S120 did, and S113's
                        // identity gate then blocked the emote's own playback (we were
                        // gating ourselves). Mirror the bytes, leave the timeline to
                        // the emote channel (epoch-driven S113 branch).
                        log.Debug("[HMSync] Seated tl=" + data.TimelineId +
                            " unmatched — left to emote channel on " + info.CharacterName);
                    }
                }
            }

            info.LastSeatedCpose = wantCpose;
            return;
        }
        // Leaving the seated family (standup etc.): clear tracking, let the existing
        // standup machinery do its job untouched.
        if (!isSeatedFamily && (info.HeldSeatedTimeline > 0 || info.LastSeatedCpose > 0))
        {
            info.HeldSeatedTimeline = 0;
            info.LastSeatedCpose = 0;
        }

        // ── All other poses: write only on change (native handling) ──
        byte curPose = (byte)character->EmoteController.CurrentPoseType;
        byte curCpose = character->EmoteController.CPoseState;
        if (wantPose == curPose && wantCpose == curCpose) return;

        character->EmoteController.CurrentPoseType = (EmoteController.PoseType)wantPose;
        character->EmoteController.CPoseState = wantCpose;
    }

    private unsafe void ApplyEmoteFromSheet(Character* character, ushort emoteRowId, PeerInfo info)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Emote>();
            if (sheet == null) return;

            var entry = sheet.GetRow(emoteRowId);
            var timelineId = (ushort)entry.ActionTimeline[0].RowId;
            if (timelineId == 0) return;

            // S106: timeline-identity gate. If this emote's timeline IS the pose loop
            // a PAT hold already owns (idle or weapon branch), playing it would restart
            // the loop — the old partial-reloop. Gated by IDENTITY, not by state, so a
            // genuine emote played while standing in a cpose (/panic with CPose>0)
            // passes untouched: its timeline can't equal the held loop.
            if (timelineId != 0 &&
                (timelineId == info.HeldIdleTimeline
                 || timelineId == (ushort)(info.HeldIdleTimeline + 1) && info.HeldIdleTimeline > 0
                 || timelineId == info.HeldWeaponTimeline
                 || timelineId == (ushort)(info.HeldWeaponTimeline + 1) && info.HeldWeaponTimeline > 0))
            {
                log.Debug("[HMSync] Emote " + emoteRowId + " gated (tl " + timelineId +
                    " held by pose channel) on " + info.CharacterName);
                return;
            }

            // S107: a real emote playing through BREAKS any pose hold (mirrors the
            // game: /wave interrupts the held cpose, plays fully, then the sender's
            // game reassumes the pose by replaying its intro — which the sender
            // re-streams, and a zeroed tracker lets it re-PAT with full blend).
            // Without this reset, the stale held value gates the reassume forever.
            bool brokeWeaponHold = info.HeldWeaponTimeline > 0; // S110: PAT-replace below
            if (info.HeldIdleTimeline > 0 || info.HeldWeaponTimeline > 0)
            {
                info.HeldIdleTimeline = 0;
                info.HeldWeaponTimeline = 0;
                // S108: also SUSPEND the hold branch. During the emote the wire still
                // carries CPose>0, and the weapon hold's per-frame byte re-assert makes
                // the game's stance resolver yank the body back to the stance mid-emote
                // (the "/wave interrupted" bug — we were yanking ourselves). Suspension
                // ends when the sender re-streams the pose intro (TimelineId>0 again);
                // the wire is the clock, no timers.
                info.PoseHoldSuspended = true;
                log.Debug("[HMSync] Pose hold broken+suspended by emote " + emoteRowId +
                    " on " + info.CharacterName);
            }

            var conditionMode = (CharacterModes)entry.EmoteMode.Value.ConditionMode;

            if (conditionMode != 0)
            {
                // Persistent emote — SetMode + intro timeline
                character->Timeline.BaseOverride = 0;
                character->SetMode(conditionMode, (byte)entry.EmoteMode.RowId);
                character->Timeline.IsWeaponDrawn = entry.DrawsWeapon;

                var introTimeline = (ushort)entry.ActionTimeline[1].RowId;
                if (introTimeline > 0)
                {
                    character->Timeline.TimelineSequencer.PlayTimeline(introTimeline);
                    log.Debug("[HMSync] Emote " + emoteRowId + " mode=" + conditionMode +
                        " intro=" + introTimeline + " on " + info.CharacterName);
                }
                else
                {
                    character->Timeline.TimelineSequencer.PlayTimeline(timelineId);
                    log.Debug("[HMSync] Emote " + emoteRowId + " mode=" + conditionMode +
                        " loop=" + timelineId + " on " + info.CharacterName);
                }

                info.EmoteActive = true;
            }
            else
            {
                // One-shot emote — play animation but do NOT set EmoteActive.
                if (brokeWeaponHold)
                {
                    // S110: a bare PlayTimeline cannot replace a PAT persistent loop —
                    // the held cpose loop reasserted over the emote within a frame
                    // ("/wave completely ignored" from the alt stance; fine from the
                    // default stance where no PAT loop is resident). Principle #4 from
                    // the saga doc, other direction: persistent state is REPLACED.
                    // PAT(emote, 34): the emote plays fully, hands off natively to the
                    // armed idle; the sender's re-streamed intro then re-PATs the
                    // reassume. Also: do NOT write entry.DrawsWeapon here — for /wave
                    // it's false and was sheathing the armed puppet's weapon flag.
                    character->Timeline.PlayActionTimeline(timelineId, 34);
                    if (DebugTrace) log.Debug("[HMSync] Emote " + emoteRowId + " one-shot via PAT(tl=" +
                        timelineId + ",34) replacing weapon hold on " + info.CharacterName);
                }
                else
                {
                    character->Timeline.TimelineSequencer.PlayTimeline(timelineId);

                    if (character->Timeline.IsWeaponDrawn)
                    {
                        // S122: armed one-shot. The sender's own trace during an armed
                        // wave shows WpnDrawn=TRUE + PoseType=255 — the game KEEPS the
                        // flag and the transit state stows the weapon visually for the
                        // emote's duration. Writing DrawsWeapon (false) here made the
                        // weapon handler fight it (re-flip + battle_start mid-emote =
                        // weapon stuck in one hand). Mirror the sender: flag untouched,
                        // sentinel written; the wire's pose bytes restore afterwards.
                        character->EmoteController.CurrentPoseType =
                            (EmoteController.PoseType)0xFF;
                    }
                    else
                    {
                        character->Timeline.IsWeaponDrawn = entry.DrawsWeapon;
                    }

                    if (DebugTrace) log.Debug("[HMSync] Emote " + emoteRowId + " one-shot tl=" + timelineId +
                        " on " + info.CharacterName);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] ApplyEmote failed for " + emoteRowId + ": " + ex.Message);
        }
    }

    private unsafe ushort ComputeJumpTimeline(Character* character, TransformData data, PeerInfo info)
    {
        var isArmed = character->Timeline.IsWeaponDrawn;
        // S197b: handles both unmounted AND mounted jumps. GetJumpTimeline's GroundMount case falls to
        // the default → GndJump* (start/fall/land), which the receiver animates on the mounted puppet
        // (proven via reverse-mount). The jump phase for a mounted sender is captured from the mount
        // object's slot 0 (the rider timeline holds only the seated pose).
        var tl = LocomotionData.GetJumpTimeline(data.MoveMode, isArmed, data.JumpPhase);
        return tl != 0 ? tl : info.LastAppliedAnim; // no clip for this phase → no change
    }

    private unsafe ushort ComputeBaseMoveTimeline(Character* character, TransformData data, TransformData? prevData, PeerInfo info)
    {
        var isArmed = character->Timeline.IsWeaponDrawn;
        var mode = data.MoveMode;
        var moveState = data.MoveState;

        // (S196b: the GroundMount→Ground remap was removed — mounted locomotion is driven on the MOUNT OBJECT's
        // own slot-0 timeline, not the rider, so this only ever sees genuine on-foot modes now.)

        ushort targetAnim = 0;

        if (moveState != 0 && prevData != null)
        {
            var direction = LocomotionData.ComputeDirection(
                data.X - prevData.X, data.Z - prevData.Z, data.Rotation, info.LastMoveDir);
            info.LastMoveDir = direction;
            targetAnim = LocomotionData.GetTimeline(mode, isArmed, moveState, direction);
        }
        else if (moveState != 0)
        {
            targetAnim = LocomotionData.GetTimeline(mode, isArmed, moveState, LocomotionData.DirForward);
        }
        else if (moveState == 0 && data.IsTurning && prevData != null)
        {
            var rotDelta = LocomotionData.WrapAngle(data.Rotation - prevData.Rotation);
            if (MathF.Abs(rotDelta) > LocomotionData.MinTurnDelta)
                targetAnim = LocomotionData.GetTurnTimeline(mode, isArmed, rotDelta > 0);
        }

        if (targetAnim != 0)
            return targetAnim;
        if (mode != LocomotionData.ModeGround)
        {
            // Non-ground idle (swim/fly idle). If GetTimeline yields nothing, return LastAppliedAnim ("no change")
            // so we preserve the prior behaviour of NOT clearing BaseOverride in that case.
            var modeIdle = LocomotionData.GetTimeline(mode, isArmed, LocomotionData.SpeedIdle, LocomotionData.DirForward);
            return modeIdle != 0 ? modeIdle : info.LastAppliedAnim;
        }
        return 0; // ground idle → clear BaseOverride
    }

    /// <summary>
    /// S323s — Phase 1 of the locomotion refactor: the single entry point for resolving a puppet's
    /// locomotion/pose animation each frame. Right now this is a behaviour-preserving DISPATCH SHELL — it routes
    /// to the existing tier functions in an explicit priority ladder, exactly reproducing the inline block it
    /// replaced (no reorder, no logic change). Phase 2 folds the tier WRITES in here (and pulls MirrorPoseState's
    /// pose bytes + the redundant ornament-action channel into it) so this becomes the SOLE writer of tl0 /
    /// BaseOverride — which is what makes the whole "last writer by line-order wins" bug class impossible.
    ///
    /// Priority ladder (highest wins): emote > ornament > jump > base locomotion/idle. (Mount is still driven on
    /// the mount object's own slot-0 in the per-frame apply; Phase 5 brings it through this tier too.)
    /// </summary>
    private unsafe void ResolvePuppetAnimation(Character* character, TransformData data, TransformData? prevData, PeerInfo info)
    {
        // 1. Emote owns the timeline (applied by ApplyEmoteState); the resolver yields.
        if (info.EmoteActive)
            return;

        // Compute the winning timeline via the priority ladder. Each tier is now PURE — it returns the target
        // timeline and does NOT touch BaseOverride/PlayTimeline itself. Convention: return a non-zero clip to play,
        // 0 to clear to the base idle, or info.LastAppliedAnim to mean "no change" (e.g. an unknown mode-idle).
        ushort target;
        if (data.OrnamentId != 0)
            target = ComputeOrnamentTimeline(character, data, prevData, info); // ornament locomotion / hold
        else if (data.JumpPhase != LocomotionData.JumpNone)
            target = ComputeJumpTimeline(character, data, info);               // jump
        else
            target = ComputeBaseMoveTimeline(character, data, prevData, info); // base locomotion / idle

        // S328ai: locodiag hook — log the resolver's inputs + resolved target for this peer this frame (edge-based).
        LocoDiag?.OnResolve(info.PeerId, info.CharacterName, data.JumpPhase, data.MoveState, data.IsTurning,
            data.MountId, target, info.LastAppliedAnim, info.EmoteActive);

        // 2. ── The single write of tl0 / BaseOverride ── ONE writer, so no "last-writer-by-line-order" race.
        // BaseOverride is asserted every frame (SUSTAIN) for LOOPING clips (walk/run/idle — held indefinitely).
        // BUT a one-shot TERMINAL clip (jump LANDING) must NOT be sustained: the sender reports JumpLanding for a
        // variable 8-14 frames (sampling + clip duration), and pinning tl33 past the land clip's ~9-frame natural
        // length re-shows its end-state (the knee-bend squat) for the extra frames → the "double-squat" glitch
        // (confirmed via locodiag: landing held 13-14f in bad jumps vs 8-10f in clean ones, 1 PlayTimeline fire).
        // Fix: fire landing ONCE, then let it play out to idle — do not re-assert BaseOverride on the held frames.
        bool targetIsLandingOneShot = target != 0 && LocomotionData.IsLandingClip(target);
        if (target != 0)
        {
            if (info.LastAppliedAnim != target)
            {
                if (targetIsLandingOneShot)
                {
                    // One-shot landing: RELEASE BaseOverride (0 = clear-to-natural, same as the idle-clear below) so
                    // the lingering falling-clip pin doesn't fight the landing, then play it ONCE. It runs to
                    // completion and hands off to idle naturally — never held on its final squat frame.
                    character->Timeline.BaseOverride = 0;
                }
                else
                {
                    // Looping clip: pin it (sustained across frames).
                    character->Timeline.BaseOverride = target;
                }
                character->Timeline.TimelineSequencer.PlayTimeline(target);
                LocoDiag?.OnPlayTimeline(info.PeerId, target);   // S328ai
                info.LastAppliedAnim = target;
            }
            else if (!targetIsLandingOneShot)
            {
                // Subsequent frames: re-assert sustain ONLY for looping clips. A landing one-shot is left alone
                // (BaseOverride already released) so it completes naturally instead of freezing on its last frame.
                character->Timeline.BaseOverride = target;
            }
        }
        else if (info.LastAppliedAnim != 0)
        {
            character->Timeline.BaseOverride = 0;
            info.LastAppliedAnim = 0;
        }
    }

    /// <summary>
    /// S323q — the ornament LOCOMOTION tier, parallel to ComputeBaseMoveTimeline but for a puppet holding a fashion
    /// accessory. Phase 0 established (a) an accessory only draws while the puppet plays the ornament's OWN
    /// timelines, and (b) those walk timelines are TRANSIENT on the sender — the directional walk fires at
    /// movement-start, then the sender's tl0 reverts to idle (7367) while the character keeps walking. So the
    /// S323n/o raw mirror got the walk-start for one frame and idle for the rest → the puppet stood and slid.
    /// This drives it receiver-side like base locomotion: compute direction from the puppet's OWN position delta
    /// and SUSTAIN the ornament directional walk via BaseOverride.
    ///
    /// The ornament locomotion SET derives from the universal ornament idle 7367 (consecutive, mirroring the base
    /// fwd/left/right/back layout): turns 7368/7369, walk 7370/7371/7372/7373 = 7370 + ComputeDirection
    /// (Forward=0, Left=1, Right=2, Backward=3). Confirmed for the parasol; assumed universal because 7367 is the
    /// universal ornament idle — validate against another hand-held ornament and, if a set differs, latch the walk
    /// base from the sender's OrnamentTimeline broadcasts instead of deriving it. When stationary, fall back to the
    /// sender-authoritative mirror: idle 7367, a held cpose (byte-mirror sets the stance; 7367 renders it), or a
    /// one-shot action (dig/emote) the sender broadcasts live.
    /// </summary>
    private unsafe ushort ComputeOrnamentTimeline(Character* character, TransformData data, TransformData? prevData, PeerInfo info)
    {
        const ushort OrnIdle = 7367;
        const ushort OrnWalkForward = 7370; // 7370 fwd, 7371 left, 7372 right, 7373 back (= 7370 + direction)
        const ushort OrnTurnLeft = 7368, OrnTurnRight = 7369;

        ushort target = 0;

        if (data.MoveState != 0 && prevData != null)
        {
            var dir = LocomotionData.ComputeDirection(
                data.X - prevData.X, data.Z - prevData.Z, data.Rotation, info.LastMoveDir);
            info.LastMoveDir = dir;
            // S323r: the walk set (7370-7373) and the RUN set differ — deriving one set for all speeds made run
            // play as a fast walk. The sender's live tl0 IS the correct clip for its current speed+direction
            // (transient — it fires at movement start, then reverts to idle), so latch it keyed by
            // MoveState (1=walk, 2=run, 3=sprint) × direction and sustain the latched value; ignore the revert.
            // Fall back to the derived walk clip only until this cell is first latched.
            int idx = ((data.MoveState & 3) * 4) + dir;
            if (data.OrnamentTimeline != 0 && data.OrnamentTimeline != OrnIdle)
                info.OrnLoco[idx] = data.OrnamentTimeline;
            target = info.OrnLoco[idx];
            if (target == 0) target = (ushort)(OrnWalkForward + dir);
        }
        else if (data.MoveState != 0)
        {
            target = OrnWalkForward; // moving but no prior sample — assume forward
        }
        else if (data.IsTurning && prevData != null)
        {
            var rotDelta = LocomotionData.WrapAngle(data.Rotation - prevData.Rotation);
            if (MathF.Abs(rotDelta) > LocomotionData.MinTurnDelta)
                target = rotDelta > 0 ? OrnTurnRight : OrnTurnLeft;
        }

        if (target != 0)
            return target;                // moving / turning: the directional ornament clip

        // Stationary. A HELD CPOSE STANCE arrives as one of the shared "cpose while holding an item" emote-pose
        // timelines (8062–8068 — confirmed identical for parasol AND shovel, so shared, not per-item).
        //
        // KNOWN LIMITATION (thread IX, S324i — see the post-resolver block for the full triangulation): forcing the
        // real stance clip (8062/8065/8067) makes the held ornament (parasol/shovel/torch) vanish on the puppet,
        // because the ornament is a separate hand-bone-parented Monster actor whose pose-specific GRIP the native
        // /cpose spawns but the public BaseOverride+PlayTimeline primitive does not — reproduced identically in HMS,
        // A Realm Repopulated, and Brio. So we DELIBERATELY return the ornament IDLE (7367) for the stance range:
        // the puppet holds the idle that keeps the parasol DRAWN, giving the good failure mode — "umbrella stays,
        // pose doesn't cycle" — instead of a vanishing accessory. Back-item ornaments (knapsack) are unaffected;
        // they use native cposes and sync fine. Genuine one-shots (dig 13383, emotes 8073/8074/8194) and the plain
        // idle fall through to the ot>0 return below and play normally.
        ushort ot = data.OrnamentTimeline;
        if (ot >= 8062 && ot <= 8068)
            return OrnIdle;                // held cpose stance → hold ornament idle so the parasol stays drawn (grip doesn't replicate — known limitation)
        if (ot > 0)
            return ot;                     // plain idle 7367 / dig / emote — play it
        return OrnIdle;                    // no signal yet → hold the ornament idle so the accessory stays drawn
    }

    private unsafe void ApplyGazeTarget(Character* character, uint targetEntityId, PeerInfo info)
    {
        if (info.LastTargetEntityId == targetEntityId) return;
        info.LastTargetEntityId = targetEntityId;
        character->SetTargetId(targetEntityId == 0 ? (ulong)0 : targetEntityId);
    }

    // /facecamera fourth-wall stare — driven the way BRIO does it (ActorLookAtService). The missing piece all
    // along: writing the look-at params directly isn't enough — the game runs a native UPDATE function to turn the
    // target into head movement, and it does NOT auto-run for puppets. So we CALL that function (updateLookAt)
    // ourselves, per-frame, once for each slot (0=Body, 1=Head, 2=Eyes), passing a LookAtTarget with
    // LookMode=3 (Position) + the frozen point. The point is FROZEN (sender snapshot) so the stare holds.
    private unsafe void ApplyFaceCamera(Character* character, bool faceCamera, PeerInfo info,
        float fcX, float fcY, float fcZ)
    {
        bool edge = info.LastFaceCamera != faceCamera;
        info.LastFaceCamera = faceCamera;

        if (updateLookAt == null) return;   // sig didn't resolve — no-op
        var controller = &character->LookAt.Controller;

        if (faceCamera)
        {
            var tgt = new LookAtTargetNative
            {
                LookMode = 3,   // Position
                Position = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(fcX, fcY, fcZ)
            };
            updateLookAt(controller, &tgt, 0, 0);   // Body
            updateLookAt(controller, &tgt, 1, 0);   // Head
            updateLookAt(controller, &tgt, 2, 0);   // Eyes
        }
        else if (edge)
        {
            // Clear: LookMode=0 (None) on each slot returns the head to normal.
            var tgt = new LookAtTargetNative { LookMode = 0 };
            updateLookAt(controller, &tgt, 0, 0);
            updateLookAt(controller, &tgt, 1, 0);
            updateLookAt(controller, &tgt, 2, 0);
        }

        if (edge && FaceCamDiag)
            log.Information("[HMSync] [FACECAM] set=" + faceCamera + " frozenPoint=(" +
                fcX.ToString("F1") + "," + fcY.ToString("F1") + "," + fcZ.ToString("F1") + ")");
    }
    public static bool FaceCamDiag = true;   // diagnostic — logs the face-cam edge + the frozen point applied

    // Dynamic face control — the general per-slot version of face-camera. Drives eyes/body/head independently via the
    // same native updateLookAt call. Each slot has its own on-flag + world target. Slot indices: 0=Body, 1=Head, 2=Eyes.
    // Tracks last on-state per slot to release cleanly. Re-asserts active slots every frame (as face-camera does).
    // Apply the local player's own face-control gaze so they SEE it while setting it (apply only drives puppets;
    // the self-actor is never a puppet). Called each frame from capture for the local player. Uses the same
    // updateLookAt path proven by face-camera. Self on-state tracked separately from peers.
    private bool selfGazeEyesWasOn, selfGazeBodyWasOn, selfGazeHeadWasOn;
    public unsafe void ApplyGazeToLocal()
    {
        if (updateLookAt == null) return;
        var lp = Control.GetLocalPlayer();
        if (lp == null) return;
        var character = (Character*)lp;
        var controller = &character->LookAt.Controller;
        DriveGazeSlot(controller, 2, FaceControlState.EyesOn, FaceControlState.Eyes.X, FaceControlState.Eyes.Y, FaceControlState.Eyes.Z, ref selfGazeEyesWasOn);
        DriveGazeSlot(controller, 0, FaceControlState.BodyOn, FaceControlState.Body.X, FaceControlState.Body.Y, FaceControlState.Body.Z, ref selfGazeBodyWasOn);
        DriveGazeSlot(controller, 1, FaceControlState.HeadOn, FaceControlState.Head.X, FaceControlState.Head.Y, FaceControlState.Head.Z, ref selfGazeHeadWasOn);
    }

    private unsafe void ApplyGazeControl(Character* character, TransformData d, PeerInfo info)
    {
        if (updateLookAt == null) return;
        // Idle short-circuit: when this peer has no slot active AND none pending release (nothing was on), skip the
        // three drive calls entirely. The common case (nobody face-controlling) then costs one bool check per frame.
        bool anyOn = d.GazeEyesOn || d.GazeBodyOn || d.GazeHeadOn;
        bool anyPendingRelease = info.GazeEyesWasOn || info.GazeBodyWasOn || info.GazeHeadWasOn;
        if (!anyOn && !anyPendingRelease) return;

        var controller = &character->LookAt.Controller;
        DriveGazeSlot(controller, 2, d.GazeEyesOn, d.GazeEyesX, d.GazeEyesY, d.GazeEyesZ, ref info.GazeEyesWasOn);   // Eyes
        DriveGazeSlot(controller, 0, d.GazeBodyOn, d.GazeBodyX, d.GazeBodyY, d.GazeBodyZ, ref info.GazeBodyWasOn);   // Body
        DriveGazeSlot(controller, 1, d.GazeHeadOn, d.GazeHeadX, d.GazeHeadY, d.GazeHeadZ, ref info.GazeHeadWasOn);   // Head
    }

    private unsafe void DriveGazeSlot(CharacterLookAtController* controller, uint slot, bool on,
        float x, float y, float z, ref bool wasOn)
    {
        if (on)
        {
            var tgt = new LookAtTargetNative { LookMode = 3, Position = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(x, y, z) };
            updateLookAt(controller, &tgt, slot, 0);
            wasOn = true;
        }
        else if (wasOn)
        {
            var tgt = new LookAtTargetNative { LookMode = 0 };   // release
            updateLookAt(controller, &tgt, slot, 0);
            wasOn = false;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float LerpAngle(float a, float b, float t)
    {
        var diff = b - a;
        while (diff > MathF.PI) diff -= MathF.Tau;
        while (diff < -MathF.PI) diff += MathF.Tau;
        return a + diff * t;
    }

    public void Dispose()
    {
        Stop();
    }
}

public class PeerInfo
{
    public string PeerId { get; set; } = "";
    public uint EntityId { get; set; }
    // S327: the STABLE, globally-unique identity key. Character.ContentId (ulong @0x2358) — same across all worlds,
    // survives zone/render-range changes, world-travel-proof (the «Wanderer»/«Traveler» tag is cosmetic). This is the
    // key we bind on; EntityId is ephemeral/client-local and Name collides (two same-named chars via world travel).
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = "";
    public long JoinSequence { get; set; }   // S326h: arrival order for the participant #-column (stable per peer)
    public ushort? ObjectIndex { get; set; }
    // v0.7.328: the peer's REAL position captured ONCE at first bind — before HMS starts overriding it with synthetic
    // coords. Because the packet firewall pins the peer at their session-start spot server-side (they read as idle),
    // this captured position IS the server-truth position for the whole session. On teardown we write it back onto the
    // preserved actor to undo the frozen synthetic override (the "peer stuck at ~75u on return" bug). Captured flag
    // guards against re-capture on later re-binds (which would grab the synthetic coords instead of the true origin).
    public System.Numerics.Vector3? OriginPosition { get; set; }
    public bool OriginCaptured { get; set; }
    public ushort LastAppliedAnim { get; set; }
    public byte LastMoveDir { get; set; }      // S322i: last locomotion direction bin (forward/strafe hysteresis)
    public float FacingOffset { get; set; }    // S322j: smoothed heading lean toward travel on a diagonal
    public bool EmoteActive { get; set; }
    public ushort LastEmoteId { get; set; }
    public uint LastEmoteEpoch { get; set; }
    public uint LastTargetEntityId { get; set; }
    public bool LastFaceCamera { get; set; }
    public bool GazeEyesWasOn;   // dynamic face control per-slot on-state (for clean release)
    public bool GazeBodyWasOn;
    public bool GazeHeadWasOn;
    public uint LastBodyDrawOffsetEpoch { get; set; }
    public float TargetBodyOffsetX { get; set; }
    public float TargetBodyOffsetY { get; set; }
    public float TargetBodyOffsetZ { get; set; }
    public bool BodyOffsetActive { get; set; }

    // S55/S61: dedicated standup channel + grace period
    public uint LastStandupEpoch { get; set; }
    public bool PoseReconciled { get; set; }   // v0.7.416: inherited-posture reconcile is once per session
    public DateTime StandupGraceUntil { get; set; }

    // S70b: prevents MirrorPoseState double-play after groundsit standup
    public bool SkipNextPoseTimeline { get; set; }

    // S96: weapon-cpose transition tracking for revert/sheathe clearing blends.
    public byte LastWeaponCpose { get; set; }
    public bool LastWeaponDrawn { get; set; }
    // S100: the timeline the hold branch last played (follows sender's intro→loop stream).
    public ushort HeldWeaponTimeline { get; set; }
    // S106: idle standing cpose hold — parallel to the weapon fields, never shared.
    public ushort HeldIdleTimeline { get; set; }

    public byte LastIdleCpose { get; set; }
    // S112: seated/doze cpose hold (chair, groundsit, bed) — own fields, mode untouched.
    public ushort HeldSeatedTimeline { get; set; }
    public byte LastSeatedCpose { get; set; }

    // S148: last mount ID applied to this peer's puppet (0 = unmounted). Tracked so we only
    // call CreateAndSetupMount when it CHANGES, not every frame.
    public ushort LastAppliedMountId { get; set; }
    // S322: last minion ID summoned on this peer's puppet (0 = none). Same change-gating as the mount.
    public ushort LastAppliedMinionId { get; set; }
    public ushort LastAppliedOrnamentId { get; set; } // S322k: ornament currently applied on this puppet
    public string LastAppliedMonikerName { get; set; } = ""; // S328x: nameplate name currently applied on this puppet
    public bool LastAppliedMonikerHideFc { get; set; }       // S328x: hide-FC flag currently applied
    public bool LastAppliedMonikerHideName { get; set; }     // hide-name flag currently applied (Moniker IPC 2.2)
    public uint LastOrnActionEpoch { get; set; }       // S323g: last ornament-action epoch replayed on this puppet
    public uint LastMountActionEpoch { get; set; }     // S323j: last mount-action epoch replayed on this puppet
    public uint LastActionEpoch { get; set; }          // COSM_1_016: last SKILL epoch replayed on this puppet
    public bool ActionEpochSeen { get; set; }          // COSM_1_016: latch — catch up on join, don't replay a pre-join cast
    public bool MountObjectSeen { get; set; }          // S323j: puppet's mount object present (epoch catch-up latch)
    public string LastOrnPeerSig { get; set; } = "";   // S323m: puppet-side ORNPEER trace change-detection signature
    public ushort[] OrnLoco = new ushort[16];           // S323r: latched ornament locomotion clips, indexed [MoveState(1-3)*4 + direction(0-3)] — walk/run/sprint sets differ, so latch per speed from the sender's live tl0
    // S322b: minion-follow state. A companion is a free actor whose AI can't track a warping puppet, so we
    // drive its position each frame; MinionObjectSeen latches once the (async) SetupCompanion spawn lands,
    // and MinionSpawnWaitFrames counts frames spent waiting so we can re-issue the summon a bounded number
    // of times and log definitively if it never spawns.
    public bool MinionObjectSeen { get; set; }
    public int MinionSpawnWaitFrames { get; set; }
    public bool OrnamentObjectSeen { get; set; }      // S322k: ornament spawn-reconcile (SetupOrnament is async too)
    public int OrnamentSpawnWaitFrames { get; set; }
    public ushort LastMinionAnim { get; set; } // S322g: last minion animation timeline replayed on the puppet
    public float LastMinionWorldX { get; set; } // S322h: last applied minion world pos (animate-while-moving gate)
    public float LastMinionWorldZ { get; set; }
    // S194: sticky test-mount override set by /hms mount <id>. Lets the test command exercise the
    // REAL receiver path (ApplyMountState mode-flip + per-frame peerMounted cpose gate + teardown)
    // on a single client, instead of the old raw-primitive probe that bypassed all of it. The
    // per-frame apply uses effectiveMountId = data.MountId != 0 ? data.MountId : TestMountId, so the
    // override flows through the same gated logic as a real wire MountId. Cleared by SanitizePeerStates.
    public ushort TestMountId { get; set; }
    // S113: SEATDIAG change tracking (temporary diagnostic).
    public ushort SeatDiagTl0 { get; set; }
    public byte SeatDiagCpose { get; set; }
    public byte SeatDiagMode { get; set; }
    // S108: hold suspended while a one-shot emote plays out; wire re-stream resumes.
    public bool PoseHoldSuspended { get; set; }
    // S79: prevents follow-up emote from restarting PlayActionTimeline's loop
    public bool CposeInFlight { get; set; }
}

internal class TransformSnapshot
{
    public TransformData Data { get; set; } = new();
    public DateTime ReceiveTime { get; set; }
}

internal class PeerInterpolationState
{
    public TransformSnapshot? Current;
    public TransformSnapshot? Previous;

    // S320e: ring of recent snapshots for render-behind interpolation (see the interpolation block in
    // OnFrameworkUpdate). Written on the relay thread, read on the framework thread — guarded by the lock.
    public readonly List<TransformSnapshot> History = new();
    public readonly object HistoryLock = new();

    public bool ProtocolWarned;   // S322j: warned once about this peer's incompatible wire version
}
