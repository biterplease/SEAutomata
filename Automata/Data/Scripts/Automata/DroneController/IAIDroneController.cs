using ImprovedAI.Config;
using ImprovedAI.Pathfinding;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using ImprovedAI.VirtualNetwork;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using System;
using System.Collections.Generic;
using VRage;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace Automata.DroneController
{
    // public class IAIDroneController
    // {

    //     private long _entityId;
    //     private int _messageCounter = 0;
    //     private int _bidRoundBidCounter = 0;
    //     private MessageQueue messaging;
    //     private IMyEntity entity;
    //     private IMyCubeBlock block;
    //     private IMySessionDelegate sessionDelegate;
    //     private IMyGamePruningStructureDelegate pruningStructureDelegate;
    //     private IMyPlanetDelegate planetDelegate;


 
    //     public IAIDroneControllerSettings settings;
    //     public Drone.OperationMode operationMode = Drone.OperationMode.StandAlone;
    //     public Drone.Capabilities capabilities;
    //     public Drone.BehaviourProfile behaviourProfile;

    //     public Drone.State currentState = Drone.State.Initializing;
    //     public bool initialized = false;
    //     private int _consecutiveErrors = 0;



    //     private IMyShipController shipController;
    //     private IMyShipWelder welder;
    //     private IMyShipGrinder grinder;
    //     public IMyRadioAntenna primaryAntenna;
    //     private readonly List<IMySensorBlock> sensors = new List<IMySensorBlock>();
    //     private readonly List<IMyShipConnector> connectors = new List<IMyShipConnector>();
    //     private readonly List<IMyLandingGear> landingGears = new List<IMyLandingGear>();
    //     private readonly Dictionary<Base6Directions.Direction, List<IMyGyro>> gyroscopes = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();
    //     private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> hydrogenThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
    //     private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> atmoThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
    //     private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> ionThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
    //     private readonly List<IMyGasTank> hydrogenTanks = new List<IMyGasTank>();
    //     private readonly List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
    //     private readonly List<IMyCargoContainer> cargoContainers = new List<IMyCargoContainer>();


    //     private ThrustData thrustData = new ThrustData();
    //     private Vector3D gravityVector = Vector3D.Zero;
    //     private float physicalMass = 0.0f;
    //     private float baseMass = 0.0f;

    //     #region Fields - Task Management
    //     private Orchestrator.Job currentTask;
    //     private Queue<Orchestrator.Task> taskQueue = new Queue<Orchestrator.Task>();
    //     private ushort currentTaskId = 0;
    //     #endregion

    //     #region Fields - Update Tracking
    //     private long _lastComponentCheckFrame = 0;
    //     private long _lastStatusReportFrame = 0;
    //     private long _lastPowerCheckFrame = 0;
    //     #endregion

    //     #region Fields - Power Monitoring
    //     private bool needsHydrogenRefuel = false;
    //     private bool needsBatteryRecharge = false;
    //     private float lastH2Level = 100f;
    //     public float currentH2Level = 100f;
    //     private float lastBatteryLevel = 100f;
    //     public float currentBatteryLevel = 100f;
    //     #endregion

    //     #region Fields - Rotation Control
    //     private bool isRotating = false;
    //     private long rotationStartFrame = 0;
    //     private long rotationDurationFrames = 0;
    //     private Vector3D rotationTarget;
    //     private float orientationToleranceDegrees = 5.0f;
    //     private float totalGyroscopeTorque = 0f;
    //     private float shipMomentOfInertia = 0f;
    //     #endregion

    //     #region Fields - Navigation
    //     private Vector3D taskPosition;
    //     private List<Vector3D> currentPath = new List<Vector3D>();
    //     private PathfindingManager pathfindingManager;
    //     private Vector3D currentWaypoint;


    //     private float currentHoverThrustPercentage = 0f;
    //     private Base6Directions.Direction currentHoverDirection = Base6Directions.Direction.Up;
    //     #endregion

    //     #region Fields - Update Intervals
    //     private readonly int COMPONENT_CHECK_INTERVAL_TICKS = 600;
    //     private int STATUS_REPORT_INTERVAL_TICKS;
    //     private int POWER_CHECK_INTERVAL_TICKS;
    //     #endregion

    //     #region Fields - Tool Offsets
    //     private struct ToolOffset
    //     {
    //         public Vector3D Offset;
    //         public Base6Directions.Direction ApproachDirection;
    //     }
    //     private Vector3D connectorOffset;
    //     private List<ToolOffset> weldOffsets = new List<ToolOffset>();
    //     private List<ToolOffset> grindOffsets = new List<ToolOffset>();
    //     #endregion

    //     #region Fields - Bid Round Messaging
    //     private readonly List<Message<BidRoundStart>> _bidRoundStartInbox = new List<Message<BidRoundStart>>();
    //     private readonly Queue<Message<BidRoundBid>> _bidRoundBidOutbox = new Queue<Message<BidRoundBid>>();
    //     private readonly List<Message<BidRoundWinnerAnnouncement>> _bidRoundWinnerInbox = new List<Message<BidRoundWinnerAnnouncement>>();
    //     private bool _registrationSent = false;
    //     #endregion

    //     public IAIDroneController(
    //         IMyEntity entity,
    //         MessageQueue messaging,
    //         IAIDroneControllerSettings settings,
    //         IMySessionDelegate sessionDelegate = null,
    //         IMyGamePruningStructureDelegate pruningStructure = null,
    //         IMyPlanetDelegate planetDelegate = null)
    //     {
    //         this.entity = entity;
    //         this.messaging = messaging;
    //         this.settings = settings;
    //         this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
    //         this.pruningStructureDelegate = pruningStructure ?? new MyGamePruningStructureDelegate();
    //         this.planetDelegate = planetDelegate ?? new MyPlanetDelegate();
    //         this.operationMode = settings.OperationMode;

    //         foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
    //         {
    //             gyroscopes[dir] = new List<IMyGyro>();
    //             ionThrusters[dir] = new List<IMyThrust>();
    //             atmoThrusters[dir] = new List<IMyThrust>();
    //             hydrogenThrusters[dir] = new List<IMyThrust>();
    //         }
    //     }

    //     #region Lifecycle
    //     public void Initialize()
    //     {
    //         if (initialized) return;

    //         _entityId = entity.EntityId;
    //         block = entity as IMyCubeBlock;
    //         currentState = Drone.State.Initializing;

    //         STATUS_REPORT_INTERVAL_TICKS = IAISession.GetConfig().MessageQueue.DroneMessageThrottlingTicks();
    //         POWER_CHECK_INTERVAL_TICKS = IAISession.GetConfig().Drone.PowerCheckIntervalTicks;

    //         shipController = entity as IMyShipController;
    //         if (shipController == null)
    //         {
    //             Log.Error("DroneController {0} entity is not a ship controller", _entityId);
    //             currentState = Drone.State.Error;
    //             return;
    //         }

    //         if (!CheckCapabilities())
    //         {
    //             Log.Error("DroneController {0} failed capability check", _entityId);
    //             currentState = Drone.State.Error;
    //             return;
    //         }

    //         pathfindingManager = new PathfindingManager(IAISession.GetConfig().Pathfinding, pruningStructureDelegate, planetDelegate);

    //         if (primaryAntenna != null)
    //         {
    //             messaging.RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
    //         }

    //         if (operationMode == Drone.OperationMode.ManagedByScheduler)
    //         {
    //             messaging.Subscribe(_entityId, Channel.ORCHESTRATOR_BID_ROUND_START);
    //             messaging.Subscribe(_entityId, Channel.ORCHESTRATOR_BID_ROUND_WINNER_ANNOUNCEMENT);
    //         }

    //         currentState = Drone.State.Standby;
    //         initialized = true;
    //         Log.Info("DroneController {0} initialized in mode: {1}", _entityId, operationMode);
    //     }

    //     public void UpdateEachFrame()
    //     {
    //         if (!initialized) return;
    //         if (!settings.IsEnabled || currentState == Drone.State.Error) return;
    //         UpdateRotation();
    //     }

    //     public void Update10(long currentFrame)
    //     {
    //         if (!initialized) return;

    //         if (currentFrame - _lastComponentCheckFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
    //         {
    //             _lastComponentCheckFrame = currentFrame;
    //             if (!CheckCapabilities())
    //             {
    //                 currentState = Drone.State.Error;
    //                 return;
    //             }
    //         }

    //         if (settings.MonitorHydrogenLevels || settings.MonitorBatteryLevels)
    //         {
    //             if (currentFrame - _lastPowerCheckFrame >= POWER_CHECK_INTERVAL_TICKS)
    //             {
    //                 _lastPowerCheckFrame = currentFrame;
    //                 CheckPowerLevels();
    //             }
    //         }

    //         if (operationMode == Drone.OperationMode.ManagedByScheduler)
    //         {
    //             if (currentFrame - _lastStatusReportFrame >= STATUS_REPORT_INTERVAL_TICKS)
    //             {
    //                 _lastStatusReportFrame = currentFrame;
    //                 BroadcastStatusReport();
    //             }

    //             if (!_registrationSent && primaryAntenna != null)
    //             {
    //                 BroadcastDroneRegistration();
    //                 _registrationSent = true;
    //             }

    //             ReadBidRoundStarts();
    //             SendBidRoundBids();
    //             ReadBidRoundWinnerAnnouncements();
    //         }

    //         if (settings.IsEnabled)
    //             UpdateAI();
    //     }

    //     public void Shutdown()
    //     {
    //         ResetDrone();
    //     }
    //     #endregion

        
    //     #region Flight Controls
    //     #endregion

    //     #region Bid Round Messaging
    //     private void ReadBidRoundStarts()
    //     {
    //         if (primaryAntenna == null) return;

    //         messaging.ReadMessages(
    //             _entityId, primaryAntenna,
    //             Channel.ORCHESTRATOR_BID_ROUND_START,
    //             _bidRoundStartInbox,
    //             maxMessages: 10, clear: true,
    //             messageFilters: PayloadType.BidRoundStart);

    //         for (int i = 0; i < _bidRoundStartInbox.Count; i++)
    //         {
    //             Message<BidRoundStart> msg = _bidRoundStartInbox[i];
    //             BidRoundStart brs = msg.Payload;
    //             if (brs == null) continue;
    //             if (brs.ExpirationTime < DateTime.UtcNow) continue;

    //             if (settings.managingScheduler != 0L && msg.SenderId != settings.managingScheduler) continue;

    //             if (!CanHandleJobType(brs.JobType)) continue;

    //             LogisticsComputer.InventoryFulfillment fulfillment = ComputeCargoFulfillment(brs);
    //             if (fulfillment == LogisticsComputer.InventoryFulfillment.None) continue;

    //             Vector3D dronePos = shipController != null ? shipController.GetPosition() : entity.GetPosition();
    //             Vector3D jobPos = brs.PositionData != null ? brs.PositionData.ToVector3D() : dronePos;
    //             float dist = (float)Math.Sqrt(Vector3D.DistanceSquared(dronePos, jobPos));
    //             float estimatedTime = dist / Math.Max(1f, settings.SpeedLimit);

    //             IOLocationData ioLocation = null;
    //             if (connector != null)
    //             {
    //                 ioLocation = new IOLocationData
    //                 {
    //                     PositionData = new Vector3DData(connector.GetPosition()),
    //                     OrientationData = new QuaternionDData(Quaternion.CreateFromRotationMatrix(connector.WorldMatrix)),
    //                     BlockType = IOBlockType.ShipConnector,
    //                 };
    //             }

    //             uint bidId = IdGenerator.GenerateId(ref _bidRoundBidCounter, _entityId);
    //             _bidRoundBidOutbox.Enqueue(new Message<BidRoundBid>
    //             {
    //                 Payload = new BidRoundBid
    //                 {
    //                     EntityId = _entityId,
    //                     EntityType = IAIEntityType.DroneControllerBlock,
    //                     JobId = brs.JobId,
    //                     BidRoundId = brs.BidRoundId,
    //                     BidRoundBidId = bidId,
    //                     Capabilities = GetEffectiveCapabilities(),
    //                     MaxCargoVolume = (MyFixedPoint)CalculateMaxCargoVolume(),
    //                     MaxLoadIn1G = (MyFixedPoint)CalculateMaxLoadIn1G(),
    //                     OptimalLoadIn1G = MyFixedPoint.Zero,
    //                     InventoryFulfillmentFlags = fulfillment,
    //                     DroneBehaviours = ComputeBehaviours(),
    //                     IOLocationData = ioLocation,
    //                 },
    //                 MessageId = IdGenerator.GenerateId(ref _messageCounter, _entityId),
    //                 CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
    //                 SenderId = _entityId,
    //                 SenderOwnerId = block != null ? block.OwnerId : 0L,
    //                 RequiresAck = false,
    //                 RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
    //                 Channel = Channel.ORCHESTRATOR_BID_ROUND_BIDS,
    //             });
    //             Log.Verbose("DroneController {0} enqueued BidRoundBid {1} for job {2} type {3}",
    //                 _entityId, bidId, brs.JobId, brs.JobType);
    //         }
    //     }

    //     private void SendBidRoundBids()
    //     {
    //         if (primaryAntenna == null) return;
    //         while (_bidRoundBidOutbox.Count > 0)
    //         {
    //             Message<BidRoundBid> bid;
    //             if (!_bidRoundBidOutbox.TryDequeue(out bid)) break;
    //             ErrorCode err = messaging.BroadcastMessage(primaryAntenna, bid);
    //             if (err != ErrorCode.None && err != ErrorCode.NoSubscribers)
    //                 Log.Warning("DroneController {0} BroadcastMessage(BidRoundBid) error: {1}", _entityId, err);
    //         }
    //     }

    //     private void ReadBidRoundWinnerAnnouncements()
    //     {
    //         if (primaryAntenna == null) return;

    //         messaging.ReadMessages(
    //             _entityId, primaryAntenna,
    //             Channel.ORCHESTRATOR_BID_ROUND_WINNER_ANNOUNCEMENT,
    //             _bidRoundWinnerInbox,
    //             maxMessages: 10, clear: true,
    //             messageFilters: PayloadType.BidRoundWinnerAnnouncement);

    //         for (int i = 0; i < _bidRoundWinnerInbox.Count; i++)
    //         {
    //             BidRoundWinnerAnnouncement ann = _bidRoundWinnerInbox[i].Payload;
    //             if (ann == null || ann.TaskAssignments == null) continue;

    //             List<Orchestrator.Task> assignedTasks;
    //             if (!ann.TaskAssignments.TryGetValue(_entityId, out assignedTasks)) continue;
    //             if (assignedTasks == null || assignedTasks.Count == 0) continue;

    //             for (int t = 0; t < assignedTasks.Count; t++)
    //             {
    //                 taskQueue.Enqueue(assignedTasks[t]);
    //                 Log.LogDroneOrders("DroneController {0} received task {1} type {2} from winner announcement",
    //                     _entityId, assignedTasks[t].TaskId, assignedTasks[t].TaskType);
    //             }
    //         }
    //     }
    //     #endregion

    //     #region Broadcast Reporting
    //     private void BroadcastDroneRegistration()
    //     {
    //         if (primaryAntenna == null) return;
    //         var msg = new Message<DroneReport>
    //         {
    //             Payload = new DroneReport
    //             {
    //                 DroneEntityId = _entityId,
    //                 Flags = Drone.UpdateFlags.Registration,
    //                 DroneState = currentState,
    //                 Capabilities = capabilities,
    //                 BatteryChargePercent = currentBatteryLevel,
    //                 BatteryRechargeThreshold = settings.BatteryRefuelThreshold,
    //                 BatteryOperationalThreshold = settings.BatteryOperationalThreshold,
    //                 H2Level = currentH2Level,
    //                 H2RefuelThreshold = settings.HydrogenRefuelThreshold,
    //                 H2OperationalThreshold = settings.HydrogenOperationalThreshold,
    //             },
    //             MessageId = IdGenerator.GenerateId(ref _messageCounter, _entityId),
    //             CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
    //             SenderId = _entityId,
    //             SenderOwnerId = block != null ? block.OwnerId : 0L,
    //             RequiresAck = false,
    //             RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
    //             Channel = Channel.DRONE_REGISTRATION,
    //         };
    //         ErrorCode err = messaging.BroadcastMessage(primaryAntenna, msg);
    //         if (err != ErrorCode.None && err != ErrorCode.NoSubscribers)
    //             Log.Warning("DroneController {0} registration broadcast error: {1}", _entityId, err);
    //         Log.LogDroneNetwork(LogLevel.Verbose, "DroneController {0} broadcast registration", _entityId);
    //     }

    //     private void BroadcastStatusReport()
    //     {
    //         if (primaryAntenna == null) return;
    //         Drone.UpdateFlags flags = Drone.UpdateFlags.None;

    //         if (Math.Abs(currentH2Level - lastH2Level) > 0.05f)
    //         {
    //             flags |= Drone.UpdateFlags.H2Update;
    //             lastH2Level = currentH2Level;
    //         }
    //         if (Math.Abs(currentBatteryLevel - lastBatteryLevel) > 0.05f)
    //         {
    //             flags |= Drone.UpdateFlags.BatteryUpdate;
    //             lastBatteryLevel = currentBatteryLevel;
    //         }
    //         if (flags == Drone.UpdateFlags.None) return;

    //         var msg = new Message<DroneReport>
    //         {
    //             Payload = new DroneReport
    //             {
    //                 DroneEntityId = _entityId,
    //                 Flags = flags,
    //                 DroneState = currentState,
    //                 BatteryChargePercent = currentBatteryLevel,
    //                 H2Level = currentH2Level,
    //             },
    //             MessageId = IdGenerator.GenerateId(ref _messageCounter, _entityId),
    //             CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
    //             SenderId = _entityId,
    //             SenderOwnerId = block != null ? block.OwnerId : 0L,
    //             RequiresAck = false,
    //             RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
    //             Channel = Channel.DRONE_REPORTS,
    //         };
    //         ErrorCode err = messaging.BroadcastMessage(primaryAntenna, msg);
    //         if (err != ErrorCode.None && err != ErrorCode.NoSubscribers)
    //             Log.Warning("DroneController {0} status report broadcast error: {1}", _entityId, err);
    //     }

    //     private void BroadcastTaskComplete()
    //     {
    //         if (primaryAntenna == null) return;
    //         var msg = new Message<DroneReport>
    //         {
    //             Payload = new DroneReport
    //             {
    //                 DroneEntityId = _entityId,
    //                 TaskId = currentTaskId,
    //                 Flags = Drone.UpdateFlags.TaskComplete,
    //                 BatteryChargePercent = currentBatteryLevel,
    //                 H2Level = currentH2Level,
    //             },
    //             MessageId = IdGenerator.GenerateId(ref _messageCounter, _entityId),
    //             CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
    //             SenderId = _entityId,
    //             SenderOwnerId = block != null ? block.OwnerId : 0L,
    //             RequiresAck = false,
    //             RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
    //             Channel = Channel.DRONE_REPORTS,
    //         };
    //         ErrorCode err = messaging.BroadcastMessage(primaryAntenna, msg);
    //         if (err != ErrorCode.None && err != ErrorCode.NoSubscribers)
    //             Log.Warning("DroneController {0} task complete broadcast error: {1}", _entityId, err);
    //         Log.LogDroneNetwork(LogLevel.Verbose, "DroneController {0} broadcast task {1} complete", _entityId, currentTaskId);
    //     }

    //     private void BroadcastTaskAborted(ushort taskId, string reason)
    //     {
    //         if (primaryAntenna == null) return;
    //         var msg = new Message<DroneReport>
    //         {
    //             Payload = new DroneReport
    //             {
    //                 DroneEntityId = _entityId,
    //                 TaskId = taskId,
    //                 Flags = Drone.UpdateFlags.Error,
    //                 ErrorMessage = reason,
    //                 DroneState = currentState,
    //             },
    //             MessageId = IdGenerator.GenerateId(ref _messageCounter, _entityId),
    //             CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
    //             SenderId = _entityId,
    //             SenderOwnerId = block != null ? block.OwnerId : 0L,
    //             RequiresAck = false,
    //             RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
    //             Channel = Channel.DRONE_REPORTS,
    //         };
    //         ErrorCode err = messaging.BroadcastMessage(primaryAntenna, msg);
    //         if (err != ErrorCode.None && err != ErrorCode.NoSubscribers)
    //             Log.Warning("DroneController {0} task aborted broadcast error: {1}", _entityId, err);
    //         Log.LogDroneNetwork(LogLevel.Warning, "DroneController {0} broadcast task {1} aborted: {2}", _entityId, taskId, reason);
    //     }
    //     #endregion

    //     #region Bid Helpers
    //     private bool CanHandleJobType(Orchestrator.JobType jobType)
    //     {
    //         Drone.Capabilities eff = GetEffectiveCapabilities();
    //         switch (jobType)
    //         {
    //             case Orchestrator.JobType.WeldBlock:
    //                 return (eff & Drone.Capabilities.CanWeld) != 0;
    //             case Orchestrator.JobType.GrindBlock:
    //                 return (eff & Drone.Capabilities.CanGrind) != 0;
    //             case Orchestrator.JobType.MineOre:
    //                 return (eff & Drone.Capabilities.CanDrill) != 0;
    //             case Orchestrator.JobType.DeliverMissingInventory:
    //             case Orchestrator.JobType.CollectInventorySurplus:
    //                 return CalculateMaxCargoVolume() > 0f;
    //             default:
    //                 return false;
    //         }
    //     }

    //     private LogisticsComputer.InventoryFulfillment ComputeCargoFulfillment(BidRoundStart brs)
    //     {
    //         float maxVolume = CalculateMaxCargoVolume();
    //         if (maxVolume <= 0f) return LogisticsComputer.InventoryFulfillment.None;

    //         float bidVolume = brs.TotalVolume > 0 ? (float)brs.TotalVolume : 0f;
    //         float maxLoad = CalculateMaxLoadIn1G();
    //         float bidMass = brs.TotalMass > 0 ? (float)brs.TotalMass : 0f;

    //         bool volumeFullyFits = bidVolume <= 0f || maxVolume >= bidVolume;
    //         bool loadFullyFits = maxLoad <= 0f || bidMass <= 0f || maxLoad >= bidMass;

    //         if (volumeFullyFits && loadFullyFits)
    //             return LogisticsComputer.InventoryFulfillment.AcceptAll;
    //         return LogisticsComputer.InventoryFulfillment.AcceptPartial;
    //     }

    //     private float CalculateMaxCargoVolume()
    //     {
    //         float total = 0f;
    //         for (int i = 0; i < cargoContainers.Count; i++)
    //         {
    //             IMyCargoContainer cargo = cargoContainers[i];
    //             if (cargo == null || !cargo.IsWorking) continue;
    //             IMyInventory inv = cargo.GetInventory(0);
    //             if (inv != null) total += (float)inv.MaxVolume;
    //         }
    //         return total;
    //     }


    //     private Drone.Capabilities GetEffectiveCapabilities()
    //     {
    //         if (!settings.UseCapabilityFilters) return capabilities;
    //         return capabilities & ~settings.CapabilityFilters;
    //     }
    //     #endregion

    //     #region Power Monitoring
    //     private void CheckPowerLevels()
    //     {
    //         if (settings.MonitorHydrogenLevels && hydrogenTanks.Count > 0)
    //         {
    //             float totalCap = 0f, totalStored = 0f;
    //             for (int i = 0; i < hydrogenTanks.Count; i++)
    //             {
    //                 IMyGasTank tank = hydrogenTanks[i];
    //                 if (!tank.IsWorking) continue;
    //                 totalCap += tank.Capacity;
    //                 totalStored += (float)(tank.Capacity * tank.FilledRatio);
    //             }
    //             currentH2Level = totalCap > 0 ? (totalStored / totalCap) * 100f : 0f;

    //             if (!needsHydrogenRefuel && currentH2Level <= settings.HydrogenRefuelThreshold)
    //             {
    //                 needsHydrogenRefuel = true;
    //                 Log.Info("DroneController {0} H2 low: {1:F1}%", _entityId, currentH2Level);
    //                 if (currentState != Drone.State.RefuelingHydrogen && currentState != Drone.State.ReturningToBase)
    //                     currentState = Drone.State.ReturningToBase;
    //             }
    //             else if (needsHydrogenRefuel && currentH2Level >= settings.HydrogenOperationalThreshold)
    //             {
    //                 needsHydrogenRefuel = false;
    //                 Log.Info("DroneController {0} H2 refueled: {1:F1}%", _entityId, currentH2Level);
    //             }
    //         }

    //         if (settings.MonitorBatteryLevels && batteries.Count > 0)
    //         {
    //             float totalCap = 0f, totalStored = 0f;
    //             for (int i = 0; i < batteries.Count; i++)
    //             {
    //                 IMyBatteryBlock bat = batteries[i];
    //                 if (!bat.IsWorking) continue;
    //                 totalCap += bat.MaxStoredPower;
    //                 totalStored += bat.CurrentStoredPower;
    //             }
    //             currentBatteryLevel = totalCap > 0 ? (totalStored / totalCap) * 100f : 0f;

    //             if (!needsBatteryRecharge && currentBatteryLevel <= settings.BatteryRefuelThreshold)
    //             {
    //                 needsBatteryRecharge = true;
    //                 Log.Info("DroneController {0} battery low: {1:F1}%", _entityId, currentBatteryLevel);
    //                 if (currentState != Drone.State.RechargingBattery && currentState != Drone.State.ReturningToBase)
    //                     currentState = Drone.State.ReturningToBase;
    //             }
    //             else if (needsBatteryRecharge && currentBatteryLevel >= settings.BatteryOperationalThreshold)
    //             {
    //                 needsBatteryRecharge = false;
    //                 Log.Info("DroneController {0} battery recharged: {1:F1}%", _entityId, currentBatteryLevel);
    //             }
    //         }
    //     }
    //     #endregion

    //     #region AI State Machine
    //     private void UpdateAI()
    //     {
    //         switch (currentState)
    //         {
    //             case Drone.State.Standby:
    //                 HandleStandby();
    //                 break;
    //             case Drone.State.NavigatingToTarget:
    //                 HandleNavigating();
    //                 break;
    //             case Drone.State.Welding:
    //                 HandleWelding();
    //                 break;
    //             case Drone.State.Grinding:
    //                 HandleGrinding();
    //                 break;
    //             case Drone.State.ReturningToBase:
    //                 HandleReturningToBase();
    //                 break;
    //             case Drone.State.AligningToHome:
    //                 HandleAligningToHome();
    //                 break;
    //             case Drone.State.Docking:
    //                 HandleDocking();
    //                 break;
    //             case Drone.State.RefuelingHydrogen:
    //             case Drone.State.RechargingBattery:
    //                 HandleRefueling();
    //                 break;
    //             case Drone.State.Error:
    //                 HandleError();
    //                 break;
    //         }
    //     }

    //     private void HandleStandby()
    //     {
    //         if (needsHydrogenRefuel || needsBatteryRecharge)
    //         {
    //             currentState = Drone.State.ReturningToBase;
    //             return;
    //         }
    //         if (currentTask != null)
    //         {
    //             StartTask(currentTask);
    //         }
    //         else if (taskQueue.Count > 0)
    //         {
    //             currentTask = taskQueue.Dequeue();
    //             StartTask(currentTask);
    //         }
    //     }

    //     private void StartTask(Orchestrator.Job task)
    //     {
    //         currentTaskId = 0;
    //         taskPosition = task.Position;

    //         switch (task.JobType)
    //         {
    //             case Orchestrator.JobType.WeldBlock:
    //             case Orchestrator.JobType.GrindBlock:
    //                 currentState = Drone.State.NavigatingToTarget;
    //                 Log.LogDroneOrders("DroneController {0} starting job type {1} at {2}", _entityId, task.JobType, task.Position);
    //                 break;
    //             default:
    //                 Log.Warning("DroneController {0} received unsupported job type: {1}", _entityId, task.JobType);
    //                 CompleteCurrentTask();
    //                 break;
    //         }
    //     }

    //     private void StartTask(Orchestrator.Task task)
    //     {
    //         currentTaskId = (ushort)task.TaskId;
    //         taskPosition = task.Position;

    //         switch (task.TaskType)
    //         {
    //             case Orchestrator.TaskType.WeldBlock:
    //             case Orchestrator.TaskType.ApproachLocation:
    //             case Orchestrator.TaskType.CollectInventory:
    //             case Orchestrator.TaskType.DeliverInventory:
    //                 currentState = Drone.State.NavigatingToTarget;
    //                 Log.LogDroneOrders("DroneController {0} starting task {1} type {2}", _entityId, task.TaskId, task.TaskType);
    //                 break;
    //             case Orchestrator.TaskType.GrindBlock:
    //                 currentState = Drone.State.NavigatingToTarget;
    //                 Log.LogDroneOrders("DroneController {0} starting grind task {1}", _entityId, task.TaskId);
    //                 break;
    //             case Orchestrator.TaskType.ReturnHome:
    //                 currentState = Drone.State.ReturningToBase;
    //                 break;
    //             default:
    //                 Log.Warning("DroneController {0} unsupported task type {1}", _entityId, task.TaskType);
    //                 CompleteCurrentTask();
    //                 break;
    //         }
    //     }

    //     private void HandleWelding()
    //     {
    //         if (welder == null) { CompleteCurrentTask(); return; }
    //         if (!welder.Enabled) welder.Enabled = true;
    //         CompleteCurrentTask();
    //     }

    //     private void HandleGrinding()
    //     {
    //         if (grinder == null) { CompleteCurrentTask(); return; }
    //         if (!grinder.Enabled) grinder.Enabled = true;
    //         CompleteCurrentTask();
    //     }

    //     private void HandleReturningToBase()
    //     {
    //         if (connector == null) { currentState = Drone.State.Standby; return; }
    //         Vector3D connPos = connector.GetPosition();
    //         Vector3D curPos = shipController.GetPosition();
    //         double distSq = Vector3D.DistanceSquared(curPos, connPos);
    //         if (distSq < 100.0)
    //         {
    //             currentState = settings.EnforceHomeOrientation ? Drone.State.AligningToHome : Drone.State.Docking;
    //         }
    //         else
    //         {
    //             MoveToPosition(connPos);
    //         }
    //     }

    //     private void HandleDocking()
    //     {
    //         if (connector == null) { currentState = Drone.State.Standby; return; }
    //         if (!connector.IsConnected) connector.Connect();
    //         if (connector.IsConnected)
    //         {
    //             if (needsHydrogenRefuel) currentState = Drone.State.RefuelingHydrogen;
    //             else if (needsBatteryRecharge) currentState = Drone.State.RechargingBattery;
    //             else currentState = Drone.State.Standby;
    //         }
    //     }

    //     private void HandleAligningToHome()
    //     {
    //         Vector3D homeForward = settings.HomeForwardDirection;
    //         Vector3D.Normalize(ref homeForward, out homeForward);
    //         Vector3D homePos = settings.HomePosition;
    //         Vector3D offset;
    //         Vector3D.Multiply(ref homeForward, 100.0, out offset);
    //         Vector3D targetPoint;
    //         Vector3D.Add(ref homePos, ref offset, out targetPoint);

    //         if (IsOrientedTowards(ref targetPoint))
    //         {
    //             currentState = Drone.State.Docking;
    //         }
    //         else if (!isRotating)
    //         {
    //             if (!InitiateRotation(ref targetPoint))
    //                 currentState = Drone.State.Docking;
    //         }
    //     }

    //     private void HandleRefueling()
    //     {
    //         if (!needsHydrogenRefuel && !needsBatteryRecharge)
    //         {
    //             currentState = Drone.State.Standby;
    //             Log.Info("DroneController {0} refueling complete", _entityId);
    //         }
    //     }

    //     private void HandleError()
    //     {
    //         _consecutiveErrors++;
    //         if (_consecutiveErrors >= ServerConfig.Instance.SchedulerBounds.MaxConsecutiveErrors)
    //             Log.Error("DroneController {0} critical error - disabling", _entityId);
    //     }

    //     private void CompleteCurrentTask()
    //     {
    //         if (currentTask != null && operationMode == Drone.OperationMode.ManagedByScheduler)
    //             BroadcastTaskComplete();
    //         currentTask = null;
    //         currentTaskId = 0;
    //         currentState = Drone.State.Standby;
    //     }

    //     private void AbortCurrentTaskDueToPathfindingComplexity()
    //     {
    //         if (currentTask != null && operationMode == Drone.OperationMode.ManagedByScheduler)
    //             BroadcastTaskAborted((ushort)(currentTask != null ? currentTaskId : 0), "Pathfinding complexity budget exceeded");
    //         currentTask = null;
    //         currentTaskId = 0;
    //         pathfindingManager.ClearCache();
    //         currentState = Drone.State.ReturningToBase;
    //         Log.Warning("DroneController {0} aborted task due to pathfinding complexity", _entityId);
    //     }

    //     private void MoveToPosition(Vector3D targetPos)
    //     {
    //         // Placeholder - full navigation handled by ApplyNavigationalThrust
    //     }
    //     #endregion

    //     #region Navigation
    //     private void HandleNavigating()
    //     {
    //         Vector3D currentPos = shipController.GetPosition();

    //         if (pathfindingManager.ShouldUpdateWaypointTracking(ref currentPos))
    //             pathfindingManager.UpdateWaypointTracking(ref currentPos);

    //         WaypointResponse waypointInfo = pathfindingManager.GetWaypointResponse();
    //         if (waypointInfo == null)
    //         {
    //             Vector3D waypoint;
    //             PathfindingResult result = pathfindingManager.GetNextWaypoint(ref currentPos, out waypoint);
    //             if (result == PathfindingResult.Success)
    //             {
    //                 currentWaypoint = waypoint;
    //                 waypointInfo = pathfindingManager.GetWaypointResponse();
    //             }
    //             else if (pathfindingManager.LastGetNextWaypointFailedDueToComplexityBudget)
    //             {
    //                 AbortCurrentTaskDueToPathfindingComplexity();
    //                 return;
    //             }
    //             else
    //             {
    //                 Log.Error("DroneController {0} failed to get waypoint", _entityId);
    //                 return;
    //             }
    //         }

    //         float targetSpeed = CalculateSafeSpeed(ref currentPos, ref waypointInfo.Position, waypointInfo.SuggestedBehavior);
    //         ApplyNavigationalThrust(ref waypointInfo.Position, targetSpeed);

    //         float distToWaypoint = (float)Vector3D.Distance(currentPos, waypointInfo.Position);
    //         if (distToWaypoint < settings.WaypointTolerance)
    //         {
    //             if (waypointInfo.IsLastWaypoint)
    //             {
    //                 currentState = Drone.State.Welding;
    //             }
    //             else
    //             {
    //                 pathfindingManager.AdvanceToNextWaypoint();
    //                 Vector3D nextWaypoint;
    //                 PathfindingResult nextResult = pathfindingManager.GetNextWaypoint(ref currentPos, out nextWaypoint);
    //                 if (nextResult == PathfindingResult.Success)
    //                     currentWaypoint = nextWaypoint;
    //                 else if (pathfindingManager.LastGetNextWaypointFailedDueToComplexityBudget)
    //                     AbortCurrentTaskDueToPathfindingComplexity();
    //             }
    //         }
    //     }
    //     #endregion

    //     #region Rotation Control
    //     private void UpdateRotation()
    //     {
    //         if (!isRotating) return;
    //         long currentFrame = sessionDelegate.GameplayFrameCounter;
    //         long elapsed = currentFrame - rotationStartFrame;
    //         if (elapsed >= rotationDurationFrames)
    //         {
    //             DisableGyroscopeOverride();
    //             isRotating = false;
    //             if (!IsOrientedTowards(ref rotationTarget))
    //             {
    //                 double corrThresh = orientationToleranceDegrees * 0.5;
    //                 Vector3D curPos = shipController.GetPosition();
    //                 Vector3D dir;
    //                 Vector3D.Subtract(ref rotationTarget, ref curPos, out dir);
    //                 Vector3D.Normalize(ref dir, out dir);
    //                 double dot;
    //                 Vector3D.Dot(ref shipController.WorldMatrix.Forward, ref dir, out dot);
    //                 dot = MathHelper.Clamp(dot, -1.0, 1.0);
    //                 double remainDeg = Math.Acos(dot) * (180.0 / Math.PI);
    //                 if (remainDeg > corrThresh)
    //                     InitiateRotation(ref rotationTarget);
    //             }
    //         }
    //     }

    //     private bool IsOrientedTowards(ref Vector3D targetPosition)
    //     {
    //         if (shipController == null) return false;
    //         Vector3D curPos = shipController.GetPosition();
    //         Vector3D dir;
    //         Vector3D.Subtract(ref targetPosition, ref curPos, out dir);
    //         Vector3D.Normalize(ref dir, out dir);
    //         Vector3D fwd = shipController.WorldMatrix.Forward;
    //         double dot;
    //         Vector3D.Dot(ref fwd, ref dir, out dot);
    //         dot = MathHelper.Clamp(dot, -1.0, 1.0);
    //         return Math.Acos(dot) * (180.0 / Math.PI) <= orientationToleranceDegrees;
    //     }

    //     private bool InitiateRotation(ref Vector3D targetPosition)
    //     {
    //         if (isRotating) return false;
    //         if (IsOrientedTowards(ref targetPosition)) return false;
    //         if (totalGyroscopeTorque <= 0 || shipMomentOfInertia <= 0)
    //         {
    //             CalibrateRotationCapabilities();
    //             if (totalGyroscopeTorque <= 0) return false;
    //         }

    //         Vector3D curPos = shipController.GetPosition();
    //         Vector3D dir;
    //         Vector3D.Subtract(ref targetPosition, ref curPos, out dir);
    //         Vector3D.Normalize(ref dir, out dir);
    //         double dot;
    //         Vector3D.Dot(ref shipController.WorldMatrix.Forward, ref dir, out dot);
    //         dot = MathHelper.Clamp(dot, -1.0, 1.0);
    //         double angleRad = Math.Acos(dot);

    //         double angAccel = totalGyroscopeTorque / shipMomentOfInertia;
    //         double rotTime = Math.Sqrt(2.0 * angleRad / angAccel) * 1.3;
    //         rotationDurationFrames = Math.Max(10L, Math.Min(600L, (long)(rotTime * 60.0)));

    //         isRotating = true;
    //         rotationStartFrame = sessionDelegate.GameplayFrameCounter;
    //         rotationTarget = targetPosition;
    //         OrientTowardsTarget(ref targetPosition);
    //         return true;
    //     }

    //     private void OrientTowardsTarget(ref Vector3D targetPosition)
    //     {
    //         if (shipController == null) return;
    //         Vector3D curPos = shipController.GetPosition();
    //         MatrixD wm = shipController.WorldMatrix;
    //         Vector3D curFwd = wm.Forward;
    //         Vector3D curUp = wm.Up;
    //         Vector3D curLeft = wm.Left;

    //         Vector3D dir;
    //         Vector3D.Subtract(ref targetPosition, ref curPos, out dir);
    //         Vector3D.Normalize(ref dir, out dir);
    //         Vector3D desiredFwd = dir;
    //         Vector3D desiredUp, desiredLeft;

    //         double gravLenSq = gravityVector.LengthSquared();
    //         if (settings.AlignToPGravity && gravLenSq > 0.1)
    //         {
    //             Vector3D antiGrav;
    //             Vector3D.Negate(ref gravityVector, out antiGrav);
    //             Vector3D.Normalize(ref antiGrav, out antiGrav);
    //             double fwdDotAG;
    //             Vector3D.Dot(ref desiredFwd, ref antiGrav, out fwdDotAG);
    //             Vector3D agComp;
    //             Vector3D.Multiply(ref antiGrav, fwdDotAG, out agComp);
    //             Vector3D.Subtract(ref antiGrav, ref agComp, out desiredUp);
    //             double upLenSq = desiredUp.LengthSquared();
    //             if (upLenSq > 0.01) Vector3D.Normalize(ref desiredUp, out desiredUp);
    //             else desiredUp = curUp;
    //         }
    //         else
    //         {
    //             double fwdDotUp;
    //             Vector3D.Dot(ref desiredFwd, ref curUp, out fwdDotUp);
    //             Vector3D upComp;
    //             Vector3D.Multiply(ref desiredFwd, fwdDotUp, out upComp);
    //             Vector3D.Subtract(ref curUp, ref upComp, out desiredUp);
    //             double upLenSq = desiredUp.LengthSquared();
    //             if (upLenSq > 0.01) Vector3D.Normalize(ref desiredUp, out desiredUp);
    //             else desiredUp = Vector3D.CalculatePerpendicularVector(desiredFwd);
    //         }

    //         Vector3D.Cross(ref desiredUp, ref desiredFwd, out desiredLeft);
    //         Vector3D.Normalize(ref desiredLeft, out desiredLeft);

    //         const double GAIN = 2.0;
    //         Vector3D fwdErr, upErr;
    //         Vector3D.Cross(ref curFwd, ref desiredFwd, out fwdErr);
    //         Vector3D.Cross(ref curUp, ref desiredUp, out upErr);
    //         Vector3D upErrScaled;
    //         Vector3D.Multiply(ref upErr, 0.5, out upErrScaled);
    //         Vector3D totalErr;
    //         Vector3D.Add(ref fwdErr, ref upErrScaled, out totalErr);

    //         double pitch, yaw, roll;
    //         Vector3D.Dot(ref totalErr, ref curLeft, out pitch);
    //         Vector3D.Dot(ref totalErr, ref curUp, out yaw);
    //         Vector3D.Dot(ref totalErr, ref curFwd, out roll);
    //         pitch = MathHelper.Clamp(pitch * GAIN, -1.0, 1.0);
    //         yaw = MathHelper.Clamp(yaw * GAIN, -1.0, 1.0);
    //         roll = MathHelper.Clamp(roll * GAIN, -1.0, 1.0);
    //         ApplyGyroscopeOverride(pitch, yaw, roll);
    //     }

    //     private void ApplyGyroscopeOverride(double pitch, double yaw, double roll)
    //     {
    //         foreach (var kvp in gyroscopes)
    //         {
    //             Base6Directions.Direction dir = kvp.Key;
    //             List<IMyGyro> gyroList = kvp.Value;
    //             if (gyroList.Count == 0) continue;

    //             float gp = 0f, gy = 0f, gr = 0f;
    //             switch (dir)
    //             {
    //                 case Base6Directions.Direction.Forward:
    //                     gp = (float)pitch; gy = (float)yaw; gr = (float)roll; break;
    //                 case Base6Directions.Direction.Backward:
    //                     gp = -(float)pitch; gy = -(float)yaw; gr = (float)roll; break;
    //                 case Base6Directions.Direction.Left:
    //                     gp = (float)yaw; gy = -(float)pitch; gr = (float)roll; break;
    //                 case Base6Directions.Direction.Right:
    //                     gp = -(float)yaw; gy = (float)pitch; gr = (float)roll; break;
    //                 case Base6Directions.Direction.Up:
    //                     gp = (float)roll; gy = (float)yaw; gr = -(float)pitch; break;
    //                 case Base6Directions.Direction.Down:
    //                     gp = -(float)roll; gy = (float)yaw; gr = (float)pitch; break;
    //             }

    //             for (int i = 0; i < gyroList.Count; i++)
    //             {
    //                 IMyGyro gyro = gyroList[i];
    //                 if (!gyro.IsFunctional) continue;
    //                 gyro.GyroOverride = true;
    //                 gyro.Pitch = gp;
    //                 gyro.Yaw = gy;
    //                 gyro.Roll = gr;
    //             }
    //         }
    //     }

    //     private void DisableGyroscopeOverride()
    //     {
    //         foreach (var kvp in gyroscopes)
    //         {
    //             List<IMyGyro> gyroList = kvp.Value;
    //             for (int i = 0; i < gyroList.Count; i++)
    //             {
    //                 gyroList[i].Pitch = 0;
    //                 gyroList[i].Yaw = 0;
    //                 gyroList[i].Roll = 0;
    //                 gyroList[i].GyroOverride = false;
    //             }
    //         }
    //     }

    //     private void CancelRotation()
    //     {
    //         if (isRotating)
    //         {
    //             DisableGyroscopeOverride();
    //             isRotating = false;
    //         }
    //     }

    //     private void CalibrateRotationCapabilities()
    //     {
    //         if (shipController == null) return;
    //         totalGyroscopeTorque = 0f;
    //         int count = 0;
    //         foreach (var kvp in gyroscopes)
    //         {
    //             for (int i = 0; i < kvp.Value.Count; i++)
    //             {
    //                 IMyGyro gyro = kvp.Value[i];
    //                 if (!gyro.IsFunctional || !gyro.Enabled) continue;
    //                 var def = gyro.SlimBlock?.BlockDefinition as MyGyroDefinition;
    //                 if (def != null) { totalGyroscopeTorque += def.ForceMagnitude; count++; }
    //             }
    //         }
    //         if (count == 0) { totalGyroscopeTorque = 0f; shipMomentOfInertia = 0f; return; }

    //         var mass = shipController.CalculateShipMass();
    //         currentMass = mass.TotalMass;
    //         var bb = shipController.CubeGrid.LocalAABB;
    //         Vector3 size;
    //         Vector3.Subtract(ref bb.Max, ref bb.Min, out size);
    //         float gs = shipController.CubeGrid.GridSize;
    //         float w = size.X * gs, h = size.Y * gs, d = size.Z * gs;
    //         float mOt = currentMass / 12f;
    //         float Ix = mOt * (h * h + d * d);
    //         float Iy = mOt * (w * w + d * d);
    //         float Iz = mOt * (w * w + h * h);
    //         shipMomentOfInertia = Math.Max(Math.Max(Ix, Iy), Iz);
    //     }
    //     #endregion

    //     #region Thrust Management
    //     private float CalculateSafeSpeed(ref Vector3D currentPos, ref Vector3D waypointPos, WaypointBehavior behavior)
    //     {
    //         float targetSpeed;
    //         switch (behavior)
    //         {
    //             case WaypointBehavior.RunThrough: targetSpeed = settings.SpeedLimit; break;
    //             case WaypointBehavior.SlowApproach: targetSpeed = settings.ApproachSpeed; break;
    //             case WaypointBehavior.FullStop: targetSpeed = 0f; break;
    //             default: targetSpeed = settings.SpeedLimit; break;
    //         }
    //         Vector3D diff;
    //         Vector3D.Subtract(ref waypointPos, ref currentPos, out diff);
    //         float dist = (float)diff.Length();
    //         float currentSpeed = (float)shipController.GetShipVelocities().LinearVelocity.Length();
    //         float stopDist = CalculateStoppingDistance(currentSpeed);
    //         if (stopDist > dist * 0.8f)
    //         {
    //             float maxDecel = GetMaxDecelerationForCurrentVelocity();
    //             if (maxDecel > 0.1f)
    //                 targetSpeed = Math.Min(targetSpeed, (float)Math.Sqrt(2.0 * maxDecel * dist * 0.8f));
    //             else
    //                 targetSpeed = settings.DockingSpeed;
    //         }
    //         return targetSpeed;
    //     }

    //     private void CalculateThrustProfile()
    //     {
    //         if (shipController == null || currentMass < 0.1f) { thrustProfileValid = false; return; }
    //         thrustProfile.MaxThrustForward = thrustProfile.MaxThrustBackward =
    //             thrustProfile.MaxThrustUp = thrustProfile.MaxThrustDown =
    //             thrustProfile.MaxThrustLeft = thrustProfile.MaxThrustRight = 0f;

    //         foreach (var kvp in thrusters)
    //         {
    //             float dirThrust = 0f;
    //             for (int i = 0; i < kvp.Value.Count; i++)
    //             {
    //                 IMyThrust t = kvp.Value[i];
    //                 if (t.IsWorking && t.IsFunctional) dirThrust += t.MaxEffectiveThrust;
    //             }
    //             switch (kvp.Key)
    //             {
    //                 case Base6Directions.Direction.Forward: thrustProfile.MaxThrustForward = dirThrust; break;
    //                 case Base6Directions.Direction.Backward: thrustProfile.MaxThrustBackward = dirThrust; break;
    //                 case Base6Directions.Direction.Up: thrustProfile.MaxThrustUp = dirThrust; break;
    //                 case Base6Directions.Direction.Down: thrustProfile.MaxThrustDown = dirThrust; break;
    //                 case Base6Directions.Direction.Left: thrustProfile.MaxThrustLeft = dirThrust; break;
    //                 case Base6Directions.Direction.Right: thrustProfile.MaxThrustRight = dirThrust; break;
    //             }
    //         }
    //         var massInfo = shipController.CalculateShipMass();
    //         currentMass = massInfo.TotalMass;
    //         if (currentMass < 0.1f) { thrustProfileValid = false; return; }

    //         thrustProfile.MaxAccelerationForward = thrustProfile.MaxThrustForward / currentMass;
    //         thrustProfile.MaxAccelerationBackward = thrustProfile.MaxThrustBackward / currentMass;
    //         thrustProfile.MaxAccelerationUp = thrustProfile.MaxThrustUp / currentMass;
    //         thrustProfile.MaxAccelerationDown = thrustProfile.MaxThrustDown / currentMass;
    //         thrustProfile.MaxAccelerationLeft = thrustProfile.MaxThrustLeft / currentMass;
    //         thrustProfile.MaxAccelerationRight = thrustProfile.MaxThrustRight / currentMass;
    //         thrustProfileValid = true;
    //     }

    //     private void UpdateHoverThrust()
    //     {
    //         gravityVector = shipController.GetNaturalGravity();
    //         double gravLenSq = gravityVector.LengthSquared();
    //         if (gravLenSq < 0.1) { if (currentHoverThrustPercentage > 0f) ClearHoverThrust(); return; }

    //         float hoverForce = currentMass * (float)Math.Sqrt(gravLenSq);
    //         Vector3D upDir;
    //         Vector3D.Negate(ref gravityVector, out upDir);
    //         Vector3D.Normalize(ref upDir, out upDir);
    //         MatrixD wm = shipController.WorldMatrix;
    //         Base6Directions.Direction upThrustDir = GetDirectionMostAlignedWith(ref upDir, ref wm);
    //         List<IMyThrust> upThrusters = thrusters[upThrustDir];
    //         if (upThrusters.Count == 0) return;

    //         float totalUp = 0f;
    //         for (int i = 0; i < upThrusters.Count; i++)
    //             if (upThrusters[i].IsWorking && upThrusters[i].IsFunctional)
    //                 totalUp += upThrusters[i].MaxEffectiveThrust;
    //         if (totalUp < 0.1f) return;

    //         float thrustPct = MathHelper.Clamp(hoverForce / totalUp, 0f, 1f);
    //         for (int i = 0; i < upThrusters.Count; i++)
    //             if (upThrusters[i].IsWorking && upThrusters[i].IsFunctional)
    //                 upThrusters[i].ThrustOverridePercentage = thrustPct;
    //         currentHoverThrustPercentage = thrustPct;
    //         currentHoverDirection = upThrustDir;
    //     }

    //     private void ClearHoverThrust()
    //     {
    //         if (currentHoverDirection == Base6Directions.Direction.Forward) return;
    //         List<IMyThrust> hoverThrusters = thrusters[currentHoverDirection];
    //         for (int i = 0; i < hoverThrusters.Count; i++)
    //             hoverThrusters[i].ThrustOverridePercentage = 0f;
    //         currentHoverThrustPercentage = 0f;
    //         currentHoverDirection = Base6Directions.Direction.Forward;
    //     }

    //     private void ApplyNavigationalThrust(ref Vector3D targetPosition, float targetSpeed)
    //     {
    //         UpdateHoverThrust();
    //         Vector3D curPos = shipController.GetPosition();
    //         Vector3 curVel = shipController.GetShipVelocities().LinearVelocity;
    //         Vector3D dirVec;
    //         Vector3D.Subtract(ref targetPosition, ref curPos, out dirVec);
    //         double distSq = dirVec.LengthSquared();
    //         if (distSq < 0.01) return;
    //         double dist = Math.Sqrt(distSq);
    //         Vector3D targetDir;
    //         Vector3D.Divide(ref dirVec, dist, out targetDir);
    //         Vector3D desiredVel;
    //         Vector3D.Multiply(ref targetDir, targetSpeed, out desiredVel);
    //         Vector3D curVelD = new Vector3D(curVel.X, curVel.Y, curVel.Z);
    //         Vector3D velErr;
    //         Vector3D.Subtract(ref desiredVel, ref curVelD, out velErr);
    //         MatrixD wm = shipController.WorldMatrix;
    //         MatrixD inv = MatrixD.Transpose(wm);
    //         Vector3D localErr;
    //         Vector3D.TransformNormal(ref velErr, ref inv, out localErr);
    //         const double GAIN = 0.5;
    //         ApplyDirectionalThrustDelta(Base6Directions.Direction.Forward, localErr.Z * GAIN);
    //         ApplyDirectionalThrustDelta(Base6Directions.Direction.Up, localErr.Y * GAIN);
    //         ApplyDirectionalThrustDelta(Base6Directions.Direction.Right, localErr.X * GAIN);
    //     }

    //     private void ApplyDirectionalThrustDelta(Base6Directions.Direction direction, double thrustAmount)
    //     {
    //         Base6Directions.Direction actualDir = thrustAmount >= 0 ? direction : Base6Directions.GetOppositeDirection(direction);
    //         float abs = MathHelper.Clamp((float)Math.Abs(thrustAmount), 0f, 1f);
    //         List<IMyThrust> dirThrusters = thrusters[actualDir];
    //         for (int i = 0; i < dirThrusters.Count; i++)
    //         {
    //             IMyThrust t = dirThrusters[i];
    //             if (!t.IsWorking || !t.IsFunctional) continue;
    //             t.ThrustOverridePercentage = MathHelper.Clamp(t.ThrustOverridePercentage + abs, 0f, 1f);
    //         }
    //     }

    //     private void ClearAllThrustOverrides()
    //     {
    //         foreach (var kvp in thrusters)
    //             for (int i = 0; i < kvp.Value.Count; i++)
    //                 kvp.Value[i].ThrustOverridePercentage = 0f;
    //         currentHoverThrustPercentage = 0f;
    //     }

    //     private float CalculateStoppingDistance(float currentSpeed)
    //     {
    //         if (!thrustProfileValid) return float.MaxValue;
    //         Vector3 velocity = shipController.GetShipVelocities().LinearVelocity;
    //         if (velocity.LengthSquared() < 0.01f) return 0f;
    //         float maxDecel = GetMaxDecelerationForCurrentVelocity();
    //         if (maxDecel < 0.1f) return float.MaxValue;
    //         return (currentSpeed * currentSpeed) / (2f * maxDecel);
    //     }

    //     private float GetMaxDecelerationForCurrentVelocity()
    //     {
    //         Vector3 vel = shipController.GetShipVelocities().LinearVelocity;
    //         if (vel.LengthSquared() < 0.01f) return thrustProfile.MaxAccelerationForward;
    //         Vector3D velD = new Vector3D(vel.X, vel.Y, vel.Z);
    //         MatrixD wm = shipController.WorldMatrix;
    //         MatrixD inv = MatrixD.Transpose(wm);
    //         Vector3D localVel;
    //         Vector3D.TransformNormal(ref velD, ref inv, out localVel);
    //         double absX = Math.Abs(localVel.X), absY = Math.Abs(localVel.Y), absZ = Math.Abs(localVel.Z);
    //         if (absZ > absX && absZ > absY)
    //             return localVel.Z > 0 ? thrustProfile.MaxAccelerationBackward : thrustProfile.MaxAccelerationForward;
    //         if (absY > absX)
    //             return localVel.Y > 0 ? thrustProfile.MaxAccelerationDown : thrustProfile.MaxAccelerationUp;
    //         return localVel.X > 0 ? thrustProfile.MaxAccelerationLeft : thrustProfile.MaxAccelerationRight;
    //     }

    //     private Base6Directions.Direction GetDirectionMostAlignedWith(ref Vector3D worldDir, ref MatrixD wm)
    //     {
    //         Vector3D fwd = wm.Forward, bwd = wm.Backward, up = wm.Up, dn = wm.Down, lt = wm.Left, rt = wm.Right;
    //         double dF, dB, dU, dD, dL, dR;
    //         Vector3D.Dot(ref worldDir, ref fwd, out dF);
    //         Vector3D.Dot(ref worldDir, ref bwd, out dB);
    //         Vector3D.Dot(ref worldDir, ref up, out dU);
    //         Vector3D.Dot(ref worldDir, ref dn, out dD);
    //         Vector3D.Dot(ref worldDir, ref lt, out dL);
    //         Vector3D.Dot(ref worldDir, ref rt, out dR);
    //         double max = dF;
    //         Base6Directions.Direction result = Base6Directions.Direction.Forward;
    //         if (dB > max) { max = dB; result = Base6Directions.Direction.Backward; }
    //         if (dU > max) { max = dU; result = Base6Directions.Direction.Up; }
    //         if (dD > max) { max = dD; result = Base6Directions.Direction.Down; }
    //         if (dL > max) { max = dL; result = Base6Directions.Direction.Left; }
    //         if (dR > max) { result = Base6Directions.Direction.Right; }
    //         return result;
    //     }
    //     #endregion

    //     #region Tool Offsets
    //     private void CalculateToolOffsets()
    //     {
    //         if (shipController == null) return;
    //         Vector3D ctrlPos = shipController.GetPosition();
    //         connectorOffset = Vector3D.Zero;
    //         if (connector != null)
    //         {
    //             Vector3D connPos = connector.GetPosition();
    //             MatrixD connMat = connector.WorldMatrix;
    //             Vector3D face = connMat.Backward;
    //             Vector3D ctrlToConn;
    //             Vector3D.Subtract(ref connPos, ref ctrlPos, out ctrlToConn);
    //             Vector3D frontOff;
    //             Vector3D.Multiply(ref face, 0.5, out frontOff);
    //             Vector3D.Add(ref ctrlToConn, ref frontOff, out connectorOffset);
    //         }

    //         weldOffsets.Clear();
    //         if (welder != null)
    //         {
    //             var welderDef = welder.SlimBlock?.BlockDefinition as MyShipWelderDefinition;
    //             if (welderDef != null)
    //                 CalculateToolActionOffsets(welder.GetPosition(), welder.WorldMatrix,
    //                     welderDef.SensorOffset, welderDef.SensorRadius, ref ctrlPos, weldOffsets);
    //         }

    //         grindOffsets.Clear();
    //         if (grinder != null)
    //         {
    //             var grinderDef = grinder.SlimBlock?.BlockDefinition as MyShipGrinderDefinition;
    //             if (grinderDef != null)
    //                 CalculateToolActionOffsets(grinder.GetPosition(), grinder.WorldMatrix,
    //                     grinderDef.SensorOffset, grinderDef.SensorRadius, ref ctrlPos, grindOffsets);
    //         }
    //     }

    //     private void CalculateToolActionOffsets(Vector3D toolPos, MatrixD toolMatrix, float sensorOffsetLocal,
    //         float sensorRadius, ref Vector3D controllerPos, List<ToolOffset> outputOffsets)
    //     {
    //         Vector3D toolFwd = toolMatrix.Forward;
    //         Vector3D sensorOffWorld;
    //         Vector3D.Multiply(ref toolFwd, (double)sensorOffsetLocal, out sensorOffWorld);
    //         Vector3D sphereCenter;
    //         Vector3D.Add(ref toolPos, ref sensorOffWorld, out sphereCenter);
    //         Vector3D toTool;
    //         Vector3D.Subtract(ref toolPos, ref sphereCenter, out toTool);
    //         double toToolLen = toTool.Length();
    //         if (toToolLen < 0.01) toTool = toolMatrix.Forward;
    //         else Vector3D.Divide(ref toTool, toToolLen, out toTool);

    //         Vector3D zAxis = toTool;
    //         Vector3D xAxis = Vector3D.CalculatePerpendicularVector(zAxis);
    //         Vector3D.Normalize(ref xAxis, out xAxis);
    //         Vector3D yAxis;
    //         Vector3D.Cross(ref zAxis, ref xAxis, out yAxis);

    //         Vector3D[] edgeDirs = {
    //             Vector3D.Negate(zAxis), Vector3D.Negate(xAxis), xAxis, Vector3D.Negate(yAxis), yAxis
    //         };
    //         Base6Directions.Direction[] approachDirs = {
    //             Base6Directions.Direction.Backward, Base6Directions.Direction.Left,
    //             Base6Directions.Direction.Right, Base6Directions.Direction.Down, Base6Directions.Direction.Up
    //         };

    //         for (int i = 0; i < 5; i++)
    //         {
    //             Vector3D edgeDir = edgeDirs[i];
    //             Vector3D radiusVec;
    //             Vector3D.Multiply(ref edgeDir, (double)sensorRadius, out radiusVec);
    //             Vector3D edgePoint;
    //             Vector3D.Add(ref sphereCenter, ref radiusVec, out edgePoint);
    //             Vector3D ctrlToEdge;
    //             Vector3D.Subtract(ref edgePoint, ref controllerPos, out ctrlToEdge);
    //             Vector3D offset;
    //             Vector3D.Negate(ref ctrlToEdge, out offset);
    //             outputOffsets.Add(new ToolOffset { Offset = offset, ApproachDirection = approachDirs[i] });
    //         }
    //     }
    //     #endregion

    //     #region State helpers
    //     private bool AnyLandingGearLocked()
    //     {
    //         foreach (var landingGear in landingGears)
    //             if (landingGear.IsLocked) return true;
    //         return false;
    //     }
    //     private bool AnyConnectorsConnected()
    //     {
    //         foreach (var connector in connectors)
    //             if (connector.IsConnected) return true;
    //         return false;
    //     }
    //     #endregion

    //     #region Static Helpers
    //     private static bool HasAnyGyros(Dictionary<Base6Directions.Direction, List<IMyGyro>> gyroscopes)
    //     {
    //         foreach (var kvp in gyroscopes)
    //             if (kvp.Value.Count > 0) return true;
    //         return false;
    //     }

    //     private static bool HasAnyThrusters(Dictionary<Base6Directions.Direction, List<IMyThrust>> thrusters)
    //     {
    //         if (thrusters.Count == 0) return false;
    //         foreach (var kvp in thrusters)
    //             if (kvp.Value.Count > 0) return true;
    //         return false;
    //     }
    //     private static float CalculateMaxLoadInG(
    //         ThrustProfile baseThrustProfile,
    //         ThrustProfile h2ThrustProfile,
    //         float mass,
    //         Base6Directions.Direction direction = Base6Directions.Direction.Up,
    //         float gravityNormal = 9.81f
    //     )
    //     {
    //         float upAccel;
    //         switch (direction)
    //         {
    //             case Base6Directions.Direction.Up:
    //                 upAccel = baseThrustProfile.MaxAccelUp + h2ThrustProfile.MaxAccelUp;
    //                 break;
    //             case Base6Directions.Direction.Down:
    //                 upAccel = baseThrustProfile.MaxAccelDown + h2ThrustProfile.MaxAccelDown;
    //                 break;
    //             case Base6Directions.Direction.Left:
    //                 upAccel = baseThrustProfile.MaxAccelLeft + h2ThrustProfile.MaxAccelLeft;
    //                 break;
    //             case Base6Directions.Direction.Right:
    //                 upAccel = baseThrustProfile.MaxAccelRight + h2ThrustProfile.MaxAccelRight;
    //                 break;
    //             case Base6Directions.Direction.Backward:
    //                 upAccel = baseThrustProfile.MaxAccelBackward + h2ThrustProfile.MaxAccelBackward;
    //                 break;
    //             case Base6Directions.Direction.Forward:
    //                 upAccel = baseThrustProfile.MaxAccelForward + h2ThrustProfile.MaxAccelForward;
    //                 break;
    //             default: // up
    //                 upAccel = baseThrustProfile.MaxAccelUp + h2ThrustProfile.MaxAccelUp;
    //                 break;
    //         }
    //         if (upAccel <= gravityNormal) return 0f;
    //         return ((upAccel - gravityNormal) * mass) / gravityNormal;
    //     }

    //     // Table dimensions: [ControllerForward, BlockForward]
    //     private static readonly Base6Directions.Direction[,] RelativeDirectionMatrix = new Base6Directions.Direction[6, 6]
    //     {
    //         // Controller = Forward
    //         { Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
    //         // Controller = Backward
    //         { Base6Directions.Direction.Backward, Base6Directions.Direction.Forward, Base6Directions.Direction.Right, Base6Directions.Direction.Left, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
    //         // Controller = Left
    //         { Base6Directions.Direction.Right, Base6Directions.Direction.Left, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
    //         // Controller = Right
    //         { Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Backward, Base6Directions.Direction.Forward, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
    //         // Controller = Up
    //         { Base6Directions.Direction.Down, Base6Directions.Direction.Up, Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward },
    //         // Controller = Down
    //         { Base6Directions.Direction.Up, Base6Directions.Direction.Down, Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Backward, Base6Directions.Direction.Forward }
    //     };

    //     private static Base6Directions.Direction GetRelativeDirection(
    //         Base6Directions.Direction blockForward,
    //         Base6Directions.Direction controllerForward)
    //     {
    //         return RelativeDirectionMatrix[(int)controllerForward, (int)blockForward];
    //     }
    //     private static ThrustProfile CalculateThrustProfile(IMyShipController shipController, float currentMass, Dictionary<Base6Directions.Direction, List<IMyThrust>> thrusters)
    //     {
    //         ThrustProfile thrustProfile = new ThrustProfile();

    //         if (shipController == null) return thrustProfile;
    //         if (currentMass < 0.1f) return thrustProfile;
    //         if (thrusters == null || thrusters.Count == 0) return thrustProfile;

    //         foreach (var kvp in thrusters)
    //         {
    //             float dirThrust = 0f;
    //             for (int i = 0; i < kvp.Value.Count; i++)
    //             {
    //                 IMyThrust t = kvp.Value[i];
    //                 if (t.IsWorking && t.IsFunctional) dirThrust += t.MaxEffectiveThrust;
    //             }
    //             switch (kvp.Key)
    //             {
    //                 case Base6Directions.Direction.Forward: thrustProfile.MaxThrustForward = dirThrust; break;
    //                 case Base6Directions.Direction.Backward: thrustProfile.MaxThrustBackward = dirThrust; break;
    //                 case Base6Directions.Direction.Up: thrustProfile.MaxThrustUp = dirThrust; break;
    //                 case Base6Directions.Direction.Down: thrustProfile.MaxThrustDown = dirThrust; break;
    //                 case Base6Directions.Direction.Left: thrustProfile.MaxThrustLeft = dirThrust; break;
    //                 case Base6Directions.Direction.Right: thrustProfile.MaxThrustRight = dirThrust; break;
    //             }
    //         }
    //         var massInfo = shipController.CalculateShipMass();
    //         currentMass = massInfo.TotalMass;
    //         if (currentMass < 0.1f)
    //         {
    //             thrustProfile.Valid = false;
    //             return thrustProfile;
    //         }

    //         thrustProfile.MaxAccelForward = thrustProfile.MaxThrustForward / currentMass;
    //         thrustProfile.MaxAccelBackward = thrustProfile.MaxThrustBackward / currentMass;
    //         thrustProfile.MaxAccelUp = thrustProfile.MaxThrustUp / currentMass;
    //         thrustProfile.MaxAccelDown = thrustProfile.MaxThrustDown / currentMass;
    //         thrustProfile.MaxAccelLeft = thrustProfile.MaxThrustLeft / currentMass;
    //         thrustProfile.MaxAccelRight = thrustProfile.MaxThrustRight / currentMass;
    //         thrustProfile.Valid = true;
    //         return thrustProfile;
    //     }
    //     #endregion

    //     #region Public Interface
    //     public void ResetDrone()
    //     {
    //         currentTask = null;
    //         taskQueue.Clear();
    //         currentPath.Clear();
    //         if (welder?.Enabled == true) welder.Enabled = false;
    //         if (grinder?.Enabled == true) grinder.Enabled = false;
    //         ClearAllThrustOverrides();
    //         DisableGyroscopeOverride();
    //         _consecutiveErrors = 0;
    //         currentState = Drone.State.Standby;
    //     }

    //     public void SetHomeOrientationToCurrent()
    //     {
    //         if (shipController == null) return;
    //         settings.HomePosition = shipController.GetPosition();
    //         settings.HomeForwardDirection = shipController.WorldMatrix.Forward;
    //     }

    //     public string GetDiagnostics()
    //     {
    //         if (!initialized) return "Drone not initialized";
    //         return string.Format("State: {0}, Caps: {1}, H2: {2:F1}%, Bat: {3:F1}%",
    //             currentState, capabilities, currentH2Level, currentBatteryLevel);
    //     }

    //     public bool CanRunDebugFlight(out string reason)
    //     {
    //         reason = null;
    //         if (!initialized || shipController == null) { reason = "Not initialized"; return false; }
    //         CheckCapabilities();
    //         bool hasThrusters = false;
    //         foreach (var kvp in thrusters)
    //             for (int i = 0; i < kvp.Value.Count; i++)
    //                 if (kvp.Value[i] != null && kvp.Value[i].IsWorking && kvp.Value[i].IsFunctional)
    //                 { hasThrusters = true; break; }
    //         if (!hasThrusters) { reason = "No functional thrusters"; return false; }
    //         bool hasGyros = false;
    //         foreach (var kvp in gyroscopes)
    //             for (int i = 0; i < kvp.Value.Count; i++)
    //                 if (kvp.Value[i] != null && kvp.Value[i].IsWorking && kvp.Value[i].IsFunctional)
    //                 { hasGyros = true; break; }
    //         if (!hasGyros) { reason = "No functional gyroscopes"; return false; }
    //         Vector3D gravity = shipController.GetNaturalGravity();
    //         bool hasGravity = gravity.LengthSquared() > 0.1;
    //         if (hasGravity && !capabilities.HasFlag(Drone.Capabilities.CanFlyAtmosphere))
    //         { reason = "Atmospheric flight required"; return false; }
    //         if (!hasGravity && !capabilities.HasFlag(Drone.Capabilities.CanFlySpace))
    //         { reason = "Space flight required in zero-g"; return false; }
    //         return true;
    //     }

    //     public IMyShipController GetShipController() { return shipController; }
    //     public IMyRemoteControl GetRemoteControl() { return entity as IMyRemoteControl; }
    //     #endregion
    // }
}
