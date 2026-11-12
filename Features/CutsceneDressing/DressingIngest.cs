using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HMSync.World;
using Lumina.Data.Files;
using static HMSync.Features.CutsceneDressing.DressingCatalog;

namespace HMSync.Features.CutsceneDressing;

// ============================================================================
// DressingIngest - builds the whole dressing catalog from the INSTALLED game, so a game update needs no manual step.
//
// Runs once per game version on a background thread and caches the result in the plugin config folder (the same pattern
// as CutsceneStageDetectService). Until it finishes (or if it fails) the embedded data shipped with the plugin is used.
// Output has exactly the shape of the embedded files (DressingCatalog.FileDto), split into the curated set (fixed rows,
// friendly names) and everything else ("lab": the auto-ranked set, labelled by quest).
//
// Rules (identical to the offline bake it replaces, which stays only as a cross-check):
//   selection   every Cutscene row whose cutb places >= 8 props inline; venue = the cutb's dominant level folder; a venue
//               without a TerritoryType must have a loadable level/bg.lgb (swap stage); per venue, scenes whose prop sets
//               overlap >= 70% (Jaccard on path multisets) collapse into the richest one
//   transforms  CTAL placement; setup C013 curve -> its scale always, its pos/rot only when within 0.5y of CTAL (curves far
//               from CTAL are shot staging); otherwise CTAL x setup C035 uniform scale
//   collision   the model's own same-named .pcb when the game ships one; a cutscene copy of housing furniture (identical
//               model bounds, vertex count within 2%) is swapped for the housing item's .sgb (engine collision + seats)
//   hides       not stored: read from the cutb at activation (CutbInstanceRefs); duplicate originals are caught live
//   labels      quest name from the Quest sheet's script vars whose name contains "CUT"; spawn = prop median + 2y on
//               territory venues (curated overrides win)
// ============================================================================
public sealed class DressingIngest
{
    private const int DeriveVersion = 5;            // bump when a rule above changes, to force a re-derive (2: b273 map edits; 3: b275 lights + door; 4: b276 deck floor; 5: b277 drawn deck floor + turned cottage)
    private const string CacheFileName = "cutscene-dressings.ingest.json";
    private const int MinProps = 8;
    private const double DedupeJaccard = 0.7;
    private const float CurveAgree = 0.5f;

    /// <summary>Hand-picked rows with friendly names; an explicit spawn overrides the prop-median one.</summary>
    public static readonly Dictionary<uint, (string Name, float[]? Spawn)> Curated = new()
    {
        [2504] = ("Valnain", null),
        [2392] = ("Imperial hangar (Blood of Emerald)", null),
        [2516] = ("Imperial hangar (Duty in the Sky with Diamond)", null),
        [2348] = ("Valens van Varro's chambers, furnished", null),
        [1882] = ("Black Rose facility", new[] { -404.4f, 6.0f, -273.5f }),
    };

    private readonly IPluginLog log;
    private readonly IDataManager data;
    private readonly string cacheDir;
    private int started;

    public DressingIngest(IPluginLog log, IDataManager data, string cacheDir)
    {
        this.log = log;
        this.data = data;
        this.cacheDir = cacheDir;
    }

    // b278: the cache is also keyed to the exact plugin build, so a rebuild can never reuse a stale derivation (b277 did:
    // a fix rebuilt under the same DeriveVersion kept serving the planter pillars from the cache).
    private static readonly string BuildId = typeof(DressingIngest).Assembly.ManifestModule.ModuleVersionId.ToString();

    private sealed class CacheDto
    {
        public string GameVersion { get; set; } = "";
        public int DeriveVersion { get; set; }
        public string Build { get; set; } = "";   // b278: the plugin build that derived it (a new build re-derives: ~4s)
        public FileDto? Curated { get; set; }
        public FileDto? Lab { get; set; }
    }

    /// <summary>Load the cache for this game version or derive it, off the framework thread. onReady is called on the
    /// background thread; marshal to the framework thread before touching game state. Idempotent unless forced.</summary>
    public void Start(string gameVersion, Action<FileDto, FileDto, string> onReady, bool force = false)
    {
        if (!force && Interlocked.Exchange(ref started, 1) != 0) return;
        Task.Run(() =>
        {
            try
            {
                var cachePath = Path.Combine(cacheDir, CacheFileName);
                if (!force && !string.IsNullOrEmpty(gameVersion) && TryLoadCache(cachePath, gameVersion, out var cached))
                {
                    log.Information("[DressingIngest] loaded from cache (game " + gameVersion + ")");
                    onReady(cached.Curated!, cached.Lab!, "ingest cache " + gameVersion);
                    return;
                }
                var sw = Stopwatch.StartNew();
                var (curated, lab) = Derive();
                sw.Stop();
                log.Information("[DressingIngest] derived from the game in " + sw.ElapsedMilliseconds + "ms: " +
                                curated.Dressings!.Count + " curated + " + lab.Dressings!.Count + " lab dressing(s)");
                if (!string.IsNullOrEmpty(gameVersion)) SaveCache(cachePath, gameVersion, curated, lab);
                onReady(curated, lab, "ingest " + gameVersion);
            }
            catch (Exception ex) { log.Error("[DressingIngest] failed, keeping the shipped data: " + ex); }
        });
    }

    private bool TryLoadCache(string path, string gameVersion, out CacheDto dto)
    {
        dto = null!;
        try
        {
            if (!File.Exists(path)) return false;
            var c = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(path), JsonOptions);
            if (c == null || c.GameVersion != gameVersion || c.DeriveVersion != DeriveVersion || c.Build != BuildId || c.Curated?.Dressings == null || c.Lab?.Dressings == null) return false;
            dto = c;
            return true;
        }
        catch (Exception ex) { log.Warning("[DressingIngest] cache read failed: " + ex.Message); return false; }
    }

    private void SaveCache(string path, string gameVersion, FileDto curated, FileDto lab)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(path, JsonSerializer.Serialize(new CacheDto { GameVersion = gameVersion, DeriveVersion = DeriveVersion, Build = BuildId, Curated = curated, Lab = lab }));
        }
        catch (Exception ex) { log.Warning("[DressingIngest] cache write failed: " + ex.Message); }
    }

    private sealed class Cand
    {
        public uint Row;
        public string Cutb = "", Venue = "", Place = "", Quest = "";
        public uint Tt;
        public List<PropDto> Props = new();
        public bool IsCurated;
        public double Score;
    }

    private (FileDto Curated, FileDto Lab) Derive()
    {
        // TerritoryType by level folder (lowest row wins) + place names.
        var ttByDir = new Dictionary<string, (uint Tt, string Place)>(StringComparer.Ordinal);
        foreach (var t in data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!)
        {
            var bg = t.Bg.ToString();
            int li = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (li < 0) continue;
            ttByDir.TryAdd(bg[..li], ((uint)t.RowId, t.PlaceName.ValueNullable?.Name.ExtractText() ?? ""));
        }

        // Cutscene row -> quest name, from any quest script var whose name contains CUT (CUTSCENE_xx, LOC_NCUT_xx, ...).
        var questOf = new Dictionary<uint, string>();
        foreach (var q in data.GetExcelSheet<Lumina.Excel.Sheets.Quest>()!)
        {
            string? name = null;
            foreach (var qp in q.QuestParams)
            {
                if (qp.ScriptArg == 0) continue;
                var instr = qp.ScriptInstruction.ExtractText();
                if (instr.Length == 0 || !instr.Contains("CUT", StringComparison.Ordinal)) continue;
                name ??= q.Name.ExtractText();
                if (name.Length > 0) questOf.TryAdd(qp.ScriptArg, name);
            }
        }

        // Every cutscene that places enough props.
        var cands = new List<Cand>();
        foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.Cutscene>()!)
        {
            var cutb = row.Path.ExtractText();
            if (cutb.Length == 0 || !cutb.Contains('/')) continue;
            bool curated = Curated.ContainsKey(row.RowId);
            byte[]? bytes;
            try { bytes = data.GetFile("cut/" + cutb + ".cutb")?.Data; } catch { bytes = null; }
            if (bytes == null) continue;
            var file = new CutbFile(bytes);
            if (!file.IsValid) continue;
            var props = file.ReadProps();
            if (props.Count < MinProps && !curated) continue;
            var venue = file.DominantVenue(props);
            if (venue == null) continue;
            uint tt = ttByDir.TryGetValue(venue, out var ti) ? ti.Tt : 0;
            if (tt == 0 && !data.FileExists("bg/" + venue + "/level/bg.lgb")) continue;

            // Rank (orders the lab list; first of a same-place/quest pair keeps the plain name): props weighted by
            // variety, plus a bump for a real stage change (level parts it switches off) and set pieces.
            int families = props.Select(p => FamilyRx.Replace(p.Path[(p.Path.LastIndexOf('/') + 1)..], "")).Distinct().Count();
            int setPieces = props.Count(p => p.Path.EndsWith(".sgb", StringComparison.Ordinal));
            double score = Math.Round(props.Count * (0.5 + Math.Min(families, 12) / 12.0) + 0.02 * Math.Min(file.ReferencedPartCount(), 2000) + 3 * setPieces, 1);
            var (scales, curves) = file.SetupKeys();
            var c = new Cand
            {
                Row = row.RowId, Cutb = cutb, Venue = venue, Tt = tt, IsCurated = curated, Score = score,
                Place = tt != 0 ? ti.Place : "", Quest = questOf.GetValueOrDefault(row.RowId, ""),
            };
            foreach (var p in props)
            {
                var pos = p.Pos; var rot = p.Rot; var scale = p.Scale;
                if (curves.TryGetValue(p.Entry, out var srt))
                {
                    scale = srt.Scale;
                    if (Vector3.Distance(srt.Pos, pos) < CurveAgree) { pos = srt.Pos; rot = srt.RotRadians; }
                }
                else if (scales.TryGetValue(p.Entry, out var s)) scale *= s;
                c.Props.Add(new PropDto { Path = p.Path, Pos = A(pos), Rot = A(rot), Scale = A(scale) });
            }
            cands.Add(c);
        }

        // Collapse near-duplicates per venue (curated rows are always kept).
        var kept = new List<Cand>();
        foreach (var g in cands.GroupBy(c => c.Venue))
        {
            var reps = new List<Cand>();
            foreach (var c in g.OrderByDescending(c => c.IsCurated).ThenByDescending(c => c.Props.Count).ThenBy(c => c.Row))
            {
                if (!c.IsCurated && reps.Any(r => Jaccard(r.Props, c.Props) >= DedupeJaccard)) continue;
                reps.Add(c);
            }
            kept.AddRange(reps);
        }

        // Collision: housing-copy swap first, else the model's own .pcb.
        var housing = HousingIndex();
        var boundsCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pcbCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var c in kept)
            foreach (var p in c.Props)
            {
                if (!p.Path!.EndsWith(".mdl", StringComparison.Ordinal)) continue;
                if (p.Path.StartsWith("bgcommon/hou/", StringComparison.Ordinal)) { p.Path = SgbOf(p.Path); continue; }
                if (!boundsCache.TryGetValue(p.Path, out var key)) boundsCache[p.Path] = key = BoundsKey(p.Path, out _);
                if (key != null && housing.TryGetValue(key, out var h))
                {
                    var verts = VertexCount(p.Path);
                    var best = h.Where(x => Math.Abs(x.Verts - verts) <= Math.Max(12, verts * 0.02)).OrderBy(x => Math.Abs(x.Verts - verts)).ThenBy(x => x.Mdl, StringComparer.Ordinal).FirstOrDefault();
                    if (best.Mdl != null) { p.Path = SgbOf(best.Mdl); continue; }
                }
                int bp = p.Path.IndexOf("/bgparts/", StringComparison.Ordinal);
                if (bp < 0) continue;
                var pcb = p.Path[..bp] + "/collision/" + p.Path[(bp + 9)..^4] + ".pcb";
                if (!pcbCache.TryGetValue(pcb, out var exists)) pcbCache[pcb] = exists = data.FileExists(pcb);
                if (exists) p.Coll = pcb;
            }

        // Emit.
        var curatedOut = new FileDto { Dressings = new() };
        var labOut = new FileDto { Dressings = new() };
        var seen = new Dictionary<(string, string), int>();
        foreach (var c in kept.OrderByDescending(c => c.Score).ThenBy(c => c.Row))
        {
            var seg = c.Venue[(c.Venue.LastIndexOf('/') + 1)..];
            var d = new DressingDto
            {
                Row = c.Row, Cutb = c.Cutb, Quest = c.Quest, Bg = c.Venue + "/level/" + seg, Tt = c.Tt,
                HidesFromCutb = true, Props = c.Props,
            };
            if (c.Tt != 0 && c.Props.Count > 0)
                d.Spawn = SpawnOf(c.Props);
            if (c.IsCurated)
            {
                var (name, spawn) = Curated[c.Row];
                d.Name = name;
                if (spawn != null) d.Spawn = spawn;
                if (d.Spawn == null && c.Props.Count > 0) d.Spawn = SpawnOf(c.Props);
                curatedOut.Dressings.Add(d);
                continue;
            }
            var place = c.Place.Length > 0 ? c.Place : seg;
            var k = (place, c.Quest);
            seen[k] = seen.GetValueOrDefault(k) + 1;
            d.Name = seen[k] == 1 ? place : place + " (" + c.Cutb[(c.Cutb.LastIndexOf('/') + 1)..] + ")";
            labOut.Dressings.Add(d);
        }
        // b273: map edits (levels rebuilt from their own and a sibling level's parts) ride the same lab list.
        labOut.Dressings.AddRange(MapEdits.Derive(data, log));
        return (curatedOut, labOut);
    }

    // Housing furniture models by bounds key: indoor (HousingFurniture) + outdoor (HousingYardObject).
    private Dictionary<string, List<(string Mdl, int Verts)>> HousingIndex()
    {
        var idx = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
        void Add(string mdl)
        {
            var key = BoundsKey(mdl, out var verts);
            if (key == null) return;
            if (!idx.TryGetValue(key, out var l)) idx[key] = l = new();
            l.Add((mdl, verts));
        }
        foreach (var f in data.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>()!)
            if (f.ModelKey > 0) Add($"bgcommon/hou/indoor/general/{f.ModelKey:D4}/bgparts/fun_b0_m{f.ModelKey:D4}.mdl");
        foreach (var y in data.GetExcelSheet<Lumina.Excel.Sheets.HousingYardObject>()!)
            if (y.ModelKey > 0) Add($"bgcommon/hou/outdoor/general/{y.ModelKey:D4}/bgparts/gar_b0_m{y.ModelKey:D4}.mdl");
        return idx;
    }

    private string? BoundsKey(string mdl, out int verts)
    {
        verts = 0;
        try
        {
            if (!data.FileExists(mdl)) return null;
            var f = data.GetFile<MdlFile>(mdl);
            if (f == null) return null;
            var b = f.ModelBoundingBoxes;
            foreach (var m in f.Meshes) verts += (int)m.VertexCount;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{b.Min[0]:F3},{b.Min[1]:F3},{b.Min[2]:F3},{b.Max[0]:F3},{b.Max[1]:F3},{b.Max[2]:F3}");
        }
        catch { return null; }
    }

    private int VertexCount(string mdl) { BoundsKey(mdl, out var v); return v; }

    private static string SgbOf(string mdl)
    {
        int i = mdl.IndexOf("/bgparts/", StringComparison.Ordinal);
        return mdl[..i] + "/asset/" + mdl[(i + 9)..^4] + ".sgb";
    }

    private static double Jaccard(List<PropDto> a, List<PropDto> b)
    {
        var ca = a.GroupBy(p => p.Path).ToDictionary(g => g.Key!, g => g.Count());
        var cb = b.GroupBy(p => p.Path).ToDictionary(g => g.Key!, g => g.Count());
        int inter = 0, union = 0;
        foreach (var k in ca.Keys.Union(cb.Keys))
        {
            int x = ca.GetValueOrDefault(k), y = cb.GetValueOrDefault(k);
            inter += Math.Min(x, y); union += Math.Max(x, y);
        }
        return union == 0 ? 0 : (double)inter / union;
    }

    private static readonly Regex FamilyRx = new(@"_\d+[a-z]?\.(mdl|sgb)$", RegexOptions.Compiled);

    private static double Median(List<PropDto> props, int axis)
    {
        var v = props.Select(p => (double)p.Pos![axis]).OrderBy(x => x).ToList();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    }

    // Prop median + 2y (arrive standing among the scene, above floor-level props).
    private static float[] SpawnOf(List<PropDto> props) =>
        new[] { (float)Math.Round(Median(props, 0), 2), (float)Math.Round(Median(props, 1) + 2.0, 2), (float)Math.Round(Median(props, 2), 2) };

    private static float[] A(Vector3 v) => new[] { MathF.Round(v.X, 4), MathF.Round(v.Y, 4), MathF.Round(v.Z, 4) };
}
