using System;
using System.Collections.Generic;

using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Util
{
    /// <summary>
    /// The drones working this block's jobs, by their keepalives (JOB_KEEPALIVE, every 60 s while on an awarded job).
    /// One unheard for SILENT_TICKS (10 min): a log line, an event (LCD) and, where 'broadcast' is set (only the orchestrator
    /// does), the chat line "NICKNAME: Drone NAME has not been heard of for 10 min! Last position GPS:..." - once per silence
    /// (heard again: armed again). A final report (completed / aborted) ends the watch; entries silent for FORGET_TICKS are
    /// dropped. Runtime only.
    /// </summary>
    public sealed class DroneWatch
    {
        private static readonly MyStringId StrNotHeard = MyStringId.GetOrCompute("Drone {0} has not been heard of for {1} min!");
        private static readonly MyStringId StrLastSeen = MyStringId.GetOrCompute("{0} Last known position {1}");
        private static readonly MyStringId StrLostInLane = MyStringId.GetOrCompute("{0}: lost in lane {1}");
        private static readonly MyStringId StrHeardAgain = MyStringId.GetOrCompute("Drone {0} heard again");

        public const int SILENT_TICKS = 10 * 60 * 60;
        private const int FORGET_TICKS = 30 * 60 * 60;
        private const int CHECK_EVERY = 5 * 60;

        private sealed class Entry { public uint JobId; public int Last; public bool Warned; public bool HasPos; public Vector3D Pos; }
        private readonly Dictionary<long, Entry> drones = new Dictionary<long, Entry>();
        private readonly List<long> scratch = new List<long>();
        private int nextCheck;

        public int Count { get { return drones.Count; } }

        /// <summary>A keepalive (or any sign of work) from 'drone' on 'jobId'.</summary>
        public void Heard(long drone, uint jobId, int now, Action<string> addEvent = null, Vector3DData? position = null)
        {
            if (drone == 0) return;
            Entry e;
            if (!drones.TryGetValue(drone, out e)) { e = new Entry { JobId = jobId, Last = now }; drones[drone] = e; }
            else
            {
                if (e.Warned && addEvent != null) addEvent(Loc.F(StrHeardAgain, Nicknames.Of(drone)));
                e.JobId = jobId;
                e.Last = now;
                e.Warned = false;
            }
            if (position.HasValue) { e.HasPos = true; e.Pos = position.Value.ToVector3D(); }
        }

        /// <summary>The drone reported its job ended: no longer watched.</summary>
        public void Done(long drone)
        {
            drones.Remove(drone);
        }

        /// <summary>
        /// Every few seconds: drones unheard for SILENT_TICKS are announced once. 'logPrefix' like "CC {0}" (the block's
        /// id goes in {0}); 'broadcast' sends the chat line through a Broadcast Controller on the block's construct.
        /// </summary>
        public void Check(int now, IMyCubeBlock block, bool broadcast, string logPrefix, long blockId, Action<string> addEvent)
        {
            if (drones.Count == 0 || now < nextCheck) return;
            nextCheck = now + CHECK_EVERY;
            scratch.Clear();
            foreach (var kv in drones)
            {
                var e = kv.Value;
                int silent = now - e.Last;
                if (silent > FORGET_TICKS) { scratch.Add(kv.Key); continue; }
                if (e.Warned || silent < SILENT_TICKS) continue;
                e.Warned = true;
                // Last seen flying a lane: said so (and the lane is suspended)
                var lanes = Pathfinding.PathfindingManager.Instance != null ? Pathfinding.PathfindingManager.Instance.Lanes : null;
                string lane = lanes != null ? lanes.Lost(kv.Key, now) : null;
                string text = lane != null ? Loc.F(StrLostInLane, Nicknames.Of(kv.Key), lane) : Loc.F(StrNotHeard, Nicknames.Of(kv.Key), SILENT_TICKS / 3600);
                if (e.HasPos)
                {
                    string name = (Nicknames.Of(kv.Key) + " last seen").Replace(':', ' ');
                    text = Loc.F(StrLastSeen, text, string.Format(System.Globalization.CultureInfo.InvariantCulture, "GPS:{0}:{1:F1}:{2:F1}:{3:F1}:", name, e.Pos.X, e.Pos.Y, e.Pos.Z));
                }
                string via = "broadcast off";
                bool sent = broadcast && block != null && BroadcastControllerSender.Send(block, Nicknames.Of(blockId) + ": " + text, out via);
                if (Log.DebugEnabled) Log.Debug(logPrefix + ": {1} (job {2}){3}", blockId, text, e.JobId, sent ? ", broadcast via " + via : " (" + via + ")");
                if (addEvent != null) addEvent(text);
            }
            for (int i = 0; i < scratch.Count; i++) drones.Remove(scratch[i]);
            scratch.Clear();
        }
    }
}
