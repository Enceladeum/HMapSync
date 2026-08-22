using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using HMSync.Sync;

namespace HMSync.Services;

/// <summary>
/// Captures the local player's full actor state at 10Hz and sends it as a
/// unified TransformData snapshot. Pulls emote/weapon state from LocalStateDetector
/// so all state travels in one message.
/// </summary>
public unsafe class StateCaptureService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly RelaySyncService relay;
    private readonly LocalStateDetector detector;
    private readonly IPluginLog log;
    private bool lastCapturedFaceCamera;                  // face-cam edge tracking

    // S326: host-set map state stamped onto every outbound snapshot. The plugin's map* command handlers write this
    // (host only); Epoch bumps on any change so receivers apply once per change. Default (Epoch 0) = ignored by peers.
    public struct MapStateSnapshot
    {
        public byte WeatherId;
        public uint WeatherDonor;   // b183: day/night sky-graft donor tt (0 = static weather, no graft)
        public bool WeatherForced;  // NB-44: true = force this weather on peers; false = the zone's native baseline (peers keep native)
        public bool TimeForced;
        public ushort EorzeaHour;
        public byte EorzeaMinute;
        public uint BgmId;
        public bool RemoveNpcs;
        public bool HideQuestSigns;
        public uint[]? HiddenNpcDataIds;   // NB-20: granular per-map hide set (ENpc DataIds); null/empty = none
        public uint Epoch;
    }
    public MapStateSnapshot MapState;

    private bool active;
    private DateTime lastSend = DateTime.MinValue;

    // S331 (release hardening): change-detection is ALWAYS ON - a stationary peer stops re-sending identical state
    // 10x/sec; only changed lanes go out, plus a low-rate keepalive (every KeepaliveSecs) so the epoch/heartbeat logic
    // and any late joiner stay fresh. The old DirtyCheckEnabled toggle (S328ag) was measurement scaffolding for A/B'ing
    // against the always-send baseline; the architecture is validated, so the off-switch is removed - it only ever
    // risked accidentally disabling the 95% idle suppression that makes the whole thing viable.
    private const double KeepaliveSecs = 2.0;   // force a send at least this often even when idle
    private DateTime lastActualSend = DateTime.MinValue;
    private TransformData? lastSentTransform;   // the WHOLE last-sent transform, for a complete render-diff (no field can be omitted)
    // S331 (late-join signal fix): set by the plugin when a peer JOINS → forces one FULL-state re-send (WARM+COLD+HOST)
    // so the newcomer gets everything set-once-static: the peer's Moniker name, held emote/cpose stance, mount, minion,
    // ornament, weapon-drawn, AND (host only) map-state. WARM/COLD are strictly change-gated (no heartbeat, the 161
    // fix), so a value set BEFORE the newcomer joined is invisible to them unless re-offered here. Generalizes the old
    // HOST-only forceHostResend (which only caught weather/time - zone/name/emote/mount were all still missed).
    private volatile bool forceFullResend;
    public void RequestFullResend() => forceFullResend = true;
    private const float PosEps = 0.01f;      // ~1cm - below this, treat position as unchanged
    private const float RotEps = 0.005f;     // radians

    // Visual body-offset tracking (send-on-change via epoch).
    private const float BodyOffsetEpsilon = 0.01f;
    private float lastBodyOffsetX = 0f;
    private float lastBodyOffsetY = 0f;
    private float lastBodyOffsetZ = 0f;
    private uint bodyDrawOffsetEpoch = 0;
    private const double TickInterval = 0.1; // 10Hz

    // Speed thresholds (units/second) for MoveState classification,
    // independent of tick timing.
    private const float WalkSpeed = 0.5f;
    private const float RunSpeed = 3.5f;
    private const float SprintSpeed = 7.0f;

    private Vector3 lastPosition;
    private bool lastPositionSet;

    public StateCaptureService(
        IObjectTable objectTable,
        IFramework framework,
        RelaySyncService relay,
        LocalStateDetector detector,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.relay = relay;
        this.detector = detector;
        this.log = log;
    }

    // S328x: supplies the local player's Moniker nameplate name (name, hideFc); ("",false) if none/absent. Wired by
    // the plugin so the capture service stays decoupled from the Moniker IPC.
    public System.Func<(string name, bool hideFc, bool hideName, bool hideTitle)>? MonikerNameSupplier;

    // COSM_1_016: supplies the last locally-accepted cast (id, type, epoch, target position) for the WARM lane.
    public System.Func<(uint id, byte type, uint epoch, System.Numerics.Vector3 target, ulong targetCid)>? SkillCastSupplier;

    public void Start()
    {
        if (active) return;
        active = true;
        lastPositionSet = false;
        framework.Update += OnFrameworkUpdate;
        log.Information("[HMSync] State capture started");
    }

    public void Stop()
    {
        if (!active) return;
        active = false;
        framework.Update -= OnFrameworkUpdate;
        log.Information("[HMSync] State capture stopped");
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        if (!active || !relay.IsConnected) return;
        // S331 (Stage 4): don't send lane frames until the relay's RoomJoined has arrived (it mints our peer id - spec
        // §10.1). Sending before we have our id would race the relay's identity assignment. Once acknowledged, proceed.
        if (!relay.RoomJoinedAcknowledged) return;

        var now = DateTime.UtcNow;
        if ((now - lastSend).TotalSeconds < TickInterval) return;
        var elapsed = (float)(now - lastSend).TotalSeconds;
        lastSend = now;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        // Pull the local actor's full state for this tick. Explicit call means
        // no dependency on framework-update registration order.
        var detected = detector.Detect();
        if (detected == null) return;
        var state = detected.Value;

        var pos = player.Position;

        // Movement state from position delta, normalized by elapsed time
        // to prevent MoveState flickering from inconsistent tick timing.
        byte moveState = 0;
        if (lastPositionSet)
        {
            var dx = pos.X - lastPosition.X;
            var dz = pos.Z - lastPosition.Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            var speed = elapsed > 0.001f ? dist / elapsed : 0f;

            if (state.IsSprinting && speed > WalkSpeed)
                moveState = 3; // sprinting (detected from timeline)
            else if (speed > SprintSpeed)
                moveState = 3; // sprinting (speed threshold fallback)
            else if (speed > RunSpeed)
                moveState = 2; // running
            else if (speed > WalkSpeed)
                moveState = 1; // walking

            // S197b: clamp mounted sprint→run. A mount's normal pace (~8-9 u/s) clears SprintSpeed and
            // classifies as sprint - but GndSprint has NO directional variant, so mounted strafe-L/R
            // would collapse to a forward animation (the slide). Run (GndRunL/R/F) HAS directional
            // variants. Reverse-mount avoided this because the on-foot peer rode at run speed; a
            // genuinely-mounted self moves at mount speed, so clamp here to keep strafe directional.
            if (moveState == 3 && state.CharMode == (byte)CharacterModes.Mounted)
                moveState = 2;
        }

        lastPosition = pos;
        lastPositionSet = true;

        // Sender-side visual body offset (always-on, 3-axis): measure how far the game
        // draws the local body from its logical position. Nonzero for swim (vertical),
        // chair-sit (vertical + horizontal seat-centring), and any future off-root state;
        // ~0 when standing/walking/bed-doze/ground-sit. Body-accurate (real skeleton,
        // glamour-proof). Epoch bumps only when any axis changes past threshold.
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
            var drawObj = go->DrawObject;
            if (drawObj != null)
            {
                var dp = drawObj->Object.Position;
                var mx = dp.X - pos.X;
                var my = dp.Y - pos.Y;
                var mz = dp.Z - pos.Z;
                if (MathF.Abs(mx - lastBodyOffsetX) > BodyOffsetEpsilon
                    || MathF.Abs(my - lastBodyOffsetY) > BodyOffsetEpsilon
                    || MathF.Abs(mz - lastBodyOffsetZ) > BodyOffsetEpsilon)
                {
                    lastBodyOffsetX = mx;
                    lastBodyOffsetY = my;
                    lastBodyOffsetZ = mz;
                    bodyDrawOffsetEpoch++;
                }
            }
        }

        // S244/S245: cosmetic display toggles - read the local DrawData bools so peers can
        // mirror them (visor flip, headgear-hidden). Weapon hide/show is SENDER-ONLY (not synced).
        bool visorToggled = false, hatHidden = false;
        bool faceCamera = false;   // /facecamera fourth-wall stare - read from LookAt, not broadcast by the game natively
        ulong selfContentId = 0;   // S327: our stable identity, stamped on the snapshot so peers can bind us correctly
        {
            var ch = (Character*)player.Address;
            if (ch != null)
            {
                visorToggled = ch->DrawData.IsVisorToggled;
                hatHidden = ch->DrawData.IsHatHidden;
                selfContentId = ch->ContentId;
                faceCamera = ch->LookAt.IsFacingCamera;
                // UNIFICATION + TOGGLE: /facecamera is a native TOGGLE (each press flips the game's IsFacingCamera).
                // We treat each press as an event: on the rising edge we FLIP OUR OWN state (hmsFaceCamActive) and
                // either write the camera point to all 3 FaceControlState slots (turning ON) or clear them (turning
                // OFF - press again resets the stance, matching the game). Then we IMMEDIATELY SUPPRESS the game's
                // native flag (zero IsFacingCamera @ LookAt+0xBB0) so the game's own face-camera drive doesn't fight
                // our unified FaceControlState path - that fight was why face-control edits were ignored after a
                // /facecamera, and why the puppet jittered on the reset press. Our path is now the sole driver.
                if (faceCamera && !lastCapturedFaceCamera)   // rising edge = a keypress
                {
                    // Derive the toggle from the ACTUAL current gaze state rather than a separate latch (the latch
                    // drifted out of sync when face-control edits or auto-clear changed FaceControlState without it -
                    // that caused the "/facecamera ignored, works on 2nd press" desync). If a gaze is currently active
                    // → this press RESETS (toggle off). If none active → this press SETS all 3 slots to camera.
                    bool gazeActive = FaceControlState.EyesOn || FaceControlState.BodyOn || FaceControlState.HeadOn;
                    if (gazeActive)
                    {
                        FaceControlState.ClearAll();   // reset stance
                    }
                    else
                    {
                        var camMgr = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
                        if (camMgr != null && camMgr->Camera != null)
                        {
                            var m = camMgr->Camera->SceneCamera.ViewMatrix;
                            var eye = new System.Numerics.Vector3(
                                -(m.M11 * m.M41 + m.M12 * m.M42 + m.M13 * m.M43),
                                -(m.M21 * m.M41 + m.M22 * m.M42 + m.M23 * m.M43),
                                -(m.M31 * m.M41 + m.M32 * m.M42 + m.M33 * m.M43));
                            FaceControlState.EyesOn = FaceControlState.BodyOn = FaceControlState.HeadOn = true;
                            FaceControlState.Eyes = FaceControlState.Body = FaceControlState.Head = eye;
                        }
                    }
                }
                // Suppress the game's native face-camera flag every frame while it's set, so it never drives the local
                // look-at itself (our FaceControlState path is the sole driver). Flag @ Character+0x1930.
                if (faceCamera)
                    *((byte*)ch + 0x1930) = 0;
                lastCapturedFaceCamera = faceCamera;
            }
        }

        // Build unified snapshot
        var transform = new TransformData
        {
            SenderContentId = selfContentId,   // S327: stable identity for correct peer→object binding
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Rotation = player.Rotation,
            MountPitch = state.MountPitch,
            Seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Protocol = SyncProtocol.Version,
            MoveState = moveState,
            MoveMode = state.MoveMode,
            JumpPhase = state.JumpPhase,
            IsTurning = state.IsTurning,
            TargetEntityId = (uint)player.TargetObjectId,
            // Old face-camera broadcast retired - /facecamera now writes FaceControlState and rides the gaze path
            // (unified). Kept false/zero on the wire (append-only fields; harmless) so no double-drive on peers.
            FaceCamera = false,
            FaceCamX = 0, FaceCamY = 0, FaceCamZ = 0,

            // Dynamic face control - read the shared UI state, broadcast per-slot so peers drive the gaze.
            GazeEyesOn = FaceControlState.EyesOn, GazeEyesX = FaceControlState.Eyes.X, GazeEyesY = FaceControlState.Eyes.Y, GazeEyesZ = FaceControlState.Eyes.Z,
            GazeBodyOn = FaceControlState.BodyOn, GazeBodyX = FaceControlState.Body.X, GazeBodyY = FaceControlState.Body.Y, GazeBodyZ = FaceControlState.Body.Z,
            GazeHeadOn = FaceControlState.HeadOn, GazeHeadX = FaceControlState.Head.X, GazeHeadY = FaceControlState.Head.Y, GazeHeadZ = FaceControlState.Head.Z,

            // Emote state
            EmoteId = state.EmoteId,
            TimelineId = state.TimelineId,
            EmoteEpoch = state.EmoteEpoch,
            CharMode = state.CharMode,
            CharModeParam = state.CharModeParam,
            PoseType = state.PoseType,
            CPoseState = state.CPoseState,

            // Weapon state
            WeaponDrawn = state.WeaponDrawn,

            // S244/S245: cosmetic display toggles (visor + headgear synced; weapon-hide sender-only).
            VisorToggled = visorToggled,
            HatHidden = hatHidden,

            // S148: mount state - receiver spawns/clears the mount model on the puppet.
            MountId = state.MountId,
            MountAnimTimeline = state.MountAnimTimeline,

            // S322: minion state - receiver summons/dismisses the minion on the puppet.
            MinionId = state.MinionId,
            MinionBehaviour = state.MinionBehaviour,
            MinionAnim = state.MinionAnim,
            MinionOffX = state.MinionOffX,
            MinionOffY = state.MinionOffY,
            MinionOffZ = state.MinionOffZ,
            MinionRot = state.MinionRot,
            OrnamentId = state.OrnamentId,
            OrnamentActionTimeline = state.OrnamentActionTimeline,
            OrnamentActionEpoch = state.OrnamentActionEpoch,
            MountActionTimeline = state.MountActionTimeline,
            MountActionEpoch = state.MountActionEpoch,
            OrnamentTimeline = state.OrnamentTimeline,

            // S326: map-state backbone - the host stamps these on every outbound snapshot so late-joiners and
            // mid-session peers converge. Non-host: MapState is default (epoch 0) and receivers ignore it (only the
            // host's stream carries a live epoch). Set via MapState.* by the plugin's map* command handlers.
            MapWeatherId = MapState.WeatherId,
            MapWeatherDonor = MapState.WeatherDonor,
            MapWeatherForced = MapState.WeatherForced,
            MapTimeForced = MapState.TimeForced,
            MapEorzeaHour = MapState.EorzeaHour,
            MapEorzeaMinute = MapState.EorzeaMinute,
            MapBgmId = MapState.BgmId,
            MapRemoveNpcs = MapState.RemoveNpcs,
            MapHideQuestSigns = MapState.HideQuestSigns,
            MapHiddenNpcDataIds = MapState.HiddenNpcDataIds,
            MapStateEpoch = MapState.Epoch,

            // Visual body offset (swim / chair-sit / future): values ride every
            // transform; epoch gates receiver reaction.
            BodyDrawOffsetX = lastBodyOffsetX,
            BodyDrawOffsetY = lastBodyOffsetY,
            BodyDrawOffsetZ = lastBodyOffsetZ,
            BodyDrawOffsetEpoch = bodyDrawOffsetEpoch,

            // S55: standup channel (early detection from get-up timeline).
            StandupTimelineId = state.StandupTimelineId,
            StandupEpoch = state.StandupEpoch,
        };

        // COSM_1_016: skills - carry the last locally-accepted cast so peers can replay it. Supplier pattern (same as
        // MonikerNameSupplier) so SkillSyncService doesn't need to be a constructor dependency. The epoch is what
        // drives replay; the id/type/target ride along. Held values are harmless - the receiver only fires on change.
        if (SkillCastSupplier != null)
        {
            var (aId, aType, aEpoch, aTgt, aCid) = SkillCastSupplier();
            transform.ActionId = aId;
            transform.ActionType = aType;
            transform.ActionEpoch = aEpoch;
            transform.ActionTgtX = aTgt.X; transform.ActionTgtY = aTgt.Y; transform.ActionTgtZ = aTgt.Z;
            transform.ActionTgtCid = aCid;
        }

        // S328x: Moniker nameplate name (always-present so late joiners get it). Empty if no Moniker / no name set.
        if (MonikerNameSupplier != null)
        {
            var (mkName, mkHideFc, mkHideName, mkHideTitle) = MonikerNameSupplier();
            transform.MonikerName = mkName;
            transform.MonikerHideFc = mkHideFc;
            transform.MonikerHideName = mkHideName;
            transform.MonikerHideTitle = mkHideTitle;
        }

        // S328ah: change-detection gate. Suppress a send if nothing a receiver renders from has changed since the last
        // SENT transform, AND we sent within the keepalive window. CRITICAL LESSON (S328ah regression): this MUST
        // compare the WHOLE transform, not a hand-picked field list. An enumerated list silently omits fields - the
        // first version missed MoveState (→ stopped actors kept walking) and also target/emote-detail/minion/ornament/
        // etc, and every future field would be another latent omission. So we compare against a retained copy of the
        // entire last-sent transform via TransformData.RenderEquals, which covers every render field with epsilon
        // tolerance on the float (position/rotation/offset) fields and exact compare on the rest. Seq/timestamp are
        // excluded (they always change). Add a field to TransformData → it's automatically in the comparison.
        // Stage 2a (S330a): emit via LANES. Two force flags with distinct meaning (S330b fix - flagged by relay thread
        // that COLD/HOST were heartbeating): forceHot sends HOT even if unchanged (the liveness/keepalive heartbeat +
        // version carrier); forceAllLanes sends EVERY lane even if unchanged (first-send-after-join only - a joiner
        // needs the complete picture). A bare keepalive forces HOT ONLY, so COLD/HOST/WARM stay strictly change-gated
        // and don't add redundant idle traffic. The lane sender internally suppresses unchanged lanes.
        bool keepaliveDue = (now - lastActualSend).TotalSeconds >= KeepaliveSecs;
        bool firstSend = lastSentTransform == null;

        // S331 (late-join signal fix): when a peer JOINS, re-send our FULL current state once so the newcomer catches
        // up on everything set-once-static. WARM/COLD are strictly change-gated (no heartbeat - the 161 fix), so a
        // value we set BEFORE they joined (Moniker name, held emote/cpose, mount, minion, ornament, weapon-drawn) is
        // invisible to them unless we re-offer it here. This generalizes the old HOST-only re-send (which only caught
        // weather/time). Consumed one-shot. Existing peers are unaffected: WARM/COLD merge is idempotent (same values),
        // and HOST skips via the epoch gate - so this catches up the newcomer without disrupting anyone already in.
        bool joinResend = forceFullResend;
        forceFullResend = false;

        bool forceAllLanes = firstSend || joinResend;            // complete picture for OUR join OR to catch up a newcomer
        bool forceHot = forceAllLanes || keepaliveDue;            // HOT also heartbeats on the keepalive

        _ = relay.SendTransformAsLanes(transform, relay.IsHost, forceHot, forceAllLanes, /*forceHostOnce:*/ false, PosEps, RotEps);

        // Reset the keepalive clock whenever we forced HOT (the heartbeat), so the ~2s floor is measured from the last
        // HOT emit. (forceAllLanes implies forceHot, so this covers first-send/dirty-off too.)
        if (forceHot) lastActualSend = now;
        lastSentTransform = transform;   // retain for the null-check + keepalive basis
    }

    public void Dispose()
    {
        Stop();
    }
}
