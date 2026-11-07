using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace HMSync.World;

// ============================================================================
// CutbFile - read-only parser for a cutscene file (.cutb): the props it places, their setup-time scale/transform from
// the timeline, and the level folder it is staged in.
//
// WORLD-LAYER PRIMITIVE (pure parsing: no Dalamud, no HMS services; takes the file bytes). Formats, as decoded for the
// cutscene dressing work (b260-b268) and cross-checked against the engine's live scene (b267 probe):
//   container : "CUTB", u32 size, u32 count, then count x (fourcc, 0x10, u32 offset, u32 size)
//   CTAL      : scene-object table. An offset array (offs[0] == 4*count) points at entries that start with
//               u32 type (0xF|3) + u32 id (0xFF000000 | entry id). Props carry pos/rot(radians)/scale @+0xC/+0x18/+0x24
//               and a model path: u32 @+0xBC, string at entry + 0x8C + value.
//   TMLB      : timeline blocks (first block = setup). Nodes {tag, u32 size, u32 idField}; id = idField & 0xFFFF,
//               time = idField >> 16. TMAC binds a handle (0xFF000000|entry) to TMTR tracks (ref = id + 6), which list
//               C-nodes (ref = id + 2). C035 = uniform scale (float @+0x14). C013 = [dur, local id, TMFC id, 7]; the
//               TMFC = [offA, 9, 1, offB, 0] holds a full transform as 9 channels of 24-byte records ending at offB:
//               pos XYZ, rot XYZ (DEGREES), scale XYZ. (Valens desks: (1.0,1.3,1.3) / (1.5,1.3,1.5), as drawn.)
// ============================================================================
public sealed class CutbFile
{
    public readonly record struct Prop(uint Entry, string Path, Vector3 Pos, Vector3 Rot, Vector3 Scale);
    public readonly record struct Srt(Vector3 Pos, Vector3 RotRadians, Vector3 Scale);

    private readonly byte[] b;

    public CutbFile(byte[] data) => b = data;

    public bool IsValid => b.Length > 12 && b[0] == (byte)'C' && b[1] == (byte)'U' && b[2] == (byte)'T' && b[3] == (byte)'B';

    private uint U32(int at) => at >= 0 && at + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : 0;
    private ushort U16(int at) => at >= 0 && at + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : (ushort)0;
    private float F32(int at) => at >= 0 && at + 4 <= b.Length ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)) : 0f;
    private Vector3 V3(int at) => new(F32(at), F32(at + 4), F32(at + 8));

    private bool Section(string tag, out int off, out int size)
    {
        off = size = 0;
        if (!IsValid) return false;
        int n = (int)U32(8);
        for (int i = 0; i < n && 12 + i * 16 + 16 <= b.Length; i++)
        {
            int o = 12 + i * 16;
            if (Encoding.ASCII.GetString(b, o, 4) != tag) continue;
            off = (int)U32(o + 8); size = (int)U32(o + 12);
            return off > 0 && off < b.Length;
        }
        return false;
    }

    private string? CString(int at, int max = 256)
    {
        if (at < 0 || at >= b.Length) return null;
        int end = at;
        while (end < b.Length && end - at < max && b[end] != 0) { if (b[end] < 0x20 || b[end] >= 0x7f) return null; end++; }
        return end > at ? Encoding.ASCII.GetString(b, at, end - at) : null;
    }

    /// <summary>Props placed inline in the scene-object table, with their authored transform and model path.</summary>
    public List<Prop> ReadProps()
    {
        var props = new List<Prop>();
        if (!Section("CTAL", out var off, out var size)) return props;
        int end = Math.Min(b.Length, off + size);
        // Locate the entry offset array: u32 count*4 followed by increasing offsets whose targets look like entries.
        for (int p = 0; p < 0x400 && off + p + 8 <= end; p += 4)
        {
            uint c4 = U32(off + p);
            if (c4 % 4 != 0 || c4 < 8 || c4 > 16000 || off + p + c4 > end) continue;
            int cnt = (int)(c4 / 4);
            var offs = new int[cnt];
            bool inc = true;
            for (int k = 0; k < cnt; k++) { offs[k] = (int)U32(off + p + 4 * k); if (k > 0 && offs[k] <= offs[k - 1]) { inc = false; break; } }
            if (!inc) continue;
            int good = 0, probe = Math.Min(8, cnt);
            for (int k = 0; k < probe; k++)
            {
                int e = off + p + offs[k];
                uint t = U32(e);
                if ((t == 0xF || t == 3) && (U32(e + 4) >> 24) == 0xFF) good++;
            }
            if (good < Math.Min(6, probe)) continue;
            for (int k = 0; k < cnt; k++)
            {
                int e = off + p + offs[k];
                int esz = k + 1 < cnt ? offs[k + 1] - offs[k] : end - e;
                if (esz < 0xC0 || U32(e) != 0xF) continue;
                var path = CString(e + 0x8C + (int)U32(e + 0xBC));
                if (path == null || !path.StartsWith("bg", StringComparison.Ordinal)
                    || !(path.EndsWith(".mdl", StringComparison.Ordinal) || path.EndsWith(".sgb", StringComparison.Ordinal))) continue;
                var pos = V3(e + 0xC);
                // timeline-animated: no authored placement (parked at the origin, sometimes with float dust, e.g. -1e-4)
                if (MathF.Abs(pos.X) < 5e-4f && MathF.Abs(pos.Y) < 5e-4f && MathF.Abs(pos.Z) < 5e-4f) continue;
                props.Add(new Prop(U32(e + 4) & 0xFFFFFF, path, pos, V3(e + 0x18), V3(e + 0x24)));
            }
            break;
        }
        return props;
    }

    private readonly record struct Node(string Tag, int At, int Size, uint IdField);

    // Every "TMLB" occurrence in file order (the first is the setup block).
    private List<int> Tmlbs()
    {
        var list = new List<int>();
        var magic = "TMLB"u8;
        for (int p = 0; p < b.Length;)
        {
            int i = b.AsSpan(p).IndexOf(magic);
            if (i < 0) break;
            list.Add(p + i);
            p += i + 4;
        }
        return list;
    }

    private (List<Node> Nodes, Dictionary<int, Node> Ids) ParseTmlb(int at)
    {
        var nodes = new List<Node>();
        var ids = new Dictionary<int, Node>();
        int n = (int)U32(at + 8), p = at + 12;
        for (int i = 0; i < n && p + 12 <= b.Length; i++)
        {
            bool tagOk = true;
            for (int c = 0; c < 4; c++) { byte ch = b[p + c]; if (!((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z'))) { tagOk = false; break; } }
            if (!tagOk) break;
            int sz = (int)U32(p + 4);
            if (sz < 12 || sz > 0x10000) break;
            var node = new Node(Encoding.ASCII.GetString(b, p, 4), p, sz, U32(p + 8));
            nodes.Add(node);
            if (node.Tag != "TMAL") ids[(int)(node.IdField & 0xFFFF)] = node;
            p += sz;
        }
        return (nodes, ids);
    }

    // Every C-node bound to each object in the timeline block at `at`: entry -> list of C-nodes (node order).
    private Dictionary<uint, List<Node>> Bindings(int at, out Dictionary<int, Node> ids)
    {
        var map = new Dictionary<uint, List<Node>>();
        var (nodes, idx) = ParseTmlb(at);
        ids = idx;
        foreach (var t in nodes)
        {
            if (t.Tag != "TMAC" || t.Size < 28) continue;
            uint handle = U32(t.At + 0x10);
            if (handle >> 24 != 0xFF) continue;
            uint ent = handle & 0xFFFFFF;
            int tracks = t.At + 0x14 + (int)U32(t.At + 0x14), tcnt = (int)U32(t.At + 0x18);
            for (int i = 0; i < tcnt && i < 256; i++)
            {
                if (!idx.TryGetValue(U16(tracks + 2 * i) - 6, out var tm) || tm.Tag != "TMTR") continue;
                int entries = tm.At + 0xC + (int)U32(tm.At + 0xC), ecnt = (int)U32(tm.At + 0x10);
                for (int j = 0; j < ecnt && j < 256; j++)
                {
                    if (!idx.TryGetValue(U16(entries + 2 * j) - 2, out var c) || c.Tag[0] != 'C') continue;
                    if (!map.TryGetValue(ent, out var l)) map[ent] = l = new List<Node>();
                    l.Add(c);
                }
            }
        }
        return map;
    }

    /// <summary>Setup-time uniform scale per object (earliest C035 key over ALL timeline blocks, as baked and verified)
    /// and full transform per object (earliest C013 curve in the setup block only; later blocks are shot moves).</summary>
    public (Dictionary<uint, float> Scales, Dictionary<uint, Srt> Curves) SetupKeys()
    {
        var scales = new Dictionary<uint, float>();
        var curves = new Dictionary<uint, Srt>();
        var blocks = Tmlbs();
        if (blocks.Count == 0) return (scales, curves);
        var scaleTime = new Dictionary<uint, uint>();
        foreach (var at in blocks)
            foreach (var (ent, cn) in Bindings(at, out _))
                foreach (var c in cn)
                {
                    if (c.Tag != "C035" || c.Size < 24) continue;
                    uint time = c.IdField >> 16;
                    if (scaleTime.TryGetValue(ent, out var bt) && time >= bt) continue;
                    float s = F32(c.At + 0x14);
                    if (s > 0.01f && s < 100f) { scales[ent] = s; scaleTime[ent] = time; }
                }
        var bind = Bindings(blocks[0], out var ids);
        foreach (var (ent, cn) in bind)
        {
            uint bestCurveTime = uint.MaxValue;
            foreach (var c in cn)
            {
                uint time = c.IdField >> 16;
                if (c.Tag == "C013" && c.Size >= 28 && time < bestCurveTime)
                {
                    int target = (int)U32(c.At + 12 + 8);
                    if (U32(c.At + 12 + 12) != 7 || !ids.TryGetValue(target, out var f) || f.Tag != "TMFC" || f.Size < 32) continue;
                    if (U32(f.At + 12 + 4) != 9) continue;
                    int first = f.At + (int)U32(f.At + 12 + 12) - 8 * 24;
                    if (first < 0 || first + 8 * 24 + 4 > b.Length) continue;
                    var v = new float[9];
                    bool ok = true;
                    for (int i = 0; i < 9; i++) { v[i] = F32(first + 24 * i); if (!float.IsFinite(v[i]) || MathF.Abs(v[i]) > 1e5f) ok = false; }
                    if (!ok || v[6] <= 0.001f || v[7] <= 0.001f || v[8] <= 0.001f || v[6] > 100 || v[7] > 100 || v[8] > 100) continue;
                    const float d2r = MathF.PI / 180f;
                    curves[ent] = new Srt(new Vector3(v[0], v[1], v[2]), new Vector3(v[3] * d2r, v[4] * d2r, v[5] * d2r), new Vector3(v[6], v[7], v[8]));
                    bestCurveTime = time;
                }
            }
        }
        return (scales, curves);
    }

    /// <summary>Level BgParts the scene's instance-reference table names (unverified against the level): ranking input.</summary>
    public int ReferencedPartCount()
    {
        var seen = new HashSet<uint>();
        for (int i = 0; i + 8 <= b.Length; i += 4)
        {
            uint first = U32(i), second = U32(i + 4);
            if (first >= 0x10000 && (second & 0xFF) == 0 && ((second >> 8) & 0xFF) == 1 && (second >> 16) != 0) seen.Add(first);
        }
        return seen.Count;
    }

    /// <summary>The level folder the scene is staged in: the dominant "bg/&lt;dir&gt;/bgparts/" prefix among the level
    /// models it lists, excluding the props it places itself (<paramref name="props"/>) and collision meshes (a scene's
    /// own set can ship hundreds of .pcb pieces that would outvote the level it actually stands in).</summary>
    public string? DominantVenue(IReadOnlyCollection<Prop>? props = null)
    {
        var own = new HashSet<string>(props?.Select(p => p.Path) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int start = -1;
        for (int i = 0; i <= b.Length; i++)
        {
            bool ok = i < b.Length && b[i] >= 0x20 && b[i] < 0x7f;
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 10 && b[start] == (byte)'b' && b[start + 1] == (byte)'g' && b[start + 2] == (byte)'/')
            {
                var s = Encoding.ASCII.GetString(b, start, i - start);
                int k = s.IndexOf("/bgparts/", StringComparison.Ordinal);
                if (k > 3 && s.EndsWith(".mdl", StringComparison.Ordinal) && !own.Contains(s)) { var dir = s[3..k]; counts[dir] = counts.GetValueOrDefault(dir) + 1; }
            }
            start = -1;
        }
        return counts.Count == 0 ? null : counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;
    }
}
