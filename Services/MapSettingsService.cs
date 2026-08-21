using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;              // b111: WeatherManager (SetNextWeather transition path)
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

/// <summary>
/// S326 - Map-state backbone. Host-authoritative environment control for a loaded map: TIME (Eorzea hour),
/// WEATHER (from the territory's legal WeatherRate set, plus 0 = atmospheric/undefined), and BGM. The host sets
/// these; they are broadcast and replayed to peers (wire fields on TransformData / a dedicated map-state message),
/// applied on map load AND changeable mid-session. NPC-removal is a wire flag here too (functionality lands later).
///
/// READS (sheet-derived, no guesswork - confirmed against Weatherman's DataProvider + the CSV column layout):
///   • Legal weather for a territory = TerritoryType.WeatherRate → WeatherRate.Weather[0..7] (weather IDs), names
///     from Weather.Name. We prepend 0 ("Default / atmospheric") as an always-valid choice (Hyperborea's trick:
///     weather 0 gives atmospheric effects with no defined weather). Integer prefixes stripped - names only.
///   • BGM names: the BGM sheet is PATHS ONLY (no titles). Friendly names come from the Orchestrion sheet
///     (keyed by Orchestrion row id - the precise BGM-sheet-id ↔ Orchestrion mapping is finalized when the BGM
///     playback helper lands; OrchestrionPath.File is a path string, NOT a BGM ref). "None" (0) is always offered.
///
/// WRITES:
///   • Weather: EnvManager.Instance()->ActiveWeather (byte). Setting it directly re-skins the sky/effects.
///     (Weatherman patches the render path for persistence; we set the field + re-assert after load, which is
///     enough for a static RP scene. If the game overwrites it on its own weather tick, the re-assert poll catches it.)
///   • Time: EnvManager exposes the Eorzea time; we set the day-time seconds. (Mirrors Weatherman's time write.)
///   • BGM: scene-based, more involved - deferred to the plugin/BGM path (Orchestrion's BGMManager pattern). This
///     service owns the DATA (what BGM id) and the wire; the actual scene push is a thin helper.
///
/// HOST-ONLY: only the session host may set map state. Peers receive + apply. Enforced at the command/plugin layer.
/// </summary>
public unsafe class MapSettingsService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly TimeFreezeService timeFreeze;
    private readonly WeatherCramService weatherCram;
    private readonly WeatherPresetStore weatherPresets;
    private readonly KeyframeSetStore keyframeSets;   // PATH I b163: durable day-night graft library
    private readonly Dictionary<uint, List<(byte id, string name, bool legal)>> lvbWeatherCache = new();
    // NB-39: cutscene stage-bg → authored default weather, read straight from the stage's own .lvb (bypasses the donor
    // territory entirely). Cached by bg path. See GetStageDefaultWeather + the Reassert stage branch.
    private readonly Dictionary<string, byte> stageWeatherCache = new();
    // NB-39: the active cutscene stage's bg, set by DoLoad before the post-load reassert; null on a plain zone load.
    // When set, Reassert resolves the load-time NATIVE weather from the STAGE's authored .lvb, not the donor territory
    // (a cutscene borrows whatever real zone you launched from as its donor — an interior donor = weather 0 = None, the
    // "cutscenes all pop as None/atmospheric" bug). Donor is the graceful fallback when the stage .lvb has no weather.
    public string? ActiveStageBgForWeather { get; set; }

    // b173: NATIVE-SKY BLACK RESCUE. On some instance/cutscene territories (e.g. The Clyteum 1345) a weather id sits IN the
    // loaded env bank (so IsWeatherInLoadedBank passes and the dropdown/promotion offers it as native) yet resolves to a
    // DEGENERATE/black EnvState — the map was only authored to render a couple of its weathers, and the rest index an empty
    // env slot. Native ApplyWeather writes ActiveWeather and the sky recomputes BLACK. The extra-preset CHIP for the same id
    // renders fine because it RESTAMPS a good donor block over that black native EnvState every frame — that is exactly the
    // "dropdown Clear Skies is black but the chip Clear Skies works" disconnect the operator reported. We cannot predict
    // black statically (in-bank ≠ has-valid-env-data), so we VERIFY after the fact: a native apply arms a short deferred
    // check; ~N frames later (once the native recompute has run) we sample the resolved EnvState, and if it came back black
    // AND we hold a good sky for that id (a time-marching graft set or a baked static preset) we upgrade to it — reproducing
    // the working chip. Non-degenerate natives (Fair Skies, Snow) read populated and are left untouched, so the cases the
    // operator likes native stay native. Weather 0 (None-Atmospheric, the deliberate dramatic-lighting blank) is never armed.
    private byte pendingVerifyId;
    private int pendingVerifyFrames;

    public MapSettingsService(IDataManager dataManager, IPluginLog log, TimeFreezeService timeFreeze, WeatherCramService weatherCram, WeatherPresetStore weatherPresets, KeyframeSetStore keyframeSets, ISigScanner sig)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.timeFreeze = timeFreeze;
        this.weatherCram = weatherCram;
        this.weatherPresets = weatherPresets;
        this.keyframeSets = keyframeSets;
        // (sig retained in the signature for future use; BGM playback uses the version-tracked BGMSystem instance.)
    }

    // ── Current host-set state (the authoritative values the host has chosen; broadcast to peers) ──
    // 0/unset sentinels: WeatherId 0 is a VALID choice (atmospheric), so "unset" is tracked separately.
    public bool HasState { get; private set; }         // has the host set anything this session?
    public byte WeatherId { get; set; }                 // 0 = default/atmospheric (valid); else a legal weather id
    public uint WeatherDonor { get; set; }              // b183: day/night sky-graft donor tt (0 = static weather, no graft) — paired with WeatherId
    public ushort EorzeaHour { get; set; } = 12;        // 0..23, the forced Eorzea hour (with minute below)
    public byte EorzeaMinute { get; set; }              // 0..59
    public bool TimeForced { get; set; }                // is time being held? (vs let it flow naturally)
    public uint BgmId { get; set; }                     // 0 = none/silence; else a BGM sheet row id
    public bool RemoveNpcs { get; set; }                // host flag - despawn all event NPCs (S328aa)
    public bool HideQuestSigns { get; set; }            // host flag - hide over-head quest markers only (S328aa)

    public void MarkStateSet() => HasState = true;

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // SHEET READS - dropdown population
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Legal weather choices for a territory: (id, displayName). Always begins with (0, "Default / atmospheric").
    /// Then the WeatherRate.Weather[] entries for the territory, de-duplicated, names from Weather.Name, integer
    /// prefixes stripped (names only). Returns just the (0) entry if the territory or its rate row is unresolvable.
    /// </summary>
    // v0.7.471 - TERRITORY-SCOPED WEATHER PROMOTIONS.
    //
    // Some territories accept a weather that isn't in their WeatherRate set, so it only ever appeared behind
    // "Show more presets" (debug-gated). These three want CutScene (59) in the ordinary picker. Verified against
    // Weather.csv (`59,CutScene,CutScenery,,,,,0`) and TerritoryType/WeatherRate:
    //
    //   958  (m5f2)  rate 133 -> native [15, 9, 7, 4, 3, 2, 1]
    //   1011 (ec029) rate 27  -> native [15]           <- one weather, hence how bare it feels
    //   1120 (ec048) rate 58  -> native [1]            <- likewise
    //
    // 59 is in NONE of those sets, so the promotion is purely additive on all three.
    //
    // WEATHER 0 ("None - Atmospheric") AND THE WeatherId==0 SENTINEL - the accurate, current picture (this block was
    // reversed several times; the reversals are collapsed here). Two facts, not one:
    //   (1) An EXPLICIT pick of 0 (or any debug id) DOES reach peers. Since v0.7.475 PushMapState ships an explicit
    //       pick VERBATIM via the weatherOverride path (HMSyncPlugin.PushMapState) - it does NOT re-derive through
    //       GetLegalWeather, so "cannot be broadcast" is FALSE for the pick itself. (An earlier revision of this
    //       comment claimed the opposite; that revision predates the verbatim-broadcast refactor and is wrong.)
    //   (2) What 0 does NOT do is PERSIST across a map-load reassert. Reassert() (below, ~line 735) and the load-time
    //       native-weather engage (~line 744) both gate on `WeatherId != 0`, treating 0 as "host set nothing" and
    //       falling back to the zone's native sky. So a held "None - Atmospheric" is dropped on the next load. Fixing
    //       THAT cleanly needs a separate HasWeather bool (mirror TimeForced) so "explicitly None" is distinguishable
    //       from "unset" - deferred as an edge case (see the Maintenance Manual known-issues, Part 7).
    // GetLegalWeather still starts its list with (0, "Default / atmospheric") so the picker can offer it; the caveat
    // above is only about persistence, not selectability.
    //
    // WHY NUMERIC IDS. The first cut of this resolved names against the Weather sheet, on the reasoning that an
    // unverified id fails silently-and-wrongly while a name miss fails visibly. That reasoning was sound and the
    // premise is now gone: with the CSVs on hand the id is ground truth, so the resolver was pure risk surface
    // (a sheet enumeration + a string match, either of which could fail quietly inside the enclosing try/catch -
    // and one of which did). Verified constant beats runtime lookup.
    private static readonly Dictionary<uint, byte[]> PromotedWeather = new()
    {
        [958]  = new byte[] { 59, 150 },   // CutScene; Apocalypse (150) - in 958's LVB/debug set, renders natively when forced, so promote it to the ordinary picker
        [1011] = new byte[] { 59 },   // CutScene
        [1120] = new byte[] { 59 },   // CutScene
        [1345] = new byte[] { 4 },    // Fog - m6d2, native set is [15] only; verified Weather.csv `4,Fog,foggy`
    };

    /// <summary>v0.7.473 - `/hms weatherdiag`. Dumps every input the weather picker and chip grid decide from, for
    /// the loaded zone, so "why isn't X in the dropdown" is answered by reading rather than by inference. Exists
    /// because two successive static reads of this path produced two wrong answers.</summary>
    public void DumpWeatherDiag(uint territoryId)
    {
        try
        {
            log.Information("[HMSync] [WXDIAG] territory=" + territoryId);
            byte def = GetDefaultWeather(territoryId);
            byte live = GetActiveWeather();
            log.Information("[HMSync] [WXDIAG] defaultWeather(live WeatherManager)=" + def + " (" + WeatherName(def) + ")"
                + "  activeWeather=" + live + " (" + WeatherName(live) + ")");
            // ⚠ The dropdown SKIPS any entry equal to defaultWeather (it's rendered as the "(native)" row above the
            // separator). So if a promoted id equals defaultWeather it is present in the list and invisible below.
            var legal = GetLegalWeather(territoryId);
            log.Information("[HMSync] [WXDIAG] GetLegalWeather -> " + legal.Count + " entries: "
                + string.Join(", ", legal.Select(x => x.id + ":" + x.name)));
            bool has59 = legal.Exists(x => x.id == 59);
            log.Information("[HMSync] [WXDIAG] CutScene(59) in legal set = " + has59
                + (has59 && def == 59 ? "  ← AND equals defaultWeather, so the dropdown skips it (renders as \"(native)\")" : ""));

            var lvb = GetLvbWeathers(territoryId);
            log.Information("[HMSync] [WXDIAG] LVB weathers -> " + lvb.Count + ": "
                + string.Join(", ", lvb.Select(x => x.id + ":" + x.name + (x.legal ? "(legal)" : "(extra)"))));
            // v0.7.474: report BOTH chip modes. The first cut modelled only the LVB path and reported "1 chip"
            // while the debug grid was showing ~70 - because debug mode passes includeAll:true, which appends
            // every used weather beyond the LVB set. A diagnostic that models one branch of the thing it is
            // diagnosing is worse than none: it reads as authoritative and disagrees with the screen.
            foreach (var dbg in new[] { false, true })
            {
                var zw = GetZoneWeathers(territoryId, dbg);
                var chips = zw.Where(x => !x.legal && x.id != 0 && x.id != def).ToList();
                log.Information("[HMSync] [WXDIAG] chips (debugMode=" + dbg + ") -> " + chips.Count + ": "
                    + string.Join(", ", chips.Select(x => x.id + ":" + x.name)));
            }
        }
        catch (Exception ex) { log.Error("[HMSync] [WXDIAG] failed: " + ex.Message); }
    }

    public List<(byte id, string name)> GetLegalWeather(uint territoryId)
    {
        var result = new List<(byte, string)> { (0, "Default / atmospheric") };
        try
        {
            var terrSheet = dataManager.GetExcelSheet<TerritoryType>();
            if (terrSheet == null || !terrSheet.HasRow(territoryId)) return result;
            var terr = terrSheet.GetRow(territoryId);

            var rateSheet = dataManager.GetExcelSheet<WeatherRate>();
            uint rateId = terr.WeatherRate.RowId;
            if (rateSheet == null || !rateSheet.HasRow(rateId)) return result;
            var rate = rateSheet.GetRow(rateId);

            var weatherSheet = dataManager.GetExcelSheet<Weather>();
            if (weatherSheet == null) return result;

            var seen = new HashSet<byte> { 0 };
            // WeatherRate.Weather is a fixed-length collection (8) of weather-id refs.
            foreach (var wRef in rate.Weather)
            {
                var wid = (byte)wRef.RowId;
                if (wid == 0 || seen.Contains(wid)) continue;
                if (!weatherSheet.HasRow(wid)) continue;
                var name = weatherSheet.GetRow(wid).Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                seen.Add(wid);
                result.Add((wid, name));
            }

            // v0.7.471: append this territory's promotions AFTER the sheet set, so the native weathers keep their
            // usual order and the promoted one lands at the bottom of the picker as the odd one out.
            if (PromotedWeather.TryGetValue(territoryId, out var promos))
                foreach (var pid in promos)
                {
                    if (pid == 0 || seen.Contains(pid) || !weatherSheet.HasRow(pid)) continue;
                    var pname = weatherSheet.GetRow(pid).Name.ToString();
                    // Unlike the native loop above, an empty name does NOT skip the entry here: the promotion is
                    // deliberate and a blank label in some client language shouldn't make it vanish silently.
                    if (string.IsNullOrWhiteSpace(pname)) pname = "Weather " + pid;
                    seen.Add(pid);
                    result.Add((pid, pname));
                }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] GetLegalWeather(" + territoryId + ") failed: " + ex.Message);
        }
        return result;
    }

    /// <summary>
    /// A weather id's display name (names only). 0 → "Default / atmospheric". Falls back to "Weather {id}".
    /// </summary>
    public string WeatherName(byte id)
    {
        if (id == 0) return "Default / atmospheric";
        try
        {
            var weatherSheet = dataManager.GetExcelSheet<Weather>();
            if (weatherSheet != null && weatherSheet.HasRow(id))
            {
                var n = weatherSheet.GetRow(id).Name.ToString();
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
        }
        catch { /* fall through */ }
        return "Weather " + id;
    }

    // S327e: read the BGM the game is CURRENTLY playing (scene 0 = the zone/field theme priority). Lets the Music row
    // show the actual track even before the host picks one, and keeps the name when paused. The friendly title still
    // needs the Orchestrion community CSV (deferred) - this returns the id, named "Track N" for now.
    public uint GetCurrentBgm()
    {
        try
        {
            var bgm = FFXIVClientStructs.FFXIV.Client.Game.BGMSystem.Instance();
            if (bgm == null || bgm->Scenes.LongCount <= 0) return 0;
            ushort playing = bgm->Scenes.First->PlayingBgmId;   // 0x0E - what's actually audible
            ushort target = bgm->Scenes.First->BgmId;           // 0x0C
            return playing != 0 ? playing : (uint)target;
        }
        catch { return 0; }
    }

    // S327b/f: current Eorzea time-of-day as HH:MM. Delegates to TimeFreezeService, which reads ClientTime.EorzeaTime
    // (the field the renderer uses) - when frozen that's our held value, when not it's the live recomputed clock.
    public (int hour, int minute) GetEorzeaTimeOfDay() => timeFreeze.GetTimeOfDay();

    // S327b: resolve a territory's display name (PlaceName) for the "Zone: <name> (ID)" header. Empty if unnamed.
    public string GetZoneName(uint territoryId)
    {
        try
        {
            var terrSheet = dataManager.GetExcelSheet<TerritoryType>();
            if (terrSheet != null && terrSheet.HasRow(territoryId))
                return terrSheet.GetRow(territoryId).PlaceName.ValueNullable?.Name.ToString() ?? "";
        }
        catch { /* fall through */ }
        return "";
    }

    /// <summary>
    /// The territory's DEFAULT BGM id (TerritoryType.BGM), used to preload the BGM dropdown's default choice.
    /// </summary>
    // (Removed CurrentZoneDefaultBgm - it read GameMain.CurrentTerritoryTypeId, which does NOT reflect a synthetic
    // HMS load: the packet filter hides the real zone change, so GameMain still reports the apartment/previous zone.
    // That produced "plays the last map's track" bugs. Callers now pass the TRUE loaded-zone id, GetDefaultBgm(id).)

    public uint GetDefaultBgm(uint territoryId)
    {
        try
        {
            var terrSheet = dataManager.GetExcelSheet<TerritoryType>();
            if (terrSheet != null && terrSheet.HasRow(territoryId))
            {
                uint tb = terrSheet.GetRow(territoryId).BGM.RowId;
                // Instanced zones (1345 Clyteum etc.) carry the SILENCE PLACEHOLDER (1001); their real music is defined
                // STATICALLY on the content via ContentFinderCondition → InstanceContent.BGM (1345 → CFC 1011 →
                // InstanceContent 104 → BGM 20264 "Untested Voyager"). Use ONLY that static value - do NOT fall back to
                // the live scene read (it returns whatever's playing mid-transition = the PREVIOUS zone's track, which
                // is what produced the "Kugane"/"880 Cartos" MISMATCHES). If the content has no BGM either, return the
                // placeholder so it resolves to SILENCE - a correct silence beats a wrong track.
                if (tb == SilencePlaceholderBgm)
                {
                    uint ic = ResolveInstanceContentBgm(territoryId);
                    return ic != 0 ? ic : tb;   // static content BGM, else the placeholder (→ silence)
                }
                return tb;
            }
        }
        catch { /* fall through */ }
        return 0;
    }

    // Resolve a territory's music via the instanced-content chain: TerritoryType.ContentFinderCondition →
    // .Content (InstanceContent) → .BGM. Returns 0 if the territory isn't instanced content or has no BGM override.
    // (Access pattern mirrors ZoneLoadService's proven TerritoryType.ContentFinderCondition.ValueNullable?.Content.)
    private uint ResolveInstanceContentBgm(uint territoryId)
    {
        try
        {
            var terrSheet = dataManager.GetExcelSheet<TerritoryType>();
            if (terrSheet == null || !terrSheet.HasRow(territoryId)) return 0;
            var cfc = terrSheet.GetRow(territoryId).ContentFinderCondition.ValueNullable;
            if (cfc == null) return 0;
            // Content is an UNTYPED RowRef (it can point at different sheets by ContentType) - resolve it explicitly
            // as InstanceContent. Returns default (RowId 0) if it isn't an InstanceContent row.
            var ic = cfc.Value.Content.GetValueOrDefault<InstanceContent>();
            if (ic == null) return 0;
            return ic.Value.BGM.RowId;
        }
        catch { return 0; }
    }

    // S327l/m/o: LOCATION-BASED BGM NAMING. The game has no readable BGM-id → song-title map (Orchestrion's sheet is
    // keyed by Orchestrion RowId ≠ BGM RowId; titles live only in Orchestrion's community CSV). We name each BGM by the
    // PLACE/DUTY that plays it - "Terncliff", "Clyteum" - which is what an RP host actually wants ("play THIS location's
    // music", not "Rambunctious Waltz of Faeries, movement 2"). Sources, all in-game:
    //  • Open-world / most zones: TerritoryType.BGM (<1000 = direct file track) + TerritoryType.PlaceName.
    //  • INSTANCED content (dungeons/raids/trials): TerritoryType.BGM is usually the SILENCE PLACEHOLDER (1001); the real
    //    track is on the content - ContentFinderCondition → InstanceContent.BGM - named by the DUTY name (CFC.Name).
    //    (Verified: 1345 → CFC 1011 → InstanceContent 104 → BGM 20264. ~558 placeholder territories resolve this way.)
    // The silence placeholder (1001) itself is excluded from naming. Distinct BGMs sharing a name get "#N" suffixes.
    private const uint SilencePlaceholderBgm = 1001;
    private Dictionary<uint, string>? bgmZoneName;

    private void EnsureBgmNames()
    {
        if (bgmZoneName != null) return;
        bgmZoneName = new Dictionary<uint, string>();
        try
        {
            var terr = dataManager.GetExcelSheet<TerritoryType>();
            if (terr == null) return;

            // Pass 1: open-world/zone tracks - FIRST place name per distinct bgm id (skip 0 + silence placeholder).
            var rawByBgm = new Dictionary<uint, string>();
            foreach (var row in terr)
            {
                uint bgmId = row.BGM.RowId;
                if (bgmId == 0 || bgmId == SilencePlaceholderBgm) continue;
                if (rawByBgm.ContainsKey(bgmId)) continue;
                var place = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(place)) continue;
                rawByBgm[bgmId] = NormalizeName(place);
            }

            // Pass 1b: INSTANCED content tracks - enumerate ContentFinderCondition → InstanceContent.BGM, keyed by the
            // duty name. Only add a bgm id not already named by a territory (territory place names win for open zones).
            // This recovers dungeon/raid/trial music that the territory BGM field hides behind the silence placeholder.
            var cfcSheet = dataManager.GetExcelSheet<ContentFinderCondition>();
            if (cfcSheet != null)
            {
                foreach (var cfc in cfcSheet)
                {
                    var ic = cfc.Content.GetValueOrDefault<InstanceContent>();
                    if (ic == null) continue;
                    uint bgmId = ic.Value.BGM.RowId;
                    if (bgmId == 0 || bgmId == SilencePlaceholderBgm) continue;
                    if (rawByBgm.ContainsKey(bgmId)) continue;   // already named by a territory/earlier duty
                    var dutyName = cfc.Name.ToString();
                    if (string.IsNullOrWhiteSpace(dutyName)) continue;
                    rawByBgm[bgmId] = NormalizeName(dutyName);
                }
            }

            // Pass 2: names shared by >1 distinct bgm id → "#N" suffixes (stable, bgm-id order → deterministic #1,#2,…).
            var countByPlace = new Dictionary<string, int>();
            foreach (var name in rawByBgm.Values)
                countByPlace[name] = countByPlace.GetValueOrDefault(name) + 1;

            var seqByPlace = new Dictionary<string, int>();
            foreach (var kv in rawByBgm.OrderBy(k => k.Key))
            {
                string name = kv.Value;
                if (countByPlace[name] > 1)
                {
                    int n = seqByPlace.GetValueOrDefault(name) + 1;
                    seqByPlace[name] = n;
                    bgmZoneName[kv.Key] = name + " #" + n;
                }
                else
                {
                    bgmZoneName[kv.Key] = name;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] EnsureBgmNames failed: " + ex.Message);
        }
    }

    /// <summary>
    /// BGM display name. 0 → "None". The name of the ZONE that plays this BGM ("Terncliff" / "Central Shroud #2"),
    /// else "Track {id}" for a BGM no territory references (rare event/cutscene stingers, or the silence placeholder).
    /// </summary>
    // Normalize a display name: capitalize the leading letter so articled duty names ("the Clyteum", "the Praetorium")
    // read consistently with place names ("Central Shroud"). CFC gives lowercase-articled names; place names are
    // already title-case - this makes them uniform.
    private static string NormalizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (char.IsUpper(s[0])) return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    public string BgmName(uint id)
    {
        if (id == 0) return "None";
        if (id == SilenceTrackBgm) return "Silence";   // BGM 1 = BGM_Null.scd (our explicit Stop target)
        EnsureBgmNames();
        if (bgmZoneName != null && bgmZoneName.TryGetValue(id, out var place) && !string.IsNullOrWhiteSpace(place))
            return place;
        return "Track " + id;
    }

    /// <summary>
    /// The full list of selectable BGMs for the picker: (defaultId, "Zone default (...)"), then (0, "None"), then every
    /// zone-named track sorted by place name. The territory default is passed so the caller can preload it first.
    /// </summary>
    public List<(uint id, string name)> GetBgmChoices(uint territoryDefaultBgm)
    {
        var result = new List<(uint, string)>();
        result.Add((territoryDefaultBgm, "Zone default (" + BgmName(territoryDefaultBgm) + ")"));
        result.Add((0, "None"));
        EnsureBgmNames();
        if (bgmZoneName != null)
        {
            foreach (var kv in bgmZoneName.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Key == territoryDefaultBgm) continue; // already the default entry
                result.Add((kv.Key, kv.Value));
            }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // WRITES - apply the host-set state to the live client
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply weather to the live client. EnvManager holds the active weather byte; setting it re-skins sky/effects.
    /// Weather 0 = atmospheric/undefined (no defined weather; Hyperborea's trick). Safe: null-gated on EnvManager.
    /// Returns true if written.
    /// </summary>
    public bool ApplyWeather(byte weatherId)
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return false;
            // CRASH GUARD (2026-08-16, refined b109): writing ActiveWeather to an off-bank id makes the native
            // weather-change path resolve that weather's avfx via ResourceGraph.FindResourceHandle; if that .avfx path
            // does NOT resolve on this zone the loader dereferences a bad handle → C0000005 (UNCATCHABLE). So we refuse
            // an off-bank native apply BY DEFAULT — foreign weathers render via the EnvState-restamp path
            // (wxpreset/WeatherCramService), which triggers no resource load. EXCEPTION: ids on the AvfxSafeWeatherIds
            // allow-list (proven to native-apply off-bank without faulting — e.g. 150 Apocalypse's meteors on TT128)
            // are permitted, so their doodads spawn; the sky is then crammed OVER the (off-bank/black) native EnvState
            // by SetWeatherUnified's additive path. (0 always passes — the safe default.)
            if (weatherId != 0 && !IsWeatherInLoadedBank(weatherId) && !AvfxSafeWeatherIds.Contains(weatherId))
            {
                log.Warning("[HMSync] ApplyWeather(" + weatherId + " " + WeatherName(weatherId)
                    + ") REFUSED — not in this zone's loaded env bank and not avfx-safe; a native apply would risk "
                    + "faulting the resource loader. Render foreign weathers via a baked preset (wxpreset) instead.");
                return false;
            }
            env->ActiveWeather = weatherId;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] ApplyWeather(" + weatherId + ") failed: " + ex.Message);
            return false;
        }
    }

    // b111 DIAGNOSTIC — drive the game's REAL weather-change pipeline (WeatherManager, not the raw EnvManager byte).
    // WHY: ApplyWeather writes EnvManager+0x27 (ActiveWeather), which only feeds the per-frame sky RECOMPUTE — it does
    // NOT run the weather-VFX-spawn path, so foreign weathers get a sky (via cram) but no doodads (150's meteors never
    // appear). WeatherManager.WeatherInterface.SetNextWeather(id, fade, disablesOverride) is the proper transition the
    // game itself uses: resolve weather → load its env resources (incl. avfx) → spawn. This is the suspected lever the
    // earlier "meteors on Limsa" build used. Isolated as `hmst wxtrans <id> [fade]` so we can PROVE it spawns the VFX
    // before wiring it into the chip path — it is NOT crash-free (the avfx resolve can still fault), so it stays a
    // manual test on the avfx-safe class only. Drives the slot at WeatherIndex (fallback slot 0).
    public string TrySetNextWeather(byte id, float fade)
    {
        try
        {
            var wm = WeatherManager.Instance();
            if (wm == null) return "[HMSync] wxtrans: WeatherManager null — in a zone?";
            var ptrs = wm->WeatherPtrs;                          // Span<Pointer<ServerWeather>> (3 slots)
            int idx = wm->WeatherIndex;
            if (idx < 0 || idx >= ptrs.Length) idx = 0;
            var sw = ptrs[idx].Value;
            if (sw == null) sw = ptrs[0].Value;
            if (sw == null) return "[HMSync] wxtrans: no active weather slot (all null).";
            byte before = wm->WeatherId;
            sw->SetNextWeather(id, fade, true);
            log.Information("[HMSync] wxtrans: SetNextWeather(" + id + " " + WeatherName(id) + ", fade=" + fade
                + ", disablesOverride=true) on slot " + idx + "; WeatherManager.WeatherId was " + before + ".");
            return "[HMSync] wxtrans: transitioning to " + id + " (" + WeatherName(id) + ") via WeatherManager (fade "
                + fade + "s, slot " + idx + "). Watch for its native VFX/doodads — this runs the resource-loading path.";
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] wxtrans(" + id + ") failed: " + ex.Message);
            return "[HMSync] wxtrans failed: " + ex.Message;
        }
    }

    // b113 DIAGNOSTIC — PURE native weather write: `env->ActiveWeather = id` with NO bank guard and NO cram. This is the
    // EXACT pre-guard b99 apply (the build that showed 150's meteors + red sky on Limsa TT128). Isolation lever to settle
    // the retrace: the current additive path does native-write + cram, and shows NO meteors; b99 did native-write ALONE
    // and DID. If `wxnative 150` on 128 reproduces meteors, the CRAM restamp — not the write — is what suppresses the
    // doodads (it overwrites the EnvState the particle emitter reads each frame), and the avfx-safe path should be
    // native-ONLY. Disarms any active cram first so the native env is uncontested. UNGUARDED by design: a weather whose
    // avfx can't resolve on this zone WILL CTD (that is precisely what the production guard exists to prevent) — only
    // hand-test an avfx-safe id on a zone you've already proven safe.
    public string RawNativeWeather(byte id)
    {
        try
        {
            if (weatherCram.ReplayActive) weatherCram.SetReplay(false);   // uncontested native env for the test
            var env = EnvManager.Instance();
            if (env == null) return "[HMSync] wxnative: EnvManager null — in a zone?";
            byte before = env->ActiveWeather;
            env->ActiveWeather = id;
            log.Information("[HMSync] wxnative: ActiveWeather " + before + " → " + id + " (" + WeatherName(id)
                + "), NO guard, NO cram (exact pre-guard b99 raw-native apply).");
            return "[HMSync] wxnative: wrote ActiveWeather=" + id + " (" + WeatherName(id) + ") raw — no guard, no cram. "
                + "Watch for its native sky + doodads. (This IS the b99 apply; an unresolvable avfx here WILL crash.)";
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] wxnative(" + id + ") failed: " + ex.Message);
            return "[HMSync] wxnative failed: " + ex.Message;
        }
    }

    // SESSION-END SANITISE (b113): clear every weather override HMS can leave behind so nothing bleeds past logout /
    // unload (user report: a forced sky left the apartment "immersed in gloom"). Two overrides can persist: (a) the cram
    // restamp (per-frame EnvState override) and (b) a native ActiveWeather write (setweather / wxnative / a wxtrans
    // SetNextWeather transition) — the existing zone-change poll only drops (a), never (b). So: disarm cram, then rewrite
    // ActiveWeather to a NATURAL weather for the current zone (the first id in the loaded bank, always resolvable → no
    // crash) so any forced native id is replaced by a legit one. All null-guarded; safe to call while the client is
    // tearing down at logout. Called on the logged-in→out transition and from Dispose.
    public void SanitiseWeather()
    {
        try
        {
            if (weatherCram.ReplayActive) weatherCram.SetReplay(false);
            var env = EnvManager.Instance();
            if (env == null) return;
            var scene = env->EnvScene;
            if (scene == null) return;
            byte* idTable = (byte*)((nint)scene + 0x30);
            byte natural = idTable[0];                 // first bank id = a weather this zone can always back
            if (natural != 0 && env->ActiveWeather != natural)
            {
                env->ActiveWeather = natural;
                log.Information("[HMSync] weather sanitise: reset ActiveWeather → " + natural + " (" + WeatherName(natural) + ").");
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] weather sanitise failed: " + ex.Message);
        }
    }

    // Runtime ground-truth: is `id` present in the CURRENT zone's loaded env bank (EnvScene.WeatherIds[32] @scene+0x30,
    // the same table DumpEnvbProbe reads)? This is the "will the engine load this weather's resources safely" test —
    // an id NOT in this set faults the resource loader when written to ActiveWeather (see ApplyWeather's crash guard).
    // Fail-OPEN: null EnvManager/EnvScene (mid-load) returns true so legitimate applies are never spuriously blocked.
    public bool IsWeatherInLoadedBank(byte id)
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return true;
            var scene = env->EnvScene;
            if (scene == null) return true;
            byte* idTable = (byte*)((nint)scene + 0x30);
            for (int i = 0; i < 32; i++)
                if (idTable[i] == id) return true;
            return false;
        }
        catch { return true; }
    }

    // S328ac: the map's native weather is resolved by GetDefaultWeather (defined below, sheet+live). The host uses it
    // to broadcast a concrete weather instead of 0, so peers mirror the real sky.

    // S326u/v: read the weather the engine is currently SHOWING so the dropdown mirrors the sky. There are two adjacent
    // bytes: EnvManager+0x26 is the DISPLAYED/current weather (what Weatherman reads as "true weather"), +0x27 is
    // ActiveWeather (the target, which can read 0 mid-transition or before the map settles). We prefer the displayed
    // byte and fall back to the target, so the dropdown shows the real sky rather than a transient 0 = "None".
    public byte GetActiveWeather()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return 0;
            byte displayed = *((byte*)((nint)env + 0x26));   // current/displayed weather
            if (displayed != 0) return displayed;
            return env->ActiveWeather;                        // fall back to the target (0x27)
        }
        catch { return 0; }
    }

    // ── WEATHER-CRAM (Fable weather-cram-755): read-only probe of the LIVE env pipeline ──────────────
    // The runtime counterpart to Fable's STATIC ENVB crack. Fable's binary hunt found NO static ENVB string
    // refs in the exe (magics reached via computed offsets) - but CS exposes the whole thing at RUNTIME:
    //   EnvManager->EnvScene->WeatherIds[32]  = the PARSED per-set weather-id table the resolver walks
    //   EnvScene->EnvSpaces[8].EnvSetResourceHandle = the loaded .envb resource (the donor-injection target)
    // This dumps both so we can (a) see which weather ids the CURRENT zone's bank actually carries, (b) tell
    // whether ActiveWeather is PRESENT in that set - the "miss" condition foreign-weather injection must solve -
    // and (c) validate Fable's cracked ENVB header (nSets @0x1C, rows @0x20) against the live resource bytes.
    // Pure read - mutates nothing (internal env-b probe; logs [ENVB-PROBE] to /xllog). This is step-1 of the
    // resolver hunt: it confirms the data path and the format before any resolver hook is designed.
    public void DumpEnvbProbe()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) { log.Information("[HMSync] [ENVB-PROBE] EnvManager null."); return; }
            byte active = env->ActiveWeather;
            byte displayed = *((byte*)((nint)env + 0x26));
            var scene = env->EnvScene;
            log.Information("[HMSync] [ENVB-PROBE] ActiveWeather=" + active + " (" + WeatherName(active) + ")  displayed="
                + displayed + " (" + WeatherName(displayed) + ")  EnvScene=0x" + ((nint)scene).ToString("X"));
            if (scene == null) return;

            // The parsed per-set weather-id table (FixedSizeArray32<byte> @0x30). Read via raw offset so this
            // doesn't depend on a generated accessor name - it's a diagnostic, robustness first.
            byte* idTable = (byte*)((nint)scene + 0x30);
            var sb = new System.Text.StringBuilder();
            bool activePresent = false;
            for (int i = 0; i < 32; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(idTable[i]);
                if (idTable[i] == active) activePresent = true;
            }
            log.Information("[HMSync] [ENVB-PROBE] EnvScene.WeatherIds[32] = [" + sb + "]");
            log.Information("[HMSync] [ENVB-PROBE] ActiveWeather " + active + " present in runtime set? " + activePresent
                + (activePresent ? "" : "  ← MISS (a foreign weather forced here renders blank without donor injection)"));

            uint locCount = *((uint*)((nint)scene + 0x888));   // EnvScene.LocationCount
            log.Information("[HMSync] [ENVB-PROBE] LocationCount=" + locCount);

            // EnvSpaces: FixedSizeArray8<EnvSpace> @0xF0, stride = sizeof(EnvSpace) = 0xF0; the loaded .envb
            // resource handle sits at EnvSpace+0x90. Walk all 8 slots; report each non-null resource + parse it.
            for (int s = 0; s < 8; s++)
            {
                nint spaceBase = (nint)scene + 0xF0 + s * 0xF0;
                var handle = *((FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle**)(spaceBase + 0x90));
                if (handle == null) continue;
                string name = handle->FileName.ToString();
                uint fileSize = handle->FileSize;
                log.Information("[HMSync] [ENVB-PROBE]   EnvSpace[" + s + "] envb='" + name + "' fileSize=" + fileSize);
                var data = handle->GetDataSpan();
                if (data.Length >= 0x20 && data[0] == (byte)'E' && data[1] == (byte)'N' && data[2] == (byte)'V' && data[3] == (byte)'B')
                    ParseEnvbHeader(data, s);
                else
                    log.Information("[HMSync] [ENVB-PROBE]   EnvSpace[" + s + "] no raw ENVB blob retained (len=" + data.Length
                        + ") - the runtime WeatherIds table above is the source of truth.");
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] [ENVB-PROBE] failed: " + ex.Message); }
    }

    // Parse the ENVB header per Fable's cracked format (weather-cram-755): 'ENVS' tag @0x0C, capacity @0x14,
    // nSets @0x1C, then nSets 16-byte rows @0x20 (endOffset, startOffset, nBlocks, weatherId). We only surface
    // the per-set weatherId list here - enough to cross-check the runtime WeatherIds table and confirm the count
    // is read from 0x1C (Fable's pitfall: 0x14 is a CONSTANT capacity of 6, not the count).
    private void ParseEnvbHeader(ReadOnlySpan<byte> d, int spaceIdx)
    {
        try
        {
            if (!(d[0x0C] == (byte)'E' && d[0x0D] == (byte)'N' && d[0x0E] == (byte)'V' && d[0x0F] == (byte)'S'))
            { log.Information("[HMSync] [ENVB-PROBE]   [" + spaceIdx + "] ENVS tag missing at 0x0C - unexpected layout."); return; }
            uint capacity = BitConverter.ToUInt32(d.Slice(0x14, 4));   // constant 6
            uint nSets = BitConverter.ToUInt32(d.Slice(0x1C, 4));      // TRUE set count
            var sb = new System.Text.StringBuilder();
            int cap = (int)Math.Min(nSets, 64u);   // bound the loop against a bad parse
            for (int i = 0; i < cap && (0x20 + i * 16 + 16) <= d.Length; i++)
            {
                uint weatherId = BitConverter.ToUInt32(d.Slice(0x20 + i * 16 + 12, 4));
                if (i > 0) sb.Append(',');
                sb.Append(weatherId);
            }
            log.Information("[HMSync] [ENVB-PROBE]   [" + spaceIdx + "] parsed ENVB: capacity=" + capacity
                + " nSets=" + nSets + " weatherIds=[" + sb + "]");
        }
        catch (Exception ex) { log.Warning("[HMSync] [ENVB-PROBE]   [" + spaceIdx + "] parse failed: " + ex.Message); }
    }

    // WEATHER-CRAM step-3: hunt the PARSED per-set param storage (the injection target for foreign-weather cram).
    // The .envb blob is freed after parse, so the render's source params live in an undocumented runtime struct
    // reached from EnvSpace. CS gives us two anchors: EnvManager.EnvState @0x58 (size 0x2F8) = the LIVE blended
    // render params (only Rain@0x12C is mapped), and EnvSpace.EnvSetResourceHandle @0x90 whose OWN tail (past the
    // 0xB0 base ResourceHandle → slots 0xB0/0xB8/0xC0) should point at the parsed env-set table. This dumps both:
    //   (a) EnvState as a filtered float window, so a second call under a DIFFERENT weather reveals (by diff) which
    //       offsets are which params - the change-scan the injection write will target;
    //   (b) each EnvSpace's ResourceHandle tail pointers, following any plausible heap pointer one hop and dumping
    //       a window as hex+float - to locate the parsed per-set param block and read its stride.
    // PURE READ, every deref plausibility-guarded (internal env-set dump; logs [ENVSET] to /xllog).
    public void DumpEnvSet()
    {
        try
        {
            var env = EnvManager.Instance();
            if (env == null) { log.Information("[HMSync] [ENVSET] EnvManager null."); return; }
            byte active = env->ActiveWeather;
            var scene = env->EnvScene;
            int slot = -1;
            if (scene != null)
            {
                byte* idTable = (byte*)((nint)scene + 0x30);
                for (int i = 0; i < 32; i++) if (idTable[i] == active) { slot = i; break; }
            }
            log.Information("[HMSync] [ENVSET] ActiveWeather=" + active + " (" + WeatherName(active) + ")  resolvedSlot="
                + (slot >= 0 ? slot.ToString() : "MISS") + "  EnvMgr=0x" + ((nint)env).ToString("X"));

            // (a) EnvState live blended params — filtered float window. Diff two weathers to map param offsets.
            nint esBase = (nint)env + 0x58;
            log.Information("[HMSync] [ENVSET] EnvState @0x" + esBase.ToString("X") + " (offset 0x58, size 0x2F8) — plausible floats:");
            DumpFloatsFiltered(esBase, 0x2F8, "EnvState");

            if (scene == null) { log.Information("[HMSync] [ENVSET] EnvScene null — no EnvSpace walk."); return; }
            // (b) Follow each EnvSpace's EnvSetResourceHandle tail to the parsed param block.
            for (int s = 0; s < 8; s++)
            {
                nint spaceBase = (nint)scene + 0xF0 + s * 0xF0;
                var handle = *((nint*)(spaceBase + 0x90));
                if (!PlausiblePtr(handle)) continue;
                // The env-set container hangs off the handle's own tail (past the 0xB0 base ResourceHandle). +0xC0
                // was the live one: a vtable'd object with count@+0x20, resolvedSlot@+0x22, and two heap pointers
                // (+0x10, +0x18) to the parsed per-set storage. Follow it (hop-2) to map the source param array.
                nint container = *((nint*)(handle + 0xC0));
                if (!PlausiblePtr(container)) { log.Information("[HMSync] [ENVSET] EnvSpace[" + s + "] handle=0x" + handle.ToString("X") + " +0xC0 not a ptr (0x" + container.ToString("X") + ")"); continue; }

                ushort count = *((ushort*)(container + 0x20));
                ushort curSlot = *((ushort*)(container + 0x22));
                nint p10 = *((nint*)(container + 0x10));
                nint p18 = *((nint*)(container + 0x18));
                log.Information("[HMSync] [ENVSET] EnvSpace[" + s + "] handle=0x" + handle.ToString("X")
                    + " container=0x" + container.ToString("X") + " count=" + count + " resolvedSlot=" + curSlot
                    + " p10=0x" + p10.ToString("X") + " p18=0x" + p18.ToString("X"));

                // p10 window — long enough to span >1 param set so a repeating layout reveals the stride by eye.
                // Compare the repeating "+0x20 sky-color"-like field against the live EnvState signature below.
                if (PlausiblePtr(p10))
                {
                    long rl = ReadableLen(p10);
                    log.Information("[HMSync] [ENVSET]   p10 readable=0x" + rl.ToString("X") + "  hex: " + HexBytes(p10, (int)Math.Min(rl, 64)));
                    log.Information("[HMSync] [ENVSET]   p10 floats (filtered, first 0x640):");
                    DumpFloatsFiltered(p10, (int)Math.Min(rl, 0x640), "p10");
                    int found = ScanForWeatherIds(p10, (int)Math.Min(rl, 0x800));
                    if (found >= 0) log.Information("[HMSync] [ENVSET]   p10: WeatherIds byte-run found at +0x" + found.ToString("X"));
                }
                // p18 window — the other pointer; likely the weather-id table or a parallel array. Scan for the run.
                if (PlausiblePtr(p18))
                {
                    long rl = ReadableLen(p18);
                    log.Information("[HMSync] [ENVSET]   p18 readable=0x" + rl.ToString("X") + "  hex: " + HexBytes(p18, (int)Math.Min(rl, 64)));
                    int found = ScanForWeatherIds(p18, (int)Math.Min(rl, 0x800));
                    if (found >= 0) log.Information("[HMSync] [ENVSET]   p18: WeatherIds byte-run found at +0x" + found.ToString("X"));
                }
            }
            // Live EnvState signature for cross-matching the source sets above (sky/fog color block @+0x20..+0x34).
            float* sig = (float*)((nint)env + 0x58);
            log.Information("[HMSync] [ENVSET] EnvState signature @+0x20: "
                + sig[0x20 / 4].ToString("0.####") + " " + sig[0x24 / 4].ToString("0.####") + " "
                + sig[0x28 / 4].ToString("0.####") + " " + sig[0x2C / 4].ToString("0.####")
                + "  (find the source set whose +0x20 matches — that's slot " + active + "'s params)");
        }
        catch (Exception ex) { log.Warning("[HMSync] [ENVSET] failed: " + ex.Message); }
    }

    private static bool PlausiblePtr(nint p) => p > 0x10000 && p < 0x7FFFFFFFFFFF;

    // Scan a region for 958's WeatherIds byte run (1,2,3,4,7 - a distinctive prefix of the zone's parsed set) so
    // whichever source pointer holds the per-set id table is auto-located. Returns the offset or -1.
    private int ScanForWeatherIds(nint addr, int len)
    {
        try
        {
            byte[] needle = { 1, 2, 3, 4, 7 };
            byte* p = (byte*)addr;
            for (int i = 0; i + needle.Length <= len; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++) if (p[i + j] != needle[j]) { hit = false; break; }
                if (hit) return i;
            }
        }
        catch { }
        return -1;
    }

    // Bytes readable from addr within its committed, non-guard, non-noaccess page region (0 if unmapped). Prevents
    // an AccessViolation (uncatchable in .NET) when a followed pointer lands in a smaller allocation than we dump.
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress; public nint AllocationBase; public uint AllocationProtect; public uint __a1;
        public nint RegionSize; public uint State; public uint Protect; public uint Type; public uint __a2;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint lpAddress, out MEMORY_BASIC_INFORMATION mbi, nuint dwLength);

    private static long ReadableLen(nint addr)
    {
        try
        {
            if (VirtualQuery(addr, out var mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) return 0;
            const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
            if (mbi.State != MEM_COMMIT) return 0;
            if ((mbi.Protect & PAGE_NOACCESS) != 0 || (mbi.Protect & PAGE_GUARD) != 0) return 0;
            long end = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            long avail = end - (long)addr;
            return avail > 0 ? avail : 0;
        }
        catch { return 0; }
    }

    // Dump a struct region as floats, logging only offsets whose value is finite, nonzero, and |v| < 1e6 - the
    // "alive" filter from the change-gated float-scan technique. Grouped a few per line to keep /xllog readable.
    private void DumpFloatsFiltered(nint baseAddr, int sizeBytes, string tag)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            int perLine = 0;
            for (int off = 0; off + 4 <= sizeBytes; off += 4)
            {
                float v = *((float*)(baseAddr + off));
                if (float.IsNaN(v) || float.IsInfinity(v) || v == 0f || Math.Abs(v) >= 1e6f) continue;
                if (perLine > 0) sb.Append("  ");
                sb.Append("+0x").Append(off.ToString("X")).Append('=').Append(v.ToString("0.####"));
                if (++perLine == 6) { log.Information("[HMSync] [ENVSET]   " + tag + ": " + sb); sb.Clear(); perLine = 0; }
            }
            if (perLine > 0) log.Information("[HMSync] [ENVSET]   " + tag + ": " + sb);
        }
        catch { log.Information("[HMSync] [ENVSET]   " + tag + ": <fault>"); }
    }

    private static string HexBytes(nint addr, int count)
    {
        var sb = new System.Text.StringBuilder();
        try { byte* p = (byte*)addr; for (int i = 0; i < count; i++) { if (i > 0) sb.Append(' '); sb.Append(p[i].ToString("X2")); } }
        catch { sb.Append("<fault>"); }
        return sb.ToString();
    }

    private static string FloatsInline(nint addr, int count)
    {
        var sb = new System.Text.StringBuilder();
        try { float* p = (float*)addr; for (int i = 0; i < count; i++) { if (i > 0) sb.Append(' '); sb.Append(p[i].ToString("0.###")); } }
        catch { sb.Append("<fault>"); }
        return sb.ToString();
    }

    // WEATHER-CRAM step-4 (b96): "render a weather the zone doesn't carry" now goes through WeatherCramService, which
    // HOOKS the game's UpdateEnvironment recompute and restamps a captured EnvState AFTER Original — the correct lever.
    // b94/b95 proved a per-frame write from Framework.Update is clobbered by that recompute every tick (our write held
    // within the frame but was wiped before the render sampled it: pure ordering loss). Same story as TimeFreezeService.
    // These delegators keep the command surface (wxcapture / wxreplay) unchanged.
    public bool WxReplayActive => weatherCram.ReplayActive;
    public string CaptureEnvState() => weatherCram.Capture();
    public string ToggleWxReplay(bool? on = null) => weatherCram.SetReplay(on);

    // b174 CITY-DIFF TOUR: raw 0x2F8 EnvState snapshot for the cross-city spine-weather diff instrument (wxdifftour).
    // Delegates to the same primitive the preset bake uses (CaptureRaw), but the caller is responsible for turning
    // replay OFF first (so this reads the TRUE native block, never a crammed one) — the tour does that via
    // ToggleWxReplay(false) before it starts capturing. Returns null if EnvManager is unavailable (not in a zone).
    public byte[]? CaptureEnvRaw(out byte active) => weatherCram.CaptureRaw(out active);

    // b160: KEYFRAME sky-graft (day-night cycling). Capture a set of donor EnvState keyframes across the Eorzean day
    // (wxkfcap on the donor, e.g. Kugane, at several times), travel to a fixed-time target, then wxkfreplay on — the
    // graft lerps the bracketing keyframes by the live tod so the stolen sky cycles (moving sun + gradient + stars).
    public bool WxKfActive => weatherCram.KfActive;
    public string AddWeatherKeyframe() => weatherCram.AddKeyframe();
    public string ToggleWxKfReplay(bool? on = null) => weatherCram.SetKfReplay(on);
    public string ClearWeatherKeyframes() => weatherCram.ClearKeyframes();
    // b166 fidelity probe: replay the dense captured set at a reduced keyframe count N (or `full`) to find the storage/quality sweet spot.
    public string DecimateWeatherKeyframes(string arg) => weatherCram.DecimateForReplay(arg);

    // b162: zone-change teardown — drop the graft but keep the captured set (the set must survive the donor→target hop).
    // Mirrors the ReplayActive/CycleActive auto-clears in HMSyncPlugin's zone-change poll. Returns true if it was on.
    public bool StopKfGraftForZoneChange() => weatherCram.StopKfGraft();

    // b183: stop any running day-night graft in place (no zone change) — used when a static weather pick must evict a
    // still-running city-variant graft so the two don't fight over the sky floats. Same underlying teardown.
    public bool StopKfGraft() => weatherCram.StopKfGraft();

    // b162: session-end reset — the graft was persisting across logout/login because nothing in the logout sanitise
    // touched it (SanitiseWeather only reverts ActiveWeather + cram, not the keyframe path). Cancel any in-flight sweep,
    // then clear the whole set + graft so a fresh session starts clean.
    public void ResetKeyframeGraftForSession()
    {
        if (kfSweepActive) CancelKeyframeSweep();
        weatherCram.ClearKeyframes();
    }

    // PATH I b163: durable keyframe-set library. The swept set is memory-only and wiped on teardown (b162), so a ~15s
    // sweep was lost every session and couldn't be validated across zones/sessions. Save bakes the current in-memory set
    // to the config-dir library keyed by its donor weather; Load restores it into memory (turn the graft on afterward).
    public string SaveKeyframeSet(string? name)
    {
        if (!weatherCram.ExportKeyframes(out var weather, out var kfs))
            return "[HMSync] wxkfsave: no graftable set in memory (need >=2 keyframes — sweep or capture first).";
        return keyframeSets.Save(weather, name, kfs);
    }

    public string LoadKeyframeSet(byte weather)
    {
        if (!keyframeSets.TryGet(weather, out var kfs))
            return "[HMSync] wxkfload: no saved set for weather " + weather + ". `wxkflist` to see what's stored.";
        // stop any live sweep/graft before swapping the set out from under it
        if (kfSweepActive) CancelKeyframeSweep();
        weatherCram.StopKfGraft();
        return weatherCram.ImportKeyframes(weather, kfs);
    }

    // b175: DONOR-SPECIFIC load — pick one city's day-set for a weather (the picker's "Clear Skies · Limsa" path). donor 0
    // routes to the legacy byte lookup (untagged, else first available donor). The graft state is donor-agnostic once
    // imported (WeatherCramService just interpolates the loaded set), so only the SELECTION carries the donor here.
    public string LoadKeyframeSet(byte weather, uint donor)
    {
        if (donor == 0) return LoadKeyframeSet(weather);
        if (!keyframeSets.TryGet(weather, donor, out var kfs))
            return "[HMSync] wxkfload: no saved set for weather " + weather + " donor " + donor + ".";
        if (kfSweepActive) CancelKeyframeSweep();
        weatherCram.StopKfGraft();
        return weatherCram.ImportKeyframes(weather, kfs);
    }

    public string ListKeyframeSets()
    {
        var ids = keyframeSets.AvailableIds;
        if (ids.Count == 0) return "[HMSync] wxkflist: no saved keyframe sets. Sweep one (`wxkfsweep`) then `wxkfsave <weatherId> [name]`.";
        var sb = new System.Text.StringBuilder("[HMSync] wxkflist: " + ids.Count + " saved set(s):");
        foreach (var id in ids)
            sb.Append("\n  weather ").Append(id).Append(" — ").Append(keyframeSets.Name(id) ?? "?")
              .Append(" (").Append(keyframeSets.Count(id)).Append(" samples)");
        return sb.ToString();
    }

    // WEATHER-CRAM Tier-1 preset pipeline. The library (WeatherPresetStore) holds baked EnvState blobs keyed by weather
    // id; these three verbs are the seam the command surface AND the sync path drive:
    //   BakeCurrent  — capture the live EnvState (must be standing under the DONOR weather) into the local library.
    //   ApplyPreset  — look the id up in the library and start replaying its blob (the render-a-foreign-sky action).
    //   ClearPreset  — stop replaying (drops back to the zone's native sky).
    // Peer sync ships only a weather id; the receiver calls ApplyPreset(id) against its OWN embedded library, so the
    // sky reproduces without shipping ~760 bytes per map-state. Gating on shipped-only presets keeps that deterministic.

    // Capture the live EnvState and stage it as preset `id` in the config-dir local library. id defaults to the current
    // ActiveWeather (the natural bake: stand under the weather you want, run the verb). Returns a user-facing status.
    public string BakeCurrentPreset(byte? id = null, string? name = null)
    {
        var blob = weatherCram.CaptureRaw(out var active);
        if (blob == null) return "[HMSync] wxbake: could not read EnvState — in a zone with weather?";
        byte target = id ?? active;
        string label = name ?? WeatherName(target);
        // b120 Tier-2: bake the avfx doodad descriptors alongside the EnvState blob, so a persisted/synced apply
        // re-establishes them (full effects, not sky-only). CaptureRaw just snapshotted them (ProbeResourceWords).
        var doodads = weatherCram.GetCapturedDoodads();
        return weatherPresets.Bake(target, label, blob, doodads);
    }

    // Apply a baked preset by weather id: render that foreign sky (+ its baked doodads, if any) on the current zone.
    public string ApplyPreset(byte id)
    {
        if (!weatherPresets.TryGet(id, out var blob))
            return "[HMSync] wxpreset: no baked preset for weather " + id + " (" + WeatherName(id) + "). Bake one with `wxbake` on a donor zone.";
        // b123: candidate-A doodad re-establishment (AllocHGlobal-copy the baked descriptor + repoint) is only crash-safe
        // for PROVEN ids (SafeDoodadIds; see the note there — 207 CTD proved the faithful copy dangles inner pointers).
        // Unproven ids render SKY-ONLY here (pass null doodads) — deterministic and crash-free — until candidate B lands.
        var doodads = DoodadsAllowedFor(id) ? weatherPresets.GetDoodads(id) : null;   // b120→b123 gate; b124 `wxdoodall` can widen
        // b131: strip-list ids (e.g. 208 Floracane) re-establish with inner ptrs zeroed to sever the second-order dangler.
        return weatherCram.ApplyBlob(blob, id, doodads, strip: StripDoodadIds.Contains(id));
    }

    public string ClearPreset() => weatherCram.SetReplay(false);

    // b119 Tier-2 experiment (candidate A): re-establish the captured weather's avfx doodads from self-allocated
    // descriptors and re-point the EnvState words at them, then `wxreplay on` to test whether the game spawns doodads
    // from a descriptor WE own (necessary for persisted/synced doodads). See WeatherCramService.ColdReplayTest.
    public string ColdReplayDoodads(bool strip) => weatherCram.ColdReplayTest(strip);
    // TRAVELING-SUN field map: toggle the read-only wxtimescan diagnostic (discovers the time-driven EnvState float
    // offsets to preserve for a live-sun cram). See WeatherCramService.ToggleTimeScan.
    public string ToggleWeatherTimeScan() => weatherCram.ToggleTimeScan();

    // b128: doodads are ON BY DEFAULT for every preset except the two known crashers (CrashDoodadIds). `wxdoodall` is now
    // an OPT-OUT kill switch, not a widen toggle — `on` (re)enables the extras, `off` suppresses ALL doodads (sky-only).
    // `on`/`off`/null=flip. Since the field is DoodadsDisabled, "enabled" means !DoodadsDisabled.
    public string ToggleAllDoodads(bool? on = null)
    {
        bool enable = on ?? DoodadsDisabled;   // null = flip current state
        DoodadsDisabled = !enable;
        return enable
            ? "[HMSync] wxdoodall: ON — doodad extras enabled (default). Every preset except the two known crashers "
              + "(207 Auroral Flares, 208 Floracane) re-establishes its full avfx. This is the shipped default."
            : "[HMSync] wxdoodall: OFF — doodad extras suppressed; presets render sky-only (crammed sky, no avfx). "
              + "Escape hatch only; `wxdoodall on` restores the default.";
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // b126: WXSWEEP — crash-resumable auto-sweep of every doodad-bearing weather (research tool, NOT a shippable gate).
    //
    // WHY: candidate-A re-establish CTDs on dense-graph weathers whose baked descriptor carries dangling donor-session
    // inner pointers (207 Auroral Flares proved it). The crash is a C0000005 on the GAME's own framework thread during
    // its env tick (sub_1402FE240) — UNCATCHABLE from .NET. So we can't try/catch our way through the list; the only way
    // to survive a crash is to have ALREADY written our progress to disk before applying, so a relaunch can attribute the
    // fault and resume. That is exactly what this does: journal "testing id N" (flushed) → apply → dwell → mark OK →
    // advance. On relaunch, InitWeatherSweep sees a dangling "testing" entry that never reached OK ⇒ that id crashed the
    // game ⇒ mark CRASHED, skip it, resume from N+1. The operator just stands on the sweep map and relaunches after a CTD.
    //
    // GUARDED mode (default): before applying id, InnerPointerReadability(doods) is checked; if ANY inner pointer is
    // currently unmapped the id is SKIP(guard)'d without applying (crash-REDUCER — 207's faulting pointer was unmapped).
    // Known-safe ids (SafeDoodadIds) are always applied. This is imperfect (readable-but-garbage can still fault) but cuts
    // the number of relaunches. Unguarded mode applies every id (maximal crash exposure, fastest to find every landmine).
    //
    // The results are RESEARCH DATA ONLY — surviving ids are sync-unsafe (a peer with a different heap layout can still
    // CTD on the same dangling deref), so they are NOT promoted to SafeDoodadIds by the sweep. Harvest is manual.
    private const int SweepDwellMs = 3000;         // hold each id long enough that a delayed env-tick crash is still attributed to it
    private string? sweepJournalPath;
    private bool sweepActive;
    private bool sweepGuarded = true;
    private uint sweepMap;
    private readonly List<byte> sweepIds = new();
    private int sweepCursor;
    private byte? sweepTesting;                     // id currently under test (journaled BEFORE apply; a CTD leaves it set)
    private long sweepDwellUntilMs;
    private readonly Dictionary<byte, string> sweepResults = new();   // id -> "OK" / "CRASHED" / "SKIP(guard)"

    // DTO mirrored to the on-disk journal (System.Text.Json). Dict keys are strings (JSON requirement).
    private sealed class SweepJournal
    {
        public bool Active { get; set; }
        public bool Guarded { get; set; }
        public uint Map { get; set; }
        public List<byte> Ids { get; set; } = new();
        public int Cursor { get; set; }
        public byte? Testing { get; set; }
        public Dictionary<string, string> Results { get; set; } = new();
    }

    private void SaveSweepJournal()
    {
        if (sweepJournalPath == null) return;
        try
        {
            var dto = new SweepJournal
            {
                Active = sweepActive,
                Guarded = sweepGuarded,
                Map = sweepMap,
                Ids = new List<byte>(sweepIds),
                Cursor = sweepCursor,
                Testing = sweepTesting,
                Results = sweepResults.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(dto);
            // Flush hard: this file is our ONLY crash-survivable record — a lost write means a mis-attributed relaunch.
            File.WriteAllText(sweepJournalPath, json);
        }
        catch (Exception ex) { log.Error(ex, "[HMSync] wxsweep: journal save failed"); }
    }

    // Called once at startup (after mapSettings construction). Loads any journal; if a sweep was mid-flight, attribute a
    // dangling "testing" id to a CRASH (it never reached OK ⇒ the game died applying it) and resume from the next id.
    public void InitWeatherSweep(string path)
    {
        sweepJournalPath = path;
        try
        {
            if (!File.Exists(path)) return;
            var dto = System.Text.Json.JsonSerializer.Deserialize<SweepJournal>(File.ReadAllText(path));
            if (dto == null || !dto.Active) return;   // no in-flight sweep to resume

            sweepGuarded = dto.Guarded;
            sweepMap = dto.Map;
            sweepIds.Clear();
            sweepIds.AddRange(dto.Ids);
            sweepCursor = dto.Cursor;
            sweepResults.Clear();
            foreach (var kv in dto.Results)
                if (byte.TryParse(kv.Key, out var b)) sweepResults[b] = kv.Value;

            if (dto.Testing is byte crashed)
            {
                // The plugin was mid-apply on `crashed` when the game died (or was force-closed). Attribute the CTD.
                sweepResults[crashed] = "CRASHED";
                log.Warning($"[HMSync] wxsweep: RESUME — weather {crashed} ({WeatherName(crashed)}) was mid-apply at last "
                          + "exit ⇒ marked CRASHED. Advancing past it.");
                // advance cursor past the crashed id so we don't re-test it
                int idx = sweepIds.IndexOf(crashed);
                if (idx >= 0) sweepCursor = idx + 1;
            }
            sweepTesting = null;
            sweepActive = true;
            DoodadsDisabled = false;   // ensure doodads are enabled so the sweep exercises candidate-A (default-on anyway)
            sweepDwellUntilMs = 0;
            log.Information($"[HMSync] wxsweep: resumed on map {sweepMap}, cursor {sweepCursor}/{sweepIds.Count} "
                          + $"({sweepResults.Count} results so far). Guarded={sweepGuarded}.");
            SaveSweepJournal();   // persist the crash attribution immediately
        }
        catch (Exception ex) { log.Error(ex, "[HMSync] wxsweep: journal load failed"); }
    }

    public string StartWeatherSweep(uint territory, bool guarded)
    {
        if (sweepActive)
            return "[HMSync] wxsweep: already running — " + WeatherSweepStatus() + " (`wxsweep stop` to abort).";
        if (territory == 0)
            return "[HMSync] wxsweep: no territory loaded. Zone into the sweep map (e.g. Lapis Manalis) first.";

        sweepIds.Clear();
        sweepIds.AddRange(weatherPresets.AvailableIds.Where(id => weatherPresets.HasDoodads(id)));
        if (sweepIds.Count == 0)
            return "[HMSync] wxsweep: no doodad-bearing presets available to sweep.";

        sweepMap = territory;
        sweepGuarded = guarded;
        sweepCursor = 0;
        sweepTesting = null;
        sweepDwellUntilMs = 0;
        sweepResults.Clear();
        sweepActive = true;
        DoodadsDisabled = false;   // doodads are default-on; make sure they aren't killed while the sweep runs
        SaveSweepJournal();
        log.Information($"[HMSync] wxsweep: START on map {territory}, {sweepIds.Count} doodad weathers, "
                      + $"guarded={guarded}, dwell {SweepDwellMs}ms.");
        return $"[HMSync] wxsweep: started — {sweepIds.Count} doodad weathers on map {territory}, "
             + $"guarded={guarded}. Applies one every ~{SweepDwellMs}ms; a CTD is journaled and resumed on relaunch. "
             + "`wxsweep status` for progress, `wxsweep stop` to abort. Do NOT leave the map while it runs.";
    }

    public string StopWeatherSweep()
    {
        if (!sweepActive) return "[HMSync] wxsweep: not running.";
        sweepActive = false;
        sweepTesting = null;
        // (doodads stay default-on after a sweep — they're the shipped default now, not a sweep-scoped widen)
        SaveSweepJournal();
        log.Information("[HMSync] wxsweep: STOPPED by operator. " + WeatherSweepStatus());
        return "[HMSync] wxsweep: stopped. " + WeatherSweepStatus();
    }

    public string WeatherSweepStatus()
    {
        int ok = sweepResults.Count(kv => kv.Value == "OK");
        int crashed = sweepResults.Count(kv => kv.Value == "CRASHED");
        int skipped = sweepResults.Count(kv => kv.Value.StartsWith("SKIP"));
        var sb = new System.Text.StringBuilder();
        sb.Append(sweepActive ? "RUNNING" : (sweepResults.Count > 0 ? "IDLE (last sweep)" : "IDLE"));
        sb.Append($" — {sweepCursor}/{sweepIds.Count} done · OK={ok} CRASHED={crashed} SKIP={skipped}");
        if (sweepActive && sweepTesting is byte t) sb.Append($" · testing {t} ({WeatherName(t)})");
        if (crashed > 0)
        {
            var ids = sweepResults.Where(kv => kv.Value == "CRASHED").Select(kv => kv.Key).OrderBy(x => x);
            sb.Append(" · crashers: " + string.Join(",", ids));
        }
        return sb.ToString();
    }

    // Per-frame pump. Cheap when idle. Runs the state machine: dwell → mark OK → advance → (guard-skip | journal+apply).
    public void TickWeatherSweep(uint currentTerritory)
    {
        if (!sweepActive) return;
        // Only advance while the operator is standing on the sweep map — cramming an off-map weather would corrupt results
        // and could fault the wrong zone. If they zoned away, pause silently until they return.
        if (currentTerritory != sweepMap) return;

        long now = Environment.TickCount64;

        // 1) If an id is under test, wait out its dwell, then record it as a SURVIVOR (no CTD occurred during the hold).
        if (sweepTesting is byte testing)
        {
            if (now < sweepDwellUntilMs) return;   // still holding — a delayed env-tick crash would still be attributed to `testing`
            sweepResults[testing] = "OK";
            log.Information($"[HMSync] wxsweep: {testing} ({WeatherName(testing)}) survived {SweepDwellMs}ms ⇒ OK.");
            sweepTesting = null;
            sweepCursor++;
            SaveSweepJournal();
            return;   // apply the next id on the following tick (keeps one action per frame)
        }

        // 2) Sweep complete?
        if (sweepCursor >= sweepIds.Count)
        {
            log.Information("[HMSync] wxsweep: COMPLETE. " + WeatherSweepStatus());
            sweepActive = false;
            // (doodads remain default-on; the sweep no longer toggles the gate)
            SaveSweepJournal();
            return;
        }

        byte id = sweepIds[sweepCursor];

        // 3) Guarded pre-filter: skip ids whose baked descriptor has any currently-unmapped inner pointer (dangling ⇒
        //    likely faults). Known-safe ids bypass the guard. One skip per tick.
        if (sweepGuarded && !SafeDoodadIds.Contains(id))
        {
            var doods = weatherPresets.GetDoodads(id);
            var (total, unmapped) = weatherCram.InnerPointerReadability(doods);
            if (unmapped > 0)
            {
                sweepResults[id] = $"SKIP(guard {unmapped}/{total} unmapped)";
                log.Information($"[HMSync] wxsweep: {id} ({WeatherName(id)}) SKIP — {unmapped}/{total} inner pointers "
                              + "unmapped (dangling, likely CTD).");
                sweepCursor++;
                SaveSweepJournal();
                return;
            }
        }

        // 4) Apply. Journal the "testing" marker and FLUSH before touching the game — if the apply CTDs, the relaunch
        //    reads this and attributes the crash to `id`.
        sweepTesting = id;
        SaveSweepJournal();   // crash-survivable record written BEFORE the (possibly fatal) apply
        try
        {
            string r = SetWeatherUnified(id);
            log.Information($"[HMSync] wxsweep: applying {id} ({WeatherName(id)}) [{sweepCursor + 1}/{sweepIds.Count}] — {r}");
        }
        catch (Exception ex)
        {
            // A managed exception (NOT the uncatchable native CTD) — record and move on without a relaunch.
            sweepResults[id] = "ERROR: " + ex.Message;
            log.Error(ex, $"[HMSync] wxsweep: managed error applying {id}");
            sweepTesting = null;
            sweepCursor++;
            SaveSweepJournal();
            return;
        }
        sweepDwellUntilMs = now + SweepDwellMs;
    }

    // UNIFIED WEATHER LEVER (Ask 2, 2026-08-16): the single seam the `setweather` command AND the debug weather chips
    // drive. The caller passes a weather id and does NOT care whether it's native to this zone or a foreign cram —
    // this method routes it:
    //   • id == 0 OR id in this zone's loaded env bank → NATIVE apply (ApplyWeather writes ActiveWeather). Any active
    //     restamp is cleared first so the native sky isn't fought by a stale foreign block.
    //   • foreign id on the AVFX-SAFE allow-list (b109, e.g. 150) → native apply for the DOODADS + sky-only cram OVER
    //     it if a preset exists (additive; the native particles play under the crammed sky). See AvfxSafeWeatherIds.
    //   • foreign id WITH a baked preset in the library → restamp path (ApplyPreset → WeatherCramService), the only
    //     crash-free route for a weather this zone can't back natively.
    //   • foreign id WITHOUT a preset → refuse with a redirect (bake one on a donor zone first).
    // Returns a user-facing status string. This is the "same lever" the user asked for: tap any chip / type any id and
    // it just works if it possibly can.
    public string SetWeatherUnified(byte id)
    {
        // b172: ALWAYS drop a running day-night graft before applying ANY explicit weather pick. b170 only stopped the
        // graft on the native branch below, so tapping a non-time-marching FOREIGN chip (which routes through the foreign
        // preset branches further down) armed the static cram but left kfActive running — and the Detour runs the graft's
        // interpolated restamp AFTER the static one, so the graft kept winning the sky floats and the new weather never
        // rendered. That is the reported "after a time-flow chip, other chips don't change the weather until I pick a legal
        // one from the dropdown" bug (the dropdown = a native pick = the one path that DID stop the graft). Hoisting the
        // stop here covers every route (native + all foreign-preset branches). NOT touching ReplayActive here — the foreign
        // branches deliberately USE the static cram; only the native branch clears it (it fights a native sky). No-op when
        // no graft is active, so this is free on the common path.
        weatherCram.StopKfGraft();
        pendingVerifyFrames = 0;   // b173: a fresh explicit pick supersedes any in-flight native black-verify
        if (id == 0 || IsWeatherInLoadedBank(id))
        {
            if (weatherCram.ReplayActive) weatherCram.SetReplay(false);
            bool ok = ApplyWeather(id);
            if (ok) ArmNativeSkyVerify(id);   // b173: verify the resolved sky; rescue if this in-bank id renders black here
            return ok
                ? "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") applied natively."
                : "[HMSync] setweather: native apply of " + id + " (" + WeatherName(id) + ") failed (see log).";
        }
        // b120 Tier-2 (candidate A, PRIMARY PATH): a foreign weather whose baked preset carries avfx DOODAD descriptors
        // renders FULL effects (sky + meteors/particles) purely through cram — ApplyBlob re-establishes live descriptors
        // from the baked bytes and goes wholesale. No native ActiveWeather write needed (that gave off-bank black sky and
        // is what forced the fragile b109 additive dance). This is the "tap the chip, it just works from the persisted
        // preset" path, and it GENERALIZES: any weather with baked doodads works, so the allow-list widens by DATA (bake
        // the doodads → the chip lights up) instead of a hardcoded id set.
        // b123: gate on SafeDoodadIds too — HasDoodads(id) is true for all 98 baked-doodad weathers, but candidate-A
        // faithful-copy only survives on proven ids (207 CTD'd the file thread). Unproven doodad weathers fall through
        // to the sky-only cram below (ApplyPreset now nulls doodads for them) — full effects light up per-id as proven.
        if (weatherPresets.HasDoodads(id) && DoodadsAllowedFor(id))
            return ApplyPreset(id) + " — full effects (crammed sky + re-established doodads).";

        // Foreign & AVFX-SAFE (b109, allow-listed — e.g. 150 Apocalypse) WITHOUT baked doodads: native-apply for the
        // DOODADS (spawns the weather's avfx), then, if a baked preset exists, arm the sky-only cram restamp OVER it.
        // Native is ADDITIVE here — never instead of the cram sky (that was the b107 regression). Cram writes EnvState
        // only and never ActiveWeather, so native particles keep playing while the baked sky renders. This is the legacy
        // same-session path, kept as a fallback for ids not yet re-baked with descriptors.
        if (AvfxSafeWeatherIds.Contains(id))
        {
            bool native = ApplyWeather(id);   // spawn the doodads (meteors)
            if (weatherPresets.Has(id))
            {
                ApplyPreset(id);              // sky-only restamp over the (off-bank/black) native EnvState
                return native
                    ? "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") — native doodads + crammed sky."
                    : "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") — crammed sky only (native doodad apply failed, see log).";
            }
            return native
                ? "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") — native doodads applied (no baked sky preset; "
                    + "`wxbake " + id + "` on a donor for a correct sky under them)."
                : "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") — native doodad apply failed and no baked preset (see log).";
        }
        if (weatherPresets.Has(id))
            return ApplyPreset(id);
        return "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") is not in this zone's env bank and has no "
            + "baked preset. Stand under it on a donor zone and `wxbake " + id + "`, then it renders here.";
    }

    // b170: the picker chip's DAY-NIGHT-AWARE lever. The "* travels the sun" marker promised a moving sky, but tapping a
    // chip only ever armed the STATIC single-snapshot cram (SetWeatherUnified → weatherPresets); the keyframe graft was
    // reachable ONLY via the `wxkfload`+`wxkfreplay` commands — so every asterisked chip was "a lie" on tap, not just the
    // flat ones. This closes that: a weather whose keyframe set genuinely CYCLES (IsTimeMarching) engages the graft — load
    // its swept day-set + turn on the per-frame interpolated restamp — so the sky actually travels the sun with the Eorzea
    // clock. Anything else drops any running graft and falls through to the normal static route. (b182: the keyframe
    // library now ships EMBEDDED so every client has it; the graft itself still applies client-side and isn't broadcast.)
    public string SetWeatherOrGraft(byte id)
    {
        pendingVerifyFrames = 0;   // b173: a chip tap supersedes any in-flight native black-verify (don't let it clobber the graft)
        if (id != 0 && keyframeSets.IsTimeMarching(id))
        {
            if (weatherCram.ReplayActive) weatherCram.SetReplay(false);   // graft owns the sky floats — don't let the static cram fight it
            string load = LoadKeyframeSet(id);                            // ImportKeyframes: pins the donor weather + skip-mask
            string on = weatherCram.SetKfReplay(true);                    // start the per-frame day-night graft
            return load + " " + on;
        }
        weatherCram.StopKfGraft();   // a non-cycling pick must not leave a stale graft running over it
        return SetWeatherUnified(id);
    }

    // b175: DONOR-AWARE graft engage — the picker's per-city sub-selection ("Clear Skies · Limsa") taps this with the city
    // tt. Same shape as SetWeatherOrGraft(byte) but pins the specific donor's swept day-set instead of the "any donor"
    // default. Only engages if THAT donor's set genuinely cycles; otherwise falls back to the static/native unified route
    // (a flat city set shouldn't pretend to travel the sun). b182: the keyframe library ships embedded so every client has
    // it; the graft still applies client-side and isn't broadcast (mirrors SetWeatherOrGraft(byte)).
    public string SetWeatherOrGraft(byte id, uint donor)
    {
        if (donor == 0) return SetWeatherOrGraft(id);
        pendingVerifyFrames = 0;   // a chip tap supersedes any in-flight native black-verify
        if (id != 0 && keyframeSets.IsTimeMarching(id, donor))
        {
            if (weatherCram.ReplayActive) weatherCram.SetReplay(false);   // graft owns the sky floats — don't let static cram fight it
            string load = LoadKeyframeSet(id, donor);                     // ImportKeyframes: pins the donor's day-set + skip-mask
            string on = weatherCram.SetKfReplay(true);                    // start the per-frame day-night graft
            return load + " " + on;
        }
        weatherCram.StopKfGraft();
        return SetWeatherUnified(id);
    }

    // NATIVE-ONLY lever for the picker's "This map's states" promotions (b105, 2026-08-17). These entries come from the
    // live env-bank read (GetLoadedBankWeatherIds) and render NATIVELY — they must NEVER touch the cram path. The
    // dark-map/weather-unresponsive bug came from routing them through SetWeatherUnified: if the combo was opened during
    // a load transition the promoted list could carry an id that is no longer in the CURRENT bank, and if that id
    // happened to have a baked preset, SetWeatherUnified's foreign branch armed a FOREIGN restamp — a donor blob that
    // sticks every frame (map goes dark, native picks stop responding, survives only a session restart). So this path:
    // always drops any active restamp first, then native-applies; if the id isn't in the live bank anymore, it refuses
    // QUIETLY (no cram fallback) rather than arming a stuck override.
    public string SetWeatherNativeOnly(byte id)
    {
        if (weatherCram.ReplayActive) weatherCram.SetReplay(false);   // never let a promoted state ride on a restamp
        weatherCram.StopKfGraft();   // b170: and never let it ride under a running day-night graft either
        pendingVerifyFrames = 0;     // b173: supersede any in-flight native black-verify from a prior pick
        if (id != 0 && !IsWeatherInLoadedBank(id))
            return "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") is no longer in this zone's env bank — skipped (no cram).";
        bool ok = ApplyWeather(id);
        if (ok) ArmNativeSkyVerify(id);   // b173: catch the in-bank-but-black case and rescue it with a good sky
        return ok
            ? "[HMSync] setweather: " + id + " (" + WeatherName(id) + ") applied natively."
            : "[HMSync] setweather: native apply of " + id + " (" + WeatherName(id) + ") failed (see log).";
    }

    // b173: arm the deferred black-sky verify for a NATIVE apply. Cleared by any subsequent explicit pick (so a chip tapped
    // during the window is never clobbered). Armed for every non-zero native id — even ones with no rescue sky — so the
    // /xllog [WXBLACK] line always reports the resolved-EnvState profile (the ground-truth instrument for the black case);
    // the rescue itself only fires when a good sky exists. Weather 0 (deliberate blank) is never armed.
    private void ArmNativeSkyVerify(byte id)
    {
        if (id == 0) { pendingVerifyFrames = 0; return; }
        pendingVerifyId = id;
        pendingVerifyFrames = 15;   // ~1/4s at 60fps: enough for the native UpdateEnvironment recompute to resolve the pick
    }

    // b173: per-frame tick (driven by the plugin's framework Update). When the arm window elapses, sample the freshly
    // resolved EnvState (EnvManager+0x58, 0x2F8 bytes) and score its degeneracy: over the SANE float lanes (finite,
    // |v|<1e4 — the same pointer/garbage filter KeyframeSetStore uses), count how many carry a non-trivial value and the
    // largest magnitude. A black/uninitialised block reads as almost-all-zero (few live lanes, tiny maxAbs); ANY real
    // rendered sky — day OR night — populates dozens of lanes (fog, ambient, cloud, star params are never all zero). If it
    // reads black and we hold a good sky for the id, upgrade: graft when the set travels the sun, else the static cram.
    public void TickNativeSkyVerify()
    {
        if (pendingVerifyFrames <= 0) return;
        if (--pendingVerifyFrames > 0) return;
        byte id = pendingVerifyId;
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return;
            nint es = (nint)env + 0x58;
            int lanes = 0x2F8 / 4;
            int live = 0; float maxAbs = 0f;
            var head = new System.Text.StringBuilder();
            for (int i = 0; i < lanes; i++)
            {
                float v = *(float*)(es + i * 4);
                if (!float.IsFinite(v) || Math.Abs(v) >= 1.0e4f) continue;   // pointer/garbage lane — not a sky float
                float a = Math.Abs(v);
                if (a > 0.001f) live++;
                if (a > maxAbs) maxAbs = a;
                if (i < 16) head.Append(v.ToString("0.###")).Append(' ');
            }
            bool black = live < 6 || maxAbs < 0.02f;
            bool marching = keyframeSets.IsTimeMarching(id);
            bool haveRescue = marching || weatherPresets.Has(id);
            log.Information("[HMSync] [WXBLACK] native weather " + id + " (" + WeatherName(id) + ") resolved: liveLanes="
                + live + " maxAbs=" + maxAbs.ToString("0.###") + " black=" + black + " rescueAvail=" + haveRescue
                + " head=[" + head.ToString().TrimEnd() + "]");
            if (black && haveRescue)
            {
                pendingVerifyFrames = 0;   // belt-and-braces: rescue must not re-arm itself
                string r = marching ? SetWeatherOrGraft(id) : ApplyPreset(id);
                log.Information("[HMSync] [WXBLACK] weather " + id + " rendered black → rescued via "
                    + (marching ? "day-night graft" : "static cram") + ". " + r);
            }
            else if (black)
            {
                log.Information("[HMSync] [WXBLACK] weather " + id + " (" + WeatherName(id) + ") rendered black and no baked "
                    + "preset/graft to rescue it — bake one on a donor (`wxbake " + id + "`).");
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] TickNativeSkyVerify failed: " + ex.Message); }
    }

    // ── preset-library queries (picker/sync) ──
    public bool HasPreset(byte id) => weatherPresets.Has(id);
    public bool HasShippedPreset(byte id) => weatherPresets.HasShipped(id);   // sync-deterministic subset
    public bool HasKeyframeSet(byte id) => keyframeSets.Has(id);              // b168: city keyframe-tour skip check
    public bool HasTimeMarchingSet(byte id) => keyframeSets.IsTimeMarching(id); // b170: only sets that actually cycle earn the UI "*"
    // b175: per-city donor variants for the picker sub-selection. TimeMarchingDonorsFor = only the city sets that actually
    // cycle (a flat city set earns no sub-chip). DonorSetName is the stored label (city · weather) for the tooltip.
    public IReadOnlyList<uint> TimeMarchingDonorsForWeather(byte id) => keyframeSets.TimeMarchingDonorsFor(id);
    public bool HasTimeMarchingSet(byte id, uint donor) => keyframeSets.IsTimeMarching(id, donor);
    public bool HasKeyframeSet(byte id, uint donor) => keyframeSets.Has(id, donor);   // b175: per-donor presence (wxkfcities skip)
    public string? DonorSetName(byte id, uint donor) => keyframeSets.Name(id, donor);
    public IReadOnlyList<byte> WeathersWithDonorVariants => keyframeSets.WeathersWithDonors; // b175: picker "City sky variants" list
    public IReadOnlyList<byte> AvailablePresetIds => weatherPresets.AvailableIds;
    public string PresetName(byte id) => weatherPresets.Name(id) ?? WeatherName(id);

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // BATCH BAKE (wxbakeall) — capture every weather THIS zone natively carries, one at a time, unattended.
    // A weather is only safely captureable where it's IN the zone's loaded env bank (renders natively — setweather is
    // safe, no resource-loader fault). So the operator hops to a donor zone (e.g. 958 for Apocalypse/CutScene) and runs
    // this ONCE per zone: it walks the bank's ids, applies each natively, waits for the sky to blend, snapshots the live
    // EnvState into the local preset library, and restores the original weather when done. Frame-driven state machine —
    // TickBatchBake() is called every frame from OnFrameworkUpdate.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private bool batchActive;
    private List<byte>? batchIds;
    private int batchIndex;
    private int batchSettle;               // frames remaining before the hard-cap timeout for the current weather
    private byte batchOriginalWeather;     // restored when the run finishes
    private int batchBaked;
    private const int BatchSettleMax = 360;   // ~6s hard cap per weather (blend never exceeds this)
    private const int BatchMinFrames = 120;   // ~2s minimum blend before we trust the EnvState is fully the target

    public bool BatchBakeActive => batchActive;

    // Read the CURRENT zone's loaded env-bank weather ids (EnvScene.WeatherIds[32] @scene+0x30) — the set that renders
    // natively here and is therefore safe to setweather + capture. Deduped, zero-skipped, order preserved.
    public List<byte> GetLoadedBankWeatherIds()
    {
        var result = new List<byte>();
        try
        {
            var env = EnvManager.Instance();
            if (env == null) return result;
            var scene = env->EnvScene;
            if (scene == null) return result;
            byte* idTable = (byte*)((nint)scene + 0x30);
            var seen = new HashSet<byte>();
            for (int i = 0; i < 32; i++)
            {
                byte id = idTable[i];
                if (id == 0 || !seen.Add(id)) continue;
                result.Add(id);
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] GetLoadedBankWeatherIds failed: " + ex.Message); }
        return result;
    }

    // Kick off a batch bake of this zone's bank weathers. force=true re-bakes ids already in the library (default skips
    // them — "capture the REMAINING extras"). Returns a user-facing status.
    public string StartBatchBake(bool force = false)
    {
        if (batchActive) return "[HMSync] wxbakeall: already running (" + (batchIndex + 1) + "/" + (batchIds?.Count ?? 0) + "). `wxbakeall stop` to cancel.";
        var ids = GetLoadedBankWeatherIds();
        if (ids.Count == 0) return "[HMSync] wxbakeall: no loaded env bank — in a zone with weather?";
        weatherCram.SetReplay(false);                 // captures must read the true native block, not a crammed one
        batchOriginalWeather = GetActiveWeather();
        var todo = new List<byte>();
        foreach (var id in ids)
            if (id != 0 && (force || !weatherPresets.Has(id))) todo.Add(id);
        int skipped = ids.Count - todo.Count;
        if (todo.Count == 0)
            return "[HMSync] wxbakeall: all " + ids.Count + " of this zone's weathers are already baked — `wxbakeall force` to re-bake.";
        batchIds = todo; batchIndex = 0; batchBaked = 0; batchActive = true;
        batchSettle = BatchSettleMax;
        ApplyWeather(todo[0]);                         // apply the first; TickBatchBake settles+captures+advances
        log.Information("[HMSync] [WXBAKEALL] start: " + todo.Count + " to bake"
            + (skipped > 0 ? " (" + skipped + " already baked, skipped)" : "")
            + " on zone bank [" + string.Join(",", ids) + "]. Restore target=" + batchOriginalWeather + ".");
        return "[HMSync] wxbakeall: baking " + todo.Count + " weather(s) here (~" + (todo.Count * 6) + "s, unattended)"
            + (skipped > 0 ? "; " + skipped + " already baked" : "") + ". Progress in /xllog [WXBAKEALL].";
    }

    public string CancelBatchBake()
    {
        if (!batchActive) return "[HMSync] wxbakeall: not running.";
        batchActive = false;
        ApplyWeather(batchOriginalWeather);
        log.Information("[HMSync] [WXBAKEALL] cancelled after " + batchBaked + " baked.");
        return "[HMSync] wxbakeall: cancelled (" + batchBaked + " baked, original weather restored).";
    }

    // Per-frame driver. No-op unless a batch is running.
    public void TickBatchBake()
    {
        if (!batchActive || batchIds == null) return;
        try
        {
            byte target = batchIds[batchIndex];
            byte shown = GetActiveWeather();
            batchSettle--;
            int elapsed = BatchSettleMax - batchSettle;
            bool ready = elapsed >= BatchMinFrames && shown == target;   // blended enough AND the displayed sky is the target
            bool timedout = batchSettle <= 0;
            if (!ready && !timedout) return;

            var blob = weatherCram.CaptureRaw(out var active);
            if (blob != null && active == target)
            {
                // b121 FIX: CaptureRaw just snapshotted this weather's avfx doodad descriptors (ProbeResourceWords →
                // capturedDoodads). Pass them into Bake exactly as BakeCurrentPreset does, so the batch/tour path
                // PERSISTS the descriptor bytes. Without this the whole-suite tour baked every preset with doodads:null
                // (sky-only, dead cross-session) — the b120 candidate-A proof only held for the manually-baked 150.
                var batchDoodads = weatherCram.GetCapturedDoodads();
                weatherPresets.Bake(target, WeatherName(target), blob, batchDoodads);
                batchBaked++;
                log.Information("[HMSync] [WXBAKEALL] baked " + target + " (" + WeatherName(target) + ") "
                    + (batchIndex + 1) + "/" + batchIds.Count
                    + ", " + batchDoodads.Count + " doodad(s)"
                    + (timedout && shown != target ? " [timeout — sky may not have fully settled]" : ""));
            }
            else
            {
                log.Warning("[HMSync] [WXBAKEALL] SKIP " + target + " (" + WeatherName(target) + "): live ActiveWeather was "
                    + active + ", not " + target + " — not captured.");
            }

            batchIndex++;
            if (batchIndex >= batchIds.Count)
            {
                batchActive = false;
                ApplyWeather(batchOriginalWeather);
                log.Information("[HMSync] [WXBAKEALL] DONE — baked " + batchBaked + " weather(s), restored " + batchOriginalWeather + " (" + WeatherName(batchOriginalWeather) + ").");
                return;
            }
            batchSettle = BatchSettleMax;
            ApplyWeather(batchIds[batchIndex]);
        }
        catch (Exception ex)
        {
            batchActive = false;
            log.Error("[HMSync] [WXBAKEALL] aborted: " + ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // b161: KEYFRAME SWEEP (wxkfsweep) — capture the ENTIRE day-night gradient, unattended.
    // b160's hand-shot keyframes cycle but BAND between the few samples. The game already computes the full gradient
    // (it interpolates the donor weather's envb curves by tod EVERY frame — that's why the sky tracks the slider live).
    // So don't hand-pick keyframes or parse the envb format: DRIVE the donor's clock across the whole day (via the same
    // TimeFreezeService the slider uses) and SAMPLE the game's own resolved EnvState at a fine step. The result IS the
    // native gradient, captured — dense enough that the b160 lerp between samples is imperceptible. Frame-driven state
    // machine (TickKeyframeSweep, pumped each frame from OnFrameworkUpdate), same shape as wxbakeall. Reuses the proven
    // b160 keyframe path verbatim (AddKeyframe tags each sample with the frozen tod; the graft replays the set).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private bool kfSweepActive;
    private int kfSweepStepMin;            // Eorzea-minute step between samples
    private int kfSweepTodMin;             // current tod being sampled, in Eorzea minutes (0..1440)
    private int kfSweepSettle;             // frames left for EnvState to resolve to the new tod before we capture
    private int kfSweepCaptured;
    private bool kfSweepWasFrozen;         // restore the operator's freeze state on completion/cancel
    private (int h, int m) kfSweepPrevTime;
    private const int KfSweepSettleFrames = 8;   // a tod change evaluates ~live (no weather-blend ease); small settle is ample slack

    public bool KeyframeSweepActive => kfSweepActive;

    // Kick off an unattended full-day sweep of the CURRENT (donor) zone under its active weather. stepMinutes = Eorzea
    // minutes between samples (default 15 ≈ 96 keyframes; smaller = smoother + larger). Stay in the donor zone until done.
    public string StartKeyframeSweep(int stepMinutes)
    {
        if (kfSweepActive)
            return "[HMSync] wxkfsweep: already running (" + (kfSweepTodMin / 60) + "h). `wxkfsweep stop` to cancel.";
        var env = EnvManager.Instance();
        if (env == null) return "[HMSync] wxkfsweep: EnvManager null — in the DONOR zone under the target weather?";
        if (stepMinutes < 1) stepMinutes = 15;
        if (stepMinutes > 120) stepMinutes = 120;
        weatherCram.SetReplay(false);      // sample the TRUE native block, not a crammed one
        weatherCram.ClearKeyframes();      // fresh set
        kfSweepWasFrozen = timeFreeze.IsFrozen;
        kfSweepPrevTime = timeFreeze.GetTimeOfDay();
        kfSweepStepMin = stepMinutes;
        kfSweepTodMin = 0;
        kfSweepSettle = KfSweepSettleFrames;
        kfSweepCaptured = 0;
        kfSweepActive = true;
        timeFreeze.FreezeAt(0, 0);         // park at midnight; the tick settles → captures → advances
        int n = (1440 + stepMinutes - 1) / stepMinutes;
        log.Information("[HMSync] [WXKFSWEEP] start: step=" + stepMinutes + "min, ~" + n + " samples, donor weather "
            + GetActiveWeather() + ".");
        return "[HMSync] wxkfsweep: sweeping the full day at " + stepMinutes + "-min steps (~" + n
            + " keyframes, unattended). Stay in the donor zone; progress in /xllog [WXKFSWEEP]. Then travel + `wxkfreplay on`.";
    }

    public string CancelKeyframeSweep()
    {
        if (!kfSweepActive) return "[HMSync] wxkfsweep: not running.";
        kfSweepActive = false;
        RestoreKfSweepFreeze();
        return "[HMSync] wxkfsweep: cancelled (" + kfSweepCaptured + " captured; freeze restored).";
    }

    private void RestoreKfSweepFreeze()
    {
        if (kfSweepWasFrozen) timeFreeze.FreezeAt(kfSweepPrevTime.h, kfSweepPrevTime.m);
        else timeFreeze.Unfreeze();
    }

    // Per-frame driver. No-op unless a sweep is running. Settle a few frames after each tod jump so the game's
    // UpdateEnvironment re-resolves EnvState to the new time, then capture that resolved block as a keyframe.
    public void TickKeyframeSweep()
    {
        if (!kfSweepActive) return;
        try
        {
            kfSweepSettle--;
            if (kfSweepSettle > 0) return;
            weatherCram.AddKeyframe();                  // EnvState has resolved to kfSweepTodMin; tag = the frozen tod
            kfSweepCaptured++;
            kfSweepTodMin += kfSweepStepMin;
            if (kfSweepTodMin >= 1440)                  // wrapped the full day (midnight is covered by the graft's wrap-lerp)
            {
                kfSweepActive = false;
                RestoreKfSweepFreeze();
                log.Information("[HMSync] [WXKFSWEEP] DONE — " + kfSweepCaptured
                    + " keyframes across the day. Travel to the target + `wxkfreplay on`.");
                return;
            }
            timeFreeze.FreezeAt(kfSweepTodMin / 60, kfSweepTodMin % 60);
            kfSweepSettle = KfSweepSettleFrames;
        }
        catch (Exception ex)
        {
            kfSweepActive = false;
            log.Error("[HMSync] [WXKFSWEEP] aborted: " + ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // b166 KEYFRAME TOUR (wxkftour) — the whole-city day-night capture in one unattended command. It is the CROSS
    // PRODUCT of the two existing state machines: OUTER loop walks the donor city's bank weathers (like wxbakeall),
    // INNER loop is a full-day keyframe sweep per weather (wxkfsweep) captured DIRECTLY at the validated knot density
    // (N=30 ⇒ 48-Eorzea-min steps — a uniform 48-min sweep reproduces the same 30-knot set decimation gave, without
    // holding 1440 samples). For each weather: apply it natively → wait for the weather blend to fully settle → sweep
    // the day → save the ~N-sample set to the keyframe-set library keyed by that weather. Skips weathers already saved
    // (resumable; `force` re-does all). Frame-driven: TickKeyframeTour() is pumped from OnFrameworkUpdate alongside
    // TickKeyframeSweep (the inner sweep it starts). Result = keyframe-sets.local.json holding one day-set per city
    // weather — the substrate to fold into an embedded resource (the "bake into the plugin" ship step) afterward.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private bool kfTourActive;
    private List<byte>? kfTourWeathers;
    private int kfTourIndex;
    private int kfTourStepMin;             // sweep step in Eorzea minutes (48 → ~30 knots/day)
    private int kfTourN;                   // target knots/day (for the status line)
    private byte kfTourOriginalWeather;
    private int kfTourSaved;
    private int kfTourBlendSettle;         // frames left in the weather-blend wait before the sweep starts
    private bool kfTourInBlend;            // true = waiting for the weather to blend; false = sweeping (or between)
    private uint kfTourDonor;              // b175: donor tt to tag saved sets with (0 = untagged/legacy). The CITY we're in.
    private string? kfTourDonorName;       // b175: donor city display name folded into each saved set's label
    private const int KfTourBlendFrames = 300;   // ~5s: let the applied weather fully ease in before freezing tod to sweep

    public bool KeyframeTourActive => kfTourActive;

    // Kick off the unattended whole-city tour on the CURRENT (donor city) zone. nTarget = knots/day per weather (default
    // 30, the validated density). force=true re-captures weathers already in the library (default skips them — "grab the
    // remaining"). Stay in the donor until [WXKFTOUR] DONE. Then travel to the target + `wxkfload <id>` + `wxkfreplay on`.
    public string StartKeyframeTour(int nTarget, bool force) => StartKeyframeTour(nTarget, force, 0, null, null);

    // b175: donor-tagged, optionally weather-restricted tour. donorTt tags every saved set with the CITY we're standing in
    // (so Limsa's Clear and Kugane's Clear coexist instead of overwriting); restrictWeathers limits the capture to a
    // specific set (the spine 1/2/3/4/7/15 for the all-cities graft tour) intersected with what this city's bank carries.
    // The skip check is donor-specific — re-touring a city skips only ITS OWN already-captured sets, not another city's.
    public string StartKeyframeTour(int nTarget, bool force, uint donorTt, string? donorName, IReadOnlyList<byte>? restrictWeathers)
    {
        if (kfTourActive)
            return "[HMSync] wxkftour: already running (" + (kfTourIndex + 1) + "/" + (kfTourWeathers?.Count ?? 0)
                + "). `wxkftour stop` to cancel.";
        if (kfSweepActive) return "[HMSync] wxkftour: a manual sweep is running — `wxkfsweep stop` first.";
        var ids = GetLoadedBankWeatherIds();
        if (ids.Count == 0) return "[HMSync] wxkftour: no loaded env bank — stand in the DONOR city zone.";
        if (nTarget < 2) nTarget = 30;
        if (nTarget > 1440) nTarget = 1440;
        int step = Math.Max(1, (int)Math.Round(1440.0 / nTarget));
        if (step > 120) step = 120;                    // StartKeyframeSweep's own clamp; keep the estimate honest
        var todo = new List<byte>();
        foreach (var id in ids)
        {
            if (id == 0) continue;
            if (restrictWeathers != null && !restrictWeathers.Contains(id)) continue;   // spine-only, when restricted
            if (force || !keyframeSets.Has(id, donorTt)) todo.Add(id);                   // donor-specific skip
        }
        int skipped = ids.Count - todo.Count;
        if (todo.Count == 0)
            return "[HMSync] wxkftour: all of this city's" + (restrictWeathers != null ? " spine" : "")
                + " weathers already have saved sets for this donor — `wxkftour force` to re-capture.";
        weatherCram.SetReplay(false);                  // capture the TRUE native block, never a crammed one
        weatherCram.StopKfGraft();
        kfTourOriginalWeather = GetActiveWeather();
        kfTourDonor = donorTt; kfTourDonorName = donorName;
        kfTourWeathers = todo; kfTourIndex = 0; kfTourSaved = 0;
        kfTourStepMin = step; kfTourN = nTarget; kfTourActive = true;
        ApplyWeather(todo[0]);                          // arm the first weather; the tick blends → sweeps → saves → advances
        kfTourInBlend = true; kfTourBlendSettle = KfTourBlendFrames;
        int perW = (KfTourBlendFrames + nTarget * KfSweepSettleFrames) / 60 + 1;
        log.Information("[HMSync] [WXKFTOUR] start: " + todo.Count + " weather(s) @ " + nTarget + " knots ("
            + step + "-min steps)" + (skipped > 0 ? ", " + skipped + " already saved" : "")
            + " on bank [" + string.Join(",", ids) + "]. Restore=" + kfTourOriginalWeather + ".");
        return "[HMSync] wxkftour: capturing " + todo.Count + " weather(s) at ~" + nTarget + " knots/day (~"
            + (todo.Count * perW) + "s, unattended). Stay in the donor; progress in /xllog [WXKFTOUR].";
    }

    public string CancelKeyframeTour()
    {
        if (!kfTourActive) return "[HMSync] wxkftour: not running.";
        kfTourActive = false;
        if (kfSweepActive) CancelKeyframeSweep();
        ApplyWeather(kfTourOriginalWeather);
        return "[HMSync] wxkftour: cancelled (" + kfTourSaved + " saved; original weather restored).";
    }

    // Per-frame driver. No-op unless a tour is running. Two phases per weather: BLEND (wait for the applied weather to
    // ease in) then SWEEP (delegate to the kfSweep machine; wait for it to finish, then save the set and advance).
    public void TickKeyframeTour()
    {
        if (!kfTourActive || kfTourWeathers == null) return;
        try
        {
            byte target = kfTourWeathers[kfTourIndex];
            if (kfTourInBlend)
            {
                kfTourBlendSettle--;
                // ActiveWeather flips instantly; the wait is for the visual EnvState blend. Require the target shown AND
                // a floor of frames so the sweep's tod-0 capture is fully the target weather, not a mid-blend of the last.
                bool ready = GetActiveWeather() == target && (KfTourBlendFrames - kfTourBlendSettle) >= 120;
                if (!ready && kfTourBlendSettle > 0) return;
                kfTourInBlend = false;
                StartKeyframeSweep(kfTourStepMin);      // clears keyframes+master, freezes tod=0, begins the full-day sweep
                return;
            }
            if (kfSweepActive) return;                  // inner sweep still running (TickKeyframeSweep pumps it)

            // sweep finished — the ~N-sample day-set for `target` is in memory (kfWeather pinned to it). Persist it.
            if (weatherCram.ExportKeyframes(out var w, out var kfs) && w == target && kfs.Count >= 2)
            {
                // b175: tag with the donor city so per-city variants coexist. Label = the CITY name when donor-tagged (the
                // picker sub-chip wants "Limsa Lominsa"; the weather is already the key); WeatherName for the untagged path.
                string label = (kfTourDonor != 0 && kfTourDonorName != null) ? kfTourDonorName : WeatherName(target);
                keyframeSets.Save(target, kfTourDonor, label, kfs);
                kfTourSaved++;
                log.Information("[HMSync] [WXKFTOUR] saved weather " + target + " (" + WeatherName(target) + ")"
                    + (kfTourDonor != 0 ? " donor " + kfTourDonor : "") + " — "
                    + kfs.Count + " knots, " + (kfTourIndex + 1) + "/" + kfTourWeathers.Count);
            }
            else
                log.Warning("[HMSync] [WXKFTOUR] SKIP weather " + target + " (" + WeatherName(target)
                    + "): swept set was weather " + w + " / " + kfs.Count + " kf — not saved.");

            kfTourIndex++;
            if (kfTourIndex >= kfTourWeathers.Count)
            {
                kfTourActive = false;
                ApplyWeather(kfTourOriginalWeather);
                log.Information("[HMSync] [WXKFTOUR] DONE — saved " + kfTourSaved + " day-set(s), restored "
                    + kfTourOriginalWeather + " (" + WeatherName(kfTourOriginalWeather)
                    + "). Travel to the target + `wxkfload <id>` + `wxkfreplay on`.");
                return;
            }
            ApplyWeather(kfTourWeathers[kfTourIndex]);
            kfTourInBlend = true; kfTourBlendSettle = KfTourBlendFrames;
        }
        catch (Exception ex)
        {
            kfTourActive = false;
            log.Error("[HMSync] [WXKFTOUR] aborted: " + ex.Message);
        }
    }

    // S327f: TIME FREEZE now goes through TimeFreezeService, which HOOKS the game's UpdateEorzeaTime recompute and
    // no-ops it while frozen, then writes ClientTime.EorzeaTime (the field the renderer reads) - Brio's mechanism. The
    // old EorzeaTimeOverride (0x30) approach was a confirmed dead end (the recompute ignores it and clobbers EorzeaTime
    // every frame - proven by the [TIME-MIRROR] "was" value marching while overridden=True). These methods keep their
    // names so the sync architecture (epoch-gated MapState apply) is unchanged.
    public bool ApplyTime(ushort hour, byte minute)
    {
        timeFreeze.FreezeAt(hour, minute);
        return true;
    }

    // Freeze at the CURRENT live time (tap Freeze without dragging → pin "now", not a stale stored value).
    public void FreezeAtCurrent() => timeFreeze.FreezeAtCurrent();

    // Release the freeze - the recompute resumes and real time flows again.
    public void DisableTimeOverride() => timeFreeze.Unfreeze();

    // Is time currently held?
    public bool IsTimeOverridden() => timeFreeze.IsFrozen;

    /// <summary>
    /// ALL weathers for the dropdown, each flagged legal (native to this territory) or not. Order: legal ones first
    /// (in WeatherRate order), then every other weather by id. The UI renders legal in white, extras in faint ivory,
    /// so the host can force any of the game's ~216 weathers while seeing which are native. Weather 0 is NOT included
    /// here (it's the explicit "None - Atmospheric" entry the UI prepends). Returns just legals if the sheet fails.
    /// </summary>
    // S326t: the set of weather ids that are used by AT LEAST ONE map (i.e. appear in any WeatherRate row's slots).
    // Of 216 Weather rows, only ~74 are ever referenced by a rate table; the rest are dead duplicates/unused. The
    // experimental "show more" list is filtered to this set so it's ~74 meaningful choices, not 216 with dupes. Cached.
    private HashSet<byte>? usedWeatherIds;
    private HashSet<byte> GetUsedWeatherIds()
    {
        if (usedWeatherIds != null) return usedWeatherIds;
        var set = new HashSet<byte>();
        try
        {
            var rateSheet = dataManager.GetExcelSheet<WeatherRate>();
            if (rateSheet != null)
                foreach (var rate in rateSheet)
                    foreach (var wRef in rate.Weather)
                    {
                        var wid = (byte)wRef.RowId;
                        if (wid != 0) set.Add(wid);
                    }
        }
        catch (Exception ex) { log.Warning("[HMSync] GetUsedWeatherIds failed: " + ex.Message); }
        usedWeatherIds = set;
        return set;
    }

    public List<(byte id, string name, bool legal)> GetAllWeatherFlagged(uint territoryId)
    {
        var result = new List<(byte, string, bool)>();
        try
        {
            // The territory's legal set (ids), preserving order.
            var legalIds = new List<byte>();
            var legalSet = new HashSet<byte>();
            foreach (var (wid, _) in GetLegalWeather(territoryId))
            {
                if (wid == 0 || legalSet.Contains(wid)) continue;
                legalIds.Add(wid); legalSet.Add(wid);
            }

            var weatherSheet = dataManager.GetExcelSheet<Weather>();
            if (weatherSheet == null) return result;

            // Legal first.
            foreach (var wid in legalIds)
            {
                var name = weatherSheet.GetRow(wid).Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = "Weather " + wid;
                result.Add((wid, name, true));
            }
            // Then all OTHER weathers that are used by SOME map (deduped to the ~74 real ones - skip the rest).
            var used = GetUsedWeatherIds();
            foreach (var id in used)
            {
                if (id == 0 || legalSet.Contains(id)) continue;
                if (!weatherSheet.HasRow(id)) continue;
                var name = weatherSheet.GetRow(id).Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add((id, name, false));
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] GetAllWeatherFlagged(" + territoryId + ") failed: " + ex.Message);
        }
        return result;
    }

    /// <summary>
    /// The zone's ACTUAL weather set, read from its <c>.lvb</c> weather table (up to 32 slots) - includes cinematic
    /// weathers (e.g. CutScene) that never appear in the WeatherRate sheet. Each entry is flagged legal via the
    /// WeatherRate set. Cached per territory. Empty if the zone has no LVB / the parse fails (caller falls back).
    /// </summary>
    public List<(byte id, string name, bool legal)> GetLvbWeathers(uint territoryId)
    {
        if (lvbWeatherCache.TryGetValue(territoryId, out var cached)) return cached;
        var result = new List<(byte, string, bool)>();
        try
        {
            var terrSheet = dataManager.GetExcelSheet<TerritoryType>();
            if (terrSheet == null || !terrSheet.HasRow(territoryId)) { lvbWeatherCache[territoryId] = result; return result; }
            var bg = terrSheet.GetRow(territoryId).Bg.ToString();
            if (string.IsNullOrWhiteSpace(bg)) { lvbWeatherCache[territoryId] = result; return result; }

            var lvb = dataManager.GetFile<LvbFile>("bg/" + bg + ".lvb");
            if (lvb?.WeatherIds == null || lvb.WeatherIds.Length == 0) { lvbWeatherCache[territoryId] = result; return result; }

            var legalSet = new HashSet<byte>();
            foreach (var (wid, _) in GetLegalWeather(territoryId)) if (wid != 0) legalSet.Add(wid);

            var weatherSheet = dataManager.GetExcelSheet<Weather>();
            var seen = new HashSet<byte>();
            foreach (var raw in lvb.WeatherIds)
            {
                if (raw == 0 || raw >= 255) continue;
                var id = (byte)raw;
                if (!seen.Add(id)) continue;
                if (weatherSheet == null || !weatherSheet.HasRow(id)) continue;
                var name = weatherSheet.GetRow(id).Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = "Weather " + id;
                result.Add((id, name, legalSet.Contains(id)));
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] GetLvbWeathers(" + territoryId + ") failed: " + ex.Message);
        }
        lvbWeatherCache[territoryId] = result;
        return result;
    }

    /// <summary>
    /// The zone's weather set for the picker: prefer the LVB table (zone-accurate, includes cinematic weathers like
    /// CutScene); fall back to the WeatherRate-derived list if the zone has no readable LVB. When <paramref name="includeAll"/>
    /// is set (debug mode), the ENTIRE game weather set is appended after the zone set - for hunting anomalous
    /// weathers that aren't in a zone's LVB but still produce an effect (rare, not normally applicable).
    /// </summary>
    public List<(byte id, string name, bool legal)> GetZoneWeathers(uint territoryId, bool includeAll)
    {
        var lvb = GetLvbWeathers(territoryId);
        var baseList = lvb.Count > 0 ? lvb : GetAllWeatherFlagged(territoryId);
        if (!includeAll) return baseList;

        // DEBUG grid (Ask 3, 2026-08-16): enumerate the ENTIRE Weather sheet, not just the WeatherRate-USED subset.
        // The old path appended only GetAllWeatherFlagged (ids referenced by some WeatherRate), which SILENTLY DROPPED
        // cinematic-only ids that no zone's rate table carries — that's exactly why 958's Apocalypse (150) and CutScene
        // didn't appear as chips and had to be found via Weatherman. In debug mode we want every named weather tappable
        // so any exotic can be crammed on any zone. Legal flag preserved from the zone's base set.
        var have = new HashSet<byte>();
        var legalSet = new HashSet<byte>();
        foreach (var (id, _, legal) in baseList) { have.Add(id); if (legal) legalSet.Add(id); }
        var result = new List<(byte, string, bool)>(baseList);
        try
        {
            var weatherSheet = dataManager.GetExcelSheet<Weather>();
            if (weatherSheet != null)
                foreach (var row in weatherSheet)
                {
                    if (row.RowId == 0 || row.RowId > byte.MaxValue) continue;
                    var id = (byte)row.RowId;
                    if (have.Contains(id)) continue;
                    var name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;   // unnamed sheet padding — skip
                    have.Add(id);
                    result.Add((id, name, legalSet.Contains(id)));
                }
        }
        catch (Exception ex) { log.Warning("[HMSync] GetZoneWeathers(all) sheet-enum failed: " + ex.Message); }
        return result;
    }

    // b105: every NAMED Weather-sheet id (0<id<=255, non-empty name) — the exact set the debug chip grid shows. Used by
    // the coverage report to separate "capturable but unbaked" from "no env bank anywhere" (sheet rows like Clear Skies
    // 91 / Core Radiation 37-39 / Eternal Bliss 83 that no territory carries → uncapturable via the native-bake path).
    public List<byte> AllNamedWeatherIds()
    {
        var ids = new List<byte>();
        try
        {
            var weatherSheet = dataManager.GetExcelSheet<Weather>();
            if (weatherSheet != null)
                foreach (var row in weatherSheet)
                {
                    if (row.RowId == 0 || row.RowId > byte.MaxValue) continue;
                    if (string.IsNullOrWhiteSpace(row.Name.ToString())) continue;
                    ids.Add((byte)row.RowId);
                }
        }
        catch (Exception ex) { log.Warning("[HMSync] AllNamedWeatherIds failed: " + ex.Message); }
        return ids;
    }

    // b106: BANK-LESS weathers — named Weather-sheet rows that NO territory's env bank (EnvbWeathers) carries, so they
    // are uncapturable via the native-bake path (load a donor → drive the weather → snapshot EnvState). Confirmed by an
    // offline xivtool survey across all 1126 territories' EnvbWeathers (weather-atlas.csv): none of these six appears in
    // any bank; id 36 sits in TT196's WeatherRate POOL but not its envb, so even that map can't render it. They are
    // cutscene-hardcoded skies with no weather-keyed bank entry. We exclude them from the guessable "extra presets" grid
    // so they stop showing as permanently-grey chips a user can never satisfy — they were never capturable, not merely
    // unbaked. If a future cutscene-EnvState-snapshot path ever backs one, drop it from this set to re-expose it.
    //   91 Clear Skies · 36/37/38/39 Core Radiation · 83 Eternal Bliss
    public static readonly HashSet<byte> BankLessWeatherIds = new() { 91, 36, 37, 38, 39, 83 };

    // AVFX-SAFE allow-list (b109, 2026-08-17). Weathers whose native avfx (particle "doodads") RESOLVE broadly enough
    // to native-apply OFF-BANK without faulting the resource loader — so we can spawn their doodads (meteors, etc.)
    // instead of only cramming a static sky. A native ActiveWeather write kicks off TWO independent subsystems: (1) the
    // sky/EnvState blend, keyed by THIS zone's env bank — an off-bank id misses → black sky (non-fatal); and (2) the
    // avfx spawn via ResourceGraph.FindResourceHandle, a GLOBAL resource lookup — if that weather's .avfx path resolves,
    // the particles spawn; if not, the loader dereferences a bad handle → C0000005 CTD (UNCATCHABLE in .NET). So avfx
    // safety is PER-WEATHER, not a property of being off-bank. 150 (Apocalypse) was CONFIRMED to render its full meteors
    // on Limsa TT128 (bank {1,2,3,4,7,15}, no 150) with NO crash — its meteor avfx is broadly resolvable. This list is
    // the OPPOSITE of a suffocating guard: it opens native apply ONLY for ids we've PROVEN safe, leaving every other
    // foreign weather on the reliable cram-only (sky restamp) path. SetWeatherUnified pairs a native apply here with a
    // sky-only cram OVER it (WeatherCramService writes EnvState only, never ActiveWeather, and preserves the zone's avfx
    // handles) so you get native doodads + a correct crammed sky at once. CAVEAT: avfx resolvability can be per-zone (an
    // old note claimed 150 once faulted on Limsa Upper Decks) — 150 is proven on 128, not guaranteed everywhere; if it
    // ever CTDs on a zone, that zone's resource graph can't resolve the meteor avfx. Grow this ONLY after in-game proof
    // (or at runtime via `hmst wxdood <id>`, session-scoped — resets to this seed on relaunch).
    //   150 Apocalypse (meteors)
    public static readonly HashSet<byte> AvfxSafeWeatherIds = new() { 150 };

    // CANDIDATE-A DOODAD-SAFE allow-list (b123, 2026-08-17). Distinct from AvfxSafeWeatherIds (that gates the NATIVE
    // avfx-spawn path). This gates the b120 candidate-A RE-ESTABLISH path: applying a persisted preset's baked doodad
    // descriptor by AllocHGlobal-copying its bytes and repointing the EnvState word at our buffer. b122 shipped all 204
    // presets' descriptors, but candidate-A faithful-copy proved UNSAFE IN GENERAL: weather 207 (Auroral Flares) CTD'd
    // the file thread (C0000005 read @0x20, Penumbra ReadSqPack → game loader) while flicking presets on TT958. The
    // 0x2C0/0x2C8 target is NOT a clean "path + transform" blob — it's a LIVE avfx emitter OBJECT-GRAPH with 35–39 real
    // heap pointers into the DONOR session. A verbatim copy leaves those inner pointers DANGLING; the game's file thread
    // walks one, hits a field that resolved to null, and reads null+0x20 → uncatchable fault. 150 has only ~2 shallow
    // inner pointers and survives (and is relaunch-proven); dense-graph weathers (149/196/201 rendered by LUCK — their
    // dangling pointers happened to land on still-committed memory) are landmines. So candidate A re-establish is opened
    // ONLY for ids PROVEN stable; every other doodad weather renders sky-only (crash-safe, still deterministic) until the
    // robust general mechanism (candidate B: game-INSTANTIATED avfx by path, so the object-graph is valid, no dangling
    // pointers) lands. Grow this set ONLY after cross-session in-game proof. The 204 baked descriptors stay embedded —
    // they cost nothing dormant and light up the moment an id is proven or candidate B replaces the copy.
    //   150 Apocalypse (meteors) — relaunch-proven cross-session, ~2 inner pointers
    public static readonly HashSet<byte> SafeDoodadIds = new() { 150 };

    // b128 DOODAD KILL SWITCH (was b124 `PermitAllDoodads`, inverted). Doodads are now ON BY DEFAULT (see the b128 policy
    // note on CrashDoodadIds below), so this is no longer a "widen" gate — it's an OPT-OUT kill switch. `wxdoodall off`
    // sets it true to suppress ALL doodad re-establish (extras render sky-only), `wxdoodall on` clears it. Default false =
    // doodads enabled. Session-scoped (resets on plugin reload). Kept mainly as an escape hatch if a future patch shifts
    // the EnvState layout and doodads start misbehaving before the blocklist can be updated.
    public static bool DoodadsDisabled;

    // b127→b128 KNOWN-CRASHER BLOCKLIST + DEFAULT-ON POLICY (2026-08-17). The empirical manual sweep across all 98 doodad
    // weathers on TT999 found EXACTLY TWO hard crashers — 207 (Auroral Flares) and 208 (Floracane), both C0000005 in the
    // env routine (sub_1402FE240). Every OTHER id re-established its doodads and rendered cleanly (many spectacularly:
    // 194/195 real meteor showers, 102/103/104, 137/22/143/68/20). This DEMOLISHED the b126 readability guard's premise
    // (it flagged 96/98 as "likely CTD" on unmapped inner pointers, but 94 render fine — the env routine only walks a FEW
    // descriptor offsets, so a weather crashes iff a WALKED offset is bad, not because it has any dangling word). So the
    // ground truth is this tiny blocklist, not a per-id allow-list. POLICY (user decision, b128): doodads are ALWAYS ON by
    // default for EVERY preset except these two — the full "bells and whistles" load for all extras, no per-id proving and
    // no manual toggle. The two crashers (207/208) are ALWAYS refused → sky-only SELECTIVE restamp (preserves the DEST
    // zone's own pointer words), deterministic and crash-free. (SafeDoodadIds/AvfxSafeWeatherIds above are now largely
    // historical — the gate no longer consults SafeDoodadIds; kept for the AvfxSafeWeatherIds native-doodad branch.)
    //
    // b133 ROOT CAUSE (corrects the b130 "second-order deref" verdict — that was WRONG): the crash is a FIRST-order deref
    // of a STALE, un-re-established EnvState pointer. The env routine sub_1402FE240 walks the EnvState doodad words at
    // 0x2C0 AND 0x2C8. The SHIPPED preset for 207/208 bakes ONLY the 0x2C0 descriptor — 0x2C8 was never captured. So
    // ReestablishDoodads re-points/strips 0x2C0 but leaves 0x2C8 holding the baked EnvState blob's STALE donor pointer,
    // which the WHOLESALE restamp (skipWord=null) then stamps into live memory → the routine follows it → CTD. PROOF: the
    // b130 fault addr for 207 (0x1F834914FB0) == the embedded 207 EnvState word at 0x2C8 EXACTLY; ditto 208 (…F60). Only
    // 207/208 are affected because they're the ONLY doodad-weathers whose EnvState 0x2C8 is an UNCOVERED pointer — every
    // other id either baked a 0x2C8 descriptor (re-pointed to a valid buffer) or has 0x2C8==0 (harmless). (0x2D0 is
    // uncovered/stale in 37 weathers but never CTDs → the routine does NOT walk 0x2D0.) `wxcoldtest strip` "worked" only
    // because the LIVE capture included BOTH descriptors (the WXPROBE showed 2), so it re-pointed+stripped 0x2C8 too.
    // FIX-TO-SHIP: re-bake 207/208 with BOTH descriptors (the 0x2C8 one is 208's ce_hina petals / 207's ce_auro01n), then
    // they can move to StripDoodadIds and ship. Until re-baked they stay here (sky-only).
    public static readonly HashSet<byte> CrashDoodadIds = new() { 207, 208 };

    // STRIP-LIST (the "sever the inner danglers" ship path, kept as infrastructure). An id here re-establishes its doodads
    // with the descriptor's inner heap-pointer words ZEROED (ReestablishDoodads strip=true) so the game re-resolves from
    // the inline .avfx path instead of a stale pointer. Proven viable via `wxcoldtest strip` on LIVE captures (b131 208
    // petals, b132 207 auroras) — but shipping it requires the EMBEDDED preset to carry EVERY EnvState-referenced
    // descriptor (0x2C0 AND 0x2C8), else the un-baked slot wholesale-stamps a stale pointer and CTDs (see CrashDoodadIds
    // b133 note). EMPTY until 207/208 are re-baked with both descriptors; then add them here and remove from CrashDoodadIds.
    public static readonly HashSet<byte> StripDoodadIds = new() { };

    // True if this id's baked doodads are allowed to re-establish: ON for everything except the crashers, unless the
    // operator killed doodads (`wxdoodall off`). b130 DIAG BYPASS retained: while `wxdooddiag` is armed we let the crashers
    // re-establish so the VEH can catch the fault (the forensic path for a re-bake→strip verification cycle).
    public bool DoodadsAllowedFor(byte id)
        => !DoodadsDisabled && (!CrashDoodadIds.Contains(id) || weatherCram.FaultDiagArmed);

    /// <summary>
    /// The territory's native weather id. S328ac: prefer the LIVE WeatherManager (GetWeatherForDaytime(territory, 0)) -
    /// the weather the game would actually show right now, respecting a territory's individual/special weather - so the
    /// host broadcasts the REAL sky instead of 0 (which peers render as "None / atmospheric"). Falls back to the
    /// WeatherRate sheet's first non-zero entry (the dominant/native weather) if the live manager is unavailable, and
    /// to 0 if neither resolves. Also used to label the "Default - {name}" choice and the prepopulated value on a
    /// fresh territory.
    /// </summary>
    public byte GetDefaultWeather(uint territoryId)
    {
        if (territoryId == 0) return 0;
        // Live path: what the game would actually show now (special/individual weather aware).
        try
        {
            if (territoryId <= ushort.MaxValue)
            {
                var wm = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager.Instance();
                if (wm != null)
                {
                    byte live = wm->GetWeatherForDaytime((ushort)territoryId, 0);   // 0 = now
                    if (live != 0) return live;
                }
            }
        }
        catch { /* fall through to the sheet */ }
        // Sheet fallback: the WeatherRate table's first non-zero entry (the dominant/native weather).
        try
        {
            var terrSheet = dataManager.GetExcelSheet<TerritoryType>();
            var rateSheet = dataManager.GetExcelSheet<WeatherRate>();
            if (terrSheet == null || rateSheet == null || !terrSheet.HasRow(territoryId)) return 0;
            var rateId = terrSheet.GetRow(territoryId).WeatherRate.RowId;
            if (!rateSheet.HasRow(rateId)) return 0;
            foreach (var wRef in rateSheet.GetRow(rateId).Weather)
            {
                var wid = (byte)wRef.RowId;
                if (wid != 0) return wid;   // first non-zero = the dominant/native weather
            }
        }
        catch { /* fall through */ }
        return 0;
    }

    /// <summary>
    /// NB-39: a CUTSCENE STAGE's authored default weather, read from the stage bg's own <c>.lvb</c> weather table —
    /// independent of the donor territory the swap-load borrows. Returns the first non-zero weather in the table (the
    /// authored/primary weather, incl. cinematic ones like CutScene that never appear in WeatherRate), or 0 if the
    /// stage has no readable .lvb / no weather (caller then falls back to the donor). Cached per bg path.
    /// </summary>
    public byte GetStageDefaultWeather(string? stageBg)
    {
        if (string.IsNullOrWhiteSpace(stageBg)) return 0;
        if (stageWeatherCache.TryGetValue(stageBg, out var cached)) return cached;
        byte result = 0;
        try
        {
            var lvb = dataManager.GetFile<LvbFile>("bg/" + stageBg + ".lvb");
            if (lvb?.WeatherIds != null)
            {
                foreach (var raw in lvb.WeatherIds)
                {
                    if (raw != 0 && raw <= byte.MaxValue) { result = (byte)raw; break; }   // first non-zero = authored primary
                }
            }
        }
        catch (Exception ex) { log.Warning("[HMSync] GetStageDefaultWeather(" + stageBg + ") failed: " + ex.Message); }
        stageWeatherCache[stageBg] = result;
        return result;
    }

    /// <summary>
    /// Play a BGM on the Territory scene (scene 11 - the zone-music layer), or stop it if 0. Uses CS's version-tracked
    /// BGMSystem.SetBGM / ResetBGM (no raw sig). Scene 11 is the zone BGM; overriding it holds our track over the map's
    /// default. 0 → ResetBGM (silence / let the scene fall back). Safe: null-gated on the BGMSystem instance.
    /// </summary>
    // S327r: play/stop BGM via the version-tracked BGMSystem.Instance()->Scenes[0] (the static-address approach in
    // S327q resolved to nothing - sig miss - and played silence, WORSE than this). CS Scene layout: BgmId@0x0C (target),
    // PlayingBgmId@0x0E, PreviousBgmId@0x10. Writing BgmId is what forces the track; the game propagates it. For STOP
    // (id 0) we also poke the flags byte @0x04 (Resume) as Orchestrion does. A diagnostic confirms the write lands.
    public bool PlayBgm(uint bgmId)
    {
        try
        {
            var bgm = FFXIVClientStructs.FFXIV.Client.Game.BGMSystem.Instance();
            if (bgm == null) { log.Warning("[HMSync] [BGM-PLAY] BGMSystem null"); return false; }
            if (bgm->Scenes.LongCount <= 0) { log.Warning("[HMSync] [BGM-PLAY] no scenes"); return false; }
            var s0 = bgm->Scenes.First;                 // priority 0
            s0->BgmId = (ushort)bgmId;                  // 0x0C - the target the game plays
            s0->PlayingBgmId = (ushort)bgmId;           // 0x0E
            s0->PreviousBgmId = (ushort)bgmId;          // 0x10
            if (bgmId == 0)
                *(uint*)((nint)s0 + 0x04) = 0x02;       // Flags = Resume - cancel playback
            return true;
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] PlayBgm(" + bgmId + ") failed: " + ex.Message);
            return false;
        }
    }

    // STOP = actual SILENCE, not "revert to default". Writing 0 makes the game re-resolve the zone's own track (that's
    // what Reset does). To truly silence, play the null track (BGM 1 = BGM_Null.scd) - an empty scd, so nothing sounds.
    private const uint SilenceTrackBgm = 1;   // BGM_Null.scd
    public bool StopBgm() => PlayBgm(SilenceTrackBgm);

    // S327s: release our forced BGM entirely so the GAME resumes its own natural music. Writing 0 to scene 0 makes the
    // game re-resolve the zone's default - which is exactly right on session-leave (the player is back in a real zone
    // and should hear that zone's music, not a stuck synthetic track). Also clears the peer-apply latch caller-side.
    public void RestoreBgm()
    {
        try
        {
            var bgm = FFXIVClientStructs.FFXIV.Client.Game.BGMSystem.Instance();
            if (bgm == null || bgm->Scenes.LongCount <= 0) return;
            var s0 = bgm->Scenes.First;
            s0->BgmId = 0;
            s0->PlayingBgmId = 0;
            s0->PreviousBgmId = 0;
        }
        catch (Exception ex) { log.Warning("[HMSync] RestoreBgm failed: " + ex.Message); }
    }

    /// <summary>
    /// Re-assert the currently-held state to the live client. Called AFTER a map load settles (the load can clobber
    /// weather/time/BGM) and on a mid-session change. Idempotent.
    /// </summary>
    // loadedZoneId MUST be the real synthetic-loaded zone (zoneLoad.CurrentLoadedZone), NOT GameMain's
    // CurrentTerritoryTypeId - under the packet filter GameMain still reports the apartment/previous zone, which caused
    // weather-legality and BGM-default to resolve against the WRONG map ("plays the last map's track").
    // NB-25: returns the concrete weather id it ENGAGED, so the caller broadcasts that exact value verbatim (see the
    // host reassert tick in HMSyncPlugin) instead of reading GetActiveWeather()'s lagging displayed byte - reading the
    // live byte right after a load is the map-load "weather not uniform on peers" settle-race. Apply behaviour below is
    // unchanged; only the resolved value is now also handed back (0 = None/atmospheric is a legitimate return).
    public byte Reassert(uint loadedZoneId)
    {
        // Held HOST STATE (weather/time/explicit BGM pick) is only re-asserted if the host actually set something this
        // session - that's what HasState gates. But it does NOT gate the BGM BASELINE below.
        if (HasState)
        {
            // Weather is per-scene: only re-assert a held weather if it's still LEGAL for the map we're now on.
            if (WeatherId != 0 && loadedZoneId != 0 && GetLegalWeather(loadedZoneId).Exists(x => x.id == WeatherId))
            {
                // b183: if a day/night sky-graft donor is held (late-join peer, or host synthetic re-load), re-engage the
                // graft rather than a flat native write — otherwise the reasserted sky stops marching the sun. Donor 0 or a
                // non-marching set falls straight back to ApplyWeather via SetWeatherOrGraft's own guard.
                if (WeatherDonor != 0) SetWeatherOrGraft(WeatherId, WeatherDonor);
                else ApplyWeather(WeatherId);
            }
            if (TimeForced) ApplyTime(EorzeaHour, EorzeaMinute);
        }

        // WEATHER ENGAGE (S328ac) - like the BGM baseline below, runs on EVERY load. The host's own weather write on a
        // synthetic load can otherwise land as a stale/generic sky (fair skies) rather than the map's real weather. If
        // no explicit host weather is held (or it's illegal here), assert the zone's NATIVE weather so the host shows
        // the true sky and broadcasts a concrete id. Explicit legal pick already applied above; this is the baseline.
        byte resolvedWeather = 0;   // NB-25: what we engaged and hand back for verbatim broadcast
        bool heldWeatherApplied = HasState && WeatherId != 0 && loadedZoneId != 0 && GetLegalWeather(loadedZoneId).Exists(x => x.id == WeatherId);
        if (heldWeatherApplied)
        {
            resolvedWeather = WeatherId;   // the held, still-legal pick
        }
        else if (loadedZoneId != 0 || ActiveStageBgForWeather != null)
        {
            // NB-39: a cutscene stage resolves its own AUTHORED weather from its .lvb first (the donor territory is a
            // borrowed real zone — an interior donor gives weather 0 = None, so every cutscene popped atmospheric). Fall
            // back to the donor's native weather only when the stage has no authored weather.
            byte stageWeather = ActiveStageBgForWeather != null ? GetStageDefaultWeather(ActiveStageBgForWeather) : (byte)0;
            byte nativeWeather = stageWeather != 0 ? stageWeather : (loadedZoneId != 0 ? GetDefaultWeather(loadedZoneId) : (byte)0);
            if (ActiveStageBgForWeather != null)
                log.Debug("[HMSync] [WX-STAGE] " + ActiveStageBgForWeather + ": authored=" + stageWeather   // cutscene-only load-time diag
                    + " donor(" + loadedZoneId + ")=" + (loadedZoneId != 0 ? GetDefaultWeather(loadedZoneId) : 0)
                    + " → engaging " + nativeWeather + " (" + WeatherName(nativeWeather) + ")");
            if (nativeWeather != 0) ApplyWeather(nativeWeather);
            resolvedWeather = nativeWeather;   // stage-authored for cutscenes, else the zone's deterministic native
        }

        // BGM ENGAGE - runs on EVERY load, HasState or not. Playing the zone's own default music is the BASELINE, not
        // "host state", so it must not be gated behind the host having configured something. (This was the bug: on a
        // fresh load with nothing set, HasState was false and the old early-return `if(!HasState) return;` skipped BGM
        // entirely → "plays none until you press Refresh". Refresh worked only because it routed through DoMapBgm, which
        // bypasses Reassert.) Explicit host pick if set, else the loaded zone's resolved default (incl.
        // CFC→InstanceContent for instanced zones like 1345 → BGM 20264).
        uint toEngage = BgmId != 0 ? BgmId : (loadedZoneId != 0 ? GetDefaultBgm(loadedZoneId) : 0);
        if (toEngage != 0) PlayBgm(toEngage);

        return resolvedWeather;   // NB-25: caller broadcasts this verbatim (not the lagging live displayed byte)
    }
}
