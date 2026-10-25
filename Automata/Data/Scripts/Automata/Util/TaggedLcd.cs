using System.Collections.Generic;
using System.Text;

using System;

using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.Utils;
using Automata.Util.Strings;

namespace Automata.Util
{
    /// <summary>
    /// A block's status text on the LCDs of its construct whose name contains its tag ("[Orchestrator]"...), like the
    /// drone's tagged LCDs. Owner / faction panels only. Panels are looked up again every 10 s (or when the tag changes);
    /// text is written only when it changed. Server side (text panel content is synced).
    /// </summary>
    public class TaggedLcd
    {

        #region Timing
        private const int RESCAN_TICKS = 600;
        #endregion
        private readonly List<IMyTextPanel> panels = new List<IMyTextPanel>();
        private readonly List<IMyCubeGrid> grids = new List<IMyCubeGrid>();
        private string tag;
        private string lastText;
        private int lastScan = int.MinValue / 2;

        public int PanelCount { get { return panels.Count; } }

        public void Update(IMyTerminalBlock owner, string newTag, StringBuilder text)
        {
            if (owner == null || owner.CubeGrid == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            bool rescanned = false;
            if (newTag != tag || now - lastScan >= RESCAN_TICKS)
            {
                tag = newTag;
                lastScan = now;
                Rescan(owner);
                rescanned = true;
            }
            if (panels.Count == 0) return;
            string s = text.ToString();
            if (!rescanned && s == lastText) return;
            lastText = s;
            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (p == null || p.Closed) continue;
                if (p.ContentType != ContentType.TEXT_AND_IMAGE) p.ContentType = ContentType.TEXT_AND_IMAGE;
                p.WriteText(s);
            }
        }

        #region Two-column header (sprites)
        #region Timing
        private const int PAGE_TICKS = 180;              // header pages switch every 3 s
        #endregion
        private const int HEADER_ROWS = 5;               // items per column per page
        private const float HEADER_SHARE = 0.30f;        // of the panel's height
        private const string HEADER_FONT = "Monospace";  // fixed width: columns and truncation line up
        private readonly StringBuilder measure = new StringBuilder("M");
        private readonly StringBuilder key = new StringBuilder();

        /// <summary>
        /// Like <see cref="Update"/>, drawn with sprites: the top 30% holds two columns (left / right lists, a title each,
        /// 5 rows; more: pages switching every 3 s, "Page N/M" in the corner), the body text below. Panel colours, font
        /// size (body) and padding are the panel's own.
        /// </summary>
        public void UpdateWithHeader(IMyTerminalBlock owner, string newTag, string leftTitle, List<string> left,
                                     string rightTitle, List<string> right, StringBuilder body)
        {
            if (owner == null || owner.CubeGrid == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            bool rescanned = false;
            if (newTag != tag || now - lastScan >= RESCAN_TICKS)
            {
                tag = newTag;
                lastScan = now;
                Rescan(owner);
                rescanned = true;
            }
            if (panels.Count == 0) return;
            int pages = Math.Max(1, Math.Max((left.Count + HEADER_ROWS - 1) / HEADER_ROWS, (right.Count + HEADER_ROWS - 1) / HEADER_ROWS));
            int page = pages > 1 ? (now / PAGE_TICKS) % pages : 0;
            key.Clear().Append(page).Append('/').Append(pages).Append('|');
            for (int i = 0; i < left.Count; i++) key.Append(left[i]).Append('\n');
            key.Append('|');
            for (int i = 0; i < right.Count; i++) key.Append(right[i]).Append('\n');
            key.Append('|').Append(body);
            string s = key.ToString();
            if (!rescanned && s == lastText) return;
            lastText = s;
            string[] bodyLines = body.ToString().Split('\n');
            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (p == null || p.Closed || !p.IsFunctional) continue;
                DrawHeaderPanel(p, leftTitle, left, rightTitle, right, page, pages, bodyLines);
            }
        }

        private void DrawHeaderPanel(IMyTextPanel p, string leftTitle, List<string> left, string rightTitle, List<string> right,
                                     int page, int pages, string[] bodyLines)
        {
            if (p.ContentType != ContentType.SCRIPT) p.ContentType = ContentType.SCRIPT;
            if (p.Script != "") p.Script = "";
            p.ScriptBackgroundColor = p.BackgroundColor;
            Color fg = p.FontColor;
            Vector2 surface = p.SurfaceSize;
            float pad = surface.X * (p.TextPadding / 100f);
            Vector2 origin = (p.TextureSize - surface) * 0.5f + new Vector2(pad, pad);
            Vector2 inner = surface - new Vector2(pad * 2, pad * 2);
            Vector2 unit = Vector2.Zero;
            try { unit = p.MeasureStringInPixels(measure, HEADER_FONT, 1f); } catch (Exception) { }
            if (unit.Y <= 1f) unit = new Vector2(19f, 30f);
            // Header: title row + HEADER_ROWS + a little for the page label, in 30% of the height
            float headerH = inner.Y * HEADER_SHARE;
            float hLine = headerH / (HEADER_ROWS + 1.6f);
            float hScale = hLine / unit.Y;
            float colW = inner.X * 0.5f;
            int colChars = Math.Max(4, (int)((colW - unit.X * hScale) / (unit.X * hScale)));
            float bodyScale = MathHelper.Clamp(p.FontSize, 0.1f, 10f);
            float bLine = unit.Y * bodyScale;
            int bodyChars = Math.Max(4, (int)(inner.X / (unit.X * bodyScale)));
            using (var frame = p.DrawFrame())
            {
                frame.Add(new MySprite(SpriteType.TEXT, Fit(leftTitle, colChars), origin, null, fg, HEADER_FONT, TextAlignment.LEFT, hScale));
                frame.Add(new MySprite(SpriteType.TEXT, Fit(rightTitle, colChars), origin + new Vector2(colW, 0), null, fg, HEADER_FONT, TextAlignment.LEFT, hScale));
                for (int r = 0; r < HEADER_ROWS; r++)
                {
                    int k = page * HEADER_ROWS + r;
                    Vector2 row = origin + new Vector2(0, hLine * (r + 1));
                    if (k < left.Count) frame.Add(new MySprite(SpriteType.TEXT, Fit(left[k], colChars), row, null, fg, HEADER_FONT, TextAlignment.LEFT, hScale));
                    if (k < right.Count) frame.Add(new MySprite(SpriteType.TEXT, Fit(right[k], colChars), row + new Vector2(colW, 0), null, fg, HEADER_FONT, TextAlignment.LEFT, hScale));
                }
                if (pages > 1)
                    frame.Add(new MySprite(SpriteType.TEXT, Loc.F(MyStrings.Common_PageColonArg0SlashArg1, page + 1, pages),
                                           origin + new Vector2(inner.X, hLine * (HEADER_ROWS + 1)), null, fg, HEADER_FONT, TextAlignment.RIGHT, hScale * 0.8f));
                // Rule under the header, then the body
                float ruleY = origin.Y + headerH;
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(origin.X + inner.X * 0.5f, ruleY), new Vector2(inner.X, Math.Max(1f, hLine * 0.08f)), fg, null, TextAlignment.CENTER));
                Vector2 pos = new Vector2(origin.X, ruleY + hLine * 0.3f);
                float bottom = origin.Y + inner.Y;
                for (int i = 0; i < bodyLines.Length && pos.Y + bLine <= bottom + 0.5f; i++)
                {
                    if (bodyLines[i].Length > 0) frame.Add(new MySprite(SpriteType.TEXT, Fit(bodyLines[i], bodyChars), pos, null, fg, HEADER_FONT, TextAlignment.LEFT, bodyScale));
                    pos.Y += bLine;
                }
            }
        }

        private static string Fit(string s, int chars)
        {
            if (s == null) return "";
            return s.Length <= chars ? s : s.Substring(0, Math.Max(1, chars - 1)) + "~";
        }
        #endregion

        private void Rescan(IMyTerminalBlock owner)
        {
            panels.Clear();
            if (string.IsNullOrWhiteSpace(tag)) return;
            grids.Clear();
            MyAPIGateway.GridGroups.GetGroup(owner.CubeGrid, GridLinkTypeEnum.Mechanical, grids);
            for (int g = 0; g < grids.Count; g++)
                foreach (var p in grids[g].GetFatBlocks<IMyTextPanel>())
                    if (p.IsFunctional && p.CustomName != null && p.CustomName.Contains(tag) && Drone.AnchorDirectory.IsAllowed(owner.OwnerId, p.OwnerId))
                        panels.Add(p);
            grids.Clear();
        }
    }
}
