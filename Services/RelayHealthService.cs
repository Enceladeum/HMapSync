using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

// Relay reachability light. While the plugin window is open, poll the relay's unauthenticated /health endpoint:
// a 200 is green, anything else (timeout, connection error, TLS failure, non-200) is red. That is the whole
// contract — a reachability dot, deliberately NOT a liveness/RTT subsystem (an earlier version with WS Ping/Pong,
// amber transitional states, and backoff was cut as overbuilt). Nothing polls while the window is closed, since the
// light isn't visible then. Runs off the main thread; the UI reads one cached value.
public enum RelayLight { Grey, Green, Red }

// The relay-key verification result, shown as the status dot in the Relay config: the user pastes a key and confirms,
// we probe /health?k=<key> once, and the relay tells us whether the key is accepted. Grey = no key entered;
// Green = accepted; Amber = the relay rejected the key; Red = the relay couldn't be reached to check.
public enum RelayKeyStatus { NoKey, Accepted, Invalid, Unreachable, Checking }

public class RelayHealthService : IDisposable
{
    // One long-lived client (the recommended pattern — never per-request), short timeout so a dead relay fails fast.
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly HMSyncConfig config;
    private readonly IPluginLog log;
    private readonly Func<bool> isWindowOpen;

    private volatile RelayLight light = RelayLight.Grey;
    private volatile RelayKeyStatus keyStatus = RelayKeyStatus.NoKey;
    private CancellationTokenSource? cts;

    public RelayLight Light => light;
    public RelayKeyStatus KeyStatus => keyStatus;

    // Called when the user confirms a key: probe /health?k=<key> once and report whether the relay accepts it.
    // A 200 = accepted; 401/403 (or any 4xx) = the relay rejected the key; unreachable/timeout = can't check.
    // Empty key → NoKey (no probe). Runs off the main thread; the UI reads the cached KeyStatus.
    public async Task CheckKey(string relayUrl, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { keyStatus = RelayKeyStatus.NoKey; return; }
        var probe = KeyHealthUrl(relayUrl, key);
        if (probe == null) { keyStatus = RelayKeyStatus.Unreachable; return; }
        keyStatus = RelayKeyStatus.Checking;
        try
        {
            using var resp = await http.GetAsync(probe).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) keyStatus = RelayKeyStatus.Accepted;
            else if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500) keyStatus = RelayKeyStatus.Invalid;
            else keyStatus = RelayKeyStatus.Unreachable;   // 5xx etc — relay up but not answering cleanly
        }
        catch { keyStatus = RelayKeyStatus.Unreachable; }
    }

    public void ResetKeyStatus() => keyStatus = RelayKeyStatus.NoKey;

    // https .../health?k=<key> for the on-demand key check. ws->http, wss->https; strips path, keeps host+port.
    private static string? KeyHealthUrl(string raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var u = new Uri(raw);
            var scheme = u.Scheme == "wss" ? "https" : (u.Scheme == "ws" ? "http" : u.Scheme);
            var port = (u.Port > 0 && !u.IsDefaultPort) ? ":" + u.Port : "";
            return scheme + "://" + u.Host + port + "/health?k=" + Uri.EscapeDataString(key);
        }
        catch { return null; }
    }

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
                    delayMs = 2000;   // window closed — don't poll; just re-check for it reopening
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
