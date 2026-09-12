using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 5 - Area tools section (Extras tab) ====================
    // Client half of the companion's area suite (AdminPanelCompanion\Wave5SrvArea.cs and
    // Wave5SrvPortals.cs). Five sub-views behind one chip row: ownership transfer + mass repair/remove in a
    // radius, protection zones, wards, portals and the prefab spawner. Everything here is UI and request
    // plumbing - the server owns the truth and ships it back in six reply payloads that this file parses
    // defensively and draws from per-frame Layout snapshots.
    //
    // Member prefix: "Area". Locale prefix: "area.".
    //
    // IMGUI law observed throughout: the reply handlers write the live payload fields at ANY time (they run
    // in ZNet.Update), while every draw method reads ONLY the *Layout snapshots pinned at the top of
    // DrawAreaToolsSection. Row counts, empty-state branches, the host gate and the GUI.enabled state of the
    // two destructive run buttons all come from those snapshots, so Repaint can never see a different
    // control count than Layout reserved. Text fields are edited live on purpose (a TextField is one control
    // whatever it contains); the per-row portal tag editors are safe because the ROW LIST is snapshotted and
    // the edit buffer is keyed by a stable row key rather than a list index, so it survives every repaint
    // and every refresh.
    //
    // HOST MODE: a listen-server host has no server peer, so ServerUid() is structurally 0 there. That is a
    // fact about routing, not about capability - the companion runs in THIS process and routes its reply back
    // to our own session id, which SenderIsServerReply now accepts. So the polls gate on "am I in a world"
    // rather than on IsServer(), and every list here fills in on a host. ServerUid() stays a valid test for a
    // REMOTE client only (no server peer yet = nowhere to send).
    //
    // One host-only throttle change: the ward list is a full-world, frame-spread ZDO scan that the companion
    // runs with a per-frame millisecond budget. On a dedicated server that budget is spent on a machine
    // nobody is looking at; on a host it is spent inside the single player's own frame, so re-scanning every
    // 60 s while the Wards view sits open is a visible stutter. Host mode therefore polls it at
    // AreaWardHostSeconds and leans on the Refresh button; the remote interval is unchanged.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _areaSectionCfg;
        private bool _areaInited;

        // ---- caps mirrored from the server so the panel never asks for something it knows is refused ----
        private const float AreaRadiusMin = 1f;
        private const float AreaRadiusMax = 128f;    // Wave5Area.HardMaxRadius
        private const int AreaTagMax = 10;           // vanilla TeleportWorld tag cap (TextInput.RequestText)
        private const int AreaPrefabNameMax = 64;    // Wave5Portals.MaxNameLen
        private const int AreaZoneNameMax = 48;      // Wave5Area.SanitizeZoneName
        private const int AreaMassRowCap = 20;       // AP_MassPiece shipped cap
        private const int AreaZoneRowCap = 40;       // AP_ZoneList shipped cap
        private const int AreaWardRowCap = 40;       // AP_WardList shipped cap
        private const int AreaPortalRowCap = 60;     // AP_PortalList shipped cap
        private const int AreaLocationRowCap = 60;   // AP_LocationList shipped cap

        // ---- poll intervals (seconds) ----
        // Zones are a stored list and portals come from ZDOMan's already-built portal list, so both are cheap
        // enough to re-read on the same 20 s beat on a host as on a dedicated server. The ward list is the
        // exception: it is a world-wide sector scan, and on a host it burns its frame budget inside the only
        // player's game, so it gets a much longer beat there.
        private const float AreaZoneSeconds = 20f;
        private const float AreaPortalSeconds = 20f;
        private const float AreaWardSeconds = 60f;
        private const float AreaWardHostSeconds = 300f;

        // ---- server truth (written by the reply handlers, read only through the *Layout snapshots) ----
        private sealed class AreaXferData { public bool DryRun; public int Matched; public int Changed; }

        private sealed class AreaMassData
        {
            public bool DryRun;
            public int Action;      // 0 repair, 1 remove
            public int Matched;
            public int Affected;
            public readonly List<AreaCountRow> Rows = new List<AreaCountRow>();
        }

        private sealed class AreaZoneData { public readonly List<AreaZoneRow> Rows = new List<AreaZoneRow>(); }

        private sealed class AreaWardData
        {
            public int Total;
            public readonly List<AreaWardRow> Rows = new List<AreaWardRow>();
        }

        private sealed class AreaPortalData
        {
            public int Total;
            public readonly List<AreaPortalRow> Rows = new List<AreaPortalRow>();
        }

        private sealed class AreaLocationData
        {
            public int Total;
            public readonly List<string> Names = new List<string>();
        }

        private struct AreaCountRow { public string Name; public int Count; }
        private struct AreaZoneRow { public string Name; public Vector3 Pos; public float Radius; public int Kind; }
        private struct AreaWardRow { public Vector3 Pos; public string Owner; public bool Enabled; public bool Permitted; }
        private struct AreaPortalRow { public Vector3 Pos; public string Tag; public bool Connected; public string TargetTag; }

        // ---- view + frame state ----
        private int _areaView;                       // 0 edit, 1 zones, 2 wards, 3 portals, 4 spawner
        private int _areaViewLayout;
        private bool _areaConnectedLayout;
        private bool _areaHostLayout;
        private Vector2 _areaScroll;

        // ---- shared centre + radius (Area Edit) ----
        private Vector3 _areaCentre;
        private bool _areaCentreSet;                 // false = follow the live player position
        private string _areaRadius = "20";
        private Vector3 _areaCentreLayout;
        private bool _areaCentrePinnedLayout;

        // ---- ownership transfer ----
        private long _areaXferTargetId;              // 0 = the server (the wire value the companion reads)
        private AreaXferData _areaXfer, _areaXferLayout;
        private string _areaXferPendingSig;          // signature the in-flight dry run was sent for
        private string _areaXferPreviewSig;          // signature the last dry-run RESULT belongs to
        private bool _areaXferArmedLayout;

        // ---- mass repair / remove ----
        private int _areaMassAction;                 // 0 repair, 1 remove
        private string _areaMassFilter = "";
        private AreaMassData _areaMass, _areaMassLayout;
        private string _areaMassPendingSig, _areaMassPreviewSig;
        private bool _areaMassArmedLayout;

        // ---- zones ----
        private AreaZoneData _areaZones, _areaZonesLayout;
        private float _areaNextZoneReq;
        private string _areaZoneName = "";
        private string _areaZoneRadius = "16";
        private int _areaZoneKind;                   // 0 no-build, 1 no-damage, 2 both
        private Vector3 _areaZoneCentre;
        private bool _areaZoneCentreSet;
        private Vector3 _areaZoneCentreLayout;
        private bool _areaZoneCentrePinnedLayout;

        // ---- wards ----
        private AreaWardData _areaWards, _areaWardsLayout;
        private float _areaNextWardReq;

        // ---- portals ----
        private AreaPortalData _areaPortals, _areaPortalsLayout;
        private float _areaNextPortalReq;
        // Per-row tag editors. Keyed by the row's rounded position (a stable identity, never a list index)
        // so a refresh that reorders the nearest-first list cannot move a half-typed tag onto another portal.
        private readonly Dictionary<string, string> _areaPortalEdit = new Dictionary<string, string>(StringComparer.Ordinal);

        // ---- spawner ----
        private string _areaSpawnPrefab = "";
        private string _areaSpawnRot = "0";
        private AreaLocationData _areaLocations, _areaLocationsLayout;

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies AreaRpcRegistration.
        internal void AreaInit()
        {
            if (_areaInited) return;
            _areaInited = true;
            _areaSectionCfg = Config.Bind("Features", "ShowAreaToolsSection", true,
                "Show the Area Tools section in the Extras tab (ownership transfer, mass repair/remove, protection zones, wards, portals, prefab spawner). Client-side UI only - it changes nothing on its own, and every action still has to pass the server companion's own role checks and kill switches.");
        }

        internal bool AreaSectionEnabled() => _areaSectionCfg == null || _areaSectionCfg.Value;

        // Called on logout. EVERY per-world field goes back to its initial value here or server A's ward list
        // renders with live Remove buttons on server B - the same hazard the 2.4.0 server-truth block calls
        // out. That includes the preview signatures: an armed mass-remove must never survive a world change.
        internal void AreaReset()
        {
            _areaView = 0;
            _areaViewLayout = 0;
            _areaConnectedLayout = false;
            _areaHostLayout = false;
            _areaScroll = Vector2.zero;

            _areaCentre = Vector3.zero;
            _areaCentreSet = false;
            _areaCentreLayout = Vector3.zero;
            _areaCentrePinnedLayout = false;
            _areaRadius = "20";

            _areaXferTargetId = 0L;
            _areaXfer = null;
            _areaXferLayout = null;
            _areaXferPendingSig = null;
            _areaXferPreviewSig = null;
            _areaXferArmedLayout = false;

            _areaMassAction = 0;
            _areaMassFilter = "";
            _areaMass = null;
            _areaMassLayout = null;
            _areaMassPendingSig = null;
            _areaMassPreviewSig = null;
            _areaMassArmedLayout = false;

            _areaZones = null;
            _areaZonesLayout = null;
            _areaNextZoneReq = 0f;
            _areaZoneName = "";
            _areaZoneRadius = "16";
            _areaZoneKind = 0;
            _areaZoneCentre = Vector3.zero;
            _areaZoneCentreSet = false;
            _areaZoneCentreLayout = Vector3.zero;
            _areaZoneCentrePinnedLayout = false;

            _areaWards = null;
            _areaWardsLayout = null;
            _areaNextWardReq = 0f;

            _areaPortals = null;
            _areaPortalsLayout = null;
            _areaNextPortalReq = 0f;
            _areaPortalEdit.Clear();

            _areaSpawnPrefab = "";
            _areaSpawnRot = "0";
            _areaLocations = null;
            _areaLocationsLayout = null;
        }

        // ==================== reply plumbing ====================

        // Own registration class so this file needs no edit to RpcRegistration in the main file. Client reply
        // handlers bind on world join (ZNet.Awake), never at plugin load - ZRoutedRpc does not exist yet in
        // the main menu, hence the null guard.
        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class AreaRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Each Register is paired with the local-bridge registration for the SAME name, so the
                // network path and the host's in-process path can never drift apart. On a listen-server host
                // the companion calls the parser directly (there is no packet and no sender to spoof); the
                // gate below is untouched and still guards everything that arrives over the wire.
                ZRoutedRpc.instance.Register<ZPackage>("AP_OwnerXfer", OnAreaOwnerXfer);
                AdminPanelLocalBridge.Register("AP_OwnerXfer", ParseAreaOwnerXfer);
                ZRoutedRpc.instance.Register<ZPackage>("AP_MassPiece", OnAreaMassPiece);
                AdminPanelLocalBridge.Register("AP_MassPiece", ParseAreaMassPiece);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ZoneList", OnAreaZoneList);
                AdminPanelLocalBridge.Register("AP_ZoneList", ParseAreaZoneList);
                ZRoutedRpc.instance.Register<ZPackage>("AP_WardList", OnAreaWardList);
                AdminPanelLocalBridge.Register("AP_WardList", ParseAreaWardList);
                ZRoutedRpc.instance.Register<ZPackage>("AP_PortalList", OnAreaPortalList);
                AdminPanelLocalBridge.Register("AP_PortalList", ParseAreaPortalList);
                ZRoutedRpc.instance.Register<ZPackage>("AP_LocationList", OnAreaLocationList);
                AdminPanelLocalBridge.Register("AP_LocationList", ParseAreaLocationList);
            }
        }

        // Every handler below follows the mandated shape: Instance + SenderIsServerReply gate (Valheim
        // relays any routed RPC to a target uid without permission filtering, so without the gate a hostile
        // client could paint attacker-chosen "truth" into an admin's panel), a leading version int, hard
        // count caps taken from the server's own constants, and a whole-body try/catch that discards the
        // WHOLE payload - a truncated packet must never leave a half-parsed table on screen.

        // AP_OwnerXfer: {int ver, bool dryRun, int matched, int changed}
        private static void OnAreaOwnerXfer(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAreaOwnerXfer(self, pkg);
        }

        internal static void ParseAreaOwnerXfer(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new AreaXferData();
                d.DryRun = pkg.ReadBool();
                d.Matched = pkg.ReadInt();
                d.Changed = pkg.ReadInt();
                self._areaXfer = d;
                // A dry run arms the real transfer for exactly the settings it was sent with; a real run (or
                // a refusal answered with dryRun=false) disarms it again.
                self._areaXferPreviewSig = d.DryRun ? self._areaXferPendingSig : null;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // AP_MassPiece: {int ver, bool dryRun, int action, int matched, int affected, int shipped(<=20),
        //                shipped x (string prefabName, int count)}
        private static void OnAreaMassPiece(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAreaMassPiece(self, pkg);
        }

        internal static void ParseAreaMassPiece(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new AreaMassData();
                d.DryRun = pkg.ReadBool();
                d.Action = pkg.ReadInt();
                d.Matched = pkg.ReadInt();
                d.Affected = pkg.ReadInt();
                var n = pkg.ReadInt();
                if (n < 0 || n > AreaMassRowCap) return;
                for (var i = 0; i < n; i++)
                {
                    var name = pkg.ReadString() ?? "";
                    var count = pkg.ReadInt();
                    d.Rows.Add(new AreaCountRow { Name = name, Count = count });
                }
                self._areaMass = d;
                self._areaMassPreviewSig = d.DryRun ? self._areaMassPendingSig : null;
            }
            catch (Exception) { }
        }

        // AP_ZoneList: {int ver, int shipped(<=40), shipped x (string name, float x, float y, float z,
        //               float radius, int kind)}
        // NOTE: no total on the wire - the server sends at most 40 of the up-to-64 zones it stores and does
        // not say how many it dropped, so the panel can only say how many it was shown.
        private static void OnAreaZoneList(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAreaZoneList(self, pkg);
        }

        internal static void ParseAreaZoneList(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new AreaZoneData();
                var n = pkg.ReadInt();
                if (n < 0 || n > AreaZoneRowCap) return;
                for (var i = 0; i < n; i++)
                {
                    var name = pkg.ReadString() ?? "";
                    var x = pkg.ReadSingle();
                    var y = pkg.ReadSingle();
                    var z = pkg.ReadSingle();
                    var radius = pkg.ReadSingle();
                    var kind = pkg.ReadInt();
                    d.Rows.Add(new AreaZoneRow
                    {
                        Name = name,
                        Pos = new Vector3(x, y, z),
                        Radius = radius,
                        Kind = Mathf.Clamp(kind, 0, 2),
                    });
                }
                self._areaZones = d;
            }
            catch (Exception) { }
        }

        // AP_WardList: {int ver, int total, int shipped(<=40),
        //               shipped x (float x, float y, float z, string ownerName, bool enabled, bool permitted)}
        private static void OnAreaWardList(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAreaWardList(self, pkg);
        }

        internal static void ParseAreaWardList(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new AreaWardData();
                d.Total = pkg.ReadInt();
                var n = pkg.ReadInt();
                if (n < 0 || n > AreaWardRowCap) return;
                for (var i = 0; i < n; i++)
                {
                    var x = pkg.ReadSingle();
                    var y = pkg.ReadSingle();
                    var z = pkg.ReadSingle();
                    var owner = pkg.ReadString() ?? "";
                    var enabled = pkg.ReadBool();
                    var permitted = pkg.ReadBool();
                    d.Rows.Add(new AreaWardRow
                    {
                        Pos = new Vector3(x, y, z),
                        Owner = owner,
                        Enabled = enabled,
                        Permitted = permitted,
                    });
                }
                if (d.Total < d.Rows.Count) d.Total = d.Rows.Count;   // never render "showing 40 of 0"
                self._areaWards = d;
            }
            catch (Exception) { }
        }

        // AP_PortalList: {int ver, int total, int shipped(<=60),
        //                 shipped x (float x, float y, float z, string tag, bool connected, string targetTag)}
        private static void OnAreaPortalList(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAreaPortalList(self, pkg);
        }

        internal static void ParseAreaPortalList(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new AreaPortalData();
                d.Total = pkg.ReadInt();
                var n = pkg.ReadInt();
                if (n < 0 || n > AreaPortalRowCap) return;
                for (var i = 0; i < n; i++)
                {
                    var x = pkg.ReadSingle();
                    var y = pkg.ReadSingle();
                    var z = pkg.ReadSingle();
                    var tag = pkg.ReadString() ?? "";
                    var connected = pkg.ReadBool();
                    var target = pkg.ReadString() ?? "";
                    d.Rows.Add(new AreaPortalRow
                    {
                        Pos = new Vector3(x, y, z),
                        Tag = tag,
                        Connected = connected,
                        TargetTag = target,
                    });
                }
                if (d.Total < d.Rows.Count) d.Total = d.Rows.Count;
                self._areaPortals = d;
            }
            catch (Exception) { }
        }

        // AP_LocationList: {int ver, int total, int shipped(<=60), shipped x (string name)}
        private static void OnAreaLocationList(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseAreaLocationList(self, pkg);
        }

        internal static void ParseAreaLocationList(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new AreaLocationData();
                d.Total = pkg.ReadInt();
                var n = pkg.ReadInt();
                if (n < 0 || n > AreaLocationRowCap) return;
                for (var i = 0; i < n; i++) d.Names.Add(pkg.ReadString() ?? "");
                if (d.Total < d.Names.Count) d.Total = d.Names.Count;
                self._areaLocations = d;
            }
            catch (Exception) { }
        }

        // ==================== polling ====================

        // Called from the Layout block only (it sends RPCs and mutates throttles, so once per frame, not
        // once per OnGUI pass), in-a-world second, throttle-FIRST third so a companion without these
        // handlers can never produce a request loop. Only the visible sub-view polls; the spawner's location
        // lookup is button-driven because it needs a filter string the admin has to type first.
        //
        // The second gate is deliberately NOT "IsServer() means give up": a host is exactly the case where
        // ServerUid() is 0 while the companion is right here and answers. Only a remote client with no server
        // peer yet has nowhere to send, so that test is applied to remote clients alone.
        private void AreaPoll()
        {
            if (Event.current == null || Event.current.type != EventType.Layout) return;
            if (ZNet.instance == null) return;                          // not in a world
            if (!_areaHostLayout && ServerUid() == 0L) return;           // remote client, not attached yet
            var now = Time.time;
            switch (_areaViewLayout)
            {
                case 1:
                    if (now >= _areaNextZoneReq) { _areaNextZoneReq = now + AreaZoneSeconds; SrvRpc("AP_SrvZoneListReq"); }
                    break;
                case 2:
                    // A ward list is a world-wide, frame-spread scan on the server, so it is polled rarely
                    // (effectively "once when you open the view") and refreshed by hand otherwise. On a host
                    // that scan runs inside the player's own frames, so it is stretched further still.
                    if (now >= _areaNextWardReq)
                    {
                        _areaNextWardReq = now + (_areaHostLayout ? AreaWardHostSeconds : AreaWardSeconds);
                        SrvRpc("AP_SrvWardListReq");
                    }
                    break;
                case 3:
                    if (now >= _areaNextPortalReq) { _areaNextPortalReq = now + AreaPortalSeconds; SrvRpc("AP_SrvPortalListReq"); }
                    break;
            }
        }

        // ==================== drawing ====================

        internal void DrawAreaToolsSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _areaViewLayout = _areaView;
                _areaConnectedLayout = ZNet.instance != null;
                _areaHostLayout = ZNet.instance != null && ZNet.instance.IsServer();

                _areaCentreLayout = AreaCentre();
                _areaCentrePinnedLayout = _areaCentreSet;
                _areaZoneCentreLayout = AreaZoneCentre();
                _areaZoneCentrePinnedLayout = _areaZoneCentreSet;

                _areaXferLayout = _areaXfer;
                _areaMassLayout = _areaMass;
                _areaZonesLayout = _areaZones;
                _areaWardsLayout = _areaWards;
                _areaPortalsLayout = _areaPortals;
                _areaLocationsLayout = _areaLocations;

                // Both destructive runs stay disabled until a dry run for EXACTLY these settings came back.
                _areaXferArmedLayout = _areaXferPreviewSig != null && _areaXferPreviewSig == AreaXferSig();
                _areaMassArmedLayout = _areaMassPreviewSig != null && _areaMassPreviewSig == AreaMassSig();

                AreaSeedPortalEdits();
                AreaPoll();
            }

            if (!_areaConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            // Sub-view chips: they WRITE the live field, everything below gates on _areaViewLayout, so a
            // click can never hand Repaint a different control count than Layout reserved.
            GUILayout.BeginHorizontal();
            AreaChip(0, "area.view_edit");
            AreaChip(1, "area.view_zones");
            AreaChip(2, "area.view_wards");
            AreaChip(3, "area.view_portals");
            AreaChip(4, "area.view_spawner");
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _areaScroll = GUILayout.BeginScrollView(_areaScroll, GUILayout.Height(ListView(200f)));

            // Host-only note. _areaHostLayout is a Layout snapshot and nothing else writes it, so this label
            // is present or absent for the WHOLE frame - Repaint always sees the count Layout reserved.
            if (_areaHostLayout) GUILayout.Label(Loc.T("area.host_note"), _hintStyle);

            switch (_areaViewLayout)
            {
                case 1: AreaDrawZones(); break;
                case 2: AreaDrawWards(); break;
                case 3: AreaDrawPortals(); break;
                case 4: AreaDrawSpawner(); break;
                default: AreaDrawCentreCard(); AreaDrawXfer(); AreaDrawMass(); break;
            }

            GUILayout.EndScrollView();
        }

        private void AreaChip(int index, string locKey)
        {
            var on = GUILayout.Toggle(_areaView == index, Loc.T(locKey), _chipStyleOrButton(), GUILayout.MinWidth(100));
            if (on && _areaView != index) { _areaView = index; _areaScroll = Vector2.zero; }
        }

        // ---- 1a. the shared centre + radius card ----

        private void AreaDrawCentreCard()
        {
            BeginCard(Loc.T("area.centre_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.centre_label"), _labelStyle, GUILayout.MinWidth(60));
            // One label either way - only the text differs, so the control count is identical on both
            // branches. Until the centre is pinned it simply follows the player, which is what the text says.
            GUILayout.Label(_areaCentrePinnedLayout
                    ? Loc.T("area.centre_pinned", AreaPos(_areaCentreLayout))
                    : Loc.T("area.centre_live", AreaPos(_areaCentreLayout)),
                _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("area.use_my_pos"), _buttonStyle, GUILayout.MinWidth(140)))
                AreaPinCentre(true);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.radius_label"), _labelStyle, GUILayout.MinWidth(60));
            _areaRadius = GUILayout.TextField(_areaRadius ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            GUILayout.Label(Loc.T("area.metres", AreaNum(AreaRadius())), _dimCellStyle, GUILayout.Width(90));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("area.centre_hint"), _hintStyle);
            GUILayout.Label(Loc.T("area.radius_hint"), _hintStyle);
            GUILayout.Label(Loc.T("area.errors_hint"), _hintStyle);
            EndCard();
        }

        // ---- 1b. ownership transfer ----

        private void AreaDrawXfer()
        {
            var others = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
            var d = _areaXferLayout;

            BeginCard(Loc.T("area.xfer_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.target_label"), _labelStyle, GUILayout.MinWidth(80));
            // Cycle button: only the LABEL changes, so reading the live target here is control-count safe.
            if (GUILayout.Button(AreaXferTargetName(others), _buttonStyle, GUILayout.MinWidth(170)))
                AreaCycleXferTarget(others);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("area.preview"), _buttonStyle, GUILayout.MinWidth(110)))
                AreaSendXfer(true);
            GUILayout.Space(8);
            // GUI.enabled is a style-level property, not a control, so flipping it from the snapshot is safe.
            var prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && _areaXferArmedLayout;
            if (ConfirmButton("area:xfer", Loc.T("area.xfer_run"), GUILayout.MinWidth(120)))
                AreaSendXfer(false);
            GUI.enabled = prevEnabled;
            GUILayout.EndHorizontal();

            GUILayout.Label(_areaXferArmedLayout ? Loc.T("area.armed") : Loc.T("area.need_preview"), _hintStyle);

            // d == null is not a fault: this card only ever answers a Preview or a Run, so before the first
            // press there is nothing to report and the line says what to press.
            if (d == null) GUILayout.Label(Loc.T("area.xfer_prompt"), _hintStyle);
            else if (d.DryRun) GUILayout.Label(Loc.T("area.xfer_preview_line", d.Matched), _headerStyle);
            else GUILayout.Label(Loc.T("area.xfer_result", d.Matched, d.Changed), _headerStyle);

            // The target list is the roster of OTHER players plus "the server". With nobody else connected
            // the server is the only possible target, which is a fact worth stating rather than leaving the
            // admin to wonder why the cycle button never moves. One label either way - text swap only, and
            // `others` is the Layout-pinned roster, so both passes read the same value.
            GUILayout.Label(Loc.T(others != null && others.Count == 0 ? "area.xfer_hint_solo" : "area.xfer_hint"),
                _hintStyle);
            EndCard();
        }

        // ---- 1c. mass repair / remove ----

        private void AreaDrawMass()
        {
            var d = _areaMassLayout;
            BeginCard(Loc.T("area.mass_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.action_label"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(Loc.T(AreaActionKey(_areaMassAction)), _buttonStyle, GUILayout.MinWidth(120)))
                _areaMassAction = (_areaMassAction + 1) % 2;
            GUILayout.Label(Loc.T("area.filter_label"), _labelStyle, GUILayout.MinWidth(90));
            _areaMassFilter = GUILayout.TextField(_areaMassFilter ?? "", AreaPrefabNameMax, _textFieldStyle, GUILayout.MinWidth(150));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("area.preview"), _buttonStyle, GUILayout.MinWidth(110)))
                AreaSendMass(true);
            GUILayout.Space(8);
            var prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && _areaMassArmedLayout;
            if (ConfirmButton("area:mass", Loc.T("area.mass_run"), GUILayout.MinWidth(120)))
                AreaSendMass(false);
            GUI.enabled = prevEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(_areaMassArmedLayout ? Loc.T("area.armed") : Loc.T("area.need_preview"), _hintStyle);

            // Button-driven like the transfer card: nothing has been asked for until Preview is pressed.
            if (d == null)
            {
                GUILayout.Label(Loc.T("area.mass_prompt"), _hintStyle);
            }
            else
            {
                GUILayout.Label(d.DryRun
                        ? Loc.T("area.mass_preview_line", Loc.T(AreaActionKey(d.Action)), d.Matched)
                        : Loc.T("area.mass_run_line", Loc.T(AreaActionKey(d.Action)), d.Matched, d.Affected),
                    _headerStyle);

                if (d.Rows.Count == 0)
                {
                    GUILayout.Label(Loc.T("area.mass_none"), _hintStyle);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("area.col_prefab"), _headerStyle, GUILayout.Width(260));
                    GUILayout.Label(Loc.T("area.col_count"), _headerStyle, GUILayout.MinWidth(80));
                    GUILayout.EndHorizontal();
                    for (var i = 0; i < d.Rows.Count; i++)
                    {
                        var r = d.Rows[i];
                        GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                        GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(260));
                        GUILayout.Label(r.Count.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.MinWidth(80));
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.Label(Loc.T("area.mass_hint"), _hintStyle);
            GUILayout.Label(Loc.T("area.mass_cap_hint"), _hintStyle);
            EndCard();
        }

        // ---- 2. protection zones ----

        private void AreaDrawZones()
        {
            var d = _areaZonesLayout;

            BeginCard(Loc.T("area.zone_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("area.refresh"), _buttonStyle, GUILayout.MinWidth(100)))
                _areaNextZoneReq = 0f;   // never send inline from the event pass; the next Layout does it
            GUILayout.Space(8);
            // Zones are polled while this view is open, so a null payload really is "asked, nothing back
            // yet" - a transient state that clears itself inside one poll interval.
            GUILayout.Label(d == null ? Loc.T("area.waiting") : Loc.T("area.zone_count", d.Rows.Count), _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (d == null)
            {
                GUILayout.Label(Loc.T("area.zone_waiting"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("area.zone_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("area.col_name"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("area.col_centre"), _headerStyle, GUILayout.Width(160));
                GUILayout.Label(Loc.T("area.col_radius"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("area.col_kind"), _headerStyle, GUILayout.MinWidth(110));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var z = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(z.Name ?? "", _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(AreaPos(z.Pos), _cellStyle, GUILayout.Width(160));
                    GUILayout.Label(Loc.T("area.metres", AreaNum(z.Radius)), _cellStyle, GUILayout.Width(80));
                    GUILayout.Label(Loc.T(AreaKindKey(z.Kind)), _dimCellStyle, GUILayout.MinWidth(110));
                    GUILayout.FlexibleSpace();
                    // Per-row confirm id so arming Delete on one zone cannot arm it on another.
                    if (ConfirmButton("area:zone:" + (z.Name ?? ""), Loc.T("area.delete"), GUILayout.MinWidth(90)))
                        AreaSendZone(z.Name, z.Pos, z.Radius, z.Kind, true);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("area.zone_cap_hint"), _hintStyle);
            EndCard();

            BeginCard(Loc.T("area.zone_add_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.name_label"), _labelStyle, GUILayout.MinWidth(60));
            _areaZoneName = GUILayout.TextField(_areaZoneName ?? "", AreaZoneNameMax, _textFieldStyle, GUILayout.MinWidth(160));
            GUILayout.Label(Loc.T("area.radius_label"), _labelStyle, GUILayout.MinWidth(60));
            _areaZoneRadius = GUILayout.TextField(_areaZoneRadius ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.centre_label"), _labelStyle, GUILayout.MinWidth(60));
            GUILayout.Label(_areaZoneCentrePinnedLayout
                    ? Loc.T("area.centre_pinned", AreaPos(_areaZoneCentreLayout))
                    : Loc.T("area.centre_live", AreaPos(_areaZoneCentreLayout)),
                _labelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("area.use_my_pos"), _buttonStyle, GUILayout.MinWidth(140)))
                AreaPinZoneCentre(true);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.col_kind"), _labelStyle, GUILayout.MinWidth(60));
            if (GUILayout.Button(Loc.T(AreaKindKey(_areaZoneKind)), _buttonStyle, GUILayout.MinWidth(150)))
                _areaZoneKind = (_areaZoneKind + 1) % 3;
            GUILayout.FlexibleSpace();
            // Saving over an existing name replaces that zone, so it takes the two-click treatment too.
            if (ConfirmButton("area:zoneadd", Loc.T("area.zone_add"), GUILayout.MinWidth(120)))
            {
                var name = AreaTrim(_areaZoneName);
                Vector3 zc;
                if (name == null) Message(Loc.T("area.msg_need_name"));
                else if (!AreaTryZoneCentre(out zc)) Message(Loc.T("area.msg_no_player"));
                else AreaSendZone(name, zc, AreaFloat(_areaZoneRadius, AreaRadiusMin, AreaRadiusMax, 16f),
                    _areaZoneKind, false);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("area.zone_hint"), _hintStyle);
            GUILayout.Label(Loc.T("area.zone_enforce_hint"), _hintStyle);
            EndCard();
        }

        // ---- 3. wards ----

        private void AreaDrawWards()
        {
            var d = _areaWardsLayout;
            BeginCard(Loc.T("area.ward_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("area.refresh"), _buttonStyle, GUILayout.MinWidth(100)))
                _areaNextWardReq = 0f;
            GUILayout.Space(8);
            GUILayout.Label(d == null ? Loc.T("area.waiting") : Loc.T("area.showing", d.Rows.Count, d.Total), _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Two distinct states, two distinct lines: the scan is still running (transient), or it came
            // back and this world genuinely has no wards - a perfectly healthy answer, so it leads with the
            // plain fact and keeps the "a scan was already running" note as a second sentence, not an excuse.
            if (d == null)
            {
                GUILayout.Label(Loc.T("area.ward_waiting"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("area.ward_none"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("area.col_pos"), _headerStyle, GUILayout.Width(160));
                GUILayout.Label(Loc.T("area.col_owner"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("area.col_state"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("area.col_access"), _headerStyle, GUILayout.MinWidth(90));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var w = d.Rows[i];
                    var key = AreaPosKey(w.Pos);
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(AreaPos(w.Pos), _cellStyle, GUILayout.Width(160));
                    GUILayout.Label(string.IsNullOrEmpty(w.Owner) ? Loc.T("area.owner_unknown") : w.Owner,
                        _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(Loc.T(w.Enabled ? "area.state_enabled" : "area.state_disabled"),
                        _cellStyle, GUILayout.Width(90));
                    GUILayout.Label(Loc.T(w.Permitted ? "area.yes" : "area.no"), _dimCellStyle, GUILayout.MinWidth(90));
                    GUILayout.FlexibleSpace();
                    // One button whose label swaps with the row's state - the count never changes.
                    if (GUILayout.Button(Loc.T(w.Enabled ? "area.disable" : "area.enable"), _buttonStyle, GUILayout.MinWidth(90)))
                        AreaSendWard(w.Pos, w.Enabled ? 0 : 1);
                    if (ConfirmButton("area:ward:" + key, Loc.T("area.remove"), GUILayout.MinWidth(90)))
                        AreaSendWard(w.Pos, 2);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("area.ward_hint"), _hintStyle);
            GUILayout.Label(Loc.T("area.ward_match_hint"), _hintStyle);
            EndCard();
        }

        // ---- 4. portals ----

        private void AreaDrawPortals()
        {
            var d = _areaPortalsLayout;
            BeginCard(Loc.T("area.portal_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("area.refresh"), _buttonStyle, GUILayout.MinWidth(100)))
                _areaNextPortalReq = 0f;
            GUILayout.Space(8);
            GUILayout.Label(d == null ? Loc.T("area.waiting") : Loc.T("area.showing", d.Rows.Count, d.Total), _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Portals are polled while this view is open, so null is transient; the empty list is already a
            // plain statement of fact ("There are no portals in this world.") and is left as it stands.
            if (d == null)
            {
                GUILayout.Label(Loc.T("area.portal_waiting"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("area.portal_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("area.col_tag"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("area.col_pos"), _headerStyle, GUILayout.Width(160));
                GUILayout.Label(Loc.T("area.col_link"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("area.col_target"), _headerStyle, GUILayout.MinWidth(110));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var p = d.Rows[i];
                    var key = AreaPosKey(p.Pos);
                    string buf;
                    if (!_areaPortalEdit.TryGetValue(key, out buf)) buf = p.Tag ?? "";

                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(string.IsNullOrEmpty(p.Tag) ? Loc.T("area.tag_none") : p.Tag,
                        _cellStyle, GUILayout.Width(110));
                    GUILayout.Label(AreaPos(p.Pos), _cellStyle, GUILayout.Width(160));
                    GUILayout.Label(Loc.T(p.Connected ? "area.link_yes" : "area.link_no"), _cellStyle, GUILayout.Width(110));
                    GUILayout.Label(string.IsNullOrEmpty(p.TargetTag) ? Loc.T("area.tag_none") : p.TargetTag,
                        _dimCellStyle, GUILayout.Width(110));
                    GUILayout.FlexibleSpace();
                    // The vanilla keypad caps a tag at 10 characters and so does the server; enforcing it in
                    // the field means the admin sees what will actually be stored instead of a silent trim.
                    _areaPortalEdit[key] = GUILayout.TextField(buf, AreaTagMax, _textFieldStyle, GUILayout.Width(110));
                    if (GUILayout.Button(Loc.T("area.set_tag"), _buttonStyle, GUILayout.MinWidth(90)))
                        AreaSendPortalTag(p.Pos, _areaPortalEdit[key]);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("area.portal_hint"), _hintStyle);
            GUILayout.Label(Loc.T("area.portal_match_hint"), _hintStyle);
            EndCard();
        }

        // ---- 5. prefab spawner ----

        private void AreaDrawSpawner()
        {
            BeginCard(Loc.T("area.spawn_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.prefab_label"), _labelStyle, GUILayout.MinWidth(70));
            _areaSpawnPrefab = GUILayout.TextField(_areaSpawnPrefab ?? "", AreaPrefabNameMax, _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("area.lookup"), _buttonStyle, GUILayout.MinWidth(100)))
            {
                SrvRpc("AP_SrvLocationListReq", AreaTrim(_areaSpawnPrefab) ?? "");
                Message(Loc.T("area.msg_lookup"));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.centre_label"), _labelStyle, GUILayout.MinWidth(70));
            GUILayout.Label(_areaCentrePinnedLayout
                    ? Loc.T("area.centre_pinned", AreaPos(_areaCentreLayout))
                    : Loc.T("area.centre_live", AreaPos(_areaCentreLayout)),
                _labelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("area.use_my_pos"), _buttonStyle, GUILayout.MinWidth(140)))
                AreaPinCentre(true);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("area.rot_label"), _labelStyle, GUILayout.MinWidth(70));
            _areaSpawnRot = GUILayout.TextField(_areaSpawnRot ?? "", 8, _textFieldStyle, GUILayout.Width(70));
            GUILayout.FlexibleSpace();
            if (ConfirmButton("area:spawn", Loc.T("area.spawn"), GUILayout.MinWidth(120)))
            {
                var name = AreaTrim(_areaSpawnPrefab);
                if (name == null) Message(Loc.T("area.msg_need_prefab"));
                else AreaSendSpawn(name);
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("area.spawn_hint"), _hintStyle);
            EndCard();

            var d = _areaLocationsLayout;
            BeginCard(Loc.T("area.loc_section"));
            // Look-up is button-driven and needs a filter typed first, so null means "nothing asked for".
            if (d == null)
            {
                GUILayout.Label(Loc.T("area.loc_prompt"), _hintStyle);
            }
            else if (d.Names.Count == 0)
            {
                GUILayout.Label(Loc.T("area.loc_empty"), _hintStyle);
            }
            else
            {
                GUILayout.Label(Loc.T("area.showing", d.Names.Count, d.Total), _labelStyle);
                for (var i = 0; i < d.Names.Count; i++)
                {
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(d.Names[i] ?? "", _cellStyle, GUILayout.MinWidth(240));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label(Loc.T("area.loc_hint"), _hintStyle);
            EndCard();
        }

        // ==================== send helpers ====================

        // AP_SrvOwnerXferReq: ZPackage{float x, float y, float z, float radius, long newOwnerUid, bool dryRun}
        private void AreaSendXfer(bool dryRun)
        {
            AreaPinCentre(false);   // a preview and the run after it must mean the same spot
            Vector3 c;
            // Without a pinned centre AND without a player there is no honest point to work from, and
            // (0,0,0) is a perfectly valid world position the server would happily act on - so refuse.
            if (!AreaTryCentre(out c)) { Message(Loc.T("area.msg_no_player")); return; }
            var pkg = new ZPackage();
            pkg.Write(c.x);
            pkg.Write(c.y);
            pkg.Write(c.z);
            pkg.Write(AreaRadius());
            pkg.Write(_areaXferTargetId);
            pkg.Write(dryRun);
            if (dryRun) _areaXferPendingSig = AreaXferSig();
            SrvRpc("AP_SrvOwnerXferReq", pkg);
            Message(Loc.T(dryRun ? "area.msg_preview" : "area.msg_xfer"));
        }

        // AP_SrvMassPieceReq: ZPackage{float x, float y, float z, float radius, int action, bool dryRun,
        //                              string prefabFilter}
        private void AreaSendMass(bool dryRun)
        {
            AreaPinCentre(false);
            Vector3 c;
            if (!AreaTryCentre(out c)) { Message(Loc.T("area.msg_no_player")); return; }
            var pkg = new ZPackage();
            pkg.Write(c.x);
            pkg.Write(c.y);
            pkg.Write(c.z);
            pkg.Write(AreaRadius());
            pkg.Write(_areaMassAction);
            pkg.Write(dryRun);
            pkg.Write(AreaTrim(_areaMassFilter) ?? "");
            if (dryRun) _areaMassPendingSig = AreaMassSig();
            SrvRpc("AP_SrvMassPieceReq", pkg);
            Message(Loc.T(dryRun ? "area.msg_preview"
                : _areaMassAction == 1 ? "area.msg_mass_remove" : "area.msg_mass_repair"));
        }

        // AP_SrvZoneSet: ZPackage{string name, float x, float y, float z, float radius, int kind, bool remove}
        // The server answers a set OR a remove with a fresh AP_ZoneList, so there is nothing to re-request.
        private void AreaSendZone(string name, Vector3 pos, float radius, int kind, bool remove)
        {
            var pkg = new ZPackage();
            pkg.Write(name ?? "");
            pkg.Write(pos.x);
            pkg.Write(pos.y);
            pkg.Write(pos.z);
            pkg.Write(Mathf.Clamp(radius, AreaRadiusMin, AreaRadiusMax));
            pkg.Write(Mathf.Clamp(kind, 0, 2));
            pkg.Write(remove);
            SrvRpc("AP_SrvZoneSet", pkg);
            Message(Loc.T(remove ? "area.msg_zone_removed" : "area.msg_zone_saved", name ?? ""));
        }

        // AP_SrvWardAction: ZPackage{float x, float y, float z, int action (0 disable, 1 enable, 2 remove)}
        // Deliberately does NOT zero the ward throttle: the list comes from a world-wide scan and the server
        // answers a request that arrives while one is already running with an EMPTY list, which would blank
        // the table the admin is clicking in. Refresh is a conscious press.
        private void AreaSendWard(Vector3 pos, int action)
        {
            var pkg = new ZPackage();
            pkg.Write(pos.x);
            pkg.Write(pos.y);
            pkg.Write(pos.z);
            pkg.Write(Mathf.Clamp(action, 0, 2));
            SrvRpc("AP_SrvWardAction", pkg);
            Message(Loc.T("area.msg_ward"));
        }

        // AP_SrvPortalSet: ZPackage{float x, float y, float z, string newTag}
        private void AreaSendPortalTag(Vector3 pos, string tag)
        {
            var clean = (tag ?? "").Trim();
            if (clean.Length > AreaTagMax) clean = clean.Substring(0, AreaTagMax).Trim();
            var pkg = new ZPackage();
            pkg.Write(pos.x);
            pkg.Write(pos.y);
            pkg.Write(pos.z);
            pkg.Write(clean);
            SrvRpc("AP_SrvPortalSet", pkg);
            _areaNextPortalReq = 0f;   // the portal list is a live in-memory list, so a re-read is cheap
            Message(Loc.T("area.msg_tag", clean.Length == 0 ? Loc.T("area.tag_none") : clean));
        }

        // AP_SrvPrefabSpawnReq: ZPackage{string prefabName, float x, float y, float z, float rotY}
        private void AreaSendSpawn(string prefabName)
        {
            Vector3 c;
            if (!AreaTryCentre(out c)) { Message(Loc.T("area.msg_no_player")); return; }
            var pkg = new ZPackage();
            pkg.Write(prefabName);
            pkg.Write(c.x);
            pkg.Write(c.y);
            pkg.Write(c.z);
            pkg.Write(AreaFloat(_areaSpawnRot, -360f, 360f, 0f));
            SrvRpc("AP_SrvPrefabSpawnReq", pkg);
            Message(Loc.T("area.msg_spawn", prefabName));
        }

        // ==================== small helpers ====================

        // The point every Area Edit tool works from: the pinned centre, or the live player position until
        // one is pinned. Vector3.zero when there is no player yet (main menu / loading) - the server's own
        // CenterOk still validates whatever arrives.
        private Vector3 AreaCentre()
        {
            if (_areaCentreSet) return _areaCentre;
            var lp = LocalPlayer;
            return lp != null ? lp.transform.position : Vector3.zero;
        }

        private Vector3 AreaZoneCentre()
        {
            if (_areaZoneCentreSet) return _areaZoneCentre;
            var lp = LocalPlayer;
            return lp != null ? lp.transform.position : Vector3.zero;
        }

        // Same value as AreaCentre / AreaZoneCentre, but says NO instead of quietly answering (0,0,0) when
        // there is neither a pinned point nor a player. Every send path goes through these two.
        private bool AreaTryCentre(out Vector3 centre)
        {
            if (_areaCentreSet) { centre = _areaCentre; return true; }
            var lp = LocalPlayer;
            centre = lp != null ? lp.transform.position : Vector3.zero;
            return lp != null;
        }

        private bool AreaTryZoneCentre(out Vector3 centre)
        {
            if (_areaZoneCentreSet) { centre = _areaZoneCentre; return true; }
            var lp = LocalPlayer;
            centre = lp != null ? lp.transform.position : Vector3.zero;
            return lp != null;
        }

        // Pin the centre where the player stands. Called by the button (announce = true) and by the two
        // request senders (announce = false): pinning before a preview is what keeps the preview signature
        // stable while the admin walks around, so the armed run still means the spot that was previewed.
        private void AreaPinCentre(bool announce)
        {
            var lp = LocalPlayer;
            if (lp == null)
            {
                if (announce) Message(Loc.T("area.msg_no_player"));
                return;
            }
            if (!_areaCentreSet || announce)
            {
                _areaCentre = lp.transform.position;
                _areaCentreSet = true;
                if (announce) Message(Loc.T("area.msg_centre"));
            }
        }

        private void AreaPinZoneCentre(bool announce)
        {
            var lp = LocalPlayer;
            if (lp == null)
            {
                if (announce) Message(Loc.T("area.msg_no_player"));
                return;
            }
            _areaZoneCentre = lp.transform.position;
            _areaZoneCentreSet = true;
            if (announce) Message(Loc.T("area.msg_centre"));
        }

        private float AreaRadius() => AreaFloat(_areaRadius, AreaRadiusMin, AreaRadiusMax, 20f);

        // Identity of the settings a preview belongs to. Changing ANY part of it drops the armed run on the
        // next Layout pass, which is what stops "preview a 5 m repair, then delete a 128 m radius".
        private string AreaCentreSig()
        {
            var c = AreaCentre();
            return c.x.ToString("0.0", CultureInfo.InvariantCulture) + "," +
                   c.y.ToString("0.0", CultureInfo.InvariantCulture) + "," +
                   c.z.ToString("0.0", CultureInfo.InvariantCulture) + "," +
                   AreaRadius().ToString("0.0", CultureInfo.InvariantCulture);
        }

        private string AreaXferSig() =>
            AreaCentreSig() + "|" + _areaXferTargetId.ToString(CultureInfo.InvariantCulture);

        private string AreaMassSig() =>
            AreaCentreSig() + "|" + _areaMassAction.ToString(CultureInfo.InvariantCulture) + "|" +
            (AreaTrim(_areaMassFilter) ?? "");

        private string AreaXferTargetName(List<ZNet.PlayerInfo> others)
        {
            if (_areaXferTargetId == 0L) return Loc.T("area.xfer_server");
            if (others != null)
                foreach (var p in others)
                    if (PeerIdOf(p) == _areaXferTargetId) return p.m_name;
            // The target left while it was selected. The uid still goes on the wire, where the server reads
            // an unknown value as a raw player profile id - so the label says plainly that this is a number.
            return Loc.T("area.xfer_offline", _areaXferTargetId);
        }

        // Cycles "the server" -> each connected player -> back to "the server". A roster entry whose peer id
        // is 0 is skipped: 0 is the wire value that MEANS the server, so it could never be sent as a player.
        private void AreaCycleXferTarget(List<ZNet.PlayerInfo> others)
        {
            var ids = new List<long>();
            if (others != null)
                for (var i = 0; i < others.Count; i++)
                {
                    var id = PeerIdOf(others[i]);
                    if (id != 0L) ids.Add(id);
                }
            if (ids.Count == 0) { _areaXferTargetId = 0L; return; }

            var idx = ids.IndexOf(_areaXferTargetId);
            if (idx < 0) { _areaXferTargetId = ids[0]; return; }             // was the server (or a stale id)
            _areaXferTargetId = idx + 1 >= ids.Count ? 0L : ids[idx + 1];    // past the last one = the server
        }

        // Seeds a tag editor for every portal row that does not have one yet, WITHOUT stomping typing.
        // Layout pass only (it is called from the snapshot block), so the buffer is ready for the same
        // frame's rows. Stale keys are pruned rather than cleared, or a refresh would wipe a half-typed tag.
        private void AreaSeedPortalEdits()
        {
            var d = _areaPortalsLayout;
            if (d == null) return;
            for (var i = 0; i < d.Rows.Count; i++)
            {
                var key = AreaPosKey(d.Rows[i].Pos);
                if (!_areaPortalEdit.ContainsKey(key)) _areaPortalEdit[key] = d.Rows[i].Tag ?? "";
            }
            if (_areaPortalEdit.Count <= AreaPortalRowCap * 2) return;

            var live = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < d.Rows.Count; i++) live.Add(AreaPosKey(d.Rows[i].Pos));
            var dead = new List<string>();
            foreach (var kv in _areaPortalEdit)
                if (!live.Contains(kv.Key)) dead.Add(kv.Key);
            for (var i = 0; i < dead.Count; i++) _areaPortalEdit.Remove(dead[i]);
        }

        private static string AreaActionKey(int action) => action == 1 ? "area.action_remove" : "area.action_repair";

        private static string AreaKindKey(int kind)
        {
            if (kind == 1) return "area.kind_nodamage";
            return kind == 2 ? "area.kind_both" : "area.kind_nobuild";
        }

        // Display coordinates, whole metres, invariant digits so no locale turns "1234.5" into "1.234,5"
        // inside a fixed-width cell.
        private static string AreaPos(Vector3 p) =>
            p.x.ToString("0", CultureInfo.InvariantCulture) + " / " +
            p.y.ToString("0", CultureInfo.InvariantCulture) + " / " +
            p.z.ToString("0", CultureInfo.InvariantCulture);

        // Stable row identity for confirm ids and the tag edit buffer. Decimetre precision: enough to
        // separate two portals in the same building, coarse enough that float noise cannot rename the key.
        private static string AreaPosKey(Vector3 p) =>
            p.x.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
            p.y.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
            p.z.ToString("0.0", CultureInfo.InvariantCulture);

        private static string AreaNum(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        private static string AreaTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        // Parses with InvariantCulture (persisted/wire numbers always are), but accepts a typed comma first
        // so a German admin typing "12,5" gets 12.5 instead of the fallback.
        private static float AreaFloat(string s, float min, float max, float fallback)
        {
            var t = AreaTrim(s);
            if (t == null) return fallback;
            t = t.Replace(',', '.');
            float v;
            if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return fallback;
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return Mathf.Clamp(v, min, max);
        }
    }
}
