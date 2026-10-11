using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using VRage.Utils;

namespace Automata.Drone
{
    /// <summary>
    /// The drone's side of pathfinding (server). Routes to approach points (jobs) and to docks come from the
    /// PathfindingManager; the drone flies them as legs, looks ahead along the leg it is on, and asks for a new route
    /// from where it is when something new is in the way. See PathfindingManager for the whole conversation.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrNavNoRouteToWaypoint = MyStringId.GetOrCompute("Nav: no route to waypoint {0}");
        private static readonly MyStringId StrRouteBlockedMAheadStopped = MyStringId.GetOrCompute("Route blocked {0:F0} m ahead: stopped");
        private static readonly MyStringId StrNoRouteAheadStopped = MyStringId.GetOrCompute("No route ahead: stopped");
        #endregion

        private enum RouteOwner : byte { None, FlyRoute, Docking, Player }

        #region Timing
        private const int ROUTE_WATCH_EVERY = 2;          // UpdateBeforeSimulation10 calls: a look-ahead every 20 ticks
        private const double ROUTE_LOOKAHEAD_SECONDS = 4.0;
        private const int ROUTE_REPLAN_MIN_TICKS = 60;    // at most one replan a second
        #endregion
        private const double ROUTE_LOOKAHEAD_MIN = 20.0;  // m
        private const double WORK_SITE_BERTH = 0.5;       // m kept around the hull on the way in to a work face
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
        // Just in time: stretches of the current route the drone couldn't see when it was planned, and from where
        private int routeUnseen;
        private int routePlanFrame = int.MinValue;
        // Where the drone was when the route's progress started being measured (the active order's anchor frame:
        // moving bases), set on the first watch after planning
        private bool routeJitArmed;
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

        /// <summary>The server requires cameras and / or sensors to see obstacles and the drone has none that work.</summary>
        private bool IsFlyingBlind()
        {
            var pf = AutomataSession.GetConfig().Pathfinding;
            if (!pf.RequireCamerasForPathfinding() || !pf.RequireSensorsForPathfinding()) return false;   // simulated all around
            var asleep = settings != null ? settings.SleepingBlockIds : null;   // docked: switched off until take-off
            for (int i = 0; i < cameraBlocks.Count; i++) if (PathfindingManager.InstrumentOn(cameraBlocks[i], asleep)) return false;
            for (int i = 0; i < sensors.Count; i++) if (PathfindingManager.InstrumentOn(sensors[i], asleep)) return false;
            return true;
        }

        private void ResetRoute()
        {
            routeFailed = false;
            routeReplans = 0;
            routeWatchCounter = 0;
        }

        private void BuildPathQuery(out PathQuery q)
        {
            q = new PathQuery
            {
                Controller = flightState.WorldMatrix,
                LocalHull = localHullBox,
                OwnGrids = droneGrids,
                Cameras = cameraBlocks,
                Sensors = sensors,
                GravityUp = flightState.InGravity ? flightState.GravityUp : Vector3D.Zero,
                Clearance = Math.Max(CONSTRUCTION_CLEARANCE, AutomataSession.GetConfig().Pathfinding.ObstacleClearance),
                HullCentred = true,
                SleepingIds = settings != null ? settings.SleepingBlockIds : null,
                AlignToRoute = true,
                // Align to P-Gravity: the pitch limit; otherwise the drone may point anywhere
                MaxPitch = flightState.InGravity && settings != null && settings.AlignToPGravity
                    ? MathHelperD.ToRadians(settings.PGravityAlignMaxPitchDegrees) : -1,
                // Every flight; the planner drops it for hops shorter than the altitude (work at ground level) and the
                // route takes off / lands straight up / down at the ends, so connectors and blocks on the ground are reached
                MinAltitude = settings != null ? settings.MinAltitude : 0,
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
        private bool PlanPath(Vector3D start, Vector3D goal, List<Vector3D> points, bool avoidObservationArea, bool areaOnlyFromOutside,
                              Vector3D startForward, Vector3D startUp, bool looseGoal)
        {
            int count = points.Count;
            if (PlanPathFrom(start, goal, points, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal)) return true;
            // No way from here (boxed in where it hovers: under / beside the build, between drones): reposition - a free
            // spot it can reach in a straight line - and plan from there; the move there becomes the route's first leg
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding()) return false;
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            pm.BeginPlan();
            if (!looseGoal && !pm.HullFits(ref q, goal)) return false;   // the goal itself is taken: moving won't help
            EscapeCandidates(start, ref q);
            Vector3D hit;
            VRage.ModAPI.IMyEntity e;
            int planned = 0;
            for (int i = 0; i < escapeSpots.Count && planned < ESCAPE_PLANS_MAX; i++)
            {
                Vector3D s = escapeSpots[i];
                pm.BeginPlan();
                if (!pm.HullFits(ref q, s) || pm.SegmentBlocked(ref q, start, s, 0.5, out hit, out e)) continue;
                if (q.GravityUp.LengthSquared() > 0.5
                    && PathfindingManager.HeightAboveTerrain(s) < PathfindingManager.HullHalfSizeAlong(ref q, ref q.Controller, q.GravityUp) + 1) continue;
                planned++;   // a full route from there: the expensive part, a few at most
                if (points.Count > count) points.RemoveRange(count, points.Count - count);
                if (!PlanPathFrom(s, goal, points, avoidObservationArea, areaOnlyFromOutside, startForward, startUp, looseGoal)) continue;
                points.Insert(count, s);              // first leg: there, keeping the attitude
                legOffset = count;
                legForwards.Insert(0, startForward);
                legUps.Insert(0, startUp);
                Log.Debug("Drone {0}: no way from where it is; repositioning {1:F0} m ({2}) and going on from there",
                          Entity.EntityId, Vector3D.Distance(start, s), escapeWhy[i]);
                escapeSpots.Clear();
                return true;
            }
            if (points.Count > count) points.RemoveRange(count, points.Count - count);
            escapeSpots.Clear();
            Log.Debug("Drone {0}: no way from here or from {1} spots around it", Entity.EntityId, escapeWhy.Count);
            return false;
        }

        private readonly List<Vector3D> escapeSpots = new List<Vector3D>();
        private readonly List<string> escapeWhy = new List<string>();
        private static readonly double[] ESCAPE_DISTANCES = { 4, 8 };
        private const int ESCAPE_PLANS_MAX = 6;   // routes planned from reposition spots per failed route
        private static readonly double[] DEPARTURE_ESCAPES = { 8, 16, 32 };

        // Free spots to reposition to when boxed in, in order of preference: up (away from what it works on, in gravity
        // the floor stays below), then level all round; never down in gravity
        private void EscapeCandidates(Vector3D from, ref PathQuery q)
        {
            escapeSpots.Clear();
            escapeWhy.Clear();
            bool gravity = q.GravityUp.LengthSquared() > 0.5;
            Vector3D up = gravity ? q.GravityUp : q.Controller.Up;
            Vector3D fwd = q.Controller.Forward - up * Vector3D.Dot(q.Controller.Forward, up);
            if (fwd.LengthSquared() < 1e-4) fwd = Vector3D.CalculatePerpendicularVector(up);
            fwd.Normalize();
            Vector3D right = Vector3D.Cross(fwd, up);
            // Just out of a dock: on the way the departure was heading first (out of a covered bay), then up from there
            if (departureSet && Vector3D.DistanceSquared(from, departureEnd) < 4)
                for (int k = 0; k < DEPARTURE_ESCAPES.Length; k++)
                {
                    double d = DEPARTURE_ESCAPES[k];
                    AddEscape(from + departureDir * d, "on out of the dock");
                    AddEscape(from + departureDir * d + up * (d * 0.5), "on out of the dock and up");
                }
            for (int k = 0; k < ESCAPE_DISTANCES.Length; k++)
            {
                double d = ESCAPE_DISTANCES[k];
                AddEscape(from + up * d, "up");
                AddEscape(from + (up - fwd) * (d * 0.7071), "up and back");
                AddEscape(from - fwd * d, "back");
                AddEscape(from + right * d, "right");
                AddEscape(from - right * d, "left");
                AddEscape(from + fwd * d, "ahead");
                if (!gravity) AddEscape(from - up * d, "down");
            }
        }

        private void AddEscape(Vector3D p, string why)
        {
            escapeSpots.Add(p);
            escapeWhy.Add(why);
        }

        private bool PlanPathFrom(Vector3D start, Vector3D goal, List<Vector3D> points, bool avoidObservationArea, bool areaOnlyFromOutside,
                                  Vector3D startForward, Vector3D startUp, bool looseGoal)
        {
            legOffset = points.Count;
            legForwards.Clear();
            legUps.Clear();
            routeScratch.Clear();
            if (avoidObservationArea && PlanObservationAreaDetour(start, goal, routeScratch, areaOnlyFromOutside))
                Log.Debug("Drone {0}: around the observation area ({1} waypoints)", Entity.EntityId, routeScratch.Count);
            var pm = PathfindingManager.Instance;
            bool usePathfinder = pm != null && AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding();
            PathQuery q = default(PathQuery);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (routePlanFrame != now)
            {
                // A new route (several PlanPath calls of one route happen in the same tick)
                routePlanFrame = now;
                routeUnseen = 0;
                routeJitArmed = true;
            }
            if (usePathfinder)
            {
                RefreshDroneGrids();
                BuildPathQuery(out q);
                q.LooseGoal = looseGoal;
                pm.BeginPlan();
            }
            Vector3D from = start;
            for (int i = 0; i <= routeScratch.Count; i++)
            {
                Vector3D to = i < routeScratch.Count ? routeScratch[i] : goal;
                // Observation-area waypoints are only a preference: one the hull doesn't fit at (something built or
                // parked there) is left out, the pathfinder takes the stretch to the next one
                if (usePathfinder && i < routeScratch.Count && !pm.PointFree(ref q, to))
                {
                    Log.Debug("Drone {0}: observation-area waypoint {1} skipped: {2} there", Entity.EntityId, i + 1,
                              PathfindingManager.Describe(pm.LastHitBlock));
                    continue;
                }
                if (usePathfinder && pm.PlanRoute(ref q, from, to, points) == RouteResult.Blocked)
                {
                    if (routeScratch.Count > 0)
                    {
                        // The way round the observation area is boxed in somewhere (a drone tucked in under / beside
                        // the build): straight to the goal instead, rather than hovering there for good
                        Log.Debug("Drone {0}: no way round the observation area, trying straight", Entity.EntityId);
                        routeScratch.Clear();
                        points.RemoveRange(legOffset, points.Count - legOffset);
                        from = start;
                        pm.BeginPlan();
                        i = -1;
                        continue;
                    }
                    return false;
                }
                if (i < routeScratch.Count) points.Add(to);
                from = to;
            }
            routeScratch.Clear();

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
                Log.Debug("Drone {0}: route {1:F0} m, {2} legs: {3:F2} ms, {4} scene checks, {5} blocks tested, {6} rays, {7} stretches not in view yet; "
                          + "scene: {8} area lookups, {9} block trees built ({10} blocks, {11:F2} ms of the total)",
                          Entity.EntityId, Vector3D.Distance(start, goal), routeChain.Count - 1, ms, checks, blocks, rays, notSeen,
                          lookups, trees, indexed, buildMs);
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

        /// <summary>How to pass waypoint 'at' between 'from' and 'to', and at what speed.</summary>
        private void PassWaypoint(Vector3D from, Vector3D at, Vector3D to, out WaypointBehavior pass, out float exit)
        {
            Vector3D none = Vector3D.Zero;
            PassWaypoint(from, at, to, ref none, ref none, out pass, out exit);
        }

        // Same; a change of attitude there of more than RUN_THROUGH_DEGREES (25°: turning to the next leg) means a stop
        private void PassWaypoint(Vector3D from, Vector3D at, Vector3D to, ref Vector3D fwdIn, ref Vector3D fwdOut,
                                  out WaypointBehavior pass, out float exit)
        {
            pass = PathfindingManager.Classify(at - from, to - at);
            if (fwdIn.LengthSquared() > 0.5 && fwdOut.LengthSquared() > 0.5 && Vector3D.Dot(fwdIn, fwdOut) < RUN_THROUGH_COS)
                pass = WaypointBehavior.FullStop;
            exit = pass == WaypointBehavior.RunThrough ? Math.Min(MaxSpeed, ApproachSpeed * 2f)
                 : pass == WaypointBehavior.SlowApproach ? ApproachSpeed : 0f;
        }

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
            Vector3D before = hullNow, at = hullNow, pf = flightState.WorldMatrix.Forward;
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
                    leg = QueueGoTo(point, pass, exit);
                }
                leg.ReferenceOffset = Vector3DData.FromVector3D(hc);
                ApplyLegAttitude(leg, world, ref am, f, u);
                leg.Watched = watched;
                leg.LooseArrival = true;   // the final work / dock order settles position and attitude precisely
                if (k < backOuts) MakeCorridorLeg(leg, backOutSpeedCap, backOutLineTol);   // out of a dock / hangar: precise, slow
                legsOut.Add(leg);
                before = at;
                at = point;
                pf = f;
            }
            return first;
        }

        /// <summary>
        /// Where the drone works a block face from: the tool's work sphere on 'point' (a face centre, 'dirW' its
        /// normal), the approach point out along the normal (the drone's length + 2.5 m, further out until it has
        /// room to turn there). 'checkSite': also that nothing else is in the way - the hull fits at the approach
        /// point, and the straight line in is clear of everything (other drones, ships: not only the target grid,
        /// whose cells the face choice already checks). False: pick another face.
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
                double halfAlongW = Math.Abs(Vector3D.Dot(dirW, frame.Right)) * half.X + Math.Abs(Vector3D.Dot(dirW, frame.Up)) * half.Y
                                  + Math.Abs(Vector3D.Dot(dirW, frame.Backward)) * half.Z;
                reach = MathHelperD.Clamp(WORK_STANDOFF - Vector3D.Dot(fromTool, dirW) + halfAlongW, 0, mount.WorkRadius);
            }
            sphereCentre = point + dirW * reach + shift;   // shift: precise placement (PreciseShift)
            approach = sphereCentre + dirA * ManoeuvreDistance();
            if (!pushOut) return true;
            Vector3D hullFromTool = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frame);
            bool free;
            approach = WithTurnRoom(approach, dirA, hullFromTool, ref frame, out free);
            workSiteWhy = null;
            workSiteHullPastFace = false;
            if (!checkSite) return true;
            if (!free) { workSiteWhy = "no room to turn at the approach point: " + turnRoomBlocker; return false; }
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding()) return true;
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;
            q.Berth = WORK_SITE_BERTH;
            q.StrictEnds = true;
            q.IgnoreGrid = targetGrid;   // the block worked on (its cells): the tool reaches into it
            q.IgnoreMin = targetMin;
            q.IgnoreMax = targetMax;
            MatrixD att = frame;
            Vector3D h;
            VRage.ModAPI.IMyEntity e;
            // Straight in, the hull (plus WORK_SITE_BERTH around it) from the approach point to where it works, its
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
            if (pm.SegmentBlocked(ref q, ref att, approach + hullFromTool, hullWork, 0.5, out h, out e, true, false))
            {
                workSiteHullPastFace = limited && frontGap < 0;   // the attitude, not the cell: every cell of this side fails alike
                workSiteWhy = DescribeWayInBlocked(pm.LastHitBlock, e, targetGrid, point, dirW);
                return false;
            }
            // From below (an underside): the hull must clear the ground there and on the way up
            if (flightState.InGravity && Vector3D.Dot(dirA, flightState.GravityUp) < -0.3)
            {
                Vector3D gUp = flightState.GravityUp;
                double below = PathfindingManager.HullHalfSizeAlong(ref q, ref att, gUp) + WORK_SITE_BERTH;
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
                what = string.Format("{0} at {1}", hitBlock.BlockDefinition.Id.SubtypeName, hitBlock.Min);
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
        /// Moves 'point' (a reference point: tool / connector) out along 'outward' (2.5 m steps, at most 4) until the
        /// drone has room to turn there about its hull centre ('hullFromPoint': world offset from the point to the
        /// hull centre in the final attitude). Unchanged without the pathfinder.
        /// </summary>
        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint, ref MatrixD frame)
        {
            bool free;
            return WithTurnRoom(point, outward, hullFromPoint, ref frame, out free);
        }

        private const double TURN_ROOM_MARGIN = 0.5;   // m around the hull for turning in place (route legs keep ObstacleClearance)
        private string turnRoomBlocker;                // what the last WithTurnRoom found in the way (debug log)

        // Same; 'frame': the attitude turned to there; 'free': room was found (false: the last point tried, still without room)
        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint, ref MatrixD frame, out bool free)
        {
            free = true;
            turnRoomBlocker = null;
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding() || outward.LengthSquared() < 1e-6) return point;
            outward.Normalize();
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;   // the work site / dock is known, not only what the instruments see from here
            q.Berth = TURN_ROOM_MARGIN;
            pm.BeginPlan();
            for (int k = 0; k <= 4; k++)
            {
                if (pm.TurnSweepFree(ref q, point + hullFromPoint, ref frame, TURN_ROOM_MARGIN)) return point;
                if (k < 4) point += outward * 2.5;
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
                    Report(StrNavNoRouteToWaypoint, w + 1);
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
        private bool Replan()
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
                return FlyRoute(anchor, ref am, target, approach, flyHull, flyRefOffset, ref frame, flyCheckLine)
                       == Orchestrator.TaskFailure.None;
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
                return StartDocking(target, dockReplanMount, dockReplanOffset, dockReplanForward, dockReplanUp);
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
            if (legsSinceUndock <= 2) return;                                        // undocking
            if (parkPhase != ParkPhase.None && o == parkOrder) return;               // landing: ends on the surface it parks on
            if (dockingOrder != null && (o == dockingOrder || nextLeg == dockingOrder
                || (nextLeg == null && queuedLegs.Count > 0 && queuedLegs.Peek() == dockingOrder))) return;   // docking
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding()) return;
            if (droneGrids.Count == 0) RefreshDroneGrids();
            CaptureFlightState();
            Vector3D from = HullCentreWorld;
            Vector3D to = o.Target.ToVector3D() + (from - ReferencePoint(o));
            double len = Vector3D.Distance(from, to);
            if (len < 1) return;
            PathQuery q;
            BuildPathQuery(out q);
            Vector3D hit;
            VRage.ModAPI.IMyEntity hitEntity;
            bool scene = len <= LEG_CHECK_SCENE_MAX;
            // The last leg of a player route (debug orders, combat, escort, parking) was planned with a loose goal (no check
            // past its end): checked the same way, or the surface it lands on counts as "in the way" (a park on a grid
            // re-planned its 1 m leg over and over, then gave up)
            bool looseEnd = routeOwner == RouteOwner.Player && nextLeg == null && queuedLegs.Count == 0;
            // A docking route's legs end at its staging point / turn point, right in front of the station (a corridor's
            // turn point can be 0.5 m off it): nothing past the end counts (the corridor / turn checks cleared that
            // spot), or the station itself blocked the leg and the drone replanned until it gave up
            if (routeOwner == RouteOwner.Docking && dockingOrder != null) looseEnd = true;
            bool blocked = scene ? pm.SegmentBlocked(ref q, ref q.Controller, from, to, 0.5, out hit, out hitEntity, !looseEnd)
                                 : pm.LegRaysBlocked(ref q, from, to, out hit, out hitEntity);
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
            if (!blocked) return;
            var pf = AutomataSession.GetConfig().Pathfinding;
            lastReplanFrame = MyAPIGateway.Session.GameplayFrameCounter;
            bool ok = pf.AllowRepathing() && ++routeReplans <= pf.MaxRepositionAttempts();
            Log.Debug("Drone {0}: leg start ({1:F0} m, {2}): {3} {4:F1} m ahead, {5}", Entity.EntityId, len, scene ? "scene" : "rays",
                      hitEntity != null ? (hitEntity.DisplayName ?? hitEntity.GetType().Name) : "obstacle", Vector3D.Distance(from, hit),
                      ok ? "new route" : "giving up");
            if (ok && Replan()) return;
            RouteStopped(Loc.F(StrRouteBlockedMAheadStopped, Vector3D.Distance(from, hit)));
        }

        /// <summary>
        /// UpdateBeforeSimulation10, AI on: while on a watched route leg, look ahead along it (as far as the drone can
        /// see, a few seconds of flight, never past the leg's end). Something in the way: a new route from here.
        /// No way round, or too many replans for this flight: stop, and the task fails (Unreachable).
        /// </summary>
        private void UpdateRouteWatch()
        {
            var o = activeFlightOrder;
            if (o == null || !o.Watched || preflightStage != 0 || routeOwner == RouteOwner.None) return;
            if (parkPhase != ParkPhase.None && o == parkOrder) return;   // landing on the surface it parks on
            if (++routeWatchCounter < ROUTE_WATCH_EVERY) return;
            routeWatchCounter = 0;
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding()) return;
            if (droneGrids.Count == 0) RefreshDroneGrids();   // the drone's own grids are never obstacles
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastReplanFrame < ROUTE_REPLAN_MIN_TICKS) return;

            Vector3D from = HullCentreWorld;
            var pf = AutomataSession.GetConfig().Pathfinding;
            // Just in time: parts of the route the drone couldn't see when it planned it are planned once it has
            // flown far enough to see them
            if (routeUnseen > 0 && pf.AllowRepathing())
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
                else if (Vector3D.Distance(here, routeJitFrom) >= Math.Max(JIT_MIN_DISTANCE, pf.SimulatedSensorRange * 0.5))
                {
                    lastReplanFrame = now;
                    Log.Debug("Drone {0}: the route ahead is in view now, planning it", Entity.EntityId);
                    if (Replan()) return;
                    RouteStopped(Loc.T(StrNoRouteAheadStopped));
                    return;
                }
            }
            Vector3D to = o.Target.ToVector3D() + (from - ReferencePoint(o));   // the leg's end, for the hull centre
            Vector3D along = to - from;
            double remaining = along.Length();
            if (remaining < 1) return;
            double look = Math.Min(remaining, Math.Max(ROUTE_LOOKAHEAD_MIN, flightState.LinearVelocity.Length() * ROUTE_LOOKAHEAD_SECONDS));
            to = from + along / remaining * look;

            PathQuery q;
            BuildPathQuery(out q);
            Vector3D hit = Vector3D.Zero;
            // Terrain ahead (planets are no ray obstacles): away from the leg's end, where work near the ground happens
            bool terrain = false;
            if (flightState.InGravity && remaining > TERRAIN_WATCH_END + 1 && !TakingOff(from))
            {
                Vector3D tEnd = from + along / remaining * Math.Min(look, remaining - TERRAIN_WATCH_END);
                terrain = PathfindingManager.TerrainTooClose(from, tEnd, TerrainNeed(), out hit);
            }
            if (!terrain && !pm.CheckAhead(ref q, from, to, out hit)) return;
            if (terrain) Log.Debug("Drone {0}: terrain {1:F0} m ahead closer than {2:F1} m below the route", Entity.EntityId, Vector3D.Distance(from, hit), TerrainNeed());

            lastReplanFrame = now;
            bool ok = pf.AllowRepathing() && ++routeReplans <= pf.MaxRepositionAttempts();
            Log.Debug("Drone {0}: obstacle {1:F1} m ahead, {2}", Entity.EntityId, Vector3D.Distance(from, hit), ok ? "new route" : "giving up");
            if (ok && Replan()) return;
            RouteStopped(Loc.F(StrRouteBlockedMAheadStopped, Vector3D.Distance(from, hit)));
        }

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
