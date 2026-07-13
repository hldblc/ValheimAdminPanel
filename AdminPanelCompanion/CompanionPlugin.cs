using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class CompanionPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.halitb.adminpanelcompanion";
        public const string PluginName = "AdminPanelCompanion";
        public const string PluginVersion = "2.1.1";

        internal static CompanionPlugin Instance;

        private static readonly List<ZDOID> LastSpawnBatch = new List<ZDOID>();

        private void Awake()
        {
            Instance = this;
            Harmony.CreateAndPatchAll(typeof(RpcRegistration));
            try { Harmony.CreateAndPatchAll(typeof(RouteRpcSanitizer)); }
            catch (Exception e) { Logger.LogWarning($"RoutedRPC sender-sanitizer patch failed (server security reduced): {e.Message}"); }
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private static void Log(string msg) => Instance?.Logger.LogInfo(msg);

        [HarmonyPatch]
        private static class RpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // server-side entry points (validated against adminlist.txt)
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvGive", OnServerGive);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSpawn", OnServerSpawn);
                ZRoutedRpc.instance.Register<long>("AP_SrvReqInv", OnServerRequestInventory);
                ZRoutedRpc.instance.Register("AP_SrvUndo", new Action<long>(OnServerUndo));
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTeleport", OnServerTeleport);
                ZRoutedRpc.instance.Register<long>("AP_SrvHeal", OnServerHeal);
                ZRoutedRpc.instance.Register<long>("AP_SrvKick", OnServerKick);
                ZRoutedRpc.instance.Register<long>("AP_SrvBan", OnServerBan);
                ZRoutedRpc.instance.Register<string>("AP_SrvUnban", OnServerUnban);
                ZRoutedRpc.instance.Register<string>("AP_SrvBroadcast", OnServerBroadcast);
                ZRoutedRpc.instance.Register<long, string>("AP_SrvMsg", OnServerMessage);
                ZRoutedRpc.instance.Register<string, Vector3>("AP_SrvEvent", OnServerEvent);
                ZRoutedRpc.instance.Register<bool>("AP_SrvPeaceful", OnServerPeaceful);
                // client-side executors (only accepted when sent by the server)
                ZRoutedRpc.instance.Register<string, int, int, string>("AP_GiveItem", OnGiveItem);
                ZRoutedRpc.instance.Register<long>("AP_InvRequest", OnInventoryRequest);
                ZRoutedRpc.instance.Register<Vector3>("AP_Teleport", OnTeleport);
                ZRoutedRpc.instance.Register("AP_HealSelf", new Action<long>(OnHealSelf));
                ZRoutedRpc.instance.Register<string>("AP_Msg", OnMessage);
            }
        }

        // Server-side hard authentication of the routed-RPC sender. Valheim's ZRoutedRpc.RPC_RoutedRPC reads
        // m_senderPeerID straight from the packet the client sent and NEVER re-stamps it with the id of the real
        // transport connection, so a malicious client can forge sender==<any admin> (privilege escalation on the
        // AP_Srv* handlers) or sender==<server> (impersonating the server toward other clients on AP_Teleport/etc.).
        // We patch the server's receive handler and overwrite the forged sender in-place with the verified uid of
        // the socket that actually delivered the packet BEFORE it is dispatched/relayed. This closes both holes for
        // every AP_* (and every other) routed RPC. On clients this is a no-op (the delivering rpc is the server).
        [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
        private static class RouteRpcSanitizer
        {
            private static void Prefix(ZRpc rpc, ZPackage pkg)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // only the server relays/dispatches
                if (pkg == null) return;
                long realUid = 0L;
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.m_rpc == rpc) { realUid = peer.m_uid; break; }
                if (realUid == 0L) return; // unknown/local connection — nothing to verify against
                // RoutedRPCData layout: long m_msgID, long m_senderPeerID, long m_targetPeerID, ...
                // so m_senderPeerID is the second long, at byte offset 8.
                var saved = pkg.GetPos();
                try
                {
                    pkg.SetPos(8); // skip m_msgID
                    var claimed = pkg.ReadLong();
                    if (claimed != realUid)
                    {
                        pkg.SetPos(8);
                        pkg.Write(realUid); // overwrite the forged sender in place (same width, same length)
                    }
                }
                catch { /* malformed packet — leave it for the game's own handler to reject */ }
                finally { pkg.SetPos(saved); }
            }
        }

        // ---------- security ----------
        private static bool IsDedicatedServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static long ServerUid()
        {
            var peer = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            return peer != null ? peer.m_uid : 0L;
        }

        private static bool SenderIsServer(long sender) => sender == ServerUid();

        private static string BareId(string host) =>
            !string.IsNullOrEmpty(host) && host.Contains("_") ? host.Substring(host.IndexOf('_') + 1) : host;

        private static SyncedList GetList(string field) =>
            AccessTools.Field(typeof(ZNet), field)?.GetValue(ZNet.instance) as SyncedList;

        private static bool SenderIsAdmin(long sender)
        {
            if (ZNet.instance == null) return false;
            var peer = ZNet.instance.GetPeer(sender);
            var host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            if (string.IsNullOrEmpty(host)) return false;

            var adminList = GetList("m_adminList");
            var isAdmin = adminList != null && (adminList.Contains(host) || adminList.Contains(BareId(host)));
            if (!isAdmin) Log($"DENIED admin action from non-admin {host} (peer {sender})");
            return isAdmin;
        }

        // ---------- server: give / spawn / inventory ----------
        private static void OnServerGive(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; string prefabName, crafter; int amount, quality;
            try
            {
                targetUid = pkg.ReadLong();
                prefabName = pkg.ReadString();
                amount = pkg.ReadInt();
                quality = pkg.ReadInt();
                crafter = pkg.ReadString();
            }
            catch (Exception e) { Log($"AP_SrvGive: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(prefabName) || amount <= 0) return;
            amount = Mathf.Min(amount, 100000); // guard against a client freeze from an absurd stack loop
            Log($"Admin {sender} gives {amount}x {prefabName} (q{quality}) to peer {targetUid}");
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_GiveItem", prefabName, amount, quality, crafter);
        }

        private static void OnServerSpawn(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            int kind, count, levelOrQuality; string prefabName, petName; Vector3 pos; bool tamed;
            try
            {
                kind = pkg.ReadInt();          // 0 = item drop, 1 = creature
                prefabName = pkg.ReadString();
                pos = pkg.ReadVector3();
                count = pkg.ReadInt();
                levelOrQuality = pkg.ReadInt();
                tamed = pkg.ReadBool();
                petName = pkg.ReadString();
            }
            catch (Exception e) { Log($"AP_SrvSpawn: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(prefabName)) return;
            // Hard server-side caps: never trust the client UI to bound these — a huge count would run millions of
            // synchronous Instantiate/ZDO creations on the main thread and freeze/OOM the dedicated server.
            count = Mathf.Clamp(count, 0, 100);

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null && ObjectDB.instance != null) prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null) { Log($"AP_SrvSpawn: prefab '{prefabName}' not found"); return; }

            Log($"Admin {sender} spawns {count}x {prefabName} (kind {kind}) at {pos}");
            LastSpawnBatch.Clear();

            if (kind == 0)
            {
                var drop = prefab.GetComponent<ItemDrop>();
                var maxStack = drop != null ? drop.m_itemData.m_shared.m_maxStackSize : 1;
                if (maxStack < 1) maxStack = 1; // guard: a 0 max-stack would loop forever
                var remaining = count;
                while (remaining > 0)
                {
                    var stack = Mathf.Min(remaining, maxStack);
                    remaining -= stack;
                    var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                    RememberSpawn(go);
                    var d = go.GetComponent<ItemDrop>();
                    if (d != null)
                    {
                        d.m_itemData.m_stack = stack;
                        d.m_itemData.m_quality = Mathf.Clamp(levelOrQuality, 1, d.m_itemData.m_shared.m_maxQuality);
                        d.m_itemData.m_durability = d.m_itemData.GetMaxDurability();
                    }
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var offset = new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), 0.5f, UnityEngine.Random.Range(-1.5f, 1.5f));
                    var go = UnityEngine.Object.Instantiate(prefab, pos + offset, Quaternion.identity);
                    RememberSpawn(go);
                    var character = go.GetComponent<Character>();
                    if (character != null)
                    {
                        if (levelOrQuality > 1) character.SetLevel(Mathf.Clamp(levelOrQuality, 1, 10));
                        if (tamed) character.SetTamed(true);
                        if (tamed && !string.IsNullOrEmpty(petName))
                        {
                            var nview = go.GetComponent<ZNetView>();
                            nview?.GetZDO()?.Set("TamedName", petName);
                        }
                    }
                }
            }
        }

        private static void RememberSpawn(GameObject go)
        {
            var nview = go.GetComponent<ZNetView>();
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null) LastSpawnBatch.Add(zdo.m_uid);
        }

        private static void OnServerUndo(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var removed = 0;
            foreach (var id in LastSpawnBatch)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) continue;
                var go = ZNetScene.instance.FindInstance(zdo);
                var nview = go != null ? go.GetComponent<ZNetView>() : null;
                if (nview != null) { nview.ClaimOwnership(); nview.Destroy(); removed++; }
                else { ZDOMan.instance.DestroyZDO(zdo); removed++; }
            }
            Log($"Admin {sender} undo: removed {removed} spawned objects");
            LastSpawnBatch.Clear();
        }

        private static void OnServerRequestInventory(long sender, long targetUid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_InvRequest", sender);
        }

        // ---------- server: player admin ----------
        private static void OnServerTeleport(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; Vector3 pos;
            try
            {
                targetUid = pkg.ReadLong();
                pos = pkg.ReadVector3();
            }
            catch (Exception e) { Log($"AP_SrvTeleport: malformed packet dropped ({e.Message})"); return; }
            Log($"Admin {sender} teleports peer {targetUid} to {pos}");
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_Teleport", pos);
        }

        private static void OnServerHeal(long sender, long targetUid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_HealSelf");
        }

        // Look up the real network host id (Steam ID) of a connected peer by its uid.
        private static string HostOfPeer(long uid)
        {
            if (ZNet.instance == null) return null;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.m_uid == uid)
                    return peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            return null;
        }

        // Kick a peer by uid. Returns true if a matching peer was found and kicked.
        private static bool KickByUid(long uid)
        {
            if (ZNet.instance == null) return false;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.m_uid != uid) continue;
                var m = AccessTools.Method(typeof(ZNet), "InternalKick", new[] { typeof(ZNetPeer) })
                        ?? AccessTools.Method(typeof(ZNet), "Kick", new[] { typeof(ZNetPeer) });
                if (m != null) m.Invoke(ZNet.instance, new object[] { peer });
                else peer.m_rpc?.GetSocket()?.Close();
                return true;
            }
            return false;
        }

        private static void OnServerKick(long sender, long uid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var ok = KickByUid(uid);
            Log($"Admin {sender} kick peer {uid}: {(ok ? "kicked" : "peer not found")}");
        }

        private static void OnServerBan(long sender, long uid)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var host = HostOfPeer(uid);          // read the Steam ID BEFORE kicking (socket closes)
            var bare = BareId(host);
            if (!string.IsNullOrEmpty(bare))
            {
                var banned = GetList("m_bannedList");
                if (banned != null && !banned.Contains(bare)) banned.Add(bare);
            }
            var ok = KickByUid(uid);
            Log($"Admin {sender} ban peer {uid} ({bare ?? "unknown"}): {(ok ? "kicked" : "peer not found")}");
        }

        private static void OnServerUnban(long sender, string host)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var banned = GetList("m_bannedList");
            var bare = BareId(host);
            if (banned != null) { banned.Remove(bare); banned.Remove(host); }
            Log($"Admin {sender} unbans {bare}");
        }

        private static void OnServerBroadcast(long sender, string text)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            Log($"Admin broadcast: {text}");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "AP_Msg", text);
        }

        private static void OnServerMessage(long sender, long targetUid, string text)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_Msg", text);
        }

        private static void OnServerEvent(long sender, string eventName, Vector3 pos)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            if (RandEventSystem.instance == null) return;
            Log($"Admin {sender} starts event '{eventName}' at {pos}");
            RandEventSystem.instance.SetRandomEventByName(eventName, pos);
        }

        private static void OnServerPeaceful(long sender, bool peaceful)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            var field = AccessTools.Field(typeof(RandEventSystem), "m_events")
                        ?? AccessTools.Field(typeof(RandEventSystem), "m_randomEvents");
            var list = field?.GetValue(RandEventSystem.instance) as IList;
            if (list == null) { Log("Peaceful toggle: event list not found"); return; }
            var changed = 0;
            foreach (var ev in list)
            {
                var enabledField = AccessTools.Field(ev.GetType(), "m_enabled");
                if (enabledField == null) continue;
                enabledField.SetValue(ev, !peaceful);
                changed++;
            }
            if (peaceful && RandEventSystem.instance != null)
                RandEventSystem.instance.ResetRandomEvent();
            Log($"Peaceful mode {(peaceful ? "ON" : "OFF")} ({changed} events toggled)");
        }

        // ---------- client: executors, server-only ----------
        private static void OnGiveItem(long sender, string prefabName, int amount, int quality, string crafter)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!SenderIsServer(sender)) return;

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null) return;
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null) return;
            // icon-less items (hair, beards, effects) corrupt the inventory grid — never add them
            if (drop.m_itemData.m_shared.m_icons == null || drop.m_itemData.m_shared.m_icons.Length == 0) return;

            var maxStack = drop.m_itemData.m_shared.m_maxStackSize;
            if (maxStack < 1) maxStack = 1; // guard: a 0 max-stack would loop forever
            var remaining = amount;
            while (remaining > 0)
            {
                var stack = Mathf.Min(remaining, maxStack);
                remaining -= stack;
                var data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = stack;
                data.m_quality = Mathf.Clamp(quality, 1, data.m_shared.m_maxQuality);
                data.m_durability = data.GetMaxDurability();
                if (!string.IsNullOrEmpty(crafter)) { data.m_crafterID = 1; data.m_crafterName = crafter; }
                if (!player.GetInventory().AddItem(data))
                {
                    var go = UnityEngine.Object.Instantiate(prefab, player.transform.position + Vector3.up, Quaternion.identity);
                    var d = go.GetComponent<ItemDrop>();
                    if (d != null)
                    {
                        d.m_itemData.m_stack = stack;
                        d.m_itemData.m_quality = Mathf.Clamp(quality, 1, d.m_itemData.m_shared.m_maxQuality);
                    }
                }
            }
            player.Message(MessageHud.MessageType.Center, $"An admin granted you {amount}x {prefabName}!");
        }

        private static void OnInventoryRequest(long sender, long replyTo)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!SenderIsServer(sender)) return;

            var pkg = new ZPackage();
            pkg.Write(player.GetPlayerName());
            var items = player.GetInventory().GetAllItems();
            pkg.Write(items.Count);
            foreach (var item in items)
            {
                pkg.Write(item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name);
                pkg.Write(item.m_stack);
                pkg.Write(item.m_quality);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(replyTo, "AP_InvData", pkg);
        }

        private static void OnTeleport(long sender, Vector3 pos)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.TeleportTo(pos + Vector3.up, player.transform.rotation, true);
            player.Message(MessageHud.MessageType.Center, "An admin teleported you!");
        }

        private static void OnHealSelf(long sender)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.Heal(player.GetMaxHealth());
            player.Message(MessageHud.MessageType.Center, "An admin healed you!");
        }

        private static void OnMessage(long sender, string text)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            player.Message(MessageHud.MessageType.Center, $"[Server] {text}");
        }
    }
}
