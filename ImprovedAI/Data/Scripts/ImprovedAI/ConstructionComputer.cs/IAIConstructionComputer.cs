using System;
using System.Collections.Generic;

using VRage.Collections;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.ModAPI;

using ImprovedAI.Config;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using static ImprovedAI.ConstructionComputer;
using Sandbox.ModAPI;
using ImprovedAI.VirtualNetwork;
using VRage.Ansel;



namespace ImprovedAI
{
    public class IAIConstructionComputer
    {
        /// Queue of discovered, but yet unporsed Jobs.
        /// </summary>
        private MyConcurrentQueue<Orchestrator.Job> jobQueue = new MyConcurrentQueue<Orchestrator.Job>();
        private bool isEnabled = false;
        private long entityId;
        private Vector3 naturalGravity;
        private IMyEntity Entity;
        private int _lastUpdateFrame = 0;
        private int _lastScanFrameAttempt = 0;
        public int PerScanLimits { get; set; }
        private int _perScanLimits;
        private int _consecutiveErrors = 0;
        private long _lastErrorRecoveryAttemptFrame = 0;
        private long _lastMaintenanceFrame = 0;
        private int _maintenanceIntervalTicks;
        private int _scanRetryIntervalTicks;
        private Vector3 weldIgnoreColor;
        private Vector3 grindColor;
        private int _jobIdCounter = 0;
        private State currentState;
        public string errorMessage { get; internal set; } = "";
        private bool _initialized = false;
        private IMyCubeBlock block;
        // block settings
        public IAIConstructionComputerSettings settings = new IAIConstructionComputerSettings();

        public OperationMode operationMode;
        public WorkModes workModes;

        private IMyRadioAntenna ownAntenna;
        private List<IMyCubeGrid> _connectedGridCache = new List<IMyCubeGrid>();
        private readonly Dictionary<string, int> _componentsCache = new Dictionary<string, int>();

        private IMyGravityProviderSystemDelegate gravityProviderSystemDelegate;
        private IMySessionDelegate sessionDelegate;
        private Quaternion _quaternionCache;
        private QuaternionD _quaternionDCache;
        private MessageQueue messageQueue;
        private int _messageCounter = 0;

        public IAIConstructionComputer(
            IMyEntity entity,
            OperationMode operationMode = OperationMode.None,
            WorkModes workModes = WorkModes.None,
            IMyRadioAntenna ownAntenna = null,
            IMyGravityProviderSystemDelegate gravityProviderSystem = null,
            IMySessionDelegate sessionDelegate = null,
            IAIConstructionComputerSettings settings = null
        )
        {
            this.entityId = entity.EntityId;
            this.Entity = entity;
            this.block = (IMyCubeBlock)Entity;
            this.operationMode = operationMode;
            this.jobQueue = new MyConcurrentQueue<Orchestrator.Job>();

            this.workModes = workModes;
            this.ownAntenna = ownAntenna;
            this.gravityProviderSystemDelegate = gravityProviderSystem ?? new MyGravityProviderSystemDelegate();
            this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
            if (settings != null)
            {
                this.settings = settings;
                weldIgnoreColor = settings.WeldIgnoreColor.ToVector3();
                grindColor = settings.GrindColor.ToVector3();
            }
            else
            {
                this.settings.WeldIgnoreColor = new Vector3Data(0.0f, 1.0f, 0.0f);
                weldIgnoreColor = this.settings.WeldIgnoreColor.ToVector3();
                this.settings.GrindColor = new Vector3Data(1.0f, 0.0f, 0.0f);
                grindColor = this.settings.GrindColor.ToVector3();
                this.settings.IgnoreTasksOutsideSpecifiedRangeMeters = 1000.0f;
                this.settings.IgnoreTasksOutsideOfAntenaRange = true;
                this.settings.IgnoreTasksOutsideSpecifiedRange = false;
                this.settings.IsEnabled = true;
                this.settings.OperationMode = OperationMode.None;
                this.settings.WorkModes = WorkModes.None;
                this.settings.ShareWith = ShareWith.NoOne;
                this.settings.WeldIgnoreList = new List<ulong>();
                this.settings.GrindIgnoreList = new List<ulong>();
                this.settings.PerScanLimits = MathHelper.Clamp(settings.PerScanLimits, (byte)1, ServerConfig.Instance.ConstructionComputer.BlockLimitPerScan);
                this.settings.ScanRetryIntervalSeconds = MathHelper.Clamp(settings.ScanRetryIntervalSeconds, ServerConfig.Instance.ConstructionComputer.MinScanRetryIntervalSeconds, ServerConfig.Instance.ConstructionComputer.MaxScanRetryIntervalSeconds);
                _maintenanceIntervalTicks = TimeUtil.TimeSpanToTick(new TimeSpan(0, 0, ServerConfig.Instance.ConstructionComputer.MaintenanceIntervalSeconds));
                _scanRetryIntervalTicks = TimeUtil.TimeSpanToTick(new TimeSpan(0, 0, settings.ScanRetryIntervalSeconds));
                this.settings.MaxJobAnnouncementsPerUpdate = MathHelper.Clamp(settings.MaxJobAnnouncementsPerUpdate, 1, ServerConfig.Instance.ConstructionComputer.MaxJobAnnouncementsPerUpdate);
            }
        }

        private void Initialize()
        {
            messageQueue = IAISession.GetMessageQueue();
            currentState = State.Initializing;
        }

        private void UpdateAI()
        {
            var currentFrame = sessionDelegate.GameplayFrameCounter;

            // Perform maintenance periodically, regardless of state
            if (currentFrame - _lastMaintenanceFrame >= _maintenanceIntervalTicks)
            {
                _lastMaintenanceFrame = currentFrame;
            }
            // built in operation mode will be called by orchestrator directly in its UpdateAI loop
            if (operationMode == OperationMode.ConstructionComputer)
            {
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
                    case State.PublishingJobs:
                        HandlePublishingJobs();
                        break;
                    case State.Error:
                        HandleError();
                        break;
                }
            }
        }
        public void HandleInitializing()
        {
            if (CheckCapabilities())
            {
                currentState = State.Standby;
                _initialized = true;

            }
            else
            {
                currentState = State.Error;
                errorMessage = "Failed to initialize construction computer; no antenna found";
            }
        }

        public void HandleStandby()
        {
            var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
            // Periodically check if we should scan
            if (currentFrame - _lastScanFrameAttempt >= _scanRetryIntervalTicks)
            {
                _lastScanFrameAttempt = currentFrame;
                if (ShouldStartScanning(settings.WorkModes))
                {
                    Log.Verbose("Orhcestrator {0} waking from standby to scan for tasks", entityId);
                    currentState = State.ScanningForJobs;
                }
            }
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

            return AntennaUtil.AntennaReady(ownAntenna);
        }

        private bool ShouldStartScanning(WorkModes workModes)
        {
            var workModeEval = workModes & (
                WorkModes.ScanOnly |
                WorkModes.Grind |
                WorkModes.WeldUnfinishedBlocks);
            return _initialized && settings.IsEnabled && AntennaUtil.AntennaReady(ownAntenna) && workModeEval > 0;
        }

        private void HandleScanningForJobs()
        {
            var totalJobs = 0;
            try
            {
                totalJobs += ScanForWeldRepairGrindJobs(jobQueue);
            }
            catch (Exception ex)
            {
                Log.Error("Orhcestrator {0} Error during task scanning: {1}", entityId, ex.Message);
                currentState = State.Error;
                return;
            }

            if (totalJobs == 0)
            {
                Log.Verbose("Scheduler {0] going on standby, no tasks found.", entityId);
                currentState = State.Standby;
                return;
            }
            Log.Verbose("Orhcestrator {0} found {1} tasks, transitioning to AssigningTasks", entityId, totalJobs);
            currentState = State.PublishingJobs;
        }
        public void HandleScanningForJobs(MyConcurrentQueue<Orchestrator.Job> orchestratorJobQueue)
        {
            var totalJobs = 0;
            try
            {
                totalJobs += ScanForWeldRepairGrindJobs(orchestratorJobQueue);
            }
            catch (Exception ex)
            {
                Log.Error("Orhcestrator {0} Error during task scanning: {1}", entityId, ex.Message);
                currentState = State.Error;
                return;
            }

            if (totalJobs == 0)
            {
                Log.Verbose("Scheduler {0] going on standby, no tasks found.", entityId);
                currentState = State.Standby;
                return;
            }
            Log.Verbose("Orhcestrator {0} found {1} tasks, transitioning to AssigningTasks", entityId, totalJobs);
            currentState = State.PublishingJobs;
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
        public int ScanForWeldRepairGrindJobs(MyConcurrentQueue<Orchestrator.Job> jobQueue)
        {
            var scannedBlocks = 0;
            var jobsCreated = 0;

            _lastScanFrameAttempt = sessionDelegate.GameplayFrameCounter;
            var cubeBlock = Entity as IMyCubeBlock;
            if (cubeBlock?.CubeGrid == null)
            {
                Log.Error("cannot scan: entity {0} is not an IMyCubeBlock", entityId);
                return jobsCreated;
            }

            // Set gravity, as the block may be mounted on a moving ship.
            SetGravity(cubeBlock);

            var antennaPosition = ownAntenna.CubeGrid.GridIntegerToWorld(ownAntenna.Position);
            var antennaRadius = ownAntenna.Radius;

            _connectedGridCache.Clear();
            try
            {
                MyAPIGateway.GridGroups.GetGroup(cubeBlock.CubeGrid, GridLinkTypeEnum.Physical, _connectedGridCache);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to get connected grids: {0}", ex.Message);
                return jobsCreated;
            }

            foreach (var grid in _connectedGridCache)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var block in blocks)
                {
                    if (++scannedBlocks > settings.PerScanLimits)
                    {
                        Log.Verbose("Scan limit reached ({0} blocks), continuing next update", settings.PerScanLimits);
                        // Continue scanning next update
                        return jobsCreated;
                    }
                    // TODO: handle projected blocks
                    var shouldWeld = ShouldWeldOrRepair(block);
                    var shouldGrind = ShouldGrind(block);
                    if (!shouldWeld && !shouldGrind)
                        continue;

                    var blockPosition = grid.GridIntegerToWorld(block.Position);
                    var distance = Vector3D.Distance(antennaPosition, blockPosition);
                    var isOutOfAntennaRange = distance > antennaRadius;
                    var isOutOfSpecificRange = distance > settings.IgnoreTasksOutsideSpecifiedRangeMeters;
                    if (isOutOfAntennaRange && settings.IgnoreTasksOutsideOfAntenaRange) continue;
                    if (isOutOfSpecificRange && settings.IgnoreTasksOutsideSpecifiedRange) continue;

                    if (shouldWeld)
                    {
                        _componentsCache.Clear();
                        block.GetMissingComponents(_componentsCache);
                        block.Orientation.GetQuaternion(out _quaternionCache);
                        _quaternionDCache.X = _quaternionCache.X;
                        _quaternionDCache.Y = _quaternionCache.Y;
                        _quaternionDCache.Z = _quaternionCache.Z;
                        _quaternionDCache.W = _quaternionCache.W;
                        jobQueue.Enqueue(new Orchestrator.Job
                        {
                            JobId = IdGenerator.GenerateId(ref _jobIdCounter, entityId),
                            JobType = Orchestrator.JobType.WeldBlock,
                            Inventory = new Inventory(_componentsCache),
                            OrientationData = QuaternionDData.FromQuaternion(_quaternionDCache),
                            PositionData = Vector3DData.FromVector3D(blockPosition),
                            OutOfOrchestratorRange = isOutOfAntennaRange,
                            NaturalGravity = this.naturalGravity.Normalize(),
                            IsStaticGrid = cubeBlock.CubeGrid.IsStatic,
                            IsInSpace = this.naturalGravity.Normalize() < 0.2f,
                        });
                        jobsCreated++;
                    }
                    if (shouldGrind)
                    {
                        _componentsCache.Clear();
                        AddMountedComponentsForGrind(block, _componentsCache);
                        block.Orientation.GetQuaternion(out _quaternionCache);
                        _quaternionDCache.X = _quaternionCache.X;
                        _quaternionDCache.Y = _quaternionCache.Y;
                        _quaternionDCache.Z = _quaternionCache.Z;
                        _quaternionDCache.W = _quaternionCache.W;
                        jobQueue.Enqueue(new Orchestrator.Job
                        {
                            JobId = IdGenerator.GenerateId(ref _jobIdCounter, entityId),
                            JobType = Orchestrator.JobType.GrindBlock,
                            Inventory = new Inventory(_componentsCache),
                            OrientationData = QuaternionDData.FromQuaternion(_quaternionDCache),
                            PositionData = Vector3DData.FromVector3D(blockPosition),
                            OutOfOrchestratorRange = isOutOfAntennaRange,
                             NaturalGravity = this.naturalGravity.Normalize(),
                            IsStaticGrid = cubeBlock.CubeGrid.IsStatic,
                            IsInSpace = this.naturalGravity.Normalize() < 0.2f,
                        });
                        jobsCreated++;
                    }
                }
            }
            if (jobsCreated > 0)
                Log.Info("Scan complete: {0} blocks scanned, {1} jobs created", scannedBlocks, jobsCreated);
            _connectedGridCache.Clear();
            _componentsCache.Clear();
            return jobsCreated;
        }

        /// <summary>
        /// Accumulates mounted component counts from <see cref="IMySlimBlock.ComponentStack"/> (material present on the block),
        /// keyed by <see cref="MyComponentStackInfo.ComponentName"/>. Suitable for grind job payload; merges into an existing dictionary.
        /// </summary>
        private static void AddMountedComponentsForGrind(IMySlimBlock block, Dictionary<string, int> addToDictionary)
        {
            IMyComponentStack stack = block.ComponentStack;
            if (stack == null)
            {
                return;
            }
            int groupCount = stack.GroupCount;
            for (int i = 0; i < groupCount; i++)
            {
                MyComponentStackInfo info = stack.GetComponentStackInfo(i);
                int amount = info.MountedCount;
                if (amount <= 0)
                {
                    continue;
                }
                string name = info.ComponentName;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                int existing;
                if (addToDictionary.TryGetValue(name, out existing))
                {
                    addToDictionary[name] = existing + amount;
                }
                else
                {
                    addToDictionary[name] = amount;
                }
            }
        }

        private void SetGravity(IMyCubeBlock cubeBlock)
        {
            var blockPosition = cubeBlock.CubeGrid.GridIntegerToWorld(cubeBlock.Position);
            this.naturalGravity = gravityProviderSystemDelegate.CalculateNaturalGravityInPoint(blockPosition);
        }

        private uint GenerateId()
        {
            return IdGenerator.GenerateId(ref _messageCounter, entityId);
        }

        private void HandlePublishingJobs()
        {
            // var currentFrame = sessionDelegate.GameplayFrameCounter;
            // if (currentFrame - _lastPublishFrame >= _publishIntervalTicks)
            // {
            //     _lastPublishFrame = currentFrame;
            // }
            // builtin mode does not need to go through message queue
            if (operationMode == OperationMode.ConstructionComputer)
            {
                // construction computer mode needs to go through message queue
                for (int i = 0; i < settings.MaxJobAnnouncementsPerUpdate; i++)
                {
                    Orchestrator.Job job;
                    if (jobQueue.TryDequeue(out job))
                    {
                        var msg = new Message<JobAnnouncement>
                        {
                            Payload = new JobAnnouncement
                            {
                                JobId = job.JobId,
                                Type = job.JobType,
                                Inventory = job.Inventory,
                                PositionData = job.PositionData,
                                OrientationData = job.OrientationData,
                                CreatedTime = job.CreatedTime,
                                EntityType = IAIEntityType.ConstructionComputerBlock,
                                NaturalGravity = job.NaturalGravity,
                                IsStaticGrid = job.IsStaticGrid,
                                IsInSpace = job.IsInSpace,
                            },
                            MessageId = GenerateId(),
                            CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                            SenderId = entityId,
                            SenderOwnerId = block?.OwnerId ?? 0,
                            RequiresAck = false,
                            RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                            Channel = Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
                        };
                        messageQueue.BroadcastMessage<JobAnnouncement>(ownAntenna, msg, true);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        public void HandlePublishingJobs(MyConcurrentQueue<Orchestrator.Job> orchestratorJobQueue)
        {
            // when mode is BuiltIn, we extract the jobs in the queue to the Orchestrator queue
            if (operationMode == OperationMode.BuiltIn)
            {
                for (int i = 0; i < settings.MaxJobAnnouncementsPerUpdate; i++)
                {
                    Orchestrator.Job job;
                    if (jobQueue.TryDequeue(out job))
                    {
                        orchestratorJobQueue.Enqueue(job);
                    }
                }
            }
        }

        private void HandleError()
        {

        }


    }
}