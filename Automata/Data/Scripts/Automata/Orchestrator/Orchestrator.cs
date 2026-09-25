using Automata.Config;
using Automata.VirtualNetwork;
using Automata.Util;
using Automata.Util.Logging;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using VRage.Utils;
using System.Configuration;
using VRage;

using Automata.LogisticsComputer;
using Automata.Inventory;
using Automata.ConstructionComputer;
using Automata.MiningSurveyor;

namespace Automata.Orchestrator
{
    public class Orchestrator
    {
        private sealed class PendingBidRoundState
        {
            public Task Task;
            public long DeadlineFrame;
            public readonly List<CollectedBid> Bids = new List<CollectedBid>();
        }

        private sealed class LogisticComputerData
        {
            public Vector3DData PositionData;
            public QuaternionDData OrientationData;
            public long EntityId;
            public LogisticsComputer.InventoryFulfillment InventoryFulfillmentFlags;
        }

        /// <summary>
        /// Logistic computer data indexed by bid round id.
        /// </summary>
        private readonly Dictionary<uint, List<LogisticComputerData>> _logisticComputerDataByBidRoundBidId = new Dictionary<uint, List<LogisticComputerData>>();

        /// <summary>
        /// Queue of discovered, but yet unporsed Jobs.
        /// </summary>
        private MyConcurrentQueue<Job> jobQueue = new MyConcurrentQueue<Job>();
        /// <summary>
        /// Queue of discovered, but yet unassigned tasks.
        /// </summary>
        private MyConcurrentQueue<Task> taskQueue = new MyConcurrentQueue<Task>();
        /// <summary>
        /// Dictionary of drone task assignments, that are awaiting completion.
        /// </summary>
        private MyConcurrentDictionary<long, List<Task>> assignedTasks = new MyConcurrentDictionary<long, List<Task>>();
        /// <summary>
        /// MessageId to TaskId mapping of delegated tasks. Once the message is confirmed as acked,
        /// this scheduler can safely remove the task, as it has been picked up by a different scheduler.
        /// </summary>
        private MyConcurrentDictionary<ushort, uint> delegatedTasksNeedingAck = new MyConcurrentDictionary<ushort, uint>();
        public State currentState { get; private set; }

        private IMyGravityProviderSystemDelegate gravityProviderSystemDelegate;
        private readonly List<Job> _jobAnnouncementCache = new List<Job>();
        private readonly List<Job> _jobCache = new List<Job>();

        private readonly List<Message<Auction>> auctionOutbox = new List<Message<Auction>>();
        private readonly List<Message<AuctionWinnerAnnouncement>> auctionWinnerAnnouncementOutbox = new List<Message<AuctionWinnerAnnouncement>>();


        private static Dictionary<long, AntennaInfo> antennaCache = new Dictionary<long, AntennaInfo>();
        public int PerScanLimits { get; set; }
        private int _perScanLimits;
        public int MaxTasksAssignedPerBatch { get; set; }
        private int _maxTasksAssignedPerBatch;

        // Management
        private long entityId;
        private long _lastUpdateFrame = 0;
        private int _lastScanFrameAttempt = 0;
        private int _consecutiveErrors = 0;
        private long _lastErrorRecoveryAttemptFrame = 0;
        private long _lastMaintenanceFrame = 0;
        private long _lastAntennaCacheUpdateFrame = 0;

        // Server settings
        private readonly int _initializingUpdateIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.StateUpdateIntervalTicks.Initializing;
        private readonly int _errorUpdateIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.StateUpdateIntervalTicks.Error;
        private readonly int _standbyUpdateIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.StateUpdateIntervalTicks.Standby;
        private readonly int _scanningUpdateIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.StateUpdateIntervalTicks.Scanning;
        private readonly int _assigningUpdateIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.StateUpdateIntervalTicks.Assigning;
        private readonly int _scanRetryIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.ScanDelayTicks;
        private readonly int _errorRecoveryIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.ErrorRecoveryIntervalTicks;
        private readonly int _maxConsecutiveErrors = ServerConfig.Instance.OrchestratorServerSettings.MaxConsecutiveErrors;
        private readonly int _maintenanceIntervalTicks = ServerConfig.Instance.OrchestratorServerSettings.ManintenanceIntervalTicks;
        private readonly int _antennaCacheUpdateIntervalTicks = ServerConfig.Instance.MessageQueue.SchedulerAntennaCacheUpdateIntervalTicks();
        private readonly int _bidCollectionWindowTicks = ServerConfig.Instance.OrchestratorServerSettings.BidCollectionWindowTicks;
        private readonly int _bidBlacklistCooldownTicks = ServerConfig.Instance.OrchestratorServerSettings.BidBlacklistCooldownTicks;

        /// <summary>
        /// Timeout after which drones are removed if we don't hear from them again.
        /// </summary>
        //private static readonly int droneTimeoutSeconds = 1800;
        //private static DateTime lastCacheUpdate;


        private MessageQueue messaging;
        private int messageReadLimit = AutomataSession.GetConfig().MessageQueue.SchedulerMessageReadLimit();
        private IMyEntity Entity;
        private IMyRadioAntenna ownAntenna;
        private List<Message<IMessagePayload>> _messageCache = new List<Message<IMessagePayload>>();
        private Dictionary<uint, Auction> _activeBidRounds = new Dictionary<uint, Auction>();
        private List<Message<Bid>> bidCache = new List<Message<Bid>>();
        private readonly List<Message<TaskAborted>> _taskAbortCache = new List<Message<TaskAborted>>();
        private readonly List<Message<DroneReport>> _droneRegistrationCache = new List<Message<DroneReport>>();
        private readonly List<Message<DroneReport>> _droneReportCache = new List<Message<DroneReport>>();

        private readonly List<Message<Bid>> _jobBidCache = new List<Message<Bid>>();
        private readonly object _taskLock = new object();
        private int _jobIdCounter = 0;
        private int _messageIdCounter = 0;
        private int _taskIdCounter = 0;
        private int _bidRoundIdCounter = 0;

        private readonly List<KVPair> _componentsCache = new List<KVPair>();
        /// <summary>
        /// Reused in <see cref="ScanForWeldRepairGrindJobs"/>: weld fills via <see cref="IMySlimBlock.GetMissingComponents"/>;
        /// grind fills via mounted <see cref="IMyComponentStack"/> counts. Caller clears before each use.
        /// </summary>
        private PendingBidRoundState _pendingBidRound;
        private readonly Dictionary<long, Dictionary<uint, long>> _bidBlacklistUntilFrame = new Dictionary<long, Dictionary<uint, long>>();
        private readonly object _bidBlacklistLock = new object();

        private ConstructionComputer constructionComputer;
        private MiningSurveyor miningSurveyor;

        private static readonly Channel[] DroneManagementChannels = new Channel[]
        {
            Channel.DRONE_REGISTRATION,
            Channel.DRONE_REPORTS,
            Channel.DRONE_PERFORMANCE,
            Channel.DRONE_TASK_ASSIGNMENT,
        };

        private static readonly Channel[] logisticsManagementTopics = new Channel[]
        {
            Channel.LOGISTIC_REGISTRATION,
            Channel.LOGISTIC_UPDATE,
            Channel.LOGISTIC_REQUEST,
            Channel.LOGISTIC_PUSH,
        };
        /// <summary>
        /// Range in meters after which tasks are ignored.
        /// </summary>

        // block settings
        public bool isEnabled { get; set; }
        public OperationMode operationMode;
        public WorkModes workModes;
        private bool ignoreTasksOutsideSpecifiedRange = false;
        private float ignoreTasksOutsideSpecifiedRangeMeters = 1000.0f;
        private bool ignoreTasksOutsideOfAntenaRange = true;
        private bool _initialized = false;
        private Vector3 weldIgnoreColor;
        private Vector3 grindColor;
        private IMySessionDelegate sessionDelegate;
        private IMyCubeBlock block;
        private IAIOrchestratorSettings settings = new IAIOrchestratorSettings();
        private readonly TerminalDisplayManager terminalDisplayManager;
        //private Dictionary<TaskType, List<Task>> pendingTasks;

        private struct AntennaInfo
        {
            public Vector3D Position;
            public double Range;
            public bool IsWorking;
            public long GridId;
            public DateTime LastUpdate;
        }

        public IAIOrchestrator(
            IMyEntity entity,
            OperationMode operationMode = OperationMode.Orchestrator,
            WorkModes workModes = WorkModes.None,
            IMySessionDelegate sessionDelegate = null,
            IMyGravityProviderSystemDelegate gravityProviderSystem = null)
        {
            this.entityId = entity.EntityId;
            this.Entity = entity;
            this.block = (IMyCubeBlock)Entity;
            this.operationMode = operationMode;
            this.workModes = workModes;
            this.gravityProviderSystemDelegate = gravityProviderSystem ?? new MyGravityProviderSystemDelegate();
            this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
            this.terminalDisplayManager = new TerminalDisplayManager(block as IMyTerminalBlock, 5);

            this.constructionComputer = new IAIConstructionComputer(
                entity,
                ConstructionComputer.OperationMode.BuiltInToOrchestrator,
                jobQueue,
                GetConstructionComputerWorkModes(workModes),
                ownAntenna,
                gravityProviderSystemDelegate, sessionDelegate, new IAIConstructionComputerSettings
                {
                    WeldIgnoreColor = new Vector3Data(0.0f, 1.0f, 0.0f),
                    GrindColor = new Vector3Data(1.0f, 0.0f, 0.0f),
                    IgnoreTasksOutsideSpecifiedRangeMeters = 1000.0f,
                    ScanRetryIntervalSeconds = 600,
                    PerScanLimits = 100,
                    WorkModes = GetConstructionComputerWorkModes(workModes),
                    ShareWith = ShareWith.NoOne,
                    IgnoreTasksOutsideSpecifiedRange = false,
                    IgnoreTasksOutsideOfAntenaRange = true,
                    IsEnabled = true,
                    OperationMode = ConstructionComputer.OperationMode.BuiltInToOrchestrator,
                    WeldIgnoreList = new List<ulong>(),
                    GrindIgnoreList = new List<ulong>(),
                });

            _maxTasksAssignedPerBatch = MaxTasksAssignedPerBatch > ServerConfig.Instance.OrchestratorServerSettings.MaxTaskAssignmentPerBatch
                ? ServerConfig.Instance.OrchestratorServerSettings.MaxTaskAssignmentPerBatch
                : MaxTasksAssignedPerBatch;

            // _perScanLimits = PerScanLimits > ServerConfig.Instance.OrchestratorServerSettings.Scan ? ServerConfig.Instance.OrchestratorServerSettings.PerScanLimit : PerScanLimits;

        }


        /// <summary>
        /// Backward-compatible entry point; delegates to <see cref="UpdateAI"/>.
        /// </summary>
        public void UpdateBeforeSimulation10()
        {
            UpdateAI();
        }

        /// <summary>
        /// Main orchestrator tick: self-throttles by <see cref="GetUpdateIntervalTicks"/>, runs one assignment
        /// pipeline step when in assigning sub-states, and performs peripheral I/O when not initializing or in error.
        /// </summary>
        public void UpdateAI()
        {
            if (!AntennaUtil.AntennaReady(ownAntenna) && currentState != State.Error && _initialized)
            {
                Log.Warning("Orhcestrator {0} antenna check failed, transitioning to error state", entityId);
                currentState = State.Error;
            }
            if (!_initialized && currentState != State.Initializing)
            {
                Initialize();
                return;
            }

            long currentFrame = sessionDelegate.GameplayFrameCounter;
            int updateInterval = GetUpdateIntervalTicks();
            if (currentFrame - _lastUpdateFrame < updateInterval)
            {
                return;
            }

            _lastUpdateFrame = currentFrame;

            try
            {
                if (currentFrame - _lastMaintenanceFrame >= _maintenanceIntervalTicks)
                {
                    _lastMaintenanceFrame = currentFrame;
                }

                if (currentState == State.AssigningTasks)
                {
                    currentState = State.AssigningTasksReadBidRoundBids;
                }

                switch (currentState)
                {
                    case State.Initializing:
                        HandleInitializing();
                        break;
                    case State.Standby:
                        HandleStandby();
                        break;
                    case State.ScanningForJobs:
                        HandleScanningForJobs();
                        break;
                    case State.AssigningTasksReadBidRoundBids:
                    case State.AssigningTasksProcessBidRoundBids:
                    case State.AssigningTasksPublishBidRoundWinner:
                    case State.AssigningTasksReadTaskBids:
                    case State.AssigningTasksFinalizePendingBidRound:
                    case State.AssigningTasksStartNextBidRound:
                    case State.AssigningTasksEvaluateStandbyTransition:
                        RunAssigningTasksPipelineStep(currentFrame);
                        break;
                    case State.Error:
                        HandleError();
                        break;
                    default:
                        Log.Warning("Orhcestrator {0}: unknown state {1}, returning to Standby", entityId, currentState);
                        currentState = State.Standby;
                        break;
                }

                if (currentState != State.Error && currentState != State.Initializing)
                {
                    if (miningSurveyor != null)
                    {
                        miningSurveyor.Update(ownAntenna);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Orhcestrator {0} update threw exception: {1}", entityId, ex.Message);
                currentState = State.Error;
            }
        }

        private int GetUpdateIntervalTicks()
        {
            switch (currentState)
            {
                case State.Initializing:
                    return _initializingUpdateIntervalTicks;
                case State.Error:
                    return _errorUpdateIntervalTicks;
                case State.Standby:
                    return _standbyUpdateIntervalTicks;
                case State.ScanningForJobs:
                    return _scanningUpdateIntervalTicks;
                case State.AssigningTasks:
                case State.AssigningTasksReadBidRoundBids:
                case State.AssigningTasksProcessBidRoundBids:
                case State.AssigningTasksPublishBidRoundWinner:
                case State.AssigningTasksReadTaskBids:
                case State.AssigningTasksFinalizePendingBidRound:
                case State.AssigningTasksStartNextBidRound:
                case State.AssigningTasksEvaluateStandbyTransition:
                    return _assigningUpdateIntervalTicks;
                default:
                    return 600; // Default 10 second updates
            }
        }

        private void Initialize()
        {
            messaging = IAISession.GetMessageQueue();
            currentState = State.Initializing;
            messaging.Subscribe(entityId, Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT);
            messaging.Subscribe(entityId, Channel.DRONE_REGISTRATION);
            messaging.Subscribe(entityId, Channel.DRONE_REPORTS);
            messaging.Subscribe(entityId, Channel.DRONE_PERFORMANCE);
            messaging.Subscribe(entityId, Channel.LOGISTIC_REGISTRATION);
            messaging.Subscribe(entityId, Channel.LOGISTIC_UPDATE);
            messaging.Subscribe(entityId, Channel.LOGISTIC_REQUEST);
            messaging.Subscribe(entityId, Channel.LOGISTIC_PUSH);
            messaging.Subscribe(entityId, Channel.SCHEDULER_FORWARD);
            messaging.Subscribe(entityId, Channel.DIRECT_MESSAGE);

            miningSurveyor = new IAIMiningSurveyor(
                Entity,
                messaging,
                MiningSurveyor.OperationMode.BuiltInToOrchestrator,
                jobQueue,
                new IAIMiningSurveyorSettings
                {
                    IsEnabled = true,
                    OperationMode = MiningSurveyor.OperationMode.BuiltInToOrchestrator,
                    WorkModes = workModes.HasFlag(WorkModes.MineOre)
                        ? MiningSurveyor.WorkModes.ScanAndPublish
                        : MiningSurveyor.WorkModes.ScanOnly,
                    ShareWith = ShareWith.NoOne,
                },
                gravityProviderSystemDelegate);
        }

        private void HandleInitializing()
        {
            if (CheckCapabilities())
            {
                if (AntennaUtil.AntennaReady(ownAntenna))
                {
                    UpdateAntennaCache(ownAntenna);
                    currentState = State.Standby;
                    _initialized = true;
                }
            }
            else
            {
                currentState = State.Error;
            }
        }



        private void HandleStandby()
        {
            var currentFrame = sessionDelegate.GameplayFrameCounter;
            // Periodically check if we should scan
            if (currentFrame - _lastScanFrameAttempt >= _scanRetryIntervalTicks)
            {
                _lastScanFrameAttempt = currentFrame;
                if (ShouldStartScanning())
                {
                    Log.Verbose("Orhcestrator {0} waking from standby to scan for tasks", entityId);
                    currentState = State.ScanningForJobs;
                }
            }
        }

        private bool ShouldStartScanning()
        {
            var workModeEval = workModes & (
                WorkModes.ScanOnly |
                WorkModes.Grind |
                WorkModes.FetchCargo |
                WorkModes.DeliverCargo |
                WorkModes.WeldUnfinishedBlocks);
            return isEnabled && AntennaUtil.AntennaReady(ownAntenna) && workModeEval > 0;
        }
        /// <summary>
        /// Scan connected grids for radio antennas. Select only the largest range for broadcasting.
        /// Only selects mechanically connected grids (rotor, piston, wheel suspension).
        /// </summary>
        /// <returns></returns>
        private bool CheckCapabilities()
        {
            var thisBlock = Entity as IMyCubeBlock;
            if (thisBlock == null) return false;
            IMyRadioAntenna localAntenna = null;

            List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(thisBlock.CubeGrid, GridLinkTypeEnum.Mechanical, connectedGrids);

            foreach (var grid in connectedGrids)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var block in blocks)
                {
                    if (block is IMyRadioAntenna)
                    {
                        if (localAntenna == null) localAntenna = (IMyRadioAntenna)block;
                        else if (((IMyRadioAntenna)block).Radius > localAntenna.Radius)
                            localAntenna = (IMyRadioAntenna)block;
                    }
                }
            }
            ownAntenna = localAntenna;

            return ownAntenna != null &&
                ownAntenna.IsFunctional &&
                ownAntenna.Enabled &&
                ownAntenna.EnableBroadcasting &&
                ownAntenna.IsWorking;
        }



        private void HandleScanningForJobs()
        {
            var totalTasks = 0;
            try
            {
                totalTasks += constructionComputer.ScanForWeldRepairGrindJobs(jobQueue);
            }
            catch (Exception ex)
            {
                Log.Error("Orhcestrator {0} Error during task scanning: {1}", entityId, ex.Message);
                currentState = State.Error;
                return;
            }

            if (totalTasks == 0)
            {
                Log.Verbose("Scheduler {0] going on standby, no tasks found.", entityId);
                currentState = State.Standby;
                return;
            }
            Log.Verbose("Orhcestrator {0} found {1} tasks, entering assigning pipeline", entityId, totalTasks);
            currentState = State.AssigningTasksReadBidRoundBids;
        }

        private bool IsOrchestratorBidRoundWorkEnabled()
        {
            return operationMode.HasFlag(OperationMode.Orchestrator);
        }

        /// <summary>
        /// Runs exactly one step of the assigning pipeline; advances <see cref="currentState"/> to the next step.
        /// </summary>
        private void RunAssigningTasksPipelineStep(long currentFrame)
        {
            switch (currentState)
            {
                case State.AssigningTasksReadBidRoundBids:
                    if (IsOrchestratorBidRoundWorkEnabled())
                    {
                        ReadBidRoundBidMessages();
                    }
                    currentState = State.AssigningTasksProcessBidRoundBids;
                    break;

                case State.AssigningTasksProcessBidRoundBids:
                    if (IsOrchestratorBidRoundWorkEnabled())
                    {
                        ProcessBidRoundBidMessages();
                    }
                    currentState = State.AssigningTasksPublishBidRoundWinner;
                    break;

                case State.AssigningTasksPublishBidRoundWinner:
                    if (IsOrchestratorBidRoundWorkEnabled())
                    {
                        BroadcastAuctionWinnerAnnouncementOne();
                    }
                    currentState = State.AssigningTasksReadTaskBids;
                    break;


                default:
                    currentState = State.AssigningTasksReadBidRoundBids;
                    break;
            }
        }


        private long GetSchedulerOwnerId()
        {
            IMyCubeBlock cube = Entity as IMyCubeBlock;
            return cube != null ? cube.OwnerId : 0L;
        }

        private bool IsBidBlacklisted(long bidderId, uint taskId, long currentFrame)
        {
            lock (_bidBlacklistLock)
            {
                Dictionary<uint, long> inner;
                if (!_bidBlacklistUntilFrame.TryGetValue(bidderId, out inner))
                    return false;
                long untilFrame;
                if (!inner.TryGetValue(taskId, out untilFrame))
                    return false;
                if (currentFrame >= untilFrame)
                {
                    inner.Remove(taskId);
                    if (inner.Count == 0)
                        _bidBlacklistUntilFrame.Remove(bidderId);
                    return false;
                }
                return true;
            }
        }

        private void AddBidBlacklist(long bidderId, uint taskId, long currentFrame)
        {
            lock (_bidBlacklistLock)
            {
                Dictionary<uint, long> inner;
                if (!_bidBlacklistUntilFrame.TryGetValue(bidderId, out inner))
                {
                    inner = new Dictionary<uint, long>();
                    _bidBlacklistUntilFrame[bidderId] = inner;
                }
                inner[taskId] = currentFrame + _bidBlacklistCooldownTicks;
            }
        }

       

     
        private int GetTotalAssignedTaskCount()
        {
            int total = 0;
            foreach (KeyValuePair<long, List<Task>> kvp in assignedTasks)
            {
                total += kvp.Value.Count;
            }
            return total;
        }

        private void MaybeTransitionAssigningToStandby()
        {
            if (taskQueue.Count != 0 || _pendingBidRound != null)
                return;
            if (GetTotalAssignedTaskCount() > 0)
                return;
            Log.Verbose("Orhcestrator {0}: task queue drained, returning to Standby", entityId);
            currentState = State.Standby;
        }

        private void UpdateAntennaCache(IMyRadioAntenna schedulerAntenna)
        {
            var currentFrame = sessionDelegate.GameplayFrameCounter;
            var updateInterval = _antennaCacheUpdateIntervalTicks;

            if (currentFrame - _lastAntennaCacheUpdateFrame < updateInterval)
                return;

            _lastAntennaCacheUpdateFrame = currentFrame;
            antennaCache.Clear();

            try
            {
                var allAntennas = new HashSet<IMyEntity>();

                MyAPIGateway.Entities.GetEntities(allAntennas);

                foreach (var antenna in allAntennas.Cast<IMyRadioAntenna>())
                {
                    if (antenna?.CubeGrid == null) continue;

                    // shouldn't be talking to antennas out of range
                    var distance = Vector3D.Distance(schedulerAntenna.GetPosition(), antenna.GetPosition());
                    if (distance < schedulerAntenna.Radius)
                    {
                        antennaCache.Remove(antenna.EntityId);
                        continue;
                    }

                    var antennaInfo = new AntennaInfo
                    {
                        Position = antenna.GetPosition(),
                        Range = antenna.IsWorking && antenna.EnableBroadcasting ? antenna.Radius : 0.0,
                        IsWorking = antenna.IsWorking && antenna.EnableBroadcasting,
                        GridId = antenna.CubeGrid.EntityId,
                        LastUpdate = DateTime.UtcNow
                    };

                    antennaCache[antenna.EntityId] = antennaInfo;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Antenna cache update failed: {ex}");
            }
        }

        public void ResetScheduler() {}


        /// <summary>
        /// Construction job announcements always come from ConstructionComputers.
        /// </summary>
        private void HandleConstructionComputerJobAnnouncements()
        {
            messaging.ReadMessages(entityId, ownAntenna, Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT, _messageCache, 50, true, PayloadType.JobAnnouncement);
            _jobCache.Clear();
            foreach (var message in _messageCache)
            {
                var job = message.Payload as Job;
                if (job == null)
                    continue;
                _jobCache.Add(job);
            }
            if (_jobCache.Count > 0)
            {
                // SummarizeJobs();
                foreach (var job in _jobCache)
                {
                    StartBidRound(job);
                }
            }
        }
        /// <summary>
        /// Summarize jobs by simple heuristics:
        /// - Build jobs sharing the same, single component, should be summarized into a single job, prefer 1 drone over several
        /// - Build jobs near the same location shoud be summarized into a single job, prefer 1 drone over several
        /// - Cargo deliveries should be summarized by route, using simple distance heuristics
        /// - Inevntory surplus collection should be summarized by route using simple distance heuristics
        /// </summary>
        private void SummarizeJobs()
        {
            foreach (var job in _jobCache)
            {
                job.BlockCount = 1;
            }
        }


        private void StartBidRound(Job job)
        {
            // get inventory
            // get mass and volume of inventory
            // Auction:
            //      LC responds with "I can fulfill this inventory"
            //      drones respond with I can carry this much, I can handle the environment
            // BidRoundEnd:
            //      Winner is announced
            //      
            MyFixedPoint totalMass = 0;
            MyFixedPoint totalVolume = 0;
            Message<Auction> msg = null;
            int messagesQueued = 0;

            switch (job.JobType)
            {
                case JobType.WeldBlock:
                    job.ComponentsInventory.GetAllItems(_componentsCache, clear: true);
                    totalMass = job.ComponentsInventory.TotalInventoryMass(IAISession.Instance);
                    totalVolume = job.ComponentsInventory.TotalInventoryVolume(IAISession.Instance);
                    msg = new Message<Auction>
                    {
                        Payload = new Auction
                        {
                            JobId = job.JobId,
                            JobType = job.JobType,
                            ComponentsInventory = job.ComponentsInventory,
                            BlocksInventory = job.BlocksInventory,
                            PositionData = job.PositionData,
                            OrientationData = job.OrientationData,
                            CreatedTime = job.CreatedTime,
                            ExpirationTime = job.CreatedTime.AddSeconds(settings.MaxBidRoundExpirationSeconds),
                            EntityType = IAIEntityType.OrchestratorBlock,
                            NaturalGravity = job.NaturalGravity,
                            IsStaticGrid = job.IsStaticGrid,
                            IsInSpace = job.IsInSpace,
                            IsInAtmosphere = job.IsInAtmosphere,
                            TotalMass = totalMass,
                            TotalVolume = totalVolume,
                            AuctionId = IdGenerator.GenerateId(ref _bidRoundIdCounter, entityId),
                        },
                        MessageId = IdGenerator.GenerateId(ref _messageIdCounter, entityId),
                        CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                        SenderId = entityId,
                        SenderOwnerId = block?.OwnerId ?? 0,
                        RequiresAck = false,
                        RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                        Channel = Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
                    };

                    auctionOutbox.Add(msg);
                    messagesQueued++;
                    break;
                case JobType.GrindBlock:
                    job.ComponentsInventory.GetAllItems(_componentsCache, clear: true);
                    totalMass = job.ComponentsInventory.TotalInventoryMass(IAISession.Instance);
                    totalVolume = job.ComponentsInventory.TotalInventoryVolume(IAISession.Instance);
                    msg = new Message<Auction>
                    {
                        Payload = new Auction
                        {
                            JobId = job.JobId,
                            JobType = job.JobType,
                            ComponentsInventory = job.ComponentsInventory,
                            BlocksInventory = job.BlocksInventory,
                            PositionData = job.PositionData,
                            OrientationData = job.OrientationData,
                            CreatedTime = job.CreatedTime,
                            ExpirationTime = job.CreatedTime.AddSeconds(settings.MaxBidRoundExpirationSeconds),
                            EntityType = IAIEntityType.OrchestratorBlock,
                            NaturalGravity = job.NaturalGravity,
                            IsStaticGrid = job.IsStaticGrid,
                            IsInSpace = job.IsInSpace,
                            IsInAtmosphere = job.IsInAtmosphere,
                            TotalMass = totalMass,
                            TotalVolume = totalVolume,
                            AuctionId = IdGenerator.GenerateId(ref _bidRoundIdCounter, entityId),
                        },
                        MessageId = IdGenerator.GenerateId(ref _messageIdCounter, entityId),
                        CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                        SenderId = entityId,
                        SenderOwnerId = block?.OwnerId ?? 0,
                        RequiresAck = false,
                        RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                        Channel = Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
                    };

                    auctionOutbox.Add(msg);
                    messagesQueued++;
                    break;
                case JobType.DeliverMissingInventory:
                case JobType.CollectInventorySurplus:
                case JobType.MineOre:
                    Log.Verbose("AI scheduler {0}: bid round for job type {1} not implemented yet (job {2})", entityId, job.JobType, job.JobId);
                    break;
            }
            if (messagesQueued > 0)
            {
                Log.Verbose("AI scheduler {0}: {1} bid rounds started for job {2}", entityId, messagesQueued, job.JobId);
            }
            foreach (var message in auctionOutbox)
            {
                _activeBidRounds[message.Payload.AuctionId] = message.Payload;
                messaging.BroadcastMessage<Auction>(ownAntenna, message, true);
                auctionOutbox.Remove(message);
            }
            if (auctionOutbox.Count > 0)
            {
                Log.Verbose("AI scheduler {0}: {1} bid rounds remaining for job {2}", entityId, auctionOutbox.Count, job.JobId);
            }
        }

        private void ReadBidRoundBidMessages()
        {
            messaging.ReadMessages(entityId, ownAntenna, Channel.ORCHESTRATOR_AUCTION_BIDS, bidCache, 100, true, PayloadType.Bid);
            Echo("AI scheduler {0}: read {1} bid round bid messages", entityId, bidCache.Count);
        }

        private void ProcessBidRoundBidMessages()
        {
            foreach (var message in bidCache)
            {
                List<Bid> potentialWinnerLCs = new List<Bid>();
                List<Bid> potentialWinnerDrones = new List<Bid>();
                Auction bidRound;
                if (!_activeBidRounds.TryGetValue(message.Payload.AuctionId, out bidRound))
                {
                    Echo("WARN: Auction {0} not found for bid {1}", message.Payload.AuctionId, message.Payload.AuctionId);
                    Log.Warning("AI scheduler {0}: Auction {1} not found", entityId, message.Payload.AuctionId);
                    continue;
                }
                Bid bid = message.Payload;
                if (bid == null)
                    continue;
                if (MinimialCheckBidAgainstBidRoundStart(bid, bidRound) && bid.EntityType == IAIEntityType.LogisticsComputerBlock)
                {
                    potentialWinnerLCs.Add(bid);
                }
                if (MinimialCheckBidAgainstBidRoundStart(bid, bidRound) && bid.EntityType == IAIEntityType.DroneControllerBlock)
                {
                    potentialWinnerDrones.Add(bid);
                }
                if (potentialWinnerLCs.Count == 0 || potentialWinnerDrones.Count == 0)
                {
                    if (potentialWinnerLCs.Count == 0)
                    {
                        Echo("WARN: no LC bid for job {0}", bidRound.JobId);
                    }
                    if (potentialWinnerDrones.Count == 0)
                    {
                        Echo("WARN: no drone bid for job {0}", bidRound.JobId);
                    }
                    continue;
                }
                List<Bid> winningLCs = SelectWinningLcsForJob(potentialWinnerLCs, bidRound);
                List<Bid> winningDrones = SelectWinningDronesForJob(potentialWinnerDrones, bidRound);
                EnqueueBidRoundWinnerAnnouncementFromWinners(winningDrones, winningLCs, bidRound);
            }

            bidCache.Clear();
        }

        /// <summary>
        /// Sends at most one queued winner announcement per tick (FIFO).
        /// </summary>
        private void BroadcastAuctionWinnerAnnouncementOne()
        {
            if (auctionWinnerAnnouncementOutbox.Count == 0)
            {
                return;
            }

            Message<AuctionWinnerAnnouncement> msg = auctionWinnerAnnouncementOutbox[0];
            if (msg == null || msg.Payload == null)
            {
                auctionWinnerAnnouncementOutbox.RemoveAt(0);
                return;
            }

            ErrorCode sendError = messaging.BroadcastMessage(ownAntenna, msg, false);
            if (sendError != ErrorCode.None)
            {
                Log.Warning(
                    "AI scheduler {0}: AuctionWinnerAnnouncement broadcast deferred for job {1} ({2})",
                    entityId,
                    msg.Payload.JobId,
                    sendError);
                return;
            }

            uint bidRoundId = msg.Payload.AuctionId;
            _activeBidRounds.Remove(bidRoundId);
            auctionWinnerAnnouncementOutbox.RemoveAt(0);
            int assigneeCount = msg.Payload.TaskAssignments != null ? msg.Payload.TaskAssignments.Count : 0;
            Log.Info("AI scheduler {0}: broadcast AuctionWinnerAnnouncement for job {1} ({2} assignees)", entityId, msg.Payload.JobId, assigneeCount);
        }

        private void EnqueueBidRoundWinnerAnnouncementFromWinners(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            if (bidRound == null)
            {
                return;
            }

            switch (bidRound.JobType)
            {
                case JobType.WeldBlock:
                    TryBuildAndEnqueueWeldBidRoundWinnerAnnouncement(winningDrones, winningLCs, bidRound);
                    break;
                case JobType.GrindBlock:
                    TryBuildAndEnqueueGrindBidRoundWinnerAnnouncement(winningDrones, winningLCs, bidRound);
                    break;
                case JobType.DeliverMissingInventory:
                    TryBuildAndEnqueueDeliverMissingInventoryBidRoundWinnerAnnouncement(winningDrones, winningLCs, bidRound);
                    break;
                case JobType.CollectInventorySurplus:
                    TryBuildAndEnqueueCollectInventorySurplusBidRoundWinnerAnnouncement(winningDrones, winningLCs, bidRound);
                    break;
                case JobType.MineOre:
                    TryBuildAndEnqueueMineOreBidRoundWinnerAnnouncement(winningDrones, winningLCs, bidRound);
                    break;
                default:
                    Log.Verbose("AI scheduler {0}: no winner announcement builder for job type {1}", entityId, bidRound.JobType);
                    break;
            }
        }

        private void TryBuildAndEnqueueWeldBidRoundWinnerAnnouncement(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            if (winningDrones == null || winningDrones.Count == 0 || winningLCs == null || winningLCs.Count == 0)
            {
                return;
            }

            Dictionary<long, List<Task>> taskAssignments = BuildWeldTaskAssignments(winningDrones, winningLCs, bidRound);
            if (taskAssignments.Count == 0)
            {
                return;
            }

            AuctionWinnerAnnouncement payload = new AuctionWinnerAnnouncement
            {
                TaskAssignments = taskAssignments,
                CreatedTime = DateTime.UtcNow,
                EntityId = entityId,
                JobId = bidRound.JobId,
                AuctionId = bidRound.AuctionId,
                BidId = winningDrones[0].BidId,
            };

            Message<AuctionWinnerAnnouncement> msg = new Message<AuctionWinnerAnnouncement>
            {
                Payload = payload,
                MessageId = IdGenerator.GenerateId(ref _messageIdCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = GetSchedulerOwnerId(),
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Drone | MessageQueue.IAIBlockType.LogisticsComputer,
                Channel = Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT,
            };

            auctionWinnerAnnouncementOutbox.Add(msg);
        }

        private static void TryBuildAndEnqueueGrindBidRoundWinnerAnnouncement(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            // Placeholder: grind job winner announcement and task decomposition not implemented yet.
        }

        private static void TryBuildAndEnqueueDeliverMissingInventoryBidRoundWinnerAnnouncement(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            // Placeholder.
        }

        private static void TryBuildAndEnqueueCollectInventorySurplusBidRoundWinnerAnnouncement(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            // Placeholder.
        }

        private static void TryBuildAndEnqueueMineOreBidRoundWinnerAnnouncement(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            // Placeholder.
        }

        private List<Bid> SelectWinningLcsForJob(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            if (bidRound == null)
            {
                return new List<Bid>();
            }

            switch (bidRound.JobType)
            {
                case JobType.WeldBlock:
                    return SelectWinningLCs(potentialWinnerLCs, bidRound);
                case JobType.GrindBlock:
                    return SelectWinningLCsForGrindBlock(potentialWinnerLCs, bidRound);
                case JobType.DeliverMissingInventory:
                    return SelectWinningLCsForDeliverMissingInventory(potentialWinnerLCs, bidRound);
                case JobType.CollectInventorySurplus:
                    return SelectWinningLCsForCollectInventorySurplus(potentialWinnerLCs, bidRound);
                case JobType.MineOre:
                    return SelectWinningLCsForMineOre(potentialWinnerLCs, bidRound);
                default:
                    return new List<Bid>();
            }
        }

        private List<Bid> SelectWinningDronesForJob(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            if (bidRound == null)
            {
                return new List<Bid>();
            }

            switch (bidRound.JobType)
            {
                case JobType.WeldBlock:
                    return SelectWinningDrones(potentialWinnerDrones, bidRound);
                case JobType.GrindBlock:
                    return SelectWinningDronesForGrindBlock(potentialWinnerDrones, bidRound);
                case JobType.DeliverMissingInventory:
                    return SelectWinningDronesForDeliverMissingInventory(potentialWinnerDrones, bidRound);
                case JobType.CollectInventorySurplus:
                    return SelectWinningDronesForCollectInventorySurplus(potentialWinnerDrones, bidRound);
                case JobType.MineOre:
                    return SelectWinningDronesForMineOre(potentialWinnerDrones, bidRound);
                default:
                    return new List<Bid>();
            }
        }

        private static List<Bid> SelectWinningLCsForGrindBlock(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningLCsForDeliverMissingInventory(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningLCsForCollectInventorySurplus(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningLCsForMineOre(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningDronesForGrindBlock(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningDronesForDeliverMissingInventory(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningDronesForCollectInventorySurplus(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            return new List<Bid>();
        }

        private static List<Bid> SelectWinningDronesForMineOre(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            return new List<Bid>();
        }

        private Dictionary<long, List<Task>> BuildWeldTaskAssignments(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            Dictionary<long, List<Task>> assignments = new Dictionary<long, List<Task>>();
            Dictionary<long, double> remainingLcVolume = BuildRemainingLcVolumeMap(winningLCs);

            Inventory requiredInventory = bidRound.ComponentsInventory ?? new Inventory();
            double requiredVolume = (double)bidRound.TotalVolume;
            if (requiredVolume <= 0.0 && !requiredInventory.IsEmpty())
            {
                requiredVolume = (double)requiredInventory.TotalInventoryVolume();
            }

            if (requiredVolume <= 0.0)
            {
                return assignments;
            }

            Vector3D jobPosition = bidRound.PositionData.ToVector3D();
            QuaternionDData jobOrientation = bidRound.OrientationData;
            double remainingVolume = requiredVolume;
            int droneIndex = 0;
            int maxIterations = winningDrones.Count * 8;

            while (remainingVolume > 0.001 && maxIterations > 0)
            {
                Bid drone = winningDrones[droneIndex];
                droneIndex = (droneIndex + 1) % winningDrones.Count;
                maxIterations--;

                if (drone == null || drone.IOLocationData == null)
                {
                    continue;
                }

                double droneTripCapacity = GetDronePreferredTripVolume(drone);
                if (droneTripCapacity <= 0.0)
                {
                    continue;
                }

                Bid lc = SelectBestLcForDrone(drone, winningLCs, remainingLcVolume);
                if (lc == null || lc.IOLocationData == null)
                {
                    break;
                }

                double lcRemaining = remainingLcVolume[lc.EntityId];
                double tripVolume = Math.Min(remainingVolume, Math.Min(droneTripCapacity, lcRemaining));
                if (tripVolume <= 0.0)
                {
                    remainingLcVolume[lc.EntityId] = 0.0;
                    continue;
                }

                List<Task> droneTasks;
                if (!assignments.TryGetValue(drone.EntityId, out droneTasks))
                {
                    droneTasks = new List<Task>();
                    assignments[drone.EntityId] = droneTasks;
                }

                Inventory tripPayload = BuildTripPayload(requiredInventory, requiredVolume, tripVolume);
                AppendWeldTripTasks(droneTasks, lc, bidRound, jobPosition, jobOrientation, tripPayload);

                remainingVolume -= tripVolume;
                remainingLcVolume[lc.EntityId] = Math.Max(0.0, lcRemaining - tripVolume);
            }

            foreach (KeyValuePair<long, List<Task>> kv in assignments)
            {
                List<Task> droneTasks = kv.Value;
                if (droneTasks.Count == 0)
                {
                    continue;
                }
                Task returnHomeTask = new Task
                {
                    TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                    JobId = bidRound.JobId,
                    TaskType = TaskType.ReturnHome,
                    CreatedTime = DateTime.UtcNow,
                    AssignedBy = entityId,
                    AssignedTime = DateTime.UtcNow,
                    PositionData = bidRound.PositionData,
                    OrientationData = bidRound.OrientationData,
                    Payload = null,
                    OutOfOrchestratorRange = false,
                };
                droneTasks.Add(returnHomeTask);
            }

            return assignments;
        }

        private Dictionary<long, double> BuildRemainingLcVolumeMap(List<Bid> winningLCs)
        {
            Dictionary<long, double> remaining = new Dictionary<long, double>();
            for (int i = 0; i < winningLCs.Count; i++)
            {
                Bid bid = winningLCs[i];
                if (bid == null)
                {
                    continue;
                }

                double availableVolume = 0.0;
                if (bid.BidInventory != null && !bid.BidInventory.IsEmpty())
                {
                    availableVolume = (double)bid.BidInventory.TotalInventoryVolume();
                }
                if (availableVolume <= 0.0)
                {
                    availableVolume = Math.Max((double)bid.MaxCargoVolume, 0.0);
                }
                remaining[bid.EntityId] = availableVolume;
            }
            return remaining;
        }

        private static double GetDronePreferredTripVolume(Bid drone)
        {
            double maxCargoVolume = Math.Max((double)drone.MaxCargoVolume, 0.0);
            double maxLoadIn1G = Math.Max((double)drone.MaxLoadIn1G, 0.0);
            double optimalLoadIn1G = Math.Max((double)drone.OptimalLoadIn1G, 0.0);
            if (maxCargoVolume <= 0.0)
            {
                return 0.0;
            }
            if (maxLoadIn1G <= 0.0 || optimalLoadIn1G <= 0.0)
            {
                return maxCargoVolume;
            }

            double ratio = Math.Min(1.0, optimalLoadIn1G / maxLoadIn1G);
            double preferred = maxCargoVolume * ratio;
            return Math.Max(0.001, preferred);
        }

        private static Bid SelectBestLcForDrone(Bid drone, List<Bid> lcs, Dictionary<long, double> remainingLcVolume)
        {
            Vector3D dronePosition = drone.IOLocationData.PositionData.ToVector3D();
            Bid best = null;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < lcs.Count; i++)
            {
                Bid lc = lcs[i];
                if (lc == null || lc.IOLocationData == null)
                {
                    continue;
                }

                double remainingVolume;
                if (!remainingLcVolume.TryGetValue(lc.EntityId, out remainingVolume) || remainingVolume <= 0.0)
                {
                    continue;
                }

                Vector3D lcPosition = lc.IOLocationData.PositionData.ToVector3D();
                double d2 = Vector3D.DistanceSquared(dronePosition, lcPosition);
                if (d2 < bestDistance)
                {
                    bestDistance = d2;
                    best = lc;
                }
            }

            return best;
        }

        private static Inventory BuildTripPayload(Inventory requiredInventory, double requiredVolume, double tripVolume)
        {
            if (requiredInventory == null || requiredInventory.IsEmpty() || requiredVolume <= 0.0 || tripVolume <= 0.0)
            {
                return null;
            }

            double ratio = Math.Min(1.0, tripVolume / requiredVolume);
            Inventory payload = new Inventory();
            List<KVPair> items = requiredInventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                KVPair item = items[i];
                int amount = item.Value;
                if (amount <= 0)
                {
                    continue;
                }
                int scaledAmount = (int)Math.Ceiling(amount * ratio);
                if (scaledAmount <= 0)
                {
                    continue;
                }
                payload.AddItem(item.Key, scaledAmount);
            }

            return payload.IsEmpty() ? null : payload;
        }

        private void AppendWeldTripTasks(List<Task> droneTasks, Bid lc, Auction bidRound, Vector3D jobPosition, QuaternionDData jobOrientation, Inventory tripPayload)
        {
            Vector3D lcPosition = lc.IOLocationData.PositionData.ToVector3D();
            QuaternionDData lcOrientation = lc.IOLocationData.OrientationData;

            droneTasks.Add(new Task
            {
                TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                JobId = bidRound.JobId,
                TaskType = TaskType.ApproachLocation,
                CreatedTime = DateTime.UtcNow,
                AssignedBy = entityId,
                AssignedTime = DateTime.UtcNow,
                PositionData = Vector3DData.FromVector3D(lcPosition),
                OrientationData = lcOrientation,
                Payload = null,
                OutOfOrchestratorRange = false,
            });

            droneTasks.Add(new Task
            {
                TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                JobId = bidRound.JobId,
                TaskType = TaskType.CollectInventory,
                CreatedTime = DateTime.UtcNow,
                AssignedBy = entityId,
                AssignedTime = DateTime.UtcNow,
                PositionData = Vector3DData.FromVector3D(lcPosition),
                OrientationData = lcOrientation,
                Payload = tripPayload,
                OutOfOrchestratorRange = false,
            });

            droneTasks.Add(new Task
            {
                TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                JobId = bidRound.JobId,
                TaskType = TaskType.ApproachLocation,
                CreatedTime = DateTime.UtcNow,
                AssignedBy = entityId,
                AssignedTime = DateTime.UtcNow,
                PositionData = Vector3DData.FromVector3D(jobPosition),
                OrientationData = jobOrientation,
                Payload = null,
                OutOfOrchestratorRange = false,
            });

            droneTasks.Add(new Task
            {
                TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                JobId = bidRound.JobId,
                TaskType = TaskType.WeldBlock,
                CreatedTime = DateTime.UtcNow,
                AssignedBy = entityId,
                AssignedTime = DateTime.UtcNow,
                PositionData = Vector3DData.FromVector3D(jobPosition),
                OrientationData = jobOrientation,
                Payload = tripPayload,
                OutOfOrchestratorRange = false,
            });
        }

        /// <summary>
        /// Filters bid rounds against bids, for minimal requirements.
        /// This method purposely avoids complex heuristics like distance, optimal load, etc.
        /// </summary>
        /// <param name="bid"></param>
        private bool MinimialCheckBidAgainstBidRoundStart(Bid bid, Auction bidRound)
        {
            if (bidRound == null || bid == null)
            {
                return false;
            }

            switch (bidRound.JobType)
            {
                case JobType.WeldBlock:
                    return MinimialCheckWeldBlockBidAgainstBidRoundStart(bid, bidRound);
                case JobType.GrindBlock:
                    return MinimialCheckGrindBlockBidAgainstBidRoundStart(bid, bidRound);
                case JobType.DeliverMissingInventory:
                    return MinimialCheckDeliverMissingInventoryBidAgainstBidRoundStart(bid, bidRound);
                case JobType.CollectInventorySurplus:
                    return MinimialCheckCollectInventorySurplusBidAgainstBidRoundStart(bid, bidRound);
                case JobType.MineOre:
                    return MinimialCheckMineOreBidAgainstBidRoundStart(bid, bidRound);
                default:
                    return false;
            }
        }

        private static bool MinimialCheckWeldBlockBidAgainstBidRoundStart(Bid bid, Auction bidRound)
        {
            switch (bid.EntityType)
            {
                case IAIEntityType.LogisticsComputerBlock:
                    if (bid.InventoryFulfillmentFlags.HasFlag(LogisticsComputer.InventoryFulfillment.SatisfyFully)
                        || bid.InventoryFulfillmentFlags.HasFlag(LogisticsComputer.InventoryFulfillment.SatisfyPartial))
                    {
                        return true;
                    }
                    return false;
                case IAIEntityType.DroneControllerBlock:
                    if (!bid.Capabilities.HasFlag(Drone.Capabilities.CanWeld))
                    {
                        return false;
                    }
                    if (bidRound.IsInSpace && !bid.Capabilities.HasFlag(Drone.Capabilities.CanFlySpace))
                    {
                        return false;
                    }
                    if (bidRound.IsInAtmosphere && !bid.Capabilities.HasFlag(Drone.Capabilities.CanFlyAtmosphere))
                    {
                        return false;
                    }
                    if (bidRound.TotalMass > bid.MaxLoadIn1G)
                    {
                        return false;
                    }
                    if (bidRound.TotalVolume > bid.MaxCargoVolume)
                    {
                        return false;
                    }
                    return true;
                default:
                    return false;
            }
        }

        private static bool MinimialCheckGrindBlockBidAgainstBidRoundStart(Bid bid, Auction bidRound)
        {
            return false;
        }

        private static bool MinimialCheckDeliverMissingInventoryBidAgainstBidRoundStart(Bid bid, Auction bidRound)
        {
            return false;
        }

        private static bool MinimialCheckCollectInventorySurplusBidAgainstBidRoundStart(Bid bid, Auction bidRound)
        {
            return false;
        }

        private static bool MinimialCheckMineOreBidAgainstBidRoundStart(Bid bid, Auction bidRound)
        {
            return false;
        }

        private List<Bid> SelectWinningLCs(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            List<Bid> selectedWinners = new List<Bid>();
            if (potentialWinnerLCs == null || potentialWinnerLCs.Count == 0 || bidRound == null)
            {
                return selectedWinners;
            }

            Vector3D jobPosition = bidRound.PositionData.ToVector3D();
            List<Bid> ranked = RankLogisticsBids(potentialWinnerLCs, jobPosition);
            if (ranked.Count == 0)
            {
                return selectedWinners;
            }

            for (int i = 0; i < ranked.Count; i++)
            {
                Bid bid = ranked[i];
                if (IsFullLogisticsFulfillment(bid))
                {
                    selectedWinners.Add(bid);
                    return selectedWinners;
                }
            }

            // No single full-fulfillment LC exists; aggregate partial bids until requirement is satisfied.
            Inventory requiredInventory = bidRound.ComponentsInventory != null ? bidRound.ComponentsInventory : new Inventory();
            Inventory aggregatedInventory = new Inventory();
            double requiredVolume = (double)bidRound.TotalVolume;
            double accumulatedVolume = 0.0;

            for (int i = 0; i < ranked.Count; i++)
            {
                Bid bid = ranked[i];
                if (!IsPartialLogisticsFulfillment(bid))
                {
                    continue;
                }

                selectedWinners.Add(bid);
                if (bid.BidInventory != null && !bid.BidInventory.IsEmpty())
                {
                    aggregatedInventory.AddItems(bid.BidInventory);
                    accumulatedVolume += (double)bid.BidInventory.TotalInventoryVolume();
                }
                else
                {
                    accumulatedVolume += Math.Max((double)bid.MaxCargoVolume, 0.0);
                }

                bool inventorySatisfied = requiredInventory.IsEmpty() || aggregatedInventory.ContainsAtLeast(requiredInventory);
                bool volumeSatisfied = requiredVolume <= 0.0 || accumulatedVolume >= requiredVolume;
                if (inventorySatisfied || volumeSatisfied)
                {
                    break;
                }
            }

            return selectedWinners;
        }

        private List<Bid> SelectWinningDrones(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            List<Bid> rankedWinners = new List<Bid>();
            if (potentialWinnerDrones == null || potentialWinnerDrones.Count == 0 || bidRound == null)
            {
                return rankedWinners;
            }

            Vector3D jobPosition = bidRound.PositionData.ToVector3D();
            double jobVolume = (double)bidRound.TotalVolume;
            double jobMass = (double)bidRound.TotalMass;

            List<double> rankedScores = new List<double>();
            for (int i = 0; i < potentialWinnerDrones.Count; i++)
            {
                Bid drone = potentialWinnerDrones[i];
                if (drone == null || drone.IOLocationData == null)
                {
                    continue;
                }

                double score = ComputeDroneBidScore(drone, jobPosition, jobVolume, jobMass);

                int insertIndex = rankedScores.Count;
                while (insertIndex > 0 && score > rankedScores[insertIndex - 1])
                {
                    insertIndex--;
                }

                rankedScores.Insert(insertIndex, score);
                rankedWinners.Insert(insertIndex, drone);
            }

            return rankedWinners;
        }

        private double ComputeDroneBidScore(Bid drone, Vector3D jobPosition, double jobVolume, double jobMass)
        {
            Vector3D dronePosition = drone.IOLocationData.PositionData.ToVector3D();
            double distanceSquared = Vector3D.DistanceSquared(dronePosition, jobPosition);
            double distanceScore = 1.0 / (1.0 + distanceSquared);

            double maxCargoVolume = Math.Max((double)drone.MaxCargoVolume, 0.0001);
            double maxLoadIn1G = Math.Max((double)drone.MaxLoadIn1G, 0.0001);
            double optimalLoadIn1G = Math.Max((double)drone.OptimalLoadIn1G, 0.0001);
            optimalLoadIn1G = Math.Min(optimalLoadIn1G, maxLoadIn1G);

            // Approximate a preferred cargo volume envelope from the configured optimal load ratio.
            double optimalCargoVolume = maxCargoVolume * (optimalLoadIn1G / maxLoadIn1G);

            double volumeFitScore = ComputeVolumeFitScore(jobVolume, maxCargoVolume, optimalCargoVolume);
            double loadComfortScore = ComputeLoadComfortScore(jobMass, optimalLoadIn1G, maxLoadIn1G);

            const double distanceWeight = 0.45;
            const double volumeFitWeight = 0.40;
            const double loadComfortWeight = 0.15;

            return distanceWeight * distanceScore
                + volumeFitWeight * volumeFitScore
                + loadComfortWeight * loadComfortScore;
        }

        private static double ComputeVolumeFitScore(double jobVolume, double maxCargoVolume, double optimalCargoVolume)
        {
            if (maxCargoVolume <= 0.0)
            {
                return 0.0;
            }
            if (jobVolume > maxCargoVolume)
            {
                return 0.0;
            }

            double constrainedOptimalVolume = Math.Min(Math.Max(optimalCargoVolume, 0.0), maxCargoVolume);
            double capacityTarget = constrainedOptimalVolume > 0.0 ? constrainedOptimalVolume : maxCargoVolume;

            if (jobVolume <= capacityTarget)
            {
                // Prefer drones whose capacity is a close fit, rather than massively oversized cargo holds.
                return Clamp01(jobVolume / capacityTarget);
            }

            double aboveOptimalRange = Math.Max(maxCargoVolume - capacityTarget, 0.0001);
            double overOptimalFraction = (jobVolume - capacityTarget) / aboveOptimalRange;
            // Still allow these drones, but penalize heavily once the job exceeds the optimal envelope.
            return Clamp01(0.5 * (1.0 - overOptimalFraction));
        }

        private static double ComputeLoadComfortScore(double jobMass, double optimalLoadIn1G, double maxLoadIn1G)
        {
            if (maxLoadIn1G <= 0.0)
            {
                return 0.0;
            }

            if (jobMass <= optimalLoadIn1G)
            {
                return 1.0;
            }

            double loadRange = Math.Max(maxLoadIn1G - optimalLoadIn1G, 0.0001);
            double fractionPastOptimal = (jobMass - optimalLoadIn1G) / loadRange;
            return Clamp01(1.0 - fractionPastOptimal);
        }

        private List<Bid> RankLogisticsBids(List<Bid> bids, Vector3D jobPosition)
        {
            List<Bid> ranked = new List<Bid>();
            List<double> scores = new List<double>();

            for (int i = 0; i < bids.Count; i++)
            {
                Bid bid = bids[i];
                if (bid == null || bid.IOLocationData == null)
                {
                    continue;
                }

                double score = ComputeLogisticsBidScore(bid, jobPosition);
                int insertIndex = scores.Count;
                while (insertIndex > 0 && score > scores[insertIndex - 1])
                {
                    insertIndex--;
                }
                scores.Insert(insertIndex, score);
                ranked.Insert(insertIndex, bid);
            }

            return ranked;
        }

        private static double ComputeLogisticsBidScore(Bid bid, Vector3D jobPosition)
        {
            Vector3D bidPosition = bid.IOLocationData.PositionData.ToVector3D();
            double distanceSquared = Vector3D.DistanceSquared(bidPosition, jobPosition);
            double distanceScore = 1.0 / (1.0 + distanceSquared);
            double fulfillmentScore = GetLogisticsFulfillmentScore(bid.InventoryFulfillmentFlags);

            const double distanceWeight = 0.35;
            const double fulfillmentWeight = 0.65;
            return (distanceScore * distanceWeight) + (fulfillmentScore * fulfillmentWeight);
        }

        private static double GetLogisticsFulfillmentScore(LogisticsComputer.InventoryFulfillment flags)
        {
            if (flags.HasFlag(LogisticsComputer.InventoryFulfillment.SatisfyFully)
                || flags.HasFlag(LogisticsComputer.InventoryFulfillment.AcceptAll))
            {
                return 1.0;
            }
            if (flags.HasFlag(LogisticsComputer.InventoryFulfillment.SatisfyPartial)
                || flags.HasFlag(LogisticsComputer.InventoryFulfillment.AcceptPartial))
            {
                return 0.55;
            }
            return 0.0;
        }

        private static bool IsFullLogisticsFulfillment(Bid bid)
        {
            LogisticsComputer.InventoryFulfillment flags = bid.InventoryFulfillmentFlags;
            return flags.HasFlag(LogisticsComputer.InventoryFulfillment.SatisfyFully)
                || flags.HasFlag(LogisticsComputer.InventoryFulfillment.AcceptAll);
        }

        private static bool IsPartialLogisticsFulfillment(Bid bid)
        {
            LogisticsComputer.InventoryFulfillment flags = bid.InventoryFulfillmentFlags;
            return flags.HasFlag(LogisticsComputer.InventoryFulfillment.SatisfyPartial)
                || flags.HasFlag(LogisticsComputer.InventoryFulfillment.AcceptPartial);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }
            if (value > 1.0)
            {
                return 1.0;
            }
            return value;
        }


        private void HandleError()
        {
            var currentFrame = sessionDelegate.GameplayFrameCounter;

            _consecutiveErrors++;

            if (_consecutiveErrors >= _maxConsecutiveErrors)
            {
                // Critical error state - enter safe mode
                Log.Error("Orhcestrator {0} entered critical error state after {1} consecutive errors. Entering safe mode.",
                    entityId, _consecutiveErrors);

                // Disable the scheduler to prevent further issues
                isEnabled = false;

                // Clear any pending work to prevent cascading failures
                if (_pendingBidRound != null)
                {
                    taskQueue.Enqueue(_pendingBidRound.Task);
                    _pendingBidRound = null;
                }
                lock (_taskLock)
                {
                    var abandonedTaskCount = taskQueue.Count;
                    taskQueue.Clear();

                    if (abandonedTaskCount > 0)
                    {
                        Log.Warning("Orhcestrator {0} abandoned {1} queued tasks due to critical errors",
                            entityId, abandonedTaskCount);
                    }

                    // Log assigned tasks that are now orphaned
                    var totalAssignedTasks = 0;
                    foreach (var kvp in assignedTasks)
                    {
                        totalAssignedTasks += kvp.Value.Count;
                    }

                    if (totalAssignedTasks > 0)
                    {
                        Log.Warning("Orhcestrator {0} has {1} tasks still assigned to drones that may be orphaned",
                            entityId, totalAssignedTasks);
                    }
                }

                // Unsubscribe from all message topics to stop processing
                foreach (var topic in DroneManagementChannels)
                {
                    // Note: You'll need to implement Unsubscribe in MessageQueue if not already present
                    // messaging.Unsubscribe(entityId, (ushort)topic);
                }

                foreach (var topic in logisticsManagementTopics)
                {
                    // messaging.Unsubscribe(entityId, (ushort)topic);
                }

                // Stay in error state indefinitely - requires manual intervention
                return;
            }

            // Normal error handling - attempt periodic recovery
            if (currentFrame - _lastErrorRecoveryAttemptFrame < _errorRecoveryIntervalTicks)
            {
                // Not time to retry yet
                Log.Verbose("Orhcestrator {0} in error state (attempt {1}/{2}), waiting for recovery interval",
                    entityId, _consecutiveErrors, _maxConsecutiveErrors);
                return;
            }
            _lastErrorRecoveryAttemptFrame = currentFrame;
            Log.Info("Orhcestrator {0} attempting recovery from error state (attempt {1}/{2})",
                entityId, _consecutiveErrors, _maxConsecutiveErrors);

            try
            {
                // Attempt to recover by re-checking capabilities
                if (CheckCapabilities())
                {
                    // Successfully recovered
                    _consecutiveErrors = 0;
                    currentState = Orchestrator.State.Standby;

                    Log.Info("Orhcestrator {0} successfully recovered from error state", entityId);

                    // Re-initialize if necessary
                    if (!_initialized)
                    {
                        Initialize();
                    }
                }
                else
                {
                    Log.Warning("Orhcestrator {0} recovery attempt failed - capabilities check failed", entityId);

                    // Provide specific error information
                    if (ownAntenna == null)
                    {
                        Log.Error("Orhcestrator {0} error: No antenna found", entityId);
                    }
                    else if (!ownAntenna.IsFunctional)
                    {
                        Log.Error("Orhcestrator {0} error: Antenna is not functional", entityId);
                    }
                    else if (!ownAntenna.Enabled)
                    {
                        Log.Error("Orhcestrator {0} error: Antenna is not enabled", entityId);
                    }
                    else if (!ownAntenna.EnableBroadcasting)
                    {
                        Log.Error("Orhcestrator {0} error: Antenna broadcasting is disabled", entityId);
                    }
                    else if (!ownAntenna.IsWorking)
                    {
                        Log.Error("Orhcestrator {0} error: Antenna is not working (may lack power)", entityId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Orhcestrator {0} recovery attempt threw exception: {1}", entityId, ex.Message);
                // Exception during recovery counts as another error
            }
        }
        /// <summary>
        /// Get diagnostic information about current error state
        /// </summary>
        public string GetErrorDiagnostics()
        {
            var diagnostics = new System.Text.StringBuilder();

            diagnostics.AppendLine($"=== Scheduler {entityId} Error Diagnostics ===");
            diagnostics.AppendLine($"State: {currentState}");
            diagnostics.AppendLine($"Consecutive Errors: {_consecutiveErrors}/{_maxConsecutiveErrors}");
            diagnostics.AppendLine($"Enabled: {isEnabled}");
            diagnostics.AppendLine($"Initialized: {_initialized}");
            diagnostics.AppendLine();

            diagnostics.AppendLine("Antenna Status:");
            if (ownAntenna == null)
            {
                diagnostics.AppendLine("  - No antenna found");
            }
            else
            {
                diagnostics.AppendLine($"  - Functional: {ownAntenna.IsFunctional}");
                diagnostics.AppendLine($"  - Enabled: {ownAntenna.Enabled}");
                diagnostics.AppendLine($"  - Broadcasting: {ownAntenna.EnableBroadcasting}");
                diagnostics.AppendLine($"  - Working: {ownAntenna.IsWorking}");
                diagnostics.AppendLine($"  - Range: {ownAntenna.Radius:F1}m");
            }
            diagnostics.AppendLine();

            diagnostics.AppendLine("Task Status:");
            diagnostics.AppendLine($"  - Queued: {taskQueue.Count}");
            diagnostics.AppendLine($"  - Assigned Drones: {assignedTasks.Count}");

            var totalAssigned = 0;
            foreach (var kvp in assignedTasks)
            {
                totalAssigned += kvp.Value.Count;
            }
            diagnostics.AppendLine($"  - Total Assigned Tasks: {totalAssigned}");
            diagnostics.AppendLine($"  - Delegated Awaiting Ack: {delegatedTasksNeedingAck.Count}");
            diagnostics.AppendLine();

            diagnostics.AppendLine("Drone Status:");
            diagnostics.AppendLine($"  - Registered: {registeredDrones.Count}");

            var now = DateTime.UtcNow;
            var outOfRange = 0;
            var stale = 0;

            foreach (var drone in registeredDrones.Values)
            {
                if (drone.IsOutOfRange) outOfRange++;
                if ((now - drone.LastSeenTime).TotalSeconds > 60) stale++;
            }

            diagnostics.AppendLine($"  - Out of Range: {outOfRange}");
            diagnostics.AppendLine($"  - Stale (>60s): {stale}");

            return diagnostics.ToString();
        }

        public ConstructionComputer.WorkModes GetConstructionComputerWorkModes(Orchestrator.WorkModes workModes)
        {
            var workModeEval = workModes & (
                WorkModes.ScanOnly |
                WorkModes.Grind |
                WorkModes.WeldUnfinishedBlocks);
            return (ConstructionComputer.WorkModes)workModeEval;
        }

        public void Echo(string message, params object[] args)
        {
            terminalDisplayManager.Echo(message, args);
        }
    }
}