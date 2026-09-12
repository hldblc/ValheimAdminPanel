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
    // ==================== Wave 8 — server RULES (server side) ====================
    // Five admin features that all share ONE mechanism: the server keeps a rule table in FeatureStore, pushes
    // it to every connected client as a "rule table broadcast", and the companion running on a MODDED client
    // applies it locally. Unmodded clients see vanilla — every admin card says so in its hint.
    //
    //   #14 map pins      table "mappins"   id -> name|x|y|z|icon|category         executor AP_MapPins
    //   #18 trader stock  table "trader"    trader|index -> item|stack|price|key    executor AP_TraderStock
    //   #19 blacklist     table "blacklist" prefab -> piece|recipe                  executor AP_Blacklist
    //   #20 skill rules   config entries    SkillGainMultiplier / SkillLevelCap / SkillOverrides
    //                                                                              executor AP_SkillRules
    //   #15 map reveal    no table: a one-shot relay AP_SrvMapReveal -> AP_MapReveal to one player's client
    //
    // BROADCAST DISCIPLINE. Each channel has a cheap signature (enabled flag + table content). Tick recomputes
    // it every 2 s and broadcasts to ZRoutedRpc.Everybody only when it changed — so a config edit made in the
    // BepInEx config manager at runtime and a panel edit take the same path. A peer that joins gets every
    // channel ~10 s after its handshake (a Tick-driven peer watcher; the same delay the MOTD and the
    // capability probe use, because a just-joined client has no Player yet), and every channel is re-sent to
    // everybody every 5 minutes as a safety net for a client that missed a packet. All behaviour-changing
    // switches default OFF; a disabled channel broadcasts an EMPTY table so clients drop what they applied.
    //
    // The client executors and the Harmony patches that apply the rules on a player's machine live in the
    // sibling partial file Wave8SrvRulesClient.cs (same DLL: the companion also runs on every modded client).
    //
    // ENGINE FACTS (verified by decompiling assembly_valheim, see the client partial for the client-side ones):
    //  * Player-placed pieces reach the server through ZDOMan.RPC_ZDOData, which calls the PRIVATE
    //    ZDOMan.CreateNewZDO(ZDOID, Vector3, int prefabHashIn = 0) with prefabHashIn == 0 and then
    //    Deserialize()s the prefab hash into the new ZDO in the same call. The server's OWN creations go
    //    through the public CreateNewZDO(Vector3, int) overload with a non-zero hash. The blacklist postfix
    //    therefore records only hash-0 creations (client-sent objects) and inspects them on the next tick,
    //    when the prefab is known. World-generated pieces carry creator == 0 ("creator", a saved long that
    //    Player.PlacePiece writes) and are never touched; pieces placed BEFORE a ban are not removed either.
    //  * ZDOMan.DestroyZDO is a silent no-op unless the caller owns the ZDO: claim with SetOwner(session) first
    //    (CompanionPlugin.OnServerUndo learned this the hard way). Nothing is handed back — the object is gone.
    //  * ZNetPeer.IsReady() == (m_uid != 0); a peer with an empty m_playerName was rejected in RPC_PeerInfo.
    internal static partial class Wave8Rules
    {
        internal const int Ver = 1;   // wire version of EVERY payload in this group — bump, never reorder

        // ---- channels (AP_SrvRulesToggle addresses them by number) ----
        internal const int ChPins = 0;
        internal const int ChTrader = 1;
        internal const int ChBlacklist = 2;
        internal const int ChSkills = 3;

        // ---- wire caps (the panel bounds every list it reads with the same numbers) ----
        internal const int PinCap = 200;
        internal const int TraderRowCap = 100;
        internal const int TraderRowsPerTrader = 40;
        internal const int BlacklistCap = 200;
        internal const int OverrideCap = 30;
        internal const int MaxPinName = 48;
        internal const int MaxCategory = 24;
        internal const int MaxPrefabName = 64;
        internal const int MaxTraderKey = 32;
        internal const int MaxGlobalKey = 40;
        internal const int MaxOverridesText = 240;

        // ---- store tables ----
        private const string TblPins = "mappins";
        private const string TblTrader = "trader";
        private const string TblBlacklist = "blacklist";

        // ---- cadences ----
        private const float SyncInterval = 2f;             // signature check
        private const float JoinDelay = 10f;               // handshake -> first rule push
        private const float RebroadcastInterval = 300f;    // safety net for a missed packet
        private const float EnforceInterval = 0.5f;        // blacklist pending-ZDO check
        private const float PlacerNoticeInterval = 5f;     // per-player "that piece is banned" throttle

        // ---- pin icons exposed to admins: Minimap.PinType values (Minimap.cs enum: Icon0=0 house, Icon1=1
        //      fire, Icon2=2 mine, Icon3=3 dot, Death=4, Bed=5, Icon4=6 portal, ..., Boss=9). Anything else is
        //      refused server-side; Minimap.AddPin would fall back to Icon3 for an invalid value anyway. ----
        internal static readonly int[] AllowedIcons = { 0, 1, 2, 3, 6, 9, 5 };

        // ---- ZDO keys (literal strings -> stable hashes; the string is what the save file stores) ----
        private static readonly int KeyCreator = "creator".GetStableHashCode();

        // ---- config ----
        private static ConfigEntry<bool> _enablePins;
        private static ConfigEntry<bool> _enableTrader;
        private static ConfigEntry<bool> _enableBlacklist;
        private static ConfigEntry<bool> _blacklistRemovePlaced;
        private static ConfigEntry<bool> _enableSkills;
        private static ConfigEntry<float> _skillMult;
        private static ConfigEntry<int> _skillCap;
        private static ConfigEntry<string> _skillOverrides;
        private static ConfigEntry<string> _hiddenCategories;   // read by the CLIENT partial

        private static bool PinsOn => _enablePins != null && _enablePins.Value;
        private static bool TraderOn => _enableTrader != null && _enableTrader.Value;
        private static bool BlacklistOn => _enableBlacklist != null && _enableBlacklist.Value;
        private static bool RemovePlacedOn => _blacklistRemovePlaced == null || _blacklistRemovePlaced.Value;
        private static bool SkillsOn => _enableSkills != null && _enableSkills.Value;

        // ---- broadcast state ----
        private static readonly string[] LastSig = new string[4];
        private static float _nextSync;
        private static float _nextRebroadcast;
        private static readonly HashSet<long> KnownPeers = new HashSet<long>();
        private static readonly Dictionary<long, float> PendingJoins = new Dictionary<long, float>();
        private static readonly List<long> PeerScratch = new List<long>();

        // ---- blacklist enforcement state ----
        private struct PendingZdo { public ZDOID Id; public int Rounds; }
        private const int PendingCap = 8192;
        private static readonly List<PendingZdo> Pending = new List<PendingZdo>();
        private static readonly HashSet<int> BannedPieceHashes = new HashSet<int>();
        private static readonly Dictionary<int, string> BannedPieceNames = new Dictionary<int, string>();
        private static readonly Dictionary<long, float> PlacerNoticeAt = new Dictionary<long, float>();
        private static volatile bool _enforceActive;   // read by the CreateNewZDO postfix on the hot path
        private static float _nextEnforce;
        private static bool _pendingOverflowLogged;

        private static bool _inited;
        private static float _nextErrLog;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            if (_inited) return;
            _inited = true;

            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enablePins = cfg.Bind("Features", "EnableMapPins", false,
                    "Push the admin-defined map pins (Tools tab > Map pins) to every player whose client runs AdminPanelCompanion. OFF by default. Pins can be edited while this is off; nothing is sent.");
                _enableTrader = cfg.Bind("Features", "EnableTraderStock", false,
                    "Replace trader stock (Haldor, Hildir, Bog Witch...) with the server-defined table on every client that runs AdminPanelCompanion. OFF by default. Unmodded clients always see vanilla stock.");
                _enableBlacklist = cfg.Bind("Features", "EnableBlacklist", false,
                    "Enforce the recipe / build-piece blacklist: modded clients hide banned pieces and recipes, and the server removes banned pieces the moment any client places them. OFF by default.");
                _blacklistRemovePlaced = cfg.Bind("Features", "BlacklistRemovePlacedPieces", true,
                    "When EnableBlacklist is on, also DESTROY a banned build piece as soon as a player places it (this is what reaches unmodded clients). Admins are exempt. Off = modded clients only hide the pieces.");
                _enableSkills = cfg.Bind("Features", "EnableSkillRules", false,
                    "Apply SkillGainMultiplier / SkillLevelCap / SkillOverrides on every client that runs AdminPanelCompanion. OFF by default. Unmodded clients keep vanilla skill gain.");
                _skillMult = cfg.Bind("Features", "SkillGainMultiplier", 1f,
                    new ConfigDescription("Skill gain multiplier applied by modded clients (0.1-10). 1 = vanilla.",
                        new AcceptableValueRange<float>(0.1f, 10f)));
                _skillCap = cfg.Bind("Features", "SkillLevelCap", 0,
                    new ConfigDescription("Highest skill level modded clients may reach (1-100). 0 = no cap.",
                        new AcceptableValueRange<int>(0, 100)));
                _skillOverrides = cfg.Bind("Features", "SkillOverrides", "",
                    "Per-skill gain multipliers, e.g. Swords=2,Bows=0.5 (skill names as in the Skills.SkillType enum; 0 = that skill never gains). An override REPLACES SkillGainMultiplier for that skill.");
                _hiddenCategories = cfg.Bind("Features", "MapPinsHiddenCategories", "",
                    "CLIENT-SIDE: comma-separated pin categories this client will not show (e.g. shops,events). Server-pushed pins in those categories are skipped on this machine only.");
                try { _hiddenCategories.SettingChanged += (s, e) => ClientHiddenCategoriesChanged(); }
                catch (Exception) { }
            }

            // Actions go through the chokepoint (audit line + role enforcement before the handler runs).
            // Reads are one-shot (section open / after an edit / a throttled Refresh click), not timer polls,
            // so they are registered too — same choice Wave5Area made for AP_SrvZoneListReq.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMapPinsReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMapPinSet", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMapPinDel", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvMapReveal", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTraderReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvTraderSet", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBlacklistReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvBlacklistSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSkillRulesReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSkillRulesSet", null);
            CompanionPlugin.RegisterAuditedRpc("AP_SrvRulesToggle", null);

            ApplyPatch("Wave8Rules.RpcRegistration", typeof(Wave8RulesRpcRegistration),
                "rule tables unavailable - map pins, trader stock, blacklist, skill rules and map reveal do nothing");
            ApplyPatch("Wave8Rules.ZDOManCreatePatch", typeof(Wave8RulesZDOManCreatePatch),
                "banned pieces placed by unmodded clients are NOT removed server-side");
            InitClientPatches();   // Trader.Start / Skills.RaiseSkill / ObjectDB hooks (client partial)
        }

        private static void ApplyPatch(string name, Type patchClass, string degradation)
        {
            var ok = true;
            try { Harmony.CreateAndPatchAll(patchClass); }
            catch (Exception e)
            {
                ok = false;
                CompanionPlugin.FeatureLog($"{name} failed ({degradation}): {e.Message}");
            }
            try { Wave2Ops.ReportPatch(name, ok); }
            catch (Exception) { /* the self-test module is optional */ }
        }

        internal static void Tick()
        {
            ServerTick();
            ClientTick();
        }

        private static void ServerTick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                // Between worlds nothing must linger: the next world starts with fresh signatures so its own
                // tables are pushed, and a peer set from the old session cannot suppress a join push.
                if (KnownPeers.Count > 0 || PendingJoins.Count > 0 || LastSig[0] != null)
                {
                    KnownPeers.Clear();
                    PendingJoins.Clear();
                    Pending.Clear();
                    PlacerNoticeAt.Clear();
                    for (var i = 0; i < LastSig.Length; i++) LastSig[i] = null;
                    _enforceActive = false;
                }
                return;
            }
            var now = Time.unscaledTime;

            if (now >= _nextSync)
            {
                _nextSync = now + SyncInterval;
                try { SyncChannels(now >= _nextRebroadcast); }
                catch (Exception e) { LogThrottled($"Rule broadcast failed: {e.Message}"); }
                if (now >= _nextRebroadcast) _nextRebroadcast = now + RebroadcastInterval;
                try { WatchPeers(now); }
                catch (Exception e) { LogThrottled($"Rule peer watcher failed: {e.Message}"); }
            }
            if (PendingJoins.Count > 0)
            {
                try { StepPendingJoins(now); }
                catch (Exception e) { LogThrottled($"Rule join push failed: {e.Message}"); }
            }
            if (now >= _nextEnforce)
            {
                _nextEnforce = now + EnforceInterval;
                try { StepEnforcement(now); }
                catch (Exception e) { LogThrottled($"Blacklist enforcement failed: {e.Message}"); }
            }
        }

        private static void LogThrottled(string msg)
        {
            var now = Time.unscaledTime;
            if (now < _nextErrLog) return;
            _nextErrLog = now + 5f;
            CompanionPlugin.FeatureLog(msg);
        }

        // ==================== RPC registration ====================

        // Both directions are registered on BOTH sides on purpose (the DLL is the same file on the server and
        // on a player's client); each handler guards its own side.
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave8RulesRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // admin requests (handled on the server)
                    ZRoutedRpc.instance.Register("AP_SrvMapPinsReq", new Action<long>(OnMapPinsReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvMapPinSet", OnMapPinSet);
                    ZRoutedRpc.instance.Register<int>("AP_SrvMapPinDel", OnMapPinDel);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvMapReveal", OnMapReveal);
                    ZRoutedRpc.instance.Register("AP_SrvTraderReq", new Action<long>(OnTraderReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvTraderSet", OnTraderSet);
                    ZRoutedRpc.instance.Register("AP_SrvBlacklistReq", new Action<long>(OnBlacklistReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvBlacklistSet", OnBlacklistSet);
                    ZRoutedRpc.instance.Register("AP_SrvSkillRulesReq", new Action<long>(OnSkillRulesReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvSkillRulesSet", OnSkillRulesSet);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvRulesToggle", OnRulesToggle);
                    // client executors (handled on a player's client; see Wave8SrvRulesClient.cs)
                    ZRoutedRpc.instance.Register<ZPackage>("AP_MapPins", OnMapPins);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_MapReveal", OnMapRevealExec);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_TraderStock", OnTraderStock);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_Blacklist", OnBlacklist);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SkillRules", OnSkillRules);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave8Rules RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== rule rows (parsed once per signature change) ====================

        internal sealed class PinRow
        {
            public int Id;
            public string Name = "";
            public Vector3 Pos;
            public int Icon;
            public string Category = "";
        }

        internal sealed class TraderRow
        {
            public string Trader = "";
            public int Index;
            public string Item = "";
            public int Stack = 1;
            public int Price = 1;
            public string Key = "";
        }

        internal sealed class BlacklistRow
        {
            public string Prefab = "";
            public int Kind;   // 0 piece, 1 recipe
        }

        private static List<PinRow> ReadPins()
        {
            var res = new List<PinRow>();
            Dictionary<string, string> t;
            try { t = FeatureStore.Table(TblPins); }
            catch (Exception) { return res; }
            foreach (var kv in t)
            {
                int id;
                if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                var p = (kv.Value ?? "").Split('|');
                if (p.Length < 5) continue;
                float x, y, z; int icon;
                if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                if (!float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out icon)) icon = 3;
                res.Add(new PinRow
                {
                    Id = id,
                    Name = p[0],
                    Pos = new Vector3(x, y, z),
                    Icon = IconOk(icon) ? icon : 3,
                    Category = p.Length > 5 ? p[5] : "",
                });
            }
            res.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (res.Count > PinCap) res.RemoveRange(PinCap, res.Count - PinCap);
            return res;
        }

        private static List<TraderRow> ReadTrader()
        {
            var res = new List<TraderRow>();
            Dictionary<string, string> t;
            try { t = FeatureStore.Table(TblTrader); }
            catch (Exception) { return res; }
            foreach (var kv in t)
            {
                var k = (kv.Key ?? "").Split('|');
                if (k.Length != 2) continue;
                int index;
                if (!int.TryParse(k[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) continue;
                var v = (kv.Value ?? "").Split('|');
                if (v.Length < 3) continue;
                int stack, price;
                if (!int.TryParse(v[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out stack)) stack = 1;
                if (!int.TryParse(v[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out price)) price = 1;
                res.Add(new TraderRow
                {
                    Trader = k[0],
                    Index = index,
                    Item = v[0],
                    Stack = Mathf.Clamp(stack, 1, 999),
                    Price = Mathf.Clamp(price, 0, 999999),
                    Key = v.Length > 3 ? v[3] : "",
                });
            }
            res.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.Trader, b.Trader);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            if (res.Count > TraderRowCap) res.RemoveRange(TraderRowCap, res.Count - TraderRowCap);
            return res;
        }

        private static List<BlacklistRow> ReadBlacklist()
        {
            var res = new List<BlacklistRow>();
            Dictionary<string, string> t;
            try { t = FeatureStore.Table(TblBlacklist); }
            catch (Exception) { return res; }
            foreach (var kv in t)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                var kind = string.Equals(kv.Value, "recipe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                res.Add(new BlacklistRow { Prefab = kv.Key, Kind = kind });
            }
            res.Sort((a, b) =>
            {
                var c = a.Kind.CompareTo(b.Kind);
                return c != 0 ? c : string.CompareOrdinal(a.Prefab, b.Prefab);
            });
            if (res.Count > BlacklistCap) res.RemoveRange(BlacklistCap, res.Count - BlacklistCap);
            return res;
        }

        // ---- skill rules live in config, not in a table ----

        private static float SkillMult() =>
            _skillMult != null ? Mathf.Clamp(_skillMult.Value, 0.1f, 10f) : 1f;

        private static int SkillCap() =>
            _skillCap != null ? Mathf.Clamp(_skillCap.Value, 0, 100) : 0;

        private static string SkillOverridesRaw() =>
            _skillOverrides != null ? (_skillOverrides.Value ?? "") : "";

        /// <summary>
        /// "Swords=2,Bows=0.5" -> (type, multiplier) pairs. Unknown names, None/All and non-numbers are
        /// skipped; multipliers clamp to 0-10 (0 = that skill never gains). Shared with the client partial.
        /// </summary>
        internal static List<KeyValuePair<Skills.SkillType, float>> ParseOverrides(string raw)
        {
            var res = new List<KeyValuePair<Skills.SkillType, float>>();
            if (string.IsNullOrEmpty(raw)) return res;
            foreach (var part in raw.Split(','))
            {
                if (res.Count >= OverrideCap) break;
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part.Substring(0, eq).Trim();
                var val = part.Substring(eq + 1).Trim();
                Skills.SkillType type;
                float mult;
                if (!TryParseSkill(name, out type)) continue;
                if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out mult)) continue;
                if (float.IsNaN(mult) || float.IsInfinity(mult)) continue;
                mult = Mathf.Clamp(mult, 0f, 10f);
                var dup = false;
                for (var i = 0; i < res.Count; i++) if (res[i].Key == type) { dup = true; break; }
                if (!dup) res.Add(new KeyValuePair<Skills.SkillType, float>(type, mult));
            }
            return res;
        }

        private static bool TryParseSkill(string name, out Skills.SkillType type)
        {
            type = Skills.SkillType.None;
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                foreach (Skills.SkillType t in Enum.GetValues(typeof(Skills.SkillType)))
                {
                    if (t == Skills.SkillType.None || t == Skills.SkillType.All) continue;
                    if (string.Equals(t.ToString(), name, StringComparison.OrdinalIgnoreCase)) { type = t; return true; }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static string CanonicalOverrides(string raw)
        {
            var list = ParseOverrides(raw);
            var sb = new StringBuilder();
            foreach (var kv in list)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key.ToString()).Append('=').Append(kv.Value.ToString("0.##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ==================== payload builders (executor and admin reply share one shape) ====================
        // withRows = false ships an EMPTY table (the disabled-channel form): clients drop what they applied.

        // AP_MapPins / AP_MapPinsData: {int ver, bool enabled, int n(<=200), n x (int id, string name,
        //                               float x, float y, float z, int icon, string category)}
        private static ZPackage BuildPins(bool enabled, bool withRows)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(enabled);
            var rows = withRows ? ReadPins() : new List<PinRow>();
            pkg.Write(rows.Count);
            foreach (var r in rows)
            {
                pkg.Write(r.Id);
                pkg.Write(r.Name ?? "");
                pkg.Write(r.Pos.x); pkg.Write(r.Pos.y); pkg.Write(r.Pos.z);
                pkg.Write(r.Icon);
                pkg.Write(r.Category ?? "");
            }
            return pkg;
        }

        // AP_TraderStock / AP_TraderStockData: {int ver, bool enabled, int n(<=100), n x (string trader,
        //                                       int index, string item, int stack, int price, string key)}
        private static ZPackage BuildTrader(bool enabled, bool withRows)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(enabled);
            var rows = withRows ? ReadTrader() : new List<TraderRow>();
            pkg.Write(rows.Count);
            foreach (var r in rows)
            {
                pkg.Write(r.Trader ?? "");
                pkg.Write(r.Index);
                pkg.Write(r.Item ?? "");
                pkg.Write(r.Stack);
                pkg.Write(r.Price);
                pkg.Write(r.Key ?? "");
            }
            return pkg;
        }

        // AP_Blacklist / AP_BlacklistData: {int ver, bool enabled, int n(<=200), n x (string prefab, int kind)}
        private static ZPackage BuildBlacklist(bool enabled, bool withRows)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(enabled);
            var rows = withRows ? ReadBlacklist() : new List<BlacklistRow>();
            pkg.Write(rows.Count);
            foreach (var r in rows)
            {
                pkg.Write(r.Prefab ?? "");
                pkg.Write(r.Kind);
            }
            return pkg;
        }

        // AP_SkillRules / AP_SkillRulesData: {int ver, bool enabled, float mult, int cap, string overridesRaw,
        //                                     int n(<=30), n x (int skillType, float mult)}
        private static ZPackage BuildSkills(bool enabled)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(enabled);
            pkg.Write(SkillMult());
            pkg.Write(SkillCap());
            var raw = CanonicalOverrides(SkillOverridesRaw());
            pkg.Write(raw);
            var list = ParseOverrides(raw);
            pkg.Write(list.Count);
            foreach (var kv in list)
            {
                pkg.Write((int)kv.Key);
                pkg.Write(kv.Value);
            }
            return pkg;
        }

        private static ZPackage BuildChannel(int channel, bool forAdmin)
        {
            switch (channel)
            {
                case ChPins: return BuildPins(PinsOn, forAdmin || PinsOn);
                case ChTrader: return BuildTrader(TraderOn, forAdmin || TraderOn);
                case ChBlacklist: return BuildBlacklist(BlacklistOn, forAdmin || BlacklistOn);
                default: return BuildSkills(SkillsOn);
            }
        }

        private static string ExecutorName(int channel)
        {
            switch (channel)
            {
                case ChPins: return "AP_MapPins";
                case ChTrader: return "AP_TraderStock";
                case ChBlacklist: return "AP_Blacklist";
                default: return "AP_SkillRules";
            }
        }

        private static string ReplyName(int channel)
        {
            switch (channel)
            {
                case ChPins: return "AP_MapPinsData";
                case ChTrader: return "AP_TraderStockData";
                case ChBlacklist: return "AP_BlacklistData";
                default: return "AP_SkillRulesData";
            }
        }

        // ==================== broadcast ====================

        private static string Signature(int channel)
        {
            var sb = new StringBuilder();
            switch (channel)
            {
                case ChPins:
                    sb.Append(PinsOn ? '1' : '0');
                    if (PinsOn) AppendTable(sb, TblPins);
                    break;
                case ChTrader:
                    sb.Append(TraderOn ? '1' : '0');
                    if (TraderOn) AppendTable(sb, TblTrader);
                    break;
                case ChBlacklist:
                    sb.Append(BlacklistOn ? '1' : '0').Append(RemovePlacedOn ? '1' : '0');
                    if (BlacklistOn) AppendTable(sb, TblBlacklist);
                    break;
                default:
                    sb.Append(SkillsOn ? '1' : '0');
                    if (SkillsOn)
                        sb.Append('|').Append(SkillMult().ToString("R", CultureInfo.InvariantCulture))
                          .Append('|').Append(SkillCap().ToString(CultureInfo.InvariantCulture))
                          .Append('|').Append(SkillOverridesRaw());
                    break;
            }
            return sb.ToString();
        }

        private static void AppendTable(StringBuilder sb, string table)
        {
            Dictionary<string, string> t;
            try { t = FeatureStore.Table(table); }
            catch (Exception) { return; }
            var parts = new List<string>(t.Count);
            foreach (var kv in t) parts.Add(kv.Key + "=" + kv.Value);
            parts.Sort(StringComparer.Ordinal);
            foreach (var p in parts) sb.Append('|').Append(p);
        }

        private static void SyncChannels(bool force)
        {
            for (var ch = 0; ch < 4; ch++)
            {
                var sig = Signature(ch);
                if (!force && sig == LastSig[ch]) continue;
                var changed = sig != LastSig[ch];
                LastSig[ch] = sig;
                if (ch == ChBlacklist) RebuildBannedHashes();
                Broadcast(ch);
                if (changed) CompanionPlugin.FeatureLog($"Rules: channel {ChannelLabel(ch)} pushed to everybody ({(ChannelEnabled(ch) ? "on" : "off")}).");
            }
        }

        private static void Broadcast(int channel)
        {
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, ExecutorName(channel), BuildChannel(channel, false)); }
            catch (Exception e) { LogThrottled($"Rule broadcast {ExecutorName(channel)} failed: {e.Message}"); }
        }

        private static void SendAllTo(long uid)
        {
            for (var ch = 0; ch < 4; ch++)
            {
                try { ZRoutedRpc.instance?.InvokeRoutedRPC(uid, ExecutorName(ch), BuildChannel(ch, false)); }
                catch (Exception e) { LogThrottled($"Rule push {ExecutorName(ch)} to {uid} failed: {e.Message}"); }
            }
        }

        private static bool ChannelEnabled(int channel)
        {
            switch (channel)
            {
                case ChPins: return PinsOn;
                case ChTrader: return TraderOn;
                case ChBlacklist: return BlacklistOn;
                default: return SkillsOn;
            }
        }

        private static string ChannelLabel(int channel)
        {
            switch (channel)
            {
                case ChPins: return "map pins";
                case ChTrader: return "trader stock";
                case ChBlacklist: return "blacklist";
                default: return "skill rules";
            }
        }

        // Peer watcher: a Tick that diffs ZNet.GetPeers() rather than a third RPC_PeerInfo postfix — the join
        // event is already patched by CompanionPlugin, wave 1/2 and Wave34Core, and one more stacked patch
        // buys nothing a 2 s diff does not.
        private static void WatchPeers(float now)
        {
            var peers = ZNet.instance.GetPeers();
            if (peers == null) return;
            PeerScratch.Clear();
            for (var i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                if (p == null || !p.IsReady() || string.IsNullOrEmpty(p.m_playerName)) continue;
                PeerScratch.Add(p.m_uid);
                if (KnownPeers.Add(p.m_uid)) PendingJoins[p.m_uid] = now + JoinDelay;
            }
            if (KnownPeers.Count > PeerScratch.Count)
            {
                var gone = new List<long>();
                foreach (var uid in KnownPeers) if (!PeerScratch.Contains(uid)) gone.Add(uid);
                foreach (var uid in gone)
                {
                    KnownPeers.Remove(uid);
                    PendingJoins.Remove(uid);
                    PlacerNoticeAt.Remove(uid);
                }
            }
            PeerScratch.Clear();
        }

        private static void StepPendingJoins(float now)
        {
            List<long> due = null;
            foreach (var kv in PendingJoins)
                if (now >= kv.Value) (due ?? (due = new List<long>())).Add(kv.Key);
            if (due == null) return;
            foreach (var uid in due)
            {
                PendingJoins.Remove(uid);
                if (ZNet.instance.GetPeer(uid) == null) continue;   // left during the delay
                SendAllTo(uid);
            }
        }

        // ==================== admin RPCs: map pins (#14) ====================

        private static void OnMapPinsReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMapPinsReq")) return;
            Reply(sender, ChPins);
        }

        // AP_SrvMapPinSet: {int ver, int id (0 = new), string name, float x, float y, float z, int icon, string category}
        private static void OnMapPinSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMapPinSet")) return;

            int ver, id, icon; string name, category; float x, y, z;
            try
            {
                ver = pkg.ReadInt();
                id = pkg.ReadInt();
                name = pkg.ReadString();
                x = pkg.ReadSingle(); y = pkg.ReadSingle(); z = pkg.ReadSingle();
                icon = pkg.ReadInt();
                category = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvMapPinSet: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            name = CleanText(name, MaxPinName);
            category = CleanKey(category, MaxCategory, allowEmpty: true);
            if (name.Length == 0) { CompanionPlugin.NotifySender(sender, "Map pin rejected: the name must be 1-48 printable characters."); return; }
            if (category == null) { CompanionPlugin.NotifySender(sender, "Map pin rejected: the category may only contain letters, digits, '_', '-' and '.'."); return; }
            if (!IconOk(icon)) icon = 3;
            var pos = new Vector3(x, y, z);
            if (!PosOk(pos)) { CompanionPlugin.NotifySender(sender, "Map pin rejected: that position is not a valid point in the world."); return; }
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Map pins unavailable: the server has no world data directory yet."); return; }

            var t = FeatureStore.Table(TblPins);
            if (id <= 0)
            {
                if (t.Count >= PinCap) { CompanionPlugin.NotifySender(sender, $"Map pin limit reached ({PinCap}). Delete one first."); Reply(sender, ChPins); return; }
                id = 1;
                foreach (var kv in t)
                {
                    int existing;
                    if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out existing) && existing >= id) id = existing + 1;
                }
            }
            var key = id.ToString(CultureInfo.InvariantCulture);
            var isNew = !t.ContainsKey(key);
            t[key] = name + "|" + F(pos.x) + "|" + F(pos.y) + "|" + F(pos.z) + "|" +
                     icon.ToString(CultureInfo.InvariantCulture) + "|" + category;
            FeatureStore.SaveTable(TblPins);

            CompanionPlugin.SrvAudit(sender, "MAPPINSET", $"id={id} name={name} pos={F(pos.x)}/{F(pos.y)}/{F(pos.z)} icon={icon} category={category} new={(isNew ? 1 : 0)}");
            CompanionPlugin.FeatureLog($"Map pin {(isNew ? "added" : "updated")} by {CompanionPlugin.SenderDisplayName(sender)}: #{id} '{name}' at {F(pos.x)}/{F(pos.z)}");
            CompanionPlugin.NotifySender(sender, PinsOn
                ? $"Map pin '{name}' saved and pushed to modded clients."
                : $"Map pin '{name}' saved, but EnableMapPins is OFF - nothing is pushed until it is enabled.");
            SyncChannels(false);
            Reply(sender, ChPins);
        }

        private static void OnMapPinDel(long sender, int id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMapPinDel")) return;
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Map pins unavailable: the server has no world data directory yet."); return; }

            var t = FeatureStore.Table(TblPins);
            var key = id.ToString(CultureInfo.InvariantCulture);
            string old;
            if (!t.TryGetValue(key, out old)) { CompanionPlugin.NotifySender(sender, $"No map pin #{id}."); Reply(sender, ChPins); return; }
            t.Remove(key);
            FeatureStore.SaveTable(TblPins);
            var oldName = (old ?? "").Split('|')[0];
            CompanionPlugin.SrvAudit(sender, "MAPPINDEL", $"id={id} name={oldName}");
            CompanionPlugin.FeatureLog($"Map pin #{id} '{oldName}' deleted by {CompanionPlugin.SenderDisplayName(sender)}");
            CompanionPlugin.NotifySender(sender, $"Map pin '{oldName}' deleted.");
            SyncChannels(false);
            Reply(sender, ChPins);
        }

        // ==================== admin RPC: map reveal / reset (#15) ====================

        // AP_SrvMapReveal: {int ver, long target (0 = the sender), int mode (0 all / 1 radius / 2 reset), float radius}
        // Relays AP_MapReveal {int ver, int mode, float radius} to the target's client. Character data (the
        // explored map) lives on the player's machine, so the target must run the companion — HasMod gate,
        // self always allowed (a host targeting itself dispatches locally).
        private static void OnMapReveal(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvMapReveal")) return;

            int ver, mode; long target; float radius;
            try
            {
                ver = pkg.ReadInt();
                target = pkg.ReadLong();
                mode = pkg.ReadInt();
                radius = pkg.ReadSingle();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvMapReveal: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            mode = Mathf.Clamp(mode, 0, 2);
            if (float.IsNaN(radius) || float.IsInfinity(radius)) radius = 200f;
            radius = Mathf.Clamp(radius, 10f, 2000f);
            if (target == 0L) target = sender;

            var self = target == sender;
            if (!self)
            {
                var has = Wave34Core.HasMod(target);
                if (has != true)
                {
                    if (has == null) Wave34Core.ProbePeer(target);
                    CompanionPlugin.NotifySender(sender, "Map reveal not sent: " + Wave34Core.CapReason(target));
                    return;
                }
            }

            var relay = new ZPackage();
            relay.Write(Ver);
            relay.Write(mode);
            relay.Write(radius);
            try { ZRoutedRpc.instance?.InvokeRoutedRPC(target, "AP_MapReveal", relay); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_MapReveal to {target} failed: {e.Message}"); return; }

            var verb = mode == 0 ? "reveal-all" : mode == 1 ? $"reveal-radius {radius:0}" : "reset";
            var who = self ? "self" : CompanionPlugin.SenderDisplayName(target);
            CompanionPlugin.SrvAudit(sender, "MAPREVEAL", $"target={target} name={who} mode={verb}");
            CompanionPlugin.FeatureLog($"Map {verb} sent to {who} ({target}) by {CompanionPlugin.SenderDisplayName(sender)}");
            CompanionPlugin.NotifySender(sender, $"Map {verb} sent to {who}.");
        }

        // ==================== admin RPCs: trader stock (#18) ====================

        private static void OnTraderReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvTraderReq")) return;
            Reply(sender, ChTrader);
        }

        // AP_SrvTraderSet: {int ver, int action (0 set row, 1 remove row, 2 reset trader to vanilla),
        //                   string trader, int index (-1 = append), string item, int stack, int price, string key}
        private static void OnTraderSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvTraderSet")) return;

            int ver, action, index, stack, price; string trader, item, gkey;
            try
            {
                ver = pkg.ReadInt();
                action = pkg.ReadInt();
                trader = pkg.ReadString();
                index = pkg.ReadInt();
                item = pkg.ReadString();
                stack = pkg.ReadInt();
                price = pkg.ReadInt();
                gkey = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvTraderSet: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            action = Mathf.Clamp(action, 0, 2);
            trader = CleanKey(trader, MaxTraderKey, allowEmpty: false);
            if (trader == null) { CompanionPlugin.NotifySender(sender, "Trader stock rejected: the trader key must be the prefab name (letters, digits, '_'), e.g. Haldor."); return; }
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Trader stock unavailable: the server has no world data directory yet."); return; }
            var t = FeatureStore.Table(TblTrader);
            var admin = CompanionPlugin.SenderDisplayName(sender);
            var prefix = trader + "|";

            if (action == 2)
            {
                var gone = new List<string>();
                foreach (var kv in t) if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) gone.Add(kv.Key);
                foreach (var k in gone) t.Remove(k);
                FeatureStore.SaveTable(TblTrader);
                CompanionPlugin.SrvAudit(sender, "TRADERRESET", $"trader={trader} rows={gone.Count}");
                CompanionPlugin.FeatureLog($"Trader '{trader}' reset to vanilla ({gone.Count} row(s)) by {admin}");
                CompanionPlugin.NotifySender(sender, gone.Count > 0
                    ? $"Trader '{trader}' reset to vanilla stock ({gone.Count} custom row(s) removed)."
                    : $"Trader '{trader}' had no custom rows.");
                SyncChannels(false);
                Reply(sender, ChTrader);
                return;
            }

            if (action == 1)
            {
                var key = prefix + index.ToString(CultureInfo.InvariantCulture);
                string old;
                if (!t.TryGetValue(key, out old)) { CompanionPlugin.NotifySender(sender, $"No row {index} for trader '{trader}'."); Reply(sender, ChTrader); return; }
                t.Remove(key);
                FeatureStore.SaveTable(TblTrader);
                CompanionPlugin.SrvAudit(sender, "TRADERROWDEL", $"trader={trader} index={index} was={old}");
                CompanionPlugin.FeatureLog($"Trader '{trader}' row {index} removed by {admin}");
                CompanionPlugin.NotifySender(sender, $"Row {index} removed from '{trader}'.");
                SyncChannels(false);
                Reply(sender, ChTrader);
                return;
            }

            // action 0: set / append a row
            item = CleanKey(item, MaxPrefabName, allowEmpty: false);
            if (item == null) { CompanionPlugin.NotifySender(sender, "Trader stock rejected: the item must be a prefab name (e.g. SwordIron)."); return; }
            if (ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(item) == null)
            {
                CompanionPlugin.NotifySender(sender, $"Trader stock rejected: no item prefab named '{item}' exists on this server.");
                return;
            }
            gkey = CleanKey(gkey, MaxGlobalKey, allowEmpty: true);
            if (gkey == null) { CompanionPlugin.NotifySender(sender, "Trader stock rejected: the required global key may only contain letters, digits, '_', '-' and '.'."); return; }
            stack = Mathf.Clamp(stack, 1, 999);
            price = Mathf.Clamp(price, 0, 999999);

            if (index < 0)
            {
                index = 0;
                foreach (var kv in t)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    int existing;
                    if (int.TryParse(kv.Key.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out existing) && existing >= index)
                        index = existing + 1;
                }
            }
            if (index >= TraderRowsPerTrader) { CompanionPlugin.NotifySender(sender, $"Trader '{trader}' is full ({TraderRowsPerTrader} rows). Remove one first."); Reply(sender, ChTrader); return; }
            var rowKey = prefix + index.ToString(CultureInfo.InvariantCulture);
            if (!t.ContainsKey(rowKey) && t.Count >= TraderRowCap) { CompanionPlugin.NotifySender(sender, $"Trader table full ({TraderRowCap} rows in total). Remove one first."); Reply(sender, ChTrader); return; }

            t[rowKey] = item + "|" + stack.ToString(CultureInfo.InvariantCulture) + "|" +
                        price.ToString(CultureInfo.InvariantCulture) + "|" + gkey;
            FeatureStore.SaveTable(TblTrader);
            CompanionPlugin.SrvAudit(sender, "TRADERROWSET", $"trader={trader} index={index} item={item} stack={stack} price={price} key={gkey}");
            CompanionPlugin.FeatureLog($"Trader '{trader}' row {index} = {stack}x {item} for {price} by {admin}");
            CompanionPlugin.NotifySender(sender, TraderOn
                ? $"'{trader}' row {index} saved ({stack}x {item} for {price}) and pushed to modded clients."
                : $"'{trader}' row {index} saved, but EnableTraderStock is OFF - clients keep vanilla stock until it is enabled.");
            SyncChannels(false);
            Reply(sender, ChTrader);
        }

        // ==================== admin RPCs: blacklist (#19) ====================

        private static void OnBlacklistReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBlacklistReq")) return;
            Reply(sender, ChBlacklist);
        }

        // AP_SrvBlacklistSet: {int ver, int action (0 add, 1 remove), string prefab, int kind (0 piece, 1 recipe)}
        private static void OnBlacklistSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvBlacklistSet")) return;

            int ver, action, kind; string prefab;
            try
            {
                ver = pkg.ReadInt();
                action = pkg.ReadInt();
                prefab = pkg.ReadString();
                kind = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvBlacklistSet: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            action = Mathf.Clamp(action, 0, 1);
            kind = Mathf.Clamp(kind, 0, 1);
            prefab = CleanKey(prefab, MaxPrefabName, allowEmpty: false);
            if (prefab == null) { CompanionPlugin.NotifySender(sender, "Blacklist rejected: give a prefab name (letters, digits, '_', '-', '.'), e.g. piece_ballista or SwordIron."); return; }
            if (!FeatureStore.Ready) { CompanionPlugin.NotifySender(sender, "Blacklist unavailable: the server has no world data directory yet."); return; }

            var t = FeatureStore.Table(TblBlacklist);
            var admin = CompanionPlugin.SenderDisplayName(sender);
            var kindLabel = kind == 1 ? "recipe" : "piece";

            if (action == 1)
            {
                if (!t.Remove(prefab)) { CompanionPlugin.NotifySender(sender, $"'{prefab}' is not on the blacklist."); Reply(sender, ChBlacklist); return; }
                FeatureStore.SaveTable(TblBlacklist);
                CompanionPlugin.SrvAudit(sender, "BLACKLISTDEL", $"prefab={prefab}");
                CompanionPlugin.FeatureLog($"Blacklist: '{prefab}' removed by {admin}");
                CompanionPlugin.NotifySender(sender, $"'{prefab}' removed from the blacklist.");
                SyncChannels(false);
                Reply(sender, ChBlacklist);
                return;
            }

            // Validate against what the server actually knows so a typo cannot sit in the table forever.
            // ZNetScene holds every networked prefab (pieces included); ObjectDB holds items and recipes.
            if (kind == 0)
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                if (ZNetScene.instance != null && go == null)
                {
                    CompanionPlugin.NotifySender(sender, $"Blacklist rejected: no prefab named '{prefab}' exists on this server (names are case-sensitive; use 'Use aimed piece').");
                    return;
                }
                if (go != null && go.GetComponent<Piece>() == null)
                {
                    CompanionPlugin.NotifySender(sender, $"Blacklist rejected: '{prefab}' is not a build piece.");
                    return;
                }
            }
            else if (ObjectDB.instance != null && !RecipeExists(prefab))
            {
                CompanionPlugin.NotifySender(sender, $"Blacklist rejected: no recipe produces an item prefab named '{prefab}' on this server (use the item's prefab name, e.g. SwordIron).");
                return;
            }
            if (!t.ContainsKey(prefab) && t.Count >= BlacklistCap) { CompanionPlugin.NotifySender(sender, $"Blacklist full ({BlacklistCap} entries). Remove one first."); Reply(sender, ChBlacklist); return; }

            t[prefab] = kindLabel;
            FeatureStore.SaveTable(TblBlacklist);
            CompanionPlugin.SrvAudit(sender, "BLACKLISTADD", $"prefab={prefab} kind={kindLabel}");
            CompanionPlugin.FeatureLog($"Blacklist: {kindLabel} '{prefab}' banned by {admin}");
            CompanionPlugin.NotifySender(sender, BlacklistOn
                ? $"{kindLabel} '{prefab}' banned. Modded clients hide it now{(kind == 0 && RemovePlacedOn ? "; the server removes it the moment anyone places it" : "")}."
                : $"{kindLabel} '{prefab}' saved to the blacklist, but EnableBlacklist is OFF - nothing is enforced until it is enabled.");
            SyncChannels(false);
            Reply(sender, ChBlacklist);
        }

        private static bool RecipeExists(string itemPrefab)
        {
            try
            {
                var db = ObjectDB.instance;
                if (db == null || db.m_recipes == null) return true;
                foreach (var r in db.m_recipes)
                {
                    if (r == null) continue;
                    if (r.m_item != null && r.m_item.gameObject != null && r.m_item.gameObject.name == itemPrefab) return true;
                    if (r.name == itemPrefab) return true;
                }
            }
            catch (Exception) { return true; }
            return false;
        }

        // ==================== admin RPCs: skill rules (#20) ====================

        private static void OnSkillRulesReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSkillRulesReq")) return;
            Reply(sender, ChSkills);
        }

        // AP_SrvSkillRulesSet: {int ver, bool enabled, float mult, int cap, string overrides}
        // Writes the config entries (BepInEx persists them), so the rules survive a restart.
        private static void OnSkillRulesSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvSkillRulesSet")) return;

            int ver, cap; bool enabled; float mult; string overrides;
            try
            {
                ver = pkg.ReadInt();
                enabled = pkg.ReadBool();
                mult = pkg.ReadSingle();
                cap = pkg.ReadInt();
                overrides = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvSkillRulesSet: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (_enableSkills == null || _skillMult == null || _skillCap == null || _skillOverrides == null)
            {
                CompanionPlugin.NotifySender(sender, "Skill rules unavailable: the companion config could not be bound.");
                return;
            }

            if (float.IsNaN(mult) || float.IsInfinity(mult)) mult = 1f;
            mult = Mathf.Clamp(mult, 0.1f, 10f);
            cap = cap <= 0 ? 0 : Mathf.Clamp(cap, 1, 100);
            overrides = overrides ?? "";
            if (overrides.Length > MaxOverridesText) overrides = overrides.Substring(0, MaxOverridesText);
            var canonical = CanonicalOverrides(overrides);

            try
            {
                _enableSkills.Value = enabled;
                _skillMult.Value = mult;
                _skillCap.Value = cap;
                _skillOverrides.Value = canonical;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Skill rules config write failed: {e.Message}");
                CompanionPlugin.NotifySender(sender, "Skill rules could not be written to the config - see the server log.");
                return;
            }

            var detail = $"enabled={(enabled ? 1 : 0)} mult={mult.ToString("0.##", CultureInfo.InvariantCulture)} cap={cap} overrides={canonical}";
            CompanionPlugin.SrvAudit(sender, "SKILLRULESSET", detail);
            CompanionPlugin.FeatureLog($"Skill rules set by {CompanionPlugin.SenderDisplayName(sender)}: {detail}");
            CompanionPlugin.NotifySender(sender, enabled
                ? $"Skill rules applied: gain x{mult.ToString("0.##", CultureInfo.InvariantCulture)}, cap {(cap == 0 ? "none" : cap.ToString(CultureInfo.InvariantCulture))}{(canonical.Length > 0 ? ", overrides " + canonical : "")}. Pushed to modded clients."
                : "Skill rules saved and DISABLED - modded clients fall back to vanilla skill gain.");
            SyncChannels(false);
            Reply(sender, ChSkills);
        }

        // ==================== admin RPC: channel on/off (owner-only) ====================

        // AP_SrvRulesToggle: {int ver, int channel, bool on} — flips the channel's Enable* config entry so the
        // switch persists, exactly as if the operator had edited the config file.
        private static void OnRulesToggle(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvRulesToggle")) return;

            int ver, channel; bool on;
            try
            {
                ver = pkg.ReadInt();
                channel = pkg.ReadInt();
                on = pkg.ReadBool();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvRulesToggle: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            channel = Mathf.Clamp(channel, 0, 3);

            ConfigEntry<bool> entry;
            switch (channel)
            {
                case ChPins: entry = _enablePins; break;
                case ChTrader: entry = _enableTrader; break;
                case ChBlacklist: entry = _enableBlacklist; break;
                default: entry = _enableSkills; break;
            }
            if (entry == null) { CompanionPlugin.NotifySender(sender, "That rule channel has no config entry on this server."); return; }
            try { entry.Value = on; }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Rules toggle config write failed: {e.Message}");
                CompanionPlugin.NotifySender(sender, "The switch could not be written to the config - see the server log.");
                return;
            }
            CompanionPlugin.SrvAudit(sender, "RULESTOGGLE", $"channel={ChannelLabel(channel)} on={(on ? 1 : 0)}");
            CompanionPlugin.FeatureLog($"Rules: {ChannelLabel(channel)} turned {(on ? "ON" : "OFF")} by {CompanionPlugin.SenderDisplayName(sender)}");
            CompanionPlugin.NotifySender(sender, $"{ChannelLabel(channel)}: {(on ? "ON - pushed to modded clients" : "OFF - modded clients fall back to vanilla")}.");
            SyncChannels(false);
            Reply(sender, channel);
        }

        // ==================== blacklist: server-side removal of placed pieces ====================

        private static void RebuildBannedHashes()
        {
            BannedPieceHashes.Clear();
            BannedPieceNames.Clear();
            if (BlacklistOn && RemovePlacedOn)
            {
                foreach (var row in ReadBlacklist())
                {
                    if (row.Kind != 0 || string.IsNullOrEmpty(row.Prefab)) continue;
                    var h = row.Prefab.GetStableHashCode();
                    BannedPieceHashes.Add(h);
                    BannedPieceNames[h] = row.Prefab;
                }
            }
            _enforceActive = BannedPieceHashes.Count > 0;
            if (!_enforceActive) Pending.Clear();
        }

        // Postfix on the PRIVATE overload every incoming (client-sent) ZDO passes through. Hot path: one
        // volatile read and, only while a ban exists, one list append. prefabHashIn != 0 means the server
        // created the object itself (never a player placement) — skipped without a lookup.
        [HarmonyPatch(typeof(ZDOMan), "CreateNewZDO", typeof(ZDOID), typeof(Vector3), typeof(int))]
        internal static class Wave8RulesZDOManCreatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZDO __result, [HarmonyArgument(2)] int prefabHash)
            {
                if (!_enforceActive || __result == null || prefabHash != 0) return;
                try
                {
                    if (Pending.Count >= PendingCap)
                    {
                        if (!_pendingOverflowLogged)
                        {
                            _pendingOverflowLogged = true;
                            CompanionPlugin.FeatureLog($"Blacklist: more than {PendingCap} new objects arrived within one check interval; some placements in that burst were not inspected.");
                        }
                        return;
                    }
                    Pending.Add(new PendingZdo { Id = __result.m_uid, Rounds = 0 });
                }
                catch (Exception) { /* never break ZDO creation */ }
            }
        }

        private static void StepEnforcement(float now)
        {
            if (Pending.Count == 0) return;
            if (!_enforceActive) { Pending.Clear(); return; }
            var man = ZDOMan.instance;
            if (man == null) { Pending.Clear(); return; }
            var session = ZDOMan.GetSessionID();

            for (var i = Pending.Count - 1; i >= 0; i--)
            {
                var p = Pending[i];
                ZDO zdo = null;
                try { zdo = man.GetZDO(p.Id); } catch (Exception) { }
                if (zdo == null || !zdo.IsValid()) { Pending.RemoveAt(i); continue; }

                int prefab;
                long creator;
                try { prefab = zdo.GetPrefab(); creator = zdo.GetLong(KeyCreator, 0L); }
                catch (Exception) { Pending.RemoveAt(i); continue; }

                if (prefab == 0 || !BannedPieceHashes.Contains(prefab)) { Pending.RemoveAt(i); continue; }
                if (creator == 0L)
                {
                    // Either world-generated (never ours to remove) or the creator write has not arrived yet:
                    // look again a couple of times, then let it be.
                    if (++p.Rounds >= 3) Pending.RemoveAt(i);
                    else Pending[i] = p;
                    continue;
                }
                Pending.RemoveAt(i);

                ZNetPeer peer = null;
                try { var owner = zdo.GetOwner(); if (owner != 0L) peer = ZNet.instance.GetPeer(owner); }
                catch (Exception) { }
                if (peer != null && PeerIsAdmin(peer)) continue;   // admins are exempt, like protection zones

                string name;
                if (!BannedPieceNames.TryGetValue(prefab, out name)) name = prefab.ToString(CultureInfo.InvariantCulture);
                var pos = Vector3.zero;
                try { pos = zdo.GetPosition(); } catch (Exception) { }
                try
                {
                    zdo.SetOwner(session);   // DestroyZDO is a silent no-op without ownership
                    man.DestroyZDO(zdo);
                }
                catch (Exception e) { LogThrottled($"Blacklist: could not remove '{name}': {e.Message}"); continue; }

                var placer = peer != null ? peer.m_playerName : "?";
                CompanionPlugin.FeatureLog($"Blacklist: removed banned piece '{name}' placed by {placer} (builder id {creator}) at {F(pos.x)}/{F(pos.z)}");
                if (peer != null)
                {
                    CompanionPlugin.SrvAudit(peer.m_uid, "BLACKLISTREMOVE", $"prefab={name} builder={creator} pos={F(pos.x)}/{F(pos.y)}/{F(pos.z)}");
                    float next;
                    if (!PlacerNoticeAt.TryGetValue(peer.m_uid, out next) || now >= next)
                    {
                        PlacerNoticeAt[peer.m_uid] = now + PlacerNoticeInterval;
                        // Vanilla ShowMessage path: reaches UNMODDED clients (Wave1Moderation.SendPlayerText).
                        Wave1Moderation.SendPlayerText(peer.m_uid, $"'{name}' is banned on this server - it was removed.");
                    }
                }
            }
        }

        private static bool PeerIsAdmin(ZNetPeer peer)
        {
            try
            {
                var host = peer?.m_socket?.GetHostName();
                return !string.IsNullOrEmpty(host) && CompanionPlugin.FeatureIsAdminId(host);
            }
            catch (Exception) { return false; }
        }

        // ==================== small helpers ====================

        private static void Reply(long uid, int channel)
        {
            CompanionPlugin.ReplyTo(uid, ReplyName(channel), BuildChannel(channel, true));
        }

        internal static bool IconOk(int icon)
        {
            for (var i = 0; i < AllowedIcons.Length; i++) if (AllowedIcons[i] == icon) return true;
            return false;
        }

        private static bool PosOk(Vector3 p)
        {
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) return false;
            if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) return false;
            return Mathf.Abs(p.x) <= 20000f && Mathf.Abs(p.z) <= 20000f && Mathf.Abs(p.y) <= 5000f;
        }

        // Free text stored in a '|'-separated row: the separator and newlines can never survive.
        private static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // Identifier-shaped text (prefab names, trader keys, categories, global keys): letters, digits and
        // '_' '-' '.' only, so it is safe both as a store key and inside a '|' row. null = rejected.
        private static string CleanKey(string s, int max, bool allowEmpty)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return allowEmpty ? "" : null;
            if (s.Length > max) return null;
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') continue;
                return null;
            }
            return s;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
