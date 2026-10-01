using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// The drone's side of pathfinding (server). Routes to approach points (jobs) and to docks come from the
    /// PathfindingManager; the drone flies them as legs, looks ahead along the leg it is on, and asks for a new route
    /// from where it is when something new is in the way. See PathfindingManager for the whole conversation.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private enum RouteOwner : byte { None, FlyRoute, Docking, Player }

        private const int ROUTE_WATCH_EVERY = 2;          // UpdateBeforeSimulation10 calls: a look-ahead every 20 ticks
        private const double ROUTE_LOOKAHEAD_MIN = 20.0;  // m
        private const double ROUTE_LOOKAHEAD_SECONDS = 4.0;
        private const int ROUTE_REPLAN_MIN_TICKS = 60;    // at most one replan a second

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
                // Player flights only: jobs work at ground level (connectors, blocks)
                MinAltitude = settings != null && cPhase != ConstructionPhase.Running ? settings.MinAltitude : 0,
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
            legOffset = points.Count;
            legForwards.Clear();
            legUps.Clear();
            routeScratch.Clear();
            if (avoidObservationArea && PlanObservationAreaDetour(start, goal, routeScratch, areaOnlyFromOutside))
                Log.Debug("Drone {0}: around the observation area ({1} waypoints)", Entity.EntityId, routeScratch.Count);
            var pm = PathfindingManager.Instance;
            bool usePathfinder = pm != null && AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding();
            PathQuery q = default(PathQuery);
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
                if (usePathfinder && pm.PlanRoute(ref q, from, to, points) == RouteResult.Blocked)
                {
                    routeScratch.Clear();
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
            if (usePathfinder) pm.AssignAttitudes(ref q, routeChain, legForwards, legUps, startForward, startUp);
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

        /// <summary>How to pass waypoint 'at' between 'from' and 'to', and at what speed.</summary>
        private void PassWaypoint(Vector3D from, Vector3D at, Vector3D to, out WaypointBehavior pass, out float exit)
        {
            Vector3D none = Vector3D.Zero;
            PassWaypoint(from, at, to, ref none, ref none, out pass, out exit);
        }

        // Same; a change of attitude there of more than 15° (turning to the next leg) means a stop
        private void PassWaypoint(Vector3D from, Vector3D at, Vector3D to, ref Vector3D fwdIn, ref Vector3D fwdOut,
                                  out WaypointBehavior pass, out float exit)
        {
            pass = PathfindingManager.Classify(at - from, to - at);
            if (fwdIn.LengthSquared() > 0.5 && fwdOut.LengthSquared() > 0.5 && Vector3D.Dot(fwdIn, fwdOut) < 0.966)
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
        private bool WorkSite(ref ToolMount mount, Vector3D point, Vector3D dirW, bool block, bool checkSite, bool pushOut,
                              out MatrixD frame, out Vector3D sphereCentre, out Vector3D approach)
        {
            // Blocks: the sphere reaches FACE_DEPTH into the face (margin for the arrival tolerance), little more,
            // so it touches as little of the neighbours as the tool size allows. Other targets: TOOL_REACH_FRACTION.
            double depth = Math.Min(mount.WorkRadius * 0.9, Math.Max(FACE_DEPTH_MIN, Math.Min(FACE_DEPTH, mount.WorkRadius * 0.5)));
            double reach = block ? mount.WorkRadius - depth : mount.WorkRadius * TOOL_REACH_FRACTION;
            frame = MountFrame(ref mount, -dirW);
            sphereCentre = point + dirW * reach;
            approach = sphereCentre + dirW * ManoeuvreDistance();
            if (!pushOut) return true;
            Vector3D hullFromTool = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frame);
            bool free;
            approach = WithTurnRoom(approach, dirW, hullFromTool, out free);
            if (!checkSite) return true;
            if (!free) return false;
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding()) return true;
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;
            MatrixD att = frame;
            Vector3D h;
            VRage.ModAPI.IMyEntity e;
            // Straight in, hull centre from the approach point to where it is while working (not past: the tool is at
            // the face there)
            if (pm.SegmentBlocked(ref q, ref att, approach + hullFromTool, sphereCentre + hullFromTool, 0.5, out h, out e, false))
            {
                Log.Debug("Drone {0}: way in to the face blocked by {1}", Entity.EntityId, e != null ? e.DisplayName : "?");
                return false;
            }
            return true;
        }

        #region Turn room
        /// <summary>
        /// Moves 'point' (a reference point: tool / connector) out along 'outward' (2.5 m steps, at most 4) until the
        /// drone has room to turn there about its hull centre ('hullFromPoint': world offset from the point to the
        /// hull centre in the final attitude). Unchanged without the pathfinder.
        /// </summary>
        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint)
        {
            bool free;
            return WithTurnRoom(point, outward, hullFromPoint, out free);
        }

        // Same; 'free': room was found (false: the last point tried, still without room)
        private Vector3D WithTurnRoom(Vector3D point, Vector3D outward, Vector3D hullFromPoint, out bool free)
        {
            free = true;
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding() || outward.LengthSquared() < 1e-6) return point;
            outward.Normalize();
            RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;   // the work site / dock is known, not only what the instruments see from here
            pm.BeginPlan();
            double r = PathfindingManager.TurnRadius(ref q, HullCentreLocal);
            for (int k = 0; k <= 4; k++)
            {
                if (pm.TurnRoomFree(ref q, point + hullFromPoint, r)) return point;
                if (k < 4) point += outward * 2.5;
            }
            Log.Debug("Drone {0}: little room to turn at the approach point", Entity.EntityId);
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
            if (docked != null)
            {
                // Straight out of the dock first (known clear), as the drone is oriented
                from += docked.WorldMatrix.Forward * Math.Max(CONSTRUCTION_BACKOUT_MIN, DroneRadius() + CONSTRUCTION_CLEARANCE);
                pts.Add(from);
                isPlayer.Add(false);
                fwds.Add(sf);
                ups.Add(su);
            }
            for (int w = 0; w < waypoints.Count; w++)
            {
                int before = pts.Count;
                if (!PlanPath(from, waypoints[w], pts, false, true, sf, su, true))
                {
                    Report("Nav: no route to waypoint {0}", w + 1);
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
                leg.Watched = !(i == 0 && docked != null);   // not the back-out: the base is right beside it
                leg.PlayerWaypoint = isPlayer[i];
                if (isPlayer[i]) leg.ArrivalTolerance = settings.WaypointTolerance;
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

        /// <summary>
        /// UpdateBeforeSimulation10, AI on: while on a watched route leg, look ahead along it (as far as the drone can
        /// see, a few seconds of flight, never past the leg's end). Something in the way: a new route from here.
        /// No way round, or too many replans for this flight: stop, and the task fails (Unreachable).
        /// </summary>
        private void UpdateRouteWatch()
        {
            var o = activeFlightOrder;
            if (o == null || !o.Watched || preflightStage != 0 || routeOwner == RouteOwner.None) return;
            if (++routeWatchCounter < ROUTE_WATCH_EVERY) return;
            routeWatchCounter = 0;
            var pm = PathfindingManager.Instance;
            if (pm == null || !AutomataSession.GetConfig().Pathfinding.AllowDirectPathfinding()) return;
            if (droneGrids.Count == 0) RefreshDroneGrids();   // the drone's own grids are never obstacles
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastReplanFrame < ROUTE_REPLAN_MIN_TICKS) return;

            Vector3D from = HullCentreWorld;
            Vector3D to = o.Target.ToVector3D() + (from - ReferencePoint(o));   // the leg's end, for the hull centre
            Vector3D along = to - from;
            double remaining = along.Length();
            if (remaining < 1) return;
            double look = Math.Min(remaining, Math.Max(ROUTE_LOOKAHEAD_MIN, flightState.LinearVelocity.Length() * ROUTE_LOOKAHEAD_SECONDS));
            to = from + along / remaining * look;

            PathQuery q;
            BuildPathQuery(out q);
            Vector3D hit;
            if (!pm.CheckAhead(ref q, from, to, out hit)) return;

            lastReplanFrame = now;
            var pf = AutomataSession.GetConfig().Pathfinding;
            bool ok = pf.AllowRepathing() && ++routeReplans <= pf.MaxRepositionAttempts();
            Log.Debug("Drone {0}: obstacle {1:F1} m ahead, {2}", Entity.EntityId, Vector3D.Distance(from, hit), ok ? "new route" : "giving up");
            if (ok && Replan()) return;
            Report("Route blocked {0:F0} m ahead: stopped", Vector3D.Distance(from, hit));
            routeFailed = true;
            routeOwner = RouteOwner.None;
            ClearFlightOrder();
            if (cPhase != ConstructionPhase.Running) SetState(State.Standby);   // jobs: the task reports it
        }
        #endregion
    }
}
