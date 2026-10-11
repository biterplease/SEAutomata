using System;
using System.Collections.Generic;

using VRage.Game;
using VRage.Utils;

namespace Automata.Inventory
{
    /// <summary>
    /// Inventory keys for physical items: a component is its subtype ("SteelPlate", what construction has always
    /// used); anything else is "Type/Subtype" ("Ingot/Iron", "Ore/Iron", "PhysicalGunObject/WelderItem",
    /// "AmmoMagazine/NATO_25x184mm"), so ore and ingots of the same name don't collide. Cached per type / subtype.
    /// </summary>
    public static class ItemKeys
    {
        public const string COMPONENT_TYPE = "MyObjectBuilder_Component";
        private const string PREFIX = "MyObjectBuilder_";
        private static readonly Dictionary<string, Dictionary<string, MyStringHash>> cache =
            new Dictionary<string, Dictionary<string, MyStringHash>>(StringComparer.Ordinal);
        private static readonly object sync = new object();

        public static MyStringHash Of(string typeId, string subtypeId)
        {
            if (typeId == null || subtypeId == null) return MyStringHash.NullOrEmpty;
            lock (sync)
            {
                Dictionary<string, MyStringHash> bySubtype;
                if (!cache.TryGetValue(typeId, out bySubtype))
                {
                    bySubtype = new Dictionary<string, MyStringHash>(StringComparer.Ordinal);
                    cache[typeId] = bySubtype;
                }
                MyStringHash key;
                if (!bySubtype.TryGetValue(subtypeId, out key))
                {
                    key = MyStringHash.GetOrCompute(Text(typeId, subtypeId));
                    bySubtype[subtypeId] = key;
                }
                return key;
            }
        }

        public static MyStringHash Of(VRage.Game.ModAPI.Ingame.MyItemType type)
        {
            return Of(type.TypeId, type.SubtypeId);
        }

        public static MyStringHash Of(MyDefinitionId id)
        {
            return Of(id.TypeId.ToString(), id.SubtypeName);
        }

        /// <summary>The key's text for a type id ("MyObjectBuilder_Ingot") and subtype.</summary>
        public static string Text(string typeId, string subtypeId)
        {
            if (typeId == COMPONENT_TYPE) return subtypeId;
            return ShortType(typeId) + "/" + subtypeId;
        }

        /// <summary>"MyObjectBuilder_Ingot" -> "Ingot".</summary>
        public static string ShortType(string typeId)
        {
            return typeId != null && typeId.StartsWith(PREFIX, StringComparison.Ordinal) ? typeId.Substring(PREFIX.Length) : typeId;
        }

        public static bool IsComponent(MyStringHash key)
        {
            return key.String.IndexOf('/') < 0;
        }

        public static void Clear()
        {
            lock (sync) cache.Clear();
        }
    }
}
