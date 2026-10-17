using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #3 boss encounter tuner + fight recorder (Bosses tab → Encounter card) ====================
    // Before a summon: HP and damage multipliers, extra HP per additional nearby player, an enrage timer. During the
    // fight the companion records damage per player, deaths and time to kill; at the end a summary appears here and can
    // be posted to Discord (Wave9SrvEncounters.cs). Needs a dedicated server or host running the companion; boss damage
    // to the player who simulates the boss is only scaled when that player's client runs the companion too.
    public partial class AdminPanelPlugin
    {
        private sealed class EncActive { public int Id; public string Prefab = ""; public float Elapsed, HpPct, Dmg; public bool Enraged; }
        private sealed class EncDone { public int Id; public string Prefab = "", Top = "", Text = ""; public bool Killed; public float Seconds; public int Players, Deaths; }

        private ConfigEntry<bool> _encCardCfg;
        private int _encBoss;
        private string _encStars = "0", _encHp = "1.5", _encPer = "0.5", _encDmg = "1.2", _encEnrageMin = "10", _encEnrageMult = "1.5";
        private List<EncActive> _encActive;
        private List<EncDone> _encDone;
        private float _encNextPoll;

        private List<EncActive> _encActiveLayout;
        private List<EncDone> _encDoneLayout;

        internal void EncInit()
        {
            _encCardCfg = Config.Bind("Features", "ShowEncounterCard", true,
                "Show the Encounter card (boss HP/damage tuning, enrage timer, fight recorder) at the top of the Bosses tab.");
        }

        internal void EncReset()
        {
            _encActive = null;
            _encDone = null;
            _encActiveLayout = null;
            _encDoneLayout = null;
        }

        internal static void EncRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_EncData", ParseEncData);

        private static void ParseEncData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var na = pkg.ReadInt();
            if (na < 0 || na > 32) return;
            var act = new List<EncActive>(na);
            for (var i = 0; i < na; i++)
                act.Add(new EncActive { Id = pkg.ReadInt(), Prefab = pkg.ReadString() ?? "", Elapsed = pkg.ReadSingle(), HpPct = pkg.ReadSingle(), Dmg = pkg.ReadSingle(), Enraged = pkg.ReadBool() });
            var nd = pkg.ReadInt();
            if (nd < 0 || nd > 16) return;
            var done = new List<EncDone>(nd);
            for (var i = 0; i < nd; i++)
                done.Add(new EncDone
                {
                    Id = pkg.ReadInt(), Prefab = pkg.ReadString() ?? "", Killed = pkg.ReadBool(), Seconds = pkg.ReadSingle(), Players = pkg.ReadInt(),
                    Top = pkg.ReadString() ?? "", Deaths = pkg.ReadInt(), Text = pkg.ReadString() ?? "",
                });
            self._encActive = act;
            self._encDone = done;
        }

        private void EncSummon()
        {
            if (BossList == null || BossList.Length == 0) return;
            var b = BossList[Mathf.Clamp(_encBoss, 0, BossList.Length - 1)];
            var pkg = W9Pkg();
            pkg.Write(b.Item1);
            pkg.Write(SpawnPos(8f));
            pkg.Write(Mathf.Clamp(TkInt(_encStars, 0), 0, 4));
            pkg.Write(Mathf.Clamp(TkFloat(_encHp, 1f), 0.1f, 50f));
            pkg.Write(Mathf.Clamp(TkFloat(_encPer, 0f), 0f, 5f));
            pkg.Write(Mathf.Clamp(TkFloat(_encDmg, 1f), 0.1f, 20f));
            pkg.Write(Mathf.Clamp(TkInt(_encEnrageMin, 0), 0, 120));
            pkg.Write(Mathf.Clamp(TkFloat(_encEnrageMult, 1f), 1f, 10f));
            if (W9Send("AP_SrvEncSummon", pkg)) { Message(Loc.T("boss.msg_requested", b.Item2)); _encNextPoll = Time.time + 1f; }
        }

        private static string EncTime(float s) => ((int)(s / 60f)) + ":" + ((int)(s % 60f)).ToString("00");

        internal void DrawEncCard()
        {
            if (_encCardCfg != null && !_encCardCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                if (Time.time >= _encNextPoll && TkReachable())
                {
                    _encNextPoll = Time.time + 3f;
                    SrvRpc("AP_SrvEncReq");
                }
                _encActiveLayout = _encActive;
                _encDoneLayout = _encDone;
            }

            BeginCard(Loc.T("enc.section"));
            GUILayout.Label(Loc.T("enc.hint"), _hintStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("enc.boss"), _labelStyle, GUILayout.MinWidth(90));
            var label = BossList != null && BossList.Length > 0 ? BossList[Mathf.Clamp(_encBoss, 0, BossList.Length - 1)].Item2 : "?";
            if (GUILayout.Button(label, _buttonStyle, GUILayout.MinWidth(150)) && BossList != null && BossList.Length > 0)
                _encBoss = (_encBoss + 1) % BossList.Length;
            GUILayout.Label(Loc.T("enc.stars"), _labelStyle, GUILayout.MinWidth(45));
            _encStars = GUILayout.TextField(_encStars ?? "", 1, _textFieldStyle, GUILayout.Width(30));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("enc.hp"), _labelStyle, GUILayout.MinWidth(90));
            _encHp = GUILayout.TextField(_encHp ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("enc.per_player"), _labelStyle, GUILayout.MinWidth(140));
            _encPer = GUILayout.TextField(_encPer ?? "", 4, _textFieldStyle, GUILayout.Width(45));
            GUILayout.Label(Loc.T("enc.dmg"), _labelStyle, GUILayout.MinWidth(80));
            _encDmg = GUILayout.TextField(_encDmg ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("enc.enrage_min"), _labelStyle, GUILayout.MinWidth(140));
            _encEnrageMin = GUILayout.TextField(_encEnrageMin ?? "", 3, _textFieldStyle, GUILayout.Width(45));
            GUILayout.Label(Loc.T("enc.enrage_mult"), _labelStyle, GUILayout.MinWidth(110));
            _encEnrageMult = GUILayout.TextField(_encEnrageMult ?? "", 4, _textFieldStyle, GUILayout.Width(45));
            if (GUILayout.Button(Loc.T("enc.summon"), _buttonStyle, GUILayout.MinWidth(120))) EncSummon();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var act = _encActiveLayout;
            if (act != null && act.Count > 0)
            {
                DrawSection(Loc.T("enc.active"));
                foreach (var a in act)
                    GUILayout.Label(Loc.T("enc.active_line", a.Id, a.Prefab, EncTime(a.Elapsed), Mathf.RoundToInt(a.HpPct * 100f), TkF1(a.Dmg)) +
                                    (a.Enraged ? " " + Loc.T("enc.enraged") : ""), a.Enraged ? _headerStyle : _cellStyle);
            }
            var done = _encDoneLayout;
            if (done != null && done.Count > 0)
            {
                DrawSection(Loc.T("enc.recent"));
                foreach (var d in done)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("#" + d.Id + " " + d.Prefab, _cellStyle, GUILayout.Width(150));
                    GUILayout.Label(Loc.T(d.Killed ? "enc.killed" : "enc.despawned", EncTime(d.Seconds), d.Players, d.Deaths), _dimCellStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("enc.post"), _buttonStyle, GUILayout.MinWidth(110)))
                    {
                        if (TkReachable()) SrvRpc("AP_SrvEncPost", d.Id);
                        else Message(Loc.T("common.not_connected_srv"));
                    }
                    GUILayout.EndHorizontal();
                    if (d.Top.Length > 0) GUILayout.Label("  " + Loc.T("enc.top", d.Top), _dimCellStyle);
                }
            }
            EndCard();
        }
    }
}
