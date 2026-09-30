using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Construction;
using Automata.Inventory;
using Automata.Logistics;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Jobs: an ordered task list per trip, run one task at a time (server side). Two sources:
    /// - Stand-alone: the embedded construction computer plans weld / grind trips inside the observation area
    ///   (DroneControllerBlock.Construction.cs). No coordination with other drones: two drones on the same area
    ///   may pick the same block or get in each other's way. Solving that is what the orchestrator is for.
    /// - Managed by scheduler: no own scanning. The drone bids on orchestrator auctions over the virtual network
    ///   while it is available (idle, past its fuel hysteresis, no error), runs the job it is awarded, then goes
    ///   home and waits for the next auction.
    /// Task positions are world, or relative to a beacon; never relative to the drone's home connector.
    /// Every task can fail (Orchestrator.TaskFailure); the runner then skips the block, heads home or ends the job.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private enum JobSource : byte
        {
            None,
            StandAlone,
            Orchestrator,
        }

        private const int NO_ORE_TICKS = 15 * 60;             // drilling this long without cargo growing: no ore here
        private const int AUCTION_MEMORY_TICKS = 10 * 60 * 60; // forget auctions bid on after 10 min

        private JobSource jobSource;
        private double mineCargoStart;
        private readonly Dictionary<uint, int> biddedAuctions = new Dictionary<uint, int>();
        private uint pendingBidAuctionId;          // one bid outstanding at a time: the auction it is for
        private DateTime pendingBidExpires;
        private readonly List<uint> auctionCleanup = new List<uint>();
        private readonly List<Message<Auction>> auctionInbox = new List<Message<Auction>>();
        private readonly List<Message<AuctionWinnerAnnouncement>> awardInbox = new List<Message<AuctionWinnerAnnouncement>>();
        private int bidIdCounter;
        private int jobMessageCounter;

        private static int TaskTimeoutTicks { get { return AutomataSession.GetConfig().Drone.TaskTimeoutSeconds * 60; } }

        #region Job lifecycle
        private void StartJob(Orchestrator.Job job, JobSource source)
        {
            currentJob = job;
            jobSource = source;
            taskIndex = 0;
            taskStarted = false;
            cPhase = ConstructionPhase.Running;
            Log.Debug("Drone {0}: job {1} ({2}): {3} x {4}, {5} tasks", Entity.EntityId, job.JobId, source,
                      job.BlockCount, job.JobType, job.Tasks != null ? job.Tasks.Count : 0);
            RunTasks();
        }

        private void FinishJob()
        {
            var source = jobSource;
            currentJob = null;
            constructionOrder = null;
            constructionLegs.Clear();
            cPhase = ConstructionPhase.Idle;
            jobSource = JobSource.None;
            lastConstructionScanFrame = int.MinValue / 2;   // stand-alone: next trip right away
            // Orchestrator jobs: back home to standby (the task list normally ends there already)
            if (source == JobSource.Orchestrator && !IsDockedAtHome() && HasHomeConnector && activeFlightOrder == null)
                GoHome();
        }

        /// <summary>Stops the current job (any source): tools off, no restart until the next scan / auction.</summary>
        public void StopJob()
        {
            if (cPhase == ConstructionPhase.Running)
            {
                ToolsOff();
                if (constructionOrder != null && IsConstructionOrderActive()) ClearFlightOrder();   // the job's own flight
            }
            currentJob = null;
            jobSource = JobSource.None;
            constructionOrder = null;
            constructionLegs.Clear();
            cPhase = ConstructionPhase.Idle;
            lastConstructionScanFrame = MyAPIGateway.Session.GameplayFrameCounter;   // no instant restart
        }

        // Load limit exceeded while working (grinding adds weight): straight home
        private void AbortJobToHome()
        {
            if (cPhase != ConstructionPhase.Running || currentJob == null) return;
            ToolsOff();
            int home = currentJob.IndexOf(Orchestrator.TaskType.ReturnHome, taskIndex);
            if (home < 0) { FinishJob(); return; }
            taskIndex = home;
            taskStarted = false;
            RunTasks();
        }

        public void PauseConstruction(int ticks)
        {
            constructionPausedUntil = MyAPIGateway.Session.GameplayFrameCounter + ticks;
        }
        #endregion

        #region Task runner
        // Runs the current task; tasks that finish at once (load, unload, skips) chain within the same update
        private void RunTasks()
        {
            for (int guard = 0; guard < 16 && cPhase == ConstructionPhase.Running && currentJob != null; guard++)
            {
                if (currentJob.Tasks == null || taskIndex >= currentJob.Tasks.Count) { FinishJob(); return; }
                bool starting = !taskStarted;
                if (starting)
                {
                    taskStarted = true;
                    taskStartFrame = MyAPIGateway.Session.GameplayFrameCounter;
                    taskProgressFrame = taskStartFrame;
                    taskBestDistance = double.MaxValue;
                    watchedOrder = null;
                    taskFailure = Orchestrator.TaskFailure.None;
                }
                TaskStatus status = StepTask(starting);
                if (taskFailure != Orchestrator.TaskFailure.None) ReportTaskFailure();
                switch (status)
                {
                    case TaskStatus.Running:
                        return;
                    case TaskStatus.Done:
                        NextTask(1);
                        break;
                    case TaskStatus.SkipBlock:
                        ToolsOff();
                        NextTask(2);
                        break;
                    case TaskStatus.ToReturnHome:
                        ToolsOff();
                        int home = currentJob.IndexOf(Orchestrator.TaskType.ReturnHome, taskIndex + 1);
                        if (home < 0) { FinishJob(); return; }
                        taskIndex = home;
                        taskStarted = false;
                        break;
                    case TaskStatus.Failed:
                        ToolsOff();
                        if (constructionOrder != null && IsConstructionOrderActive()) ClearFlightOrder();
                        FinishJob();
                        lastConstructionScanFrame = MyAPIGateway.Session.GameplayFrameCounter;   // wait a scan interval
                        return;
                    case TaskStatus.Interrupted:
                        // A player order, Go home, power monitoring... took over: stop, and don't restart right away
                        Report("Job: interrupted");
                        StopJob();
                        PauseConstruction(AutomataSession.GetConfig().ConstructionComputer.DroneScanIntervalSeconds * 60);
                        return;
                }
            }
        }

        private Orchestrator.TaskFailure taskFailure;

        private TaskStatus Fail(Orchestrator.TaskFailure failure, TaskStatus then)
        {
            taskFailure = failure;
            return then;
        }

        private void ReportTaskFailure()
        {
            var task = currentJob.Tasks[taskIndex];
            Report("Task {0}/{1} {2} failed: {3}", taskIndex + 1, currentJob.Tasks.Count, task.Type, taskFailure);
            taskFailure = Orchestrator.TaskFailure.None;
        }

        private void NextTask(int count)
        {
            taskIndex += count;
            taskStarted = false;
        }

        private TaskStatus StepTask(bool starting)
        {
            var task = currentJob.Tasks[taskIndex];
            int elapsed = MyAPIGateway.Session.GameplayFrameCounter - taskStartFrame;
            switch (task.Type)
            {
                case Orchestrator.TaskType.LoadInventory:
                {
                    var source = DockedConnector();   // owner / faction checked
                    if (source == null) return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.Failed);
                    if (task.Inventory == null || task.Inventory.IsEmpty()) return TaskStatus.Done;
                    BuildHomeNetwork(source);   // the network behind whatever connector the drone is docked at
                    int planned = task.Inventory.GetTotalItemCount();
                    int moved = TransferToDrone(task.Inventory);
                    if (moved < planned)
                    {
                        // Materials gone meanwhile, no conveyor path, or cargo refused: don't fly a trip that can't be finished
                        UnloadCargo(null);
                        ReportProblem("Job: could load only " + moved + "/" + planned + " parts, check conveyors");
                        return Fail(Orchestrator.TaskFailure.InsufficientMaterials, TaskStatus.Failed);
                    }
                    lastConstructionProblem = null;
                    Report("Job: {0} blocks, loaded {1} parts", currentJob.BlockCount, moved);
                    return TaskStatus.Done;
                }

                case Orchestrator.TaskType.UnloadInventory:
                {
                    var target = DockedConnector();
                    if (target == null) return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.Failed);
                    BuildHomeNetwork(target);
                    if (!UnloadCargo(task.Inventory)) return Fail(Orchestrator.TaskFailure.NoCargoSpace, TaskStatus.Failed);
                    return TaskStatus.Done;
                }

                case Orchestrator.TaskType.NavigateToolTo:
                {
                    if (taskIndex + 1 >= currentJob.Tasks.Count || currentJob.Tasks[taskIndex + 1].Kind != Orchestrator.TaskKind.Tool)
                        return TaskStatus.Done;   // nothing to put a tool on
                    var tool = currentJob.Tasks[taskIndex + 1];
                    if (starting)
                    {
                        // Done meanwhile (another drone, by hand, a neighbour's weld): nothing to fly to
                        var already = ToolTargetState(ref tool);
                        if (already != Orchestrator.TaskFailure.None) return Fail(already, TaskStatus.SkipBlock);
                        var failure = DispatchToolTo(ref task, ref tool);
                        if (failure == Orchestrator.TaskFailure.MissingEquipment) return Fail(failure, TaskStatus.ToReturnHome);
                        return failure == Orchestrator.TaskFailure.None ? TaskStatus.Running : Fail(failure, TaskStatus.SkipBlock);
                    }
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    if (constructionOrder.Arrived) return TaskStatus.Done;
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.SkipBlock);
                }

                case Orchestrator.TaskType.NavigateTo:
                {
                    if (starting)
                    {
                        var failure = DispatchNavigateTo(ref task);
                        return failure == Orchestrator.TaskFailure.None ? TaskStatus.Running : Fail(failure, TaskStatus.ToReturnHome);
                    }
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    if (constructionOrder.Arrived) return TaskStatus.Done;
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.ToReturnHome);
                }

                case Orchestrator.TaskType.WeldBlock:
                case Orchestrator.TaskType.GrindBlock:
                case Orchestrator.TaskType.Mine:
                {
                    if (starting)
                    {
                        var already = ToolTargetState(ref task);
                        if (already != Orchestrator.TaskFailure.None) return Fail(already, TaskStatus.Done);
                        if (!HasTool(task.Type)) return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                        mineCargoStart = CargoUsedVolume();
                        SetToolEnabled(task.Type, true);
                        return TaskStatus.Running;
                    }
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    if (IsToolTaskDone(ref task, elapsed))
                    {
                        SetToolEnabled(task.Type, false);   // one block at a time: off before moving on
                        return TaskStatus.Done;
                    }
                    if (task.Type == Orchestrator.TaskType.Mine && elapsed > NO_ORE_TICKS && CargoUsedVolume() - mineCargoStart < 0.001)
                    {
                        SetToolEnabled(task.Type, false);
                        return Fail(Orchestrator.TaskFailure.NoOre, TaskStatus.ToReturnHome);
                    }
                    if (elapsed > TaskTimeoutTicks)
                    {
                        SetToolEnabled(task.Type, false);
                        return Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
                    }
                    return TaskStatus.Running;
                }

                case Orchestrator.TaskType.DockAt:
                {
                    if (starting)
                    {
                        var target = task.TargetEntityId != 0 ? AnchorDirectory.Resolve(block, task.TargetEntityId) as IMyShipConnector : null;
                        if (target == null) return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.ToReturnHome);
                        if (IsDockedAt(target.EntityId)) return TaskStatus.Done;
                        ToolsOff();
                        if (!DockAtConnector(target)) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
                        constructionOrder = activeFlightOrder;
                        constructionLegs.Clear();
                        return TaskStatus.Running;
                    }
                    if (IsDockedAt(task.TargetEntityId)) return TaskStatus.Done;
                    if (dockingHomeId == 0 && activeFlightOrder == null) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
                    if (dockingHomeId != task.TargetEntityId) return TaskStatus.Interrupted;   // Go home, a player order...
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.ToReturnHome);
                }

                case Orchestrator.TaskType.ReturnHome:
                {
                    if (starting)
                    {
                        ToolsOff();
                        if (IsDockedAtHome()) return TaskStatus.Done;
                        if (!GoHome()) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Failed);
                        constructionOrder = activeFlightOrder;
                        constructionLegs.Clear();
                        return TaskStatus.Running;
                    }
                    if (IsDockedAtHome()) return TaskStatus.Done;
                    if (dockingHomeId == 0 && activeFlightOrder == null) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Failed);
                    if (!IsGoingHome) return TaskStatus.Interrupted;                                // someone else's order
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Failed);
                }

                default:
                    // DeliverMessage: not implemented yet
                    Log.Debug("Drone {0}: task {1} not supported yet, skipped", Entity.EntityId, task.Type);
                    return TaskStatus.Done;
            }
        }

        private bool IsConstructionOrderActive()
        {
            return activeFlightOrder != null
                && (activeFlightOrder == constructionOrder || constructionLegs.Contains(activeFlightOrder));
        }

        /// <summary>
        /// Progress watchdog for the current leg: a detour leg that hovers close to its waypoint without settling is
        /// handed over; no progress for the task timeout means stuck (false).
        /// </summary>
        private bool WatchProgress()
        {
            var o = activeFlightOrder;
            if (o == null) return true;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (o != watchedOrder)
            {
                // New leg: its own distance to measure progress against
                watchedOrder = o;
                taskBestDistance = double.MaxValue;
                taskProgressFrame = now;
            }
            double d = Vector3D.Distance(ReferencePoint(o), o.Target.ToVector3D());
            if (d < taskBestDistance - 0.5)
            {
                taskBestDistance = d;
                taskProgressFrame = now;
                return true;
            }
            int stalled = now - taskProgressFrame;
            if (constructionLegs.Contains(o) && d < CONSTRUCTION_LEG_CLOSE && stalled > CONSTRUCTION_LEG_SETTLE_TICKS)
            {
                Log.Debug("Drone {0}: detour leg settled {1:F1} m off, moving on", Entity.EntityId, d);
                ForceLegHandover();
                taskBestDistance = double.MaxValue;
                taskProgressFrame = now;
                return true;
            }
            return stalled <= TaskTimeoutTicks;
        }
        #endregion

        #region Navigation
        /// <summary>
        /// Task positions: world, or in a beacon's frame (Right, Up, Forward). The beacon must be the drone owner's
        /// or their faction's (AnchorDirectory). 'anchor' is null for world positions.
        /// </summary>
        private bool ResolveTaskFrame(long beaconId, out IMyTerminalBlock anchor, out MatrixD frame)
        {
            anchor = null;
            frame = MatrixD.Identity;
            if (beaconId == 0) return true;
            anchor = AnchorDirectory.Resolve(block, beaconId);
            if (anchor == null) return false;
            frame = anchor.WorldMatrix;
            return true;
        }

        private static Vector3D TaskToWorld(IMyTerminalBlock anchor, ref MatrixD frame, Vector3DData p)
        {
            return anchor == null ? p.ToVector3D() : ConstructionComputer.FromFrame(ref frame, p.ToVector3D());
        }

        private static Vector3DData WorldToTask(IMyTerminalBlock anchor, ref MatrixD frame, Vector3D w)
        {
            return Vector3DData.FromVector3D(anchor == null ? w : ConstructionComputer.ToFrame(ref frame, w));
        }

        // The tool's work-sphere centre is positioned (ReferenceOffset) straight out from the target point along
        // the approach line, with the point just inside the far edge of the sphere. Blocks: the point is the centre
        // of one of the block's cell faces and the line its normal - the planned face, unless a better one is free
        // now (neighbours built since, the side the drone is on, attitude).
        private const double FACE_DEPTH = 0.5;       // m: how far the tool's work sphere reaches past a block face...
        private const double FACE_DEPTH_MIN = 0.4;   // ...at least (arrival tolerance 0.3 m + margin), small tools too

        private Orchestrator.TaskFailure DispatchToolTo(ref Orchestrator.Task nav, ref Orchestrator.Task tool)
        {
            IMyTerminalBlock anchor;
            MatrixD am;
            if (!ResolveTaskFrame(nav.RelativeBeaconEntityId, out anchor, out am)) return Orchestrator.TaskFailure.TargetNotFound;
            if (!HasTool(tool.Type)) return Orchestrator.TaskFailure.MissingEquipment;
            ToolMount mount = tool.Type == Orchestrator.TaskType.GrindBlock ? grinderMount
                            : tool.Type == Orchestrator.TaskType.Mine ? drillMount : welderMount;
            CaptureFlightState();
            RefreshDroneGrids();
            Vector3D point = TaskToWorld(anchor, ref am, nav.Position);
            Vector3D dirW = TaskToWorld(anchor, ref am, nav.ApproachFrom) - point;
            if (dirW.LengthSquared() > 1e-4) dirW.Normalize();
            double hull = DroneRadius();

            IMyEntity e;
            var grid = tool.Type != Orchestrator.TaskType.Mine && tool.TargetEntityId != 0
                       && MyAPIGateway.Entities.TryGetEntityById(tool.TargetEntityId, out e) ? e as IMyCubeGrid : null;
            if (grid != null)
            {
                Vector3I faceCell, normal, min, max;
                if (ConstructionComputer.TryGetBlockExtent(grid, tool.TargetBlock.ToVector3I(), out min, out max))
                {
                    // Built, started or still projected: its extent now. The drone needs the tool reach plus its own
                    // size clear along the normal; the planned side and the side the drone is on are preferred.
                    MatrixD areaMatrix;
                    Vector3D half;
                    Vector3D areaCenter = jobSource == JobSource.StandAlone && TryGetObservationArea(out areaMatrix, out half)
                        ? areaMatrix.Translation : grid.WorldVolume.Center;
                    int clearCells = (int)Math.Ceiling((mount.WorkRadius + 2 * hull) / grid.GridSize);
                    Vector3D toDrone = flightState.Position - point;
                    if (toDrone.LengthSquared() > 1e-4) toDrone.Normalize();
                    Vector3D planned = dirW;
                    if (!ConstructionComputer.ChooseFace(grid, min, max, areaCenter,
                            flightState.InGravity ? flightState.GravityUp : Vector3D.Zero, clearCells,
                            w => AttitudeCost(ref mount, -w) + 0.25 * (1 - Vector3D.Dot(w, toDrone)) - (Vector3D.Dot(w, planned) > 0.9 ? 0.5 : 0),
                            out faceCell, out normal))
                        return Orchestrator.TaskFailure.Unreachable;   // walled in
                }
                else
                {
                    // Projection gone (projector off...): the planned face, if its outside cell is still free
                    if (!ConstructionComputer.FaceOf(grid, point, dirW, out faceCell, out normal) || grid.CubeExists(faceCell + normal))
                        return Orchestrator.TaskFailure.Unreachable;
                }
                point = ConstructionComputer.FacePoint(grid, faceCell, normal, out dirW);
            }
            if (dirW.LengthSquared() < 1e-4) dirW = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Backward;
            dirW.Normalize();

            // Blocks: the sphere reaches FACE_DEPTH into the face (margin for the arrival tolerance), little more,
            // so it touches as little of the neighbours as the tool size allows. Other targets: TOOL_REACH_FRACTION.
            double depth = Math.Min(mount.WorkRadius * 0.9, Math.Max(FACE_DEPTH_MIN, Math.Min(FACE_DEPTH, mount.WorkRadius * 0.5)));
            double reach = grid != null ? mount.WorkRadius - depth : mount.WorkRadius * TOOL_REACH_FRACTION;
            MatrixD frame = MountFrame(ref mount, -dirW);
            Vector3D sphereCentre = point + dirW * reach;
            Vector3D approach = sphereCentre + dirW * ConstructionComputer.APPROACH_DISTANCE;
            nav.Position = WorldToTask(anchor, ref am, point);   // the face actually used
            nav.ApproachFrom = WorldToTask(anchor, ref am, point + dirW * ConstructionComputer.APPROACH_DISTANCE);
            currentJob.Tasks[taskIndex] = nav;

            var failure = FlyRoute(anchor, ref am, sphereCentre, approach, hull, mount.LocalPoint, ref frame);
            if (failure == Orchestrator.TaskFailure.None)
                Log.Debug("Drone {0}: {1} at {2}, face point {3}, normal {4}", Entity.EntityId, tool.Type, tool.TargetBlock.ToVector3I(), point, dirW);
            return failure;
        }

        // Plain NavigateTo: the controller to Position, in along ApproachFrom -> Position, level
        private Orchestrator.TaskFailure DispatchNavigateTo(ref Orchestrator.Task nav)
        {
            IMyTerminalBlock anchor;
            MatrixD am;
            if (!ResolveTaskFrame(nav.RelativeBeaconEntityId, out anchor, out am)) return Orchestrator.TaskFailure.TargetNotFound;
            CaptureFlightState();
            RefreshDroneGrids();
            Vector3D target = TaskToWorld(anchor, ref am, nav.Position);
            Vector3D approach = TaskToWorld(anchor, ref am, nav.ApproachFrom);
            if (Vector3D.DistanceSquared(target, approach) < 0.01) approach = flightState.Position;
            Vector3D heading = target - approach;
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            heading -= gUp * Vector3D.Dot(heading, gUp);
            if (heading.LengthSquared() < 1e-4) heading = flightState.WorldMatrix.Forward;
            MatrixD frame = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(heading), gUp);
            return FlyRoute(anchor, ref am, target, approach, DroneRadius(), Vector3D.Zero, ref frame);
        }

        /// <summary>
        /// Orders the flight to 'target' (the point 'referenceOffset' is placed on), in along approach -> target with
        /// the attitude of 'frame'. Backs out of the dock first, and detours over obstacles (PlanRoute).
        /// </summary>
        private Orchestrator.TaskFailure FlyRoute(IMyTerminalBlock anchor, ref MatrixD am, Vector3D target, Vector3D approach,
                                                  double hull, Vector3D referenceOffset, ref MatrixD frame)
        {
            bool world = anchor == null;
            constructionLegs.Clear();
            routePoints.Clear();
            Vector3D start = flightState.Position;
            var docked = DockedConnector();
            if (docked != null)
            {
                // Back out along the connector axis first: the way the drone came in is known to be clear
                start += docked.WorldMatrix.Forward * Math.Max(CONSTRUCTION_BACKOUT_MIN, hull + CONSTRUCTION_CLEARANCE);
                routePoints.Add(start);
            }
            Vector3D over1, over2;
            RouteKind route = PlanRoute(start, approach, hull, out over1, out over2);
            if (route == RouteKind.Blocked) return Orchestrator.TaskFailure.Unreachable;
            if (route == RouteKind.Detour)
            {
                // Up and over whatever is in the way, down onto the approach point, then in along the line
                routePoints.Add(over1);
                routePoints.Add(over2);
                Log.Debug("Drone {0}: detour, climbing {1:F1} m", Entity.EntityId, Vector3D.Distance(over1, start));
            }
            FlightOrder order;
            if (routePoints.Count > 0)
            {
                var first = world ? OrderGoTo(routePoints[0]) : OrderGoToRelative(anchor, WorldToAnchorPoint(ref am, routePoints[0]));
                if (first == null) return Orchestrator.TaskFailure.TargetNotFound;
                constructionLegs.Add(first);
                for (int i = 1; i < routePoints.Count; i++)
                    constructionLegs.Add(QueueGoTo(routePoints[i], Pathfinding.WaypointBehavior.FullStop, 0));
                order = QueueGoTo(target, Pathfinding.WaypointBehavior.FullStop, 0);
                order.UseApproachLine = true;
                order.Phase = FlightPhase.Transit;
                if (world) order.ApproachFrom = Vector3DData.FromVector3D(approach);
                else order.ApproachFromLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref am, approach));
                routePoints.Clear();
                // Route legs: no turning on the way - the drone translates with its current attitude
                for (int i = 0; i < constructionLegs.Count; i++)
                {
                    var leg = constructionLegs[i];
                    leg.ArrivalTolerance = CONSTRUCTION_ROUTE_TOLERANCE;
                    SetExplicitAttitude(leg, world, ref am, flightState.WorldMatrix.Forward, flightState.WorldMatrix.Up);
                }
            }
            else
            {
                order = world ? OrderGoTo(target, approach)
                              : OrderGoToRelative(anchor, WorldToAnchorPoint(ref am, target), WorldToAnchorPoint(ref am, approach));
                if (order == null) return Orchestrator.TaskFailure.TargetNotFound;
            }
            ToolsOff();   // after the order: waking from the dock restores blocks, tools included
            constructionOrder = order;
            order.ReferenceOffset = Vector3DData.FromVector3D(referenceOffset);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = ApproachSpeed;
            order.ArrivalTolerance = 0.3f;
            if (constructionLegs.Count == 0)
            {
                Vector3D here = ReferencePoint(order);
                if (world) order.TransitStart = Vector3DData.FromVector3D(here);
                else order.TransitStartLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref am, here));
            }
            SetExplicitAttitude(order, world, ref am, frame.Forward, frame.Up);
            return Orchestrator.TaskFailure.None;
        }

        // Fixed attitude for an order: in the anchor's frame (follows a moving grid), or world
        private void SetExplicitAttitude(FlightOrder o, bool world, ref MatrixD am, Vector3D forward, Vector3D up)
        {
            o.Orientation = FlightOrientationMode.Explicit;
            o.IgnoreGravityLimits = true;
            if (world)
            {
                o.Forward = Vector3DData.FromVector3D(forward);
                o.Up = Vector3DData.FromVector3D(up);
                return;
            }
            o.ForwardLocal = Vector3DData.FromVector3D(WorldToAnchorDir(ref am, forward));
            o.UpLocal = Vector3DData.FromVector3D(WorldToAnchorDir(ref am, up));
            RefreshAnchoredOrder(o);
        }

        private enum RouteKind : byte { Direct, Detour, Blocked }

        /// <summary>
        /// Straight to the approach point when nothing solid is in the way (raycasts along the hull's outline).
        /// Otherwise up (gravity up, or away from the target in space), across at a height that clears whatever
        /// was hit, and down onto the approach point. Blocked when the climb or the descent itself is obstructed,
        /// or no height clears the way within a few raises: the task fails rather than fly through something.
        /// </summary>
        private RouteKind PlanRoute(Vector3D start, Vector3D approach, double hull, out Vector3D over1, out Vector3D over2)
        {
            over1 = over2 = Vector3D.Zero;
            double top;
            Vector3D up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            if (!SegmentObstacleTop(start, approach, up, hull, hull, out top)) return RouteKind.Direct;

            if (!flightState.InGravity)
            {
                up = start - approach;
                if (up.LengthSquared() < 1) up = flightState.WorldMatrix.Up;
                up.Normalize();
            }
            double clear = hull + CONSTRUCTION_CLEARANCE;
            double height = Math.Max(Vector3D.Dot(start, up), Vector3D.Dot(approach, up)) + clear;
            for (int i = 0; i < CONSTRUCTION_DETOUR_RAISES; i++)
            {
                over1 = start + up * (height - Vector3D.Dot(start, up));
                over2 = approach + up * (height - Vector3D.Dot(approach, up));
                if (SegmentObstacleTop(start, over1, up, hull, hull, out top)) return RouteKind.Blocked;      // overhang above
                if (SegmentObstacleTop(approach, over2, up, hull, 0, out top)) return RouteKind.Blocked;      // something above the approach point
                if (!SegmentObstacleTop(over1, over2, up, hull, 0, out top)) return RouteKind.Detour;
                height = Math.Max(height + 1, top + clear);
            }
            return RouteKind.Blocked;
        }

        /// <summary>
        /// Anything solid that isn't the drone (or a character) between a and b, on the centre line and the hull's
        /// four sides. Hits closer than 'skipStart' to a are ignored (where the drone already is). 'top' is the
        /// highest hit along 'up', plus a cell for grids (the hit is on a face).
        /// </summary>
        private bool SegmentObstacleTop(Vector3D a, Vector3D b, Vector3D up, double hull, double skipStart, out double top)
        {
            top = double.MinValue;
            Vector3D along = b - a;
            if (along.LengthSquared() < 0.01) return false;
            Vector3D dir = Vector3D.Normalize(along);
            Vector3D side = Vector3D.CalculatePerpendicularVector(dir);
            Vector3D side2 = Vector3D.Cross(dir, side);
            bool hit = false;
            int rays = hull > 0 ? 5 : 1;
            for (int r = 0; r < rays; r++)
            {
                Vector3D o = r == 0 ? Vector3D.Zero : (r == 1 ? side : r == 2 ? -side : r == 3 ? side2 : -side2) * hull;
                rayHits.Clear();
                MyAPIGateway.Physics.CastRay(a + o, b + o, rayHits);
                for (int i = 0; i < rayHits.Count; i++)
                {
                    var e = rayHits[i].HitEntity;
                    if (e == null) continue;
                    var topMost = e.GetTopMostParent();
                    var g = topMost as IMyCubeGrid;
                    if (g != null && droneGrids.Contains(g)) continue;
                    if (topMost is IMyCharacter) continue;
                    Vector3D p = rayHits[i].Position;
                    if (Vector3D.Dot(p - (a + o), dir) < skipStart) continue;
                    hit = true;
                    double h = Vector3D.Dot(p, up) + (g != null ? g.GridSize : 0);
                    if (h > top) top = h;
                }
            }
            rayHits.Clear();
            return hit;
        }

        private double DroneRadius()
        {
            double r = 0;
            for (int i = 0; i < droneGrids.Count; i++)
                r = Math.Max(r, Vector3D.Distance(droneGrids[i].WorldVolume.Center, flightState.Position) + droneGrids[i].WorldVolume.Radius);
            return r > 0 ? r : block.CubeGrid.WorldVolume.Radius;
        }

        // Tilt (radians, in gravity) the drone needs so the tool faces 'toolForward'; large tilts are worth avoiding
        private double AttitudeCost(ref ToolMount mount, Vector3D toolForward)
        {
            if (!flightState.InGravity) return 0;
            MatrixD frame = MountFrame(ref mount, toolForward);
            double tilt = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(frame.Up, flightState.GravityUp), -1, 1));
            return tilt / MathHelperD.PiOver2;   // 90° costs as much as one "outward" unit
        }
        #endregion

        #region Tools and targets
        private bool HasTool(Orchestrator.TaskType type)
        {
            switch (type)
            {
                case Orchestrator.TaskType.GrindBlock: return grinder != null && grinderMount.WorkRadius > 0;
                case Orchestrator.TaskType.Mine:       return drill != null && drillMount.WorkRadius > 0;
                default:                               return welder != null && welderMount.WorkRadius > 0;
            }
        }

        private bool HasToolsFor(List<ConstructionTarget> targets)
        {
            bool needWelder = false, needGrinder = false;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Type == Orchestrator.JobType.Grind) needGrinder = true;
                else needWelder = true;
            }
            if (needWelder && !HasTool(Orchestrator.TaskType.WeldBlock)) { ReportProblem("Construction: needs a welder"); return false; }
            if (needGrinder && !HasTool(Orchestrator.TaskType.GrindBlock)) { ReportProblem("Construction: needs a grinder"); return false; }
            return true;
        }

        private void ToolsOff()
        {
            if (welder != null && welder.Enabled) welder.Enabled = false;
            if (grinder != null && grinder.Enabled) grinder.Enabled = false;
            if (drill != null && drill.Enabled) drill.Enabled = false;
        }

        // Same problem again (every scan) is reported once
        private void ReportProblem(string text)
        {
            if (text == lastConstructionProblem) return;
            lastConstructionProblem = text;
            Report(text);
        }

        private void SetToolEnabled(Orchestrator.TaskType type, bool on)
        {
            var tool = type == Orchestrator.TaskType.GrindBlock ? (IMyFunctionalBlock)grinder
                     : type == Orchestrator.TaskType.Mine ? (IMyFunctionalBlock)drill : welder;
            if (tool != null && tool.Enabled != on) tool.Enabled = on;
        }

        private IMySlimBlock ToolTarget(ref Orchestrator.Task task, out bool gridGone)
        {
            IMyEntity e;
            var grid = task.TargetEntityId != 0 && MyAPIGateway.Entities.TryGetEntityById(task.TargetEntityId, out e) ? e as IMyCubeGrid : null;
            gridGone = grid == null;
            return grid != null ? grid.GetCubeBlock(task.TargetBlock.ToVector3I()) : null;
        }

        /// <summary>
        /// Before working a block: is there still something to do? Weld: AlreadyFullIntegrity when welded to full,
        /// TargetRemoved when a built (not projected) block is gone. Grind: TargetRemoved when already dismounted.
        /// </summary>
        private Orchestrator.TaskFailure ToolTargetState(ref Orchestrator.Task task)
        {
            if (task.Type == Orchestrator.TaskType.Mine || task.TargetEntityId == 0) return Orchestrator.TaskFailure.None;
            bool gridGone;
            var slim = ToolTarget(ref task, out gridGone);
            if (!gridGone && !IsGridAllowed(slim != null ? slim.CubeGrid : null, task.TargetEntityId))
                return Orchestrator.TaskFailure.TargetNotFound;   // not ours / our faction's: never weld or grind it
            if (task.Type == Orchestrator.TaskType.GrindBlock)
                return slim == null ? Orchestrator.TaskFailure.TargetRemoved : Orchestrator.TaskFailure.None;
            if (slim == null)
                return gridGone || !task.IsProjected ? Orchestrator.TaskFailure.TargetRemoved : Orchestrator.TaskFailure.None;
            return slim.IsFullIntegrity && !slim.HasDeformation ? Orchestrator.TaskFailure.AlreadyFullIntegrity : Orchestrator.TaskFailure.None;
        }

        // The tool task's completion trigger. 'elapsed': ticks since the task started (Timespan trigger).
        // On a block, the inventory triggers also end when the block itself is done: a partial weld stops at full
        // integrity or when the cargo holds nothing more the block needs; a partial grind when the block is gone.
        private bool IsToolTaskDone(ref Orchestrator.Task task, int elapsed)
        {
            bool onBlock = task.TargetEntityId != 0 && task.Type != Orchestrator.TaskType.Mine;
            switch (task.Completion)
            {
                case Orchestrator.ToolTaskCompletionTrigger.Timespan:
                    return elapsed >= task.CompletionValue * 60;
                case Orchestrator.ToolTaskCompletionTrigger.DroneInventoryFull:
                    if (CargoFillRatio() >= 0.95) return true;
                    if (!onBlock) return false;
                    break;
                case Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty:
                    if (CargoFillRatio() <= 0.001) return true;
                    if (!onBlock) return false;
                    break;
            }
            bool gridGone;
            var slim = ToolTarget(ref task, out gridGone);
            if (gridGone) return true;
            if (task.Completion == Orchestrator.ToolTaskCompletionTrigger.DroneInventoryFull)
                return slim == null || slim.IsFullyDismounted;
            if (task.Completion == Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty)
            {
                if (slim == null) return !task.IsProjected;   // gone meanwhile; projections: not built yet
                if (slim.IsFullIntegrity && !slim.HasDeformation) return true;
                return !CargoHasAnyOf(slim);                  // nothing more this drone can add to it
            }
            switch (task.Completion)
            {
                case Orchestrator.ToolTaskCompletionTrigger.BlockFullyDismounted:
                    return slim == null || slim.IsFullyDismounted;
                case Orchestrator.ToolTaskCompletionTrigger.BlockIntegrityPercent:
                    return slim != null && slim.MaxIntegrity > 0 && slim.Integrity / slim.MaxIntegrity * 100f >= task.CompletionValue;
                default:   // BlockFullIntegrity
                    if (slim == null) return !task.IsProjected;   // gone meanwhile; projections: not built yet
                    return slim.IsFullIntegrity && !slim.HasDeformation;
            }
        }

        // Grids the drone may work on: unowned, or owned (majority) by the drone owner / their faction
        private bool IsGridAllowed(IMyCubeGrid grid, long gridId)
        {
            if (grid == null)
            {
                IMyEntity e;
                grid = MyAPIGateway.Entities.TryGetEntityById(gridId, out e) ? e as IMyCubeGrid : null;
                if (grid == null) return false;
            }
            var owners = grid.BigOwners;
            if (owners == null || owners.Count == 0) return true;
            for (int i = 0; i < owners.Count; i++)
                if (AnchorDirectory.IsAllowed(block.OwnerId, owners[i])) return true;
            return false;
        }

        private readonly Dictionary<string, int> missingBuffer = new Dictionary<string, int>();

        // Does the drone's cargo hold any component the block still misses?
        private bool CargoHasAnyOf(IMySlimBlock slim)
        {
            missingBuffer.Clear();
            slim.GetMissingComponents(missingBuffer);
            if (missingBuffer.Count == 0) return false;
            for (int c = 0; c < cargoContainers.Count; c++)
            {
                var inv = cargoContainers[c].GetInventory(0);
                if (inv == null) continue;
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = 0; i < itemBuffer.Count; i++)
                    if (itemBuffer[i].Type.TypeId == COMPONENT_TYPE && missingBuffer.ContainsKey(itemBuffer[i].Type.SubtypeId))
                    {
                        itemBuffer.Clear();
                        return true;
                    }
            }
            itemBuffer.Clear();
            return false;
        }

        private double CargoUsedVolume()
        {
            double used = 0;
            for (int i = 0; i < cargoContainers.Count; i++)
            {
                var inv = cargoContainers[i].GetInventory(0);
                if (inv != null) used += (double)inv.CurrentVolume;
            }
            return used;
        }

        private double CargoFillRatio()
        {
            double used = 0, max = 0;
            for (int i = 0; i < cargoContainers.Count; i++)
            {
                var inv = cargoContainers[i].GetInventory(0);
                if (inv == null) continue;
                used += (double)inv.CurrentVolume;
                max += (double)inv.MaxVolume;
            }
            return max > 0 ? used / max : 1;
        }

        // The connector on the other side of whichever drone connector is locked, or null. Only the drone owner's or
        // their faction's: cargo is never moved to or from anyone else's network.
        private IMyShipConnector DockedConnector()
        {
            for (int i = 0; i < connectors.Count; i++)
            {
                var c = connectors[i];
                if (!c.IsConnected || c.OtherConnector == null) continue;
                if (AnchorDirectory.Resolve(block, c.OtherConnector.EntityId) != null) return c.OtherConnector;
            }
            return null;
        }

        private bool IsDockedAt(long connectorId)
        {
            var other = DockedConnector();
            return other != null && other.EntityId == connectorId;
        }
        #endregion

        #region Orchestrator: auctions and awarded jobs
        /// <summary>
        /// Available for a new job: not working one, not refuelling (past the fuel hysteresis), not in error,
        /// not flying somewhere, and able to fly at all.
        /// </summary>
        public bool IsAvailableForJobs
        {
            get
            {
                return initialized && settings != null && settings.IsEnabled
                    && settings.OperationMode == OperationMode.ManagedByScheduler
                    && cPhase == ConstructionPhase.Idle && currentJob == null
                    && currentState != State.Error && !needsBatteryRecharge && !needsHydrogenRefuel
                    && preflightStage == 0 && activeFlightOrder == null && dockingHomeId == 0
                    && (capabilities & (Capabilities.CanFlyAtmosphere | Capabilities.CanFlySpace)) != 0;
            }
        }

        // UpdateBeforeSimulation100, server, ManagedByScheduler only
        private void UpdateOrchestratorJobs()
        {
            if (!IsServer || settings == null || !settings.IsEnabled || settings.OperationMode != OperationMode.ManagedByScheduler) return;
            if (cPhase == ConstructionPhase.Running && jobSource == JobSource.Orchestrator && (needsBatteryRecharge || needsHydrogenRefuel))
            {
                Report("Job abandoned: low power");
                StopJob();   // MonitorPower sends the drone home
                return;
            }
            var queue = AutomataSession.GetMessageQueue();
            if (queue == null || primaryAntenna == null) return;
            ReadAwards(queue);
            ReadAuctions(queue);
        }

        private void ReadAwards(MessageQueue queue)
        {
            queue.ReadMessages(_entityId, primaryAntenna, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT, awardInbox, 10, true,
                               PayloadType.AuctionWinnerAnnouncement);
            for (int i = 0; i < awardInbox.Count; i++)
            {
                var award = awardInbox[i].Payload;
                List<Orchestrator.Task> tasks;
                if (award == null || award.TaskAssignments == null || !award.TaskAssignments.TryGetValue(_entityId, out tasks)
                    || tasks == null || tasks.Count == 0) continue;
                // Only the award for our outstanding bid (announcements are re-read for a while: no replays), and
                // only from an orchestrator of the drone owner / their faction
                if (award.AuctionId != pendingBidAuctionId || pendingBidAuctionId == 0) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, awardInbox[i].SenderOwnerId)) continue;
                pendingBidAuctionId = 0;
                if (!IsAvailableForJobs)
                {
                    Log.Debug("Drone {0}: award for job {1} ignored, busy", _entityId, award.JobId);
                    continue;
                }
                var job = new Orchestrator.Job
                {
                    JobId = award.JobId,
                    Tasks = new List<Orchestrator.Task>(tasks),
                    CreatedTime = award.CreatedTime,
                    AssignedTime = DateTime.UtcNow,
                };
                for (int k = 0; k < tasks.Count; k++)
                {
                    if (tasks[k].Kind != Orchestrator.TaskKind.Tool) continue;
                    job.BlockCount++;
                    if (job.JobType == Orchestrator.JobType.None)
                        job.JobType = tasks[k].Type == Orchestrator.TaskType.GrindBlock ? Orchestrator.JobType.Grind
                                    : tasks[k].Type == Orchestrator.TaskType.Mine ? Orchestrator.JobType.MineOre : Orchestrator.JobType.Weld;
                }
                job.CalculateTotalInventory();
                Report("Job {0} awarded: {1} tasks", award.JobId, tasks.Count);
                StartJob(job, JobSource.Orchestrator);
                break;   // one job at a time
            }
            awardInbox.Clear();
        }

        private void ReadAuctions(MessageQueue queue)
        {
            queue.ReadMessages(_entityId, primaryAntenna, Channel.ORCHESTRATOR_AUCTION_START, auctionInbox, 10, true, PayloadType.Auction);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            ForgetOldAuctions(now);
            if (pendingBidAuctionId != 0 && DateTime.UtcNow > pendingBidExpires) pendingBidAuctionId = 0;   // lost / never announced
            if (!IsAvailableForJobs || pendingBidAuctionId != 0) { auctionInbox.Clear(); return; }
            for (int i = 0; i < auctionInbox.Count; i++)
            {
                var auction = auctionInbox[i].Payload;
                if (auction == null || auction.ExpirationTime < DateTime.UtcNow || biddedAuctions.ContainsKey(auction.AuctionId)) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, auctionInbox[i].SenderOwnerId)) continue;
                if (!CanTakeJob(auction)) continue;
                SendBid(queue, auction);
                biddedAuctions[auction.AuctionId] = now;
                pendingBidAuctionId = auction.AuctionId;
                pendingBidExpires = auction.ExpirationTime.AddSeconds(30);   // the announcement follows the bid window
                break;   // one job at a time: wait for this auction's outcome
            }
            auctionInbox.Clear();
        }

        // The drone's own minimum check (the orchestrator checks again): equipment, environment, load
        private bool CanTakeJob(Auction auction)
        {
            var caps = BidCapabilities;
            switch (auction.JobType)
            {
                case Orchestrator.JobType.Weld:    if ((caps & Capabilities.CanWeld) == 0) return false; break;
                case Orchestrator.JobType.Grind:   if ((caps & Capabilities.CanGrind) == 0) return false; break;
                case Orchestrator.JobType.MineOre: if ((caps & Capabilities.CanDrill) == 0) return false; break;
                default:                           if ((caps & Capabilities.CargoVolumeNil) != 0) return false; break;
            }
            if (auction.IsInSpace && (caps & Capabilities.CanFlySpace) == 0) return false;
            if (auction.IsInAtmosphere && (caps & Capabilities.CanFlyAtmosphere) == 0) return false;
            return true;
        }

        private void SendBid(MessageQueue queue, Auction auction)
        {
            CaptureFlightState();
            double maxLoad1G = Math.Max(0, RatedThrust(Base6Directions.Direction.Up) / 9.81 - DroneOwnMass());
            var msg = new Message<Bid>
            {
                Payload = new Bid
                {
                    EntityId = _entityId,
                    EntityType = AutomataEntityType.DroneControllerBlock,
                    JobId = auction.JobId,
                    AuctionId = auction.AuctionId,
                    BidId = IdGenerator.GenerateId(ref bidIdCounter, _entityId),
                    CreatedTime = DateTime.UtcNow,
                    Capabilities = BidCapabilities,
                    DroneBehaviours = settings.BehaviourProfile,
                    IOLocationData = new IOLocationData
                    {
                        PositionData = Vector3DData.FromVector3D(flightState.Position),
                        OrientationData = QuaternionDData.FromQuaternionD(QuaternionD.CreateFromRotationMatrix(flightState.WorldMatrix)),
                        EntityId = _entityId,
                    },
                    MaxLoadIn1G = (MyFixedPoint)maxLoad1G,
                    OptimalLoadIn1G = (MyFixedPoint)PayloadIn1G(),   // within the player's max-load setting
                    MaxCargoVolume = (MyFixedPoint)DroneCargoFreeVolume(),
                },
                MessageId = IdGenerator.GenerateId(ref jobMessageCounter, _entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                Channel = Channel.ORCHESTRATOR_AUCTION_BIDS,
            };
            queue.BroadcastMessage(primaryAntenna, msg);
            Log.Debug("Drone {0}: bid on auction {1} (job {2}, {3})", _entityId, auction.AuctionId, auction.JobId, auction.JobType);
        }

        private void ForgetOldAuctions(int now)
        {
            if (biddedAuctions.Count == 0) return;
            auctionCleanup.Clear();
            foreach (var kv in biddedAuctions)
                if (now - kv.Value > AUCTION_MEMORY_TICKS) auctionCleanup.Add(kv.Key);
            for (int i = 0; i < auctionCleanup.Count; i++) biddedAuctions.Remove(auctionCleanup[i]);
            auctionCleanup.Clear();
        }
        #endregion
    }
}
