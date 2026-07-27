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
    // this to fail LOUDLY (refuse + warn) instead of silently dropping the connection — the one heartbeat-sig-break
    // failure mode made diagnosable. (See FFXIV-Network-Opcodes-Security.md §14.)
    public bool HeartbeatResolved => heartbeatOpcode != 0;

    // v0.7.461 (P1, Codex QA): the firewall may only be enabled if EVERY critical hook actually created AND the
    // heartbeat resolved. Enable() is null-safe (sendHook?.Enable()), so if a game patch broke ONLY the send-packet
    // signature, the old code would still set IsActive=true while outbound packets flowed UNFILTERED to the live
    // server — the exact exposure the firewall exists to prevent. sendHook is the load-bearing one (outbound
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

        // InteractWithObject — prevents NPC interactions from generating packets
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
        // v0.7.461: never enable with a missing critical hook — IsActive must imply the firewall is actually
        // intercepting. The caller (EngageSyntheticSession) checks CanEnable and aborts the load with a warning;
        // this is the defense-in-depth backstop so IsActive can't be set true through any other path either.
        if (!CanEnable)
        {
            log.Error("[HMSync] Packet filter NOT enabled — a critical hook is missing (signature break). Refusing to set active.");
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
                                log.Information("[HMSync] [SAY-FINDER] OUTBOUND MATCH — opcode=" + opcode + " (0x" + opcode.ToString("X3") +
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
            // reveals which outbound opcode /say produces (ChatHandler?) and confirms it's being dropped in-session —
            // the likely reason /say doesn't reach peers (the SENDER's outbound chat is killed before it leaves).
            if (SendDiag)
            {
                bool willPass = !IsActive
                    || opcode == heartbeatOpcode
                    || (PassSayChatOut && SayChatOutOpcodes.Contains(opcode));
                log.Information("[HMSync] [SEND-DIAG] outbound opcode=" + opcode + " (0x" + opcode.ToString("X3") + ") → " +
                    (willPass ? "PASS" : "SUPPRESS") + (IsActive ? "" : " [capture-only]"));
            }

            // ⚠ v0.7.418 — SAFETY GATE, and it is not optional.
            // This method ends in `return 1` (suppress). It was previously unreachable outside a
            // session because the send hook only existed while the filter was active. Capture-only
            // now installs it WITHOUT the filter, so without this line every outbound packet the
            // client produced would be swallowed — movement, actions, chat, everything — and the
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
    // the real layout — channel/type byte, sender id, message string offset — to design the structural validator on
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
    // content correlation (S328i). Yell/shout added once confirmed (they may share 695 — a chat-type byte in-payload —
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

    // Structural validator (S328p) — the SAFETY CORE. Confirms a packet on the say opcode really IS spatial chat, by
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
            if (chatType != 10 && chatType != 11 && chatType != 30) return false;   // not say/shout/yell
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
        // !IsActive — see the safety gate there. This is the window the exit-freeze investigation
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
            // (695) isn't arriving we can see what DOES — distinguishes "server didn't send it" (proximity broke) from
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

            // Say-opcode finder: scan the payload for the target string (set via SayFinderText). A hit means THIS packet
            // carries the /say text → log its opcode. Content correlation — definitive, no timestamp matching.
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
                            log.Information("[HMSync] [SAY-FINDER] MATCH — opcode=" + fop + " (0x" + fop.ToString("X3") +
                                ") carries '" + SayFinderText + "' at payload offset 0x" + matchAt.ToString("X") +
                                ". THIS is the /say inbound opcode.");
                            SayFinderText = null;   // one-shot: clear after the first hit
                            OnSayOpcodeFound?.Invoke(fop);   // re-learn: let the plugin update + verify config
                        }
                    }
                }
                catch { /* payload shorter than scan window — ignore */ }
            }

            if (CaptureInbound)
            {
                // IPC segment header (FFXIVClientStructs ServerIpcSegmentHeader, size 0x10):
                //   +0x02 OpCode · +0x0C Timestamp · +0x10 payload start.
                ushort opcode = *(ushort*)(a3 + 0x02);
                if (CaptureOpcodes == null || CaptureOpcodes.Count == 0 || CaptureOpcodes.Contains(opcode))
                {
                    int ts = *(int*)(a3 + 0x0C);
                    // The target entity id is the SECOND hook parameter (a2 / targetId) — this is exactly how Dalamud's
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

            // Drop inbound ONLY when the filter is genuinely active (in a session). If we're merely CAPTURING outside a
            // session, PASS the packet through — otherwise capturing in an inn would break your inbound traffic.
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
                        // On the configured opcode but NOT chat-shaped: candidate drift. Count consecutive failures;
                        // enough of them means the opcode rotated to something else → shut the passthrough + notify.
                        if (!DriftDetected && ++consecutiveChatShapeFailures >= DriftFailureThreshold)
                        {
                            DriftDetected = true;
                            log.Warning("[HMSync] [DRIFT] opcode " + op + " (0x" + op.ToString("X3") + ") stopped carrying chat-shaped packets after " +
                                DriftFailureThreshold + " tries — likely rotated by a patch. Shutting say passthrough (fail-closed).");
                            OnDriftDetected?.Invoke();
                        }
                        return;   // not chat-shaped → suppress (fail closed), do NOT pass
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
        // Block all NPC/object interactions — prevents server-bound packets
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
