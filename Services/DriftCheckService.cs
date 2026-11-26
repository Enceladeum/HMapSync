using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

/// <summary>Features that depend on a game memory layout HMS cannot take for granted after a patch.</summary>
public enum DriftFeature
{
    DisplayedWeather,   // raw EnvManager+0x26 (no named field); falls back to ActiveWeather when disabled
    MusicStop,          // raw BGM scene +0x04 SceneFlags write (commented out in ClientStructs)
    TargetScanClear,    // TargetSystem scan-array Length writes during a map reload (named fields)
    TimelineState,      // TimelineContainer ModelState / AnimationState reads (named fields)
    WeatherTables,      // EnvScene weather ids / EnvSpaces / LocationCount, EnvSpace handle, EnvManager.EnvState (named fields)
    LineVfx,            // LineVfx style + position (named fields)
    SharedGroupDump,    // shared-group timeline indices in a diagnostic dump (named field)
}

/// <summary>
/// b321/b323 (Q-0020): struct drift check, run once at plugin load. Pure reflection over the ClientStructs copy Dalamud
/// loaded (updated on patch day); no game memory is touched.
///
/// b323 moved every offset that has a named field onto that field, so those positions now follow ClientStructs by
/// themselves. What can still go wrong after a patch, and what this checks:
///  1. A named field HMS uses was renamed or removed (the code would fail at runtime). Checked by presence.
///  2. The two raw numbers left (no named field exists) sit next to named fields; if a neighbour moved, the raw number is
///     presumed stale. Checked by the neighbour's offset.
/// A failed check DISABLES the dependent feature and says so in chat in every build, not only debug:
/// "HMS: game structures moved after an update. &lt;feature&gt; has been disabled pending a fix."
/// Use sites call <see cref="Refuse"/> (refuse + one chat line per session) or <see cref="IsDisabled"/>.
/// </summary>
public static class DriftCheckService
{
    private enum Kind { Present, At }

    // Present: the member must exist. At: the member must exist at Offset (anchors for a raw neighbour).
    private sealed record Row(DriftFeature Feature, string CsType, string Member, Kind Kind, int Offset, string Why);

    // CsType is relative to FFXIVClientStructs.FFXIV ('+' for nested types).
    private static readonly Row[] Rows =
    {
        new(DriftFeature.DisplayedWeather, "Client.Graphics.Environment.EnvManager", "ActiveWeather", Kind.At, 0x27, "neighbour of the raw displayed-weather byte at 0x26"),
        new(DriftFeature.MusicStop, "Client.Game.BGMSystem+Scene", "SceneId", Kind.At, 0x00, "neighbour of the raw SceneFlags write at 0x04"),
        new(DriftFeature.MusicStop, "Client.Game.BGMSystem+Scene", "SituationKind", Kind.At, 0x08, "neighbour of the raw SceneFlags write at 0x04"),
        new(DriftFeature.TargetScanClear, "Client.Game.Control.TargetSystem", "TargetableObjectsOnScreen", Kind.Present, 0, ""),
        new(DriftFeature.TargetScanClear, "Client.Game.Control.TargetSystem", "ObjectFilterArray1", Kind.Present, 0, ""),
        new(DriftFeature.TargetScanClear, "Client.Game.Control.TargetSystem", "ObjectFilterArray2", Kind.Present, 0, ""),
        new(DriftFeature.TargetScanClear, "Client.Game.Control.TargetSystem", "ObjectFilterArray3", Kind.Present, 0, ""),
        new(DriftFeature.TargetScanClear, "Client.Game.Control.GameObjectArray", "Length", Kind.Present, 0, ""),
        new(DriftFeature.TimelineState, "Client.Game.Character.TimelineContainer", "ModelState", Kind.Present, 0, ""),
        new(DriftFeature.TimelineState, "Client.Game.Character.TimelineContainer", "_animationState", Kind.Present, 0, ""),
        new(DriftFeature.WeatherTables, "Client.Graphics.Environment.EnvManager", "EnvState", Kind.Present, 0, ""),
        new(DriftFeature.WeatherTables, "Client.Graphics.Scene.EnvScene", "_weatherIds", Kind.Present, 0, ""),
        new(DriftFeature.WeatherTables, "Client.Graphics.Scene.EnvScene", "_envSpaces", Kind.Present, 0, ""),
        new(DriftFeature.WeatherTables, "Client.Graphics.Scene.EnvScene", "LocationCount", Kind.Present, 0, ""),
        new(DriftFeature.WeatherTables, "Client.Graphics.Scene.EnvSpace", "EnvSetResourceHandle", Kind.Present, 0, ""),
        new(DriftFeature.LineVfx, "Client.LayoutEngine.Layer.LineVfxLayoutInstance", "LineStyle", Kind.Present, 0, ""),
        new(DriftFeature.LineVfx, "Client.LayoutEngine.Layer.RangeLayoutInstance", "Transform", Kind.Present, 0, ""),
        new(DriftFeature.SharedGroupDump, "Client.LayoutEngine.Group.SharedGroupLayoutInstance", "_timelineIndices", Kind.Present, 0, ""),
    };

    private static readonly Dictionary<DriftFeature, string> FeatureNames = new()
    {
        [DriftFeature.DisplayedWeather] = "Reading the displayed weather (the weather menu falls back to the target weather)",
        [DriftFeature.MusicStop] = "Stopping music",
        [DriftFeature.TargetScanClear] = "The targeting crash guard during map loads",
        [DriftFeature.TimelineState] = "Reading your pose state for sync",
        [DriftFeature.WeatherTables] = "Weather and sky controls",
        [DriftFeature.LineVfx] = "Dungeon barrier line removal",
        [DriftFeature.SharedGroupDump] = "The shared-group diagnostic dump",
    };

    // Not checkable at all (no named field nearby); diagnostics only, pointer-guarded at the use site.
    private static readonly string[] Unverifiable =
    {
        "EnvSet handle +0xC0 container and its +0x10..+0x22 fields (MapSettingsService env-set diagnostic)",
    };

    private static readonly HashSet<DriftFeature> disabled = new();
    private static readonly HashSet<DriftFeature> refusedNotified = new();
    private static Action<string>? chat;
    private static IPluginLog? log;

    public static bool IsDisabled(DriftFeature f) { lock (disabled) return disabled.Contains(f); }

    /// <summary>True (and one loud chat line per session for this feature) when the feature is disabled. Use at the
    /// point a user action would have used the layout.</summary>
    public static bool Refuse(DriftFeature f)
    {
        if (!IsDisabled(f)) return false;
        bool first;
        lock (disabled) first = refusedNotified.Add(f);
        if (first)
        {
            var msg = $"HMS: {FeatureNames[f]} is disabled: game structures moved after an update, pending an HMS fix.";
            log?.Error("[HMSync] [DRIFT] refused: " + msg);
            chat?.Invoke(msg);
        }
        return true;
    }

    /// <summary>Runs the check. Returns the number of disabled features.</summary>
    public static int Run(IPluginLog pluginLog, HMSyncConfig config, Action<string> chatPrint)
    {
        log = pluginLog; chat = chatPrint;
        string gameVer = ReadGameVersion();
        var csAsm = typeof(FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem).Assembly;
        string csVer = csAsm.GetName().Version?.ToString() ?? "?";
        bool firstRun = config.DriftLastGameVersion.Length == 0 && config.DriftLastCsVersion.Length == 0;
        bool versionsChanged = gameVer != config.DriftLastGameVersion || csVer != config.DriftLastCsVersion;

        var lines = new List<string>();
        var failed = new Dictionary<DriftFeature, List<string>>();
        foreach (var r in Rows)
        {
            string? fail = Check(csAsm, r);
            string label = $"{r.CsType.Substring(r.CsType.LastIndexOfAny(new[] { '.', '+' }) + 1)}.{r.Member}";
            if (fail == null) lines.Add($"  ok    {label} ({r.Feature})");
            else
            {
                lines.Add($"  FAIL  {label} ({r.Feature}): {fail}");
                if (!failed.TryGetValue(r.Feature, out var l)) failed[r.Feature] = l = new List<string>();
                l.Add(label + ": " + fail);
            }
        }
        lock (disabled) { disabled.Clear(); refusedNotified.Clear(); foreach (var f in failed.Keys) disabled.Add(f); }

        string since = firstRun ? " (first check on this install)"
            : versionsChanged ? $" (changed since last load: game {config.DriftLastGameVersion}, ClientStructs {config.DriftLastCsVersion})" : "";
        string head = $"[DRIFT] game {gameVer}, ClientStructs {csVer}: {Rows.Length - CountFails(failed)} of {Rows.Length} checks ok, {failed.Count} feature(s) disabled{since}.";
        if (failed.Count > 0 || versionsChanged)
        {
            if (failed.Count > 0) log.Error("[HMSync] " + head); else log.Information("[HMSync] " + head);
            foreach (var l in lines) { if (l.Contains("FAIL")) log.Error("[HMSync] [DRIFT]" + l); else log.Information("[HMSync] [DRIFT]" + l); }
            foreach (var u in Unverifiable) log.Information("[HMSync] [DRIFT]   not checkable: " + u);
        }
        else log.Information("[HMSync] " + head);   // b324: always one visible line (the release gate quotes it, Q-0056)

        // Loud in every build: one line per disabled feature.
        foreach (var f in failed.Keys)
            chatPrint($"HMS: game structures moved after an update. {FeatureNames[f]} has been disabled pending a fix. Details: [DRIFT] in /xllog.");

        if (versionsChanged)
        {
            config.DriftLastGameVersion = gameVer;
            config.DriftLastCsVersion = csVer;
            config.Save();
        }
        return failed.Count;
    }

    private static int CountFails(Dictionary<DriftFeature, List<string>> failed)
    {
        int n = 0; foreach (var l in failed.Values) n += l.Count; return n;
    }

    // null = pass; otherwise why it failed.
    private static string? Check(Assembly csAsm, Row r)
    {
        var t = csAsm.GetType("FFXIVClientStructs.FFXIV." + r.CsType);
        if (t == null) return "type not found (renamed or removed)";
        var f = t.GetField(r.Member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) return "field not found (renamed or removed)";
        if (r.Kind == Kind.Present) return null;
        var fo = f.GetCustomAttribute<FieldOffsetAttribute>();
        if (fo == null) return "field has no FieldOffset";
        return fo.Value == r.Offset ? null : $"moved from 0x{r.Offset:X} to 0x{fo.Value:X}; {r.Why} is presumed stale";
    }

    // ffxivgame.ver sits next to ffxiv_dx11.exe. "?" when unreadable (never throws).
    private static string ReadGameVersion()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            var dir = exe != null ? Path.GetDirectoryName(exe) : null;
            var ver = dir != null ? Path.Combine(dir, "ffxivgame.ver") : null;
            return ver != null && File.Exists(ver) ? File.ReadAllText(ver).Trim() : "?";
        }
        catch { return "?"; }
    }
}
