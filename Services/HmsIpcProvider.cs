namespace HMSync.Services;

using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// HMS's FIRST IPC PROVIDER surface — the "HMSync.*" namespace. Until now HMS was purely an IPC *consumer*
// (HDM.* / Moniker.* / Glamourer.*); this is the reverse direction: HMS exposing state to its sibling modules.
//
// Contract of record: docs\HMS-IPC-Provider-Contract.md (v1). First consumer: HDM 0.8.70 (HGuise\HmsIpc.cs),
// whose ask is docs\ (HGuise) hms-accent-ipc-ask.md. The LABEL STRINGS are the hard contract — HDM binds
// these exact strings, so they must not drift. This is Tier 0 (handshake) + Tier 1 (accent) of the tiered
// surface; Tier 2 (session context) / Tier 3 (transport multiplexer for the world editor) append here later
// under the SAME namespace, bumping ApiMinor per additive group (consumers gate on ApiMajor).
//
// ⚠ ALC BOUNDARY: only primitives / value-tuples / float[] cross cleanly (no shared types). GetAccentColor
// returns HMSyncConfig.AccentColor AS-IS (RGBA 0..1, length 4) — HDM reads [0..3], forces alpha 1.
//
// SOFT-DEGRADE both ways: registration is guarded; if it ever fails HMS runs normally and HDM simply falls
// back to its own accent (both default to the same gold, so nothing looks off until HMS is themed).
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class HmsIpcProvider : IDisposable
{
    // Version of the HMSync.* provider SURFACE (not the plugin). Accent surface = (1, 0).
    // ApiMajor++ on a breaking change (removed/renamed gate, changed signature/semantics);
    // ApiMinor++ when an additive capability group lands (session context, transport lanes).
    private const uint ApiMajor = 1;
    private const uint ApiMinor = 0;

    private static readonly float[] DefaultAccent = { 0.83f, 0.62f, 0.20f, 1f };   // gold — same default HMS + HDM ship

    private readonly IPluginLog log;
    private readonly Func<float[]?> accentSupplier;

    private ICallGateProvider<(uint, uint)>? apiVersion;
    private ICallGateProvider<float[]>? getAccentColor;
    private ICallGateProvider<object?>? accentChanged;   // push-only (SendMessage); no local handler registered

    public HmsIpcProvider(IDalamudPluginInterface pi, IPluginLog log, Func<float[]?> accentSupplier)
    {
        this.log = log;
        this.accentSupplier = accentSupplier;

        try
        {
            apiVersion = pi.GetIpcProvider<(uint, uint)>("HMSync.ApiVersion");
            apiVersion.RegisterFunc(() => (ApiMajor, ApiMinor));

            getAccentColor = pi.GetIpcProvider<float[]>("HMSync.GetAccentColor");
            getAccentColor.RegisterFunc(GetAccent);

            // Optional-but-recommended change ping: lets a subscriber refresh instantly instead of polling.
            // HDM 0.8.70 polls (~2 s) and does not yet subscribe this; exposing it now lets a later HDM drop the poll.
            accentChanged = pi.GetIpcProvider<object?>("HMSync.AccentChanged");

            log.Information($"[HMSync] IPC provider registered (HMSync.* v{ApiMajor}.{ApiMinor}).");
        }
        catch (Exception ex)
        {
            log.Debug("[HMSync] IPC provider registration failed (non-fatal): " + ex.Message);
        }
    }

    /// <summary>Live accent as RGBA 0..1 length-4, config value as-is; a null/short array degrades to the gold default.</summary>
    private float[] GetAccent()
    {
        try
        {
            var a = accentSupplier();
            if (a != null && a.Length >= 4) return a;
        }
        catch (Exception ex) { log.Debug("[HMSync] GetAccentColor supplier threw: " + ex.Message); }
        return DefaultAccent;
    }

    /// <summary>Fire after the user COMMITS an accent change (the Config accent picker, right after config.Save()).
    /// Fan-out only — no-op if no subscriber. Removes a consumer's poll lag; never required for correctness.</summary>
    public void NotifyAccentChanged()
    {
        try { accentChanged?.SendMessage(); }
        catch (Exception ex) { log.Debug("[HMSync] AccentChanged notify failed: " + ex.Message); }
    }

    public void Dispose()
    {
        try { apiVersion?.UnregisterFunc(); } catch { }
        try { getAccentColor?.UnregisterFunc(); } catch { }
        // accentChanged is push-only — nothing was registered, nothing to unregister.
    }
}
