using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #4 custom status-effect forge (server + client executor) ====================
    // Build a new effect from parts - speed, health and stamina regen, damage dealt, resistances, carry weight,
    // duration, name - as an SE_Stats created at runtime. A status effect only exists for a client whose ObjectDB
    // holds it (SEMan.AddStatusEffect(int) looks the hash up there, ObjectDB.cs:70-80), and stats apply on the
    // player's own client, so the definition travels WITH the apply: AP_SefApply carries the whole recipe, the
    // target's companion registers (or refreshes) "APForge_<id>" in its ObjectDB and adds it. The admin's panel
    // builds the same SE locally for "Me" (it needs no companion for itself).
    //
    // Presets: "w9_sef" (id -> v1|name|duration|speedPct|hpRegenPct|staminaRegenPct|damagePct|carry|resistMask|tooltip).
    //   AP_SrvSefList()            -> AP_SefData { ver, n x def }
    //   AP_SrvSefSet(ZPackage)     { ver, int op (0 upsert, 1 delete), def } -> AP_SefData
    //   AP_SrvSefApply(ZPackage)   { ver, string id, long target } -> AP_SefApply(def) on the target's client
    //   def: string id, string name, float duration, int speed, int hpRegen, int stRegen, int damage, float carry,
    //        int resistMask (HitData.DamageType bits: blunt 1 slash 2 pierce 4 fire 32 frost 64 lightning 128
    //        poison 256 spirit 512, each "Resistant"), string tooltip
    internal static class Wave9Sef
    {
        private const string Tbl = "w9_sef";
        private const int MaxDefs = 40;
        internal const string Prefix = "APForge_";

        internal sealed class Def
        {
            public string Id = "", Name = "", Tooltip = "";
            public float Duration = 300f, Carry;
            public int Speed, HpRegen, StRegen, Damage, Resist;

            public void Write(ZPackage p)
            {
                p.Write(Id); p.Write(Name); p.Write(Duration); p.Write(Speed); p.Write(HpRegen); p.Write(StRegen); p.Write(Damage);
                p.Write(Carry); p.Write(Resist); p.Write(Tooltip);
            }

            public static Def Read(ZPackage p) => new Def
            {
                Id = p.ReadString() ?? "", Name = p.ReadString() ?? "", Duration = p.ReadSingle(), Speed = p.ReadInt(), HpRegen = p.ReadInt(),
                StRegen = p.ReadInt(), Damage = p.ReadInt(), Carry = p.ReadSingle(), Resist = p.ReadInt(), Tooltip = p.ReadString() ?? "",
            };

            public void Clamp()
            {
                Id = Wave9.CleanName(Id, 12);
                Name = Wave9.Clean(Name, 40);
                Tooltip = Wave9.Clean(Tooltip, 160);
                Duration = Mathf.Clamp(float.IsNaN(Duration) ? 300f : Duration, 5f, 86400f);
                Speed = Mathf.Clamp(Speed, -90, 300);
                HpRegen = Mathf.Clamp(HpRegen, -90, 1000);
                StRegen = Mathf.Clamp(StRegen, -90, 1000);
                Damage = Mathf.Clamp(Damage, -90, 500);
                Carry = Mathf.Clamp(float.IsNaN(Carry) ? 0f : Carry, -300f, 2000f);
                Resist &= 0x3E7;
            }
        }

        private static readonly List<Def> Defs = new List<Def>();
        private static bool _loaded;

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSefSet", "builder");
            CompanionPlugin.GrantRole("AP_SrvSefList", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvSefApply", "builder");
        }

        internal static void Register(ZRoutedRpc r)
        {
            r.Register("AP_SrvSefList", new Action<long>(OnList));
            r.Register<ZPackage>("AP_SrvSefSet", OnSet);
            r.Register<ZPackage>("AP_SrvSefApply", OnApply);
            r.Register<ZPackage>("AP_SefApply", OnSefApply);   // client executor
            Defs.Clear();
            _loaded = false;
        }

        private static void Load()
        {
            if (_loaded || !FeatureStore.Ready) return;
            _loaded = true;
            Defs.Clear();
            foreach (var kv in FeatureStore.Table(Tbl))
            {
                var p = Wave9.Unpack(kv.Value, 10);
                if (p[0] != "v1") continue;
                Defs.Add(new Def
                {
                    Id = kv.Key, Name = p[1], Duration = Wave9.ParseFloat(p[2], 300f), Speed = Wave9.ParseInt(p[3], 0), HpRegen = Wave9.ParseInt(p[4], 0),
                    StRegen = Wave9.ParseInt(p[5], 0), Damage = Wave9.ParseInt(p[6], 0), Carry = Wave9.ParseFloat(p[7], 0f), Resist = Wave9.ParseInt(p[8], 0),
                    Tooltip = p[9],
                });
            }
        }

        private static void Save()
        {
            var t = FeatureStore.Table(Tbl);
            t.Clear();
            foreach (var d in Defs)
                t[d.Id] = Wave9.Pack("v1", d.Name, Wave9.F1(d.Duration), Wave9.I(d.Speed), Wave9.I(d.HpRegen), Wave9.I(d.StRegen),
                    Wave9.I(d.Damage), Wave9.F1(d.Carry), Wave9.I(d.Resist), d.Tooltip);
            FeatureStore.SaveTable(Tbl);
        }

        private static ZPackage ListPkg()
        {
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(Defs.Count);
            foreach (var d in Defs) d.Write(pkg);
            return pkg;
        }

        private static void OnList(long sender)
        {
            if (!Wave9.Can(sender, "AP_SrvSefList")) return;
            Load();
            Wave9.Reply(sender, "AP_SefData", ListPkg());
        }

        private static void OnSet(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvSefSet")) return;
            Load();
            int op; Def d;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                op = pkg.ReadInt();
                d = Def.Read(pkg);
            }
            catch (Exception) { return; }
            d.Clamp();
            var existing = Defs.Find(x => x.Id == d.Id);
            string what;
            if (op == 1)
            {
                if (existing == null) return;
                Defs.Remove(existing);
                what = "deleted";
            }
            else
            {
                if (d.Name.Length == 0) { Wave9.Tell(sender, "A forged effect needs a name."); return; }
                if (existing == null)
                {
                    if (Defs.Count >= MaxDefs) { Wave9.Tell(sender, $"Forged effect limit reached ({MaxDefs})."); return; }
                    d.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                    Defs.Add(d);
                    what = "saved";
                }
                else { Defs[Defs.IndexOf(existing)] = d; what = "updated"; }
            }
            Save();
            CompanionPlugin.SrvAudit(sender, "SeForge", $"{what} '{d.Name}' ({d.Duration:0} s, speed {d.Speed}%, dmg {d.Damage}%, carry {d.Carry:0})");
            Wave9.Reply(sender, "AP_SefData", ListPkg());
        }

        private static void OnApply(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvSefApply")) return;
            Load();
            string id; long target;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                id = pkg.ReadString();
                target = pkg.ReadLong();
            }
            catch (Exception) { return; }
            string why;
            if (!ApplyTo(target, id, out why)) { Wave9.Tell(sender, "Forged effect not applied: " + why); return; }
            var d = Find(id);
            CompanionPlugin.SrvAudit(sender, "SeForgeApply", $"'{(d != null ? d.Name : id)}' -> {target}");
            Wave9Undo.Note(sender, "effect", target, (Prefix + id).GetStableHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "applied forged effect " + (d != null ? d.Name : id));
        }

        private static Def Find(string idOrName)
        {
            Load();
            var key = (idOrName ?? "").Trim();
            return Defs.Find(x => x.Id == key) ?? Defs.Find(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Send a preset (by id or name) to a player's client. Used by the panel relay and the trigger engine.</summary>
        internal static bool ApplyTo(long uid, string idOrName, out string why)
        {
            why = "";
            var d = Find(idOrName);
            if (d == null) { why = "no forged effect with that id or name"; return false; }
            if (uid == 0L) { why = "no target"; return false; }
            if (Wave9.IsHostUid(uid)) { ApplyLocal(d); return true; }
            if (Wave9.PeerByUid(uid) == null) { why = "player not connected"; return false; }
            if (Wave34Core.HasMod(uid) != true) { why = Wave34Core.CapReason(uid); return false; }
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            d.Write(pkg);
            ZRoutedRpc.instance.InvokeRoutedRPC(uid, "AP_SefApply", pkg);
            return true;
        }

        // ==================== client executor ====================

        private static void OnSefApply(long sender, ZPackage pkg)
        {
            if (!Wave9.FromServer(sender) || Player.m_localPlayer == null) return;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                var d = Def.Read(pkg);
                d.Clamp();
                ApplyLocal(d);
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Forged effect could not be applied: {e.Message}"); }
        }

        private static void ApplyLocal(Def d)
        {
            var p = Player.m_localPlayer;
            if (p == null || ObjectDB.instance == null) return;
            var se = Register(d);
            if (se == null) return;
            Wave9Undo.NoteEffectApplying(p.GetSEMan(), se.NameHash(), out var hadBefore);
            p.GetSEMan().AddStatusEffect(se.NameHash(), true);
            Wave9Undo.NoteEffectApplied(se.NameHash(), hadBefore);
            p.Message(MessageHud.MessageType.Center, d.Name);
        }

        /// <summary>Create (or refresh) "APForge_&lt;id&gt;" in this client's ObjectDB from a definition.</summary>
        internal static SE_Stats Register(Def d)
        {
            var db = ObjectDB.instance;
            if (db == null) return null;
            var name = Prefix + d.Id;
            var hash = name.GetStableHashCode();
            // The ObjectDB entry is only a template (SEMan.AddStatusEffect clones it), so a replaced one can be freed.
            foreach (var old in db.m_StatusEffects.FindAll(s => s != null && s.NameHash() == hash)) UnityEngine.Object.Destroy(old);
            db.m_StatusEffects.RemoveAll(s => s == null || s.NameHash() == hash);
            var se = ScriptableObject.CreateInstance<SE_Stats>();
            se.name = name;
            se.m_name = d.Name;
            se.m_tooltip = d.Tooltip;
            se.m_ttl = d.Duration;
            var template = db.GetStatusEffect("Rested".GetStableHashCode());
            if (template != null) se.m_icon = template.m_icon;
            se.m_speedModifier = d.Speed / 100f;
            se.m_healthRegenMultiplier = 1f + d.HpRegen / 100f;
            se.m_staminaRegenMultiplier = 1f + d.StRegen / 100f;
            se.m_modifyAttackSkill = Skills.SkillType.All;
            se.m_damageModifier = 1f + d.Damage / 100f;
            se.m_addMaxCarryWeight = d.Carry;
            se.m_mods = new List<HitData.DamageModPair>();
            foreach (HitData.DamageType t in new[] { HitData.DamageType.Blunt, HitData.DamageType.Slash, HitData.DamageType.Pierce, HitData.DamageType.Fire,
                                                     HitData.DamageType.Frost, HitData.DamageType.Lightning, HitData.DamageType.Poison, HitData.DamageType.Spirit })
                if ((d.Resist & (int)t) != 0) se.m_mods.Add(new HitData.DamageModPair { m_type = t, m_modifier = HitData.DamageModifier.Resistant });
            db.m_StatusEffects.Add(se);
            return se;
        }
    }
}
