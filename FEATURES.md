# Advanced Admin Panel — Feature Reference

**The all-in-one, server-secured admin toolkit for Valheim.**
Press **F7** in-game to open the panel — spawn anything, teleport anywhere, moderate anyone, and make the panel yours.

- **Version:** `2.5.1`
- **Author / Team:** Hephaestus Labs
- **Dependencies:** BepInEx 5.4 only — no Jötunn, no wrappers
- **Ships:** two DLLs — `AdminPanel.dll` (client) + `AdminPanelCompanion.dll` (server-side validator)
- **Languages:** 9 (English, Deutsch, Français, Español, Italiano, Português BR, Polski, Nederlands, Svenska)

> _This document reflects the **current in-development build** — the 2.4.0 feature set plus the card/skin
> UI overhaul that has not yet been versioned or released._

---

## Table of contents
1. [Why this panel](#why-this-panel)
2. [The eight tabs](#the-eight-tabs)
   - [Items](#-items) · [Creatures](#-creatures) · [Bosses](#-bosses) · [Player](#-player)
   - [World](#-world) · [Players](#-players) · [Server](#-server) · [Settings](#-settings)
3. [UI / UX overhaul](#ui--ux-overhaul)
4. [How the security works](#how-the-security-works)
5. [Localization](#localization)
6. [Compatibility](#compatibility)
7. [Installation](#installation)
8. [Technical notes](#technical-notes)

---

## Why this panel

- **Zero framework baggage** — no Jötunn, no wrappers, no dependencies beyond BepInEx. Two DLLs and you're done.
- **Server-validated** — every action that touches the world or another player is checked **server-side**
  against `adminlist.txt`, so a copied DLL can't touch your server.
- **Speaks your language** — ships in 9 languages and follows your game's language automatically.
- **Feels native** — wood, parchment & gold UI in Valheim's own Norse font; resizable, draggable, and it
  remembers its size and position between sessions.

---

## The eight tabs

### 🎒 Items
- **Every item** in the game, each with its **icon**.
- **Relevance-ranked live search** — typing `iron` puts *Iron* first instead of burying it alphabetically;
  ranking order is exact → prefix → word-start → substring → prefab match, ties broken by shorter name.
  Debounced (150 ms) so it doesn't re-sort the whole database on every keystroke.
- **Real item stats** — weapon damage, armor values, food values (health/stamina/duration).
- **Categories & sub-categories** — including **materials grouped by biome**.
- **Favorites** and **recents** for the items you use most.
- **One-click gear kits** — full loadouts from **Bronze → Ashlands** in a single click.
- **Bulk packs** for common stacks of materials.
- **Give straight into any online player's inventory**, not just your own.

### 🐺 Creatures
- **Spawn anything by faction**, with a chosen **count** and **star level**.
- Spawn **at your crosshair**, or **pre-tamed** with a custom **pet name**.
- **Saved presets** for creature setups you reuse.
- **Multi-step undo** — steps back through your **last 20 spawns**, tracked **per admin** (your undo never
  deletes another admin's spawn). The server replies with how many steps remain.
- **Arena mode** — pick faction **A vs B** and press **FIGHT!**

### ⚔ Bosses
- **One-click boss summons** — Eikthyr → Fader.
- **Instant altar offerings** — hand yourself the required offering items in one click.
- **Real raid events** triggered server-side at your position, now organized into two labelled groups:
  - **Boss Raids** — Eikthyr, The Elder, Bonemass, Moder, Fuling Horde, Seekers, Gjall, Charred Legion.
  - **Creature Raids** — Trolls, Skeletons, Blobs, Wolves, Bats, Surtlings.
  - Buttons show **friendly names**; the correct vanilla event still fires under the hood.

### 🧙 Player
Organized into **subcategory chips** — pick a section and only that one shows:

- **Toggles** — god mode, ghost, fly, free build, no stamina, one-hit kill.
- **Multipliers** — speed, jump, and pickup-range sliders.
- **Quick Actions** — common one-shot actions.
- **Skills** — a full skill browser with live level and per-skill **−10 / −1 / +1 / +10 / +100 / ±custom**,
  targeting **yourself or any online player** (with an optional **private note** shown only to that player).
  Plus **All skills +10 / All skills 100 / Reset skills**.
- **Status Effects** — a **categorized, searchable browser** you can **apply to any player**; buffs
  **persist through death**.

### 🌍 World
- **Time & weather control**, including **wind for sailing**.
- **Teleport section** (rebuilt with aligned rows so everything lines up):
  - **To Player** — teleport to any online player.
  - **Sacrificial Stones** — quick-jump to boss altars.
  - **Coordinates** — teleport to exact X/Y/Z.
  - **Last Death** — jump back to where you last died.
  - **Bookmark** — save and recall named locations.
  - **Map teleport** — open the map, hover any point, press **T** to teleport there.
- **Area tools** — the destructive ones (kill all loaded, ground items, clear trees) require a
  **second click** within 3 s before firing.
- **Global keys** — set/clear world progression flags.

### 👥 Players
Live roster of everyone online. Per player:
- **Teleport to** · **Summon** · **Spectate** · **Heal** · **Ping** · ⚡ **Lightning strike**.
- **Live inventory viewer** with **item removal**.
- **Kick** / **Ban** (ban asks twice before firing).
- **Admin notes** per player.

### 🖥 Server
The "server truth" center — data read authoritatively from the server, not guessed by the client:
- **Live stats** — authoritative **ZDO count**, **zones loaded**, and **session uptime**.
- **Version handshake** — panel ↔ companion version check with a ⚠ **mismatch warning** banner.
- **Server plugin roster** — the list of BepInEx plugins actually loaded on the server (the stale-companion
  detector).
- **Per-peer ping**, **last-save clock**, and **next-autosave** time.
- **Force save** the world on demand.
- **Unban by Steam ID** (offline ban/unban supported).
- **Join / leave history** — server-side, so it survives an admin relog.

### ⚙ Settings
Everything below is changed **in-game** and **persists**:
- **Language** (or Auto — follows Valheim's own language setting).
- **Font**, **font size**, and **panel opacity**.
- **Logo header** toggle.
- **Camera lock** and **hotkey rebinding** (including mouse buttons 2–6).
- **Support** — an optional "buy the developer a coffee" button that opens PayPal in your browser.

---

## UI / UX overhaul

The recent visual overhaul (in development) modernizes the whole panel:

### Layout — card system
- Content is grouped into **bordered "cards"** (`BeginCard`/`EndCard`) so related controls read as one block
  instead of a mixed wall of buttons. Applied to the World, Server, Player, Players, Bosses, and Settings
  tabs. Items & Creatures remain fast list-browsers by design.

### Skin — real Valheim runtime textures
- The window, dividers, and card backgrounds **borrow Valheim's own loaded UI textures at runtime**
  (wood panel, separators, panel backgrounds) — nothing from the game is copied or redistributed.
- Wood / parchment / gold palette drawn in Valheim's **Norse font**.
- **Config toggles** (`[UI]` section):
  - `UseGameSkin` (default **true**) — turn the runtime skin on/off.
  - `LogSkinAssets` (default **false**) — dump UI texture names to the log for discovery.
- **Darker brown background** — a warm multiply tint deepens the wood to a richer brown while buttons,
  fields, text, and gold rules stay full-colour.
- **Procedural bordered buttons** — dark wood centre with a bronze/gold frame and clearly distinct
  **hover** (brighter) and **pressed** (recessed) states.

### Organization
- **Player tab subcategory chips** — Toggles · Multipliers · Quick Actions · Skills · Status Effects, one
  section at a time (mirrors the Items tab).
- **Raid Events** split into **Boss Raids** / **Creature Raids** with friendly names.
- **Teleport section** rebuilt with an aligned label column; boss altars renamed **Sacrificial Stones** and
  **Teleport to Last Death** moved in so all teleports live together.
- **Title Case** on all section and card headers.
- **Window-scaled lists** — the Skills and Status-Effects browsers grow with the window instead of stopping
  at a fixed height, so a tall panel shows many more rows with no wasted space.

---

## How the security works

This is a **client** mod, so the panel window opens for anyone who installs the DLL — no client mod can
prevent that. What the companion protects is **your server**.

- Every action that touches the world or another player — **spawn, give, teleport, kick, ban, unban, heal,
  inventory view/remove, broadcast, world events, skills, status effects, skip night, force save** — is
  sent to the companion as a **request** and validated **server-side against `adminlist.txt`**.
- The companion **re-stamps the true sender ID**, so forged or spoofed requests are dropped and logged.
- A non-admin who copies the DLL onto your server gets a panel full of buttons that **do nothing**.
- **Self-only toggles** (god mode, fly, no-stamina) run on that player's own client and are **not**
  server-validated — that's true of every Valheim client mod, since the game trusts clients for their own
  character. Pair with a server-side anticheat if you need to stop that too.

---

## Localization

- Ships with **English, Deutsch, Français, Español, Italiano, Português (BR), Polski, Nederlands, Svenska**.
- **Follows Valheim's own language setting** by default (override in **Settings → Language**).
- Item and creature names always follow the game.
- **Add your own language** — drop a `.txt` into `BepInEx\plugins\AdminPanel_Localization\` — no rebuild
  needed, and any line you don't translate falls back to English per-line.

---

## Compatibility

- **Works with other mods** — custom weapons, armor, and creatures from other installed mods appear in the
  panel automatically. (To spawn modded content on a dedicated server, that mod must also be installed
  server-side.)
- Compatible with **ValheimPlus** and other BepInEx mods.

---

## Installation

**Requires** [BepInEx for Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/).

### Single-player / client
1. Install BepInEx.
2. Copy **`AdminPanel.dll`** and **`AdminPanelCompanion.dll`** into `<Valheim>\BepInEx\plugins\`.
3. Add your 64-bit Steam ID to **`adminlist.txt`**.
4. Launch the game and press **F7**.

### Dedicated server
- Put **`AdminPanelCompanion.dll`** in the server's `plugins` folder and add admin Steam IDs to the
  server's `adminlist.txt`.
- Each admin's **client** needs **both** DLLs.
- **Both files always ship with matching version numbers** — the panel warns in-game if the server's
  companion is out of date.

---

## Technical notes

- **Rendering:** Unity **IMGUI** (`OnGUI`/`GUILayout`), not Valheim's UGUI — no prefab or Canvas dependency.
- **Architecture:** BepInEx 5.4 + HarmonyX plugin; client panel + server companion communicate over
  admin-gated RPCs (`AP_Srv*` request → server re-stamps sender → validated action).
- **Version policy:** the panel and companion always ship the **same** version number and perform a
  handshake (`AP_SrvVersion` → `AP_VersionData`); a mismatch shows a ⚠ banner.
- **Fonts:** the panel resolves Valheim's Norse font at runtime and falls back to Unity's default font if
  the chosen font can't render the text (e.g. non-Latin item names).

---

_Advanced Admin Panel is an unofficial, fan-made mod and is not affiliated with or endorsed by Iron Gate AB
or Coffee Stain Publishing. Valheim™ and its visual style are the property of Iron Gate AB._
