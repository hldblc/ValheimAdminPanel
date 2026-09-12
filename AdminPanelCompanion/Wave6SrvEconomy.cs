using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 6 — economy core (server side) ====================
    // Currency ledger, admin-defined shops, playtime ranks, daily streaks and a weighted lottery, plus the
    // three panel RPCs (AP_SrvEcoReq / AP_SrvEcoAdjust / AP_SrvShopSet).
    //
    // ONE master switch: Features.EnableEconomy, default FALSE. Nothing in this file changes a single byte of
    // server behaviour until an owner turns it on — no chat commands are registered, no currency accrues, no
    // ranks are granted, and the RPC handlers answer "economy disabled". A server that merely upgrades the
    // DLL sees exactly what it saw before.
    //
    // THE CAPABILITY RULE THAT SHAPES THIS WHOLE FILE (wave 3/4 capability model):
    //   Inventories live CLIENT-SIDE, in the player's own .fch. The server cannot add an item to, or read an
    //   item out of, a player who is not running AdminPanelCompanion.dll. Therefore EVERY currency-for-items
    //   path here (!buy, !sell, !lottery) is REFUSED outright for a client without the companion — it is
    //   never queued through the wave-4 offline queue, because a shop that takes coins now and hands over
    //   goods "sometime after your next login" is worse than a shop that says no. Currency-only paths
    //   (!bal, !pay, !daily, ranks, online earnings, admin adjustments) work for EVERYONE, modded or not:
    //   the ledger is a server-side table and never touches a character file.
    //
    // MONEY-SAFETY INVARIANTS (the reason the delivery paths look convoluted):
    //   1. Balances are long, clamped to [0, MaxBalance]. There is exactly ONE mutation helper (Mutate) —
    //      clamp, lifetime bookkeeping, persist, audit all happen there and nowhere else.
    //   2. Items first, money second. A purchase/lottery roll deducts NOTHING until the target client has
    //      confirmed the items actually landed (AP_EcoDeliverRep). No confirmation = no charge = the "refund"
    //      is structural rather than a compensating write that could itself fail.
    //   3. A sell credits NOTHING until the client confirms it removed the exact stack (AP_EcoTakeRep).
    //   4. One outstanding item transaction per player (_busy). While a delivery is in flight that player
    //      cannot !buy, !sell, !pay or !lottery, so the balance checked at request time is still the balance
    //      at confirm time. That closes the "spend it twice while the packet is in the air" window without
    //      pre-deducting anything.
    //
    // House rules obeyed: one Harmony class per target method, each applied in its own try/catch with a named
    // warning + Wave2Ops.ReportPatch; every packet read wrapped and every count clamped server-side; replies
    // only to the requester; reply packets start with an int version.
    internal static class Wave6Economy
    {
        // ---- store tables ----
        private const string TblEco = "eco";          // id     -> "balance|lifetime|name"
        private const string TblShop = "shop";        // sku    -> "prefab|count|price|buy"
        private const string TblStreak = "streak";    // id     -> "lastDayUtc|streak|claimedDayUtc"
        private const string TblRanks = "ranks";      // id     -> "rank|grantedTicks"
        private const string TblPresence = "presence";// id     -> "first|last|sessions|totalSeconds|lastName"  (wave 1 owns it; READ ONLY here)

        private const int Ver = 1;                    // wire version for every packet this file writes

        // ---- wire caps (see spec-companion §3.2: exceeding the panel's read bound discards the WHOLE reply) ----
        private const int EcoShipCap = 60;
        private const int ShopShipCap = 40;

        // ---- clamps ----
        private const long MaxBalance = 1000000000000L;  // 1e12: far past any real economy, far from long overflow
        private const int MaxIdLen = 64;
        private const int MaxTextLen = 200;
        private const int MaxSkuLen = 24;
        private const int MaxPrefabLen = 64;
        private const int MaxStackPerTrade = 999;        // one SKU may never move more than this many items
        private const int ShopPageSize = 10;
        private const int MaxShopRows = 200;             // hard ceiling on stored SKUs
        private const int MaxCurrencyNameLen = 16;

        // ---- timings ----
        private const float TickSeconds = 5f;            // accrual sweep
        private const float RankSeconds = 30f;           // rank evaluation sweep
        private const float AuditFlushSeconds = 60f;     // aggregate earn/penalty audit line
        private const float DeliverTimeout = 20f;        // how long we wait for a client's delivery confirmation
        private const double PayConfirmSeconds = 30d;    // !pay two-step window
        private const double MaxAccrualStep = 30d;       // seconds credited for one sweep, however long the frame hitch was

        // ==================== config ====================

        private static ConfigEntry<bool> _enableEconomy;
        private static ConfigEntry<string> _currencyName;
        private static ConfigEntry<int> _earnPerMinute;
        private static ConfigEntry<int> _earnPerDeathPenalty;
        private static ConfigEntry<string> _rankThresholds;
        private static ConfigEntry<int> _rankReward;
        private static ConfigEntry<bool> _enableDailyStreak;
        private static ConfigEntry<int> _streakDailyCoins;
        private static ConfigEntry<int> _streakBonusPerDay;
        private static ConfigEntry<int> _streakMaxBonus;
        private static ConfigEntry<bool> _enableLottery;
        private static ConfigEntry<int> _lotteryTicketPrice;
        private static ConfigEntry<string> _lotteryPrizes;

        /// <summary>The master gate. Everything in this file is a no-op while it is false.</summary>
        private static bool EconomyOn => _enableEconomy != null && _enableEconomy.Value;

        private static int EarnPerMinute => _earnPerMinute != null ? Mathf.Clamp(_earnPerMinute.Value, 0, 100000) : 0;
        private static int DeathPenalty => _earnPerDeathPenalty != null ? Mathf.Clamp(_earnPerDeathPenalty.Value, 0, 100000) : 0;
        private static int RankReward => _rankReward != null ? Mathf.Clamp(_rankReward.Value, 0, 1000000) : 0;
        private static bool StreakOn => _enableDailyStreak != null && _enableDailyStreak.Value;
        private static int StreakDaily => _streakDailyCoins != null ? Mathf.Clamp(_streakDailyCoins.Value, 0, 1000000) : 10;
        private static int StreakBonus => _streakBonusPerDay != null ? Mathf.Clamp(_streakBonusPerDay.Value, 0, 1000000) : 5;
        private static int StreakCap => _streakMaxBonus != null ? Mathf.Clamp(_streakMaxBonus.Value, 0, 1000000) : 100;
        private static bool LotteryOn => _enableLottery != null && _enableLottery.Value;
        private static int TicketPrice => _lotteryTicketPrice != null ? Mathf.Clamp(_lotteryTicketPrice.Value, 0, 1000000) : 50;

        /// <summary>Display name of the currency, sanitized ('|' is the store's field separator).</summary>
        internal static string CurrencyLabel()
        {
            var raw = _currencyName != null ? _currencyName.Value : null;
            if (string.IsNullOrEmpty(raw)) return "coins";
            var s = raw.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) return "coins";
            return s.Length > MaxCurrencyNameLen ? s.Substring(0, MaxCurrencyNameLen) : s;
        }

        // ==================== in-memory state ====================

        // Per-online-peer accrual. Built ONLY from ZNet.GetPeers(), so a listen-server host is structurally
        // absent from it (the host is never in m_peers — spec-companion §2.3) and therefore never earns
        // online currency. That is a documented limitation, not a pruning bug: entries are added and removed
        // purely from the peer sweep, so there is no "GetPeer() == null" prune to get wrong.
        private sealed class OnlinePlayer
        {
            public string Id;
            public string Name;
            public double CarrySeconds;     // sub-minute remainder not yet paid out
            public double SessionSeconds;   // this session's length, for rank playtime
        }

        private static readonly Dictionary<long, OnlinePlayer> Online = new Dictionary<long, OnlinePlayer>();

        // One in-flight item transaction per player, keyed by token; _busy mirrors the uids (invariant 4).
        private sealed class Pending
        {
            public long Token;
            public long Uid;
            public string Id;
            public string Name;
            public string Kind;      // "buy" | "lottery" | "sell"
            public string Sku;       // "" for a lottery roll
            public string Prefab;
            public int Count;
            public long Price;
            public float Expiry;     // Time.unscaledTime deadline
        }

        private static readonly Dictionary<long, Pending> Pendings = new Dictionary<long, Pending>();
        private static readonly HashSet<long> Busy = new HashSet<long>();
        private static long _nextToken = 1L;

        // !pay confirmation step: senderUid -> the transfer awaiting a repeat of the same command.
        private sealed class PayConfirm
        {
            public string TargetId;
            public string TargetName;
            public long Amount;
            public DateTime ExpiresUtc;
        }

        private static readonly Dictionary<long, PayConfirm> PayConfirms = new Dictionary<long, PayConfirm>();

        // Aggregate audit counters (one line per flush, never one per player — an earning server would
        // otherwise write an audit entry per player per minute forever).
        private static int _earnPlayers;
        private static long _earnTotal;
        private static int _penaltyCount;
        private static long _penaltyTotal;

        private static readonly System.Random Rng = new System.Random();

        private static bool _inited;
        private static float _nextTick;
        private static float _nextRankTick;
        private static float _nextAuditFlush;
        private static double _lastSweepTime;

        // Mirror of CompanionPlugin.SenderSanitizerActive, read reflectively once (it is private and this
        // file may not edit CompanionPlugin.cs). See SenderIsServerPeer.
        private static bool _sanitizerFieldRead;
        private static FieldInfo _sanitizerField;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableEconomy = cfg.Bind("Features", "EnableEconomy", false,
                    "Master switch for the whole economy (currency, shops, ranks, daily streak, lottery). OFF by default: with it off no chat command is registered and no balance ever changes.");
                _currencyName = cfg.Bind("Features", "CurrencyName", "coins",
                    "Display name of the server currency, e.g. 'coins', 'gold', 'silver'.");
                _earnPerMinute = cfg.Bind("Features", "EarnPerMinuteOnline", 0,
                    "Currency granted per full minute a player is connected. 0 = nobody earns anything just by being online.");
                _earnPerDeathPenalty = cfg.Bind("Features", "EarnPerDeathPenalty", 0,
                    "Currency subtracted when a player dies (balances never go below zero). 0 = deaths cost nothing.");
                _rankThresholds = cfg.Bind("Features", "RankThresholdsHours", "",
                    "Playtime ranks, format '10=Bronze,50=Silver,100=Gold' (hours=name). Empty = ranks are off.");
                _rankReward = cfg.Bind("Features", "RankRewardCoins", 0,
                    "One-off currency reward granted with each new rank. 0 = the rank is honorary.");
                _enableDailyStreak = cfg.Bind("Features", "EnableDailyStreak", false,
                    "Enable the '!daily' claim (once per UTC day, consecutive days build a streak bonus).");
                _streakDailyCoins = cfg.Bind("Features", "StreakDailyCoins", 10,
                    "Base currency granted by '!daily'.");
                _streakBonusPerDay = cfg.Bind("Features", "StreakBonusPerDay", 5,
                    "Extra currency per consecutive day of the streak, added on top of StreakDailyCoins.");
                _streakMaxBonus = cfg.Bind("Features", "StreakMaxBonus", 100,
                    "Ceiling for the streak bonus, so a year-long streak cannot print unlimited currency.");
                _enableLottery = cfg.Bind("Features", "EnableLottery", false,
                    "Enable the '!lottery' loot crate (spends LotteryTicketPrice, rolls a weighted prize).");
                _lotteryTicketPrice = cfg.Bind("Features", "LotteryTicketPrice", 50,
                    "Currency cost of one '!lottery' roll. Charged ONLY after the prize was confirmed delivered.");
                _lotteryPrizes = cfg.Bind("Features", "LotteryPrizes", "",
                    "Lottery prize table, format 'prefab:count:weight,prefab:count:weight'. Empty = the lottery has nothing to give and refuses.");
            }

            // Owner-only by design: minting currency and defining shop prices ARE the economy. Passing null as
            // the role grant means no builtin role (moderator/builder) receives them, so with tiered roles
            // enforced only an owner (or an explicit custom roleperms entry) can call them. The read RPC
            // AP_SrvEcoReq is deliberately NOT registered here: it changes nothing and polls from the panel,
            // and auditing a poll would drown the log (same reasoning as AP_SrvInfoReq).
            CompanionPlugin.RegisterAuditedRpc("AP_SrvEcoAdjust", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvShopSet", null);

            var ok = true;
            try { Harmony.CreateAndPatchAll(typeof(Wave6EconomyRpcRegistration)); }
            catch (Exception e)
            {
                ok = false;
                CompanionPlugin.FeatureLog($"Wave6EconomyRpcRegistration patch failed (economy RPCs and item delivery unavailable): {e.Message}");
            }
            try { Wave2Ops.ReportPatch("Wave6EconomyRpcRegistration", ok); }
            catch (Exception) { }

            // The death penalty is a currency-only effect, so it may stay subscribed permanently; the handler
            // itself re-checks the master switch and the penalty amount on every death.
            try { Wave34Core.OnDeath += OnDeathObserved; }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Economy could not subscribe to the death feed (death penalty disabled): {e.Message}"); }

            // Chat commands are registered ONLY when the economy is on. Wave1Chat's dispatcher CONSUMES any
            // registered '!' command, so registering them while the economy is off would silently swallow
            // "!bal"/"!shop" chat on a server that never opted in — exactly the "zero change until you turn it
            // on" rule this wave must not break. Consequence (documented limitation): flipping EnableEconomy
            // on at runtime enables the RPCs, accrual and ranks immediately, but the chat commands appear
            // after the next server restart.
            if (EconomyOn)
            {
                Wave1Chat.RegisterChatCommand("bal", OnBalCommand);
                Wave1Chat.RegisterChatCommand("shop", OnShopCommand);
                Wave1Chat.RegisterChatCommand("buy", OnBuyCommand);
                Wave1Chat.RegisterChatCommand("sell", OnSellCommand);
                Wave1Chat.RegisterChatCommand("pay", OnPayCommand);
                Wave1Chat.RegisterChatCommand("daily", OnDailyCommand);
                Wave1Chat.RegisterChatCommand("lottery", OnLotteryCommand);
            }
        }

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!_inited) return;

            var now = Time.unscaledTime;

            // Pending deliveries must expire even if the economy was switched off mid-flight, otherwise a
            // player could stay flagged busy forever.
            if (Pendings.Count > 0)
            {
                try { ExpirePendings(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Economy pending sweep failed: {e.Message}"); }
            }

            if (!EconomyOn) return;

            if (now >= _nextTick)
            {
                _nextTick = now + TickSeconds;
                try { AccrueOnline(now); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Economy accrual sweep failed: {e.Message}"); }
                try { ExpirePayConfirms(); }
                catch (Exception) { }
            }

            if (now >= _nextRankTick)
            {
                _nextRankTick = now + RankSeconds;
                try { EvaluateRanks(); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Economy rank sweep failed: {e.Message}"); }
            }

            if (now >= _nextAuditFlush)
            {
                _nextAuditFlush = now + AuditFlushSeconds;
                try { FlushAggregateAudit(); }
                catch (Exception) { }
            }
        }

        // ==================== RPC registration ====================

        // One class, one target method (ZNet.Awake). It registers BOTH halves — the server entry points and
        // the two client executors — exactly like CompanionPlugin.RpcRegistration: the DLL is the same file on
        // both sides and each handler guards its own side (IsServer() / SenderIsServerPeer()).
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave6EconomyRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // --- server side (panel -> server) ---
                    ZRoutedRpc.instance.Register<string>("AP_SrvEcoReq", OnEcoReq);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvEcoAdjust", OnEcoAdjust);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvShopSet", OnShopSet);

                    // --- server side (our own client executors' confirmations) ---
                    ZRoutedRpc.instance.Register<ZPackage>("AP_EcoDeliverRep", OnDeliverRep);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_EcoTakeRep", OnTakeRep);

                    // --- client side (only accepted when the SERVER sent them) ---
                    // Deliberately NOT "AP_GiveItem"/"AP_RemoveItem": those names are owned by CompanionPlugin
                    // and answer nothing, and a shop must know whether the goods actually landed before it
                    // charges. Distinct names, distinct payloads, zero interference with the existing paths.
                    ZRoutedRpc.instance.Register<ZPackage>("AP_EcoDeliver", OnEcoDeliverClient);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_EcoTake", OnEcoTakeClient);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Economy RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. currency ledger ====================

        /// <summary>Current balance of a platform id (0 when unknown). Never throws.</summary>
        internal static long Balance(string id)
        {
            long bal, life; string name;
            ReadEco(CleanId(id), out bal, out life, out name);
            return bal;
        }

        /// <summary>Lifetime earnings (sum of every positive delta ever applied) for a platform id.</summary>
        internal static long LifetimeEarned(string id)
        {
            long bal, life; string name;
            ReadEco(CleanId(id), out bal, out life, out name);
            return life;
        }

        /// <summary>True when the master switch is on. Sibling wave modules check this before offering rewards.</summary>
        internal static bool EconomyEnabled() => EconomyOn;

        /// <summary>
        /// Add (delta &gt; 0) or remove (delta &lt; 0) currency and write ONE audit line. Balances are clamped
        /// to [0, 1e12]; lifetime earnings only ever grow. Returns the new balance. Safe to call from any
        /// wave module (event rewards, quest payouts) — it is the single mutation door.
        /// </summary>
        internal static long Grant(string id, long delta, string reason, long auditSender)
        {
            return Mutate(id, delta, false, reason, auditSender, null, true);
        }

        /// <summary>
        /// Spend currency. Returns false and changes NOTHING when the balance is short — callers must treat
        /// false as "the purchase did not happen".
        /// </summary>
        internal static bool Charge(string id, long amount, string reason, long auditSender)
        {
            if (amount <= 0L) return true;
            var clean = CleanId(id);
            if (clean.Length == 0) return false;
            if (Balance(clean) < amount) return false;
            Mutate(clean, -amount, false, reason, auditSender, null, true);
            return true;
        }

        // The ONE mutation helper. Every path in this file (earnings, penalties, purchases, sales, streaks,
        // ranks, admin adjustments, transfers) funnels through here so clamping, lifetime bookkeeping,
        // persistence and auditing can never be forgotten in one branch and remembered in another.
        //   setAbsolute: treat `delta` as the new balance instead of an offset (admin "set" mode).
        //   knownName:   refresh the cached display name when we happen to know it.
        //   audit:       false for high-frequency sweeps that emit ONE aggregate line instead (see
        //                FlushAggregateAudit) — never false for anything an operator or player triggered.
        private static long Mutate(string id, long delta, bool setAbsolute, string reason, long auditSender,
            string knownName, bool audit)
        {
            var clean = CleanId(id);
            if (clean.Length == 0 || clean == "?") return 0L;

            var table = FeatureStore.Table(TblEco);
            var key = FindKey(table, clean) ?? clean;

            long bal, life; string name;
            ParseEco(table.TryGetValue(key, out var raw) ? raw : null, out bal, out life, out name);
            if (!string.IsNullOrEmpty(knownName)) name = CleanText(knownName);

            var before = bal;
            long target;
            if (setAbsolute) target = delta;
            else if (delta > 0L) target = bal > MaxBalance - delta ? MaxBalance : bal + delta;   // overflow-safe add
            else target = bal + delta;                                                          // delta < 0: no overflow possible

            if (target < 0L) target = 0L;                 // INVARIANT: a balance is never negative
            if (target > MaxBalance) target = MaxBalance;
            var applied = target - before;
            if (applied > 0L)
            {
                life = life > MaxBalance - applied ? MaxBalance : life + applied;   // lifetime counts income only
            }

            table[key] = target.ToString(CultureInfo.InvariantCulture) + "|" +
                         life.ToString(CultureInfo.InvariantCulture) + "|" + name;
            FeatureStore.SaveTable(TblEco);

            if (audit)
                CompanionPlugin.SrvAudit(auditSender, "ECO",
                    $"id={key} delta={applied} balance={target} lifetime={life} reason={CleanText(reason)}");
            return target;
        }

        private static void ReadEco(string id, out long bal, out long life, out string name)
        {
            bal = 0L; life = 0L; name = "";
            if (string.IsNullOrEmpty(id)) return;
            var table = FeatureStore.Table(TblEco);
            var key = FindKey(table, id);
            if (key == null) return;
            ParseEco(table[key], out bal, out life, out name);
        }

        private static void ParseEco(string value, out long bal, out long life, out string name)
        {
            bal = 0L; life = 0L; name = "";
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 3);
            if (parts.Length > 0) long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out bal);
            if (parts.Length > 1) long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out life);
            if (parts.Length > 2) name = parts[2];
            if (bal < 0L) bal = 0L;
            if (life < 0L) life = 0L;
        }

        // ==================== 2. earning ====================

        // Per-minute online earnings. Presence is derived from the peer list on our own schedule (wave 1 owns
        // the "presence" TABLE; this module never calls into wave 1's join tracking). Elapsed time is measured
        // from the real clock and clamped: a 40-second GC hitch or a debugger pause must not pay out 40
        // seconds' worth of currency in one frame... it pays at most MaxAccrualStep.
        private static void AccrueOnline(float now)
        {
            var elapsed = _lastSweepTime <= 0d ? 0d : Math.Max(0d, now - _lastSweepTime);
            if (elapsed > MaxAccrualStep) elapsed = MaxAccrualStep;
            _lastSweepTime = now;

            var peers = ZNet.instance.GetPeers();
            if (peers == null) return;

            var seen = new HashSet<long>();
            var rate = EarnPerMinute;

            foreach (var peer in peers)
            {
                if (peer == null || peer.m_uid == 0L || peer.m_socket == null) continue;
                var host = peer.m_socket.GetHostName();
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(peer.m_playerName)) continue;   // not fully joined yet
                seen.Add(peer.m_uid);

                OnlinePlayer op;
                if (!Online.TryGetValue(peer.m_uid, out op))
                {
                    op = new OnlinePlayer { Id = CleanId(host), Name = CleanText(peer.m_playerName) };
                    Online[peer.m_uid] = op;
                    continue;   // first sighting: start the clock now, pay from the next sweep
                }
                op.Name = CleanText(peer.m_playerName);
                op.SessionSeconds += elapsed;
                if (rate <= 0) continue;   // still track session time (ranks need it), just do not pay

                op.CarrySeconds += elapsed;
                if (op.CarrySeconds < 60d) continue;
                var minutes = (int)(op.CarrySeconds / 60d);
                op.CarrySeconds -= minutes * 60d;

                var amount = (long)minutes * rate;
                if (amount <= 0L) continue;
                // Quiet mutation: the aggregate line in FlushAggregateAudit covers the whole sweep.
                Mutate(op.Id, amount, false, "online time", 0L, op.Name, false);
                _earnPlayers++;
                _earnTotal += amount;
            }

            if (Online.Count == 0) return;
            List<long> gone = null;
            foreach (var kv in Online)
                if (!seen.Contains(kv.Key)) (gone ?? (gone = new List<long>())).Add(kv.Key);
            if (gone == null) return;
            foreach (var uid in gone) Online.Remove(uid);
        }

        // Death penalty. TIER-VANILLA: the death feed is derived from character ZDOs, so unmodded players are
        // penalised too — currency lives on the server, not in their save file.
        private static void OnDeathObserved(Wave34Core.DeathInfo info)
        {
            if (!EconomyOn) return;
            var penalty = DeathPenalty;
            if (penalty <= 0) return;
            var id = CleanId(info.PlatformId);
            if (id.Length == 0 || id == "?") return;

            var before = Balance(id);
            if (before <= 0L) return;   // nothing to take; do not spam the ledger with no-ops
            var after = Mutate(id, -penalty, false, "death penalty", 0L, info.PlayerName, false);
            var taken = before - after;
            if (taken <= 0L) return;

            _penaltyCount++;
            _penaltyTotal += taken;

            var uid = UidOfId(id);
            if (uid != 0L)
                Tell(uid, $"Death penalty: -{taken} {CurrencyLabel()} (balance {after})");
        }

        // One audit line per flush window, never one per player: an earning server with 10 players would
        // otherwise write 14 400 audit rows a day and bury every real admin action.
        private static void FlushAggregateAudit()
        {
            if (_earnPlayers > 0)
            {
                CompanionPlugin.SrvAudit(0L, "ECO-EARN",
                    $"grants={_earnPlayers} total={_earnTotal} perMinute={EarnPerMinute} window={AuditFlushSeconds:0}s");
                _earnPlayers = 0;
                _earnTotal = 0L;
            }
            if (_penaltyCount > 0)
            {
                CompanionPlugin.SrvAudit(0L, "ECO-DEATH-PENALTY",
                    $"deaths={_penaltyCount} total={_penaltyTotal} perDeath={DeathPenalty} window={AuditFlushSeconds:0}s");
                _penaltyCount = 0;
                _penaltyTotal = 0L;
            }
        }

        // ==================== 3. shops ====================

        private static void ParseSku(string value, out string prefab, out int count, out long price, out bool buy)
        {
            prefab = ""; count = 0; price = 0L; buy = true;
            if (string.IsNullOrEmpty(value)) return;
            var parts = value.Split(new[] { '|' }, 4);
            if (parts.Length > 0) prefab = parts[0];
            if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
            if (parts.Length > 2) long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out price);
            if (parts.Length > 3) buy = parts[3] == "1";
            if (count < 0) count = 0;
            if (price < 0L) price = 0L;
        }

        private static List<string> SortedSkus()
        {
            var keys = new List<string>();
            foreach (var kv in FeatureStore.Table(TblShop)) keys.Add(kv.Key);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            return keys;
        }

        private static bool LookupSku(string sku, out string key, out string prefab, out int count, out long price, out bool buy)
        {
            key = null; prefab = ""; count = 0; price = 0L; buy = true;
            if (string.IsNullOrEmpty(sku)) return false;
            var table = FeatureStore.Table(TblShop);
            foreach (var kv in table)
            {
                if (!string.Equals(kv.Key, sku, StringComparison.OrdinalIgnoreCase)) continue;
                key = kv.Key;
                ParseSku(kv.Value, out prefab, out count, out price, out buy);
                return true;
            }
            return false;
        }

        // ---- !bal ----

        private static void OnBalCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;
            var id = SenderId(sender);
            if (id.Length == 0) { Tell(sender, "Your account could not be identified."); return; }
            long bal, life; string name;
            ReadEco(id, out bal, out life, out name);
            var rank = RankOf(id);
            var suffix = rank.Length > 0 ? $" | rank: {rank}" : "";
            Tell(sender, $"Balance: {bal} {CurrencyLabel()} (lifetime {life}){suffix}");
        }

        // ---- !shop [page] ----

        private static void OnShopCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;
            var keys = SortedSkus();
            if (keys.Count == 0) { Tell(sender, "The shop is empty. An admin has not defined any items yet."); return; }

            var pages = (keys.Count + ShopPageSize - 1) / ShopPageSize;
            var page = 1;
            if (!string.IsNullOrEmpty(args))
            {
                var first = args.Trim().Split(' ')[0];
                if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out page)) page = 1;
            }
            page = Mathf.Clamp(page, 1, pages);

            var start = (page - 1) * ShopPageSize;
            var lines = new List<string> { $"Shop (page {page}/{pages}) - {CurrencyLabel()}" };
            var table = FeatureStore.Table(TblShop);
            for (var i = start; i < keys.Count && i < start + ShopPageSize; i++)
            {
                string prefab; int count; long price; bool buy;
                ParseSku(table[keys[i]], out prefab, out count, out price, out buy);
                lines.Add(buy
                    ? $"!buy {keys[i]} - {count}x {prefab} for {price}"
                    : $"!sell {keys[i]} - {count}x {prefab} pays {price}");
            }
            if (pages > 1) lines.Add($"'!shop {(page < pages ? page + 1 : 1)}' for the next page");
            TellList(sender, string.Join("\n", lines.ToArray()));
        }

        // ---- !buy <sku> ----

        private static void OnBuyCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;
            var sku = CleanSku(FirstWord(args));
            if (sku.Length == 0) { Tell(sender, "Usage: !buy <sku>   (see !shop)"); return; }

            string key, prefab; int count; long price; bool buy;
            if (!LookupSku(sku, out key, out prefab, out count, out price, out buy))
            { Tell(sender, $"No such item: {sku}. Type !shop for the list."); return; }
            if (!buy) { Tell(sender, $"{key} is a SELL offer. Use '!sell {key}'."); return; }
            if (count <= 0 || prefab.Length == 0) { Tell(sender, $"{key} is misconfigured; tell an admin."); return; }

            var id = SenderId(sender);
            if (id.Length == 0) { Tell(sender, "Your account could not be identified."); return; }
            if (IsBusy(sender)) return;

            var bal = Balance(id);
            if (bal < price)
            { Tell(sender, $"Not enough {CurrencyLabel()}: {key} costs {price}, you have {bal}."); return; }

            // CAPABILITY GATE — the whole reason this is a refusal and not a queued IOU.
            if (!RequireMod(sender, "Buying items")) return;

            BeginDelivery(sender, id, "buy", key, prefab, count, price);
            Tell(sender, $"Delivering {count}x {prefab}... you are charged only when it arrives.");
        }

        // ---- !sell <sku> ----

        private static void OnSellCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;
            var sku = CleanSku(FirstWord(args));
            if (sku.Length == 0) { Tell(sender, "Usage: !sell <sku>   (see !shop)"); return; }

            string key, prefab; int count; long price; bool buy;
            if (!LookupSku(sku, out key, out prefab, out count, out price, out buy))
            { Tell(sender, $"No such item: {sku}. Type !shop for the list."); return; }
            if (buy) { Tell(sender, $"{key} is a BUY offer. Use '!buy {key}'."); return; }
            if (count <= 0 || prefab.Length == 0) { Tell(sender, $"{key} is misconfigured; tell an admin."); return; }

            var id = SenderId(sender);
            if (id.Length == 0) { Tell(sender, "Your account could not be identified."); return; }
            if (IsBusy(sender)) return;

            // Selling means READING and REMOVING from an inventory that lives in the player's own save file.
            // Same capability gate, same refusal, for exactly the same reason as buying.
            if (!RequireMod(sender, "Selling items")) return;

            BeginTake(sender, id, key, prefab, count, price);
            Tell(sender, $"Checking your inventory for {count}x {prefab}...");
        }

        // ---- !pay <playerName> <amount> ----

        private static void OnPayCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;

            var parts = (args ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { Tell(sender, "Usage: !pay <playerName> <amount>"); return; }
            var amountText = parts[parts.Length - 1];
            var targetName = string.Join(" ", parts, 0, parts.Length - 1).Trim();

            long amount;
            if (!long.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) || amount <= 0L)
            { Tell(sender, "Amount must be a positive whole number."); return; }

            var fromId = SenderId(sender);
            if (fromId.Length == 0) { Tell(sender, "Your account could not be identified."); return; }
            if (IsBusy(sender)) return;

            // Transfers resolve by CONNECTED player name: the server knows a name only for a peer that is
            // online (the ledger stores the last seen name, but names are not unique and paying the wrong
            // stranger is unrecoverable). Ambiguity is refused rather than guessed.
            long targetUid = 0L; string targetId = null, resolvedName = null; var matches = 0;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_socket == null || string.IsNullOrEmpty(peer.m_playerName)) continue;
                if (!peer.m_playerName.Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue;
                matches++;
                targetUid = peer.m_uid;
                targetId = CleanId(peer.m_socket.GetHostName());
                resolvedName = CleanText(peer.m_playerName);
            }
            if (matches == 0) { Tell(sender, $"No connected player named '{CleanText(targetName)}'."); return; }
            if (matches > 1) { Tell(sender, $"More than one player is called '{CleanText(targetName)}'. Ask one of them to rename."); return; }
            if (string.IsNullOrEmpty(targetId)) { Tell(sender, "That player's account could not be identified."); return; }
            if (Wave1Moderation.IdMatches(targetId, fromId)) { Tell(sender, "You cannot pay yourself."); return; }

            var bal = Balance(fromId);
            if (bal < amount) { Tell(sender, $"Not enough {CurrencyLabel()}: you have {bal}."); return; }

            // Two-step confirmation: the first !pay stores the intent for 30 s, an identical repeat commits.
            PayConfirm pc;
            var now = DateTime.UtcNow;
            if (PayConfirms.TryGetValue(sender, out pc) && pc.ExpiresUtc > now &&
                pc.Amount == amount && Wave1Moderation.IdMatches(pc.TargetId, targetId))
            {
                PayConfirms.Remove(sender);
                // Re-check at commit time: the 30 s window is long enough for the balance to have moved.
                if (!Charge(fromId, amount, $"pay to {resolvedName}", 0L))
                { Tell(sender, $"Not enough {CurrencyLabel()} any more - nothing was transferred."); return; }
                var newTarget = Mutate(targetId, amount, false, $"pay from {SenderName(sender)}", 0L, resolvedName, true);

                CompanionPlugin.SrvAudit(0L, "ECO-PAY",
                    $"from={fromId} to={targetId} amount={amount} fromBalance={Balance(fromId)} toBalance={newTarget}");
                Tell(sender, $"Sent {amount} {CurrencyLabel()} to {resolvedName}. Balance: {Balance(fromId)}");
                if (targetUid != 0L) Tell(targetUid, $"{SenderName(sender)} sent you {amount} {CurrencyLabel()}. Balance: {newTarget}");
                return;
            }

            PayConfirms[sender] = new PayConfirm
            {
                TargetId = targetId,
                TargetName = resolvedName,
                Amount = amount,
                ExpiresUtc = now.AddSeconds(PayConfirmSeconds),
            };
            Tell(sender, $"Send {amount} {CurrencyLabel()} to {resolvedName}? Repeat the same command within {(int)PayConfirmSeconds}s to confirm.");
        }

        private static void ExpirePayConfirms()
        {
            if (PayConfirms.Count == 0) return;
            var now = DateTime.UtcNow;
            List<long> dead = null;
            foreach (var kv in PayConfirms)
                if (kv.Value.ExpiresUtc <= now) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var uid in dead) PayConfirms.Remove(uid);
        }

        // ==================== item delivery (the capability-gated half) ====================

        /// <summary>
        /// True when this peer's client is known to run the companion. On anything else the caller must
        /// refuse: false = a vanilla client (items cannot be written to their save file at all), null = the
        /// probe has not answered yet (we nudge it and ask them to retry).
        /// </summary>
        private static bool RequireMod(long uid, string what)
        {
            var has = Wave34Core.HasMod(uid);
            if (has == true) return true;
            try { Wave34Core.ProbePeer(uid); }
            catch (Exception) { }
            // CapReason is deliberately verbose — a player refused by a shop deserves the real reason.
            Tell(uid, $"{what} needs the AdminPanelCompanion mod on your client: {Wave34Core.CapReason(uid)}");
            return false;
        }

        private static bool IsBusy(long uid)
        {
            if (!Busy.Contains(uid)) return false;
            Tell(uid, "One transaction at a time - wait for the current one to finish.");
            return true;
        }

        // Send the goods FIRST; the charge happens in OnDeliverRep and only there.
        private static void BeginDelivery(long uid, string id, string kind, string sku, string prefab, int count, long price)
        {
            var token = _nextToken++;
            var p = new Pending
            {
                Token = token,
                Uid = uid,
                Id = id,
                Name = SenderName(uid),
                Kind = kind,
                Sku = sku ?? "",
                Prefab = prefab,
                Count = Mathf.Clamp(count, 1, MaxStackPerTrade),
                Price = price,
                Expiry = Time.unscaledTime + DeliverTimeout,
            };
            Pendings[token] = p;
            Busy.Add(uid);

            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(token);
            pkg.Write(p.Prefab);
            pkg.Write(p.Count);
            pkg.Write(kind == "lottery" ? "Lottery prize" : "Purchase");
            try { ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_EcoDeliver", pkg); }
            catch (Exception e)
            {
                Pendings.Remove(token);
                Busy.Remove(uid);
                CompanionPlugin.FeatureLog($"Economy delivery send failed for {p.Name}: {e.Message}");
                Tell(uid, "The server could not reach your client. You were not charged.");
            }
        }

        // Ask the client to remove an exact stack; the credit happens in OnTakeRep and only there.
        private static void BeginTake(long uid, string id, string sku, string prefab, int count, long price)
        {
            var token = _nextToken++;
            var p = new Pending
            {
                Token = token,
                Uid = uid,
                Id = id,
                Name = SenderName(uid),
                Kind = "sell",
                Sku = sku ?? "",
                Prefab = prefab,
                Count = Mathf.Clamp(count, 1, MaxStackPerTrade),
                Price = price,
                Expiry = Time.unscaledTime + DeliverTimeout,
            };
            Pendings[token] = p;
            Busy.Add(uid);

            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(token);
            pkg.Write(p.Prefab);
            pkg.Write(p.Count);
            pkg.Write("Sale");
            try { ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_EcoTake", pkg); }
            catch (Exception e)
            {
                Pendings.Remove(token);
                Busy.Remove(uid);
                CompanionPlugin.FeatureLog($"Economy take send failed for {p.Name}: {e.Message}");
                Tell(uid, "The server could not reach your client. Nothing was sold.");
            }
        }

        // ZPackage: int ver, long token, int delivered, string error.
        private static void OnDeliverRep(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int ver, delivered; long token; string error;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                delivered = pkg.ReadInt();
                error = pkg.ReadString() ?? "";
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_EcoDeliverRep: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;   // unknown wire version: discard rather than guess

            Pending p;
            if (!Pendings.TryGetValue(token, out p)) return;          // expired / already settled / unknown
            if (p.Uid != sender) return;                              // sanitized sender: only the buyer may confirm
            // The delivery and take flows share one token table, so a reply must also match the KIND of
            // transaction the token was issued for. Without this a purchase could be settled with a sale
            // confirmation, which credits the price instead of charging it — the player would keep the
            // goods AND gain the money. Checked before the token is consumed so a mismatched packet
            // cannot burn the real pending either; it simply expires normally.
            if (p.Kind == "sell")
            {
                CompanionPlugin.FeatureLog($"Economy: DeliverRep for a '{p.Kind}' token from {p.Name} ignored (wrong reply for this transaction).");
                return;
            }
            Pendings.Remove(token);
            Busy.Remove(p.Uid);

            if (delivered < p.Count)
            {
                // NOT charged. The client-side executor drops to the ground when the bag is full, so a short
                // delivery means the prefab did not resolve (mod/version mismatch) — the player keeps their
                // currency, and the partial case is logged loudly because it means free items.
                CompanionPlugin.FeatureLog(
                    $"Economy: delivery of {p.Count}x {p.Prefab} to {p.Name} reported {delivered} ({(error.Length > 0 ? error : "no reason given")}); NOT charged.");
                if (delivered > 0)
                    CompanionPlugin.SrvAudit(0L, "ECO-DELIVER-PARTIAL",
                        $"id={p.Id} name={p.Name} sku={p.Sku} prefab={p.Prefab} want={p.Count} got={delivered} charged=0");
                Tell(p.Uid, error.Length > 0
                    ? $"Delivery failed ({error}). You were not charged."
                    : "Delivery failed. You were not charged.");
                return;
            }

            // Items are confirmed in the player's hands: NOW the money moves.
            if (!Charge(p.Id, p.Price, p.Kind == "lottery" ? "lottery ticket" : $"buy {p.Sku}", 0L))
            {
                // Only reachable if an admin adjusted the balance downward during the ~20 s flight window
                // (a player cannot spend while busy). The goods are already delivered, so record it loudly
                // instead of pretending it did not happen.
                CompanionPlugin.SrvAudit(0L, "ECO-CHARGE-FAILED",
                    $"id={p.Id} name={p.Name} sku={p.Sku} price={p.Price} balance={Balance(p.Id)} note=items-already-delivered");
                CompanionPlugin.FeatureLog($"Economy: {p.Name} received {p.Count}x {p.Prefab} but the charge of {p.Price} failed (balance changed mid-delivery).");
                Wave1Moderation.NotifyOnlineAdmins($"Economy: {p.Name} got {p.Count}x {p.Prefab} but could not be charged {p.Price} (balance changed mid-delivery).");
                return;
            }

            var bal = Balance(p.Id);
            CompanionPlugin.SrvAudit(0L, p.Kind == "lottery" ? "ECO-LOTTERY" : "ECO-BUY",
                $"id={p.Id} name={p.Name} sku={p.Sku} prefab={p.Prefab} count={p.Count} price={p.Price} balance={bal}");
            Tell(p.Uid, p.Kind == "lottery"
                ? $"You won {p.Count}x {p.Prefab}! -{p.Price} {CurrencyLabel()} (balance {bal})"
                : $"Bought {p.Count}x {p.Prefab} for {p.Price} {CurrencyLabel()} (balance {bal})");
        }

        // ZPackage: int ver, long token, int removed, string error.
        private static void OnTakeRep(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int ver, removed; long token; string error;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                removed = pkg.ReadInt();
                error = pkg.ReadString() ?? "";
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_EcoTakeRep: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            Pending p;
            if (!Pendings.TryGetValue(token, out p)) return;
            if (p.Uid != sender) return;
            // Mirror of the guard in OnDeliverRep: only a token issued for a sale may be settled by a take
            // confirmation. Otherwise answering a buy with this packet pays the player their own price.
            if (p.Kind != "sell")
            {
                CompanionPlugin.FeatureLog($"Economy: TakeRep for a '{p.Kind}' token from {p.Name} ignored (wrong reply for this transaction).");
                return;
            }
            Pendings.Remove(token);
            Busy.Remove(p.Uid);

            if (removed < p.Count)
            {
                Tell(p.Uid, error.Length > 0 ? $"Sale cancelled: {error}" : $"Sale cancelled: you need {p.Count}x {p.Prefab}.");
                return;
            }

            var bal = Mutate(p.Id, p.Price, false, $"sell {p.Sku}", 0L, p.Name, true);
            CompanionPlugin.SrvAudit(0L, "ECO-SELL",
                $"id={p.Id} name={p.Name} sku={p.Sku} prefab={p.Prefab} count={p.Count} paid={p.Price} balance={bal}");
            Tell(p.Uid, $"Sold {p.Count}x {p.Prefab} for {p.Price} {CurrencyLabel()} (balance {bal})");
        }

        // A client that never answers (crash, disconnect, a hacked client suppressing the reply) simply keeps
        // its money and its items: the transaction dissolves and the player is un-busied.
        private static void ExpirePendings(float now)
        {
            List<long> dead = null;
            foreach (var kv in Pendings)
                if (now >= kv.Value.Expiry) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var token in dead)
            {
                var p = Pendings[token];
                Pendings.Remove(token);
                Busy.Remove(p.Uid);
                CompanionPlugin.FeatureLog($"Economy: {p.Kind} of {p.Count}x {p.Prefab} for {p.Name} timed out unconfirmed; nothing was charged or paid.");
                Tell(p.Uid, p.Kind == "sell"
                    ? "Your client did not answer in time. Nothing was sold."
                    : "Your client did not answer in time. You were not charged.");
            }
        }

        // ==================== 4. ranks + playtime rewards ====================

        // "10=Bronze,50=Silver,100=Gold" -> ascending (hours, name). Unparseable entries are skipped, so one
        // typo costs one rank rather than the whole ladder.
        private static List<KeyValuePair<int, string>> ParseThresholds()
        {
            var res = new List<KeyValuePair<int, string>>();
            var raw = _rankThresholds != null ? (_rankThresholds.Value ?? "") : "";
            if (raw.Trim().Length == 0) return res;
            foreach (var chunk in raw.Split(','))
            {
                var part = chunk.Trim();
                if (part.Length == 0) continue;
                var eq = part.IndexOf('=');
                if (eq <= 0 || eq >= part.Length - 1) continue;
                int hours;
                if (!int.TryParse(part.Substring(0, eq).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)) continue;
                if (hours < 0) continue;
                var name = CleanText(part.Substring(eq + 1).Trim());
                if (name.Length == 0) continue;
                res.Add(new KeyValuePair<int, string>(hours, name));
            }
            res.Sort((a, b) => a.Key.CompareTo(b.Key));
            return res;
        }

        private static string RankOf(string id)
        {
            var table = FeatureStore.Table(TblRanks);
            var key = FindKey(table, id);
            if (key == null) return "";
            var parts = table[key].Split(new[] { '|' }, 2);
            return parts.Length > 0 ? parts[0] : "";
        }

        // Total playtime = wave 1's persisted presence seconds (written when a session ENDS) + the seconds we
        // have counted for the session that is running right now. The presence table is read-only here.
        private static double PlaytimeSeconds(string id, OnlinePlayer op)
        {
            double stored = 0d;
            var value = Wave1AuditRpc.LookupById(FeatureStore.Table(TblPresence), id);
            if (!string.IsNullOrEmpty(value))
            {
                var parts = value.Split(new[] { '|' }, 5);
                long seconds;
                if (parts.Length > 3 && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) && seconds > 0L)
                    stored = seconds;
            }
            return stored + (op != null ? op.SessionSeconds : 0d);
        }

        private static void EvaluateRanks()
        {
            var ladder = ParseThresholds();
            if (ladder.Count == 0 || Online.Count == 0) return;

            var table = FeatureStore.Table(TblRanks);
            var reward = RankReward;
            var dirty = false;

            foreach (var kv in Online)
            {
                var op = kv.Value;
                if (op == null || string.IsNullOrEmpty(op.Id)) continue;
                var hours = PlaytimeSeconds(op.Id, op) / 3600d;

                // Highest threshold reached wins: a player who crosses two tiers between sweeps (or joins an
                // existing server with 200 hours) lands on the top one and is rewarded once, not per tier.
                string earned = null;
                var earnedHours = 0;
                for (var i = 0; i < ladder.Count; i++)
                {
                    if (hours + 0.0001d < ladder[i].Key) break;
                    earned = ladder[i].Value;
                    earnedHours = ladder[i].Key;
                }
                if (earned == null) continue;

                var key = FindKey(table, op.Id) ?? op.Id;
                string current = "";
                string raw;
                if (table.TryGetValue(key, out raw))
                {
                    var parts = raw.Split(new[] { '|' }, 2);
                    if (parts.Length > 0) current = parts[0];
                }
                if (string.Equals(current, earned, StringComparison.Ordinal)) continue;   // already at this rank

                table[key] = earned + "|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                dirty = true;

                long balance = 0L;
                if (reward > 0) balance = Mutate(op.Id, reward, false, $"rank {earned}", 0L, op.Name, true);

                CompanionPlugin.SrvAudit(0L, "ECO-RANK",
                    $"id={op.Id} name={op.Name} rank={earned} hours={hours:0.#} threshold={earnedHours} reward={reward} balance={balance}");
                CompanionPlugin.FeatureLog($"Rank up: {op.Name} reached {earned} ({hours:0.#}h){(reward > 0 ? $", +{reward} {CurrencyLabel()}" : "")}");
                Wave1AuditRpc.PostModLog($"RANK {op.Name} reached {earned} ({hours:0.#}h)");

                // Announced to EVERYONE (vanilla-safe ShowMessage, so unmodded clients see it too).
                var line = $"{op.Name} reached rank {earned} ({earnedHours}h played)!";
                try
                {
                    foreach (var peer in ZNet.instance.GetPeers())
                    {
                        if (peer == null || peer.m_uid == 0L) continue;
                        Wave1Moderation.SendPlayerText(peer.m_uid, line);
                    }
                }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Rank announcement failed: {e.Message}"); }

                try
                {
                    Wave34Core.Enqueue("Rank up",
                        $"{op.Name} reached **{earned}** after {hours:0.#} hours" + (reward > 0 ? $" (+{reward} {CurrencyLabel()})" : ""),
                        Wave34Core.ColorInfo);
                }
                catch (Exception) { }
            }

            if (dirty) FeatureStore.SaveTable(TblRanks);
        }

        // ==================== 5. daily streak ====================

        // Day granularity is UTC days since the Unix epoch — one integer, no timezone, no DST, and it compares
        // correctly across a server restart.
        private static long TodayUtc() => (long)(DateTime.UtcNow.Date - new DateTime(1970, 1, 1)).TotalDays;

        private static void OnDailyCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;
            if (!StreakOn) { Tell(sender, "Daily rewards are disabled on this server."); return; }

            var id = SenderId(sender);
            if (id.Length == 0) { Tell(sender, "Your account could not be identified."); return; }

            var table = FeatureStore.Table(TblStreak);
            var key = FindKey(table, id) ?? id;
            long lastDay = 0L, claimedDay = 0L;
            var streak = 0;
            string raw;
            if (table.TryGetValue(key, out raw))
            {
                var parts = raw.Split(new[] { '|' }, 3);
                if (parts.Length > 0) long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out lastDay);
                if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out streak);
                if (parts.Length > 2) long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out claimedDay);
            }

            var today = TodayUtc();
            if (claimedDay == today)
            { Tell(sender, $"You already claimed today. Come back tomorrow (streak: {Math.Max(1, streak)})."); return; }

            // Consecutive if the previous claim was yesterday; any gap (or a first claim) restarts at 1.
            streak = claimedDay == today - 1 && streak > 0 ? streak + 1 : 1;
            var bonus = Math.Min((long)StreakBonus * (streak - 1), StreakCap);
            var amount = StreakDaily + bonus;

            // Row layout "lastDayUtc|streak|claimedDayUtc": lastDay keeps the PREVIOUS claim day (audit trail
            // for a support question "when did my streak break"), claimedDay is today's dedup key.
            table[key] = claimedDay.ToString(CultureInfo.InvariantCulture) + "|" +
                         streak.ToString(CultureInfo.InvariantCulture) + "|" +
                         today.ToString(CultureInfo.InvariantCulture);
            FeatureStore.SaveTable(TblStreak);

            var balance = amount > 0L ? Mutate(id, amount, false, $"daily streak {streak}", 0L, SenderName(sender), true) : Balance(id);
            CompanionPlugin.SrvAudit(0L, "ECO-DAILY",
                $"id={id} name={SenderName(sender)} streak={streak} amount={amount} balance={balance} prevClaimDay={claimedDay}");
            Tell(sender, bonus > 0L
                ? $"Daily claimed: +{amount} {CurrencyLabel()} (day {streak} streak, +{bonus} bonus). Balance: {balance}"
                : $"Daily claimed: +{amount} {CurrencyLabel()} (day {streak}). Balance: {balance}");
        }

        // ==================== 6. lottery / loot crate ====================

        private struct Prize
        {
            public string Prefab;
            public int Count;
            public int Weight;
        }

        // "prefab:count:weight,prefab:count:weight". Malformed entries are skipped, never fatal.
        private static List<Prize> ParsePrizes()
        {
            var res = new List<Prize>();
            var raw = _lotteryPrizes != null ? (_lotteryPrizes.Value ?? "") : "";
            if (raw.Trim().Length == 0) return res;
            foreach (var chunk in raw.Split(','))
            {
                var part = chunk.Trim();
                if (part.Length == 0) continue;
                var bits = part.Split(':');
                if (bits.Length < 2) continue;
                var prefab = CleanPrefab(bits[0]);
                if (prefab.Length == 0) continue;
                int count, weight = 1;
                if (!int.TryParse(bits[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) continue;
                if (bits.Length > 2) int.TryParse(bits[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out weight);
                count = Mathf.Clamp(count, 1, MaxStackPerTrade);
                weight = Mathf.Clamp(weight, 1, 1000000);
                res.Add(new Prize { Prefab = prefab, Count = count, Weight = weight });
            }
            return res;
        }

        private static void OnLotteryCommand(long sender, string args)
        {
            if (!GateCommand(sender)) return;
            if (!LotteryOn) { Tell(sender, "The lottery is disabled on this server."); return; }

            var prizes = ParsePrizes();
            if (prizes.Count == 0) { Tell(sender, "The lottery has no prizes configured. Tell an admin."); return; }

            var id = SenderId(sender);
            if (id.Length == 0) { Tell(sender, "Your account could not be identified."); return; }
            if (IsBusy(sender)) return;

            var price = TicketPrice;
            var bal = Balance(id);
            if (bal < price)
            { Tell(sender, $"A ticket costs {price} {CurrencyLabel()}, you have {bal}."); return; }

            // REFUND-BY-CONSTRUCTION: the ticket price is charged in OnDeliverRep, after the prize is
            // confirmed to be in the player's inventory. Every failure path below (no mod, send failure,
            // timeout, prefab missing on the client) therefore ends with the player's balance untouched —
            // there is no compensating "give it back" write that could itself fail.
            if (!RequireMod(sender, "The lottery")) return;

            var total = 0L;
            foreach (var p in prizes) total += p.Weight;
            if (total <= 0L) { Tell(sender, "The lottery prize table is invalid. Tell an admin."); return; }
            var roll = (long)(Rng.NextDouble() * total);
            if (roll >= total) roll = total - 1;
            var pick = prizes[prizes.Count - 1];
            long acc = 0L;
            foreach (var p in prizes)
            {
                acc += p.Weight;
                if (roll < acc) { pick = p; break; }
            }

            BeginDelivery(sender, id, "lottery", "", pick.Prefab, pick.Count, price);
            Tell(sender, "Rolling the crate... you are charged only if the prize arrives.");
        }

        // ==================== 7. panel RPCs ====================

        // AP_SrvEcoReq(string idOrEmpty) -> AP_EcoData
        //   {int ver, long totalCirculating, int shipped, shipped x (string id, string name, long balance,
        //    long lifetimeEarned), int shopShipped, shopShipped x (string sku, string prefab, int count,
        //    long price, bool buy)}
        // Read-only, so it is gated on plain adminlist membership rather than SenderCanFeature: a moderator
        // must be able to LOOK at the ledger even when tiered roles withhold the ability to change it.
        private static void OnEcoReq(long sender, string idOrEmpty)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.FeatureSenderIsAdmin(sender)) return;

            var filter = CleanId(idOrEmpty);
            var eco = FeatureStore.Table(TblEco);

            long totalCirculating = 0L;
            var rows = new List<KeyValuePair<string, string>>();
            foreach (var kv in eco)
            {
                long bal, life; string name;
                ParseEco(kv.Value, out bal, out life, out name);
                totalCirculating += bal;
                if (filter.Length > 0 && !Wave1Moderation.IdMatches(kv.Key, filter)) continue;
                rows.Add(kv);
            }

            // Richest first when the panel asked for the whole ledger: 60 rows of "the accounts that matter"
            // beats 60 arbitrary ones.
            rows.Sort((a, b) =>
            {
                long ba, la, bb, lb; string na, nb;
                ParseEco(a.Value, out ba, out la, out na);
                ParseEco(b.Value, out bb, out lb, out nb);
                return bb.CompareTo(ba);
            });

            var shipped = Math.Min(rows.Count, EcoShipCap);
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(totalCirculating);
            pkg.Write(shipped);
            for (var i = 0; i < shipped; i++)
            {
                long bal, life; string name;
                ParseEco(rows[i].Value, out bal, out life, out name);
                pkg.Write(rows[i].Key);
                pkg.Write(name ?? "");
                pkg.Write(bal);
                pkg.Write(life);
            }

            var skus = SortedSkus();
            var shopShipped = Math.Min(skus.Count, ShopShipCap);
            var shop = FeatureStore.Table(TblShop);
            pkg.Write(shopShipped);
            for (var i = 0; i < shopShipped; i++)
            {
                string prefab; int count; long price; bool buy;
                ParseSku(shop[skus[i]], out prefab, out count, out price, out buy);
                pkg.Write(skus[i]);
                pkg.Write(prefab);
                pkg.Write(count);
                pkg.Write(price);
                pkg.Write(buy);
            }

            try { CompanionPlugin.ReplyTo(sender, "AP_EcoData", pkg); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_EcoData reply failed: {e.Message}"); }
        }

        // AP_SrvEcoAdjust: ZPackage{string id, long delta, bool set, string reason}. Owner-only (no builtin
        // role grant), audited by the chokepoint AND with the rich line Mutate writes, mirrored to the modlog.
        private static void OnEcoAdjust(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvEcoAdjust")) return;

            string id, reason; long delta; bool set;
            try
            {
                id = CleanId(pkg.ReadString());
                delta = pkg.ReadLong();
                set = pkg.ReadBool();
                reason = CleanText(pkg.ReadString());
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvEcoAdjust: malformed packet dropped ({e.Message})"); return; }
            if (id.Length == 0) { CompanionPlugin.NotifySender(sender, "Economy: no player id given."); return; }
            if (!EconomyOn)
            {
                CompanionPlugin.NotifySender(sender, "Economy: the economy is disabled (set Features.EnableEconomy = true).");
                return;
            }
            if (delta < -MaxBalance) delta = -MaxBalance;
            if (delta > MaxBalance) delta = MaxBalance;
            if (set && delta < 0L) delta = 0L;

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var before = Balance(id);
            var after = Mutate(id, delta, set, reason.Length > 0 ? $"admin: {reason}" : $"admin adjust by {admin}", sender, null, true);

            CompanionPlugin.SrvAudit(sender, "ECO-ADJUST",
                $"id={id} mode={(set ? "set" : "delta")} value={delta} before={before} after={after} reason={reason}");
            CompanionPlugin.FeatureLog($"Economy: {admin} {(set ? "set" : "adjusted")} {id} -> {after} {CurrencyLabel()} ({reason})");
            Wave1AuditRpc.PostModLog($"ECO {admin} {(set ? "set" : "adjusted")} {id}: {before} -> {after} {CurrencyLabel()} ({(reason.Length > 0 ? reason : "no reason")})");
            CompanionPlugin.NotifySender(sender, $"{id}: {before} -> {after} {CurrencyLabel()}");

            var uid = UidOfId(id);
            if (uid != 0L)
                Tell(uid, after >= before
                    ? $"An admin granted you {after - before} {CurrencyLabel()} (balance {after})"
                    : $"An admin removed {before - after} {CurrencyLabel()} (balance {after})");
        }

        // AP_SrvShopSet: ZPackage{string sku, string prefab, int count, long price, bool buy, bool remove}.
        private static void OnShopSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvShopSet")) return;

            string sku, prefab; int count; long price; bool buy, remove;
            try
            {
                sku = CleanSku(pkg.ReadString());
                prefab = CleanPrefab(pkg.ReadString());
                count = pkg.ReadInt();
                price = pkg.ReadLong();
                buy = pkg.ReadBool();
                remove = pkg.ReadBool();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvShopSet: malformed packet dropped ({e.Message})"); return; }
            if (sku.Length == 0) { CompanionPlugin.NotifySender(sender, "Shop: the SKU must be a single word."); return; }

            var table = FeatureStore.Table(TblShop);
            var admin = CompanionPlugin.SenderDisplayName(sender);
            string existing = null;
            foreach (var kv in table)
                if (string.Equals(kv.Key, sku, StringComparison.OrdinalIgnoreCase)) { existing = kv.Key; break; }

            if (remove)
            {
                if (existing == null) { CompanionPlugin.NotifySender(sender, $"Shop: no SKU named {sku}."); return; }
                var was = table[existing];
                table.Remove(existing);
                FeatureStore.SaveTable(TblShop);
                CompanionPlugin.SrvAudit(sender, "SHOP-REMOVE", $"sku={existing} was={was}");
                CompanionPlugin.FeatureLog($"Shop: {admin} removed SKU {existing} ({was})");
                Wave1AuditRpc.PostModLog($"SHOP {admin} removed {existing}");
                CompanionPlugin.NotifySender(sender, $"Removed SKU {existing}.");
                return;
            }

            if (prefab.Length == 0) { CompanionPlugin.NotifySender(sender, "Shop: a prefab name is required."); return; }
            if (existing == null && table.Count >= MaxShopRows)
            { CompanionPlugin.NotifySender(sender, $"Shop: the catalogue is full ({MaxShopRows} SKUs)."); return; }
            count = Mathf.Clamp(count, 1, MaxStackPerTrade);
            if (price < 0L) price = 0L;
            if (price > MaxBalance) price = MaxBalance;

            // Best-effort prefab sanity check. ObjectDB may legitimately be null on a dedicated server, and a
            // modded item may exist only on clients, so a miss is a WARNING to the admin, never a refusal.
            var warn = "";
            try
            {
                if (ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(prefab) == null)
                    warn = " (warning: this server's ObjectDB does not know that prefab - check the spelling)";
            }
            catch (Exception) { }

            var key = existing ?? sku;
            table[key] = prefab + "|" + count.ToString(CultureInfo.InvariantCulture) + "|" +
                         price.ToString(CultureInfo.InvariantCulture) + "|" + (buy ? "1" : "0");
            FeatureStore.SaveTable(TblShop);

            CompanionPlugin.SrvAudit(sender, "SHOP-SET",
                $"sku={key} prefab={prefab} count={count} price={price} mode={(buy ? "buy" : "sell")}");
            CompanionPlugin.FeatureLog($"Shop: {admin} set {key} = {count}x {prefab} @ {price} ({(buy ? "buy" : "sell")}){warn}");
            Wave1AuditRpc.PostModLog($"SHOP {admin} set {key}: {count}x {prefab} @ {price} ({(buy ? "buy" : "sell")})");
            CompanionPlugin.NotifySender(sender, $"SKU {key}: {count}x {prefab} @ {price} {CurrencyLabel()} ({(buy ? "buy" : "sell")}){warn}");
        }

        // ==================== client-side executors ====================
        // These run on the TARGET player's own client (that is the only place an inventory exists) and only
        // for packets the SERVER sent. Both answer, always — the server charges nothing until they do.

        // ZPackage: int ver, long token, string prefab, int count, string label.
        private static void OnEcoDeliverClient(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServerPeer(sender)) return;

            int ver, count; long token; string prefab, label;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                prefab = pkg.ReadString();
                count = pkg.ReadInt();
                label = pkg.ReadString() ?? "";
            }
            catch (Exception) { return; }
            if (ver != Ver) return;

            var delivered = 0;
            var error = "";
            try { delivered = AddToInventory(player, prefab, Mathf.Clamp(count, 0, MaxStackPerTrade), out error); }
            catch (Exception e) { error = "client error: " + e.Message; }

            if (delivered > 0)
            {
                try { player.Message(MessageHud.MessageType.Center, $"{label}: {delivered}x {prefab}"); }
                catch (Exception) { }
            }

            var rep = new ZPackage();
            rep.Write(Ver);
            rep.Write(token);
            rep.Write(delivered);
            rep.Write(error ?? "");
            // Reply to `sender`, which SenderIsServerPeer just proved is the server.
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_EcoDeliverRep", rep); }
            catch (Exception) { }
        }

        // ZPackage: int ver, long token, string prefab, int count, string label.
        // ALL-OR-NOTHING: if the player does not hold the full stack nothing is removed and removed=0 comes
        // back, so a sale can never half-happen.
        private static void OnEcoTakeClient(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServerPeer(sender)) return;

            int ver, count; long token; string prefab, label;
            try
            {
                ver = pkg.ReadInt();
                token = pkg.ReadLong();
                prefab = pkg.ReadString();
                count = pkg.ReadInt();
                label = pkg.ReadString() ?? "";
            }
            catch (Exception) { return; }
            if (ver != Ver) return;
            count = Mathf.Clamp(count, 0, MaxStackPerTrade);

            var removed = 0;
            var error = "";
            try
            {
                var have = CountInInventory(player, prefab);
                if (have < count) error = $"you have {have}x {prefab}, {count} needed";
                else removed = RemoveFromInventory(player, prefab, count);
            }
            catch (Exception e) { error = "client error: " + e.Message; removed = 0; }

            if (removed > 0)
            {
                try { player.Message(MessageHud.MessageType.Center, $"{label}: -{removed}x {prefab}"); }
                catch (Exception) { }
            }

            var rep = new ZPackage();
            rep.Write(Ver);
            rep.Write(token);
            rep.Write(removed);
            rep.Write(error ?? "");
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_EcoTakeRep", rep); }
            catch (Exception) { }
        }

        // Modelled on CompanionPlugin.OnGiveItem (the shipping grant path): ObjectDB lookup, icon guard,
        // stack splitting, and the drop-at-feet fallback for a full inventory — so "delivered" really means
        // the items exist in the player's world, not merely that a packet was received.
        private static int AddToInventory(Player player, string prefabName, int amount, out string error)
        {
            error = "";
            if (amount <= 0) return 0;
            if (string.IsNullOrEmpty(prefabName)) { error = "no prefab"; return 0; }

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null) { error = $"unknown item '{prefabName}' on your client"; return 0; }
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null) { error = $"'{prefabName}' is not an item"; return 0; }
            // Icon-less items (hair, beards, effects) corrupt the inventory grid — never add them.
            if (drop.m_itemData.m_shared.m_icons == null || drop.m_itemData.m_shared.m_icons.Length == 0)
            { error = $"'{prefabName}' cannot be carried"; return 0; }

            var maxStack = drop.m_itemData.m_shared.m_maxStackSize;
            if (maxStack < 1) maxStack = 1;   // a 0 max-stack would loop forever
            var remaining = amount;
            var placed = 0;
            while (remaining > 0)
            {
                var stack = Mathf.Min(remaining, maxStack);
                remaining -= stack;
                var data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = stack;
                data.m_quality = Mathf.Clamp(1, 1, data.m_shared.m_maxQuality);
                data.m_durability = data.GetMaxDurability();
                if (player.GetInventory().AddItem(data)) { placed += stack; continue; }

                // Bag full: drop at the player's feet rather than losing the purchase.
                try
                {
                    var go = UnityEngine.Object.Instantiate(prefab, player.transform.position + Vector3.up, Quaternion.identity);
                    var d = go.GetComponent<ItemDrop>();
                    if (d != null)
                    {
                        d.m_itemData.m_stack = stack;
                        d.m_itemData.m_quality = Mathf.Clamp(1, 1, d.m_itemData.m_shared.m_maxQuality);
                    }
                    placed += stack;
                }
                catch (Exception e) { error = "inventory full and the drop failed: " + e.Message; break; }
            }
            return placed;
        }

        private static int CountInInventory(Player player, string prefabName)
        {
            var inv = player != null ? player.GetInventory() : null;
            if (inv == null || string.IsNullOrEmpty(prefabName)) return 0;
            var total = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item == null) continue;
                var key = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
                if (!string.Equals(key, prefabName, StringComparison.OrdinalIgnoreCase)) continue;
                total += Mathf.Max(0, item.m_stack);
            }
            return total;
        }

        private static int RemoveFromInventory(Player player, string prefabName, int amount)
        {
            var inv = player.GetInventory();
            var removed = 0;
            foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (removed >= amount) break;
                if (item == null) continue;
                var key = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
                if (!string.Equals(key, prefabName, StringComparison.OrdinalIgnoreCase)) continue;
                // An equipped item must be unequipped first or the player keeps a ghost-equipped copy in hand
                // (invisible in the bag, still usable) until they relog.
                if (item.m_equipped) player.UnequipItem(item, false);
                var take = Mathf.Min(item.m_stack, amount - removed);
                inv.RemoveItem(item, take);
                removed += take;
            }
            return removed;
        }

        /// <summary>
        /// Client-side trust gate, identical in effect to CompanionPlugin.SenderIsServer (private, in a file
        /// this wave may not edit). The host branch is gated on the routed-RPC sender sanitizer: without it a
        /// remote client could forge sender == our own session id and drive a client executor (here: adding or
        /// deleting items). If the flag cannot be read we FAIL CLOSED — the executors stop working on a
        /// listen-server host, a visible degradation rather than a hole.
        /// </summary>
        private static bool SenderIsServerPeer(long sender)
        {
            if (ZNet.instance == null) return false;
            try
            {
                var sp = ZNet.instance.GetServerPeer();
                if (sp != null && sp.m_uid != 0L && sender == sp.m_uid) return true;   // real client
            }
            catch (Exception) { }

            if (!ZNet.instance.IsServer() || ZDOMan.instance == null) return false;
            if (!SanitizerActive()) return false;
            return sender == ZDOMan.GetSessionID();                                     // listen-server host
        }

        private static bool SanitizerActive()
        {
            if (!_sanitizerFieldRead)
            {
                _sanitizerFieldRead = true;
                _sanitizerField = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                if (_sanitizerField == null)
                    CompanionPlugin.FeatureLog("Economy: sender-sanitizer flag not found; host-side item delivery disabled (fail closed).");
            }
            try { return _sanitizerField != null && (bool)_sanitizerField.GetValue(null); }
            catch (Exception) { return false; }
        }

        // ==================== shared helpers ====================

        // Every chat command starts here: the master switch is re-checked at call time so flipping
        // EnableEconomy off at runtime stops the economy immediately even though the command stays registered.
        private static bool GateCommand(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
            if (!EconomyOn) { Tell(sender, "The economy is disabled on this server."); return false; }
            if (!FeatureStore.Ready)
            {
                Tell(sender, "The economy store is not ready yet - try again in a moment.");
                return false;
            }
            return true;
        }

        /// <summary>Platform id of a chat sender ("" when unresolvable).</summary>
        private static string SenderId(long sender)
        {
            var id = CompanionPlugin.SenderPlatformId(sender);
            if (string.IsNullOrEmpty(id) || id == "?") return "";
            return CleanId(id);
        }

        private static string SenderName(long sender)
        {
            var n = CompanionPlugin.SenderDisplayName(sender);
            return string.IsNullOrEmpty(n) || n == "?" ? "someone" : CleanText(n);
        }

        // Text to a player. Center-screen ShowMessage, which every VANILLA client registers
        // (MessageHud.cs:111) — economy replies must reach unmodded players too.
        private static void Tell(long uid, string text)
        {
            if (uid == 0L || string.IsNullOrEmpty(text)) return;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "ShowMessage", 2, text); }
            catch (Exception) { }
        }

        // Multi-line output (the shop catalogue) goes top-left instead of center: a 12-line block in the
        // middle of the screen is unreadable while playing.
        private static void TellList(long uid, string text)
        {
            if (uid == 0L || string.IsNullOrEmpty(text)) return;
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "ShowMessage", 1, text); }
            catch (Exception) { }
        }

        private static long UidOfId(string id)
        {
            if (ZNet.instance == null || string.IsNullOrEmpty(id)) return 0L;
            try
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer == null || peer.m_socket == null) continue;
                    if (Wave1Moderation.IdMatches(id, peer.m_socket.GetHostName())) return peer.m_uid;
                }
            }
            catch (Exception) { }
            return 0L;
        }

        // Full "Platform_id" or bare id — either form matches, exactly like the adminlist read path.
        private static string FindKey(Dictionary<string, string> table, string id)
        {
            if (table == null || string.IsNullOrEmpty(id)) return null;
            if (table.ContainsKey(id)) return id;
            foreach (var kv in table)
                if (Wave1Moderation.IdMatches(kv.Key, id)) return kv.Key;
            return null;
        }

        // Ids are stored AS ENTERED (trimmed) — stripping a platform prefix silently breaks crossplay ids.
        private static string CleanId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            if (s.Length > MaxIdLen) s = s.Substring(0, MaxIdLen);
            return s.IndexOf(' ') >= 0 ? "" : s;
        }

        // '|' is the field separator inside stored values, so it can never survive in free text.
        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > MaxTextLen ? s.Substring(0, MaxTextLen) : s;
        }

        // A SKU is a single word: it is a chat-command argument and a table key.
        private static string CleanSku(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().Replace('|', '_').Replace('=', '_');
            if (s.Length > MaxSkuLen) s = s.Substring(0, MaxSkuLen);
            return s.IndexOf(' ') >= 0 ? "" : s;
        }

        private static string CleanPrefab(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().Replace('|', '_').Replace('=', '_');
            if (s.Length > MaxPrefabLen) s = s.Substring(0, MaxPrefabLen);
            return s.IndexOf(' ') >= 0 ? "" : s;
        }

        private static string FirstWord(string args)
        {
            if (string.IsNullOrEmpty(args)) return "";
            var t = args.Trim();
            var sp = t.IndexOf(' ');
            return sp < 0 ? t : t.Substring(0, sp);
        }
    }
}
