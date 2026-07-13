using System;
using System.Collections.Generic;
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
        public const string PluginVersion = "2.2.0";

        internal static AdminPanelPlugin Instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _mapTpKey;

        private bool _visible;
        private Rect _windowRect = new Rect(60, 60, 740, 680);
        private int _tab;
        private static readonly string[] TabNames = { "Items", "Creatures", "Bosses", "Player", "World", "Players", "Server" };

        // ==================== Items tab state ====================
        private string _itemSearch = "";
        private Vector2 _itemScroll;
        private int _itemAmount = 1;
        private int _itemQuality = 1;
        private int _giveTargetIndex = -1;

        private class ItemEntry
        {
            public ItemDrop Drop;
            public string Prefab;
            public string Display;
            public string Cat;
            public string Sub;
            public Sprite Icon;
            public bool IconTried;
        }

        // cached filter results — recomputed only when filters change (fixes per-frame lag)
        private List<ItemEntry> _filteredItemsCache;
        private string _itemFilterKey = "";
        private int _favVersion;
        private List<CreatureEntry> _filteredCreaturesCache;
        private string _creatureFilterKey = "";

        private List<ItemEntry> _itemIndex;
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
        private bool _skinReady;

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
            _favorites = new HashSet<string>(
                _favoritesCfg.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            _playerNotes = ParseKv(_playerNotesCfg.Value);
            Harmony.CreateAndPatchAll(typeof(RpcRegistration));
            Harmony.CreateAndPatchAll(typeof(CheatPatches));
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Press {_toggleKey.Value} in-game.");
        }

        private static Dictionary<string, string> ParseKv(string raw)
        {
            var dict = new Dictionary<string, string>();
            foreach (var pair in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx > 0) dict[pair.Substring(0, idx)] = pair.Substring(idx + 1);
            }
            return dict;
        }

        private static string JoinKv(Dictionary<string, string> dict) =>
            string.Join("|", dict.Select(kv => $"{kv.Key}={kv.Value}"));

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
            var playerName = pkg.ReadString();
            var count = pkg.ReadInt();
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
        private bool _eventSystemDisabled;

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value))
            {
                _visible = !_visible;
                if (_visible) RefreshCaches();
            }

            // map-point teleport: full map open + hover a spot + press the map-teleport key
            if (Input.GetKeyDown(_mapTpKey.Value) && LocalPlayer != null &&
                Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)
            {
                TeleportToMapCursor();
            }

            // block clicks from passing through the panel to the game's UI behind it
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
            {
                var mouse = Input.mousePosition;
                var guiPoint = new Vector2(mouse.x, Screen.height - mouse.y);
                var overPanel = _visible && _windowRect.Contains(guiPoint);
                if (overPanel && !_eventSystemDisabled) { es.enabled = false; _eventSystemDisabled = true; }
                else if (!overPanel && _eventSystemDisabled) { es.enabled = true; _eventSystemDisabled = false; }
            }

            // re-apply persistent buffs when the local Player instance changes (death/respawn/teleport)
            var lp = Player.m_localPlayer;
            if (lp != null && lp != _appliedTo)
            {
                ReapplyPlayerState(lp);
                _appliedTo = lp;
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

        private void RefreshCaches()
        {
            _itemIndex = null;
            _creatureIndex = null;
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
                            Cat = cat,
                            Sub = sub
                        };
                    })
                    .OrderBy(e => e.Cat).ThenBy(e => e.Sub).ThenBy(e => e.Display)
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
            var wood = SolidTex(new Color(0.118f, 0.082f, 0.055f, 0.96f));
            var woodLight = SolidTex(new Color(0.220f, 0.160f, 0.100f, 1f));
            var woodHover = SolidTex(new Color(0.310f, 0.230f, 0.130f, 1f));
            var woodActive = SolidTex(new Color(0.160f, 0.115f, 0.070f, 1f));
            var fieldBg = SolidTex(new Color(0.070f, 0.050f, 0.035f, 1f));
            var parchment = new Color(0.870f, 0.790f, 0.620f);
            var gold = new Color(0.980f, 0.780f, 0.350f);

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = wood;
            _windowStyle.onNormal.background = wood;
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

            _skinReady = true;
        }

        // ==================== GUI root ====================
        private void OnGUI()
        {
            if (!_visible) return;
            EnsureSkin();
            _windowRect = GUILayout.Window(918273, _windowRect, DrawWindow,
                $"⚔ Valheim Admin Panel ⚔   [{_toggleKey.Value} to close]", _windowStyle);
        }

        private void DrawWindow(int id)
        {
            if (LocalPlayer == null)
            {
                GUILayout.Label("Not in game (no local player).", _labelStyle);
                GUI.DragWindow();
                return;
            }

            GUILayout.BeginHorizontal();
            for (var i = 0; i < TabNames.Length; i++)
            {
                var pressed = GUILayout.Toggle(_tab == i, TabNames[i], _tabStyle);
                if (pressed && _tab != i) _tab = i;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(14);

            switch (_tab)
            {
                case 0: DrawItemsTab(); break;
                case 1: DrawCreaturesTab(); break;
                case 2: DrawBossesTab(); break;
                case 3: DrawPlayerTab(); break;
                case 4: DrawWorldTab(); break;
                case 5: DrawPlayersTab(); break;
                case 6: DrawServerTab(); break;
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

        private string GiveTargetName(List<ZNet.PlayerInfo> others)
        {
            if (_giveTargetIndex < 0 || _giveTargetIndex >= others.Count) return "(nobody)";
            return others[_giveTargetIndex].m_name;
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
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvSpawn", pkg);
        }

        private void SendServerGive(long targetUid, string prefabName, int amount, int quality)
        {
            var pkg = new ZPackage();
            pkg.Write(targetUid);
            pkg.Write(prefabName);
            pkg.Write(amount);
            pkg.Write(quality);
            pkg.Write(_crafterNameCfg.Value ?? "");
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvGive", pkg);
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
        }

        private List<ItemEntry> FilteredItems()
        {
            var key = $"{_mainCat}|{_subCat}|{_itemSearch}|{_favVersion}|{_recentItems.Count}";
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

            var others = OtherPlayers();

            if (_mainCat == "Kits")
            {
                GUILayout.Label("Gear kits (delivered to inventory via server):", _headerStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Give target:", _labelStyle, GUILayout.Width(80));
                if (GUILayout.Button(GiveTargetName(others), _buttonStyle, GUILayout.Width(180)))
                    _giveTargetIndex = others.Count == 0 ? -1 : (_giveTargetIndex + 1) % others.Count;
                GUILayout.EndHorizontal();
                _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(380));
                foreach (var kit in GearKits)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(kit.Name, _labelStyle, GUILayout.Width(140));
                    GUILayout.Label(string.Join(", ", kit.Items.Select(i => i.Count > 1 ? $"{i.Prefab} x{i.Count}" : i.Prefab)), _labelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("To me", _buttonStyle, GUILayout.Width(70)))
                        GiveKit(kit, SelfUid());
                    if (GUILayout.Button("Give", _buttonStyle, GUILayout.Width(55)) &&
                        _giveTargetIndex >= 0 && _giveTargetIndex < others.Count)
                        GiveKit(kit, PeerIdOf(others[_giveTargetIndex]));
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
                var subs = _itemIndex.Where(e => e.Cat == _mainCat).Select(e => e.Sub).Distinct().OrderBy(s => s).ToList();
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
                _giveTargetIndex = others.Count == 0 ? -1 : (_giveTargetIndex + 1) % others.Count;
            GUILayout.Label("Drop = ground | Bag = your bag | Give = target's bag", _labelStyle);
            GUILayout.EndHorizontal();

            if (_itemIndex == null) { GUILayout.Label("Item DB not loaded.", _labelStyle); return; }

            GUILayout.Space(10);

            // virtualized list: only rows inside the viewport are rendered
            const float rowH = 32f;
            const float viewH = 350f;
            var filtered = FilteredItems();
            var total = filtered.Count;
            var first = Mathf.Max(0, Mathf.FloorToInt(_itemScroll.y / rowH) - 1);
            var visible = Mathf.Min(total - first, Mathf.CeilToInt(viewH / rowH) + 3);

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
                GUILayout.Label(e.Prefab, _dimLabelStyle);
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
                    if (!bagSafe) Message($"{e.Display} has no icon — cannot be given");
                    else if (_giveTargetIndex >= 0 && _giveTargetIndex < others.Count)
                    { SendServerGive(PeerIdOf(others[_giveTargetIndex]), e.Prefab, amount, quality); MarkRecent(e); Message($"Sent {amount}x {e.Display} to {others[_giveTargetIndex].m_name}"); }
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
            var key = $"{_creatureCat}|{_creatureSearch}";
            if (_filteredCreaturesCache != null && key == _creatureFilterKey) return _filteredCreaturesCache;

            IEnumerable<CreatureEntry> src = _creatureIndex;
            if (_creatureCat == "Bosses") src = src.Where(e => e.Boss);
            else if (_creatureCat == "Tamable") src = src.Where(e => e.Tamable);
            else if (_creatureCat != "All") src = src.Where(e => e.Faction == _creatureCat);
            if (!string.IsNullOrEmpty(_creatureSearch))
                src = src.Where(e =>
                    e.Display.IndexOf(_creatureSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Name.IndexOf(_creatureSearch, StringComparison.OrdinalIgnoreCase) >= 0);

            _filteredCreaturesCache = src.ToList();
            _creatureFilterKey = key;
            return _filteredCreaturesCache;
        }

        private void DrawCreaturesTab()
        {
            if (_creatureIndex == null) { GUILayout.Label("Scene DB not loaded.", _labelStyle); return; }

            var cats = new List<string> { "All", "Bosses", "Tamable" };
            cats.AddRange(_creatureIndex.Select(e => e.Faction).Distinct().OrderBy(f => f));
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
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Pet name:", _labelStyle, GUILayout.Width(60));
            _petName = GUILayout.TextField(_petName, _textFieldStyle, GUILayout.Width(120));
            if (GUILayout.Button("Undo last spawn", _buttonStyle, GUILayout.Width(120)))
            { ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvUndo"); Message("Undo requested"); }
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
            foreach (var preset in _spawnPresetsCfg.Value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = preset.Split('=');
                if (bits.Length != 2) continue;
                if (GUILayout.Button(bits[0], _buttonStyle))
                {
                    foreach (var spawn in bits[1].Split(';'))
                    {
                        var s = spawn.Split(':');
                        if (s.Length >= 3 && int.TryParse(s[1], out var n) && int.TryParse(s[2], out var lvl))
                            SendServerSpawn(1, s[0], SpawnPos(4f), n, lvl, false);
                    }
                    Message($"Preset '{bits[0]}' spawned");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            const float rowH = 32f;
            const float viewH = 320f;
            var filtered = FilteredCreatures();
            var total = filtered.Count;
            var first = Mathf.Max(0, Mathf.FloorToInt(_creatureScroll.y / rowH) - 1);
            var visible = Mathf.Min(total - first, Mathf.CeilToInt(viewH / rowH) + 3);

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
                    var entry = $"{_presetName}={e.Name}:{Math.Max(1, _creatureCount)}:{_creatureLevel}";
                    _spawnPresetsCfg.Value = string.IsNullOrEmpty(_spawnPresetsCfg.Value) ? entry : _spawnPresetsCfg.Value + "|" + entry;
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
            _bossScroll = GUILayout.BeginScrollView(_bossScroll, GUILayout.Height(380));
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
                    ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvEvent", ev, LocalPlayer.transform.position);
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
            _playerScroll = GUILayout.BeginScrollView(_playerScroll, GUILayout.Height(470));

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
                _seScroll = GUILayout.BeginScrollView(_seScroll, GUILayout.Height(160));
                foreach (var se in ObjectDB.instance.m_StatusEffects)
                {
                    if (se == null) continue;
                    if (!string.IsNullOrEmpty(_seSearch) &&
                        se.name.IndexOf(_seSearch, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(se.name, _labelStyle, GUILayout.Width(260));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Apply", _buttonStyle, GUILayout.Width(60)))
                    {
                        player.GetSEMan().AddStatusEffect(se.NameHash(), true);
                        Message($"Applied {se.name}");
                    }
                    GUILayout.EndHorizontal();
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
            _worldScroll = GUILayout.BeginScrollView(_worldScroll, GUILayout.Height(470));

            GUILayout.Label("Time of day:", _headerStyle);
            GUILayout.BeginHorizontal();
            _timeSlider = GUILayout.HorizontalSlider(_timeSlider, 0f, 1f, GUILayout.Width(280));
            GUILayout.Label(TimeLabel(_timeSlider), _labelStyle, GUILayout.Width(50));
            if (GUILayout.Button("Set", _buttonStyle, GUILayout.Width(50)))
            {
                EnvMan.instance.m_debugTimeOfDay = true;
                EnvMan.instance.m_debugTime = _timeSlider;
                _timeLocked = true;
            }
            if (_timeLocked && GUILayout.Button("Release", _buttonStyle, GUILayout.Width(70)))
            {
                EnvMan.instance.m_debugTimeOfDay = false;
                _timeLocked = false;
            }
            if (GUILayout.Button("Skip night", _buttonStyle, GUILayout.Width(80)))
            {
                EnvMan.instance.m_debugTimeOfDay = true;
                EnvMan.instance.m_debugTime = 0.3f;
                EnvMan.instance.m_debugTimeOfDay = false;
                Message("Time pushed to morning");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Weather (empty = reset). Clear, Rain, ThunderStorm, Snow, Mist, Twilight_Clear:", _labelStyle);
            GUILayout.BeginHorizontal();
            _weather = GUILayout.TextField(_weather, _textFieldStyle, GUILayout.Width(200));
            if (GUILayout.Button("Apply", _buttonStyle, GUILayout.Width(70)))
            {
                EnvMan.instance.m_debugEnv = _weather;
                Message(string.IsNullOrEmpty(_weather) ? "Weather reset" : $"Weather forced: {_weather}");
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Wind:", _headerStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Dir {_windAngle:0}°", _labelStyle, GUILayout.Width(70));
            _windAngle = GUILayout.HorizontalSlider(_windAngle, 0f, 360f, GUILayout.Width(160));
            GUILayout.Label($"Str {_windIntensity:0.0}", _labelStyle, GUILayout.Width(60));
            _windIntensity = GUILayout.HorizontalSlider(_windIntensity, 0f, 1f, GUILayout.Width(120));
            if (GUILayout.Button("Set", _buttonStyle, GUILayout.Width(45)))
            { EnvMan.instance.SetDebugWind(_windAngle, _windIntensity); _windLocked = true; Message("Wind set"); }
            if (_windLocked && GUILayout.Button("Reset", _buttonStyle, GUILayout.Width(55)))
            { EnvMan.instance.ResetDebugWind(); _windLocked = false; Message("Wind reset"); }
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
                marks[_bookmarkName] = $"{pos.x:0.#},{pos.y:0.#},{pos.z:0.#}";
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
                    if (parts.Length == 3 && float.TryParse(parts[0], out var bx) &&
                        float.TryParse(parts[1], out var by) && float.TryParse(parts[2], out var bz))
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
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvPeaceful", _peaceful);
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
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvBroadcast", _broadcastText);
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
            _playersScroll = GUILayout.BeginScrollView(_playersScroll, GUILayout.Height(250));
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
                    { ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvHeal", PeerIdOf(info)); Message($"Healing {info.m_name}"); }
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
                        ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvReqInv", PeerIdOf(info));
                    }
                    if (GUILayout.Button("Kick", _buttonStyle, GUILayout.Width(45)))
                    { ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvKick", PeerIdOf(info)); Message($"Kicked {info.m_name}"); }
                    if (GUILayout.Button("Ban", _buttonStyle, GUILayout.Width(42)))
                    { ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvBan", PeerIdOf(info)); Message($"Banned {info.m_name}"); }
                }
                else if (GUILayout.Button("Inventory", _buttonStyle, GUILayout.Width(75)))
                {
                    _inspectPlayerName = info.m_name;
                    _inspectInventory = null;
                    _inspectPending = true;
                    _inspectRequestTime = Time.time;
                    ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvReqInv", PeerIdOf(info));
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
                    _playerNotesCfg.Value = JoinKv(_playerNotes);
                    Config.Save();
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
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvTeleport", pkg);
            Message($"Summoning {info.m_name}");
        }

        // ==================== Server tab ====================
        private void DrawServerTab()
        {
            _serverScroll = GUILayout.BeginScrollView(_serverScroll, GUILayout.Height(470));

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
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), "AP_SrvUnban", _unbanId);
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
    }
}
