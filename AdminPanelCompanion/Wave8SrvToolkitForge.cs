using System;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Wave 8 — #17 item attribute editor: "give with attributes" (server + client executor) ====================
    // AP_SrvGive hands out an item at full durability, default variant and no crafter stamp. This is the
    // same relay with the four extra attributes an event prize needs:
    //
    //   AP_SrvGiveEx(ZPackage)   admin -> server   validated, chokepoint-audited (builder grant), then
    //   AP_GiveItemEx(ZPackage)  server -> target  executed on the TARGET's own client (an inventory
    //                                              lives with its owner, CompanionPlugin.OnGiveItem shape)
    //
    // Wire: int ver=1 | long target | string prefab | int amount | int quality | int durabilityPct |
    //       int variant | string crafter        (the relay drops the target field; everything else verbatim)
    //
    // The aimed-item-drop half of #17 is client-only (panel raycast + ClaimOwnership + SaveToZDO) and
    // never touches the companion.
    internal static class Wave8ToolkitForge
    {
        private const int Ver = 1;
        private const int MaxAmount = 100000;    // same ceiling as AP_SrvGive: a bigger loop freezes the client
        private const int MaxQuality = 10;       // clamped again on the client against m_shared.m_maxQuality
        private const int MaxVariant = 31;       // clamped again on the client against m_shared.m_variants
        private const int MaxPrefabLen = 64;
        private const int MaxCrafterLen = 40;

        internal static void Init() { /* no config: an admin action that does nothing until clicked */ }

        // ==================== server: AP_SrvGiveEx ====================

        internal static void OnGiveEx(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!CompanionPlugin.SenderCanFeature(sender, "AP_SrvGiveEx")) return;

            int ver, amount, quality, durability, variant; long target; string prefab, crafter;
            try
            {
                ver = pkg.ReadInt();
                target = pkg.ReadLong();
                prefab = pkg.ReadString();
                amount = pkg.ReadInt();
                quality = pkg.ReadInt();
                durability = pkg.ReadInt();
                variant = pkg.ReadInt();
                crafter = pkg.ReadString();
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_SrvGiveEx: malformed packet dropped ({e.Message})"); return; }
            if (ver != Ver) return;

            prefab = Wave8Toolkit.CleanName(prefab, MaxPrefabLen);
            crafter = Wave8Toolkit.CleanText(crafter, MaxCrafterLen);
            amount = Mathf.Clamp(amount, 1, MaxAmount);
            quality = Mathf.Clamp(quality, 1, MaxQuality);
            durability = Mathf.Clamp(durability, 0, 100);
            variant = Mathf.Clamp(variant, 0, MaxVariant);
            if (prefab.Length == 0) { CompanionPlugin.NotifySender(sender, "Item forge: no prefab name given."); return; }
            if (target == 0L) { CompanionPlugin.NotifySender(sender, "Item forge: no target player."); return; }

            // The prefab is resolved here too so a typo is answered with a sentence instead of a silent
            // no-op on the target's client (ObjectDB exists on a dedicated server; OnServerSpawn relies on it).
            GameObject go = null;
            try { go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null; }
            catch (Exception) { }
            if (go == null || go.GetComponent<ItemDrop>() == null)
            {
                CompanionPlugin.NotifySender(sender, $"Item forge: '{prefab}' is not an item prefab on this server.");
                return;
            }

            // The item is built on the target's client, so that client must run the companion. An admin
            // giving to themself needs no probe (their own client is the one that sent this request), and
            // neither does a listen-server host: it is never in m_peers and never probed, but this very
            // code is running on it, so the relay below dispatches locally and lands.
            var isHost = Player.m_localPlayer != null && ZDOMan.instance != null && target == ZDOMan.GetSessionID();
            if (target != sender && !isHost)
            {
                if (ZNet.instance.GetPeer(target) == null)
                {
                    CompanionPlugin.NotifySender(sender, "Item forge: that player is not online.");
                    return;
                }
                var has = Wave34Core.HasMod(target);
                if (has != true)
                {
                    if (has == null) { try { Wave34Core.ProbePeer(target); } catch (Exception) { } }
                    CompanionPlugin.NotifySender(sender, "Item forge: " + Wave34Core.CapReason(target));
                    return;
                }
            }

            var relay = new ZPackage();
            relay.Write(Ver);
            relay.Write(prefab);
            relay.Write(amount);
            relay.Write(quality);
            relay.Write(durability);
            relay.Write(variant);
            relay.Write(crafter);
            try { ZRoutedRpc.instance.InvokeRoutedRPC(target, "AP_GiveItemEx", relay); }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_GiveItemEx relay to {target} failed: {e.Message}"); return; }

            CompanionPlugin.SrvAudit(sender, "GIVE-EX",
                $"target={target} prefab={prefab} x{amount} q{quality} dur{durability}% v{variant} crafter={crafter}");
            CompanionPlugin.FeatureLog($"Admin {sender} forges {amount}x {prefab} (q{quality} dur{durability}% v{variant} by '{crafter}') for peer {target}");
            CompanionPlugin.NotifySender(sender, $"Forged {amount}x {prefab} (q{quality}, {durability}% durability, variant {variant}).");
        }

        // ==================== client executor: AP_GiveItemEx ====================

        // Runs on the TARGET player's client. Trust template: only a packet the SERVER sent, only once a
        // local Player exists (a dedicated server has none and never executes its own relay). The item
        // is cloned exactly like CompanionPlugin.OnGiveItem, then the four attributes are applied in an
        // order that matters: quality first, because GetMaxDurability() scales with m_quality.
        internal static void OnGiveItemEx(long sender, ZPackage pkg)
        {
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                if (!Wave8Toolkit.SenderIsTrustedServer(sender)) return;

                int ver, amount, quality, durability, variant; string prefabName, crafter;
                try
                {
                    ver = pkg.ReadInt();
                    prefabName = pkg.ReadString();
                    amount = pkg.ReadInt();
                    quality = pkg.ReadInt();
                    durability = pkg.ReadInt();
                    variant = pkg.ReadInt();
                    crafter = pkg.ReadString();
                }
                catch (Exception) { return; }
                if (ver != Ver || string.IsNullOrEmpty(prefabName)) return;
                amount = Mathf.Clamp(amount, 1, MaxAmount);
                durability = Mathf.Clamp(durability, 0, 100);
                crafter = Wave8Toolkit.CleanText(crafter, MaxCrafterLen);

                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
                if (prefab == null) return;
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop == null) return;
                // icon-less items (hair, beards, effects) corrupt the inventory grid — never add them
                if (drop.m_itemData.m_shared.m_icons == null || drop.m_itemData.m_shared.m_icons.Length == 0) return;

                var maxStack = drop.m_itemData.m_shared.m_maxStackSize;
                if (maxStack < 1) maxStack = 1;   // guard: a 0 max-stack would loop forever
                var remaining = amount;
                while (remaining > 0)
                {
                    var stack = Mathf.Min(remaining, maxStack);
                    remaining -= stack;
                    var data = drop.m_itemData.Clone();
                    data.m_dropPrefab = prefab;
                    data.m_stack = stack;
                    Apply(data, quality, durability, variant, crafter);
                    if (!player.GetInventory().AddItem(data))
                    {
                        // Full inventory: drop the item at the player's feet with the SAME attributes and
                        // write them to its ZDO right away, or the drop's own Load() on the next revision
                        // would put the defaults back.
                        var go = UnityEngine.Object.Instantiate(prefab, player.transform.position + Vector3.up, Quaternion.identity);
                        var d = go.GetComponent<ItemDrop>();
                        if (d != null)
                        {
                            d.m_itemData.m_stack = stack;
                            Apply(d.m_itemData, quality, durability, variant, crafter);
                            var nview = go.GetComponent<ZNetView>();
                            if (nview != null && nview.IsValid())
                            {
                                try { ItemDrop.SaveToZDO(d.m_itemData, nview.GetZDO()); }
                                catch (Exception) { }
                            }
                        }
                    }
                }
                player.Message(MessageHud.MessageType.Center, $"An admin granted you {amount}x {prefabName}!");
            }
            catch (Exception e) { CompanionPlugin.FeatureLog($"AP_GiveItemEx failed: {e.Message}"); }
        }

        private static void Apply(ItemDrop.ItemData data, int quality, int durabilityPct, int variant, string crafter)
        {
            data.m_quality = Mathf.Clamp(quality, 1, Math.Max(1, data.m_shared.m_maxQuality));
            data.m_durability = data.GetMaxDurability() * (durabilityPct / 100f);
            // m_variants is the count of variant textures (0 or 1 = the item has none); a variant index
            // past it renders a magenta placeholder, so clamp to the real range.
            var variants = data.m_shared.m_variants;
            data.m_variant = variants > 1 ? Mathf.Clamp(variant, 0, variants - 1) : 0;
            if (!string.IsNullOrEmpty(crafter))
            {
                data.m_crafterID = 1L;       // the same sentinel OnGiveItem uses: "crafted", by nobody's player id
                data.m_crafterName = crafter;
            }
        }
    }
}
