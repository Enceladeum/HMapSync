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

// PATH I b163: durable library for KEYFRAME SETS — the day-night sky-graft's swept gradient (WeatherCramService's
// keyframe path). A set is one donor weather's whole Eorzean day: an ordered list of (tod-seconds, raw 0x2F8 EnvState
// block) samples captured by `wxkfsweep`. The in-memory set is ephemeral (cleared on logout/teardown, b162), so a sweep
// that took ~15s to run was lost every session. This bakes it to a config-dir file so it survives, reloads, and is the
// substrate for the eventual embedded/sync-deterministic ship (mirrors WeatherPresetStore's single-blob pipeline, one
// dimension richer: N tod-tagged blocks instead of one).
//
// b175 DONOR DIMENSION: a set is now keyed by (weather, donorTt) — the CITY it was swept in — not weather alone. The
// cross-city diff (wxdifftour) proved the same spine weather (Clear/Fair/Clouds/Fog/Rain/Snow) renders a genuinely
// different sky per city (Limsa's sunset ≠ Kugane's), so we ship EVERY city's version as its own graftable day-set and
// the picker offers "Clear Skies · Limsa" vs "Clear Skies · Kugane". donorTt==0 is the LEGACY/untagged slot: manual
// sweeps and pre-b175 files load there, and byte-only callers (wxkfload, the single-graft chip) resolve to it (falling
// back to the first available donor when no untagged set exists), so the old single-set flow keeps working unchanged.
//
// SHIPPED (b182): the swept day-night library now ships as an embedded resource (keyframe-sets.json), byte-identical for
// every peer, so a fresh install's picker is fully populated — this is the flagship per-city day/night feature. TWO
// SOURCES, MERGED (local overrides shipped), mirroring WeatherPresetStore's pipeline:
//   1. EMBEDDED baseline — keyframe-sets.json baked into the DLL (the validated all-cities tour). Recorded in shippedKeys;
//      byte-identical for every client, so a future graft-sync only needs to send (weather, donor).
//   2. LOCAL overlay     — config-dir keyframe-sets.local.json where fresh `wxkfsweep`/`wxkfcities` captures land; a local
//      set OVERRIDES the embedded one with the same (weather, donor) so you can re-sweep and validate without a rebuild.
// Fold a validated local set into keyframe-sets.json to promote it to the shipped baseline (then it can drop from local).
public sealed class KeyframeSetStore
{
    private const string EmbeddedResource = "keyframe-sets.json";
    private const string LocalFileName = "keyframe-sets.local.json";
    private const int EnvStateSize = 0x2F8;

    private readonly IPluginLog log;
    private readonly string localPath;

    // (weather, donorTt) -> ordered keyframes (tod-seconds, 0x2F8 block), sorted by tod. donorTt 0 = legacy/untagged.
    // This is the MERGED live view (embedded baseline with local sweeps layered on top); all read accessors read it.
    private readonly Dictionary<(byte w, uint donor), List<(float tod, byte[] block)>> sets = new();
    private readonly Dictionary<(byte w, uint donor), string> names = new();
    // b182: `local` holds ONLY config-dir sweeps so SaveLocal never bloats the local file with the embedded baseline;
    // `shippedKeys` marks the byte-identical embedded sets a future graft-sync can gate on (see IsShipped).
    private readonly Dictionary<(byte w, uint donor), List<(float tod, byte[] block)>> local = new();
    private readonly HashSet<(byte w, uint donor)> shippedKeys = new();

    public KeyframeSetStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.localPath = Path.Combine(pluginInterface.ConfigDirectory.FullName, LocalFileName);
        LoadEmbedded();
        LoadLocal();
        log.Information("[HMSync] KeyframeSetStore: " + sets.Count + " keyframe set(s) available ("
            + shippedKeys.Count + " shipped, " + local.Count + " local) across "
            + sets.Keys.Select(k => k.w).Distinct().Count() + " weather(s).");
    }

    private sealed record KfDto([property: JsonPropertyName("tod")] float Tod,
                                [property: JsonPropertyName("b64")] string? B64);
    private sealed record SetDto([property: JsonPropertyName("name")] string? Name,
                                 [property: JsonPropertyName("keyframes")] List<KfDto>? Keyframes);
    private sealed record FileDto([property: JsonPropertyName("version")] string? Version,
                                  [property: JsonPropertyName("sets")] Dictionary<string, SetDto>? Sets);

    // b175: key encoding in the JSON is "weather:donor" (e.g. "1:128"). A legacy key with no colon is a pre-b175 untagged
    // set → donor 0. Robust to garbage: unparseable keys are skipped, not fatal.
    private static bool TryParseKey(string s, out byte weather, out uint donor)
    {
        weather = 0; donor = 0;
        int c = s.IndexOf(':');
        if (c < 0) return byte.TryParse(s, out weather);   // legacy: bare weather id → donor 0
        return byte.TryParse(s.AsSpan(0, c), out weather) && uint.TryParse(s.AsSpan(c + 1), out donor);
    }
    private static string MakeKey(byte weather, uint donor) => donor == 0 ? weather.ToString() : weather + ":" + donor;

    private void LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(EmbeddedResource, StringComparison.OrdinalIgnoreCase));
            if (resName == null) { log.Warning("[HMSync] keyframe sets: embedded resource not found"); return; }
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) { log.Warning("[HMSync] keyframe sets: embedded stream null"); return; }
            using var reader = new StreamReader(stream);
            Ingest(reader.ReadToEnd(), shipped: true);
        }
        catch (Exception ex) { log.Warning("[HMSync] keyframe sets: embedded load failed: " + ex.Message); }
    }

    private void LoadLocal()
    {
        try
        {
            if (!File.Exists(localPath)) return;
            Ingest(File.ReadAllText(localPath), shipped: false);
        }
        catch (Exception ex) { log.Warning("[HMSync] keyframe sets: local load failed: " + ex.Message); }
    }

    // Parse a FileDto blob into the merged live view (`sets`/`names`). shipped=true → embedded baseline (also recorded in
    // shippedKeys); shipped=false → config-dir sweeps (also mirrored into `local`, and OVERRIDE the embedded set for the
    // same (weather, donor) so a fresh re-sweep supersedes without a rebuild). Ingest embedded first, then local.
    private void Ingest(string json, bool shipped)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto?.Sets == null) return;
        foreach (var kv in dto.Sets)
        {
            if (!TryParseKey(kv.Key, out var weather, out var donor)) continue;
            var kfs = kv.Value?.Keyframes;
            if (kfs == null || kfs.Count == 0) continue;
            var list = new List<(float, byte[])>(kfs.Count);
            foreach (var kf in kfs)
            {
                if (string.IsNullOrEmpty(kf.B64)) continue;
                byte[] block;
                try { block = Convert.FromBase64String(kf.B64); }
                catch { continue; }
                if (block.Length != EnvStateSize) continue;
                list.Add((kf.Tod, block));
            }
            if (list.Count < 2) continue;                       // a graftable set needs >=2 samples
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var key = (weather, donor);
            sets[key] = list;
            if (!string.IsNullOrEmpty(kv.Value?.Name)) names[key] = kv.Value!.Name!;
            if (shipped) shippedKeys.Add(key);
            else local[key] = list;
        }
    }

    // ── donor-aware lookups ──────────────────────────────────────────────────────────────────────────────────────
    public bool TryGet(byte weather, uint donor, out List<(float tod, byte[] block)> keyframes)
    {
        if (sets.TryGetValue((weather, donor), out var list)) { keyframes = list; return true; }
        keyframes = new List<(float, byte[])>();
        return false;
    }

    public bool Has(byte weather, uint donor) => sets.ContainsKey((weather, donor));
    public string? Name(byte weather, uint donor) => names.TryGetValue((weather, donor), out var n) ? n : null;
    public int Count(byte weather, uint donor) => sets.TryGetValue((weather, donor), out var l) ? l.Count : 0;
    public bool IsTimeMarching(byte weather, uint donor)
        => sets.TryGetValue((weather, donor), out var kfs) && kfs.Count >= 2 && MaxSkyFloatDelta(kfs) > TimeMarchEpsilon;

    // Donors (sorted) that have a set for this weather — the picker's per-city sub-selection list. Excludes the legacy
    // donor-0 slot (that's the un-citied fallback, surfaced by the plain weather chip, not a named city variant).
    public IReadOnlyList<uint> DonorsFor(byte weather)
        => sets.Keys.Where(k => k.w == weather && k.donor != 0).Select(k => k.donor).Distinct().OrderBy(x => x).ToList();

    // Donors whose set for this weather actually CYCLES (earns the "* travels the sun" marker), sorted.
    public IReadOnlyList<uint> TimeMarchingDonorsFor(byte weather)
        => DonorsFor(weather).Where(d => IsTimeMarching(weather, d)).ToList();

    // Distinct weathers that have at least one CITY-tagged (donor != 0) set — the picker's "City sky variants" list.
    public IReadOnlyList<byte> WeathersWithDonors
        => sets.Keys.Where(k => k.donor != 0).Select(k => k.w).Distinct().OrderBy(x => x).ToList();

    // ── legacy byte-only convenience (donor 0, else first available donor) ───────────────────────────────────────
    // Keeps the pre-b175 single-set flow (wxkfload <id>, the single-graft chip) working: prefer the untagged slot, but
    // if only city-tagged sets exist, resolve to the first donor so `wxkfload 1` still finds *a* Clear-Skies day-set.
    public bool TryGet(byte weather, out List<(float tod, byte[] block)> keyframes)
    {
        if (TryGet(weather, 0, out keyframes)) return true;
        var d = DonorsFor(weather);
        if (d.Count > 0) return TryGet(weather, d[0], out keyframes);
        keyframes = new List<(float, byte[])>();
        return false;
    }
    public bool Has(byte weather) => sets.Keys.Any(k => k.w == weather);
    public string? Name(byte weather)
    {
        if (names.TryGetValue((weather, 0u), out var n)) return n;
        var d = DonorsFor(weather);
        return d.Count > 0 && names.TryGetValue((weather, d[0]), out var n2) ? n2 : null;
    }
    public int Count(byte weather)
        => TryGet(weather, out var l) ? l.Count : 0;

    // b170 TRUTH-GATE for the UI "* travels the sun" marker, widened for donors: weather X earns the marker if ANY of its
    // sets (untagged or any city) genuinely cycles. See MaxSkyFloatDelta for the flatness rationale.
    public bool IsTimeMarching(byte weather)
        => sets.Keys.Any(k => k.w == weather && IsTimeMarching(weather, k.donor));

    // Distinct weathers that have at least one set (any donor) — for wxkflist / picker gating.
    public IReadOnlyList<byte> AvailableIds => sets.Keys.Select(k => k.w).Distinct().OrderBy(x => x).ToList();
    // All (weather, donor) keys — for a full listing / the embed-fold step.
    public IReadOnlyList<(byte weather, uint donor)> AvailableKeys
        => sets.Keys.OrderBy(k => k.w).ThenBy(k => k.donor).ToList();

    // b182: is this set part of the byte-identical embedded baseline (every peer has it)? A future graft-sync gates here,
    // mirroring WeatherPresetStore.HasShipped. Local-only sweeps return false (not sync-safe until folded into the embed).
    public bool IsShipped(byte weather, uint donor) => shippedKeys.Contains((weather, donor));

    // b170: TRUTH-GATE detail. Having a set is NOT the same as having a set that actually CYCLES: a donor weather with no
    // day-night variation (e.g. Kugane's CutScene 59 — a static overcast that reads the same EnvState at every tod) sweeps
    // to N near-identical keyframes, so grafting it travels no sun. A set earns the marker only if some sky float moves
    // meaningfully across the captured day. Threshold is generous against float noise (0.01) yet far below real day-night
    // deltas (sun/ambient colors swing whole units), so flat sets read exactly 0.
    private const float TimeMarchEpsilon = 0.01f;

    // Max per-offset (max-min) over the EnvState's float lanes, across all keyframes. Pointer-range/garbage lanes read as
    // non-finite or absurdly-large floats (a heap address reinterpreted as float) — an offset that is insane in ANY
    // keyframe is dropped, so only genuine sky floats contribute. A flat day yields 0; a real day yields >> epsilon.
    private static float MaxSkyFloatDelta(List<(float tod, byte[] block)> kfs)
    {
        int lanes = EnvStateSize / 4;
        float maxDelta = 0f;
        for (int f = 0; f < lanes; f++)
        {
            int off = f * 4;
            float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
            bool sane = true;
            foreach (var (_, block) in kfs)
            {
                float v = BitConverter.ToSingle(block, off);
                if (!float.IsFinite(v) || Math.Abs(v) >= 1.0e4f) { sane = false; break; }   // pointer/garbage lane → skip
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            if (sane) { float d = mx - mn; if (d > maxDelta) maxDelta = d; }
        }
        return maxDelta;
    }

    // ── persistence ──────────────────────────────────────────────────────────────────────────────────────────────
    // Persist a swept set into the config-dir library keyed by (weather, donor) and keep it available in memory.
    public string Save(byte weather, uint donor, string? name, IReadOnlyList<(float tod, byte[] block)> keyframes)
    {
        if (keyframes == null || keyframes.Count < 2)
            return "[HMSync] wxkfsave: need at least 2 keyframes (have " + (keyframes?.Count ?? 0) + ").";
        foreach (var kf in keyframes)
            if (kf.block == null || kf.block.Length != EnvStateSize)
                return "[HMSync] wxkfsave: a keyframe has bad block size (expected " + EnvStateSize + ").";
        var key = (weather, donor);
        var list = new List<(float, byte[])>(keyframes.Select(k => (k.tod, k.block)));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        sets[key] = list;
        local[key] = list;                                          // b182: persistence source — SaveLocal writes `local` only
        if (!string.IsNullOrEmpty(name)) names[key] = name!;
        try { SaveLocal(); }
        catch (Exception ex) { return "[HMSync] wxkfsave: set " + weather + (donor != 0 ? "@" + donor : "")
            + " staged in memory but SAVE FAILED: " + ex.Message; }
        string where = donor != 0 ? " donor " + donor : "";
        return "[HMSync] wxkfsave: keyframe set weather " + weather + where + " (" + (name ?? "?") + ", " + list.Count
            + " samples) saved to local library.";
    }

    // Legacy single-arg save (manual wxkfsave / pre-donor callers) → the untagged donor-0 slot.
    public string Save(byte weather, string? name, IReadOnlyList<(float tod, byte[] block)> keyframes)
        => Save(weather, 0u, name, keyframes);

    private void SaveLocal()
    {
        var outSets = new Dictionary<string, SetDto>();
        foreach (var kv in local)                                   // b182: local sweeps only — never the embedded baseline
        {
            var kfs = new List<KfDto>(kv.Value.Count);
            foreach (var (tod, block) in kv.Value)
                kfs.Add(new KfDto(tod, Convert.ToBase64String(block)));
            outSets[MakeKey(kv.Key.w, kv.Key.donor)] = new SetDto(names.TryGetValue(kv.Key, out var n) ? n : null, kfs);
        }
        var dto = new FileDto("local", outSets);
        File.WriteAllText(localPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }
}
