using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AdminPanel
{
    // ==================== Wave 1 — Tiered admin roles (client side) ====================
    // Assignment UI for the companion's roles table. Enforcement itself is a SERVER decision
    // (Features.EnableTieredRoles in the companion config) — this panel can only read the flag and say so;
    // it deliberately offers no way to flip it, because a client toggling its own permission model would be
    // exactly the wrong trust boundary.
    //
    // The reply carries the full picture in one packet (flag + assignments + custom permission rows), so
    // there is a single 30s poll and no per-row request.
    public partial class AdminPanelPlugin
    {
        // Role names are STATE KEYS shared with the server's roles.txt — never localized, never translated.
        private static readonly string[] RoleBuiltins = { "owner", "moderator", "builder" };

        private ConfigEntry<bool> _roleSectionCfg;

        // ---- live payload (written by the RPC handler, any time) ----
        private bool _roleEnabledSrv;
        private bool _roleHaveData;
        private List<RoleAssign> _roleAssigns;
        private List<RolePerm> _rolePerms;

        // ---- UI state ----
        private string _roleIdInput = "";
        private int _roleSel;                  // index into the Layout-pinned choice list
        private Vector2 _roleScroll;
        private float _roleNextReq;

        // ---- Layout snapshots ----
        private bool _roleHostLayout;
        private bool _roleEnabledLayout;
        private bool _roleHaveLayout;
        private List<RoleAssign> _roleAssignsLayout;
        private List<RolePerm> _rolePermsLayout;
        private string[] _roleChoicesLayout;
        // Pinned from the roster DrawWindow already built this frame. Text-only: it picks which sentence the
        // "no assignments" label carries, never how many controls this section emits.
        private bool _roleSoloLayout;

        private sealed class RoleAssign
        {
            public string Id;
            public string Role;
        }

        private sealed class RolePerm
        {
            public string Role;
            public string Csv;
        }

        // ---- lifecycle ----

        internal void RoleInit()
        {
            _roleSectionCfg = Config.Bind("Features", "ShowRolesSection", true,
                "Show the admin-roles section in the Extras tab. Viewing and assigning roles never changes whether the server ENFORCES them (that is Features.EnableTieredRoles in the companion config).");
        }

        internal bool RoleSectionEnabled() => _roleSectionCfg == null || _roleSectionCfg.Value;

        internal void RoleReset()
        {
            _roleEnabledSrv = false;
            _roleHaveData = false;
            _roleAssigns = null;
            _rolePerms = null;
            _roleIdInput = "";
            _roleSel = 0;
            _roleScroll = Vector2.zero;
            _roleNextReq = 0f;
            _roleHostLayout = false;
            _roleEnabledLayout = false;
            _roleHaveLayout = false;
            _roleAssignsLayout = null;
            _rolePermsLayout = null;
            _roleChoicesLayout = null;
            _roleSoloLayout = false;
        }

        // ---- reply ----

        private static void RoleOnRolesData(long sender, ZPackage pkg)
        {
            var self = Instance;
            if (self == null || !SenderIsServerReply(sender)) return;
            ParseRolesData(self, pkg);
        }

        // Parse half, shared with the in-process bridge (a listen-server host has no server peer for the
        // gate above to authenticate against). Registered next to the RPC in Wave1_Audit's registration class.
        internal static void ParseRolesData(AdminPanelPlugin self, ZPackage pkg)
        {
            try
            {
                if (pkg.ReadInt() != 1) return;             // payload version gate
                var enabled = pkg.ReadBool();
                var count = pkg.ReadInt();
                if (count < 0 || count > 100) return;
                var assigns = new List<RoleAssign>(count);
                for (var i = 0; i < count; i++)
                    assigns.Add(new RoleAssign { Id = pkg.ReadString(), Role = pkg.ReadString() });
                var permCount = pkg.ReadInt();
                if (permCount < 0 || permCount > 20) return;
                var perms = new List<RolePerm>(permCount);
                for (var i = 0; i < permCount; i++)
                    perms.Add(new RolePerm { Role = pkg.ReadString(), Csv = pkg.ReadString() });
                self._roleEnabledSrv = enabled;
                self._roleAssigns = assigns;
                self._rolePerms = perms;
                self._roleHaveData = true;
            }
            catch (Exception) { /* malformed reply — keep whatever we had */ }
        }

        // ---- polling ----

        private void RolePoll()
        {
            // Poll whenever a companion can hear us: a remote server peer (ServerUid() != 0) OR a
            // listen-server host, where the companion lives in this process and answers locally. Only "no
            // ZNet" and "connecting, no peer yet" skip. _roleHostLayout is the Layout-pass snapshot and
            // RolePoll only ever runs on that pass.
            if (ZNet.instance == null) return;
            if (ServerUid() == 0L && !_roleHostLayout) return;
            if (Time.time < _roleNextReq) return;
            _roleNextReq = Time.time + 30f;                 // throttle-first
            SrvRpc("AP_SrvRolesReq");
        }

        // Builtins first, then any custom role the server reported a permission row for. Rebuilt on Layout
        // because the cycle button indexes into it and the list length changes when a reply lands.
        private void RoleRebuildChoices()
        {
            var list = new List<string>(RoleBuiltins);
            var perms = _rolePermsLayout;
            if (perms != null)
                foreach (var p in perms)
                {
                    if (string.IsNullOrEmpty(p.Role)) continue;
                    var known = false;
                    foreach (var c in list)
                        if (string.Equals(c, p.Role, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                    if (!known) list.Add(p.Role);
                }
            _roleChoicesLayout = list.ToArray();
        }

        private void RoleSend(string id, string role)
        {
            var pkg = new ZPackage();
            pkg.Write(id ?? "");
            pkg.Write(role ?? "");
            SrvRpc("AP_SrvRoleSet", pkg);
            _roleNextReq = 0f;   // pull the authoritative list back on the next Layout pass
        }

        // ---- draw ----

        internal void DrawRolesSection()
        {
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                _roleHostLayout = ZNet.instance != null && ZNet.instance.IsServer();
                _roleEnabledLayout = _roleEnabledSrv;
                _roleHaveLayout = _roleHaveData;
                _roleAssignsLayout = _roleAssigns;
                _rolePermsLayout = _rolePerms;
                // Read the roster DrawWindow pinned this frame - never rebuild it here.
                _roleSoloLayout = _othersSnapshot == null || _othersSnapshot.Count == 0;
                RoleRebuildChoices();
                RolePoll();
            }

            var choices = _roleChoicesLayout ?? RoleBuiltins;
            var sel = choices.Length == 0 ? 0 : ((_roleSel % choices.Length) + choices.Length) % choices.Length;

            BeginCard(Loc.T("role.section"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(_roleEnabledLayout ? Loc.T("role.enforce_on") : Loc.T("role.enforce_off"), _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("role.refresh"), _buttonStyle, GUILayout.MinWidth(90))) _roleNextReq = 0f;
            GUILayout.EndHorizontal();
            // One label either way - only the KEY swaps (both take the same {0} config-name argument), so the
            // control count matches between Layout and Repaint on a host and on a remote client alike.
            GUILayout.Label(Loc.T(_roleHostLayout ? "role.host_local_hint" : "role.enforce_hint",
                "Features.EnableTieredRoles"), _hintStyle);

            // ---- assignment row ----
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("role.id_label"), _labelStyle, GUILayout.MinWidth(70));
            _roleIdInput = GUILayout.TextField(_roleIdInput ?? "", _textFieldStyle, GUILayout.MinWidth(200));
            // Cycle button: the label changes but the control count never does.
            if (GUILayout.Button(choices.Length == 0 ? "-" : choices[sel], _buttonStyle, GUILayout.MinWidth(120)))
                _roleSel = sel + 1;
            if (GUILayout.Button(Loc.T("role.assign"), _buttonStyle, GUILayout.MinWidth(90)))
            {
                var id = (_roleIdInput ?? "").Trim();
                if (id.Length > 0 && choices.Length > 0)
                {
                    RoleSend(id, choices[sel]);
                    Message(Loc.T("role.msg_assigned", id, choices[sel]));
                    _roleIdInput = "";
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("role.assign_hint"), _hintStyle);

            // ---- current assignments ----
            DrawSection(Loc.T("role.assignments"));
            // No host special-case: the in-process companion answers AP_SrvRolesReq the same way a remote one
            // does, so a host walks the normal pending / none / table ladder below.
            var assigns = _roleAssignsLayout;
            if (!_roleHaveLayout || assigns == null)
            {
                GUILayout.Label(Loc.T("role.pending"), _hintStyle);
            }
            else if (assigns.Count == 0)
            {
                // An empty table is healthy, not a failure: nobody has been given a tier yet. Solo it is also
                // structurally expected, so say what the feature is for instead of leaving a bare "none".
                // One label either way - only the key swaps.
                GUILayout.Label(Loc.T(_roleSoloLayout ? "role.none_assigned_solo" : "role.none_assigned"), _hintStyle);
            }
            else
            {
                _roleScroll = GUILayout.BeginScrollView(_roleScroll,
                    GUILayout.Height(Mathf.Min(ListView(430f), assigns.Count * 30f + 16f)));
                for (var i = 0; i < assigns.Count; i++)
                {
                    var a = assigns[i];
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(a.Id, _cellStyle, GUILayout.Width(280));
                    GUILayout.Label(a.Role, _cellStyle, GUILayout.Width(140));
                    GUILayout.FlexibleSpace();
                    // Per-row confirm id keyed on the player id, never the row index (rows reorder).
                    if (ConfirmButton("role:rm:" + a.Id, Loc.T("role.remove"), GUILayout.MinWidth(100)))
                    {
                        RoleSend(a.Id, "");
                        Message(Loc.T("role.msg_removed", a.Id));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            // ---- reference: what the built-in roles grant ----
            DrawSection(Loc.T("role.builtin_title"));
            GUILayout.Label(Loc.T("role.builtin_owner"), _hintStyle);
            GUILayout.Label(Loc.T("role.builtin_mod"), _hintStyle);
            GUILayout.Label(Loc.T("role.builtin_builder"), _hintStyle);

            // ---- custom roles (read-only; editing them is a server-file job) ----
            DrawSection(Loc.T("role.custom_title"));
            var perms = _rolePermsLayout;
            if (perms == null || perms.Count == 0)
            {
                GUILayout.Label(Loc.T("role.custom_none"), _hintStyle);
            }
            else
            {
                for (var i = 0; i < perms.Count; i++)
                {
                    GUILayout.BeginHorizontal(i % 2 == 0 ? _rowEven : _rowOdd);
                    GUILayout.Label(perms[i].Role, _cellStyle, GUILayout.Width(140));
                    GUILayout.Label(perms[i].Csv, _dimCellStyle, GUILayout.MinWidth(200));
                    GUILayout.EndHorizontal();
                }
            }

            EndCard();
        }
    }
}
