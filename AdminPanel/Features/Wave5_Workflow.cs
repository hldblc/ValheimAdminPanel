using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 5 - Workflow section (Extras tab) ====================
    // Admin quality-of-life, 100% client-side: a quick-action strip, a unified favorites view, a ring of
    // recently-affected players, multi-select batch operations and an in-panel cheatsheet.
    //
    // Member prefix: "Wf". Locale prefix: "ux2.".
    //
    // Nothing in this file invents a new wire protocol. Every action re-uses a helper that already exists in
    // AdminPanelPlugin.cs (SendServerGive / SendServerSpawn / SrvRpc("AP_SrvHeal"|"AP_SrvKick"|"AP_SrvTeleport")
    // / LocalPlayer.TeleportTo), so a server companion that is missing or old degrades exactly the way the
    // corresponding base-tab button already does.
    //
    // IMGUI law observed throughout: EVERY collection that decides a control count (quick-action slots, the
    // three favorite lists, the recent ring, the roster and the batch selection) is pinned on the Layout pass
    // and only the pinned copy is read while drawing. Config-backed lists are never mutated in place - an edit
    // builds a fresh list, writes the config string and lets the NEXT Layout pass re-parse it, so a Remove
    // click can never shorten the list Repaint is walking. The batch checkboxes are the classic trap: the row
    // list comes from _othersSnapshot (already built once per frame in DrawWindow) and a checkbox never adds
    // or removes controls - it only flips a bool inside a row that is drawn either way.
    public partial class AdminPanelPlugin
    {
        // ---- limits ----
        private const int WfMaxSlots = 12;     // quick-action slots kept in config
        private const int WfMaxRecent = 10;    // recently-affected ring size (task spec: max 10)
        private const int WfBatchCap = 20;     // hard cap of targets touched by one batch action
        private const int WfMaxFavList = 40;   // per-list cap for the creature/player favorites

        // ---- config ----
        private ConfigEntry<bool> _wfSectionCfg;
        private ConfigEntry<string> _wfSlotsCfg;
        private ConfigEntry<string> _wfFavCreaturesCfg;
        private ConfigEntry<string> _wfFavPlayersCfg;
        private bool _wfInited;

        /// <summary>
        /// Runner for quick-action command strings. The command registry lives in a sibling feature file, so
        /// this file never references it: the glue file assigns this property (typically to the command
        /// palette's runner) after both files have inited. Null = the palette is absent or disabled, and the
        /// quick-action strip renders dimmed with an explanatory hint instead of vanishing (a strip that
        /// appears/disappears would change the control count of the section).
        /// Contract: returns true when the command was recognized and executed.
        /// </summary>
        internal Func<string, bool> WfCommandRunner { get; set; }

        // ---- recently-affected ring (in-memory only, per world) ----
        private struct WfTarget
        {
            public string Name;
            public long Uid;
        }

        private readonly List<WfTarget> _wfRecent = new List<WfTarget>();

        // ---- selection (live; written by checkboxes on the event pass) ----
        private readonly HashSet<long> _wfSelection = new HashSet<long>();

        // ---- config-parse caches, keyed on the raw config string (the PresetsKv/BookmarksKv idiom) ----
        private string _wfSlotsRaw, _wfFavCreRaw, _wfFavPlrRaw;
        private List<KeyValuePair<string, string>> _wfSlotsCache;
        private List<string> _wfFavCreCache, _wfFavPlrCache;

        // ---- per-frame Layout snapshots (the ONLY thing draw code reads) ----
        private int _wfSub;          // 0 slots, 1 favorites, 2 recent, 3 batch, 4 help
        private int _wfSubLayout;
        private bool _wfConnectedLayout;
        private bool _wfRunnerLayout;
        private List<KeyValuePair<string, string>> _wfSlotsLayout;
        private List<KeyValuePair<string, string>> _wfFavItemRowsLayout;   // prefab -> display
        private List<string> _wfFavCreaturesLayout, _wfFavPlayersLayout;
        private List<WfTarget> _wfRecentLayout;
        private List<ZNet.PlayerInfo> _wfRosterLayout;
        private HashSet<long> _wfSelectionLayout;

        // ---- editor text fields ----
        private string _wfNewSlotLabel = "";
        private string _wfNewSlotCmd = "";
        private string _wfNewCreature = "";
        private string _wfNewPlayer = "";
        private string _wfGiveAmount = "1";

        private Vector2 _wfScroll, _wfHelpScroll;

        private static readonly string[] WfSubKeys =
        { "ux2.sub_slots", "ux2.sub_favorites", "ux2.sub_recent", "ux2.sub_batch", "ux2.sub_help" };

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and points WfCommandRunner at the
        // palette. Everything here is client-local UI state, so there are no patches and no RPC registrations.
        internal void WfInit()
        {
            if (_wfInited) return;
            _wfInited = true;
            _wfSectionCfg = Config.Bind("Features", "ShowWorkflowSection", true,
                "Show the Workflow section in the Extras tab (quick actions, favorites, recent players, batch operations, in-panel help). Client-side UI only.");
            _wfSlotsCfg = Config.Bind("Features", "QuickActionSlots", "",
                "Quick-action bar slots as label=command pairs separated by '|'. '%', '|' and '=' inside a value are percent-escaped (%25/%7C/%3D). Edited from the panel; hand-editing works too.");
            _wfFavCreaturesCfg = Config.Bind("Features", "FavoriteCreatures", "",
                "Favorite creature prefab names, separated by '|' (percent-escaped). Shown in the Extras > Workflow > Favorites view.");
            _wfFavPlayersCfg = Config.Bind("Features", "FavoritePlayers", "",
                "Favorite player names, separated by '|' (percent-escaped). Shown in the Extras > Workflow > Favorites view.");
        }

        internal bool WfSectionEnabled() => _wfSectionCfg == null || _wfSectionCfg.Value;

        // Called on logout. Every per-world field must die here: the recent ring and the batch selection hold
        // player uids from the world we just left, and a stale uid would aim a Kick at whoever inherits it on
        // the next server. The config-backed favorite/slot lists are global settings and deliberately survive;
        // WfCommandRunner is wired once at init and must survive too, or quick actions break after a relog.
        internal void WfReset()
        {
            _wfRecent.Clear();
            _wfSelection.Clear();
            _wfSlotsRaw = null; _wfFavCreRaw = null; _wfFavPlrRaw = null;
            _wfSlotsCache = null; _wfFavCreCache = null; _wfFavPlrCache = null;
            _wfSub = 0;
            _wfSubLayout = 0;
            _wfConnectedLayout = false;
            _wfRunnerLayout = false;
            _wfSlotsLayout = null;
            _wfFavItemRowsLayout = null;
            _wfFavCreaturesLayout = null;
            _wfFavPlayersLayout = null;
            _wfRecentLayout = null;
            _wfRosterLayout = null;
            _wfSelectionLayout = null;
            _wfNewSlotLabel = "";
            _wfNewSlotCmd = "";
            _wfNewCreature = "";
            _wfNewPlayer = "";
            _wfGiveAmount = "1";
            _wfScroll = Vector2.zero;
            _wfHelpScroll = Vector2.zero;
        }

        /// <summary>
        /// Record a player as "recently affected" so the Workflow section can offer re-target shortcuts.
        /// Safe to call from anywhere (draw code, RPC-adjacent code): it only touches an in-memory list that
        /// the UI reads through a Layout snapshot, so it can never change a control count mid-frame.
        /// </summary>
        internal void WfNoteTarget(string displayName, long uid)
        {
            var name = string.IsNullOrEmpty(displayName) ? null : displayName.Trim();
            if (string.IsNullOrEmpty(name) && uid == 0L) return;
            if (string.IsNullOrEmpty(name)) name = uid.ToString();

            for (var i = _wfRecent.Count - 1; i >= 0; i--)
            {
                var e = _wfRecent[i];
                var same = uid != 0L && e.Uid == uid;
                if (!same && uid == 0L && e.Uid == 0L) same = e.Name == name;
                if (same) _wfRecent.RemoveAt(i);
            }
            _wfRecent.Insert(0, new WfTarget { Name = name, Uid = uid });
            while (_wfRecent.Count > WfMaxRecent) _wfRecent.RemoveAt(_wfRecent.Count - 1);
        }

        // ==================== config-backed lists ====================
        // Same escaping as ParseKv/JoinKv (EncKv/DecKv), so a label with '=' or '|' round-trips and an old
        // plain-text config still loads unchanged.

        private List<KeyValuePair<string, string>> WfSlots()
        {
            var raw = _wfSlotsCfg != null ? (_wfSlotsCfg.Value ?? "") : "";
            if (_wfSlotsCache == null || _wfSlotsRaw != raw)
            {
                _wfSlotsCache = WfParseSlots(raw);
                _wfSlotsRaw = raw;
            }
            return _wfSlotsCache;
        }

        private static List<KeyValuePair<string, string>> WfParseSlots(string raw)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var pair in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0) continue;
                list.Add(new KeyValuePair<string, string>(DecKv(pair.Substring(0, idx)), DecKv(pair.Substring(idx + 1))));
                if (list.Count >= WfMaxSlots) break;
            }
            return list;
        }

        private static string WfJoinSlots(List<KeyValuePair<string, string>> list)
        {
            var sb = new StringBuilder();
            foreach (var kv in list)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(EncKv(kv.Key)).Append('=').Append(EncKv(kv.Value));
            }
            return sb.ToString();
        }

        private static List<string> WfParseList(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var part in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = DecKv(part);
                if (string.IsNullOrEmpty(v)) continue;
                if (!list.Contains(v)) list.Add(v);
                if (list.Count >= WfMaxFavList) break;
            }
            return list;
        }

        private static string WfJoinList(List<string> list)
        {
            var sb = new StringBuilder();
            foreach (var v in list)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(EncKv(v));
            }
            return sb.ToString();
        }

        private List<string> WfFavCreatures()
        {
            var raw = _wfFavCreaturesCfg != null ? (_wfFavCreaturesCfg.Value ?? "") : "";
            if (_wfFavCreCache == null || _wfFavCreRaw != raw) { _wfFavCreCache = WfParseList(raw); _wfFavCreRaw = raw; }
            return _wfFavCreCache;
        }

        private List<string> WfFavPlayers()
        {
            var raw = _wfFavPlayersCfg != null ? (_wfFavPlayersCfg.Value ?? "") : "";
            if (_wfFavPlrCache == null || _wfFavPlrRaw != raw) { _wfFavPlrCache = WfParseList(raw); _wfFavPlrRaw = raw; }
            return _wfFavPlrCache;
        }

        // Favorite ITEMS are owned by the Items tab (_favorites / the "Favorites" config key). This view is a
        // read-only mirror: it never writes _favorites, so starring stays a single-owner operation.
        // Built once per Layout pass - one pass over the item index, not one lookup per favorite per pass.
        private List<KeyValuePair<string, string>> WfBuildFavItemRows()
        {
            var rows = new List<KeyValuePair<string, string>>();
            var favs = _favorites;
            if (favs == null || favs.Count == 0) return rows;

            var seen = new HashSet<string>();
            if (_itemIndex != null)
            {
                foreach (var e in _itemIndex)
                {
                    if (e == null || e.Prefab == null || !favs.Contains(e.Prefab)) continue;
                    if (!seen.Add(e.Prefab)) continue;
                    rows.Add(new KeyValuePair<string, string>(e.Prefab, string.IsNullOrEmpty(e.Display) ? e.Prefab : e.Display));
                }
            }
            // Favorites for items the object DB has not loaded (or no longer ships) still get a row, keyed by
            // prefab name, so the list never silently loses entries after a game update.
            foreach (var p in favs)
            {
                if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
                rows.Add(new KeyValuePair<string, string>(p, p));
            }
            rows.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        // ==================== drawing ====================

        internal void DrawWorkflowSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _wfSubLayout = _wfSub;
                _wfConnectedLayout = ZNet.instance != null;
                _wfRunnerLayout = WfCommandRunner != null;
                _wfSlotsLayout = WfSlots();
                _wfFavItemRowsLayout = WfBuildFavItemRows();
                _wfFavCreaturesLayout = WfFavCreatures();
                _wfFavPlayersLayout = WfFavPlayers();
                _wfRecentLayout = new List<WfTarget>(_wfRecent);
                _wfRosterLayout = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
                _wfSelectionLayout = new HashSet<long>(_wfSelection);
            }

            // First-frame / defensive fallbacks (spec: consumers must tolerate a null snapshot).
            var slots = _wfSlotsLayout ?? (_wfSlotsLayout = WfSlots());
            var recent = _wfRecentLayout ?? (_wfRecentLayout = new List<WfTarget>(_wfRecent));
            var roster = _wfRosterLayout ?? (_wfRosterLayout = new List<ZNet.PlayerInfo>());
            var selection = _wfSelectionLayout ?? (_wfSelectionLayout = new HashSet<long>(_wfSelection));

            WfDrawQuickBar(slots);

            GUILayout.BeginHorizontal();
            for (var i = 0; i < WfSubKeys.Length; i++)
            {
                var on = _wfSub == i;
                if (GUILayout.Toggle(on, Loc.T(WfSubKeys[i]), _catStyle) && !on)
                {
                    _wfSub = i;
                    _wfScroll = Vector2.zero;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            _wfScroll = GUILayout.BeginScrollView(_wfScroll, GUILayout.Height(ListView(230f)));
            switch (_wfSubLayout)
            {
                case 0: WfDrawSlotEditor(slots); break;
                case 1: WfDrawFavorites(roster); break;
                case 2: WfDrawRecent(recent, roster); break;
                case 3: WfDrawBatch(roster, selection); break;
                default: WfDrawHelp(); break;
            }
            GUILayout.EndScrollView();
        }

        // ---- 1. quick-action bar ----

        // A compact strip of user-chosen buttons, drawn above the chip row so it is reachable from every view
        // of this section. Not an overlay: the floating overlay slot belongs to the command palette.
        private void WfDrawQuickBar(List<KeyValuePair<string, string>> slots)
        {
            BeginCard(Loc.T("ux2.qa_section"));
            if (slots.Count == 0)
            {
                GUILayout.Label(Loc.T("ux2.qa_empty"), _hintStyle);
            }
            else
            {
                // GUI.contentColor is restored on every exit path (including a throw inside a click handler),
                // otherwise the dim tint would leak into every control drawn after this card.
                var prevColor = GUI.contentColor;
                try
                {
                    if (!_wfRunnerLayout) GUI.contentColor = new Color(0.62f, 0.60f, 0.55f, 1f);
                    var perRow = 0;
                    GUILayout.BeginHorizontal();
                    foreach (var slot in slots)
                    {
                        if (perRow == 6) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); perRow = 0; }
                        if (GUILayout.Button(slot.Key, _buttonStyle, GUILayout.MinWidth(110)))
                            WfRunCommand(slot.Key, slot.Value);
                        perRow++;
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
                finally { GUI.contentColor = prevColor; }
            }
            GUILayout.Label(_wfRunnerLayout ? Loc.T("ux2.qa_hint") : Loc.T("ux2.qa_no_runner"), _hintStyle);
            EndCard();
        }

        // The command registry lives in a sibling feature file and may be absent entirely; this file only ever
        // sees the delegate. Foreign code, so the invoke is wrapped: a throwing command degrades to a "not
        // recognized" toast instead of tearing the frame.
        private void WfRunCommand(string label, string command)
        {
            var runner = WfCommandRunner;
            if (runner == null) { Message(Loc.T("ux2.qa_no_runner")); return; }
            var ok = false;
            try { ok = runner(command ?? ""); }
            catch (Exception ex) { Logger.LogWarning($"Quick action '{label}' failed: {ex.Message}"); }
            Message(Loc.T(ok ? "ux2.qa_ran" : "ux2.qa_failed", label));
        }

        private void WfDrawSlotEditor(List<KeyValuePair<string, string>> slots)
        {
            BeginCard(Loc.T("ux2.slots_section"));
            if (slots.Count == 0) GUILayout.Label(Loc.T("ux2.qa_empty"), _hintStyle);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(slot.Key, _cellStyle, GUILayout.Width(150));
                GUILayout.Label(slot.Value, _dimCellStyle, GUILayout.Width(240));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("ux2.run"), _buttonStyle, GUILayout.MinWidth(60)))
                    WfRunCommand(slot.Key, slot.Value);
                if (GUILayout.Button(Loc.T("ux2.up"), _buttonStyle, GUILayout.MinWidth(50)))
                    WfMoveSlotUp(i);
                if (GUILayout.Button(Loc.T("ux2.remove"), _buttonStyle, GUILayout.MinWidth(80)))
                    WfRemoveSlot(i);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ux2.label_label"), _labelStyle, GUILayout.MinWidth(60));
            _wfNewSlotLabel = GUILayout.TextField(_wfNewSlotLabel ?? "", _textFieldStyle, GUILayout.MinWidth(120));
            GUILayout.Label(Loc.T("ux2.cmd_label"), _labelStyle, GUILayout.MinWidth(70));
            _wfNewSlotCmd = GUILayout.TextField(_wfNewSlotCmd ?? "", _textFieldStyle, GUILayout.MinWidth(160));
            if (GUILayout.Button(Loc.T("ux2.add"), _buttonStyle, GUILayout.MinWidth(70)))
                WfAddSlot();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("ux2.slots_hint"), _hintStyle);
            EndCard();
        }

        // Config-backed list edits ALWAYS build a fresh list: the pinned _wfSlotsLayout must keep the length
        // Layout reserved controls for, and the rewrite only becomes visible on the next Layout pass.
        private void WfAddSlot()
        {
            var label = WfTrim(_wfNewSlotLabel);
            var cmd = WfTrim(_wfNewSlotCmd);
            if (label == null || cmd == null) { Message(Loc.T("ux2.msg_need_label")); return; }
            var current = WfSlots();
            if (current.Count >= WfMaxSlots) { Message(Loc.T("ux2.slots_full", WfMaxSlots)); return; }
            var next = new List<KeyValuePair<string, string>>(current);
            for (var i = next.Count - 1; i >= 0; i--)
                if (string.Equals(next[i].Key, label, StringComparison.OrdinalIgnoreCase)) next.RemoveAt(i);
            next.Add(new KeyValuePair<string, string>(label, cmd));
            WfSaveSlots(next);
            _wfNewSlotLabel = "";
            _wfNewSlotCmd = "";
            Message(Loc.T("ux2.msg_slot_added", label));
        }

        private void WfRemoveSlot(int index)
        {
            var current = WfSlots();
            if (index < 0 || index >= current.Count) return;
            var label = current[index].Key;
            var next = new List<KeyValuePair<string, string>>(current);
            next.RemoveAt(index);
            WfSaveSlots(next);
            Message(Loc.T("ux2.msg_slot_removed", label));
        }

        private void WfMoveSlotUp(int index)
        {
            var current = WfSlots();
            if (index <= 0 || index >= current.Count) return;
            var next = new List<KeyValuePair<string, string>>(current);
            var tmp = next[index - 1];
            next[index - 1] = next[index];
            next[index] = tmp;
            WfSaveSlots(next);
        }

        private void WfSaveSlots(List<KeyValuePair<string, string>> next)
        {
            if (_wfSlotsCfg == null) return;
            _wfSlotsCfg.Value = WfJoinSlots(next);
            Config.Save();
        }

        // ---- 2. cross-tab favorites ----

        private void WfDrawFavorites(List<ZNet.PlayerInfo> roster)
        {
            var itemRows = _wfFavItemRowsLayout ?? (_wfFavItemRowsLayout = WfBuildFavItemRows());
            var creatures = _wfFavCreaturesLayout ?? (_wfFavCreaturesLayout = WfFavCreatures());
            var players = _wfFavPlayersLayout ?? (_wfFavPlayersLayout = WfFavPlayers());

            // items
            BeginCard(Loc.T("ux2.fav_items"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ux2.amount"), _labelStyle, GUILayout.MinWidth(60));
            _wfGiveAmount = GUILayout.TextField(_wfGiveAmount ?? "1", _textFieldStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (itemRows.Count == 0) GUILayout.Label(Loc.T("ux2.fav_items_empty"), _hintStyle);
            for (var i = 0; i < itemRows.Count; i++)
            {
                var row = itemRows[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(row.Value, _cellStyle, GUILayout.Width(210));
                GUILayout.Label(row.Key, _dimCellStyle, GUILayout.Width(180));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("ux2.give_me"), _buttonStyle, GUILayout.MinWidth(90)))
                    WfGiveSelf(row.Key, row.Value);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(Loc.T("ux2.fav_items_hint"), _hintStyle);
            EndCard();

            // creatures
            BeginCard(Loc.T("ux2.fav_creatures"));
            if (creatures.Count == 0) GUILayout.Label(Loc.T("ux2.fav_creatures_empty"), _hintStyle);
            for (var i = 0; i < creatures.Count; i++)
            {
                var name = creatures[i];
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(name, _cellStyle, GUILayout.Width(260));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("ux2.spawn"), _buttonStyle, GUILayout.MinWidth(80)))
                    WfSpawnCreature(name);
                if (GUILayout.Button(Loc.T("ux2.remove"), _buttonStyle, GUILayout.MinWidth(80)))
                    WfSetFavCreature(name, false);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            _wfNewCreature = GUILayout.TextField(_wfNewCreature ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("ux2.add"), _buttonStyle, GUILayout.MinWidth(70)))
            {
                var n = WfTrim(_wfNewCreature);
                if (n == null) Message(Loc.T("ux2.msg_need_name"));
                else { WfSetFavCreature(n, true); _wfNewCreature = ""; }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("ux2.fav_add_hint"), _hintStyle);
            EndCard();

            // players
            BeginCard(Loc.T("ux2.fav_players"));
            if (players.Count == 0) GUILayout.Label(Loc.T("ux2.fav_players_empty"), _hintStyle);
            for (var i = 0; i < players.Count; i++)
            {
                var name = players[i];
                var online = WfFindOnline(roster, name, out var info);
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(name, _cellStyle, GUILayout.Width(180));
                GUILayout.Label(online ? Loc.T("ux2.online") : Loc.T("ux2.offline"), _dimCellStyle, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                // The three buttons exist whether or not the player is online: an offline row that dropped its
                // buttons would change this card's control count between two frames' worth of roster data.
                if (GUILayout.Button(Loc.T("ux2.tp_to"), _buttonStyle, GUILayout.MinWidth(70)))
                {
                    if (!online) Message(Loc.T("ux2.fav_offline", name));
                    else WfTeleportTo(info.m_position, name, PeerIdOf(info));
                }
                if (GUILayout.Button(Loc.T("ux2.summon"), _buttonStyle, GUILayout.MinWidth(80)))
                {
                    if (!online) Message(Loc.T("ux2.fav_offline", name));
                    else WfSummon(PeerIdOf(info), name, true);
                }
                if (GUILayout.Button(Loc.T("ux2.remove"), _buttonStyle, GUILayout.MinWidth(80)))
                    WfSetFavPlayer(name, false);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            _wfNewPlayer = GUILayout.TextField(_wfNewPlayer ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("ux2.add"), _buttonStyle, GUILayout.MinWidth(70)))
            {
                var n = WfTrim(_wfNewPlayer);
                if (n == null) Message(Loc.T("ux2.msg_need_name"));
                else { WfSetFavPlayer(n, true); _wfNewPlayer = ""; }
            }
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void WfSetFavCreature(string name, bool add)
        {
            if (_wfFavCreaturesCfg == null || string.IsNullOrEmpty(name)) return;
            var next = new List<string>(WfFavCreatures());
            var changed = WfApplyFav(next, name, add);
            if (!changed) return;
            _wfFavCreaturesCfg.Value = WfJoinList(next);
            Config.Save();
            if (add && _creatureIndex != null && !WfKnownCreature(name)) Message(Loc.T("ux2.unknown_prefab", name));
            else Message(Loc.T(add ? "ux2.msg_fav_added" : "ux2.msg_fav_removed", name));
        }

        private void WfSetFavPlayer(string name, bool add)
        {
            if (_wfFavPlayersCfg == null || string.IsNullOrEmpty(name)) return;
            var next = new List<string>(WfFavPlayers());
            var changed = WfApplyFav(next, name, add);
            if (!changed) return;
            _wfFavPlayersCfg.Value = WfJoinList(next);
            Config.Save();
            Message(Loc.T(add ? "ux2.msg_fav_added" : "ux2.msg_fav_removed", name));
        }

        private static bool WfApplyFav(List<string> list, string name, bool add)
        {
            var idx = -1;
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (add)
            {
                if (idx >= 0 || list.Count >= WfMaxFavList) return false;
                list.Add(name);
                return true;
            }
            if (idx < 0) return false;
            list.RemoveAt(idx);
            return true;
        }

        private bool WfKnownCreature(string name)
        {
            var idx = _creatureIndex;
            if (idx == null) return true;   // index not built yet - do not cry wolf
            foreach (var e in idx)
                if (e != null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void WfGiveSelf(string prefab, string display)
        {
            var uid = SelfUid();
            if (uid == 0L) { Message(Loc.T("players.not_connected")); return; }
            var amount = 1;
            if (int.TryParse(WfTrim(_wfGiveAmount) ?? "", out var parsed)) amount = Mathf.Clamp(parsed, 1, 999);
            SendServerGive(uid, prefab, amount, 1);
            Message(Loc.T("ux2.msg_gave", display, amount));
        }

        private void WfSpawnCreature(string prefab)
        {
            if (LocalPlayer == null) { Message(Loc.T("players.not_connected")); return; }
            SendServerSpawn(1, prefab, SpawnPos(), 1, 1, false);
            Message(Loc.T("ux2.msg_spawned", prefab));
        }

        // ---- 3. recently-affected players ----

        private void WfDrawRecent(List<WfTarget> recent, List<ZNet.PlayerInfo> roster)
        {
            BeginCard(Loc.T("ux2.recent_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ux2.recent_hint", WfMaxRecent), _hintStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("ux2.clear"), _buttonStyle, GUILayout.MinWidth(80)))
            { _wfRecent.Clear(); Message(Loc.T("ux2.msg_recent_cleared")); }
            GUILayout.EndHorizontal();

            if (recent.Count == 0) GUILayout.Label(Loc.T("ux2.recent_empty"), _hintStyle);
            for (var i = 0; i < recent.Count; i++)
            {
                var t = recent[i];
                var online = WfFindOnlineByUid(roster, t, out var info);
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                GUILayout.Label(t.Name, _cellStyle, GUILayout.Width(180));
                GUILayout.Label(online ? Loc.T("ux2.online") : Loc.T("ux2.offline"), _dimCellStyle, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("ux2.tp_to"), _buttonStyle, GUILayout.MinWidth(70)))
                {
                    if (!online) Message(Loc.T("ux2.fav_offline", t.Name));
                    else WfTeleportTo(info.m_position, t.Name, PeerIdOf(info));
                }
                if (GUILayout.Button(Loc.T("ux2.summon"), _buttonStyle, GUILayout.MinWidth(80)))
                {
                    if (!online) Message(Loc.T("ux2.fav_offline", t.Name));
                    else WfSummon(PeerIdOf(info), t.Name, true);
                }
                if (GUILayout.Button(Loc.T("ux2.heal"), _buttonStyle, GUILayout.MinWidth(60)))
                {
                    if (!online) Message(Loc.T("ux2.fav_offline", t.Name));
                    else WfHeal(PeerIdOf(info), t.Name);
                }
                if (GUILayout.Button(Loc.T("ux2.fav_toggle"), _buttonStyle, GUILayout.MinWidth(60)))
                    WfSetFavPlayer(t.Name, true);
                GUILayout.EndHorizontal();
            }
            EndCard();
        }

        // ---- 4. multi-select batch operations ----

        private void WfDrawBatch(List<ZNet.PlayerInfo> roster, HashSet<long> selection)
        {
            BeginCard(Loc.T("ux2.batch_section"));
            if (!_wfConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                EndCard();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("ux2.selected_count", selection.Count), _labelStyle, GUILayout.MinWidth(110));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("ux2.select_all"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                foreach (var p in roster) _wfSelection.Add(PeerIdOf(p));
            }
            if (GUILayout.Button(Loc.T("ux2.select_none"), _buttonStyle, GUILayout.MinWidth(80)))
                _wfSelection.Clear();
            GUILayout.EndHorizontal();

            if (roster.Count == 0) GUILayout.Label(Loc.T("ux2.batch_empty"), _hintStyle);
            for (var i = 0; i < roster.Count; i++)
            {
                var p = roster[i];
                var uid = PeerIdOf(p);
                GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                // A checkbox is exactly ONE control whether ticked or not, and the row list itself comes from
                // the per-frame roster snapshot, so ticking can never change what Repaint has to draw.
                var was = selection.Contains(uid);
                var now = GUILayout.Toggle(was, " " + p.m_name, _toggleStyle, GUILayout.MinWidth(190));
                if (now != was)
                {
                    if (now) _wfSelection.Add(uid);
                    else _wfSelection.Remove(uid);
                }
                GUILayout.Label($"({p.m_position.x:0}, {p.m_position.z:0})", _cellStyle, GUILayout.Width(110));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("ux2.fav_toggle"), _buttonStyle, GUILayout.MinWidth(60)))
                    WfSetFavPlayer(p.m_name, true);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("ux2.batch_heal"), _buttonStyle, GUILayout.MinWidth(110)))
                WfBatchRun(roster, selection, 0);
            if (GUILayout.Button(Loc.T("ux2.batch_tp"), _buttonStyle, GUILayout.MinWidth(120)))
                WfBatchRun(roster, selection, 1);
            if (GUILayout.Button(Loc.T("ux2.batch_summon"), _buttonStyle, GUILayout.MinWidth(120)))
                WfBatchRun(roster, selection, 2);
            // Kicking a group is the one irreversible action here, so it takes the two-click treatment.
            if (ConfirmButton("wf:batchKick", Loc.T("ux2.batch_kick"), GUILayout.MinWidth(110)))
                WfBatchRun(roster, selection, 3);
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("ux2.batch_cap_hint", WfBatchCap), _hintStyle);
            EndCard();
        }

        // action: 0 heal, 1 bring to me, 2 summon in front of me, 3 kick.
        // Iterates the per-frame roster (not the selection set) so the order is the roster order and a uid that
        // has since disconnected is simply skipped. Hard-capped at WfBatchCap targets per press.
        private void WfBatchRun(List<ZNet.PlayerInfo> roster, HashSet<long> selection, int action)
        {
            if (selection == null || selection.Count == 0) { Message(Loc.T("ux2.msg_batch_none")); return; }
            if ((action == 1 || action == 2) && LocalPlayer == null) { Message(Loc.T("players.not_connected")); return; }

            var done = 0;
            foreach (var p in roster)
            {
                if (done >= WfBatchCap) break;
                var uid = PeerIdOf(p);
                if (uid == 0L || !selection.Contains(uid)) continue;
                switch (action)
                {
                    case 0: SrvRpc("AP_SrvHeal", uid); break;
                    case 1: WfSendTeleport(uid, LocalPlayer.transform.position + Vector3.up * 0.5f); break;
                    case 2: WfSendTeleport(uid, LocalPlayer.transform.position + LocalPlayer.transform.forward * 2f); break;
                    default: SrvRpc("AP_SrvKick", uid); break;
                }
                WfNoteTarget(p.m_name, uid);
                done++;
            }

            string key;
            switch (action)
            {
                case 0: key = "ux2.msg_batch_heal"; break;
                case 1: key = "ux2.msg_batch_tp"; break;
                case 2: key = "ux2.msg_batch_summon"; break;
                default: key = "ux2.msg_batch_kick"; break;
            }
            Message(Loc.T(key, done));
        }

        // ---- 5. in-panel help ----

        // Prose only - no state, no server calls. Every line is short enough to stay readable at font size 16,
        // and the block scrolls on its own so the section does not grow without bound.
        private void WfDrawHelp()
        {
            BeginCard(Loc.T("ux2.help_section"));
            _wfHelpScroll = GUILayout.BeginScrollView(_wfHelpScroll, GUILayout.Height(Mathf.Min(460f, ListView(330f))));

            GUILayout.Label(Loc.T("ux2.help_tabs_title"), _headerStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_items"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_creatures"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_bosses"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_player"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_world"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_players"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_server"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_tab_settings"), _proseStyle);

            GUILayout.Space(8);
            GUILayout.Label(Loc.T("ux2.help_extras_title"), _headerStyle);
            GUILayout.Label(Loc.T("ux2.help_extras"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_extras2"), _proseStyle);

            GUILayout.Space(8);
            GUILayout.Label(Loc.T("ux2.help_cap_title"), _headerStyle);
            GUILayout.Label(Loc.T("ux2.help_cap"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_cap2"), _proseStyle);

            GUILayout.Space(8);
            GUILayout.Label(Loc.T("ux2.help_cfg_title"), _headerStyle);
            GUILayout.Label(Loc.T("ux2.help_cfg"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_cfg2"), _proseStyle);

            GUILayout.Space(8);
            GUILayout.Label(Loc.T("ux2.help_trouble_title"), _headerStyle);
            GUILayout.Label(Loc.T("ux2.help_t1"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_t2"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_t3"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_t4"), _proseStyle);
            GUILayout.Label(Loc.T("ux2.help_t5"), _proseStyle);

            GUILayout.EndScrollView();
            EndCard();
        }

        // ==================== small helpers ====================

        // Every player-facing action funnels through these three so the recent ring gets populated even when
        // nothing else in the mod calls WfNoteTarget.
        private void WfTeleportTo(Vector3 pos, string name, long uid)
        {
            if (LocalPlayer == null) { Message(Loc.T("players.not_connected")); return; }
            LocalPlayer.TeleportTo(pos + Vector3.up, LocalPlayer.transform.rotation, true);
            WfNoteTarget(name, uid);
            Message(Loc.T("ux2.msg_tp_to", name));
        }

        private void WfSummon(long uid, string name, bool inFront)
        {
            if (LocalPlayer == null) { Message(Loc.T("players.not_connected")); return; }
            WfSendTeleport(uid, inFront
                ? LocalPlayer.transform.position + LocalPlayer.transform.forward * 2f
                : LocalPlayer.transform.position + Vector3.up * 0.5f);
            WfNoteTarget(name, uid);
            Message(Loc.T("ux2.msg_summoned", name));
        }

        private void WfHeal(long uid, string name)
        {
            if (uid == 0L) return;
            SrvRpc("AP_SrvHeal", uid);
            WfNoteTarget(name, uid);
            Message(Loc.T("ux2.msg_healed", name));
        }

        // Same payload shape as SummonPlayer (uid + destination), but keyed off a uid so it also works for a
        // recent-list entry whose ZNet.PlayerInfo we no longer hold.
        private void WfSendTeleport(long uid, Vector3 dest)
        {
            if (uid == 0L) return;
            var pkg = new ZPackage();
            pkg.Write(uid);
            pkg.Write(dest);
            SrvRpc("AP_SrvTeleport", pkg);
        }

        private static bool WfFindOnline(List<ZNet.PlayerInfo> roster, string name, out ZNet.PlayerInfo found)
        {
            found = default(ZNet.PlayerInfo);
            if (roster == null || string.IsNullOrEmpty(name)) return false;
            foreach (var p in roster)
                if (string.Equals(p.m_name, name, StringComparison.OrdinalIgnoreCase)) { found = p; return true; }
            return false;
        }

        // Prefer the stable uid; fall back to the name so an entry recorded before the roster refreshed still
        // resolves (and so entries noted with uid 0 by other features are still actionable).
        private static bool WfFindOnlineByUid(List<ZNet.PlayerInfo> roster, WfTarget t, out ZNet.PlayerInfo found)
        {
            found = default(ZNet.PlayerInfo);
            if (roster == null) return false;
            if (t.Uid != 0L)
                foreach (var p in roster)
                    if (PeerIdOf(p) == t.Uid) { found = p; return true; }
            return WfFindOnline(roster, t.Name, out found);
        }

        private static string WfTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }
    }
}
