using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Stage = HMSync.Services.CutsceneStageService.Stage;

namespace HMSync.Services;

// ============================================================================
// CutsceneStageDetectService (NB-33) - runtime auto-detection of cutscene-only bg venues.
//
// Ports the proven offline chain (xivtool XivTool.Core/Cutb/{ScenePartsOps,StageCandidateOps}.cs)
// into the plugin: enumerate the Cutscene sheet, scan each unique .cutb for its scene-part paths,
// take the dominant bg/<stagedir>/ prefix as the venue, join TerritoryType, and DROP every venue
// reachable by a normal TT load (a job intro in Ul'dah is redundant - just load Ul'dah). What
// survives = bgs that exist ONLY as cutscene venues.
//
// The scan is ~4k cutbs, too heavy per-launch, so it runs once on a background thread and caches
// the result to the plugin config dir keyed by game version - re-derived only after a patch. The
// cache is a plugin-internal DERIVED artifact, not a hand-authored data file, so this stays
// "automatic like territories" (auto-derived per patch, not hand-authored).
//
// Detected venues are forced Experimental; curated AllStages[] rows win on Bg collision (they carry the
// real Name/Spawn). Promoted to the live build after the 7.55hf1 post-patch validation gate.
// ============================================================================
public sealed class CutsceneStageDetectService : IDisposable
{
    // Scene-part path shapes (verbatim from ScenePartsOps): level bgparts .mdl and collision .pcb.
    private static readonly Regex RxPart = new(@"^bg/[ -~]+?/bgparts/[ -~]+?\.mdl$", RegexOptions.Compiled);
    private static readonly Regex RxColl = new(@"^bg/[ -~]+?/collision/[ -~]+?\.pcb$", RegexOptions.Compiled);
    // bg/<stagedir>/bgparts|collision/... -> stagedir (the venue key).
    private static readonly Regex RxStageDir = new(@"^bg/(?<dir>.+?)/(?:bgparts|collision)/", RegexOptions.Compiled);

    // Dedup rule (b70, operator-directed): drop every venue that has a TerritoryType row. If a venue has a
    // TT number it's reachable via a normal TT load and already offered by the other chips ("if it has a TT
    // number, grab it elseway, so it needn't go here"). What survives = genuinely cutscene-ONLY rooms with no
    // TerritoryType at all (the /evt donor rooms - e.g. x6e3, x6e6, z6c1). This supersedes the earlier
    // IntendedUse-roam (b68) and ContentFinderCondition-duty (b69) refinements, which were partial proxies for
    // "normally loadable"; "has a TT" is the exact, complete signal.

    private const string CacheFileName = "cutscene-venues.json";
    // Bump when the derivation LOGIC changes (independent of game version) so a stale cache from an older
    // logic version is discarded even on the same patch. b68 = 1 (roam-only drop); b69 = 2 (+ CFC-duty drop);
    // b70 = 3 (drop ALL TT-backed venues; keep only TT-less /evt rooms); b72 = 4 (+ auto quest-label join
    // from embedded cutscene-quests.json); b73 = 5 (+ auto spawn/facing join from embedded cutscene-spawns.json);
    // b74 = 6 (spawn-map values refined - x6e6 re-derived from the 'actor' cast; bump forces stale b73 caches to
    // re-derive so the corrected map is picked up, since the cache stores the baked spawn, not the map);
    // b75 = 7 (+ manual label overrides baked into Stage.Name; bump so caches re-derive with the friendly labels).
    private const int DeriveVersion = 7;

    // NB-33: embedded stagedir->quest map. DERIVED per-patch artifact (Fable cutscene-territory-index.csv joined
    // with dominant-venue-per-cutb), NOT hand-authored. This is the TT-less slice of the SAME provenance that baked
    // the TerritoryId-keyed QuestNames dict; keyed by stagedir since TT-less venues have no TerritoryId.
    private const string QuestMapResource = "cutscene-quests.json";
    // NB-33: manual label overrides for detected venues. Labels are the ONE part not yet auto-derivable - the code
    // ("x6e6") isn't friendly, and the auto quest label names the STORY beat ("Embracing Oblivion"), not the PLACE.
    // Keyed by stagedir. Until a PlaceName heuristic exists this stays a small hand-curated list (operator-supplied);
    // absent => Name falls back to the code. This is the deliberate manual-curation slot; the quest/spawn maps stay
    // fully auto-derived.
    private static readonly Dictionary<string, string> LabelOverrides = new(StringComparer.Ordinal)
    {
        ["ex5/01_xkt_x6/evt/x6e3"] = "Shaaloani",
        ["ex5/01_xkt_x6/evt/x6e6"] = "Everkeep",
        ["ex5/05_zon_z6/chr/z6c1"] = "DT title screen",
    };

    // NB-33: embedded stagedir->spawn map. DERIVED per-patch artifact (Fable), NOT hand-authored: median-floor
    // centroid of each venue's cutb character keyframes - the same provenance the curated AllStages spawns came from
    // (stand among the cutscene cast at floor Y). Absent venue => loader keeps the donor-territory fallback (no regress).
    private const string SpawnMapResource = "cutscene-spawns.json";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly IDataManager data;

    private volatile List<Stage>? detected;
    private int started;

    public CutsceneStageDetectService(IDalamudPluginInterface pi, IPluginLog log, IDataManager data)
    {
        this.pi = pi; this.log = log; this.data = data;
    }

    /// <summary>Detected cutscene-only venues (empty until the background scan completes).</summary>
    public IReadOnlyList<Stage> Detected => detected ?? (IReadOnlyList<Stage>)Array.Empty<Stage>();

    /// <summary>Fired (on the background thread) when Detected is first populated.</summary>
    public event System.Action? ScanCompleted;

    /// <summary>Kick off the version-cached background derivation. Idempotent. gameVersion should be
    /// captured on the framework thread by the caller (it reads a CS singleton).</summary>
    public void StartScan(string gameVersion)
    {
        if (System.Threading.Interlocked.Exchange(ref started, 1) != 0) return;
        Task.Run(() =>
        {
            try { Run(gameVersion); }
            catch (Exception ex) { log.Error("[CSDetect] scan failed: " + ex); }
        });
    }

    private void Run(string gameVersion)
    {
        var cachePath = Path.Combine(pi.ConfigDirectory.FullName, CacheFileName);
        if (!string.IsNullOrEmpty(gameVersion) && TryLoadCache(cachePath, gameVersion, out var cached))
        {
            log.Information("[CSDetect] loaded " + cached.Count + " venue(s) from cache (game " + gameVersion + ")");
            Publish(cached);
            return;
        }

        var sw = Stopwatch.StartNew();
        var venues = Derive();
        sw.Stop();
        log.Information("[CSDetect] scan complete in " + sw.ElapsedMilliseconds + "ms: " + venues.Count + " venue(s) kept");
        if (!string.IsNullOrEmpty(gameVersion)) SaveCache(cachePath, gameVersion, venues);
        Publish(venues);
    }

    private void Publish(List<Stage> venues)
    {
        detected = venues;
        try { ScanCompleted?.Invoke(); } catch (Exception ex) { log.Warning("[CSDetect] ScanCompleted handler threw: " + ex.Message); }
    }

    // ------------------------------------------------------------------ derivation

    private List<Stage> Derive()
    {
        // 1. Cutscene rows -> unique cutb paths.
        var cutbs = new HashSet<string>(StringComparer.Ordinal);
        var cs = data.GetExcelSheet<Cutscene>();
        if (cs != null)
            foreach (var row in cs)
            {
                var p = row.Path.ToString();
                if (!string.IsNullOrEmpty(p) && p.Contains('/')) cutbs.Add(p);
            }

        // 2. TerritoryType index: stagedir ("ffxiv/wil_w1/evt/w1en") -> (lowest RowId, full Bg, PlaceName, IntendedUse).
        //    b70: a stagedir being present here at all is the drop signal - if a venue has a TT it's reachable
        //    via a normal load and already offered by the other chips, so the earlier CFC/IntendedUse refinement
        //    is no longer needed. We keep the full tuple only for logging the dropped venue's identity.
        var ttByDir = new Dictionary<string, (uint Tt, string Bg, string Place, uint Use)>(StringComparer.Ordinal);
        var tsheet = data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (tsheet != null)
            foreach (var t in tsheet)
            {
                var bg = t.Bg.ToString();
                if (string.IsNullOrEmpty(bg)) continue;
                int li = bg.IndexOf("/level/", StringComparison.Ordinal);
                if (li < 0) continue;
                var dir = bg[..li];
                if (ttByDir.ContainsKey(dir)) continue; // first row (lowest RowId) wins
                var place = t.PlaceName.ValueNullable?.Name.ToString() ?? "";
                ttByDir[dir] = ((uint)t.RowId, bg, place, t.TerritoryIntendedUse.RowId);
            }

        // 3. Per cutb: dominant bg/<stagedir>/ prefix over its part+collision paths.
        var venueCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in cutbs)
        {
            var dir = DominantVenue(p);
            if (dir != null) venueCounts[dir] = venueCounts.GetValueOrDefault(dir) + 1;
        }

        // 4. Classify + bias-to-keep dedup. Log every keep/drop so results are inspectable ("see how it works").
        var questMap = LoadQuestMap();
        var spawnMap = LoadSpawnMap();
        var result = new List<Stage>();
        int dropped = 0, tierA = 0;
        foreach (var kv in venueCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var dir = kv.Key;
            var count = kv.Value;
            var code = dir[(dir.LastIndexOf('/') + 1)..];

            if (ttByDir.TryGetValue(dir, out var tt))
            {
                // Has a TerritoryType row => reachable via a normal TT load and already offered by the other
                // chips. Operator rule (b70): "if it has a TT number, grab it elseway from the other chips,
                // so it needn't go here." Any TT-backed venue is redundant in the Cutscenes chip regardless of
                // IntendedUse/duty. Always drop. Only genuinely cutscene-ONLY rooms (no TerritoryType) are novel.
                dropped++;
                log.Information("[CSDetect] drop  " + tt.Bg + " TT" + tt.Tt + " use=" + tt.Use +
                    " '" + tt.Place + "' (has TT - loadable via other chips, " + count + " cutb)");
                continue;
            }
            else
            {
                // Tier A - no TT. Needs a real level bg.lgb to be a swap candidate.
                var lgb = "bg/" + dir + "/level/bg.lgb";
                if (!data.FileExists(lgb))
                {
                    log.Debug("[CSDetect] skip  " + dir + " (no TT, no bg.lgb, " + count + " cutb)");
                    continue;
                }
                var bg = dir + "/level/" + code;
                var name = LabelOverrides.TryGetValue(dir, out var lbl) ? lbl : code;
                var quest = questMap.TryGetValue(dir, out var qq) ? qq : "";
                (float X, float Y, float Z)? spawn = null;
                float? facing = null;
                if (spawnMap.TryGetValue(dir, out var sp)) { spawn = (sp.X, sp.Y, sp.Z); facing = sp.Facing; }
                tierA++;
                log.Information("[CSDetect] keep  " + bg + " '" + name + "' (Tier A swap, " + count + " cutb" +
                    (quest.Length > 0 ? ", quest '" + quest + "'" : "") +
                    (spawn.HasValue ? ", spawn (" + spawn.Value.X.ToString("F1") + "," + spawn.Value.Y.ToString("F1") +
                        "," + spawn.Value.Z.ToString("F1") + ")" : "") + ")");
                result.Add(new Stage(name, bg, true, quest, 0, spawn, facing));
            }
        }

        log.Information("[CSDetect] " + result.Count + " kept (" + tierA + " Tier A / TT-less), " +
            dropped + " dropped (has TT - loadable elsewhere), of " + venueCounts.Count +
            " raw venue(s) from " + cutbs.Count + " cutb(s)");
        return result;
    }

    // Printable-string scan of one cutb -> its dominant venue stagedir (null when absent / no bg parts).
    // Verbatim logic from ScenePartsOps.Scan + StageCandidateOps dominant-prefix pick.
    private string? DominantVenue(string cutbPath)
    {
        var f = data.GetFile("cut/" + cutbPath + ".cutb");
        if (f == null) return null;
        var d = f.Data;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int start = -1;
        for (int i = 0; i <= d.Length; i++)
        {
            bool ok = i < d.Length && d[i] >= 0x20 && d[i] < 0x7f;
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 10)
            {
                var s = Encoding.ASCII.GetString(d, start, i - start);
                if (RxPart.IsMatch(s) || RxColl.IsMatch(s))
                {
                    var m = RxStageDir.Match(s);
                    if (m.Success)
                    {
                        var dir = m.Groups["dir"].Value;
                        counts[dir] = counts.GetValueOrDefault(dir) + 1;
                    }
                }
            }
            start = -1;
        }
        if (counts.Count == 0) return null;
        return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;
    }

    // ------------------------------------------------------------------ quest map (embedded)

    private sealed record QuestMapDto(string? Version, Dictionary<string, string>? Venues);

    // Load the embedded stagedir->quest map. Best-effort: a missing/garbled resource just means detected venues
    // show no quest (never fatal). Mirrors OpcodeMapService.LoadEmbedded (resource name = "HMSync.<file>").
    private Dictionary<string, string> LoadQuestMap()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(QuestMapResource, StringComparison.OrdinalIgnoreCase));
            if (resName == null) { log.Warning("[CSDetect] quest map resource not found"); return new(StringComparer.Ordinal); }
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) { log.Warning("[CSDetect] quest map stream null"); return new(StringComparer.Ordinal); }
            using var reader = new StreamReader(stream);
            var dto = JsonSerializer.Deserialize<QuestMapDto>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dto?.Venues != null)
                foreach (var kv in dto.Venues)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) map[kv.Key] = kv.Value;
            log.Information("[CSDetect] quest map loaded (bundled): " + map.Count + " venue(s), ver " + (dto?.Version ?? "?"));
            return map;
        }
        catch (Exception ex) { log.Warning("[CSDetect] quest map load failed: " + ex.Message); return new(StringComparer.Ordinal); }
    }

    // ------------------------------------------------------------------ spawn map (embedded)

    private sealed record SpawnDto(float X, float Y, float Z, float? Facing);
    private sealed record SpawnMapDto(string? Version, Dictionary<string, SpawnDto>? Venues);

    // Load the embedded stagedir->spawn map. Best-effort: absent/garbled => detected venues fall back to the loader's
    // donor-territory spawn (never fatal, never a regression). Mirrors LoadQuestMap.
    private Dictionary<string, SpawnDto> LoadSpawnMap()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(SpawnMapResource, StringComparison.OrdinalIgnoreCase));
            if (resName == null) { log.Warning("[CSDetect] spawn map resource not found"); return new(StringComparer.Ordinal); }
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) { log.Warning("[CSDetect] spawn map stream null"); return new(StringComparer.Ordinal); }
            using var reader = new StreamReader(stream);
            var dto = JsonSerializer.Deserialize<SpawnMapDto>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var map = new Dictionary<string, SpawnDto>(StringComparer.Ordinal);
            if (dto?.Venues != null)
                foreach (var kv in dto.Venues)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) map[kv.Key] = kv.Value;
            log.Information("[CSDetect] spawn map loaded (bundled): " + map.Count + " venue(s), ver " + (dto?.Version ?? "?"));
            return map;
        }
        catch (Exception ex) { log.Warning("[CSDetect] spawn map load failed: " + ex.Message); return new(StringComparer.Ordinal); }
    }

    // ------------------------------------------------------------------ version cache

    private sealed record VenueDto(string Bg, string Name, uint TerritoryId, string Quest,
        float? SpawnX, float? SpawnY, float? SpawnZ, float? Facing);
    private sealed record CacheDto(string GameVersion, int DeriveVersion, List<VenueDto> Venues);

    private bool TryLoadCache(string path, string version, out List<Stage> stages)
    {
        stages = new List<Stage>();
        try
        {
            if (!File.Exists(path)) return false;
            var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(path));
            if (dto == null || dto.GameVersion != version || dto.DeriveVersion != DeriveVersion || dto.Venues == null) return false;
            foreach (var v in dto.Venues)
            {
                (float X, float Y, float Z)? spawn = (v.SpawnX.HasValue && v.SpawnY.HasValue && v.SpawnZ.HasValue)
                    ? (v.SpawnX.Value, v.SpawnY.Value, v.SpawnZ.Value) : null;
                stages.Add(new Stage(v.Name, v.Bg, true, v.Quest ?? "", v.TerritoryId, spawn, v.Facing));
            }
            return true;
        }
        catch (Exception ex) { log.Warning("[CSDetect] cache read failed: " + ex.Message); return false; }
    }

    private void SaveCache(string path, string version, List<Stage> stages)
    {
        try
        {
            var dto = new CacheDto(version, DeriveVersion, stages.Select(s => new VenueDto(s.Bg, s.Name, s.TerritoryId, s.Quest,
                s.Spawn?.X, s.Spawn?.Y, s.Spawn?.Z, s.Facing)).ToList());
            File.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { log.Warning("[CSDetect] cache write failed: " + ex.Message); }
    }

    public void Dispose() { }
}
