# Backlog implementation — 2026-07-31

The `FEATURE_BACKLOG.md` list (recovered the same day) is implemented. **Nothing has been committed or
released.** The build WAS deployed to the local game on 2026-07-31 at the user's request and smoke-tested
in single player; see `CONTINUE_HERE.md` for current state and the outstanding dedicated-server test.
Pre-change DLLs for rollback: `ValheimAdminPanel-backup-2026-07-31\deployed-rollback\`.

## How it is wired

All new code is **additive**: `ValheimAdminPanel\Features\*.cs` and `AdminPanelCompanion\Wave*.cs`, each
another `partial` of the existing plugin class. The two pre-existing source files carry only hook lines:

| File | Change |
|---|---|
| `AdminPanelPlugin.cs` | `partial` keyword; `FeaturesInit()`, `FeaturesOnGUI()`, `FeaturesResetSession()`; `DrawFeatureTabButton()` in the tab row; `default:` case routing the Extras tab; `MinPanelWidth()` replacing two `660f` literals; `_killTamed` field + toggle |
| `CompanionPlugin.cs` | `partial` keyword; `FeaturesInit()`; the crossplay ban fix and its unban counterpart |

Everything else hangs off those. Deleting a wave's files plus its glue file removes it cleanly.

**Master switch:** `[Features] EnableExtrasTab` (default true) hides the whole Extras tab. Every
behavior-changing server feature defaults **off**; only passive views and logging default on.

## Capability tiers (the rule that shapes half the design)

Valheim keeps character data (inventory, skills) **client-side** in the player's `.fch`. A dedicated server
never holds it. So:

- **Works for everyone:** teleports, kick/ban, chat, position/health observation, tombstones, playtime,
  MOTD and on-screen text, warps, rescue, votes, currency balances.
- **Needs the companion DLL on the target's client:** inventory read/write, item grants, skills, status
  effects, snapshots, shops.
- **Impossible:** editing an offline player's character file. Every "offline" action is a **queue** applied
  on their next join.

`Wave34Core.HasMod(uid)` answers this per peer; features degrade and say *why* rather than silently no-op.

## Known limits worth remembering

- **Server-truth views don't populate on a listen-server host.** The mandatory `SenderIsServerReply` gate
  needs a real server peer; weakening it would reopen the spoofing hole closed in 2.4.0. Action buttons work.
- **Alt detection is timing-only.** Valheim exposes no IP to plugins (sockets return the platform id).
- **Protection zones are after-the-fact.** No-build deletes pieces that appear after the zone exists;
  no-damage restores health rather than preventing a hit.
- **Anti-cheat sees only what a client syncs**, and "no answer" from the mod probe is a distinct status,
  never "clean".
- **Discord is one-way.** Inbound commands need the bot side to call the exposed link-code method.

## Verification performed

Both DLLs build 0 errors. Locales **1397 keys × 9 languages**, validator green; a second checker confirms
**1077 keys referenced in code, none missing, none orphaned**.

An 8-dimension adversarial review (48 agents; every finding independently attacked by a verifier defaulting
to "this is wrong") produced 40 candidates → **29 confirmed, 11 refuted**. All 29 are fixed. Highlights:

- **Currency exploit (high):** buy and sell shared one pending-token table and neither reply handler checked
  the transaction kind, so answering a purchase with a sale confirmation credited the price instead of
  charging it — items kept *and* money gained, logged as ordinary trading. Both handlers now verify kind
  before consuming the token.
- **Unban regression (introduced by the crossplay ban fix in this same session):** bans store both the bare
  and full id, but unban removed only the two spellings of what was typed, so unbanning by bare id left the
  player banned. Unban now normalises every stored entry.
- **Audit chokepoint:** did a synchronous file open/append/close inside the RPC-bus prefix — any non-admin
  running the public panel could drive unbounded disk I/O by leaving a tab open, and a `|` in a character
  name forged audit columns. Now sanitised, per-sender throttled, buffered, and rotated at 8 MB.
- **Dead code paths:** the World Modifiers sub-tab called RPCs no server file implemented, and rescue /
  offline-rescue / player-reset had no client caller. Both closed.
- **`FeatureStore.Tail`** read whole log files on the main thread; now a bounded backward read.

The refutations mattered too — "MinPanelWidth silently widens every saved panel", "guard escalation becomes a
permanent kick loop" and "mass remove destroys 5000 ZDOs in one frame" were each dismissed against the guards
that already prevent them.

## Not done

- **Never tested on a dedicated server** — and that is the design target (the mod supports single player but
  is built for dedicated multiplayer hosts). Roughly two thirds of these features cannot be meaningfully
  exercised solo. This is the single biggest outstanding risk.
- **Single player is smoke-tested only** (2026-07-31). It surfaced two real bugs, both fixed: the Extras chip
  row could not navigate backwards, and on a listen host every companion reply was discarded because
  `ServerUid()` is 0 there — fixed with the in-process bridge rather than by weakening the anti-spoof gate.
- **Not committed.** Per the standing rule, git stays parked until sign-off.
- Non-code backlog items (landing page, demo video, Steam guide, Reddit posts) were out of scope.
