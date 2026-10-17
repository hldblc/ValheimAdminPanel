using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #10 ZDO ownership and lag-owner finder (Server tab → Performance card) ====================
    // "The server is laggy" on Valheim is usually ONE player: whoever owns a busy base simulates all of it and
    // streams it to everyone nearby, so a 300 ms owner makes the base stutter for every visitor. The census
    // (AP_SrvZownReq -> AP_ZownData, Wave9SrvZown.cs) lists how many objects each peer owns next to its ping and
    // where its biggest cluster sits; Release / Give hand that area to the engine's next pick or to a better-connected
    // player who is already nearby (a hand-off to someone far away would bounce straight back - see the server header).
    public partial class AdminPanelPlugin
    {
        private const int ZownRowCap = 40;

        private sealed class ZownCand { public long Uid; public string Name = ""; public int Ping; }

        private sealed class ZownRow
        {
            public long Uid;
            public string Name = "";
            public string Pid = "";
            public int Ping;
            public int Count;
            public Vector3 HotPos;
            public int HotCount;
            public int Zones;
            public List<ZownCand> Cands = new List<ZownCand>();
        }

        private ConfigEntry<bool> _zownCardCfg;
        private List<ZownRow> _zownRows;          // null = no census yet
        private long _zownMs;
        private int _zownScanned, _zownOwned, _zownUnowned;
        private bool _zownPending;
        private float _zownAskedAt, _zownNextReq;
        private Vector2 _zownScroll;

        private List<ZownRow> _zownRowsLayout;
        private bool _zownPendingLayout;
        private string _zownWaitKeyLayout = "zown.idle";

        internal void ZownInit()
        {
            _zownCardCfg = Config.Bind("Features", "ShowOwnershipCard", true,
                "Show the Performance card (ZDO ownership census / lag-owner finder) in the Server tab.");
        }

        internal void ZownReset()
        {
            _zownRows = null;
            _zownPending = false;
            _zownAskedAt = 0f;
            _zownNextReq = 0f;
            _zownScroll = Vector2.zero;
            _zownRowsLayout = null;
        }

        internal static void ZownRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_ZownData", ParseZownData);

        private static void ParseZownData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var ms = pkg.ReadLong();
            var scanned = pkg.ReadInt();
            var owned = pkg.ReadInt();
            var unowned = pkg.ReadInt();
            var n = pkg.ReadInt();
            if (n < 0 || n > ZownRowCap) return;
            var rows = new List<ZownRow>(n);
            for (var i = 0; i < n; i++)
            {
                var r = new ZownRow
                {
                    Uid = pkg.ReadLong(), Name = pkg.ReadString() ?? "", Pid = pkg.ReadString() ?? "", Ping = pkg.ReadInt(),
                    Count = pkg.ReadInt(), HotPos = pkg.ReadVector3(), HotCount = pkg.ReadInt(), Zones = pkg.ReadInt(),
                };
                var nc = pkg.ReadInt();
                if (nc < 0 || nc > 3) return;
                for (var c = 0; c < nc; c++)
                    r.Cands.Add(new ZownCand { Uid = pkg.ReadLong(), Name = pkg.ReadString() ?? "", Ping = pkg.ReadInt() });
                rows.Add(r);
            }
            self._zownRows = rows;
            self._zownMs = ms;
            self._zownScanned = scanned;
            self._zownOwned = owned;
            self._zownUnowned = unowned;
            self._zownPending = false;
        }

        private void ZownScan()
        {
            if (Time.time < _zownNextReq) return;
            _zownNextReq = Time.time + 3f;
            if (!W9Send("AP_SrvZownReq")) return;
            _zownPending = true;
            _zownAskedAt = Time.time;
            Message(Loc.T("zown.msg_scanning"));
        }

        private void ZownMove(ZownRow row, long newOwner, string label)
        {
            var pkg = W9Pkg();
            pkg.Write(row.HotPos);   // GetZonePos is the zone centre
            pkg.Write(48f);
            pkg.Write(newOwner);
            if (W9Send("AP_SrvZownMove", pkg)) Message(Loc.T("zown.msg_moving", label));
        }

        internal void DrawZownCard()
        {
            if (_zownCardCfg != null && !_zownCardCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                _zownRowsLayout = _zownRows;
                _zownPendingLayout = _zownPending;
                _zownWaitKeyLayout = W9WaitKey(_zownPending, _zownAskedAt, "zown.idle");
            }

            BeginCard(Loc.T("zown.section"));
            GUILayout.Label(Loc.T("zown.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("zown.scan"), _buttonStyle, GUILayout.MinWidth(150))) ZownScan();
            var rows = _zownRowsLayout;
            if (rows != null)
                GUILayout.Label(Loc.T("zown.summary", _zownOwned.ToString("N0"), _zownUnowned.ToString("N0"), _zownScanned.ToString("N0"), _zownMs), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (rows == null || _zownPendingLayout)
                GUILayout.Label(Loc.T(_zownWaitKeyLayout), _hintStyle);
            if (rows != null)
            {
                if (rows.Count == 0) GUILayout.Label(Loc.T("zown.none"), _hintStyle);
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("zown.col_owner"), _headerStyle, GUILayout.Width(150));
                    GUILayout.Label(Loc.T("zown.col_ping"), _headerStyle, GUILayout.Width(55));
                    GUILayout.Label(Loc.T("zown.col_objects"), _headerStyle, GUILayout.Width(70));
                    GUILayout.Label(Loc.T("zown.col_hot"), _headerStyle, GUILayout.Width(150));
                    GUILayout.EndHorizontal();
                    _zownScroll = GUILayout.BeginScrollView(_zownScroll, GUILayout.Height(Mathf.Min(260f, rows.Count * 56f + 12f)));
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        GUILayout.BeginVertical(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(W9Short(r.Name, 22), _cellStyle, GUILayout.Width(150));
                        GUILayout.Label(r.Ping < 0 ? "?" : r.Ping + " ms", r.Ping >= 150 ? _headerStyle : _cellStyle, GUILayout.Width(55));
                        GUILayout.Label(r.Count.ToString("N0"), _cellStyle, GUILayout.Width(70));
                        GUILayout.Label(Loc.T("zown.hot", W9Pos(r.HotPos), r.HotCount, r.Zones), _dimCellStyle, GUILayout.Width(150));
                        if (GUILayout.Button(Loc.T("zown.tp"), _buttonStyle, GUILayout.MinWidth(40)))
                            TeleportToWorld(r.HotPos, r.Name);
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(16);
                        if (GUILayout.Button(Loc.T("zown.release"), _buttonStyle, GUILayout.MinWidth(80)))
                            ZownMove(r, 0L, Loc.T("zown.released_label"));
                        foreach (var c in r.Cands)
                        {
                            if (GUILayout.Button(Loc.T("zown.give_to", W9Short(c.Name, 14), c.Ping < 0 ? "?" : c.Ping.ToString()), _buttonStyle, GUILayout.MinWidth(120)))
                                ZownMove(r, c.Uid, c.Name);
                        }
                        if (r.Cands.Count == 0) GUILayout.Label(Loc.T("zown.no_cands"), _dimCellStyle);
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                        GUILayout.EndVertical();
                    }
                    GUILayout.EndScrollView();
                }
            }
            EndCard();
        }
    }
}
