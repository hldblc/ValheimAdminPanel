using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #23 Bounty board (server + client-side kill detector) ====================
    // Admins post bounties; players complete them; the reward is paid from the Wave 6 currency ledger through
    // its ONE mutation door (Wave6Economy.Grant) — there is deliberately no second ledger here.
    //
    //   kind "kill"    — kill N creatures of a prefab       (client-side detection, modded killers only)
    //   kind "boss"    — kill a boss prefab (count usually 1) (same detection; the kind only labels it)
    //   kind "deliver" — hand in N items of a prefab         (!bounty claim <id>, or an admin collects)
    //
    // KILL DETECTION runs on the client that OWNS the dying creature: Character.OnDeath is called from
    // CheckDeath() inside the owner-only branch of Character.FixedUpdate (verified by decompiling), and
    // m_lastHit (protected) is the HitData Damage() stored on that same owner. The postfix reads it through
    // Harmony's ___field injection, keeps only hits whose attacker is a PLAYER, and sends AP_BountyKill with
    // the prefab and the attacker's ZDOID. The server credits the peer whose character has that ZDOID — so a
    // player who lands the last hit on a boss owned by someone else still gets credit, as long as the OWNER
    // runs the companion (the owner vouches; the reporter itself is a sanitized, rate-limited peer).
    // Clients only report prefabs on the active target list (AP_BountyTargets, pushed on change and on
    // join), so a greyling massacre cannot starve the rate limit of the one kill that matters.
    //
    // DELIVER hand-ins reuse Wave 6's item-take mechanism: the SAME client executor (AP_EcoTake, all-or-
    // nothing removal) and the same reply name (AP_EcoTakeRep). Wave 6's tokens count up from 1; ours count
    // DOWN from -1, and a prefix on Wave6Economy.OnTakeRep claims negative tokens before the economy's own
    // handler sees them — so neither module can ever settle the other's transaction.
    //
    // Tables: "bounties"  id -> "kind|target|count|reward|createdTicks|createdBy|status|winner"
    //         "bountyprog" "bountyId|platformId" -> "count|name"
    // Server config EnableBounties defaults OFF (it announces to players and moves currency).
    internal static class Wave8SystemsBounty
    {
        private const int Ver = 1;
        private const int EcoVer = 1;               // Wave6Economy's wire version for AP_EcoTake/AP_EcoTakeRep (private there)
        private const string TblBounties = "bounties";
        private const string TblProgress = "bountyprog";
        private const string SeqKey = "_seq";       // counter row inside "bounties"; readers skip keys starting with '_'
        private const int ShipCap = 100;            // wire contract: rows <= 100
        private const int MaxActive = 50;           // open bounties at once
        private const int MaxStored = 150;          // open + finished rows kept; oldest finished pruned past this
        private const int MaxProgressRows = 2000;
        private const int MaxTargetLen = 64;
        private const int MaxNameLen = 60;
        private const int MaxCount = 10000;
        private const long MaxReward = 1000000000L;
        private const int TargetsCap = 50;          // AP_BountyTargets list cap
        private const float TakeTimeout = 20f;      // same window Wave 6 gives its own takes
        private const float TickSeconds = 5f;
        private const int KillBurst = 5;            // per-sender token bucket: 5 reports, then 1/s
        private const float KillRefillPerSecond = 1f;
        private const int ListLines = 8;            // !bounties output cap (top-left HUD)

        private static ConfigEntry<bool> _enableCfg;
        private static bool BountiesOn => _enableCfg != null && _enableCfg.Value;

        private sealed class Bounty
        {
            public string Id;
            public string Kind;
            public string Target;
            public int Count;
            public long Reward;
            public long CreatedTicks;
            public string CreatedBy;
            public string Status;   // active | closed | done
            public string Winner;
        }

        // ---- kill-report rate limit (server) ----
        private sealed class Bucket { public float Tokens = KillBurst; public float Last; }
        private static readonly Dictionary<long, Bucket> Buckets = new Dictionary<long, Bucket>();

        // ---- deliver hand-ins in flight (server); negative tokens, see header ----
        private sealed class PendingTake
        {
            public long Token;
            public long Uid;
            public string Id;
            public string Name;
            public string BountyId;
            public string Prefab;
            public int Count;
            public float Expiry;
            public long Requester;   // admin who triggered the collect (0 = the player)
        }
        private static readonly Dictionary<long, PendingTake> Takes = new Dictionary<long, PendingTake>();
        private static long _nextTake = -1L;
        private static bool _takePatchOk;

        private static bool _progressDirty;
        private static float _nextTick;
        private static bool _inited;

        // ---- client state: prefabs worth reporting (cleared on disconnect) ----
        private static readonly HashSet<string> ClientTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _clientTargetsSet;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                _enableCfg = cfg.Bind("Features", "EnableBounties", false,
                    "Bounty board: admins post kill / boss / deliver bounties, players complete them (!bounties, !bounty claim <id>) and are paid from the currency ledger. OFF by default: it announces to players and moves currency. Kill tracking needs AdminPanelCompanion.dll on the killer's client.");

            CompanionPlugin.RegisterAuditedRpc("AP_SrvBountySet", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBountyAction", "builder");

            Wave8Systems.ApplyPatch("Wave8SystemsCharacterDeathPatch", typeof(Wave8SystemsCharacterDeathPatch),
                "kill bounties cannot be tracked from this client");
            _takePatchOk = true;
            try { Harmony.CreateAndPatchAll(typeof(Wave8SystemsEcoTakeRepPatch)); }
            catch (Exception e)
            {
                _takePatchOk = false;
                CompanionPlugin.FeatureLog($"Wave8SystemsEcoTakeRepPatch failed (deliver bounties cannot be handed in): {e.Message}");
            }
            try { Wave2Ops.ReportPatch("Wave8SystemsEcoTakeRepPatch", _takePatchOk); }
            catch (Exception) { }

            try
            {
                Wave1Chat.RegisterChatCommand("bounties", OnBountiesCommand);
                Wave1Chat.RegisterChatCommand("bounty", OnBountyCommand);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Bounty chat commands unavailable: {e.Message}"); }
        }

        internal static void Tick()
        {
            if (!_inited) return;
            if (ZNet.instance == null)
            {
                // CLIENT side: the target list belongs to the server just left.
                if (_clientTargetsSet) { ClientTargets.Clear(); _clientTargetsSet = false; }
                return;
            }
            if (!ZNet.instance.IsServer()) return;
            var now = Time.unscaledTime;
            if (now < _nextTick) return;
            _nextTick = now + TickSeconds;

            ExpireTakes(now);
            if (_progressDirty)
            {
                _progressDirty = false;
                try { FeatureStore.SaveTable(TblProgress); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Bounty progress save failed: {e.Message}"); }
            }
            if (Buckets.Count > 0)
            {
                List<long> gone = null;
                foreach (var kv in Buckets)
                    if (!Wave8Systems.PeerConnected(kv.Key) && !Wave8Systems.IsHostSession(kv.Key))
                        (gone ?? (gone = new List<long>())).Add(kv.Key);
                if (gone != null) foreach (var uid in gone) Buckets.Remove(uid);
            }
        }

        internal static void OnPeerReady(long uid)
        {
            if (!BountiesOn) return;
            SendTargets(uid);
        }

        internal static void OnPeerLeft(long uid)
        {
            Buckets.Remove(uid);
            List<long> dead = null;
            foreach (var kv in Takes)
                if (kv.Value.Uid == uid) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null) foreach (var t in dead) Takes.Remove(t);
        }

        // ==================== table access ====================

        private static Bounty Parse(string id, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var p = raw.Split('|');
            if (p.Length < 7) return null;
            return new Bounty
            {
                Id = id,
                Kind = p[0],
                Target = p[1],
                Count = Mathf.Clamp(Wave8Systems.ParseInt(p[2], 1), 1, MaxCount),
                Reward = Math.Max(0L, Math.Min(MaxReward, Wave8Systems.ParseLong(p[3], 0L))),
                CreatedTicks = Wave8Systems.ParseLong(p[4], 0L),
                CreatedBy = p[5],
                Status = p[6],
                Winner = p.Length > 7 ? p[7] : "",
            };
        }

        private static string Serialize(Bounty b) =>
            b.Kind + "|" + b.Target + "|" + b.Count.ToString(CultureInfo.InvariantCulture) + "|" +
            b.Reward.ToString(CultureInfo.InvariantCulture) + "|" + b.CreatedTicks.ToString(CultureInfo.InvariantCulture) + "|" +
            Wave8Systems.CleanText(b.CreatedBy, MaxNameLen) + "|" + b.Status + "|" + Wave8Systems.CleanText(b.Winner, MaxNameLen);

        // Newest first. Ids are numeric strings, so "newest" is the largest id.
        private static List<Bounty> LoadAll()
        {
            var res = new List<Bounty>();
            try
            {
                foreach (var kv in FeatureStore.Table(TblBounties))
                {
                    if (kv.Key.Length == 0 || kv.Key[0] == '_') continue;
                    var b = Parse(kv.Key, kv.Value);
                    if (b != null) res.Add(b);
                }
            }
            catch (Exception) { }
            res.Sort((a, b) => Wave8Systems.ParseLong(b.Id, 0L).CompareTo(Wave8Systems.ParseLong(a.Id, 0L)));
            return res;
        }

        private static Bounty Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                string raw;
                return FeatureStore.Table(TblBounties).TryGetValue(id, out raw) ? Parse(id, raw) : null;
            }
            catch (Exception) { return null; }
        }

        private static void Save(Bounty b)
        {
            var t = FeatureStore.Table(TblBounties);
            t[b.Id] = Serialize(b);
            FeatureStore.SaveTable(TblBounties);
        }

        private static string NextId()
        {
            var t = FeatureStore.Table(TblBounties);
            string raw;
            var seq = t.TryGetValue(SeqKey, out raw) ? Wave8Systems.ParseLong(raw, 0L) : 0L;
            // A hand-edited table may hold ids past the counter: never reuse one.
            foreach (var kv in t)
            {
                if (kv.Key.Length == 0 || kv.Key[0] == '_') continue;
                var v = Wave8Systems.ParseLong(kv.Key, 0L);
                if (v > seq) seq = v;
            }
            seq++;
            t[SeqKey] = seq.ToString(CultureInfo.InvariantCulture);
            return seq.ToString(CultureInfo.InvariantCulture);
        }

        // Makes room for one more row: finished bounties go first, oldest first; open bounties are never
        // pruned here (MaxActive caps those separately).
        private static void PruneStored()
        {
            var all = LoadAll();   // newest first
            var excess = all.Count - MaxStored + 1;
            if (excess <= 0) return;
            var t = FeatureStore.Table(TblBounties);
            for (var i = all.Count - 1; i >= 0 && excess > 0; i--)
            {
                if (all[i].Status == "active") continue;
                t.Remove(all[i].Id);
                RemoveProgress(all[i].Id);
                excess--;
            }
        }

        private static string ProgressKey(string bountyId, string platformId) => bountyId + "|" + platformId;

        private static int ProgressOf(string bountyId, string platformId, out string name)
        {
            name = "";
            try
            {
                var t = FeatureStore.Table(TblProgress);
                string raw;
                // Ids may be stored in either spelling; scan when the exact key misses.
                if (!t.TryGetValue(ProgressKey(bountyId, platformId), out raw))
                {
                    raw = null;
                    foreach (var kv in t)
                    {
                        var bar = kv.Key.IndexOf('|');
                        if (bar <= 0 || kv.Key.Substring(0, bar) != bountyId) continue;
                        if (Wave1Moderation.IdMatches(kv.Key.Substring(bar + 1), platformId)) { raw = kv.Value; break; }
                    }
                }
                if (raw == null) return 0;
                var p = raw.Split(new[] { '|' }, 2);
                if (p.Length > 1) name = p[1];
                return Mathf.Clamp(Wave8Systems.ParseInt(p[0], 0), 0, MaxCount);
            }
            catch (Exception) { return 0; }
        }

        private static int AddProgress(string bountyId, string platformId, string name, int delta)
        {
            var t = FeatureStore.Table(TblProgress);
            string prevName;
            var cur = ProgressOf(bountyId, platformId, out prevName);
            var key = ProgressKey(bountyId, platformId);
            if (!t.ContainsKey(key) && t.Count >= MaxProgressRows) return cur;   // full: progress freezes rather than the file growing without bound
            cur = Mathf.Clamp(cur + delta, 0, MaxCount);
            t[key] = cur.ToString(CultureInfo.InvariantCulture) + "|" + Wave8Systems.CleanText(string.IsNullOrEmpty(name) ? prevName : name, MaxNameLen);
            _progressDirty = true;
            return cur;
        }

        private static void RemoveProgress(string bountyId)
        {
            try
            {
                var t = FeatureStore.Table(TblProgress);
                List<string> dead = null;
                foreach (var kv in t)
                {
                    var bar = kv.Key.IndexOf('|');
                    if (bar > 0 && kv.Key.Substring(0, bar) == bountyId) (dead ?? (dead = new List<string>())).Add(kv.Key);
                }
                if (dead == null) return;
                foreach (var k in dead) t.Remove(k);
                _progressDirty = true;
            }
            catch (Exception) { }
        }

        // "Name 4, Name 2, Name 1" — the three furthest along.
        private static string TopProgress(string bountyId)
        {
            var rows = new List<KeyValuePair<int, string>>();
            try
            {
                foreach (var kv in FeatureStore.Table(TblProgress))
                {
                    var bar = kv.Key.IndexOf('|');
                    if (bar <= 0 || kv.Key.Substring(0, bar) != bountyId) continue;
                    var p = (kv.Value ?? "").Split(new[] { '|' }, 2);
                    var n = Wave8Systems.ParseInt(p[0], 0);
                    var name = p.Length > 1 && p[1].Length > 0 ? p[1] : kv.Key.Substring(bar + 1);
                    rows.Add(new KeyValuePair<int, string>(n, name));
                }
            }
            catch (Exception) { }
            if (rows.Count == 0) return "";
            rows.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new StringBuilder();
            for (var i = 0; i < rows.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Wave8Systems.Clamp(rows[i].Value, 24)).Append(' ').Append(rows[i].Key.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Describe(Bounty b)
        {
            switch (b.Kind)
            {
                case "deliver": return $"deliver {b.Count}x {b.Target}";
                case "boss": return b.Count > 1 ? $"defeat {b.Count}x {b.Target}" : $"defeat {b.Target}";
                default: return $"kill {b.Count}x {b.Target}";
            }
        }

        private static string RewardText(Bounty b) =>
            b.Reward > 0L ? $"{b.Reward} {Wave6Economy.CurrencyLabel()}" : "no currency reward";

        // ==================== completion (the only place a reward is paid) ====================

        private static void Complete(Bounty b, string winnerId, string winnerName, long winnerUid)
        {
            b.Status = "done";
            b.Winner = string.IsNullOrEmpty(winnerName) ? winnerId : winnerName;
            try { Save(b); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Bounty #{b.Id}: could not persist completion: {e.Message}"); }

            long balance = -1L;
            if (b.Reward > 0L && winnerId != "?" && winnerId.Length > 0)
            {
                try { balance = Wave6Economy.Grant(winnerId, b.Reward, $"bounty #{b.Id} {Describe(b)}", 0L); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Bounty #{b.Id}: ledger credit failed: {e.Message}"); }
            }
            CompanionPlugin.SrvAudit(0L, "BOUNTY-DONE", $"id={b.Id} {Describe(b)} winner={winnerId} name={winnerName} reward={b.Reward} balance={balance}");
            CompanionPlugin.FeatureLog($"Bounty #{b.Id} ({Describe(b)}) completed by {winnerName} ({winnerId}); reward {b.Reward}, balance {balance}");
            Wave8Systems.AnnounceAll($"Bounty #{b.Id} completed by {winnerName}: {Describe(b)} - {RewardText(b)}.");
            if (winnerUid != 0L && b.Reward > 0L)
                Wave8Systems.Tell(winnerUid, $"Bounty #{b.Id} complete! +{b.Reward} {Wave6Economy.CurrencyLabel()} (balance {balance}).");
            BroadcastTargets();
        }

        // ==================== kill reports (client -> server) ====================

        // CLIENT side. Runs after Character.OnDeath on the client that owns the creature; the ZDO is already
        // detached by then (ZNetScene.Destroy), so the prefab comes from the GameObject name (Unity's Destroy
        // is deferred to the end of the frame, so the object is still readable here).
        [HarmonyPatch(typeof(Character), "OnDeath")]
        internal static class Wave8SystemsCharacterDeathPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, HitData ___m_lastHit)
            {
                try
                {
                    if (!_clientTargetsSet || ClientTargets.Count == 0) return;
                    if (__instance == null || __instance.IsPlayer()) return;
                    if (Player.m_localPlayer == null || ZNet.instance == null) return;
                    if (___m_lastHit == null || ___m_lastHit.m_attacker.IsNone()) return;

                    var prefab = Utils.GetPrefabName(__instance.gameObject);
                    if (string.IsNullOrEmpty(prefab) || !ClientTargets.Contains(prefab)) return;

                    // Only a PLAYER's last hit counts; fire, falls and other creatures never hold bounties.
                    var attackerGo = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(___m_lastHit.m_attacker) : null;
                    var attacker = attackerGo != null ? attackerGo.GetComponent<Character>() : null;
                    if (attacker == null || !attacker.IsPlayer()) return;

                    var target = Wave8Systems.ServerTargetId();
                    if (target == 0L) return;
                    var pkg = new ZPackage();
                    pkg.Write(Ver);
                    pkg.Write(prefab);
                    pkg.Write(___m_lastHit.m_attacker);
                    ZRoutedRpc.instance?.InvokeRoutedRPC(target, "AP_BountyKill", pkg);
                }
                catch (Exception) { /* a bounty report must never break a creature death */ }
            }
        }

        private static bool RateOk(long sender)
        {
            var now = Time.unscaledTime;
            Bucket b;
            if (!Buckets.TryGetValue(sender, out b)) { b = new Bucket { Last = now }; Buckets[sender] = b; }
            b.Tokens = Mathf.Min(KillBurst, b.Tokens + (now - b.Last) * KillRefillPerSecond);
            b.Last = now;
            if (b.Tokens < 1f) return false;
            b.Tokens -= 1f;
            return true;
        }

        // SERVER side. The sender is a sanitized peer uid (or our own session id on a listen server). The
        // CONTENT is client-authored: length-capped, prefab matched against the stored bounties only, and the
        // credited identity resolved from the peer list — never from the packet.
        internal static void OnBountyKill(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!BountiesOn) return;
            var host = Wave8Systems.IsHostSession(sender);
            if (!host && !Wave8Systems.PeerConnected(sender)) return;
            if (!RateOk(sender)) return;

            int ver; string prefab; ZDOID attacker;
            try
            {
                ver = pkg.ReadInt();
                prefab = Wave8Systems.CleanText(pkg.ReadString(), MaxTargetLen);
                attacker = pkg.ReadZDOID();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_BountyKill: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver || prefab.Length == 0 || attacker.IsNone()) return;

            // Who gets the credit: the connected peer whose character is the attacker, or the host's own
            // player on a listen server. Anything else (a player who already left, a forged id) is dropped.
            string creditId = null, creditName = null;
            long creditUid = 0L;
            var peer = Wave8Systems.PeerOfCharacter(attacker);
            if (peer != null && peer.m_socket != null)
            {
                creditId = Wave8Systems.CleanId(peer.m_socket.GetHostName(), MaxTargetLen);
                creditName = Wave8Systems.CleanText(peer.m_playerName, MaxNameLen);
                creditUid = peer.m_uid;
            }
            else if (Player.m_localPlayer != null && Player.m_localPlayer.GetZDOID() == attacker && ZDOMan.instance != null)
            {
                creditId = "HOST";
                creditName = Wave8Systems.CleanText(Player.m_localPlayer.GetPlayerName(), MaxNameLen);
                creditUid = ZDOMan.GetSessionID();
            }
            if (string.IsNullOrEmpty(creditId)) return;
            if (!FeatureStore.Ready) return;

            foreach (var b in LoadAll())
            {
                if (b.Status != "active") continue;
                if (b.Kind != "kill" && b.Kind != "boss") continue;
                if (!string.Equals(b.Target, prefab, StringComparison.OrdinalIgnoreCase)) continue;
                var n = AddProgress(b.Id, creditId, creditName, 1);
                if (n >= b.Count) Complete(b, creditId, creditName, creditUid);
                else if (creditUid != 0L) Wave8Systems.Tell(creditUid, $"Bounty #{b.Id}: {n}/{b.Count} {b.Target}.", false);
            }
        }

        // ==================== target list (server -> modded clients) ====================

        private static ZPackage TargetsPackage()
        {
            var set = new List<string>();
            if (BountiesOn)
            {
                foreach (var b in LoadAll())
                {
                    if (b.Status != "active" || (b.Kind != "kill" && b.Kind != "boss")) continue;
                    if (set.Count >= TargetsCap) break;
                    var dup = false;
                    foreach (var s in set) if (string.Equals(s, b.Target, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) set.Add(b.Target);
                }
            }
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(set.Count);
            foreach (var s in set) pkg.Write(s);
            return pkg;
        }

        private static void SendTargets(long uid)
        {
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, "AP_BountyTargets", TargetsPackage()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_BountyTargets to {uid} failed: {e.Message}"); }
        }

        private static void BroadcastTargets()
        {
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "AP_BountyTargets", TargetsPackage()); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_BountyTargets broadcast failed: {e.Message}"); }
        }

        // CLIENT side executor: replace the whole set each time (a full list is sent every time).
        internal static void OnTargetsExecutor(long sender, ZPackage pkg)
        {
            try
            {
                if (!Wave8Systems.SenderIsTrustedServer(sender)) return;
                if (pkg.ReadInt() != Ver) return;
                var n = pkg.ReadInt();
                if (n < 0 || n > TargetsCap) return;
                var fresh = new List<string>(n);
                for (var i = 0; i < n; i++) fresh.Add(Wave8Systems.Clamp(pkg.ReadString(), MaxTargetLen));
                ClientTargets.Clear();
                foreach (var s in fresh) if (s.Length > 0) ClientTargets.Add(s);
                _clientTargetsSet = true;
            }
            catch (Exception) { /* malformed executor payload: keep the previous list */ }
        }

        // ==================== deliver hand-ins (Wave 6 take executor, our tokens) ====================

        // Ask the player's client to remove the exact stack. Credit happens in OnTakeReply and only there.
        private static void BeginClaim(long uid, string bountyId, long requester)
        {
            var b = Find(bountyId);
            var who = requester != 0L ? requester : uid;
            if (b == null || b.Status != "active") { Reply(who, requester != 0L, $"Bounty #{bountyId} is not open."); return; }
            if (b.Kind != "deliver") { Reply(who, requester != 0L, $"Bounty #{bountyId} is a {b.Kind} bounty - kills are tracked automatically, there is nothing to hand in."); return; }
            if (!_takePatchOk) { Reply(who, requester != 0L, "Hand-ins are unavailable on this server build (the item-take hook did not apply)."); return; }

            var host = Wave8Systems.IsHostSession(uid);
            var peer = host ? null : ZNet.instance.GetPeer(uid);
            if (!host && peer == null) { Reply(who, requester != 0L, "That player is not connected."); return; }
            if (!host && Wave34Core.HasMod(uid) != true)
            {
                // Character data lives on the player's client; without the companion there is nothing to ask.
                if (requester != 0L) CompanionPlugin.NotifySender(requester, "Cannot collect: " + Wave34Core.CapReason(uid));
                Wave8Systems.Tell(uid, "Hand-ins need AdminPanelCompanion.dll on your client - the server cannot see your inventory without it.");
                return;
            }
            foreach (var kv in Takes)
                if (kv.Value.Uid == uid) { Reply(who, requester != 0L, "A hand-in is already in progress for that player."); return; }

            var id = host ? "HOST" : Wave8Systems.CleanId(peer.m_socket != null ? peer.m_socket.GetHostName() : "", MaxTargetLen);
            var name = host
                ? Wave8Systems.CleanText(Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "HOST", MaxNameLen)
                : Wave8Systems.CleanText(peer.m_playerName, MaxNameLen);
            if (id.Length == 0) { Reply(who, requester != 0L, "That player's id could not be resolved."); return; }

            var token = _nextTake--;
            Takes[token] = new PendingTake
            {
                Token = token, Uid = uid, Id = id, Name = name, BountyId = b.Id, Prefab = b.Target, Count = b.Count,
                Expiry = Time.unscaledTime + TakeTimeout, Requester = requester,
            };

            // Wave 6's AP_EcoTake wire shape: int ver, long token, string prefab, int count, string label.
            var pkg = new ZPackage();
            pkg.Write(EcoVer);
            pkg.Write(token);
            pkg.Write(b.Target);
            pkg.Write(b.Count);
            pkg.Write("Bounty #" + b.Id);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_EcoTake", pkg); }
            catch (Exception e)
            {
                Takes.Remove(token);
                CompanionPlugin.FeatureLog($"Bounty take send failed for {name}: {e.Message}");
                Reply(who, requester != 0L, "The server could not reach that client. Nothing was taken.");
                return;
            }
            if (requester != 0L) CompanionPlugin.NotifySender(requester, $"Asked {name}'s client for {b.Count}x {b.Target} (bounty #{b.Id})...");
        }

        private static void Reply(long uid, bool toAdmin, string text)
        {
            if (toAdmin) CompanionPlugin.NotifySender(uid, text);
            else Wave8Systems.Tell(uid, text);
        }

        // Claimed by the prefix below for negative tokens. removed < count = nothing was taken (all-or-nothing).
        internal static void OnTakeReply(long sender, int ver, long token, int removed, string error)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            PendingTake p;
            if (!Takes.TryGetValue(token, out p)) return;          // expired / unknown
            if (p.Uid != sender) return;                           // sanitized sender: only that client may confirm
            Takes.Remove(token);
            if (ver != EcoVer) return;

            var b = Find(p.BountyId);
            if (removed < p.Count)
            {
                var why = string.IsNullOrEmpty(error) ? $"you need {p.Count}x {p.Prefab}" : error;
                Wave8Systems.Tell(p.Uid, $"Bounty #{p.BountyId}: hand-in cancelled - {why}.");
                if (p.Requester != 0L) CompanionPlugin.NotifySender(p.Requester, $"Collect from {p.Name} failed: {why}.");
                return;
            }
            if (b == null || b.Status != "active")
            {
                // Closed while the request was in flight: the items are gone from the player, so say so
                // loudly rather than pretend nothing happened (the admin can give them back).
                CompanionPlugin.SrvAudit(0L, "BOUNTY-TAKE-ORPHANED", $"id={p.BountyId} player={p.Id} name={p.Name} prefab={p.Prefab} count={p.Count}");
                Wave1Moderation.NotifyOnlineAdmins($"Bounty #{p.BountyId} was closed while {p.Name} handed in {p.Count}x {p.Prefab}; the items were removed - consider giving them back.");
                Wave8Systems.Tell(p.Uid, $"Bounty #{p.BountyId} was closed meanwhile; an admin has been told.");
                return;
            }
            AddProgress(b.Id, p.Id, p.Name, p.Count);
            CompanionPlugin.SrvAudit(0L, "BOUNTY-DELIVER", $"id={b.Id} player={p.Id} name={p.Name} prefab={p.Prefab} count={p.Count} by={(p.Requester != 0L ? "admin" : "player")}");
            if (p.Requester != 0L) CompanionPlugin.NotifySender(p.Requester, $"Collected {p.Count}x {p.Prefab} from {p.Name}.");
            Complete(b, p.Id, p.Name, p.Uid);
        }

        private static void ExpireTakes(float now)
        {
            if (Takes.Count == 0) return;
            List<long> dead = null;
            foreach (var kv in Takes)
                if (now >= kv.Value.Expiry) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var token in dead)
            {
                var p = Takes[token];
                Takes.Remove(token);
                CompanionPlugin.FeatureLog($"Bounty #{p.BountyId}: hand-in by {p.Name} timed out unconfirmed; nothing was taken or paid.");
                Wave8Systems.Tell(p.Uid, "Your client did not answer in time. Nothing was handed in.");
                if (p.Requester != 0L) CompanionPlugin.NotifySender(p.Requester, $"{p.Name}'s client did not answer the collect request in time.");
            }
        }

        // Prefix on Wave 6's AP_EcoTakeRep handler: negative tokens are ours. The packet position is restored
        // on every path that lets the original run, so the economy reads exactly what it would have read.
        [HarmonyPatch]
        internal static class Wave8SystemsEcoTakeRepPatch
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(Wave6Economy), "OnTakeRep", new[] { typeof(long), typeof(ZPackage) });

            [HarmonyPrefix]
            private static bool Prefix(long sender, ZPackage pkg)
            {
                if (pkg == null) return true;
                int pos;
                try { pos = pkg.GetPos(); }
                catch (Exception) { return true; }
                try
                {
                    var ver = pkg.ReadInt();
                    var token = pkg.ReadLong();
                    if (token >= 0L) { pkg.SetPos(pos); return true; }   // Wave 6's own transaction
                    var removed = pkg.ReadInt();
                    var error = pkg.ReadString() ?? "";
                    OnTakeReply(sender, ver, token, removed, error);
                    return false;
                }
                catch (Exception)
                {
                    try { pkg.SetPos(pos); } catch (Exception) { }
                    return true;
                }
            }
        }

        // ==================== chat commands (work for unmodded players) ====================

        private static void OnBountiesCommand(long sender, string args)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!BountiesOn) { Wave8Systems.Tell(sender, "Bounties are disabled on this server."); return; }
            if (!FeatureStore.Ready) { Wave8Systems.Tell(sender, "The bounty board is not ready yet - try again in a moment."); return; }
            var id = CompanionPlugin.SenderPlatformId(sender);
            var sb = new StringBuilder();
            var lines = 0;
            foreach (var b in LoadAll())
            {
                if (b.Status != "active") continue;
                if (lines >= ListLines) { sb.Append("\n..."); break; }
                string n;
                var mine = ProgressOf(b.Id, id, out n);
                if (lines > 0) sb.Append('\n');
                sb.Append('#').Append(b.Id).Append(' ').Append(Describe(b)).Append(" - ").Append(RewardText(b));
                if (b.Kind != "deliver") sb.Append(" (you: ").Append(mine).Append('/').Append(b.Count).Append(')');
                lines++;
            }
            if (lines == 0) { Wave8Systems.Tell(sender, "No open bounties right now."); return; }
            sb.Append("\nHand in items with !bounty claim <id>");
            Wave8Systems.Tell(sender, sb.ToString(), false);
        }

        private static void OnBountyCommand(long sender, string args)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!BountiesOn) { Wave8Systems.Tell(sender, "Bounties are disabled on this server."); return; }
            var a = (args ?? "").Trim();
            if (a.StartsWith("claim", StringComparison.OrdinalIgnoreCase))
            {
                var rest = a.Length > 5 ? a.Substring(5).Trim() : "";
                var id = Wave8Systems.ParseLong(rest, -1L);
                if (id <= 0L) { Wave8Systems.Tell(sender, "Usage: !bounty claim <id>  (ids from !bounties)"); return; }
                BeginClaim(sender, id.ToString(CultureInfo.InvariantCulture), 0L);
                return;
            }
            Wave8Systems.Tell(sender, "Bounties: !bounties lists them, !bounty claim <id> hands in items for a deliver bounty.");
        }

        // ==================== admin RPCs ====================

        // Read (not audited): anyone who may create bounties may read the board.
        internal static void OnBountyReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBountyReq") &&
                !CompanionPlugin.SenderCanFeature(sender, "AP_SrvBountySet")) return;
            SendData(sender);
        }

        // ZPackage: int ver, int kind (0 kill, 1 boss, 2 deliver), string target, int count, long reward.
        internal static void OnBountySet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBountySet")) return;
            int ver, kind, count; string target; long reward;
            try
            {
                ver = pkg.ReadInt();
                kind = pkg.ReadInt();
                target = pkg.ReadString();
                count = pkg.ReadInt();
                reward = pkg.ReadLong();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvBountySet: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (!BountiesOn) { CompanionPlugin.NotifySender(sender, "Bounties are disabled: set Features.EnableBounties = true in the companion config."); return; }
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Bounty store not ready."); return; }

            target = Wave8Systems.CleanId(target, MaxTargetLen);
            if (kind < 0 || kind > 2 || target.Length == 0) { CompanionPlugin.NotifySender(sender, "Bounty rejected: invalid kind or prefab."); return; }
            count = Mathf.Clamp(count, 1, MaxCount);
            reward = Math.Max(0L, Math.Min(MaxReward, reward));

            // Verify the prefab on the server (prefab assets exist on a dedicated server) and store its exact
            // spelling: the kill detector compares GameObject prefab names.
            string canonical;
            var verified = VerifyTarget(kind, target, out canonical);
            if (verified == false) { CompanionPlugin.NotifySender(sender, $"Bounty rejected: '{target}' is not a {(kind == 2 ? "known item" : "known creature")} prefab on this server."); return; }
            if (canonical != null) target = canonical;

            var active = 0;
            foreach (var b0 in LoadAll()) if (b0.Status == "active") active++;
            if (active >= MaxActive) { CompanionPlugin.NotifySender(sender, $"Bounty rejected: {MaxActive} bounties are already open. Close some first."); return; }

            var b = new Bounty
            {
                Id = NextId(),
                Kind = kind == 2 ? "deliver" : kind == 1 ? "boss" : "kill",
                Target = target,
                Count = count,
                Reward = reward,
                CreatedTicks = DateTime.UtcNow.Ticks,
                CreatedBy = Wave8Systems.CleanText(CompanionPlugin.SenderDisplayName(sender), MaxNameLen),
                Status = "active",
                Winner = "",
            };
            PruneStored();
            Save(b);
            CompanionPlugin.SrvAudit(sender, "BOUNTY-CREATE", $"id={b.Id} kind={b.Kind} target={b.Target} count={b.Count} reward={b.Reward} verified={verified}");
            CompanionPlugin.FeatureLog($"Bounty #{b.Id} created by {Wave1AuditRpc.AdminLabel(sender)}: {Describe(b)}, reward {b.Reward}");
            CompanionPlugin.NotifySender(sender, verified == null
                ? $"Bounty #{b.Id} posted (prefab could not be verified on this server - check the spelling)."
                : $"Bounty #{b.Id} posted.");
            Wave8Systems.AnnounceAll($"New bounty #{b.Id}: {Describe(b)} - {RewardText(b)}. Type !bounties.");
            BroadcastTargets();
            SendData(sender);
        }

        // true = known prefab, false = definitely unknown, null = cannot verify (no prefab database on this side).
        private static bool? VerifyTarget(int kind, string target, out string canonical)
        {
            canonical = null;
            try
            {
                if (kind == 2)
                {
                    if (ObjectDB.instance == null) return null;
                    var item = ObjectDB.instance.GetItemPrefab(target);
                    if (item == null || item.GetComponent<ItemDrop>() == null) return false;
                    canonical = item.name;
                    return true;
                }
                if (ZNetScene.instance == null) return null;
                var go = ZNetScene.instance.GetPrefab(target);
                if (go == null || go.GetComponent<Character>() == null) return false;
                canonical = go.name;
                return true;
            }
            catch (Exception) { return null; }
        }

        // ZPackage: int ver, string id, int op (0 close, 1 delete, 2 collect), long targetUid (op 2 only).
        internal static void OnBountyAction(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBountyAction")) return;
            int ver, op; string id; long targetUid = 0L;
            try
            {
                ver = pkg.ReadInt();
                id = pkg.ReadString();
                op = pkg.ReadInt();
                if (op == 2) targetUid = pkg.ReadLong();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvBountyAction: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Bounty store not ready."); return; }
            id = Wave8Systems.CleanId(id, 20);
            var b = Find(id);
            if (b == null) { CompanionPlugin.NotifySender(sender, $"Bounty #{id} does not exist."); return; }

            switch (op)
            {
                case 0:
                    if (b.Status != "active") { CompanionPlugin.NotifySender(sender, $"Bounty #{id} is already {b.Status}."); return; }
                    b.Status = "closed";
                    Save(b);
                    CompanionPlugin.SrvAudit(sender, "BOUNTY-CLOSE", $"id={id} {Describe(b)}");
                    CompanionPlugin.NotifySender(sender, $"Bounty #{id} closed (no reward paid).");
                    Wave8Systems.AnnounceAll($"Bounty #{id} ({Describe(b)}) was closed by an admin.");
                    BroadcastTargets();
                    break;
                case 1:
                    try
                    {
                        FeatureStore.Table(TblBounties).Remove(id);
                        FeatureStore.SaveTable(TblBounties);
                        RemoveProgress(id);
                    }
                    catch (Exception e) { CompanionPlugin.FeatureLog($"Bounty delete failed: {e.Message}"); }
                    CompanionPlugin.SrvAudit(sender, "BOUNTY-DELETE", $"id={id} {Describe(b)} status={b.Status}");
                    CompanionPlugin.NotifySender(sender, $"Bounty #{id} deleted.");
                    if (b.Status == "active") BroadcastTargets();
                    break;
                case 2:
                    if (targetUid == 0L) targetUid = sender;   // "collect from me" on the admin's own client
                    CompanionPlugin.SrvAudit(sender, "BOUNTY-COLLECT", $"id={id} targetUid={targetUid}");
                    BeginClaim(targetUid, id, sender);
                    break;
                default:
                    return;
            }
            SendData(sender);
        }

        // Reply shape (v1): int ver, bool enabled, string currency, bool deliverOk, int rows(<=100) x
        // (string id, string kind, string target, int count, long reward, long createdTicks, string createdBy,
        //  string status, string winner, string topProgress). Open bounties first, newest first.
        private static void SendData(long sender)
        {
            var all = LoadAll();
            var ordered = new List<Bounty>(all.Count);
            foreach (var b in all) if (b.Status == "active") ordered.Add(b);
            foreach (var b in all) if (b.Status != "active") ordered.Add(b);

            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(BountiesOn);
            pkg.Write(Wave6Economy.CurrencyLabel());
            pkg.Write(_takePatchOk);
            var n = ordered.Count < ShipCap ? ordered.Count : ShipCap;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var b = ordered[i];
                pkg.Write(b.Id);
                pkg.Write(b.Kind);
                pkg.Write(b.Target);
                pkg.Write(b.Count);
                pkg.Write(b.Reward);
                pkg.Write(b.CreatedTicks);
                pkg.Write(b.CreatedBy ?? "");
                pkg.Write(b.Status ?? "");
                pkg.Write(b.Winner ?? "");
                pkg.Write(Wave8Systems.Clamp(TopProgress(b.Id), 120));
            }
            CompanionPlugin.ReplyTo(sender, "AP_BountyData", pkg);
        }
    }
}
