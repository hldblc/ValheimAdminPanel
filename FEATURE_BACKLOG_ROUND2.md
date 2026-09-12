# Advanced Admin Panel — Feature Backlog, Round 2

**Origin:** brainstorm on **2026-09-10** (session `3b37abf2`), after the 105-idea round-1 backlog was fully
implemented. **None of these are built.** Every idea below was checked against `FEATURES.md` and
`FEATURE_BACKLOG.md` so it does not duplicate round 1. This file is the durable copy — do not lose it.

**How to read the tags:** effort `S` / `M` / `L` · `server` = companion-only, works for every player ·
`client` = panel-only, no server change · `target-mod` = the other player's client must also run the mod.

---

## A. Moderation, access & security

### 1. Tripwire chests  `[M · server]`
- **What:** Mark any container as a tripwire. When it is opened the companion logs who opened it, when, and
  where, and pings every online admin. Exact identity when the opener runs the mod; otherwise the nearest
  player who owned the chest's ZDO at that moment.
- **Why:** Catches thieves in the act instead of after the loot is gone. Bait chests are a classic
  community-server trick that no Valheim mod supports.

### 2. Player movement trail  `[M · server]`
- **What:** The companion samples every peer's reference position every ~15 s into a rolling server-side
  store. Panel: "who was within N m of this spot between T1 and T2", plus a breadcrumb trail per player.
- **Why:** Answers "who was at the base at 3 am" with evidence rather than guesswork. Complements the
  round-1 grief-spike alerts, which only see destruction bursts.

### 3. Evidence capture  `[S · client]`
- **What:** One hotkey takes a screenshot stamped with time, coordinates, nearby player names and IDs, and
  files it under the target's rap sheet as an attachment plus a note.
- **Why:** Ban disputes are won with evidence. Today admins juggle Steam screenshots and hand-typed notes.

### 4. Two-admin approval for nuclear actions  `[M · server]`
- **What:** Optional rule: a wipe, purge, mass remove, permanent ban or restore needs a second online admin to
  confirm within 2 minutes; the companion holds the action until then and logs both names.
- **Why:** The four-eyes principle. A single tired admin can still nuke a town today, second click or not.

### 5. Panel lock and streamer mode  `[S · client]`
- **What:** A PIN locks the panel after N idle minutes. Streamer mode blurs Steam IDs, IPs and the server
  password in every view.
- **Why:** Many admins stream or share a PC. One leaked ID on stream is enough to start a harassment wave.

### 6. AFK detection and auto-kick  `[M · server]`
- **What:** No movement for N minutes → whisper a warning → kick with a polite reason. Admins and a per-player
  exemption list are excluded. Configurable, default off.
- **Why:** Idle players hold slots on full servers. Pairs naturally with the round-1 reserved slots and queue.

### 7. Access-list editor  `[M · server]`
- **What:** One view over `adminlist.txt`, `permittedlist.txt` and `banlist.txt`: add, remove, search,
  annotate, and a whitelist on/off switch. The companion reloads the lists live, no restart.
- **Why:** Every list is still edited over SFTP. Roles from round 1 layer on top of these files but do not
  edit them.

### 8. Runtime password and visibility  `[M · server]`
- **What:** Change the join password from the panel; new joins use it immediately. Public/private flag is
  staged for the next restart, because the backend registration happens at boot.
- **Why:** Rotating a leaked password currently means stopping the server and editing launch args.

### 9. Admin-only chat channel  `[M · server]`
- **What:** A chat prefix or panel box that relays only to online admins and moderators. Messages are also
  kept in the round-1 chat buffer, tagged as staff.
- **Why:** Staff coordinate a ban or a rescue without the whole server reading it. Direct Message from
  round 1 is admin-to-one-player; this is staff-to-staff.

---

## B. World, creatures & items

### 10. Tame roster and pet manager  `[M · server]`
- **What:** World-wide scan for tamed creatures: species, pet name, owner, health, star level, location.
  Actions: teleport to, heal, rename, un-tame, or cull a runaway breeding pen with a two-click confirm.
- **Why:** Breeding pens are the top hidden lag source. The round-1 census counts prefabs; this shows owners.

### 11. Creature stat editor  `[M · server]`
- **What:** Aim at a creature and edit it: current and max health, star level, name, tame/untame,
  invulnerable, and a despawn button.
- **Why:** Event bosses with custom health, a pet that lost stars, or a stuck immortal boss are all fixed
  in place today only by killing and respawning.

### 12. Spawner and nest manager  `[M · server]`
- **What:** List creature spawners near the admin or near a coordinate: greydwarf nests, draugr spawners,
  surtling geysers, dungeon spawners. Remove one, remove all within a radius, or teleport to it. Uses the
  ZDO owner fix from 2.3.0.
- **Why:** Spawn towns keep getting overrun by a nest nobody can find. Vanilla offers nothing.

### 13. Location finder  `[M · server]`
- **What:** Ask the server for the nearest N of any location type: crypts, caves, Haldor, Hildir, the Bog
  Witch, Vegvisir stones, unexplored dungeons. Distance, direction, and teleport per row.
- **Why:** Admins spend real time flying around to help a lost player. Round 1 only spawns locations and
  teleports to sacrificial stones.

### 14. Server-wide map pins  `[M · target-mod]`
- **What:** Admin-defined named pins pushed to every modded client's map: spawn town, arena, shop, event.
  Categories with icons; players can hide a category.
- **Why:** Vanilla shared pins need a cartography table and are per-player. Unmodded clients see nothing,
  the panel says so.

### 15. Map reveal or reset for any player  `[S · target-mod]`
- **What:** Reveal the whole map, reveal a radius, or reset exploration for a chosen online player.
- **Why:** New players on old servers ask for it constantly, and it is a two-line client action.

### 16. Chest viewer and container search  `[M · server]`
- **What:** Aim at a chest to see and edit its contents from the panel. World-wide search: "which
  containers hold item X", with owner, count and teleport.
- **Why:** Locates duped or stolen items and lost storage. The round-1 item audit only reads player
  inventories.

### 17. Item attribute editor  `[M · server]`
- **What:** When giving an item, pick quality level, durability, variant and the crafter name stamped on it.
  Also edit those on an aimed item drop.
- **Why:** Prize weapons, replacement gear at the right quality, and exact "Crafted by" stamps for events.

### 18. Trader stock and price editor  `[M · target-mod]`
- **What:** Edit what Haldor, Hildir and the Bog Witch sell and for how much. The companion broadcasts the
  stock table; modded clients apply it.
- **Why:** Server economies want custom vendor lists. Unmodded clients see vanilla stock, stated in the UI.

### 19. Recipe and build-piece blacklist  `[M · server + target-mod]`
- **What:** Ban specific recipes or build pieces server-wide. Modded clients hide them; for unmodded
  clients the companion removes a banned piece the moment it is placed, like round-1 protection zones.
- **Why:** Ballistas, the hoe, or trophy walls cause endless disputes. Owners want a switch, not a rule post.

---

## C. Rules, events & operations

### 20. Server-enforced skill gain rate and cap  `[M · target-mod]`
- **What:** A multiplier for skill gain and an optional level cap, set once on the server and applied by
  modded clients. Optional per-skill overrides.
- **Why:** Skill gain is not a vanilla world modifier, so "2× skills" servers currently need a separate mod.

### 21. Death rules  `[M · server + target-mod]`
- **What:** Per-server death policy: lives counter with a ghost or spectator end state, permadeath into the
  round-1 quarantine, or keep-gear-on-death. Announced on join.
- **Why:** Hardcore and seasonal servers are a real niche with no clean tooling.

### 22. Custom raid composer  `[M · server]`
- **What:** Build your own raid: creature list, counts, star levels, waves, duration, radius, and the
  banner text. Save as a preset, fire it at your position or at a pin.
- **Why:** Round-1 events fire vanilla raids and a fixed boss rush. Owners want "20 two-star draugr at the
  arena in three waves".

### 23. Bounty board  `[M · target-mod]`
- **What:** Admins post bounties: kill N of a creature, kill a named boss, deliver items. Progress is
  tracked for players running the mod and paid from the round-1 currency ledger.
- **Why:** Gives the economy a sink and a purpose between scheduled events.

### 24. Companion self-update  `[L · server]`
- **What:** The companion checks GitHub releases, downloads the matching version, verifies the SHA256, and
  stages it for the next restart. The panel shows "update staged" and a restart button.
- **Why:** Every stale-companion failure to date came from a manual upload step. The panel already warns
  about mismatches; this closes the loop.

### 25. Client performance census  `[M · target-mod]`
- **What:** Modded clients report average FPS, ping, and their plugin list every minute. The panel shows a
  roster: who is lagging, who runs which mods, who is on an old version.
- **Why:** "The server is lagging" is usually one client. Round-1 perf graph and ping are server-side only.

---

## Also considered, not in the 25
- More languages: Russian, Simplified Chinese, Japanese, Korean, Turkish, Ukrainian `[S · client]` — the
  font fallback already handles non-Latin text.
- Mini HUD widget outside the panel: online count, tick rate, next autosave, watchlist joins `[S · client]`.
- World rotation: stage a different world for the next restart `[M · server]`.
- Generic webhook and Telegram alerts beside Discord `[M · server]`.
