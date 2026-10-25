using System.Text;
using VRage;
using VRage.Utils;

namespace Automata.Util
{
    /// <summary>
    /// Localized text lookups. Every UI string is a MyStringId defined in Util/Strings/*.cs (MyStrings: one file per block,
    /// ids like "Drone_EnableAI", Keen's way); its English text is generated into Data/Localization/MyTexts.resx by
    /// tools/generate_mytexts_resx.py. Whole phrases are one string each: texts are not composed from words.
    /// </summary>
    public static class Loc
    {
        /// <summary>The text of 'id' in the current language.</summary>
        public static string T(MyStringId id)
        {
            return MyTexts.GetString(id);
        }

        /// <summary>string.Format over the text of 'id'.</summary>
        public static string F(MyStringId id, object arg0)
        {
            return string.Format(MyTexts.GetString(id), arg0);
        }

        public static string F(MyStringId id, object arg0, object arg1)
        {
            return string.Format(MyTexts.GetString(id), arg0, arg1);
        }

        public static string F(MyStringId id, object arg0, object arg1, object arg2)
        {
            return string.Format(MyTexts.GetString(id), arg0, arg1, arg2);
        }

        public static string F(MyStringId id, params object[] args)
        {
            return string.Format(MyTexts.GetString(id), args);
        }

        /// <summary>A MyStringId for a formatted text (titles / tooltips built once, at control creation).</summary>
        public static MyStringId Id(MyStringId format, params object[] args)
        {
            return MyStringId.GetOrCompute(string.Format(MyTexts.GetString(format), args));
        }

        /// <summary>Appends the text of 'id'.</summary>
        public static StringBuilder AppendLoc(this StringBuilder sb, MyStringId id)
        {
            return sb.Append(MyTexts.GetString(id));
        }

        /// <summary>AppendFormat over the text of 'id'.</summary>
        public static StringBuilder AppendLoc(this StringBuilder sb, MyStringId id, object arg0)
        {
            return sb.AppendFormat(MyTexts.GetString(id), arg0);
        }

        public static StringBuilder AppendLoc(this StringBuilder sb, MyStringId id, object arg0, object arg1)
        {
            return sb.AppendFormat(MyTexts.GetString(id), arg0, arg1);
        }

        public static StringBuilder AppendLoc(this StringBuilder sb, MyStringId id, object arg0, object arg1, object arg2)
        {
            return sb.AppendFormat(MyTexts.GetString(id), arg0, arg1, arg2);
        }

        public static StringBuilder AppendLoc(this StringBuilder sb, MyStringId id, params object[] args)
        {
            return sb.AppendFormat(MyTexts.GetString(id), args);
        }
    }
}
