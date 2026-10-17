using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #2 creature command mode (server + the simulating client) ====================
    // Orders for the creatures in a radius, or the one under the crosshair: follow a player, guard a point within a
    // patrol radius, stay passive, or attack a named player. A creature is simulated by whichever client OWNS its ZDO
    // (usually the nearest player), so the server only writes the order into the ZDO and that owner carries it out:
    //   ap_cmd (int)    1 follow, 2 guard, 3 passive, 4 attack, 0/absent = normal
    //   ap_cmd_t (str)  the player to follow / attack
    //   ap_cmd_r (float) guard patrol radius
    // GUARD also writes the vanilla patrol fields (ZDOVars.s_patrol / s_patrolPoint, BaseAI.cs:271-301, read by the owner
    // every second), so a guard holds its post for EVERY owner, modded or not. Follow, passive, attack and the custom
    // patrol radius are applied by a MonsterAI.UpdateAI prefix in this companion on the owning client: those orders
    // need the companion on whichever client simulates the creature, and lapse (until it moves to a modded owner) on a
    // vanilla one. Orders persist with the creature (ZDO data is saved).
    //
    //   AP_SrvCcmd(ZPackage) { ver, int mode, int scope (0 radius, 1 one creature), Vector3 center, float radius,
    //                          ZDOID one, string targetName, Vector3 point, float guardRadius }
    internal static class Wave9Ccmd
    {
        private const int Cap = 100;
        internal static readonly int KeyCmd = "ap_cmd".GetStableHashCode();
        internal static readonly int KeyTarget = "ap_cmd_t".GetStableHashCode();
        internal static readonly int KeyRadius = "ap_cmd_r".GetStableHashCode();

        private static readonly List<ZDO> Scratch = new List<ZDO>();

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvCcmd", "builder");
            Wave9.ApplyPatch("Wave9CreatureCommand", typeof(AiPatch), "creature follow / passive / attack orders are not carried out by creatures this client simulates");
        }

        internal static void Register(ZRoutedRpc r) => r.Register<ZPackage>("AP_SrvCcmd", OnCmd);

        private static void OnCmd(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvCcmd")) return;
            int mode, scope; Vector3 center, point; float radius, guardRadius; ZDOID one; string target;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                mode = Mathf.Clamp(pkg.ReadInt(), 0, 4);
                scope = pkg.ReadInt();
                center = pkg.ReadVector3();
                radius = Mathf.Clamp(pkg.ReadSingle(), 2f, 64f);
                one = pkg.ReadZDOID();
                target = Wave9.Clean(pkg.ReadString(), 40);
                point = pkg.ReadVector3();
                guardRadius = Mathf.Clamp(pkg.ReadSingle(), 0f, 60f);
            }
            catch (Exception) { return; }
            if (!Wave9.Finite(center) || !Wave9.Finite(point)) return;
            if ((mode == 1 || mode == 4) && target.Length == 0) { Wave9.Tell(sender, "Pick a player for that order."); return; }

            var zdos = new List<ZDO>();
            if (scope == 1)
            {
                var z = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(one) : null;
                if (z != null) zdos.Add(z);
            }
            else
            {
                Wave9.ZdosInRadius(center, radius, Scratch);
                zdos.AddRange(Scratch);
                Scratch.Clear();
            }
            var n = 0;
            foreach (var z in zdos)
            {
                var kind = Wave9.KindOf(z.GetPrefab());
                if ((kind & Wave9.KCharacter) == 0 || (kind & Wave9.KPlayer) != 0) continue;
                if (n >= Cap) break;
                // The creature's simulating client owns this ZDO; the order is re-applied until its copy shows it.
                var m = mode; var t = target; var gr = guardRadius; var pt = point;
                Action<ZDO> apply = zz =>
                {
                    zz.Set(KeyCmd, m);
                    zz.Set(KeyTarget, t);
                    zz.Set(KeyRadius, gr);
                    if (m == 2) { zz.Set(ZDOVars.s_patrolPoint, pt); zz.Set(ZDOVars.s_patrol, true); }
                    else if (m == 0) zz.Set(ZDOVars.s_patrol, false);
                };
                apply(z);
                try { ZDOMan.instance.ForceSendZDO(z.m_uid); } catch (Exception) { }
                Wave9Reassert.Add(z.m_uid, zz => zz.GetInt(KeyCmd, 0) == m && zz.GetString(KeyTarget, "") == t, apply);
                n++;
            }
            var label = mode == 0 ? "normal" : mode == 1 ? "follow " + target : mode == 2 ? "guard" : mode == 3 ? "passive" : "attack " + target;
            CompanionPlugin.SrvAudit(sender, "CreatureCommand", $"{label}: {n} creature(s) {(scope == 1 ? "(aimed)" : $"within {radius:0} m of {Wave9.Vec(center)}")}");
            Wave9.Tell(sender, n == 0 ? "No creatures found for that order." : $"{n} creature(s) ordered: {label}.");
        }

        // ==================== the owner's side ====================

        private sealed class Orig { public bool CannotTarget; public float MoveRange; public MonsterAI Ai; }

        // Forget creatures that no longer exist (Unity null); an order still running keeps its true original values.
        private static void PruneOriginals()
        {
            var dead = new List<int>();
            foreach (var kv in Originals) if (kv.Value.Ai == null) dead.Add(kv.Key);
            foreach (var k in dead) Originals.Remove(k);
        }

        private static readonly Dictionary<int, Orig> Originals = new Dictionary<int, Orig>();
        private static readonly AccessTools.FieldRef<BaseAI, ZNetView> NView = AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<BaseAI, Character> CharacterRef = AccessTools.FieldRefAccess<BaseAI, Character>("m_character");
        private static readonly AccessTools.FieldRef<MonsterAI, Character> TargetCreature = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, StaticTarget> TargetStatic = AccessTools.FieldRefAccess<MonsterAI, StaticTarget>("m_targetStatic");

        [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
        private static class AiPatch
        {
            [HarmonyPrefix]
            private static void Prefix(MonsterAI __instance)
            {
                try
                {
                    var nv = NView(__instance);
                    if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
                    var zdo = nv.GetZDO();
                    var mode = zdo.GetInt(KeyCmd, 0);
                    var id = __instance.GetInstanceID();
                    Orig orig;
                    if (mode == 0)
                    {
                        if (Originals.TryGetValue(id, out orig))
                        {
                            var c0 = CharacterRef(__instance);
                            if (c0 != null) c0.m_aiCannotTargetOthers = orig.CannotTarget;
                            __instance.m_randomMoveRange = orig.MoveRange;
                            if (c0 != null && __instance.GetFollowTarget() != null && !c0.IsTamed()) __instance.SetFollowTarget(null);
                            Originals.Remove(id);
                        }
                        return;
                    }
                    var ch = CharacterRef(__instance);
                    if (ch == null) return;
                    if (!Originals.TryGetValue(id, out orig))
                    {
                        if (Originals.Count >= 2000) PruneOriginals();   // instances come and go; drop only the dead ones
                        orig = new Orig { CannotTarget = ch.m_aiCannotTargetOthers, MoveRange = __instance.m_randomMoveRange, Ai = __instance };
                        Originals[id] = orig;
                    }
                    switch (mode)
                    {
                        case 1:
                        {
                            var p = FindPlayer(zdo.GetString(KeyTarget, ""));
                            if (p != null && __instance.GetFollowTarget() != p.gameObject) __instance.SetFollowTarget(p.gameObject);
                            ch.m_aiCannotTargetOthers = true;
                            TargetCreature(__instance) = null;
                            TargetStatic(__instance) = null;
                            break;
                        }
                        case 2:
                        {
                            var r = zdo.GetFloat(KeyRadius, 0f);
                            __instance.m_randomMoveRange = r > 0f ? r : orig.MoveRange;
                            ch.m_aiCannotTargetOthers = orig.CannotTarget;
                            break;
                        }
                        case 3:
                            ch.m_aiCannotTargetOthers = true;
                            TargetCreature(__instance) = null;
                            TargetStatic(__instance) = null;
                            break;
                        case 4:
                        {
                            var p = FindPlayer(zdo.GetString(KeyTarget, ""));
                            ch.m_aiCannotTargetOthers = true;
                            if (p != null && !p.IsDead()) { TargetCreature(__instance) = p; TargetStatic(__instance) = null; }
                            break;
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        private static Player FindPlayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Player.GetAllPlayers())
                if (p != null && string.Equals(p.GetPlayerName(), name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }
}
