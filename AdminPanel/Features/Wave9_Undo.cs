using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #13 universal undo timeline (title bar + Tools › Shortcuts › Undo timeline) ====================
    // Every reversible admin action records its inverse on the server (Wave9SrvUndo.cs): give, teleport, spawn, skill,
    // status effect, ban - and, reported from here, the admin's own teleports and the piece editor's remove / move /
    // rotate. One timeline shared by all admins; undo one row, or everything one admin did in the last N minutes. The
    // title-bar Undo reverses the requesting admin's own last action.
    public partial class AdminPanelPlugin
    {
        private const int UndoRowCap = 150;

        private sealed class UndoRow
        {
            public int Id;
            public long Ticks;
            public string AdminName = "", AdminId = "", Kind = "", Target = "", Summary = "";
            public bool Undone, Undoable;
        }

        private ConfigEntry<bool> _undoSectionCfg;
        private List<UndoRow> _undoRows;
        private string _undoMinutes = "10";
        private string _undoAdminFilter = "", _undoAdminFilterName = "";
        private bool _undoPending;
        private float _undoAskedAt, _undoSuppressSelfTpUntil;
        private Vector2 _undoScroll;

        private List<UndoRow> _undoRowsLayout;
        private string _undoWaitLayout = "undo.idle";
        private bool _undoTitleLayout;
        private string _undoFilterLayout = "";

        internal void UndoInit()
        {
            _undoSectionCfg = Config.Bind("Features", "ShowUndoTimeline", true,
                "Show the Undo button in the panel's title bar and the shared undo timeline in Tools > Shortcuts.");
            try { Harmony.CreateAndPatchAll(typeof(UndoSelfTeleportPatch)); }
            catch (Exception e) { Logger.LogWarning($"Undo timeline: self-teleport recording unavailable: {e.Message}"); }
        }

        internal bool UndoSectionEnabled() => _undoSectionCfg == null || _undoSectionCfg.Value;

        internal void UndoReset()
        {
            _undoRows = null;
            _undoPending = false;
            _undoRowsLayout = null;
        }

        internal static void UndoRegisterReplies(ZRoutedRpc r)
        {
            W9Reply(r, "AP_UndoData", ParseUndoData);
            W9Reply(r, "AP_UndoPieceLocal", ParseUndoPieceLocal);
        }

        private static void ParseUndoData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var n = pkg.ReadInt();
            if (n < 0 || n > UndoRowCap) return;
            var rows = new List<UndoRow>(n);
            for (var i = 0; i < n; i++)
                rows.Add(new UndoRow
                {
                    Id = pkg.ReadInt(), Ticks = pkg.ReadLong(), AdminName = pkg.ReadString() ?? "", AdminId = pkg.ReadString() ?? "",
                    Kind = pkg.ReadString() ?? "", Target = pkg.ReadString() ?? "", Summary = pkg.ReadString() ?? "",
                    Undone = pkg.ReadBool(), Undoable = pkg.ReadBool(),
                });
            self._undoRows = rows;
            self._undoPending = false;
        }

        // The undoing admin's own loaded copy of a moved piece snaps back at once (others see it on reload, exactly
        // like the original edit, because pieces carry no transform sync).
        private static void ParseUndoPieceLocal(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var id = pkg.ReadZDOID();
            var pos = pkg.ReadVector3();
            var rot = pkg.ReadQuaternion();
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
            if (go == null) return;
            go.transform.position = pos;
            go.transform.rotation = rot;
        }

        private void UndoList()
        {
            var pkg = W9Pkg();
            pkg.Write(_undoAdminFilter ?? "");
            pkg.Write(0);
            if (!W9Send("AP_SrvUndoList", pkg)) return;
            _undoPending = true;
            _undoAskedAt = Time.time;
        }

        private bool UndoDo(int mode, int id, string adminId, int minutes)
        {
            var pkg = W9Pkg();
            pkg.Write(mode);
            pkg.Write(id);
            pkg.Write(adminId ?? "");
            pkg.Write(minutes);
            if (!W9Send("AP_SrvUndoDo", pkg)) return false;
            _undoSuppressSelfTpUntil = Time.time + 4f;   // the server's teleport-back must not be journaled as a new move
            _undoPending = true;
            _undoAskedAt = Time.time;
            return true;
        }

        // ---- reporters (called by the panel's own edits) ----

        private bool UndoJournal(string kind, string data, string summary)
        {
            if (!PanelGateOk || !TkReachable()) return false;
            var pkg = W9Pkg();
            pkg.Write(kind);
            pkg.Write(data ?? "");
            pkg.Write(summary ?? "");
            SrvRpc("AP_SrvJournal", pkg);
            return true;
        }

        /// <summary>Wave5_BuildTools calls this BEFORE destroying a piece: the ZDO's full saved state, so it can be recreated.</summary>
        /// <returns>false when the removal could NOT be recorded (the caller tells the admin).</returns>
        internal bool UndoReportPieceRemoved(ZDO zdo, string label)
        {
            if (zdo == null) return false;
            try
            {
                var pkg = new ZPackage();
                pkg.Write((short)42);
                pkg.Write(1);
                zdo.Save(pkg);
                return UndoJournal("piece_rm", Convert.ToBase64String(pkg.GetArray()), "removed piece " + label);
            }
            catch (Exception e) { Logger.LogWarning($"Undo timeline: could not record a piece removal: {e.Message}"); return false; }
        }

        /// <summary>Wave5_BuildTools calls this BEFORE moving or rotating a piece.</summary>
        internal void UndoReportPieceMoved(ZDO zdo, Vector3 oldPos, Quaternion oldRot, string label)
        {
            if (zdo == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var data = zdo.m_uid.UserID.ToString(inv) + ":" + zdo.m_uid.ID.ToString(inv) + "|" +
                       string.Format(inv, "{0:0.###};{1:0.###};{2:0.###}", oldPos.x, oldPos.y, oldPos.z) + "|" +
                       string.Format(inv, "{0:0.#####};{1:0.#####};{2:0.#####};{3:0.#####}", oldRot.x, oldRot.y, oldRot.z, oldRot.w);
            UndoJournal("piece_mv", data, "moved piece " + label);
        }

        // The admin's own teleports: any TeleportTo of the local player while the panel or the large map is open is the
        // admin's doing (TakeInput is off while the panel is open, so no portal or bed can trigger one). Journaled in a
        // POSTFIX, only when the game accepted the teleport: TeleportTo returns false while one is already running or
        // within its 2 s cooldown (Player.cs:5926-5933), and a refused click must not leave a row behind.
        [HarmonyPatch]
        private static class UndoSelfTeleportPatch
        {
            [HarmonyPatch(typeof(Player), "TeleportTo")]
            [HarmonyPrefix]
            private static void Prefix(Player __instance, out Vector3 __state)
            {
                __state = __instance != null ? __instance.transform.position : Vector3.zero;
            }

            [HarmonyPatch(typeof(Player), "TeleportTo")]
            [HarmonyPostfix]
            private static void Postfix(Player __instance, Vector3 pos, bool __result, Vector3 __state)
            {
                var self = Instance;
                if (!__result || self == null || __instance != Player.m_localPlayer || Time.time < self._undoSuppressSelfTpUntil) return;
                if (!self._visible && !(Minimap.instance != null && Minimap.IsOpen())) return;
                var from = __state;
                if ((from - pos).sqrMagnitude < 4f) return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                self.UndoJournal("selftp", string.Format(inv, "{0:0.##};{1:0.##};{2:0.##}", from.x, from.y + 0.5f, from.z),
                    string.Format(inv, "teleported self to {0:0}, {1:0}", pos.x, pos.z));
            }
        }

        // ---- title bar ----

        internal void UndoTitleBar()
        {
            if (Event.current.type == EventType.Layout) _undoTitleLayout = UndoSectionEnabled() && PanelGateOk;
            if (!_undoTitleLayout) return;
            // Two clicks, like every irreversible button: it sits next to the close button, and a slip of the mouse must
            // not reverse a give or a ban. One fixed-rect control either way, so the label may change mid-frame.
            const string confirmId = "undo:title";
            var armed = _confirmAction == confirmId && Time.realtimeSinceStartup < _confirmExpiresAt;
            var label = Loc.T(armed ? "undo.title_confirm" : "undo.title_btn");
            var w = Mathf.Max(68f, _buttonStyle.CalcSize(new GUIContent(label)).x + 8f);
            if (!GUI.Button(new Rect(_windowRect.width - 34f - 14f - w, 4f, w, 22f), label, _buttonStyle)) return;
            if (!armed)
            {
                _confirmAction = confirmId;
                _confirmExpiresAt = Time.realtimeSinceStartup + ConfirmWindowSeconds;
                Message(Loc.T("undo.msg_arm"));
                return;
            }
            _confirmAction = null;
            if (UndoDo(2, 0, "", 0)) Message(Loc.T("undo.msg_last"));
        }

        // ---- section ----

        internal void DrawUndoSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                if (_undoRows == null && !_undoPending && TkReachable()) UndoList();
                _undoRowsLayout = _undoRows;
                _undoWaitLayout = W9WaitKey(_undoPending, _undoAskedAt, "undo.idle");
                _undoFilterLayout = _undoAdminFilter ?? "";
            }

            BeginCard(Loc.T("undo.section"));
            GUILayout.Label(Loc.T("undo.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("undo.refresh"), _buttonStyle, GUILayout.MinWidth(90))) UndoList();
            if (_undoFilterLayout.Length > 0)
            {
                GUILayout.Label(Loc.T("undo.filter_admin", _undoAdminFilterName), _labelStyle);
                if (GUILayout.Button(Loc.T("undo.clear_filter"), _buttonStyle, GUILayout.MinWidth(70))) { _undoAdminFilter = ""; _undoAdminFilterName = ""; UndoList(); }
            }
            GUILayout.Label(Loc.T(_undoWaitLayout), _dimLabelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (_undoFilterLayout.Length > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("undo.bulk_minutes"), _labelStyle, GUILayout.MinWidth(160));
                _undoMinutes = GUILayout.TextField(_undoMinutes ?? "", 4, _textFieldStyle, GUILayout.Width(50));
                if (ConfirmButton("undo:bulk", Loc.T("undo.bulk", _undoAdminFilterName), GUILayout.MinWidth(220)))
                    UndoDo(1, 0, _undoAdminFilter, Mathf.Clamp(TkInt(_undoMinutes, 10), 1, 1440));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            var rows = _undoRowsLayout;
            if (rows != null)
            {
                if (rows.Count == 0) GUILayout.Label(Loc.T("undo.none"), _hintStyle);
                else
                {
                    _undoScroll = GUILayout.BeginScrollView(_undoScroll, GUILayout.Height(FeatureListView(380f, 90f)));
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.Label(TkClock(r.Ticks), _dimCellStyle, GUILayout.Width(45));
                        if (GUILayout.Button(W9Short(r.AdminName, 14), _buttonStyle, GUILayout.Width(110)))
                        { _undoAdminFilter = r.AdminId; _undoAdminFilterName = r.AdminName; UndoList(); }
                        GUILayout.Label(W9Short(r.Summary + (r.Target.Length > 0 ? " (" + r.Target + ")" : ""), 52), r.Undone ? _dimCellStyle : _cellStyle);
                        GUILayout.FlexibleSpace();
                        if (r.Undone) GUILayout.Label(Loc.T("undo.undone"), _dimCellStyle, GUILayout.Width(70));
                        else if (GUILayout.Button(Loc.T("undo.undo"), _buttonStyle, GUILayout.MinWidth(60))) UndoDo(0, r.Id, "", 0);
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }
            EndCard();
        }
    }
}
