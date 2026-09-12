using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 2 - Diagnostics section (Extras tab) ====================
    // Three read-only views onto the server companion: live performance (FPS / frame-time percentiles /
    // a sparkline of recent frame times), a tail of the server's OWN log file, and a server self-test.
    // Nothing in this section changes server state - every button either asks for data or re-asks for it.
    //
    // Member prefix: "Diag". Locale prefix: "diag.".
    //
    // IMGUI law observed throughout: the three reply handlers write _diagPerf/_diagLogLines/_diagChecks at
    // ANY time (they run in ZNet.Update), and the sub-view chips flip _diagView during the event pass. The
    // draw code therefore reads ONLY the *Layout snapshots pinned at the top of DrawDiagnosticsSection.
    // Text fields bind live on purpose - a TextField is one control regardless of its contents.
    //
    // The sparkline is the one piece of custom drawing here. It follows BeginCard's Repaint idiom exactly:
    // GUILayoutUtility.GetRect ONCE per frame (so Layout and Repaint agree on the control count no matter
    // how many samples arrived) and GUI.DrawTexture only on the Repaint pass. Emitting one control per
    // sample would be the classic "control N in a group with only M controls" crash.
    public partial class AdminPanelPlugin
    {
        // ---- thresholds (documented once; used by both the status words and the sparkline colours) ----
        // A dedicated Valheim server is not a renderer - it ticks the simulation. Below ~25 tps players start
        // to feel rubber-banding, below ~15 tps the world is visibly stuttering, so those are the two knees.
        private const float DiagFpsGood = 25f;
        private const float DiagFpsWarn = 15f;
        private const float DiagFrameWarnMs = 40f;   // 25 tps
        private const float DiagFrameBadMs = 66f;    // 15 tps

        // ---- config ----
        private ConfigEntry<bool> _diagSectionCfg;
        private ConfigEntry<int> _diagPollSecondsCfg;
        private bool _diagInited;

        // ---- sub-view (0 = performance, 1 = server log, 2 = self-test) ----
        private int _diagView;
        private int _diagViewLayout;

        // ---- server truth: performance ----
        private sealed class DiagPerfData
        {
            public float FpsAvg, Ms50, Ms95, Ms99, SendQueue;
            public int SampleCount, ZdoCount, PeerCount;
            public float[] Samples = new float[0];   // oldest first
            public long UptimeSeconds, GcBytes;
            public float ReceivedAt;                 // Time.time when the reply landed
        }

        private DiagPerfData _diagPerf;        // live - OnDiagPerfData writes this
        private DiagPerfData _diagPerfLayout;  // per-frame snapshot - the ONLY thing draw code reads
        private float _diagNextPerfReq;
        private Vector2 _diagPerfScroll;

        // ---- server truth: log tail ----
        private List<string> _diagLogLines;    // newest LAST, exactly as the server shipped them
        private int _diagLogTotal;
        private List<string> _diagLogLinesLayout;
        private int _diagLogTotalLayout;
        private bool _diagLogAsked;            // false = never pressed Fetch, so "no data" is expected
        private bool _diagLogAskedLayout;
        private string _diagLogMaxText = "120";
        private string _diagLogFilter = "";
        private Vector2 _diagLogScroll;

        // ---- server truth: self-test ----
        private struct DiagCheck { public string Name; public int Status; public string Detail; }

        private List<DiagCheck> _diagChecks;
        private List<DiagCheck> _diagChecksLayout;
        private bool _diagTestAsked;
        private bool _diagTestAskedLayout;
        private Vector2 _diagTestScroll;

        // ---- shared snapshots ----
        private bool _diagConnectedLayout;
        private bool _diagHostLayout;

        // 1x1 white pixel, tinted per draw call. HideAndDontSave or the logout Resources.UnloadUnusedAssets()
        // sweep destroys it (plugin fields are not serialized, so the sweep counts it as unused) and the
        // sparkline silently disappears after a relog.
        private Texture2D _diagPixel;

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies DiagRpcRegistration.
        internal void DiagInit()
        {
            if (_diagInited) return;
            _diagInited = true;
            _diagSectionCfg = Config.Bind("Features", "ShowDiagnosticsSection", true,
                "Show the Diagnostics section in the Extras tab (server performance, server log tail, server self-test). Read-only: it asks the server companion for numbers and renders them.");
            _diagPollSecondsCfg = Config.Bind("Features", "DiagPerfPollSeconds", 5,
                new ConfigDescription("How often the Performance view asks the server companion for its frame-time stats, in seconds.",
                    new AcceptableValueRange<int>(3, 60)));
            DiagEnsureTex();
        }

        internal bool DiagSectionEnabled() => _diagSectionCfg == null || _diagSectionCfg.Value;

        // Called on logout. EVERY per-world field must be cleared here: server A's frame times, log lines and
        // self-test verdicts rendered on server B would be worse than no data at all, because they look live.
        // The texture is deliberately NOT destroyed - it is a per-process asset with a null canary in the
        // draw path, exactly like the skin textures the main file keeps across sessions.
        internal void DiagReset()
        {
            _diagView = 0;
            _diagViewLayout = 0;
            _diagPerf = null;
            _diagPerfLayout = null;
            _diagNextPerfReq = 0f;
            _diagPerfScroll = Vector2.zero;
            _diagLogLines = null;
            _diagLogTotal = 0;
            _diagLogLinesLayout = null;
            _diagLogTotalLayout = 0;
            _diagLogAsked = false;
            _diagLogAskedLayout = false;
            _diagLogMaxText = "120";
            _diagLogFilter = "";
            _diagLogScroll = Vector2.zero;
            _diagChecks = null;
            _diagChecksLayout = null;
            _diagTestAsked = false;
            _diagTestAskedLayout = false;
            _diagTestScroll = Vector2.zero;
            _diagConnectedLayout = false;
            _diagHostLayout = false;
        }

        // ==================== reply plumbing ====================

        // Own registration class so this file needs no edit to RpcRegistration in the main file. Registration
        // rides ZNet.Awake because client RPCs bind on world join, not on plugin load.
        [HarmonyPatch]
        internal static class DiagRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Each network registration is paired with the in-process bridge registration for the SAME
                // parser, right here, so the two paths can never drift apart. On a listen-server host the
                // companion hands the payload straight to the parser (no packet, nothing to spoof); on a
                // client the gated handler above it is the only way in.
                ZRoutedRpc.instance.Register<ZPackage>("AP_PerfData", OnDiagPerfData);
                AdminPanelLocalBridge.Register("AP_PerfData", ParseDiagPerfData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_LogTail", OnDiagLogTail);
                AdminPanelLocalBridge.Register("AP_LogTail", ParseDiagLogTail);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SelfTest", OnDiagSelfTest);
                AdminPanelLocalBridge.Register("AP_SelfTest", ParseDiagSelfTest);
            }
        }

        // AP_PerfData (v1): int ver, float fpsAvg, float ms50, float ms95, float ms99, int sampleCount,
        //                   int shipped(<=60) x float msSample, long uptimeSeconds, int zdoCount,
        //                   int peerCount, long gcTotalBytes, float sendQueue
        // Server-authoritative, so it must actually come from the server: without the gate a hostile client
        // could InvokeRoutedRPC(adminUid, "AP_PerfData", forged) and paint a fake "server is dying" picture.
        // Unknown version or an out-of-range count discards the WHOLE reply (keep whatever we had).
        private static void OnDiagPerfData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseDiagPerfData(self, pkg);
        }

        internal static void ParseDiagPerfData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new DiagPerfData
                {
                    FpsAvg = DiagSane(pkg.ReadSingle()),
                    Ms50 = DiagSane(pkg.ReadSingle()),
                    Ms95 = DiagSane(pkg.ReadSingle()),
                    Ms99 = DiagSane(pkg.ReadSingle())
                };
                d.SampleCount = Mathf.Max(0, pkg.ReadInt());

                var shipped = pkg.ReadInt();
                if (shipped < 0 || shipped > 60) return;
                var samples = new float[shipped];
                for (var i = 0; i < shipped; i++) samples[i] = DiagSane(pkg.ReadSingle());
                d.Samples = samples;

                d.UptimeSeconds = pkg.ReadLong();
                d.ZdoCount = pkg.ReadInt();
                d.PeerCount = pkg.ReadInt();
                d.GcBytes = pkg.ReadLong();
                d.SendQueue = DiagSane(pkg.ReadSingle());
                d.ReceivedAt = Time.time;
                self._diagPerf = d;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // AP_LogTail (v1): int ver, int total, int shipped(<=120) x string line
        private static void OnDiagLogTail(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseDiagLogTail(self, pkg);
        }

        internal static void ParseDiagLogTail(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var total = pkg.ReadInt();
                var shipped = pkg.ReadInt();
                if (shipped < 0 || shipped > 120) return;
                var lines = new List<string>(shipped);
                for (var i = 0; i < shipped; i++) lines.Add(pkg.ReadString() ?? "");
                self._diagLogTotal = Mathf.Max(total, shipped);
                self._diagLogLines = lines;
            }
            catch (Exception) { }
        }

        // AP_SelfTest (v1): int ver, int shipped(<=30) x (string checkName, int status, string detail)
        private static void OnDiagSelfTest(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseDiagSelfTest(self, pkg);
        }

        internal static void ParseDiagSelfTest(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var shipped = pkg.ReadInt();
                if (shipped < 0 || shipped > 30) return;
                var list = new List<DiagCheck>(shipped);
                for (var i = 0; i < shipped; i++)
                {
                    var name = pkg.ReadString() ?? "";
                    var status = pkg.ReadInt();
                    var detail = pkg.ReadString() ?? "";
                    // An unknown status from a newer companion reads as a warning rather than being dropped.
                    if (status < 0 || status > 2) status = 1;
                    list.Add(new DiagCheck { Name = name, Status = status, Detail = detail });
                }
                self._diagChecks = list;
            }
            catch (Exception) { }
        }

        // NaN/Infinity would propagate into Mathf.Clamp and produce NaN-sized sparkline rects (and "NaN ms"
        // text). Neutralize at the parse boundary so nothing downstream has to think about it.
        private static float DiagSane(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) return 0f;
            return v > 100000f ? 100000f : v;
        }

        // ==================== polling ====================

        // Copies RequestServerTruth exactly: Layout pass first (the caller guarantees it), in-world second,
        // throttle-FIRST third so a companion with no AP_SrvPerfReq handler can never cause a loop.
        // On a listen-server host the companion is in this process: the request is handled locally and the
        // reply carries our own session id, which SenderIsServerReply accepts. So the only thing worth
        // checking is that there is someone to ask - a remote client needs a server peer, a host is one.
        private void DiagPollPerf()
        {
            if (_diagViewLayout != 0) return;
            if (ZNet.instance == null) return;
            if (!_diagHostLayout && ServerUid() == 0L) return;
            if (Time.time < _diagNextPerfReq) return;
            var every = _diagPollSecondsCfg != null ? Mathf.Clamp(_diagPollSecondsCfg.Value, 3, 60) : 5;
            _diagNextPerfReq = Time.time + every;   // set BEFORE sending
            SrvRpc("AP_SrvPerfReq");
        }

        // ==================== drawing ====================

        internal void DrawDiagnosticsSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _diagViewLayout = _diagView;
                _diagConnectedLayout = ZNet.instance != null;
                _diagHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _diagPerfLayout = _diagPerf;
                _diagLogLinesLayout = _diagLogLines;
                _diagLogTotalLayout = _diagLogTotal;
                _diagLogAskedLayout = _diagLogAsked;
                _diagChecksLayout = _diagChecks;
                _diagTestAskedLayout = _diagTestAsked;
                DiagPollPerf();
            }

            if (!_diagConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            // Sub-view chips. The chips WRITE the live field; everything below gates on the snapshot taken
            // above, so a click can never change this frame's control count.
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_diagView == 0, Loc.T("diag.view_perf"), _chipStyleOrButton(), GUILayout.MinWidth(120))
                && _diagView != 0) { _diagView = 0; }
            if (GUILayout.Toggle(_diagView == 1, Loc.T("diag.view_log"), _chipStyleOrButton(), GUILayout.MinWidth(120))
                && _diagView != 1) { _diagView = 1; _diagLogScroll = Vector2.zero; }
            if (GUILayout.Toggle(_diagView == 2, Loc.T("diag.view_selftest"), _chipStyleOrButton(), GUILayout.MinWidth(120))
                && _diagView != 2) { _diagView = 2; _diagTestScroll = Vector2.zero; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            if (_diagViewLayout == 1) DiagDrawLogView();
            else if (_diagViewLayout == 2) DiagDrawSelfTestView();
            else DiagDrawPerfView();
        }

        // ---- view 1: performance ----

        private void DiagDrawPerfView()
        {
            var p = _diagPerfLayout;
            // Two stacked cards can exceed a short window, and neither of them owns an inner list, so one
            // outer scroll is the right shape here (the log/self-test views scroll their lists instead).
            _diagPerfScroll = GUILayout.BeginScrollView(_diagPerfScroll, GUILayout.Height(ListView(160f)));
            BeginCard(Loc.T("diag.perf_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("diag.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _diagNextPerfReq = 0f;   // force the next Layout pass to send; never send from the event pass
            GUILayout.Space(8);
            // Before the first sample the honest line points at the button next to it: the poller will fill
            // this in within one interval, and Refresh asks immediately. Never "no answer from the companion".
            GUILayout.Label(p == null
                    ? Loc.T("diag.perf_waiting")
                    : Loc.T("diag.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - p.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Headline: server FPS with a colour-coded status word next to it.
            var fps = p != null ? p.FpsAvg : 0f;
            var fpsStatus = p == null ? -1 : DiagFpsStatus(fps);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("diag.fps"), _labelStyle, GUILayout.MinWidth(140));
            DiagColoredLabel(p == null ? "-" : DiagNum(fps, 1), fpsStatus, _headerStyle, GUILayout.MinWidth(70));
            DiagColoredLabel(DiagStatusWord(fpsStatus), fpsStatus, _labelStyle, GUILayout.MinWidth(70));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Frame-time percentiles on one row: p50 is the typical tick, p99 is what a player actually
            // notices as a freeze, so all three are colour-coded independently.
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("diag.frametime"), _labelStyle, GUILayout.MinWidth(140));
            DiagPercentile("diag.p50", p != null ? p.Ms50 : 0f, p != null);
            DiagPercentile("diag.p95", p != null ? p.Ms95 : 0f, p != null);
            DiagPercentile("diag.p99", p != null ? p.Ms99 : 0f, p != null);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Sparkline. GetRect is called unconditionally, exactly once per pass, so the control count is
            // identical whether 0 or 60 samples arrived; only the Repaint-time texture blits vary.
            var rect = GUILayoutUtility.GetRect(1f, 48f, GUILayout.ExpandWidth(true));
            DiagDrawSparkline(rect, p != null ? p.Samples : null);
            GUILayout.Label(p == null
                    ? Loc.T("diag.spark_hint")
                    : Loc.T("diag.spark_hint_n", p.Samples.Length, p.SampleCount),
                _hintStyle);

            EndCard();

            BeginCard(Loc.T("diag.health_section"));
            DiagStatRow(Loc.T("diag.uptime"), p == null ? "-" : DiagUptime(p.UptimeSeconds), -1);
            DiagStatRow(Loc.T("diag.zdos"), p == null ? "-" : p.ZdoCount.ToString(CultureInfo.InvariantCulture), -1);
            DiagStatRow(Loc.T("diag.peers"), p == null ? "-" : p.PeerCount.ToString(CultureInfo.InvariantCulture), -1);
            DiagStatRow(Loc.T("diag.gc"), p == null ? "-" : Loc.T("diag.mb_val", DiagNum(p.GcBytes / 1048576f, 1)), -1);
            DiagStatRow(Loc.T("diag.sendqueue"), p == null ? "-" : DiagNum(p.SendQueue, 1), -1);
            // One label, text swapped on the Layout snapshot - never a conditional widget.
            GUILayout.Label(_diagHostLayout
                    ? Loc.T("diag.host_note", _diagPollSecondsCfg != null ? _diagPollSecondsCfg.Value : 5)
                    : Loc.T("diag.perf_hint", _diagPollSecondsCfg != null ? _diagPollSecondsCfg.Value : 5),
                _hintStyle);
            EndCard();
            GUILayout.EndScrollView();
        }

        // One "p95  41.2 ms" pair. Always two controls, so it is safe to call from a fixed row.
        private void DiagPercentile(string labelKey, float ms, bool have)
        {
            var status = have ? DiagFrameStatus(ms) : -1;
            GUILayout.Label(Loc.T(labelKey), _dimLabelStyle, GUILayout.MinWidth(36));
            DiagColoredLabel(have ? Loc.T("diag.ms_val", DiagNum(ms, 1)) : "-", status, _labelStyle, GUILayout.MinWidth(80));
        }

        private void DiagStatRow(string label, string value, int status)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.MinWidth(140));
            DiagColoredLabel(value, status, _labelStyle, GUILayout.MinWidth(120));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // ---- view 2: server log ----

        private void DiagDrawLogView()
        {
            BeginCard(Loc.T("diag.log_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("diag.log_max"), _labelStyle, GUILayout.MinWidth(60));
            _diagLogMaxText = GUILayout.TextField(_diagLogMaxText ?? "", _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("diag.log_filter"), _labelStyle, GUILayout.MinWidth(60));
            _diagLogFilter = GUILayout.TextField(_diagLogFilter ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("diag.fetch"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                var max = DiagClampLines(_diagLogMaxText);
                _diagLogMaxText = max.ToString(CultureInfo.InvariantCulture);   // show the value we actually sent
                var pkg = new ZPackage();
                pkg.Write(max);
                pkg.Write((_diagLogFilter ?? "").Trim());
                SrvRpc("AP_SrvLogTailReq", pkg);
                _diagLogAsked = true;
                Message(Loc.T("diag.msg_log_req", max));
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("diag.log_hint"), _hintStyle);
            GUILayout.Label(Loc.T("diag.log_max_hint"), _hintStyle);

            // No host branch: on a listen-server host the companion tails this process's own log file and the
            // reply lands here like any other. Every branch below is decided from Layout snapshots.
            var lines = _diagLogLinesLayout;
            if (lines == null)
            {
                GUILayout.Label(Loc.T(_diagLogAskedLayout ? "diag.log_waiting" : "diag.log_pending"), _hintStyle);
            }
            else if (lines.Count == 0)
            {
                GUILayout.Label(Loc.T("diag.log_empty"), _hintStyle);
            }
            else
            {
                // Newest last, exactly as tailed. Cell style clips instead of wrapping, and the labels size
                // to their text, so a long line scrolls horizontally rather than reflowing the whole list.
                _diagLogScroll = GUILayout.BeginScrollView(_diagLogScroll, GUILayout.Height(ListView(330f)));
                for (var i = 0; i < lines.Count; i++)
                    GUILayout.Label(lines[i] ?? "", _dimCellStyle);
                GUILayout.EndScrollView();
                GUILayout.Label(Loc.T("diag.log_count", lines.Count, Mathf.Max(_diagLogTotalLayout, lines.Count)),
                    _dimLabelStyle);
            }

            EndCard();
        }

        // ---- view 3: self-test ----

        private void DiagDrawSelfTestView()
        {
            var checks = _diagChecksLayout;
            BeginCard(Loc.T("diag.st_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("diag.st_run"), _buttonStyle, GUILayout.MinWidth(120)))
            {
                SrvRpc("AP_SrvSelfTestReq");
                _diagTestAsked = true;
                Message(Loc.T("diag.msg_st_run"));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("diag.st_hint"), _hintStyle);

            // No host branch: the self-test runs wherever the companion runs, which on a host is this process.
            if (checks == null)
            {
                GUILayout.Label(Loc.T(_diagTestAskedLayout ? "diag.st_waiting" : "diag.st_pending"), _hintStyle);
            }
            else if (checks.Count == 0)
            {
                GUILayout.Label(Loc.T("diag.st_empty"), _hintStyle);
            }
            else
            {
                var warn = 0;
                var fail = 0;
                for (var i = 0; i < checks.Count; i++)
                {
                    if (checks[i].Status == 1) warn++;
                    else if (checks[i].Status == 2) fail++;
                }

                _diagTestScroll = GUILayout.BeginScrollView(_diagTestScroll, GUILayout.Height(ListView(360f)));
                for (var i = 0; i < checks.Count; i++)
                {
                    var c = checks[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    // Plain ASCII status words, never symbols: the panel font is chosen for Latin text and a
                    // missing glyph would drop the WHOLE panel onto the fallback font.
                    DiagColoredLabel(DiagCheckWord(c.Status), c.Status, _cellStyle, GUILayout.Width(60));
                    GUILayout.Label(c.Name ?? "", _cellStyle, GUILayout.Width(180));
                    GUILayout.Label(c.Detail ?? "", _dimCellStyle, GUILayout.MinWidth(160));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();

                var summaryStatus = fail > 0 ? 2 : (warn > 0 ? 1 : 0);
                DiagColoredLabel(fail == 0 && warn == 0
                        ? Loc.T("diag.st_all_good", checks.Count)
                        : Loc.T("diag.st_summary", checks.Count, warn, fail),
                    summaryStatus, _labelStyle);
            }

            EndCard();
        }

        // ==================== sparkline ====================

        // Solid 1x1 white pixel, tinted through GUI.color. Created in DiagInit and re-created here through
        // the fake-null canary: the logout asset sweep destroys plugin textures, and a destroyed Texture2D
        // compares == null in Unity, so this single check covers both "never made" and "was swept".
        private void DiagEnsureTex()
        {
            if (_diagPixel == null) _diagPixel = SolidTex(Color.white);
        }

        // Repaint-only custom drawing on a rect the layout system already reserved (BeginCard's idiom).
        // Not one control per sample: one GetRect for the whole chart, N texture blits inside Repaint.
        private void DiagDrawSparkline(Rect r, float[] samples)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            DiagEnsureTex();
            var tex = _diagPixel;
            if (tex == null || r.width < 4f || r.height < 4f) return;

            var prev = GUI.color;
            try
            {
                GUI.color = new Color(0f, 0f, 0f, 0.35f);
                GUI.DrawTexture(r, tex);
                if (samples == null || samples.Length == 0) return;

                // Scale to the worst sample but never below the warn knee, so an idle server does not paint
                // a wall of full-height bars over a 2 ms baseline.
                var max = DiagFrameWarnMs;
                for (var i = 0; i < samples.Length; i++) if (samples[i] > max) max = samples[i];
                if (max <= 0.01f) return;

                var n = samples.Length;
                var bw = r.width / n;
                for (var i = 0; i < n; i++)
                {
                    var v = samples[i];
                    var h = Mathf.Clamp(r.height * (v / max), 1f, r.height);
                    var c = DiagStatusColor(DiagFrameStatus(v));
                    c.a = 0.9f;
                    GUI.color = c;
                    GUI.DrawTexture(new Rect(r.x + i * bw, r.yMax - h, Mathf.Max(1f, bw - 1f), h), tex);
                }

                // Reference line at the warn knee, so the eye can judge the bars without reading numbers.
                var y = r.yMax - r.height * (DiagFrameWarnMs / max);
                GUI.color = new Color(1f, 1f, 1f, 0.18f);
                GUI.DrawTexture(new Rect(r.x, y, r.width, 1f), tex);
            }
            finally { GUI.color = prev; }   // restored on EVERY path, including the early returns above
        }

        // ==================== small helpers ====================

        // status: 0 ok, 1 warn, 2 fail, anything else = leave the inherited colour alone. Always exactly one
        // control, and GUI.contentColor is restored on every path (an exception mid-label would otherwise
        // tint the rest of the panel green).
        private void DiagColoredLabel(string text, int status, GUIStyle style, params GUILayoutOption[] opts)
        {
            var prev = GUI.contentColor;
            try
            {
                if (status >= 0 && status <= 2) GUI.contentColor = DiagStatusColor(status);
                GUILayout.Label(text ?? "", style ?? _labelStyle, opts);
            }
            finally { GUI.contentColor = prev; }
        }

        private static Color DiagStatusColor(int status)
        {
            if (status == 2) return new Color(0.95f, 0.45f, 0.40f);   // fail
            if (status == 1) return new Color(0.95f, 0.80f, 0.40f);   // warn
            return new Color(0.55f, 0.85f, 0.55f);                    // ok
        }

        private static int DiagFpsStatus(float fps) => fps >= DiagFpsGood ? 0 : (fps >= DiagFpsWarn ? 1 : 2);

        private static int DiagFrameStatus(float ms) => ms <= DiagFrameWarnMs ? 0 : (ms <= DiagFrameBadMs ? 1 : 2);

        private static string DiagStatusWord(int status)
        {
            if (status == 2) return Loc.T("diag.status_bad");
            if (status == 1) return Loc.T("diag.status_warn");
            if (status == 0) return Loc.T("diag.status_good");
            return "";
        }

        private static string DiagCheckWord(int status)
        {
            if (status == 2) return Loc.T("diag.st_fail");
            if (status == 1) return Loc.T("diag.st_warn");
            return Loc.T("diag.st_ok");
        }

        // Numbers are rendered InvariantCulture on purpose: the panel's own decimal separator must not change
        // with the game language while the surrounding words do (and it keeps every value ASCII).
        private static string DiagNum(float v, int digits) =>
            v.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        private static string DiagUptime(long seconds)
        {
            if (seconds < 0L) seconds = 0L;
            var d = seconds / 86400L;
            var h = seconds % 86400L / 3600L;
            var m = seconds % 3600L / 60L;
            return Loc.T("diag.uptime_val", d, h, m);
        }

        // The wire contract caps a tail at 120 lines; anything below 20 is not worth a round trip.
        private static int DiagClampLines(string s)
        {
            int n;
            if (!int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return 120;
            return Mathf.Clamp(n, 20, 120);
        }
    }
}
