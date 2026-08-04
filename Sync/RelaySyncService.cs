using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HMSync.Services;

namespace HMSync.Sync;

public class RelaySyncService : IDisposable
{
    private readonly IPluginLog log;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private Task? receiveTask;

    public string LocalPeerId { get; private set; } = Guid.NewGuid().ToString("N")[..12];
    // S331 (Stage 4): true once the relay's RoomJoined has arrived (it mints our peer id). The capture/send loop must
    // wait for this before emitting lane frames - the relay-minted id is our identity, and we need it to recognize our
    // own echoes. Reset on disconnect.
    public bool RoomJoinedAcknowledged { get; private set; }
    public string RoomId { get; private set; } = "";
    public bool IsConnected { get; private set; }
    // S328am: the URL we actually connected to (for the GUI to show the ACTIVE endpoint, not just the saved one).
    // Set on a successful connect, cleared on disconnect. Token is NOT stripped here - the UI redacts it for display.
    public string ConnectedUrl { get; private set; } = "";
    public bool IsHost { get; set; }

    // Opaque relay-generated RoomId, handed back in RoomJoined. Cached so a dropped host/peer can reconnect to the
    // SAME room (Reconnect mode) rather than re-resolving by nearby players (who aren't visible from the synthetic zone).
    public string CachedRoomId { get; private set; } = "";

    // Relay-authoritative room capacity (ROOM_CAP), handed to us on RoomJoined. 0 = unlimited / don't show. Never
    // hardcoded - the relay may lower it after perf testing with just a restart, so we only ever read it from here.
    public int RoomCap { get; private set; }

    // The room password for the current session - retained so the UI can show it as the shareable key (auto-generated
    // on Host if the user left the field blank). In-memory only; cleared on disconnect.
    public string CurrentPassword { get; private set; } = "";

    // S328f - SOLO MODE. When true, the plugin runs the full map-authoring feature set (zone load, time/weather/BGM,
    // NPC, cosmetics, movement, packet filter) with NO relay connection and no peers - the same client-side loop
    // Hyperborea does. The relay exists ONLY to sync peers; everything else is client-side and relay-independent.
    // These two accessors are the single point solo flows through, so solo is one flag rather than scattered checks:
    //   • HasMapAuthority - gates MAP actions (weather/time/BGM/NPC/load/reassert). True for the host OR in solo.
    //   • IsSessionActive - gates feature-availability / packet-filter / "in a session". True when connected OR in solo.
    // Peer-only operations (summon/kick/password/lock/transfer host) stay on the literal IsHost - solo can't do them.
    public bool SoloMode { get; set; }
    public bool HasMapAuthority => IsHost || SoloMode;
    public bool IsSessionActive => IsConnected || SoloMode;

    public event Action<string, ulong, string>? OnPeerJoined;
    public event Action<string>? OnPeerLeft;
    public event Action<string>? OnHostTransfer;
    public event Action<string, TransformData, bool>? OnTransformReceived;   // (peerId, snapshot, isHotLane) - v0.7.461: lane bool for the HOT-only Seq gate

    // ── Per-subject composite cache (Stage 2a) ── each lane message updates its slice of the subject's composite
    // TransformData; the merged whole feeds OnTransformReceived. Keyed by SUBJECT entity id (payload `sid`), not
    // sender - the entity-addressing seam. This preserves the burst-coalescing property: one composite per subject,
    // each lane updates its own fields, the apply reads the merged whole. Evicted on peer-leave/despawn.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TransformData> laneComposites = new();

    private TransformData CompositeFor(string subjectId) =>
        laneComposites.GetOrAdd(subjectId, _ => new TransformData());

    /// <summary>Drop a subject's composite (peer left / entity despawned). Called from the peer-leave path.</summary>
    public void EvictComposite(string subjectId) => laneComposites.TryRemove(subjectId, out _);

    // A distinct COPY of the composite for handing to the apply/interpolation path. The composite is a single mutating
    // instance per subject (so lanes merge into one place); but interpolation stores references, so it needs its own
    // frozen snapshot each message or all history entries alias to the latest value (flip-book). JSON round-trip
    // copies every field and can't silently miss one (same rationale as RenderEquals/CloneTransform).
    private static TransformData SnapshotComposite(TransformData c) =>
        JsonSerializer.Deserialize<TransformData>(JsonSerializer.Serialize(c))!;

    // ── S331 (Stage 4): wiredump - the required binary-debuggability tool. Captures the next N frames (sent and/or
    // received) and pretty-prints kind + decoded msgpack payload as readable text. Turns the binary wire back into
    // something eyeball-able on demand. Armed via /hms wiredump.
    public bool WireDumpActive { get; private set; }
    private int wireDumpRemaining;
    private readonly System.Collections.Generic.List<string> wireDumpLines = new();
    public System.Action<string>? WireDumpEmit;   // where to print (chat), set by the plugin

    public void ArmWireDump(int frames)
    {
        wireDumpLines.Clear();
        wireDumpRemaining = frames;
        WireDumpActive = true;
        WireDumpEmit?.Invoke("[HMSync] wiredump armed - capturing next " + frames + " frames (sent+received).");
    }

    private void WireDumpCapture(bool sent, byte kind, byte[] payload, int frameLen)
    {
        if (!WireDumpActive) return;
        string dir = sent ? "SEND" : "RECV";
        string decoded = WireDumpDecoder.DecodePayload(kind, payload);
        wireDumpLines.Add(dir + " " + KindName(kind) + " (" + frameLen + " B): " + decoded);
        if (--wireDumpRemaining <= 0)
        {
            WireDumpActive = false;
            WireDumpEmit?.Invoke("[HMSync] wiredump (" + wireDumpLines.Count + " frames):");
            foreach (var line in wireDumpLines) WireDumpEmit?.Invoke("  " + line);
        }
    }
    public event Action<ZoneLoadData>? OnZoneLoadReceived;
    public event Action<RoomJoinedData>? OnRoomJoined;
    public event Action? OnSessionEnded;
    public event Action<uint, string>? OnError;
    public event Action? OnDisconnected;

    // v0.7.464 (RMS QA F3, soft tier): the relay's SOFT ingress-throttle notice (WireKind.RateLimited 0x08).
    // Non-fatal - the socket stays open and the session continues; excess ingress is simply not fanned out.
    // Deliberately NOT an ErrorPayload code: see the note on WireKind.RateLimited for why an unknown code is
    // fatal on an older client and an unknown kind is inert.
    public event Action? OnRateLimited;

    // v0.7.464 (RMS QA F3, hard tier): the close status of the LAST closed connection, so the disconnect handler
    // can say WHY instead of the generic "lost connection". Null when the socket dropped without a close frame
    // (abort/timeout/network) - which is exactly the case we cannot diagnose, and must not guess at.
    public int? LastCloseCode { get; private set; }
    public string LastCloseReason { get; private set; } = "";

    public RelaySyncService(IPluginLog log)
    {
        this.log = log;
    }

    // S328ag: bandwidth instrumentation. Set by the plugin; the two byte choke points (Send / receive loop) feed it.
    // Null-safe so the relay works without it. Change-detection is always on (per-lane, in SendTransformAsLanes).
    public NetStatsService? NetStats { get; set; }

    public async Task<bool> Connect(string relayUrl, string roomId, bool? createIfMissing, string? password, ulong[]? nearbyContentIds, ulong contentId, uint entityId, string charName)
    {
        // Force-kill any existing connection state synchronously
        // Don't try graceful close - just nuke it
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        cts = null;

        try { ws?.Abort(); } catch { } // Abort is synchronous, unlike CloseAsync
        try { ws?.Dispose(); } catch { }
        ws = null;

        IsConnected = false;
        IsHost = false;

        // Small delay to let any background receive loops exit
        await Task.Delay(100);

        RoomId = roomId;
        CurrentPassword = password ?? "";
        LastCloseCode = null;                // v0.7.464: fresh connection - a stale 4029 must not colour the next drop
        LastCloseReason = "";
        IsHost = createIfMissing == true;   // optimistic; RoomJoined carries the relay's authoritative value
        cts = new CancellationTokenSource();

        try
        {
            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            await ws.ConnectAsync(new Uri(relayUrl), cts.Token);
            IsConnected = true;
            ConnectedUrl = relayUrl;   // S328am: record the active endpoint for the GUI
            log.Information("[HMSync] Connected to relay");

            var joinWire = new HMSync.Wire.JoinPayload
            {
                RoomId = roomId,               // empty on host/join; the cached opaque id on reconnect
                ContentId = contentId,
                EntityId = entityId,
                CharacterName = charName,
                RoomPassword = password,        // the user's password - relay compares (constant-time)
                CreateIfMissing = createIfMissing,       // true=Host, false=Join/Reconnect, null=legacy (never null here)
                NearbyContentIds = nearbyContentIds,     // Join only - the ContentIds we can see; relay resolves the room
            };
            await SendFrame(HMSync.Wire.WireKind.JoinRoom,
                MessagePack.MessagePackSerializer.Serialize(joinWire, HMSync.Wire.WireFormat.Options));

            receiveTask = Task.Run(() => ReceiveLoop(cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            // Keep the raw handshake error in the LOG for diagnosis (e.g. "status code '502' when '101' was expected"),
            // but hand the USER one honest message: a non-101 is bad key OR relay-down, indistinguishable once
            // Cloudflare rewrites the origin's 401 to a 502 over the tunnel, so we don't claim "invalid key". (RMS→HMS.)
            log.Error("[HMSync] Connection failed: " + ex.Message);
            IsConnected = false;
            OnError?.Invoke(0u, "Couldn't connect. Check your key, or the server may be down.");   // 0 = generic (client-side connect failure)
            return false;
        }
    }

    public async Task Disconnect()
    {
        if (!IsConnected && ws == null) return;

        // Flip connection state OFF synchronously, FIRST - before any await. This is what the GUI and the
        // /hms stop guards read, so it must change the instant we decide to disconnect, not after the
        // (possibly slow/hanging) graceful close. The old code set it only AFTER awaiting CloseAsync, so a
        // server-half-closed socket faulted the fire-and-forget Task before the flag flipped → session
        // looked connected until a second /hms stop. Local socket cleanup still happens in finally.
        IsConnected = false;
        IsHost = false;
        laneComposites.Clear();   // S330c (2b): drop all per-subject composites - a fresh session/reconnect starts clean
        RoomJoinedAcknowledged = false;   // S331: re-arm the join gate for the next connection
        var closing = ws;

        try
        {
            // Best-effort graceful close, TIMEBOXED to 1s. Optional - teardown does not depend on it.
            if (closing?.State == WebSocketState.Open)
            {
                using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    // Tell the server we're leaving (matters for the /hms leave peer path, which sends no
                    // SessionEnd). Best-effort - SendFrame targets the live ws (still set until finally) and
                    // swallows its own errors; if the socket's already closing, CloseAsync below throws and
                    // we force teardown.
                    await SendFrame(HMSync.Wire.WireKind.LeaveRoom, System.Array.Empty<byte>());
                    await closing.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", graceCts.Token);
                }
                catch (Exception ex) { log.Debug("[HMSync] Graceful close skipped (" + ex.Message + ") - forcing teardown."); }
            }
        }
        finally
        {
            // ALWAYS run local teardown - never gated behind network I/O.
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
            cts = null;

            try { closing?.Abort(); } catch { }   // synchronous, unblocks anything pending
            try { closing?.Dispose(); } catch { }
            if (ReferenceEquals(ws, closing)) ws = null;
            RoomId = "";
            CurrentPassword = "";
            log.Information("[HMSync] Disconnected");
        }
    }

    // S331 (Stage 4 / Stage 3 retirement): SendTransform (the monolithic TransformUpdate 0x10) is REMOVED. The whole-
    // struct JSON send is gone - the binary lanes (SendTransformAsLanes) are the only wire format now. This is the
    // point where the monolith is retired: there is no method here that sends the entire struct as one message.

    // ── LANE SENDER (Stage 2a, S330a) ── emit ONLY the lanes whose fields changed since the last send. This IS the
    // "no monolithic send path" property: there is no method here that sends the whole struct as lanes - each lane is
    // an independent message with only its fields. A walking-not-emoting peer sends HOT only; an idle peer sends
    // nothing (until the keepalive forces a HOT). Envelope SenderId = LocalPeerId ALWAYS (relay sender-exclusion +
    // spoof-guard); the subject entity id rides IN the payload (== LocalPeerId today; an NPC's id tomorrow).
    private TransformData? lastSentLanes;   // last transform we emitted lanes for (per-lane diff basis)
    private uint hotSeq;

    /// <summary>
    /// Emit changed lanes. Two force flags with distinct meaning:
    ///   <paramref name="forceHot"/> - send HOT even if unchanged (the keepalive/liveness heartbeat; HOT carries the
    ///   version and is the "still here" signal). True on keepalive, first-send, and dirty-check-off.
    ///   <paramref name="forceAllLanes"/> - send EVERY lane even if unchanged (a joiner needs the complete initial
    ///   picture: position + appearance + map-state + emote). True on first-send and dirty-check-off ONLY - NOT on a
    ///   bare keepalive, because resending unchanged COLD/HOST/WARM on every keepalive is redundant idle traffic.
    /// </summary>
    public async Task SendTransformAsLanes(TransformData t, bool isHost, bool forceHot, bool forceAllLanes, bool forceHostOnce, float posEps, float rotEps)
    {
        if (!IsConnected) return;
        var prev = lastSentLanes;
        // S331 (Stage 4): subjectId = "" - for a player puppet the subject IS the relay-stamped sender, so we send the
        // 1-byte empty sentinel instead of the full id (spec §4). An NPC/entity later would put its explicit id here.
        const string subject = "";

        // HOT - position/movement. Sent on change OR on the keepalive/liveness heartbeat.
        if (forceHot || prev == null || !LaneProjection.HotEquals(t, prev, posEps, rotEps))
        {
            hotSeq++;
            var hot = LaneProjection.ToHotWire(t, subject, hotSeq);
            await SendFrame(HMSync.Wire.WireKind.HotUpdate,
                MessagePack.MessagePackSerializer.Serialize(hot, HMSync.Wire.WireFormat.Options));
        }

        // WARM - emote/mount/minion/ornament/etc. STRICTLY change-gated (forceAllLanes only, never keepalive).
        if (forceAllLanes || prev == null || !LaneProjection.WarmEquals(t, prev))
        {
            var warm = LaneProjection.ToWarmWire(t, subject);
            await SendFrame(HMSync.Wire.WireKind.WarmUpdate,
                MessagePack.MessagePackSerializer.Serialize(warm, HMSync.Wire.WireFormat.Options));
        }

        // COLD - Moniker/cosmetic toggles. STRICTLY change-gated (forceAllLanes only, never keepalive).
        if (forceAllLanes || prev == null || !LaneProjection.ColdEquals(t, prev))
        {
            var cold = LaneProjection.ToColdWire(t, subject);
            await SendFrame(HMSync.Wire.WireKind.ColdUpdate,
                MessagePack.MessagePackSerializer.Serialize(cold, HMSync.Wire.WireFormat.Options));
        }

        // HOST - map-state block. Only the host emits it. STRICTLY change-gated (forceAllLanes only, never keepalive) -
        // EXCEPT forceHostOnce, the late-join re-broadcast: the host re-sends current map-state once when a peer joins so
        // the newcomer (who missed the original change) gets weather/time/BGM. Existing peers skip it via the epoch gate.
        if (isHost && (forceAllLanes || forceHostOnce || prev == null || !LaneProjection.HostEquals(t, prev)))
        {
            var host = LaneProjection.ToHostWire(t, subject);
            await SendFrame(HMSync.Wire.WireKind.HostUpdate,
                MessagePack.MessagePackSerializer.Serialize(host, HMSync.Wire.WireFormat.Options));
        }

        // Retain a COPY for the next per-lane diff. (Clone so later mutation of t doesn't corrupt the diff basis.)
        lastSentLanes = CloneTransform(t);
    }

    private static TransformData CloneTransform(TransformData t)
    {
        // Shallow value-copy is enough - all fields are value types + strings (immutable). JSON round-trip avoids
        // hand-listing 54 fields and can't silently miss one (same lesson as RenderEquals).
        return JsonSerializer.Deserialize<TransformData>(JsonSerializer.Serialize(t))!;
    }

    public async Task SendZoneLoad(ZoneLoadData zoneData)
    {
        if (!IsConnected || !IsHost) return;

        // S331 (Stage 4): ZoneLoadData isn't ported to a wire POCO yet (spec §4.5 - it rides its own path). Carry its
        // JSON as the payload bytes for now; the relay treats the payload as opaque either way, so this is transparent
        // to it. Port to a msgpack ZoneLoadPayload later if zone-load traffic ever matters for bytes (it's rare).
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(zoneData));
        await SendFrame(HMSync.Wire.WireKind.ZoneLoadExecute, jsonBytes);
    }

    public async Task SendSessionEnd()
    {
        if (!IsConnected || !IsHost) return;
        await SendFrame(HMSync.Wire.WireKind.SessionEnd, System.Array.Empty<byte>());
    }

    // Host ejects + bans a peer (relay removes, bans the ContentId for the room's life, hard-closes their socket,
    // broadcasts PeerLeft; the kicked client gets Error code 5).
    public async Task SendKick(string targetPeerId)
    {
        if (!IsConnected || !IsHost || string.IsNullOrEmpty(targetPeerId)) return;
        var payload = MessagePack.MessagePackSerializer.Serialize(
            new HMSync.Wire.KickPeerPayload { TargetPeerId = targetPeerId }, HMSync.Wire.WireFormat.Options);
        await SendFrame(HMSync.Wire.WireKind.KickPeer, payload);
    }

    // Host hands the role to a specific peer (relay reassigns host + broadcasts HostTransfer; we drop IsHost when the
    // broadcast comes back). Distinct from leave-driven auto-succession - this is an explicit pick.
    public async Task SendHostTransfer(string targetPeerId)
    {
        if (!IsConnected || !IsHost || string.IsNullOrEmpty(targetPeerId)) return;
        var payload = MessagePack.MessagePackSerializer.Serialize(
            new HMSync.Wire.HostTransferPayload { TargetPeerId = targetPeerId }, HMSync.Wire.WireFormat.Options);
        await SendFrame(HMSync.Wire.WireKind.HostTransfer, payload);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        // S328ak: reassemble multi-frame messages. A WebSocket message may arrive as several frames (EndOfMessage
        // false until the last), regardless of size - the old code assumed one frame == one message and used
        // result.Count directly, so a roster/burst/large payload would truncate and silently fail to parse (the
        // catch{continue} swallowed it). Accumulate until EndOfMessage, with a cap. Relay Claude fixed the mirror
        // bug server-side; this is the client half. 64KB cap matches the relay's.
        const int MaxMessageBytes = 64 * 1024;
        using var assembly = new System.IO.MemoryStream();

        while (!ct.IsCancellationRequested && ws?.State == WebSocketState.Open)
        {
            try
            {
                assembly.SetLength(0);
                System.Net.WebSockets.WebSocketReceiveResult result;
                bool tooLarge = false;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // v0.7.464: record WHY the server closed before we unwind. Application close codes live in
                        // 4000–4999 (RFC 6455 §7.4.2) and .NET surfaces them cast into WebSocketCloseStatus, so an
                        // int cast is the honest read. Only a clean close frame carries this - an aborted socket
                        // throws WebSocketException instead and leaves LastCloseCode null (correctly undiagnosed).
                        var cs = result.CloseStatus ?? ws?.CloseStatus;
                        LastCloseCode = cs.HasValue ? (int?)cs.Value : (int?)null;
                        LastCloseReason = result.CloseStatusDescription ?? ws?.CloseStatusDescription ?? "";
                        log.Information("[HMSync] Relay closed the connection (code=" +
                            (LastCloseCode?.ToString() ?? "none") + " reason=\"" + LastCloseReason + "\")");
                        break;
                    }
                    if (assembly.Length + result.Count > MaxMessageBytes)
                    {
                        // Oversized message - drain the rest of it and drop, rather than corrupt the stream.
                        tooLarge = true;
                        continue;
                    }
                    assembly.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (tooLarge)
                {
                    log.Warning("[HMSync] Dropped oversized message (> " + (MaxMessageBytes / 1024) + "KB).");
                    continue;
                }

                var count = (int)assembly.Length;
                NetStats?.RecordIn(count);   // S328ag: wire bytes actually received (now the full reassembled frame)

                // S331 (Stage 4): parse the BINARY downleg frame (spec §1b) - [magic][kind][flags][senderLen][senderId]
                // [timestamp][msgpack payload]. The senderId is RELAY-STAMPED (trusted); the client never asserted it.
                var frameBytes = new byte[count];
                System.Buffer.BlockCopy(assembly.GetBuffer(), 0, frameBytes, 0, count);
                var parsed = HMSync.Wire.FrameHeader.ParseDownleg(frameBytes);
                if (!parsed.Ok) { log.Warning("[HMSync] Dropped bad frame (magic/length)."); continue; }

                if (WireDumpActive) WireDumpCapture(false, parsed.Kind, parsed.Payload, count);

                // S328al: drop our OWN state broadcasts (we don't render our own puppet). EXCEPTION: HostTransfer is a
                // control message ABOUT us - the relay stamps it with senderId = the NEW host (i.e. us, when promoted),
                // so a blanket self-drop meant the new host never learned it was promoted. Let HostTransfer through even
                // when the stamped sender is our own id.
                if (parsed.SenderId == LocalPeerId && parsed.Kind != HMSync.Wire.WireKind.HostTransfer) continue;

                HandleFrame(parsed.Kind, parsed.SenderId, parsed.Payload);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException)
            {
                log.Warning("[HMSync] WebSocket connection lost");
                break;
            }
            catch (Exception ex)
            {
                log.Error("[HMSync] Receive error: " + ex.Message);
                await Task.Delay(100, ct);
            }
        }

        // If we exit the loop unexpectedly, notify
        if (IsConnected)
        {
            IsConnected = false;
            laneComposites.Clear();   // S330c (2b): unexpected drop - clear composites so a reconnect starts clean
            RoomJoinedAcknowledged = false;   // S331: re-arm the join gate
            OnDisconnected?.Invoke();
        }
    }

    // S331 (Stage 4): binary frame dispatch. kind + relay-stamped senderId + opaque msgpack payload. Replaces the JSON
    // HandleMessage. Lane payloads decode to HMSync.Wire types → merge into the per-subject composite → snapshot → apply.
    // The subjectId sentinel (spec §4): payload SubjectId == "" means "subject is the stamped sender" (player puppet) -
    // resolve it to senderId. A non-empty SubjectId is an explicit entity id (NPC, later).
    private void HandleFrame(byte kind, string senderId, byte[] payload)
    {
        // Resolve the subject id from a lane payload's SubjectId field: "" → the relay-stamped sender.
        string ResolveSubject(string sid) => string.IsNullOrEmpty(sid) ? senderId : sid;

        switch (kind)
        {
            case HMSync.Wire.WireKind.RoomJoined:
            {
                var data = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.RoomJoinedPayload>(payload, HMSync.Wire.WireFormat.Options);
                // The relay MINTS our peer id - adopt it as our LocalPeerId (S331: relay-authoritative identity).
                if (!string.IsNullOrEmpty(data.AssignedPeerId)) LocalPeerId = data.AssignedPeerId;
                IsHost = data.IsHost;
                if (!string.IsNullOrEmpty(data.RoomId)) CachedRoomId = data.RoomId;   // opaque id → cache for reconnect
                RoomCap = data.RoomCap;   // relay-authoritative capacity (0 = unlimited/unshown)
                RoomJoinedAcknowledged = true;   // gate: the capture/send loop may start now (we have our id)
                // Bridge to the existing OnRoomJoined path. The v4 RoomJoined payload carries id+host; the richer
                // zone/spawn fields the old JSON relay sent aren't in v4 (map-state now arrives via the HOST lane +
                // the late-join re-broadcast, not baked into RoomJoined). Invoke with a minimal RoomJoinedData so the
                // existing handler runs; CurrentZoneId=0 → no positional auto-load (correct - HOST lane drives it).
                OnRoomJoined?.Invoke(new RoomJoinedData { PeerIds = System.Array.Empty<string>() });
                break;
            }

            case HMSync.Wire.WireKind.PeerJoined:
            {
                string joiner = senderId;
                ulong joinerContentId = 0;
                string joinerName = "";
                try
                {
                    var p = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.PeerJoinedPayload>(payload, HMSync.Wire.WireFormat.Options);
                    if (!string.IsNullOrEmpty(p.PeerId)) joiner = p.PeerId;
                    joinerContentId = p.ContentId;      // relay-stamped identity (0 from a pre-Part-2 relay)
                    joinerName = p.CharacterName;
                }
                catch { /* empty/malformed payload → the header sender is the joiner, identity stays blank */ }
                OnPeerJoined?.Invoke(joiner, joinerContentId, joinerName);
                break;
            }

            case HMSync.Wire.WireKind.PeerLeft:
            {
                string leaver = senderId;
                string newHost = "";
                try
                {
                    var p = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.PeerLeftPayload>(payload, HMSync.Wire.WireFormat.Options);
                    if (!string.IsNullOrEmpty(p.PeerId)) leaver = p.PeerId;
                    newHost = p.NewHostId;
                }
                catch { /* empty/malformed payload → the header sender is the leaver, no succession info */ }
                OnPeerLeft?.Invoke(leaver);
                EvictComposite(leaver);   // Stage 2a: drop the leaver's lane composite
                if (!string.IsNullOrEmpty(newHost))
                {
                    log.Information("[HMSync] Host transfer: new host = " + newHost[..Math.Min(6, newHost.Length)]);
                    OnHostTransfer?.Invoke(newHost);
                }
                break;
            }

            case HMSync.Wire.WireKind.HostTransfer:
            {
                // The relay stamps senderId = the new host's id in the HEADER (spec §3.1 exception) - that's the
                // authoritative source. The payload MAY also carry it, but we don't require it: read the header sender
                // first, and only fall back to the payload if the header is somehow empty. This is robust to an empty
                // HostTransfer payload (the relay may send just the header stamp) - deserializing empty bytes would throw.
                string newHost = senderId;
                if (string.IsNullOrEmpty(newHost) && payload.Length > 0)
                {
                    try { newHost = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.HostTransferPayload>(payload, HMSync.Wire.WireFormat.Options).TargetPeerId; }
                    catch { /* header was the source of truth anyway */ }
                }
                if (!string.IsNullOrEmpty(newHost))
                {
                    log.Information("[HMSync] Received HostTransfer: new host = " + newHost[..Math.Min(6, newHost.Length)]);
                    OnHostTransfer?.Invoke(newHost);
                }
                break;
            }

            // ── LANE RECEIVERS (Stage 4 binary) ── decode → merge into per-subject composite → snapshot → apply.
            case HMSync.Wire.WireKind.HotUpdate:
            {
                var h = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.HotPayload>(payload, HMSync.Wire.WireFormat.Options);
                string subj = ResolveSubject(h.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeHotWire(comp, h);
                // Hand a SNAPSHOT COPY to the apply path - interpolation stores references, so the shared mutating
                // composite would alias every snapshot to the latest position → flip-book. A copy per message gives
                // interpolation distinct points to glide between.
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ true);
                break;
            }
            case HMSync.Wire.WireKind.WarmUpdate:
            {
                var w = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.WarmPayload>(payload, HMSync.Wire.WireFormat.Options);
                string subj = ResolveSubject(w.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeWarmWire(comp, w);
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ false);
                break;
            }
            case HMSync.Wire.WireKind.ColdUpdate:
            {
                var d = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.ColdPayload>(payload, HMSync.Wire.WireFormat.Options);
                string subj = ResolveSubject(d.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeColdWire(comp, d);
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ false);
                break;
            }
            case HMSync.Wire.WireKind.HostUpdate:
            {
                var hd = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.HostPayload>(payload, HMSync.Wire.WireFormat.Options);
                string subj = ResolveSubject(hd.SubjectId);
                var comp = CompositeFor(subj);
                LaneProjection.MergeHostWire(comp, hd);
                OnTransformReceived?.Invoke(subj, SnapshotComposite(comp), /*isHotLane:*/ false);
                break;
            }

            case HMSync.Wire.WireKind.ZoneLoadExecute:
            {
                // ZoneLoad carries its JSON as payload bytes (not yet ported to a wire POCO - see SendZoneLoad).
                var json = Encoding.UTF8.GetString(payload);
                var zd = JsonSerializer.Deserialize<ZoneLoadData>(json);
                if (zd != null) OnZoneLoadReceived?.Invoke(zd);
                break;
            }

            case HMSync.Wire.WireKind.SessionEnd:
                OnSessionEnded?.Invoke();
                break;

            case HMSync.Wire.WireKind.Pong:
                // NB-27: Pong is a recognized-and-swallowed keepalive. The old lastPongReceived timestamp field was
                // set here but never read anywhere (no timeout/health check consumed it) - dead state, removed. Keep
                // the case so an inbound Pong is still consumed cleanly rather than falling through to the default.
                break;

            // v0.7.464 (RMS QA F3, soft tier): the relay is dropping some of our ingress but keeping us connected.
            // Payload is empty by contract - do NOT deserialize it (a future version may append fields; parsing an
            // empty buffer today would throw and turn an advisory into a log error). Advisory only: no teardown.
            case HMSync.Wire.WireKind.RateLimited:
                OnRateLimited?.Invoke();
                break;

            case HMSync.Wire.WireKind.Error:
            {
                var e = MessagePack.MessagePackSerializer.Deserialize<HMSync.Wire.ErrorPayload>(payload, HMSync.Wire.WireFormat.Options);
                if (e.Code == 1) CachedRoomId = "";   // RoomNotFound → the cached opaque id is stale, drop it
                OnError?.Invoke(e.Code, e.Message);
                break;
            }
        }
    }

    // ── S331 (Stage 4): binary frame send. Builds an UPLEG frame [magic][kind][flags][timestamp][payload] and
    // sends it as a WS BINARY message. The payload is already-serialized msgpack bytes. No sender/room - the relay
    // stamps the trusted sender and knows the room from the connection (spec §1a). This replaces the JSON Send path.
    private async Task SendFrame(byte kind, byte[] msgpackPayload)
    {
        if (ws?.State != WebSocketState.Open) return;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = HMSync.Wire.FrameHeader.BuildUpleg(kind, ts, msgpackPayload);
        NetStats?.RecordOut(frame.Length, KindName(kind));   // upleg bytes (the minimality figure netdiag/dashboard track)
        if (WireDumpActive) WireDumpCapture(true, kind, msgpackPayload, frame.Length);
        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] SendFrame error: " + ex.Message);
        }
    }

    // Kind byte → name, for metering + wiredump (mirrors the relay's MsgType.Name).
    private static string KindName(byte kind) => kind switch
    {
        HMSync.Wire.WireKind.JoinRoom => "JoinRoom",
        HMSync.Wire.WireKind.LeaveRoom => "LeaveRoom",
        HMSync.Wire.WireKind.RoomJoined => "RoomJoined",
        HMSync.Wire.WireKind.PeerJoined => "PeerJoined",
        HMSync.Wire.WireKind.PeerLeft => "PeerLeft",
        HMSync.Wire.WireKind.HostTransfer => "HostTransfer",
        HMSync.Wire.WireKind.RateLimited => "RateLimited",
        HMSync.Wire.WireKind.HotUpdate => "HotUpdate",
        HMSync.Wire.WireKind.WarmUpdate => "WarmUpdate",
        HMSync.Wire.WireKind.ColdUpdate => "ColdUpdate",
        HMSync.Wire.WireKind.HostUpdate => "HostUpdate",
        HMSync.Wire.WireKind.EventPulse => "EventPulse",
        HMSync.Wire.WireKind.ZoneLoadExecute => "ZoneLoadExecute",
        HMSync.Wire.WireKind.SessionEnd => "SessionEnd",
        HMSync.Wire.WireKind.Ping => "Ping",
        HMSync.Wire.WireKind.Pong => "Pong",
        HMSync.Wire.WireKind.Error => "Error",
        _ => "0x" + kind.ToString("X2"),
    };

    public void Dispose()
    {
        _ = Disconnect();
        cts?.Dispose();
    }
}
