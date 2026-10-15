using System;
using System.Diagnostics;
using System.Text;

using Sandbox.ModAPI;

using Automata.Config;
using Automata.Util.Logging;

namespace Automata.Util
{
    /// <summary>
    /// Debug-only timers around the costly parts (LCD drawing, construction scans). Nothing is measured unless the
    /// server's log level includes Debug; a summary line per timer is logged every minute.
    /// Usage: <c>double t = PerfStats.Start(); ...; PerfStats.Stop(PerfStats.LcdDraw, t);</c>
    /// </summary>
    public static class PerfStats
    {
        public const int LcdDraw = 0;        // BlockLcd.Publish: content key + feed packet (computers, only with viewers)
        public const int LcdRescan = 1;      // (unused)
        public const int LcdBuild = 2;       // a computer's UpdateLcd: viewer check, header + log text, plus the Publish above
        public const int DroneLcd = 3;       // DroneStatusDisplay.Flush: feed packet (drones, only with viewers)
        public const int CcScan = 4;         // a whole Construction Computer scan
        public const int CcGroup = 5;        // one grid group scanned for real (ConstructionComputer.ScanArea)
        public const int CcGroupCached = 6;  // one grid group answered from the cache (signature check only)
        private const int COUNT = 7;

        private static readonly string[] Names =
        {
            "LCD feed packet", "(unused)", "LCD update (computers)", "LCD feed packet (drones)",
            "CC scan", "CC group scanned", "CC group cached",
        };

        /// <summary>True when timers run (server log level includes Debug).</summary>
        public static bool Enabled { get { return (Log.LogLevel & LogLevel.Debug) != 0; } }

        private const int REPORT_TICKS = 60 * 60;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly int[] count = new int[COUNT];
        private static readonly double[] total = new double[COUNT];
        private static readonly double[] max = new double[COUNT];
        private static int periodStart = -1;

        /// <summary>The time to give <see cref="Stop"/> (ms on a private clock), or -1 when timers are off.</summary>
        public static double Start()
        {
            return Enabled ? clock.Elapsed.TotalMilliseconds : -1;
        }

        /// <summary>Milliseconds since 'start' (0 when timers are off).</summary>
        public static double Since(double start)
        {
            return start < 0 ? 0 : clock.Elapsed.TotalMilliseconds - start;
        }

        public static void Stop(int timer, double start)
        {
            if (start < 0) return;
            double ms = clock.Elapsed.TotalMilliseconds - start;
            count[timer]++;
            total[timer] += ms;
            if (ms > max[timer]) max[timer] = ms;
        }

        /// <summary>Server, every tick: a summary of the last minute, once a minute.</summary>
        public static void Update(int frame)
        {
            if (!Enabled) return;
            if (periodStart < 0) { periodStart = frame; return; }
            int ticks = frame - periodStart;
            if (ticks < REPORT_TICKS) return;
            periodStart = frame;
            double budget = ticks * (1000.0 / 60.0);   // ms of server time in the period (60 ticks/s)
            var sb = new StringBuilder();
            for (int i = 0; i < COUNT; i++)
            {
                if (count[i] == 0) continue;
                sb.Length = 0;
                sb.Append(Names[i]).Append(": ").Append(count[i]).Append("x, ").Append(total[i].ToString("F2")).Append(" ms total, avg ")
                  .Append((total[i] / count[i]).ToString("F3")).Append(" ms, max ").Append(max[i].ToString("F2")).Append(" ms, ")
                  .Append((total[i] * 100.0 / budget).ToString("F3")).Append("% of server time");
                if (Log.DebugEnabled) Log.Debug("Perf ({0:F0} s): {1}", ticks / 60.0, sb.ToString());
                count[i] = 0;
                total[i] = 0;
                max[i] = 0;
            }
        }
    }
}
