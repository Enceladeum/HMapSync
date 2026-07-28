# Changelog

All notable changes to HMapSync (HMS) are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.0.0.1]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.1
[1.0.0]: https://github.com/Enceladeum/HMapSync/releases/tag/v1.0.0.0
