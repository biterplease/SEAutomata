using System;
using System.Globalization;

using VRage.Utils;

namespace Automata.Mining
{
    /// <summary>One ore order of a mining surveyor: "ore:kg", e.g. "Ore/Iron:100000". The ore is anything
    /// <see cref="AutomataSession.TryResolveItem"/> finds that is an ore; kg is a whole number above 0.</summary>
    public struct OreOrder
    {
        public MyStringHash Ore;
        public int Kg;

        public override string ToString()
        {
            return Format(Ore, Kg);
        }

        public static string Format(MyStringHash ore, int kg)
        {
            return ore.String + ":" + kg.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsOre(MyStringHash key)
        {
            return key.String.StartsWith("Ore/", StringComparison.Ordinal);
        }

        public static bool TryParse(string text, out OreOrder order)
        {
            order = default(OreOrder);
            if (string.IsNullOrWhiteSpace(text)) return false;
            int c = text.LastIndexOf(':');
            if (c <= 0) return false;
            int kg;
            if (!int.TryParse(text.Substring(c + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out kg) || kg < 0) return false;
            ComponentData item;
            string name = text.Substring(0, c).Trim();
            if (!AutomataSession.TryResolveItem(name, out item) && !AutomataSession.TryResolveItem("Ore/" + name, out item)) return false;
            if (!IsOre(item.Key)) return false;
            order = new OreOrder { Ore = item.Key, Kg = kg };
            return true;
        }
    }
}
