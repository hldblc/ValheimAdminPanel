using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace AdminPanel
{
    // ==================== Item progression tiers ====================
    //
    // "Which point of the game does this item belong to?" - answered with the biome progression every Valheim
    // guide uses, because each biome's boss gates the next one's materials:
    //
    //   1 Meadows       wood, stone, flint, leather, deer hide
    //   2 Black Forest  copper + tin = bronze, troll hide, fine wood, core wood
    //   3 Swamp         iron, ancient bark, guck, chain
    //   4 Mountain      silver, obsidian, wolf pelt, fenris hair, crystal
    //   5 Plains        black metal, linen, barley, lox pelt, needle, tar
    //   6 Mistlands     eitr, carapace, black marble, Yggdrasil wood, scale hide
    //   7 Ashlands      flametal, ashwood, grausten, charred bone, asksvin hide, gemstones
    //   8 Deep North    (only what the override file or a recipe chain puts there)
    //   0               not placed (no recipe and not a known material)
    //
    // An item is as late as the LATEST thing needed to make it: the tier of a craftable is the highest tier among
    // its recipe's ingredients (resolved recursively) and its crafting station; with several recipes the earliest
    // one counts. Raw materials, drops and trophies come from the table below. Nothing is hard-coded per weapon,
    // so modded items and items added by a game update sort themselves as long as they are built from known
    // materials - a Draugr Fang is tier 4 because it needs silver, not because a list says so.
    //
    // A server owner can correct or extend the result without rebuilding: one "PrefabName=tier" line per item in
    // BepInEx/plugins/AdminPanel_ItemTiers.txt (same folder as AdminPanel.dll; '#' starts a comment).
    internal static class ItemTiers
    {
        internal const int Max = 8;

        private static readonly Dictionary<string, int> Known = BuildKnown();
        private static readonly Dictionary<string, int> Stations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "piece_workbench", 1 },
            { "forge", 2 },
            { "piece_cauldron", 2 },
            { "piece_MeadCauldron", 2 },
            { "piece_stonecutter", 3 },
            { "piece_artisanstation", 5 },   // needs a dragon tear (Moder): what it makes is Plains-tier
            { "blackforge", 6 },
            { "piece_magetable", 6 },        // galdr table
        };

        private static readonly Dictionary<string, int> Cache = new Dictionary<string, int>(StringComparer.Ordinal);
        private static Dictionary<string, List<Recipe>> _recipes;
        private static Dictionary<string, int> _overrides;
        private static object _db;

        /// <summary>Tier 1..8 of an item prefab, or 0 when it cannot be placed. Cached per ObjectDB instance.</summary>
        internal static int Of(ItemDrop drop)
        {
            if (drop == null) return 0;
            var db = ObjectDB.instance;
            if (!ReferenceEquals(db, _db))
            {
                // A new world (or a mod that rebuilt ObjectDB) can change recipes: start over.
                Cache.Clear();
                _recipes = null;
                _overrides = null;
                _db = db;
            }
            try { return Resolve(drop.name, 0); }
            catch (Exception) { return 0; }   // a broken recipe in some other mod must not break the item list
        }

        private static int Resolve(string prefab, int depth)
        {
            if (string.IsNullOrEmpty(prefab)) return 0;
            int t;
            if (Cache.TryGetValue(prefab, out t)) return t;
            if (Overrides().TryGetValue(prefab, out t) || Known.TryGetValue(prefab, out t))
            {
                Cache[prefab] = t;
                return t;
            }
            if (depth > 6) return 0;      // deliberately not cached: a shorter path may still resolve it

            Cache[prefab] = 0;            // cycle guard: A needs B needs A
            var best = 0;
            List<Recipe> list;
            if (Recipes().TryGetValue(prefab, out list))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var rt = RecipeTier(list[i], depth);
                    if (rt > 0 && (best == 0 || rt < best)) best = rt;   // several recipes: the earliest way to get it
                }
            }
            Cache[prefab] = best;
            return best;
        }

        private static int RecipeTier(Recipe r, int depth)
        {
            var t = 0;
            if (r.m_resources != null)
            {
                foreach (var req in r.m_resources)
                {
                    // m_amount == 0 is an upgrade-only ingredient: it does not gate crafting the item.
                    if (req == null || req.m_resItem == null || req.m_amount <= 0) continue;
                    var rt = Resolve(req.m_resItem.name, depth + 1);
                    if (rt > t) t = rt;
                }
            }
            if (r.m_craftingStation != null)
            {
                int st;
                if (Stations.TryGetValue(r.m_craftingStation.name, out st) && st > t) t = st;
            }
            return t;
        }

        private static Dictionary<string, List<Recipe>> Recipes()
        {
            if (_recipes != null) return _recipes;
            _recipes = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
            var db = ObjectDB.instance;
            if (db == null || db.m_recipes == null) return _recipes;
            foreach (var r in db.m_recipes)
            {
                if (r == null || r.m_item == null || !r.m_enabled) continue;
                List<Recipe> list;
                if (!_recipes.TryGetValue(r.m_item.name, out list)) _recipes[r.m_item.name] = list = new List<Recipe>(1);
                list.Add(r);
            }
            return _recipes;
        }

        private static Dictionary<string, int> Overrides()
        {
            if (_overrides != null) return _overrides;
            _overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                var file = Path.Combine(dir, "AdminPanel_ItemTiers.txt");
                if (!File.Exists(file)) return _overrides;
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    int tier;
                    if (!int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tier)) continue;
                    if (tier < 0 || tier > Max) continue;
                    _overrides[line.Substring(0, eq).Trim()] = tier;
                }
            }
            catch (Exception) { /* an unreadable override file means "no overrides", never a broken item list */ }
            return _overrides;
        }

        // Raw materials, drops and trophies by the biome that first yields them. Names that do not exist on the
        // running game are simply never asked for, so an entry that a later update renames costs nothing.
        private static Dictionary<string, int> BuildKnown()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            Add(d, 1, "Wood", "Stone", "Flint", "Resin", "LeatherScraps", "DeerHide", "Feathers", "Dandelion", "Raspberry",
                "Mushroom", "Honey", "QueenBee", "RawMeat", "DeerMeat", "NeckTail", "HardAntler", "BeechSeeds", "Acorn",
                "BirchSeeds", "FishRaw", "Fish1", "Fish2", "TrophyBoar", "TrophyDeer", "TrophyNeck", "TrophyEikthyr");
            Add(d, 2, "Copper", "CopperOre", "CopperScrap", "Tin", "TinOre", "Bronze", "BronzeScrap", "BronzeNails", "TrollHide",
                "FineWood", "RoundLog", "GreydwarfEye", "SurtlingCore", "Thistle", "Blueberries", "MushroomYellow", "Carrot",
                "CarrotSeeds", "BoneFragments", "AncientSeed", "Coal", "Amber", "AmberPearl", "Ruby", "Coins", "SilverNecklace",
                "CryptKey", "PineCone", "FirCone", "Ectoplasm", "YmirRemains", "Thunderstone", "FishingBait", "FishingRod",
                "BeltStrength", "HelmetDverger", "Fish5", "TrophyGreydwarf", "TrophyGreydwarfBrute", "TrophyGreydwarfShaman",
                "TrophySkeleton", "TrophySkeletonPoison", "TrophyFrostTroll", "TrophyTheElder", "TrophyGhost");
            Add(d, 3, "Iron", "IronScrap", "IronOre", "IronNails", "ElderBark", "Guck", "Chain", "Ooze", "Bloodbag", "Entrails",
                "WitheredBone", "Root", "Turnip", "TurnipSeeds", "Wishbone", "Chitin", "Pukeberries", "Fish6", "TrophyDraugr",
                "TrophyDraugrElite", "TrophyDraugrFem", "TrophyLeech", "TrophyBlob", "TrophyWraith", "TrophyAbomination",
                "TrophySurtling", "TrophyBonemass", "TrophyKvastur");
            Add(d, 4, "Silver", "SilverOre", "SilverScrap", "Obsidian", "WolfPelt", "WolfFang", "WolfClaw", "WolfHairBundle",
                "WolfMeat", "Crystal", "FreezeGland", "DragonEgg", "DragonTear", "JuteRed", "Onion", "OnionSeeds",
                "SerpentScale", "SerpentMeat", "Fish4_cave", "TrophyWolf", "TrophyFenring", "TrophyHatchling", "TrophySGolem",
                "TrophyUlv", "TrophyCultist", "TrophyDragonQueen", "TrophySerpent");
            Add(d, 5, "BlackMetal", "BlackMetalScrap", "Flax", "LinenThread", "Barley", "BarleyFlour", "LoxPelt", "LoxMeat",
                "Needle", "Tar", "Cloudberry", "GoblinTotem", "YagluthDrop", "Fish7", "Fish8", "TrophyDeathsquito", "TrophyLox",
                "TrophyGoblin", "TrophyGoblinBrute", "TrophyGoblinShaman", "TrophyGrowth", "TrophyGoblinKing");
            Add(d, 6, "Eitr", "Sap", "Softtissue", "Carapace", "BlackMarble", "YggdrasilWood", "ScaleHide", "Mandible",
                "BlackCore", "Wisp", "JuteBlue", "Bilebag", "GiantBloodSack", "RoyalJelly", "MushroomJotunPuffs",
                "MushroomMagecap", "DvergrNeedle", "DvergrKeyFragment", "DvergrKey", "QueenDrop", "HareMeat", "BugMeat",
                "ChickenMeat", "ChickenEgg", "Fish9", "Fish12", "TrophySeeker", "TrophySeekerBrute", "TrophyGjall", "TrophyTick",
                "TrophyHare", "TrophyDvergr", "TrophySeekerQueen");
            Add(d, 7, "FlametalNew", "FlametalOreNew", "Flametal", "FlametalOre", "Blackwood", "Grausten", "CharredBone",
                "AskHide", "AskBladder", "MorgenSinew", "MorgenHeart", "GemstoneRed", "GemstoneBlue", "GemstoneGreen",
                "CelestialFeather", "SulfurStone", "ProustitePowder", "CharcoalResin", "CeramicPlate", "BellFragment",
                "MoltenCore", "Charredskull", "FaderDrop", "Fiddleheadfern", "Vineberry", "MushroomSmokePuff", "VoltureMeat",
                "VoltureEgg", "AsksvinMeat", "BonemawSerpentMeat", "BoneMawSerpentTooth", "CharredCogwheel",
                "DyrnwynBladeFragment", "DyrnwynHiltFragment", "DyrnwynTipFragment", "Fish10", "Fish11", "TrophyCharredArcher",
                "TrophyCharredMelee", "TrophyCharredMage", "TrophyMorgen", "TrophyAsksvin", "TrophyVolture",
                "TrophyFallenValkyrie", "TrophyFader", "TrophyBonemawSerpent");
            return d;
        }

        private static void Add(Dictionary<string, int> d, int tier, params string[] names)
        {
            foreach (var n in names) d[n] = tier;
        }
    }

    public partial class AdminPanelPlugin
    {
        // The game's own biome names, so the tier labels follow the player's game language for free (the same
        // tokens the minimap prints). English only when the game cannot resolve one.
        private static readonly string[] TierTokens =
        {
            "", "$biome_meadows", "$biome_blackforest", "$biome_swamp", "$biome_mountain", "$biome_plains",
            "$biome_mistlands", "$biome_ashlands", "$biome_deepnorth"
        };

        private static readonly string[] TierFallback =
        {
            "", "Meadows", "Black Forest", "Swamp", "Mountain", "Plains", "Mistlands", "Ashlands", "Deep North"
        };

        private string[] _tierNames;         // [0] = "not placed", 1..8 = biome names; rebuilt with the item index
        private string[] _itemTierLabels;    // filter dropdown: all, 1..8, not placed
        private int _itemTier;               // 0 = all tiers, 1..8 = that tier, 9 = not placed

        /// <summary>Called from RefreshCaches, before the item index is built.</summary>
        private void TierRefreshNames()
        {
            var names = new string[ItemTiers.Max + 1];
            names[0] = Loc.T("items.tier_none");
            for (var i = 1; i <= ItemTiers.Max; i++) names[i] = LocalizeSafe(TierTokens[i], TierFallback[i]);
            _tierNames = names;

            var labels = new string[ItemTiers.Max + 2];
            labels[0] = Loc.T("items.tier_all");
            for (var i = 1; i <= ItemTiers.Max; i++) labels[i] = i.ToString(CultureInfo.InvariantCulture) + " · " + names[i];
            labels[ItemTiers.Max + 1] = names[0];
            _itemTierLabels = labels;
        }

        /// <summary>"T3 Swamp" - the tag shown in front of an item's stat line. Empty for an unplaced item.</summary>
        private string TierTag(int tier)
        {
            if (tier <= 0 || tier > ItemTiers.Max || _tierNames == null) return "";
            return "T" + tier.ToString(CultureInfo.InvariantCulture) + " " + _tierNames[tier];
        }

        private bool TierFilterMatches(int tier)
        {
            if (_itemTier <= 0) return true;
            if (_itemTier > ItemTiers.Max) return tier <= 0;
            return tier == _itemTier;
        }

        // Unplaced items sort after every tier in both directions: "early first" must not open with a page of
        // odds and ends, and "late first" should open with the endgame, not with them either.
        private static int TierSortKey(int tier, bool lateFirst) =>
            tier <= 0 ? int.MaxValue : (lateFirst ? ItemTiers.Max - tier : tier);
    }
}
