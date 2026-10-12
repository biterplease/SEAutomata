using System;
using System.Collections.Generic;
using System.Text;

using ProtoBuf;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace Automata.Util
{
    /// <summary>LCD look of an Automata computer (same defaults as the drone's): colours, font size, header, header page.</summary>
    [ProtoContract]
    public class LcdStyle
    {
        [ProtoMember(1, IsRequired = true)] public uint Foreground = 0xFF00FF00;   // RGB(0,255,0)
        [ProtoMember(2, IsRequired = true)] public uint Background = 0xFF202020;   // RGB(32,32,32)
        [ProtoMember(3, IsRequired = true)] public float FontSize = 0.4f;
        [ProtoMember(4, IsRequired = true)] public bool ShowHeader = true;
        /// <summary>Header page shown ("Header PgUp" / "Header PgDn" actions), modulo the pages there are.</summary>
        [ProtoMember(5)] public int HeaderPage;
    }

    /// <summary>
    /// A block's player log (terminal and LCDs): entries of one or more lines, oldest first. An entry's first line
    /// starts at the left edge; its other lines are indented by the caller (2 spaces per level).
    /// </summary>
    public class BlockLog
    {
        private const int MAX_LINES = 80;
        private readonly List<string[]> entries = new List<string[]>();
        private int lines;

        /// <summary>Changes on every entry (LCD redraws).</summary>
        public int Version { get; private set; }

        public void Add(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return;
            var parts = entry.Split('\n');
            entries.Add(parts);
            lines += parts.Length;
            while (lines > MAX_LINES && entries.Count > 1)
            {
                lines -= entries[0].Length;
                entries.RemoveAt(0);
            }
            Version++;
        }

        public void Clear()
        {
            entries.Clear();
            lines = 0;
            Version++;
        }

        /// <summary>Every line, oldest first.</summary>
        public void GetLines(List<string> into)
        {
            for (int i = 0; i < entries.Count; i++)
                for (int k = 0; k < entries[i].Length; k++) into.Add(entries[i][k]);
        }

        public void AppendTo(StringBuilder sb)
        {
            for (int i = 0; i < entries.Count; i++)
                for (int k = 0; k < entries[i].Length; k++) sb.Append(entries[i][k]).Append('\n');
        }
    }

    /// <summary>
    /// The LCDs of an Automata computer: text panels on its construct whose name contains its LCD tag AND whose content is set
    /// to the "Automata Log" app (the player does both). The app draws the text on each client; the server only builds the
    /// header and log lines while a client is looking at such a panel, and sends them when they changed (see LcdFeed.cs).
    /// </summary>
    public class BlockLcd
    {
        public const string FONT = "Monospace";
        private const int MAX_LINES = 40;     // newest log lines sent; a panel shows as many as fit
        private const int MAX_CHARS = 160;    // per line

        private readonly LcdFeeder feeder = new LcdFeeder();
        private readonly List<string> logLines = new List<string>();

        /// <summary>Server, every update: true when a client is looking at one of the block's LCDs (then build the header and call <see cref="Publish"/>).</summary>
        public bool Wanted(IMyTerminalBlock owner, string tag)
        {
            feeder.Bind(owner, tag, false);
            return feeder.HasViewers;
        }

        /// <summary>Sends the header and the newest log lines to the viewers, when something changed since the last time.</summary>
        public void Publish(LcdStyle style, List<string> header, BlockLog log)
        {
            if (style == null) return;
            double t = PerfStats.Start();
            ulong key = LcdFeeder.Seed;
            key = LcdFeeder.Mix(key, style.Foreground);
            key = LcdFeeder.Mix(key, style.Background);
            key = LcdFeeder.Mix(key, (long)Math.Round(style.FontSize * 100f));
            key = LcdFeeder.Mix(key, style.ShowHeader ? 1 : 0);
            key = LcdFeeder.Mix(key, log != null ? log.Version : 0);
            if (style.ShowHeader && header != null)
                for (int i = 0; i < header.Count; i++) key = LcdFeeder.Mix(key, header[i]);
            if (feeder.Changed(key))
            {
                var packet = new LcdFeedPacket
                {
                    Foreground = style.Foreground,
                    Background = style.Background,
                    FontSize = style.FontSize,
                    Lines = new List<string>(),
                };
                if (style.ShowHeader && header != null)
                {
                    packet.Header = new List<string>(header.Count);
                    for (int i = 0; i < header.Count; i++) packet.Header.Add(Cut(header[i]));
                }
                if (log != null)
                {
                    logLines.Clear();
                    log.GetLines(logLines);
                    for (int i = Math.Max(0, logLines.Count - MAX_LINES); i < logLines.Count; i++) packet.Lines.Add(Cut(logLines[i]));
                    logLines.Clear();
                }
                feeder.Send(packet);
            }
            PerfStats.Stop(PerfStats.LcdDraw, t);
        }

        private static string Cut(string s)
        {
            if (s == null) return "";
            return s.Length > MAX_CHARS ? s.Substring(0, MAX_CHARS) : s;
        }

        /// <summary>Two columns (header tables): 'left' padded to 'width', then 'right'.</summary>
        public static string Columns(string left, string right, int width)
        {
            left = left ?? "";
            if (string.IsNullOrEmpty(right)) return left;
            return left.Length >= width ? left + " " + right : left.PadRight(width) + right;
        }

        /// <summary>Header pages of 'perPage' rows over 'count' items: the style's page (cycling), its first item; the page count.</summary>
        public static int Paginate(LcdStyle style, int count, int perPage, out int first)
        {
            int pages = Math.Max(1, (count + perPage - 1) / perPage);
            int page = style != null ? ((style.HeaderPage % pages) + pages) % pages : 0;   // the actions cycle through the pages
            first = page * perPage;
            return pages;
        }
    }

    /// <summary>
    /// LCD controls of the Automata programmable-block computers, like the drone's: text / background color, font size,
    /// show header; and the "Header PgUp" / "Header PgDn" toolbar actions (blocks with a paged header).
    /// </summary>
    public static class LcdControls
    {
        #region UI strings
        private static readonly MyStringId StrText = MyStringId.GetOrCompute("Text color");
        private static readonly MyStringId StrTextTip = MyStringId.GetOrCompute("LCD text color.");
        private static readonly MyStringId StrBack = MyStringId.GetOrCompute("Background color");
        private static readonly MyStringId StrBackTip = MyStringId.GetOrCompute("LCD background color.");
        private static readonly MyStringId StrFont = MyStringId.GetOrCompute("Font size");
        private static readonly MyStringId StrFontTip = MyStringId.GetOrCompute("LCD font size. Only as many log lines as fit are shown.");
        private static readonly MyStringId StrHeader = MyStringId.GetOrCompute("Show header");
        private static readonly MyStringId StrHeaderTip = MyStringId.GetOrCompute("The summary lines above the log.");
        private const string PG_UP = "Header PgUp";
        private const string PG_DN = "Header PgDn";
        #endregion

        /// <summary>'edited': the block saves and syncs its settings (SettingsEdited).</summary>
        public static void Add<T>(string prefix, Func<IMyTerminalBlock, T> logic, Func<T, LcdStyle> style, Action<T> edited, bool pagedHeader)
            where T : class
        {
            Func<IMyTerminalBlock, bool> visible = b => logic(b) != null;
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyProgrammableBlock>(prefix + "LcdForeground");
                c.Title = StrText;
                c.Tooltip = StrTextTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = visible;
                c.Getter = b => { var l = logic(b); return l != null ? new Color(style(l).Foreground) : Color.Lime; };
                c.Setter = (b, v) => { var l = logic(b); if (l == null) return; style(l).Foreground = v.PackedValue; edited(l); };
                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyProgrammableBlock>(prefix + "LcdBackground");
                c.Title = StrBack;
                c.Tooltip = StrBackTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = visible;
                c.Getter = b => { var l = logic(b); return l != null ? new Color(style(l).Background) : new Color(32, 32, 32); };
                c.Setter = (b, v) => { var l = logic(b); if (l == null) return; style(l).Background = v.PackedValue; edited(l); };
                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(prefix + "LcdFontSize");
                c.Title = StrFont;
                c.Tooltip = StrFontTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = visible;
                c.SetLimits(0.1f, 2f);
                c.Getter = b => { var l = logic(b); return l != null ? style(l).FontSize : 0.6f; };
                c.Setter = (b, v) => { var l = logic(b); if (l == null) return; style(l).FontSize = MathHelper.Clamp((float)Math.Round(v, 2), 0.1f, 2f); edited(l); };
                c.Writer = (b, sb) => { var l = logic(b); if (l != null) sb.Append(style(l).FontSize.ToString("F2")); };
                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(prefix + "LcdShowHeader");
                c.Title = StrHeader;
                c.Tooltip = StrHeaderTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = visible;
                c.Getter = b => { var l = logic(b); return l != null && style(l).ShowHeader; };
                c.Setter = (b, v) => { var l = logic(b); if (l == null) return; style(l).ShowHeader = v; edited(l); };
                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            if (!pagedHeader) return;
            AddPageAction(prefix + "HeaderPgUp", PG_UP, "Textures\\GUI\\Icons\\Actions\\Decrease.dds", -1, logic, style, edited, visible);
            AddPageAction(prefix + "HeaderPgDn", PG_DN, "Textures\\GUI\\Icons\\Actions\\Increase.dds", 1, logic, style, edited, visible);
        }

        private static void AddPageAction<T>(string id, string name, string icon, int step, Func<IMyTerminalBlock, T> logic, Func<T, LcdStyle> style,
                                             Action<T> edited, Func<IMyTerminalBlock, bool> visible) where T : class
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyProgrammableBlock>(id);
            a.Name = new StringBuilder(name);
            a.Icon = icon;
            a.ValidForGroups = true;
            a.Enabled = visible;
            a.Action = b =>
            {
                var l = logic(b);
                if (l == null) return;
                var s = style(l);
                s.HeaderPage += step;   // taken modulo the pages there are (cycles)
                edited(l);
            };
            a.Writer = (b, sb) => { };
            MyAPIGateway.TerminalControls.AddAction<IMyProgrammableBlock>(a);
        }
    }
}
