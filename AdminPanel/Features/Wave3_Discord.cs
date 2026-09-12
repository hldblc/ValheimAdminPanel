using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 3 - Discord section (Extras tab) ====================
    // Client half of the Discord bridge. This is a STATUS / DIAGNOSTICS surface, not a config editor:
    // everything that decides whether the bridge works (webhook URL, feed/alert/link switches) lives in the
    // server companion's BepInEx config and is deliberately not settable from here - a panel that could
    // rewrite the webhook URL would let any admin redirect the server's outbound traffic to a URL of their
    // choosing. So the section shows what the server reports and names the config keys to change.
    //
    // Tier: TIER-VANILLA on the client side. The panel neither reads nor writes character data here; it asks
    // the server companion for counters and asks it to post one test embed. Nothing requires the companion
    // DLL on any other player's client.
    //
    // Member prefix: "Disc". Locale prefix: "disc.".
    //
    // IMGUI law observed throughout: OnDiscordStateData writes _discState at ANY time (it runs in
    // ZNet.Update); the draw code reads ONLY _discStateLayout, pinned on the Layout pass. Every card emits a
    // FIXED number of controls - the "feed is off" hint and the "last error" line are label-text swaps, never
    // conditionally rendered widgets - so Repaint can never disagree with what Layout reserved.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _discSectionCfg;
        private ConfigEntry<int> _discPollSecondsCfg;
        private bool _discInited;

        // ---- server truth (written by OnDiscordStateData, read only through _discStateLayout) ----
        private sealed class DiscStateData
        {
            public bool FeedOn, AlertsOn, LinkOn;
            public int Queued, SentTotal, FailedTotal, LinkedCount;
            public string LastError = "";
            public long LastSentTicksUtc;
            public float ReceivedAt;   // Time.time when the reply landed, for the "updated Ns ago" line
        }

        private DiscStateData _discState;         // live - RPC handler writes this
        private DiscStateData _discStateLayout;   // per-frame snapshot - the ONLY thing draw code reads
        private bool _discConnectedLayout;
        private bool _discHostLayout;

        private float _discNextStateReq;          // request throttle (Time.time based)
        private Vector2 _discScroll;

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies DiscRpcRegistration.
        internal void DiscInit()
        {
            if (_discInited) return;
            _discInited = true;
            _discSectionCfg = Config.Bind("Features", "ShowDiscordSection", true,
                "Show the Discord section in the Extras tab (bridge status, queue/sent/failed counters, test message). Read-only diagnostics: the webhook URL and the feature switches live in the SERVER companion's config, never here.");
            _discPollSecondsCfg = Config.Bind("Features", "DiscordStatePollSeconds", 20,
                new ConfigDescription("How often the panel asks the server companion for Discord bridge status, in seconds.",
                    new AcceptableValueRange<int>(5, 120)));
        }

        internal bool DiscSectionEnabled() => _discSectionCfg == null || _discSectionCfg.Value;

        // Called on logout. EVERY per-world field must be cleared here: server A's "feed on, 12 queued, last
        // error: 401" rendered against server B looks live and would send an admin chasing a fault that is not
        // theirs. The counters are cheap to re-fetch, so there is never a reason to keep them.
        internal void DiscReset()
        {
            _discState = null;
            _discStateLayout = null;
            _discConnectedLayout = false;
            _discHostLayout = false;
            _discNextStateReq = 0f;
            _discScroll = Vector2.zero;
        }

        // ==================== reply plumbing ====================

        // Own registration class so this file needs no edit to RpcRegistration in the main file. Registration
        // rides ZNet.Awake because client RPCs bind on world join, not on plugin load.
        [HarmonyPatch]
        internal static class DiscRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Both halves side by side: the network handler (gate + parse) and the parse half the
                // in-process bridge calls on a listen-server host, where the companion runs in this very
                // process and there is no server peer for the gate to authenticate against.
                ZRoutedRpc.instance.Register<ZPackage>("AP_DiscordState", OnDiscordStateData);
                AdminPanelLocalBridge.Register("AP_DiscordState", DiscParseState);
            }
        }

        // AP_DiscordState wire format (v1):
        //   int ver=1, bool feedOn, bool alertsOn, bool linkOn, int queued, int sentTotal, int failedTotal,
        //   string lastError, long lastSentTicksUtc, int linkedCount
        // Server-authoritative payload, so it must actually come from the server: without the gate a hostile
        // client could InvokeRoutedRPC(adminUid, "AP_DiscordState", forged) and paint a fake "webhook broken,
        // 900 failed" picture - or a fake "all green" one that hides a real outage. Unknown version discards
        // the WHOLE reply (keep whatever we had).
        private static void OnDiscordStateData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            DiscParseState(self, pkg);
        }

        // Parse half, shared verbatim with the in-process bridge so the local and network paths can never
        // interpret the same payload differently.
        internal static void DiscParseState(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var st = new DiscStateData
                {
                    FeedOn = pkg.ReadBool(),
                    AlertsOn = pkg.ReadBool(),
                    LinkOn = pkg.ReadBool(),
                    // Counters are display-only, so a nonsense value cannot break anything - but clamp at 0 so
                    // a truncated/garbled read can never render "-1 queued" and read as a mod bug.
                    Queued = Mathf.Max(0, pkg.ReadInt()),
                    SentTotal = Mathf.Max(0, pkg.ReadInt()),
                    FailedTotal = Mathf.Max(0, pkg.ReadInt())
                };
                // The error text is the server's own words (an HTTP status line, a DNS failure). Cap it: a
                // multi-kilobyte string in a wrapping label would push every card below it off the screen.
                st.LastError = DiscClip(pkg.ReadString(), 240);
                st.LastSentTicksUtc = pkg.ReadLong();
                st.LinkedCount = Mathf.Max(0, pkg.ReadInt());
                st.ReceivedAt = Time.time;
                self._discState = st;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // Periodic poll. Same three gates as ModRequestState, in the same order and for the same reasons:
        // Layout pass FIRST (this both sends an RPC and mutates the throttle, and must do so once per frame
        // rather than once per pass), someone-to-ask SECOND, throttle-FIRST-then-send THIRD so a companion
        // with no AP_SrvDiscordStateReq handler can never turn this into a per-frame packet loop.
        // On a listen-server host the companion runs in THIS process: the request is handled locally and the
        // reply comes back stamped with our own session id, which SenderIsServerReply accepts. So a host polls
        // like anyone else; only a remote client with no server peer yet has nothing to send to.
        private void DiscRequestState()
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            if (ZNet.instance == null) return;
            if (!_discHostLayout && ServerUid() == 0L) return;
            if (Time.time < _discNextStateReq) return;
            var every = _discPollSecondsCfg != null ? Mathf.Clamp(_discPollSecondsCfg.Value, 5, 120) : 20;
            _discNextStateReq = Time.time + every;
            SrvRpc("AP_SrvDiscordStateReq");
        }

        // ==================== drawing ====================

        internal void DrawDiscordSection()
        {
            if (Event.current.type == EventType.Layout)
            {
                _discStateLayout = _discState;
                _discConnectedLayout = ZNet.instance != null;
                _discHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
            }

            if (!_discConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            DiscRequestState();

            _discScroll = GUILayout.BeginScrollView(_discScroll, GUILayout.Height(ListView(150f)));

            DiscDrawStatusCard();
            DiscDrawTestCard();
            DiscDrawAboutCard();

            GUILayout.EndScrollView();
        }

        private void DiscDrawStatusCard()
        {
            var st = _discStateLayout;
            BeginCard(Loc.T("disc.state_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("disc.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _discNextStateReq = 0f;   // force the NEXT Layout pass to send; never send from the event pass
            GUILayout.Space(8);
            GUILayout.Label(st == null
                    ? Loc.T("disc.no_data")
                    : Loc.T("disc.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - st.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Three switches. Unknown (no reply yet) is its own state on purpose: "off" and "we have not been
            // told yet" lead an admin to completely different actions.
            DiscRow("disc.feed", DiscOnOff(st, 0), DiscSwitchStatus(st, 0));
            DiscRow("disc.alerts", DiscOnOff(st, 1), DiscSwitchStatus(st, 1));
            DiscRow("disc.linking", DiscOnOff(st, 2), DiscSwitchStatus(st, 2));

            // Counters. A non-zero queue is normal for a second or two (the sender batches); a queue that
            // stays high across refreshes is the symptom the warning colour is for.
            DiscRow("disc.queued", st == null ? Loc.T("disc.unknown") : st.Queued.ToString(),
                st == null ? -1 : (st.Queued > 20 ? 1 : 0));
            DiscRow("disc.sent", st == null ? Loc.T("disc.unknown") : st.SentTotal.ToString(), -1);
            DiscRow("disc.failed", st == null ? Loc.T("disc.unknown") : st.FailedTotal.ToString(),
                st == null ? -1 : (st.FailedTotal > 0 ? 1 : 0));
            DiscRow("disc.last_sent", st == null ? Loc.T("disc.unknown") : DiscAgo(st.LastSentTicksUtc), -1);
            DiscRow("disc.linked", st == null ? Loc.T("disc.unknown") : st.LinkedCount.ToString(), -1);

            // Always one control: the text swaps between the error and "none". A conditionally rendered
            // warning line would change the control count the moment a reply landed mid-frame. This one gets
            // a full-width WRAPPING style rather than a row cell - a server's error text can be a whole
            // sentence, and a clipped "HTTP 40..." is worthless to the person trying to fix it.
            var err = st != null && !string.IsNullOrEmpty(st.LastError);
            DiscColoredLabel(Loc.T("disc.last_error_line", err ? st.LastError : Loc.T("disc.no_error")),
                err ? 2 : -1, _hintStyle);

            // The webhook hint. Two fixed labels, text-swapped - never a conditional block.
            // The no-reply text leads with what is actually true for almost everyone who sees it: the bridge
            // ships OFF and needs a webhook set server-side. Naming both config keys turns a dead-looking card
            // into the setup instructions. This is not a multiplayer-only feature - it is just as useful solo.
            DiscColoredLabel(
                st == null ? Loc.T("disc.feed_unset_hint")
                           : Loc.T(st.FeedOn ? "disc.feed_on_hint" : "disc.feed_off_hint"),
                st != null && !st.FeedOn ? 1 : -1,
                _proseStyle);
            GUILayout.Label(Loc.T("disc.no_remote_webhook"), _hintStyle);

            // One label, text swapped on the Layout snapshot - never a conditional widget.
            GUILayout.Label(_discHostLayout
                    ? Loc.T("disc.host_note", _discPollSecondsCfg != null ? _discPollSecondsCfg.Value : 20)
                    : Loc.T("disc.state_hint", _discPollSecondsCfg != null ? _discPollSecondsCfg.Value : 20),
                _hintStyle);
            EndCard();
        }

        private void DiscDrawTestCard()
        {
            BeginCard(Loc.T("disc.test_section"));
            GUILayout.BeginHorizontal();
            // Confirmed because it is outbound traffic with the server's identity on it: a mistyped click
            // posts into whatever channel the webhook belongs to, in front of the whole community.
            if (ConfirmButton("disc:test", Loc.T("disc.test_send"), GUILayout.MinWidth(150)))
            {
                SrvRpc("AP_SrvDiscordTest");
                Message(Loc.T("disc.msg_test"));
                _discNextStateReq = 0f;   // the counters move right after; re-poll on the next frame
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("disc.test_hint"), _hintStyle);
            EndCard();
        }

        private void DiscDrawAboutCard()
        {
            BeginCard(Loc.T("disc.about_section"));
            GUILayout.Label(Loc.T("disc.about_1"), _proseStyle);
            GUILayout.Label(Loc.T("disc.about_2"), _proseStyle);
            GUILayout.Label(Loc.T("disc.about_3"), _proseStyle);
            GUILayout.Label(Loc.T("disc.about_4"), _proseStyle);
            EndCard();
        }

        // ==================== small helpers ====================

        // One status row: fixed label, coloured value, flexible tail. Always exactly the same control count.
        private void DiscRow(string labelKey, string value, int status)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), _labelStyle, GUILayout.MinWidth(140));
            DiscColoredLabel(value, status, _cellStyle, GUILayout.MinWidth(120));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // status: 0 ok, 1 warn, 2 fail, anything else = leave the inherited colour alone. Exactly one control,
        // and GUI.contentColor is restored on EVERY path - an exception mid-label would otherwise tint the
        // rest of the panel red for the remainder of the frame.
        private void DiscColoredLabel(string text, int status, GUIStyle style, params GUILayoutOption[] opts)
        {
            var prev = GUI.contentColor;
            try
            {
                if (status >= 0 && status <= 2) GUI.contentColor = DiscStatusColor(status);
                GUILayout.Label(text ?? "", style ?? _labelStyle, opts);
            }
            finally { GUI.contentColor = prev; }
        }

        private static Color DiscStatusColor(int status)
        {
            if (status == 2) return new Color(0.95f, 0.45f, 0.40f);   // fail
            if (status == 1) return new Color(0.95f, 0.80f, 0.40f);   // warn
            return new Color(0.55f, 0.85f, 0.55f);                    // ok
        }

        // which: 0 feed, 1 alerts, 2 linking
        private static string DiscOnOff(DiscStateData st, int which)
        {
            if (st == null) return Loc.T("disc.unknown");
            var on = which == 0 ? st.FeedOn : (which == 1 ? st.AlertsOn : st.LinkOn);
            return Loc.T(on ? "disc.on" : "disc.off");
        }

        // Feed off is a warning (the bridge is silent); alerts/linking off are legitimate choices, so they
        // stay neutral rather than nagging an owner who deliberately turned them off.
        private static int DiscSwitchStatus(DiscStateData st, int which)
        {
            if (st == null) return -1;
            if (which == 0) return st.FeedOn ? 0 : 1;
            var on = which == 1 ? st.AlertsOn : st.LinkOn;
            return on ? 0 : -1;
        }

        // Relative age of a server-stamped DateTime.UtcNow.Ticks value. Client and server clocks drift, so a
        // slightly-in-the-future stamp is normal and reads as "just now" instead of a negative number.
        private static string DiscAgo(long ticksUtc)
        {
            if (ticksUtc <= 0L) return Loc.T("disc.never");
            var delta = DateTime.UtcNow.Ticks - ticksUtc;
            if (delta < TimeSpan.TicksPerMinute) return Loc.T("disc.just_now");
            var mins = delta / TimeSpan.TicksPerMinute;
            if (mins < 60L) return Loc.T("disc.mins_ago", mins);
            if (mins < 1440L) return Loc.T("disc.hours_ago", mins / 60L);
            return Loc.T("disc.days_ago", mins / 1440L);
        }

        // Bound an untrusted server string for display. Newlines are flattened too: the error goes into a
        // fixed-width row cell, and a multi-line value there would clip to its first line anyway.
        private static string DiscClip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return t.Length <= max ? t : t.Substring(0, max);
        }
    }
}
