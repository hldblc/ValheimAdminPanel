using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #3 boss encounter tuner + fight recorder (server + modded clients) ====================
    // Summon a boss with an HP multiplier (optionally scaled per extra nearby player), a damage multiplier and an
    // enrage timer; record the fight (damage per player, deaths, time to kill) and keep a summary that can be posted
    // to Discord.
    //
    // HOW, given that a boss is simulated by whichever client owns it:
    //  * HP: the server spawns the boss itself and writes ZDO max_health = M and health = M - 1. Character.Awake only
    //    resets max health when health == max (Character.cs:700-703), so the owning client keeps the tuned value.
    //  * Boss damage to players: a hit on a player some OTHER client owns is a routed RPC_Damage that passes through the
    //    server; a prefix on ZRoutedRpc.RPC_RoutedRPC rewrites its HitData (x multiplier). A hit on the boss owner's
    //    own player never leaves that client, so the companion there scales it (victim side, Character.RPC_Damage) when
    //    the attacker carries the ZDO marker "ap_enc_dmg". An unmodded boss owner takes unscaled hits - said in the UI.
    //  * Damage per player: routed hits ON the boss are counted by the same prefix; hits the boss owner's own player
    //    lands locally are reported by its companion (AP_EncDmg, batched). Participants also come from the boss ZDO's
    //    attacker flags (ZDOVars.s_attackers + player name, Character.cs:2285-2291).
    //  * Enrage: after N minutes the multiplier rises and "ap_enc_dmg" is rewritten; everyone is told.
    //  * The end: the boss ZDO disappears; a boss-kill event near it in the last 15 s means killed, otherwise despawned.
    //
    //   AP_SrvEncSummon(ZPackage) { ver, string prefab, Vector3 pos, int stars, float hpMult, float perPlayer,
    //                               float dmgMult, int enrageMin, float enrageMult }
    //   AP_SrvEncReq()            -> AP_EncData { ver, nActive x {int id, string prefab, float elapsed, float hpPct,
    //                               float dmgMult, bool enraged}, nDone x {int id, string prefab, bool killed, float seconds,
    //                               int players, string top, int deaths, string text} }
    //   AP_SrvEncPost(int id)     -> the summary to the Discord feed
    //   AP_EncDmg(ZPackage)       client -> server { ver, ZDOID boss, n x {string name, float dmg} }
    internal static class Wave9Enc
    {
        private const int DoneCap = 10;
        internal static readonly int KeyEnc = "ap_enc".GetStableHashCode();
        internal static readonly int KeyEncDmg = "ap_enc_dmg".GetStableHashCode();
        private static readonly int RpcDamageHash = "RPC_Damage".GetStableHashCode();

        private sealed class Fight
        {
            public int Id;
            public ZDOID Boss;
            public string Prefab = "";
            public Vector3 LastPos;
            public float StartedAt;
            public long StartTicks;
            public float HpMax;
            public float DmgMult = 1f, EnrageMult = 1f;
            public float EnrageAt = -1f;
            public bool Enraged, Killed;
            public float EndedAt;
            public readonly Dictionary<string, float> Damage = new Dictionary<string, float>(StringComparer.Ordinal);
            public readonly HashSet<string> Players = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> Deaths = new List<string>();
            public string Text = "";
        }

        private static readonly List<Fight> Active = new List<Fight>();
        private static readonly List<Fight> Done = new List<Fight>();
        private static readonly Dictionary<ZDOID, Fight> ByBoss = new Dictionary<ZDOID, Fight>();
        private static int _seq;
        private static float _nextTick;
        private static bool _hooked;
        private static Vector3 _lastKillPos;
        private static float _lastKillAt = -100f;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvEncSummon", "builder");
            CompanionPlugin.GrantRole("AP_SrvEncReq", "builder");
            CompanionPlugin.GrantRole("AP_SrvEncReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvEncPost", "moderator");
            Wave9.ApplyPatch("Wave9EncRoutedDamage", typeof(RoutedDamagePatch), "encounter damage multipliers and per-player damage are not applied to routed hits");
            Wave9.ApplyPatch("Wave9EncClientDamage", typeof(ClientDamagePatch), "encounter damage on the boss owner's own player is not scaled / reported by this client");
            Wave9.ApplyPatch("Wave9EncHostDamage", typeof(HostDamagePatch), "on a listen server, hits of a host-simulated encounter boss on other players are not scaled");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register<ZPackage>("AP_SrvEncSummon", OnSummon);
            r.Register("AP_SrvEncReq", new Action<long>(OnReq));
            r.Register<int>("AP_SrvEncPost", OnPost);
            r.Register<ZPackage>("AP_EncDmg", OnEncDmg);
            Active.Clear();
            ByBoss.Clear();
            if (_hooked) return;
            _hooked = true;
            Wave9Prog.BossKilled += k => { _lastKillPos = k.Pos; _lastKillAt = Time.unscaledTime; };
            Wave34Core.OnDeath += d =>
            {
                foreach (var f in Active)
                    if (Wave9.MapDist(d.Pos, f.LastPos) < 200f) f.Deaths.Add(d.PlayerName);
            };
        }

        // ==================== summon ====================

        private static void OnSummon(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvEncSummon")) return;
            string prefab; Vector3 pos; int stars, enrageMin; float hpMult, perPlayer, dmgMult, enrageMult;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                prefab = Wave9.CleanName(pkg.ReadString(), 64);
                pos = pkg.ReadVector3();
                stars = Mathf.Clamp(pkg.ReadInt(), 0, 4);
                hpMult = Mathf.Clamp(pkg.ReadSingle(), 0.1f, 50f);
                perPlayer = Mathf.Clamp(pkg.ReadSingle(), 0f, 5f);
                dmgMult = Mathf.Clamp(pkg.ReadSingle(), 0.1f, 20f);
                enrageMin = Mathf.Clamp(pkg.ReadInt(), 0, 120);
                enrageMult = Mathf.Clamp(pkg.ReadSingle(), 1f, 10f);
            }
            catch (Exception) { return; }
            if (!Wave9.Finite(pos)) return;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null || go.GetComponent<Character>() == null) { Wave9.Tell(sender, $"'{prefab}' is not a creature on this server."); return; }

            var nearby = 0;
            foreach (var p in Wave9.ReadyPeers()) if (Wave9.MapDist(Wave9.PeerPos(p), pos) < 150f) nearby++;
            if (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated() &&
                Wave9.MapDist(Player.m_localPlayer.transform.position, pos) < 150f) nearby++;
            nearby = Math.Max(1, nearby);

            var inst = UnityEngine.Object.Instantiate(go, pos, Quaternion.identity);
            var ch = inst.GetComponent<Character>();
            var nv = inst.GetComponent<ZNetView>();
            var zdo = nv != null ? nv.GetZDO() : null;
            if (ch == null || zdo == null) { Wave9.Tell(sender, "The boss could not be created."); return; }
            if (stars > 0) ch.SetLevel(stars + 1);
            var baseMax = ch.GetMaxHealth();
            var max = Mathf.Max(10f, baseMax * hpMult * (1f + perPlayer * (nearby - 1)));
            zdo.Set(Wave9.KeyMaxHealth, max);
            zdo.Set(Wave9.KeyHealth, max - 1f);   // != max, so the owning client's Awake keeps the tuned value
            zdo.Set(KeyEnc, 1f);
            zdo.Set(KeyEncDmg, dmgMult);

            var f = new Fight
            {
                Id = ++_seq, Boss = zdo.m_uid, Prefab = prefab, LastPos = pos, StartedAt = Time.unscaledTime, StartTicks = DateTime.UtcNow.Ticks,
                HpMax = max, DmgMult = dmgMult, EnrageMult = enrageMult, EnrageAt = enrageMin > 0 ? Time.unscaledTime + enrageMin * 60f : -1f,
            };
            Active.Add(f);
            ByBoss[f.Boss] = f;
            Wave9Undo.NoteSpawn(sender, new List<ZDOID> { zdo.m_uid });
            CompanionPlugin.SrvAudit(sender, "EncounterSummon", $"{prefab} *{stars} hp x{hpMult:0.##} (+{perPlayer:0.##}/player, {nearby} near) = {max:0}, dmg x{dmgMult:0.##}, enrage {enrageMin} min x{enrageMult:0.##}");
            Wave9.Tell(sender, $"Encounter #{f.Id}: {prefab} summoned with {max:0} HP ({nearby} player(s) near), damage x{dmgMult:0.##}" + (enrageMin > 0 ? $", enrage in {enrageMin} min" : "") + ".");
        }

        // ==================== tick: enrage + end detection ====================

        internal static void Tick()
        {
            var now = Time.unscaledTime;
            if (LocalDamage.Count > 0 && now >= _nextReport) ReportSoon();   // modded client: flush the last hits of a fight
            if (!Wave9.IsServer || Active.Count == 0) return;
            if (now < _nextTick) return;
            _nextTick = now + 1f;
            var man = ZDOMan.instance;
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var f = Active[i];
                var zdo = man != null ? man.GetZDO(f.Boss) : null;
                if (zdo != null)
                {
                    f.LastPos = zdo.GetPosition();
                    foreach (var p in Wave9.ReadyPeers())
                        if (zdo.GetBool(ZDOVars.s_attackers.ToString(CultureInfo.InvariantCulture) + (p.m_playerName ?? ""), false)) f.Players.Add(p.m_playerName);
                    if (!f.Enraged && f.EnrageAt > 0f && now >= f.EnrageAt)
                    {
                        f.Enraged = true;
                        f.DmgMult *= f.EnrageMult;
                        var mult = f.DmgMult;
                        zdo.Set(KeyEncDmg, mult);
                        // A client simulates the boss: re-apply until its copy carries the new multiplier.
                        Wave9Reassert.Add(zdo.m_uid, zz => Mathf.Approximately(zz.GetFloat(KeyEncDmg, 0f), mult), zz => zz.Set(KeyEncDmg, mult));
                        Wave9.Announce($"{Wave9.PrefabName(zdo.GetPrefab())} is ENRAGED!");
                    }
                    continue;
                }
                f.Killed = now - _lastKillAt < 15f && Wave9.MapDist(_lastKillPos, f.LastPos) < 250f;
                f.EndedAt = now;
                Finish(f);
                Active.RemoveAt(i);
                ByBoss.Remove(f.Boss);
            }
        }

        private static void Finish(Fight f)
        {
            foreach (var n in f.Damage.Keys) f.Players.Add(n);
            var secs = f.EndedAt - f.StartedAt;
            var sb = new StringBuilder();
            sb.Append(f.Killed ? "Killed" : "Despawned").Append(" after ").Append(Fmt(secs)).Append(" by ").Append(f.Players.Count).Append(" player(s)");
            if (f.Enraged) sb.Append(" (enraged)");
            sb.Append(". HP ").Append(f.HpMax.ToString("0", CultureInfo.InvariantCulture)).Append(", damage x").Append(f.DmgMult.ToString("0.##", CultureInfo.InvariantCulture)).Append('.');
            var top = Top(f, 5);
            if (top.Length > 0) sb.Append(" Damage: ").Append(top).Append('.');
            if (f.Deaths.Count > 0) sb.Append(" Deaths: ").Append(string.Join(", ", f.Deaths.ToArray())).Append('.');
            f.Text = sb.ToString();
            Done.Insert(0, f);
            if (Done.Count > DoneCap) Done.RemoveAt(Done.Count - 1);
            CompanionPlugin.FeatureLog($"Encounter #{f.Id} {f.Prefab}: {f.Text}");
        }

        private static string Fmt(float secs) => ((int)(secs / 60f)).ToString(CultureInfo.InvariantCulture) + "m " + ((int)(secs % 60f)).ToString("00", CultureInfo.InvariantCulture) + "s";

        private static string Top(Fight f, int n)
        {
            var rows = new List<KeyValuePair<string, float>>(f.Damage);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (var i = 0; i < rows.Count && i < n; i++) parts.Add(rows[i].Key + " " + rows[i].Value.ToString("0", CultureInfo.InvariantCulture));
            return string.Join(", ", parts.ToArray());
        }

        // ==================== RPCs ====================

        private static void OnReq(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvEncReq")) return;
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(Active.Count);
            var man = ZDOMan.instance;
            foreach (var f in Active)
            {
                var zdo = man != null ? man.GetZDO(f.Boss) : null;
                var hp = zdo != null ? zdo.GetFloat(Wave9.KeyHealth, f.HpMax) : 0f;
                pkg.Write(f.Id);
                pkg.Write(f.Prefab);
                pkg.Write(Time.unscaledTime - f.StartedAt);
                pkg.Write(f.HpMax > 0f ? Mathf.Clamp01(hp / f.HpMax) : 0f);
                pkg.Write(f.DmgMult);
                pkg.Write(f.Enraged);
            }
            pkg.Write(Done.Count);
            foreach (var f in Done)
            {
                pkg.Write(f.Id);
                pkg.Write(f.Prefab);
                pkg.Write(f.Killed);
                pkg.Write(f.EndedAt - f.StartedAt);
                pkg.Write(f.Players.Count);
                pkg.Write(Top(f, 3));
                pkg.Write(f.Deaths.Count);
                pkg.Write(f.Text);
            }
            Wave9.Reply(sender, "AP_EncData", pkg);
        }

        private static void OnPost(long sender, int id)
        {
            if (!Wave9.Can(sender, "AP_SrvEncPost")) return;
            var f = Done.Find(x => x.Id == id);
            if (f == null) return;
            if (!Wave34Core.EnqueueTest("Boss encounter: " + f.Prefab, f.Text, Wave34Core.ColorBoss))
            { Wave9.Tell(sender, "No Discord webhook is configured on the server."); return; }
            Wave9.Tell(sender, "Encounter summary queued for Discord.");
        }

        private static void OnEncDmg(long sender, ZPackage pkg)
        {
            if (!Wave9.IsServer) return;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                var boss = pkg.ReadZDOID();
                Fight f;
                if (!ByBoss.TryGetValue(boss, out f)) return;
                // Only the boss's current owner reports local hits; anyone else could only inflate numbers.
                var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(boss) : null;
                if (zdo == null || zdo.GetOwner() != sender) return;
                var n = pkg.ReadInt();
                if (n < 0 || n > 32) return;
                // The owner is an ordinary player's client: only names of players actually connected count, and the
                // table stays small whatever it sends.
                var connected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in Wave9.ReadyPeers()) if (!string.IsNullOrEmpty(p.m_playerName)) connected.Add(p.m_playerName);
                if (Player.m_localPlayer != null) connected.Add(Player.m_localPlayer.GetPlayerName());
                for (var i = 0; i < n; i++)
                {
                    var name = Wave9.Clean(pkg.ReadString(), 32);
                    var dmg = pkg.ReadSingle();
                    if (name.Length == 0 || float.IsNaN(dmg) || dmg <= 0f || dmg > 1e6f) continue;
                    if (!connected.Contains(name) || (!f.Damage.ContainsKey(name) && f.Damage.Count >= 64)) continue;
                    float cur;
                    f.Damage.TryGetValue(name, out cur);
                    f.Damage[name] = cur + dmg;
                }
            }
            catch (Exception) { }
        }

        // ==================== server: routed hits ====================

        private static string AttackerName(ZDOID attacker)
        {
            if (attacker.IsNone()) return null;
            foreach (var p in Wave9.ReadyPeers()) if (p.m_characterID == attacker) return p.m_playerName;
            if (Player.m_localPlayer != null && Player.m_localPlayer.GetZDOID() == attacker) return Player.m_localPlayer.GetPlayerName();
            return null;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        [HarmonyPriority(130)]
        private static class RoutedDamagePatch
        {
            private static void Prefix(ref ZPackage pkg)
            {
                if (ByBoss.Count == 0 || pkg == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                int pos;
                try { pos = pkg.GetPos(); } catch (Exception) { return; }
                try
                {
                    pkg.SetPos(36);
                    if (pkg.ReadInt() != RpcDamageHash) { pkg.SetPos(pos); return; }
                    pkg.SetPos(pos);
                    var data = new ZRoutedRpc.RoutedRPCData();
                    data.Deserialize(pkg);
                    pkg.SetPos(pos);
                    var parms = data.m_parameters;
                    parms.SetPos(0);
                    var hit = new HitData();
                    hit.Deserialize(ref parms);

                    Fight onBoss;
                    if (ByBoss.TryGetValue(data.m_targetZDO, out onBoss))
                    {
                        var who = AttackerName(hit.m_attacker);
                        if (who != null)
                        {
                            float cur;
                            onBoss.Damage.TryGetValue(who, out cur);
                            onBoss.Damage[who] = cur + Mathf.Max(0f, hit.GetTotalDamage());
                        }
                        return;   // hits ON the boss are only counted; its HP is already tuned
                    }
                    Fight byBoss;
                    if (!ByBoss.TryGetValue(hit.m_attacker, out byBoss) || Mathf.Approximately(byBoss.DmgMult, 1f)) return;
                    if (!IsPlayerZdo(data.m_targetZDO)) return;   // the tuning is about the fight with players, not its scenery
                    hit.m_damage.Modify(byBoss.DmgMult);
                    var np = new ZPackage();
                    hit.Serialize(ref np);
                    data.m_parameters = np;
                    var outPkg = new ZPackage();
                    data.Serialize(outPkg);
                    outPkg.SetPos(0);
                    pkg = outPkg;
                }
                catch (Exception)
                {
                    try { pkg.SetPos(pos); } catch (Exception) { }
                }
            }
        }

        private static bool IsPlayerZdo(ZDOID id)
        {
            var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            return zdo != null && (Wave9.KindOf(zdo.GetPrefab()) & Wave9.KPlayer) != 0;
        }

        // Listen-server host: a boss the HOST simulates sends its hits on remote players with InvokeRoutedRPC straight to
        // RouteRPC - they never pass the RPC_RoutedRPC rewrite above, and the victim's client only scales hits from its
        // own boss. Scale a clone of the hit here instead; a hit on the host's own player stays local and is scaled by
        // ClientDamagePatch, so it is left alone (never both).
        [HarmonyPatch(typeof(ZRoutedRpc), "InvokeRoutedRPC", new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) })]
        private static class HostDamagePatch
        {
            private static void Prefix(long targetPeerID, ZDOID targetZDO, string methodName, object[] parameters)
            {
                try
                {
                    if (ByBoss.Count == 0 || methodName != "RPC_Damage" || parameters == null || parameters.Length == 0) return;
                    if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNet.instance.IsDedicated()) return;
                    if (targetPeerID == ZDOMan.GetSessionID()) return;
                    var hit = parameters[0] as HitData;
                    if (hit == null) return;
                    Fight byBoss;
                    if (!ByBoss.TryGetValue(hit.m_attacker, out byBoss) || Mathf.Approximately(byBoss.DmgMult, 1f)) return;
                    if (!IsPlayerZdo(targetZDO)) return;
                    var copy = hit.Clone();
                    copy.m_damage.Modify(byBoss.DmgMult);
                    parameters[0] = copy;
                }
                catch (Exception) { }
            }
        }

        // ==================== modded clients: the boss owner's local hits ====================

        private static readonly Dictionary<ZDOID, Dictionary<string, float>> LocalDamage = new Dictionary<ZDOID, Dictionary<string, float>>();
        private static float _nextReport;

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class ClientDamagePatch
        {
            [HarmonyPrefix]
            private static void Prefix(Character __instance, long sender, HitData hit)
            {
                try
                {
                    if (hit == null || __instance == null || sender != ZDOMan.GetSessionID()) return;   // routed hits are the server's job
                    var nv = __instance.GetComponent<ZNetView>();
                    var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                    if (zdo == null || !nv.IsOwner()) return;
                    if (zdo.GetFloat(KeyEnc, 0f) > 0f)
                    {
                        var attacker = hit.GetAttacker() as Player;
                        if (attacker == null) return;
                        Dictionary<string, float> d;
                        if (!LocalDamage.TryGetValue(zdo.m_uid, out d)) LocalDamage[zdo.m_uid] = d = new Dictionary<string, float>(StringComparer.Ordinal);
                        float cur;
                        var name = attacker.GetPlayerName();
                        d.TryGetValue(name, out cur);
                        d[name] = cur + Mathf.Max(0f, hit.GetTotalDamage());
                        ReportSoon();
                        return;
                    }
                    if (__instance != Player.m_localPlayer) return;
                    var a = hit.GetAttacker();
                    var anv = a != null ? a.GetComponent<ZNetView>() : null;
                    var azdo = anv != null && anv.IsValid() ? anv.GetZDO() : null;
                    var mult = azdo != null ? azdo.GetFloat(KeyEncDmg, 0f) : 0f;
                    if (mult > 0f && !Mathf.Approximately(mult, 1f)) hit.m_damage.Modify(mult);
                }
                catch (Exception) { }
            }
        }

        private static void ReportSoon()
        {
            var now = Time.unscaledTime;
            if (now < _nextReport) return;
            _nextReport = now + 2f;
            var stale = new List<ZDOID>();
            foreach (var kv in LocalDamage) if (kv.Value.Count == 0) stale.Add(kv.Key);
            foreach (var k in stale) LocalDamage.Remove(k);
            if (ZRoutedRpc.instance == null) return;
            foreach (var kv in LocalDamage)
            {
                if (kv.Value.Count == 0) continue;
                var pkg = new ZPackage();
                pkg.Write(Wave9.Ver);
                pkg.Write(kv.Key);
                pkg.Write(kv.Value.Count);
                foreach (var d in kv.Value) { pkg.Write(d.Key); pkg.Write(d.Value); }
                try
                {
                    if (Wave9.IsServer) { pkg.SetPos(0); OnEncDmg(ZDOMan.GetSessionID(), pkg); }
                    else ZRoutedRpc.instance.InvokeRoutedRPC("AP_EncDmg", pkg);
                }
                catch (Exception) { }
                kv.Value.Clear();
            }
        }
    }
}
