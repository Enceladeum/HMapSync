using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HMSync.Services;
using HMSync.Sync;
using HMSync.UI;
using Glamourer.Api.Enums;
using CharacterModes = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes;

namespace HMSync;

public sealed class HMSyncPlugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly IGameInteropProvider hooks;
    private readonly ISigScanner sigScanner;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IGameGui gameGui;   // S316: for carpet orientation-ring WorldToScreen overlay
    private readonly ITextureProvider textureProvider;   // S322e: emote-browser icons
    private readonly IAddonLifecycle addonLifecycle;   // v0.7.339: mount-HUD icon click-to-dismount

    private readonly HMSyncConfig config;
    private readonly RelaySyncService relay;
    private readonly StateCaptureService stateCapture;
    private readonly StateApplyService stateApply;
    private readonly PacketFilterService packetFilter;
    private readonly OpcodeMapService opcodeMap;
    private SayFilterService sayFilter = null!;
    private bool sayDriftBanner;   // S328p: set when the say passthrough auto-shuts (drift/patch); shown in the Config tab + session strip
    private bool relearnGotOut, relearnGotIn;   // S328q: symmetric re-learn - track which direction's opcode has been captured
    private readonly ZoneLoadService zoneLoad;
    private readonly CutsceneStageService cutscene;   // cutscene free-roam (Tier B LoadZone / Tier A donor-redirect)
    private readonly NoclipService noclip;
    private readonly CarpetService carpet;   // S315: ported HCollider ground-carpet (walk anywhere)
    private readonly DeckFloorService deckFloor;   // v0.7.351: constructive collision - add box floor patches
    private readonly GPoseMountDrawService gposeMountDraw;   // v0.7.358: keep HMS mounts drawn in gpose
    private readonly SkillSyncService skillSync;   // COSM_1_016: cosmetic skill capture + peer replay
    private readonly InstalledPluginService installedPlugins;   // v0.7.371: Modules panel presence + open
    private readonly LocalStateDetector detector;
    private readonly MonikerService moniker;   // S328x: Moniker nameplate integration
    private readonly NpcVisibilityService npcVisibility;   // S328aa: host-authoritative NPC scene-cleanup
    private readonly NetStatsService netStats;   // S328ag: relay bandwidth instrumentation
    private readonly RelayHealthService relayHealth;   // background /health poll → relay traffic-light
    private readonly LocoDiagService locoDiag;   // S328ai: receiver-side locomotion diagnostic
    private readonly EmoteDiagService emoteDiag;
    private readonly ActorVisibilityService actorVisibility;
    private readonly MapSettingsService mapSettings;   // S326: host-authoritative environment (time/weather/BGM/NPC)
    private readonly TimeFreezeService timeFreeze;      // S327f: Brio-style Eorzea-time freeze hook
    private readonly GlamourerIpc glamourer;   // S246: cosmetic visibility toggles via Glamourer (if installed)
    private readonly AfkNotificationSuppressor afkSuppressor;   // S250: hide duty AFK warning in-session only
    private readonly MountHudDismountService mountHudDismount;   // v0.7.339: click-to-dismount on the mount HUD icon
    private readonly HMSyncUI ui;

    private readonly ConcurrentQueue<Action> mainThreadQueue = new();

    // S247: HMS-tracked INTENDED state for the cosmetic display toggles. We must NOT read "current"
    // from the DrawData bit to decide the next toggle: when Glamourer owns the state (S246), it sets
    // VisorState/WeaponState in its OWN state and does NOT reliably flip the IsVisorToggled/
    // IsWeaponHidden DrawData bits, so reading them back gives a stale value and the toggle pushes
    // the same state every click (the "visor only ever turns on" / "arms only ever shows" bug).
    // Headgear happened to work because HideHeadgear writes its bit through. Tracking our own
    // intent makes all three flip correctly regardless of whether Glamourer or DrawData is backing.
    // hidden-semantics for hat/arms (true = hidden), toggled-semantics for visor (true = visor up).
    private bool headgearHidden;
    private bool weaponHidden;
    private bool visorToggled;

    public HMSyncPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log,
        IChatGui chat,
        IGameInteropProvider hooks,
        ISigScanner sigScanner,
        IDataManager dataManager,
        IClientState clientState,
        IGameGui gameGui,
        ITextureProvider textureProvider,
        IAddonLifecycle addonLifecycle)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.chat = chat;
        this.hooks = hooks;
        this.sigScanner = sigScanner;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.gameGui = gameGui;
        this.textureProvider = textureProvider;
        this.addonLifecycle = addonLifecycle;

        config = pluginInterface.GetPluginConfig() as HMSyncConfig ?? new HMSyncConfig();
        config.MigrateRecentZones();   // v0.7.231: forward-migrate legacy RecentZones → RecentPlaces (idempotent)
        config.MigrateRelayKeys();     // v0.7.248: split baked-in ?k= keys into the Key field (fixes stale-master-key bug)
        config.Initialize(pluginInterface);
        config.EnsureRelayServicesSeeded();   // S328am: populate built-in relay services on first run
        config.EnforceRelayServiceForMode(config.ShowDebugCommands);   // v0.7.338: keep non-debug users off the localhost dev endpoint

        // S329a (Stage 1): validate the sync-lane census - every TransformData field must map to exactly one lane
        // (or be marked non-render). This is the anti-orphan guard: it fails LOUD here if a field is added without a
        // lane assignment, rather than silently not-syncing after the Stage 2/3 split. Scaffolding only right now -
        // the wire still carries the monolithic TransformUpdate; this just keeps the census honest ahead of the split.
        var laneCensusError = HMSync.Sync.LaneCensus.Validate();
        if (laneCensusError != null)
            log.Error("[HMSync] SYNC-LANE CENSUS INVALID - a field is orphaned or misassigned:\n" + laneCensusError);
        else
        {
            var lc = HMSync.Sync.LaneCensus.LaneCounts();
            log.Information("[HMSync] Sync-lane census OK - HOT:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Hot)
                + " WARM:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Warm)
                + " COLD:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Cold)
                + " HOST:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Host)
                + " (scaffolding - wire still monolithic TransformUpdate).");
        }

        relay = new RelaySyncService(log);
        netStats = new NetStatsService();   // S328ag
        relay.NetStats = netStats;          // relay feeds byte counters
        relayHealth = new RelayHealthService(config, log, () => ui?.IsOpen ?? false);
        relayHealth.Start();                // /health poll while the window is open → the uplink light
        detector = new LocalStateDetector(objectTable, dataManager, log);
        detector.DebugTrace = config.ShowDebugCommands;   // S328w: LOCOTRACE only when debug mode is on
        moniker = new MonikerService(pluginInterface, log);   // S328x: Moniker nameplate integration (optional; inert if absent)
        npcVisibility = new NpcVisibilityService(objectTable, log);   // S328aa: NPC scene-cleanup (despawn / hide quest signs)
        stateCapture = new StateCaptureService(objectTable, framework, relay, detector, log);
        // (S328ag dirty-check toggle removed - change-detection is always on now.)
        locoDiag = new LocoDiagService(log);   // S328ai
        stateApply = new StateApplyService(objectTable, framework, dataManager, log, pluginInterface, sigScanner);
        stateApply.LocoDiag = locoDiag;        // receiver-side locomotion diagnostic hook (after stateApply exists)
        stateApply.DebugTrace = config.ShowDebugCommands;   // S329b: receiver traces respect plugin debug mode
        packetFilter = new PacketFilterService(log, hooks, sigScanner);
        opcodeMap = new OpcodeMapService(log);
        // Say filter: supplier returns the set of session-member character names (local + peers), lower-cased. Used to
        // decide whose /say to allow; everyone else's /say is hidden while the filter is enabled (session active + opt-in).
        sayFilter = new SayFilterService(chat, log, () =>
        {
            var names = new System.Collections.Generic.HashSet<string>();
            var lp = objectTable.LocalPlayer;
            if (lp != null) names.Add(lp.Name.TextValue.ToLowerInvariant());
            foreach (var info in stateApply.Peers.Values)
                if (!string.IsNullOrEmpty(info.CharacterName)) names.Add(info.CharacterName.ToLowerInvariant());
            return names;
        },
        // Synthetic distance (yalms) from local player to the named sender's puppet. Same computation as the
        // participants list: distance between local player and the peer's rendered (synthetic-position) object.
        // 0 for the local player (always in range); -1 if the sender isn't a resolvable session peer.
        senderName =>
        {
            var lp = objectTable.LocalPlayer;
            if (lp == null) return -1f;
            if (string.Equals(lp.Name.TextValue, senderName, StringComparison.OrdinalIgnoreCase)) return 0f;
            foreach (var info in stateApply.Peers.Values)
            {
                if (!string.Equals(info.CharacterName, senderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (info.ObjectIndex.HasValue)
                {
                    var obj = objectTable[info.ObjectIndex.Value];
                    if (obj != null) return (lp.Position - obj.Position).Length();
                }
                return -1f;   // known peer but no resolved body → don't cull
            }
            return -1f;
        });
        zoneLoad = new ZoneLoadService(objectTable, log, sigScanner, hooks, framework, dataManager);
        zoneLoad.DebugMode = config.ShowDebugCommands;      // v0.7.259: origin/map-hop notifications only in debug (must be AFTER construction)
        cutscene = new CutsceneStageService(pluginInterface, log, dataManager, zoneLoad);
        noclip = new NoclipService(objectTable, framework, log, sigScanner, hooks);
        carpet = new CarpetService(objectTable, framework, log, config);
        emoteDiag = new EmoteDiagService(objectTable, framework, dataManager, log);
        actorVisibility = new ActorVisibilityService(objectTable, framework, log);
        timeFreeze = new TimeFreezeService(sigScanner, hooks, log);   // S327f: Brio-style time-freeze hook
        mapSettings = new MapSettingsService(dataManager, log, timeFreeze, sigScanner);   // S326
        glamourer = new GlamourerIpc(pluginInterface);   // S246: optional Glamourer routing for visibility toggles
        // S248: when Glamourer reports ANY actor's state changed, mark badges dirty. The refresh
        // (main thread) reads the LOCAL player's state - we don't need to match the address here;
        // a cheap re-read on any change keeps the badges correct without per-frame polling.
        glamourer.StateChanged += _ => glamourerBadgesDirty = true;

        // S250: AFK-warning suppressor. Gated on relay.IsConnected so it ONLY acts during an HMS
        // session - never swallows a real player's expel warning in a real dungeon.
        afkSuppressor = new AfkNotificationSuppressor(chat, log, () => relay.IsSessionActive);

        // v0.7.351: deck-floor patcher. Player-position getter reuses ReadLivePosition (native GameObject.Position).
        deckFloor = new DeckFloorService(log, () => ReadLivePosition());

        // v0.7.358: keep HMS-applied mounts drawn while gposing (the probe proved state is fine; draw is the issue).
        gposeMountDraw = new GPoseMountDrawService(log, clientState, objectTable,
            () => relay.IsSessionActive, () => stateApply.GetPeerObjectIndices(),
            // v0.7.391: only let the recovery pass un-hide objects HMS itself hid. Blanket-clearing 0x02
            // undid the game's own hide of gpose originals, so original + clone both drew.
            idx => actorVisibility.WasHiddenByUs(idx));
        // v0.7.360: the hide sweep must stand down during gpose - it was hiding the player's gpose COPY
        // (different object index than the live local player) and the mount inherited the hide.
        actorVisibility.IsGPosing = () => clientState.IsGPosing;

        // COSM_1_016: skills. Capture hooks UseAction (original runs first, so the game keeps enforcing cooldowns/
        // restrictions); the sender carries the cast on WARM; the receiver replays it on the peer's puppet via the
        // engine's own ActionEffectHandler.Receive, which cascades animation + VFX + sound for free.
        installedPlugins = new InstalledPluginService(pluginInterface);
        skillSync = new SkillSyncService(log, hooks, () => relay.IsSessionActive);
        skillSync.Init();
        stateCapture.SkillCastSupplier = () => (skillSync.PendingActionId, skillSync.PendingActionType,
                                                skillSync.PendingActionEpoch, skillSync.PendingActionTarget,
                                                skillSync.PendingActionTargetCid);
        // The delegate's signature contains a Character*, so CONSTRUCTING it needs unsafe context here (the bridge
        // method being unsafe isn't sufficient - CS0214 is raised at the conversion site).
        unsafe { stateApply.SkillReplay = SkillReplayBridge; }

        // v0.7.339: click-to-dismount on the mount-status HUD icon (_StatusCustom2). The native click fires a dismount
        // ACTION the filter drops behind the firewall; route the click to HMS's local dismount instead. Only acts while
        // a synthetic session is active AND you're actually mounted - outside a session the native click works normally,
        // so we no-op and let the game handle it.
        mountHudDismount = new MountHudDismountService(addonLifecycle, gameGui, log, () =>
        {
            if (!relay.IsSessionActive) return;         // real play → let the game's own click handle it
            if (CurrentMountId() == 0) return;          // not mounted → nothing to do
            var result = stateApply.MountSelf(0);
            if (result == StateApplyService.MountResult.Dismounted)
            {
                noclip.DisableFlight();
                chat.Print("[HMSync] Dismounted.");
            }
        });
        mountHudDismount.Enable();

        relay.OnTransformReceived += stateApply.OnTransformReceived;
        stateApply.OnPeerBound = idx => { actorVisibility.RegisterPeer(idx); zoneLoad.UnhidePreservedObject(idx); };   // S327/v0.7.335: show a puppet the moment it binds, clearing BOTH hide systems
        // S328x: Moniker nameplate integration - capture the local chosen name into outgoing transforms, and apply a
        // peer's chosen name to their puppet. Both no-op if Moniker isn't installed (MonikerService.Available == false).
        stateCapture.MonikerNameSupplier = () => moniker.GetLocalName();
        stateApply.ApplyMonikerName = (idx, name, hideFc, hideName, redraw) => moniker.ApplyName(idx, name, hideFc, hideName, redraw);
        moniker.LocalPlayerIndex = () => { var lp = objectTable.LocalPlayer; return lp != null ? (int)lp.ObjectIndex : -1; };
        relay.OnPeerJoined += OnPeerJoined;
        relay.OnPeerLeft += OnPeerLeft;
        relay.OnHostTransfer += OnHostTransfer;
        relay.OnRoomJoined += OnRoomJoined;
        relay.WireDumpEmit = s => chat.Print(s);   // S331: route /hms wiredump output to chat
        relay.OnZoneLoadReceived += OnZoneLoadReceived;
        relay.OnSessionEnded += OnSessionEnded;
        relay.OnDisconnected += OnDisconnected;
        relay.OnError += OnRelayError;
        relay.OnRateLimited += OnRelayRateLimited;   // v0.7.464: soft throttle - advisory banner, no teardown

        packetFilter.Initialize();
        sayFilter.Initialize();
        zoneLoad.Initialize();
        noclip.Initialize();
        carpet.Initialize();
        afkSuppressor.Initialize();

        // Route status messages (e.g. flight) to chat - same surface as the /hms commands.
        noclip.StatusReport = msg => chat.Print(msg);
        carpet.StatusReport = msg => chat.Print(msg);
        zoneLoad.StatusReport = msg => chat.Print(msg);
        stateApply.Notify = msg => chat.Print(msg);
        // S326: peers apply the host's map-state (weather/time now; BGM/NPC stored for when their helpers land) from
        // the host's stream. Runs on the framework thread (StateApplyService bounces it there). Weather/time are
        // environment-global (not per-puppet), so this just drives the local EnvManager to match the host.
        stateApply.ApplyMapState = td =>
        {
            // Store the host's state always (so it applies when this peer's map finishes loading), but only touch the
            // live environment if a map is actually loaded here - never in the open world. Weather/time/BGM applied;
            // NPC-removal is still a held flag (despawn functionality pending).
            mapSettings.WeatherId = td.MapWeatherId;
            mapSettings.TimeForced = td.MapTimeForced;
            mapSettings.EorzeaHour = td.MapEorzeaHour;
            mapSettings.EorzeaMinute = td.MapEorzeaMinute;
            mapSettings.BgmId = td.MapBgmId;
            mapSettings.RemoveNpcs = td.MapRemoveNpcs;
            mapSettings.HideQuestSigns = td.MapHideQuestSigns;
            mapSettings.MarkStateSet();
            if (zoneLoad.IsZoneLoaded)
            {
                // v0.7.475 - MIRROR VERBATIM, INCLUDING 0. The v0.7.429 version substituted the zone's native
                // default whenever the host sent 0, on the reasoning that 0 renders an invalid "void" sky. That
                // reasoning was right about the render and wrong about the intent: the invalid void IS the
                // feature ("None - Atmospheric", the cinematic blank), and it is also where most debug weathers
                // legitimately land. Since the host now ships the sky it is actually rendering (PushMapState
                // reads live EnvManager), there is nothing here left to resolve - re-deriving would reintroduce
                // exactly the host/peer divergence both guards were written to prevent.
                mapSettings.ApplyWeather(td.MapWeatherId);   // idempotent write, safe to repeat; 0 is meaningful
                // Single time path: freeze at the host's time when held, release to the real clock when not.
                if (td.MapTimeForced) mapSettings.ApplyTime(td.MapEorzeaHour, td.MapEorzeaMinute);
                else mapSettings.DisableTimeOverride();
                // SINGLE-AUTHORITY BGM: the host broadcasts a RESOLVED concrete id (never 0). Mirror it VERBATIM - no
                // peer-side GetDefaultBgm/live-read (that independent resolution caused the host#3/peer-Silence drift).
                // Only (re)play on change so rapid epoch bumps (a time drag) don't restart the track.
                if (td.MapBgmId != 0 && td.MapBgmId != lastAppliedPeerBgm)
                    mapSettings.PlayBgm(td.MapBgmId);
                lastAppliedPeerBgm = td.MapBgmId;
                // S328aa: NPC scene-cleanup - engage the host's chosen NPC modes locally (despawn all / hide quest signs).
                // The service is a persistent watch (NPCs stream in by proximity), started when either mode is on and
                // stopped when both are off. Same host-authoritative broadcast + late-join replay as the rest of map-state.
                DriveNpcVisibility(td.MapRemoveNpcs, td.MapHideQuestSigns);
            }
        };
        // S291: once the deferred home-restore settles the actor at home, drop the packet filter. Held up
        // through the entire reload+restore so the server can't snap us back to the stale foreign-zone
        // position (the air-stop fling). This is the Hyperborea firewall-after-revert ordering, adapted
        // to our async (deferred-restore) revert.
        zoneLoad.OnHomeRestoreComplete = () =>
        {
            // v0.7.419 - POSTURE SANITISE AFTER SETTLE. Force-clear any posture the reload
            // rebuilt from the client's internal cache.
            // v0.7.449 - only evict the base lane (idle stomp) when the ORIGIN was a genuine seated/emote
            // posture (the cache-rebuild case that needs it). If the origin was merely STANDING (e.g. a
            // folded-arms cpose), the forced clear still resets mode/emote/draw-offset but SKIPS the idle
            // stomp that would otherwise flicker the cpose off-and-on for one cycle on exit.
            bool originWasPosture = originMode == CharacterModes.InPositionLoop
                                 || originMode == CharacterModes.EmoteLoop;
            SanitiseLocalPosture("post-settle", force: true, evictBaseLane: originWasPosture);

            if (packetFilter.IsActive)
            {
                packetFilter.Disable();
                chat.Print("[HMSync] Packet filter OFF.");
            }

            // v0.7.419 - SERVER-ACKNOWLEDGED STANDUP. The filter prevented us from telling the
            // server we stood up. After the filter drops, re-enter the origin posture and execute
            // the sit toggle so the server processes the mode transition. See ServerAckStandup().
            if (originWasPosture)
                ServerAckStandup();

            // v0.7.419 - re-clear peers after the reload rebuilt them from cached HMS state.
            // Stop() ran before the reload but the reload overwrites it. Do a second pass now
            // that the actors are settled. The server's natural update cadence will repaint
            // peers with their real state once both filters are down.
            stateApply.SanitisePeerPostures();

            // v0.7.328: write each preserved peer's captured origin position back onto its frozen actor, undoing the
            // synthetic-coord freeze (the "peer stuck at ~75u / OOB on return" bug). The actor is a continuously-present
            // real player pinned at origin by the firewall, so its true position is the session-start spot we captured.
            // Re-assert for a short window since a stray late settle-write could re-touch it right after the reload.
            if (retPeerOrigins.Count > 0)
            {
                foreach (var (idx, pos) in retPeerOrigins) stateApply.WritePeerPosition(idx, pos);
                retOriginFrames = 120;   // ~2s of re-assert
            }
        };
        // Movement hooks stay inert during any zone load/revert (teardown window where zone
        // objects may be half-destroyed).
        noclip.TransitionGuard = () => zoneLoad.IsTransitioning;
        carpet.TransitionGuard = () => zoneLoad.IsTransitioning;
        // S320: carpet is map-specific and must not carry across a zone load (you'd glide flat off the
        // first staircase on arrival). Turn it OFF + notify on ANY zone change - HMS-driven (/hms load)
        // or external (normal teleport / zone line). /hms stop|leave is handled in DoLeaveInternal.
        zoneLoad.ZoneWillChange += carpet.Disable;

        // S240: single consolidated window (Session + Zones tabs). Zone directory
        // wiring moves onto the unified UI; reuses the proven DoLoad path, greys out
        // Load unless hosting.
        ui = new HMSyncUI(config, relay, log, dataManager, gameGui, textureProvider)
        {
            OnLoadZone = id => RunOnMainThread(() => DoLoad(id)),
            OnQuickLoad = id => RunOnMainThread(() => DoQuickLoad(id)),
            OnLoadCutscene = idx => RunOnMainThread(() =>
            {
                if (idx < 0 || idx >= cutscene.Stages.Count) return;
                uint donor = clientState.TerritoryType != 0 ? clientState.TerritoryType : 1u;   // current zone as the donor
                if (!relay.IsSessionActive) DoStartSolo();                 // solo-if-idle, mirrors DoQuickLoad
                if (relay.IsSessionActive) cutscene.LoadStage(cutscene.Stages[idx], donor, DoLoad);   // DoLoad brings up the filter
            }),
            CutsceneEntries = cutscene.Stages.Select((st, i) => new HMSyncUI.CutsceneEntry
            {
                Name = st.Name, Region = st.Expansion, Quest = st.Quest, Code = st.Code, Bg = st.Bg, Id = st.TerritoryId, Index = i
            }).ToList(),
            // v0.7.371: Modules panel. Aliases cover the internal-vs-display name gap (HMS itself ships as "HM-Sync"),
            // so a module isn't silently reported missing just because its manifest name differs from the label.
            ModulePresent = n => n == "Moniker"
                ? installedPlugins.IsPresent("Moniker", "HMoniker", "HM-Moniker")
                : installedPlugins.IsPresent(n, "H" + n, "HM-" + n),
            ModuleCanOpen = n => n == "Moniker"
                ? installedPlugins.CanOpen("Moniker", "HMoniker", "HM-Moniker")
                : installedPlugins.CanOpen(n, "H" + n, "HM-" + n),
            OpenModule = n => { if (n == "Moniker") installedPlugins.Open("Moniker", "HMoniker", "HM-Moniker");
                                else installedPlugins.Open(n, "H" + n, "HM-" + n); },
            CanLoad = () => relay.HasMapAuthority,
            RunCommand = (sub, arg) => RunCommandFromUI(sub, arg),
            CanUseEmote = CanUseEmoteSafe,   // S322: lets the Emotes tab grey locked rows out of session
            CanUseMinion = CanUseMinionSafe, // S322: same for the Minions tab
            CanUseOrnament = CanUseOrnamentSafe, // S322k: same for the Accessories tab
            CanUseMount = CanUseMountSafe,    // S323c: same for the Mounts tab
            Carpet = carpet,   // S315: Carpet tab binds live to the service
            MapSettings = mapSettings,   // S326: Map Settings tab reads legal weather / BGM names per territory
            CurrentLoadedZone = () => zoneLoad.CurrentLoadedZone,   // S326d: live-vs-prep mode discrimination
            CurrentStageName = () => zoneLoad.ActiveStageBg != null ? cutscene.GetStageName(zoneLoad.ActiveStageBg) : null,   // v0.7.340: cutscene name for the Zone: header
            MovementAllowed = () => MovementEnableAllowed(),        // v0.7.262: one gate for all movement buttons
            MovementResearchAllowed = () => MovementResearchAllowed(), // v0.7.445: stricter gate for fly/noclip (teleport)
            PacketFilterActive = () => packetFilter.IsActive,       // S326e/f: packet-filter status (now a status dot)
            DebugMode = () => config.ShowDebugCommands,
            SetDebugMode = v => { config.ShowDebugCommands = v; config.Save(); detector.DebugTrace = v; stateApply.DebugTrace = v; zoneLoad.DebugMode = v; },
            ConnectedRelay = () => relay.IsConnected,                              // S328am: service-picker live indicator
            RelayKeyStatusGet = () => relayHealth.KeyStatus,                       // v0.7.317: key-status dot (grey/green/amber/red)
            ConfirmRelayKey = () =>                                                // verify the key via the relay's real WS handshake (101 = accepted)
            {
                var svcs = config.RelayServices; int s = config.SelectedRelayService;
                if (s >= 0 && s < svcs.Count) _ = relayHealth.CheckKey(svcs[s].Key ?? "");
            },
            ResetRelayKeyEdit = () => relayHealth.ResetKeyStatus(),                // re-open editing → clear the status
            RelayLightFn = () => relayHealth.Light,                                // relay reachability traffic-light
            ActiveRelayUrl = () => relay.IsConnected ? relay.ConnectedUrl : "",    // active endpoint (only when connected)
            SayOpcodeState = () => (config.SayOutboundOpcode, config.SayInboundOpcode, config.SayOpcodesVerified, config.SayOpcodesGameVersion),
            SetSayOpcodes = (o, i) => { config.SayOutboundOpcode = o; config.SayInboundOpcode = i; config.Save(); },
            VerifySayOpcodes = () =>
            {
                config.SayOpcodesVerified = true;
                config.SayOpcodesGameVersion = GetGameVersion();
                config.Save();
                sayDriftBanner = false;
                packetFilter.ConfigureSayOpcodes(config.SayOutboundOpcode, config.SayInboundOpcode);
                if (packetFilter.IsActive) { packetFilter.PassSayChat = true; packetFilter.PassSayChatOut = true; }
                chat.Print("[HMSync] Say opcodes marked verified for game version " + config.SayOpcodesGameVersion +
                    (packetFilter.IsActive ? ". Passthrough re-armed." : ". Passthrough will arm next session."));
            },
            RelearnSayOpcodes = () => DoSayFind("RELEARN"),
            SayDriftBanner = () => sayDriftBanner,
            DismissSayDriftBanner = () => sayDriftBanner = false,
            MonikerAvailable = () => moniker.Available,
            // S327x: packet inspector (Packets tab) wiring.
            CaptureActive = () => packetFilter.CaptureInbound,
            SetCapture = filterCsv => RunOnMainThread(() => DoPktCap(filterCsv ?? "")),
            StopCapture = () => RunOnMainThread(() => { if (packetFilter.CaptureInbound) DoPktCap(null); }),
            ClearCapture = () => packetFilter.ClearCapture(),
            SnapshotCapture = () => packetFilter.SnapshotCapture(),
            OpcodeName = op => opcodeMap.InboundName(op),
            OpcodeMapStatus = () => opcodeMap.StatusLine(),
            LocalEntityId = () => objectTable.LocalPlayer?.EntityId ?? 0u,
            EntityName = eid => objectTable.SearchByEntityId(eid)?.Name.TextValue ?? "",
            FlyActive = () => noclip.FlightActive,                  // S326m: folded movement toggle states
            NoclipActive = () => noclip.NoclipActive,
            CarpetActive = () => carpet.On,
            HereCoords = () => ReadHereCoords(),                    // S326m: spawn management
            LivePosition = () => ReadLivePosition(),
            OnTeleport = pos => RunOnMainThread(() => DoTeleport(pos)),
            CaptureSpawnFor = terr => CaptureSpawn(terr),
            RevertSpawnFor = terr => RevertSpawn(terr),
            // v0.7.230: stage-aware. On a swap cutscene stage the user spawn lives in UserStageSpawns keyed by bg
            // (not UserSpawns keyed by the shared donor id), so a plain UserSpawns[terr] check reported "no spawn" and
            // greyed the reset button even though one was set - and it couldn't be cleared. Check the active stage bg
            // first, then fall back to territory.
            HasUserSpawn = terr =>
                (zoneLoad.ActiveStageBg != null && config.UserStageSpawns.ContainsKey(zoneLoad.ActiveStageBg))
                || config.UserSpawns.ContainsKey(terr),
            BgmNowPlaying = () => config.MapBgmId == 0 ? "" : mapSettings.BgmName(config.MapBgmId),
            // S327g: the SINGLE host time-set path. Silent (no chat) - updates config, applies locally (freeze or
            // release via the Brio hook), and pushes map-state (bumps the epoch) so peers get this exact value on the
            // next transform. Called on drag (every frame), Freeze, and reset - one path, no spam, no separate mirror.
            SetHostTime = (h, m, forced) =>
            {
                config.MapEorzeaHour = h; config.MapEorzeaMinute = m; config.MapTimeForced = forced;
                if (forced) mapSettings.ApplyTime(h, m); else mapSettings.DisableTimeOverride();
                PushMapState();   // bump epoch → rides the next transform to peers
                config.Save();
            },
            EmotePlaying = () => IsEmotePlaying(),                  // S326p: dynamic action-button states
            MinionOut = () => CurrentMinionId() != 0,
            OrnamentOut = () => CurrentOrnamentId() != 0,
            MountOut = () => CurrentMountId() != 0,
            SessionParticipants = BuildParticipantList,             // S326f: Wholist-style participant table
            SummonPeer = peerId => DoSummonPeer(peerId),            // S326f: host summons a peer to their position
            KickPeer = peerId => DoKickPeer(peerId),                // S326f: host removes a peer from the room
            TransferHost = peerId => DoTransferHost(peerId),        // S326h: host hands the session to a peer
        };
        pluginInterface.UiBuilder.Draw += ui.Draw;
        pluginInterface.UiBuilder.Draw += ui.DrawCarpetOverlay;   // S316: rings render even when window closed
        pluginInterface.UiBuilder.Draw += ui.DrawCarpetBar;       // v0.7.252: the tear-off carpet control bar
        pluginInterface.UiBuilder.Draw += ui.DrawFaceControlBar;  // dynamic face control tear-off
        pluginInterface.UiBuilder.Draw += ui.DrawMovementBar;      // v0.7.465: movement strip tear-off
        pluginInterface.UiBuilder.Draw += ui.DrawAppearanceBar;    // v0.7.465: appearance strip tear-off
        pluginInterface.UiBuilder.OpenMainUi += ui.OpenMain;       // installer "Open" button → window on Session tab
        pluginInterface.UiBuilder.OpenConfigUi += ui.OpenConfig;   // installer "Settings" button → window on Config tab

        framework.Update += OnFrameworkUpdate;

        // v0.7.448: if a prior session crashed with maps still auto-revealed, a pending-restore file exists.
        // Replay those restores once the discovery manager is live (deferred poll), then delete the file.
        ArmRevealCrashRecovery();

        commands.AddHandler("/hms", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the HMapSync window.",
            ShowInHelp = true,
        });

        var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        log.Information("[HMSync] Plugin loaded v" + (asmVer != null ? asmVer.ToString(3) : "?"));

        // F3: best-effort once-per-session refresh of the packet-inspector opcode-name map from GitHub. Non-blocking;
        // falls back to the bundled map if GitHub is unreachable. Labels only - cannot affect the firewall.
        opcodeMap.StartRefresh(GetGameVersion());
    }

    // Teleport hold - a one-shot SetPosition gets re-grounded/reverted by the engine each tick (which is why noclip
    // writes every frame), so we force the target for a few frames to make it stick.
    private System.Numerics.Vector3? teleportHoldTarget;
    private int teleportHoldFrames;
    private int retOriginFrames;   // v0.7.328: post-return re-assert window for restoring peer origin positions
    private System.Collections.Generic.List<(ushort idx, System.Numerics.Vector3 pos)> retPeerOrigins = new();

    // v0.7.419 - origin posture state, captured at engage BEFORE SanitiseLocalPosture clears it.
    // Used in post-settle to execute a server-acknowledged standup if the server still thinks we're
    // in a posture. The server is the mode authority; the filter prevented us from telling it we stood.
    private CharacterModes originMode;
    private byte originModeParam;
    private ushort originEmoteId;   // v0.7.420: which emote to re-execute for origin restore
    // Dynamic face control auto-clear-on-move: anchor the player pose when a gaze is set; clear if they move/turn.
    private System.Numerics.Vector3 faceGazeAnchorPos;
    private float faceGazeAnchorRot;
    private bool faceGazeHasAnchor;
    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.PI * 2f;
        while (a < -MathF.PI) a += MathF.PI * 2f;
        return a;
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        while (mainThreadQueue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { log.Error("[HMSync] Main thread error: " + ex.Message); }
        }

        // Apply an in-progress teleport for a few frames so the engine's re-grounding doesn't revert it.
        if (teleportHoldFrames > 0 && teleportHoldTarget.HasValue) { ApplyTeleportHold(); teleportHoldFrames--; }

        actorVisibility.Update();
        npcVisibility.Update();   // S328aa: persistent NPC re-scan (NPCs stream in by proximity like furniture)

        // v0.7.352: apply/clear baked collision patches for the active cutscene stage (o1e1 observation deck, etc.).
        // Idempotent - creates the colliders once when the stage becomes active, drops them when it changes to null or
        // another stage. Only while a session's virtual map is active (patches are a synthetic-scene edit).
        deckFloor.EnsureStagePatches(relay.IsSessionActive ? zoneLoad.ActiveStageBg : null);

        gposeMountDraw.Update();    // v0.7.358: keep HMS mounts drawn while gposing

        // v0.7.328: re-assert restored peer origin positions for a brief window after return, in case a late settle
        // write re-touches the actor right after the reload. Self-limiting; only runs when armed on return.
        if (retOriginFrames > 0)
        {
            retOriginFrames--;
            if (retOriginFrames % 15 == 0)
                foreach (var (idx, pos) in retPeerOrigins) stateApply.WritePeerPosition(idx, pos);
        }

        // v0.7.320: guarantee the furniture de-draw poll is running on EVERY client in a session on a virtual map -
        // not just whoever's load path armed it. A peer pulled into the host's map (by any path) must de-draw
        // furniture the same as the host; the trigger is role-agnostic, so once the poll is subscribed the peer
        // catches re-streaming furniture whenever it's visible, no matter who approached. Idempotent (no state reset).
        if (relay.IsSessionActive && zoneLoad.IsZoneLoaded)
            zoneLoad.EnsureDeDrawPollRunning();

        // Dynamic face control: apply the local player's own gaze so they SEE it while setting it (apply only drives
        // puppets). Auto-clear on movement (fire-and-forget): set a gaze, then walking/turning releases it - matches
        // the RP flow of glancing away then pivoting back. ApplyGazeToLocal is called EVERY frame (not gated on
        // anyGaze) so that when the last slot clears, its own on/off tracking fires the release - otherwise the head
        // would stick in the last gaze after clearing.
        {
            bool anyGaze = FaceControlState.EyesOn || FaceControlState.BodyOn || FaceControlState.HeadOn;
            var lp = objectTable.LocalPlayer;
            if (anyGaze && lp != null)
            {
                var p = lp.Position; float rot = lp.Rotation;
                if (!faceGazeHasAnchor)
                {
                    // First frame this gaze is active: anchor the pose ONCE. Don't re-anchor every frame - otherwise
                    // 'moved' only ever measures one frame's delta, so gradual walking never trips the threshold
                    // (that was the walk-doesn't-clear-but-pivot-does bug). Anchor-at-set makes displacement cumulative.
                    faceGazeAnchorPos = p; faceGazeAnchorRot = rot; faceGazeHasAnchor = true;
                }
                else if (!FaceControlState.Locked)
                {
                    // Fire-and-forget auto-clear (unless "hold coords" is locked): moving OR turning past a small
                    // threshold clears the gaze. Measured against the ANCHOR (set-point), so walking accumulates.
                    float moved = (p - faceGazeAnchorPos).Length();
                    float rotDelta = Math.Abs(NormalizeAngle(rot - faceGazeAnchorRot));
                    if (moved > 0.05f || rotDelta > 0.05f)
                        FaceControlState.ClearAll();
                }
                if (!(FaceControlState.EyesOn || FaceControlState.BodyOn || FaceControlState.HeadOn))
                    faceGazeHasAnchor = false;
            }
            else faceGazeHasAnchor = false;

            // Always apply/release self-gaze (release path fires when a slot just turned off).
            stateApply.ApplyGazeToLocal();
        }
        // (end face control self-apply)

        // S248: when the window transitions closed→open, force a badge refresh.
        if (ui.IsOpen && !uiWasOpen)
            glamourerBadgesDirty = true;
        uiWasOpen = ui.IsOpen;

        // S248: refresh the Glamourer display badges when the window is open and either Glamourer
        // signalled a state change or we haven't read yet this open-session. Event-driven (no
        // per-frame IPC) - glamourerBadgesDirty is set by the StateChanged handler and on window open.
        if (ui.IsOpen && glamourerBadgesDirty)
        {
            glamourerBadgesDirty = false;
            RefreshGlamourerBadges();
        }

        // S326h: re-assert host map-state a short while AFTER a load settles. A zone/map load can clobber weather+time
        // (the load writes its own environment), so setting them before/during the load doesn't stick - we re-apply
        // once the load has settled. mapReassertCountdown is armed on load (ArmMapReassert) and on the host only.
        if (mapReassertCountdown > 0)
        {
            mapReassertCountdown--;
            if (mapReassertCountdown == 0 && relay.HasMapAuthority)
            {
                mapSettings.Reassert(zoneLoad.CurrentLoadedZone);   // host engages the resolved track locally
                PushMapState();   // + broadcast the resolved map-state so peers mirror the NEW zone's BGM (not stale)
            }
        }

        // S327j: guest post-load map-state re-apply. When it fires, clear the applied-epoch so the next transform
        // re-applies the host's held time/weather/BGM to the freshly-loaded map.
        if (guestMapReapplyCountdown > 0)
        {
            guestMapReapplyCountdown--;
            // S328ab: don't fire the re-apply until the zone is ACTUALLY loaded. A blind countdown can hit 0 mid-load,
            // where ApplyMapState skips the live write (IsZoneLoaded false) but still consumes the epoch → the state
            // never lands. Hold the countdown (re-arm to 1) while the zone is still loading so the forced re-apply lands
            // on a loaded map. The countdown is just a lower bound / settle delay now, not the trigger.
            if (guestMapReapplyCountdown == 0 && !relay.HasMapAuthority)
            {
                if (zoneLoad.IsZoneLoaded)
                    stateApply.ForceMapStateReapply();   // re-apply the host's (resolved, concrete) map-state on the next transform
                else
                    guestMapReapplyCountdown = 1;         // not loaded yet - check again next frame
            }
        }

        // v0.7.448: AUTO MAP REVEAL, settle-gated. Armed on every HMS load (ArmMapReveal) for host AND guest -
        // both load the zone and each reveals its OWN HUD fog locally. Like the guest re-apply above, hold the
        // countdown until the zone is actually loaded AND the agent's CurrentMapId has caught up, so we reveal
        // the RIGHT map (a blind countdown could fire mid-load on the stale/previous map). Purely local; the
        // original bytes are snapshotted for restore-on-exit + crash recovery inside AutoRevealMap.
        if (mapRevealCountdown > 0)
        {
            mapRevealCountdown--;
            if (mapRevealCountdown == 0)
            {
                if (zoneLoad.IsZoneLoaded)
                {
                    uint mapId = 0;
                    unsafe
                    {
                        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
                        if (agent != null) mapId = agent->CurrentMapId;
                    }
                    if (mapId != 0) AutoRevealMap(mapId);
                    else mapRevealCountdown = 1;   // agent not ready - check again next frame
                }
                else
                {
                    mapRevealCountdown = 1;        // zone not loaded yet - hold
                }
            }
        }

        // S327j: the old per-frame time-override lifecycle block was REMOVED here. It was the mystery disabler: it
        // gated "want time held" on `relay.IsHost`, so on a PEER (IsHost=false) it hit the else-branch and called
        // DisableTimeOverride() EVERY FRAME - unfreezing the clock ~1 frame after ApplyMapState froze it (the two
        // fought at frame cadence; the real clock won). It also fought the host's own SetHostTime path. Time is now
        // driven entirely by: host UI → SetHostTime (apply + push epoch) and peer → ApplyMapState (freeze if
        // MapTimeForced, else release). No per-frame re-assertion is needed - the Brio hook HOLDS once set, and
        // Reassert() re-applies after a map load. A single writer per client, no races.

        // S326h: PACKET-FILTER SAFETY WATCHDOG. The filter is what keeps the server seeing us idle while we're on a
        // virtual map broadcasting movement. If it drops UNEXPECTEDLY (hook failure, exception, external toggle) while
        // a map is loaded, we are exposed - the server sees real movement on a map we shouldn't be roaming. Previously
        // nothing reacted; the status dot just went red and relied on the host noticing. This makes it automatic and
        // immediate: an unexpected drop while loaded → full safe teardown (which returns us to origin + ends the
        // session, via the same idempotent DoLeaveInternal used on hard disconnect). We only trip when the filter was
        // EXPECTED to be on: connected + a virtual map loaded. During normal teardown the filter is turned off on
        // purpose, but by then IsZoneLoaded is being torn down too and DoLeaveInternal is already running/idempotent,
        // so re-entry is harmless. Latched so it fires once.
        if (relay.IsSessionActive && zoneLoad.IsZoneLoaded && !packetFilter.IsActive && !filterDropHandled)
        {
            filterDropHandled = true;
            log.Warning("[HMSync] Packet filter dropped while a virtual map was loaded - emergency return to origin + session end.");
            chat.PrintError("[HMSync] Lost protection on a virtual map. Returning you to safety and ending the session.");
            DoLeaveInternal(silent: true);
        }
        else if (packetFilter.IsActive || !relay.IsSessionActive)
        {
            // Reset the latch once we're safe again (filter back on, or fully out of a session).
            filterDropHandled = false;
        }
    }

    // S326: arm the post-load map-state re-assert (~2.5s at 60fps, comfortably after the furniture de-draw settle so
    // the environment has finished loading). Called after a successful load/host-load.
    private int mapReassertCountdown;    private int guestMapReapplyCountdown;   // S327j: after a guest zone-load, force map-state (held time) re-apply
    private uint lastAppliedPeerBgm;   // S327g: guest-side last-applied BGM id (idempotent apply - don't restart music every epoch bump)
    private bool filterDropHandled;   // S326h: latch so the packet-filter-drop emergency return fires once
    private void ArmMapReassert() => mapReassertCountdown = 150;

    // v0.7.448: arm the settle-gated auto map-reveal (consumed in the tick loop). Same ~2.5s lower bound as
    // the reassert; the tick holds it until the zone + agent are actually ready, so the value is just a floor.
    private int mapRevealCountdown;
    private void ArmMapReveal() => mapRevealCountdown = 150;

    // S328aa: engage/disengage the NPC scene-cleanup service from the two host modes. Start when either mode is on,
    // stop (restoring every NPC) when both are off, and push mode changes through live. Called from the map-state apply
    // (peers + host) and from Reassert (host/solo local engage) so despawn/quest-sign-hide behaves exactly like the
    // rest of the map-state backbone - host-authoritative, broadcast, late-join-replayed, solo-compatible.
    private void DriveNpcVisibility(bool despawn, bool hideQuestSigns)
    {
        if (despawn || hideQuestSigns)
        {
            npcVisibility.SetModes(despawn, hideQuestSigns);
            npcVisibility.Start();   // idempotent; SetModes already re-applied if it was already running
        }
        else
        {
            npcVisibility.Stop();    // both off → restore all NPCs
        }
    }

    private bool uiWasOpen;

    private bool glamourerBadgesDirty = true;

    // Reads Glamourer meta-state for the local player and pushes it to the UI badges. Main-thread only.
    private void RefreshGlamourerBadges()
    {
        try
        {
            if (!glamourer.Available)
            {
                ui.SetGlamourerBadges(false, false, false, false, false);
                return;
            }
            bool known = glamourer.TryGetMeta(0, out var wpn, out var hat, out var vis);
            ui.SetGlamourerBadges(true, known, wpn, hat, vis);
        }
        catch (Exception ex)
        {
            log.Debug("[HMSync] Glamourer badge refresh failed: " + ex.Message);
            ui.SetGlamourerBadges(false, false, false, false, false);
        }
    }

    private void RunOnMainThread(Action action) => mainThreadQueue.Enqueue(action);

    // Lets the GUI buttons run any /hms subcommand. Routes through the same dispatch as the
    // slash command, on the main thread. `sub` is the subcommand (e.g. "start"), `arg` optional.
    public void RunCommandFromUI(string sub, string? arg = null)
    {
        var args = arg == null ? sub : sub + " " + arg;
        RunOnMainThread(() => OnCommand("/hms", args));
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { ui.ToggleMain(); return; }

        var sub = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        // ── Default-deny session gate ──
        // Inverts the old per-handler whitelist (which failed OPEN - most commands fired with no
        // session check). Only these commands are allowed when NOT in a session: the ones that
        // START/ENTER a session, and pure-UI commands that touch no game state. EVERYTHING ELSE
        // returns "not in session". This is fail-closed: a new command is gated by default unless
        // explicitly added here.
        if (!relay.IsSessionActive)
        {
            switch (sub)
            {
                case "start":
                case "startsolo":
                case "starts":
                case "join":
                case "maps":
                case "load":       // v0.7.363: allowed out of session - it routes through DoQuickLoad, which starts a
                                   // solo session first (exactly what clicking a zone/recent chip has always done).
                                   // The command was the odd one out; the UI never required /hms start first.
                case "status":
                case "visor":      // S320: harmless cosmetic toggles (Glamourer-equivalent) - no session needed
                case "displayhead":
                case "displayarms":
                case "emote":      // S322: client-side self-emote (also useful solo for previewing/testing IDs)
                case "minion":     // S322: client-side self-minion (unlocked summonable solo; locked gated in DoMinion)
                case "accessory":  // S322k: client-side self-ornament (fashion accessory; locked gated in DoAccessory)
                case "senddiag":   // v0.7.418: outbound observer - must run OUT of session (that is the window under test)
                case "pktcap":     // S327: packet inspector capture - a diagnostic; useful out of session (passes packets through when not filtering)
                case "firecut":    // P1 cutscene probe - arms capture; must run in an inn (out of session)
                case "cutstop":    // P1 safety escape - must work anywhere
                    break; // allowed outside a session
                default:
                    chat.Print("[HMSync] Not in a session. Use /hms start or /hms join <code> first.");
                    return;
            }
        }

        // ── Debug-command gate (S328ad) ──
        // Developer/troubleshooting commands are hidden behind ShowDebugCommands so the user surface stays clean.
        // With debug off they report the requirement instead of running. This is the single place the whole debug
        // class is gated (no per-handler checks) - add a new debug command by listing it here.
        switch (sub)
        {

            case "firecut":
                if (arg != null && uint.TryParse(arg, out var _cutId)) zoneLoad.FireCutscene(_cutId);
                else chat.Print("[HMSync] usage: /hms firecut <cutsceneRowId>");
                return;


            case "cutstop":
                zoneLoad.CutStop();
                return;

            case "mapdiag":
            case "mapreveal":
            case "maprestore":
            case "netdiag":
            case "lanecensus":
            case "wiredump":
            case "lgbdump":
            case "diag":
            case "diagpeer":
            case "locodiag":
            case "gposediag":
            case "dumpstructs":
            case "housingdiag":
            case "furndiag":
            case "debug":
            case "teardownhousing":
                if (!config.ShowDebugCommands)
                {
                    chat.Print("[HMSync] /hms " + sub + " is a debug command. Enable debug mode in Config to use it.");
                    return;
                }
                break;
        }

        switch (sub)
        {
            case "start": DoStart(arg); break;
            case "starts": case "startsolo": DoStartSolo(); break;   // 'starts' primary; 'startsolo' kept as alias
            case "memo": CaptureSpawn(0); break;   // record a spawn point for the current map (== the Set spawn button)
            case "join":
                if (arg == null) { chat.Print("[HMSync] Usage: /hms join <code>"); return; }
                DoJoin(arg); break;
            case "load":
            {
                if (arg == null) { chat.Print("[HMSync] Usage: /hms load <territory ID | zone name | cutscene name/tag>"); return; }
                // v0.7.363: use DoQuickLoad (solo-if-idle → load), the same path the zone chips and recent-map chips
                // take. The command used to call DoLoad directly, which refuses when idle ("Not in a session") - an
                // inconsistency, not a safeguard: clicking a chip from idle has always started solo automatically.
                if (uint.TryParse(arg, out var zoneId)) { DoQuickLoad(zoneId); break; }
                // v0.7.362: cutscene stages resolve in TWO passes around the zone lookup, deliberately:
                //   (a) an exact TAG match ("o1e1") wins outright - tags never collide with place names;
                //   (b) zones are tried next, so real place names win. Several stage names ARE real zone names
                //       ("Limsa Lominsa", "Kholusia", "Baelsar's Wall", "The Burn"), and someone typing those means
                //       the zone, not the cutscene;
                //   (c) only then do stage NAMES match, catching stage-only names like "ship cabin".
                int csIdx = cutscene.ResolveStage(arg, out var csName, out var csTag, out var csOthers);
                bool exactTag = csIdx >= 0 && string.Equals(csTag, arg.Trim(), StringComparison.OrdinalIgnoreCase);
                if (exactTag)
                {
                    chat.Print("[HMSync] Loading " + csName + " (" + csTag + ").");
                    ui.OnLoadCutscene?.Invoke(csIdx);   // same path as the UI chip: solo-if-idle, donor = current zone
                    break;
                }
                if (ui.ResolveZoneByName(arg, out var namedId, out var resolvedName, out var others))
                {
                    chat.Print("[HMSync] Loading " + resolvedName + " (" + namedId + ")" +
                        (others > 0 ? "  [" + others + " other match" + (others == 1 ? "" : "es") + "]" : "") + ".");
                    DoQuickLoad(namedId);
                    break;
                }
                if (csIdx >= 0)
                {
                    chat.Print("[HMSync] Loading " + csName + " (" + csTag + ")" +
                        (csOthers > 0 ? "  [" + csOthers + " other match" + (csOthers == 1 ? "" : "es") + "]" : "") + ".");
                    ui.OnLoadCutscene?.Invoke(csIdx);
                    break;
                }
                chat.Print("[HMSync] No zone or cutscene found matching \"" + arg + "\".");
                break;
            }
            case "reload": DoReload(); break;
            case "leave": DoLeave(); break;
            case "stop": DoStop(); break;
            case "fly":
                if (!noclip.FlightActive && !MovementResearchAllowed()) { chat.Print("[HMSync] Flight is only available on a loaded map or cutscene (or research mode)."); break; }
                DoToggleFly(); break;
            case "facecamdump":
                DoFaceCamDump(); break;
            case "noclip":
                if (!noclip.NoclipActive && !MovementResearchAllowed()) { chat.Print("[HMSync] Noclip is only available on a loaded map or cutscene (or research mode)."); break; }
                DoToggleNoclip(); break;
            case "carpet":
                if (!carpet.On && !MovementResearchAllowed()) { chat.Print("[HMSync] Carpet is only available on a loaded map or cutscene (or research mode)."); break; }
                carpet.Toggle(); break;   // S315: ground-carpet - walk on unwired surfaces
            case "emote": DoEmote(arg); break;        // S322: play + sync an emote (locked ones gated to in-session)
            case "minion": DoMinion(arg); break;      // S322: summon + sync a minion (locked ones gated to in-session)
            case "accessory": DoAccessory(arg); break; // S322k: equip + sync a fashion accessory (ornament)
            case "mapweather": DoMapWeather(arg); break; // S326: host set forced weather (broadcast + apply)
            case "maptime": DoMapTime(arg); break;       // S326: host hold/set Eorzea time ("H:M" or "off")
            case "mapbgm": DoMapBgm(arg); break;         // S326: host set BGM (0 = none)
            case "npc": DoMapNpc(arg); break;             // S326/S328aa: host despawn all event NPCs ("on"/"off")
            case "qbubble": DoMapQuestSigns(arg); break;  // S328aa: host hide over-head quest bubbles ("on"/"off")
            case "roompassword": DoRoomPassword(arg); break; // S326f: room password (enforcement needs relay)
            case "roomlock": DoRoomLock(arg); break;         // S326f: lock room to new joiners (needs relay)
            case "transferhost": if (arg != null) DoTransferHost(arg); break; // S326h: hand host to a peer (needs relay)
            case "here": DoPrintHere(); break;
            case "debug":
                // S262: toggle development/research mode at runtime (no recompile). ON = LoadZone sets
                // up the InstanceContentDirector (re-arms MapEffect/director-update machinery for
                // explorer-mode investigation; the Duty-Info HUD will show). OFF = clean shipping load.
                // Applies on the NEXT /hms load. Resets to OFF on plugin reload.
                {
                    zoneLoad.ResearchMode = !zoneLoad.ResearchMode;
                    LocalStateDetector.Verbose = zoneLoad.ResearchMode; // S304: gate pose/cpose/mode traces too
                    chat.Print("[HMSync] Research mode " + (zoneLoad.ResearchMode ? "ON" : "OFF") +
                        " - director setup " + (zoneLoad.ResearchMode ? "ENABLED (Duty-Info HUD will show on next load)" : "disabled (clean load)") +
                        ". Applies on the next /hms load.");
                }
                break;
            case "lgbdump":
                if (arg != null && uint.TryParse(arg.Trim(), out var lgbTid))
                { zoneLoad.DumpLgb(lgbTid); chat.Print("[HMSync] LGB dump for " + lgbTid + " logged - check [LGBDUMP]."); }
                else chat.Print("[HMSync] Usage: /hms lgbdump <territoryId>");
                break;
            case "status": DoStatus(); break;
            case "senddiag":
                // v0.7.418: OUTBOUND packet observer. Logs every opcode the client emits, with
                // PASS/SUPPRESS, and installs the send hook in pass-through so it works with NO session
                // running - which is the window the exit-freeze needs (does the client resume sending
                // movement after teardown, or stay silent?).
                // Safe to leave on: OnSendPacket returns Original unconditionally while !IsActive.
                {
                    packetFilter.SendDiag = !packetFilter.SendDiag;
                    if (packetFilter.SendDiag) packetFilter.EnableCaptureOnly();
                    else if (!packetFilter.CaptureInbound) packetFilter.DisableCaptureOnly();
                    chat.Print("[HMSync] Outbound packet diagnostic " + (packetFilter.SendDiag ? "ON" : "OFF") +
                        " - watch [SEND-DIAG] in /xllog. Nothing is filtered while out of session.");
                }
                break;
            case "pktcap": DoPktCap(arg); break;
            case "mapdiag": DoMapDiag(); break;   // S328ab: map-reveal investigation (logs AgentMap + discovery state)
            case "mapreveal": DoMapReveal(); break;   // v0.7.447: TEST - snapshot + reveal the current map's discovery table (research mode)
            case "maprestore": DoMapRestore(); break; // v0.7.447: TEST - write the snapshot back (undo mapreveal)
            case "netdiag": DoNetDiag(arg); break;   // S328ag: relay bandwidth diag (live rates + reset)
            case "mounthud":
                // v0.7.339 probe: force a mount-HUD attach attempt + dump the addon/node state, so we can see whether
                // _StatusCustom2 is found, whether the node exists, and whether AddEvent takes - independent of the
                // lifecycle listener timing. Run it WHILE MOUNTED.
                mountHudDismount.DebugProbe();
                break;
            case "doordump":
                // v0.7.342 probe: in o1e1, report the gate state + total BgPart count + every BgPart near the two door
                // positions (path/pos/dist/loaded). Tells us whether the door pass runs and whether the doors exist as
                // BgPart instances (cutscene layouts may populate InstancesByType differently than real zones).
                zoneLoad.DumpDoorsO1E1();
                break;
            case "roaddump":
                // v0.7.353b probe (/hms roaddump [term]): list every BgPart whose path contains term (default flo01) with
                // real path/pos/collider-type - to fix the road-clone source match (which found nothing).
                zoneLoad.DumpRoads1345(arg ?? "");
                break;
            case "weatherdiag":
                // v0.7.473: dump the weather picker's inputs for the loaded zone. Read /xllog for [WXDIAG].
                mapSettings.DumpWeatherDiag(zoneLoad.CurrentLoadedZone);
                chat.Print("[HMSync] Weather diagnostic written to /xllog - look for [WXDIAG]. Also reports config.MapWeatherId=" + config.MapWeatherId + ".");
                break;
            case "linevfx":
            {
                // v0.7.466 (/hms linevfx [scan|gfx|one|off|destroy|on]): boss-barrier LINE suppression, type 59.
                // DIAGNOSTIC-FIRST - run with no argument and read /xllog before mutating anything. The scan says
                // which of the three mechanisms is usable; `one` exists so the first SetActive costs one call.
                // NB: named lvSub, not sub - `sub` is the outer dispatcher's subcommand local (line ~772) and
                // C# forbids shadowing it here (CS0136).
                string lvSub = (arg ?? "scan").Trim().ToLowerInvariant();
                switch (lvSub)
                {
                    case "scan":
                        zoneLoad.DumpLineVfx();
                        chat.Print("[HMSync] LineVFX scan written to /xllog - look for [LINEVFX]. Read the last line: it names the mechanism to use.");
                        break;
                    case "gfx":
                        chat.Print("[HMSync] LineVFX: hid graphics leaf on " + zoneLoad.SuppressLineVfx("gfx", 0) + " instance(s).");
                        break;
                    case "one":
                    case "near":
                        // v0.7.468: targets the instance NEAREST the player, so a single-instance test is also a
                        // visual one. On 893 the old first-in-map-order pick was 240 units away.
                        chat.Print("[HMSync] LineVFX: DestroyPrimary on " + zoneLoad.SuppressLineVfx("destroy", 1, nearest: true)
                            + " nearest instance.");
                        break;
                    case "auto":
                        zoneLoad.SetLineVfxAuto(!zoneLoad.LineVfxAuto);
                        chat.Print("[HMSync] LineVFX auto-cadence " + (zoneLoad.LineVfxAuto ? "ON" : "OFF")
                            + (zoneLoad.LineVfxAuto ? " - lines are re-suppressed every frame as they re-stream." : " - lines will return on movement."));
                        break;
                    case "off":
                        chat.Print("[HMSync] LineVFX: SetActive(false) on " + zoneLoad.SuppressLineVfx("setactive", 0) + " instance(s).");
                        break;
                    case "destroy":
                        chat.Print("[HMSync] LineVFX: DestroyPrimary on " + zoneLoad.SuppressLineVfx("destroy", 0)
                            + " instance(s) - NOT reversible without a re-stream.");
                        break;
                    case "on":
                        // Disable the cadence BEFORE restoring - otherwise the very next frame re-destroys
                        // everything we just restored and the command silently appears to do nothing.
                        zoneLoad.SetLineVfxAuto(false);
                        chat.Print("[HMSync] LineVFX: auto-cadence OFF; restored " + zoneLoad.RestoreLineVfx()
                            + " instance(s) (a destroyed primary returns on re-stream, i.e. when you move).");
                        break;
                    default:
                        chat.Print("[HMSync] /hms linevfx [scan|near|gfx|off|destroy|on|auto] - auto-cadence is ON by default.");
                        break;
                }
                break;
            }
            case "vfxdump":
                // v0.7.380 probe (/hms vfxdump [term]): list every VFX instance's real .avfx path in this zone,
                // whether the current suppression patterns would match it, and whether its graphics leaf is
                // reachable. Turns "suppress that effect" into an exact substring instead of a guess, and
                // distinguishes layout VFX (hideable here) from actor/system VFX (not in the layout graph at all).
                zoneLoad.DumpVfxPaths(arg ?? "");
                chat.Print("[HMSync] VFX dump written to /xllog - look for [VFXDUMP]. MATCH = a current pattern hits it.");
                break;
            // v0.7.456: /hms gposemount (the gpose mount-flicker diagnostic probe) REMOVED - a spent v0.7.357
            // investigation tool (its finding is recorded in GPoseMountDrawService). The probe object, per-tick
            // Update, and wiring are gone; the null-safe GPoseProbe?.NoteClear stubs in StateApplyService become
            // permanent no-ops (left in place - they're ?.-guarded and woven into working mount-clear paths; a
            // later deep-clean can strip them). Not user-facing; pure dev scaffolding.
            // v0.7.456: /hms deckfloor (the manual place-and-see collision-patch authoring tool) REMOVED - it was
            // a one-off used to dial in the o1e1 ship-cabin observation-deck floor, now baked into DeckFloorService
            // (StagePatches) and auto-applied via EnsureStagePatches on stage load. The service + baked patch stay;
            // only the runtime hand-placement command is gone (superseded, and carpet covers the general case).
            case "wiredump":
                // S331 (Stage 4): arm the binary-frame decoder - capture the next N frames (sent+received) and
                // pretty-print kind + decoded msgpack payload as readable JSON. Buys back the eyeball-ability the JSON
                // wire gave for free. Default 10 frames. Usage: /hms wiredump [n]
                {
                    int n = 10;
                    if (!string.IsNullOrWhiteSpace(arg) && int.TryParse(arg.Trim(), out var parsed) && parsed > 0)
                        n = Math.Min(parsed, 200);
                    relay.ArmWireDump(n);
                }
                break;
            case "lanecensus":
                // S329a: print the sync-lane census to chat on demand - verifies the anti-orphan guard without
                // hunting the startup log. Shows the field→lane breakdown, or the error if a field is orphaned.
                {
                    var err = HMSync.Sync.LaneCensus.Validate();
                    if (err != null)
                        chat.PrintError("[HMSync] LANE CENSUS INVALID:\n" + err);
                    else
                    {
                        var lc = HMSync.Sync.LaneCensus.LaneCounts();
                        int total = HMSync.Sync.LaneCensus.Map.Count;
                        chat.Print("[HMSync] Lane census OK - " + total + " render fields mapped: "
                            + "HOT:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Hot)
                            + " WARM:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Warm)
                            + " COLD:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Cold)
                            + " HOST:" + lc.GetValueOrDefault(HMSync.Sync.SyncLane.Host)
                            + ". Wire is lane-split (binary, per-lane change detection).");
                    }
                }
                break;
            case "teardownhousing":
                // CONTINGENCY ONLY (console command, intentionally not in HelpMessage). The normal
                // furniture lifecycle is fully handled by the deferred de-draw (despawn on load) +
                // lean revert (respawn on stop) - this nuclear Dtor(1) territory teardown is NOT in
                // that path and is kept only as a manual fallback if the de-draw ever fails on some
                // map. Clunky to type by design; do not wire into auto-flow.
                DoTeardownHousing();
                break;
            case "gposediag":
                // v0.7.390: audit the object table across a gpose enter->exit cycle. Logs index AND
                // ContentId side by side every frame (change-gated), so a ContentId appearing at two
                // indices identifies the clone, and localIdx disagreeing with our own ContentId's index
                // catches the self-hide in the act. Covers both gpose symptoms in one run.
                actorVisibility.Diag = !actorVisibility.Diag;
                chat.Print("[HMSync] gpose diagnostic " + (actorVisibility.Diag ? "ON" : "OFF") +
                    " - enter gpose, look around, exit. Watch [GPOSEDIAG] in /xllog.");
                break;
            case "locodiag":
                locoDiag.Start();
                chat.Print("[HMSync] Locomotion diagnostic armed - 15s. Jump, dismount, stop, turn near a peer; watch [LOCODIAG] in /xllog.");
                break;
            case "diag":
                emoteDiag.StartLogging();
                chat.Print("[HMSync] Diagnostic logging started (LOCAL) - 15 seconds.");
                break;
            case "diagpeer":
                emoteDiag.StartLogging(15000, peer: true);
                chat.Print("[HMSync] Diagnostic logging started (PEER) - 15 seconds.");
                break;
            case "dumpstructs":
                try
                {
                    var dumpPath = StructDumper.Dump(pluginInterface.ConfigDirectory.FullName);
                    log.Information("[HMSync] Struct dump written to " + dumpPath);
                    chat.Print("[HMSync] Struct dump written to: " + dumpPath);
                }
                catch (Exception ex)
                {
                    log.Error("[HMSync] Struct dump failed: " + ex);
                    chat.Print("[HMSync] Struct dump failed: " + ex.Message);
                }
                break;
            case "mount":
                // S197f ASYNC complete: /hms mount SELF-mounts the local player. The two proven halves,
                // now joined:
                //   (1) Self-view: MountSelf mounts YOU locally (Mode=Mounted) → your own client renders
                //       the mount natively (you see yourself mounted, the self-illusion).
                //   (2) Peer-view: the sender broadcasts your MountId + PLAIN ON-FOOT locomotion (iter 1:
                //       moveMode forced to Ground even while mounted), so every peer applies your mount to
                //       your puppet and drives it through the PROVEN testmount path - native mounted
                //       animation, all restrictions/speed limits respected, skate-free, with the free
                //       native dismount/dismiss animation.
                // They run async and reconcile visually because everyone sees the same world. "/hms mount 0"
                // dismounts you (native dismiss plays); session exit (stop/leave/disconnect/crash) clears it
                // via SanitizePeerStates; the mount PERSISTS across map loads (only session-exit tears down).
                // S326q: mount toggle + bare-arg convenience.
                //   • "/hms mount" (bare) while MOUNTED → dismount; while unmounted → mount the MOST RECENT.
                //   • "/hms mount <id>" while THAT mount (or any, engine-toggle) is out → dismount; else mount it.
                // This makes the dynamic Mount/Dismount button and a repeated row-click both dismount as expected.
                {
                    var curMount = CurrentMountId();
                    short reqMount;
                    var marg = arg?.Trim() ?? "";
                    if (marg.Length == 0)
                    {
                        // Bare: dismount if mounted, else most-recent from history.
                        if (curMount != 0) reqMount = 0;
                        else
                        {
                            var recent = config.RecentMounts;
                            if (recent.Count == 0)
                            { chat.Print("[HMSync] No recent mount to summon. Try /hms mount <id> or pick one in the Character tab."); break; }
                            reqMount = (short)recent[0];
                        }
                    }
                    else if (!short.TryParse(marg, out reqMount))
                    {
                        chat.Print("[HMSync] Usage: /hms mount [id]  (bare = most recent / dismount if mounted; 0 = dismount).");
                        break;
                    }
                    // Toggle: clicking the mount that's already out (or "0") dismounts.
                    if (reqMount != 0 && curMount == (ushort)reqMount) reqMount = 0;

                    var result = stateApply.MountSelf(reqMount);
                    switch (result)
                    {
                        case StateApplyService.MountResult.Mounted:
                            noclip.EnableFlight();
                            config.PushRecentMount((ushort)reqMount);
                            chat.Print("[HMSync] Mounted " + reqMount + ".");
                            break;
                        case StateApplyService.MountResult.Dismounted:
                            noclip.DisableFlight();
                            chat.Print("[HMSync] Dismounted.");
                            break;
                        case StateApplyService.MountResult.InvalidId:
                            chat.Print("[HMSync] Invalid mount ID " + reqMount + ". No such mount exists.");
                            break;
                        case StateApplyService.MountResult.NoLocalPlayer:
                            chat.Print("[HMSync] Can't mount: no local player.");
                            break;
                    }
                }
                break;
            case "housingdiag":
                // S145 [HOUSINGDIAG]: read-only decoration-state dump. "/hms housingdiag" starts,
                // "/hms housingdiag stop" stops early. Walk into the FC room while it runs.
                if (arg != null && arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    zoneLoad.StopHousingDiag();
                    chat.Print("[HMSync] HOUSINGDIAG stopped.");
                }
                else
                {
                    zoneLoad.StartHousingDiag();
                    chat.Print("[HMSync] HOUSINGDIAG started - now run the test sequence (see chat/log). Auto-stops in ~20s.");
                }
                break;
            case "furndiag":
                // v0.7.422 [FURNDIAG]: one-shot dump of every furniture-manager object AND every
                // GOM slot in the EventObjectManager range (440-500): kind, RenderFlags, DrawObject,
                // visibility, position. Run once with leaked items VISIBLE (post-hop) and once with
                // them hidden (post-initial-load); the diff names the rendering object + its kind.
                zoneLoad.DumpFurnDiag();
                chat.Print("[HMSync] FURNDIAG dumped - see log.");
                break;
            case "maps":
                // S240: open the consolidated window on the Zones tab.
                ui.OpenZones();
                chat.Print("[HMSync] Opened HM-Sync window (Zones).");
                break;
            case "displayarms": DoToggleDisplayArms(); break;
            case "displayhead": DoToggleDisplayHead(); break;
            case "visor": DoToggleVisor(); break;
            default: chat.Print("[HMSync] Unknown: " + sub); break;
        }
    }

    // ── Commands ──────────────────────────────────

    private void DoStart(string? code = null)
    {
        // Force-clear stale connection state
        if (relay.IsConnected)
        {
            DoLeaveInternal(silent: true);
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null) { chat.Print("[HMSync] No local player."); return; }

        // Guard: if the heartbeat sig didn't resolve (a patch broke the signature), starting a session would suppress
        // the heartbeat too and disconnect you. Fail loudly here instead. (The one opcode/sig failure mode - §14.)
        if (!packetFilter.HeartbeatResolved)
        {
            chat.Print("[HMSync] Cannot start: a game patch changed the heartbeat signature.");
            chat.Print("[HMSync] Starting a session now would disconnect you. This needs a plugin update to fix the signature.");
            return;
        }

        // The typed value is the room PASSWORD (the relay generates the opaque RoomId). Blank → auto-generate a short,
        // shareable one (shown in-session so the host can read it out); a custom /hms start <pw> still works.
        var password = (code ?? "").Trim();
        if (password.Length == 0) { password = GenerateShortPassword(); chat.Print("[HMSync] Room password: " + password); }

        DoStartAsync(password, LocalContentId(), localPlayer.EntityId, localPlayer.Name.TextValue);
    }

    // S328f - SOLO SESSION. Runs the full map-authoring feature set (zone load, time/weather/BGM, NPC, cosmetics,
    // movement, packet filter) with NO relay and no peers - the same client-side loop Hyperborea does. It's the
    // DoStartAsync engage sequence MINUS relay.Connect, with SoloMode set so HasMapAuthority/IsSessionActive are true.
    // The peer apply loop, roster, and all transmit are naturally inert without a connection (transmit gates on
    // IsConnected; the apply loop has no peers). Teardown reuses DoLeaveInternal verbatim (relay-independent).
    private void DoStartSolo()
    {
        if (relay.IsConnected || relay.SoloMode)
        {
            DoLeaveInternal(silent: true);
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null) { chat.Print("[HMSync] No local player."); return; }

        // Same heartbeat guard as a networked session - solo still engages the packet filter, so a broken heartbeat sig
        // would disconnect. Fail loudly instead. (§14.)
        if (!packetFilter.HeartbeatResolved)
        {
            chat.Print("[HMSync] Cannot start solo: a game patch changed the heartbeat signature.");
            chat.Print("[HMSync] Starting now would disconnect you. This needs a plugin update to fix the signature.");
            return;
        }

        // Solo lobby: set the flag so HasMapAuthority/IsSessionActive are true, but DON'T engage the synthetic
        // session yet - that fires on zone-load (EngageSyntheticSession in DoLoad), same as a hosted session. Until
        // then you're a normal player.
        relay.SoloMode = true;

        ui.SetStatus("Solo");
        chat.Print("[HMSync] Solo session ready. Load a map to start the scene. /hms stop to end.");
    }

    // S328p - say-passthrough pre-flight, run at every session start. Checks the game version against the stamp the
    // opcodes were confirmed on; if the game has patched since, the opcodes are UNVERIFIED → the passthrough stays
    // shut and the user is prompted to re-learn (fail-closed on patch). Otherwise loads the configured opcodes and
    // arms the passthrough. Wires the drift handler so a mid-session rotation (validator failures) also shuts it.
    private bool PrepareSayPassthrough()
    {
        // Wire drift → shut + notify (idempotent; safe to set each start).
        packetFilter.OnDriftDetected = () => RunOnMainThread(() =>
        {
            packetFilter.PassSayChat = false;
            packetFilter.PassSayChatOut = false;
            config.SayOpcodesVerified = false;
            config.Save();
            sayDriftBanner = true;
            chat.PrintError("[HMSync] The /say passthrough was shut off automatically: the chat packet no longer looks like chat, " +
                "which usually means a game patch changed it. Your /say won't reach session members until you re-learn it " +
                "(Config tab → Say opcodes → Re-learn). Everything else is unaffected.");
        });

        string gameVersion = GetGameVersion();
        // F1: only run the patch-detection when we could actually READ the live version. An empty read (CS not ready /
        // exception) is NOT evidence of a patch - comparing "" against a real stamp would falsely trip the drift branch
        // and shut a perfectly-good passthrough. Skip the check and leave the current verified state untouched.
        if (string.IsNullOrEmpty(gameVersion))
        {
            log.Warning("[HMSync] /say passthrough: could not read the live game version; skipping patch-change check " +
                "and leaving the current opcode state as-is.");
        }
        // If the game has patched since these opcodes were confirmed, treat them as unverified until re-learned.
        else if (!string.IsNullOrEmpty(config.SayOpcodesGameVersion) && config.SayOpcodesGameVersion != gameVersion && config.SayOpcodesVerified)
        {
            config.SayOpcodesVerified = false;
            config.Save();
            sayDriftBanner = true;
            chat.PrintError("[HMSync] The game was patched since the /say opcodes were last confirmed (" + config.SayOpcodesGameVersion +
                " → " + gameVersion + "). The /say passthrough is off until you re-learn it (Config tab → Say opcodes → Re-learn), " +
                "so it can't accidentally pass the wrong packet. Everything else works normally.");
        }

        if (!config.SayOpcodesVerified)
        {
            packetFilter.PassSayChat = false;
            packetFilter.PassSayChatOut = false;
            // F4: say WHY it's shut so the closed state is diagnosable from the log, not silent.
            log.Information("[HMSync] /say passthrough stays off: opcodes are not verified for this game version. " +
                "Re-learn them (Config tab → Say opcodes → Re-learn) to enable it.");
            return false;   // stay shut until re-learned
        }

        // v0.7.462 (P2): belt-and-suspenders - never arm with an EMPTY version stamp. A stamp is only written
        // by Re-learn (which captures live opcodes for the running version). Empty means "never learned on any
        // known version", so even if Verified somehow reads true, the opcodes aren't trustworthy for THIS game
        // version. Fail closed. (The default is now unverified, so this is a second guard, not the primary one.)
        if (string.IsNullOrEmpty(config.SayOpcodesGameVersion))
        {
            packetFilter.PassSayChat = false;
            packetFilter.PassSayChatOut = false;
            config.SayOpcodesVerified = false;
            config.Save();
            // F4: this branch means Verified read true but there's no version stamp - the opcodes were never captured
            // for any known version. Log it (rare, but otherwise invisible) instead of shutting silently.
            log.Warning("[HMSync] /say passthrough stays off: opcodes were marked verified but carry no game-version " +
                "stamp, so they can't be trusted for this version. Re-learn them to enable it.");
            return false;
        }

        packetFilter.ConfigureSayOpcodes(config.SayOutboundOpcode, config.SayInboundOpcode);
        packetFilter.PassSayChat = true;
        packetFilter.PassSayChatOut = true;
        return true;
    }

    private string GetGameVersion()
    {
        try { unsafe { return FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->GameVersionString ?? ""; } }
        catch { return ""; }
    }

    // S327f: clear all targets before the packet filter engages on session start. A queued/hard/focus target on an
    // INTERACTABLE (e.g. a door) can start an interaction whose server response the filter then DROPS, leaving the
    // character "occupied"-locked (only alt-F4 recovers - even logout shows "you're occupied"). Nulling the target
    // pointers means no interactable is queued, so nothing can get stuck. Safe: just clears selection state.
    private unsafe void ClearTargetsOnStart()
    {
        try
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts == null) return;
            ts->Target = null;
            ts->SoftTarget = null;
            ts->FocusTarget = null;
            ts->MouseOverTarget = null;
            ts->MouseOverNameplateTarget = null;
        }
        catch (Exception ex) { log.Warning("[HMSync] ClearTargetsOnStart failed: " + ex.Message); }
    }

    // S327: read the local player's stable ContentId (Character.ContentId @0x2358). Used as our identity on the wire.
    private unsafe ulong LocalContentId()
    {
        var lp = objectTable.LocalPlayer;
        if (lp == null) return 0;
        var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
        return ch == null ? 0 : ch->ContentId;
    }

    private async void DoStartAsync(string password, ulong contentId, uint entityId, string charName)
    {
        try
        {
            // Host: relay generates the opaque RoomId (we send it empty), CreateIfMissing=true, and stores the
            // password. The party joins by nearby-resolution + this password.
            var ok = await relay.Connect(config.RelayUrl, "", createIfMissing: true, password: password, nearbyContentIds: null, contentId, entityId, charName);
            RunOnMainThread(() =>
            {
                // Success ("Lobby open") now waits for the relay's RoomJoined (see OnRoomJoined) - connect-ok only
                // means the socket opened and JoinRoom was sent; a refusal (e.g. AlreadyHosting) still arrives after.
                if (!ok) chat.Print("[HMSync] Failed to connect to relay.");
            });
        }
        catch (Exception ex) { RunOnMainThread(() => chat.Print("[HMSync] Error: " + ex.Message)); }
    }

    private void DoJoin(string code)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null) { chat.Print("[HMSync] No local player."); return; }

        if (!packetFilter.HeartbeatResolved)
        {
            chat.Print("[HMSync] Cannot join: a game patch changed the heartbeat signature.");
            chat.Print("[HMSync] Joining now would disconnect you. This needs a plugin update to fix the signature.");
            return;
        }

        if (relay.IsConnected)
        {
            chat.Print("[HMSync] Leaving current session.");
            DoLeaveInternal(silent: true);
        }

        var password = (code ?? "").Trim();
        if (password.Length == 0) { chat.Print("[HMSync] Enter the room password to join."); return; }

        DoJoinAsync(password, LocalContentId(), localPlayer.EntityId, localPlayer.Name.TextValue);
    }

    private async void DoJoinAsync(string password, ulong contentId, uint entityId, string charName)
    {
        try
        {
            // Join: the relay resolves WHICH room from the ContentIds we can see (the presence gate) + the password -
            // no target, no picker. Send everyone visible; the relay disambiguates multiple candidate rooms by the
            // password. If nobody's visible there's no one to resolve against - say so before connecting.
            var nearby = EnumerateNearbyContentIds();
            if (nearby.Length == 0)
            {
                RunOnMainThread(() => chat.Print("[HMSync] Nobody nearby is hosting - you must be in visual range of the host to join."));
                return;
            }
            var ok = await relay.Connect(config.RelayUrl, "", createIfMissing: false, password: password, nearbyContentIds: nearby, contentId, entityId, charName);
            RunOnMainThread(() =>
            {
                // Success ("Joined") now waits for RoomJoined (see OnRoomJoined). Connect-ok is just socket+JoinRoom; a
                // wrong-password / no-host-nearby refusal arrives as an Error after this and must NOT read as "joined".
                if (!ok) chat.Print("[HMSync] Failed to reach the relay.");
            });
        }
        catch (Exception ex) { RunOnMainThread(() => chat.Print("[HMSync] Error: " + ex.Message)); }
    }

    // The ContentIds of every player character currently in our object table (visual range), sent on Join so the relay
    // can resolve which room we mean by intersecting with live session members - the presence gate. ContentId is read
    // natively (same as LocalContentId); our own is excluded. Everyone visible is included - the relay disambiguates
    // multiple candidate rooms by password, so we don't filter to one group.
    private unsafe ulong[] EnumerateNearbyContentIds()
    {
        var ids = new System.Collections.Generic.List<ulong>();
        ulong self = LocalContentId();
        foreach (var obj in objectTable)
        {
            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
            {
                var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)pc.Address;
                if (ch == null) continue;
                ulong cid = ch->ContentId;
                if (cid != 0 && cid != self) ids.Add(cid);
            }
        }
        return ids.ToArray();
    }

    // Quick-load from the Recent list: if idle, silently start a solo lobby first (so there's map authority), then
    // load; if already in a session, just load. The load itself goes synthetic (EngageSyntheticSession in DoLoad) -
    // the filter comes up before the zone changes, so nothing is exposed.
    // COSM_1_016: bridge for StateApplyService.SkillReplay - an unsafe method (not a lambda) because the delegate
    // takes a Character*, which can't be an implicitly-typed lambda parameter in this non-unsafe class.
    private unsafe void SkillReplayBridge(FFXIVClientStructs.FFXIV.Client.Game.Character.Character* caster,
        uint actionId, byte actionType, System.Numerics.Vector3 targetPos,
        FFXIVClientStructs.FFXIV.Client.Game.Character.Character* target)
        => skillSync.ReplayOn(caster, actionId, actionType, targetPos, target);

    private void DoQuickLoad(uint territoryId)
    {
        if (!relay.IsSessionActive) DoStartSolo();
        if (relay.IsSessionActive) DoLoad(territoryId);
    }

    // Engage the synthetic session - the packet filter plus everything that isolates you from the real server and
    // drives peers as puppets. This is phase two: it fires on ZONE-LOAD (host DoLoad, guest OnZoneLoadReceived), NOT
    // on host/join/solo start, so the lobby stays a normal-player gather where friends are real characters and Mare
    // can cache them. Idempotent - a second zone-load while already synthetic no-ops. Returns false (aborting the
    // load) if the heartbeat signature is unresolved, so the filter can never fail to come up while the zone changes.
    private unsafe bool EngageSyntheticSession()
    {
        if (packetFilter.IsActive) return true;   // already synthetic - don't re-engage on a subsequent load
        // v0.7.461 (P1, Codex QA): gate on CanEnable (all critical hooks created AND heartbeat resolved), not just
        // HeartbeatResolved. A patch that broke only the send-packet sig would pass the old heartbeat-only check and
        // engage the session with outbound traffic UNFILTERED to the live server. CanEnable closes that exposure.
        if (!packetFilter.CanEnable)
        {
            chat.Print("[HMSync] Cannot start the private session: a game patch changed a packet signature.");
            chat.Print("[HMSync] Loading now would expose you to the real server. This needs a plugin update to fix the signature.");
            return false;
        }
        ClearTargetsOnStart();   // S327f: prevent focus-target interaction softlock behind the filter
        packetFilter.Enable();
        sayFilter.Active = true;   // S328v: hide non-session /say + range-cull members
        PrepareSayPassthrough();   // S328p: version-check + configure opcodes + arm (fail-closed if unverified/patched)
        stateCapture.Start();
        stateApply.Start();
        detector.Reset();   // baselines from the LIVE actor (v0.7.413) - must precede the stand-up below

        // v0.7.419 - capture the origin posture BEFORE SanitiseLocalPosture clears it. If the player
        // entered seated, the server still thinks they're in InPositionLoop for the whole session. On
        // exit, we need to tell the server via a native standup emote. Capture here; act in post-settle.
        // v0.7.420: also capture the emote ID for origin restore (50=chair-sit, 52=ground-sit, 203=lean).
        {
            var lp = objectTable.LocalPlayer;
            if (lp != null)
            {
                var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
                originMode = ch->Mode;
                originModeParam = ch->ModeParam;
                originEmoteId = ch->EmoteController.EmoteId;
                log.Debug("[HMSync] [POSE] origin captured: Mode=" + originMode + "/" + originModeParam +
                    " EmoteId=" + originEmoteId);
            }
            else
            {
                originMode = CharacterModes.Normal;
                originModeParam = 0;
                originEmoteId = 0;
            }
        }

        SanitiseLocalPosture("engage");
        actorVisibility.Start();
        // Bridge peers that bound during the LOBBY (before actorVisibility was running) into its visible set.
        // actorVisibility.Start() hides all non-self players, and the OnPeerBound→RegisterPeer path only fires for
        // peers that bind via the apply loop (which wasn't running in the lobby) - so without this the co-located
        // peers stay hidden after we go synthetic. The apply loop re-registers them on their next transform (idempotent).
        foreach (var idx in stateApply.GetPeerObjectIndices())
            actorVisibility.RegisterPeer(idx);
        // (v0.7.261: dropped the "Private session active" line - redundant with "Solo/Lobby ready" + the zone-load line.)
        return true;
    }

    // v0.7.348: extract the short stage tag from a cutscene bg path - "ffxiv/ocn_o1/evt/o1e1/level/o1e1" → "o1e1"
    // (the last '/'-segment). Used to label a cutscene load by its stage tag instead of the donor territory id.
    private static string StageTagFromBg(string bg)
    {
        if (string.IsNullOrEmpty(bg)) return "";
        int i = bg.LastIndexOf('/');
        return i >= 0 && i < bg.Length - 1 ? bg.Substring(i + 1) : bg;
    }

    private void DoLoad(uint territoryId)
    {
        if (!relay.IsSessionActive) { chat.Print("[HMSync] Not in a session."); return; }
        if (!relay.HasMapAuthority) { chat.Print("[HMSync] Only the host can load zones."); return; }
        if (!zoneLoad.IsValidTerritory(territoryId)) { chat.Print("[HMSync] Invalid territory ID."); return; }

        // Hide NPCs is per-map - clear it on every load so a new map starts with its NPCs shown (flip it back on for
        // this map if you want them gone). Quest markers are left as-is.
        if (config.MapRemoveNpcs) { config.MapRemoveNpcs = false; config.Save(); DriveNpcVisibility(false, config.MapHideQuestSigns); }

        // v0.7.227: resolve the active swap-stage bg. LoadStage sets BOTH PendingStageBg and ActiveStageBg together for
        // a swap; a plain zone load sets neither. So PendingStageBg==null at entry means this is a plain load → clear
        // any stale ActiveStageBg from a prior swap. ActiveStageBg is otherwise PERSISTENT (survives the load) so a
        // later "Set spawn" capture keys by the same bg. When set, spawns key by STAGE BG (unique) instead of the
        // shared donor territoryId - the fix for the "custom spawn applies to every map" leak.
        if (zoneLoad.PendingStageBg == null)
            zoneLoad.ActiveStageBg = null;
        var stageBg = zoneLoad.ActiveStageBg;

        // v0.7.340: name the CUTSCENE stage, not the donor territory. For a swap stage the territoryId is the donor
        // (e.g. the apartment), so GetZoneName(territoryId) printed "Ingleside Apartment" instead of the stage. Prefer
        // the stage name when this is a swap load; fall back to the territory name for a plain zone load.
        var zoneName = (stageBg != null ? cutscene.GetStageName(stageBg) : null) ?? zoneLoad.GetZoneName(territoryId);

        // Spawn resolution. Order of preference:
        //   swap stage  : user-captured-for-this-bg  →  baked curated stage spawn  →  ResolveSpawnPoint(donor) fallback
        //   plain load  : user-captured-for-territory →  ResolveSpawnPoint(territoryId)   (unchanged S326m behaviour)
        // The user spawn stores facing at [3] (carried through so you spawn oriented the way you set it).
        System.Numerics.Vector3 spawn;
        float? spawnFacing = null;
        if (stageBg != null)
        {
            if (config.UserStageSpawns.TryGetValue(stageBg, out var uss) && uss.Length >= 3)
            {
                spawn = new System.Numerics.Vector3(uss[0], uss[1], uss[2]);
                if (uss.Length >= 4) spawnFacing = uss[3];
            }
            else if (cutscene.TryGetCuratedStageSpawn(stageBg, out var curatedStage, out var curatedFacing))
            {
                spawn = curatedStage;
                if (curatedFacing.HasValue) spawnFacing = curatedFacing;
            }
            else
            {
                spawn = zoneLoad.ResolveSpawnPoint(territoryId);
            }
        }
        else if (config.UserSpawns.TryGetValue(territoryId, out var us) && us.Length >= 3)
        {
            spawn = new System.Numerics.Vector3(us[0], us[1], us[2]);
            if (us.Length >= 4) spawnFacing = us[3];
        }
        else
            spawn = zoneLoad.ResolveSpawnPoint(territoryId);

        // v0.7.348: for a cutscene swap stage the parenthetical should read the STAGE tag (e.g. "o1e1"), not the donor
        // territory id (999). Derive the tag from the stage bg's last path segment; fall back to the territory id for a
        // plain zone load.
        string loadTag = stageBg != null ? StageTagFromBg(stageBg) : territoryId.ToString();
        chat.Print("[HMSync] Loading " + zoneName + " (" + loadTag + ").");

        // Go synthetic BEFORE the zone changes - the filter must be up before we leave the real map, or the server
        // sees us at the synthetic coordinates. Idempotent (no-op if already synthetic on a subsequent load); aborts
        // the load if the filter can't engage.
        if (!EngageSyntheticSession()) return;

        var peerIndices = stateApply.GetPeerObjectIndices();
        zoneLoad.LoadZone(territoryId, peerIndices, spawn, spawnFacing);
        actorVisibility.Refresh();
        config.PushRecentZone(territoryId);   // legacy list (kept for migration)
        // v0.7.231: unified recent - a swap cutscene stage records by its bg (stageBg), a plain zone by territoryId.
        // This is why "I just visited the Correction Chamber" now shows up in Recent instead of the donor zone.
        config.PushRecentPlace(territoryId, stageBg);

        // v0.7.475 (was v0.7.429, supersedes S326u): reset the WEATHER pick per load, exactly like BGM below.
        // The reset is still right but the REASON changed and the old wording is now false - PushMapState no
        // longer resolves a pick, it broadcasts the LIVE sky. Which is what makes the reset safe: on a fresh
        // load the live sky IS the new zone's native weather, so clearing the pick and reading reality agree.
        // (Under the old resolver the justification was "pick if legal, else native"; that ladder is gone.)
        // pick=0 is safe and correct:
        // it means "follow the new zone's own sky". Without this reset, a pick from map A (e.g. Snow) rode
        // into map B's broadcast; the host legality-gated it locally in Reassert (fell back to native - sunny),
        // but peers mirrored the illegal id verbatim → invalid render → the "peers stuck on none/atmospheric
        // while the host sees sunny skies" desync. Map load resets everyone to the zone default; subsequent
        // host picks sync as before.
        config.MapWeatherId = 0;    // pick does not carry across loads
        mapSettings.WeatherId = 0;  // service copy too, so post-load Reassert resolves the NEW zone's default

        ArmMapReassert();   // S326: re-apply host map-state once the load settles (load clobbers weather/time)
        ArmMapReveal();     // v0.7.448: reveal this map's HUD fog once the load settles (local; restored on exit)

        // S327l: reset BGM to the NEW zone's default on load - a pick from the previous map must not carry over (it
        // would show/play the wrong zone's track). The display reads live, but resetting the stored pick keeps the
        // picker/broadcast honest. lastAppliedPeerBgm reset so guests re-apply cleanly.
        config.MapBgmId = 0;   // 0 = follow the zone's natural/default music (no forced override)
        mapSettings.BgmId = 0; // S327v: reset the service copy too so post-load Reassert resolves the NEW zone default
        lastAppliedPeerBgm = 0;

        // v0.7.332: if this load is a cutscene stage (ActiveStageBg set by CutsceneStageService.LoadStage), carry the
        // stage bg path + name so peers run the same donor-load-with-bg-swap instead of loading only the donor territory.
        string bcastStageBg = zoneLoad.ActiveStageBg ?? "";
        string bcastStageName = "";
        if (bcastStageBg.Length > 0)
            foreach (var st in cutscene.Stages) if (st.Bg == bcastStageBg) { bcastStageName = st.Name; break; }

        _ = relay.SendZoneLoad(new ZoneLoadData
        {
            TerritoryId = territoryId,
            SpawnX = spawn.X, SpawnY = spawn.Y, SpawnZ = spawn.Z,
            StageBg = bcastStageBg,
            StageName = bcastStageName,
        });

        ui.SetStatus("Zone: " + zoneName);
        // Only mention peers when there actually are some (solo sessions have none - "broadcast sent to peers" was a lie).
        chat.Print(stateApply.Peers.Count > 0 ? "[HMSync] Loaded. Synced to peers." : "[HMSync] Loaded.");
    }

    private void DoTeardownHousing()
    {
        chat.Print("[HMSync] Attempting IndoorTerritory teardown (Dtor) - watch for furniture removal or crash.");
        zoneLoad.TeardownHousing();
    }


    private void DoReload()
    {
        if (!zoneLoad.IsZoneLoaded) { chat.Print("[HMSync] No zone loaded."); return; }
        var peerIndices = stateApply.GetPeerObjectIndices();
        zoneLoad.ReloadZone(peerIndices);
        actorVisibility.Refresh();
        chat.Print("[HMSync] Zone reloaded.");
    }

    private void DoLeave()
    {
        if (!relay.IsSessionActive) { chat.Print("[HMSync] Not in a session."); return; }
        DoLeaveInternal(silent: false);
    }

    private void DoLeaveInternal(bool silent)
    {
        // S289: AIRBORNE-STOP FIX. Do NOT revoke flight while still airborne over the foreign zone -
        // the moment IsFlightProhibited flips back to prohibited, the GAME force-relocates the airborne
        // actor to a "valid" position, which over flyable-but-not-walkable space (e.g. the Clyteum)
        // resolves to a point ~1000 yalms out. That was the air-stop-only OOB (ground stop never hit it
        // because there's no airborne actor to relocate). So we keep flight state intact through the
        // reload; Revert dismounts cleanly, and the deferred home-restore (S288) is the final authority
        // on position. Movement state is fully sanitized via noclip.Disable() BEFORE the reload (S292).
        // v0.7.328: snapshot peer origin positions before SanitizePeerStates clears the roster, so we can write them
        // back onto the frozen actors once the return settles (undoing the synthetic-coord freeze).
        retPeerOrigins = stateApply.SnapshotPeerOrigins();
        stateApply.SanitizePeerStates();
        sayFilter.Active = false;            // S328v: chat returns fully to normal once the session ends (stop/leave/crash all route here)

        // S328u - reset the host's per-session map overrides to neutral so nothing (experimental weather especially)
        // leaks into a LATER session. These are only ever SET by the map* handlers and were never reset, so a value
        // chosen in one session would persist in config and get re-broadcast to the next session's peers even after
        // the host's live state reset on zone load. Reset regardless of whether it was the weather-bug cause - a
        // set-but-never-reset value is a latent cross-session leak. Neutral defaults mirror the config initializers.
        config.MapWeatherId = 0;         // 0 = default/atmospheric
        config.MapTimeForced = false;
        config.MapEorzeaHour = 12;       // config default is noon, not 0
        config.MapEorzeaMinute = 0;
        config.MapBgmId = 0;             // 0 = none/default
        config.MapRemoveNpcs = false;
        config.MapHideQuestSigns = false;   // S328aa
        npcVisibility.Stop();               // S328aa: restore all NPCs on session end
        config.Save();
        packetFilter.PassSayChat = false;    // S328i: stop passing spatial-chat packets once the session ends
        packetFilter.PassSayChatOut = false; // S328k: stop passing outbound chat once the session ends
        relay.SoloMode = false;              // S328f: clear solo flag on any teardown (stop/leave/crash all route here)
        mapSettings.DisableTimeOverride();   // S326v: never leave the player's clock frozen after a session ends
        mapSettings.RestoreBgm();            // S327s: release our forced BGM so the real zone's music resumes (not a stuck synthetic track)
        config.MapBgmId = 0;                 // S327v: clear the stored pick so a re-host starts from the zone default, not a stale custom track
        lastAppliedPeerBgm = 0;              // S327s: clear the latch so a re-join re-engages BGM cleanly

        stateCapture.Stop();
        stateApply.Stop();
        detector.Stop();
        actorVisibility.Stop();

        bool announceMovement = noclip.IsActive;

        // S292: clear ALL movement state (flight, noclip, flat) BEFORE Revert. The flying mount's
        // movement controller writes the actor position EVERY frame while flight is active - it was
        // overwriting our restore SetPosition each tick (drift frozen at 72.4 for 30 frames). Flight
        // MUST be off before the restore poll runs, or the restore can never land. The S289 concern
        // (revoking flight while airborne triggers the game's relocate) is moot here: we reload the zone
        // immediately, and the held packet filter + deferred restore own the landing.
        noclip.Disable();
        // v0.7.259: no notification on auto-disable - movement modes dropping on leave is expected, not news.

        // S320: carpet is a session/map convenience - drop it on stop|leave too (notifies via its own
        // StatusReport; idempotent, so the ZoneWillChange fired by Revert's reload won't double-notify).
        carpet.Disable();

        // v0.7.419 - POSTURE SANITISE ON EXIT. Same pattern as the mount/minion/ornament teardown:
        // clear any posture the player is in (whether inherited from origin or acquired during the
        // session) so it doesn't leak through Revert. Without this, a chair-sit acquired during HMS
        // persists → actor at origin in InPositionLoop with no furniture anchor → sunk into floor,
        // MoveController locked by mode → frozen for peers.
        // Covers both InPositionLoop (sit/groundsit/sleep) and EmoteLoop (lean/dance/cheers).
        SanitiseLocalPosture("exit");

        // v0.7.448 - MAP-REVEAL SANITISE ON EXIT. Restore every map we auto-revealed this session to its
        // recorded original discovery bytes (restore-to-snapshot, never to zero - genuine progress is
        // preserved), and delete the crash-recovery file. All stop/leave paths funnel here, so a clean exit
        // always sanitises; only a hard crash bypasses it, which the on-load crash-recovery sweep handles.
        SanitiseRevealedMaps();

        // v0.7.419 - release any locked gaze. DriveGazeSlot's release path (LookMode=0) fires in the
        // per-frame loop, but Stop() killed that loop above - so a Locked gaze that was on at Stop()
        // never gets its release call. ClearAll resets the static flags so the next session starts clean
        // and the zone reload's DrawObject rebuild doesn't inherit a stale look-at target.
        FaceControlState.ClearAll();

        if (zoneLoad.IsZoneLoaded) zoneLoad.Revert();

        // S301: filter must end OFF after any stop/leave, via exactly one owner: the deferred restore poll
        // (OnHomeRestoreComplete → Disable once settled) OR an inline disable here. Skip inline ONLY when
        // the poll is armed and will own it. Idempotent (IsActive-gated), safe from any entry path.
        if (packetFilter.IsActive && !zoneLoad.HomeRestoreArmed)
        {
            packetFilter.Disable();
            if (!silent) chat.Print("[HMSync] Packet filter OFF.");
        }

        _ = relay.Disconnect();

        if (!silent)
        {
            ui.SetStatus("Disconnected");
            chat.Print("[HMSync] Left session.");
        }
    }

    // /hms stop - slash-command ONLY (never wired to the GUI), so it's always a deliberate keystroke. For the HOST
    // this now TERMINATES the session for EVERYONE: it emits the SessionEnd control frame (every peer receives it and
    // runs DoLeaveInternal → "Host ended the session"), then tears down locally. For a non-host (or solo) it's just a
    // leave - same as the GUI button and /hms leave. The GUI's leave/stop button stays DoLeave (you leave, session
    // lives on; if you were host, relay auto-transfers to the next peer). This gives the host a deliberate, explicit
    // "end it for all" lever that a stray button-tap can't trigger.
    private void DoStop()
    {
        if (!relay.IsSessionActive) { chat.Print("[HMSync] Not in a session."); return; }
        if (relay.IsConnected && relay.IsHost)
        {
            chat.Print("[HMSync] Ending the session for everyone…");
            // Send SessionEnd and let it flush BEFORE local teardown - DoLeaveInternal calls Disconnect(), which
            // closes the socket, so a fire-and-forget SessionEnd could be dropped before peers receive it. Chain the
            // teardown onto the send's completion (back on the main thread). Bounded: SendSessionEnd is best-effort.
            _ = relay.SendSessionEnd().ContinueWith(_ => RunOnMainThread(() => DoLeaveInternal(silent: false)));
            return;
        }
        DoLeaveInternal(silent: false);   // non-host / solo → plain leave (same as the GUI button and /hms leave)
    }

    // Movement modes (fly / noclip / carpet) may only be ENABLED with a zone loaded (except in debug) - flying or
    // noclipping on an un-loaded real map is the open anti-cheat exposure. Disabling is always allowed. Gated at the
    // command layer so both the /hms commands and the UI pills (which route through it) hit the same gate.
    // v0.7.262: movement (fly/noclip/carpet) is gated on an ACTIVE SESSION or debug - the packet filter that a session
    // brings up is what makes movement safe; a merely-loaded zone outside a session is real-server exposure. Previously
    // this was IsZoneLoaded||debug, and because each button re-implemented its own check, the carpet button added in the
    // redesign inherited the weaker zone-loaded gate. Now there's ONE capability gate and every button routes through it.
    private bool MovementEnableAllowed() => relay.IsSessionActive || config.ShowDebugCommands;

    // v0.7.445: fly / noclip / carpet are all movement affordances that, on the LIVE real zone, amount
    // to a teleport-to-target cheat (start any session - solo, host, or joined - and before loading a
    // map you're standing on the real world; toggling movement then stopping lands you at the targeted
    // position; carpet is the same, just slower). So all three gate together on whether HMS has actually
    // loaded an environment. IsZoneLoaded is true for a loaded map AND for a cutscene swap stage (both
    // go through the LoadZone path - a cutscene is an HMS environment via a donor territory), so
    // cutscenes get movement freely, exactly like a loaded map. When NOT on an HMS environment (i.e. the
    // live zone), movement is allowed only under the second-tier research mode (/hms debug, itself only
    // reachable with the Config debug checkbox on) - so leaving the checkbox on isn't enough to stumble
    // onto a movement cheat. Replaces the old "in a session OR debug checkbox" gate, which leaked because
    // any session (including solo) satisfied it while still on the live map.
    private bool MovementResearchAllowed() => zoneLoad.IsZoneLoaded || zoneLoad.ResearchMode;

    private void DoToggleFly()
    {
        // v0.7.445: allowed on an HMS-loaded environment (map or cutscene) or under research mode -
        // never on the bare live zone. Mirrors MovementResearchAllowed so command, button, and inner
        // toggle all agree.
        if (!MovementResearchAllowed())
        {
            chat.Print("[HMSync] Flight is only available on a loaded map or cutscene (or research mode).");
            return;
        }
        noclip.ToggleFlight();
        chat.Print("[HMSync] Flight " + (noclip.FlightActive ? "ON - jump to fly" : "OFF"));
    }

    private void DoToggleNoclip()
    {
        // v0.7.445: allowed on an HMS-loaded environment (map or cutscene) or under research mode.
        if (!MovementResearchAllowed())
        {
            chat.Print("[HMSync] Noclip is only available on a loaded map or cutscene (or research mode).");
            return;
        }
        noclip.ToggleNoclip();
        if (noclip.NoclipActive)
            chat.Print("[HMSync] Noclip ON.");
        else
            chat.Print("[HMSync] Noclip OFF");
    }

    // /hms pktcap [opcodes] - toggle inbound-packet capture. With no arg, toggles logging ALL inbound packets (firehose;
    // use in an inn). With a comma-list (e.g. "103,356"), logs ONLY those opcodes. Logs (opcode, timestamp, payload hex)
    // to the plugin log; packets are still dropped (behavior unchanged). Lets us learn what specific opcodes carry.
    // /hms senddiag - toggle the OUTBOUND opcode diagnostic. Logs every outbound opcode + pass/suppress. Reveals the
    // /say outbound opcode (ChatHandler?) and confirms whether the sender's chat is being dropped before it leaves.
    // /hms sayfind <text> - the content-correlation say-opcode finder. The standalone command is retired (S328ad), but
    // this method is KEPT because the Config-tab re-learn auto-capture calls it (RelearnSayOpcodes → DoSayFind("RELEARN")).
    private void DoSayFind(string? arg)
    {
        // Re-learn mode (from the Config tab): arm the finder with a generated marker and, on a hit, update+verify the
        // inbound opcode in config automatically. Wire the one-time callback here.
        bool relearn = arg == "RELEARN";
        if (relearn)
        {
            // Symmetric re-learn: BOTH opcodes can rotate on a patch, and they're found on different hooks by
            // different observers. Outbound (your submission) is captured when YOU say the marker. Inbound (delivery)
            // is captured when a CO-LOCATED partner says the marker - your own say is local echo, never inbound.
            var marker = "HMSRELEARN" + Environment.TickCount % 10000;
            relearnGotOut = false;
            relearnGotIn = false;

            packetFilter.RelearnArmed = true;
            // Outbound capture (you say the marker):
            packetFilter.SayFinderTextOut = marker;
            packetFilter.OnSayOutOpcodeFound = found => RunOnMainThread(() =>
            {
                config.SayOutboundOpcode = found;
                config.Save();
                relearnGotOut = true;
                chat.Print("[HMSync] Re-learn: OUTBOUND /say opcode = " + found + " (0x" + found.ToString("X3") + ") captured.");
                RelearnMaybeFinish();
            });
            // Inbound capture (a co-located partner says the marker):
            packetFilter.SayFinderText = marker;
            packetFilter.OnSayOpcodeFound = found => RunOnMainThread(() =>
            {
                config.SayInboundOpcode = found;
                config.Save();
                relearnGotIn = true;
                chat.Print("[HMSync] Re-learn: INBOUND /say opcode = " + found + " (0x" + found.ToString("X3") + ") captured.");
                RelearnMaybeFinish();
            });

            packetFilter.EnableCaptureOnly();
            chat.Print("[HMSync] Re-learn armed. Out of session (filter off):");
            chat.Print("[HMSync]   1. YOU /say this exactly: " + marker + "   → captures the OUTBOUND opcode");
            chat.Print("[HMSync]   2. A co-located friend /says the same → captures the INBOUND opcode");
            chat.Print("[HMSync] (Inbound needs someone else - your own /say is local echo, never received.) Both verify automatically.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            packetFilter.SayFinderText = null;
            packetFilter.SayFinderTextOut = null;
            packetFilter.DisableCaptureOnly();
            chat.Print("[HMSync] Say-finder OFF. (Usage: /hms sayfind <text>, then /say that text out of session.)");
            return;
        }
        packetFilter.SayFinderText = arg.Trim();
        packetFilter.EnableCaptureOnly();   // ensure the receive hook is live to scan inbound
        chat.Print("[HMSync] Say-finder ARMED for '" + arg.Trim() + "'. Now /say " + arg.Trim() + " (out of session). Watch the log for [SAY-FINDER].");
    }

    // S328q - called after each re-learn capture. When BOTH opcodes are captured, verify + re-arm the passthrough.
    // If only one is in so far, report what's still needed (the two captures may arrive seconds apart, or the inbound
    // may need a partner). Verifying on outbound-only is allowed as a partial win, but flagged.
    private void RelearnMaybeFinish()
    {
        if (relearnGotOut && relearnGotIn)
        {
            // F2: don't stamp/verify against an empty version. GetGameVersion() can return "" (CS not ready), and a
            // verified state with a blank stamp is exactly what the PrepareSayPassthrough empty-stamp guard fails
            // closed on next start. Keep the captured opcodes but refuse to verify; ask the user to retry so we
            // stamp a real version. The re-learn stays armed so a moment later (CS ready) it can complete.
            string learnedVersion = GetGameVersion();
            if (string.IsNullOrEmpty(learnedVersion))
            {
                log.Warning("[HMSync] Re-learn captured both opcodes but the live game version could not be read; " +
                    "not verifying to avoid an empty stamp.");
                chat.PrintError("[HMSync] Both /say opcodes were captured, but the game version couldn't be read just now, " +
                    "so they weren't confirmed. Please run Re-learn again in a moment.");
                relearnGotOut = false;
                relearnGotIn = false;
                return;
            }
            packetFilter.RelearnArmed = false;
            packetFilter.OnSayOpcodeFound = null;
            packetFilter.OnSayOutOpcodeFound = null;
            config.SayOpcodesVerified = true;
            config.SayOpcodesGameVersion = learnedVersion;
            config.Save();
            sayDriftBanner = false;
            packetFilter.ConfigureSayOpcodes(config.SayOutboundOpcode, config.SayInboundOpcode);
            if (packetFilter.IsActive) { packetFilter.PassSayChat = true; packetFilter.PassSayChatOut = true; }
            chat.Print("[HMSync] Re-learn complete - both opcodes captured and verified for game version " + config.SayOpcodesGameVersion +
                " (outbound " + config.SayOutboundOpcode + ", inbound " + config.SayInboundOpcode + ").");
        }
        else if (relearnGotOut)
        {
            chat.Print("[HMSync] Outbound captured. Still need INBOUND - have a co-located friend /say the same marker.");
        }
        else if (relearnGotIn)
        {
            chat.Print("[HMSync] Inbound captured. Still need OUTBOUND - /say the marker yourself.");
        }
    }

    // /hms saydiag - toggle the chat diagnostic. Logs every chat message's kind/sender/text via IChatGui (the display
    // layer), to learn where /say actually arrives and whether the firewall drops it. (S328h: repointed from the wrong
    // HandleSocialPacket hook - that's the friends-list handler, not chat.)
    // S328ab: map-reveal investigation. Logs the AgentMap current/selected view + the MapDiscoveryManager persistent
    // discovery state for the current map. Run it (a) standing in a real, visited zone, and (b) inside a synthetic HMS
    // session on an unvisited map - compare. This reveals whether the big-map "blank+blinking" gate reads the TRANSIENT
    // agent fields (safe to drive) or the PERSISTENT discovery bitmap (contamination risk - must never write). No writes.
    // S328ag: relay bandwidth diagnostic. Shows live in/out rates, session totals, and the per-message-type
    // breakdown so we can see whether transforms dominate (they will). Sub: "/hms netdiag reset" zeroes the counters
    // to start a clean measurement window. (The old "dirty on|off" A/B toggle was removed at release hardening -
    // change-detection is always on now.)
    private void DoNetDiag(string? arg)
    {
        var a = (arg ?? "").Trim().ToLowerInvariant();
        if (a == "reset") { netStats.Reset(); chat.Print("[HMSync] Net stats reset - measurement window restarted."); return; }

        var (outBps, inBps) = netStats.LiveRates();
        var el = netStats.Elapsed.TotalSeconds;
        double avgOut = el > 0 ? netStats.TotalBytesOut / el : 0;
        double avgIn = el > 0 ? netStats.TotalBytesIn / el : 0;
        chat.Print("[HMSync] === Net diag ===");
        chat.Print(string.Format("[HMSync] LIVE:  out {0:F1} KB/s ({1:F0} kbps) | in {2:F1} KB/s ({3:F0} kbps)",
            outBps / 1024, outBps * 8 / 1000, inBps / 1024, inBps * 8 / 1000));
        chat.Print(string.Format("[HMSync] AVG:   out {0:F1} KB/s | in {1:F1} KB/s  over {2:F0}s",
            avgOut / 1024, avgIn / 1024, el));
        chat.Print(string.Format("[HMSync] TOTAL: out {0:F2} MB ({1} msgs) | in {2:F2} MB ({3} msgs)",
            netStats.TotalBytesOut / 1048576.0, netStats.TotalMsgsOut, netStats.TotalBytesIn / 1048576.0, netStats.TotalMsgsIn));
        // Per-type outbound breakdown (which channel dominates).
        foreach (var kvp in netStats.BytesOutByType)
        {
            netStats.MsgsOutByType.TryGetValue(kvp.Key, out var mc);
            double avgMsg = mc > 0 ? (double)kvp.Value / mc : 0;
            chat.Print(string.Format("[HMSync]   {0}: {1:F2} MB, {2} msgs, {3:F0} B/msg", kvp.Key, kvp.Value / 1048576.0, mc, avgMsg));
        }
    }

    private unsafe void DoMapDiag()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            if (agent == null) { chat.Print("[HMSync] [MAPDIAG] AgentMap null."); return; }
            log.Information("[HMSync] [MAPDIAG] === AgentMap ===");
            log.Information("[HMSync] [MAPDIAG] CurrentTerritoryId=" + agent->CurrentTerritoryId
                + " CurrentMapId=" + agent->CurrentMapId
                + " CurrentMapDiscoveryFlag=0x" + agent->CurrentMapDiscoveryFlag.ToString("X"));
            log.Information("[HMSync] [MAPDIAG] SelectedTerritoryId=" + agent->SelectedTerritoryId
                + " SelectedMapId=" + agent->SelectedMapId
                + " SelectedMapDiscoveryFlag=0x" + agent->SelectedMapDiscoveryFlag.ToString("X"));

            var disc = FFXIVClientStructs.FFXIV.Client.Game.MapDiscoveryManager.Instance();
            if (disc == null) { chat.Print("[HMSync] [MAPDIAG] MapDiscoveryManager null (see log for AgentMap)."); return; }
            uint mapId = agent->CurrentMapId;
            log.Information("[HMSync] [MAPDIAG] === MapDiscoveryManager (map " + mapId + ") ===");
            log.Information("[HMSync] [MAPDIAG] IsDiscoveryEnabledForMap(" + mapId + ")=" + disc->IsDiscoveryEnabledForMap(mapId));
            // Probe the first several region indices - the persistent per-region reveal bits. All-false on an unvisited
            // map; some-true once explored. If the HUD shows the map while these stay false, the gate is TRANSIENT (safe).
            var sb = new System.Text.StringBuilder("[HMSync] [MAPDIAG] IsMapRegionDiscovered region0..15: ");
            for (byte r = 0; r < 16; r++) sb.Append(disc->IsMapRegionDiscovered(mapId, r) ? '1' : '0');
            log.Information(sb.ToString());

            // v0.7.447: also dump the RAW discovery-table bytes for this map, resolved by DiscoveryIndex +
            // DiscoveryArrayByte from the Map sheet. This confirms our indexing before any write: the raw
            // bytes should agree with IsMapRegionDiscovered above (1 where discovered). Layout (from CS):
            //   16-region table: base + 0x000, stride 0x10, DiscoveryIndex-indexed, 16 bytes of bool
            //   32-region table: base + 0xA20, stride 0x20, DiscoveryIndex-indexed, 32 bytes of bool
            var dr = ResolveDiscoveryTable(mapId, out var tablePtr, out int regionCount, out int discoveryIndex, out bool use16);
            if (dr == DiscResolve.Ok)
            {
                var raw = new System.Text.StringBuilder();
                for (int i = 0; i < regionCount; i++) raw.Append(((byte*)tablePtr)[i] != 0 ? '1' : '0');
                log.Information("[HMSync] [MAPDIAG] RAW table (DiscoveryIndex=" + discoveryIndex + " use16=" + use16
                    + " regions=" + regionCount + " @0x" + ((nint)tablePtr).ToString("X") + "): " + raw);
            }
            else
            {
                log.Information("[HMSync] [MAPDIAG] RAW table: " + DescribeDiscResolve(dr, discoveryIndex, use16) + ".");
            }
            chat.Print("[HMSync] [MAPDIAG] logged AgentMap + discovery state for map " + mapId + ". See the log. Run in a real zone AND in a private session to compare.");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [MAPDIAG] failed: " + ex);
            chat.Print("[HMSync] [MAPDIAG] error - see log.");
        }
    }

    // Result of ResolveDiscoveryTable. Was a bool, which collapsed five distinct exits into one caller message
    // ("DiscoveryIndex -1") - misleading when the real cause is a STALE BUILD (the game's discovery array grew
    // past our measured bound after a patch). IndexOutOfRange is the case worth naming: reveal would otherwise
    // write nowhere and the fog would silently stay. See DescribeDiscResolve + docs/mapdiscovery-remeasure.md.
    private enum DiscResolve
    {
        Ok,              // resolved: tablePtr / regionCount / discoveryIndex / use16 are valid
        ManagerNull,     // MapDiscoveryManager isn't live yet
        SheetNull,       // the Map sheet is unavailable
        RowMissing,      // mapId isn't in the Map sheet
        NoTable,         // DiscoveryIndex == -1: this map legitimately has no fog table
        IndexOutOfRange, // DiscoveryIndex >= this build's measured array bound: STALE BUILD, re-measure
    }

    // MapDiscoveryManager array bounds, MEASURED from ffxiv_dx11.exe (recipe: docs/mapdiscovery-remeasure.md).
    // These are NOT taken from FFXIVClientStructs: as of 7.55 CS still declares Size 0x1024 / FixedSizeArray48,
    // stale for the current game. Ground truth is the disassembly of MapDiscoveryManager.IsRegionDiscovered.
    //   _mapsWithUpTo16Regions @0x000, stride 0x10, each = 16 bool
    //   _mapsWithUpTo32Regions @0xA20, stride 0x20, each = 32 bool
    private const int Disc16Base = 0x000;
    private const int Disc16Stride = 0x10;
    private const int Disc16Count = 162;    // 0xA2 (mov eax, imm32) in 7.55; the Map sheet's max 16-region DiscoveryIndex is 159
    private const int Disc32Base = 0xA20;   // 0x51 * 0x20
    private const int Disc32Stride = 0x20;
    private const int Disc32Count = 49;     // 0x31 (cmp dx, imm8) in 7.55; was 48 - The North Horn (map 1346) uses DiscoveryIndex 48, the 49th slot

    // v0.7.447: resolve the raw discovery-table pointer + region count for a map, from the Map sheet's
    // DiscoveryIndex (which row in the table) and DiscoveryArrayByte (16- vs 32-region table). Raw byte access
    // (not the generated FixedSizeArray accessor) per the "prefer offset reads for unproven bindings" rule - the
    // offsets are measured and stable. Returns a DiscResolve describing success or the specific failure class.
    private unsafe DiscResolve ResolveDiscoveryTable(uint mapId, out void* tablePtr, out int regionCount, out int discoveryIndex, out bool use16)
    {
        tablePtr = null; regionCount = 0; discoveryIndex = -1; use16 = true;
        var disc = FFXIVClientStructs.FFXIV.Client.Game.MapDiscoveryManager.Instance();
        if (disc == null) return DiscResolve.ManagerNull;
        var mapSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        if (mapSheet == null) return DiscResolve.SheetNull;
        var row = mapSheet.GetRowOrDefault(mapId);
        if (row == null) return DiscResolve.RowMissing;
        discoveryIndex = row.Value.DiscoveryIndex;
        if (discoveryIndex < 0) return DiscResolve.NoTable;   // map legitimately has no discovery table
        use16 = row.Value.DiscoveryArrayByte;
        byte* baseP = (byte*)disc;
        if (use16)
        {
            if (discoveryIndex >= Disc16Count) return DiscResolve.IndexOutOfRange;
            tablePtr = baseP + Disc16Base + (discoveryIndex * Disc16Stride);
            regionCount = 16;
        }
        else
        {
            if (discoveryIndex >= Disc32Count) return DiscResolve.IndexOutOfRange;
            tablePtr = baseP + Disc32Base + (discoveryIndex * Disc32Stride);
            regionCount = 32;
        }
        return DiscResolve.Ok;
    }

    // Human-readable reason for a non-Ok ResolveDiscoveryTable result, for a log/chat line. IndexOutOfRange is the
    // load-bearing one: it means a game patch grew the discovery array past this build's measured bound, so a
    // reveal write would land nowhere and the fog would silently persist - we say so and point at the fix.
    private static string DescribeDiscResolve(DiscResolve r, int discoveryIndex, bool use16)
    {
        switch (r)
        {
            case DiscResolve.ManagerNull: return "the map-discovery manager isn't live yet";
            case DiscResolve.SheetNull:   return "the Map sheet is unavailable";
            case DiscResolve.RowMissing:  return "the map isn't in the Map sheet";
            case DiscResolve.NoTable:     return "it has no discovery table (DiscoveryIndex -1)";
            case DiscResolve.IndexOutOfRange:
                return "it uses DiscoveryIndex " + discoveryIndex + ", but this build measured the "
                    + (use16 ? "16" : "32") + "-region array as holding only " + (use16 ? Disc16Count : Disc32Count)
                    + " entries - this build is stale for the current game version; re-measure MapDiscoveryManager (docs/mapdiscovery-remeasure.md)";
            default: return "resolved";
        }
    }

    // v0.7.447: snapshot of the current map's discovery table, captured by DoMapReveal so DoMapRestore
    // (and, later, an automatic sanitise-on-exit) can write it back byte-for-byte. Null = nothing captured.
    private byte[]? mapRevealSnapshot;
    private uint mapRevealSnapshotMapId;

    // v0.7.447: STEP-2 TEST. Snapshot the current map's raw discovery table, then set every region byte to
    // 1 (discovered) and log before/after. You then eyeball whether the HUD map reveals. Undo with
    // /hms maprestore. Research-mode gated (writes memory). The persistence question (does this survive a
    // relog?) is answered by NOT restoring and relogging - see the test plan.
    private unsafe void DoMapReveal()
    {
        if (!zoneLoad.ResearchMode)
        {
            chat.Print("[HMSync] /hms mapreveal is not available.");
            return;
        }
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            if (agent == null) { chat.Print("[HMSync] [MAPREVEAL] AgentMap null."); return; }
            uint mapId = agent->CurrentMapId;
            var dr = ResolveDiscoveryTable(mapId, out var tablePtr, out int regionCount, out int discoveryIndex, out bool use16);
            if (dr != DiscResolve.Ok)
            {
                chat.Print("[HMSync] [MAPREVEAL] map " + mapId + " not revealed: " + DescribeDiscResolve(dr, discoveryIndex, use16) + ".");
                return;
            }
            // Snapshot BEFORE writing.
            var snap = new byte[regionCount];
            for (int i = 0; i < regionCount; i++) snap[i] = ((byte*)tablePtr)[i];
            mapRevealSnapshot = snap;
            mapRevealSnapshotMapId = mapId;

            var before = new System.Text.StringBuilder();
            for (int i = 0; i < regionCount; i++) before.Append(snap[i] != 0 ? '1' : '0');

            // Reveal: set every region byte to 1.
            for (int i = 0; i < regionCount; i++) ((byte*)tablePtr)[i] = 1;

            var after = new System.Text.StringBuilder();
            for (int i = 0; i < regionCount; i++) after.Append(((byte*)tablePtr)[i] != 0 ? '1' : '0');

            // v0.7.479: post-write confirmation. We wrote through a pointer computed from measured constants; this asks
            // the GAME (via its own mapId->slot resolution) whether a region we just set now reads as discovered. It
            // prevents nothing and gates nothing - if the base offset ever drifts, reveal would write to the wrong slot
            // and the fog would simply stay with no error, and this line is the difference between "wrong address" and
            // "render didn't refresh" without an investigation.
            // v0.7.480: probe region 1, not region 0. DiscoveryFlag bit 0 is clear on every one of the 501 discovery
            // maps in the 7.55 sheet - region numbering starts at 1 (North Horn 0x1FFFFFFE, South Horn 0x7FFFFFFE), so
            // slot 0 is dead space no map owns. It only reads back today because the write loop above is unconditional;
            // if that ever masks to DiscoveryFlag, a region-0 probe would cry "UNDISCOVERED" on every successful reveal.
            // Bit 1 is the lowest set region on every discovery map, so it is always written and always a valid slot.
            const byte probeRegion = 1;
            bool agrees = false;
            try { var d = FFXIVClientStructs.FFXIV.Client.Game.MapDiscoveryManager.Instance(); if (d != null) agrees = d->IsMapRegionDiscovered(mapId, probeRegion); } catch { }
            log.Information("[HMSync] [MAPREVEAL] game reads region " + probeRegion + " as "
                + (agrees ? "discovered - address confirmed" : "UNDISCOVERED - our address may not be the game's slot"));

            log.Information("[HMSync] [MAPREVEAL] map=" + mapId + " DiscoveryIndex=" + discoveryIndex + " use16=" + use16
                + " regions=" + regionCount + " @0x" + ((nint)tablePtr).ToString("X"));
            log.Information("[HMSync] [MAPREVEAL] before: " + before);
            log.Information("[HMSync] [MAPREVEAL] after:  " + after);
            chat.Print("[HMSync] [MAPREVEAL] set " + regionCount + " regions on map " + mapId
                + ". Check the HUD map. /hms maprestore to undo. (Snapshot saved.)");
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [MAPREVEAL] failed: " + ex);
            chat.Print("[HMSync] [MAPREVEAL] error - see log.");
        }
    }

    // v0.7.447: STEP-2 TEST. Write the snapshot captured by DoMapReveal back to the same map's table,
    // restoring the original fog state. Confirms the reveal→restore round trip is clean before we wire
    // any automatic sanitise-on-exit.
    private unsafe void DoMapRestore()
    {
        if (mapRevealSnapshot == null)
        {
            chat.Print("[HMSync] [MAPRESTORE] no snapshot - run /hms mapreveal first.");
            return;
        }
        try
        {
            if (ResolveDiscoveryTable(mapRevealSnapshotMapId, out var tablePtr, out int regionCount, out _, out _) != DiscResolve.Ok)
            {
                chat.Print("[HMSync] [MAPRESTORE] could not resolve the table for the snapshotted map " + mapRevealSnapshotMapId + ".");
                return;
            }
            int n = Math.Min(regionCount, mapRevealSnapshot.Length);
            for (int i = 0; i < n; i++) ((byte*)tablePtr)[i] = mapRevealSnapshot[i];

            var restored = new System.Text.StringBuilder();
            for (int i = 0; i < regionCount; i++) restored.Append(((byte*)tablePtr)[i] != 0 ? '1' : '0');
            log.Information("[HMSync] [MAPRESTORE] map=" + mapRevealSnapshotMapId + " restored: " + restored);
            chat.Print("[HMSync] [MAPRESTORE] restored " + n + " regions on map " + mapRevealSnapshotMapId + ". Check the HUD map re-fogged.");
            mapRevealSnapshot = null;
        }
        catch (Exception ex)
        {
            log.Error("[HMSync] [MAPRESTORE] failed: " + ex);
            chat.Print("[HMSync] [MAPRESTORE] error - see log.");
        }
    }

    // ============================================================================================
    // v0.7.448: AUTOMATIC MAP REVEAL (session-scoped, with crash recovery).
    //
    // On an HMS map load we reveal that map's HUD fog, recording its ORIGINAL discovery bytes first.
    // On session end (DoLeaveInternal - the stop/leave/crash-cleanup chokepoint) we write those originals
    // back, so nothing artificial persists past the session. Because a hard game CRASH bypasses that
    // chokepoint, the same original bytes are mirrored to a small JSON file on each reveal; on the next
    // plugin load we replay any pending restores (deferred until MapDiscoveryManager is live), cleaning up
    // a crashed session's leaked reveals.
    //
    // SAFETY: we snapshot-before-reveal and restore-to-SNAPSHOT, never restore-to-zero. A map the player
    // genuinely explored either is never revealed by us (so untouched) or, if revealed, its snapshot
    // already holds the real bits - so restore preserves genuine progress. First-snapshot-wins: re-revealing
    // a map already recorded this session does NOT overwrite its original snapshot with revealed bytes.
    // ============================================================================================

    private readonly Dictionary<uint, byte[]> revealSnapshots = new();   // mapId -> original bytes, this session
    private string MapRevealPendingPath => System.IO.Path.Combine(pluginInterface.ConfigDirectory.FullName, "map-reveal-pending.json");

    // Reveal the given map (default: the current map), recording its original bytes first. Idempotent per
    // map per session. Persists the pending record to disk so a crash can be recovered on next load.
    private unsafe void AutoRevealMap(uint mapId)
    {
        try
        {
            if (mapId == 0) return;
            var dr = ResolveDiscoveryTable(mapId, out var tablePtr, out int regionCount, out int discoveryIndex, out bool use16);
            if (dr != DiscResolve.Ok)
            {
                // The stale-build case (the game's discovery array outgrew our measured bound) is the one worth
                // shouting about: auto-reveal would otherwise fail silently and the fog would just stay. Everything
                // else (no table / manager not live yet) is an ordinary "nothing to do".
                if (dr == DiscResolve.IndexOutOfRange)
                    log.Warning("[HMSync] [MAPREVEAL] auto-reveal SKIPPED for map " + mapId + ": " + DescribeDiscResolve(dr, discoveryIndex, use16));
                return;
            }

            // First-snapshot-wins: only record the original if we haven't already touched this map.
            if (!revealSnapshots.ContainsKey(mapId))
            {
                var snap = new byte[regionCount];
                bool anyUnrevealed = false;
                for (int i = 0; i < regionCount; i++) { snap[i] = ((byte*)tablePtr)[i]; if (snap[i] == 0) anyUnrevealed = true; }
                // If the map is already fully discovered, there's nothing to reveal and nothing to clean up
                // later - skip recording it so we don't carry no-op entries (and never risk a needless write).
                if (!anyUnrevealed) return;
                revealSnapshots[mapId] = snap;
                PersistPendingReveals();
            }

            // Reveal: set every region byte to 1.
            for (int i = 0; i < regionCount; i++) ((byte*)tablePtr)[i] = 1;
            if (zoneLoad.ResearchMode) log.Information("[HMSync] [MAPREVEAL] auto-revealed map " + mapId + " (" + regionCount + " regions).");
        }
        catch (Exception ex) { log.Warning("[HMSync] [MAPREVEAL] auto-reveal failed for map " + mapId + ": " + ex.Message); }
    }

    // Session-end sanitise: restore every revealed map to its recorded original bytes, clear the record,
    // and delete the disk file. Called from DoLeaveInternal (stop/leave). Safe to call with nothing pending.
    private unsafe void SanitiseRevealedMaps()
    {
        if (revealSnapshots.Count == 0) { DeletePendingFile(); return; }
        int restored = 0;
        foreach (var kv in revealSnapshots)
        {
            try
            {
                if (ResolveDiscoveryTable(kv.Key, out var tablePtr, out int regionCount, out _, out _) != DiscResolve.Ok) continue;
                int n = Math.Min(regionCount, kv.Value.Length);
                for (int i = 0; i < n; i++) ((byte*)tablePtr)[i] = kv.Value[i];
                restored++;
            }
            catch (Exception ex) { log.Warning("[HMSync] [MAPREVEAL] sanitise failed for map " + kv.Key + ": " + ex.Message); }
        }
        if (zoneLoad.ResearchMode) log.Information("[HMSync] [MAPREVEAL] sanitised " + restored + " revealed map(s) on session end.");
        revealSnapshots.Clear();
        DeletePendingFile();
    }

    // Write the current pending records to disk (map id -> original bytes, base64). Overwrites. Called on
    // each new reveal so a crash always has an up-to-date record to recover from.
    private void PersistPendingReveals()
    {
        try
        {
            var payload = new Dictionary<string, string>();
            foreach (var kv in revealSnapshots)
                payload[kv.Key.ToString()] = Convert.ToBase64String(kv.Value);
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            System.IO.File.WriteAllText(MapRevealPendingPath, json);
        }
        catch (Exception ex) { log.Warning("[HMSync] [MAPREVEAL] failed to persist pending reveals: " + ex.Message); }
    }

    private void DeletePendingFile()
    {
        try { if (System.IO.File.Exists(MapRevealPendingPath)) System.IO.File.Delete(MapRevealPendingPath); }
        catch (Exception ex) { log.Warning("[HMSync] [MAPREVEAL] failed to delete pending file: " + ex.Message); }
    }

    // Crash recovery: if a pending file exists at load, a prior session crashed with reveals still applied.
    // Read it and restore each recorded map to its original bytes, then delete the file. DEFERRED until the
    // discovery manager is live (a load-time write could race the save load or hit an uninitialised struct),
    // via a short framework poll. No-op if the file is absent.
    private void ArmRevealCrashRecovery()
    {
        try
        {
            if (!System.IO.File.Exists(MapRevealPendingPath)) return;
        }
        catch { return; }

        int ticks = 0;
        void Poll(IFramework fw)
        {
            ticks++;
            bool done = false;
            try
            {
                unsafe
                {
                    var disc = FFXIVClientStructs.FFXIV.Client.Game.MapDiscoveryManager.Instance();
                    if (disc != null)
                    {
                        RecoverPendingReveals();
                        done = true;
                    }
                }
            }
            catch (Exception ex) { log.Warning("[HMSync] [MAPREVEAL] crash-recovery poll error: " + ex.Message); done = true; }

            if (done || ticks > 600)   // give up after ~10s; the file stays for the next attempt if we never settled
            {
                framework.Update -= Poll;
                if (!done) log.Warning("[HMSync] [MAPREVEAL] crash-recovery timed out waiting for the discovery manager.");
            }
        }
        framework.Update += Poll;
    }

    private unsafe void RecoverPendingReveals()
    {
        int restored = 0;
        try
        {
            var json = System.IO.File.ReadAllText(MapRevealPendingPath);
            var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (payload != null)
            {
                foreach (var kv in payload)
                {
                    if (!uint.TryParse(kv.Key, out var mapId)) continue;
                    byte[] orig;
                    try { orig = Convert.FromBase64String(kv.Value); } catch { continue; }
                    if (ResolveDiscoveryTable(mapId, out var tablePtr, out int regionCount, out _, out _) != DiscResolve.Ok) continue;
                    int n = Math.Min(regionCount, orig.Length);
                    for (int i = 0; i < n; i++) ((byte*)tablePtr)[i] = orig[i];
                    restored++;
                }
            }
            log.Information("[HMSync] [MAPREVEAL] crash recovery: restored " + restored + " map(s) from a prior session.");
        }
        catch (Exception ex) { log.Warning("[HMSync] [MAPREVEAL] crash recovery read failed: " + ex.Message); }
        DeletePendingFile();
    }

    private void DoPktCap(string? arg)
    {
        if (packetFilter.CaptureInbound)
        {
            packetFilter.CaptureInbound = false;
            packetFilter.CaptureOpcodes = null;
            packetFilter.DisableCaptureOnly();
            chat.Print("[HMSync] Packet capture OFF.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(arg))
        {
            var set = new HashSet<ushort>();
            foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (ushort.TryParse(part, out var op)) set.Add(op);
            packetFilter.CaptureOpcodes = set.Count > 0 ? set : null;
            chat.Print("[HMSync] Packet capture ON (opcodes: " + (set.Count > 0 ? string.Join(",", set) : "ALL") + "). Check the log.");
        }
        else
        {
            packetFilter.CaptureOpcodes = null;
            chat.Print("[HMSync] Packet capture ON (ALL inbound - firehose; use in an inn). Check the log.");
        }
        packetFilter.CaptureInbound = true;
        packetFilter.EnableCaptureOnly();   // enable the receive hook for capture if the full filter isn't already running
    }

    private void DoStatus()
    {
        chat.Print("[HMSync] === Status ===");
        chat.Print("[HMSync] Connected: " + relay.IsConnected);
        chat.Print("[HMSync] Room: " + (relay.RoomId.Length > 0 ? relay.RoomId : "none"));
        chat.Print("[HMSync] Host: " + relay.IsHost);
        chat.Print("[HMSync] Packet filter: " + (packetFilter.IsActive ? "ON" : "OFF"));
        chat.Print("[HMSync] Noclip: " + (noclip.IsActive ? "ON" : "OFF"));
        chat.Print("[HMSync] Zone: " +
            (zoneLoad.IsZoneLoaded
                ? zoneLoad.GetZoneName(zoneLoad.CurrentLoadedZone) + " (" + zoneLoad.CurrentLoadedZone + ")"
                : "none"));
        chat.Print("[HMSync] Peers: " + stateApply.Peers.Count);
    }

    // S205: print the local player's exact world position (entity space) + territory ID. The in-game
    // ── S326m: spawn management (UI-facing) ──────────────────────────────────────────────────────────────────────
    // Read the local player's live position + facing as a compact string (for the Room-options "Here" button). Uses
    // the same authoritative native GameObject.Position as /hms here. Returns null if no player.
    private unsafe string? ReadHereCoords()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return null;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        var p = go->Position;
        return "X " + p.X.ToString("F1") + "   Y " + p.Y.ToString("F1") + "   Z " + p.Z.ToString("F1") +
               "   (facing " + player.Rotation.ToString("F2") + ")";
    }

    // Raw live player position for the teleport fields (same native GameObject.Position as /hms here). Null if none.
    private unsafe System.Numerics.Vector3? ReadLivePosition()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return null;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        var p = go->Position;
        return new System.Numerics.Vector3(p.X, p.Y, p.Z);
    }

    // Teleport the local player to an XYZ target (the Show-coordinates Teleport button). Same SetPosition write the
    // spawn/noclip paths use; only invoked from a loaded-zone context (the UI gates it there).
    private unsafe void DoTeleport(System.Numerics.Vector3 pos)
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        go->SetPosition(pos.X, pos.Y, pos.Z);          // immediate...
        teleportHoldTarget = pos; teleportHoldFrames = 12;   // ...and hold, so the engine doesn't re-ground/revert it
    }

    private unsafe void ApplyTeleportHold()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { teleportHoldFrames = 0; return; }
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        var t = teleportHoldTarget!.Value;
        go->SetPosition(t.X, t.Y, t.Z);
    }

    // Capture the local player's current position as the user spawn for a territory (persisted). Territory 0 = the
    // currently-loaded zone. Returns true if captured.
    private unsafe bool CaptureSpawn(uint territoryId)
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { chat.Print("[HMSync] Capture spawn: no local player."); return false; }
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        var p = go->Position;

        // v0.7.227: if we're standing in a SWAP cutscene stage, key the capture by its bg path (unique) - NOT by the
        // donor territoryId, which is shared across co-donor stages and would leak this spawn to all of them (the bug).
        var stageBg = zoneLoad.ActiveStageBg;
        if (stageBg != null)
        {
            config.UserStageSpawns[stageBg] = new[] { p.X, p.Y, p.Z, player.Rotation };
            config.Save();
            chat.Print("[HMSync] Spawn captured for stage " + stageBg + " at X=" + p.X.ToString("F1") +
                " Y=" + p.Y.ToString("F1") + " Z=" + p.Z.ToString("F1") + ".");
            return true;
        }

        if (territoryId == 0) territoryId = zoneLoad.IsZoneLoaded ? zoneLoad.CurrentLoadedZone : 0;
        if (territoryId == 0) { chat.Print("[HMSync] Capture spawn: no territory (load a map or select one)."); return false; }
        config.UserSpawns[territoryId] = new[] { p.X, p.Y, p.Z, player.Rotation };
        config.Save();
        chat.Print("[HMSync] Spawn captured for territory " + territoryId + " at X=" + p.X.ToString("F1") +
            " Y=" + p.Y.ToString("F1") + " Z=" + p.Z.ToString("F1") + ".");
        return true;
    }

    // Clear the user spawn for a territory (revert to curated/LGB resolution). Territory 0 = currently-loaded.
    private void RevertSpawn(uint territoryId)
    {
        // v0.7.227: swap-stage capture clears by bg (parallels CaptureSpawn's bg keying).
        var stageBg = zoneLoad.ActiveStageBg;
        if (stageBg != null)
        {
            if (config.UserStageSpawns.Remove(stageBg))
            {
                config.Save();
                chat.Print("[HMSync] Spawn override cleared for stage " + stageBg + " (using curated/default spawn).");
            }
            else chat.Print("[HMSync] No spawn override to clear for this stage.");
            return;
        }
        if (territoryId == 0) territoryId = zoneLoad.IsZoneLoaded ? zoneLoad.CurrentLoadedZone : 0;
        if (territoryId != 0 && config.UserSpawns.Remove(territoryId))
        {
            config.Save();
            chat.Print("[HMSync] Spawn override cleared for territory " + territoryId + " (using default spawn).");
        }
        else chat.Print("[HMSync] No spawn override to clear for that territory.");
    }

    // map only shows XY display coords; spawns need the full XYZ vector for SetPosition. Use this to
    // capture verified spawn points (stand where you want the spawn, /hms here, record the vector).
    private unsafe void DoPrintHere()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { chat.Print("[HMSync] /hms here: no local player."); return; }
        var terr = zoneLoad.IsZoneLoaded ? zoneLoad.CurrentLoadedZone : 0;

        // Read THREE sources to diagnose the (0,0,0) wrapper result:
        //  - Dalamud wrapper (player.Position) - what /hms here used before; can read stale/zero
        //  - native GameObject.Position @0xB0 - the authoritative live position
        //  - native GameObject.DefaultPosition @0x10 - the game's assigned spawn for this territory
        var wrap = player.Position;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        var nat = go->Position;
        var def = go->DefaultPosition;

        chat.Print("[HMSync] === here ===");
        chat.Print("[HMSync] Territory: " + terr +
            (terr != 0 ? " (" + zoneLoad.GetZoneName(terr) + ")" : ""));
        chat.Print("[HMSync] Native Pos:   X=" + nat.X.ToString("F4") +
            " Y=" + nat.Y.ToString("F4") + " Z=" + nat.Z.ToString("F4"));
        chat.Print("[HMSync] DefaultPos:   X=" + def.X.ToString("F4") +
            " Y=" + def.Y.ToString("F4") + " Z=" + def.Z.ToString("F4"));
        chat.Print("[HMSync] Wrapper Pos:  X=" + wrap.X.ToString("F4") +
            " Y=" + wrap.Y.ToString("F4") + " Z=" + wrap.Z.ToString("F4"));
        chat.Print("[HMSync] Facing (yaw): " + player.Rotation.ToString("F4"));
        log.Information("[HMSync] /hms here - terr=" + terr +
            " NATIVE X=" + nat.X.ToString("R") + " Y=" + nat.Y.ToString("R") + " Z=" + nat.Z.ToString("R") +
            " | DEFAULT X=" + def.X.ToString("R") + " Y=" + def.Y.ToString("R") + " Z=" + def.Z.ToString("R") +
            " | WRAPPER X=" + wrap.X.ToString("R") + " Y=" + wrap.Y.ToString("R") + " Z=" + wrap.Z.ToString("R"));
    }

    // ── Cosmetic toggles (client-side, bypass packet filter) ──

    private unsafe void DoToggleDisplayArms()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;

        // S248: when Glamourer is authoritative, flip from ITS current state (so the click always
        // matches the badge - no HMS-tracked bool to desync). WeaponState is VISIBILITY (true=shown).
        if (glamourer.Available && glamourer.TryGetMeta(0, out var curWpnVis, out _, out _))
        {
            glamourer.SetMeta(0, MetaFlag.WeaponState, !curWpnVis);
            weaponHidden = curWpnVis; // keep the fallback bool in step for if Glamourer drops later
            chat.Print("[HMSync] Weapons " + (curWpnVis ? "hidden" : "shown") + " when sheathed.");
        }
        else
        {
            // Fallback: no Glamourer - flip OUR tracked intent and write DrawData.
            weaponHidden = !weaponHidden;
            character->DrawData.HideWeapons(weaponHidden);
            chat.Print("[HMSync] Weapons " + (weaponHidden ? "hidden" : "shown") + " when sheathed.");
        }
        glamourerBadgesDirty = true;
    }

    private unsafe void DoToggleDisplayHead()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;

        // S248: flip from Glamourer's current state when authoritative (HatState = visibility).
        if (glamourer.Available && glamourer.TryGetMeta(0, out _, out var curHatVis, out _))
        {
            glamourer.SetMeta(0, MetaFlag.HatState, !curHatVis);
            headgearHidden = curHatVis;
            chat.Print("[HMSync] Headgear " + (curHatVis ? "hidden" : "shown") + ".");
        }
        else
        {
            // Fallback: method ONLY - HideHeadgear owns the bit; pre-setting no-ops the redraw (S245).
            headgearHidden = !headgearHidden;
            character->DrawData.HideHeadgear(0, headgearHidden);
            chat.Print("[HMSync] Headgear " + (headgearHidden ? "hidden" : "shown") + ".");
        }
        glamourerBadgesDirty = true;
    }

    // [FACECAMDUMP] byte-diff the LookAt region to see EXACTLY what /facecamera changes on the self-actor.
    // First call: capture BEFORE. Toggle /facecamera on. Second call: capture AFTER + log every changed byte.
    // Ends the guessing about which field/offset is the real switch. LookAt @ Character+0xD80, region 0xB80..0x1980.
    private byte[]? faceCamDumpBefore;
    private unsafe void DoFaceCamDump()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { chat.Print("[HMSync] no player"); return; }
        byte* baseP = (byte*)player.Address;
        const int start = 0xB80, end = 0x1980;   // covers container header, controller, _params, CameraVector, flag
        int len = end - start;
        var snap = new byte[len];
        for (int i = 0; i < len; i++) snap[i] = baseP[start + i];

        if (faceCamDumpBefore == null)
        {
            faceCamDumpBefore = snap;
            chat.Print("[HMSync] facecamdump: BEFORE captured. Now toggle /facecamera ON, then run /hms facecamdump again.");
            return;
        }

        // diff
        int changes = 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < len; i++)
        {
            if (faceCamDumpBefore[i] != snap[i])
            {
                changes++;
                int off = start + i;
                sb.Append("  char+0x" + off.ToString("X") + " (LookAt+0x" + (off - 0xD80).ToString("X") + "): " +
                    faceCamDumpBefore[i].ToString("X2") + " -> " + snap[i].ToString("X2") + "\n");
            }
        }
        log.Information("[HMSync] [FACECAMDUMP] " + changes + " bytes changed:\n" + sb.ToString());
        chat.Print("[HMSync] facecamdump: " + changes + " changed bytes logged. (run again to reset BEFORE)");
        faceCamDumpBefore = null;   // reset for next round
    }

    private unsafe void DoToggleVisor()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;

        // S248: flip from Glamourer's current state when authoritative (VisorState = toggled sense).
        if (glamourer.Available && glamourer.TryGetMeta(0, out _, out _, out var curVisor))
        {
            glamourer.SetMeta(0, MetaFlag.VisorState, !curVisor);
            visorToggled = !curVisor;
            chat.Print("[HMSync] Visor " + (!curVisor ? "on" : "off") + ".");
        }
        else
        {
            // Fallback: native SetVisor + bit.
            visorToggled = !visorToggled;
            character->DrawData.SetVisor(visorToggled);
            character->DrawData.IsVisorToggled = visorToggled;
            chat.Print("[HMSync] Visor " + (visorToggled ? "on" : "off") + ".");
        }
        glamourerBadgesDirty = true;
    }

    // /hms emote <id|name> - play an emote on the local player and sync it to peers.
    //   • Usable now → AgentEmote.ExecuteEmote - the same agent the in-game emote menu drives, so it owns
    //     the full lifecycle (stance, loop mode, movement-cancel) AND natively cancels whatever was already
    //     playing. The resulting state is read by LocalStateDetector → the existing capture→
    //     ApplyEmoteFromSheet pipeline syncs it. (S322a–e tried the lower-level EmoteManager.ExecuteEmote +
    //     a manual mode-clear; the agent path is what finally makes emote→emote transitions clean.)
    //   • Locked / gated (the RP point - unowned or item-gated emotes the agent won't play) → bypass by
    //     driving the emote's ActionTimeline / mode directly (the SAME mechanism the peer side uses on
    //     puppets), first cancelling any active loop so it doesn't dominate the forced animation.
    // Either path is client-side only (no server roundtrip - correct, peers are in other instances).
    // Validated against the Emote sheet first (a bad ID must never reach the game).
    private unsafe void DoEmote(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            chat.Print("[HMSync] Usage: /hms emote <id|name|stop>  (e.g. /hms emote 21, /hms emote dance, /hms emote stop). Plays it on you (and syncs to peers in a session). Locked emotes only play inside a session.");
            return;
        }

        // S322: stop/cancel the current emote → idle (and sync the stop to peers). Mirrors the mount Dismount
        // and minion Dismiss. Checked before the sheet lookup so it never collides with an emote name.
        var trimmed = arg.Trim();
        if (trimmed.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            StopEmoteSelf();
            return;
        }

        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        if (sheet == null) { chat.Print("[HMSync] Emote sheet unavailable."); return; }

        var q = arg.Trim();
        bool numeric = ushort.TryParse(q, out var parsed);
        ushort emoteId = 0, introTl = 0, loopTl = 0;
        string label = q;

        // Resolve by ID or exact name, capturing the intro (AT[1]) + loop (AT[0]) timelines for the bypass.
        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            bool match = numeric
                ? row.RowId == parsed
                : string.Equals(row.Name.ToString(), q, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                emoteId = (ushort)row.RowId;
                introTl = (ushort)row.ActionTimeline[1].RowId;
                loopTl = (ushort)row.ActionTimeline[0].RowId;
                if (!numeric) label = row.Name.ToString();
                break;
            }
        }
        if (emoteId == 0 && !numeric) // contains-fallback (e.g. "battle" → "Battle Stance")
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
                var nm = row.Name.ToString();
                if (!string.IsNullOrEmpty(nm) && nm.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    emoteId = (ushort)row.RowId;
                    introTl = (ushort)row.ActionTimeline[1].RowId;
                    loopTl = (ushort)row.ActionTimeline[0].RowId;
                    label = nm;
                    break;
                }
            }
        }
        if (emoteId == 0)
        { chat.Print("[HMSync] No emote " + (numeric ? "with ID " + q : "matching \"" + q + "\"") + "."); return; }
        if (numeric) label = "#" + emoteId;

        // S322: locked emotes only play INSIDE a session, where peers actually see them. Outside one, forcing
        // a locked emote would just fool the local player into thinking they'd unlocked it (HaselDebug-style
        // gating). Unlocked emotes play normally anywhere. The Emotes tab greys locked rows out of session to
        // match; this guard also covers the typed command.
        if (!CanUseEmoteSafe(emoteId) && !relay.IsSessionActive)
        {
            chat.Print("[HMSync] \"" + label + "\" isn't unlocked. Locked emotes only play inside an HMS session.");
            return;
        }

        config.PushRecentEmote(emoteId);
        PlayResolvedEmote(emoteId, introTl, loopTl, label);
    }

    // S322: does the local player have this emote unlocked? UI uses this (via the CanUseEmote hook) to grey
    // locked rows; DoEmote uses it to gate forcing outside a session. Mirrors HaselDebug's CanUseEmote check.
    private unsafe bool CanUseEmoteSafe(ushort emoteId)
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentEmote.Instance();
        return agent != null && agent->CanUseEmote(emoteId);
    }

    // S322: cancel the current emote on the local player → idle, letting the change sync to peers. This is the
    // breaker block from PlayResolvedEmote in isolation: clear the emote owner so the game stops re-stamping a
    // loop, force Mode=Normal (the field the detector reads), release the base override. The detector then sees
    // Normal + idle and broadcasts a standup (emoteId=0) so peers drop the emote too. Idempotent when already
    // idle. Mirrors the mount line's Dismount and the minion Dismiss.
    // ── v0.7.415: clear the local posture at session engage. QUIETLY. ─────────────────────────────
    //
    // v0.7.413/414 played the get-up timeline (644/655) to make the transition observable to the
    // detector, so it would emit a StandupEpoch. That worked - the detector saw it - but it was the
    // wrong shape twice over:
    //   • it played a visible stand-up animation nobody asked for, and
    //   • the receiver could not act on the standup anyway: its gate is
    //         if (data.StandupTimelineId > 0 && info.EmoteActive)
    //     and EmoteActive is set in exactly ONE place - when HMS itself applies an emote. A peer whose
    //     puppet is seated because the player GENUINELY WAS seated has EmoteActive false, so the
    //     standup is received, the epoch is consumed, and nothing happens.
    //
    // The real situation: A is not standing up. A is ALREADY STANDING - B just does not know, because
    // B's puppet inherited a true seated state from before the session and HMS has no path to clear a
    // pose it did not create. That is a RECEIVER problem, fixed at bind (see
    // StateApplyService.ReconcileInheritedPose), so the sender does not need to perform anything.
    //
    // So this is now the minimum: drop the posture locally, no animation, no broadcast theatre.
    // v0.7.419 - sanitise the local player's posture to standing/idle. Used at BOTH engage (clear
    // origin posture before loading the synthetic zone) and exit (clear any posture acquired during
    // the session before Revert). Covers both posture families:
    //   • InPositionLoop (ConditionMode 11): Sit, Sit on Ground, Sleep, Stand Up - 15 rows
    //   • EmoteLoop (ConditionMode 3): dances, cheers, /lean, persistent emotes - 97 rows
    // Do NOT widen to "any non-Normal mode." Mounted, Crafting, Gathering have their own tested
    // teardown paths (mount sanitise, noclip.Disable). The two loop families are the posture families.
    //
    // When force=true (post-settle), clear Mode/EmoteId/DrawOffset/BaseOverride UNCONDITIONALLY.
    // The zone reload in Revert rebuilds the actor from the client's internal character cache, which
    // may hold the PRE-SESSION state (seated) even though the entry sanitise cleared it - the entry
    // sanitise wrote Mode=Normal onto the live object, but the cache was never updated. So after the
    // reload, Mode can be InPositionLoop again even though the player was standing throughout the
    // session. The post-settle call fires after the rebuild, on the settled actor, and must clear
    // regardless of what the isPosture guard thinks.
    // v0.7.449 - evictBaseLane: whether to stomp the base lane to idle (PlayTimeline(3)) to evict a
    // lingering seated/emote pose clip. null = follow isPosture (the default: a real seated/emote posture
    // needs the evict; a plain standing state does not). Callers pass false to force-clear mode/emote/
    // draw-offset over a STANDING cpose without the idle stomp that would flicker folded-arms on exit.
    private unsafe void SanitiseLocalPosture(string context, bool force = false, bool? evictBaseLane = null)
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { log.Debug("[HMSync] [POSE] " + context + ": no local player"); return; }
        var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        if (ch == null) { log.Debug("[HMSync] [POSE] " + context + ": null character"); return; }

        var currentMode = ch->Mode;
        var currentParam = ch->ModeParam;

        bool isPosture = currentMode == CharacterModes.InPositionLoop
                      || currentMode == CharacterModes.EmoteLoop;

        if (!isPosture && !force)
        {
            log.Debug("[HMSync] [POSE] " + context + ": no posture to clear (Mode=" + currentMode + "/" + currentParam + ")");
            return;
        }

        ch->EmoteController.EmoteId = 0;
        ch->SetMode(CharacterModes.Normal, 0);
        ch->Timeline.BaseOverride = 0;
        ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address)->SetDrawOffset(0f, 0f, 0f);

        // v0.7.417 - CLEAR THE BASE LANE TOO, or we broadcast a seated emote one frame later.
        //
        // Clearing Mode is instant; the base-lane CLIP is not. TimelineIds[0] keeps holding 3132
        // (emote/s_pose01_loop - the chair-sit POSE variant) for a frame or two. detector.Reset() has
        // just baselined lastTimelineId to that same 3132, so on the next tick:
        //   • timelineChanged is FALSE  -> the standup branch cannot fire, and
        //   • the modeChanged branch finds 3132 in emoteTimelineIds (it belongs to emote 95) and
        //     broadcasts emote 95. Its EmoteMode's ConditionMode is InPositionLoop, so the receiver
        //     does SetMode(InPositionLoop) - a visible SIT-DOWN animation, and the puppet sticks.
        // That is the "loads standing, then sits back down" symptom exactly.
        //
        // PlayTimeline(3) = normal/idle. Verified against the sheet: NO emote lists timeline 3 among
        // its ActionTimelines, so it cannot be misread as an emote - the detector falls through to
        // emoteId = 0. Base-lane eviction is correct here; we are deliberately dropping the pose, and
        // idle is what the game settles on by itself a frame later anyway. No visible animation.
        //
        // v0.7.449 - but the eviction is only wanted when there's a SEATED/EMOTE clip to evict. When
        // force-clearing on exit while the player is merely STANDING in a cpose (e.g. folded arms),
        // PlayTimeline(3) pushes neutral idle for one cycle and the game's cpose reasserts folded-arms
        // the next cycle - a one-cycle arms-drop flicker. A numeric timeline-id gate does NOT work: the
        // standing cpose clips (idle_sp/*, ids 210…11271) and the seated emote-pose clips (emote/*_pose,
        // ids 1065+) overlap in id space with no clean boundary. So the caller decides: evictBaseLane is
        // passed true only when the posture being cleared is genuinely seated/emote (a real posture, or
        // the forced exit case where the ORIGIN was seated), and false when force-clearing over a plain
        // standing cpose. When not forced, isPosture already implies a real seated/emote posture, so the
        // default follows isPosture.
        if (evictBaseLane ?? isPosture)
            ch->Timeline.TimelineSequencer.PlayTimeline(3);

        log.Debug("[HMSync] [POSE] " + context + ": cleared posture (was " + currentMode + "/" + currentParam +
            (force ? ", forced" : "") + ")");
    }

    // v0.7.420 - SERVER-ACK + ORIGIN RESTORE. Two jobs in one:
    // 1. Tell the server we left the origin posture (the filter prevented it hearing the standup).
    // 2. Re-enter the origin posture so the player returns to exactly where they started.
    //
    // For InPositionLoop (sit/groundsit/sleep): two-step via AgentEmote.
    //   Step 1: re-enter the posture client-side → execute emote 50 (/sit toggle) → from
    //           InPositionLoop this is a standup → server clears InPositionLoop → both agree standing.
    //   Step 2: re-execute the origin emote (50/52/88) → normal sit-down → server processes it →
    //           both agree seated → player is back where they started.
    // For EmoteLoop (lean/dance): server doesn't track these - just re-execute the origin emote.
    private unsafe void ServerAckStandup()
    {
        try
        {
            var lp = objectTable.LocalPlayer;
            if (lp == null) return;
            var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentEmote.Instance();
            if (agent == null) return;

            if (originMode == CharacterModes.InPositionLoop)
            {
                // Step 1: re-enter the posture the server thinks we're in, then toggle to standup.
                ch->SetMode(originMode, originModeParam);
                log.Debug("[HMSync] [POSE] server-ack: re-entered " + originMode + "/" + originModeParam);

                agent->ExecuteEmote(50, addToHistory: false);
                log.Debug("[HMSync] [POSE] server-ack: step 1 - standup (emote 50 toggle)");

                // Step 2: re-execute the origin emote to restore the pose.
                // Both sides now agree we're standing, so this is a normal sit-down.
                if (originEmoteId > 0)
                {
                    agent->ExecuteEmote(originEmoteId, addToHistory: false);
                    log.Debug("[HMSync] [POSE] server-ack: step 2 - restore (emote " + originEmoteId + ")");
                }
            }
            else if (originMode == CharacterModes.EmoteLoop)
            {
                // EmoteLoop (lean, dance) - server doesn't track these. Just re-execute.
                if (originEmoteId > 0)
                {
                    agent->ExecuteEmote(originEmoteId, addToHistory: false);
                    log.Debug("[HMSync] [POSE] server-ack: restored EmoteLoop (emote " + originEmoteId + ")");
                }
                else
                {
                    ch->SetMode(CharacterModes.Normal, 0);
                    log.Debug("[HMSync] [POSE] server-ack: cleared EmoteLoop (no emote ID)");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning("[HMSync] [POSE] server-ack failed: " + ex.Message);
        }
    }

    private unsafe void StopEmoteSelf()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { chat.Print("[HMSync] No local player."); return; }
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;

        // Only cancel when an emote is actually running - a loop (EmoteLoop/InPositionLoop) or a persistent/
        // one-shot with a live EmoteController.EmoteId. Without this guard, Stop would force Mode=Normal out of
        // an unrelated mode (e.g. mounted) and glitch it. No emote ⇒ no-op.
        bool emoteActive = character->Mode == CharacterModes.EmoteLoop
            || character->Mode == CharacterModes.InPositionLoop
            || character->EmoteController.EmoteId != 0;
        if (!emoteActive)
            return;

        character->EmoteController.EmoteId = 0;
        character->SetMode(CharacterModes.Normal, 0);
        character->Mode = CharacterModes.Normal;
        character->ModeParam = 0;
        character->Timeline.BaseOverride = 0;
    }

    // S322: /hms minion <id|name> - summon a minion on yourself (synced to peers in a session). Mirror of
    // DoEmote. Locked minions only summon INSIDE a session (otherwise it just fakes a local unlock); unlocked
    // minions summon anywhere. "0" / "off" / "dismiss" clears it. The detector captures the summon + broadcasts.
    private unsafe void DoMinion(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            chat.Print("[HMSync] Usage: /hms minion <id|name>  (e.g. /hms minion 52, /hms minion wind-up). Summons it on you (and syncs to peers in a session). 0 dismisses. Locked minions only summon inside a session.");
            return;
        }

        var q = arg.Trim();
        if (q == "0" || q.Equals("off", StringComparison.OrdinalIgnoreCase) || q.Equals("dismiss", StringComparison.OrdinalIgnoreCase))
        {
            stateApply.SummonMinionSelf(0);
            return;
        }

        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
        if (sheet == null) { chat.Print("[HMSync] Companion sheet unavailable."); return; }

        bool numeric = ushort.TryParse(q, out var parsed);
        ushort minionId = 0;
        string label = q;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            bool match = numeric ? row.RowId == parsed
                : string.Equals(row.Singular.ToString(), q, StringComparison.OrdinalIgnoreCase);
            if (match) { minionId = (ushort)row.RowId; if (!numeric) label = row.Singular.ToString(); break; }
        }
        if (minionId == 0 && !numeric) // contains-fallback
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
                var nm = row.Singular.ToString();
                if (!string.IsNullOrEmpty(nm) && nm.Contains(q, StringComparison.OrdinalIgnoreCase))
                { minionId = (ushort)row.RowId; label = nm; break; }
            }
        }
        if (minionId == 0)
        { chat.Print("[HMSync] No minion " + (numeric ? "with ID " + q : "matching \"" + q + "\"") + "."); return; }
        if (numeric) label = "#" + minionId;

        if (!CanUseMinionSafe(minionId) && !relay.IsSessionActive)
        {
            chat.Print("[HMSync] \"" + label + "\" isn't unlocked. Locked minions only summon inside an HMS session.");
            return;
        }

        // S322: a repeated click/command on the already-summoned minion DISMISSES it (toggle). A DIFFERENT
        // minion replaces the current one (SetupCompanion handles the swap). The live minion is read from the
        // spawned companion object (CompanionData.CompanionId is not the live id).
        if (CurrentMinionId() == minionId)
        {
            stateApply.SummonMinionSelf(0);
            return;
        }

        if (stateApply.SummonMinionSelf((short)minionId))
            config.PushRecentMinion(minionId);
    }

    // S322: the live summoned minion id (Companion sheet row), or 0 if none is out. Read from the spawned
    // companion object's BaseId - the same field HaselDebug uses; CompanionData.CompanionId is not the live id.
    private unsafe ushort CurrentMinionId()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return 0;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        return character->CompanionData.CompanionObject != null
            ? (ushort)character->CompanionData.CompanionObject->BaseId : (ushort)0;
    }

    // S322k: does the local player have this minion unlocked? UI uses this (via CanUseMinion) to grey locked
    // rows; DoMinion uses it to gate forcing outside a session. UIState.IsCompanionUnlocked is the analog.
    private unsafe bool CanUseMinionSafe(ushort minionId)
    {
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        return ui != null && ui->IsCompanionUnlocked(minionId);
    }

    private unsafe bool CanUseMountSafe(ushort mountId)
    {
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return ps != null && ps->IsMountUnlocked(mountId);
    }

    // S322k: /hms accessory <id|name> - equip + sync a fashion accessory (ornament). Direct mirror of DoMinion:
    // numeric or name match against the Ornament sheet, repeated-same toggles off, locked ones gated to a
    // session. Ornaments are skeletally attached so they ride the puppet natively once SetupOrnament seats them.
    private unsafe void DoAccessory(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            chat.Print("[HMSync] Usage: /hms accessory <id|name>  (e.g. /hms accessory 3 for a parasol). Equips it on you (and syncs to peers in a session). 0 removes. Locked accessories only equip inside a session.");
            return;
        }

        var q = arg.Trim();
        if (q == "0" || q.Equals("off", StringComparison.OrdinalIgnoreCase) || q.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            stateApply.SummonOrnamentSelf(0);
            return;
        }

        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Ornament>();
        if (sheet == null) { chat.Print("[HMSync] Ornament sheet unavailable."); return; }

        bool numeric = ushort.TryParse(q, out var parsed);
        ushort ornamentId = 0;
        string label = q;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
            bool match = numeric ? row.RowId == parsed
                : string.Equals(row.Singular.ToString(), q, StringComparison.OrdinalIgnoreCase);
            if (match) { ornamentId = (ushort)row.RowId; if (!numeric) label = row.Singular.ToString(); break; }
        }
        if (ornamentId == 0 && !numeric) // contains-fallback
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;
                var nm = row.Singular.ToString();
                if (!string.IsNullOrEmpty(nm) && nm.Contains(q, StringComparison.OrdinalIgnoreCase))
                { ornamentId = (ushort)row.RowId; label = nm; break; }
            }
        }
        if (ornamentId == 0)
        { chat.Print("[HMSync] No fashion accessory " + (numeric ? "with ID " + q : "matching \"" + q + "\"") + "."); return; }
        if (numeric) label = "#" + ornamentId;

        if (!CanUseOrnamentSafe(ornamentId) && !relay.IsSessionActive)
        {
            chat.Print("[HMSync] \"" + label + "\" isn't unlocked. Locked accessories only equip inside an HMS session.");
            return;
        }

        // Repeated command on the already-equipped ornament REMOVES it (toggle); a different one swaps. The live
        // ornament is read from the spawned object (OrnamentData.OrnamentId reads 0, the CompanionId trap).
        if (CurrentOrnamentId() == ornamentId)
        {
            stateApply.SummonOrnamentSelf(0);
            return;
        }

        if (stateApply.SummonOrnamentSelf((short)ornamentId))
            config.PushRecentOrnament(ornamentId);
    }

    // ── S326: map-state handlers (host-only). Each sets the persisted config, mirrors into the capture holder so the
    // value rides the outbound stream (broadcast to peers), bumps the shared epoch (so peers apply once per change),
    // applies locally on the host so the host sees it immediately, and saves. Peers apply on receipt (StateApplyService).
    /// <param name="weatherOverride">The weather the host JUST picked, passed verbatim. See the note below on why
    /// an explicit pick must not be read back from the engine.</param>
    private void PushMapState(byte? weatherOverride = null)
    {
        // Mirror config → capture holder and bump the epoch. Called by every map* handler after it updates config.
        // SINGLE-AUTHORITY WEATHER - v0.7.475: BROADCAST REALITY, NOT INTENT.
        //
        // History, because this reversed twice. Originally the host's raw pick went on the wire; the host's own
        // Reassert legality-gated locally (falling back to native) while peers applied the raw id, so host and peer
        // diverged - "peers stuck on none/atmospheric while the host sees sunny". v0.7.429 fixed that by resolving
        // the wire value to a legal, non-zero id. That killed two real features, because it cannot tell
        // `MapWeatherId == 0` = "host picked nothing" from `== 0` = "host explicitly picked None - Atmospheric",
        // and it discards any id not in the map's legal set - which is precisely the debug-weather case (Fog on
        // 1345, and the cinematic blank the invalid ones fall through to).
        //
        // The real defect was never the wire value: it was host and peer resolving INDEPENDENTLY. So resolve once,
        // on the host, by reading what the engine is actually rendering (EnvManager displayed weather) and shipping
        // that. Live weather is post-fallback truth - it is whatever the host can SEE:
        //   • host never picked        → live = the map's native sky   → peers match the host
        //   • host picked None (0)     → live = 0                      → peers render the same cinematic blank
        //   • host picked Fog on 1345  → live = that id                → peers render the same stunning sky
        //   • host picked a dud        → live = whatever it fell to    → peers fall to the same place
        // In every case peers mirror the host because there is nothing left to re-derive. Same principle the BGM
        // path already states: mirror VERBATIM, never resolve independently on both sides.
        //
        // ⚠ 0 IS NOW A LEGITIMATE WIRE VALUE. The old "peers must never receive 0" invariant is deleted, not
        // relaxed - and the receiver's matching 0-substitution in ApplyMapState had to go with it. Changing one
        // end alone is a no-op; that is why this looked like a wire bug and was a pair of independent guards.
        // ⚠ v0.7.476 - REALITY LAGS THE WRITE. GetActiveWeather reads EnvManager+0x26, the DISPLAYED weather: the
        // value the engine is rendering, which does not update until at least the next frame. So reading it
        // immediately after ApplyWeather returns the PREVIOUS sky, and every pick reached peers one weather late -
        // cycling the dropdown left peers permanently one behind, and a second click on the same entry "fixed" it
        // only because by then the engine had caught up. Live-read is the right source for a SETTLED state and the
        // wrong one for a state we just changed.
        //
        // So: an explicit pick is passed in and shipped VERBATIM (including 0 = None - Atmospheric, including any
        // debug id) - we already know the intent, there is nothing to read back. Every other caller (map load,
        // time, BGM, host succession) pushes state that has been settled for many frames, where the live read is
        // both accurate and the thing that keeps host and peer from resolving independently.
        byte effectiveWeather;
        if (weatherOverride.HasValue)
        {
            effectiveWeather = weatherOverride.Value;
        }
        else if (zoneLoad.IsZoneLoaded)
        {
            effectiveWeather = mapSettings.GetActiveWeather();
        }
        else
        {
            // No synthetic map loaded, so live weather is the open world's and irrelevant to peers. Fall back to
            // the stored pick, resolved to the zone's native only when nothing is stored.
            effectiveWeather = config.MapWeatherId;
            if (effectiveWeather == 0)
                effectiveWeather = mapSettings.GetDefaultWeather(zoneLoad.CurrentLoadedZone);
        }
        stateCapture.MapState.WeatherId = effectiveWeather;
        stateCapture.MapState.TimeForced = config.MapTimeForced;
        stateCapture.MapState.EorzeaHour = config.MapEorzeaHour;
        stateCapture.MapState.EorzeaMinute = config.MapEorzeaMinute;
        // SINGLE-AUTHORITY BGM: broadcast the RESOLVED effective track, never 0. The host owns "what plays here" and the
        // peer mirrors this concrete id VERBATIM (no peer-side GetDefaultBgm / live read - that independent resolution
        // was the desync: host broadcast 0=follow-default, peer re-resolved and drifted). Explicit pick if set, else the
        // loaded zone's resolved default (incl. CFC→InstanceContent for instanced zones).
        uint effectiveBgm = config.MapBgmId != 0 ? config.MapBgmId : mapSettings.GetDefaultBgm(zoneLoad.CurrentLoadedZone);
        stateCapture.MapState.BgmId = effectiveBgm;
        stateCapture.MapState.RemoveNpcs = config.MapRemoveNpcs;
        stateCapture.MapState.HideQuestSigns = config.MapHideQuestSigns;
        stateCapture.MapState.Epoch++;
        // Keep the service's own copy in sync (used by Reassert after a load).
        mapSettings.WeatherId = effectiveWeather;
        mapSettings.TimeForced = config.MapTimeForced;
        mapSettings.EorzeaHour = config.MapEorzeaHour;
        mapSettings.EorzeaMinute = config.MapEorzeaMinute;
        mapSettings.BgmId = effectiveBgm;
        mapSettings.RemoveNpcs = config.MapRemoveNpcs;
        mapSettings.HideQuestSigns = config.MapHideQuestSigns;
        mapSettings.MarkStateSet();
        // S328aa: engage NPC cleanup live on the host when a map is loaded (peers get it via ApplyMapState).
        if (MapApplyLive) DriveNpcVisibility(config.MapRemoveNpcs, config.MapHideQuestSigns);
        config.Save();
    }

    // S327r: map settings apply LIVE when hosting AND a synthetic zone is loaded. (The old gate also required
    // config.MapSettingsTerritory == the loaded zone - but that field belonged to the REMOVED "pick a territory in a
    // dropdown to pre-configure it" model and is NEVER assigned, so it was permanently 0 → MapApplyLive permanently
    // FALSE → the host's BGM/weather buttons never applied locally. Under the current "configure the map you're standing
    // on, live" model there's no separate edit-target: the loaded map IS the edit target, so the check is just
    // "hosting + zone loaded".)
    private bool MapApplyLive => relay.HasMapAuthority && zoneLoad.IsZoneLoaded;

    private void DoMapWeather(string? arg)
    {
        if (!relay.HasMapAuthority) { chat.Print("[HMSync] Only the host can set map weather."); return; }
        if (!byte.TryParse(arg?.Trim(), out var wid)) { chat.Print("[HMSync] Usage: /hms mapweather <id> (0 = None/atmospheric)."); return; }
        config.MapWeatherId = wid;
        // v0.7.475: apply BEFORE PushMapState. PushMapState now broadcasts the LIVE sky, so pushing first would
        // ship the PREVIOUS weather and every change would land on peers exactly one pick late. (The UI's Pick()
        // already applied-then-broadcast; this path was the odd one out.)
        if (MapApplyLive) mapSettings.ApplyWeather(wid);   // apply live only in-session on a loaded map
        PushMapState(wid);   // verbatim: the pick is intent, not something to read back off a lagging engine field
        if (config.ShowDebugCommands) chat.Print("[HMSync] Map weather set to " + mapSettings.WeatherName(wid) + (MapApplyLive ? "." : " (saved, applies on map load)."));
    }

    private void DoMapTime(string? arg)
    {
        if (!relay.HasMapAuthority) { chat.Print("[HMSync] Only the host can set map time."); return; }
        var a = arg?.Trim() ?? "";
        if (a.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            config.MapTimeForced = false;
            PushMapState();
            if (config.ShowDebugCommands) chat.Print("[HMSync] Map time hold off (time flows normally).");
            return;
        }
        // Parse "H" or "H:M".
        int hour, minute = 0;
        var parts = a.Split(':');
        if (parts.Length == 0 || !int.TryParse(parts[0], out hour))
        { chat.Print("[HMSync] Usage: /hms maptime <hour 0-23>[:minute] | off"); return; }
        if (parts.Length > 1) int.TryParse(parts[1], out minute);
        config.MapTimeForced = true;
        config.MapEorzeaHour = (ushort)Math.Clamp(hour, 0, 23);
        config.MapEorzeaMinute = (byte)Math.Clamp(minute, 0, 59);
        PushMapState();
        if (MapApplyLive) mapSettings.ApplyTime(config.MapEorzeaHour, config.MapEorzeaMinute);   // live only in-session
        chat.Print("[HMSync] Map time → " + config.MapEorzeaHour + ":" + config.MapEorzeaMinute.ToString("D2") +
            (MapApplyLive ? " (held)." : " (saved; applies on map load)."));
    }

    private void DoMapBgm(string? arg)
    {
        if (!relay.HasMapAuthority) { chat.Print("[HMSync] Only the host can set map BGM."); return; }
        var a = arg?.Trim() ?? "";
        // Support "stop" as an explicit silence + a numeric id.
        if (a.Equals("stop", StringComparison.OrdinalIgnoreCase)) a = "0";
        if (!uint.TryParse(a, out var bid)) { chat.Print("[HMSync] Usage: /hms mapbgm <id> | stop  (0/stop = none)."); return; }
        config.MapBgmId = bid;
        PushMapState();
        if (MapApplyLive) mapSettings.PlayBgm(bid);   // real playback (scene-0 write) - in-session only
        // No chat notification on BGM change - the Music tab shows the current track; per-change chat lines are noise.
    }

    private void DoMapNpc(string? arg)
    {
        if (!relay.HasMapAuthority) { chat.Print("[HMSync] Only the host can set NPC removal."); return; }
        var a = arg?.Trim() ?? "";
        config.MapRemoveNpcs = a.Equals("on", StringComparison.OrdinalIgnoreCase);
        PushMapState();
        if (config.ShowDebugCommands) chat.Print("[HMSync] Remove NPCs " + (config.MapRemoveNpcs ? "on (event NPCs hidden, striking dummies kept)." : "off (NPCs restored).")
            + (MapApplyLive ? "" : " (saved; applies on map load)."));
    }

    private void DoMapQuestSigns(string? arg)
    {
        if (!relay.HasMapAuthority) { chat.Print("[HMSync] Only the host can set quest-sign hiding."); return; }
        var a = arg?.Trim() ?? "";
        config.MapHideQuestSigns = a.Equals("on", StringComparison.OrdinalIgnoreCase);
        PushMapState();
        if (config.ShowDebugCommands) chat.Print("[HMSync] Hide quest signs " + (config.MapHideQuestSigns ? "on (over-head quest markers hidden, NPCs kept)." : "off (quest markers restored).")
            + (MapApplyLive ? "" : " (saved; applies on map load)."));
    }

    // ── S326f: session participants (Wholist-style) + host per-peer actions ──────────────────────────────────────
    // Resolves each registered peer to its live PlayerObject for name/world/FC/distance. Runs per-frame from the UI;
    // kept cheap (object-table lookups, no allocation beyond the list). Unresolved peers (not yet streamed in) show
    // their last-known name with blank world/FC and distance -1.
    private System.Collections.Generic.List<HMSync.UI.HMSyncUI.ParticipantRow> BuildParticipantList()
    {
        var rows = new System.Collections.Generic.List<HMSync.UI.HMSyncUI.ParticipantRow>();
        var local = objectTable.LocalPlayer;

        // Entry #1 is always the local player (the host, or yourself if you joined) - full designator, no actions on
        // self. This replaces the old "just you so far" placeholder text with a real first row.
        if (local != null)
        {
            var selfRow = new HMSync.UI.HMSyncUI.ParticipantRow
            {
                PeerId = "",                       // empty = self, UI shows no action buttons
                Name = local.Name.TextValue,
                World = WorldName(local.HomeWorld.RowId),
                Fc = "",
                Distance = 0f,
                Resolved = true,
                IsSelf = true,
            };
            unsafe
            {
                var lc = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)local.Address;
                selfRow.Fc = lc->FreeCompanyTagString;
            }
            rows.Add(selfRow);
        }

        // Remote peers, in join order (stable per peer via JoinSequence).
        var ordered = new System.Collections.Generic.List<PeerInfo>(stateApply.Peers.Values);
        ordered.Sort((a, b) => a.JoinSequence.CompareTo(b.JoinSequence));
        foreach (var info in ordered)
        {
            var row = new HMSync.UI.HMSyncUI.ParticipantRow
            {
                PeerId = info.PeerId,
                Name = info.CharacterName,
                World = "",
                Fc = "",
                Distance = -1f,
                Resolved = false,
                IsSelf = false,
            };
            if (info.ObjectIndex.HasValue)
            {
                var obj = objectTable[info.ObjectIndex.Value];
                if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
                {
                    row.Name = pc.Name.TextValue;
                    row.World = WorldName(pc.HomeWorld.RowId);
                    unsafe
                    {
                        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)pc.Address;
                        row.Fc = chara->FreeCompanyTagString;
                    }
                    if (local != null)
                        row.Distance = (local.Position - pc.Position).Length();
                    // v0.7.430 - camera-relative bearing for the compass arrow (Wholist's formula,
                    // DataStructures/PlayerInfoSlim.cs CameraRelativeDirection): the vector FROM the peer
                    // TO the local player, atan2(Δz,Δx), rotated into screen space by the active camera's
                    // horizontal angle + π. Camera-relative (not player-facing) so the arrow points the way
                    // you'd turn the screen to look at them - the intuitive "which way is my friend" readout.
                    // Works on synthetic maps because peer bodies carry real positions under the firewall.
                    unsafe
                    {
                        try
                        {
                            var cam = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
                            var active = cam != null ? cam->GetActiveCamera() : null;
                            if (active != null && local != null)
                            {
                                var d = local.Position - pc.Position;
                                row.Bearing = Math.Atan2(d.Z, d.X) + active->DirH + Math.PI;
                            }
                        }
                        catch { row.Bearing = null; }
                    }
                    row.Resolved = true;
                }
            }
            rows.Add(row);
        }

        // Mark the host and pin them to #1. Host is self when we hold it; otherwise the transfer-tracked host, falling
        // back to the earliest peer by join order (covers succession and fresh joins before any transfer is seen).
        int hostIdx = -1;
        if (relay.IsHost) hostIdx = rows.FindIndex(r => r.IsSelf);
        else if (!string.IsNullOrEmpty(currentHostPeerId)) hostIdx = rows.FindIndex(r => r.PeerId == currentHostPeerId);
        if (hostIdx < 0) hostIdx = rows.FindIndex(r => !r.IsSelf);
        if (hostIdx >= 0)
        {
            var hr = rows[hostIdx]; hr.IsHost = true; rows[hostIdx] = hr;
            if (hostIdx != 0) { rows.RemoveAt(hostIdx); rows.Insert(0, hr); }
        }
        return rows;
    }

    // World-name cache (RowId → name), built lazily from the World sheet.
    private System.Collections.Generic.Dictionary<uint, string>? worldNames;
    private string WorldName(uint worldRowId)
    {
        if (worldNames == null)
        {
            worldNames = new System.Collections.Generic.Dictionary<uint, string>();
            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
                if (sheet != null)
                    foreach (var w in sheet)
                    {
                        var n = w.Name.ToString();
                        if (!string.IsNullOrWhiteSpace(n)) worldNames[w.RowId] = n;
                    }
            }
            catch { /* fall through */ }
        }
        return worldNames.TryGetValue(worldRowId, out var name) ? name : "";
    }

    // Host summons a peer to their position. NOTE: requires a relay message the peer acts on (teleport-to-coords via
    // the same SetPosition path). The relay wire for this is NOT yet implemented - this is a stub that reports the
    // intent so the UI is present; the actual summon lands with the relay-side command. (§E session-management.)
    private void DoSummonPeer(string peerId)
    {
        if (!relay.IsHost) { chat.Print("[HMSync] Only the host can summon."); return; }
        if (config.ShowDebugCommands)
            chat.Print("[HMSync] Summon requested (peer " + peerId + "). Relay summon message not yet implemented.");
        // TODO(§E): relay.SendSummon(peerId, hostPosition); peer applies via SetPosition on receipt.
    }

    // Host removes + bans a peer. The relay drops their connection, bans the ContentId for the room's life, and
    // broadcasts PeerLeft; the kicked client receives Error code 5 and tears down.
    private void DoKickPeer(string peerId)
    {
        if (!relay.IsHost) { chat.Print("[HMSync] Only the host can remove peers."); return; }
        chat.Print("[HMSync] Removing " + PeerName(peerId) + " from the room.");
        _ = relay.SendKick(peerId);
    }

    private void DoRoomPassword(string? arg)
    {
        if (!relay.IsHost) { chat.Print("[HMSync] Only the host can set a room password."); return; }
        var pw = arg?.Trim() ?? "";
        chat.Print("[HMSync] Room password " + (pw.Length == 0 ? "cleared" : "set") +
            " - enforcement needs relay support (not yet active).");
        // TODO(§E): relay.SetRoomPassword(pw); server rejects joins without the matching password.
    }

    private void DoRoomLock(string? arg)
    {
        if (!relay.IsHost) { chat.Print("[HMSync] Only the host can lock the room."); return; }
        var on = (arg?.Trim() ?? "").Equals("on", StringComparison.OrdinalIgnoreCase);
        chat.Print("[HMSync] Room lock " + (on ? "ON" : "OFF") +
            " - enforcement needs relay support (not yet active).");
        // TODO(§E): relay.SetRoomLocked(on); server refuses new joiners while locked.
    }

    // Host hands the session to a specific peer. The relay reassigns the host role and broadcasts HostTransfer; we
    // drop IsHost when that broadcast returns. (Explicit pick - distinct from leave-driven auto-succession.)
    private void DoTransferHost(string peerId)
    {
        if (!relay.IsHost) { chat.Print("[HMSync] Only the host can transfer host."); return; }
        chat.Print("[HMSync] Transferring host to " + PeerName(peerId) + ".");
        _ = relay.SendHostTransfer(peerId);
    }

    // Resolve a relay PeerId to a display name for notifications/menus; falls back to the id if not yet resolved.
    private string PeerName(string peerId)
        => stateApply.Peers.TryGetValue(peerId, out var pi) && !string.IsNullOrEmpty(pi.CharacterName) ? pi.CharacterName : peerId;

    // S322k: the live equipped ornament id (Ornament sheet row), or 0 if none. Read from the spawned object's
    // OrnamentId - the container-side OrnamentData.OrnamentId reads 0, the same trap as CompanionData.CompanionId.
    private unsafe ushort CurrentOrnamentId()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return 0;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        // S323b: the LIVE worn ornament is the CONTAINER's OrnamentId (@0x18). While equipped, the ornament OBJECT's
        // own OrnamentId reads 0 (the opposite of intuition) - matching LocalStateDetector's proven read.
        return character->OrnamentData.OrnamentId;
    }

    // S326p: the local player's current mount id (0 = not mounted). MountContainer @Character 0x670, MountId @+0x18.
    // Powers the dynamic Mount/Dismount button.
    private unsafe ushort CurrentMountId()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return 0;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        return character->Mount.MountId;
    }

    // S326p: is an emote/pose currently playing on the local player (for the dynamic Play/Stop button).
    private unsafe bool IsEmotePlaying()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return false;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        return character->EmoteController.EmoteId != 0;
    }

    // S322k: does the local player have this ornament unlocked? Gates forcing outside a session.
    // PlayerState.IsOrnamentUnlocked is the analog of UIState.IsCompanionUnlocked.
    private unsafe bool CanUseOrnamentSafe(ushort ornamentId)
    {
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return ps != null && ps->IsOrnamentUnlocked(ornamentId);
    }

    // Plays the emote on the local player. Three paths, picked by emote kind and current state:
    //   1. Usable emote from a clean (non-looping) state → AgentEmote.ExecuteEmote, the in-game menu path -
    //      owns the full lifecycle and natively interrupts a prior one-shot (/wave → /point).
    //   2. Anything interrupting a LOOP, or a genuinely locked emote → break the old loop (the receiver's
    //      move-cancel, which also forces Mode=Normal so the detector observes the change) then drive the new
    //      emote directly via SetMode + PlayTimeline. The agent is NOT usable for (2): clearing the loop's
    //      emote state the same frame makes its execute silently no-op. Both persistent and one-shot use
    //      PlayTimeline (not PlayActionTimeline) so the id lands in TimelineIds[0] where the detector reads it.
    // All client-side only; the resulting state is read by LocalStateDetector and synced through the normal
    // capture → ApplyEmoteFromSheet pipeline.
    private unsafe void PlayResolvedEmote(ushort emoteId, ushort introTl, ushort loopTl, string label)
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { chat.Print("[HMSync] No local player."); return; }
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;

        // Is a looped emote currently running that this new emote has to interrupt? Decided up front because
        // it changes BOTH whether we break the old loop and how we play the new emote.
        //
        // v0.7.388 - POSTURES ARE NOT LOOPS TO BREAK. This used to include InPositionLoop, which is the mode
        // the game uses for SITTING, GROUND-SITTING and SLEEPING (EmoteMode.ConditionMode = 11: Sit, Sit on
        // Ground, Sleep, Stand Up - 15 rows). Treating a posture as an interruptible loop sent every emote
        // played while seated down the breaker branch, and the breaker forces Mode=Normal - which STANDS THE
        // CHARACTER UP before playing the standing variant. That is why `/hms emote airquotes` stood you up
        // while the native `/airquotes` correctly played the seated version.
        //
        // Only ConditionMode = 3 (EmoteLoop - the 97 dances/cheers/persistent emotes) is a genuine loop that
        // the agent cannot interrupt and that therefore needs the breaker. A posture must be PRESERVED: the
        // game plays the emote's seated variant over it and returns to the pose afterwards, which is exactly
        // what AgentEmote.ExecuteEmote does natively. Ruleset §5.2 - drive the engine's own path rather than
        // reconstructing the presentation.
        //
        // (This also fixes the posture-variant problem generally: the agent picks the right
        // ActionTimeline slot for the current posture - ground-sit [2], chair-sit [3], upper-body [4] -
        // whereas the direct-play branch below only ever knows about slot 0/1.)
        bool interruptingLoop = character->Mode == CharacterModes.EmoteLoop;

        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentEmote.Instance();
        bool usable = agent != null && agent->CanUseEmote(emoteId);

        // ── ANIM: replicate the ENGINE's condition restrictions (v0.7.392) ────────────────────────
        // The direct-play branch below is a deliberate bypass - it is how HMS lets you play emotes you
        // have not unlocked, inside a session. But it bypassed EVERY refusal, not just the unlock one:
        // when CanUseEmote said no for a STATE reason, HMS fell through and forced the emote anyway.
        //
        // That is the Gulp bug. Gridanian/Ul'dahn/Lominsan Gulp (301/302/303) do not fire in the real
        // game while seated - the engine refuses. HMS forced them, and since those three carry no
        // posture variants at all (ConditionMode 3, slots 0 and 1 only), the standing loop played
        // wherever the body already was, i.e. on top of the chair. The visible symptom looked like a
        // positioning bug; the actual fault was invoking something the game had declined.
        //
        // Fix: tell the two refusals apart, and let the ENGINE be the authority for the second - no
        // hand-tuned condition table, no per-emote special cases.
        //   UIState.IsEmoteUnlocked(id)        - do you own it
        //   EmoteManager.CanExecuteEmote(id)   - can you do it RIGHT NOW (engine-level, not the UI agent)
        // unlocked && !executable  ->  the game is refusing on state grounds. Refuse too.
        // !unlocked                ->  HMS's in-session bypass stands, exactly as designed.
        //
        // (Restrictions we may WANT to lift later - mount actions in particular - are a separate case
        // and should be lifted explicitly here, not by leaving the whole gate open.)
        var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        var emoteMgr = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteManager.Instance();
        bool unlocked = uiState != null && uiState->IsEmoteUnlocked(emoteId);
        bool executable = emoteMgr == null || emoteMgr->CanExecuteEmote(emoteId);

        log.Debug("[HMSync] [EMOTEGATE] id=" + emoteId + " '" + label + "' unlocked=" + unlocked +
            " canExecute=" + executable + " canUse=" + usable +
            " mode=" + character->Mode + "/" + character->ModeParam);


        // Usable emote from a clean (non-looping) state → AgentEmote.ExecuteEmote, the in-game menu path: it
        // owns the full lifecycle and natively interrupts a prior one-shot (proven: /wave → /point). It is
        // NOT used to interrupt a LOOP: force-clearing the loop's emote state the same frame makes the agent's
        // execute silently no-op (the "looped → non-looped doesn't register" bug). Those go through the direct
        // play below, which survives the breaker (proven: forced-loop → forced-loop interrupts cleanly).
        // ── v0.7.395: congruency by default, better where we can ──────────────────────────────────
        // The game's own emote window shows each emote's legal conditions, and it is right: Gridanian
        // Gulp (301) is STANDING-ONLY. Its sheet row has slots 0 and 1 only - no posture variants at
        // all - so refusing it while seated is correct behaviour, not a bug. Confirmed in the live
        // game: it will not fire seated on a bench or on a chair, HMS session or not.
        //
        // But HMS can do better here, and it costs nothing. Both gulp timelines are
        // ActionTimeline.Slot=3 - the ADD lane, not the base lane. An additive animation LAYERS OVER a
        // held pose instead of replacing it, so direct-playing the standing drink while the sit pose
        // holds underneath produces a genuine seated drink. SE authored the motion on a lane that
        // composes; they simply never wired a seated entry point for it.
        //
        // The rule, derived from the sheet rather than hand-tuned per emote:
        //   in a posture AND the emote has NO posture variant  ->  direct play (layer it over the pose)
        //   otherwise                                          ->  the agent, which picks the right
        //                                                          posture slot when one exists
        // So 301 seated gains a seated drink, 301 standing is unchanged, and Drink Tea (239) - which
        // DOES ship a seated form, u_sp39 in slots 2/3/4 - keeps going through the agent so the game's
        // own stock variant is used rather than one we improvised.
        // ── v0.7.397: congruency. `/hms emote X` behaves like `/X`. ───────────────────────────────
        // The game's emote window publishes each emote's legal conditions and it is correct: Gridanian
        // Gulp (301) is STANDING-ONLY. Its sheet row carries slots 0 and 1 only - no posture variant -
        // and the live game refuses it from a bench or a chair, HMS session or not.
        //
        // Neither engine predicate detects this. CanUseEmote and CanExecuteEmote BOTH returned true for
        // 301 while seated, and the agent then stood the character up to play it. So the authority here
        // has to be the SHEET, which states the fact plainly: no variant in slots 2/3/4 means no seated
        // form exists.
        //
        //   in a posture AND no posture variant  ->  refuse, exactly as the slash command does
        //
        // ⚠ WE CAN DO BETTER THAN THIS, and it is written up - see
        // "Synthetic emotes via additive-lane compositing" (2026-07-23). These clips are
        // ActionTimeline.Slot=3 (Add lane), so driving them through PlayActionTimeline with NO SetMode
        // layers them over a held pose and yields a seated drink the game never shipped. That was built
        // in v0.7.396 and REMOVED here on purpose: it is local-only, because LocalStateDetector reads
        // TimelineIds[0] and Mode to recognise a one-shot and the layering path deliberately changes
        // neither, so peers see nothing. A synthetic emote only the performer can see is worthless for
        // RP. Re-landing it needs the emote id carried as an epoch event so the receiver can run the
        // same PlayActionTimeline - a scoped wire change, post-release, tracked as peer visibility.
        bool inPosture = character->Mode == CharacterModes.InPositionLoop;
        bool hasPostureVariant = false;
        {
            var esheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            var erow = esheet?.GetRowOrDefault(emoteId);
            if (erow != null)
            {
                var baseTl = erow.Value.ActionTimeline[0].RowId;
                for (int i = 2; i <= 4; i++)
                {
                    var v = erow.Value.ActionTimeline[i].RowId;
                    if (v != 0 && v != baseTl) { hasPostureVariant = true; break; }
                }
            }
        }

        log.Debug("[HMSync] [EMOTEGATE] id=" + emoteId + " mode=" + character->Mode + "/" +
            character->ModeParam + " inPosture=" + inPosture +
            " hasPostureVariant=" + hasPostureVariant);

        if (inPosture && !hasPostureVariant)
        {
            // Silent, deliberately. The native slash command says nothing when it refuses either, so a
            // chat line would be MORE noise than the behaviour we are replicating - and it would double
            // up once the compositing blend lands. The debug line above records it.
            return;
        }

        // Congruency gate for everything the engine itself declines (unlock is exempt - playing locked
        // emotes in-session is the deliberate RP bypass, per DoEmote's own note).
        if (unlocked && !executable)
        {
            chat.Print("[HMSync] " + label + " can't be used right now.");
            return;
        }

        if (usable && !interruptingLoop)
        {
            // addToHistory:false - scripted play, keep it out of the player's recent-emote history.
            agent->ExecuteEmote(emoteId, addToHistory: false);
        }
        else
        {
            // BREAKER: dismiss the running loop before replacing it - the same state reset the game does on
            // move. Clear the emote owner so the game stops re-stamping the mode from EmoteController, reset
            // the mode, release the base override. This is the receiver's proven move-cancel (ApplyEmoteState);
            // a bare SetMode(Normal) is overwritten next frame, leaving the old loop to resume.
            if (interruptingLoop)
            {
                character->EmoteController.EmoteId = 0;
                character->SetMode(CharacterModes.Normal, 0);
                // Force the mode FIELD to Normal too. LocalStateDetector reads character->Mode directly, and a
                // one-shot following this sets no mode of its own - so unless Mode reads Normal the detector
                // treats the new timeline as a sustained pose and never broadcasts it (peer misses the
                // interrupt). EmoteId=0 above stops the game re-stamping the loop, so this value holds.
                character->Mode = CharacterModes.Normal;
                character->ModeParam = 0;
                character->Timeline.BaseOverride = 0;
            }

            // Direct play - genuinely locked emotes, AND any emote interrupting a loop. Mirrors the receiver's
            // ApplyEmoteFromSheet.
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            var entry = sheet.GetRow(emoteId);

            // 318 "divine arm" etc. auto-draw the weapon (native /bm); while sheathed the animation no-ops.
            // Mirror the peer-side draw: flag + base-evict (34) + class-resolved draw flourish.
            if (entry.DrawsWeapon && !character->Timeline.IsWeaponDrawn)
            {
                character->Timeline.IsWeaponDrawn = true;
                character->Timeline.TimelineSequencer.PlayTimeline(34);
                character->Timeline.TimelineSequencer.PlayTimeline(LocomotionData.WeaponDraw);
            }

            var conditionMode = (CharacterModes)entry.EmoteMode.Value.ConditionMode;
            if (conditionMode != 0)
            {
                // Persistent emote → SetMode sustains the loop + drives mode-based sync + lets the game cancel
                // on movement. (Proven for forced-loop → forced-loop.)
                character->Timeline.BaseOverride = 0;
                character->SetMode(conditionMode, (byte)entry.EmoteMode.RowId);
                character->Timeline.IsWeaponDrawn = entry.DrawsWeapon;
                character->Timeline.TimelineSequencer.PlayTimeline(introTl > 0 ? introTl : loopTl);
            }
            else
            {
                // One-shot - from idle OR interrupting a loop (the breaker above already returned us to a
                // clean Normal state, so it's the same case either way).
                //
                // v0.7.398 - POSTURE-AWARE SLOT SELECTION.
                // This branch used to play ActionTimeline[0] unconditionally: the STANDING clip. For an
                // emote with a stock seated form that is simply wrong, and it is why `/hms emote 239`
                // (Drink Tea) stood the character up - 239 ships 8058 `sp39` standing in slot 0 and 8064
                // `u_sp39` seated in slots 2/3/4, and only slot 0 was ever read.
                //
                // Why this branch and not the agent: the agent refuses emotes the character has not
                // unlocked, which is exactly the RP case HMS exists to serve. So the bypass has to do the
                // posture selection itself - the agent is not available to do it for us.
                //
                //   ModeParam is the EmoteMode row: 1 = Sit on Ground -> slot 2 (j_)
                //                                   2 = Sit (chair)   -> slot 3 (s_)
                //   Mounted                                           -> slot 4 (u_)
                // Slot 4 is the general upper-body fallback, so an emote carrying only that still resolves.
                ushort postureTl = 0;
                if (inPosture || character->Mode == CharacterModes.Mounted)
                {
                    int slot = character->Mode == CharacterModes.Mounted ? 4
                             : character->ModeParam == 1 ? 2
                             : character->ModeParam == 2 ? 3
                             : 4;
                    postureTl = (ushort)entry.ActionTimeline[slot].RowId;
                    if (postureTl == 0) postureTl = (ushort)entry.ActionTimeline[4].RowId;
                    if (postureTl == loopTl) postureTl = 0;   // no distinct variant - nothing gained
                }

                if (postureTl != 0)
                {
                    // The seated/mounted variants are authored to COMPOSE over the held pose, so they go
                    // on the ACTION lane. PlayTimeline would land them in TimelineIds[0] and evict the
                    // pose - the same base-lane mistake that produced the cpose regression in the visor
                    // arc, and the same one standing the character up here.
                    //
                    // ⚠ TRADE-OFF, deliberate: LocalStateDetector recognises a one-shot by reading
                    // TimelineIds[0], which this path does not touch, so the seated form is LOCAL-ONLY.
                    // Before this change the emote synced - but synced the WRONG animation, having first
                    // stood the character up. Correct-and-unsynced beats wrong-and-synced for a pose the
                    // player is deliberately holding. Syncing it properly is the same epoch-carried
                    // emote-id wire change already scoped post-release for the compositing work; both
                    // land together.
                    character->Timeline.PlayActionTimeline(postureTl);
                    log.Debug("[HMSync] [EMOTEGATE] posture variant tl=" + postureTl +
                        " (base " + loopTl + ") mode=" + character->Mode + "/" + character->ModeParam);
                }
                else
                {
                    // PlayTimeline both plays the animation AND lands the id in
                    // TimelineSequencer.TimelineIds[0] - the exact field LocalStateDetector reads to
                    // recognise and broadcast a one-shot, so the peer gets it. (PlayActionTimeline(tl,34)
                    // plays the same animation on the ACTION lane, leaving TimelineIds[0] unchanged, so the
                    // detector never saw it and the peer missed the interrupt. The receiver only needs the
                    // action lane because IT doesn't clear the mode first; we do.)
                    character->Timeline.TimelineSequencer.PlayTimeline(loopTl);
                }
            }
        }
    }

    // ── Relay events (background thread → main thread) ──

    private void OnPeerJoined(string peerId, ulong contentId, string characterName)
    {
        RunOnMainThread(() =>
        {
            // Register in the roster immediately so the participant list reflects the peer in the LOBBY (pre-load) -
            // the roster used to be built only from the transform stream, which no longer flows before zone-load. The
            // relay now stamps identity (ContentId + CharacterName) on PeerJoined, so a co-located lobby peer resolves
            // to their real character (name/world/FC) at once via RegisterPeer's ContentId bind. entityId is derived on
            // resolve, so 0 here is fine; the transform path later updates this same entry in place (preserving order).
            stateApply.RegisterPeer(peerId, contentId, 0, characterName);

            // S327: binding is now driven by the transform stream (each peer's transforms carry their stable ContentId;
            // StateApplyService creates the peerInfos entry and the per-frame resolve loop binds by ContentId once the
            // character is in render range). So we no longer guess a body from the object table here - that was the
            // fragile positional/name path (the residential-district misattribution). Just announce the connection; the
            // participant list will fill in the name once the peer's identity resolves.
            chat.Print("[HMSync] " + (string.IsNullOrEmpty(characterName) ? "A peer" : characterName) + " connected to the session.");

            // S331 (late-join signal fix): when a peer joins, re-send our FULL current state once so the newcomer
            // catches up on everything set-once-static - our Moniker name, held emote/cpose stance, mount, minion,
            // ornament, weapon-drawn, and (if we're the host) map-state. WARM/COLD are strictly change-gated (no
            // heartbeat), so anything we set BEFORE they joined is invisible to them without this re-offer. EVERY peer
            // does this (not just the host) - a guest's appearance/pose/mount matters to the newcomer too. Existing
            // peers are unaffected (one-shots are epoch-gated, level-states are idempotent). Also re-send the zone
            // (host only) so the newcomer loads the right map - see RequestZoneResend below.
            stateCapture.RequestFullResend();
            if (relay.IsHost) RequestZoneResend();
        });
    }

    // S331 (late-join signal fix): the host re-broadcasts the CURRENT zone-load so a newcomer loads the right map.
    // Zone-load is a one-shot EVENT (ZoneLoadExecute), not lane state, so it isn't caught by the WARM/COLD/HOST
    // re-send - it needs its own re-offer. The receiver skip-guard (OnZoneLoadReceived) makes this safe to broadcast:
    // a peer ALREADY in the target zone ignores it (no disruptive reload), while the newcomer (not yet there) loads it.
    private void RequestZoneResend()
    {
        if (!relay.IsHost) return;
        uint territory = zoneLoad.CurrentLoadedZone;
        if (territory == 0 || !zoneLoad.IsZoneLoaded) return;   // no zone loaded → nothing to catch up

        // Re-resolve the spawn the same way DoLoad did (user spawn for this territory, else curated/LGB spawn).
        System.Numerics.Vector3 spawn;
        if (config.UserSpawns.TryGetValue(territory, out var us) && us.Length >= 3)
            spawn = new System.Numerics.Vector3(us[0], us[1], us[2]);
        else
            spawn = zoneLoad.ResolveSpawnPoint(territory);

        // v0.7.332: carry the stage bg + name for a cutscene so a late-joiner runs the donor-load-with-bg-swap too.
        string rsStageBg = zoneLoad.ActiveStageBg ?? "";
        string rsStageName = "";
        if (rsStageBg.Length > 0)
            foreach (var st in cutscene.Stages) if (st.Bg == rsStageBg) { rsStageName = st.Name; break; }

        _ = relay.SendZoneLoad(new ZoneLoadData
        {
            TerritoryId = territory,
            SpawnX = spawn.X, SpawnY = spawn.Y, SpawnZ = spawn.Z,
            StageBg = rsStageBg,
            StageName = rsStageName,
        });
        log.Information("[HMSync] Re-sent zone-load (territory " + territory + ") to catch up a joining peer.");
    }

    private void OnPeerLeft(string peerId)
    {
        RunOnMainThread(() =>
        {
            // S322: announce by name (was a nameless "Peer disconnected."). Grab the name before unregister.
            string who = "A peer";
            if (stateApply.Peers.TryGetValue(peerId, out var info))
            {
                if (!string.IsNullOrEmpty(info.CharacterName)) who = info.CharacterName;
                if (info.ObjectIndex.HasValue)
                {
                    actorVisibility.UnregisterPeer(info.ObjectIndex.Value);
                    moniker.ClearName(info.ObjectIndex.Value);   // S328x: restore the departing peer's real nameplate
                }
            }
            stateApply.UnregisterPeer(peerId);
            chat.Print("[HMSync] " + who + " left the session.");
        });
    }

    // The current host's relay PeerId - tracked so the roster can pin + amber-tint the host on everyone's screen. Set
    // to self when we hold host and updated on explicit HostTransfer; succession-on-leave and fresh joins fall back to
    // first-by-join-order in BuildParticipantList.
    private string currentHostPeerId = "";

    private void OnHostTransfer(string newHostId)
    {
        RunOnMainThread(() =>
        {
            currentHostPeerId = newHostId;
            if (newHostId == relay.LocalPeerId)
            {
                // S330c (Stage 2b): INHERIT map-state on promotion. Before becoming host, this client was a guest
                // applying the old host's HostUpdates. If we now stamp our OWN MapState (which is default/empty - we
                // never authored map-state as a guest), our first HostUpdate would wipe the scene's weather/time/BGM
                // AND restart the epoch. So seed our MapState from the last map-state we APPLIED - same values, and
                // the epoch continues from where the old host left off (our next change is inheritedEpoch+1).
                var inherited = stateApply.LastAppliedMapState;
                if (inherited != null)
                {
                    stateCapture.MapState.WeatherId = inherited.MapWeatherId;
                    stateCapture.MapState.TimeForced = inherited.MapTimeForced;
                    stateCapture.MapState.EorzeaHour = inherited.MapEorzeaHour;
                    stateCapture.MapState.EorzeaMinute = inherited.MapEorzeaMinute;
                    stateCapture.MapState.BgmId = inherited.MapBgmId;
                    stateCapture.MapState.RemoveNpcs = inherited.MapRemoveNpcs;
                    stateCapture.MapState.HideQuestSigns = inherited.MapHideQuestSigns;
                    stateCapture.MapState.Epoch = inherited.MapStateEpoch;   // continue the sequence, don't restart
                    log.Information("[HMSync] Inherited map-state on host promotion (epoch " + inherited.MapStateEpoch + ").");
                }
                relay.IsHost = true;
                chat.Print("[HMSync] You are now the host. Use /hms load to change zones.");
            }
            else
            {
                // v0.7.431 - DEMOTION: the missing half of the transfer. If we currently hold host and
                // the role is moving to someone else, drop authority. Everything downstream (GUI host
                // view, host-command guards, weather/BGM authority via HasMapAuthority, CanLoad) reads
                // relay.IsHost live, so they revert to guest behaviour the instant this flips - no
                // per-consumer plumbing. Pure authority swap: no map movement, we keep the zone we're on.
                // Symmetric with the promotion branch above. Also reset the map-state apply gate: as the
                // prior host our lastAppliedMapEpoch tracked our OWN outbound epoch, and the new host's
                // epoch continues that sequence - so its next change could land at an epoch we already
                // equal and get ignored, leaving our scene stale. ForceMapStateReapply() re-opens the
                // gate so we mirror the new host's next broadcast.
                if (relay.IsHost)
                {
                    relay.IsHost = false;
                    stateApply.ForceMapStateReapply();
                    log.Information("[HMSync] Demoted from host on transfer - now a guest, mirroring the new host.");
                }

                var shortId = newHostId.Length >= 6 ? newHostId[..6] : newHostId;
                // Try to find peer name
                string hostName = shortId;
                foreach (var (id, info) in stateApply.Peers)
                {
                    if (id == newHostId)
                    {
                        hostName = info.CharacterName;
                        break;
                    }
                }
                chat.Print("[HMSync] Host transferred to " + hostName + ".");
            }
        });
    }

    private void OnRoomJoined(RoomJoinedData data)
    {
        RunOnMainThread(() =>
        {
            log.Information("[HMSync] Room joined. Zone=" + data.CurrentZoneId + " Peers=" + data.PeerIds.Length);

            // Fresh room → drop any roster that survived (e.g. an unclean disconnect that skipped teardown). The
            // fanned PeerJoined frames that populate this room arrive AFTER RoomJoined, so clearing here is safe.
            stateApply.ClearRoster();
            currentHostPeerId = relay.IsHost ? relay.LocalPeerId : "";   // self if we host; else fall back to join order

            // Admission confirmed by the relay (not just connect-ok) - announce the lobby HERE so a refusal
            // (wrong password / no host nearby / already hosting) never prints a false "joined". Host vs peer per
            // the relay's authoritative IsHost.
            if (relay.IsHost)
            {
                ui.SetStatus("Hosting");
                chat.Print("[HMSync] Lobby open. Gather your party, then load a zone to start the scene.");
            }
            else
            {
                ui.SetStatus("Joined");
                chat.Print("[HMSync] Joined. Waiting for the host to load a zone.");
            }

            // S327: no positional pairing here anymore. The old code walked the object table and paired the Nth nearby
            // player with the Nth relay PeerId - random (the orderings are unrelated), stranger-polluted, and it dropped
            // unmatched PeerIds when fewer characters were loaded than peers (exactly the cross-map case). Binding is now
            // driven by the transform stream: each peer's transforms carry their stable ContentId, StateApplyService
            // creates the peerInfos entry, and the per-frame resolve loop binds by ContentId once the character is in
            // render range. Visibility is registered at bind time in the resolve loop, not here.

            // Auto-load zone if room has one active
            if (data.CurrentZoneId > 0)
            {
                var zoneName = zoneLoad.GetZoneName(data.CurrentZoneId);
                chat.Print("[HMSync] Auto-loading zone: " + zoneName);

                var peerIndices = stateApply.GetPeerObjectIndices();
                zoneLoad.LoadZone(data.CurrentZoneId, peerIndices,
                    new FFXIVClientStructs.FFXIV.Common.Math.Vector3(data.SpawnX, data.SpawnY, data.SpawnZ));
                actorVisibility.Refresh();
                // S328ab: force the host's map-state (weather/time/BGM) to re-apply once THIS join's zone finishes
                // loading. Without this, the host's map-state epoch arrives while the joiner's zone is still loading -
                // ApplyMapState stores the values but SKIPS the live write (gated on IsZoneLoaded) yet still marks the
                // epoch consumed, so it never re-fires once loaded → the joiner is stuck on the real zone's weather
                // (None/atmospheric) and clock. The mid-session host-load path already did this; the JOIN path didn't.
                guestMapReapplyCountdown = 120;
                lastAppliedPeerBgm = 0;   // new zone → let its BGM re-apply fresh, not stale

                ui.SetStatus("Zone: " + zoneName);
            }
        });
    }

    private void OnZoneLoadReceived(ZoneLoadData data)
    {
        RunOnMainThread(() =>
        {
            // S331 (late-join signal fix): skip-guard. The host re-broadcasts zone-load when a peer joins (so a
            // latecomer loads the right map). But that broadcast reaches EVERYONE - and a peer already in the target
            // zone must NOT reload (a disruptive flash + re-spawn). If we're already loaded into this territory, this
            // is a redundant catch-up re-send meant for the newcomer, not us - ignore it. The newcomer (not yet in the
            // zone) falls through and loads it. This is what makes the join-time re-broadcast safe to fan to the room.
            // v0.7.332: cutscene stages load via a DONOR territory with a bg-path swap. data.TerritoryId is the donor;
            // data.StageBg (when set) is the actual stage. A stage-aware skip-guard: only skip as a redundant re-send if
            // we're in the same territory AND the same stage (else a cutscene sharing the donor of our current zone, or a
            // different stage on the same donor, would be wrongly skipped).
            bool isStage = data.StageBg.Length > 0;
            if (zoneLoad.IsZoneLoaded && zoneLoad.CurrentLoadedZone == data.TerritoryId
                && (zoneLoad.ActiveStageBg ?? "") == (isStage ? data.StageBg : ""))
            {
                log.Information("[HMSync] Ignoring zone-load for territory " + data.TerritoryId + " - already loaded (catch-up re-send for a newcomer).");
                return;
            }

            // Print the REAL name - the stage name for a cutscene (the donor's GetZoneName was the "Ingleside Apartment"
            // lie), the territory name otherwise.
            string zoneName = isStage && data.StageName.Length > 0 ? data.StageName : zoneLoad.GetZoneName(data.TerritoryId);
            chat.Print("[HMSync] Host loading zone: " + zoneName);

            // Guest goes synthetic before loading the host's zone - same filter-first-then-load invariant as the host.
            if (!EngageSyntheticSession()) return;

            // v0.7.332: for a cutscene, arm the same bg-swap the host used - set BOTH stage fields before the donor load
            // so the guest's CreateScene detour substitutes the stage bg (mirrors CutsceneStageService.LoadStage).
            if (isStage)
            {
                zoneLoad.ActiveStageBg = data.StageBg;
                zoneLoad.PendingStageBg = data.StageBg;
            }

            var peerIndices = stateApply.GetPeerObjectIndices();
            zoneLoad.LoadZone(data.TerritoryId, peerIndices,
                new FFXIVClientStructs.FFXIV.Common.Math.Vector3(data.SpawnX, data.SpawnY, data.SpawnZ));
            actorVisibility.Refresh();
            // S327j: the guest just loaded a fresh map. Force the host's current map-state (esp. a HELD time) to
            // re-apply on the next transform - otherwise the new map runs on the real clock until the host next edits
            // time. Deferred a little so the apply lands after the load settles.
            guestMapReapplyCountdown = 120;
            lastAppliedPeerBgm = 0;   // S327l: new zone → clear BGM tracking so its music re-applies fresh (not stale)
            ArmMapReveal();           // v0.7.448: guest reveals its own HUD fog for the loaded map (local; restored on exit)

            ui.SetStatus("Zone: " + zoneName);
        });
    }

    private void OnSessionEnded()
    {
        RunOnMainThread(() =>
        {
            chat.Print("[HMSync] Host ended the session.");
            DoLeaveInternal(silent: true);
            ui.SetStatus("Session ended by host");
        });
    }

    // v0.7.464: when OnRelayError announces a hard throttle, the close that follows must not print a second,
    // vaguer line about the same event. This is a TIMESTAMP, not a bool latch, and that is deliberate:
    // Error(9) → DoLeaveInternal → relay.Disconnect(), and Disconnect() clears IsConnected SYNCHRONOUSLY, so the
    // receive loop's `if (IsConnected)` tail guard is already false and OnDisconnected NEVER FIRES on that path.
    // A bool would therefore latch true forever and silently swallow the message for the next genuine drop. A
    // timestamp self-expires whether or not the clearing path ever runs - harmless by construction, not by
    // remembering to reset.
    private DateTime throttleAnnouncedAt = DateTime.MinValue;
    private bool ThrottleJustAnnounced => (DateTime.UtcNow - throttleAnnouncedAt).TotalSeconds < 10;

    private void OnDisconnected()
    {
        RunOnMainThread(() =>
        {
            // v0.7.464 (RMS QA F3, hard tier): a close carrying 4029 is a throttle disconnect, not a network drop -
            // say so, because "lost connection" sends the user hunting a fault that isn't there. The close code is
            // only present when the relay closed CLEANLY; an aborted socket leaves it null and we fall back to the
            // generic line rather than guessing.
            bool hardThrottle = relay.LastCloseCode == HMSync.Wire.WireClose.RateLimitExceeded;
            if (!ThrottleJustAnnounced)
            {
                if (hardThrottle)
                    chat.PrintError("[HMSync] Disconnected - the relay throttled this connection for excessive traffic. " +
                                    "You can reconnect when ready.");
                else
                    chat.Print("[HMSync] Lost connection to relay.");
            }
            // S193: a hard disconnect must still clear every imposed peer state, or mounts (and
            // future transients) leak - the puppets are still in our local object table even
            // though the relay connection dropped. This was the "mount persists through dc" gap:
            // OnDisconnected printed a message but never ran the sweep. DoLeaveInternal handles
            // the full teardown (sanitize + revert + filter-off + state services), idempotently.
            DoLeaveInternal(silent: true);
            ui.SetStatus("Disconnected");
        });
    }

    // v0.7.464 (RMS QA F3, soft tier). The relay is dropping our excess ingress but keeping the socket open, so this
    // is advisory ONLY - no teardown, no state change, nothing the user must do. Two surfaces, deliberately:
    //   • the status strip lights an amber line for 5 s (re-armed by each notice, so it stays lit through a burst);
    //   • chat gets ONE line at most every 30 s, because the relay may emit up to ~1/3 s and a per-notice print
    //     would bury the log - the sin the diagnostics-noise pass exists to prevent.
    private DateTime lastThrottleChatPrint = DateTime.MinValue;

    private void OnRelayRateLimited()
    {
        RunOnMainThread(() =>
        {
            ui.NoteThrottled();
            if ((DateTime.UtcNow - lastThrottleChatPrint).TotalSeconds < 30) return;
            lastThrottleChatPrint = DateTime.UtcNow;
            chat.Print("[HMSync] The relay is throttling this connection - some updates are being dropped. " +
                       "The session continues; this clears on its own.");
        });
    }

    private void OnRelayError(uint code, string relayMsg)
    {
        RunOnMainThread(() =>
        {
            // v0.7.464: switched from magic integers to the shared HMSync.Wire.ErrCode constants. The codes used to
            // live ONLY in the relay's Program.cs while this switch re-implemented them as bare literals - the one
            // thing both sides must agree on was the one thing the compiler-enforced contract didn't cover. ErrCode
            // now lives in HMSyncWireTypes.cs alongside WireKind, so a code added on one side is visible on the other.
            string msg = code switch
            {
                HMSync.Wire.ErrCode.RoomNotFound   => "That room has ended.",
                HMSync.Wire.ErrCode.NotHosting     => "No one nearby is hosting a room - you must be in visual range of the host.",
                HMSync.Wire.ErrCode.RoomFull       => "That room is full.",
                HMSync.Wire.ErrCode.NotHost        => "Only the host can do that.",
                HMSync.Wire.ErrCode.Kicked         => "You were removed from the room.",
                HMSync.Wire.ErrCode.Banned         => "You have been removed from this room.",
                HMSync.Wire.ErrCode.WrongPassword  => "Incorrect room password.",
                HMSync.Wire.ErrCode.AlreadyHosting => "You already host a live room - leave it first.",
                // HARD throttle: the relay closes the socket immediately after this. Distinct from the SOFT tier
                // (WireKind.RateLimited 0x08), which is advisory and leaves the session running.
                HMSync.Wire.ErrCode.RateLimited    => "The relay is throttling this connection for excessive traffic - " +
                                                     "the session has ended. You can reconnect when ready.",
                _ => string.IsNullOrEmpty(relayMsg) ? "Relay error." : relayMsg,
            };
            chat.Print("[HMSync] " + msg);

            // The close frame arrives moments after this - and on this path DoLeaveInternal disconnects us first, so
            // OnDisconnected may not run at all. Either way the user gets one message for one cause.
            if (code == HMSync.Wire.ErrCode.RateLimited) throttleAnnouncedAt = DateTime.UtcNow;

            // NotHost is action-refused, NOT session-ending - stay connected. Everything else tears down to idle:
            // a refused join leaves us connected-but-roomless on the relay, and Kicked is a hard close on their side.
            // We never auto-retry - retrying into a ban would just bounce back as Banned.
            //
            // ⚠ NOTE FOR ANY FUTURE ERROR CODE: this fallthrough means an UNRECOGNISED code tears the session down.
            // That is the right default for a refusal channel, but it makes ErrorPayload unusable for advisory
            // signals - a new advisory must ride a new WireKind (which an older client ignores), not a new code.
            if (code != HMSync.Wire.ErrCode.NotHost)
            {
                DoLeaveInternal(silent: true);
                ui.SetStatus("");
            }
        });
    }

    // ── Helpers ──

    private static readonly System.Random pwRng = new System.Random();

    // A short, shareable room password when the host leaves the field blank. 5 chars from an unambiguous alphabet
    // (no 0/O/1/l/I) - easy to read out, and unique enough for a nearby lobby (presence is the real gate, not entropy).
    private static string GenerateShortPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var sb = new System.Text.StringBuilder(5);
        for (int i = 0; i < 5; i++) sb.Append(alphabet[pwRng.Next(alphabet.Length)]);
        return sb.ToString();
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        commands.RemoveHandler("/hms");
        try { mountHudDismount.Dispose(); } catch { }   // v0.7.339: drop the mount-icon click handler + listeners
        try { deckFloor.Clear(); } catch { }   // v0.7.351: remove any box floor patches
        try { skillSync.Dispose(); } catch { }   // COSM_1_016: drop the UseAction hook
        pluginInterface.UiBuilder.Draw -= ui.Draw;
        pluginInterface.UiBuilder.Draw -= ui.DrawCarpetOverlay;
        pluginInterface.UiBuilder.Draw -= ui.DrawCarpetBar;
        pluginInterface.UiBuilder.Draw -= ui.DrawFaceControlBar;   // v0.7.461 (P2, Codex QA): was added (480) but never removed - stale callback on reload
        pluginInterface.UiBuilder.Draw -= ui.DrawMovementBar;       // v0.7.465: paired with the += above
        pluginInterface.UiBuilder.Draw -= ui.DrawAppearanceBar;     // v0.7.465: paired with the += above
        pluginInterface.UiBuilder.OpenMainUi -= ui.OpenMain;        // paired with the += above
        pluginInterface.UiBuilder.OpenConfigUi -= ui.OpenConfig;    // paired with the += above

        try { mapSettings.DisableTimeOverride(); } catch { }   // S326v: don't leave the clock frozen on unload

        // v0.7.448: restore any auto-revealed maps on plugin unload too (graceful disable mid-session). The
        // crash-recovery file would catch this on next load regardless, but sanitising here cleans up now.
        try { SanitiseRevealedMaps(); } catch (Exception ex) { log.Warning("[HMSync] map-reveal sanitise on dispose failed: " + ex.Message); }

        // Safety: revert zone if loaded to prevent character getting stuck
        if (zoneLoad.IsZoneLoaded)
        {
            try { zoneLoad.Revert(); }
            catch (Exception ex) { log.Error("[HMSync] Safety revert failed: " + ex.Message); }
        }

        if (packetFilter.IsActive)
        {
            try { packetFilter.Disable(); }
            catch (Exception ex) { log.Debug("[HMSync] Packet filter disable failed: " + ex.Message); }
        }

        relay.OnTransformReceived -= stateApply.OnTransformReceived;
        relay.OnPeerJoined -= OnPeerJoined;
        relay.OnPeerLeft -= OnPeerLeft;
        relay.OnHostTransfer -= OnHostTransfer;
        relay.OnRoomJoined -= OnRoomJoined;
        relay.OnZoneLoadReceived -= OnZoneLoadReceived;
        relay.OnSessionEnded -= OnSessionEnded;
        relay.OnDisconnected -= OnDisconnected;
        relay.OnError -= OnRelayError;
        relay.OnRateLimited -= OnRelayRateLimited;   // v0.7.464: symmetry (the DrawFaceControlBar leak's lesson)

        stateCapture.Dispose();
        stateApply.Dispose();
        emoteDiag.Dispose();
        actorVisibility.Dispose();
        glamourer.Dispose();
        afkSuppressor.Dispose();
        packetFilter.Dispose();
        sayFilter.Dispose();
        cutscene.Dispose();
        zoneLoad.Dispose();
        relayHealth.Dispose();   // stop the background /health poll
        noclip.Dispose();
        timeFreeze.Dispose();   // S327f: unfreeze + dispose the time hook
        carpet.Dispose();
        moniker.Dispose();   // S328x
        npcVisibility.Dispose();   // S328aa
        relay.Dispose();

        log.Information("[HMSync] Plugin unloaded");
    }
}
