# Changelog

All notable changes to HMapSync (HMS) are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.0.0.5]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.5
[1.0.0.4]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.4
[1.0.0.3]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.3
[1.0.0.2]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.2
[1.0.0.1]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.1
[1.0.0]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.0
