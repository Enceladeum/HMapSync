namespace HMSync.Services;

using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// WeatherDtrService (b225) — optional server-info-bar (DTR) readout of the CURRENT displayed weather.
//
// Mirrors the Weatherman plugin's "clear skies" HUD entry. Off by default (config.ShowWeatherDtr); the user opts
// in from the Config tab. The entry text tracks MapSettingsService.GetActiveWeather() — out of session that's the
// REAL zone's displayed weather (matching what the player sees), and in a session it's the authored/synced value
// the env is showing. Clicking the entry pops out HMS's weather-presets window (via the injected onClick).
//
// Threading: GetActiveWeather() dereferences game memory, so the read must run on the framework thread. Tick() is
// driven from the plugin's existing OnFrameworkUpdate (framework thread) — never off-thread. Reload() only
// creates/removes the entry (no game read), so it is safe to call from the UI toggle path as well.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class WeatherDtrService : IDisposable
{
    private const string EntryTitle = "HM-Sync";

    private readonly IDtrBar dtrBar;
    private readonly MapSettingsService mapSettings;
    private readonly Func<bool> enabled;   // config.ShowWeatherDtr
    private readonly Action onClick;        // pop out the weather-presets window
    private readonly IPluginLog log;

    private IDtrBarEntry? entry;
    private int lastWeather = -1;   // -1 = force a text refresh on the next tick

    public WeatherDtrService(IDtrBar dtrBar, MapSettingsService mapSettings, Func<bool> enabled, Action onClick, IPluginLog log)
    {
        this.dtrBar = dtrBar;
        this.mapSettings = mapSettings;
        this.enabled = enabled;
        this.onClick = onClick;
        this.log = log;
        Reload();
    }

    /// <summary>Create or tear down the DTR entry to match the current config toggle. Idempotent; safe to call on
    /// every toggle flip. Does NOT read game state (the text is filled by the next Tick).</summary>
    public void Reload()
    {
        bool want = enabled();
        if (want && entry == null)
        {
            try
            {
                entry = dtrBar.Get(EntryTitle, "…");   // placeholder ellipsis until the first Tick fills it
                entry.OnClick = _ => onClick();
                entry.Shown = true;
                lastWeather = -1;   // force the next Tick to (re)write the text
            }
            catch (Exception ex)
            {
                log.Warning("[HMSync] Weather DTR entry create failed: " + ex.Message);
                entry = null;
            }
        }
        else if (!want && entry != null)
        {
            try { entry.Remove(); } catch { /* best-effort */ }
            entry = null;
        }
    }

    /// <summary>Per-frame change-gated text refresh. Called from the plugin's OnFrameworkUpdate (framework thread).
    /// No-op unless the entry exists (toggle on).</summary>
    public void Tick()
    {
        if (entry == null) return;

        byte w;
        try { w = mapSettings.GetDisplayWeather(); }   // b226: prefer the user's applied pick — a graft/cram leaves the engine byte at the native weather
        catch { return; }   // env not resolvable this frame (loading screen etc.) — leave the last text up

        if (w == lastWeather) return;   // change-gated: skip the SeString build on unchanged frames
        lastWeather = w;

        string name = mapSettings.WeatherName(w);
        entry.Text = string.IsNullOrEmpty(name) ? ("Weather " + w) : name;
    }

    public void Dispose()
    {
        try { entry?.Remove(); } catch { /* best-effort */ }
        entry = null;
    }
}
