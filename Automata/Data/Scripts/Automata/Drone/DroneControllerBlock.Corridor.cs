using System;
using System.Collections.Generic;

using Sandbox.ModAPI;

using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util.Logging;
using VRage.Utils;
using Automata.Util;

namespace Automata.Drone
{
    /// <summary>
    /// Entry corridors (TODO/16): docking in tight spaces (a hangar, a bay, a ship's dock) and undocking out of them.
    ///
    /// Docking used to need a clear column of 10 m+ out along the connector axis, with room to turn at its end, and
    /// flew the route to it with ObstacleClearance (3 m) on every side: no hangar with a roof, no door with a metre to
    /// spare. Now the dock pose P0 is worked out backwards:
    ///  - P1: P0 moved CORRIDOR_SLIDE out along the target connector's axis: the final slide onto the connector.
    ///  - The corridor: from P1, holding the dock attitude, straight out along the axis, or out along the axis and then
    ///    across it along one of the target block's axes (an L: up off the floor, out through the door), to the nearest
    ///    point T where the drone has room to turn (TurnSweepFree). Checked with DockClearance (0.5 m) around the hull,
    ///    in the target's frame (a moving ship changes nothing).
    ///  - The flight: the normal route to T (ObstacleClearance), the turn to the dock attitude there, the corridor legs
    ///    at Safe speed with tight line / arrival tolerances and a full stop at each corner, the slide at the same
    ///    speed. All of it anchored to the target connector: a moving ship is matched first (MatchSpeed).
    ///  - Undocking: straight out by the hull's depth along the axis + BACKOUT_MARGIN when that is clear; otherwise the
    ///    same corridor backwards, then the route from its outer end.
    /// No corridor found: the old way (the approach point out along the axis, pushed out until there is room to turn).
    /// Bounded search, no A*: CORRIDOR_STEP along the axis, 1 m across it, at most CORRIDOR_TURN_CHECKS turn checks.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrCorridorTight = MyStringId.GetOrCompute("tight: {0}");
        private static readonly MyStringId StrCorridorBoth = MyStringId.GetOrCompute("{0:F2} m: {1} | {2:F2} m: {3}");
        private static readonly MyStringId StrNoPathfinder = MyStringId.GetOrCompute("no pathfinder");
        private static readonly MyStringId StrNoRoomMOffThe = MyStringId.GetOrCompute("no room {0:F2} m off the connector ({1} within {2:F1} m)");
        private static readonly MyStringId StrNoRoomToTurnWithin = MyStringId.GetOrCompute("no room to turn within reach: along the axis {0:F1} m clear{1}");
        private static readonly MyStringId StrMHullMAcross = MyStringId.GetOrCompute("; {0}: {1:F1} m (hull {2:F1} m across{3})");
        private static readonly MyStringId StrClearanceMCandidatesTurnChecks = MyStringId.GetOrCompute("; clearance {0:F2} m, {1} candidates, {2} turn checks, {3} sweeps, {4:F2} ms");
        #endregion

        private const double CORRIDOR_SLIDE = 0.5;           // m: P1 -> P0, the final slide onto the connector
        private const double CORRIDOR_STEP = 0.25;           // m: candidate heights along the connector axis
        private const double CORRIDOR_ACROSS_STEP = 1.0;     // m: candidate distances across it
        private const int CORRIDOR_AXIS_STEPS = 60;          // along the axis, at most (15 m)
        private const int CORRIDOR_TURN_CHECKS = 600;        // exit checks per search (the expensive part)
        private const double CORRIDOR_EXIT_EXTRA = 3.0;      // m: T moved this much further out when clear (room for the route to arrive)
        private const double CORRIDOR_RISE_MAX = 6.0;        // m: out of a door at floor level, up this much at most to find room to turn
        private const double CORRIDOR_RISE_STEP = 0.5;
        private const float CORRIDOR_LINE_TOLERANCE = 0.15f; // m: corridor legs, off the line (at most half the clearance)
        private const float CORRIDOR_ARRIVAL = 0.15f;        // m: corridor legs, arrival (same)
        private const double CORRIDOR_LINE_TOLERANCE_MIN = 0.08;   // m: the tightest line the controller is asked to hold
        private const double BACKOUT_MARGIN = 1.5;           // m: straight back-out = the hull's depth along the axis + this

        private double backOutSpeedCap;                      // set by DeparturePoints for the back-out legs (0: not strict)
        private IMyCubeBlock corridorIgnore;                 // the connector being docked at / undocked from: not an obstacle
        private float backOutLineTol = CORRIDOR_LINE_TOLERANCE;
        private float dockLineTol = CORRIDOR_LINE_TOLERANCE;  // the dock corridor's legs and slide
        private float dockCorridorSpeed;
        private readonly List<Vector3D> corridorPts = new List<Vector3D>();
        private readonly List<Vector3D> dockCorridorPts = new List<Vector3D>();
        private readonly List<FlightOrder> dockCorridorLegs = new List<FlightOrder>();
        private bool dockCorridor;                           // the current docking uses an entry corridor
        private Vector3D corridorExitDir;                    // the last corridor found: the way out at its outer end
        private readonly System.Diagnostics.Stopwatch corridorWatch = new System.Diagnostics.Stopwatch();

        private struct CorridorCandidate
        {
            public double Cost;
            public Vector3D M, T, Out;
        }
        private readonly List<CorridorCandidate> corridorCandidates = new List<CorridorCandidate>();

        // Corridor / back-out leg: precise (tight line and arrival, full attitude), slow, not watched (the walls are close)
        private void MakeCorridorLeg(FlightOrder leg, double cap, float tol)
        {
            if (leg == null || cap <= 0) return;
            leg.LooseArrival = false;
            leg.Watched = false;
            leg.LineTolerance = tol;
            leg.ArrivalTolerance = tol;
            leg.SpeedCap = cap;
            leg.HoldAttitudeInTransit = true;
        }

        // From the turn point in (and out, undocking): the drone's Safe speed
        private float CorridorSpeed { get { return Math.Max(MIN_FLIGHT_SPEED, SafeSpeed); } }

        #region Staging point (the docking's 4th endpoint)
        /// <summary>m out from the turn point where the route ends; from there to the turn point: Approach speed.</summary>
        private const double DOCK_STAGING_DISTANCE = 50;
        private const double DOCK_STAGING_MIN = 5;

        /// <summary>
        /// The docking's 4th endpoint: DOCK_STAGING_DISTANCE out from the turn point 'turnAt' (hull centre) along the way
        /// out 'outDir' (the corridor's exit, or the connector axis); in gravity, if that isn't clear (a hill, the
        /// ground), 45° up or straight up. Clear: the route's own clearance and terrain. Shorter when blocked (the
        /// longest clear one); none (turnAt) under DOCK_STAGING_MIN.
        /// </summary>
        private Vector3D DockStagingPoint(Vector3D turnAt, Vector3D outDir)
        {
            var pm = PathfindingManager.Instance;
            var cfg = AutomataSession.GetConfig();
            if (pm == null || cfg == null || !cfg.Pathfinding.AllowDirectPathfinding() || outDir.LengthSquared() < 1e-6) return turnAt;
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;
            pm.BeginPlan();
            outDir.Normalize();
            bool gravity = q.GravityUp.LengthSquared() > 0.5;
            int count = gravity ? 3 : 1;
            double bestLen = 0;
            Vector3D best = turnAt;
            double clearance = cfg.Pathfinding.ObstacleClearance;
            for (int i = 0; i < count; i++)
            {
                Vector3D dir = i == 0 ? outDir : i == 1 ? Vector3D.Normalize(outDir + q.GravityUp) : q.GravityUp;
                if (i > 0 && Vector3D.Dot(dir, outDir) > 0.99) continue;
                double len = DOCK_STAGING_DISTANCE;
                Vector3D hit;
                IMyEntity e;
                if (pm.SegmentBlocked(ref q, ref q.Controller, turnAt, turnAt + dir * len, 0.5, out hit, out e))
                    len = Vector3D.Dot(hit - turnAt, dir) - PathfindingManager.HullHalfSizeAlong(ref q, ref q.Controller, dir) - clearance;
                if (gravity && len > 10)
                {
                    Vector3D th;
                    double need = PathfindingManager.HullHalfSizeAlong(ref q, ref q.Controller, q.GravityUp) + 1;
                    if (PathfindingManager.TerrainTooClose(turnAt + dir * 10, turnAt + dir * len, need, out th))
                        len = Vector3D.Dot(th - turnAt, dir) - 5;
                }
                if (len >= DOCK_STAGING_DISTANCE - 0.1) return turnAt + dir * DOCK_STAGING_DISTANCE;
                if (len > bestLen) { bestLen = len; best = turnAt + dir * len; }
            }
            return bestLen >= DOCK_STAGING_MIN ? best : turnAt;
        }
        #endregion

        // The clearances a corridor is searched with: the configured one, then the minimum (tight doors)
        private void CorridorClearances(out double normal, out double tight)
        {
            var cfg = AutomataSession.GetConfig();
            normal = cfg != null ? cfg.Pathfinding.DockClearance : 0.5;
            tight = cfg != null ? Math.Min(normal, cfg.Pathfinding.DockClearanceMin) : 0.2;
        }

        // Speed and line tolerance in a corridor found with clearance 'berth': half the clearance off the line at most;
        // below the configured clearance, half the speed
        private void CorridorFlight(double berth, out float speed, out float tol)
        {
            double normal, tight;
            CorridorClearances(out normal, out tight);
            tol = (float)Math.Max(CORRIDOR_LINE_TOLERANCE_MIN, Math.Min(CORRIDOR_LINE_TOLERANCE, berth * 0.6));
            speed = CorridorSpeed;
            if (berth < normal - 1e-3) speed = Math.Max(MIN_FLIGHT_SPEED, speed * 0.5f);
        }

        /// <summary>FindCorridor with the configured clearance, then with the minimum one. 'berth': the one that worked.</summary>
        private bool FindCorridorAny(Vector3D p0, Vector3D axis, ref MatrixD att, ref MatrixD frame, List<Vector3D> pts, out string how, out double berth)
        {
            double normal, tight;
            CorridorClearances(out normal, out tight);
            berth = normal;
            if (FindCorridor(p0, axis, ref att, ref frame, pts, out how, normal)) return true;
            if (tight >= normal - 1e-3) return false;
            string howTight;
            berth = tight;
            if (FindCorridor(p0, axis, ref att, ref frame, pts, out howTight, tight)) { how = Loc.F(StrCorridorTight, howTight); return true; }
            how = Loc.F(StrCorridorBoth, normal, how, tight, howTight);
            return false;
        }

        // The hull box's size along world direction 'axis' (unit), as the drone is now
        private double HullDepthAlong(Vector3D axis)
        {
            if (localHullBox.Max.X < localHullBox.Min.X) return DroneRadius();
            Vector3D size = localHullBox.Size;
            MatrixD m = flightState.WorldMatrix;
            return Math.Abs(Vector3D.Dot(axis, m.Right)) * size.X + Math.Abs(Vector3D.Dot(axis, m.Up)) * size.Y
                 + Math.Abs(Vector3D.Dot(axis, m.Backward)) * size.Z;
        }

        /// <summary>
        /// Undocking from 'docked' (the base's connector): the first points (hull centres, world) appended to 'pts',
        /// 'start' moved to the last. Straight out along the connector axis by the hull's depth along it +
        /// BACKOUT_MARGIN when that is clear (DockClearance); otherwise the entry corridor backwards (hangars);
        /// otherwise straight out anyway. Sets backOutSpeedCap. Returns the number of points (back-out legs).
        /// </summary>
        private int DeparturePoints(IMyShipConnector docked, ref Vector3D start, List<Vector3D> pts)
        {
            CaptureFlightState();
            // Out of the base's connector, towards the drone: its real connection axis (a platform / inset connector's
            // is not its Forward)
            Vector3D pD, axis;
            if (!ConnectorGeometry(docked, out pD, out axis)) axis = docked.WorldMatrix.Forward;
            axis.Normalize();
            corridorIgnore = docked;
            try { return DeparturePointsFor(docked, axis, ref start, pts); }
            finally { corridorIgnore = null; }
        }

        private int DeparturePointsFor(IMyShipConnector docked, Vector3D axis, ref Vector3D start, List<Vector3D> pts)
        {
            double depth = HullDepthAlong(axis) + BACKOUT_MARGIN;
            Vector3D straight = start + axis * depth;
            MatrixD att = flightState.WorldMatrix;
            att.Translation = Vector3D.Zero;
            backOutSpeedCap = SafeSpeed;
            backOutLineTol = CORRIDOR_LINE_TOLERANCE;
            string why;
            if (StraightOutClear(start, axis, depth, ref att, out why))
            {
                pts.Add(straight);
                start = straight;
                SetDepartureExit(straight, axis);
                return 1;
            }
            MatrixD frame = docked.WorldMatrix;
            string how;
            double berth;
            if (FindCorridorAny(start, axis, ref att, ref frame, corridorPts, out how, out berth))
            {
                for (int i = corridorPts.Count - 1; i >= 0; i--) pts.Add(corridorPts[i]);
                int n = corridorPts.Count;
                start = corridorPts[0];
                // The way on out: the corridor's last stretch (out of a bay sideways: on in that direction)
                SetDepartureExit(corridorPts[0], corridorPts.Count > 1 ? corridorPts[0] - corridorPts[1] : axis);
                corridorPts.Clear();
                float speed;
                CorridorFlight(berth, out speed, out backOutLineTol);
                backOutSpeedCap = speed;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: undocking from {1} through its corridor ({2}; straight out blocked: {3})", Entity.EntityId,
                          docked.CustomName, how, why);
                return n;
            }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: undocking from {1}: straight out ({2:F1} m) blocked by {3}, no corridor ({4}): straight out anyway",
                      Entity.EntityId, docked.CustomName, depth, why, how);
            pts.Add(straight);
            start = straight;
            SetDepartureExit(straight, axis);
            return 1;
        }

        // Where the departure from a dock ended and which way it was heading: the route's escape candidates go on that
        // way first (a parking bay under a roof: the planner's detours, up and to the sides of the first obstacle, can't
        // find the way out of a pocket when the goal is behind the structure)
        private Vector3D departureEnd, departureDir;
        private bool departureSet;

        private void SetDepartureExit(Vector3D end, Vector3D dir)
        {
            departureSet = dir.LengthSquared() > 1e-4;
            departureEnd = end;
            departureDir = departureSet ? Vector3D.Normalize(dir) : Vector3D.Zero;
        }

        // Tight query: the corridor's clearance everywhere (StrictEnds), not limited by the instruments
        private bool CorridorQuery(out PathQuery q, out PathfindingManager pm)
        {
            pm = PathfindingManager.Instance;
            q = default(PathQuery);
            var cfg = AutomataSession.GetConfig();
            if (pm == null || cfg == null || !cfg.Pathfinding.AllowDirectPathfinding()) return false;
            RefreshDroneGrids();
            BuildPathQuery(out q);
            q.SeeAll = true;
            q.StrictEnds = true;
            q.Berth = cfg.Pathfinding.DockClearance;
            if (corridorIgnore != null && corridorIgnore.SlimBlock != null)
            {
                q.IgnoreGrid = corridorIgnore.CubeGrid;
                q.IgnoreMin = corridorIgnore.SlimBlock.Min;
                q.IgnoreMax = corridorIgnore.SlimBlock.Max;
            }
            return true;
        }

        private bool StraightOutClear(Vector3D start, Vector3D axis, double length, ref MatrixD att, out string why)
        {
            why = null;
            PathQuery q;
            PathfindingManager pm;
            if (!CorridorQuery(out q, out pm)) return true;   // no pathfinder: as before
            pm.BeginPlan();
            Vector3D a = start + axis * CORRIDOR_SLIDE;   // the hull touches the base where it is docked
            Vector3D hit;
            IMyEntity e;
            if (!pm.SegmentBlocked(ref q, ref att, a, start + axis * length, 0, out hit, out e)) return true;
            why = PathfindingManager.Describe(pm.LastHitBlock);
            return false;
        }

        // The old straight approach (no corridor): from the slide's start to the approach point, hull centres, with the
        // minimum dock clearance. Blocked: the block in the way.
        private bool ApproachLineClear(Vector3D p0Hull, Vector3D approachHull, ref MatrixD att, out string blocker)
        {
            blocker = null;
            PathQuery q;
            PathfindingManager pm;
            if (!CorridorQuery(out q, out pm)) return true;
            double normal, tight;
            CorridorClearances(out normal, out tight);
            q.Berth = tight;
            pm.BeginPlan();
            Vector3D dir = approachHull - p0Hull;
            double len = dir.Length();
            if (len < CORRIDOR_SLIDE + 0.1) return true;
            dir /= len;
            Vector3D hit;
            IMyEntity e;
            MatrixD rot = att;
            rot.Translation = Vector3D.Zero;
            if (!pm.SegmentBlocked(ref q, ref rot, p0Hull + dir * CORRIDOR_SLIDE, approachHull, 0, out hit, out e)) return true;
            blocker = PathfindingManager.Describe(pm.LastHitBlock);
            return false;
        }

        // How far the hull centre can move from 'from' along 'dir' (unit) with the berth, up to 'max'
        private static double ClearAlong(PathfindingManager pm, ref PathQuery q, ref MatrixD att, Vector3D from, Vector3D dir, double max)
        {
            string blocker;
            return ClearAlong(pm, ref q, ref att, from, dir, max, out blocker);
        }

        private static double ClearAlong(PathfindingManager pm, ref PathQuery q, ref MatrixD att, Vector3D from, Vector3D dir, double max, out string blocker)
        {
            blocker = null;
            if (max <= 0) return 0;
            Vector3D hit;
            IMyEntity e;
            if (!pm.SegmentBlocked(ref q, ref att, from, from + dir * max, 0, out hit, out e)) return max;
            blocker = PathfindingManager.Describe(pm.LastHitBlock);
            double half = PathfindingManager.HullHalfSizeAlong(ref q, ref att, dir);
            return MathHelper.Clamp(Vector3D.Dot(hit - from, dir) - half - q.Berth, 0, max);
        }

        /// <summary>
        /// The entry corridor of a dock pose: 'p0' the hull centre docked (world), 'axis' out of the target connector,
        /// 'att' the dock attitude (rotation), 'frame' the target connector's matrix (its axes: the directions tried).
        /// 'pts': hull centres in flight order inbound, T (room to turn) ... P1 (the slide's start); one point when T is
        /// P1. False (with why): no room at P1, or no way out within the limits.
        /// </summary>
        private bool FindCorridor(Vector3D p0, Vector3D axis, ref MatrixD att, ref MatrixD frame, List<Vector3D> pts, out string how, double berth)
        {
            pts.Clear();
            how = Loc.T(StrNoPathfinder);
            PathQuery q;
            PathfindingManager pm;
            if (!CorridorQuery(out q, out pm)) return false;
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
            double exitMargin = Math.Max(TURN_ROOM_MARGIN, cfg.ObstacleClearance);
            PathQuery qt = q;
            qt.StrictEnds = false;
            qt.Berth = exitMargin;
            pm.BeginPlan();
            int turnChecks = 0, sweeps = 0;
            axis.Normalize();

            // P1 itself, with the berth across the axis (along it the hull is about to touch the connector; the floor
            // around a flush connector is that close too)
            Vector3D p1 = p0 + axis * CORRIDOR_SLIDE;
            Vector3D axisLocal = Vector3D.TransformNormal(axis, MatrixD.Transpose(rot));
            Vector3D grow = new Vector3D(1 - Math.Abs(axisLocal.X), 1 - Math.Abs(axisLocal.Y), 1 - Math.Abs(axisLocal.Z)) * q.Berth;
            var box = new MyOrientedBoundingBoxD(p1, localHullBox.HalfExtents + grow, Quaternion.CreateFromRotationMatrix(rot));
            if (localHullBox.Max.X >= localHullBox.Min.X && !pm.BoxFree(ref q, ref box))
            {
                how = Loc.F(StrNoRoomMOffThe, CORRIDOR_SLIDE, PathfindingManager.Describe(pm.LastHitBlock), q.Berth);
                return false;
            }
            double axisClear = Math.Min(ClearAlong(pm, ref q, ref rot, p1, axis, maxLen), CORRIDOR_AXIS_STEPS * CORRIDOR_STEP);
            sweeps++;

            // 1. Straight out along the axis: the first height with room to turn
            double best = double.MaxValue;
            Vector3D bestT = p1, bestM = p1, bestOut = axis;
            for (double s = 0; s <= axisClear + 1e-6 && turnChecks < CORRIDOR_TURN_CHECKS; s += CORRIDOR_STEP)
            {
                Vector3D m = p1 + axis * s;
                turnChecks++;
                if (pm.TurnSweepFree(ref qt, m, ref rot, exitMargin)) { best = s; bestT = bestM = m; break; }
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
                    double clear = ClearAlong(pm, ref q, ref rot, m, dir, reach);
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
                bool room = pm.TurnSweepFree(ref qt, c.T, ref rot, exitMargin);
                if (!room && Math.Abs(Vector3D.Dot(rise, c.Out)) < 0.1)
                {
                    double riseClear = ClearAlong(pm, ref q, ref rot, c.T, rise, Math.Min(CORRIDOR_RISE_MAX, best - c.Cost));
                    sweeps++;
                    for (up = CORRIDOR_RISE_STEP; up <= riseClear + 1e-6 && !room && turnChecks < CORRIDOR_TURN_CHECKS; up += CORRIDOR_RISE_STEP)
                    {
                        turnChecks++;
                        room = pm.TurnSweepFree(ref qt, c.T + rise * up, ref rot, exitMargin);
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
                ClearAlong(pm, ref q, ref rot, p1, axis, maxLen, out axisBlock);
                sb.AppendLoc(StrNoRoomToTurnWithin, axisClear, axisBlock != null ? " (" + axisBlock + ")" : "");
                for (int d = 0; d < 6; d++)
                {
                    Vector3D dir = frame.GetDirectionVector((Base6Directions.Direction)d);
                    if (Math.Abs(Vector3D.Dot(dir, axis)) > 0.1) continue;
                    string blocker;
                    double clear = ClearAlong(pm, ref q, ref rot, p1, dir, maxLen, out blocker);
                    sb.AppendLoc(StrMHullMAcross, (Base6Directions.Direction)d, clear,
                                    2 * PathfindingManager.HullHalfSizeAlong(ref q, ref rot, Vector3D.Cross(dir, axis)),
                                    blocker != null ? ", " + blocker : "");
                }
                sb.AppendLoc(StrClearanceMCandidatesTurnChecks, q.Berth, candidates, turnChecks, sweeps,
                                corridorWatch.Elapsed.TotalMilliseconds);
                how = sb.ToString();
                return false;
            }
            // A little further out where clear (up, after a rise): more room for the route to arrive and to turn
            Vector3D exitDir = bestRise > 0 ? rise : bestOut;
            corridorExitDir = exitDir;
            Vector3D turnAt = bestT + rise * bestRise;
            double extra = ClearAlong(pm, ref q, ref rot, turnAt, exitDir, CORRIDOR_EXIT_EXTRA);
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
