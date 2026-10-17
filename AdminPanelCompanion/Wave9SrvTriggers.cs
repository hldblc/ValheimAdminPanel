using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #12 trigger rules ("if this, then that", server) ====================
    // Server-side automation that macros (client-side, run by hand) and scheduled events (time only) cannot do.
    //   TRIGGERS  0 join (once per session) · 1 enters a rule zone (param: zone name, empty = any) · 2 boss kill
    //             present (param: boss, empty = any) · 3 dies · 4 chat keyword (param, substring) · 5 time of day
    //             (param "HH:MM" game time, once per in-game day, every online player)
    //   CONDITIONS role (0 anyone, 1 admins, 2 non-admins) · first time only (per player, persisted) · cooldown s
    //   ACTIONS (up to 4, "type~param", {player} {zone} {boss} substituted)
    //             broadcast · message (to the player) · give "prefab:amount" (needs the companion on that client,
    //             like every grant; provenance-stamped) · effect (status effect name, or "forge:<id>") · teleport
    //             "x,y,z" or a warp name · event (random event name, at the player) · discord (feed text)
    // Rules: "w9_trig" (id -> v1|name|on|trig|param|role|first|cooldown|a1|a2|a3|a4). Fired-once marks:
    // "w9_trig_fired" (rule|playerId -> ticks). A rule fires at most 30 times a minute (loop guard).
    //
    //   AP_SrvTrigList()          -> AP_TrigData { ver, n x def, n x long firedCount }
    //   AP_SrvTrigSet(ZPackage)   { ver, int op (0 upsert, 1 delete, 2 toggle), def } -> AP_TrigData
    //   AP_SrvTrigTest(string id) -> runs the actions with the requesting admin as {player}, ignoring conditions
    //   def: string id, string name, bool on, int trig, string param, int role, bool first, int cooldown, int nA, nA x string
    internal static class Wave9Trig
    {
        private const string Tbl = "w9_trig";
        private const string TblFired = "w9_trig_fired";
        private const int MaxRules = 60;
        private const int MaxActions = 4;
        private const int RatePerMinute = 30;

        internal const int TJoin = 0, TZone = 1, TBoss = 2, TDeath = 3, TChat = 4, TTime = 5;

        private sealed class Rule
        {
            public string Id = "", Name = "", Param = "";
            public bool On = true, First;
            public int Trig, Role, Cooldown;
            public List<string> Actions = new List<string>();
            public long Fired;
            public readonly Dictionary<string, float> LastFire = new Dictionary<string, float>(StringComparer.Ordinal);
            public readonly Queue<float> Recent = new Queue<float>();

            public void Write(ZPackage p)
            {
                p.Write(Id); p.Write(Name); p.Write(On); p.Write(Trig); p.Write(Param); p.Write(Role); p.Write(First); p.Write(Cooldown);
                p.Write(Actions.Count);
                foreach (var a in Actions) p.Write(a ?? "");
            }

            public static Rule Read(ZPackage p)
            {
                var r = new Rule { Id = p.ReadString() ?? "", Name = p.ReadString() ?? "", On = p.ReadBool(), Trig = p.ReadInt(), Param = p.ReadString() ?? "",
                                   Role = p.ReadInt(), First = p.ReadBool(), Cooldown = p.ReadInt() };
                var n = p.ReadInt();
                if (n < 0 || n > MaxActions) throw new FormatException("actions");
                for (var i = 0; i < n; i++) r.Actions.Add(p.ReadString() ?? "");
                return r;
            }
        }

        private static readonly List<Rule> Rules = new List<Rule>();
        private static bool _loaded, _hooked;
        private static readonly HashSet<long> JoinedThisSession = new HashSet<long>();
        private static float _nextJoinCheck, _lastDayFraction = -1f;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTrigSet", "builder");
            CompanionPlugin.GrantRole("AP_SrvTrigList", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTrigTest", "builder");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register("AP_SrvTrigList", new Action<long>(OnList));
            r.Register<ZPackage>("AP_SrvTrigSet", OnSet);
            r.Register<string>("AP_SrvTrigTest", OnTest);
            Rules.Clear();
            _loaded = false;
            JoinedThisSession.Clear();
            JoinSeenAt.Clear();
            _lastDayFraction = -1f;
            if (_hooked) return;
            _hooked = true;
            Wave9Rzone.ZoneEntered += (uid, name, zone) => Fire(TZone, uid, name, zone.Name, "{zone}", zone.Name);
            Wave9Prog.BossKilled += kill =>
            {
                foreach (var p in kill.Present) Fire(TBoss, p.m_uid, p.m_playerName, kill.Boss, "{boss}", kill.Boss);
            };
            Wave34Core.OnDeath += d =>
            {
                if (string.IsNullOrEmpty(d.PlatformId) || d.PlatformId == "?") return;   // unresolved id: would match the wrong player
                if (d.PlatformId == "HOST")
                {
                    if (Player.m_localPlayer != null) Fire(TDeath, ZDOMan.GetSessionID(), Player.m_localPlayer.GetPlayerName(), "", null, null);
                    return;
                }
                foreach (var p in Wave9.ReadyPeers())
                    if (Wave9.PeerId(p) == d.PlatformId) { Fire(TDeath, p.m_uid, p.m_playerName, "", null, null); break; }
            };
            Wave1Chat.ChatSeen += (sender, text) =>
            {
                var peer = Wave9.PeerByUid(sender);
                if (peer != null) Fire(TChat, sender, peer.m_playerName, text, null, null);
                else if (Wave9.IsHostUid(sender) && Player.m_localPlayer != null) Fire(TChat, sender, Player.m_localPlayer.GetPlayerName(), text, null, null);
            };
        }

        // ==================== store ====================

        private static void Load()
        {
            if (_loaded || !FeatureStore.Ready) return;
            _loaded = true;
            Rules.Clear();
            foreach (var kv in FeatureStore.Table(Tbl))
            {
                var p = Wave9.Unpack(kv.Value, 12);
                if (p[0] != "v1") continue;
                var r = new Rule
                {
                    Id = kv.Key, Name = p[1], On = p[2] != "0", Trig = Wave9.ParseInt(p[3], 0), Param = p[4],
                    Role = Wave9.ParseInt(p[5], 0), First = p[6] == "1", Cooldown = Wave9.ParseInt(p[7], 0),
                };
                for (var i = 8; i < 12; i++) if (p[i].Length > 0) r.Actions.Add(p[i]);
                Rules.Add(r);
            }
        }

        private static void Save()
        {
            var t = FeatureStore.Table(Tbl);
            t.Clear();
            foreach (var r in Rules)
            {
                var a = new string[4];
                for (var i = 0; i < 4; i++) a[i] = i < r.Actions.Count ? r.Actions[i] : "";
                t[r.Id] = Wave9.Pack("v1", r.Name, r.On ? "1" : "0", Wave9.I(r.Trig), r.Param, Wave9.I(r.Role), r.First ? "1" : "0",
                    Wave9.I(r.Cooldown), a[0], a[1], a[2], a[3]);
            }
            FeatureStore.SaveTable(Tbl);
        }

        // ==================== RPCs ====================

        private static ZPackage ListPkg()
        {
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(Rules.Count);
            foreach (var r in Rules) r.Write(pkg);
            foreach (var r in Rules) pkg.Write(r.Fired);
            return pkg;
        }

        private static void OnList(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvTrigList")) return;
            Load();
            Wave9.Reply(sender, "AP_TrigData", ListPkg());
        }

        private static void OnSet(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvTrigSet")) return;
            Load();
            int op; Rule r;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                op = pkg.ReadInt();
                r = Rule.Read(pkg);
            }
            catch (Exception) { return; }
            r.Id = Wave9.CleanName(r.Id, 12);
            r.Name = Wave9.Clean(r.Name, 40);
            r.Param = Wave9.Clean(r.Param, 80);
            r.Trig = Mathf.Clamp(r.Trig, 0, 5);
            r.Role = Mathf.Clamp(r.Role, 0, 2);
            r.Cooldown = Mathf.Clamp(r.Cooldown, 0, 86400 * 7);
            for (var i = 0; i < r.Actions.Count; i++) r.Actions[i] = Wave9.Clean(r.Actions[i], 200);
            r.Actions.RemoveAll(a => a.Length == 0);
            var existing = Rules.Find(x => x.Id == r.Id);
            string what;
            if (op == 1)
            {
                if (existing == null) return;
                Rules.Remove(existing);
                what = "deleted";
            }
            else if (op == 2)
            {
                if (existing == null) return;
                existing.On = !existing.On;
                what = existing.On ? "enabled" : "disabled";
                r = existing;
            }
            else
            {
                if (r.Name.Length == 0) { Wave9.Tell(sender, "A trigger rule needs a name."); return; }
                if (r.Actions.Count == 0) { Wave9.Tell(sender, "A trigger rule needs at least one action."); return; }
                if (existing == null)
                {
                    if (Rules.Count >= MaxRules) { Wave9.Tell(sender, $"Trigger rule limit reached ({MaxRules})."); return; }
                    r.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                    Rules.Add(r);
                    what = "created";
                }
                else
                {
                    r.Fired = existing.Fired;
                    Rules[Rules.IndexOf(existing)] = r;
                    what = "updated";
                }
            }
            Save();
            CompanionPlugin.SrvAudit(sender, "TriggerRule", $"{what} '{r.Name}' trig={r.Trig} param={r.Param} actions={string.Join(" ; ", r.Actions.ToArray())}");
            Wave9.Tell(sender, $"Trigger rule '{r.Name}' {what}.");
            Wave9.Reply(sender, "AP_TrigData", ListPkg());
        }

        private static void OnTest(long sender, string id)
        {
            if (!Wave9.Can(sender, "AP_SrvTrigTest")) return;
            Load();
            var r = Rules.Find(x => x.Id == id);
            if (r == null) return;
            var peer = Wave9.PeerByUid(sender);
            var name = peer != null ? peer.m_playerName : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "admin");
            var results = Run(r, sender, name, "{zone}", r.Param);
            Wave9.Tell(sender, $"Test of '{r.Name}': {results}");
        }

        // ==================== engine ====================

        private const float JoinHandshakeWait = 15f;   // give/effect need to know whether the client runs the companion
        private static readonly Dictionary<long, float> JoinSeenAt = new Dictionary<long, float>();

        internal static void Tick()
        {
            if (!Wave9.IsServer) return;
            Load();
            var now = Time.unscaledTime;
            if (now < _nextJoinCheck) return;
            _nextJoinCheck = now + 2f;
            foreach (var p in Wave9.ReadyPeers())
            {
                if (p.m_characterID.IsNone() || JoinedThisSession.Contains(p.m_uid)) continue;
                // No rules: still note who is here, so a rule created later does not fire "join" for everyone online.
                if (Rules.Count == 0) { JoinedThisSession.Add(p.m_uid); continue; }
                float seen;
                if (!JoinSeenAt.TryGetValue(p.m_uid, out seen)) JoinSeenAt[p.m_uid] = seen = now;
                if (Wave34Core.HasMod(p.m_uid) == null && now - seen < JoinHandshakeWait) continue;   // handshake still running
                JoinedThisSession.Add(p.m_uid);
                JoinSeenAt.Remove(p.m_uid);
                Fire(TJoin, p.m_uid, p.m_playerName, "", null, null);
            }
            if (Rules.Count > 0) TimeTick();
        }

        private static void TimeTick()
        {
            var env = EnvMan.instance;
            if (env == null) return;
            float frac;
            try { frac = env.GetDayFraction(); } catch (Exception) { return; }
            var prev = _lastDayFraction;
            _lastDayFraction = frac;
            if (prev < 0f) return;
            foreach (var r in Rules)
            {
                if (!r.On || r.Trig != TTime) continue;
                var target = TimeParam(r.Param);
                if (target < 0f) continue;
                var crossed = prev <= frac ? (prev < target && target <= frac) : (target > prev || target <= frac);   // wraps at midnight
                if (!crossed) continue;
                var once = true;
                foreach (var p in Wave9.ReadyPeers())
                {
                    if (p.m_characterID.IsNone()) continue;
                    FireRule(r, p.m_uid, p.m_playerName, null, null, once);
                    once = false;
                }
            }
        }

        private static float TimeParam(string s)
        {
            var parts = (s ?? "").Split(':');
            if (parts.Length != 2) return -1f;
            var h = Wave9.ParseInt(parts[0], -1);
            var m = Wave9.ParseInt(parts[1], -1);
            if (h < 0 || h > 23 || m < 0 || m > 59) return -1f;
            return (h * 60 + m) / 1440f;
        }

        private static void Fire(int trig, long uid, string name, string subject, string token, string tokenValue)
        {
            if (!Wave9.IsServer) return;
            Load();
            foreach (var r in Rules)
            {
                if (!r.On || r.Trig != trig) continue;
                if (!Matches(r, subject)) continue;
                FireRule(r, uid, name, token, tokenValue, true);
            }
        }

        private static bool Matches(Rule r, string subject)
        {
            var p = (r.Param ?? "").Trim();
            switch (r.Trig)
            {
                case TZone:
                case TBoss:
                    return p.Length == 0 || string.Equals(p, subject ?? "", StringComparison.OrdinalIgnoreCase)
                           || (subject ?? "").IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
                case TChat:
                    return p.Length > 0 && (subject ?? "").IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
                default:
                    return true;
            }
        }

        private static void FireRule(Rule r, long uid, string name, string token, string tokenValue, bool globalActions)
        {
            var id = PlayerId(uid);
            if (r.Role == 1 && !CompanionPlugin.FeatureIsAdminId(id)) return;
            if (r.Role == 2 && CompanionPlugin.FeatureIsAdminId(id)) return;
            var now = Time.unscaledTime;
            float last;
            var cooldown = Mathf.Max(r.Cooldown, MinPlayerCooldown(r));
            if (cooldown > 0 && r.LastFire.TryGetValue(id, out last) && now - last < cooldown) return;
            var fired = FeatureStore.Table(TblFired);
            var key = r.Id + "|" + id;
            if (r.First && fired.ContainsKey(key)) return;
            while (r.Recent.Count > 0 && now - r.Recent.Peek() > 60f) r.Recent.Dequeue();
            if (r.Recent.Count >= RatePerMinute) { Wave9.ThrottledLog("trig-rate-" + r.Id, $"Trigger '{r.Name}' hit its {RatePerMinute}/minute limit - further fires dropped."); return; }
            r.Recent.Enqueue(now);
            r.LastFire[id] = now;
            r.Fired++;
            bool anyFailed;
            var result = Run(r, uid, name, token, tokenValue, out anyFailed, globalActions);
            // "First time only" is spent only when every action went through: a welcome kit that could not be sent
            // (handshake not done, player died, item unknown) is tried again next time.
            if (r.First && !anyFailed) { fired[key] = Wave9.I(DateTime.UtcNow.Ticks); FeatureStore.SaveTable(TblFired); }
            CompanionPlugin.SrvAudit(0L, "Trigger", $"'{r.Name}' fired for {name} ({id}): {result}");
        }

        // Rules anyone can set off over and over get a per-player floor whatever the configured cooldown: a chat
        // keyword 10 s, anything that gives items or starts a raid 30 s.
        private static float MinPlayerCooldown(Rule r)
        {
            var floor = r.Trig == TChat ? 10f : 0f;
            foreach (var a in r.Actions)
            {
                var t = (a ?? "").TrimStart().ToLowerInvariant();
                if (t.StartsWith("give", StringComparison.Ordinal) || t.StartsWith("event", StringComparison.Ordinal)) return 30f;
            }
            return floor;
        }

        private static bool Succeeded(string res) =>
            res == "ok" || res == "sent" || res == "queued" || res == "no webhook" || res == "skipped (once)";

        private static string PlayerId(long uid)
        {
            if (Wave9.IsHostUid(uid)) return "HOST";
            return Wave9.PeerId(Wave9.PeerByUid(uid));
        }

        private static string Run(Rule r, long uid, string name, string token, string tokenValue, bool globalActions = true)
        {
            bool anyFailed;
            return Run(r, uid, name, token, tokenValue, out anyFailed, globalActions);
        }

        private static string Run(Rule r, long uid, string name, string token, string tokenValue, out bool anyFailed, bool globalActions = true)
        {
            anyFailed = false;
            var results = new List<string>();
            foreach (var a in r.Actions)
            {
                var sep = a.IndexOf('~');
                var type = (sep > 0 ? a.Substring(0, sep) : a).Trim().ToLowerInvariant();
                var param = sep > 0 ? a.Substring(sep + 1) : "";
                param = param.Replace("{player}", name ?? "?");
                if (token != null) param = param.Replace(token, tokenValue ?? "");
                string res;
                try { res = Do(type, param, uid, globalActions); }
                catch (Exception e) { res = "error " + e.Message; }
                if (!Succeeded(res)) anyFailed = true;
                results.Add(type + ":" + res);
            }
            return string.Join(", ", results.ToArray());
        }

        private static string Do(string type, string param, long uid, bool globalActions)
        {
            switch (type)
            {
                case "broadcast":
                    if (!globalActions) return "skipped (once)";
                    Wave9.Announce(param);
                    return "ok";
                case "message":
                    Wave9.Center(uid, param);
                    return "ok";
                case "discord":
                    if (!globalActions) return "skipped (once)";
                    Wave34Core.Enqueue("Trigger", param, Wave34Core.ColorInfo);
                    return Wave34Core.WebhookConfigured() ? "queued" : "no webhook";
                case "teleport":
                {
                    Vector3 pos; bool adminOnly;
                    var v = param.Split(',');
                    if (v.Length == 3) pos = new Vector3(Wave9.ParseFloat(v[0], 0f), Wave9.ParseFloat(v[1], 0f), Wave9.ParseFloat(v[2], 0f));
                    else if (!Wave6Events.TryGetWarp(param.Trim(), out pos, out adminOnly)) return "unknown warp";
                    return Wave9.TeleportPeer(uid, pos) ? "ok" : "player not reachable";
                }
                case "event":
                {
                    if (!globalActions) return "skipped (once)";
                    var peer = Wave9.PeerByUid(uid);
                    var pos = peer != null ? Wave9.PeerPos(peer) : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
                    if (RandEventSystem.instance == null) return "no event system";
                    RandEventSystem.instance.SetRandomEventByName(param.Trim(), pos);
                    return "ok";
                }
                case "give":
                {
                    var p = param.Split(':');
                    var prefab = Wave9.CleanName(p[0], 64);
                    var amount = p.Length > 1 ? Mathf.Clamp(Wave9.ParseInt(p[1], 1), 1, 1000) : 1;
                    if (prefab.Length == 0 || ObjectDB.instance == null || ObjectDB.instance.GetItemPrefab(prefab) == null) return "unknown item";
                    if (!Wave9.IsHostUid(uid) && Wave34Core.HasMod(uid) != true) return "no companion on that client";
                    Wave9Prov.BeforeGrant(0L, uid, prefab, amount, "trigger");   // serial unused: trigger grants are not journaled
                    ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_GiveItem", prefab, amount, 1, "");
                    return "sent";
                }
                case "effect":
                {
                    var name = param.Trim();
                    if (name.StartsWith("forge:", StringComparison.OrdinalIgnoreCase))
                    {
                        string why;
                        return Wave9Sef.ApplyTo(uid, name.Substring(6), out why) ? "ok" : why;
                    }
                    var hash = name.GetStableHashCode();
                    if (ObjectDB.instance != null && ObjectDB.instance.GetStatusEffect(hash) == null) return "unknown effect";
                    if (Wave9.IsHostUid(uid) && Player.m_localPlayer != null) { Player.m_localPlayer.GetSEMan().AddStatusEffect(hash, true); return "ok"; }
                    if (Wave34Core.HasMod(uid) != true) return "no companion on that client";
                    ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_ApplySE", hash);
                    return "sent";
                }
                default:
                    return "unknown action";
            }
        }
    }
}
