using System;
using System.Text;
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
using VRage.Utils;

namespace Automata.Drone
{
    /// <summary>
    /// Jobs: an ordered task list per trip, run one task at a time (server side). Two sources:
    /// - Stand-alone: the embedded construction computer plans weld / grind trips inside the observation area
    ///   (DroneControllerBlock.Construction.cs). No coordination with other drones: two drones on the same area
    ///   may pick the same block or get in each other's way. Solving that is what the orchestrator is for.
    /// - Managed by orchestrator: no own scanning. The drone bids on orchestrator auctions over the virtual network
    ///   while it is available (idle, past its fuel hysteresis, no error), runs the job it is awarded, then goes
    ///   home and waits for the next auction.
    /// Task positions are world, or relative to a beacon; never relative to the drone's home connector.
    /// Every task can fail (Orchestrator.TaskFailure); the runner then skips the block, heads home or ends the job.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrTaskFailed = MyStringId.GetOrCompute("task failed");
        private static readonly MyStringId StrTaskFailedReason = MyStringId.GetOrCompute("task failed ({0})");
        private static readonly MyStringId StrTaskFailedSentHome = MyStringId.GetOrCompute("task failed ({0}), sent home");
        private static readonly MyStringId StrReasonSentHome = MyStringId.GetOrCompute("{0}, sent home");
        private static readonly MyStringId StrStopped = MyStringId.GetOrCompute("stopped");
        private static readonly MyStringId StrCouldLoadOnly = MyStringId.GetOrCompute("Job: could load only {0}/{1} parts, check conveyors");
        private static readonly MyStringId StrJobOutcomeReason = MyStringId.GetOrCompute("Job {0} ({3}): {1} ({2})");
        private static readonly MyStringId StrJobOutcome = MyStringId.GetOrCompute("Job {0} ({2}): {1}");
        private static readonly MyStringId StrStartingJobBlocksTasks = MyStringId.GetOrCompute("Starting Job {0} ({3}): blocks: {1}, tasks: {2}");
        private static readonly MyStringId StrSentHomeLoadPower = MyStringId.GetOrCompute("sent home (load / power)");
        private static readonly MyStringId StrBlocksLeftUndone = MyStringId.GetOrCompute("blocks left undone");
        private static readonly MyStringId StrReturnHomeFailedTheJob = MyStringId.GetOrCompute("Return home failed ({0}): the job's work stands, trying again");
        private static readonly MyStringId StrJobInterrupted = MyStringId.GetOrCompute("Job: interrupted");
        private static readonly MyStringId StrInterruptedPlayerOrderGoHome = MyStringId.GetOrCompute("interrupted (player order / go home)");
        private static readonly MyStringId StrBlockTaskFailed = MyStringId.GetOrCompute("Block {0}/{1}, task {2}/{3}: {4} failed: {5}");
        private static readonly MyStringId StrLoadedItemsAtTheRest = MyStringId.GetOrCompute("Loaded {0}/{1} items at {2} (the rest was gone)");
        private static readonly MyStringId StrJobBlocksLoadedParts = MyStringId.GetOrCompute("Job: {0} blocks, loaded {1} parts");
        private static readonly MyStringId StrLoadedItemsAt = MyStringId.GetOrCompute("Loaded {0} items at {1}");
        private static readonly MyStringId StrBlockDone = MyStringId.GetOrCompute("Block {0}/{1}: {2} done ({3})");
        private static readonly MyStringId StrDockWaiting = MyStringId.GetOrCompute("Dock: {0} {1}, waiting");
        private static readonly MyStringId StrCantReachChat = MyStringId.GetOrCompute("Can't reach {0}: {1}");
        private static readonly MyStringId StrDockUsingInstead = MyStringId.GetOrCompute("Dock: {0} instead ({1} {2})");
        private static readonly MyStringId StrDockStillInUseAfter = MyStringId.GetOrCompute("Dock: {0} still in use after {1} min, giving up");
        private static readonly MyStringId StrDockFreeAfterS = MyStringId.GetOrCompute("Dock: {0} free after {1:F0} s");
        private static readonly MyStringId StrTask = MyStringId.GetOrCompute("Task");
        private static readonly MyStringId StrPitchRollLimits = MyStringId.GetOrCompute("pitch {0:F0}° / roll {1:F0}°, limits {2:F0}° / {3:F0}°");
        private static readonly MyStringId StrNoGravity = MyStringId.GetOrCompute("no gravity");
        private static readonly MyStringId StrConstructionNeedsAWelder = MyStringId.GetOrCompute("Construction: needs a welder");
        private static readonly MyStringId StrConstructionNeedsAGrinder = MyStringId.GetOrCompute("Construction: needs a grinder");
        private static readonly MyStringId StrTaskSeconds = MyStringId.GetOrCompute("{0:F0} s");
        private static readonly MyStringId StrCargoFull = MyStringId.GetOrCompute("cargo full");
        private static readonly MyStringId StrCargoEmpty = MyStringId.GetOrCompute("cargo empty");
        private static readonly MyStringId StrGridGone = MyStringId.GetOrCompute("grid gone");
        private static readonly MyStringId StrDismounted = MyStringId.GetOrCompute("dismounted");
        private static readonly MyStringId StrBlockGone = MyStringId.GetOrCompute("block gone");
        private static readonly MyStringId StrFullIntegrity = MyStringId.GetOrCompute("full integrity");
        private static readonly MyStringId StrNoPartsLeftForIt = MyStringId.GetOrCompute("no parts left for it in cargo");
        private static readonly MyStringId StrIntegrity = MyStringId.GetOrCompute("integrity {0:F0}%");
        private static readonly MyStringId StrJobAbandonedLowPower = MyStringId.GetOrCompute("Job abandoned: low power");
        private static readonly MyStringId StrBusy = MyStringId.GetOrCompute("busy");
        #endregion

        private enum JobSource : byte
        {
            None,
            StandAlone,
            Orchestrator,
            Housekeeping,   // the drone's own errand: dock, clear its cargo, rest (DroneControllerBlock.Cargo)
        }

        #region Timing
        private const double MINE_START_SLACK = 4.0;          // m from the bore's surface point: close enough to start drilling
        private const int NO_ORE_TICKS = 15 * 60;             // drilling this long without cargo growing: no ore here
        private const int AUCTION_MEMORY_TICKS = 10 * 60 * 60; // forget auctions bid on after 10 min
        #endregion

        private JobSource jobSource;
        private double mineCargoStart;
        private readonly Dictionary<uint, int> biddedAuctions = new Dictionary<uint, int>();
        private uint pendingBidAuctionId;          // one bid outstanding at a time: the auction it is for
        private DateTime pendingBidExpires;
        private int pendingBidSeenFrame;
        #region Timing
        private const int PENDING_BID_SILENCE_TICKS = 35 * 60;   // its auction not re-broadcast this long: dropped
        #endregion
        private readonly List<uint> auctionCleanup = new List<uint>();
        private readonly List<Message<Auction>> auctionInbox = new List<Message<Auction>>();
        private readonly List<Message<AuctionWinnerAnnouncement>> awardInbox = new List<Message<AuctionWinnerAnnouncement>>();
        private int bidIdCounter;
        private int jobMessageCounter;
        // The award behind the current orchestrator job (its outcome is reported back: JobStatusReport)
        private long jobAnnouncerId, jobOrchestratorId;
        private uint jobAuctionId;
        private Orchestrator.JobType jobAwardType;
        private byte jobAwardWeld;           // weld jobs: which weld (JobTypeExtensions.WELD_*), for logs and reports
        #region Timing
        private const int JOB_REPORT_TTL_SECONDS = 2 * 3600;   // held while out of range (flights between bases), then dropped
        #endregion
        private readonly List<JobStatusReport> unsentReports = new List<JobStatusReport>();
        private readonly List<DateTime> unsentReportTimes = new List<DateTime>();

        // The drone's own setting, capped by the server's (which may have been lowered since it was set)
        private const int PROJECTED_APPEAR_TICKS = 10 * 60;   // welding a projected block that isn't there after this long: skipped

        private int TaskTimeoutTicks
        {
            get
            {
                int max = AutomataSession.GetConfig().Drone.TaskTimeoutSeconds;
                return MathHelper.Clamp(settings != null ? settings.TaskTimeoutSeconds : max, 0, max) * 60;
            }
        }

        #region Job lifecycle
        private void StartJob(Orchestrator.Job job, JobSource source, int startIndex = 0)
        {
            currentJob = job;
            jobAbortReason = null;
            jobSource = source;
            jobBlocksDone = 0;
            lastJobFailure = null;
            ClearSiteRejects(0, Vector3I.Zero);
            jobSharedSite = false;
            siteWatchCenterSet = false;
            siteYielding = false;
            mineAngled = false;
            mineSiteFixed = false;
            scoutSkips = 0;
            lastKeepaliveFrame = int.MinValue / 2;   // the first keepalive at once: its announcer starts watching
            mineRebores = 0;
            boreLineStarts.Clear();
            boreLineEnds.Clear();
            fieldContinuations = 0;
            noFieldContinuation = false;
            tripFailed.Clear();
            taskIndex = startIndex;
            taskStarted = false;
            homeAfterBackOut = false;
            ResetStrafe();
            cPhase = ConstructionPhase.Running;
            if (startIndex == 0 && source != JobSource.Housekeeping) PrepareCargoForJob(job);   // starts empty
            else clearTripInserted = false;
            Report(StrStartingJobBlocksTasks, job.JobId, job.BlockCount, job.Tasks != null ? job.Tasks.Count : 0, Orchestrator.JobTypeExtensions.Label(job.JobType, job.WeldCategory));
            Log.Debug("Drone {0}: job {1}: {2}, {3}", Entity.EntityId, job.JobId, source, job.JobType);
            RunTasks();
        }

        private int jobBlocksDone;   // tool tasks of the current job that completed
        private string jobAbortReason;   // set by a task that ends the job early without failing (engage / escort cut short): reported Aborted

        // Continuing a stand-alone trip in the field: once its blocks are done, more blocks became buildable (projections
        // grow from what was just welded) and the drone may still carry their parts - rescan, plan from the cargo,
        // go on, instead of a round trip home for every handful of blocks.
        private const int FIELD_CONTINUATIONS_MAX = 5;   // per trip
        private int fieldContinuations;
        private bool noFieldContinuation;   // this trip's ReturnHome was a jump (failure / abort): go home
        private readonly List<KeyValuePair<long, Vector3I>> tripFailed = new List<KeyValuePair<long, Vector3I>>();

        private bool TryContinueInField()
        {
            if (jobSource != JobSource.StandAlone || currentJob == null || currentJob.JobType != Orchestrator.JobType.Weld) return false;
            if (noFieldContinuation || !HasTool(Orchestrator.TaskType.WeldBlock)) return false;
            if (fieldContinuations >= FIELD_CONTINUATIONS_MAX || needsBatteryRecharge || needsHydrogenRefuel) return false;
            var cargo = CargoComponents();
            if (cargo.IsEmpty()) return false;
            var home = GetHomeConnector();
            IMyTerminalBlock beacon;
            if (home == null || !TryGetStandAloneFrame(home, out beacon)) return false;
            if (ScanConstructionTargets() == 0) return false;
            for (int i = scannedTargets.Count - 1; i >= 0; i--)
            {
                var t = scannedTargets[i];
                for (int k = 0; k < tripFailed.Count; k++)
                    if (tripFailed[k].Key == t.GridEntityId && tripFailed[k].Value == t.Cell) { scannedTargets.RemoveAt(i); break; }
            }
            if (scannedTargets.Count == 0) return false;
            double g = flightState.InGravity ? flightState.Gravity.Length() : 0;
            var missing = new DiscreteInventory();
            var more = constructionComputer.PlanJob(scannedTargets, cargo, 1e12, 1e12, beacon, (float)g, missing);
            scannedTargets.Clear();
            if (more == null || more.JobType != Orchestrator.JobType.Weld) return false;
            // Its work only: not the Load at the start nor the ReturnHome / Unload at the end
            int at = taskIndex + 1, added = 0;
            for (int i = 0; i < more.Tasks.Count; i++)
            {
                var t = more.Tasks[i];
                if (t.Type == Orchestrator.TaskType.LoadInventory || t.Type == Orchestrator.TaskType.UnloadInventory
                    || t.Type == Orchestrator.TaskType.ReturnHome) continue;
                currentJob.Tasks.Insert(at + added++, t);
            }
            if (added == 0) return false;
            currentJob.Tasks.Insert(at + added, Orchestrator.Task.ReturnHome());   // and home after those (or on again)
            currentJob.BlockCount += more.BlockCount;
            fieldContinuations++;
            Log.Debug("Drone {0}: continuing in the field ({1}/{2}): {3} more blocks from what it carries", Entity.EntityId,
                      fieldContinuations, FIELD_CONTINUATIONS_MAX, more.BlockCount);
            return true;
        }

        // Components in the drone's cargo and its welder
        private DiscreteInventory CargoComponents()
        {
            var inv = new DiscreteInventory();
            for (int c = 0; c <= cargoContainers.Count; c++)
            {
                var src = c < cargoContainers.Count ? cargoContainers[c].GetInventory(0) : welder != null ? welder.GetInventory(0) : null;
                if (src == null) continue;
                itemBuffer.Clear();
                src.GetItems(itemBuffer);
                for (int i = 0; i < itemBuffer.Count; i++)
                    if (itemBuffer[i].Type.TypeId == COMPONENT_TYPE) inv.AddItem(itemBuffer[i].Type.SubtypeId, (int)itemBuffer[i].Amount);
            }
            itemBuffer.Clear();
            return inv;
        }

        private void FinishJob(bool failed = false)
        {
            if (jobSource == JobSource.Orchestrator && currentJob != null)
            {
                bool complete = !failed && !noFieldContinuation && jobAbortReason == null
                                && (currentJob.BlockCount == 0 || jobBlocksDone >= currentJob.BlockCount);
                ReportJobOutcome(complete ? JobOutcome.Completed : JobOutcome.Aborted,
                                 complete ? null : jobAbortReason ?? (failed ? (taskFailure != Orchestrator.TaskFailure.None ? Loc.F(StrTaskFailedReason, taskFailure) : Loc.T(StrTaskFailed))
                                 : noFieldContinuation ? (taskFailure != Orchestrator.TaskFailure.None ? Loc.F(StrTaskFailedSentHome, taskFailure)
                                                          : lastJobFailure != null ? Loc.F(StrReasonSentHome, lastJobFailure) : Loc.T(StrSentHomeLoadPower))
                                 : lastJobFailure ?? Loc.T(StrBlocksLeftUndone)));
            }
            jobAbortReason = null;
            var source = jobSource;
            currentJob = null;
            constructionOrder = null;
            constructionLegs.Clear();
            ResetStrafe();
            cPhase = ConstructionPhase.Idle;
            jobSource = JobSource.None;
            if (source == JobSource.StandAlone && jobBlocksDone == 0)
            {
                // Nothing got done (all unreachable / timed out): not straight back into the same trip
                lastConstructionScanFrame = MyAPIGateway.Session.GameplayFrameCounter;
                Log.Debug("Drone {0}: trip made no progress, next scan in {1} s", Entity.EntityId,
                          AutomataSession.GetConfig().ConstructionComputer.DroneScanIntervalSeconds);
            }
            else lastConstructionScanFrame = int.MinValue / 2;   // stand-alone: next trip right away
            // Orchestrator jobs: back home to standby (the task list normally ends there already)
            if (source == JobSource.Orchestrator && !IsAtRest() && (HasHomeConnector || ParkMode || ReturnsToDeparture) && activeFlightOrder == null && !IsGoingToRest)
                GoRest();
            // Cargo still aboard (a failed job, nothing to unload at its end): cleared before the next job
            if (source != JobSource.Housekeeping && currentJob == null) TryStartHousekeeping();
        }

        /// <summary>Stops the current job (any source): tools off, no restart until the next scan / auction.</summary>
        public void StopJob(string reason = null)
        {
            if (reason == null) reason = Loc.T(StrStopped);
            jobAbortReason = null;
            if (designatorCombat != DesignatorCombat.None)
            {
                // Engage / escort under way: AI behaviours off, our controller takes back (the caller sends it home)
                EndCombat(reason);
                ClearFlightOrder();
            }
            if (jobSource == JobSource.Orchestrator && currentJob != null) ReportJobOutcome(JobOutcome.Aborted, reason);
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
            noFieldContinuation = true;   // sent home (load, power...): straight home, no more work on the way
            // The tool is in the block: straight back out first, then home
            var current = taskIndex < currentJob.Tasks.Count ? currentJob.Tasks[taskIndex].Type : Orchestrator.TaskType.None;
            if (current == Orchestrator.TaskType.BackOut && taskStarted) { homeAfterBackOut = true; return; }
            if (Orchestrator.Task.KindOf(current) == Orchestrator.TaskKind.Tool && IsBackOutAt(taskIndex + 1))
            {
                homeAfterBackOut = true;
                NextTask(1);
                RunTasks();
                return;
            }
            int home = currentJob.IndexOf(Orchestrator.TaskType.ReturnHome, taskIndex);
            if (home < 0) { FinishJob(); return; }
            homeAfterBackOut = false;
            taskIndex = home;
            taskStarted = false;
            RunTasks();
        }

        /// <summary>Stand-alone: whole seconds until work resumes after "Stop"; 0 when no Stop pause is running.</summary>
        private int StopRemainingSeconds()
        {
            if (settings == null || settings.OperationMode != OperationMode.StandAlone) return 0;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now >= stopResumeFrame || now >= constructionPausedUntil) return 0;
            return (constructionPausedUntil - now + 59) / 60;
        }

        public void PauseConstruction(int ticks)
        {
            // Never shortens a pause already running (a Stop outlasts a later interruption)
            constructionPausedUntil = Math.Max(constructionPausedUntil, MyAPIGateway.Session.GameplayFrameCounter + ticks);
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
                    ResetRoute();
                }
                TaskStatus status = StepTask(starting);
                if (taskFailure != Orchestrator.TaskFailure.None) ReportTaskFailure();
                switch (status)
                {
                    case TaskStatus.Running:
                        return;
                    case TaskStatus.Done:
                        if (homeAfterBackOut && currentJob.Tasks[taskIndex].Type == Orchestrator.TaskType.BackOut)
                        {
                            homeAfterBackOut = false;
                            if (!JumpToReturnHome()) return;
                            break;
                        }
                        NextTask(1);
                        break;
                    case TaskStatus.SkipBlock:
                        ToolsOff();
                        strafeNext = false;
                        if (onFace && IsBackOutAt(taskIndex + 2))
                        {
                            // In a strafe chain the drone is still at a face: back out from here
                            forceBackOut = true;
                            NextTask(2);
                        }
                        else NextTask(IsBackOutAt(taskIndex + 2) ? 3 : 2);
                        // Nothing done and nothing aboard (every block skipped on a grind trip): no empty unload trip
                        if (jobBlocksDone == 0 && taskIndex < currentJob.Tasks.Count && currentJob.Tasks[taskIndex].Type == Orchestrator.TaskType.DockAt
                            && CargoUsedVolume() < 0.01)
                        {
                            int home = currentJob.IndexOf(Orchestrator.TaskType.ReturnHome, taskIndex);
                            if (home >= 0) { taskIndex = home; taskStarted = false; }
                        }
                        break;
                    case TaskStatus.ToReturnHome:
                        ToolsOff();
                        // Out of the block's way first when the tool was on it
                        if (currentJob.Tasks[taskIndex].Kind == Orchestrator.TaskKind.Tool && IsBackOutAt(taskIndex + 1))
                        {
                            homeAfterBackOut = true;
                            NextTask(1);
                            break;
                        }
                        // Mid strafe chain (a NavigateToolTo gave up): still at a face, back out first
                        if (onFace && currentJob.Tasks[taskIndex].Type == Orchestrator.TaskType.NavigateToolTo && IsBackOutAt(taskIndex + 2))
                        {
                            homeAfterBackOut = true;
                            forceBackOut = true;
                            strafeNext = false;
                            NextTask(2);
                            break;
                        }
                        if (!JumpToReturnHome()) return;
                        break;
                    case TaskStatus.Failed:
                        ToolsOff();
                        if (constructionOrder != null && IsConstructionOrderActive()) ClearFlightOrder();
                        // Only the way home failed: the work stands (completed, or the earlier reason); FinishJob tries home again
                        bool homeOnly = currentJob.Tasks[taskIndex].Type == Orchestrator.TaskType.ReturnHome;
                        if (homeOnly) Report(StrReturnHomeFailedTheJob, lastJobFailure ?? "?");   // (taskFailure is reported and reset by now)
                        FinishJob(!homeOnly);
                        lastConstructionScanFrame = MyAPIGateway.Session.GameplayFrameCounter;   // wait a scan interval
                        return;
                    case TaskStatus.Interrupted:
                        // A player order, Go home, power monitoring... took over: stop, and don't restart right away
                        Report(StrJobInterrupted);
                        StopJob(Loc.T(StrInterruptedPlayerOrderGoHome));
                        PauseConstruction(AutomataSession.GetConfig().ConstructionComputer.DroneScanIntervalSeconds * 60);
                        return;
                }
            }
        }

        private Orchestrator.TaskFailure taskFailure;
        private string lastJobFailure;   // the job's last task failure (its outcome report says why)
        private bool homeAfterBackOut;   // a tool task sent the job home: its BackOut runs first

        // Strafing along contiguous blocks: a BackOut is skipped when the next block has a face next to the current
        // one, same side, same plane; the next NavigateToolTo then slides sideways onto it, no turning.
        private bool strafeNext;         // the next NavigateToolTo strafes to strafeCell / strafeNormal
        private bool strafeUnit;         // ...staying on the same small-grid work unit (the tool position covers it)
        private bool siteUnit;           // the work site in use aims at a whole small-grid work unit
        private Vector3D siteShift;      // ...its tool sphere moved off the default spot (PreciseShift)
        private Vector3I strafeCell, strafeNormal;
        private long strafeGridId;
        private bool onFace;             // at a work face without having backed out (a strafe chain)
        private bool forceBackOut;       // a block of the chain was skipped: back out, no strafe planning

        private void ResetStrafe()
        {
            strafeNext = onFace = forceBackOut = false;
        }

        private bool IsBackOutAt(int index)
        {
            return index < currentJob.Tasks.Count && currentJob.Tasks[index].Type == Orchestrator.TaskType.BackOut;
        }

        // Skips ahead to the job's ReturnHome; false when the job ended (no ReturnHome left)
        private bool JumpToReturnHome()
        {
            int home = currentJob.IndexOf(Orchestrator.TaskType.ReturnHome, taskIndex + 1);
            if (home < 0) { FinishJob(); return false; }
            noFieldContinuation = true;   // a task sent it home (missing tool, cargo empty...): no more work this trip
            taskIndex = home;
            taskStarted = false;
            return true;
        }

        private TaskStatus Fail(Orchestrator.TaskFailure failure, TaskStatus then)
        {
            taskFailure = failure;
            return then;
        }

        private void ReportTaskFailure()
        {
            var task = currentJob.Tasks[taskIndex];
            // Remembered for this trip: not planned again by TryContinueInField
            if (task.Type == Orchestrator.TaskType.NavigateToolTo && taskIndex + 1 < currentJob.Tasks.Count)
            {
                var tool = currentJob.Tasks[taskIndex + 1];
                tripFailed.Add(new KeyValuePair<long, Vector3I>(tool.TargetEntityId, tool.TargetBlock.ToVector3I()));
                if (taskFailure == Orchestrator.TaskFailure.Unreachable && jobSource == JobSource.StandAlone)
                    RememberUnreachable(tool.TargetEntityId, tool.TargetBlock.ToVector3I());
            }
            else if (task.Kind == Orchestrator.TaskKind.Tool && task.TargetEntityId != 0)
                tripFailed.Add(new KeyValuePair<long, Vector3I>(task.TargetEntityId, task.TargetBlock.ToVector3I()));
            // Numbers within this job (one trip): the block being worked, and the task in the job's list
            Report(StrBlockTaskFailed, BlockNumber(taskIndex), currentJob.BlockCount,
                   taskIndex + 1, currentJob.Tasks.Count, task.Type, taskFailure);
            lastJobFailure = task.Type + ": " + taskFailure;
            if (taskFailure == Orchestrator.TaskFailure.Unreachable) BroadcastUnreachable(ref task);
            taskFailure = Orchestrator.TaskFailure.None;
        }

        #region Timing
        private const int UNREACHABLE_BROADCAST_TICKS = 10 * 60 * 60;   // the same spot is announced at most this often
        #endregion
        private readonly Dictionary<Vector3I, int> unreachableBroadcasts = new Dictionary<Vector3I, int>();

        /// <summary>
        /// "Use broadcast controller": a position the drone was sent to and can't reach is announced in chat with a GPS
        /// point (what, where), so the player can see what is in the way. Each spot (10 m cells) once per
        /// UNREACHABLE_BROADCAST_TICKS: a drone retrying the same site doesn't spam.
        /// </summary>
        private void BroadcastUnreachable(ref Orchestrator.Task task)
        {
            if (settings == null || !settings.UseBroadcastController) return;
            Vector3D at;
            string what = task.Type.ToString();
            if (task.Type == Orchestrator.TaskType.DockAt)
            {
                var c = task.TargetEntityId != 0 ? AnchorDirectory.Resolve(block, task.TargetEntityId) as IMyShipConnector : null;
                if (c == null) return;
                at = c.GetPosition();
                what = "dock " + c.CustomName;
            }
            else if (task.Type == Orchestrator.TaskType.ReturnHome)
            {
                var home = GetHomeConnector();
                if (home == null) return;
                at = home.GetPosition();
                what = "home " + home.CustomName;
            }
            else
            {
                IMyTerminalBlock anchor;
                MatrixD am;
                if (!ResolveTaskFrame(task.RelativeBeaconEntityId, out anchor, out am)) return;
                at = TaskToWorld(anchor, ref am, task.Position);
                if (at.LengthSquared() < 1) return;
            }
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            var cell = Vector3I.Floor(at / 10.0);
            int last;
            if (unreachableBroadcasts.TryGetValue(cell, out last) && now - last < UNREACHABLE_BROADCAST_TICKS) return;
            if (unreachableBroadcasts.Count > 64) unreachableBroadcasts.Clear();
            unreachableBroadcasts[cell] = now;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string name = (Nicknames.Of(_entityId) + " can't reach").Replace(':', ' ');
            Broadcast(Loc.F(StrCantReachChat, what, string.Format(inv, "GPS:{0}:{1:F1}:{2:F1}:{3:F1}:", name, at.X, at.Y, at.Z)));
        }

        // 1-based block of the job the task at 'index' belongs to (its NavigateToolTo counted), 0 before the first
        private int BlockNumber(int index)
        {
            int n = 0;
            for (int i = 0; i <= index && i < currentJob.Tasks.Count; i++)
                if (currentJob.Tasks[i].Type == Orchestrator.TaskType.NavigateToolTo) n++;
            return n;
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
                    lastCargoPortId = source.EntityId;
                    if (ClearsCargo) ClearCargo();   // leftovers of earlier work out first: it carries this load only
                    int planned = task.Inventory.GetTotalItemCount();
                    int moved = TransferToDrone(task.Inventory);
                    Log.Debug("Drone {0}: load at {1}: {2}/{3} items ({4})", Entity.EntityId, source.CustomName, moved, planned,
                              DescribeTop(task.Inventory, 6));
                    if (moved < planned && moved > 0 && currentJob.BlockCount == 0)
                    {
                        // Logistics: the supplier had less than it bid (used meanwhile): deliver what was loaded
                        Report(StrLoadedItemsAtTheRest, moved, planned, source.CustomName);
                        return TaskStatus.Done;
                    }
                    if (moved < planned)
                    {
                        // Materials gone meanwhile, no conveyor path, or cargo refused: don't fly a trip that can't be finished
                        UnloadCargo(task.Inventory);
                        UnloadCargo(null);
                        ReportProblem(Loc.F(StrCouldLoadOnly, moved, planned));
                        return Fail(Orchestrator.TaskFailure.InsufficientMaterials, TaskStatus.Failed);
                    }
                    lastConstructionProblem = null;
                    if (currentJob.BlockCount > 0) Report(StrJobBlocksLoadedParts, currentJob.BlockCount, moved);
                    else Report(StrLoadedItemsAt, moved, source.CustomName);
                    return TaskStatus.Done;
                }

                case Orchestrator.TaskType.UnloadInventory:
                {
                    var target = DockedConnector();
                    if (target == null) return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.Failed);
                    BuildHomeNetwork(target);
                    lastCargoPortId = target.EntityId;
                    // Mining: every ore aboard (stone, other ores: refineries take them), unless another unload follows
                    // (ice jobs: the ice at the O2/H2 generators' computer first, the rest at the refinery's)
                    bool allOre = currentJob.JobType == Orchestrator.JobType.MineOre && currentJob.IndexOf(Orchestrator.TaskType.UnloadInventory, taskIndex + 1) < 0;
                    if (!UnloadCargo(task.Inventory, allOre))
                    {
                        Log.Debug("Drone {0}: unload at {1}: no room for everything", Entity.EntityId, target.CustomName);
                        return Fail(Orchestrator.TaskFailure.NoCargoSpace, TaskStatus.Failed);
                    }
                    Log.Debug("Drone {0}: unloaded at {1} ({2})", Entity.EntityId, target.CustomName,
                              task.Inventory != null && !task.Inventory.IsEmpty() ? DescribeTop(task.Inventory, 6) : "all components");
                    return TaskStatus.Done;
                }

                case Orchestrator.TaskType.ClearCargo:
                    return StepClearCargo();

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
                        if (tool.Type == Orchestrator.TaskType.Mine && !mineSiteFixed) PlanMineSite(ref task, ref tool);   // angled bore for a sideways drill (rebores: set already)
                        var failure = DispatchToolTo(ref task, ref tool);
                        if (failure == Orchestrator.TaskFailure.MissingEquipment) return Fail(failure, TaskStatus.ToReturnHome);
                        return failure == Orchestrator.TaskFailure.None ? TaskStatus.Running : Fail(failure, TaskStatus.SkipBlock);
                    }
                    // No way round what appeared, or flying about without getting closer to the work site (each replan
                    // finds another way that leads nowhere): another face / approach, then the next block
                    string stuck = routeFailed ? "no route" : SiteGoalStalled() ? "no closer to the work site in " + SITE_STALL_TICKS / 60 + " s" : null;
                    if (stuck != null)
                    {
                        var again = RedispatchToolTo(ref task, ref tool, true, stuck);
                        if (again == Orchestrator.TaskFailure.None) return TaskStatus.Running;
                        return Fail(again, again == Orchestrator.TaskFailure.MissingEquipment ? TaskStatus.ToReturnHome : TaskStatus.SkipBlock);
                    }
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    if (constructionOrder.Arrived) return TaskStatus.Done;
                    // Pressed against something (the way in or a route leg ran into what the checks didn't see): back out,
                    // another face. Not a bore: the drill cuts its way.
                    Vector3D away;
                    if (tool.Type != Orchestrator.TaskType.Mine && PushBlocked(out away))
                        return AfterContact(ref task, ref tool, away, "pressed against something");
                    // Pitch / roll limits (or Align to P-Gravity) changed on the way: the site was chosen for the old
                    // ones (a face the drone can't hold now, or one it can reach better now): chosen again, same block
                    if (activeFlightOrder != constructionOrder && SiteLimitsChanged())
                    {
                        var again = RedispatchToolTo(ref task, ref tool, false, "pitch / roll limits changed");
                        if (again == Orchestrator.TaskFailure.None) return TaskStatus.Running;
                        return Fail(again, again == Orchestrator.TaskFailure.MissingEquipment ? TaskStatus.ToReturnHome : TaskStatus.SkipBlock);
                    }
                    // A bore starts wherever the drill touches: close to the surface point and not moving (the ground, an
                    // old hole's rim) is close enough; the bore line goes on from here
                    // Mining: aligned with the site (the final order, after the turn in place): drill on in terrain clearing
                    // mode, it cuts its way to the surface point; stopped close to it (the line's transit or the approach):
                    // the bore starts there
                    if (tool.Type == Orchestrator.TaskType.Mine && activeFlightOrder == constructionOrder)
                    {
                        SetDrillClearing(true);
                        SetToolEnabled(Orchestrator.TaskType.Mine, true);
                        if ((constructionOrder.Phase == FlightPhase.Approach || constructionOrder.Phase == FlightPhase.Transit)
                            && Vector3D.DistanceSquared(ReferencePoint(constructionOrder), constructionOrder.Target.ToVector3D()) < MINE_START_SLACK * MINE_START_SLACK
                            && flightState.RelativeVelocity.LengthSquared() < 0.25)
                            return TaskStatus.Done;
                    }
                    if (WatchProgress()) return TaskStatus.Running;
                    // Stalled: a bore gives the block up; work faces back out and try another face (a few times, then skip)
                    if (tool.Type == Orchestrator.TaskType.Mine) return Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.SkipBlock);
                    return AfterContact(ref task, ref tool, Vector3D.Zero, "no progress in " + StallTimeoutTicks / 60 + " s");
                }

                case Orchestrator.TaskType.BackOut:
                {
                    // Best effort: a back-out that can't be flown (or isn't needed) or stalls just moves on; the
                    // next route starts from wherever the drone is
                    if (starting)
                    {
                        bool force = forceBackOut;
                        forceBackOut = false;
                        // Contiguous next block, same side and plane: stay at the face, slide over to it instead
                        if (!force && !homeAfterBackOut && TryPlanStrafe()) return TaskStatus.Done;
                        onFace = false;
                        return DispatchBackOut(ref task) ? TaskStatus.Running : TaskStatus.Done;
                    }
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    if (constructionOrder.Arrived) return TaskStatus.Done;
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
                }

                case Orchestrator.TaskType.NavigateTo:
                {
                    if (starting)
                    {
                        var failure = DispatchNavigateTo(ref task);
                        return failure == Orchestrator.TaskFailure.None ? TaskStatus.Running : NavigateFailed(failure);
                    }
                    if (routeFailed) return NavigateFailed(Orchestrator.TaskFailure.Unreachable);
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    if (constructionOrder.Arrived) { scoutSkips = 0; return TaskStatus.Done; }
                    // Scouting: a fly-by. Close enough to the waypoint is there (no stop): the scan starts and the drone flies on
                    if (activeFlightOrder == constructionOrder && taskIndex + 1 < currentJob.Tasks.Count
                        && currentJob.Tasks[taskIndex + 1].Type == Orchestrator.TaskType.Survey
                        && Vector3D.DistanceSquared(ReferencePoint(constructionOrder), constructionOrder.Target.ToVector3D()) < SCOUT_PASS_DISTANCE * SCOUT_PASS_DISTANCE)
                    { scoutSkips = 0; return TaskStatus.Done; }
                    return WatchProgress() ? TaskStatus.Running : NavigateFailed(Orchestrator.TaskFailure.Timeout);
                }

                case Orchestrator.TaskType.Mine:
                    return RunMine(ref task, starting, elapsed);

                case Orchestrator.TaskType.Survey:
                    return RunSurvey(starting);

                case Orchestrator.TaskType.Engage:
                case Orchestrator.TaskType.Escort:
                    return RunDesignatorCombat(ref task, starting);

                case Orchestrator.TaskType.WeldBlock:
                case Orchestrator.TaskType.GrindBlock:
                {
                    if (starting)
                    {
                        var already = ToolTargetState(ref task);
                        if (already != Orchestrator.TaskFailure.None) return Fail(already, TaskStatus.Done);
                        if (!HasTool(task.Type)) return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                        // Small grid welds (any job source): until the cargo has nothing more for the block, since
                        // the welder's sphere also welds the neighbours with the parts loaded for this one
                        if (task.Type == Orchestrator.TaskType.WeldBlock
                            && task.Completion == Orchestrator.ToolTaskCompletionTrigger.BlockFullIntegrity
                            && IsSmallGridTarget(ref task))
                        {
                            task.Completion = Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty;
                            currentJob.Tasks[taskIndex] = task;
                        }
                        mineCargoStart = CargoUsedVolume();
                        ResetModelContact();
                        SetToolEnabled(task.Type, true);
                        return TaskStatus.Running;
                    }
                    if (!IsConstructionOrderActive()) return TaskStatus.Interrupted;
                    string why;
                    if (IsToolTaskDone(ref task, elapsed, out why))
                    {
                        Report(StrBlockDone, BlockNumber(taskIndex), currentJob.BlockCount, task.Type, why);
                        jobBlocksDone++;
                        SetToolEnabled(task.Type, false);   // one block at a time: off before moving on
                        return TaskStatus.Done;
                    }
                    if (task.Type != Orchestrator.TaskType.Mine) KeepToolOnModel(ref task, elapsed);   // a thin model: the sphere onto it
                    // A projected block that doesn't appear (not attachable after all - a predicted line whose block before didn't
                    // take - or something in its cells): on to the next one well before the task timeout
                    if (task.IsProjected && elapsed > PROJECTED_APPEAR_TICKS)
                    {
                        bool gone;
                        if (ToolTarget(ref task, out gone) == null)
                        {
                            SetToolEnabled(task.Type, false);
                            Log.Debug("Drone {0}: projected block at {1} not built after {2} s of welding: skipped", Entity.EntityId,
                                      task.TargetBlock.ToVector3I(), PROJECTED_APPEAR_TICKS / 60);
                            return Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
                        }
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
                        dockWaitTarget = 0;
                        var target = task.TargetEntityId != 0 ? AnchorDirectory.Resolve(block, task.TargetEntityId) as IMyShipConnector : null;
                        if (target == null) return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.ToReturnHome);
                        if (IsDockedAt(target.EntityId)) return TaskStatus.Done;
                        // Docked at another connector of the same network: the load / unload reaches the same containers from
                        // here (BuildHomeNetwork reads the whole logical group): no undock, no dock again
                        IMyShipConnector dockedHere;
                        if (DockedInNetworkOf(target, out dockedHere))
                        {
                            lastCargoPortId = dockedHere.EntityId;
                            Log.Debug("Drone {0}: already docked at {1}, same network as {2}: no new dock", Entity.EntityId, dockedHere.CustomName, target.CustomName);
                            return TaskStatus.Done;
                        }
                        ToolsOff();
                        string busy = ConnectorBusy(target);
                        if (busy != null)
                        {
                            // Taken (another drone parked there, a ship): a free one of the same station instead of waiting
                            var other = FreeSiblingConnector(target);
                            if (other == null) { lastCargoPortId = target.EntityId; return StartDockWait(target, busy); }
                            Report(StrDockUsingInstead, other.CustomName, target.CustomName, busy);
                            task.TargetEntityId = other.EntityId;
                            currentJob.Tasks[taskIndex] = task;
                            target = other;
                        }
                        lastCargoPortId = target.EntityId;   // somewhere to clear the cargo if the job fails later (after a load too)
                        if (!DockAtConnector(target)) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
                        constructionOrder = activeFlightOrder;
                        constructionLegs.Clear();
                        return TaskStatus.Running;
                    }
                    if (IsDockedAt(task.TargetEntityId)) return TaskStatus.Done;
                    if (dockWaitTarget != 0) return UpdateDockWait(task);
                    if (dockingHomeId == 0 && activeFlightOrder == null) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
                    if (dockingHomeId != task.TargetEntityId) return TaskStatus.Interrupted;   // Go home, a player order...
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.ToReturnHome);
                }

                case Orchestrator.TaskType.ReturnHome:
                {
                    if (starting)
                    {
                        ToolsOff();
                        if (InsertClearTripBeforeRest()) return TaskStatus.Running;   // dock + clear first (no home connector)
                        if (IsAtRest()) { ClearIfDocked(); return TaskStatus.Done; }
                        if (TryContinueInField()) return TaskStatus.Done;   // more blocks it carries the parts for: on with those
                        if (!GoRest()) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Failed);
                        if (parkPhase == ParkPhase.None)
                        {
                            constructionOrder = activeFlightOrder;
                            constructionLegs.Clear();
                        }
                        return TaskStatus.Running;
                    }
                    if (IsAtRest()) { ClearIfDocked(); return TaskStatus.Done; }   // home: cleared on arrival
                    if (dockingHomeId == 0 && parkPhase == ParkPhase.None && activeFlightOrder == null) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Failed);
                    if (!IsGoingToRest) return TaskStatus.Interrupted;                              // someone else's order
                    if (parkPhase != ParkPhase.None) return TaskStatus.Running;                     // the park watches itself
                    return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Failed);
                }

                default:
                    // DeliverMessage: not implemented yet
                    Log.Debug("Drone {0}: task {1} not supported yet, skipped", Entity.EntityId, task.Type);
                    return TaskStatus.Done;
            }
        }

        // Waiting for a connector another drone (or ship) is using: parked drones stay parked, flying ones hold where
        // they are; the connector is checked every update and docked at as soon as it is free.
        #region Timing
        private const int DOCK_WAIT_MAX_TICKS = 10 * 60 * 60;
        #endregion
        private const double SCOUT_PASS_DISTANCE = 15;   // m: a scout waypoint this close counts as reached
        private const int SCOUT_SKIPS_MAX = 3;           // area sweep: waypoints in a row it may skip (unreachable, stalled) before going home
        private const int SCOUT_SPACE_TIMEOUT_MIN = 120; // s: scout jobs out of natural gravity (asteroids): the stall timeout is at least this...
        private const int SCOUT_SPACE_TIMEOUT_FACTOR = 3;// ...and this x the drone's task timeout
        private int scoutSkips;

        // A NavigateTo that failed. An area sweep's waypoint (a Survey follows): skipped with its scan, up to
        // SCOUT_SKIPS_MAX in a row (a ring point inside an asteroid's rock ended the whole sweep); otherwise home
        private bool SweepWaypointActive()
        {
            return currentJob != null && currentJob.JobType == Orchestrator.JobType.DesignatorScoutArea
                   && taskIndex + 1 < currentJob.Tasks.Count && currentJob.Tasks[taskIndex + 1].Type == Orchestrator.TaskType.Survey;
        }

        private TaskStatus NavigateFailed(Orchestrator.TaskFailure failure)
        {
            bool sweepPoint = SweepWaypointActive();
            if (sweepPoint && ++scoutSkips <= SCOUT_SKIPS_MAX)
            {
                Log.Debug("Drone {0}: sweep waypoint {1}/{2} skipped ({3}), {4}/{5} in a row", Entity.EntityId, taskIndex / 2 + 1,
                          currentJob.Tasks.Count / 2, failure, scoutSkips, SCOUT_SKIPS_MAX);
                routeFailed = false;
                ClearFlightOrder();
                return Fail(failure, TaskStatus.SkipBlock);
            }
            return Fail(failure, TaskStatus.ToReturnHome);
        }

        // The stall timeout: the drone's task timeout; scout jobs out of natural gravity get longer (long legs between
        // asteroid ring points, the turn to face each one)
        private int StallTimeoutTicks
        {
            get
            {
                int t = TaskTimeoutTicks;
                if (currentJob != null && !flightState.InGravity
                    && (currentJob.JobType == Orchestrator.JobType.DesignatorScoutArea || currentJob.JobType == Orchestrator.JobType.DesignatorScoutPoint))
                    t = Math.Max(t * SCOUT_SPACE_TIMEOUT_FACTOR, SCOUT_SPACE_TIMEOUT_MIN * 60);
                return t;
            }
        }
        private long dockWaitTarget;
        private int dockWaitFrame;

        private const int DOCK_SIBLING_CHECK_TICKS = 5 * 60;

        /// <summary>
        /// A free, working, friendly connector of the same station (mechanical group) as 'busy' that the drone can dock at
        /// (connector size, facing in gravity), nearest 'busy' first; null when there is none.
        /// </summary>
        private IMyShipConnector FreeSiblingConnector(IMyShipConnector busy)
        {
            var faces = DockFaces();
            var smallFaces = DockFaces(true);
            if (faces.Count == 0 && smallFaces.Count == 0) return null;
            CaptureFlightState();
            RefreshDroneGrids();
            Vector3D g = flightState.InGravity ? flightState.Gravity : Vector3D.Zero;
            siblingGrids.Clear();
            busy.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(siblingGrids);
            IMyShipConnector best = null;
            double bestD = double.MaxValue;
            for (int i = 0; i < siblingGrids.Count; i++)
            {
                if (droneGrids.Contains(siblingGrids[i])) continue;
                foreach (var c in siblingGrids[i].GetFatBlocks<IMyShipConnector>())
                {
                    if (c == busy || !DockableFree(c, faces, smallFaces, g)) continue;
                    double d = Vector3D.DistanceSquared(c.GetPosition(), busy.GetPosition());
                    if (d < bestD) { bestD = d; best = c; }
                }
            }
            siblingGrids.Clear();
            return best;
        }
        private readonly List<IMyCubeGrid> siblingGrids = new List<IMyCubeGrid>();

        // A connector this drone may dock at right now: working, free, allowed owner, a drone connector of its size whose
        // face fits the connector's facing (gravity)
        private bool DockableFree(IMyShipConnector c, List<byte> faces, List<byte> smallFaces, Vector3D g)
        {
            if (c.Closed || !c.IsWorking || c.IsConnected || !AnchorDirectory.IsAllowed(block.OwnerId, c.OwnerId)) return false;
            var sized = Logistics.ConveyorNetwork.IsStandardConnector(c) ? faces : smallFaces;   // sizes mate with their own
            if (sized.Count == 0 || ConnectorBusy(c) != null) return false;
            Vector3D p, axis;
            ConnectorGeometry(c, out p, out axis);
            byte facing = Logistics.DockFit.FacingIn(axis, g);
            for (int f = 0; f < sized.Count; f++) if (Logistics.DockFit.Fits(sized[f], facing, flightState.InGravity)) return true;
            return false;
        }

        private TaskStatus StartDockWait(IMyShipConnector target, string busy)
        {
            dockWaitTarget = target.EntityId;
            dockWaitFrame = MyAPIGateway.Session.GameplayFrameCounter;
            if (!IsParkedNow() && preflightStage == 0)
            {
                CaptureFlightState();
                OrderGoTo(flightState.Position);   // hold here
            }
            Report(StrDockWaiting, target.CustomName, busy);
            return TaskStatus.Running;
        }

        private TaskStatus UpdateDockWait(Orchestrator.Task task)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            var target = AnchorDirectory.Resolve(block, dockWaitTarget) as IMyShipConnector;
            if (target == null) { dockWaitTarget = 0; return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.ToReturnHome); }
            if (ConnectorBusy(target) != null)
            {
                // Another connector of the station came free meanwhile: that one (checked every few seconds)
                if ((now - dockWaitFrame) % DOCK_SIBLING_CHECK_TICKS < 10)
                {
                    var other = FreeSiblingConnector(target);
                    if (other != null)
                    {
                        dockWaitTarget = 0;
                        Report(StrDockUsingInstead, other.CustomName, target.CustomName, ConnectorBusy(target));
                        var t = currentJob.Tasks[taskIndex];
                        t.TargetEntityId = other.EntityId;
                        currentJob.Tasks[taskIndex] = t;
                        lastCargoPortId = other.EntityId;
                        if (!DockAtConnector(other)) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
                        constructionOrder = activeFlightOrder;
                        constructionLegs.Clear();
                        taskProgressFrame = now;
                        taskBestDistance = double.MaxValue;
                        return TaskStatus.Running;
                    }
                }
                if (now - dockWaitFrame > DOCK_WAIT_MAX_TICKS)
                {
                    dockWaitTarget = 0;
                    Report(StrDockStillInUseAfter, target.CustomName, DOCK_WAIT_MAX_TICKS / 3600);
                    return Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.ToReturnHome);
                }
                return TaskStatus.Running;
            }
            dockWaitTarget = 0;
            Report(StrDockFreeAfterS, target.CustomName, (now - dockWaitFrame) / 60.0);
            if (!DockAtConnector(target)) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
            constructionOrder = activeFlightOrder;
            constructionLegs.Clear();
            taskProgressFrame = now;            // the stall clock starts with the docking flight
            taskBestDistance = double.MaxValue;
            return TaskStatus.Running;
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
            // Measured to what the order flies to now: the approach point while in transit / aligning there (a dock
            // order's connector stays ~DockApproachDistance away all that time), the target on the final approach
            bool toApproachPoint = o.UseApproachLine && (o.Phase == FlightPhase.Transit || o.Phase == FlightPhase.Align);
            double d = Vector3D.Distance(ReferencePoint(o), toApproachPoint ? o.ApproachFrom.ToVector3D() : o.Target.ToVector3D());
            if (o != watchedOrder)
            {
                // New leg (handover, replan): its own distance to measure progress against. The stall clock keeps
                // running: a replan every few seconds (drifting off, route watch) must not hide a drone getting nowhere
                watchedOrder = o;
                watchedPhase = o.Phase;
                taskBestDistance = d;
            }
            else if (o.Phase != watchedPhase)
            {
                // Next phase of the same order (transit -> align -> approach): progress, measured afresh
                watchedPhase = o.Phase;
                taskBestDistance = d;
                taskProgressFrame = now;
                return true;
            }
            // Progress: 0.5 m closer, or (close in) 30% closer: the last metre of a dock / work approach is slow
            if (d < taskBestDistance - Math.Min(0.5, Math.Max(0.05, taskBestDistance * 0.3)))
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
            if (stalled <= StallTimeoutTicks) return true;
            LogStall(o, d);
            NoteSpeedTimeout(Loc.T(StrTask));
            return false;
        }

        #region Work site retries
        // A block's work site (face + approach) is chosen once per flight there. Given up on (no route, or flying about
        // without getting closer: every just-in-time replan found another way that led nowhere - 24 min under a
        // platform deck once) it is chosen again without that face, a few times, before the block is skipped.
        // Pitch / roll limits changed on the way: chosen again too (same faces allowed).
        private const int SITE_STALL_TICKS = 90 * 60;     // no new closest distance to the work site for this long: stuck
        private const double SITE_PROGRESS = 2.0;         // m closer than the best so far (25% of it when closer than 8 m)
        private const int SITE_REDISPATCH_MAX = 3;        // sites chosen again per block before it is skipped

        private readonly List<Vector3I> siteRejectCells = new List<Vector3I>();
        private readonly List<Vector3I> siteRejectNormals = new List<Vector3I>();
        private readonly List<Vector3I> siteRejectUnitSides = new List<Vector3I>();   // small-grid work units: a whole side
        private long siteRejectGrid;
        private Vector3I siteRejectBlock;
        private int siteRedispatches;
        private bool siteChosen;            // DispatchToolTo picked siteCell / siteNormal (not a strafe / the planned face)
        private Vector3I siteCell, siteNormal;
        private double siteBestDistance = double.MaxValue;
        private int siteProgressFrame;
        private bool siteAlign;
        private float sitePitch, siteRoll;

        private void ClearSiteRejects(long gridId, Vector3I blockPos)
        {
            siteRejectCells.Clear();
            siteRejectNormals.Clear();
            siteRejectUnitSides.Clear();
            siteRejectGrid = gridId;
            siteRejectBlock = blockPos;
            siteRedispatches = 0;
        }

        private void RememberSiteLimits()
        {
            if (settings == null) return;
            siteAlign = settings.AlignToPGravity;
            sitePitch = settings.PGravityAlignMaxPitchDegrees;
            siteRoll = settings.PGravityAlignMaxRollDegrees;
        }

        private bool SiteLimitsChanged()
        {
            if (settings == null || !flightState.InGravity) return false;
            return siteAlign != settings.AlignToPGravity
                || (settings.AlignToPGravity && (Math.Abs(sitePitch - settings.PGravityAlignMaxPitchDegrees) > 0.5
                                                 || Math.Abs(siteRoll - settings.PGravityAlignMaxRollDegrees) > 0.5));
        }

        /// <summary>
        /// On the route to a work site (before the final approach order): has the tool point got closer to the site's
        /// approach point lately? Measured to the site itself, not to the current leg: every replan starts a new leg,
        /// which always looks like progress (WatchProgress).
        /// </summary>
        private bool SiteGoalStalled()
        {
            if (routeOwner != RouteOwner.FlyRoute || constructionOrder == null || activeFlightOrder == null || activeFlightOrder == constructionOrder)
                return false;
            Vector3D goal = flyApproach;
            if (flyAnchorId != 0)
            {
                var anchor = AnchorDirectory.Resolve(block, flyAnchorId);
                if (anchor == null) return false;
                MatrixD am = anchor.WorldMatrix;
                goal = AnchorToWorldPoint(ref am, flyApproach);
            }
            double d = Vector3D.Distance(flightState.Position + Vector3D.TransformNormal(flyRefOffset, flightState.WorldMatrix), goal);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (d < siteBestDistance - (siteBestDistance < 8 ? siteBestDistance * 0.25 : SITE_PROGRESS))
            {
                siteBestDistance = d;
                siteProgressFrame = now;
                return false;
            }
            return now - siteProgressFrame > SITE_STALL_TICKS;
        }

        // The work site chosen again ('rejectSite': without the face just tried); the route from where the drone is
        private Orchestrator.TaskFailure RedispatchToolTo(ref Orchestrator.Task nav, ref Orchestrator.Task tool, bool rejectSite, string why)
        {
            if (++siteRedispatches > SITE_REDISPATCH_MAX)
            {
                Log.Debug("Drone {0}: block {1}: {2}; {3} work sites tried, skipping it", Entity.EntityId, tool.TargetBlock.ToVector3I(), why, SITE_REDISPATCH_MAX);
                return Orchestrator.TaskFailure.Unreachable;
            }
            if (rejectSite && siteChosen)
            {
                if (siteUnit) siteRejectUnitSides.Add(siteNormal);
                else
                {
                    siteRejectCells.Add(siteCell);
                    siteRejectNormals.Add(siteNormal);
                }
            }
            Log.Debug("Drone {0}: block {1}: {2}; choosing its work site again{3} ({4}/{5})", Entity.EntityId, tool.TargetBlock.ToVector3I(), why,
                      rejectSite && siteChosen ? string.Format(" without face {0} side {1}", siteCell, siteNormal) : "", siteRedispatches, SITE_REDISPATCH_MAX);
            ClearFlightOrder();
            routeOwner = RouteOwner.None;
            constructionLegs.Clear();
            constructionOrder = null;
            ResetRoute();
            watchedOrder = null;
            taskBestDistance = double.MaxValue;
            taskProgressFrame = MyAPIGateway.Session.GameplayFrameCounter;
            var already = ToolTargetState(ref tool);
            if (already != Orchestrator.TaskFailure.None) return already;
            return DispatchToolTo(ref nav, ref tool);
        }
        #endregion

        #region Contact recovery
        // Stuck on the way to a work site (pressed against something, or no progress): back out first, then the site
        // chosen again without that face (RedispatchToolTo). The back-out is the first leg of the next FlyRoute.
        private const double CONTACT_BACK_OUT = 2.0;       // m: straight away from what it pushed against
        private const int CONTACT_BACK_OUT_TICKS = 120;    // the next FlyRoute within this, from about the same place
        private bool contactBackOutSet;
        private Vector3D contactBackOut, contactBackOutFrom;
        private int contactBackOutFrame;

        private TaskStatus AfterContact(ref Orchestrator.Task nav, ref Orchestrator.Task tool, Vector3D away, string why)
        {
            PlanContactBackOut(away);
            pushBlockedTicks = 0;
            var again = RedispatchToolTo(ref nav, ref tool, true, why);
            if (again == Orchestrator.TaskFailure.None) return TaskStatus.Running;
            // Skipped: the next block's route (if any, right away) still backs out first
            return Fail(again, again == Orchestrator.TaskFailure.MissingEquipment ? TaskStatus.ToReturnHome : TaskStatus.SkipBlock);
        }

        // 'away': the way out of the contact (world), zero when unknown
        private void PlanContactBackOut(Vector3D away)
        {
            CaptureFlightState();
            var o = activeFlightOrder;
            Vector3D hc = HullCentreWorld;
            contactBackOutFrom = hc;
            contactBackOutFrame = MyAPIGateway.Session.GameplayFrameCounter;
            contactBackOutSet = true;
            if (o != null && o == constructionOrder)
            {
                // On the way in: back along the line it came in on (checked clear), unless that is where it pushes
                Vector3D toApproach = o.ApproachFrom.ToVector3D() - ReferencePoint(o);
                if (toApproach.LengthSquared() > 1.0 && (away.LengthSquared() < 0.5 || Vector3D.Dot(toApproach, away) > 0))
                {
                    contactBackOut = hc + toApproach;
                    return;
                }
            }
            if (away.LengthSquared() < 0.5)
                away = legDir.LengthSquared() > 0.5 ? -legDir : flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Backward;
            contactBackOut = hc + Vector3D.Normalize(away) * CONTACT_BACK_OUT;
        }

        // FlyRoute (not docked): the planned back-out as the route's first leg, when it is still fresh and from here
        private void TakeContactBackOut(ref Vector3D start, List<Vector3D> points)
        {
            if (!contactBackOutSet) return;
            contactBackOutSet = false;
            if (MyAPIGateway.Session.GameplayFrameCounter - contactBackOutFrame > CONTACT_BACK_OUT_TICKS
                || Vector3D.DistanceSquared(start, contactBackOutFrom) > 9) return;
            Log.Debug("Drone {0}: backing out {1:F1} m first", Entity.EntityId, Vector3D.Distance(start, contactBackOut));
            start = contactBackOut;
            points.Add(start);
        }
        #endregion

        // Debug: where a navigation task stalled
        private void LogStall(FlightOrder o, double d)
        {
            Vector3D fwd, up;
            // The attitude the order ends in (holding the current one on the way would always read 0° off)
            if (o.Orientation == FlightOrientationMode.Explicit) { fwd = o.Forward.ToVector3D(); up = o.Up.ToVector3D(); }
            else BuildOrderFrame(o, out fwd, out up);
            MatrixD wm = flightState.WorldMatrix;
            double angle = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(wm.Forward, fwd), -1, 1)));
            double upAngle = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(wm.Up, up), -1, 1)));
            string which = dockingHomeId != 0 ? (o == dockingOrder ? "dock order" : "docking route leg")
                         : o == constructionOrder ? "final approach"
                         : constructionLegs.Contains(o) ? "route leg " + (constructionLegs.IndexOf(o) + 1) + "/" + constructionLegs.Count : "other order";
            int gyrosWorking = 0;
            for (int i = 0; i < gyroscopes.Count; i++) if (gyroscopes[i].Gyro.IsWorking) gyrosWorking++;
            double tiltNow = flightState.InGravity ? MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(wm.Up, flightState.GravityUp), -1, 1))) : 0;
            double tiltWanted = flightState.InGravity ? MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(up, flightState.GravityUp), -1, 1))) : 0;
            Log.Debug("Drone {0}: stalled on {1}, phase {2}: {3:F1} m from its target (best {4:F1} m), speed {5:F2} m/s, "
                      + "attitude off by {6:F0}° (forward) / {7:F0}° (up), tilt from level {8:F0}° (wanted {9:F0}°), turning {10:F2} rad/s "
                      + "(asked {11:F2}, gyro accel est. {12:F2} rad/s², {13}/{14} gyros working), {15} legs queued, gravity {16:F1} m/s²",
                      Entity.EntityId, which, o.Phase, d, taskBestDistance, flightState.LinearVelocity.Length(), angle, upAngle,
                      tiltNow, tiltWanted, flightState.AngularVelocity.Length(), lastDesiredRotSpeed, rotAlphaEstimate, gyrosWorking,
                      gyroscopes.Count, queuedLegs.Count + (nextLeg != null ? 1 : 0), flightState.InGravity ? flightState.Gravity.Length() : 0);
            // Why it doesn't move: what the controller asks, what caps it, what it can push with
            Log.Debug("Drone {0}: stall detail: command {1:F2} m/s (profile {2:F2} m/s x gate {3:F2}), order cap {4}, lane cap {5}, "
                      + "yielding {6}, economy {7}, accel cap {8:F2} m/s² (Max thrust {9:F0} %), thrust overrides {10}, "
                      + "face max F {11} B {12} L {13} R {14} U {15} D {16}, mass {17:F0} kg",
                      Entity.EntityId, commandedVelocity.Length(), diagAlong, diagGate, o.SpeedCap > 0 ? o.SpeedCap.ToString("F2") : "-",
                      laneSpeedCap < double.MaxValue ? laneSpeedCap.ToString("F2") : "-", siteYielding, ecoActive, AccelCap,
                      settings != null ? settings.MaxThrustPercent : 0, thrustOverridesActive,
                      DroneStatusDisplay.Force(EffectiveMax(Base6Directions.Direction.Forward)), DroneStatusDisplay.Force(EffectiveMax(Base6Directions.Direction.Backward)),
                      DroneStatusDisplay.Force(EffectiveMax(Base6Directions.Direction.Left)), DroneStatusDisplay.Force(EffectiveMax(Base6Directions.Direction.Right)),
                      DroneStatusDisplay.Force(EffectiveMax(Base6Directions.Direction.Up)), DroneStatusDisplay.Force(EffectiveMax(Base6Directions.Direction.Down)),
                      physicalMass);
            Log.Debug("Drone {0}: stall detail: dampeners {1}, throttle F {2:P0} B {3:P0} L {4:P0} R {5:P0} U {6:P0} D {7:P0}, "
                      + "integral F {8:F2} U {9:F2} R {10:F2}, line integral {11:F2} m/s, asked {12:F2} m/s, moving {13:F2} m/s that way, "
                      + "pressed {14:F1} s, thrust obstructed {15}, gear locked {16}, connector locked {17}, connector status {18}",
                      Entity.EntityId, shipController != null && shipController.DampenersOverride,
                      groupThrottle[(int)Base6Directions.Direction.Forward], groupThrottle[(int)Base6Directions.Direction.Backward],
                      groupThrottle[(int)Base6Directions.Direction.Left], groupThrottle[(int)Base6Directions.Direction.Right],
                      groupThrottle[(int)Base6Directions.Direction.Up], groupThrottle[(int)Base6Directions.Direction.Down],
                      integralForward, integralUp, integralRight, xtIntegral.Length(), pushAsked, pushMoved, pushBlockedTicks / 60.0,
                      thrustObstructedDir >= 0 ? ((Base6Directions.Direction)thrustObstructedDir).ToString() : "no",
                      AnyGearLocked(), AnyConnectorLocked(), ConnectorStatuses());
            Log.Debug("Drone {0}: stall detail: {1}", Entity.EntityId, ThrustAndContactDiag());
            // Turning to a work attitude it can't hold (tilted too far for its thrusters / gyros in gravity): later
            // approaches stay below that tilt (runtime only)
            if (o == constructionOrder && (o.Phase == FlightPhase.Align || o.DirectApproach) && flightState.InGravity && angle > 15 && tiltWanted > 20)
            {
                double limit = Math.Max(10, Math.Min(maxWorkTiltDegrees, tiltWanted - 10));
                if (limit < maxWorkTiltDegrees)
                {
                    maxWorkTiltDegrees = limit;
                    Log.Debug("Drone {0}: couldn't turn to a {1:F0}° tilt from level: work attitudes limited to {2:F0}° from now on",
                              Entity.EntityId, tiltWanted, limit);
                }
            }
        }

        // Stall log: each drone connector's status (Connectable: another connector's magnet holds it)
        private string ConnectorStatuses()
        {
            if (connectors.Count == 0) return "-";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < connectors.Count; i++)
            {
                if (i > 0) sb.Append('/');
                var c = connectors[i];
                sb.Append(c == null || c.Closed ? "gone" : !c.Enabled ? "off" : c.Status.ToString());
            }
            return sb.ToString();
        }

        private double maxWorkTiltDegrees = 180;   // learnt: steepest work attitude (tilt from level, gravity) it manages
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
        private const int WORK_SITE_ATTEMPTS = 6;     // faces tried when something else blocks the way in
        private const int SMALL_REACH_CELLS = 2;      // small grids: a block is worked through up to this many welded cells
        private const int APPROACHES_PER_FACE = 25;   // approach directions tried per face: all (straight + 3 tilts x 8 sides; each check is cheap)
        // Tilts (degrees from the face normal) of the approach: the work sphere stays on the face point, the drone
        // comes in at an angle (comfortable attitude, or past what is in the way straight above the face). Up to 50°
        // so the face's normal stays the dominant axis of the line (FaceOf, BackOut).
        private static readonly double[] APPROACH_TILTS = { 0, 20, 35, 50 };
        // Undersides: also nearly level, sliding in under an overhang (the drone can't fly up into a low gap)
        private static readonly double[] UNDERSIDE_TILTS = { 0, 35, 65, 80 };
        private readonly List<Vector3D> approachDirs = new List<Vector3D>();
        private readonly List<double> approachCosts = new List<double>();
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
            // Weld sites (any kind): the drone's real shape / the neighbours' real shapes where the boxes are in the way
            siteWeld = tool.Type == Orchestrator.TaskType.WeldBlock;
            siteByShape = 0;
            var spm = Pathfinding.PathfindingManager.Instance;
            if (spm != null)
            {
                spm.RealShapeRayBudget = REAL_SHAPE_RAYS_PER_DISPATCH;
                spm.RealShapeClears = 0;
                spm.RealShapeRays = 0;
            }
            siteChosen = false;
            RememberSiteLimits();
            siteBestDistance = double.MaxValue;
            siteProgressFrame = MyAPIGateway.Session.GameplayFrameCounter;
            Vector3D point = TaskToWorld(anchor, ref am, nav.Position);
            Vector3D dirW = TaskToWorld(anchor, ref am, nav.ApproachFrom) - point;
            if (dirW.LengthSquared() > 1e-4) dirW.Normalize();
            double hull = DroneRadius();

            IMyEntity e;
            var grid = tool.Type != Orchestrator.TaskType.Mine && tool.TargetEntityId != 0
                       && MyAPIGateway.Entities.TryGetEntityById(tool.TargetEntityId, out e) ? e as IMyCubeGrid : null;
            bool strafe = strafeNext && grid != null && grid.EntityId == strafeGridId;
            strafeNext = false;
            Vector3D dirA = dirW;   // the approach line (tilted from the face normal 'dirW' when that suits better)
            if (strafe)
            {
                // Contiguous with the block just worked: the face found when skipping its BackOut
                point = ConstructionComputer.FacePoint(grid, strafeCell, strafeNormal, out dirW);
                dirA = dirW;   // strafes only follow straight approaches
            }
            else if (grid != null)
            {
                Vector3I faceCell = Vector3I.Zero, normal = Vector3I.Zero, min, max;
                if (ConstructionComputer.TryGetBlockExtent(grid, tool.TargetBlock.ToVector3I(), out min, out max))
                {
                    // Built, started or still projected: its extent now. The drone needs the tool reach plus its own
                    // size clear along the normal; the planned side and the side the drone is on are preferred.
                    MatrixD areaMatrix = MatrixD.Identity;
                    Vector3D half;
                    bool hasArea = jobSource == JobSource.StandAlone && TryGetObservationArea(out areaMatrix, out half);
                    Vector3D areaCenter = hasArea ? areaMatrix.Translation : grid.WorldVolume.Center;
                    // Space: "top" as the scan planned it (a projecting projector's up, else the area's, else the grid's)
                    Vector3D topUp = ConstructionComputer.TopOf(grid, Vector3D.Zero, hasArea ? areaMatrix.Up : Vector3D.Zero);
                    // The approach line must be clear: tool reach + the closest approach point's room to turn (the work site
                    // check moves the point further out where something is in the way)
                    int clearCells = (int)Math.Ceiling((mount.WorkRadius + TurnRoomOut) / grid.GridSize);
                    Vector3D toDrone = flightState.Position - point;
                    if (toDrone.LengthSquared() > 1e-4) toDrone.Normalize();
                    Vector3D planned = dirW;
                    Vector3D gUp = flightState.InGravity ? flightState.GravityUp : Vector3D.Zero;
                    // A face costs what its most comfortable approach costs (tilts included)
                    Func<Vector3D, double> cost = w => BestApproachCost(ref mount, w, toDrone, gUp)
                                                     - (Vector3D.Dot(w, planned) > 0.9 ? 0.5 : 0);
                    // A face whose whole approach line is free first; any free face otherwise. Then the site itself:
                    // nothing else (another drone parked there, a ship) in the way in or at the approach point;
                    // otherwise the next best face.
                    var rejectedCells = new List<Vector3I>();
                    var rejectedNormals = new List<Vector3I>();
                    var rejectedSides = new List<Vector3I>();   // unit pass: a side tried is tried once (one aim point)
                    // Sites given up on earlier for this block (never reached: RedispatchToolTo) stay out
                    if (siteRejectGrid != grid.EntityId || siteRejectBlock != tool.TargetBlock.ToVector3I()) ClearSiteRejects(grid.EntityId, tool.TargetBlock.ToVector3I());
                    bool inUnitPass = false;
                    Func<Vector3I, Vector3I, bool> reject = (c, n) =>
                    {
                        if (rejectedSides.Contains(n)) return true;
                        if (inUnitPass && siteRejectUnitSides.Contains(n)) return true;   // a unit side: one aim point
                        for (int r = 0; r < rejectedCells.Count; r++)
                            if (rejectedCells[r] == c && rejectedNormals[r] == n) return true;
                        for (int r = 0; r < siteRejectCells.Count; r++)
                            if (siteRejectCells[r] == c && siteRejectNormals[r] == n) return true;
                        return false;
                    };
                    bool found = false;
                    // Small grids: one tool position covers the planner's whole 3x3x3 work unit - aim at the unit
                    // (the centre of its face), its own blocks don't count as in the way; a block the sphere can't
                    // reach from any unit face goes by its own faces
                    // Then (small grids) a face up to SMALL_REACH_CELLS out from the block, through what is welded around it: the
                    // sphere reaches a block or two deep. Last, the block's own faces.
                    Vector3I umin = min, umax = max;
                    bool smallWeld = tool.Type == Orchestrator.TaskType.WeldBlock && grid.GridSizeEnum == MyCubeSize.Small;
                    bool unitMode = smallWeld && BuildPlanner.TryUnitBox(grid, tool.TargetBlock.ToVector3I(), out umin, out umax);
                    if (unitMode) { umin = Vector3I.Min(umin, min); umax = Vector3I.Max(umax, max); }
                    else { umin = min; umax = max; }
                    Vector3I rmin = min - new Vector3I(SMALL_REACH_CELLS), rmax = max + new Vector3I(SMALL_REACH_CELLS);
                    for (int pass = unitMode ? 0 : smallWeld ? 1 : 2; pass < 3 && !found; pass++)
                    {
                    bool unitPass = pass <= 1;   // the sphere as deep as the hull allows, and it must reach the block
                    inUnitPass = unitPass;
                    Vector3I emin = pass == 0 ? umin : pass == 1 ? rmin : min, emax = pass == 0 ? umax : pass == 1 ? rmax : max;
                    rejectedCells.Clear();
                    rejectedNormals.Clear();
                    rejectedSides.Clear();
                    for (int attempt = 0; attempt < WORK_SITE_ATTEMPTS && !found; attempt++)
                    {
                        if (!ConstructionComputer.ChooseFace(grid, emin, emax, areaCenter, gUp, clearCells, cost, out faceCell, out normal, true, reject, topUp)
                            && !ConstructionComputer.ChooseFace(grid, emin, emax, areaCenter, gUp, clearCells, cost, out faceCell, out normal, false, reject, topUp))
                            break;   // walled in / no face left
                        Vector3I chosenCell = faceCell;
                        if (unitPass) faceCell = FaceCentreCell(emin, emax, normal);
                        Vector3D fn;
                        Vector3D fp = ConstructionComputer.FacePoint(grid, faceCell, normal, out fn);
                        MatrixD f;
                        Vector3D sc, ap;
                        // Straight in, else tilted: the cheapest approach whose way in is clear
                        ApproachCandidates(ref mount, grid, normal, fn, toDrone, gUp);
                        Log.Debug("Drone {0}: block {1}{6}: face {2} side {3} (up {4:F2}), {5} approaches to try",
                                  Entity.EntityId, tool.TargetBlock.ToVector3I(), faceCell, normal, Vector3D.Dot(fn, gUp.LengthSquared() > 0.5 ? gUp : topUp),
                                  Math.Min(approachDirs.Count, APPROACHES_PER_FACE),
                                  pass == 0 ? string.Format(" (unit {0}..{1})", emin, emax) : pass == 1 ? string.Format(" (through welded blocks, {0}..{1})", emin, emax) : "");
                        string firstWhy = null, lastWhy = null;
                        int refused = 0;
                        bool sideRefused = false;   // an attitude problem (hull into the face plane): the whole side, not this cell
                        for (int k = 0; k < approachDirs.Count && k < APPROACHES_PER_FACE && !found; k++)
                        {
                            if (WorkSite(ref mount, fp, fn, approachDirs[k], true, true, true, grid, emin, emax, unitPass, Vector3D.Zero, out f, out sc, out ap)
                                && (!unitPass || SphereReaches(grid, sc, mount.WorkRadius, min, max)))
                            {
                                found = true;
                                dirA = approachDirs[k];
                                siteCell = chosenCell;
                                siteNormal = normal;
                                siteChosen = true;
                                siteUnit = unitPass;
                                siteShift = !unitPass && tool.Type == Orchestrator.TaskType.WeldBlock && grid.GridSizeEnum == MyCubeSize.Large
                                            ? PreciseShift(ref mount, grid, min, max, fp, fn, normal, dirA, ref f, sc) : Vector3D.Zero;
                                siteShift = ShiftFits(ref mount, fp, fn, dirA, grid, emin, emax, siteShift);
                            }
                            else if (unitPass && workSiteWhy == null)
                            {
                                refused++;
                                string why = string.Format("tilt {0:F0}°: the tool sphere wouldn't reach the block from the unit's face", TiltDegrees(approachDirs[k], fn));
                                if (firstWhy == null) firstWhy = why; else lastWhy = why;
                            }
                            else
                            {
                                refused++;
                                if (workSiteHullPastFace) sideRefused = true;
                                string why = string.Format("tilt {0:F0}°: {1}", TiltDegrees(approachDirs[k], fn), workSiteWhy ?? "refused");
                                if (firstWhy == null) firstWhy = why; else lastWhy = why;
                            }
                        }
                        if (refused > 0)
                            Log.Debug("Drone {0}:   {1} approaches refused{2}; first: {3}{4}", Entity.EntityId, refused,
                                      found ? " before one fit" : "", firstWhy, lastWhy != null ? "; last: " + lastWhy : "");
                        if (!found)
                        {
                            rejectedCells.Add(chosenCell);
                            rejectedNormals.Add(normal);
                            if (unitPass || sideRefused) rejectedSides.Add(normal);   // next attempt: another side
                        }
                    }
                    }
                    if (!found) return Orchestrator.TaskFailure.Unreachable;
                }
                else
                {
                    // Projection gone (projector off...): the planned face, if its outside cell is still free
                    if (!ConstructionComputer.FaceOf(grid, point, dirW, out faceCell, out normal) || grid.CubeExists(faceCell + normal))
                        return Orchestrator.TaskFailure.Unreachable;
                    // The planned (possibly tilted) approach line stays
                }
                point = ConstructionComputer.FacePoint(grid, faceCell, normal, out dirW);
                if (Vector3D.Dot(dirA, dirW) < 0.15) dirA = dirW;   // (tilts up to 80°: undersides)
            }
            if (dirW.LengthSquared() < 1e-4) dirW = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Backward;
            dirW.Normalize();
            if (dirA.LengthSquared() < 1e-4) dirA = dirW;
            dirA.Normalize();

            MatrixD frame;
            Vector3D sphereCentre, approach;
            if (strafe)
            {
                siteUnit = strafeUnit;
                siteShift = Vector3D.Zero;
                Vector3I smin, smax;
                if (!strafeUnit && tool.Type == Orchestrator.TaskType.WeldBlock && grid.GridSizeEnum == MyCubeSize.Large
                    && ConstructionComputer.TryGetBlockExtent(grid, tool.TargetBlock.ToVector3I(), out smin, out smax))
                {
                    // Strafing along a face: the sphere placed as precisely as for a fresh approach
                    MatrixD f0;
                    Vector3D sc0, ap0;
                    WorkSite(ref mount, point, dirW, dirA, true, false, false, null, Vector3I.Zero, Vector3I.Zero, false, Vector3D.Zero, out f0, out sc0, out ap0);
                    siteShift = PreciseShift(ref mount, grid, smin, smax, point, dirW, strafeNormal, dirA, ref f0, sc0);
                    siteShift = ShiftFits(ref mount, point, dirW, dirA, grid, smin, smax, siteShift);
                }
            }
            else if (grid == null) { siteUnit = false; siteShift = Vector3D.Zero; }
            WorkSite(ref mount, point, dirW, dirA, grid != null, false, grid != null && !strafe, null, Vector3I.Zero, Vector3I.Zero, siteUnit, siteShift,
                     out frame, out sphereCentre, out approach);
            nav.Position = WorldToTask(anchor, ref am, point);   // the face actually used
            siteWatchCenter = point;
            siteWatchCenterSet = true;
            nav.ApproachFrom = WorldToTask(anchor, ref am, approach);
            currentJob.Tasks[taskIndex] = nav;
            if (IsBackOutAt(taskIndex + 2))
            {
                var back = currentJob.Tasks[taskIndex + 2];
                back.RelativeBeaconEntityId = nav.RelativeBeaconEntityId;
                back.Position = back.ApproachFrom = nav.ApproachFrom;
                currentJob.Tasks[taskIndex + 2] = back;
            }

            if (strafe)
            {
                onFace = true;
                if (!FlyStrafe(anchor, ref am, sphereCentre, ref mount, ref frame)) return Orchestrator.TaskFailure.Unreachable;
                Log.Debug("Drone {0}: {1} at {2}, strafing to face point {3}", Entity.EntityId, tool.Type, tool.TargetBlock.ToVector3I(), point);
                return Orchestrator.TaskFailure.None;
            }
            onFace = false;
            var failure = FlyRoute(anchor, ref am, sphereCentre, approach, hull, mount.LocalPoint, ref frame, grid != null);
            if (failure == Orchestrator.TaskFailure.None)
                Log.Debug("Drone {0}: {1} at {2}, face point {3}, normal {4}, approach tilted {5:F0}°, {6:F1} m out, work attitude {7}{8}", Entity.EntityId, tool.Type,
                          tool.TargetBlock.ToVector3I(), point, dirW, MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(dirA, dirW), -1, 1))),
                          Vector3D.Distance(approach, sphereCentre), DescribeAttitude(ref frame), SiteShapeNote());
            return failure;
        }

        // Plain NavigateTo: the controller to Position, in along ApproachFrom -> Position, level
        // A NavigateTo that finds no route repositions and tries again (DroneControllerBlock.Recovery)
        private Orchestrator.TaskFailure DispatchNavigateTo(ref Orchestrator.Task nav)
        {
            ResetRecovery();
            bool was = recoverPlanning;
            recoverPlanning = true;
            try { return DispatchNavigateToCore(ref nav); }
            finally { recoverPlanning = was; }
        }

        private Orchestrator.TaskFailure DispatchNavigateToCore(ref Orchestrator.Task nav)
        {
            IMyTerminalBlock anchor;
            MatrixD am;
            if (!ResolveTaskFrame(nav.RelativeBeaconEntityId, out anchor, out am)) return Orchestrator.TaskFailure.TargetNotFound;
            CaptureFlightState();
            RefreshDroneGrids();
            Vector3D target = TaskToWorld(anchor, ref am, nav.Position);
            Vector3D approach = TaskToWorld(anchor, ref am, nav.ApproachFrom);
            if (Vector3D.DistanceSquared(target, approach) < 0.01) approach = target;   // no approach line: the route leads to the target
            Vector3D heading = target - approach;
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            heading -= gUp * Vector3D.Dot(heading, gUp);
            if (heading.LengthSquared() < 1e-4) heading = flightState.WorldMatrix.Forward;
            MatrixD frame = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(heading), gUp);
            return FlyRoute(anchor, ref am, target, approach, DroneRadius(), Vector3D.Zero, ref frame, false);
        }

        /// <summary>
        /// At a BackOut: can the drone slide straight to the next block instead? Yes when the next pair works the
        /// same grid with the same tool, that block still needs work, and it has a free face next to the current
        /// one, on the same side and in the same plane (<see cref="ConstructionComputer.FindAdjacentFace"/>).
        /// Then the BackOut is skipped and the next NavigateToolTo strafes. Otherwise the drone backs out: the
        /// next block isn't contiguous, or the job's blocks are done.
        /// </summary>
        private bool TryPlanStrafe()
        {
            strafeNext = false;
            int prevNavIndex = taskIndex - 2, prevToolIndex = taskIndex - 1, navIndex = taskIndex + 1, toolIndex = taskIndex + 2;
            if (prevNavIndex < 0 || toolIndex >= currentJob.Tasks.Count) return false;
            var prevNav = currentJob.Tasks[prevNavIndex];
            var prevTool = currentJob.Tasks[prevToolIndex];
            var nav = currentJob.Tasks[navIndex];
            var tool = currentJob.Tasks[toolIndex];
            if (prevNav.Type != Orchestrator.TaskType.NavigateToolTo || nav.Type != Orchestrator.TaskType.NavigateToolTo) return false;
            if (tool.Kind != Orchestrator.TaskKind.Tool || tool.Type == Orchestrator.TaskType.Mine || tool.Type != prevTool.Type) return false;
            if (tool.TargetEntityId == 0 || tool.TargetEntityId != prevTool.TargetEntityId) return false;
            if (nav.RelativeBeaconEntityId != prevNav.RelativeBeaconEntityId) return false;
            if (ToolTargetState(ref tool) != Orchestrator.TaskFailure.None) return false;   // done meanwhile: skipped anyway

            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(tool.TargetEntityId, out e) ? e as IMyCubeGrid : null;
            if (grid == null) return false;
            IMyTerminalBlock anchor;
            MatrixD am;
            if (!ResolveTaskFrame(prevNav.RelativeBeaconEntityId, out anchor, out am)) return false;
            // The face the drone is at (its NavigateToolTo holds the face actually used)
            Vector3D point = TaskToWorld(anchor, ref am, prevNav.Position);
            Vector3D dir = TaskToWorld(anchor, ref am, prevNav.ApproachFrom) - point;
            if (dir.LengthSquared() < 1e-4) return false;
            dir.Normalize();
            Vector3I fromCell, normal, min, max, next;
            if (!ConstructionComputer.FaceOf(grid, point, dir, out fromCell, out normal)) return false;
            if (Vector3D.Dot(dir, Vector3D.TransformNormal((Vector3D)normal, grid.WorldMatrix)) < 0.99) return false;   // came in tilted: back out
            strafeUnit = false;
            if (siteUnit && tool.Type == Orchestrator.TaskType.WeldBlock && grid.GridSizeEnum == MyCubeSize.Small)
            {
                // Same small-grid work unit: the tool position stays (it covers the unit)
                Vector3I pmin, pmax, nmin, nmax;
                if (BuildPlanner.TryUnitBox(grid, prevTool.TargetBlock.ToVector3I(), out pmin, out pmax)
                    && BuildPlanner.TryUnitBox(grid, tool.TargetBlock.ToVector3I(), out nmin, out nmax) && pmin == nmin)
                {
                    strafeNext = true;
                    strafeUnit = true;
                    strafeCell = fromCell;
                    strafeNormal = normal;
                    strafeGridId = grid.EntityId;
                    onFace = true;
                    Log.Debug("Drone {0}: next block in the same unit ({1}), staying", Entity.EntityId, tool.TargetBlock.ToVector3I());
                    return true;
                }
            }
            if (!ConstructionComputer.TryGetBlockExtent(grid, tool.TargetBlock.ToVector3I(), out min, out max)) return false;
            ToolMount mount = tool.Type == Orchestrator.TaskType.GrindBlock ? grinderMount : welderMount;
            int clearCells = (int)Math.Ceiling((mount.WorkRadius + TurnRoomOut) / grid.GridSize);
            if (!ConstructionComputer.FindAdjacentFace(grid, min, max, fromCell, normal, clearCells, out next)) return false;
            strafeNext = true;
            strafeCell = next;
            strafeNormal = normal;
            strafeGridId = grid.EntityId;
            onFace = true;
            ToolsOff();
            Log.Debug("Drone {0}: next block contiguous ({1} -> {2}), strafing instead of backing out", Entity.EntityId, fromCell, next);
            return true;
        }

        /// <summary>
        /// Strafe: the tool's work point straight sideways to 'target' (the next block's work point), attitude held
        /// (same face side, so the same work attitude). Slow: it moves along the surface.
        /// </summary>
        private bool FlyStrafe(IMyTerminalBlock anchor, ref MatrixD am, Vector3D target, ref ToolMount mount, ref MatrixD frame)
        {
            CaptureFlightState();
            ToolsOff();
            bool world = anchor == null;
            Vector3D here = flightState.Position + Vector3D.TransformNormal(mount.LocalPoint, flightState.WorldMatrix);
            var order = world ? OrderGoTo(target, here)
                              : OrderGoToRelative(anchor, WorldToAnchorPoint(ref am, target), WorldToAnchorPoint(ref am, here));
            if (order == null) return false;
            if (world) order.TransitStart = order.ApproachFrom;
            else order.TransitStartLocal = order.ApproachFromLocal;
            MakeDirect(order);
            order.ReferenceOffset = Vector3DData.FromVector3D(mount.LocalPoint);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = SafeSpeed;
            order.ArrivalTolerance = 0.3f;
            SetExplicitAttitude(order, world, ref am, frame.Forward, frame.Up);
            constructionOrder = order;
            constructionLegs.Clear();
            return true;
        }

        // Tool orders: straight into the final approach on their line (no transit / align hold at the approach point)
        private static void MakeDirect(FlightOrder o)
        {
            o.DirectApproach = true;
            o.HoldAttitudeInTransit = true;   // MatchSpeed (anchored): keeps the attitude it has
            if (o.Phase != FlightPhase.Transit && o.Phase != FlightPhase.Align) return;   // MatchSpeed: enters Approach after it
            o.Phase = FlightPhase.Approach;
            o.Captured = true;
        }

        /// <summary>
        /// BackOut: the tool's work point (preceding tool task's mount) straight back to Position, holding the
        /// attitude it worked with - the drone reverses out the way it came in. False: nothing to fly.
        /// </summary>
        private bool DispatchBackOut(ref Orchestrator.Task task)
        {
            IMyTerminalBlock anchor;
            MatrixD am;
            if (!ResolveTaskFrame(task.RelativeBeaconEntityId, out anchor, out am)) return false;
            var toolType = taskIndex > 0 ? currentJob.Tasks[taskIndex - 1].Type : Orchestrator.TaskType.WeldBlock;
            ToolMount mount = toolType == Orchestrator.TaskType.GrindBlock ? grinderMount
                            : toolType == Orchestrator.TaskType.Mine ? drillMount : welderMount;
            CaptureFlightState();
            ToolsOff();
            bool world = anchor == null;
            Vector3D target = TaskToWorld(anchor, ref am, task.Position);
            Vector3D here = flightState.Position + Vector3D.TransformNormal(mount.LocalPoint, flightState.WorldMatrix);
            if (Vector3D.DistanceSquared(here, target) < CONSTRUCTION_ROUTE_TOLERANCE * CONSTRUCTION_ROUTE_TOLERANCE) return false;
            // Approach line from where the tool is now, straight back
            var order = world ? OrderGoTo(target, here)
                              : OrderGoToRelative(anchor, WorldToAnchorPoint(ref am, target), WorldToAnchorPoint(ref am, here));
            if (order == null) return false;
            if (world) order.TransitStart = order.ApproachFrom;
            else order.TransitStartLocal = order.ApproachFromLocal;
            MakeDirect(order);
            order.ReferenceOffset = Vector3DData.FromVector3D(mount.LocalPoint);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = ApproachSpeed;   // moving away from the face: no need to crawl (was Safe speed: ~20 s for 5 m)
            order.ArrivalTolerance = CONSTRUCTION_ROUTE_TOLERANCE;
            order.LooseArrival = true;
            SetExplicitAttitude(order, world, ref am, flightState.WorldMatrix.Forward, flightState.WorldMatrix.Up);
            constructionOrder = order;
            constructionLegs.Clear();
            return true;
        }

        /// <summary>m: longest side of the drone's hull box (whole mechanical group).</summary>
        private double DroneLength()
        {
            if (localHullBox.Min.X > localHullBox.Max.X) return 2 * DroneRadius();   // not measured yet
            Vector3D size = localHullBox.Size;
            return Math.Max(size.X, Math.Max(size.Y, size.Z));
        }

        /// <summary>
        /// m: the room the drone needs to turn at an approach point: its length + MANOEUVRE_MARGIN. Approach points
        /// (tool work, docking) lie at least this far out, so the drone turns clear of what it works on.
        /// </summary>
        public double ManoeuvreDistance()
        {
            return DroneLength() + MANOEUVRE_MARGIN;
        }

        private const double MANOEUVRE_MARGIN = 2.5;

        // m: room a work site needs in front of the face for the drone to turn in place (hull radius + margin)
        private double TurnRoomOut { get { return Math.Min(ManoeuvreDistance(), HullRadius + TURN_ROOM_MARGIN); } }

        /// <summary>
        /// Orders the flight to 'target' (the point 'referenceOffset' is placed on), in along approach -> target with
        /// the attitude of 'frame'. Backs out of the dock first; the way to the approach point comes from the
        /// pathfinder (PlanPath: around the observation area, around obstacles, over terrain). The way to
        /// the approach point is flown as the drone is oriented (translation only); it turns to 'frame' only there.
        /// 'checkApproachLine': the approach -> target line must be clear too (tool work), else Unreachable.
        /// </summary>
        private Orchestrator.TaskFailure FlyRoute(IMyTerminalBlock anchor, ref MatrixD am, Vector3D target, Vector3D approach,
                                                  double hull, Vector3D referenceOffset, ref MatrixD frame, bool checkApproachLine)
        {
            bool world = anchor == null;
            constructionLegs.Clear();
            routePoints.Clear();
            // The route steers the hull box centre (turns about it sweep the least). At the approach point, with the
            // work attitude, the hull centre sits here:
            Vector3D hullAtApproach = approach - Vector3D.TransformNormal(referenceOffset - HullCentreLocal, frame);
            Vector3D start = HullCentreWorld;
            var docked = DockedConnector();
            // Out of the dock first: straight out along the connector axis, or the hangar's corridor (DeparturePoints)
            backOutSpeedCap = 0;
            if (docked != null) { contactBackOutSet = false; DeparturePoints(docked, ref start, routePoints); }
            else
            {
                TakeContactBackOut(ref start, routePoints);   // stuck against something: away from it first
                ClimbOut(ref start, hull, routePoints);       // in a bore hole: out of it first
            }
            // Stand-alone, coming from outside the observation area (the dock, elsewhere): around the area and in
            // only where the work is, not across what has been built there. Every stretch: the pathfinder.
            int backOutLegs = routePoints.Count;
            undockLegs = docked != null ? backOutLegs : 2;
            if (!PlanPath(start, hullAtApproach, routePoints, jobSource == JobSource.StandAlone))
                return Orchestrator.TaskFailure.Unreachable;
            if (checkApproachLine)
            {
                // Straight in along the normal: nothing between the approach point and the work point
                double top;
                Vector3D up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
                if (SegmentObstacleTop(approach, target, up, 0, 0, out top))
                {
                    Log.Debug("Drone {0}: approach line blocked", Entity.EntityId);
                    return Orchestrator.TaskFailure.Unreachable;
                }
            }
            // Legs (back-out of the dock, detours, the approach point) with the pathfinder's attitudes, a turn in
            // place at the approach point to the work attitude, then the final order: the reference point (tool)
            // straight in from the approach point.
            if (BuildHullLegs(anchor, ref am, routePoints, backOutLegs, hullAtApproach, frame.Forward, frame.Up, constructionLegs) == null)
                return Orchestrator.TaskFailure.TargetNotFound;
            routePoints.Clear();
            FlightOrder order = QueueGoTo(target, Pathfinding.WaypointBehavior.FullStop, 0);
            order.UseApproachLine = true;
            // After the turn the reference point is on (or within the loose hand-over of) the approach point: straight
            // into the final approach, the strict line control (cross-track gain + integral) pulls it onto the line and
            // holds the advance until it is on it and aligned. (Was a zero-length transit that had to settle within
            // 0.5 m first, with a weak cross-track push only: drones hovered there until the task timed out.)
            if (world) order.ApproachFrom = order.TransitStart = Vector3DData.FromVector3D(approach);
            else order.ApproachFromLocal = order.TransitStartLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref am, approach));
            order.Phase = FlightPhase.Transit;
            MakeDirect(order);
            ToolsOff();   // after the order: waking from the dock restores blocks, tools included
            constructionOrder = order;
            order.ReferenceOffset = Vector3DData.FromVector3D(referenceOffset);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = ApproachSpeed;
            order.ArrivalTolerance = 0.3f;
            SetExplicitAttitude(order, world, ref am, frame.Forward, frame.Up);
            // Without a checked approach line (plain NavigateTo) the final stretch is watched too
            order.Watched = !checkApproachLine && Vector3D.DistanceSquared(approach, target) > 1;
            RememberFlyRoute(anchor, ref am, target, approach, hull, referenceOffset, ref frame, checkApproachLine);
            return Orchestrator.TaskFailure.None;
        }

        // Fixed attitude for an order: in the anchor's frame (follows a moving grid), or world
        // (Align to P-Gravity, in gravity: held within the pitch / roll limits here, once - not re-clamped while flying, so
        // an anchored attitude turns with its grid. Work, park, route and corridor frames are within them already.)
        private void SetExplicitAttitude(FlightOrder o, bool world, ref MatrixD am, Vector3D forward, Vector3D up)
        {
            o.Orientation = FlightOrientationMode.Explicit;
            o.IgnoreGravityLimits = true;
            if (settings != null && settings.AlignToPGravity && flightState.InGravity)
            {
                Vector3D gUp = flightState.GravityUp, cur = flightState.WorldMatrix.Forward;
                forward.Normalize();
                ClampToGravityLimits(ref forward, ref up, ref gUp, ref cur);
            }
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
        /// <summary>
        /// Approach directions for the face with grid normal 'normal' (world 'n'), cheapest first, into approachDirs:
        /// straight along the normal, then tilted (APPROACH_TILTS) towards the 8 directions across the face. Cost:
        /// the attitude the tool direction needs, the tilt, the side the drone is on. Never from below in gravity.
        /// </summary>
        private void ApproachCandidates(ref ToolMount mount, IMyCubeGrid grid, Vector3I normal, Vector3D n, Vector3D toDrone, Vector3D gUp)
        {
            approachDirs.Clear();
            approachCosts.Clear();
            Vector3D e1 = Vector3D.TransformNormal((Vector3D)Base6Directions.GetIntVector(
                Base6Directions.GetPerpendicular(Base6Directions.GetDirection(normal))), grid.WorldMatrix);
            Vector3D e2 = Vector3D.Cross(n, e1);
            bool inGravity = gUp.LengthSquared() > 0.5;
            bool underside = inGravity && Vector3D.Dot(n, gUp) < -0.7;   // a face looking down: only from below
            var tilts = underside ? UNDERSIDE_TILTS : APPROACH_TILTS;
            for (int t = 0; t < tilts.Length; t++)
            {
                double tilt = MathHelperD.ToRadians(tilts[t]);
                int azimuths = t == 0 ? 1 : 8;
                for (int a = 0; a < azimuths; a++)
                {
                    Vector3D across = a == 0 ? e1 : a == 1 ? -e1 : a == 2 ? e2 : a == 3 ? -e2
                                    : Vector3D.Normalize((a == 4 || a == 5 ? e1 : -e1) + (a == 4 || a == 6 ? e2 : -e2));
                    Vector3D d = t == 0 ? n : n * Math.Cos(tilt) + across * Math.Sin(tilt);
                    if (inGravity && !underside && Vector3D.Dot(d, gUp) < -0.3) continue;   // not from below (but undersides)
                    if (inGravity && AttitudeCost(ref mount, -d) * 90 > maxWorkTiltDegrees) continue;   // tilts it couldn't hold before
                    double cost = ApproachCost(ref mount, d, tilts[t], toDrone);
                    int at = approachCosts.Count;
                    while (at > 0 && approachCosts[at - 1] > cost) at--;
                    approachCosts.Insert(at, cost);
                    approachDirs.Insert(at, d);
                }
            }
            // Straight in is always among those tried, whatever its cost
            if (approachDirs.Count > APPROACHES_PER_FACE)
            {
                int straight = -1;
                for (int i = 0; i < approachDirs.Count && straight < 0; i++)
                    if (Vector3D.Dot(approachDirs[i], n) > 0.9999) straight = i;
                if (straight >= APPROACHES_PER_FACE)
                {
                    Vector3D d = approachDirs[straight];
                    double c = approachCosts[straight];
                    approachDirs.RemoveAt(straight);
                    approachCosts.RemoveAt(straight);
                    approachDirs.Insert(APPROACHES_PER_FACE - 1, d);
                    approachCosts.Insert(APPROACHES_PER_FACE - 1, c);
                }
            }
        }

        private readonly List<Vector3I> collateralCells = new List<Vector3I>();
        private static readonly double[] PRECISE_ACROSS = { 0, 0.4, -0.4, 0.8, -0.8 };
        private static readonly double[] PRECISE_IN = { 0, -0.2, -0.4, -0.6 };
        private const double COLLATERAL_MARGIN = 0.2;    // m past the sphere a projected neighbour counts as touched
        private const int PRECISE_CHECKS_MAX = 4;        // spots validated (WorkSite) per block

        /// <summary>
        /// A welder builds every projected block its sphere touches, not only the one it was sent to: neighbours built
        /// out of order can wall in a cell (gaps). Moves the sphere (across the face, closer to it - never so close the
        /// hull reaches the face) to the spot touching the fewest projected, unbuilt neighbours while still reaching the
        /// block; the spot is validated like the default one. Zero: the default spot (no better one, or none fits).
        /// </summary>
        /// <summary>
        /// A precise-weld shift moves the hull too (sideways, up to a cell): the shifted site gets the same way-in check as
        /// the unshifted one (it pressed a drone against a neighbour 0.6 m short of its spot, stalled till the timeout).
        /// The shift if it fits, else half of it, else none.
        /// </summary>
        private Vector3D ShiftFits(ref ToolMount mount, Vector3D fp, Vector3D fn, Vector3D dirA, IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3D shift)
        {
            if (shift.LengthSquared() < 1e-6) return shift;
            for (int k = 0; k < 2; k++, shift *= 0.5)
            {
                MatrixD f;
                Vector3D sc, ap;
                if (WorkSite(ref mount, fp, fn, dirA, true, true, true, grid, min, max, false, shift, out f, out sc, out ap)) return shift;
                Log.Debug("Drone {0}: precise weld: the sphere moved {1:F1} m puts the hull in the way ({2}){3}", Entity.EntityId, shift.Length(),
                          workSiteWhy ?? "?", k == 0 ? ", trying half" : ", not moved");
            }
            return Vector3D.Zero;
        }

        private Vector3D PreciseShift(ref ToolMount mount, IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3D fp, Vector3D fn,
                                      Vector3I normal, Vector3D dirA, ref MatrixD frame, Vector3D defaultCentre)
        {
            ConstructionComputer.ProjectedAround(grid, min, max, collateralCells);
            if (collateralCells.Count == 0) return Vector3D.Zero;
            double r = mount.WorkRadius;
            int baseline = Touched(grid, defaultCentre, r);
            if (baseline == 0) return Vector3D.Zero;
            Vector3D e1 = Vector3D.TransformNormal((Vector3D)Base6Directions.GetIntVector(
                Base6Directions.GetPerpendicular(Base6Directions.GetDirection(normal))), grid.WorldMatrix);
            Vector3D e2 = Vector3D.Cross(fn, e1);
            // Closest the sphere may come to the face: the hull's front WORK_STANDOFF off it
            Vector3D fromTool = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frame);
            Vector3D half = localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.HalfExtents : new Vector3D(1);
            double halfAlongW = Math.Abs(Vector3D.Dot(fn, frame.Right)) * half.X + Math.Abs(Vector3D.Dot(fn, frame.Up)) * half.Y
                              + Math.Abs(Vector3D.Dot(fn, frame.Backward)) * half.Z;
            double minOut = WORK_STANDOFF - Vector3D.Dot(fromTool, fn) + halfAlongW;
            double defaultOut = Vector3D.Dot(defaultCentre - fp, fn);
            // Candidates, fewest touched first, then the smallest move
            var shifts = new List<Vector3D>();
            var scores = new List<double>();
            for (int i = 0; i < PRECISE_IN.Length; i++)
            {
                if (PRECISE_IN[i] < 0 && defaultOut + PRECISE_IN[i] < minOut) break;
                for (int a = 0; a < PRECISE_ACROSS.Length; a++)
                    for (int b = 0; b < PRECISE_ACROSS.Length; b++)
                    {
                        Vector3D s = fn * PRECISE_IN[i] + e1 * PRECISE_ACROSS[a] + e2 * PRECISE_ACROSS[b];
                        if (!SphereReaches(grid, defaultCentre + s, r, min, max)) continue;
                        int touched = Touched(grid, defaultCentre + s, r);
                        if (touched >= baseline) continue;
                        double score = touched * 100 + s.Length();
                        int at = scores.Count;
                        while (at > 0 && scores[at - 1] > score) at--;
                        scores.Insert(at, score);
                        shifts.Insert(at, s);
                    }
            }
            MatrixD f;
            Vector3D sc, ap;
            for (int k = 0; k < shifts.Count && k < PRECISE_CHECKS_MAX; k++)
            {
                if (!WorkSite(ref mount, fp, fn, dirA, true, true, true, grid, min, max, false, shifts[k], out f, out sc, out ap)) continue;
                Log.Debug("Drone {0}: precise weld: sphere moved {1:F1} m, touches {2} projected neighbour(s) instead of {3}",
                          Entity.EntityId, shifts[k].Length(), (int)(scores[k] / 100), baseline);
                return shifts[k];
            }
            if (shifts.Count > 0)
                Log.Debug("Drone {0}: precise weld: no better sphere spot fits; {1} projected neighbour(s) touched", Entity.EntityId, baseline);
            return Vector3D.Zero;
        }

        // Projected, unbuilt neighbours (collateralCells) a sphere at 'centre' touches
        private int Touched(IMyCubeGrid grid, Vector3D centre, double radius)
        {
            Vector3D local = Vector3D.Transform(centre, grid.WorldMatrixNormalizedInv);
            double gs = grid.GridSize, reach = radius + COLLATERAL_MARGIN;
            int n = 0;
            for (int i = 0; i < collateralCells.Count; i++)
            {
                Vector3D c = (Vector3D)collateralCells[i] * gs;
                Vector3D nearest = Vector3D.Clamp(local, c - gs * 0.5, c + gs * 0.5);
                if (Vector3D.DistanceSquared(local, nearest) < reach * reach) n++;
            }
            return n;
        }

        // The cell at the centre of the (min..max) box's face on side 'normal'
        private static Vector3I FaceCentreCell(Vector3I min, Vector3I max, Vector3I normal)
        {
            Vector3I c = new Vector3I((min.X + max.X) >> 1, (min.Y + max.Y) >> 1, (min.Z + max.Z) >> 1);
            if (normal.X != 0) c.X = normal.X > 0 ? max.X : min.X;
            if (normal.Y != 0) c.Y = normal.Y > 0 ? max.Y : min.Y;
            if (normal.Z != 0) c.Z = normal.Z > 0 ? max.Z : min.Z;
            return c;
        }

        // Does a tool sphere at 'centre' reach into the block (cells min..max), with the arrival tolerance to spare?
        private static bool SphereReaches(IMyCubeGrid grid, Vector3D centre, double radius, Vector3I min, Vector3I max)
        {
            Vector3D local = Vector3D.Transform(centre, grid.WorldMatrixNormalizedInv);
            double gs = grid.GridSize;
            Vector3D lo = ((Vector3D)min - 0.5) * gs, hi = ((Vector3D)max + 0.5) * gs;
            Vector3D nearest = Vector3D.Clamp(local, lo, hi);
            return Vector3D.Distance(local, nearest) < radius - UNIT_REACH_MARGIN;
        }
        private const double UNIT_REACH_MARGIN = 0.2;   // m: arrival tolerance (0.3) mostly lies across the line

        private static double TiltDegrees(Vector3D a, Vector3D n)
        {
            return MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(a, n), -1, 1)));
        }

        private double ApproachCost(ref ToolMount mount, Vector3D d, double tiltDegrees, Vector3D toDrone)
        {
            return AttitudeCost(ref mount, -d) + tiltDegrees / 90.0 * 0.5 + 0.25 * (1 - Vector3D.Dot(d, toDrone));
        }

        // A face's cost for ChooseFace: its cheapest approach (straight or tilted, any side)
        private double BestApproachCost(ref ToolMount mount, Vector3D n, Vector3D toDrone, Vector3D gUp)
        {
            double best = ApproachCost(ref mount, n, 0, toDrone);
            Vector3D e1 = Vector3D.CalculatePerpendicularVector(n), e2 = Vector3D.Cross(n, e1);
            bool inGravity = gUp.LengthSquared() > 0.5;
            bool underside = inGravity && Vector3D.Dot(n, gUp) < -0.7;
            var tilts = underside ? UNDERSIDE_TILTS : APPROACH_TILTS;
            for (int t = 1; t < tilts.Length; t++)
            {
                double tilt = MathHelperD.ToRadians(tilts[t]);
                for (int a = 0; a < 4; a++)
                {
                    Vector3D across = a == 0 ? e1 : a == 1 ? -e1 : a == 2 ? e2 : -e2;
                    Vector3D d = n * Math.Cos(tilt) + across * Math.Sin(tilt);
                    if (inGravity && !underside && Vector3D.Dot(d, gUp) < -0.3) continue;
                    best = Math.Min(best, ApproachCost(ref mount, d, tilts[t], toDrone));
                }
            }
            return best;
        }

        private double AttitudeCost(ref ToolMount mount, Vector3D toolForward)
        {
            if (!flightState.InGravity) return 0;
            MatrixD frame = WorkFrame(ref mount, toolForward);
            double tilt = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(frame.Up, flightState.GravityUp), -1, 1));
            return tilt / MathHelperD.PiOver2;   // 90° costs as much as one "outward" unit
        }
        /// <summary>
        /// The attitude for working with 'mount' facing 'toolForward' (<see cref="MountFrame"/>). Align to P-Gravity
        /// on, in gravity: held within the pitch / roll limits (the tool then faces as close to 'toolForward' as they
        /// allow; straight down with a forward tool: level, the tool forward).
        /// </summary>
        private MatrixD WorkFrame(ref ToolMount mount, Vector3D toolForward)
        {
            MatrixD f = MountFrame(ref mount, toolForward);
            if (settings == null || !settings.AlignToPGravity || !flightState.InGravity) return f;
            Vector3D fwd = f.Forward, up = f.Up, gUp = flightState.GravityUp;
            Vector3D fallback = Vector3D.Dot(fwd, gUp) < 0 ? up : -up;   // pointing straight down / up: un-pitch
            ClampToGravityLimits(ref fwd, ref up, ref gUp, ref fallback);
            return MatrixD.CreateWorld(Vector3D.Zero, fwd, up);
        }

        /// <summary>Align to P-Gravity on, in gravity: 'f' within the pitch / roll limits (1° slack)? 'need': its pitch / roll.</summary>
        private bool WithinGravityLimits(ref MatrixD f, out string need)
        {
            need = null;
            if (settings == null || !settings.AlignToPGravity || !flightState.InGravity) return true;
            Vector3D gUp = flightState.GravityUp;
            double pitch = MathHelperD.ToDegrees(Math.Asin(MathHelperD.Clamp(Vector3D.Dot(f.Forward, gUp), -1, 1)));
            double roll = 90;
            Vector3D levelUp = gUp - f.Forward * Vector3D.Dot(gUp, f.Forward);
            if (levelUp.LengthSquared() > 1e-6)
            {
                levelUp.Normalize();
                Vector3D levelRight = Vector3D.Cross(f.Forward, levelUp);
                roll = MathHelperD.ToDegrees(Math.Atan2(Vector3D.Dot(f.Up, levelRight), Vector3D.Dot(f.Up, levelUp)));
            }
            if (Math.Abs(pitch) <= settings.PGravityAlignMaxPitchDegrees + 1 && Math.Abs(roll) <= settings.PGravityAlignMaxRollDegrees + 1) return true;
            need = Loc.F(StrPitchRollLimits, pitch, roll,
                                 settings.PGravityAlignMaxPitchDegrees, settings.PGravityAlignMaxRollDegrees);
            return false;
        }

        // Pitch / roll (degrees) of an attitude from level, for the logs
        private string DescribeAttitude(ref MatrixD f)
        {
            if (!flightState.InGravity) return Loc.T(StrNoGravity);
            Vector3D gUp = flightState.GravityUp;
            double pitch = MathHelperD.ToDegrees(Math.Asin(MathHelperD.Clamp(Vector3D.Dot(f.Forward, gUp), -1, 1)));
            Vector3D levelUp = gUp - f.Forward * Vector3D.Dot(gUp, f.Forward);
            double roll = 90;
            if (levelUp.LengthSquared() > 1e-6)
            {
                levelUp.Normalize();
                Vector3D levelRight = Vector3D.Cross(f.Forward, levelUp);
                roll = MathHelperD.ToDegrees(Math.Atan2(Vector3D.Dot(f.Up, levelRight), Vector3D.Dot(f.Up, levelUp)));
            }
            return string.Format("pitch {0:F0}° roll {1:F0}°{2}", pitch, roll,
                                 settings != null && settings.AlignToPGravity
                                 ? string.Format(" (limits {0:F0}° / {1:F0}°)", settings.PGravityAlignMaxPitchDegrees, settings.PGravityAlignMaxRollDegrees)
                                 : " (no limits)");
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
            if (needWelder && !HasTool(Orchestrator.TaskType.WeldBlock)) { ReportProblem(Loc.T(StrConstructionNeedsAWelder)); return false; }
            if (needGrinder && !HasTool(Orchestrator.TaskType.GrindBlock)) { ReportProblem(Loc.T(StrConstructionNeedsAGrinder)); return false; }
            return true;
        }

        private void ToolsOff()
        {
            boring = false;
            RestoreDrillClearing();
            if (welder != null && welder.Enabled) welder.Enabled = false;
            if (grinder != null && grinder.Enabled) grinder.Enabled = false;
            for (int i = 0; i < drills.Count; i++) if (drills[i] != null && !drills[i].Closed && drills[i].Enabled) drills[i].Enabled = false;
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
            if (type == Orchestrator.TaskType.Mine)
            {
                for (int i = 0; i < drills.Count; i++) if (drills[i] != null && !drills[i].Closed && drills[i].Enabled != on) drills[i].Enabled = on;
                return;
            }
            var tool = type == Orchestrator.TaskType.GrindBlock ? (IMyFunctionalBlock)grinder : welder;
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
            if (slim != null)
            {
                RefreshDroneGrids();
                if (droneGrids.Contains(slim.CubeGrid)) return Orchestrator.TaskFailure.TargetNotFound;   // itself: another drone's work
            }
            if (task.Type == Orchestrator.TaskType.GrindBlock)
                return slim == null ? Orchestrator.TaskFailure.TargetRemoved : Orchestrator.TaskFailure.None;
            if (slim == null)
                return gridGone || !task.IsProjected ? Orchestrator.TaskFailure.TargetRemoved : ProjectedBuildState(ref task);
            if (slim.IsFullIntegrity && !slim.HasDeformation) return Orchestrator.TaskFailure.AlreadyFullIntegrity;
            // Nothing aboard it still needs: not flown to (it would only stand there)
            return CargoHasAnyOf(slim) ? Orchestrator.TaskFailure.None : Orchestrator.TaskFailure.NoPartsAboard;
        }

        /// <summary>
        /// A projected block not built yet: can the welder place it now? Its projector must allow it (attached to what is
        /// built: NotAttached otherwise) and the cargo must hold its first component (a ship welder places a projected
        /// block only with that at hand: NoPartsAboard otherwise). None also when the projection can't be found (the
        /// weld then gives up after PROJECTED_APPEAR_TICKS, as before).
        /// </summary>
        private Orchestrator.TaskFailure ProjectedBuildState(ref Orchestrator.Task task)
        {
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(task.TargetEntityId, out e) ? e as IMyCubeGrid : null;
            if (grid == null) return Orchestrator.TaskFailure.None;
            Vector3I cell = task.TargetBlock.ToVector3I();
            Vector3D world = grid.GridIntegerToWorld(cell);
            foreach (var projector in grid.GetFatBlocks<IMyProjector>())
            {
                var projected = projector.ProjectedGrid;
                if (projected == null || !projector.IsProjecting) continue;
                var pb = projected.GetCubeBlock(projected.WorldToGridInteger(world));
                if (pb == null) continue;
                if (projector.CanBuild(pb, false) == BuildCheckResult.NotConnected)
                {
                    Log.Debug("Drone {0}: projected block at {1} ({2}) not attached to anything built yet: skipped",
                              Entity.EntityId, cell, pb.BlockDefinition.Id.SubtypeName);
                    return Orchestrator.TaskFailure.NotAttached;
                }
                var def = pb.BlockDefinition as Sandbox.Definitions.MyCubeBlockDefinition;
                if (def != null && def.Components != null && def.Components.Length > 0 && !CargoHasComponent(def.Components[0].Definition.Id.SubtypeName))
                {
                    Log.Debug("Drone {0}: projected block at {1} ({2}) needs a {3} to be placed, none aboard: skipped",
                              Entity.EntityId, cell, def.Id.SubtypeName, def.Components[0].Definition.Id.SubtypeName);
                    return Orchestrator.TaskFailure.NoPartsAboard;
                }
                return Orchestrator.TaskFailure.None;
            }
            return Orchestrator.TaskFailure.None;
        }

        // Any component in the cargo or the welder
        private bool AnyComponentAboard()
        {
            for (int c = 0; c <= cargoContainers.Count; c++)
            {
                var inv = c < cargoContainers.Count ? cargoContainers[c].GetInventory(0)
                        : welder != null ? welder.GetInventory(0) : null;
                if (inv == null) continue;
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = 0; i < itemBuffer.Count; i++)
                    if (itemBuffer[i].Type.TypeId == COMPONENT_TYPE) { itemBuffer.Clear(); return true; }
            }
            itemBuffer.Clear();
            return false;
        }

        // At least one of this component in the cargo or the welder
        private bool CargoHasComponent(string subtype)
        {
            for (int c = 0; c <= cargoContainers.Count; c++)
            {
                var inv = c < cargoContainers.Count ? cargoContainers[c].GetInventory(0)
                        : welder != null ? welder.GetInventory(0) : null;
                if (inv == null) continue;
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = 0; i < itemBuffer.Count; i++)
                    if (itemBuffer[i].Type.TypeId == COMPONENT_TYPE && itemBuffer[i].Type.SubtypeId == subtype) { itemBuffer.Clear(); return true; }
            }
            itemBuffer.Clear();
            return false;
        }

        // The tool task's completion trigger. 'elapsed': ticks since the task started (Timespan trigger).
        // On a block, the inventory triggers also end when the block itself is done: a partial weld stops at full
        // integrity or when the cargo holds nothing more the block needs; a partial grind when the block is gone.
        // 'why': the completion trigger that fired, for the log
        private bool IsToolTaskDone(ref Orchestrator.Task task, int elapsed, out string why)
        {
            why = null;
            bool onBlock = task.TargetEntityId != 0 && task.Type != Orchestrator.TaskType.Mine;
            switch (task.Completion)
            {
                case Orchestrator.ToolTaskCompletionTrigger.Timespan:
                    if (elapsed < task.CompletionValue * 60) return false;
                    why = Loc.F(StrTaskSeconds, task.CompletionValue);
                    return true;
                case Orchestrator.ToolTaskCompletionTrigger.DroneInventoryFull:
                    if (CargoFillRatio() >= 0.95) { why = Loc.T(StrCargoFull); return true; }
                    if (!onBlock) return false;
                    break;
                case Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty:
                    // Welds: no component left aboard (cargo and the welder's own inventory). Not the cargo's fill ratio: a
                    // few parts in a big container are under 0.1 % of it, and the welder pulls them out of the cargo
                    // anyway - welds ended as "cargo empty" the moment they started.
                    if (task.Type == Orchestrator.TaskType.WeldBlock ? !AnyComponentAboard() : CargoFillRatio() <= 0.001)
                    { why = Loc.T(StrCargoEmpty); return true; }
                    if (!onBlock) return false;
                    break;
            }
            bool gridGone;
            var slim = ToolTarget(ref task, out gridGone);
            if (gridGone) { why = Loc.T(StrGridGone); return true; }
            if (task.Completion == Orchestrator.ToolTaskCompletionTrigger.DroneInventoryFull)
            {
                if (slim == null || slim.IsFullyDismounted) { why = Loc.T(StrDismounted); return true; }
                return false;
            }
            if (task.Completion == Orchestrator.ToolTaskCompletionTrigger.DroneInventoryEmpty)
            {
                if (slim == null)
                {
                    if (task.IsProjected) return false;   // projection: not built yet
                    why = Loc.T(StrBlockGone);
                    return true;
                }
                if (slim.IsFullIntegrity && !slim.HasDeformation) { why = Loc.T(StrFullIntegrity); return true; }
                if (!CargoHasAnyOf(slim))
                {
                    why = Loc.T(StrNoPartsLeftForIt);
                    // Not finished: not planned again this trip (it would come straight back here); missing parts in the log
                    tripFailed.Add(new KeyValuePair<long, Vector3I>(task.TargetEntityId, task.TargetBlock.ToVector3I()));
                    missingBuffer.Clear();
                    slim.GetMissingComponents(missingBuffer);
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in missingBuffer) sb.Append(' ').Append(kv.Value).Append(' ').Append(kv.Key);
                    Log.Debug("Drone {0}: block {1} ({2}, {3:P0} built) still misses:{4}; cargo has:{5}", Entity.EntityId,
                              task.TargetBlock.ToVector3I(), slim.BlockDefinition.Id.SubtypeName, slim.BuildLevelRatio, sb, DescribeTop(CargoComponents(), 6));
                    return true;
                }
                return false;
            }
            switch (task.Completion)
            {
                case Orchestrator.ToolTaskCompletionTrigger.BlockFullyDismounted:
                    if (slim == null || slim.IsFullyDismounted) { why = Loc.T(StrDismounted); return true; }
                    return false;
                case Orchestrator.ToolTaskCompletionTrigger.BlockIntegrityPercent:
                    if (slim != null && slim.MaxIntegrity > 0 && slim.Integrity / slim.MaxIntegrity * 100f >= task.CompletionValue)
                    {
                        why = Loc.F(StrIntegrity, task.CompletionValue);
                        return true;
                    }
                    return false;
                default:   // BlockFullIntegrity
                    if (slim == null)
                    {
                        if (task.IsProjected) return false;   // gone meanwhile; projections: not built yet
                        why = Loc.T(StrBlockGone);
                        return true;
                    }
                    if (slim.IsFullIntegrity && !slim.HasDeformation) { why = Loc.T(StrFullIntegrity); return true; }
                    return false;
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
        private static bool IsSmallGridTarget(ref Orchestrator.Task task)
        {
            IMyEntity e;
            var grid = task.TargetEntityId != 0 && MyAPIGateway.Entities.TryGetEntityById(task.TargetEntityId, out e) ? e as IMyCubeGrid : null;
            return grid != null && grid.GridSizeEnum == MyCubeSize.Small;
        }

        // Cargo containers and the welder's own inventory (it pulls parts in before using them)
        private bool CargoHasAnyOf(IMySlimBlock slim)
        {
            missingBuffer.Clear();
            slim.GetMissingComponents(missingBuffer);
            // Nothing missing yet not complete: every part is on the block already (its stockpile), only welding is left
            if (missingBuffer.Count == 0) return true;
            for (int c = 0; c <= cargoContainers.Count; c++)
            {
                var inv = c < cargoContainers.Count ? cargoContainers[c].GetInventory(0)
                        : welder != null ? welder.GetInventory(0) : null;
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

        private long DockedConnectorIdOrZero()
        {
            var c = DockedConnector();
            return c != null ? c.EntityId : 0;
        }

        private readonly List<IMyCubeGrid> sameNetworkGrids = new List<IMyCubeGrid>();

        /// <summary>
        /// Docked at a connector of the same conveyor network as 'target' (same logical grid group: what BuildHomeNetwork
        /// reads, so a load / unload / clear here reaches the same containers as one at 'target'). Out: the connector.
        /// </summary>
        private bool DockedInNetworkOf(IMyShipConnector target, out IMyShipConnector docked)
        {
            docked = DockedConnector();
            if (docked == null || target == null) return false;
            if (docked.EntityId == target.EntityId) return true;
            sameNetworkGrids.Clear();
            docked.CubeGrid.GetGridGroup(GridLinkTypeEnum.Logical).GetGrids(sameNetworkGrids);
            bool same = sameNetworkGrids.Contains(target.CubeGrid);
            sameNetworkGrids.Clear();
            return same;
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
                    && settings.OperationMode == OperationMode.ManagedByOrchestrator
                    && cPhase == ConstructionPhase.Idle && currentJob == null
                    && combatPhase == CombatPhase.Idle   // combat drones out on their own detection: busy
                    && currentState != State.Error && !needsBatteryRecharge && !needsHydrogenRefuel
                    && preflightStage == 0 && activeFlightOrder == null && dockingHomeId == 0
                    && (capabilities & (Capabilities.CanFlyAtmosphere | Capabilities.CanFlySpace)) != 0;
            }
        }

        // UpdateBeforeSimulation100, server, ManagedByOrchestrator only
        private void UpdateOrchestratorJobs()
        {
            if (!IsServer || settings == null || !settings.IsEnabled || settings.OperationMode != OperationMode.ManagedByOrchestrator) return;
            if (savedSave != null) return;   // its saved job / pending bid come first
            if (cPhase == ConstructionPhase.Running && jobSource == JobSource.Orchestrator && (needsBatteryRecharge || needsHydrogenRefuel))
            {
                Report(StrJobAbandonedLowPower);
                StopJob(Loc.T(StrLowPower));   // MonitorPower sends the drone home
                return;
            }
            var queue = AutomataSession.GetMessageQueue();
            if (queue == null || primaryAntenna == null) return;
            // Registered with the virtual network (dropped while its antenna was off / out of date: register again)
            VirtualNetwork.MessageQueue.AntennaInfo antennaInfo;
            if (!queue.GetAntennaInfo(_entityId, out antennaInfo) || antennaInfo.Antenna != primaryAntenna)
            {
                queue.RemoveAntenna(_entityId);
                queue.RegisterAntenna(_entityId, VirtualNetwork.MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
                queue.Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_START);
                queue.Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
            }
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
                // Only the award for our outstanding bid, from an orchestrator of the drone owner / their faction
                if (award == null || award.AuctionId != pendingBidAuctionId || pendingBidAuctionId == 0) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, awardInbox[i].SenderOwnerId)) continue;
                pendingBidAuctionId = 0;   // decided either way: free to bid again
                List<Orchestrator.Task> tasks;
                if (award.TaskAssignments == null || !award.TaskAssignments.TryGetValue(_entityId, out tasks) || tasks == null || tasks.Count == 0)
                {
                    Log.Debug("Drone {0}: auction {1} (job {2}) went to another drone", _entityId, award.AuctionId, award.JobId);
                    continue;
                }
                if (!IsAvailableForJobs)
                {
                    Log.Debug("Drone {0}: award for job {1} declined, busy", _entityId, award.JobId);
                    QueueJobReport(new JobStatusReport
                    {
                        AnnouncerEntityId = award.AnnouncerEntityId, JobId = award.JobId, AuctionId = award.AuctionId,
                        OrchestratorEntityId = awardInbox[i].SenderId, DroneEntityId = _entityId, Outcome = JobOutcome.Declined,
                        Reason = Loc.T(StrBusy),
                    });
                    continue;
                }
                var job = new Orchestrator.Job
                {
                    JobId = award.JobId,
                    Tasks = new List<Orchestrator.Task>(tasks),
                    CreatedTime = award.CreatedTime,
                    AssignedTime = DateTime.UtcNow,
                    WeldCategory = award.WeldCategory,
                };
                for (int k = 0; k < tasks.Count; k++)
                {
                    if (tasks[k].Kind != Orchestrator.TaskKind.Tool) continue;
                    job.BlockCount++;
                    if (job.JobType == Orchestrator.JobType.None)
                        job.JobType = tasks[k].Type == Orchestrator.TaskType.GrindBlock ? Orchestrator.JobType.Grind
                                    : tasks[k].Type == Orchestrator.TaskType.Mine ? Orchestrator.JobType.MineOre : Orchestrator.JobType.Weld;
                }
                int surveys = 0;
                for (int k = 0; k < tasks.Count; k++) if (tasks[k].Type == Orchestrator.TaskType.Survey) surveys++;
                for (int k = 0; k < tasks.Count && job.JobType == Orchestrator.JobType.None; k++)
                {
                    // One scan: a spot check; several: an area sweep sector
                    if (tasks[k].Type == Orchestrator.TaskType.Survey)
                        job.JobType = surveys > 1 ? Orchestrator.JobType.DesignatorScoutArea : Orchestrator.JobType.DesignatorScoutPoint;
                    else if (tasks[k].Type == Orchestrator.TaskType.Engage) job.JobType = Orchestrator.JobType.DesignatorEngage;
                    else if (tasks[k].Type == Orchestrator.TaskType.Escort) job.JobType = Orchestrator.JobType.DesignatorEscort;
                }
                job.CalculateTotalInventory();
                jobAnnouncerId = award.AnnouncerEntityId;
                jobAuctionId = award.AuctionId;
                jobOrchestratorId = awardInbox[i].SenderId;
                jobAwardType = job.JobType;
                jobAwardWeld = job.JobType == Orchestrator.JobType.Weld ? job.WeldCategory : Orchestrator.JobTypeExtensions.WELD_ANY;
                Report(BidWonText(award, job));
                Log.Debug("Drone {0}: job {1} awarded: {2} tasks{3}", _entityId, award.JobId, tasks.Count, award.SharedSite ? ", shared site" : "");
                StartJob(job, JobSource.Orchestrator);
                jobSharedSite = award.SharedSite && settings.OperationMode == OperationMode.ManagedByOrchestrator;
                break;   // one job at a time
            }
            awardInbox.Clear();
        }

        // "bid won: Weld :: LC:<lc> CC: <cc>" / "bid won: Request :: <supplier> -> <requester>", then the payload, one item a line
        private static string BidWonText(AuctionWinnerAnnouncement award, Orchestrator.Job job)
        {
            var type = award.JobType != Orchestrator.JobType.None ? award.JobType : job.JobType;
            var sb = new StringBuilder("bid won: ");
            string label = Orchestrator.JobTypeExtensions.Label(type, award.WeldCategory);
            string lcs = Names(award.ComponentSources);
            string announcer = Nicknames.Of(award.AnnouncerEntityId);
            DiscreteInventory payload = job.TotalInventory;
            switch (type)
            {
                case Orchestrator.JobType.Weld:
                case Orchestrator.JobType.Grind:
                    sb.Append(label).Append(" :: LC:").Append(lcs).Append(" CC: ").Append(announcer);
                    if (type == Orchestrator.JobType.Grind) payload = UnloadsOf(job);
                    break;
                case Orchestrator.JobType.DeliverMissingInventory:
                    sb.Append("Request :: ").Append(lcs).Append(" -> ").Append(announcer);
                    break;
                case Orchestrator.JobType.CollectInventorySurplus:
                    sb.Append("Provide :: ").Append(announcer).Append(" -> ").Append(lcs);
                    break;
                case Orchestrator.JobType.MineOre:
                    sb.Append("Mine :: MC: ").Append(announcer).Append(" LC:").Append(lcs);
                    payload = UnloadsOf(job);
                    break;
                default:
                    sb.Append(type);
                    payload = null;
                    break;
            }
            Nicknames.AppendPayload(sb, "payload", payload);
            return sb.ToString();
        }

        private static string Names(List<long> ids)
        {
            if (ids == null || ids.Count == 0) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(Nicknames.Of(ids[i])); }
            return sb.ToString();
        }

        // What the job's unload tasks name (grind returns, mined ore); null when they unload everything
        private static DiscreteInventory UnloadsOf(Orchestrator.Job job)
        {
            DiscreteInventory inv = null;
            for (int i = 0; job.Tasks != null && i < job.Tasks.Count; i++)
            {
                var t = job.Tasks[i];
                if (t.Type != Orchestrator.TaskType.UnloadInventory || t.Inventory == null || t.Inventory.IsEmpty()) continue;
                if (inv == null) inv = new DiscreteInventory();
                inv.AddItems(t.Inventory);
            }
            return inv;
        }

        private void ReadAuctions(MessageQueue queue)
        {
            queue.ReadMessages(_entityId, primaryAntenna, Channel.ORCHESTRATOR_AUCTION_START, auctionInbox, 10, true, PayloadType.Auction);
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            ForgetOldAuctions(now);
            if (pendingBidAuctionId != 0)
            {
                // The orchestrator re-broadcasts an open auction every 20 s: one not heard for 30 s was dropped (nobody tells
                // the bidders), so the drone stops waiting for it instead of sitting out its whole lifetime + 30 s
                for (int i = 0; i < auctionInbox.Count; i++)
                    if (auctionInbox[i].Payload != null && auctionInbox[i].Payload.AuctionId == pendingBidAuctionId) pendingBidSeenFrame = now;
                if (DateTime.UtcNow > pendingBidExpires || now - pendingBidSeenFrame > PENDING_BID_SILENCE_TICKS)
                {
                    Log.Debug("Drone {0}: auction {1}: no outcome heard, free to bid again", _entityId, pendingBidAuctionId);
                    pendingBidAuctionId = 0;   // lost / dropped / never announced
                }
            }
            if (!IsAvailableForJobs || pendingBidAuctionId != 0)
            {
                if (auctionInbox.Count > 0 && Log.DebugEnabled)
                    Log.Debug("Drone {0}: {1} auction(s) read, no bid: {2}", _entityId, auctionInbox.Count,
                              pendingBidAuctionId != 0 ? "waiting for the outcome of auction " + pendingBidAuctionId : WhyNotAvailable());
                auctionInbox.Clear();
                return;
            }
            // One bid at a time: a welder biased to one kind of weld (Welder:Repair / Projected / Unbuilt) bids on an auction
            // of its own kind when one is open, else on the first it can take
            byte bias = Orchestrator.JobTypeExtensions.WeldBias(settings.BehaviourProfile);
            Auction pick = null;
            for (int i = 0; i < auctionInbox.Count; i++)
            {
                var auction = auctionInbox[i].Payload;
                if (auction == null || auction.ExpirationTime < DateTime.UtcNow || biddedAuctions.ContainsKey(auction.AuctionId)) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, auctionInbox[i].SenderOwnerId)) continue;
                string why = WhyNotJob(auction);
                if (why != null)
                {
                    if (noBidLogged.Count > 200) noBidLogged.Clear();
                    if (noBidLogged.Add(auction.AuctionId))   // rebroadcasts: logged once
                        Log.Debug("Drone {0}: auction {1} ({2}, job {3}): no bid: {4}", _entityId, auction.AuctionId,
                                  Orchestrator.JobTypeExtensions.Label(auction.JobType, auction.WeldCategory), auction.JobId, why);
                    continue;
                }
                bool own = bias != Orchestrator.JobTypeExtensions.WELD_ANY && auction.JobType == Orchestrator.JobType.Weld && auction.WeldCategory == bias;
                if (pick == null || own) pick = auction;
                if (own || bias == Orchestrator.JobTypeExtensions.WELD_ANY) break;
            }
            if (pick != null)
            {
                var auction = pick;
                if (bias != Orchestrator.JobTypeExtensions.WELD_ANY && Log.DebugEnabled)
                    Log.Debug("Drone {0}: bidding on auction {1} ({2}, job {3}){4}", _entityId, auction.AuctionId,
                              Orchestrator.JobTypeExtensions.Label(auction.JobType, auction.WeldCategory), auction.JobId,
                              auction.JobType == Orchestrator.JobType.Weld && auction.WeldCategory == bias ? ": its own kind of weld" : "");
                SendBid(queue, auction);
                biddedAuctions[auction.AuctionId] = now;
                pendingBidAuctionId = auction.AuctionId;
                pendingBidSeenFrame = now;
                pendingBidExpires = auction.ExpirationTime.AddSeconds(30);   // the announcement follows the bid window
                // one job at a time: wait for this auction's outcome
            }
            auctionInbox.Clear();
        }

        // Debug: why IsAvailableForJobs is false
        private readonly HashSet<uint> noBidLogged = new HashSet<uint>();

        private string WhyNotAvailable()
        {
            if (!initialized || settings == null) return "not initialized";
            if (!settings.IsEnabled) return "AI off";
            if (settings.OperationMode != OperationMode.ManagedByOrchestrator) return "mode " + settings.OperationMode;
            if (cPhase != ConstructionPhase.Idle || currentJob != null) return "working a job";
            if (combatPhase != CombatPhase.Idle) return "combat (own detection) " + combatPhase;
            if (currentState == State.Error) return "in error";
            if (needsBatteryRecharge || needsHydrogenRefuel) return "recharging / refuelling";
            if (preflightStage != 0 || activeFlightOrder != null || dockingHomeId != 0) return "flying / docking";
            if ((capabilities & (Capabilities.CanFlyAtmosphere | Capabilities.CanFlySpace)) == 0) return "can't fly (no thrust for its mass: capabilities " + capabilities + ")";
            return "?";
        }

        private string WhyNotJob(Auction auction)
        {
            var caps = BidCapabilities;
            if (auction.OnlyDroneEntityId != 0 && auction.OnlyDroneEntityId != _entityId) return "ordered for another (tagged) drone";
            if (batteryPercent == 0 && h2Percent <= 0) return "no power (batteries empty)";
            if (settings.BehaviourProfile == BehaviourProfile.IsOreHauler && auction.JobType != Orchestrator.JobType.MineOre
                && !((auction.JobType == Orchestrator.JobType.DeliverMissingInventory || auction.JobType == Orchestrator.JobType.CollectInventorySurplus)
                     && auction.ComponentsInventory != null && auction.ComponentsInventory.IsOreOnly()))
                return "ore hauler: ores / ice only";
            switch (auction.JobType)
            {
                case Orchestrator.JobType.Weld:    if ((caps & Capabilities.CanWeld) == 0) return "no welder"; break;
                case Orchestrator.JobType.Grind:   if ((caps & Capabilities.CanGrind) == 0) return "no grinder"; break;
                case Orchestrator.JobType.MineOre:
                    if ((caps & Capabilities.CanDrill) == 0 || (caps & Capabilities.CanScoutOre) == 0)
                        return (settings.ExcludedCapabilities & (Capabilities.CanDrill | Capabilities.CanScoutOre)) != 0
                            ? "drill / ore detector excluded (terminal)" : "needs a drill and an ore detector";
                    if ((caps & Capabilities.CargoVolumeNil) != 0 || DroneCargoFreeVolume() < 0.01) return "no free cargo (the orchestrator would refuse it)";
                    if (auction.ComponentsInventory != null)
                        foreach (var ore in auction.ComponentsInventory.GetAllItems())
                            if (IsOreIgnored(ore.Key.String)) return "ore ignored (" + ore.Key.String + ")";
                    break;
                case Orchestrator.JobType.DesignatorScoutPoint:
                case Orchestrator.JobType.DesignatorScoutArea:
                    if (settings.BehaviourProfile != BehaviourProfile.IsOreScout) return "not an ore scout (behaviour profile)";
                    if ((caps & Capabilities.CanScoutOre) == 0) return "needs an ore detector";
                    break;
                case Orchestrator.JobType.DesignatorEngage:
                case Orchestrator.JobType.DesignatorEscort:
                    if (settings.BehaviourProfile != BehaviourProfile.IsCombatDrone) return "not a combat drone (behaviour profile)";
                    if ((caps & Capabilities.HasDefenseSystems) == 0) return "needs AI Defensive, AI Flight and AI Offensive blocks";
                    break;
                default:
                    if ((caps & Capabilities.CargoVolumeNil) != 0) return "no cargo";
                    if (DockFaces().Count == 0 && (!SmallConnectorsAllowed || DockFaces(true).Count == 0)) return "no usable connector";
                    break;
            }
            // Jobs that carry something need room (the orchestrator refuses a bid without): cleared when idle (ClearWhenIdle)
            if (!Orchestrator.JobTypeExtensions.IsDesignator(auction.JobType) && auction.JobType != Orchestrator.JobType.MineOre
                && (caps & Capabilities.CargoVolumeNil) == 0 && DroneCargoFreeVolume() < 0.01)
                return "cargo full (cleared at a connector first)";
            if (auction.IsInSpace && (caps & Capabilities.CanFlySpace) == 0) return "job in space, the drone can't fly there (" + caps + ")";
            if (auction.IsInAtmosphere && (caps & Capabilities.CanFlyAtmosphere) == 0) return "job in gravity, the drone can't lift itself there (" + caps + ")";
            return null;
        }

        // The drone's own minimum check (the orchestrator checks again): equipment, environment, load
        private bool CanTakeJob(Auction auction)
        {
            var caps = BidCapabilities;
            switch (auction.JobType)
            {
                case Orchestrator.JobType.Weld:    if ((caps & Capabilities.CanWeld) == 0) return false; break;
                case Orchestrator.JobType.Grind:   if ((caps & Capabilities.CanGrind) == 0) return false; break;
                case Orchestrator.JobType.MineOre: if ((caps & Capabilities.CanDrill) == 0 || (caps & Capabilities.CanScoutOre) == 0) return false; break;
                case Orchestrator.JobType.DesignatorScoutPoint:
                case Orchestrator.JobType.DesignatorScoutArea: if ((caps & Capabilities.CanScoutOre) == 0) return false; break;
                case Orchestrator.JobType.DesignatorEngage:
                case Orchestrator.JobType.DesignatorEscort: if ((caps & Capabilities.HasDefenseSystems) == 0) return false; break;
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
            // A job in space asks nothing of the drone's lift in 1 g (a large ship that can't hover is a fine miner there):
            // its load is what it can still move at SPACE_MIN_ACCEL
            double optimalLoad = PayloadIn1G();
            Capabilities bidCaps = BidCapabilities;
            if (auction.IsInSpace)
            {
                maxLoad1G = optimalLoad = PayloadInSpace();
                bidCaps = (bidCaps & ~(Capabilities.LiftNil | Capabilities.LiftLt1t | Capabilities.LiftLt10t | Capabilities.LiftLt100t | Capabilities.LiftLt1000t))
                          | LiftCapability(maxLoad1G);
            }
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
                    Capabilities = bidCaps,
                    DockFaces = DockFaces(),
                    SmallDockFaces = DockFaces(true),
                    DockedConnectorId = DockedConnectorIdOrZero(),
                    ToolFaces = ToolFaces(auction.JobType),
                    WorkPitchDegrees = WorkTiltLimit(true),
                    WorkRollDegrees = WorkTiltLimit(false),
                    CanFlyInverted = CanWorkInverted(),
                    DroneBehaviours = settings.BehaviourProfile,
                    IOLocationData = new IOLocationData
                    {
                        PositionData = Vector3DData.FromVector3D(flightState.Position),
                        OrientationData = QuaternionDData.FromQuaternionD(QuaternionD.CreateFromRotationMatrix(flightState.WorldMatrix)),
                        EntityId = _entityId,
                    },
                    MaxLoadIn1G = (MyFixedPoint)maxLoad1G,
                    OptimalLoadIn1G = (MyFixedPoint)optimalLoad,   // within the player's max-load setting (gravity)
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

        // The face (Base6Directions.Direction, controller frame) of the tool a weld / grind job uses; null otherwise
        private List<byte> ToolFaces(Orchestrator.JobType type)
        {
            ToolMount mount;
            if (type == Orchestrator.JobType.Weld && welder != null) mount = welderMount;
            else if (type == Orchestrator.JobType.Grind && grinder != null) mount = grinderMount;
            else return null;
            if (mount.LocalForward.LengthSquared() < 0.5) return null;
            return new List<byte> { (byte)Base6Directions.GetClosestDirection((Vector3)mount.LocalForward) };
        }

        // For the orchestrator's tool ranking: the steepest pitch / roll it works at (P-Gravity limits, or 180 without
        // them), never more than the tilt it learnt it can't hold (maxWorkTiltDegrees)
        private float WorkTiltLimit(bool pitch)
        {
            double limit = settings == null || !settings.AlignToPGravity ? 180
                         : pitch ? settings.PGravityAlignMaxPitchDegrees : settings.PGravityAlignMaxRollDegrees;
            return (float)Math.Min(limit, maxWorkTiltDegrees);
        }

        private const double INVERTED_LIFT_MARGIN = 1.2;   // "down" thrust must lift the drone this many times over

        // Upside down: no P-Gravity limits (they cap at 90°), its "down" thrusters lift it (here, or in 1 g when not in gravity)
        private bool CanWorkInverted()
        {
            if (settings == null || settings.AlignToPGravity || maxWorkTiltDegrees < 180) return false;
            double g = flightState.InGravity ? flightState.Gravity.Length() : 9.81;
            double mass = DroneOwnMass();
            return mass > 0 && RatedThrust(Base6Directions.Direction.Down) / mass >= g * INVERTED_LIFT_MARGIN;
        }

        #region Job outcome reports
        // The outcome of the current orchestrator job (once; the award's ids are cleared)
        private void ReportJobOutcome(JobOutcome outcome, string reason)
        {
            if (jobAuctionId == 0 || currentJob == null) return;
            QueueJobReport(new JobStatusReport
            {
                AnnouncerEntityId = jobAnnouncerId, JobId = currentJob.JobId, AuctionId = jobAuctionId, OrchestratorEntityId = jobOrchestratorId,
                DroneEntityId = _entityId, Outcome = outcome, Reason = reason, BlocksDone = jobBlocksDone, BlocksTotal = currentJob.BlockCount,
                JobType = jobAwardType, OreKg = minedOreKg, WeldCategory = jobAwardWeld,
            });
            string label = Orchestrator.JobTypeExtensions.Label(jobAwardType, jobAwardWeld);
            if (reason != null) Report(StrJobOutcomeReason, currentJob.JobId, outcome, reason, label); else Report(StrJobOutcome, currentJob.JobId, outcome, label);
            jobAuctionId = 0;
            jobAnnouncerId = jobOrchestratorId = 0;
            minedOreKg = 0;
        }

        private double minedOreKg;   // MineOre: ore unloaded at the job's logistics computer so far

        private void QueueJobReport(JobStatusReport r)
        {
            unsentReports.Add(r);
            unsentReportTimes.Add(DateTime.UtcNow);
            FlushJobReports();
        }

        #region Timing
        private const int JOB_KEEPALIVE_TICKS = 20 * 60;       // working on an awarded job: tell its announcer this often (it warns after 60 s unheard)
        #endregion
        private int lastKeepaliveFrame = int.MinValue / 2;

        // Construction computer / mining surveyor jobs expire without it (dead / destroyed / unresponsive drone).
        // Best effort: not queued, the next one goes a minute later.
        private void SendJobKeepalive()
        {
            if (!IsServer || currentJob == null || jobAuctionId == 0 || jobAnnouncerId == 0) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastKeepaliveFrame < JOB_KEEPALIVE_TICKS) return;
            var queue = AutomataSession.GetMessageQueue();
            if (queue == null || !EnsureNetworkRegistration(queue)) return;
            lastKeepaliveFrame = now;
            queue.BroadcastMessage(primaryAntenna, new Message<JobStatusReport>
            {
                Payload = new JobStatusReport
                {
                    AnnouncerEntityId = jobAnnouncerId, JobId = currentJob.JobId, AuctionId = jobAuctionId, OrchestratorEntityId = jobOrchestratorId,
                    DroneEntityId = _entityId, Outcome = JobOutcome.Working, BlocksDone = jobBlocksDone, BlocksTotal = currentJob.BlockCount,
                    JobType = jobAwardType, OreKg = minedOreKg, WeldCategory = jobAwardWeld,
                },
                MessageId = IdGenerator.GenerateId(ref jobMessageCounter, _entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = VirtualNetwork.MessageQueue.IAIBlockType.ConstructionComputer | VirtualNetwork.MessageQueue.IAIBlockType.MiningSurveyor
                                     | VirtualNetwork.MessageQueue.IAIBlockType.LogisticsComputer | VirtualNetwork.MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.JOB_KEEPALIVE,
            });
        }

        // Sent until someone that listens is in range (the drone may finish out of range), at most JOB_REPORT_TTL_SECONDS
        private void FlushJobReports()
        {
            if (unsentReports.Count == 0 || !IsServer) return;
            var queue = AutomataSession.GetMessageQueue();
            for (int i = unsentReports.Count - 1; i >= 0; i--)
            {
                var r = unsentReports[i];
                ErrorCode error = ErrorCode.NoSubscribers;
                if (queue != null && EnsureNetworkRegistration(queue))
                {
                    error = queue.BroadcastMessage(primaryAntenna, new Message<JobStatusReport>
                    {
                        Payload = r,
                        MessageId = IdGenerator.GenerateId(ref jobMessageCounter, _entityId),
                        CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                        SenderId = _entityId,
                        SenderOwnerId = block.OwnerId,
                        RequiresAck = false,
                        RecipientBlockType = VirtualNetwork.MessageQueue.IAIBlockType.Orchestrator | VirtualNetwork.MessageQueue.IAIBlockType.ConstructionComputer
                                             | VirtualNetwork.MessageQueue.IAIBlockType.LogisticsComputer | VirtualNetwork.MessageQueue.IAIBlockType.MiningSurveyor,
                        Channel = r.Outcome == JobOutcome.Completed ? Channel.TASKS_COMPLETED : Channel.TASKS_ABORTED,
                    });
                }
                bool old = (DateTime.UtcNow - unsentReportTimes[i]).TotalSeconds > JOB_REPORT_TTL_SECONDS;
                if (error == ErrorCode.None || old)
                {
                    Log.Debug("Drone {0}: job {1} {2} report to {3}: {4}", _entityId, r.JobId, r.Outcome, r.AnnouncerEntityId,
                              error == ErrorCode.None ? "sent" : "dropped (" + error + ")");
                    unsentReports.RemoveAt(i);
                    unsentReportTimes.RemoveAt(i);
                }
            }
        }
        #endregion

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
