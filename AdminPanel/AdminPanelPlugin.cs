using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        public const string PluginVersion = "2.3.0";

        internal static AdminPanelPlugin Instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _mapTpKey;

        // ==================== Settings tab state ====================
        private ConfigEntry<string> _fontChoiceCfg;
        private ConfigEntry<int> _fontSizeCfg;
        private ConfigEntry<string> _languageCfg;
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
        // Localization keys rather than literals: tab labels are resolved through Loc at draw time so a
        // language change takes effect without rebuilding this array. Order must match the switch in DrawWindow.
        private static readonly string[] TabKeys =
        { "tab.items", "tab.creatures", "tab.bosses", "tab.player", "tab.world", "tab.players", "tab.server", "tab.settings" };

        // ==================== Items tab state ====================
        private string _itemSearch = "";
        private Vector2 _itemScroll;
        private int _itemAmount = 1;
        private int _itemQuality = 1;
        private long _giveTargetId;   // stable id of the selected give target (0 = nobody); survives roster changes
        private List<ZNet.PlayerInfo> _othersSnapshot;   // OtherPlayers() built once per frame (Layout) in DrawWindow, shared by all tabs
        private List<string> _globalKeysLayout;          // ZoneSystem global keys, snapshotted per frame (World tab draws a row each)
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
            public int Bucket;         // index into SeBucketKeys
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
        private bool _showSkills;
        private Vector2 _skillScroll;
        private string _skillCustom = "25";
        private long _skillTargetId;   // 0 = apply skills to yourself; else a peer id (needs companion 2.2.0 on target)
        private long _seTargetId;      // 0 = apply status effects to yourself; else a peer id (needs companion 2.3.0)
        private string _skillMsg = ""; // optional private note shown ONLY to the skill target (empty = silent)
        // cached once — enumerating an enum allocates, and this list renders every OnGUI pass
        private static readonly Skills.SkillType[] AllSkillTypes = Enum.GetValues(typeof(Skills.SkillType))
            .Cast<Skills.SkillType>().Where(t => t != Skills.SkillType.None && t != Skills.SkillType.All).ToArray();

        // ==================== Players tab state ====================
        private Vector2 _playersScroll;
        private string _broadcastText = "";
        private ConfigEntry<string> _playerNotesCfg;
        private ConfigEntry<string> _windowRectCfg;
        private bool _resizing;
        private bool _notesDirty;   // per-player notes edited in memory but not yet flushed to disk
        private Dictionary<string, string> _playerNotes;
        private string _inspectPlayerName;
        private long _inspectTargetId;   // peer id of the inspected player — target for Remove actions
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

        // ==================== Side window (What's New / Bug Report) ====================
        private enum SideMode { None, WhatsNew, BugReport }
        private SideMode _sideMode = SideMode.None;
        private SideMode _sideModeLayout;             // Layout-pass snapshot (same control-count rule as _openDropdownLayout)
        private Vector2 _sideScroll;
        private ConfigEntry<bool> _autoWhatsNewCfg;   // auto-open What's New once per new version
        private ConfigEntry<string> _seenVersionCfg;  // last version whose What's New was shown
        private ConfigEntry<string> _bugWebhookCfg;   // Discord webhook receiving in-panel bug reports ("" = sending disabled)
        private ConfigEntry<string> _lastDeathCfg;    // "x,y,z" of the local admin's last death ("" = none yet)
        private string _bugText = "";
        private bool _bugAttachShot = true;
        private volatile string _bugStatus;           // result line from the async sender (worker thread writes it)
        private string _bugStatusLayout;              // Layout-pass snapshot — the status label must not (dis)appear mid-frame
        private bool _bugCoolLayout;                  // Layout-pass snapshot of the cooldown state (same reason)
        private bool _bugSending;
        private float _nextBugSend;                   // client-side cooldown so the webhook can't be spammed
        private const string DiscordInvite = "https://discord.gg/2RVn78hNrz";
        // Opened with Application.OpenURL, which hands off to the player's default browser rather than any
        // in-game overlay — the panel never renders or intercepts the payment page itself.
        private const string DonateUrl =
            "https://www.paypal.com/donate/?business=HRKGKS3G5JD94&no_recurring=0&item_name=Your+Friendly+Neighborhood+Dev%2C+Halit.%0AMay+the+Lord+bless+you+with+even+more%21&currency_code=USD";

        // ==================== Update check ====================
        private volatile string _updateAvailable;   // newer version string once the check finds one (null = none/unknown)
        private string _updateBannerLayout;         // Layout-pass snapshot — the banner must not appear mid-frame

        // ==================== Companion version handshake ====================
        private volatile string _srvCompVersion;    // server companion's version reply (null = none yet)
        private string _versionWarnLayout;          // Layout-pass snapshot of the warning line (null = no warning)
        private float _nextVersionReq;              // ≥30s between requests — a missing/old companion can NEVER cause a request loop
        private float _versionReqFirst;             // when the first request went out (drives the no-reply timeout)
        private const string ReleasesApi = "https://api.github.com/repos/hldblc/ValheimAdminPanel/releases/latest";
        private const string ReleasesPage = "https://github.com/hldblc/ValheimAdminPanel/releases/latest";
        // Baked-in so reports work out of the box; server owners can point BugReport.WebhookUrl elsewhere.
        // Targets the 🐞-bug-reports FORUM channel, so every payload must carry a thread_name (see PostToWebhook).
        private const string DefaultBugWebhook =
            "https://discord.com/api/webhooks/1527589135672672266/vqCLH1pZV8mRtKauUCcRxExWGwTazxkSJ6gz75Ey6yQoSfriVnuc4b07e3DYNvCaUPK3";

        // Deliberately English-only: this is release-note content, not UI chrome, and it changes every
        // release — translating it would leave every locale permanently one version behind.
        private const string WhatsNewText =
            "• The panel speaks your language: German, French, Spanish, Italian, Portuguese (BR), " +
            "Polish, Dutch and Swedish. It follows Valheim's own language setting by default — " +
            "change it under Settings → Language.\n\n" +
            "• Smarter search: results are ranked by relevance, so typing \"iron\" puts Iron first " +
            "instead of burying it alphabetically. Typing no longer re-sorts the whole list on " +
            "every keystroke.\n\n" +
            "• Multi-step undo: Undo spawn now steps back through your last 20 spawns instead of " +
            "just one, and each admin has their own history — your undo can't delete someone else's " +
            "work.\n\n" +
            "• Destructive actions ask first: Kill ALL loaded, Ground items, Clear trees and Ban " +
            "now need a second click to confirm.\n\n" +
            "• Status effects for anyone: the status-effect browser now has the same Me/any-player " +
            "target picker as the skill browser (needs companion 2.3.0 on the target).\n\n" +
            "• Undo actually works on dedicated servers now — the destroy silently did nothing " +
            "server-side unless the server owned the object.\n\n" +
            "• Scrolling panel lists no longer zooms the camera (for real this time).\n\n" +
            "• Layout survives big font sizes: labels never wrap mid-word and buttons no longer " +
            "stretch across the row.\n\n" +
            "• Server tab: added ZDOs loaded, zones loaded and session uptime.\n\n" +
            "• Smoother: the panel does far less work per frame across every tab, and switches to a " +
            "readable font automatically if the game's Norse font can't draw your language.\n\n" +
            "• If the mod has been useful, there's now a \"Buy the developer a coffee\" button in " +
            "Settings. Entirely optional — the mod is free and always will be.";

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
        private GUIStyle _tabStyle, _catStyle, _rowEven, _rowOdd, _dimLabelStyle, _textAreaStyle;
        private GUIStyle _cellStyle, _dimCellStyle;   // fixed-width row columns: no wrap, clip overflow
        private Texture2D _texWood;   // window background — kept so the opacity slider can recolor it in place
        private Texture2D _texRule;   // thin gold rule used by DrawSection dividers
        private Texture2D _logoTex;   // embedded logo header (null = missing/failed, panel renders without it)
        private bool _logoTried;
        private float _logoAspect = 3.63f;   // width/height; recomputed from the decoded texture
        private ConfigEntry<bool> _showLogoCfg;
        private bool _skinReady;
        private bool _fontApplied;
        private Font _appliedFont;    // the font actually applied to the styles (null = Unity default) — destroyed-check canary
        private bool _wasInWorld;     // tracks ZNet presence so per-session state resets exactly once per logout
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

        // Record where the local player died so the Player tab can teleport back there. Prefix, because the
        // position must be read before OnDeath hands the body to the ragdoll/teardown path. Persisted to
        // config so the death point survives a relog (deaths are rare; one small config write is fine).
        [HarmonyPatch(typeof(Player), "OnDeath")]
        private static class DeathPointPatch
        {
            [HarmonyPrefix]
            private static void Prefix(Player __instance)
            {
                if (Instance == null || __instance == null || __instance != Player.m_localPlayer) return;
                var p = __instance.transform.position;
                Instance._lastDeathCfg.Value =
                    string.Format(CultureInfo.InvariantCulture, "{0:0.#},{1:0.#},{2:0.#}", p.x, p.y, p.z);
                Instance.Config.Save();
            }
        }

        // Freeze camera zoom while the panel is open. Zeroing ZInput.GetMouseScrollWheel proved unreliable
        // in the field — Mono's JIT inlines the wrapper AND Internal_GetMouseScrollWheel into
        // GameCamera.UpdateCamera, bypassing any detour on them — so the zoom result is pinned instead.
        //
        // Field-verified 2026-07-21: restoring m_distance in an UpdateCamera POSTFIX is NOT enough. The wheel
        // is applied to m_distance and the camera transform is positioned from it INSIDE the same call
        // (GetCameraPosition), so a postfix-only restore fixes the field after the camera already moved —
        // scrolling a panel list still visibly zoomed every frame. The restore has to land BETWEEN the wheel
        // math and the positioning: a prefix on GetCameraPosition, which UpdateCamera calls right after the
        // zoom block. The dead/ragdoll and attached branches never reach GetCameraPosition, so the prefix
        // can't disturb those.
        //
        // Deliberately NOT gated on the camera-lock toggle (unlike mouse-LOOK): the wheel over an open panel
        // is always list-scrolling, never an intentional zoom.
        [HarmonyPatch]
        private static class ZoomFreezePatch
        {
            private static readonly AccessTools.FieldRef<GameCamera, float> Distance =
                AccessTools.FieldRefAccess<GameCamera, float>("m_distance");
            // The free-fly branch returns before any of the m_distance math and instead feeds the wheel into
            // its fly-speed multiplier (×1.44 per up-notch), so scrolling a panel list while spectating in
            // free-fly silently re-scaled the camera speed. Same pin, second field. Resolved defensively:
            // if the field is ever renamed the FieldRef is null and free-fly simply isn't pinned.
            private static readonly AccessTools.FieldRef<GameCamera, float> FlySpeed = ResolveFlySpeed();

            private static AccessTools.FieldRef<GameCamera, float> ResolveFlySpeed()
            {
                try { return AccessTools.FieldRefAccess<GameCamera, float>("m_freeFlySpeed"); }
                catch { return null; }
            }

            private static float _pre;
            private static float _preFly;
            private static bool _armed;   // only restore within an UpdateCamera call that we snapshotted

            private static bool Active => Instance != null && Instance._visible;

            [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
            [HarmonyPrefix]
            private static void Capture(GameCamera __instance)
            {
                _pre = Distance(__instance);
                if (FlySpeed != null) _preFly = FlySpeed(__instance);
                _armed = true;
            }

            // Runs after the wheel has been folded into m_distance but before the camera is positioned.
            [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
            [HarmonyPrefix]
            private static void RestoreBeforePositioning(GameCamera __instance)
            {
                if (_armed && Active) Distance(__instance) = _pre;
            }

            // Belt-and-braces: keep the field clean even on frames where GetCameraPosition is skipped
            // (dead/ragdoll, attached) so no wheel delta survives into the next frame.
            [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
            [HarmonyPostfix]
            private static void RestoreAfter(GameCamera __instance)
            {
                if (_armed && Active)
                {
                    Distance(__instance) = _pre;
                    if (FlySpeed != null) FlySpeed(__instance) = _preFly;
                }
                _armed = false;
            }
        }

        // Show the mod on the main menu under the game's version line, ValheimPlus-style. SetupGui is
        // where the game writes m_versionLabel.text, so a postfix always appends after it. TMP_Text is
        // driven via reflection so the project needs no TextMeshPro compile reference; any failure is
        // cosmetic-only and logged as a warning.
        [HarmonyPatch(typeof(FejdStartup), "SetupGui")]
        private static class MenuVersionPatch
        {
            [HarmonyPostfix]
            private static void Postfix(FejdStartup __instance)
            {
                try
                {
                    var label = AccessTools.Field(typeof(FejdStartup), "m_versionLabel")?.GetValue(__instance);
                    var textProp = label?.GetType().GetProperty("text");
                    if (textProp?.GetValue(label) is string cur && !cur.Contains("Advanced Admin Panel"))
                        textProp.SetValue(label, cur + $"\n<color=#FAC759>Advanced Admin Panel {PluginVersion}</color>");
                }
                catch (Exception e) { Instance?.Logger.LogWarning($"Menu version line failed (cosmetic only): {e.Message}"); }
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
            _languageCfg = Config.Bind("UI", "Language", Loc.MenuNames[0], new ConfigDescription(
                "Panel language. 'Auto' follows Valheim's own language setting. Item and creature names always " +
                "follow the game regardless of this setting.",
                new AcceptableValueList<string>(Loc.MenuNames)));
            _panelAlphaCfg = Config.Bind("UI", "PanelOpacity", 96, new ConfigDescription(
                "Panel background opacity in percent.", new AcceptableValueRange<int>(55, 100)));
            _cameraLockCfg = Config.Bind("UI", "CameraLockWhilePanelOpen", true,
                "Lock mouse-look while the panel is open (like the inventory). Turn off to keep the camera live.");
            _showLogoCfg = Config.Bind("UI", "ShowLogoHeader", true,
                "Show the Advanced Admin Panel logo at the top of the panel.");
            _autoWhatsNewCfg = Config.Bind("UI", "ShowWhatsNewOnUpdate", true,
                "Open the What's New side panel once after the mod updates.");
            _seenVersionCfg = Config.Bind("UI", "WhatsNewSeenVersion", "",
                "Last version whose What's New was shown (internal bookkeeping).");
            _bugWebhookCfg = Config.Bind("BugReport", "WebhookUrl", DefaultBugWebhook,
                "Discord webhook that receives in-panel bug reports. Empty disables the Send button.");
            _lastDeathCfg = Config.Bind("Player", "LastDeathPos", "",
                "Your last death position, auto-recorded for the 'TP to last death' button (x,y,z).");
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
            try { Harmony.CreateAndPatchAll(typeof(DeathPointPatch)); }
            catch (Exception e) { Logger.LogWarning($"Death-point patch failed (panel still works): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(ZoomFreezePatch)); }
            catch (Exception e) { Logger.LogWarning($"Zoom-freeze patch failed (panel still works): {e.Message}"); }
            try { Harmony.CreateAndPatchAll(typeof(MenuVersionPatch)); }
            catch (Exception e) { Logger.LogWarning($"Menu version-line patch failed (panel still works): {e.Message}"); }
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Press {_toggleKey.Value} in-game.");
            StartUpdateCheck();
        }

        // One GitHub-releases lookup per game launch, on a worker thread — the game never waits on it.
        // Any failure (offline, rate-limited, renamed repo) just means no banner.
        private void StartUpdateCheck()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                    using (var http = new System.Net.Http.HttpClient())
                    {
                        http.Timeout = TimeSpan.FromSeconds(10);
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("AdvancedAdminPanel/" + PluginVersion);   // GitHub requires a UA
                        var json = http.GetStringAsync(ReleasesApi).GetAwaiter().GetResult();
                        var m = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                        if (m.Success && new System.Version(m.Groups[1].Value) > new System.Version(PluginVersion))
                            _updateAvailable = m.Groups[1].Value;
                    }
                }
                catch { /* stay silent — an update notice is never worth an error */ }
            });
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

        // ParseKv splits and allocates a fresh Dictionary on every call, and the presets/bookmarks rows are
        // drawn from it on both OnGUI passes of every frame. Cache the parse against the raw config string:
        // any edit changes the string, which invalidates the cache on its own with no explicit bookkeeping.
        private string _presetsKvRaw, _bookmarksKvRaw;
        private Dictionary<string, string> _presetsKv, _bookmarksKv;

        private Dictionary<string, string> PresetsKv()
        {
            var raw = _spawnPresetsCfg.Value ?? "";
            if (_presetsKv == null || _presetsKvRaw != raw) { _presetsKv = ParseKv(raw); _presetsKvRaw = raw; }
            return _presetsKv;
        }

        private Dictionary<string, string> BookmarksKv()
        {
            var raw = _bookmarksCfg.Value ?? "";
            if (_bookmarksKv == null || _bookmarksKvRaw != raw) { _bookmarksKv = ParseKv(raw); _bookmarksKvRaw = raw; }
            return _bookmarksKv;
        }

        // Per-frame snapshots of the two config-backed row lists. These are NOT just a perf cache: clicking Del
        // on a bookmark (or Save on a preset) rewrites the config during the event pass, so a draw loop reading
        // the config live would give Repaint a different row count than Layout reserved controls for — IMGUI's
        // "control N in a group with only M controls". Rebuilding only on Layout pins the count for the frame,
        // and the edit shows up on the next one.
        private List<KeyValuePair<string, string>> _presetRowsLayout, _bookmarkRowsLayout;

        private List<KeyValuePair<string, string>> PresetRows()
        {
            if (Event.current != null && Event.current.type == EventType.Layout || _presetRowsLayout == null)
                _presetRowsLayout = new List<KeyValuePair<string, string>>(PresetsKv());
            return _presetRowsLayout;
        }

        private List<KeyValuePair<string, string>> BookmarkRows()
        {
            if (Event.current != null && Event.current.type == EventType.Layout || _bookmarkRowsLayout == null)
                _bookmarkRowsLayout = new List<KeyValuePair<string, string>>(BookmarksKv());
            return _bookmarkRowsLayout;
        }

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
                ZRoutedRpc.instance.Register<string>("AP_VersionData", OnVersionData);
            }
        }

        // Server companion's reply to the AP_SrvVersion handshake.
        private static void OnVersionData(long sender, string version)
        {
            if (Instance != null) Instance._srvCompVersion = version;
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
                if (_visible)
                {
                    // Resolve the language here rather than only in Awake: in "Auto" mode this reads
                    // Localization.instance, which does not exist that early in startup. Refresh early-outs
                    // unless the resolved code actually changed, so this is a no-op on every later open.
                    Loc.Refresh(_languageCfg.Value);
                    RefreshCaches();
                    // One-shot What's New after an update (opt-out in Settings). Marked seen immediately so
                    // closing it without reading doesn't re-trigger it every open.
                    if (_autoWhatsNewCfg.Value && _seenVersionCfg.Value != PluginVersion)
                    {
                        _sideMode = SideMode.WhatsNew;
                        _seenVersionCfg.Value = PluginVersion;
                        Config.Save();
                    }
                }
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

            // Companion version handshake: ask the server's companion for its version while the panel is
            // open and we have no answer. Hard-throttled to one request per 30s — if the server companion
            // is old (no AP_SrvVersion handler) there is simply no reply, never a loop.
            if (_visible && _srvCompVersion == null && ZNet.instance != null && Time.time >= _nextVersionReq)
            {
                _nextVersionReq = Time.time + 30f;
                if (_versionReqFirst == 0f) _versionReqFirst = Time.time;
                SrvRpc("AP_SrvVersion");
            }

            // Per-session state reset, fired once when leaving a world. Logout destroys the objects behind
            // the caches (ObjectDB prefabs, sprites, ZNet roster), so drop everything that points at them
            // and let the existing lazy rebuilds re-create it all on the next login.
            if (ZNet.instance != null) _wasInWorld = true;
            else if (_wasInWorld)
            {
                _wasInWorld = false;
                ResetSessionState();
            }

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

        // Everything here references per-world objects (prefabs, sprites, peers) that logout destroys.
        // Cheat flags (_god/_ghost/...) deliberately survive — ReapplyPlayerState restores them on the next
        // spawn, and the skin textures/font self-heal separately (SolidTex hideFlags + ApplyFont canary).
        private void ResetSessionState()
        {
            FlushNotes();
            _visible = false;                       // panel is meaningless at the main menu; reopen re-runs RefreshCaches
            _itemIndex = null; _seIndex = null;
            _creatureIndex = null; _creatureCats = null;
            _filteredItemsCache = null; _itemFilterKey = "";
            _filteredCreaturesCache = null; _creatureFilterKey = "";
            _seFilteredCache = null; _seFilterKey = null;
            _subCatsCache = null; _subCatsKey = null;
            _itemWindowList = null; _creWindowList = null;
            _othersSnapshot = null;
            _recentItems.Clear(); _recentVersion++;  // entries hold dead ItemDrop/Sprite refs from the old world
            _giveTargetId = 0;
            _openDropdown = null; _openDropdownLayout = null;
            _sideMode = SideMode.None;
            _inspectPlayerName = null; _inspectInventory = null; _inspectPending = false; _inspectTargetId = 0;
            _joinLog.Clear(); _lastSeenPlayers.Clear(); _seenPlayersInit = false;
            _skillTargetId = 0; _skillMsg = "";
            _srvCompVersion = null; _versionReqFirst = 0f; _nextVersionReq = 0f;
            _appliedTo = null;
            _baseWalk = -1f;                        // force a fresh base-stat capture on the next player
        }

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
            if (_screenToWorld == null) { Message(Loc.T("world.msg_map_tp_unavailable")); return; }

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
            Message(Loc.T("world.msg_tp_to", label));
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
        // Keys, not literals — ClassifySe returns an index into this array and the header is localized at draw.
        private static readonly string[] SeBucketKeys =
        {
            "se.boss_powers", "se.set_bonuses", "se.potions",
            "se.debuffs", "se.comfort", "se.other"
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

        // Search-filtered snapshot of _seIndex. Rebuilt ONLY when the DEBOUNCED search text changes — same
        // discipline as FilteredItems/FilteredCreatures, and not optional: the TextField mutates _seSearch
        // during the KeyDown pass, so filtering on the live string rebuilt the list BETWEEN that event's
        // Layout and its handling — a backspace that grew the list threw "control N in a group with only M
        // controls" on every list-growing keystroke (caught by the tab try/catch, torn frame). Debouncing
        // pins rebuilds to the Layout pass, where changing the control count is legal.
        private string _seSearchApplied = "", _seSearchLastSeen = "";
        private float _seSearchDirtyAt = -1f;

        private List<SeEntry> FilteredStatusEffects()
        {
            var q = DebounceSearch(_seSearch, ref _seSearchApplied, ref _seSearchLastSeen, ref _seSearchDirtyAt);
            if (_seFilteredCache != null && q == _seFilterKey) return _seFilteredCache;
            if (_seIndex == null)
            {
                _seFilteredCache = new List<SeEntry>();
                _seFilterKey = q;
                return _seFilteredCache;
            }
            IEnumerable<SeEntry> src = _seIndex;
            if (!string.IsNullOrEmpty(q))
                src = _seIndex.Where(e =>
                    e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Se.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            _seFilteredCache = src.ToList();
            _seFilterKey = q;
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
            // Logout runs Resources.UnloadUnusedAssets(); textures referenced only from non-serialized
            // plugin fields count as "unused" to that sweep and get destroyed, which silently strips the
            // background off every style (flat, unboxed buttons after a relog). HideAndDontSave exempts them.
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void EnsureSkin()
        {
            // _texWood doubles as the canary: if the asset sweep destroyed the skin textures anyway
            // (fake-null), rebuild the whole skin instead of trusting the latch.
            if (_skinReady && _texWood != null) return;
            _texWood = SolidTex(WoodColor(_panelAlphaLive));
            _texRule = SolidTex(new Color(0.62f, 0.46f, 0.22f, 0.45f));
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

            _textAreaStyle = new GUIStyle(_textFieldStyle) { wordWrap = true };

            _toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 13 };
            _toggleStyle.normal.textColor = parchment;
            _toggleStyle.onNormal.textColor = gold;
            _toggleStyle.hover.textColor = parchment;
            _toggleStyle.onHover.textColor = gold;

            // polish: comfortable button padding + margins
            _buttonStyle.padding = new RectOffset(10, 10, 5, 5);
            _buttonStyle.margin = new RectOffset(3, 3, 3, 3);
            _buttonStyle.fontSize = 13;
            // Buttons size to their text (MinWidth is the floor) instead of stretching to fill the row.
            // With the default stretch, any row without a FlexibleSpace divided its slack among the buttons —
            // at 740px that turned "Send to all" and "FIGHT!" into half-window slabs (field-reported).
            _buttonStyle.stretchWidth = false;

            // main tab bar: bigger, bolder. Tabs and chips keep the stretch that plain buttons lost —
            // eight content-width tabs can overflow the 660px minimum window at large font sizes, and the
            // evenly-filled tab bar is the intended look.
            _tabStyle = new GUIStyle(_buttonStyle) { fontSize = 14, stretchWidth = true };
            _tabStyle.padding = new RectOffset(12, 12, 8, 8);
            _tabStyle.margin = new RectOffset(3, 3, 4, 4);
            var goldBg = SolidTex(new Color(0.42f, 0.30f, 0.12f, 1f));
            _tabStyle.onNormal.background = goldBg;
            _tabStyle.onHover.background = goldBg;

            // category chips: slightly smaller, clearly selected
            _catStyle = new GUIStyle(_buttonStyle) { fontSize = 12, stretchWidth = true };
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

            // Fixed-width row columns (list name cells etc.). GUI.skin.label wraps by default, so a name that
            // outgrows its column at a larger font size folded onto a second line inside a fixed-height row.
            // Cells clip instead — a truncated name reads better than a smeared row.
            _cellStyle = new GUIStyle(_labelStyle) { wordWrap = false, clipping = TextClipping.Clip };
            _dimCellStyle = new GUIStyle(_dimLabelStyle) { wordWrap = false, clipping = TextClipping.Clip };

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

        /// <summary>
        /// A bounded sample of the game-supplied names the panel draws, used to decide whether the chosen font
        /// can actually render them. Bounded because this runs on the font-resolve path: the first entries are
        /// enough to reveal the script in use, and scanning the whole item DB every resolve would not tell us
        /// anything the first few hundred names don't.
        /// </summary>
        private IEnumerable<string> ContentFontSamples()
        {
            const int cap = 200;
            var n = 0;
            if (_itemIndex != null)
                foreach (var e in _itemIndex)
                {
                    if (n++ >= cap) break;
                    yield return e.Display;
                }
            n = 0;
            if (_creatureIndex != null)
                foreach (var e in _creatureIndex)
                {
                    if (n++ >= cap) break;
                    yield return e.Display;
                }
        }

        // Apply the configured font to every text GUIStyle once it becomes available.
        private void ApplyFont()
        {
            // Self-heal, two cases: (a) the applied game font was destroyed by the logout asset sweep
            // (fake-null), or (b) an earlier resolve gave up after 10 tries — e.g. the panel was opened at
            // the main menu where the Norse fonts aren't loaded — and permanently fell back to the default
            // font (clipped buttons, wrong metrics). Re-arm the resolver in both cases; _nextFontTry keeps
            // the engine-wide scan throttled to once per second, so a truly missing font can't peg the frame.
            if (_fontApplied && _appliedFont == null && _fontChoiceCfg.Value != "Default")
            {
                _fontApplied = false;
                _fontTries = 0;
                _appliedFont = null;
            }
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

                // Valheim's Norse fonts are Latin-only, but the panel renders text in the *game's* language:
                // LocalizeSafe returns item/creature names in whatever the player set Valheim to, so a Russian
                // or Chinese client would draw Cyrillic/CJK through a font with no such glyphs and get blank
                // boxes. (The game's own UI looking fine proves nothing — that is TextMeshPro, a separate font
                // system from IMGUI.) If the chosen font can't draw what we're about to show, drop to Unity's
                // built-in font, which has far broader coverage. Readable beats pretty.
                if (f != null && Loc.ResolveFontSafety(f, ContentFontSamples()))
                {
                    Logger.LogInfo($"Font '{f.name}' lacks glyphs for the current language; using the default font instead.");
                    f = null;
                }
            }
            foreach (var s in new[]
            {
                _windowStyle, _buttonStyle, _labelStyle, _headerStyle, _textFieldStyle,
                _toggleStyle, _tabStyle, _catStyle, _rowEven, _rowOdd, _dimLabelStyle, _textAreaStyle,
                _cellStyle, _dimCellStyle
            })
                if (s != null) s.font = f;
            _appliedFont = f;
            _fontApplied = true;
        }

        // ==================== Embedded images ====================
        /// <summary>
        /// Load an embedded PNG by filename suffix. Returns null on ANY failure (resource absent, decode
        /// failure, ImageConversion stripped) so a caller can fall back to text — this runs off the OnGUI
        /// path and an exception there would tear the frame.
        /// </summary>
        private static Texture2D LoadEmbeddedPng(string fileSuffix)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                byte[] bytes;
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    bytes = new byte[s.Length];
                    var off = 0;
                    while (off < bytes.Length)
                    {
                        var n = s.Read(bytes, off, bytes.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                }
                // ImageConversion targets netstandard 2.1 and can't be compile-referenced from net48.
                var loadImage = AccessTools.Method(
                    AccessTools.TypeByName("UnityEngine.ImageConversion"), "LoadImage",
                    new[] { typeof(Texture2D), typeof(byte[]) });
                if (loadImage == null) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                { filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave };   // survive the logout asset sweep
                return (bool)loadImage.Invoke(null, new object[] { tex, bytes }) ? tex : null;
            }
            catch { return null; }
        }

        private Texture2D _coffeeTex;
        private bool _coffeeTried;

        private Texture2D CoffeeTex()
        {
            if (_coffeeTried && _coffeeTex != null) return _coffeeTex;
            // Re-arm when the texture was swept (fake-null) by the logout asset cleanup, same as the logo.
            _coffeeTried = true;
            _coffeeTex = LoadEmbeddedPng("coffee.png");
            return _coffeeTex;
        }

        /// <summary>
        /// Support button that opens the donation page in the player's default browser.
        /// Renders icon+label when coffee.png is embedded and plain text when it is not, so the mod builds
        /// and runs either way rather than depending on an art asset being present.
        /// </summary>
        private void DrawCoffeeButton(float minWidth)
        {
            var tex = CoffeeTex();
            var label = Loc.T("set.buy_coffee");
            var content = tex != null ? new GUIContent("  " + label, tex) : new GUIContent(label);
            if (GUILayout.Button(content, _buttonStyle, GUILayout.MinWidth(minWidth), GUILayout.Height(28)))
                Application.OpenURL(DonateUrl);
        }

        // ==================== Logo header ====================
        // Load the embedded logo once. Any failure (missing resource, decode error, stripped module)
        // leaves _logoTex null and the panel simply renders with its text title instead.
        private Texture2D LogoTex()
        {
            if (_logoTried) return _logoTex;
            _logoTried = true;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                byte[] bytes;
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    bytes = new byte[s.Length];
                    var off = 0;
                    while (off < bytes.Length)
                    {
                        var n = s.Read(bytes, off, bytes.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                }
                // ImageConversion lives in UnityEngine.ImageConversionModule, which targets netstandard 2.1
                // and can't be compile-referenced from net48 — resolve LoadImage at runtime instead.
                var loadImage = AccessTools.Method(
                    AccessTools.TypeByName("UnityEngine.ImageConversion"), "LoadImage",
                    new[] { typeof(Texture2D), typeof(byte[]) });
                if (loadImage == null) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                { filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave };   // survive the logout asset sweep
                if (!(bool)loadImage.Invoke(null, new object[] { tex, bytes })) return null;
                _logoAspect = (float)tex.width / tex.height;
                _logoTex = tex;
            }
            catch (Exception e) { Logger.LogWarning($"Logo header failed to load (panel still works): {e.Message}"); }
            return _logoTex;
        }

        // One source of truth for the drawn size so the ListView reserve always matches the header.
        // 55% of the window width reads large without dwarfing the tabs; capped so huge windows
        // don't turn the header into a banner.
        private Vector2 LogoSize()
        {
            var w = Mathf.Min(_windowRect.width * 0.55f, 430f);
            return new Vector2(w, w / _logoAspect);
        }

        private float LogoHeaderHeight()
        {
            if (_logoTex == null || _showLogoCfg == null || !_showLogoCfg.Value) return 0f;
            return LogoSize().y + 6f;
        }

        private void DrawLogoHeader()
        {
            if (LogoTex() == null || !_showLogoCfg.Value) return;
            var size = LogoSize();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var r = GUILayoutUtility.GetRect(size.x, size.y, GUILayout.Width(size.x), GUILayout.Height(size.y));
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(r, _logoTex, ScaleMode.ScaleToFit, true);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
        }

        // A host runs the companion (or not) in ITS OWN process, so its presence is knowable directly —
        // no RPC round-trip involved. Nullable-bool caches the one-time reflection scan.
        private static bool? _companionLoadedHost;
        private static bool CompanionLoadedInProcess()
        {
            _companionLoadedHost = _companionLoadedHost
                ?? (AccessTools.TypeByName("AdminPanelCompanion.CompanionPlugin") != null);
            return _companionLoadedHost.Value;
        }

        // One-line mismatch warning, or null when everything is fine. The no-reply case only fires on a
        // REMOTE server and only after 15s of silence, so a slow login can't flash a false warning.
        // A host is checked in-process instead: the old "a host answers itself instantly" assumption held
        // only when the host HAD the companion — a host without it got silent no-ops and success toasts
        // for every server action, with no warning path able to fire.
        private string CompanionWarning()
        {
            if (ZNet.instance == null) return null;
            if (ZNet.instance.IsServer() && !CompanionLoadedInProcess())
                return Loc.T("chrome.companion_missing_host");
            if (_srvCompVersion != null)
                return _srvCompVersion == PluginVersion
                    ? null
                    : Loc.T("chrome.version_mismatch", PluginVersion, _srvCompVersion);
            if (!ZNet.instance.IsServer() && _versionReqFirst > 0f && Time.time - _versionReqFirst > 15f)
                return Loc.T("chrome.companion_silent");
            return null;
        }

        // ==================== Section divider ====================
        // One visual language for section breaks across all tabs: a thin gold rule, then the section
        // title. Replaces the bare header Labels that made long tabs read as one undifferentiated column.
        // Emits the same controls on every IMGUI pass (GetRect + Label), so control counts stay stable.
        private void DrawSection(string title)
        {
            GUILayout.Space(10);
            var r = GUILayoutUtility.GetRect(1f, 2f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && _texRule != null)
                GUI.DrawTexture(new Rect(r.x, r.y, r.width - 14f, 2f), _texRule);   // -14 keeps clear of the scrollbar
            GUILayout.Space(4);
            if (!string.IsNullOrEmpty(title)) GUILayout.Label(title, _headerStyle);
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
            // With the logo header shown the title text would be redundant — keep just the close hint.
            var logoShown = _logoTex != null && _showLogoCfg.Value;
            _windowRect = GUI.Window(918273, _windowRect, DrawWindow,
                logoShown ? Loc.T("chrome.title_hint", _toggleKey.Value)
                          : Loc.T("chrome.title_full", _toggleKey.Value), _windowStyle);
            // Save after the user finishes moving or resizing the window (only runs while _visible).
            if (Event.current.type == EventType.MouseUp) SaveWindowRect();

            // Docked side window (What's New / Bug Report). Its rect derives from the panel's every frame,
            // so it stays glued to the panel's right edge (flipping to the left edge at the screen border);
            // the returned rect is deliberately discarded — the side window is not independently draggable.
            if (_sideMode != SideMode.None)
            {
                const float sideW = 340f;
                var sideH = Mathf.Clamp(_windowRect.height, 380f, 560f);
                var sx = _windowRect.xMax + 6f + sideW <= Screen.width
                    ? _windowRect.xMax + 6f
                    : _windowRect.x - sideW - 6f;
                GUI.Window(918274, new Rect(sx, _windowRect.y, sideW, sideH), DrawSideWindow,
                    _sideMode == SideMode.WhatsNew ? "What's New" : "Bug Report", _windowStyle);
            }
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
            Mathf.Clamp(_windowRect.height - reserve - LogoHeaderHeight()
                        - Mathf.Max(0, _fontSizeLive - 13) * 3f, 160f, 4000f);

        private void DrawWindow(int id)
        {
            // Close button pinned to the title-bar corner (same affordance as the side window's ✕).
            // Fixed-rect GUI.Button, not GUILayout — it lives outside the layout flow, so the control
            // count stays identical on every pass. Runs the same cleanup as the F7 close path.
            if (GUI.Button(new Rect(_windowRect.width - 34f, 4f, 28f, 22f), "✕", _buttonStyle))
            {
                _visible = false;
                FlushNotes();
                CommitUiSettings();
                _openDropdown = null;
            }

            if (LocalPlayer == null)
            {
                GUILayout.Label(Loc.T("chrome.not_in_game"), _labelStyle);
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
                _updateBannerLayout = _updateAvailable;   // arrives from a worker thread — pin it per frame
                _versionWarnLayout = CompanionWarning();  // recomputed once per frame, shown consistently across passes
                // One roster build per frame, shared by every tab. OtherPlayers() runs GetPlayerList()+LINQ and
                // allocates a List; several tabs called it independently, each on both the Layout and Repaint
                // pass. Building it here also guarantees the two passes see an identical roster, which matters
                // because tabs emit one control row per player.
                _othersSnapshot = OtherPlayers();
                // Same reasoning: GetGlobalKeys() allocates and the World tab draws one row per key.
                _globalKeysLayout = ZoneSystem.instance != null
                    ? new List<string>(ZoneSystem.instance.GetGlobalKeys())
                    : null;
            }

            DrawLogoHeader();

            if (_updateBannerLayout != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("chrome.update_available", _updateBannerLayout, PluginVersion), _headerStyle);
                if (GUILayout.Button(Loc.T("chrome.get_update"), _buttonStyle, GUILayout.MinWidth(110)))
                    Application.OpenURL(ReleasesPage);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (_versionWarnLayout != null)
                GUILayout.Label(_versionWarnLayout, _headerStyle);

            GUILayout.BeginHorizontal();
            for (var i = 0; i < TabKeys.Length; i++)
            {
                var pressed = GUILayout.Toggle(_tab == i, Loc.T(TabKeys[i]), _tabStyle);
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
            return idx < 0 ? Loc.T("common.nobody") : others[idx].m_name;
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
            { Message(Loc.T("common.not_connected_srv")); return; }
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

        // ==================== Cached localized label arrays ====================
        // MainCats / *SortModes double as state keys (_mainCat is compared against them and is written into
        // ItemEntry.Cat), so those arrays stay English and only their *display* is localized. The localized
        // arrays are cached and rebuilt only when the language changes: DropdownButton/DropdownOptions take a
        // string[], and building one per OnGUI pass would allocate twice per frame for the life of the panel.
        private static readonly string[] MainCatKeys =
        {
            "cat.all", "cat.fav", "cat.recent", "cat.kits", "cat.weapons", "cat.shields", "cat.armor",
            "cat.accessories", "cat.ammo", "cat.tools", "cat.food", "cat.materials", "cat.trophies", "cat.misc"
        };

        private string[] _mainCatLabels, _itemSortLabels, _creatureSortLabels;
        private string _labelsLang;

        private void EnsureLocalizedLabels()
        {
            if (_labelsLang == Loc.ActiveCode && _mainCatLabels != null) return;
            _labelsLang = Loc.ActiveCode;
            _mainCatLabels = new string[MainCatKeys.Length];
            for (var i = 0; i < MainCatKeys.Length; i++) _mainCatLabels[i] = Loc.T(MainCatKeys[i]);
            _itemSortLabels = new[] { Loc.T("sort.az"), Loc.T("sort.za"), Loc.T("sort.category") };
            _creatureSortLabels = new[] { Loc.T("sort.az"), Loc.T("sort.za"), Loc.T("sort.faction") };
        }

        // ==================== Destructive-action confirmation ====================
        // "Kill ALL loaded", "Clear trees" and friends are irreversible, act on the whole loaded world, and sit
        // one stray click away from buttons you press constantly. They now arm on the first click and only fire
        // on a second click within the window below.
        //
        // Deliberately a two-step button rather than a modal: a conditionally-rendered popup changes the control
        // count between the Layout and Repaint passes, which is exactly the IMGUI failure the dropdown code below
        // already has to work around. Swapping a button's *label* keeps the count identical on every pass.
        private const float ConfirmWindowSeconds = 3f;
        private string _confirmAction;
        private float _confirmExpiresAt;

        /// <summary>
        /// Draws a button that requires two clicks. Returns true only on the confirming click.
        /// Arming is per-<paramref name="id"/>, so arming one destructive button disarms any other.
        /// </summary>
        private bool ConfirmButton(string id, string label, params GUILayoutOption[] opts)
        {
            var armed = _confirmAction == id && Time.realtimeSinceStartup < _confirmExpiresAt;
            if (!GUILayout.Button(armed ? Loc.T("common.confirm_prompt") : label, _buttonStyle, opts))
                return false;
            if (armed) { _confirmAction = null; return true; }
            _confirmAction = id;
            _confirmExpiresAt = Time.realtimeSinceStartup + ConfirmWindowSeconds;
            return false;
        }

        // ==================== Search: debounce + relevance ranking ====================
        // The filter cache is keyed on the search text, so every keystroke used to invalidate it and re-run the
        // whole LINQ chain *and* a full sort over the entire index — twice per frame while typing, since OnGUI
        // runs Layout and Repaint. Debouncing moves that work to "when the admin stops typing"; the list simply
        // keeps showing the previous result for the intervening moment, which reads as instant.
        private const float SearchDebounceSeconds = 0.15f;

        private string _itemSearchApplied = "", _itemSearchLastSeen = "";
        private float _itemSearchDirtyAt = -1f;
        private string _creatureSearchApplied = "", _creatureSearchLastSeen = "";
        private float _creatureSearchDirtyAt = -1f;

        /// <summary>
        /// Advance a debounced search term and return the value the filter should actually use. Each change
        /// restarts the window, so holding a key down doesn't rebuild once per character.
        /// </summary>
        private static string DebounceSearch(string live, ref string applied, ref string lastSeen, ref float dirtyAt)
        {
            // Advance only on the Layout pass. Layout and Repaint run within the same frame and
            // Time.realtimeSinceStartup moves between them, so letting the window elapse on Repaint could
            // return a longer/shorter list than Layout already reserved controls for — IMGUI's "control N in a
            // group with only M controls". Same snapshot-on-Layout rule the dropdown code follows.
            if (Event.current == null || Event.current.type != EventType.Layout) return applied;

            if (!string.Equals(live, lastSeen, StringComparison.Ordinal))
            {
                lastSeen = live;
                dirtyAt = Time.realtimeSinceStartup;
            }
            if (dirtyAt >= 0f && Time.realtimeSinceStartup - dirtyAt >= SearchDebounceSeconds)
            {
                applied = live;
                dirtyAt = -1f;
            }
            return applied;
        }

        /// <summary>
        /// Relevance rank for a search hit; lower is better. Plain substring matching ranks "Ancient Bark Spear"
        /// the same as "Bark" for the query "bark", so a short query buries its own obvious answer somewhere in
        /// an alphabetical list. Exact and prefix hits come first instead.
        /// </summary>
        private static int MatchRank(string display, string id, string q)
        {
            if (string.Equals(display, q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (display.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 1;
            if (WordStartsWith(display, q)) return 2;
            if (display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (id.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 4;
            return 5;   // matched the prefab name only
        }

        // True when any word after a space starts with q, so "spear" ranks well against "Ancient Bark Spear".
        private static bool WordStartsWith(string s, string q)
        {
            var i = s.IndexOf(' ');
            while (i >= 0 && i + 1 < s.Length)
            {
                if (string.Compare(s, i + 1, q, 0, q.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
                i = s.IndexOf(' ', i + 1);
            }
            return false;
        }

        private List<ItemEntry> FilteredItems()
        {
            var q = DebounceSearch(_itemSearch, ref _itemSearchApplied, ref _itemSearchLastSeen, ref _itemSearchDirtyAt);
            var key = $"{_mainCat}|{_subCat}|{q}|{_favVersion}|{_recentItems.Count}|{_recentVersion}|{_itemSort}";
            if (_filteredItemsCache != null && key == _itemFilterKey) return _filteredItemsCache;

            IEnumerable<ItemEntry> src = _itemIndex;
            if (_mainCat == "★ Fav") src = _itemIndex.Where(e => _favorites.Contains(e.Prefab));
            else if (_mainCat == "Recent") src = _recentItems;
            else if (_mainCat != "All")
            {
                src = _itemIndex.Where(e => e.Cat == _mainCat);
                if (_subCat != "All") src = src.Where(e => e.Sub == _subCat);
            }
            if (!string.IsNullOrEmpty(q))
                src = src.Where(e =>
                    e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Prefab.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            if (_mainCat != "Recent") // Recent keeps its most-recent-first order
            {
                // When a search is active, relevance leads and the shorter name wins ties ("Bark" before
                // "BarkSpear"); the chosen sort then orders entries *within* a rank band, so picking Z→A still
                // does something meaningful instead of burying the obvious match. With no search the no-op
                // primary key collapses this to exactly the old behaviour.
                var ord = string.IsNullOrEmpty(q)
                    ? src.OrderBy(_ => 0)
                    : src.OrderBy(e => MatchRank(e.Display, e.Prefab, q)).ThenBy(e => e.Display.Length);

                switch (_itemSort)
                {
                    case 1: ord = ord.ThenByDescending(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                    case 2: ord = ord.ThenBy(e => e.Cat).ThenBy(e => e.Sub).ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                    default: ord = ord.ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                }
                src = ord;
            }

            _filteredItemsCache = src.ToList();
            _itemFilterKey = key;
            return _filteredItemsCache;
        }

        private void GiveKit((string Name, (string Prefab, int Count)[] Items) kit, long targetUid)
        {
            foreach (var (prefab, count) in kit.Items)
                SendServerGive(targetUid, prefab, count, 1);
            Message(Loc.T("items.kit_sent", kit.Name));
        }

        // ---- Reusable inline dropdown ----
        // Split into trigger + popup so the option list can render BELOW a horizontal row (call DropdownButton
        // inside the row, then DropdownOptions after EndHorizontal). The trigger sets _openDropdown on click, but
        // the options render off _openDropdownLayout (snapshotted on the Layout pass) so the control count stays
        // consistent across a frame's Layout/Repaint/event passes.
        private void DropdownButton(string id, string label, string[] options, int selected, float width)
        {
            var cur = options[Mathf.Clamp(selected, 0, options.Length - 1)];
            // Empty label = the row already has its own label; composing "{label}: {cur}" here would render
            // it twice ("Language: Language: Auto…", worse in locales whose label ends with a colon).
            var text = string.IsNullOrEmpty(label)
                ? $"{cur}  {(_openDropdown == id ? "▲" : "▼")}"
                : $"{label}: {cur}  {(_openDropdown == id ? "▲" : "▼")}";
            if (GUILayout.Button(text, _buttonStyle, GUILayout.MinWidth(width)))
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
                if (GUILayout.Button((i == selected ? "• " : "    ") + options[i], _buttonStyle, GUILayout.MinWidth(width)))
                { selected = i; _openDropdown = null; changed = true; }
            }
            return changed;
        }

        private void DrawItemsTab()
        {
            EnsureLocalizedLabels();
            for (var row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                var half = (MainCats.Length + 1) / 2;
                for (var i = row * half; i < Mathf.Min(MainCats.Length, (row + 1) * half); i++)
                {
                    var pressed = GUILayout.Toggle(_mainCat == MainCats[i], _mainCatLabels[i], _catStyle);
                    if (pressed && _mainCat != MainCats[i]) { _mainCat = MainCats[i]; _subCat = "All"; _itemScroll = Vector2.zero; }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(12);

            // Built once per frame in DrawWindow's Layout block; the null guard only covers a tab drawn before
            // that snapshot ever ran.
            var others = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());

            if (_mainCat == "Kits")
            {
                GUILayout.Label(Loc.T("items.kits_title"), _headerStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("items.give_target"), _labelStyle, GUILayout.MinWidth(80));
                if (GUILayout.Button(GiveTargetName(others), _buttonStyle, GUILayout.MinWidth(180)))
                    CycleGiveTarget(others);
                GUILayout.EndHorizontal();
                _itemScroll = GUILayout.BeginScrollView(_itemScroll, GUILayout.Height(Mathf.Min(ListView(300f), GearKits.Length * 28f + 96f)));
                foreach (var kit in GearKits)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(kit.Name, _cellStyle, GUILayout.Width(140));
                    GUILayout.Label(string.Join(", ", kit.Items.Select(i => i.Count > 1 ? $"{i.Prefab} x{i.Count}" : i.Prefab)), _labelStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("items.to_me"), _buttonStyle, GUILayout.MinWidth(70)))
                        GiveKit(kit, SelfUid());
                    if (GUILayout.Button(Loc.T("items.give"), _buttonStyle, GUILayout.MinWidth(55)))
                    {
                        var gi = GiveTargetIndex(others);
                        if (gi >= 0) GiveKit(kit, PeerIdOf(others[gi]));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(10);
                GUILayout.Label(Loc.T("items.bulk_title"), _headerStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(_bulkPackCfg.Value, _labelStyle);
                if (GUILayout.Button(Loc.T("items.bulk_grab"), _buttonStyle, GUILayout.MinWidth(120)))
                {
                    foreach (var part in _bulkPackCfg.Value.Split(','))
                    {
                        var bits = part.Trim().Split(':');
                        if (bits.Length == 2 && int.TryParse(bits[1], out var n))
                            SendServerGive(SelfUid(), bits[0].Trim(), n, 1);
                    }
                    Message(Loc.T("items.bulk_requested"));
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
                    var allPressed = GUILayout.Toggle(_subCat == "All", Loc.T("common.all"), _catStyle);
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
            GUILayout.Label(Loc.T("common.search"), _labelStyle, GUILayout.MinWidth(50));
            _itemSearch = GUILayout.TextField(_itemSearch, _textFieldStyle);
            GUILayout.Label(Loc.T("items.amount"), _labelStyle, GUILayout.MinWidth(55));
            var amountStr = GUILayout.TextField(_itemAmount.ToString(), _textFieldStyle, GUILayout.Width(50));
            int.TryParse(amountStr, out _itemAmount);
            GUILayout.Label(Loc.T("items.quality"), _labelStyle, GUILayout.MinWidth(50));
            var qStr = GUILayout.TextField(_itemQuality.ToString(), _textFieldStyle, GUILayout.Width(30));
            int.TryParse(qStr, out _itemQuality);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("items.give_target"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(GiveTargetName(others), _buttonStyle, GUILayout.MinWidth(180)))
                CycleGiveTarget(others);
            GUILayout.Label(Loc.T("items.legend"), _labelStyle);
            GUILayout.FlexibleSpace();
            DropdownButton("itemSort", Loc.T("common.sort"), _itemSortLabels, _itemSort, 150);
            GUILayout.EndHorizontal();
            DropdownOptions("itemSort", _itemSortLabels, ref _itemSort, 150);

            if (_itemIndex == null) { GUILayout.Label(Loc.T("items.db_not_loaded"), _labelStyle); return; }

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
                if (GUILayout.Button(_favorites.Contains(e.Prefab) ? "★" : "☆", _buttonStyle, GUILayout.MinWidth(30)))
                    ToggleFavorite(e.Prefab);
                DrawIcon(e);
                GUILayout.Space(6);
                GUILayout.Label(e.Display, _cellStyle, GUILayout.Width(210));
                GUILayout.Label(e.Info, _dimCellStyle);   // stat summary (cached); cell style: a wrap would overpaint the fixed-height row below
                GUILayout.FlexibleSpace();
                var amount = Math.Max(1, _itemAmount);
                var quality = Math.Max(1, _itemQuality);
                if (GUILayout.Button(Loc.T("items.drop"), _buttonStyle, GUILayout.MinWidth(58)))
                { SendServerSpawn(0, e.Prefab, SpawnPos(1.5f), amount, quality, false); MarkRecent(e); Message(Loc.T("items.msg_requested", amount, e.Display)); }
                var bagSafe = HasIcon(e.Drop);
                if (GUILayout.Button(bagSafe ? Loc.T("items.bag") : "✕", _buttonStyle, GUILayout.MinWidth(52)))
                {
                    if (bagSafe) { SendServerGive(SelfUid(), e.Prefab, amount, quality); MarkRecent(e); Message(Loc.T("items.msg_requested_bag", amount, e.Display)); }
                    else Message(Loc.T("items.msg_no_icon_drop", e.Display));
                }
                if (GUILayout.Button(bagSafe ? Loc.T("items.give") : "✕", _buttonStyle, GUILayout.MinWidth(58)))
                {
                    var gi = GiveTargetIndex(others);
                    if (!bagSafe) Message(Loc.T("items.msg_no_icon_give", e.Display));
                    else if (gi >= 0)
                    { SendServerGive(PeerIdOf(others[gi]), e.Prefab, amount, quality); MarkRecent(e); Message(Loc.T("items.msg_sent", amount, e.Display, others[gi].m_name)); }
                    else Message(Loc.T("items.msg_no_target"));
                }
                GUILayout.EndHorizontal();
            }
            var below = total - (first + visible);
            if (below > 0) GUILayout.Space(below * rowH);
            GUILayout.EndScrollView();
            GUILayout.Label(Loc.T("items.count", total), _dimLabelStyle);
        }

        // ==================== Creatures tab ====================
        private List<CreatureEntry> FilteredCreatures()
        {
            var q = DebounceSearch(_creatureSearch, ref _creatureSearchApplied, ref _creatureSearchLastSeen, ref _creatureSearchDirtyAt);
            var key = $"{_creatureCat}|{q}|{_creatureSort}";
            if (_filteredCreaturesCache != null && key == _creatureFilterKey) return _filteredCreaturesCache;

            IEnumerable<CreatureEntry> src = _creatureIndex;
            if (_creatureCat == "Bosses") src = src.Where(e => e.Boss);
            else if (_creatureCat == "Tamable") src = src.Where(e => e.Tamable);
            else if (_creatureCat != "All") src = src.Where(e => e.Faction == _creatureCat);
            if (!string.IsNullOrEmpty(q))
                src = src.Where(e =>
                    e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            // Same relevance-then-chosen-sort ordering as the Items tab; see FilteredItems for the rationale.
            var ord = string.IsNullOrEmpty(q)
                ? src.OrderBy(_ => 0)
                : src.OrderBy(e => MatchRank(e.Display, e.Name, q)).ThenBy(e => e.Display.Length);

            switch (_creatureSort)
            {
                case 1: ord = ord.ThenByDescending(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                case 2: ord = ord.ThenBy(e => e.Faction).ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
                default: ord = ord.ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase); break;
            }
            src = ord;

            _filteredCreaturesCache = src.ToList();
            _creatureFilterKey = key;
            return _filteredCreaturesCache;
        }

        // Only the three fixed chips are ours to translate; everything after them is a faction name straight
        // from the game data (and, for modded creatures, from other mods), so it passes through untouched.
        private static string CreatureCatLabel(string cat)
        {
            switch (cat)
            {
                case "All": return Loc.T("common.all");
                case "Bosses": return Loc.T("cre.bosses");
                case "Tamable": return Loc.T("cre.tamable");
                default: return cat;
            }
        }

        private void DrawCreaturesTab()
        {
            EnsureLocalizedLabels();
            if (_creatureIndex == null) { GUILayout.Label(Loc.T("cre.db_not_loaded"), _labelStyle); return; }

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
                    var pressed = GUILayout.Toggle(_creatureCat == cats[i], CreatureCatLabel(cats[i]), _catStyle);
                    if (pressed && _creatureCat != cats[i]) { _creatureCat = cats[i]; _creatureScroll = Vector2.zero; }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("common.search"), _labelStyle, GUILayout.MinWidth(50));
            _creatureSearch = GUILayout.TextField(_creatureSearch, _textFieldStyle);
            GUILayout.Label(Loc.T("cre.count"), _labelStyle, GUILayout.MinWidth(45));
            var cStr = GUILayout.TextField(_creatureCount.ToString(), _textFieldStyle, GUILayout.Width(40));
            int.TryParse(cStr, out _creatureCount);
            GUILayout.Label(Loc.T("cre.stars"), _labelStyle, GUILayout.MinWidth(40));
            var lStr = GUILayout.TextField((_creatureLevel - 1).ToString(), _textFieldStyle, GUILayout.Width(30));
            if (int.TryParse(lStr, out var stars)) _creatureLevel = Mathf.Clamp(stars, 0, 10) + 1;
            _spawnAtCrosshair = GUILayout.Toggle(_spawnAtCrosshair, " " + Loc.T("cre.at_crosshair"), _toggleStyle);
            DropdownButton("creatureSort", Loc.T("common.sort"), _creatureSortLabels, _creatureSort, 150);
            GUILayout.EndHorizontal();
            DropdownOptions("creatureSort", _creatureSortLabels, ref _creatureSort, 150);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("cre.pet_name"), _labelStyle, GUILayout.MinWidth(60));
            _petName = GUILayout.TextField(_petName, _textFieldStyle, GUILayout.Width(120));
            // Multi-step since 2.3.0: the server keeps a per-admin stack and replies with how many steps remain,
            // so the local message only acknowledges the request.
            if (GUILayout.Button(Loc.T("cre.undo"), _buttonStyle, GUILayout.MinWidth(120)))
            { SrvRpc("AP_SrvUndo"); Message(Loc.T("cre.undo_requested")); }
            GUILayout.Label(Loc.T("cre.arena", _arenaA ?? "?", _arenaB ?? "?"), _labelStyle);
            if (GUILayout.Button(Loc.T("cre.fight"), _buttonStyle, GUILayout.MinWidth(60)) && _arenaA != null && _arenaB != null)
            {
                var center = SpawnPos(8f);
                SendServerSpawn(1, _arenaA, center + Vector3.left * 6f, _arenaCountA, 1, false);
                SendServerSpawn(1, _arenaB, center + Vector3.right * 6f, _arenaCountB, 1, false);
                Message(Loc.T("cre.arena_started", _arenaCountA, _arenaA, _arenaCountB, _arenaB));
            }
            GUILayout.EndHorizontal();

            // presets
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("cre.preset"), _labelStyle, GUILayout.MinWidth(45));
            _presetName = GUILayout.TextField(_presetName, _textFieldStyle, GUILayout.Width(110));
            foreach (var kv in PresetRows())
            {
                if (GUILayout.Button(kv.Key, _buttonStyle))
                {
                    foreach (var spawn in kv.Value.Split(';'))
                    {
                        var s = spawn.Split(':');
                        if (s.Length >= 3 && int.TryParse(s[1], out var n) && int.TryParse(s[2], out var lvl))
                            SendServerSpawn(1, s[0], SpawnPos(4f), n, lvl, false);
                    }
                    Message(Loc.T("cre.preset_spawned", kv.Key));
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
                GUILayout.Label(e.Display, _cellStyle, GUILayout.Width(180));
                GUILayout.Label(e.Name + (e.Boss ? "  " + Loc.T("cre.tag_boss") : "") + (e.Tamable ? "  " + Loc.T("cre.tag_tamable") : ""), _dimCellStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("A", _buttonStyle, GUILayout.MinWidth(26))) { _arenaA = e.Name; _arenaCountA = Math.Max(1, _creatureCount); }
                if (GUILayout.Button("B", _buttonStyle, GUILayout.MinWidth(26))) { _arenaB = e.Name; _arenaCountB = Math.Max(1, _creatureCount); }
                if (GUILayout.Button(Loc.T("cre.save"), _buttonStyle, GUILayout.MinWidth(50)) && !string.IsNullOrEmpty(_presetName))
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
                    Message(Loc.T("cre.preset_saved", _presetName));
                }
                if (GUILayout.Button(Loc.T("cre.spawn"), _buttonStyle, GUILayout.MinWidth(60)))
                { SendServerSpawn(1, e.Name, SpawnPos(), Math.Max(1, _creatureCount), _creatureLevel, false); Message(Loc.T("cre.msg_requested", e.Display)); }
                if (e.Tamable && GUILayout.Button(Loc.T("cre.tame"), _buttonStyle, GUILayout.MinWidth(55)))
                { SendServerSpawn(1, e.Name, SpawnPos(), Math.Max(1, _creatureCount), _creatureLevel, true, _petName); Message(Loc.T("cre.msg_requested_tamed", e.Display)); }
                GUILayout.EndHorizontal();
            }
            var below = total - (first + visible);
            if (below > 0) GUILayout.Space(below * rowH);
            GUILayout.EndScrollView();
            GUILayout.Label(Loc.T("cre.count_total", total), _dimLabelStyle);
        }

        // ==================== Bosses tab ====================
        private void DrawBossesTab()
        {
            DrawSection(Loc.T("boss.section"));
            GUILayout.Label(Loc.T("boss.hint"), _dimLabelStyle);
            // short fixed list — size to content so it doesn't balloon to fill a tall window (leaving a dead gap)
            _bossScroll = GUILayout.BeginScrollView(_bossScroll, GUILayout.Height(Mathf.Min(ListView(140f), BossList.Length * 28f + 8f)));
            foreach (var (prefabName, label, offerPrefab, offerCount) in BossList)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(label, _cellStyle, GUILayout.Width(130));
                GUILayout.Label(prefabName, _cellStyle, GUILayout.Width(120));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("boss.offering", offerCount, offerPrefab), _buttonStyle, GUILayout.MinWidth(230)))
                { SendServerGive(SelfUid(), offerPrefab, offerCount, 1); Message(Loc.T("items.msg_requested", offerCount, offerPrefab)); }
                if (GUILayout.Button(Loc.T("cre.spawn"), _buttonStyle, GUILayout.MinWidth(70)))
                { SendServerSpawn(1, prefabName, SpawnPos(6f), 1, 1, false); Message(Loc.T("boss.msg_requested", label)); }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            DrawSection(Loc.T("boss.raids"));
            GUILayout.Label(Loc.T("boss.raids_hint"), _dimLabelStyle);
            GUILayout.BeginHorizontal();
            var col = 0;
            foreach (var ev in RaidEvents)
            {
                if (GUILayout.Button(ev, _buttonStyle))
                {
                    SrvRpc("AP_SrvEvent", ev, LocalPlayer.transform.position);
                    Message(Loc.T("boss.msg_event", ev));
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

        // Toggle acknowledgements share one ON/OFF pair rather than baking the words into each message, so a
        // translator writes them once.
        private static string OnOff(bool on) => on ? Loc.T("common.on") : Loc.T("common.off");

        private void DrawPlayerTab()
        {
            var player = LocalPlayer;
            EnsureBaseStats();
            _playerScroll = GUILayout.BeginScrollView(_playerScroll, GUILayout.Height(ListView(100f)));

            DrawSection(Loc.T("player.toggles"));
            var god = GUILayout.Toggle(_god, " " + Loc.T("player.god"), _toggleStyle);
            if (god != _god) { _god = god; player.SetGodMode(_god); Message(Loc.T("player.msg_god", OnOff(_god))); }

            var ghost = GUILayout.Toggle(_ghost, " " + Loc.T("player.ghost"), _toggleStyle);
            if (ghost != _ghost) { _ghost = ghost; player.SetGhostMode(_ghost); Message(Loc.T("player.msg_ghost", OnOff(_ghost))); }

            var fly = GUILayout.Toggle(_fly, " " + Loc.T("player.fly"), _toggleStyle);
            if (fly != _fly)
            {
                _fly = fly;
                Player.m_debugMode = true;
                player.ToggleDebugFly();
                Message(_fly ? Loc.T("player.msg_fly_on") : Loc.T("player.msg_fly_off"));
            }

            var noCost = GUILayout.Toggle(_noCost, " " + Loc.T("player.free_build"), _toggleStyle);
            if (noCost != _noCost) { _noCost = noCost; player.SetNoPlacementCost(_noCost); Message(Loc.T("player.msg_free_build", OnOff(_noCost))); }

            var noStam = GUILayout.Toggle(_noStamina, " " + Loc.T("player.no_stamina"), _toggleStyle);
            if (noStam != _noStamina) { _noStamina = noStam; NoStaminaFlag = noStam; Message(Loc.T("player.msg_no_stamina", OnOff(noStam))); }

            var ohk = GUILayout.Toggle(_oneHitKill, " " + Loc.T("player.one_hit"), _toggleStyle);
            if (ohk != _oneHitKill) { _oneHitKill = ohk; OneHitKillFlag = ohk; Message(Loc.T("player.msg_one_hit", OnOff(ohk))); }

            var weight = GUILayout.Toggle(_infiniteWeight, " " + Loc.T("player.inf_weight"), _toggleStyle);
            if (weight != _infiniteWeight)
            {
                _infiniteWeight = weight;
                player.m_maxCarryWeight = weight ? 100000f : _baseWeight;
                Message(Loc.T("player.msg_inf_weight", OnOff(weight)));
            }

            DrawSection(Loc.T("player.multipliers"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("player.speed", _speedMult.ToString("0.0")), _labelStyle, GUILayout.MinWidth(90));
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
            GUILayout.Label(Loc.T("player.jump", _jumpMult.ToString("0.0")), _labelStyle, GUILayout.MinWidth(90));
            var newJump = GUILayout.HorizontalSlider(_jumpMult, 1f, 5f, GUILayout.Width(250));
            if (Math.Abs(newJump - _jumpMult) > 0.05f)
            {
                _jumpMult = newJump;
                player.m_jumpForce = _baseJump * _jumpMult;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("player.pickup", _pickupRange.ToString("0")), _labelStyle, GUILayout.MinWidth(90));
            var newPickup = GUILayout.HorizontalSlider(_pickupRange, 2f, 30f, GUILayout.Width(250));
            if (Math.Abs(newPickup - _pickupRange) > 0.3f)
            {
                _pickupRange = newPickup;
                player.m_autoPickupRange = _pickupRange;
            }
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("player.quick_actions"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("player.full_heal"), _buttonStyle)) { player.Heal(player.GetMaxHealth()); Message(Loc.T("player.msg_healed")); }
            if (GUILayout.Button(Loc.T("player.full_stamina"), _buttonStyle)) { player.AddStamina(player.GetMaxStamina()); Message(Loc.T("player.msg_stamina")); }
            if (GUILayout.Button(Loc.T("player.full_eitr"), _buttonStyle)) { player.AddEitr(player.GetMaxEitr()); Message(Loc.T("player.msg_eitr")); }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("player.repair_all"), _buttonStyle))
            {
                foreach (var item in player.GetInventory().GetAllItems())
                    item.m_durability = item.GetMaxDurability();
                Message(Loc.T("player.msg_repaired"));
            }
            if (GUILayout.Button(Loc.T("player.clear_se"), _buttonStyle))
            {
                player.GetSEMan().RemoveAllStatusEffects();
                Message(Loc.T("player.msg_se_cleared"));
            }
            if (GUILayout.Button(Loc.T("player.fix_broken"), _buttonStyle))
            {
                var broken = player.GetInventory().GetAllItems()
                    .Where(i => i.m_shared.m_icons == null || i.m_shared.m_icons.Length == 0)
                    .ToList();
                foreach (var item in broken)
                    player.GetInventory().RemoveItem(item);
                Message(Loc.T("player.msg_fixed_broken", broken.Count));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("player.tp_death"), _buttonStyle))
            {
                var parts = _lastDeathCfg.Value.Split(',');
                if (parts.Length == 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dy) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dz))
                {
                    player.TeleportTo(new Vector3(dx, dy + 0.5f, dz), player.transform.rotation, true);
                    Message(Loc.T("player.msg_tp_death"));
                }
                else Message(Loc.T("player.msg_no_death"));
            }
            GUILayout.Label(string.IsNullOrEmpty(_lastDeathCfg.Value)
                ? Loc.T("player.no_death_yet")
                : Loc.T("player.last_death", _lastDeathCfg.Value), _dimLabelStyle);
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("player.skills"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("player.skills_plus10"), _buttonStyle)) ChangeSkills(10);
            if (GUILayout.Button(Loc.T("player.skills_100"), _buttonStyle)) SetSkills(100);
            if (GUILayout.Button(Loc.T("player.skills_reset"), _buttonStyle)) SetSkills(0);
            GUILayout.EndHorizontal();

            _showSkills = GUILayout.Toggle(_showSkills, " " + Loc.T("player.show_skills"), _toggleStyle);
            if (_showSkills)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("player.apply_to"), _labelStyle, GUILayout.MinWidth(105));
                if (GUILayout.Button(SkillTargetLabel(), _buttonStyle, GUILayout.MinWidth(160))) CycleSkillTarget();
                GUILayout.Label(Loc.T("player.custom"), _labelStyle, GUILayout.MinWidth(55));
                _skillCustom = GUILayout.TextField(_skillCustom, _textFieldStyle, GUILayout.Width(60));
                GUILayout.EndHorizontal();
                if (_skillTargetId != 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("player.private_note"), _labelStyle, GUILayout.MinWidth(105));
                    _skillMsg = GUILayout.TextField(_skillMsg, _textFieldStyle, GUILayout.Width(280));
                    GUILayout.EndHorizontal();
                    GUILayout.Label(Loc.T("player.private_note_hint"), _dimLabelStyle);
                }
                var skills = player.GetSkills();
                _skillScroll = GUILayout.BeginScrollView(_skillScroll, GUILayout.Height(220));
                foreach (var type in AllSkillTypes)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(type.ToString(), _cellStyle, GUILayout.Width(130));
                    GUILayout.Label(Loc.T("player.level_short", skills.GetSkillLevel(type).ToString("0.#")), _headerStyle, GUILayout.MinWidth(60));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("−10", _buttonStyle, GUILayout.MinWidth(44))) RaiseSkill(type, -10);
                    if (GUILayout.Button("−1", _buttonStyle, GUILayout.MinWidth(36))) RaiseSkill(type, -1);
                    if (GUILayout.Button("+1", _buttonStyle, GUILayout.MinWidth(36))) RaiseSkill(type, 1);
                    if (GUILayout.Button("+10", _buttonStyle, GUILayout.MinWidth(44))) RaiseSkill(type, 10);
                    if (GUILayout.Button("+100", _buttonStyle, GUILayout.MinWidth(52))) RaiseSkill(type, 100);
                    if (GUILayout.Button(Loc.T("player.custom_pm"), _buttonStyle, GUILayout.MinWidth(70)))
                    {
                        // negative custom values lower the skill
                        if (float.TryParse(_skillCustom, NumberStyles.Float, CultureInfo.InvariantCulture, out var amt) && amt != 0)
                            RaiseSkill(type, amt);
                        else Message(Loc.T("player.msg_custom_hint"));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            DrawSection(Loc.T("player.status_effects"));
            _showStatusEffects = GUILayout.Toggle(_showStatusEffects, " " + Loc.T("player.show_se"), _toggleStyle);
            if (_showStatusEffects && ObjectDB.instance != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("common.search"), _labelStyle, GUILayout.MinWidth(50));
                _seSearch = GUILayout.TextField(_seSearch, _textFieldStyle, GUILayout.Width(200));
                GUILayout.Space(12);
                // Same Me → player → … cycle as the skill browser; effects run on the target's own client
                // via the server (AP_SrvApplySE), so remote targets need companion 2.3.0 on their side.
                GUILayout.Label(Loc.T("player.apply_to"), _labelStyle, GUILayout.MinWidth(70));
                if (GUILayout.Button(TargetLabel(ref _seTargetId), _buttonStyle, GUILayout.MinWidth(160)))
                    CycleTarget(ref _seTargetId);
                GUILayout.EndHorizontal();
                // The relay is fire-and-forget (same as skills): a target on companion <2.3.0 silently drops
                // AP_ApplySE and the routed RPC gives no failure signal — so say it up front instead.
                if (_seTargetId != 0)
                    GUILayout.Label(Loc.T("player.se_target_hint"), _dimLabelStyle);

                // Build the categorized, search-filtered list ONCE before the scroll view. It is cached and only
                // rebuilt when the search text changes, so Layout and Repaint iterate the identical list and emit an
                // identical control count (each entry = one Horizontal row; each new bucket = one header Label).
                var seList = FilteredStatusEffects();
                _seScroll = GUILayout.BeginScrollView(_seScroll, GUILayout.Height(200));
                if (seList.Count == 0)
                {
                    GUILayout.Label(Loc.T("player.no_se_match"), _dimLabelStyle);
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
                            GUILayout.Label(Loc.T(SeBucketKeys[entry.Bucket]), _headerStyle);
                        }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(entry.Display, _cellStyle, GUILayout.Width(180));
                        GUILayout.Label(entry.Tooltip, _dimCellStyle);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(Loc.T("player.apply"), _buttonStyle, GUILayout.MinWidth(60)))
                        {
                            if (_seTargetId == 0)
                            {
                                // Self: apply locally — works in single-player where no server relay exists.
                                player.GetSEMan().AddStatusEffect(entry.Hash, true);
                                Message(Loc.T("player.msg_applied", entry.Display));
                            }
                            else
                            {
                                SrvRpc("AP_SrvApplySE", _seTargetId, entry.Hash);
                                Message(Loc.T("player.msg_applied_to", entry.Display, TargetLabel(ref _seTargetId)));
                            }
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndScrollView();
        }

        private void RaiseSkill(Skills.SkillType type, float amount)
        {
            if (_skillTargetId == 0)
            {
                LocalPlayer.GetSkills().CheatRaiseSkill(type.ToString(), amount, false);
                Message(Loc.T("player.msg_skill_self", type, amount.ToString("0.#")));
            }
            else
            {
                var pkg = new ZPackage();
                pkg.Write(_skillTargetId);
                pkg.Write(type.ToString());
                pkg.Write(amount);
                pkg.Write(_skillMsg ?? "");
                SrvRpc("AP_SrvSkillRaise", pkg);
                Message(Loc.T("player.msg_skill_target", type, amount.ToString("0.#"), SkillTargetLabel()));
            }
        }

        // Shared target-picker helpers: the skill browser and the status-effect browser both aim at
        // "Me or any online player", stored as a stable peer id (0 = self) so the pick survives roster
        // churn. A cycle button sidesteps the dropdown's Layout/Repaint control-count bookkeeping for a
        // list that changes as players join/leave.
        private string TargetLabel(ref long targetId)
        {
            if (targetId == 0) return Loc.T("common.me");
            foreach (var p in OtherPlayers())
                if (PeerIdOf(p) == targetId) return p.m_name;
            targetId = 0;   // target left the game — snap back to self
            return Loc.T("common.me");
        }

        private void CycleTarget(ref long targetId)
        {
            var others = OtherPlayers();
            if (others.Count == 0) { targetId = 0; return; }
            if (targetId == 0) { targetId = PeerIdOf(others[0]); return; }
            for (var i = 0; i < others.Count; i++)
                if (PeerIdOf(others[i]) == targetId)
                { targetId = i + 1 < others.Count ? PeerIdOf(others[i + 1]) : 0; return; }
            targetId = 0;
        }

        private string SkillTargetLabel() => TargetLabel(ref _skillTargetId);
        private void CycleSkillTarget() => CycleTarget(ref _skillTargetId);

        private void ChangeSkills(float delta)
        {
            foreach (Skills.SkillType type in Enum.GetValues(typeof(Skills.SkillType)))
            {
                if (type == Skills.SkillType.None || type == Skills.SkillType.All) continue;
                LocalPlayer.GetSkills().CheatRaiseSkill(type.ToString(), delta);
            }
            Message(Loc.T("player.msg_skills_changed", delta));
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
            Message(Loc.T("player.msg_skills_set", value));
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

            DrawSection(Loc.T("world.time_weather"));
            GUILayout.BeginHorizontal();
            _timeSlider = GUILayout.HorizontalSlider(_timeSlider, 0f, 1f, GUILayout.Width(280));
            GUILayout.Label(TimeLabel(_timeSlider), _labelStyle, GUILayout.MinWidth(50));
            if (GUILayout.Button(Loc.T("world.set"), _buttonStyle, GUILayout.MinWidth(50)) && env != null)
            {
                env.m_debugTimeOfDay = true;
                env.m_debugTime = _timeSlider;
                _timeLocked = true;
            }
            if (_timeLocked && GUILayout.Button(Loc.T("world.release"), _buttonStyle, GUILayout.MinWidth(70)) && env != null)
            {
                env.m_debugTimeOfDay = false;
                _timeLocked = false;
            }
            if (GUILayout.Button(Loc.T("world.skip_night"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                // World time is SERVER-owned — the old local debug-flag flip changed nothing. The companion
                // runs the game's own sleep-skip (EnvMan.SkipToMorning) server-side. Needs companion 2.2.9.
                SrvRpc("AP_SrvSkipNight");
                Message(Loc.T("world.msg_skip_night"));
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("world.weather_hint"), _labelStyle);
            GUILayout.BeginHorizontal();
            _weather = GUILayout.TextField(_weather, _textFieldStyle, GUILayout.Width(200));
            if (GUILayout.Button(Loc.T("player.apply"), _buttonStyle, GUILayout.MinWidth(70)) && env != null)
            {
                env.m_debugEnv = _weather;
                Message(string.IsNullOrEmpty(_weather) ? Loc.T("world.msg_weather_reset") : Loc.T("world.msg_weather_forced", _weather));
            }
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("world.wind"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.wind_dir", _windAngle.ToString("0")), _labelStyle, GUILayout.MinWidth(70));
            _windAngle = GUILayout.HorizontalSlider(_windAngle, 0f, 360f, GUILayout.Width(160));
            GUILayout.Label(Loc.T("world.wind_str", _windIntensity.ToString("0.0")), _labelStyle, GUILayout.MinWidth(60));
            _windIntensity = GUILayout.HorizontalSlider(_windIntensity, 0f, 1f, GUILayout.Width(120));
            if (GUILayout.Button(Loc.T("world.set"), _buttonStyle, GUILayout.MinWidth(45)) && env != null)
            { env.SetDebugWind(_windAngle, _windIntensity); _windLocked = true; Message(Loc.T("world.msg_wind_set")); }
            if (_windLocked && GUILayout.Button(Loc.T("world.reset"), _buttonStyle, GUILayout.MinWidth(55)) && env != null)
            { env.ResetDebugWind(); _windLocked = false; Message(Loc.T("world.msg_wind_reset")); }
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("world.teleport"));
            GUILayout.Label(Loc.T("world.map_hint", _mapTpKey.Value), _dimLabelStyle);

            // teleport straight to an online player
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.to_player"), _labelStyle, GUILayout.MinWidth(80));
            var tpOthers = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
            if (tpOthers.Count == 0) GUILayout.Label(Loc.T("world.no_others"), _dimLabelStyle);
            else foreach (var p in tpOthers)
                if (GUILayout.Button(p.m_name, _buttonStyle))
                {
                    LocalPlayer.TeleportTo(p.m_position + Vector3.up, LocalPlayer.transform.rotation, true);
                    Message(Loc.T("world.msg_tp_to", p.m_name));
                }
            GUILayout.EndHorizontal();

            // quick jump to known world locations (spawn + boss altars)
            if (ZoneSystem.instance != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("world.bosses"), _labelStyle, GUILayout.MinWidth(80));
                var any = false;
                foreach (var (label, loc) in QuickJumps)
                {
                    if (!ZoneSystem.instance.GetLocationIcon(loc, out var lp)) continue;
                    any = true;
                    if (GUILayout.Button(label, _buttonStyle)) TeleportToWorld(lp, label);
                }
                if (!any) GUILayout.Label(Loc.T("world.no_altars"), _dimLabelStyle);
                GUILayout.EndHorizontal();
            }

            var pos = LocalPlayer.transform.position;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.position"), _labelStyle, GUILayout.MinWidth(80));
            GUILayout.Label(Loc.T("world.now_at", pos.x.ToString("0"), pos.y.ToString("0"), pos.z.ToString("0")), _dimLabelStyle, GUILayout.MinWidth(140));
            GUILayout.Label("X:", _labelStyle, GUILayout.MinWidth(18));
            _tpX = GUILayout.TextField(_tpX, _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label("Y:", _labelStyle, GUILayout.MinWidth(18));
            _tpY = GUILayout.TextField(_tpY, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label("Z:", _labelStyle, GUILayout.MinWidth(18));
            _tpZ = GUILayout.TextField(_tpZ, _textFieldStyle, GUILayout.Width(60));
            if (GUILayout.Button(Loc.T("world.go"), _buttonStyle, GUILayout.MinWidth(40)) &&
                float.TryParse(_tpX, out var x) && float.TryParse(_tpY, out var y) && float.TryParse(_tpZ, out var z))
            {
                LocalPlayer.TeleportTo(new Vector3(x, y <= 0 ? 200 : y, z), LocalPlayer.transform.rotation, true);
                Message(Loc.T("world.msg_teleporting"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.bookmark"), _labelStyle, GUILayout.MinWidth(80));
            _bookmarkName = GUILayout.TextField(_bookmarkName, _textFieldStyle, GUILayout.Width(110));
            if (GUILayout.Button(Loc.T("world.save_here"), _buttonStyle, GUILayout.MinWidth(80)) && !string.IsNullOrEmpty(_bookmarkName))
            {
                var marks = ParseKv(_bookmarksCfg.Value);
                // Format coordinates with InvariantCulture so the decimal point is always '.', never a ','
                // that would collide with the ',' field delimiter on comma-decimal locales (de-DE/fr-FR).
                marks[_bookmarkName] = string.Format(CultureInfo.InvariantCulture, "{0:0.#},{1:0.#},{2:0.#}", pos.x, pos.y, pos.z);
                _bookmarksCfg.Value = JoinKv(marks);
                Config.Save();
                Message(Loc.T("world.msg_bookmark_saved", _bookmarkName));
            }
            GUILayout.EndHorizontal();
            foreach (var kv in BookmarkRows())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Key, _cellStyle, GUILayout.Width(120));
                GUILayout.Label(kv.Value, _cellStyle, GUILayout.Width(160));
                if (GUILayout.Button(Loc.T("world.go"), _buttonStyle, GUILayout.MinWidth(40)))
                {
                    var parts = kv.Value.Split(',');
                    if (parts.Length == 3 &&
                        float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bx) &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var by) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bz))
                    {
                        LocalPlayer.TeleportTo(new Vector3(bx, by, bz), LocalPlayer.transform.rotation, true);
                        Message(Loc.T("world.msg_tp_to", kv.Key));
                    }
                }
                if (GUILayout.Button(Loc.T("world.del"), _buttonStyle, GUILayout.MinWidth(40)))
                {
                    // Re-parse from config rather than mutating the cached dictionary: the row list for this
                    // frame is already snapshotted, so the deletion simply lands on the next Layout pass.
                    var marks = ParseKv(_bookmarksCfg.Value);
                    marks.Remove(kv.Key);
                    _bookmarksCfg.Value = JoinKv(marks);
                    Config.Save();
                }
                GUILayout.EndHorizontal();
            }

            DrawSection(Loc.T("world.area_actions"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.combat"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(Loc.T("world.kill_50"), _buttonStyle)) KillNearby(50f, false);
            // World-wide and unrecoverable — the only one here that can wipe creatures the admin can't even see.
            if (ConfirmButton("killAll", Loc.T("world.kill_all"))) KillNearby(100000f, false);
            if (GUILayout.Button(Loc.T("world.tame_30"), _buttonStyle)) TameNearby(30f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.cleanup"), _labelStyle, GUILayout.MinWidth(80));
            // Both destroy player property (dropped loot, planted/grown trees) with no undo.
            if (ConfirmButton("cleanupDrops", Loc.T("world.ground_items"))) CleanupDrops(50f);
            if (GUILayout.Button(Loc.T("world.repair_builds"), _buttonStyle)) RepairBuilds(50f);
            if (ConfirmButton("clearTrees", Loc.T("world.clear_trees"))) ClearTrees(20f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("world.world"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(Loc.T("world.explore_map"), _buttonStyle)) { Minimap.instance?.ExploreAll(); Message(Loc.T("world.msg_map_explored")); }
            if (GUILayout.Button(Loc.T("world.ping_pos"), _buttonStyle)) { Chat.instance?.SendPing(LocalPlayer.transform.position); Message(Loc.T("world.msg_pinged")); }
            var peaceful = GUILayout.Toggle(_peaceful, " " + Loc.T("world.peaceful"), _toggleStyle);
            if (peaceful != _peaceful)
            {
                _peaceful = peaceful;
                SrvRpc("AP_SrvPeaceful", _peaceful);
                Message(Loc.T("world.msg_peaceful", OnOff(_peaceful)));
            }
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("world.global_keys"));
            GUILayout.Label(Loc.T("world.global_keys_hint"), _dimLabelStyle);
            if (ZoneSystem.instance != null)
            {
                foreach (var key in _globalKeysLayout ?? (IEnumerable<string>)ZoneSystem.instance.GetGlobalKeys())
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(key, _cellStyle, GUILayout.Width(300));
                    if (GUILayout.Button(Loc.T("world.remove"), _buttonStyle, GUILayout.MinWidth(70)))
                    { ZoneSystem.instance.RemoveGlobalKey(key); Message(Loc.T("world.msg_key_removed", key)); }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                _newGlobalKey = GUILayout.TextField(_newGlobalKey, _textFieldStyle, GUILayout.Width(220));
                if (GUILayout.Button(Loc.T("world.add_key"), _buttonStyle, GUILayout.MinWidth(70)) && !string.IsNullOrEmpty(_newGlobalKey))
                { ZoneSystem.instance.SetGlobalKey(_newGlobalKey); Message(Loc.T("world.msg_key_added", _newGlobalKey)); _newGlobalKey = ""; }
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
            Message(Loc.T("world.msg_killed", killed));
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
            Message(Loc.T("world.msg_tamed", tamed));
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
            Message(Loc.T("world.msg_drops_removed", removed));
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
            Message(Loc.T("world.msg_repaired", repaired));
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
            Message(Loc.T("world.msg_trees_removed", removed));
        }

        // ==================== Players tab ====================
        private void DrawPlayersTab()
        {
            if (ZNet.instance == null) { GUILayout.Label(Loc.T("players.not_connected"), _labelStyle); return; }

            DrawSection(Loc.T("players.broadcast_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("players.broadcast"), _labelStyle, GUILayout.MinWidth(70));
            _broadcastText = GUILayout.TextField(_broadcastText, _textFieldStyle);
            if (GUILayout.Button(Loc.T("players.send_all"), _buttonStyle, GUILayout.MinWidth(90)) && !string.IsNullOrEmpty(_broadcastText))
            {
                SrvRpc("AP_SrvBroadcast", _broadcastText);
                Message(Loc.T("players.msg_broadcast_sent"));
                _broadcastText = "";
            }
            if (GUILayout.Button(Loc.T("players.summon_all"), _buttonStyle, GUILayout.MinWidth(95)))
            {
                foreach (var p in OtherPlayers()) SummonPlayer(p);
                Message(Loc.T("players.msg_summon_all"));
            }
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("players.connected"));
            _playersScroll = GUILayout.BeginScrollView(_playersScroll, GUILayout.Height(Mathf.Min(250f, ListView(330f))));
            foreach (var info in ZNet.instance.GetPlayerList())
            {
                var isSelf = LocalPlayer != null && info.m_name == LocalPlayer.GetPlayerName();
                GUILayout.BeginHorizontal();
                GUILayout.Label(info.m_name + (isSelf ? " " + Loc.T("players.you_suffix") : ""), _cellStyle, GUILayout.Width(140));
                GUILayout.Label($"({info.m_position.x:0}, {info.m_position.z:0})", _cellStyle, GUILayout.Width(100));
                if (!isSelf)
                {
                    if (GUILayout.Button(Loc.T("players.tp_to"), _buttonStyle, GUILayout.MinWidth(50)))
                    { LocalPlayer.TeleportTo(info.m_position + Vector3.up, LocalPlayer.transform.rotation, true); Message(Loc.T("world.msg_tp_to", info.m_name)); }
                    if (GUILayout.Button(Loc.T("players.summon"), _buttonStyle, GUILayout.MinWidth(65))) SummonPlayer(info);
                    if (GUILayout.Button(Loc.T("players.watch"), _buttonStyle, GUILayout.MinWidth(55)))
                    {
                        if (!_ghost) { _ghost = true; LocalPlayer.SetGhostMode(true); }
                        if (!_fly) { _fly = true; Player.m_debugMode = true; LocalPlayer.ToggleDebugFly(); }
                        LocalPlayer.TeleportTo(info.m_position + Vector3.up * 8f, LocalPlayer.transform.rotation, true);
                        Message(Loc.T("players.msg_watching", info.m_name));
                    }
                    if (GUILayout.Button(Loc.T("players.heal"), _buttonStyle, GUILayout.MinWidth(45)))
                    { SrvRpc("AP_SrvHeal", PeerIdOf(info)); Message(Loc.T("players.msg_healing", info.m_name)); }
                    if (GUILayout.Button(Loc.T("players.map"), _buttonStyle, GUILayout.MinWidth(45)))
                    { Chat.instance?.SendPing(info.m_position); Message(Loc.T("players.msg_pinged", info.m_name)); }
                    if (GUILayout.Button("⚡", _buttonStyle, GUILayout.MinWidth(30)))
                    { SendServerSpawn(1, "lightning", info.m_position, 1, 1, false); Message(Loc.T("players.msg_lightning", info.m_name)); }
                    if (GUILayout.Button(Loc.T("players.inventory"), _buttonStyle, GUILayout.MinWidth(75)))
                    {
                        _inspectPlayerName = info.m_name;
                        _inspectTargetId = PeerIdOf(info);
                        _inspectInventory = null;
                        _inspectPending = true;
                        _inspectRequestTime = Time.time;
                        SrvRpc("AP_SrvReqInv", PeerIdOf(info));
                    }
                    if (GUILayout.Button(Loc.T("players.kick"), _buttonStyle, GUILayout.MinWidth(45)))
                    { SrvRpc("AP_SrvKick", PeerIdOf(info)); Message(Loc.T("players.msg_kicked", info.m_name)); }
                    // Per-player id: arming Ban on one row must not arm it on another.
                    if (ConfirmButton("ban:" + PeerIdOf(info), Loc.T("players.ban"), GUILayout.MinWidth(42)))
                    { SrvRpc("AP_SrvBan", PeerIdOf(info)); Message(Loc.T("players.msg_banned", info.m_name)); }
                }
                else if (GUILayout.Button(Loc.T("players.inventory"), _buttonStyle, GUILayout.MinWidth(75)))
                {
                    _inspectPlayerName = info.m_name;
                    _inspectTargetId = PeerIdOf(info);
                    _inspectInventory = null;
                    _inspectPending = true;
                    _inspectRequestTime = Time.time;
                    SrvRpc("AP_SrvReqInv", PeerIdOf(info));
                }
                GUILayout.EndHorizontal();

                // note line
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUILayout.Label(Loc.T("players.note"), _labelStyle, GUILayout.MinWidth(40));
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

            DrawSection(Loc.T("players.inv_viewer"));
            GUILayout.Label(_inspectPlayerName ?? Loc.T("players.pick_player"), _labelStyle);
            if (_inspectPending)
            {
                GUILayout.Label(Time.time - _inspectRequestTime > 5f
                    ? Loc.T("players.no_response")
                    : Loc.T("players.waiting"), _labelStyle);
            }
            else if (_inspectInventory != null)
            {
                _inspectScroll = GUILayout.BeginScrollView(_inspectScroll, GUILayout.Height(140));
                foreach (var (name, stack, quality) in _inspectInventory)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(name, _cellStyle, GUILayout.Width(250));
                    GUILayout.Label($"x{stack}", _labelStyle, GUILayout.MinWidth(60));
                    GUILayout.Label($"q{quality}", _labelStyle, GUILayout.MinWidth(40));
                    GUILayout.FlexibleSpace();
                    // Removal is executed on the target's client via the server (admin-validated); the
                    // target pushes its refreshed inventory back, so the list updates itself.
                    if (GUILayout.Button(Loc.T("players.remove_1"), _buttonStyle, GUILayout.MinWidth(75))) RemoveFromInspected(name, 1);
                    if (GUILayout.Button(Loc.T("players.remove_all"), _buttonStyle, GUILayout.MinWidth(85))) RemoveFromInspected(name, stack);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                GUILayout.Label(Loc.T("players.remove_hint"), _dimLabelStyle);
            }
        }

        private void RemoveFromInspected(string itemName, int amount)
        {
            if (_inspectTargetId == 0 || amount <= 0) return;
            var pkg = new ZPackage();
            pkg.Write(_inspectTargetId);
            pkg.Write(itemName);
            pkg.Write(amount);
            SrvRpc("AP_SrvInvRemove", pkg);
            Message(Loc.T("players.msg_removing", amount, itemName, _inspectPlayerName));
        }

        private void SummonPlayer(ZNet.PlayerInfo info)
        {
            var pkg = new ZPackage();
            pkg.Write(PeerIdOf(info));
            pkg.Write(LocalPlayer.transform.position + LocalPlayer.transform.forward * 2f);
            SrvRpc("AP_SrvTeleport", pkg);
            Message(Loc.T("players.msg_summoning", info.m_name));
        }

        // ==================== Server tab ====================
        // OnGUI runs at least twice per frame (Layout + Repaint), so anything built inline in a draw method is
        // built twice a frame forever. These stat lines involve reflection and string formatting, so they are
        // rebuilt on a timer and merely rendered from cache in between.
        private float _nextStatsRefresh;
        private string _statsMain = "";
        private string _statsLoad = "";
        private string _statsHost = "";

        /// <summary>
        /// Count a private Valheim collection by reflection, trying each candidate field name in order.
        /// Valheim renames internals between patches and this tab is cosmetic, so a miss degrades to null
        /// (rendered as "n/a") instead of breaking the tab — or the build — on the next game update.
        /// </summary>
        private static int? ReflectCount(object instance, params string[] fieldNames)
        {
            if (instance == null) return null;
            foreach (var name in fieldNames)
            {
                try
                {
                    var f = AccessTools.Field(instance.GetType(), name);
                    // Non-generic ICollection covers both Dictionary<,> and List<> without knowing the type args.
                    if (f?.GetValue(instance) is System.Collections.ICollection c) return c.Count;
                }
                catch { /* try the next candidate */ }
            }
            return null;
        }

        private static string OrNa(int? v) => v.HasValue ? v.Value.ToString() : "n/a";

        private void RefreshServerStats()
        {
            var day = EnvMan.instance != null && ZNet.instance != null
                ? EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds()) : 0;
            var players = ZNet.instance != null ? ZNet.instance.GetPlayerList().Count : 0;
            var chars = Character.GetAllCharacters().Count;
            var fps = Mathf.RoundToInt(1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
            _statsMain = Loc.T("srv.stats_line", day, players, chars, fps);

            // World-load indicators. On a dedicated server these are the *client's* loaded set, not the whole
            // world — labelled accordingly so nobody reads them as server-wide totals.
            var zdos = ReflectCount(ZDOMan.instance, "m_objectsByID", "m_objectsByID_", "m_objects");
            var zones = ReflectCount(ZoneSystem.instance, "m_zones", "m_loadedZones");
            var session = TimeSpan.FromSeconds(Time.realtimeSinceStartup);
            _statsLoad = Loc.T("srv.load_line", OrNa(zdos), OrNa(zones),
                               $"{(int)session.TotalHours:00}:{session.Minutes:00}");

            var serverPeer = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            _statsHost = serverPeer?.m_socket != null ? Loc.T("srv.host", serverPeer.m_socket.GetHostName()) : "";
        }

        private void DrawServerTab()
        {
            _serverScroll = GUILayout.BeginScrollView(_serverScroll, GUILayout.Height(ListView(100f)));

            // 4x/sec is well under the ~60 rebuilds/sec the old inline version did, and still reads as "live".
            if (Time.realtimeSinceStartup >= _nextStatsRefresh)
            {
                _nextStatsRefresh = Time.realtimeSinceStartup + 0.25f;
                RefreshServerStats();
            }

            DrawSection(Loc.T("srv.live_stats"));
            GUILayout.Label(_statsMain, _labelStyle);
            GUILayout.Label(_statsLoad, _labelStyle);
            if (_statsHost.Length > 0) GUILayout.Label(_statsHost, _labelStyle);
            GUILayout.Label(Loc.T("srv.versions", PluginVersion, _srvCompVersion ?? Loc.T("srv.no_reply")), _labelStyle);

            DrawSection(Loc.T("srv.unban_title"));
            GUILayout.BeginHorizontal();
            _unbanId = GUILayout.TextField(_unbanId, _textFieldStyle, GUILayout.Width(220));
            if (GUILayout.Button(Loc.T("srv.unban"), _buttonStyle, GUILayout.MinWidth(70)) && !string.IsNullOrEmpty(_unbanId))
            {
                SrvRpc("AP_SrvUnban", _unbanId);
                Message(Loc.T("srv.unban_requested", _unbanId));
                _unbanId = "";
            }
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("srv.joinlog_title"));
            if (_joinLog.Count == 0) GUILayout.Label(Loc.T("common.nothing_yet"), _labelStyle);
            // Indexed loop rather than .Take(40): LINQ allocates an iterator on every one of the two OnGUI
            // passes per frame, for a list that is usually short anyway.
            var shown = Math.Min(_joinLog.Count, 40);
            for (var i = 0; i < shown; i++)
                GUILayout.Label(_joinLog[i], _labelStyle);

            GUILayout.EndScrollView();
        }

        private string _unbanId = "";

        // ==================== Side window (What's New / Bug Report) ====================
        private void DrawSideWindow(int id)
        {
            // Snapshot mutable state on Layout so mid-frame changes (mode switch, async status arriving,
            // cooldown expiring) can't desync IMGUI control counts (same rule as _openDropdownLayout).
            if (Event.current.type == EventType.Layout)
            {
                _sideModeLayout = _sideMode;
                _bugStatusLayout = _bugStatus;
                _bugCoolLayout = Time.time < _nextBugSend;
            }
            var mode = _sideModeLayout;

            GUILayout.Space(12);   // breathing room under the window title
            GUILayout.BeginHorizontal();
            // Switch only when a toggle flips off→on. Comparing against _sideMode instead would let the
            // still-on old-mode toggle (which just returns its input) immediately switch the mode back —
            // that made "What's New" unclickable from Bug Report.
            var wnOn = mode == SideMode.WhatsNew;
            var brOn = mode == SideMode.BugReport;
            if (GUILayout.Toggle(wnOn, Loc.T("side.whats_new"), _catStyle) && !wnOn) _sideMode = SideMode.WhatsNew;
            if (GUILayout.Toggle(brOn, Loc.T("side.bug_report"), _catStyle) && !brOn) _sideMode = SideMode.BugReport;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("✕", _buttonStyle, GUILayout.MinWidth(30))) _sideMode = SideMode.None;
            GUILayout.EndHorizontal();

            if (mode == SideMode.WhatsNew)
            {
                DrawSection(Loc.T("side.version", PluginVersion));
                _sideScroll = GUILayout.BeginScrollView(_sideScroll);
                GUILayout.Label(WhatsNewText, _labelStyle);
                GUILayout.EndScrollView();
                GUILayout.Label(Loc.T("side.whats_new_hint"), _dimLabelStyle);
            }
            else if (mode == SideMode.BugReport)
            {
                DrawSection(Loc.T("side.describe"));
                _bugText = GUILayout.TextArea(_bugText, _textAreaStyle,
                    GUILayout.MinHeight(120f), GUILayout.MaxHeight(170f), GUILayout.ExpandHeight(false));
                _bugAttachShot = GUILayout.Toggle(_bugAttachShot, " " + Loc.T("side.attach"), _toggleStyle);
                GUILayout.Space(4);

                var webhookSet = !string.IsNullOrEmpty(_bugWebhookCfg.Value);
                var coolingDown = _bugCoolLayout;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_bugSending ? Loc.T("side.sending") : Loc.T("side.send"), _buttonStyle)
                    && !_bugSending && webhookSet && !coolingDown)
                {
                    if (_bugText.Trim().Length < 10)
                        _bugStatus = Loc.T("side.describe_first");
                    else
                        StartCoroutine(SendBugReport());
                }
                if (GUILayout.Button(Loc.T("side.discord"), _buttonStyle)) Application.OpenURL(DiscordInvite);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                DrawCoffeeButton(200f);
                GUILayout.EndHorizontal();

                if (!webhookSet)
                    GUILayout.Label(Loc.T("side.no_webhook"), _dimLabelStyle);
                else if (coolingDown)
                    GUILayout.Label(Loc.T("side.cooldown", Mathf.Max(1, Mathf.CeilToInt(_nextBugSend - Time.time))), _dimLabelStyle);
                if (_bugStatusLayout != null) GUILayout.Label(_bugStatusLayout, _dimLabelStyle);

                GUILayout.FlexibleSpace();
                GUILayout.Label(Loc.T("side.privacy"), _dimLabelStyle);
            }
        }

        // Capture (optionally) a screenshot with the panel hidden for exactly one frame, then hand the
        // HTTP POST to a worker thread — the OnGUI/game loop never blocks on Discord.
        private System.Collections.IEnumerator SendBugReport()
        {
            _bugSending = true;
            _bugStatus = null;

            byte[] jpg = null;
            if (_bugAttachShot)
            {
                // Capture at end-of-frame, panel INCLUDED — most reports are about the panel itself,
                // so hiding it (as an earlier build did) removed exactly the thing being reported.
                yield return new WaitForEndOfFrame();
                try
                {
                    var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                    tex.Apply();
                    jpg = EncodeJpg(tex);
                    Destroy(tex);
                }
                catch (Exception e) { Logger.LogWarning($"Bug-report screenshot failed (sending without it): {e.Message}"); }
            }

            var report =
                $"**Bug report** — Advanced Admin Panel {PluginVersion}\n" +
                $"Game: {GameVersionString()}   ·   Mode: {SessionModeString()}\n" +
                $"Reporter: {(LocalPlayer != null ? LocalPlayer.GetPlayerName() : "(not in game)")}\n\n" +
                _bugText.Trim();

            // Forum-post title: version + reporter + the first words of the description.
            var firstLine = _bugText.Trim().Split('\n')[0];
            var title = $"[{PluginVersion}] {(LocalPlayer != null ? LocalPlayer.GetPlayerName() : "unknown")}: {firstLine}";
            if (title.Length > 95) title = title.Substring(0, 95) + "…";   // Discord caps thread names at 100

            var url = _bugWebhookCfg.Value;
            var task = System.Threading.Tasks.Task.Run(() => PostToWebhook(url, title, report, jpg));
            while (!task.IsCompleted) yield return null;

            _bugStatus = task.Result;
            if (task.Result != null && task.Result.StartsWith("Sent", StringComparison.Ordinal))
            {
                _bugText = "";
                _nextBugSend = Time.time + 60f;   // one report per minute per client
            }
            _bugSending = false;
        }

        // Runs on a worker thread — must not touch Unity APIs. Discord webhook: multipart form with a
        // payload_json part (content capped at 2000 chars by Discord) plus an optional image attachment.
        // thread_name makes each report its own post in the target FORUM channel (required there —
        // Discord rejects forum webhook posts without it).
        private static string PostToWebhook(string url, string threadName, string content, byte[] jpg)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var http = new System.Net.Http.HttpClient())
                using (var form = new System.Net.Http.MultipartFormDataContent())
                {
                    if (content.Length > 1900) content = content.Substring(0, 1900) + "…";
                    form.Add(new System.Net.Http.StringContent(
                        "{\"content\":" + JsonString(content) + ",\"thread_name\":" + JsonString(threadName) + "}",
                        Encoding.UTF8, "application/json"), "payload_json");
                    if (jpg != null)
                    {
                        var file = new System.Net.Http.ByteArrayContent(jpg);
                        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                        form.Add(file, "files[0]", "screenshot.jpg");
                    }
                    var resp = http.PostAsync(url, form).GetAwaiter().GetResult();
                    return resp.IsSuccessStatusCode
                        ? Loc.T("side.sent_thanks")
                        : Loc.T("side.rejected", (int)resp.StatusCode);
                }
            }
            catch (Exception e) { return Loc.T("side.send_failed", e.Message); }
        }

        private static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            return sb.Append('"').ToString();
        }

        // ImageConversion targets netstandard 2.1 and can't be compile-referenced from net48 —
        // resolve EncodeToJPG at runtime, exactly like the logo loader resolves LoadImage.
        private static byte[] EncodeJpg(Texture2D tex)
        {
            var m = AccessTools.Method(
                AccessTools.TypeByName("UnityEngine.ImageConversion"), "EncodeToJPG",
                new[] { typeof(Texture2D), typeof(int) });
            return m != null ? (byte[])m.Invoke(null, new object[] { tex, 85 }) : null;
        }

        private static string GameVersionString()
        {
            try
            {
                var m = AccessTools.Method(AccessTools.TypeByName("Version"), "GetVersionString", Type.EmptyTypes);
                return m?.Invoke(null, null)?.ToString() ?? Application.version;
            }
            catch { return "unknown"; }
        }

        private static string SessionModeString() =>
            ZNet.instance == null ? "menu"
            : ZNet.instance.IsServer() ? "host / single-player"
            : "client on dedicated server";

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
            if (_textAreaStyle != null) _textAreaStyle.fontSize = _textFieldStyle.fontSize;
            _headerStyle.fontSize = s + 1;
            _windowStyle.fontSize = Mathf.Min(s + 2, 18);
            // The tab bar and category chips must stay one row wide even at the largest base sizes
            // (the row can't shrink below its text and would push the last tabs off-window), so cap them.
            _tabStyle.fontSize = Mathf.Clamp(s + 1, 12, 15);
            _catStyle.fontSize = Mathf.Clamp(s - 1, 10, 14);
            _dimLabelStyle.fontSize = Mathf.Max(s - 1, 10);
            if (_cellStyle != null) _cellStyle.fontSize = s;
            if (_dimCellStyle != null) _dimCellStyle.fontSize = Mathf.Max(s - 1, 10);
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

            DrawSection(Loc.T("set.appearance"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("set.font"), _labelStyle, GUILayout.MinWidth(90));
            foreach (var choice in FontChoices)
            {
                var on = _fontChoiceCfg.Value == choice;
                if (GUILayout.Toggle(on, choice, _catStyle) && !on) SelectFont(choice);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("set.font_hint"), _dimLabelStyle);
            // Explain the override rather than letting the panel silently ignore the chosen font.
            if (Loc.NeedsFallbackFont && _fontChoiceCfg.Value != "Default")
                GUILayout.Label(Loc.T("set.font_fallback_note"), _dimLabelStyle);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("set.font_size", _fontSizeLive), _labelStyle, GUILayout.MinWidth(120));
            var newSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(_fontSizeLive, 10f, 20f, GUILayout.Width(220)));
            if (GUILayout.Button("−", _buttonStyle, GUILayout.MinWidth(30))) newSize = Mathf.Max(10, _fontSizeLive - 1);
            if (GUILayout.Button("+", _buttonStyle, GUILayout.MinWidth(30))) newSize = Mathf.Min(20, _fontSizeLive + 1);
            GUILayout.EndHorizontal();
            if (newSize != _fontSizeLive)
            {
                _fontSizeLive = newSize;
                ApplyFontSizes();   // live preview; committed to disk on mouse-release (CommitUiSettings)
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("set.opacity", _panelAlphaLive), _labelStyle, GUILayout.MinWidth(120));
            var newAlpha = Mathf.RoundToInt(GUILayout.HorizontalSlider(_panelAlphaLive, 55f, 100f, GUILayout.Width(220)));
            GUILayout.EndHorizontal();
            if (newAlpha != _panelAlphaLive)
            {
                _panelAlphaLive = newAlpha;
                ApplyPanelAlpha();
            }

            GUILayout.Space(6);
            var logo = GUILayout.Toggle(_showLogoCfg.Value, " " + Loc.T("set.show_logo"), _toggleStyle);
            if (logo != _showLogoCfg.Value) { _showLogoCfg.Value = logo; Config.Save(); }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("set.language"), _labelStyle, GUILayout.MinWidth(90));
            var langIdx = Mathf.Max(0, Array.IndexOf(Loc.MenuNames, _languageCfg.Value));
            DropdownButton("language", "", Loc.MenuNames, langIdx, 200);   // row label above already says it
            GUILayout.EndHorizontal();
            if (DropdownOptions("language", Loc.MenuNames, ref langIdx, 200))
            {
                _languageCfg.Value = Loc.MenuNames[langIdx];
                Config.Save();
                Loc.Refresh(_languageCfg.Value);
                // Widths are measured from the previous language's text, so a switch mid-session leaves stale
                // metrics until the styles are rebuilt. Re-arm the font/style pass rather than making the user
                // reopen the panel.
                _fontApplied = false;
            }
            GUILayout.Label(Loc.T("set.language_hint"), _dimLabelStyle);

            DrawSection(Loc.T("set.behavior"));
            var cam = GUILayout.Toggle(_cameraLockCfg.Value, " " + Loc.T("set.camera_lock"), _toggleStyle);
            if (cam != _cameraLockCfg.Value) { _cameraLockCfg.Value = cam; Config.Save(); }

            DrawSection(Loc.T("set.panel"));
            var wnAuto = GUILayout.Toggle(_autoWhatsNewCfg.Value, " " + Loc.T("set.auto_whats_new"), _toggleStyle);
            if (wnAuto != _autoWhatsNewCfg.Value) { _autoWhatsNewCfg.Value = wnAuto; Config.Save(); }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("set.whats_new"), _buttonStyle, GUILayout.MinWidth(150))) _sideMode = SideMode.WhatsNew;
            if (GUILayout.Button(Loc.T("set.report_bug"), _buttonStyle, GUILayout.MinWidth(130))) _sideMode = SideMode.BugReport;
            GUILayout.EndHorizontal();

            DrawSection(Loc.T("set.support"));
            GUILayout.BeginHorizontal();
            DrawCoffeeButton(240f);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("set.support_hint"), _dimLabelStyle);

            DrawSection(Loc.T("set.hotkeys"));
            DrawRebindRow(Loc.T("set.key_toggle"), _toggleKey, 1);
            DrawRebindRow(Loc.T("set.key_map_tp"), _mapTpKey, 2);
            // Emit the "listening" hint only when the Layout pass saw the rebind active (same control-count
            // rule as _openDropdownLayout: _rebindTarget flips mid-frame on the click pass).
            if (_rebindTargetLayout != 0)
                GUILayout.Label(Loc.T("set.rebind_hint"), _headerStyle);
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
                // Mouse buttons bind too — but never left/right (0/1): those are needed to click the panel
                // itself. Unity exposes them as KeyCode.Mouse0..Mouse6, and Input.GetKeyDown reads them fine.
                else if (ev.type == EventType.MouseDown && ev.button >= 2 && ev.button <= 6)
                {
                    var target = _rebindTarget == 1 ? _toggleKey : _mapTpKey;
                    target.Value = (KeyCode)((int)KeyCode.Mouse0 + ev.button);
                    Config.Save();
                    _rebindTarget = 0;
                    ev.Use();
                }
            }

            DrawSection(Loc.T("set.reset"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("set.reset_window"), _buttonStyle, GUILayout.MinWidth(230)))
            {
                _windowRect = new Rect(60, 60, 740, 680);
                SaveWindowRect();
            }
            if (GUILayout.Button(Loc.T("set.reset_appearance"), _buttonStyle, GUILayout.MinWidth(160)))
            {
                _fontSizeLive = 13;
                _panelAlphaLive = 96;
                ApplyFontSizes();
                ApplyPanelAlpha();
                CommitUiSettings();
                SelectFont(FontChoices[0]);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("set.saved_hint"), _dimLabelStyle);

            GUILayout.EndScrollView();
        }

        private void DrawRebindRow(string label, ConfigEntry<KeyCode> entry, int target)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}:", _labelStyle, GUILayout.MinWidth(170));
            GUILayout.Label($"[{entry.Value}]", _headerStyle, GUILayout.MinWidth(100));
            var listening = _rebindTarget == target;
            if (GUILayout.Button(listening ? Loc.T("set.listening") : Loc.T("set.rebind"), _buttonStyle, GUILayout.MinWidth(110)))
                _rebindTarget = listening ? 0 : target;
            GUILayout.EndHorizontal();
        }
    }
}
