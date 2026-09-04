# Changelog

All notable changes to HMapSync (HMS) are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Lights-out**: darken a map's atmosphere for your whole session. `/hms stagelights` extinguishes the ambient map lights, and `/hms vfxoff` hides every visual effect on the map (flames, weather, effect props); `/hms vfxlist` previews what would be hidden without changing anything. Anyone in the session can toggle it with everyone seeing the same result. Turning a toggle back off, or ending the session, restores everything.

## [1.0.1.5] - 2026-08-30

### Added

- **Custom names in chat**: your session's custom Moniker names can now appear in the chat log too, not only on nameplates. Say, yell, shout, and /em emotes from you and other session members show the Moniker name so chat matches the plate. On by default (a "Use custom names in chat" toggle under Moniker in the Modules panel turns it off); requires the Moniker plugin.
- **Possession sync (HDM)**: when a player drives a possessed NPC through the HDM disguise module, everyone in the session now sees that NPC move and animate, and the driver's own hidden body stays hidden for the whole group. Requires HDM.
- **Frozen poses sync (HDM)**: freezing a disguise or puppet in place through HDM now holds it in the same pose for everyone in the session. Requires HDM.

### Fixed

- **Disguise returns to normal when a session ends (HDM)**: ending or leaving a session while wearing an HDM disguise now restores your character's normal size and height straight away, instead of leaving it shrunk or floating until you reloaded the area or relogged. Requires HDM.

## [1.0.1.4] - 2026-08-26

### Added

- **Sync custom names in the lobby**: shows each other's custom Moniker names while gathered together before anyone loads a map, not only once inside a loaded map. On by default (a setting under Moniker in the Modules panel lets you turn it off); requires the Moniker plugin.

### Fixed

- **Hidden status icon now syncs**: if you use Moniker's option to hide the status icon on your nameplate, other players in your session now see it hidden too, matching the name, title, and Free Company tag options that already synced.
- **Weather-preset sections no longer jump the view**: opening **Extra presets** or **City sky variants** in Map Control could snap the tab back to the top; they now expand in place.

## [1.0.1.3] - 2026-08-25

### Fixed

- **Summoned puppets are visible to other players again**: a companion you spawn in a session now appears for everyone in the lobby, not only once you disguise it. A recent change left the "spawn" and "remove" signals for a puppet indistinguishable on the wire, so peers treated every spawn as a removal and never showed the puppet; the two are now told apart explicitly.
- **Weather no longer resets when the host changes**: after the host role passed to another player, the sky could snap back to the zone default and stop following the new host until they picked a fresh preset; the scene now keeps tracking the new host across a handoff.
- **Tidier weather-preset headers**: the **Extra presets** and **City sky variants** collapsibles in Map Control no longer extend slightly past the panel's right edge; their frames now line up with the controls beneath them.

## [1.0.1.2] - 2026-08-24

### Added

- **Keep a session open with no map loaded**: a hosted lobby can now stay live for open-world roleplay without loading a virtual zone first.
- **HDM groundwork**: adds the disguise-sync and accent handshake the upcoming HDM module builds on, plus its entry in the modules list; inert until HDM is installed.

## [1.0.1.1] - 2026-08-23

### Changed

- **See which weather preset is currently live**: the **Extra presets** and **City sky variants** grids in Map Control now highlight the sky you have applied right now, so the active pick stands out from the others that are merely available to choose.

### Fixed

- **Pose changes now sync on the first try**: cycling your standing or sitting pose (`/cpose`) sometimes did not reach other players until you repeated it; the first change now broadcasts reliably.
- **Matching sky on instanced zones**: in some instanced zones (for example Magna Glacies), a synced player could show a different sky than the host even when both were on the zone's own weather; synced players now match the host's native sky, and a chosen weather preset is only imposed when the host actually picks one.
- **Relay key status is correct after a restart**: a valid key could show as "No key" until you re-entered it; the plugin now re-checks your saved key on launch so the status reflects reality without any manual step.

## [1.0.1.0] - 2026-08-21

### Added

- **Cram any weather onto any map**: the weather picker in Map Control gains an **Extra presets** library — an alphabetised grid of weathers you can apply to the current zone even when the map doesn't naturally carry them, drawing in a foreign sky and, where it's safe to, the ambient effects that go with it. Weathers the map *does* carry but never randomly rolls now surface too, grouped under **This map's states**.
- **Skies that move with the time of day, shared across a session**: crammed city weathers marked with an asterisk (`*`) now travel the sun through a full day instead of holding a single fixed moment — and the moving sky is synced, so every member of a session watches the same city dusk roll in at the same pace, not only the host. A companion **City sky variants** section offers the same weather as it looks in different cities — a Limsa, Kugane, or Gridania sky, for instance — each cycling through that city's day, synced to the group when you host.
- **Set weather from chat**: `/hms setweather <id>` applies any weather to the current zone by number, whether it's native to the map or crammed in from elsewhere, and shares it with the session when you're hosting.
- **Roam through invisible barriers**: the invisible blockers that fence off boss arenas, dungeon sections, quest-phase areas, and NPC pens are now lifted automatically on every map you load, letting you walk straight through them with no command. Only the invisible walls are dropped — the scenery stays visible and floors are never affected. Turn it off for the session with `/hms dropcolliders off` (`on` puts them back and re-enables it).

### Changed

- **Locked emotes trigger the normal way in a session**: emotes you haven't unlocked now play from the emote menu, from their `/emote` text command, and from hotbar macros — not only from `/hms emote`. Locked and owned emotes now respond identically to these native triggers during a session, and both are visible to other members. Emotes that are genuinely unusable in the moment — a standing-only emote while seated, say — are still correctly refused.
- **Peaceful Solution Nine and Tuliyollal**: loading Solution Nine or Tuliyollal now hides the lingering event and post-battle debris, so both cities read clean and calm on a free-roam visit, in line with the other decluttered zones.

## [1.0.0.9] - 2026-08-08

### Added

- **More explorable cutscene stages, discovered automatically**: HMS now finds cutscene-only locations from the game's own data rather than a hand-kept list, so stages added by a patch surface on their own each update. New this release: the Dawntrail title-screen vista, the Everkeep server room, the Cosmic Exploration capsule, and the Scion's ship from the Endwalker intro.
- **Switch between a cutscene stage's alternate looks**: a few cutscene stages exist in more than one form — for example a ruined and a restored version of the same set. `/hms stagestate` flips between them (call it again to cycle, or name the look you want). Every such stage still loads clean on its own; this is just for manually switching to the other composition.

### Fixed

- **Cutscene stages no longer shimmer as they load**: a handful of cutscene stages that flickered or z-fought on entry (such as the Doma throne-room finale) now come up clean, with the correct version of overlapping set pieces shown by default.
- **Cutscenes load with their intended sky**: cutscene stages now show their own authored weather instead of sometimes coming up under a blank, atmospheric "none" sky (which could depend on where you launched from). Everyone in a session sees the same sky.
- **Cutscene stages now identify themselves like any zone**: the zone header and `/hms status` now show the cutscene stage's own name and tag instead of the internal map it borrows to load, so loading a cutscene reads the same as loading a normal zone.

### Changed

- **New and tidied cutscene stages**: added a Cosmic Exploration stage and a dedicated Seaship stage (also listed under Seaships alongside the other sailing locations), and cleaned up a PvP stage's name.
- **Magna Glacies boss-arena declutter**: the "do not cross" border curtain in the Magna Glacies boss arena is now hidden on free-roam, matching the other decluttered arenas.

## [1.0.0.8] - 2026-08-06

### Added

- **Two story zones now load already finished**: virtual-loading Doma Enclave or Elysion now presents the map the way it looks once its questline is complete, rather than mid-progress. This applies to these two maps specifically. Doma Enclave also arrives with its streets populated by the end-state residents, and both load automatically with no extra step and no visible rebuild flicker.
- **Pin favourite cutscene stages**: cutscene stages can now be starred just like zones. Favourited stages pin to the top of the list with a filled star, matching how pinned zones already behave.

### Fixed

- **Terncliff loading**: fixed a loading issue with Terncliff so the zone now comes up correctly.
- **Cosmic Exploration spawn point (Auxesia)**: entering Auxesia now places you on the ground toward the center of the area, instead of dropping you into mid-air off to one side.

## [1.0.0.7] - 2026-08-04

### Added

- **Live join and leave**: session members who arrive or leave partway through a session are now handled live. A late joiner is bound and driven as soon as they join, and a member who leaves no longer leaves a lingering frozen copy behind on other members' screens. HMS also re-learns the game's player spawn and despawn packets automatically if a game patch shifts them, so this keeps working across patches without waiting for a plugin update.

### Fixed

- **Facing preserved on session end**: when a session ends, members are handed back facing their true direction instead of snapping to the last synced heading.

## [1.0.0.6] - 2026-08-04

### Added

- **Teleport to a session member**: right-click a participant in the lobby list and choose "Teleport to" to jump straight to their live position, handy on large maps where it's easy to lose the group. It's a private, local-only move (no host control, no relay traffic), and the option is disabled for anyone not currently visible to you.

### Fixed

- **Leaving or ending a session while airborne no longer strands a frozen body**: a member who disconnects, is kicked, or stops the session while falling, flying, or noclipping is now returned to solid ground and reset to a neutral idle before their character unloads. Other members could previously see them frozen mid-fall or left hanging in the air.
- **Seated members stand up when the group changes maps**: hopping to a new map no longer drops a seated participant into a stuck seated pose above the ground on everyone else's screen; they now stand as the new map loads.
- **Weather matches for everyone after a map change**: the host now lets the new map's sky settle before sharing it, so every member sees the same weather. Previously a mistimed read right after loading could briefly hand peers a stale or generic sky (often plain fair skies) that didn't match the host's.

## [1.0.0.5] - 2026-08-02

### Added

- **Granular NPC hide per map**: available in the Map Control tab. Hides selected NPC(s) for you and HMS peers. Hide settings persist between sessions.

### Fixed

- **Version stamp**: the version in the main window now reflects the actual build number.

## [1.0.0.4] - 2026-08-02

### Fixed

- **Session /say, /yell and /shout no longer vanish for members using name mods**: spatial chat from a session member is now matched by the speaker's underlying character identity rather than the displayed chat name. Members running a nameplate or name-prefix mod (e.g. one that prepends a class abbreviation) had all their proximity chat wrongly hidden while in a session; it now shows correctly.

### Changed

- **Guest map state moved to the Map Control tab**: for session guests, the read-only zone / weather / time / music readout now lives under Map Control alongside the Spawn point panel, instead of the Session tab — keeping all map-related information in one place.

## [1.0.0.3] - 2026-08-01

### Added

- **Teleport forward**: a new button in the Spawn point panel propels you a set distance in the direction you're facing (distance is editable), for quickly crossing gaps or punching through geometry without typing coordinates.
- **Spawn point & teleport for session guests**: peers now get the same Spawn point panel as the host/solo view. Tagging a spawn and teleporting are private, local-only conveniences — synced map state (weather, music, time of day) remains host-controlled.

### Fixed

- **Chat restrictions no longer leak into loaded zones**: virtual-loading a duty or the Mordion Gaol no longer inherits that zone's chat lockdown (e.g. "/tell unavailable while bound by duty", or disabled shout/party). Chat now follows your real location's rules.

### Changed

- **Coordinates always shown**: the live X/Y/Z readout and Teleport controls now display without the "Show coordinates" toggle.
- **The Clyteum visual cleanup**: leftover combat-event debris that no longer fits a free-roam visit is now hidden, so the factory floor reads clean.

## [1.0.0.2] - 2026-07-31

### Added

- **Hide-title syncing (HMoniker)**: when a session member uses HMoniker's new "hide title" option, other members now see that nameplate title hidden too, matching what the wearer sees. Requires HMoniker.

### Fixed

- **`/em` emotes now reach session members**: custom text emotes (`/em`) from co-located session members are now visible, alongside the existing `/say`, `/yell`, and `/shout` passthrough.
- **Aetherial Sea location**: loading the Aetherial Sea (the login-screen backdrop) now places you on solid footing instead of an unusable spot.
- **Bayside Battleground spawn**: loading the Bayside Battleground (PvP) now drops you at a better starting position.
- **Relay key stays confirmed across restarts**: a saved relay key now shows as confirmed after you restart the game, instead of appearing editable again.

## [1.0.0.1] - 2026-07-28

### Fixed

- **Map reveal in newly patched zones**: automatic HUD map reveal now works in zones added by patch 7.55, such as The North Horn. The game's discovery table grew with the patch and the previous build silently skipped the new maps.

### Added

- **Installer buttons**: the plugin's entry in the Dalamud installer now has working **Open** and **Settings** buttons, opening the main window and jumping to the config tab respectively.

### Changed

- **Relay key check**: a pasted relay key is now verified against the real relay handshake, so the status light reflects whether the key is actually accepted instead of lighting green for any input.

## [1.0.0] - 2026-07-28

Initial public release.

### Added

- **Client-side zone loading**: enter any in-game map with `/hms load` (by number, name, or from the GUI) and roam it freely from an apartment, estate, FC room, or the world map, including cutscene-only locations such as pre-war Garlemald, the Garlean throne room, and the Steps of Faith.
- **Packet firewall**: a filter engages automatically on entering a virtual zone, passing only heartbeat traffic so the server sees you as idle in your room. Optional `/say`, `/yell`, and `/shout` passthrough, plus an auto-fail safeguard that ends the session and returns you safely if the filter ever stutters.
- **Co-op sessions**: host or join lobbies of up to 20 players over the RMS relay with no time limits or action restrictions. Requires a relay key (closed beta).
- **Solo mode**: `/hms startsolo` runs everything locally and never connects to the relay.
- **Shared map state**: hosts control weather, background music, and time of day, synced to every peer on the map.
- **Carpet mode**: spawn a personal floor to stand or walk where collision is missing, such as ledges, bridges, and texture-only surfaces, with slope controls.
- **Movement helpers**: flight (`/hms fly`) and noclip (`/hms noclip`) toggles for reaching and framing otherwise inaccessible spots.
- **Face control**: head-tilt and gaze tracking broadcast to peers outside of gpose for more expressive roleplay.
- **Mounts, emotes, and minions**: client-side summons visible to peers, with all emotes unlocked during a session.
- **Chat**: proximity `/say` and `/yell` passthrough after a one-time opcode setup; party, alliance, and free company chat always work behind the firewall.
- **Scene cleanup**: hide event NPCs and remove VFX clutter (entrance curtains, arena barriers, border lines) for an immersive freeroam.
- **Curated spawns** for every zone, with your own spawn points recorded via `/hms memo`.
- **Optional integrations**: Glamourer for cosmetic visibility toggles, and Moniker for in-session nameplate changes.

[1.0.1.2]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.1.2
[1.0.1.1]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.1.1
[1.0.1.0]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.1.0
[1.0.0.9]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.9
[1.0.0.8]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.8
[1.0.0.7]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.7
[1.0.0.6]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.6
[1.0.0.5]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.5
[1.0.0.4]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.4
[1.0.0.3]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.3
[1.0.0.2]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.2
[1.0.0.1]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.1
[1.0.0]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.0
