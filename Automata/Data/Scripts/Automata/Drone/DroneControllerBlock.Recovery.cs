using System;
using System.Collections.Generic;

using VRage.Utils;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// No route (a dock / home trip, a NavigateTo, a replan mid-flight): instead of giving up, the drone flies to a free
    /// spot in its immediate surroundings (straight there, the hull fits, never within RECOVER_EXCLUDE of a place it
    /// already tried from), and plans again from there - with a new view of what is around. Up to RECOVER_MAX
    /// repositions; then error state (kept until Stop / Go home / recall) and a broadcast.
    ///  - Only where a failed plan means "stuck": StartDocking, DispatchNavigateTo, Replan (recoverPlanning). A work
    ///    site's approaches (DispatchToolTo) keep their own retries (other faces) and don't reposition.
    ///  - The route is [spot] then a straight stretch to the goal that is planned again at the spot (UpdateRecovery10).
    ///  - A plan that works resets the count.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private static readonly MyStringId StrNoRouteRepositioning = MyStringId.GetOrCompute("No route: repositioning {0:F0} m and planning again ({1}/{2})");
        private static readonly MyStringId StrNoRouteAfterRepositions = MyStringId.GetOrCompute("no route after {0} repositions");
        private static readonly MyStringId StrDroneNoRouteStopped = MyStringId.GetOrCompute("Drone {0}: no route after {1} repositions, stopped; trying again in {2} s.");

        private const int RECOVER_MAX = 3;
        private const double RECOVER_EXCLUDE_MIN = 10;   // m: no spot this close to a place already tried from (or the hull radius x 1.5)
        private static readonly double[] RECOVER_DISTANCES = { 15, 30 };   // m (at least 2 / 4 hull radii)

        private bool recoverPlanning;                    // the caller lets a failed plan reposition the drone
        private int recoverAttempts;
        private readonly List<Vector3D> recoverVisited = new List<Vector3D>();
        private readonly List<Vector3D> recoverCandidates = new List<Vector3D>();
        private bool recoverReplanPending;               // flying to recoverSpot: plan again once there
        private Vector3D recoverSpot;
        private double recoverArrive;
        private string routeErrorPending;                // repositions used up: error state at the next update
        private bool routeError;                         // in State.Error for want of a route (kept until Stop / Go home / recall)
        private bool routeErrorByDrone;                  // ...because other drones were in the way: tried again by itself
        private int routeErrorFrame;
        #region Timing
        private const int ROUTE_ERROR_DRONE_RETRY_TICKS = 5 * 60;    // in error because of drones in the way: free again after this
        private const int ROUTE_ERROR_RETRY_TICKS = 15 * 60;         // any other route error: tried again after this (was: until a player acted)
        #endregion

        private void RecoverSucceeded()
        {
            if (recoverAttempts > 0) if (Log.DebugEnabled) Log.Debug("Drone {0}: route found after {1} reposition(s)", Entity.EntityId, recoverAttempts);
            recoverAttempts = 0;
            recoverVisited.Clear();
            routeErrorPending = null;   // a stale "used up" from an earlier plan (a skipped waypoint)
        }

        /// <summary>A new trip (task): the reposition count starts again.</summary>
        private void ResetRecovery()
        {
            recoverAttempts = 0;
            recoverVisited.Clear();
            recoverReplanPending = false;
            routeErrorPending = null;
        }

        /// <summary>
        /// PlanPath found nothing, from here or the escape spots: a reposition spot as the route's only waypoint (the
        /// stretch from it to the goal is planned again there). False: not allowed here, used up (error pending), or no
        /// spot.
        /// </summary>
        private bool TryRecoverRoute(Vector3D start, Vector3D goal, List<Vector3D> points, int count, Vector3D startForward, Vector3D startUp,
                                     PathfindingManager pm, ref PathQuery q)
        {
            if (!recoverPlanning || pm == null || SweepWaypointActive()) return false;   // a sweep waypoint nobody can reach is skipped, not a drone that is stuck
            if (recoverAttempts >= RECOVER_MAX)
            {
                routeErrorPending = Loc.F(StrNoRouteAfterRepositions, RECOVER_MAX);
                routeErrorByDrone = IsOtherDrone(pm.LastBlocker);
                return false;
            }
            if (recoverAttempts == 0) recoverVisited.Clear();
            recoverVisited.Add(start);
            double radius = DroneRadius();
            double exclude = Math.Max(RECOVER_EXCLUDE_MIN, radius * 1.5);
            RecoverCandidates(start, ref q, radius);
            Vector3D hit;
            VRage.ModAPI.IMyEntity e;
            for (int i = 0; i < recoverCandidates.Count; i++)
            {
                Vector3D s = recoverCandidates[i];
                bool near = false;
                for (int v = 0; v < recoverVisited.Count && !near; v++) near = Vector3D.DistanceSquared(s, recoverVisited[v]) < exclude * exclude;
                if (near) continue;
                pm.BeginPlan();
                if (!pm.HullFits(ref q, s) || pm.SegmentBlocked(ref q, start, s, 0.5, out hit, out e)) continue;
                if (q.GravityUp.LengthSquared() > 0.5
                    && PathfindingManager.HeightAboveTerrain(s) < PathfindingManager.HullHalfSizeAlong(ref q, ref q.Controller, q.GravityUp) + 1) continue;
                recoverAttempts++;
                recoverVisited.Add(s);
                if (points.Count > count) points.RemoveRange(count, points.Count - count);
                points.Add(s);
                legOffset = count;
                legForwards.Clear();
                legUps.Clear();
                legForwards.Add(startForward); legUps.Add(startUp);   // the leg there: attitude held
                legForwards.Add(startForward); legUps.Add(startUp);   // on to the goal: replaced at the spot
                recoverReplanPending = true;
                recoverSpot = s;
                recoverArrive = Math.Max(3, Vector3D.Distance(start, s) * 0.4);
                recoverCandidates.Clear();
                Report(StrNoRouteRepositioning, Vector3D.Distance(start, s), recoverAttempts, RECOVER_MAX);
                return true;
            }
            recoverCandidates.Clear();
            routeErrorPending = Loc.F(StrNoRouteAfterRepositions, recoverAttempts);   // nowhere left to go
            routeErrorByDrone = IsOtherDrone(pm.LastBlocker);
            return false;
        }

        // Spots around 'from': up first (in gravity: never down), then level all round, then the diagonals; near ones first
        private void RecoverCandidates(Vector3D from, ref PathQuery q, double radius)
        {
            recoverCandidates.Clear();
            bool gravity = q.GravityUp.LengthSquared() > 0.5;
            Vector3D up = gravity ? q.GravityUp : q.Controller.Up;
            Vector3D fwd = q.Controller.Forward - up * Vector3D.Dot(q.Controller.Forward, up);
            if (fwd.LengthSquared() < 1e-4) fwd = Vector3D.CalculatePerpendicularVector(up);
            fwd.Normalize();
            Vector3D right = Vector3D.Cross(fwd, up);
            for (int k = 0; k < RECOVER_DISTANCES.Length; k++)
            {
                double d = Math.Max(RECOVER_DISTANCES[k], radius * 2 * (k + 1));
                recoverCandidates.Add(from + up * d);
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4;
                    Vector3D h = fwd * Math.Cos(ang) + right * Math.Sin(ang);
                    recoverCandidates.Add(from + h * d);
                }
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4 + Math.PI / 8;
                    Vector3D h = fwd * Math.Cos(ang) + right * Math.Sin(ang);
                    recoverCandidates.Add(from + Vector3D.Normalize(h + up) * d);
                    if (!gravity) recoverCandidates.Add(from + Vector3D.Normalize(h - up) * d);
                }
                if (!gravity) recoverCandidates.Add(from - up * d);
            }
        }

        /// <summary>UpdateBeforeSimulation10, AI on: at the reposition spot, plan again; repositions used up: error.</summary>
        private void UpdateRecovery10()
        {
            // Stopped because other drones were in the way: they move on, so it doesn't wait for a player. A drone in
            // error holds still where it is, often where the others need to pass: it is free again after a while (no job:
            // it bids, goes home, clears its cargo...), and plans with the traffic as it is then
            if (routeError && currentState == State.Error
                && Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter - routeErrorFrame > RouteRetryTicks)
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: stopped for want of a route{1}: trying again", Entity.EntityId, routeErrorByDrone ? " because of other drones" : "");
                ClearRouteError();
                return;
            }
            if (routeErrorPending != null)
            {
                string why = routeErrorPending;
                routeErrorPending = null;
                EnterRouteError(why);
                return;
            }
            if (!recoverReplanPending) return;
            if (activeFlightOrder == null || routeOwner == RouteOwner.None) { recoverReplanPending = false; return; }
            CaptureFlightState();
            if (Vector3D.DistanceSquared(HullCentreWorld, recoverSpot) > recoverArrive * recoverArrive) return;
            recoverReplanPending = false;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: at the reposition spot ({1}/{2}): planning again", Entity.EntityId, recoverAttempts, RECOVER_MAX);
            lastReplanFrame = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter;
            if (!Replan()) RouteStopped(Loc.T(StrNoRouteAheadStopped));
        }

        private int RouteRetryTicks { get { return routeErrorByDrone ? ROUTE_ERROR_DRONE_RETRY_TICKS : ROUTE_ERROR_RETRY_TICKS; } }

        private void EnterRouteError(string why)
        {
            if (currentState == State.Error) return;
            routeError = true;
            routeErrorFrame = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter;
            why += (routeErrorByDrone ? " (other drones in the way" : " (") + (routeErrorByDrone ? ": " : "") + "trying again in " + RouteRetryTicks / 60 + " s)";
            recoverReplanPending = false;
            recoverAttempts = 0;
            recoverVisited.Clear();
            errorReason = why;
            Report(StrERRORStopped, why);
            Broadcast(Loc.F(StrDroneNoRouteStopped, Nicknames.Of(Entity), RECOVER_MAX, RouteRetryTicks / 60));
            StopJob(why);
            ClearFlightOrder();
            ClearAllThrustOverrides();
            ReleaseGyroscopes();
            SetState(State.Error);
            UpdateAlert();
        }

        /// <summary>Stop / Go home / recall: a route error ends (the drone may fly again).</summary>
        private void ClearRouteError()
        {
            recoverAttempts = 0;
            recoverVisited.Clear();
            recoverReplanPending = false;
            routeErrorPending = null;
            if (!routeError) return;
            routeError = false;
            routeErrorByDrone = false;
            errorReason = null;
            if (currentState == State.Error) SetState(IsParkedNow() ? State.Docked : State.Standby);
            UpdateAlert();
        }
    }
}
