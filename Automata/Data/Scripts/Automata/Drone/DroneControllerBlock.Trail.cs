using System;
using System.Collections.Generic;

using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// The way back. A drone gets into tight spots through precise checks (a weld site found with its real shape and
    /// the neighbours' real shapes, a tilted approach, a turn checked with its exact box), but the planner's ways out
    /// use general ones: the any-attitude sphere of the hull (escape spots, Lazy Theta*'s cells, reposition spots).
    /// Wedged in a frame, none of those fit anywhere near and the planner finds nothing, though the drone got in.
    ///  - Where it flew since it left a dock (hull centres every TRAIL_STEP m, the last TRAIL_MAX) is kept.
    ///  - No way from here: back along it, holding the attitude it has now, each stretch checked again (built over
    ///    meanwhile: no further), to the first point where the hull fits in any attitude and a route from there works.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const double TRAIL_STEP = 1.5;      // m between kept points
        private const int TRAIL_MAX = 64;           // points kept (~100 m)
        private const double TRAIL_JUMP = 50;       // m moved between two records: not flown (a carrier, a teleport): start again
        private const int TRAIL_PLANS_MAX = 3;      // full routes planned from points of the way back, per failed route

        private readonly List<Vector3D> trail = new List<Vector3D>();
        private readonly List<Vector3D> retracePoints = new List<Vector3D>();

        // UpdateBeforeSimulation10 (server, AI on): a point every TRAIL_STEP m while out of the dock
        private void RecordTrail10()
        {
            if (preflightStage != 0 || isParked || localHullBox.Max.X < localHullBox.Min.X || DockedConnector() != null)
            {
                if (trail.Count > 0) trail.Clear();
                return;
            }
            Vector3D p = HullCentreWorld;
            if (trail.Count > 0)
            {
                double d2 = Vector3D.DistanceSquared(trail[trail.Count - 1], p);
                if (d2 > TRAIL_JUMP * TRAIL_JUMP) trail.Clear();
                else if (d2 < TRAIL_STEP * TRAIL_STEP) return;
            }
            if (trail.Count >= TRAIL_MAX) trail.RemoveAt(0);
            trail.Add(p);
        }

        /// <summary>
        /// PlanPath found no way from 'start': back the way the drone came (holding its attitude) to the first point of
        /// it where the hull fits in any attitude and a route to 'goal' works from there. The way back becomes the
        /// route's first legs. False: no trail, built over, or no route from any point of it.
        /// </summary>
        private bool TryRetrace(Vector3D start, Vector3D goal, List<Vector3D> points, int count, bool avoidObservationArea, bool areaOnlyFromOutside,
                                Vector3D startForward, Vector3D startUp, bool looseGoal, PathfindingManager pm, ref PathQuery q)
        {
            if (trail.Count == 0) return false;
            retracePoints.Clear();
            Vector3D from = start;
            double back = 0;
            int plans = 0;
            Vector3D hit;
            IMyEntity e;
            string stop = null;
            for (int i = trail.Count - 1; i >= 0 && plans < TRAIL_PLANS_MAX; i--)
            {
                Vector3D p = trail[i];
                if (Vector3D.DistanceSquared(p, from) < 0.25) continue;
                pm.BeginPlan();
                if (pm.SegmentBlocked(ref q, from, p, 0.5, out hit, out e))
                {
                    stop = PathfindingManager.Describe(pm.LastHitBlock);
                    break;   // built over / something parked there since: no further back
                }
                back += Vector3D.Distance(from, p);
                retracePoints.Add(p);
                from = p;
                if (!pm.HullFits(ref q, p)) continue;   // still tight here: further back
                plans++;
                if (points.Count > count) points.RemoveRange(count, points.Count - count);
                if (!PlanPathFrom(p, goal, points, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal)) continue;
                points.InsertRange(count, retracePoints);
                legOffset = count;
                for (int k = 0; k < retracePoints.Count; k++)
                {
                    legForwards.Insert(0, startForward);   // back the way it came: the attitude held
                    legUps.Insert(0, startUp);
                }
                if (Log.DebugEnabled) Log.Debug("Drone {0}: no way from where it is; back the way it came, {1:F0} m ({2} points), and on from there",
                          Entity.EntityId, back, retracePoints.Count);
                retracePoints.Clear();
                RecoverSucceeded();
                return true;
            }
            if (points.Count > count) points.RemoveRange(count, points.Count - count);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: no way back the way it came either ({1:F0} m of {2} trail points tried, {3} route(s) from it{4})", Entity.EntityId,
                      back, trail.Count, plans, stop != null ? "; built over: " + stop : "");
            retracePoints.Clear();
            return false;
        }

        // Escape spots: the hull fits there in any attitude, or (the leg there holds the attitude) as it is, with a margin
        private const double ESCAPE_HELD_MARGIN = 0.25;

        private bool EscapeSpotFits(PathfindingManager pm, ref PathQuery q, Vector3D spot, Vector3D forward, Vector3D up)
        {
            if (pm.HullFits(ref q, spot)) return true;
            MatrixD f = MatrixD.CreateWorld(Vector3D.Zero, forward, up);
            return pm.HullFitsOriented(ref q, spot, ref f, ESCAPE_HELD_MARGIN);
        }

        /// <summary>
        /// m the hull (centre 'hullCentre', held attitude) can move along 'dir' (unit) before what is in the way, up to
        /// 'len'; 'blocker': what stops it (null: nothing). What it already overlaps behind it doesn't count.
        /// </summary>
        private double FreeRun(Vector3D hullCentre, Vector3D dir, double len, out string blocker)
        {
            blocker = null;
            var pm = PathfindingManager.Instance;
            if (pm == null || len < 0.1 || localHullBox.Max.X < localHullBox.Min.X) return len;
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            pm.BeginPlan();
            Vector3D hit;
            IMyEntity e;
            if (!pm.SegmentBlocked(ref q, hullCentre, hullCentre + dir * len, 0, out hit, out e)) return len;
            blocker = PathfindingManager.Describe(pm.LastHitBlock) ?? (e != null ? e.DisplayName : "?");
            // The obstacle's near side along dir; the hull's front is its half size along dir ahead of the centre
            Vector3D dl = Vector3D.TransformNormal(dir, MatrixD.Transpose(flightState.WorldMatrix));
            Vector3D h = localHullBox.HalfExtents;
            double half = Math.Abs(dl.X) * h.X + Math.Abs(dl.Y) * h.Y + Math.Abs(dl.Z) * h.Z;
            return MathHelper.Clamp(Vector3D.Dot(hit - hullCentre, dir) - half - FREE_RUN_MARGIN, 0, len);
        }

        // Docking / undocking: no turning to another attitude for a route (held Lazy Theta* routes), no contact replans
        // (drones turned / went round again next to the platform and hit it). The zone: around the connector being
        // docked at, and around the end of the departure from the one just left.
        private const double DOCK_AREA_MIN = 25;   // m, or 2 x the room to turn
        private bool dockAreaSet;
        private Vector3D dockAreaCentre;

        private double DockAreaRadius { get { return Math.Max(DOCK_AREA_MIN, 2 * ManoeuvreDistance()); } }

        private bool NearDockArea(Vector3D p)
        {
            double r = DockAreaRadius;
            if (dockAreaSet && Vector3D.DistanceSquared(p, dockAreaCentre) < r * r) return true;
            if (departureSet && Vector3D.DistanceSquared(p, departureEnd) < r * r) return true;
            if (dockingHomeId != 0)
            {
                var c = AnchorDirectory.Resolve(block, dockingHomeId);
                if (c != null && Vector3D.DistanceSquared(p, c.GetPosition()) < r * r) return true;
            }
            return false;
        }

        private const double FREE_RUN_MARGIN = 0.5;   // m kept from what a back-out runs towards
        private const double BACK_OUT_MIN = 0.5;      // m: a shorter back-out isn't flown
    }
}
