# Advanced Admin Panel — Feature Backlog (recovered)

**Origin:** 8-agent brainstorm on **2026-07-22 07:09 UTC** (session `faf78c82`), recovered on **2026-07-31**
after the list was lost to the UI-overhaul pivot. **None of these are built.** This file is the durable copy —
do not lose it again.

**How to read:** `effort` S/M/L · `RPC` = needs new companion RPC(s) · `tier` = original agent's priority rating.

## Proposed release sequencing (from the original brainstorm — never started)
- **v2.5.0 "Accountability"** — admin audit log + tiered roles/permissions + temp-bans + chat mute + player rap sheet
- **v2.6.0 "Server owner's toolkit"** — world backups/restore + ZDO census/lag-finder + worldwide cleanup + companion self-test + scheduled restarts
- **v2.7.0 "Discord bridge"** — live event feed + slash commands + admin alerts
- **Then:** the character-vault arc (server-side snapshot vault unlocking ~6 player-data features)

---

## Domain: moderation (13 ideas)

### 1. Admin Audit Log (accountability backbone)  `[M · RPC]`
- **Tier:** S — build first
- **What:** Companion logs every admin RPC at its dispatch chokepoint — timestamp, real sender (already re-stamped by RouteRpcSanitizer so it can't be forged), action, target, and params — to a rolling server-side file. A new 'Audit' tab pulls the tail via a read RPC (AP_SrvAuditReq, gated by SenderIsServerReply) with filter-by-admin / by-target / by-action, and one-click reverse on undoable entries (unban, un-mute, un-freeze).
- **Why:** The single biggest gap for a multi-admin community server: accountability. Today a careless or rogue admin's spawns, bans, and item-removals leave zero trace. The companion already sees and validates every action, so logging is nearly free to bolt on — and it becomes the record every other moderation feature writes into.

### 2. Temp-bans with expiry + reason  `[M · RPC]`
- **Tier:** S — build first
- **What:** Extend ban to carry a duration and reason. Companion keeps a parallel timed-ban store (vanilla banlist.txt has no expiry), auto-expires entries, and re-checks on join — kicking with the stored reason until expiry, then re-admitting automatically. Panel lists active temp-bans with remaining time and a manual early-release.
- **Why:** Vanilla only does permanent bans, so admins either forgive-and-forget or track '3-day ban' in a spreadsheet. Timed bans with reasons are table stakes for community servers and something no popular Valheim admin mod ships cleanly.

### 3. Kick/ban reason → target + Discord mod-log + appeal seed  `[M · RPC]`
- **Tier:** A — high value
- **What:** Give kick/ban a reason field surfaced to the target as the disconnect message. Companion emits a moderation record the client relays to a Discord mod-log channel via the existing webhook/Ikarus infra — who banned whom, why, expiry — stamped with an appeal instruction. That off-server record is the ban-appeals surface (banned players can't reach the in-game panel).
- **Why:** Silent kicks/bans breed 'why was I banned?' drama. A permanent Discord paper trail plus a clear appeal path is exactly what accountable communities want, and the release-announce webhook pipe already exists to carry it.

### 4. Chat mute / gag (timed)  `[M · RPC]`
- **Tier:** S — build first
- **What:** Timed server-side mute: companion Harmony-patches the server chat relay (Talker/Chat RPC) and drops a muted player's Say/Shout before rebroadcast, optionally whispering 'you are muted (Nm left)' back to them. Panel mute button per roster row with a duration picker; auto-unmute on expiry; logged to the audit trail.
- **Why:** Valheim has no server-side mute — an abusive or spamming player can only be kicked or banned, which is disproportionate. Mute is the missing middle rung of the moderation escalation ladder.

### 5. Tiered roles & granular permissions (moderator tier)  `[L · RPC]`
- **Tier:** S — strategic (top value, largest effort)
- **What:** Companion maintains its own roles/permissions file layered over adminlist.txt: e.g. a 'moderator' tier that may kick/mute/teleport/heal but not ban/spawn/give/access-lists. Every AP_Srv RPC is gated by the sender's role+permission (not just the binary admin check), and the panel greys out actions the caller lacks. Owner assigns roles from the panel.
- **Why:** Vanilla adminlist is all-or-nothing: to let someone kick a griefer at 3am you must hand them full spawn/give/god cheats. Granular delegation is THE feature community owners ask for and neither ValheimPlus nor the stock admin tooling provides it. Highest-value differentiator; L effort but strategic.

### 6. Freeze / jail a player  `[M · RPC]`
- **Tier:** A — high value
- **What:** Freeze: an admin-validated RPC to the target's client roots the character and blocks input until released — stop a live griefer without kicking. Jail: teleport-confine them to a designated cell and snap them back if they leave the radius. Both logged; release from the panel or on timeout.
- **Why:** Between 'ignore' and 'ban' there is no way to physically stop someone mid-grief. Freeze/jail lets an admin pause a situation, talk it out, or gather evidence — standard on Minecraft/Rust mod stacks, absent in Valheim.

### 7. Alt / ban-evasion detection  `[M · RPC]`
- **Tier:** A — high value
- **What:** The server-side join log already records each peer's endpoint. Companion groups sessions by IP/endpoint to flag likely alts (multiple character names / UserIDs from one address) and, when a timed-banned or watchlisted endpoint reconnects under a new name, auto-kicks and alerts online admins. Panel 'possible alts' view per player.
- **Why:** Ban evasion via a fresh character is trivial in Valheim. Surfacing shared-endpoint clusters turns data the companion already collects into real evasion defense — nothing else in the ecosystem does this.

### 8. Warnings with auto-escalation  `[M · RPC]`
- **Tier:** A — high value
- **What:** Issue a formal warning (with reason) to a player: stored server-side, shown to them in-game, and counted. Configurable auto-escalation — N warnings within a window → automatic temp-ban. Panel shows each player's warning count/history; warnings feed the audit log and player rap sheet.
- **Why:** Gives moderation a documented, progressive ladder (warn → mute → temp-ban) instead of jumping straight to the ban hammer, and the paper trail defuses 'you banned me with no warning' disputes.

### 9. Server-side chat history buffer  `[M · RPC]`
- **Tier:** A — high value
- **What:** Companion keeps a rolling server-side buffer of recent chat (Say/Shout/Whisper with sender + timestamp), readable in a panel 'Chat log' view — including messages sent before the admin logged in. Configurable retention; gate it behind a permission and flag it as privacy-relevant capture.
- **Why:** When a player reports 'he said X and threatened me,' admins have zero recourse if they weren't online at the time. A reviewable chat log is essential evidence for fair moderation and pairs naturally with mute.

### 10. Player rap sheet (history-of-record profile)  `[M · RPC]`
- **Tier:** A — high value
- **What:** Per-player profile aggregating what the companion already/newly knows: first-seen, session count, total playtime and last-seen (from the persistent join log), admin notes, active/past warnings, bans and mutes, and known alts — one consolidated view opened from any roster row.
- **Why:** Player context is currently scattered across the roster, the notes field, and the join log. A single history-of-record view lets an admin make an informed call in seconds and is the natural home for every other moderation signal.

### 11. Watchlist with join alerts  `[M · RPC]`
- **Tier:** B — solid add-on
- **What:** Flag a player as watched (with a why-note). When a watched player connects, the companion pushes a join alert to every online admin's panel (toast + audit entry) so they can spectate or prepare. Managed from the panel; feeds the rap sheet.
- **Why:** Owners often want to keep an eye on a borderline player without banning them. Proactive join alerts turn 'someone noticed later' into 'the right admin was watching from the moment they connected.'

### 12. Panic / server lockdown mode  `[M · RPC]`
- **Tier:** A — high value
- **What:** One-click lockdown: companion sets a flag that rejects all new non-admin / non-whitelisted joins with a custom message, and optionally kicks all current non-admins — for shutting down a coordinated griefer raid or exploit wave. Toggle off just as fast; the toggle and its state are audited.
- **Why:** During a raid by multiple alts there is no fast 'seal the doors' control today; admins ban one account at a time while damage compounds. A single lockdown toggle buys time to clean up.

### 13. Anti-grief spike alerts  `[L · RPC]`
- **Tier:** B — high impact, higher cost
- **What:** Companion watches for suspicious bursts — rapid structure destruction, mass terrain edits, or wholesale piece removal by one player — and raises an admin alert (plus optional auto-freeze) when a threshold trips, naming the offender and location. Thresholds configurable.
- **Why:** Griefing is usually detected only after the base is already gone. WardIsLove guards specific wards; a server-wide destruction-rate tripwire catches griefing anywhere and is the safety net raid-heavy communities keep asking for. L effort (needs server event hooks) but high impact.


## Domain: world-build (13 ideas)

### 1. Terrain Reset (radius) — undo terraforming  `[M · client-only]`
- **Tier:** must-build
- **What:** Aim or stand at a spot, pick a radius, and wipe ALL terrain edits back to the natural heightmap: enumerate TerrainModifier/TerrainComp ZDOs in range, ClaimOwnership + Destroy them, then poke Heightmap + ClutterSystem to rebuild. Same client-side ZDO pattern as ClearTrees. Confirm-gated; pairs with the Snapshot/Restore idea for safety.
- **Why:** The single most-requested world tool: players dig moats, pave everything, or grief-flatten spawn, and vanilla's 'resetterrain' console is tiny-radius and unreliable. Owners currently install WorldEditCommands (JereKuusela) just for this. Terrain lag from thousands of stacked modifiers is also a top server-perf complaint this directly fixes.

### 2. Terrain Sculpt Pad (flatten / raise / smooth to target height)  `[M · client-only]`
- **Tier:** high-roi
- **What:** Apply level/raise/smooth TerrainModifier ops programmatically over a radius to a chosen target Y (or the crosshair's height), with an optional paint-to-grass/dirt pass. Client-side, reuses the same terrain-poke plumbing as Terrain Reset.
- **Why:** Owners setting up spawn towns, PvP arenas, and event pads need a big flat buildable area without hoe-grinding it by hand for an hour. Distinct from Reset (which removes edits) — this creates clean ground. No current admin-panel competitor bundles both.

### 3. Piece Editor (grab-move / rotate / raise / clone / delete under crosshair)  `[L · client-only]`
- **Tier:** high-roi
- **What:** Raycast to the piece you're looking at, ClaimOwnership, then an interactive edit mode: nudge/rotate/raise-lower with keys, delete, or clone it at the aim point (clone spawns via existing AP_SrvSpawn so it's server-owned). Ghost preview while editing.
- **Why:** Fixes floating/misaligned/half-griefed pieces without demolishing and rebuilding, and lets owners reposition community structures. This is InfinityHammer's core value — the most-installed building mod — brought inside the admin panel with admin-gating.

### 4. Object Inspector (aim-to-identify)  `[S · client-only]`
- **Tier:** high-roi
- **What:** Raycast to whatever piece/creature/ward you're looking at and show prefab name, creator/owner ID + resolved player name, WearNTear health, ward-protected status, and ZDO id, with a hover highlight. Read-only, client-side reflection on the ZDO.
- **Why:** Cheap standalone win and the UI backbone for the Piece Editor, Ownership Transfer, and Ward Manager. Admins constantly ask 'what is this / whose is this' when investigating griefing or abandoned bases; nothing in the panel answers that today.

### 5. Base Ownership Transfer / Adopt Base  `[M · client-only]`
- **Tier:** must-build
- **What:** Select a base (radius around aim, or a specific ward) and reassign every Piece.m_creator + PrivateArea creator/permitted list to a chosen online player — or clear it entirely so anyone can edit. Client-side on loaded ZDOs after ClaimOwnership.
- **Why:** Solves the #1 unfixable community-server headache the brief calls out: a player quits and their warded base can't be edited, deconstructed, or handed to a new owner. No mainstream mod does clean bulk ownership reassignment — this is a headline differentiator.

### 6. Ward & Protection Manager  `[M · client-only]`
- **Tier:** high-roi
- **What:** List every PrivateArea (ward) loaded around the admin with owner, permitted players, and on/off state; toggle wards, add/remove any player to the permit list, flash a ward's radius, and force-unlock an orphaned ward whose owner left. Client-side on loaded ward ZDOs.
- **Why:** Wards are the main source of 'I'm admin but can't touch this' friction. WardIsLove is a top server mod purely for ward control; folding the essentials in (plus force-unlock for abandoned wards) covers most of its demand without a second dependency.

### 7. Protected / No-Build Zones (server-enforced)  `[L · RPC]`
- **Tier:** must-build
- **What:** Admin defines circular zones (center + radius, named) that non-admins cannot build in or destroy pieces within. Companion persists the zone list to disk, watches for piece ZDOs created/removed by non-admins inside a zone and reverts them + warns the offender, and pushes the zone list to clients for on-map preview. New RPCs: AP_SrvZoneAdd/Del/List.
- **Why:** Protecting spawn, shops, and event areas from griefing is a core community-server need with no clean solution today — owners hack it with generic anti-grief mods. This is the flagship server-side feature of the domain and batches perfectly into one companion bump.

### 8. Portal Network Manager  `[L · RPC]`
- **Tier:** high-roi
- **What:** Companion scans all portal_wood/portal_stone ZDOs world-wide (including unloaded zones) and returns each portal's tag, position, biome, and pair status (paired / orphaned / duplicate-tag). Admin can rename a tag, teleport to it, force-pair two portals, or delete. New RPCs: AP_SrvPortalList + AP_SrvPortalTag (teleport reuses existing paths).
- **Why:** Large servers accumulate dozens of portals with clashing or orphaned tags — untangling them by hand means physically walking to each one. A world map of the whole portal graph with remote rename/pair is a genuinely novel admin capability no competitor offers.

### 9. Blueprint Capture & Paste  `[L · RPC]`
- **Tier:** high-roi
- **What:** Select a radius/box around a build, capture every piece as (prefab, relative pos, rotation) into a named blueprint saved to config/disk, then paste at the crosshair with a rotation offset. Paste places pieces server-owned; a batched AP_SrvSpawnBatch RPC avoids flooding on big builds.
- **Why:** Lets owners template spawn hubs, shops, and event structures and redeploy them across worlds/servers — PlanBuild's headline feature, which is one of the most-downloaded building mods. Also becomes the serialization backbone for undoing mass-removes.

### 10. Mass Structure Remove (filtered, with undo)  `[M · client-only]`
- **Tier:** must-build
- **What:** Remove all pieces in a radius with optional filters: only unwarded, only a specific creator's pieces, or only a category (workbenches/fences/etc.), with a live count preview. Client-side ClaimOwnership+Destroy; hooks the existing per-admin multi-step undo by storing removed-piece descriptors and re-spawning on undo.
- **Why:** Fills a real gap — today's area actions clear drops and trees and repair builds, but there is NO structure-removal tool. Clearing abandoned/griefed bases is a constant chore currently done piece-by-piece or with console spawn-nuke risk. Undo makes it safe to actually use.

### 11. Prefab & Location Spawner  `[M · RPC]`
- **Tier:** medium-roi
- **What:** Two things the Items/Creatures tabs can't do: spawn ANY raw prefab by name (build pieces, props, runestones, dungeon entrances) via existing AP_SrvSpawn, and place vanilla Locations (Haldor the trader, boss altars, crypts/dungeons, runestones) at the crosshair via a new AP_SrvSpawnLocation that calls ZoneSystem.GenerateLocation server-side.
- **Why:** Owners building custom content or relocating a missing/despawned Haldor have no in-game way to do it — they edit the world file offline or install WorldEditCommands. Location placement in particular is a power no admin panel currently exposes.

### 12. Persistent No-Decay / Auto-Repair (server toggle)  `[M · RPC]`
- **Tier:** medium-roi
- **What:** A server-side toggle that stops structure decay globally (patch WearNTear wear, or run a mass-repair pass each autosave). New RPC AP_SrvNoDecay persisted in companion config. Distinct from the existing one-shot 50m RepairBuilds button — this keeps community builds pristine forever.
- **Why:** Rain/weather decay slowly wrecks big community builds and event areas; owners currently pull in ValheimPlus or a dedicated no-decay mod just for this one setting. Bundling it removes a dependency and is a frequent request on shared-build servers.

### 13. Area Snapshot & Restore (regional undo)  `[L · client-only]`
- **Tier:** medium-roi
- **What:** Before any destructive op, snapshot all pieces (and optionally terrain modifiers) in a radius to disk as a restore point; one click rebuilds them (pieces via batched spawn, terrain via re-applied modifiers). Reuses the Blueprint serialization; acts as the shared undo backbone for Terrain Reset, Sculpt, and Mass Remove.
- **Why:** Turns every scary destructive tool in this domain from 'one misclick nukes the town' into a safe, reversible operation — the safety net that makes owners actually trust and use the rest of the world tools. No competing mod offers regional restore points.


## Domain: server-ops (13 ideas)

### 1. ZDO Prefab Census (top-N)  `[M · RPC]`
- **Tier:** S — flagship diagnostic, build first
- **What:** New read-only truth RPC (AP_SrvZdoCensus) that walks ZDOMan.m_objectsByID server-side, groups by prefab hash, and replies to the requesting admin with the top-N prefabs by count + grand total + a 'dropped items' subtotal. Panel shows a sortable ranked list (name, count, %). Iterate in chunked passes off the hot sim path to avoid a stall on 100k+ ZDO worlds; reuse the reflective m_objectsByID access the Server tab already does for the ZDO count.
- **Why:** The single most-requested server-owner diagnostic: instantly surfaces the runaway boar/wolf breeding pen, the dupe/dropped-item pile, or the 4000 arrows on the ground that are tanking tick rate. Today the panel shows ONLY a total ZDO number with no way to see what they are. This is the foundation the purge/hotspot/cap features all build on.

### 2. Prefab-targeted worldwide purge  `[M · RPC]`
- **Tier:** S — pairs with census, huge payoff
- **What:** The act-side of the census: each census row gets a two-click ConfirmButton 'Remove all N' that fires AP_SrvZdoPurge(prefabHash) — the companion deletes every matching ZDO worldwide. CRITICAL: must call zdo.SetOwner(GetSessionID()) before ZDOMan.DestroyZDO for each, exactly the fix discovered for the dedicated-undo bug (DestroyZDO is a silent no-op unless the caller owns the ZDO, and dedicated servers own nothing they didn't instantiate). Report count actually removed back over AP_Msg.
- **Why:** Turns diagnosis into a one-click fix — the reason you run a census is to delete the offending pile. Uniquely valuable for 'remove all dropped items' and culling runaway tamed herds. Competing cleanup mods make you edit configs or run console spawn-reduction; nobody offers point-and-delete from a live census.

### 3. Per-zone ZDO density / lag-hotspot finder  `[M · RPC]`
- **Tier:** A — concrete, unique, reuses TP infra
- **What:** AP_SrvZoneDensity replies with the top-N heaviest ZoneSystem sectors (zone coord, world x/z, ZDO count, dominant prefab). Panel lists them with a 'Teleport here' button wired into the existing map-point teleport. Optionally overlay pins on the minimap.
- **Why:** Complements the census's 'what' with 'where' — points the admin straight at the base or farm that's causing localized lag, then teleports them there to investigate. Directly actionable for the 'my server hitches near spawn' complaint that has no vanilla answer.

### 4. Scheduled + on-demand rotating world backups  `[L · RPC]`
- **Tier:** S — headline reliability feature
- **What:** Companion zips the world save (.db + .fwl, and .db.old) from the world save dir into a timestamped backups folder, keeping the last N. AP_SrvBackupNow triggers it; a config interval does it automatically; AP_SrvBackupList replies with existing backups (name, age, size) for a panel list. Zip on a background thread using net48 System.IO.Compression.FileSystem; force a save immediately before so the backup is fresh.
- **Why:** The #1 reliability feature server owners want and the top reason people fear community servers. The panel already force-saves and shows the save clock but has NO backup story — a corrupt .db or a griefer wipe is currently unrecoverable in-panel. No competing admin mod offers in-panel rotating backups.

### 5. Backup restore / rollback (stage-on-next-boot)  `[L · RPC]`
- **Tier:** A — high value, ships right after backups
- **What:** From the backup list, a guarded 'Restore this' (AP_SrvBackupRestore) that stages the chosen archive to overwrite the live world on the NEXT server boot — you can't hot-swap the open, memory-mapped .db, so it drops the files into place and flags a pending restore, then pairs with the scheduled-restart feature to apply it. Panel shows a clear 'restore pending on next restart' banner.
- **Why:** Backups are only half the value without rollback. Recovers from griefing, a bad admin action, or corruption. The stage-on-boot design is the honest, safe way to do it given the world file is locked while running — sets correct expectations instead of pretending a live swap is possible.

### 6. Scheduled restart with countdown broadcasts  `[M · RPC]`
- **Tier:** S — high value, mostly reuses existing pieces
- **What:** AP_SrvScheduleRestart(atTime | inMinutes) schedules a restart; the companion broadcasts '10/5/1 min to restart' via the existing AP_SrvBroadcast path, force-saves at T-0, then exits the process. AP_SrvRestartStatus replies with any pending restart for a panel banner + cancel button. Be explicit in the UI: a mod can't relaunch a killed process itself, so this relies on the host's auto-restart wrapper (GTX panel, systemd, pm2, LGSM) — which virtually every managed/dedicated host has.
- **Why:** Scheduled restarts with player warnings are table-stakes for healthy long-running servers (clears leaked memory/ZDO churn, applies staged config/restores) and a staple of ValheimServerWarden-style tooling. Reuses broadcast + force-save you already have; the only new piece is the timer + graceful exit.

### 7. In-panel server log tail  `[M · RPC]`
- **Tier:** A — turns a private debug tool into a feature
- **What:** AP_SrvLogTail(cursor) returns new BepInEx/LogOutput.log lines since the admin's last cursor (or the last N lines), with a level filter (all / warnings / errors). Panel renders a scrolling read-only console. Companion reads its own log file or attaches a BepInEx LogListener ring buffer.
- **Why:** Lets an owner spot mod exceptions, failed saves, disconnect spam, and version-mismatch errors without SSH/SFTP. You already had to build gtx-fetch-log.js over SFTP just to debug the undo bug — this productizes that capability for every owner, most of whom can't get at server logs at all on managed hosts.

### 8. Live server perf graph (frame time + history)  `[M · RPC]`
- **Tier:** A — makes the truth suite diagnostic, not just descriptive
- **What:** Extend the info reply with a server-side sampler: a small ring buffer of server frame time / effective tick rate (Time.deltaTime on the headless process), ZDO count, and per-peer bytes in/out over a rolling window. Panel renders sparklines. Explicitly separate SERVER frame time from network ping (the current per-peer ping only measures the link, not server load).
- **Why:** The Server tab shows instantaneous numbers but no trend — you can't tell if the server is degrading, when the nightly lag spike hits, or which peer's traffic correlates with hitches. A real perf history is what owners screenshot when asking their host for more resources, and it makes census/cleanup wins visible ('ZDO dropped 40k, tick rate recovered').

### 9. Vanilla world-modifier sliders (write side)  `[M · RPC]`
- **Tier:** B — solid, uses supported APIs
- **What:** Expose Valheim's server-side world modifiers — combat difficulty, death penalty, resource rate, raid frequency, portals on/off, passive mobs, no-map — as panel dropdowns that set them live via AP_SrvSetModifier. These are server-authoritative game settings the companion can push at runtime.
- **Why:** Directly answers the domain's 'difficulty/portal/raid sliders' ask using VANILLA-supported knobs (no fragile patching), so it's low-risk. Owners currently must relaunch with different start args to change these; doing it live from the panel is a clear quality-of-life win and something ValheimPlus-style config editing does clumsily via file edits.

### 10. Runtime autosave interval control  `[S · RPC]`
- **Tier:** B — low-effort quick win, batch it
- **What:** AP_SrvSetAutosave reads and sets ZNet's save interval (m_saveInterval) live, plus an enable/disable toggle. The panel already computes and shows 'next autosave' — add a small stepper next to it to change the cadence without a restart.
- **Why:** Cheap, obvious quick win layered on data already displayed. Owners tune autosave frequency to trade save-hitch against data-loss risk; today that needs a config/relaunch. Small enough to ride along in the same companion bump as a bigger feature.

### 11. Dropped-item & orphaned-ZDO auto-sweep  `[M · RPC]`
- **Tier:** A — recurring hygiene, high ongoing value
- **What:** AP_SrvSweep with an age threshold: worldwide remove dropped ItemDrops older than N minutes and orphaned/dangling ZDOs (no valid prefab hash or broken connections). Offer both a one-shot guarded button and an optional recurring server-side sweep at a configured interval. Uses the same SetOwner→DestroyZDO ownership-first delete as the purge.
- **Why:** Dropped-item accumulation is a top silent lag source (death piles, mass-crafting spill, farm drops) and orphaned ZDOs bloat the .db permanently. A scheduled sweep keeps the world lean between manual censuses — the automated hygiene layer that keeps a busy community server healthy long-term.

### 12. World-save integrity / corrupt-save early warning  `[S · RPC]`
- **Tier:** B — cheap reliability insurance
- **What:** On each save the companion checks the fresh .db against .db.old (size sanity, non-zero, write succeeded, timestamp advanced) and surfaces a green/amber/red integrity indicator in the Server tab, with the last-save result. Extend the existing SaveWorldThread postfix already used to timestamp last-save.
- **Why:** Catches the nightmare scenario — a save silently truncating or failing on a full disk — BEFORE it becomes an unrecoverable world, ideally in time to grab a backup. Very cheap because it hooks the same save postfix already added for the last-save clock; pure reliability upside.

### 13. Server health alerts to Discord (reuse owned infra)  `[M · client-only]`
- **Tier:** B — differentiator, leverages infra you already run
- **What:** Companion-side, config-driven watchdog that posts to the existing Discord webhook / Ikarus on notable events: save failure, ZDO count crossing a threshold, server empty for N hours, an unhandled exception in the log, or a completed scheduled restart. Runs autonomously server-side; no client RPC needed to function (panel toggles to configure it would add one later).
- **Why:** Turns the mod from 'panel you must have open' into 'server that tells you when something's wrong' — owner gets pinged on their phone instead of finding a dead/corrupt server hours later. Uniquely cheap here because the Discord server, webhooks, and always-on Ikarus bot are ALREADY owned infra; it's mostly wiring, and no competing admin mod integrates a health watchdog with an owner's Discord.


## Domain: discord-ext (13 ideas)

### 1. Live Server Event Feed (#server-feed)  `[M · client-only]`
- **Tier:** must-build
- **What:** Companion Harmony hooks on join/leave, death, boss-kill, raid-start/end, and player pings POST rich embeds to a configured webhook — always on, no admin needs to be in-game. This is the core of the domain and is a straight companion feature (Harmony hook -> format -> webhook POST); config lives in the BepInEx file initially (see idea #2 for panel config).
- **Why:** This is the single most-requested community-server feature and the whole reason DiscordConnector has ~millions of downloads. Because the companion runs in the server process 24/7, it fires even when no admin is online — the client's existing webhook code can't do that. Directly replaces DiscordConnector while living inside a mod owners already run.

### 2. In-Panel Discord Feed Configuration  `[M · RPC]`
- **Tier:** high-value
- **What:** F7 panel section to set webhook URLs, per-event toggles (join/death/boss/raid/ping), embed style, and rate-limit, pushed to the companion via a new AP_SrvSetDiscordCfg RPC + AP_SrvGetDiscordCfg readback, with a 'Send test message' button. Config persists server-side.
- **Why:** Every competing mod forces owners to hand-edit a config file and restart the server. Configuring the entire event feed from inside the admin panel — with a live test button — is a genuine UX moat and turns idea #1 from 'a feature' into 'a product'. Fits the existing admin-gated RPC model exactly.

### 3. Admin Action Audit Log -> #admin-audit  `[S · client-only]`
- **Tier:** must-build
- **What:** The companion already validates and executes every admin RPC (ban/kick/give/spawn/teleport/heal/skill-edit/inventory-removal/world-actions). Wrap that choke-point so each executed action auto-posts an embed: actor name+id, action, target, args, coords, timestamp. Optional 'destructive only' filter.
- **Why:** Multi-admin community servers have no accountability today — you can't tell which admin banned a player or spawned 500 dragons. The re-stamped true-sender id makes the log trustworthy. It's low effort (one POST at the existing execution point, no new RPC), and it's a killer differentiator no free Valheim mod offers. Moderation transparency sells servers.

### 4. Auto-Updating Live Status Embed  `[M · client-only]`
- **Tier:** high-value
- **What:** Ikarus keeps ONE pinned Discord message continuously edited (~60s): online players + names, uptime, world day, weather, boss progression, next scheduled restart. Data source is either a periodic companion status snapshot POSTed to a relay, or a bot-side A2S/Steam query — no new client RPC.
- **Why:** A single self-updating status card beats a spammy feed for 'is the server up / who's on right now', which is the #1 thing players check before logging in. Reuses the authoritative uptime/ZDO/roster data the Server tab already computes. Mostly bot-side, so it ships without a companion version bump if fed via webhook snapshot.

### 5. External Down / Crash Watchdog Alerts  `[M · client-only]`
- **Tier:** must-build
- **What:** Ikarus A2S/TCP-pings the game server every minute and alerts #admin-alerts on N consecutive failures (with @here) and again on recovery, plus optional 'no heartbeat in X min' from a companion pulse. Add low-disk / runaway-ZDO warnings sourced from the companion status snapshot.
- **Why:** This MUST live in the bot, not the mod — a crashed server can't post its own obituary, which is exactly the blind spot of in-process mods like DiscordConnector. Owners find out their server died from Discord in 60s instead of from angry players hours later. Highest operational value in the whole domain.

### 6. Discord Slash-Commands That Drive the Server  `[L · RPC]`
- **Tier:** high-value
- **What:** /who, /save, /broadcast, /kick, /ban, /restart run from Discord. Bridge design that fits the firewall: the companion POLLS an authenticated command queue on the Ikarus VPS (outbound-only, so no open inbound port on the game host), executes with the SAME adminlist gating as F7, and replies with the result. Bot maps Discord admin role -> allowed commands.
- **Why:** Remote admin from your phone via Discord is something almost no free mod offers because Valheim has no native RCON. The outbound-poll bridge avoids opening a port and reuses the existing sanitized-sender security model. This is the single biggest 'wow' differentiator; mark it L and batch its bridge so ideas #8/#9/#11 can reuse it.

### 7. Player-Linked Discord Accounts  `[L · RPC]`
- **Tier:** high-value
- **What:** /link in Discord issues a one-time code; the player types it in in-game chat; the companion's chat hook verifies and stores a steamID<->discordID map. Unlocks: DM/@mention you on your own death, 'who is this Discord user in-game', role-gated perks, and personalized digests.
- **Why:** Identity linking is the foundation layer that makes every other integration personal instead of anonymous — death pings that tag the right person, role sync (#9), per-player stats. It's the piece DiscordConnector never built, and it deepens community lock-in. L because it needs the chat hook, verification handshake, and persistent store.

### 8. Two-Way Chat Relay (#in-game-chat)  `[M · client-only]`
- **Tier:** high-value
- **What:** Companion hooks Chat.RPC_ChatMessage and relays global/shout chat to a Discord webhook (in-game -> Discord is the easy S half). Discord -> in-game rides the command-bridge poll from idea #6 to inject messages, prefixed [Discord] <user>. Filters for spam/PII and channel scoping.
- **Why:** Lets Discord-only members talk to players actually in the world and vice-versa — huge for keeping a community engaged when people aren't logged in. Outbound-only is a quick standalone win; the return path is nearly free once #6's bridge exists. Another feature DiscordConnector lacks (it only mirrors joins/deaths, not live chat).

### 9. Discord Role <-> Admin/Whitelist Sync  `[L · RPC]`
- **Tier:** high-value
- **What:** Bot watches Discord 'Admin'/'VIP'/'Whitelisted' roles and reconciles adminlist.txt / permittedlist.txt through the command bridge (or direct file write on the host). Grant the role in Discord -> in-game admin/whitelist within a minute; remove it -> access revoked. Two-way audit of drift.
- **Why:** Directly kills the memory note's 'who is actually an admin' drift problem: today access lists and Discord roles are maintained by hand and diverge. Managing server access from the place you already manage your community (Discord roles) is a real ops win and pairs perfectly with the existing access-list tab. Requires account linking (#7) to be safe.

### 10. Boss-Kill & 'Server First' Announcements  `[M · client-only]`
- **Tier:** high-value
- **What:** Boss-death hook posts a hype embed: boss, killers present, world day, time-to-kill, coords/biome — and flags SERVER FIRST the first time each boss falls on this world, backed by a small persisted progression record the Server tab can also surface.
- **Why:** Boss first-clears are the peak social moments on a community server and generate exactly the screenshots that recruit new players. DiscordConnector announces deaths but doesn't track world-first progression. Cheap (companion boss hook + webhook + a tiny persisted flag set) for outsized community hype.

### 11. Scheduled Restart with In-Game Countdown + Safe Save  `[L · RPC]`
- **Tier:** high-value
- **What:** Owner schedules from Discord (/restart in 10m "patch day") or a cron on Ikarus. The companion broadcasts an escalating in-game countdown and force-saves the world (reusing AP_SrvSaveWorld) before the bot bounces the process; posts start/complete/back-online to #admin-alerts.
- **Why:** Turns the scariest routine op — restarting a live server without eating unsaved progress — into one safe, announced pipeline. Ties the existing force-save + broadcast primitives to real host process control. Depends on the #6 bridge and host process access; high value for any server that patches or reboots on a schedule.

### 12. Weekly Digest & Leaderboards -> #server-stats  `[M · client-only]`
- **Tier:** nice-to-have
- **What:** Scheduled bot embed compiling the event feed (#1): total playtime per player, deaths, boss kills, new joiners, peak concurrency, longest session. Optional per-player DM digest for linked accounts (#7).
- **Why:** Leaderboards are the one DiscordConnector feature this project would otherwise lack, and they drive weekly re-engagement ('I'm #2 in deaths, need to grind'). Because it's computed bot-side from the feed, it needs no companion change once #1 exists — pure upside on top of already-shipped data.

### 13. Death / Ping Heatmap Image Posts  `[L · client-only]`
- **Tier:** nice-to-have
- **What:** The companion already knows death and ping coordinates; periodically render a small world-map thumbnail with plotted markers (danger zones, exploration hotspots) and post it to Discord as an image attachment, weekly or on demand via slash-command.
- **Why:** A visual 'where the server is dying' map is a novel, shareable artifact no competing mod produces, and it reuses coordinate data the audit/event systems already capture. Lower priority than the text feeds but a memorable flex once the plumbing (#1, image POST like the existing bug-report screenshot upload) exists.


## Domain: events-fun (14 ideas)

### 1. Server-side currency ledger (economy foundation)  `[L · RPC]`
- **Tier:** keystone
- **What:** Companion keeps a durable per-player coin balance keyed by platform id in a NEW on-disk store under the world/config dir (the mod has no persistence layer today — UndoHistory and the join log are in-memory). New Economy tab: search/view balances, grant/deduct/set, transaction log. RPCs: AP_SrvEcoReq (read → requesting admin only) + AP_SrvEcoAdjust (grant/deduct/set).
- **Why:** Every shop/reward/lottery/tournament-prize below needs one authoritative balance. Balances must live server-side, NOT as in-world items, because vanilla inventories are client-authoritative and would be dup'd or lost-on-death — this is exactly the integrity gap ServerCharacters fills, offered here as a lighter ledger. Unlocks the entire domain.

### 2. Player chat-command bridge  `[L · RPC]`
- **Tier:** keystone
- **What:** Patch the server's chat path (Talker/Chat 'Say' RPC) to intercept a configurable prefix (e.g. !) from ANY player and route to a SAFE non-admin whitelist: !balance, !pay, !daily, !shop/!buy, !tp <hub>, !top, !bounties. Replies go center-screen to that player only. Panel: per-command enable, prefix, cooldowns. Strictly firewalled from the AP_Srv* admin handlers; RouteRpcSanitizer already stamps the true sender on chat too.
- **Why:** The panel is admin-only (F7) — players have no UI, so chat commands are the ONLY way to make any economy/reward/hub feature actually reachable by ordinary players without shipping a client to everyone. This is the interaction layer the whole domain depends on.

### 3. Playtime rewards & ranks  `[M · RPC]`
- **Tier:** high-ROI
- **What:** Extend the existing server-side join/leave logging into durable accumulated playtime, then auto-grant currency/items and assign rank tags at thresholds (e.g. 10h -> 'Thegn'). Panel: playtime table, configurable reward tiers, manual rank grant. Rank tag can prefix the player's chat name via the chat bridge.
- **Why:** Playtime rewards are the top retention loop on community servers and reuse infra you already ship. Ranks give visible status; auto-currency feeds the economy with zero admin babysitting.

### 4. Daily login reward + streak  `[M · RPC]`
- **Tier:** high-ROI
- **What:** On join, grant a once-per-real-day reward (coins/item); consecutive-day streaks multiply it, with a Discord webhook post on milestone streaks. !daily to claim if not auto. Admin configures the reward table and reset time.
- **Why:** Daily-reset hooks are the cheapest, highest-impact retention mechanic in live games. Trivial once the ledger + chat bridge exist, and every streak drives a daily login and a Discord post.

### 5. Leaderboards + weekly Discord post  `[M · RPC]`
- **Tier:** high-ROI
- **What:** Track deaths / playtime / boss-kills / creature-kills server-side (playtime already exists; add Character.OnDeath and boss-defeat/global-key hooks) into the durable store. Panel Leaderboards tab; !top <stat> in chat; Ikarus/webhook auto-posts a weekly Top 10.
- **Why:** Public rankings create rivalry and a standing reason to check Discord every week — a direct funnel. Needs only a couple of server-side event hooks you don't have yet.

### 6. Scheduled / automated events with Discord auto-announce  `[L · RPC]`
- **Tier:** high-ROI
- **What:** A companion-owned scheduler fires configured events on a cron/interval even with no admin online: invasions/raids, boss-rush, treasure hunt, double-drop/double-skill windows. Reuses the existing AP_SrvEvent/raid plumbing + a countdown broadcast + an outbound Discord webhook. Panel: recurring + one-off schedule editor, enable/disable, 'run now'.
- **Why:** Turns your existing one-click event buttons into a self-running content calendar — the server feels alive on a timer without the admin present, and every event auto-pings Discord (retention + funnel in one). Requires the companion to own a timer and do the webhook POST itself.

### 7. Loot crate / server lottery (coin sink + gambling)  `[M · RPC]`
- **Tier:** high-ROI
- **What:** Admin defines crates/lottery with weighted reward tables (items/coins/cosmetics). Players spend coins via !buy crate / !lottery; server rolls, grants, and broadcasts rare wins server-wide with a Discord post. Panel: crate & odds editor, ticket sales, draw-now for a periodic jackpot.
- **Why:** Gambling loops are the stickiest coin sink and manufacture 'big win' moments worth screenshotting to Discord. EpicLoot's gamble popularity shows the demand; this mod currently has no sink for currency.

### 8. Teleport-hub network for players  `[M · RPC]`
- **Tier:** high-ROI
- **What:** Admin defines named public hub nodes (spawn, trader, boss arenas, biomes). Players fast-travel with !tp <hub> / !hubs, with optional coin cost, cooldown, and configurable no-teleport-with-ore / PvP rules. Server-side teleport already exists (AP_SrvTeleport) — this exposes a curated, player-usable subset via a hub registry.
- **Why:** A sanctioned fast-travel network is one of the most-requested QoL features on large servers (portals can't carry ore); charging coins ties it into the economy. You already own the teleport primitive — this is mostly the registry + chat exposure.

### 9. Treasure hunt event  `[M · RPC]`
- **Tier:** high-ROI
- **What:** Admin or the scheduler drops a reward chest at a random/chosen location; the companion posts coordinates or a biome riddle to Discord and a teaser broadcast in-game; first player to open it wins the loot and is announced. Opener tracked server-side.
- **Why:** A recurring treasure hunt is low-cost, high-engagement, and explicitly pushes players to Discord for the clue — the exact funnel goal. Reuses spawn + broadcast + webhook.

### 10. Boss-rush gauntlet event  `[M · RPC]`
- **Tier:** high-ROI
- **What:** One-click/scheduled gauntlet that spawns bosses in sequence with escalating difficulty and a countdown, tracks clear time server-side, and posts a fastest-clear leaderboard to Discord. Wraps your existing boss-summon plumbing in an event + timer + scoreboard.
- **Why:** Gives the Bosses tab an endgame 'event' mode with a competitive score — repeatable content for maxed players who'd otherwise log off. Mostly orchestration over primitives you already ship.

### 11. PvP arena / bracketed tournament tooling  `[L · RPC]`
- **Tier:** stretch
- **What:** Admin creates a tournament: sign-ups (!join arena), auto-teleport paired fighters into an arena, temporary forced-PvP + full-heal between rounds, spectate for the eliminated (you already have spectate), auto-bracket advancement, winner announced with a coin prize + Discord post.
- **Why:** Scheduled PvP tournaments are marquee events that pull players online at a set time and generate Discord highlights. Nothing automates brackets today; you already have teleport/heal/spectate primitives to compose.

### 12. Cosmetic grants + chat titles  `[M · RPC]`
- **Tier:** nice-to-have
- **What:** Admin grants cosmetic-only rewards (capes, Hildir/Haldor cosmetics, utility items) to any player and assigns chat titles/prefixes (e.g. '[Champion]') rendered via the chat hook. Titles tie to ranks, event wins, or coin purchase.
- **Why:** Status cosmetics are a zero-power-creep reward players chase and show off — the perfect prize for events/leaderboards and a coin sink that doesn't unbalance PvE. Rides the existing give-item + chat-hook infra.

### 13. Seasonal / holiday content toggles  `[S · RPC]`
- **Tier:** quick-win
- **What:** One-panel toggles for seasonal content: Yule decor spawns + festive weather, Hildir/Haldor event flags, holiday drop tables, themed mob spawns — plus a scheduler hook to auto-enable on date ranges. Partly rides Valheim's built-in seasonal global keys, already reachable through your global-keys plumbing.
- **Why:** Seasonal refreshes give players a reason to return on holidays at near-zero content cost since much is built into the game. Low effort, strong 'the server cares' signal, easy Discord announce.

### 14. Broadcast center + scheduled MOTD / event countdown banners  `[S · client-only]`
- **Tier:** quick-win
- **What:** Grow the existing AP_SrvBroadcast into a real broadcast center: styled center-screen banners, a rotating MOTD on join, and live countdown banners to the next scheduled event. Panel: message queue, schedule, target (all / single player). Manual broadcasts reuse the existing RPC; the auto MOTD/countdown leans on the scheduler from the automated-events idea.
- **Why:** A visible 'next event in 12m' banner converts the scheduler into felt anticipation and keeps players online for it. The broadcast RPC already exists — this is mostly panel UX plus tying it to the scheduler.


## Domain: admin-ux (13 ideas)

### 1. Command Palette (Ctrl+K / slash-typed actions)  `[M · client-only]`
- **Tier:** must-build (highest value / medium effort)
- **What:** A single text-driven overlay opened with a hotkey where the admin types actions like `/give iron 50 @Bjorn`, `/spawn troll 3* @crosshair`, `/heal @all`, `/tp bookmark:mountainbase`. Fuzzy autocomplete over item ids, creature ids, online player names, world bookmarks, and verbs; Tab to complete, Enter to fire. Each parsed command maps to an existing AP_Srv* RPC (Give/Spawn/Heal/Teleport). Ranked results reuse the existing FilteredItems/FilteredCreatures caches.
- **Why:** This is the single biggest power-admin win and the marquee differentiator. Valheim admins already live in console/chat command muscle memory; nothing (ValheimPlus, ServerCharacters, WardIsLove) offers a fuzzy palette that bridges that habit to a validated, admin-gated GUI. Turns a 6-click cross-tab task into 3 keystrokes. Zero companion change since it just routes existing RPCs.

### 2. Global cross-tab search  `[M · client-only]`
- **Tier:** high-ROI (high value / medium effort)
- **What:** One search box (its own tab or a persistent header field) that queries every tab's dataset at once — items, creatures, bosses, players, status effects, skills, world bookmarks, and even actions ('kick', 'save world') — and returns a unified ranked result list. Selecting a result jumps to that tab pre-filtered, or offers the inline action. Reuses the existing per-tab filtered caches and search-debounce plumbing (SearchDebounceSeconds, FilteredCache).
- **Why:** Right now search is siloed per tab, so admins must know which tab a thing lives in before they can find it. A global search removes the mental map entirely ('where's Serpent? boss or creature?'). Cheap because the per-tab search/filter machinery already exists — this is an aggregation layer over it.

### 3. Quick-action bar (cross-tab pinned toolbar)  `[M · client-only]`
- **Tier:** high-ROI (high value / medium effort)
- **What:** A thin, always-visible strip at the top of the panel holding admin-pinned actions from ANY tab: 'Give Bronze kit', 'Spawn 3 greydwarves', 'Save world', 'Toggle fly', 'Heal @all', a specific teleport bookmark. Right-click any button/action anywhere to 'Pin to bar'; drag to reorder. Backed by a steamID-scoped config the same way favoritesCfg/bookmarksCfg already are.
- **Why:** Implements the 'custom favorites across tabs' ask. Power admins repeat ~8 actions constantly; a persistent one-click bar collapses their most common workflow to a single click regardless of which tab is open. Reuses the existing config-persistence pattern, so low risk.

### 4. Recently-affected-players list + repeat-last-action  `[S · client-only]`
- **Tier:** high-ROI (high value / low effort)
- **What:** Auto-tracked ring buffer of the last N players you acted on (gave item / healed / teleported / raised skill), shown as a compact quick-pick. Selecting one refills the target field so the next action retargets them instantly. Plus a 'Repeat last action' hotkey that re-fires your previous action against the currently selected target. Purely client bookkeeping around existing RPC calls.
- **Why:** Admin work is bursty and player-centric ('now do the same for the next three people who died'). Re-finding a player in the roster each time is the top micro-friction. Directly answers the 'recently affected players quick list' + 'action templates' asks with almost no code — it's a client-side MRU list.

### 5. Action macros / sequences  `[M · client-only]`
- **Tier:** high-value (high value / medium effort)
- **What:** Let an admin compose an ordered list of existing actions into one named macro — e.g. 'New Player Welcome' = give starter kit + 50 wood + heal + teleport to spawn + apply Rested. Fire the whole sequence with one click; optional per-step delay and a placeholder token like {player} filled at run time. Stored in config like presetsKv. Sequencing is entirely client-side; each step is an existing AP_Srv* RPC.
- **Why:** This is the headline 'macros/scripts' capability and a genuine category gap — no competing Valheim admin mod ships composable action macros. Server owners run the same multi-step rituals constantly (event setup, onboarding, arena reset); macros turn a 30-second click-tour into one button. High value, and architecturally free because it orchestrates RPCs you already ship.

### 6. Keyboard-driven navigation  `[M · client-only]`
- **Tier:** solid (medium value / medium effort)
- **What:** Full no-mouse operation: number keys 1-7 jump tabs, up/down arrows move the highlighted row in any list, Enter executes the row's primary action, Esc closes/backs out, and typing anywhere focuses that tab's search. Focus-ring rendering in IMGUI so the selected row is visible.
- **Why:** Power admins hate clicking (explicitly stated). Combined with the command palette and quick-bar, this makes the whole panel operable in seconds without leaving home row. Pure client input handling; no RPC or server involvement.

### 7. Multi-select + batch apply in rosters and lists  `[M · client-only]`
- **Tier:** high-value (high value / medium effort)
- **What:** Shift/Ctrl-click (or checkboxes) to select multiple players in the roster — or multiple items/creatures — then apply one action to all: heal selected, give item to selected, kick/ban selected, teleport selected to me. Client loops the existing per-target RPC over the selection; a confirm dialog lists exactly who/what is affected before firing.
- **Why:** Answers 'multi-select in lists' and 'batch operations'. Event moderation and raid cleanup are inherently many-target ('summon everyone', 'heal the whole raid'). Today that's one player at a time. Works with zero companion change by looping existing RPCs; can later add an optional server-side batch RPC purely to cut network chatter.

### 8. Client session action log / audit feed  `[S · client-only]`
- **Tier:** solid (medium value / low effort)
- **What:** A live, searchable, timestamped feed of every action YOU performed this session — verb, target, parameters, and RPC result (ok / failed / blocked-by-adminlist). Filter by target or verb, click an entry to re-run it, and a 'copy to clipboard' that dumps a clean text log for pasting into a Discord incident report.
- **Why:** Admins constantly need to reconstruct 'what did I just do' after a busy event, and 'paste what happened' for the community. Purely client (it observes RPC calls/replies you already make), so it's cheap, and it pairs naturally with the existing bug-report-to-Discord webhook.

### 9. In-panel help & command cheatsheet overlay  `[S · client-only]`
- **Tier:** solid (medium value / low effort)
- **What:** Press ? (or a Help button) for a searchable overlay listing every action, its hotkey, and its command-palette syntax, grouped by tab, plus context help ('what does Global Keys do?'). Localized via the existing 9-language Loc system and covered by the localization validator.
- **Why:** Discoverability is the flip side of adding a palette/macros/hotkeys — power features are worthless if admins can't find them. A searchable cheatsheet also doubles as living documentation, cutting 'how do I…' Discord questions for the server owner. Client-only, and it slots straight into the existing Loc.cs pipeline.

### 10. Parameterized action templates (cross-tab presets with placeholders)  `[M · client-only]`
- **Tier:** solid (medium value / medium effort)
- **What:** Generalize the existing per-tab presets (creature presets, gear kits) into cross-tab templates that carry placeholder slots — e.g. 'Full Padded + 200 wood → {player}' or 'Arena: {count}x {creature} {stars}★'. On run, the panel prompts for the blanks (or pulls from the current selection) then fires the underlying RPCs. Reuses presetsKv storage.
- **Why:** Extends a proven, already-loved feature (presets) into a reusable, retargetable form, which is the 'action templates' ask. Lets owners codify their server's standard grants/events once and reuse forever. Low risk since it builds on the existing preset persistence and RPC calls.

### 11. Per-admin layout & panel-state persistence  `[S · client-only]`
- **Tier:** nice-to-have (medium value / low effort)
- **What:** Remember, per steamID, the last-open tab, window position/size, opacity, which sections are collapsed, sort order, and pinned quick-bar — restored on next open. Extends the existing steamID-scoped config approach (favoritesCfg/PresetsCfg) with a small layout blob.
- **Why:** Answers 'per-admin layouts'. Multi-admin servers share one client build but each admin has a different workflow; persisting their arrangement removes daily re-setup friction. Very cheap — it's serializing a handful of UI state fields into the config you already write.

### 12. Team-shared macro & preset library (server-synced)  `[L · RPC]`
- **Tier:** high-value / server-RPC (batch into a companion release)
- **What:** Opt-in sync so an owner's macros, templates, quick-bar, and bookmarks live in the companion's config and are pushed to every admin on connect, giving the whole moderation team one shared, versioned action library that a new admin inherits instantly. Needs a new AP_SrvMacroSync RPC (get/set), admin-gated and re-stamped by RouteRpcSanitizer like every other write; read replies go only to the requesting admin.
- **Why:** The single highest-value idea for the target niche (community servers with multiple admins): today every admin rebuilds their own presets. A shared library standardizes how the team operates and onboards new mods in seconds. This is the natural companion bump to batch with a release — and no competing mod offers team-shared admin macros.

### 13. Server-side admin action audit log (queryable in panel)  `[M · RPC]`
- **Tier:** high-value / server-RPC (accountability, batch into a companion release)
- **What:** The companion records every admin action it executes (who, when, verb, target, params) to a rolling server-side log, exposed in the panel via a read-only AP_SrvAuditReq reply — filter by admin, target player, or verb, and answer 'who spawned this / who banned Bjorn / who gave 500 iron'. Survives admin relogs, exactly like the existing server-side join/leave history.
- **Why:** Accountability is the missing pillar for multi-admin communities — owners need to see what their staff did, and this is the natural sibling to the join/leave history you already persist server-side. Since the companion already validates and executes every action, logging is a small, contained addition on top of an existing choke point. Pairs with the client session log (which only shows YOUR actions).


## Domain: player-data (13 ideas)

### 1. Playtime & presence ledger (first-seen / last-seen / total hours)  `[M · RPC]`
- **Tier:** S
- **What:** Companion aggregates the existing server-side join/leave log into a persistent per-player ledger keyed by m_characterID.UserID: first-seen date, last-seen, total playtime, session count, avg session, longest session. Panel shows a sortable table + a 'most active' leaderboard. Builds directly on the AP_SrvJoinLogReq infra that already survives admin relogs.
- **Why:** Server owners constantly want to know who is actually playing, who has gone inactive (reap-able for pruning old bases), and who to reward — no admin panel does this well today.

### 2. Unified player profile card  `[M · client-only]`
- **Tier:** A
- **What:** One screen per player that stitches together data the panel already fetches: identity + user id, live position/biome, health/food/level, skills summary, inventory summary, admin note. MVP is pure client-side aggregation of the roster + AP_SrvReqInv reply + skill reads + client join log (no new RPC); the richer version splices in server-side playtime and death count from the ledger.
- **Why:** Turns five scattered actions into one 'who is this player' view — the single most-used moderation lookup, and the natural home for every other feature in this domain.

### 3. Server-side character snapshot vault (enabling primitive)  `[L · RPC]`
- **Tier:** S
- **What:** Opt-in: the companion captures each player's inventory + skills + position + death count + level when they log out and on world save, storing a rolling, retention-capped set of timestamped snapshots server-side keyed by user id. Vanilla stores .fch client-side, so this server-owned store is the prerequisite that unlocks true offline profile/edit/backup/transfer/rescue.
- **Why:** This is the strategic ServerCharacters-parity move, admin-driven: one L-effort build that turns a half-dozen 'impossible offline' requests into 'read/write a stored snapshot.' Everything below marked as vault-dependent rides on it.

### 4. Inventory backup & restore per player  `[M · RPC]`
- **Tier:** S
- **What:** On top of the vault: list a player's snapshots with timestamps, preview each snapshot's contents, and restore a chosen one — applied live via the existing give/remove path if the player is online, or staged to apply on next login if offline. Confirm-gated, and it snapshots the current state first so a restore is itself undoable.
- **Why:** The #1 community-server support ticket is 'a bug/griefer/death-spiral wiped my gear' — this is the one-click fix, and the marquee reason an owner installs the mod.

### 5. Offline grant / strip items & skills  `[M · RPC]`
- **Tier:** A
- **What:** Edit a stored snapshot for a player who is not online — add or remove items, ±skill levels — and queue the delta to apply on their next login (or apply immediately via existing RPCs if they happen to be online). Vault-dependent; reuses the Items tab picker and skill-browser UI for the editing surface.
- **Why:** Lets an owner honor a reward, fix a mistake, or confiscate an exploited item without needing the player and admin online at the same instant — the offline analog of features that today require the target present.

### 6. Un-stuck / rescue (online + offline)  `[M · RPC]`
- **Tier:** A
- **What:** For online players extend the existing teleport-to with 'send to their bed / to spawn / to safe coords.' For OFFLINE players, stage a pending relocation flag (via the vault/login hook) so a player wedged in terrain, sunk in the sea, or dropped into Ashlands lava is moved to safety the instant they reconnect, before they die again.
- **Why:** Stuck-player rescues are a recurring, urgent support case and vanilla gives admins no offline lever — this removes the 'delete your character' last resort.

### 7. Cross-server character transfer (export / import)  `[L · RPC]`
- **Tier:** A
- **What:** Export a player's snapshot (inventory + skills + stats) to a portable file the admin downloads through the panel; import it on another server running the companion to seed or overwrite that player's character. Vault-dependent, with a version/compatibility header on the file.
- **Why:** Multi-world communities (season resets, prod↔test worlds, world merges, 'bring your char to our new map') have no clean path today — ServerCharacters is per-world, so cross-server portability is a genuine competitive gap.

### 8. Reset / wipe a player to fresh  `[S · RPC]`
- **Tier:** A
- **What:** Confirm-gated action that clears inventory, resets skills to 0, clears deaths, and optionally returns the player to spawn — snapshotting the pre-reset state first so it can be reverted. Online version reuses existing remove/skill RPCs; offline version writes the vault snapshot.
- **Why:** Owners need a clean 'fresh start' for new seasons, opt-in hardcore resets, or as a moderation step short of a ban — and today they have to walk the player through deleting a local file.

### 9. Death log & loss recap per player  `[M · RPC]`
- **Tier:** A
- **What:** Companion hooks server-observed player deaths and logs a per-player history: timestamp, coords/biome, and cause. Surfaces 'last death location' with a one-click teleport-to-grave for recovery, plus a deaths-over-time count feeding the profile card.
- **Why:** 'Help me get my body back' is a top support request; logging death sites server-side (survives the player relogging) lets an admin recover a corpse the player can no longer reach, and exposes death hotspots.

### 10. Cross-playerbase item audit ('who has item X')  `[M · RPC]`
- **Tier:** A
- **What:** Scan for a given item across all stored snapshots (and live online inventories) to list every holder, quantities, and grand total — with jump-to-owner. Vault-dependent for full offline coverage; an online-only MVP is smaller.
- **Why:** Gives owners a real economy/anti-cheat lens: find duplicated or illegitimately-spawned items, spot hoarding, and audit rewards — something no Valheim admin panel currently offers.

### 11. First-join automation & starter kit  `[S · RPC]`
- **Tier:** B
- **What:** Use the first-seen ledger to detect a genuinely new player and, once only, auto-grant a configured item kit and/or fire a welcome broadcast. Reuses the existing gear-kit/bulk-pack definitions from the Items tab as the payload.
- **Why:** Standardizes onboarding on community servers (everyone starts with the same fair kit) without an admin having to be online at the moment each newcomer joins.

### 12. Combat-log protection  `[L · RPC]`
- **Tier:** B
- **What:** Admin-toggled: when a player disconnects shortly after taking/dealing damage, keep their character present and vulnerable in-world for N seconds (or drop their carried loot), so logging out mid-fight can't be an escape. Server-side behavior with a configurable window and an exempt-list.
- **Why:** Anti-combat-logging is one of the most-requested features on PvP and hardcore servers; ServerCharacters has a variant, and matching it closes a notable gap — flagged as higher-risk because keeping a ZDO alive past peer disconnect is fiddly.

### 13. Character freeze / quarantine (escrow)  `[M · RPC]`
- **Tier:** B
- **What:** Flag a suspected cheater/griefer so that on next login their character is snapshotted and their inventory moved into an admin escrow (and optionally movement/build locked), pending review — a reversible alternative to an outright ban. Vault-dependent.
- **Why:** Gives owners a proportionate moderation step: freeze and investigate suspicious loot without permanently losing the player or destroying evidence, then restore-or-ban after review.

---

## Gap analysis — ideas the domain sweeps missed (13)

### G1. Server-side anti-cheat / impossible-state monitor  `[L · RPC]`
- **Tier:** A — flagship, largest anti-abuse gap (alert can reuse existing watchlist toast plumbing)
- **What:** Companion samples server-observed player state and flags physically impossible values: movement or teleport beyond max speed with no portal, flying/no-fall without an admin grant, health/stamina/eitr above legit caps, instant skill jumps, item stacks over max, or damage immunity (external god-mode). On trip: alert online admins + audit entry + optional auto-freeze/kick. Per-check enable + configurable thresholds.
- **Why:** The single biggest unmet ask for public servers, and a true gap here. The list covers anti-GRIEF (structure/terrain destruction) and ALT detection (IP grouping) but nothing catches players running OTHER cheat mods (fly/speed/spawn/infinite-resource) — exactly what dedicated Valheim anti-cheat plugins target. Moves the mod from cleanup-after-the-fact to detection.

### G2. Required / forbidden client-mod enforcement  `[M · RPC]`
- **Tier:** A — high value, unique to server-owner niche
- **What:** Extend the existing version handshake so joining clients transmit their loaded BepInEx plugin GUIDs+versions; the companion rejects clients (with a custom disconnect message) that are missing owner-required mods or running blacklisted cheat mods, logging the full manifest. Report-only mode for observation before enforcing.
- **Why:** Extensibility/integrity gap. Community servers routinely need 'everyone must run mod X' or 'no cheat mods.' Vanilla only validates its own hash; nothing in the list enforces a client mod manifest. Natural extension of the handshake that already exists.

### G3. Rules-acceptance gate + onboarding flow  `[M · RPC]`
- **Tier:** B — solid, reuses chat bridge + a small acceptance store
- **What:** First-join players get a mandatory rules/MOTD panel and are held (frozen at spawn or build-blocked) until they type !accept in chat; acceptance is timestamped and stored server-side for dispute resolution. Rules text is localized via the existing Loc system; grace behavior configurable.
- **Why:** Onboarding gap. First-join kit and MOTD exist, but nothing makes a player ACKNOWLEDGE rules — the record that makes a 'you were warned' ban defensible. Standard on other community game servers, absent from Valheim tooling.

### G4. Companion-hosted web admin dashboard (remote/mobile)  `[L · client-only]`
- **Tier:** B — differentiator, higher cost, no client RPC needed
- **What:** The companion runs a lightweight token-authenticated HTTP server (net48 HttpListener, bound to localhost/LAN or behind a reverse proxy) serving a mobile-friendly page: live roster, kick/ban/mute, broadcast, force-save, restart, audit tail — moderate from a phone browser with no game client running.
- **Why:** Remote/mobile-control gap. Discord slash-commands are the only remote path proposed; a real-time web UI is richer and frees owners who don't want a Discord dependency. Distinct transport, runs autonomously server-side.

### G5. Public modder API / extension SDK  `[L · client-only]`
- **Tier:** A — strategic ecosystem play (extensions may add their own RPCs)
- **What:** Ship a stable, semver'd interface assembly other mods reference to (a) register custom panel tabs/buttons, (b) register custom admin actions that automatically inherit the same adminlist gating + audit logging, and (c) subscribe to the companion's event bus (join/leave/death/ban/spawn). Documented contract.
- **Why:** Extensibility gap explicitly called out. Turns the panel into a platform other modders build on, compounding adoption; no competing admin mod (ValheimPlus, ServerCharacters, WardIsLove) offers a first-class extension surface. Each extension gets gating + audit for free.

### G6. Data export & external metrics endpoint  `[M · RPC]`
- **Tier:** B — solid, owner-grade (export request RPC; metrics endpoint is server-side)
- **What:** One-click CSV/JSON export (downloaded through the panel) of the audit log, player ledger, bans/warnings, and economy for spreadsheets/BI. Plus an optional Prometheus-format /metrics endpoint (players online, ZDO count, server frame time, uptime, last-save status) the companion exposes for Grafana / uptime dashboards.
- **Why:** Analytics/export gap. Leaderboards and digests only post curated embeds to Discord; owners running a monitoring stack or wanting raw data have no export path. Low-effort, high-credibility for serious hosts.

### G7. Global dry-run / simulate mode for destructive ops  `[M · RPC]`
- **Tier:** A — high value / safety (adds a dryRun flag to existing destructive RPCs)
- **What:** A session toggle that makes any destructive server op (mass structure remove, terrain reset, ZDO purge, orphan sweep, player wipe) return the exact affected count + a sample manifest WITHOUT executing — one consistent 'preview → then execute' contract across every destructive action, not ad-hoc per feature.
- **Why:** Testing/safety gap. Some features mention count previews individually, but a uniform simulate mode makes the entire destructive surface safe to explore, demo, and trust — preventing the catastrophic mis-radius delete on a live community world.

### G8. Companion self-test & connectivity diagnostics  `[S · RPC]`
- **Tier:** A — high value / low effort
- **What:** One click round-trips a dedicated test RPC, confirms client↔companion version match, verifies the adminlist gate actually recognizes the caller, checks write access + free space on the world/backup dirs, validates the config file, and reports a green/red checklist with fix hints.
- **Why:** Directly kills the known 'stale server companion silently drops RPCs' failure recorded in project memory. Turns an opaque handshake into a diagnosable one — the first thing an owner runs when 'the panel isn't doing anything.' Cheap, high trust.

### G9. Player voting system  `[M · RPC]`
- **Tier:** B — solid community add (extends chat bridge)
- **What:** Server-run polls via the chat bridge: vote-skip-night, vote-kick (threshold + cooldown gated, owner can disable entirely), vote-for-next-event, vote-on-a-rule. Center-screen tallies, configurable quorum/duration; passing results trigger existing actions (skip time, kick, launch event).
- **Why:** Community QoL gap. A staple of public servers that lets players self-govern when no admin is online. Rides the existing chat-command bridge + broadcast infra rather than inventing new plumbing.

### G10. Reserved admin/VIP slots + join queue  `[M · RPC]`
- **Tier:** B — solid (server-side join gate + a panel config RPC)
- **What:** When the server reaches max players, the companion keeps N slots reserved for admins/whitelist/Discord-linked VIPs by refusing the lowest-priority non-VIP join with a queue-position message, so staff can always get in during a rush.
- **Why:** Operational gap. Lockdown is an emergency all-or-nothing switch; reserved slots is normal-operation VIP priority — a frequently requested capability common in other game-server managers and absent from Valheim tooling.

### G11. Free-cam inspection camera + measurement tools  `[M · client-only]`
- **Tier:** B — QoL, client-only rendering
- **What:** A detached noclip free camera (distinct from spectate, which follows a player, and from fly/ghost, which move the body) to glide through and screenshot builds, plus a two-point ruler (distance + height delta), a level/grid overlay, and a north indicator for builders and content creators.
- **Why:** Admin-own-gameplay + builder QoL gap. Nothing in the list gives a true detached camera or measurement. Build-focused owners and streamers want exactly this; Object Inspector identifies a piece but is not a camera or ruler.

### G12. Admin personal loadout vault (own-character quick-swap)  `[S · client-only]`
- **Tier:** B — QoL / low effort (reuses existing give/remove RPCs)
- **What:** Save/restore the ADMIN'S OWN inventory + equipped gear + relevant skills as named loadouts (e.g. 'Building', 'Combat test', 'Screenshots') for instant context-switching between admin tasks; client-side over the existing give/remove paths.
- **Why:** Admin-own-gameplay gap. The player snapshot vault is a server-side tool for OTHER players; this is a lightweight personal quick-swap the admin uses constantly while testing and building. Small, high daily utility, no server change.

### G13. Server config pack export/import (clone a proven setup)  `[M · RPC]`
- **Tier:** B — migration (export/import read+write RPCs)
- **What:** Export the entire AP server configuration — no-build zones, teleport hubs, scheduled events, loot crates/odds, reward tiers, MOTD/broadcasts, Discord cfg, permissions — as one portable, versioned 'server pack' file another owner can import to clone a battle-tested configuration. Selective import + a version/compat header.
- **Why:** Migration gap. Cross-server CHARACTER transfer exists, but nothing moves the SERVER'S OWN configuration. Lets owners share turnkey setups, seed a new server, or spin up a staging copy in one step.

---

## Also discussed, not in the 92-idea backlog

### Growth / promotion (brainstorm of 2026-07-21 03:29)
- **Landing page at hephaestuslabs.com** — hero screenshot, features, download buttons, Discord invite (domain + VPS already owned).
- **60-second demo video** — map → press T → teleport; for store embeds, Reddit, YouTube.
- **Steam Community admin guide** — "How to admin your Valheim server" evergreen guide for search traffic.
- **Reddit + official-Discord promotion sequence** — r/valheim, r/valheimmods, Valheim official Discord modding channels (after store listings are live).
- **Free player-facing QoL mod** (deliberate maybe — only after the Discord bridge ships; splits focus).

### Known deferred fixes (also tracked in CONTINUE_HERE.md)
- Roster Ban crossplay-prefix fix (`OnServerBan` BareId-strips connected peers → crossplay ban no-op).
- Real coffee.png icon (shipped one is a generated placeholder).
- Thunderstore page bullets for multi-step undo / SE-targets (ride the next version bump).
- Store screenshots reshoot (still 2.2.x-era).
- `AP_SrvMsg` server RPC exists but has **no client UI** (private admin message to one player) — half-built feature, finish or remove.
- `KillNearby(includeTamed)` parameter exists but no UI toggle — dead parameter, wire up or drop.
- Orphaned locale key `world.bosses` (superseded by `world.sac_stones`) — delete from all 9 locales.

