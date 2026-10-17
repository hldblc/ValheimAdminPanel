using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdminPanelCompanion
{
    // ==================== Inventory blob codec (container ZDO "items", every layout the game has written) ====================
    // A container keeps its contents in its ZDO under "items": a ZPackage written by Inventory.Save. Since Valheim 1.0.12
    // that is a BYTE ARRAY (Container.Save/Load, decompile-1.0.20 Container.cs:470-500); an older world's base64 STRING
    // is converted to the byte array when the world loads (ZDOMan.cs:2134-2154) and the string removed. ReadBlob/WriteBlob
    // are the only places that know this; the Wave 8 chest viewer used the string and so saw every chest as empty.
    // The layout changed twice (decompile-1.0.20 Inventory.cs:800-920, ItemDrop.cs:465-555, Version.Item):
    //
    //   <= 107  "old":     int ver | int n | n x { string prefab, int stack, float durability, Vector2i pos, bool
    //                      equipped, [101+] int quality, [102+] int variant, [103+] long crafterId + string crafter,
    //                      [104+] int nCustom + pairs, [105+] int worldLevel, [106+] bool pickedUp, [107] bool cheated }
    //   >= 108  "compact": int ver | ushort n | n x ItemData.Save { int durability*100, byte gridX, byte gridY,
    //                      byte worldLevel, byte flags, [4] ushort quality, [8] ushort stack, [16] int variant,
    //                      [32] long crafterId + string crafter, [64] int prefabHash, [128] NumItems nCustom + pairs,
    //                      [109+] byte cheatFlags (bit 1 = cheated from 110) }
    //
    // Valheim 1.0.12 already wrote 109 and 1.0.20 writes 110, so the Wave 8 chest viewer (which knew only 106)
    // reported every chest saved since then as "newer format". This codec reads all of them field for field into
    // plain records and writes the compact layout back (109 for records upgraded from the old layout, otherwise the
    // version it read) - lossless for items the server's ObjectDB does not know, because the compact layout carries
    // the prefab HASH and the record keeps it. Inventory.Load is deliberately not used: on a server it drops unknown
    // prefabs and clamps stacks, so a decode + re-encode round trip would silently delete modded items.
    internal static class InvCodec
    {
        internal const int MaxKnown = 110;     // Version.Item.NewCheats (1.0.20)
        private const int Smaller = 108;       // Version.Item.Smaller: first compact layout
        private const int ChunksNCheats = 109;
        private const int AbandonedDN = 107;
        private const int ItemCap = 4096;
        private const int CustomCap = 256;

        internal sealed class Rec
        {
            public string Prefab = "";      // resolved name ("" when the server does not know the hash)
            public int PrefabHash;          // 0 = none (an old-layout entry with an empty name)
            public int Stack = 1;
            public float Durability = 100f;
            public Vector2i Pos;
            public bool Equipped;
            public int Quality = 1;
            public int Variant;
            public long CrafterId;
            public string CrafterName = "";
            public List<KeyValuePair<string, string>> Custom;   // null = none
            public int WorldLevel;
            public bool PickedUp;
            public bool Cheated;

            public string CustomValue(string key)
            {
                if (Custom == null) return null;
                for (var i = 0; i < Custom.Count; i++) if (Custom[i].Key == key) return Custom[i].Value;
                return null;
            }

            /// <summary>Display name: the prefab name, or "#hash" when this server cannot resolve it.</summary>
            public string Name => Prefab.Length > 0 ? Prefab : (PrefabHash != 0 ? "#" + PrefabHash : "?");
        }

        private static readonly int KeyItems = "items".GetStableHashCode();

        /// <summary>A container ZDO's contents as stored: the byte array, or a legacy base64 string's bytes. Null = empty.</summary>
        internal static byte[] ReadBlob(ZDO zdo)
        {
            if (zdo == null) return null;
            var b = zdo.GetByteArray(KeyItems);
            if (b != null && b.Length > 0) return b;
            var s = zdo.GetString(KeyItems, "");
            if (string.IsNullOrEmpty(s)) return null;
            try { return new ZPackage(s).GetArray(); } catch (Exception) { return null; }
        }

        /// <summary>Write a container's contents the way Container.Save does (byte array), dropping any legacy string.</summary>
        internal static void WriteBlob(ZDO zdo, byte[] data)
        {
            if (zdo == null) return;
            zdo.Set(KeyItems, data ?? new byte[0]);
            try { zdo.RemoveString(KeyItems); } catch (Exception) { }
        }

        /// <summary>Parse a legacy base64 container blob.</summary>
        internal static bool Parse(string b64, out int version, out int declared, List<Rec> items)
        {
            byte[] data = null;
            if (!string.IsNullOrEmpty(b64))
            {
                try { data = new ZPackage(b64).GetArray(); }
                catch (Exception) { version = 0; declared = 0; items.Clear(); return false; }
            }
            return Parse(data, out version, out declared, items);
        }

        /// <summary>Parse a container blob. False = unreadable (version still set when the header was readable).</summary>
        internal static bool Parse(byte[] data, out int version, out int declared, List<Rec> items)
        {
            version = 0;
            declared = 0;
            items.Clear();
            if (data == null || data.Length == 0) return true;   // nothing stored is an empty container
            try
            {
                var pkg = new ZPackage(data);
                version = pkg.ReadInt();
                if (version > MaxKnown) return false;
                if (version >= Smaller)
                {
                    declared = pkg.ReadUShort();
                    for (var i = 0; i < declared; i++)
                    {
                        var r = ReadCompact(pkg, version);
                        if (r.PrefabHash != 0) items.Add(r);   // Inventory.Load skips hash 0 (no drop prefab)
                    }
                }
                else
                {
                    declared = pkg.ReadInt();
                    if (declared < 0 || declared > ItemCap) return false;
                    for (var i = 0; i < declared; i++)
                    {
                        var r = ReadOld(pkg, version);
                        if (r.Prefab.Length > 0) items.Add(r);   // Inventory.LoadOld skips nameless entries
                    }
                }
                return true;
            }
            catch (Exception)
            {
                items.Clear();
                return false;
            }
        }

        private static Rec ReadOld(ZPackage pkg, int version)
        {
            var r = new Rec();
            r.Prefab = pkg.ReadString() ?? "";
            r.PrefabHash = r.Prefab.Length > 0 ? r.Prefab.GetStableHashCode() : 0;
            r.Stack = pkg.ReadInt();
            r.Durability = pkg.ReadSingle();
            r.Pos = pkg.ReadVector2i();
            r.Equipped = pkg.ReadBool();
            if (version >= 101) r.Quality = pkg.ReadInt();
            if (version >= 102) r.Variant = pkg.ReadInt();
            if (version >= 103) { r.CrafterId = pkg.ReadLong(); r.CrafterName = pkg.ReadString() ?? ""; }
            if (version >= 104)
            {
                var n = pkg.ReadInt();
                if (n < 0 || n > CustomCap) throw new FormatException("custom data count");
                if (n > 0)
                {
                    r.Custom = new List<KeyValuePair<string, string>>(n);
                    for (var j = 0; j < n; j++) r.Custom.Add(new KeyValuePair<string, string>(pkg.ReadString() ?? "", pkg.ReadString() ?? ""));
                }
            }
            if (version >= 105) r.WorldLevel = pkg.ReadInt();
            if (version >= 106) r.PickedUp = pkg.ReadBool();
            if (version == AbandonedDN) r.Cheated = pkg.ReadBool();
            return r;
        }

        private static Rec ReadCompact(ZPackage pkg, int version)
        {
            var r = new Rec();
            r.Durability = pkg.ReadInt() * 0.01f;
            r.Pos = new Vector2i(pkg.ReadByte(), pkg.ReadByte());
            r.WorldLevel = pkg.ReadByte();
            var b = pkg.ReadByte();
            r.PickedUp = (b & 1) != 0;
            r.Equipped = (b & 2) != 0;
            r.Quality = (b & 4) == 0 ? 1 : pkg.ReadUShort();
            r.Stack = (b & 8) == 0 ? 1 : pkg.ReadUShort();
            r.Variant = (b & 0x10) != 0 ? pkg.ReadInt() : 0;
            if ((b & 0x20) != 0) { r.CrafterId = pkg.ReadLong(); r.CrafterName = pkg.ReadString() ?? ""; }
            r.PrefabHash = (b & 0x40) != 0 ? pkg.ReadInt() : 0;
            var n = (b & 0x80) != 0 ? pkg.ReadNumItems() : 0;
            if (n < 0 || n > CustomCap) throw new FormatException("custom data count");
            if (n > 0)
            {
                r.Custom = new List<KeyValuePair<string, string>>(n);
                for (var j = 0; j < n; j++) r.Custom.Add(new KeyValuePair<string, string>(pkg.ReadString() ?? "", pkg.ReadString() ?? ""));
            }
            if (version >= ChunksNCheats)
            {
                var c = pkg.ReadByte();
                r.Cheated = version >= MaxKnown && (c & 1) != 0;
            }
            r.Prefab = NameOf(r.PrefabHash);
            return r;
        }

        /// <summary>Write the compact layout. <paramref name="version"/> below 109 is raised to 109.</summary>
        internal static byte[] Serialize(List<Rec> items, int version)
        {
            if (version < ChunksNCheats) version = ChunksNCheats;
            if (version > MaxKnown) version = MaxKnown;
            var pkg = new ZPackage();
            pkg.Write(version);
            var keep = new List<Rec>(items.Count);
            foreach (var r in items) if (r != null && HashOf(r) != 0) keep.Add(r);
            pkg.Write((ushort)Math.Min(keep.Count, ushort.MaxValue));
            foreach (var r in keep)
            {
                var hash = HashOf(r);
                var custom = r.Custom != null ? r.Custom.Count : 0;
                var flags = 0;
                flags |= r.PickedUp ? 1 : 0;
                flags |= r.Equipped ? 2 : 0;
                flags |= r.Quality != 1 ? 4 : 0;
                flags |= r.Stack != 1 ? 8 : 0;
                flags |= r.Variant != 0 ? 0x10 : 0;
                flags |= r.CrafterId != 0L ? 0x20 : 0;
                flags |= 0x40;   // the prefab hash is always written: without it Inventory.Load drops the entry
                flags |= custom != 0 ? 0x80 : 0;
                pkg.Write(Mathf.RoundToInt(r.Durability * 100f));   // the value came from int * 0.01: round, never truncate, or it loses 0.01 per save
                pkg.Write((byte)Mathf.Clamp(r.Pos.x, 0, 255));
                pkg.Write((byte)Mathf.Clamp(r.Pos.y, 0, 255));
                pkg.Write((byte)Mathf.Clamp(r.WorldLevel, 0, 255));
                pkg.Write((byte)flags);
                if ((flags & 4) != 0) pkg.Write((ushort)Mathf.Clamp(r.Quality, 0, ushort.MaxValue));
                if ((flags & 8) != 0) pkg.Write((ushort)Mathf.Clamp(r.Stack, 0, ushort.MaxValue));
                if ((flags & 0x10) != 0) pkg.Write(r.Variant);
                if ((flags & 0x20) != 0) { pkg.Write(r.CrafterId); pkg.Write(r.CrafterName ?? ""); }
                pkg.Write(hash);
                if ((flags & 0x80) != 0)
                {
                    pkg.WriteNumItems(custom);
                    foreach (var kv in r.Custom) { pkg.Write(kv.Key ?? ""); pkg.Write(kv.Value ?? ""); }
                }
                pkg.Write((byte)(version >= MaxKnown && r.Cheated ? 1 : 0));
            }
            return pkg.GetArray();
        }

        private static int HashOf(Rec r)
        {
            if (r.PrefabHash != 0) return r.PrefabHash;
            return string.IsNullOrEmpty(r.Prefab) ? 0 : r.Prefab.GetStableHashCode();
        }

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        private static object _namesDb;

        internal static string NameOf(int hash)
        {
            if (hash == 0) return "";
            object dbo;
            try { dbo = ObjectDB.instance; } catch (Exception) { return ""; }   // no game loaded (offline tooling)
            var db = dbo as ObjectDB;
            if (ReferenceEquals(db, null)) return "";   // a destroyed instance falls into the try below
            if (!ReferenceEquals(db, _namesDb)) { Names.Clear(); _namesDb = db; }
            string n;
            if (Names.TryGetValue(hash, out n)) return n;
            try
            {
                var go = db.GetItemPrefab(hash);
                n = go != null ? go.name : "";
            }
            catch (Exception) { n = ""; }
            Names[hash] = n;
            return n;
        }
    }
}
