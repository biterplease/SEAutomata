using Sandbox.ModAPI;
using System;

namespace Automata.Util
{
    public static class TimeUtil
    {
        public const float TICKS_PER_SECOND = 60.0f;
        public const float MILLISECONDS_PER_TICK = 1000.0f / TICKS_PER_SECOND; // 16.666... ms
        public const float TICKS_PER_MILLISECOND = TICKS_PER_SECOND / 1000.0f; // 0.06 ticks
        public static DateTime Epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static int SecondsToTicks(uint seconds)
        {
            return (int)(seconds * TICKS_PER_SECOND);
        }
        public static float TickToSeconds(uint ticks)
        {
            return (float)ticks / TICKS_PER_SECOND;
        }
        /// <summary>
        /// Convert ticks to MS, rounded down
        /// </summary>
        /// <param name="ticks"></param>
        /// <returns></returns>
        public static int TickToMs(int ticks)
        {
            return (int)(ticks * MILLISECONDS_PER_TICK);
        }
        /// <summary>
        /// Convert milliseconds to server ticks
        /// </summary>
        /// <param name="ms">Time in milliseconds</param>
        /// <returns>Equivalent number of server ticks (rounded)</returns>
        public static int MsToTick(int ms)
        {
            return (int)Math.Floor(ms * TICKS_PER_MILLISECOND);
        }
        /// <summary>
        /// Convert TimeSpan to server ticks
        /// </summary>
        /// <param name="timeSpan">TimeSpan to convert</param>
        /// <returns>Equivalent number of server ticks (rounded)</returns>
        public static int TimeSpanToTick(TimeSpan timeSpan)
        {
            return MsToTick((int)timeSpan.TotalMilliseconds);
        }

        /// <summary>
        /// Convert server ticks to TimeSpan
        /// </summary>
        /// <param name="ticks">Number of server ticks</param>
        /// <returns>Equivalent TimeSpan</returns>
        public static TimeSpan TickToTimeSpan(int ticks)
        {
            return TimeSpan.FromMilliseconds(TickToMs(ticks));
        }

        public static ulong DateTimeToTimestamp(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Local)
                dateTime = dateTime.ToUniversalTime();
            TimeSpan tSpan = dateTime.Subtract(Epoch);
            return (ulong)tSpan.TotalMilliseconds;
        }
        public static DateTime TimestampToDateTime(ulong timestamp)
        {
            TimeSpan timeSpan = TimeSpan.FromMilliseconds(timestamp);
            return Epoch.Add(timeSpan);
        }
    }
}