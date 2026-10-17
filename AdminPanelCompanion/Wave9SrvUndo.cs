using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Round 3 — #13 universal undo timeline (server) ====================
    // One timeline shared by every admin: each reversible admin action records its inverse when it happens, and any
    // row - or "everything admin X did in the last N minutes", newest first - can be undone from the panel.
    //
    //   recorded by the server's own handlers (CompanionPlugin / Wave8ToolkitForge call Note*):
    //     give   -> take back ONLY the copies carrying that grant's provenance serial (AP_W9TakeBack on the target,
    //               needs their companion); a grant made with stamping off has no serial and is not undoable,
    //               because nothing would tell the admin's items from the player's own
    //     tp     -> teleport the player back where they stood (vanilla RPC_TeleportPlayer, works for everyone)
    //     spawn  -> destroy the spawned objects
    //     skill  -> reverse the change the target's client CONFIRMED it applied (AP_SrvSkillApplied: the game clamps
    //               at 0/100, so +50 on a level-80 skill is +20 and the undo is -20, never -50)
    //     effect -> remove the status effect, but only if the admin's apply added it (AP_W9RemoveSE)
    //     ban    -> unban (every stored spelling, CompanionPlugin's own unban)
    //   reported by the admin's panel (AP_SrvJournal, admin-gated, validated, 30 rows/min per admin):
    //     selftp   -> teleport the admin back (only that admin may undo it)
    //     piece_rm -> recreate the removed piece from its ZDO.Save blob (must decode to a building piece)
    //     piece_mv -> put the piece's ZDO position/rotation back (+ AP_UndoPieceLocal so the undoing admin's
    //                 loaded copy moves at once; others see it on reload, exactly like the original edit).
    //                 A burst of nudges to one piece is ONE row that returns it to where the burst started.
    // Permissions: undoing a row needs the permission of the action it reverses (role-checked per row), and a row
    // made by ANOTHER admin also needs "AP_SrvUndoOthers" (granted to moderators; owners have everything).
    // In memory, the last 500 rows (cleared on world change); every row is also appended to "w9_undo.log".
    //
    //   AP_SrvUndoList(ZPackage {ver, string adminId ("" = all), int minutes (0 = all)}) -> AP_UndoData
    //   AP_SrvUndoDo(ZPackage {ver, int mode (0 row, 1 admin+minutes, 2 my last), int id, string adminId, int minutes})
    //   AP_UndoData { ver, n x { int id, long ticks, string adminName, string adminId, string kind, string target,
    //                 string summary, bool undone, bool undoable } }
    internal static class Wave9Undo
    {
        private const int Cap = 500;
        private const int ShipCap = 150;
        private const int DataCap = 32768;
        private const int JournalPerMinute = 30;
        private const double CoalesceSeconds = 60d;
        private const string Log = "w9_undo";
        private const string OthersAction = "AP_SrvUndoOthers";

        private sealed class Entry
        {
            public int Id;
            public long Ticks;
            public long AdminUid;
            public string AdminId = "", AdminName = "";
            public string Kind = "";
            public long TargetUid;
            public string TargetName = "";
            public string Summary = "";
            public string Data = "";
            public List<ZDOID> Ids;
            public bool Undone;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static int _seq;
        private static readonly Dictionary<long, List<long>> JournalTimes = new Dictionary<long, List<long>>();

        // give undo in flight: "<targetUid>|<serial>" -> (admin to tell, expiry)
        private static readonly Dictionary<string, KeyValuePair<long, float>> TakeBackPending = new Dictionary<string, KeyValuePair<long, float>>();

        internal static void Init()
        {
            CompanionPlugin.RegisterAuditedRpc("AP_SrvUndoDo", "builder");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvUndoDo", "moderator");
            CompanionPlugin.GrantRole("AP_SrvUndoList", "builder");
            CompanionPlugin.GrantRole("AP_SrvUndoList", "moderator");
            CompanionPlugin.GrantRole("AP_SrvJournal", "builder");
            CompanionPlugin.GrantRole("AP_SrvJournal", "moderator");
            CompanionPlugin.GrantRole(OthersAction, "moderator");
        }

        internal static void Register(ZRoutedRpc r)
        {
            // A new world: rows of the old one point at objects and players that no longer exist.
            Entries.Clear();
            JournalTimes.Clear();
            TakeBackPending.Clear();
            r.Register<ZPackage>("AP_SrvUndoList", OnList);
            r.Register<ZPackage>("AP_SrvUndoDo", OnDo);
            r.Register<ZPackage>("AP_SrvJournal", OnJournal);
            r.Register<ZPackage>("AP_SrvSkillApplied", OnSkillApplied);   // from the TARGET's client
            r.Register<ZPackage>("AP_SrvTakenBack", OnTakenBack);         // from the TARGET's client
            r.Register<int>("AP_W9RemoveSE", OnRemoveSE);                  // client executor
            r.Register<ZPackage>("AP_W9TakeBack", OnTakeBack);             // client executor
        }

        // ==================== recording ====================

        private static string NameOf(long uid)
        {
            if (Wave9.IsHostUid(uid)) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "host";
            var p = Wave9.PeerByUid(uid);
            return p != null ? p.m_playerName ?? "?" : "?";
        }

        private static Entry Add(long admin, string kind, long target, string summary, string data)
        {
            if (!Wave9.IsServer) return null;
            var e = new Entry
            {
                Id = ++_seq, Ticks = DateTime.UtcNow.Ticks, AdminUid = admin, AdminId = CompanionPlugin.SenderPlatformId(admin),
                AdminName = CompanionPlugin.SenderDisplayName(admin), Kind = kind, TargetUid = target, TargetName = target != 0L ? NameOf(target) : "",
                Summary = Wave9.Clean(summary, 120), Data = data ?? "",
            };
            Entries.Add(e);
            if (Entries.Count > Cap) Entries.RemoveAt(0);
            try { FeatureStore.Append(Log, Wave9.Pack(Wave9.I(e.Ticks), e.AdminId, e.AdminName, kind, e.TargetName, e.Summary)); } catch (Exception) { }
            return e;
        }

        internal static void Note(long admin, string kind, long target, string data, string summary) => Add(admin, kind, target, summary, data);

        internal static void NoteSpawn(long admin, List<ZDOID> batch)
        {
            if (batch == null || batch.Count == 0) return;
            var e = Add(admin, "spawn", 0L, $"spawned {batch.Count} object(s)", "");
            if (e != null) e.Ids = new List<ZDOID>(batch);
        }

        private static bool JournalRateOk(long sender)
        {
            List<long> times;
            if (!JournalTimes.TryGetValue(sender, out times)) JournalTimes[sender] = times = new List<long>();
            var now = DateTime.UtcNow.Ticks;
            times.RemoveAll(t => now - t > TimeSpan.TicksPerMinute);
            if (times.Count >= JournalPerMinute) return false;
            times.Add(now);
            return true;
        }

        private static bool IsBuildPiece(int prefab)
        {
            var k = Wave9.KindOf(prefab);
            return (k & Wave9.KPiece) != 0 && (k & (Wave9.KCharacter | Wave9.KPlayer)) == 0;
        }

        private static Wave9Wdiff.Obj DecodePiece(string data, out string why)
        {
            why = "";
            try
            {
                var pkg = new ZPackage(Convert.FromBase64String(data));
                int ver = pkg.ReadShort();
                pkg.ReadInt();
                var o = Wave9Wdiff.ReadZdo(pkg, ver, true);
                if (o == null) { why = "unreadable record"; return null; }
                if (!Wave9.Finite(o.Pos)) { why = "bad position"; return null; }
                if (!IsBuildPiece(o.Prefab)) { why = "not a building piece"; return null; }
                return o;
            }
            catch (Exception ex) { why = ex.Message; return null; }
        }

        private static bool TryParseMove(string data, out ZDOID zid, out Vector3 pos, out Quaternion rot)
        {
            zid = ZDOID.None; pos = Vector3.zero; rot = Quaternion.identity;
            var p = (data ?? "").Split('|');
            if (p.Length != 3) return false;
            var ids = p[0].Split(':');
            if (ids.Length != 2) return false;
            zid = new ZDOID(Wave9.ParseLong(ids[0], 0L), (uint)Wave9.ParseLong(ids[1], 0L));
            pos = Wave9.ParseVec(p[1]);
            var q = p[2].Split(';');
            if (q.Length != 4) return false;
            rot = new Quaternion(Wave9.ParseFloat(q[0], 0f), Wave9.ParseFloat(q[1], 0f), Wave9.ParseFloat(q[2], 0f), Wave9.ParseFloat(q[3], 1f));
            return Wave9.Finite(pos) && !float.IsNaN(rot.x) && !float.IsNaN(rot.y) && !float.IsNaN(rot.z) && !float.IsNaN(rot.w);
        }

        private static void OnJournal(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvJournal")) return;
            string kind, data, summary;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                kind = pkg.ReadString();
                data = pkg.ReadString();
                summary = pkg.ReadString();
            }
            catch (Exception) { return; }
            if (kind != "selftp" && kind != "piece_rm" && kind != "piece_mv") return;
            if (data == null || data.Length == 0 || data.Length > DataCap) return;

            // The row will later be executed with server authority by whoever undoes it, so it must describe exactly
            // the harmless inverse it claims: a position, a building piece, or a nearby move of an existing piece.
            string why = "";
            switch (kind)
            {
                case "selftp":
                    if (!Wave9.Finite(Wave9.ParseVec(data))) return;
                    break;
                case "piece_rm":
                    if (DecodePiece(data, out why) == null) { Wave9.ThrottledLog("undo-journal", $"Journal row from {sender} refused: {why}"); return; }
                    break;
                case "piece_mv":
                {
                    ZDOID zid; Vector3 pos; Quaternion rot;
                    if (!TryParseMove(data, out zid, out pos, out rot)) return;
                    var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(zid) : null;
                    if (zdo == null || !IsBuildPiece(zdo.GetPrefab())) return;
                    if ((zdo.GetPosition() - pos).sqrMagnitude > 64f * 64f) return;   // a nudge, never a relocation
                    // A burst of nudges to one piece: keep the FIRST row (its "before" is where the burst started).
                    for (var i = Entries.Count - 1; i >= 0; i--)
                    {
                        var prev = Entries[i];
                        if (prev.AdminUid != sender) continue;
                        if (!prev.Undone && prev.Kind == "piece_mv" && prev.Data.StartsWith(data.Split('|')[0] + "|", StringComparison.Ordinal)
                            && (DateTime.UtcNow.Ticks - prev.Ticks) < TimeSpan.FromSeconds(CoalesceSeconds).Ticks)
                        {
                            prev.Ticks = DateTime.UtcNow.Ticks;
                            return;
                        }
                        break;   // only the admin's latest row coalesces
                    }
                    break;
                }
            }
            if (!JournalRateOk(sender)) { Wave9.ThrottledLog("undo-rate", $"Journal rows from {sender} over {JournalPerMinute}/min dropped"); return; }
            Add(sender, kind, kind == "selftp" ? sender : 0L, summary, data);
        }

        // The target's client reports what a relayed skill raise actually did (the game clamps at 0 and 100).
        private static void OnSkillApplied(long sender, ZPackage pkg)
        {
            if (!Wave9.IsServer) return;
            string skill; float before, after;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                skill = pkg.ReadString();
                before = pkg.ReadSingle();
                after = pkg.ReadSingle();
            }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(skill) || float.IsNaN(before) || float.IsNaN(after)) return;
            var horizon = DateTime.UtcNow.AddSeconds(-60).Ticks;
            for (var i = Entries.Count - 1; i >= 0 && Entries[i].Ticks >= horizon; i--)
            {
                var e = Entries[i];
                if (e.Kind != "skill" || e.Undone || e.TargetUid != sender) continue;
                var p = e.Data.Split('|');
                if (p.Length != 2 || !string.Equals(p[0], skill, StringComparison.OrdinalIgnoreCase)) continue;
                var requested = Wave9.ParseFloat(p[1], 0f);
                // The report comes from the player's own client: it can only ever make the undo SMALLER.
                var applied = requested >= 0f ? Mathf.Clamp(after - before, 0f, requested) : Mathf.Clamp(after - before, requested, 0f);
                e.Data = p[0] + "|" + p[1] + "|" + applied.ToString("0.###", CultureInfo.InvariantCulture);
                return;
            }
        }

        // ==================== list ====================

        private static void OnList(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvUndoList")) return;
            string adminId; int minutes;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                adminId = Wave9.CleanName(pkg.ReadString(), 64);
                minutes = Mathf.Clamp(pkg.ReadInt(), 0, 100000);
            }
            catch (Exception) { return; }
            SendList(sender, adminId, minutes);
        }

        private static void SendList(long to, string adminId, int minutes)
        {
            var cutoff = minutes > 0 ? DateTime.UtcNow.AddMinutes(-minutes).Ticks : 0L;
            var rows = new List<Entry>();
            for (var i = Entries.Count - 1; i >= 0 && rows.Count < ShipCap; i--)
            {
                var e = Entries[i];
                if (e.Ticks < cutoff) break;
                if (adminId.Length > 0 && e.AdminId != adminId) continue;
                rows.Add(e);
            }
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(rows.Count);
            foreach (var e in rows)
            {
                pkg.Write(e.Id);
                pkg.Write(e.Ticks);
                pkg.Write(e.AdminName);
                pkg.Write(e.AdminId);
                pkg.Write(e.Kind);
                pkg.Write(e.TargetName);
                pkg.Write(e.Summary);
                pkg.Write(e.Undone);
                pkg.Write(!e.Undone && Reversible(e));
            }
            Wave9.Reply(to, "AP_UndoData", pkg);
        }

        private static bool Reversible(Entry e)
        {
            switch (e.Kind)
            {
                case "give": { var p = e.Data.Split('|'); return p.Length >= 3 && p[2].Length > 0; }
                case "skill": return e.Data.Split('|').Length >= 3;
                default: return ActionOf(e.Kind) != null;
            }
        }

        // ==================== undo ====================

        // The permission the original action needed; undoing it needs the same one.
        private static string ActionOf(string kind)
        {
            switch (kind)
            {
                case "give": return "AP_SrvGive";
                case "tp":
                case "selftp": return "AP_SrvTeleport";
                case "spawn":
                case "piece_rm":
                case "piece_mv": return "AP_SrvSpawn";
                case "skill": return "AP_SrvSkillRaise";
                case "effect": return "AP_SrvApplySE";
                case "ban": return "AP_SrvUnban";
                default: return null;
            }
        }

        private static string Permitted(long sender, string senderId, Entry e)
        {
            var mine = e.AdminUid == sender || e.AdminId == senderId;
            if (e.Kind == "selftp" && !mine) return "only " + e.AdminName + " can undo their own teleport";
            if (!mine && !CompanionPlugin.SenderCanFeature(sender, OthersAction)) return "made by " + e.AdminName + " (your role cannot undo other admins' actions)";
            var act = ActionOf(e.Kind);
            if (act == null) return "not reversible";
            if (!CompanionPlugin.SenderCanFeature(sender, act)) return "your role does not allow " + act;
            return null;
        }

        private static void OnDo(long sender, ZPackage pkg)
        {
            if (!Wave9.Can(sender, "AP_SrvUndoDo")) return;
            int mode, id, minutes; string adminId;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                mode = pkg.ReadInt();
                id = pkg.ReadInt();
                adminId = Wave9.CleanName(pkg.ReadString(), 64);
                minutes = Mathf.Clamp(pkg.ReadInt(), 1, 1440);
            }
            catch (Exception) { return; }

            var me = CompanionPlugin.SenderPlatformId(sender);
            var targets = new List<Entry>();
            if (mode == 0) { var e = Entries.Find(x => x.Id == id); if (e != null) targets.Add(e); }
            else if (mode == 2)
            {
                for (var i = Entries.Count - 1; i >= 0; i--)
                    if (!Entries[i].Undone && (Entries[i].AdminUid == sender || Entries[i].AdminId == me)) { targets.Add(Entries[i]); break; }
            }
            else if (mode == 1 && adminId.Length > 0)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-minutes).Ticks;
                for (var i = Entries.Count - 1; i >= 0 && Entries[i].Ticks >= cutoff; i--)
                    if (!Entries[i].Undone && Entries[i].AdminId == adminId) targets.Add(Entries[i]);   // newest first
            }
            if (targets.Count == 0) { Wave9.Tell(sender, "Nothing to undo."); return; }

            int ok = 0, failed = 0;
            var notes = new List<string>();
            foreach (var e in targets)
            {
                if (e.Undone) continue;
                string why = Permitted(sender, me, e);
                if (why == null && Undo(sender, e, out why)) { e.Undone = true; ok++; }
                else { failed++; if (notes.Count < 3) notes.Add($"#{e.Id} {e.Kind}: {why}"); }
            }
            CompanionPlugin.SrvAudit(sender, "Undo", $"mode {mode}: undid {ok}, failed {failed}" + (notes.Count > 0 ? " - " + string.Join("; ", notes.ToArray()) : ""));
            Wave9.Tell(sender, $"Undo: {ok} action(s) reversed" + (failed > 0 ? $", {failed} could not be ({string.Join("; ", notes.ToArray())})" : "") + ".");
            SendList(sender, mode == 1 ? adminId : "", 0);
        }

        private static bool Undo(long by, Entry e, out string why)
        {
            why = "";
            switch (e.Kind)
            {
                case "give":
                {
                    var p = e.Data.Split('|');
                    if (p.Length < 3 || p[2].Length == 0) { why = "no provenance serial (stamping was off), the items cannot be told from the player's own"; return false; }
                    if (!Online(e.TargetUid)) { why = "target offline"; return false; }
                    var relay = new ZPackage();
                    relay.Write(Wave9.Ver);
                    relay.Write(p[0]);
                    relay.Write(p[2]);
                    relay.Write(Wave9.ParseInt(p[1], 0));
                    TakeBackPending[e.TargetUid.ToString(CultureInfo.InvariantCulture) + "|" + p[2]] = new KeyValuePair<long, float>(by, Time.time + 30f);
                    ZRoutedRpc.instance.InvokeRoutedRPC(e.TargetUid, "AP_W9TakeBack", relay);
                    return true;
                }
                case "tp":
                case "selftp":
                    if (!Online(e.TargetUid)) { why = "player offline"; return false; }
                    if (!Wave9.TeleportPeer(e.TargetUid, Wave9.ParseVec(e.Data))) { why = "teleport failed"; return false; }
                    return true;
                case "spawn":
                {
                    var man = ZDOMan.instance;
                    if (man == null || e.Ids == null) { why = "nothing recorded"; return false; }
                    foreach (var id in e.Ids) { var z = man.GetZDO(id); if (z != null) Wave9.DestroyZdo(z); }
                    return true;
                }
                case "skill":
                {
                    var p = e.Data.Split('|');
                    if (p.Length < 3) { why = "the player's client never confirmed the change (their companion predates round 3)"; return false; }
                    var applied = Wave9.ParseFloat(p[2], 0f);
                    if (Mathf.Abs(applied) < 0.001f) return true;   // the clamp ate the whole change: nothing to reverse
                    if (!Online(e.TargetUid)) { why = "target offline"; return false; }
                    var relay = new ZPackage();
                    relay.Write(p[0]);
                    relay.Write(-applied);
                    relay.Write("");
                    relay.Write(by);
                    ZRoutedRpc.instance.InvokeRoutedRPC(e.TargetUid, "AP_SkillRaise", relay);
                    return true;
                }
                case "effect":
                    if (!Online(e.TargetUid)) { why = "target offline"; return false; }
                    ZRoutedRpc.instance.InvokeRoutedRPC(e.TargetUid, "AP_W9RemoveSE", Wave9.ParseInt(e.Data, 0));
                    return true;
                case "ban":
                    CompanionPlugin.FeatureUnban(by, e.Data);
                    return true;
                case "piece_rm":
                {
                    var man = ZDOMan.instance;
                    if (man == null) { why = "no world"; return false; }
                    var o = DecodePiece(e.Data, out why);
                    if (o == null) return false;
                    if (!Wave9Wdiff.Recreate(man, o)) { why = "recreate failed"; return false; }
                    return true;
                }
                case "piece_mv":
                {
                    ZDOID zid; Vector3 pos; Quaternion rot;
                    if (!TryParseMove(e.Data, out zid, out pos, out rot)) { why = "bad record"; return false; }
                    var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(zid) : null;
                    if (zdo == null) { why = "piece is gone"; return false; }
                    if (!IsBuildPiece(zdo.GetPrefab())) { why = "not a building piece"; return false; }
                    var prev = zdo.GetOwner();
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    zdo.SetPosition(pos);
                    zdo.SetRotation(rot);
                    try { ZDOMan.instance.ForceSendZDO(zid); } catch (Exception) { }
                    if (prev != 0L && prev != ZDOMan.GetSessionID()) zdo.SetOwner(prev);
                    var local = new ZPackage();
                    local.Write(Wave9.Ver);
                    local.Write(zid);
                    local.Write(pos);
                    local.Write(rot);
                    Wave9.Reply(by, "AP_UndoPieceLocal", local);
                    return true;
                }
                default:
                    why = "not reversible";
                    return false;
            }
        }

        private static bool Online(long uid) => uid != 0L && (Wave9.IsHostUid(uid) || Wave9.PeerByUid(uid) != null);

        // The target's client says how many stamped copies it found and removed.
        private static void OnTakenBack(long sender, ZPackage pkg)
        {
            if (!Wave9.IsServer) return;
            string serial, prefab; int removed, asked;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                serial = pkg.ReadString();
                prefab = pkg.ReadString();
                removed = pkg.ReadInt();
                asked = pkg.ReadInt();
            }
            catch (Exception) { return; }
            var key = sender.ToString(CultureInfo.InvariantCulture) + "|" + serial;
            KeyValuePair<long, float> pending;
            if (!TakeBackPending.TryGetValue(key, out pending)) return;   // only answers the server asked for
            TakeBackPending.Remove(key);
            if (Time.time > pending.Value) return;
            var who = NameOf(sender);
            Wave9.Tell(pending.Key, removed >= asked
                ? $"Undo: took back {removed}x {Wave9.Clean(prefab, 64)} from {who}."
                : $"Undo: {who} still held {removed} of the {asked}x {Wave9.Clean(prefab, 64)} from that grant; took back what was left (their own items were not touched).");
        }

        // ==================== client side (the TARGET's companion) ====================

        // Status effects the server's apply ADDED (they were not active before): only those may be removed by an undo,
        // so undoing "applied Rested" never strips a Rested the player earned themselves.
        private static readonly HashSet<int> AdminAddedSE = new HashSet<int>();

        internal static void NoteEffectApplying(SEMan seman, int hash, out bool hadBefore)
        {
            hadBefore = false;
            try { hadBefore = seman != null && seman.HaveStatusEffect(hash); } catch (Exception) { }
        }

        internal static void NoteEffectApplied(int hash, bool hadBefore)
        {
            if (!hadBefore) AdminAddedSE.Add(hash);
        }

        private static void OnRemoveSE(long sender, int hash)
        {
            if (!Wave9.FromServer(sender)) return;
            var p = Player.m_localPlayer;
            if (p == null || hash == 0) return;
            if (!AdminAddedSE.Remove(hash)) return;   // it was already active (or long gone): leave the player's own effect
            try { p.GetSEMan().RemoveStatusEffect(hash, true); } catch (Exception) { }
        }

        internal static float SkillLevelOf(Player player, string skillName)
        {
            try
            {
                var skills = player != null ? player.GetSkills() : null;
                if (skills == null) return float.NaN;
                // GetSkillList only: Skills.GetSkill would CREATE a missing skill in the save.
                foreach (var s in skills.GetSkillList())
                    if (s?.m_info != null && string.Equals(s.m_info.m_skill.ToString(), skillName, StringComparison.OrdinalIgnoreCase))
                        return s.m_level;
                return 0f;   // not learned yet: CheatRaiseSkill creates it from 0
            }
            catch (Exception) { return float.NaN; }
        }

        internal static void ReportSkillApplied(Player player, string skillName, float before)
        {
            if (float.IsNaN(before) || ZRoutedRpc.instance == null) return;
            var after = SkillLevelOf(player, skillName);
            if (float.IsNaN(after)) return;
            var pkg = new ZPackage();
            pkg.Write(Wave9.Ver);
            pkg.Write(skillName);
            pkg.Write(before);
            pkg.Write(after);
            ZRoutedRpc.instance.InvokeRoutedRPC("AP_SrvSkillApplied", pkg);
        }

        internal static bool SerialMatches(string stamp, string serial)
        {
            var parts = (stamp ?? "").Split('|');
            if (parts.Length < 2 || string.IsNullOrEmpty(serial)) return false;
            return parts[1] == serial || parts[1].StartsWith(serial + ".", StringComparison.Ordinal);
        }

        // Give undo on the target: remove only copies stamped with that grant's serial, never the player's own items.
        private static void OnTakeBack(long sender, ZPackage pkg)
        {
            if (!Wave9.FromServer(sender)) return;
            var player = Player.m_localPlayer;
            if (player == null) return;
            string prefab, serial; int amount;
            try
            {
                if (pkg.ReadInt() != Wave9.Ver) return;
                prefab = pkg.ReadString();
                serial = pkg.ReadString();
                amount = pkg.ReadInt();
            }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(serial) || amount <= 0) return;
            var removed = 0;
            try
            {
                var inv = player.GetInventory();
                foreach (var it in new List<ItemDrop.ItemData>(inv.GetAllItems()))
                {
                    if (removed >= amount) break;
                    if (it?.m_dropPrefab == null || it.m_dropPrefab.name != prefab || it.m_customData == null) continue;
                    string stamp;
                    if (!it.m_customData.TryGetValue(Wave9Prov.CustomKey, out stamp) || !SerialMatches(stamp, serial)) continue;
                    var take = Math.Min(it.m_stack, amount - removed);
                    if (take >= it.m_stack)
                    {
                        if (it.m_equipped) player.UnequipItem(it, false);
                        inv.RemoveItem(it);
                    }
                    else inv.RemoveItem(it, take);
                    removed += take;
                }
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Undo take-back failed: {e.Message}"); }
            if (ZRoutedRpc.instance == null) return;
            var reply = new ZPackage();
            reply.Write(Wave9.Ver);
            reply.Write(serial);
            reply.Write(prefab);
            reply.Write(removed);
            reply.Write(amount);
            ZRoutedRpc.instance.InvokeRoutedRPC("AP_SrvTakenBack", reply);
        }
    }
}