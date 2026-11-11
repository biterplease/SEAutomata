using System;
using System.Collections.Generic;

using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Flight lane obstructions (TODO/25): a stretch of a lane the drone's route check found blocked by rock, a tree or a
    /// static grid (a moving grid may get out of the way: not reported), with the points it flew round it. Held until the
    /// lane's orchestrator hears it: sent at once with a keepalive when in antenna range, and with the job's report for sure
    /// (queued until delivered, saved with the job). The orchestrator redraws the lane (FlightLanes.Obstructed). Also the
    /// scout's finds (ore hits) that decide whether an order lane stays, the lanes it gave up waiting at (the orchestrator
    /// grows a lane drones keep giving up at: FlightLanes.GaveUp), lane hops (the first lane of a way through several:
    /// planned again at its end), and lane rides: a lane flown end to end through its box with nothing in the way found
    /// (planning, the in-flight checks) is reported clear - the orchestrator trusts it (FlightLanes.Verified), drones then
    /// fly it unchecked (trustedLanePoints: CheckLegStart / UpdateRouteWatch skip its legs).
    /// </summary>
    public partial class DroneControllerBlock
    {
        private readonly List<LaneObstruction> laneObstructions = new List<LaneObstruction>();
        private int jobFinds;                               // ore hits the job's scans found (scout jobs: their order lane stays)
        private const int LANE_OBSTRUCTIONS_MAX = 16;
        private bool laneHopOk;                             // FlyRoute / docking planning: a lane hop may be given
        private bool laneHopPending;                        // flying a lane hop: planned again at laneHopSpot
        private Vector3D laneHopSpot;
        private const double LANE_HOP_ARRIVE = 30;          // m from the hop's end: planned again
        private bool laneRideActive, laneRideFromStart, laneRideToEnd, laneRideObstructed;   // flying an untrusted lane: reported clear at its end
        private int laneRideNumber, laneRideRevision;
        private bool laneRideAB;
        private Vector3D laneRideEnd;
        private readonly List<Vector3D> trustedLanePoints = new List<Vector3D>();   // the route's waypoints inside trusted lanes
        private readonly List<Vector3D> relaxedLanePoints = new List<Vector3D>();   // ...inside trusted or mostly safe lanes
        private readonly List<int> relaxedLaneNumbers = new List<int>();            // (their lanes)
        private readonly HashSet<int> suspectLanes = new HashSet<int>();            // mostly safe lanes this trip found something in: checked

        // A route through an untrusted lane: the ride to its end (a replan on the way keeps where it began)
        private void BeginLaneRide(FlightLanes.LaneLeg lane, Vector3D end)
        {
            bool same = laneRideActive && laneRideNumber == lane.Number && laneRideAB == lane.AB && laneRideRevision == lane.Revision;
            if (!same)
            {
                laneRideActive = true;
                laneRideNumber = lane.Number;
                laneRideAB = lane.AB;
                laneRideRevision = lane.Revision;
                laneRideObstructed = false;
                laneRideFromStart = lane.FromStart;   // (a replan part way keeps this: its start was flown already)
            }
            laneRideToEnd = lane.ToEnd;
            laneRideEnd = end;
        }

        // A leg inside a trusted lane: not checked in flight at all (its target one of the route's trusted lane points)
        private bool TrustedLaneLeg(FlightOrder o)
        {
            if (trustedLanePoints.Count == 0 || o.AnchorEntityId != 0) return false;
            Vector3D t = o.Target.ToVector3D();
            for (int i = 0; i < trustedLanePoints.Count; i++)
                if (Vector3D.DistanceSquared(t, trustedLanePoints[i]) < 4) return true;
            return false;
        }

        // A leg inside a trusted or mostly safe lane (not checked at its start): its lane's number, else 0
        private int RelaxedLaneLeg(FlightOrder o)
        {
            if (relaxedLanePoints.Count == 0 || o.AnchorEntityId != 0) return 0;
            Vector3D t = o.Target.ToVector3D();
            for (int i = 0; i < relaxedLanePoints.Count; i++)
                if (Vector3D.DistanceSquared(t, relaxedLanePoints[i]) < 4) return relaxedLaneNumbers[i];
            return 0;
        }

        // The look-ahead saw something on a leg of a mostly safe lane: for the rest of the trip the lane is planned with
        // checks (what they find in it is reported: the lane is checked again by everyone)
        private void LaneSuspect(FlightOrder o)
        {
            int lane = RelaxedLaneLeg(o);
            if (lane == 0 || !suspectLanes.Add(lane)) return;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: something ahead in mostly safe lane L{1}: planned with checks for the rest of the trip", Entity.EntityId, lane);
        }

        // No sublane free at the entrance of 'lane' for FlightLanes' wait: the orchestrator hears it (as obstructions are)
        private void QueueLaneGiveUp(int lane, bool ab, string laneName)
        {
            if (lane == 0) return;
            if (laneObstructions.Count >= LANE_OBSTRUCTIONS_MAX) laneObstructions.RemoveAt(0);
            laneObstructions.Add(new LaneObstruction { Id = DateTime.UtcNow.Ticks, Lane = lane, AB = ab, Kind = FlightLanes.GAVE_UP, What = "" });
            lastKeepaliveFrame = int.MinValue / 2;   // heard at once when in range (else with the job's report)
            if (Log.DebugEnabled) Log.Debug("Drone {0}: lane {1}: gave up waiting at its entrance: reported to its orchestrator", Entity.EntityId, laneName);
        }

        /// <summary>UpdateBeforeSimulation10, AI on: lane rides and hops (UpdateLaneRide10, UpdateLaneHop10).</summary>
        private void UpdateLanes10()
        {
            if (activeFlightOrder == null && suspectLanes.Count > 0) suspectLanes.Clear();   // the trip is over
            UpdateLaneRide10();
            UpdateLaneHop10();
        }

        // At the end of an untrusted lane flown end to end with nothing in the way: reported clear
        private void UpdateLaneRide10()
        {
            if (!laneRideActive) return;
            if (activeFlightOrder == null || routeOwner == RouteOwner.None) { laneRideActive = false; return; }
            CaptureFlightState();
            double arrive = Math.Max(LANE_HOP_ARRIVE, flightState.LinearVelocity.Length() * 0.5);
            if (Vector3D.DistanceSquared(HullCentreWorld, laneRideEnd) > arrive * arrive) return;
            laneRideActive = false;
            if (laneRideObstructed || !laneRideFromStart || !laneRideToEnd) return;
            if (laneObstructions.Count >= LANE_OBSTRUCTIONS_MAX) laneObstructions.RemoveAt(0);
            laneObstructions.Add(new LaneObstruction { Id = DateTime.UtcNow.Ticks, Lane = laneRideNumber, AB = laneRideAB, Kind = FlightLanes.CLEAR, What = "", Revision = laneRideRevision });
            lastKeepaliveFrame = int.MinValue / 2;   // heard at once when in range (else with the job's report)
            if (Log.DebugEnabled) Log.Debug("Drone {0}: lane L{1} flown end to end, nothing in the way: reported clear to its orchestrator", Entity.EntityId, laneRideNumber);
        }

        // At the end of a lane hop, the route is planned again (the next lane)
        private void UpdateLaneHop10()
        {
            if (!laneHopPending) return;
            if (activeFlightOrder == null || routeOwner == RouteOwner.None) { laneHopPending = false; return; }
            if (recoverReplanPending) return;
            CaptureFlightState();
            double arrive = Math.Max(LANE_HOP_ARRIVE, flightState.LinearVelocity.Length() * 0.5);   // (checked every 10 ticks)
            if (Vector3D.DistanceSquared(HullCentreWorld, laneHopSpot) > arrive * arrive) return;
            laneHopPending = false;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: at the end of its lane hop: planning on from there", Entity.EntityId);
            lastReplanFrame = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter;
            if (!Replan()) RouteStopped(Loc.T(MyStrings.Drone_NoRouteAheadStopped));
        }

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
                if (laneObstructions[i].Lane == lane && laneObstructions[i].Kind < FlightLanes.GAVE_UP && Vector3D.DistanceSquared(laneObstructions[i].A.ToVector3D(), a) < 100
                    && Vector3D.DistanceSquared(laneObstructions[i].B.ToVector3D(), b) < 100) return;
            if (laneObstructions.Count >= LANE_OBSTRUCTIONS_MAX) laneObstructions.RemoveAt(0);
            if (laneRideActive && laneRideNumber == lane) laneRideObstructed = true;   // not a clear ride
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
