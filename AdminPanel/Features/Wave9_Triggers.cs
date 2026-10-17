using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #12 trigger rules (Tools › Server › Automation) ====================
    // Server-side "if this, then that": a player joins, enters a rule zone, is present at a boss kill, dies, types a
    // chat keyword, or a time of day arrives; conditions on role, first time only and cooldown; actions broadcast,
    // message, grant an item or effect, teleport, start a raid event, post to Discord. One rule per row with an on/off
    // switch and a Test button that runs its actions on the admin (Wave9SrvTriggers.cs).
    public partial class AdminPanelPlugin
    {
        private const int TrigCap = 60;
        private static readonly string[] TrigTypeKeys = { "trig.t_join", "trig.t_zone", "trig.t_boss", "trig.t_death", "trig.t_chat", "trig.t_time" };
        private static readonly string[] TrigParamHintKeys = { "trig.p_none", "trig.p_zone", "trig.p_boss", "trig.p_none", "trig.p_chat", "trig.p_time" };
        private static readonly string[] TrigRoleKeys = { "trig.role_any", "trig.role_admin", "trig.role_player" };
        private static readonly string[] TrigActions = { "broadcast", "message", "give", "effect", "teleport", "event", "discord" };
        private static readonly string[] TrigActionKeys = { "trig.a_broadcast", "trig.a_message", "trig.a_give", "trig.a_effect", "trig.a_teleport", "trig.a_event", "trig.a_discord" };

        private sealed class TrigRule
        {
            public string Id = "", Name = "", Param = "";
            public bool On = true, First;
            public int Trig, Role, Cooldown;
            public List<string> Actions = new List<string>();
            public long Fired;
        }

        private ConfigEntry<bool> _trigSectionCfg;
        private List<TrigRule> _trigRules;
        private bool _trigPending;
        private float _trigAskedAt;
        private Vector2 _trigScroll;

        // editor
        private string _trigEditId = "", _trigEditName = "", _trigEditParam = "", _trigEditCooldown = "0";
        private int _trigEditTrig, _trigEditRole;
        private bool _trigEditFirst;
        private readonly int[] _trigEditActType = new int[4];
        private readonly string[] _trigEditActParam = { "", "", "", "" };

        private List<TrigRule> _trigRulesLayout;
        private string _trigWaitLayout = "trig.idle";
        private int _trigEditTrigLayout, _trigEditRoleLayout;

        internal void TrigInit()
        {
            _trigSectionCfg = Config.Bind("Features", "ShowAutomationSection", true,
                "Show the Automation section (server-side trigger rules) in Tools > Server.");
        }

        internal bool TrigSectionEnabled() => _trigSectionCfg == null || _trigSectionCfg.Value;

        internal void TrigReset()
        {
            _trigRules = null;
            _trigPending = false;
            _trigRulesLayout = null;
        }

        internal static void TrigRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_TrigData", ParseTrigData);

        private static void ParseTrigData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var n = pkg.ReadInt();
            if (n < 0 || n > TrigCap) return;
            var list = new List<TrigRule>(n);
            for (var i = 0; i < n; i++)
            {
                var r = new TrigRule { Id = pkg.ReadString() ?? "", Name = pkg.ReadString() ?? "", On = pkg.ReadBool(), Trig = pkg.ReadInt(),
                                       Param = pkg.ReadString() ?? "", Role = pkg.ReadInt(), First = pkg.ReadBool(), Cooldown = pkg.ReadInt() };
                var na = pkg.ReadInt();
                if (na < 0 || na > 4) return;
                for (var k = 0; k < na; k++) r.Actions.Add(pkg.ReadString() ?? "");
                list.Add(r);
            }
            foreach (var r in list) r.Fired = pkg.ReadLong();
            self._trigRules = list;
            self._trigPending = false;
        }

        private void TrigList()
        {
            if (!W9Send("AP_SrvTrigList")) return;
            _trigPending = true;
            _trigAskedAt = Time.time;
        }

        private void TrigSend(int op, TrigRule r)
        {
            var pkg = W9Pkg();
            pkg.Write(op);
            pkg.Write(r.Id ?? ""); pkg.Write(r.Name ?? ""); pkg.Write(r.On); pkg.Write(r.Trig); pkg.Write(r.Param ?? "");
            pkg.Write(r.Role); pkg.Write(r.First); pkg.Write(r.Cooldown);
            pkg.Write(r.Actions.Count);
            foreach (var a in r.Actions) pkg.Write(a ?? "");
            if (W9Send("AP_SrvTrigSet", pkg)) { _trigPending = true; _trigAskedAt = Time.time; }
        }

        private void TrigLoadEdit(TrigRule r)
        {
            _trigEditId = r.Id;
            _trigEditName = r.Name;
            _trigEditParam = r.Param;
            _trigEditTrig = r.Trig;
            _trigEditRole = r.Role;
            _trigEditFirst = r.First;
            _trigEditCooldown = TkI(r.Cooldown);
            for (var i = 0; i < 4; i++)
            {
                _trigEditActType[i] = 0;
                _trigEditActParam[i] = "";
                if (i >= r.Actions.Count) continue;
                var a = r.Actions[i];
                var sep = a.IndexOf('~');
                var type = sep > 0 ? a.Substring(0, sep) : a;
                var idx = Array.IndexOf(TrigActions, type);
                _trigEditActType[i] = idx < 0 ? 0 : idx;
                _trigEditActParam[i] = sep > 0 ? a.Substring(sep + 1) : "";
            }
        }

        private void TrigSaveEdit()
        {
            var r = new TrigRule
            {
                Id = _trigEditId ?? "", Name = (_trigEditName ?? "").Trim(), Param = (_trigEditParam ?? "").Trim(), Trig = _trigEditTrig,
                Role = _trigEditRole, First = _trigEditFirst, Cooldown = Mathf.Clamp(TkInt(_trigEditCooldown, 0), 0, 604800),
            };
            for (var i = 0; i < 4; i++)
            {
                var p = (_trigEditActParam[i] ?? "").Trim();
                if (p.Length > 0) r.Actions.Add(TrigActions[_trigEditActType[i]] + "~" + p);
            }
            if (r.Name.Length == 0) { Message(Loc.T("trig.msg_name")); return; }
            if (r.Actions.Count == 0) { Message(Loc.T("trig.msg_action")); return; }
            var existing = _trigRules != null ? _trigRules.Find(x => x.Id == r.Id) : null;
            if (existing != null) r.On = existing.On;
            TrigSend(0, r);
        }

        private string TrigDescribe(TrigRule r)
        {
            var t = Loc.T(r.Trig >= 0 && r.Trig < TrigTypeKeys.Length ? TrigTypeKeys[r.Trig] : "trig.t_join");
            if (r.Param.Length > 0) t += " \"" + r.Param + "\"";
            if (r.Role > 0) t += " · " + Loc.T(TrigRoleKeys[Mathf.Clamp(r.Role, 0, 2)]);
            if (r.First) t += " · " + Loc.T("trig.first_short");
            if (r.Cooldown > 0) t += " · " + Loc.T("trig.cooldown_short", r.Cooldown);
            return t + " » " + string.Join(", ", r.Actions.ToArray());
        }

        internal void DrawTrigSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                if (_trigRules == null && !_trigPending && TkReachable()) TrigList();
                _trigRulesLayout = _trigRules;
                _trigWaitLayout = W9WaitKey(_trigPending, _trigAskedAt, "trig.idle");
                _trigEditTrigLayout = _trigEditTrig;
                _trigEditRoleLayout = _trigEditRole;
            }

            BeginCard(Loc.T("trig.section"));
            GUILayout.Label(Loc.T("trig.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("trig.refresh"), _buttonStyle, GUILayout.MinWidth(90))) TrigList();
            GUILayout.Label(Loc.T(_trigWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var list = _trigRulesLayout;
            if (list != null)
            {
                if (list.Count == 0) GUILayout.Label(Loc.T("trig.none"), _hintStyle);
                else
                {
                    _trigScroll = GUILayout.BeginScrollView(_trigScroll, GUILayout.Height(Mathf.Min(220f, list.Count * 54f + 10f)));
                    for (var i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        GUILayout.BeginVertical(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.BeginHorizontal();
                        var on = GUILayout.Toggle(r.On, "", _toggleStyle, GUILayout.Width(24));
                        if (on != r.On) TrigSend(2, r);
                        GUILayout.Label(W9Short(r.Name, 24), _cellStyle, GUILayout.Width(170));
                        GUILayout.Label(Loc.T("trig.fired_n", r.Fired), _dimCellStyle, GUILayout.Width(90));
                        if (GUILayout.Button(Loc.T("trig.test"), _buttonStyle, GUILayout.MinWidth(50)))
                        {
                            if (TkReachable()) SrvRpc("AP_SrvTrigTest", r.Id);
                            else Message(Loc.T("common.not_connected_srv"));
                        }
                        if (GUILayout.Button(Loc.T("trig.edit"), _buttonStyle, GUILayout.MinWidth(50))) TrigLoadEdit(r);
                        if (ConfirmButton("trig:del:" + r.Id, Loc.T("trig.delete"), GUILayout.MinWidth(60))) TrigSend(1, r);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        GUILayout.Label("  " + W9Short(TrigDescribe(r), 110), _dimCellStyle);
                        GUILayout.EndVertical();
                    }
                    GUILayout.EndScrollView();
                }
            }

            DrawSection(Loc.T(_trigEditId.Length > 0 ? "trig.edit_title" : "trig.new_title"));
            _trigEditName = W9Field("trig.name", _trigEditName, 40, 220f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("trig.when"), _labelStyle, GUILayout.MinWidth(120));
            for (var i = 0; i < TrigTypeKeys.Length; i++)
                if (GUILayout.Toggle(_trigEditTrigLayout == i, Loc.T(TrigTypeKeys[i]), _catStyle) && _trigEditTrigLayout != i) _trigEditTrig = i;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("trig.param"), _labelStyle, GUILayout.MinWidth(120));
            _trigEditParam = GUILayout.TextField(_trigEditParam ?? "", 80, _textFieldStyle, GUILayout.Width(180));
            GUILayout.Label(Loc.T(TrigParamHintKeys[Mathf.Clamp(_trigEditTrigLayout, 0, TrigParamHintKeys.Length - 1)]), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("trig.who"), _labelStyle, GUILayout.MinWidth(120));
            for (var i = 0; i < TrigRoleKeys.Length; i++)
                if (GUILayout.Toggle(_trigEditRoleLayout == i, Loc.T(TrigRoleKeys[i]), _catStyle) && _trigEditRoleLayout != i) _trigEditRole = i;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _trigEditFirst = GUILayout.Toggle(_trigEditFirst, " " + Loc.T("trig.first"), _toggleStyle, GUILayout.MinWidth(180));
            GUILayout.Label(Loc.T("trig.cooldown"), _labelStyle, GUILayout.MinWidth(90));
            _trigEditCooldown = GUILayout.TextField(_trigEditCooldown ?? "", 6, _textFieldStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("trig.actions"), _labelStyle);
            for (var i = 0; i < 4; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                if (GUILayout.Button(Loc.T(TrigActionKeys[_trigEditActType[i]]), _buttonStyle, GUILayout.Width(110)))
                    _trigEditActType[i] = (_trigEditActType[i] + 1) % TrigActions.Length;
                _trigEditActParam[i] = GUILayout.TextField(_trigEditActParam[i] ?? "", 200, _textFieldStyle, GUILayout.Width(300));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(Loc.T("trig.actions_hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("trig.save"), _buttonStyle, GUILayout.MinWidth(100))) TrigSaveEdit();
            if (GUILayout.Button(Loc.T("trig.new"), _buttonStyle, GUILayout.MinWidth(80))) TrigLoadEdit(new TrigRule());
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndCard();
        }
    }
}
