using System;
using System.Globalization;

using VRage.Utils;

namespace Automata.Logistics
{
    /// <summary>
    /// One Min / Max line of a logistics computer: "item:min:max", e.g. "SteelPlate:100:5000", "Ingot/Iron:0:20000".
    /// The item is anything <see cref="AutomataSession.TryResolveItem"/> finds (key, subtype, display name); the
    /// numbers are whole and >= 0, 0 meaning "not set". Parsing fails (and nothing is changed) otherwise.
    /// </summary>
    public struct QuotaEntry
    {
        public MyStringHash Key;
        public int Min;
        public int Max;

        public override string ToString()
        {
            return Format(Key, Min, Max);
        }

        public static string Format(MyStringHash key, int min, int max)
        {
            return key.String + ":" + min.ToString(CultureInfo.InvariantCulture) + ":" + max.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string text, out QuotaEntry entry)
        {
            entry = default(QuotaEntry);
            if (string.IsNullOrWhiteSpace(text)) return false;
            // From the right: the item part could be typed with spaces, never with ':'
            int b = text.LastIndexOf(':');
            if (b <= 0) return false;
            int a = text.LastIndexOf(':', b - 1);
            if (a <= 0) return false;
            int min, max;
            if (!int.TryParse(text.Substring(a + 1, b - a - 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out min) || min < 0) return false;
            if (!int.TryParse(text.Substring(b + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out max) || max < 0) return false;
            ComponentData item;
            if (!AutomataSession.TryResolveItem(text.Substring(0, a), out item)) return false;
            entry = new QuotaEntry { Key = item.Key, Min = min, Max = max };
            return true;
        }
    }
}
