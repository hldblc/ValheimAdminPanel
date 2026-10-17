using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #7 rule zones (Tools › World › Zones) ====================
    // Circles or rectangles with any mix of rules: forced PvP, keep inventory on death, no portals, no item drops,
    // speed aura, healing aura, an entry message, no-build and no-damage. Entry messages, healing, no-build and
    // no-damage are enforced by the server for everyone (no-build/no-damage ARE wave 5 protection zones underneath);
    // PvP, keep-inventory, portals, drops and speed are decided on the player's own client, so they need the
    // companion there (Wave9SrvRuleZones.cs). Zones can be drawn on the map.
    public partial class AdminPanelPlugin
    {
        private const int RzoneCap = 48;

        private static readonly (int Bit, string Key)[] RzoneRules =
        {
            (1, "rzone.r_pvp"), (2, "rzone.r_keepinv"), (4, "rzone.r_noportal"), (8, "rzone.r_nodrop"), (16, "rzone.r_speed"),
            (32, "rzone.r_heal"), (64, "rzone.r_message"), (128, "rzone.r_nobuild"), (256, "rzone.r_nodamage"),
        };

        private sealed class RzoneDef
        {
            public string Id = "", Name = "", Msg = "";
            public int Shape, Rules, SpeedPct = 25;
            public float Cx, Cz, R = 20f, Hx = 20f, Hz = 20f, Heal = 5f;
            public bool On = true;

            public RzoneDef Clone() => (RzoneDef)MemberwiseClone();
        }

        private ConfigEntry<bool> _rzoneSectionCfg;
        private List<RzoneDef> _rzones;
        private RzoneDef _rzoneEdit = new RzoneDef();
        private string _rzoneR = "20", _rzoneHx = "20", _rzoneHz = "20", _rzoneSpeed = "25", _rzoneHeal = "5";
        private bool _rzonePending, _rzoneShowPins;
        private float _rzoneAskedAt;
        private Vector2 _rzoneScroll;
        private readonly List<Minimap.PinData> _rzonePins = new List<Minimap.PinData>();
        private bool _rzonePinsDirty;

        private List<RzoneDef> _rzonesLayout;
        private string _rzoneWaitLayout = "rzone.idle";
        private int _rzoneShapeLayout;

        internal void RzoneInit()
        {
            _rzoneSectionCfg = Config.Bind("Features", "ShowRuleZonesSection", true,
                "Show the Zones section (rule zones: PvP, keep inventory, no portals, no drops, speed/healing aura, entry message, no-build) in Tools > World.");
        }

        internal bool RzoneSectionEnabled() => _rzoneSectionCfg == null || _rzoneSectionCfg.Value;

        internal void RzoneReset()
        {
            RzoneClearPins();
            _rzones = null;
            _rzonePending = false;
            _rzoneShowPins = false;
            _rzonesLayout = null;
        }

        internal static void RzoneRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_RzoneData", ParseRzoneData);

        private static RzoneDef RzoneRead(ZPackage p) => new RzoneDef
        {
            Id = p.ReadString() ?? "", Name = p.ReadString() ?? "", Shape = p.ReadInt(), Cx = p.ReadSingle(), Cz = p.ReadSingle(),
            R = p.ReadSingle(), Hx = p.ReadSingle(), Hz = p.ReadSingle(), Rules = p.ReadInt(), SpeedPct = p.ReadInt(),
            Heal = p.ReadSingle(), Msg = p.ReadString() ?? "", On = p.ReadBool(),
        };

        private static void RzoneWrite(ZPackage p, RzoneDef z)
        {
            p.Write(z.Id ?? ""); p.Write(z.Name ?? ""); p.Write(z.Shape); p.Write(z.Cx); p.Write(z.Cz); p.Write(z.R); p.Write(z.Hx); p.Write(z.Hz);
            p.Write(z.Rules); p.Write(z.SpeedPct); p.Write(z.Heal); p.Write(z.Msg ?? ""); p.Write(z.On);
        }

        private static void ParseRzoneData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var n = pkg.ReadInt();
            if (n < 0 || n > RzoneCap) return;
            var list = new List<RzoneDef>(n);
            for (var i = 0; i < n; i++) list.Add(RzoneRead(pkg));
            self._rzones = list;
            self._rzonePending = false;
            self._rzonePinsDirty = true;
        }

        private void RzoneList()
        {
            if (!W9Send("AP_SrvRzoneList")) return;
            _rzonePending = true;
            _rzoneAskedAt = Time.time;
        }

        private void RzoneSend(int op, RzoneDef z)
        {
            var pkg = W9Pkg();
            pkg.Write(op);
            RzoneWrite(pkg, z);
            if (W9Send("AP_SrvRzoneSet", pkg)) { _rzonePending = true; _rzoneAskedAt = Time.time; }
        }

        private void RzoneSaveEdit()
        {
            var z = _rzoneEdit.Clone();
            z.Name = (z.Name ?? "").Trim();
            if (z.Name.Length == 0) { Message(Loc.T("rzone.msg_name")); return; }
            z.R = Mathf.Clamp(TkFloat(_rzoneR, 20f), 2f, 1000f);
            z.Hx = Mathf.Clamp(TkFloat(_rzoneHx, 20f), 2f, 1000f);
            z.Hz = Mathf.Clamp(TkFloat(_rzoneHz, 20f), 2f, 1000f);
            z.SpeedPct = Mathf.Clamp(TkInt(_rzoneSpeed, 25), -50, 200);
            z.Heal = Mathf.Clamp(TkFloat(_rzoneHeal, 5f), 0f, 200f);
            RzoneSend(0, z);
        }

        private void RzoneLoadEdit(RzoneDef z)
        {
            _rzoneEdit = z.Clone();
            _rzoneR = TkF(z.R);
            _rzoneHx = TkF(z.Hx);
            _rzoneHz = TkF(z.Hz);
            _rzoneSpeed = TkI(z.SpeedPct);
            _rzoneHeal = TkF1(z.Heal);
        }

        // ---- map pins ----

        internal void RzoneTick()
        {
            if (_rzonePins.Count > 0 && Minimap.instance == null) _rzonePins.Clear();
            if (!_rzonePinsDirty) return;
            _rzonePinsDirty = false;
            RzoneClearPins();
            if (!_rzoneShowPins || _rzones == null || !PanelGateOk) return;
            var mm = Minimap.instance;
            if (mm == null) return;
            foreach (var z in _rzones)
            {
                var c = new Vector3(z.Cx, 0f, z.Cz);
                try
                {
                    if (z.Shape == 0)
                    {
                        var area = W9AddPin(mm, c, Minimap.PinType.EventArea, "");
                        if (area != null) { area.m_worldSize = z.R * 2f; _rzonePins.Add(area); }
                    }
                    else
                    {
                        foreach (var corner in new[] { new Vector3(-z.Hx, 0, -z.Hz), new Vector3(z.Hx, 0, -z.Hz), new Vector3(z.Hx, 0, z.Hz), new Vector3(-z.Hx, 0, z.Hz) })
                        {
                            var pin = W9AddPin(mm, c + corner, Minimap.PinType.Icon3, "");
                            if (pin != null) _rzonePins.Add(pin);
                        }
                    }
                    var label = W9AddPin(mm, c, Minimap.PinType.Icon3, z.Name + (z.On ? "" : " (off)"));
                    if (label != null) _rzonePins.Add(label);
                }
                catch (Exception) { }
            }
        }

        internal void RzoneGateClosed()
        {
            _rzoneShowPins = false;
            _rzonePinsDirty = false;
            RzoneClearPins();
        }

        internal void RzoneClearPins()
        {
            var mm = Minimap.instance;
            foreach (var p in _rzonePins) { try { if (mm != null && p != null) mm.RemovePin(p); } catch (Exception) { } }
            _rzonePins.Clear();
        }

        private string RzoneRulesText(int rules)
        {
            var parts = new List<string>();
            foreach (var r in RzoneRules) if ((rules & r.Bit) != 0) parts.Add(Loc.T(r.Key));
            return parts.Count == 0 ? Loc.T("rzone.no_rules") : string.Join(", ", parts.ToArray());
        }

        // ---- draw ----

        internal void DrawRzoneSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                if (_rzones == null && !_rzonePending && TkReachable()) RzoneList();
                _rzonesLayout = _rzones;
                _rzoneWaitLayout = W9WaitKey(_rzonePending, _rzoneAskedAt, "rzone.idle");
                _rzoneShapeLayout = _rzoneEdit.Shape;
            }

            BeginCard(Loc.T("rzone.section"));
            GUILayout.Label(Loc.T("rzone.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("rzone.refresh"), _buttonStyle, GUILayout.MinWidth(90))) RzoneList();
            var show = GUILayout.Toggle(_rzoneShowPins, " " + Loc.T("rzone.show_map"), _toggleStyle);
            if (show != _rzoneShowPins) { _rzoneShowPins = show; _rzonePinsDirty = true; }
            GUILayout.Label(Loc.T(_rzoneWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var list = _rzonesLayout;
            if (list != null)
            {
                if (list.Count == 0) GUILayout.Label(Loc.T("rzone.none"), _hintStyle);
                else
                {
                    _rzoneScroll = GUILayout.BeginScrollView(_rzoneScroll, GUILayout.Height(Mathf.Min(200f, list.Count * 52f + 10f)));
                    for (var i = 0; i < list.Count; i++)
                    {
                        var z = list[i];
                        GUILayout.BeginVertical(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(W9Short(z.Name, 20) + (z.On ? "" : " " + Loc.T("rzone.off")), _cellStyle, GUILayout.Width(150));
                        GUILayout.Label(z.Shape == 0 ? Loc.T("rzone.circle_n", TkF(z.R)) : Loc.T("rzone.rect_n", TkF(z.Hx * 2f), TkF(z.Hz * 2f)), _dimCellStyle, GUILayout.Width(110));
                        GUILayout.Label(W9Pos(new Vector3(z.Cx, 0f, z.Cz)), _dimCellStyle, GUILayout.Width(90));
                        if (GUILayout.Button(Loc.T("rzone.tp"), _buttonStyle, GUILayout.MinWidth(40))) TeleportToWorld(new Vector3(z.Cx, 0f, z.Cz), z.Name);
                        if (GUILayout.Button(Loc.T("rzone.edit"), _buttonStyle, GUILayout.MinWidth(50))) RzoneLoadEdit(z);
                        if (GUILayout.Button(Loc.T(z.On ? "rzone.disable" : "rzone.enable"), _buttonStyle, GUILayout.MinWidth(70))) RzoneSend(2, z);
                        if (ConfirmButton("rzone:del:" + z.Id, Loc.T("rzone.delete"), GUILayout.MinWidth(60))) RzoneSend(1, z);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        GUILayout.Label("  " + RzoneRulesText(z.Rules), _dimCellStyle);
                        GUILayout.EndVertical();
                    }
                    GUILayout.EndScrollView();
                }
            }

            // ---- editor ----
            var e = _rzoneEdit;
            DrawSection(Loc.T(e.Id.Length > 0 ? "rzone.edit_title" : "rzone.new_title"));
            e.Name = W9Field("rzone.name", e.Name, 32, 200f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("rzone.shape"), _labelStyle, GUILayout.MinWidth(120));
            if (GUILayout.Toggle(_rzoneShapeLayout == 0, Loc.T("rzone.circle"), _catStyle) && _rzoneShapeLayout != 0) e.Shape = 0;
            if (GUILayout.Toggle(_rzoneShapeLayout == 1, Loc.T("rzone.rect"), _catStyle) && _rzoneShapeLayout != 1) e.Shape = 1;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("rzone.center", W9Pos(new Vector3(e.Cx, 0f, e.Cz))), _labelStyle, GUILayout.MinWidth(170));
            if (GUILayout.Button(Loc.T("rzone.use_me"), _buttonStyle, GUILayout.MinWidth(90)) && LocalPlayer != null)
            { var p = LocalPlayer.transform.position; e.Cx = p.x; e.Cz = p.z; }
            if (GUILayout.Button(Loc.T("rzone.use_map"), _buttonStyle, GUILayout.MinWidth(110)))
            {
                Vector3 w;
                if (W9MapCursor(out w)) { e.Cx = w.x; e.Cz = w.z; }
                else Message(Loc.T("rzone.msg_open_map"));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (_rzoneShapeLayout == 0)
            {
                GUILayout.Label(Loc.T("rzone.radius"), _labelStyle, GUILayout.MinWidth(120));
                _rzoneR = GUILayout.TextField(_rzoneR ?? "", 5, _textFieldStyle, GUILayout.Width(60));
            }
            else
            {
                GUILayout.Label(Loc.T("rzone.half_x"), _labelStyle, GUILayout.MinWidth(120));
                _rzoneHx = GUILayout.TextField(_rzoneHx ?? "", 5, _textFieldStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("rzone.half_z"), _labelStyle, GUILayout.MinWidth(60));
                _rzoneHz = GUILayout.TextField(_rzoneHz ?? "", 5, _textFieldStyle, GUILayout.Width(60));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("rzone.rules"), _labelStyle);
            GUILayout.BeginHorizontal();
            for (var i = 0; i < RzoneRules.Length; i++)
            {
                if (i == 5) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                var bit = RzoneRules[i].Bit;
                var on = (e.Rules & bit) != 0;
                var now = GUILayout.Toggle(on, " " + Loc.T(RzoneRules[i].Key), _toggleStyle, GUILayout.MinWidth(120));
                if (now != on) e.Rules = now ? e.Rules | bit : e.Rules & ~bit;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("rzone.speed_pct"), _labelStyle, GUILayout.MinWidth(120));
            _rzoneSpeed = GUILayout.TextField(_rzoneSpeed ?? "", 4, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("rzone.heal"), _labelStyle, GUILayout.MinWidth(120));
            _rzoneHeal = GUILayout.TextField(_rzoneHeal ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            e.Msg = W9Field("rzone.message", e.Msg, 160, 320f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("rzone.save"), _buttonStyle, GUILayout.MinWidth(100))) RzoneSaveEdit();
            if (GUILayout.Button(Loc.T("rzone.new"), _buttonStyle, GUILayout.MinWidth(80))) RzoneLoadEdit(new RzoneDef());
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("rzone.client_hint"), _hintStyle);
            EndCard();
        }
    }
}
