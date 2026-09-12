using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #10 tame roster and pet manager (server side) ====================
    // World-wide, frame-spread scan for creature ZDOs whose persisted "tamed" flag is set (any Character
    // prefab, not only Tameable ones: the panel's own spawner can tame anything), then per-creature
    // actions that re-validate the ZDO before touching it. See Wave8SrvObjectsCore.cs for the ownership
    // rules every action obeys.
    //
    //   AP_SrvTameScanReq  {int ver, bool startNew, Vector3 origin, int max}
    //   AP_TameRoster      {int ver, bool running, int scanned, int total, int found, int shipped(<=100),
    //                       shipped x (ZDOID id, string prefab, string nameToken, string petName,
    //                                  string owner, int level, float health, float maxHealth,
    //                                  Vector3 pos, float dist), long scanMillis}
    //   AP_SrvTameAction   {int ver, int action(0 heal, 1 rename, 2 untame, 3 cull), ZDOID id, string text}
    //   AP_SrvTameCullArea {int ver, string prefab, Vector3 center, float radius}
    internal static partial class Wave8Objects
    {
        private sealed class TameRow
        {
            public ZDOID Id;
            public string Prefab = "";
            public string Token = "";
            public string PetName = "";
            public string Author = "";
            public int Level;
            public float Health;
            public float MaxHealth;
            public Vector3 Pos;
            public float Dist;
        }

        // ==================== AP_SrvTameScanReq ====================

        private static void OnTameScanReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvTameScanReq")) return;

            int ver, max; bool startNew; Vector3 origin;
            try
            {
                ver = pkg.ReadInt();
                startNew = pkg.ReadBool();
                origin = pkg.ReadVector3();
                max = pkg.ReadInt();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvTameScanReq: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (!PosOk(origin)) origin = Vector3.zero;
            max = Mathf.Clamp(max, 1, TameRowCap);

            if (_job != null)
            {
                if (_job.Kind == KindTames)
                {
                    // Progress poll (or a second Scan click) while the roster scan runs: running=true + progress.
                    SendTameRoster(_job, sender, true);
                    return;
                }
                // The scanner is busy with the container search; say so instead of spinning the panel.
                CompanionPlugin.NotifySender(sender, "A world scan is already running - try again in a moment.");
                SendTameRoster(_lastTame, sender, false);
                return;
            }
            if (!startNew)
            {
                // A poll that arrived after the scan finished: answer with the finished roster, never start
                // another world sweep on a timer.
                SendTameRoster(_lastTame, sender, false);
                return;
            }

            var job = NewJob(KindTames, sender);
            if (job == null) { SendTameRoster(null, sender, false); return; }
            job.Origin = origin;
            job.TopN = max;
            job.Tames = new List<TameRow>();
            _job = job;
        }

        private static void TameProcess(ScanJob job, ZDO zdo, int hash)
        {
            var info = InfoOf(hash);
            if (info == null || !info.IsCharacter) return;      // cheap prefab test before any ZDO data lookup
            if (!zdo.GetBool(KeyTamed)) return;
            job.Matched++;
            if (job.Tames.Count >= CandidateCap) return;        // counted, not stored: the cap is 100 rows anyway

            var pos = zdo.GetPosition();
            var level = zdo.GetInt(KeyLevel, 1);
            if (level < 1) level = 1;
            // max_health is written by the owning client's SetupMaxHealth (base * level); a creature that
            // was never simulated carries no entry, so the same formula is the default.
            var max = zdo.GetFloat(KeyMaxHealth, info.BaseHealth * level);
            var health = zdo.GetFloat(KeyHealth, max);
            job.Tames.Add(new TameRow
            {
                Id = zdo.m_uid,
                Prefab = info.Name,
                Token = info.NameToken ?? "",
                PetName = zdo.GetString(KeyTamedName, "") ?? "",
                Author = zdo.GetString(KeyTamedNameAuthor, "") ?? "",
                Level = level,
                Health = health,
                MaxHealth = max,
                Pos = pos,
                Dist = Vector3.Distance(pos, job.Origin),
            });
        }

        private static void TameFinish(ScanJob job)
        {
            if (job.Tames != null)
            {
                job.Tames.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                var keep = Mathf.Clamp(job.TopN, 1, TameRowCap);
                if (job.Tames.Count > keep) job.Tames.RemoveRange(keep, job.Tames.Count - keep);
            }
            _lastTame = job;
            CompanionPlugin.FeatureLog($"Tame roster for {CompanionPlugin.SenderDisplayName(job.Requester)}: {job.Matched} tamed creature(s) among {job.Scanned} ZDOs in {job.Watch.ElapsedMilliseconds} ms.");
            SendTameRoster(job, job.Requester, false);
        }

        private static void SendTameRoster(ScanJob job, long uid, bool running)
        {
            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(running);
            pkg.Write(job != null ? job.Scanned : 0);
            pkg.Write(job != null ? job.Total : TotalZdos());
            pkg.Write(job != null ? job.Matched : 0);

            var rows = !running && job != null ? job.Tames : null;
            var n = rows != null ? Math.Min(rows.Count, TameRowCap) : 0;
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var r = rows[i];
                pkg.Write(r.Id);
                pkg.Write(r.Prefab ?? "");
                pkg.Write(r.Token ?? "");
                pkg.Write(Clean(r.PetName, MaxPetNameLen));
                pkg.Write(Clean(AuthorLabel(r.Author), 64));
                pkg.Write(r.Level);
                pkg.Write(r.Health);
                pkg.Write(r.MaxHealth);
                pkg.Write(r.Pos);
                pkg.Write(r.Dist);
            }
            pkg.Write(job != null ? job.Watch.ElapsedMilliseconds : 0L);
            Reply(uid, "AP_TameRoster", pkg);
        }

        // ==================== AP_SrvTameAction ====================

        private static void OnTameAction(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvTameAction")) return;

            int ver, action; ZDOID id; string text;
            try
            {
                ver = pkg.ReadInt();
                action = pkg.ReadInt();
                id = pkg.ReadZDOID();
                text = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvTameAction: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;
            if (action < 0 || action > 3) return;

            if (!TameActionsEnabled)
            {
                CompanionPlugin.NotifySender(sender, "Tame actions are disabled in the server config (EnableTameManager=false).");
                return;
            }
            var man = ZDOMan.instance;
            var zdo = man != null ? man.GetZDO(id) : null;
            if (zdo == null || !zdo.IsValid())
            {
                CompanionPlugin.NotifySender(sender, "That creature no longer exists - scan again.");
                return;
            }
            // Re-validate against the live ZDO: the roster the admin clicked may be minutes old, and these
            // actions must never land on an arbitrary object id.
            var info = InfoOf(zdo.GetPrefab());
            if (info == null || !info.IsCharacter || !zdo.GetBool(KeyTamed))
            {
                CompanionPlugin.NotifySender(sender, "That creature is not tamed (any more) - scan again.");
                return;
            }
            var pet = Clean(zdo.GetString(KeyTamedName, ""), MaxPetNameLen);
            var label = pet.Length > 0 ? $"{pet} ({info.Name})" : info.Name;
            var admin = Wave1AuditRpc.AdminLabel(sender);
            string how;

            try
            {
                switch (action)
                {
                    case 0:
                        how = TameHeal(zdo, info);
                        CompanionPlugin.SrvAudit(sender, "TAME_HEAL", $"{id}|{info.Name}|{pet}|{how}");
                        CompanionPlugin.NotifySender(sender, $"Healed {label} ({how}).");
                        break;
                    case 1:
                        var name = Clean(text, MaxPetNameLen);
                        if (name.Length == 0)
                        {
                            CompanionPlugin.NotifySender(sender, "Rename rejected: the new name is empty.");
                            return;
                        }
                        how = TameRename(zdo, name);
                        CompanionPlugin.SrvAudit(sender, "TAME_RENAME", $"{id}|{info.Name}|{pet}->{name}|{how}");
                        CompanionPlugin.NotifySender(sender, $"Renamed {label} to '{name}' ({how}).");
                        break;
                    case 2:
                        how = TameUntame(zdo);
                        CompanionPlugin.SrvAudit(sender, "TAME_UNTAME", $"{id}|{info.Name}|{pet}|{how}");
                        CompanionPlugin.NotifySender(sender, $"Un-tamed {label} ({how}) - it is wild again.");
                        break;
                    default:
                        DestroyNow(zdo);
                        CompanionPlugin.SrvAudit(sender, "TAME_CULL", $"{id}|{info.Name}|{pet}|pos={zdo.GetPosition()}");
                        Wave1AuditRpc.PostModLog($"TAME CULL {admin} removed {label}");
                        CompanionPlugin.NotifySender(sender, $"Culled {label}.");
                        break;
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvTameAction {action} on {id} failed: {e.Message}");
                CompanionPlugin.NotifySender(sender, "The action failed on the server - see the server log.");
                return;
            }
            CompanionPlugin.FeatureLog($"Tame action {action} by {admin} on {label} ({id})");
        }

        // Heal to full. Three routes, in order (see the ownership rules in Wave8SrvObjectsCore.cs):
        //   host with a live instance -> Character.SetHealth in-process after ClaimOwnership;
        //   connected owning client   -> Character.RPC_Heal on that client (it clamps to its own max);
        //   nobody owns it            -> raw ZDO write, then hand back.
        private static string TameHeal(ZDO zdo, PrefabInfo info)
        {
            var view = LocalView(zdo);
            if (view != null)
            {
                var ch = view.GetComponent<Character>();
                if (ch != null)
                {
                    view.ClaimOwnership();
                    ch.SetHealth(ch.GetMaxHealth());
                    return "local";
                }
            }
            var owner = ConnectedOwner(zdo);
            if (owner != 0L)
            {
                // RPC_Heal(hp, showText): Mathf.Min(health + hp, GetMaxHealth()) — a huge hp means "to full".
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_Heal", 1000000000f, false);
                return "via owning client";
            }
            var level = Math.Max(1, zdo.GetInt(KeyLevel, 1));
            var max = zdo.GetFloat(KeyMaxHealth, info.BaseHealth * level);
            var prev = ClaimForWrite(zdo);
            zdo.Set(KeyHealth, max);
            HandBack(zdo, prev);
            return "zdo";
        }

        // Rename. The author field is what the game uses for UGC filtering on other clients; "host" is the
        // value the game itself writes for an unsigned local user and is filtered as "no specific author",
        // so it can never break another client's name lookup the way a malformed platform id could.
        private static string TameRename(ZDO zdo, string name)
        {
            const string author = "host";
            var owner = ConnectedOwner(zdo);
            if (owner != 0L && LocalView(zdo) == null)
            {
                // Tameable.RPC_SetName runs on the owner and requires IsOwner && IsTamed — both hold there.
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "SetName", name, author);
                return "via owning client";
            }
            var view = LocalView(zdo);
            if (view != null) view.ClaimOwnership();
            var prev = ClaimForWrite(zdo);
            zdo.Set(KeyTamedName, name);
            zdo.Set(KeyTamedNameAuthor, author);
            if (view != null) { try { ZDOMan.instance?.ForceSendZDO(zdo.m_uid); } catch (Exception) { } }
            else HandBack(zdo, prev);
            return view != null ? "local" : "zdo";
        }

        // Un-tame. Character caches m_tamed on the OWNER and only re-reads the ZDO when it is not the owner
        // (Character.IsTamed(float), 1 s cadence), so an owned creature must be told through RPC_SetTamed;
        // an unowned one takes the raw write and the next client to load it reads the flag in Awake.
        private static string TameUntame(ZDO zdo)
        {
            var view = LocalView(zdo);
            if (view != null)
            {
                var ch = view.GetComponent<Character>();
                if (ch != null)
                {
                    view.ClaimOwnership();
                    ch.SetTamed(false);
                    return "local";
                }
            }
            var owner = ConnectedOwner(zdo);
            if (owner != 0L)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_SetTamed", false);
                return "via owning client";
            }
            var prev = ClaimForWrite(zdo);
            zdo.Set(KeyTamed, false);
            HandBack(zdo, prev);
            return "zdo";
        }

        // ==================== AP_SrvTameCullArea ====================

        // Breeding-pen cleanup: every TAMED creature of one prefab within `radius` (XZ, like wards) of
        // `center`, queued for frame-spread deletion. Owner-only when roles are on (registered with a null
        // grant) because it is a mass removal.
        private static void OnTameCullArea(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvTameCullArea")) return;

            int ver; string prefab; Vector3 center; float radius;
            try
            {
                ver = pkg.ReadInt();
                prefab = pkg.ReadString();
                center = pkg.ReadVector3();
                radius = pkg.ReadSingle();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvTameCullArea: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            if (!TameActionsEnabled)
            {
                CompanionPlugin.NotifySender(sender, "Tame actions are disabled in the server config (EnableTameManager=false).");
                return;
            }
            prefab = Clean(prefab, 64);
            if (prefab.Length == 0 || !PosOk(center) || float.IsNaN(radius) || float.IsInfinity(radius))
            {
                CompanionPlugin.NotifySender(sender, "Cull rejected: pick a species (scan and select a row first) and a valid radius.");
                return;
            }
            radius = Mathf.Clamp(radius, 1f, MaxCullRadius);
            var hash = prefab.GetStableHashCode();
            var info = InfoOf(hash);
            if (info == null || !info.IsCharacter)
            {
                CompanionPlugin.NotifySender(sender, $"Cull rejected: '{prefab}' is not a creature prefab on this server.");
                return;
            }

            var zoneSize = 64f;
            try { if (ZoneSystem.instance != null && ZoneSystem.instance.m_zoneSize > 1f) zoneSize = ZoneSystem.instance.m_zoneSize; }
            catch (Exception) { }
            var zones = Mathf.Clamp(Mathf.CeilToInt(radius / zoneSize), 1, MaxZoneBlock);
            if (!CollectBlock(center, zones, Scratch))
            {
                CompanionPlugin.NotifySender(sender, "Cull unavailable: the server could not read the world object store.");
                return;
            }

            var matched = 0;
            var queued = 0;
            for (var i = 0; i < Scratch.Count; i++)
            {
                var zdo = Scratch[i];
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid() || zdo.GetPrefab() != hash) continue;
                    if (!zdo.GetBool(KeyTamed)) continue;
                    if (DistXZ(zdo.GetPosition(), center) > radius) continue;
                    matched++;
                    if (queued >= CullAreaCap) continue;
                    if (QueueDelete(zdo.m_uid)) queued++;
                }
                catch (Exception) { }
            }
            Scratch.Clear();

            var admin = Wave1AuditRpc.AdminLabel(sender);
            var detail = $"prefab={info.Name} center={center} radius={radius:0} matched={matched} queued={queued}";
            CompanionPlugin.SrvAudit(sender, "TAME_CULL_AREA", detail);
            CompanionPlugin.FeatureLog($"Tame cull-area by {admin}: {detail}");
            if (queued == 0)
                CompanionPlugin.NotifySender(sender, $"No tamed {info.Name} within {radius:0} m of that point.");
            else
            {
                Wave1AuditRpc.PostModLog($"TAME CULL {admin} removed {queued} tamed {info.Name} within {radius:0} m");
                CompanionPlugin.NotifySender(sender, queued < matched
                    ? $"Culling {queued} of {matched} tamed {info.Name} (per-request cap {CullAreaCap}) - run it again for the rest."
                    : $"Culling {queued} tamed {info.Name} within {radius:0} m.");
            }
        }
    }
}
