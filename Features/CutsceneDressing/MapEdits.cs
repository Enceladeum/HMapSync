using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using HMSync.World;
using MdlFile = Lumina.Data.Files.MdlFile;
using static HMSync.Features.CutsceneDressing.DressingCatalog;

namespace HMSync.Features.CutsceneDressing;

// ============================================================================
// MapEdits - dressings that are not cutscenes: a level rebuilt from parts of itself and of a sibling level, derived from
// the installed game at ingest time (no baked numbers), and played through the same dressing path (spawn, hide,
// collision, peer sync by row id).
//
// First edit (b273): The For'ard Cabins (inn TT 1205, level y6ti) is a cut-down copy of Tuliyollal's pier (y6t1). Only
// the cottage players arrive in is furnished; the other three are the city's empty exterior shells, and none of the pier
// carries collision (only the inn floor is walkable). The edit:
//   cottages   anchors = the sibling level's cottage body model (y6t1_v1_room1) instances; the furnished cottage is the
//              anchor nearest the inn's own cottage models (layer "hotel"); every other anchor is a target, offset by the
//              anchor difference (all share one rotation, checked)
//   footprint  the union of the bounding boxes of the furnished cottage's own inn models (MdlFile), shifted per target
//   copy       every inn-only instance (layer not prefixed by the sibling level's code) inside the source footprint, unless
//              the same model already stands at the shifted spot; BgParts keep their own collision mesh
//   hide       sibling-layer instances inside a target footprint with no counterpart in the furnished cottage (the
//              exterior shell parts the inn model replaces)
//   collision  pier parts (the anchors' layer) that the city gives a .pcb and the inn does not: the city's mesh at the
//              inn's transform; pier parts the city walks on through its terrain instead: the city's collision tiles
//              that contain them
// ============================================================================
public static class MapEdits
{
    // Unbind: event objects (by EObjName) whose driven layout object is swapped for a free copy of itself (b274: the inn's
    // "wooden door" exit holds its door shut; the copies in the other cottages open on approach with no collision).
    // MainPier (b277): model-name prefix of the main pier; when set, the last cottage is also placed turned 180 degrees about
    // its connector's junction on that pier (a fifth cottage facing the open sea). Empty = no turned copy.
    private sealed record Spec(uint Row, uint Tt, string Level, string Sibling, string AnchorModel, string FootprintLayer, string Suffix, string[] Unbind, string MainPier);

    private static readonly Spec[] Specs =
    {
        new(900001, 1205, "ex5/02_ykt_y6/twn/y6ti", "ex5/02_ykt_y6/twn/y6t1", "y6t1_v1_room1.mdl", "hotel", "all cottages furnished", new[] { "wooden door" }, "y6t1_b0_pier"),
    };

    private const float Same = 0.1f;       // same model "already there": positions agree to 10 cm
    private const float Match = 0.05f;     // inn part == city part (copied verbatim, so they agree to float precision)

    public const string Quest = "Map edit";

    public static List<DressingDto> Derive(IDataManager data, IPluginLog log)
    {
        var outList = new List<DressingDto>();
        foreach (var s in Specs)
        {
            try { var d = DeriveOne(data, s, log); if (d != null) outList.Add(d); }
            catch (Exception ex) { log.Warning("[MapEdits] " + s.Row + " failed: " + ex.Message); }
        }
        return outList;
    }

    private static List<LgbFile.Instance> Level(IDataManager data, string level)
    {
        var all = new List<LgbFile.Instance>();
        foreach (var f in LgbFile.LevelFiles)
        {
            byte[]? b;
            try { b = data.GetFile($"bg/{level}/level/{f}.lgb")?.Data; } catch { b = null; }
            all.AddRange(LgbFile.Read(b, f));
        }
        return all;
    }

    private static string FileName(string p) => p[(p.LastIndexOf('/') + 1)..];

    private static Quaternion Q(Vector3 e) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
        Matrix4x4.CreateRotationX(e.X) * Matrix4x4.CreateRotationY(e.Y) * Matrix4x4.CreateRotationZ(e.Z)));   // = dressing RotMode 0

    private static DressingDto? DeriveOne(IDataManager data, Spec s, IPluginLog log)
    {
        var inn = Level(data, s.Level);
        var city = Level(data, s.Sibling);
        if (inn.Count == 0 || city.Count == 0) { log.Warning("[MapEdits] " + s.Row + ": level data missing"); return null; }
        string prefix = FileName(s.Sibling) + "_";

        var anchors = city.Where(x => x.Type == 1 && FileName(x.Asset) == s.AnchorModel).OrderBy(x => x.Pos.Z).ThenBy(x => x.Pos.X).ToList();
        var foot = inn.Where(x => x.Type == 1 && x.Layer == s.FootprintLayer && x.File == "bg").ToList();
        if (anchors.Count < 2 || foot.Count == 0) { log.Warning("[MapEdits] " + s.Row + ": anchors/footprint not found"); return null; }
        string anchorLayer = anchors[0].Layer;

        // Footprint (XZ) of the furnished cottage: its own models' bounds, rotated into place.
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var m in foot)
        {
            MdlFile? f = null;
            try { f = data.GetFile<MdlFile>(m.Asset); } catch { }
            if (f == null) continue;
            var bb = f.ModelBoundingBoxes;
            var q = Q(m.Rot);
            for (int c = 0; c < 8; c++)
            {
                var local = new Vector3((c & 1) != 0 ? bb.Max[0] : bb.Min[0], (c & 2) != 0 ? bb.Max[1] : bb.Min[1], (c & 4) != 0 ? bb.Max[2] : bb.Min[2]) * m.Scale;
                var w = Vector3.Transform(local, q) + m.Pos;
                x0 = MathF.Min(x0, w.X); x1 = MathF.Max(x1, w.X); z0 = MathF.Min(z0, w.Z); z1 = MathF.Max(z1, w.Z);
            }
        }
        if (x0 > x1) { log.Warning("[MapEdits] " + s.Row + ": footprint models unreadable"); return null; }
        bool Inside(Vector3 p, Vector3 off) => p.X >= x0 + off.X && p.X <= x1 + off.X && p.Z >= z0 + off.Z && p.Z <= z1 + off.Z;

        var center = new Vector3((x0 + x1) / 2, 0, (z0 + z1) / 2);
        var src = anchors.OrderBy(a => Vector2.Distance(new(a.Pos.X, a.Pos.Z), new(center.X, center.Z))).First();
        var placeable = inn.Where(x => x.File == "bg" && (x.Type == 1 || x.Type == 6)).ToList();
        var copy = placeable.Where(x => !x.Layer.StartsWith(prefix, StringComparison.Ordinal) && Inside(x.Pos, Vector3.Zero)).ToList();
        var srcPool = placeable.Where(x => Inside(x.Pos, Vector3.Zero)).ToList();
        bool Has(string asset, Vector3 p, List<LgbFile.Instance> pool) => pool.Any(y => y.Asset == asset && Vector3.Distance(y.Pos, p) < Same);

        var props = new List<PropDto>();
        var hides = new List<uint[]>();
        var hidden = new HashSet<uint>();
        int targets = 0;
        foreach (var a in anchors)
        {
            if (a.Id == src.Id) continue;
            if (Vector3.Distance(a.Rot, src.Rot) > 1e-3f) { log.Warning("[MapEdits] " + s.Row + ": cottage at " + a.Pos + " is rotated differently, skipped"); continue; }
            var off = a.Pos - src.Pos;
            var tgt = placeable.Where(x => Inside(x.Pos, off)).ToList();
            foreach (var x in copy)
            {
                var p = x.Pos + off;
                if (Has(x.Asset, p, tgt)) continue;
                props.Add(new PropDto { Path = x.Asset, Pos = A(p), Rot = A(x.Rot), Scale = A(x.Scale), Coll = x.Type == 1 && x.Collision.Length > 0 ? x.Collision : null });
            }
            foreach (var x in tgt)
                if (x.Layer.StartsWith(prefix, StringComparison.Ordinal) && !Has(x.Asset, x.Pos - off, srcPool) && hidden.Add(x.Id))
                    hides.Add(new[] { x.Id, x.LayerId & 0xFFFF, (uint)x.Type });
            targets++;
        }

        // Lights: every level light inside the furnished cottage, copied (live, at activation) to each target.
        var srcLights = inn.Where(x => x.Type == 3 && Inside(x.Pos, Vector3.Zero)).ToList();
        var lightCopies = new List<LightDto>();
        foreach (var a in anchors)
            if (a.Id != src.Id && Vector3.Distance(a.Rot, src.Rot) <= 1e-3f)
                foreach (var l in srcLights) lightCopies.Add(new LightDto { Source = l.Id, Offset = A(a.Pos - src.Pos) });

        // Unbind: hide the event-driven object, place a free copy of it on the same spot.
        var eobjNames = data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
        int unbound = 0;
        foreach (var e in inn.Where(x => x.Type == 45 && x.BoundId != 0 && Inside(x.Pos, Vector3.Zero)))
        {
            var nm = eobjNames?.GetRowOrDefault(e.BaseId)?.Singular.ExtractText() ?? "";
            if (!s.Unbind.Any(u => string.Equals(u, nm, StringComparison.OrdinalIgnoreCase))) continue;
            var b = inn.FirstOrDefault(x => x.Id == e.BoundId && (x.Type == 1 || x.Type == 6));
            if (b.Asset == null || b.Asset.Length == 0 || !hidden.Add(b.Id)) continue;
            hides.Add(new[] { b.Id, b.LayerId & 0xFFFF, (uint)b.Type });
            props.Add(new PropDto { Path = b.Asset, Pos = A(b.Pos), Rot = A(b.Rot), Scale = A(b.Scale), Coll = b.Type == 1 && b.Collision.Length > 0 ? b.Collision : null });
            unbound++;
        }

        // Pier collision.
        var cityColl = city.Where(x => x.Type == 1 && x.Collision.Length > 0).GroupBy(x => x.Asset).ToDictionary(g => g.Key, g => g.ToList());
        var needTerrain = new List<LgbFile.Instance>();
        int partColl = 0;
        foreach (var x in inn.Where(x => x.Type == 1 && x.Layer == anchorLayer && x.Collision.Length == 0 && !hidden.Contains(x.Id)))
        {
            var m = cityColl.TryGetValue(x.Asset, out var l) ? l.FirstOrDefault(y => Vector3.Distance(y.Pos, x.Pos) < Match) : default;
            if (m.Asset != null) { props.Add(new PropDto { Path = "", Pos = A(x.Pos), Rot = A(x.Rot), Scale = A(x.Scale), Coll = m.Collision }); partColl++; }
            else needTerrain.Add(x);
        }
        // Parts the city walks on through its TERRAIN (bridge planks without a mesh of their own): b273-b278 added the city's
        // terrain tiles that contain them, but those tiles also carry the city's invisible boundary walls (b279: tr1323 has
        // a 40y one-sided wall on the pier's east edge, which blocked the fifth cottage's bridge one way). Their floor is
        // now built like the decks, from the planks' drawn surfaces; no city terrain is added.
        int tiles = 0;
        var stripParts = inn.Where(x => x.Type == 1 && x.Layer == anchorLayer && !hidden.Contains(x.Id)).ToList();
        var strip = stripParts.Select(x => BoundsXZ(data, x)).Where(b => b.HasValue).Select(b => b!.Value).ToList();
        if (strip.Count > 0)
        {
            float px0 = strip.Min(r => r.X0), px1 = strip.Max(r => r.X1), pz0 = strip.Min(r => r.Z0), pz1 = strip.Max(r => r.Z1);
            byte[]? list;
            try { list = data.GetFile($"bg/{s.Sibling}/collision/list.pcb")?.Data; } catch { list = null; }
            var terrain = LgbFile.ReadTiles(list)
                .Where(t => t.Min.X <= px1 && t.Max.X >= px0 && t.Min.Z <= pz1 && t.Max.Z >= pz0)
                .Select(t => $"bg/{s.Sibling}/collision/tr{t.Id:D4}.pcb").Where(p => data.FileExists(p))
                .Select(p => new LgbFile.Instance("terrain", 0, "", 1, 0, Vector3.Zero, Vector3.Zero, Vector3.One, "", p)).ToList();
            var ownMeshes = stripParts.Where(x => cityColl.ContainsKey(x.Asset))
                .Select(x => x with { Collision = cityColl[x.Asset].FirstOrDefault(y => Vector3.Distance(y.Pos, x.Pos) < Match).Collision ?? "" })
                .Where(x => x.Collision.Length > 0);
            foreach (var (c, h) in StripBoxes(data, terrain, needTerrain, ownMeshes, strip, px0, pz0, px1, pz1, src.Pos.Y))
            { props.Add(new PropDto { Path = "", Box = true, Pos = A(c), Rot = new float[3], Scale = A(h) }); tiles++; }
            log.Information($"[MapEdits] {s.Row}: promenade floor grafted from {terrain.Count} city terrain tile(s) (walkable surfaces only) inside {strip.Count} pier/bridge part extents");
        }

        // b276/b277 decks: the inn's cottage collision (its interior model's .pcb) misses much of the deck its exterior model
        // DRAWS. The missing floor is rebuilt as thin boxes whose tops sit on the drawn surface. b276 borrowed the city's
        // cottage collision instead; the inn builds its cottage ~2.9y off the city's centre, so that floor sat beside the
        // drawn steps (b277: drawn surface = the source of truth).
        int decks = 0;
        var deckBoxes = DeckBoxes(data, foot, x0, z0, x1, z1, src.Pos.Y);
        foreach (var a in anchors)
        {
            if (Vector3.Distance(a.Rot, src.Rot) > 1e-3f) continue;
            var off = a.Pos - src.Pos;
            foreach (var (c, h) in deckBoxes)
            { props.Add(new PropDto { Path = "", Box = true, Pos = A(c + off), Rot = new float[3], Scale = A(h) }); decks++; }
        }

        // b277 fifth cottage: the LAST cottage as it now stands (the furnished copy, the parts it kept, and its connector up
        // to the main pier's centreline) turned 180 degrees about the connector's junction on the pier: same junction,
        // deck to the open sea on the far side. Collision travels with it: the parts' own meshes, the deck boxes, the city
        // meshes of connector parts, and drawn-floor boxes for planks the city walks via terrain (none over open water).
        int turned = 0;
        if (s.MainPier.Length > 0)
        {
            var same = anchors.Where(a => Vector3.Distance(a.Rot, src.Rot) <= 1e-3f).ToList();
            var last = same.OrderBy(a => a.Pos.Z).Last();
            var offL = last.Pos - src.Pos;
            var pierParts = inn.Where(x => x.Type == 1 && x.Layer == anchorLayer && FileName(x.Asset).StartsWith(s.MainPier, StringComparison.Ordinal)).ToList();
            var pier = pierParts.Select(x => x.Pos.X).OrderBy(v => v).ToList();
            if (pier.Count == 0) log.Warning("[MapEdits] " + s.Row + ": main pier (" + s.MainPier + "*) not found, no turned cottage");
            else
            {
                // The pier models' ORIGIN runs along the pier's west edge (b278: their meshes extend 7.7y east of it), so the
                // junction pieces are found at the origin line, but the turn is about the pier's true centreline: the middle
                // of the pier models' drawn extent. (b277 turned about the origin line: the copied junction piece then lay
                // across the whole pier as an invisible wall, and landed on the original junction pieces.)
                float axisX = pier[pier.Count / 2];
                var pierSpans = pierParts.Select(x => BoundsXZ(data, x)).Where(b => b.HasValue).Select(b => b!.Value).ToList();
                var mids = pierSpans.Select(b => (b.X0 + b.X1) / 2).OrderBy(v => v).ToList();
                float centreX = mids.Count > 0 ? mids[mids.Count / 2] : axisX;
                int Nearest(Vector3 p) { int bi = 0; float bd = float.MaxValue; for (int k = 0; k < anchors.Count; k++) { float dd = Vector2.Distance(new(anchors[k].Pos.X, anchors[k].Pos.Z), new(p.X, p.Z)); if (dd < bd) { bd = dd; bi = k; } } return bi; }
                int li = anchors.IndexOf(last);
                // The connector = the parts standing within the drawn extent of the bridge that meets the pier (the junction
                // pieces on the pier's centreline), between the cottage and the pier.
                var cand = inn.Where(x => x.File == "bg" && (x.Type == 1 || x.Type == 6) && x.Layer == anchorLayer && !hidden.Contains(x.Id)
                    && !FileName(x.Asset).StartsWith(s.MainPier, StringComparison.Ordinal)
                    && x.Pos.X > x1 + offL.X && x.Pos.X <= axisX + 0.5f && Nearest(x.Pos) == li).ToList();
                var junction = cand.Where(x => MathF.Abs(x.Pos.X - axisX) < 1f).ToList();
                var spans = junction.Select(j => BoundsXZ(data, j)).Where(bb => bb.HasValue).Select(bb => bb!.Value).ToList();
                var connector = cand.Where(x => spans.Any(bb => x.Pos.X >= bb.X0 - 1 && x.Pos.X <= bb.X1 + 1 && x.Pos.Z >= bb.Z0 - 1 && x.Pos.Z <= bb.Z1 + 1)).ToList();
                float pz = junction.Count > 0 ? junction.Average(x => x.Pos.Z) : connector.Count > 0 ? connector.Average(x => x.Pos.Z) : last.Pos.Z;
                var pivot = new Vector3(centreX, 0, pz);
                Vector3 Turn(Vector3 p) => new(2 * pivot.X - p.X, p.Y, 2 * pivot.Z - p.Z);
                var copyLayers = copy.Select(x => x.Layer).ToHashSet();
                var kept = placeable.Where(x => Inside(x.Pos, offL) && !hidden.Contains(x.Id) && (x.Layer == anchorLayer || copyLayers.Contains(x.Layer))).ToList();
                string CityColl(LgbFile.Instance x) =>
                    x.Type == 1 && cityColl.TryGetValue(x.Asset, out var l) ? l.FirstOrDefault(y => Vector3.Distance(y.Pos, x.Pos) < Match).Collision ?? "" : "";
                void Add(string asset, Vector3 pos, Vector3 rot, Vector3 scale, string coll)
                {
                    props.Add(new PropDto { Path = asset, Pos = A(Turn(pos)), Rot = A(TurnEuler(rot)), Scale = A(scale), Coll = coll.Length > 0 ? coll : null });
                    turned++;
                }
                foreach (var x in copy) Add(x.Asset, x.Pos + offL, x.Rot, x.Scale, x.Type == 1 ? x.Collision : "");
                var bare = new List<LgbFile.Instance>();
                foreach (var x in kept.Concat(connector))
                {
                    var c = x.Collision.Length > 0 ? x.Collision : CityColl(x);
                    Add(x.Asset, x.Pos, x.Rot, x.Scale, c);
                    if (c.Length == 0 && x.Type == 1) bare.Add(x);
                }
                foreach (var (c, h) in deckBoxes)
                { props.Add(new PropDto { Path = "", Box = true, Pos = A(Turn(c + offL)), Rot = new float[3], Scale = A(h) }); decks++; }
                if (bare.Count > 0)
                {
                    float bx0 = bare.Min(x => x.Pos.X) - 12, bx1 = bare.Max(x => x.Pos.X) + 12, bz0 = bare.Min(x => x.Pos.Z) - 12, bz1 = bare.Max(x => x.Pos.Z) + 12;
                    foreach (var (c, h) in DeckBoxes(data, bare, bx0, bz0, bx1, bz1, src.Pos.Y))
                    { props.Add(new PropDto { Path = "", Box = true, Pos = A(Turn(c)), Rot = new float[3], Scale = A(h) }); decks++; }
                }
                foreach (var l in srcLights)
                    lightCopies.Add(new LightDto { Source = l.Id, Offset = A(offL), Yaw = MathF.PI, Pivot = A(pivot) });
                log.Information($"[MapEdits] {s.Row}: turned copy of the cottage at z {last.Pos.Z:F1} about the pier junction ({pivot.X:F2}, {pivot.Z:F2}): " +
                                $"{connector.Count} connector part(s), {bare.Count} plank(s) floored by drawn-surface boxes");
            }
        }

        string place = data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(s.Tt)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
        string seg = FileName(s.Level);
        log.Information($"[MapEdits] {s.Row} '{place}': {targets} cottage(s) furnished ({props.Count - partColl - tiles - unbound - decks - turned} placed, {hides.Count - unbound} shell part(s) hidden), " +
                        $"{lightCopies.Count} light copies, {unbound} event object(s) unbound, {decks} deck floor box(es), {turned} part(s) in the turned cottage, pier collision {partColl} part mesh(es) + {tiles} promenade floor box(es)");
        return new DressingDto
        {
            Row = s.Row, Name = (place.Length > 0 ? place : seg) + " (" + s.Suffix + ")", Cutb = "", Quest = Quest,
            Bg = s.Level + "/level/" + seg, Tt = s.Tt, Hides = hides, Props = props, HidesFromCutb = false, Lights = lightCopies,
            ExactHides = true,   // b278: hides are derived exactly here; the live "level copy of a re-placed prop" scan must not run
        };
    }

    private const float DeckRes = 0.25f;        // floor raster cell (yalms)
    private const float DeckThick = 0.25f;      // box half-height below the floor surface

    // A part's collision mesh (.pcb) or its drawn model (.mdl, LOD0), as vertices + triangles in local space.
    private static (List<Vector3> Verts, List<(int A, int B, int C)> Tris)? Collision(IDataManager data, LgbFile.Instance part)
    {
        try { var m = PcbFile.Read(data.GetFile(part.Collision)?.Data); return m == null ? null : (m.Verts, m.Tris); } catch { return null; }
    }

    private static (List<Vector3> Verts, List<(int A, int B, int C)> Tris)? Drawn(IDataManager data, LgbFile.Instance part)
    {
        try
        {
            var f = data.GetFile<MdlFile>(part.Asset);
            if (f == null) return null;
            var model = new Lumina.Models.Models.Model(f, Lumina.Models.Models.Model.ModelLod.High);
            var verts = new List<Vector3>(); var tris = new List<(int, int, int)>();
            foreach (var mesh in model.Meshes)
            {
                int b = verts.Count;
                foreach (var v in mesh.Vertices) { var q = v.Position ?? default; verts.Add(new Vector3(q.X, q.Y, q.Z)); }
                var idx = mesh.Indices;
                for (int i = 0; i + 2 < idx.Length; i += 3) tris.Add((b + idx[i], b + idx[i + 1], b + idx[i + 2]));
            }
            return (verts, tris);
        }
        catch { return null; }
    }

    // Floor cells (top surface height per cell, NaN = none) of the parts' meshes within a band around the deck height.
    private static float[,] FloorRaster(IDataManager data, IEnumerable<LgbFile.Instance> parts, bool drawn, float x0, float z0, int nx, int nz, float deckY, float above)
    {
        var top = new float[nx, nz];
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++) top[i, j] = float.NaN;
        foreach (var part in parts)
        {
            var m = drawn ? Drawn(data, part) : Collision(data, part);
            if (m == null) continue;
            var w = PcbFile.World(part.Pos, part.Rot, part.Scale);
            var v = m.Value.Verts.ConvertAll(p => Vector3.Transform(p, w));
            foreach (var (ia, ib, ic) in m.Value.Tris)
            {
                if (ia >= v.Count || ib >= v.Count || ic >= v.Count) continue;
                Vector3 a = v[ia], b = v[ib], c = v[ic];
                var n = Vector3.Cross(b - a, c - a);
                float len = n.Length();
                if (len < 1e-9f || MathF.Abs(n.Y / len) <= 0.7f) continue;               // walkable surfaces only
                float my = (a.Y + b.Y + c.Y) / 3;
                if (my < deckY - 1.5f || my > deckY + above) continue;                   // the deck band (steps go down)
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (MathF.Abs(d) < 1e-9f) continue;
                int i0 = Math.Max(0, (int)((MathF.Min(a.X, MathF.Min(b.X, c.X)) - x0) / DeckRes));
                int i1 = Math.Min(nx - 1, (int)((MathF.Max(a.X, MathF.Max(b.X, c.X)) - x0) / DeckRes));
                int j0 = Math.Max(0, (int)((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - z0) / DeckRes));
                int j1 = Math.Min(nz - 1, (int)((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - z0) / DeckRes));
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        float X = x0 + (i + 0.5f) * DeckRes, Z = z0 + (j + 0.5f) * DeckRes;
                        float l1 = ((b.Z - c.Z) * (X - c.X) + (c.X - b.X) * (Z - c.Z)) / d;
                        float l2 = ((c.Z - a.Z) * (X - c.X) + (a.X - c.X) * (Z - c.Z)) / d;
                        if (l1 < -1e-4f || l2 < -1e-4f || 1 - l1 - l2 < -1e-4f) continue;
                        float y = l1 * a.Y + l2 * b.Y + (1 - l1 - l2) * c.Y;
                        if (float.IsNaN(top[i, j]) || y > top[i, j]) top[i, j] = y;
                    }
            }
        }
        return top;
    }

    // Floor the cottage DRAWS and its own collision does not offer, as boxes (centre, half extents): cells grouped by
    // height (cm), merged greedily into rectangles; grown one cell into the existing floor to close seams.
    private static List<(Vector3 Center, Vector3 Half)> DeckBoxes(IDataManager data, IEnumerable<LgbFile.Instance> own,
        float x0, float z0, float x1, float z1, float deckY)
    {
        int nx = (int)MathF.Ceiling((x1 - x0) / DeckRes), nz = (int)MathF.Ceiling((z1 - z0) / DeckRes);
        var parts = own.ToList();
        // drawn floor: deck level and the steps below it (higher drawn surfaces are railing caps, sills, the roof);
        // already walkable: anything the cottage's own collision offers up to 2y above the deck (built-in seats, bed base)
        var want = FloorRaster(data, parts, true, x0, z0, nx, nz, deckY, 0.5f);
        var have = FloorRaster(data, parts.Where(x => x.Collision.Length > 0), false, x0, z0, nx, nz, deckY, 2f);
        return Boxes(want, have, x0, z0, nx, nz);
    }

    // b280: the promenade. The city's pier floor lives in its TERRAIN tiles (the pier models' own meshes are only posts and
    // rails), and those tiles also carry invisible one-sided boundary walls (tr1323: 40y on the pier's east edge). Graft
    // only the terrain's walkable surfaces, only inside the promenade strip (the drawn extents of the pier/bridge parts),
    // as boxes at the terrain's own heights: the original walkable shape, without its walls. Where the city has no terrain
    // (the turned bridge over open water) the parts' drawn surface is used.
    private static List<(Vector3 Center, Vector3 Half)> StripBoxes(IDataManager data, IEnumerable<LgbFile.Instance> terrain,
        IEnumerable<LgbFile.Instance> drawnParts, IEnumerable<LgbFile.Instance> ownMeshes, List<(float X0, float Z0, float X1, float Z1)> strip,
        float x0, float z0, float x1, float z1, float deckY)
    {
        int nx = (int)MathF.Ceiling((x1 - x0) / DeckRes), nz = (int)MathF.Ceiling((z1 - z0) / DeckRes);
        var terr = FloorRaster(data, terrain, false, x0, z0, nx, nz, deckY, 0.5f);
        var drawn = FloorRaster(data, drawnParts, true, x0, z0, nx, nz, deckY, 0.5f);
        var have = FloorRaster(data, ownMeshes, false, x0, z0, nx, nz, deckY, 2f);
        var want = new float[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                float X = x0 + (i + 0.5f) * DeckRes, Z = z0 + (j + 0.5f) * DeckRes;
                bool inStrip = false;
                foreach (var r in strip) if (X >= r.X0 && X <= r.X1 && Z >= r.Z0 && Z <= r.Z1) { inStrip = true; break; }
                want[i, j] = !inStrip ? float.NaN : !float.IsNaN(terr[i, j]) ? terr[i, j] : drawn[i, j];
            }
        return Boxes(want, have, x0, z0, nx, nz);
    }

    // Cells to floor = wanted and not already walkable, grown one cell into existing floor (seams); heights clustered;
    // greedy rectangles per level; each a thin box whose top is the level.
    private static List<(Vector3 Center, Vector3 Half)> Boxes(float[,] want, float[,] have, float x0, float z0, int nx, int nz)
    {
        var need = new bool[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                need[i, j] = !float.IsNaN(want[i, j]) && float.IsNaN(have[i, j]);
        var grown = (bool[,])need.Clone();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                if (!need[i, j] && !float.IsNaN(want[i, j]) && !float.IsNaN(have[i, j]) &&
                    ((i > 0 && need[i - 1, j]) || (i + 1 < nx && need[i + 1, j]) || (j > 0 && need[i, j - 1]) || (j + 1 < nz && need[i, j + 1])))
                    grown[i, j] = true;
        var boxes = new List<(Vector3, Vector3)>();
        var used = new bool[nx, nz];
        // Heights in cm, clustered: values chaining within 3 cm are one level (plank surfaces vary by a cm or two), and the
        // level's box top is its highest value, so nothing drawn sits above the floor by more than ~3 cm.
        var levels = new SortedSet<int>();
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++) if (grown[i, j]) levels.Add((int)MathF.Round(want[i, j] * 100));
        var level = new Dictionary<int, int>();
        var chain = new List<int>();
        void Close() { foreach (var c in chain) level[c] = chain[^1]; chain.Clear(); }
        foreach (var v in levels) { if (chain.Count > 0 && v - chain[^1] > 3) Close(); chain.Add(v); }
        Close();
        int H(int i, int j) => level[(int)MathF.Round(want[i, j] * 100)];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                if (!grown[i, j] || used[i, j]) continue;
                int h = H(i, j), je = j, ie = i;
                while (je + 1 < nz && grown[i, je + 1] && !used[i, je + 1] && H(i, je + 1) == h) je++;
                while (ie + 1 < nx)
                {
                    bool ok = true;
                    for (int k = j; k <= je && ok; k++) ok = grown[ie + 1, k] && !used[ie + 1, k] && H(ie + 1, k) == h;
                    if (!ok) break;
                    ie++;
                }
                for (int q = i; q <= ie; q++) for (int k = j; k <= je; k++) used[q, k] = true;
                float topY = h / 100f;
                var half = new Vector3((ie - i + 1) * DeckRes / 2, DeckThick, (je - j + 1) * DeckRes / 2);
                var center = new Vector3(x0 + (i + ie + 1) * DeckRes / 2, topY - DeckThick, z0 + (j + je + 1) * DeckRes / 2);
                boxes.Add((center, half));
            }
        return boxes;
    }

    // A layout Euler (X then Y then Z) followed by a half turn about Y, as a layout Euler again. The two forms the levels
    // use (yaw only; (pi, b, pi) = yaw pi - b) are mapped exactly; anything else goes through the matrix.
    private static Vector3 TurnEuler(Vector3 e)
    {
        static bool Zero(float v) => MathF.Abs(v) < 1e-3f;
        static bool Pi(float v) => MathF.Abs(MathF.Abs(v) - MathF.PI) < 1e-3f;
        static float Wrap(float v) { while (v > MathF.PI) v -= 2 * MathF.PI; while (v < -MathF.PI) v += 2 * MathF.PI; return v; }
        if (Zero(e.X) && Zero(e.Z)) return new Vector3(0, Wrap(e.Y + MathF.PI), 0);
        if (Pi(e.X) && Pi(e.Z)) return new Vector3(0, Wrap(-e.Y), 0);
        var m = Matrix4x4.CreateRotationX(e.X) * Matrix4x4.CreateRotationY(e.Y) * Matrix4x4.CreateRotationZ(e.Z) * Matrix4x4.CreateRotationY(MathF.PI);
        return new Vector3(MathF.Atan2(m.M23, m.M33), MathF.Asin(Math.Clamp(-m.M13, -1f, 1f)), MathF.Atan2(m.M12, m.M11));
    }

    // World XZ bounds of a part's drawn model (its MdlFile bounding box through the part's transform).
    private static (float X0, float Z0, float X1, float Z1)? BoundsXZ(IDataManager data, LgbFile.Instance m)
    {
        MdlFile? f = null;
        try { f = data.GetFile<MdlFile>(m.Asset); } catch { }
        if (f == null) return null;
        var bb = f.ModelBoundingBoxes;
        var w = PcbFile.World(m.Pos, m.Rot, m.Scale);
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        for (int c = 0; c < 8; c++)
        {
            var p = Vector3.Transform(new Vector3((c & 1) != 0 ? bb.Max[0] : bb.Min[0], (c & 2) != 0 ? bb.Max[1] : bb.Min[1], (c & 4) != 0 ? bb.Max[2] : bb.Min[2]), w);
            x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); z0 = MathF.Min(z0, p.Z); z1 = MathF.Max(z1, p.Z);
        }
        return (x0, z0, x1, z1);
    }

    private static float[] A(Vector3 v) => new[] { MathF.Round(v.X, 4), MathF.Round(v.Y, 4), MathF.Round(v.Z, 4) };
}
