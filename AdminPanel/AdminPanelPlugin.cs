using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AdminPanel
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class AdminPanelPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.halitb.adminpanel";
        public const string PluginName = "AdminPanel";
        public const string PluginVersion = "2.2.5";

        internal static AdminPanelPlugin Instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _mapTpKey;

        // ==================== Settings tab state ====================
        private ConfigEntry<string> _fontChoiceCfg;
        private ConfigEntry<int> _fontSizeCfg;
        private ConfigEntry<int> _panelAlphaCfg;
        private ConfigEntry<bool> _cameraLockCfg;
        private int _fontSizeLive;      // sliders preview live via these; committed to config on mouse-release
        private int _panelAlphaLive;
        private int _rebindTarget;         // 0 = none, 1 = panel toggle key, 2 = map-teleport key
        private int _rebindTargetLayout;   // snapshot taken on the Layout pass (same pattern as _openDropdownLayout)
        private Vector2 _settingsScroll;
        private static readonly string[] FontChoices = { "Norse (auto)", "Norse Bold", "Norse", "Averia Serif", "Default" };

        private bool _visible;
        private Rect _windowRect = new Rect(60, 60, 740, 680);
        private Rect _lastSavedRect;   // last rect persisted to disk — save only when the rect actually changes
        private int _tab;
        private static readonly string[] TabNames = { "Items", "Creatures", "Bosses", "Player", "World", "Players", "Server", "Settings" };

        // ==================== Items tab state ====================
        private string _itemSearch = "";
        private Vector2 _itemScroll;
        private int _itemAmount = 1;
        private int _itemQuality = 1;
        private long _giveTargetId;   // stable id of the selected give target (0 = nobody); survives roster changes
        private List<ZNet.PlayerInfo> _othersSnapshot;   // OtherPlayers() cached once per frame (Layout) for the Items tab
        private int _itemSort;   // index into ItemSortModes
        private List<ItemEntry> _itemWindowList; // virtualization window snapshot (Layout->Repaint consistency)
        private int _itemWindowFirst, _itemWindowVisible, _itemWindowTotal;
        private static readonly string[] ItemSortModes = { "A → Z", "Z → A", "Category" };
        private static readonly string[] CreatureSortModes = { "A → Z", "Z → A", "Faction" };
        private int _creatureSort;      // index into CreatureSortModes
        private string _openDropdown;   // id of the currently-expanded dropdown (null = none)
        private string _openDropdownLayout;   // snapshot of _openDropdown taken on the Layout event so option
                                              // controls emitted on Repaint/Mouse passes match the Layout count

        private class ItemEntry
        {
            public ItemDrop Drop;
            public string Prefab;
            public string Display;
            public string Info;        // precomputed one-line stat summary (built in RefreshCaches, never per-frame)
            public string Cat;
            public string Sub;
            public Sprite Icon;
            public bool IconTried;
        }

        // Cached status-effect model for the categorized browser. Built ONCE in RefreshCaches (never per OnGUI pass);
        // filtered copy is rebuilt only when the search text changes. Mirrors the ItemEntry / _filteredItemsCache pattern.
        private class SeEntry
        {
            public StatusEffect Se;
            public string Display;     // localized name
            public string Tooltip;     // short localized description of what it does
            public int Hash;           // NameHash() precomputed for the Apply call
            public int Bucket;         // index into SeBucketNames
        }

        // cached filter results — recomputed only when filters change (fixes per-frame lag)
        private List<ItemEntry> _filteredItemsCache;
        private string _itemFilterKey = "";
        private int _favVersion;
        private int _recentVersion;   // bumped on every MarkRecent so the Recent view isn't served a stale cache
        private List<CreatureEntry> _filteredCreaturesCache;
        private string _creatureFilterKey = "";
        private List<string> _creatureCats;    // faction category chips, cached (rebuilt in RefreshCaches)
        private List<string> _subCatsCache;    // item sub-category chips, cached per _mainCat
        private string _subCatsKey;

        private List<ItemEntry> _itemIndex;
        private List<SeEntry> _seIndex;               // categorized status-effect index (built in RefreshCaches)
        private List<SeEntry> _seFilteredCache;       // search-filtered snapshot (rebuilt only on search change)
        private string _seFilterKey;                  // last search text the filtered snapshot was built for
        private string _mainCat = "All";
        private string _subCat = "All";
        private ConfigEntry<string> _favoritesCfg;
        private ConfigEntry<string> _crafterNameCfg;
        private ConfigEntry<string> _bulkPackCfg;
        private HashSet<string> _favorites;
        private readonly List<ItemEntry> _recentItems = new List<ItemEntry>();

        private static readonly string[] MainCats =
        {
            "All", "★ Fav", "Recent", "Kits", "Weapons", "Shields", "Armor", "Accessories",
            "Ammo", "Tools", "Food & Potions", "Materials", "Trophies", "Misc"
        };

        private static readonly Dictionary<string, string> MaterialBiome = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Wood","Meadows"},{"Stone","Meadows"},{"Resin","Meadows"},{"Feathers","Meadows"},
            {"LeatherScraps","Meadows"},{"DeerHide","Meadows"},{"Flint","Meadows"},{"RawMeat","Meadows"},
            {"NeckTail","Meadows"},{"BoarMeat","Meadows"},{"DeerMeat","Meadows"},{"Honey","Meadows"},
            {"Dandelion","Meadows"},{"BeechSeeds","Meadows"},{"QueenBee","Meadows"},
            {"FineWood","Black Forest"},{"RoundLog","Black Forest"},{"Coal","Black Forest"},
            {"Tin","Black Forest"},{"TinOre","Black Forest"},{"Copper","Black Forest"},{"CopperOre","Black Forest"},
            {"CopperScrap","Black Forest"},{"Bronze","Black Forest"},{"BronzeNails","Black Forest"},
            {"TrollHide","Black Forest"},{"BoneFragments","Black Forest"},{"SurtlingCore","Black Forest"},
            {"GreydwarfEye","Black Forest"},{"Thistle","Black Forest"},{"AncientSeed","Black Forest"},
            {"Amber","Black Forest"},{"AmberPearl","Black Forest"},{"Ruby","Black Forest"},{"Coins","Black Forest"},
            {"CarrotSeeds","Black Forest"},{"FirCone","Black Forest"},{"PineCone","Black Forest"},
            {"Iron","Swamp"},{"IronScrap","Swamp"},{"WitheredBone","Swamp"},{"Chain","Swamp"},
            {"ElderBark","Swamp"},{"Guck","Swamp"},{"Ooze","Swamp"},{"Entrails","Swamp"},
            {"Bloodbag","Swamp"},{"TurnipSeeds","Swamp"},{"IronNails","Swamp"},{"Root","Swamp"},
            {"SerpentScale","Swamp"},{"Chitin","Swamp"},
            {"Silver","Mountain"},{"SilverOre","Mountain"},{"Obsidian","Mountain"},{"WolfPelt","Mountain"},
            {"WolfFang","Mountain"},{"FreezeGland","Mountain"},{"Crystal","Mountain"},{"WolfClaw","Mountain"},
            {"WolfHairBundle","Mountain"},{"JuteRed","Mountain"},{"DragonEgg","Mountain"},{"OnionSeeds","Mountain"},
            {"BlackMetal","Plains"},{"BlackMetalScrap","Plains"},{"Barley","Plains"},{"Flax","Plains"},
            {"LoxPelt","Plains"},{"Needle","Plains"},{"LinenThread","Plains"},{"Tar","Plains"},
            {"BarleyFlour","Plains"},{"LoxMeat","Plains"},{"Cloudberry","Plains"},
            {"BlackMarble","Mistlands"},{"Sap","Mistlands"},{"Eitr","Mistlands"},{"RefinedEitr","Mistlands"},
            {"YggdrasilWood","Mistlands"},{"Carapace","Mistlands"},{"Softtissue","Mistlands"},
            {"ScaleHide","Mistlands"},{"BlackCore","Mistlands"},{"Wisp","Mistlands"},{"Mandible","Mistlands"},
            {"RoyalJelly","Mistlands"},{"SeekerBrain","Mistlands"},{"JuteBlue","Mistlands"},
            {"FlametalNew","Ashlands"},{"FlametalOreNew","Ashlands"},{"Grausten","Ashlands"},
            {"CharredBone","Ashlands"},{"ProustitePowder","Ashlands"},{"CelestialFeather","Ashlands"},
            {"AskHide","Ashlands"},{"Blackwood","Ashlands"},{"SulfurStone","Ashlands"},
            {"MoltenCore","Ashlands"},{"CharcoalResin","Ashlands"},{"GemstoneRed","Ashlands"},
            {"GemstoneGreen","Ashlands"},{"GemstoneBlue","Ashlands"},{"BonemawSerpentMeat","Ashlands"},
        };

        private static readonly (string Name, (string Prefab, int Count)[] Items)[] GearKits =
        {
            ("Bronze Kit", new[]{("ArmorBronzeChest",1),("ArmorBronzeLegs",1),("HelmetBronze",1),("CapeDeerHide",1),("SwordBronze",1),("ShieldBronzeBuckler",1)}),
            ("Iron Kit", new[]{("ArmorIronChest",1),("ArmorIronLegs",1),("HelmetIron",1),("CapeTrollHide",1),("SwordIron",1),("ShieldBanded",1)}),
            ("Wolf Kit", new[]{("ArmorWolfChest",1),("ArmorWolfLegs",1),("HelmetDrake",1),("CapeWolf",1),("SwordSilver",1),("ShieldSilver",1)}),
            ("Padded Kit", new[]{("ArmorPaddedCuirass",1),("ArmorPaddedGreaves",1),("HelmetPadded",1),("CapeLox",1),("SwordBlackmetal",1),("ShieldBlackmetal",1)}),
            ("Carapace Kit", new[]{("ArmorCarapaceChest",1),("ArmorCarapaceLegs",1),("HelmetCarapace",1),("CapeFeather",1),("SwordMistwalker",1),("ShieldCarapace",1)}),
            ("Ashlands Kit", new[]{("ArmorAshlandsMediumChest",1),("ArmorAshlandsMediumlegs",1),("HelmetAshlandsMediumHood",1),("CapeAsksvin",1),("SwordNiedhogg",1),("ShieldFlametal",1)}),
            ("Food Pack", new[]{("FishAndBread",10),("MeatPlatter",10),("YggdrasilPorridge",10),("MeadHealthMajor",5),("MeadStaminaLingering",5)}),
            ("Builder Pack", new[]{("Hammer",1),("Hoe",1),("Cultivator",1),("Wood",50),("Stone",50),("IronNails",100),("FineWood",50)}),
        };

        // ==================== Creatures tab state ====================
        private class CreatureEntry
        {
            public GameObject Prefab;
            public string Name;
            public string Display;
            public string Faction;
            public bool Boss;
            public bool Tamable;
        }

        private string _creatureSearch = "";
        private Vector2 _creatureScroll;
        // virtualized-list window snapshot (computed on Layout, reused on Repaint so control counts match)
        private List<CreatureEntry> _creWindowList;
        private int _creWindowFirst, _creWindowVisible, _creWindowTotal;
        private int _creatureCount = 1;
        private int _creatureLevel = 1;
        private List<CreatureEntry> _creatureIndex;
        private string _creatureCat = "All";
        private bool _spawnAtCrosshair;
        private string _petName = "";
        private string _arenaA, _arenaB;
        private int _arenaCountA = 5, _arenaCountB = 5;
        private ConfigEntry<string> _spawnPresetsCfg;
        private string _presetName = "";

        // ==================== World tab state ====================
        private string _weather = "";
        private float _timeSlider = 0.5f;
        private bool _timeLocked;
        private float _windAngle = 0f, _windIntensity = 0.5f;
        private bool _windLocked;
        private ConfigEntry<string> _bookmarksCfg;
        private string _bookmarkName = "";
        private string _tpX = "0", _tpY = "0", _tpZ = "0";
        private string _newGlobalKey = "";
        private bool _peaceful;
        private Vector2 _worldScroll;

        // Quick-jump destinations by Valheim location name. GetLocationIcon returns the
        // world's actual (randomized) position, or false if that location isn't known —
        // so unknown/undiscovered ones simply don't show a button.
        private static readonly (string Label, string Location)[] QuickJumps =
        {
            ("Spawn", "StartTemple"),
            ("Eikthyr", "Eikthyrnir"),
            ("The Elder", "GDKing"),
            ("Bonemass", "Bonemass"),
            ("Moder", "Dragonqueen"),
            ("Yagluth", "GoblinKing"),
            ("The Queen", "Mistlands_DvergrBossEntrance1"),
            ("Fader", "FaderLocation"),
        };

        // ==================== Player tab state ====================
        private bool _god, _ghost, _fly, _noCost;
        private float _speedMult = 1f, _jumpMult = 1f;
        private bool _infiniteWeight, _noStamina, _oneHitKill;
        private float _pickupRange = 2f;
        private float _baseWalk = -1f, _baseRun, _baseSwim, _baseJump, _baseWeight, _basePickup;
        private Player _appliedTo;   // tracks the Player instance our buffs are applied to (re-apply on respawn)
        private string _seSearch = "";
        private Vector2 _seScroll;
        private bool _showStatusEffects;
        private Vector2 _playerScroll;

        // ==================== Players tab state ====================
        private Vector2 _playersScroll;
        private string _broadcastText = "";
        private ConfigEntry<string> _playerNotesCfg;
        private ConfigEntry<string> _windowRectCfg;
        private bool _resizing;
        private bool _notesDirty;   // per-player notes edited in memory but not yet flushed to disk
        private Dictionary<string, string> _playerNotes;
        private string _inspectPlayerName;
        private List<(string name, int stack, int quality)> _inspectInventory;
        private bool _inspectPending;
        private float _inspectRequestTime;
        private Vector2 _inspectScroll;

        // ==================== Server tab state ====================
        private readonly List<string> _joinLog = new List<string>();
        private HashSet<string> _lastSeenPlayers = new HashSet<string>();
        private bool _seenPlayersInit;
        private float _nextPlayerPoll;
        private float _nextInvClean;
        private Vector2 _serverScroll;
        private static readonly string[] RaidEvents =
        {
            "army_eikthyr", "army_theelder", "army_bonemass", "army_moder", "army_goblin",
            "army_seekers", "army_gjall", "foresttrolls", "skeletons", "blobs",
            "surtlings", "wolves", "bats", "army_charred"
        };

        // ==================== Bosses ====================
        private Vector2 _bossScroll;
        private static readonly (string Prefab, string Label, string OfferPrefab, int OfferCount)[] BossList =
        {
            ("Eikthyr", "Eikthyr", "TrophyDeer", 2),
            ("gd_king", "The Elder", "AncientSeed", 3),
            ("Bonemass", "Bonemass", "WitheredBone", 10),
            ("Dragon", "Moder", "DragonEgg", 3),
            ("GoblinKing", "Yagluth", "GoblinTotem", 5),
            ("SeekerQueen", "The Queen", "DvergrKeyFragment", 9),
            ("Fader", "Fader", "BellFragment", 3),
        };

        // ==================== Skin ====================
        private GUIStyle _windowStyle, _buttonStyle, _labelStyle, _headerStyle, _textFieldStyle, _toggleStyle;
        private GUIStyle _tabStyle, _catStyle, _rowEven, _rowOdd, _dimLabelStyle;
        private Texture2D _texWood;   // window background — kept so the opacity slider can recolor it in place
        private bool _skinReady;
        private bool _fontApplied;
        private float _nextFontTry;   // throttle the (expensive) font-asset scan while the native font is unresolved
        private int _fontTries;       // give up after a few attempts so the scan never runs every frame forever

        // ==================== Harmony cheat flags ====================
        internal static bool NoStaminaFlag;
        internal static bool OneHitKillFlag;

        [HarmonyPatch]
        private static class CheatPatches
        {
            [HarmonyPatch(typeof(Player), "UseStamina")]
            [HarmonyPrefix]
            private static bool NoStaminaPrefix(Player __instance)
            {
                return !(NoStaminaFlag && __instance == Player.m_localPlayer);
            }

            [HarmonyPatch(typeof(Character), "Damage")]
            [HarmonyPrefix]
            private static void OneHitPrefix(Character __instance, HitData hit)
            {
                if (!OneHitKillFlag || hit == null || Player.m_localPlayer == null) return;
                if (__instance == null || __instance == Player.m_localPlayer) return;
                try
                {
                    if (hit.GetAttacker() != Player.m_localPlayer) return;
                    hit.m_damage.m_damage = 1e9f;
                }
                catch { /* never let a bad hit break combat */ }
            }
        }

        // Free & show the mouse cursor while the panel is open. Valheim re-locks/hides the cursor every frame
        // in GameCamera.UpdateMouseCapture, so we override it and skip the vanilla capture while _visible.
        [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
        private static class CursorPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (Instance == null || !Instance._visible) return true; // let vanilla capture the mouse
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return false;
            }
        }

        // While the panel is open, stop world input (movement, attacks, mouse-look) so clicking buttons
        // doesn't swing the camera or move your character.
        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static class InputBlockPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (Instance != null && Instance._visible) __result = false;
            }
        }

        // Hard-block the local player's attacks while the panel is open. TakeInput being false doesn't stop
        // the same mouse click that presses a GUI button from also triggering an attack, so skip StartAttack.
        [HarmonyPatch(typeof(Humanoid), "StartAttack")]
        private static class BlockAttackPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Humanoid __instance)
            {
                // skip the attack entirely while the panel is open (prevents click-through swings/bow)
                return !(Instance != null && Instance._visible && __instance == Player.m_localPlayer);
            }
        }

        // While the panel is open, LOCK THE CAMERA so moving the mouse does NOT rotate the view — exactly like
        // when the inventory is open. Valheim funnels ALL mouse-look through the shared source ZInput.GetMouseDelta():
        // each frame PlayerController reads it and feeds it into Player.SetMouseLook, which rotates the player's eye
        // (the transform the camera follows). Zeroing the returned delta while _visible suppresses ONLY that look
        // input — it reads no camera fields and writes no camera/player state, so it is a strict NO-OP when the panel
        // is closed and is inherently reversible: the very next frame gets the real delta the instant _visible flips
        // false (nothing to restore even if _visible somehow stuck). This is the game's own "interface open" behavior
        // and does not touch the UpdateMouseCapture / TakeInput / StartAttack patches or the window-resize logic.
        // The target is resolved by name (ZInput lives in assembly_utils.dll) so a signature/assembly change can only
        // make the lock inert — it can never break mod load (the Awake registration is wrapped in try/catch).
        [HarmonyPatch]
        private static class CameraLockPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("ZInput");
                return t == null ? null : AccessTools.Method(t, "GetMouseDelta", Type.EmptyTypes);
            }

            [HarmonyPostfix]
            private static void Postfix(ref Vector2 __result)
            {
                // honors the Settings-tab toggle; null check covers the window before the config binds in Awake
                if (Instance != null && Instance._visible &&
                    (Instance._cameraLockCfg == null || Instance._cameraLockCfg.Value))
                    __result = Vector2.zero;
            }
        }

        private void Awake()
        {
            Instance = this;
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F7, "Key that opens/closes the admin panel");
            _mapTpKey = Config.Bind("General", "MapTeleportKey", KeyCode.T,
                "With the full map open, hover a spot and press this key to teleport there");
            _favoritesCfg = Config.Bind("Items", "Favorites", "", "Comma-separated favorite item prefabs");
            _crafterNameCfg = Config.Bind("Items", "CrafterName", "", "Crafter signature on given items (empty = none)");
            _bulkPackCfg = Config.Bind("Items", "BulkPack", "Wood:50,Stone:50,FineWood:30,Iron:30,BronzeNails:100",
                "Items granted by the Bulk Pack button (prefab:count, comma-separated)");
            _spawnPresetsCfg = Config.Bind("Creatures", "SpawnPresets", "", "Saved creature spawn presets");
            _bookmarksCfg = Config.Bind("World", "Bookmarks", "", "Saved teleport bookmarks");
            _playerNotesCfg = Config.Bind("Players", "Notes", "", "Per-player admin notes");
            _windowRectCfg = Config.Bind("General", "WindowRect", "60,60,740,680",
                "Admin panel window position+size x,y,width,height (auto-saved)");
            _fontChoiceCfg = Config.Bind("UI", "Font", FontChoices[0], new ConfigDescription(
                "Panel font. 'Norse (auto)' picks the best available game font; 'Default' is Unity's built-in font.",
                new AcceptableValueList<string>(FontChoices)));
            _fontSizeCfg = Config.Bind("UI", "FontSize", 13, new ConfigDescription(
                "Base font size for panel text; headers, tabs and the title scale with it.",
                new AcceptableValueRange<int>(10, 20)));
            _panelAlphaCfg = Config.Bind("UI", "PanelOpacity", 96, new ConfigDescription(
                "Panel background opacity in percent.", new AcceptableValueRange<int>(55, 100)));
            _cameraLockCfg = Config.Bind("UI", "CameraLockWhilePanelOpen", true,
                "Lock mouse-look while the panel is open (like the inventory). Turn off to keep the camera live.");
            _fontSizeLive = _fontSizeCfg.Value;
            _panelAlphaLive = _panelAlphaCfg.Value;
            try
            {
                var parts = _windowRectCfg.Value.Split(',');
                if (parts.Length == 4 &&
                    float.TryParse(parts[0], out var wx) && float.TryParse(parts[1], out var wy) &&
                    float.TryParse(parts[2], out var ww) && float.TryParse(parts[3], out var wh))
                    _windowRect = new Rect(wx, wy, ww, wh);
            }
            catch (Exception e) { Logger.LogWarning($"Bad WindowRect config, using default: {e.Message}"); }
            _lastSavedRect = _windowRect;
            _favorites = new HashSet<string>(
                _favoritesCfg.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            _playerNotes = ParseKv(_playerNotesCfg.Value);
            Harmony.CreateAndPatchAll(typeof(RpcRegistration));
            Harmony.CreateAndPatchAll(typeof(CheatPatches));
            try { Harmony.CreateAndPatchAll(typeof(CursorPatch)); }
            catch (Exception e) { Logger.LogWarning($"Cursor patch failed (panel still works): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(InputBlockPatch)); }
            catch (Exception e) { Logger.LogWarning($"Input-block patch failed (panel still works): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(BlockAttackPatch)); }
            catch (Exception e) { Logger.LogWarning($"Attack-block patch failed (panel still works): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(CameraLockPatch)); }
            catch (Exception e) { Logger.LogWarning($"Camera-lock patch failed (panel still works): {e.Message}"); }
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Press {_toggleKey.Value} in-game.");
        }

        // The '|' record and '=' field separators are structural, so any '|'/'=' inside a key or value must be
        // escaped or it corrupts the store (truncated values + phantom entries). Percent-encode on write, decode
        // on read. '%' is encoded first / decoded last so the scheme round-trips. Legacy unescaped values still
        // load unchanged (they contain no %25/%7C/%3D sequences).
        private static string EncKv(string s) => string.IsNullOrEmpty(s)
            ? s
            : s.Replace("%", "%25").Replace("|", "%7C").Replace("=", "%3D");

        private static string DecKv(string s) => string.IsNullOrEmpty(s)
            ? s
            : s.Replace("%3D", "=").Replace("%7C", "|").Replace("%25", "%");

        private static Dictionary<string, string> ParseKv(string raw)
        {
            var dict = new Dictionary<string, string>();
            foreach (var pair in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx > 0) dict[DecKv(pair.Substring(0, idx))] = DecKv(pair.Substring(idx + 1));
            }
            return dict;
        }

        private static string JoinKv(Dictionary<string, string> dict) =>
            string.Join("|", dict.Select(kv => $"{EncKv(kv.Key)}={EncKv(kv.Value)}"));

        // ==================== RPC plumbing ====================
        [HarmonyPatch]
        private static class RpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_InvData", OnInventoryData);
            }
        }

        private static void OnInventoryData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null) return;
            // NOTE: AP_InvData's `sender` is the INSPECTED player's peer id (the server relays it preserving the
            // original sender), NOT the server — so we must NOT reject on sender. The count bound + try/catch below
            // fully neutralize a malformed/hostile packet (it can at worst show bogus rows in the viewer, never crash).
            try
            {
                var playerName = pkg.ReadString();
                var count = pkg.ReadInt();
                if (count < 0 || count > 512) return;   // reject an implausible/hostile item count
                var list = new List<(string, int, int)>();
                for (var i = 0; i < count; i++)
                {
                    var name = pkg.ReadString();
                    var stack = pkg.ReadInt();
                    var quality = pkg.ReadInt();
                    list.Add((name, stack, quality));
                }
                self._inspectPlayerName = playerName;
                self._inspectInventory = list;
                self._inspectPending = false;
            }
            catch (Exception) { /* malformed/truncated packet — drop it rather than throw out of the RPC dispatch */ }
        }

        private static long PeerIdOf(ZNet.PlayerInfo info) => info.m_characterID.UserID;

        private static long ServerUid()
        {
            var peer = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            return peer != null ? peer.m_uid : 0L;
        }

        private long SelfUid()
        {
            if (ZNet.instance == null || LocalPlayer == null) return 0L;
            var myName = LocalPlayer.GetPlayerName();
            foreach (var p in ZNet.instance.GetPlayerList())
                if (p.m_name == myName) return PeerIdOf(p);
            return 0L;
        }

        // ==================== Lifecycle ====================
        private void Update()
        {
            // While the Settings tab is listening for a rebind, the hotkeys are suppressed so pressing the key
            // being (re)assigned doesn't also fire its old action in the same frame.
            if (_rebindTarget == 0 && Input.GetKeyDown(_toggleKey.Value))
            {
                _visible = !_visible;
                if (_visible) RefreshCaches();
                else { FlushNotes(); CommitUiSettings(); _openDropdown = null; }   // persist edits + drop leaked UI state on close
            }

            // Lazily (re)build the item/creature indices once their game DBs finish loading, in case the panel was
            // opened before ObjectDB/ZNetScene were ready (the open-toggle refresh would have left them null).
            if (_visible && (((_itemIndex == null || _seIndex == null) && ObjectDB.instance != null) ||
                             (_creatureIndex == null && ZNetScene.instance != null)))
                RefreshCaches();

            // map-point teleport: full map open + hover a spot + press the map-teleport key
            if (_rebindTarget == 0 && Input.GetKeyDown(_mapTpKey.Value) && LocalPlayer != null &&
                Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)
            {
                TeleportToMapCursor();
            }

            // Commit slider-adjusted UI settings once the drag ends (mirrors SaveWindowRect's write-on-release,
            // so dragging a slider never writes the config file per tick).
            if (_visible && Input.GetMouseButtonUp(0)) CommitUiSettings();

            // safety: if a previous build left the UI input system disabled, restore it
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && !es.enabled) es.enabled = true;

            // re-apply persistent buffs when the local Player instance changes (death/respawn/teleport)
            var lp = Player.m_localPlayer;
            if (lp != null && lp != _appliedTo)
            {
                ReapplyPlayerState(lp);
                _appliedTo = lp;
            }

            // auto-strip icon-less items — they crash InventoryGrid.UpdateGui every frame (freezes the bag)
            if (lp != null && Time.time >= _nextInvClean)
            {
                _nextInvClean = Time.time + 2f;
                var inv = lp.GetInventory();
                if (inv != null)
                {
                    var broken = inv.GetAllItems()
                        .Where(i => i.m_shared == null || i.m_shared.m_icons == null || i.m_shared.m_icons.Length == 0)
                        .ToList();
                    if (broken.Count > 0)
                    {
                        foreach (var it in broken) inv.RemoveItem(it);
                        Logger.LogWarning($"Auto-removed {broken.Count} icon-less item(s) that would freeze the inventory.");
                        lp.Message(MessageHud.MessageType.TopLeft, $"[Admin] Removed {broken.Count} broken item(s) from your bag");
                    }
                }
            }

            // join/leave tracker
            if (ZNet.instance != null && Time.time >= _nextPlayerPoll)
            {
                _nextPlayerPoll = Time.time + 3f;
                var now = new HashSet<string>(ZNet.instance.GetPlayerList().Select(p => p.m_name));
                if (_seenPlayersInit)
                {
                    foreach (var name in now.Except(_lastSeenPlayers))
                        _joinLog.Insert(0, $"{DateTime.Now:HH:mm} + {name} joined");
                    foreach (var name in _lastSeenPlayers.Except(now))
                        _joinLog.Insert(0, $"{DateTime.Now:HH:mm} - {name} left");
                }
                else _seenPlayersInit = true;   // first poll: seed silently, don't spam "joined"
                if (_joinLog.Count > 100) _joinLog.RemoveRange(100, _joinLog.Count - 100);
                _lastSeenPlayers = now;
            }
        }

        private void OnDisable() => FlushNotes();   // last-chance persist if the plugin is unloaded with edits pending

        // Persist per-player notes once, when the panel closes — instead of rewriting the whole config file on
        // every keystroke while the admin is typing (which stutters the GUI thread and thrashes the disk).
        private void FlushNotes()
        {
            if (!_notesDirty) return;
            _playerNotesCfg.Value = JoinKv(_playerNotes);
            Config.Save();
            _notesDirty = false;
        }

        // Re-capture base movement stats from a fresh Player and re-apply active buffs.
        // Valheim replaces Player.m_localPlayer on respawn, wiping instance-level buffs.
        private void ReapplyPlayerState(Player p)
        {
            _baseWalk = p.m_walkSpeed;
            _baseRun = p.m_runSpeed;
            _baseSwim = p.m_swimSpeed;
            _baseJump = p.m_jumpForce;
            _baseWeight = p.m_maxCarryWeight;
            _basePickup = p.m_autoPickupRange;

            if (_god) p.SetGodMode(true);
            if (_ghost) p.SetGhostMode(true);
            if (_noCost) p.SetNoPlacementCost(true);
            if (_infiniteWeight) p.m_maxCarryWeight = 100000f;
            if (_speedMult > 1.001f)
            {
                p.m_walkSpeed = _baseWalk * _speedMult;
                p.m_runSpeed = _baseRun * _speedMult;
                p.m_swimSpeed = _baseSwim * _speedMult;
            }
            if (_jumpMult > 1.001f) p.m_jumpForce = _baseJump * _jumpMult;
            if (_pickupRange > 2.001f) p.m_autoPickupRange = _pickupRange;

            // debug-fly cannot cleanly survive an instance swap; keep the toggle honest
            _fly = false;
        }

        private static Player LocalPlayer => Player.m_localPlayer;

        // Minimap.ScreenToWorldPoint is non-public — reflect it once and cache.
        private static System.Reflection.MethodInfo _screenToWorld;

        // Teleport the local player to the world point under the cursor on the open map.
        private void TeleportToMapCursor()
        {
            if (_screenToWorld == null)
                _screenToWorld = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
            if (_screenToWorld == null) { Message("Map teleport unavailable (game API changed)"); return; }

            var world = (Vector3)_screenToWorld.Invoke(Minimap.instance, new object[] { Input.mousePosition });
            TeleportToWorld(world, $"map point ({world.x:0}, {world.z:0})");
        }

        // Teleport to a world position, snapping to ground height at the destination.
        private void TeleportToWorld(Vector3 world, string label)
        {
            var y = world.y;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(world, out var gh))
                y = gh;
            LocalPlayer.TeleportTo(new Vector3(world.x, y + 1.5f, world.z), LocalPlayer.transform.rotation, true);
            Message($"Teleporting to {label}");
        }

        // ==================== Item / creature indexing ====================
        private static (string cat, string sub) Categorize(ItemDrop drop)
        {
            var shared = drop.m_itemData.m_shared;
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                    if (shared.m_skillType == Skills.SkillType.Pickaxes) return ("Tools", "Pickaxes");
                    return ("Weapons", shared.m_skillType.ToString());
                case ItemDrop.ItemData.ItemType.Shield: return ("Shields", "All");
                case ItemDrop.ItemData.ItemType.Helmet: return ("Armor", "Helmets");
                case ItemDrop.ItemData.ItemType.Chest: return ("Armor", "Chest");
                case ItemDrop.ItemData.ItemType.Legs: return ("Armor", "Legs");
                case ItemDrop.ItemData.ItemType.Shoulder: return ("Armor", "Capes");
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Customization: return ("Accessories", "All");
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable: return ("Ammo", "All");
                case ItemDrop.ItemData.ItemType.Consumable:
                    return ("Food & Potions", shared.m_food > 0f ? "Food" : "Potions & Other");
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Tool: return ("Tools", "All");
                case ItemDrop.ItemData.ItemType.Trophy: return ("Trophies", "All");
                case ItemDrop.ItemData.ItemType.Material:
                    return ("Materials", MaterialBiome.TryGetValue(drop.name, out var biome) ? biome : "Other");
                default: return ("Misc", "All");
            }
        }

        private static string LocalizeSafe(string token, string fallback)
        {
            var text = Localization.instance != null ? Localization.instance.Localize(token) : fallback;
            if (string.IsNullOrEmpty(text) || text.StartsWith("[")) text = fallback;
            return text;
        }

        // Localize a token, flatten newlines, trim, and truncate to a short single line (empty if none).
        private static string ShortDesc(string token, int max)
        {
            var text = LocalizeSafe(token, "");
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length > max) text = text.Substring(0, max).TrimEnd() + "…";
            return text;
        }

        // Non-zero damage components of a weapon/ammo, e.g. "Slash 24 · Fire 12". Empty if all zero.
        // Allocates a small list, but only at cache-build time (RefreshCaches), never per OnGUI pass.
        private static string BuildDamageLine(HitData.DamageTypes d)
        {
            var parts = new List<string>(4);
            if (d.m_damage > 0f) parts.Add($"Dmg {d.m_damage:0}");
            if (d.m_blunt > 0f) parts.Add($"Blunt {d.m_blunt:0}");
            if (d.m_slash > 0f) parts.Add($"Slash {d.m_slash:0}");
            if (d.m_pierce > 0f) parts.Add($"Pierce {d.m_pierce:0}");
            if (d.m_chop > 0f) parts.Add($"Chop {d.m_chop:0}");
            if (d.m_pickaxe > 0f) parts.Add($"Pickaxe {d.m_pickaxe:0}");
            if (d.m_fire > 0f) parts.Add($"Fire {d.m_fire:0}");
            if (d.m_frost > 0f) parts.Add($"Frost {d.m_frost:0}");
            if (d.m_lightning > 0f) parts.Add($"Lightning {d.m_lightning:0}");
            if (d.m_poison > 0f) parts.Add($"Poison {d.m_poison:0}");
            if (d.m_spirit > 0f) parts.Add($"Spirit {d.m_spirit:0}");
            return string.Join(" · ", parts);
        }

        // One-line stat summary shown on each item row instead of the raw prefab code. Decides by item TYPE,
        // checking food first (food stats can ride on Consumable OR Utility). Cached in ItemEntry.Info.
        private static string BuildStatLine(ItemDrop.ItemData.SharedData s)
        {
            if (s == null) return "";

            if (s.m_food > 0f)
            {
                var parts = new List<string>(4);
                parts.Add($"+{s.m_food:0} HP");
                if (s.m_foodStamina > 0f) parts.Add($"+{s.m_foodStamina:0} ST");
                if (s.m_foodEitr > 0f) parts.Add($"+{s.m_foodEitr:0} Eitr");
                parts.Add($"{s.m_foodBurnTime:0}s");
                return string.Join(" · ", parts);
            }

            switch (s.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                {
                    var dmg = BuildDamageLine(s.m_damages);
                    return dmg.Length > 0 ? dmg : ShortDesc(s.m_description, 60);
                }
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                {
                    var line = $"Armor {s.m_armor:0}";
                    if (s.m_armorPerLevel > 0f) line += $" (+{s.m_armorPerLevel:0}/lv)";
                    return line;
                }
                case ItemDrop.ItemData.ItemType.Shield:
                    return $"Block {s.m_blockPower:0}";
                default:
                    // Consumable non-food (meads/potions), materials, trophies, tools, utility, trinkets, misc…
                    return ShortDesc(s.m_description, 60);
            }
        }

        // Fixed, ordered status-effect buckets. Order here is the header order shown in the browser.
        private static readonly string[] SeBucketNames =
        {
            "Boss Powers", "Armor Set Bonuses", "Potions & Mead",
            "Debuffs & Environment", "Comfort & Rest", "Other"
        };
        private static readonly string[] SeDebuffKeys =
        {
            "Burning", "Cold", "Freezing", "Frost", "Wet", "Poison", "Smoked", "Tared",
            "Harpooned", "Stagger", "Encumbered", "Slime", "Lightning", "Immobilized",
            "Barnacle", "Puke", "Debuff", "Curse", "Bleeding"
        };
        private static readonly string[] SeComfortKeys =
        {
            "Rested", "Resting", "Shelter", "Comfort", "Campfire", "Warm", "Fire",
            "Sated", "SoftDeath", "Sitting", "Bed"
        };

        // Categorize a StatusEffect by its prefab .name (m_category is unreliable/empty on vanilla effects).
        // First match wins, in bucket-priority order, so every effect lands in exactly one bucket.
        private static int ClassifySe(string name)
        {
            if (string.IsNullOrEmpty(name)) return 5;
            if (name.StartsWith("GP_", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.StartsWith("SetEffect_", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.StartsWith("Potion_", StringComparison.OrdinalIgnoreCase)) return 2;
            foreach (var k in SeDebuffKeys)
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            foreach (var k in SeComfortKeys)
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            return 5;
        }

        // Search-filtered snapshot of _seIndex. Rebuilt ONLY when the search text changes (mirrors FilteredItems),
        // so the render loop just walks an already-built, frame-stable list. Preserves the bucket/name ordering.
        private List<SeEntry> FilteredStatusEffects()
        {
            if (_seFilteredCache != null && _seSearch == _seFilterKey) return _seFilteredCache;
            if (_seIndex == null)
            {
                _seFilteredCache = new List<SeEntry>();
                _seFilterKey = _seSearch;
                return _seFilteredCache;
            }
            IEnumerable<SeEntry> src = _seIndex;
            if (!string.IsNullOrEmpty(_seSearch))
                src = _seIndex.Where(e =>
                    e.Display.IndexOf(_seSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Se.name.IndexOf(_seSearch, StringComparison.OrdinalIgnoreCase) >= 0);
            _seFilteredCache = src.ToList();
            _seFilterKey = _seSearch;
            return _seFilteredCache;
        }

        private void RefreshCaches()
        {
            _itemIndex = null;
            _seIndex = null;
            _seFilteredCache = null;
            _seFilterKey = null;
            _creatureIndex = null;
            _creatureCats = null;
            _subCatsCache = null;
            _subCatsKey = null;
            if (ObjectDB.instance != null)
            {
                _itemIndex = ObjectDB.instance.m_items
                    .Select(go => go.GetComponent<ItemDrop>())
                    .Where(id => id != null)
                    .Select(id =>
                    {
                        var (cat, sub) = Categorize(id);
                        return new ItemEntry
                        {
                            Drop = id,
                            Prefab = id.name,
                            Display = LocalizeSafe(id.m_itemData.m_shared.m_name, id.name),
                            Info = BuildStatLine(id.m_itemData.m_shared),
                            Cat = cat,
                            Sub = sub
                        };
                    })
                    .OrderBy(e => e.Cat).ThenBy(e => e.Sub).ThenBy(e => e.Display)
                    .ToList();

                // Categorized status-effect index for the player-tab browser. Localization + hashing happen
                // here (once), so the render loop never allocates or localizes per frame.
                _seIndex = ObjectDB.instance.m_StatusEffects
                    .Where(se => se != null)
                    .Select(se => new SeEntry
                    {
                        Se = se,
                        Display = LocalizeSafe(se.m_name, se.name),
                        Tooltip = ShortDesc(se.m_tooltip, 90),
                        Hash = se.NameHash(),
                        Bucket = ClassifySe(se.name)
                    })
                    .OrderBy(e => e.Bucket).ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (ZNetScene.instance != null)
            {
                _creatureIndex = ZNetScene.instance.m_prefabs
                    .Where(p => p != null && p.GetComponent<Character>() != null && p.GetComponent<Player>() == null)
                    .Select(p =>
                    {
                        var ch = p.GetComponent<Character>();
                        return new CreatureEntry
                        {
                            Prefab = p,
                            Name = p.name,
                            Display = LocalizeSafe(ch.m_name, p.name),
                            Faction = ch.m_faction.ToString(),
                            Boss = ch.IsBoss(),
                            Tamable = p.GetComponent<Tameable>() != null
                        };
                    })
                    .OrderBy(e => e.Faction).ThenBy(e => e.Display)
                    .ToList();
                _creatureCats = new List<string> { "All", "Bosses", "Tamable" };
                _creatureCats.AddRange(_creatureIndex.Select(e => e.Faction).Distinct().OrderBy(f => f));
            }
        }

        // ==================== Skin ====================
        private static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void EnsureSkin()
        {
            if (_skinReady) return;
            _texWood = SolidTex(WoodColor(_panelAlphaLive));
            var woodLight = SolidTex(new Color(0.220f, 0.160f, 0.100f, 1f));
            var woodHover = SolidTex(new Color(0.310f, 0.230f, 0.130f, 1f));
            var woodActive = SolidTex(new Color(0.160f, 0.115f, 0.070f, 1f));
            var fieldBg = SolidTex(new Color(0.070f, 0.050f, 0.035f, 1f));
            var parchment = new Color(0.870f, 0.790f, 0.620f);
            var gold = new Color(0.980f, 0.780f, 0.350f);

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _texWood;
            _windowStyle.onNormal.background = _texWood;
            _windowStyle.normal.textColor = gold;
            _windowStyle.onNormal.textColor = gold;
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.fontSize = 15;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.normal.background = woodLight;
            _buttonStyle.hover.background = woodHover;
            _buttonStyle.active.background = woodActive;
            _buttonStyle.onNormal.background = woodHover;
            _buttonStyle.normal.textColor = parchment;
            _buttonStyle.hover.textColor = gold;
            _buttonStyle.active.textColor = gold;
            _buttonStyle.onNormal.textColor = gold;
            _buttonStyle.fontStyle = FontStyle.Bold;

            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _labelStyle.normal.textColor = parchment;

            _headerStyle = new GUIStyle(_labelStyle) { fontStyle = FontStyle.Bold, fontSize = 14 };
            _headerStyle.normal.textColor = gold;

            _textFieldStyle = new GUIStyle(GUI.skin.textField);
            _textFieldStyle.normal.background = fieldBg;
            _textFieldStyle.focused.background = fieldBg;
            _textFieldStyle.hover.background = fieldBg;
            _textFieldStyle.normal.textColor = parchment;
            _textFieldStyle.focused.textColor = gold;
            _textFieldStyle.hover.textColor = parchment;

            _toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 13 };
            _toggleStyle.normal.textColor = parchment;
            _toggleStyle.onNormal.textColor = gold;
            _toggleStyle.hover.textColor = parchment;
            _toggleStyle.onHover.textColor = gold;

            // polish: comfortable button padding + margins
            _buttonStyle.padding = new RectOffset(10, 10, 5, 5);
            _buttonStyle.margin = new RectOffset(3, 3, 3, 3);
            _buttonStyle.fontSize = 13;

            // main tab bar: bigger, bolder
            _tabStyle = new GUIStyle(_buttonStyle) { fontSize = 14 };
            _tabStyle.padding = new RectOffset(12, 12, 8, 8);
            _tabStyle.margin = new RectOffset(3, 3, 4, 4);
            var goldBg = SolidTex(new Color(0.42f, 0.30f, 0.12f, 1f));
            _tabStyle.onNormal.background = goldBg;
            _tabStyle.onHover.background = goldBg;

            // category chips: slightly smaller, clearly selected
            _catStyle = new GUIStyle(_buttonStyle) { fontSize = 12 };
            _catStyle.padding = new RectOffset(10, 10, 6, 6);
            _catStyle.margin = new RectOffset(3, 3, 4, 4);
            _catStyle.onNormal.background = goldBg;
            _catStyle.onHover.background = goldBg;

            // alternating row backgrounds for readability
            _rowEven = new GUIStyle();
            _rowEven.padding = new RectOffset(4, 4, 3, 3);
            _rowOdd = new GUIStyle(_rowEven);
            _rowOdd.normal.background = SolidTex(new Color(1f, 1f, 1f, 0.035f));

            _dimLabelStyle = new GUIStyle(_labelStyle) { fontSize = 12 };
            _dimLabelStyle.normal.textColor = new Color(0.62f, 0.55f, 0.44f);

            ApplyFontSizes();   // override the hardcoded defaults above with the configured base size

            _skinReady = true;
        }

        // Locate Valheim's native Norse font among loaded Unity fonts.
        private static Font FindValheimFont()
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            return fonts.FirstOrDefault(f => f.name.IndexOf("Norsebold", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? fonts.FirstOrDefault(f => f.name.IndexOf("AveriaSerifLibre", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? fonts.FirstOrDefault(f => f.name.IndexOf("Norse", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Resolve the font the Settings tab picked. Any choice that can't be found falls back to null,
        // which GUIStyle renders with Unity's built-in default font.
        private static Font FindFontFor(string choice)
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            switch (choice)
            {
                case "Norse Bold":
                    return fonts.FirstOrDefault(f => f.name.IndexOf("Norsebold", StringComparison.OrdinalIgnoreCase) >= 0);
                case "Norse":
                    return fonts.FirstOrDefault(f => f.name.IndexOf("Norse", StringComparison.OrdinalIgnoreCase) >= 0
                                                  && f.name.IndexOf("bold", StringComparison.OrdinalIgnoreCase) < 0);
                case "Averia Serif":
                    return fonts.FirstOrDefault(f => f.name.IndexOf("AveriaSerifLibre", StringComparison.OrdinalIgnoreCase) >= 0);
                default:   // "Norse (auto)" — best available game font
                    return FindValheimFont();
            }
        }

        // Apply the configured font to every text GUIStyle once it becomes available.
        private void ApplyFont()
        {
            if (_fontApplied) return;
            Font f = null;
            if (_fontChoiceCfg.Value != "Default")
            {
                // Throttle the engine-wide font scan to at most once per second (it is otherwise called every OnGUI
                // pass), and give up after a few tries so a font that never resolves can't peg the frame forever
                // (falling back to the default font instead).
                if (Time.time < _nextFontTry) return;
                _nextFontTry = Time.time + 1f;
                f = FindFontFor(_fontChoiceCfg.Value);
                if (f == null && ++_fontTries < 10) return;
            }
            foreach (var s in new[]
            {
                _windowStyle, _buttonStyle, _labelStyle, _headerStyle, _textFieldStyle,
                _toggleStyle, _tabStyle, _catStyle, _rowEven, _rowOdd, _dimLabelStyle
            })
                if (s != null) s.font = f;
            _fontApplied = true;
        }

        // ==================== GUI root ====================
        private void OnGUI()
        {
            if (!_visible) return;
            EnsureSkin();
            ApplyFont();
            // Keep the window sanely sized and fully on-screen. This also self-heals a bad saved size
            // (e.g. one left over from an older build) so it can never get stuck stretched off-screen.
            // Min width 660 keeps all 8 tabs clickable in one row (the tab row can't shrink below its text).
            _windowRect.width = Mathf.Clamp(_windowRect.width, 660f, Screen.width);
            _windowRect.height = Mathf.Clamp(_windowRect.height, 300f, Screen.height);
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - _windowRect.height));
            // GUI.Window (NOT GUILayout.Window) uses the rect size exactly. GUILayout.Window auto-grows to fit
            // its content, which — combined with the list height being derived from the window height — created a
            // runaway feedback loop that stretched the panel to full screen. GUI.Window breaks that loop.
            _windowRect = GUI.Window(918273, _windowRect, DrawWindow,
                $"⚔ Valheim Admin Panel ⚔   [{_toggleKey.Value} to close]", _windowStyle);
            // Save after the user finishes moving or resizing the window (only runs while _visible).
            if (Event.current.type == EventType.MouseUp) SaveWindowRect();
        }

        private void SaveWindowRect()
        {
            // Only write to disk when the window actually moved/resized. OnGUI calls this on every surviving
            // MouseUp (and the resize-end path calls it too), so without this guard a full config-file write
            // fires on stray clicks and the resize end double-saves.
            if (_windowRect == _lastSavedRect) return;
            _lastSavedRect = _windowRect;
            _windowRectCfg.Value = $"{_windowRect.x:0},{_windowRect.y:0},{_windowRect.width:0},{_windowRect.height:0}";
            Config.Save();
        }

        // Scale a scroll-view height with the window height so a taller window shows more rows.
        // reserve must cover everything that is NOT the scroll view: the title bar + tab row + spacing
        // (~75px) plus any per-tab content outside the list, plus ~25px so the resize grip stays clickable.
        // The reserves were tuned at font size 13; bigger fonts grow the title/tab rows, so compensate here
        // centrally instead of at every call site.
        private float ListView(float reserve) =>
            Mathf.Clamp(_windowRect.height - reserve - Mathf.Max(0, _fontSizeLive - 13) * 3f, 160f, 4000f);

        private void DrawWindow(int id)
        {
            if (LocalPlayer == null)
            {
                GUILayout.Label("Not in game (no local player).", _labelStyle);
                GUI.DragWindow();
                return;
            }

            // Snapshot which dropdown is open on the Layout pass so option controls emitted on later passes of the
            // same frame match the Layout control count (a live _openDropdown flips on the click/MouseUp pass,
            // diverging from Layout -> IMGUI "control N in a group with only M controls" exception).
            if (Event.current.type == EventType.Layout)
            {
                _openDropdownLayout = _openDropdown;
                _rebindTargetLayout = _rebindTarget;
            }

            GUILayout.BeginHorizontal();
            for (var i = 0; i < TabNames.Length; i++)
            {
                var pressed = GUILayout.Toggle(_tab == i, TabNames[i], _tabStyle);
                if (pressed && _tab != i) { _tab = i; _openDropdown = null; _rebindTarget = 0; }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(14);

            // A tab handler that dereferences a not-yet-ready game singleton could throw mid-window, skipping the
            // End* calls and the GUI.DragWindow below. Contain it so the drag/resize code still runs (IMGUI
            // re-inits its layout stack next frame, so the panel self-heals rather than getting stuck).
            try
            {
                switch (_tab)
                {
                    case 0: DrawItemsTab(); break;
                    case 1: DrawCreaturesTab(); break;
                    case 2: DrawBossesTab(); break;
                    case 3: DrawPlayerTab(); break;
                    case 4: DrawWorldTab(); break;
                    case 5: DrawPlayersTab(); break;
                    case 6: DrawServerTab(); break;
                    case 7: DrawSettingsTab(); break;
                }
            }
            catch (Exception ex) { Logger.LogError($"AdminPanel tab {_tab} draw error: {ex.Message}"); }

            // Resize grip in the bottom-right corner. mousePosition inside a GUILayout.Window is relative to the
            // window's top-left, so using it directly for width/height is correct.
            var grip = new Rect(_windowRect.width - 22, _windowRect.height - 22, 22, 22);
            GUI.Label(grip, "◢", _dimLabelStyle);
            var e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition))
            {
                _resizing = true;
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseDrag)
            {
                _windowRect.width = Mathf.Clamp(e.mousePosition.x + 11, 660f, Screen.width - _windowRect.x);
                _windowRect.height = Mathf.Clamp(e.mousePosition.y + 11, 300f, Screen.height - _windowRect.y);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _resizing)
            {
                _resizing = false;
                SaveWindowRect();
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // ==================== helpers ====================
        private List<ZNet.PlayerInfo> OtherPlayers()
        {
            if (ZNet.instance == null) return new List<ZNet.PlayerInfo>();
            var myName = LocalPlayer != null ? LocalPlayer.GetPlayerName() : "";
            return ZNet.instance.GetPlayerList().Where(p => p.m_name != myName).ToList();
        }

        // The give target is stored by stable peer id, not by list position, so it stays pinned to the intended
        // player even when someone earlier in the roster disconnects and the list shifts.
        private int GiveTargetIndex(List<ZNet.PlayerInfo> others)
        {
            if (_giveTargetId == 0L) return -1;
            return others.FindIndex(p => PeerIdOf(p) == _giveTargetId);
        }

        private string GiveTargetName(List<ZNet.PlayerInfo> others)
        {
            var idx = GiveTargetIndex(others);
            return idx < 0 ? "(nobody)" : others[idx].m_name;
        }

        private void CycleGiveTarget(List<ZNet.PlayerInfo> others)
        {
            if (others.Count == 0) { _giveTargetId = 0L; return; }
            var idx = (GiveTargetIndex(others) + 1) % others.Count;   // -1 (nobody) advances to the first entry
            _giveTargetId = PeerIdOf(others[idx]);
        }

        private Vector3 SpawnPos(float distance = 3f)
        {
            if (_spawnAtCrosshair && GameCamera.instance != null)
            {
                var cam = GameCamera.instance.transform;
                if (Physics.Raycast(cam.position, cam.forward, out var hit, 200f))
                    return hit.point + Vector3.up * 0.3f;
            }
            return LocalPlayer.transform.position + LocalPlayer.transform.forward * distance + Vector3.up * 0.5f;
        }

        // Send a server-executor RPC to the server peer. ServerUid() is 0 when not connected, and 0 aliases
        // ZRoutedRpc.Everybody, so an unguarded send would broadcast the admin packet to every peer instead of
        // dropping it. On a listen-server/host (IsServer) target 0 is legitimate (the host handles it locally).
        private void SrvRpc(string method, params object[] parameters)
        {
            var s = ServerUid();
            if (s == 0L && !(ZNet.instance != null && ZNet.instance.IsServer()))
            { Message("Not connected to a server"); return; }
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(s, method, parameters);
        }

        private void SendServerSpawn(int kind, string prefabName, Vector3 pos, int count, int levelOrQuality, bool tamed, string petName = "")
        {
            var pkg = new ZPackage();
            pkg.Write(kind);
            pkg.Write(prefabName);
            pkg.Write(pos);
            pkg.Write(count);
            pkg.Write(levelOrQuality);
            pkg.Write(tamed);
            pkg.Write(petName ?? "");
            SrvRpc("AP_SrvSpawn", pkg);
        }

        private void SendServerGive(long targetUid, string prefabName, int amount, int quality)
        {
            var pkg = new ZPackage();
            pkg.Write(targetUid);
            pkg.Write(prefabName);
            pkg.Write(amount);
            pkg.Write(quality);
            pkg.Write(_crafterNameCfg.Value ?? "");
            SrvRpc("AP_SrvGive", pkg);
        }

        private void Message(string text)
        {
            LocalPlayer?.Message(MessageHud.MessageType.TopLeft, $"[Admin] {text}");
            Logger.LogInfo(text);
        }

        // ==================== Items tab ====================
        private static bool HasIcon(ItemDrop drop)
        {
            var icons = drop.m_itemData.m_shared.m_icons;
            return icons != null && icons.Length > 0;
        }

        private void DrawIcon(ItemEntry e)
        {
            var rect = GUILayoutUtility.GetRect(26, 26, GUILayout.Width(26), GUILayout.Height(26));
            if (!e.IconTried)
            {
                e.IconTried = true;
                try { e.Icon = e.Drop.m_itemData.GetIcon(); } catch { }
            }
            var sprite = e.Icon;
            if (sprite == null || sprite.texture == null) return;
            var tr = sprite.textureRect;
            var tex = sprite.texture;
            var coords = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height);
            GUI.DrawTextureWithTexCoords(rect, tex, coords);
        }

        private void ToggleFavorite(string prefab)
        {
            if (!_favorites.Remove(prefab)) _favorites.Add(prefab);
            _favoritesCfg.Value = string.Join(",", _favorites);
            _favVersion++;
            Config.Save();
        }

        private void MarkRecent(ItemEntry e)
        {
            _recentItems.RemoveAll(r => r.Prefab == e.Prefab);
            _recentItems.Insert(0, e);
            if (_recentItems.Count > 25) _recentItems.RemoveAt(_recentItems.Count - 1);
            _recentVersion++;   // invalidate the filter cache even when Count is unchanged (reorder / tail-drop)
        }

        private List<ItemEntry> FilteredItems()
        {
            var key = $"{_mainCat}|{_subCat}|{_itemSearch}|{_favVersion}|{_recentItems.Count}|{_recentVersion}|{_itemSort}";
            if (_filteredItemsCache != null && key == _itemFilterKey) return _filteredItemsCache;

            IEnumerable<ItemEntry> src = _itemIndex;
            if (_mainCat == "★ Fav") src = _itemIndex.Where(e => _favorites.Contains(e.Prefab));
            else if (_mainCat == "Recent") src = _recentItems;
            else if (_mainCat != "All")
            {
                src = _itemIndex.Where(e => e.Cat == _mainCat);
                if (_subCat != "All") src = src.Where(e => e.Sub == _subCat);
            }
            if (!string.IsNullOrEmpty(_itemSearch))
                src = src.Where(e =>
                    e.Display.IndexOf(_itemSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Prefab.IndexOf(_itemSearch, StringComparison.OrdinalIgnoreCase) >= 0);

            if (_mainCat != "Recent") // Recent keeps its most-recent-first order
            {
                switch (_itemSort)
                {
                    case 1: src = src.OrderByDescending(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                    case 2: src = src.OrderBy(e => e.Cat).ThenBy(e => e.Sub).ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                    default: src = src.OrderBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                }
            }

            _filteredItemsCache = src.ToList();
            _itemFilterKey = key;
            return _filteredItemsCache;
        }

        private void GiveKit((string Name, (string Prefab, int Count)[] Items) kit, long targetUid)
        {
            foreach (var (prefab, count) in kit.Items)
                SendServerGive(targetUid, prefab, count, 1);
            Message($"Kit '{kit.Name}' sent");
        }

        // ---- Reusable inline dropdown ----
        // Split into trigger + popup so the option list can render BELOW a horizontal row (call DropdownButton
        // inside the row, then DropdownOptions after EndHorizontal). The trigger sets _openDropdown on click, but
        // the options render off _openDropdownLayout (snapshotted on the Layout pass) so the control count stays
        // consistent across a frame's Layout/Repaint/event passes.
        private void DropdownButton(string id, string label, string[] options, int selected, float width)
        {
            var cur = options[Mathf.Clamp(selected, 0, options.Length - 1)];
            if (GUILayout.Button($"{label}: {cur}  {(_openDropdown == id ? "▲" : "▼")}", _buttonStyle, GUILayout.Width(width)))
                _openDropdown = _openDropdown == id ? null : id;
        }
        private bool DropdownOptions(string id, string[] options, ref int selected, float width)
        {
            // Gate on the Layout snapshot, not the live flag: the trigger button flips _openDropdown during the
            // click/MouseUp pass, so emitting options off the live flag would add controls the Layout pass never
            // reserved -> IMGUI "control N in a group with only M controls". Options appear on the next frame.
            if (_openDropdownLayout != id) return false;
            var changed = false;
            for (var i = 0; i < options.Length; i++)
            {
                if (GUILayout.Button((i == selected ? "• " : "    ") + options[i], _buttonStyle, GUILayout.Width(width)))
                { selected = i; _openDropdown = null; changed = true; }
            }
            return changed;
        }

        private void DrawItemsTab()
        {
            for (var row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                var half = (MainCats.Length + 1) / 2;
                for (var i = row * half; i < Mathf.Min(MainCats.Length, (row + 1) * half); i++)
                {
                    var pressed = GUILayout.Toggle(_mainCat == MainCats[i], MainCats[i], _catStyle);
                    if (pressed && _mainCat != MainCats[i]) { _mainCat = MainCats[i]; _subCat = "All"; _itemScroll = Vector2.zero; }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(12);

            // Rebuild the player list at most once per frame (on Layout) and reuse the snapshot for the Repaint
            // and input passes, instead of allocating a fresh GetPlayerList()+LINQ list on every OnGUI pass.
            if (Event.current.type == EventType.Layout || _othersSnapshot == null)
                _othersSnapshot = OtherPlayers();
            var others = _othersSnapshot;

            if (_mainCat == "Kits")
            {
                GUILayout.Label("Gear kits (delivered to inventory via server):", _headerStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Give target:", _labelStyle, GUILayout.Width(80));
                if (GUILayout.Button(GiveTargetName(others), _buttonStyle, GUILayout.Width(180)))
                    CycleGiveTarget(others);
                GUILayout.EndHorizontal();
                _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(Mathf.Min(ListView(300f), GearKits.Length * 28f + 96f)));
                foreach (var kit in GearKits)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(kit.Name, _labelStyle, GUILayout.Width(140));
                    GUILayout.Label(string.Join(", ", kit.Items.Select(i => i.Count > 1 ? $"{i.Prefab} x{i.Count}" : i.Prefab)), _labelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("To me", _buttonStyle, GUILayout.Width(70)))
                        GiveKit(kit, SelfUid());
                    if (GUILayout.Button("Give", _buttonStyle, GUILayout.Width(55)))
                    {
                        var gi = GiveTargetIndex(others);
                        if (gi >= 0) GiveKit(kit, PeerIdOf(others[gi]));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(10);
                GUILayout.Label("Bulk pack (edit in config file):", _headerStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(_bulkPackCfg.Value, _labelStyle);
                if (GUILayout.Button("Grab bulk pack", _buttonStyle, GUILayout.Width(120)))
                {
                    foreach (var part in _bulkPackCfg.Value.Split(','))
                    {
                        var bits = part.Trim().Split(':');
                        if (bits.Length == 2 && int.TryParse(bits[1], out var n))
                            SendServerGive(SelfUid(), bits[0].Trim(), n, 1);
                    }
                    Message("Bulk pack requested");
                }
                GUILayout.EndHorizontal();
                GUILayout.EndScrollView();
                return;
            }

            if (_itemIndex != null && _mainCat != "All" && _mainCat != "★ Fav" && _mainCat != "Recent")
            {
                // Cache the sub-category chips per main category instead of scanning the whole item index every pass.
                if (_subCatsCache == null || _subCatsKey != _mainCat)
                {
                    _subCatsCache = _itemIndex.Where(e => e.Cat == _mainCat).Select(e => e.Sub).Distinct().OrderBy(s => s).ToList();
                    _subCatsKey = _mainCat;
                }
                var subs = _subCatsCache;
                if (subs.Count > 1)
                {
                    GUILayout.BeginHorizontal();
                    var allPressed = GUILayout.Toggle(_subCat == "All", "All", _catStyle);
                    if (allPressed && _subCat != "All") { _subCat = "All"; _itemScroll = Vector2.zero; }
                    foreach (var sub in subs)
                    {
                        var pressed = GUILayout.Toggle(_subCat == sub, sub, _catStyle);
                        if (pressed && _subCat != sub) { _subCat = sub; _itemScroll = Vector2.zero; }
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(10);
                }
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", _labelStyle, GUILayout.Width(50));
            _itemSearch = GUILayout.TextField(_itemSearch, _textFieldStyle);
            GUILayout.Label("Amount:", _labelStyle, GUILayout.Width(55));
            var amountStr = GUILayout.TextField(_itemAmount.ToString(), _textFieldStyle, GUILayout.Width(50));
            int.TryParse(amountStr, out _itemAmount);
            GUILayout.Label("Quality:", _labelStyle, GUILayout.Width(50));
            var qStr = GUILayout.TextField(_itemQuality.ToString(), _textFieldStyle, GUILayout.Width(30));
            int.TryParse(qStr, out _itemQuality);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Give target:", _labelStyle, GUILayout.Width(80));
            if (GUILayout.Button(GiveTargetName(others), _buttonStyle, GUILayout.Width(180)))
                CycleGiveTarget(others);
            GUILayout.Label("Drop = ground | Bag = your bag | Give = target's bag", _labelStyle);
            GUILayout.FlexibleSpace();
            DropdownButton("itemSort", "Sort", ItemSortModes, _itemSort, 150);
            GUILayout.EndHorizontal();
            DropdownOptions("itemSort", ItemSortModes, ref _itemSort, 150);

            if (_itemIndex == null) { GUILayout.Label("Item DB not loaded.", _labelStyle); return; }

            GUILayout.Space(10);

            // virtualized list: only rows inside the viewport are rendered.
            // Snapshot the window on the Layout event so Repaint draws an identical control count — otherwise
            // BeginScrollView reassigning _itemScroll mid-pass causes the IMGUI "control N in a group with only N
            // controls" crash (same fix as DrawCreaturesTab).
            const float rowH = 32f;
            float viewH = ListView(330f);
            if (Event.current.type == EventType.Layout || _itemWindowList == null)
            {
                _itemWindowList = FilteredItems();
                _itemWindowTotal = _itemWindowList.Count;
                _itemWindowFirst = Mathf.Clamp(Mathf.FloorToInt(_itemScroll.y / rowH) - 1, 0, Mathf.Max(0, _itemWindowTotal));
                _itemWindowVisible = Mathf.Max(0, Mathf.Min(_itemWindowTotal - _itemWindowFirst, Mathf.CeilToInt(viewH / rowH) + 3));
            }
            var filtered = _itemWindowList;
            var total = _itemWindowTotal;
            var first = _itemWindowFirst;
            var visible = _itemWindowVisible;

            _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(viewH));
            if (first > 0) GUILayout.Space(first * rowH);
            for (var i = first; i < first + visible; i++)
            {
                var e = filtered[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd, GUILayout.Height(rowH - 2));
                if (GUILayout.Button(_favorites.Contains(e.Prefab) ? "★" : "☆", _buttonStyle, GUILayout.Width(30)))
                    ToggleFavorite(e.Prefab);
                DrawIcon(e);
                GUILayout.Space(6);
                GUILayout.Label(e.Display, _labelStyle, GUILayout.Width(210));
                GUILayout.Label(e.Info, _dimLabelStyle);   // stat summary (cached) instead of the raw prefab code
                GUILayout.FlexibleSpace();
                var amount = Math.Max(1, _itemAmount);
                var quality = Math.Max(1, _itemQuality);
                if (GUILayout.Button("Drop", _buttonStyle, GUILayout.Width(58)))
                { SendServerSpawn(0, e.Prefab, SpawnPos(1.5f), amount, quality, false); MarkRecent(e); Message($"Requested {amount}x {e.Display}"); }
                var bagSafe = HasIcon(e.Drop);
                if (GUILayout.Button(bagSafe ? "Bag" : "✕", _buttonStyle, GUILayout.Width(52)))
                {
                    if (bagSafe) { SendServerGive(SelfUid(), e.Prefab, amount, quality); MarkRecent(e); Message($"Requested {amount}x {e.Display} to bag"); }
                    else Message($"{e.Display} has no icon — it would corrupt your inventory (drop only)");
                }
                if (GUILayout.Button(bagSafe ? "Give" : "✕", _buttonStyle, GUILayout.Width(58)))
                {
                    var gi = GiveTargetIndex(others);
                    if (!bagSafe) Message($"{e.Display} has no icon — cannot be given");
                    else if (gi >= 0)
                    { SendServerGive(PeerIdOf(others[gi]), e.Prefab, amount, quality); MarkRecent(e); Message($"Sent {amount}x {e.Display} to {others[gi].m_name}"); }
                    else Message("No give target selected");
                }
                GUILayout.EndHorizontal();
            }
            var below = total - (first + visible);
            if (below > 0) GUILayout.Space(below * rowH);
            GUILayout.EndScrollView();
            GUILayout.Label($"{total} items", _dimLabelStyle);
        }

        // ==================== Creatures tab ====================
        private List<CreatureEntry> FilteredCreatures()
        {
            var key = $"{_creatureCat}|{_creatureSearch}|{_creatureSort}";
            if (_filteredCreaturesCache != null && key == _creatureFilterKey) return _filteredCreaturesCache;

            IEnumerable<CreatureEntry> src = _creatureIndex;
            if (_creatureCat == "Bosses") src = src.Where(e => e.Boss);
            else if (_creatureCat == "Tamable") src = src.Where(e => e.Tamable);
            else if (_creatureCat != "All") src = src.Where(e => e.Faction == _creatureCat);
            if (!string.IsNullOrEmpty(_creatureSearch))
                src = src.Where(e =>
                    e.Display.IndexOf(_creatureSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Name.IndexOf(_creatureSearch, StringComparison.OrdinalIgnoreCase) >= 0);

            switch (_creatureSort)
            {
                case 1: src = src.OrderByDescending(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                case 2: src = src.OrderBy(e => e.Faction).ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                default: src = src.OrderBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
            }

            _filteredCreaturesCache = src.ToList();
            _creatureFilterKey = key;
            return _filteredCreaturesCache;
        }

        private void DrawCreaturesTab()
        {
            if (_creatureIndex == null) { GUILayout.Label("Scene DB not loaded.", _labelStyle); return; }

            // Use the cached faction-category chips (built in RefreshCaches) rather than a Select/Distinct/OrderBy
            // over the whole creature index on every OnGUI pass. Lazily build once if the cache was missed.
            var cats = _creatureCats;
            if (cats == null)
            {
                cats = new List<string> { "All", "Bosses", "Tamable" };
                cats.AddRange(_creatureIndex.Select(e => e.Faction).Distinct().OrderBy(f => f));
                _creatureCats = cats;
            }
            for (var row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                var half = (cats.Count + 1) / 2;
                for (var i = row * half; i < Mathf.Min(cats.Count, (row + 1) * half); i++)
                {
                    var pressed = GUILayout.Toggle(_creatureCat == cats[i], cats[i], _catStyle);
                    if (pressed && _creatureCat != cats[i]) { _creatureCat = cats[i]; _creatureScroll = Vector2.zero; }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", _labelStyle, GUILayout.Width(50));
            _creatureSearch = GUILayout.TextField(_creatureSearch, _textFieldStyle);
            GUILayout.Label("Count:", _labelStyle, GUILayout.Width(45));
            var cStr = GUILayout.TextField(_creatureCount.ToString(), _textFieldStyle, GUILayout.Width(40));
            int.TryParse(cStr, out _creatureCount);
            GUILayout.Label("Stars:", _labelStyle, GUILayout.Width(40));
            var lStr = GUILayout.TextField((_creatureLevel - 1).ToString(), _textFieldStyle, GUILayout.Width(30));
            if (int.TryParse(lStr, out var stars)) _creatureLevel = Mathf.Clamp(stars, 0, 10) + 1;
            _spawnAtCrosshair = GUILayout.Toggle(_spawnAtCrosshair, " At crosshair", _toggleStyle);
            DropdownButton("creatureSort", "Sort", CreatureSortModes, _creatureSort, 150);
            GUILayout.EndHorizontal();
            DropdownOptions("creatureSort", CreatureSortModes, ref _creatureSort, 150);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Pet name:", _labelStyle, GUILayout.Width(60));
            _petName = GUILayout.TextField(_petName, _textFieldStyle, GUILayout.Width(120));
            if (GUILayout.Button("Undo last spawn", _buttonStyle, GUILayout.Width(120)))
            { SrvRpc("AP_SrvUndo"); Message("Undo requested"); }
            GUILayout.Label($"Arena: A={_arenaA ?? "?"} vs B={_arenaB ?? "?"}", _labelStyle);
            if (GUILayout.Button("FIGHT!", _buttonStyle, GUILayout.Width(60)) && _arenaA != null && _arenaB != null)
            {
                var center = SpawnPos(8f);
                SendServerSpawn(1, _arenaA, center + Vector3.left * 6f, _arenaCountA, 1, false);
                SendServerSpawn(1, _arenaB, center + Vector3.right * 6f, _arenaCountB, 1, false);
                Message($"Arena: {_arenaCountA}x {_arenaA} vs {_arenaCountB}x {_arenaB}");
            }
            GUILayout.EndHorizontal();

            // presets
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset:", _labelStyle, GUILayout.Width(45));
            _presetName = GUILayout.TextField(_presetName, _textFieldStyle, GUILayout.Width(110));
            foreach (var kv in ParseKv(_spawnPresetsCfg.Value))
            {
                if (GUILayout.Button(kv.Key, _buttonStyle))
                {
                    foreach (var spawn in kv.Value.Split(';'))
                    {
                        var s = spawn.Split(':');
                        if (s.Length >= 3 && int.TryParse(s[1], out var n) && int.TryParse(s[2], out var lvl))
                            SendServerSpawn(1, s[0], SpawnPos(4f), n, lvl, false);
                    }
                    Message($"Preset '{kv.Key}' spawned");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            const float rowH = 32f;
            float viewH = ListView(360f);
            // Snapshot the list + virtualization window ONCE per frame, on the Layout event, and reuse it for
            // Repaint/mouse passes. BeginScrollView reassigns _creatureScroll mid-pass, so deriving the window
            // live would give Layout and Repaint different control counts (the IMGUI "control N in a group with
            // only N controls" / unbalanced GUIClips crash).
            if (Event.current.type == EventType.Layout || _creWindowList == null)
            {
                _creWindowList = FilteredCreatures();
                _creWindowTotal = _creWindowList.Count;
                _creWindowFirst = Mathf.Clamp(Mathf.FloorToInt(_creatureScroll.y / rowH) - 1, 0, Mathf.Max(0, _creWindowTotal));
                _creWindowVisible = Mathf.Max(0, Mathf.Min(_creWindowTotal - _creWindowFirst, Mathf.CeilToInt(viewH / rowH) + 3));
            }
            var filtered = _creWindowList;
            var total = _creWindowTotal;
            var first = _creWindowFirst;
            var visible = _creWindowVisible;

            _creatureScroll = GUILayout.BeginScrollView(_creatureScroll, GUILayout.Height(viewH));
            if (first > 0) GUILayout.Space(first * rowH);
            for (var i = first; i < first + visible; i++)
            {
                var e = filtered[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd, GUILayout.Height(rowH - 2));
                GUILayout.Label(e.Display, _labelStyle, GUILayout.Width(180));
                GUILayout.Label(e.Name + (e.Boss ? "  [BOSS]" : "") + (e.Tamable ? "  [tamable]" : ""), _dimLabelStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("A", _buttonStyle, GUILayout.Width(26))) { _arenaA = e.Name; _arenaCountA = Math.Max(1, _creatureCount); }
                if (GUILayout.Button("B", _buttonStyle, GUILayout.Width(26))) { _arenaB = e.Name; _arenaCountB = Math.Max(1, _creatureCount); }
                if (GUILayout.Button("Save", _buttonStyle, GUILayout.Width(50)) && !string.IsNullOrEmpty(_presetName))
                {
                    var spawn = $"{e.Name}:{Math.Max(1, _creatureCount)}:{_creatureLevel}";
                    var presets = ParseKv(_spawnPresetsCfg.Value);
                    // Keyed by name (via the escaped KV store): re-saving the same name appends another creature
                    // with ';' (making multi-creature presets reachable) instead of stacking duplicate buttons.
                    presets[_presetName] = presets.TryGetValue(_presetName, out var existing) && !string.IsNullOrEmpty(existing)
                        ? existing + ";" + spawn
                        : spawn;
                    _spawnPresetsCfg.Value = JoinKv(presets);
                    Config.Save();
                    Message($"Preset '{_presetName}' saved");
                }
                if (GUILayout.Button("Spawn", _buttonStyle, GUILayout.Width(60)))
                { SendServerSpawn(1, e.Name, SpawnPos(), Math.Max(1, _creatureCount), _creatureLevel, false); Message($"Requested {e.Display}"); }
                if (e.Tamable && GUILayout.Button("Tame", _buttonStyle, GUILayout.Width(55)))
                { SendServerSpawn(1, e.Name, SpawnPos(), Math.Max(1, _creatureCount), _creatureLevel, true, _petName); Message($"Requested tamed {e.Display}"); }
                GUILayout.EndHorizontal();
            }
            var below = total - (first + visible);
            if (below > 0) GUILayout.Space(below * rowH);
            GUILayout.EndScrollView();
            GUILayout.Label($"{total} creatures", _dimLabelStyle);
        }

        // ==================== Bosses tab ====================
        private void DrawBossesTab()
        {
            GUILayout.Label("Bosses — spawn directly, or grab the altar offering items:", _headerStyle);
            // short fixed list — size to content so it doesn't balloon to fill a tall window (leaving a dead gap)
            _bossScroll = GUILayout.BeginScrollView(_bossScroll, GUILayout.Height(Mathf.Min(ListView(140f), BossList.Length * 28f + 8f)));
            foreach (var (prefabName, label, offerPrefab, offerCount) in BossList)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(label, _labelStyle, GUILayout.Width(130));
                GUILayout.Label(prefabName, _labelStyle, GUILayout.Width(120));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button($"Offering ({offerCount}x {offerPrefab})", _buttonStyle, GUILayout.Width(230)))
                { SendServerGive(SelfUid(), offerPrefab, offerCount, 1); Message($"Requested {offerCount}x {offerPrefab}"); }
                if (GUILayout.Button("Spawn", _buttonStyle, GUILayout.Width(70)))
                { SendServerSpawn(1, prefabName, SpawnPos(6f), 1, 1, false); Message($"Requested boss {label}"); }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Label("Raid events (started server-side at your position):", _headerStyle);
            GUILayout.BeginHorizontal();
            var col = 0;
            foreach (var ev in RaidEvents)
            {
                if (GUILayout.Button(ev, _buttonStyle))
                {
                    SrvRpc("AP_SrvEvent", ev, LocalPlayer.transform.position);
                    Message($"Event '{ev}' requested");
                }
                if (++col % 5 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();
        }

        // ==================== Player tab ====================
        private void EnsureBaseStats()
        {
            if (_baseWalk >= 0f || LocalPlayer == null) return;
            _baseWalk = LocalPlayer.m_walkSpeed;
            _baseRun = LocalPlayer.m_runSpeed;
            _baseSwim = LocalPlayer.m_swimSpeed;
            _baseJump = LocalPlayer.m_jumpForce;
            _baseWeight = LocalPlayer.m_maxCarryWeight;
            _basePickup = LocalPlayer.m_autoPickupRange;
        }

        private void DrawPlayerTab()
        {
            var player = LocalPlayer;
            EnsureBaseStats();
            _playerScroll = GUILayout.BeginScrollView(_playerScroll, GUILayout.Height(ListView(100f)));

            GUILayout.Label("Toggles:", _headerStyle);
            var god = GUILayout.Toggle(_god, " God mode (no damage)", _toggleStyle);
            if (god != _god) { _god = god; player.SetGodMode(_god); Message($"God mode {(_god ? "ON" : "OFF")}"); }

            var ghost = GUILayout.Toggle(_ghost, " Ghost mode (enemies ignore you)", _toggleStyle);
            if (ghost != _ghost) { _ghost = ghost; player.SetGhostMode(_ghost); Message($"Ghost mode {(_ghost ? "ON" : "OFF")}"); }

            var fly = GUILayout.Toggle(_fly, " Fly (debug fly)", _toggleStyle);
            if (fly != _fly)
            {
                _fly = fly;
                Player.m_debugMode = true;
                player.ToggleDebugFly();
                Message($"Fly {(_fly ? "ON — jump to fly, sneak to descend" : "OFF")}");
            }

            var noCost = GUILayout.Toggle(_noCost, " Free build (no resource cost)", _toggleStyle);
            if (noCost != _noCost) { _noCost = noCost; player.SetNoPlacementCost(_noCost); Message($"Free build {(_noCost ? "ON" : "OFF")}"); }

            var noStam = GUILayout.Toggle(_noStamina, " No stamina drain", _toggleStyle);
            if (noStam != _noStamina) { _noStamina = noStam; NoStaminaFlag = noStam; Message($"No stamina drain {(noStam ? "ON" : "OFF")}"); }

            var ohk = GUILayout.Toggle(_oneHitKill, " One-hit kill (your attacks deal massive damage)", _toggleStyle);
            if (ohk != _oneHitKill) { _oneHitKill = ohk; OneHitKillFlag = ohk; Message($"One-hit kill {(ohk ? "ON" : "OFF")}"); }

            var weight = GUILayout.Toggle(_infiniteWeight, " Infinite carry weight", _toggleStyle);
            if (weight != _infiniteWeight)
            {
                _infiniteWeight = weight;
                player.m_maxCarryWeight = weight ? 100000f : _baseWeight;
                Message($"Infinite carry weight {(weight ? "ON" : "OFF")}");
            }

            GUILayout.Space(8);
            GUILayout.Label("Multipliers:", _headerStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Speed x{_speedMult:0.0}", _labelStyle, GUILayout.Width(90));
            var newSpeed = GUILayout.HorizontalSlider(_speedMult, 1f, 10f, GUILayout.Width(250));
            if (Math.Abs(newSpeed - _speedMult) > 0.05f)
            {
                _speedMult = newSpeed;
                player.m_walkSpeed = _baseWalk * _speedMult;
                player.m_runSpeed = _baseRun * _speedMult;
                player.m_swimSpeed = _baseSwim * _speedMult;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Jump x{_jumpMult:0.0}", _labelStyle, GUILayout.Width(90));
            var newJump = GUILayout.HorizontalSlider(_jumpMult, 1f, 5f, GUILayout.Width(250));
            if (Math.Abs(newJump - _jumpMult) > 0.05f)
            {
                _jumpMult = newJump;
                player.m_jumpForce = _baseJump * _jumpMult;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Pickup {_pickupRange:0}m", _labelStyle, GUILayout.Width(90));
            var newPickup = GUILayout.HorizontalSlider(_pickupRange, 2f, 30f, GUILayout.Width(250));
            if (Math.Abs(newPickup - _pickupRange) > 0.3f)
            {
                _pickupRange = newPickup;
                player.m_autoPickupRange = _pickupRange;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Full heal", _buttonStyle)) { player.Heal(player.GetMaxHealth()); Message("Healed"); }
            if (GUILayout.Button("Full stamina", _buttonStyle)) { player.AddStamina(player.GetMaxStamina()); Message("Stamina restored"); }
            if (GUILayout.Button("Full eitr", _buttonStyle)) { player.AddEitr(player.GetMaxEitr()); Message("Eitr restored"); }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Repair all items", _buttonStyle))
            {
                foreach (var item in player.GetInventory().GetAllItems())
                    item.m_durability = item.GetMaxDurability();
                Message("All items repaired");
            }
            if (GUILayout.Button("Clear status effects", _buttonStyle))
            {
                player.GetSEMan().RemoveAllStatusEffects();
                Message("Status effects cleared");
            }
            if (GUILayout.Button("Fix broken inventory items", _buttonStyle))
            {
                var broken = player.GetInventory().GetAllItems()
                    .Where(i => i.m_shared.m_icons == null || i.m_shared.m_icons.Length == 0)
                    .ToList();
                foreach (var item in broken)
                    player.GetInventory().RemoveItem(item);
                Message($"Removed {broken.Count} broken (icon-less) items from inventory");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Skills:", _headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All skills +10", _buttonStyle)) ChangeSkills(10);
            if (GUILayout.Button("All skills 100", _buttonStyle)) SetSkills(100);
            if (GUILayout.Button("Reset skills", _buttonStyle)) SetSkills(0);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            _showStatusEffects = GUILayout.Toggle(_showStatusEffects, " Show status effect browser", _toggleStyle);
            if (_showStatusEffects && ObjectDB.instance != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Search:", _labelStyle, GUILayout.Width(50));
                _seSearch = GUILayout.TextField(_seSearch, _textFieldStyle, GUILayout.Width(200));
                GUILayout.EndHorizontal();

                // Build the categorized, search-filtered list ONCE before the scroll view. It is cached and only
                // rebuilt when the search text changes, so Layout and Repaint iterate the identical list and emit an
                // identical control count (each entry = one Horizontal row; each new bucket = one header Label).
                var seList = FilteredStatusEffects();
                _seScroll = GUILayout.BeginScrollView(_seScroll, GUILayout.Height(200));
                if (seList.Count == 0)
                {
                    GUILayout.Label("No matching status effects.", _dimLabelStyle);
                }
                else
                {
                    var lastBucket = -1;
                    for (var i = 0; i < seList.Count; i++)
                    {
                        var entry = seList[i];
                        if (entry.Bucket != lastBucket)
                        {
                            lastBucket = entry.Bucket;
                            GUILayout.Label(SeBucketNames[entry.Bucket], _headerStyle);
                        }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(entry.Display, _labelStyle, GUILayout.Width(180));
                        GUILayout.Label(entry.Tooltip, _dimLabelStyle);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Apply", _buttonStyle, GUILayout.Width(60)))
                        {
                            player.GetSEMan().AddStatusEffect(entry.Hash, true);
                            Message($"Applied {entry.Display}");
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndScrollView();
        }

        private void ChangeSkills(float delta)
        {
            foreach (Skills.SkillType type in Enum.GetValues(typeof(Skills.SkillType)))
            {
                if (type == Skills.SkillType.None || type == Skills.SkillType.All) continue;
                LocalPlayer.GetSkills().CheatRaiseSkill(type.ToString(), delta);
            }
            Message($"Skills changed by {delta}");
        }

        private void SetSkills(float value)
        {
            var skills = LocalPlayer.GetSkills();
            foreach (Skills.SkillType type in Enum.GetValues(typeof(Skills.SkillType)))
            {
                if (type == Skills.SkillType.None || type == Skills.SkillType.All) continue;
                skills.CheatResetSkill(type.ToString());
                if (value > 0) skills.CheatRaiseSkill(type.ToString(), value);
            }
            Message($"All skills set to {value}");
        }

        // ==================== World tab ====================
        private static string TimeLabel(float t)
        {
            var h = Mathf.FloorToInt(t * 24f);
            var m = Mathf.FloorToInt((t * 24f - h) * 60f);
            return $"{h:00}:{m:00}";
        }

        private void DrawWorldTab()
        {
            _worldScroll = GUILayout.BeginScrollView(_worldScroll, GUILayout.Height(ListView(100f)));

            // Capture the environment manager once. It can be momentarily null (e.g. during a world load/teardown),
            // and it drives the time/weather/wind actions below — dereferencing it raw would throw an NRE out of
            // the OnGUI callback. The controls always render (stable IMGUI control count); only the click acts.
            var env = EnvMan.instance;

            GUILayout.Label("Time of day:", _headerStyle);
            GUILayout.BeginHorizontal();
            _timeSlider = GUILayout.HorizontalSlider(_timeSlider, 0f, 1f, GUILayout.Width(280));
            GUILayout.Label(TimeLabel(_timeSlider), _labelStyle, GUILayout.Width(50));
            if (GUILayout.Button("Set", _buttonStyle, GUILayout.Width(50)) && env != null)
            {
                env.m_debugTimeOfDay = true;
                env.m_debugTime = _timeSlider;
                _timeLocked = true;
            }
            if (_timeLocked && GUILayout.Button("Release", _buttonStyle, GUILayout.Width(70)) && env != null)
            {
                env.m_debugTimeOfDay = false;
                _timeLocked = false;
            }
            if (GUILayout.Button("Skip night", _buttonStyle, GUILayout.Width(80)) && env != null)
            {
                env.m_debugTimeOfDay = true;
                env.m_debugTime = 0.3f;
                env.m_debugTimeOfDay = false;
                Message("Time pushed to morning");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Weather (empty = reset). Clear, Rain, ThunderStorm, Snow, Mist, Twilight_Clear:", _labelStyle);
            GUILayout.BeginHorizontal();
            _weather = GUILayout.TextField(_weather, _textFieldStyle, GUILayout.Width(200));
            if (GUILayout.Button("Apply", _buttonStyle, GUILayout.Width(70)) && env != null)
            {
                env.m_debugEnv = _weather;
                Message(string.IsNullOrEmpty(_weather) ? "Weather reset" : $"Weather forced: {_weather}");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Wind:", _headerStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Dir {_windAngle:0}°", _labelStyle, GUILayout.Width(70));
            _windAngle = GUILayout.HorizontalSlider(_windAngle, 0f, 360f, GUILayout.Width(160));
            GUILayout.Label($"Str {_windIntensity:0.0}", _labelStyle, GUILayout.Width(60));
            _windIntensity = GUILayout.HorizontalSlider(_windIntensity, 0f, 1f, GUILayout.Width(120));
            if (GUILayout.Button("Set", _buttonStyle, GUILayout.Width(45)) && env != null)
            { env.SetDebugWind(_windAngle, _windIntensity); _windLocked = true; Message("Wind set"); }
            if (_windLocked && GUILayout.Button("Reset", _buttonStyle, GUILayout.Width(55)) && env != null)
            { env.ResetDebugWind(); _windLocked = false; Message("Wind reset"); }
            GUILayout.EndHorizontal();

            GUILayout.Label("Teleport:", _headerStyle);
            GUILayout.Label($"🗺  Open the full map (M), hover a spot, press [{_mapTpKey.Value}] to teleport there.", _dimLabelStyle);

            // quick jump to known world locations (spawn + boss altars)
            if (ZoneSystem.instance != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Quick jump:", _labelStyle, GUILayout.Width(80));
                var any = false;
                foreach (var (label, loc) in QuickJumps)
                {
                    if (!ZoneSystem.instance.GetLocationIcon(loc, out var lp)) continue;
                    any = true;
                    if (GUILayout.Button(label, _buttonStyle)) TeleportToWorld(lp, label);
                }
                if (!any) GUILayout.Label("(no known altars yet — explore / defeat bosses)", _dimLabelStyle);
                GUILayout.EndHorizontal();
            }

            var pos = LocalPlayer.transform.position;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"You are at: {pos.x:0}, {pos.y:0}, {pos.z:0}", _labelStyle, GUILayout.Width(220));
            GUILayout.Label("X:", _labelStyle, GUILayout.Width(18));
            _tpX = GUILayout.TextField(_tpX, _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label("Y:", _labelStyle, GUILayout.Width(18));
            _tpY = GUILayout.TextField(_tpY, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label("Z:", _labelStyle, GUILayout.Width(18));
            _tpZ = GUILayout.TextField(_tpZ, _textFieldStyle, GUILayout.Width(60));
            if (GUILayout.Button("Go", _buttonStyle, GUILayout.Width(40)) &&
                float.TryParse(_tpX, out var x) && float.TryParse(_tpY, out var y) && float.TryParse(_tpZ, out var z))
            {
                LocalPlayer.TeleportTo(new Vector3(x, y <= 0 ? 200 : y, z), LocalPlayer.transform.rotation, true);
                Message("Teleporting");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Bookmark:", _labelStyle, GUILayout.Width(65));
            _bookmarkName = GUILayout.TextField(_bookmarkName, _textFieldStyle, GUILayout.Width(110));
            if (GUILayout.Button("Save here", _buttonStyle, GUILayout.Width(80)) && !string.IsNullOrEmpty(_bookmarkName))
            {
                var marks = ParseKv(_bookmarksCfg.Value);
                // Format coordinates with InvariantCulture so the decimal point is always '.', never a ','
                // that would collide with the ',' field delimiter on comma-decimal locales (de-DE/fr-FR).
                marks[_bookmarkName] = string.Format(CultureInfo.InvariantCulture, "{0:0.#},{1:0.#},{2:0.#}", pos.x, pos.y, pos.z);
                _bookmarksCfg.Value = JoinKv(marks);
                Config.Save();
                Message($"Bookmark '{_bookmarkName}' saved");
            }
            GUILayout.EndHorizontal();
            var bookmarks = ParseKv(_bookmarksCfg.Value);
            foreach (var kv in bookmarks.ToList())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Key, _labelStyle, GUILayout.Width(120));
                GUILayout.Label(kv.Value, _labelStyle, GUILayout.Width(160));
                if (GUILayout.Button("Go", _buttonStyle, GUILayout.Width(40)))
                {
                    var parts = kv.Value.Split(',');
                    if (parts.Length == 3 &&
                        float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var by) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bz))
                    {
                        LocalPlayer.TeleportTo(new Vector3(bx, by, bz), LocalPlayer.transform.rotation, true);
                        Message($"Teleporting to {kv.Key}");
                    }
                }
                if (GUILayout.Button("Del", _buttonStyle, GUILayout.Width(40)))
                {
                    bookmarks.Remove(kv.Key);
                    _bookmarksCfg.Value = JoinKv(bookmarks);
                    Config.Save();
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Label("Area actions:", _headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Kill enemies 50m", _buttonStyle)) KillNearby(50f, false);
            if (GUILayout.Button("Kill ALL loaded", _buttonStyle)) KillNearby(100000f, false);
            if (GUILayout.Button("Tame animals 30m", _buttonStyle)) TameNearby(30f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cleanup ground items 50m", _buttonStyle)) CleanupDrops(50f);
            if (GUILayout.Button("Repair builds 50m", _buttonStyle)) RepairBuilds(50f);
            if (GUILayout.Button("Clear trees 20m", _buttonStyle)) ClearTrees(20f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Explore full map", _buttonStyle)) { Minimap.instance?.ExploreAll(); Message("Map explored"); }
            if (GUILayout.Button("Ping my position", _buttonStyle)) { Chat.instance?.SendPing(LocalPlayer.transform.position); Message("Pinged"); }
            var peaceful = GUILayout.Toggle(_peaceful, " Peaceful mode (no raids)", _toggleStyle);
            if (peaceful != _peaceful)
            {
                _peaceful = peaceful;
                SrvRpc("AP_SrvPeaceful", _peaceful);
                Message($"Peaceful mode {(_peaceful ? "ON" : "OFF")} requested");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Global keys (world progression flags — control raids and boss state):", _headerStyle);
            if (ZoneSystem.instance != null)
            {
                foreach (var key in ZoneSystem.instance.GetGlobalKeys().ToList())
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(key, _labelStyle, GUILayout.Width(300));
                    if (GUILayout.Button("Remove", _buttonStyle, GUILayout.Width(70)))
                    { ZoneSystem.instance.RemoveGlobalKey(key); Message($"Removed key {key}"); }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                _newGlobalKey = GUILayout.TextField(_newGlobalKey, _textFieldStyle, GUILayout.Width(220));
                if (GUILayout.Button("Add key", _buttonStyle, GUILayout.Width(70)) && !string.IsNullOrEmpty(_newGlobalKey))
                { ZoneSystem.instance.SetGlobalKey(_newGlobalKey); Message($"Added key {_newGlobalKey}"); _newGlobalKey = ""; }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
        }

        private void KillNearby(float radius, bool includeTamed)
        {
            var pos = LocalPlayer.transform.position;
            var killed = 0;
            foreach (var ch in Character.GetAllCharacters().ToList())
            {
                if (ch == null || ch.IsPlayer()) continue;
                if (!includeTamed && ch.IsTamed()) continue;
                if (Vector3.Distance(ch.transform.position, pos) > radius) continue;
                var hit = new HitData();
                hit.m_damage.m_damage = 1e10f;
                ch.Damage(hit);
                killed++;
            }
            Message($"Killed {killed} creatures");
        }

        private void TameNearby(float radius)
        {
            var pos = LocalPlayer.transform.position;
            var tamed = 0;
            foreach (var ch in Character.GetAllCharacters())
            {
                if (ch == null || ch.IsPlayer() || ch.IsTamed()) continue;
                if (Vector3.Distance(ch.transform.position, pos) > radius) continue;
                if (ch.GetComponent<Tameable>() == null) continue;
                ch.SetTamed(true);
                tamed++;
            }
            Message($"Tamed {tamed} animals");
        }

        private void CleanupDrops(float radius)
        {
            var pos = LocalPlayer.transform.position;
            var removed = 0;
            foreach (var drop in FindObjectsOfType<ItemDrop>())
            {
                if (drop == null) continue;
                if (Vector3.Distance(drop.transform.position, pos) > radius) continue;
                var nview = drop.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                nview.ClaimOwnership();
                nview.Destroy();
                removed++;
            }
            Message($"Removed {removed} ground items");
        }

        private void RepairBuilds(float radius)
        {
            var pos = LocalPlayer.transform.position;
            var repaired = 0;
            foreach (var wnt in FindObjectsOfType<WearNTear>())
            {
                if (wnt == null) continue;
                if (Vector3.Distance(wnt.transform.position, pos) > radius) continue;
                if (wnt.Repair()) repaired++;
            }
            Message($"Repaired {repaired} build pieces");
        }

        private void ClearTrees(float radius)
        {
            var pos = LocalPlayer.transform.position;
            var removed = 0;
            foreach (var tree in FindObjectsOfType<TreeBase>())
            {
                if (tree == null || Vector3.Distance(tree.transform.position, pos) > radius) continue;
                var nview = tree.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                nview.ClaimOwnership();
                nview.Destroy();
                removed++;
            }
            foreach (var log in FindObjectsOfType<TreeLog>())
            {
                if (log == null || Vector3.Distance(log.transform.position, pos) > radius) continue;
                var nview = log.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                nview.ClaimOwnership();
                nview.Destroy();
                removed++;
            }
            Message($"Removed {removed} trees/logs");
        }

        // ==================== Players tab ====================
        private void DrawPlayersTab()
        {
            if (ZNet.instance == null) { GUILayout.Label("Not connected.", _labelStyle); return; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Broadcast:", _labelStyle, GUILayout.Width(70));
            _broadcastText = GUILayout.TextField(_broadcastText, _textFieldStyle);
            if (GUILayout.Button("Send to all", _buttonStyle, GUILayout.Width(90)) && !string.IsNullOrEmpty(_broadcastText))
            {
                SrvRpc("AP_SrvBroadcast", _broadcastText);
                Message("Broadcast sent");
                _broadcastText = "";
            }
            if (GUILayout.Button("Summon ALL", _buttonStyle, GUILayout.Width(95)))
            {
                foreach (var p in OtherPlayers()) SummonPlayer(p);
                Message("Summoning everyone");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Connected players:", _headerStyle);
            _playersScroll = GUILayout.BeginScrollView(_playersScroll, GUILayout.Height(Mathf.Min(250f, ListView(330f))));
            foreach (var info in ZNet.instance.GetPlayerList())
            {
                var isSelf = LocalPlayer != null && info.m_name == LocalPlayer.GetPlayerName();
                GUILayout.BeginHorizontal();
                GUILayout.Label(info.m_name + (isSelf ? " (you)" : ""), _labelStyle, GUILayout.Width(140));
                GUILayout.Label($"({info.m_position.x:0}, {info.m_position.z:0})", _labelStyle, GUILayout.Width(100));
                if (!isSelf)
                {
                    if (GUILayout.Button("TP to", _buttonStyle, GUILayout.Width(50)))
                    { LocalPlayer.TeleportTo(info.m_position + Vector3.up, LocalPlayer.transform.rotation, true); Message($"Teleporting to {info.m_name}"); }
                    if (GUILayout.Button("Summon", _buttonStyle, GUILayout.Width(65))) SummonPlayer(info);
                    if (GUILayout.Button("Watch", _buttonStyle, GUILayout.Width(55)))
                    {
                        if (!_ghost) { _ghost = true; LocalPlayer.SetGhostMode(true); }
                        if (!_fly) { _fly = true; Player.m_debugMode = true; LocalPlayer.ToggleDebugFly(); }
                        LocalPlayer.TeleportTo(info.m_position + Vector3.up * 8f, LocalPlayer.transform.rotation, true);
                        Message($"Watching {info.m_name} (ghost+fly enabled)");
                    }
                    if (GUILayout.Button("Heal", _buttonStyle, GUILayout.Width(45)))
                    { SrvRpc("AP_SrvHeal", PeerIdOf(info)); Message($"Healing {info.m_name}"); }
                    if (GUILayout.Button("Map", _buttonStyle, GUILayout.Width(45)))
                    { Chat.instance?.SendPing(info.m_position); Message($"Pinged {info.m_name}'s position"); }
                    if (GUILayout.Button("⚡", _buttonStyle, GUILayout.Width(30)))
                    { SendServerSpawn(1, "lightning", info.m_position, 1, 1, false); Message($"Lightning on {info.m_name}!"); }
                    if (GUILayout.Button("Inventory", _buttonStyle, GUILayout.Width(75)))
                    {
                        _inspectPlayerName = info.m_name;
                        _inspectInventory = null;
                        _inspectPending = true;
                        _inspectRequestTime = Time.time;
                        SrvRpc("AP_SrvReqInv", PeerIdOf(info));
                    }
                    if (GUILayout.Button("Kick", _buttonStyle, GUILayout.Width(45)))
                    { SrvRpc("AP_SrvKick", PeerIdOf(info)); Message($"Kicked {info.m_name}"); }
                    if (GUILayout.Button("Ban", _buttonStyle, GUILayout.Width(42)))
                    { SrvRpc("AP_SrvBan", PeerIdOf(info)); Message($"Banned {info.m_name}"); }
                }
                else if (GUILayout.Button("Inventory", _buttonStyle, GUILayout.Width(75)))
                {
                    _inspectPlayerName = info.m_name;
                    _inspectInventory = null;
                    _inspectPending = true;
                    _inspectRequestTime = Time.time;
                    SrvRpc("AP_SrvReqInv", PeerIdOf(info));
                }
                GUILayout.EndHorizontal();

                // note line
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUILayout.Label("Note:", _labelStyle, GUILayout.Width(40));
                _playerNotes.TryGetValue(info.m_name, out var note);
                var newNote = GUILayout.TextField(note ?? "", _textFieldStyle, GUILayout.Width(300));
                if (newNote != (note ?? ""))
                {
                    _playerNotes[info.m_name] = newNote;
                    _notesDirty = true;   // flushed once on panel close (FlushNotes) — not a full disk write per keystroke
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Label($"Inventory viewer: {_inspectPlayerName ?? "(pick a player above)"}", _headerStyle);
            if (_inspectPending)
            {
                GUILayout.Label(Time.time - _inspectRequestTime > 5f
                    ? "No response — that player probably doesn't have the companion mod installed."
                    : "Waiting for response...", _labelStyle);
            }
            else if (_inspectInventory != null)
            {
                _inspectScroll = GUILayout.BeginScrollView(_inspectScroll, GUILayout.Height(140));
                foreach (var (name, stack, quality) in _inspectInventory)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(name, _labelStyle, GUILayout.Width(250));
                    GUILayout.Label($"x{stack}", _labelStyle, GUILayout.Width(60));
                    GUILayout.Label($"q{quality}", _labelStyle);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
        }

        private void SummonPlayer(ZNet.PlayerInfo info)
        {
            var pkg = new ZPackage();
            pkg.Write(PeerIdOf(info));
            pkg.Write(LocalPlayer.transform.position + LocalPlayer.transform.forward * 2f);
            SrvRpc("AP_SrvTeleport", pkg);
            Message($"Summoning {info.m_name}");
        }

        // ==================== Server tab ====================
        private void DrawServerTab()
        {
            _serverScroll = GUILayout.BeginScrollView(_serverScroll, GUILayout.Height(ListView(100f)));

            GUILayout.Label("Live stats:", _headerStyle);
            var day = EnvMan.instance != null && ZNet.instance != null
                ? EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds()) : 0;
            var players = ZNet.instance != null ? ZNet.instance.GetPlayerList().Count : 0;
            var chars = Character.GetAllCharacters().Count;
            var fps = Mathf.RoundToInt(1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
            GUILayout.Label($"World day: {day}     Players online: {players}     Loaded creatures: {chars}     Your FPS: {fps}", _labelStyle);
            var serverPeer = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            if (serverPeer != null && serverPeer.m_socket != null)
                GUILayout.Label($"Server: {serverPeer.m_socket.GetHostName()}", _labelStyle);

            GUILayout.Space(8);
            GUILayout.Label("Unban a player (Steam ID):", _headerStyle);
            GUILayout.BeginHorizontal();
            _unbanId = GUILayout.TextField(_unbanId, _textFieldStyle, GUILayout.Width(220));
            if (GUILayout.Button("Unban", _buttonStyle, GUILayout.Width(70)) && !string.IsNullOrEmpty(_unbanId))
            {
                SrvRpc("AP_SrvUnban", _unbanId);
                Message($"Unban requested for {_unbanId}");
                _unbanId = "";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Join/leave history (this session):", _headerStyle);
            if (_joinLog.Count == 0) GUILayout.Label("Nothing yet.", _labelStyle);
            foreach (var line in _joinLog.Take(40))
                GUILayout.Label(line, _labelStyle);

            GUILayout.EndScrollView();
        }

        private string _unbanId = "";

        // ==================== Settings tab ====================
        private static Color WoodColor(int alphaPct) =>
            new Color(0.118f, 0.082f, 0.055f, Mathf.Clamp(alphaPct, 55, 100) / 100f);

        // All sizes derive from one base so the hierarchy (headers/title slightly larger, chips/dim smaller)
        // survives any base the user picks. Mutating fontSize on the live styles is safe mid-frame: it changes
        // no control counts, so IMGUI's Layout/Repaint passes stay consistent. At the default base (13) every
        // value below equals the pre-settings hardcoded sizes, so a fresh install renders pixel-identical.
        private void ApplyFontSizes()
        {
            var s = Mathf.Clamp(_fontSizeLive, 10, 20);
            _labelStyle.fontSize = s;
            _buttonStyle.fontSize = s;
            _toggleStyle.fontSize = s;
            // 0 = "use the font's own default size", which is exactly how text fields rendered before this
            // setting existed — keep that at the default base so nothing shifts for existing users.
            _textFieldStyle.fontSize = s == 13 ? 0 : s;
            _headerStyle.fontSize = s + 1;
            _windowStyle.fontSize = Mathf.Min(s + 2, 18);
            // The tab bar and category chips must stay one row wide even at the largest base sizes
            // (the row can't shrink below its text and would push the last tabs off-window), so cap them.
            _tabStyle.fontSize = Mathf.Clamp(s + 1, 12, 15);
            _catStyle.fontSize = Mathf.Clamp(s - 1, 10, 14);
            _dimLabelStyle.fontSize = Mathf.Max(s - 1, 10);
        }

        // Recolor the window-background texture in place — no new textures and no style rebuild, so the
        // opacity slider can preview live without leaking a Texture2D per tick.
        private void ApplyPanelAlpha()
        {
            if (_texWood == null) return;
            _texWood.SetPixel(0, 0, WoodColor(_panelAlphaLive));
            _texWood.Apply();
        }

        // Sliders preview by mutating the live styles/texture every tick; the config file is only written
        // here — on mouse-release and panel close (same anti-thrash pattern as SaveWindowRect).
        private void CommitUiSettings()
        {
            if (_fontSizeCfg == null || _panelAlphaCfg == null) return;
            var dirty = false;
            if (_fontSizeCfg.Value != _fontSizeLive) { _fontSizeCfg.Value = _fontSizeLive; dirty = true; }
            if (_panelAlphaCfg.Value != _panelAlphaLive) { _panelAlphaCfg.Value = _panelAlphaLive; dirty = true; }
            if (dirty) Config.Save();
        }

        private void SelectFont(string choice)
        {
            if (_fontChoiceCfg.Value == choice) return;
            _fontChoiceCfg.Value = choice;   // one write per click — saved immediately
            _fontApplied = false;            // make ApplyFont re-resolve on the next OnGUI pass
            _fontTries = 0;
            _nextFontTry = 0f;
        }

        private void DrawSettingsTab()
        {
            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll, GUILayout.Height(ListView(100f)));

            GUILayout.Label("Appearance:", _headerStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Font:", _labelStyle, GUILayout.Width(90));
            foreach (var choice in FontChoices)
            {
                var on = _fontChoiceCfg.Value == choice;
                if (GUILayout.Toggle(on, choice, _catStyle) && !on) SelectFont(choice);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("'Norse (auto)' picks the best available game font. A font the game hasn't loaded falls back to Default.", _dimLabelStyle);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Font size: {_fontSizeLive}", _labelStyle, GUILayout.Width(120));
            var newSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(_fontSizeLive, 10f, 20f, GUILayout.Width(220)));
            if (GUILayout.Button("−", _buttonStyle, GUILayout.Width(30))) newSize = Mathf.Max(10, _fontSizeLive - 1);
            if (GUILayout.Button("+", _buttonStyle, GUILayout.Width(30))) newSize = Mathf.Min(20, _fontSizeLive + 1);
            GUILayout.EndHorizontal();
            if (newSize != _fontSizeLive)
            {
                _fontSizeLive = newSize;
                ApplyFontSizes();   // live preview; committed to disk on mouse-release (CommitUiSettings)
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Opacity: {_panelAlphaLive}%", _labelStyle, GUILayout.Width(120));
            var newAlpha = Mathf.RoundToInt(GUILayout.HorizontalSlider(_panelAlphaLive, 55f, 100f, GUILayout.Width(220)));
            GUILayout.EndHorizontal();
            if (newAlpha != _panelAlphaLive)
            {
                _panelAlphaLive = newAlpha;
                ApplyPanelAlpha();
            }

            GUILayout.Space(10);
            GUILayout.Label("Behavior:", _headerStyle);
            var cam = GUILayout.Toggle(_cameraLockCfg.Value, " Lock camera while the panel is open (like the inventory)", _toggleStyle);
            if (cam != _cameraLockCfg.Value) { _cameraLockCfg.Value = cam; Config.Save(); }

            GUILayout.Space(10);
            GUILayout.Label("Hotkeys:", _headerStyle);
            DrawRebindRow("Open / close panel", _toggleKey, 1);
            DrawRebindRow("Map teleport", _mapTpKey, 2);
            // Emit the "listening" hint only when the Layout pass saw the rebind active (same control-count
            // rule as _openDropdownLayout: _rebindTarget flips mid-frame on the click pass).
            if (_rebindTargetLayout != 0)
                GUILayout.Label("Press the new key…   (Esc cancels)", _headerStyle);
            if (_rebindTarget != 0)
            {
                var ev = Event.current;
                if (ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None)
                {
                    if (ev.keyCode != KeyCode.Escape)
                    {
                        var target = _rebindTarget == 1 ? _toggleKey : _mapTpKey;
                        target.Value = ev.keyCode;
                        Config.Save();
                    }
                    _rebindTarget = 0;
                    ev.Use();
                }
            }

            GUILayout.Space(10);
            GUILayout.Label("Reset:", _headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset window size & position", _buttonStyle, GUILayout.Width(230)))
            {
                _windowRect = new Rect(60, 60, 740, 680);
                SaveWindowRect();
            }
            if (GUILayout.Button("Reset appearance", _buttonStyle, GUILayout.Width(160)))
            {
                _fontSizeLive = 13;
                _panelAlphaLive = 96;
                ApplyFontSizes();
                ApplyPanelAlpha();
                CommitUiSettings();
                SelectFont(FontChoices[0]);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Settings are saved to the BepInEx config file and survive restarts.", _dimLabelStyle);

            GUILayout.EndScrollView();
        }

        private void DrawRebindRow(string label, ConfigEntry<KeyCode> entry, int target)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}:", _labelStyle, GUILayout.Width(170));
            GUILayout.Label($"[{entry.Value}]", _headerStyle, GUILayout.Width(100));
            var listening = _rebindTarget == target;
            if (GUILayout.Button(listening ? "Listening…" : "Rebind", _buttonStyle, GUILayout.Width(110)))
                _rebindTarget = listening ? 0 : target;
            GUILayout.EndHorizontal();
        }
    }
}
