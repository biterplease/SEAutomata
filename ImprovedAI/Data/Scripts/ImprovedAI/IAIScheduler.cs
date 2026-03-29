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
        private readonly object _taskLock = new object();
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
                    ReadDroneRegistrations();
                    ReadDroneReports((ushort)messageReadLimit);
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
            int assigned = 0;
            // scheduler set to only repeater task messages
            //if (operationMode.Equals(OperationMode.Repeater))
            //{
            //    Task task;
            //    while (taskQueue.TryDequeue(out task))
            //    {
            //        var message
            //        var needsAck = false; // repeater does not care
            //        messaging.BroadcastMessage((ushort)Channel.SCHEDULER_FORWARD, task, entityId, needsAck);
            //    }
            //}
            if (registeredDrones.Count != 0 && operationMode.HasFlag(OperationMode.Orchestrator))
            {
            }
            // no registered drones
            if (registeredDrones.Count == 0 && operationMode.HasFlag(OperationMode.DelegateIfNoDrones))
            {
                for (int i = 0; i < _maxTasksAssignedPerBatch; i++)
                {
                    Task task;
                    if (taskQueue.TryDequeue(out task))
                    {
                        var needsAck = true; // delegated messages should be checked, perhaps there is no onet o pick them up
                        var messageId = messaging.SendMessage((ushort)Channel.SCHEDULER_FORWARD, task, entityId, needsAck);
                        delegatedTasksNeedingAck.Add(messageId, task.TaskId);

                        if (assigned > _maxTasksAssignedPerBatch)
                        {

                        }
                        assigned++;
                    }
                    else
                    { // no more tasks to deque
                        break;
                    }
                }
            }
            if (registeredDrones.Count == 0 && !operationMode.HasFlag(OperationMode.DelegateIfNoDrones))
                Log.Info("Scheduler {0}: No available drones", entityId);
            return;

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
                            TaskId = _idGenerator.GenerateId(),
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
                            TaskId = _idGenerator.GenerateId(),
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
            messaging.ReadMessages(entityId,ownAntenna,Channel.DIRECT_MESSAGE, _messageCache,50,true, PayloadType.DroneReport);
            var droneReports = messaging.ReadMessages<DroneReport>(entityId, (ushort)Channel.DRONE_REGISTRATION, 50);
            Drone drone;
            foreach (var reg in droneReports)
            {
                // if exists, redirect the message to drone report queue
                if (registeredDrones.TryGetValue(reg.DroneEntityId, out drone))
                {
                    messaging.SendMessage<DroneReport>((ushort)Channel.DRONE_REPORTS, reg, this.entityId, false);
                    continue;
                }
                drone = new Drone(
                    reg.DroneEntityId,
                    reg.Capabilities.GetValueOrDefault(Drone.Capabilities.None), // if this drone reported without capabilities, its probably unusable
                    reg.DroneState.GetValueOrDefault(Drone.State.Standby),
                    reg.BatteryChargePercent.GetValueOrDefault(100.0f),
                    reg.BatteryRechargeThreshold.GetValueOrDefault(25.0f),
                    reg.BatteryOperationalThreshold.GetValueOrDefault(80.0f),
                    reg.H2Level.GetValueOrDefault(100.0f),
                    reg.H2RefuelThreshold.GetValueOrDefault(25.0f),
                    reg.H2OperationalThreshold.GetValueOrDefault(80.0f)
                   );

                registeredDrones.Add(reg.DroneEntityId, drone);
            }
        }
        private void ReadDroneReports(ushort maxMessages)
        {
            var droneReports = messaging.ReadMessages<DroneReport>(entityId, (ushort)Channel.DRONE_REPORTS, maxMessages);
            foreach (var report in droneReports)
            {
                Drone drone;
                if (registeredDrones.TryGetValue(report.DroneEntityId, out drone))
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
                        HandleTaskCompletion(report.DroneEntityId, report.TaskId.Value);
                    // GoingOutOfRange unregisters, must be evaluated last
                    if (report.Flags.HasFlag(Drone.UpdateFlags.GoingOutOfRange))
                    {
                        drone.IsOutOfRange = true;
                        drone.LastSeenTime = DateTime.UtcNow;
                        Log.LogDroneNetwork("AI scheduler {0}: Drone {1} going out of range",
                            entityId, report.DroneEntityId);
                    }
                    if (report.Flags.HasFlag(Drone.UpdateFlags.ReturningIntoRange))
                    {
                        drone.IsOutOfRange = false;
                        drone.LastSeenTime = DateTime.UtcNow;
                        Log.LogDroneNetwork("AI scheduler {0}: Drone {1} returning into range", entityId, report.DroneEntityId);
                    }
                }
                else
                {
                    // if not registered, redirect to registration queue
                    messaging.SendMessage<DroneReport>((ushort)Channel.DRONE_REGISTRATION, report, this.entityId, false);
                }
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
        /// Drone gave up on a task (pathfinding dead end). Release assignment and re-queue work for another drone.
        /// </summary>
        private void HandleTaskAborted(long droneId, ushort taskId, string reason)
        {
            lock (_taskLock)
            {
                List<Task> taskList;
                if (!assignedTasks.TryGetValue(droneId, out taskList))
                {
                    Log.Warning("AI scheduler {0}: TaskAborted for task {1} from unassigned drone {2}", entityId, taskId, droneId);
                    return;
                }

                var taskIndex = taskList.FindIndex(t => t.TaskId == taskId);
                if (taskIndex < 0)
                {
                    Log.Warning("AI scheduler {0}: TaskAborted task {1} not found for drone {2}", entityId, taskId, droneId);
                    return;
                }

                var abortedTask = taskList[taskIndex];
                taskList.RemoveAt(taskIndex);
                taskQueue.Enqueue(abortedTask);

                if (taskList.Count == 0)
                {
                    List<Task> removed;
                    assignedTasks.TryRemove(droneId, out removed);
                }

                Log.Info(
                    "AI scheduler {0}: Drone {1} aborted task {2} ({3}); task re-queued for reassignment",
                    entityId, droneId, taskId, reason ?? string.Empty);
            }
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
            var droneReports = messaging.ReadMessages<DroneReport>(entityId, (ushort)Channel.DRONE_REPORTS, (ushort)messageReadLimit);
            foreach (var report in droneReports) { }
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