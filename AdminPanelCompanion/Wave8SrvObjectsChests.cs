using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #16 chest viewer and container search (server side) ====================
    // A container's contents live in its ZDO as the string "items": a base64 ZPackage written by
    // Inventory.Save (Inventory.cs:720-752) and read by Inventory.Load (754-838). The engine's Inventory is
    // deliberately NOT used to decode or re-encode that blob here:
    //   * Inventory.Load drops every entry whose prefab ObjectDB does not know (Inventory.cs:AddItem private
    //     overload: "Failed to find item prefab" -> return false) and clamps each stack to the prefab's
    //     m_maxStackSize. On a server that lacks a client-side item mod, a decode + re-encode round trip
    //     would silently DELETE those items from the chest.
    //   * It also instantiates the item prefab for every entry, which is pointless on a server.
    // Instead the blob is parsed field for field with the same version rules as Inventory.Load (version
    // 106 = every field; older versions gate quality/variant/crafter/custom data/world level/pickedUp on
    // the version), edited as plain records, and re-encoded in the current 106 layout — byte-identical to
    // what Inventory.Save writes for the same data, and lossless for entries the server cannot resolve.
    // A blob newer than 106 is reported and never edited.
    //
    //   AP_SrvChestReadReq   {int ver, ZDOID id}
    //   AP_ChestData         {int ver, ZDOID id, bool found, string prefab, string nameToken, int w, int h,
    //                         bool inUse, int blobVersion, int declared, int reason, int shipped(<=100),
    //                         shipped x (string itemToken, string itemPrefab, int stack, int quality,
    //                                    int variant, float durability, string crafter)}
    //   AP_SrvChestEdit      {int ver, ZDOID id, int op(0 remove, 1 set count, 2 add), int index, int count,
    //                         string prefab, int quality}                      -> AP_ChestData (refreshed)
    //   AP_SrvChestSearchReq {int ver, string query, Vector3 origin, bool startNew}
    //   AP_ChestSearch       {int ver, bool running, int scanned, int total, int containers, int matched,
    //                         string query, int shipped(<=50),
    //                         shipped x (ZDOID id, string prefab, Vector3 pos, int count, string sample,
    //                                    string nearestPlayer, float dist), long scanMillis}
    internal static partial class Wave8Objects
    {
        private const int BlobVersionKnown = 106;   // the layout Inventory.Save writes on this build
        private const int BlobItemCap = 4096;       // sanity bound on the declared entry count
        private const int CustomDataCap = 256;

        private const int ReasonOk = 0;
        private const int ReasonInUse = 1;
        private const int ReasonDisabled = 2;
        private const int ReasonUnreadable = 3;
        private const int ReasonNewer = 4;
        private const int ReasonMissing = 5;

        // One inventory entry, exactly the fields Inventory.Save persists.
        private sealed class ChestItem
        {
            public string Prefab = "";
            public int Stack = 1;
            public float Durability = 100f;
            public Vector2i Pos;
            public bool Equipped;
            public int Quality = 1;
            public int Variant;
            public long CrafterId;
            public string CrafterName = "";
            public List<KeyValuePair<string, string>> Custom;   // null = none
            public int WorldLevel;
            public bool PickedUp;
        }

        private sealed class SearchRow
        {
            public ZDOID Id;
            public string Prefab = "";
            public Vector3 Pos;
            public int Count;
            public string Sample = "";
            public float Dist;
            public string Nearest = "";
        }

        private static readonly List<ChestItem> ChestScratch = new List<ChestItem>();
        private static readonly List<ChestItem> SearchItemScratch = new List<ChestItem>();

        // ==================== blob codec ====================

        // Mirrors Inventory.Load field for field. Returns false when the blob cannot be read; `version` is
        // still set when the header was readable so the caller can tell "newer format" from "garbage".
        private static bool ParseChestBlob(string b64, out int version, out int declared, List<ChestItem> items)
        {
            version = 0;
            declared = 0;
            items.Clear();
            if (string.IsNullOrEmpty(b64)) return true;   // an empty string is an empty chest
            try
            {
                var pkg = new ZPackage(b64);
                version = pkg.ReadInt();
                declared = pkg.ReadInt();
                if (declared < 0 || declared > BlobItemCap) return false;
                if (version > BlobVersionKnown) return false;   // unknown trailing fields would desync the parse
                for (var i = 0; i < declared; i++)
                {
                    var it = new ChestItem();
                    it.Prefab = pkg.ReadString() ?? "";
                    it.Stack = pkg.ReadInt();
                    it.Durability = pkg.ReadSingle();
                    it.Pos = pkg.ReadVector2i();
                    it.Equipped = pkg.ReadBool();
                    if (version >= 101) it.Quality = pkg.ReadInt();
                    if (version >= 102) it.Variant = pkg.ReadInt();
                    if (version >= 103)
                    {
                        it.CrafterId = pkg.ReadLong();
                        it.CrafterName = pkg.ReadString() ?? "";
                    }
                    if (version >= 104)
                    {
                        var n = pkg.ReadInt();
                        if (n < 0 || n > CustomDataCap) return false;
                        if (n > 0)
                        {
                            it.Custom = new List<KeyValuePair<string, string>>(n);
                            for (var j = 0; j < n; j++)
                            {
                                var key = pkg.ReadString() ?? "";
                                var val = pkg.ReadString() ?? "";
                                it.Custom.Add(new KeyValuePair<string, string>(key, val));
                            }
                        }
                    }
                    if (version >= 105) it.WorldLevel = pkg.ReadInt();
                    if (version >= 106) it.PickedUp = pkg.ReadBool();
                    // Inventory.Load skips nameless entries (a prefab that was already missing when the chest
                    // was last saved); they carry nothing worth keeping.
                    if (it.Prefab.Length > 0) items.Add(it);
                }
                return true;
            }
            catch (Exception)
            {
                items.Clear();
                return false;
            }
        }

        // Current-layout writer (Inventory.Save, version 106). Entries parsed from an older blob are
        // upgraded with exactly the defaults Inventory.Load would have assigned them.
        private static string SerializeChestBlob(List<ChestItem> items)
        {
            var pkg = new ZPackage();
            pkg.Write(BlobVersionKnown);
            pkg.Write(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                pkg.Write(it.Prefab ?? "");
                pkg.Write(it.Stack);
                pkg.Write(it.Durability);
                pkg.Write(it.Pos);
                pkg.Write(it.Equipped);
                pkg.Write(it.Quality);
                pkg.Write(it.Variant);
                pkg.Write(it.CrafterId);
                pkg.Write(it.CrafterName ?? "");
                var n = it.Custom != null ? it.Custom.Count : 0;
                pkg.Write(n);
                for (var j = 0; j < n; j++)
                {
                    pkg.Write(it.Custom[j].Key ?? "");
                    pkg.Write(it.Custom[j].Value ?? "");
                }
                pkg.Write(it.WorldLevel);
                pkg.Write(it.PickedUp);
            }
            return pkg.GetBase64();
        }

        // ==================== item metadata (ObjectDB, cached per prefab name) ====================

        private sealed class ItemMeta
        {
            public bool Known;
            public string Token = "";          // m_shared.m_name ("$item_sword_iron") — the panel localizes it
            public string TokenLower = "";     // "item_sword_iron", for substring matching
            public string LocalizedLower = ""; // English name when the server can localize, else ""
            public int MaxStack = 1;
            public int MaxQuality = 1;
            public float MaxDurability = 100f;
            public float DurabilityPerLevel;
        }

        private static readonly ItemMeta UnknownMeta = new ItemMeta();
        private static readonly Dictionary<string, ItemMeta> MetaByPrefab = new Dictionary<string, ItemMeta>(StringComparer.Ordinal);
        private static object _metaDb;
        private static bool _locBroken;

        private static ItemMeta MetaOf(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return UnknownMeta;
            var db = ObjectDB.instance;
            if (db == null) return UnknownMeta;
            if (!ReferenceEquals(_metaDb, db)) { MetaByPrefab.Clear(); _metaDb = db; }
            ItemMeta m;
            if (MetaByPrefab.TryGetValue(prefab, out m)) return m;
            m = new ItemMeta();
            try
            {
                var go = db.GetItemPrefab(prefab);
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                var shared = drop != null && drop.m_itemData != null ? drop.m_itemData.m_shared : null;
                if (shared != null)
                {
                    m.Known = true;
                    m.Token = shared.m_name ?? "";
                    m.TokenLower = m.Token.TrimStart('$').ToLowerInvariant();
                    m.MaxStack = Math.Max(1, shared.m_maxStackSize);
                    m.MaxQuality = Math.Max(1, shared.m_maxQuality);
                    m.MaxDurability = shared.m_maxDurability;
                    m.DurabilityPerLevel = shared.m_durabilityPerLevel;
                    m.LocalizedLower = LocalizeLower(m.Token);
                }
            }
            catch (Exception) { }
            MetaByPrefab[prefab] = m;
            return m;
        }

        // Best-effort English name for matching. Localization lives in assembly_guiutils, which the
        // companion deliberately does not reference (it must load on a bare dedicated server), so it is
        // resolved by reflection once; a dedicated server may or may not carry the tables, and the first
        // failure disables further attempts — matching then falls back to prefab + token, which still
        // covers "sword", "ore", "wolf" and the like.
        private static System.Reflection.MethodInfo _locLocalize;
        private static object _locInstance;
        private static bool _locResolved;

        private static string LocalizeLower(string token)
        {
            if (_locBroken || string.IsNullOrEmpty(token)) return "";
            try
            {
                if (!_locResolved)
                {
                    _locResolved = true;
                    var t = HarmonyLib.AccessTools.TypeByName("Localization");
                    var prop = t != null ? HarmonyLib.AccessTools.Property(t, "instance") : null;
                    _locInstance = prop != null ? prop.GetValue(null, null) : null;
                    _locLocalize = t != null ? HarmonyLib.AccessTools.Method(t, "Localize", new[] { typeof(string) }) : null;
                }
                if (_locInstance == null || _locLocalize == null) { _locBroken = true; return ""; }
                var s = _locLocalize.Invoke(_locInstance, new object[] { token }) as string;
                if (string.IsNullOrEmpty(s) || s.StartsWith("[", StringComparison.Ordinal)) return "";
                return s.ToLowerInvariant();
            }
            catch (Exception)
            {
                _locBroken = true;
                return "";
            }
        }

        // ==================== AP_SrvChestReadReq ====================

        private static void OnChestReadReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvChestReadReq")) return;

            int ver; ZDOID id;
            try
            {
                ver = pkg.ReadInt();
                id = pkg.ReadZDOID();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvChestReadReq: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            SendChestData(sender, id);
        }

        private static void SendChestData(long uid, ZDOID id)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(id);

            var man = ZDOMan.instance;
            var zdo = man != null ? man.GetZDO(id) : null;
            PrefabInfo info = null;
            try { if (zdo != null && zdo.IsValid()) info = InfoOf(zdo.GetPrefab()); }
            catch (Exception) { info = null; }
            var found = info != null && info.HasContainer;
            pkg.Write(found);
            if (!found)
            {
                // Same field layout as the found case so the panel reads one shape.
                pkg.Write(""); pkg.Write(""); pkg.Write(0); pkg.Write(0);
                pkg.Write(false); pkg.Write(0); pkg.Write(0); pkg.Write(ReasonMissing);
                pkg.Write(0);
                Reply(uid, "AP_ChestData", pkg);
                return;
            }

            var blob = zdo.GetString(KeyItems, "") ?? "";
            var inUse = zdo.GetInt(KeyInUse, 0) == 1;
            int version, declared;
            var ok = ParseChestBlob(blob, out version, out declared, ChestScratch);
            var reason = ReasonOk;
            if (!ok) reason = version > BlobVersionKnown ? ReasonNewer : ReasonUnreadable;
            else if (inUse) reason = ReasonInUse;
            else if (!ChestEditEnabled) reason = ReasonDisabled;

            pkg.Write(info.Name ?? "");
            pkg.Write(info.ContainerName ?? "");
            pkg.Write(info.ContainerW);
            pkg.Write(info.ContainerH);
            pkg.Write(inUse);
            pkg.Write(version);
            pkg.Write(declared);
            pkg.Write(reason);
            var n = Math.Min(ChestScratch.Count, ChestRowCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var it = ChestScratch[i];
                var meta = MetaOf(it.Prefab);
                pkg.Write(meta.Token ?? "");
                pkg.Write(it.Prefab ?? "");
                pkg.Write(it.Stack);
                pkg.Write(it.Quality);
                pkg.Write(it.Variant);
                pkg.Write(it.Durability);
                pkg.Write(Clean(it.CrafterName, 32));
            }
            ChestScratch.Clear();
            Reply(uid, "AP_ChestData", pkg);
        }

        // ==================== AP_SrvChestEdit ====================

        private static void OnChestEdit(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvChestEdit")) return;

            int ver, op, index, count, quality; ZDOID id; string prefab;
            try
            {
                ver = pkg.ReadInt();
                id = pkg.ReadZDOID();
                op = pkg.ReadInt();
                index = pkg.ReadInt();
                count = pkg.ReadInt();
                prefab = pkg.ReadString();
                quality = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvChestEdit: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (op < 0 || op > 2) return;

            if (!ChestEditEnabled)
            {
                CompanionPlugin.NotifySender(sender, "Container edits are disabled in the server config (EnableChestEdit=false).");
                SendChestData(sender, id);
                return;
            }
            var man = ZDOMan.instance;
            var zdo = man != null ? man.GetZDO(id) : null;
            var info = zdo != null && zdo.IsValid() ? InfoOf(zdo.GetPrefab()) : null;
            if (info == null || !info.HasContainer)
            {
                CompanionPlugin.NotifySender(sender, "That container no longer exists (or is not a container) - aim and read again.");
                SendChestData(sender, id);
                return;
            }
            // The owning client writes the whole blob back when the GUI closes, which would overwrite any
            // edit made meanwhile — and a player would watch items appear or vanish under the cursor.
            if (zdo.GetInt(KeyInUse, 0) == 1)
            {
                CompanionPlugin.NotifySender(sender, "Refused: a player has that container open right now. Try again when it is closed.");
                SendChestData(sender, id);
                return;
            }
            int version, declared;
            var items = ChestScratch;
            if (!ParseChestBlob(zdo.GetString(KeyItems, "") ?? "", out version, out declared, items))
            {
                CompanionPlugin.NotifySender(sender, version > BlobVersionKnown
                    ? $"Refused: this container uses inventory format {version}, newer than this companion knows (up to {BlobVersionKnown}). Update the companion."
                    : "Refused: the container's item data could not be decoded, so it is left untouched.");
                SendChestData(sender, id);
                return;
            }

            string detail;
            string note = "";
            switch (op)
            {
                case 0:
                    if (index < 0 || index >= items.Count)
                    {
                        CompanionPlugin.NotifySender(sender, "That stack is no longer there - read the container again.");
                        items.Clear();
                        SendChestData(sender, id);
                        return;
                    }
                    var removed = items[index];
                    items.RemoveAt(index);
                    detail = $"remove|{removed.Prefab}x{removed.Stack}";
                    break;
                case 1:
                    if (index < 0 || index >= items.Count)
                    {
                        CompanionPlugin.NotifySender(sender, "That stack is no longer there - read the container again.");
                        items.Clear();
                        SendChestData(sender, id);
                        return;
                    }
                    var target = items[index];
                    var meta1 = MetaOf(target.Prefab);
                    var cap = meta1.Known ? meta1.MaxStack : Math.Max(1, count);
                    var newCount = Mathf.Clamp(count, 1, cap);
                    if (count > cap) note = $" (clamped to the max stack of {cap})";
                    detail = $"count|{target.Prefab}|{target.Stack}->{newCount}";
                    target.Stack = newCount;
                    break;
                default:
                    prefab = Clean(prefab, 64);
                    var meta2 = MetaOf(prefab);
                    if (!meta2.Known)
                    {
                        CompanionPlugin.NotifySender(sender, $"Refused: '{prefab}' is not an item prefab on this server.");
                        items.Clear();
                        SendChestData(sender, id);
                        return;
                    }
                    Vector2i slot;
                    if (!FindFreeSlot(items, info.ContainerW, info.ContainerH, out slot))
                    {
                        CompanionPlugin.NotifySender(sender, "Refused: that container has no free slot.");
                        items.Clear();
                        SendChestData(sender, id);
                        return;
                    }
                    var q = Mathf.Clamp(quality, 1, meta2.MaxQuality);
                    var c = Mathf.Clamp(count, 1, meta2.MaxStack);
                    if (count > meta2.MaxStack) note = $" (one stack holds at most {meta2.MaxStack})";
                    items.Add(new ChestItem
                    {
                        Prefab = prefab,
                        Stack = c,
                        Quality = q,
                        Variant = 0,
                        // ItemData.GetMaxDurability(quality): base + (quality-1) * per-level (ItemDrop.cs:519-522)
                        Durability = meta2.MaxDurability + Math.Max(0, q - 1) * meta2.DurabilityPerLevel,
                        Pos = slot,
                        WorldLevel = Game.m_worldLevel,
                    });
                    detail = $"add|{prefab}x{c}|q{q}|slot={slot.x},{slot.y}";
                    break;
            }

            try
            {
                var b64 = SerializeChestBlob(items);
                items.Clear();
                var view = LocalView(zdo);
                if (view != null) view.ClaimOwnership();     // listen-server host: keep the live instance in step
                var prev = ClaimForWrite(zdo);
                zdo.Set(KeyItems, b64);
                if (view != null) { try { man.ForceSendZDO(zdo.m_uid); } catch (Exception) { } }
                else HandBack(zdo, prev);
            }
            catch (Exception e)
            {
                items.Clear();
                CompanionPlugin.FeatureLog($"AP_SrvChestEdit write failed on {id}: {e.Message}");
                CompanionPlugin.NotifySender(sender, "The edit failed on the server - see the server log.");
                SendChestData(sender, id);
                return;
            }

            var admin = Wave1AuditRpc.AdminLabel(sender);
            var line = $"{id}|{info.Name}|pos={zdo.GetPosition()}|{detail}";
            CompanionPlugin.SrvAudit(sender, "CHEST_EDIT", line);
            CompanionPlugin.FeatureLog($"Container edit by {admin}: {line}");
            CompanionPlugin.NotifySender(sender, $"Container updated: {detail.Replace('|', ' ')}{note}.");
            SendChestData(sender, id);
        }

        // First free grid cell, row by row, the way Inventory.FindEmptySlot fills a chest.
        private static bool FindFreeSlot(List<ChestItem> items, int w, int h, out Vector2i slot)
        {
            w = Mathf.Clamp(w, 1, 64);
            h = Mathf.Clamp(h, 1, 64);
            var used = new HashSet<int>();
            for (var i = 0; i < items.Count; i++)
            {
                var p = items[i].Pos;
                if (p.x >= 0 && p.y >= 0 && p.x < w && p.y < h) used.Add(p.y * w + p.x);
            }
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (!used.Contains(y * w + x)) { slot = new Vector2i(x, y); return true; }
            slot = new Vector2i(-1, -1);
            return false;
        }

        // ==================== AP_SrvChestSearchReq ====================

        private static void OnChestSearchReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvChestSearchReq")) return;

            int ver; string query; Vector3 origin; bool startNew;
            try
            {
                ver = pkg.ReadInt();
                query = pkg.ReadString();
                origin = pkg.ReadVector3();
                startNew = pkg.ReadBool();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvChestSearchReq: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (!PosOk(origin)) origin = Vector3.zero;

            if (_job != null)
            {
                if (_job.Kind == KindSearch) { SendSearch(_job, sender, true); return; }
                CompanionPlugin.NotifySender(sender, "A world scan is already running - try again in a moment.");
                SendSearch(_lastSearch, sender, false);
                return;
            }
            if (!startNew)
            {
                // A progress poll that arrived after the search finished: never start a sweep on a timer.
                SendSearch(_lastSearch, sender, false);
                return;
            }
            query = Clean(query, MaxQueryLen);
            if (query.Length < 2)
            {
                CompanionPlugin.NotifySender(sender, "Container search: type at least two characters of a prefab or item name.");
                SendSearch(null, sender, false);
                return;
            }
            var job = NewJob(KindSearch, sender);
            if (job == null) { SendSearch(null, sender, false); return; }
            job.Origin = origin;
            job.Query = query;
            job.QueryLower = query.ToLowerInvariant();
            job.Hits = new List<SearchRow>();
            _job = job;
        }

        private static void SearchProcess(ScanJob job, ZDO zdo, int hash)
        {
            var info = InfoOf(hash);
            if (info == null || !info.HasContainer) return;   // cheap prefab test before any ZDO data lookup
            job.Containers++;
            var blob = zdo.GetString(KeyItems, "");
            if (string.IsNullOrEmpty(blob)) return;
            int version, declared;
            if (!ParseChestBlob(blob, out version, out declared, SearchItemScratch)) return;   // unreadable: skipped

            var count = 0;
            string sample = null;
            var q = job.QueryLower;
            for (var i = 0; i < SearchItemScratch.Count; i++)
            {
                var it = SearchItemScratch[i];
                var meta = MetaOf(it.Prefab);
                if (!(it.Prefab.ToLowerInvariant().Contains(q)
                      || (meta.TokenLower.Length > 0 && meta.TokenLower.Contains(q))
                      || (meta.LocalizedLower.Length > 0 && meta.LocalizedLower.Contains(q))))
                    continue;
                count += Math.Max(1, it.Stack);
                if (sample == null) sample = meta.Token.Length > 0 ? meta.Token : it.Prefab;
            }
            SearchItemScratch.Clear();
            if (count == 0) return;

            job.Matched++;
            if (job.Hits.Count >= CandidateCap) return;   // counted, not stored: the reply is 50 rows anyway
            var pos = zdo.GetPosition();
            job.Hits.Add(new SearchRow
            {
                Id = zdo.m_uid,
                Prefab = info.Name,
                Pos = pos,
                Count = count,
                Sample = sample ?? "",
                Dist = Vector3.Distance(pos, job.Origin),
            });
        }

        private static void SearchFinish(ScanJob job)
        {
            if (job.Hits != null)
            {
                job.Hits.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                if (job.Hits.Count > SearchRowCap) job.Hits.RemoveRange(SearchRowCap, job.Hits.Count - SearchRowCap);
                for (var i = 0; i < job.Hits.Count; i++) job.Hits[i].Nearest = NearestPlayerName(job.Hits[i].Pos);
            }
            _lastSearch = job;
            CompanionPlugin.FeatureLog($"Container search '{job.Query}' for {CompanionPlugin.SenderDisplayName(job.Requester)}: {job.Matched} container(s) matched among {job.Containers} decoded ({job.Scanned} ZDOs, {job.Watch.ElapsedMilliseconds} ms).");
            SendSearch(job, job.Requester, false);
        }

        private static void SendSearch(ScanJob job, long uid, bool running)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(running);
            pkg.Write(job != null ? job.Scanned : 0);
            pkg.Write(job != null ? job.Total : TotalZdos());
            pkg.Write(job != null ? job.Containers : 0);
            pkg.Write(job != null ? job.Matched : 0);
            pkg.Write(job != null ? job.Query ?? "" : "");

            var rows = !running && job != null ? job.Hits : null;
            var n = rows != null ? Math.Min(rows.Count, SearchRowCap) : 0;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var r = rows[i];
                pkg.Write(r.Id);
                pkg.Write(r.Prefab ?? "");
                pkg.Write(r.Pos);
                pkg.Write(r.Count);
                pkg.Write(r.Sample ?? "");
                pkg.Write(Clean(r.Nearest, 64));
                pkg.Write(r.Dist);
            }
            pkg.Write(job != null ? job.Watch.ElapsedMilliseconds : 0L);
            Reply(uid, "AP_ChestSearch", pkg);
        }
    }
}
