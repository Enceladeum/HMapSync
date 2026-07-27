using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace HMSync;

public class HMSyncConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string RelayUrl { get; set; } = "ws://localhost:9420";

    // S328am: Mare-style relay-service picker. A list of named services (presets + user-added); the selected one's
    // URL is what Connect uses (mirrored into RelayUrl for back-compat with existing connect calls). Modeled on the
    // Mare/Snowcloak/Lightless service menu: pick from a dropdown, or add your own URL+name.
    public List<RelayService> RelayServices { get; set; } = new();
    public int SelectedRelayService { get; set; } = 0;

    // First-run seeding: if the list is empty, populate the two built-ins (local dev + the Enceladeum tunnel).
    // Called from the plugin ctor after config load. Idempotent.
    public void EnsureRelayServicesSeeded()
    {
        if (RelayServices.Count > 0) return;
        RelayServices.Add(new RelayService { Name = "Local (localhost)", Url = "ws://localhost:9420", BuiltIn = true });
        RelayServices.Add(new RelayService { Name = "Enceladeum", Url = "wss://relay.enceladeum.com/", BuiltIn = true });
        // v0.7.338: default to Enceladeum (index 1), NOT localhost. Localhost is a dev-only endpoint reachable via the
        // debug picker; a normal user seeded onto it saw "relay offline" with no way to switch and no idea why (the
        // friend's bug). The Enceladeum entry still needs the user's key pasted before it connects, but it's the right
        // default target.
        SelectedRelayService = 1;
        SyncSelectedRelayUrl();
    }

    // v0.7.338: keep normal (non-debug) users OFF the localhost dev endpoint. The localhost service is only meaningful
    // with a local relay running (dev only) and is only switchable via the debug-mode picker — so a non-debug user
    // sitting on it (fresh seed pre-0.7.338, or a stale config) is stuck showing "offline" with no recourse. If we're
    // not in debug mode and the selected service is the localhost built-in, move them to Enceladeum. Called from the
    // plugin ctor once debug-mode is known. No-op in debug mode (devs keep their localhost selection).
    public void EnforceRelayServiceForMode(bool debugMode)
    {
        if (debugMode) return;
        if (SelectedRelayService < 0 || SelectedRelayService >= RelayServices.Count) { SelectedRelayService = 0; }
        var sel = RelayServices.Count > 0 ? RelayServices[SelectedRelayService] : null;
        bool onLocalhost = sel != null && (sel.Url ?? "").Contains("localhost");
        if (!onLocalhost) return;
        // Find the Enceladeum built-in and select it.
        for (int i = 0; i < RelayServices.Count; i++)
            if (RelayServices[i].BuiltIn && RelayServices[i].Name.StartsWith("Enceladeum"))
            {
                SelectedRelayService = i;
                SyncSelectedRelayUrl();
                Save();
                return;
            }
    }

    // v0.7.248: migrate pre-key-field configs. Before the Key field existed, users pasted the FULL keyed URL
    // (wss://host/?k=KEY) into svc.Url. That baked-in key rode every connection even after the new Key field was
    // emptied (SyncSelectedRelayUrl saw ?k= already present and used the URL verbatim — the "master key won't clear"
    // bug). Split any ?k=/&k= token out of every service URL into svc.Key, leaving a clean base URL, so the Key field
    // becomes authoritative and emptying it actually clears the key. Also re-canonicalises the Enceladeum built-in base.
    public void MigrateRelayKeys()
    {
        foreach (var svc in RelayServices)
        {
            var u = svc.Url ?? "";
            int ki = u.IndexOf("?k=", System.StringComparison.Ordinal);
            if (ki < 0) ki = u.IndexOf("&k=", System.StringComparison.Ordinal);
            if (ki >= 0)
            {
                var key = u.Substring(ki + 3);
                // stop at any further &param
                int amp = key.IndexOf('&');
                if (amp >= 0) key = key.Substring(0, amp);
                if (string.IsNullOrEmpty(svc.Key)) svc.Key = key;   // don't clobber an explicit Key
                svc.Url = u.Substring(0, ki);                        // clean base
            }
        }
        // Canonicalise the Enceladeum built-in base so a stale keyed URL can never ride.
        foreach (var svc in RelayServices)
            if (svc.BuiltIn && svc.Name.StartsWith("Enceladeum"))
            {
                svc.Url = "wss://relay.enceladeum.com/";
                svc.Name = "Enceladeum";   // v0.7.317: drop the old "(Cloudflare tunnel)" suffix for existing users
            }
        SyncSelectedRelayUrl();
    }

    // Compose the selected service's connect URL into RelayUrl so existing Connect(config.RelayUrl, ...) calls pick it
    // up. v0.7.247: if the service has a Key, append it as ?k=/&k= (users enter just the key; base URL is fixed). A
    // Url that already carries ?k= (legacy or a custom full URL) is used verbatim.
    public void SyncSelectedRelayUrl()
    {
        if (SelectedRelayService >= 0 && SelectedRelayService < RelayServices.Count)
        {
            var svc = RelayServices[SelectedRelayService];
            var baseUrl = svc.Url ?? "";
            if (!string.IsNullOrEmpty(svc.Key) && !baseUrl.Contains("?k=") && !baseUrl.Contains("&k="))
            {
                var sep = baseUrl.Contains("?") ? "&" : "?";
                RelayUrl = baseUrl + sep + "k=" + svc.Key;
            }
            else
            {
                RelayUrl = baseUrl;
            }
        }
    }
    // v0.7.466 (D-12): `TickRateHz` REMOVED — it was declared here and read by NOTHING. The real cadence is
    // `const double TickInterval = 0.1` in StateCaptureService (10 Hz, compile-time).
    //
    // ⚠ DO NOT RE-ADD THIS AS A USER SETTING WITHOUT TALKING TO THE RELAY. The relay's ingress rate brake
    // (RMS 1.0.0 / F3) is sized against that fixed 10 Hz: a non-host peer's structural ceiling is 2 lanes ×
    // 10 Hz = 20 msg/s, which sits BELOW the relay's 25 msg/s refill — which is why normal play cannot trip the
    // brake at all. Making the tick rate user-tunable breaks that arithmetic silently: a peer at 30 Hz would
    // trip the throttle constantly through no fault of their own, and the symptom (an amber "relay throttling"
    // banner during ordinary play) would look like a relay bug. If this ever becomes a setting, the relay's
    // token-bucket budgets must be re-tuned FIRST.

    // S319/S320c: carpet tunables persisted across sessions. Defaults match CarpetService's preset.
    public bool CarpetShowRings { get; set; } = true;
    public float CarpetRadius { get; set; } = 1.3f;
    public float CarpetStep { get; set; } = 2.2f;
    public int CarpetTrail { get; set; } = 5;
    public float CarpetLeadBase { get; set; } = 1.0f;
    public float CarpetLeadPerSpeed { get; set; } = 0.25f;
    public float CarpetPitch { get; set; } = -0.05f;       // walking slope (was CarpetYOffset/flat-lock)
    public float CarpetDropOffset { get; set; } = -0.05f;  // first-patch offset (cinematic drop-in)

    // S326: map-state backbone — the host's last-chosen map settings, persisted so a scene can be re-dialled
    // quickly across sessions. MapSettingsTerritory is the territory selected in the Map Settings tab dropdown.
    public uint MapSettingsTerritory { get; set; }         // selected territory in the Map Settings tab (0 = none)
    public byte MapWeatherId { get; set; }                 // 0 = default/atmospheric (valid)
    public bool MapTimeForced { get; set; }
    public ushort MapEorzeaHour { get; set; } = 12;
    public byte MapEorzeaMinute { get; set; }
    public uint MapBgmId { get; set; }                     // 0 = none
    public bool MapRemoveNpcs { get; set; }
    public bool MapHideQuestSigns { get; set; }   // S328aa: hide over-head quest markers (keep NPC bodies)
    // (S328ag DirtyCheckEnabled removed at release hardening — change-detection is always on, no longer configurable.)

    // S328d: when true, /say from players NOT in the HMS session is hidden from chat while a session is active
    // (a privacy-safe display filter — hides strangers' /say during RP sessions; no chat is collected or relayed).

    // ── Say-passthrough opcode management (S328p) ──────────────────────────────────────────────────────────────────
    // The ONLY two hardcoded opcodes in the filter, now config-driven so they survive a patch without a rebuild.
    // 300 = ChatHandler (outbound /say/yell/shout submission); 912 = inbound spatial-chat delivery. A structural
    // validator (chat-shape check) guards the inbound pass so a rotated opcode fails CLOSED. ShowDebugCommands gates
    // the debug UI. GameVersionStamp records the game version these were last confirmed on — a version change marks
    // them unverified and shuts the passthrough until re-learned. SayOpcodesVerified is the live gate.
    public uint SayOutboundOpcode { get; set; } = 300;
    public uint SayInboundOpcode { get; set; } = 912;
    // v0.7.462 (P2, Codex QA + V): DEFAULT UNVERIFIED (fail-closed). A fresh install must NOT auto-arm the
    // hardcoded 300/912 — a game patch could have repurposed the send-opcode since these were captured, and
    // passing the wrong outbound packet is a security exposure. The seed values above are a hint for the
    // learner, not a trusted default. Passthrough stays OFF until the user runs Re-learn (which captures the
    // live opcodes AND stamps the current game version). README documents "initiate on first use".
    public bool SayOpcodesVerified { get; set; } = false;
    public string SayOpcodesGameVersion { get; set; } = "";
    public bool ShowDebugCommands { get; set; }

    // Action-button / active-toggle accent, user-set in the Config tab. Gold by default. Neutral and Danger are fixed
    // in the UI; hover and text-on-accent are derived so any accent stays legible.
    public float[] AccentColor { get; set; } = { 0.83f, 0.62f, 0.20f, 1f };

    // Say proximity is fixed game behavior, not a preference — no config. The values match the game (see the
    // SayFilterService constants). "Hide non-session say" is likewise always-on: in a session you're isolated, so any
    // /say from outside the session is hidden (in-session members are heard via the passthrough + proximity cull).

    // S326m: per-territory USER spawn override. When set for a territory, LoadZone uses it instead of the curated/LGB
    // spawn. Keyed by territory id; value is [X, Y, Z, Facing]. Absent = fall back to curated resolution. Stored as
    // float[] for clean JSON round-trip (Vector3 doesn't serialize cleanly through the config).
    public Dictionary<uint, float[]> UserSpawns { get; set; } = new();

    // v0.7.227: user-captured spawns for SWAP cutscene stages, keyed by bg path (not territoryId). Swap stages share a
    // donor territory id, so keying by territoryId leaked one stage's spawn to every co-donor stage. bg is unique per
    // stage. Value is [X, Y, Z, Facing] like UserSpawns. Curated (baked) stage spawns live in CutsceneStageService;
    // this dict is only the user's own "Set spawn" captures on a swap stage.
    public Dictionary<string, float[]> UserStageSpawns { get; set; } = new();

    // S326m: adjustable heights (px) for the Session-dashboard Maps + Participants tables (drag-handle persisted).
    public float DashMapsHeight { get; set; } = 220f;
    public float DashParticipantsHeight { get; set; } = 180f;

    // S322: Emotes tab — favourites (user-starred) + recently-played (rolling, most-recent-first). Both
    // persisted so the top split survives restarts. This is the template for the future mount list.
    public List<uint> FavouriteEmotes { get; set; } = new();
    public List<uint> RecentEmotes { get; set; } = new();

    // S326q: the display list for a section's "Recent" — STARRED (pinned) items first (they survive the FIFO
    // overwrite), then the most-recent non-starred, capped at 6 total. Starring pins; un-starring lets it age out
    // of Recent normally. This is what the Character-tab quadrants show under "Recent".
    public const int PinnedRecentCap = 6;
    public static List<uint> BuildPinnedRecent(List<uint> favourites, List<uint> recent)
    {
        var outList = new List<uint>(PinnedRecentCap);
        foreach (var f in favourites)          // pinned first, in star order
        {
            if (outList.Count >= PinnedRecentCap) break;
            if (!outList.Contains(f)) outList.Add(f);
        }
        foreach (var r in recent)              // then recent, skipping already-pinned
        {
            if (outList.Count >= PinnedRecentCap) break;
            if (!outList.Contains(r)) outList.Add(r);
        }
        return outList;
    }

    public void ToggleFavouriteEmote(uint id)
    {
        if (!FavouriteEmotes.Remove(id)) FavouriteEmotes.Add(id);
        Save();
    }

    public void PushRecentEmote(uint id)
    {
        RecentEmotes.Remove(id);          // move-to-front (dedupe)
        RecentEmotes.Insert(0, id);
        const int max = 6;                // 3×2 rows — exactly fills the History grid's default height
        if (RecentEmotes.Count > max)
            RecentEmotes.RemoveRange(max, RecentEmotes.Count - max);
        Save();
    }

    public List<uint> RecentZones { get; set; } = new();
    // S332: Zones-tab favourites — territory ids the user has pinned (★). Persisted across sessions.
    public List<uint> FavouriteZones { get; set; } = new();

    // v0.7.231: unified recent list. From the user's chair a cutscene stage is just a place they visited, so Recent
    // must list it like any zone. Zones have a unique territory id; swap cutscene stages share a donor id and are
    // identified by their bg path instead. RecentPlace carries both — StageBg==null means a plain territory, else a
    // swap stage reloaded via CutsceneStageService. RecentZones (the old uint list) is kept only so existing configs
    // migrate forward (see MigrateRecentZones, called once on load).
    public class RecentPlace
    {
        public uint TerritoryId { get; set; }        // the zone id (plain zone) OR the donor id (swap stage — informational)
        public string? StageBg { get; set; }         // set => swap cutscene stage, reloaded by bg; null => plain zone
    }
    public List<RecentPlace> RecentPlaces { get; set; } = new();

    public void PushRecentPlace(uint territoryId, string? stageBg)
    {
        // Dedupe on identity: bg for a stage, territory id for a zone.
        RecentPlaces.RemoveAll(r => stageBg != null ? r.StageBg == stageBg : (r.StageBg == null && r.TerritoryId == territoryId));
        RecentPlaces.Insert(0, new RecentPlace { TerritoryId = territoryId, StageBg = stageBg });
        const int max = 5;
        if (RecentPlaces.Count > max)
            RecentPlaces.RemoveRange(max, RecentPlaces.Count - max);
        Save();
    }

    // One-time forward-migration of the legacy RecentZones (uint) list into RecentPlaces. Idempotent: only runs while
    // RecentPlaces is empty and RecentZones has entries. Cutscene stages were never in the old list (they recorded the
    // donor id, a known wrong behaviour), so nothing is lost.
    public void MigrateRecentZones()
    {
        if (RecentPlaces.Count > 0 || RecentZones.Count == 0) return;
        foreach (var id in RecentZones)
            RecentPlaces.Add(new RecentPlace { TerritoryId = id, StageBg = null });
    }

    public void PushRecentZone(uint id)
    {
        RecentZones.Remove(id);           // move-to-front (dedupe)
        RecentZones.Insert(0, id);
        const int max = 5;                // Recent-5 quick-load
        if (RecentZones.Count > max)
            RecentZones.RemoveRange(max, RecentZones.Count - max);
        Save();
    }

    // S322: Minions tab — favourites (user-starred) + recently-summoned (rolling). Mirror of the emote lists.
    public List<uint> FavouriteMinions { get; set; } = new();
    public List<uint> RecentMinions { get; set; } = new();

    public void ToggleFavouriteMinion(uint id)
    {
        if (!FavouriteMinions.Remove(id)) FavouriteMinions.Add(id);
        Save();
    }

    public void PushRecentMinion(uint id)
    {
        RecentMinions.Remove(id);
        RecentMinions.Insert(0, id);
        const int max = 6;                // 3×2 rows — exactly fills the History grid's default height
        if (RecentMinions.Count > max)
            RecentMinions.RemoveRange(max, RecentMinions.Count - max);
        Save();
    }

    // S322k: Accessories tab — favourites + recently-equipped, same shape as the minion/emote lists.
    public List<uint> FavouriteOrnaments { get; set; } = new();
    public List<uint> RecentOrnaments { get; set; } = new();

    public void ToggleFavouriteOrnament(uint id)
    {
        if (!FavouriteOrnaments.Remove(id)) FavouriteOrnaments.Add(id);
        Save();
    }

    public void PushRecentOrnament(uint id)
    {
        RecentOrnaments.Remove(id);
        RecentOrnaments.Insert(0, id);
        const int max = 6;
        if (RecentOrnaments.Count > max)
            RecentOrnaments.RemoveRange(max, RecentOrnaments.Count - max);
        Save();
    }

    // S323c: Mounts tab — favourites + recently-summoned, same shape as the other lists.
    public List<uint> FavouriteMounts { get; set; } = new();
    public List<uint> RecentMounts { get; set; } = new();

    public void ToggleFavouriteMount(uint id)
    {
        if (!FavouriteMounts.Remove(id)) FavouriteMounts.Add(id);
        Save();
    }

    public void PushRecentMount(uint id)
    {
        RecentMounts.Remove(id);
        RecentMounts.Insert(0, id);
        const int max = 6;
        if (RecentMounts.Count > max)
            RecentMounts.RemoveRange(max, RecentMounts.Count - max);
        Save();
    }

    // S322: persisted per-section heights for the Emotes/Minions quick grids. 0 = use the computed 3-row
    // default; >0 = a user override set by dragging the resize handle under that grid. Favourites and History
    // are independent so a long favourites list and a tidy history can be sized separately.
    public float EmoteFavHeight { get; set; } = 0f;
    public float EmoteHistHeight { get; set; } = 0f;
    public float MinionFavHeight { get; set; } = 0f;
    public float MinionHistHeight { get; set; } = 0f;
    public float OrnamentFavHeight { get; set; } = 0f;
    public float OrnamentHistHeight { get; set; } = 0f;
    public float MountFavHeight { get; set; } = 0f;
    public float MountHistHeight { get; set; } = 0f;
    // S326n: the "All" lists are now trimmed to ~3 rows by default + adjustable, like Favourites/History.
    public float EmoteAllHeight { get; set; } = 0f;
    public float MinionAllHeight { get; set; } = 0f;
    public float OrnamentAllHeight { get; set; } = 0f;
    public float MountAllHeight { get; set; } = 0f;

    // S326o: character tab 2x2 quadrant layout — height of the TOP row of quadrants (Emotes/Mounts); the bottom row
    // (Minions/Accessories) fills the remainder. Dragging the divider trades space between the two rows.
    public float CharQuadrantTopHeight { get; set; } = 260f;

    // Persist after a resize-handle drag ends (the UI mutates the height live, saves once on release).
    public void SaveHeights() => Save();

    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}

// S328am: a named relay endpoint for the service picker. BuiltIn services can't be deleted (only custom ones).
public class RelayService
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    // v0.7.247: the access key, entered alone. The full connect URL is composed as {Url}?k={Key} by
    // SyncSelectedRelayUrl — users type only the key they're handed, the plugin does the wss://.../?k= bureaucracy.
    public string Key { get; set; } = "";
    public bool BuiltIn { get; set; } = false;
}
