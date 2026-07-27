using System.Text.Json.Serialization;

namespace HMSync.Sync;

public static class SyncProtocol
{
    // Wire-format version. Bump ONLY on an incompatible change to the sync payloads (a new REQUIRED field, a
    // changed field meaning, a reorder). Peers compare this on every TransformUpdate and refuse a mismatch with
    // a message rather than half-applying it and desyncing/crashing. This is NOT the plugin version (that bumps
    // every build); it changes only when old and new clients genuinely can't interoperate.
    public const int Version = 4;   // S331: Stage 4 — binary frame (MessagePack lanes). Retires the JSON monolith.
                                     // (MinionBehaviour->mnb etc). Renamed keys are a wire-incompatible change: a v2
                                     // peer sends the long key, a v3 peer reads the short key → the field silently
                                     // drops. Bump so mismatched clients refuse rather than half-apply.
                                     // (Prior: v2 S327 SenderContentId identity binding; v1 positional/name binding.)
}

public enum SyncMessageType : byte
{
    // Session
    JoinRoom        = 0x01,
    LeaveRoom       = 0x02,
    RoomJoined      = 0x03,
    PeerJoined      = 0x04,
    PeerLeft        = 0x05,
    HostTransfer    = 0x06,

    // State (10Hz) — single authoritative actor snapshot
    TransformUpdate = 0x10,

    // ── SYNC LANES (S329a, Stage 1: DEFINED, NOT YET EMITTED) ──────────────────────────────────────────────
    // The typed-lane replacement for the monolithic TransformUpdate. Stage 2 begins emitting these; Stage 3 retires
    // TransformUpdate. Until then these values are reserved and unused — the wire still carries 0x10. See SyncLanes.cs
    // for the field→lane census map that defines what each lane carries.
    HotUpdate       = 0x11,   // pos/rot/movement/mount-pitch/body-offset — up to 10Hz while moving
    WarmUpdate      = 0x12,   // emote/pose/mount/minion/ornament/target/weapon/standup — on change
    ColdUpdate      = 0x13,   // Moniker + cosmetic toggles — session-start + on change
    HostUpdate      = 0x14,   // map-state block — host only, on change
    EventPulse      = 0x15,   // fire-and-forget one-shots (future: CosmicClaw VFX, HDM fires) — reserved

    // Zone control (host only)
    ZoneLoadExecute = 0x30,

    // Session control
    SessionEnd      = 0x40,

    // Control
    Ping            = 0xF0,
    Pong            = 0xF1,
    Error           = 0xFF,
}

public class SyncMessage
{
    [JsonPropertyName("t")]
    public SyncMessageType Type { get; set; }

    [JsonPropertyName("s")]
    public string SenderId { get; set; } = "";

    [JsonPropertyName("r")]
    public string RoomId { get; set; } = "";

    [JsonPropertyName("ts")]
    public long Timestamp { get; set; }

    [JsonPropertyName("p")]
    public string? Payload { get; set; }
}

/// <summary>
/// Unified actor state snapshot sent at 10Hz.
/// Contains position, movement, emote state, weapon state, and gaze target.
/// The receiver applies all fields from a single message — no race conditions
/// between separate emote and transform channels.
/// </summary>
public class TransformData
{
    // S327: the sender's STABLE identity (Character.ContentId). Rides every transform so every client learns each
    // peer's ContentId directly from their packets — no relay change, self-healing (arrives continuously, works for
    // late joiners + reconnects). This is what binds peerId → the right local object (by matching ContentId), instead
    // of the old positional/name guess. 0 until the sender's own ContentId is readable (in-world).
    [JsonPropertyName("cid")]
    public ulong SenderContentId { get; set; }

    // ── Position / rotation ──
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    [JsonPropertyName("r")]
    public float Rotation { get; set; }

    /// <summary>S206: mount nose pitch (radians) for flying mounts, from the mount DrawObject
    /// quaternion via asin(2*(w*x-y*z)). ~0 level, negative climbing, positive diving. 0 when not
    /// mounted/flying. Applied to the peer's mount DrawObject on top of inherited yaw so a flying
    /// mount tilts its nose on ascend/dive instead of staying perfectly level.</summary>
    [JsonPropertyName("mp")]
    public float MountPitch { get; set; }

    [JsonPropertyName("sq")]
    public long Seq { get; set; }

    /// <summary>S322j: sender's wire-format version (SyncProtocol.Version). A receiver refuses a mismatch
    /// (warns once, doesn't apply) so a format skew can't desync/crash. Absent on pre-S322w clients ⇒ 0 ⇒ refused.</summary>
    [JsonPropertyName("pv")]
    public int Protocol { get; set; }

    // ── Movement ──
    /// <summary>0=idle, 1=walking, 2=running, 3=sprinting</summary>
    [JsonPropertyName("ms")]
    public byte MoveState { get; set; }

    /// <summary>0=ground, 1=swim_surface, 2=swim_under, 3=fly_mount, 4=ground_mount, 5=swim_mount</summary>
    [JsonPropertyName("mm")]
    public byte MoveMode { get; set; }

    /// <summary>0=none, 1=jump_start, 2=falling, 3=landing</summary>
    [JsonPropertyName("jp")]
    public byte JumpPhase { get; set; }

    /// <summary>True when sender is doing a keyboard turn (A+LMB stepping), not mouse pivot.</summary>
    [JsonPropertyName("tr")]
    public bool IsTurning { get; set; }

    // ── Gaze ──
    /// <summary>Entity ID of sender's current target. 0 = no target.</summary>
    [JsonPropertyName("tid")]
    public uint TargetEntityId { get; set; }

    /// <summary>/facecamera fourth-wall stare active. Not broadcast by the game natively — HMS replicates it.</summary>
    [JsonPropertyName("fc")]
    public bool FaceCamera { get; set; }

    /// <summary>Camera eye world point snapshotted at face-camera activation (frozen — held until reset).</summary>
    [JsonPropertyName("fcx")] public float FaceCamX { get; set; }
    [JsonPropertyName("fcy")] public float FaceCamY { get; set; }
    [JsonPropertyName("fcz")] public float FaceCamZ { get; set; }

    /// <summary>Dynamic face control (Brio-style): per-slot eyes/body/head look-at target. Broadcast per-actor.</summary>
    [JsonPropertyName("geo")] public bool GazeEyesOn { get; set; }
    [JsonPropertyName("gex")] public float GazeEyesX { get; set; }
    [JsonPropertyName("gey")] public float GazeEyesY { get; set; }
    [JsonPropertyName("gez")] public float GazeEyesZ { get; set; }
    [JsonPropertyName("gbo")] public bool GazeBodyOn { get; set; }
    [JsonPropertyName("gbx")] public float GazeBodyX { get; set; }
    [JsonPropertyName("gby")] public float GazeBodyY { get; set; }
    [JsonPropertyName("gbz")] public float GazeBodyZ { get; set; }
    [JsonPropertyName("gho")] public bool GazeHeadOn { get; set; }
    [JsonPropertyName("ghx")] public float GazeHeadX { get; set; }
    [JsonPropertyName("ghy")] public float GazeHeadY { get; set; }
    [JsonPropertyName("ghz")] public float GazeHeadZ { get; set; }

    // ── Skills (COSM_1_016) ── cosmetic action replay. The local caster already sees their own cast natively (the
    // firewall only stops the server hearing it), so these exist to let PEERS see it. Fire-and-forget on the epoch,
    // same idiom as MountAction/Standup. Replayed via ActionEffectHandler.Receive with zero targets.
    [JsonPropertyName("aid")] public uint ActionId { get; set; }
    [JsonPropertyName("aty")] public byte ActionType { get; set; }
    [JsonPropertyName("aep")] public uint ActionEpoch { get; set; }
    [JsonPropertyName("atx")] public float ActionTgtX { get; set; }
    [JsonPropertyName("aty2")] public float ActionTgtY { get; set; }
    [JsonPropertyName("atz")] public float ActionTgtZ { get; set; }
    [JsonPropertyName("atc")] public ulong ActionTgtCid { get; set; }

    // ── Emote state ──
    /// <summary>
    /// Emote row ID from the Emote excel sheet.
    /// > 0: persistent or one-shot emote active.
    /// 0: no emote (normal state).
    /// </summary>
    [JsonPropertyName("eid")]
    public ushort EmoteId { get; set; }

    /// <summary>
    /// Raw timeline ID for cpose variants.
    /// Only meaningful when EmoteId > 0 and the timeline differs from the
    /// emote's default loop timeline.
    /// </summary>
    [JsonPropertyName("tl")]
    public ushort TimelineId { get; set; }

    /// <summary>
    /// Emote epoch — increments each time emote state changes on the sender.
    /// Used to distinguish stale frames from current state. A transform frame
    /// with a lower epoch than the peer's current epoch cannot cancel an emote.
    /// </summary>
    [JsonPropertyName("ee")]
    public uint EmoteEpoch { get; set; }

    /// <summary>
    /// EmoteController.CurrentPoseType (EmoteController+0x20): which pose family
    /// (Idle=0, WeaponDrawn=1, Sit=2, GroundSit=3, Doze=4, Umbrella=5, Accessory=6).
    /// </summary>
    [JsonPropertyName("pt")]
    public byte PoseType { get; set; }

    /// <summary>
    /// EmoteController.CPoseState (EmoteController+0x21): the pose index within the
    /// family that /cpose cycles. This is the authoritative pose state the game reads
    /// to render the seated/standing pose — replicated directly rather than via timeline.
    /// </summary>
    [JsonPropertyName("cp")]
    public byte CPoseState { get; set; }

    /// <summary>Sender's CharacterModes value.</summary>
    [JsonPropertyName("cm")]
    public byte CharMode { get; set; }

    /// <summary>Sender's ModeParam value.</summary>
    [JsonPropertyName("cmp")]
    public byte CharModeParam { get; set; }

    /// <summary>
    /// S148: Mount sheet row ID the sender is riding. >0: receiver spawns this mount on the
    /// puppet via CreateAndSetupMount. 0: not mounted → receiver dismounts. Captured regardless
    /// of how the sender mounted (game menu or mod UI — dual-track).
    /// </summary>
    [JsonPropertyName("mnt")]
    public ushort MountId { get; set; }

    /// <summary>
    /// S322: Companion (minion) sheet row ID the sender has summoned. >0: receiver summons it on the
    /// puppet via CompanionData.SetupCompanion. 0: none → receiver dismisses. Captured regardless of how
    /// the minion was summoned (game UI or mod UI). Direct mirror of MountId.
    /// </summary>
    [JsonPropertyName("min")]
    public ushort MinionId { get; set; }
    [JsonPropertyName("mnb")] public byte MinionBehaviour { get; set; } // S322f: CompanionMove from the sender (3 = Stationary)
    [JsonPropertyName("mna")] public ushort MinionAnim { get; set; }    // S322g: sender's minion base timeline, replayed on the puppet
    [JsonPropertyName("mox")] public float MinionOffX { get; set; }      // S322h: minion offset from owner — receiver places puppetPos+offset
    [JsonPropertyName("moy")] public float MinionOffY { get; set; }
    [JsonPropertyName("moz")] public float MinionOffZ { get; set; }
    [JsonPropertyName("mnr")] public float MinionRot { get; set; }       // S322h: minion facing
    [JsonPropertyName("orn")] public ushort OrnamentId { get; set; }     // S322k: fashion accessory (ornament) id, 0 = none
    [JsonPropertyName("oat")] public ushort OrnamentActionTimeline { get; set; } // S323g: the tl0 of an ornament emote/action one-shot
    [JsonPropertyName("oae")] public uint OrnamentActionEpoch { get; set; }      // S323g: bumps per new ornament action (one-shot replay gate)
    [JsonPropertyName("mta")] public ushort MountActionTimeline { get; set; }    // S323j: the tl0 of a mount action one-shot (on the mount object)
    [JsonPropertyName("mae")] public uint MountActionEpoch { get; set; }         // S323j: bumps per new mount action (one-shot replay gate)
    [JsonPropertyName("ort")] public ushort OrnamentTimeline { get; set; }       // S323n: the sender's LIVE ornament tl0 — the peer mirrors it so the accessory stays in its own animation (idle/walk/hold) and renders

    // S328x — Moniker nameplate integration. The sender's chosen nameplate name (from the Moniker plugin), carried
    // always-present so late joiners get it too. Empty = no Moniker name set (peers show the real name). MonikerHideFc
    // mirrors Moniker's "hide FC tag" flag. HMS applies these to the peer's puppet via Moniker's SetCharacterName IPC.
    [JsonPropertyName("mkn")] public string MonikerName { get; set; } = "";
    [JsonPropertyName("mkf")] public bool MonikerHideFc { get; set; }
    [JsonPropertyName("mkh")] public bool MonikerHideName { get; set; }

    // ── S326: map-state backbone (host-authoritative environment; broadcast + replayed to peers) ──
    [JsonPropertyName("msw")] public byte MapWeatherId { get; set; }     // forced weather (0 = default/atmospheric, valid)
    [JsonPropertyName("mst")] public bool MapTimeForced { get; set; }    // is the host holding Eorzea time?
    [JsonPropertyName("msh")] public ushort MapEorzeaHour { get; set; }  // 0..23 forced hour
    [JsonPropertyName("msm")] public byte MapEorzeaMinute { get; set; }  // 0..59 forced minute
    [JsonPropertyName("msb")] public uint MapBgmId { get; set; }         // forced BGM (0 = none)
    [JsonPropertyName("msn")] public bool MapRemoveNpcs { get; set; }    // NPC-removal flag (despawn all event NPCs)
    [JsonPropertyName("msq")] public bool MapHideQuestSigns { get; set; } // hide over-head quest markers only (keep NPCs)
    [JsonPropertyName("mse")] public uint MapStateEpoch { get; set; }    // bumps on any host map-state change (apply gate)

    /// <summary>
    /// S196b: the MOUNT OBJECT's slot-0 ActionTimeline. The mount is a separate Character* whose
    /// own timeline runs the standard Gnd* locomotion (idle 3, turn 7/8, run 22/23/24, sprint 30,
    /// jump 31/32/33) while the rider sits in a seated pose (166/167) on top. ALL mounted movement
    /// animation — forward/back/strafe/jump/turn-in-place — lives here, not on the rider. The
    /// receiver writes this to its puppet's MountObject slot 0 so the mount animates correctly.
    /// 0 when unmounted or mount object absent. (Discovered via [MNTALL]: rider slots only ever held
    /// 166/167 + overlays while the mount object's slot 0 carried 31/32/33 on jump, 23/24 on A/D.)
    /// </summary>
    [JsonPropertyName("mat")]
    public ushort MountAnimTimeline { get; set; }

    /// <summary>
    /// Sender-measured visual body offset: the sender's drawn body position minus its
    /// logical position (DrawObject.Position − GameObject.Position), per axis. Nonzero
    /// whenever the game renders the body at a different place than the actor root —
    /// swimming (vertical), chair-sit (vertical AND horizontal: the seat-snap slides the
    /// body to centre it), and potentially future cases (flight hover, mounts, poses).
    /// Measured against the sender's real skeleton, so correct regardless of race/glamour.
    /// World-space; transfers directly because rotation is synced. The receiver replicates
    /// it as a DrawOffset on the peer. All ~0 when the body sits at its root.
    /// </summary>
    [JsonPropertyName("bdx")]
    public float BodyDrawOffsetX { get; set; }

    [JsonPropertyName("bdo")]
    public float BodyDrawOffsetY { get; set; }

    [JsonPropertyName("bdz")]
    public float BodyDrawOffsetZ { get; set; }

    /// <summary>
    /// Increments whenever any body-offset axis changes past a small threshold. The
    /// receiver reacts (updates target + active flag) only on epoch change, then
    /// maintains the offset every frame while active — so the values ride in every
    /// transform (robust for late joiners / reconnects / packet loss) but only trigger
    /// work when they actually change.
    /// </summary>
    [JsonPropertyName("bde")]
    public uint BodyDrawOffsetEpoch { get; set; }

    // ── Standup channel (S55) ──
    // Separate from the emote channel. The sender sets these when the game's
    // get-up timeline appears on ActionTimeline[0] while still in a seated mode
    // (InPositionLoop / EmoteLoop) — i.e. at the START of the standup, not the
    // end. The receiver fires the animation immediately, ~0.5-0.7s earlier than
    // the emote-epoch-based detection. The emote channel cannot carry standups
    // (EndEmotes share EmoteMode with StartEmotes → oscillation, S51).
    // Values ride every transform (robust for late join / reconnect); epoch
    // gates receiver reaction.

    /// <summary>
    /// ActionTimeline row of the game's native get-up animation (e.g. 644 for
    /// chair, 655 for groundsit). 0 = no standup in progress.
    /// </summary>
    [JsonPropertyName("sut")]
    public ushort StandupTimelineId { get; set; }

    /// <summary>
    /// Increments each time a standup is initiated on the sender. Receiver
    /// reacts on epoch change, not absolute value.
    /// </summary>
    [JsonPropertyName("sue")]
    public uint StandupEpoch { get; set; }

    // ── Weapon state ──
    [JsonPropertyName("wd")]
    public bool WeaponDrawn { get; set; }

    // ── Cosmetic display toggles (S244/S245) — synced so peers see the sender's choices. ──
    // Visor flipped up/down (helmets with a visor action).
    [JsonPropertyName("vis")]
    public bool VisorToggled { get; set; }
    // Headgear hidden (the "/displayhead" state).
    [JsonPropertyName("hth")]
    public bool HatHidden { get; set; }
    // NOTE: weapon hide/show ("/displayarms") is intentionally SENDER-ONLY — not synced.

    /// <summary>
    /// S328ah: does this transform render IDENTICALLY to <paramref name="o"/>? Used by the sender's dirty-check to
    /// suppress a resend when nothing a peer would SEE has changed. Compares EVERY render field — float fields
    /// (position/rotation/offsets) with epsilon tolerance, all others exact. EXCLUDES Seq (always changes) and Protocol
    /// (constant). 
    ///
    /// This is deliberately a full field-by-field compare rather than a hand-picked subset: a subset silently omits
    /// fields (the S328ah regression was an omitted MoveState → stopped actors kept walking). **If you add a render
    /// field to TransformData, add it here too** — that is the one maintenance obligation, and it's local + obvious,
    /// unlike a scattered dirty-check field list.
    /// </summary>
    public bool RenderEquals(TransformData o, float posEps, float rotEps)
    {
        if (o == null) return false;
        bool FEq(float a, float b, float e) => System.Math.Abs(a - b) <= e;
        return
            // position / rotation / offsets — epsilon
            FEq(X, o.X, posEps) && FEq(Y, o.Y, posEps) && FEq(Z, o.Z, posEps) &&
            FEq(Rotation, o.Rotation, rotEps) && FEq(MountPitch, o.MountPitch, rotEps) &&
            FEq(MinionOffX, o.MinionOffX, posEps) && FEq(MinionOffY, o.MinionOffY, posEps) &&
            FEq(MinionOffZ, o.MinionOffZ, posEps) && FEq(MinionRot, o.MinionRot, rotEps) &&
            FEq(BodyDrawOffsetX, o.BodyDrawOffsetX, posEps) && FEq(BodyDrawOffsetY, o.BodyDrawOffsetY, posEps) &&
            FEq(BodyDrawOffsetZ, o.BodyDrawOffsetZ, posEps) &&
            // movement — exact (these are the transition fields the regression missed)
            MoveState == o.MoveState && MoveMode == o.MoveMode && JumpPhase == o.JumpPhase && IsTurning == o.IsTurning &&
            // gaze / target
            TargetEntityId == o.TargetEntityId &&
            FaceCamera == o.FaceCamera &&
            FaceCamX == o.FaceCamX && FaceCamY == o.FaceCamY && FaceCamZ == o.FaceCamZ &&
            GazeEyesOn == o.GazeEyesOn && GazeEyesX == o.GazeEyesX && GazeEyesY == o.GazeEyesY && GazeEyesZ == o.GazeEyesZ &&
            GazeBodyOn == o.GazeBodyOn && GazeBodyX == o.GazeBodyX && GazeBodyY == o.GazeBodyY && GazeBodyZ == o.GazeBodyZ &&
            GazeHeadOn == o.GazeHeadOn && GazeHeadX == o.GazeHeadX && GazeHeadY == o.GazeHeadY && GazeHeadZ == o.GazeHeadZ &&
            // emote / pose
            EmoteId == o.EmoteId && TimelineId == o.TimelineId && EmoteEpoch == o.EmoteEpoch &&
            PoseType == o.PoseType && CPoseState == o.CPoseState && CharMode == o.CharMode && CharModeParam == o.CharModeParam &&
            // mount
            MountId == o.MountId && MountAnimTimeline == o.MountAnimTimeline &&
            MountActionTimeline == o.MountActionTimeline && MountActionEpoch == o.MountActionEpoch &&
            // minion
            MinionId == o.MinionId && MinionBehaviour == o.MinionBehaviour && MinionAnim == o.MinionAnim &&
            // ornament
            OrnamentId == o.OrnamentId && OrnamentTimeline == o.OrnamentTimeline &&
            OrnamentActionTimeline == o.OrnamentActionTimeline && OrnamentActionEpoch == o.OrnamentActionEpoch &&
            // moniker
            MonikerName == o.MonikerName && MonikerHideFc == o.MonikerHideFc && MonikerHideName == o.MonikerHideName &&
            // map-state (host)
            MapWeatherId == o.MapWeatherId && MapTimeForced == o.MapTimeForced &&
            MapEorzeaHour == o.MapEorzeaHour && MapEorzeaMinute == o.MapEorzeaMinute && MapBgmId == o.MapBgmId &&
            MapRemoveNpcs == o.MapRemoveNpcs && MapHideQuestSigns == o.MapHideQuestSigns && MapStateEpoch == o.MapStateEpoch &&
            // body-offset gate + standup + cosmetic toggles
            BodyDrawOffsetEpoch == o.BodyDrawOffsetEpoch &&
            StandupTimelineId == o.StandupTimelineId && StandupEpoch == o.StandupEpoch &&
            // skills (cosmetic action replay)
            ActionId == o.ActionId && ActionType == o.ActionType && ActionEpoch == o.ActionEpoch &&
            ActionTgtX == o.ActionTgtX && ActionTgtY == o.ActionTgtY && ActionTgtZ == o.ActionTgtZ &&
            ActionTgtCid == o.ActionTgtCid &&
            WeaponDrawn == o.WeaponDrawn && VisorToggled == o.VisorToggled && HatHidden == o.HatHidden;
    }
}

public class ZoneLoadData
{
    [JsonPropertyName("zone")]
    public uint TerritoryId { get; set; }

    [JsonPropertyName("sx")]
    public float SpawnX { get; set; }

    [JsonPropertyName("sy")]
    public float SpawnY { get; set; }

    [JsonPropertyName("sz")]
    public float SpawnZ { get; set; }

    // v0.7.332: cutscene-stage sync. A cutscene stage has no TerritoryType row — it loads via a DONOR territory
    // (TerritoryId above) with its bg PATH swapped in by the CreateScene hook. So a plain territory-id load gives the
    // peer only the donor (the origin apartment) — the "peer gets the torn-down apartment instead of the cutscene" bug.
    // StageBg carries the stage's bg path so the peer can run the same donor-load-with-bg-swap; StageName is the real
    // stage name for the load print (the donor's GetZoneName was the "Host loading zone: Ingleside Apartment" lie).
    [JsonPropertyName("stbg")]
    public string StageBg { get; set; } = "";

    [JsonPropertyName("stnm")]
    public string StageName { get; set; } = "";
}

public class RoomJoinedData
{
    [JsonPropertyName("zone")]
    public uint CurrentZoneId { get; set; }

    [JsonPropertyName("sx")]
    public float SpawnX { get; set; }

    [JsonPropertyName("sy")]
    public float SpawnY { get; set; }

    [JsonPropertyName("sz")]
    public float SpawnZ { get; set; }

    [JsonPropertyName("peers")]
    public string[] PeerIds { get; set; } = [];

    [JsonPropertyName("host")]
    public string HostId { get; set; } = "";
}

public class JoinRoomData
{
    [JsonPropertyName("cid")]
    public ulong ContentId { get; set; }   // S327: stable global identity key (the one we bind on)

    [JsonPropertyName("eid")]
    public uint EntityId { get; set; }

    [JsonPropertyName("name")]
    public string CharacterName { get; set; } = "";
}
