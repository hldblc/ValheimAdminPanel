using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Feature-module core (additive; owns the Extras tab) ====================
    // Every backlog feature lives in Features\*.cs as another partial of AdminPanelPlugin and registers a
    // section here. The main file only carries eight one-line hooks; with no wave files compiled in, all
    // partial-method calls below vanish at compile time and the panel is byte-for-byte today's behavior.
    public partial class AdminPanelPlugin
    {
        // One section = one chip inside the Extras tab. Sections draw with the shared styles/card API, so
        // they inherit the runtime skin with no extra work. Draw runs inside DrawWindow's try/catch.
        internal sealed class FeatureSection
        {
            public string Id;             // stable English id — never localized (state key, config key)
            public string LocKey;         // chip label locale key
            public Action Draw;
            public Func<bool> Enabled;    // section hidden when false (re-check each Layout pass)
        }

        // The Tools tab (2.5.5: one tab, was Extras + Tools) is a category row over a section row over the
        // section body. Categories are fixed here, in display order, and own the order of their sections;
        // a section a wave registers under an id no category lists lands in the last category so it is
        // never silently lost. Category ids are config/state keys and never localized.
        internal sealed class FeatureCategory
        {
            public string Id;
            public string LocKey;
            public string[] Sections;   // section ids, in display order
        }

        private static readonly FeatureCategory[] FeatureCategories =
        {
            new FeatureCategory { Id = "moderation", LocKey = "feat.cat.moderation",
                Sections = new[] { "Moderation", "RapSheet", "Roles", "Audit", "Guard", "StaffChat", "DirectMessage" } },
            new FeatureCategory { Id = "server", LocKey = "feat.cat.server",
                Sections = new[] { "ServerTools", "Diagnostics", "Discord", "ClientPerf", "Extensions" } },
            new FeatureCategory { Id = "players", LocKey = "feat.cat.players",
                Sections = new[] { "PlayerData", "Economy", "Bounties", "DeathRules", "SkillRules", "MapReveal", "TraderStock", "Blacklist", "ItemForge" } },
            new FeatureCategory { Id = "world", LocKey = "feat.cat.world",
                Sections = new[] { "AreaTools", "BuildTools", "LocationFinder", "Spawners", "Containers", "TameRoster", "CreatureEditor", "MapPins", "RaidComposer" } },
            new FeatureCategory { Id = "shortcuts", LocKey = "feat.cat.shortcuts",
                Sections = new[] { "Macros", "Workflow" } },
        };

        private ConfigEntry<bool> _extrasTabCfg;   // config key kept as EnableExtrasTab (2.5.x files stay valid)
        private readonly List<FeatureSection> _featureSections = new List<FeatureSection>();
        private bool _featuresInited;

        // Tools tab index sits after the fixed tabs; the switch in DrawWindow routes it via default:.
        private static int FeatureTabIndex => TabKeys.Length;

        // Selection follows the Items/Player-tab snapshot discipline: the live fields flip on the event pass,
        // the Layout snapshots are what every pass of the same frame draws from. The chip is remembered per
        // category so switching categories and back lands on the section the admin was using.
        private string _featureCat;
        private string _featureCatLayout;
        private readonly Dictionary<string, string> _featureChip = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _featureChipLayout;
        private bool _featureTabVisibleLayout;
        private List<FeatureCategory> _featureCatsLayout;        // categories with at least one enabled section
        private List<FeatureSection> _featureSectionsLayout;     // enabled sections of the selected category, in order
        private int _featureSectionRowsLayout;                   // chip rows the section row wraps into (drives the body height)
        private Vector2 _featureBodyScroll;

        // ---- lifecycle hooks (called from the main file) ----

        private void FeaturesInit()
        {
            if (_featuresInited) return;
            _featuresInited = true;
            _extrasTabCfg = Config.Bind("Features", "EnableExtrasTab", true,
                "Master switch for the Tools tab that hosts all 32 optional modules (categories: Moderation, Server, Players, World, Shortcuts). Off = the panel shows only the eight base tabs.");

            // Waves register their sections + init their own config/patches. Unimplemented waves no-op.
            //
            // Each call is isolated: FeaturesInit() is the LAST thing Awake does, so an exception escaping
            // any one wave would propagate out of Awake, BepInEx would mark the whole plugin failed, and the
            // user would lose the eight original tabs along with the feature that broke. One bad wave should
            // cost that wave and nothing else. (Partial methods cannot be taken as delegates, hence the
            // repetition rather than a loop.)
            try { FeaturesInitWave1(); } catch (Exception e) { WaveInitFailed("1 (accountability)", e); }
            try { FeaturesInitWave2(); } catch (Exception e) { WaveInitFailed("2 (server tools)", e); }
            try { FeaturesInitWave3(); } catch (Exception e) { WaveInitFailed("3 (discord)", e); }
            try { FeaturesInitWave4(); } catch (Exception e) { WaveInitFailed("4 (player data)", e); }
            try { FeaturesInitWave5(); } catch (Exception e) { WaveInitFailed("5 (admin UX)", e); }
            try { FeaturesInitWave6(); } catch (Exception e) { WaveInitFailed("6 (economy)", e); }
            try { FeaturesInitWave7(); } catch (Exception e) { WaveInitFailed("7 (guard/SDK)", e); }
            try { FeaturesInitExtra(); } catch (Exception e) { WaveInitFailed("extras", e); }
            // Round 2 (2026-09): four groups, each its own partial so one can be dropped by deleting its files.
            try { FeaturesInitWave8Objects(); } catch (Exception e) { WaveInitFailed("8 (world objects)", e); }
            try { FeaturesInitWave8Toolkit(); } catch (Exception e) { WaveInitFailed("8 (toolkit)", e); }
            try { FeaturesInitWave8Rules(); } catch (Exception e) { WaveInitFailed("8 (server rules)", e); }
            try { FeaturesInitWave8Systems(); } catch (Exception e) { WaveInitFailed("8 (systems)", e); }
        }

        private void WaveInitFailed(string wave, Exception e) =>
            Logger.LogWarning($"Feature wave {wave} failed to initialise (its sections are unavailable; the rest of the panel is unaffected): {e}");

        // 2.5.5 admin gate closed (or never opened) for this server: anything a feature module keeps running
        // OUTSIDE the panel body — the palette overlay + its hotkey, macros, free camera — stops here, in the
        // same tick the main file clears its own self-cheats. Everything else lives in the (now hidden) body.
        private void FeaturesOnGateClosed()
        {
            if (!_featuresInited) return;
            try { PalOnGateClosed(); }
            catch (Exception e) { Logger.LogWarning($"Palette gate hook failed (ignored): {e.Message}"); }
            try { BldOnGateClosed(); }
            catch (Exception e) { Logger.LogWarning($"Build tools gate hook failed (ignored): {e.Message}"); }
        }

        private void FeaturesResetSession()
        {
            _featureSectionsLayout = null;
            _featureCatsLayout = null;
            _featureBodyScroll = Vector2.zero;
            FeaturesResetWave1();
            FeaturesResetWave2();
            FeaturesResetWave3();
            FeaturesResetWave4();
            FeaturesResetWave5();
            FeaturesResetWave6();
            FeaturesResetWave7();
            FeaturesResetExtra();
            FeaturesResetWave8Objects();
            FeaturesResetWave8Toolkit();
            FeaturesResetWave8Rules();
            FeaturesResetWave8Systems();
        }

        // Feature overlays (command palette, quick bar) draw before the panel-visibility gate so they can
        // exist with the panel closed. Each implementation must call EnsureSkin()/ApplyFont() itself and
        // keep its own control counts Layout-stable.
        private void FeaturesOnGUI()
        {
            if (!_featuresInited) return;
            FeaturesGuiWave1();
            FeaturesGuiWave3();
            FeaturesGuiWave5();
            FeaturesGuiWave6();
            FeaturesGuiWave8Objects();
            FeaturesGuiWave8Toolkit();
            FeaturesGuiWave8Rules();
            FeaturesGuiWave8Systems();
        }

        // Client-side timers. LateUpdate is unclaimed by the main file, so features get a lifecycle slot
        // without touching it. Runs from the main menu on; wave ticks must guard on world state themselves.
        private void LateUpdate()
        {
            if (!_featuresInited) return;
            FeatureClampTab();
            FeaturesTickWave1();
            FeaturesTickWave2();
            FeaturesTickWave3();
            FeaturesTickWave4();
            FeaturesTickWave5();
            FeaturesTickWave6();
            FeaturesTickWave7();
            FeaturesTickWave8Objects();
            FeaturesTickWave8Toolkit();
            FeaturesTickWave8Rules();
            FeaturesTickWave8Systems();
        }

        // ---- Extras tab plumbing ----

        internal void RegisterFeatureSection(FeatureSection s)
        {
            if (s == null || string.IsNullOrEmpty(s.Id) || s.Draw == null) return;
            if (_featureSections.Exists(x => x.Id == s.Id)) return;   // waves must not collide on ids
            _featureSections.Add(s);
        }

        // 660 keeps the 8 fixed tabs clickable; the Tools tab needs one more 90 px slot. Not layout-affecting
        // per-pass (it clamps the window rect, not a control), so live computation is safe here.
        private float MinPanelWidth() => 660f + (FeatureTabVisible() ? 90f : 0f);

        private bool FeatureTabVisible() =>
            _featuresInited && _extrasTabCfg.Value && _featureSections.Count > 0;

        // The master switch alone, for wave ticks that must fall silent when the operator turned the Extras
        // tab off — a tick has no chip and does not care about the registered-section count FeatureTabVisible
        // folds in. Only ever called from LateUpdate, so reading the config live is safe here.
        internal bool FeaturesEnabled() => _featuresInited && _extrasTabCfg != null && _extrasTabCfg.Value;

        // Called inside the tab row. Pins its own visibility on Layout so the row's control count can
        // never change between the Layout and Repaint passes of one frame.
        private void DrawFeatureTabButton()
        {
            if (Event.current.type == EventType.Layout) _featureTabVisibleLayout = FeatureTabVisible();
            if (!_featureTabVisibleLayout) return;
            var pressed = GUILayout.Toggle(_tab == FeatureTabIndex, Loc.T("tab.tools"), _tabStyle);
            if (pressed && _tab != FeatureTabIndex) { _tab = FeatureTabIndex; _openDropdown = null; _rebindTarget = 0; }
        }

        // The Extras button vanishes the moment the master switch goes off, but _tab is what DrawWindow's
        // switch routes on: left at the Extras index, the panel would keep showing feature UI with no tab
        // drawn as selected. The fallback lives here and not in OnGUI because _tab decides WHICH tab body
        // is emitted — changing it between the Layout and Repaint passes of one frame would hand IMGUI two
        // different control counts. It reads _featureTabVisibleLayout, the same Layout snapshot the tab row
        // gates the button on, so the button and the body can never disagree about whether the tab exists.
        private void FeatureClampTab()
        {
            if (_tab < FeatureTabIndex || _featureTabVisibleLayout) return;
            _tab = 0;
            _openDropdown = null;
            _rebindTarget = 0;
        }

        private FeatureSection FindSection(string id)
        {
            foreach (var s in _featureSections) if (s.Id == id) return s;
            return null;
        }

        private static bool SectionEnabled(FeatureSection s) => s.Enabled == null || s.Enabled();

        // Enabled sections of one category, in the category's own order; sections registered under an id no
        // category lists are appended to the LAST category (never dropped).
        private List<FeatureSection> EnabledSectionsOf(FeatureCategory cat)
        {
            var list = new List<FeatureSection>();
            foreach (var id in cat.Sections)
            {
                var s = FindSection(id);
                if (s != null && SectionEnabled(s)) list.Add(s);
            }
            if (ReferenceEquals(cat, FeatureCategories[FeatureCategories.Length - 1]))
            {
                foreach (var s in _featureSections)
                {
                    if (!SectionEnabled(s)) continue;
                    var listed = false;
                    foreach (var c in FeatureCategories) if (Array.IndexOf(c.Sections, s.Id) >= 0) { listed = true; break; }
                    if (!listed) list.Add(s);
                }
            }
            return list;
        }

        // Height reserved above the section body: the shared title/tab chrome the other tabs budget (~96 px at
        // font 13), the category row, the section chip rows and the spacing between them. Read from the Layout
        // snapshot so it is constant within a frame.
        private float FeatureBodyReserve() => 96f + 34f * (1 + Mathf.Max(1, _featureSectionRowsLayout)) + 14f;

        // For lists INSIDE a section. The body already scrolls as a whole, so an inner list may never be taller
        // than the body minus whatever the section draws ABOVE the list (its own view-chip row, a card): that is
        // `above`, in pixels at font 13. Sections whose list sits under a large form pass a bigger reserve and get
        // a shorter box, exactly as before; the floor only stops the list from outgrowing the body.
        internal float FeatureListView(float reserve, float above = 0f) =>
            ListView(Mathf.Max(reserve, FeatureBodyReserve() + 28f + above));

        // DrawWindow's default: case lands here for every index past the fixed tabs.
        private void DrawFeaturesTab()
        {
            // Pin everything the row/body emits per frame (sections toggle from config UI on the event pass; an
            // unpinned list would change the control count mid-frame). With the tab hidden this body must emit
            // nothing at all, or the feature UI stays drawn under a tab row where no tab is selected.
            if (Event.current.type == EventType.Layout)
            {
                List<FeatureCategory> cats = null;
                List<FeatureSection> sections = null;
                if (_featureTabVisibleLayout)
                {
                    cats = new List<FeatureCategory>();
                    foreach (var c in FeatureCategories) if (EnabledSectionsOf(c).Count > 0) cats.Add(c);
                    if (cats.Count > 0)
                    {
                        var cur = cats.Find(c => c.Id == _featureCat);
                        if (cur == null) { cur = cats[0]; _featureCat = cur.Id; }
                        sections = EnabledSectionsOf(cur);
                        string chip;
                        if (!_featureChip.TryGetValue(cur.Id, out chip) || sections.Find(x => x.Id == chip) == null)
                        {
                            chip = sections.Count > 0 ? sections[0].Id : null;
                            _featureChip[cur.Id] = chip;
                        }
                        _featureChipLayout = chip;
                    }
                }
                _featureCatsLayout = cats;
                _featureCatLayout = _featureCat;
                _featureSectionsLayout = sections;
                _featureSectionRowsLayout = sections != null ? (sections.Count + 5) / 6 : 1;
            }

            var catList = _featureCatsLayout;
            if (catList == null) return;   // tab not visible this frame — no controls, on every pass
            if (catList.Count == 0)
            {
                GUILayout.Label(Loc.T("feat.none"), _hintStyle);
                return;
            }

            // Category row — the Player tab's subcategory style, one row.
            GUILayout.BeginHorizontal();
            foreach (var c in catList)
            {
                var wasOn = _featureCatLayout == c.Id;
                if (GUILayout.Toggle(wasOn, Loc.T(c.LocKey), _catStyle) && !wasOn)
                {
                    _featureCat = c.Id;
                    _featureBodyScroll = Vector2.zero;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            // Section row, 6-wide wrap. Act only on an off->on FLIP: an untouched Toggle returns the value it was
            // drawn with, so the chip that is already selected reports true on every pass — and because it is
            // drawn later in this same loop than the chip the user just clicked, a plain "if (on)" would let it
            // overwrite the new selection and snap straight back.
            var list = _featureSectionsLayout;
            var perRow = 0;
            GUILayout.BeginHorizontal();
            foreach (var s in list)
            {
                if (perRow == 6) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); perRow = 0; }
                var wasOn = _featureChipLayout == s.Id;
                var on = GUILayout.Toggle(wasOn, Loc.T(s.LocKey), _chipStyleOrButton(), GUILayout.MinWidth(90));
                if (on && !wasOn)
                {
                    _featureChip[_featureCatLayout] = s.Id;
                    _featureBodyScroll = Vector2.zero;
                }
                perRow++;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // One body scroll view for every section: the bottom edge is the same on every chip, sections with
            // no list of their own no longer run off the window, and long forms stay reachable.
            _featureBodyScroll = GUILayout.BeginScrollView(_featureBodyScroll, GUILayout.Height(ListView(FeatureBodyReserve())));
            try
            {
                foreach (var s in list)
                {
                    if (s.Id != _featureChipLayout) continue;
                    s.Draw();
                    break;
                }
            }
            finally { GUILayout.EndScrollView(); }
        }

        // The Items/Player chips use _buttonStyle as their toggle style; reuse it so chips skin identically.
        private GUIStyle _chipStyleOrButton() => _buttonStyle ?? GUI.skin.button;

        // ---- wave hook declarations (implemented by wave files; calls vanish if a wave is absent) ----
        partial void FeaturesInitWave1();
        partial void FeaturesInitWave2();
        partial void FeaturesInitWave3();
        partial void FeaturesInitWave4();
        partial void FeaturesInitWave5();
        partial void FeaturesInitWave6();
        partial void FeaturesInitWave7();
        partial void FeaturesInitExtra();

        partial void FeaturesResetWave1();
        partial void FeaturesResetWave2();
        partial void FeaturesResetWave3();
        partial void FeaturesResetWave4();
        partial void FeaturesResetWave5();
        partial void FeaturesResetWave6();
        partial void FeaturesResetWave7();
        partial void FeaturesResetExtra();

        partial void FeaturesGuiWave1();
        partial void FeaturesGuiWave3();
        partial void FeaturesGuiWave5();
        partial void FeaturesGuiWave6();

        partial void FeaturesTickWave1();
        partial void FeaturesTickWave2();
        partial void FeaturesTickWave3();
        partial void FeaturesTickWave4();
        partial void FeaturesTickWave5();
        partial void FeaturesTickWave6();
        partial void FeaturesTickWave7();

        // ---- round 2 (wave 8) hooks: one group per glue file (Wave8ObjectsGlue.cs etc.) ----
        partial void FeaturesInitWave8Objects();
        partial void FeaturesInitWave8Toolkit();
        partial void FeaturesInitWave8Rules();
        partial void FeaturesInitWave8Systems();

        partial void FeaturesResetWave8Objects();
        partial void FeaturesResetWave8Toolkit();
        partial void FeaturesResetWave8Rules();
        partial void FeaturesResetWave8Systems();

        partial void FeaturesGuiWave8Objects();
        partial void FeaturesGuiWave8Toolkit();
        partial void FeaturesGuiWave8Rules();
        partial void FeaturesGuiWave8Systems();

        partial void FeaturesTickWave8Objects();
        partial void FeaturesTickWave8Toolkit();
        partial void FeaturesTickWave8Rules();
        partial void FeaturesTickWave8Systems();
    }
}
