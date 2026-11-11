using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AdminPanel
{
    /// <summary>
    /// Panel-chrome localization (tab names, buttons, messages).
    ///
    /// This is deliberately NOT Valheim's Localization system: that one is keyed on the game's own tokens and
    /// is already used via AdminPanelPlugin.LocalizeSafe for *content* (item/creature/status-effect names), which
    /// follows the player's game language for free. What is missing is the panel's own chrome, which this covers.
    ///
    /// Format is line-based `key=value`, not JSON, for two reasons: net48 has no built-in JSON parser and the mod
    /// ships no dependencies (the "just two DLLs" promise), and translators can edit these in Notepad without
    /// tripping over punctuation. Blank lines and lines starting with '#' are ignored. The first '=' splits the
    /// pair, so values may contain '='. Use \n for a line break in a value.
    /// </summary>
    internal static class Loc
    {
        // English is loaded once and kept as the fallback so a partial translation degrades to English per-key
        // rather than showing a raw token. Every lookup that misses _active falls through to _fallback.
        private static readonly Dictionary<string, string> Fallback = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _active = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string _activeCode = "en";
        private static bool _loaded;

        /// <summary>Language code actually in use after resolution (e.g. "de"). "en" when nothing else matched.</summary>
        internal static string ActiveCode => _activeCode;

        /// <summary>Set by ResolveFontSafety when the chosen language needs glyphs the panel font cannot draw.</summary>
        internal static bool NeedsFallbackFont { get; private set; }

        // Shipped translations. Keep in sync with the embedded resources in the .csproj.
        // Latin scripts plus Russian: Valheim's Norsebold font (what "Norse (auto)" picks) carries the Russian
        // alphabet (U+0410-044F, Ё/ё - see tools\font-glyphs.ps1), so Russian needs no font work either. With a
        // font that lacks it, ResolveFontSafety moves the panel to Unity's default font as for any other script.
        internal static readonly string[] Shipped = { "en", "de", "fr", "es", "it", "pt-BR", "pl", "nl", "sv", "ru" };

        // Human-readable names for the Settings dropdown, in Shipped order, prefixed by the auto option.
        internal static readonly string[] MenuNames =
        {
            "Auto (game language)", "English", "Deutsch", "Français", "Español", "Italiano",
            "Português (BR)", "Polski", "Nederlands", "Svenska", "Русский (Russian)"
        };

        // Valheim reports its language as an English name (Localization.GetSelectedLanguage() -> "German").
        // Map only what we ship; anything else falls through to English.
        private static readonly Dictionary<string, string> GameLangToCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "English", "en" },
            { "German", "de" },
            { "French", "fr" },
            { "Spanish", "es" },
            { "Italian", "it" },
            { "Portuguese_Brazilian", "pt-BR" },
            { "Portuguese_European", "pt-BR" },   // closest shipped match; better than dropping to English
            { "Polish", "pl" },
            { "Dutch", "nl" },
            { "Swedish", "sv" },
            { "Russian", "ru" },
        };

        /// <summary>
        /// Resolve the language and load its table. Safe to call repeatedly — it early-outs unless the resolved
        /// code changed, so it can sit on the panel-open path without rescanning resources every frame.
        /// <paramref name="configChoice"/> is the Settings value: "Auto (game language)" or a MenuNames entry.
        /// </summary>
        internal static void Refresh(string configChoice)
        {
            _choice = configChoice;
            if (Fallback.Count == 0) LoadInto(Fallback, "en");   // once; English is the permanent safety net

            var want = ResolveCode(configChoice);
            if (_loaded && want == _activeCode) return;

            _activeCode = want;
            _active = want == "en" ? Fallback : LoadNew(want);
            _loaded = true;
        }

        // The Settings choice as last handed to Refresh (or Prime). T() uses it to load the tables on demand.
        private static string _choice;
        // True while "Auto" could not ask the game for its language yet (Localization.instance was null).
        private static bool _autoPending;

        /// <summary>Remember the Settings choice at startup, without loading anything yet.</summary>
        internal static void Prime(string configChoice) => _choice = configChoice;

        // Refresh used to run only when the panel opened. Everything that speaks BEFORE the first open - the
        // map-teleport hotkey, the palette, a server reply - then found both tables empty and showed its raw
        // key ("[Admin] world.msg_tp_to"). So the first lookup loads the tables itself, and a lookup made
        // after the game's own language became readable re-resolves "Auto" once.
        private static void EnsureLoaded()
        {
            if (_loaded && !(_autoPending && GameUiReady())) return;
            try { Refresh(_choice); }
            catch (Exception e)
            {
                _autoPending = false;
                Debug.LogWarning($"[AdminPanel] locale load failed: {e.Message}");
            }
            _loaded = true;   // even on failure: never retry per lookup (OnGUI calls T() thousands of times a frame)
        }

        // The main menu or a world exists, i.e. the game has built its own Localization. Plain static reads.
        private static bool GameUiReady() => FejdStartup.instance != null || ZNet.instance != null;

        private static string ResolveCode(string configChoice)
        {
            _autoPending = false;
            // Explicit override wins. MenuNames[0] is the auto option, so index i>0 maps to Shipped[i-1].
            if (!string.IsNullOrEmpty(configChoice))
            {
                var idx = Array.IndexOf(MenuNames, configChoice);
                if (idx > 0 && idx - 1 < Shipped.Length) return Shipped[idx - 1];
            }

            // Auto: follow the game. Localization.instance builds itself on first access, so it must not be
            // touched before the game's own startup has (a lookup can now come from anywhere, not only from
            // the panel opening). Until the main menu or a world exists this stays English and is asked again.
            if (!GameUiReady()) { _autoPending = true; return "en"; }
            try
            {
                var lang = Localization.instance != null ? Localization.instance.GetSelectedLanguage() : null;
                if (!string.IsNullOrEmpty(lang) && GameLangToCode.TryGetValue(lang, out var code)) return code;
            }
            catch { /* never let a language lookup break the panel */ }

            return "en";
        }

        private static Dictionary<string, string> LoadNew(string code)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            LoadInto(dict, code);
            return dict;
        }

        /// <summary>
        /// Fill <paramref name="dict"/> from the embedded table for <paramref name="code"/>, then overlay an
        /// optional on-disk file. The disk overlay exists so a translator can iterate without rebuilding the DLL
        /// (and so a server owner can correct a phrase locally); it wins over the embedded copy key-by-key.
        /// </summary>
        private static void LoadInto(Dictionary<string, string> dict, string code)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                // Resource names are namespace-qualified by the SDK: AdminPanel.Localization.de.txt
                var res = asm.GetManifestResourceNames()
                             .FirstOrDefault(n => n.EndsWith("Localization." + code + ".txt", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        Parse(r.ReadToEnd(), dict);
            }
            catch (Exception e) { Debug.LogWarning($"[AdminPanel] embedded locale '{code}' failed to load: {e.Message}"); }

            try
            {
                var dir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                                       "AdminPanel_Localization");
                var file = Path.Combine(dir, code + ".txt");
                if (File.Exists(file)) Parse(File.ReadAllText(file, Encoding.UTF8), dict);
            }
            catch (Exception e) { Debug.LogWarning($"[AdminPanel] disk locale '{code}' failed to load: {e.Message}"); }
        }

        private static void Parse(string text, Dictionary<string, string> dict)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                if (key.Length > 0) dict[key] = val;
            }
        }

        /// <summary>
        /// Look up <paramref name="key"/>. Falls back to English, then to the key itself — a missing key is
        /// visible in the UI rather than blank, which is what you want while a translation is being written.
        /// </summary>
        internal static string T(string key)
        {
            if (!_loaded || _autoPending) EnsureLoaded();
            if (_active.TryGetValue(key, out var v)) return v;
            if (Fallback.TryGetValue(key, out var f)) return f;
            return key;
        }

        /// <summary>
        /// Formatted lookup. Values use {0}/{1} placeholders — never string interpolation at the call site,
        /// because word order differs between languages and a translator has to be able to move the slots.
        /// A bad format string in a community translation must not take the panel down, so it degrades to the
        /// unformatted text instead of throwing mid-OnGUI (which would tear the whole IMGUI frame).
        /// </summary>
        internal static string T(string key, params object[] args)
        {
            var raw = T(key);
            if (args == null || args.Length == 0) return raw;
            try { return string.Format(raw, args); }
            catch (FormatException)
            {
                Debug.LogWarning($"[AdminPanel] locale '{_activeCode}' key '{key}' has a bad format string");
                return raw;
            }
        }

        /// <summary>
        /// Decide whether <paramref name="font"/> can actually draw the active language plus the game-content
        /// names the panel shows (item/creature names come from Valheim and follow the *game* language, which
        /// can be Russian or Chinese even when the panel chrome is English).
        ///
        /// This is why it samples live content rather than only our own table: the panel has always had a latent
        /// hole where a non-Latin game language renders as blank boxes in the Norse IMGUI font. Returning true
        /// tells the caller to use Unity's default font, which has far broader coverage.
        /// </summary>
        internal static bool ResolveFontSafety(Font font, IEnumerable<string> contentSamples)
        {
            NeedsFallbackFont = false;
            if (font == null) return false;

            try
            {
                // Only dynamic fonts can be probed meaningfully; a bitmap font reports what it baked.
                foreach (var s in EnumerateProbeText(contentSamples))
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    foreach (var c in s)
                    {
                        if (c < 0x0080) continue;                 // ASCII is safe in every font we ship with
                        if (char.IsWhiteSpace(c)) continue;
                        if (!font.HasCharacter(c)) { NeedsFallbackFont = true; return true; }
                    }
                }
            }
            catch { /* HasCharacter can throw on odd font assets; assume the font is fine rather than fighting it */ }

            return false;
        }

        private static IEnumerable<string> EnumerateProbeText(IEnumerable<string> contentSamples)
        {
            foreach (var v in _active.Values) yield return v;
            if (contentSamples == null) yield break;
            foreach (var s in contentSamples) yield return s;
        }
    }
}
