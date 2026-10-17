using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #14 input conflict detector (client only) ====================
    // Field report 2026-10-14: hover worked, clicks did nothing. IMGUI buttons fire on MOUSE-UP, and every
    // MonoBehaviour's OnGUI sees the same Event in turn — so a mod whose OnGUI runs first and calls Event.Use() on
    // the release leaves ours a Used event (rawType still MouseUp) and no button ever fires. Nothing in the panel
    // could tell the player that.
    //
    // Detection: LateUpdate notes every raw left-button release (Input.GetMouseButtonUp) while the cursor is over the
    // panel; OnGUI's first line (before anything of ours could use the event) records whether a MouseUp reached us
    // and whether it arrived already Used. Two frames later the release is scored ok / swallowed / never arrived;
    // the last 12 decide. Three bad of twelve with the latest one bad trips the notice; six good in a row clears it.
    //
    // Fix: OnGUI order for input follows GUI.depth (lowest first). When the detector trips (and the auto option is on)
    // or the "always" option is set, the panel's OnGUI takes depth -1000 so it sees the click before other mods.
    public partial class AdminPanelPlugin
    {
        private const int InpRing = 12;
        private const int InpTripBad = 3;
        private const int InpClearGood = 6;

        private ConfigEntry<bool> _inpAutoPriorityCfg;
        private ConfigEntry<bool> _inpAlwaysPriorityCfg;

        private readonly int[] _inpOutcomes = new int[InpRing];   // 0 empty, 1 ok, 2 swallowed, 3 never arrived
        private int _inpNext;
        private int _inpGoodStreak;
        private bool _inpTripped;
        private bool _inpPriorityActive;

        private int _inpPendingUpFrame;     // frame of a raw release over the panel, awaiting its score
        private int _inpUpSeenFrame;        // last frame our OnGUI saw a left MouseUp (any state)
        private int _inpUpUsedFrame;        // last frame that MouseUp arrived already Used
        private string _inpTestAt = "";
        private List<string> _inpGuiPlugins;

        // Layout snapshots
        private bool _inpTrippedLayout;
        private int _inpOkLayout, _inpUsedLayout, _inpMissingLayout;

        internal void InpInit()
        {
            _inpAutoPriorityCfg = Config.Bind("Diagnostics", "InputPriorityWhenConflict", true,
                "When another mod is detected swallowing the panel's mouse releases, process the panel's GUI first (GUI.depth) so its buttons get the click.");
            _inpAlwaysPriorityCfg = Config.Bind("Diagnostics", "InputPriorityAlways", false,
                "Always process the panel's GUI before other mods' GUI (GUI.depth -1000).");
        }

        internal void InpReset()
        {
            _inpPendingUpFrame = 0;
        }

        private bool InpPriorityWanted() =>
            (_inpAlwaysPriorityCfg != null && _inpAlwaysPriorityCfg.Value) || _inpPriorityActive;

        internal void InpOnGuiFirst()
        {
            // Depth is per behaviour and read when Unity sorts the next event dispatch; setting it every call is cheap,
            // and setting it BOTH ways means turning the option off really hands the first look back to other mods.
            GUI.depth = InpPriorityWanted() ? -1000 : 0;
            if (!_visible) return;
            var e = Event.current;
            if (e == null || e.button != 0) return;
            if (e.rawType == EventType.MouseUp)
            {
                _inpUpSeenFrame = Time.frameCount;
                if (e.type == EventType.Used) _inpUpUsedFrame = Time.frameCount;
            }
        }

        internal void InpTick()
        {
            if (!_visible) { _inpPendingUpFrame = 0; return; }
            if (_inpPendingUpFrame > 0 && Time.frameCount - _inpPendingUpFrame >= 2)
            {
                int outcome;
                if (_inpUpUsedFrame >= _inpPendingUpFrame) outcome = 2;
                else if (_inpUpSeenFrame >= _inpPendingUpFrame) outcome = 1;
                else outcome = 3;
                InpRecord(outcome);
                _inpPendingUpFrame = 0;
            }
            if (_inpPendingUpFrame == 0 && Input.GetMouseButtonUp(0) && InpMouseOverPanel())
                _inpPendingUpFrame = Time.frameCount;
        }

        private bool InpMouseOverPanel()
        {
            var m = Input.mousePosition;
            var gui = new Vector2(m.x, Screen.height - m.y);
            return _windowRect.Contains(gui);
        }

        private void InpRecord(int outcome)
        {
            _inpOutcomes[_inpNext] = outcome;
            _inpNext = (_inpNext + 1) % InpRing;
            if (outcome == 1)
            {
                _inpGoodStreak++;
                if (_inpTripped && _inpGoodStreak >= InpClearGood) _inpTripped = false;
                return;
            }
            _inpGoodStreak = 0;
            var bad = 0;
            foreach (var o in _inpOutcomes) if (o == 2 || o == 3) bad++;
            if (bad >= InpTripBad && !_inpTripped)
            {
                _inpTripped = true;
                if (_inpAutoPriorityCfg == null || _inpAutoPriorityCfg.Value) _inpPriorityActive = true;
                Logger.LogWarning("Input conflict: mouse releases over the panel are being swallowed before they reach it " +
                                  $"({bad} of the last {InpRing}). Mods with their own IMGUI: {string.Join(", ", InpGuiPlugins().ToArray())}" +
                                  (_inpPriorityActive ? ". The panel now processes its GUI first (Diagnostics.InputPriorityWhenConflict)." : "."));
            }
        }

        // Loaded plugins (other than this one) whose assembly declares an OnGUI on some MonoBehaviour. Computed once.
        private List<string> InpGuiPlugins()
        {
            if (_inpGuiPlugins != null) return _inpGuiPlugins;
            var res = new List<string>();
            try
            {
                var mine = typeof(AdminPanelPlugin).Assembly;
                var seen = new HashSet<Assembly>();
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    var info = kv.Value;
                    var inst = info != null ? info.Instance : null;
                    if (inst == null) continue;
                    var asm = inst.GetType().Assembly;
                    if (asm == mine || !seen.Add(asm)) continue;
                    if (!InpAssemblyHasOnGui(asm)) continue;
                    res.Add(info.Metadata != null ? info.Metadata.Name + " " + info.Metadata.Version : asm.GetName().Name);
                    if (res.Count >= 12) break;
                }
            }
            catch (Exception e) { Logger.LogWarning($"Input diagnostics: plugin scan failed: {e.Message}"); }
            res.Sort(StringComparer.OrdinalIgnoreCase);
            _inpGuiPlugins = res;
            return res;
        }

        private static bool InpAssemblyHasOnGui(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch (Exception) { return false; }
            if (types == null) return false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                if (t == null) continue;
                try
                {
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (t.GetMethod("OnGUI", flags) != null) return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        // ---- draw ----

        internal void InpTopNotice()
        {
            if (Event.current.type == EventType.Layout) _inpTrippedLayout = _inpTripped;
            if (!_inpTrippedLayout) return;
            GUILayout.Label(Loc.T(InpPriorityWanted() ? "inp.notice_fixed" : "inp.notice"), _proseStyle);
        }

        internal void DrawInpCard()
        {
            if (Event.current.type == EventType.Layout)
            {
                _inpOkLayout = _inpUsedLayout = _inpMissingLayout = 0;
                foreach (var o in _inpOutcomes)
                {
                    if (o == 1) _inpOkLayout++;
                    else if (o == 2) _inpUsedLayout++;
                    else if (o == 3) _inpMissingLayout++;
                }
                _inpTrippedLayout = _inpTripped;
            }

            BeginCard(Loc.T("inp.section"));
            GUILayout.Label(Loc.T("inp.hint"), _hintStyle);
            GUILayout.Label(Loc.T("inp.counts", _inpOkLayout, _inpUsedLayout, _inpMissingLayout, InpRing), _labelStyle);
            GUILayout.Label(Loc.T(_inpTrippedLayout ? "inp.state_tripped" : "inp.state_ok"), _inpTrippedLayout ? _proseStyle : _dimLabelStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("inp.test"), _buttonStyle, GUILayout.MinWidth(120)))
                _inpTestAt = DateTime.Now.ToString("HH:mm:ss");
            GUILayout.Label(_inpTestAt.Length > 0 ? Loc.T("inp.test_ok", _inpTestAt) : Loc.T("inp.test_idle"), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var always = _inpAlwaysPriorityCfg != null && _inpAlwaysPriorityCfg.Value;
            var nAlways = GUILayout.Toggle(always, " " + Loc.T("inp.always"), _toggleStyle);
            if (nAlways != always && _inpAlwaysPriorityCfg != null) { _inpAlwaysPriorityCfg.Value = nAlways; Config.Save(); }
            var auto = _inpAutoPriorityCfg == null || _inpAutoPriorityCfg.Value;
            var nAuto = GUILayout.Toggle(auto, " " + Loc.T("inp.auto"), _toggleStyle);
            if (nAuto != auto && _inpAutoPriorityCfg != null) { _inpAutoPriorityCfg.Value = nAuto; Config.Save(); }
            GUILayout.Label(Loc.T(InpPriorityWanted() ? "inp.priority_on" : "inp.priority_off"), _dimLabelStyle);

            DrawSection(Loc.T("inp.plugins_title"));
            var plugins = InpGuiPlugins();
            if (plugins.Count == 0) GUILayout.Label(Loc.T("inp.plugins_none"), _hintStyle);
            else foreach (var p in plugins) GUILayout.Label("  " + p, _cellStyle);
            EndCard();
        }
    }
}
