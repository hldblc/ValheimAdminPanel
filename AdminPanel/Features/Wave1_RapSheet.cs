using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 1 — Player rap sheet (client side) ====================
    // One-shot lookup: give the companion a platform id, get back everything the server durably knows about
    // that player (presence, role, warnings, watchlist/temp-ban flags, recent chat) in a single reply.
    // Strictly read-only — every moderation action lives in its own section, so nothing here can misfire
    // from a mistyped id.
    public partial class AdminPanelPlugin
    {
        private ConfigEntry<bool> _rapSectionCfg;

        // ---- live payload (written by the RPC handler, any time) ----
        private RapSheetData _rapData;
        private bool _rapNoData;      // server answered with an empty id echo = nothing on file
        private bool _rapPending;     // a request is out and no answer has come back yet

        // ---- UI state ----
        private string _rapIdInput = "";
        private Vector2 _rapChatScroll;
        private float _rapNextReq;    // click guard; the reply is not throttled by it

        // ---- Layout snapshots ----
        private bool _rapHostLayout;
        private bool _rapNoDataLayout;
        private bool _rapPendingLayout;
        private RapSheetData _rapDataLayout;
        // Pinned from the roster DrawWindow already built this frame. Text-only: it picks which sentence the
        // idle label carries, never how many controls this section emits.
        private bool _rapSoloLayout;

        private sealed class RapSheetData
        {
            public string Id = "";
            public string LastName = "";
            public long FirstSeenTicks;
            public long LastSeenTicks;
            public int Sessions;
            public long PlaySeconds;
            public int Warnings;
            public string WarnLastReason = "";
            public bool Watchlisted;
            public bool TempBanned;
            public string Role = "";
            public List<string> Chat = new List<string>();
        }

        // ---- lifecycle ----

        internal void RapInit()
        {
            _rapSectionCfg = Config.Bind("Features", "ShowRapSheetSection", true,
                "Show the player rap-sheet lookup in the Extras tab. Read-only: it queries the server's stored player record and changes nothing.");
        }

        internal bool RapSectionEnabled() => _rapSectionCfg == null || _rapSectionCfg.Value;

        internal void RapReset()
        {
            _rapData = null;
            _rapNoData = false;
            _rapPending = false;
            _rapIdInput = "";
            _rapChatScroll = Vector2.zero;
            _rapNextReq = 0f;
            _rapHostLayout = false;
            _rapNoDataLayout = false;
            _rapPendingLayout = false;
            _rapDataLayout = null;
            _rapSoloLayout = false;
        }

        // ---- reply ----

        private static void RapOnRapSheet(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseRapSheet(self, pkg);
        }

        // Parse half, shared with the in-process bridge (a listen-server host has no server peer for the
        // gate above to authenticate against). Registered next to the RPC in Wave1_Audit's registration class.
        internal static void ParseRapSheet(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;              // payload version gate
                var d = new RapSheetData
                {
                    Id = pkg.ReadString(),
                    LastName = pkg.ReadString(),
                    FirstSeenTicks = pkg.ReadLong(),
                    LastSeenTicks = pkg.ReadLong(),
                    Sessions = pkg.ReadInt(),
                    PlaySeconds = pkg.ReadLong(),
                    Warnings = pkg.ReadInt(),
                    WarnLastReason = pkg.ReadString(),
                    Watchlisted = pkg.ReadBool(),
                    TempBanned = pkg.ReadBool(),
                    Role = pkg.ReadString(),
                };
                var chatN = pkg.ReadInt();
                if (chatN < 0 || chatN > 20) return;
                for (var i = 0; i < chatN; i++) d.Chat.Add(pkg.ReadString());

                self._rapPending = false;
                // An empty id echo is the companion's "nothing on file" answer, not a malformed packet.
                if (string.IsNullOrEmpty(d.Id)) { self._rapData = null; self._rapNoData = true; }
                else { self._rapData = d; self._rapNoData = false; }
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- formatting ----

        // Ticks are DateTime.UtcNow.Ticks written by the server. Out-of-range values (0, negative, a clock
        // that ran off the end of the calendar) must not throw out of the draw code, so they read "never".
        private string RapWhen(long ticks)
        {
            if (ticks <= 0L || ticks > DateTime.MaxValue.Ticks) return Loc.T("rap.never");
            try
            {
                var utc = new DateTime(ticks, DateTimeKind.Utc);
                var local = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                var span = DateTime.UtcNow - utc;
                if (span.Ticks < 0) span = TimeSpan.Zero;
                string ago;
                if (span.TotalDays >= 1) ago = Loc.T("rap.ago_days", (int)span.TotalDays);
                else if (span.TotalHours >= 1) ago = Loc.T("rap.ago_hours", (int)span.TotalHours);
                else ago = Loc.T("rap.ago_minutes", (int)span.TotalMinutes);
                return Loc.T("rap.seen_line", local, ago);
            }
            catch (Exception) { return Loc.T("rap.never"); }
        }

        private void RapRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.MinWidth(120));
            GUILayout.Label(value ?? "", _headerStyle, GUILayout.MinWidth(120));
            GUILayout.EndHorizontal();
        }

        private void RapLookup()
        {
            var id = (_rapIdInput ?? "").Trim();
            if (id.Length == 0) return;
            // Reachable companion = a remote server peer OR a listen-server host, where the companion runs in
            // this process and answers the lookup locally. Only a missing ZNet or a half-open connection
            // (no server peer and we are not the server) is a real "not connected".
            if (ZNet.instance == null || (ServerUid() == 0L && !ZNet.instance.IsServer()))
            { Message(Loc.T("common.not_connected_srv")); return; }
            if (Time.time < _rapNextReq) return;            // throttle-first, so a held button cannot spam
            _rapNextReq = Time.time + 2f;
            _rapPending = true;
            _rapNoData = false;
            SrvRpc("AP_SrvRapSheetReq", id);
        }

        // ---- draw ----

        internal void DrawRapSheetSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _rapHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _rapDataLayout = _rapData;
                _rapNoDataLayout = _rapNoData;
                _rapPendingLayout = _rapPending;
                // Read the roster DrawWindow pinned this frame - never rebuild it here.
                _rapSoloLayout = _othersSnapshot == null || _othersSnapshot.Count == 0;
            }

            BeginCard(Loc.T("rap.section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("rap.id_label"), _labelStyle, GUILayout.MinWidth(70));
            _rapIdInput = GUILayout.TextField(_rapIdInput ?? "", _textFieldStyle, GUILayout.MinWidth(220));
            if (GUILayout.Button(Loc.T("rap.lookup"), _buttonStyle, GUILayout.MinWidth(100))) RapLookup();
            GUILayout.EndHorizontal();
            // One label either way - only the KEY swaps, so the control count never moves between passes.
            GUILayout.Label(Loc.T(_rapHostLayout ? "rap.host_local_hint" : "rap.id_hint"), _hintStyle);

            // No host special-case below: the in-process companion answers AP_SrvRapSheetReq itself, so a
            // host walks the same idle / pending / no-data / record ladder as a remote client.
            var d = _rapDataLayout;
            if (_rapNoDataLayout)
            {
                GUILayout.Label(Loc.T("rap.no_data"), _hintStyle);
            }
            else if (d == null)
            {
                // Idle is not a failure - the lookup simply has not been asked for anything yet. Alone on the
                // server the only id at hand is the admin's own, so say what the lookup actually covers.
                // Still one label: only the key swaps.
                var idleKey = _rapPendingLayout ? "rap.pending"
                            : _rapSoloLayout ? "rap.idle_solo"
                            : "rap.idle";
                GUILayout.Label(Loc.T(idleKey), _hintStyle);
            }
            else
            {
                DrawSection(d.LastName.Length > 0 ? d.LastName : d.Id);
                RapRow(Loc.T("rap.id_col"), d.Id);
                RapRow(Loc.T("rap.role"), d.Role.Length > 0 ? d.Role : Loc.T("rap.role_none"));
                RapRow(Loc.T("rap.first_seen"), RapWhen(d.FirstSeenTicks));
                RapRow(Loc.T("rap.last_seen"), RapWhen(d.LastSeenTicks));
                RapRow(Loc.T("rap.sessions"), d.Sessions.ToString(CultureInfo.InvariantCulture));
                RapRow(Loc.T("rap.playtime"),
                    Loc.T("rap.hours", (d.PlaySeconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture)));
                RapRow(Loc.T("rap.warnings"), d.Warnings.ToString(CultureInfo.InvariantCulture));
                RapRow(Loc.T("rap.warn_last"), d.WarnLastReason.Length > 0 ? d.WarnLastReason : Loc.T("rap.warn_none"));

                // Badges: both labels are always emitted (text swaps, control count does not).
                GUILayout.BeginHorizontal();
                GUILayout.Label(d.Watchlisted ? Loc.T("rap.badge_watch") : Loc.T("rap.badge_watch_no"),
                    d.Watchlisted ? _headerStyle : _dimLabelStyle, GUILayout.MinWidth(160));
                GUILayout.Label(d.TempBanned ? Loc.T("rap.badge_tempban") : Loc.T("rap.badge_tempban_no"),
                    d.TempBanned ? _headerStyle : _dimLabelStyle, GUILayout.MinWidth(160));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                DrawSection(Loc.T("rap.chat_title"));
                if (d.Chat.Count == 0)
                {
                    GUILayout.Label(Loc.T("rap.chat_none"), _hintStyle);
                }
                else
                {
                    _rapChatScroll = GUILayout.BeginScrollView(_rapChatScroll,
                        GUILayout.Height(Mathf.Min(ListView(430f), d.Chat.Count * 26f + 16f)));
                    for (var i = 0; i < d.Chat.Count; i++)
                    {
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.Label(d.Chat[i] ?? "", _dimCellStyle, GUILayout.MinWidth(200));
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }

            EndCard();
        }
    }
}
