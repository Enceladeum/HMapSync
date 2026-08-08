using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HMSync.Services;

// ============================================================================
// CutsceneStageService - free-roam catalog of cutscene-only bg locations (curated from V's research list).
// These have NO TerritoryType row, so LoadZone(territoryId) can't reach them.
//
// DIRECT-LOAD approach (supersedes the retired Penumbra file-redirect, which was fragile - a missed drop
// corrupted file resolution and needed a game repair). The game will load any bg into a walkable scene if a
// territory is pointed at it; a modder proved this by swapping a donor territory's bg (inn / 2055 private
// island) with a cutscene bg via a raw memory edit. We do the same in-engine. The bg-swap SEAM is not yet
// wired - LoadStage logs and returns false until it is (see the working thread's RE notes). No Penumbra.
// ============================================================================
public sealed class CutsceneStageService : IDisposable
{
    public sealed record Stage(string Name, string Bg, bool Experimental, string Quest = "", uint TerritoryId = 0, (float X, float Y, float Z)? Spawn = null, float? Facing = null)
    {
        public string Expansion => Bg.StartsWith("ex") && Bg.Length >= 3
            ? (Bg[..3] switch { "ex1" => "HW", "ex2" => "SB", "ex3" => "ShB", "ex4" => "EW", "ex5" => "DT", _ => "ARR" })
            : "ARR";
        public string Code => Bg.Split('/').Last();   // "n4e5" - searchable tag for the ID column
    }

    private static readonly Stage[] AllStages =
    {
        new("Knights of the round table", "ex1/01_roc_r2/evt/r2e2/level/r2e2", false, "Alphinaud's Way", Spawn: (0.3802f, 0.05f, 16.0202f), Facing: -3.13f),
        new("St Endalim's Scholasticate office", "ex1/01_roc_r2/evt/r2e7/level/r2e7", false, "Balancing the Spear", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Heavensward finale", "ex1/01_roc_r2/evt/r2e8/level/r2e8", false, "A Requiem for Heroes", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("The Borel Manor", "ex1/01_roc_r2/evt/r2e9/level/r2e9", false, "Promises Kept", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Hraesvelgr and Nidhogg's forum", "ex1/02_dra_d2/evt/d2e1/level/d2e1", false, "The Song Begins", Spawn: (0.1f, 1.565f, 6f)),
        new("Alexander control room", "ex1/02_dra_d2/evt/d2e6/level/d2e6", false, "A Gob in the Machine", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Toxic skybox", "ex1/03_abr_a2/evt/a2e1/level/a2e1", true, "Bolt, Chain, and Island"),
        new("Proto Ultima trial", "ex1/03_abr_a2/fld/a2ff/level/a2ff", false, "", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Elidibus on the moon", "ex1/05_zon_z2/evt/z2e1/level/z2e1", false, "Heavensward", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Steps of Faith", "ex1/05_zon_z2/evt/z2e2/level/z2e2", false, "Coming to Ishgard", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Garlean Ala Mhigo throne room", "ex1/05_zon_z2/evt/z2e3/level/z2e3", false, "A Defector's Tidings", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Fordola artillery", "ex2/01_gyr_g3/evt/g3e2/level/g3e2", false, "Hells Open", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Temple of the Fist", "ex2/01_gyr_g3/evt/g3e5/level/g3e5", false, "The Lady in Red", Spawn: (90f, 70.368f, 260f)),
        new("Ala Mhigan airship landing", "ex2/01_gyr_g3/evt/g3e8/level/g3e8", false, "Futures Rewritten", Spawn: (-363.046f, 383f, -90.49f)),
        new("Yotsuyu and Gosetsu", "ex2/02_est_e3/evt/e3e2/level/e3e2", false, "Forever and Ever Apart", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Yotsuyu jail", "ex2/02_est_e3/evt/e3e3/level/e3e3", false, "A Final Peace", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Doma castle finale", "ex2/02_est_e3/evt/e3e4/level/e3e4", false, "All the Little Angels", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Gosetsu's island", "ex2/02_est_e3/evt/e3e6/level/e3e6", false, "Stormblood", Spawn: (0f, 0.875f, 5.9f)),
        new("The Doman Enclave", "ex2/02_est_e3/evt/e3ef/level/e3ef", false, "Fruits of Her Labor", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Valens van Varro's chambers", "ex2/05_zon_z3/evt/z3e4/level/z3e4", false, "Duty in the Sky with Diamond", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1332f),
        new("Ridorana Cataract", "ex2/05_zon_z3/evt/z3e6/level/z3e6", false, "Desire", Spawn: (-2.5828f, 16.837f, -12.5837f), Facing: -2.8452f),
        new("Black Rose dead camp", "ex2/05_zon_z3/evt/z3e7/level/z3e7", false, "Prelude in Violet", Spawn: (-27f, 12.978f, 202f)),
        new("Prima Vista", "ex2/05_zon_z3/evt/z3ea/level/z3ea", false, "The City of Lost Angels", Spawn: (0.1282f, -1.5f, 5.9394f), Facing: -3.1284f),
        new("Ridorana burning", "ex2/05_zon_z3/rad/z3r4/level/z3r4", false, "A City Fallen", Spawn: (-321.1753f, 2.9124f, 251.8999f), Facing: 3.1206f),
        new("Vauthry's chambers", "ex3/01_nvt_n4/evt/n4e4/level/n4e4", false, "A Feast of Lies", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1286f),
        new("Garlemald, pre-war", "ex3/01_nvt_n4/evt/n4e5/level/n4e5", false, "Out of the Wood", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1287f),
        new("Amh Araeng flashback", "ex3/01_nvt_n4/evt/n4e6/level/n4e6", false, "Crossing Paths", Spawn: (0f, 0.2f, -6f)),
        new("Kholusia", "ex3/01_nvt_n4/evt/n4e7/level/n4e7", false, "Extinguishing the Last Light", Spawn: (-139f, 2.358f, 460f)),
        new("Werlyt countryside", "ex3/01_nvt_n4/evt/n4ec/level/n4ec", false, "Ruby Doomsday", Spawn: (106f, 0f, 99f)),
        new("Garlemald, ruined", "ex3/01_nvt_n4/evt/n4ed/level/n4ed", false, "Death Unto Dawn", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1288f),
        new("The Flames of War", "ex3/01_nvt_n4/evt/n4ee/level/n4ee", true, ""),
        new("The Burn", "ex3/01_nvt_n4/goe/n4gx/level/n4gx", false, "Empty Promise", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1289f),
        new("Cloudscape", "ex4/01_nvt_n5/evt/n5e2/level/n5e2", true, ""),
        new("The Tower of Zot", "ex4/02_mid_m5/evt/m5e2/level/m5e2", false, "", Spawn: (84f, 20.995f, 43f)),
        new("Fair skies skybox", "ex4/03_kld_k5/evt/k5e3/level/k5e3", true, ""),
        new("The Edge of Creation", "ex4/04_uvs_u5/evt/u5e2/level/u5e2", false, "", Spawn: (113.5174f, 0.0f, 98.6273f), Facing: -1.5f),
        new("The 13th", "ex4/05_zon_z5/evt/z5e2/level/z5e2", false, "", Spawn: (0.12f, 0.0f, 5.939f)),
        new("Golbez throne", "ex4/05_zon_z5/evt/z5e3/level/z5e3", false, "", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.14f),
        new("Prima Vista first map", "ffxiv/air_a1/evt/a1e1/level/a1e1", false, "A City Fallen"),
        new("Prima Vista starry sky", "ffxiv/air_a1/evt/a1e2/level/a1e2", false, "Something Fishy This Way Comes"),
        new("Alpha Ruby Sea", "ffxiv/est_e1/evt/e1e1/level/e1e1", false, "", Spawn: (-683.2109f, 45.2518f, -567.413f), Facing: 0.9337f),
        new("The Borderland Ruins", "ffxiv/lak_l1/evt/l1e4/level/l1e4", false, "An Ending to Mark a New Beginning", Spawn: (-58.7841f, 39.9581f, -36.0119f), Facing: -0.1826f),
        new("Ship cabin", "ffxiv/ocn_o1/evt/o1e1/level/o1e1", false, "We Who Are About to Set Sail Salute You", Spawn: (-2.8493f, 9.1549f, -32.1657f), Facing: -0.5484f),
        new("Ishgard throne room", "ffxiv/roc_r1/evt/r1e1/level/r1e1", false, "Brave New Companions", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: 3.1262f),
        new("Merlwyb's office", "ffxiv/sea_s1/evt/s1e5/level/s1e5", false, "A Mizzenmast Repast", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1296f),
        new("The Maelstrom Hall", "ffxiv/sea_s1/evt/s1e6/level/s1e6", false, "A Hero in the Making", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1297f),
        new("Ship lower deck", "ffxiv/sea_s1/evt/s1e7/level/s1e7", false, "", Spawn: (16.6457f, -3.1478f, -1.602f), Facing: -1.561f),
        new("Ul'dah meeting chamber", "ffxiv/wil_w1/evt/w1e5/level/w1e5", false, "A Dainty Dilemma", Spawn: (0.246f, 0.01f, 18.3065f), Facing: -3.1318f),
        new("Ganelon's office", "ffxiv/wil_w1/evt/w1e8/level/w1e8", false, "Eight-armed and Dangerous", Spawn: (0f, 1f, -9.5f)),
        new("Ul'dah tunnel", "ffxiv/wil_w1/evt/w1e9/level/w1e9", false, "The Parting Glass", Spawn: (8.6721f, 0.6649f, -0.8728f), Facing: -1.6616f),
        new("Empty Ul'dah inn", "ffxiv/wil_w1/evt/w1ea/level/w1ea", false, "Friends Forever", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1301f),
        new("Correction Chamber", "ffxiv/wil_w1/evt/w1ec/level/w1ec", false, "Blood of Emerald / Before the Dawn", Spawn: (-3.5f, 0f, 3.4f)),
        new("Aetherial sea", "ffxiv/zon_z1/evt/z1e3/level/z1e3", false, "", Spawn: (0.151f, -123.305f, 0.061f)),   // NB-4: login-screen bg; tiny collision box where you're meant to stand
        new("Zodiark intro", "ffxiv/zon_z1/evt/z1e4/level/z1e4", false, "The Ultimate Weapon"),
        new("Old Mordion Gaol", "ffxiv/zon_z1/evt/z1e6/level/z1e6", false, "Alisaie's Path", Spawn: (0.4626f, -0.1172f, -2.6083f), Facing: 0.0873f),
        new("Ascian meeting", "ffxiv/zon_z1/evt/z1e7/level/z1e7", false, "Shadows of the Past", Spawn: (-0.056f, 0.0085f, -0.1209f), Facing: -3.136f),
        new("Imperial throne room", "ffxiv/zon_z1/evt/z1e8/level/z1e8", false, "A Breath of Respite", Spawn: (0.1282f, 0.0f, 5.9394f), Facing: -3.1305f),
        // --- class-2 (evt, no TT) stages the old relic-filter wrongly dropped; TT-less so they load via the swap ---
        new("Black screen", "ffxiv/zon_z1/evt/z1e1/level/z1e1", true, ""),
        new("White screen", "ffxiv/zon_z1/evt/z1e2/level/z1e2", true, ""),
        new("Black screen", "ffxiv/zon_z1/evt/z1eb/level/z1eb", true, ""),
        new("Baelsar's Wall", "ffxiv/fst_f1/evt/f1e8/level/f1e8", false, "", Spawn: (577.5458f, 66.5f, 1057.9313f), Facing: -2.3f),
        // --- 7.x cutscene stages surfaced by the automated bg sweep ---
        new("Cosmic exploration", "ffxiv/cos_c1/evt/c1e1/level/c1e1", false, "", Spawn: (0f, 0f, 0f)),   // origin-locked (all axes at 0)
        new("La Noscea PvP", "ffxiv/sea_s1/pvp/s1p4/level/s1p4", false, ""),
        new("Seaship", "ex2/03_ocn_o3/evt/o3e2/level/o3e2", false, "The Next Ship to Sail", Spawn: (0.207f, 11.853f, 2.000f)),   // also in the Seaships chip (SeashipCutsceneBgs)
        // --- prize picks from the 2026-07-15 gap-hunt (TT-backed dressings worth surfacing in the cutscene list) ---
        new("Terncliff Bay", "ex3/01_nvt_n4/evt/n4eb/level/n4eb", false, "Forever at Your Side", 926),
        new("Cinder Drift (Ruby Weapon)", "ex3/01_nvt_n4/fld/n4fe/level/n4fe", false, "Ruby Doomsday", 897),
        new("Eorzean Alliance Headquarters", "ex2/01_gyr_g3/evt/g3e7/level/g3e7", false, "A Requiem for Heroes", 829),
        new("The Prima Vista Tiring Room", "ex2/05_zon_z3/evt/z3e2/level/z3e2", false, "A City Fallen", 828),
        new("The Prima Vista Bridge", "ex2/05_zon_z3/evt/z3e3/level/z3e3", false, "Dramatis Personae", 736),
        new("The Ocular", "ex3/01_nvt_n4/evt/n4e1/level/n4e1", false, "A Party Soon Divided", 844),
        new("The Seat of Sacrifice", "ex3/01_nvt_n4/fld/n4ff/level/n4ff", false, "Hope's Confluence", 931),
        // --- Tier B addendum (Valens-tracer sweep 2026-07-15): wrapper TTs the evt-only filter dropped. Plain LoadZone. ---
        new("The Imperial Palace", "ex2/05_zon_z3/btl/z3b1/level/z3b1", false, "", 893),
        new("Castrum Marinum Drydocks", "ex3/01_nvt_n4/fld/n4fg/level/n4fg", false, "", 967),
        new("G-Savior Deck", "ex3/01_nvt_n4/fld/n4fh/level/n4fh", false, "", 991),
        new("The Confessional of Toupasa the Elder", "ex3/01_nvt_n4/btl/n4b1/level/n4b1", false, "", 859),
        new("Cid's Memory", "ex3/01_nvt_n4/btl/n4b2/level/n4b2", false, "", 911),
        new("Trial's Threshold", "ex3/01_nvt_n4/btl/n4b3/level/n4b3", false, "", 914),
        new("The Last Trace", "ex3/01_nvt_n4/btl/n4b5/level/n4b5", false, "", 955),
        new("Dreamlike Palace", "ex3/01_nvt_n4/dun/n4d9/level/n4d9", false, "", 1234),
        new("Royal Palace", "ex2/01_gyr_g3/dun/g3d2/level/g3d2", false, "", 737),
        new("The Nabaath Mines", "ex3/01_nvt_n4/fld/n4f3/level/n4f3", false, "", 876),
        new("Steps of Faith", "ffxiv/roc_r1/fld/r1fd/level/r1fd", false, "", 1068),
        new("The Weeping Saint", "ffxiv/roc_r1/fld/r1f1/level/r1f1", false, "", 368),
        new("Limsa Lominsa", "ffxiv/sea_s1/twn/s1t1/level/s1t1", false, "", 181),
        new("Eorzean Subterrane", "ffxiv/sea_s1/bah/s1b7/level/s1b7", false, "", 338),
        new("The Ridorana Cataract", "ex2/05_zon_z3/rad/z3r2/level/z3r2", false, "", 787),
    };

    private readonly IPluginLog log;
    private readonly IDataManager data;
    private readonly ZoneLoadService zoneLoad;

    private List<Stage>? _exposed;
    public IReadOnlyList<Stage> Stages => _exposed ??= AllStages.Where(x => x.TerritoryId == 0).ToList();   // Tier B (real TTs) live in the zones tab

    public CutsceneStageService(IDalamudPluginInterface pi, IPluginLog log, IDataManager data, ZoneLoadService zoneLoad)
    {
        this.log = log; this.data = data; this.zoneLoad = zoneLoad;
    }

    // Grouping key from the bg area code (these are all TT-less, so no PlaceNameRegion to read).
    public string RegionFor(Stage s)
    {
        var seg = s.Bg.Split('/');
        var code = seg.Length > 1 ? seg[1] : "";
        var area = code.Length >= 3 ? code[..3] : code;
        return area switch
        {
            "ocn" or "sea" => "La Noscea",
            "wil" => "Thanalan",
            "fst" => "The Black Shroud",
            "roc" => "Coerthas / Ishgard",
            "dra" => "Dravania",
            "abr" => "Abalathia's Spine",
            "gyr" => "Gyr Abania",
            "est" => "Othard",
            "nvt" => "Norvrandt",
            "kld" => "Elpis",
            "uvs" => "Ultima Thule",
            "mid" => "Garlemald",
            "xkt" or "ykt" => "Tural",
            "cos" => "Cosmic Exploration",   // NB-38: c1e1 lives under ffxiv/cos_c1
            "lak" => "Mor Dhona",
            "air" => "Prima Vista",
            "zon" => s.Expansion,
            _ => s.Expansion,
        };
    }

    // These are bg-path-only stages - they need the direct bg-swap load, NOT a territory load. doLoad is the
    // plugin's DoLoad (kept in the signature for when the swap is wired: donor load under the filter + swapped bg).
    public bool LoadStage(Stage s, uint donor, Action<uint> doLoad)
    {
        if (s.TerritoryId != 0)                      // Tier B - real TerritoryType row, plain load, no swap
        {
            zoneLoad.ActiveStageBg = null;           // a real TT load - spawn keys by territoryId, not stage
            log.Information("[CSS] load '" + s.Name + "' (TT " + s.TerritoryId + ")");
            doLoad(s.TerritoryId);
            return true;
        }
        log.Information("[CSS] direct-load '" + s.Name + "' via donor territory " + donor + " (bg " + s.Bg + ")");
        zoneLoad.ActiveStageBg = s.Bg;               // swap stage: spawn + user-capture key by THIS bg, not the shared donor id
        zoneLoad.PendingStageBg = s.Bg;              // CreateScene detour swaps this into the scene path during the load
        doLoad(donor);                               // reload the origin; its CreateScene call gets the stage bg instead
        return true;
    }

    // Curated per-stage spawn (baked calibration; keyed by BG path because swap stages share donor territory ids so a
    // territoryId key would leak one stage's spawn to every co-donor stage). Returns the Stage.Spawn for the bg, if set,
    // plus the curated facing (yaw) if one was baked (null → caller keeps default orientation).
    public bool TryGetCuratedStageSpawn(string bg, out Vector3 spawn, out float? facing)
    {
        foreach (var st in AllStages)
        {
            if (st.Bg == bg && st.Spawn.HasValue)
            {
                var v = st.Spawn.Value;
                spawn = new Vector3(v.X, v.Y, v.Z);
                facing = st.Facing;
                return true;
            }
        }
        spawn = default;
        facing = null;
        return false;
    }

    // v0.7.340: resolve a swap-stage's display NAME from its bg path (for the "Loading <name>" print + the map-control
    // Zone: field, which otherwise showed the donor territory's name). Returns null if the bg isn't a known stage.
    public string? GetStageName(string bg)
    {
        if (string.IsNullOrEmpty(bg)) return null;
        foreach (var st in AllStages)
            if (st.Bg == bg) return st.Name;
        return null;
    }

    /// <summary>
    /// v0.7.362: resolve free-text to a cutscene stage for "/hms load &lt;text&gt;". Matches either the stage TAG
    /// (Code - the bg's last segment, e.g. "o1e1") or the display NAME ("ship cabin"), so both spellings work.
    ///
    /// Ranking, best first:
    ///   0. exact tag        ("o1e1")
    ///   1. exact name       ("ship cabin")
    ///   2. name prefix      ("ship ca")
    ///   3. name substring   ("cabin")
    ///   4. tag prefix       ("o1e")
    /// Ties break toward the non-experimental stage, then the shorter name, then list order - so a curated stage
    /// wins over an experimental one with a similar name. Returns the index into <see cref="Stages"/> (what
    /// OnLoadCutscene takes), or -1 when nothing matches.
    /// </summary>
    public int ResolveStage(string query, out string resolvedName, out string resolvedTag, out int otherMatches)
    {
        resolvedName = ""; resolvedTag = ""; otherMatches = 0;
        if (string.IsNullOrWhiteSpace(query)) return -1;
        var q = query.Trim().ToLowerInvariant();

        int bestIdx = -1, bestTier = int.MaxValue; bool bestExp = true; int bestLen = int.MaxValue;
        int matches = 0;
        var list = Stages;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var tag = s.Code.ToLowerInvariant();
            var name = s.Name.ToLowerInvariant();

            int tier;
            if (tag == q) tier = 0;
            else if (name == q) tier = 1;
            else if (name.StartsWith(q, StringComparison.Ordinal)) tier = 2;
            else if (name.Contains(q, StringComparison.Ordinal)) tier = 3;
            else if (tag.StartsWith(q, StringComparison.Ordinal)) tier = 4;
            else continue;

            matches++;
            bool better = tier < bestTier
                || (tier == bestTier && bestExp && !s.Experimental)
                || (tier == bestTier && bestExp == s.Experimental && s.Name.Length < bestLen);
            if (bestIdx < 0 || better)
            {
                bestIdx = i; bestTier = tier; bestExp = s.Experimental; bestLen = s.Name.Length;
                resolvedName = s.Name; resolvedTag = s.Code;
            }
        }
        otherMatches = matches > 0 ? matches - 1 : 0;
        return bestIdx;
    }

    public void Dispose() { }
}
