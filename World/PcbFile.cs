using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace HMSync.World;

// ============================================================================
// PcbFile - read-only parser for a collision mesh (.pcb): vertices + triangles in the mesh's local space.
//
// WORLD-LAYER PRIMITIVE (pure parsing). Port of TerritoryViewer DumpCore.ParsePcb (per FFXIVClientStructs BGCollision
// Mesh.cs):
//   header 0x10 : version i32 @4 (1 | 4)
//   node   0x30 : child1 i32 @8 / child2 i32 @0xC (byte offsets from the node, 0 = none), AABB f32x6 @0x10,
//                 nCompressed u16 @0x28, nPrims u16 @0x2A, nRaw u16 @0x2C; then f32[3*nRaw], u16[3*nCompressed]
//                 (0..65535 lerp of the node AABB), then nPrims x 12 bytes { u8 v1, v2, v3, pad, u64 material }
// ============================================================================
public static class PcbFile
{
    public sealed class Mesh
    {
        public readonly List<Vector3> Verts = new();
        public readonly List<(int A, int B, int C)> Tris = new();
    }

    public static Mesh? Read(byte[]? d)
    {
        if (d == null || d.Length < 0x40) return null;
        int version = I32(d, 4);
        if (version != 1 && version != 4) return null;
        var m = new Mesh();
        var stack = new Stack<int>();
        stack.Push(0x10);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 100000)
        {
            int o = stack.Pop();
            if (o <= 0 || o + 0x30 > d.Length) continue;
            int c1 = I32(d, o + 8), c2 = I32(d, o + 0xC);
            var bmin = new Vector3(F32(d, o + 0x10), F32(d, o + 0x14), F32(d, o + 0x18));
            var bmax = new Vector3(F32(d, o + 0x1C), F32(d, o + 0x20), F32(d, o + 0x24));
            int nComp = U16(d, o + 0x28), nPrim = U16(d, o + 0x2A), nRaw = U16(d, o + 0x2C);
            int vbase = m.Verts.Count, pRaw = o + 0x30, pComp = pRaw + 12 * nRaw, pPrim = pComp + 6 * nComp;
            if (pPrim + 12 * nPrim > d.Length) continue;
            for (int i = 0; i < nRaw; i++)
                m.Verts.Add(new Vector3(F32(d, pRaw + 12 * i), F32(d, pRaw + 12 * i + 4), F32(d, pRaw + 12 * i + 8)));
            var scale = (bmax - bmin) / 65535f;
            for (int i = 0; i < nComp; i++)
                m.Verts.Add(bmin + scale * new Vector3(U16(d, pComp + 6 * i), U16(d, pComp + 6 * i + 2), U16(d, pComp + 6 * i + 4)));
            for (int i = 0; i < nPrim; i++)
            {
                int p = pPrim + 12 * i;
                m.Tris.Add((vbase + d[p], vbase + d[p + 1], vbase + d[p + 2]));
            }
            if (c1 != 0) stack.Push(o + c1);
            if (c2 != 0) stack.Push(o + c2);
        }
        return m;
    }

    /// <summary>World matrix for a layout transform (Euler radians, X then Y then Z; System.Numerics row-vector),
    /// the convention level collision is placed with (TerritoryViewer LocalMatrix; ColliderSpawner rotation).</summary>
    public static Matrix4x4 World(Vector3 pos, Vector3 rot, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(rot.X) * Matrix4x4.CreateRotationY(rot.Y) *
        Matrix4x4.CreateRotationZ(rot.Z) * Matrix4x4.CreateTranslation(pos);

    private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    private static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at, 4));
}
