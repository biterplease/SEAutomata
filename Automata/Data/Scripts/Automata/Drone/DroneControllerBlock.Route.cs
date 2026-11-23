using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using VRage.Utils;
using Automata.Util.Strings;

namespace Automata.Drone
{
    /// <summary>
    /// The drone's side of pathfinding (server). Routes to approach points (jobs) and to docks come from the
    /// PathfindingManager; the drone flies them as legs, looks ahead along the leg it is on, and asks for a new route
    /// from where it is when something new is in the way. See PathfindingManager for the whole conversation.
    /// </summary>
    public partial class DroneControllerBlock
    {

        private enum RouteOwner : byte { None, FlyRoute, Docking, Player, Sweep }

        #region Timing
        private const int ROUTE_WATCH_EVERY = 2;          // UpdateBeforeSimulation10 calls: a look-ahead every 20 ticks
        private const double ROUTE_LOOKAHEAD_SECONDS = 4.0;
        private const int ROUTE_REPLAN_MIN_TICKS = 60;    // at most one replan a second
        #endregion
        private const double ROUTE_LOOKAHEAD_MIN = 20.0;  // m
        private const double WORK_STANDOFF = 0.25;        // m: the way-in check stops the hull's front this far off the face
        private const double JIT_MIN_DISTANCE = 10.0;     // m flown before parts not seen at planning are planned

        private readonly List<Vector3D> routeScratch = new List<Vector3D>();
        private readonly List<Vector3D> routeChain = new List<Vector3D>();
        // Attitude of each planned leg (PlanPath): leg k ends at points[legOffset + k], the last one at the goal
        private readonly List<Vector3D> legForwards = new List<Vector3D>();
        private readonly List<Vector3D> legUps = new List<Vector3D>();
        private int legOffset;
        private RouteOwner routeOwner;
        private bool routeFailed;          // no way round what appeared on the way: the task fails (Unreachable)
        private int routeReplans;
        private int lastReplanFrame = int.MinValue / 2;
        private int routeWatchCounter;

        // Leg-start check: every watched leg is checked once as the drone starts flying it (the route was planned
        // earlier; things move meanwhile): short legs against the scene, long ones with the pathfinder's ray bundle.
        // Not the first two legs after leaving a dock, nor the last two before docking (the base is right there).
        private const double LEG_CHECK_SCENE_MAX = 50.0;   // m: up to this, the scene (block boxes); longer: rays
        private const double TERRAIN_WATCH_END = 20.0;     // m before a leg's end: no terrain check (landing, work at ground level)

        // Height the hull needs above the terrain in flight: half its size plus a little
        private double TerrainNeed()
        {
            return localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.Size.Max() * 0.5 + 2.0 : 5.0;
        }
        // Below the height the hull needs over the terrain (just lifted off, in a bore hole): climbing, not flying into it
        private bool TakingOff(Vector3D hullCentre)
        {
            if (!flightState.InGravity) return false;
            double h = PathfindingManager.HeightAboveTerrain(hullCentre);
            return h != double.MaxValue && h < TerrainNeed();
        }
        private FlightOrder legChecked;
        private int legsSinceUndock = 99;
        private int undockLegs = 2;       // back-out legs of the route just planned (a straight back-out is one: the leg after it is checked)
        // Just in time: stretches of the current route the drone couldn't see when it was planned, and from where
        private int routeUnseen;
        private int routePlanFrame = int.MinValue;
        // Where the drone was when the route's progress started being measured (the active order's anchor frame:
        // moving bases), set on the first watch after planning
        private bool routeJitArmed;
        private bool routeJitForce;   // plan the parts not seen yet now (economy flight, before it turns round)
        private long routeJitAnchorId;
        private Vector3D routeJitFrom;

        // What FlyRoute was called with (world, or in the anchor's frame), to fly it again from where the drone is
        private long flyAnchorId;
        private Vector3D flyTarget, flyApproach, flyRefOffset, flyForward, flyUp;
        private double flyHull;
        private bool flyCheckLine;

        // What StartDocking was called with
        private long dockReplanTargetId;
        private int dockReplanMount;
        private Vector3DData dockReplanOffset, dockReplanForward, dockReplanUp;

        private static bool WELDER_USE_DRONE_SHAPE = true;
        private static bool WELDER_USE_NEIGHBOR_SHAPES = true;

        /// <summary>
        /// The header warning for missing eyes, or null: no camera / no sensor / neither, among the ones the server
        /// requires for pathfinding (an instrument that isn't required is simulated all around). Docked instruments
        /// switched off until take-off count.
        /// </summary>
        private string MissingInstrumentsWarning()
        {
            var pf = AutomataSession.GetConfig().Pathfinding;
            var asleep = settings != null ? settings.SleepingBlockIds : null;
            bool noCamera = pf.RequireCamerasForPathfinding(), noSensor = pf.RequireSensorsForPathfinding();
            for (int i = 0; i < cameraBlocks.Count && noCamera; i++) if (PathfindingManager.InstrumentOn(cameraBlocks[i], asleep)) noCamera = false;
            for (int i = 0; i < sensors.Count && noSensor; i++) if (PathfindingManager.InstrumentOn(sensors[i], asleep)) noSensor = false;
            if (noCamera && noSensor) return Loc.T(MyStrings.Drone_WarnNoSensorOrCamera);
            if (noCamera) return Loc.T(MyStrings.Drone_WarnNoCameraDetected);
            if (noSensor) return Loc.T(MyStrings.Drone_WarnNoSensorDetected);
            return null;
        }

        private bool AnyCameraOn()
        {
            var asleep = settings != null ? settings.SleepingBlockIds : null;
            for (int i = 0; i < cameraBlocks.Count; i++) if (PathfindingManager.InstrumentOn(cameraBlocks[i], asleep)) return true;
            return false;
        }

        private void ResetRoute()
        {
            CancelPendingRoute();
            routeFailed = false;
            routeReplans = 0;
            routeWatchCounter = 0;
            if (!replanning) ResetFreeFlightWatch();   // (a replan goes on with the flight's counts)
        }

        private void BuildPathQuery(out PathQuery q)
        {
            q = new PathQuery
            {
                Controller = flightState.WorldMatrix,
                Owner = Entity.EntityId,
                LocalHull = localHullBox,
                OwnGrids = PathOwnGrids(),   // (quarry lane mates too)
                Cameras = cameraBlocks,
                Sensors = sensors,
                GravityUp = flightState.InGravity ? flightState.GravityUp : Vector3D.Zero,
                // (every drone the same, by the clearance ladder: a harvester's routes start at its tightest step, StartFactor)
                Clearance = AutomataSession.GetConfig().Pathfinding.ObstacleClearance * planFactor,   // (detours: the same as the checks')
                Berth = AutomataSession.GetConfig().Pathfinding.ObstacleClearance * planFactor,   // (halved per failed plan)
                HullCentred = true,
                NoSearch = planNoSearch,   // (PlanPathCheap: the cheap stages only, from the spots round a start the searches failed from)
                SleepingIds = settings != null ? settings.SleepingBlockIds : null,
                AlignToRoute = true,
                // In gravity the route's legs fly level: the pitch / roll limits are for work, corridors and economy tilt
                LevelLegs = flightState.InGravity,
                // Align to P-Gravity: the pitch limit; otherwise the drone may point anywhere
                MaxPitch = flightState.InGravity && settings != null && settings.AlignToPGravity
                    ? MathHelperD.ToRadians(settings.PGravityAlignMaxPitchDegrees) : -1,
                // Every flight; the planner drops it for hops shorter than the altitude (work at ground level) and the
                // route takes off / lands straight up / down at the ends, so connectors and blocks on the ground are reached
                MinAltitude = settings != null && !SweepIgnoresMinAltitude && !CompactFlight ? settings.MinAltitude : 0,   // (a scout sweep in gravity: low; harvesters: indoors)
                ExactAtTightest = BerthNow(planFactor) <= Pathfinding.Clearances.MIN + 1e-6,
            };
        }

        /// <summary>
        /// Waypoints (controller positions, world) from 'start' to 'goal', appended to 'points' (goal excluded):
        /// stand-alone and coming from outside, around the observation area first; each stretch then through the
        /// pathfinder (obstacles the drone can see, terrain). False: no way found.
        /// </summary>
        private bool PlanPath(Vector3D start, Vector3D goal, List<Vector3D> points, bool avoidObservationArea, bool areaOnlyFromOutside = true)
        {
            return PlanPath(start, goal, points, avoidObservationArea, areaOnlyFromOutside, flightState.WorldMatrix.Forward, flightState.WorldMatrix.Up, false);
        }

        /// <summary>Same; 'startForward' / 'startUp': the attitude the drone will have at 'start' (default: now).</summary>
        // Clearance by failed plans (2026-11-13): no route to a goal with the clearance (ObstacleClearance, for the checks and
        // the detours alike) - half of it, again, down to Pathfinding.Clearances.MIN; back to all of it for another goal. The
        // legs of such a route fly slower where it is tight (LegSpeedByClearance).

        /// <summary>
        /// Where a drone's clearance ladder starts (a factor of ObstacleClearance): 1, the full clearance; harvesters
        /// (indoors: a 2 m drone under a 3.5 m roof) at Pathfinding.Clearances.MIN. The only difference left between them: the same
        /// planner chain, leg checks and ladder for every drone (2026-11-18; harvesters had their own direct legs, a fixed
        /// clearance and their own leg checks).
        /// </summary>
        private double StartFactor()
        {
            if (!CompactFlight) return 1;
            double full = BerthNow(1);
            return full > Pathfinding.Clearances.MIN ? Pathfinding.Clearances.MIN / full : 1;
        }

        /// <summary>
        /// A recorded route (server): the waypoints in line with their neighbours dropped where the shortcut is clear for
        /// the hull at the floor clearance (in any of its profiles, PathfindingManager.LegProfile - the recorded attitude
        /// isn't used: the route is for drones of every shape, 2026-11-14), so each link is one the planner will find clear. The legs between consecutive samples stay as flown (logged when they look blocked:
        /// a corner cut between samples - record more often).
        /// </summary>
        private void SimplifyRecorded(Pathfinding.PathfindingManager pm, List<Vector3D> pts, List<Vector3D> fwds, List<Vector3D> ups)
        {
            double was = planFactor;
            planFactor = Pathfinding.Clearances.MIN / Math.Max(Pathfinding.Clearances.MIN, BerthNow(1));
            Pathfinding.PathQuery q;
            BuildPathQuery(out q);
            planFactor = was;
            q.SeeAll = true;   // every known block counts: the drone (at the route's end) sees little of it - a shortcut
                               // out of its view was "clear" and went through walls (2026-11-13)
            pm.BeginPlan();
            Func<Vector3D, Vector3D, Vector3D, Vector3D, bool> clear = (a, b, f, u) =>
            {
                if (Vector3D.DistanceSquared(a, b) < 0.01) return true;
                MatrixD now = q.Controller, att;
                bool tight;
                return pm.LegProfile(ref q, a, b, 0.5, ref now, false, out att, out tight);
            };
            if (Log.DebugEnabled)
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (!clear(pts[i], pts[i + 1], fwds[i], ups[i]))
                    {
                        var hb = pm.LastHitBlock;
                        Log.Debug("Drone {0}: recorded leg {1} -> {2} ({3:F1} m) looks blocked at {4:F2} m clearance by {5} at {6}: kept as flown", Entity.EntityId, i, i + 1, Vector3D.Distance(pts[i], pts[i + 1]), q.Berth, hb != null ? PathfindingManager.BlockName(hb) : "?", hb != null ? hb.Position.ToString() : "?");
                    }
            var raw = new List<Vector3D>(pts);
            var rawAtt = new List<KeyValuePair<Vector3D, Vector3D>>(pts.Count);
            for (int i = 0; i < pts.Count; i++) rawAtt.Add(new KeyValuePair<Vector3D, Vector3D>(fwds[i], ups[i]));
            var cleanAtt = new List<KeyValuePair<Vector3D, Vector3D>>(pts.Count);
            pts.Clear();
            CleanRoute(raw, rawAtt, pts, cleanAtt, clear);
            fwds.Clear();
            ups.Clear();
            for (int i = 0; i < cleanAtt.Count; i++) { fwds.Add(cleanAtt[i].Key); ups.Add(cleanAtt[i].Value); }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: recorded route: {1} waypoints, {2} after the clear straight stretches went", Entity.EntityId, raw.Count, pts.Count);
        }
        private double planFactor = 1;
        private Vector3D planFactorGoal = new Vector3D(double.NaN);

        // The clearance a route between two places needed lately (start ~10 m, goal ~2.5 m): the next plan between them
        // starts there, not at the full clearance found too wide a moment ago (the maze benchmark: 1.0 m failed on every
        // trip, ~45 ms each, 46% of its planning - 2026-11-14). Kept GOAL_FACTOR_TICKS; the full clearance is tried again
        // after that (something moved away).
        private struct GoalFactor { public double Factor; public int Frame; }
        private readonly Dictionary<long, GoalFactor> goalFactors = new Dictionary<long, GoalFactor>();
        private const int GOAL_FACTOR_TICKS = 10 * 60 * 60;
        private const int GOAL_FACTORS_MAX = 32;

        private static long GoalFactorKey(Vector3D start, Vector3D goal)
        {
            long h = 17;
            h = h * 31 + (long)Math.Round(goal.X / 2.5);
            h = h * 31 + (long)Math.Round(goal.Y / 2.5);
            h = h * 31 + (long)Math.Round(goal.Z / 2.5);
            h = h * 31 + (long)Math.Round(start.X / 10);
            h = h * 31 + (long)Math.Round(start.Y / 10);
            h = h * 31 + (long)Math.Round(start.Z / 10);
            return h;
        }

        private bool PlanPath(Vector3D start, Vector3D goal, List<Vector3D> points, bool avoidObservationArea, bool areaOnlyFromOutside,
                              Vector3D startForward, Vector3D startUp, bool looseGoal)
        {
            int nowFrame = MyAPIGateway.Session.GameplayFrameCounter;
            routeLooseGoal = looseGoal;
            long gfKey = GoalFactorKey(start, goal);
            if (!(Vector3D.DistanceSquared(goal, planFactorGoal) <= 4))
            {
                double was = planFactor;
                planFactor = StartFactor();
                planFactorGoal = goal;
                GoalFactor gf;
                if (goalFactors.TryGetValue(gfKey, out gf))
                {
                    if (nowFrame - gf.Frame <= GOAL_FACTOR_TICKS) planFactor = Math.Min(planFactor, gf.Factor);
                    else goalFactors.Remove(gfKey);
                }
                if (Log.DebugEnabled && (was < 1 || planFactor < 1))
                    Log.Debug("Drone {0}: another goal: route clearance {1:F2} m{2}", Entity.EntityId, BerthNow(planFactor), planFactor < 1 ? " (what a route between these places needed lately)" : "");
            }
            int count = points.Count;
            var pmc = PathfindingManager.Instance;
            if (pmc != null) { pmc.PlanBerthCap = 0; pmc.PlanTransient = false; pmc.ResetRealShape(Entity.EntityId); }   // (its own surface measured again: the exact checks)
            double startFactor = planFactor;
            // The escalation, by cost (2026-11-13). From here, PlanRoute's chain: 1. players' custom routes, 2. the straight
            // line, 3. the shared memory, 4. Lazy Theta* (queued: the drone waits), 5. the block lattice - at the full
            // clearance, then at half of it, down to Pathfinding.Clearances.MIN. No way at any: 6. from the spots round it, the way
            // back, a reposition (PlanPathCheap: the cheap stages only, the full clearance again).
            while (true)
            {
                if (PlanPathFrom(start, goal, points, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal))
                {
                    RecoverSucceeded();
                    // (the clearance that worked, when the wider ones failed for good - not for want of measured shapes)
                    if (planFactor >= StartFactor()) goalFactors.Remove(gfKey);
                    else if (planFactor < startFactor && !(pmc != null && pmc.PlanTransient))
                    {
                        if (goalFactors.Count >= GOAL_FACTORS_MAX) goalFactors.Clear();
                        goalFactors[gfKey] = new GoalFactor { Factor = planFactor, Frame = nowFrame };
                    }
                    // Over a custom route recorded at a smaller clearance: the leg-start check and the look-ahead use that
                    // one (with the plan's they found its legs blocked, planned the same route again, and gave up)
                    if (pmc != null && pmc.PlanBerthCap > 0 && BerthNow(planFactor) > pmc.PlanBerthCap + 1e-6)
                    {
                        planFactor = pmc.PlanBerthCap / Math.Max(pmc.PlanBerthCap, BerthNow(1));
                        if (Log.DebugEnabled) Log.Debug("Drone {0}: route over a custom route: its legs checked with {1:F2} m clearance (as recorded)", Entity.EntityId, BerthNow(planFactor));
                    }
                    return true;
                }
                if (points.Count > count) points.RemoveRange(count, points.Count - count);
                double berth = BerthNow(planFactor);
                if (berth <= Pathfinding.Clearances.MIN + 1e-6) break;
                planFactor = Math.Max(planFactor * 0.5, Pathfinding.Clearances.MIN / Math.Max(Pathfinding.Clearances.MIN, BerthNow(1)));
                if (Log.DebugEnabled) Log.Debug("Drone {0}: no route {1:F0} m with {2:F2} m clearance: trying {3:F2} m", Entity.EntityId, Vector3D.Distance(start, goal), berth, BerthNow(planFactor));
            }
            // (if that finds nothing either, the next ask for this goal starts at the tightest clearance: the wider ones
            // failed just now - unless one failed for a passing reason: block shapes being measured on the first plan
            // made the full clearance fail, and every later ask stayed at the tightest, 2026-11-13)
            double tightest = pmc != null && pmc.PlanTransient ? StartFactor() : planFactor;
            planFactor = StartFactor();
            planNoSearch = true;
            bool moved;
            try { moved = PlanPathCheap(start, goal, points, count, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal) > 0; }
            finally { planNoSearch = false; }
            if (!moved) planFactor = tightest;
            return moved;
        }

        // The route clearance (ObstacleClearance) at 'factor'
        private static double BerthNow(double factor)
        {
            return AutomataSession.GetConfig().Pathfinding.ObstacleClearance * factor;
        }

        /// <summary>
        /// A route leg's speed by the room around it (the hull box held at f / u along a -> b, everything known): 5 m all
        /// round - Max speed; 1 m - Approach speed; less (a route through a tight spot, its clearance halved) - Safe speed.
        /// Returns the room: 0 - LEG_ROOM_WIDE or more, 1 - less, 2 - under LEG_ROOM_NORMAL; -1: not looked at.
        /// </summary>
        private int LegSpeedByClearance(FlightOrder leg, Vector3D a, Vector3D b, Vector3D f, Vector3D u, int index)
        {
            var pm = PathfindingManager.Instance;
            if (pm == null) return -1;
            if (Vector3D.DistanceSquared(a, b) < 1) return 1;   // (too short to tell: no speed cap; a turn there in place)
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;
            MatrixD att = MatrixD.CreateWorld(Vector3D.Zero, f, u);
            // A long leg: its ends left out (the dock it leaves, the station it comes to - flown slowly there anyway,
            // leaving from a stop and arriving): a 5.5 km leg out of a dock flew at Safe speed all the way, its scout
            // timed out going home (2026-11-19)
            double len = Vector3D.Distance(a, b);
            if (len > LEG_ROOM_TRIM_FROM)
            {
                double trim = Math.Min(len * 0.2, LEG_ROOM_END_TRIM);
                Vector3D dir = (b - a) / len;
                a += dir * trim;
                b -= dir * trim;
            }
            Vector3D hit;
            VRage.ModAPI.IMyEntity e;
            q.Berth = LEG_ROOM_WIDE;
            if (!pm.SegmentBlocked(ref q, ref att, a, b, 0.5, out hit, out e)) return 0;   // room: Max speed
            q.Berth = LEG_ROOM_NORMAL;
            bool normal = !pm.SegmentBlocked(ref q, ref att, a, b, 0.5, out hit, out e);
            double cap = normal ? ApproachSpeed : SafeSpeed;
            if (leg.SpeedCap <= 0 || cap < leg.SpeedCap) leg.SpeedCap = cap;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: leg {1} ({2:F0} m): {3} ({4}): {5:F1} m/s", Entity.EntityId, index, Vector3D.Distance(a, b),
                normal ? "under 5 m of room" : "under 1 m of room", PathfindingManager.Describe(pm.LastHitBlock), normal ? "Approach speed" : "Safe speed", cap);
            return normal ? 1 : 2;
        }
        private const double LEG_ROOM_WIDE = 5.0, LEG_ROOM_NORMAL = 1.0;   // m around the hull
        private const double LEG_ROOM_TRIM_FROM = 30, LEG_ROOM_END_TRIM = 15;   // m: legs longer: their ends (up to this) not looked at
        private static readonly double TURN_FIRST_COS = Math.Cos(MathHelperD.ToRadians(10));

        /// <summary>
        /// A route leg with little room round it ('room' from LegSpeedByClearance: 1 or 2) flies as the planner checked it:
        /// a change of attitude at its start is a turn in place there, the leg before stopped (TurnFirst), not a turn on the
        /// way. Under LEG_ROOM_NORMAL of room it also keeps to its line and arrives on its end point (a corridor leg's
        /// tolerances: a route leg's loose arrival, up to CONSTRUCTION_ROUTE_TOLERANCE off, cut the corner into the wall),
        /// and the leg before it (either kind) stops on its start, within the corridor tolerance.
        /// </summary>
        private void TightLeg(FlightOrder leg, FlightOrder prev, int room, Vector3D before, Vector3D at, Vector3D to,
                              Vector3D pf, Vector3D pu, Vector3D f, Vector3D u)
        {
            bool turns = Vector3D.Dot(pf, f) < TURN_FIRST_COS || Vector3D.Dot(pu, u) < TURN_FIRST_COS;
            bool precise = room >= 2;
            float speed, tol;
            CorridorFlight(BerthNow(planFactor), out speed, out tol);
            if (turns) leg.TurnFirst = true;
            if (precise)
            {
                leg.LooseArrival = false;
                leg.LineTolerance = tol;
                leg.ArrivalTolerance = tol;
            }
            if (prev == null || !(turns || precise)) return;
            // The leg before stops on this one's start (also in a straight line into it): the planner checked this leg -
            // and the turn in place - from there. Run through loosely (1.5 m off), the drone started the leg into a gate
            // beside its line; the check from there found the gate's edge in the way, and it planned anew five times
            // over (2026-11-14)
            prev.Behavior = WaypointBehavior.FullStop;
            prev.ExitSpeed = 0;
            prev.LooseArrival = false;
            if (prev.ArrivalTolerance > tol) prev.ArrivalTolerance = tol;
        }

        private bool planNoSearch;   // BuildPathQuery: the cheap stages only (PathQuery.NoSearch)
        // The route was planned with a loose goal (onto an object to collect, next to a plot): its last leg's start check
        // doesn't look past its end either (the planner didn't) - the floor in front of a plot "2.4 m ahead" had the
        // harvester plan the same 1 m leg again and again (2026-11-19)
        private bool routeLooseGoal;

        // No route from here: the escape spots round it, the way back, a reposition (cheap stages only): 1 found (or
        // repositioning), -1 the goal is taken, 0 none
        private int PlanPathCheap(Vector3D start, Vector3D goal, List<Vector3D> points, int count, bool avoidObservationArea, bool areaOnlyFromOutside,
                                  Vector3D startForward, Vector3D startUp, bool looseGoal)
        {
            // No way from here (boxed in where it hovers: under / beside the build, between drones): reposition - a free
            // spot it can reach in a straight line - and plan from there; the move there becomes the route's first leg
            var pm = PathfindingManager.Instance;
            if (pm == null) return -1;
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            pm.BeginPlan();
            if (!looseGoal && !pm.GoalFits(ref q, goal)) return -1;   // the goal itself is taken: moving won't help
            // Wedged in (the hull doesn't fit here in any attitude: it got in through precise checks): back the way it
            // came first; escape spots around a tight spot rarely fit
            bool tight = !pm.HullFits(ref q, start);
            if (tight && TryRetrace(start, goal, points, count, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal, pm, ref q))
                return 1;
            EscapeCandidates(start, ref q);
            int planned = 0;
            for (int i = 0; i < escapeSpots.Count && planned < ESCAPE_PLANS_MAX; i++)
            {
                Vector3D s = escapeSpots[i];
                pm.BeginPlan();
                if (!EscapeSpotFits(pm, ref q, s, startForward, startUp) || !pm.SpotReachable(ref q, start, s)) continue;
                planned++;   // a full route from there: the expensive part, a few at most
                if (points.Count > count) points.RemoveRange(count, points.Count - count);
                if (!PlanPathFrom(s, goal, points, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal)) continue;
                points.Insert(count, s);              // first leg: there, keeping the attitude
                legOffset = count;
                legForwards.Insert(0, startForward);
                legUps.Insert(0, startUp);
                if (Log.DebugEnabled) Log.Debug("Drone {0}: no way from where it is; repositioning {1:F0} m ({2}) and going on from there", Entity.EntityId, Vector3D.Distance(start, s), escapeWhy[i]);
                escapeSpots.Clear();
                RecoverSucceeded();
                return 1;
            }
            if (points.Count > count) points.RemoveRange(count, points.Count - count);
            escapeSpots.Clear();
            if (Log.DebugEnabled) Log.Debug("Drone {0}: no way from here or from {1} spots around it{2}", Entity.EntityId, escapeWhy.Count, tight ? " (the hull doesn't fit here in every attitude)" : "");
            if (!tight && TryRetrace(start, goal, points, count, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal, pm, ref q))
                return 1;
            // Stuck (a dock / home trip, a NavigateTo, a replan): fly to a spot nearby and plan again there
            return TryRecoverRoute(start, goal, points, count, startForward, startUp, pm, ref q) ? 1 : 0;
        }

        // Boxed in at the start: the search kept the dock corridor's clearance near it (PathQuery.EscapeRadius). The legs
        // that start in that zone are flown like corridor legs - precise, slow, not watched (the leg-start check and the
        // look-ahead keep the full berth: they would see the way out as blocked and plan again, over and over)
        private bool routeEscape;
        private Vector3D routeEscapeCentre;
        private double routeEscapeRadius, routeEscapeBerth;

        private bool InRouteEscape(Vector3D p)
        {
            return routeEscape && Vector3D.DistanceSquared(p, routeEscapeCentre) < routeEscapeRadius * routeEscapeRadius;
        }

        private void MakeEscapeLeg(FlightOrder leg)
        {
            float speed, tol;
            CorridorFlight(routeEscapeBerth, out speed, out tol);
            MakeCorridorLeg(leg, speed, tol);
        }

        private readonly List<Vector3D> escapeSpots = new List<Vector3D>();
        private readonly List<string> escapeWhy = new List<string>();
        private const int ESCAPE_PLANS_MAX = 6;   // routes planned from reposition spots per failed route
        private static readonly double[] DEPARTURE_ESCAPES = { 8, 16, 32 };

        // Free spots to reposition to when boxed in: just out of a dock, on the way the departure was heading first (out of
        // a covered bay), then up from there; then the pathfinder's escape spots
        private void EscapeCandidates(Vector3D from, ref PathQuery q)
        {
            escapeSpots.Clear();
            escapeWhy.Clear();
            if (departureSet && Vector3D.DistanceSquared(from, departureEnd) < 4)
            {
                Vector3D up, fwd, right;
                PathfindingManager.LevelFrame(ref q, out up, out fwd, out right);
                for (int k = 0; k < DEPARTURE_ESCAPES.Length; k++)
                {
                    double d = DEPARTURE_ESCAPES[k];
                    escapeSpots.Add(from + departureDir * d); escapeWhy.Add("on out of the dock");
                    escapeSpots.Add(from + departureDir * d + up * (d * 0.5)); escapeWhy.Add("on out of the dock and up");
                }
            }
            PathfindingManager.EscapeSpots(ref q, from, escapeSpots, escapeWhy);
        }

        private bool PlanPathFrom(Vector3D start, Vector3D goal, List<Vector3D> points, bool avoidObservationArea, bool areaOnlyFromOutside,
                                  Vector3D startForward, Vector3D startUp, bool looseGoal)
        {
            legOffset = points.Count;
            legForwards.Clear();
            legUps.Clear();
            routeScratch.Clear();
            if (avoidObservationArea && PlanObservationAreaDetour(start, goal, routeScratch, areaOnlyFromOutside))
                if (Log.DebugEnabled) Log.Debug("Drone {0}: around the observation area ({1} waypoints)", Entity.EntityId, routeScratch.Count);
            var pm = PathfindingManager.Instance;
            // A flight lane of this drone's network from near the start to near the goal: through the slot the pathfinder
            // gives it (its points are the route's preferred waypoints, each stretch planned and checked as usual). No sublane
            // free at the entrance yet: it holds outside and asks again (a planner that can wait only); waited too long: it
            // flies without the lane and tells the orchestrator. A hop (the first lane of a way through several, FlyRoute /
            // docking only): the route ends at the lane's end for now, planned again there (UpdateLaneHop10). A trusted lane
            // (flown clear end to end before): its stretches aren't checked here, nor at their start in flight; mostly safe:
            // looked ahead in flight (an obstacle seen: the lane is checked again, LaneSuspect), trusted: not even that; another
            // one flown end to end with nothing in the way is reported clear (UpdateLaneRide10)
            FlightLanes.LaneLeg lane = default(FlightLanes.LaneLeg);
            string laneName = null;
            int laneFirst = -1, laneLast = -1;
            bool laneHop = false, laneTrusted = false;
            if (laneHopOk) laneHopPending = false;
            if (routeScratch.Count == 0 && pm != null && localHullBox.Max.X >= localHullBox.Min.X)
            {
                var join = pm.LaneRoute(jobOrchestratorId, Entity.EntityId, start, goal, localHullBox.HalfExtents, routeScratch, laneHopOk, out lane);
                if (join == FlightLanes.Join.Wait)
                {
                    if (asyncPlanOk) throw PlanPendingException.Instance;
                    routeScratch.Clear();
                }
                else if (join == FlightLanes.Join.GaveUp)
                {
                    QueueLaneGiveUp(lane.Number, lane.AB, lane.Name);
                    routeScratch.Clear();
                }
                else if (join != FlightLanes.Join.None)
                {
                    laneName = lane.Name;
                    laneHop = lane.Hop;
                    laneTrusted = lane.Relaxed && !suspectLanes.Contains(lane.Number);
                    laneFirst = 0;
                    laneLast = routeScratch.Count - 1;
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: {1} lane {2} ({3} points{4}{5})", Entity.EntityId, join == FlightLanes.Join.Beside ? "beside" : "through", laneName, routeScratch.Count, laneHop ? ", to its end: planned again there" : "", laneTrusted ? (lane.Trusted ? ", trusted: not checked" : ", mostly safe: looked ahead only") : "");
                }
            }
            if (laneName != null && !lane.Relaxed && routeScratch.Count > 0) BeginLaneRide(lane, routeScratch[laneLast]);
            else laneRideActive = false;
            bool usePathfinder = pm != null;
            PathQuery q = default(PathQuery);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (routePlanFrame != now)
            {
                // A new route (several PlanPath calls of one route happen in the same tick)
                routePlanFrame = now;
                routeUnseen = 0;
                routeJitArmed = true;
                routeEscape = false;
                trustedLanePoints.Clear();
                relaxedLanePoints.Clear();
                relaxedLaneNumbers.Clear();
            }
            if (usePathfinder)
            {
                RefreshDroneGrids();
                BuildPathQuery(out q);
                q.LooseGoal = looseGoal;
                q.NoHeldTurn = NearDockArea(start);
                q.AsyncOk = asyncPlanOk;
                pm.BeginPlan();
            }
            Vector3D from = start;
            for (int i = 0; i <= routeScratch.Count - (laneHop ? 1 : 0); i++)   // (a hop: not the stretch on to the goal)
            {
                Vector3D to = i < routeScratch.Count ? routeScratch[i] : goal;
                if (laneTrusted && i > laneFirst && i <= laneLast)
                {
                    // Inside a trusted lane: straight on, unchecked
                    points.Add(to);
                    relaxedLanePoints.Add(to);
                    relaxedLaneNumbers.Add(lane.Number);
                    if (lane.Trusted) trustedLanePoints.Add(to);
                    from = to;
                    continue;
                }
                // Observation-area waypoints are only a preference: one the hull doesn't fit at (something built or
                // parked there) is left out, the pathfinder takes the stretch to the next one
                if (usePathfinder && i < routeScratch.Count && !pm.PointFree(ref q, to))
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: observation-area waypoint {1} skipped: {2} there", Entity.EntityId, i + 1, PathfindingManager.Describe(pm.LastHitBlock));
                    continue;
                }
                int before = points.Count;
                RouteResult rr = usePathfinder ? pm.PlanRoute(ref q, from, to, points) : RouteResult.Direct;
                if (rr == RouteResult.Pending) throw PlanPendingException.Instance;   // FlyRoute holds the drone and plans again later
                // A stretch inside the lane that needed a way round: rock, a tree or a static grid in it is reported to the
                // lane's orchestrator (with the way round), which redraws the lane
                if (laneName != null && i > laneFirst && i <= laneLast && (rr == RouteResult.Blocked || points.Count > before))
                {
                    QueueLaneObstruction(lane.Number, lane.AB, laneName, from, to, points, before, rr == RouteResult.Blocked, pm.StretchObstacle(ref q, from, to));
                    laneName = null;
                }
                if (usePathfinder && rr == RouteResult.Blocked)
                {
                    if (routeScratch.Count > 0)
                    {
                        // The way round the observation area is boxed in somewhere (a drone tucked in under / beside
                        // the build): straight to the goal instead, rather than hovering there for good
                        if (Log.DebugEnabled) Log.Debug("Drone {0}: no way round the observation area, trying straight", Entity.EntityId);
                        routeScratch.Clear();
                        laneHop = false;
                        laneTrusted = false;
                        laneRideActive = false;
                        points.RemoveRange(legOffset, points.Count - legOffset);
                        from = start;
                        pm.BeginPlan();
                        i = -1;
                        continue;
                    }
                    return false;
                }
                if (i < routeScratch.Count) points.Add(to);
                // Boxed in at the start, a way out found with corridor clearance near it: those legs fly as corridor legs
                if (usePathfinder && q.EscapeRadius > 0 && !routeEscape)
                {
                    routeEscape = true;
                    routeEscapeCentre = q.EscapeCentre;
                    routeEscapeRadius = q.EscapeRadius;
                    routeEscapeBerth = q.EscapeBerth;
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: boxed in here: out with {1:F2} m clearance for the first {2:F0} m (corridor legs)", Entity.EntityId, q.EscapeBerth, q.EscapeRadius);
                }
                from = to;
            }
            routeScratch.Clear();
            if (laneHop && points.Count > legOffset)
            {
                laneHopPending = true;
                laneHopSpot = points[points.Count - 1];
            }

            // Attitudes along the route: aligned with each leg where the drone has room to turn
            routeChain.Clear();
            routeChain.Add(start);
            for (int i = legOffset; i < points.Count; i++) routeChain.Add(points[i]);
            routeChain.Add(goal);
            if (usePathfinder)
            {
                pm.AssignAttitudes(ref q, routeChain, legForwards, legUps, startForward, startUp);
                int notSeen, checks, rays, blocks, lookups, trees, indexed;
                double ms, buildMs;
                pm.PlanStats(out notSeen, out checks, out rays, out blocks, out ms, out lookups, out trees, out indexed, out buildMs);
                routeUnseen += notSeen;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: route {1:F0} m, {2} legs: {3:F2} ms, {4} scene checks, {5} blocks tested, {6} rays, {7} stretches not in view yet; " + "scene: {8} area lookups, {9} block trees built ({10} blocks, {11:F2} ms of the total)", Entity.EntityId, Vector3D.Distance(start, goal), routeChain.Count - 1, ms, checks, blocks, rays, notSeen, lookups, trees, indexed, buildMs);
            }
            else
                for (int i = 0; i + 1 < routeChain.Count; i++)
                {
                    legForwards.Add(startForward);
                    legUps.Add(startUp);
                }
            routeChain.Clear();
            return true;
        }

        /// <summary>
        /// Attitude for the leg ending at points[index] of the last PlanPath (-1: the leg to its goal). Legs before
        /// the planned part (a back-out) keep the current attitude.
        /// </summary>
        private void LegAttitude(int index, out Vector3D forward, out Vector3D up)
        {
            int k = index < 0 ? legForwards.Count - 1 : index - legOffset;
            if (k < 0 || k >= legForwards.Count)
            {
                forward = flightState.WorldMatrix.Forward;
                up = flightState.WorldMatrix.Up;
                return;
            }
            forward = legForwards[k];
            up = legUps[k];
        }

        // Route legs: the given attitude, turned to while flying (the pathfinder checked the room for it)
        private void ApplyLegAttitude(FlightOrder leg, bool world, ref MatrixD am, Vector3D forward, Vector3D up)
        {
            leg.ArrivalTolerance = CONSTRUCTION_ROUTE_TOLERANCE;
            leg.HoldAttitudeInTransit = true;   // anchored first leg: MatchSpeed
            SetExplicitAttitude(leg, world, ref am, forward, up);
        }

        private static readonly double RUN_THROUGH_COS = Math.Cos(MathHelperD.ToRadians(PathfindingManager.RUN_THROUGH_DEGREES));
        private static readonly double ARC_RUN_THROUGH_COS = Math.Cos(MathHelperD.ToRadians(PathfindingManager.ARC_RUN_THROUGH_DEGREES));

        // How to pass waypoint 'at' between 'from' and 'to', and at what speed; a change of attitude there of more than RUN_THROUGH_DEGREES (25°: turning to the next leg) means a stop
        private void PassWaypoint(Vector3D from, Vector3D at, Vector3D to, ref Vector3D fwdIn, ref Vector3D fwdOut,
                                  out WaypointBehavior pass, out float exit)
        {
            pass = PathfindingManager.Classify(at - from, to - at);
            // A rounded corner's point: flown through up to ARC_RUN_THROUGH_DEGREES (its chords turn up to 44°; the bend speed
            // comes from the drone's thrust, TurnPassSpeed)
            var pm = PathfindingManager.Instance;
            bool arc = pm != null && pm.IsArcPoint(at);
            Vector3D din = at - from, dout = to - at;
            if (arc && din.LengthSquared() > 1e-6 && dout.LengthSquared() > 1e-6
                && Vector3D.Dot(Vector3D.Normalize(din), Vector3D.Normalize(dout)) > ARC_RUN_THROUGH_COS)
                pass = WaypointBehavior.RunThrough;
            if (fwdIn.LengthSquared() > 0.5 && fwdOut.LengthSquared() > 0.5 && Vector3D.Dot(fwdIn, fwdOut) < (arc ? ARC_RUN_THROUGH_COS : RUN_THROUGH_COS))
                pass = WaypointBehavior.FullStop;
            // Between two open legs (OPEN_LEG long: a lane's, a long flight's) a run-through isn't slowed beyond what the bend and
            // the legs after it need (ExitSpeedFor: TurnPassSpeed, StoppableSpeed); between short ones (around obstacles, the
            // corners the planner hugs them at) twice the Approach speed at most
            bool open = din.LengthSquared() >= OPEN_LEG * OPEN_LEG && dout.LengthSquared() >= OPEN_LEG * OPEN_LEG;
            exit = pass == WaypointBehavior.RunThrough ? (open ? MaxSpeed : Math.Min(MaxSpeed, ApproachSpeed * 2f))
                 : pass == WaypointBehavior.SlowApproach ? ApproachSpeed : 0f;
        }
        private const double OPEN_LEG = 200;   // m: legs this long both sides of a run-through: its speed set by the bend only

        /// <summary>The hull box centre, controller frame: route legs steer this point (turns about it sweep the least).</summary>
        private Vector3D HullCentreLocal
        {
            get { return localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.Center : Vector3D.Zero; }
        }

        private Vector3D HullCentreWorld
        {
            get { return flightState.Position + Vector3D.TransformNormal(HullCentreLocal, flightState.WorldMatrix); }
        }

        /// <summary>
        /// Route legs through 'points' (hull centre positions; the first 'backOuts' keep the current attitude and are
        /// not watched: docks), then a move to 'turnAt' (when not there already) and a turn in place at 'turnAt' to
        /// (turnFwd, turnUp): turning about the hull centre sweeps the smallest circle. Each leg gets its PlanPath
        /// attitude; a waypoint where the attitude changes more than 15° is a stop. Legs go to 'legsOut'.
        /// Returns the first order (null: refused).
        /// </summary>
        private FlightOrder BuildHullLegs(IMyTerminalBlock anchor, ref MatrixD am, List<Vector3D> points, int backOuts,
                                          Vector3D turnAt, Vector3D turnFwd, Vector3D turnUp, List<FlightOrder> legsOut)
        {
            bool world = anchor == null;
            Vector3D hc = HullCentreLocal;
            Vector3D hullNow = HullCentreWorld;
            int n = points.Count;
            bool move = n > 0 || Vector3D.DistanceSquared(hullNow, turnAt) > CONSTRUCTION_ROUTE_TOLERANCE * CONSTRUCTION_ROUTE_TOLERANCE;
            int total = n + (move ? 1 : 0) + 1;
            FlightOrder first = null;
            Vector3D before = hullNow, at = hullNow, pf = flightState.WorldMatrix.Forward, pu = flightState.WorldMatrix.Up;
            for (int k = 0; k < total; k++)
            {
                Vector3D point, f, u;
                bool watched;
                if (k < n)
                {
                    point = points[k];
                    if (k < backOuts) { f = flightState.WorldMatrix.Forward; u = flightState.WorldMatrix.Up; watched = false; }
                    else { LegAttitude(k, out f, out u); watched = true; }
                }
                else if (move && k == n) { point = turnAt; LegAttitude(-1, out f, out u); watched = true; }
                else { point = turnAt; f = turnFwd; u = turnUp; watched = false; }   // turn in place

                FlightOrder leg;
                if (first == null)
                {
                    leg = world ? OrderGoTo(point) : OrderGoToRelative(anchor, WorldToAnchorPoint(ref am, point));
                    if (leg == null) return null;
                    // Its line starts where the hull centre is now
                    if (world) leg.ApproachFrom = leg.TransitStart = Vector3DData.FromVector3D(hullNow);
                    else leg.ApproachFromLocal = leg.TransitStartLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref am, hullNow));
                    first = leg;
                }
                else
                {
                    WaypointBehavior pass;
                    float exit;
                    PassWaypoint(before, at, point, ref pf, ref f, out pass, out exit);
                    // (a roll: the same forward, another up)
                    if (pu.LengthSquared() > 0.5 && u.LengthSquared() > 0.5 && Vector3D.Dot(pu, u) < RUN_THROUGH_COS) { pass = WaypointBehavior.FullStop; exit = 0f; }
                    leg = QueueGoTo(point, pass, exit);
                }
                leg.ReferenceOffset = Vector3DData.FromVector3D(hc);
                ApplyLegAttitude(leg, world, ref am, f, u);
                leg.Watched = watched;
                leg.LooseArrival = true;   // the final work / dock order settles position and attitude precisely
                if (k < backOuts) MakeCorridorLeg(leg, backOutSpeedCap, backOutLineTol);   // out of a dock / hangar: precise, slow
                else if (k < n + (move ? 1 : 0) && InRouteEscape(at)) MakeEscapeLeg(leg);   // boxed in: out of it with corridor clearance
                else if (k < n + (move ? 1 : 0))
                {
                    int room = LegSpeedByClearance(leg, at, point, f, u, k + 1);
                    if (room >= 1) TightLeg(leg, legsOut.Count > 0 ? legsOut[legsOut.Count - 1] : null, room, before, at, point, pf, pu, f, u);
                }
                legsOut.Add(leg);
                before = at;
                at = point;
                pf = f;
                pu = u;
            }
            return first;
        }

        /// <summary>
        /// Where the drone works a block face from: the tool's work sphere on 'point' (a face centre, 'dirW' its
        /// normal), the approach point out along the normal (closest first: where the hull clears the face, further
        /// out - 1 m steps, then 2.5 m - until it has room to turn there). 'checkSite': also that nothing else is in
        /// the way - the hull fits at the approach point, and the straight line in is clear of everything (other
        /// drones, ships: not only the target grid, whose cells the face choice already checks). False: pick another face.
        /// </summary>
        private bool WorkSite(ref ToolMount mount, Vector3D point, Vector3D dirW, Vector3D dirA, bool block, bool checkSite, bool pushOut,
                              IMyCubeGrid targetGrid, Vector3I targetMin, Vector3I targetMax, bool wholeUnit, Vector3D shift,
                              out MatrixD frame, out Vector3D sphereCentre, out Vector3D approach)
        {
            // dirA: the approach line, dirW tilted (up to ~50°) or dirW itself. The sphere stays on the face point
            // along dirW either way; only the drone comes in at an angle (tool pointing down the line).
            // Blocks: the sphere reaches FACE_DEPTH into the face (margin for the arrival tolerance), little more,
            // so it touches as little of the neighbours as the tool size allows. Other targets: TOOL_REACH_FRACTION.
            double depth = Math.Min(mount.WorkRadius * 0.9, Math.Max(FACE_DEPTH_MIN, Math.Min(FACE_DEPTH, mount.WorkRadius * 0.5)));
            double reach = block ? mount.WorkRadius - depth : mount.WorkRadius * TOOL_REACH_FRACTION;
            frame = WorkFrame(ref mount, -dirA);
            // Align to P-Gravity: the attitude is held within the pitch / roll limits, the tool then no longer points
            // down the line (e.g. level above a top face, the tool forward): the sphere still sits on the face point
            bool limited = Vector3D.Dot(Vector3D.TransformNormal(mount.LocalForward, frame), -dirA) < 0.9998;
            // A whole small-grid work unit: the sphere as close to the face as the hull allows (front WORK_STANDOFF
            // off it), so it reaches as deep into the unit as it can; the caller checks it reaches the block
            if (wholeUnit && block)
            {
                Vector3D fromTool = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frame);
                Vector3D half = localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.HalfExtents : new Vector3D(1);
                double halfAlongW = PathfindingManager.HalfSizeAlong(ref frame, ref half, dirW);
                reach = MathHelperD.Clamp(WORK_STANDOFF - Vector3D.Dot(fromTool, dirW) + halfAlongW, 0, mount.WorkRadius);
            }
            sphereCentre = point + dirW * reach + shift;   // shift: precise placement (PreciseShift)
            if (!pushOut)
            {
                approach = sphereCentre + dirA * ManoeuvreDistance();
                return true;
            }
            Vector3D hullFromTool = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frame);
            // Closest first: from where the hull just clears the face plane, out until it has room to turn there (was:
            // the drone's length + 2.5 m from the start, ~10-15 m out for a big drone, before any check)
            // (Without the pathfinder nothing is checked: the old fixed distance)
            double closest = PathfindingManager.Instance != null
                ? ClosestApproach(point, dirW, dirA, sphereCentre, hullFromTool, ref frame) : ManoeuvreDistance();
            approach = sphereCentre + dirA * closest;
            bool free;
            approach = WithTurnRoom(approach, dirA, hullFromTool, ref frame, out free,
                                    ManoeuvreDistance() + TURN_ROOM_STEPS * TURN_ROOM_STEP - closest, TURN_ROOM_FINE_SPAN);
            workSiteWhy = null;
            workSiteHullPastFace = false;
            if (!checkSite) return true;
            if (!free) { workSiteWhy = "no room to turn at the approach point: " + turnRoomBlocker; return false; }
            var pm = PathfindingManager.Instance;
            if (pm == null) return true;
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;
            q.Berth = Pathfinding.Clearances.WORK;
            q.StrictEnds = true;
            q.IgnoreGrid = targetGrid;   // the block worked on (its cells): the tool reaches into it
            q.IgnoreMin = targetMin;
            q.IgnoreMax = targetMax;
            MatrixD att = frame;
            Vector3D h;
            VRage.ModAPI.IMyEntity e;
            // Straight in, the hull (plus Pathfinding.Clearances.WORK around it) from the approach point to where it works, its
            // front WORK_STANDOFF off the face plane: neighbours flush with the face don't count, anything sticking out
            // within the berth does (another block, another drone)
            Vector3D hullWork = sphereCentre + hullFromTool;
            double frontGap = Vector3D.Dot(hullWork - point, dirW) - PathfindingManager.HullHalfSizeAlong(ref q, ref att, dirW);
            double along = Vector3D.Dot(dirA, dirW);
            // Straight in: the hull's front stops WORK_STANDOFF off the face for the check (neighbours flush with the
            // face don't count). Tilted with the hull reaching below the face plane: checked where it really is - built
            // neighbours there refuse it, empty / projected cells don't.
            // Limited attitude with the hull reaching past the face plane: checked where it really is, the target too
            // (with the hull wholly outside the face plane the target's cells can't be in the way)
            if (limited && frontGap < 0) q.IgnoreGrid = null;
            else if (frontGap < WORK_STANDOFF && (along >= 0.999 || frontGap >= 0))
                hullWork += dirA * ((WORK_STANDOFF - frontGap) / Math.Max(0.5, along));
            // Weld sites: built neighbours of the target in the way by their cells are checked by their real shape
            if (siteWeld && targetGrid != null && WELDER_USE_NEIGHBOR_SHAPES) q.RealShapeGrid = targetGrid;
            if (pm.SegmentBlocked(ref q, ref att, approach + hullFromTool, hullWork, 0.5, out h, out e, true, false))
            {
                // Weld sites: the bounding box doesn't fit; the drone's real shape (a welder snout between neighbours) may
                if (!(siteWeld && WELDER_USE_DRONE_SHAPE
                      && ShapeWayInFree(pm, q, ref att, ref mount, ref frame, approach, sphereCentre, point, dirW, dirA, limited, out e)))
                {
                    workSiteHullPastFace = limited && frontGap < 0;   // the attitude, not the cell: every cell of this side fails alike
                    workSiteWhy = DescribeWayInBlocked(pm.LastHitBlock, e, targetGrid, point, dirW);
                    return false;
                }
                siteByShape++;
            }
            // From below (an underside): the hull must clear the ground there and on the way up
            if (flightState.InGravity && Vector3D.Dot(dirA, flightState.GravityUp) < -0.3)
            {
                Vector3D gUp = flightState.GravityUp;
                double below = PathfindingManager.HullHalfSizeAlong(ref q, ref att, gUp) + Pathfinding.Clearances.WORK;
                double atWork = PathfindingManager.HeightAboveTerrain(hullWork) - below;
                double atApproach = PathfindingManager.HeightAboveTerrain(approach + hullFromTool) - below;
                if (atWork < 0 || atApproach < 0)
                {
                    workSiteWhy = string.Format("from below: the hull would be {0:F1} m into the ground", -Math.Min(atWork, atApproach));
                    return false;
                }
            }
            return true;
        }

        #region Weld sites by shape
        private const int REAL_SHAPE_RAYS_PER_DISPATCH = 600;    // rays the neighbours' real-shape checks may spend per site choice
        private bool siteWeld;       // the site being chosen is a weld site (shape checks apply)
        private int siteByShape;     // sites accepted by the drone's shape (bounding box refused) this choice

        /// <summary>
        /// The drone's real shape (HullShape boxes) straight in from the approach point to the work position: each box
        /// swept on its own (berth Pathfinding.Clearances.FINE), the front WORK_STANDOFF off the face as for the bounding box. 'q' is a
        /// copy (its hull box is replaced per shape box). True: no box hits anything.
        /// </summary>
        private bool ShapeWayInFree(Pathfinding.PathfindingManager pm, PathQuery q, ref MatrixD att, ref ToolMount mount, ref MatrixD frame,
                                    Vector3D approach, Vector3D sphereCentre, Vector3D point, Vector3D dirW, Vector3D dirA, bool limited,
                                    out VRage.ModAPI.IMyEntity hitEntity)
        {
            hitEntity = null;
            var shape = HullShape();
            if (shape.Count == 0) return false;
            Vector3D toolOffset = Vector3D.TransformNormal(mount.LocalPoint, frame);
            Vector3D ctrlApproach = approach - toolOffset, ctrlWork = sphereCentre - toolOffset;
            // The shape's front at the work position (closest to the face plane)
            double frontGap = double.MaxValue;
            for (int i = 0; i < shape.Count; i++)
            {
                var b = shape[i];
                Vector3D c = ctrlWork + Vector3D.TransformNormal(b.Center, frame);
                Vector3D hb = b.HalfExtents;
                double half = Pathfinding.PathfindingManager.HalfSizeAlong(ref frame, ref hb, dirW);
                frontGap = Math.Min(frontGap, Vector3D.Dot(c - point, dirW) - half);
            }
            double along = Vector3D.Dot(dirA, dirW);
            if (limited && frontGap < 0) q.IgnoreGrid = null;
            else if (frontGap < WORK_STANDOFF && (along >= 0.999 || frontGap >= 0))
                ctrlWork += dirA * ((WORK_STANDOFF - frontGap) / Math.Max(0.5, along));
            q.Berth = Pathfinding.Clearances.FINE;
            q.HullCentred = false;
            Vector3D h;
            for (int i = 0; i < shape.Count; i++)
            {
                q.LocalHull = shape[i];
                if (pm.SegmentBlocked(ref q, ref att, ctrlApproach, ctrlWork, 0.5, out h, out hitEntity, true, false)) return false;
            }
            return true;
        }

        // Dispatch log: how the site was found (shape / real-shape neighbours)
        private string SiteShapeNote()
        {
            if (!siteWeld) return "";
            var pm = Pathfinding.PathfindingManager.Instance;
            int clears = pm != null ? pm.RealShapeClears : 0, rays = pm != null ? pm.RealShapeRays : 0;
            if (siteByShape == 0 && clears == 0) return "";
            return string.Format(" ({0} site(s) fit by the drone's shape, {1} neighbour cell(s) clear by their real shape, {2} rays)", siteByShape, clears, rays);
        }
        #endregion

        private const double MIN_APPROACH_RUN = 1.0;   // m: the shortest straight run in from the approach point

        /// <summary>
        /// m out from the sphere centre along 'dirA' where the hull, in the work attitude, clears the face plane by
        /// Pathfinding.Clearances.WORK: the first approach point tried (WithTurnRoom moves it out until the turn there is free).
        /// </summary>
        private double ClosestApproach(Vector3D point, Vector3D dirW, Vector3D dirA, Vector3D sphereCentre, Vector3D hullFromTool, ref MatrixD frame)
        {
            Vector3D half = localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.HalfExtents : new Vector3D(1);
            double halfAlongW = PathfindingManager.HalfSizeAlong(ref frame, ref half, dirW);
            double centreOut = Vector3D.Dot(sphereCentre + hullFromTool - point, dirW);   // hull centre over the face, working
            double along = Math.Max(0.3, Vector3D.Dot(dirA, dirW));
            double d = (halfAlongW + Pathfinding.Clearances.WORK - centreOut) / along;
            return MathHelperD.Clamp(d, MIN_APPROACH_RUN, ManoeuvreDistance());
        }

        private string workSiteWhy;   // why the last WorkSite check refused (debug log)
        private bool workSiteHullPastFace;   // refused because the held (level) attitude puts the hull into the face plane

        // What blocked a face's way in, in enough detail to tell a neighbour of the target, a subgrid of the build
        // (rotor / piston part) and another drone apart
        private string DescribeWayInBlocked(IMySlimBlock hitBlock, VRage.ModAPI.IMyEntity e, IMyCubeGrid targetGrid, Vector3D facePoint, Vector3D normal)
        {
            var grid = e as IMyCubeGrid;
            if (grid == null) return "way in blocked by " + (e != null ? e.DisplayName : "?");
            string relation = "unrelated grid";
            if (grid == targetGrid) relation = "the target grid";
            else if (targetGrid != null)
            {
                var group = new List<IMyCubeGrid>();
                targetGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(group);
                if (group.Contains(grid)) relation = "a subgrid of the target (rotor / piston / hinge)";
            }
            string what = "?";
            double offFace = 0, aside = 0;
            if (hitBlock != null)
            {
                what = string.Format("{0} at {1}", PathfindingManager.BlockName(hitBlock), hitBlock.Min);
                Vector3D c = grid.GridIntegerToWorld(hitBlock.Position);
                offFace = Vector3D.Dot(c - facePoint, normal);
                aside = Vector3D.Reject(c - facePoint, normal).Length();
            }
            return string.Format("way in blocked by {0} ({1}, {2} grid, {3}, {4}), block {5}: {6:F1} m out from the face, {7:F1} m aside",
                                 grid.DisplayName, grid.EntityId, grid.GridSizeEnum, grid.IsStatic ? "static" : "dynamic", relation,
                                 what, offFace, aside);
        }

        #region Turn room
        /// <summary>
        /// Moves 'point' (a reference point: tool / connector) out along 'outward' (2.5 m steps, at most 4; work sites:
        /// 1 m steps first, further out) until the
        /// drone has room to turn there about its hull centre ('hullFromPoint': world offset from the point to the
        /// hull centre in the final attitude). Unchanged without the pathfinder.
        /// </summary>
        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint, ref MatrixD frame)
        {
            bool free;
            return WithTurnRoom(point, outward, hullFromPoint, ref frame, out free);
        }

        private const double TURN_ROOM_STEP = 2.5;     // m per step out
        private const int TURN_ROOM_STEPS = 4;         // steps (docks: 10 m out at most)
        private const double TURN_ROOM_FINE_STEP = 1.0;   // work sites: 1 m steps over the first metres (closest first)...
        private const double TURN_ROOM_FINE_SPAN = 6.0;   // ...this many, then TURN_ROOM_STEP
        private string turnRoomBlocker;                // what the last WithTurnRoom found in the way (debug log)

        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint, ref MatrixD frame, out bool free)
        {
            return WithTurnRoom(point, outward, hullFromPoint, ref frame, out free, TURN_ROOM_STEPS * TURN_ROOM_STEP, 0);
        }

        // Same; 'frame': the attitude turned to there; 'free': room was found (false: the last point tried, still without room).
        // 'maxOut': how far out it may move; 'fineSpan': metres stepped TURN_ROOM_FINE_STEP at a time first.
        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint, ref MatrixD frame, out bool free,
                                      double maxOut, double fineSpan)
        {
            free = true;
            turnRoomBlocker = null;
            var pm = PathfindingManager.Instance;
            if (pm == null || outward.LengthSquared() < 1e-6) return point;
            outward.Normalize();
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;   // the work site / dock is known, not only what the instruments see from here
            q.Berth = Pathfinding.Clearances.WORK;
            pm.BeginPlan();
            double moved = 0;
            while (true)
            {
                if (pm.TurnSweepFree(ref q, point + hullFromPoint, ref frame, Pathfinding.Clearances.WORK)) return point;
                if (moved >= maxOut - 1e-3) break;
                double step = Math.Min(moved < fineSpan - 1e-3 ? TURN_ROOM_FINE_STEP : TURN_ROOM_STEP, maxOut - moved);
                point += outward * step;
                moved += step;
            }
            turnRoomBlocker = PathfindingManager.Describe(pm.LastHitBlock);
            free = false;
            return point;
        }
        #endregion

        #region Player routes (debug orders)
        // Waypoints the player gave that are still ahead (runtime: FlightOrder.PlayerWaypoint legs)
        private readonly List<Vector3D> playerWaypoints = new List<Vector3D>();

        /// <summary>
        /// Debug "Navigate" / "Queue waypoint": through the player's waypoints in order, each stretch through the
        /// pathfinder, legs aligned with the route where there is room; watched and replanned like job routes.
        /// </summary>
        private bool FlyPlayerRoute(List<Vector3D> waypoints)
        {
            if (waypoints.Count == 0) return false;
            CaptureFlightState();
            routeScratch.Clear();   // PlanPath uses it
            var pts = new List<Vector3D>();
            var isPlayer = new List<bool>();
            var fwds = new List<Vector3D>();
            var ups = new List<Vector3D>();
            Vector3D from = HullCentreWorld;   // the route steers the hull centre
            Vector3D sf = flightState.WorldMatrix.Forward, su = flightState.WorldMatrix.Up;
            var docked = DockedConnector();
            int backOuts = 0;
            backOutSpeedCap = 0;
            if (docked != null)
            {
                // Out of the dock first, as the drone is oriented (straight out, or the hangar's corridor)
                backOuts = DeparturePoints(docked, ref from, pts);
                for (int i = 0; i < backOuts; i++)
                {
                    isPlayer.Add(false);
                    fwds.Add(sf);
                    ups.Add(su);
                }
            }
            for (int w = 0; w < waypoints.Count; w++)
            {
                int before = pts.Count;
                if (!PlanPath(from, waypoints[w], pts, false, true, sf, su, true))
                {
                    Report(MyStrings.Drone_NavNoRouteToWaypoint, w + 1);
                    return false;
                }
                for (int i = before; i < pts.Count; i++)
                {
                    isPlayer.Add(false);
                    Vector3D f, u;
                    LegAttitude(i, out f, out u);
                    fwds.Add(f);
                    ups.Add(u);
                }
                pts.Add(waypoints[w]);
                isPlayer.Add(true);
                Vector3D gf, gu;
                LegAttitude(-1, out gf, out gu);
                fwds.Add(gf);
                ups.Add(gu);
                from = waypoints[w];
                sf = gf;   // the next stretch starts with the attitude the drone arrives with
                su = gu;
            }

            MatrixD none = MatrixD.Identity;
            Vector3D hullNow = HullCentreWorld;
            FlightOrder leg = OrderGoTo(pts[0]);
            if (leg == null) return false;
            leg.ApproachFrom = leg.TransitStart = Vector3DData.FromVector3D(hullNow);
            Vector3D prev = hullNow;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    WaypointBehavior pass;
                    float exit;
                    Vector3D fin = fwds[i - 1], fout = fwds[i];
                    PassWaypoint(prev, pts[i - 1], pts[i], ref fin, ref fout, out pass, out exit);
                    leg = QueueGoTo(pts[i], pass, exit);
                    prev = pts[i - 1];
                }
                leg.ReferenceOffset = Vector3DData.FromVector3D(HullCentreLocal);
                ApplyLegAttitude(leg, true, ref none, fwds[i], ups[i]);
                leg.Watched = i >= backOuts;   // not the back-out: the base is right beside it
                if (i < backOuts) MakeCorridorLeg(leg, backOutSpeedCap, backOutLineTol);
                else if (InRouteEscape(i == 0 ? hullNow : pts[i - 1])) MakeEscapeLeg(leg);   // boxed in: out with corridor clearance
                else LegSpeedByClearance(leg, i == 0 ? hullNow : pts[i - 1], pts[i], fwds[i], ups[i], i + 1);
                leg.PlayerWaypoint = isPlayer[i];
                if (isPlayer[i]) leg.ArrivalTolerance = WaypointTol;
            }
            playerWaypoints.Clear();
            playerWaypoints.AddRange(waypoints);
            routeOwner = RouteOwner.Player;
            ResetRoute();
            return true;
        }

        // The player's waypoints not reached yet: those of the active leg, the next one and the queued ones
        private void RemainingPlayerWaypoints(List<Vector3D> into)
        {
            into.Clear();
            if (activeFlightOrder != null && activeFlightOrder.PlayerWaypoint) into.Add(activeFlightOrder.Target.ToVector3D());
            if (nextLeg != null && nextLeg.PlayerWaypoint) into.Add(nextLeg.Target.ToVector3D());
            foreach (var leg in queuedLegs) if (leg.PlayerWaypoint) into.Add(leg.Target.ToVector3D());
        }
        #endregion

        #region Replanning
        private void RememberFlyRoute(IMyTerminalBlock anchor, ref MatrixD am, Vector3D target, Vector3D approach, double hull,
                                      Vector3D referenceOffset, ref MatrixD frame, bool checkApproachLine)
        {
            routeOwner = RouteOwner.FlyRoute;
            flyAnchorId = anchor != null ? anchor.EntityId : 0;
            bool world = anchor == null;
            flyTarget = world ? target : WorldToAnchorPoint(ref am, target);
            flyApproach = world ? approach : WorldToAnchorPoint(ref am, approach);
            flyForward = world ? frame.Forward : WorldToAnchorDir(ref am, frame.Forward);
            flyUp = world ? frame.Up : WorldToAnchorDir(ref am, frame.Up);
            flyRefOffset = referenceOffset;
            flyHull = hull;
            flyCheckLine = checkApproachLine;
        }

        private void RememberDocking(long targetId, int mount, Vector3DData offset, Vector3DData forward, Vector3DData up)
        {
            routeOwner = RouteOwner.Docking;
            dockReplanTargetId = targetId;
            dockReplanMount = mount;
            dockReplanOffset = offset;
            dockReplanForward = forward;
            dockReplanUp = up;
        }

        // A new route from where the drone is, to the same place. False: none.
        private bool replanning;   // inside Replan: anchored orders start moving, not matching the anchor's speed

        private bool Replan()
        {
            bool was = recoverPlanning, wasReplanning = replanning;
            recoverPlanning = true;
            replanning = true;
            try { return ReplanCore(); }
            finally { recoverPlanning = was; replanning = wasReplanning; }
        }

        private bool ReplanCore()
        {
            CaptureFlightState();
            if (routeOwner == RouteOwner.FlyRoute)
            {
                IMyTerminalBlock anchor = null;
                MatrixD am = MatrixD.Identity;
                if (flyAnchorId != 0)
                {
                    anchor = AnchorDirectory.Resolve(block, flyAnchorId);
                    if (anchor == null) return false;
                    am = anchor.WorldMatrix;
                }
                bool world = anchor == null;
                Vector3D target = world ? flyTarget : AnchorToWorldPoint(ref am, flyTarget);
                Vector3D approach = world ? flyApproach : AnchorToWorldPoint(ref am, flyApproach);
                Vector3D fwd = world ? flyForward : AnchorToWorldDir(ref am, flyForward);
                Vector3D up = world ? flyUp : AnchorToWorldDir(ref am, flyUp);
                MatrixD frame = MatrixD.CreateWorld(Vector3D.Zero, fwd, up);
                return FlyRoute(anchor, ref am, target, approach, flyHull, flyRefOffset, ref frame, flyCheckLine,
                                cPhase == ConstructionPhase.Running && currentJob != null)
                       == Orchestrator.TaskFailure.None;
            }
            if (routeOwner == RouteOwner.Sweep)
            {
                int replans = routeReplans;
                bool ok = SweepReplan();
                routeReplans = replans;   // SweepFlyFrom starts a new count: keep this flight's
                return ok;
            }
            if (routeOwner == RouteOwner.Player)
            {
                var remaining = new List<Vector3D>();
                RemainingPlayerWaypoints(remaining);
                int replans = routeReplans;
                bool ok = remaining.Count > 0 && FlyPlayerRoute(remaining);
                routeReplans = replans;   // FlyPlayerRoute starts a new count: keep this flight's
                return ok;
            }
            if (routeOwner == RouteOwner.Docking)
            {
                var target = AnchorDirectory.Resolve(block, dockReplanTargetId) as IMyShipConnector;
                if (target == null || dockReplanMount >= connectorMounts.Count) return false;
                int replans = routeReplans;   // StartDocking starts a new count: keep this flight's (it re-planned the same
                                              // blocked leg every 10 s without end, 2026-11-14)
                bool ok = StartDocking(target, dockReplanMount, dockReplanOffset, dockReplanForward, dockReplanUp, true);
                routeReplans = replans;
                return ok;
            }
            return false;
        }

        /// <summary>UpdateBeforeSimulation10, AI on: the leg the drone just started, checked once (see legChecked).</summary>
        private void CheckLegStart()
        {
            var o = activeFlightOrder;
            if (o == null || preflightStage != 0 || o == legChecked) return;
            legChecked = o;
            legsSinceUndock++;
            if (!o.Watched || routeOwner == RouteOwner.None) return;
            if (RelaxedLaneLeg(o) != 0) return;                                      // in a trusted / mostly safe lane: flown clear before
            if (RecoverFlying) return;                                               // to a reposition spot: checked clear when chosen
            if (legsSinceUndock <= Math.Min(2, undockLegs)) return;                  // undocking (the back-out legs only)
            if (parkPhase != ParkPhase.None && o == parkOrder) return;               // landing: ends on the surface it parks on
            if (dockingOrder != null && (o == dockingOrder || nextLeg == dockingOrder
                || (nextLeg == null && queuedLegs.Count > 0 && queuedLegs.Peek() == dockingOrder))) return;   // docking
            var pm = PathfindingManager.Instance;
            if (pm == null) return;
            if (droneGrids.Count == 0) RefreshDroneGrids();
            CaptureFlightState();
            Vector3D from = HullCentreWorld;
            Vector3D to = o.Target.ToVector3D() + (from - ReferencePoint(o));
            double len = Vector3D.Distance(from, to);
            if (len < 1) return;
            PathQuery q;
            BuildPathQuery(out q);
            // Within the clearance of where the leg was planned to start (arrived a little off it): the leg as planned -
            // its check covers the hull getting back onto its line. From where the hull is, the sweep cut across the line
            // into the edge of the gate it was planned through, and the drone planned the same leg again and again
            // (2026-11-14). Further off: from where it is.
            Vector3D planned = o.TransitStart.ToVector3D() + (from - ReferencePoint(o));
            double plannedOff = Vector3D.Distance(planned, from);
            bool havePlanned = Vector3D.DistanceSquared(planned, to) >= 1;
            if (havePlanned && plannedOff > 0.01 && plannedOff <= q.Berth)
            {
                from = planned;
                len = Vector3D.Distance(from, to);
            }
            Vector3D hit;
            VRage.ModAPI.IMyEntity hitEntity;
            bool scene = len <= LEG_CHECK_SCENE_MAX;
            // The last leg of a player route (debug orders, combat, escort, parking) was planned with a loose goal (no check
            // past its end): checked the same way, or the surface it lands on counts as "in the way" (a park on a grid
            // re-planned its 1 m leg over and over, then gave up)
            bool looseEnd = (routeOwner == RouteOwner.Player || routeLooseGoal) && nextLeg == null && queuedLegs.Count == 0;
            // A docking route's legs end at its staging point / turn point, right in front of the station (a corridor's
            // turn point can be 0.5 m off it): nothing past the end counts (the corridor / turn checks cleared that
            // spot), or the station itself blocked the leg and the drone replanned until it gave up
            if (routeOwner == RouteOwner.Docking && dockingOrder != null) looseEnd = true;
            // A scout sweep's planned stretch ends on its mapped path, close over the surface: nothing past its end counts
            if (routeOwner == RouteOwner.Sweep) looseEnd = true;
            // Checked in the leg's own attitude (the one the planner checked it with: a held attitude turned to at the
            // start of the route isn't the drone's yet when the leg starts)
            MatrixD legAtt = LegAttitude(o, ref q);
            // A job's final move in (the work order): with its tool's room (WorkBerth), as its site was chosen
            if (o == constructionOrder) q.Berth = WorkBerth();
            // The final order of a work route ends at the work point itself - a mining bore's entry is the rock: nothing
            // past its end counts and its end zone (the hull's front half plus the berth) isn't checked. It counted the
            // rock being mined as "in the way": 10 replans at the approach point, route error, the deposit skipped
            if (o == constructionOrder)
            {
                looseEnd = true;
                double keep = len - pm.EndZoneLength(ref q, ref legAtt, (to - from) / len);
                if (keep < 1) return;
                to = from + (to - from) * (keep / len);
            }
            bool blocked = scene ? pm.SegmentBlocked(ref q, ref legAtt, from, to, 0.5, out hit, out hitEntity, !looseEnd)
                                 : pm.LegRaysBlocked(ref q, ref legAtt, from, to, out hit, out hitEntity);
            // Terrain under the leg (planets are no ray / scene obstacles), its end excepted
            // Taking off from the ground (or a bore hole): the first stretch is exempt as well
            double t0 = TakingOff(from) ? Math.Min(TERRAIN_WATCH_END, len) / len : 0;
            if (!blocked && flightState.InGravity && len > TERRAIN_WATCH_END + 1 && t0 < (len - TERRAIN_WATCH_END) / len
                && PathfindingManager.TerrainTooClose(from + (to - from) * t0, from + (to - from) * ((len - TERRAIN_WATCH_END) / len), TerrainNeed(), out hit))
            {
                blocked = true;
                hitEntity = null;
                scene = false;
            }
            if (!blocked) { if (routeWaitFor != 0) EndYield("the leg is clear"); return; }
            // Further off its line than the clearance (a leg flown into at speed, round a corner): the leg as planned is
            // clear - the drone flies back onto its line (the look-ahead watches the way). Not a tight spot to turn for,
            // nor a new route (an open course: 12 turns onto another side, 22 new routes, 2026-11-14)
            if (scene && havePlanned && plannedOff > q.Berth && o != constructionOrder)
            {
                Vector3D h2;
                VRage.ModAPI.IMyEntity e2;
                if (!pm.SegmentBlocked(ref q, ref legAtt, planned, to, 0.5, out h2, out e2, !looseEnd))
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: leg start ({1:F0} m): {2:F1} m off its line, the leg as planned is clear: flying back onto it", Entity.EntityId, len, plannedOff);
                    if (routeWaitFor != 0) EndYield("the leg is clear");
                    return;
                }
            }
            // By the cells / voxels blocked, by the real physics clear (a tight spot, the work order): flown as planned
            if (scene && ExactlyClear(pm, ref q, ref legAtt, from, to, !looseEnd, o == constructionOrder, string.Format("leg start ({0:F0} m)", len)))
            {
                if (routeWaitFor != 0) EndYield("the leg is clear");
                return;
            }
            if (RouteWaitForDrone(hitEntity)) return;   // a drone in the way near the work site: held until it clears
            // A tight spot (the hull's box doesn't fit along the leg as planned): another side of the hull - turned to here,
            // in place - before a new route (PathfindingManager.LegProfile, 2026-11-14)
            if (scene && o != constructionOrder && LegOnAnotherSide(pm, o, ref q, from, to)) return;
            if (routeWaitFor != 0) EndYield("something else in the way");
            var pf = AutomataSession.GetConfig().Pathfinding;
            lastReplanFrame = MyAPIGateway.Session.GameplayFrameCounter;
            bool ok = ++routeReplans <= pf.MaxRepositionAttempts();
            if (Log.DebugEnabled)
            {
                var hb = pm.LastHitBlock;
                double turnDeg = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(legAtt.Forward, flightState.WorldMatrix.Forward), -1, 1)));
                Log.Debug("Drone {0}: leg start ({1:F0} m, {2}): {3}{6} {4:F1} m ahead (clearance {7:F2} m, the leg's attitude {8:F0}° from the drone's, {9:F1} m/s), {5}", Entity.EntityId, len, scene ? "scene" : "rays",
                          hitEntity != null ? (hitEntity.DisplayName ?? hitEntity.GetType().Name) : "obstacle", Vector3D.Distance(from, hit), ok ? "new route" : "giving up",
                          hb != null ? string.Format(" ({0} at {1})", PathfindingManager.BlockName(hb), hb.Position) : "", q.Berth, turnDeg, flightState.LinearVelocity.Length());
            }
            if (ok && Replan()) return;
            RouteStopped(Loc.F(MyStrings.Drone_RouteBlockedMAheadStopped, Vector3D.Distance(from, hit)));
        }

        /// <summary>
        /// UpdateBeforeSimulation10, AI on: while on a watched route leg, look ahead along it (as far as the drone can
        /// see, a few seconds of flight, never past the leg's end). Something in the way: a new route from here.
        /// No way round, or too many replans for this flight: stop, and the task fails (Unreachable).
        /// </summary>
        // How far the drone flies before the parts it couldn't see are planned again: half of what it sees ahead (a
        // camera along the leg: hundreds of metres; sensors only: their range)
        private double JitStep(PathfindingManager pm, FlightOrder o, Vector3D from)
        {
            var pf = AutomataSession.GetConfig().Pathfinding;
            double view = pf.SimulatedSensorRange;
            Vector3D ahead = o.Target.ToVector3D() + (from - ReferencePoint(o)) - from;
            if (ahead.LengthSquared() > 1)
            {
                PathQuery q;
                BuildPathQuery(out q);
                view = Math.Max(view, Math.Min(pm.ObservedRange(ref q, Vector3D.Normalize(ahead)), 1e5));
            }
            return Math.Max(JIT_MIN_DISTANCE, view * 0.5);
        }

        private bool memoryChained;

        // The frame the route memory records in: the order's anchor block only on a grid that can move; on a static grid
        // world positions (they don't move): a docking route's points (anchored to the connector) and a route's (world)
        // were separate frames that never merge, and the remembered points didn't connect (2026-11-13)
        private static long MemoryAnchor(long anchorId)
        {
            if (anchorId == 0) return 0;
            VRage.ModAPI.IMyEntity e;
            var b = MyAPIGateway.Entities.TryGetEntityById(anchorId, out e) ? e as IMyCubeBlock : null;
            return b != null && b.CubeGrid != null && b.CubeGrid.IsStatic ? 0 : anchorId;
        }
        private Func<Vector3D, Vector3D, bool> memoryLinkClear;

        // A link the route memory would record between two hull centres: clear for this drone's hull box in its attitude
        // now (the drone was at both ends; the cell boxes and the berth, as the memory's own check of a link it offers)
        private bool MemoryLinkClear(Vector3D a, Vector3D b)
        {
            var pm = PathfindingManager.Instance;
            if (pm == null) return false;
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;
            pm.BeginPlan();
            Vector3D hit;
            VRage.ModAPI.IMyEntity e;
            return !pm.SegmentBlocked(ref q, a, b, 0.5, out hit, out e);
        }
        private double laneSpeedCap = double.MaxValue;   // the safe following speed in a flight lane (double.MaxValue: none)
        private Vector3D lastFlightVelocity;
        private bool lastFlightVelocitySet;

        // The shared pathfinding memory and the flight lanes learn where drones fly: the hull centre every few ticks
        // while on a route. The lanes answer with the speed the drone may fly at behind the one ahead in its lane.
        private void RecordFlight10()
        {
            var pm = PathfindingManager.Instance;
            if (pm == null) return;
            var o = activeFlightOrder;
            bool flying = o != null && o.Watched && routeOwner != RouteOwner.None && preflightStage == 0 && !boring
                          && o.Phase != FlightPhase.MatchSpeed && localHullBox.Max.X >= localHullBox.Min.X;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!flying)
            {
                if (memoryChained)
                {
                    if (pm.Memory != null) pm.Memory.BreakChain(Entity.EntityId);
                    if (pm.Lanes != null) pm.Lanes.OnTripEnd(Entity.EntityId);
                    memoryChained = false;
                }
                laneSpeedCap = double.MaxValue;
                lastFlightVelocitySet = false;
                return;
            }
            Vector3D pos = HullCentreWorld;
            double hull = localHullBox.HalfExtents.Length();
            if (pm.Memory != null) pm.Memory.Record(Entity.EntityId, MemoryAnchor(o.AnchorEntityId), pos, hull, now, memoryLinkClear ?? (memoryLinkClear = MemoryLinkClear),
                                                    routeOwner == RouteOwner.Player);
            memoryChained = true;
            if (pm.Lanes == null) return;
            Vector3D vel = flightState.LinearVelocity;
            double speed = vel.Length();
            double fullA = speed > 0.5 ? UsableAccel(-vel / speed) : 1;   // the most it will brake (Max thrust setting)
            // A crash: the speed dropped far faster than the thrusters can brake
            if (lastFlightVelocitySet)
            {
                double prev = lastFlightVelocity.Length();
                double decel = (lastFlightVelocity - vel).Length() * TimeUtil.TICKS_PER_SECOND / 10.0;
                if (prev > 5 && speed < prev * 0.5 && decel > Math.Max(20, 3 * fullA))
                {
                    string lane = pm.Lanes.Crashed(Entity.EntityId);
                    if (lane != null)
                    {
                        string text = Loc.F(MyStrings.Drone_CrashedInLane, lane);
                        if (Log.DebugEnabled) Log.Debug("Drone {0}: {1} ({2:F0} -> {3:F0} m/s)", Entity.EntityId, text, prev, speed);
                        Broadcast(text);
                    }
                }
            }
            lastFlightVelocity = vel;
            lastFlightVelocitySet = true;
            laneSpeedCap = pm.Lanes.Report(Entity.EntityId, jobOrchestratorId, pos, vel, hull, fullA * TRANS_BRAKE, fullA, now);
        }

        private void UpdateRouteWatch()
        {
            if (routeWaitFor != 0) return;   // held for a drone in the way (CheckLegStart checks the leg again)
            var o = activeFlightOrder;
            if (o == null || !o.Watched || preflightStage != 0 || routeOwner == RouteOwner.None) return;
            if (parkPhase != ParkPhase.None && o == parkOrder) return;   // landing on the surface it parks on
            if (TrustedLaneLeg(o)) return;                               // in a trusted lane: flown clear before
            if (++routeWatchCounter < ROUTE_WATCH_EVERY) return;
            routeWatchCounter = 0;
            var pm = PathfindingManager.Instance;
            if (pm == null) return;
            if (droneGrids.Count == 0) RefreshDroneGrids();   // the drone's own grids are never obstacles
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastReplanFrame < ROUTE_REPLAN_MIN_TICKS) return;
            if (RecoverFlying) return;   // to a reposition spot: planned again there (UpdateRecovery10)

            Vector3D from = HullCentreWorld;
            var pf = AutomataSession.GetConfig().Pathfinding;
            // Just in time: parts of the route the drone couldn't see when it planned it are planned once it has
            // flown far enough to see them
            if (routeUnseen > 0 && !EconomyHoldsRoute())   // economy flight: not while it turns / brakes / corrects
            {
                IMyTerminalBlock anchor = o.AnchorEntityId == 0 ? null
                    : anchorBlock != null && anchorBlock.EntityId == o.AnchorEntityId ? anchorBlock : AnchorDirectory.Resolve(block, o.AnchorEntityId);
                MatrixD am = anchor != null ? anchor.WorldMatrix : MatrixD.Identity;
                Vector3D here = anchor != null ? WorldToAnchorPoint(ref am, from) : from;
                long anchorId = anchor != null ? anchor.EntityId : 0;
                if (routeJitArmed || anchorId != routeJitAnchorId)
                {
                    routeJitArmed = false;
                    routeJitAnchorId = anchorId;
                    routeJitFrom = here;
                }
                else if (routeJitForce || Vector3D.Distance(here, routeJitFrom) >= JitStep(pm, o, from))
                {
                    lastReplanFrame = now;
                    routeJitForce = false;
                    // The route as planned, checked now that more of it is in view: clear - kept. (Planned anew from
                    // here - mid-corridor, off its line - the plan swapped the lattice's way for a custom route's, then
                    // found none from the tight spot that left it in: stuck, 2026-11-14)
                    int stillUnseen;
                    if (RouteAheadClear(pm, o, from, out stillUnseen))
                    {
                        if (Log.DebugEnabled) Log.Debug("Drone {0}: the route ahead is in view now: still clear, kept ({1} stretch(es) not in view yet)", Entity.EntityId, stillUnseen);
                        routeUnseen = stillUnseen;
                        routeJitFrom = here;
                        return;
                    }
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: the route ahead is in view now, planning it", Entity.EntityId);
                    if (Replan()) return;
                    RouteStopped(Loc.T(MyStrings.Drone_NoRouteAheadStopped));
                    return;
                }
            }
            Vector3D to = o.Target.ToVector3D() + (from - ReferencePoint(o));   // the leg's end, for the hull centre
            // Within the clearance of the leg's planned line: looked along from the line (see CheckLegStart)
            {
                Vector3D ts = o.TransitStart.ToVector3D() + (from - ReferencePoint(o)), line = to - ts;
                double l2 = line.LengthSquared();
                if (l2 > 1)
                {
                    Vector3D onLine = ts + line * MathHelperD.Clamp(Vector3D.Dot(from - ts, line) / l2, 0, 1);
                    double off = Vector3D.Distance(onLine, from);
                    if (off > 0.01 && off <= BerthNow(planFactor)) from = onLine;
                }
            }
            Vector3D along = to - from;
            double remaining = along.Length();
            if (remaining < 1) return;
            double look = Math.Min(remaining, Math.Max(ROUTE_LOOKAHEAD_MIN, flightState.LinearVelocity.Length() * ROUTE_LOOKAHEAD_SECONDS));
            to = from + along / remaining * look;

            PathQuery q;
            BuildPathQuery(out q);
            // The leg's attitude (as the planner checked it); a work route's final order: not its end zone (CheckLegStart)
            MatrixD legAtt = LegAttitude(o, ref q);
            if (o == constructionOrder) q.Berth = WorkBerth();   // (as CheckLegStart)
            double rayLook = look;
            if (o == constructionOrder) rayLook = Math.Min(look, remaining - pm.EndZoneLength(ref q, ref legAtt, along / remaining));
            Vector3D hit = Vector3D.Zero;
            // Terrain ahead (planets are no ray obstacles): away from the leg's end, where work near the ground happens
            bool terrain = false;
            if (flightState.InGravity && remaining > TERRAIN_WATCH_END + 1 && !TakingOff(from))
            {
                Vector3D tEnd = from + along / remaining * Math.Min(look, remaining - TERRAIN_WATCH_END);
                terrain = PathfindingManager.TerrainTooClose(from, tEnd, TerrainNeed(), out hit);
            }
            if (!terrain && (rayLook < 1 || !pm.CheckAhead(ref q, ref legAtt, from, from + along / remaining * rayLook, out hit))) return;
            if (!terrain && ExactlyClear(pm, ref q, ref legAtt, from, from + along / remaining * rayLook, false, o == constructionOrder, "the look-ahead")) return;
            if (terrain) if (Log.DebugEnabled) Log.Debug("Drone {0}: terrain {1:F0} m ahead closer than {2:F1} m below the route", Entity.EntityId, Vector3D.Distance(from, hit), TerrainNeed());
            LaneSuspect(o);   // in a mostly safe lane: the new route checks it

            lastReplanFrame = now;
            bool ok = ++routeReplans <= pf.MaxRepositionAttempts();
            if (Log.DebugEnabled)
            {
                var hb = terrain ? null : pm.LastHitBlock;
                Log.Debug("Drone {0}: obstacle {1:F1} m ahead{3}, {2}", Entity.EntityId, Vector3D.Distance(from, hit), ok ? "new route" : "giving up",
                          hb != null ? string.Format(" ({0} at {1} of {2}, clearance {3:F2} m)", PathfindingManager.BlockName(hb), hb.Position, hb.CubeGrid.DisplayName, q.Berth) : "");
            }
            if (ok && Replan()) return;
            RouteStopped(Loc.F(MyStrings.Drone_RouteBlockedMAheadStopped, Vector3D.Distance(from, hit)));
        }

        private readonly List<FlightOrder> aheadLegs = new List<FlightOrder>();

        // CheckLegStart: the leg 'from' -> 'to' blocked in its attitude - one of the hull's profiles it fits in (the one it
        // is turned now first; another needs room to turn into it here): the leg flies in it, turning first. False: none.
        private bool LegOnAnotherSide(PathfindingManager pm, FlightOrder o, ref PathQuery q, Vector3D from, Vector3D to)
        {
            MatrixD now = flightState.WorldMatrix;
            now.Translation = Vector3D.Zero;
            MatrixD alt;
            bool tight;
            if (!pm.LegProfile(ref q, from, to, 0.5, ref now, true, out alt, out tight)) return false;
            IMyTerminalBlock anchor = null;
            if (o.AnchorEntityId != 0)
            {
                anchor = anchorBlock != null && anchorBlock.EntityId == o.AnchorEntityId ? anchorBlock : AnchorDirectory.Resolve(block, o.AnchorEntityId);
                if (anchor == null) return false;
            }
            MatrixD am = anchor != null ? anchor.WorldMatrix : MatrixD.Identity;
            SetExplicitAttitude(o, anchor == null, ref am, alt.Forward, alt.Up);
            o.TurnFirst = true;
            if (nextLeg != null) nextLeg.TurnFirst = true;   // (the next leg was planned from the old attitude: turned to before it too)
            if (Log.DebugEnabled)
            {
                double deg = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(alt.Forward, now.Forward), -1, 1)));
                Log.Debug("Drone {0}: leg start ({1:F0} m): its box doesn't fit as planned: flown on another side of the hull ({2:F0}° from how it is turned now)", Entity.EntityId, Vector3D.Distance(from, to), deg);
            }
            if (routeWaitFor != 0) EndYield("the leg is clear");
            return true;
        }

        /// <summary>
        /// The route still ahead (the leg flown from where the hull is now, then the queued legs, each in its attitude,
        /// at the route's clearance - as the planner and CheckLegStart check them): clear? Up to the docking / work /
        /// landing order (their own checks). 'notSeen': stretches still out of view.
        /// </summary>
        private bool RouteAheadClear(PathfindingManager pm, FlightOrder o, Vector3D from, out int notSeen)
        {
            notSeen = 0;
            aheadLegs.Clear();
            aheadLegs.Add(o);
            if (nextLeg != null) aheadLegs.Add(nextLeg);
            foreach (var l in queuedLegs) aheadLegs.Add(l);
            PathQuery q;
            BuildPathQuery(out q);
            bool looseAll = (routeOwner == RouteOwner.Docking && dockingOrder != null) || routeOwner == RouteOwner.Sweep;
            pm.BeginPlan();
            pm.UnseenStretches = 0;
            Vector3D a = from;
            try
            {
                for (int i = 0; i < aheadLegs.Count; i++)
                {
                    var leg = aheadLegs[i];
                    if (leg == dockingOrder || leg == constructionOrder || (parkPhase != ParkPhase.None && leg == parkOrder)) break;
                    Vector3D b = leg.Target.ToVector3D() + (from - ReferencePoint(leg));
                    if (leg.Watched && Vector3D.DistanceSquared(a, b) >= 1)
                    {
                        MatrixD att = LegAttitude(leg, ref q);
                        bool loose = looseAll || (routeOwner == RouteOwner.Player && i == aheadLegs.Count - 1);
                        Vector3D hit;
                        VRage.ModAPI.IMyEntity e;
                        // (the leg flown now: from its line where the hull is close to it - see CheckLegStart - and as
                        // planned when the hull is further off and the way from here is blocked)
                        Vector3D ts = i == 0 ? leg.TransitStart.ToVector3D() + (from - ReferencePoint(leg)) : a;
                        if (i == 0)
                        {
                            Vector3D line = b - ts;
                            double l2 = line.LengthSquared();
                            if (l2 > 1)
                            {
                                Vector3D onLine = ts + line * MathHelperD.Clamp(Vector3D.Dot(a - ts, line) / l2, 0, 1);
                                if (Vector3D.Distance(onLine, a) <= q.Berth) a = onLine;
                            }
                        }
                        if (pm.SegmentBlocked(ref q, ref att, a, b, i == 0 ? 0.5 : 0, out hit, out e, !loose)
                            && (i > 0 || Vector3D.DistanceSquared(ts, b) < 1 || pm.SegmentBlocked(ref q, ref att, ts, b, 0.5, out hit, out e, !loose)))
                        {
                            var hb = pm.LastHitBlock;
                            if (Log.DebugEnabled) Log.Debug("Drone {0}: the route ahead: leg {1} of {2} blocked by {3}{4} {5:F1} m along it", Entity.EntityId, i + 1, aheadLegs.Count,
                                                            e != null ? e.DisplayName : "?", hb != null ? string.Format(" ({0} at {1})", PathfindingManager.BlockName(hb), hb.Position) : "", Vector3D.Distance(a, hit));
                            return false;
                        }
                    }
                    a = b;
                }
                notSeen = pm.UnseenStretches;
                return true;
            }
            finally { aheadLegs.Clear(); }
        }

        /// <summary>
        /// A leg the cell / voxel checks found blocked (pm.LastHitBlock): clear by the real physics - the drone's own surface
        /// swept along it, RealSweepClear - with 'q.Berth'? Asked at a route's tightest clearance and for a job's final move
        /// in only, against static grids' blocks (voxels are up to half a block coarse: a gap the hull fits by centimetres
        /// looks shut). Every drone (2026-11-18; only harvesters checked legs this way, every leg).
        /// </summary>
        private bool ExactlyClear(PathfindingManager pm, ref PathQuery q, ref MatrixD att, Vector3D from, Vector3D to, bool ahead, bool work, string what)
        {
            if (!work && BerthNow(planFactor) > Pathfinding.Clearances.MIN + 1e-6) return false;
            var hb = pm.LastHitBlock;
            if (hb == null || hb.CubeGrid == null || !hb.CubeGrid.IsStatic) return false;   // drones, ships, rock: as they are
            if (Vector3D.DistanceSquared(from, to) < 0.01) return false;
            string by;
            if (!pm.RealSweepClear(ref q, ref att, from, to, q.Berth, ahead, out by)) return false;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: {1}: {2} in the way by its cells, clear by the real physics ({3:F2} m)", Entity.EntityId, what, PathfindingManager.BlockName(hb), q.Berth);
            return true;
        }

        /// <summary>
        /// The room round the hull on a job's final move in, by tool (one table): collectors - the beam's line,
        /// Pathfinding.Clearances.TOUCH (centimetres off a plot); welders, grinders, drills - Pathfinding.Clearances.WORK, as their site check.
        /// </summary>
        private double WorkBerth()
        {
            if (currentJob != null)
                for (int i = Math.Max(0, taskIndex - 1); i < currentJob.Tasks.Count && i <= taskIndex + 1; i++)
                    if (Orchestrator.Task.KindOf(currentJob.Tasks[i].Type) == Orchestrator.TaskKind.Collect) return Pathfinding.Clearances.TOUCH;
            return Pathfinding.Clearances.WORK;
        }

        // The attitude the planner checked a leg with: its explicit one, else the drone's own
        private MatrixD LegAttitude(FlightOrder o, ref PathQuery q)
        {
            if (o.Orientation == FlightOrientationMode.Explicit && o.Forward.ToVector3D().LengthSquared() > 0.5 && o.Up.ToVector3D().LengthSquared() > 0.5)
                return MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(o.Forward.ToVector3D()), Vector3D.Normalize(o.Up.ToVector3D()));
            return q.Controller;
        }

        #region Flights outside a job
        // No task watches a flight outside a job (Go home, auto-home, return to rest / to where it left from, player
        // routes, combat launches): pressed against something the look-ahead didn't see, it pushed and wiggled until its
        // power ran out. Now: pressed (WatchPush), it backs off and plans again, a few times; no progress for the stall
        // timeout, it plans again once; then it stops (Standby) where it is.
        private const int FREE_CONTACT_REPLANS_MAX = 3;
        private const int FREE_STALL_REPLANS_MAX = 1;
        private const int FREE_STALL_MIN_TICKS = 60 * 60;   // at least this long without progress (task timeouts can be short)
        private int freeContactReplans, freeStallReplans;
        private FlightOrder freeWatchedOrder;
        private FlightPhase freeWatchedPhase;
        private double freeBestDistance;
        private int freeProgressFrame;

        private void ResetFreeFlightWatch()
        {
            freeContactReplans = freeStallReplans = 0;
            freeWatchedOrder = null;
        }

        /// <summary>UpdateBeforeSimulation10, AI on: contact and progress watch of flights no task watches.</summary>
        private void UpdateFreeFlightWatch()
        {
            var o = activeFlightOrder;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (o == null || routeOwner == RouteOwner.None || preflightStage != 0 || isParked
                || (cPhase == ConstructionPhase.Running && currentJob != null))   // job flights: the task watches them
            {
                freeWatchedOrder = null;
                return;
            }
            // Waits that aren't stalls: the dock's final approach and lock (UpdateDocking times them), a park landing (its
            // own timeout), an escort's station keeping, holding still, a route search, arrived
            if (o == dockingOrder || dockWaitTarget != 0 || (parkPhase != ParkPhase.None && o == parkOrder) || o.FollowEntityId != 0
                || holdActive || siteYielding || boring || routePending || arrivalReported)
            {
                freeWatchedOrder = null;
                return;
            }
            Vector3D away;
            if (PushBlocked(out away))
            {
                CaptureFlightState();
                if (NearDockArea(HullCentreWorld)) return;   // next to the platform: the docking's own watch
                pushBlockedTicks = 0;
                bool again = ++freeContactReplans <= FREE_CONTACT_REPLANS_MAX;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: pressed against something on the way ({1}/{2}, no task): {3}", Entity.EntityId, freeContactReplans, FREE_CONTACT_REPLANS_MAX, again ? "backing off, new route" : "stopping");
                if (again)
                {
                    PlanContactBackOut(away, false);
                    lastReplanFrame = now;
                    if (Replan())
                    {
                        // The command wound up against the obstacle (it kept rising while the drone didn't move) stays behind:
                        // the back-out starts from the real speed, not from a push into what it touches
                        commandedVelocity = flightState.LinearVelocity;
                        freeWatchedOrder = null;
                        return;
                    }
                }
                RouteStopped(Loc.T(MyStrings.Drone_PressedAgainstSomethingOnStopped));
                return;
            }
            // Progress: 0.5 m closer to what the order flies to now (30% of it close in), the clock running across legs.
            // Not toward a threat (a scramble re-routes to where it is now, every few seconds)
            if (combatPhase == CombatPhase.Scramble) { freeWatchedOrder = null; return; }
            bool toApproachPoint = o.UseApproachLine && (o.Phase == FlightPhase.Transit || o.Phase == FlightPhase.Align);
            double d = Vector3D.Distance(ReferencePoint(o), toApproachPoint ? o.ApproachFrom.ToVector3D() : o.Target.ToVector3D());
            if (o != freeWatchedOrder)
            {
                if (freeWatchedOrder == null) freeProgressFrame = now;   // watching (again): the clock starts
                freeWatchedOrder = o;
                freeWatchedPhase = o.Phase;
                freeBestDistance = d;
                return;
            }
            if (o.Phase != freeWatchedPhase || d < freeBestDistance - Math.Min(0.5, Math.Max(0.05, freeBestDistance * 0.3)))
            {
                freeWatchedPhase = o.Phase;
                freeBestDistance = d;
                freeProgressFrame = now;
                return;
            }
            int limit = Math.Max(StallTimeoutTicks, FREE_STALL_MIN_TICKS);
            if (now - freeProgressFrame <= limit) return;
            freeProgressFrame = now;
            bool replan = ++freeStallReplans <= FREE_STALL_REPLANS_MAX;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: no progress on the way for {1} s ({2:F1} m to its target, no task): {3}", Entity.EntityId, limit / 60, d, replan ? "new route" : "stopping");
            if (replan)
            {
                lastReplanFrame = now;
                if (Replan()) { freeWatchedOrder = null; return; }
            }
            RouteStopped(Loc.F(MyStrings.Drone_NoProgressOnTheWayStopped, limit / 60));
        }
        #endregion

        // No way on: stop; the task fails (Unreachable), outside jobs the drone waits
        private void RouteStopped(string why)
        {
            Report(why);
            routeFailed = true;
            routeOwner = RouteOwner.None;
            ClearFlightOrder();
            if (cPhase != ConstructionPhase.Running) SetState(State.Standby);   // jobs: the task reports it
        }
        #endregion
    }
}
