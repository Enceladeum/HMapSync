using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace HMSync.World;

// ============================================================================
// LgbFile - read-only parser for a level layout file (.lgb) and a level's collision tile index (collision/list.pcb).
//
// WORLD-LAYER PRIMITIVE (pure parsing: takes bytes; no Dalamud, no HMS services). Layout as used by the research tools
// (b273) and cross-checked against live instance ids:
//   file      : "LGB1", u32 size, u32 count, "LGP1" @12, ... chunk header @20: [?, ?, layersOffset, layerCount] (i32,
//               offsets relative to @20)
//   layer     : u32 id, i32 nameOffset, i32 instancesOffset, i32 instanceCount (offsets relative to the layer)
//   instance  : i32 type, u32 id, i32 nameOffset, pos @+12, rot (Euler radians) @+24, scale @+36; type 1 (BgPart) and 6
//               (SharedGroup) carry an asset path offset @+48; a BgPart carries its collision mesh path offset @+52
//               (0 = none); an EventObject (45) carries u32 EObj row @+48 and u32 bound layout instance id @+52 (the
//               object it drives, e.g. the inn's door SharedGroup). All offsets relative to the instance.
//   list.pcb  : u32 count, 28 bytes header, then count x 32 bytes { u32 tileId, float min[3], float max[3], pad };
//               tile meshes live at collision/tr{id:D4}.pcb in world coordinates.
// ============================================================================
public static class LgbFile
{
    public readonly record struct Instance(string File, uint LayerId, string Layer, int Type, uint Id, Vector3 Pos, Vector3 Rot, Vector3 Scale,
        string Asset, string Collision, uint BaseId = 0, uint BoundId = 0);   // EventObject (45): EObj row + the layout instance it drives

    public readonly record struct Tile(uint Id, Vector3 Min, Vector3 Max);

    /// <summary>The LGB files a level folder can carry (absent ones are skipped by the caller).</summary>
    public static readonly string[] LevelFiles = { "bg", "planevent", "planlive", "planmap", "planner", "sound", "vfx" };

    /// <summary>Every instance in one .lgb. Empty for a missing/stub file (some levels ship LGBs with no layer chunk).</summary>
    public static List<Instance> Read(byte[]? b, string fileTag)
    {
        var list = new List<Instance>();
        if (b == null || b.Length < 36 || b[12] != (byte)'L' || b[13] != (byte)'G' || b[14] != (byte)'P' || b[15] != (byte)'1') return list;
        try
        {
            const int start = 20;
            int layOff = I32(b, start + 8), layCnt = I32(b, start + 12);
            int lbase = start + layOff;
            for (int li = 0; li < layCnt; li++)
            {
                int L = lbase + I32(b, lbase + 4 * li);
                uint lid = U32(b, L);
                string lname = Str(b, L + I32(b, L + 4));
                int ibase = L + I32(b, L + 8), icnt = I32(b, L + 12);
                for (int ii = 0; ii < icnt; ii++)
                {
                    int I = ibase + I32(b, ibase + 4 * ii);
                    int type = I32(b, I);
                    string asset = "", coll = "";
                    if (type == 1 || type == 6) asset = Str(b, I + I32(b, I + 48));
                    if (type == 1) { int co = I32(b, I + 52); if (co != 0) coll = Str(b, I + co); }
                    uint baseId = 0, bound = 0;
                    if (type == 45) { baseId = U32(b, I + 48); bound = U32(b, I + 52); }
                    list.Add(new Instance(fileTag, lid, lname, type, U32(b, I + 4), V3(b, I + 12), V3(b, I + 24), V3(b, I + 36), asset, coll, baseId, bound));
                }
            }
        }
        catch (ArgumentOutOfRangeException) { }   // truncated/odd file: keep what parsed
        return list;
    }

    /// <summary>Collision tile index of a level (collision/list.pcb).</summary>
    public static List<Tile> ReadTiles(byte[]? b)
    {
        var list = new List<Tile>();
        if (b == null || b.Length < 32) return list;
        int n = I32(b, 0);
        for (int i = 0; i < n && 32 + 32 * i + 28 <= b.Length; i++)
        {
            int o = 32 + 32 * i;
            list.Add(new Tile(U32(b, o), V3(b, o + 4), V3(b, o + 16)));
        }
        return list;
    }

    private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at, 4));
    private static Vector3 V3(byte[] b, int at) => new(F32(b, at), F32(b, at + 4), F32(b, at + 8));

    private static string Str(byte[] b, int at)
    {
        if (at < 0 || at >= b.Length) return "";
        int end = Array.IndexOf(b, (byte)0, at);
        if (end < 0) end = b.Length;
        return Encoding.UTF8.GetString(b, at, end - at);
    }
}
