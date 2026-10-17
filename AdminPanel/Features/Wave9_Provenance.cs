using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #1 item provenance + dupe finder (Tools › Moderation › Provenance) ====================
    // Every item the panel creates carries a serial stamp and a ledger row on the server (Wave9SrvProvenance.cs has
    // the full story). The scan walks containers and ground items and asks every modded client for its stamped
    // items, then reports: the same serial on a non-stackable item in two places (a duplicate), more units on one
    // serial than the grant made, admin-made items held by players the ledger never granted anything to, and
    // stamps the ledger does not know. The Items tab marks prefabs the last scan found stamped copies of.
    public partial class AdminPanelPlugin
    {
        private const int ProvFindingCap = 120;
        private const int ProvPrefabCap = 200;

        private static readonly string[] ProvKindKeys = { "", "prov.k_dupe", "prov.k_overflow", "prov.k_nogrant", "prov.k_unknown" };

        private sealed class ProvFinding
        {
            public int Kind;
            public string Serial = "", Prefab = "", Where = "", Grant = "";
            public Vector3 Pos;
        }

        private ConfigEntry<bool> _provSectionCfg;
        private List<ProvFinding> _provFindings;    // null = no scan yet
        private int _provFindTotal, _provStamped, _provAsked, _provAnswered, _provUnreadable;
        private int _provUnreadableLayout;
        private long _provMs;
        private HashSet<string> _provPrefabs = new HashSet<string>(StringComparer.Ordinal);
        private bool _provPending;
        private float _provAskedAt, _provNextReq;
        private Vector2 _provScroll;

        private List<ProvFinding> _provFindingsLayout;
        private string _provWaitLayout = "prov.idle";
        private bool _provPendingLayout;

        internal void ProvInit()
        {
            _provSectionCfg = Config.Bind("Features", "ShowProvenanceSection", true,
                "Show the Provenance section (item serial stamps, dupe finder) in Tools > Moderation.");
        }

        internal bool ProvSectionEnabled() => _provSectionCfg == null || _provSectionCfg.Value;

        internal void ProvReset()
        {
            _provFindings = null;
            _provPrefabs = new HashSet<string>(StringComparer.Ordinal);
            _provPending = false;
            _provAskedAt = 0f;
            _provNextReq = 0f;
            _provFindingsLayout = null;
        }

        internal static void ProvRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_ProvData", ParseProvData);

        private static void ParseProvData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var ms = pkg.ReadLong();
            var stamped = pkg.ReadInt();
            var asked = pkg.ReadInt();
            var answered = pkg.ReadInt();
            var unreadable = pkg.ReadInt();
            var total = pkg.ReadInt();
            var n = pkg.ReadInt();
            if (n < 0 || n > ProvFindingCap) return;
            var list = new List<ProvFinding>(n);
            for (var i = 0; i < n; i++)
                list.Add(new ProvFinding
                {
                    Kind = pkg.ReadByte(), Serial = pkg.ReadString() ?? "", Prefab = pkg.ReadString() ?? "",
                    Where = pkg.ReadString() ?? "", Pos = pkg.ReadVector3(), Grant = pkg.ReadString() ?? "",
                });
            var np = pkg.ReadInt();
            if (np < 0 || np > ProvPrefabCap) return;
            var prefabs = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < np; i++) prefabs.Add(pkg.ReadString() ?? "");
            self._provFindings = list;
            self._provFindTotal = total;
            self._provStamped = stamped;
            self._provAsked = asked;
            self._provAnswered = answered;
            self._provUnreadable = unreadable;
            self._provMs = ms;
            self._provPrefabs = prefabs;   // swapped whole: the Items tab reads it during OnGUI, replies land in Update
            self._provPending = false;
        }

        internal void ProvItemTag(string prefab, ref string label)
        {
            if (prefab != null && _provPrefabs.Count > 0 && _provPrefabs.Contains(prefab))
                label = label + " " + Loc.T("prov.tag");
        }

        private void ProvScan()
        {
            if (Time.time < _provNextReq) return;
            _provNextReq = Time.time + 5f;
            if (!W9Send("AP_SrvProvScan")) return;
            _provPending = true;
            _provAskedAt = Time.time;
        }

        internal void DrawProvSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _provFindingsLayout = _provFindings;
                _provUnreadableLayout = _provUnreadable;   // drives a branch: pinned with the rows (a host's reply lands mid-pass)
                _provPendingLayout = _provPending;
                // A world walk takes a while on a big server; "no answer" only after a generous wait.
                _provWaitLayout = !_provPending ? "prov.idle" : (Time.time - _provAskedAt > 120f ? "w9.no_answer" : "prov.scanning");
            }

            BeginCard(Loc.T("prov.section"));
            GUILayout.Label(Loc.T("prov.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("prov.scan"), _buttonStyle, GUILayout.MinWidth(140))) ProvScan();
            GUILayout.Label(Loc.T(_provWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var rows = _provFindingsLayout;
            if (rows != null)
            {
                GUILayout.Label(Loc.T("prov.summary", _provStamped, _provAnswered, _provAsked, _provUnreadableLayout, _provMs), _labelStyle);
                if (_provUnreadableLayout > 0) GUILayout.Label(Loc.T("prov.unreadable_hint"), _hintStyle);
                if (rows.Count == 0) GUILayout.Label(Loc.T("prov.clean"), _hintStyle);
                else
                {
                    GUILayout.Label(Loc.T("prov.findings", _provFindTotal, rows.Count), _headerStyle);
                    _provScroll = GUILayout.BeginScrollView(_provScroll, GUILayout.Height(FeatureListView(420f, 110f)));
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var f = rows[i];
                        GUILayout.BeginVertical(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(Loc.T(f.Kind > 0 && f.Kind < ProvKindKeys.Length ? ProvKindKeys[f.Kind] : "prov.k_unknown"), _headerStyle, GUILayout.Width(110));
                        GUILayout.Label(f.Prefab, _cellStyle, GUILayout.Width(150));
                        GUILayout.Label(W9Short(f.Where, 46), _cellStyle);
                        GUILayout.FlexibleSpace();
                        if (f.Pos != Vector3.zero && GUILayout.Button(Loc.T("prov.tp"), _buttonStyle, GUILayout.MinWidth(40)))
                            TeleportToWorld(f.Pos, f.Where);
                        GUILayout.EndHorizontal();
                        GUILayout.Label(Loc.T("prov.serial_line", f.Serial, f.Grant.Length > 0 ? f.Grant : Loc.T("prov.no_ledger")), _dimCellStyle);
                        GUILayout.EndVertical();
                    }
                    GUILayout.EndScrollView();
                }
            }
            EndCard();
        }
    }
}
