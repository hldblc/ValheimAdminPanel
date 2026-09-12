using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — server RULES: client-side executors ====================
    // Runs on every machine that loads AdminPanelCompanion.dll — a player's client, an admin's client, a
    // listen-server host — and applies the rule tables Wave8SrvRules.cs broadcasts. A dedicated server
    // receives its own broadcasts too (ZRoutedRpc.Everybody dispatches locally) but never has a local Player,
    // so nothing below ever applies there.
    //
    // Every rule is held in memory only, tagged with the ZNet session it arrived in, and cleared the moment
    // that session is gone (ZNet.instance null, or a different instance): rules from server A can never apply
    // in world B, and a world without a companion is vanilla by construction. Payloads are STORED on receipt
    // and APPLIED from Tick once the world is ready (Player / Minimap / ObjectDB exist) — a host gets the
    // first broadcast before its own character has spawned.
    //
    // ENGINE FACTS (assembly_valheim decompile):
    //  * Minimap.AddPin(Vector3 pos, PinType type, string name, bool save, bool isChecked, long ownerID = 0,
    //    PlatformUserID author = default) returns the PinData; RemovePin(PinData) is public. save:false keeps
    //    the pin out of the player's map data, so nothing persists on the client.
    //  * Minimap.ExploreAll() and Reset() are public; Explore(Vector3, float) and Explore(int, int) are private
    //    (Minimap.cs:1548 / 1569) and reached through AccessTools, with the (int,int) loop as the fallback.
    //  * Trader.m_items is a public List<TradeItem>; StoreGui.FillList reads m_trader.GetAvailableItems() on
    //    every open/buy/sell, so replacing the list takes effect on the next store refresh. Trader.Start is
    //    private and runs once per instance (zone load) — the postfix re-applies for traders that stream in.
    //  * PieceTable.UpdateAvailable skips a piece whose Piece.m_enabled is false (PieceTable.cs:45);
    //    Player.GetAvailableRecipes skips a Recipe whose m_enabled is false (Player.cs:5147). Both flags live
    //    on PREFAB ASSETS shared across scenes, so every flag flipped here is remembered (Piece / Recipe
    //    references) and put back when the rules clear. Player.UpdateKnownRecipesList /
    //    UpdateAvailablePiecesList are private; invoked reflectively so the hammer menu refreshes at once.
    //  * Skills.RaiseSkill(SkillType, float factor = 1f) is public and is the only caller of Skill.Raise; the
    //    private m_skillData dictionary maps type -> Skills.Skill (public nested class, m_level/m_accumulator).
    internal static partial class Wave8Rules
    {
        // ---- client rule state (memory only; see ClearClientRules) ----
        private static readonly List<PinRow> ClientPins = new List<PinRow>();
        private static readonly List<Minimap.PinData> OurPins = new List<Minimap.PinData>();
        private static object _pinsMinimap;          // the Minimap instance OurPins were added to
        private static bool _pinsDirty;

        private static readonly Dictionary<string, List<TraderRow>> ClientTrader =
            new Dictionary<string, List<TraderRow>>(StringComparer.Ordinal);
        private static readonly Dictionary<Trader, List<Trader.TradeItem>> VanillaStock =
            new Dictionary<Trader, List<Trader.TradeItem>>();
        private static readonly HashSet<string> MissingItemsLogged = new HashSet<string>(StringComparer.Ordinal);
        private static bool _traderDirty;

        private static readonly HashSet<string> BannedPieces = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> BannedRecipes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<Piece> DisabledPieces = new List<Piece>();
        private static readonly List<Recipe> DisabledRecipes = new List<Recipe>();
        private static bool _blackDirty;

        private static bool _skillOn;
        private static float _skillMultClient = 1f;
        private static int _skillCapClient;
        private static readonly Dictionary<Skills.SkillType, float> SkillOverrides =
            new Dictionary<Skills.SkillType, float>();

        private static ZNet _rulesNet;              // the session the rules belong to
        private static bool _haveClientRules;
        private static float _nextClientErrLog;

        // ==================== lifecycle (called from Init / Tick in the server partial) ====================

        private static void InitClientPatches()
        {
            ApplyPatch("Wave8Rules.TraderStartPatch", typeof(Wave8RulesTraderStartPatch),
                "trader stock only applies to traders already loaded when the table arrives");
            ApplyPatch("Wave8Rules.SkillsRaisePatch", typeof(Wave8RulesSkillsRaisePatch),
                "skill gain rules are not applied on this client");
            ApplyPatch("Wave8Rules.ObjectDBAwakePatch", typeof(Wave8RulesObjectDBAwakePatch),
                "blacklist is not re-applied after an ObjectDB reload");
            ApplyPatch("Wave8Rules.ObjectDBCopyPatch", typeof(Wave8RulesObjectDBCopyPatch),
                "blacklist is not re-applied after ObjectDB.CopyOtherDB");
        }

        private static void ClientTick()
        {
            try
            {
                var net = ZNet.instance;
                if (net == null || (_rulesNet != null && !ReferenceEquals(_rulesNet, net)))
                {
                    if (_haveClientRules) ClearClientRules();
                    return;
                }
                if (!_haveClientRules) return;
                var player = Player.m_localPlayer;
                if (player == null) return;   // dedicated server, or a client still loading its world

                if (_pinsDirty || (ClientPins.Count > 0 && Minimap.instance != null && !ReferenceEquals(_pinsMinimap, Minimap.instance)))
                    ApplyPins();
                if (_traderDirty) ApplyTraderAll();
                if (_blackDirty) ApplyBlacklist(player);
            }
            catch (Exception e)
            {
                var now = Time.unscaledTime;
                if (now >= _nextClientErrLog)
                {
                    _nextClientErrLog = now + 5f;
                    CompanionPlugin.FeatureLog($"Rule apply failed on this client: {e.Message}");
                }
            }
        }

        private static bool RulesCurrent() =>
            _haveClientRules && _rulesNet != null && ReferenceEquals(_rulesNet, ZNet.instance);

        private static void MarkRules()
        {
            var net = ZNet.instance;
            if (net == null) return;
            _rulesNet = net;
            _haveClientRules = true;
        }

        // Idempotent and cheap: everything applied is undone, then the tables are forgotten.
        private static void ClearClientRules()
        {
            _haveClientRules = false;
            _rulesNet = null;

            try { RemoveOurPins(Minimap.instance); } catch (Exception) { }
            ClientPins.Clear();
            _pinsDirty = false;
            _pinsMinimap = null;

            try { RestoreVanillaStock(); } catch (Exception) { }
            ClientTrader.Clear();
            _traderDirty = false;

            try { RestoreBlacklist(); } catch (Exception) { }
            BannedPieces.Clear();
            BannedRecipes.Clear();
            _blackDirty = false;

            _skillOn = false;
            _skillMultClient = 1f;
            _skillCapClient = 0;
            SkillOverrides.Clear();
        }

        private static void ClientHiddenCategoriesChanged()
        {
            if (ClientPins.Count > 0) _pinsDirty = true;
        }

        // ==================== trust gate ====================
        // CompanionPlugin.SenderIsServer is private and Wave34Core's wrapper is private too; this is the same
        // two-step (reflective bind, equivalent fallback) so a rename can only make the executors go silent,
        // never let a peer impersonate the server.
        private static MethodInfo _senderIsServerMi;
        private static bool _senderIsServerProbed;

        private static bool SenderIsTrustedServer(long sender)
        {
            if (!_senderIsServerProbed)
            {
                _senderIsServerProbed = true;
                try { _senderIsServerMi = AccessTools.Method(typeof(CompanionPlugin), "SenderIsServer", new[] { typeof(long) }); }
                catch (Exception) { }
            }
            if (_senderIsServerMi != null)
            {
                try { return (bool)_senderIsServerMi.Invoke(null, new object[] { sender }); }
                catch (Exception) { }
            }
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return false;
                var serverPeer = znet.GetServerPeer();
                if (serverPeer != null && serverPeer.m_uid != 0L && sender == serverPeer.m_uid) return true;
                if (!znet.IsServer() || ZDOMan.instance == null) return false;
                if (sender != ZDOMan.GetSessionID()) return false;
                var f = AccessTools.Field(typeof(CompanionPlugin), "SenderSanitizerActive");
                return f != null && (bool)f.GetValue(null);
            }
            catch (Exception) { return false; }
        }

        // ==================== #14 map pins ====================

        private static void OnMapPins(long sender, ZPackage pkg)
        {
            if (!SenderIsTrustedServer(sender)) return;
            List<PinRow> rows;
            try
            {
                if (pkg.ReadInt() != Ver) return;
                var enabled = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > PinCap) return;
                rows = new List<PinRow>(n);
                for (var i = 0; i < n; i++)
                {
                    var r = new PinRow { Id = pkg.ReadInt() };
                    r.Name = Clamp(pkg.ReadString(), MaxPinName);
                    var x = pkg.ReadSingle(); var y = pkg.ReadSingle(); var z = pkg.ReadSingle();
                    r.Pos = new Vector3(x, y, z);
                    var icon = pkg.ReadInt();
                    r.Icon = IconOk(icon) ? icon : 3;
                    r.Category = Clamp(pkg.ReadString(), MaxCategory);
                    rows.Add(r);
                }
                if (!enabled) rows.Clear();
            }
            catch (Exception) { return; }   // malformed: keep what we had

            ClientPins.Clear();
            ClientPins.AddRange(rows);
            _pinsDirty = true;
            MarkRules();
        }

        private static void ApplyPins()
        {
            var map = Minimap.instance;
            if (map == null) return;   // stays dirty; retried next frame
            RemoveOurPins(map);
            var hidden = HiddenCategorySet();
            foreach (var r in ClientPins)
            {
                if (r.Category.Length > 0 && hidden.Contains(r.Category)) continue;
                try
                {
                    var pd = MinimapAddPin(map, r.Pos, r.Icon, r.Name ?? "");
                    if (pd != null) OurPins.Add(pd);
                }
                catch (Exception) { }
            }
            _pinsMinimap = map;
            _pinsDirty = false;
        }

        // Minimap.AddPin's trailing optional parameter is a PlatformUserID from the Splatform assembly, which
        // this project deliberately does not reference (it would make the companion load-dependent on one
        // more game DLL). Calling it through reflection keeps the compile-time dependency out: the six real
        // arguments are passed, every further parameter gets its declared default (or the struct default).
        private static MethodInfo _addPinMi;
        private static bool _addPinProbed;

        private static Minimap.PinData MinimapAddPin(Minimap map, Vector3 pos, int icon, string name)
        {
            if (!_addPinProbed)
            {
                _addPinProbed = true;
                try { _addPinMi = AccessTools.Method(typeof(Minimap), "AddPin"); } catch (Exception) { }
                if (_addPinMi == null) CompanionPlugin.FeatureLog("Map pins unavailable on this game build: Minimap.AddPin could not be resolved.");
            }
            if (_addPinMi == null) return null;
            var ps = _addPinMi.GetParameters();
            if (ps.Length < 6) return null;
            var args = new object[ps.Length];
            args[0] = pos;
            args[1] = Enum.ToObject(ps[1].ParameterType, icon);
            args[2] = name;
            args[3] = false;   // save: never written into the player's map data
            args[4] = false;   // isChecked
            args[5] = 0L;      // ownerID
            for (var i = 6; i < ps.Length; i++)
            {
                object dflt = null;
                try { if (ps[i].HasDefaultValue) dflt = ps[i].DefaultValue; } catch (Exception) { }
                if (dflt == null && ps[i].ParameterType.IsValueType) dflt = Activator.CreateInstance(ps[i].ParameterType);
                args[i] = dflt;
            }
            return _addPinMi.Invoke(map, args) as Minimap.PinData;
        }

        // Only pins added to THIS Minimap instance are handed back to it; pins of a destroyed map (scene
        // change) died with it and are simply forgotten.
        private static void RemoveOurPins(Minimap map)
        {
            if (OurPins.Count == 0) return;
            if (map != null && ReferenceEquals(map, _pinsMinimap))
            {
                foreach (var pd in OurPins)
                {
                    try { map.RemovePin(pd); } catch (Exception) { }
                }
            }
            OurPins.Clear();
        }

        private static HashSet<string> HiddenCategorySet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = _hiddenCategories != null ? _hiddenCategories.Value : null;
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var part in raw.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) set.Add(p);
            }
            return set;
        }

        // ==================== #15 map reveal / reset ====================

        // AP_MapReveal: {int ver, int mode (0 all / 1 radius / 2 reset), float radius} — immediate executor.
        private static void OnMapRevealExec(long sender, ZPackage pkg)
        {
            try
            {
                if (!SenderIsTrustedServer(sender)) return;
                var player = Player.m_localPlayer;
                if (player == null) return;
                if (pkg.ReadInt() != Ver) return;
                var mode = Mathf.Clamp(pkg.ReadInt(), 0, 2);
                var radius = pkg.ReadSingle();
                if (float.IsNaN(radius) || float.IsInfinity(radius)) radius = 200f;
                radius = Mathf.Clamp(radius, 10f, 2000f);
                var map = Minimap.instance;
                if (map == null) return;

                switch (mode)
                {
                    case 0:
                        map.ExploreAll();
                        player.Message(MessageHud.MessageType.Center, "An admin revealed your whole map.");
                        break;
                    case 1:
                        if (ExploreRadius(map, player.transform.position, radius))
                            player.Message(MessageHud.MessageType.Center, $"An admin revealed the map within {radius:0} m of you.");
                        else
                            CompanionPlugin.FeatureLog("Map reveal (radius) unavailable on this game build: Minimap.Explore could not be resolved.");
                        break;
                    default:
                        map.Reset();
                        player.Message(MessageHud.MessageType.Center, "An admin reset your map exploration.");
                        break;
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Map reveal failed on this client: {e.Message}"); }
        }

        private static bool ExploreRadius(Minimap map, Vector3 pos, float radius)
        {
            var byWorld = AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) });
            if (byWorld != null)
            {
                byWorld.Invoke(map, new object[] { pos, radius });
                return true;
            }
            // Fallback: the per-pixel primitive plus a manual texture flush (Minimap.cs:1548-1566 inlined).
            var byPixel = AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(int), typeof(int) });
            if (byPixel == null) return false;
            var size = map.m_textureSize;
            var pixel = map.m_pixelSize;
            if (size <= 0 || pixel <= 0f) return false;
            var half = size / 2;
            var px = Mathf.RoundToInt(pos.x / pixel + half);
            var py = Mathf.RoundToInt(pos.z / pixel + half);
            var r = Mathf.CeilToInt(radius / pixel);
            var args = new object[2];
            for (var y = py - r; y <= py + r; y++)
            {
                for (var x = px - r; x <= px + r; x++)
                {
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    if ((x - px) * (x - px) + (y - py) * (y - py) > r * r) continue;
                    args[0] = x; args[1] = y;
                    byPixel.Invoke(map, args);
                }
            }
            var fog = AccessTools.Field(typeof(Minimap), "m_fogTexture")?.GetValue(map) as Texture2D;
            if (fog != null) fog.Apply();
            return true;
        }

        // ==================== #18 trader stock ====================

        private static void OnTraderStock(long sender, ZPackage pkg)
        {
            if (!SenderIsTrustedServer(sender)) return;
            Dictionary<string, List<TraderRow>> table;
            try
            {
                if (pkg.ReadInt() != Ver) return;
                var enabled = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > TraderRowCap) return;
                table = new Dictionary<string, List<TraderRow>>(StringComparer.Ordinal);
                for (var i = 0; i < n; i++)
                {
                    var r = new TraderRow
                    {
                        Trader = Clamp(pkg.ReadString(), MaxTraderKey),
                        Index = pkg.ReadInt(),
                        Item = Clamp(pkg.ReadString(), MaxPrefabName),
                        Stack = Mathf.Clamp(pkg.ReadInt(), 1, 999),
                        Price = Mathf.Clamp(pkg.ReadInt(), 0, 999999),
                        Key = Clamp(pkg.ReadString(), MaxGlobalKey),
                    };
                    if (r.Trader.Length == 0 || r.Item.Length == 0) continue;
                    List<TraderRow> list;
                    if (!table.TryGetValue(r.Trader, out list)) table[r.Trader] = list = new List<TraderRow>();
                    if (list.Count < TraderRowsPerTrader) list.Add(r);
                }
                if (!enabled) table.Clear();
            }
            catch (Exception) { return; }

            ClientTrader.Clear();
            foreach (var kv in table)
            {
                kv.Value.Sort((a, b) => a.Index.CompareTo(b.Index));
                ClientTrader[kv.Key] = kv.Value;
            }
            _traderDirty = true;
            MarkRules();
        }

        // Apply to every trader currently loaded (FindObjectsOfType is only paid on a table change); traders
        // that stream in later are handled by the Trader.Start postfix.
        private static void ApplyTraderAll()
        {
            PruneVanillaStock();
            Trader[] traders;
            try { traders = UnityEngine.Object.FindObjectsByType<Trader>(FindObjectsSortMode.None); }
            catch (Exception) { traders = null; }
            if (traders != null)
                foreach (var t in traders) ApplyTrader(t);
            // A trader that had a table and lost it is restored by ApplyTrader above (no rows -> vanilla).
            _traderDirty = false;
        }

        private static void ApplyTrader(Trader t)
        {
            if (t == null) return;
            var key = PrefabName(t.gameObject);
            List<TraderRow> rows;
            if (ClientTrader.TryGetValue(key, out rows) && rows.Count > 0)
            {
                if (!VanillaStock.ContainsKey(t)) VanillaStock[t] = t.m_items;   // the original LIST object, kept for restore
                var list = new List<Trader.TradeItem>(rows.Count);
                var db = ObjectDB.instance;
                foreach (var r in rows)
                {
                    ItemDrop drop = null;
                    try
                    {
                        var go = db != null ? db.GetItemPrefab(r.Item) : null;
                        drop = go != null ? go.GetComponent<ItemDrop>() : null;
                    }
                    catch (Exception) { }
                    if (drop == null)
                    {
                        if (MissingItemsLogged.Add(r.Item))
                            CompanionPlugin.FeatureLog($"Trader stock: item prefab '{r.Item}' does not exist on this client; row skipped.");
                        continue;
                    }
                    list.Add(new Trader.TradeItem
                    {
                        m_prefab = drop,
                        m_stack = r.Stack,
                        m_price = r.Price,
                        m_requiredGlobalKey = string.IsNullOrEmpty(r.Key) ? null : r.Key,
                    });
                }
                t.m_items = list;
            }
            else
            {
                List<Trader.TradeItem> vanilla;
                if (VanillaStock.TryGetValue(t, out vanilla))
                {
                    t.m_items = vanilla;
                    VanillaStock.Remove(t);
                }
            }
        }

        private static void RestoreVanillaStock()
        {
            foreach (var kv in VanillaStock)
            {
                var t = kv.Key;
                if (t == null) continue;   // Unity null: the instance was unloaded with its zone
                try { t.m_items = kv.Value; } catch (Exception) { }
            }
            VanillaStock.Clear();
        }

        // Destroyed traders must not pin their (dead) instance in the dictionary forever.
        private static void PruneVanillaStock()
        {
            if (VanillaStock.Count == 0) return;
            List<Trader> dead = null;
            foreach (var kv in VanillaStock)
                if (kv.Key == null) (dead ?? (dead = new List<Trader>())).Add(kv.Key);
            if (dead != null) foreach (var t in dead) VanillaStock.Remove(t);
        }

        [HarmonyPatch(typeof(Trader), "Start")]
        internal static class Wave8RulesTraderStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Trader __instance)
            {
                try
                {
                    if (ClientTrader.Count == 0 || !RulesCurrent()) return;
                    ApplyTrader(__instance);
                }
                catch (Exception) { /* a trader must still work with vanilla stock */ }
            }
        }

        // ==================== #19 blacklist ====================

        private static void OnBlacklist(long sender, ZPackage pkg)
        {
            if (!SenderIsTrustedServer(sender)) return;
            var pieces = new HashSet<string>(StringComparer.Ordinal);
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (pkg.ReadInt() != Ver) return;
                var enabled = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > BlacklistCap) return;
                for (var i = 0; i < n; i++)
                {
                    var prefab = Clamp(pkg.ReadString(), MaxPrefabName);
                    var kind = pkg.ReadInt();
                    if (prefab.Length == 0) continue;
                    if (kind == 1) recipes.Add(prefab); else pieces.Add(prefab);
                }
                if (!enabled) { pieces.Clear(); recipes.Clear(); }
            }
            catch (Exception) { return; }

            BannedPieces.Clear();
            BannedRecipes.Clear();
            foreach (var p in pieces) BannedPieces.Add(p);
            foreach (var r in recipes) BannedRecipes.Add(r);
            _blackDirty = true;
            MarkRules();
        }

        private static void ApplyBlacklist(Player player)
        {
            var db = ObjectDB.instance;
            if (db == null) return;   // stays dirty; retried next frame

            // 1) Put back whatever is no longer banned (also covers a shrinking table).
            for (var i = DisabledPieces.Count - 1; i >= 0; i--)
            {
                var p = DisabledPieces[i];
                if (p == null) { DisabledPieces.RemoveAt(i); continue; }
                if (BannedPieces.Contains(p.gameObject.name)) continue;
                p.m_enabled = true;
                DisabledPieces.RemoveAt(i);
            }
            for (var i = DisabledRecipes.Count - 1; i >= 0; i--)
            {
                var r = DisabledRecipes[i];
                if (r == null) { DisabledRecipes.RemoveAt(i); continue; }
                if (IsBannedRecipe(r)) continue;
                r.m_enabled = true;
                DisabledRecipes.RemoveAt(i);
            }

            // 2) Disable what is banned. Only flags actually flipped are remembered, so a piece vanilla ships
            //    disabled is never switched ON by an unban.
            if (BannedPieces.Count > 0 && db.m_items != null)
            {
                foreach (var item in db.m_items)
                {
                    if (item == null) continue;
                    PieceTable table = null;
                    try
                    {
                        var drop = item.GetComponent<ItemDrop>();
                        table = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null
                            ? drop.m_itemData.m_shared.m_buildPieces : null;
                    }
                    catch (Exception) { }
                    if (table == null || table.m_pieces == null) continue;
                    foreach (var go in table.m_pieces)
                    {
                        if (go == null || !BannedPieces.Contains(go.name)) continue;
                        var piece = go.GetComponent<Piece>();
                        if (piece == null || !piece.m_enabled) continue;
                        piece.m_enabled = false;
                        DisabledPieces.Add(piece);
                    }
                }
            }
            if (BannedRecipes.Count > 0 && db.m_recipes != null)
            {
                foreach (var r in db.m_recipes)
                {
                    if (r == null || !r.m_enabled || !IsBannedRecipe(r)) continue;
                    r.m_enabled = false;
                    DisabledRecipes.Add(r);
                }
            }

            RefreshPlayerLists(player);
            _blackDirty = false;
        }

        // A recipe is addressed by the prefab name of the item it produces (what admins see in the Items tab)
        // or, as a courtesy, by the Recipe asset's own name ("Recipe_SwordIron").
        private static bool IsBannedRecipe(Recipe r)
        {
            try
            {
                if (r.m_item != null && r.m_item.gameObject != null && BannedRecipes.Contains(r.m_item.gameObject.name)) return true;
                return BannedRecipes.Contains(r.name);
            }
            catch (Exception) { return false; }
        }

        private static void RestoreBlacklist()
        {
            foreach (var p in DisabledPieces) { if (p != null) try { p.m_enabled = true; } catch (Exception) { } }
            DisabledPieces.Clear();
            foreach (var r in DisabledRecipes) { if (r != null) try { r.m_enabled = true; } catch (Exception) { } }
            DisabledRecipes.Clear();
            var player = Player.m_localPlayer;
            if (player != null) RefreshPlayerLists(player);
        }

        private static MethodInfo _miKnownRecipes, _miAvailPieces;
        private static bool _miProbed;

        private static void RefreshPlayerLists(Player player)
        {
            if (player == null) return;
            if (!_miProbed)
            {
                _miProbed = true;
                try
                {
                    _miKnownRecipes = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
                    _miAvailPieces = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
                }
                catch (Exception) { }
            }
            try { _miKnownRecipes?.Invoke(player, null); } catch (Exception) { }
            try { _miAvailPieces?.Invoke(player, null); } catch (Exception) { }
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        internal static class Wave8RulesObjectDBAwakePatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (BannedPieces.Count > 0 || BannedRecipes.Count > 0) _blackDirty = true;
            }
        }

        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
        internal static class Wave8RulesObjectDBCopyPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (BannedPieces.Count > 0 || BannedRecipes.Count > 0) _blackDirty = true;
            }
        }

        // ==================== #20 skill rules ====================

        private static void OnSkillRules(long sender, ZPackage pkg)
        {
            if (!SenderIsTrustedServer(sender)) return;
            bool enabled; float mult; int cap;
            var overrides = new Dictionary<Skills.SkillType, float>();
            try
            {
                if (pkg.ReadInt() != Ver) return;
                enabled = pkg.ReadBool();
                mult = pkg.ReadSingle();
                cap = pkg.ReadInt();
                pkg.ReadString();   // canonical overrides text: informational, the parsed rows follow
                var n = pkg.ReadInt();
                if (n < 0 || n > OverrideCap) return;
                for (var i = 0; i < n; i++)
                {
                    var type = (Skills.SkillType)pkg.ReadInt();
                    var m = pkg.ReadSingle();
                    if (float.IsNaN(m) || float.IsInfinity(m)) continue;
                    if (type == Skills.SkillType.None || type == Skills.SkillType.All) continue;
                    overrides[type] = Mathf.Clamp(m, 0f, 10f);
                }
            }
            catch (Exception) { return; }

            if (float.IsNaN(mult) || float.IsInfinity(mult)) mult = 1f;
            _skillOn = enabled;
            _skillMultClient = enabled ? Mathf.Clamp(mult, 0.1f, 10f) : 1f;
            _skillCapClient = enabled ? Mathf.Clamp(cap, 0, 100) : 0;
            SkillOverrides.Clear();
            if (enabled) foreach (var kv in overrides) SkillOverrides[kv.Key] = kv.Value;
            MarkRules();
        }

        private static bool SkillRulesActive() => _skillOn && RulesCurrent();

        private static FieldInfo _skillDataField;
        private static bool _skillDataProbed;

        private static Skills.Skill FindSkill(Skills skills, Skills.SkillType type)
        {
            if (skills == null) return null;
            if (!_skillDataProbed)
            {
                _skillDataProbed = true;
                try { _skillDataField = AccessTools.Field(typeof(Skills), "m_skillData"); } catch (Exception) { }
            }
            if (_skillDataField == null) return null;
            var dict = _skillDataField.GetValue(skills) as Dictionary<Skills.SkillType, Skills.Skill>;
            Skills.Skill s;
            return dict != null && dict.TryGetValue(type, out s) ? s : null;
        }

        // Prefix scales the gain (an override REPLACES the global multiplier for that skill; 0 = no gain) and
        // refuses the whole raise once the cap is reached, so no accumulator creeps past it. Postfix clamps in
        // case a raise crossed the cap inside this call. Fail-open: any exception leaves vanilla behaviour.
        [HarmonyPatch(typeof(Skills), "RaiseSkill")]
        internal static class Wave8RulesSkillsRaisePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Skills __instance, Skills.SkillType skillType, ref float factor)
            {
                try
                {
                    if (!SkillRulesActive() || skillType == Skills.SkillType.None) return true;
                    float mult;
                    if (!SkillOverrides.TryGetValue(skillType, out mult)) mult = _skillMultClient;
                    factor *= mult;
                    if (_skillCapClient > 0)
                    {
                        var s = FindSkill(__instance, skillType);
                        if (s != null && s.m_level >= _skillCapClient) return false;
                    }
                }
                catch (Exception) { }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(Skills __instance, Skills.SkillType skillType)
            {
                try
                {
                    if (!SkillRulesActive() || _skillCapClient <= 0) return;
                    var s = FindSkill(__instance, skillType);
                    if (s != null && s.m_level > _skillCapClient)
                    {
                        s.m_level = _skillCapClient;
                        s.m_accumulator = 0f;
                    }
                }
                catch (Exception) { }
            }
        }

        // ==================== small helpers ====================

        private static string PrefabName(GameObject go)
        {
            if (go == null) return "";
            var n = go.name ?? "";
            var idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx > 0 ? n.Substring(0, idx) : n;
        }

        private static string Clamp(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }
}
