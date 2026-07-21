<p align="center">
  <img src="AdminPanel/logo.png" alt="Advanced Admin Panel" width="720">
</p>

A full-featured in-game admin panel for Valheim dedicated servers, with **server-side admin authentication** — only Steam IDs on the server's `adminlist.txt` can use it. In everyone else's hands, the panel is completely dead.

![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23-blue) ![Valheim](https://img.shields.io/badge/Valheim-0.221.x-green) ![License](https://img.shields.io/badge/license-MIT-brightgreen)

<p align="center">
  <a href="https://discord.gg/2RVn78hNrz"><img src="assets/discord.png" width="56" alt="Discord"></a><br>
  <a href="https://discord.gg/2RVn78hNrz"><b>Join the Hephaestus Labs Discord</b></a><br>
  <i>support · bug reports · release announcements</i>
</p>

## ✨ Features

Press **F7** (configurable) in-game to open a draggable, Valheim-styled panel with eight tabs:

### 🎁 Items
- Every item in the game, organized by category (Weapons, Shields, Armor, Ammo, Tools, Food & Potions, Materials, Trophies…) with **sub-categories** — weapons by skill type, armor by slot, **materials by biome**
- Item icons, localized names, live search
- **Drop** (spawn on ground), **Bag** (straight into your inventory), **Give** (into another player's inventory)
- ⭐ Favorites (persisted), Recent items, **gear kits** (full Bronze→Ashlands sets in one click), bulk material packs
- Amount and quality controls

### 🐗 Creatures
- Every creature, organized by faction, with Bosses and Tamable filters
- Spawn with count and star level, spawn **at crosshair**, spawn pre-tamed with a **custom pet name**
- **Arena mode** — pick creature A and B, hit FIGHT!
- Saved spawn presets, **undo last spawn** (server deletes what it just spawned)

### ⚔ Bosses
- One-click spawn for all bosses (Eikthyr → Fader)
- **Altar offering buttons** (grabs e.g. 3 Dragon Eggs instantly)
- Trigger real **raid events** ("The horde is attacking!") server-side

### 🏃 Player
- God mode, ghost mode, fly, free build, no stamina, one-hit kill, infinite carry weight
- Speed / jump / auto-pickup-radius sliders
- Full heal / stamina / eitr, repair all, skills +10 / max / reset
- Status effect browser — apply any buff in the game

### 🌍 World
- Time-of-day slider, weather control, **wind direction & strength** (sailing!)
- Teleport to coordinates + saved **bookmarks**
- Kill/tame nearby creatures, cleanup ground items, **repair all builds**, clear trees
- **Peaceful mode** (disable raids server-side), global keys editor, full map reveal

### 👥 Players
- Per player: teleport to, **summon**, watch (spectate), heal, map ping, ⚡ lightning strike, **live inventory viewer**, kick, ban
- Broadcast messages to everyone, summon ALL, per-player admin notes

### 📊 Server
- Live stats (world day, players online, loaded creatures, FPS)
- Unban by Steam ID, join/leave history

### ⚙ Settings
- **Font** (Norse auto / Norse Bold / Norse / Averia Serif / Default) and **font size** — the whole panel scales
- **Panel opacity**, **logo header toggle**, **camera-lock-while-open toggle**
- **Rebind hotkeys** (panel key, map-teleport key) straight from the UI
- Reset window / reset appearance — everything persists in the BepInEx config

## 🔒 Security model

Every action that touches the world or another player — spawn, give, teleport, kick, ban, unban, heal, inventory view/remove, broadcast, world events, skills, skip night — is routed through the **server**, which validates the sender's Steam ID against `adminlist.txt` before executing and re-stamps the true sender ID so requests can't be forged. Denied attempts are logged with the offender's Steam ID. Clients additionally refuse admin commands that don't originate from the server. If someone copies the panel DLL onto your server, every one of those buttons does nothing.

Being a client mod, the panel *window* still opens for anyone who installs the DLL, and self-only toggles (god mode, fly, no-stamina) run on that player's own client without server validation — inherent to every Valheim client mod, since the game trusts clients for their own character. Pair with a server-side anticheat if you need to prevent that too.

## 📦 Installation

Requires [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) on all machines.

| Where | DLL |
|---|---|
| **Admin's game** (`BepInEx/plugins`) | `AdminPanel.dll` + `AdminPanelCompanion.dll` |
| **Dedicated server** (`BepInEx/plugins`) | `AdminPanelCompanion.dll` |
| **Other players** (`BepInEx/plugins`) | `AdminPanelCompanion.dll` (needed to receive give/teleport/inventory features) |

Make sure your Steam ID is in the server's `adminlist.txt`. That's it — press **F7** in-game.

⚠️ Keep `AdminPanel.dll` to yourself. It's harmless in others' hands (the server rejects non-admins), but there's no reason to hand it out.

## 🌍 Localization

The panel ships with **English, Deutsch, Français, Español, Italiano, Português (BR), Polski, Nederlands** and **Svenska**, embedded in the DLL so the install stays two files. By default it follows Valheim's own language setting; override it in **Settings → Language**. Item, creature and status-effect names always come from the game itself.

**Adding or fixing a language** — no rebuild required:

1. Copy [`AdminPanel/Localization/en.txt`](AdminPanel/Localization/en.txt) to `<code>.txt` (e.g. `cs.txt`).
2. Translate the right-hand side of each `key=value` line. Leave keys alone. Keep every `{0}`/`{1}` placeholder — you may reorder them for your language's word order.
3. Drop it in `BepInEx/plugins/AdminPanel_Localization/` and restart the game. A disk file overrides the embedded copy key-by-key.
4. Run `pwsh tools/check-locales.ps1` to verify nothing drifted, then open a PR.

Any line you don't translate falls back to English on its own, so a partial translation is perfectly usable. To have a new language appear in the Settings dropdown it also needs an entry in `Loc.Shipped` / `Loc.MenuNames`.

## 🔨 Building from source

1. Install the [.NET SDK](https://dotnet.microsoft.com/download)
2. Get the game assemblies — the free [Valheim Dedicated Server](https://steamdb.info/app/896660/) via SteamCMD works:
   `steamcmd +login anonymous +app_update 896660 +quit`
3. Get BepInEx core DLLs (`BepInEx.dll`, `0Harmony.dll`) from the BepInExPack
4. Edit the `<ValheimManaged>` and `<BepInExCore>` paths at the top of each `.csproj`
5. `dotnet build` in each project folder

## ⚙ Configuration

Config file is created at `BepInEx/config/com.halitb.adminpanel.cfg`:
- Toggle key (default F7)
- Favorites, bookmarks, spawn presets, player notes (managed in-game)
- Bulk pack contents, crafter signature name

## ❗ Notes

- Built against Valheim **0.221.x**. Game updates may require a rebuild.
- Compatible with ValheimPlus and other BepInEx mods.
- Icon-less cosmetic prefabs (hair, beards) are intentionally drop-only — putting them in an inventory corrupts the vanilla inventory UI.

## 📜 License

MIT — see [LICENSE](LICENSE).

---

<sub><i>Advanced Admin Panel is an unofficial, fan-made mod and is not affiliated with or endorsed by Iron Gate AB or Coffee Stain Publishing. Valheim™ and its visual style are the property of Iron Gate AB. The logo is fan art inspired by Valheim's official logo design.</i></sub>
