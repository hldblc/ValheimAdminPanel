using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #2 creature command mode (Creatures tab → Command card, under the spawn controls) ====================
    // Orders for the creatures around the admin or the one under the crosshair: follow a player, guard a point within a
    // patrol radius, stay passive, attack a named player, or back to normal. The server writes the order into each
    // creature's ZDO; the client that simulates the creature carries it out (Wave9SrvCreatureCommand.cs). Guard works
    // for every owner (it is the game's own patrol point); the other orders need the companion on the simulating
    // client. Spawned creatures become event actors instead of a mob that wanders off.
    public partial class AdminPanelPlugin
    {
        private static readonly string[] CcmdModeKeys = { "ccmd.m_normal", "ccmd.m_follow", "ccmd.m_guard", "ccmd.m_passive", "ccmd.m_attack" };

        private ConfigEntry<bool> _ccmdCardCfg;
        private bool _ccmdOpen;
        private int _ccmdMode = 1;
        private bool _ccmdAimed;
        private string _ccmdRadius = "15", _ccmdGuardRadius = "8";
        private string _ccmdPlayer = "";
        private Vector3 _ccmdGuardPoint;
        private bool _ccmdGuardSet;

        private bool _ccmdOpenLayout, _ccmdAimedLayout;
        private int _ccmdModeLayout;

        internal void CcmdInit()
        {
            _ccmdCardCfg = Config.Bind("Features", "ShowCreatureCommandCard", true,
                "Show the Command card (order creatures to follow, guard, stay passive or attack) under the Creatures tab's spawn controls.");
        }

        internal void CcmdReset()
        {
            _ccmdPlayer = "";
            _ccmdGuardSet = false;
        }

        private void CcmdCyclePlayer()
        {
            var names = new List<string>();
            if (LocalPlayer != null) names.Add(LocalPlayer.GetPlayerName());
            foreach (var p in _othersSnapshot ?? OtherPlayers()) if (IsSpawned(p)) names.Add(p.m_name);
            if (names.Count == 0) { _ccmdPlayer = ""; return; }
            var i = names.IndexOf(_ccmdPlayer);
            _ccmdPlayer = names[(i + 1) % names.Count];
        }

        private void CcmdApply()
        {
            var lp = LocalPlayer;
            if (lp == null) return;
            var mode = _ccmdMode;
            if ((mode == 1 || mode == 4) && string.IsNullOrEmpty(_ccmdPlayer)) { Message(Loc.T("ccmd.msg_pick_player")); return; }
            var one = ZDOID.None;
            if (_ccmdAimed)
            {
                RaycastHit hit;
                var nv = W9Crosshair(60f, out hit) ? W9ViewOf(hit) : null;
                var ch = nv != null ? nv.GetComponent<Character>() : null;
                if (ch == null || ch.IsPlayer()) { Message(Loc.T("ccmd.msg_no_aim")); return; }
                one = nv.GetZDO().m_uid;
            }
            var point = _ccmdGuardSet ? _ccmdGuardPoint : lp.transform.position;
            var pkg = W9Pkg();
            pkg.Write(mode);
            pkg.Write(_ccmdAimed ? 1 : 0);
            pkg.Write(lp.transform.position);
            pkg.Write(Mathf.Clamp(TkFloat(_ccmdRadius, 15f), 2f, 64f));
            pkg.Write(one);
            pkg.Write(_ccmdPlayer ?? "");
            pkg.Write(point);
            pkg.Write(Mathf.Clamp(TkFloat(_ccmdGuardRadius, 8f), 0f, 60f));
            W9Send("AP_SrvCcmd", pkg);
        }

        // Returns the height the card took, pinned on the Layout pass so the creature list below never changes size
        // between the passes of one frame.
        internal float DrawCcmdCard()
        {
            if (_ccmdCardCfg != null && !_ccmdCardCfg.Value) return 0f;
            if (Event.current.type == EventType.Layout)
            {
                _ccmdOpenLayout = _ccmdOpen;
                _ccmdModeLayout = _ccmdMode;
                _ccmdAimedLayout = _ccmdAimed;
            }
            GUILayout.BeginHorizontal();
            var open = GUILayout.Toggle(_ccmdOpenLayout, " " + Loc.T("ccmd.toggle"), _toggleStyle);
            if (open != _ccmdOpenLayout) _ccmdOpen = open;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (!_ccmdOpenLayout) return 28f;

            GUILayout.BeginVertical(_cardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ccmd.order"), _labelStyle, GUILayout.MinWidth(70));
            for (var i = 0; i < CcmdModeKeys.Length; i++)
                if (GUILayout.Toggle(_ccmdModeLayout == i, Loc.T(CcmdModeKeys[i]), _catStyle) && _ccmdModeLayout != i) _ccmdMode = i;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ccmd.who"), _labelStyle, GUILayout.MinWidth(70));
            if (GUILayout.Toggle(!_ccmdAimedLayout, Loc.T("ccmd.around_me"), _catStyle) && _ccmdAimedLayout) _ccmdAimed = false;
            if (GUILayout.Toggle(_ccmdAimedLayout, Loc.T("ccmd.aimed"), _catStyle) && !_ccmdAimedLayout) _ccmdAimed = true;
            GUILayout.Label(Loc.T("ccmd.radius"), _labelStyle, GUILayout.MinWidth(55));
            _ccmdRadius = GUILayout.TextField(_ccmdRadius ?? "", 3, _textFieldStyle, GUILayout.Width(40));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ccmd.player"), _labelStyle, GUILayout.MinWidth(70));
            if (GUILayout.Button(string.IsNullOrEmpty(_ccmdPlayer) ? Loc.T("ccmd.pick") : _ccmdPlayer, _buttonStyle, GUILayout.MinWidth(130))) CcmdCyclePlayer();
            GUILayout.Label(Loc.T("ccmd.guard_radius"), _labelStyle, GUILayout.MinWidth(90));
            _ccmdGuardRadius = GUILayout.TextField(_ccmdGuardRadius ?? "", 3, _textFieldStyle, GUILayout.Width(40));
            if (GUILayout.Button(Loc.T(_ccmdGuardSet ? "ccmd.guard_here_set" : "ccmd.guard_here"), _buttonStyle, GUILayout.MinWidth(110)) && LocalPlayer != null)
            { _ccmdGuardPoint = LocalPlayer.transform.position; _ccmdGuardSet = true; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("ccmd.apply"), _buttonStyle, GUILayout.MinWidth(110))) CcmdApply();
            GUILayout.Label(Loc.T("ccmd.hint"), _hintStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            // The list below reserves this much: rows grow with the font size (13 = the default) and with controller
            // mode's larger touch targets, and the hint may wrap to a second line.
            var scale = Mathf.Max(1f, _fontSizeLive / 13f);
            return (28f + 4 * 32f + 24f) * scale + (_padActive ? 4 * 6f : 0f) + 18f;
        }
    }
}
