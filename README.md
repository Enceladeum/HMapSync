# HMapSync (HMS)

> Explore any zone together. Built for roleplayers and gposers.

Enter any in-game map and explore it with friends. HMS puts you behind a firewall and lets you load any location client-side, including otherwise unavailable cutscenes while the server sees you sitting afk in your room.

<img width="486" height="612" alt="Screenshot_5" src="https://github.com/user-attachments/assets/47849b11-343a-451b-9418-eb24be90b6e8" />


<img width="488" height="669" alt="Screenshot_3" src="https://github.com/user-attachments/assets/47bc33b8-0394-4c97-aca1-a0f4a9064d6e" />

## Highlights

- Load any zone client-side, including cutscenes) and roam it freely from your apartment, estate, FC room or even a world map
- Packet filter automatically engages once you load a virtual zone and keeps you secure while in-session, letting only heartbeat signals and optionally allowing `/say`, `/yell` and `/shout` though, making it seem you're just standing and chatting to any outside observer
- Access cutscene-only zones (pre-war Garlemald, Garlean throne room, Steps of Faith bridge or Werlytian countryside)
- Co-op mode: enter any map with up to 20 friends without any time limits or action restrictions. Ever wanted to roleplay in a dungeon for longer than 90 minutes and use flying mounts indoors? Now you can
- Enter question, raid and solo instances like Terncliff, NPC rooms or raids and freely explore them
- Teleport to peers by rightclicking on their name in the lobby - never get lost again
- Free flight, noclip to get around quickly
- Roleplaying tools - head tilt display, weather and BGM sync as well as NPC hide for immersive sessions

## Features

### Zones and exploration
- Load any zone client-side with `/hms load` by zone number, name, GUI, (i.e. /hms load Kugane)
- Carpet mode - spawn floor underfoot to stand or walk in zones that don't have normal collision, such as high ledges, bridges or landmarks
- Most zone barriers cleaned up for easy travel. If you're stuck, try `/hms carpet` or `/hms noclip` 
- Curated spawns for all zones: never spawn out of bounds. You can set your own spawn points too with `/hms memo`
- Shared weather, BGM and time view via host session control - everyone on the map sees the same thing. Never have a friend emote about rain while you see a dry midday on your end.
- Hide NPCs to free up the chair you always wanted to use yourself
- Removed VFX clutter from maps, such as the purple entrance curtain, some boss zone barriers and red/blue border lines for an immersive experience
- Added a hand-curated section with various seaships for the great blue adventure enjoyers, allowing you to spend time at sea with your friends
- Better Explorer mode for the Clyteum map - removed invisible walls in the city, allowing you to take any turn you like in a fully immersive freeroam mode

<img width="1036" height="594" alt="Screenshot_15" src="https://github.com/user-attachments/assets/abb84289-e262-48e6-813c-ae7acd76875a" />


### Play together (lobbies and sync, requires a beta key)
- Gather in one location. Joiners should be within rendering range (about 1000 yalms) and on the same map (world map, city, interior, etc.)
- If you're the host, type `/hms start <password>` to start a lobby. Leave password blank to auto-generate one
- Type `/hms join <password>` or use the plugin GUI to enter the lobby
- Type `/hms leave` (or use plugin window) to leave the session
- Type `/hms stop` to end the session for everyone if you're host, otherwise the command works as the `/hms leave` command
- If the host leaves, host is auto-transferred to a peer, so the session is never interrupted
- You need a relay key to play together. Relay keys are currently available to closed beta testers only

### Plugin interface

- Session - click on "movement", "appearance" or "face control" headers to create hotbars 
- Map control - set weather, BGM or hide NPCS (host-only, map state shared by all peers)
- Zones - maps split by type
- Summons - minions, emotes, fashion accessories and mounts
- Carpet - spawn-your-own floor controls 
- Config - relay settings, appearance and modules
- Packets (debug mode) - similar to Dalamud's `/xldata` network tab

<img width="493" height="551" alt="Screenshot_8" src="https://github.com/user-attachments/assets/3b70f621-299d-4ad1-a041-5ce65573de5b" />


### Carpet
- Spawn floor for your character only in-session. Lets you walk on roofs, far bridges or places that have textures-only surfaces
- Uphill/Downhill creates a gentle up down slope. Press "flat" to reset

<img width="617" height="236" alt="Screenshot_14" src="https://github.com/user-attachments/assets/86c03eae-6c1b-4eb6-8ecb-10c315808d56" />


### Face control
- Tilt your head or move your eyes outside of gpose - broadcasted in-HMS for a more expressive roleplaying experience
- Both body posture, gaze tracking and head tilt are broadcast to peers - you can now roll your eyes mid-RP and others will see it


<img width="918" height="775" alt="Screenshot_9" src="https://github.com/user-attachments/assets/fb633eae-bf3d-496c-b4dd-345cea22a3c9" />

Face control bar

<img width="378" height="148" alt="Screenshot_10" src="https://github.com/user-attachments/assets/aaba49f2-b557-4965-a48c-e6241a173e55" />


### Mounts
- Type `/hms mount <id>` to giddy up or use mount picker GUI in the Summons tab
- Right-click the chocobo icon to dismount or type `/hms mount` when mounted to dismount
- Non-MechOps mount actions are available and visible to peers in HMS sessions

### Character and cosmetics
- All emotes unlocked while in HMS session
- Head tilt (camera look or `/facecamera`) is shown to peer unlike the vanilla game. Dramatically gaze up into the night sky knowing others see you doing just that
- Change or hide nameplate in-session with the optional Moniker plugin

### Chat
- Chat passthrough between session members after a short opcode set up via config menu
- Proximity-based `/say` and `/yell`, just like in-game
- Party, alliance and FC chats will always work behind firewall - no opcode set up required

### RMS - Relay MapSync

The relay is a **message forwarder**. It takes what your game client sends and copies it to the other people in your session. It is deliberately stupid: for the things that actually describe you (where you are, what you look like, what you're doing), **it forwards them without ever opening them.**

- **It never sees anything you say.** Chat doesn't go through the relay at all. Not `/say`, not `/tell`, not party chat. None of it. There is no conversation on the server, ever
- **It never sees what you sync.** Your position, animation, emotes, mount, minion, appearance settings: all of it is forwarded as sealed bytes. The server measures how *big* the message was and what *category* it was, i.e. movement, map weather change, without details - just the message type. It does not, and cannot, read what's inside
- **It does write down that you were there.** Your character name appears in the server log when you join a room. That is the main thing it records about you personally. It's not possible to say who's where with who or even what maps they have loaded

### Relay privacy

- Your security and privacy are non-negotiable, so the server is configured to capture as little data as humanly possible, which is retained for 15 days after which it's permanently deleted
- What it captures: your character name and ID when you begin hosting (it needs it to establish a session), the room password and the list of participants who join the lobby. The list is captured briefly once to validate who's in the rendering proximity and then discarded.
- You can use solo session `/hms startsolo` to use the plugin locally where it never connects to the relay. Solo mode is recommended if you're not planning to co-op

These live in the server's memory for as long as your session lasts, and vanish when it ends or the server restarts. **None of them is saved to disk.**

| What | Why |
|---|---|
| **Your ContentId** (your character's permanent FFXIV id) | This is how "join the room the people near you are in" works, and how a kick makes a ban stick. **It is never written to a log, never saved, and never attached to any statistic.** |
| **Your character name** | Shown in the lobby roster so people know who's in the room. |
| **The room password** | Checked against what you typed. Never written down anywhere. (Still, don't use passwords you use elsewhere, or use plugin auto-generated lobby passwords as best practice) |
| **The characters you can see** | Used for exactly one lookup (to work out which room you're trying to join), then thrown away. Only the *number* of them is ever recorded, never who they are. |
| **Which key you connected with** | Abuse detection |

Your **EntityId** is sent by the plugin and the relay never even reads it. Note that **EntityID** is distinct from **AccountID** which is used by the game for account-level identification for blacklist.

The plugin does not nor will it ever collect AccountID.

### Session security

Packet filter has been outfitted with auto-fail detector, so if the firewall stutters or fails in any way, the session instantly ends and you get safely teleported back.
While every effort was taken to make sessions as secure as possible, as a general rule don't use any plugins or mods on an account you can't afford to lose. 

## Commands

The plugin registers a single `/hms` command with subcommands:

```
/hms start | join | load | reload | leave | stop | fly | carpet | emote <id|name> | minion <id|name> | maps | status
```

Common subcommands:

| Subcommand | Description |
| --- | --- |
| `hms` | Open/close plugin window |
| `start <password>` | Start a session (host) with a chosen password. Leave blank to auto-generate session password. |
| `starts` / `startsolo` | Start a solo, non-relay session. |
| `join <password>` | Join a session. |
| `load <zone>` | Load a zone client-side (partial-name matching). |
| `maps` | Open zone list. |
| `status` | Show session status. |
| `reload` | Reload the current map. |
| `leave` | Leave the session. |
| `stop` | Leave the session if you're a peer, or stop the session for everyone if you're the host. |
| `fly` | Toggle flight. |
| `noclip` | Toggle noclip. |
| `carpet` | Toggle carpet. |
| `emote <id \ name>` | Play a client-side emote. |
| `minion <id \ name>` | Summon a client-side minion. |
| `memo` | Record a spawn point for the current map. |
| `stagestate [name \ next]` | For the handful of cutscene stages that have more than one composition (e.g. a ruined vs. restored version of the same set), flip between them. Called with no argument it cycles to the next composition; you can also name one directly. The clean, flicker-free default is applied automatically when the stage loads — this is only for manually switching to an alternate look. |

`/facecamera` / Pause/Break key - drives head-tilt / gaze sync.

## Known issues

| Issue | Solution |
| --- | --- |
| Furniture doesn't reappear on return  | Load any zone and exit it again or relog/re-enter the apartment |
| All estate houses disappear if HMS started from a residential district | Not to get technical, but just don't do that or re-enter the zone if you did and the houses will reappear |
| Paintings, wallpapers and flooring reset to default on session end | Re-enter the interior to make them reappear | 
| Client softlocked on diving / zone change on foot (i.e. between Northern and Western Thanalan or Upper/Lower Limsa) | Zone change has not been implemented yet. When you try to change zone, you send a packet request to the server. Since you're behind the firewall, the request never reaches the server so the client is stuck waiting for the server response which will never come. Use `/hms load` to map hop instead and restart your client if you got stuck | 
| Stopped by an invisible wall | Try `/hms noclip` or `/hms carpet`. This usually happens when there's a collision barrier (such as between boss arenas) (use noclip) or the map doesn't have the floor underneath (then use carpet). A lot of barriers have been auto removed in HMS maps, but in many places, horizontal walls are welded to the floor, so removing those makes large areas of the map unusable and that collision is difficult to restore cleanly |
| Multi-seater mounts don't work | Not implemented yet |
| In-game cutscene is missing from the zone list | FFXIV uses several tricks to instantiate cutscenes: one of them is dynamically spawning assets and furniture on existing maps to make them appear as new locations. Some maps are used for multiple different cutscenes too. All cutscene locations which have dedicated maps are available in the Cutscenes tab. Some are constructed dynamically and are not available yet. For example, the Black Rose Research facility where Varis speaks with Emet-Selch takes place in Mor Dhona with the game adding temporary props which are removed once it's finished playing. Similarly, the Diamond Weapon bay from the Sorrow of Werlyt questline takes place in the magitek hangar of the Imperial Palace (the one where Estinien fights the Arch Ultima boss in the Vows of Virtue, Deeds of Cruelty duty). The Valens' control chamber, the Weapon itself and other props are spawned dynamically. There is currently no way to easily invoke and freeze them. This also explains why Valens van Varro's chapter is missing some familiar props - these are spawned dynamically by the cutscene director |
| Any other map / character visibility bugs on session exit | Fixed by relogging or re-entering the map or either `/sit` or `/groundsit` to refresh character position |
| Game crashes on trying to load a map from the expension you don't have | 1) Support the game, get the expansion 2) Use a workaround to make the game download the full content. If you have an account with full content, run it once to allow the client to grab all necessary game files and then switch back to the original account, trial accounts / etc will recognise the files and let the HMS 'feed' them for map load |

## Tips and tricks

- Some maps huge and have a lot of hidden chambers, event/phase locations or interiors tucked underground (i.e. Zadnor, South Horn, Mount Rokkon). Use `noclip` on spawn and press shift to move under the map to see inspect if there's anything hidden underneath
- Press noclip+carpet to get around quickly if you don't have teleport coordinates
- Use `/hms fly` to reach an out of place location, adjust your footing and then toggle `/hms carpet on`. Then disable flight and enjoy the sensation of walking on a solid ground where the ground didn't exist five minutes ago for cinematic shots
- You can also drop `/hms carpet` while mounted, to make a cinematic landing and dismount. The carpet will persist through dismount
- In face control, look up in the sky and press 'hold coords'. This will make your character continue looking at the fixed point while walking. Helpful for immersion or to roleplay tracking a high up / faraway object
- Ship cabin zone (o1e1) has an observation deck! Navigate downstairs to the aft side of the ship and enjoy the view

<img width="2166" height="1314" alt="Screenshot_11" src="https://github.com/user-attachments/assets/456d820e-48f2-42ae-90c3-33abc703c7e5" />


## Requirements

- XIVLauncher with Dalamud
- Glamourer: cosmetic visibility toggles use Glamourer IPC when it is installed

## Installing

HMapSync is distributed through a custom Dalamud plugin repository.

1. In game, open Dalamud settings (`/xlsettings`) and go to the Experimental tab.
2. Add the custom plugin repository URL: `https://raw.githubusercontent.com/Enceladeum/DalamudPlugins/main/repo.json`.
3. Open the plugin installer (`/xlplugins`), search for HMapSync, and install.

## Demos

  ▶️ <b>Carpet feature ↗️</b>
  
<a href="https://youtu.be/ZkZxXGvuWtw">
  <img src="https://img.youtube.com/vi/ZkZxXGvuWtw/maxresdefault.jpg" width="600" alt="Carpet demo">
</a>


  ▶️ <b>Live face tracking ↗️</b>

  <a href="https://youtu.be/FJbYoHjaAoU">
  <img src="https://img.youtube.com/vi/FJbYoHjaAoU/maxresdefault.jpg" width="600" alt="Carpet demo">
</a>


  ▶️ <b>Cutscene location co-op access ↗️</b>

<a href="https://youtu.be/UddbA2rMVsQ">
  <img src="https://img.youtube.com/vi/UddbA2rMVsQ/maxresdefault.jpg" width="600" alt="Carpet demo">
</a>

  ▶️ <b>Copied Factory freeroam ↗️</b>

<a href="https://youtu.be/xFMyCv2BwzA">
  <img src="https://img.youtube.com/vi/xFMyCv2BwzA/maxresdefault.jpg" width="600" alt="Carpet demo">
</a>


## Credits

- Author: The Enceladeum
- Built on Dalamud, FFXIVClientStructs, Glamourer.Api, and MessagePack
- Inspired by Hyperborea. We stand on the shoulders of giants.
- License: GNU AGPL-3.0. See [LICENSE](LICENSE).
