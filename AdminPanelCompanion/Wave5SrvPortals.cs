using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 5 — portal network + prefab/location spawner (server side) ====================
    //
    // Four admin RPCs, all cheap and bounded — none of them sweeps the world, so none of them needs the
    // frame-spread scanner Wave2SrvWorld.cs runs:
    //
    //   AP_SrvPortalListReq   -> AP_PortalList     read-only, ZDOMan.GetPortals() (a live, already-built list)
    //   AP_SrvPortalSet                            re-tag one portal by position
    //   AP_SrvPrefabSpawnReq                       spawn ONE arbitrary prefab, owner-only, rate limited
    //   AP_SrvLocationListReq -> AP_LocationList   read-only name list from ZoneSystem.m_locations
    //
    // ---------------------------------------------------------------------------------------------------
    // PORTALS (all line numbers refer to the decompiled game sources under spec/decompile/)
    //
    // * Enumeration: ZDOMan keeps `private readonly List<ZDO> m_portalObjects` (ZDOMan.cs:74) and exposes it
    //   as `public List<ZDO> GetPortals()` (ZDOMan.cs:1198-1201). It is filled on world load (ZDOMan.cs:329-337),
    //   on ZDO creation (ZDOMan.cs:394-401) and on receive (ZDOMan.cs:828-831), always tested against
    //   Game.instance.PortalPrefabHash — so it covers BOTH portal prefabs ("portal_wood" and the ancient
    //   "portal") without hardcoding a name, and it costs nothing to read. We copy it before touching
    //   anything, because it is the live list.
    //
    // * Tag: ZDOVars.s_tag ("tag", ZDOVars.cs:281). Author: ZDOVars.s_tagauthor (ZDOVars.cs:283), used only to
    //   pick the UGC censor's user id in TeleportWorld.GetText (TeleportWorld.cs:145-147); an empty author is
    //   the sanctioned "no user" value (PlatformUserID.None), which is exactly right for a server-side edit.
    //
    // * Pairing is a ZDO CONNECTION, not a var: zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)
    //   (ZDO.cs:686, TeleportWorld.cs:126) / zdo.SetConnection(...) (ZDO.cs:360). The legacy
    //   ZDOVars.s_toRemoveTarget appears ONLY in the pre-v31 conversion path (ZDOMan.ConvertPortals,
    //   ZDOMan.cs:1342-1372) and is never read or written here.
    //
    // * WHY A RE-TAG NEEDS NO CLIENT RPC (this was verified end to end, not assumed):
    //     1. zdo.Set(...) bumps DataRevision (ZDO.cs:417-423 -> IncreaseDataRevision, ZDO.cs:518).
    //     2. ZDOMan.ForceSendZDO(uid) (ZDOMan.cs:1251-1257) queues the ZDO for EVERY peer, and
    //        AddForceSendZdos (ZDOMan.cs:924-940) appends it to the sync list irrespective of sector/area —
    //        so even a client whose active area does not cover the portal receives it.
    //     3. The receiving client applies any packet with a higher DataRevision (ZDOMan.RPC_ZDOData,
    //        ZDOMan.cs:802-827). There is NO "ignore updates for ZDOs I own" branch, so stealing ownership
    //        first (which the write requires anyway) makes the server's copy authoritative.
    //     4. TeleportWorld never caches the tag: GetText() reads ZDOVars.s_tag from the ZDO on every call
    //        (TeleportWorld.cs:138-148) and UpdatePortal re-reads the connection twice a second
    //        (TeleportWorld.cs:43,78-96). So the hover text and the connected/unconnected glow follow the
    //        ZDO by themselves.
    //   Conclusion: writing the ZDO IS sufficient; RPC_SetTag is a CLIENT->owner path and is not needed (and
    //   would be worse — it is silently dropped when the owning client has no instance of that portal).
    //
    // * RE-PAIRING IS THE GAME'S JOB, NOT OURS. The server runs Game.ConnectPortalsCoroutine every 5 s
    //   (Game.cs:258-263, 693-700): ConnectPortals (Game.cs:702-740) first breaks every connection whose
    //   partner carries a different tag, then pairs unconnected same-tag portals at random. We therefore do
    //   exactly what vanilla's own RPC_SetTag does (TeleportWorld.cs:165-180) — drop this portal's connection
    //   and its partner's — and let the engine re-pair. We never hand-build a pairing: doing so would fight
    //   the coroutine and could produce the one state vanilla treats as corrupt (A->B while B->C).
    //
    // ---------------------------------------------------------------------------------------------------
    // PREFAB SPAWNER
    //
    // Uses the EXACT mechanism the shipping creature spawner uses (CompanionPlugin.OnServerSpawn,
    // CompanionPlugin.cs:203-275): resolve via ZNetScene.GetPrefab / ObjectDB.GetItemPrefab, then
    // UnityEngine.Object.Instantiate on the server, whose ZNetView.Awake creates the ZDO server-owned and
    // ZDOMan replicates it to clients. Prefab ASSETS do exist on a dedicated server (the give/spawn path has
    // been reading their components since 2.0); what does not exist is a scene full of world GameObjects.
    //
    // LOCATIONS are listed but never placed — see the class-level note on OnLocationListReq and the
    // limitations returned with this file.
    internal static class Wave5Portals
    {
        private const int Ver = 1;              // wire version for AP_PortalList / AP_LocationList

        private const int PortalCap = 60;       // AP_PortalList shipped cap (contract)
        private const int LocationCap = 60;     // AP_LocationList shipped cap (contract)
        private const int TagMaxLen = 10;       // vanilla UI cap: TextInput.RequestText(..., 10), TeleportWorld.cs:69
        private const int MaxNameLen = 64;      // longest prefab/filter string we will even look at
        private const float PortalMatchRadius = 3f;   // how close the clicked position must be to a portal
        private const int SpawnsPerMinute = 10; // hard per-admin ceiling (contract)
        private const float SpawnWindowSec = 60f;
        private const int SuggestionCount = 3;
        private const float WorldEdge = 25000f; // generous outer bound; the map itself ends near 10.5 km

        // ---- config ----
        private static ConfigEntry<bool> _enableRetag;
        private static ConfigEntry<bool> _enableSpawner;

        private static bool RetagEnabled => _enableRetag == null || _enableRetag.Value;
        private static bool SpawnerEnabled => _enableSpawner == null || _enableSpawner.Value;

        // ---- prefab registry cache (rebuilt when ZNetScene changes, i.e. on world reload) ----
        private static object _prefabScene;
        private static Dictionary<string, string> _prefabByLowerName;   // lower-case -> real prefab name
        private static List<string> _prefabNames;

        // ---- location registry cache (rebuilt when ZoneSystem changes) ----
        private static object _locationZoneSystem;
        private static List<string> _locationNames;
        private static HashSet<string> _locationNameSet;

        // ---- spawn rate limiter: admin key -> unscaled timestamps of recent spawns ----
        private static readonly Dictionary<string, List<float>> SpawnStamps =
            new Dictionary<string, List<float>>(StringComparer.Ordinal);
        private static float _nextPrune;

        // ==================== lifecycle ====================

        internal static void Init()
        {
            var cfg = CompanionPlugin.Instance != null ? CompanionPlugin.Instance.Config : null;
            if (cfg != null)
            {
                _enableRetag = cfg.Bind("Features", "EnablePortalRetag", true,
                    "Allow admins to rename portals from the panel. The server rewrites the portal's tag ZDO and lets the game's own 5-second portal-pairing pass re-connect same-tag portals. Turn off to make the portal list read-only.");
                _enableSpawner = cfg.Bind("Features", "EnablePrefabSpawner", true,
                    "Allow admins to spawn an arbitrary networked prefab by name at a position. Owner-only when tiered roles are on, audited, one object per request, at most 10 per minute per admin. Turn off on servers where only the vetted item/creature spawner should be reachable.");
            }

            // Portal reads and re-tags are moderator-grade. The prefab spawner can put ANY object into the
            // world, so it stays owner-only (null grant) exactly like world cleanup does in wave 2.
            CompanionPlugin.RegisterAuditedRpc("AP_SrvPortalListReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvPortalSet", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvLocationListReq", "moderator");
            CompanionPlugin.RegisterAuditedRpc("AP_SrvPrefabSpawnReq", null);

            try
            {
                Harmony.CreateAndPatchAll(typeof(Wave5PortalRpcRegistration));
                Wave2Ops.ReportPatch("Wave5Portals.RpcRegistration", true);
            }
            catch (Exception e)
            {
                Wave2Ops.ReportPatch("Wave5Portals.RpcRegistration", false);
                CompanionPlugin.FeatureLog($"Wave5Portals RPC registration patch failed (portal management and the prefab spawner are unavailable): {e.Message}");
            }
        }

        // Nothing here runs per frame; the only periodic work is expiring rate-limiter entries so a long-lived
        // server does not keep one small list per admin who ever spawned something.
        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var now = Time.unscaledTime;
            if (now < _nextPrune) return;
            _nextPrune = now + 60f;
            try { PruneSpawnStamps(now); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"Prefab-spawn rate-limiter prune failed: {e.Message}"); }
        }

        // ==================== RPC registration ====================

        [HarmonyPatch(typeof(ZNet), "Awake")]
        internal static class Wave5PortalRpcRegistration
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                try
                {
                    // No-arg requests must use the Action<long> form — Register<T> needs a payload type.
                    ZRoutedRpc.instance.Register("AP_SrvPortalListReq", new Action<long>(OnPortalListReq));
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvPortalSet", OnPortalSet);
                    ZRoutedRpc.instance.Register<ZPackage>("AP_SrvPrefabSpawnReq", OnPrefabSpawnReq);
                    ZRoutedRpc.instance.Register<string>("AP_SrvLocationListReq", OnLocationListReq);
                }
                catch (Exception e)
                {
                    CompanionPlugin.FeatureLog($"Wave5 portal/spawner RPC registration failed: {e.Message}");
                }
            }
        }

        // ==================== 1. portal list ====================

        /// <summary>
        /// AP_SrvPortalListReq -> AP_PortalList
        /// {int ver, int total, int shipped(&lt;=60), shipped x (float x, float y, float z, string tag,
        ///  bool connected, string targetTag)}.
        /// Nearest-first when the requester's reference position is resolvable, otherwise ZDOMan's own order.
        /// </summary>
        internal static void OnPortalListReq(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvPortalListReq")) return;

            var rows = new List<PortalRow>();
            var total = 0;
            try { total = CollectPortals(sender, rows); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Portal list failed: {e.Message}");
                CompanionPlugin.NotifySender(sender, "The server could not read the portal list on this game build.");
            }

            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(total);
            var n = Math.Min(rows.Count, PortalCap);
            pkg.Write(n);
            for (var i = 0; i < n; i++)
            {
                var r = rows[i];
                pkg.Write(r.Pos.x);
                pkg.Write(r.Pos.y);
                pkg.Write(r.Pos.z);
                pkg.Write(r.Tag ?? "");
                pkg.Write(r.Connected);
                pkg.Write(r.TargetTag ?? "");
            }
            Reply(sender, "AP_PortalList", pkg);
        }

        private struct PortalRow
        {
            public Vector3 Pos;
            public string Tag;
            public bool Connected;
            public string TargetTag;
            public float Dist2;
        }

        // Returns the total number of live portals; fills rows (already sorted, already trimmed to the cap).
        private static int CollectPortals(long sender, List<PortalRow> rows)
        {
            var man = ZDOMan.instance;
            if (man == null) return 0;

            // GetPortals() hands back the LIVE list (ZDOMan.cs:1198-1201). Copy it before doing anything else:
            // reading a tag cannot mutate it today, but a mod (or a future engine change) that creates a ZDO
            // mid-walk would invalidate the enumerator, and this list is small enough that a copy is free.
            var snapshot = ZoneCompat.PortalsSnapshot(man);   // per-sector table since 1.0.12, flattened copy
            if (snapshot.Count == 0) return 0;

            Vector3 origin;
            var haveOrigin = TryRequesterPos(sender, out origin);

            var total = 0;
            foreach (var zdo in snapshot)
            {
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    total++;

                    var pos = zdo.GetPosition();
                    var row = new PortalRow
                    {
                        Pos = pos,
                        Tag = zdo.GetString(ZDOVars.s_tag, ""),
                        Connected = false,
                        TargetTag = "",
                        Dist2 = 0f,
                    };
                    if (haveOrigin)
                    {
                        var dx = pos.x - origin.x;
                        var dz = pos.z - origin.z;
                        row.Dist2 = dx * dx + dz * dz;
                    }

                    // A connection id that no longer resolves is a DANGLING link (the partner was destroyed
                    // between the two ZDO writes). Reporting it as "connected" would be a lie, so the row says
                    // unconnected with an empty target; the game's own ConnectPortals pass clears it within 5 s.
                    var targetId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                    if (!targetId.IsNone())
                    {
                        var target = man.GetZDO(targetId);
                        if (target != null && target.IsValid())
                        {
                            row.Connected = true;
                            row.TargetTag = target.GetString(ZDOVars.s_tag, "");
                        }
                    }
                    rows.Add(row);
                }
                catch (Exception)
                {
                    // One malformed portal ZDO must never cost the admin the whole list.
                }
            }

            if (haveOrigin) rows.Sort((a, b) => a.Dist2.CompareTo(b.Dist2));
            if (rows.Count > PortalCap) rows.RemoveRange(PortalCap, rows.Count - PortalCap);
            return total;
        }

        // ==================== 2. portal re-tag ====================

        /// <summary>
        /// AP_SrvPortalSet(ZPackage{float x, float y, float z, string newTag}).
        /// Identifies the portal by position (the panel sends a row it got from AP_PortalList), claims
        /// ownership, writes the tag, drops both halves of the old pairing and force-sends the ZDOs.
        /// </summary>
        internal static void OnPortalSet(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvPortalSet")) return;

            float x, y, z;
            string rawTag;
            try
            {
                x = pkg.ReadSingle();
                y = pkg.ReadSingle();
                z = pkg.ReadSingle();
                rawTag = pkg.ReadString();
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPortalSet: malformed packet dropped ({e.Message})");
                return;
            }

            if (!RetagEnabled)
            {
                CompanionPlugin.NotifySender(sender, "Portal renaming is disabled in the server config (EnablePortalRetag=false).");
                return;
            }
            if (!FinitePosition(x, y, z))
            {
                CompanionPlugin.NotifySender(sender, "Portal position out of range.");
                return;
            }

            bool truncated;
            var tag = SanitizeTag(rawTag, out truncated);

            var man = ZDOMan.instance;
            if (man == null) { CompanionPlugin.NotifySender(sender, "The world is not loaded yet."); return; }

            ZDO portal;
            try { portal = FindPortalNear(new Vector3(x, y, z)); }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPortalSet: portal lookup failed ({e.Message})");
                CompanionPlugin.NotifySender(sender, "The server could not read the portal list on this game build.");
                return;
            }
            if (portal == null)
            {
                CompanionPlugin.NotifySender(sender,
                    $"No portal within {PortalMatchRadius:0.#} m of that position - refresh the portal list and try again.");
                return;
            }

            var oldTag = "";
            try
            {
                oldTag = portal.GetString(ZDOVars.s_tag, "");
                if (oldTag == tag)
                {
                    CompanionPlugin.NotifySender(sender, $"That portal is already tagged '{tag}'.");
                    return;
                }

                var session = ZDOMan.GetSessionID();
                var partnerId = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);

                // Claim first: every ZDO write is only authoritative from the owner, and the owner revision is
                // what makes clients accept the ownership change alongside the data (ZDOMan.cs:802-826).
                portal.SetOwner(session);
                portal.Set(ZDOVars.s_tag, tag);
                // Author is the UGC-censor identity (TeleportWorld.cs:145-147). The server is not a platform
                // user, and empty == PlatformUserID.None, which is the documented "no author" value.
                portal.Set(ZDOVars.s_tagauthor, "");
                // Mirror vanilla's own re-tag (TeleportWorld.RPC_SetTag, TeleportWorld.cs:165-180): a renamed
                // portal loses its pairing immediately instead of pointing at a differently-tagged partner for
                // up to 5 seconds.
                portal.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
                man.ForceSendZDO(portal.m_uid);

                if (!partnerId.IsNone())
                {
                    var partner = man.GetZDO(partnerId);
                    if (partner != null && partner.IsValid())
                    {
                        partner.SetOwner(session);
                        partner.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
                        man.ForceSendZDO(partner.m_uid);
                    }
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPortalSet: write failed ({e.Message})");
                CompanionPlugin.NotifySender(sender, "Renaming that portal failed on the server - see the server log.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var detail = $"pos={x:0.#},{y:0.#},{z:0.#} old='{oldTag}' new='{tag}'";
            CompanionPlugin.FeatureLog($"Portal re-tagged by {admin}: {detail}");
            CompanionPlugin.SrvAudit(sender, "PORTALTAG", detail);

            var msg = $"Portal renamed '{oldTag}' -> '{tag}'.";
            if (truncated) msg += $" (Tags are capped at {TagMaxLen} characters, so it was shortened.)";
            if (tag.Length == 0) msg += " An empty tag pairs with other untagged portals.";
            msg += " The server re-pairs same-tag portals within about 5 seconds.";
            CompanionPlugin.NotifySender(sender, msg);
        }

        // Nearest portal within PortalMatchRadius (XZ distance plus a generous vertical tolerance, so a portal
        // on a different floor of the same building is never grabbed by accident).
        private static ZDO FindPortalNear(Vector3 pos)
        {
            var man = ZDOMan.instance;
            var portals = ZoneCompat.PortalsSnapshot(man);
            if (portals.Count == 0) return null;

            ZDO best = null;
            var bestDist2 = PortalMatchRadius * PortalMatchRadius;
            for (var i = 0; i < portals.Count; i++)
            {
                var zdo = portals[i];
                if (zdo == null) continue;
                try
                {
                    if (!zdo.IsValid()) continue;
                    var p = zdo.GetPosition();
                    if (Mathf.Abs(p.y - pos.y) > PortalMatchRadius * 2f) continue;
                    var dx = p.x - pos.x;
                    var dz = p.z - pos.z;
                    var d2 = dx * dx + dz * dz;
                    if (d2 > bestDist2) continue;
                    bestDist2 = d2;
                    best = zdo;
                }
                catch (Exception) { }
            }
            return best;
        }

        // Tags are rendered inside a single hover line and round-trip through ZPackage strings, so control
        // characters and the store's separator are stripped before the 10-character vanilla cap is applied.
        private static string SanitizeTag(string raw, out bool truncated)
        {
            truncated = false;
            if (raw == null) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (c == '\r' || c == '\n' || c == '\t' || c == '|') { sb.Append(' '); continue; }
                if (char.IsControl(c)) continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim();
            if (s.Length > TagMaxLen) { s = s.Substring(0, TagMaxLen).Trim(); truncated = true; }
            return s;
        }

        // ==================== 3. prefab spawner ====================

        /// <summary>
        /// AP_SrvPrefabSpawnReq(ZPackage{string prefabName, float x, float y, float z, float rotY}).
        /// One object per request, at most <see cref="SpawnsPerMinute"/> per minute per admin, owner-only when
        /// tiered roles are enforced, audited and mirrored to the mod log.
        /// </summary>
        internal static void OnPrefabSpawnReq(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvPrefabSpawnReq")) return;

            string rawName;
            float x, y, z, rotY;
            try
            {
                rawName = pkg.ReadString();
                x = pkg.ReadSingle();
                y = pkg.ReadSingle();
                z = pkg.ReadSingle();
                rotY = pkg.ReadSingle();
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPrefabSpawnReq: malformed packet dropped ({e.Message})");
                return;
            }

            if (!SpawnerEnabled)
            {
                CompanionPlugin.NotifySender(sender, "The prefab spawner is disabled in the server config (EnablePrefabSpawner=false).");
                return;
            }

            var name = (rawName ?? "").Trim();
            if (name.Length == 0 || name.Length > MaxNameLen)
            {
                CompanionPlugin.NotifySender(sender, $"Give a prefab name of 1-{MaxNameLen} characters.");
                return;
            }
            if (!FinitePosition(x, y, z) || float.IsNaN(rotY) || float.IsInfinity(rotY))
            {
                CompanionPlugin.NotifySender(sender, "Spawn position out of range.");
                return;
            }
            if (!RateLimitOk(sender))
            {
                CompanionPlugin.NotifySender(sender,
                    $"Spawn rate limit reached ({SpawnsPerMinute} per minute). Wait a moment and try again.");
                return;
            }

            // Locations (dungeons, boss altars, the merchant) are NOT prefabs you may instantiate: vanilla
            // places them during world generation, registers them in ZoneSystem.m_locationInstances and marks
            // the zone generated. Instantiating the root object at runtime produces a shell with no zone
            // registration, no interior scene and no terrain flattening. Refuse, loudly.
            if (IsLocationName(name))
            {
                CompanionPlugin.NotifySender(sender,
                    $"'{name}' is a world LOCATION, not a spawnable prefab. Locations are placed during world generation only; forcing one at runtime leaves the zone in an inconsistent state, so the spawner refuses.");
                return;
            }

            // Budget is consumed HERE, before the registry lookup, not on success: name resolution plus the
            // near-match ranking walks a few thousand strings, so a typo loop must be bounded too. Ten
            // attempts a minute is far more than any real admin needs.
            RecordSpawn(sender);

            GameObject prefab = null;
            var resolved = name;
            try
            {
                // Exact name first (hash lookup), then a case-insensitive match against the registry so
                // "beech1" finds "Beech1" instead of a bare failure. Same two-source resolution the shipping
                // creature/item spawner uses (CompanionPlugin.cs:223-224).
                prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                if (prefab == null)
                {
                    var index = PrefabIndex();
                    string real;
                    if (index != null && index.TryGetValue(name.ToLowerInvariant(), out real) &&
                        ZNetScene.instance != null)
                    {
                        prefab = ZNetScene.instance.GetPrefab(real);
                        if (prefab != null) resolved = real;
                    }
                }
                if (prefab == null && ObjectDB.instance != null) prefab = ObjectDB.instance.GetItemPrefab(name);
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPrefabSpawnReq: prefab lookup failed ({e.Message})");
            }

            if (prefab == null)
            {
                var near = NearMatches(name, SuggestionCount);
                CompanionPlugin.NotifySender(sender, near.Count > 0
                    ? $"Unknown prefab '{name}'. Did you mean: {string.Join(", ", near.ToArray())}?"
                    : $"Unknown prefab '{name}'.");
                return;
            }

            // A prefab with no ZNetView cannot become a ZDO: instantiating it would create a GameObject that
            // exists ONLY inside the server process, is invisible to every client and is never cleaned up.
            try
            {
                if (prefab.GetComponent<ZNetView>() == null)
                {
                    CompanionPlugin.NotifySender(sender,
                        $"'{resolved}' is not a networked object (no ZNetView), so the server cannot spawn it - it would exist only inside the server process.");
                    return;
                }
                if (prefab.GetComponent<Player>() != null)
                {
                    CompanionPlugin.NotifySender(sender, "Refusing to spawn a player object.");
                    return;
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPrefabSpawnReq: component probe failed ({e.Message})");
                CompanionPlugin.NotifySender(sender, "The server could not inspect that prefab; spawn refused.");
                return;
            }

            var pos = new Vector3(x, y, z);
            ZDOID uid;
            try
            {
                // EXACTLY the shipping mechanism (CompanionPlugin.OnServerSpawn, CompanionPlugin.cs:256-257):
                // Instantiate on the server; ZNetView.Awake creates the ZDO owned by this session and ZDOMan
                // replicates it. Rotation is yaw only — the panel sends a single angle.
                var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, rotY, 0f));
                var nview = go != null ? go.GetComponent<ZNetView>() : null;
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null)
                {
                    // No ZDO means nothing was networked; drop the local shell instead of leaking it.
                    if (go != null) UnityEngine.Object.Destroy(go);
                    CompanionPlugin.NotifySender(sender, $"'{resolved}' could not be created as a networked object; nothing was spawned.");
                    return;
                }
                uid = zdo.m_uid;
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"AP_SrvPrefabSpawnReq: spawning '{resolved}' failed ({e.Message})");
                CompanionPlugin.NotifySender(sender, $"Spawning '{resolved}' failed on the server - see the server log.");
                return;
            }

            var admin = CompanionPlugin.SenderDisplayName(sender);
            var detail = $"prefab={resolved} pos={x:0.#},{y:0.#},{z:0.#} rotY={rotY:0.#} zdo={uid}";
            CompanionPlugin.FeatureLog($"Admin {sender} ({admin}) spawned prefab: {detail}");
            CompanionPlugin.SrvAudit(sender, "PREFABSPAWN", detail);
            Wave1AuditRpc.PostModLog($"PREFAB SPAWN '{resolved}' at {x:0},{y:0},{z:0} by {admin}");
            CompanionPlugin.NotifySender(sender, $"Spawned '{resolved}' at {x:0}, {y:0}, {z:0}.");
        }

        // ---- rate limiting (per admin identity, so reconnecting does not reset the window) ----

        private static string RateKey(long sender)
        {
            var id = CompanionPlugin.SenderPlatformId(sender);
            return string.IsNullOrEmpty(id) || id == "?" ? "uid:" + sender : id;
        }

        private static bool RateLimitOk(long sender)
        {
            var now = Time.unscaledTime;
            var key = RateKey(sender);
            List<float> stamps;
            if (!SpawnStamps.TryGetValue(key, out stamps)) return true;
            var recent = 0;
            for (var i = 0; i < stamps.Count; i++)
                if (now - stamps[i] < SpawnWindowSec) recent++;
            return recent < SpawnsPerMinute;
        }

        private static void RecordSpawn(long sender)
        {
            var now = Time.unscaledTime;
            var key = RateKey(sender);
            List<float> stamps;
            if (!SpawnStamps.TryGetValue(key, out stamps))
            {
                stamps = new List<float>();
                SpawnStamps[key] = stamps;
            }
            stamps.RemoveAll(t => now - t >= SpawnWindowSec);
            stamps.Add(now);
        }

        private static void PruneSpawnStamps(float now)
        {
            if (SpawnStamps.Count == 0) return;
            List<string> dead = null;
            foreach (var kv in SpawnStamps)
            {
                kv.Value.RemoveAll(t => now - t >= SpawnWindowSec);
                if (kv.Value.Count == 0) (dead ?? (dead = new List<string>())).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var k in dead) SpawnStamps.Remove(k);
        }

        // ==================== 4. location listing (read-only discovery aid) ====================

        /// <summary>
        /// AP_SrvLocationListReq(string filter) -> AP_LocationList
        /// {int ver, int total, int shipped(&lt;=60), shipped x (string name)}.
        /// Names come from ZoneSystem.m_locations (ZoneSystem.cs:409, filled by SetupLocations,
        /// ZoneSystem.cs:711-742). This is a NAME LOOKUP ONLY — see the class limitations: nothing here places
        /// a location, and the spawner refuses every name this list returns.
        /// </summary>
        internal static void OnLocationListReq(long sender, string filter)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvLocationListReq")) return;

            var q = (filter ?? "").Trim();
            if (q.Length > MaxNameLen) q = q.Substring(0, MaxNameLen);

            var names = LocationNames();
            var hits = new List<string>();
            foreach (var n in names)
            {
                if (q.Length > 0 && n.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(n);
            }

            var pkg = new ZPackage();
            pkg.Write(Ver);
            pkg.Write(hits.Count);
            var shipped = Math.Min(hits.Count, LocationCap);
            pkg.Write(shipped);
            for (var i = 0; i < shipped; i++) pkg.Write(hits[i]);
            Reply(sender, "AP_LocationList", pkg);

            if (names.Count == 0)
                CompanionPlugin.NotifySender(sender, "The server could not read the location registry on this game build.");
        }

        // ==================== registries ====================

        // ZoneSystem.m_locations is a plain public List<ZoneLocation> (ZoneSystem.cs:409). Cached against the
        // ZoneSystem instance so a world reload rebuilds it, and wrapped so a game update that empties or
        // renames the collection degrades this feature to "no names" instead of throwing.
        private static List<string> LocationNames()
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return _locationNames ?? new List<string>();
            if (_locationNames != null && ReferenceEquals(_locationZoneSystem, zs)) return _locationNames;

            var names = new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var locs = zs.m_locations;
                if (locs != null)
                    foreach (var l in locs)
                    {
                        if (l == null) continue;
                        // m_name is the registry name ("Crypt2", "Eikthyrnir"); m_prefabName is the soft-reference
                        // asset name and is what a naive admin would type. Both are refused by the spawner.
                        AddName(names, set, l.m_name);
                        AddName(names, set, l.m_prefabName);
                    }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Location registry unavailable ({e.Message}); location listing will be empty.");
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            _locationNames = names;
            _locationNameSet = set;
            _locationZoneSystem = zs;
            return _locationNames;
        }

        private static void AddName(List<string> names, HashSet<string> set, string n)
        {
            if (string.IsNullOrEmpty(n)) return;
            if (!set.Add(n)) return;
            names.Add(n);
        }

        private static bool IsLocationName(string name)
        {
            LocationNames();   // ensures the cache matches the current ZoneSystem
            return _locationNameSet != null && _locationNameSet.Contains(name);
        }

        // Prefab registry, keyed lower-case -> real name, for case-insensitive resolution and suggestions.
        // m_namedPrefabs (ZNetScene.cs:17) already covers m_prefabs AND m_nonNetViewPrefabs (ZNetScene.cs:36-43),
        // so it is the most complete source; the public m_prefabs list is the fallback if it is ever renamed.
        private static Dictionary<string, string> PrefabIndex()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return _prefabByLowerName;
            if (_prefabByLowerName != null && ReferenceEquals(_prefabScene, scene)) return _prefabByLowerName;

            var byLower = new Dictionary<string, string>(StringComparer.Ordinal);
            var all = new List<string>();
            try
            {
                var named = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs")?.GetValue(scene)
                    as Dictionary<int, GameObject>;
                if (named != null)
                {
                    foreach (var kv in named) AddPrefabName(byLower, all, kv.Value);
                }
                else
                {
                    if (scene.m_prefabs != null)
                        foreach (var go in scene.m_prefabs) AddPrefabName(byLower, all, go);
                    if (scene.m_nonNetViewPrefabs != null)
                        foreach (var go in scene.m_nonNetViewPrefabs) AddPrefabName(byLower, all, go);
                }
            }
            catch (Exception e)
            {
                CompanionPlugin.FeatureLog($"Prefab index unavailable ({e.Message}); spawn suggestions are disabled.");
                return _prefabByLowerName;
            }
            if (byLower.Count == 0) return _prefabByLowerName;

            all.Sort(StringComparer.OrdinalIgnoreCase);
            _prefabByLowerName = byLower;
            _prefabNames = all;
            _prefabScene = scene;
            return _prefabByLowerName;
        }

        private static void AddPrefabName(Dictionary<string, string> byLower, List<string> all, GameObject go)
        {
            if (go == null) return;
            var n = go.name;
            if (string.IsNullOrEmpty(n)) return;
            var key = n.ToLowerInvariant();
            if (byLower.ContainsKey(key)) return;
            byLower[key] = n;
            all.Add(n);
        }

        // Cheap, dependency-free ranking: prefix match beats substring match beats a shared leading run of
        // characters. Good enough to catch the usual typo ("beeh" -> "Beech1") without a Levenshtein matrix
        // over ~2000 names on the main thread.
        private static List<string> NearMatches(string query, int take)
        {
            var res = new List<string>();
            if (PrefabIndex() == null || _prefabNames == null || string.IsNullOrEmpty(query)) return res;

            var q = query.ToLowerInvariant();
            var scored = new List<KeyValuePair<int, string>>();
            foreach (var n in _prefabNames)
            {
                var l = n.ToLowerInvariant();
                int score;
                if (l.StartsWith(q, StringComparison.Ordinal)) score = 1000 - Math.Abs(l.Length - q.Length);
                else if (l.IndexOf(q, StringComparison.Ordinal) >= 0) score = 800 - Math.Abs(l.Length - q.Length);
                else
                {
                    var common = 0;
                    while (common < l.Length && common < q.Length && l[common] == q[common]) common++;
                    if (common < 3) continue;
                    score = common * 10 - Math.Abs(l.Length - q.Length);
                }
                scored.Add(new KeyValuePair<int, string>(score, n));
            }
            scored.Sort((a, b) => b.Key != a.Key
                ? b.Key.CompareTo(a.Key)
                : string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < scored.Count && res.Count < take; i++) res.Add(scored[i].Value);
            return res;
        }

        // ==================== helpers ====================

        // The requester's world position, used only to sort the portal list. Remote peers report a reference
        // position to the server for ZDO syncing (ZNetPeer.m_refPos / GetRefPos, ZNetPeer.cs:15,43-46). The
        // host is never in m_peers, so it falls back to its own local player. Exactly (0,0,0) is treated as
        // "not reported yet" rather than as the middle of the ocean.
        private static bool TryRequesterPos(long sender, out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                var peer = ZNet.instance != null ? ZNet.instance.GetPeer(sender) : null;
                if (peer != null)
                {
                    var p = peer.GetRefPos();
                    if (p != Vector3.zero) { pos = p; return true; }
                    return false;
                }
                var local = Player.m_localPlayer;   // listen-server host only; null on a dedicated server
                if (local != null) { pos = local.transform.position; return true; }
            }
            catch (Exception) { }
            return false;
        }

        private static bool FinitePosition(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            return Mathf.Abs(x) <= WorldEdge && Mathf.Abs(z) <= WorldEdge && Mathf.Abs(y) <= WorldEdge;
        }

        // Every server->admin reply in this file funnels through here. ReplyTo hands the payload to the
        // panel in-process when the requester IS this process (listen-server host, where a routed reply has
        // no server peer to authenticate against and the panel's anti-spoof gate correctly discards it) and
        // falls back to the ordinary routed send for a remote admin.
        private static void Reply(long uid, string rpc, ZPackage pkg)
        {
            CompanionPlugin.ReplyTo(uid, rpc, pkg);
        }
    }
}
