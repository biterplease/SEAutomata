using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;

namespace Automata.Util
{
    /// <summary>
    /// The "Automata Log" LCD app (a text surface script): shows the header and log of the Automata block whose LCD tag is in
    /// the LCD's name. The player sets the LCD's content to this app and names the LCD with the tag. Runs on each client
    /// only (a dedicated server never runs it); the text comes from the server as LcdFeedPackets (see LcdFeed.cs), and the
    /// app redraws only when a new feed arrived.
    /// </summary>
    [MyTextSurfaceScript(LcdFeedServer.APP, "Automata Log")]
    public class AutomataLogApp : MyTSSCommon
    {
        #region UI strings
        private static readonly MyStringId StrTitle = MyStringId.GetOrCompute("Automata Log");
        private static readonly MyStringId StrWaiting = MyStringId.GetOrCompute("Waiting for the server...");
        private static readonly MyStringId StrNotPanel = MyStringId.GetOrCompute("Use this app on a text panel LCD.");
        private static readonly MyStringId StrNotApp = MyStringId.GetOrCompute("Set this LCD's content to the Automata Log app.");
        private static readonly MyStringId StrBusy = MyStringId.GetOrCompute("This block has too many LCDs in view.");
        private static readonly MyStringId StrNoBlock = MyStringId.GetOrCompute("No Automata block on this construct has its LCD tag in this LCD's name (same owner or faction). Name the LCD with the tag.");
        #endregion

        private const int ASK_EVERY_RUNS = 3;   // one request per ~5 s (a run every 100 ticks)
        private const int REDRAW_EVERY_RUNS = 6; // drawn again even when nothing changed (~10 s), in case the screen lost its sprites
        private const string FONT = "Monospace";
        private static readonly Color ERROR_COLOR = new Color(255, 40, 40);
        private static readonly Color WARNING_COLOR = new Color(255, 230, 0);

        private readonly long panelId;
        private readonly StringBuilder measure = new StringBuilder("M");
        private int askIn;
        private bool drawn;
        private LcdFeedPacket drawnFeed;
        private byte drawnReason;
        private Vector2 drawnSurface;
        private float drawnPadding;
        private int sinceDraw;

        public override ScriptUpdate NeedsUpdate { get { return ScriptUpdate.Update100; } }

        public AutomataLogApp(Sandbox.ModAPI.Ingame.IMyTextSurface surface, VRage.Game.ModAPI.Ingame.IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            panelId = block.EntityId;
        }

        public override void Run()
        {
            base.Run();
            try
            {
                if (--askIn < 0)
                {
                    askIn = ASK_EVERY_RUNS - 1;
                    LcdFeedClient.Request(panelId);
                }
                byte reason;
                var feed = LcdFeedClient.Get(panelId, out reason);
                Vector2 surfaceSize = m_surface.SurfaceSize;
                float padding = m_surface.TextPadding;
                if (drawn && ++sinceDraw < REDRAW_EVERY_RUNS && ReferenceEquals(feed, drawnFeed) && reason == drawnReason
                    && surfaceSize == drawnSurface && padding == drawnPadding) return;
                drawn = true;
                sinceDraw = 0;
                drawnFeed = feed;
                drawnReason = reason;
                drawnSurface = surfaceSize;
                drawnPadding = padding;
                Draw(feed, reason);
            }
            catch (Exception)
            {
                // a script must never throw into the game; the next run tries again
                drawn = false;
            }
        }

        private void Draw(LcdFeedPacket feed, byte reason)
        {
            var surf = m_surface;
            Color fg = feed != null ? new Color(feed.Foreground) : new Color(0, 255, 0);
            Color bg = feed != null ? new Color(feed.Background) : new Color(32, 32, 32);
            float scale = feed != null ? MathHelper.Clamp(feed.FontSize, 0.1f, 2f) : 0.7f;

            Vector2 unit = Vector2.Zero;
            try { unit = surf.MeasureStringInPixels(measure, FONT, scale); } catch (Exception) { }
            if (unit.Y <= 1f) unit = new Vector2(30f * 0.63f * scale, 30f * scale);
            Vector2 surface = surf.SurfaceSize;
            Vector2 texture = surf.TextureSize;
            float pad = surface.X * (surf.TextPadding / 100f);
            Vector2 origin = (texture - surface) * 0.5f + new Vector2(pad, pad);
            Vector2 inner = surface - new Vector2(pad * 2, pad * 2);
            int rows = Math.Max(1, (int)(inner.Y / unit.Y));
            int cols = Math.Max(4, (int)(inner.X / Math.Max(1f, unit.X)));
            Vector2 pos = origin;

            using (var frame = surf.DrawFrame())
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", texture * 0.5f, texture, bg, null, TextAlignment.CENTER, 0f));
                if (feed == null)
                {
                    AddLine(frame, Loc.T(StrTitle), ref pos, unit.Y, fg, scale, cols);
                    AddLine(frame, new string('-', cols), ref pos, unit.Y, fg, scale, cols);
                    string text = reason == LcdAttachPacket.NOT_A_PANEL ? Loc.T(StrNotPanel)
                                : reason == LcdAttachPacket.NOT_THE_APP ? Loc.T(StrNotApp)
                                : reason == LcdAttachPacket.NO_BLOCK ? Loc.T(StrNoBlock)
                                : reason == LcdAttachPacket.BUSY ? Loc.T(StrBusy)
                                : Loc.T(StrWaiting);
                    foreach (var line in Wrap(text, cols)) AddLine(frame, line, ref pos, unit.Y, fg, scale, cols);
                    return;
                }

                if (feed.Header != null)
                {
                    for (int i = 0; i < feed.Header.Count && rows > 0; i++, rows--) AddLine(frame, feed.Header[i], ref pos, unit.Y, fg, scale, cols);
                    if (feed.AlertRow && rows > 0)
                    {
                        Color alert = feed.AlertLevel == 2 ? ERROR_COLOR : WARNING_COLOR;
                        AddLine(frame, feed.Alert, ref pos, unit.Y, alert, scale, cols);   // reserved even when empty
                        rows--;
                    }
                    if (rows > 0) { AddLine(frame, new string('-', cols), ref pos, unit.Y, fg, scale, cols); rows--; }
                }
                var lines = feed.Lines;
                if (lines == null) return;
                int n = Math.Min(Math.Max(rows, 0), lines.Count);
                for (int i = lines.Count - n; i < lines.Count; i++) AddLine(frame, lines[i], ref pos, unit.Y, fg, scale, cols);
            }
        }

        private static void AddLine(MySpriteDrawFrame frame, string line, ref Vector2 pos, float lineHeight, Color color, float scale, int cols)
        {
            if (!string.IsNullOrEmpty(line))
            {
                if (line.Length > cols) line = line.Substring(0, cols);
                frame.Add(new MySprite(SpriteType.TEXT, line, pos, null, color, FONT, TextAlignment.LEFT, scale));
            }
            pos.Y += lineHeight;
        }

        private static List<string> Wrap(string text, int cols)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > cols)
                {
                    lines.Add(line.ToString());
                    line.Length = 0;
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
        }
    }
}
