using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace HMSync.Services;

public unsafe class PacketFilterService : IDisposable
{
    private readonly IPluginLog log;
    private readonly IGameInteropProvider hooks;
    private readonly ISigScanner sigScanner;

    private delegate byte SendPacketDelegate(nint a1, nint a2, nint a3, byte a4);
    private Hook<SendPacketDelegate>? sendHook;

    private delegate void ReceivePacketDelegate(PacketDispatcher* a1, uint a2, nint a3);
    private Hook<ReceivePacketDelegate>? receiveHook;

    private delegate nint InteractWithObjectDelegate(nint a1, nint a2, byte a3);
    private Hook<InteractWithObjectDelegate>? interactHook;

    private ushort heartbeatOpcode;
    // True once the heartbeat opcode has been resolved by sig-scan. If false, the filter would suppress the heartbeat
    // too (heartbeatOpcode==0 matches no real packet) → the client disconnects on session start. Session start checks
    // this to fail LOUDLY (refuse + warn) instead of silently dropping the connection - the one heartbeat-sig-break
    // failure mode made diagnosable. (See FFXIV-Network-Opcodes-Security.md §14.)
    public bool HeartbeatResolved => heartbeatOpcode != 0;

    // v0.7.461 (P1, Codex QA): the firewall may only be enabled if EVERY critical hook actually created AND the
    // heartbeat resolved. Enable() is null-safe (sendHook?.Enable()), so if a game patch broke ONLY the send-packet
    // signature, the old code would still set IsActive=true while outbound packets flowed UNFILTERED to the live
    // server - the exact exposure the firewall exists to prevent. sendHook is the load-bearing one (outbound
    // suppression); receiveHook and interactHook round out the guarantee. HeartbeatResolved alone is insufficient
    // because it's independent of these hooks. EngageSyntheticSession must gate on this, not just HeartbeatResolved.
    public bool CanEnable => HeartbeatResolved && sendHook != null && receiveHook != null && interactHook != null;

    public bool IsActive { get; private set; }

    public PacketFilterService(
        IPluginLog log,
        IGameInteropProvider hooks,
        ISigScanner sigScanner)
    {
        this.log = log;
        this.hooks = hooks;
        this.sigScanner = sigScanner;
    }

    public void Initialize()
    {
        try
        {
            var heartbeatAddr = sigScanner.ScanText("C7 44 24 ?? ?? ?? ?? ?? 48 F7 F1");
            heartbeatOpcode = (ushort)Marshal.ReadInt32(heartbeatAddr + 0x4);
            log.Information("[HMSync] Heartbeat opcode: " + heartbeatOpcode);
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Failed to scan heartbeat opcode: " + ex.Message);
            heartbeatOpcode = 0;
        }

        try
        {
            var sendAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 74 24 ?? 4C 89 64 24 ?? 55 41 56 41 57 48 8B EC 48 83 EC 70");
            sendHook = hooks.HookFromAddress<SendPacketDelegate>(sendAddr, OnSendPacket);
            log.Information("[HMSync] Send hook created");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Failed to hook send: " + ex.Message);
        }

        try
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var receiveAddr = (nint)framework->NetworkModuleProxy->NetworkModule
                ->PacketReceiverCallback->PacketDispatcher.VirtualTable->OnReceivePacket;
            receiveHook = hooks.HookFromAddress<ReceivePacketDelegate>(receiveAddr, OnReceivePacket);
            log.Information("[HMSync] Receive hook created");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Failed to hook receive: " + ex.Message);
        }

        // InteractWithObject - prevents NPC interactions from generating packets
        try
        {
            var interactAddr = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 56 48 83 EC 20 48 8B E9 41 0F B6 F0");
            interactHook = hooks.HookFromAddress<InteractWithObjectDelegate>(interactAddr, OnInteractWithObject);
            log.Information("[HMSync] InteractWithObject hook created");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Failed to hook interact: " + ex.Message);
        }
    }

    public void Enable()
    {
        if (IsActive) return;
        // v0.7.461: never enable with a missing critical hook - IsActive must imply the firewall is actually
        // intercepting. The caller (EngageSyntheticSession) checks CanEnable and aborts the load with a warning;
        // this is the defense-in-depth backstop so IsActive can't be set true through any other path either.
        if (!CanEnable)
        {
            log.Error("[HMSync] Packet filter NOT enabled - a critical hook is missing (signature break). Refusing to set active.");
            return;
        }
        sendHook?.Enable();
        receiveHook?.Enable();
        interactHook?.Enable();
        IsActive = true;
        log.Information("[HMSync] Packet filter enabled");
    }

    public void Disable()
    {
        if (!IsActive) return;
        sendHook?.Disable();
        receiveHook?.Disable();
        interactHook?.Disable();
        IsActive = false;
        log.Information("[HMSync] Packet filter disabled");
    }

    private byte OnSendPacket(nint a1, nint a2, nint a3, byte a4)
    {
        if (a2 == nint.Zero)
            return sendHook!.Original(a1, a2, a3, a4);

        try
        {
            var opcode = *(ushort*)a2;

            // Outbound say-opcode finder (re-learn): scan the outbound payload for the marker. A hit means THIS packet
            // is the chat submission → its opcode is the outbound say opcode. The send packet base is a2, opcode at
            // offset 0, payload follows the header. Scans a window from offset 0x20 (past the header) for the marker.
            if (SayFinderTextOut != null)
            {
                try
                {
                    var target = System.Text.Encoding.ASCII.GetBytes(SayFinderTextOut);
                    if (target.Length > 0)
                    {
                        const int scanLen = 256;
                        for (int i = 0x20; i <= 0x20 + scanLen - target.Length; i++)
                        {
                            bool ok = true;
                            for (int j = 0; j < target.Length; j++)
                                if (*(byte*)(a2 + i + j) != target[j]) { ok = false; break; }
                            if (ok)
                            {
                                log.Information("[HMSync] [SAY-FINDER] OUTBOUND MATCH - opcode=" + opcode + " (0x" + opcode.ToString("X3") +
                                    ") carries the marker. THIS is the /say outbound opcode.");
                                SayFinderTextOut = null;
                                OnSayOutOpcodeFound?.Invoke(opcode);
                                break;
                            }
                        }
                    }
                }
                catch { /* short packet */ }
            }

            // Outbound diagnostic (/hms senddiag): log every outbound opcode + whether it's passed or suppressed. This
            // reveals which outbound opcode /say produces (ChatHandler?) and confirms it's being dropped in-session -
            // the likely reason /say doesn't reach peers (the SENDER's outbound chat is killed before it leaves).
            if (SendDiag)
            {
                bool willPass = !IsActive
                    || opcode == heartbeatOpcode
                    || (PassSayChatOut && SayChatOutOpcodes.Contains(opcode));
                log.Information("[HMSync] [SEND-DIAG] outbound opcode=" + opcode + " (0x" + opcode.ToString("X3") + ") → " +
                    (willPass ? "PASS" : "SUPPRESS") + (IsActive ? "" : " [capture-only]"));
            }

            // ⚠ v0.7.418 - SAFETY GATE, and it is not optional.
            // This method ends in `return 1` (suppress). It was previously unreachable outside a
            // session because the send hook only existed while the filter was active. Capture-only
            // now installs it WITHOUT the filter, so without this line every outbound packet the
            // client produced would be swallowed - movement, actions, chat, everything - and the
            // player would be frozen with no indication why.
            // Mirrors OnReceivePacket, which already passes through when !IsActive.
            if (!IsActive)
                return sendHook!.Original(a1, a2, a3, a4);
            // Structure dump for the outbound chat packet (opcode at offset 0 for the send path; payload follows).
            if (SayDump && SayChatOutOpcodes.Contains(opcode))
                DumpChatPacket("OUTBOUND", a2, 0);

            if (opcode == heartbeatOpcode)
                return sendHook!.Original(a1, a2, a3, a4);

            // Say passthrough (outbound): let the sender's spatial-chat submission reach the server so it delivers /say
            // to co-located peers. Safe + on-model: "idle and chatting" is exactly the cover profile (only chat +
            // heartbeat cross to the server). Without this, the sender's /say never leaves and no peer can hear it.
            if (PassSayChatOut && SayChatOutOpcodes.Contains(opcode))
                return sendHook!.Original(a1, a2, a3, a4);

            return 1; // suppress
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Send hook error: " + ex.Message);
            return 1;
        }
    }

    // Outbound diagnostic + outbound say-passthrough (S328k).
    public bool SendDiag;
    public bool PassSayChatOut;
    // Config-driven (S328p): set from config at session start, not a hardcoded literal. 300 = ChatHandler default.
    public readonly HashSet<ushort> SayChatOutOpcodes = new();

    // Structure dump (S328o): when true, dump a labeled hex+ASCII view of chat packets (out 300 / in 912) so we can see
    // the real layout - channel/type byte, sender id, message string offset - to design the structural validator on
    // CONFIRMED offsets rather than inferred ones. Toggled with /hms saydump. Dumps 96 bytes from the packet base.
    public bool SayDump;

    private unsafe void DumpChatPacket(string tag, nint pkt, int opcodeOffset)
    {
        try
        {
            const int n = 96;
            var hex = new System.Text.StringBuilder();
            var asc = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                byte b = *(byte*)(pkt + i);
                hex.Append(b.ToString("X2")).Append(i % 16 == 15 ? "\n         " : " ");
                asc.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                if (i % 16 == 15) asc.Append("\n         ");
            }
            ushort op = *(ushort*)(pkt + opcodeOffset);
            log.Information("[HMSync] [SAY-DUMP] " + tag + " opcode=" + op + " (0x" + op.ToString("X3") + ") first" + n + " bytes:\n" +
                "  hex:   " + hex.ToString().TrimEnd() + "\n" +
                "  ascii: " + asc.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] SayDump error: " + ex.Message);
        }
    }

    // ── D-16 roster struct dump (b31, READ-ONLY measurement) ───────────────────────────────────────────────────────
    // Longer full-payload dump of PlayerSpawn/DespawnCharacter so we can MEASURE the 7.55 structure (ContentId, name,
    // customize block, total length for the fat spawn; leaver-id offset + length for despawn) and then write the
    // opcode-agnostic STRUCTURAL validators on CONFIRMED offsets - never guessed (security rule). Dumps from the packet
    // base a3 so the byte offsets in the log map 1:1 onto what the code reads (opcode @ +0x02, payload @ +0x10). The
    // 32-byte capture ring buffer is too short for the fat spawn; this dumps up to 320 bytes. Works in capture-only (no
    // session needed): the block runs before the IsActive suppress, exactly like SayDump. Toggled via /hms rosterdump,
    // which also seeds SpawnOpcodes/DespawnOpcodes from the live 7.55 map so the two packet types are recognised. This
    // is the chicken-and-egg resolved: we use the currently-correct opcode to LOCATE the packet to measure, so we can
    // then stop depending on the opcode. Read-only throughout - never calls Original, never touches game state.
    public bool RosterDump;

    private unsafe void DumpRosterPacket(string tag, nint pkt, int bytes)
    {
        try
        {
            var hex = new System.Text.StringBuilder();
            var asc = new System.Text.StringBuilder();
            for (int i = 0; i < bytes; i++)
            {
                byte b = *(byte*)(pkt + i);
                hex.Append(b.ToString("X2")).Append(i % 16 == 15 ? "\n         " : " ");
                asc.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                if (i % 16 == 15) asc.Append("\n         ");
            }
            ushort op = *(ushort*)(pkt + 0x02);
            log.Information("[HMSync] [ROSTER-DUMP] " + tag + " opcode=" + op + " (0x" + op.ToString("X3") + ") first " + bytes +
                " bytes from packet base (opcode@+0x02, payload@+0x10):\n" +
                "  hex:   " + hex.ToString().TrimEnd() + "\n" +
                "  ascii: " + asc.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] RosterDump error: " + ex.Message);
        }
    }

    public bool CaptureInbound;
    public HashSet<ushort>? CaptureOpcodes;   // null/empty = log all; else only these

    // ── Say-opcode finder (content correlation) ────────────────────────────────────────────────────────────────────
    // Set this to a distinctive string, then /say that string OUT of session. Every Zone inbound packet whose payload
    // contains the string (as ASCII) gets logged with its opcode → that opcode IS the /say opcode. Unambiguous content
    // match, no timestamp guessing, no hardcoding. Independent of the GUI capture. Auto-clears after the first hit.
    public string? SayFinderText;
    public Action<ushort>? OnSayOpcodeFound;   // S328p: fired when the finder identifies the inbound opcode (re-learn flow)
    public bool RelearnArmed;                  // when true, a finder hit updates + verifies config (vs. just logging)
    public string? SayFinderTextOut;           // S328q: outbound marker scan (parallel to inbound; captures ChatHandler)
    public Action<ushort>? OnSayOutOpcodeFound; // S328q: fired when the outbound submission opcode is identified

    // ── Say passthrough ────────────────────────────────────────────────────────────────────────────────────────────
    // When PassSayChat is true, these opcodes are PASSED inbound even while the filter is active (everything else is
    // still dropped). This lets co-located session members hear each other's spatial chat (/say, /yell, /shout). The
    // DISPLAY filter (SayFilterService) then hides non-session-members' messages. Opcode 695 (0x2B7) = /say, found by
    // content correlation (S328i). Yell/shout added once confirmed (they may share 695 - a chat-type byte in-payload -
    // or use their own opcodes). Passing the packet is safe: it's inbound chat, no server anomaly (we send nothing).
    // Inbound spatial-chat opcode (config-driven, S328p). 912 default carries /say, /yell, AND /shout inbound (one
    // "public chat" packet type; the channel is a byte inside the payload). Guarded by ValidateChatShape so a rotated
    // opcode fails CLOSED. Drift detection: too many consecutive validation failures on the configured opcode means
    // the opcode has rotated away → DriftDetected fires → the plugin shuts the passthrough + notifies the user.
    public bool PassSayChat;
    public readonly HashSet<ushort> SayChatOpcodes = new();
    public bool DriftDetected;
    public Action? OnDriftDetected;
    private int consecutiveChatShapeFailures;
    private const int DriftFailureThreshold = 20;

    // ── D-16: room-roster register (READ-ONLY, out-of-band) ────────────────────────────────────────────────────────
    // While the firewall is active, the server keeps streaming the REAL zone to us (we present as an idle/chatting
    // player). Spawn/despawn packets for people who walk into / out of the room therefore still reach this receive hook
    // - but they're SUPPRESSED (we never call Original for them), so no actor is instantiated in the virtual zone: no
    // object-index/coordinate/ActorVisibility conflict, no outbound leak. This register READS those packets purely to
    // know WHO is physically in the room, so a later reconcile can hide phantoms of the departed and (luxury, via the
    // existing S331 late-join path) let a genuine walk-in join the session after entering the password.
    //
    // Identification is by NAME-resolved opcode (PlayerSpawn / DespawnCharacter), pushed in from OpcodeMapService. There
    // is no free self-healing code-sig here (unlike the heartbeat), so we fail CLOSED two ways: (1) an unresolved name
    // leaves the set empty → nothing matches → nothing tracked; (2) a resolved-but-rotated opcode now pointing at some
    // other packet won't carry a plausible player entity id, so the range check below rejects it. Keys:
    //   • PlayerSpawn arrival id  = a2 (the hook's targetId = the spawner). MEASURED from live capture.
    //   • DespawnCharacter leaver = payload offset 4 (uint32 LE). MEASURED from capture 2 (a2 here is the LOCAL player,
    //     NOT the leaver; the departing actor id lives in the payload). Corrected from an earlier offset-3 image guess.
    // The register holds ephemeral per-session entity object ids only (never ContentIds) and logs COUNTS only.
    public bool RoomTrackingEnabled;
    // D-16.2/.3: INBOUND pass-through. When true, a VALIDATED PlayerSpawn/DespawnCharacter is passed to Original instead
    // of suppressed, so the game keeps a TRUTHFUL object table while firewalled: a real walk-in is instantiated (hidden
    // by ActorVisibility until/unless they join the session, where the existing ContentId-bind + RegisterPeer show and
    // drive them) and a leaver is removed (no frozen phantom). This is the ONLY inbound relaxation of the firewall and
    // it is deliberately narrow: INBOUND ONLY (the outbound send hook is untouched, so nothing about YOU ever leaks),
    // PLAYERS ONLY (NpcSpawn/ObjectSpawn stay suppressed - real-zone NPCs must not bleed into the virtual scene), and
    // structurally fail-closed (a rotated opcode that no longer carries a plausible 0x1nnnnnnn player id is NOT passed).
    // "Only heartbeat + chat crosses" widens to "+ validated player spawn/despawn, inbound"; the security core (no
    // outbound, no self-position snap-back) is unchanged.
    public bool RoomPassthrough;
    public readonly HashSet<ushort> SpawnOpcodes = new();     // PlayerSpawn (arrival key = a2)
    public readonly HashSet<ushort> DespawnOpcodes = new();   // DespawnCharacter (leaver key = payload +4)

    // ── Stage 2 (D-16): PlayerSpawn opcode SELF-RE-LEARN ───────────────────────────────────────────────────────────
    // After a game patch rotates PlayerSpawn's opcode, the seeded SpawnOpcodes set (from the embedded opcodes.min.json)
    // no longer matches, so walk-ins silently stop instantiating - and there's NO drift signal, because we never even
    // reach the shape check (SpawnOpcodes.Contains is false). Backstop: while RoomPassthrough is active, cheaply test
    // every inbound packet whose opcode is NOT already a known spawn/despawn against the fixed-header spawn SIGNATURE
    // (ContentId>0xFFFF @+0x10, exact magic 0x00400017 @+0x1C, 0xE0 @+0x33). That signature is so specific a non-spawn
    // match is astronomically unlikely, so a hit on an UNEXPECTED opcode = the rotated PlayerSpawn. After
    // RelearnConfirmThreshold sightings of the same opcode (guards a one-off misparse) we re-seed SpawnOpcodes to it -
    // the spawn-side analogue of the heartbeat's code-sig self-recovery: recover the opcode even when the map is stale.
    // Threading: mutated on the NETWORK thread (same thread as the Contains reads in the hook), so those are serialized;
    // ConfigureRosterOpcodes (framework thread, at engage/teardown) is the only cross-thread writer, a rare boundary
    // event consistent with the existing lock-free posture on these sets.
    private readonly Dictionary<ushort, int> spawnRelearnCandidates = new();
    private const int RelearnConfirmThreshold = 3;
    public Action<ushort>? OnSpawnOpcodeRelearned;   // optional notify/persist (NETWORK thread - marshal before game state)

    // ── Stage 2 (D-16): DespawnCharacter opcode SELF-RE-LEARN (CORRELATION, not fingerprint) ────────────────────────
    // Despawn has no strong shape (small index @+0x10, trackable leaver @+0x14 - too weak to scan opcode-agnostically like
    // the spawn magic). So we correlate against state we already hold instead. MEASURED wedge (today's captures): a real
    // despawn's a2 (hook targetId) is the LOCAL PLAYER and the leaver is in the payload @+0x14 - unlike a per-actor
    // move/position packet, whose a2 IS that actor. A candidate packet on an UNKNOWN opcode must pass ALL of:
    //   (1) a2 == LocalPlayerEntityId          → addressed-to-me, not a per-actor broadcast (excludes ~all movement)
    //   (2) small index @+0x10 (<0x10000) AND trackable 0x1nnnnnnn @+0x14 != a2  → despawn-shaped, position floats fail
    //   (3) leaver @+0x14 ∈ roomActors         → the departer is someone we SAW SPAWN (the actual spawn↔despawn link)
    //   (4) uniqueness: same id repeating on that opcode = streaming (status/move) → DISQUALIFY the opcode for the session
    // After DespawnRelearnConfirm distinct tracked-actor departures (each exactly once) on one opcode → re-seed. Fail-closed:
    // a false-miss just lingers a phantom (cosmetic); the strict AND-stack makes a false-accept extremely unlikely. Leans on
    // the measured a2==local fact - RE-CONFIRM at the next live rotation. (Spawn re-learn feeds roomActors, which feeds this.)
    public uint LocalPlayerEntityId;   // cached at engage on the framework thread; read on the network thread (atomic uint)
    private sealed class DespawnRelearn { public readonly HashSet<uint> Leavers = new(); public bool Disqualified; }
    private readonly Dictionary<ushort, DespawnRelearn> despawnRelearnCandidates = new();
    private const int DespawnRelearnConfirm = 3;
    public Action<ushort>? OnDespawnOpcodeRelearned;   // optional notify/persist (NETWORK thread - marshal before game state)
    private readonly Dictionary<uint, DateTime> roomActors = new();   // entity id → first-seen wallclock
    private readonly object rosterLock = new();
    // Fired on the network thread when the register changes. Consumers MUST marshal to the framework thread before
    // touching game state. Optional - the register is useful on its own via SnapshotRoster() for exit-time reconcile.
    public Action<uint>? OnRoomActorArrived;
    public Action<uint>? OnRoomActorDeparted;

    // FFXIV player/battle-chara object ids sit in the 0x1nnnnnnn band (both live captures: 0x100D52C8, 0x10094A5E).
    // 0xE0000000 is the "none" sentinel. Anything outside the band is a misparse (rotated opcode / wrong offset) → reject.
    private static bool IsTrackableActorId(uint id) => id != 0 && id != 0xE0000000u && (id >> 28) == 0x1u;

    // Push the NAME-resolved opcodes in at session engage. Empty sets (name not in the loaded map) = tracking inert for
    // that packet = fail-closed. Clearing the register here too so a fresh session starts from an empty room.
    public void ConfigureRosterOpcodes(IEnumerable<ushort> spawnOpcodes, IEnumerable<ushort> despawnOpcodes)
    {
        SpawnOpcodes.Clear();
        foreach (var op in spawnOpcodes) if (op != 0) SpawnOpcodes.Add(op);
        DespawnOpcodes.Clear();
        foreach (var op in despawnOpcodes) if (op != 0) DespawnOpcodes.Add(op);
        spawnRelearnCandidates.Clear();   // fresh session → discard any stale re-learn tallies
        despawnRelearnCandidates.Clear();
        lock (rosterLock) roomActors.Clear();
        log.Information("[HMSync] [ROSTER] configured: " + SpawnOpcodes.Count + " spawn opcode(s), " +
            DespawnOpcodes.Count + " despawn opcode(s). Tracking " + (RoomTrackingEnabled ? "ON" : "OFF") + ".");
    }

    public void ClearRoster() { lock (rosterLock) roomActors.Clear(); }
    public int RosterCount { get { lock (rosterLock) return roomActors.Count; } }
    // Snapshot the currently-present real actor ids (copy; safe to read off-thread). Used at exit-time reconcile.
    public List<uint> SnapshotRoster() { lock (rosterLock) return new List<uint>(roomActors.Keys); }

    // Called from OnReceivePacket (network thread) for every inbound packet while the filter is active. Reads the
    // arrival/departure keys and updates the register. Never touches game state, never calls Original.
    private void UpdateRoster(uint a2, nint a3)
    {
        try
        {
            if (SpawnOpcodes.Count == 0 && DespawnOpcodes.Count == 0) return;
            ushort op = *(ushort*)(a3 + 0x02);
            if (SpawnOpcodes.Contains(op))
            {
                uint id = a2;                                   // arrival = hook targetId (the spawner)
                if (!IsTrackableActorId(id)) return;            // fail-closed on a misparse
                bool added;
                lock (rosterLock) { added = !roomActors.ContainsKey(id); if (added) roomActors[id] = DateTime.UtcNow; }
                if (added)
                {
                    log.Debug("[HMSync] [ROSTER] arrival tracked (room now " + RosterCount + ").");
                    OnRoomActorArrived?.Invoke(id);
                }
            }
            else if (DespawnOpcodes.Contains(op))
            {
                uint id = *(uint*)(a3 + 0x10 + 4);              // leaver = payload offset 4 (MEASURED, capture 2)
                if (!IsTrackableActorId(id)) return;
                bool removed;
                lock (rosterLock) removed = roomActors.Remove(id);
                if (removed)
                {
                    log.Debug("[HMSync] [ROSTER] departure tracked (room now " + RosterCount + ").");
                    OnRoomActorDeparted?.Invoke(id);
                }
            }
        }
        catch { /* payload shorter than expected / transient - ignore, register just misses this one */ }
    }

    // ── D-16 STRUCTURAL VALIDATORS (opcode-agnostic recognition) ───────────────────────────────────────────────────
    // The pass-through no longer trusts the opcode alone: a packet is only passed to Original if it is STRUCTURALLY a
    // player spawn / despawn. This is the same fail-closed doctrine as ValidateChatShape - a rotated opcode now pointing
    // at some OTHER packet fails the shape test and is suppressed, so the "only heartbeat + chat + validated
    // spawn/despawn crosses" model survives an opcode rotation. Offsets MEASURED from live 7.55 captures (20 spawns +
    // many despawns; see WORKING-CHANGELOG b33/b34). Both validators are NETWORK-THREAD-SAFE: they read only the packet
    // buffer and NEVER touch the object table / game state (which would be unsafe off the framework thread).
    //
    // The name-obfuscation cipher (spawn): the character name is stored as an additive cipher,
    // decoded = (rawByte + 0x3E) & 0xFF  (pad 0xC2 → 0x00, separator 0xE2 → space 0x20). Decoding city-plaza samples at
    // +0x262 yielded plausible names ("Trey Monmokai", "Ruby Blaire", "Rose Embercrest").
    // b36 CAVEAT — +0x262 is NOT a stable name offset: in-session peer spawns shift the tail (variable gear/mount/status
    // length), so the name is elsewhere there and the offset can't anchor the validator (it caused a false-reject). These
    // consts are RETAINED as the measured cipher + city-spawn offset (reference / future name-decode use), NOT used by
    // ValidateSpawnShape, which now keys on the fixed-header signature (ContentId + 0x00400017 @+0x1C + 0xE0 @+0x33).
    private const byte NameCipherAdd = 0x3E;   // decode: (rawByte + 0x3E) & 0xFF
    private const int SpawnNameOffset = 0x262; // abs offset of the name in a CITY-PLAZA PlayerSpawn (context-dependent)

    // Fat player-spawn. All of (1)-(3) live within the first 0x34 bytes, so they are safe to read even if a rotated
    // opcode points at a SHORT packet (no far over-read → no access-violation risk during a blind re-learn scan). Only
    // after (1)-(3) pass - which prove the packet IS a fat spawn - do we read the deep name field at +0x262.
    private unsafe bool ValidateSpawnShape(nint a3)
    {
        try
        {
            // (1) 8-byte ContentId at payload+0 (abs +0x10): nonzero and larger than a despawn's small index word.
            ulong contentId = *(ulong*)(a3 + 0x10);
            // (2) Fixed structural marker at payload+0x0C (abs +0x1C) = 17 00 40 00 in ALL 20 measured spawns (7.55).
            uint marker = *(uint*)(a3 + 0x1C);
            // (3) First appearance/status-slot sentinel: byte at abs +0x33 == 0xE0 in every measured spawn.
            byte sentinel = *(byte*)(a3 + 0x33);
            bool c1 = contentId > 0xFFFF;
            bool c2 = marker == 0x00400017u;
            bool c3 = sentinel == 0xE0;
            // NOTE (b36): the 4th check - "name first byte decodes A-Z @ +0x262" - was REMOVED after a measured
            // false-reject. In-session peer spawns shift the tail (gear/mount/status length is variable), so +0x262 does
            // NOT hold the name there and c4 rejected a REAL spawn (Hael Vera: c1/c2/c3 all True, c4 name0=0x5E → invisible
            // late-join regression, b34). c1+c2+c3 are already an overwhelming signature - an 8-byte nonzero ContentId, the
            // exact 32-bit magic 0x00400017 at a FIXED header offset, and 0xE0 @ +0x33, all inside the first 52 bytes (fixed
            // header region, over-read-safe). A rotated opcode coincidentally matching all three is astronomically unlikely;
            // the fragile deep name offset added marginal confirmation at the cost of the regression, so it's gone.
            bool ok = c1 && c2 && c3;
            if (!ok)
                // Diagnostic: dump the actual field values so a rejected packet on the spawn opcode tells us which check
                // failed - drift signal (Information so no /senddiag needed; spawns are infrequent).
                log.Information("[HMSync] [SHAPE-SPAWN] FAIL cid=0x" + contentId.ToString("X") +
                    " marker=0x" + marker.ToString("X8") + "(want 00400017) sent=0x" + sentinel.ToString("X2") +
                    "(want E0) → c1=" + c1 + " c2=" + c2 + " c3=" + c3);
            return ok;
        }
        catch (Exception ex) { log.Information("[HMSync] [SHAPE-SPAWN] EXC " + ex.Message); return false; }
    }

    // Silent (NO logging) spawn-signature test for the self-re-learn scan - the SAME fixed-header 3-check signature
    // ValidateSpawnShape enforces, minus the diagnostic log (this runs on MANY packets; logging would flood). All reads
    // are fixed offsets inside the first 52 bytes: over-read-safe on the reused dispatch buffer, and the exact 32-bit
    // 0x00400017 magic makes a stale-byte false positive negligible.
    private unsafe bool MatchesSpawnSignature(nint a3)
    {
        try
        {
            return *(ulong*)(a3 + 0x10) > 0xFFFF
                && *(uint*)(a3 + 0x1C) == 0x00400017u
                && *(byte*)(a3 + 0x33) == 0xE0;
        }
        catch { return false; }
    }

    // Correlation-based DespawnCharacter re-learn (see the field-block doctrine above). Called only for UNKNOWN opcodes
    // (a known despawn short-circuits before we get here). Does the full AND-stack internally and re-seeds DespawnOpcodes
    // once a candidate opcode is confirmed. Read-only w.r.t. the packet - we never pass it; the NEXT despawn on the
    // re-seeded opcode flows through the validated pass-through. Runs on the network thread (same as the roster reads).
    private unsafe void TryRelearnDespawnOpcode(uint a2, nint a3, ushort rop)
    {
        try
        {
            // (1) addressed-to-me: a despawn's a2 is the LOCAL player (measured). No cached id yet ⇒ can't correlate.
            uint self = LocalPlayerEntityId;
            if (self == 0 || a2 != self) return;
            // (2) despawn-shaped: small object index, and a trackable leaver id that isn't us.
            uint idx = *(uint*)(a3 + 0x10);
            if (idx >= 0x10000) return;
            uint leaver = *(uint*)(a3 + 0x14);
            if (!IsTrackableActorId(leaver) || leaver == self) return;
            // (3) the departer must be someone we SAW SPAWN - the actual spawn↔despawn correlation.
            bool known; lock (rosterLock) known = roomActors.ContainsKey(leaver);
            if (!known) return;

            // (4)+(confirm): tally distinct one-shot departures per candidate opcode; a repeat = streaming ⇒ disqualify.
            if (!despawnRelearnCandidates.TryGetValue(rop, out var st))
            {
                st = new DespawnRelearn();
                despawnRelearnCandidates[rop] = st;
            }
            if (st.Disqualified) return;
            if (!st.Leavers.Add(leaver))       // already seen this id on this opcode → it STREAMS → not a despawn
            {
                st.Disqualified = true;
                st.Leavers.Clear();
                return;
            }
            if (st.Leavers.Count >= DespawnRelearnConfirm)
            {
                string old = DespawnOpcodes.Count > 0 ? string.Join(",", DespawnOpcodes) : "∅";
                DespawnOpcodes.Clear();          // DespawnCharacter is singular → replace the stale opcode wholesale
                DespawnOpcodes.Add(rop);
                despawnRelearnCandidates.Clear();
                log.Warning("[HMSync] [RELEARN] DespawnCharacter opcode rotated: " + DespawnRelearnConfirm +
                    " tracked-actor departures now correlate on opcode " + rop + " (0x" + rop.ToString("X3") + "); was {" +
                    old + "}. Re-seeded → leaver-phantom cleanup restored (patch-resilient, fail-closed unchanged).");
                OnDespawnOpcodeRelearned?.Invoke(rop);
            }
        }
        catch { /* over-read/transient → ignore; re-learn is best-effort and must never fault the hook */ }
    }

    // Short despawn. payload+0 (abs +0x10) = small object/spawn index (0x02-0x4B measured); payload+4 (abs +0x14) =
    // leaver actor id, always 0x10nnnnnn. A spawn-on-despawn-opcode misparse is rejected because a spawn's payload+0 is
    // the ContentId low word (huge), not a small index. Handles the ORPHAN case (a player present before session engage,
    // whose spawn we never saw): their leaver id is still a trackable 0x1nnnnnnn and the packet is despawn-shaped, so it
    // passes and the game removes the actor - no frozen phantom - WITHOUT needing an (unsafe) off-thread object-table read.
    private unsafe bool ValidateDespawnShape(nint a3)
    {
        try
        {
            uint idx = *(uint*)(a3 + 0x10);
            uint leaver = *(uint*)(a3 + 0x14);
            bool c1 = idx < 0x10000;                           // real despawns carry a small object index here
            bool c2 = IsTrackableActorId(leaver);
            bool ok = c1 && c2;
            if (!ok)
                log.Information("[HMSync] [SHAPE-DESPAWN] FAIL idx=0x" + idx.ToString("X") +
                    "(want <10000) leaver=0x" + leaver.ToString("X8") + "(want 1nnnnnnn) → c1=" + c1 + " c2=" + c2);
            return ok;
        }
        catch (Exception ex) { log.Information("[HMSync] [SHAPE-DESPAWN] EXC " + ex.Message); return false; }
    }

    // Structural validator (S328p) - the SAFETY CORE. Confirms a packet on the say opcode really IS spatial chat, by
    // STRUCTURE, before passing it. Offsets confirmed from live captures (network doc §27):
    //   payload chat-type @ +0x26 (2 bytes LE) ∈ {Say=10, Shout=11, Yell=30}   ← the decisive field
    //   sender ContentId  @ +0x10 (8 bytes) nonzero
    //   sender name       @ +0x28 begins with an ASCII letter
    // A rotated opcode now pointing at a position/spawn packet fails these → NOT passed → the "only heartbeat + chat
    // crosses" model holds across an opcode rotation. Also drives drift detection.
    private unsafe bool ValidateChatShape(nint pkt)
    {
        try
        {
            ushort chatType = *(ushort*)(pkt + 0x26);
            // NB-7: say/yell/shout AND /em all ride the SAME inbound "public-chat" opcode (825 on 7.55); the chat-type
            // byte is the only discriminator. /em is CustomEmote=28 (0x1C), MEASURED from a live capture (two peers) -
            // not guessed. Widening the accepted set here (vs. a separate lane) is correct because the /em packet
            // already arrives on the say opcode; it just needs to pass the shape gate. StandardEmote is a DIFFERENT
            // code and is NOT whitelisted until measured - fail-closed by omission.
            if (chatType != 10 && chatType != 11 && chatType != 30 && chatType != 28) return false;   // say/shout/yell/em
            ulong senderContentId = *(ulong*)(pkt + 0x10);
            if (senderContentId == 0) return false;                                 // chat always has a sender
            byte nameFirst = *(byte*)(pkt + 0x28);
            if (!((nameFirst >= 'A' && nameFirst <= 'Z') || (nameFirst >= 'a' && nameFirst <= 'z'))) return false;
            return true;
        }
        catch { return false; }
    }

    // Load the say opcodes from config (called at session start). Resets drift state so a fresh session re-evaluates.
    public void ConfigureSayOpcodes(uint outbound, uint inbound)
    {
        SayChatOutOpcodes.Clear();
        if (outbound != 0) SayChatOutOpcodes.Add((ushort)outbound);
        SayChatOpcodes.Clear();
        if (inbound != 0) SayChatOpcodes.Add((ushort)inbound);
        consecutiveChatShapeFailures = 0;
        DriftDetected = false;
    }

    // Captured-packet ring buffer for the GUI inspector. Bounded so a firehose can't grow unbounded.
    public readonly struct CapturedPacket
    {
        public readonly ushort Opcode;
        public readonly int Timestamp;
        public readonly uint EntityId;      // actor id from payload+0 (most actor packets carry it here; 0 if not applicable)
        public readonly string PayloadHex;
        public readonly System.DateTime WallClock;
        public CapturedPacket(ushort op, int ts, uint eid, string hex, System.DateTime wc)
        { Opcode = op; Timestamp = ts; EntityId = eid; PayloadHex = hex; WallClock = wc; }
    }
    private const int CaptureBufferMax = 500;
    private readonly System.Collections.Generic.List<CapturedPacket> capturedPackets = new(CaptureBufferMax);
    private readonly object captureLock = new();
    private long captureSeq;   // monotonic index for display

    public long CaptureCount { get { lock (captureLock) return captureSeq; } }
    public void ClearCapture() { lock (captureLock) { capturedPackets.Clear(); } }
    // Snapshot the buffer for the UI (returns a copy so the UI never touches the live list off-thread).
    public System.Collections.Generic.List<CapturedPacket> SnapshotCapture()
    {
        lock (captureLock) return new System.Collections.Generic.List<CapturedPacket>(capturedPackets);
    }

    // Enable JUST the receive hook for capture, without the full filter (send/interact). Used for inspecting inbound
    // traffic outside a session (e.g. idling in an inn to see what specific opcodes carry). Safe: OnReceivePacket passes
    // packets through when !IsActive, so this doesn't suppress your inbound.
    public void EnableCaptureOnly()
    {
        if (IsActive) return;                 // full filter already running → its receive hook is live
        receiveHook?.Enable();
        // v0.7.418: install the SEND hook too, so outbound traffic is observable with no session
        // running. Safe only because OnSendPacket now returns Original unconditionally when
        // !IsActive - see the safety gate there. This is the window the exit-freeze investigation
        // needs: after teardown, does the client still emit movement at all?
        sendHook?.Enable();
    }
    public void DisableCaptureOnly()
    {
        if (IsActive) return;                 // don't touch the hooks if the full filter owns them
        receiveHook?.Disable();
        sendHook?.Disable();
    }

    private void OnReceivePacket(PacketDispatcher* a1, uint a2, nint a3)
    {
        if (a3 == nint.Zero)
        {
            if (!IsActive) receiveHook!.Original(a1, a2, a3);
            return;
        }

        try
        {
            // Full inbound opcode log (under SendDiag): shows EVERY inbound opcode reaching the receiver, so if the say
            // (695) isn't arriving we can see what DOES - distinguishes "server didn't send it" (proximity broke) from
            // "it arrived but we didn't render" (downstream). Throttled to say-plausible range to avoid a firehose.
            if (SendDiag && IsActive)
            {
                ushort iop = *(ushort*)(a3 + 0x02);
                // Log a compact marker for inbound packets while in session (helps spot the say opcode if it's not 695).
                log.Information("[HMSync] [RECV-ALL] inbound opcode=" + iop + " (0x" + iop.ToString("X3") + ")");
            }
            // Structure dump for the inbound chat packet (opcode at +0x02, payload at +0x10 for the receive path).
            if (SayDump)
            {
                ushort iopd = *(ushort*)(a3 + 0x02);
                if (SayChatOpcodes.Contains(iopd))
                    DumpChatPacket("INBOUND", a3, 0x02);
            }

            // D-16 roster struct dump (b31): full-payload measurement of PlayerSpawn/DespawnCharacter. Recognised by the
            // currently-seeded opcode (from the live 7.55 map). Spawn is fat → 320 bytes; despawn is short → 64 bytes
            // (capped so we don't read far past a small buffer). Read-only; runs in capture-only and in-session both.
            if (RosterDump)
            {
                ushort rdop = *(ushort*)(a3 + 0x02);
                if (SpawnOpcodes.Contains(rdop)) DumpRosterPacket("SPAWN", a3, 768);
                else if (DespawnOpcodes.Contains(rdop)) DumpRosterPacket("DESPAWN", a3, 64);
            }

            // Say-opcode finder: scan the payload for the target string (set via SayFinderText). A hit means THIS packet
            // carries the /say text → log its opcode. Content correlation - definitive, no timestamp matching.
            if (SayFinderText != null)
            {
                try
                {
                    ushort fop = *(ushort*)(a3 + 0x02);
                    // Scan a generous window of the payload for the ASCII bytes of the target string.
                    var target = System.Text.Encoding.ASCII.GetBytes(SayFinderText);
                    if (target.Length > 0)
                    {
                        const int scanLen = 512;
                        int matchAt = -1;
                        for (int i = 0x10; i <= 0x10 + scanLen - target.Length; i++)
                        {
                            bool ok = true;
                            for (int j = 0; j < target.Length; j++)
                                if (*(byte*)(a3 + i + j) != target[j]) { ok = false; break; }
                            if (ok) { matchAt = i; break; }
                        }
                        if (matchAt >= 0)
                        {
                            log.Information("[HMSync] [SAY-FINDER] MATCH - opcode=" + fop + " (0x" + fop.ToString("X3") +
                                ") carries '" + SayFinderText + "' at payload offset 0x" + matchAt.ToString("X") +
                                ". THIS is the /say inbound opcode (also carries /em - same opcode, chat-type distinguishes).");
                            SayFinderText = null;   // one-shot: clear after the first hit
                            OnSayOpcodeFound?.Invoke(fop);   // re-learn: let the plugin update + verify config
                        }
                    }
                }
                catch { /* payload shorter than scan window - ignore */ }
            }

            if (CaptureInbound)
            {
                // IPC segment header (FFXIVClientStructs ServerIpcSegmentHeader, size 0x10):
                //   +0x02 OpCode · +0x0C Timestamp · +0x10 payload start.
                ushort opcode = *(ushort*)(a3 + 0x02);
                if (CaptureOpcodes == null || CaptureOpcodes.Count == 0 || CaptureOpcodes.Contains(opcode))
                {
                    int ts = *(int*)(a3 + 0x0C);
                    // The target entity id is the SECOND hook parameter (a2 / targetId) - this is exactly how Dalamud's
                    // NetworkMonitor gets it, NOT a payload offset. ZoneUp (outbound) would be 0; inbound carries the id.
                    uint eid = a2;
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < 32; i++)
                        sb.Append((*(byte*)(a3 + 0x10 + i)).ToString("X2")).Append(' ');
                    var entry = new CapturedPacket(opcode, ts, eid, sb.ToString().TrimEnd(), System.DateTime.Now);
                    lock (captureLock)
                    {
                        capturedPackets.Add(entry);
                        if (capturedPackets.Count > CaptureBufferMax) capturedPackets.RemoveAt(0);
                        captureSeq++;
                    }
                }
            }

            // D-16: update the read-only room roster BEFORE the suppress below. This only READS the spawn/despawn keys
            // (arrival = a2, leaver = payload+4) to know who's physically in the room; the packet is still dropped by the
            // suppress that follows, so nothing is instantiated. Gated on IsActive because the register describes the
            // firewalled in-session room (outside a session, actors render normally and need no tracking).
            if (IsActive && RoomTrackingEnabled)
                UpdateRoster(a2, a3);

            // Drop inbound ONLY when the filter is genuinely active (in a session). If we're merely CAPTURING outside a
            // session, PASS the packet through - otherwise capturing in an inn would break your inbound traffic.
            if (IsActive)
            {
                // Say passthrough: pass spatial-chat packets (/say/yell/shout) so co-located members hear each other;
                // the display filter hides non-members afterward. Everything else is still suppressed.
                if (PassSayChat)
                {
                    ushort op = *(ushort*)(a3 + 0x02);
                    if (SayChatOpcodes.Contains(op))
                    {
                        // SAFETY CORE: pass only if the packet really IS chat-shaped. A rotated opcode now pointing at
                        // a position/spawn packet fails this → dropped → the "only heartbeat + chat crosses" model holds.
                        if (ValidateChatShape(a3))
                        {
                            consecutiveChatShapeFailures = 0;
                            if (SendDiag)
                                log.Information("[HMSync] [RECV-DIAG] inbound say opcode=" + op + " (0x" + op.ToString("X3") + ") → PASS (chat-shaped, rendering)");
                            receiveHook!.Original(a1, a2, a3);   // pass this chat packet
                            return;
                        }
                        // NB-7: say/yell/shout/em all ride this one opcode and all now pass ValidateChatShape, so a
                        // shape-fail here is genuine drift (the opcode rotated to something that ISN'T public chat),
                        // not an unhandled chat channel. Count consecutive failures → shut + notify if the opcode moved.
                        if (SendDiag)
                        {
                            ushort ct = 0; try { ct = *(ushort*)(a3 + 0x26); } catch { }
                            log.Information("[HMSync] [RECV-DIAG] inbound say opcode=" + op + " (0x" + op.ToString("X3") +
                                ") chatType=" + ct + " (0x" + ct.ToString("X2") + ") ∉ {Say=10,Shout=11,Yell=30,Em=28} → REJECTED (drift candidate).");
                        }
                        // On the configured opcode but NOT chat-shaped: candidate drift. Count consecutive failures;
                        // enough of them means the opcode rotated to something else → shut the passthrough + notify.
                        if (!DriftDetected && ++consecutiveChatShapeFailures >= DriftFailureThreshold)
                        {
                            DriftDetected = true;
                            log.Warning("[HMSync] [DRIFT] opcode " + op + " (0x" + op.ToString("X3") + ") stopped carrying chat-shaped packets after " +
                                DriftFailureThreshold + " tries - likely rotated by a patch. Shutting say passthrough (fail-closed).");
                            OnDriftDetected?.Invoke();
                        }
                        return;   // not chat-shaped → suppress (fail closed), do NOT pass
                    }
                }
                // D-16.2/.3 room pass-through: let a VALIDATED player spawn/despawn cross INBOUND so the object table
                // stays truthful (walk-in instantiated + hidden; leaver removed → no phantom). Fail-closed: opcode must
                // be in the configured set AND carry a plausible 0x1nnnnnnn player id, else suppress. Players only -
                // NpcSpawn/ObjectSpawn are not in SpawnOpcodes, so real-zone NPCs stay firewalled out of the scene.
                if (RoomPassthrough)
                {
                    ushort rop = *(ushort*)(a3 + 0x02);
                    bool isSpawn = SpawnOpcodes.Contains(rop);
                    bool isDespawn = !isSpawn && DespawnOpcodes.Contains(rop);
                    if (isSpawn || isDespawn)
                    {
                        // b36: opcode-agnostic fail-closed gate, RE-ENABLED. The opcode only NOMINATES a candidate; the
                        // STRUCTURAL validator decides. A rotated opcode now carrying some other packet fails the shape test
                        // → suppressed, so the firewall model holds across a patch that rotates opcodes (like ValidateChat).
                        // The b34→b35 regression was the SPAWN validator's fragile name check (+0x262) false-rejecting real
                        // in-session spawns; that check is removed (see ValidateSpawnShape), so re-enabling the block is now
                        // measured-safe — the 3-check header signature passed on the real Hael Vera spawn.
                        bool shapeOk = isSpawn ? ValidateSpawnShape(a3) : ValidateDespawnShape(a3);
                        if (!shapeOk)
                        {
                            // A shape-fail is now visible via [SHAPE-SPAWN]/[SHAPE-DESPAWN] FAIL at Information (drift signal).
                            if (SendDiag)
                                log.Information("[HMSync] [ROSTER-PASS] " + (isSpawn ? "spawn" : "despawn") +
                                    " opcode=" + rop + " FAILED shape validation → SUPPRESS (fail-closed).");
                            return;   // opcode matched but not structurally a spawn/despawn → do NOT pass a misparse
                        }
                        uint id = isSpawn ? a2 : *(uint*)(a3 + 0x10 + 4);   // spawn key = a2; leaver key = payload+4
                        if (IsTrackableActorId(id))
                        {
                            receiveHook!.Original(a1, a2, a3);   // instantiate / remove the real actor (inbound only)
                            // b27/b28 validated: pass-through instantiates a real bindable actor mid-session (late-join
                            // works end-to-end). Kept at Debug now the machine is proven. eid is an ephemeral object id.
                            log.Debug("[HMSync] [ROSTER-PASS] " + (isSpawn ? "spawn" : "despawn") +
                                " opcode=" + rop + " eid=0x" + id.ToString("X8") + " (shape ok) → Original");
                            return;
                        }
                        return;   // opcode matched but id implausible → fail closed (do NOT pass a misparse)
                    }
                    // Stage 2 self-re-learn: this opcode is NOT a currently-known spawn/despawn. If the packet nonetheless
                    // carries the fixed-header spawn SIGNATURE, PlayerSpawn's opcode has rotated (patch) and the seeded set
                    // is stale. Confirm across a few sightings (guards a one-off misparse), then re-seed so walk-ins
                    // instantiate again. Read-only here (we do NOT pass this packet - it falls through to the suppress
                    // below); the NEXT spawn on the re-seeded opcode flows through the validated pass-through above. Cheap:
                    // 3 fixed reads, and only on opcodes we don't already recognise (known spawns/despawns short-circuit).
                    else if (MatchesSpawnSignature(a3))
                    {
                        int seen = spawnRelearnCandidates.TryGetValue(rop, out var n) ? n + 1 : 1;
                        spawnRelearnCandidates[rop] = seen;
                        if (seen >= RelearnConfirmThreshold)
                        {
                            string old = SpawnOpcodes.Count > 0 ? string.Join(",", SpawnOpcodes) : "∅";
                            SpawnOpcodes.Clear();            // PlayerSpawn is singular → replace the stale opcode wholesale
                            SpawnOpcodes.Add(rop);
                            spawnRelearnCandidates.Clear();
                            log.Warning("[HMSync] [RELEARN] PlayerSpawn opcode rotated: spawn-signature now on opcode " + rop +
                                " (0x" + rop.ToString("X3") + ") after " + RelearnConfirmThreshold + " sightings; was {" + old +
                                "}. Re-seeded → walk-in instantiation restored (patch-resilient, fail-closed unchanged).");
                            OnSpawnOpcodeRelearned?.Invoke(rop);
                        }
                    }
                    else
                    {
                        // Not spawn-shaped either → try the correlation-based DespawnCharacter re-learn (a2==local +
                        // known-leaver + anti-streaming). Still read-only; a confirmed hit re-seeds DespawnOpcodes.
                        TryRelearnDespawnOpcode(a2, a3, rop);
                    }
                }
                return;                             // filter active → suppress (the normal session behavior)
            }
            receiveHook!.Original(a1, a2, a3);       // capture-only → pass through
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] Receive hook error: " + ex.Message);
        }
    }

    private nint OnInteractWithObject(nint a1, nint a2, byte a3)
    {
        // Block all NPC/object interactions - prevents server-bound packets
        return 0;
    }

    public void Dispose()
    {
        Disable();
        sendHook?.Dispose();
        receiveHook?.Dispose();
        interactHook?.Dispose();
    }
}
