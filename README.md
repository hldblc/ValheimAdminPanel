![Advanced Admin Panel](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/AdminPanel/logo.png)

**The all-in-one, server-secured admin toolkit for Valheim.**
Press **F7** and run your realm from one window: spawn anything, teleport anywhere, moderate anyone, and tune the panel to your taste. No Jötunn, no wrappers. Just BepInEx and two DLLs.

[![Discord](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/discord.png)](https://discord.gg/2RVn78hNrz)

**[Hephaestus Labs Discord](https://discord.gg/2RVn78hNrz)** · support, bug reports, release announcements  ·  **[Source & changelog](https://github.com/hldblc/ValheimAdminPanel)**

---

## At a glance

- **Nine tabs** covering items, creatures, bosses, your character, the world, other players, server status, settings, and 33 optional server modules.
- **Server-validated.** Every action that touches the world or another player is checked on the server against `adminlist.txt`. Non-admins get buttons that do nothing.
- **Single-player and dedicated servers.** In single-player you are the host and automatically an admin.
- **Mod-aware.** Items and creatures from other installed mods show up automatically.
- **Nine languages**, in-game hotkey rebinding, persistent settings.

## Quick start

**Requirements:** BepInEx for Valheim (denikson BepInExPack_Valheim). With r2modman or Thunderstore Mod Manager, just install this package.

| Where | What to do |
| --- | --- |
| **Your client** (single-player or joining a server) | Copy `AdminPanel.dll` and `AdminPanelCompanion.dll` into `BepInEx/plugins/`. Launch the game and press **F7**. |
| **Dedicated server** | Copy `AdminPanelCompanion.dll` into the server's `BepInEx/plugins/`. Add each admin's SteamID64 to the server's `adminlist.txt` (`Steam_<id>`, `V_<id>` and the bare number all work). Restart the server. |

Both DLLs must be the **same version** on the client and the server. The Server tab tells you if they are not.

## The tabs

| Tab | What you get |
| --- | --- |
| 🎁 **Items** | Every item with icons, live search and real stats (damage, armor, food values). Categories, favorites, recents. One-click gear kits from Bronze to Ashlands, bulk packs, and give straight into any player's inventory. |
| 🐗 **Creatures** | Spawn by faction with count and star level, at your crosshair, optionally pre-tamed with a pet name. Saved presets, undo last spawn, arena mode (A vs B). |
| ⚔ **Bosses** | One-click summons from Eikthyr to Fader, instant altar offerings, real raid events at your position. |
| 🏃 **Player** | God mode, ghost, fly, free build, no stamina, one-hit kill. Speed, jump and pickup sliders, skill +10 / max / reset, a categorized status-effect browser. Buffs persist through death. |
| 🌍 **World** | Time and weather control, wind for sailing, teleport bookmarks, quick-jump to boss altars, teleport to any map point (open the map, hover, press T). |
| 👥 **Players** | Live roster: teleport to, summon, spectate, heal, ping, lightning strike, live inventory viewer, kick / ban, per-player admin notes. |
| 📊 **Server** | Stats read from the server itself: ZDO count, per-peer ping, last-save and next-autosave clocks, the server's plugin roster, admin and ban lists, join / leave log, force save, offline ban / unban by ID. |
| ⚙ **Settings** | Language, font and size, panel opacity, logo header, camera lock, hotkey rebinding. All in-game, all persistent. |
| 🧰 **Extras** | 33 optional modules for dedicated-server admins, one chip each. See below. |

## Extras: 33 server modules

Every module is a chip you switch on in the Extras tab. **Everything that changes server behaviour is off by default** and needs the companion on the server.

| Area | Modules |
| --- | --- |
| **Moderation & accountability** | Moderation (warn, mute, freeze, jail, watchlist, temp-ban, lockdown), audit trail, rap sheets, tiered admin roles, direct messages, staff chat, guard (anti-cheat flags, client-mod reports, dry-run mode) |
| **Server operations** | Server tools (MOTD, scheduled restarts, backups and staged restore), diagnostics and performance, Discord webhooks, companion self-update, client performance census |
| **Players & economy** | Player data and offline queues, economy and shops, trader stock editor, bounty board, death rules, skill gain rules |
| **World & objects** | Area tools and protection zones, build tools (piece editor, blueprints, terrain reset, free camera), location finder, spawner and nest manager, chest inspector, tame roster, creature editor, map pins, map reveal |
| **Rules & content** | Recipe and build blacklist, item forge, raid composer, macros and workflows, extensions SDK |

## How the security works

This is a client mod, so the panel window opens for anyone who installs the DLL. No client mod can prevent that. What the companion protects is **your server**.

- Every action that touches the world or another player (spawn, give, teleport, kick, ban, heal, inventory, broadcasts, world events, skills, and every Extras module) is sent to the companion as a request and validated **server-side** against `adminlist.txt`, using the game's own admin rules.
- The companion re-stamps the true sender ID, so forged or spoofed requests are dropped and written to the audit log.
- Self-only toggles (god mode, fly, no stamina) run on the player's own client and are not server-validated. That is true of every Valheim client mod, because the game trusts clients for their own character. Pair with a server-side anticheat if you need to stop that too.

## Languages

Ships with **English, Deutsch, Español, Français, Italiano, Nederlands, Polski, Português (BR)** and **Svenska**, following Valheim's language setting by default (override it under Settings → Language). Item and creature names always follow the game.

Want another language? Drop a `<code>.txt` into `BepInEx/plugins/AdminPanel_Localization/`. No rebuild needed, and untranslated lines fall back to English. Pull requests are welcome.

## Compatibility

- Custom weapons, armor and creatures from other installed mods appear automatically. To spawn modded content on a dedicated server, that mod must also be installed server-side.
- Works alongside ValheimPlus and other BepInEx mods.
- Built against the current Valheim release. Both DLLs run a self-check at startup and report on the Server tab if a game update broke anything.

## FAQ

**The panel shows a version-mismatch banner.** The client and server companion differ. Update `AdminPanelCompanion.dll` on the server (and both DLLs on the client) to the same release and restart.

**I am in adminlist.txt but server actions say I am not an admin.** Update to 2.5.3 or later. Older companions only matched the bare SteamID64 and rejected the `Steam_` and `V_` forms that hosting panels write.

**Do I need the companion in single-player?** Yes, both DLLs. You are the host there, so no adminlist entry is needed.

**Extras shows "no reply from the companion".** The server has no companion, an older one, or you are not an admin there. The Server tab shows which.

## Screenshots

![Items tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-1-Items.png)
![Creatures tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-2-Creatures.png)
![Bosses tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-3-Bosses.png)
![Player tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-4-Player.png)
![World tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-5-World.png)
![Players tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-6-Players.png)
![Server tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-7-Server.png)
![Settings tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-8-Settings.png)

## Support

- [Discord: Hephaestus Labs](https://discord.gg/2RVn78hNrz) for help and bug reports. The panel also has a one-click bug report on the Server tab.
- [GitHub](https://github.com/hldblc/ValheimAdminPanel) for source, releases and the full changelog.

---

*Advanced Admin Panel is an unofficial, fan-made mod and is not affiliated with or endorsed by Iron Gate AB or Coffee Stain Publishing. Valheim™ and its visual style are the property of Iron Gate AB. The logo is fan art inspired by Valheim's official logo design.*
