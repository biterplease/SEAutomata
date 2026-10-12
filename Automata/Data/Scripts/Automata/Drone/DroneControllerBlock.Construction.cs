using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Construction;
using Automata.Inventory;
using Automata.Logistics;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// Stand-alone construction (OperationMode.StandAlone), server side. Never goes through the virtual network.
    ///
    /// Cycle: scan the observation area (embedded ConstructionComputer, central-out) -> dock at home -> unload,
    /// read the home conveyor network, plan a batch that fits the load limit and cargo -> pull the components ->
    /// fly to each block and work on it ONE AT A TIME (tool on only once in position, off as soon as that block is
    /// done, so approaching neighbours aren't welded on the way and the drone doesn't wall itself out) -> go home,
    /// repeat until the area has nothing left to do.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrConstructionNoLoadCapacity = MyStringId.GetOrCompute("Construction: no load capacity (max load setting)");
        private static readonly MyStringId StrConstructionMissing = MyStringId.GetOrCompute("Construction: missing {0}");
        private static readonly MyStringId StrConstructionOnHoldHomeConnector = MyStringId.GetOrCompute("Construction on hold: home connector unavailable (damaged, owner / faction, out of range)");
        private static readonly MyStringId StrConstructionPausedLowPower = MyStringId.GetOrCompute("Construction paused: low power");
        private static readonly MyStringId StrResumingJobAtTask = MyStringId.GetOrCompute("Resuming job {0} ({3}) at task {1}/{2}");
        private static readonly MyStringId StrConstructionBlocksToWorkOn = MyStringId.GetOrCompute("Construction: {0} blocks to work on");
        private static readonly MyStringId StrConstructionBlocksOutOfReach = MyStringId.GetOrCompute("Construction: {0} blocks out of reach for now");
        private static readonly MyStringId StrConstructionNothingToDoIn = MyStringId.GetOrCompute("Construction: nothing to do in the area");
        private static readonly MyStringId StrConstructionMovingBaseSelectIts = MyStringId.GetOrCompute("Construction: moving base, select its beacon (Home is relative to beacon)");
        private static readonly MyStringId StrConstructionHomeIsMovingTick = MyStringId.GetOrCompute("Construction: home is moving: tick 'Home connector is not station' and select its beacon");
        private static readonly MyStringId StrComponents = MyStringId.GetOrCompute("components");
        #endregion

        private enum ConstructionPhase : byte
        {
            Idle,
            WaitingForDock,   // blocks found away from home: flying home to plan and load
            Running,          // executing currentJob's tasks
        }

        private enum TaskStatus : byte
        {
            Running,
            Done,
            SkipBlock,        // this NavigateToolTo, its tool task and its BackOut
            ToReturnHome,     // skip ahead to the job's ReturnHome
            Failed,           // end the job here
            Interrupted,      // someone else took the controls
        }

        private const float CONSTRUCTION_LINE_TOLERANCE = 0.5f;      // m: stay on the approach line
        #region Timing
        private const int CONSTRUCTION_LEG_SETTLE_TICKS = 3 * 60;    // a detour leg hovering close by: good enough
        #endregion
        private const double CONSTRUCTION_LEG_CLOSE = 3.0;           // m: "close by" for that
        private const double CONSTRUCTION_CLEARANCE = 2.0;           // m: kept between the hull and anything when detouring
        private const float CONSTRUCTION_ROUTE_TOLERANCE = 1.5f;     // m: arrival at detour / back-out points
        #region Timing
        public const int CONSTRUCTION_STOP_PAUSE_TICKS = 120 * 60;   // "Stop" pauses stand-alone work for 2 min
        #endregion

        private ConstructionComputer constructionComputer;           // embedded, BuiltInToDrone
        private readonly ConveyorNetwork homeNetwork = new ConveyorNetwork();
        private long homeNetworkLastUpdateFrame = 0;
        private int homeNetworkUpdateIntervalTicks = 600; // 10 seconds
        private readonly List<ConstructionTarget> scannedTargets = new List<ConstructionTarget>();
        private readonly List<IMyCubeGrid> droneGrids = new List<IMyCubeGrid>();
        private readonly List<IMyCubeGrid> homeGrids = new List<IMyCubeGrid>();
        private readonly List<VRage.Game.ModAPI.Ingame.MyInventoryItem> itemBuffer = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
        private readonly List<KVPair> kvBuffer = new List<KVPair>();
        private ConstructionPhase cPhase;
        private Orchestrator.Job currentJob;                  // one trip: the drone works its tasks in order
        private int taskIndex;
        private bool taskStarted;
        private int taskStartFrame;
        private double taskBestDistance;                      // progress watchdog for navigation tasks (per leg)
        private int taskProgressFrame;
        private FlightOrder watchedOrder;
        private FlightPhase watchedPhase;
        private int lastConstructionScanFrame = int.MinValue / 2;
        private int lastReportedJobCount = -1;
        private FlightOrder constructionOrder;          // the order construction issued; anything else means "stop"
        private readonly List<FlightOrder> constructionLegs = new List<FlightOrder>();   // detour legs flown before it
        private readonly List<IHitInfo> rayHits = new List<IHitInfo>();
        private readonly List<Vector3D> routePoints = new List<Vector3D>();
        private int constructionPausedUntil = int.MinValue / 2;
        private int stopResumeFrame = int.MinValue / 2;       // "Stop" pressed: its pause ends here (header countdown)
        private int shownStopSeconds = -1;
        private string lastConstructionProblem;          // reported once until it changes

        private static readonly string COMPONENT_TYPE = "MyObjectBuilder_Component";
        private static readonly string ORE_TYPE = "MyObjectBuilder_Ore";

        #region Terminal
        public bool IsStandAlone { get { return settings != null && settings.OperationMode == OperationMode.StandAlone; } }

        public Construction.WorkModes Terminal_ConstructionWorkModes
        {
            get { return settings != null ? settings.ConstructionWorkModes : Construction.WorkModes.None; }
            set
            {
                if (settings == null) return;
                settings.ConstructionWorkModes = value & (Construction.WorkModes.WeldUnfinishedBlocks | Construction.WorkModes.RepairDamagedBlocks
                                                         | Construction.WorkModes.WeldProjectedBlocks | Construction.WorkModes.Grind);
                lastConstructionScanFrame = int.MinValue / 2;   // rescan with the new modes
                SaveSettings();
                SyncSetting(DroneSettingKey.ConstructionWorkModes);
            }
        }

        public long Terminal_ConstructionWorkPriority
        {
            get { return settings != null ? (long)settings.ConstructionWorkPriority : 0; }
            set
            {
                if (settings == null) return;
                var p = Construction.WorkPriorities.Clamp(value);
                if (settings.ConstructionWorkPriority == p) return;
                settings.ConstructionWorkPriority = p;
                lastConstructionScanFrame = int.MinValue / 2;   // rescan in the new order
                SaveSettings();
                SyncSetting(DroneSettingKey.ConstructionWorkPriority);
            }
        }

        public bool Terminal_GetWorkMode(Construction.WorkModes flag)
        {
            return (Terminal_ConstructionWorkModes & flag) != 0;
        }

        public void Terminal_SetWorkMode(Construction.WorkModes flag, bool on)
        {
            var modes = Terminal_ConstructionWorkModes;
            Terminal_ConstructionWorkModes = on ? modes | flag : modes & ~flag;
        }

        public Color Terminal_WeldIgnoreColor
        {
            get { return settings != null ? new Color(settings.WeldIgnoreColor) : new Color(255, 0, 255); }
            set
            {
                if (settings == null) return;
                settings.WeldIgnoreColor = value.PackedValue;
                SaveSettings();
                SyncSetting(DroneSettingKey.WeldIgnoreColor);
            }
        }

        public Color Terminal_GrindColor
        {
            get { return settings != null ? new Color(settings.GrindColor) : Color.Red; }
            set
            {
                if (settings == null) return;
                settings.GrindColor = value.PackedValue;
                SaveSettings();
                SyncSetting(DroneSettingKey.GrindColor);
            }
        }
        #endregion

        #region Update hooks (server)
        // UpdateBeforeSimulation100: when to start a trip
        private bool homeUsable = true;   // last seen state of the home connector (reported on change)

        private void UpdateConstruction100()
        {
            if (!IsServer || settings == null) return;
            if (savedSave != null) return;   // its saved job comes first
            if (jobSource == JobSource.Orchestrator || jobSource == JobSource.Housekeeping) return;   // awarded jobs: UpdateOrchestratorJobs; its own errand
            bool home = HasHomeConnector;
            bool wanted = settings.IsEnabled && IsStandAlone && settings.ConstructionWorkModes != Construction.WorkModes.None;
            if (wanted && home != homeUsable)
            {
                homeUsable = home;
                if (!home) Report(StrConstructionOnHoldHomeConnector);
            }
            bool active = wanted && home;
            if (!active)
            {
                if (cPhase != ConstructionPhase.Idle) StopJob();
                return;
            }
            if (needsBatteryRecharge || needsHydrogenRefuel)
            {
                if (cPhase != ConstructionPhase.Idle)
                {
                    Report(StrConstructionPausedLowPower);
                    StopJob();   // MonitorPower sends the drone home
                }
                return;
            }

            int now = MyAPIGateway.Session.GameplayFrameCounter;
            switch (cPhase)
            {
                case ConstructionPhase.Idle:
                    if (now < constructionPausedUntil) return;
                    if (now - lastConstructionScanFrame < AutomataSession.GetConfig().ConstructionComputer.DroneScanIntervalSeconds * 60) return;
                    lastConstructionScanFrame = now;
                    if (activeFlightOrder != null && dockingHomeId == 0) return;   // busy with a player's order
                    if (ScanConstructionTargets() == 0) return;
                    if (!HasToolsFor(scannedTargets)) return;
                    if (IsDockedAtHome()) StartStandAloneJob();
                    else if (dockingHomeId == 0 && GoHome()) cPhase = ConstructionPhase.WaitingForDock;
                    break;

                case ConstructionPhase.WaitingForDock:
                    if (IsDockedAtHome()) StartStandAloneJob();
                    else if (dockingHomeId == 0 && activeFlightOrder == null) cPhase = ConstructionPhase.Idle;   // docking failed / cancelled
                    break;
            }
        }

        // UpdateBeforeSimulation10: the task runner
        private void UpdateConstruction10()
        {
            if (savedSave != null && IsServer && initialized && Util.BlockSave.CanRestore) ResumeSaved();
            if (!IsServer || cPhase != ConstructionPhase.Running || currentJob == null) return;
            RunTasks();
        }

        #region Save / load
        private DroneSaveData savedSave;   // runtime parts of the save struct, read at load, applied once the drone is set up

        private DroneJobState CaptureJobState()
        {
            var s = new DroneJobState
            {
                BidCounter = bidIdCounter,
                MessageCounter = jobMessageCounter,
                UnsentReports = new List<VirtualNetwork.JobStatusReport>(unsentReports),
                PendingBidAuctionId = pendingBidAuctionId,
                PendingBidSecondsLeft = pendingBidAuctionId != 0 ? Util.BlockSave.SecondsLeft(pendingBidExpires) : 0,
            };
            if (currentJob != null && cPhase == ConstructionPhase.Running)
            {
                s.Job = currentJob;
                s.TaskIndex = taskIndex;
                s.Source = (byte)jobSource;
                s.BlocksDone = jobBlocksDone;
                s.FieldContinuations = fieldContinuations;
                if (jobSource == JobSource.Orchestrator)
                {
                    s.Announcer = jobAnnouncerId;
                    s.AuctionId = jobAuctionId;
                    s.Orchestrator = jobOrchestratorId;
                    s.MinedOreKg = minedOreKg;
                    s.SharedSite = jobSharedSite;
                }
            }
            Log.Debug("Drone {0}: state saved: job {1} at task {2}, pending bid {3}", Entity.EntityId, s.Job != null ? s.Job.JobId : 0,
                      s.TaskIndex, s.PendingBidAuctionId);
            return s;
        }

        private DroneOperatingState CaptureOperatingState()
        {
            var o = new DroneOperatingState
            {
                MaxWorkTiltDegrees = maxWorkTiltDegrees,
                NeedsBatteryRecharge = needsBatteryRecharge,
                NeedsHydrogenRefuel = needsHydrogenRefuel,
            };
            for (int d = 0; d < thrusterBaseline.Length; d++) o.ThrusterBaseline.Add(thrusterBaseline[d]);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            for (int i = 0; i < unreachableBlocks.Count; i++)
            {
                var u = unreachableBlocks[i];
                if (u.Until > now)
                    o.Unreachable.Add(new UnreachableState { Grid = u.Grid, X = u.Cell.X, Y = u.Cell.Y, Z = u.Cell.Z, TicksLeft = u.Until - now });
            }
            if (workedGrids.Count > 0) BuildPlanner.ExportFrames(workedGrids, o.Frames);
            o.Surveys.AddRange(surveyBuffer);
            return o;
        }

        // Grids its scans / jobs worked on (their planner sweep frames go into the save)
        private readonly HashSet<long> workedGrids = new HashSet<long>();

        private void ResumeSaved()
        {
            var save = savedSave;
            savedSave = null;
            var o = save.Operating;
            if (o != null)
            {
                maxWorkTiltDegrees = o.MaxWorkTiltDegrees;
                needsBatteryRecharge = o.NeedsBatteryRecharge;
                needsHydrogenRefuel = o.NeedsHydrogenRefuel;
                if (o.ThrusterBaseline != null)
                    for (int d = 0; d < thrusterBaseline.Length && d < o.ThrusterBaseline.Count; d++)
                        thrusterBaseline[d] = Math.Max(thrusterBaseline[d], o.ThrusterBaseline[d]);
                int now = MyAPIGateway.Session.GameplayFrameCounter;
                if (o.Unreachable != null)
                    for (int i = 0; i < o.Unreachable.Count; i++)
                        unreachableBlocks.Add(new UnreachableBlock { Grid = o.Unreachable[i].Grid, Cell = new Vector3I(o.Unreachable[i].X, o.Unreachable[i].Y, o.Unreachable[i].Z),
                                                                     Until = now + o.Unreachable[i].TicksLeft });
                if (o.Surveys != null) surveyBuffer.AddRange(o.Surveys);
                if (o.Frames != null)
                {
                    BuildPlanner.ImportFrames(o.Frames);
                    for (int i = 0; i < o.Frames.Count; i++) workedGrids.Add(o.Frames[i].Key < 0 ? ~o.Frames[i].Key : o.Frames[i].Key);
                }
            }
            var queue = AutomataSession.GetMessageQueue();
            int sent = queue != null ? queue.RestoreSent(save.Outbox) : 0;
            Log.Info("Drone {0}: save applied: work tilt {1:F0}°, {2} unreachable block(s), {3} sweep frame(s), {4} message(s) back in flight",
                     Entity.EntityId, maxWorkTiltDegrees, unreachableBlocks.Count, o != null && o.Frames != null ? o.Frames.Count : 0, sent);
            if (save.Job != null) ResumeSavedJob(save.Job);
            if (save.Combat != null) ResumeCombat(save.Combat);
        }

        // The saved job goes on from the start of the task group it was in: a tool / back-out task from its
        // NavigateToolTo, a load / unload from the DockAt before it (the drone may have drifted, docked or undocked)
        private void ResumeSavedJob(DroneJobState s)
        {
            bidIdCounter = System.Math.Max(bidIdCounter, s.BidCounter);
            jobMessageCounter = System.Math.Max(jobMessageCounter, s.MessageCounter);
            if (s.UnsentReports != null)
                for (int i = 0; i < s.UnsentReports.Count; i++) { unsentReports.Add(s.UnsentReports[i]); unsentReportTimes.Add(DateTime.UtcNow); }
            if (s.PendingBidAuctionId != 0 && s.PendingBidSecondsLeft > 0 && currentJob == null)
            {
                pendingBidAuctionId = s.PendingBidAuctionId;
                pendingBidExpires = Util.BlockSave.FromSecondsLeft(s.PendingBidSecondsLeft);
            }
            if (s.Job == null || s.Job.Tasks == null || s.Job.Tasks.Count == 0) return;
            if (currentJob != null || settings == null || !settings.IsEnabled)
            {
                Log.Info("Drone {0}: saved job {1} not resumed ({2})", Entity.EntityId, s.Job.JobId, currentJob != null ? "busy" : "AI off");
                return;
            }
            int index = System.Math.Min(System.Math.Max(0, s.TaskIndex), s.Job.Tasks.Count - 1);
            var type = s.Job.Tasks[index].Type;
            if (s.Job.Tasks[index].Kind == Orchestrator.TaskKind.Tool || type == Orchestrator.TaskType.BackOut)
            {
                while (index > 0 && s.Job.Tasks[index].Type != Orchestrator.TaskType.NavigateToolTo) index--;
            }
            else if (type == Orchestrator.TaskType.LoadInventory || type == Orchestrator.TaskType.UnloadInventory)
            {
                while (index > 0 && s.Job.Tasks[index].Type != Orchestrator.TaskType.DockAt) index--;
            }
            Report(StrResumingJobAtTask, s.Job.JobId, index + 1, s.Job.Tasks.Count, Orchestrator.JobTypeExtensions.Label(s.Job.JobType, s.Job.WeldCategory));
            StartJob(s.Job, (JobSource)s.Source, index);
            jobAnnouncerId = s.Announcer;
            jobAuctionId = s.AuctionId;
            jobOrchestratorId = s.Orchestrator;
            jobAwardType = s.Job.JobType;
            jobAwardWeld = s.Job.WeldCategory;
            minedOreKg = s.MinedOreKg;
            jobSharedSite = s.SharedSite && (JobSource)s.Source == JobSource.Orchestrator;
            jobBlocksDone = s.BlocksDone;
            fieldContinuations = s.FieldContinuations;
            Log.Info("Drone {0}: job {1} ({2}) resumed after load at task {3} ({4}), saved at {5}", Entity.EntityId, s.Job.JobId, (JobSource)s.Source,
                     index, s.Job.Tasks[index].Type, s.TaskIndex);
        }
        #endregion
        #endregion

        #region Scan / plan
        private readonly List<IMyCubeGrid> gpsRoots = new List<IMyCubeGrid>();

        private int ScanConstructionTargets()
        {
            scannedTargets.Clear();
            var home = GetHomeConnector();
            MatrixD areaMatrix;
            Vector3D half;
            if (home == null || !TryGetObservationArea(out areaMatrix, out half)) return 0;
            if (constructionComputer == null)
                constructionComputer = new ConstructionComputer(Entity, Construction.OperationMode.BuiltInToDrone);
            RefreshDroneGrids();
            var grindColor = new Color(settings.GrindColor);
            var weldIgnoreColor = new Color(settings.WeldIgnoreColor);
            // The home grid and everything physically linked to it (rotors, pistons, connectors, landing gear) that
            // the drone owner / faction owns (or nobody does)
            // GPS observation area: the structures there (not linked to the home grid) are scanned too
            Vector3D gps;
            List<IMyCubeGrid> roots = null;
            if (ObservationGps(out gps))
            {
                gpsRoots.Clear();
                var sphere = new BoundingSphereD(areaMatrix.Translation, half.Length());
                foreach (var e in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere))
                {
                    var g = e as IMyCubeGrid;
                    if (g != null && g.Physics != null && !droneGrids.Contains(g)) gpsRoots.Add(g);
                }
                roots = gpsRoots;
            }
            int count = constructionComputer.ScanArea(home.CubeGrid, ref areaMatrix, ref half, settings.ConstructionWorkModes, grindColor, weldIgnoreColor,
                                                      droneGrids, AutomataSession.GetConfig().ConstructionComputer.DroneMaxJobsPerScan, scannedTargets,
                                                      g => !IsGridAllowed(g, 0) ? "not owner/faction" : IsActiveDroneGroup(g) ? "a working drone" : null,
                                                      settings.ConstructionWorkPriority, roots);
            if (roots != null) gpsRoots.Clear();
            for (int i = 0; i < scannedTargets.Count; i++) workedGrids.Add(scannedTargets[i].GridEntityId);
            int parked = SkipRecentlyUnreachable();
            count = scannedTargets.Count;
            Log.Debug("Drone {0}: construction scan found {1} blocks{2}", Entity.EntityId, count,
                      parked > 0 ? string.Format(" ({0} more unreachable lately, retried later)", parked) : "");
            if (count != lastReportedJobCount)
            {
                lastReportedJobCount = count;
                if (count > 0) Report(StrConstructionBlocksToWorkOn, count);
                else if (parked > 0) Report(StrConstructionBlocksOutOfReach, parked);
                else Report(StrConstructionNothingToDoIn);
            }
            return count;
        }

        // Blocks no face / approach of which fitted (NavigateToolTo: Unreachable) stay out of the plans for a while:
        // otherwise every scan loads their parts, finds them unreachable again at once and unloads (a loop every scan).
        // Retried later: neighbours built or removed meanwhile can open a way in.
        #region Timing
        private const int UNREACHABLE_RETRY_TICKS = 5 * 60 * 60;
        #endregion
        private struct UnreachableBlock { public long Grid; public Vector3I Cell; public int Until; }
        private readonly List<UnreachableBlock> unreachableBlocks = new List<UnreachableBlock>();

        private void RememberUnreachable(long grid, Vector3I cell)
        {
            int until = MyAPIGateway.Session.GameplayFrameCounter + UNREACHABLE_RETRY_TICKS;
            for (int i = 0; i < unreachableBlocks.Count; i++)
                if (unreachableBlocks[i].Grid == grid && unreachableBlocks[i].Cell == cell)
                {
                    unreachableBlocks[i] = new UnreachableBlock { Grid = grid, Cell = cell, Until = until };
                    return;
                }
            unreachableBlocks.Add(new UnreachableBlock { Grid = grid, Cell = cell, Until = until });
        }

        // Drops recently unreachable blocks from scannedTargets (expired entries forgotten); returns how many
        private int SkipRecentlyUnreachable()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter, skipped = 0;
            for (int i = unreachableBlocks.Count - 1; i >= 0; i--)
                if (now >= unreachableBlocks[i].Until) unreachableBlocks.RemoveAt(i);
            if (unreachableBlocks.Count == 0) return 0;
            for (int i = scannedTargets.Count - 1; i >= 0; i--)
            {
                var t = scannedTargets[i];
                for (int k = 0; k < unreachableBlocks.Count; k++)
                    if (unreachableBlocks[k].Grid == t.GridEntityId && unreachableBlocks[k].Cell == t.Cell)
                    {
                        scannedTargets.RemoveAt(i);
                        skipped++;
                        break;
                    }
            }
            return skipped;
        }

        /// <summary>
        /// Frame for a stand-alone trip's positions. A station: world positions. A moving base ("Home connector is
        /// not station"): the home beacon, so the work follows the base. Without a beacon a moving base can't be
        /// worked on. (Never the home connector itself: jobs are shared with orchestrators.)
        /// </summary>
        private bool TryGetStandAloneFrame(IMyShipConnector home, out IMyTerminalBlock beacon)
        {
            beacon = null;
            bool moving = settings.HomeNotStatic
                || (home.CubeGrid.Physics != null && home.CubeGrid.Physics.LinearVelocity.LengthSquared() > 0.25);
            if (!moving) return true;
            beacon = settings.HomeBeaconId != 0 ? AnchorDirectory.Resolve(block, settings.HomeBeaconId) : null;
            if (beacon != null) return true;
            ReportProblem(settings.HomeNotStatic
                ? Loc.T(StrConstructionMovingBaseSelectIts)
                : Loc.T(StrConstructionHomeIsMovingTick));
            return false;
        }

        // Docked at home: plan one trip from the scan (what the home network has, what the drone can lift and hold)
        private void StartStandAloneJob()
        {
            cPhase = ConstructionPhase.Idle;
            var home = GetHomeConnector();
            if (home == null) return;
            IMyTerminalBlock beacon;
            if (!TryGetStandAloneFrame(home, out beacon)) return;
            if (scannedTargets.Count == 0 && ScanConstructionTargets() == 0)
                return;

            BuildHomeNetwork(home);
            UnloadCargo(null); // leftovers back first, so the plan sees everything
            ClearCargo();      // ores, ingots, the tools' inventories too: the trip starts empty
            homeNetwork.UpdateInventories();

            // Capacity: load limit for where the drone is (thrusters may be asleep: use their rated thrust)
            double limitPct = flightState.InGravity ? settings.MaxLoadGravity : settings.MaxLoadSpace;
            double g = flightState.InGravity ? flightState.Gravity.Length() : 0;
            double maxTotal = flightState.InGravity
                ? RatedThrust(Base6Directions.Direction.Up) / g
                : WeakestRatedThrust() / SPACE_REFERENCE_ACCEL;
            double maxMass = maxTotal * limitPct / 100.0 - DroneOwnMass();
            double maxVolume = DroneCargoFreeVolume();

            var missing = new DiscreteInventory();
            var job = constructionComputer.PlanJob(scannedTargets, homeNetwork.GetCurrentInventory(), Math.Max(0, maxMass), maxVolume,
                                                   beacon, (float)g, missing);
            if (job == null)
            {
                ReportProblem(maxMass <= 0 ? Loc.T(StrConstructionNoLoadCapacity) : Loc.F(StrConstructionMissing, DescribeTop(missing, 3)));
                return;
            }
            scannedTargets.Clear();   // rescan after this trip: neighbours become buildable as blocks are finished
            LogTrip(job, maxMass, maxVolume);
            StartJob(job, JobSource.StandAlone);
        }
        #endregion

        // Debug: the blocks of a trip, in the order they will be worked
        private void LogTrip(Orchestrator.Job job, double maxMass, double maxVolume)
        {
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < job.Tasks.Count; i++)
            {
                var t = job.Tasks[i];
                if (t.Kind != Orchestrator.TaskKind.Tool) continue;
                if (n++ < 30) sb.Append(' ').Append(t.TargetBlock.ToVector3I().ToString()).Append(t.IsProjected ? "p" : "");
            }
            Log.Debug("Drone {0}: trip: {1} {2} blocks (load limit {3:F0} kg, {4:F2} m3), in order:{5}{6}", Entity.EntityId, n, job.JobType,
                      maxMass, maxVolume, sb, n > 30 ? " ..." : "");
        }

        #region Home conveyor network and cargo
        private bool IsDockedAtHome()
        {
            if (settings == null || settings.HomeConnectorId == 0) return false;
            for (int i = 0; i < connectors.Count; i++)
            {
                var c = connectors[i];
                if (c.IsConnected && c.OtherConnector != null && c.OtherConnector.EntityId == settings.HomeConnectorId) return true;
            }
            return false;
        }

        private void RefreshDroneGrids()
        {
            droneGrids.Clear();
            if (shipController != null)
                shipController.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(droneGrids);
        }

        // The conveyor-connected base behind the home connector (logical group, minus the drone)
        private void BuildHomeNetwork(IMyShipConnector home)
        {
            RefreshDroneGrids();
            homeNetwork.Clear();
            homeGrids.Clear();
            home.CubeGrid.GetGridGroup(GridLinkTypeEnum.Logical).GetGrids(homeGrids);
            for (int i = 0; i < homeGrids.Count; i++)
            {
                var grid = homeGrids[i];
                if (droneGrids.Contains(grid) || grid.Physics == null || HasDroneController(grid)) continue;   // other docked drones
                foreach (var b in grid.GetFatBlocks<IMyCargoContainer>()) if (b.IsFunctional) homeNetwork.cargoContainers.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyShipConnector>()) if (b.IsFunctional) homeNetwork.connectors.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyAssembler>()) if (b.IsFunctional) homeNetwork.assemblers.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyCollector>()) if (b.IsFunctional) homeNetwork.collectors.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyRefinery>()) if (b.IsFunctional) homeNetwork.refineries.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyReactor>()) if (b.IsFunctional) homeNetwork.reactors.Add(b);
            }
            homeGrids.Clear();
        }


        // Pulls 'load' items (inventory keys: components, "Ingot/Iron", ...) from the home network into the drone's
        // cargo (conveyor rules apply)
        private int TransferToDrone(DiscreteInventory load)
        {
            int total = 0;
            kvBuffer.Clear();
            load.GetAllItems(kvBuffer);
            for (int k = 0; k < kvBuffer.Count; k++)
            {
                var key = kvBuffer[k].Key;
                int remaining = kvBuffer[k].Value;
                remaining -= PullFrom(homeNetwork.cargoContainers, key, remaining, 0);
                if (remaining > 0) remaining -= PullFrom(homeNetwork.connectors, key, remaining, 0);
                if (remaining > 0) remaining -= PullFrom(homeNetwork.assemblers, key, remaining, 1);   // assembler output
                if (remaining > 0) remaining -= PullFrom(homeNetwork.refineries, key, remaining, 1);   // refinery output (ingots)
                total += kvBuffer[k].Value - remaining;
            }
            kvBuffer.Clear();
            return total;
        }

        private int PullFrom<T>(List<T> sources, MyStringHash key, int wanted, int inventoryIndex) where T : IMyCubeBlock
        {
            int moved = 0;
            for (int s = 0; s < sources.Count && moved < wanted; s++)
            {
                var src = sources[s];
                if (src == null || src.Closed || src.InventoryCount <= inventoryIndex) continue;
                var inv = src.GetInventory(inventoryIndex);
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = itemBuffer.Count - 1; i >= 0 && moved < wanted; i--)
                {
                    var item = itemBuffer[i];
                    if (ItemKeys.Of(item.Type) != key) continue;
                    int amount = Math.Min(wanted - moved, (int)item.Amount);
                    if (amount <= 0) continue;
                    moved += MoveToDroneCargo(inv, i, amount);
                }
            }
            itemBuffer.Clear();
            return moved;
        }

        // What actually moved (TransferItemTo reports success even when only part of the stack fits)
        private int MoveToDroneCargo(IMyInventory src, int itemIndex, int amount)
        {
            var item = src.GetItemAt(itemIndex);
            if (!item.HasValue) return 0;
            var type = item.Value.Type;
            MyFixedPoint before = AmountAt(src, itemIndex, type);
            int moved = 0;
            for (int c = 0; c < cargoContainers.Count && moved < amount; c++)
            {
                var dst = cargoContainers[c].GetInventory(0);
                if (dst == null) continue;
                src.TransferItemTo(dst, itemIndex, null, true, (MyFixedPoint)(amount - moved), true);
                moved = (int)(before - AmountAt(src, itemIndex, type));
            }
            return moved;
        }

        private static MyFixedPoint AmountAt(IMyInventory inv, int index, VRage.Game.ModAPI.Ingame.MyItemType type)
        {
            var it = inv.GetItemAt(index);
            return it.HasValue && it.Value.Type == type ? it.Value.Amount : (MyFixedPoint)0;
        }

        // Drone cargo back into the home network (docked, conveyor-connected). 'only' lists the items to unload
        // (inventory keys; a bare ore subtype matches that ore too); null / empty = all components (ice, ammo etc.
        // stay: the drone may need them)
        // Returns false when something that should have gone stayed in the drone (no room on the other side)
        // allOre: every ore aboard too (mining: other ores and stone the drill picked up)
        private bool UnloadCargo(DiscreteInventory only, bool allOre = false)
        {
            if (homeNetwork.cargoContainers.Count == 0) return false;   // built by the caller for the connector docked at
            bool filtered = only != null && !only.IsEmpty();
            bool left = false;
            // Cargo and every tool of the construct (connectors, collectors, welders, grinders, drills): a drill's ore and
            // stone after the cargo filled, a grinder's parts
            RefreshToolStores();
            for (int c = 0; c < ClearSourceCount; c++)
            {
                var src = ClearSource(c);
                if (src == null) continue;
                itemBuffer.Clear();
                src.GetItems(itemBuffer);
                for (int i = itemBuffer.Count - 1; i >= 0; i--)
                {
                    string typeId = itemBuffer[i].Type.TypeId;
                    if (!(allOre && typeId == ORE_TYPE) && (filtered ? only.GetItemCount(ItemKeys.Of(itemBuffer[i].Type)) <= 0
                                   && (typeId != ORE_TYPE || only.GetItemCount(itemBuffer[i].Type.SubtypeId) <= 0)
                                 : typeId != COMPONENT_TYPE)) continue;
                    var type = itemBuffer[i].Type;
                    // Container after container until the whole stack is gone (a transfer can be partial)
                    for (int d = 0; d < homeNetwork.cargoContainers.Count && AmountAt(src, i, type) > 0; d++)
                    {
                        var dst = homeNetwork.cargoContainers[d].GetInventory(0);
                        if (dst != null) src.TransferItemTo(dst, i, null, true, null, true);
                    }
                    if (AmountAt(src, i, type) > 0) left = true;
                }
            }
            itemBuffer.Clear();
            return !left;
        }

        private static readonly List<IMyCubeGrid> droneGroupBuffer = new List<IMyCubeGrid>();

        /// <summary>
        /// An operating Automata drone (set up, controller functional, AI on, and working a job or neither parked on its
        /// gears nor docked) in the grid's mechanical group: not a construction target (it may fly off). A drone at rest,
        /// or with its AI off, is one (repairs); so is a drone still being built from a projection (not set up yet).
        /// </summary>
        public static bool IsActiveDroneGroup(IMyCubeGrid grid)
        {
            droneGroupBuffer.Clear();
            grid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(droneGroupBuffer);
            bool active = false;
            for (int i = 0; i < droneGroupBuffer.Count; i++)
                if (droneGroupBuffer[i].IsStatic) { droneGroupBuffer.Clear(); return false; }   // a station: no drone
            for (int i = 0; i < droneGroupBuffer.Count && !active; i++)
                foreach (var rc in droneGroupBuffer[i].GetFatBlocks<IMyRemoteControl>())
                {
                    var d = rc.GameLogic != null ? rc.GameLogic.GetAs<DroneControllerBlock>() : null;
                    if (d != null && d.initialized && rc.IsFunctional && d.IsOperating) { active = true; break; }
                }
            droneGroupBuffer.Clear();
            return active;
        }

        /// <summary>AI on and working a job, or neither parked on its gears nor docked (see <see cref="IsActiveDroneGroup"/>).</summary>
        public bool IsOperating
        {
            get
            {
                if (!initialized || settings == null || !settings.IsEnabled) return false;
                if (currentJob != null || cPhase != ConstructionPhase.Idle) return true;
                return !(AnyLandingGearLocked() || AnyConnectorsConnected());
            }
        }

        // Another Automata drone (its cargo is its own business)
        private static bool HasDroneController(IMyCubeGrid grid)
        {
            foreach (var rc in grid.GetFatBlocks<IMyRemoteControl>())
                if (rc.GameLogic != null && rc.GameLogic.GetAs<DroneControllerBlock>() != null) return true;
            return false;
        }

        // Drone mass alone (its own grids; a docked base is a separate physics body)
        // Blocks + cargo of the drone's own grids, as the physics weighs them (inventory mass / the world's block
        // inventory multiplier, like MyCubeGrid.GetCurrentMass's physical mass). Not grid.Physics.Mass: docked, the
        // drone's body is welded to the base's and that reports the whole base (183 t for a 22 t drone).
        private readonly List<IMySlimBlock> massBlocks = new List<IMySlimBlock>();
        private double DroneOwnMass()
        {
            RefreshDroneGrids();
            double blocks = 0, cargo = 0;
            for (int i = 0; i < droneGrids.Count; i++)
            {
                massBlocks.Clear();
                droneGrids[i].GetBlocks(massBlocks);
                for (int k = 0; k < massBlocks.Count; k++)
                {
                    var b = massBlocks[k];
                    blocks += b.Mass;
                    var fat = b.FatBlock;
                    if (fat == null || !fat.HasInventory) continue;
                    for (int n = 0; n < fat.InventoryCount; n++)
                    {
                        var inv = fat.GetInventory(n);
                        if (inv != null) cargo += (double)inv.CurrentMass;
                    }
                }
            }
            massBlocks.Clear();
            float mult = MyAPIGateway.Session != null && MyAPIGateway.Session.SessionSettings != null
                         ? MyAPIGateway.Session.SessionSettings.BlocksInventorySizeMultiplier : 1f;
            return blocks + cargo / (mult > 0 ? mult : 1f);
        }

        private double DroneCargoFreeVolume()
        {
            double v = 0;
            for (int i = 0; i < cargoContainers.Count; i++)
            {
                var inv = cargoContainers[i].GetInventory(0);
                if (inv != null) v += (double)(inv.MaxVolume - inv.CurrentVolume);
            }
            return v;
        }

        // Thrust the drone can have once awake (docked drones have their thrusters switched off)
        private double RatedThrust(Base6Directions.Direction dir)
        {
            double t = 0;
            float density; bool atmosphere;
            AirAt(shipController, out density, out atmosphere);
            var list = allThrusters[(int)dir];
            for (int i = 0; i < list.Count; i++)
                if (list[i].IsFunctional) t += list[i].MaxThrust * AirMultiplier(list[i], density, atmosphere);
            return t;
        }

        private double WeakestRatedThrust()
        {
            double min = double.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                double t = RatedThrust((Base6Directions.Direction)i);
                if (t > 0 && t < min) min = t;
            }
            return min == double.MaxValue ? 0 : min;
        }

        private string DescribeTop(DiscreteInventory inv, int max)
        {
            kvBuffer.Clear();
            inv.GetAllItems(kvBuffer);
            var sb = new StringBuilder();
            for (int i = 0; i < kvBuffer.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(kvBuffer[i].Value).Append(' ').Append(kvBuffer[i].Key.String);
            }
            if (kvBuffer.Count > max) sb.Append(", ...");
            kvBuffer.Clear();
            return sb.Length > 0 ? sb.ToString() : Loc.T(StrComponents);
        }
        #endregion
    }
}
