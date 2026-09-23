namespace HMSync.Services;

using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
// TrackDtrService (b240) — optional server-info-bar (DTR) readout of the CURRENT BGM track. The music twin of
// WeatherDtrService: off by default (config.ShowTrackDtr), opt in from the Config tab's "Server info bar" panel.
// The entry text tracks MapSettingsService.GetCurrentBgm() (the live scene track) named via BgmName; clicking it
// pops out HMS's BGM picker window (via the injected onClick).
//
// Threading: GetCurrentBgm() dereferences game memory, so Tick() must run on the framework thread — it's driven
// from the plugin's OnFrameworkUpdate. Reload() only creates/removes the entry (no game read), so the UI toggle
// path may call it directly. A distinct entry title ("HM-Music") keeps it separate from the weather entry.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
public sealed class TrackDtrService : IDisposable
{
    private const string EntryTitle = "HM-Music";

    private readonly IDtrBar dtrBar;
    private readonly MapSettingsService mapSettings;
    private readonly Func<bool> enabled;   // config.ShowTrackDtr
    private readonly Action onClick;        // pop out the BGM picker
    private readonly IPluginLog log;

    private IDtrBarEntry? entry;
    private long lastTrack = -1;   // -1 = force a text refresh on the next tick

    public TrackDtrService(IDtrBar dtrBar, MapSettingsService mapSettings, Func<bool> enabled, Action onClick, IPluginLog log)
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
                lastTrack = -1;   // force the next Tick to (re)write the text
            }
            catch (Exception ex)
            {
                log.Warning("[HMSync] Track DTR entry create failed: " + ex.Message);
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

        uint id;
        try { id = mapSettings.GetCurrentBgm(); }   // the live scene track
        catch { return; }   // not resolvable this frame (loading screen etc.) — leave the last text up

        if (id == lastTrack) return;   // change-gated: skip the SeString build on unchanged frames
        lastTrack = id;

        string name = id != 0 ? mapSettings.BgmName(id) : "None";
        entry.Text = "♪ " + (string.IsNullOrEmpty(name) ? ("Track " + id) : name);   // ♪ prefix distinguishes it from weather
    }

    public void Dispose()
    {
        try { entry?.Remove(); } catch { /* best-effort */ }
        entry = null;
    }
}
