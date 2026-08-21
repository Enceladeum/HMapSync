using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HMSync.Services;

// WEATHER-CRAM Tier-1: the baked-EnvState preset library. Each preset is the raw 0x2F8-byte EnvState block captured
// (wxbake) while some weather rendered natively on a donor zone, stored as base64. Applying a preset restamps that
// block on a zone that lacks the weather (via WeatherCramService), rendering the foreign sky.
//
// TWO SOURCES, MERGED (local overrides shipped):
//   1. SHIPPED / embedded  — weather-presets.json baked into the DLL. Byte-identical for every peer => the sync path
//      only needs to send a weather id; each client decodes the same blob. This is what makes a host's crammed sky
//      reproduce exactly on all clients.
//   2. LOCAL / config-dir  — weather-presets.local.json in ConfigDirectory. Where fresh `wxbake` captures land so you
//      can validate a preset in-game before shipping it. A local entry OVERRIDES a shipped one with the same id
//      (lets you re-bake without a rebuild). NOT sync-safe on its own — only the host has it — so cram-sync must gate
//      on shipped presets; local is a bake/validation staging area.
//
// Fold a validated local preset into weather-presets.json (then delete it from local) to ship it.
public sealed class WeatherPresetStore
{
    private const string EmbeddedResource = "weather-presets.json";
    private const string LocalFileName = "weather-presets.local.json";
    private const int EnvStateSize = 0x2F8;

    private readonly IPluginLog log;
    private readonly string localPath;

    // id -> raw 0x2F8 EnvState bytes
    private readonly Dictionary<byte, byte[]> shipped = new();
    private readonly Dictionary<byte, byte[]> local = new();
    // id -> human label (for the picker); shipped label wins unless a local entry re-bakes the id.
    private readonly Dictionary<byte, string> names = new();
    // b120 Tier-2: id -> baked avfx doodad descriptors (offset + path + raw bytes). Persisted alongside the EnvState blob
    // so a persisted/synced apply re-establishes live descriptors → the foreign weather spawns its doodads (meteors),
    // not just its sky. Local overrides shipped for the same id (mirrors the blob override).
    private readonly Dictionary<byte, List<DoodadBake>> shippedDoodads = new();
    private readonly Dictionary<byte, List<DoodadBake>> localDoodads = new();

    public WeatherPresetStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.localPath = Path.Combine(pluginInterface.ConfigDirectory.FullName, LocalFileName);
        LoadEmbedded();
        LoadLocal();
        log.Information("[HMSync] WeatherPresetStore: " + shipped.Count + " shipped, " + local.Count
            + " local preset(s) available.");
    }

    private sealed record DoodadDto([property: JsonPropertyName("off")] int Off,
                                    [property: JsonPropertyName("path")] string? Path,
                                    [property: JsonPropertyName("b64")] string? B64);
    private sealed record PresetDto([property: JsonPropertyName("name")] string? Name,
                                    [property: JsonPropertyName("b64")] string? B64,
                                    [property: JsonPropertyName("doodads")] List<DoodadDto>? Doodads);
    private sealed record FileDto([property: JsonPropertyName("version")] string? Version,
                                  [property: JsonPropertyName("presets")] Dictionary<string, PresetDto>? Presets);

    private void LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(EmbeddedResource, StringComparison.OrdinalIgnoreCase));
            if (resName == null) { log.Warning("[HMSync] weather presets: embedded resource not found"); return; }
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) { log.Warning("[HMSync] weather presets: embedded stream null"); return; }
            using var reader = new StreamReader(stream);
            Ingest(reader.ReadToEnd(), shipped, "shipped");
        }
        catch (Exception ex) { log.Warning("[HMSync] weather presets: embedded load failed: " + ex.Message); }
    }

    private void LoadLocal()
    {
        try
        {
            if (!File.Exists(localPath)) return;
            Ingest(File.ReadAllText(localPath), local, "local");
        }
        catch (Exception ex) { log.Warning("[HMSync] weather presets: local load failed: " + ex.Message); }
    }

    private void Ingest(string json, Dictionary<byte, byte[]> into, string tag)
    {
        var doodadsInto = ReferenceEquals(into, local) ? localDoodads : shippedDoodads;
        var dto = JsonSerializer.Deserialize<FileDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto?.Presets == null) return;
        foreach (var kv in dto.Presets)
        {
            if (!byte.TryParse(kv.Key, out var id)) continue;
            var b64 = kv.Value?.B64;
            if (string.IsNullOrEmpty(b64)) continue;
            byte[] blob;
            try { blob = Convert.FromBase64String(b64); }
            catch { log.Warning("[HMSync] weather presets (" + tag + "): id " + id + " has invalid base64"); continue; }
            if (blob.Length != EnvStateSize)
            {
                log.Warning("[HMSync] weather presets (" + tag + "): id " + id + " wrong size " + blob.Length
                    + " (expected " + EnvStateSize + ") — skipped");
                continue;
            }
            into[id] = blob;
            if (!string.IsNullOrEmpty(kv.Value?.Name)) names[id] = kv.Value!.Name!;
            // b120: baked avfx doodad descriptors (optional — sky-only presets have none).
            if (kv.Value?.Doodads is { Count: > 0 } dds)
            {
                var list = new List<DoodadBake>();
                foreach (var dd in dds)
                {
                    if (string.IsNullOrEmpty(dd.B64)) continue;
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(dd.B64); }
                    catch { continue; }
                    list.Add(new DoodadBake { Offset = dd.Off, Path = dd.Path ?? "", Bytes = bytes });
                }
                if (list.Count > 0) doodadsInto[id] = list;
            }
        }
    }

    // Local overrides shipped for the same id (lets a fresh bake supersede an embedded preset without a rebuild).
    public bool TryGet(byte id, out byte[] blob)
    {
        if (local.TryGetValue(id, out var l)) { blob = l; return true; }
        if (shipped.TryGetValue(id, out var s)) { blob = s; return true; }
        blob = Array.Empty<byte>();
        return false;
    }

    public bool Has(byte id) => local.ContainsKey(id) || shipped.ContainsKey(id);

    // b120: baked avfx doodads for an id (local overrides shipped, mirroring TryGet). Empty list = sky-only preset.
    public IReadOnlyList<DoodadBake> GetDoodads(byte id)
    {
        if (localDoodads.TryGetValue(id, out var l)) return l;
        if (shippedDoodads.TryGetValue(id, out var s)) return s;
        return Array.Empty<DoodadBake>();
    }

    // b120: does this id's preset carry baked doodads (⇒ apply renders full effects, not sky-only)?
    public bool HasDoodads(byte id) => localDoodads.ContainsKey(id) || shippedDoodads.ContainsKey(id);

    // Only SHIPPED presets are sync-deterministic (every peer has the identical bytes). Cram-sync gates on this set.
    public bool HasShipped(byte id) => shipped.ContainsKey(id);

    public string? Name(byte id) => names.TryGetValue(id, out var n) ? n : null;

    public IReadOnlyList<byte> AvailableIds =>
        shipped.Keys.Union(local.Keys).OrderBy(x => x).ToList();

    public IReadOnlyList<byte> ShippedIds => shipped.Keys.OrderBy(x => x).ToList();

    // Persist a freshly-captured EnvState blob into the config-dir local library and make it immediately available.
    // Returns a user-facing status string.
    public string Bake(byte id, string? name, byte[] blob, IReadOnlyList<DoodadBake>? doodads = null)
    {
        if (blob == null || blob.Length != EnvStateSize)
            return "[HMSync] wxbake: bad blob size " + (blob?.Length.ToString() ?? "null") + " (expected " + EnvStateSize + ").";
        local[id] = blob;
        if (!string.IsNullOrEmpty(name)) names[id] = name!;
        // b120: persist the avfx doodad descriptors so a later persisted/synced apply re-establishes them (full effects).
        int ndood = 0;
        if (doodads is { Count: > 0 })
        {
            localDoodads[id] = new List<DoodadBake>(doodads);
            ndood = doodads.Count;
        }
        else
        {
            localDoodads.Remove(id);   // re-bake with no doodads → drop stale ones
        }
        try
        {
            SaveLocal();
        }
        catch (Exception ex)
        {
            return "[HMSync] wxbake: preset id " + id + " staged in memory but SAVE FAILED: " + ex.Message;
        }
        return "[HMSync] wxbake: preset id " + id + " (" + (name ?? "?") + ") baked to local library ("
            + local.Count + " local, " + ndood + " doodad" + (ndood == 1 ? "" : "s") + "). Apply with `wxpreset " + id
            + "`; fold into weather-presets.json to ship.";
    }

    private void SaveLocal()
    {
        var presets = new Dictionary<string, PresetDto>();
        foreach (var kv in local)
        {
            List<DoodadDto>? dds = null;
            if (localDoodads.TryGetValue(kv.Key, out var list) && list.Count > 0)
            {
                dds = new List<DoodadDto>(list.Count);
                foreach (var d in list)
                    dds.Add(new DoodadDto(d.Offset, d.Path, Convert.ToBase64String(d.Bytes)));
            }
            presets[kv.Key.ToString()] = new PresetDto(names.TryGetValue(kv.Key, out var n) ? n : null,
                                                       Convert.ToBase64String(kv.Value), dds);
        }
        var dto = new FileDto("local", presets);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(localPath, json);
    }
}
