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
using Automata.Util.Strings;

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

        private const double CORRIDOR_SLIDE = PathfindingManager.CORRIDOR_SLIDE;   // m: P1 -> P0, the final slide onto the connector
        private const float CORRIDOR_LINE_TOLERANCE = 0.15f; // m: corridor legs, off the line (at most half the clearance)
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
        // For the logs: each waypoint's height over the start (gravity) / its distance, "+3.2 m, +50.1 m, ..."
        private string RouteHeights(Vector3D start, List<Vector3D> points, Vector3D end)
        {
            if (points == null) return "";
            var sb = new System.Text.StringBuilder(" (height over the start:");
            bool g = flightState.InGravity;
            for (int i = 0; i <= points.Count; i++)
            {
                Vector3D p = i < points.Count ? points[i] : end;
                double h = g ? Vector3D.Dot(p - start, flightState.GravityUp) : Vector3D.Distance(p, start);
                sb.Append(i == 0 ? " " : ", ").Append(h.ToString("+0.0;-0.0")).Append(" m");
            }
            return sb.Append(g ? ")" : " away)").ToString();
        }

        private Vector3D DockStagingPoint(Vector3D turnAt, Vector3D outDir, double maxLen = DOCK_STAGING_DISTANCE)
        {
            var pm = PathfindingManager.Instance;
            var cfg = AutomataSession.GetConfig();
            if (pm == null || cfg == null || outDir.LengthSquared() < 1e-6) return turnAt;
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
            double clearance = CompactFlight ? COMPACT_CLEARANCE : cfg.Pathfinding.ObstacleClearance;
            double stageMin = CompactFlight ? 1.5 : DOCK_STAGING_MIN;
            for (int i = 0; i < count; i++)
            {
                Vector3D dir = i == 0 ? outDir : i == 1 ? Vector3D.Normalize(outDir + q.GravityUp) : q.GravityUp;
                if (i > 0 && Vector3D.Dot(dir, outDir) > 0.99) continue;
                double len = maxLen;
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
                if (len >= maxLen - 0.1) return turnAt + dir * maxLen;
                if (len > bestLen) { bestLen = len; best = turnAt + dir * len; }
            }
            return bestLen >= stageMin ? best : turnAt;
        }
        #endregion

        // Speed and line tolerance in a corridor found with clearance 'berth': half the clearance off the line at most;
        // below the configured clearance, half the speed
        private void CorridorFlight(double berth, out float speed, out float tol)
        {
            double normal, tight;
            PathfindingManager.CorridorClearances(out normal, out tight);
            tol = (float)Math.Max(CORRIDOR_LINE_TOLERANCE_MIN, Math.Min(CORRIDOR_LINE_TOLERANCE, berth * 0.6));
            speed = CorridorSpeed;
            if (berth < normal - 1e-3) speed = Math.Max(MIN_FLIGHT_SPEED, speed * 0.5f);
        }

        /// <summary>
        /// The pathfinder's corridor search (PathfindingManager.FindCorridorAny) with the drone's corridor query; on success
        /// corridorExitDir is the way out at its outer end. 'berth': the clearance that worked.
        /// </summary>
        private bool FindCorridorAny(Vector3D p0, Vector3D axis, ref MatrixD att, ref MatrixD frame, List<Vector3D> pts, out string how, out double berth)
        {
            pts.Clear();
            berth = 0;
            PathQuery q;
            PathfindingManager pm;
            if (!CorridorQuery(out q, out pm)) { how = Loc.T(MyStrings.Drone_NoPathfinder); return false; }
            Vector3D exitDir;
            if (!pm.FindCorridorAny(ref q, p0, axis, ref att, ref frame, TURN_ROOM_MARGIN, pts, out how, out berth, out exitDir)) return false;
            corridorExitDir = exitDir;
            return true;
        }

        // The hull box's size along world direction 'axis' (unit), as the drone is now
        private double HullDepthAlong(Vector3D axis)
        {
            if (localHullBox.Max.X < localHullBox.Min.X) return DroneRadius();
            Vector3D half = localHullBox.HalfExtents;
            MatrixD m = flightState.WorldMatrix;
            return 2 * PathfindingManager.HalfSizeAlong(ref m, ref half, axis);
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
                if (Log.DebugEnabled) Log.Debug("Drone {0}: undocking from {1} through its corridor ({2}; straight out blocked: {3})", Entity.EntityId, docked.CustomName, how, why);
                return n;
            }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: undocking from {1}: straight out ({2:F1} m) blocked by {3}, no corridor ({4}): straight out anyway", Entity.EntityId, docked.CustomName, depth, why, how);
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
            if (pm == null || cfg == null) return false;
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
            PathfindingManager.CorridorClearances(out normal, out tight);
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
    }
}
