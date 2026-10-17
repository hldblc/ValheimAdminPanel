using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #15 gamepad and Steam Deck mode (Settings tab → Panel card) ====================
    // IMGUI has no controller support at all: no focus traversal, no "press" from a pad. So the panel brings its own:
    //   * every Repaint inside the panel window records each button/toggle (GUI.DoControl - the one path both share,
    //     UnityEngine.IMGUIModule GUI.cs DoButton/DoToggle -> DoControl), text field (GUI.DoTextField) and scroll view
    //     (GUI.BeginScrollView) in draw order, with its on-screen rect clipped to what is actually visible
    //     (GUIClip.visibleRect) - scrolled-away controls are never focusable;
    //   * D-pad / left stick move a focus ring to the nearest visible control in that direction; A "clicks" it by
    //     returning !on from DoControl for the SAME control index on the next Layout pass (IMGUI guarantees one control
    //     order across a frame's passes, and a change made during Layout is seen identically by the Repaint after it).
    //     A press only fires when the control at that index still shows the caption that was focused (a reply that
    //     inserted rows above it must not turn "A" into a click on a different button) and is enabled (GUI.enabled:
    //     a greyed-out "real run" button stays inert; disabled controls are not focusable at all);
    //     on a text field A gives it keyboard focus and opens the Steam on-screen keyboard; B backs out (text focus,
    //     then an open dropdown, then the panel); the bumpers switch tabs; the right stick scrolls the scroll view the
    //     focus sits in; L3 + R3 opens and closes the panel.
    //   * larger touch targets: buttons, toggles and chips get extra padding while controller mode is active.
    // "Auto" turns all of this on whenever the game says a gamepad was the last input (ZInput.IsGamepadActive).
    public partial class AdminPanelPlugin
    {
        private struct PadCtrl
        {
            public Rect Screen;
            public bool Visible;
            public int Kind;   // 0 button/toggle, 1 text field
            public int Id;
            public string Text;  // the caption at Repaint: a press is checked against it
        }

        private ConfigEntry<string> _padModeCfg;   // "Auto" | "On" | "Off"
        private bool _padActive;
        private bool _padInWindow;
        private EventType _padPass;
        private int _padIndex, _padScrollIndex;
        private List<PadCtrl> _padCtrls = new List<PadCtrl>();
        private List<PadCtrl> _padBuild = new List<PadCtrl>();
        private List<Rect> _padScrolls = new List<Rect>();
        private List<Rect> _padScrollBuild = new List<Rect>();
        private int _padFocus = -1;
        private int _padPress = -1;
        private string _padPressText;
        private int _padPressKind;
        private int _padPressFrame;
        private int _padScrollTarget = -1;
        private float _padScrollDelta;
        private Texture2D _padRingTex;
        private GUIStyle _padPaddedFor;
        private readonly Dictionary<GUIStyle, RectOffset> _padOrigPadding = new Dictionary<GUIStyle, RectOffset>();
        private static Func<Rect> _padVisibleRect;
        private static bool _padVisibleProbed;
        private string _padModeLayout = "Auto";

        private static readonly string[] PadModes = { "Auto", "On", "Off" };
        private static readonly string[] PadModeKeys = { "pad.m_auto", "pad.m_on", "pad.m_off" };

        internal void PadInit()
        {
            _padModeCfg = Config.Bind("Panel", "ControllerMode", "Auto",
                "Gamepad / Steam Deck navigation of the panel: Auto (on whenever a gamepad was the last input), On, or Off. L3 + R3 opens and closes the panel.");
            try { Harmony.CreateAndPatchAll(typeof(PadGuiHooks)); }
            catch (Exception e) { Logger.LogWarning($"Controller mode unavailable (IMGUI hooks failed): {e.Message}"); }
        }

        internal void PadReset()
        {
            _padFocus = -1;
            _padPress = -1;
            _padScrollTarget = -1;
            _padScrollDelta = 0f;
        }

        private bool PadWanted()
        {
            var mode = _padModeCfg != null ? _padModeCfg.Value : "Auto";
            if (mode == "Off") return false;
            if (mode == "On") return true;
            try { return ZInput.IsGamepadActive(); } catch (Exception) { return false; }
        }

        // ==================== per-frame input (LateUpdate) ====================

        internal void PadTick()
        {
            var wanted = PadWanted();
            if (wanted != _padActive) { _padActive = wanted; PadApplyPadding(wanted); }
            else if (_padActive && _padPaddedFor != _buttonStyle) PadApplyPadding(true);   // styles were rebuilt
            if (_padModeCfg != null && _padModeCfg.Value == "Off") return;

            try
            {
                if ((ZInput.GetButton("JoyLStick") && ZInput.GetButtonDown("JoyRStick")) ||
                    (ZInput.GetButton("JoyRStick") && ZInput.GetButtonDown("JoyLStick")))
                { PadTogglePanel(); return; }
            }
            catch (Exception) { return; }
            if (!_padActive || !_visible) return;

            try
            {
                if (ZInput.GetButtonDown("JoyDPadUp") || ZInput.GetButtonDown("JoyLStickUp")) PadMove(0f, -1f);
                else if (ZInput.GetButtonDown("JoyDPadDown") || ZInput.GetButtonDown("JoyLStickDown")) PadMove(0f, 1f);
                else if (ZInput.GetButtonDown("JoyDPadLeft") || ZInput.GetButtonDown("JoyLStickLeft")) PadMove(-1f, 0f);
                else if (ZInput.GetButtonDown("JoyDPadRight") || ZInput.GetButtonDown("JoyLStickRight")) PadMove(1f, 0f);

                if (ZInput.GetButtonDown("JoyButtonA")) PadPress();
                if (ZInput.GetButtonDown("JoyButtonB")) PadBack();
                if (ZInput.GetButtonDown("JoyLBumper")) PadTab(-1);
                if (ZInput.GetButtonDown("JoyRBumper")) PadTab(1);

                var stick = ZInput.GetJoyRightStickY();
                if (Mathf.Abs(stick) > 0.2f) PadScroll(stick * 900f * Time.unscaledDeltaTime);
            }
            catch (Exception) { }
        }

        private void PadTogglePanel()
        {
            if (_rebindTarget != 0) return;   // the F7 path has the same guard: a rebind in progress owns the input
            _visible = !_visible;
            if (_visible)
            {
                Loc.Refresh(_languageCfg.Value);
                RefreshCaches();
                _padFocus = -1;
            }
            else { FlushNotes(); CommitUiSettings(); _openDropdown = null; }
        }

        private bool PadFocusValid() => _padFocus >= 0 && _padFocus < _padCtrls.Count && _padCtrls[_padFocus].Visible;

        private void PadMove(float dx, float dy)
        {
            var ctrls = _padCtrls;
            if (ctrls.Count == 0) return;
            if (!PadFocusValid()) { _padFocus = PadFirstVisible(); return; }
            var from = ctrls[_padFocus].Screen.center;
            var best = -1;
            var bestCost = float.MaxValue;
            for (var i = 0; i < ctrls.Count; i++)
            {
                if (i == _padFocus || !ctrls[i].Visible) continue;
                var d = ctrls[i].Screen.center - from;
                var along = d.x * dx + d.y * dy;              // distance in the pressed direction
                if (along <= 2f) continue;
                var across = Mathf.Abs(d.x * dy) + Mathf.Abs(d.y * dx);
                var cost = along + across * 2.5f;
                if (cost < bestCost) { bestCost = cost; best = i; }
            }
            if (best >= 0) _padFocus = best;
        }

        private int PadFirstVisible()
        {
            var best = -1;
            var bestKey = float.MaxValue;
            for (var i = 0; i < _padCtrls.Count; i++)
            {
                if (!_padCtrls[i].Visible) continue;
                var c = _padCtrls[i].Screen;
                var key = c.y * 4f + c.x;
                if (key < bestKey) { bestKey = key; best = i; }
            }
            return best;
        }

        private void PadPress()
        {
            if (!PadFocusValid()) { _padFocus = PadFirstVisible(); return; }
            _padPress = _padFocus;
            _padPressText = _padCtrls[_padFocus].Text;
            _padPressKind = _padCtrls[_padFocus].Kind;
            _padPressFrame = Time.frameCount;
            if (_padCtrls[_padFocus].Kind == 1) PadOpenKeyboard(_padCtrls[_padFocus].Screen);
        }

        private void PadBack()
        {
            if (GUIUtility.keyboardControl != 0) { GUIUtility.keyboardControl = 0; return; }
            if (_openDropdown != null) { _openDropdown = null; return; }
            if (_sideMode != SideMode.None) { _sideMode = SideMode.None; return; }
            _visible = false;
            FlushNotes();
            CommitUiSettings();
        }

        private void PadTab(int dir)
        {
            if (_gate != Gate.Ok && _gate != Gate.OldCompanion) return;   // a closed gate shows Settings only
            var count = TabKeys.Length + (FeatureTabVisible() ? 1 : 0);
            _tab = ((_tab + dir) % count + count) % count;
            _openDropdown = null;
            _rebindTarget = 0;
            _padFocus = -1;
        }

        private void PadScroll(float delta)
        {
            if (!PadFocusValid()) return;
            var p = _padCtrls[_padFocus].Screen.center;
            var target = -1;
            for (var i = 0; i < _padScrolls.Count; i++)
                if (_padScrolls[i].Contains(p)) target = i;   // later = nested deeper
            if (target < 0) return;
            if (target != _padScrollTarget) _padScrollDelta = 0f;
            _padScrollTarget = target;
            _padScrollDelta += delta;
        }

        // ==================== Steam on-screen keyboard ====================

        private static MethodInfo _padSteamKb;
        private static Type _padSteamKbMode;
        private static bool _padSteamProbed;

        private void PadOpenKeyboard(Rect screen)
        {
            try
            {
                if (!_padSteamProbed)
                {
                    _padSteamProbed = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var utils = asm.GetType("Steamworks.SteamUtils", false);
                        if (utils == null) continue;
                        _padSteamKbMode = asm.GetType("Steamworks.EFloatingGamepadTextInputMode", false);
                        _padSteamKb = utils.GetMethod("ShowFloatingGamepadTextInput", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }
                if (_padSteamKb == null || _padSteamKbMode == null) return;
                // GUI y runs down from the top; Steam wants the same top-left screen coordinates.
                _padSteamKb.Invoke(null, new object[] { Enum.ToObject(_padSteamKbMode, 0), (int)screen.x, (int)screen.y, (int)screen.width, (int)screen.height });
            }
            catch (Exception) { /* no Steam, no overlay keyboard: a physical keyboard still types into the focused field */ }
        }

        // ==================== larger touch targets ====================

        private void PadApplyPadding(bool on)
        {
            if (_buttonStyle == null) return;
            foreach (var kv in _padOrigPadding) if (kv.Key != null) kv.Key.padding = kv.Value;
            _padOrigPadding.Clear();
            _padPaddedFor = null;
            if (!on) return;
            foreach (var st in new[] { _buttonStyle, _toggleStyle, _catStyle })
            {
                if (st == null || _padOrigPadding.ContainsKey(st)) continue;
                var p = st.padding;
                _padOrigPadding[st] = new RectOffset(p.left, p.right, p.top, p.bottom);
                st.padding = new RectOffset(p.left + 2, p.right + 2, p.top + 3, p.bottom + 3);
            }
            _padPaddedFor = _buttonStyle;
        }

        // ==================== window hooks ====================

        internal void PadWindowBegin()
        {
            if (!_padActive) return;
            _padInWindow = true;
            _padPass = Event.current.type;
            _padIndex = 0;
            _padScrollIndex = 0;
            if (_padPass == EventType.Repaint) { _padBuild.Clear(); _padScrollBuild.Clear(); }
        }

        internal void PadWindowEnd()
        {
            if (!_padInWindow) return;
            _padInWindow = false;
            if (_padPass == EventType.Layout)
            {
                _padPress = -1;            // a press is offered to exactly one Layout pass
                _padScrollDelta = 0f;
            }
            if (_padPass != EventType.Repaint) return;
            var hadFocus = PadFocusValid();
            var oldFocus = hadFocus ? _padCtrls[_padFocus] : default(PadCtrl);
            var t = _padCtrls; _padCtrls = _padBuild; _padBuild = t;
            var s = _padScrolls; _padScrolls = _padScrollBuild; _padScrollBuild = s;
            // The layout changed under the focus (rows inserted, a card opened): follow the same control by caption,
            // nearest first, instead of leaving the ring on whatever now sits at the old index.
            if (hadFocus && (!PadFocusValid() || _padCtrls[_padFocus].Text != oldFocus.Text || _padCtrls[_padFocus].Kind != oldFocus.Kind))
                _padFocus = PadFindLike(oldFocus);
            if (!PadFocusValid()) return;
            if (_padRingTex == null) _padRingTex = SolidTex(new Color(0.98f, 0.78f, 0.35f, 1f));
            var r = _padCtrls[_padFocus].Screen;
            var tl = GUIUtility.ScreenToGUIPoint(new Vector2(r.x, r.y));
            var g = new Rect(tl.x - 2f, tl.y - 2f, r.width + 4f, r.height + 4f);
            GUI.DrawTexture(new Rect(g.x, g.y, g.width, 2f), _padRingTex);
            GUI.DrawTexture(new Rect(g.x, g.yMax - 2f, g.width, 2f), _padRingTex);
            GUI.DrawTexture(new Rect(g.x, g.y, 2f, g.height), _padRingTex);
            GUI.DrawTexture(new Rect(g.xMax - 2f, g.y, 2f, g.height), _padRingTex);
        }

        // Overlay slot: nothing to draw outside the window; the ring is drawn inside it (PadWindowEnd).
        internal void PadGui() { }

        // First line of every OnGUI call: nothing of ours is "inside the window" yet (a window body that threw must not
        // leave the flag set, or other mods' controls would be counted - and pressed). A press nobody consumed within
        // two frames (the window was not drawn) is dropped rather than fired later on whatever sits at that index.
        internal void PadOnGuiFirst()
        {
            _padInWindow = false;
            if (_padPress >= 0 && Time.frameCount - _padPressFrame > 2) _padPress = -1;
        }

        private int PadFindLike(PadCtrl old)
        {
            var best = -1;
            var bestD = float.MaxValue;
            for (var i = 0; i < _padCtrls.Count; i++)
            {
                var c = _padCtrls[i];
                if (!c.Visible || c.Kind != old.Kind || c.Text != old.Text) continue;
                var d = (c.Screen.center - old.Screen.center).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private void PadRecord(Rect position, int kind, int id, string text)
        {
            var vis = position;
            try
            {
                if (!_padVisibleProbed)
                {
                    _padVisibleProbed = true;
                    var clip = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip");
                    var prop = clip != null ? clip.GetProperty("visibleRect", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) : null;
                    var getter = prop != null ? prop.GetGetMethod(true) : null;
                    if (getter != null) _padVisibleRect = (Func<Rect>)Delegate.CreateDelegate(typeof(Func<Rect>), getter);
                }
                if (_padVisibleRect != null)
                {
                    var c = _padVisibleRect();
                    var xMin = Mathf.Max(position.xMin, c.xMin);
                    var yMin = Mathf.Max(position.yMin, c.yMin);
                    var xMax = Mathf.Min(position.xMax, c.xMax);
                    var yMax = Mathf.Min(position.yMax, c.yMax);
                    vis = new Rect(xMin, yMin, Mathf.Max(0f, xMax - xMin), Mathf.Max(0f, yMax - yMin));
                }
            }
            catch (Exception) { }
            var tl = GUIUtility.GUIToScreenPoint(new Vector2(vis.x, vis.y));
            _padBuild.Add(new PadCtrl
            {
                Screen = new Rect(tl.x, tl.y, vis.width, vis.height),
                Visible = GUI.enabled && vis.width >= 6f && vis.height >= 6f,   // disabled controls are not focusable
                Kind = kind, Id = id, Text = text ?? "",
            });
        }

        [HarmonyPatch]
        private static class PadGuiHooks
        {
            [HarmonyPatch(typeof(GUI), "DoControl")]
            [HarmonyPostfix]
            private static void DoControl(Rect position, int id, bool on, GUIContent content, ref bool __result)
            {
                var s = Instance;
                if (s == null || !s._padInWindow) return;
                var idx = s._padIndex++;
                var text = content != null ? content.text : "";
                if (s._padPass == EventType.Repaint) s.PadRecord(position, 0, id, text);
                else if (s._padPass == EventType.Layout && idx == s._padPress)
                {
                    s._padPress = -1;
                    // Only the control that was focused, and only while it is enabled: the input events that normally
                    // keep a disabled button inert never reach a Layout pass.
                    if (!GUI.enabled || s._padPressKind != 0 || (text ?? "") != (s._padPressText ?? "")) return;
                    __result = !on;
                    GUI.changed = true;
                }
            }

            [HarmonyPatch(typeof(GUI), "DoTextField", new[] { typeof(Rect), typeof(int), typeof(GUIContent), typeof(bool), typeof(int), typeof(GUIStyle), typeof(string), typeof(char) })]
            [HarmonyPrefix]
            private static void DoTextField(Rect position, int id)
            {
                var s = Instance;
                if (s == null || !s._padInWindow) return;
                var idx = s._padIndex++;
                if (s._padPass == EventType.Repaint) s.PadRecord(position, 1, id, "");   // a field's text changes as it is typed
                else if (s._padPass == EventType.Layout && idx == s._padPress)
                {
                    s._padPress = -1;
                    if (GUI.enabled && s._padPressKind == 1) GUIUtility.keyboardControl = id;
                }
            }

            [HarmonyPatch(typeof(GUI), "BeginScrollView", new[] { typeof(Rect), typeof(Vector2), typeof(Rect), typeof(bool), typeof(bool), typeof(GUIStyle), typeof(GUIStyle), typeof(GUIStyle) })]
            [HarmonyPrefix]
            private static void BeginScrollView(Rect position, ref Vector2 scrollPosition, Rect viewRect)
            {
                var s = Instance;
                if (s == null || !s._padInWindow) return;
                var idx = s._padScrollIndex++;
                if (s._padPass == EventType.Repaint)
                {
                    var tl = GUIUtility.GUIToScreenPoint(new Vector2(position.x, position.y));
                    s._padScrollBuild.Add(new Rect(tl.x, tl.y, position.width, position.height));
                }
                else if (s._padPass == EventType.Layout && idx == s._padScrollTarget && s._padScrollDelta != 0f)
                {
                    // Unity clamps the offset only on mouse events: clamp here, or holding the stick at the bottom piles up
                    // an overshoot that has to be unwound before scrolling back up moves anything.
                    scrollPosition.y = Mathf.Clamp(scrollPosition.y + s._padScrollDelta, 0f, Mathf.Max(0f, viewRect.height - position.height));
                    s._padScrollDelta = 0f;
                }
            }
        }

        // ==================== Settings → Panel card row ====================

        internal void DrawPadToggle()
        {
            if (Event.current.type == EventType.Layout) _padModeLayout = _padModeCfg != null ? _padModeCfg.Value : "Auto";
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pad.mode"), _labelStyle, GUILayout.MinWidth(130));
            for (var i = 0; i < PadModes.Length; i++)
            {
                var m = PadModes[i];
                var on = _padModeLayout == m;
                if (GUILayout.Toggle(on, Loc.T(PadModeKeys[i]), _catStyle) && !on && _padModeCfg != null)
                { _padModeCfg.Value = m; Config.Save(); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T(_padActive ? "pad.hint_active" : "pad.hint"), _hintStyle);
        }
    }
}
