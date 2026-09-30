using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Drone
{
    public enum DisplayAlertLevel : byte
    {
        None,
        Warning,
        Error,
    }

    /// <summary>
    /// Drone log shown on tagged LCDs of the drone's own grid (no subgrids, no connected grids).
    /// Newest line at the bottom; optional header: state, battery and hydrogen levels, then one line reserved for
    /// the current error (red) or warning (yellow), then a rule.
    /// Drawn as sprites (text mode has no per-line colour). Only redrawn when something changed; drawn on the
    /// server only (the sprites are synced).
    /// </summary>
    public class DroneStatusDisplay
    {
        private static readonly Color ERROR_COLOR = new Color(255, 40, 40);
        private static readonly Color WARNING_COLOR = new Color(255, 230, 0);

        public const string FONT = "Monospace";          // fixed width, so the header columns line up
        private const int CAPACITY = 40;                 // lines kept; panels show as many as fit
        private const float FALLBACK_LINE_HEIGHT = 30f;  // px at font size 1, if MeasureStringInPixels is unavailable

        private readonly string[] lines = new string[CAPACITY];
        private int head;       // index of the oldest line
        private int count;

        private readonly List<IMyTextPanel> panels = new List<IMyTextPanel>();
        private readonly List<int> panelRows = new List<int>();   // visible text rows per panel, for the current font size
        private readonly List<float> panelLineHeights = new List<float>();
        private readonly StringBuilder sb = new StringBuilder(128);
        private readonly StringBuilder measure = new StringBuilder("X");

        private bool dirty = true;
        private bool styleDirty = true;
        private string headerState = "";
        private int headerPower = -1, headerH2 = -1;
        private string alertText = "";
        private DisplayAlertLevel alertLevel;

        public int PanelCount { get { return panels.Count; } }

        public void Add(string line)
        {
            if (count < CAPACITY)
            {
                lines[(head + count) % CAPACITY] = line;
                count++;
            }
            else
            {
                lines[head] = line;
                head = (head + 1) % CAPACITY;
            }
            dirty = true;
        }

        public void SetHeader(string state, int powerPercent, int h2Percent)
        {
            if (state == headerState && powerPercent == headerPower && h2Percent == headerH2) return;
            headerState = state;
            headerPower = powerPercent;
            headerH2 = h2Percent;
            dirty = true;
        }

        /// <summary>The header's reserved line: the most important current problem, or nothing.</summary>
        public void SetAlert(DisplayAlertLevel level, string text)
        {
            if (text == null || level == DisplayAlertLevel.None) { text = ""; level = DisplayAlertLevel.None; }
            if (level == alertLevel && text == alertText) return;
            alertLevel = level;
            alertText = text;
            dirty = true;
        }

        public void MarkStyleDirty()
        {
            styleDirty = true;
            dirty = true;
        }

        /// <summary>Rebuilds the panel list: text panels on 'grid' only whose name contains 'tag'.</summary>
        public void FindPanels(IMyCubeGrid grid, string tag)
        {
            panels.Clear();
            panelRows.Clear();
            panelLineHeights.Clear();
            if (grid == null || string.IsNullOrEmpty(tag)) return;
            foreach (var p in grid.GetFatBlocks<IMyTextPanel>())
            {
                if (p.IsFunctional && p.CustomName != null && p.CustomName.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    panels.Add(p);
                    panelRows.Add(0);
                    panelLineHeights.Add(0);
                }
            }
            styleDirty = true;
            dirty = true;
        }

        /// <summary>Writes to the panels if anything changed. Cheap no-op otherwise.</summary>
        public void Flush(DroneControllerSettings settings)
        {
            if (!dirty || panels.Count == 0 || settings == null) return;
            if (!MyAPIGateway.Multiplayer.IsServer) { dirty = false; return; }
            dirty = false;

            float fontSize = MathHelper.Clamp(settings.LcdFontSize, 0.1f, 2f);
            if (styleDirty)
            {
                styleDirty = false;
                Color fg = new Color(settings.LcdForeground);
                Color bg = new Color(settings.LcdBackground);
                for (int i = 0; i < panels.Count; i++)
                {
                    var p = panels[i];
                    if (p.Closed) continue;
                    p.ContentType = ContentType.SCRIPT;
                    p.Script = "";
                    p.ScriptForegroundColor = fg;
                    p.ScriptBackgroundColor = bg;
                    float lineHeight;
                    panelRows[i] = VisibleRows(p, fontSize, out lineHeight);
                    panelLineHeights[i] = lineHeight;
                }
            }

            Color text = new Color(settings.LcdForeground);
            string header = null, rule = null;
            if (settings.LcdShowHeader)
            {
                sb.Clear();
                sb.Append(headerState.Length > 10 ? headerState.Substring(0, 10) : headerState.PadRight(10));
                sb.Append(" POW:").Append(Pct(headerPower)).Append("%  H2:").Append(Pct(headerH2)).Append('%');
                header = sb.ToString();
                rule = new string('-', header.Length);
            }
            Color alertColor = alertLevel == DisplayAlertLevel.Error ? ERROR_COLOR : WARNING_COLOR;

            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (p.Closed || !p.IsFunctional) continue;
                int rows = panelRows[i];
                float lh = panelLineHeights[i];
                // Sprite space is the texture; the visible surface is centred in it
                Vector2 origin = (p.TextureSize - p.SurfaceSize) * 0.5f + p.SurfaceSize * (p.TextPadding / 100f);
                Vector2 pos = origin;
                using (var frame = p.DrawFrame())
                {
                    if (header != null)
                    {
                        AddLine(frame, header, ref pos, lh, text, fontSize);
                        AddLine(frame, alertText, ref pos, lh, alertColor, fontSize);   // reserved even when empty
                        AddLine(frame, rule, ref pos, lh, text, fontSize);
                        rows -= 3;
                    }
                    int n = Math.Min(Math.Max(rows, 0), count);
                    for (int k = count - n; k < count; k++)
                        AddLine(frame, lines[(head + k) % CAPACITY], ref pos, lh, text, fontSize);
                }
            }
        }

        private static void AddLine(MySpriteDrawFrame frame, string line, ref Vector2 pos, float lineHeight, Color color, float scale)
        {
            if (!string.IsNullOrEmpty(line))
                frame.Add(new MySprite(SpriteType.TEXT, line, pos, null, color, FONT, TextAlignment.LEFT, scale));
            pos.Y += lineHeight;
        }

        private static string Pct(int v)
        {
            if (v < 0) return "  -";
            return v.ToString().PadLeft(3);
        }

        private int VisibleRows(IMyTextPanel p, float fontSize, out float lineHeight)
        {
            lineHeight = 0;
            try { lineHeight = p.MeasureStringInPixels(measure, FONT, fontSize).Y; }
            catch (Exception) { }
            if (lineHeight <= 1f) lineHeight = FALLBACK_LINE_HEIGHT * fontSize;
            float height = p.SurfaceSize.Y * (1f - 2f * p.TextPadding / 100f);
            return Math.Max(1, (int)(height / lineHeight));
        }
    }
}
