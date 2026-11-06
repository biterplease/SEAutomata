using System;
using System.Collections.Generic;

using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Flight lane obstructions (TODO/25): a stretch of a lane the drone's route check found blocked by rock, a tree or a
    /// static grid (a moving grid may get out of the way: not reported), with the points it flew round it. Held until the
    /// lane's orchestrator hears it: sent at once with a keepalive when in antenna range, and with the job's report for sure
    /// (queued until delivered, saved with the job). The orchestrator redraws the lane (FlightLanes.Obstructed). Also the
    /// scout's finds (ore hits) that decide whether an order lane stays.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private readonly List<LaneObstruction> laneObstructions = new List<LaneObstruction>();
        private int jobFinds;                               // ore hits the job's scans found (scout jobs: their order lane stays)
        private const int LANE_OBSTRUCTIONS_MAX = 16;

        private void QueueLaneObstruction(int lane, bool ab, string laneName, Vector3D a, Vector3D b, List<Vector3D> points, int before, bool blocked, IMyEntity e)
        {
            byte kind = FlightLanes.ObstacleKind(e);
            string what = e == null ? "?" : !string.IsNullOrEmpty(e.DisplayName) ? e.DisplayName : kind == 1 ? "rock" : kind == 2 ? "a tree" : e.GetType().Name;
            if (kind == 0 || lane == 0)
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: lane {1}: a stretch needed a way round ({2}): not reported (it may move)", Entity.EntityId, laneName, what);
                return;
            }
            for (int i = 0; i < laneObstructions.Count; i++)   // the same stretch again (a replan): once
                if (laneObstructions[i].Lane == lane && Vector3D.DistanceSquared(laneObstructions[i].A.ToVector3D(), a) < 100
                    && Vector3D.DistanceSquared(laneObstructions[i].B.ToVector3D(), b) < 100) return;
            if (laneObstructions.Count >= LANE_OBSTRUCTIONS_MAX) laneObstructions.RemoveAt(0);
            var o = new LaneObstruction
            {
                Id = DateTime.UtcNow.Ticks, Lane = lane, AB = ab, Kind = kind, What = what,
                A = Vector3DData.FromVector3D(a), B = Vector3DData.FromVector3D(b), Detour = new List<Vector3DData>(),
            };
            if (!blocked) for (int i = before; i < points.Count; i++) o.Detour.Add(Vector3DData.FromVector3D(points[i]));
            laneObstructions.Add(o);
            lastKeepaliveFrame = int.MinValue / 2;   // heard at once when in range (else with the job's report)
            if (Log.DebugEnabled) Log.Debug("Drone {0}: lane {1}: {2} in it, {3}: reported to its orchestrator", Entity.EntityId, laneName, what,
                                            blocked ? "no way round" : o.Detour.Count + " point(s) round it");
        }
    }
}
