# Advanced Admin Panel — Feature Backlog, Round 3

> **STATUS 2026-10-15: ALL 15 BUILT (unreleased, not deployed, not committed).** Both DLLs build (0 errors), companion
> bind probe on the local 1.0.20 dedicated server: 1955 methods, 0 failed (after the review fixes). 358 new locale keys × 9 languages (validator
> green). Code: client `Features\Wave9_*.cs` + `Wave9Glue.cs`, companion `Wave9Srv*.cs` + `FeaturesWave9Glue.cs`; the main
> file only gained one-line partial-method hooks. Where each item landed and its honest limits:
>
> | # | Built as | Limits |
> |---|---|---|
> | 1 | Provenance stamps on give/kits/bulk/forge/offline/drops; Tools › Moderation › Provenance scan; Items-list marker | Players without the companion: inventories unreadable (seen once items hit a chest/ground). Stackables are judged by total, not by copy. |
> | 2 | Creatures tab → Command card (follow / guard / passive / attack / normal) | Guard = vanilla patrol (works for all); follow/passive/attack need the companion on the simulating client. |
> | 3 | Bosses tab → Encounter card (HP ×, +HP per player, damage ×, enrage), fight recorder, Discord post | A hit on the player whose client simulates the boss is scaled only when that client runs the companion. Only hits on players are scaled. |
> | 4 | Player tab → Status Effects → Forge card (runtime SE_Stats presets) | Other players need the companion (the recipe travels with the apply). |
> | 5 | World tab → Locations card (loot state, reset one / all of a type looted N days ago) | "Looted since" counts from the first scan that saw it looted. Refused while any of its chests holds items (empty them first). Owner-only. |
> | 6 | World tab → Teleport card map-layer chips (+ chips over the open map) | Pins are unsaved and refreshed on demand. |
> | 7 | Tools › World › Zones (rule zones; no-build/no-damage write through to wave-5 protection zones) | PvP/keep-inventory/portals/drops/speed are client-side (companion); rectangles use their enclosing circle for no-build. |
> | 8 | Tools › World › Bases (cluster, last seen, claim / ruin / delete + snapshot restore) | A builder the ledger never recorded counts as gone since the ledger started, so nothing qualifies until the ledger is older than the asked-for days. Claim / ruin / delete / restore are owner-only. |
> | 9 | Players tab → Progression card; flags into Guard as rule "progression" (never escalates) | "First craft" is "first seen equipped" (crafting is invisible to the server). |
> | 10 | Server tab → Performance card (ZDO ownership per peer + ping, Release / give to a nearby peer) | A hand-off sticks only while the new owner stays near (engine rule). |
> | 11 | Server tab → Backup compare card (live or A vs B, per 64 m area, restore removed / chests / remove added) | Chunked backups only; objects matched by prefab + position (chunk saves keep no ids), so ships and carts are left out. A result expires after 30 min; restore is owner-only; "remove added" keeps chests that hold items. |
> | 12 | Tools › Server › Automation (6 triggers, 3 conditions, 7 actions, Test) | give/effect actions need the companion on the target client. |
> | 13 | Title-bar Undo (two clicks) + Tools › Shortcuts › Undo timeline | Journal is in memory (last 500, cleared on world change, also logged to w9_undo.log). Give-undo takes back only the serial-stamped copies of that grant (needs provenance stamping on and the companion on the target); skill-undo reverses the change the target confirmed. Undoing another admin's row needs moderator. |
> | 14 | Settings tab → Diagnostics card + top notice; auto input priority (GUI.depth) | — |
> | 15 | Settings tab → Panel card → Controller mode (Auto/On/Off) | Sliders are not focusable (use the +/- buttons where present). |
>
> **Found and fixed on the way:** the Wave 8 chest viewer / container search could never read a chest since Valheim 1.0.12
> (contents are a ZDO byte array in layout 109/110, the code read a base64 string in layout 106). Shared codec
> `Wave9SrvInvCodec.cs`, verified lossless on 732 real chests from the GTX world. Also: the chest editor's "is this still the
> stack you saw" check was sent by the panel since 2.5.0 but never read by the server; it is now enforced.
>
> **Review (2026-10-15/16):** three review agents over the finished code, 43 findings (6 HIGH: undo permissions,
> give-undo taking the player's own items, skill-undo overshooting the 0/100 clamp, base actions hitting the wrong base
> after a rescan, backup restore acting on another admin's comparison, moving ships counted as removed + added). All
> confirmed findings fixed; see CHANGELOG "Hardening from the code review".
**Origin:** 2026-10-15, request for "15 advanced features, organized as always in the panel". **None of these
are built.** Each idea was checked against `FEATURES.md`, `FEATURE_BACKLOG.md` (105 ideas) and
`FEATURE_BACKLOG_ROUND2.md` (25 ideas) so nothing repeats; where an idea extends an existing module it says
which. Every game API named below was confirmed in the Valheim 1.0.20 decompile
(`valheim-modding\decompile-1.0.20`).

**How to read the tags:** effort `S` / `M` / `L` · `server` = companion-only, works for every player ·
`client` = panel-only, no server change · `target-mod` = the other player's client must also run the mod.

**Where** names the tab and card (or Extras category and section) each feature lands in. House rules that
apply to all 15: one card per feature; destructive buttons use the two-click confirm; anything that changes
the shared world goes through the companion and the admin gate; every string goes through `Loc` in all 9
locales and uses only glyphs the panel font has (`tools\font-glyphs.ps1`).

---

## Items tab

### 1. Item provenance stamps and dupe finder  `[L · server]`
- **What:** Every item the panel creates (give, kits, bulk packs, item forge, offline grants) gets a stamp
  in `ItemData.m_customData`: admin ID, time, and a unique serial. A companion scan walks online
  inventories, chests and ground items and reports (a) the same serial in two places, meaning a duplicate,
  and (b) admin-made items held by players who never received a grant.
- **Why:** Dupe exploits are the most common economy breaker, and today nobody can tell an admin-spawned
  sword from a crafted one. Pairs with round 1's "who has item X" audit and round 2's chest search.
- **Where:** Items tab → a small "stamped" marker on item rows; the scan lives in
  Extras › Moderation → "Provenance" card.

## Creatures tab

### 2. Creature command mode  `[M · server]`
- **What:** Give orders to the creatures in a radius, or the one under the crosshair: follow a player,
  guard a point within a patrol radius, stay passive, or attack a named target. Uses
  `MonsterAI.SetFollowTarget` and `BaseAI.SetPatrolPoint`, run on the ZDO owner.
- **Why:** Turns spawned creatures into event actors (escorts, guards, ambushes) instead of a mob that
  wanders off. Round 2's creature editor changes stats, not behavior.
- **Where:** Creatures tab → new "Command" card under the spawn controls.

## Bosses tab

### 3. Boss encounter tuner and fight recorder  `[L · server]`
- **What:** Before a summon, set HP and damage multipliers, optional scaling per nearby player, and an
  enrage timer. During the fight the companion records damage per player, deaths and time-to-kill; at the
  end a summary card appears and can be posted to Discord.
- **Why:** Vanilla bosses are trivial for a geared group and brutal for one player; servers want both.
  Kill summaries also feed the existing leaderboards and weekly digest.
- **Where:** Bosses tab → "Encounter" card above the summon buttons.

## Player tab

### 4. Custom status-effect forge  `[L · server + target-mod]`
- **What:** Build a new effect from parts (speed, health/stamina regen, damage and resistance modifiers,
  carry weight, duration, name) as an `SE_Stats` created at runtime and registered on every modded
  client. Apply it like any existing effect and save it as a preset.
- **Why:** Event buffs ("Blessing of Odin: +20% speed for 10 min"), PvP handicaps, VIP perks. Round 2's
  item forge does this for items; nothing does it for effects.
- **Where:** Player tab → Status Effects chip → "Forge" card beside the browser.

## World tab

### 5. Dungeon and location regeneration  `[L · server]`
- **What:** Reset a looted crypt, burial chamber or any other location instance: delete its ZDOs and
  respawn it from `ZoneSystem.m_locationInstances` via `SpawnLocation`, so chests and spawners refill.
  One location, a radius, or "every location of type X looted more than N days ago". Refuses while a
  player is inside.
- **Why:** Long-running servers run out of dungeons, and today the only fix is a new world. Builds on
  round 2's location finder, which can only find.
- **Where:** World tab → new "Locations" card (finder and reset share it).

### 6. Admin map overlays  `[M · server]`
- **What:** Layers you can toggle on the vanilla map: wards with their radius, portals and their links,
  ships, tames, recent deaths, ZDO density heat. Drawn as unsaved `Minimap.AddPin` pins with custom
  icons, fed by one companion snapshot RPC.
- **Why:** Most admin questions are about places: "where are the lag bases?", "whose ward is this?".
  The data already exists across a dozen cards; this puts it on the map, where admins look.
- **Where:** World tab → Teleport card's map row gets layer chips; the same chips show on the map while
  the panel is open.

### 7. Rule zones  `[L · server + target-mod]`
- **What:** Extend round 1's no-build zones into general rule zones: forced PvP, keep inventory on death,
  no portals, no item drops, speed or healing aura, an entry message. Circle or rectangle, drawn on the map.
- **Why:** Arenas, safe towns and event grounds each need their own mod today.
- **Where:** Extras › World → "Zones" card (no-build becomes one rule among several).

### 8. Abandoned-base finder and cleanup  `[M · server]`
- **What:** Group pieces into bases by distance and `Piece.GetCreator()`, join them with round 1's
  last-seen ledger, and list bases whose builders have been gone N days, with piece count, ZDO cost and
  location. Actions: teleport, claim, turn into ruins (remove a percentage), or delete after a snapshot.
  Ships and carts left far from any base are listed too.
- **Why:** Abandoned bases are the biggest source of ZDO bloat and lag on old servers, and finding them
  by hand is impossible.
- **Where:** Extras › World → "Bases" card next to the ward manager.

## Players tab

### 9. Progression timeline and anomaly flags  `[M · server]`
- **What:** Record per player when they first entered each biome, each boss kill they were present for,
  and their first craft of each tier material (bronze through flametal). Flag impossible jumps, such as
  Ashlands gear 20 minutes after first joining, or a boss kill without the boss before it.
- **Why:** Gives round 1's anti-cheat monitor a progression signal, and gives the profile card a "how far
  along" view.
- **Where:** Players tab → profile card → "Progression" section.

## Server tab

### 10. ZDO ownership and lag-owner finder  `[M · server]`
- **What:** List how many ZDOs each peer owns, next to their ping. One click hands ownership of an area to
  the server or to a better-connected peer (`ZDO.SetOwner`).
- **Why:** A laggy player who owns a busy base lags everyone near it. It's the most common cause of "the
  server is laggy" on Valheim, and invisible without tools. The round-1 density finder shows where; this
  shows who.
- **Where:** Server tab → "Performance" card.

### 11. World diff between backups  `[L · server]`
- **What:** Compare the live world (or backup A) with backup B: pieces added and removed per area with
  creator IDs, and containers whose contents changed. Click a row to teleport there or restore just those
  objects.
- **Why:** Turns round 1's backups from all-or-nothing rollbacks into a forensic tool: "what changed at
  this base since last night?"
- **Where:** Server tab → "Backups" card → "Compare" button.

## Extras

### 12. Trigger rules (if this, then that)  `[L · server]`
- **What:** Server-side rules. Trigger: a player joins, enters a zone, kills a boss, dies, types a chat
  keyword, or a time of day arrives. Conditions: role, first time only, cooldown. Actions: broadcast,
  grant an item or effect, teleport, start an event, post to Discord.
- **Why:** Automates repetitive admin work that macros (client-side, run by hand) and scheduled events
  (time only) cannot.
- **Where:** Extras › Server → "Automation" card, one rule per row with an on/off switch and a Test button.

### 13. Universal undo timeline  `[L · client + server]`
- **What:** Every reversible action records its inverse: give → remove, teleport → return,
  spawn → despawn, piece edit → restore, skill or effect change → revert. One timeline shared by all
  admins; undo any single row, or "everything admin X did in the last 10 minutes".
- **Why:** Undo exists per feature today (spawn undo, area snapshots). One timeline makes mistakes cheap
  everywhere.
- **Where:** Title bar → Undo button for the last action; the full timeline under Extras › Shortcuts.

## Settings tab

### 14. Input conflict detector  `[S · client]`
- **What:** Compare Unity's raw mouse state with the IMGUI events the panel receives. When presses reach
  the panel's buttons but releases never do, another mod is consuming the mouse-up. Show a notice naming
  the loaded plugins that draw their own IMGUI, plus a "test click" button.
- **Why:** Straight from the 2026-10-14 Discord report: hover worked, clicks did nothing, and the player
  had no way to know another mod was at fault.
- **Where:** Settings tab → new "Diagnostics" card, plus a one-line notice at the top of the panel when it
  trips.

### 15. Gamepad and Steam Deck mode  `[M · client]`
- **What:** Controller navigation (D-pad moves focus, A presses, B backs out, bumpers switch tabs), larger
  touch targets, and the Steam on-screen keyboard for search fields.
- **Why:** IMGUI has no controller support at all, so Deck players can't use the panel without a mouse.
  The 2026-10-15 font fix (no more □ boxes on Linux) removed the other Deck blocker.
- **Where:** Settings tab → Panel card → "Controller mode" switch, turned on automatically when a gamepad
  was the last input.

---

## Suggested order
- **Quick wins first:** 14 (input detector, small and tied to a live support case), 10 (lag-owner finder),
  6 (map overlays, mostly reuses existing data).
- **Then the moderation/forensics set:** 1, 9, 11, which share the scan-and-report shape.
- **Then world management:** 8, 5, 7.
- **Then the big systems:** 12, 13, 4, 3, 2, 15.
