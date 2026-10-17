using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #6 admin map overlays (World tab → Teleport card, and over the open map) ====================
    // Wards with their radius, portals and their links, ships and carts, tames, recent deaths and the busiest zones,
    // as layers on the vanilla map. One companion snapshot (AP_SrvOvlReq -> AP_OvlData, Wave9SrvOverlays.cs) feeds
    // every layer; the pins are added with save=false, so they never reach the admin's map file and vanish with the
    // session, a layer switch-off, or the admin gate closing. The same chips float over the large map while the panel
    // is open, because the map is where the question "whose ward is this?" gets asked.
    public partial class AdminPanelPlugin
    {
        private const int OvlLayers = 6;
        private const int OvlPointCap = 1200;

        private static readonly string[] OvlLayerKeys =
            { "ovl.l_wards", "ovl.l_portals", "ovl.l_vehicles", "ovl.l_tames", "ovl.l_deaths", "ovl.l_density" };

        private sealed class OvlPin
        {
            public int Layer;
            public Minimap.PinData Pin;
        }

        private ConfigEntry<bool> _ovlCfg;
        private int _ovlMask;                      // layers switched on
        private int _ovlLoadedMask;                // layers the current pins came from
        private readonly List<OvlPin> _ovlPins = new List<OvlPin>();
        private bool _ovlPending;
        private float _ovlAskedAt, _ovlNextReq;
        private int _ovlCount;
        private long _ovlMs;

        private int _ovlMaskLayout;
        private bool _ovlPendingLayout;
        private int _ovlCountLayout;

        internal void OvlInit()
        {
            _ovlCfg = Config.Bind("Features", "ShowMapOverlays", true,
                "Show the admin map-layer chips (wards, portals, vehicles, tames, deaths, density) in the World tab and over the open map.");
        }

        internal void OvlReset()
        {
            OvlClearPins();
            _ovlMask = 0;
            _ovlLoadedMask = 0;
            _ovlPending = false;
            _ovlCount = 0;
        }

        internal static void OvlRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_OvlData", ParseOvlData);

        private static void ParseOvlData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var mask = pkg.ReadInt();
            var ms = pkg.ReadLong();
            var n = pkg.ReadInt();
            if (n < 0 || n > OvlPointCap) return;
            var pts = new List<(int Layer, Vector3 Pos, float Radius, string Label)>(n);
            for (var i = 0; i < n; i++)
                pts.Add((pkg.ReadByte(), pkg.ReadVector3(), pkg.ReadSingle(), pkg.ReadString() ?? ""));
            self.OvlApply(mask, ms, pts);
        }

        private void OvlApply(int mask, long ms, List<(int Layer, Vector3 Pos, float Radius, string Label)> pts)
        {
            _ovlPending = false;
            if (!PanelGateOk) return;   // a reply that was in flight when the gate closed: admin pins never on a closed gate
            OvlClearPins();
            var mm = Minimap.instance;
            if (mm == null) return;
            _ovlLoadedMask = mask;
            _ovlMs = ms;
            foreach (var p in pts)
            {
                if (p.Layer < 0 || p.Layer >= OvlLayers || (_ovlMask & (1 << p.Layer)) == 0) continue;
                try
                {
                    var label = p.Label;
                    switch (p.Layer)
                    {
                        case 0:   // ward: the protected circle + a house marker carrying the owner
                            OvlAdd(0, W9AddPin(mm, p.Pos, Minimap.PinType.EventArea, ""), p.Radius);
                            OvlAdd(0, W9AddPin(mm, p.Pos, Minimap.PinType.Icon1, label), 0f);
                            break;
                        case 1: OvlAdd(1, W9AddPin(mm, p.Pos, Minimap.PinType.Icon4, label), 0f); break;
                        case 2: OvlAdd(2, W9AddPin(mm, p.Pos, Minimap.PinType.Icon2, label), 0f); break;
                        case 3: OvlAdd(3, W9AddPin(mm, p.Pos, Minimap.PinType.Icon3, label), 0f); break;
                        case 4: OvlAdd(4, W9AddPin(mm, p.Pos, Minimap.PinType.Death, label), 0f); break;
                        case 5:   // density: one zone-sized area + a fire marker with the count
                            OvlAdd(5, W9AddPin(mm, p.Pos, Minimap.PinType.EventArea, ""), p.Radius);
                            OvlAdd(5, W9AddPin(mm, p.Pos, Minimap.PinType.Icon0, label), 0f);
                            break;
                    }
                }
                catch (Exception e) { Logger.LogWarning($"Map overlay pin failed: {e.Message}"); break; }
            }
            _ovlCount = _ovlPins.Count;
        }

        private void OvlAdd(int layer, Minimap.PinData pin, float radius)
        {
            if (pin == null) return;
            if (radius > 0f) pin.m_worldSize = radius * 2f;
            _ovlPins.Add(new OvlPin { Layer = layer, Pin = pin });
        }

        internal void OvlClearPins()
        {
            var mm = Minimap.instance;
            foreach (var p in _ovlPins)
            {
                try { if (mm != null && p.Pin != null) mm.RemovePin(p.Pin); }
                catch (Exception) { }
            }
            _ovlPins.Clear();
            _ovlCount = 0;
        }

        private void OvlRemoveLayer(int layer)
        {
            var mm = Minimap.instance;
            for (var i = _ovlPins.Count - 1; i >= 0; i--)
            {
                if (_ovlPins[i].Layer != layer) continue;
                try { if (mm != null && _ovlPins[i].Pin != null) mm.RemovePin(_ovlPins[i].Pin); }
                catch (Exception) { }
                _ovlPins.RemoveAt(i);
            }
            _ovlCount = _ovlPins.Count;
            _ovlLoadedMask &= ~(1 << layer);   // switched back on later, it must be fetched again
        }

        /// <summary>Gate closed: no pins, and nothing left switched on that would bring them back.</summary>
        internal void OvlGateClosed()
        {
            OvlClearPins();
            _ovlMask = 0;
            _ovlLoadedMask = 0;
            _ovlPending = false;
        }

        private void OvlRequest()
        {
            if (_ovlMask == 0) { OvlClearPins(); return; }
            if (Time.time < _ovlNextReq) return;
            _ovlNextReq = Time.time + 2f;
            var pkg = W9Pkg();
            pkg.Write(_ovlMask);
            if (!W9Send("AP_SrvOvlReq", pkg)) return;
            _ovlPending = true;
            _ovlAskedAt = Time.time;
        }

        private void OvlToggle(int layer, bool on)
        {
            var bit = 1 << layer;
            if (on) { _ovlMask |= bit; OvlRequest(); }
            else { _ovlMask &= ~bit; OvlRemoveLayer(layer); }
        }

        internal void OvlTick()
        {
            // A world change destroys the Minimap and every pin with it; forget the stale references.
            if (_ovlPins.Count > 0 && Minimap.instance == null) { _ovlPins.Clear(); _ovlCount = 0; }
            // A layer switched on while a snapshot for other layers was already running: ask again once it lands.
            if (!_ovlPending && (_ovlMask & ~_ovlLoadedMask) != 0 && Time.time >= _ovlNextReq && PanelGateOk) OvlRequest();
        }

        // ---- draw: chip row in the Teleport card ----

        internal void DrawOvlChipRow()
        {
            if (_ovlCfg != null && !_ovlCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                _ovlMaskLayout = _ovlMask;
                _ovlPendingLayout = _ovlPending && Time.time - _ovlAskedAt < W9NoAnswerSeconds;
                _ovlCountLayout = _ovlCount;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ovl.title"), _labelStyle, GUILayout.MinWidth(135));
            OvlChips(_ovlMaskLayout, _catStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Space(139);
            if (GUILayout.Button(Loc.T("ovl.refresh"), _buttonStyle, GUILayout.MinWidth(80))) OvlRequest();
            if (GUILayout.Button(Loc.T("ovl.clear"), _buttonStyle, GUILayout.MinWidth(70))) { _ovlMask = 0; OvlClearPins(); }
            GUILayout.Label(_ovlPendingLayout ? Loc.T("ovl.loading") : Loc.T("ovl.count", _ovlCountLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void OvlChips(int maskSnapshot, GUIStyle style)
        {
            for (var i = 0; i < OvlLayers; i++)
            {
                var wasOn = (maskSnapshot & (1 << i)) != 0;
                var on = GUILayout.Toggle(wasOn, Loc.T(OvlLayerKeys[i]), style);
                if (on != wasOn) OvlToggle(i, on);
            }
        }

        // ---- draw: the same chips over the large map while the panel is open ----

        private int _ovlGuiMaskLayout;

        internal void OvlGui()
        {
            if (!_visible || (_ovlCfg != null && !_ovlCfg.Value) || !PanelGateOk) return;
            if (Minimap.instance == null || !Minimap.IsOpen()) return;
            EnsureSkin();
            ApplyFont();
            if (Event.current.type == EventType.Layout) _ovlGuiMaskLayout = _ovlMask;
            GUILayout.BeginArea(new Rect(12f, 12f, 760f, 40f));
            GUILayout.BeginHorizontal(_cardStyle ?? GUI.skin.box);
            GUILayout.Label(Loc.T("ovl.title"), _labelStyle);
            OvlChips(_ovlGuiMaskLayout, _catStyle ?? GUI.skin.button);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
