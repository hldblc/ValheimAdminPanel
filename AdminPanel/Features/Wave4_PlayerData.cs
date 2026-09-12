using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 4 - Player Data section (Extras tab, client side) ====================
    // Character vault (snapshot / restore / delete), the offline action queue, the death log, the player
    // ledger, the item audit and the rescue lane (online un-stuck, offline rescue, player reset).
    // Everything here is UI + request plumbing: the server companion owns every
    // piece of truth and ships it back in one reply per topic, which this file parses defensively and draws
    // from per-frame Layout snapshots.
    //
    // Member prefix: "Pdat". Locale prefix: "pdat.".
    //
    // CAPABILITY TIERS (surfaced to the admin in the "what can and cannot be done" card, and the reason the
    // vault cards read the way they do):
    //   TIER-VANILLA  - death-point teleports, the ledger and the death log: the server owns that data or
    //                   performs the move itself, so they work for every player, modded or not.
    //   TIER-MODDED   - snapshots, restores and the item audit: character data (inventory, skills) lives in
    //                   the player's own .fch file on THEIR machine, so the server has to ask that client.
    //                   Only a client running AdminPanelCompanion answers; everyone else silently ignores it.
    //   TIER-IMPOSSIBLE - touching an OFFLINE player's character. The file is not on the server at all, so
    //                   the offline view is a QUEUE that the companion applies on that player's next join.
    //                   The UI says so in plain words rather than pretending an offline edit happened.
    //
    // IMGUI law observed throughout (see Features\FeaturesCore.cs and the Player-tab chip pattern): reply
    // handlers write the live payload fields at ANY time (they run in ZNet.Update) and the sub-view chips
    // flip _pdatView during the event pass, so EVERY control-count decision below - which view is drawn, how
    // many table rows, which empty-state line - reads a *Layout snapshot pinned at the top of
    // DrawPlayerDataSection. Text fields bind to live edit state on purpose: a TextField is one control no
    // matter what is inside it.
    //
    // HOST MODE: on a listen-server host the companion runs in THIS process, so a reply is routed back to
    // our own session id instead of arriving from a server peer. SenderIsServerReply accepts that one case,
    // so every table below now fills in on a host exactly like it does on a remote client, and the poll only
    // refuses when there is no world (or, on a remote client, no server peer yet). ServerUid() is
    // structurally 0 on a host - SrvRpc already handles that by sending to target 0, which the in-process
    // companion picks up - so it must NOT be used as a "can I ask?" test here.
    //
    // What is still genuinely impossible on a host, and what "pdat.host_note" says out loud: the companion
    // resolves a snapshot / rescue TARGET with FindPeerByUid, and the host's own character has no peer entry,
    // so it can never be the target of either lane. The item audit counts the host's own inventory locally
    // (Wave4SrvPlayerData.OnItemAuditReq), so that one does work for a single-player host.
    public partial class AdminPanelPlugin
    {
        // ---- config ----
        private ConfigEntry<bool> _pdatSectionCfg;
        private ConfigEntry<int> _pdatLedgerSecCfg;
        private bool _pdatInited;

        // Wire values for the offline queue. English forever - they are the protocol's "kind" strings and
        // double as the state key of the cycle button; only the DISPLAY text is localized (PdatKindLabel).
        private static readonly string[] PdatKinds = { "give", "strip", "tp", "kit" };

        // ==================== server truth (written by the reply handlers) ====================

        private struct PdatSnap { public long Ticks; public string Label; public int ItemCount; public string Source; }
        private struct PdatQueued { public long Ticks; public string Kind; public string Detail; }
        private struct PdatDeath { public long Ticks; public string Name; public string Id; public float X, Y, Z; public bool HasTomb; }
        private struct PdatLedgerRow { public string Id; public string LastName; public long First; public long Last; public int Sessions; public long TotalSeconds; }
        private struct PdatAuditRow { public string Name; public string Id; public int Count; }

        private sealed class PdatVaultData
        {
            public string Id = "";
            public readonly List<PdatSnap> Rows = new List<PdatSnap>();
        }

        private sealed class PdatQueueData
        {
            public string Id = "";
            public readonly List<PdatQueued> Rows = new List<PdatQueued>();
        }

        private sealed class PdatDeathData
        {
            public readonly List<PdatDeath> Rows = new List<PdatDeath>();
        }

        private sealed class PdatLedgerData
        {
            public readonly List<PdatLedgerRow> Rows = new List<PdatLedgerRow>();
            public float ReceivedAt;
        }

        private sealed class PdatAuditData
        {
            public string Prefab = "";
            public int Scanned;
            public int Answered;
            public readonly List<PdatAuditRow> Rows = new List<PdatAuditRow>();
        }

        private PdatVaultData _pdatVault;
        private PdatQueueData _pdatQueue;
        private PdatDeathData _pdatDeaths;
        private PdatLedgerData _pdatLedger;
        private PdatAuditData _pdatAudit;

        // ==================== Layout snapshots (the ONLY things draw code may read) ====================

        private int _pdatViewLayout;
        private bool _pdatConnectedLayout;
        private bool _pdatHostLayout;
        private bool _pdatWipeFirstLayout;
        private PdatVaultData _pdatVaultLayout;
        private PdatQueueData _pdatQueueLayout;
        private PdatDeathData _pdatDeathsLayout;
        private PdatLedgerData _pdatLedgerLayout;
        private PdatAuditData _pdatAuditLayout;

        // ==================== UI / edit state ====================

        private int _pdatView;                  // 0 vault 1 queue 2 deaths 3 ledger 4 item audit 5 rescue
        private Vector2 _pdatScroll;

        private string _pdatVaultId = "";
        private bool _pdatWipeFirst;
        private long _pdatSnapTargetId;         // stable peer uid, 0 = nobody (never a roster index)
        private string _pdatSnapLabel = "";

        private string _pdatQueueId = "";
        private int _pdatAddKind;               // index into PdatKinds
        private string _pdatAddDetail = "";

        private string _pdatDeathFilter = "";
        private string _pdatPrefab = "";

        private long _pdatRescueTargetId;       // stable peer uid, 0 = nobody (never a roster index)
        private int _pdatRescueMode;            // 0 nudge, 1 world spawn, 2 safe ground - the wire's own mode ints
        private string _pdatRescueOfflineId = "";
        private string _pdatResetId = "";

        private float _pdatNextLedgerReq;       // ledger poll throttle (Time.time based)

        // ==================== lifecycle ====================

        // Config binds only. The glue file registers the FeatureSection and applies PdatRpcRegistration.
        internal void PdatInit()
        {
            if (_pdatInited) return;
            _pdatInited = true;
            _pdatSectionCfg = Config.Bind("Features", "ShowPlayerDataSection", true,
                "Show the Player Data section in the Extras tab (character vault, offline action queue, death log, player ledger, item audit). Client-side UI only - nothing here runs on its own.");
            _pdatLedgerSecCfg = Config.Bind("Features", "PlayerLedgerPollSeconds", 60,
                new ConfigDescription("How often the panel refreshes the player ledger while that view is open, in seconds. Read-only request.",
                    new AcceptableValueRange<int>(15, 600)));
        }

        internal bool PdatSectionEnabled() => _pdatSectionCfg == null || _pdatSectionCfg.Value;

        // Called on logout. EVERY per-world field must be cleared here or server A's snapshot list renders -
        // with live Restore/Delete buttons - against server B.
        internal void PdatReset()
        {
            _pdatVault = null;
            _pdatQueue = null;
            _pdatDeaths = null;
            _pdatLedger = null;
            _pdatAudit = null;

            _pdatViewLayout = 0;
            _pdatConnectedLayout = false;
            _pdatHostLayout = false;
            _pdatWipeFirstLayout = false;
            _pdatVaultLayout = null;
            _pdatQueueLayout = null;
            _pdatDeathsLayout = null;
            _pdatLedgerLayout = null;
            _pdatAuditLayout = null;

            _pdatView = 0;
            _pdatScroll = Vector2.zero;

            _pdatVaultId = "";
            _pdatWipeFirst = false;
            _pdatSnapTargetId = 0L;
            _pdatSnapLabel = "";

            _pdatQueueId = "";
            _pdatAddKind = 0;
            _pdatAddDetail = "";

            _pdatDeathFilter = "";
            _pdatPrefab = "";

            _pdatRescueTargetId = 0L;
            _pdatRescueMode = 0;
            _pdatRescueOfflineId = "";
            _pdatResetId = "";

            _pdatNextLedgerReq = 0f;
        }

        // ==================== reply plumbing ====================

        // Own registration class so no existing file needs an edit. Bound on ZNet.Awake (once per world
        // join) exactly like the main file's RpcRegistration; the glue applies it with CreateAndPatchAll.
        [HarmonyPatch]
        internal static class PdatRpcRegistration
        {
            [HarmonyPatch(typeof(ZNet), "Awake")]
            [HarmonyPostfix]
            private static void ZNetAwakePostfix()
            {
                if (ZRoutedRpc.instance == null) return;
                // Both halves side by side for every name: the network handler (gate + parse) and the parse
                // half the in-process bridge calls on a listen-server host, where the companion runs in this
                // very process and there is no server peer for the gate to authenticate against.
                ZRoutedRpc.instance.Register<ZPackage>("AP_VaultList", PdatOnVaultList);
                AdminPanelLocalBridge.Register("AP_VaultList", PdatParseVaultList);
                ZRoutedRpc.instance.Register<ZPackage>("AP_OfflineQueue", PdatOnOfflineQueue);
                AdminPanelLocalBridge.Register("AP_OfflineQueue", PdatParseOfflineQueue);
                ZRoutedRpc.instance.Register<ZPackage>("AP_DeathLog", PdatOnDeathLog);
                AdminPanelLocalBridge.Register("AP_DeathLog", PdatParseDeathLog);
                ZRoutedRpc.instance.Register<ZPackage>("AP_Ledger", PdatOnLedger);
                AdminPanelLocalBridge.Register("AP_Ledger", PdatParseLedger);
                ZRoutedRpc.instance.Register<ZPackage>("AP_ItemAudit", PdatOnItemAudit);
                AdminPanelLocalBridge.Register("AP_ItemAudit", PdatParseItemAudit);
            }
        }

        // All five handlers share the mandated shape: Instance + SenderIsServerReply gate (these payloads
        // claim server authority - without it a hostile client could InvokeRoutedRPC(adminUid, "AP_Ledger",
        // forged) and paint invented playtime, fake death points or a fake snapshot list into an admin's
        // panel), a payload version gate, bounded counts, and a whole-body try/catch that discards the
        // ENTIRE reply so a truncated packet can never half-apply.
        //
        // Each one is split in two: the gated network handler, and a PdatParse* half that the in-process
        // bridge calls on a listen-server host. Both paths run the SAME parsing code, so the local delivery
        // can never interpret a payload differently from the network one.

        // AP_VaultList v1: string id, int shipped(<=30) x (long snapTicksUtc, string label, int itemCount, string source)
        private static void PdatOnVaultList(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            PdatParseVaultList(self, pkg);
        }

        internal static void PdatParseVaultList(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new PdatVaultData { Id = pkg.ReadString() ?? "" };
                var n = pkg.ReadInt();
                if (n < 0 || n > 30) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new PdatSnap
                    {
                        Ticks = pkg.ReadLong(),
                        Label = pkg.ReadString() ?? "",
                        ItemCount = pkg.ReadInt(),
                        Source = pkg.ReadString() ?? ""
                    });
                self._pdatVault = d;
            }
            catch (Exception) { /* malformed/truncated reply - keep whatever we had */ }
        }

        // AP_OfflineQueue v1: string id, int shipped(<=30) x (long queuedTicksUtc, string kind, string detail)
        private static void PdatOnOfflineQueue(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            PdatParseOfflineQueue(self, pkg);
        }

        internal static void PdatParseOfflineQueue(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new PdatQueueData { Id = pkg.ReadString() ?? "" };
                var n = pkg.ReadInt();
                if (n < 0 || n > 30) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new PdatQueued
                    {
                        Ticks = pkg.ReadLong(),
                        Kind = pkg.ReadString() ?? "",
                        Detail = pkg.ReadString() ?? ""
                    });
                self._pdatQueue = d;
            }
            catch (Exception) { }
        }

        // AP_DeathLog v1: int shipped(<=60) x (long ticksUtc, string playerName, string id, float x, float y, float z, bool hasTombstone)
        private static void PdatOnDeathLog(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            PdatParseDeathLog(self, pkg);
        }

        internal static void PdatParseDeathLog(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new PdatDeathData();
                var n = pkg.ReadInt();
                if (n < 0 || n > 60) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new PdatDeath
                    {
                        Ticks = pkg.ReadLong(),
                        Name = pkg.ReadString() ?? "",
                        Id = pkg.ReadString() ?? "",
                        X = PdatSane(pkg.ReadSingle()),
                        Y = PdatSane(pkg.ReadSingle()),
                        Z = PdatSane(pkg.ReadSingle()),
                        HasTomb = pkg.ReadBool()
                    });
                self._pdatDeaths = d;
            }
            catch (Exception) { }
        }

        // AP_Ledger v1: int shipped(<=60) x (string id, string lastName, long firstTicks, long lastTicks, int sessions, long totalSeconds)
        private static void PdatOnLedger(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            PdatParseLedger(self, pkg);
        }

        internal static void PdatParseLedger(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new PdatLedgerData { ReceivedAt = Time.time };
                var n = pkg.ReadInt();
                if (n < 0 || n > 60) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new PdatLedgerRow
                    {
                        Id = pkg.ReadString() ?? "",
                        LastName = pkg.ReadString() ?? "",
                        First = pkg.ReadLong(),
                        Last = pkg.ReadLong(),
                        Sessions = pkg.ReadInt(),
                        TotalSeconds = pkg.ReadLong()
                    });
                self._pdatLedger = d;
            }
            catch (Exception) { }
        }

        // AP_ItemAudit v1: string prefab, int scanned, int answered, int shipped(<=60) x (string playerName, string id, int count)
        private static void PdatOnItemAudit(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            PdatParseItemAudit(self, pkg);
        }

        internal static void PdatParseItemAudit(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;
                var d = new PdatAuditData
                {
                    Prefab = pkg.ReadString() ?? "",
                    Scanned = pkg.ReadInt(),
                    Answered = pkg.ReadInt()
                };
                var n = pkg.ReadInt();
                if (n < 0 || n > 60) return;
                for (var i = 0; i < n; i++)
                    d.Rows.Add(new PdatAuditRow
                    {
                        Name = pkg.ReadString() ?? "",
                        Id = pkg.ReadString() ?? "",
                        Count = pkg.ReadInt()
                    });
                self._pdatAudit = d;
            }
            catch (Exception) { }
        }

        // ==================== polling ====================

        // The ledger is the only periodic read here (everything else is request/response on a button). Called
        // from the Layout block only - it sends an RPC and mutates a throttle, so once per frame not once per
        // pass - and throttle-FIRST so a companion without AP_SrvLedgerReq can never loop.
        //
        // The gate is "am I in a world", NOT "am I a client": a host has no server peer, so ServerUid() is 0
        // there and would have blocked every request. Only a REMOTE client with no server peer yet has
        // genuinely nowhere to send. Throttle values are untouched.
        private void PdatPoll()
        {
            if (ZNet.instance == null) return;
            if (!_pdatHostLayout && ServerUid() == 0L) return;
            if (_pdatViewLayout != 3) return;
            if (Time.time < _pdatNextLedgerReq) return;
            var every = _pdatLedgerSecCfg != null ? Mathf.Clamp(_pdatLedgerSecCfg.Value, 15, 600) : 60;
            _pdatNextLedgerReq = Time.time + every;
            SrvRpc("AP_SrvLedgerReq");
        }

        // ==================== drawing ====================

        internal void DrawPlayerDataSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _pdatViewLayout = _pdatView;
                _pdatConnectedLayout = ZNet.instance != null;
                _pdatHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _pdatWipeFirstLayout = _pdatWipeFirst;
                _pdatVaultLayout = _pdatVault;
                _pdatQueueLayout = _pdatQueue;
                _pdatDeathsLayout = _pdatDeaths;
                _pdatLedgerLayout = _pdatLedger;
                _pdatAuditLayout = _pdatAudit;
                PdatPoll();
            }

            if (!_pdatConnectedLayout)
            {
                GUILayout.Label(Loc.T("players.not_connected"), _labelStyle);
                return;
            }

            // Sub-view chips: they WRITE the live field, everything below gates on _pdatViewLayout, so a
            // click can never hand Repaint a different control count than Layout reserved.
            GUILayout.BeginHorizontal();
            PdatChip(0, "pdat.view_vault");
            PdatChip(1, "pdat.view_queue");
            PdatChip(2, "pdat.view_deaths");
            PdatChip(3, "pdat.view_ledger");
            PdatChip(4, "pdat.view_audit");
            PdatChip(5, "pdat.view_rescue");
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _pdatScroll = GUILayout.BeginScrollView(_pdatScroll, GUILayout.Height(ListView(200f)));

            // Host-only note. _pdatHostLayout is a Layout snapshot and nothing else writes it, so this label
            // is present or absent for the WHOLE frame - Repaint always sees the count Layout reserved.
            if (_pdatHostLayout) GUILayout.Label(Loc.T("pdat.host_note"), _hintStyle);

            switch (_pdatViewLayout)
            {
                case 1: PdatDrawQueue(); PdatDrawQueueAdd(); break;
                case 2: PdatDrawDeaths(); break;
                case 3: PdatDrawLedger(); break;
                case 4: PdatDrawAudit(); break;
                case 5: PdatDrawRescue(); PdatDrawOfflineRescue(); PdatDrawReset(); break;
                default: PdatDrawTiers(); PdatDrawVault(); PdatDrawSnapshot(); break;
            }

            GUILayout.EndScrollView();
        }

        private void PdatChip(int index, string locKey)
        {
            var on = GUILayout.Toggle(_pdatView == index, Loc.T(locKey), _chipStyleOrButton(), GUILayout.MinWidth(110));
            if (on && _pdatView != index) { _pdatView = index; _pdatScroll = Vector2.zero; }
        }

        // ---- 0a. the capability-tier explainer (deliberately the first thing in the section) ----

        private void PdatDrawTiers()
        {
            BeginCard(Loc.T("pdat.tier_section"));
            GUILayout.Label(Loc.T("pdat.tier_modded"), _proseStyle);
            GUILayout.Space(4);
            GUILayout.Label(Loc.T("pdat.tier_vanilla"), _proseStyle);
            GUILayout.Space(4);
            GUILayout.Label(Loc.T("pdat.tier_offline"), _proseStyle);
            EndCard();
        }

        // ---- 0b. profile & vault ----

        private void PdatDrawVault()
        {
            var d = _pdatVaultLayout;
            BeginCard(Loc.T("pdat.vault_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.id_label"), _labelStyle, GUILayout.MinWidth(80));
            _pdatVaultId = GUILayout.TextField(_pdatVaultId ?? "", _textFieldStyle, GUILayout.MinWidth(190));
            if (GUILayout.Button(Loc.T("pdat.load"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                var id = PdatTrim(_pdatVaultId);
                if (id == null) Message(Loc.T("pdat.msg_need_id"));
                else SrvRpc("AP_SrvVaultListReq", id);
            }
            GUILayout.EndHorizontal();

            // The wipe-first switch is pinned on Layout and the pinned value is what a Restore click sends,
            // so the flag that was on screen when the button was armed is the flag that travels. It is also
            // baked into the confirm id below: flipping it disarms an armed Restore, on purpose.
            _pdatWipeFirst = GUILayout.Toggle(_pdatWipeFirst, " " + Loc.T("pdat.wipe_first"), _toggleStyle);
            GUILayout.Label(Loc.T("pdat.wipe_hint"), _hintStyle);

            // Exactly one status/empty label on every path - the text changes, the control count does not.
            // d == null is NOT a fault here: this card is button-driven, so nothing has been asked for yet.
            if (d == null)
            {
                GUILayout.Label(Loc.T("pdat.vault_prompt"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("pdat.vault_empty"), _hintStyle);
            }
            else
            {
                GUILayout.Label(Loc.T("pdat.showing", d.Id ?? ""), _dimLabelStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("pdat.col_when"), _headerStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("pdat.col_label"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("pdat.col_items"), _headerStyle, GUILayout.Width(60));
                GUILayout.Label(Loc.T("pdat.col_source"), _headerStyle, GUILayout.MinWidth(80));
                GUILayout.EndHorizontal();

                var wipe = _pdatWipeFirstLayout;
                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    var tick = r.Ticks.ToString(CultureInfo.InvariantCulture);
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(PdatWhen(r.Ticks), _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(r.Label ?? "", _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(r.ItemCount.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(60));
                    GUILayout.Label(PdatSourceLabel(r.Source), _dimCellStyle, GUILayout.Width(80));
                    GUILayout.FlexibleSpace();
                    // Per-row ids keyed on the snapshot's own timestamp, never on the list index: a reply
                    // landing between the arming click and the confirming click must not re-point them.
                    if (ConfirmButton("pdat:restore:" + tick + (wipe ? ":w" : ":n"),
                            Loc.T("pdat.restore"), GUILayout.MinWidth(90)))
                        PdatSendRestore(d.Id, r.Ticks, wipe);
                    if (ConfirmButton("pdat:delete:" + tick, Loc.T("pdat.delete"), GUILayout.MinWidth(80)))
                        PdatSendDelete(d.Id, r.Ticks);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("pdat.vault_hint"), _hintStyle);
            EndCard();
        }

        // Snapshotting reads the TARGET's live character, which only their own client can do, so the picker
        // cycles the online roster by peer uid (ZNet.PlayerInfo exposes no platform id client-side).
        private void PdatDrawSnapshot()
        {
            var others = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
            BeginCard(Loc.T("pdat.snap_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.target"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(PdatSnapTargetName(others), _buttonStyle, GUILayout.MinWidth(150)))
                PdatCycleSnapTarget(others);
            GUILayout.Label(Loc.T("pdat.label_label"), _labelStyle, GUILayout.MinWidth(60));
            _pdatSnapLabel = GUILayout.TextField(_pdatSnapLabel ?? "", _textFieldStyle, GUILayout.MinWidth(140));
            if (GUILayout.Button(Loc.T("pdat.snap"), _buttonStyle, GUILayout.MinWidth(100)))
            {
                if (_pdatSnapTargetId == 0L) Message(Loc.T("pdat.msg_no_target"));
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(_pdatSnapTargetId);
                    pkg.Write(PdatTrim(_pdatSnapLabel) ?? "");
                    SrvRpc("AP_SrvVaultSnapReq", pkg);
                    Message(Loc.T("pdat.msg_snap", PdatSnapTargetName(others)));
                }
            }
            GUILayout.EndHorizontal();

            // Snapshotting is structurally a multiplayer lane - it reads ANOTHER player's live character on
            // their own client. With nobody else connected the picker is empty by design, so say that in
            // plain words instead of leaving a hint that implies something is broken. Text swap only: one
            // label either way, and PdatNobodyElse reads a Layout-pinned roster, so both passes agree.
            GUILayout.Label(Loc.T(PdatNobodyElse() ? "pdat.snap_hint_solo" : "pdat.snap_hint"), _hintStyle);
            EndCard();
        }

        // ---- 1. offline queue ----

        private void PdatDrawQueue()
        {
            var d = _pdatQueueLayout;
            BeginCard(Loc.T("pdat.queue_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.id_label"), _labelStyle, GUILayout.MinWidth(80));
            _pdatQueueId = GUILayout.TextField(_pdatQueueId ?? "", _textFieldStyle, GUILayout.MinWidth(190));
            if (GUILayout.Button(Loc.T("pdat.load"), _buttonStyle, GUILayout.MinWidth(80)))
            {
                var id = PdatTrim(_pdatQueueId);
                if (id == null) Message(Loc.T("pdat.msg_need_id"));
                else SrvRpc("AP_SrvOfflineQueueReq", id);
            }
            // Ticks 0 is the wire's "clear everything for this id" value.
            if (ConfirmButton("pdat:clearall", Loc.T("pdat.clear_all"), GUILayout.MinWidth(100)))
            {
                var id = PdatTrim(_pdatQueueId);
                if (id == null) Message(Loc.T("pdat.msg_need_id"));
                else
                {
                    PdatSendClear(id, 0L);
                    Message(Loc.T("pdat.msg_clear_all", id));
                }
            }
            GUILayout.EndHorizontal();

            // Button-driven like the vault: nothing has been requested until Load is pressed.
            if (d == null)
            {
                GUILayout.Label(Loc.T("pdat.queue_prompt"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("pdat.queue_empty"), _hintStyle);
            }
            else
            {
                GUILayout.Label(Loc.T("pdat.showing", d.Id ?? ""), _dimLabelStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("pdat.col_when"), _headerStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("pdat.col_kind"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("pdat.col_detail"), _headerStyle, GUILayout.MinWidth(180));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(PdatWhen(r.Ticks), _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(PdatKindLabel(r.Kind), _cellStyle, GUILayout.Width(110));
                    GUILayout.Label(r.Detail ?? "", _dimCellStyle, GUILayout.MinWidth(180));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("pdat.cancel"), _buttonStyle, GUILayout.MinWidth(80)))
                    {
                        PdatSendClear(d.Id, r.Ticks);
                        Message(Loc.T("pdat.msg_cancel"));
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("pdat.queue_hint"), _hintStyle);
            EndCard();
        }

        private void PdatDrawQueueAdd()
        {
            BeginCard(Loc.T("pdat.add_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.col_kind"), _labelStyle, GUILayout.MinWidth(80));
            // A cycle button, not a dropdown: one control on every pass, and the label is pure text so
            // reading the live index here cannot change any control count.
            if (GUILayout.Button(PdatKindLabel(PdatKinds[PdatKindIndex()]), _buttonStyle, GUILayout.MinWidth(150)))
                _pdatAddKind = (PdatKindIndex() + 1) % PdatKinds.Length;
            GUILayout.Label(Loc.T("pdat.detail_label"), _labelStyle, GUILayout.MinWidth(60));
            _pdatAddDetail = GUILayout.TextField(_pdatAddDetail ?? "", _textFieldStyle, GUILayout.MinWidth(180));
            if (GUILayout.Button(Loc.T("pdat.add"), _buttonStyle, GUILayout.MinWidth(100)))
            {
                var id = PdatTrim(_pdatQueueId);
                if (id == null) Message(Loc.T("pdat.msg_need_id"));
                else
                {
                    var kind = PdatKinds[PdatKindIndex()];
                    var pkg = new ZPackage();
                    pkg.Write(id);
                    pkg.Write(kind);
                    pkg.Write(PdatTrim(_pdatAddDetail) ?? "");
                    SrvRpc("AP_SrvOfflineAdd", pkg);
                    Message(Loc.T("pdat.msg_queued", PdatKindLabel(kind), id));
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T(PdatKindHintKey(PdatKinds[PdatKindIndex()])), _hintStyle);
            EndCard();
        }

        // ---- 2. deaths ----

        private void PdatDrawDeaths()
        {
            var d = _pdatDeathsLayout;
            BeginCard(Loc.T("pdat.deaths_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.filter_label"), _labelStyle, GUILayout.MinWidth(80));
            _pdatDeathFilter = GUILayout.TextField(_pdatDeathFilter ?? "", _textFieldStyle, GUILayout.MinWidth(190));
            if (GUILayout.Button(Loc.T("pdat.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                SrvRpc("AP_SrvDeathLogReq", PdatTrim(_pdatDeathFilter) ?? "");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // The death log has no poll - it is read on the Refresh button - so a null payload means
            // "not asked for yet", never "the companion is silent".
            if (d == null)
            {
                GUILayout.Label(Loc.T("pdat.deaths_prompt"), _hintStyle);
            }
            else if (d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("pdat.deaths_empty"), _hintStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("pdat.col_when"), _headerStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("pdat.col_player"), _headerStyle, GUILayout.Width(110));
                GUILayout.Label(Loc.T("pdat.col_pos"), _headerStyle, GUILayout.Width(140));
                GUILayout.Label(Loc.T("pdat.col_grave"), _headerStyle, GUILayout.MinWidth(80));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(PdatWhen(r.Ticks), _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(110));
                    GUILayout.Label(PdatPos(r.X, r.Y, r.Z), _cellStyle, GUILayout.Width(140));
                    // Plain word, no colour and no glyph: one bad glyph forces the whole panel onto a
                    // fallback font, and the dash keeps the column one control wide either way.
                    GUILayout.Label(r.HasTomb ? Loc.T("pdat.tombstone") : "-", _dimCellStyle, GUILayout.Width(80));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("pdat.tp_there"), _buttonStyle, GUILayout.MinWidth(140)))
                        PdatSendGraveTp(r);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("pdat.deaths_hint"), _hintStyle);
            EndCard();
        }

        // ---- 3. ledger ----

        private void PdatDrawLedger()
        {
            var d = _pdatLedgerLayout;
            BeginCard(Loc.T("pdat.ledger_section"));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("pdat.refresh"), _buttonStyle, GUILayout.MinWidth(90)))
                _pdatNextLedgerReq = 0f;   // force the next Layout pass to send; never send from the event pass
            GUILayout.Space(8);
            // The ledger IS polled while this view is open, so a null payload here is the one genuine
            // "asked, nothing back yet" state in this section - it clears itself within a poll interval.
            GUILayout.Label(d == null
                    ? Loc.T("pdat.ledger_waiting")
                    : Loc.T("pdat.age", Mathf.Max(0, Mathf.RoundToInt(Time.time - d.ReceivedAt))),
                _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (d != null && d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T("pdat.ledger_empty"), _hintStyle);
            }
            else if (d != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("pdat.col_player"), _headerStyle, GUILayout.Width(120));
                GUILayout.Label(Loc.T("pdat.col_id"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("pdat.col_first"), _headerStyle, GUILayout.Width(105));
                GUILayout.Label(Loc.T("pdat.col_last"), _headerStyle, GUILayout.Width(105));
                GUILayout.Label(Loc.T("pdat.col_sessions"), _headerStyle, GUILayout.Width(70));
                GUILayout.Label(Loc.T("pdat.col_hours"), _headerStyle, GUILayout.MinWidth(60));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.LastName ?? "", _cellStyle, GUILayout.Width(120));
                    GUILayout.Label(r.Id ?? "", _dimCellStyle, GUILayout.Width(150));
                    GUILayout.Label(PdatWhen(r.First), _cellStyle, GUILayout.Width(105));
                    GUILayout.Label(PdatWhen(r.Last), _cellStyle, GUILayout.Width(105));
                    GUILayout.Label(r.Sessions.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.Width(70));
                    GUILayout.Label(PdatHours(r.TotalSeconds), _cellStyle, GUILayout.MinWidth(60));
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("pdat.ledger_hint"), _hintStyle);
            EndCard();
        }

        // ---- 4. item audit ----

        private void PdatDrawAudit()
        {
            var d = _pdatAuditLayout;
            BeginCard(Loc.T("pdat.audit_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.prefab_label"), _labelStyle, GUILayout.MinWidth(80));
            _pdatPrefab = GUILayout.TextField(_pdatPrefab ?? "", _textFieldStyle, GUILayout.MinWidth(190));
            if (GUILayout.Button(Loc.T("pdat.scan"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                var p = PdatTrim(_pdatPrefab);
                if (p == null) Message(Loc.T("pdat.msg_need_prefab"));
                else { SrvRpc("AP_SrvItemAuditReq", p); Message(Loc.T("pdat.msg_scan", p)); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // The answered/scanned line is the headline number: it is how an admin sees that half the server
            // simply cannot answer, instead of reading "0 of them have the item".
            GUILayout.Label(d == null
                    ? Loc.T("pdat.audit_prompt")
                    : Loc.T("pdat.audit_answered", d.Answered, d.Scanned),
                _headerStyle);

            // Solo the companion counts the admin's OWN inventory locally (Wave4SrvPlayerData.OnItemAuditReq
            // scans Player.m_localPlayer on a host), so an empty result means "you are not carrying it" -
            // not "nobody answered". Text swap only, so the control count is identical on both branches.
            if (d != null && d.Rows.Count == 0)
            {
                GUILayout.Label(Loc.T(PdatNobodyElse() ? "pdat.audit_empty_solo" : "pdat.audit_empty"), _hintStyle);
            }
            else if (d != null)
            {
                GUILayout.Label(Loc.T("pdat.showing", d.Prefab ?? ""), _dimLabelStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("pdat.col_player"), _headerStyle, GUILayout.Width(150));
                GUILayout.Label(Loc.T("pdat.col_id"), _headerStyle, GUILayout.Width(170));
                GUILayout.Label(Loc.T("pdat.col_count"), _headerStyle, GUILayout.MinWidth(70));
                GUILayout.EndHorizontal();

                for (var i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(r.Name ?? "", _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(r.Id ?? "", _dimCellStyle, GUILayout.Width(170));
                    GUILayout.Label(r.Count.ToString(CultureInfo.InvariantCulture), _cellStyle, GUILayout.MinWidth(70));
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Label(Loc.T("pdat.audit_hint"), _hintStyle);
            EndCard();
        }

        // ---- 5. rescue / offline rescue / player reset ----

        // Three one-shot requests: no poll, no reply payload, nothing to snapshot. The target uid and the
        // mode index are read live on purpose - both only ever choose LABEL TEXT here, and a label whose text
        // changes is still exactly one control on both passes.
        //
        // AP_SrvRescueReq {long targetUid, int mode}: mode 0 nudges +3 m, 1 sends them to the world spawn,
        // 2 puts them on the server's generated surface at their own X/Z. The server performs it with the
        // vanilla RPC_TeleportPlayer that every client registers, so the TARGET needs no mod - the one
        // TIER-VANILLA lane in this view, and the hint has to say so or an admin will assume otherwise.
        private void PdatDrawRescue()
        {
            var others = _othersSnapshot ?? (_othersSnapshot = OtherPlayers());
            BeginCard(Loc.T("pdat.rescue_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.target"), _labelStyle, GUILayout.MinWidth(80));
            if (GUILayout.Button(PdatRescueTargetName(others), _buttonStyle, GUILayout.MinWidth(150)))
                PdatCycleRescueTarget(others);
            GUILayout.Label(Loc.T("pdat.mode_label"), _labelStyle, GUILayout.MinWidth(60));
            if (GUILayout.Button(Loc.T(PdatRescueModeKey(PdatRescueModeIndex())), _buttonStyle, GUILayout.MinWidth(140)))
                _pdatRescueMode = (PdatRescueModeIndex() + 1) % 3;
            if (GUILayout.Button(Loc.T("pdat.rescue"), _buttonStyle, GUILayout.MinWidth(100)))
            {
                if (_pdatRescueTargetId == 0L) Message(Loc.T("pdat.msg_no_target"));
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(_pdatRescueTargetId);
                    pkg.Write(PdatRescueModeIndex());
                    SrvRpc("AP_SrvRescueReq", pkg);
                    Message(Loc.T("pdat.msg_rescue", PdatRescueTargetName(others)));
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T(PdatRescueModeHintKey(PdatRescueModeIndex())), _hintStyle);
            // Online rescue moves ANOTHER connected player, so with nobody else on the server the picker is
            // empty by design. Say that plainly rather than leaving a hint that reads like a broken tool.
            GUILayout.Label(Loc.T(PdatNobodyElse() ? "pdat.rescue_hint_solo" : "pdat.rescue_hint"), _hintStyle);
            EndCard();
        }

        // AP_SrvRescueOffline {string id, int mode, float x, float y, float z}. Mode 1 (world spawn, resolved
        // server-side at queue time) is the only mode that means anything for a player who is not connected:
        // the server has no position for them, so "nudge" and "safe ground near them" have no origin. The
        // coordinates travel as zeroes because mode 1 ignores them - the field order is still mandatory.
        private void PdatDrawOfflineRescue()
        {
            BeginCard(Loc.T("pdat.roffline_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.id_label"), _labelStyle, GUILayout.MinWidth(80));
            _pdatRescueOfflineId = GUILayout.TextField(_pdatRescueOfflineId ?? "", _textFieldStyle, GUILayout.MinWidth(190));
            if (ConfirmButton("pdat:roffline", Loc.T("pdat.queue_rescue"), GUILayout.MinWidth(120)))
            {
                var id = PdatTrim(_pdatRescueOfflineId);
                if (id == null) Message(Loc.T("pdat.msg_need_id"));
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(id);
                    pkg.Write(1);
                    pkg.Write(0f); pkg.Write(0f); pkg.Write(0f);
                    SrvRpc("AP_SrvRescueOffline", pkg);
                    Message(Loc.T("pdat.msg_roffline", id));
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("pdat.roffline_hint"), _hintStyle);
            EndCard();
        }

        // AP_SrvPlayerReset {string id, long targetUid, string reason}. uid 0 means "resolve the target from
        // the platform id yourself": the server finds the online peer, or falls back to its own reset queue
        // when the player is offline or has no companion. The reason travels empty and the server stamps its
        // own default, so nothing free-typed here can reach a '|'-delimited store row.
        private void PdatDrawReset()
        {
            BeginCard(Loc.T("pdat.reset_section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("pdat.id_label"), _labelStyle, GUILayout.MinWidth(80));
            _pdatResetId = GUILayout.TextField(_pdatResetId ?? "", _textFieldStyle, GUILayout.MinWidth(190));
            if (ConfirmButton("pdat:reset", Loc.T("pdat.reset"), GUILayout.MinWidth(120)))
            {
                var id = PdatTrim(_pdatResetId);
                if (id == null) Message(Loc.T("pdat.msg_need_id"));
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(id);
                    pkg.Write(0L);
                    pkg.Write("");
                    SrvRpc("AP_SrvPlayerReset", pkg);
                    Message(Loc.T("pdat.msg_reset", id));
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("pdat.reset_warn"), _proseStyle);
            EndCard();
        }

        // ==================== send helpers ====================

        private void PdatSendRestore(string id, long ticks, bool wipeFirst)
        {
            var target = PdatTrim(id) ?? PdatTrim(_pdatVaultId);
            if (target == null) { Message(Loc.T("pdat.msg_need_id")); return; }
            var pkg = new ZPackage();
            pkg.Write(target);
            pkg.Write(ticks);
            pkg.Write(wipeFirst);
            SrvRpc("AP_SrvVaultRestoreReq", pkg);
            Message(Loc.T("pdat.msg_restore", target));
        }

        private void PdatSendDelete(string id, long ticks)
        {
            var target = PdatTrim(id) ?? PdatTrim(_pdatVaultId);
            if (target == null) { Message(Loc.T("pdat.msg_need_id")); return; }
            var pkg = new ZPackage();
            pkg.Write(target);
            pkg.Write(ticks);
            SrvRpc("AP_SrvVaultDeleteReq", pkg);
            Message(Loc.T("pdat.msg_delete", target));
        }

        private void PdatSendClear(string id, long ticks)
        {
            var target = PdatTrim(id) ?? PdatTrim(_pdatQueueId);
            if (target == null) { Message(Loc.T("pdat.msg_need_id")); return; }
            var pkg = new ZPackage();
            pkg.Write(target);
            pkg.Write(ticks);
            SrvRpc("AP_SrvOfflineClear", pkg);
        }

        // Teleports the LOCAL admin to a recorded death point (TIER-VANILLA: the server moves whoever the
        // uid names, no mod needed on anyone else's side). SelfUid resolves this session's own peer uid.
        private void PdatSendGraveTp(PdatDeath r)
        {
            var uid = SelfUid();
            if (uid == 0L) { Message(Loc.T("pdat.msg_no_self")); return; }
            var pkg = new ZPackage();
            pkg.Write(uid);
            pkg.Write(r.X);
            pkg.Write(r.Y);
            pkg.Write(r.Z);
            SrvRpc("AP_SrvGraveTpReq", pkg);
            Message(Loc.T("pdat.msg_tp", string.IsNullOrEmpty(r.Name) ? "?" : r.Name));
        }

        // ==================== small helpers ====================

        // "Is there anybody else on this server?" - the honest test behind every multiplayer-only empty
        // state in this section. It READS the per-frame roster DrawWindow pins on its Layout pass and never
        // rebuilds it, so the answer is identical on Layout and Repaint of the same frame. A null roster
        // (only possible outside a drawn frame) falls back to the multiplayer wording, which is never wrong,
        // only less specific. Every caller uses it to pick a label's TEXT - never to add or drop a control.
        private bool PdatNobodyElse()
        {
            var others = _othersSnapshot;
            return others != null && others.Count == 0;
        }

        private string PdatSnapTargetName(List<ZNet.PlayerInfo> others)
        {
            if (_pdatSnapTargetId == 0L) return Loc.T("common.nobody");
            if (others != null)
                foreach (var p in others)
                    if (PeerIdOf(p) == _pdatSnapTargetId) return p.m_name;
            return Loc.T("common.nobody");   // target left the game; the send path still rejects on id 0
        }

        private void PdatCycleSnapTarget(List<ZNet.PlayerInfo> others)
        {
            if (others == null || others.Count == 0) { _pdatSnapTargetId = 0L; return; }
            var idx = -1;
            for (var i = 0; i < others.Count; i++)
                if (PeerIdOf(others[i]) == _pdatSnapTargetId) { idx = i; break; }
            idx = (idx + 1) % others.Count;   // -1 (nobody) advances to the first entry
            _pdatSnapTargetId = PeerIdOf(others[idx]);
        }

        private string PdatRescueTargetName(List<ZNet.PlayerInfo> others)
        {
            if (_pdatRescueTargetId == 0L) return Loc.T("common.nobody");
            if (others != null)
                foreach (var p in others)
                    if (PeerIdOf(p) == _pdatRescueTargetId) return p.m_name;
            return Loc.T("common.nobody");   // target left the game; the send path still rejects on id 0
        }

        private void PdatCycleRescueTarget(List<ZNet.PlayerInfo> others)
        {
            if (others == null || others.Count == 0) { _pdatRescueTargetId = 0L; return; }
            var idx = -1;
            for (var i = 0; i < others.Count; i++)
                if (PeerIdOf(others[i]) == _pdatRescueTargetId) { idx = i; break; }
            idx = (idx + 1) % others.Count;   // -1 (nobody) advances to the first entry
            _pdatRescueTargetId = PeerIdOf(others[idx]);
        }

        // The mode int IS the wire value, so it must stay inside 0-2 whatever a future edit does to the cycle.
        private int PdatRescueModeIndex()
        {
            if (_pdatRescueMode < 0 || _pdatRescueMode > 2) _pdatRescueMode = 0;
            return _pdatRescueMode;
        }

        private static string PdatRescueModeKey(int mode)
        {
            switch (mode)
            {
                case 1: return "pdat.rmode_spawn";
                case 2: return "pdat.rmode_ground";
                default: return "pdat.rmode_nudge";
            }
        }

        private static string PdatRescueModeHintKey(int mode)
        {
            switch (mode)
            {
                case 1: return "pdat.rhint_spawn";
                case 2: return "pdat.rhint_ground";
                default: return "pdat.rhint_nudge";
            }
        }

        // Config/reset can never put the index out of range, but a future edit could; clamping here keeps
        // every read of PdatKinds inside the array.
        private int PdatKindIndex()
        {
            if (_pdatAddKind < 0 || _pdatAddKind >= PdatKinds.Length) _pdatAddKind = 0;
            return _pdatAddKind;
        }

        // Wire value -> display text. Unknown kinds (a newer companion) render raw rather than blank.
        private static string PdatKindLabel(string kind)
        {
            switch (kind)
            {
                case "give": return Loc.T("pdat.kind_give");
                case "strip": return Loc.T("pdat.kind_strip");
                case "tp": return Loc.T("pdat.kind_tp");
                case "kit": return Loc.T("pdat.kind_kit");
                default: return string.IsNullOrEmpty(kind) ? "?" : kind;
            }
        }

        private static string PdatKindHintKey(string kind)
        {
            switch (kind)
            {
                case "strip": return "pdat.hint_strip";
                case "tp": return "pdat.hint_tp";
                case "kit": return "pdat.hint_kit";
                default: return "pdat.hint_give";
            }
        }

        private static string PdatSourceLabel(string source)
        {
            switch (source)
            {
                case "auto": return Loc.T("pdat.src_auto");
                case "manual": return Loc.T("pdat.src_manual");
                case "death": return Loc.T("pdat.src_death");
                default: return string.IsNullOrEmpty(source) ? "?" : source;
            }
        }

        private static string PdatTrim(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        private static float PdatSane(float f) =>
            float.IsNaN(f) || float.IsInfinity(f) ? 0f : Mathf.Clamp(f, -1000000f, 1000000f);

        // A server-stamped DateTime.UtcNow.Ticks value shown in the admin's own local time. Digits only, so
        // no locale can mangle the column, and every out-of-range/hostile value degrades to a dash.
        private static string PdatWhen(long ticksUtc)
        {
            if (ticksUtc <= 0L || ticksUtc > DateTime.MaxValue.Ticks) return "-";
            try
            {
                return new DateTime(ticksUtc, DateTimeKind.Utc).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "-"; }
        }

        private static string PdatPos(float x, float y, float z) =>
            x.ToString("0", CultureInfo.InvariantCulture) + ", " +
            y.ToString("0", CultureInfo.InvariantCulture) + ", " +
            z.ToString("0", CultureInfo.InvariantCulture);

        private static string PdatHours(long totalSeconds)
        {
            if (totalSeconds <= 0L) return "0.0";
            return (totalSeconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
