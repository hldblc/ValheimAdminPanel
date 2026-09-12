![Advanced Admin Panel](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/AdminPanel/logo.png)

**The all-in-one, server-secured admin toolkit for Valheim.**
Press **F7** and rule your realm — spawn anything, teleport anywhere, moderate anyone, and make the panel yours. No Jötunn, no wrappers — just BepInEx and two DLLs.

[![Discord](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/discord.png)](https://discord.gg/2RVn78hNrz)

**[Join the Hephaestus Labs Discord](https://discord.gg/2RVn78hNrz)** — support · bug reports · release announcements

---

## ⚔ Nine tabs of power

| Tab | What you get |
| --- | --- |
| 🎁 **Items** | Every item with icons, live search & **real stats** (damage, armor, food values), categories & sub-categories, favorites, recents, one-click **gear kits** (Bronze → Ashlands), bulk packs, give straight into any player's inventory |
| 🐗 **Creatures** | Spawn by faction with count & star level, at crosshair, pre-tamed with a custom pet name, saved presets, **undo last spawn**, arena mode (A vs B — FIGHT!) |
| ⚔ **Bosses** | One-click summons Eikthyr → Fader, instant altar offerings, real **raid events** at your position |
| 🏃 **Player** | God mode, ghost, fly, free build, no stamina, one-hit kill, speed/jump/pickup sliders, skills +10/max/reset, **categorized status-effect browser** — buffs persist through death |
| 🌍 **World** | Time & weather control, **wind for sailing**, teleport bookmarks, quick-jump to boss altars, teleport to **any map point** (open map, hover, press T) |
| 👥 **Players** | Live roster: teleport to, summon, spectate, heal, ping, ⚡ lightning strike, **live inventory viewer**, kick/ban, admin notes |
| 📊 **Server** | **Server-truth** stats read from the server itself: ZDO count, per-peer ping, last-save & next-autosave clock, the server's **plugin roster**, admin/ban lists, server-side join/leave log, force save, offline ban/unban by ID |
| ⚙ **Settings** | Language, font, font size, panel opacity, logo header, camera lock & **hotkey rebinding** — all in-game, all persistent |
| 🧰 **Extras** *(new in 2.5.0)* | **33 optional modules** for dedicated-server admins: moderation (warn/mute/freeze/jail/watchlist), audit trail, tiered admin roles, rap sheets, server tools (MOTD, restarts, backups), diagnostics, Discord webhooks, player data & offline queues, economy & shops, protection zones, build tools, map pins & map reveal, location finder, spawner/nest manager, chest inspector, tame roster, creature editor, recipe/build blacklist, skill gain rules, trader editor, death rules, raid composer, bounty board, staff chat, item forge, client perf census, companion self-update. Everything that changes server behaviour is **off by default**. |

## 🔒 How the security actually works

This is a *client* mod, so the panel window opens for anyone who installs the DLL — no client mod can prevent that. What the companion protects is **your server**.

Every action that touches the world or another player — spawn, give, teleport, kick, ban, unban, heal, inventory view/remove, broadcast, world events, skills, skip night — is sent to the companion as a request and validated **server-side** against `adminlist.txt`. The companion re-stamps the true sender ID, so forged or spoofed requests are dropped and logged. A non-admin who copies the DLL onto your server gets a panel full of buttons that do nothing.

Self-only toggles (god mode, fly, no-stamina) run on that player's own client and are **not** server-validated — that's true of every Valheim client mod, since the game trusts clients for their own character. Pair with a server-side anticheat if you need to stop that too.

## 🌍 Speaks your language

Ships with **Deutsch, Français, Español, Italiano, Português (BR), Polski, Nederlands** and **Svenska**, and follows Valheim's own language setting by default (override it in Settings → Language). Item and creature names always follow the game.

Want your language? Drop a `<code>.txt` into `BepInEx/plugins/AdminPanel_Localization/` — no rebuild needed, and any line you don't translate simply falls back to English. Pull requests welcome.

## 🤝 Works with other mods

Custom weapons, armor and creatures from *other* installed mods appear automatically. (To spawn modded content on a dedicated server, that mod must also be installed server-side.) Compatible with ValheimPlus and other BepInEx mods.

## 📸 Screenshots

![Items tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-1-Items.png)
![Creatures tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-2-Creatures.png)
![Bosses tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-3-Bosses.png)
![Player tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-4-Player.png)
![World tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-5-World.png)
![Players tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-6-Players.png)
![Server tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-7-Server.png)
![Settings tab](https://raw.githubusercontent.com/hldblc/ValheimAdminPanel/main/assets/AdvancedAdminPanel-8-Settings.png)

## 📥 Installation

Requires **BepInEx for Valheim**. With a mod manager (r2modman / Thunderstore Mod Manager) just install this package.

Manual — client: copy `AdminPanel.dll` + `AdminPanelCompanion.dll` into `BepInEx/plugins/`, add your 64-bit Steam ID to `adminlist.txt`, press **F7** in-game.
Dedicated server: `AdminPanelCompanion.dll` in the server's plugins folder + admin Steam IDs in the server's `adminlist.txt`.

## 🔗 Links

- [Discord — Hephaestus Labs](https://discord.gg/2RVn78hNrz)
- [Source & changelog on GitHub](https://github.com/hldblc/ValheimAdminPanel)

---

*Advanced Admin Panel is an unofficial, fan-made mod and is not affiliated with or endorsed by Iron Gate AB or Coffee Stain Publishing. Valheim™ and its visual style are the property of Iron Gate AB. The logo is fan art inspired by Valheim's official logo design.*
