using ImprovedAI.Config;
using ImprovedAI.VirtualNetwork;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Collections;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using static ImprovedAI.Scheduler;

namespace ImprovedAI
{
    public class IAIScheduler
    {
        private sealed class PendingBidRoundState
        {
            public Task Task;
            public long DeadlineFrame;
            public readonly List<CollectedBid> Bids = new List<CollectedBid>();
        }

        private MyConcurrentDictionary<long, Drone> registeredDrones = new MyConcurrentDictionary<long, Drone>();
        private MyConcurrentDictionary<long, LogisticsComputer> registeredLogisticsComputers = new MyConcurrentDictionary<long, LogisticsComputer>();
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


        private static Dictionary<long, AntennaInfo> antennaCache = new Dictionary<long, AntennaInfo>();
        public int PerScanLimits { get; set; }
        private int _perScanLimits;
        public int MaxTasksAssignedPerBatch { get; set; }
        private int _maxTasksAssignedPerBatch;

        // Management
        private long entityId;
        private int _lastUpdateFrame = 0;
        private int _lastScanFrameAttempt = 0;
        private int _consecutiveErrors = 0;
        private long _lastErrorRecoveryAttemptFrame = 0;
        private long _lastMaintenanceFrame = 0;
        private long _lastAntennaCacheUpdateFrame = 0;

        // Server settings
        private readonly int _initializingUpdateIntervalTicks = ServerConfig.Instance.SchedulerBounds.StateUpdateIntervalTicks.Initializing;
        private readonly int _errorUpdateIntervalTicks = ServerConfig.Instance.SchedulerBounds.StateUpdateIntervalTicks.Error;
        private readonly int _standbyUpdateIntervalTicks = ServerConfig.Instance.SchedulerBounds.StateUpdateIntervalTicks.Standby;
        private readonly int _scanningUpdateIntervalTicks = ServerConfig.Instance.SchedulerBounds.StateUpdateIntervalTicks.Scanning;
        private readonly int _assigningUpdateIntervalTicks = ServerConfig.Instance.SchedulerBounds.StateUpdateIntervalTicks.Assigning;
        private readonly int _scanRetryIntervalTicks = ServerConfig.Instance.SchedulerBounds.ScanDelayTicks;
        private readonly int _errorRecoveryIntervalTicks = ServerConfig.Instance.SchedulerBounds.ErrorRecoveryIntervalTicks;
        private readonly int _maxConsecutiveErrors = ServerConfig.Instance.SchedulerBounds.MaxConsecutiveErrors;
        private readonly int _maintenanceIntervalTicks = ServerConfig.Instance.SchedulerBounds.ManintenanceIntervalTicks;
        private readonly int _antennaCacheUpdateIntervalTicks = ServerConfig.Instance.MessageQueue.SchedulerAntennaCacheUpdateIntervalTicks();
        private readonly int _bidCollectionWindowTicks = ServerConfig.Instance.SchedulerBounds.BidCollectionWindowTicks;
        private readonly int _bidBlacklistCooldownTicks = ServerConfig.Instance.SchedulerBounds.BidBlacklistCooldownTicks;
        /// <summary>
        /// Timeout after which drones are removed if we don't hear from them again.
        /// </summary>
        //private static readonly int droneTimeoutSeconds = 1800;
        //private static DateTime lastCacheUpdate;


        private MessageQueue messaging;
        private int messageReadLimit = IAISession.GetConfig().MessageQueue.SchedulerMessageReadLimit();
        private IMyEntity Entity;
        private IMyRadioAntenna ownAntenna;
        private List<Message<IMessagePayload>> _messageCache = new List<Message<IMessagePayload>>();
        private readonly List<Message<TaskAborted>> _taskAbortCache = new List<Message<TaskAborted>>();
        private readonly List<Message<DroneReport>> _droneRegistrationCache = new List<Message<DroneReport>>();
        private readonly List<Message<DroneReport>> _droneReportCache = new List<Message<DroneReport>>();
        private readonly List<Message<LogisticsUpdate>> _logisticsRegistrationCache = new List<Message<LogisticsUpdate>>();
        private readonly List<Message<LogisticsUpdate>> _logisticsUpdateCache = new List<Message<LogisticsUpdate>>();
        private readonly List<Message<TaskBid>> _taskBidCache = new List<Message<TaskBid>>();
        private readonly List<Message<TaskFulfillmentLost>> _taskFulfillmentLostCache = new List<Message<TaskFulfillmentLost>>();
        private readonly object _taskLock = new object();
        private int _taskIdCounter = 0;
        private PendingBidRoundState _pendingBidRound;
        private readonly Dictionary<long, Dictionary<uint, long>> _bidBlacklistUntilFrame = new Dictionary<long, Dictionary<uint, long>>();
        private readonly object _bidBlacklistLock = new object();

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

        //private Dictionary<TaskType, List<Task>> pendingTasks;

        private struct AntennaInfo
        {
            public Vector3D Position;
            public double Range;
            public bool IsWorking;
            public long GridId;
            public DateTime LastUpdate;
        }

        public IAIScheduler(
            IMyEntity entity,
            OperationMode operationMode = OperationMode.Orchestrator,
            WorkModes workModes = WorkModes.None)
        {
            this.entityId = entity.EntityId;
            this.Entity = entity;
            this.operationMode = operationMode;
            this.workModes = workModes;

            _maxTasksAssignedPerBatch = MaxTasksAssignedPerBatch > ServerConfig.Instance.SchedulerBounds.MaxTaskAssignmentPerBatch
                ? ServerConfig.Instance.SchedulerBounds.MaxTaskAssignmentPerBatch
                : MaxTasksAssignedPerBatch;

            _perScanLimits = PerScanLimits > ServerConfig.Instance.SchedulerBounds.PerScanLimit ? ServerConfig.Instance.SchedulerBounds.PerScanLimit : PerScanLimits;
        }


        public void UpdateBeforeSimulation10()
        {
            if (!AntennaOK() && currentState != State.Error && _initialized)
            {
                Log.Warning("Scheduler {0} antenna check failed, transitioning to error state", entityId);
                currentState = State.Error;
            }
            if (!_initialized && currentState != State.Initializing)
            {
                Initialize();
                return;
            }
            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
            var updateInterval = GetUpdateIntervalTicks();

            if (currentFrame - _lastUpdateFrame < updateInterval)
                return;

            _lastUpdateFrame = currentFrame;

            try
            {
                UpdateAI();

                if (currentState != State.Error && currentState != State.Initializing)
                {
                    ReadTaskAbortedMessages();
                    ReadTaskFulfillmentLostMessages();
                    ReadDroneRegistrations();
                    ReadDroneReports((ushort)messageReadLimit);
                    ReadLogisticsRegistrationAndUpdates();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scheduler {0} update threw exception: {1}", entityId, ex.Message);
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
                case State.ScanningForTasks:
                    return _scanningUpdateIntervalTicks;
                case State.AssigningTasks:
                    return _assigningUpdateIntervalTicks;
                default:
                    return 600; // Default 10 second updates
            }
        }

        private void Initialize()
        {
            messaging = IAISession.GetMessageQueue();
            currentState = State.Initializing;
            messaging.Subscribe(entityId, Channel.DRONE_REGISTRATION);
            messaging.Subscribe(entityId, Channel.DRONE_REPORTS);
            messaging.Subscribe(entityId, Channel.DRONE_PERFORMANCE);
            messaging.Subscribe(entityId, Channel.LOGISTIC_REGISTRATION);
            messaging.Subscribe(entityId, Channel.LOGISTIC_UPDATE);
            messaging.Subscribe(entityId, Channel.LOGISTIC_REQUEST);
            messaging.Subscribe(entityId, Channel.LOGISTIC_PUSH);
            messaging.Subscribe(entityId, Channel.SCHEDULER_FORWARD);
            messaging.Subscribe(entityId, Channel.DIRECT_MESSAGE);
        }

        private void UpdateAI()
        {
            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;

            // Perform maintenance periodically, regardless of state
            if (currentFrame - _lastMaintenanceFrame >= _maintenanceIntervalTicks)
            {
                _lastMaintenanceFrame = currentFrame;
                PerformDroneMaintenance();
            }
            switch (currentState)
            {
                case State.Initializing:
                    HandleInitializing();
                    break;
                case State.Standby:
                    HandleStandby();
                    break;
                case State.ScanningForTasks:
                    HandleScanningForTasks();
                    break;
                case State.AssigningTasks:
                    HandleTaskAssignment();
                    break;
                case State.Error:
                    HandleError();
                    break;
            }
        }
        private void HandleInitializing()
        {
            if (CheckCapabilities())
            {
                if (AntennaOK())
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
            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
            // Periodically check if we should scan
            if (currentFrame - _lastScanFrameAttempt >= _scanRetryIntervalTicks)
            {
                _lastScanFrameAttempt = currentFrame;
                if (ShouldStartScanning())
                {
                    Log.Verbose("Scheduler {0} waking from standby to scan for tasks", entityId);
                    currentState = State.ScanningForTasks;
                }
            }
        }

        private bool ShouldStartScanning()
        {
            var workModeEval = workModes & (
                WorkModes.Scan |
                WorkModes.Grind |
                WorkModes.FetchCargo |
                WorkModes.DeliverCargo |
                WorkModes.WeldUnfinishedBlocks);
            return isEnabled && AntennaOK() && workModeEval > 0;
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

        private bool AntennaOK()
        {
            return ownAntenna != null &&
                ownAntenna.IsFunctional &&
                ownAntenna.Enabled &&
                ownAntenna.EnableBroadcasting &&
                ownAntenna.IsWorking;
        }



        private void HandleScanningForTasks()
        {
            var totalTasks = 0;
            try
            {
                totalTasks += ScanForWeldRepairGrindTasks();
                totalTasks += ScanForLogisticTasks();
            }
            catch (Exception ex)
            {
                Log.Error("Scheduler {0} Error during task scanning: {1}", entityId, ex.Message);
                currentState = State.Error;
                return;
            }

            if (totalTasks == 0)
            {
                Log.Verbose("Scheduler {0] going on standby, no tasks found.", entityId);
                currentState = State.Standby;
                return;
            }
            Log.Verbose("Scheduler {0} found {1} tasks, transitioning to AssigningTasks", entityId, totalTasks);
            currentState = State.AssigningTasks;
        }

        private void HandleTaskAssignment()
        {
            long currentFrame = MyAPIGateway.Session.GameplayFrameCounter;

            if (operationMode.HasFlag(OperationMode.Orchestrator) &&
                (registeredDrones.Count > 0 || registeredLogisticsComputers.Count > 0))
            {
                ReadTaskBids(currentFrame);
                TryFinalizePendingBidRound(currentFrame);
                TryStartNextBidRound(currentFrame);
                MaybeTransitionAssigningToStandby();
            }

            if (registeredDrones.Count == 0 && operationMode.HasFlag(OperationMode.DelegateIfNoDrones))
            {
                for (int i = 0; i < _maxTasksAssignedPerBatch; i++)
                {
                    Task task;
                    if (taskQueue.TryDequeue(out task))
                    {
                        var taskAssignment = new TaskAssignment
                        {
                            Tasks = new List<Task> { task }
                        };
                        uint msgId = IdGenerator.GenerateId(ref _taskIdCounter, entityId);
                        ErrorCode errorCode = messaging.SendMessage((ushort)Channel.SCHEDULER_FORWARD, taskAssignment, entityId, true);
                        if (errorCode == ErrorCode.None)
                            delegatedTasksNeedingAck.Add((ushort)(msgId & 0xFFFF), task.TaskId);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            if (registeredDrones.Count == 0 && !operationMode.HasFlag(OperationMode.DelegateIfNoDrones))
                Log.Info("Scheduler {0}: No available drones", entityId);
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

        private void ReadTaskBids(long currentFrame)
        {
            if (_pendingBidRound == null)
                return;

            messaging.ReadMessages(
                entityId,
                ownAntenna,
                Channel.DIRECT_MESSAGE,
                _taskBidCache,
                messageReadLimit,
                clear: true,
                messageFilters: PayloadType.TaskBid);

            Task pendingTask = _pendingBidRound.Task;
            for (int i = 0; i < _taskBidCache.Count; i++)
            {
                Message<TaskBid> message = _taskBidCache[i];
                if (message.RecipientId != entityId)
                    continue;
                TaskBid payload = message.Payload;
                if (payload == null || payload.TaskId != pendingTask.TaskId)
                    continue;

                long senderId = message.SenderId;
                if (!registeredDrones.ContainsKey(senderId) && !registeredLogisticsComputers.ContainsKey(senderId))
                    continue;
                if (IsBidBlacklisted(senderId, payload.TaskId, currentFrame))
                    continue;

                bool senderIsDrone = registeredDrones.ContainsKey(senderId);
                bool senderIsLc = registeredLogisticsComputers.ContainsKey(senderId);
                if (payload.BidderKind == TaskBidderKind.Drone && !senderIsDrone)
                    continue;
                if (payload.BidderKind == TaskBidderKind.LogisticsComputer && !senderIsLc)
                    continue;

                List<CollectedBid> bids = _pendingBidRound.Bids;
                int existing = -1;
                for (int b = 0; b < bids.Count; b++)
                {
                    if (bids[b].SenderId == senderId)
                    {
                        existing = b;
                        break;
                    }
                }
                CollectedBid cb = new CollectedBid { SenderId = senderId, Bid = payload };
                if (existing >= 0)
                    bids[existing] = cb;
                else
                    bids.Add(cb);
            }
        }

        private void TryFinalizePendingBidRound(long currentFrame)
        {
            if (_pendingBidRound == null)
                return;
            if (currentFrame < _pendingBidRound.DeadlineFrame)
                return;

            PendingBidRoundState round = _pendingBidRound;
            _pendingBidRound = null;

            Task task = round.Task;
            List<CollectedBid> bids = round.Bids;
            int winIdx = TaskBidSelection.PickWinnerIndex(bids, task.TaskType);
            if (winIdx < 0)
            {
                taskQueue.Enqueue(task);
                Log.Info("AI scheduler {0}: no bids for task {1}, re-queued", entityId, task.TaskId);
                return;
            }

            CollectedBid winner = bids[winIdx];
            AwardTaskToWinner(winner.SenderId, task, winner.Bid.BidderKind);
            Log.Info("AI scheduler {0}: awarded task {1} to bidder {2}", entityId, task.TaskId, winner.SenderId);
        }

        private void TryStartNextBidRound(long currentFrame)
        {
            if (_pendingBidRound != null)
                return;
            if (taskQueue.Count == 0)
                return;

            Task task;
            if (!taskQueue.TryDequeue(out task))
                return;

            Inventory reqPayload = task.Payload;
            TaskAnnouncement announcement = new TaskAnnouncement
            {
                TaskId = task.TaskId,
                Type = task.TaskType,
                Destination = task.Position,
                RequiredCapabilities = SchedulerTaskCapabilities.RequiredCapabilitiesFor(task.TaskType),
                SchedulerEntityId = entityId,
                RequiredPayload = reqPayload,
            };

            Message<TaskAnnouncement> msg = new Message<TaskAnnouncement>
            {
                Payload = announcement,
                MessageId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = GetSchedulerOwnerId(),
                Channel = Channel.DRONE_TASK_ANNOUNCEMENT,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Drone | MessageQueue.IAIBlockType.LogisticsComputer,
            };

            ErrorCode broadcastResult = messaging.BroadcastMessage(ownAntenna, msg, false);
            if (broadcastResult != ErrorCode.None)
            {
                taskQueue.Enqueue(task);
                Log.Warning(
                    "AI scheduler {0}: TaskAnnouncement broadcast failed ({1}) for task {2}",
                    entityId, broadcastResult, task.TaskId);
                return;
            }

            _pendingBidRound = new PendingBidRoundState
            {
                Task = task,
                DeadlineFrame = currentFrame + _bidCollectionWindowTicks,
            };
            Log.Verbose("AI scheduler {0}: bid round started for task {1}", entityId, task.TaskId);
        }

        private void AwardTaskToWinner(long winnerId, Task task, TaskBidderKind bidderKind)
        {
            task.AssignedBy = entityId;
            task.AssignedTime = DateTime.UtcNow;

            TaskAssignment assignment = new TaskAssignment
            {
                Tasks = new List<Task> { task },
            };

            MessageQueue.IAIBlockType blockType = MessageQueue.IAIBlockType.Drone;
            if (bidderKind == TaskBidderKind.LogisticsComputer)
                blockType = MessageQueue.IAIBlockType.LogisticsComputer;

            Message<TaskAssignment> msg = new Message<TaskAssignment>
            {
                Payload = assignment,
                MessageId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                RecipientId = winnerId,
                SenderId = entityId,
                SenderOwnerId = GetSchedulerOwnerId(),
                RequiresAck = false,
                RecipientBlockType = blockType,
                Channel = Channel.DIRECT_MESSAGE,
            };

            ErrorCode sendResult = messaging.SendDirectMessage(ownAntenna, msg, false);
            if (sendResult != ErrorCode.None)
            {
                taskQueue.Enqueue(task);
                Log.Warning(
                    "AI scheduler {0}: TaskAssignment to {1} failed ({2}) for task {3}",
                    entityId, winnerId, sendResult, task.TaskId);
                return;
            }

            lock (_taskLock)
            {
                List<Task> taskList;
                if (!assignedTasks.TryGetValue(winnerId, out taskList))
                {
                    taskList = new List<Task>();
                    assignedTasks[winnerId] = taskList;
                }
                taskList.Add(task);
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
            Log.Verbose("Scheduler {0}: task queue drained, returning to Standby", entityId);
            currentState = State.Standby;
        }

        private void ReadTaskFulfillmentLostMessages()
        {
            messaging.ReadMessages(
                entityId,
                ownAntenna,
                Channel.DIRECT_MESSAGE,
                _taskFulfillmentLostCache,
                messageReadLimit,
                clear: true,
                messageFilters: PayloadType.TaskFulfillmentLost);

            for (int i = 0; i < _taskFulfillmentLostCache.Count; i++)
            {
                Message<TaskFulfillmentLost> message = _taskFulfillmentLostCache[i];
                if (message.RecipientId != entityId)
                    continue;
                TaskFulfillmentLost payload = message.Payload;
                if (payload == null)
                    continue;
                HandleTaskFulfillmentLost(message.SenderId, payload.TaskId, payload.Reason);
            }
        }

        private void HandleTaskFulfillmentLost(long logisticsComputerId, uint taskId, string reason)
        {
            long currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
            lock (_taskLock)
            {
                List<Task> taskList;
                if (!assignedTasks.TryGetValue(logisticsComputerId, out taskList))
                {
                    Log.Warning(
                        "AI scheduler {0}: TaskFulfillmentLost for task {1} from unassigned logistics computer {2}",
                        entityId, taskId, logisticsComputerId);
                    return;
                }

                int taskIndex = taskList.FindIndex(t => t.TaskId == taskId);
                if (taskIndex < 0)
                {
                    Log.Warning(
                        "AI scheduler {0}: TaskFulfillmentLost task {1} not found for logistics computer {2}",
                        entityId, taskId, logisticsComputerId);
                    return;
                }

                Task lostTask = taskList[taskIndex];
                taskList.RemoveAt(taskIndex);
                taskQueue.Enqueue(lostTask);

                if (taskList.Count == 0)
                {
                    List<Task> removed;
                    assignedTasks.TryRemove(logisticsComputerId, out removed);
                }

                Log.Info(
                    "AI scheduler {0}: Logistics computer {1} lost fulfillment for task {2} ({3}); task re-queued",
                    entityId, logisticsComputerId, taskId, reason ?? string.Empty);
            }
            AddBidBlacklist(logisticsComputerId, taskId, currentFrame);
        }
        private void UpdateAntennaCache(IMyRadioAntenna schedulerAntenna)
        {
            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
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

        private bool ShouldWeldOrRepair(IMySlimBlock block)
        {
            if (workModes.HasFlag(WorkModes.WeldUnfinishedBlocks) &&
                    block.BuildLevelRatio < 1.0f &&
                    !ColorUtil.ColorMatch(block, weldIgnoreColor))
                return true;
            if (workModes.HasFlag(WorkModes.RepairDamagedBlocks) &&
                    block.CurrentDamage > 0.0f &&
                    !ColorUtil.ColorMatch(block, weldIgnoreColor))
                return true;
            return false;
        }
        private bool ShouldGrind(IMySlimBlock block)
        {
            if (workModes.HasFlag(WorkModes.Grind) &&
                ColorUtil.ColorMatch(block, grindColor))
                return true;

            return false;
        }

        private int ScanForWeldRepairGrindTasks()
        {
            var scannedBlocks = 0;
            var tasksCreated = 0;

            var cubeBlock = Entity as IMyCubeBlock;
            if (cubeBlock?.CubeGrid == null)
            {
                Log.Error("cannot scan: entity {0} is not an IMyCubeBlock", entityId);
                return tasksCreated;
            }

            var antennaPosition = ownAntenna.CubeGrid.GridIntegerToWorld(ownAntenna.Position);
            var antennaRadius = ownAntenna.Radius;

            List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
            try
            {
                MyAPIGateway.GridGroups.GetGroup(cubeBlock.CubeGrid, GridLinkTypeEnum.Physical, connectedGrids);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to get connected grids: {0}", ex.Message);
                return tasksCreated;
            }

            foreach (var grid in connectedGrids)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var block in blocks)
                {
                    if (++scannedBlocks > _perScanLimits)
                    {
                        Log.Verbose("Scan limit reached ({0} blocks), continuing next update", _perScanLimits);
                        // Continue scanning next update
                        return tasksCreated;
                    }
                    var shouldWeld = ShouldWeldOrRepair(block);
                    var shouldGrind = ShouldGrind(block);
                    if (!shouldWeld && !shouldGrind)
                        continue;

                    var blockPosition = grid.GridIntegerToWorld(block.Position);
                    var distance = Vector3D.Distance(antennaPosition, blockPosition);
                    var isOutOfAntennaRange = distance > antennaRadius;
                    var isOutOfSpecificRange = distance > ignoreTasksOutsideSpecifiedRangeMeters;
                    if (isOutOfAntennaRange && ignoreTasksOutsideOfAntenaRange) continue;
                    if (isOutOfSpecificRange && ignoreTasksOutsideSpecifiedRange) continue;

                    if (shouldWeld)
                    {
                        var componentsDict = new Dictionary<string, int>();
                        block.GetMissingComponents(componentsDict);
                        taskQueue.Enqueue(new Task
                        {
                            TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                            TaskType = TaskType.PreciseWelding,
                            Payload = new Inventory(componentsDict),
                            Position = blockPosition,
                            OutOfSchedulerRange = isOutOfAntennaRange
                        });
                        tasksCreated++;
                    }
                    if (shouldGrind)
                    {
                        taskQueue.Enqueue(new Task
                        {
                            TaskId = IdGenerator.GenerateId(ref _taskIdCounter, entityId),
                            TaskType = TaskType.PreciseGrinding,
                            Position = blockPosition,
                            OutOfSchedulerRange = isOutOfAntennaRange
                        });
                        tasksCreated++;
                    }
                }
            }
            if (tasksCreated > 0)
                Log.Info("Scan complete: {0} blocks scanned, {1} tasks created", scannedBlocks, tasksCreated);
            return tasksCreated;
        }

        private void ReadDroneRegistrations()
        {
            messaging.ReadMessages(entityId, ownAntenna, Channel.DIRECT_MESSAGE, _messageCache, 50, true, PayloadType.DroneReport);
            messaging.ReadMessages(entityId, ownAntenna, Channel.DRONE_REGISTRATION, _droneRegistrationCache, 50, true, PayloadType.DroneReport);
            Drone drone;
            foreach (var message in _droneRegistrationCache)
            {
                var reg = message.Payload;
                long droneId = reg.DroneEntityId != 0 ? reg.DroneEntityId : message.SenderId;
                // if exists, redirect the message to drone report queue
                if (registeredDrones.TryGetValue(droneId, out drone))
                {
                    messaging.SendMessage<DroneReport>((ushort)Channel.DRONE_REPORTS, reg, this.entityId, false);
                    continue;
                }
                drone = new Drone(
                    droneId,
                    reg.Capabilities.GetValueOrDefault(Drone.Capabilities.None),
                    reg.DroneState.GetValueOrDefault(Drone.State.Standby),
                    reg.BatteryChargePercent.GetValueOrDefault(100.0f),
                    reg.BatteryRechargeThreshold.GetValueOrDefault(25.0f),
                    reg.BatteryOperationalThreshold.GetValueOrDefault(80.0f),
                    reg.H2Level.GetValueOrDefault(100.0f),
                    reg.H2RefuelThreshold.GetValueOrDefault(25.0f),
                    reg.H2OperationalThreshold.GetValueOrDefault(80.0f)
                );
                registeredDrones.Add(droneId, drone);
            }
        }
        private void ReadDroneReports(ushort maxMessages)
        {
            messaging.ReadMessages(entityId, ownAntenna, Channel.DRONE_REPORTS, _droneReportCache, maxMessages, true, PayloadType.DroneReport);
            foreach (var message in _droneReportCache)
            {
                var report = message.Payload;
                long droneId = report.DroneEntityId != 0 ? report.DroneEntityId : message.SenderId;
                Drone drone;
                if (registeredDrones.TryGetValue(droneId, out drone))
                {
                    if (report.Flags.HasFlag(Drone.UpdateFlags.StateChanged))
                        drone._State = report.DroneState.GetValueOrDefault(drone._State);
                    if (report.Flags.HasFlag(Drone.UpdateFlags.CapabilitiesChanged))
                        drone._Capabilities = report.Capabilities.GetValueOrDefault(drone._Capabilities);
                    if (report.Flags.HasFlag(Drone.UpdateFlags.BatteryUpdate))
                    {
                        drone.BatteryLevel = report.BatteryChargePercent.GetValueOrDefault(drone.BatteryLevel);
                        drone.BatteryRechargeThreshold = report.BatteryRechargeThreshold.GetValueOrDefault(drone.BatteryRechargeThreshold);
                        drone.BatteryOperationalThreshold = report.BatteryOperationalThreshold.GetValueOrDefault(drone.BatteryOperationalThreshold);
                    }
                    if (report.Flags.HasFlag(Drone.UpdateFlags.H2Update))
                    {
                        drone.H2Level = report.H2Level.GetValueOrDefault(drone.H2Level);
                        drone.H2RefuelThreshold = report.H2RefuelThreshold.GetValueOrDefault(drone.H2RefuelThreshold);
                        drone.H2OperationalThreshold = report.H2OperationalThreshold.GetValueOrDefault(drone.H2OperationalThreshold);
                    }
                    if (report.Flags.HasFlag(Drone.UpdateFlags.TaskComplete))
                        HandleTaskCompletion(droneId, report.TaskId.Value);
                    if (report.Flags.HasFlag(Drone.UpdateFlags.GoingOutOfRange))
                    {
                        drone.IsOutOfRange = true;
                        drone.LastSeenTime = DateTime.UtcNow;
                        Log.LogDroneNetwork(LogLevel.Info, "AI scheduler {0}: Drone {1} going out of range", entityId, droneId);
                    }
                    if (report.Flags.HasFlag(Drone.UpdateFlags.ReturningIntoRange))
                    {
                        drone.IsOutOfRange = false;
                        drone.LastSeenTime = DateTime.UtcNow;
                        Log.LogDroneNetwork(LogLevel.Info, "AI scheduler {0}: Drone {1} returning into range", entityId, droneId);
                    }
                }
                else
                {
                    // if not registered, redirect to registration queue
                    messaging.SendMessage<DroneReport>((ushort)Channel.DRONE_REGISTRATION, report, this.entityId, false);
                }
            }
        }

        /// <summary>
        /// Messaging-only: merge LC registration/update snapshots into <see cref="registeredLogisticsComputers"/>.
        /// </summary>
        private void ReadLogisticsRegistrationAndUpdates()
        {
            messaging.ReadMessages(
                entityId,
                ownAntenna,
                Channel.LOGISTIC_REGISTRATION,
                _logisticsRegistrationCache,
                messageReadLimit,
                clear: true,
                messageFilters: PayloadType.LogisticsUpdate);
            for (int i = 0; i < _logisticsRegistrationCache.Count; i++)
            {
                Message<LogisticsUpdate> message = _logisticsRegistrationCache[i];
                LogisticsComputer merged = SchedulerLogisticsUpdateMerge.ToLogisticsComputer(message.Payload, message.SenderId);
                registeredLogisticsComputers[merged.EntityId] = merged;
            }

            messaging.ReadMessages(
                entityId,
                ownAntenna,
                Channel.LOGISTIC_UPDATE,
                _logisticsUpdateCache,
                messageReadLimit,
                clear: true,
                messageFilters: PayloadType.LogisticsUpdate);
            for (int i = 0; i < _logisticsUpdateCache.Count; i++)
            {
                Message<LogisticsUpdate> message = _logisticsUpdateCache[i];
                LogisticsComputer merged = SchedulerLogisticsUpdateMerge.ToLogisticsComputer(message.Payload, message.SenderId);
                registeredLogisticsComputers[merged.EntityId] = merged;
            }
        }

        private void ReadTaskAbortedMessages()
        {
            messaging.ReadMessages(
                entityId,
                ownAntenna,
                Channel.DIRECT_MESSAGE,
                _taskAbortCache,
                messageReadLimit,
                clear: true,
                messageFilters: PayloadType.TaskAborted);

            foreach (var message in _taskAbortCache)
            {
                if (message.RecipientId != entityId)
                    continue;
                TaskAborted payload = message.Payload;
                if (payload == null)
                    continue;
                HandleTaskAborted(message.SenderId, payload.TaskId, payload.Reason);
            }
        }

        /// <summary>
        /// Drone or logistics computer gave up on an assigned task. Release assignment and re-queue; blacklist bidder for this task.
        /// </summary>
        private void HandleTaskAborted(long bidderId, ushort taskId, string reason)
        {
            uint taskIdU = taskId;
            long currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
            lock (_taskLock)
            {
                List<Task> taskList;
                if (!assignedTasks.TryGetValue(bidderId, out taskList))
                {
                    Log.Warning("AI scheduler {0}: TaskAborted for task {1} from unassigned bidder {2}", entityId, taskId, bidderId);
                    return;
                }

                int taskIndex = taskList.FindIndex(t => t.TaskId == taskIdU);
                if (taskIndex < 0)
                {
                    Log.Warning("AI scheduler {0}: TaskAborted task {1} not found for bidder {2}", entityId, taskId, bidderId);
                    return;
                }

                Task abortedTask = taskList[taskIndex];
                taskList.RemoveAt(taskIndex);
                taskQueue.Enqueue(abortedTask);

                if (taskList.Count == 0)
                {
                    List<Task> removed;
                    assignedTasks.TryRemove(bidderId, out removed);
                }

                Log.Info(
                    "AI scheduler {0}: Bidder {1} aborted task {2} ({3}); task re-queued for reassignment",
                    entityId, bidderId, taskId, reason ?? string.Empty);
            }
            AddBidBlacklist(bidderId, taskIdU, currentFrame);
        }

        private void HandleTaskCompletion(long droneId, ushort taskId)
        {
            lock (_taskLock)
            {
                List<Task> taskList;
                if (assignedTasks.TryGetValue(droneId, out taskList))
                {
                    var taskIndex = taskList.FindIndex(t => t.TaskId == taskId);
                    if (taskIndex >= 0)
                    {
                        var completedTask = taskList[taskIndex];
                        Log.Info("AI scheduler {0} task {1} completed by drone {2}, removing from assigned tasks",
                            entityId, completedTask.TaskId, droneId);
                        taskList.RemoveAt(taskIndex);

                        if (taskList.Count == 0)
                        {
                            List<Task> removedList;
                            assignedTasks.TryRemove(droneId, out removedList);
                            Log.Info("AI scheduler {0}: All tasks completed for drone {1}, removing from assigned tasks",
                                entityId, droneId);
                        }
                    }
                    else
                    {
                        Log.Warning("Task {0} not found for drone {1}", taskId, droneId);
                    }
                }
                else
                {
                    Log.Warning("received report of a task assigned to a non-existing drone. Scheduler: {0}, droneId {1}",
                        entityId, droneId);
                }
            }
        }
        private void PerformDroneMaintenance()
        {
            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromMinutes(30); // Configurable timeout
            var dronesTimedOut = new List<long>();

            foreach (var kvp in registeredDrones)
            {
                var drone = kvp.Value;

                // Check if drone hasn't been seen in too long
                if (now - drone.LastSeenTime > timeout)
                {
                    dronesTimedOut.Add(kvp.Key);
                }
            }

            // Clean up timed-out drones
            foreach (var droneId in dronesTimedOut)
            {
                Drone removedDrone;
                if (registeredDrones.TryRemove(droneId, out removedDrone))
                {
                    Log.Warning("AI scheduler {0}: Drone {1} timed out (last seen: {2}), removing from registry",
                        entityId, droneId, removedDrone.LastSeenTime);

                    lock (_taskLock)
                    {
                        List<Task> removedTasks;
                        if (assignedTasks.TryRemove(droneId, out removedTasks))
                        {
                            if (removedTasks.Count > 0)
                            {
                                Log.Warning("AI scheduler {0}: Returning {1} abandoned tasks to queue from timed-out drone {2}",
                                    entityId, removedTasks.Count, droneId);

                                // Return tasks to unassigned queue
                                foreach (var task in removedTasks)
                                {
                                    taskQueue.Enqueue(task);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ReadForwardedSchedulerMessages()
        {
            // Placeholder: forwarded scheduler messages processed as drone reports
            messaging.ReadMessages(entityId, ownAntenna, Channel.SCHEDULER_FORWARD, _droneReportCache, messageReadLimit, true, PayloadType.None);
        }

        private int ScanForLogisticTasks()
        {
            // check logistic requests
            // see which providers can satisfy
            // try single provider
            // else multiple providers
            return 0;

        }

        /// <summary>
        /// Reset scheduler to initial state - useful for manual recovery
        /// </summary>
        public void ResetScheduler()
        {
            Log.Info("Scheduler {0} manual reset requested", entityId);

            lock (_taskLock)
            {
                taskQueue.Clear();
                assignedTasks.Clear();
                delegatedTasksNeedingAck.Clear();
            }

            _pendingBidRound = null;
            lock (_bidBlacklistLock)
            {
                _bidBlacklistUntilFrame.Clear();
            }

            registeredDrones.Clear();
            registeredLogisticsComputers.Clear();

            _consecutiveErrors = 0;
            _initialized = false;
            isEnabled = false;

            currentState = State.Initializing;

            Log.Info("Scheduler {0} reset complete", entityId);
        }
        private void HandleError()
        {
            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;

            _consecutiveErrors++;

            if (_consecutiveErrors >= _maxConsecutiveErrors)
            {
                // Critical error state - enter safe mode
                Log.Error("Scheduler {0} entered critical error state after {1} consecutive errors. Entering safe mode.",
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
                        Log.Warning("Scheduler {0} abandoned {1} queued tasks due to critical errors",
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
                        Log.Warning("Scheduler {0} has {1} tasks still assigned to drones that may be orphaned",
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
                Log.Verbose("Scheduler {0} in error state (attempt {1}/{2}), waiting for recovery interval",
                    entityId, _consecutiveErrors, _maxConsecutiveErrors);
                return;
            }
            _lastErrorRecoveryAttemptFrame = currentFrame;
            Log.Info("Scheduler {0} attempting recovery from error state (attempt {1}/{2})",
                entityId, _consecutiveErrors, _maxConsecutiveErrors);

            try
            {
                // Attempt to recover by re-checking capabilities
                if (CheckCapabilities())
                {
                    // Successfully recovered
                    _consecutiveErrors = 0;
                    currentState = Scheduler.State.Standby;

                    Log.Info("Scheduler {0} successfully recovered from error state", entityId);

                    // Re-initialize if necessary
                    if (!_initialized)
                    {
                        Initialize();
                    }
                }
                else
                {
                    Log.Warning("Scheduler {0} recovery attempt failed - capabilities check failed", entityId);

                    // Provide specific error information
                    if (ownAntenna == null)
                    {
                        Log.Error("Scheduler {0} error: No antenna found", entityId);
                    }
                    else if (!ownAntenna.IsFunctional)
                    {
                        Log.Error("Scheduler {0} error: Antenna is not functional", entityId);
                    }
                    else if (!ownAntenna.Enabled)
                    {
                        Log.Error("Scheduler {0} error: Antenna is not enabled", entityId);
                    }
                    else if (!ownAntenna.EnableBroadcasting)
                    {
                        Log.Error("Scheduler {0} error: Antenna broadcasting is disabled", entityId);
                    }
                    else if (!ownAntenna.IsWorking)
                    {
                        Log.Error("Scheduler {0} error: Antenna is not working (may lack power)", entityId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scheduler {0} recovery attempt threw exception: {1}", entityId, ex.Message);
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
    }
}