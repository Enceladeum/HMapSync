using System;
using System.Linq;
using Dalamud.Plugin;

namespace HMSync.Services;

// v0.7.371: presence detection + one-click open for the Modules panel.
//
// Deliberately NOT IPC-based. The Modules panel is display-only ("is this installed?"), and binding IPC just to
// answer that would mean inventing an IPC contract per module — fine for Moniker (HMS already talks to it for real)
// but wrong for HOutfits, which HMS doesn't integrate with at all yet. Dalamud's InstalledPlugins list answers the
// presence question directly, for any plugin, with no contract at all.
//
// It also gives us something better than a slash command for the clickable chip: IExposedPlugin exposes
// HasMainUi/OpenMainUi(), so the chip opens the plugin's own window directly — no guessing at "/hmoniker" or
// "/houtfits", and it keeps working if a module renames its command.
public sealed class InstalledPluginService
{
    private readonly IDalamudPluginInterface pi;

    public InstalledPluginService(IDalamudPluginInterface pi) => this.pi = pi;

    // Match on InternalName OR display Name, case-insensitively, plus any supplied aliases. Deliberately tolerant:
    // a plugin's INTERNAL name often differs from what users call it (HMS itself ships as "HM-Sync", not "HMSync"),
    // and guessing wrong would silently leave a module permanently showing "Not installed" with no clue why. Matching
    // both fields makes the row correct without needing the manifest in hand.
    private IExposedPlugin? Find(string name, params string[] aliases)
    {
        try
        {
            bool Matches(IExposedPlugin p)
            {
                if (!p.IsLoaded) return false;
                if (string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var a in aliases)
                {
                    if (string.Equals(p.InternalName, a, StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(p.Name, a, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            return pi.InstalledPlugins.FirstOrDefault(Matches);
        }
        catch { return null; }
    }

    /// <summary>Is this plugin installed AND loaded?</summary>
    public bool IsPresent(string name, params string[] aliases) => Find(name, aliases) != null;

    /// <summary>Can we open a window for it? (main UI preferred, config UI as fallback)</summary>
    public bool CanOpen(string name, params string[] aliases)
    {
        var p = Find(name, aliases);
        return p != null && (p.HasMainUi || p.HasConfigUi);
    }

    /// <summary>Open the plugin's own window — main UI if it has one, else its config UI.</summary>
    public void Open(string name, params string[] aliases)
    {
        var p = Find(name, aliases);
        if (p == null) return;
        try
        {
            if (p.HasMainUi) p.OpenMainUi();
            else if (p.HasConfigUi) p.OpenConfigUi();
        }
        catch { /* a module's own UI threw — not ours to handle */ }
    }
}
