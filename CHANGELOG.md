# Changelog

## 2.5.1

**Hotfix for Valheim 1.0.12 (the 11 September game update). BOTH DLLs are 2.5.1** — server owners: update
`AdminPanelCompanion.dll` on the server and restart, or the panel shows a ⚠ version-mismatch banner.

2.5.0 was compiled against a copy of the game assemblies that had not been updated since July. On the
current game the runtime silently refused every call whose signature had changed (the failures were caught
and logged as "did nothing"). All of it was found by reading the server's own log and rebuilding against the
live game; the projects now compile against the live install, so a future game update fails the build
instead of failing on your server.

- **The companion's data store never resolved its folder** - `World.m_fileName` no longer exists (the on-disk
  name is `World.m_worldName` now), so nothing server-side persisted (roles, warnings, temp-bans, ledgers,
  audit trail) and the lookup **spammed a HarmonyX warning ~30x per second** (a 213 MB `LogOutput.log` in one
  day). The store now resolves the world name silently and caches the lookup, with a fallback to the game's
  own DB path if the field is renamed again.
- **Rule broadcasts and the death-rules tick failed** with `Field not found: ZRoutedRpc.Everybody` - the game
  turned that field into a constant.
- **Every sector walk was dead**: tombstone search, area counts and cleanup, object block collection, the
  world scan's zone hotspots, the location finder and the "highest nearby object" helper all used the old
  `FindSectorObjects(zone, area, ...)` shape; the game now takes a `SimulationDistance` and zone coordinates
  are `Vector2s`. One shim (`ZoneCompat`) reproduces the old square walk on the new API.
- **Portal list and portal rescue** read `GetPortals()` as a flat list; it is now grouped per sector index.
- **Self-update version compare** picked up the game's own global `Version` class instead of `System.Version`.
- **Terrain reset (Build Tools)** called `Heightmap.Poke(bool)`; the signature changed.
- The Discord "world name" and backup path helpers used the removed `m_fileName` field and now share the
  fixed resolver; the world scan no longer warns about the removed `m_objectsByOutsideSector` bucket.

Also in this release: the in-panel **What's New** window now shows the 2.5.x notes (2.5.0 still showed the
2.3.0 text), and the Server tab's "waiting for server data" hint names the right companion version.


## 2.5.0

**BOTH DLLs changed** — `AdminPanel.dll` AND `AdminPanelCompanion.dll` are **2.5.0**. Server owners: update
the companion on the server and restart, or the panel will show a ⚠ version-mismatch banner and the new
server features will stay silent (unregistered RPCs are dropped without a log line).

This is the biggest release so far: the 2.4.0 "server-truth" Server tab that never shipped, plus a new
**Extras** tab with **33 optional modules** for dedicated-server admins. Every module that changes
behaviour on the server defaults **OFF**; passive views and logging default on. The whole tab can be
hidden with `[Features] EnableExtrasTab = false`.

> The Extras modules are new and have had far less field time than the eight base tabs. Single player
> is a smoke test; dedicated servers are the real proving ground. If something misbehaves, the in-panel
> Bug Report (side window) or the Discord is the fastest way to get it fixed.

### New — Server tab is now the "server truth" center (the 2.4.0 work)
Data is read authoritatively from the server instead of guessed by the client: uptime, real **ZDO count**,
**per-peer ping**, **last-save clock** and **next autosave**, the **server's BepInEx plugin roster** (spot a
stale companion at a glance), admin/banned/permitted lists, a **server-side join/leave log** that survives
your relogs, **force save**, and **offline ban / unban by ID** (crossplay IDs stored as entered). Reply
parsers reject anything that did not come from the server peer, so "server truth" cannot be spoofed.
Listen-server hosts get the same actions through a local bridge.

### New — the Extras tab (9th tab, one chip per module)
**Moderation & accountability:** Moderation (warn / mute / freeze / jail / watchlist) · Audit Trail (every
admin RPC, old and new, logged at one chokepoint — off the main thread) · Admin Roles (tiered grants,
custom roles) · Player Rap Sheet (notes, history, recent chat) · Direct Message · Guard (anti-cheat
probes; "no answer" is its own status, never "clean").

**Server operations:** Server Tools (MOTD, announcements, scheduled restarts, backups) · Diagnostics
(self-test, RPC round-trip, log tail) · Discord (one-way webhooks for joins, deaths, bans, boots) ·
Player Data (playtime, tombstones, last position, queued offline actions) · Economy (currency, shops,
reserved slots) · Extensions (a small SDK other plugins can register sections into) · Macros and Workflow
(command palette, favourites, help pages).

**World & build:** Area Tools (protection zones, bulk cleanup with an undo list) · Build Tools (piece
palette, snap/rotate helpers, mass repair) · Server-wide Map Pins · Map Reveal / Reset for any player ·
Location finder (nearest dungeons, altars, spawners) · Spawner and nest manager · Containers (chest
inspector / editor, container search) · Tames (roster, rename, cull) · Creature stat editor.

**Rules & events:** Recipe and build-piece Blacklist · Skill Gain Rules (rate and cap, server-enforced) ·
Trader stock and prices · Death rules (keep-inventory, tombstone handling) · Raid Composer (custom
multi-wave raids) · Bounty board · Staff Chat (admin-only channel) · Item Forge (give with attributes) ·
Client performance census · Companion self-update (staged for the next restart).

Actions that need the target player to run the mod (inventory, skills, status effects, map, trader,
blacklist enforcement) say so in the UI and are ignored, not broken, when they don't.

### Fixed
- **Crossplay bans** through the roster Ban button and offline ban now keep the full platform ID (they were
  prefix-stripped and silently did nothing). Unban matches the same way.
- **Force save** no longer blocks the main thread; guarded against overlapping saves.
- **Last-save timestamp** now works on headless dedicated servers (it relied on a client-only HUD event).
- **Server tab state resets** on disconnect, so server A's ban list is never shown as current on server B.
- **Kill nearby** has an *include tamed* toggle instead of always sparing tames.
- Extras chip row navigates in both directions; empty sections explain *why* they are empty
  (press Scan / nothing yet / multiplayer-only) instead of looking broken.
- **Economy:** a purchase could be settled with a *sale* confirmation (items kept AND money paid).
- **Unban** regression introduced by the crossplay ban fix — caught in review before release.

### Localization
All nine languages ship the complete 2065-key table (validator green). Community overrides in
`BepInEx/plugins/AdminPanel_Localization/` still work and fall back to English per line.


## 2.3.0

**BOTH DLLs changed** — `AdminPanel.dll` AND `AdminPanelCompanion.dll` are **2.3.0**. Server owners: update
the companion on the server and restart, or the panel will show a ⚠ version-mismatch banner.

### New — the panel speaks your language
Eight translations ship in the DLL: **Deutsch, Français, Español, Italiano, Português (BR), Polski,
Nederlands, Svenska**. By default the panel follows Valheim's own language setting; override it under
**Settings → Language**. Item, creature and status-effect names always follow the game, as before.

Translators: drop a `<code>.txt` file into `BepInEx/plugins/AdminPanel_Localization/` to override or add a
language without rebuilding the mod. A missing line falls back to English on its own, so partial
translations are perfectly usable.

### New — relevance-ranked search
Searching Items or Creatures now ranks results: exact match, then prefix, then word-start, then anything
else. Typing `iron` puts **Iron** first instead of burying it in an alphabetical list — previously "Banded
shield" (prefab `ShieldIronSquare`) came first. Typing `sledge` now finds "Iron sledge", which plain prefix
matching missed entirely. Your chosen sort still orders results *within* each relevance band.

Typing is also much cheaper: the list no longer re-filters and re-sorts the entire item database on every
single keystroke.

### New — multi-step undo, per admin
**Undo spawn** now steps back through your last **20** spawns instead of only the most recent one, and the
history is **per admin** — your undo can no longer delete a spawn another admin made. The server reports how
many steps are left after each undo.

### New — destructive actions ask first
**Kill ALL loaded**, **Ground items 50m**, **Clear trees 20m** and per-player **Ban** now arm on the first
click and only fire on a second click within 3 seconds.

### New — status effects for any player
The status-effect browser (Player → Status effects) now has the same **Me / any online player** target
picker as the skill browser. Effects are validated on the server and applied on the target's own client —
the target needs companion 2.3.0 on their side.

### Fixed — Undo actually removes objects on dedicated servers
Undo looked fine in the server log but the creature kept standing. Root cause: dedicated servers don't
instantiate creatures (the nearest client simulates them), and on that path Valheim's `ZDOMan.DestroyZDO`
is a **silent no-op unless the caller owns the ZDO**. The server now claims ownership of the raw ZDO first,
so the destroy actually broadcasts. Found by reading the server's own log against the client's.

### Fixed — scrolling panel lists no longer zooms the vanilla camera
2.2.9's fix restored the zoom distance *after* the camera had already been positioned from it, so every
scroll tick still visibly zoomed. The wheel reads are now muted at the IL level inside
`GameCamera.UpdateCamera`/`UpdateFreeFly` while the panel is open (verified at startup — grep the log for
`patch-verify`), and no longer tied to the camera-lock toggle.

**Known limitation:** ValheimPlus's *FirstPerson* feature implements a second, independent scroll-zoom.
This release includes a best-effort mute for it, but if your server force-syncs a V+ config with
FirstPerson enabled you may still see some camera zoom while scrolling the panel. Vanilla and non-V+
setups are fully covered.

### Fixed — layout at large font sizes
At font size 14+ labels tuned for the default size wrapped mid-word ("Broadcas / t:") and buttons
stretched to fill leftover row space ("Send to all" as a half-window slab). Form labels now grow with
their text, list columns clip instead of wrapping, and buttons size to their content.

### New — support the developer
An optional **Buy the developer a coffee** button in Settings opens PayPal in your browser. The mod is free
and always will be.

### Improved — Server tab
Added **ZDOs loaded**, **zones loaded** and **session uptime** alongside the existing stats.

### Improved — performance and rendering
- Live stats, player rosters, global keys, bookmarks and spawn presets are now built **once per frame**
  instead of on every IMGUI pass (which runs at least twice per frame).
- Fixed a latent crash: deleting a bookmark or saving a preset rewrote the config mid-frame, which could
  desync IMGUI's control count.
- If the game's Norse font cannot draw the active language, the panel now switches to a readable font
  automatically instead of rendering blank boxes.

## 2.2.9

**BOTH DLLs changed** — `AdminPanel.dll` AND `AdminPanelCompanion.dll` are now **2.2.9**. From this release
on the two files always share one version number, and the panel warns in-game when the server's companion
doesn't match ("⚠ Version mismatch"). Server owners: update the companion on the server and restart.

### New — manage player inventories
The Players-tab inventory viewer now has **Remove 1 / Remove all** per item. Removal runs on the target
player's own client (admin-validated through the server), unequips equipped items first, and pushes the
refreshed inventory back to your viewer automatically.

### New — skill browser with targets and private notes
**Player → Skills → Show skill browser**: every skill with its level and **−10 / −1 / +1 / +10 / +100 /
±custom** buttons. Apply to yourself or **any online player** — with an optional **private note** that pops
center-screen only for that player, together with the skill's new value. Empty note = silent change.

### Fixed — Skip night actually skips night
The old button flipped a local debug flag and did nothing (world time is server-owned). It now runs the
game's own sleep-skip on the server — same effect as everyone going to bed.

### Fixed — camera zoom while scrolling the panel (for real)
2.2.8's input patch was silently bypassed by JIT inlining. The camera's zoom distance is now pinned
directly while the panel is open — there is no path around it.

### More
- **Teleport to any online player** (World → Teleport), and Teleport / Area actions reorganized into
  labeled rows (To player / Bosses / Position / Bookmark · Combat / Cleanup / World).
- **Mod version on the title screen**, under the game's version line.
- **✕ close button** on the panel (and the side panel's What's New ↔ Bug Report switch works both ways).
- **Hotkeys can bind middle/side mouse buttons.**
- **Buttons grow to fit their text** — no more clipped labels; the panel can no longer get stuck on the
  wrong font after visiting the main menu.
- **Bug-report screenshots include the panel** (that's usually the thing being reported).

## 2.2.8

**Only `AdminPanel.dll` changed** (→ 2.2.8). The companion stays at 2.1.2.

### Fixed — panel styles broke after logging out and back in
Logging out to the main menu made the game unload the textures and font behind the panel's skin, so after
relogging every button, tab and chip rendered as flat text with no background. The skin now survives the
unload sweep and self-heals if anything still gets destroyed, and all per-world caches (item icons,
creatures, player roster) reset cleanly on logout instead of pointing at destroyed objects.

### New — What's New & in-panel bug reports
A side panel docked to the main window, with two tabs:
- **What's New** — opens once after each update (toggle in **Settings → Panel**).
- **Bug Report** — describe a bug and send it straight from the game, with an optional screenshot
  (the panel hides itself for the capture). A **Join our Discord** button is right there too.

### New — TP to last death
Your death position is recorded automatically; **Player → Quick actions → TP to last death** takes you back
to your corpse. Survives a relog.

### New — update notice
When a newer version is released, the panel shows a banner at the top with a **Get update** button
(one quiet GitHub check per game launch; fully silent when offline).

### Fixed — scrolling a panel list no longer zooms the camera
The mouse wheel was reaching the game camera while the panel was open. Blocked the same way as
mouse-look (honors the **Settings → Behavior** camera-lock toggle).

### Changed — cleaner tabs
Every tab is reorganized with section dividers (Settings, Player, World, Players, Server, Bosses), so long
tabs read as titled sections instead of one wall of controls.

## 2.2.7

**Both DLLs changed.** `AdminPanel.dll` → 2.2.7, `AdminPanelCompanion.dll` → 2.1.2. Update both.

### Fixed — admin actions did nothing in single-player
If you hosted the game yourself (single-player, or a listen-server for friends), **every** server-executed
action silently did nothing: spawning items/creatures/bosses, Bag/Give, kits, undo, summon, heal, kick, ban,
broadcast, events and peaceful mode. The panel said "Requested ..." and nothing happened.

The companion identified the caller by looking them up in the server's peer list, but a host is never in that
list — it only ever contains *remote* connections. So the host failed the admin check before `adminlist.txt`
was even read, which is also why adding your own Steam ID to the list didn't help. The host is now recognised
as an admin directly, the way the game itself does it.

Client-side features (buffs, teleport, fly, the map-point `T` teleport) were never affected, which is why
those kept working while spawning didn't.

**Dedicated servers were never affected** — admins connect as remote clients and were always resolved
correctly. Server owners can update at their convenience; there is no behaviour change for them.

### New — logo header
The Advanced Admin Panel logo now sits above the tabs. Toggle it in **Settings → Show logo header**.

### Notes
- In single-player you no longer need an `adminlist.txt` entry (it never actually worked there anyway).
- Denied admin actions are now logged instead of failing silently, so this can't hide again.

## 2.2.6

### New — Settings tab
The panel has a **Settings** tab so admins can customize it in-game (no config-file editing):
- **Font** — Norse (auto), Norse Bold, Norse, Averia Serif, or Default; missing fonts fall back gracefully.
- **Font size** — 10–20 slider; the whole panel scales from it while headers/tabs keep their hierarchy.
- **Panel opacity** — 55–100% background transparency.
- **Camera lock toggle** — turn the inventory-style camera lock off if you prefer a live camera while the panel is open.
- **Hotkey rebinding** — rebind the panel key (F7) and map-teleport key (T) from the UI; Esc cancels.
- **Reset buttons** — reset window size/position or all appearance settings.

Everything persists in the BepInEx config and defaults render exactly like v2.2.5. Sliders preview live and only write to disk when you release the mouse.

### Changed
- Minimum panel width raised 560 → 660 px so all 8 tabs always stay clickable in one row.
