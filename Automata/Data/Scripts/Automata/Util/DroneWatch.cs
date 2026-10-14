using System;
using System.Collections.Generic;

using VRage.Game.ModAPI;
using VRage.Utils;

using Automata.Util.Logging;

namespace Automata.Util
{
    /// <summary>
    /// The drones working this block's jobs, by their keepalives (JOB_KEEPALIVE, every 20 s while on an awarded job).
    /// One unheard for SILENT_TICKS: a log line, an event (LCD) and, with "Use broadcast controller", the chat line
    /// "Drone NAME has not been heard of for 60s!" - once per silence (heard again: armed again). A final report
    /// (completed / aborted) ends the watch; entries silent for FORGET_TICKS are dropped. Runtime only.
    /// </summary>
    public sealed class DroneWatch
    {
        private static readonly MyStringId StrNotHeard = MyStringId.GetOrCompute("Drone {0} has not been heard of for {1}s!");
        private static readonly MyStringId StrLostInLane = MyStringId.GetOrCompute("{0}: lost in lane {1}");
        private static readonly MyStringId StrHeardAgain = MyStringId.GetOrCompute("Drone {0} heard again");

        public const int SILENT_TICKS = 60 * 60;
        private const int FORGET_TICKS = 30 * 60 * 60;
        private const int CHECK_EVERY = 5 * 60;

        private sealed class Entry { public uint JobId; public int Last; public bool Warned; }
        private readonly Dictionary<long, Entry> drones = new Dictionary<long, Entry>();
        private readonly List<long> scratch = new List<long>();
        private int nextCheck;

        public int Count { get { return drones.Count; } }

        /// <summary>A keepalive (or any sign of work) from 'drone' on 'jobId'.</summary>
        public void Heard(long drone, uint jobId, int now, Action<string> addEvent = null)
        {
            if (drone == 0) return;
            Entry e;
            if (!drones.TryGetValue(drone, out e)) { drones[drone] = new Entry { JobId = jobId, Last = now }; return; }
            if (e.Warned && addEvent != null) addEvent(Loc.F(StrHeardAgain, Nicknames.Of(drone)));
            e.JobId = jobId;
            e.Last = now;
            e.Warned = false;
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
                string text = lane != null ? Loc.F(StrLostInLane, Nicknames.Of(kv.Key), lane) : Loc.F(StrNotHeard, Nicknames.Of(kv.Key), SILENT_TICKS / 60);
                string via = "broadcast off";
                bool sent = broadcast && block != null && BroadcastControllerSender.Send(block, text, out via);
                Log.Info(logPrefix + ": {1} (job {2}){3}", blockId, text, e.JobId, sent ? ", broadcast via " + via : " (" + via + ")");
                if (addEvent != null) addEvent(text);
            }
            for (int i = 0; i < scratch.Count; i++) drones.Remove(scratch[i]);
            scratch.Clear();
        }
    }
}
