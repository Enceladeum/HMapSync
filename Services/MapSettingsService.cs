using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
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
    private readonly Dictionary<uint, List<(byte id, string name, bool legal)>> lvbWeatherCache = new();
    // NB-39: cutscene stage-bg → authored default weather, read straight from the stage's own .lvb (bypasses the donor
    // territory entirely). Cached by bg path. See GetStageDefaultWeather + the Reassert stage branch.
    private readonly Dictionary<string, byte> stageWeatherCache = new();
    // NB-39: the active cutscene stage's bg, set by DoLoad before the post-load reassert; null on a plain zone load.
    // When set, Reassert resolves the load-time NATIVE weather from the STAGE's authored .lvb, not the donor territory
    // (a cutscene borrows whatever real zone you launched from as its donor — an interior donor = weather 0 = None, the
    // "cutscenes all pop as None/atmospheric" bug). Donor is the graceful fallback when the stage .lvb has no weather.
    public string? ActiveStageBgForWeather { get; set; }

    public MapSettingsService(IDataManager dataManager, IPluginLog log, TimeFreezeService timeFreeze, ISigScanner sig)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.timeFreeze = timeFreeze;
        // (sig retained in the signature for future use; BGM playback uses the version-tracked BGMSystem instance.)
    }

    // ── Current host-set state (the authoritative values the host has chosen; broadcast to peers) ──
    // 0/unset sentinels: WeatherId 0 is a VALID choice (atmospheric), so "unset" is tracked separately.
    public bool HasState { get; private set; }         // has the host set anything this session?
    public byte WeatherId { get; set; }                 // 0 = default/atmospheric (valid); else a legal weather id
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
        [958]  = new byte[] { 59 },   // CutScene
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
            env->ActiveWeather = weatherId;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] ApplyWeather(" + weatherId + ") failed: " + ex.Message);
            return false;
        }
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

        var have = new HashSet<byte>();
        foreach (var (id, _, _) in baseList) have.Add(id);
        var result = new List<(byte, string, bool)>(baseList);
        foreach (var (id, name, legal) in GetAllWeatherFlagged(territoryId))
            if (have.Add(id)) result.Add((id, name, legal));
        return result;
    }

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
                ApplyWeather(WeatherId);
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
