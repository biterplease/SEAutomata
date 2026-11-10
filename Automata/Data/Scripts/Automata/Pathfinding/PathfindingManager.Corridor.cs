using System;
using System.Collections.Generic;

using VRage.Game;
using VRageMath;

using Automata.Util;
using Automata.Util.Strings;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Entry corridors (TODO/16): the way in to / out of a dock pose in a tight space (a hangar, a bay, a ship's dock), worked
    /// out backwards from the docked hull P0: P1 is P0 moved CORRIDOR_SLIDE out along the target connector's axis; from
    /// there, holding the dock attitude, straight out along the axis, or out and then across along one of the target
    /// block's axes (an L: up off the floor, out through the door), to the nearest point T with room to turn
    /// (TurnSweepFree). Bounded search, no A*: CORRIDOR_STEP along the axis, 1 m across, at most CORRIDOR_TURN_CHECKS
    /// turn checks. The drone flies it (DroneControllerBlock.Corridor).
    /// </summary>
    public sealed partial class PathfindingManager
    {
        public const double CORRIDOR_SLIDE = 0.5;            // m: P1 -> P0, the final slide onto the connector
        private const double CORRIDOR_STEP = 0.25;           // m: candidate heights along the connector axis
        private const double CORRIDOR_ACROSS_STEP = 1.0;     // m: candidate distances across it
        private const int CORRIDOR_AXIS_STEPS = 60;          // along the axis, at most (15 m)
        private const int CORRIDOR_TURN_CHECKS = 600;        // exit checks per search (the expensive part)
        private const double CORRIDOR_EXIT_EXTRA = 3.0;      // m: T moved this much further out when clear (room for the route to arrive)
        private const double CORRIDOR_RISE_MAX = 6.0;        // m: out of a door at floor level, up this much at most to find room to turn
        private const double CORRIDOR_RISE_STEP = 0.5;
        private readonly System.Diagnostics.Stopwatch corridorWatch = new System.Diagnostics.Stopwatch();

        private struct CorridorCandidate
        {
            public double Cost;
            public Vector3D M, T, Out;
        }
        private readonly List<CorridorCandidate> corridorCandidates = new List<CorridorCandidate>();

        /// <summary>The clearances a corridor is searched with: the configured one, then the minimum (tight doors).</summary>
        public static void CorridorClearances(out double normal, out double tight)
        {
            var cfg = AutomataSession.GetConfig();
            normal = cfg != null ? cfg.Pathfinding.DockClearance : 0.5;
            tight = cfg != null ? Math.Min(normal, cfg.Pathfinding.DockClearanceMin) : 0.2;
        }

        /// <summary>
        /// FindCorridor with the configured clearance, then with the minimum one. 'berth': the one that worked; 'exitDir':
        /// the way out at the corridor's outer end. 'turnMargin': room kept round the hull where it turns (at least the
        /// route's ObstacleClearance). 'q': the drone's query (SeeAll, StrictEnds, the connector itself ignored).
        /// </summary>
        public bool FindCorridorAny(ref PathQuery q, Vector3D p0, Vector3D axis, ref MatrixD att, ref MatrixD frame, double turnMargin,
                                    List<Vector3D> pts, out string how, out double berth, out Vector3D exitDir)
        {
            double normal, tight;
            CorridorClearances(out normal, out tight);
            berth = normal;
            if (FindCorridor(q, p0, axis, ref att, ref frame, turnMargin, pts, out how, out exitDir, normal)) return true;
            if (tight >= normal - 1e-3) return false;
            string howTight;
            berth = tight;
            if (FindCorridor(q, p0, axis, ref att, ref frame, turnMargin, pts, out howTight, out exitDir, tight)) { how = Loc.F(MyStrings.Drone_Tight, howTight); return true; }
            how = Loc.F(MyStrings.Drone_MM, normal, how, tight, howTight);
            return false;
        }

        /// <summary>
        /// The entry corridor of a dock pose: 'p0' the hull centre docked (world), 'axis' out of the target connector,
        /// 'att' the dock attitude (rotation), 'frame' the target connector's matrix (its axes: the directions tried).
        /// 'pts': hull centres in flight order inbound, T (room to turn) ... P1 (the slide's start); one point when T is
        /// P1. False (with why): no room at P1, or no way out within the limits.
        /// </summary>
        private bool FindCorridor(PathQuery q, Vector3D p0, Vector3D axis, ref MatrixD att, ref MatrixD frame, double turnMargin, List<Vector3D> pts,
                                  out string how, out Vector3D exitDir, double berth)
        {
            pts.Clear();
            exitDir = Vector3D.Zero;
            q.Berth = berth;
            corridorWatch.Reset();
            corridorWatch.Start();
            var cfg = AutomataSession.GetConfig().Pathfinding;
            double maxLen = cfg.DockCorridorMaxLength;
            MatrixD rot = att;
            rot.Translation = Vector3D.Zero;
            // The corridor's outer end T: where the route takes over, so room to turn there with the route's own
            // clearance (ObstacleClearance) around the hull - not merely room to turn inside a big hangar the route
            // couldn't reach through its door
            double exitMargin = Math.Max(turnMargin, cfg.ObstacleClearance);
            PathQuery qt = q;
            qt.StrictEnds = false;
            qt.Berth = exitMargin;
            BeginPlan();
            int turnChecks = 0, sweeps = 0;
            axis.Normalize();

            // P1 itself, with the berth across the axis (along it the hull is about to touch the connector; the floor
            // around a flush connector is that close too)
            Vector3D p1 = p0 + axis * CORRIDOR_SLIDE;
            Vector3D axisLocal = Vector3D.TransformNormal(axis, MatrixD.Transpose(rot));
            Vector3D grow = new Vector3D(1 - Math.Abs(axisLocal.X), 1 - Math.Abs(axisLocal.Y), 1 - Math.Abs(axisLocal.Z)) * q.Berth;
            var box = new MyOrientedBoundingBoxD(p1, q.LocalHull.HalfExtents + grow, Quaternion.CreateFromRotationMatrix(rot));
            if (q.LocalHull.Max.X >= q.LocalHull.Min.X && !BoxFree(ref q, ref box, false))   // (blocks: the corridor is about the station)
            {
                how = Loc.F(MyStrings.Drone_NoRoomMOff, CORRIDOR_SLIDE, Describe(LastHitBlock), q.Berth);
                return false;
            }
            double axisClear = Math.Min(ClearRun(ref q, ref rot, p1, axis, maxLen, q.Berth), CORRIDOR_AXIS_STEPS * CORRIDOR_STEP);
            sweeps++;

            // 1. Straight out along the axis: the first height with room to turn
            double best = double.MaxValue;
            Vector3D bestT = p1, bestM = p1, bestOut = axis;
            for (double s = 0; s <= axisClear + 1e-6 && turnChecks < CORRIDOR_TURN_CHECKS; s += CORRIDOR_STEP)
            {
                Vector3D m = p1 + axis * s;
                turnChecks++;
                if (TurnSweepFree(ref qt, m, ref rot, exitMargin)) { best = s; bestT = bestM = m; break; }
            }

            // 2. Out along the axis, then across it along one of the target block's axes: only where shorter
            corridorCandidates.Clear();
            for (double s = 0; s <= axisClear + 1e-6 && s + CORRIDOR_ACROSS_STEP < best; s += CORRIDOR_STEP)
            {
                Vector3D m = p1 + axis * s;
                for (int d = 0; d < 6; d++)
                {
                    Vector3D dir = frame.GetDirectionVector((Base6Directions.Direction)d);
                    if (Math.Abs(Vector3D.Dot(dir, axis)) > 0.1) continue;
                    double reach = Math.Min(maxLen, best - s);
                    double clear = ClearRun(ref q, ref rot, m, dir, reach, q.Berth);
                    sweeps++;
                    for (double k = CORRIDOR_ACROSS_STEP; k <= clear + 1e-6; k += CORRIDOR_ACROSS_STEP)
                        corridorCandidates.Add(new CorridorCandidate { Cost = s + k, M = m, T = m + dir * k, Out = dir });
                }
            }
            corridorCandidates.Sort((x, y) => x.Cost.CompareTo(y.Cost));
            // Out through a door at floor level the turn may not fit at that height (the deck outside): up from there
            // (away from gravity; in space along the axis), where clear, a little at a time
            Vector3D rise = q.GravityUp.LengthSquared() > 0.5 ? q.GravityUp : axis;
            double bestRise = 0;
            for (int i = 0; i < corridorCandidates.Count && turnChecks < CORRIDOR_TURN_CHECKS; i++)
            {
                var c = corridorCandidates[i];
                if (c.Cost >= best) break;
                turnChecks++;
                double up = 0;
                bool room = TurnSweepFree(ref qt, c.T, ref rot, exitMargin);
                if (!room && Math.Abs(Vector3D.Dot(rise, c.Out)) < 0.1)
                {
                    double riseClear = ClearRun(ref q, ref rot, c.T, rise, Math.Min(CORRIDOR_RISE_MAX, best - c.Cost), q.Berth);
                    sweeps++;
                    for (up = CORRIDOR_RISE_STEP; up <= riseClear + 1e-6 && !room && turnChecks < CORRIDOR_TURN_CHECKS; up += CORRIDOR_RISE_STEP)
                    {
                        turnChecks++;
                        room = TurnSweepFree(ref qt, c.T + rise * up, ref rot, exitMargin);
                        if (room) break;
                    }
                }
                if (!room || c.Cost + up >= best) continue;
                best = c.Cost + up;
                bestT = c.T;
                bestM = c.M;
                bestOut = c.Out;
                bestRise = up;
                break;
            }
            int candidates = corridorCandidates.Count;
            corridorCandidates.Clear();
            corridorWatch.Stop();
            if (best == double.MaxValue)
            {
                // Why, for the log: what stops each direction at P1 (hull size across it, clear length, the block)
                var sb = new System.Text.StringBuilder();
                string axisBlock;
                ClearRun(ref q, ref rot, p1, axis, maxLen, q.Berth, out axisBlock);
                sb.AppendLoc(MyStrings.Drone_NoRoomToTurnWithin, axisClear, axisBlock != null ? " (" + axisBlock + ")" : "");
                for (int d = 0; d < 6; d++)
                {
                    Vector3D dir = frame.GetDirectionVector((Base6Directions.Direction)d);
                    if (Math.Abs(Vector3D.Dot(dir, axis)) > 0.1) continue;
                    string blocker;
                    double clear = ClearRun(ref q, ref rot, p1, dir, maxLen, q.Berth, out blocker);
                    sb.AppendLoc(MyStrings.Drone_MHullMAcross, (Base6Directions.Direction)d, clear,
                                    2 * HullHalfSizeAlong(ref q, ref rot, Vector3D.Cross(dir, axis)),
                                    blocker != null ? ", " + blocker : "");
                }
                sb.AppendLoc(MyStrings.Drone_ClearanceMCandidatesTurnChecks, q.Berth, candidates, turnChecks, sweeps,
                                corridorWatch.Elapsed.TotalMilliseconds);
                how = sb.ToString();
                return false;
            }
            // A little further out where clear (up, after a rise): more room for the route to arrive and to turn
            exitDir = bestRise > 0 ? rise : bestOut;
            Vector3D turnAt = bestT + rise * bestRise;
            double extra = ClearRun(ref q, ref rot, turnAt, exitDir, CORRIDOR_EXIT_EXTRA, q.Berth);
            turnAt += exitDir * extra;
            pts.Add(turnAt);
            if (bestRise > 0) pts.Add(bestT);   // the corner outside the door
            if (Vector3D.DistanceSquared(bestM, bestT) > 0.01 && Vector3D.DistanceSquared(bestM, p1) > 0.01) pts.Add(bestM);
            if (Vector3D.DistanceSquared(turnAt, p1) > 0.01 && Vector3D.DistanceSquared(pts[pts.Count - 1], p1) > 0.01) pts.Add(p1);
            bool straight = Vector3D.DistanceSquared(bestM, bestT) < 0.01 || Vector3D.Dot(bestOut, axis) > 0.9;
            how = straight
                ? string.Format("straight out {0:F1} m", Vector3D.Distance(p1, bestT) + CORRIDOR_SLIDE)
                : string.Format("{0:F1} m out along the axis, then {1:F1} m across{2}", Vector3D.Distance(p1, bestM) + CORRIDOR_SLIDE, Vector3D.Distance(bestM, bestT),
                                bestRise > 0 ? string.Format(", then {0:F1} m up", Vector3D.Distance(bestT, turnAt)) : "");
            how += string.Format("; {0} candidates, {1} turn checks, {2} sweeps, {3:F2} ms", candidates, turnChecks, sweeps, corridorWatch.Elapsed.TotalMilliseconds);
            return true;
        }
    }
}
