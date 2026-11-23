using System.Text;

namespace Automata.Util
{
    /// <summary>
    /// The longest line a block shows - in its terminal info and on its LCDs; longer ones end in "..." (the whole text
    /// stays in Automata.log). The game lays the whole info panel out again at every line, and one long line (a dock
    /// failure's ~5,000 characters) slowed the game while the panel was open; the LCD feed sends every line to every viewer.
    /// </summary>
    public static class TextLimits
    {
        public const int TERMINAL_LINE = 200;   // characters
        public const int LCD_LINE = 160;        // characters (Monospace: about the width of a wide LCD at the default font size)
        private const string MORE = "...";

        /// <summary>'s' cut to 'max' characters ("..." at the end when cut); null: "".</summary>
        public static string Cut(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - MORE.Length) + MORE;
        }

        /// <summary>Appends 'text' to 'target' with every line cut to 'max' characters.</summary>
        public static void AppendCut(StringBuilder target, StringBuilder text, int max = TERMINAL_LINE)
        {
            int n = text.Length, run = 0;
            bool longLine = false;
            for (int i = 0; i < n && !longLine; i++)
            {
                if (text[i] == '\n') run = 0;
                else if (++run > max) longLine = true;
            }
            if (!longLine) { target.Append(text); return; }   // the usual case: as it is
            for (int start = 0; start < n; )
            {
                int end = start;
                while (end < n && text[end] != '\n') end++;
                int keep = end - start <= max ? end - start : max - MORE.Length;
                for (int i = start; i < start + keep; i++) target.Append(text[i]);
                if (keep < end - start) target.Append(MORE);
                if (end < n) target.Append('\n');
                start = end + 1;
            }
        }
    }
}
