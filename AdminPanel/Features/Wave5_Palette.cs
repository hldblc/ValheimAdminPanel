using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 5 - Command palette / global search / macros ====================
    // Member prefix: "Pal". Locale prefix: "ux.".
    //
    // This file owns an ON-SCREEN OVERLAY, so it is the only feature module whose drawing runs while the
    // main panel is CLOSED (FeaturesOnGUI -> PalOnGUI, before OnGUI's !_visible early-out). That has three
    // consequences the rest of the codebase never has to think about:
    //
    //   1. EnsureSkin()/ApplyFont() are called by OnGUI only AFTER the visibility gate, so PalOnGUI calls
    //      them itself before drawing anything.
    //   2. The overlay is its own GUI.Window on a fresh id (918275; 918273 = main panel, 918274 = side
    //      window) and follows the PanelBgTint rule - tint before the call, white on the callback's first
    //      line, restore after.
    //   3. With the panel closed, Valheim's cursor/input/attack patches (which all gate on _visible) are
    //      inert, so typing into the palette would also drive the character. PalOverlayPatches re-applies
    //      exactly those three gates for "palette open, panel closed". Every patch is optional: if one
    //      fails to bind the palette still works, it just leaks input to the game.
    //
    // IMGUI law: the candidate list is rebuilt ONLY on the Layout pass (and only when the query text
    // actually changed), and every draw - including the window's own height - reads the pinned snapshot.
    // Key handling never adds or removes a control: Escape/Enter set a deferred flag instead of acting
    // inline, and everything that touches the MAIN panel (tab switch, search prefill, opening the panel,
    // running a command) is executed from PalTick (LateUpdate), outside OnGUI entirely. A jump that flipped
    // _visible mid-pass would hand the main window a Repaint with no Layout behind it.
    //
    // No new server RPCs: every verb calls the panel's own existing helpers (SendServerGive,
    // SendServerSpawn, SrvRpc("AP_SrvHeal"/"AP_SrvSkipNight"), TeleportToWorld, KillNearby, the Player-tab
    // cheat fields). The palette is a faster way to press buttons that already exist.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<KeyCode> _palKeyCfg;        // Features / PaletteKey
        private ConfigEntry<bool> _palSectionCfg;       // Features / ShowMacroSection
        private ConfigEntry<string> _palMacrosCfg;      // Features / Macros
        private bool _palInited;

        private const int PalWindowId = 918275;
        private const int PalMaxRows = 12;              // visible candidate rows, hard cap
        private const int PalMacroSlots = 8;            // fixed editor slots => stable control count
        private const float PalStepGap = 0.25f;         // 250 ms between macro steps

        // Row groups (also the search-mode headings).
        private const int PalGroupCommand = 0;
        private const int PalGroupItem = 1;
        private const int PalGroupCreature = 2;
        private const int PalGroupPlayer = 3;
        private const int PalGroupSetting = 4;

        // Row kinds. Verb/Arg complete the input line; Jump* leave the palette and drive the main panel.
        private const int PalKindVerb = 0;
        private const int PalKindArg = 1;
        private const int PalKindJumpItems = 2;
        private const int PalKindJumpCreatures = 3;
        private const int PalKindJumpPlayers = 4;
        private const int PalKindJumpSettings = 5;
        private const int PalKindPickCommand = 6;

        private sealed class PalVerb
        {
            public string Verb;      // English, never localized: it is typed by the user and stored in macros
            public string Usage;     // syntax line, English on purpose (it is syntax, not prose)
            public string HintKey;   // locale key for the one-line description
            public Func<string[], bool> Run;   // false = failed (a macro aborts on the first false)
        }

        private sealed class PalRow
        {
            public int Group;
            public int Kind;
            public string Label;
            public string Detail;
            public string Payload;   // verb / completion token / search prefill
        }

        private readonly List<PalVerb> _palVerbs = new List<PalVerb>();

        // ---- overlay state ----
        private bool _palOpen;
        private bool _palOpenRequested;      // set from the Extras section; applied in PalTick (never mid-OnGUI)
        private bool _palCloseRequested;     // set inside the window callback; applied after GUI.Window returns
        private bool _palJustOpened;         // one-shot BringWindowToFront/FocusWindow
        private bool _palFocusField;         // one-shot keyboard focus into the text field
        private bool _palCaretToEnd;         // move the caret after a programmatic completion
        private string _palInput = "";
        private int _palSel;

        private List<PalRow> _palRowsLayout;   // the ONLY list the overlay draws from
        private string _palRowsKey;            // query the snapshot was built for (null = rebuild next Layout)
        private int _palSelLayout;
        private List<string> _palWeatherNames; // EnvMan environment names, resolved lazily per world

        // ---- deferred work (applied in PalTick, outside OnGUI) ----
        private int _palPendingJump;           // 0 = none, else PalKindJump*
        private string _palPendingJumpArg;
        private string _palPendingRun;

        // ---- macro state ----
        private List<string> _palQueue;        // remaining steps of the running macro (null = idle)
        private string _palRunName;
        private int _palRunStep, _palRunTotal;
        private float _palNextStepAt;

        private string _palMacroRaw;           // cache key for the parsed macro table (same idiom as BookmarksKv)
        private Dictionary<string, string> _palMacroKv;
        private List<KeyValuePair<string, string>> _palMacroRowsLayout;
        private bool _palRunningLayout;
        private string _palRunNameLayout;
        private int _palRunStepLayout, _palRunTotalLayout;

        private string _palEditName = "";
        private string[] _palEditSteps = new string[PalMacroSlots];
        private Vector2 _palSectionScroll;

        // Settings the global search can point at. These reuse the Settings tab's OWN locale keys, so the
        // search text always matches what the tab actually shows, in every language, with no new strings.
        private static readonly string[] PalSettingKeys =
        {
            "set.language", "set.font", "set.font_size", "set.opacity", "set.show_logo",
            "set.camera_lock", "set.auto_whats_new", "set.key_toggle", "set.key_map_tp",
            "set.reset_window", "set.report_bug", "set.buy_coffee"
        };

        // ==================== lifecycle ====================

        // Config binds + the built-in verb registry + the overlay input patches. The glue file only has to
        // call PalInit / PalTick / PalReset / PalOnGUI and register the section.
        internal void PalInit()
        {
            if (_palInited) return;
            _palInited = true;

            // Default KeyCode.None = DISABLED. A palette that grabbed a key by default could collide with a
            // bind the admin already uses (or with another mod) on first launch, and an overlay that eats
            // keystrokes is the worst possible thing to enable without being asked.
            _palKeyCfg = Config.Bind("Features", "PaletteKey", KeyCode.None,
                "Key that opens the command palette overlay. None = disabled (the default) so it can never " +
                "collide with an existing bind. Set it to e.g. BackQuote or F8 to enable the palette.");
            _palSectionCfg = Config.Bind("Features", "ShowMacroSection", true,
                "Show the Macros section in the Extras tab (command palette info, saved macros and the macro editor).");
            _palMacrosCfg = Config.Bind("Features", "Macros", "",
                "Saved macros: name=step;step;step|name2=... Steps are palette command lines. Percent-escaped " +
                "the same way as bookmarks and presets - edit them in the panel rather than by hand.");

            for (var i = 0; i < _palEditSteps.Length; i++) _palEditSteps[i] = "";

            PalRegisterBuiltins();

            // Optional: without these the palette still opens and runs, but with the main panel CLOSED the
            // game keeps reading movement/attack input and keeps the cursor captured while you type.
            try { Harmony.CreateAndPatchAll(typeof(PalOverlayPatches)); }
            catch (Exception e)
            {
                Logger.LogWarning("Palette overlay input patches failed (palette still works; game input may " +
                                  "leak while it is open with the panel closed): " + e.Message);
            }
        }

        internal bool PalSectionEnabled() => _palSectionCfg == null || _palSectionCfg.Value;

        // Per-world state only. The verb registry is process-level (it points at plugin methods, not at world
        // objects) and MUST survive a logout, or the palette would come back empty on the next login.
        internal void PalReset()
        {
            _palOpen = false;
            _palOpenRequested = false;
            _palCloseRequested = false;
            _palJustOpened = false;
            _palFocusField = false;
            _palCaretToEnd = false;
            _palInput = "";
            _palSel = 0;
            _palSelLayout = 0;
            _palRowsLayout = null;
            _palRowsKey = null;
            _palWeatherNames = null;
            _palPendingJump = 0;
            _palPendingJumpArg = null;
            _palPendingRun = null;
            _palQueue = null;
            _palRunName = null;
            _palRunStep = 0;
            _palRunTotal = 0;
            _palNextStepAt = 0f;
            _palMacroRaw = null;
            _palMacroKv = null;
            _palMacroRowsLayout = null;
            _palRunningLayout = false;
            _palRunNameLayout = null;
            _palRunStepLayout = 0;
            _palRunTotalLayout = 0;
            _palSectionScroll = Vector2.zero;
            _palEditName = "";
            for (var i = 0; i < _palEditSteps.Length; i++) _palEditSteps[i] = "";
        }

        // ==================== overlay input patches ====================
        // Active ONLY while the palette is open and the main panel is closed - when the panel is open its own
        // patches (CursorPatch / InputBlockPatch / BlockAttackPatch) already do all of this, and doubling up
        // would be harmless but pointless. Every hook is a strict no-op when Active is false.
        [HarmonyPatch]
        internal static class PalOverlayPatches
        {
            private static bool Active => Instance != null && Instance._palOpen && !Instance._visible;

            [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
            [HarmonyPrefix]
            private static bool CursorPrefix()
            {
                if (!Active) return true;   // let vanilla capture the mouse
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return false;
            }

            [HarmonyPatch(typeof(Player), "TakeInput")]
            [HarmonyPostfix]
            private static void InputPostfix(ref bool __result)
            {
                if (Active) __result = false;
            }

            [HarmonyPatch(typeof(Humanoid), "StartAttack")]
            [HarmonyPrefix]
            private static bool AttackPrefix(Humanoid __instance)
            {
                return !(Active && __instance == Player.m_localPlayer);
            }
        }

        // ==================== command registry ====================

        /// <summary>
        /// Register a palette verb. Verbs are matched case-insensitively and must be unique; a duplicate is
        /// ignored so a wave can never silently shadow another wave's command. <paramref name="run"/> returns
        /// false when the step failed - a macro aborts on the first false.
        /// </summary>
        internal void PalRegister(string verb, string usage, string locKeyHint, Func<string[], bool> run)
        {
            if (string.IsNullOrEmpty(verb) || run == null) return;
            if (PalFindVerb(verb) != null) return;
            _palVerbs.Add(new PalVerb
            {
                Verb = verb,
                Usage = usage ?? verb,
                HintKey = string.IsNullOrEmpty(locKeyHint) ? "ux.cmd_custom" : locKeyHint,
                Run = run
            });
        }

        private PalVerb PalFindVerb(string verb)
        {
            if (string.IsNullOrEmpty(verb)) return null;
            foreach (var v in _palVerbs)
                if (string.Equals(v.Verb, verb, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        // Order = how often an admin reaches for them; it is also the order shown for an empty query.
        private void PalRegisterBuiltins()
        {
            PalRegister("give", "give <item> <count> [@player]", "ux.cmd_give", PalCmdGive);
            PalRegister("spawn", "spawn <creature> <count> [star]", "ux.cmd_spawn", PalCmdSpawn);
            PalRegister("tp", "tp <player>|<x,y,z>|bookmark:<name>", "ux.cmd_tp", PalCmdTp);
            PalRegister("heal", "heal [@player|@all]", "ux.cmd_heal", PalCmdHeal);
            PalRegister("kill-all", "kill-all", "ux.cmd_killall", PalCmdKillAll);
            PalRegister("time", "time <0-1>", "ux.cmd_time", PalCmdTime);
            PalRegister("weather", "weather <name>", "ux.cmd_weather", PalCmdWeather);
            PalRegister("skip-night", "skip-night", "ux.cmd_skipnight", PalCmdSkipNight);
            PalRegister("god", "god [on|off]", "ux.cmd_god", a => PalCmdToggle("god", a));
            PalRegister("fly", "fly [on|off]", "ux.cmd_fly", a => PalCmdToggle("fly", a));
            PalRegister("ghost", "ghost [on|off]", "ux.cmd_ghost", a => PalCmdToggle("ghost", a));
            PalRegister("noStamina", "noStamina [on|off]", "ux.cmd_nostamina", a => PalCmdToggle("noStamina", a));
        }

        // ==================== command implementations ====================
        // Every one of these calls a method or RPC the panel already had. They run from PalTick (LateUpdate),
        // never from inside OnGUI, so they are free to touch panel state without control-count worries.

        private bool PalCmdGive(string[] args)
        {
            if (LocalPlayer == null) { Message(Loc.T("ux.err_no_player")); return false; }
            var parts = new List<string>(args);

            // Optional "@name" anywhere in the line, taken out first so it can never be read as the count.
            string wanted = null;
            for (var i = parts.Count - 1; i >= 0; i--)
                if (parts[i].StartsWith("@", StringComparison.Ordinal))
                { wanted = parts[i].Substring(1); parts.RemoveAt(i); break; }

            var count = 1;
            if (parts.Count > 1 && int.TryParse(parts[parts.Count - 1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var n) && n > 0)
            { count = Mathf.Clamp(n, 1, 9999); parts.RemoveAt(parts.Count - 1); }

            if (parts.Count == 0) { Message(Loc.T("ux.err_usage", "give <item> <count> [@player]")); return false; }

            var query = string.Join(" ", parts.ToArray());
            var item = PalFindItem(query);
            if (item == null) { Message(Loc.T("ux.err_no_item", query)); return false; }

            var target = SelfUid();
            var targetName = Loc.T("common.me");
            if (!string.IsNullOrEmpty(wanted))
            {
                if (!PalFindPlayer(wanted, out var info)) { Message(Loc.T("ux.err_no_player_named", wanted)); return false; }
                target = PeerIdOf(info);
                targetName = info.m_name;
            }
            SendServerGive(target, item.Prefab, count, 1);
            Message(Loc.T("ux.msg_give", count, item.Display, targetName));
            return true;
        }

        private bool PalCmdSpawn(string[] args)
        {
            if (LocalPlayer == null) { Message(Loc.T("ux.err_no_player")); return false; }
            var parts = new List<string>(args);

            // Trailing numbers are count and stars, in that order: "spawn troll 3 2".
            var stars = 0;
            var count = 1;
            var trailing = new List<int>();
            while (parts.Count > 1 && int.TryParse(parts[parts.Count - 1], NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out var v) && trailing.Count < 2)
            { trailing.Insert(0, v); parts.RemoveAt(parts.Count - 1); }
            if (trailing.Count >= 1) count = Mathf.Clamp(trailing[0], 1, 100);
            if (trailing.Count >= 2) stars = Mathf.Clamp(trailing[1], 0, 10);

            if (parts.Count == 0) { Message(Loc.T("ux.err_usage", "spawn <creature> <count> [star]")); return false; }
            var query = string.Join(" ", parts.ToArray());
            var cre = PalFindCreature(query);
            if (cre == null) { Message(Loc.T("ux.err_no_creature", query)); return false; }

            SendServerSpawn(1, cre.Name, SpawnPos(3f), count, stars + 1, false);
            Message(Loc.T("ux.msg_spawn", count, cre.Display));
            return true;
        }

        private bool PalCmdTp(string[] args)
        {
            if (LocalPlayer == null) { Message(Loc.T("ux.err_no_player")); return false; }
            if (args.Length == 0) { Message(Loc.T("ux.err_usage", "tp <player>|<x,y,z>|bookmark:<name>")); return false; }
            var joined = string.Join(" ", args).Trim();

            if (joined.StartsWith("bookmark:", StringComparison.OrdinalIgnoreCase))
            {
                var name = joined.Substring("bookmark:".Length).Trim();
                string value = null, shown = name;
                foreach (var kv in BookmarksKv())
                {
                    if (!string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) &&
                        !kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                    value = kv.Value; shown = kv.Key;
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) break;
                }
                if (value == null) { Message(Loc.T("ux.err_no_bookmark", name)); return false; }
                if (!PalParseVec(value, out var bp)) { Message(Loc.T("ux.err_bad_coords")); return false; }
                LocalPlayer.TeleportTo(bp, LocalPlayer.transform.rotation, true);
                Message(Loc.T("ux.msg_tp", shown));
                return true;
            }

            if (joined.IndexOf(',') >= 0)
            {
                if (!PalParseVec(joined, out var p)) { Message(Loc.T("ux.err_bad_coords")); return false; }
                // Same "y <= 0 means drop me from the sky" convention as the World tab's coordinate row.
                if (p.y <= 0f) p.y = 200f;
                LocalPlayer.TeleportTo(p, LocalPlayer.transform.rotation, true);
                Message(Loc.T("ux.msg_tp", string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}", p.x, p.z)));
                return true;
            }

            if (!PalFindPlayer(joined, out var info)) { Message(Loc.T("ux.err_no_player_named", joined)); return false; }
            LocalPlayer.TeleportTo(info.m_position + Vector3.up, LocalPlayer.transform.rotation, true);
            Message(Loc.T("ux.msg_tp", info.m_name));
            return true;
        }

        private bool PalCmdHeal(string[] args)
        {
            var me = LocalPlayer;
            if (me == null) { Message(Loc.T("ux.err_no_player")); return false; }
            var who = args.Length > 0 ? args[0].TrimStart('@') : "";

            if (string.IsNullOrEmpty(who))
            {
                me.Heal(me.GetMaxHealth());
                me.AddStamina(me.GetMaxStamina());
                me.AddEitr(me.GetMaxEitr());
                Message(Loc.T("ux.msg_heal", Loc.T("common.me")));
                return true;
            }
            if (string.Equals(who, "all", StringComparison.OrdinalIgnoreCase))
            {
                me.Heal(me.GetMaxHealth());
                me.AddStamina(me.GetMaxStamina());
                foreach (var p in OtherPlayers()) SrvRpc("AP_SrvHeal", PeerIdOf(p));
                Message(Loc.T("ux.msg_heal_all"));
                return true;
            }
            if (!PalFindPlayer(who, out var info)) { Message(Loc.T("ux.err_no_player_named", who)); return false; }
            SrvRpc("AP_SrvHeal", PeerIdOf(info));
            Message(Loc.T("ux.msg_heal", info.m_name));
            return true;
        }

        private bool PalCmdKillAll(string[] args)
        {
            if (LocalPlayer == null) { Message(Loc.T("ux.err_no_player")); return false; }
            KillNearby(100000f, false);   // exactly what the World tab's confirmed "Kill ALL loaded" runs
            return true;
        }

        private bool PalCmdTime(string[] args)
        {
            var env = EnvMan.instance;
            if (env == null) { Message(Loc.T("ux.err_no_env")); return false; }
            if (args.Length == 0) { Message(Loc.T("ux.err_usage", "time <0-1>")); return false; }
            if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            { Message(Loc.T("ux.err_bad_number", args[0])); return false; }
            t = Mathf.Clamp01(t);
            env.m_debugTimeOfDay = true;
            env.m_debugTime = t;
            _timeSlider = t;
            _timeLocked = true;
            Message(Loc.T("ux.msg_time", TimeLabel(t)));
            return true;
        }

        private bool PalCmdWeather(string[] args)
        {
            var env = EnvMan.instance;
            if (env == null) { Message(Loc.T("ux.err_no_env")); return false; }
            var name = args.Length == 0 ? "" : string.Join(" ", args).Trim();
            if (name.Length == 0 || string.Equals(name, "clear", StringComparison.OrdinalIgnoreCase))
            {
                env.m_debugEnv = "";
                _weather = "";
                Message(Loc.T("ux.msg_weather_reset"));
                return true;
            }
            // Snap a partial name to a real environment when we can see the list; otherwise pass it through
            // and let the game ignore an unknown value (exactly what the World tab's text field does).
            var known = PalWeatherNames();
            var resolved = name;
            foreach (var w in known)
            {
                if (string.Equals(w, name, StringComparison.OrdinalIgnoreCase)) { resolved = w; break; }
                if (resolved == name && w.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) resolved = w;
            }
            env.m_debugEnv = resolved;
            _weather = resolved;
            Message(Loc.T("ux.msg_weather", resolved));
            return true;
        }

        private bool PalCmdSkipNight(string[] args)
        {
            SrvRpc("AP_SrvSkipNight");   // world time is server-owned; same call as the World tab
            Message(Loc.T("world.msg_skip_night"));
            return true;
        }

        // god / fly / ghost / noStamina. Optional on|off; with no argument it toggles. The fields written
        // here are the SAME ones the Player tab draws, so its toggles stay in sync (a toggle's value changing
        // never changes a control count).
        private bool PalCmdToggle(string which, string[] args)
        {
            var me = LocalPlayer;
            if (me == null) { Message(Loc.T("ux.err_no_player")); return false; }
            bool? want = null;
            if (args.Length > 0)
            {
                if (string.Equals(args[0], "on", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[0], "1", StringComparison.Ordinal)) want = true;
                else if (string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(args[0], "0", StringComparison.Ordinal)) want = false;
            }
            switch (which)
            {
                case "god":
                    _god = want ?? !_god;
                    me.SetGodMode(_god);
                    Message(Loc.T("ux.msg_toggle", "god", OnOff(_god)));
                    return true;
                case "ghost":
                    _ghost = want ?? !_ghost;
                    me.SetGhostMode(_ghost);
                    Message(Loc.T("ux.msg_toggle", "ghost", OnOff(_ghost)));
                    return true;
                case "noStamina":
                    _noStamina = want ?? !_noStamina;
                    NoStaminaFlag = _noStamina;
                    Message(Loc.T("ux.msg_toggle", "noStamina", OnOff(_noStamina)));
                    return true;
                case "fly":
                    // ToggleDebugFly has no "set to X" form, so an explicit on/off that already matches the
                    // tracked state is a no-op rather than a flip.
                    var target = want ?? !_fly;
                    if (target != _fly)
                    {
                        _fly = target;
                        Player.m_debugMode = true;
                        me.ToggleDebugFly();
                    }
                    Message(Loc.T("ux.msg_toggle", "fly", OnOff(_fly)));
                    return true;
            }
            return false;
        }

        // ==================== lookup helpers ====================

        private static bool PalParseVec(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            var bits = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length < 3) return false;
            if (!float.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(bits[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        // Prefix match on the typed fragment, so "@Hal" finds "Halit" without needing quotes for names
        // that contain spaces.
        private bool PalFindPlayer(string name, out ZNet.PlayerInfo found)
        {
            found = default(ZNet.PlayerInfo);
            if (string.IsNullOrEmpty(name)) return false;
            var list = OtherPlayers();
            foreach (var p in list)
                if (string.Equals(p.m_name, name, StringComparison.OrdinalIgnoreCase)) { found = p; return true; }
            foreach (var p in list)
                if (p.m_name != null && p.m_name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { found = p; return true; }
            foreach (var p in list)
                if (p.m_name != null && p.m_name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { found = p; return true; }
            return false;
        }

        // Exact prefab id wins outright (that is what the completion inserts); otherwise the panel's own
        // relevance rank decides, shortest name breaking ties - the Items tab's ordering, reused.
        private ItemEntry PalFindItem(string q)
        {
            if (_itemIndex == null || string.IsNullOrEmpty(q)) return null;
            ItemEntry best = null;
            var bestRank = int.MaxValue;
            var bestLen = int.MaxValue;
            foreach (var e in _itemIndex)
            {
                if (string.Equals(e.Prefab, q, StringComparison.OrdinalIgnoreCase)) return e;
                if (e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Prefab.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var r = MatchRank(e.Display, e.Prefab, q);
                if (r > bestRank || (r == bestRank && e.Display.Length >= bestLen)) continue;
                best = e; bestRank = r; bestLen = e.Display.Length;
            }
            return best;
        }

        private CreatureEntry PalFindCreature(string q)
        {
            if (_creatureIndex == null || string.IsNullOrEmpty(q)) return null;
            CreatureEntry best = null;
            var bestRank = int.MaxValue;
            var bestLen = int.MaxValue;
            foreach (var e in _creatureIndex)
            {
                if (string.Equals(e.Name, q, StringComparison.OrdinalIgnoreCase)) return e;
                if (e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var r = MatchRank(e.Display, e.Name, q);
                if (r > bestRank || (r == bestRank && e.Display.Length >= bestLen)) continue;
                best = e; bestRank = r; bestLen = e.Display.Length;
            }
            return best;
        }

        // EnvSetup.m_name is a public field on EnvMan.m_environments (verified in the decompiled EnvMan), but
        // the whole read is guarded: a game update that reshapes it costs weather completion, nothing else.
        private List<string> PalWeatherNames()
        {
            if (_palWeatherNames != null) return _palWeatherNames;
            var list = new List<string>();
            try
            {
                var env = EnvMan.instance;
                if (env != null && env.m_environments != null)
                    foreach (var e in env.m_environments)
                    {
                        if (e == null || string.IsNullOrEmpty(e.m_name)) continue;
                        if (!list.Contains(e.m_name)) list.Add(e.m_name);
                    }
            }
            catch (Exception ex) { Logger.LogWarning("Palette: weather list unavailable: " + ex.Message); }
            _palWeatherNames = list;
            return list;
        }

        private static string PalSettingLabel(string key)
        {
            var s = Loc.T(key) ?? key;
            s = s.Replace("{0}", "").Replace("{1}", "");
            return s.Replace(":", "").Trim();
        }

        // ==================== candidate list (Layout only) ====================

        private List<PalRow> PalBuildRows(string raw)
        {
            var rows = new List<PalRow>();
            var input = raw ?? "";
            if (input.StartsWith("?", StringComparison.Ordinal))
            {
                PalBuildSearchRows(input.Substring(1).Trim(), rows);
                return rows;
            }

            var tokens = PalTokens(input);
            var trailingSpace = input.Length > 0 && char.IsWhiteSpace(input[input.Length - 1]);

            // Verb stage: nothing typed yet, or still typing the first word.
            if (tokens.Count == 0 || (tokens.Count == 1 && !trailingSpace))
            {
                var q = tokens.Count == 1 ? tokens[0] : "";
                foreach (var v in PalRankVerbs(q))
                {
                    if (rows.Count >= PalMaxRows) break;
                    rows.Add(new PalRow
                    {
                        Group = PalGroupCommand,
                        Kind = PalKindVerb,
                        Label = v.Verb,
                        Detail = v.Usage + "  -  " + Loc.T(v.HintKey),
                        Payload = v.Verb
                    });
                }
                return rows;
            }

            // Argument stage.
            var verb = PalFindVerb(tokens[0]);
            var frag = trailingSpace ? "" : tokens[tokens.Count - 1];
            var ordinal = trailingSpace ? tokens.Count : tokens.Count - 1;   // 1-based index of the argument being typed
            PalBuildArgRows(verb, ordinal, frag, rows);
            return rows;
        }

        private IEnumerable<PalVerb> PalRankVerbs(string q)
        {
            if (string.IsNullOrEmpty(q)) return _palVerbs;
            var hits = new List<PalVerb>();
            var ranks = new List<int>();
            foreach (var v in _palVerbs)
            {
                if (v.Verb.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && !PalFuzzy(v.Verb, q)) continue;
                var r = MatchRank(v.Verb, v.Verb, q);
                var at = ranks.Count;
                for (var i = 0; i < ranks.Count; i++) if (ranks[i] > r) { at = i; break; }
                ranks.Insert(at, r);
                hits.Insert(at, v);
            }
            return hits;
        }

        // Subsequence match ("kla" -> "kill-all"), used only as a fallback after plain substring matching so
        // the familiar exact/prefix ordering is never disturbed.
        private static bool PalFuzzy(string s, string q)
        {
            if (string.IsNullOrEmpty(q)) return true;
            if (string.IsNullOrEmpty(s)) return false;
            var qi = 0;
            for (var i = 0; i < s.Length && qi < q.Length; i++)
                if (char.ToLowerInvariant(s[i]) == char.ToLowerInvariant(q[qi])) qi++;
            return qi == q.Length;
        }

        private void PalBuildArgRows(PalVerb verb, int ordinal, string frag, List<PalRow> rows)
        {
            var name = verb != null ? verb.Verb.ToLowerInvariant() : "";
            var wantsPlayerToken = frag.StartsWith("@", StringComparison.Ordinal);
            var bare = wantsPlayerToken ? frag.Substring(1) : frag;

            switch (name)
            {
                case "give":
                    if (wantsPlayerToken || ordinal >= 3) PalAddPlayerRows(bare, rows, true);
                    else PalAddItemRows(bare, rows);
                    return;
                case "spawn":
                    if (ordinal <= 1) PalAddCreatureRows(bare, rows);
                    return;
                case "tp":
                    PalAddPlayerRows(bare, rows, false);
                    PalAddBookmarkRows(bare, rows);
                    return;
                case "heal":
                    if (rows.Count < PalMaxRows && ("all".IndexOf(bare, StringComparison.OrdinalIgnoreCase) >= 0 || bare.Length == 0))
                        rows.Add(new PalRow
                        {
                            Group = PalGroupPlayer,
                            Kind = PalKindArg,
                            Label = "@all",
                            Detail = Loc.T("ux.cmd_heal"),
                            Payload = "@all"
                        });
                    PalAddPlayerRows(bare, rows, true);
                    return;
                case "weather":
                    PalAddWeatherRows(bare, rows);
                    return;
                case "time":
                case "kill-all":
                case "skip-night":
                    return;
                case "god":
                case "fly":
                case "ghost":
                case "nostamina":
                    PalAddLiteralRows(new[] { "on", "off" }, bare, rows);
                    return;
                default:
                    // Unknown verb (typo) or one registered by another wave: offer everything and let the
                    // ranking sort it out.
                    PalAddItemRows(bare, rows);
                    PalAddCreatureRows(bare, rows);
                    PalAddPlayerRows(bare, rows, false);
                    return;
            }
        }

        private void PalAddLiteralRows(string[] options, string q, List<PalRow> rows)
        {
            foreach (var o in options)
            {
                if (rows.Count >= PalMaxRows) return;
                if (q.Length > 0 && o.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new PalRow { Group = PalGroupCommand, Kind = PalKindArg, Label = o, Detail = "", Payload = o });
            }
        }

        // Completions insert the PREFAB id, never the display name: prefab ids have no spaces, so the inserted
        // token can be re-parsed unambiguously, and PalFindItem short-circuits on an exact prefab match.
        private void PalAddItemRows(string q, List<PalRow> rows, int max = 6)
        {
            if (_itemIndex == null) return;
            var added = 0;
            foreach (var e in PalRankItems(q, max))
            {
                if (rows.Count >= PalMaxRows || added >= max) return;
                rows.Add(new PalRow
                {
                    Group = PalGroupItem,
                    Kind = PalKindArg,
                    Label = e.Display,
                    Detail = e.Prefab,
                    Payload = e.Prefab
                });
                added++;
            }
        }

        private void PalAddCreatureRows(string q, List<PalRow> rows, int max = 6)
        {
            if (_creatureIndex == null) return;
            var added = 0;
            foreach (var e in PalRankCreatures(q, max))
            {
                if (rows.Count >= PalMaxRows || added >= max) return;
                rows.Add(new PalRow
                {
                    Group = PalGroupCreature,
                    Kind = PalKindArg,
                    Label = e.Display,
                    Detail = e.Name,
                    Payload = e.Name
                });
                added++;
            }
        }

        private void PalAddPlayerRows(string q, List<PalRow> rows, bool atPrefix, int max = 4)
        {
            var added = 0;
            foreach (var p in OtherPlayers())
            {
                if (rows.Count >= PalMaxRows || added >= max) return;
                var n = p.m_name ?? "";
                if (q.Length > 0 && n.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // A name with spaces cannot round-trip through a whitespace-split line, so the completion
                // inserts the first word only - the resolver prefix-matches it back to the full name.
                var token = n;
                var sp = token.IndexOf(' ');
                if (sp > 0) token = token.Substring(0, sp);
                rows.Add(new PalRow
                {
                    Group = PalGroupPlayer,
                    Kind = PalKindArg,
                    Label = n,
                    Detail = string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}", p.m_position.x, p.m_position.z),
                    Payload = atPrefix ? "@" + token : token
                });
                added++;
            }
        }

        private void PalAddBookmarkRows(string q, List<PalRow> rows, int max = 4)
        {
            var added = 0;
            foreach (var kv in BookmarksKv())
            {
                if (rows.Count >= PalMaxRows || added >= max) return;
                if (q.Length > 0 && kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new PalRow
                {
                    Group = PalGroupPlayer,
                    Kind = PalKindArg,
                    Label = "bookmark:" + kv.Key,
                    Detail = kv.Value,
                    Payload = "bookmark:" + kv.Key
                });
                added++;
            }
        }

        private void PalAddWeatherRows(string q, List<PalRow> rows, int max = 8)
        {
            var added = 0;
            foreach (var w in PalWeatherNames())
            {
                if (rows.Count >= PalMaxRows || added >= max) return;
                if (q.Length > 0 && w.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new PalRow { Group = PalGroupCommand, Kind = PalKindArg, Label = w, Detail = "", Payload = w });
                added++;
            }
        }

        private List<ItemEntry> PalRankItems(string q, int max)
        {
            var hits = new List<ItemEntry>();
            var ranks = new List<int>();
            if (_itemIndex == null) return hits;
            foreach (var e in _itemIndex)
            {
                if (q.Length > 0 &&
                    e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Prefab.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var r = q.Length == 0 ? 5 : MatchRank(e.Display, e.Prefab, q);
                PalInsertRanked(hits, ranks, e, r, e.Display.Length, max);
                if (q.Length == 0 && hits.Count >= max) break;
            }
            return hits;
        }

        private List<CreatureEntry> PalRankCreatures(string q, int max)
        {
            var hits = new List<CreatureEntry>();
            var ranks = new List<int>();
            if (_creatureIndex == null) return hits;
            foreach (var e in _creatureIndex)
            {
                if (q.Length > 0 &&
                    e.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var r = q.Length == 0 ? 5 : MatchRank(e.Display, e.Name, q);
                PalInsertRanked(hits, ranks, e, r, e.Display.Length, max);
                if (q.Length == 0 && hits.Count >= max) break;
            }
            return hits;
        }

        // Bounded insertion sort: keeps the best <max> without sorting (and allocating) the whole index on
        // every keystroke. Rank first, then the shorter name - the Items tab's tie-break.
        private static void PalInsertRanked<T>(List<T> hits, List<int> ranks, T item, int rank, int len, int max)
        {
            var score = rank * 1000 + Mathf.Min(len, 999);
            var at = ranks.Count;
            for (var i = 0; i < ranks.Count; i++) if (ranks[i] > score) { at = i; break; }
            if (at >= max) return;
            ranks.Insert(at, score);
            hits.Insert(at, item);
            if (ranks.Count > max) { ranks.RemoveAt(ranks.Count - 1); hits.RemoveAt(hits.Count - 1); }
        }

        // ---- global search ('?' prefix): one query, results grouped by where they live ----
        private void PalBuildSearchRows(string q, List<PalRow> rows)
        {
            if (q.Length == 0) return;

            foreach (var e in PalRankItems(q, 3))
                rows.Add(new PalRow
                {
                    Group = PalGroupItem,
                    Kind = PalKindJumpItems,
                    Label = e.Display,
                    Detail = e.Prefab,
                    Payload = e.Display
                });

            foreach (var e in PalRankCreatures(q, 3))
                rows.Add(new PalRow
                {
                    Group = PalGroupCreature,
                    Kind = PalKindJumpCreatures,
                    Label = e.Display,
                    Detail = e.Name,
                    Payload = e.Display
                });

            var players = 0;
            foreach (var p in OtherPlayers())
            {
                if (players >= 2) break;
                var n = p.m_name ?? "";
                if (n.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new PalRow
                {
                    Group = PalGroupPlayer,
                    Kind = PalKindJumpPlayers,
                    Label = n,
                    Detail = string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}", p.m_position.x, p.m_position.z),
                    Payload = n
                });
                players++;
            }

            var settings = 0;
            foreach (var key in PalSettingKeys)
            {
                if (settings >= 2) break;
                var label = PalSettingLabel(key);
                if (label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new PalRow
                {
                    Group = PalGroupSetting,
                    Kind = PalKindJumpSettings,
                    Label = label,
                    Detail = Loc.T("tab.settings"),
                    Payload = label
                });
                settings++;
            }

            var cmds = 0;
            foreach (var v in PalRankVerbs(q))
            {
                if (cmds >= 2) break;
                rows.Add(new PalRow
                {
                    Group = PalGroupCommand,
                    Kind = PalKindPickCommand,
                    Label = v.Verb,
                    Detail = v.Usage,
                    Payload = v.Verb
                });
                cmds++;
            }

            if (rows.Count > PalMaxRows) rows.RemoveRange(PalMaxRows, rows.Count - PalMaxRows);
        }

        private static List<string> PalTokens(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var t in s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) list.Add(t);
            return list;
        }

        private static string PalGroupLabel(int group)
        {
            switch (group)
            {
                case PalGroupItem: return Loc.T("ux.grp_items");
                case PalGroupCreature: return Loc.T("ux.grp_creatures");
                case PalGroupPlayer: return Loc.T("ux.grp_players");
                case PalGroupSetting: return Loc.T("ux.grp_settings");
                default: return Loc.T("ux.grp_commands");
            }
        }

        // ==================== overlay ====================

        // Called from FeaturesOnGUI on EVERY OnGUI pass, panel open or closed.
        internal void PalOnGUI()
        {
            if (!_palOpen) return;
            EnsureSkin();
            ApplyFont();

            if (Event.current.type == EventType.Layout)
            {
                if (_palRowsLayout == null || _palRowsKey != _palInput)
                {
                    _palRowsLayout = PalBuildRows(_palInput);
                    _palRowsKey = _palInput;
                }
                if (_palSel >= _palRowsLayout.Count) _palSel = Mathf.Max(0, _palRowsLayout.Count - 1);
                if (_palSel < 0) _palSel = 0;
                _palSelLayout = _palSel;
            }
            if (_palRowsLayout == null) _palRowsLayout = new List<PalRow>();

            // Height comes from the pinned row count, so it is identical on Layout and Repaint.
            var rowH = 26f + Mathf.Max(0, _fontSizeLive - 13) * 2f;
            var w = Mathf.Min(780f, Mathf.Max(360f, Screen.width - 80f));
            var h = Mathf.Min(Screen.height - 80f, 116f + _palRowsLayout.Count * rowH);
            var rect = new Rect(Mathf.Max(0f, (Screen.width - w) * 0.5f),
                                Mathf.Min(100f, Mathf.Max(0f, Screen.height * 0.12f)), w, h);

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = PanelBgTint;
            GUI.Window(PalWindowId, rect, PalDrawWindow, Loc.T("ux.pal_title"), _windowStyle);
            GUI.backgroundColor = prevBg;

            if (_palJustOpened && Event.current.type == EventType.Repaint)
            {
                _palJustOpened = false;
                GUI.BringWindowToFront(PalWindowId);
                GUI.FocusWindow(PalWindowId);
            }

            // Deferred close: Escape (and a command/jump) only REQUEST it, so the pass that handled the key
            // still draws the identical control set. From the next pass the window simply isn't drawn -
            // the same thing the main panel's close button does with _visible.
            if (_palCloseRequested)
            {
                _palCloseRequested = false;
                PalCloseNow();
            }
        }

        private void PalDrawWindow(int id)
        {
            GUI.backgroundColor = Color.white;   // mandatory first line: undo PanelBgTint for the contents

            PalHandleKeys();

            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            var searchMode = (_palInput ?? "").StartsWith("?", StringComparison.Ordinal);
            GUILayout.Label(searchMode ? Loc.T("ux.pal_prompt_search") : Loc.T("ux.pal_prompt_cmd"),
                _labelStyle, GUILayout.MinWidth(70));
            GUI.SetNextControlName("apPalInput");
            _palInput = GUILayout.TextField(_palInput ?? "", _textFieldStyle);
            GUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
            {
                if (_palFocusField)
                {
                    _palFocusField = false;
                    GUI.FocusControl("apPalInput");
                }
                if (_palCaretToEnd)
                {
                    _palCaretToEnd = false;
                    // After a programmatic completion the editor still holds the old string/caret; push the
                    // caret to the end so the next keystroke appends. Purely cosmetic - guarded because the
                    // editor state object is an engine internal.
                    try
                    {
                        // Cast through object on purpose: GetStateObject's declared return type differs
                        // between Unity versions, and going via object keeps this compiling either way.
                        object state = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                        var te = state as TextEditor;
                        if (te != null)
                        {
                            te.text = _palInput ?? "";
                            te.MoveTextEnd();
                        }
                    }
                    catch { /* caret placement is cosmetic */ }
                }
            }

            GUILayout.Label(Loc.T("ux.pal_hint"), _hintStyle);

            var rows = _palRowsLayout;
            if (rows.Count == 0)
            {
                GUILayout.Label(Loc.T("ux.pal_no_match"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var selected = i == _palSelLayout;
                    var prev = GUI.contentColor;
                    if (selected) GUI.contentColor = new Color(1f, 0.92f, 0.62f);
                    var text = (selected ? "> " : "   ") + r.Label +
                               "   [" + PalGroupLabel(r.Group) + "]" +
                               (string.IsNullOrEmpty(r.Detail) ? "" : "   " + r.Detail);
                    // One button per row: clicking activates it (the overlay frees the cursor while the
                    // panel is closed), keyboard users never touch it.
                    var pressed = GUILayout.Button(text, _buttonStyle, GUILayout.ExpandWidth(true));
                    GUI.contentColor = prev;
                    if (pressed) { _palSel = i; _palSelLayout = i; PalActivate(); }
                }
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // Keys are read at the TOP of the window callback and consumed, so Up/Down never move the text
        // caret, Tab never shifts IMGUI focus and Enter never reaches the game.
        private void PalHandleKeys()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            var rows = _palRowsLayout;
            var count = rows != null ? rows.Count : 0;
            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    _palCloseRequested = true;
                    e.Use();
                    break;
                case KeyCode.UpArrow:
                    if (count > 0) _palSel = (_palSel - 1 + count) % count;
                    e.Use();
                    break;
                case KeyCode.DownArrow:
                    if (count > 0) _palSel = (_palSel + 1) % count;
                    e.Use();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    PalActivate();
                    e.Use();
                    break;
                case KeyCode.Tab:
                    PalComplete();
                    e.Use();
                    break;
            }
        }

        // Enter. In search mode it jumps; in command mode it RUNS the line when the line already names a
        // known verb and has everything that verb needs, and otherwise completes the selection (so "gi" +
        // Enter becomes "give ", while "god" + Enter fires immediately).
        private void PalActivate()
        {
            var rows = _palRowsLayout;
            var input = _palInput ?? "";
            var sel = rows != null && rows.Count > 0 ? rows[Mathf.Clamp(_palSelLayout, 0, rows.Count - 1)] : null;

            if (input.StartsWith("?", StringComparison.Ordinal))
            {
                if (sel == null) return;
                if (sel.Kind == PalKindPickCommand)
                {
                    // Stay open, drop out of search mode with the verb already typed.
                    _palInput = sel.Payload + " ";
                    _palSel = 0;
                    _palRowsKey = null;
                    _palCaretToEnd = true;
                    return;
                }
                _palPendingJump = sel.Kind;
                _palPendingJumpArg = sel.Payload;
                _palCloseRequested = true;
                return;
            }

            var tokens = PalTokens(input);
            if (tokens.Count > 0)
            {
                var verb = PalFindVerb(tokens[0]);
                var needsArgs = verb != null && verb.Usage.IndexOf('<') >= 0;
                if (verb != null && (tokens.Count > 1 || !needsArgs))
                {
                    _palPendingRun = input;
                    _palCloseRequested = true;
                    return;
                }
            }
            PalComplete();
        }

        // Tab. Replaces the fragment being typed (or appends after a trailing space) with the selected row.
        private void PalComplete()
        {
            var rows = _palRowsLayout;
            if (rows == null || rows.Count == 0) return;
            var r = rows[Mathf.Clamp(_palSelLayout, 0, rows.Count - 1)];
            if (string.IsNullOrEmpty(r.Payload)) return;
            var input = _palInput ?? "";

            if (r.Kind == PalKindVerb || r.Kind == PalKindPickCommand)
            {
                _palInput = r.Payload + " ";
            }
            else if (r.Kind == PalKindArg)
            {
                var trailingSpace = input.Length > 0 && char.IsWhiteSpace(input[input.Length - 1]);
                if (trailingSpace) _palInput = input + r.Payload + " ";
                else
                {
                    var cut = input.LastIndexOf(' ');
                    _palInput = (cut < 0 ? "" : input.Substring(0, cut + 1)) + r.Payload + " ";
                }
            }
            else return;   // jump rows are not completions

            _palSel = 0;
            _palRowsKey = null;   // force a rebuild on the next Layout pass
            _palCaretToEnd = true;
        }

        private void PalOpenNow()
        {
            if (LocalPlayer == null) return;   // the palette acts on the world; it is meaningless in the menu
            _palOpen = true;
            _palJustOpened = true;
            _palFocusField = true;
            _palInput = "";
            _palSel = 0;
            _palSelLayout = 0;
            _palRowsLayout = null;
            _palRowsKey = null;
            _palWeatherNames = null;
            // The palette can be the FIRST thing used in a session, before the panel was ever opened, so the
            // item/creature indices may not exist yet. Runs in LateUpdate, never inside OnGUI.
            if ((_itemIndex == null || _creatureIndex == null) &&
                (ObjectDB.instance != null || ZNetScene.instance != null))
                RefreshCaches();
        }

        private void PalCloseNow()
        {
            _palOpen = false;
            _palJustOpened = false;
            _palFocusField = false;
            _palCaretToEnd = false;
            _palInput = "";
            _palSel = 0;
            _palSelLayout = 0;
            _palRowsLayout = null;
            _palRowsKey = null;
        }

        // ==================== tick: hotkey, deferred work, macro runner ====================

        internal void PalTick()
        {
            if (!_palInited) return;

            // Hotkey. Disabled by default (KeyCode.None) and suppressed while the Settings tab listens for a
            // rebind, exactly like the panel's own hotkeys.
            var key = _palKeyCfg != null ? _palKeyCfg.Value : KeyCode.None;
            if (key != KeyCode.None && _rebindTarget == 0 && Input.GetKeyDown(key))
            {
                if (_palOpen) PalCloseNow();
                else PalOpenNow();
            }

            if (_palOpenRequested)
            {
                _palOpenRequested = false;
                if (!_palOpen) PalOpenNow();
            }
            if (_palCloseRequested && _palOpen)
            {
                _palCloseRequested = false;
                PalCloseNow();
            }
            // The world went away underneath an open palette (logout races the reset).
            if (_palOpen && LocalPlayer == null) PalCloseNow();

            if (_palPendingJump != 0)
            {
                var kind = _palPendingJump;
                var arg = _palPendingJumpArg;
                _palPendingJump = 0;
                _palPendingJumpArg = null;
                PalApplyJump(kind, arg);
            }

            if (_palPendingRun != null)
            {
                var line = _palPendingRun;
                _palPendingRun = null;
                PalRunLine(line);
            }

            PalTickMacro();
        }

        // Global-search jump. Runs OUTSIDE OnGUI on purpose: flipping _visible or _tab mid-frame would hand
        // the main window a pass whose layout was never built.
        private void PalApplyJump(int kind, string arg)
        {
            var q = arg ?? "";
            switch (kind)
            {
                case PalKindJumpItems:
                    _tab = 0;
                    _mainCat = "All";
                    _subCat = "All";
                    _itemSearch = q;
                    _itemScroll = Vector2.zero;
                    break;
                case PalKindJumpCreatures:
                    _tab = 1;
                    _creatureCat = "All";
                    _creatureSearch = q;
                    _creatureScroll = Vector2.zero;
                    break;
                case PalKindJumpPlayers:
                    _tab = 5;
                    _playersScroll = Vector2.zero;
                    break;
                case PalKindJumpSettings:
                    _tab = 7;
                    _settingsScroll = Vector2.zero;
                    break;
                default:
                    return;
            }
            _openDropdown = null;
            _rebindTarget = 0;
            if (!_visible)
            {
                _visible = true;
                Loc.Refresh(_languageCfg.Value);
                RefreshCaches();
            }
        }

        private bool PalRunLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var tokens = PalTokens(line);
            if (tokens.Count == 0) return false;
            var verb = PalFindVerb(tokens[0]);
            if (verb == null) { Message(Loc.T("ux.err_unknown_cmd", tokens[0])); return false; }
            var args = tokens.GetRange(1, tokens.Count - 1).ToArray();
            try { return verb.Run(args); }
            catch (Exception e)
            {
                Logger.LogWarning("Palette command '" + verb.Verb + "' failed: " + e.Message);
                Message(Loc.T("ux.err_failed", verb.Verb));
                return false;
            }
        }

        // ==================== macros ====================

        // Same cache-against-the-raw-string idiom as BookmarksKv: any edit changes the string, which
        // invalidates the parse with no explicit bookkeeping.
        private Dictionary<string, string> PalMacros()
        {
            var raw = _palMacrosCfg != null ? (_palMacrosCfg.Value ?? "") : "";
            if (_palMacroKv == null || _palMacroRaw != raw) { _palMacroKv = ParseKv(raw); _palMacroRaw = raw; }
            return _palMacroKv;
        }

        // Steps live inside one ParseKv VALUE, separated by ';'. ParseKv/JoinKv already percent-escape
        // '%', '|' and '=' (EncKv/DecKv); ';' gets the same treatment here so a step containing a semicolon
        // cannot split itself. The two layers are symmetric: JoinKv escapes what this wrote, ParseKv undoes
        // exactly that, then PalDecStep undoes this layer.
        private static string PalEncStep(string s) =>
            string.IsNullOrEmpty(s) ? "" : EncKv(s).Replace(";", "%3B");

        private static string PalDecStep(string s) =>
            string.IsNullOrEmpty(s) ? "" : DecKv(s.Replace("%3B", ";"));

        private static List<string> PalStepsOf(string value)
        {
            var steps = new List<string>();
            if (string.IsNullOrEmpty(value)) return steps;
            foreach (var part in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = PalDecStep(part).Trim();
                if (s.Length > 0) steps.Add(s);
            }
            return steps;
        }

        private static string PalJoinSteps(List<string> steps)
        {
            var encoded = new List<string>(steps.Count);
            foreach (var s in steps)
            {
                var t = (s ?? "").Trim();
                if (t.Length > 0) encoded.Add(PalEncStep(t));
            }
            return string.Join(";", encoded.ToArray());
        }

        private void PalSaveMacro(string name, List<string> steps)
        {
            var macros = ParseKv(_palMacrosCfg.Value ?? "");
            macros[name] = PalJoinSteps(steps);
            _palMacrosCfg.Value = JoinKv(macros);
            Config.Save();
            Message(Loc.T("ux.msg_macro_saved", name));
        }

        private void PalDeleteMacro(string name)
        {
            var macros = ParseKv(_palMacrosCfg.Value ?? "");
            if (!macros.Remove(name)) return;
            _palMacrosCfg.Value = JoinKv(macros);
            Config.Save();
            if (string.Equals(_palRunName, name, StringComparison.Ordinal)) PalStopMacro(false);
            Message(Loc.T("ux.msg_macro_deleted", name));
        }

        // Only ARMS the run; PalTickMacro drives it one step per 250 ms so a long macro can never block a
        // frame (and so each step's server RPC gets its own tick).
        private void PalStartMacro(string name)
        {
            if (!PalMacros().TryGetValue(name, out var value)) return;
            var steps = PalStepsOf(value);
            if (steps.Count == 0) { Message(Loc.T("ux.msg_macro_need_step")); return; }
            _palQueue = steps;
            _palRunName = name;
            _palRunStep = 0;
            _palRunTotal = steps.Count;
            _palNextStepAt = Time.time;   // first step on the next tick
            Message(Loc.T("ux.msg_macro_started", name, steps.Count));
        }

        private void PalStopMacro(bool announce)
        {
            _palQueue = null;
            _palRunName = null;
            _palRunStep = 0;
            _palRunTotal = 0;
            if (announce) Message(Loc.T("ux.msg_macro_stopped"));
        }

        private void PalTickMacro()
        {
            if (_palQueue == null) return;
            if (LocalPlayer == null) { PalStopMacro(false); return; }
            if (Time.time < _palNextStepAt) return;

            if (_palQueue.Count == 0)
            {
                var done = _palRunName;
                PalStopMacro(false);
                Message(Loc.T("ux.msg_macro_done", done));
                return;
            }

            var step = _palQueue[0];
            _palQueue.RemoveAt(0);
            _palRunStep++;
            var stepNo = _palRunStep;
            var name = _palRunName;
            if (!PalRunLine(step))
            {
                PalStopMacro(false);
                Message(Loc.T("ux.msg_macro_failed", name, stepNo, step));
                return;
            }
            _palNextStepAt = Time.time + PalStepGap;
        }

        // ==================== Extras-tab section ====================

        internal void DrawMacroSection()
        {
            // Config-backed row list + the running-macro banner both change control counts, so both are
            // pinned on Layout (a [Delete] click rewrites the config during the event pass).
            if (Event.current.type == EventType.Layout)
            {
                _palMacroRowsLayout = new List<KeyValuePair<string, string>>(PalMacros());
                _palRunningLayout = _palQueue != null;
                _palRunNameLayout = _palRunName;
                _palRunStepLayout = _palRunStep;
                _palRunTotalLayout = _palRunTotal;
            }

            _palSectionScroll = GUILayout.BeginScrollView(_palSectionScroll, GUILayout.Height(ListView(150f)));
            PalDrawPaletteCard();
            PalDrawMacroListCard();
            PalDrawMacroEditorCard();
            GUILayout.EndScrollView();
        }

        private void PalDrawPaletteCard()
        {
            BeginCard(Loc.T("ux.pal_section"));
            var key = _palKeyCfg != null ? _palKeyCfg.Value : KeyCode.None;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ux.pal_key", key == KeyCode.None ? Loc.T("ux.pal_key_none") : key.ToString()),
                _labelStyle, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("ux.pal_open"), _buttonStyle, GUILayout.MinWidth(120)))
                _palOpenRequested = true;   // applied in PalTick: never open a window from inside OnGUI
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("ux.pal_key_hint"), _hintStyle);
            GUILayout.Label(Loc.T("ux.pal_search_hint"), _hintStyle);
            GUILayout.Label(Loc.T("ux.pal_cmds", _palVerbs.Count), _dimLabelStyle);
            EndCard();
        }

        private void PalDrawMacroListCard()
        {
            BeginCard(Loc.T("ux.macro_section"));
            GUILayout.Label(Loc.T("ux.macro_hint"), _hintStyle);

            if (_palRunningLayout)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("ux.macro_running", _palRunNameLayout ?? "", _palRunStepLayout, _palRunTotalLayout),
                    _headerStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("ux.macro_stop"), _buttonStyle, GUILayout.MinWidth(70)))
                    PalStopMacro(true);
                GUILayout.EndHorizontal();
            }

            var rows = _palMacroRowsLayout;
            if (rows == null || rows.Count == 0)
            {
                GUILayout.Label(Loc.T("ux.macro_empty"), _hintStyle);
                EndCard();
                return;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var kv = rows[i];
                var steps = PalStepsOf(kv.Value);
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(kv.Key, _cellStyle, GUILayout.MinWidth(120));
                GUILayout.Label(Loc.T("ux.macro_steps", steps.Count), _dimCellStyle, GUILayout.MinWidth(70));
                GUILayout.Label(steps.Count > 0 ? steps[0] : "", _dimCellStyle, GUILayout.MinWidth(140));
                GUILayout.FlexibleSpace();
                // Running a macro fires a burst of admin actions, so it asks twice - per-row id so arming one
                // row can never arm another.
                if (ConfirmButton("pal:run:" + kv.Key, Loc.T("ux.macro_run"), GUILayout.MinWidth(70)))
                    PalStartMacro(kv.Key);
                if (GUILayout.Button(Loc.T("ux.macro_load"), _buttonStyle, GUILayout.MinWidth(60)))
                {
                    _palEditName = kv.Key;
                    for (var s = 0; s < _palEditSteps.Length; s++)
                        _palEditSteps[s] = s < steps.Count ? steps[s] : "";
                }
                if (ConfirmButton("pal:del:" + kv.Key, Loc.T("ux.macro_del"), GUILayout.MinWidth(70)))
                    PalDeleteMacro(kv.Key);
                GUILayout.EndHorizontal();
            }
            EndCard();
        }

        private void PalDrawMacroEditorCard()
        {
            BeginCard(Loc.T("ux.macro_edit_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ux.macro_name"), _labelStyle, GUILayout.MinWidth(60));
            _palEditName = GUILayout.TextField(_palEditName ?? "", _textFieldStyle, GUILayout.MinWidth(160));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("ux.macro_save"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                var name = (_palEditName ?? "").Trim();
                if (name.Length == 0) Message(Loc.T("ux.msg_macro_need_name"));
                else
                {
                    var steps = new List<string>();
                    foreach (var s in _palEditSteps)
                    {
                        var t = (s ?? "").Trim();
                        if (t.Length > 0) steps.Add(t);
                    }
                    if (steps.Count == 0) Message(Loc.T("ux.msg_macro_need_step"));
                    else PalSaveMacro(name, steps);
                }
            }
            if (GUILayout.Button(Loc.T("ux.macro_clear"), _buttonStyle, GUILayout.MinWidth(70)))
            {
                _palEditName = "";
                for (var s = 0; s < _palEditSteps.Length; s++) _palEditSteps[s] = "";
            }
            GUILayout.EndHorizontal();

            // Eight fixed slots, always drawn: a "add another step" button would change the control count
            // between passes, which is the one thing IMGUI cannot survive. Empty slots are simply skipped
            // when saving.
            for (var i = 0; i < _palEditSteps.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("ux.macro_step", i + 1), _labelStyle, GUILayout.MinWidth(60));
                _palEditSteps[i] = GUILayout.TextField(_palEditSteps[i] ?? "", _textFieldStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(Loc.T("ux.macro_edit_hint"), _hintStyle);
            EndCard();
        }
    }
}
