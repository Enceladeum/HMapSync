using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using HMSync.World;

namespace HMSync.Features.CutsceneDressing;

/// <summary>One prop a cutscene places: asset path + authored world transform (rotation as Euler radians X,Y,Z).</summary>
public sealed record DressingProp(string Path, Vector3 Pos, Vector3 RotEuler, Vector3 Scale, string? Coll = null);   // b269: Coll = the model's own .pcb, if the game ships one

/// <summary>
/// A cutscene's set dressing, keyed by its Cutscene sheet row: the level instances it switches off, per instance type
/// (keys from LayoutInstanceVisibility.Key), and the props it places. HideOther counts referenced instances of types
/// not handled (e.g. ambient sound), for logging. Spawn, when set, overrides the map's default arrival point (big field
/// maps). The stage to load it on is chosen by the catalog entry that references the row.
/// </summary>
public sealed record Dressing(uint Row, string Name, string Cutb, IReadOnlyDictionary<InstanceType, HashSet<ulong>> Hides,
    IReadOnlyDictionary<string, int> HideOther, IReadOnlyList<DressingProp> Props, Vector3? Spawn, bool HidesFromCutb = false)
{
    public int HideCount => Hides.Values.Sum(s => s.Count);
}

/// <summary>A lab ("Cutscenes 2") entry: where the dressing loads, for building its chip. Bg as Stage.Bg (no "bg/").</summary>
public sealed record LabEntry(uint Row, string Name, string Quest, string Bg, uint TerritoryId);

/// <summary>
/// Loads the embedded cutscene-dressings.json (derived offline from the game data).
/// Best-effort: a missing or garbled resource yields an empty catalog, never a throw.
/// </summary>
public sealed class DressingCatalog
{
    private const string ResourceName = "cutscene-dressings.json";
    // b263: experimental "Cutscenes 2" set (derived offline the same way). Hides read at runtime.
    private const string LabResourceName = "cutscene-dressings-lab.json";

    // LGB asset type (as baked) -> runtime layout instance type. Only types LayoutInstanceVisibility can switch off.
    private static readonly Dictionary<uint, InstanceType> HideTypes = new()
    {
        [1] = InstanceType.BgPart, [3] = InstanceType.Light, [4] = InstanceType.Vfx, [6] = InstanceType.SharedGroup,
    };

    private readonly Dictionary<uint, Dressing> byRow = new();
    private readonly List<LabEntry> lab = new();

    public int Count => byRow.Count;

    /// <summary>Lab entries in ranked order, for the "Cutscenes 2" chip.</summary>
    public IReadOnlyList<LabEntry> Lab => lab;

    public DressingCatalog(IPluginLog log)
    {
        Load(log, ResourceName, isLab: false);
        Load(log, LabResourceName, isLab: true);
        log.Information("[Dressing] catalog loaded: " + (byRow.Count - lab.Count) + " curated + " + lab.Count + " lab dressing(s)");
    }

    private void Load(IPluginLog log, string resourceName, bool isLab)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var res = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
            if (res == null) { log.Warning("[Dressing] " + resourceName + " not embedded"); return; }
            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null) return;
            using var reader = new StreamReader(stream);
            var dto = JsonSerializer.Deserialize<FileDto>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var d in dto?.Dressings ?? new List<DressingDto>())
            {
                var props = new List<DressingProp>();
                foreach (var p in d.Props ?? new List<PropDto>())
                    if (!string.IsNullOrEmpty(p.Path) && p.Pos is { Length: 3 } && p.Rot is { Length: 3 } && p.Scale is { Length: 3 })
                        props.Add(new DressingProp(p.Path, V(p.Pos), V(p.Rot), V(p.Scale), string.IsNullOrEmpty(p.Coll) ? null : p.Coll));
                var hides = new Dictionary<InstanceType, HashSet<ulong>>();
                foreach (var h in d.Hides ?? new List<uint[]>())   // [instanceId, layerId & 0xFFFF, assetType]
                    if (h is { Length: 3 } && HideTypes.TryGetValue(h[2], out var type))
                    {
                        if (!hides.TryGetValue(type, out var set)) hides[type] = set = new HashSet<ulong>();
                        set.Add(LayoutInstanceVisibility.Key((ushort)h[1], h[0]));
                    }
                Vector3? spawn = d.Spawn is { Length: 3 } ? V(d.Spawn) : null;
                if (isLab && byRow.ContainsKey(d.Row)) continue;   // curated wins
                byRow[d.Row] = new Dressing(d.Row, d.Name ?? ("cutscene " + d.Row), d.Cutb ?? "", hides,
                    d.HideOther ?? new Dictionary<string, int>(), props, spawn, d.HidesFromCutb);
                if (isLab && !string.IsNullOrEmpty(d.Bg))
                    lab.Add(new LabEntry(d.Row, d.Name ?? ("cutscene " + d.Row), d.Quest ?? "", d.Bg, d.Tt));
            }
        }
        catch (Exception ex) { log.Warning("[Dressing] " + resourceName + " load failed: " + ex.Message); }
    }

    public bool TryGet(uint row, out Dressing dressing) => byRow.TryGetValue(row, out dressing!);

    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);

    private sealed class FileDto { public List<DressingDto>? Dressings { get; set; } }
    private sealed class DressingDto
    {
        public uint Row { get; set; }
        public string? Name { get; set; }
        public string? Cutb { get; set; }
        public List<uint[]>? Hides { get; set; }
        public Dictionary<string, int>? HideOther { get; set; }
        public List<PropDto>? Props { get; set; }
        public float[]? Spawn { get; set; }
        public string? Quest { get; set; }          // lab only
        public string? Bg { get; set; }             // lab only: stage bg (Stage.Bg form)
        public uint Tt { get; set; }                // lab only: TerritoryType (0 = swap stage)
        public bool HidesFromCutb { get; set; }     // lab: read hides from the cutb at runtime
    }
    private sealed class PropDto
    {
        public string? Path { get; set; }
        public float[]? Pos { get; set; }
        public float[]? Rot { get; set; }
        public float[]? Scale { get; set; }
        public string? Coll { get; set; }
    }
}
