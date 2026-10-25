using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;
using Automata.Util.Strings;

namespace Automata.Util
{
    /// <summary>
    /// The "Automata Log" LCD app (a text surface script): shows the header and log of the Automata block whose LCD tag is in
    /// the LCD's name. The player sets the LCD's content to this app and names the LCD with the tag. Runs on each client
    /// only (a dedicated server never runs it); the text comes from the server as LcdFeedPackets (see LcdFeed.cs), and the
    /// app redraws only when a new feed arrived.
    /// </summary>
    [MyTextSurfaceScript(LcdFeedServer.APP, "AutomataLog")]
    public class AutomataLogApp : MyTSSCommon
    {

        private const int ASK_EVERY_RUNS = 3;   // one request per ~5 s (a run every 100 ticks)
        private const int REDRAW_EVERY_RUNS = 6; // drawn again even when nothing changed (~10 s), in case the screen lost its sprites
        private const string FONT = "Monospace";
        private static readonly Color ERROR_COLOR = new Color(255, 40, 40);
        private static readonly Color WARNING_COLOR = new Color(255, 230, 0);

        private readonly VRage.Game.ModAPI.Ingame.IMyCubeBlock ownerBlock;
        private readonly long blockId;
        private byte surfaceIndex;
        private long panelKey;
        private bool resolved;
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
            ownerBlock = block;
            blockId = block != null ?  block.EntityId : 0;
        }

        private bool TryResolve()                               // false: screens not ready yet, try next run
        {
            var provider = ownerBlock as Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
            int index = 0;
            if (provider != null)
            {
                int n = provider.SurfaceCount;
                bool found = false;
                for (int i = 0; i < n && !found; i++)
                {
                    var s = provider.GetSurface(i);
                    if (s == null) return false;
                    if (ReferenceEquals(s, m_surface)) { index = i; found = true; }
                }
                for (int i = 0; i < n && !found; i++)          // fallback: ScreenArea name
                {
                    var s = provider.GetSurface(i);
                    if (s != null && s.Name == m_surface.Name) { index = i; found = true; }
                }
            }                                                   // not found / not a provider: index 0
            surfaceIndex = (byte)index;
            panelKey = LcdFeedServer.PanelKey(blockId, surfaceIndex);
            return true;
        }

        // private static byte FindSurface(VRage.Game.ModAPI.Ingame.IMyCubeBlock block, Sandbox.ModAPI.Ingame.IMyTextSurface surface)
        // {
        //     var provider = block as Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;   // a programmable block implements this one statically
        //     if (provider == null) return 0;
        //     for (int i = 0; i < provider.SurfaceCount; i++)
        //         if (ReferenceEquals(provider.GetSurface(i), surface)) return (byte)i;
        //     for (int i = 0; i < provider.SurfaceCount; i++)                         // fallback: ScreenArea name
        //         if (provider.GetSurface(i).Name == surface.Name) return (byte)i;
        //     return 0;
        // }

        public override void Run()
        {
            base.Run();
            try
            {
                if (blockId == 0) return;
                if (!resolved) {
                    resolved = TryResolve();
                    if (!resolved)
                        return;
                }
                if (--askIn < 0)
                {
                    askIn = ASK_EVERY_RUNS - 1;
                    LcdFeedClient.Request(blockId, surfaceIndex);
                }
                byte reason;
                var feed = LcdFeedClient.Get(panelKey, out reason);
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
                    AddLine(frame, Loc.T(MyStrings.Common_AutomataLog), ref pos, unit.Y, fg, scale, cols);
                    AddLine(frame, new string('-', cols), ref pos, unit.Y, fg, scale, cols);
                    string text = reason == LcdAttachPacket.NOT_A_PANEL ? Loc.T(MyStrings.Common_UseThisApp)
                                : reason == LcdAttachPacket.NOT_THE_APP ? Loc.T(MyStrings.Common_SetThisLCDsContent)
                                : reason == LcdAttachPacket.NO_BLOCK ? Loc.T(MyStrings.Common_NoAutomataBlock)
                                : reason == LcdAttachPacket.BUSY ? Loc.T(MyStrings.Common_ThisBlockHasTooMany)
                                : reason == LcdAttachPacket.NOT_RUNNING ? Loc.T(MyStrings.Common_ThisBlockIsOff)
                                : Loc.T(MyStrings.Common_WaitingForTheServer);
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
