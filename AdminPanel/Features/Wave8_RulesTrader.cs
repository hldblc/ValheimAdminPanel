using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 8 — Trader stock and price editor (client side, #18) ====================
    // Edit what Haldor, Hildir, the Bog Witch (any Trader prefab, by its GameObject name) sell and for how
    // much. The table lives on the server ("trader": <traderKey>|<index> -> item|stack|price|requiredKey);
    // modded clients replace Trader.m_items from it, unmodded clients keep vanilla stock — the hint says so.
    // A trader with NO rows keeps its vanilla list, so "Reset to vanilla" simply deletes the trader's rows.
    //
    // IMGUI discipline: the rows shown are filtered by the trader key typed in the text field, and both the
    // reply and the typed key change outside the draw, so the filtered list is rebuilt on the Layout pass
    // only and every gate reads that snapshot.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _trSectionCfg;

        private sealed class TrRow
        {
            public string Trader = "";
            public int Index;
            public string Item = "";
            public int Stack;
            public int Price;
            public string Key = "";
        }

        // ---- live payload ----
        private List<TrRow> _trRows;       // null = no reply yet
        private bool _trEnabled;
        private bool _trPending;
        private bool _trRequested;
        private float _trNextReq;

        // ---- UI state ----
        private string _trTrader = "Haldor";
        private string _trItem = "";
        private string _trStack = "1";
        private string _trPrice = "100";
        private string _trKey = "";
        private int _trEditIndex = -1;     // row being edited (its fields are loaded into the form); -1 = adding
        private Vector2 _trScroll;

        // ---- Layout snapshots ----
        private List<TrRow> _trRowsLayout;      // rows of the trader typed in the field; null = no reply yet
        private string _trOthersLayout = "";    // other traders that have rows (label text only)
        private bool _trEnabledLayout;
        private bool _trPendingLayout;
        private bool _trHaveLayout;
        private int _trEditIndexLayout = -1;

        private const int TrRowCap = 100;   // wire contract with the companion

        // The three vanilla traders' prefab names, offered as quick picks; any other Trader prefab can be
        // typed by name.
        private static readonly string[] TrKnownTraders = { "Haldor", "Hildir", "BogWitch" };

        // ---- lifecycle ----

        internal void TrInit()
        {
            _trSectionCfg = Config.Bind("Features", "ShowTraderStockSection", true,
                "Show the Trader Stock section in the Tools tab (server-defined trader items and prices, applied by modded clients).");
        }

        internal bool TrSectionEnabled() => _trSectionCfg == null || _trSectionCfg.Value;

        internal void TrReset()
        {
            _trRows = null;
            _trEnabled = false;
            _trPending = false;
            _trRequested = false;
            _trNextReq = 0f;
            _trTrader = "Haldor";
            _trItem = "";
            _trStack = "1";
            _trPrice = "100";
            _trKey = "";
            _trEditIndex = -1;
            _trScroll = Vector2.zero;
            _trRowsLayout = null;
            _trOthersLayout = "";
            _trEnabledLayout = false;
            _trPendingLayout = false;
            _trHaveLayout = false;
            _trEditIndexLayout = -1;
        }

        // ---- reply ----

        private static void TrOnData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseTraderStockData(self, pkg);
        }

        // AP_TraderStockData: {int ver, bool enabled, int n(<=100), n x (string trader, int index, string item,
        //                      int stack, int price, string key)}
        internal static void ParseTraderStockData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != RulesVer) return;
                var enabled = pkg.ReadBool();
                var n = pkg.ReadInt();
                if (n < 0 || n > TrRowCap) return;
                var rows = new List<TrRow>(n);
                for (var i = 0; i < n; i++)
                {
                    rows.Add(new TrRow
                    {
                        Trader = pkg.ReadString() ?? "",
                        Index = pkg.ReadInt(),
                        Item = pkg.ReadString() ?? "",
                        Stack = pkg.ReadInt(),
                        Price = pkg.ReadInt(),
                        Key = pkg.ReadString() ?? "",
                    });
                }
                self._trRows = rows;
                self._trEnabled = enabled;
                self._trPending = false;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- requests ----

        private void TrRequest()
        {
            if (!RulesReachable(false)) return;
            if (Time.time < _trNextReq) return;
            _trNextReq = Time.time + 2f;
            _trPending = true;
            SrvRpc("AP_SrvTraderReq");
        }

        // AP_SrvTraderSet: {int ver, int action (0 set row / 1 remove row / 2 reset trader), string trader,
        //                   int index (-1 = append), string item, int stack, int price, string key}
        private void TrSend(int action, int index)
        {
            if (!RulesReachable(true)) return;
            var trader = (_trTrader ?? "").Trim();
            if (trader.Length == 0) { Message(Loc.T("trader.msg_need_trader")); return; }

            var item = "";
            var stack = 1;
            var price = 100;
            var key = "";
            if (action == 0)
            {
                item = (_trItem ?? "").Trim();
                if (item.Length == 0) { Message(Loc.T("trader.msg_need_item")); return; }
                if (!RulesParseInt(_trStack, out stack) || stack < 1 || stack > 999) { Message(Loc.T("trader.msg_bad_stack")); return; }
                if (!RulesParseInt(_trPrice, out price) || price < 0 || price > 999999) { Message(Loc.T("trader.msg_bad_price")); return; }
                key = (_trKey ?? "").Trim();
            }

            var pkg = new ZPackage();
            pkg.Write(RulesVer);
            pkg.Write(action);
            pkg.Write(trader);
            pkg.Write(index);
            pkg.Write(item);
            pkg.Write(stack);
            pkg.Write(price);
            pkg.Write(key);
            SrvRpc("AP_SrvTraderSet", pkg);
            _trPending = true;

            if (action == 0)
            {
                Message(Loc.T(index < 0 ? "trader.msg_added" : "trader.msg_saved", item, trader));
                _trItem = "";
                _trKey = "";
                _trEditIndex = -1;
            }
            else if (action == 1) Message(Loc.T("trader.msg_removed", index, trader));
            else Message(Loc.T("trader.msg_reset", trader));
        }

        private void TrBeginEdit(TrRow r)
        {
            if (r == null) return;
            _trEditIndex = r.Index;
            _trItem = r.Item;
            _trStack = r.Stack.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _trPrice = r.Price.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _trKey = r.Key;
        }

        // Rebuilt on the Layout pass only (the reply and the typed trader key both change outside the draw).
        private void TrRebuildLayout()
        {
            _trHaveLayout = _trRows != null;
            _trEnabledLayout = _trEnabled;
            _trPendingLayout = _trPending;
            _trEditIndexLayout = _trEditIndex;
            if (_trRows == null) { _trRowsLayout = null; _trOthersLayout = ""; return; }

            var trader = (_trTrader ?? "").Trim();
            var mine = new List<TrRow>();
            var others = new List<string>();
            foreach (var r in _trRows)
            {
                if (string.Equals(r.Trader, trader, StringComparison.Ordinal)) mine.Add(r);
                else if (!others.Contains(r.Trader)) others.Add(r.Trader);
            }
            mine.Sort((a, b) => a.Index.CompareTo(b.Index));
            others.Sort(StringComparer.Ordinal);
            _trRowsLayout = mine;
            _trOthersLayout = others.Count > 0 ? string.Join(", ", others.ToArray()) : "";
        }

        // ---- draw ----

        internal void DrawTraderStockSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                TrRebuildLayout();
                if (!_trRequested && RulesReachable(false)) { _trRequested = true; TrRequest(); }   // see MapPins: only once reachable
            }

            BeginCard(Loc.T("trader.section"));
            RulesDrawStatusRow(_trHaveLayout, _trEnabledLayout, RulesChTrader,
                "trader.status_unknown", "trader.status_on", "trader.status_off", "trader.turn_on", "trader.turn_off");

            // Trader picker: free text plus the three vanilla names as quick picks (always three buttons).
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("trader.trader"), _labelStyle, GUILayout.MinWidth(70));
            _trTrader = GUILayout.TextField(_trTrader ?? "", 32, _textFieldStyle, GUILayout.MinWidth(120));
            for (var i = 0; i < TrKnownTraders.Length; i++)
                if (GUILayout.Button(TrKnownTraders[i], _buttonStyle, GUILayout.MinWidth(80)))
                { _trTrader = TrKnownTraders[i]; _trEditIndex = -1; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // One label either way: "other traders with rows: ..." or the dash.
            GUILayout.Label(Loc.T("trader.others", _trOthersLayout.Length > 0 ? _trOthersLayout : "-"), _dimLabelStyle);

            // Row form (add or edit, decided by the pinned edit index — only the button label swaps).
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("trader.item"), _labelStyle, GUILayout.MinWidth(50));
            _trItem = GUILayout.TextField(_trItem ?? "", 64, _textFieldStyle, GUILayout.MinWidth(150));
            GUILayout.Label(Loc.T("trader.stack"), _labelStyle, GUILayout.MinWidth(50));
            _trStack = GUILayout.TextField(_trStack ?? "", 4, _textFieldStyle, GUILayout.MinWidth(45));
            GUILayout.Label(Loc.T("trader.price"), _labelStyle, GUILayout.MinWidth(50));
            _trPrice = GUILayout.TextField(_trPrice ?? "", 7, _textFieldStyle, GUILayout.MinWidth(65));
            GUILayout.Label(Loc.T("trader.key"), _labelStyle, GUILayout.MinWidth(50));
            _trKey = GUILayout.TextField(_trKey ?? "", 40, _textFieldStyle, GUILayout.MinWidth(110));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            var editing = _trEditIndexLayout >= 0;
            if (GUILayout.Button(Loc.T(editing ? "trader.save_row" : "trader.add_row"), _buttonStyle, GUILayout.MinWidth(110)))
                TrSend(0, editing ? _trEditIndexLayout : -1);
            if (GUILayout.Button(Loc.T("trader.cancel_edit"), _buttonStyle, GUILayout.MinWidth(90)))
            { _trEditIndex = -1; _trItem = ""; _trKey = ""; }
            GUILayout.FlexibleSpace();
            // Per-trader confirm id: arming Reset for Haldor must never arm it for Hildir.
            if (ConfirmButton("trader:reset:" + (_trTrader ?? ""), Loc.T("trader.reset_vanilla"), GUILayout.MinWidth(130)))
                TrSend(2, -1);
            if (GUILayout.Button(Loc.T("trader.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                _trNextReq = 0f;
                _trRequested = false;
            }
            GUILayout.EndHorizontal();

            var rows = _trRowsLayout;
            if (rows == null)
            {
                GUILayout.Label(Loc.T(_trPendingLayout ? "trader.pending" : "trader.idle"), _hintStyle);
            }
            else if (rows.Count == 0)
            {
                GUILayout.Label(Loc.T("trader.empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("trader.col_index"), _headerStyle, GUILayout.Width(40));
                GUILayout.Label(Loc.T("trader.col_item"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("trader.col_stack"), _headerStyle, GUILayout.Width(50));
                GUILayout.Label(Loc.T("trader.col_price"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("trader.col_key"), _headerStyle, GUILayout.MinWidth(100));
                GUILayout.EndHorizontal();

                _trScroll = GUILayout.BeginScrollView(_trScroll,
                    GUILayout.Height(Mathf.Min(ListView(380f), rows.Count * 28f + 16f)));
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), _dimCellStyle, GUILayout.Width(40));
                    GUILayout.Label(r.Item, _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(r.Stack.ToString(System.Globalization.CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(50));
                    GUILayout.Label(r.Price.ToString(System.Globalization.CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(70));
                    GUILayout.Label(r.Key.Length > 0 ? r.Key : "-", _dimCellStyle, GUILayout.MinWidth(100));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("trader.edit"), _buttonStyle, GUILayout.MinWidth(60))) TrBeginEdit(r);
                    if (ConfirmButton("trader:del:" + r.Trader + ":" + r.Index, Loc.T("trader.remove"), GUILayout.MinWidth(80)))
                        TrSend(1, r.Index);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Label(Loc.T("trader.hint"), _hintStyle);
            EndCard();
        }
    }
}
