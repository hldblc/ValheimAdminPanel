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
        // Version policy: lockstep with the panel — both DLLs of a release always carry the SAME number,
        // and the panel warns in-game when the server's companion doesn't match (AP_SrvVersion handshake).
        public const string PluginVersion = "2.3.0";

        internal static CompanionPlugin Instance;

        // Undo history, keyed by admin peer id. Deliberately NOT one shared "last batch" as before: that had two
        // problems on a multi-admin server — one admin's Undo deleted whichever admin had spawned most recently,
        // and it could only ever step back a single spawn. Depth is bounded so a long session cannot grow without
        // limit, and entries for peers that have disconnected are pruned whenever a new batch is pushed.
        private const int UndoDepth = 20;
        private static readonly Dictionary<long, List<List<ZDOID>>> UndoHistory =
            new Dictionary<long, List<List<ZDOID>>>();

        // Recognising the host by its session id is only safe while the sanitizer is re-stamping incoming senders.
        private static bool SenderSanitizerActive;

        private void Awake()
        {
            Instance = this;
            Harmony.CreateAndPatchAll(typeof(RpcRegistration));
            try { Harmony.CreateAndPatchAll(typeof(RouteRpcSanitizer)); SenderSanitizerActive = true; }
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
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvInvRemove", OnServerInvRemove);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSkillRaise", OnServerSkillRaise);
                ZRoutedRpc.instance.Register("AP_SrvVersion", new Action<long>(OnServerVersionReq));
                ZRoutedRpc.instance.Register("AP_SrvSkipNight", new Action<long>(OnServerSkipNight));
                // client-side executors (only accepted when sent by the server)
                ZRoutedRpc.instance.Register<string, int, int, string>("AP_GiveItem", OnGiveItem);
                ZRoutedRpc.instance.Register<ZPackage>("AP_RemoveItem", OnRemoveItem);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SkillRaise", OnSkillRaise);
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

        // A host (single-player or listen-server) is never in ZNet.m_peers — that list is filled only from
        // OnNewConnection, i.e. remote sockets — so it cannot be resolved by peer lookup and ServerUid() is
        // structurally 0 for it (GetServerPeer() returns null when IsServer()). Both checks below therefore have
        // to recognise the host by its own session id, which is what ZRoutedRpc stamps on a locally dispatched
        // packet. A remote client cannot forge it: RouteRpcSanitizer overwrites the sender of every
        // socket-delivered packet with the real peer uid before dispatch.
        // Gated on the sanitizer: if that patch ever fails to apply, an incoming packet's sender is attacker-chosen,
        // so the host's session id would be forgeable and this would become a privilege escalation.
        private static bool IsLocalHostSender(long sender) =>
            SenderSanitizerActive && ZNet.instance != null && ZNet.instance.IsServer() &&
            ZDOMan.instance != null && sender == ZDOMan.GetSessionID();

        private static bool SenderIsServer(long sender)
        {
            var s = ServerUid();
            if (s != 0L && sender == s) return true;   // client: the packet really came from the server peer
            return IsLocalHostSender(sender);          // host: we are the server
        }

        private static string BareId(string host) =>
            !string.IsNullOrEmpty(host) && host.Contains("_") ? host.Substring(host.IndexOf('_') + 1) : host;

        private static SyncedList GetList(string field) =>
            AccessTools.Field(typeof(ZNet), field)?.GetValue(ZNet.instance) as SyncedList;

        private static bool SenderIsAdmin(long sender)
        {
            if (ZNet.instance == null) return false;
            // The host is implicitly admin, exactly as the engine treats it in ZNet.LocalPlayerIsAdminOrHost().
            // Without this the peer lookup below returns null for the host and denies every admin action before
            // adminlist.txt is ever consulted, which is why adding your own id to the list had no effect.
            if (IsLocalHostSender(sender)) return true;
            var peer = ZNet.instance.GetPeer(sender);
            var host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            if (string.IsNullOrEmpty(host)) { Log($"DENIED admin action from unresolvable peer {sender}"); return false; }

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
            var batch = new List<ZDOID>();

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
                    RememberSpawn(go, batch);
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
                    RememberSpawn(go, batch);
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

            // One spawn action = one undo step, recorded after the whole batch exists so a partially-built
            // batch can never be popped.
            PushUndo(sender, batch);
        }

        private static void RememberSpawn(GameObject go, List<ZDOID> batch)
        {
            var nview = go.GetComponent<ZNetView>();
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null) batch.Add(zdo.m_uid);
        }

        /// <summary>Record a completed spawn batch as one undo step for this admin.</summary>
        private static void PushUndo(long sender, List<ZDOID> batch)
        {
            if (batch == null || batch.Count == 0) return;   // nothing spawned = nothing to step back over
            PruneUndoHistory();

            if (!UndoHistory.TryGetValue(sender, out var stack))
            {
                stack = new List<List<ZDOID>>();
                UndoHistory[sender] = stack;
            }
            stack.Add(batch);
            if (stack.Count > UndoDepth) stack.RemoveAt(0);   // drop the oldest step, keep the newest UndoDepth
        }

        // Forget history for peers that are no longer connected. Peer ids are session-scoped, so without this a
        // long-lived server would accumulate a stack per admin who ever joined.
        private static void PruneUndoHistory()
        {
            if (UndoHistory.Count == 0 || ZNet.instance == null) return;
            List<long> dead = null;
            foreach (var kv in UndoHistory)
                // The host is never in ZNet.m_peers — only OnNewConnection fills it — so GetPeer() returns null
                // for our own session id. Without this exemption a host's history would be pruned on the very
                // next spawn (PushUndo prunes before it pushes) and multi-step undo would silently never work
                // in single-player, while testing fine against a dedicated server.
                if (!IsLocalHostSender(kv.Key) && ZNet.instance.GetPeer(kv.Key) == null)
                    (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var id in dead) UndoHistory.Remove(id);
        }

        private static void OnServerUndo(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;

            if (!UndoHistory.TryGetValue(sender, out var stack) || stack.Count == 0)
            {
                Log($"Admin {sender} undo: nothing left to undo");
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg", "Nothing left to undo");
                return;
            }

            var batch = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) UndoHistory.Remove(sender);

            var removed = 0;
            foreach (var id in batch)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) continue;   // already gone (killed, despawned, or undone by a world reload)
                var go = ZNetScene.instance.FindInstance(zdo);
                var nview = go != null ? go.GetComponent<ZNetView>() : null;
                if (nview != null) { nview.ClaimOwnership(); nview.Destroy(); removed++; }
                else { ZDOMan.instance.DestroyZDO(zdo); removed++; }
            }

            var left = stack.Count;
            Log($"Admin {sender} undo: removed {removed} spawned objects ({left} step(s) left)");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_Msg",
                $"Undo: removed {removed} object(s) — {left} step(s) left");
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

        // ---------- server: remove items from a player's inventory (admin moderation) ----------
        private static void OnServerInvRemove(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; string itemName; int amount;
            try
            {
                targetUid = pkg.ReadLong();
                itemName = pkg.ReadString();
                amount = pkg.ReadInt();
            }
            catch (Exception e) { Log($"AP_SrvInvRemove: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(itemName) || amount <= 0) return;
            Log($"Admin {sender} removes {amount}x {itemName} from peer {targetUid}");
            var relay = new ZPackage();
            relay.Write(itemName);
            relay.Write(amount);
            relay.Write(sender);   // whom the target pushes its refreshed inventory to
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_RemoveItem", relay);
        }

        // Runs on the TARGET player's client (inventory lives with its owner). Matches items by the same
        // key OnInventoryRequest serializes (prefab name, falling back to shared name), removes up to the
        // requested amount across stacks, then pushes the fresh inventory back to the admin's viewer.
        private static void OnRemoveItem(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            string itemName; int amount; long replyTo;
            try
            {
                itemName = pkg.ReadString();
                amount = pkg.ReadInt();
                replyTo = pkg.ReadLong();
            }
            catch { return; }
            if (string.IsNullOrEmpty(itemName) || amount <= 0) return;

            var inv = player.GetInventory();
            var removed = 0;
            foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (removed >= amount) break;
                var key = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
                if (key != itemName) continue;
                // an equipped item must be unequipped first, or the player keeps a ghost-equipped
                // copy in hand (invisible in the bag, still usable) until they relog
                if (item.m_equipped) player.UnequipItem(item, false);
                var take = Mathf.Min(item.m_stack, amount - removed);
                inv.RemoveItem(item, take);
                removed += take;
            }
            if (removed > 0)
                player.Message(MessageHud.MessageType.Center, $"An admin removed {removed}x {itemName} from your inventory");
            OnInventoryRequest(sender, replyTo);   // refresh the admin's inventory viewer
        }

        // ---------- server: version handshake ----------
        // Any client may ask; the reply goes only to the asker. No admin gate needed — the version string
        // is not sensitive, and gating it would hide exactly the mismatch the panel wants to display.
        private static void OnServerVersionReq(long sender)
        {
            if (!IsDedicatedServer) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_VersionData", PluginVersion);
        }

        // ---------- server: skip to morning ----------
        // World time is server-owned (ZNet.m_netTime), so a client can't skip night by itself — the old
        // panel button only flipped a local debug flag and changed nothing. EnvMan.SkipToMorning() is the
        // game's own sleep-skip: it advances net time to the next morning for everyone.
        private static void OnServerSkipNight(long sender)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            if (EnvMan.instance == null) return;
            Log($"Admin {sender} skips to morning");
            EnvMan.instance.SkipToMorning();
        }

        // ---------- server: raise a skill on any player ----------
        private static void OnServerSkillRaise(long sender, ZPackage pkg)
        {
            if (!IsDedicatedServer || !SenderIsAdmin(sender)) return;
            long targetUid; string skillName; float amount; string note;
            try
            {
                targetUid = pkg.ReadLong();
                skillName = pkg.ReadString();
                amount = pkg.ReadSingle();
                note = pkg.ReadString();
            }
            catch (Exception e) { Log($"AP_SrvSkillRaise: malformed packet dropped ({e.Message})"); return; }
            if (string.IsNullOrEmpty(skillName)) return;
            amount = Mathf.Clamp(amount, -100f, 100f);   // one click can never exceed the whole skill range
            Log($"Admin {sender} raises {skillName} by {amount} for peer {targetUid}");
            var relay = new ZPackage();
            relay.Write(skillName);
            relay.Write(amount);
            relay.Write(note ?? "");
            ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_SkillRaise", relay);
        }

        // Runs on the TARGET player's client (skills live with their owner, like inventories).
        private static void OnSkillRaise(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null || !SenderIsServer(sender)) return;
            string skillName; float amount; string note;
            try
            {
                skillName = pkg.ReadString();
                amount = pkg.ReadSingle();
                note = pkg.ReadString();
            }
            catch { return; }
            if (string.IsNullOrEmpty(skillName)) return;
            player.GetSkills().CheatRaiseSkill(skillName, Mathf.Clamp(amount, -100f, 100f), false);
            // Only the admin's own note is shown, and only on THIS client (the routed RPC targets one
            // peer). Empty note = the change is completely silent — the admin decides what, if anything,
            // the player gets told. When a note is present it pops center-screen together with the
            // skill's NEW level, read back after the change was applied.
            if (!string.IsNullOrEmpty(note))
            {
                var levelLine = "";
                if (Enum.TryParse<Skills.SkillType>(skillName, out var st))
                    levelLine = $"\n{skillName}: {player.GetSkills().GetSkillLevel(st):0.#}";
                player.Message(MessageHud.MessageType.Center, note + levelLine);
            }
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
