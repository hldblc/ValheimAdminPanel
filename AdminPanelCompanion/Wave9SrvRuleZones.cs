using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #7 rule zones (server + modded-client enforcement) ====================
    // Round 1's no-build zones generalised: a circle or rectangle with any mix of rules. Where each rule is enforced
    // is dictated by where the game decides it:
    //   SERVER (works for every player, modded or not)
    //     64  entry message   - vanilla ShowMessage on entering
    //     32  healing aura    - vanilla Character RPC_Heal to the player's own client every 5 s
    //     128 no-build / 256 no-damage - written through to wave 5's protection zones (Wave5Area.UpsertExternalZone,
    //         name "rz:<id>"; rectangles as their enclosing circle) and enforced there, including its
    //         EnableProtectionZones switch, grandfathering and admin exemption
    //   PLAYER'S CLIENT (needs the companion on that player's client - the decision is made there)
    //     1   forced PvP      - Player.SetPVP is forced on inside, restored on leaving
    //     2   keep inventory  - Player.CreateTombStone is skipped (exactly what the DeathKeepInventory world key does)
    //     4   no portals      - TeleportWorld.Teleport refused for a player or portal inside
    //     8   no item drops   - Humanoid.DropItem refused
    //     16  speed aura      - SEMan.ApplyStatusEffectSpeedMods scaled by the zone's percentage
    // Definitions live in "w9_rzones" (id -> v1|name|shape|cx|cz|r|hx|hz|rules|speedPct|heal|msg|on) and are pushed
    // to every client that runs the companion (AP_RzoneDefs) on change and when a client is first seen modded.
    //
    //   AP_SrvRzoneList()           -> AP_RzoneData { ver, n x def }
    //   AP_SrvRzoneSet(ZPackage)    { ver, int op (0 upsert, 1 delete, 2 toggle), def } -> AP_RzoneData
    //   def: string id, string name, int shape, float cx, float cz, float r, float hx, float hz, int rules,
    //        int speedPct, float heal, string msg, bool on
    internal static class Wave9Rzone
    {
        private const string Tbl = "w9_rzones";
        private const int MaxZones = 48;
        internal const int RPvp = 1, RKeepInv = 2, RNoPortal = 4, RNoDrop = 8, RSpeed = 16, RHeal = 32, RMessage = 64, RNoBuild = 128, RNoDamage = 256;

        internal sealed class Zone
        {
            public string Id = "", Name = "", Msg = "";
            public int Shape;            // 0 circle, 1 rectangle (axis-aligned)
            public float Cx, Cz, R = 20f, Hx = 20f, Hz = 20f, Heal = 5f;
            public int Rules, SpeedPct = 25;
            public bool On = true;

            public bool Has(int rule) => On && (Rules & rule) != 0;

            public bool Contains(Vector3 p)
            {
                var dx = p.x - Cx;
                var dz = p.z - Cz;
                return Shape == 0 ? dx * dx + dz * dz <= R * R : Math.Abs(dx) <= Hx && Math.Abs(dz) <= Hz;
            }

            public float Enclosing => Shape == 0 ? R : Mathf.Sqrt(Hx * Hx + Hz * Hz);

            public void Write(ZPackage p)
            {
                p.Write(Id); p.Write(Name); p.Write(Shape); p.Write(Cx); p.Write(Cz); p.Write(R); p.Write(Hx); p.Write(Hz);
                p.Write(Rules); p.Write(SpeedPct); p.Write(Heal); p.Write(Msg); p.Write(On);
            }

            public static Zone Read(ZPackage p) => new Zone
            {
                Id = p.ReadString() ?? "", Name = p.ReadString() ?? "", Shape = p.ReadInt(), Cx = p.ReadSingle(), Cz = p.ReadSingle(),
                R = p.ReadSingle(), Hx = p.ReadSingle(), Hz = p.ReadSingle(), Rules = p.ReadInt(), SpeedPct = p.ReadInt(),
                Heal = p.ReadSingle(), Msg = p.ReadString() ?? "", On = p.ReadBool(),
            };
        }

        /// <summary>Fires on the SERVER when a player enters a zone (trigger engine, #12).</summary>
        internal static event Action<long, string, Zone> ZoneEntered;

        private static readonly List<Zone> Zones = new List<Zone>();     // server truth
        private static readonly List<Zone> ClientZones = new List<Zone>(); // what this client was told
        private static bool _loaded;
        private static readonly Dictionary<long, HashSet<string>> Inside = new Dictionary<long, HashSet<string>>();
        private static readonly HashSet<long> DefsSent = new HashSet<long>();
        private static float _nextCheck, _nextHeal, _nextDefs, _nextClient;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRzoneSet", "builder");
            CompanionPlugin.GrantRole("AP_SrvRzoneList", "builder");
            CompanionPlugin.GrantRole("AP_SrvRzoneList", "moderator");
            Wave9.ApplyPatch("Wave9RzoneClientRules", typeof(ClientRulePatches), "rule zones' client-side rules (PvP, keep inventory, portals, drops, speed) are not enforced on this client");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register("AP_SrvRzoneList", new Action<long>(OnList));
            r.Register<ZPackage>("AP_SrvRzoneSet", OnSet);
            r.Register<ZPackage>("AP_RzoneDefs", OnDefs);
            Zones.Clear();
            ClientZones.Clear();
            Inside.Clear();
            DefsSent.Clear();
            _loaded = false;
        }

        // ==================== server: store ====================

        private static void Load()
        {
            if (_loaded || !FeatureStore.Ready) return;
            _loaded = true;
            Zones.Clear();
            foreach (var kv in FeatureStore.Table(Tbl))
            {
                var p = Wave9.Unpack(kv.Value, 13);
                if (p[0] != "v1") continue;
                Zones.Add(new Zone
                {
                    Id = kv.Key, Name = p[1], Shape = Wave9.ParseInt(p[2], 0), Cx = Wave9.ParseFloat(p[3], 0f), Cz = Wave9.ParseFloat(p[4], 0f),
                    R = Wave9.ParseFloat(p[5], 20f), Hx = Wave9.ParseFloat(p[6], 20f), Hz = Wave9.ParseFloat(p[7], 20f),
                    Rules = Wave9.ParseInt(p[8], 0), SpeedPct = Wave9.ParseInt(p[9], 25), Heal = Wave9.ParseFloat(p[10], 5f),
                    Msg = p[11], On = p[12] != "0",
                });
            }
        }

        private static void Save()
        {
            var t = FeatureStore.Table(Tbl);
            t.Clear();
            foreach (var z in Zones)
                t[z.Id] = Wave9.Pack("v1", z.Name, Wave9.I(z.Shape), F(z.Cx), F(z.Cz), F(z.R), F(z.Hx), F(z.Hz),
                    Wave9.I(z.Rules), Wave9.I(z.SpeedPct), F(z.Heal), z.Msg, z.On ? "1" : "0");
            FeatureStore.SaveTable(Tbl);
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static void SyncProtection(Zone z, bool removed)
        {
            var name = "rz:" + z.Id;
            try
            {
                var kind = (z.Rules & RNoBuild) != 0 && (z.Rules & RNoDamage) != 0 ? 2 : (z.Rules & RNoBuild) != 0 ? 0 : (z.Rules & RNoDamage) != 0 ? 1 : -1;
                if (removed || !z.On || kind < 0) Wave5Area.RemoveExternalZone(name);
                else
                {
                    var y = 0f;
                    try { if (ZoneSystem.instance != null) y = ZoneSystem.instance.GetGroundHeight(new Vector3(z.Cx, 0f, z.Cz)); } catch (Exception) { }
                    Wave5Area.UpsertExternalZone(name, new Vector3(z.Cx, y, z.Cz), z.Enclosing, kind);
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Rule zone {z.Name}: protection sync failed: {e.Message}"); }
        }

        // ==================== server: RPCs ====================

        private static void OnList(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvRzoneList")) return;
            Load();
            Wave9.Reply(sender, "AP_RzoneData", ListPkg());
        }

        private static ZPackage ListPkg()
        {
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(Zones.Count);
            foreach (var z in Zones) z.Write(pkg);
            return pkg;
        }

        private static void OnSet(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvRzoneSet")) return;
            Load();
            int op; Zone z;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                op = pkg.ReadInt();
                z = Zone.Read(pkg);
            }
            catch (Exception) { return; }
            z.Name = Wave9.Clean(z.Name, 32);
            z.Msg = Wave9.Clean(z.Msg, 160);
            z.Id = Wave9.CleanName(z.Id, 12);
            var existing = Zones.Find(x => x.Id == z.Id);
            string what;
            if (op == 1)
            {
                if (existing == null) return;
                Zones.Remove(existing);
                SyncProtection(existing, true);
                what = "deleted";
            }
            else if (op == 2)
            {
                if (existing == null) return;
                existing.On = !existing.On;
                SyncProtection(existing, false);
                what = existing.On ? "enabled" : "disabled";
                z = existing;
            }
            else
            {
                if (z.Name.Length == 0) { Wave9.Tell(sender, "A rule zone needs a name."); return; }
                if (float.IsNaN(z.Cx) || float.IsNaN(z.Cz) || float.IsInfinity(z.Cx) || float.IsInfinity(z.Cz)) return;
                z.Shape = Mathf.Clamp(z.Shape, 0, 1);
                z.R = Mathf.Clamp(float.IsNaN(z.R) ? 20f : z.R, 2f, 1000f);
                z.Hx = Mathf.Clamp(float.IsNaN(z.Hx) ? 20f : z.Hx, 2f, 1000f);
                z.Hz = Mathf.Clamp(float.IsNaN(z.Hz) ? 20f : z.Hz, 2f, 1000f);
                z.SpeedPct = Mathf.Clamp(z.SpeedPct, -50, 200);
                z.Heal = Mathf.Clamp(float.IsNaN(z.Heal) ? 5f : z.Heal, 0f, 200f);
                z.Rules &= 0x1FF;
                if ((z.Rules & (RNoBuild | RNoDamage)) != 0)
                {
                    // No-build / no-damage ARE protection zones (Wave5Area): one circle of at most 128 m, at most 64 per
                    // world. Refuse instead of silently protecting less (or a different shape) than the admin drew.
                    if (z.Enclosing > Wave5Area.ExternalMaxRadius)
                    {
                        Wave9.Tell(sender, $"No-build / no-damage cover at most {Wave5Area.ExternalMaxRadius:0} m around the centre; this zone reaches {z.Enclosing:0} m" +
                                           (z.Shape == 1 ? " (a rectangle counts to its corners)" : "") + ". Make it smaller or untick those two rules.");
                        return;
                    }
                    if (!Wave5Area.CanAddExternalZone("rz:" + (existing != null ? existing.Id : "")))
                    {
                        Wave9.Tell(sender, "No-build / no-damage need a protection zone, and this world already has the maximum of 64. Remove one in Area Tools first.");
                        return;
                    }
                }
                if (existing == null)
                {
                    if (Zones.Count >= MaxZones) { Wave9.Tell(sender, $"Rule zone limit reached ({MaxZones})."); return; }
                    z.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                    Zones.Add(z);
                    what = "created";
                }
                else
                {
                    Zones[Zones.IndexOf(existing)] = z;
                    what = "updated";
                }
                SyncProtection(z, false);
            }
            Save();
            BroadcastDefs();
            CompanionPlugin.SrvAudit(sender, "RuleZone", $"{what} '{z.Name}' rules={z.Rules} shape={z.Shape} at {F(z.Cx)},{F(z.Cz)}");
            Wave9.Tell(sender, $"Rule zone '{z.Name}' {what}.");
            Wave9.Reply(sender, "AP_RzoneData", ListPkg());
        }

        private static ZPackage DefsPkg()
        {
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            var on = Zones.FindAll(x => x.On && (x.Rules & (RPvp | RKeepInv | RNoPortal | RNoDrop | RSpeed)) != 0);
            pkg.Write(on.Count);
            foreach (var z in on) z.Write(pkg);
            return pkg;
        }

        private static void BroadcastDefs()
        {
            DefsSent.Clear();   // the next tick re-sends to every modded peer
            _nextDefs = 0f;
            ApplyDefsLocally();
        }

        // A listen-server host is a client too: it applies the definitions in-process.
        private static void ApplyDefsLocally()
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            ClientZones.Clear();
            foreach (var z in Zones) if (z.On) ClientZones.Add(z);
        }

        // ==================== server: tick ====================

        internal static void Tick()
        {
            var now = Time.unscaledTime;
            if (Wave9.IsServer)
            {
                Load();
                if (now >= _nextDefs)
                {
                    _nextDefs = now + 5f;
                    if (DefsSent.Count == 0) ApplyDefsLocally();
                    foreach (var p in Wave9.ReadyPeers())
                    {
                        if (DefsSent.Contains(p.m_uid) || Wave34Core.HasMod(p.m_uid) != true) continue;
                        try { ZRoutedRpc.instance.InvokeRoutedRPC(p.m_uid, "AP_RzoneDefs", DefsPkg()); DefsSent.Add(p.m_uid); } catch (Exception) { }
                    }
                }
                if (Zones.Count > 0 && now >= _nextCheck)
                {
                    _nextCheck = now + 1f;
                    var heal = now >= _nextHeal;
                    if (heal) _nextHeal = now + 5f;
                    CheckPeers(heal);
                }
            }
            if (now >= _nextClient)
            {
                _nextClient = now + 0.5f;
                ClientTick();
            }
        }

        private static void CheckPeers(bool heal)
        {
            var seen = new HashSet<long>();
            foreach (var p in Wave9.ReadyPeers())
            {
                if (p.m_characterID.IsNone()) continue;
                seen.Add(p.m_uid);
                CheckOne(p.m_uid, p.m_playerName, Wave9.PeerPos(p), heal, p.m_characterID);
            }
            if (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated())
            {
                var host = ZDOMan.GetSessionID();
                seen.Add(host);
                var nv = Player.m_localPlayer.GetComponent<ZNetView>();
                CheckOne(host, Player.m_localPlayer.GetPlayerName(), Player.m_localPlayer.transform.position, heal, nv != null && nv.GetZDO() != null ? nv.GetZDO().m_uid : ZDOID.None);
            }
            var gone = new List<long>();
            foreach (var k in Inside.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) Inside.Remove(k);
        }

        private static void CheckOne(long uid, string name, Vector3 pos, bool heal, ZDOID character)
        {
            HashSet<string> before;
            if (!Inside.TryGetValue(uid, out before)) Inside[uid] = before = new HashSet<string>();
            foreach (var z in Zones)
            {
                if (!z.On) { before.Remove(z.Id); continue; }
                var inside = z.Contains(pos);
                if (inside && before.Add(z.Id))
                {
                    if ((z.Rules & RMessage) != 0 && z.Msg.Length > 0) Wave9.Center(uid, z.Msg);
                    var h = ZoneEntered;
                    if (h != null) { try { h(uid, name, z); } catch (Exception e) { CompanionPlugin.FeatureLog($"Zone-enter subscriber failed: {e.Message}"); } }
                }
                else if (!inside) before.Remove(z.Id);
                if (inside && heal && (z.Rules & RHeal) != 0 && z.Heal > 0f && !character.IsNone())
                {
                    try { ZRoutedRpc.instance.InvokeRoutedRPC(uid, character, "RPC_Heal", z.Heal, true); } catch (Exception) { }
                }
            }
        }

        // ==================== client: definitions + enforcement ====================

        private static void OnDefs(long sender, ZPackage pkg)
        {
            if (!Wave9.FromServer(sender)) return;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                var n = pkg.ReadInt();
                if (n < 0 || n > MaxZones) return;
                var list = new List<Zone>(n);
                for (var i = 0; i < n; i++) list.Add(Zone.Read(pkg));
                ClientZones.Clear();
                ClientZones.AddRange(list);
            }
            catch (Exception) { }
        }

        private static int _localRules;          // union of the rules of every zone the local player stands in
        private static int _localSpeedPct;
        private static bool _pvpForced, _pvpBefore;

        private static void ClientTick()
        {
            var p = Player.m_localPlayer;
            // Clear the rules BEFORE releasing: ReleasePvp's SetPVP(false) passes the ForcePvp prefix only once the
            // local player no longer counts as standing in a PvP zone.
            if (p == null || ClientZones.Count == 0) { _localRules = 0; _localSpeedPct = 0; ReleasePvp(p); return; }
            var pos = p.transform.position;
            var rules = 0;
            var speed = 0;
            foreach (var z in ClientZones)
            {
                if (!z.On || !z.Contains(pos)) continue;
                rules |= z.Rules;
                if ((z.Rules & RSpeed) != 0 && Math.Abs(z.SpeedPct) > Math.Abs(speed)) speed = z.SpeedPct;
            }
            _localRules = rules;
            _localSpeedPct = speed;
            if ((rules & RPvp) != 0)
            {
                if (!_pvpForced) { _pvpForced = true; _pvpBefore = p.IsPVPEnabled(); }
                if (!p.IsPVPEnabled()) p.SetPVP(true);
            }
            else ReleasePvp(p);
        }

        private static void ReleasePvp(Player p)
        {
            if (!_pvpForced) return;
            _pvpForced = false;
            try { if (p != null && !_pvpBefore) p.SetPVP(false); } catch (Exception) { }
        }

        private static bool LocalHas(int rule) => (_localRules & rule) != 0;

        private static bool PointHas(Vector3 pos, int rule)
        {
            foreach (var z in ClientZones) if (z.Has(rule) && z.Contains(pos)) return true;
            return false;
        }

        [HarmonyPatch]
        private static class ClientRulePatches
        {
            [HarmonyPatch(typeof(Player), "CreateTombStone")]
            [HarmonyPrefix]
            private static bool KeepInventory(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !PointHas(__instance.transform.position, RKeepInv)) return true;
                __instance.Message(MessageHud.MessageType.TopLeft, "Rule zone: you keep your inventory here.");
                return false;
            }

            [HarmonyPatch(typeof(TeleportWorld), "Teleport")]
            [HarmonyPrefix]
            private static bool NoPortal(TeleportWorld __instance, Player player)
            {
                if (player == null || player != Player.m_localPlayer) return true;
                if (!PointHas(player.transform.position, RNoPortal) && !PointHas(__instance.transform.position, RNoPortal)) return true;
                player.Message(MessageHud.MessageType.Center, "Portals are disabled in this zone.");
                return false;
            }

            [HarmonyPatch(typeof(Humanoid), "DropItem")]
            [HarmonyPrefix]
            private static bool NoDrop(Humanoid __instance, ref bool __result)
            {
                if (__instance != Player.m_localPlayer || !LocalHas(RNoDrop)) return true;
                __instance.Message(MessageHud.MessageType.Center, "Dropping items is disabled in this zone.");
                __result = false;
                return false;
            }

            [HarmonyPatch(typeof(SEMan), "ApplyStatusEffectSpeedMods")]
            [HarmonyPostfix]
            private static void Speed(Character ___m_character, ref float speed)
            {
                if (_localSpeedPct == 0 || ___m_character == null || ___m_character != Player.m_localPlayer || !LocalHas(RSpeed)) return;
                speed *= 1f + _localSpeedPct / 100f;
            }

            [HarmonyPatch(typeof(Player), "SetPVP")]
            [HarmonyPrefix]
            private static void ForcePvp(Player __instance, ref bool enabled)
            {
                if (__instance == Player.m_localPlayer && !enabled && LocalHas(RPvp)) enabled = true;
            }
        }
    }
}
