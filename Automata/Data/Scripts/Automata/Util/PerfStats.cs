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
        public const int PathPlan = 7;       // main thread: one PlanRoute (stages 1-2, accepting a search's answer)
        public const int PathPrepare = 8;    // main thread: scene captured for the queued searches (block trees built), per tick
        public const int PathWarm = 9;       // main thread: scene warm-up of the areas expected to be busy, per tick
        public const int PathSearch = 10;    // worker threads: one Lazy Theta* search (not main-thread time)
        public const int PathSearchWait = 11; // a search from queued until its answer is back (not main-thread time)
        public const int PathAccept = 12;    // main thread: a search's answer checked with rays and doors
        private const int COUNT = 13;

        private static readonly string[] Names =
        {
            "LCD feed packet", "(unused)", "LCD update (computers)", "LCD feed packet (drones)",
            "CC scan", "CC group scanned", "CC group cached",
            "path plan (main thread)", "path search scene capture (main thread)", "path warm-up (main thread)",
            "path search (worker threads)", "path search queued to answered", "path search answer checked (main thread)",
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

        /// <summary>Main thread: a time measured elsewhere (a worker's search, reported by its completion callback).</summary>
        public static void Record(int timer, double ms)
        {
            if (!Enabled) return;
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
