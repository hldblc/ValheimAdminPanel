using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #1 item provenance stamps + dupe finder (server + client executor) ====================
    // Every item the panel CREATES gets a stamp in ItemDrop.ItemData.m_customData["ap_prov"]:
    //     "1|<serial>.<k>|<admin platform id>|<utc ticks>"
    // and every grant leaves a ledger row (append log "w9_prov": serial|adminId|adminName|targetId|targetName|prefab|
    // amount|ticks|source). Vanilla ignores custom data it does not know, keeps it through save/load, drop/pick-up and
    // container moves, and Clone() copies it - so the stamp travels with the item.
    //
    // WHERE stamps are made (the server decides; clients only apply):
    //   * Items-tab Drop (AP_SrvSpawn kind 0): the server builds the ItemDrop itself -> StampServerDrop.
    //   * Give / kits / bulk packs (AP_SrvGive -> AP_GiveItem), item forge (AP_SrvGiveEx -> AP_GiveItemEx) and offline
    //     grants (AP_VaultApply from the queue): the item is built on the TARGET's client, so the server first sends
    //     AP_ProvStamp {prefab, amount, stamp} to that client and the grant right after it (same connection, so the
    //     order holds). The target's companion keeps the stamp pending for 10 s and CompanionPlugin.AddOrDrop - the one
    //     place every grant executor puts items - applies it, one ".k" suffix per stack. Vault restores and shop
    //     deliveries also go through AddOrDrop but are never preceded by a stamp, so they stay unstamped.
    //
    // THE SCAN (AP_SrvProvScan -> AP_ProvData): one frame-spread world walk over containers (InvCodec) and ground
    // items (ItemDrop.LoadFromZDO into a scratch ItemData), plus AP_ProvInvReq to every online client that runs the
    // companion (inventories live on the clients; others are reported as unreadable). Findings:
    //   1 DUPE      the same full serial on a non-stackable item in two places (splitting a stack legitimately clones
    //               a stackable's stamp, so stackables are judged by total instead)
    //   2 OVERFLOW  more units carry one grant's serial than that grant created
    //   3 NO GRANT  an admin-made item held by a player the ledger never granted anything to
    //   4 UNKNOWN   a stamp whose serial is not in the ledger (pruned, or forged by a modified client)
    internal static class Wave9Prov
    {
        internal const string CustomKey = "ap_prov";
        private const string Log = "w9_prov";
        private const string Label = "provenance";
        private const int LedgerLoad = 20000;
        private const float PendingSeconds = 10f;
        private const float ClientWaitSeconds = 8f;
        private const int FindingCap = 120;
        private const int PrefabCap = 200;
        private const int ClientItemCap = 400;

        private static ConfigEntry<bool> _enabled;
        private static bool StampingOn => _enabled == null || _enabled.Value;

        internal static void Init()
        {
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
                _enabled = cfg.Bind("Features", "EnableItemProvenance", true,
                    "Stamp every item the admin panel creates (give, kits, bulk packs, item forge, offline grants, Items-tab drops) with a serial in the item's custom data, and keep a grant ledger, so the Provenance scan can find duplicates. Metadata only: vanilla ignores it.");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvProvScan", "moderator");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register("AP_SrvProvScan", new Action<long>(OnScanReq));
            r.Register<ZPackage>("AP_ProvStamp", OnStamp);           // server -> target client
            r.Register<ZPackage>("AP_ProvInvReq", OnInvReq);         // server -> every modded client
            r.Register<ZPackage>("AP_ProvInvData", OnInvData);       // client -> server
        }

        internal static void Tick()
        {
            PrunePending();
            if (_scan != null && _scan.WorldDone && (Time.unscaledTime >= _scan.ClientDeadline || _scan.Answered >= _scan.Asked))
                FinishScan();
        }

        // ==================== ledger ====================

        private sealed class Grant
        {
            public string Serial;
            public string AdminId, AdminName, TargetId, TargetName, Prefab, Source;
            public int Amount;
            public long Ticks;
        }

        private static Dictionary<string, Grant> _ledger;
        private static HashSet<string> _grantTargets;
        private static long _ledgerOldest;      // oldest loaded grant (UTC ticks), when the load hit LedgerLoad lines
        private static bool _ledgerTruncated;

        private static void EnsureLedger()
        {
            if (_ledger != null) return;
            _ledger = new Dictionary<string, Grant>(StringComparer.Ordinal);
            _grantTargets = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var lines = FeatureStore.Tail(Log, LedgerLoad);
                foreach (var line in lines) AddLedgerLine(line);
                // Only the newest LedgerLoad grants are in memory: a stamp OLDER than all of them may be a real grant
                // whose ledger line was simply not loaded, so it must not be called forged.
                _ledgerTruncated = lines.Count >= LedgerLoad;
                _ledgerOldest = long.MaxValue;
                foreach (var g in _ledger.Values) if (g.Ticks > 0L && g.Ticks < _ledgerOldest) _ledgerOldest = g.Ticks;
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Provenance ledger could not be read: {e.Message}"); }
        }

        private static void AddLedgerLine(string line)
        {
            var p = Wave9.Unpack(line, 9);
            if (p[0].Length == 0) return;
            var g = new Grant
            {
                Serial = p[0], AdminId = p[1], AdminName = p[2], TargetId = p[3], TargetName = p[4], Prefab = p[5],
                Amount = Wave9.ParseInt(p[6], 0), Ticks = Wave9.ParseLong(p[7], 0L), Source = p[8],
            };
            _ledger[g.Serial] = g;
            if (g.TargetId.Length > 0) { _grantTargets.Add(g.TargetId); _grantTargets.Add(CompanionPlugin.FeatureBareId(g.TargetId)); }
        }

        private static string Record(long sender, string adminName, string targetId, string targetName, string prefab, int amount, string source)
        {
            EnsureLedger();
            var serial = Guid.NewGuid().ToString("N").Substring(0, 12);
            var adminId = sender != 0L ? CompanionPlugin.SenderPlatformId(sender) : "queue";
            var ticks = DateTime.UtcNow.Ticks;
            var line = Wave9.Pack(serial, adminId, adminName, targetId, targetName, prefab, Wave9.I(amount), Wave9.I(ticks), source);
            try { FeatureStore.Append(Log, line); } catch (Exception) { }
            AddLedgerLine(line);
            return "1|" + serial + "|" + adminId + "|" + Wave9.I(ticks);
        }

        // ==================== stamping (server side) ====================

        /// <summary>
        /// Called by every relayed grant just before the item-creating RPC is sent to <paramref name="targetUid"/>.
        /// Returns the grant's serial ("" when nothing was stamped) so the undo timeline can take back exactly these items.
        /// </summary>
        internal static string BeforeGrant(long sender, long targetUid, string prefab, int amount, string source)
        {
            if (!StampingOn || !Wave9.IsServer || targetUid == 0L || string.IsNullOrEmpty(prefab) || amount <= 0) return "";
            try
            {
                var peer = Wave9.PeerByUid(targetUid);
                string targetId, targetName;
                if (peer != null) { targetId = Wave9.PeerId(peer); targetName = peer.m_playerName ?? "?"; }
                else if (Wave9.IsHostUid(targetUid)) { targetId = "HOST"; targetName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "host"; }
                else return "";
                var adminName = sender != 0L ? CompanionPlugin.SenderDisplayName(sender) : "offline queue";
                var stamp = Record(sender, adminName, targetId, targetName, prefab, amount, source);
                var pkg = new ZPackage();
                pkg.Write(Wave9.Ver);
                pkg.Write(prefab);
                pkg.Write(amount);
                pkg.Write(stamp);
                ZRoutedRpc.instance.InvokeRoutedRPC(targetUid, "AP_ProvStamp", pkg);
                return SerialOf(stamp);
            }
            catch (Exception e) { Wave9.ThrottledLog("prov-before", $"Provenance stamp for {prefab} failed (the grant still goes out): {e.Message}"); return ""; }
        }

        internal static string SerialOf(string stamp)
        {
            var parts = (stamp ?? "").Split('|');
            return parts.Length >= 2 ? parts[1] : "";
        }

        // "1|serial|admin|ticks" + stack k -> "1|serial.k|admin|ticks": every stack of one grant gets its own full serial,
        // so two swords from one grant are two serials, never a "duplicate".
        internal static string WithIndex(string stamp, int k)
        {
            var parts = (stamp ?? "").Split('|');
            if (parts.Length < 2) return stamp;
            parts[1] = parts[1] + "." + k.ToString(CultureInfo.InvariantCulture);
            return string.Join("|", parts);
        }

        /// <summary>Items-tab Drop: the server builds the ItemDrop itself, so it stamps it directly (saved by ItemDrop.Start).</summary>
        internal static void StampServerDrop(long sender, ItemDrop.ItemData data, string prefab)
        {
            if (!StampingOn || data == null) return;
            try
            {
                var stamp = Record(sender, CompanionPlugin.SenderDisplayName(sender), "ground", "ground", prefab, Math.Max(1, data.m_stack), "drop");
                if (data.m_customData == null) data.m_customData = new Dictionary<string, string>();
                data.m_customData[CustomKey] = WithIndex(stamp, 1);
            }
            catch (Exception) { }
        }

        // ==================== stamping (target client side) ====================

        private sealed class Pending
        {
            public string Prefab;
            public string Stamp;
            public int Remaining;
            public int Index;
            public float Expires;
        }

        private static readonly List<Pending> PendingStamps = new List<Pending>();

        private static void OnStamp(long sender, ZPackage pkg)
        {
            if (!Wave9.FromServer(sender)) return;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                var prefab = pkg.ReadString();
                var amount = pkg.ReadInt();
                var stamp = pkg.ReadString();
                if (string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(stamp) || amount <= 0) return;
                if (PendingStamps.Count >= 64) PendingStamps.RemoveAt(0);
                PendingStamps.Add(new Pending { Prefab = prefab, Stamp = Wave9.Clean(stamp, 96), Remaining = amount, Expires = Time.unscaledTime + PendingSeconds });
            }
            catch (Exception) { }
        }

        private static void PrunePending()
        {
            if (PendingStamps.Count == 0) return;
            var now = Time.unscaledTime;
            PendingStamps.RemoveAll(p => p.Expires < now || p.Remaining <= 0);
        }

        /// <summary>Called by CompanionPlugin.AddOrDrop for every stack a grant executor creates.</summary>
        internal static void ApplyPending(ItemDrop.ItemData data)
        {
            if (data == null || PendingStamps.Count == 0 || data.m_dropPrefab == null) return;
            try
            {
                var name = Utils.GetPrefabName(data.m_dropPrefab);
                var now = Time.unscaledTime;
                for (var i = 0; i < PendingStamps.Count; i++)
                {
                    var p = PendingStamps[i];
                    if (p.Remaining <= 0 || p.Expires < now || p.Prefab != name) continue;
                    if (data.m_customData == null) data.m_customData = new Dictionary<string, string>();
                    data.m_customData[CustomKey] = WithIndex(p.Stamp, ++p.Index);
                    p.Remaining -= Math.Max(1, data.m_stack);
                    if (p.Remaining <= 0) PendingStamps.RemoveAt(i);
                    return;
                }
            }
            catch (Exception) { }
        }

        // ==================== client: report my stamped items ====================

        private static void OnInvReq(long sender, ZPackage pkg)
        {
            if (!Wave9.FromServer(sender)) return;
            var player = Player.m_localPlayer;
            if (player == null) return;
            int token;
            try { if (pkg.ReadInt() != Wave9.Ver) return; token = pkg.ReadInt(); }
            catch (Exception) { return; }
            var rows = new List<ItemDrop.ItemData>();
            var total = 0;
            try
            {
                foreach (var it in player.GetInventory().GetAllItems())
                {
                    total++;
                    if (it?.m_customData != null && it.m_customData.ContainsKey(CustomKey)) rows.Add(it);
                }
            }
            catch (Exception) { }
            var reply = new ZPackage();
            reply.Write(Wave9.Ver);
            reply.Write(token);
            reply.Write(total);
            var n = Math.Min(rows.Count, ClientItemCap);
            reply.Write(n);
            for (var i = 0; i < n; i++)
            {
                var it = rows[i];
                reply.Write(it.m_dropPrefab != null ? Utils.GetPrefabName(it.m_dropPrefab) : "?");
                reply.Write(it.m_customData[CustomKey] ?? "");
                reply.Write(it.m_stack);
                reply.Write(it.m_shared != null ? it.m_shared.m_maxStackSize : 1);
            }
            try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, "AP_ProvInvData", reply); } catch (Exception) { }
        }

        // ==================== the scan ====================

        private sealed class Holding
        {
            public string Stamp;       // full "1|serial.k|admin|ticks"
            public string Serial;      // "serial.k"
            public string BaseSerial;  // "serial"
            public string Prefab;
            public int Stack;
            public int MaxStack;
            public string Where;
            public string HolderId;    // platform id for player holdings, "" otherwise
            public Vector3 Pos;
        }

        private sealed class ScanState
        {
            public long Requester;
            public int Token;
            public bool WorldDone;
            public bool Aborted;
            public long WorldMs;
            public float ClientDeadline;
            public int Asked, Answered, Unreadable;
            public readonly HashSet<long> AskedUids = new HashSet<long>();
            public readonly List<Holding> Holdings = new List<Holding>();
            public readonly List<InvCodec.Rec> Scratch = new List<InvCodec.Rec>();
        }

        private static ScanState _scan;
        private static int _token;
        private static readonly Dictionary<int, int> MaxStackCache = new Dictionary<int, int>();

        private static void OnScanReq(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvProvScan")) return;
            if (_scan != null) { Wave9.Tell(sender, "A provenance scan is already running - its result will arrive when it finishes."); return; }
            EnsureLedger();
            var s = new ScanState { Requester = sender, Token = ++_token };
            _scan = s;

            foreach (var peer in Wave9.ReadyPeers())
            {
                if (Wave34Core.HasMod(peer.m_uid) != true) { s.Unreadable++; continue; }
                var req = new ZPackage();
                req.Write(Wave9.Ver);
                req.Write(s.Token);
                try { ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "AP_ProvInvReq", req); s.Asked++; s.AskedUids.Add(peer.m_uid); }
                catch (Exception) { s.Unreadable++; }
            }
            if (Player.m_localPlayer != null && ZNet.instance != null && !ZNet.instance.IsDedicated())
                AddHostInventory(s);

            if (!Wave9Scan.Start(Label, sender, Visit, job =>
            {
                if (_scan == null) return;
                _scan.WorldDone = true;
                _scan.Aborted = job.Aborted;
                _scan.WorldMs = job.Millis;
                _scan.ClientDeadline = Time.unscaledTime + ClientWaitSeconds;
            }))
                _scan = null;
            else Wave9.Tell(sender, $"Provenance scan started: walking the world and asking {s.Asked} modded client(s) for their stamped items.");
        }

        private static void AddHostInventory(ScanState s)
        {
            try
            {
                var p = Player.m_localPlayer;
                foreach (var it in p.GetInventory().GetAllItems())
                {
                    string stamp;
                    if (it?.m_customData == null || !it.m_customData.TryGetValue(CustomKey, out stamp)) continue;
                    AddHolding(s, stamp, it.m_dropPrefab != null ? Utils.GetPrefabName(it.m_dropPrefab) : "?", it.m_stack,
                        it.m_shared != null ? it.m_shared.m_maxStackSize : 1, p.GetPlayerName() + " (host)", "HOST", p.transform.position);
                }
            }
            catch (Exception) { }
        }

        private static void Visit(ZDO zdo)
        {
            var s = _scan;
            if (s == null) return;
            var hash = zdo.GetPrefab();
            var kind = Wave9.KindOf(hash);
            if ((kind & Wave9.KContainer) != 0)
            {
                var blob = InvCodec.ReadBlob(zdo);
                if (blob == null) return;
                int version, declared;
                if (!InvCodec.Parse(blob, out version, out declared, s.Scratch)) return;
                foreach (var r in s.Scratch)
                {
                    var stamp = r.CustomValue(CustomKey);
                    if (stamp == null) continue;
                    var pos = zdo.GetPosition();
                    AddHolding(s, stamp, r.Name, r.Stack, MaxStackOf(r.PrefabHash), Wave9.PrefabName(hash) + " @ " + Wave9.F(pos.x) + "," + Wave9.F(pos.z), "", pos);
                }
                s.Scratch.Clear();
            }
            else if ((kind & Wave9.KItemDrop) != 0)
            {
                var bytes = zdo.GetByteArray(Wave9.KeyItemData);
                if (bytes == null || bytes.Length < 3) return;
                var it = new ItemDrop.ItemData();
                ItemDrop.LoadFromZDO(it, zdo);
                string stamp;
                if (it.m_customData == null || !it.m_customData.TryGetValue(CustomKey, out stamp)) return;
                var pos = zdo.GetPosition();
                AddHolding(s, stamp, Wave9.PrefabName(hash), it.m_stack, MaxStackOf(hash), "ground @ " + Wave9.F(pos.x) + "," + Wave9.F(pos.z), "", pos);
            }
        }

        private static int MaxStackOf(int hash)
        {
            int m;
            if (MaxStackCache.TryGetValue(hash, out m)) return m;
            m = 1;
            try
            {
                var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
                var d = go != null ? go.GetComponent<ItemDrop>() : null;
                if (d != null) m = Math.Max(1, d.m_itemData.m_shared.m_maxStackSize);
            }
            catch (Exception) { }
            MaxStackCache[hash] = m;
            return m;
        }

        private static long StampTicks(string stamp)
        {
            var p = (stamp ?? "").Split('|');
            return p.Length >= 4 ? Wave9.ParseLong(p[3], 0L) : 0L;
        }

        private static void AddHolding(ScanState s, string stamp, string prefab, int stack, int maxStack, string where, string holderId, Vector3 pos)
        {
            var parts = (stamp ?? "").Split('|');
            if (parts.Length < 2 || parts[1].Length == 0) return;
            var serial = parts[1];
            var dot = serial.LastIndexOf('.');
            s.Holdings.Add(new Holding
            {
                Stamp = stamp, Serial = serial, BaseSerial = dot > 0 ? serial.Substring(0, dot) : serial,
                Prefab = prefab ?? "?", Stack = Math.Max(1, stack), MaxStack = Math.Max(1, maxStack), Where = where, HolderId = holderId ?? "", Pos = pos,
            });
        }

        private static void OnInvData(long sender, ZPackage pkg)
        {
            var s = _scan;
            if (s == null || !Wave9.IsServer || !s.AskedUids.Remove(sender)) return;   // only peers we asked, once each
            var peer = Wave9.PeerByUid(sender);
            if (peer == null) return;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                if (pkg.ReadInt() != s.Token) return;
                pkg.ReadInt();   // total items (unused)
                var n = pkg.ReadInt();
                if (n < 0 || n > ClientItemCap) return;
                var pos = Wave9.PeerPos(peer);
                for (var i = 0; i < n; i++)
                {
                    var prefab = Wave9.CleanName(pkg.ReadString(), 64);
                    var stamp = Wave9.Clean(pkg.ReadString(), 120);
                    var stack = Mathf.Clamp(pkg.ReadInt(), 1, 100000);      // the player's own client says so: bounded
                    var maxStack = Mathf.Clamp(pkg.ReadInt(), 1, 100000);
                    AddHolding(s, stamp, prefab, stack, maxStack, (peer.m_playerName ?? "?") + " (inventory, reported by their client)", Wave9.PeerId(peer), pos);
                }
                s.Answered++;
            }
            catch (Exception) { }
        }

        private sealed class Finding
        {
            public byte Kind;
            public string Serial, Prefab, Where, Grant;
            public Vector3 Pos;
        }

        private static void FinishScan()
        {
            var s = _scan;
            _scan = null;
            if (s == null) return;
            if (s.Aborted) { Wave9.Tell(s.Requester, "Provenance scan aborted (world changed)."); return; }

            var findings = new List<Finding>();
            var prefabs = new HashSet<string>(StringComparer.Ordinal);
            var byFull = new Dictionary<string, List<Holding>>(StringComparer.Ordinal);
            var byBase = new Dictionary<string, List<Holding>>(StringComparer.Ordinal);
            foreach (var h in s.Holdings)
            {
                prefabs.Add(h.Prefab);
                List<Holding> l;
                if (!byFull.TryGetValue(h.Serial, out l)) byFull[h.Serial] = l = new List<Holding>();
                l.Add(h);
                if (!byBase.TryGetValue(h.BaseSerial, out l)) byBase[h.BaseSerial] = l = new List<Holding>();
                l.Add(h);
            }

            foreach (var kv in byFull)
            {
                var l = kv.Value;
                if (l.Count < 2 || l[0].MaxStack > 1) continue;
                foreach (var h in l) findings.Add(Make(1, h));
            }
            foreach (var kv in byBase)
            {
                Grant g;
                long sum = 0;   // client-reported stacks are untrusted: never let them overflow
                foreach (var h in kv.Value) sum += h.Stack;
                if (!_ledger.TryGetValue(kv.Key, out g))
                {
                    if (_ledgerTruncated && StampTicks(kv.Value[0].Stamp) < _ledgerOldest) continue;   // older than the loaded ledger: cannot judge
                    findings.Add(Make(4, kv.Value[0]));
                    continue;
                }
                if (g.Amount > 0 && sum > g.Amount && kv.Value[0].MaxStack > 1)
                {
                    var f = Make(2, kv.Value[0]);
                    f.Where = $"{sum} units carry this serial; the grant made {g.Amount}";
                    findings.Add(f);
                }
                foreach (var h in kv.Value)
                {
                    if (h.HolderId.Length == 0 || h.HolderId == "HOST") continue;
                    if (_grantTargets.Contains(h.HolderId) || _grantTargets.Contains(CompanionPlugin.FeatureBareId(h.HolderId))) continue;
                    findings.Add(Make(3, h));
                }
            }

            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(s.WorldMs);
            pkg.Write(s.Holdings.Count);
            pkg.Write(s.Asked);
            pkg.Write(s.Answered);
            pkg.Write(s.Unreadable);
            var n = Math.Min(findings.Count, FindingCap);
            pkg.Write(findings.Count);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var f = findings[i];
                pkg.Write(f.Kind);
                pkg.Write(f.Serial ?? "");
                pkg.Write(f.Prefab ?? "");
                pkg.Write(f.Where ?? "");
                pkg.Write(f.Pos);
                pkg.Write(f.Grant ?? "");
            }
            var names = new List<string>(prefabs);
            names.Sort(StringComparer.Ordinal);
            var np = Math.Min(names.Count, PrefabCap);
            pkg.Write(np);
            for (var i = 0; i < np; i++) pkg.Write(names[i]);
            Wave9.Reply(s.Requester, "AP_ProvData", pkg);
            CompanionPlugin.SrvAudit(s.Requester, "ProvScan", $"{s.Holdings.Count} stamped stack(s), {findings.Count} finding(s), {s.Answered}/{s.Asked} clients answered, {s.Unreadable} unreadable");
        }

        private static Finding Make(byte kind, Holding h)
        {
            Grant g;
            var grant = "";
            if (_ledger != null && _ledger.TryGetValue(h.BaseSerial, out g))
                grant = $"{g.Source} by {g.AdminName} to {g.TargetName} x{g.Amount} " +
                        new DateTime(g.Ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
            return new Finding { Kind = kind, Serial = h.Serial, Prefab = h.Prefab, Where = h.Where, Pos = h.Pos, Grant = grant };
        }
    }
}
