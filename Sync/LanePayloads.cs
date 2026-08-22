namespace HMSync.Sync;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// LANE PROJECTION - binary lanes only (S331 Stage 4; JSON payloads removed v0.7.364)
//
// ⚠ LANE PAYLOADS ARE BINARY-ONLY. Do NOT reintroduce a parallel JSON payload mirror.
//
// This file once carried a second, JSON copy of every lane payload (HotData/WarmData/ColdData/HostData plus their
// ToX/MergeX mappers) alongside the binary path. Nothing called them after the Stage-4 binary cutover, and the
// duplication silently rotted: the 12 face/gaze fields were added to the binary WarmPayload but never to the JSON
// WarmData, so the two copies disagreed. They were deleted rather than repaired - a parallel encoder that nothing
// sends is pure maintenance debt and a standing trap for the next person adding a field.
//
// The live wire format is MessagePack over HMSync.Wire.*Payload (keys via [Key(n)] in Wire/HMSyncWireTypes.cs).
// Adding a synced field now means touching exactly TWO places: the Wire POCO + its ToXWire/MergeXWire mapper here,
// and the lane's *Equals below.
//
// ENTITY-ADDRESSING (architecture doc §10, + relay-thread constraint): every lane payload carries a SUBJECT entity
// id (`sid`). This is the entity the update is ABOUT. Today sid == the sending peer's id (a player puppet reporting
// itself), so it's set from the local peer id on send. TOMORROW an NPC has sid = <npc id> authored by a director,
// on the SAME lanes. CRITICAL: the subject id lives in the PAYLOAD, never in the envelope `SenderId` (`s`). The relay
// uses envelope `s` for fan-out sender-exclusion AND its spoof-guard (drops any non-Join message where s ≠ the
// connection's bound peer id). If the subject went in `s`, an NPC (subject ≠ sender) would fail exclusion and trip
// the guard. So: envelope `s` = sending connection id ALWAYS; `sid` (payload) = subject entity id.
//
// Seq rides HOT for ordering/dedup (the high-rate lane).
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

// ── Projection helpers: build a wire payload from a full TransformData (sender side), and merge one back into a
// per-subject composite TransformData (receiver side). These are the ONLY bridge between the lanes and the
// TransformData shape the apply path still consumes.
public static class LaneProjection
{
    // ── S331 (Stage 4): wire-payload projections - TransformData → HMSync.Wire.* (the msgpack POCOs). These replace
    // the JSON path's ToHot/ToWarm/etc for the binary sender. Field mapping is identical; only the target type differs.
    public static HMSync.Wire.HotPayload ToHotWire(TransformData t, string subjectId, uint seq) => new()
    {
        SubjectId = subjectId, Seq = seq, Protocol = t.Protocol, SenderContentId = t.SenderContentId,
        X = t.X, Y = t.Y, Z = t.Z, Rotation = t.Rotation, MountPitch = t.MountPitch,
        MoveState = t.MoveState, MoveMode = t.MoveMode, JumpPhase = t.JumpPhase, IsTurning = t.IsTurning,
        BodyDrawOffsetX = t.BodyDrawOffsetX, BodyDrawOffsetY = t.BodyDrawOffsetY, BodyDrawOffsetZ = t.BodyDrawOffsetZ,
        BodyDrawOffsetEpoch = t.BodyDrawOffsetEpoch,
    };

    public static HMSync.Wire.WarmPayload ToWarmWire(TransformData t, string subjectId) => new()
    {
        SubjectId = subjectId,
        TargetEntityId = t.TargetEntityId, EmoteId = t.EmoteId, TimelineId = t.TimelineId, EmoteEpoch = t.EmoteEpoch,
        PoseType = t.PoseType, CPoseState = t.CPoseState, CharMode = t.CharMode, CharModeParam = t.CharModeParam,
        MountId = t.MountId, MountAnimTimeline = t.MountAnimTimeline, MountActionTimeline = t.MountActionTimeline,
        MountActionEpoch = t.MountActionEpoch, MinionId = t.MinionId, MinionBehaviour = t.MinionBehaviour,
        MinionAnim = t.MinionAnim, MinionOffX = t.MinionOffX, MinionOffY = t.MinionOffY, MinionOffZ = t.MinionOffZ,
        MinionRot = t.MinionRot, OrnamentId = t.OrnamentId, OrnamentTimeline = t.OrnamentTimeline,
        OrnamentActionTimeline = t.OrnamentActionTimeline, OrnamentActionEpoch = t.OrnamentActionEpoch,
        WeaponDrawn = t.WeaponDrawn, StandupTimelineId = t.StandupTimelineId, StandupEpoch = t.StandupEpoch,
        FaceCamera = t.FaceCamera, FaceCamX = t.FaceCamX, FaceCamY = t.FaceCamY, FaceCamZ = t.FaceCamZ,
        GazeEyesOn = t.GazeEyesOn, GazeEyesX = t.GazeEyesX, GazeEyesY = t.GazeEyesY, GazeEyesZ = t.GazeEyesZ,
        GazeBodyOn = t.GazeBodyOn, GazeBodyX = t.GazeBodyX, GazeBodyY = t.GazeBodyY, GazeBodyZ = t.GazeBodyZ,
        GazeHeadOn = t.GazeHeadOn, GazeHeadX = t.GazeHeadX, GazeHeadY = t.GazeHeadY, GazeHeadZ = t.GazeHeadZ,
        ActionId = t.ActionId, ActionType = t.ActionType, ActionEpoch = t.ActionEpoch,
        ActionTgtX = t.ActionTgtX, ActionTgtY = t.ActionTgtY, ActionTgtZ = t.ActionTgtZ, ActionTgtCid = t.ActionTgtCid,
    };

    public static HMSync.Wire.ColdPayload ToColdWire(TransformData t, string subjectId) => new()
    {
        SubjectId = subjectId,
        MonikerName = t.MonikerName, MonikerHideFc = t.MonikerHideFc, MonikerHideName = t.MonikerHideName, MonikerHideTitle = t.MonikerHideTitle, VisorToggled = t.VisorToggled, HatHidden = t.HatHidden,
    };

    public static HMSync.Wire.HostPayload ToHostWire(TransformData t, string subjectId) => new()
    {
        SubjectId = subjectId,
        MapWeatherId = t.MapWeatherId, MapWeatherDonor = t.MapWeatherDonor, MapWeatherForced = t.MapWeatherForced, MapTimeForced = t.MapTimeForced, MapEorzeaHour = t.MapEorzeaHour,
        MapEorzeaMinute = t.MapEorzeaMinute, MapBgmId = t.MapBgmId, MapRemoveNpcs = t.MapRemoveNpcs,
        MapHideQuestSigns = t.MapHideQuestSigns, MapStateEpoch = t.MapStateEpoch,
        HiddenNpcDataIds = t.MapHiddenNpcDataIds,
    };

    // ── Merge a wire payload into a composite (receiver side). Same as the JSON Merge* but from HMSync.Wire types. ──
    public static void MergeHotWire(TransformData c, HMSync.Wire.HotPayload h)
    {
        c.Protocol = h.Protocol; c.SenderContentId = h.SenderContentId;
        c.X = h.X; c.Y = h.Y; c.Z = h.Z; c.Rotation = h.Rotation; c.MountPitch = h.MountPitch;
        c.MoveState = h.MoveState; c.MoveMode = h.MoveMode; c.JumpPhase = h.JumpPhase; c.IsTurning = h.IsTurning;
        c.BodyDrawOffsetX = h.BodyDrawOffsetX; c.BodyDrawOffsetY = h.BodyDrawOffsetY; c.BodyDrawOffsetZ = h.BodyDrawOffsetZ;
        c.BodyDrawOffsetEpoch = h.BodyDrawOffsetEpoch;
        c.Seq = h.Seq;
    }

    public static void MergeWarmWire(TransformData c, HMSync.Wire.WarmPayload w)
    {
        c.TargetEntityId = w.TargetEntityId; c.EmoteId = w.EmoteId; c.TimelineId = w.TimelineId; c.EmoteEpoch = w.EmoteEpoch;
        c.PoseType = w.PoseType; c.CPoseState = w.CPoseState; c.CharMode = w.CharMode; c.CharModeParam = w.CharModeParam;
        c.MountId = w.MountId; c.MountAnimTimeline = w.MountAnimTimeline; c.MountActionTimeline = w.MountActionTimeline;
        c.MountActionEpoch = w.MountActionEpoch; c.MinionId = w.MinionId; c.MinionBehaviour = w.MinionBehaviour;
        c.MinionAnim = w.MinionAnim; c.MinionOffX = w.MinionOffX; c.MinionOffY = w.MinionOffY; c.MinionOffZ = w.MinionOffZ;
        c.MinionRot = w.MinionRot; c.OrnamentId = w.OrnamentId; c.OrnamentTimeline = w.OrnamentTimeline;
        c.OrnamentActionTimeline = w.OrnamentActionTimeline; c.OrnamentActionEpoch = w.OrnamentActionEpoch;
        c.WeaponDrawn = w.WeaponDrawn; c.StandupTimelineId = w.StandupTimelineId; c.StandupEpoch = w.StandupEpoch;
        c.FaceCamera = w.FaceCamera; c.FaceCamX = w.FaceCamX; c.FaceCamY = w.FaceCamY; c.FaceCamZ = w.FaceCamZ;
        c.GazeEyesOn = w.GazeEyesOn; c.GazeEyesX = w.GazeEyesX; c.GazeEyesY = w.GazeEyesY; c.GazeEyesZ = w.GazeEyesZ;
        c.GazeBodyOn = w.GazeBodyOn; c.GazeBodyX = w.GazeBodyX; c.GazeBodyY = w.GazeBodyY; c.GazeBodyZ = w.GazeBodyZ;
        c.GazeHeadOn = w.GazeHeadOn; c.GazeHeadX = w.GazeHeadX; c.GazeHeadY = w.GazeHeadY; c.GazeHeadZ = w.GazeHeadZ;
        c.ActionId = w.ActionId; c.ActionType = w.ActionType; c.ActionEpoch = w.ActionEpoch;
        c.ActionTgtX = w.ActionTgtX; c.ActionTgtY = w.ActionTgtY; c.ActionTgtZ = w.ActionTgtZ; c.ActionTgtCid = w.ActionTgtCid;
    }

    public static void MergeColdWire(TransformData c, HMSync.Wire.ColdPayload d)
    {
        c.MonikerName = d.MonikerName; c.MonikerHideFc = d.MonikerHideFc; c.MonikerHideName = d.MonikerHideName; c.MonikerHideTitle = d.MonikerHideTitle; c.VisorToggled = d.VisorToggled; c.HatHidden = d.HatHidden;
    }

    public static void MergeHostWire(TransformData c, HMSync.Wire.HostPayload d)
    {
        c.MapWeatherId = d.MapWeatherId; c.MapWeatherDonor = d.MapWeatherDonor; c.MapWeatherForced = d.MapWeatherForced; c.MapTimeForced = d.MapTimeForced; c.MapEorzeaHour = d.MapEorzeaHour;
        c.MapEorzeaMinute = d.MapEorzeaMinute; c.MapBgmId = d.MapBgmId; c.MapRemoveNpcs = d.MapRemoveNpcs;
        c.MapHideQuestSigns = d.MapHideQuestSigns; c.MapStateEpoch = d.MapStateEpoch;
        c.MapHiddenNpcDataIds = d.HiddenNpcDataIds;
    }

    // ── Per-lane change detection (sender side) ── each returns true if THAT lane's fields differ between two
    // transforms. The sender emits only the lanes that changed, so a walking-but-not-emoting peer sends HOT only.
    // Float fields use epsilon (position/rotation jitter); the rest exact. Mirrors RenderEquals but lane-scoped.
    private static bool FEq(float a, float b, float e) => System.Math.Abs(a - b) <= e;

    public static bool HotEquals(TransformData a, TransformData b, float posEps, float rotEps) =>
        FEq(a.X, b.X, posEps) && FEq(a.Y, b.Y, posEps) && FEq(a.Z, b.Z, posEps) &&
        FEq(a.Rotation, b.Rotation, rotEps) && FEq(a.MountPitch, b.MountPitch, rotEps) &&
        a.MoveState == b.MoveState && a.MoveMode == b.MoveMode && a.JumpPhase == b.JumpPhase && a.IsTurning == b.IsTurning &&
        FEq(a.BodyDrawOffsetX, b.BodyDrawOffsetX, posEps) && FEq(a.BodyDrawOffsetY, b.BodyDrawOffsetY, posEps) &&
        FEq(a.BodyDrawOffsetZ, b.BodyDrawOffsetZ, posEps) && a.BodyDrawOffsetEpoch == b.BodyDrawOffsetEpoch;

    public static bool WarmEquals(TransformData a, TransformData b) =>
        a.TargetEntityId == b.TargetEntityId && a.EmoteId == b.EmoteId && a.TimelineId == b.TimelineId &&
        a.EmoteEpoch == b.EmoteEpoch && a.PoseType == b.PoseType && a.CPoseState == b.CPoseState &&
        a.CharMode == b.CharMode && a.CharModeParam == b.CharModeParam && a.MountId == b.MountId &&
        a.MountAnimTimeline == b.MountAnimTimeline && a.MountActionTimeline == b.MountActionTimeline &&
        a.MountActionEpoch == b.MountActionEpoch && a.MinionId == b.MinionId && a.MinionBehaviour == b.MinionBehaviour &&
        a.MinionAnim == b.MinionAnim && FEq(a.MinionOffX, b.MinionOffX, 0.001f) && FEq(a.MinionOffY, b.MinionOffY, 0.001f) &&
        FEq(a.MinionOffZ, b.MinionOffZ, 0.001f) && FEq(a.MinionRot, b.MinionRot, 0.001f) && a.OrnamentId == b.OrnamentId &&
        a.OrnamentTimeline == b.OrnamentTimeline && a.OrnamentActionTimeline == b.OrnamentActionTimeline &&
        a.OrnamentActionEpoch == b.OrnamentActionEpoch && a.WeaponDrawn == b.WeaponDrawn &&
        a.StandupTimelineId == b.StandupTimelineId && a.StandupEpoch == b.StandupEpoch &&
        a.FaceCamera == b.FaceCamera && FEq(a.FaceCamX, b.FaceCamX, 0.001f) && FEq(a.FaceCamY, b.FaceCamY, 0.001f) && FEq(a.FaceCamZ, b.FaceCamZ, 0.001f) &&
        a.GazeEyesOn == b.GazeEyesOn && FEq(a.GazeEyesX, b.GazeEyesX, 0.001f) && FEq(a.GazeEyesY, b.GazeEyesY, 0.001f) && FEq(a.GazeEyesZ, b.GazeEyesZ, 0.001f) &&
        a.GazeBodyOn == b.GazeBodyOn && FEq(a.GazeBodyX, b.GazeBodyX, 0.001f) && FEq(a.GazeBodyY, b.GazeBodyY, 0.001f) && FEq(a.GazeBodyZ, b.GazeBodyZ, 0.001f) &&
        a.GazeHeadOn == b.GazeHeadOn && FEq(a.GazeHeadX, b.GazeHeadX, 0.001f) && FEq(a.GazeHeadY, b.GazeHeadY, 0.001f) && FEq(a.GazeHeadZ, b.GazeHeadZ, 0.001f) &&
        a.ActionId == b.ActionId && a.ActionType == b.ActionType && a.ActionEpoch == b.ActionEpoch &&
        FEq(a.ActionTgtX, b.ActionTgtX, 0.001f) && FEq(a.ActionTgtY, b.ActionTgtY, 0.001f) && FEq(a.ActionTgtZ, b.ActionTgtZ, 0.001f) &&
        a.ActionTgtCid == b.ActionTgtCid;

    public static bool ColdEquals(TransformData a, TransformData b) =>
        a.MonikerName == b.MonikerName && a.MonikerHideFc == b.MonikerHideFc && a.MonikerHideName == b.MonikerHideName && a.MonikerHideTitle == b.MonikerHideTitle &&
        a.VisorToggled == b.VisorToggled && a.HatHidden == b.HatHidden;

    public static bool HostEquals(TransformData a, TransformData b) =>
        a.MapWeatherId == b.MapWeatherId && a.MapWeatherDonor == b.MapWeatherDonor && a.MapWeatherForced == b.MapWeatherForced && a.MapTimeForced == b.MapTimeForced && a.MapEorzeaHour == b.MapEorzeaHour &&
        a.MapEorzeaMinute == b.MapEorzeaMinute && a.MapBgmId == b.MapBgmId && a.MapRemoveNpcs == b.MapRemoveNpcs &&
        a.MapHideQuestSigns == b.MapHideQuestSigns && a.MapStateEpoch == b.MapStateEpoch &&
        UintSetEqual(a.MapHiddenNpcDataIds, b.MapHiddenNpcDataIds);

    // Order-insensitive equality for the granular hidden-DataId set (null == empty). Sets are small (a handful of ids),
    // so an O(n²) contains-scan is fine and avoids allocating a HashSet on the hot change-detection path for the common
    // both-empty case.
    private static bool UintSetEqual(uint[]? a, uint[]? b)
    {
        int na = a?.Length ?? 0, nb = b?.Length ?? 0;
        if (na != nb) return false;
        if (na == 0) return true;
        foreach (var x in a!)
        {
            bool found = false;
            foreach (var y in b!) if (x == y) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }
}
