![Advanced Admin Panel](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/AdminPanel/logo.png)

**The all-in-one, server-secured admin toolkit for Valheim.**
Press **F7** and run your realm from one window: spawn anything, teleport anywhere, moderate anyone, and tune the panel to your taste. No Jötunn, no wrappers. Just BepInEx and two DLLs.

[![Discord](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/discord.png)](https://discord.gg/2RVn78hNrz)

**[Hephaestus Labs Discord](https://discord.gg/2RVn78hNrz)** · support, bug reports, release announcements  ·  **[Source & changelog](https://github.com/hldblc/ValheimAdminPanel)**

---

## At a glance

- **Nine tabs** covering items, creatures, bosses, your character, the world, other players, server status, settings, and a Tools tab with 32 optional server modules in five categories.
- **Admins only.** The panel checks your `adminlist.txt` status on every server you join. If you are not an admin there, or the server has no companion, the window shows a notice instead of the tabs. Server actions are validated again server-side by the companion.
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

Once the server is running, its `BepInEx/LogOutput.log` must contain `Loading [AdminPanelCompanion <version>]` followed by a `Bind probe: ... 0 failed` line. If it does not, the companion is not installed where BepInEx looks (`BepInEx/plugins/` of the instance players join), the zip was uploaded without being extracted, or the server was not fully stopped and started after the upload.

## The tabs

| Tab | What you get |
| --- | --- |
| 🎁 **Items** | Every item with icons, live search and real stats (damage, armor, food values). Categories, favorites, recents. One-click gear kits from Bronze to Ashlands, bulk packs, and give straight into any player's inventory. |
| 🐗 **Creatures** | Spawn by faction with count and star level, at your crosshair, optionally pre-tamed with a pet name. Saved presets, undo last spawn, arena mode (A vs B). |
| ⚔ **Bosses** | One-click summons from Eikthyr to Fader, instant altar offerings, real raid events at your position. |
| 🏃 **Player** | God mode, ghost, fly, free build, no stamina, one-hit kill. Speed, jump and pickup sliders. Skills (+10 / max / reset, per-skill ±) and a categorized status-effect browser, for yourself or any online player. Buffs persist through death. |
| 🌍 **World** | Time and weather control, wind for sailing, teleport bookmarks, quick-jump to boss altars, teleport to any map point (open the map, hover, press T). |
| 👥 **Players** | Live roster: teleport to, summon, spectate, heal, ping, lightning strike, live inventory viewer, kick / ban, per-player admin notes. |
| 📊 **Server** | Stats read from the server itself: ZDO count, per-peer ping, last-save and next-autosave clocks, the server's plugin roster, admin and ban lists, join / leave log, force save, offline ban / unban by ID. |
| ⚙ **Settings** | Language, font and size, panel opacity, logo header, camera lock, hotkey rebinding. All in-game, all persistent. |
| 🧰 **Tools** | 32 optional modules for dedicated-server admins, grouped into Moderation, Server, Players, World and Shortcuts. See below. |

## Tools: 32 server modules

Every module is a chip in the Tools tab: pick a category, then a section. **Everything that changes server behaviour is off by default** and needs the companion on the server. Every section can be hidden in the config file (section `Features`).

| Category | Sections |
| --- | --- |
| **Moderation** | Moderation (warn, mute, freeze, jail, watchlist, temp-ban, lockdown), Rap Sheet, Roles (tiered admin roles), Audit (audit trail), Guard (anti-cheat rules: fly, speed, health, noclip, damage cap; client-mod reports; dry-run mode), Staff Chat, Direct Message |
| **Server** | Server Tools (MOTD, scheduled restarts, backups and staged restore), Diagnostics (performance, log tail, self-test), Discord (webhooks), Client Performance (census), Extensions (SDK) |
| **Players** | Player Data (vault, offline queue, death log, rescue, reset), Economy (shops, ledger), Bounties, Death Rules, Skill Rules, Map Reveal, Trader Stock, Blacklist, Item Forge |
| **World** | Area Tools (protection zones, ownership transfer, mass remove), Build Tools (piece editor, blueprints, terrain reset, free camera), Locations (finder), Spawners, Containers (chest inspector), Tames (roster), Creature Editor, Map Pins, Raid Composer |
| **Shortcuts** | Macros (command palette and macros), Workflow (batch actions, quick-action bar) |

## How the security works

Two layers, and it helps to know what each one does.

- **The panel's admin gate (client side, since 2.5.5).** When you join a server the panel asks the companion whether your ID is in that server's `adminlist.txt` (the game itself also hands every client a copy of that list, which the panel uses until the companion answers). If you are not an admin there, or the server has no companion, the whole window is replaced by a notice with a Re-check button; no tab, tool or self-only cheat is reachable. The companion writes one line to the server log per connection when a non-admin opens the panel. This gate is honesty for normal players. A modified client can remove it, which is why the second layer exists.
- **The companion (server side).** Every request that goes through the server (spawn, give, teleport, kick, ban, heal, inventory, broadcasts, world events, skills, and the Tools modules) is validated **server-side** against `adminlist.txt` with the game's own admin rules, and the companion re-stamps the true sender ID so forged requests are dropped and written to the audit log. Without the companion on the server, nothing is enforced and every server action is silently dropped. The companion also drops the game's own inbound "AdminList" / "PlayerList" RPCs on the server, which a modified client could otherwise use to rewrite the server's in-memory admin list (a vanilla gap, present with or without this mod).
- **What only the gate covers.** Self-only toggles (god mode, fly, no stamina, one-hit) and the tools that edit objects the game lets any client edit (World-tab kill / tame / clear tools, global keys, the Build Tools piece editor and terrain reset, the Creature Editor, the Item Forge) run on the admin's own client. Valheim trusts clients for those, as it does for every client mod. They sit behind the admin gate, not behind the companion. Pair with a server-side anticheat if you need to stop a modified client from doing what the game already allows it to do.

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

**The panel says "You are not an admin on this server".** Your ID is not in that server's `adminlist.txt` in any form the game accepts. Ask the owner to add it (one ID per line) and press Re-check; a companion restart is not needed. If you are the owner and you are sure the ID is there, check the server log for `Loading [AdminPanelCompanion` and for the line the companion writes when a non-admin opens the panel.

**The panel says "This server does not run AdminPanelCompanion.dll".** No companion answered within 15 seconds. Install both files on the server, in `BepInEx/plugins/` of the instance players join, then do a full stop and start.

**Everyone who installs the mod gets the panel on my server.** They get the window, not the powers. Since 2.5.5 non-admins see only the notice; before 2.5.5 they saw the tabs while every server action was denied. Either way, install the companion on the server, that is what enforces `adminlist.txt`.

**Do I need the companion in single-player?** Yes, both DLLs. You are the host there, so no adminlist entry is needed.

**I gave another player items or skills and nothing changed for them.** Items, skills, status effects and inventories live in that player's own save, so the companion on *their* client applies them: every player you give things to needs `AdminPanelCompanion.dll` too, same version as the server's. The panel tells you when the server knows a player runs without it, and the Skills list shows the picked player's real levels (2.5.6 on their side and on the server). With a 2.5.6 companion on the server, summon, heal and direct messages work without the mod on their side. A player who is dead or still loading in cannot be picked until they are back in the world.

**A Tools section shows "no reply from the companion".** The server has no companion, an older one, or you are not an admin there. The Server tab shows which.

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
