using MessagePack;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// HMSync WIRE TYPES - the SHARED CONTRACT (protocol v4)
//
// ⚠ THIS FILE IS SHARED BY THE PLUGIN, THE RELAY AND THE HARNESS. Do not add plugin- or relay-specific
// dependencies. Pure POCOs + MessagePack attributes only. Namespace is project-neutral (HMSync.Wire).
//
// ⚠⚠ THE INVARIANT IS NOT "BYTE-IDENTICAL IN ALL THREE" - that was the old wording and it is FALSE today, in a
// way that is CORRECT and must not be "repaired". The real invariant, in two halves:
//
//   • CONTROL SURFACE - MUST MATCH BYTE-FOR-BYTE across plugin/relay/harness: WireFormat (magic, options,
//     RelaySender), WireKind, ErrCode, FrameHeader, and the control/Join payload types (JoinPayload,
//     RoomJoinedPayload, PeerJoinedPayload, PeerLeftPayload, HostTransferPayload, KickPeerPayload,
//     ErrorPayload). The relay ENCODES/DECODES these, so a mismatch is a live protocol break.
//
//   • LANE PAYLOADS (Hot/Warm/Cold/Host) - MAY LEGALLY LEAD ON THE CLIENT. The relay never deserializes a lane
//     payload (control is read, cargo is not), so client-only lane keys cost it nothing. The plugin's copy is
//     AHEAD by design: WarmPayload Keys 27–49 (face-camera, gaze, skill replay) and ColdPayload Keys 5-6
//     (MonikerHideName, MonikerHideTitle) exist here and NOT in the relay's copy.
//
//   ⛔ NEVER "resync" this file by copying the relay's copy over the plugin's - that silently DELETES the
//      shipped client-ahead lane features above. Sync the control surface only; diff the lane blocks by eye, never by overwrite.
//
// Authoritative spec: docs/architecture/HMSync-Frame-Format-v4.md. Field ORDER = Key(n) = wire order. NEVER
// reorder or reuse a Key; new fields APPEND with the next Key number (forward-compat, spec §7).
//
// MessagePack options (BOTH sides must use the same): integer-keyed [MessagePackObject], default resolver,
// NO LZ4 frame compression. Serialize/deserialize with MessagePackSerializer using StandardOptions (below) so
// the two codebases can't drift.
//
// LANE payloads (Hot/Warm/Cold/Host) are CLIENT-authored - the relay treats them as opaque bytes and never
// encodes/decodes them. CONTROL + Join payloads are the SHARED encode/decode surface (the relay CONSTRUCTS the
// control ones and READS Join). All live here so the whole wire vocabulary is one file.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

namespace HMSync.Wire;

/// <summary>Shared MessagePack options - BOTH sides serialize/deserialize with this so encoding can't drift.</summary>
public static class WireFormat
{
    /// <summary>Protocol version. The frame magic byte encodes this (0xA5 = v4).</summary>
    public const int ProtocolVersion = 4;

    /// <summary>Frame sentinel + version marker. First byte of every v4 frame.</summary>
    public const byte FrameMagic = 0xA5;

    /// <summary>Sentinel header senderId for relay-authored control frames (spec §3.1).</summary>
    public const string RelaySender = "relay";

    /// <summary>The one options block both codebases use. No LZ4, integer-keyed, standard resolver.</summary>
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.None);
}

// ── Message-kind bytes (spec §3). Shared so the relay's name-map and the client agree. ──
public static class WireKind
{
    public const byte JoinRoom        = 0x01;
    public const byte LeaveRoom       = 0x02;
    public const byte RoomJoined      = 0x03;
    public const byte PeerJoined      = 0x04;
    public const byte PeerLeft        = 0x05;
    public const byte HostTransfer    = 0x06;
    public const byte KickPeer        = 0x07;   // host-gated: eject (+ ban for the room's life) a peer
    // SOFT ingress-throttle notice (relay → client). Empty payload today; extensible later by appending fields
    // to a payload type declared on both sides. NON-FATAL: the socket stays open and the session continues.
    //
    // 📌 WHY A KIND AND NOT AN ERROR CODE. The client's Error handler tears the session down for every code it
    // doesn't explicitly recognise (`if (code != NotHost) DoLeaveInternal`), so a soft throttle delivered as a
    // new ErrorPayload code would DISCONNECT any client older than the change - the exact inverse of "soft".
    // An unknown KIND, by contrast, falls through the client's dispatch switch (no `default:`) and is silently
    // ignored. So: unknown kind = inert, unknown code = fatal. Soft signals ride a kind; fatal ones ride a code.
    public const byte RateLimited     = 0x08;
    public const byte HotUpdate       = 0x11;
    public const byte WarmUpdate      = 0x12;
    public const byte ColdUpdate      = 0x13;
    public const byte HostUpdate      = 0x14;
    public const byte EventPulse      = 0x15;   // reserved
    public const byte ZoneLoadExecute = 0x30;
    public const byte SessionEnd      = 0x40;
    public const byte Ping            = 0xF0;
    public const byte Pong            = 0xF1;
    public const byte Error           = 0xFF;
}

/// <summary>ErrorPayload.Code values - the client branches on these to show the right message.
///
/// MOVED HERE from the relay's Program.cs (was relay-only; the client re-implemented it as magic integers in a
/// switch, so the ONE thing both sides must agree on was the one thing the shared contract didn't cover). This
/// is a constants class, not a payload - no wire change, no flag day. Append new codes; never renumber.</summary>
public static class ErrCode
{
    public const uint Generic        = 0;
    public const uint RoomNotFound   = 1;   // stale cached RoomId - that session has ended
    public const uint NotHosting     = 2;   // nobody nearby is in a session
    public const uint RoomFull       = 3;
    public const uint NotHost        = 4;   // host-only action attempted by a guest
    public const uint Kicked         = 5;   // removed by the host
    public const uint Banned         = 6;   // previously kicked from this room
    public const uint WrongPassword  = 7;
    public const uint AlreadyHosting = 8;   // this ContentId already hosts a live room
    // HARD ingress throttle - sustained saturation. Paired with WebSocket close 4029 "rate_limit_exceeded".
    // Fatal by design (unlike the SOFT tier, which rides WireKind.RateLimited 0x08 and leaves the socket up).
    public const uint RateLimited    = 9;

    /// <summary>Human name for logs + the refusal metric label.</summary>
    public static string Name(uint code) => code switch
    {
        Generic        => "Generic",
        RoomNotFound   => "RoomNotFound",
        NotHosting     => "NotHosting",
        RoomFull       => "RoomFull",
        NotHost        => "NotHost",
        Kicked         => "Kicked",
        Banned         => "Banned",
        WrongPassword  => "WrongPassword",
        AlreadyHosting => "AlreadyHosting",
        RateLimited    => "RateLimited",
        _              => "Unknown",
    };
}

/// <summary>WebSocket close codes the relay uses in the application range (4000–4999, RFC 6455 §7.4.2).
/// Not a msgpack type - a close-frame status the client reads off the receive result.</summary>
public static class WireClose
{
    /// <summary>Hard ingress throttle: sustained flooding. Reason string "rate_limit_exceeded".</summary>
    public const int RateLimitExceeded = 4029;
}

// ═══════════════════════════ LANE PAYLOADS (client-authored; relay-opaque) ═══════════════════════════
// idx 0 subjectId sentinel (spec §4): "" = subject is the stamped sender (player puppet, 1 byte); non-empty
// = explicit NPC/entity id. Receiver: "" → route under header senderId, else under subjectId.

/// <summary>HOT lane (0x11): position/rotation/movement/flight-attitude/body-offset. Carries version + identity.</summary>
[MessagePackObject]
public class HotPayload
{
    [Key(0)]  public string SubjectId { get; set; } = "";   // "" = stamped sender (player puppet)
    [Key(1)]  public uint Seq { get; set; }
    [Key(2)]  public int Protocol { get; set; }             // wire version (rides HOT - always-flowing lane)
    [Key(3)]  public ulong SenderContentId { get; set; }    // identity-binding key (S327) - rides HOT so puppet binds
    [Key(4)]  public float X { get; set; }
    [Key(5)]  public float Y { get; set; }
    [Key(6)]  public float Z { get; set; }
    [Key(7)]  public float Rotation { get; set; }
    [Key(8)]  public float MountPitch { get; set; }
    [Key(9)]  public byte MoveState { get; set; }
    [Key(10)] public byte MoveMode { get; set; }
    [Key(11)] public byte JumpPhase { get; set; }
    [Key(12)] public bool IsTurning { get; set; }
    [Key(13)] public float BodyDrawOffsetX { get; set; }
    [Key(14)] public float BodyDrawOffsetY { get; set; }
    [Key(15)] public float BodyDrawOffsetZ { get; set; }
    [Key(16)] public uint BodyDrawOffsetEpoch { get; set; }
}

/// <summary>WARM lane (0x12): emote/pose/mount/minion/ornament/target/weapon/standup. On change.</summary>
[MessagePackObject]
public class WarmPayload
{
    [Key(0)]  public string SubjectId { get; set; } = "";
    [Key(1)]  public uint TargetEntityId { get; set; }
    [Key(2)]  public ushort EmoteId { get; set; }
    [Key(3)]  public ushort TimelineId { get; set; }
    [Key(4)]  public uint EmoteEpoch { get; set; }
    [Key(5)]  public byte PoseType { get; set; }
    [Key(6)]  public byte CPoseState { get; set; }
    [Key(7)]  public byte CharMode { get; set; }
    [Key(8)]  public byte CharModeParam { get; set; }
    [Key(9)]  public ushort MountId { get; set; }
    [Key(10)] public ushort MountAnimTimeline { get; set; }
    [Key(11)] public ushort MountActionTimeline { get; set; }
    [Key(12)] public uint MountActionEpoch { get; set; }
    [Key(13)] public ushort MinionId { get; set; }
    [Key(14)] public byte MinionBehaviour { get; set; }
    [Key(15)] public ushort MinionAnim { get; set; }
    [Key(16)] public float MinionOffX { get; set; }
    [Key(17)] public float MinionOffY { get; set; }
    [Key(18)] public float MinionOffZ { get; set; }
    [Key(19)] public float MinionRot { get; set; }
    [Key(20)] public ushort OrnamentId { get; set; }
    [Key(21)] public ushort OrnamentTimeline { get; set; }
    [Key(22)] public ushort OrnamentActionTimeline { get; set; }
    [Key(23)] public uint OrnamentActionEpoch { get; set; }
    [Key(24)] public bool WeaponDrawn { get; set; }
    [Key(25)] public ushort StandupTimelineId { get; set; }
    [Key(26)] public uint StandupEpoch { get; set; }
    [Key(27)] public bool FaceCamera { get; set; }   // /facecamera fourth-wall stare - not broadcast by the game natively
    [Key(28)] public float FaceCamX { get; set; }     // the camera eye world point snapshotted at activation (frozen)
    [Key(29)] public float FaceCamY { get; set; }
    [Key(30)] public float FaceCamZ { get; set; }
    // Dynamic face control (Brio-style): per-slot (eyes/body/head) look-at target, driven via updateLookAt. WARM -
    // same lane and snapshot-and-hold model as /facecamera (which is instant on WARM). Set-once-and-hold per slot;
    // the always-armed UI writes the point atomically on "Set cam" so the change registers cleanly. Per-actor.
    [Key(31)] public bool GazeEyesOn { get; set; }
    [Key(32)] public float GazeEyesX { get; set; }
    [Key(33)] public float GazeEyesY { get; set; }
    [Key(34)] public float GazeEyesZ { get; set; }
    [Key(35)] public bool GazeBodyOn { get; set; }
    [Key(36)] public float GazeBodyX { get; set; }
    [Key(37)] public float GazeBodyY { get; set; }
    [Key(38)] public float GazeBodyZ { get; set; }
    [Key(39)] public bool GazeHeadOn { get; set; }
    [Key(40)] public float GazeHeadX { get; set; }
    [Key(41)] public float GazeHeadY { get; set; }
    [Key(42)] public float GazeHeadZ { get; set; }
    // COSM_1_016 skills - cosmetic action replay. Fire-and-forget on an epoch, exactly like MountAction/Standup: the
    // receiver replays only when ActionEpoch CHANGES, so a held value never re-fires. The caster's own client already
    // presents its cast natively (the firewall only stops the SERVER hearing it), so this lane exists purely to let
    // PEERS see it. Replayed via ActionEffectHandler.Receive with zero targets - presentation only, never an effect.
    [Key(43)] public uint ActionId { get; set; }        // the action cast (0 = none)
    [Key(44)] public byte ActionType { get; set; }      // ActionType enum (Action/Item/GeneralAction/…)
    [Key(45)] public uint ActionEpoch { get; set; }     // monotonic - distinguishes a NEW cast from a held value
    [Key(46)] public float ActionTgtX { get; set; }     // area-target position for ground-targeted actions
    [Key(47)] public float ActionTgtY { get; set; }
    [Key(48)] public float ActionTgtZ { get; set; }
    [Key(49)] public ulong ActionTgtCid { get; set; }   // target's stable ContentId (0 = none/non-player → animate on caster)
}

/// <summary>COLD lane (0x13): Moniker + cosmetic toggles. Session-start + on change.</summary>
[MessagePackObject]
public class ColdPayload
{
    [Key(0)] public string SubjectId { get; set; } = "";
    [Key(1)] public string MonikerName { get; set; } = "";
    [Key(2)] public bool MonikerHideFc { get; set; }
    [Key(5)] public bool MonikerHideName { get; set; }   // additive (Moniker IPC 2.2): NEW key index so VisorToggled(3)/HatHidden(4) keep theirs; old peers ignore key 5, missing key 5 → false
    [Key(6)] public bool MonikerHideTitle { get; set; }  // additive (Moniker IPC 2.3): next free key after HideName(5); old peers ignore key 6, missing key 6 → false (title shown)
    [Key(3)] public bool VisorToggled { get; set; }
    [Key(4)] public bool HatHidden { get; set; }
}

/// <summary>HOST lane (0x14): map-state block (host-authoritative). On change + late-join re-send.</summary>
[MessagePackObject]
public class HostPayload
{
    [Key(0)] public string SubjectId { get; set; } = "";
    [Key(1)] public byte MapWeatherId { get; set; }
    [Key(2)] public bool MapTimeForced { get; set; }
    [Key(3)] public ushort MapEorzeaHour { get; set; }
    [Key(4)] public byte MapEorzeaMinute { get; set; }
    [Key(5)] public uint MapBgmId { get; set; }
    [Key(6)] public bool MapRemoveNpcs { get; set; }
    [Key(7)] public bool MapHideQuestSigns { get; set; }
    [Key(8)] public uint MapStateEpoch { get; set; }
}

// ═══════════════════════ CONTROL + JOIN PAYLOADS (shared encode/decode surface) ═══════════════════════
// The relay CONSTRUCTS the control ones (RoomJoined/PeerJoined/PeerLeft/HostTransfer/Error) and READS JoinPayload.
// These MUST encode/decode identically on both sides - that's why this file is shared verbatim.

/// <summary>JoinRoom (0x01) payload - client→relay. The relay READS this to register the connection.</summary>
[MessagePackObject]
public class JoinPayload
{
    [Key(0)] public string RoomId { get; set; } = "";       // empty on host/join; set ONLY on reconnect (cached opaque id)
    [Key(1)] public ulong ContentId { get; set; }           // the joiner's own stable identity
    [Key(2)] public uint EntityId { get; set; }
    [Key(3)] public string CharacterName { get; set; } = "";
    [Key(4)] public string? RoomPassword { get; set; }      // NOW REAL - the user's password (relay compares, constant-time)
    [Key(5)] public bool? CreateIfMissing { get; set; }     // null=legacy (inert), true=Host (create), false=Join/Reconnect
    [Key(6)] public ulong[]? NearbyContentIds { get; set; } // Join: ContentIds you can see; relay resolves the room from these
}

/// <summary>RoomJoined (0x03) payload - relay→client. Tells the client its assigned id + host status.</summary>
[MessagePackObject]
public class RoomJoinedPayload
{
    [Key(0)] public string AssignedPeerId { get; set; } = "";   // the relay-minted peer id for this connection
    [Key(1)] public bool IsHost { get; set; }                   // first-in-room = host
    [Key(2)] public string RoomId { get; set; } = "";           // opaque relay-generated id; CACHE for reconnect
    [Key(3)] public int RoomCap { get; set; }                   // NEW - max peers (relay ROOM_CAP), 0 = unlimited/unshown
}

/// <summary>PeerJoined (0x04) payload - relay→client.</summary>
[MessagePackObject]
public class PeerJoinedPayload
{
    [Key(0)] public string PeerId { get; set; } = "";
    [Key(1)] public ulong ContentId { get; set; }
    [Key(2)] public string CharacterName { get; set; } = "";
}

/// <summary>PeerLeft (0x05) payload - relay→client. NewHostId non-empty on host-leave succession.</summary>
[MessagePackObject]
public class PeerLeftPayload
{
    [Key(0)] public string PeerId { get; set; } = "";
    [Key(1)] public string NewHostId { get; set; } = "";    // "" if no succession
}

/// <summary>HostTransfer (0x06) payload - both directions. Target of an explicit transfer.</summary>
[MessagePackObject]
public class HostTransferPayload
{
    [Key(0)] public string TargetPeerId { get; set; } = "";
}

/// <summary>Error (0xFF) payload - relay→client.</summary>
/// <summary>KickPeer (0x07) payload - client(host)→relay. Host ejects + bans a peer for the room's life.</summary>
[MessagePackObject]
public class KickPeerPayload
{
    [Key(0)] public string TargetPeerId { get; set; } = "";
}

[MessagePackObject]
public class ErrorPayload
{
    [Key(0)] public uint Code { get; set; }
    [Key(1)] public string Message { get; set; } = "";
}

// ── Frame header codec (shared logic - the fixed binary header, NOT msgpack). ──
// Both sides build/parse the header identically. The relay builds the DOWNLEG form (with stamped sender); the
// client builds the UPLEG form (no sender) and parses the DOWNLEG form. Payload bytes are appended/read opaque.

/// <summary>
/// Builds and parses the v4 fixed binary frame header (spec §1). The header is hand-packed binary (not msgpack);
/// only the payload after it is msgpack. Little-endian throughout.
/// </summary>
public static class FrameHeader
{
    // Upleg (client→relay): [magic][kind][flags][timestamp:8][payload]. No sender/room.
    public static byte[] BuildUpleg(byte kind, long timestamp, byte[] payload)
    {
        var buf = new byte[1 + 1 + 1 + 8 + payload.Length];
        buf[0] = WireFormat.FrameMagic;
        buf[1] = kind;
        buf[2] = 0;   // flags
        WriteInt64LE(buf, 3, timestamp);
        System.Buffer.BlockCopy(payload, 0, buf, 11, payload.Length);
        return buf;
    }

    // Downleg (relay→clients): [magic][kind][flags][senderLen:2][senderId][timestamp:8][payload].
    // The relay builds this by stamping the trusted sender; also used to CONSTRUCT relay-authored control frames.
    public static byte[] BuildDownleg(byte kind, string senderId, long timestamp, byte[] payload)
    {
        var senderBytes = System.Text.Encoding.UTF8.GetBytes(senderId);
        var buf = new byte[1 + 1 + 1 + 2 + senderBytes.Length + 8 + payload.Length];
        int o = 0;
        buf[o++] = WireFormat.FrameMagic;
        buf[o++] = kind;
        buf[o++] = 0;   // flags
        WriteUInt16LE(buf, o, (ushort)senderBytes.Length); o += 2;
        System.Buffer.BlockCopy(senderBytes, 0, buf, o, senderBytes.Length); o += senderBytes.Length;
        WriteInt64LE(buf, o, timestamp); o += 8;
        System.Buffer.BlockCopy(payload, 0, buf, o, payload.Length);
        return buf;
    }

    /// <summary>Parsed downleg frame: kind, stamped sender, timestamp, and the opaque payload slice.</summary>
    public struct Parsed
    {
        public byte Kind;
        public string SenderId;
        public long Timestamp;
        public byte[] Payload;
        public bool Ok;
    }

    // Parse a DOWNLEG frame (what a client receives). Returns Ok=false on a bad magic/short frame.
    public static Parsed ParseDownleg(byte[] frame)
    {
        var r = new Parsed { Ok = false };
        if (frame.Length < 1 + 1 + 1 + 2 + 8) return r;          // minimum: header with empty sender + timestamp
        if (frame[0] != WireFormat.FrameMagic) return r;         // bad magic → not a v4 frame
        int o = 1;
        r.Kind = frame[o++];
        o++;   // flags (ignored)
        ushort senderLen = ReadUInt16LE(frame, o); o += 2;
        if (frame.Length < o + senderLen + 8) return r;          // truncated
        r.SenderId = System.Text.Encoding.UTF8.GetString(frame, o, senderLen); o += senderLen;
        r.Timestamp = ReadInt64LE(frame, o); o += 8;
        int payloadLen = frame.Length - o;
        r.Payload = new byte[payloadLen];
        System.Buffer.BlockCopy(frame, o, r.Payload, 0, payloadLen);
        r.Ok = true;
        return r;
    }

    // Parse the header of an UPLEG frame (what the relay receives): [magic][kind][flags][timestamp][payload].
    public struct ParsedUpleg
    {
        public byte Kind;
        public long Timestamp;
        public byte[] Payload;
        public bool Ok;
    }
    public static ParsedUpleg ParseUpleg(byte[] frame)
    {
        var r = new ParsedUpleg { Ok = false };
        if (frame.Length < 1 + 1 + 1 + 8) return r;
        if (frame[0] != WireFormat.FrameMagic) return r;
        r.Kind = frame[1];
        // frame[2] flags ignored
        r.Timestamp = ReadInt64LE(frame, 3);
        int payloadLen = frame.Length - 11;
        r.Payload = new byte[payloadLen];
        System.Buffer.BlockCopy(frame, 11, r.Payload, 0, payloadLen);
        r.Ok = true;
        return r;
    }

    // ── LE primitives (no BitConverter - explicit LE so endianness is never in question) ──
    private static void WriteUInt16LE(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static ushort ReadUInt16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static void WriteInt64LE(byte[] b, int o, long v)
    {
        for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
    }
    private static long ReadInt64LE(byte[] b, int o)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v |= (long)b[o + i] << (8 * i);
        return v;
    }
}

// NOTE: the /hms wiredump decoder lives CLIENT-SIDE (HMSync.Plugin/Sync/WireDumpDecoder.cs), not here - it needs
// System.Text.Json for named-field display, and only the client uses it. Keeping it out of this shared file holds
// the shared dependency surface to just MessagePack, so the relay compiles this file with no extra deps.
