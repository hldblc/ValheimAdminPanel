using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 2 - Server Tools section (Extras tab, client side) ====================
    // The owner's toolkit: world census / lag hotspots, world cleanup, backups, the schedule (restart,
    // announcements, MOTD, autosave override) and world modifiers. Everything here is UI + request
    // plumbing; the server companion owns every piece of truth and ships it back in one reply per topic
    // which this file parses defensively and draws from per-frame Layout snapshots.
    //
    // Member prefix: "Tool". Locale prefix: "tool.".
    //
    // IMGUI law observed throughout (see Features\FeaturesCore.cs and the Player-tab chip pattern):
    // reply handlers write the live payload fields at ANY time (they run in ZNet.Update) and the sub-view
    // chips flip _toolView during the event pass, so EVERY control-count decision below - which view is
    // drawn, how many table rows, how many announcement rows, whether the restart card shows a countdown -
    // reads a *Layout snapshot pinned at the top of DrawServerToolsSection. Text fields bind to live edit
    // state on purpose: a TextField is one control no matter what is inside it.
    //
    // HOST MODE: on a listen-server host the companion runs IN THIS PROCESS, so a request routed to target 0
    // is handled locally and the reply comes back stamped with our OWN session id. SenderIsServerReply accepts
    // that case (the companion's RouteRpcSanitizer re-stamps every INCOMING routed packet with the real socket
    // uid, so a remote peer cannot forge it), so every view below fills in on a host exactly like on a client.
    // The pollers therefore gate on "in a world" only, and "tool.host_note" describes the in-process companion
    // instead of the old "this stays empty" limitation.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _toolSectionCfg;
        private bool _toolInited;

        private const int ToolAnnSlots = 20;   // AP_SrvAnnSet slot range is 0..19 (wire contract)

        // ==================== server truth (written by the reply handlers) ====================

        private struct ToolCount { public string Name; public int Count; }
        private struct ToolHotspot { public int X; public int Y; public int Count; public string Top; }
        private struct ToolBackupFile { public string Name; public long Size; public long Ticks; }
        private struct ToolAnn { public string Text; public int Every; public long Next; }
        private struct ToolWorldKey { public string Key; public bool ServerOption; }

        private sealed class ToolCensusData
        {
            public bool Running;
            public int Scanned;
            public int TotalZdos;
            public int DroppedItems;
            public long ScanMillis;
            public readonly List<ToolCount> Rows = new List<ToolCount>();
        }

        private sealed class ToolHotspotData
        {
            public int TotalZones;
            public readonly List<ToolHotspot> Rows = new List<ToolHotspot>();
        }

        private sealed class ToolCleanupData
        {
            public bool DryRun;
            public int Matched;
            public int Removed;
            public readonly List<ToolCount> Rows = new List<ToolCount>();
        }

        private sealed class ToolBackupData
        {
            public bool AutoOn;
            public int IntervalMinutes;
            public string LastError = "";
            public readonly List<ToolBackupFile> Files = new List<ToolBackupFile>();
        }

        private sealed class ToolSchedData
        {
            public bool RestartPending;
            public long RestartAt;
            public string RestartReason = "";
            public string Motd = "";
            public bool AutosaveOn;
            public int AutosaveMinutes;
            public readonly List<ToolAnn> Anns = new List<ToolAnn>();
        }

        private sealed class ToolWorldModData
        {
            public string Preset = "";
            public readonly List<ToolWorldKey> Keys = new List<ToolWorldKey>();
        }

        private ToolCensusData _toolCensus;
        private ToolHotspotData _toolHotspot;
        private ToolCleanupData _toolCleanup;
        private ToolBackupData _toolBackup;
        private ToolSchedData _toolSched;
        private ToolWorldModData _toolWorldMod;

        // ==================== Layout snapshots (the ONLY things draw code may read) ====================

        private int _toolViewLayout;
        private bool _toolConnectedLayout;
        private bool _toolHostLayout;
        // "Hosting with nobody else connected". Pinned from the roster DrawWindow already built this frame -
        // never rebuilt here - and used ONLY to swap the TEXT of an empty-state label, so the control count is
        // identical solo or not. Restarts and timed announcements are dedicated-server features; saying so is
        // honest, whereas the generic "no answer" line made them read as broken.
        private bool _toolSoloLayout;
        private ToolCensusData _toolCensusLayout;
        private ToolHotspotData _toolHotspotLayout;
        private ToolCleanupData _toolCleanupLayout;
        private ToolBackupData _toolBackupLayout;
        private ToolSchedData _toolSchedLayout;
        private ToolWorldModData _toolWorldModLayout;
        private bool _toolRunEnabledLayout;                     // cleanup: is a matching preview on file?
        private List<ToolWorldKey> _toolWorldRowsLayout;        // union of every key seen this session
        private HashSet<string> _toolWorldOnLayout;             // which of those are currently on

        // ==================== UI / edit state ====================

        private int _toolView;                                  // 0 census 1 cleanup 2 backups 3 schedule 4 modifiers
        private Vector2 _toolScroll;

        private float _toolNextCensusReq, _toolNextBackupReq, _toolNextSchedReq, _toolNextWorldModReq;

        private string _toolTopN = "25";

        private int _toolCleanMode;                             // 0 dropped items, 1 orphan ZDOs, 2 both
        private string _toolCleanMinutes = "60";
        private string _toolCleanPendingSig;                    // signature of the settings a preview was sent for
        private string _toolCleanPreviewSig;                    // signature the last dry-run RESULT belongs to

        private string _toolRestartMinutes = "10";
        private string _toolRestartReason = "";

        private string[] _toolAnnText = new string[ToolAnnSlots];
        private string[] _toolAnnEvery = new string[ToolAnnSlots];
        private string _toolAnnNewText = "";
        private string _toolAnnNewEvery = "60";
        private int _toolAnnSeededRows = -1;                    // row count the edit fields were seeded from

        private string _toolMotd = "";
        private bool _toolMotdSeeded;
        private bool _toolAutosaveOn;
        private string _toolAutosaveMin = "20";
        private bool _toolAutosaveSeeded;

        // World modifiers: the server reports the keys that are in effect. Turning one off would drop it out
        // of the next reply and make it unreachable, so the section remembers every key it has ever seen this
        // session (the row list) and tracks on/off separately, with an optimistic override until the next
        // reply lands so a toggle does not visually snap back for a poll interval.
        private readonly List<ToolWorldKey> _toolWorldKnown = new List<ToolWorldKey>();
        private readonly Dictionary<string, bool> _toolWorldOverride = new Dictionary<string, bool>();

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies ToolRpcRegistration.
        internal void ToolInit()
        {
            if (_toolInited) return;
            _toolInited = true;
            _toolSectionCfg = Config.Bind("Features", "ShowServerToolsSection", true,
                "Show the Server Tools section in the Extras tab (world census, cleanup, backups, restart/announcement schedule, world modifiers). Client-side UI only - nothing here runs on its own.");
            for (var i = 0; i < ToolAnnSlots; i++) { _toolAnnText[i] = ""; _toolAnnEvery[i] = ""; }
        }

        internal bool ToolSectionEnabled() => _toolSectionCfg == null || _toolSectionCfg.Value;

        // Called on logout. EVERY per-world field must be cleared or server A's backup list, schedule and
        // world modifiers render - with live Stage-restore and Toggle controls - against server B.
        internal void ToolReset()
        {
            _toolCensus = null;
            _toolHotspot = null;
            _toolCleanup = null;
            _toolBackup = null;
            _toolSched = null;
            _toolWorldMod = null;

            _toolViewLayout = 0;
            _toolConnectedLayout = false;
            _toolHostLayout = false;
            _toolSoloLayout = false;
            _toolCensusLayout = null;
            _toolHotspotLayout = null;
            _toolCleanupLayout = null;
            _toolBackupLayout = null;
            _toolSchedLayout = null;
            _toolWorldModLayout = null;
            _toolRunEnabledLayout = false;
            _toolWorldRowsLayout = null;
            _toolWorldOnLayout = null;

            _toolView = 0;
            _toolScroll = Vector2.zero;
            _toolNextCensusReq = 0f;
            _toolNextBackupReq = 0f;
            _toolNextSchedReq = 0f;
            _toolNextWorldModReq = 0f;

            _toolTopN = "25";
            _toolCleanMode = 0;
            _toolCleanMinutes = "60";
            _toolCleanPendingSig = null;
            _toolCleanPreviewSig = null;

            _toolRestartMinutes = "10";
            _toolRestartReason = "";

            _toolAnnText = new string[ToolAnnSlots];
            _toolAnnEvery = new string[ToolAnnSlots];
            for (var i = 0; i < ToolAnnSlots; i++) { _toolAnnText[i] = ""; _toolAnnEvery[i] = ""; }
            _toolAnnNewText = "";
            _toolAnnNewEvery = "60";
            _toolAnnSeededRows = -1;

            _toolMotd = "";
            _toolMotdSeeded = false;
            _toolAutosaveOn = false;
            _toolAutosaveMin = "20";
            _toolAutosaveSeeded = false;

            _toolWorldKnown.Clear();
            _toolWorldOverride.Clear();
        }

        // ==================== reply plumbing ====================

        // Own registration class so no existing file needs an edit. Bound on ZNet.Awake (once per world
        // join) exactly like the main file's RpcRegistration; the glue applies it with CreateAndPatchAll.
        [HarmonyPatch]
        internal static class ToolRpcRegistration
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
                ZRoutedRpc.instance.Register<ZPackage>("AP_CensusData", ToolOnCensusData);
                AdminPanelLocalBridge.Register("AP_CensusData", ToolParseCensusData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_HotspotData", ToolOnHotspotData);
                AdminPanelLocalBridge.Register("AP_HotspotData", ToolParseHotspotData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_CleanupResult", ToolOnCleanupResult);
                AdminPanelLocalBridge.Register("AP_CleanupResult", ToolParseCleanupResult);
                ZRoutedRpc.instance.Register<ZPackage>("AP_BackupData", ToolOnBackupData);
                AdminPanelLocalBridge.Register("AP_BackupData", ToolParseBackupData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SchedData", ToolOnSchedData);
                AdminPanelLocalBridge.Register("AP_SchedData", ToolParseSchedData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_WorldModData", ToolOnWorldModData);
                AdminPanelLocalBridge.Register("AP_WorldModData", ToolParseWorldModData);
            }
        }

        // All six handlers share the mandated shape: Instance + SenderIsServerReply gate (these payloads
        // claim server authority - without the gate a hostile client could paint a fake backup list or a
        // fake "server option" row into an admin's panel), payload version gate, bounded counts, and a
        // whole-body try/catch that discards the ENTIRE reply so a truncated packet can never half-apply.

        // AP_CensusData v1: bool running, int scanned, int totalZdos, int shipped(<=40) x (string,int),
        //                   int droppedItems, long scanMillis
        private static void ToolOnCensusData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ToolParseCensusData(self, pkg);
        }

        internal static void ToolParseCensusData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ToolCensusData
                {
                    Running = pkg.ReadBool(),
                    Scanned = pkg.ReadInt(),
                    TotalZdos = pkg.ReadInt()
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 40) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new ToolCount { Name = pkg.ReadString(), Count = pkg.ReadInt() });
                d.DroppedItems = pkg.ReadInt();
                d.ScanMillis = pkg.ReadLong();
                self._toolCensus = d;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // AP_HotspotData v1: int shipped(<=25) x (int zoneX, int zoneY, int zdoCount, string topPrefab),
        //                    int totalZones
        private static void ToolOnHotspotData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ToolParseHotspotData(self, pkg);
        }

        internal static void ToolParseHotspotData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ToolHotspotData();
                var n = pkg.ReadInt();
                if (n < 0 || n > 25) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new ToolHotspot
                    {
                        X = pkg.ReadInt(),
                        Y = pkg.ReadInt(),
                        Count = pkg.ReadInt(),
                        Top = pkg.ReadString()
                    });
                d.TotalZones = pkg.ReadInt();
                self._toolHotspot = d;
            }
            catch (Exception) { }
        }

        // AP_CleanupResult v1: bool dryRun, int matched, int removed, int shipped(<=20) x (string,int)
        private static void ToolOnCleanupResult(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ToolParseCleanupResult(self, pkg);
        }

        internal static void ToolParseCleanupResult(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ToolCleanupData
                {
                    DryRun = pkg.ReadBool(),
                    Matched = pkg.ReadInt(),
                    Removed = pkg.ReadInt()
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 20) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new ToolCount { Name = pkg.ReadString(), Count = pkg.ReadInt() });
                self._toolCleanup = d;
                // A dry run arms the real Run button for exactly the settings it was requested with; a real
                // run disarms it again, so every destructive pass is preceded by its own fresh preview.
                self._toolCleanPreviewSig = d.DryRun ? self._toolCleanPendingSig : null;
            }
            catch (Exception) { }
        }

        // AP_BackupData v1: int shipped(<=30) x (string fileName, long sizeBytes, long ticksUtc),
        //                   bool autoBackupOn, int intervalMinutes, string lastError
        private static void ToolOnBackupData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ToolParseBackupData(self, pkg);
        }

        internal static void ToolParseBackupData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ToolBackupData();
                var n = pkg.ReadInt();
                if (n < 0 || n > 30) return;
                for (var i = 0; i < n; i++)
                    d.Files.Add(new ToolBackupFile
                    {
                        Name = pkg.ReadString(),
                        Size = pkg.ReadLong(),
                        Ticks = pkg.ReadLong()
                    });
                d.AutoOn = pkg.ReadBool();
                d.IntervalMinutes = pkg.ReadInt();
                d.LastError = pkg.ReadString() ?? "";
                self._toolBackup = d;
            }
            catch (Exception) { }
        }

        // AP_SchedData v1: bool restartPending, long restartAtTicksUtc, string restartReason,
        //                  int annShipped(<=20) x (string text, int everyMinutes, long nextTicksUtc),
        //                  string motd, bool autosaveOverrideOn, int autosaveMinutes
        private static void ToolOnSchedData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ToolParseSchedData(self, pkg);
        }

        internal static void ToolParseSchedData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ToolSchedData
                {
                    RestartPending = pkg.ReadBool(),
                    RestartAt = pkg.ReadLong(),
                    RestartReason = pkg.ReadString() ?? ""
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > ToolAnnSlots) return;
                for (var i = 0; i < n; i++)
                    d.Anns.Add(new ToolAnn
                    {
                        Text = pkg.ReadString() ?? "",
                        Every = pkg.ReadInt(),
                        Next = pkg.ReadLong()
                    });
                d.Motd = pkg.ReadString() ?? "";
                d.AutosaveOn = pkg.ReadBool();
                d.AutosaveMinutes = pkg.ReadInt();
                self._toolSched = d;
            }
            catch (Exception) { }
        }

        // AP_WorldModData v1: int shipped(<=40) x (string key, bool isServerOption), string presetName
        private static void ToolOnWorldModData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ToolParseWorldModData(self, pkg);
        }

        internal static void ToolParseWorldModData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new ToolWorldModData();
                var n = pkg.ReadInt();
                if (n < 0 || n > 40) return;
                for (var i = 0; i < n; i++)
                    d.Keys.Add(new ToolWorldKey { Key = pkg.ReadString() ?? "", ServerOption = pkg.ReadBool() });
                d.Preset = pkg.ReadString() ?? "";
                self._toolWorldMod = d;
                self._toolWorldOverride.Clear();   // fresh truth wins over the optimistic toggle state
            }
            catch (Exception) { }
        }

        // ==================== polling ====================

        // Layout-gated (it sends RPCs and mutates throttles, so once per frame not once per pass), in-world
        // only, throttle-FIRST so a companion without these handlers can never produce a request loop.
        // The only "am I allowed to send" question is whether there is anything to send TO: a remote client
        // needs a server peer, a host IS the server and answers itself. Every throttle below is unchanged.
        private void ToolPoll()
        {
            if (ZNet.instance == null) return;
            if (!_toolHostLayout && ServerUid() == 0L) return;
            var now = Time.time;
            switch (_toolViewLayout)
            {
                case 0:
                    // Only while a scan is actually running - an idle census is polled by the Scan button.
                    var c = _toolCensusLayout;
                    if (c != null && c.Running && now >= _toolNextCensusReq)
                    {
                        _toolNextCensusReq = now + 2f;
                        SrvRpc("AP_SrvCensusReq", ToolTopN());
                    }
                    break;
                case 2:
                    if (now >= _toolNextBackupReq) { _toolNextBackupReq = now + 20f; SrvRpc("AP_SrvBackupReq"); }
                    break;
                case 3:
                    if (now >= _toolNextSchedReq) { _toolNextSchedReq = now + 15f; SrvRpc("AP_SrvSchedReq"); }
                    break;
                case 4:
                    if (now >= _toolNextWorldModReq) { _toolNextWorldModReq = now + 30f; SrvRpc("AP_SrvWorldModReq"); }
                    break;
            }
        }

        // ==================== drawing ====================

        internal void DrawServerToolsSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _toolViewLayout = _toolView;
                _toolConnectedLayout = ZNet.instance != null;
                _toolHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                // Read the roster DrawWindow pinned this frame; do NOT rebuild it. Null (drawn outside the
                // normal window path) reads as "not solo" so the panel never claims solo-ness it cannot see.
                _toolSoloLayout = _toolHostLayout && _othersSnapshot != null && _othersSnapshot.Count == 0;
                _toolCensusLayout = _toolCensus;
                _toolHotspotLayout = _toolHotspot;
                _toolCleanupLayout = _toolCleanup;
                _toolBackupLayout = _toolBackup;
                _toolSchedLayout = _toolSched;
                _toolWorldModLayout = _toolWorldMod;
                _toolRunEnabledLayout = _toolCleanPreviewSig != null && _toolCleanPreviewSig == ToolCleanSig();
                ToolBuildWorldRows();
                ToolSeedEdits();
                ToolPoll();
            }

            if (!_toolConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            // Sub-view chips: they WRITE the live field, everything below gates on _toolViewLayout, so a
            // click can never hand Repaint a different control count than Layout reserved.
            GUILayout.BeginHorizontal();
            ToolChip(0, "tool.view_census");
            ToolChip(1, "tool.view_cleanup");
            ToolChip(2, "tool.view_backup");
            ToolChip(3, "tool.view_sched");
            ToolChip(4, "tool.view_mods");
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _toolScroll = GUILayout.BeginScrollView(_toolScroll, GUILayout.Height(ListView(200f)));

            // Host note. The count decision reads the Layout snapshot, so it is fixed for the whole frame;
            // the text now describes where the data comes from rather than claiming there will not be any.
            if (_toolHostLayout) GUILayout.Label(Loc.T("tool.host_note"), _hintStyle);

            switch (_toolViewLayout)
            {
                case 1: ToolDrawCleanup(); break;
                case 2: ToolDrawBackups(); break;
                case 3: ToolDrawSchedule(); break;
                case 4: ToolDrawWorldMods(); break;
                default: ToolDrawCensus(); ToolDrawHotspots(); break;
            }

            GUILayout.EndScrollView();
        }

        private void ToolChip(int index, string locKey)
        {
            var on = GUILayout.Toggle(_toolView == index, Loc.T(locKey), _chipStyleOrButton(), GUILayout.MinWidth(110));
            if (on && _toolView != index) { _toolView = index; _toolScroll = Vector2.zero; }
        }

        // ---- 1. world census ----

        private void ToolDrawCensus()
        {
            var d = _toolCensusLayout;
            BeginCard(Loc.T("tool.census_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tool.topn_label"), _labelStyle, GUILayout.MinWidth(70));
            _toolTopN = GUILayout.TextField(_toolTopN ?? "", _textFieldStyle, GUILayout.Width(60));
            if (GUILayout.Button(Loc.T("tool.scan"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                SrvRpc("AP_SrvCensusReq", ToolTopN());
                // Space the first auto-repoll so the click and the poll cannot double-send in one second.
                _toolNextCensusReq = Time.time + 2f;
                Message(Loc.T("tool.msg_scan"));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Exactly one status label on every path - the text changes, the control count does not.
            // Before the first Scan there is nothing to wait FOR: the census is request-only, so the honest
            // line is an instruction, not "no answer from the companion".
            if (d == null) GUILayout.Label(Loc.T("tool.census_pending"), _hintStyle);
            else if (d.Running) GUILayout.Label(Loc.T("tool.census_running", d.Scanned, d.TotalZdos), _headerStyle);
            else GUILayout.Label(Loc.T("tool.census_done", d.Scanned, d.ScanMillis), _labelStyle);

            if (d != null)
            {
                if (d.Rows.Count == 0)
                {
                    // A reply DID arrive, so this is never "press Scan". Mid-scan the ranking simply is not
                    // filled in yet; a finished scan with no rows means the world really holds nothing.
                    // Text swap on the pinned payload - one label either way.
                    GUILayout.Label(Loc.T(d.Running ? "tool.census_counting" : "tool.census_none"), _hintStyle);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("tool.col_rank"), _headerStyle, GUILayout.Width(50));
                    GUILayout.Label(Loc.T("tool.col_prefab"), _headerStyle, GUILayout.Width(240));
                    GUILayout.Label(Loc.T("tool.col_count"), _headerStyle, GUILayout.Width(90));
                    GUILayout.Label(Loc.T("tool.col_share"), _headerStyle, GUILayout.MinWidth(70));
                    GUILayout.EndHorizontal();

                    var total = d.TotalZdos;
                    for (var i = 0; i < d.Rows.Count; i++)
                    {
                        var r = d.Rows[i];
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.Label((i + 1).ToString(CultureInfo.InvariantCulture), _dimCellStyle, GUILayout.Width(50));
                        GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(240));
                        GUILayout.Label(r.Count.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(90));
                        GUILayout.Label(ToolPercent(r.Count, total), _dimCellStyle, GUILayout.MinWidth(70));
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.Label(Loc.T("tool.census_dropped", d.DroppedItems), _labelStyle);
            }

            GUILayout.Label(Loc.T("tool.census_hint"), _hintStyle);
            EndCard();
        }

        // ---- 2. lag hotspots (same view, below the census) ----

        private void ToolDrawHotspots()
        {
            var d = _toolHotspotLayout;
            BeginCard(Loc.T("tool.hot_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("tool.hot_find"), _buttonStyle, GUILayout.MinWidth(150)))
            {
                SrvRpc("AP_SrvHotspotReq");
                Message(Loc.T("tool.msg_hotspots"));
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(d == null ? "" : Loc.T("tool.hot_total", d.TotalZones), _dimLabelStyle);
            GUILayout.EndHorizontal();

            if (d == null)
            {
                // Request-only, exactly like the census: no scan has been asked for, nothing is pending.
                GUILayout.Label(Loc.T("tool.hot_pending"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("tool.hot_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("tool.col_zone"), _headerStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("tool.col_objects"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("tool.col_top"), _headerStyle, GUILayout.MinWidth(160));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(Loc.T("tool.zone_fmt", r.X, r.Y), _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(r.Count.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(90));
                    GUILayout.Label(r.Top ?? "", _dimCellStyle, GUILayout.MinWidth(160));
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("tool.hot_hint"), _hintStyle);
            EndCard();
        }

        // ---- 3. cleanup ----

        private void ToolDrawCleanup()
        {
            BeginCard(Loc.T("tool.clean_section"));

            GUILayout.Label(Loc.T("tool.clean_warn"), _proseStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tool.clean_mode"), _labelStyle, GUILayout.MinWidth(60));
            // Cycle button: only the LABEL changes, so reading the live mode here is control-count safe.
            if (GUILayout.Button(Loc.T(ToolModeKey(_toolCleanMode)), _buttonStyle, GUILayout.MinWidth(170)))
                _toolCleanMode = (_toolCleanMode + 1) % 3;
            GUILayout.Label(Loc.T("tool.clean_older"), _labelStyle, GUILayout.MinWidth(90));
            _toolCleanMinutes = GUILayout.TextField(_toolCleanMinutes ?? "", _textFieldStyle, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("tool.clean_preview"), _buttonStyle, GUILayout.MinWidth(110)))
                ToolSendCleanup(true);
            GUILayout.Space(8);
            // The real run stays disabled until a dry run for THESE settings has come back. GUI.enabled is a
            // style-level property, not a control, so flipping it from the snapshot is frame-safe.
            var prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && _toolRunEnabledLayout;
            if (ConfirmButton("tool:cleanup", Loc.T("tool.clean_run"), GUILayout.MinWidth(130)))
                ToolSendCleanup(false);
            GUI.enabled = prevEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(_toolRunEnabledLayout ? Loc.T("tool.clean_armed") : Loc.T("tool.clean_need_preview"), _hintStyle);
            EndCard();

            var d = _toolCleanupLayout;
            BeginCard(Loc.T("tool.clean_result"));
            if (d == null)
            {
                // Nothing has been requested, so there is no reply outstanding - point at the Preview button.
                GUILayout.Label(Loc.T("tool.clean_pending"), _hintStyle);
            }
            else
            {
                GUILayout.Label(d.DryRun
                        ? Loc.T("tool.clean_preview_line", d.Matched)
                        : Loc.T("tool.clean_run_line", d.Matched, d.Removed),
                    _headerStyle);
                if (d.Rows.Count == 0)
                {
                    GUILayout.Label(Loc.T("tool.clean_none"), _hintStyle);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("tool.col_prefab"), _headerStyle, GUILayout.Width(260));
                    GUILayout.Label(Loc.T("tool.col_count"), _headerStyle, GUILayout.MinWidth(90));
                    GUILayout.EndHorizontal();
                    for (var i = 0; i < d.Rows.Count; i++)
                    {
                        var r = d.Rows[i];
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(260));
                        GUILayout.Label(r.Count.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.MinWidth(90));
                        GUILayout.EndHorizontal();
                    }
                }
            }
            EndCard();
        }

        private void ToolSendCleanup(bool dryRun)
        {
            var mins = ToolInt(_toolCleanMinutes, 0, 525600, 60);
            var pkg = new ZPackage();
            pkg.Write(_toolCleanMode);
            pkg.Write(mins);
            pkg.Write(dryRun);
            if (dryRun) _toolCleanPendingSig = ToolCleanSig();
            SrvRpc("AP_SrvCleanupReq", pkg);
            Message(Loc.T(dryRun ? "tool.msg_preview" : "tool.msg_cleanup"));
        }

        // Identity of the settings a preview belongs to. Changing either half invalidates the armed run.
        private string ToolCleanSig() =>
            _toolCleanMode.ToString(CultureInfo.InvariantCulture) + ":" +
            ToolInt(_toolCleanMinutes, 0, 525600, 60).ToString(CultureInfo.InvariantCulture);

        private static string ToolModeKey(int mode)
        {
            if (mode == 1) return "tool.mode_orphans";
            return mode == 2 ? "tool.mode_both" : "tool.mode_drops";
        }

        // ---- 4. backups ----

        private void ToolDrawBackups()
        {
            var d = _toolBackupLayout;
            BeginCard(Loc.T("tool.bk_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("tool.bk_now"), _buttonStyle, GUILayout.MinWidth(120)))
            {
                SrvRpc("AP_SrvBackupNow");
                _toolNextBackupReq = 0f;   // the server pushes a fresh AP_BackupData; poll again promptly too
                Message(Loc.T("tool.msg_backup"));
            }
            if (GUILayout.Button(Loc.T("tool.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _toolNextBackupReq = 0f;
            GUILayout.FlexibleSpace();
            GUILayout.Label(d == null
                    ? Loc.T("tool.bk_auto_unknown")
                    : Loc.T(d.AutoOn ? "tool.bk_auto_on" : "tool.bk_auto_off", d.IntervalMinutes),
                _labelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("tool.bk_cfg_hint"), _hintStyle);
            if (d != null && !string.IsNullOrEmpty(d.LastError))
                GUILayout.Label(Loc.T("tool.bk_error", d.LastError), _proseStyle);

            if (d == null)
            {
                // This view polls every 20s, so before the first reply a request really IS outstanding -
                // state (1), and the only place in this file where "waiting for the companion" is the truth.
                GUILayout.Label(Loc.T("tool.bk_waiting"), _hintStyle);
            }
            else if (d.Files.Count == 0)
            {
                GUILayout.Label(Loc.T("tool.bk_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("tool.col_file"), _headerStyle, GUILayout.Width(250));
                GUILayout.Label(Loc.T("tool.col_size"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("tool.col_age"), _headerStyle, GUILayout.Width(110));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Files.Count; i++)
                {
                    var f = d.Files[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(f.Name ?? "", _cellStyle, GUILayout.Width(250));
                    GUILayout.Label(Loc.T("tool.size_mb", ToolMb(f.Size)), _cellStyle, GUILayout.Width(90));
                    GUILayout.Label(ToolAge(f.Ticks), _dimCellStyle, GUILayout.Width(110));
                    GUILayout.FlexibleSpace();
                    // Per-file confirm id: arming one row must never arm another.
                    if (ConfirmButton("tool:stage:" + (f.Name ?? i.ToString(CultureInfo.InvariantCulture)),
                            Loc.T("tool.bk_stage"), GUILayout.MinWidth(120)))
                    {
                        SrvRpc("AP_SrvBackupStage", f.Name ?? "");
                        Message(Loc.T("tool.msg_stage", f.Name ?? ""));
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("tool.bk_stage_hint"), _hintStyle);
            EndCard();
        }

        // ---- 5. schedule ----

        private void ToolDrawSchedule()
        {
            ToolDrawRestartCard();
            ToolDrawAnnouncementsCard();
            ToolDrawMotdCard();
            ToolDrawAutosaveCard();
        }

        private void ToolDrawRestartCard()
        {
            var sc = _toolSchedLayout;
            BeginCard(Loc.T("tool.restart_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tool.restart_minutes"), _labelStyle, GUILayout.MinWidth(110));
            _toolRestartMinutes = GUILayout.TextField(_toolRestartMinutes ?? "", _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("tool.restart_reason"), _labelStyle, GUILayout.MinWidth(70));
            _toolRestartReason = GUILayout.TextField(_toolRestartReason ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (ConfirmButton("tool:restart", Loc.T("tool.restart_apply"), GUILayout.MinWidth(150)))
            {
                var pkg = new ZPackage();
                var mins = ToolInt(_toolRestartMinutes, 0, 1440, 10);
                pkg.Write(mins);
                pkg.Write(ToolTrim(_toolRestartReason) ?? "");
                pkg.Write(false);
                SrvRpc("AP_SrvRestartReq", pkg);
                _toolNextSchedReq = 0f;
                Message(Loc.T("tool.msg_restart", mins));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // The countdown row exists only when the snapshot says a restart is pending, so its control
            // count is decided on Layout and stays put for the rest of the frame.
            if (sc != null && sc.RestartPending)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("tool.restart_in", ToolCountdown(sc.RestartAt)), _headerStyle);
                GUILayout.Space(8);
                GUILayout.Label(string.IsNullOrEmpty(sc.RestartReason)
                    ? Loc.T("tool.reason_none") : sc.RestartReason, _dimLabelStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("tool.restart_cancel"), _buttonStyle, GUILayout.MinWidth(90)))
                {
                    var pkg = new ZPackage();
                    pkg.Write(0);
                    pkg.Write("");
                    pkg.Write(true);
                    SrvRpc("AP_SrvRestartReq", pkg);
                    _toolNextSchedReq = 0f;
                    Message(Loc.T("tool.msg_restart_cancel"));
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                // Three distinct truths behind one label: the schedule poll has not answered yet; nothing is
                // scheduled on a server that has other people on it; or nothing is scheduled and there is
                // nobody to schedule around, in which case say plainly that this is a dedicated-server tool.
                GUILayout.Label(sc == null
                        ? Loc.T("tool.sched_waiting")
                        : Loc.T(_toolSoloLayout ? "tool.restart_none_solo" : "tool.restart_none"),
                    _hintStyle);
            }

            GUILayout.Label(Loc.T("tool.restart_hint"), _hintStyle);
            EndCard();
        }

        // THE control-count trap of this whole file: the drawn row list comes from the Layout-pinned
        // payload, NEVER from the live edit arrays (which a keystroke or a Save/Delete click mutates
        // mid-frame). The edit arrays only ever supply TEXT for fields the pinned list already reserved.
        private void ToolDrawAnnouncementsCard()
        {
            var sc = _toolSchedLayout;
            BeginCard(Loc.T("tool.ann_section"));

            var rows = sc != null ? sc.Anns.Count : 0;
            if (rows > ToolAnnSlots) rows = ToolAnnSlots;

            if (sc == null)
            {
                GUILayout.Label(Loc.T("tool.sched_waiting"), _hintStyle);
            }
            else if (rows == 0)
            {
                // Zero announcements is a perfectly healthy server. Solo it is also structurally pointless,
                // so name it as the dedicated-server feature it is instead of leaving a bare "nothing here".
                GUILayout.Label(Loc.T(_toolSoloLayout ? "tool.ann_empty_solo" : "tool.ann_empty"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < rows; i++)
                {
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(Loc.T("tool.ann_slot", i), _dimCellStyle, GUILayout.Width(40));
                    _toolAnnText[i] = GUILayout.TextField(_toolAnnText[i] ?? "", _textFieldStyle, GUILayout.MinWidth(200));
                    GUILayout.Label(Loc.T("tool.ann_every"), _labelStyle, GUILayout.MinWidth(50));
                    _toolAnnEvery[i] = GUILayout.TextField(_toolAnnEvery[i] ?? "", _textFieldStyle, GUILayout.Width(50));
                    if (GUILayout.Button(Loc.T("tool.save"), _buttonStyle, GUILayout.MinWidth(70)))
                        ToolSendAnn(i, _toolAnnText[i], ToolInt(_toolAnnEvery[i], 1, 10080, 60));
                    if (ConfirmButton("tool:anndel:" + i.ToString(CultureInfo.InvariantCulture),
                            Loc.T("tool.delete"), GUILayout.MinWidth(80)))
                        ToolSendAnn(i, "", 0);       // everyMinutes <= 0 deletes the slot (wire contract)
                    GUILayout.EndHorizontal();

                    // Next-fire line for the same row, from the pinned payload.
                    var a = sc.Anns[i];
                    GUILayout.Label(a.Every > 0
                            ? Loc.T("tool.ann_next", a.Every, ToolCountdown(a.Next))
                            : Loc.T("tool.ann_off"),
                        _hintStyle);
                }
            }

            // Add-row: reserved on the pinned count only, so it appears/disappears between frames, never
            // between Layout and Repaint.
            if (sc != null && rows < ToolAnnSlots)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("tool.ann_new"), _labelStyle, GUILayout.MinWidth(40));
                _toolAnnNewText = GUILayout.TextField(_toolAnnNewText ?? "", _textFieldStyle, GUILayout.MinWidth(200));
                GUILayout.Label(Loc.T("tool.ann_every"), _labelStyle, GUILayout.MinWidth(50));
                _toolAnnNewEvery = GUILayout.TextField(_toolAnnNewEvery ?? "", _textFieldStyle, GUILayout.Width(50));
                if (GUILayout.Button(Loc.T("tool.ann_add"), _buttonStyle, GUILayout.MinWidth(70)))
                {
                    var text = ToolTrim(_toolAnnNewText);
                    if (text == null) Message(Loc.T("tool.msg_need_text"));
                    else
                    {
                        ToolSendAnn(rows, text, ToolInt(_toolAnnNewEvery, 1, 10080, 60));
                        _toolAnnNewText = "";
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Label(Loc.T("tool.ann_hint"), _hintStyle);
            EndCard();
        }

        private void ToolSendAnn(int slot, string text, int everyMinutes)
        {
            if (slot < 0 || slot >= ToolAnnSlots) return;
            var pkg = new ZPackage();
            pkg.Write(slot);
            pkg.Write(text ?? "");
            pkg.Write(everyMinutes);
            SrvRpc("AP_SrvAnnSet", pkg);
            _toolNextSchedReq = 0f;
            _toolAnnSeededRows = -1;   // let the next reply reseed the edit fields from server truth
            Message(Loc.T(everyMinutes > 0 ? "tool.msg_ann_saved" : "tool.msg_ann_deleted", slot));
        }

        private void ToolDrawMotdCard()
        {
            BeginCard(Loc.T("tool.motd_section"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tool.motd_label"), _labelStyle, GUILayout.MinWidth(60));
            _toolMotd = GUILayout.TextField(_toolMotd ?? "", _textFieldStyle, GUILayout.MinWidth(260));
            if (GUILayout.Button(Loc.T("tool.save"), _buttonStyle, GUILayout.MinWidth(70)))
            {
                SrvRpc("AP_SrvMotdSet", _toolMotd ?? "");
                _toolNextSchedReq = 0f;
                Message(Loc.T("tool.msg_motd"));
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("tool.motd_hint"), _hintStyle);
            EndCard();
        }

        private void ToolDrawAutosaveCard()
        {
            BeginCard(Loc.T("tool.autosave_section"));
            GUILayout.BeginHorizontal();
            // A checkbox is one control whatever its value, so binding it live is safe; the Apply button
            // is what actually sends, so a mis-click never changes the server on its own.
            _toolAutosaveOn = GUILayout.Toggle(_toolAutosaveOn, " " + Loc.T("tool.autosave_on"), _toggleStyle);
            GUILayout.Space(8);
            GUILayout.Label(Loc.T("tool.autosave_minutes"), _labelStyle, GUILayout.MinWidth(90));
            _toolAutosaveMin = GUILayout.TextField(_toolAutosaveMin ?? "", _textFieldStyle, GUILayout.Width(60));
            if (GUILayout.Button(Loc.T("tool.apply"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                var pkg = new ZPackage();
                var mins = ToolInt(_toolAutosaveMin, 1, 1440, 20);
                pkg.Write(_toolAutosaveOn);
                pkg.Write(mins);
                SrvRpc("AP_SrvAutosaveSet", pkg);
                _toolNextSchedReq = 0f;
                Message(Loc.T(_toolAutosaveOn ? "tool.msg_autosave_on" : "tool.msg_autosave_off", mins));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("tool.autosave_hint"), _hintStyle);
            EndCard();
        }

        // ---- 6. world modifiers ----

        private void ToolDrawWorldMods()
        {
            var d = _toolWorldModLayout;
            var rows = _toolWorldRowsLayout;
            var on = _toolWorldOnLayout;

            BeginCard(Loc.T("tool.wm_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("tool.wm_preset"), _labelStyle, GUILayout.MinWidth(70));
            GUILayout.Label(d == null || string.IsNullOrEmpty(d.Preset) ? Loc.T("tool.wm_preset_unknown") : d.Preset,
                _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("tool.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _toolNextWorldModReq = 0f;
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("tool.wm_warn"), _proseStyle);

            if (rows == null || rows.Count == 0)
            {
                // Polled every 30s, so a null payload is a real outstanding request; a payload with no keys
                // means this world simply runs on its defaults, which is not a fault.
                GUILayout.Label(d == null ? Loc.T("tool.wm_waiting") : Loc.T("tool.wm_empty"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var k = rows[i];
                    var isOn = on != null && on.Contains(k.Key);
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(k.Key ?? "", _cellStyle, GUILayout.Width(230));
                    GUILayout.Label(Loc.T(k.ServerOption ? "tool.wm_permanent" : "tool.wm_session"),
                        _dimCellStyle, GUILayout.Width(150));
                    GUILayout.FlexibleSpace();
                    if (k.ServerOption)
                    {
                        // A permanent world change cannot ride a one-click checkbox: label + ConfirmButton,
                        // never a conditional widget (the label swap keeps the control count identical).
                        GUILayout.Label(Loc.T(isOn ? "common.on" : "common.off"), _cellStyle, GUILayout.Width(50));
                        if (ConfirmButton("tool:wm:" + (k.Key ?? i.ToString(CultureInfo.InvariantCulture)),
                                Loc.T(isOn ? "tool.wm_disable" : "tool.wm_enable"), GUILayout.MinWidth(110)))
                            ToolSendWorldMod(k.Key, !isOn);
                    }
                    else
                    {
                        GUILayout.Label("", _cellStyle, GUILayout.Width(50));
                        var now = GUILayout.Toggle(isOn, " " + Loc.T(isOn ? "common.on" : "common.off"),
                            _toggleStyle, GUILayout.MinWidth(110));
                        if (now != isOn) ToolSendWorldMod(k.Key, now);
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("tool.wm_hint"), _hintStyle);
            EndCard();
        }

        private void ToolSendWorldMod(string key, bool on)
        {
            if (string.IsNullOrEmpty(key)) return;
            var pkg = new ZPackage();
            pkg.Write(key);
            pkg.Write(on);
            SrvRpc("AP_SrvWorldModSet", pkg);
            _toolWorldOverride[key] = on;   // optimistic until the next reply replaces it with server truth
            _toolNextWorldModReq = 0f;
            Message(Loc.T(on ? "tool.msg_wm_on" : "tool.msg_wm_off", key));
        }

        // Union of every key the server has reported this session plus the current on-set. Built on the
        // Layout pass only: a toggle click writes _toolWorldOverride during the event pass and would
        // otherwise change the row set mid-frame.
        private void ToolBuildWorldRows()
        {
            var d = _toolWorldModLayout;
            if (d != null)
            {
                for (var i = 0; i < d.Keys.Count; i++)
                {
                    var k = d.Keys[i];
                    if (string.IsNullOrEmpty(k.Key)) continue;
                    var seen = false;
                    for (var j = 0; j < _toolWorldKnown.Count; j++)
                        if (_toolWorldKnown[j].Key == k.Key) { seen = true; break; }
                    if (!seen) _toolWorldKnown.Add(k);
                }
            }

            var set = new HashSet<string>(StringComparer.Ordinal);
            if (d != null)
                for (var i = 0; i < d.Keys.Count; i++)
                    if (!string.IsNullOrEmpty(d.Keys[i].Key)) set.Add(d.Keys[i].Key);
            foreach (var kv in _toolWorldOverride)
            {
                if (kv.Value) set.Add(kv.Key);
                else set.Remove(kv.Key);
            }

            _toolWorldOnLayout = set;
            _toolWorldRowsLayout = new List<ToolWorldKey>(_toolWorldKnown);
        }

        // ==================== edit-field seeding ====================

        // Seeds the schedule edit fields from server truth WITHOUT stomping typing: MOTD/autosave seed once
        // per session, announcement rows reseed only when the row COUNT changes (a save/delete) or after a
        // send asked for it. Layout pass only - it feeds text fields the same frame's rows were reserved for.
        private void ToolSeedEdits()
        {
            var sc = _toolSchedLayout;
            if (sc == null) return;

            if (!_toolMotdSeeded) { _toolMotd = sc.Motd ?? ""; _toolMotdSeeded = true; }

            if (!_toolAutosaveSeeded)
            {
                _toolAutosaveOn = sc.AutosaveOn;
                _toolAutosaveMin = (sc.AutosaveMinutes > 0 ? sc.AutosaveMinutes : 20).ToString(CultureInfo.InvariantCulture);
                _toolAutosaveSeeded = true;
            }

            var rows = Mathf.Min(sc.Anns.Count, ToolAnnSlots);
            if (_toolAnnSeededRows == rows) return;
            for (var i = 0; i < ToolAnnSlots; i++)
            {
                if (i < rows)
                {
                    var a = sc.Anns[i];
                    _toolAnnText[i] = a.Text ?? "";
                    _toolAnnEvery[i] = a.Every.ToString(CultureInfo.InvariantCulture);
                }
                else { _toolAnnText[i] = ""; _toolAnnEvery[i] = ""; }
            }
            _toolAnnSeededRows = rows;
        }

        // ==================== small helpers ====================

        private int ToolTopN() => ToolInt(_toolTopN, 1, 40, 25);

        private static string ToolTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        private static int ToolInt(string s, int min, int max, int fallback)
        {
            int v;
            if (!int.TryParse(ToolTrim(s) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return fallback;
            return Mathf.Clamp(v, min, max);
        }

        private static string ToolPercent(int count, int total)
        {
            if (total <= 0) return "-";
            return (count * 100f / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static string ToolMb(long bytes)
        {
            if (bytes < 0L) bytes = 0L;
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // Age of a server-stamped DateTime.UtcNow.Ticks value. Client and server clocks drift a little, so
        // this is a display approximation only.
        private static string ToolAge(long ticksUtc)
        {
            if (ticksUtc <= 0L) return "-";
            var delta = DateTime.UtcNow.Ticks - ticksUtc;
            if (delta < 0L) delta = 0L;
            var mins = delta / TimeSpan.TicksPerMinute;
            if (mins < 60L) return Loc.T("tool.age_min", mins);
            if (mins < 1440L) return Loc.T("tool.age_hour", mins / 60L);
            return Loc.T("tool.age_day", mins / 1440L);
        }

        // Time remaining until a server-stamped tick value, as digits only ("4m 05s") so no locale can
        // mangle it. Past/zero renders as the "any moment now" string.
        private static string ToolCountdown(long ticksUtc)
        {
            if (ticksUtc <= 0L) return Loc.T("tool.due_now");
            var delta = ticksUtc - DateTime.UtcNow.Ticks;
            if (delta <= 0L) return Loc.T("tool.due_now");
            var secs = delta / TimeSpan.TicksPerSecond;
            if (secs < 3600L)
                return (secs / 60L).ToString(CultureInfo.InvariantCulture) + "m " +
                       (secs % 60L).ToString("00", CultureInfo.InvariantCulture) + "s";
            var hours = secs / 3600L;
            return hours.ToString(CultureInfo.InvariantCulture) + "h " +
                   ((secs % 3600L) / 60L).ToString("00", CultureInfo.InvariantCulture) + "m";
        }
    }
}
