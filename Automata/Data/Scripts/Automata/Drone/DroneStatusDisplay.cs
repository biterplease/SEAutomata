using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.Utils;
using Automata.Util;

namespace Automata.Drone
{
    public enum DisplayAlertLevel : byte
    {
        None,
        Warning,
        Error,
    }

    /// <summary>
    /// Drone log shown on tagged LCDs of the drone's own grid (no subgrids, no connected grids) that are set to the
    /// "Automata Log" app (see LcdFeed.cs). Newest line at the bottom; optional header: state, battery and hydrogen levels,
    /// then one line reserved for the current error (red) or warning (yellow), then a rule.
    /// The server only keeps the lines; the feed is built and sent when a client is looking at one of the LCDs and
    /// something changed, and the app draws it on the client.
    /// </summary>
    public class DroneStatusDisplay
    {
        #region UI strings
        private static readonly MyStringId StrHeaderLevels = MyStringId.GetOrCompute("POW:{0}%  H2:{1}%");
        #endregion

        public const string FONT = "Monospace";          // fixed width, so the header columns line up
        private const int CAPACITY = 40;                 // lines kept (and sent); panels show as many as fit

        private readonly string[] lines = new string[CAPACITY];
        private int head;       // index of the oldest line
        private int count;

        private readonly StringBuilder sb = new StringBuilder(128);
        private readonly LcdFeeder feeder = new LcdFeeder();

        private bool dirty = true;
        private string headerState = "";
        private int headerPower = -1, headerH2 = -1;
        private string alertText = "";
        private DisplayAlertLevel alertLevel;

        public void Add(string line)
        {
            if (line == null) line = "";
            if (line.IndexOf('\n') >= 0)
            {
                // A log entry of several lines: one row each (its other lines come indented)
                var parts = line.Split('\n');
                for (int i = 0; i < parts.Length; i++) Add(parts[i]);
                return;
            }
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
            dirty = true;
        }

        /// <summary>The drone's controller and its LCD tag (LCDs on the controller's own grid only). Server only.</summary>
        public void Bind(IMyTerminalBlock controller, string tag)
        {
            if (feeder.Owner != controller || feeder.Tag != (tag ?? "")) dirty = true;
            feeder.Bind(controller, tag, true);
        }

        /// <summary>Sends the feed to the clients looking at the drone's LCDs, if anything changed. Cheap no-op otherwise.</summary>
        public void Flush(DroneControllerSettings settings)
        {
            if (settings == null) return;
            if (!MyAPIGateway.Multiplayer.IsServer) { dirty = false; return; }
            if (feeder.ForcePending) dirty = true;   // a viewer arrived: it gets the current state
            if (!dirty) return;
            dirty = false;
            if (!feeder.HasViewers) return;          // the lines are kept; nothing is built for nobody
            double t = Util.PerfStats.Start();
            try { Send(settings); }
            finally { Util.PerfStats.Stop(Util.PerfStats.DroneLcd, t); }
        }

        private void Send(DroneControllerSettings settings)
        {
            var packet = new LcdFeedPacket
            {
                Foreground = settings.LcdForeground,
                Background = settings.LcdBackground,
                FontSize = MathHelper.Clamp(settings.LcdFontSize, 0.1f, 2f),
                Lines = new List<string>(count),
            };
            if (settings.LcdShowHeader)
            {
                sb.Clear();
                sb.Append(headerState.Length > 10 ? headerState.Substring(0, 10) : headerState.PadRight(10));
                sb.Append(' ').AppendLoc(StrHeaderLevels, Pct(headerPower), Pct(headerH2));
                packet.Header = new List<string>(1) { sb.ToString() };
                packet.AlertRow = true;
                packet.AlertLevel = (byte)alertLevel;
                packet.Alert = alertText;
            }
            for (int k = 0; k < count; k++) packet.Lines.Add(lines[(head + k) % CAPACITY]);
            feeder.Send(packet);
        }

        private static string Pct(int v)
        {
            if (v < 0) return "  -";
            return v.ToString().PadLeft(3);
        }
    }
}
