using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

// Relay reachability light. While the plugin window is open, poll the relay's unauthenticated /health endpoint:
// a 200 is green, anything else (timeout, connection error, TLS failure, non-200) is red. That is the whole
// contract - a reachability dot, deliberately NOT a liveness/RTT subsystem (an earlier version with WS Ping/Pong,
// amber transitional states, and backoff was cut as overbuilt). Nothing polls while the window is closed, since the
// light isn't visible then. Runs off the main thread; the UI reads one cached value.
public enum RelayLight { Grey, Green, Red }

// The relay-key verification result, shown as the status dot in the Relay config. The user pastes a key and confirms;
// we verify it by opening the SAME authenticated WebSocket the session uses. The relay validates the key BEFORE the
// protocol upgrade, so reaching the open state (HTTP 101) is the ONLY true "accepted" signal. States: Grey (NoKey) =
// nothing entered; Green (Accepted) = the handshake reached 101; Amber (Invalid) = it did NOT reach 101 - which is
// ONE failure class on purpose: a bad key (relay 401) and a down/unreachable relay are indistinguishable once
// Cloudflare rewrites the origin's non-101 to a 502 over the tunnel, so we must not claim "invalid key" specifically;
// Red (Unreachable) = the configured URL couldn't even be parsed to attempt a connection; Checking = probe in flight.
// (Was: an unauthenticated GET /health?k=<key> that returned 200 for ANY key, so the dot always lit green - the bug
// this replaces. RMS→HMS connection-state brief.)
public enum RelayKeyStatus { NoKey, Accepted, Invalid, Unreachable, Checking }

public class RelayHealthService : IDisposable
{
    // One long-lived client (the recommended pattern - never per-request), short timeout so a dead relay fails fast.
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly HMSyncConfig config;
    private readonly IPluginLog log;
    private readonly Func<bool> isWindowOpen;

    private volatile RelayLight light = RelayLight.Grey;
    private volatile RelayKeyStatus keyStatus = RelayKeyStatus.NoKey;
    private CancellationTokenSource? cts;

    public RelayLight Light => light;
    public RelayKeyStatus KeyStatus => keyStatus;

    // Called when the user confirms a key. Verify it by exercising the EXACT handshake the session uses: open a
    // WebSocket to the relay's real connect URL (config.RelayUrl - the key rides in ?k=, composed by
    // SyncSelectedRelayUrl, so we test byte-for-byte what Connect() will use). The relay validates the key BEFORE the
    // upgrade, so reaching the open state (HTTP 101) is the ONLY "accepted" signal - a bad key never gets there and
    // ConnectAsync throws. We fold EVERY non-101 (401 direct / 502 tunnelled / timeout / network) into one failure
    // class (Invalid): per the RMS→HMS brief, "bad key" and "relay down" are indistinguishable once Cloudflare
    // rewrites the origin's non-101 to a 502, so we must not assert "invalid key" specifically. The old path GET'd the
    // unauthenticated /health?k=<key>, which answered 200 for ANY key - the false "accepted" this fixes. Empty key →
    // NoKey (no probe). Runs off the main thread; the UI reads the cached KeyStatus.
    public async Task CheckKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { keyStatus = RelayKeyStatus.NoKey; return; }

        Uri? uri = null;
        try { var raw = config.RelayUrl; if (!string.IsNullOrWhiteSpace(raw)) uri = new Uri(raw); }
        catch { uri = null; }
        if (uri == null) { keyStatus = RelayKeyStatus.Unreachable; return; }   // malformed configured URL - can't even attempt

        keyStatus = RelayKeyStatus.Checking;
        ClientWebSocket? probe = null;
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            probe = new ClientWebSocket();
            await probe.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
            // Reached 101 → the relay accepted the key at its pre-upgrade gate. That's all the verification needs; we
            // never send a JoinRoom (this isn't a session), so close politely - timeboxed, then Abort as a fallback so
            // an un-acked close can't hang the probe (mirrors RelaySyncService.Disconnect's grace-then-force pattern).
            keyStatus = RelayKeyStatus.Accepted;
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await probe.CloseAsync(WebSocketCloseStatus.NormalClosure, "key probe", closeTimeout.Token).ConfigureAwait(false);
            }
            catch { try { probe.Abort(); } catch { /* already gone */ } }
        }
        catch
        {
            // Any non-101 (401 / 502 / timeout / network) - one failure class; we cannot tell bad-key from relay-down.
            keyStatus = RelayKeyStatus.Invalid;
            try { probe?.Abort(); } catch { /* already gone */ }
        }
        finally { try { probe?.Dispose(); } catch { /* already gone */ } }
    }

    public void ResetKeyStatus() => keyStatus = RelayKeyStatus.NoKey;

    public RelayHealthService(HMSyncConfig config, IPluginLog log, Func<bool> isWindowOpen)
    {
        this.config = config;
        this.log = log;
        this.isWindowOpen = isWindowOpen;
    }

    public void Start()
    {
        if (cts != null) return;
        cts = new CancellationTokenSource();
        _ = PollLoop(cts.Token);
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch { /* already gone */ }
        cts?.Dispose();
        cts = null;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int delayMs = 15000;
            try
            {
                if (!isWindowOpen())
                {
                    delayMs = 2000;   // window closed - don't poll; just re-check for it reopening
                }
                else
                {
                    var url = HealthUrl();
                    if (url == null)
                    {
                        light = RelayLight.Grey;   // nothing configured to probe
                    }
                    else
                    {
                        try
                        {
                            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                            light = resp.IsSuccessStatusCode ? RelayLight.Green : RelayLight.Red;   // 200 = up, else down
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { light = RelayLight.Red; }   // timeout / connection refused / TLS failure = unreachable
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.Warning("[HMSync] relay health poll error: " + ex.Message);
            }

            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Derive the https .../health probe URL from the configured relay (ws->http, wss->https), stripping any path and
    // query so no auth token ever lands in the URL. Returns null if the configured URL can't be parsed.
    private string? HealthUrl()
    {
        var raw = config.RelayUrl;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var u = new Uri(raw);
            var scheme = u.Scheme == "wss" ? "https" : (u.Scheme == "ws" ? "http" : u.Scheme);
            var port = (u.Port > 0 && !u.IsDefaultPort) ? ":" + u.Port : "";
            return scheme + "://" + u.Host + port + "/health";
        }
        catch { return null; }
    }
}
