namespace HMSync.Services;

/// <summary>
/// Immutable snapshot of the local player's actor state for one frame.
/// Produced by <see cref="LocalStateDetector"/> and consumed by
/// StateCaptureService when composing the 10Hz TransformData. Replaces the
/// previous pattern of public mutable fields shared between services, which
/// depended on an implicit framework-update ordering contract.
/// </summary>
public readonly struct LocalActorState
{
    // ── Emote ──
    public readonly ushort EmoteId;
    public readonly ushort TimelineId;
    public readonly uint EmoteEpoch;
    public readonly byte CharMode;
    public readonly byte CharModeParam;
    public readonly byte PoseType;
    public readonly byte CPoseState;

    // ── Movement ──
    public readonly byte MoveMode;
    public readonly byte JumpPhase;
    public readonly bool IsSprinting;
    public readonly bool IsTurning;

    // ── Weapon ──
    public readonly bool WeaponDrawn;

    // ── Standup (S55) ──
    public readonly ushort StandupTimelineId;
    public readonly uint StandupEpoch;

    // ── Mount (S148) ──
    public readonly ushort MountId;
    public readonly ushort MountAnimTimeline; // S196b: mount object's slot-0 locomotion timeline
    public readonly float MountPitch; // S206: mount nose pitch (radians) for flying mounts; 0 = level

    // ── Minion (S322) ──
    public readonly ushort MinionId;
    public readonly byte MinionBehaviour; // S322f: CompanionMove (0 None/1 Obedient/2 Independent/3 Stationary)
    public readonly ushort MinionAnim;    // S322g: minion's base animation timeline (replayed on the puppet)
    public readonly float MinionOffX;     // S322h: minion position offset from owner (minionPos − ownerPos)
    public readonly float MinionOffY;
    public readonly float MinionOffZ;
    public readonly float MinionRot;      // S322h: minion facing
    public readonly ushort OrnamentId;    // S322k: fashion accessory (ornament) id, 0 = none
    public readonly ushort OrnamentActionTimeline; // S323g: tl0 of an ornament action/emote one-shot
    public readonly uint OrnamentActionEpoch;      // S323g: bumps per new ornament action
    public readonly ushort MountActionTimeline;    // S323j: tl0 of a mount action one-shot (on the MOUNT OBJECT)
    public readonly uint MountActionEpoch;         // S323j: bumps per new mount action
    public readonly ushort OrnamentTimeline;       // S323n: the sender's LIVE ornament tl0 (idle 7367, parasol-walk 7374, intros, actions) — mirrored on the peer so the accessory renders

    public LocalActorState(
        ushort emoteId,
        ushort timelineId,
        uint emoteEpoch,
        byte charMode,
        byte charModeParam,
        byte poseType,
        byte cPoseState,
        byte moveMode,
        byte jumpPhase,
        bool isSprinting,
        bool isTurning,
        bool weaponDrawn,
        ushort standupTimelineId,
        uint standupEpoch,
        ushort mountId,
        ushort minionId,
        ushort mountAnimTimeline = 0,
        float mountPitch = 0f,
        byte minionBehaviour = 0,
        ushort minionAnim = 0,
        float minionOffX = 0f,
        float minionOffY = 0f,
        float minionOffZ = 0f,
        float minionRot = 0f,
        ushort ornamentId = 0,
        ushort ornamentActionTimeline = 0,
        uint ornamentActionEpoch = 0,
        ushort mountActionTimeline = 0,
        uint mountActionEpoch = 0,
        ushort ornamentTimeline = 0)
    {
        EmoteId = emoteId;
        TimelineId = timelineId;
        EmoteEpoch = emoteEpoch;
        CharMode = charMode;
        CharModeParam = charModeParam;
        PoseType = poseType;
        CPoseState = cPoseState;
        MoveMode = moveMode;
        JumpPhase = jumpPhase;
        IsSprinting = isSprinting;
        IsTurning = isTurning;
        WeaponDrawn = weaponDrawn;
        StandupTimelineId = standupTimelineId;
        StandupEpoch = standupEpoch;
        MountId = mountId;
        MinionId = minionId;
        MinionBehaviour = minionBehaviour;
        MinionAnim = minionAnim;
        MinionOffX = minionOffX;
        MinionOffY = minionOffY;
        MinionOffZ = minionOffZ;
        MinionRot = minionRot;
        OrnamentId = ornamentId;
        OrnamentActionTimeline = ornamentActionTimeline;
        OrnamentActionEpoch = ornamentActionEpoch;
        MountActionTimeline = mountActionTimeline;
        MountActionEpoch = mountActionEpoch;
        OrnamentTimeline = ornamentTimeline;
        MountAnimTimeline = mountAnimTimeline;
        MountPitch = mountPitch;
    }
}
