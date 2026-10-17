using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Round 3 — #4 custom status-effect forge (Player tab → Status Effects → Forge card) ====================
    // Build an effect from parts (speed, health/stamina regen, damage dealt, resistances, carry weight, duration, name),
    // save it as a server preset, and apply it like any other effect through the browser's "Apply to" pick. A status
    // effect only exists for a client whose ObjectDB holds it, so the recipe travels with every apply: "Me" is built
    // here, another player's companion builds it on their client (Wave9SrvStatusForge.cs).
    public partial class AdminPanelPlugin
    {
        private const int SefCap = 40;
        private const string SefPrefix = "APForge_";

        private static readonly (int Bit, string Key)[] SefResists =
        {
            (1, "sef.r_blunt"), (2, "sef.r_slash"), (4, "sef.r_pierce"), (32, "sef.r_fire"),
            (64, "sef.r_frost"), (128, "sef.r_lightning"), (256, "sef.r_poison"), (512, "sef.r_spirit"),
        };

        private sealed class SefDef
        {
            public string Id = "", Name = "", Tooltip = "";
            public float Duration = 300f, Carry;
            public int Speed, HpRegen, StRegen, Damage, Resist;
        }

        private ConfigEntry<bool> _sefCardCfg;
        private List<SefDef> _sefDefs;
        private bool _sefPending;
        private float _sefAskedAt;
        private string _sefEditId = "", _sefName = "", _sefTooltip = "";
        private string _sefDur = "300", _sefSpeed = "0", _sefHp = "0", _sefSt = "0", _sefDmg = "0", _sefCarry = "0";
        private int _sefResist;

        private List<SefDef> _sefDefsLayout;
        private string _sefWaitLayout = "sef.idle";

        internal void SefInit()
        {
            _sefCardCfg = Config.Bind("Features", "ShowStatusForge", true,
                "Show the Forge card (build custom status effects from parts) under the Player tab's status-effect browser.");
        }

        internal void SefReset()
        {
            _sefDefs = null;
            _sefPending = false;
            _sefDefsLayout = null;
        }

        internal static void SefRegisterReplies(ZRoutedRpc r) => W9Reply(r, "AP_SefData", ParseSefData);

        private static void ParseSefData(AdminPanelPlugin self, ZPackage pkg)
        {
            if (pkg.ReadInt() != 1) return;
            var n = pkg.ReadInt();
            if (n < 0 || n > SefCap) return;
            var list = new List<SefDef>(n);
            for (var i = 0; i < n; i++)
                list.Add(new SefDef
                {
                    Id = pkg.ReadString() ?? "", Name = pkg.ReadString() ?? "", Duration = pkg.ReadSingle(), Speed = pkg.ReadInt(), HpRegen = pkg.ReadInt(),
                    StRegen = pkg.ReadInt(), Damage = pkg.ReadInt(), Carry = pkg.ReadSingle(), Resist = pkg.ReadInt(), Tooltip = pkg.ReadString() ?? "",
                });
            self._sefDefs = list;
            self._sefPending = false;
        }

        private void SefList()
        {
            if (!W9Send("AP_SrvSefList")) return;
            _sefPending = true;
            _sefAskedAt = Time.time;
        }

        private void SefSave(int op, SefDef d)
        {
            var pkg = W9Pkg();
            pkg.Write(op);
            pkg.Write(d.Id ?? ""); pkg.Write(d.Name ?? ""); pkg.Write(d.Duration); pkg.Write(d.Speed); pkg.Write(d.HpRegen); pkg.Write(d.StRegen);
            pkg.Write(d.Damage); pkg.Write(d.Carry); pkg.Write(d.Resist); pkg.Write(d.Tooltip ?? "");
            if (W9Send("AP_SrvSefSet", pkg)) { _sefPending = true; _sefAskedAt = Time.time; }
        }

        private void SefApply(SefDef d)
        {
            if (_seTargetId == 0)
            {
                var p = LocalPlayer;
                var se = SefBuildLocal(d);
                if (p == null || se == null) return;
                p.GetSEMan().AddStatusEffect(se.NameHash(), true);
                Message(Loc.T("player.msg_applied", d.Name));
                return;
            }
            if (!TargetReady(_seTargetId)) return;
            var pkg = W9Pkg();
            pkg.Write(d.Id);
            pkg.Write(_seTargetId);
            if (W9Send("AP_SrvSefApply", pkg)) Message(Loc.T("player.msg_applied_to", d.Name, TargetLabel(_seTargetId)));
        }

        // Same recipe as Wave9Sef.Register on the companion side; the panel needs no companion to forge for itself.
        private SE_Stats SefBuildLocal(SefDef d)
        {
            var db = ObjectDB.instance;
            if (db == null) return null;
            var name = SefPrefix + d.Id;
            var hash = name.GetStableHashCode();
            db.m_StatusEffects.RemoveAll(s => s != null && s.NameHash() == hash);
            var se = ScriptableObject.CreateInstance<SE_Stats>();
            se.name = name;
            se.m_name = d.Name;
            se.m_tooltip = d.Tooltip;
            se.m_ttl = Mathf.Clamp(d.Duration, 5f, 86400f);
            var template = db.GetStatusEffect("Rested".GetStableHashCode());
            if (template != null) se.m_icon = template.m_icon;
            se.m_speedModifier = d.Speed / 100f;
            se.m_healthRegenMultiplier = 1f + d.HpRegen / 100f;
            se.m_staminaRegenMultiplier = 1f + d.StRegen / 100f;
            se.m_modifyAttackSkill = Skills.SkillType.All;
            se.m_damageModifier = 1f + d.Damage / 100f;
            se.m_addMaxCarryWeight = d.Carry;
            se.m_mods = new List<HitData.DamageModPair>();
            foreach (var r in SefResists)
                if ((d.Resist & r.Bit) != 0)
                    se.m_mods.Add(new HitData.DamageModPair { m_type = (HitData.DamageType)r.Bit, m_modifier = HitData.DamageModifier.Resistant });
            db.m_StatusEffects.Add(se);
            return se;
        }

        private void SefLoadEdit(SefDef d)
        {
            _sefEditId = d.Id;
            _sefName = d.Name;
            _sefTooltip = d.Tooltip;
            _sefDur = TkF(d.Duration);
            _sefSpeed = TkI(d.Speed);
            _sefHp = TkI(d.HpRegen);
            _sefSt = TkI(d.StRegen);
            _sefDmg = TkI(d.Damage);
            _sefCarry = TkF(d.Carry);
            _sefResist = d.Resist;
        }

        private string SefSummary(SefDef d)
        {
            var parts = new List<string> { Loc.T("sef.s_dur", TkF(d.Duration)) };
            if (d.Speed != 0) parts.Add(Loc.T("sef.s_speed", d.Speed));
            if (d.HpRegen != 0) parts.Add(Loc.T("sef.s_hp", d.HpRegen));
            if (d.StRegen != 0) parts.Add(Loc.T("sef.s_st", d.StRegen));
            if (d.Damage != 0) parts.Add(Loc.T("sef.s_dmg", d.Damage));
            if (d.Carry != 0f) parts.Add(Loc.T("sef.s_carry", TkF(d.Carry)));
            if (d.Resist != 0) parts.Add(Loc.T("sef.s_resist"));
            return string.Join(", ", parts.ToArray());
        }

        internal void DrawSefCard()
        {
            if (_sefCardCfg != null && !_sefCardCfg.Value) return;
            if (Event.current.type == EventType.Layout)
            {
                if (_sefDefs == null && !_sefPending && TkReachable()) SefList();
                _sefDefsLayout = _sefDefs;
                _sefWaitLayout = W9WaitKey(_sefPending, _sefAskedAt, "sef.idle");
            }

            BeginCard(Loc.T("sef.section"));
            GUILayout.Label(Loc.T("sef.hint", TargetLabel(_seTargetId)), _hintStyle);
            var defs = _sefDefsLayout;
            if (defs == null || _sefWaitLayout != "sef.idle") GUILayout.Label(Loc.T(_sefWaitLayout), _dimLabelStyle);
            if (defs != null)
            {
                if (defs.Count == 0) GUILayout.Label(Loc.T("sef.none"), _hintStyle);
                foreach (var d in defs)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(W9Short(d.Name, 22), _cellStyle, GUILayout.Width(160));
                    GUILayout.Label(W9Short(SefSummary(d), 50), _dimCellStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(Loc.T("player.apply"), _buttonStyle, GUILayout.MinWidth(60))) SefApply(d);
                    if (GUILayout.Button(Loc.T("sef.edit"), _buttonStyle, GUILayout.MinWidth(50))) SefLoadEdit(d);
                    if (ConfirmButton("sef:del:" + d.Id, Loc.T("sef.delete"), GUILayout.MinWidth(60))) SefSave(1, d);
                    GUILayout.EndHorizontal();
                }
            }

            DrawSection(Loc.T(_sefEditId.Length > 0 ? "sef.edit_title" : "sef.new_title"));
            _sefName = W9Field("sef.name", _sefName, 40, 200f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("sef.duration"), _labelStyle, GUILayout.MinWidth(120));
            _sefDur = GUILayout.TextField(_sefDur ?? "", 6, _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("sef.speed"), _labelStyle, GUILayout.MinWidth(80));
            _sefSpeed = GUILayout.TextField(_sefSpeed ?? "", 4, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("sef.damage"), _labelStyle, GUILayout.MinWidth(80));
            _sefDmg = GUILayout.TextField(_sefDmg ?? "", 4, _textFieldStyle, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("sef.hp_regen"), _labelStyle, GUILayout.MinWidth(120));
            _sefHp = GUILayout.TextField(_sefHp ?? "", 4, _textFieldStyle, GUILayout.Width(60));
            GUILayout.Label(Loc.T("sef.st_regen"), _labelStyle, GUILayout.MinWidth(80));
            _sefSt = GUILayout.TextField(_sefSt ?? "", 4, _textFieldStyle, GUILayout.Width(50));
            GUILayout.Label(Loc.T("sef.carry"), _labelStyle, GUILayout.MinWidth(80));
            _sefCarry = GUILayout.TextField(_sefCarry ?? "", 5, _textFieldStyle, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("sef.resists"), _labelStyle);
            GUILayout.BeginHorizontal();
            for (var i = 0; i < SefResists.Length; i++)
            {
                if (i == 4) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                var on = (_sefResist & SefResists[i].Bit) != 0;
                var now = GUILayout.Toggle(on, " " + Loc.T(SefResists[i].Key), _toggleStyle, GUILayout.MinWidth(110));
                if (now != on) _sefResist = now ? _sefResist | SefResists[i].Bit : _sefResist & ~SefResists[i].Bit;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            _sefTooltip = W9Field("sef.tooltip", _sefTooltip, 160, 320f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("sef.save"), _buttonStyle, GUILayout.MinWidth(100)))
            {
                var d = new SefDef
                {
                    Id = _sefEditId ?? "", Name = (_sefName ?? "").Trim(), Tooltip = (_sefTooltip ?? "").Trim(),
                    Duration = Mathf.Clamp(TkFloat(_sefDur, 300f), 5f, 86400f), Speed = TkInt(_sefSpeed, 0), HpRegen = TkInt(_sefHp, 0),
                    StRegen = TkInt(_sefSt, 0), Damage = TkInt(_sefDmg, 0), Carry = TkFloat(_sefCarry, 0f), Resist = _sefResist,
                };
                if (d.Name.Length == 0) Message(Loc.T("sef.msg_name"));
                else SefSave(0, d);
            }
            if (GUILayout.Button(Loc.T("sef.new"), _buttonStyle, GUILayout.MinWidth(80))) SefLoadEdit(new SefDef());
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("sef.client_hint"), _hintStyle);
            EndCard();
        }
    }
}
