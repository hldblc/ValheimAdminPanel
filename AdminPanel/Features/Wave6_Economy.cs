using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 6 - Economy & Events section (Extras tab, client side) ====================
    // Six sub-views behind one chip row: player balances, the shop catalogue, timed events + votes +
    // seasonal toggles, warp points, reserved slots, and the settings export/import pack. Everything here
    // is UI + request plumbing: the server companion owns every piece of truth (the "eco", "shop", "warps",
    // "events", "votes", "slots" store tables) and ships it back in four replies which this file parses
    // defensively and draws from per-frame Layout snapshots.
    //
    // Member prefix: "Eco". Locale prefix: "eco.".
    //
    // WHAT IS AND IS NOT POSSIBLE (surfaced in the hints, not hidden from the admin):
    //   - Balances, shop rows, warps, events, votes, seasonal flags and the slot queue are pure server
    //     state, so they work for every player whether or not they run the mod.
    //   - ACTUALLY HANDING OVER a purchased item does not: character inventory lives in the buying
    //     player's own .fch on THEIR machine, so a purchase can only complete for a player running the
    //     companion DLL (eco.shop_hint says so). For an unmodded or offline buyer the server side is
    //     expected to fall back to the wave-4 offline queue (table "offline_<id>", kind "give", detail
    //     "<prefab>|<count>|<quality>") - Wave4Vault owns that delivery; nothing in this file writes it.
    //   - A queued player sees only the vanilla "server full" message; the mod cannot render a queue
    //     position to a vanilla client (eco.slots_hint says so).
    //
    // IMGUI law observed throughout: the four reply handlers write the live payload fields at ANY time
    // (they run in ZNet.Update) and the chips flip _ecoView during the event pass, so EVERY control-count
    // decision below - which view draws, how many table rows, which empty-state line, vote-active vs
    // vote-form - reads a *Layout snapshot pinned at the top of DrawEconomySection. Text fields bind to
    // live edit state on purpose: a TextField is one control no matter what is inside it. The config-pack
    // import deliberately uses a FIXED set of 12 fields rather than a growable list for the same reason.
    //
    // HOST MODE: on a listen-server host the companion runs in THIS process. SrvRpc reaches it (target 0 is
    // Everybody, which includes the local handler) and its reply is routed straight back to our own session
    // id, which SenderIsServerReply accepts for the host case. So the polls below run on a host exactly like
    // on a remote client - same throttles - and "eco.host_note" only explains where the data comes from.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _ecoSectionCfg;
        private ConfigEntry<int> _ecoPollSecondsCfg;        // balances / shop / slots
        private ConfigEntry<int> _ecoEventPollSecondsCfg;   // events / votes / seasons / warps
        private bool _ecoInited;

        // Wire values - English forever (they ARE the protocol) - and their parallel display-key arrays.
        private static readonly string[] EcoEventKinds = { "bossrush", "treasure", "invasion", "tournament" };
        private static readonly string[] EcoVoteActions = { "skipnight", "kick", "event", "none" };

        private const int EcoPackFields = 12;   // fixed-size import form: control count can never drift

        // ==================== server truth (written by the reply handlers) ====================

        private struct EcoAccountRow { public string Id; public string Name; public long Balance; public long Lifetime; }
        private struct EcoShopRow { public string Sku; public string Prefab; public int Count; public long Price; public bool Buy; }
        private struct EcoSeasonRow { public string Name; public string Kind; public bool Enabled; }
        private struct EcoWarpRow { public string Name; public float X, Y, Z; public bool AdminOnly; }
        private struct EcoQueueRow { public string Id; public int Position; }

        private sealed class EcoData
        {
            public long Total;
            public readonly List<EcoAccountRow> Rows = new List<EcoAccountRow>();
            public readonly List<EcoShopRow> Shop = new List<EcoShopRow>();
            public float ReceivedAt;
        }

        private sealed class EcoEventData
        {
            public string Active = "";
            public long EndsTicks;
            public readonly List<EcoSeasonRow> Seasons = new List<EcoSeasonRow>();
            public bool VoteActive;
            public string VoteTopic = "";
            public int VoteYes, VoteNo;
            public long VoteEndsTicks;
            public readonly List<EcoWarpRow> Warps = new List<EcoWarpRow>();
            public float ReceivedAt;
        }

        private sealed class EcoSlotsInfo
        {
            public bool ReserveOn;
            public int Reserved, MaxPlayers, Online, Queued;
            public readonly List<EcoQueueRow> Rows = new List<EcoQueueRow>();
            public float ReceivedAt;
        }

        private sealed class EcoPackData
        {
            public readonly List<string> Lines = new List<string>();
            public float ReceivedAt;
        }

        private EcoData _ecoData;
        private EcoEventData _ecoEvents;
        private EcoSlotsInfo _ecoSlots;
        private EcoPackData _ecoPack;

        // ==================== Layout snapshots (the ONLY things draw code may read) ====================

        private int _ecoViewLayout;
        private bool _ecoConnectedLayout;
        private bool _ecoHostLayout;
        private EcoData _ecoDataLayout;
        private EcoEventData _ecoEventsLayout;
        private EcoSlotsInfo _ecoSlotsLayout;
        private EcoPackData _ecoPackLayout;

        // ==================== UI / edit state ====================

        private int _ecoView;                 // 0 balances 1 shop 2 events 3 warps 4 slots 5 config pack
        private Vector2 _ecoScroll;
        private Vector2 _ecoPackScroll;

        private string _ecoFilterId = "";
        private string _ecoAdjId = "";
        private string _ecoAdjAmount = "";
        private string _ecoAdjReason = "";

        private string _ecoShopSku = "";
        private string _ecoShopPrefab = "";
        private string _ecoShopCount = "1";
        private string _ecoShopPrice = "10";
        private bool _ecoShopBuy = true;

        private int _ecoEventKind;            // index into EcoEventKinds
        private string _ecoEventMinutes = "30";
        private string _ecoEventParam = "";

        private string _ecoVoteTopic = "";
        private string _ecoVoteMinutes = "5";
        private int _ecoVoteAction;           // index into EcoVoteActions
        private string _ecoVoteTarget = "";   // player id for "kick", event kind for "event"

        private string _ecoWarpName = "";
        private string _ecoWarpX = "";
        private string _ecoWarpY = "";
        private string _ecoWarpZ = "";
        private bool _ecoWarpAdminOnly;

        private readonly string[] _ecoPackIn = new string[EcoPackFields];

        private float _ecoNextEcoReq, _ecoNextEventReq, _ecoNextSlotsReq;   // Time.time based throttles

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies EcoRpcRegistration.
        internal void EcoInit()
        {
            if (_ecoInited) return;
            _ecoInited = true;
            _ecoSectionCfg = Config.Bind("Features", "ShowEconomySection", true,
                "Show the Economy & Events section in the Extras tab (balances, shop, events, votes, warps, reserved slots, settings pack). Client-side UI only - it changes nothing on its own, and every server-side feature it drives is off until an operator turns it on.");
            _ecoPollSecondsCfg = Config.Bind("Features", "EcoPollSeconds", 30,
                new ConfigDescription("How often the panel refreshes balances, the shop catalogue and the slot queue while those views are open, in seconds. Read-only requests.",
                    new AcceptableValueRange<int>(10, 300)));
            _ecoEventPollSecondsCfg = Config.Bind("Features", "EcoEventPollSeconds", 15,
                new ConfigDescription("How often the panel refreshes event, vote, seasonal and warp state while those views are open, in seconds. Read-only request.",
                    new AcceptableValueRange<int>(5, 120)));
        }

        internal bool EcoSectionEnabled() => _ecoSectionCfg == null || _ecoSectionCfg.Value;

        // Called on logout. EVERY per-world field must be cleared here or server A's balances, shop rows and
        // warps render - with live Delete/Adjust buttons - against server B.
        internal void EcoReset()
        {
            _ecoData = null;
            _ecoEvents = null;
            _ecoSlots = null;
            _ecoPack = null;

            _ecoViewLayout = 0;
            _ecoConnectedLayout = false;
            _ecoHostLayout = false;
            _ecoDataLayout = null;
            _ecoEventsLayout = null;
            _ecoSlotsLayout = null;
            _ecoPackLayout = null;

            _ecoView = 0;
            _ecoScroll = Vector2.zero;
            _ecoPackScroll = Vector2.zero;

            _ecoFilterId = "";
            _ecoAdjId = "";
            _ecoAdjAmount = "";
            _ecoAdjReason = "";

            _ecoShopSku = "";
            _ecoShopPrefab = "";
            _ecoShopCount = "1";
            _ecoShopPrice = "10";
            _ecoShopBuy = true;

            _ecoEventKind = 0;
            _ecoEventMinutes = "30";
            _ecoEventParam = "";

            _ecoVoteTopic = "";
            _ecoVoteMinutes = "5";
            _ecoVoteAction = 0;
            _ecoVoteTarget = "";

            _ecoWarpName = "";
            _ecoWarpX = "";
            _ecoWarpY = "";
            _ecoWarpZ = "";
            _ecoWarpAdminOnly = false;

            for (var i = 0; i < _ecoPackIn.Length; i++) _ecoPackIn[i] = "";

            _ecoNextEcoReq = 0f;
            _ecoNextEventReq = 0f;
            _ecoNextSlotsReq = 0f;
        }

        // ==================== reply plumbing ====================

        // Own registration class so no existing file needs an edit. Bound on ZNet.Awake (once per world
        // join) exactly like the main file's RpcRegistration; the glue applies it with CreateAndPatchAll.
        // AP_Msg (the config-pack apply summary) is deliberately NOT registered here - the companion DLL
        // already owns that handler, and registering it twice would double every server message.
        [HarmonyPatch]
        internal static class EcoRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>("AP_EcoData", EcoOnEcoData);
                AdminPanelLocalBridge.Register("AP_EcoData", EcoParseEcoData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_EventState", EcoOnEventState);
                AdminPanelLocalBridge.Register("AP_EventState", EcoParseEventState);
                ZRoutedRpc.instance.Register<ZPackage>("AP_SlotsData", EcoOnSlotsData);
                AdminPanelLocalBridge.Register("AP_SlotsData", EcoParseSlotsData);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ConfigPack", EcoOnConfigPack);
                AdminPanelLocalBridge.Register("AP_ConfigPack", EcoParseConfigPack);
            }
        }

        // All four handlers share the mandated shape: Instance + SenderIsServerReply gate (these payloads
        // claim server authority - without it a hostile client could InvokeRoutedRPC(adminUid, "AP_EcoData",
        // forged) and paint invented balances, a fake shop or bogus warp coordinates into an admin's panel),
        // a payload version gate, bounded counts, and a whole-body try/catch that discards the ENTIRE reply
        // so a truncated packet can never half-apply.

        // AP_EcoData v1: long totalCirculating,
        //   int shipped(<=60) x (string id, string name, long balance, long lifetimeEarned),
        //   int shopShipped(<=40) x (string sku, string prefab, int count, long price, bool buy)
        private static void EcoOnEcoData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            EcoParseEcoData(self, pkg);
        }

        internal static void EcoParseEcoData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new EcoData { Total = pkg.ReadLong(), ReceivedAt = Time.time };
                var n = pkg.ReadInt();
                if (n < 0 || n > 60) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new EcoAccountRow
                    {
                        Id = pkg.ReadString() ?? "",
                        Name = pkg.ReadString() ?? "",
                        Balance = pkg.ReadLong(),
                        Lifetime = pkg.ReadLong()
                    });
                n = pkg.ReadInt();
                if (n < 0 || n > 40) return;
                for (var i = 0; i < n; i++)
                    d.Shop.Add(new EcoShopRow
                    {
                        Sku = pkg.ReadString() ?? "",
                        Prefab = pkg.ReadString() ?? "",
                        Count = pkg.ReadInt(),
                        Price = pkg.ReadLong(),
                        Buy = pkg.ReadBool()
                    });
                self._ecoData = d;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // AP_EventState v1: string activeEvent, long endsTicksUtc,
        //   int shipped(<=20) x (string name, string kind, bool enabled),
        //   bool voteActive, string voteTopic, int voteYes, int voteNo, long voteEndsTicksUtc,
        //   int warpShipped(<=40) x (string name, float x, float y, float z, bool adminOnly)
        private static void EcoOnEventState(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            EcoParseEventState(self, pkg);
        }

        internal static void EcoParseEventState(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new EcoEventData
                {
                    Active = pkg.ReadString() ?? "",
                    EndsTicks = pkg.ReadLong(),
                    ReceivedAt = Time.time
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 20) return;
                for (var i = 0; i < n; i++)
                    d.Seasons.Add(new EcoSeasonRow
                    {
                        Name = pkg.ReadString() ?? "",
                        Kind = pkg.ReadString() ?? "",
                        Enabled = pkg.ReadBool()
                    });
                d.VoteActive = pkg.ReadBool();
                d.VoteTopic = pkg.ReadString() ?? "";
                d.VoteYes = pkg.ReadInt();
                d.VoteNo = pkg.ReadInt();
                d.VoteEndsTicks = pkg.ReadLong();
                n = pkg.ReadInt();
                if (n < 0 || n > 40) return;
                for (var i = 0; i < n; i++)
                    d.Warps.Add(new EcoWarpRow
                    {
                        Name = pkg.ReadString() ?? "",
                        X = EcoSane(pkg.ReadSingle()),
                        Y = EcoSane(pkg.ReadSingle()),
                        Z = EcoSane(pkg.ReadSingle()),
                        AdminOnly = pkg.ReadBool()
                    });
                self._ecoEvents = d;
            }
            catch (Exception) { }
        }

        // AP_SlotsData v1: bool reserveOn, int reserved, int maxPlayers, int online, int queued,
        //   int shipped(<=30) x (string id, int position)
        private static void EcoOnSlotsData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            EcoParseSlotsData(self, pkg);
        }

        internal static void EcoParseSlotsData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new EcoSlotsInfo
                {
                    ReserveOn = pkg.ReadBool(),
                    Reserved = pkg.ReadInt(),
                    MaxPlayers = pkg.ReadInt(),
                    Online = pkg.ReadInt(),
                    Queued = pkg.ReadInt(),
                    ReceivedAt = Time.time
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 30) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new EcoQueueRow { Id = pkg.ReadString() ?? "", Position = pkg.ReadInt() });
                self._ecoSlots = d;
            }
            catch (Exception) { }
        }

        // AP_ConfigPack v1: int shipped(<=200) x (string line)   // "section.key=value"
        private static void EcoOnConfigPack(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            EcoParseConfigPack(self, pkg);
        }

        internal static void EcoParseConfigPack(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new EcoPackData { ReceivedAt = Time.time };
                var n = pkg.ReadInt();
                if (n < 0 || n > 200) return;
                for (var i = 0; i < n; i++) d.Lines.Add(pkg.ReadString() ?? "");
                self._ecoPack = d;
            }
            catch (Exception) { }
        }

        // ==================== polling ====================

        // Called from the Layout block only - it sends RPCs and mutates throttles, so once per frame not
        // once per pass - and throttle-FIRST so a companion with no AP_SrvEcoReq handler can never spin.
        // Only the open view polls; the config pack never polls at all (its export is an explicit button).
        // A host is allowed through: it has no server peer (ServerUid() == 0) but the companion lives in
        // this process, so the request is delivered locally and the reply comes back to our own session id.
        // Every throttle below is unchanged.
        private void EcoPoll()
        {
            if (ZNet.instance == null) return;
            if (!_ecoHostLayout && ServerUid() == 0L) return;
            var money = _ecoPollSecondsCfg != null ? Mathf.Clamp(_ecoPollSecondsCfg.Value, 10, 300) : 30;
            var events = _ecoEventPollSecondsCfg != null ? Mathf.Clamp(_ecoEventPollSecondsCfg.Value, 5, 120) : 15;
            switch (_ecoViewLayout)
            {
                case 0:
                case 1:   // the shop catalogue rides along in AP_EcoData
                    if (Time.time >= _ecoNextEcoReq)
                    {
                        _ecoNextEcoReq = Time.time + money;
                        SrvRpc("AP_SrvEcoReq", EcoTrim(_ecoFilterId) ?? "");
                    }
                    break;
                case 2:
                case 3:   // warps ride along in AP_EventState
                    if (Time.time >= _ecoNextEventReq)
                    {
                        _ecoNextEventReq = Time.time + events;
                        SrvRpc("AP_SrvEventStateReq");
                    }
                    break;
                case 4:
                    if (Time.time >= _ecoNextSlotsReq)
                    {
                        _ecoNextSlotsReq = Time.time + money;
                        SrvRpc("AP_SrvSlotsReq");
                    }
                    break;
            }
        }

        // ==================== drawing ====================

        internal void DrawEconomySection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _ecoViewLayout = _ecoView;
                _ecoConnectedLayout = ZNet.instance != null;
                _ecoHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _ecoDataLayout = _ecoData;
                _ecoEventsLayout = _ecoEvents;
                _ecoSlotsLayout = _ecoSlots;
                _ecoPackLayout = _ecoPack;
                EcoPoll();
            }

            if (!_ecoConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            // Sub-view chips: they WRITE the live field, everything below gates on _ecoViewLayout, so a
            // click can never hand Repaint a different control count than Layout reserved.
            GUILayout.BeginHorizontal();
            EcoChip(0, "eco.view_balances");
            EcoChip(1, "eco.view_shop");
            EcoChip(2, "eco.view_events");
            EcoChip(3, "eco.view_warps");
            EcoChip(4, "eco.view_slots");
            EcoChip(5, "eco.view_pack");
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _ecoScroll = GUILayout.BeginScrollView(_ecoScroll, GUILayout.Height(ListView(200f)));

            // One optional line, and the decision comes from the Layout snapshot - never from live state -
            // so the control count is identical on both passes of a frame.
            if (_ecoHostLayout) GUILayout.Label(Loc.T("eco.host_note"), _hintStyle);

            // Scope line for the three views that only mean something with a playerbase (balances, shop,
            // reserved slots). Warps and events are useful in single player, so they deliberately do not
            // get it. The structural branch reads _ecoViewLayout - a Layout snapshot - so the control count
            // is frozen for the frame; only the TEXT inside it reads the per-frame roster.
            if (_ecoViewLayout == 0 || _ecoViewLayout == 1 || _ecoViewLayout == 4)
                GUILayout.Label(Loc.T(EcoSolo() ? "eco.scope_solo" : "eco.scope_note"), _proseStyle);

            switch (_ecoViewLayout)
            {
                case 1: EcoDrawShop(); EcoDrawShopEditor(); break;
                case 2: EcoDrawActiveEvent(); EcoDrawEventStart(); EcoDrawVote(); EcoDrawSeasons(); break;
                case 3: EcoDrawWarps(); EcoDrawWarpAdd(); break;
                case 4: EcoDrawSlots(); break;
                case 5: EcoDrawPackExport(); EcoDrawPackImport(); break;
                default: EcoDrawBalances(); EcoDrawAdjust(); break;
            }

            GUILayout.EndScrollView();
        }

        // Balances, the shop and the slot queue describe a playerbase. Alone they are ALWAYS empty, and the
        // empty-state text should say that plainly rather than read like a missing reply. _othersSnapshot is
        // the roster of OTHER players, pinned once per frame in DrawWindow's Layout block - read it, never
        // rebuild it - and it only ever picks a string here, never a control count. Null means it has not
        // been pinned yet, which counts as "not alone" so a populated server is never called empty.
        private bool EcoSolo() => _othersSnapshot != null && _othersSnapshot.Count == 0;

        private void EcoChip(int index, string locKey)
        {
            var on = GUILayout.Toggle(_ecoView == index, Loc.T(locKey), _chipStyleOrButton(), GUILayout.MinWidth(100));
            if (on && _ecoView != index) { _ecoView = index; _ecoScroll = Vector2.zero; }
        }

        // ---- 0. balances ----

        private void EcoDrawBalances()
        {
            var d = _ecoDataLayout;
            BeginCard(Loc.T("eco.bal_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.filter_label"), _labelStyle, GUILayout.MinWidth(80));
            _ecoFilterId = GUILayout.TextField(_ecoFilterId ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("eco.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _ecoNextEcoReq = 0f;   // force the next Layout pass to send; never send from the event pass
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Headline + freshness are one label each on every path: the text swaps, the count does not.
            GUILayout.Label(d == null ? Loc.T("eco.no_data") : Loc.T("eco.total", EcoNum(d.Total)), _headerStyle);
            GUILayout.Label(d == null
                    ? Loc.T("eco.poll_hint", _ecoPollSecondsCfg != null ? _ecoPollSecondsCfg.Value : 30)
                    : Loc.T("eco.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - d.ReceivedAt))),
                _dimLabelStyle);

            if (d == null)
            {
                // nothing else to draw - the two labels above already said so
            }
            else if (d.Rows.Count == 0)
            {
                // The server answered and the answer is "no accounts" - a healthy result, not a failure.
                GUILayout.Label(Loc.T(EcoSolo() ? "eco.bal_empty_solo" : "eco.bal_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.col_player"), _headerStyle, GUILayout.Width(130));
                GUILayout.Label(Loc.T("eco.col_id"), _headerStyle, GUILayout.Width(160));
                GUILayout.Label(Loc.T("eco.col_balance"), _headerStyle, GUILayout.Width(90));
                GUILayout.Label(Loc.T("eco.col_lifetime"), _headerStyle, GUILayout.MinWidth(90));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(string.IsNullOrEmpty(r.Name) ? "-" : r.Name, _cellStyle, GUILayout.Width(130));
                    GUILayout.Label(r.Id ?? "", _dimCellStyle, GUILayout.Width(160));
                    GUILayout.Label(EcoNum(r.Balance), _cellStyle, GUILayout.Width(90));
                    GUILayout.Label(EcoNum(r.Lifetime), _dimCellStyle, GUILayout.MinWidth(90));
                    GUILayout.FlexibleSpace();
                    // Plain button: it only copies the row's id into the adjust card below.
                    if (GUILayout.Button(Loc.T("eco.pick"), _buttonStyle, GUILayout.MinWidth(70)))
                        _ecoAdjId = r.Id ?? "";
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("eco.bal_hint"), _hintStyle);
            EndCard();
        }

        private void EcoDrawAdjust()
        {
            BeginCard(Loc.T("eco.adjust_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.id_label"), _labelStyle, GUILayout.MinWidth(80));
            _ecoAdjId = GUILayout.TextField(_ecoAdjId ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.Label(Loc.T("eco.amount_label"), _labelStyle, GUILayout.MinWidth(70));
            _ecoAdjAmount = GUILayout.TextField(_ecoAdjAmount ?? "", _textFieldStyle, GUILayout.Width(90));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.reason_label"), _labelStyle, GUILayout.MinWidth(80));
            _ecoAdjReason = GUILayout.TextField(_ecoAdjReason ?? "", _textFieldStyle, GUILayout.MinWidth(160));
            // Money movement is irreversible from the admin's side (the player can spend it immediately),
            // so all three take the two-click treatment. Distinct ids: arming one disarms the others.
            if (ConfirmButton("eco:grant", Loc.T("eco.grant"), GUILayout.MinWidth(80))) EcoSendAdjust(1);
            if (ConfirmButton("eco:take", Loc.T("eco.take"), GUILayout.MinWidth(80))) EcoSendAdjust(-1);
            if (ConfirmButton("eco:set", Loc.T("eco.set_exact"), GUILayout.MinWidth(100))) EcoSendAdjust(0);
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("eco.adjust_hint"), _hintStyle);
            EndCard();
        }

        // ---- 1. shop ----

        private void EcoDrawShop()
        {
            var d = _ecoDataLayout;
            BeginCard(Loc.T("eco.shop_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("eco.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _ecoNextEcoReq = 0f;
            GUILayout.Space(8);
            GUILayout.Label(d == null
                    ? Loc.T("eco.no_data")
                    : Loc.T("eco.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - d.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (d == null)
            {
                // the status label above is the whole story
            }
            else if (d.Shop.Count == 0)
            {
                GUILayout.Label(Loc.T("eco.shop_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.col_sku"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("eco.col_prefab"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("eco.col_count"), _headerStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("eco.col_price"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("eco.col_mode"), _headerStyle, GUILayout.MinWidth(60));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Shop.Count; i++)
                {
                    var r = d.Shop[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Sku ?? "", _cellStyle, GUILayout.Width(110));
                    GUILayout.Label(r.Prefab ?? "", _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(r.Count.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(60));
                    GUILayout.Label(EcoNum(r.Price), _cellStyle, GUILayout.Width(80));
                    // Plain words, never a coloured glyph: one bad glyph forces the whole panel onto the
                    // fallback font, and the column stays one control wide either way.
                    GUILayout.Label(Loc.T(r.Buy ? "eco.buy" : "eco.sell"), _dimCellStyle, GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("eco.edit"), _buttonStyle, GUILayout.MinWidth(70)))
                    {
                        _ecoShopSku = r.Sku ?? "";
                        _ecoShopPrefab = r.Prefab ?? "";
                        _ecoShopCount = r.Count.ToString(CultureInfo.InvariantCulture);
                        _ecoShopPrice = r.Price.ToString(CultureInfo.InvariantCulture);
                        _ecoShopBuy = r.Buy;
                    }
                    // Per-row confirm id keyed on the SKU, never the list index: a reply landing between
                    // the arming click and the confirming click must not re-point the button.
                    if (ConfirmButton("eco:shopdel:" + (r.Sku ?? ""), Loc.T("eco.delete"), GUILayout.MinWidth(80)))
                        EcoSendShop(r.Sku, r.Prefab, r.Count, r.Price, r.Buy, true);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("eco.shop_hint"), _hintStyle);
            EndCard();
        }

        private void EcoDrawShopEditor()
        {
            BeginCard(Loc.T("eco.shop_edit_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.sku_label"), _labelStyle, GUILayout.MinWidth(60));
            _ecoShopSku = GUILayout.TextField(_ecoShopSku ?? "", _textFieldStyle, GUILayout.MinWidth(110));
            GUILayout.Label(Loc.T("eco.prefab_label"), _labelStyle, GUILayout.MinWidth(60));
            _ecoShopPrefab = GUILayout.TextField(_ecoShopPrefab ?? "", _textFieldStyle, GUILayout.MinWidth(150));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.count_label"), _labelStyle, GUILayout.MinWidth(60));
            _ecoShopCount = GUILayout.TextField(_ecoShopCount ?? "", _textFieldStyle, GUILayout.Width(70));
            GUILayout.Label(Loc.T("eco.price_label"), _labelStyle, GUILayout.MinWidth(60));
            _ecoShopPrice = GUILayout.TextField(_ecoShopPrice ?? "", _textFieldStyle, GUILayout.Width(90));
            // A cycle button, not a dropdown: one control on every pass, and the label is pure text so
            // reading the live flag here cannot change any control count.
            if (GUILayout.Button(Loc.T(_ecoShopBuy ? "eco.buy" : "eco.sell"), _buttonStyle, GUILayout.MinWidth(80)))
                _ecoShopBuy = !_ecoShopBuy;
            if (GUILayout.Button(Loc.T("eco.save"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                var sku = EcoTrim(_ecoShopSku);
                var prefab = EcoTrim(_ecoShopPrefab);
                if (sku == null) Message(Loc.T("eco.msg_need_sku"));
                else if (prefab == null) Message(Loc.T("eco.msg_need_prefab"));
                else
                {
                    var count = (int)EcoWhole(_ecoShopCount, 1L, 9999L, 1L);
                    var price = EcoWhole(_ecoShopPrice, 0L, 1000000000L, 0L);
                    EcoSendShop(sku, prefab, count, price, _ecoShopBuy, false);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("eco.shop_edit_hint"), _hintStyle);
            EndCard();
        }

        // ---- 2. events, votes, seasonal ----

        private void EcoDrawActiveEvent()
        {
            var d = _ecoEventsLayout;
            BeginCard(Loc.T("eco.event_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("eco.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _ecoNextEventReq = 0f;
            GUILayout.Space(8);
            // One status label on every path (no data / nothing running / running), one Stop button on
            // every path: the strings swap, the control count is fixed.
            GUILayout.Label(EcoActiveText(d), _headerStyle);
            GUILayout.FlexibleSpace();
            if (ConfirmButton("eco:stopevent", Loc.T("eco.stop_event"), GUILayout.MinWidth(110)))
            {
                SrvRpc("AP_SrvEventStop");
                Message(Loc.T("eco.msg_event_stop"));
                _ecoNextEventReq = 0f;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("eco.event_hint"), _hintStyle);
            EndCard();
        }

        private void EcoDrawEventStart()
        {
            BeginCard(Loc.T("eco.start_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.kind_label"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(EcoKindLabel(EcoEventKinds[EcoKindIndex()]), _buttonStyle, GUILayout.MinWidth(150)))
                _ecoEventKind = (EcoKindIndex() + 1) % EcoEventKinds.Length;
            GUILayout.Label(Loc.T("eco.minutes_label"), _labelStyle, GUILayout.MinWidth(60));
            _ecoEventMinutes = GUILayout.TextField(_ecoEventMinutes ?? "", _textFieldStyle, GUILayout.Width(70));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.param_label"), _labelStyle, GUILayout.MinWidth(80));
            _ecoEventParam = GUILayout.TextField(_ecoEventParam ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("eco.start_event"), _buttonStyle, GUILayout.MinWidth(120)))
            {
                var kind = EcoEventKinds[EcoKindIndex()];
                var mins = (int)EcoWhole(_ecoEventMinutes, 1L, 10080L, 0L);
                if (mins <= 0) Message(Loc.T("eco.msg_need_minutes"));
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(kind);
                    pkg.Write(mins);
                    pkg.Write(EcoTrim(_ecoEventParam) ?? "");
                    SrvRpc("AP_SrvEventStart", pkg);
                    Message(Loc.T("eco.msg_event_start", EcoKindLabel(kind), mins));
                    _ecoNextEventReq = 0f;
                }
            }
            GUILayout.EndHorizontal();

            // Per-kind hint: one label, only the key changes.
            GUILayout.Label(Loc.T(EcoKindHintKey(EcoEventKinds[EcoKindIndex()])), _hintStyle);
            EndCard();
        }

        private void EcoDrawVote()
        {
            var d = _ecoEventsLayout;
            BeginCard(Loc.T("eco.vote_section"));

            // Active-vote view vs start form is a control-count fork, so it is decided ONLY from the
            // Layout snapshot - a reply landing mid-frame can never swap the branch under Repaint.
            if (d != null && d.VoteActive)
            {
                GUILayout.Label(Loc.T("eco.vote_active",
                        string.IsNullOrEmpty(d.VoteTopic) ? "-" : d.VoteTopic,
                        d.VoteYes, d.VoteNo, EcoRemaining(d.VoteEndsTicks)),
                    _headerStyle);
                GUILayout.Label(Loc.T("eco.vote_wait_hint"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.vote_topic_label"), _labelStyle, GUILayout.MinWidth(80));
                _ecoVoteTopic = GUILayout.TextField(_ecoVoteTopic ?? "", _textFieldStyle, GUILayout.MinWidth(180));
                GUILayout.Label(Loc.T("eco.minutes_label"), _labelStyle, GUILayout.MinWidth(60));
                _ecoVoteMinutes = GUILayout.TextField(_ecoVoteMinutes ?? "", _textFieldStyle, GUILayout.Width(60));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.vote_action_label"), _labelStyle, GUILayout.MinWidth(80));
                if (GUILayout.Button(EcoActionLabel(EcoVoteActions[EcoActionIndex()]), _buttonStyle, GUILayout.MinWidth(140)))
                    _ecoVoteAction = (EcoActionIndex() + 1) % EcoVoteActions.Length;
                GUILayout.Label(Loc.T("eco.vote_target_label"), _labelStyle, GUILayout.MinWidth(60));
                _ecoVoteTarget = GUILayout.TextField(_ecoVoteTarget ?? "", _textFieldStyle, GUILayout.MinWidth(140));
                if (GUILayout.Button(Loc.T("eco.vote_start"), _buttonStyle, GUILayout.MinWidth(100)))
                    EcoSendVote();
                GUILayout.EndHorizontal();

                // A vote asks the other players on the server; alone there is nobody to ask. Text swap only.
                GUILayout.Label(Loc.T(EcoSolo() ? "eco.vote_hint_solo" : "eco.vote_hint"), _hintStyle);
            }

            EndCard();
        }

        private void EcoDrawSeasons()
        {
            var d = _ecoEventsLayout;
            BeginCard(Loc.T("eco.season_section"));

            if (d == null)
            {
                GUILayout.Label(Loc.T("eco.no_data"), _hintStyle);
            }
            else if (d.Seasons.Count == 0)
            {
                GUILayout.Label(Loc.T("eco.season_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.col_name"), _headerStyle, GUILayout.Width(160));
                GUILayout.Label(Loc.T("eco.col_kind"), _headerStyle, GUILayout.Width(130));
                GUILayout.Label(Loc.T("eco.col_state"), _headerStyle, GUILayout.MinWidth(60));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Seasons.Count; i++)
                {
                    var r = d.Seasons[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(160));
                    GUILayout.Label(string.IsNullOrEmpty(r.Kind) ? "-" : r.Kind, _dimCellStyle, GUILayout.Width(130));
                    GUILayout.Label(Loc.T(r.Enabled ? "eco.on" : "eco.off"), _cellStyle, GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    // Label swaps, control count does not. Reversible, so no confirm step.
                    if (GUILayout.Button(Loc.T(r.Enabled ? "eco.disable" : "eco.enable"), _buttonStyle, GUILayout.MinWidth(90)))
                    {
                        var pkg = new ZPackage();
                        pkg.Write(r.Name ?? "");
                        pkg.Write(!r.Enabled);
                        SrvRpc("AP_SrvSeasonSet", pkg);
                        Message(Loc.T("eco.msg_season", r.Name ?? "", Loc.T(r.Enabled ? "eco.off" : "eco.on")));
                        _ecoNextEventReq = 0f;
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("eco.season_hint"), _hintStyle);
            EndCard();
        }

        // ---- 3. warps ----

        private void EcoDrawWarps()
        {
            var d = _ecoEventsLayout;   // warps ship inside AP_EventState
            BeginCard(Loc.T("eco.warp_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("eco.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _ecoNextEventReq = 0f;
            GUILayout.Space(8);
            GUILayout.Label(d == null
                    ? Loc.T("eco.no_data")
                    : Loc.T("eco.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - d.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (d == null)
            {
                // status label above says it
            }
            else if (d.Warps.Count == 0)
            {
                GUILayout.Label(Loc.T("eco.warp_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.col_name"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("eco.col_pos"), _headerStyle, GUILayout.Width(170));
                GUILayout.Label(Loc.T("eco.col_admin"), _headerStyle, GUILayout.MinWidth(80));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Warps.Count; i++)
                {
                    var r = d.Warps[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(EcoPos(r.X, r.Y, r.Z), _cellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.AdminOnly ? Loc.T("eco.yes") : "-", _dimCellStyle, GUILayout.Width(80));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("eco.edit"), _buttonStyle, GUILayout.MinWidth(70)))
                    {
                        _ecoWarpName = r.Name ?? "";
                        _ecoWarpX = r.X.ToString("0.##", CultureInfo.InvariantCulture);
                        _ecoWarpY = r.Y.ToString("0.##", CultureInfo.InvariantCulture);
                        _ecoWarpZ = r.Z.ToString("0.##", CultureInfo.InvariantCulture);
                        _ecoWarpAdminOnly = r.AdminOnly;
                    }
                    if (ConfirmButton("eco:warpdel:" + (r.Name ?? ""), Loc.T("eco.delete"), GUILayout.MinWidth(80)))
                    {
                        EcoSendWarp(r.Name, r.X, r.Y, r.Z, r.AdminOnly, true);
                        Message(Loc.T("eco.msg_warp_deleted", r.Name ?? ""));
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("eco.warp_hint"), _hintStyle);
            EndCard();
        }

        private void EcoDrawWarpAdd()
        {
            BeginCard(Loc.T("eco.warp_add_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("eco.name_label"), _labelStyle, GUILayout.MinWidth(80));
            _ecoWarpName = GUILayout.TextField(_ecoWarpName ?? "", _textFieldStyle, GUILayout.MinWidth(160));
            if (GUILayout.Button(Loc.T("eco.use_my_pos"), _buttonStyle, GUILayout.MinWidth(130)))
            {
                // LocalPlayer is null in the main menu and for a moment during a world load; say so
                // instead of writing zeros into the fields.
                var p = LocalPlayer;
                if (p == null) Message(Loc.T("eco.msg_no_player"));
                else
                {
                    var pos = p.transform.position;
                    _ecoWarpX = pos.x.ToString("0.##", CultureInfo.InvariantCulture);
                    _ecoWarpY = pos.y.ToString("0.##", CultureInfo.InvariantCulture);
                    _ecoWarpZ = pos.z.ToString("0.##", CultureInfo.InvariantCulture);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("X", _labelStyle, GUILayout.MinWidth(20));
            _ecoWarpX = GUILayout.TextField(_ecoWarpX ?? "", _textFieldStyle, GUILayout.Width(90));
            GUILayout.Label("Y", _labelStyle, GUILayout.MinWidth(20));
            _ecoWarpY = GUILayout.TextField(_ecoWarpY ?? "", _textFieldStyle, GUILayout.Width(90));
            GUILayout.Label("Z", _labelStyle, GUILayout.MinWidth(20));
            _ecoWarpZ = GUILayout.TextField(_ecoWarpZ ?? "", _textFieldStyle, GUILayout.Width(90));
            _ecoWarpAdminOnly = GUILayout.Toggle(_ecoWarpAdminOnly, " " + Loc.T("eco.admin_only"), _toggleStyle);
            if (GUILayout.Button(Loc.T("eco.save"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                var name = EcoTrim(_ecoWarpName);
                if (name == null) Message(Loc.T("eco.msg_need_name"));
                else if (!EcoFloat(_ecoWarpX, out var x) || !EcoFloat(_ecoWarpY, out var y) || !EcoFloat(_ecoWarpZ, out var z))
                    Message(Loc.T("eco.msg_need_pos"));
                else
                {
                    EcoSendWarp(name, x, y, z, _ecoWarpAdminOnly, false);
                    Message(Loc.T("eco.msg_warp_saved", name));
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("eco.warp_add_hint"), _hintStyle);
            EndCard();
        }

        // ---- 4. reserved slots ----

        private void EcoDrawSlots()
        {
            var d = _ecoSlotsLayout;
            BeginCard(Loc.T("eco.slots_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("eco.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _ecoNextSlotsReq = 0f;
            GUILayout.Space(8);
            GUILayout.Label(d == null
                    ? Loc.T("eco.no_data")
                    : Loc.T("eco.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - d.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Four fixed labels: every value degrades to a dash when there is no reply yet, so the card
            // never changes shape between "no data" and "data".
            GUILayout.Label(Loc.T("eco.slots_reserve",
                d == null ? "-" : Loc.T(d.ReserveOn ? "eco.on" : "eco.off")), _headerStyle);
            GUILayout.Label(Loc.T("eco.slots_reserved",
                d == null ? "-" : d.Reserved.ToString(CultureInfo.InvariantCulture)), _labelStyle);
            GUILayout.Label(Loc.T("eco.slots_online",
                d == null ? "-" : d.Online.ToString(CultureInfo.InvariantCulture),
                d == null ? "-" : d.MaxPlayers.ToString(CultureInfo.InvariantCulture)), _labelStyle);
            GUILayout.Label(Loc.T("eco.slots_queue",
                d == null ? "-" : d.Queued.ToString(CultureInfo.InvariantCulture)), _labelStyle);

            if (d == null)
            {
                // the four lines above already carry the state
            }
            else if (d.Rows.Count == 0)
            {
                // An empty queue is the normal state of a healthy server; alone it is the only state.
                GUILayout.Label(Loc.T(EcoSolo() ? "eco.queue_empty_solo" : "eco.queue_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("eco.col_position"), _headerStyle, GUILayout.Width(80));
                GUILayout.Label(Loc.T("eco.col_id"), _headerStyle, GUILayout.MinWidth(200));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Position.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(80));
                    GUILayout.Label(r.Id ?? "", _dimCellStyle, GUILayout.MinWidth(200));
                    GUILayout.EndHorizontal();
                }
            }

            // eco.slots_hint (still in the locale files, kept so no translation is invalidated) named a
            // config key the companion does not have. The real switches are Features.EnableReservedSlots
            // and Features.ReservedSlots, so this card points at those instead.
            GUILayout.Label(Loc.T("eco.slots_hint2"), _hintStyle);
            EndCard();
        }

        // ---- 5. config pack ----

        private void EcoDrawPackExport()
        {
            var d = _ecoPackLayout;
            BeginCard(Loc.T("eco.pack_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("eco.export"), _buttonStyle, GUILayout.MinWidth(100)))
            {
                SrvRpc("AP_SrvConfigPackReq");
                Message(Loc.T("eco.msg_export"));
            }
            GUILayout.Space(8);
            GUILayout.Label(d == null ? Loc.T("eco.pack_empty") : Loc.T("eco.pack_lines", d.Lines.Count), _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Inner scroll inside the section scroll: its height is capped so the outer scroll stays
            // passive (no double scrollbar) even with the full 200-line export.
            _ecoPackScroll = GUILayout.BeginScrollView(_ecoPackScroll,
                GUILayout.Height(Mathf.Min(240f, ListView(360f))));
            if (d != null)
                for (var i = 0; i < d.Lines.Count; i++)
                    GUILayout.Label(d.Lines[i] ?? "", i % 2 == 0 ? _cellStyle : _dimCellStyle, GUILayout.MinWidth(360));
            GUILayout.EndScrollView();

            GUILayout.Label(Loc.T("eco.pack_hint"), _hintStyle);
            EndCard();
        }

        private void EcoDrawPackImport()
        {
            BeginCard(Loc.T("eco.import_section"));

            // FIXED 12 rows, always drawn: a growable list would change the control count the moment a
            // line was added during the event pass. Empty rows are skipped when the payload is built.
            for (var i = 0; i < EcoPackFields; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1).ToString(CultureInfo.InvariantCulture), _dimCellStyle, GUILayout.Width(24));
                _ecoPackIn[i] = GUILayout.TextField(_ecoPackIn[i] ?? "", _textFieldStyle, GUILayout.MinWidth(340));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("eco.preview"), _buttonStyle, GUILayout.MinWidth(100)))
                EcoSendPack(true);
            // Apply rewrites live server settings, so it takes the two-click treatment.
            if (ConfirmButton("eco:packapply", Loc.T("eco.apply"), GUILayout.MinWidth(100)))
                EcoSendPack(false);
            if (GUILayout.Button(Loc.T("eco.clear"), _buttonStyle, GUILayout.MinWidth(90)))
                for (var i = 0; i < _ecoPackIn.Length; i++) _ecoPackIn[i] = "";
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("eco.import_hint"), _hintStyle);
            EndCard();
        }

        // ==================== send helpers ====================

        // mode: 1 grant, -1 take, 0 set exact.
        private void EcoSendAdjust(int mode)
        {
            var id = EcoTrim(_ecoAdjId);
            if (id == null) { Message(Loc.T("eco.msg_need_id")); return; }
            if (!long.TryParse(EcoTrim(_ecoAdjAmount) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            { Message(Loc.T("eco.msg_need_amount")); return; }
            // Clamp both ways: a fat-fingered 20-digit value must not wrap the server's ledger arithmetic.
            if (amount > 1000000000000L) amount = 1000000000000L;
            if (amount < -1000000000000L) amount = -1000000000000L;
            if (mode != 0 && amount < 0L) amount = -amount;   // Take takes a positive number

            var set = mode == 0;
            var delta = set ? amount : (mode > 0 ? amount : -amount);
            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write(delta);
            pkg.Write(set);
            pkg.Write(EcoTrim(_ecoAdjReason) ?? "");
            SrvRpc("AP_SrvEcoAdjust", pkg);
            Message(Loc.T(set ? "eco.msg_set" : (mode > 0 ? "eco.msg_grant" : "eco.msg_take"),
                EcoNum(set ? amount : Math.Abs(amount)), id));
            _ecoNextEcoReq = 0f;
        }

        private void EcoSendShop(string sku, string prefab, int count, long price, bool buy, bool remove)
        {
            var s = EcoTrim(sku);
            if (s == null) { Message(Loc.T("eco.msg_need_sku")); return; }
            var pkg = new ZPackage();
            pkg.Write(s);
            pkg.Write(EcoTrim(prefab) ?? "");
            pkg.Write(count);
            pkg.Write(price);
            pkg.Write(buy);
            pkg.Write(remove);
            SrvRpc("AP_SrvShopSet", pkg);
            Message(Loc.T(remove ? "eco.msg_shop_deleted" : "eco.msg_shop_saved", s));
            _ecoNextEcoReq = 0f;
        }

        private void EcoSendWarp(string name, float x, float y, float z, bool adminOnly, bool remove)
        {
            var n = EcoTrim(name);
            if (n == null) { Message(Loc.T("eco.msg_need_name")); return; }
            var pkg = new ZPackage();
            pkg.Write(n);
            pkg.Write(EcoSane(x));
            pkg.Write(EcoSane(y));
            pkg.Write(EcoSane(z));
            pkg.Write(adminOnly);
            pkg.Write(remove);
            SrvRpc("AP_SrvWarpSet", pkg);
            _ecoNextEventReq = 0f;
        }

        // Wire action strings: "skipnight" | "kick:<id>" | "event:<kind>" | "none".
        private void EcoSendVote()
        {
            var topic = EcoTrim(_ecoVoteTopic);
            if (topic == null) { Message(Loc.T("eco.msg_need_topic")); return; }
            var mins = (int)EcoWhole(_ecoVoteMinutes, 1L, 1440L, 0L);
            if (mins <= 0) { Message(Loc.T("eco.msg_need_minutes")); return; }

            var kind = EcoVoteActions[EcoActionIndex()];
            string action;
            switch (kind)
            {
                case "kick":
                {
                    var target = EcoTrim(_ecoVoteTarget);
                    if (target == null) { Message(Loc.T("eco.msg_need_target")); return; }
                    action = "kick:" + target;
                    break;
                }
                case "event":
                {
                    // The event kind must be one the protocol knows; anything else would start nothing.
                    var target = EcoTrim(_ecoVoteTarget);
                    if (target == null) { Message(Loc.T("eco.msg_need_target")); return; }
                    var known = false;
                    foreach (var k in EcoEventKinds)
                        if (string.Equals(k, target, StringComparison.OrdinalIgnoreCase)) { known = true; target = k; break; }
                    if (!known) { Message(Loc.T("eco.msg_bad_kind")); return; }
                    action = "event:" + target;
                    break;
                }
                case "none":
                    action = "none";
                    break;
                default:
                    action = "skipnight";
                    break;
            }

            var pkg = new ZPackage();
            pkg.Write(topic);
            pkg.Write(mins);
            pkg.Write(action);
            SrvRpc("AP_SrvVoteStart", pkg);
            Message(Loc.T("eco.msg_vote_start", topic));
            _ecoNextEventReq = 0f;
        }

        // The reply is a plain AP_Msg text summary, which the companion's own client handler shows - this
        // file registers no handler for it (see EcoRpcRegistration).
        private void EcoSendPack(bool dryRun)
        {
            var lines = new List<string>();
            for (var i = 0; i < _ecoPackIn.Length; i++)
            {
                var t = EcoTrim(_ecoPackIn[i]);
                if (t == null || t[0] == '#') continue;
                if (t.IndexOf('=') <= 0) continue;   // "section.key=value" or it is not a setting
                lines.Add(t);
            }
            if (lines.Count == 0) { Message(Loc.T("eco.msg_need_lines")); return; }

            var pkg = new ZPackage();
            pkg.Write(lines.Count);
            for (var i = 0; i < lines.Count; i++) pkg.Write(lines[i]);
            pkg.Write(dryRun);
            SrvRpc("AP_SrvConfigPackApply", pkg);
            Message(Loc.T(dryRun ? "eco.msg_preview" : "eco.msg_apply", lines.Count));
        }

        // ==================== small helpers ====================

        // Config/reset can never put these indices out of range, but a future edit could; clamping here
        // keeps every read of the wire arrays inside the array.
        private int EcoKindIndex()
        {
            if (_ecoEventKind < 0 || _ecoEventKind >= EcoEventKinds.Length) _ecoEventKind = 0;
            return _ecoEventKind;
        }

        private int EcoActionIndex()
        {
            if (_ecoVoteAction < 0 || _ecoVoteAction >= EcoVoteActions.Length) _ecoVoteAction = 0;
            return _ecoVoteAction;
        }

        // Wire value -> display text. An unknown kind (a newer companion) renders raw rather than blank.
        private static string EcoKindLabel(string kind)
        {
            switch (kind)
            {
                case "bossrush": return Loc.T("eco.kind_bossrush");
                case "treasure": return Loc.T("eco.kind_treasure");
                case "invasion": return Loc.T("eco.kind_invasion");
                case "tournament": return Loc.T("eco.kind_tournament");
                default: return string.IsNullOrEmpty(kind) ? "?" : kind;
            }
        }

        private static string EcoKindHintKey(string kind)
        {
            switch (kind)
            {
                case "treasure": return "eco.hint_treasure";
                case "invasion": return "eco.hint_invasion";
                case "tournament": return "eco.hint_tournament";
                default: return "eco.hint_bossrush";
            }
        }

        private static string EcoActionLabel(string action)
        {
            switch (action)
            {
                case "kick": return Loc.T("eco.act_kick");
                case "event": return Loc.T("eco.act_event");
                case "none": return Loc.T("eco.act_none");
                default: return Loc.T("eco.act_skipnight");
            }
        }

        private string EcoActiveText(EcoEventData d)
        {
            if (d == null) return Loc.T("eco.no_data");
            if (string.IsNullOrEmpty(d.Active)) return Loc.T("eco.event_none");
            return Loc.T("eco.event_running", EcoKindLabel(d.Active), EcoRemaining(d.EndsTicks));
        }

        private static string EcoTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        // Whole-number field parse with clamping. Returns fallback when the text is not a number, so the
        // caller can tell "empty/garbage" (fallback) from a real value.
        private static long EcoWhole(string s, long min, long max, long fallback)
        {
            if (!long.TryParse(EcoTrim(s) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return fallback;
            if (v < min) return min;
            return v > max ? max : v;
        }

        private static bool EcoFloat(string s, out float value)
        {
            value = 0f;
            if (!float.TryParse(EcoTrim(s) ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            value = EcoSane(v);
            return true;
        }

        private static float EcoSane(float f) =>
            float.IsNaN(f) || float.IsInfinity(f) ? 0f : Mathf.Clamp(f, -1000000f, 1000000f);

        // Digits and separators only, invariant: no locale can mangle a money column.
        private static string EcoNum(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        private static string EcoPos(float x, float y, float z) =>
            x.ToString("0", CultureInfo.InvariantCulture) + ", " +
            y.ToString("0", CultureInfo.InvariantCulture) + ", " +
            z.ToString("0", CultureInfo.InvariantCulture);

        // Remaining time from a server-stamped DateTime.UtcNow.Ticks deadline. Client and server clocks can
        // drift a little, so this is a display approximation - the server is what actually ends the event.
        private static string EcoRemaining(long endsTicksUtc)
        {
            if (endsTicksUtc <= 0L || endsTicksUtc > DateTime.MaxValue.Ticks) return "-";
            var delta = endsTicksUtc - DateTime.UtcNow.Ticks;
            if (delta <= 0L) return Loc.T("eco.expired");
            var mins = delta / TimeSpan.TicksPerMinute;
            if (mins < 60L) return Loc.T("eco.mins", Math.Max(1L, mins));
            if (mins < 1440L) return Loc.T("eco.hours", mins / 60L);
            return Loc.T("eco.days", mins / 1440L);
        }
    }
}
