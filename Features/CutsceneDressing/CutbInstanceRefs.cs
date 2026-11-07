using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using HMSync.World;

namespace HMSync.Features.CutsceneDressing;

/// <summary>
/// Reads a cutscene's instance-reference table straight from its .cutb: the level instances the scene switches off.
/// Format (decoded 2026-11-07, b261): u32 pairs (instanceId, flags) with flags = [0x00][assetType][layerId &amp; 0xFFFF].
/// The offline bake validates pairs against the level's LGBs; here they are taken as-is and only ever act on a LIVE
/// instance with that exact (layer, instance, type), so a stray match in other cutb data cannot hide anything real.
/// Used for the lab ("Cutscenes 2") dressings, which carry no baked hide lists.
/// </summary>
public static class CutbInstanceRefs
{
    private const uint MinInstanceId = 0x10000;   // LGB instance ids are large (z3e4 ~7.2M); skips small-int noise

    public static Dictionary<InstanceType, HashSet<ulong>> Read(IDataManager data, string cutbPath)
    {
        var result = new Dictionary<InstanceType, HashSet<ulong>>();
        byte[]? b;
        try { b = data.GetFile("cut/" + cutbPath + ".cutb")?.Data; }
        catch { b = null; }
        if (b == null) return result;
        for (int p = 0; p + 8 <= b.Length; p += 4)
        {
            uint id = BitConverter.ToUInt32(b, p);
            if (id < MinInstanceId) continue;
            uint flags = BitConverter.ToUInt32(b, p + 4);
            if ((flags & 0xFF) != 0) continue;
            ushort layer = (ushort)(flags >> 16);
            if (layer == 0) continue;
            InstanceType type;
            switch ((flags >> 8) & 0xFF)
            {
                case 1: type = InstanceType.BgPart; break;
                case 3: type = InstanceType.Light; break;
                case 4: type = InstanceType.Vfx; break;
                case 6: type = InstanceType.SharedGroup; break;
                default: continue;
            }
            if (!result.TryGetValue(type, out var set)) result[type] = set = new HashSet<ulong>();
            set.Add(LayoutInstanceVisibility.Key(layer, id));
        }
        return result;
    }
}
