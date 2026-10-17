using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #9 progression timeline + anomaly flags (server) ====================
    // Per player (platform id), what the SERVER can observe without trusting the client:
    //   * first entry into each biome - WorldGenerator.GetBiome(character ZDO position), sampled every 5 s;
    //   * first time seen EQUIPPED with gear of each material tier - the character ZDO's VisEquipment ints
    //     (RightItem, ChestItem, ... = item prefab name hashes, VisEquipment.cs:463-478), tier = the highest tier
    //     material in the item's recipe (Bronze 2, Iron 3, Silver 4, BlackMetal 5, Eitr/Carapace 6, Flametal 7).
    //     Crafting itself happens on the client and is invisible to the server, so "first craft" becomes "first
    //     seen wearing/wielding" - which is what a duped or spawned item betrays anyway;
    //   * each boss kill the player was present for - every kill sends the routed "SetGlobalKey" defeat key to the
    //     server (Character.OnDeath, Character.cs:2986-2989; ZoneSystem.RPC_SetGlobalKey ignores repeats but runs),
    //     and "present" = within 150 m of the killing client (the boss's ZDO owner).
    // Anomaly flags (Wave7Guard.ProgressionFlag: same table/alerts as the guard, never escalates):
    //   R1 tier >= 4 gear first seen within 30 min of the player's first join; R2 present at a boss kill while the
    //   previous boss in the vanilla order was never defeated anywhere in this world (a key carried in from
    //   elsewhere; being carried by friends is normal and not flagged); R3 first entry into Mistlands / Ashlands /
    //   Deep North within 15 min of first join.
    // A "defeated_*" key counts as a kill only when it is corroborated: vanilla accepts SetGlobalKey from ANY client,
    // so the sender must be a connected player (or the host) with a live object of that boss within 150 m on the
    // server (Character.OnDeath sends the key before ZNetScene.Destroy, and the destroy only ships on the next
    // ZDOMan.Update, so a real kill always finds it). Keys the dedicated server sets itself (the panel's global-key
    // tool) are not kills. One event per sender and key per 30 s.
    //
    //   AP_SrvProgList()          -> AP_ProgList { ver, n x { id, name, firstTicks, maxTier, bosses, flags, online } }
    //   AP_SrvProgReq(string id)  -> AP_ProgData { ver, id, name, firstTicks, nB x {int biome, long t},
    //                                nT x {int tier, long t, string item}, nK x {string boss, long t}, nF x {long t, string text} }
    //
    // The boss-kill hook is shared: BossKilled fires for the encounter recorder (#3) and the trigger engine (#12).
    internal static class Wave9Prog
    {
        private const string Tbl = "w9_prog";
        private const float SamplePeriod = 5f;
        private const float SavePeriod = 30f;
        private const float PresentRadius = 150f;
        private const int FlagCap = 20;
        private const int ListCap = 200;

        internal static readonly string[] BossOrder = { "eikthyr", "gdking", "bonemass", "dragon", "goblinking", "queen", "fader" };

        private static readonly int[] EquipKeys =
        {
            "RightItem".GetStableHashCode(), "LeftItem".GetStableHashCode(), "ChestItem".GetStableHashCode(),
            "LegItem".GetStableHashCode(), "HelmetItem".GetStableHashCode(), "ShoulderItem".GetStableHashCode(),
            "UtilityItem".GetStableHashCode(), "TrinketItem".GetStableHashCode(),
        };

        // ==================== shared boss-kill event ====================

        internal sealed class BossKill
        {
            public string Key;              // "defeated_bonemass"
            public string Boss;             // "bonemass"
            public long KillerUid;          // the client that owned (simulated) the boss
            public Vector3 Pos;             // that client's position
            public List<ZNetPeer> Present = new List<ZNetPeer>();
            public long Ticks;
        }

        internal static event Action<BossKill> BossKilled;

        [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
        private static class SetGlobalKeyWatch
        {
            [HarmonyPrefix]
            private static void Prefix(long sender, string name)
            {
                try
                {
                    if (!Wave9.IsServer || string.IsNullOrEmpty(name) || !name.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase)) return;
                    OnBossKey(sender, name);
                }
                catch (Exception e) { Wave9.ThrottledLog("prog-boss", $"Boss-kill watch failed: {e.Message}"); }
            }
        }

        private static readonly Dictionary<string, HashSet<int>> BossPrefabs = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static object _bossScene;
        private static readonly Dictionary<string, float> LastKeyEvent = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<ZDO> BossScratch = new List<ZDO>();

        // Every character prefab whose death sets this key (vanilla bosses and modded ones alike).
        private static HashSet<int> PrefabsSetting(string key)
        {
            var scene = ZNetScene.instance;
            if (!ReferenceEquals(scene, _bossScene)) { BossPrefabs.Clear(); _bossScene = scene; }
            HashSet<int> set;
            if (BossPrefabs.TryGetValue(key, out set)) return set;
            set = new HashSet<int>();
            if (scene != null)
                foreach (var go in scene.m_prefabs)
                {
                    if (go == null) continue;
                    var c = go.GetComponent<Character>();
                    if (c != null && string.Equals(c.m_defeatSetGlobalKey, key, StringComparison.OrdinalIgnoreCase)) set.Add(go.name.GetStableHashCode());
                }
            BossPrefabs[key] = set;
            return set;
        }

        private static bool BossNear(string key, Vector3 pos)
        {
            var prefabs = PrefabsSetting(key);
            if (prefabs.Count == 0) return false;
            Wave9.ZdosInRadius(pos, PresentRadius, BossScratch);
            var found = BossScratch.Exists(z => prefabs.Contains(z.GetPrefab()));
            BossScratch.Clear();
            return found;
        }

        private static void OnBossKey(long sender, string key)
        {
            var boss = key.Substring("defeated_".Length).ToLowerInvariant();
            if (Array.IndexOf(BossOrder, boss) < 0) return;   // vanilla bosses only (custom keys are not a progression step)
            var killer = Wave9.PeerByUid(sender);
            var host = killer == null && Wave9.IsHostUid(sender) && ZNet.instance != null && !ZNet.instance.IsDedicated();
            if (killer == null && !host) return;   // the server itself (panel global-key tool) or nobody we know
            var rateKey = sender + "|" + boss;
            float last;
            if (LastKeyEvent.TryGetValue(rateKey, out last) && Time.unscaledTime - last < 30f) return;
            var kill = new BossKill { Key = key, Boss = boss, KillerUid = sender, Ticks = DateTime.UtcNow.Ticks };
            if (killer != null) kill.Pos = Wave9.PeerPos(killer);
            else if (Player.m_localPlayer != null) kill.Pos = Player.m_localPlayer.transform.position;
            if (!BossNear(key, kill.Pos))
            {
                Wave9.ThrottledLog("prog-forged", $"Ignored {key} from {(killer != null ? killer.m_playerName : "host")}: no {boss} within {PresentRadius:0} m of them (not a kill)");
                return;
            }
            LastKeyEvent[rateKey] = Time.unscaledTime;
            foreach (var p in Wave9.ReadyPeers())
                if (p.m_uid == sender || Wave9.MapDist(Wave9.PeerPos(p), kill.Pos) <= PresentRadius) kill.Present.Add(p);

            foreach (var p in kill.Present) RecordBoss(Wave9.PeerId(p), p.m_playerName, p.m_uid, kill.Boss, kill.Ticks, Wave9.PeerPos(p));
            if (Wave9.IsHostUid(sender) || (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated() &&
                                            Wave9.MapDist(Player.m_localPlayer.transform.position, kill.Pos) <= PresentRadius))
                RecordBoss("HOST", Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "host", ZDOMan.GetSessionID(), kill.Boss, kill.Ticks, kill.Pos);

            var h = BossKilled;
            if (h == null) return;
            foreach (var d in h.GetInvocationList())
            {
                try { ((Action<BossKill>)d)(kill); }
                catch (Exception e) { CompanionPlugin.FeatureLog($"Boss-kill subscriber failed: {e.Message}"); }
            }
        }

        // ==================== records ====================

        private sealed class Rec
        {
            public string Name = "";
            public long First;
            public readonly Dictionary<int, long> Biomes = new Dictionary<int, long>();
            public readonly Dictionary<int, KeyValuePair<long, string>> Tiers = new Dictionary<int, KeyValuePair<long, string>>();
            public readonly Dictionary<string, long> Bosses = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly List<KeyValuePair<long, string>> Flags = new List<KeyValuePair<long, string>>();
        }

        private static readonly Dictionary<string, Rec> Cache = new Dictionary<string, Rec>(StringComparer.Ordinal);
        private static bool _dirty;
        private static float _nextSample, _nextSave;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvProgReq", "moderator");
            CompanionPlugin.GrantRole("AP_SrvProgList", "moderator");
            Wave9.ApplyPatch("Wave9ProgBossWatch", typeof(SetGlobalKeyWatch), "boss kills are not recorded (progression, encounter summaries, boss triggers)");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register("AP_SrvProgList", new Action<long>(OnList));
            r.Register<string>("AP_SrvProgReq", OnReq);
        }

        internal static void Tick()
        {
            if (!Wave9.IsServer || !FeatureStore.Ready) return;
            var now = Time.unscaledTime;
            if (now >= _nextSample)
            {
                _nextSample = now + SamplePeriod;
                Sample();
            }
            if (_dirty && now >= _nextSave)
            {
                _nextSave = now + SavePeriod;
                Save();
            }
        }

        private static Rec Get(string id, string name)
        {
            Rec r;
            if (Cache.TryGetValue(id, out r)) { if (!string.IsNullOrEmpty(name)) r.Name = name; return r; }
            string raw;
            r = FeatureStore.Table(Tbl).TryGetValue(id, out raw) ? Parse(raw) : new Rec();
            if (!string.IsNullOrEmpty(name)) r.Name = name;
            if (r.First == 0L)
            {
                var pres = PresenceFirst(id);
                r.First = pres > 0L ? pres : DateTime.UtcNow.Ticks;
                _dirty = true;
            }
            Cache[id] = r;
            return r;
        }

        private static long PresenceFirst(string id)
        {
            try
            {
                var pres = FeatureStore.Table("presence");
                string row;
                if (!pres.TryGetValue(id, out row) && !pres.TryGetValue(CompanionPlugin.FeatureBareId(id), out row)) return 0L;
                var parts = (row ?? "").Split('|');
                return parts.Length > 0 ? Wave9.ParseLong(parts[0], 0L) : 0L;
            }
            catch (Exception) { return 0L; }
        }

        private static void Sample()
        {
            var gen = WorldGenerator.instance;
            foreach (var p in Wave9.ReadyPeers())
            {
                if (p.m_characterID.IsNone()) continue;
                var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(p.m_characterID) : null;
                if (zdo == null) continue;
                var id = Wave9.PeerId(p);
                if (id == "?") continue;
                SampleOne(id, p.m_playerName, p.m_uid, zdo.GetPosition(), zdo, gen);
            }
            if (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated())
            {
                var lp = Player.m_localPlayer;
                var nv = lp.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    SampleOne("HOST", lp.GetPlayerName(), ZDOMan.GetSessionID(), lp.transform.position, nv.GetZDO(), gen);
            }
        }

        private static void SampleOne(string id, string name, long uid, Vector3 pos, ZDO zdo, WorldGenerator gen)
        {
            var r = Get(id, name);
            var now = DateTime.UtcNow.Ticks;
            if (gen != null)
            {
                var biome = (int)gen.GetBiome(pos);
                if (biome != 0 && !r.Biomes.ContainsKey(biome))
                {
                    r.Biomes[biome] = now;
                    _dirty = true;
                    if ((biome == (int)Heightmap.Biome.Mistlands || biome == (int)Heightmap.Biome.AshLands || biome == (int)Heightmap.Biome.DeepNorth)
                        && now - r.First < TimeSpan.TicksPerMinute * 15)
                        Flag(r, id, uid, pos, $"first entered {(Heightmap.Biome)biome} {Minutes(now - r.First)} min after first joining");
                }
            }
            foreach (var key in EquipKeys)
            {
                var hash = zdo.GetInt(key, 0);
                if (hash == 0) continue;
                string itemName;
                var tier = TierOf(hash, out itemName);
                if (tier < 2 || r.Tiers.ContainsKey(tier)) continue;
                r.Tiers[tier] = new KeyValuePair<long, string>(now, itemName);
                _dirty = true;
                if (tier >= 4 && now - r.First < TimeSpan.TicksPerMinute * 30)
                    Flag(r, id, uid, pos, $"seen with tier-{tier} gear ({itemName}) {Minutes(now - r.First)} min after first joining");
            }
        }

        private static void RecordBoss(string id, string name, long uid, string boss, long ticks, Vector3 pos)
        {
            if (string.IsNullOrEmpty(id) || id == "?") return;
            var r = Get(id, name);
            var idx = Array.IndexOf(BossOrder, boss);
            if (idx > 0 && !r.Bosses.ContainsKey(BossOrder[idx - 1]) && !r.Bosses.ContainsKey(boss) && !WorldDefeated(BossOrder[idx - 1]))
                Flag(r, id, uid, pos, $"present at the {boss} kill while {BossOrder[idx - 1]} was never defeated in this world");
            if (!r.Bosses.ContainsKey(boss)) r.Bosses[boss] = ticks;
            _dirty = true;
        }

        private static bool WorldDefeated(string boss)
        {
            try { return ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey("defeated_" + boss); }
            catch (Exception) { return true; }   // unknown: never accuse
        }

        private static int Minutes(long ticks) => (int)Math.Max(0, ticks / TimeSpan.TicksPerMinute);

        private static void Flag(Rec r, string id, long uid, Vector3 pos, string text)
        {
            text = (text ?? "").Replace('|', '/').Replace(';', ',').Replace('~', '-');
            r.Flags.Add(new KeyValuePair<long, string>(DateTime.UtcNow.Ticks, text));
            while (r.Flags.Count > FlagCap) r.Flags.RemoveAt(0);
            _dirty = true;
            if (id == "HOST") return;
            try { Wave7Guard.ProgressionFlag(id, uid, text, pos); }
            catch (Exception) { try { Wave1Moderation.NotifyOnlineAdmins($"Progression flag: {r.Name} ({id}) {text}"); } catch (Exception) { } }
        }

        // ---- tier classification (cached per item hash) ----

        private static readonly Dictionary<int, KeyValuePair<int, string>> TierCache = new Dictionary<int, KeyValuePair<int, string>>();
        private static object _tierDb;

        private static int TierOf(int itemHash, out string itemName)
        {
            var db = ObjectDB.instance;
            if (!ReferenceEquals(db, _tierDb)) { TierCache.Clear(); _tierDb = db; }
            KeyValuePair<int, string> c;
            if (TierCache.TryGetValue(itemHash, out c)) { itemName = c.Value; return c.Key; }
            var tier = 0;
            itemName = "#" + itemHash;
            try
            {
                var go = db != null ? db.GetItemPrefab(itemHash) : null;
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop != null)
                {
                    itemName = go.name;
                    var recipe = db.GetRecipe(drop.m_itemData);
                    if (recipe != null && recipe.m_resources != null)
                        foreach (var req in recipe.m_resources)
                            if (req != null && req.m_resItem != null) tier = Math.Max(tier, MaterialTier(req.m_resItem.name));
                }
            }
            catch (Exception) { }
            TierCache[itemHash] = new KeyValuePair<int, string>(tier, itemName);
            return tier;
        }

        private static int MaterialTier(string mat)
        {
            switch (mat)
            {
                case "Bronze": return 2;
                case "Iron": return 3;
                case "Silver": return 4;
                case "BlackMetal": return 5;
                case "Eitr": case "Carapace": return 6;
                case "Flametal": case "FlametalNew": return 7;
                default: return 0;
            }
        }

        // ---- persistence: "v1|name|first|biomes|tiers|bosses|flags" ----

        private static Rec Parse(string raw)
        {
            var r = new Rec();
            var p = Wave9.Unpack(raw, 7);
            if (p[0] != "v1") return r;
            r.Name = p[1];
            r.First = Wave9.ParseLong(p[2], 0L);
            foreach (var e in p[3].Split(','))
            {
                var kv = e.Split(':');
                if (kv.Length == 2) r.Biomes[Wave9.ParseInt(kv[0], 0)] = Wave9.ParseLong(kv[1], 0L);
            }
            foreach (var e in p[4].Split(','))
            {
                var kv = e.Split(':');
                if (kv.Length == 3) r.Tiers[Wave9.ParseInt(kv[0], 0)] = new KeyValuePair<long, string>(Wave9.ParseLong(kv[1], 0L), kv[2]);
            }
            foreach (var e in p[5].Split(','))
            {
                var kv = e.Split(':');
                if (kv.Length == 2 && kv[0].Length > 0) r.Bosses[kv[0]] = Wave9.ParseLong(kv[1], 0L);
            }
            foreach (var e in p[6].Split(';'))
            {
                var t = e.IndexOf('~');
                if (t > 0) r.Flags.Add(new KeyValuePair<long, string>(Wave9.ParseLong(e.Substring(0, t), 0L), e.Substring(t + 1)));
            }
            return r;
        }

        private static string Serialize(Rec r)
        {
            var b = new StringBuilder();
            foreach (var kv in r.Biomes) b.Append(b.Length > 0 ? "," : "").Append(kv.Key).Append(':').Append(kv.Value);
            var biomes = b.ToString(); b.Length = 0;
            foreach (var kv in r.Tiers) b.Append(b.Length > 0 ? "," : "").Append(kv.Key).Append(':').Append(kv.Value.Key).Append(':').Append((kv.Value.Value ?? "").Replace(':', '_').Replace(',', '_'));
            var tiers = b.ToString(); b.Length = 0;
            foreach (var kv in r.Bosses) b.Append(b.Length > 0 ? "," : "").Append(kv.Key).Append(':').Append(kv.Value);
            var bosses = b.ToString(); b.Length = 0;
            foreach (var kv in r.Flags) b.Append(b.Length > 0 ? ";" : "").Append(kv.Key).Append('~').Append(kv.Value);
            return Wave9.Pack("v1", r.Name, Wave9.I(r.First), biomes, tiers, bosses, b.ToString());
        }

        private static void Save()
        {
            _dirty = false;
            var t = FeatureStore.Table(Tbl);
            foreach (var kv in Cache) t[kv.Key] = Serialize(kv.Value);
            FeatureStore.SaveTable(Tbl);
        }

        // ==================== RPCs ====================

        private static void OnList(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvProgList")) return;
            if (_dirty) Save();
            var online = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in Wave9.ReadyPeers()) online.Add(Wave9.PeerId(p));
            var rows = new List<KeyValuePair<string, Rec>>();
            foreach (var kv in FeatureStore.Table(Tbl))
            {
                Rec r;
                if (!Cache.TryGetValue(kv.Key, out r)) r = Parse(kv.Value);
                rows.Add(new KeyValuePair<string, Rec>(kv.Key, r));
            }
            rows.Sort((a, b) =>
            {
                var f = b.Value.Flags.Count.CompareTo(a.Value.Flags.Count);
                if (f != 0) return f;
                var o = online.Contains(b.Key).CompareTo(online.Contains(a.Key));
                return o != 0 ? o : string.Compare(a.Value.Name, b.Value.Name, StringComparison.OrdinalIgnoreCase);
            });
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            var n = Math.Min(rows.Count, ListCap);
            pkg.Write(rows.Count);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var r = rows[i].Value;
                var maxTier = 0;
                foreach (var t in r.Tiers.Keys) maxTier = Math.Max(maxTier, t);
                pkg.Write(rows[i].Key);
                pkg.Write(r.Name ?? "");
                pkg.Write(r.First);
                pkg.Write(maxTier);
                pkg.Write(r.Bosses.Count);
                pkg.Write(r.Flags.Count);
                pkg.Write(online.Contains(rows[i].Key));
            }
            Wave9.Reply(sender, "AP_ProgList", pkg);
        }

        private static void OnReq(long sender, string id)
        {
            if (!Wave9.Can(sender, "AP_SrvProgReq")) return;
            id = Wave9.CleanName(id, 64);
            if (id.Length == 0) return;
            Rec r;
            if (!Cache.TryGetValue(id, out r))
            {
                string raw;
                if (!FeatureStore.Table(Tbl).TryGetValue(id, out raw)) { Wave9.Tell(sender, "No progression recorded for that player yet."); return; }
                r = Parse(raw);
            }
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(id);
            pkg.Write(r.Name ?? "");
            pkg.Write(r.First);
            pkg.Write(r.Biomes.Count);
            foreach (var kv in r.Biomes) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
            pkg.Write(r.Tiers.Count);
            foreach (var kv in r.Tiers) { pkg.Write(kv.Key); pkg.Write(kv.Value.Key); pkg.Write(kv.Value.Value ?? ""); }
            pkg.Write(r.Bosses.Count);
            foreach (var kv in r.Bosses) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
            pkg.Write(r.Flags.Count);
            foreach (var kv in r.Flags) { pkg.Write(kv.Key); pkg.Write(kv.Value ?? ""); }
            Wave9.Reply(sender, "AP_ProgData", pkg);
        }
    }
}
