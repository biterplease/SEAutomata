using ImprovedAI.Config;
using ImprovedAI.Pathfinding;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using ImprovedAI.VirtualNetwork;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ImprovedAI
{
    /// <summary>
    /// Integrated drone controller that manages both the Space Engineers block lifecycle
    /// and the drone AI functionality
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RemoteControl), false,
        "ImprovedAISmallDroneController",
        "ImprovedAILargeDroneController")]
    public class IAIDroneControllerBlock : MyGameLogicComponent
    {
        #region Fields - Core References
        private long _entityId;
        private int _messageCounter = 0;
        private MessageQueue messaging;
        private IMyRemoteControl remoteControl;
        private IMyCubeBlock block;
        private IMyUtilitiesDelegate myUtilitiesDelegate;
        private IMySessionDelegate mySessionDelegate;
        #endregion

        #region Fields - Configuration
        private IAIDroneControllerSettings settings;
        public Drone.OperationMode operationMode = Drone.OperationMode.StandAlone;
        public Drone.Capabilities capabilities;
        #endregion

        #region Fields - State
        private Drone.State currentState = Drone.State.Initializing;
        private bool _initialized = false;
        private bool _componentInitialized = false;
        private int _initializationTicks = 0;
        private int _consecutiveErrors = 0;
        private const int INITIALIZATION_DELAY_TICKS = 180;
        #endregion

        #region Fields - Components
        private IMyShipController shipController;
        private IMyShipConnector connector;
        private IMyShipWelder welder;
        private IMyShipGrinder grinder;
        private IMyRadioAntenna primaryAntenna;
        private readonly List<IMySensorBlock> sensors = new List<IMySensorBlock>();
        private readonly Dictionary<Base6Directions.Direction, List<IMyGyro>> gyroscopes = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();
        private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> thrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
        private readonly List<IMyGasTank> hydrogenTanks = new List<IMyGasTank>();
        private readonly List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
        private readonly List<IMyCargoContainer> cargoContainers = new List<IMyCargoContainer>();
        #endregion

        #region Fields - Cached Data
        private ThrustData thrustData = new ThrustData();
        private float currentMass = 0f;
        private float maxLoad = 0f;
        private Vector3D gravityVector = Vector3D.Zero;
        //private List<RelayMessage<Scheduler.Task>> _carriedMessages;
        #endregion

        #region Fields - Task Management
        private Scheduler.Task currentTask;
        private Queue<Scheduler.Task> taskQueue = new Queue<Scheduler.Task>();
        private ushort currentTaskId = 0;
        private List<Message<IMessagePayload>> _messageCache = new List<Message<IMessagePayload>>();
        #endregion

        #region Fields - Update Tracking
        private long _lastUpdateFrame = 0;
        private long _lastComponentCheckFrame = 0;
        private long _lastStatusReportFrame = 0;
        private long _lastPowerCheckFrame = 0;
        #endregion

        #region Fields - Power Monitoring
        private bool needsHydrogenRefuel = false;
        private bool needsBatteryRecharge = false;
        private float lastH2Level = 100f;
        private float currentH2Level = 100f;
        private float lastBatteryLevel = 100f;
        private float currentBatteryLevel = 100f;
        #endregion

        #region Fields - Rotation Control
        private bool isRotating = false;
        private long rotationStartFrame = 0;
        private long rotationDurationFrames = 0;
        private Vector3D rotationTarget;
        private float orientationToleranceDegrees = 5.0f;
        private float totalGyroscopeTorque = 0f; // Total torque in Newton-meters
        private float shipMomentOfInertia = 0f; // Moment of inertia in kg⋅m²s
        #endregion

        #region Fields - Navigation
        private Vector3D taskPosition;
        private List<Vector3D> currentPath = new List<Vector3D>();
        // Request next when within 100m
        private const float WAYPOINT_LOOKAHEAD_DISTANCE = 100f;
        private PathfindingManager pathfindingManager;
        private Vector3D currentWaypoint;
        private struct ThrustProfile
        {
            public float MaxThrustForward;
            public float MaxThrustBackward;
            public float MaxThrustUp;
            public float MaxThrustDown;
            public float MaxThrustLeft;
            public float MaxThrustRight;

            public float MaxAccelerationForward;  // m/s²
            public float MaxAccelerationBackward;
            public float MaxAccelerationUp;
            public float MaxAccelerationDown;
            public float MaxAccelerationLeft;
            public float MaxAccelerationRight;
        }

        private ThrustProfile thrustProfile;
        private bool thrustProfileValid = false;
        private long lastThrustProfileUpdate = 0;
        private const int THRUST_PROFILE_UPDATE_INTERVAL = 600; // frames

        // Hover thrust state
        private float currentHoverThrustPercentage = 0f;
        private Base6Directions.Direction currentHoverDirection = Base6Directions.Direction.Up;
        #endregion

        #region Fields - Update Intervals
        private ServerConfig.DroneControllerBlockConfig config;
        private readonly int COMPONENT_CHECK_INTERVAL_TICKS = 600;
        private int STATUS_REPORT_INTERVAL_TICKS;
        private int POWER_CHECK_INTERVAL_TICKS;
        #endregion


        #region Fields - Tool Offsets
        private struct ToolOffset
        {
            public Vector3D Offset;
            public Base6Directions.Direction ApproachDirection; // Which direction this offset approaches from
        }

        private Vector3D connectorOffset;
        private List<ToolOffset> weldOffsets = new List<ToolOffset>();
        private List<ToolOffset> grindOffsets = new List<ToolOffset>();
        #endregion

        #region Lifecycle - Initialization

        public IAIDroneControllerBlock(
            IMyUtilitiesDelegate utilitiesDelegate = null,
            IMySessionDelegate sessionDelegate = null
        )
        {
            this.myUtilitiesDelegate = utilitiesDelegate ?? new MyUtilitiesDelegate();
            this.mySessionDelegate = sessionDelegate ?? new MySessionDelegate();
        }
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            _entityId = Entity.EntityId;
            block = (IMyCubeBlock)Entity;
            settings = new IAIDroneControllerSettings();
            settings.OperationMode = operationMode;

            config = IAISession.GetConfig().Drone;
            foreach (Base6Directions.Direction direction in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                gyroscopes[direction] = new List<IMyGyro>();
                thrusters[direction] = new List<IMyThrust>();
            }

            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            IAISession.Instance.RegisterDroneController(this);

            IAIDroneControllerTerminalControls.DoOnce(ModContext);
            remoteControl = (IMyRemoteControl)Entity;

            if (remoteControl.CubeGrid?.Physics == null)
                return; // ignore ghost/projected grids

            // rotations get updated every frame
            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            // general navigation (except rotations) are updated every 10th frame
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            // task reading and assignment, triggers every 100 frames but has a delay checker
            // in reality should be around 600-700 frames 
            NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;

        }
        #endregion

        #region Lifecycle - Update Methods
        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            try
            {
                if (!_componentInitialized)
                {
                    _initializationTicks++;
                    if (_initializationTicks >= INITIALIZATION_DELAY_TICKS)
                    {
                        InitializeComponent();
                    }
                    return;
                }

                if (!_initialized)
                {
                    InitializeDroneController();
                    return;
                }

                if (!settings.IsEnabled || currentState == Drone.State.Error)
                    return;

                // Main update logic handled in UpdateBeforeSimulation10
                UpdateRotation();
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneController block {0}: UpdateBeforeSimulation error: {1}", Entity.EntityId, ex);
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();

            try
            {
                if (!_componentInitialized || !_initialized)
                    return;

                var currentFrame = mySessionDelegate.GameplayFrameCounter;

                // Component check
                if (currentFrame - _lastComponentCheckFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
                {
                    _lastComponentCheckFrame = currentFrame;
                    if (!CheckCapabilities())
                    {
                        currentState = Drone.State.Error;
                        return;
                    }
                }

                // Power check
                if (settings.MonitorHydrogenLevels || settings.MonitorBatteryLevels)
                {
                    if (currentFrame - _lastPowerCheckFrame >= POWER_CHECK_INTERVAL_TICKS)
                    {
                        _lastPowerCheckFrame = currentFrame;
                        CheckPowerLevels();
                    }
                }

                // Status reporting
                if (operationMode == Drone.OperationMode.ManagedByScheduler)
                {
                    if (currentFrame - _lastStatusReportFrame >= STATUS_REPORT_INTERVAL_TICKS)
                    {
                        _lastStatusReportFrame = currentFrame;
                        SendStatusReport();
                    }
                }

                // Read task assignments
                if (operationMode == Drone.OperationMode.ManagedByScheduler)
                {
                    ReadTaskAssignments();
                }

                // Update AI state machine
                if (settings.IsEnabled)
                {
                    UpdateAI();
                }
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneController block {0}: UpdateBeforeSimulation10 error: {1}", Entity.EntityId, ex);
            }
        }

        public override void UpdateBeforeSimulation100()
        {
            base.UpdateBeforeSimulation100();

            try
            {
                if (!_componentInitialized)
                    return;

                SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneController block {0}: UpdateBeforeSimulation100 error: {1}", Entity.EntityId, ex);
            }
        }
        #endregion

        #region Initialization Logic
        private void InitializeComponent()
        {
            if (_componentInitialized)
                return;

            // Load configuration from block storage if available

            // Mark component as initialized
            _componentInitialized = true;

            // Register with session
            IAISession.Instance?.RegisterDroneController(this);

            Log.Verbose("ImprovedAI Drone controller component initialized: {0}", Entity.DisplayName);
        }

        private void InitializeDroneController()
        {
            if (_initialized)
                return;

            currentState = Drone.State.Initializing;

            // Initialize message queue reference
            messaging = IAISession.GetMessageQueue();

            // Initialize update intervals from config
            STATUS_REPORT_INTERVAL_TICKS = IAISession.GetConfig().MessageQueue.DroneMessageThrottlingTicks();
            POWER_CHECK_INTERVAL_TICKS = IAISession.GetConfig().Drone.PowerCheckIntervalTicks;

            shipController = Entity as IMyShipController;
            if (shipController == null)
            {
                Log.Error("DroneController {0} entity is not a ship controller", _entityId);
                currentState = Drone.State.Error;
                return;
            }

            if (!CheckCapabilities())
            {
                Log.Error("DroneController {0} failed capability check", _entityId);
                currentState = Drone.State.Error;
                return;
            }

            // Register antenna if available
            if (primaryAntenna != null)
            {
                messaging.RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
            }

            // Subscribe to messages if managed by scheduler
            if (operationMode == Drone.OperationMode.ManagedByScheduler)
            {
                messaging.Subscribe(_entityId, Channel.DRONE_TASK_ASSIGNMENT);
                SendDroneRegistration();
            }

            currentState = Drone.State.Standby;
            _initialized = true;

            Log.Info("DroneController {0} initialized in mode: {1}", _entityId, operationMode);

        }
        #endregion

        #region Configuration Management
        void SaveSettings()
        {
            try
            {
                if (block == null)
                    return;
                if (settings == null)
                    throw new NullReferenceException($"settings == null on drone controller {_entityId}; modInstance={IAISession.Instance != null}");
                if (MyAPIGateway.Utilities == null)
                    throw new NullReferenceException($"MyAPIGateway.Utilities == null; entId={_entityId}; modInstance={IAISession.Instance != null}");
                if (myUtilitiesDelegate == null)
                {
                    Log.Error("MyAPIGateway.Utilities not set as Drone controller block {0} delegate", _entityId);
                    return;
                }
                if (Entity.Storage == null)
                    Entity.Storage = new MyModStorageComponent();

                block.Storage.SetValue(IAISession.MOD_GUID, Convert.ToBase64String(myUtilitiesDelegate.SerializeToBinary(settings)));
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneController block {0}: SaveToModStorageComponent error: {1}", Entity.EntityId, ex);
            }
        }
        #endregion

        #region Capability Detection
        private bool CheckCapabilities()
        {
            var cubeBlock = Entity as IMyCubeBlock;
            if (cubeBlock?.CubeGrid == null) return false;

            // Clear collections
            gyroscopes.Clear();
            thrusters.Clear();
            sensors.Clear();
            hydrogenTanks.Clear();
            batteries.Clear();
            cargoContainers.Clear();

            // Initialize direction dictionaries
            foreach (Base6Directions.Direction direction in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                gyroscopes[direction].Clear();
                thrusters[direction].Clear();
            }

            connector = null;
            welder = null;
            grinder = null;
            primaryAntenna = null;

            var controllerMatrix = shipController.Orientation;

            // Scan connected grids
            var connectedGrids = new List<IMyCubeGrid>();

            var ggd = cubeBlock.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical);

            ggd.GetGrids(connectedGrids);

            foreach (var grid in connectedGrids)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var block in blocks)
                {
                    var fatBlock = block.FatBlock;
                    if (fatBlock == null || !fatBlock.IsFunctional) continue;

                    // Categorize blocks
                    if (fatBlock is IMyShipConnector && connector == null)
                        connector = (IMyShipConnector)fatBlock;
                    else if (fatBlock is IMyShipWelder && welder == null)
                        welder = (IMyShipWelder)fatBlock;
                    else if (fatBlock is IMyShipGrinder && grinder == null)
                        grinder = (IMyShipGrinder)fatBlock;
                    else if (fatBlock is IMyGyro)
                    {
                        var gyro = (IMyGyro)fatBlock;
                        var gyroBlock = gyro as IMyTerminalBlock;

                        // Get gyroscope's forward direction relative to controller
                        var relativeDirection = GetRelativeDirection(
                            gyroBlock.Orientation.Forward,
                            controllerMatrix.Forward,
                            controllerMatrix.Up,
                            controllerMatrix.Left);
                        gyroscopes[relativeDirection].Add(gyro);
                    }
                    else if (fatBlock is IMyThrust)
                    {
                        var thruster = (IMyThrust)fatBlock;
                        var thrusterBlock = thruster as IMyTerminalBlock;

                        // Thrusters produce thrust in their Forward direction
                        // We want to know which direction they push the ship (in controller's frame)
                        var relativeDirection = GetRelativeDirection(thrusterBlock.Orientation.Forward,
                            controllerMatrix.Forward,
                            controllerMatrix.Up,
                            controllerMatrix.Left);
                        thrusters[relativeDirection].Add(thruster);
                    }
                    else if (fatBlock is IMySensorBlock)
                        sensors.Add((IMySensorBlock)fatBlock);
                    else if (fatBlock is IMyGasTank)
                    {
                        var gasTank = (IMyGasTank)fatBlock;
                        if (gasTank.BlockDefinition.SubtypeName.Contains("Hydrogen"))
                            hydrogenTanks.Add(gasTank);
                    }
                    else if (fatBlock is IMyBatteryBlock)
                        batteries.Add((IMyBatteryBlock)fatBlock);
                    else if (fatBlock is IMyCargoContainer)
                        cargoContainers.Add((IMyCargoContainer)fatBlock);
                    else if (fatBlock is IMyRadioAntenna)
                    {
                        var antenna = (IMyRadioAntenna)fatBlock;
                        if (antenna.EnableBroadcasting && antenna.IsWorking)
                        {
                            if (primaryAntenna == null || antenna.Radius > primaryAntenna.Radius)
                                primaryAntenna = antenna;
                        }
                    }
                }
            }

            // Determine capabilities
            capabilities = Drone.Capabilities.None;

            if (welder != null)
                capabilities |= Drone.Capabilities.CanWeld;
            if (grinder != null)
                capabilities |= Drone.Capabilities.CanGrind;
            if (sensors.Count > 0)
                capabilities |= Drone.Capabilities.HasSensors;
            if (connector != null)
            {
                if (settings.AlwaysRefuelWhenDocked)
                    capabilities |= Drone.Capabilities.RefuelWhenDocked | Drone.Capabilities.RechargeWhenDocked;
            }

            var total = thrusters.Count;
            var atmos = 0;
            var ion = 0;
            var h2 = 0;
            // Check flight capabilities
            foreach (var kvp in thrusters)
            {
                if (kvp.Value.Count > 0)
                {
                    foreach (var thruster in kvp.Value)
                    {
                        if (thruster.BlockDefinition.SubtypeName.Contains("Hydrogen")) h2++;
                        if (thruster.BlockDefinition.SubtypeName.Contains("Ion")) ion++;
                        if (thruster.BlockDefinition.SubtypeName.Contains("Atmospheric")) atmos++;
                    }
                }
            }
            float h2Ratio = (float)h2 / total;
            float atmoRatio = (float)atmos / total;
            float ionRatio = (float)ion / total;

            if (h2Ratio > 0.5f)
                capabilities |= Drone.Capabilities.CanFlyAtmosphere | Drone.Capabilities.CanFlySpace;
            else if (atmoRatio > 0.5f)
                capabilities |= Drone.Capabilities.CanFlyAtmosphere;
            else if (ionRatio > 0.5f)
                capabilities |= Drone.Capabilities.CanFlySpace;

            // Validate minimum requirements
            bool hasMinimumComponents = gyroscopes.Count > 0 &&
                                       thrusters.Count > 0 &&
                                       connector != null;

            if (hasMinimumComponents)
            {
                CalculateThrustProfile();
                CalculateToolOffsets();
                return true;
            }

            Log.Warning("DroneController {0} missing required components", _entityId);
            return false;
        }

        /// <summary>
        /// Calculate offsets for connector, welders, and grinders relative to controller.
        /// When added to a target waypoint, these offsets position the controller so the tool
        /// reaches the exact target position.
        /// </summary>
        private void CalculateToolOffsets()
        {
            if (shipController == null)
            {
                Log.Warning("Drone {0}: Cannot calculate offsets without ship controller", _entityId);
                return;
            }

            Vector3D controllerPos = shipController.GetPosition();

            // === CONNECTOR OFFSET ===
            if (connector != null)
            {
                Vector3D connectorPos = connector.GetPosition();
                MatrixD connectorMatrix = connector.WorldMatrix;

                // Connectors dock backward, so we want to be 0.5m in front of the docking target
                // The connector face is at connectorMatrix.Backward
                Vector3D connectorFace = connectorMatrix.Backward;

                // Vector from controller to connector
                Vector3D controllerToConnector;
                Vector3D.Subtract(ref connectorPos, ref controllerPos, out controllerToConnector);

                // Add 0.5m offset in front of connector face
                Vector3D frontOffset;
                Vector3D.Multiply(ref connectorFace, 0.5, out frontOffset);

                // Total offset: (controller to connector) + (0.5m in front)
                Vector3D.Add(ref controllerToConnector, ref frontOffset, out connectorOffset);

                Log.Verbose("Drone {0} connector offset: {1:F2}m", _entityId, connectorOffset.Length());
            }
            else
            {
                connectorOffset = Vector3D.Zero;
            }

            // === WELDER OFFSETS ===
            weldOffsets.Clear();
            if (welder != null)
            {
                var welderDef = welder.SlimBlock?.BlockDefinition as MyShipWelderDefinition;
                if (welderDef != null)
                {
                    CalculateToolActionOffsets(
                        welder.GetPosition(),
                        welder.WorldMatrix,
                        welderDef.SensorOffset,
                        welderDef.SensorRadius,
                        ref controllerPos,
                        weldOffsets);

                    Log.Verbose("Drone {0} calculated {1} weld offsets", _entityId, weldOffsets.Count);
                }
            }

            // === GRINDER OFFSETS ===
            grindOffsets.Clear();
            if (grinder != null)
            {
                var grinderDef = grinder.SlimBlock?.BlockDefinition as MyShipGrinderDefinition;
                if (grinderDef != null)
                {
                    CalculateToolActionOffsets(
                        grinder.GetPosition(),
                        grinder.WorldMatrix,
                        grinderDef.SensorOffset,
                        grinderDef.SensorRadius,
                        ref controllerPos,
                        grindOffsets);

                    Log.Verbose("Drone {0} calculated {1} grind offsets", _entityId, grindOffsets.Count);
                }
            }
        }

        /// <summary>
        /// Calculate the 5 approach offsets for a tool's action sphere
        /// </summary>
        private void CalculateToolActionOffsets(
            Vector3D toolPos,
            MatrixD toolMatrix,
            float sensorOffsetLocal,
            float sensorRadius,
            ref Vector3D controllerPos,
            List<ToolOffset> outputOffsets)
        {
            Vector3D toolForward = toolMatrix.Forward;
            Vector3D sensorOffsetWorld;
            Vector3D.Multiply(ref toolForward, (double)sensorOffsetLocal, out sensorOffsetWorld);

            // Calculate sphere center in world space
            Vector3D sphereCenter;
            Vector3D.Add(ref toolPos, ref sensorOffsetWorld, out sphereCenter);

            // Create coordinate system where +Z points from sphere center toward tool block center
            Vector3D toTool;
            Vector3D.Subtract(ref toolPos, ref sphereCenter, out toTool);
            double toToolLength = toTool.Length();

            if (toToolLength < 0.01)
            {
                // Sensor offset is zero, use tool's forward as reference
                toTool = toolMatrix.Forward;
            }
            else
            {
                Vector3D.Divide(ref toTool, toToolLength, out toTool);
            }

            // Create orthonormal basis
            Vector3D zAxis = toTool;
            Vector3D xAxis = Vector3D.CalculatePerpendicularVector(zAxis);
            Vector3D.Normalize(ref xAxis, out xAxis);
            Vector3D yAxis;
            Vector3D.Cross(ref zAxis, ref xAxis, out yAxis);

            // Define 5 approach directions: -Z, -X, +X, -Y, +Y
            Vector3D[] edgeDirections = new Vector3D[5];
            Base6Directions.Direction[] approachDirs = new Base6Directions.Direction[5];

            // -Z (back of sphere, away from tool)
            Vector3D.Negate(ref zAxis, out edgeDirections[0]);
            approachDirs[0] = Base6Directions.Direction.Backward;

            // -X (left of sphere)
            Vector3D.Negate(ref xAxis, out edgeDirections[1]);
            approachDirs[1] = Base6Directions.Direction.Left;

            // +X (right of sphere)
            edgeDirections[2] = xAxis;
            approachDirs[2] = Base6Directions.Direction.Right;

            // -Y (bottom of sphere)
            Vector3D.Negate(ref yAxis, out edgeDirections[3]);
            approachDirs[3] = Base6Directions.Direction.Down;

            // +Y (top of sphere)
            edgeDirections[4] = yAxis;
            approachDirs[4] = Base6Directions.Direction.Up;

            // Calculate offset for each edge point
            for (int i = 0; i < 5; i++)
            {
                // Edge point = sphere center + radius * direction
                Vector3D radiusVector;
                Vector3D.Multiply(ref edgeDirections[i], (double)sensorRadius, out radiusVector);

                Vector3D edgePoint;
                Vector3D.Add(ref sphereCenter, ref radiusVector, out edgePoint);

                // Offset = -(edge point - controller)
                // When added to target waypoint W, controller positions at W + offset,
                // which places the edge point exactly at W
                Vector3D controllerToEdge;
                Vector3D.Subtract(ref edgePoint, ref controllerPos, out controllerToEdge);

                Vector3D offset;
                Vector3D.Negate(ref controllerToEdge, out offset);

                outputOffsets.Add(new ToolOffset
                {
                    Offset = offset,
                    ApproachDirection = approachDirs[i]
                });
            }
        }


        /// <summary>
        /// Gets the forward direction of a block relative to the ship controller's orientation
        /// </summary>
        private Base6Directions.Direction GetRelativeDirection(
            Base6Directions.Direction blockForward,
            Base6Directions.Direction controllerForward,
            Base6Directions.Direction controllerUp,
            Base6Directions.Direction controllerLeft
            )
        {
            // Transform to be relative to the controller's orientation
            // We need to find what the block's forward maps to in the controller's reference frame

            // Get the transformation from controller space to grid space

            // Get the transformation from grid space to controller space
            // This is essentially asking: "if the controller's forward is actually grid's X direction,
            // and block's forward is grid's Y direction, then block's forward is controller's Up"

            // Use Base6Directions to transform
            var blockForwardVector = Base6Directions.GetIntVector(blockForward);
            var controllerForwardVector = Base6Directions.GetIntVector(controllerForward);
            var controllerLeftVector = Base6Directions.GetIntVector(controllerLeft);
            var controllerUpVector = Base6Directions.GetIntVector(controllerUp);

            // Transform the block's forward vector to controller's reference frame
            // Project the block's forward onto controller's axes
            int dotForward = Vector3I.Dot(blockForwardVector, controllerForwardVector);
            int dotLeft = Vector3I.Dot(blockForwardVector, controllerLeftVector);
            int dotUp = Vector3I.Dot(blockForwardVector, controllerUpVector);

            // Find which axis has the largest projection
            if (Math.Abs(dotForward) > Math.Abs(dotLeft) && Math.Abs(dotForward) > Math.Abs(dotUp))
            {
                return dotForward > 0 ? Base6Directions.Direction.Forward : Base6Directions.Direction.Backward;
            }
            else if (Math.Abs(dotLeft) > Math.Abs(dotUp))
            {
                return dotLeft > 0 ? Base6Directions.Direction.Left : Base6Directions.Direction.Right;
            }
            else
            {
                return dotUp > 0 ? Base6Directions.Direction.Up : Base6Directions.Direction.Down;
            }
        }
        #endregion



        #region Power Monitoring
        private void CheckPowerLevels()
        {
            // Check hydrogen
            if (settings.MonitorHydrogenLevels && hydrogenTanks.Count > 0)
            {
                float totalCapacity = 0f;
                float totalStored = 0f;

                foreach (var tank in hydrogenTanks.Where(t => t.IsWorking))
                {
                    totalCapacity += tank.Capacity;
                    totalStored += (float)(tank.Capacity * tank.FilledRatio);
                }

                currentH2Level = totalCapacity > 0 ? (totalStored / totalCapacity) * 100f : 0f;

                if (!needsHydrogenRefuel && currentH2Level <= settings.HydrogenRefuelThreshold)
                {
                    needsHydrogenRefuel = true;
                    Log.Info("Drone {0} H2 low: {1:F1}%", _entityId, currentH2Level);

                    if (currentState != Drone.State.RefuelingHydrogen && currentState != Drone.State.ReturningToBase)
                    {
                        currentState = Drone.State.ReturningToBase;
                    }
                }
                else if (needsHydrogenRefuel && currentH2Level >= settings.HydrogenOperationalThreshold)
                {
                    needsHydrogenRefuel = false;
                    Log.Info("Drone {0} H2 refueled: {1:F1}%", _entityId, currentH2Level);
                }
            }

            // Check battery
            if (settings.MonitorBatteryLevels && batteries.Count > 0)
            {
                float totalCapacity = 0f;
                float totalStored = 0f;

                foreach (var battery in batteries.Where(b => b.IsWorking))
                {
                    totalCapacity += battery.MaxStoredPower;
                    totalStored += battery.CurrentStoredPower;
                }

                currentBatteryLevel = totalCapacity > 0 ? (totalStored / totalCapacity) * 100f : 0f;

                if (!needsBatteryRecharge && currentBatteryLevel <= settings.BatteryRefuelThreshold)
                {
                    needsBatteryRecharge = true;
                    Log.Info("Drone {0} battery low: {1:F1}%", _entityId, currentBatteryLevel);

                    if (currentState != Drone.State.RechargingBattery && currentState != Drone.State.ReturningToBase)
                    {
                        currentState = Drone.State.ReturningToBase;
                    }
                }
                else if (needsBatteryRecharge && currentBatteryLevel >= settings.BatteryOperationalThreshold)
                {
                    needsBatteryRecharge = false;
                    Log.Info("Drone {0} battery recharged: {1:F1}%", _entityId, currentBatteryLevel);
                }
            }
        }
        #endregion

        #region AI State Machine
        private void UpdateAI()
        {
            switch (currentState)
            {
                case Drone.State.Initializing:
                    // Handled by Initialize()
                    break;

                case Drone.State.Standby:
                    HandleStandby();
                    break;

                case Drone.State.NavigatingToTarget:
                    HandleNavigating();
                    break;

                case Drone.State.Welding:
                    HandleWelding();
                    break;

                case Drone.State.Grinding:
                    HandleGrinding();
                    break;

                case Drone.State.ReturningToBase:
                    HandleReturningToBase();
                    break;

                case Drone.State.AligningToHome:
                    HandleAligningToHome();
                    break;

                case Drone.State.Docking:
                    HandleDocking();
                    break;

                case Drone.State.RefuelingHydrogen:
                case Drone.State.RechargingBattery:
                    HandleRefueling();
                    break;

                case Drone.State.Error:
                    HandleError();
                    break;
            }
        }

        private void HandleStandby()
        {
            // Check if we need refueling
            if (needsHydrogenRefuel || needsBatteryRecharge)
            {
                currentState = Drone.State.ReturningToBase;
            }
            // Check if we have tasks
            if (currentTask != null)
            {
                StartTask(currentTask);
            }
            else if (taskQueue.Count > 0)
            {
                currentTask = taskQueue.Dequeue();
                StartTask(currentTask);
            }

        }

        private void StartTask(Scheduler.Task task)
        {
            currentTaskId = (ushort)task.TaskId;
            taskPosition = task.Position;

            switch (task.TaskType)
            {
                case Scheduler.TaskType.PreciseWelding:
                case Scheduler.TaskType.ScanWeld:
                    currentState = Drone.State.NavigatingToTarget;
                    Log.LogDroneOrders("Drone {0} starting weld task at {1}", _entityId, task.Position);
                    break;

                case Scheduler.TaskType.PreciseGrinding:
                case Scheduler.TaskType.ScanGrind:
                    currentState = Drone.State.NavigatingToTarget;
                    Log.LogDroneOrders("Drone {0} starting grind task at {1}", _entityId, task.Position);
                    break;

                default:
                    Log.Warning("Drone {0} received unsupported task type: {1}", _entityId, task.TaskType);
                    CompleteCurrentTask();
                    break;
            }
        }

        private void HandleWelding()
        {
            if (welder == null)
            {
                Log.Error("Drone {0} cannot weld - no welder", _entityId);
                CompleteCurrentTask();
                return;
            }

            // Enable welder
            if (!welder.Enabled)
                welder.Enabled = true;

            // Simple welding logic - would need enhancement
            // Check if target is complete
            CompleteCurrentTask();
        }

        private void HandleGrinding()
        {
            if (grinder == null)
            {
                Log.Error("Drone {0} cannot grind - no grinder", _entityId);
                CompleteCurrentTask();
                return;
            }

            // Enable grinder
            if (!grinder.Enabled)
                grinder.Enabled = true;

            // Simple grinding logic - would need enhancement
            CompleteCurrentTask();
        }

        private void HandleReturningToBase()
        {
            if (connector == null)
            {
                currentState = Drone.State.Standby;
                return;
            }

            var connectorPos = connector.GetPosition();
            var currentPos = shipController.GetPosition();
            var distance = Vector3D.Distance(currentPos, connectorPos);

            if (distance < 10.0)
            {
                // Close enough to start orientation check
                if (settings.EnforceHomeOrientation)
                {
                    currentState = Drone.State.AligningToHome;
                    HandleAligningToHome();
                    return;
                }
                else
                {
                    currentState = Drone.State.Docking;
                }
            }
            else
            {
                MoveToPosition(connectorPos);
            }
        }

        private void HandleDocking()
        {
            if (connector == null)
            {
                currentState = Drone.State.Standby;
                return;
            }

            if (!connector.IsConnected)
            {
                // Attempt to connect
                connector.Connect();
            }

            if (connector.IsConnected)
            {
                if (needsHydrogenRefuel)
                    currentState = Drone.State.RefuelingHydrogen;
                else if (needsBatteryRecharge)
                    currentState = Drone.State.RechargingBattery;
                else
                    currentState = Drone.State.Standby;
            }
        }

        private void HandleAligningToHome()
        {
            // Calculate target position in home forward direction
            Vector3D currentPos = shipController.GetPosition();
            Vector3D homePos = settings.HomePosition;

            // Create a target point in the home forward direction
            Vector3D targetPoint;
            Vector3D homeForward = settings.HomeForwardDirection;
            Vector3D.Normalize(ref homeForward, out homeForward);

            // Target is 100m in the home forward direction from home position
            Vector3D offset;
            Vector3D.Multiply(ref homeForward, 100.0, out offset);
            Vector3D.Add(ref homePos, ref offset, out targetPoint);

            // Check if already aligned
            if (IsOrientedTowards(ref targetPoint))
            {
                // Aligned! Proceed to docking
                currentState = Drone.State.Docking;
                Log.Info("Drone {0} aligned to home orientation", _entityId);
            }
            else
            {
                // Initiate rotation if not already rotating
                if (!isRotating)
                {
                    bool rotationStarted = InitiateRotation(ref targetPoint);
                    if (!rotationStarted)
                    {
                        Log.Warning("Drone {0} failed to start home alignment rotation", _entityId);
                        // Proceed to docking anyway
                        currentState = Drone.State.Docking;
                    }
                }

                // Hold position while rotating
                MoveToPosition(currentPos); // Stay in place
            }
        }

        private void HandleRefueling()
        {
            // Check if refueling is complete
            if (!needsHydrogenRefuel && !needsBatteryRecharge)
            {
                currentState = Drone.State.Standby;
                Log.Info("Drone {0} refueling complete", _entityId);
            }
        }

        private void HandleError()
        {
            _consecutiveErrors++;

            if (_consecutiveErrors >= ServerConfig.Instance.SchedulerBounds.MaxConsecutiveErrors)
            {
                // Critical error - disable drone
                Log.Error("Drone {0} critical error - disabling", _entityId);
            }
        }

        private void CompleteCurrentTask()
        {
            if (currentTask != null && operationMode == Drone.OperationMode.ManagedByScheduler)
            {
                SendTaskCompleteReport();
            }

            currentTask = null;
            currentTaskId = 0;
            currentState = Drone.State.Standby;
        }

        private void MoveToPosition(Vector3D targetPos)
        {
            // Simple movement logic - would be replaced with proper pathfinding
            var currentPos = shipController.GetPosition();
            var direction = Vector3D.Normalize(targetPos - currentPos);

            // Apply thrust in direction
            foreach (var thruster in thrusters)
            {
                // Simplified thrust control
                //thruster.ThrustOverridePercentage = 0.5f;
            }
        }
        #endregion

        #region Message Handling
        private uint GenerateId()
        {
            return IdGenerator.GenerateId(ref _messageCounter, _entityId);
        }
        private void SendDroneRegistration()
        {
            var msg = new Message<DroneReport>
            {
                Payload = new DroneReport
                {
                    Flags = Drone.UpdateFlags.Registration,
                    DroneState = currentState,
                    Capabilities = capabilities,
                    BatteryChargePercent = currentBatteryLevel,
                    BatteryRechargeThreshold = settings.BatteryRefuelThreshold,
                    BatteryOperationalThreshold = settings.BatteryOperationalThreshold,
                    H2Level = currentH2Level,
                    H2RefuelThreshold = settings.HydrogenRefuelThreshold,
                    H2OperationalThreshold = settings.HydrogenOperationalThreshold
                },
                MessageId = GenerateId(),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                RecipientId = settings.managingScheduler,
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE,

            };

            var errorCode = messaging.SendDirectMessage(primaryAntenna, msg, false);
            if (errorCode != ErrorCode.None)
                HandleErrorCode(errorCode, msg);

            Log.LogDroneNetwork(LogLevel.Verbose, "Drone {0} sent registration", _entityId);
        }
        private void HandleErrorCode<T>(ErrorCode errorCode, Message<T> msg = null) where T : class, IMessagePayload
        {
            switch (errorCode)
            {
                case ErrorCode.None:
                    return;
                case ErrorCode.MessageIsNull:
                    Log.Error("null mesasge sent from Drone: {0}", _entityId);
                    break;
                case ErrorCode.InvalidChannel:
                    Log.Error("message to invalid channel {0} sent by drone {1}", msg?.Channel, _entityId);
                    break;
                case ErrorCode.ShutdownInProgress:
                    Log.Warning("drone {0} attempting to send message during mod shutdown", _entityId);
                    break;
                default:
                    Log.Verbose("drone {0} received message response {1}", _entityId, errorCode.ToString());
                    return;
            }
        }

        private void SendStatusReport()
        {
            var flags = Drone.UpdateFlags.None;

            // Determine what changed
            if (Math.Abs(currentH2Level - lastH2Level) > 0.05f)
            {
                flags |= Drone.UpdateFlags.H2Update;
                lastH2Level = currentH2Level;
            }

            if (Math.Abs(currentBatteryLevel - lastBatteryLevel) > 0.05f)
            {
                flags |= Drone.UpdateFlags.BatteryUpdate;
                lastBatteryLevel = currentBatteryLevel;
            }

            if (flags == Drone.UpdateFlags.None)
                return; // Nothing to report

            var msg = new Message<DroneReport>
            {
                Payload = new DroneReport
                {
                    Flags = flags,
                    DroneState = currentState,
                    BatteryChargePercent = currentBatteryLevel,
                    H2Level = currentH2Level
                },
                MessageId = GenerateId(),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                RecipientId = settings.managingScheduler,
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE,

            };

            var errorCode = messaging.SendDirectMessage(primaryAntenna, msg, false);
            HandleErrorCode(errorCode, msg);
            Log.LogDroneNetwork(LogLevel.Verbose, "Drone {0} sent task {1} status update", _entityId, msg.Payload.TaskId);
        }

        private void SendTaskCompleteReport()
        {
            var msg = new Message<DroneReport>
            {
                Payload = new DroneReport
                {
                    TaskId = currentTaskId,
                    BatteryChargePercent = currentBatteryLevel,
                    H2Level = currentH2Level,
                },
                MessageId = GenerateId(),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                RecipientId = settings.managingScheduler,
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE,

            };

            var errorCode = messaging.SendDirectMessage(primaryAntenna, msg, false);
            HandleErrorCode(errorCode, msg);
            Log.LogDroneNetwork(LogLevel.Verbose, "Drone {0} sent task {1} completion", _entityId, msg.Payload.TaskId);
        }

        private void ReadTaskAssignments()
        {

            messaging.ReadMessages(
                _entityId,
                primaryAntenna,
                Channel.DIRECT_MESSAGE,
                _messageCache,
                maxMessages: 10,
                clear: true,
                messageFilters: PayloadType.TaskAssignment);

            foreach (var message in _messageCache)
            {
                if (message.RecipientId != _entityId)
                {
                    Log.Warning("drone {0} recived a message intended for entity id {1} type {2}", _entityId, message.RecipientId, message.RecipientBlockType.ToString());
                    continue;
                }
                if (message.Payload is TaskAssignment)
                {
                    var payload = (TaskAssignment)message.Payload;
                    foreach (var task in payload.Tasks)
                    {
                        taskQueue.Enqueue(task);
                        Log.LogDroneOrders("Drone {0} received task {1} type {2}", _entityId, task.TaskId, task.TaskType);
                    }
                }
            }
        }

        //private void ReadMailmanForwards()
        //{
        //    // TODO: check if listeners in range
        //    // if no return
        //    // if yes, send messages
        //    var relayMessages = messaging.ReadMessages<RelayMessage<Scheduler.Task>>(_entityId, (ushort)Channel.MAILMAN_FORWARD, 10);

        //    foreach (var relayMessage in relayMessages)
        //    {
        //        if (relayMessage.DestinationEntityId != _entityId)
        //            continue;

        //        Scheduler.Task task = relayMessage.Payload;
        //        // send message

        //    }
        //}
        #endregion


        /// <summary>
        /// Gets the torque (force magnitude) of a gyroscope from its definition.
        /// </summary>
        /// <param name="gyro">The gyroscope block</param>
        /// <returns>Torque in Newton-meters</returns>
        private float GetGyroscopeTorque(IMyGyro gyro)
        {
            try
            {
                var def = gyro.SlimBlock?.BlockDefinition as MyGyroDefinition;
                if (def != null)
                {
                    return def.ForceMagnitude;
                }
                return 0f;
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to read gyroscope torque from definition: {0}", ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// Calibrates rotation capabilities based on current mass and gyroscope torque.
        /// Should be called when mass changes or gyroscopes are added/removed.
        /// </summary>
        private void CalibrateRotationCapabilities()
        {
            if (shipController == null)
                return;

            // Calculate total gyroscope torque
            totalGyroscopeTorque = 0f;
            int functionalGyroCount = 0;

            foreach (var directionGroup in gyroscopes.Values)
            {
                foreach (var gyro in directionGroup)
                {
                    if (gyro.IsFunctional && gyro.Enabled)
                    {
                        float gyroTorque = GetGyroscopeTorque(gyro);
                        totalGyroscopeTorque += gyroTorque;
                        functionalGyroCount++;
                    }
                }
            }

            if (functionalGyroCount == 0)
            {
                Log.Warning("Drone {0} has no functional gyroscopes for rotation", _entityId);
                totalGyroscopeTorque = 0f;
                shipMomentOfInertia = 0f;
                return;
            }

            // Calculate ship's moment of inertia
            var mass = shipController.CalculateShipMass();
            currentMass = mass.TotalMass;

            // Get the ship's bounding box for inertia calculation
            var boundingBox = shipController.CubeGrid.LocalAABB;
            Vector3 size;
            Vector3.Subtract(ref boundingBox.Max, ref boundingBox.Min, out size);

            // Calculate moment of inertia using rectangular prism approximation
            float gridSize = shipController.CubeGrid.GridSize;
            float width = size.X * gridSize;
            float height = size.Y * gridSize;
            float depth = size.Z * gridSize;

            // Calculate squared dimensions once
            float widthSq = width * width;
            float heightSq = height * height;
            float depthSq = depth * depth;

            // I = (1/12) × m × (sum of two perpendicular dimensions squared)
            float oneOverTwelve = 1f / 12f;
            float massOverTwelve = currentMass * oneOverTwelve;

            float Ix = massOverTwelve * (heightSq + depthSq);
            float Iy = massOverTwelve * (widthSq + depthSq);
            float Iz = massOverTwelve * (widthSq + heightSq);

            // Use maximum (worst case) for rotation planning
            shipMomentOfInertia = Math.Max(Math.Max(Ix, Iy), Iz);

            Log.Info("Drone {0} rotation calibrated: Torque={1:F0} Nm, Inertia={2:F0} kg⋅m², Mass={3:F0} kg, Gyros={4}",
                _entityId, totalGyroscopeTorque, shipMomentOfInertia, currentMass, functionalGyroCount);
        }

        /// <summary>
        /// Checks if the drone is oriented towards the target within tolerance.
        /// </summary>
        /// <param name="targetPosition">Target position in world space</param>
        /// <returns>True if within tolerance</returns>
        private bool IsOrientedTowards(ref Vector3D targetPosition)
        {
            if (shipController == null)
                return false;

            Vector3D currentPosition = shipController.GetPosition();
            Vector3D directionToTarget;
            Vector3D.Subtract(ref targetPosition, ref currentPosition, out directionToTarget);
            Vector3D.Normalize(ref directionToTarget, out directionToTarget);

            MatrixD worldMatrix = shipController.WorldMatrix;
            Vector3D currentForward = worldMatrix.Forward;

            double dotProduct;
            Vector3D.Dot(ref currentForward, ref directionToTarget, out dotProduct);
            dotProduct = MathHelper.Clamp(dotProduct, -1.0, 1.0);

            double angle = Math.Acos(dotProduct);
            double angleDegrees = angle * (180.0 / Math.PI);

            return angleDegrees <= orientationToleranceDegrees;
        }
        /// <summary>
        /// Initiates rotation towards a target position.
        /// Calculates rotation time based on gyroscope torque and ship inertia.
        /// </summary>
        private bool InitiateRotation(ref Vector3D targetPosition)
        {
            if (isRotating)
            {
                Log.Warning("Drone {0} already rotating", _entityId);
                return false;
            }

            // Check if already aligned
            if (IsOrientedTowards(ref targetPosition))
            {
                Log.Verbose("Drone {0} already aligned to target", _entityId);
                return false;
            }

            if (totalGyroscopeTorque <= 0 || shipMomentOfInertia <= 0)
            {
                Log.Warning("Drone {0} rotation not calibrated", _entityId);
                CalibrateRotationCapabilities();

                if (totalGyroscopeTorque <= 0)
                {
                    Log.Error("Drone {0} has no functional gyroscopes", _entityId);
                    return false;
                }
            }

            // Cache controller data
            Vector3D currentPosition = shipController.GetPosition();
            MatrixD worldMatrix = shipController.WorldMatrix;
            Vector3D currentForward = worldMatrix.Forward;

            // Calculate direction to target
            Vector3D directionToTarget;
            Vector3D.Subtract(ref targetPosition, ref currentPosition, out directionToTarget);
            Vector3D.Normalize(ref directionToTarget, out directionToTarget);

            // Calculate angle to rotate (in radians)
            double dotProduct;
            Vector3D.Dot(ref currentForward, ref directionToTarget, out dotProduct);
            dotProduct = MathHelper.Clamp(dotProduct, -1.0, 1.0);
            double angleRadians = Math.Acos(dotProduct);

            // Physics calculation: τ = I × α → α = τ / I
            double angularAcceleration = totalGyroscopeTorque / shipMomentOfInertia;

            // Kinematic equation: θ = 0.5 × α × t² → t = sqrt(2 × θ / α)
            double rotationTimeSeconds = Math.Sqrt(2.0 * angleRadians / angularAcceleration);

            // Add safety margin (30%)
            rotationTimeSeconds *= 1.3;

            // Convert to frames (60 ticks per second)
            rotationDurationFrames = (long)(rotationTimeSeconds * 60.0);

            // Enforce minimum rotation time
            if (rotationDurationFrames < 10)
                rotationDurationFrames = 10;

            // Cap maximum rotation time
            if (rotationDurationFrames > 600)
            {
                Log.Warning("Drone {0} calculated rotation time too long ({1}s), capping at 10s",
                    _entityId, rotationTimeSeconds);
                rotationDurationFrames = 600;
            }

            // Store rotation state
            isRotating = true;
            rotationStartFrame = mySessionDelegate.GameplayFrameCounter;
            rotationTarget = targetPosition;

            // Apply gyroscope override
            OrientTowardsTarget(ref targetPosition);

            double angleDegrees = angleRadians * (180.0 / Math.PI);
            Log.Verbose("Drone {0} rotating {1:F1}° over {2:F2}s ({3} frames) [α={4:F2} rad/s², I={5:F0}]",
                _entityId, angleDegrees, rotationTimeSeconds, rotationDurationFrames,
                angularAcceleration, shipMomentOfInertia);

            return true;
        }

        /// <summary>
        /// Updates rotation state. Call this in UpdateBeforeSimulation() or UpdateBeforeSimulation10().
        /// </summary>
        private void UpdateRotation()
        {
            if (!isRotating)
                return;

            long currentFrame = mySessionDelegate.GameplayFrameCounter;
            long elapsedFrames = currentFrame - rotationStartFrame;

            // Check if rotation duration has elapsed
            if (elapsedFrames >= rotationDurationFrames)
            {
                // Rotation time complete - disable overrides
                DisableGyroscopeOverride();
                isRotating = false;

                // Check if we're actually aligned
                if (IsOrientedTowards(ref rotationTarget))
                {
                    Log.Verbose("Drone {0} rotation complete and aligned", _entityId);
                }
                else
                {
                    // Calculate remaining misalignment
                    Vector3D currentPosition = shipController.GetPosition();
                    Vector3D directionToTarget;
                    Vector3D.Subtract(ref rotationTarget, ref currentPosition, out directionToTarget);
                    Vector3D.Normalize(ref directionToTarget, out directionToTarget);

                    MatrixD worldMatrix = shipController.WorldMatrix;
                    Vector3D currentForward = worldMatrix.Forward;

                    double dotProduct;
                    Vector3D.Dot(ref currentForward, ref directionToTarget, out dotProduct);
                    dotProduct = MathHelper.Clamp(dotProduct, -1.0, 1.0);

                    double remainingAngle = Math.Acos(dotProduct);
                    double remainingAngleDegrees = remainingAngle * (180.0 / Math.PI);

                    Log.Verbose("Drone {0} rotation complete but misaligned by {1:F1}°, initiating correction",
                        _entityId, remainingAngleDegrees);

                    // Initiate correction rotation if error is significant
                    double correctionThreshold = orientationToleranceDegrees * 0.5;
                    if (remainingAngleDegrees > correctionThreshold)
                    {
                        InitiateRotation(ref rotationTarget);
                    }
                }
            }
        }

        /// <summary>
        /// Cancels any active rotation and disables gyroscope overrides.
        /// </summary>
        private void CancelRotation()
        {
            if (isRotating)
            {
                DisableGyroscopeOverride();
                isRotating = false;
                Log.Verbose("Drone {0} rotation cancelled", _entityId);
            }
        }

        /// <summary>
        /// Orients the drone towards a target position using gyroscope override.
        /// This is called by InitiateRotation and should not be called directly for timed rotations.
        /// </summary>
        private void OrientTowardsTarget(ref Vector3D targetPosition)
        {
            if (gyroscopes.Count == 0 || shipController == null)
                return;

            // Cache controller data
            Vector3D currentPosition = shipController.GetPosition();
            MatrixD worldMatrix = shipController.WorldMatrix;
            Vector3D currentForward = worldMatrix.Forward;
            Vector3D currentUp = worldMatrix.Up;
            Vector3D currentLeft = worldMatrix.Left;

            // Calculate direction to target
            Vector3D directionToTarget;
            Vector3D.Subtract(ref targetPosition, ref currentPosition, out directionToTarget);
            Vector3D.Normalize(ref directionToTarget, out directionToTarget);

            Vector3D desiredForward = directionToTarget;
            Vector3D desiredUp;
            Vector3D desiredLeft;

            // Handle gravity alignment if enabled
            double gravityLengthSq = gravityVector.LengthSquared();
            if (settings.AlignToPGravity && gravityLengthSq > 0.1)
            {
                // Calculate anti-gravity direction
                Vector3D antiGravity;
                Vector3D.Negate(ref gravityVector, out antiGravity);
                Vector3D.Normalize(ref antiGravity, out antiGravity);

                // Calculate desired up perpendicular to forward
                double forwardDotAntiGrav;
                Vector3D.Dot(ref desiredForward, ref antiGravity, out forwardDotAntiGrav);

                Vector3D antiGravComponent;
                Vector3D.Multiply(ref antiGravity, forwardDotAntiGrav, out antiGravComponent);
                Vector3D.Subtract(ref antiGravity, ref antiGravComponent, out desiredUp);

                double desiredUpLengthSq = desiredUp.LengthSquared();
                if (desiredUpLengthSq < 0.01)
                {
                    desiredUp = currentUp;
                }
                else
                {
                    Vector3D.Normalize(ref desiredUp, out desiredUp);
                }

                // Apply pitch limit
                double forwardDotAntiGravForPitch;
                Vector3D.Dot(ref desiredForward, ref antiGravity, out forwardDotAntiGravForPitch);
                forwardDotAntiGravForPitch = MathHelper.Clamp(forwardDotAntiGravForPitch, -1.0, 1.0);
                double desiredPitch = Math.Asin(forwardDotAntiGravForPitch) * (180.0 / Math.PI);

                if (Math.Abs(desiredPitch) > settings.PGravityAlignMaxPitchDegrees)
                {
                    double maxPitchRadians = settings.PGravityAlignMaxPitchDegrees * (Math.PI / 180.0);
                    double pitchSign = Math.Sign(desiredPitch);

                    // Calculate horizontal forward
                    Vector3D horizontalForward;
                    Vector3D antiGravComp;
                    Vector3D.Dot(ref desiredForward, ref antiGravity, out forwardDotAntiGrav);
                    Vector3D.Multiply(ref antiGravity, forwardDotAntiGrav, out antiGravComp);
                    Vector3D.Subtract(ref desiredForward, ref antiGravComp, out horizontalForward);

                    double horizLengthSq = horizontalForward.LengthSquared();
                    if (horizLengthSq > 0.01)
                    {
                        Vector3D.Normalize(ref horizontalForward, out horizontalForward);

                        double cosPitch = Math.Cos(maxPitchRadians);
                        double sinPitch = Math.Sin(maxPitchRadians) * pitchSign;

                        Vector3D horizComponent, antiGravPitchComponent;
                        Vector3D.Multiply(ref horizontalForward, cosPitch, out horizComponent);
                        Vector3D.Multiply(ref antiGravity, sinPitch, out antiGravPitchComponent);
                        Vector3D.Add(ref horizComponent, ref antiGravPitchComponent, out desiredForward);
                        Vector3D.Normalize(ref desiredForward, out desiredForward);
                    }
                }

                // Recalculate up
                Vector3D.Dot(ref desiredForward, ref antiGravity, out forwardDotAntiGrav);
                Vector3D.Multiply(ref antiGravity, forwardDotAntiGrav, out antiGravComponent);
                Vector3D.Subtract(ref antiGravity, ref antiGravComponent, out desiredUp);

                desiredUpLengthSq = desiredUp.LengthSquared();
                if (desiredUpLengthSq > 0.01)
                {
                    Vector3D.Normalize(ref desiredUp, out desiredUp);
                }
                else
                {
                    desiredUp = currentUp;
                }

                // Apply roll limit
                Vector3D.Cross(ref desiredUp, ref desiredForward, out desiredLeft);

                Vector3D currentRollAxis;
                Vector3D.Cross(ref antiGravity, ref currentForward, out currentRollAxis);
                double rollAxisLengthSq = currentRollAxis.LengthSquared();

                if (rollAxisLengthSq > 0.01)
                {
                    Vector3D.Normalize(ref currentRollAxis, out currentRollAxis);

                    Vector3D desiredRollAxis;
                    Vector3D.Cross(ref antiGravity, ref desiredForward, out desiredRollAxis);
                    double desiredRollAxisLengthSq = desiredRollAxis.LengthSquared();

                    if (desiredRollAxisLengthSq > 0.01)
                    {
                        Vector3D.Normalize(ref desiredRollAxis, out desiredRollAxis);

                        double rollDot;
                        Vector3D.Dot(ref currentRollAxis, ref desiredRollAxis, out rollDot);
                        rollDot = MathHelper.Clamp(rollDot, -1.0, 1.0);
                        double rollAngle = Math.Acos(rollDot) * (180.0 / Math.PI);

                        if (rollAngle > settings.PGravityAlignMaxRollDegrees)
                        {
                            double maxRollRadians = settings.PGravityAlignMaxRollDegrees * (Math.PI / 180.0);
                            double cosRoll = Math.Cos(maxRollRadians);
                            double sinRoll = Math.Sin(maxRollRadians);

                            Vector3D rollComponent1, rollComponent2, crossProduct;
                            Vector3D.Multiply(ref currentRollAxis, cosRoll, out rollComponent1);
                            Vector3D.Cross(ref desiredForward, ref currentRollAxis, out crossProduct);
                            Vector3D.Multiply(ref crossProduct, sinRoll, out rollComponent2);
                            Vector3D.Add(ref rollComponent1, ref rollComponent2, out desiredLeft);
                            Vector3D.Normalize(ref desiredLeft, out desiredLeft);

                            Vector3D.Cross(ref desiredForward, ref desiredLeft, out desiredUp);
                            Vector3D.Normalize(ref desiredUp, out desiredUp);
                        }
                    }
                }
            }
            else
            {
                // No gravity or gravity alignment disabled
                double forwardDotUp;
                Vector3D.Dot(ref desiredForward, ref currentUp, out forwardDotUp);

                Vector3D upComponent;
                Vector3D.Multiply(ref desiredForward, forwardDotUp, out upComponent);
                Vector3D.Subtract(ref currentUp, ref upComponent, out desiredUp);

                double desiredUpLengthSq = desiredUp.LengthSquared();
                if (desiredUpLengthSq < 0.01)
                {
                    desiredUp = Vector3D.CalculatePerpendicularVector(desiredForward);
                }
                else
                {
                    Vector3D.Normalize(ref desiredUp, out desiredUp);
                }
            }

            // Calculate desired left
            Vector3D.Cross(ref desiredUp, ref desiredForward, out desiredLeft);
            Vector3D.Normalize(ref desiredLeft, out desiredLeft);

            // Calculate rotation error
            const double ROTATION_GAIN = 2.0;

            Vector3D forwardError, upError;
            Vector3D.Cross(ref currentForward, ref desiredForward, out forwardError);
            Vector3D.Cross(ref currentUp, ref desiredUp, out upError);

            Vector3D upErrorScaled;
            Vector3D.Multiply(ref upError, 0.5, out upErrorScaled);

            Vector3D totalError;
            Vector3D.Add(ref forwardError, ref upErrorScaled, out totalError);

            // Convert to pitch, yaw, roll
            double pitchInput, yawInput, rollInput;
            Vector3D.Dot(ref totalError, ref currentLeft, out pitchInput);
            Vector3D.Dot(ref totalError, ref currentUp, out yawInput);
            Vector3D.Dot(ref totalError, ref currentForward, out rollInput);

            pitchInput *= ROTATION_GAIN;
            yawInput *= ROTATION_GAIN;
            rollInput *= ROTATION_GAIN;

            pitchInput = MathHelper.Clamp(pitchInput, -1.0, 1.0);
            yawInput = MathHelper.Clamp(yawInput, -1.0, 1.0);
            rollInput = MathHelper.Clamp(rollInput, -1.0, 1.0);

            ApplyGyroscopeOverride(pitchInput, yawInput, rollInput);
        }

        /// <summary>
        /// Applies pitch, yaw, roll override to gyroscopes based on their orientation.
        /// </summary>
        private void ApplyGyroscopeOverride(double pitch, double yaw, double roll)
        {
            foreach (var directionGroup in gyroscopes)
            {
                var direction = directionGroup.Key;
                var gyroList = directionGroup.Value;

                if (gyroList.Count == 0)
                    continue;

                float gyroPitch = 0f;
                float gyroYaw = 0f;
                float gyroRoll = 0f;

                switch (direction)
                {
                    case Base6Directions.Direction.Forward:
                        gyroPitch = (float)pitch;
                        gyroYaw = (float)yaw;
                        gyroRoll = (float)roll;
                        break;
                    case Base6Directions.Direction.Backward:
                        gyroPitch = -(float)pitch;
                        gyroYaw = -(float)yaw;
                        gyroRoll = (float)roll;
                        break;
                    case Base6Directions.Direction.Left:
                        gyroPitch = (float)yaw;
                        gyroYaw = -(float)pitch;
                        gyroRoll = (float)roll;
                        break;
                    case Base6Directions.Direction.Right:
                        gyroPitch = -(float)yaw;
                        gyroYaw = (float)pitch;
                        gyroRoll = (float)roll;
                        break;
                    case Base6Directions.Direction.Up:
                        gyroPitch = (float)roll;
                        gyroYaw = (float)yaw;
                        gyroRoll = -(float)pitch;
                        break;
                    case Base6Directions.Direction.Down:
                        gyroPitch = -(float)roll;
                        gyroYaw = (float)yaw;
                        gyroRoll = (float)pitch;
                        break;
                }

                foreach (var gyro in gyroList)
                {
                    if (!gyro.IsFunctional)
                        continue;

                    gyro.GyroOverride = true;
                    gyro.Pitch = gyroPitch;
                    gyro.Yaw = gyroYaw;
                    gyro.Roll = gyroRoll;
                }
            }
        }

        /// <summary>
        /// Disables gyroscope override on all gyroscopes.
        /// </summary>
        private void DisableGyroscopeOverride()
        {
            foreach (var directionGroup in gyroscopes.Values)
            {
                foreach (var gyro in directionGroup)
                {
                    gyro.Pitch = 0;
                    gyro.Yaw = 0;
                    gyro.Roll = 0;
                    gyro.GyroOverride = false;
                }
            }
        }

        private void HandleNavigating()
        {
            Vector3D currentPos = shipController.GetPosition();

            // Check if we need to update waypoint tracking (lookahead)
            if (pathfindingManager.ShouldUpdateWaypointTracking(ref currentPos))
            {
                pathfindingManager.UpdateWaypointTracking(ref currentPos);
            }

            // Get current waypoint information
            WaypointResponse waypointInfo = pathfindingManager.GetWaypointResponse();

            if (waypointInfo == null)
            {
                // No waypoint yet, get first one
                Vector3D waypoint;
                PathfindingResult wpResult = pathfindingManager.GetNextWaypoint(ref currentPos, out waypoint);
                if (wpResult == PathfindingResult.Success)
                {
                    currentWaypoint = waypoint;
                    waypointInfo = pathfindingManager.GetWaypointResponse();
                }
                else if (pathfindingManager.LastGetNextWaypointFailedDueToComplexityBudget)
                {
                    AbortCurrentTaskDueToPathfindingComplexity();
                    return;
                }
                else
                {
                    Log.Error("Failed to get waypoint");
                    return;
                }
            }

            // Calculate safe speed based on waypoint behavior
            float targetSpeed = CalculateSafeSpeed(
                ref currentPos,
                ref waypointInfo.Position,
                waypointInfo.SuggestedBehavior
            );

            // Apply thrust to move toward waypoint
            ApplyNavigationalThrust(ref waypointInfo.Position, targetSpeed);

            // Check if we've reached the waypoint
            float distanceToWaypoint = (float)Vector3D.Distance(currentPos, waypointInfo.Position);

            if (distanceToWaypoint < settings.WaypointTolerance)
            {
                // Reached waypoint
                if (waypointInfo.IsLastWaypoint)
                {
                    // Arrived at destination
                    currentState = Drone.State.Welding; // or appropriate task state
                }
                else
                {
                    // Advance to next waypoint
                    pathfindingManager.AdvanceToNextWaypoint();

                    // Get next waypoint
                    Vector3D nextWaypoint;
                    PathfindingResult nextWp = pathfindingManager.GetNextWaypoint(ref currentPos, out nextWaypoint);
                    if (nextWp == PathfindingResult.Success)
                    {
                        currentWaypoint = nextWaypoint;
                    }
                    else if (pathfindingManager.LastGetNextWaypointFailedDueToComplexityBudget)
                    {
                        AbortCurrentTaskDueToPathfindingComplexity();
                    }
                }
            }
        }

        private void AbortCurrentTaskDueToPathfindingComplexity()
        {
            if (currentTask != null && operationMode == Drone.OperationMode.ManagedByScheduler)
            {
                SendTaskAborted((ushort)currentTask.TaskId, "Pathfinding complexity budget exceeded");
            }

            currentTask = null;
            currentTaskId = 0;
            pathfindingManager.ClearCache();
            currentState = Drone.State.ReturningToBase;
            Log.Warning("Drone {0} aborted task due to pathfinding complexity; returning to base", _entityId);
        }

        private void SendTaskAborted(ushort taskId, string reason)
        {
            var msg = new Message<TaskAborted>
            {
                Payload = new TaskAborted
                {
                    TaskId = taskId,
                    Reason = reason
                },
                MessageId = GenerateId(),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                RecipientId = settings.managingScheduler,
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Scheduler,
                Channel = Channel.DIRECT_MESSAGE,
            };

            var errorCode = messaging.SendDirectMessage(primaryAntenna, msg, false);
            HandleErrorCode(errorCode, msg);
            Log.LogDroneNetwork(LogLevel.Warning, "Drone {0} sent TaskAborted for task {1}", _entityId, taskId);
        }

        #region Thrust Management

        /// <summary>
        /// Calculate safe speed based on waypoint behavior and stopping distance
        /// </summary>
        private float CalculateSafeSpeed(
            ref Vector3D currentPos,
            ref Vector3D waypointPos,
            WaypointBehavior behavior)
        {
            // Get base speed from behavior
            float targetSpeed;
            switch (behavior)
            {
                case WaypointBehavior.RunThrough:
                    targetSpeed = settings.SpeedLimit;
                    break;
                case WaypointBehavior.SlowApproach:
                    targetSpeed = settings.ApproachSpeed;
                    break;
                case WaypointBehavior.FullStop:
                    targetSpeed = 0f;
                    break;
                default:
                    targetSpeed = settings.SpeedLimit;
                    break;
            }

            // Calculate distance to waypoint
            Vector3D distanceVector;
            Vector3D.Subtract(ref waypointPos, ref currentPos, out distanceVector);
            float distance = (float)distanceVector.Length();

            // Get current speed
            float currentSpeed = (float)shipController.GetShipVelocities().LinearVelocity.Length();

            // Calculate stopping distance at current speed
            float stoppingDistance = CalculateStoppingDistance(currentSpeed);

            // If we can't stop in time, reduce speed (80% safety margin)
            if (stoppingDistance > distance * 0.8f)
            {
                // Calculate max safe speed: v = sqrt(2 * a * s)
                float maxDeceleration = GetMaxDecelerationForCurrentVelocity();
                if (maxDeceleration > 0.1f)
                {
                    float safeSpeed = (float)Math.Sqrt(2.0 * maxDeceleration * distance * 0.8f);
                    targetSpeed = Math.Min(targetSpeed, safeSpeed);
                }
                else
                {
                    // Can't decelerate properly, use docking speed as fallback
                    targetSpeed = settings.DockingSpeed;
                }
            }

            return targetSpeed;
        }

        /// <summary>
        /// Calculate thrust profile from current thrusters and mass
        /// </summary>
        private void CalculateThrustProfile()
        {
            if (shipController == null || currentMass < 0.1f)
            {
                thrustProfileValid = false;
                return;
            }

            // Sum thrust for each direction
            thrustProfile.MaxThrustForward = 0f;
            thrustProfile.MaxThrustBackward = 0f;
            thrustProfile.MaxThrustUp = 0f;
            thrustProfile.MaxThrustDown = 0f;
            thrustProfile.MaxThrustLeft = 0f;
            thrustProfile.MaxThrustRight = 0f;

            foreach (var kvp in thrusters)
            {
                float directionThrust = 0f;

                foreach (var thruster in kvp.Value)
                {
                    if (thruster.IsWorking && thruster.IsFunctional)
                        directionThrust += thruster.MaxEffectiveThrust;
                }

                switch (kvp.Key)
                {
                    case Base6Directions.Direction.Forward:
                        thrustProfile.MaxThrustForward = directionThrust;
                        break;
                    case Base6Directions.Direction.Backward:
                        thrustProfile.MaxThrustBackward = directionThrust;
                        break;
                    case Base6Directions.Direction.Up:
                        thrustProfile.MaxThrustUp = directionThrust;
                        break;
                    case Base6Directions.Direction.Down:
                        thrustProfile.MaxThrustDown = directionThrust;
                        break;
                    case Base6Directions.Direction.Left:
                        thrustProfile.MaxThrustLeft = directionThrust;
                        break;
                    case Base6Directions.Direction.Right:
                        thrustProfile.MaxThrustRight = directionThrust;
                        break;
                }
            }

            // Calculate accelerations: a = F / m
            thrustProfile.MaxAccelerationForward = thrustProfile.MaxThrustForward / currentMass;
            thrustProfile.MaxAccelerationBackward = thrustProfile.MaxThrustBackward / currentMass;
            thrustProfile.MaxAccelerationUp = thrustProfile.MaxThrustUp / currentMass;
            thrustProfile.MaxAccelerationDown = thrustProfile.MaxThrustDown / currentMass;
            thrustProfile.MaxAccelerationLeft = thrustProfile.MaxThrustLeft / currentMass;
            thrustProfile.MaxAccelerationRight = thrustProfile.MaxThrustRight / currentMass;

            thrustProfileValid = true;
            lastThrustProfileUpdate = MyAPIGateway.Session.GameplayFrameCounter;

            Log.Verbose("Drone {0} thrust profile updated - Mass: {1:F0}kg, MaxAccel: F:{2:F1} B:{3:F1} U:{4:F1} D:{5:F1}",
                _entityId, currentMass,
                thrustProfile.MaxAccelerationForward,
                thrustProfile.MaxAccelerationBackward,
                thrustProfile.MaxAccelerationUp,
                thrustProfile.MaxAccelerationDown);
        }

        /// <summary>
        /// Update hover thrust to counteract gravity
        /// </summary>
        private void UpdateHoverThrust()
        {
            // Update gravity vector
            gravityVector = shipController.GetNaturalGravity();
            double gravityLengthSq = gravityVector.LengthSquared();

            if (gravityLengthSq < 0.1)
            {
                // No gravity - clear hover thrust
                if (currentHoverThrustPercentage > 0f)
                {
                    ClearHoverThrust();
                }
                return;
            }

            double gravityLength = Math.Sqrt(gravityLengthSq);

            // Force needed: F = m * g
            float hoverForceRequired = currentMass * (float)gravityLength;

            // Get upward direction (opposite of gravity)
            Vector3D upDirection;
            Vector3D.Negate(ref gravityVector, out upDirection);
            Vector3D.Normalize(ref upDirection, out upDirection);

            // Find which thruster direction provides upward thrust
            MatrixD worldMatrix = shipController.WorldMatrix;
            Base6Directions.Direction upThrustDirection = GetDirectionMostAlignedWith(ref upDirection, ref worldMatrix);

            // Get thrusters for this direction
            var upThrusters = thrusters[upThrustDirection];
            if (upThrusters.Count == 0)
            {
                Log.Warning("Drone {0} has no thrusters to counteract gravity in direction {1}",
                    _entityId, upThrustDirection);
                return;
            }

            // Calculate total available thrust
            float totalUpThrust = 0f;
            foreach (var thruster in upThrusters)
            {
                if (thruster.IsWorking && thruster.IsFunctional)
                    totalUpThrust += thruster.MaxEffectiveThrust;
            }

            if (totalUpThrust < 0.1f)
            {
                Log.Warning("Drone {0} has no functional thrusters for gravity compensation", _entityId);
                return;
            }

            // Calculate required thrust percentage
            float thrustPercentage = MathHelper.Clamp(hoverForceRequired / totalUpThrust, 0f, 1f);

            // Warn if insufficient thrust
            if (thrustPercentage > 0.95f)
            {
                Log.Warning("Drone {0} requires {1:F1}% thrust just to hover - insufficient thrust margin",
                    _entityId, thrustPercentage * 100f);
            }

            // Apply hover thrust
            foreach (var thruster in upThrusters)
            {
                if (thruster.IsWorking && thruster.IsFunctional)
                    thruster.ThrustOverridePercentage = thrustPercentage;
            }

            currentHoverThrustPercentage = thrustPercentage;
            currentHoverDirection = upThrustDirection;
        }

        /// <summary>
        /// Clear hover thrust from all thrusters
        /// </summary>
        private void ClearHoverThrust()
        {
            if (currentHoverDirection == Base6Directions.Direction.Forward)
                return; // No hover thrust active

            var hoverThrusters = thrusters[currentHoverDirection];
            foreach (var thruster in hoverThrusters)
            {
                thruster.ThrustOverridePercentage = 0f;
            }

            currentHoverThrustPercentage = 0f;
            currentHoverDirection = Base6Directions.Direction.Forward;
        }

        /// <summary>
        /// Apply navigational thrust to move toward target
        /// </summary>
        private void ApplyNavigationalThrust(ref Vector3D targetPosition, float targetSpeed)
        {
            // First ensure hover thrust is applied
            UpdateHoverThrust();

            // Get current state
            Vector3D currentPos = shipController.GetPosition();
            Vector3 currentVel = shipController.GetShipVelocities().LinearVelocity;

            // Calculate direction to target
            Vector3D directionVector;
            Vector3D.Subtract(ref targetPosition, ref currentPos, out directionVector);

            double distanceSq = directionVector.LengthSquared();
            if (distanceSq < 0.01)
                return; // Already at target

            double distance = Math.Sqrt(distanceSq);

            // Normalize direction
            Vector3D targetDirection;
            Vector3D.Divide(ref directionVector, distance, out targetDirection);

            // Calculate desired velocity
            Vector3D desiredVelocity;
            Vector3D.Multiply(ref targetDirection, targetSpeed, out desiredVelocity);

            // Calculate velocity error
            Vector3D currentVelD = new Vector3D(currentVel.X, currentVel.Y, currentVel.Z);
            Vector3D velocityError;
            Vector3D.Subtract(ref desiredVelocity, ref currentVelD, out velocityError);

            // Convert to local controller frame
            MatrixD worldMatrix = shipController.WorldMatrix;
            MatrixD inverseMatrix = MatrixD.Transpose(worldMatrix);

            Vector3D localVelocityError;
            Vector3D.TransformNormal(ref velocityError, ref inverseMatrix, out localVelocityError);

            // Apply proportional control
            const double THRUST_GAIN = 0.5; // Tune this value

            // Decompose into controller axes
            double forwardThrust = localVelocityError.Z * THRUST_GAIN;
            double upThrust = localVelocityError.Y * THRUST_GAIN;
            double rightThrust = localVelocityError.X * THRUST_GAIN;

            // Apply thrust (these methods handle combining with hover thrust)
            ApplyDirectionalThrustDelta(Base6Directions.Direction.Forward, forwardThrust);
            ApplyDirectionalThrustDelta(Base6Directions.Direction.Up, upThrust);
            ApplyDirectionalThrustDelta(Base6Directions.Direction.Right, rightThrust);
        }

        /// <summary>
        /// Apply additional thrust in a direction (adds to existing override like hover thrust)
        /// </summary>
        private void ApplyDirectionalThrustDelta(Base6Directions.Direction direction, double thrustAmount)
        {
            // Determine forward or backward
            Base6Directions.Direction actualDirection;
            float absThrust = (float)Math.Abs(thrustAmount);

            if (thrustAmount >= 0)
            {
                actualDirection = direction;
            }
            else
            {
                actualDirection = Base6Directions.GetOppositeDirection(direction);
            }

            // Get thrusters for this direction
            var directionThrusters = thrusters[actualDirection];
            if (directionThrusters.Count == 0)
                return;

            // Clamp thrust amount
            absThrust = MathHelper.Clamp(absThrust, 0f, 1f);

            // Apply thrust to all thrusters in this direction
            foreach (var thruster in directionThrusters)
            {
                if (!thruster.IsWorking || !thruster.IsFunctional)
                    continue;

                // Get current override (may include hover thrust)
                float currentOverride = thruster.ThrustOverridePercentage;

                // Add navigational thrust
                float combinedThrust = MathHelper.Clamp(currentOverride + absThrust, 0f, 1f);

                thruster.ThrustOverridePercentage = combinedThrust;
            }
        }

        /// <summary>
        /// Get the direction most aligned with a world vector
        /// </summary>
        private Base6Directions.Direction GetDirectionMostAlignedWith(
            ref Vector3D worldDirection,
            ref MatrixD worldMatrix)
        {
            // Cache basis vectors
            Vector3D forward = worldMatrix.Forward;
            Vector3D backward = worldMatrix.Backward;
            Vector3D up = worldMatrix.Up;
            Vector3D down = worldMatrix.Down;
            Vector3D left = worldMatrix.Left;
            Vector3D right = worldMatrix.Right;

            // Calculate dot products (temp variables)
            double dotForward, dotBackward, dotUp, dotDown, dotLeft, dotRight;
            Vector3D.Dot(ref worldDirection, ref forward, out dotForward);
            Vector3D.Dot(ref worldDirection, ref backward, out dotBackward);
            Vector3D.Dot(ref worldDirection, ref up, out dotUp);
            Vector3D.Dot(ref worldDirection, ref down, out dotDown);
            Vector3D.Dot(ref worldDirection, ref left, out dotLeft);
            Vector3D.Dot(ref worldDirection, ref right, out dotRight);

            // Find maximum alignment
            double maxDot = dotForward;
            Base6Directions.Direction result = Base6Directions.Direction.Forward;

            if (dotBackward > maxDot) { maxDot = dotBackward; result = Base6Directions.Direction.Backward; }
            if (dotUp > maxDot) { maxDot = dotUp; result = Base6Directions.Direction.Up; }
            if (dotDown > maxDot) { maxDot = dotDown; result = Base6Directions.Direction.Down; }
            if (dotLeft > maxDot) { maxDot = dotLeft; result = Base6Directions.Direction.Left; }
            if (dotRight > maxDot) { maxDot = dotRight; result = Base6Directions.Direction.Right; }

            return result;
        }

        /// <summary>
        /// Calculate stopping distance at current speed
        /// </summary>
        private float CalculateStoppingDistance(float currentSpeed)
        {
            if (!thrustProfileValid)
                return float.MaxValue;

            // Get current movement direction
            Vector3 velocity = shipController.GetShipVelocities().LinearVelocity;
            if (velocity.LengthSquared() < 0.01f)
                return 0f;

            // Get deceleration capability (opposite of movement direction)
            float maxDeceleration = GetMaxDecelerationForCurrentVelocity();

            if (maxDeceleration < 0.1f)
                return float.MaxValue; // Can't decelerate

            // s = v² / (2a)
            return (currentSpeed * currentSpeed) / (2f * maxDeceleration);
        }

        /// <summary>
        /// Calculate max speed for a given stopping distance
        /// </summary>
        private float CalculateMaxSpeedForDistance(float distance)
        {
            if (!thrustProfileValid)
                return 0f;

            float maxDeceleration = GetMaxDecelerationForCurrentVelocity();

            if (maxDeceleration < 0.1f)
                return 0f;

            // v = sqrt(2 * a * s)
            return (float)Math.Sqrt(2.0 * maxDeceleration * distance);
        }

        /// <summary>
        /// Get maximum deceleration capability in current velocity direction
        /// </summary>
        private float GetMaxDecelerationForCurrentVelocity()
        {
            Vector3 velocity = shipController.GetShipVelocities().LinearVelocity;
            if (velocity.LengthSquared() < 0.01f)
                return thrustProfile.MaxAccelerationForward; // Default

            // Convert velocity to local frame
            Vector3D velocityD = new Vector3D(velocity.X, velocity.Y, velocity.Z);
            MatrixD worldMatrix = shipController.WorldMatrix;
            MatrixD inverseMatrix = MatrixD.Transpose(worldMatrix);

            Vector3D localVelocity;
            Vector3D.TransformNormal(ref velocityD, ref inverseMatrix, out localVelocity);

            // Determine primary movement direction and get opposing thrust
            double absX = Math.Abs(localVelocity.X);
            double absY = Math.Abs(localVelocity.Y);
            double absZ = Math.Abs(localVelocity.Z);

            if (absZ > absX && absZ > absY)
            {
                // Moving primarily forward/backward
                return localVelocity.Z > 0 ?
                    thrustProfile.MaxAccelerationBackward :
                    thrustProfile.MaxAccelerationForward;
            }
            else if (absY > absX)
            {
                // Moving primarily up/down
                return localVelocity.Y > 0 ?
                    thrustProfile.MaxAccelerationDown :
                    thrustProfile.MaxAccelerationUp;
            }
            else
            {
                // Moving primarily left/right
                return localVelocity.X > 0 ?
                    thrustProfile.MaxAccelerationLeft :
                    thrustProfile.MaxAccelerationRight;
            }
        }

        /// <summary>
        /// Clear all thrust overrides
        /// </summary>
        private void ClearAllThrustOverrides()
        {
            foreach (var kvp in thrusters)
            {
                foreach (var thruster in kvp.Value)
                {
                    thruster.ThrustOverridePercentage = 0f;
                }
            }

            currentHoverThrustPercentage = 0f;
        }

        #endregion




        /// <summary>
        /// Gets diagnostic information about the drone
        /// </summary>
        public string GetDiagnostics()
        {
            if (!_initialized)
                return "Drone not initialized";

            return $"State: {currentState}, Capabilities: {capabilities}, H2: {currentH2Level:F1}%, Battery: {currentBatteryLevel:F1}%";
        }

        /// <summary>
        /// Resets the drone to initial state
        /// </summary>
        public void Reset()
        {
            ResetDrone();
        }

        public void ResetDrone()
        {
            Log.Verbose("Drone {0} reset requested", _entityId);

            currentTask = null;
            taskQueue.Clear();
            currentPath.Clear();

            // Disable tools
            if (welder?.Enabled == true) welder.Enabled = false;
            if (grinder?.Enabled == true) grinder.Enabled = false;

            // Reset thrusters
            foreach (var kvp in thrusters)
            {
                foreach (var thruster in kvp.Value)
                    thruster.ThrustOverridePercentage = 0f;
            }

            // Reset gyros
            foreach (var kvp in gyroscopes)
            {
                foreach (var gyro in kvp.Value)
                {
                    gyro.Yaw = 0;
                    gyro.Pitch = 0;
                    gyro.Roll = 0;
                    gyro.GyroOverride = false;
                }
            }

            _consecutiveErrors = 0;
            currentState = Drone.State.Standby;

            Log.Verbose("Drone {0} reset complete", _entityId);
        }

        #region Lifecycle - Cleanup
        public void SessionRegister()
        {
            //MyAPIGateway.Multiplayer.SendMessageToServer()
        }

        // User sets it by pointing the drone in desired direction
        public void SetHomeOrientationToCurrent()
        {
            settings.HomePosition = shipController.GetPosition();
            settings.HomeForwardDirection = shipController.WorldMatrix.Forward;
            Log.Info("Drone {0} home orientation set to current", _entityId);
        }
        public override void MarkForClose()
        {
            try
            {
                // Clean shutdown
                ResetDrone();

                // Save configuration before closing
                SaveSettings();

                // Unregister from session
                IAISession.Instance?.UnregisterDroneController(Entity.EntityId);

                // Clear references
                remoteControl = null;
                shipController = null;
                connector = null;
                welder = null;
                grinder = null;
                primaryAntenna = null;

                gyroscopes?.Clear();
                thrusters?.Clear();
                sensors?.Clear();
                hydrogenTanks?.Clear();
                batteries?.Clear();
                cargoContainers?.Clear();
                taskQueue?.Clear();
                currentPath?.Clear();

                Log.Verbose("ImprovedAIDroneController Drone controller closed: {0}", Entity?.DisplayName);
            }
            catch (Exception ex)
            {
                Log.Error("MarkForClose", ex);
            }

            base.MarkForClose();
        }
        #endregion

        #region Terminal Controls
        // Entity ID (read-only)
        public long Terminal_EntityId
        {
            get { return _entityId; }
        }

        // Enabled
        public bool Terminal_Enabled
        {
            get { return settings?.IsEnabled ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.IsEnabled = value;
                    SaveSettings();
                }
            }
        }

        // Operation Mode
        public long Terminal_OperationModeValue
        {
            get { return settings != null ? (long)settings.OperationMode : 0; }
            set
            {
                if (settings != null)
                {
                    settings.OperationMode = (Drone.OperationMode)value;
                    operationMode = (Drone.OperationMode)value;
                    SaveSettings();
                }
            }
        }

        // Managing Scheduler
        public long Terminal_ManagingScheduler
        {
            get { return settings?.managingScheduler ?? 0; }
            set
            {
                if (settings != null)
                {
                    settings.managingScheduler = value;
                    SaveSettings();
                }
            }
        }

        public void Terminal_FillSchedulerList(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            // TODO: Implement scheduler discovery within antenna range
            var currentScheduler = settings?.managingScheduler ?? 0;
            if (currentScheduler != 0)
            {
                var item = new MyTerminalControlListBoxItem(
                    MyStringId.GetOrCompute($"Scheduler {currentScheduler}"),
                    MyStringId.NullOrEmpty,
                    currentScheduler);
                items.Add(item);
                if (settings.managingScheduler == currentScheduler)
                    selected.Add(item);
            }
        }

        // Home Position GPS
        public StringBuilder Terminal_HomePositionGPS
        {
            get
            {
                if (settings == null) return new StringBuilder("");
                var gps = ConvertVectorToGPS("Drone Home", settings.HomePosition);
                return new StringBuilder(gps);
            }
            set
            {
                if (settings != null)
                {
                    var position = ParseGPSString(value.ToString());
                    if (position.HasValue)
                    {
                        settings.HomePosition = position.Value;
                        SaveSettings();
                    }
                }
            }
        }

        public void Terminal_SetCurrentAsHome()
        {
            if (remoteControl != null && settings != null)
            {
                settings.HomePosition = remoteControl.GetPosition();
                settings.HomeForwardDirection = remoteControl.WorldMatrix.Forward;
                SaveSettings();
                Log.Info("Drone {0} home position set to current location", _entityId);
            }
        }

        private string ConvertVectorToGPS(string name, Vector3D position)
        {
            return $"GPS:{name}:{position.X:F2}:{position.Y:F2}:{position.Z:F2}:#FF75C9F1:";
        }

        private Vector3D? ParseGPSString(string gpsString)
        {
            if (string.IsNullOrWhiteSpace(gpsString)) return null;

            var parts = gpsString.Split(':');
            if (parts.Length < 5 || !parts[0].Equals("GPS", StringComparison.OrdinalIgnoreCase))
                return null;

            double x, y, z;
            if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
                double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y) &&
                double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
            {
                return new Vector3D(x, y, z);
            }

            return null;
        }

        // Home Orientation
        public bool Terminal_EnforceHomeOrientation
        {
            get { return settings?.EnforceHomeOrientation ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.EnforceHomeOrientation = value;
                    SaveSettings();
                }
            }
        }

        public long Terminal_HomeForwardDirectionValue
        {
            get
            {
                if (settings == null) return 0;
                var forward = settings.HomeForwardDirection;
                return (long)Base6Directions.GetClosestDirection(forward);
            }
            set
            {
                if (settings != null)
                {
                    settings.HomeForwardDirection = Base6Directions.GetVector((Base6Directions.Direction)value);
                    SaveSettings();
                }
            }
        }

        // Task Type Filters
        public bool Terminal_UseTaskTypeFilters
        {
            get { return settings?.UseTaskTypeFilters ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.UseTaskTypeFilters = value;
                    SaveSettings();
                }
            }
        }

        public Scheduler.TaskType Terminal_TaskTypeFilters
        {
            get { return settings?.TaskTypeFilters ?? 0; }
            set
            {
                if (settings != null)
                {
                    settings.TaskTypeFilters = value;
                    SaveSettings();
                }
            }
        }

        // Capability Filters
        public bool Terminal_UseCapabilityFilters
        {
            get { return settings?.UseCapabilityFilters ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.UseCapabilityFilters = value;
                    SaveSettings();
                }
            }
        }

        public Drone.Capabilities Terminal_CapabilityFilters
        {
            get { return settings?.CapabilityFilters ?? 0; }
            set
            {
                if (settings != null)
                {
                    settings.CapabilityFilters = value;
                    SaveSettings();
                }
            }
        }

        // Navigation Settings
        public float Terminal_WaypointTolerance
        {
            get { return settings?.WaypointTolerance ?? 5f; }
            set
            {
                if (settings != null)
                {
                    settings.WaypointTolerance = MathHelper.Clamp(value, 1f, 50f);
                    SaveSettings();
                }
            }
        }

        public float Terminal_ApproachSpeed
        {
            get { return settings?.ApproachSpeed ?? 5f; }
            set
            {
                if (settings != null)
                {
                    settings.ApproachSpeed = MathHelper.Clamp(value, 1f, 50f);
                    SaveSettings();
                }
            }
        }

        public float Terminal_SpeedLimit
        {
            get { return settings?.SpeedLimit ?? 50f; }
            set
            {
                if (settings != null)
                {
                    settings.SpeedLimit = MathHelper.Clamp(value, 10f, 100f);
                    SaveSettings();
                }
            }
        }

        public float Terminal_DockingSpeed
        {
            get { return settings?.DockingSpeed ?? 2.5f; }
            set
            {
                if (settings != null)
                {
                    settings.DockingSpeed = MathHelper.Clamp(value, 0.5f, 10f);
                    SaveSettings();
                }
            }
        }

        // Gravity Alignment
        public bool Terminal_AlignToPGravity
        {
            get { return settings?.AlignToPGravity ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.AlignToPGravity = value;
                    SaveSettings();
                }
            }
        }

        public float Terminal_MaxPitchDegrees
        {
            get { return settings?.PGravityAlignMaxPitchDegrees ?? 10f; }
            set
            {
                if (settings != null)
                {
                    settings.PGravityAlignMaxPitchDegrees = MathHelper.Clamp(value, 0f, 45f);
                    SaveSettings();
                }
            }
        }

        public float Terminal_MaxRollDegrees
        {
            get { return settings?.PGravityAlignMaxRollDegrees ?? 10f; }
            set
            {
                if (settings != null)
                {
                    settings.PGravityAlignMaxRollDegrees = MathHelper.Clamp(value, 0f, 45f);
                    SaveSettings();
                }
            }
        }

        // Display
        public StringBuilder Terminal_LCDScreenTag
        {
            get { return settings != null ? new StringBuilder(settings.LCDScreenTag) : new StringBuilder(""); }
            set
            {
                if (settings != null)
                {
                    settings.LCDScreenTag = value != null ? value.ToString() : "[IAIDrone]";
                    SaveSettings();
                }
            }
        }

        // Power Monitoring - Hydrogen
        public bool Terminal_MonitorHydrogen
        {
            get { return settings?.MonitorHydrogenLevels ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.MonitorHydrogenLevels = value;
                    SaveSettings();
                }
            }
        }

        public float Terminal_H2RefuelThreshold
        {
            get { return settings?.HydrogenRefuelThreshold ?? 25f; }
            set
            {
                if (settings != null)
                {
                    settings.HydrogenRefuelThreshold = MathHelper.Clamp(value, 5f, 50f);
                    SaveSettings();
                }
            }
        }

        public float Terminal_H2OperationalThreshold
        {
            get { return settings?.HydrogenOperationalThreshold ?? 50f; }
            set
            {
                if (settings != null)
                {
                    settings.HydrogenOperationalThreshold = MathHelper.Clamp(value, 10f, 95f);
                    SaveSettings();
                }
            }
        }

        public bool Terminal_AlwaysRefuel
        {
            get { return settings?.AlwaysRefuelWhenDocked ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.AlwaysRefuelWhenDocked = value;
                    SaveSettings();
                }
            }
        }

        // Power Monitoring - Battery
        public bool Terminal_MonitorBattery
        {
            get { return settings?.MonitorBatteryLevels ?? false; }
            set
            {
                if (settings != null)
                {
                    settings.MonitorBatteryLevels = value;
                    SaveSettings();
                }
            }
        }

        public float Terminal_BatteryRefuelThreshold
        {
            get { return settings?.BatteryRefuelThreshold ?? 20f; }
            set
            {
                if (settings != null)
                {
                    settings.BatteryRefuelThreshold = MathHelper.Clamp(value, 5f, 50f);
                    SaveSettings();
                }
            }
        }

        public float Terminal_BatteryOperationalThreshold
        {
            get { return settings?.BatteryOperationalThreshold ?? 80f; }
            set
            {
                if (settings != null)
                {
                    settings.BatteryOperationalThreshold = MathHelper.Clamp(value, 10f, 95f);
                    SaveSettings();
                }
            }
        }

        // Controller Settings
        public long Terminal_ControllerForwardDirectionValue
        {
            get { return settings != null ? (long)settings.ControllerForwardDirection : (long)Base6Directions.Direction.Forward; }
            set
            {
                if (settings != null)
                {
                    settings.ControllerForwardDirection = (Base6Directions.Direction)value;
                    SaveSettings();
                }
            }
        }
        #endregion
    }
}
