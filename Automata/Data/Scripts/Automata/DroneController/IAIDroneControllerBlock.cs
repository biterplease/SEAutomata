using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

using Automata.Util.Logging;
using Automata.Pathfinding;
using Automata.Util;
using Automata.VirtualNetwork;

namespace Automata.DroneController
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RemoteControl), false,
        "ImprovedAISmallDroneController",
        "ImprovedAILargeDroneController")]
    public class DroneControllerBlock : MyGameLogicComponent
    {
        private IMyUtilitiesDelegate utilitiesDelegate;
        private IMySessionDelegate sessionDelegate;


        private long _entityId;
        private int _messageCounter = 0;
        private int _bidRoundBidCounter = 0;
        private MessageQueue messaging;
        private IMyEntity entity;
        private IMyCubeBlock block;
        private IMyGamePruningStructureDelegate pruningStructureDelegate;
        private IMyPlanetDelegate planetDelegate;
        private string _debugFlightGps = string.Empty;

        private TerminalDisplayManager terminalLogger;

        
        private DroneControllerSettings settings;
        private OperationMode operationMode = OperationMode.StandAlone;
        private Capabilities capabilities;
        private BehaviourProfile behaviourProfile;
        private State currentState = State.Initializing;
        private bool initialized = false;
        private int _consecutiveErrors = 0;

        private IMyShipController shipController;
        private IMyShipWelder welder;
        private IMyShipGrinder grinder;
        public IMyRadioAntenna primaryAntenna;
        private readonly List<IMySensorBlock> sensors = new List<IMySensorBlock>();
        private readonly List<IMyShipConnector> connectors = new List<IMyShipConnector>();
        private readonly List<IMyLandingGear> landingGears = new List<IMyLandingGear>();
        private readonly Dictionary<Base6Directions.Direction, List<IMyGyro>> gyroscopes = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();
        private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> hydrogenThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
        private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> atmoThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
        private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> ionThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
        // cache to prevent recalculating every time
        private readonly Dictionary<Base6Directions.Direction, List<IMyThrust>> allThrusters = new Dictionary<Base6Directions.Direction, List<IMyThrust>>();
        private readonly List<IMyGasTank> hydrogenTanks = new List<IMyGasTank>();
        private readonly List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
        private readonly List<IMyCargoContainer> cargoContainers = new List<IMyCargoContainer>();

        private Vector3D gravityVector = Vector3D.Zero;
        private float physicalMass = 0.0f;
        private float baseMass = 0.0f;

        private Orchestrator.Job currentTask;
        private Queue<Orchestrator.Task> taskQueue = new Queue<Orchestrator.Task>();
        private ushort currentTaskId = 0;

        private long _lastComponentCheckFrame = 0;
        private long _lastStatusReportFrame = 0;
        private long _lastPowerCheckFrame = 0;

        #region Fields - Power Monitoring
        private bool needsHydrogenRefuel = false;
        private bool needsBatteryRecharge = false;
        private float lastH2Level = 100f;
        public float currentH2Level = 100f;
        private float lastBatteryLevel = 100f;
        public float currentBatteryLevel = 100f;
        #endregion

        #region Server-Imperative settings
        private int POWER_CHECK_INTERVAL_MIN_TICKS = 300;// 5 second default
        private int COMPONENT_CHECK_INTERVAL_MIN_TICKS = 600; // 10 second default;
        #endregion
        
        #region Fields - Navigation
        private Vector3D taskPosition;
        private List<Vector3D> currentPath = new List<Vector3D>();
        // private PathfindingManager pathfindingManager;
        private Vector3D currentWaypoint;


        private float currentHoverThrustPercentage = 0f;
        private Base6Directions.Direction currentHoverDirection = Base6Directions.Direction.Up;
        private Vector3DData navigationTarget;
        #endregion

        private int _initTicks = 0;
        private const int INIT_DELAY_TICKS = 180;

        public DroneControllerBlock(
            DroneControllerSettings settings = null,
            MessageQueue messaging = null,
            IMyUtilitiesDelegate utilitiesDelegate = null,
            IMySessionDelegate sessionDelegate = null,
            IMyGamePruningStructureDelegate pruningStructure = null,
            IMyPlanetDelegate planetDelegate = null)
        {
            this.settings = settings ?? new DroneControllerSettings();
            this.messaging = messaging ?? AutomataSession.GetMessageQueue();
            this.operationMode = settings.OperationMode;
            this.utilitiesDelegate = utilitiesDelegate ?? new MyUtilitiesDelegate();
            this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
            this.pruningStructureDelegate = pruningStructure ?? new MyGamePruningStructureDelegate();
            this.planetDelegate = planetDelegate ?? new MyPlanetDelegate();

            foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                gyroscopes[dir] = new List<IMyGyro>();
                ionThrusters[dir] = new List<IMyThrust>();
                atmoThrusters[dir] = new List<IMyThrust>();
                hydrogenThrusters[dir] = new List<IMyThrust>();
                allThrusters[dir] = new List<IMyThrust>();
            }
        }

        #region Core API Lifecycle
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            settings = new DroneControllerSettings();
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            DroneControllerTerminalControls.DoOnce(ModContext);
            
            this.entity = Entity;
            var remoteControl = Entity as IMyRemoteControl;
            if (remoteControl?.CubeGrid?.Physics == null) return;

            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            try
            {
                if (settings != null && settings.EnableInertialDampening)
                    UpdateInertialDampening();
                else
                    ClearAllThrustOverrides();
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneControllerBlock {0}: UpdateBeforeSimulation error: {1}", Entity.EntityId, ex);
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();
            // if (_controller == null || !_controller.initialized) return;
            // try
            // {
            //     if (!initialized) return;

            // if (currentFrame - _lastComponentCheckFrame >= COMPONENT_CHECK_INTERVAL_TICKS)
            // {
            //     _lastComponentCheckFrame = currentFrame;
            //     if (!CheckCapabilities())
            //     {
            //         currentState = State.Error;
            //         return;
            //     }
            // }

            // if (settings.MonitorHydrogenLevels || settings.MonitorBatteryLevels)
            // {
            //     if (currentFrame - _lastPowerCheckFrame >= POWER_CHECK_INTERVAL_TICKS)
            //     {
            //         _lastPowerCheckFrame = currentFrame;
            //         CheckPowerLevels();
            //     }
            // }

            // if (operationMode == OperationMode.ManagedByScheduler)
            // {
            //     if (currentFrame - _lastStatusReportFrame >= STATUS_REPORT_INTERVAL_TICKS)
            //     {
            //         _lastStatusReportFrame = currentFrame;
            //         BroadcastStatusReport();
            //     }

            //     if (!_registrationSent && primaryAntenna != null)
            //     {
            //         BroadcastDroneRegistration();
            //         _registrationSent = true;
            //     }

            //     ReadBidRoundStarts();
            //     SendBidRoundBids();
            //     ReadBidRoundWinnerAnnouncements();
            // }

            // if (settings.IsEnabled)
            //     UpdateAI();
            // }
            // catch (Exception ex)
            // {
            //     Log.Error("IAIDroneControllerBlock {0}: UpdateBeforeSimulation10 error: {1}", Entity.EntityId, ex);
            // }
        }

        public override void UpdateBeforeSimulation100()
        {
            base.UpdateBeforeSimulation100();
            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneControllerBlock {0}: UpdateBeforeSimulation100 error: {1}", Entity.EntityId, ex);
            }
        }

        public override void MarkForClose()
        {
            try
            {
                Shutdown();
                SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneControllerBlock MarkForClose error: {0}", ex);
            }
            base.MarkForClose();
        }
        #endregion

        #region State load/save
        private void SaveSettings()
        {
            try
            {
                if (settings == null || utilitiesDelegate == null) return;
                if (Entity.Storage == null)
                    Entity.Storage = new MyModStorageComponent();
                Entity.Storage.SetValue(AutomataSession.MOD_GUID, Convert.ToBase64String(utilitiesDelegate.SerializeToBinary(settings)));
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneControllerBlock {0}: SaveSettings error: {1}", Entity.EntityId, ex);
            }
        }
        #endregion

        #region Initialization
        private void Initialize()
        {
            if (initialized) return;

            _entityId = entity.EntityId;
            block = entity as IMyCubeBlock;
            currentState = State.Initializing;

            // STATUS_REPORT_INTERVAL_TICKS = AutomataSession.GetConfig().MessageQueue.DroneMessageThrottlingTicks();
            POWER_CHECK_INTERVAL_MIN_TICKS = TimeUtil.SecondsToTicks(AutomataSession.GetConfig().Drone.PowerCheckIntervalMinSeconds);
            COMPONENT_CHECK_INTERVAL_MIN_TICKS = TimeUtil.SecondsToTicks(AutomataSession.GetConfig().Drone.ComponentCheckIntervalMinSeconds);

            terminalLogger = new TerminalDisplayManager(block as IMyTerminalBlock, 10);
            shipController = entity as IMyShipController;
            if (shipController == null)
            {
                terminalLogger.Echo("DroneController {0} entity is not a ship controller", _entityId);
                Log.Error("DroneController {0} entity is not a ship controller", _entityId);
                currentState = State.Error;
                return;
            }

            if (!CheckCapabilities())
            {
                Log.Error("DroneController {0} failed capability check", _entityId);
                terminalLogger.Echo("DroneController {0} failed capability check", _entityId);
                currentState = State.Error;
                return;
            }

            // pathfindingManager = new PathfindingManager(AutomataSession.GetConfig().Pathfinding, pruningStructureDelegate, planetDelegate);

            if (primaryAntenna != null)
            {
                messaging.RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
            }

            if (operationMode == OperationMode.ManagedByScheduler)
            {
                messaging.Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_START);
                messaging.Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
            }

            currentState = State.Standby;
            initialized = true;
            Log.Info("DroneController {0} initialized in mode: {1}", _entityId, operationMode);
        }
        #endregion

        #region Capability Detection
        public bool CheckCapabilities()
        {
            var cubeBlock = entity as IMyCubeBlock;
            if (cubeBlock == null) return false;

            var shipController = entity as IMyShipController;
            if (shipController?.CubeGrid == null) return false;

            UpdateMass();

            foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                gyroscopes[dir].Clear();
                hydrogenThrusters[dir].Clear();
                atmoThrusters[dir].Clear();
                ionThrusters[dir].Clear();
                allThrusters[dir].Clear();
            }
            sensors.Clear();
            hydrogenTanks.Clear();
            batteries.Clear();
            connectors.Clear();
            welder = null;
            grinder = null;
            primaryAntenna = null;

            var controllerMatrix = shipController.Orientation;
            var connectedGrids = new List<IMyCubeGrid>();
            cubeBlock.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(connectedGrids);

            foreach (var grid in connectedGrids)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var slimBlock in blocks)
                {
                    var fatBlock = slimBlock.FatBlock;
                    if (fatBlock == null || !fatBlock.IsFunctional) continue;

                    if (fatBlock is IMyShipConnector)
                        connectors.Add((IMyShipConnector)fatBlock);
                    else if (fatBlock is IMyShipWelder && welder == null)
                        welder = (IMyShipWelder)fatBlock;
                    else if (fatBlock is IMyShipGrinder && grinder == null)
                        grinder = (IMyShipGrinder)fatBlock;
                    else if (fatBlock is IMyLandingGear)
                        landingGears.Add((IMyLandingGear)fatBlock);
                    else if (fatBlock is IMyGyro)
                    {
                        var gyro = (IMyGyro)fatBlock;
                        var relDir = GetRelativeDirection(
                            gyro.Orientation.Forward,
                            settings.ControllerForwardDirection);
                        gyroscopes[relDir].Add(gyro);
                    }
                    else if (fatBlock is IMyThrust)
                    {
                        var thruster = (IMyThrust)fatBlock;
                        var relDir = GetRelativeDirection(
                            thruster.Orientation.Forward,
                            settings.ControllerForwardDirection);

                        if (thruster.BlockDefinition.SubtypeName.Contains("Hydrogen"))
                            hydrogenThrusters[relDir].Add(thruster);
                        else if (thruster.BlockDefinition.SubtypeName.Contains("Ion"))
                            ionThrusters[relDir].Add(thruster);
                        else if (thruster.BlockDefinition.SubtypeName.Contains("Atmospheric"))
                            atmoThrusters[relDir].Add(thruster);

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
            bool hasMinimum = HasAnyGyros(gyroscopes) && (HasAnyThrusters(hydrogenThrusters) || HasAnyThrusters(ionThrusters) || HasAnyThrusters(atmoThrusters)) && connectors.Count > 0;
            if (!hasMinimum){
                return false; // return early if minimum evaluation specs don't exist
            }

            foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                var thrusters = new List<IMyThrust>();
                List<IMyThrust> cache;
                if (ionThrusters.TryGetValue(dir, out cache))
                    thrusters.AddRange(cache);
                if (atmoThrusters.TryGetValue(dir, out cache))
                    thrusters.AddRange(cache);
                if (hydrogenThrusters.TryGetValue(dir, out cache))
                    thrusters.AddRange(cache);
                allThrusters[dir] = thrusters;
            }

            var h2ThrustProfile = CalculateThrustProfile(shipController, physicalMass, hydrogenThrusters);
            var atmoThrustProfile = CalculateThrustProfile(shipController, physicalMass, atmoThrusters);
            var ionThrustProfile = CalculateThrustProfile(shipController, physicalMass, ionThrusters);

            capabilities = Capabilities.None;
            behaviourProfile = BehaviourProfile.None;
            if (sensors.Count > 0) capabilities|= Capabilities.HasSensors;
            if (settings.AlwaysRefuelWhenDocked && connectors.Count > 0)
                behaviourProfile|= BehaviourProfile.RefuelWhenDocked | BehaviourProfile.RechargeWhenDocked;

            var hasAnyAtmoThrusters = HasAnyThrusters(atmoThrusters);
            var hasAnyHydrogenThrusters = HasAnyThrusters(hydrogenThrusters);
            var hasAnyIonThrusters = HasAnyThrusters(ionThrusters);

            if (hasAnyAtmoThrusters || hasAnyHydrogenThrusters)
            {
                var max1GLoad = CalculateMaxLoadInG(
                    atmoThrustProfile, h2ThrustProfile, physicalMass,
                    GetRelativeDirection(Base6Directions.Direction.Up, settings.ControllerForwardDirection), gravityNormal:9.81f);
                if (max1GLoad > 1.0f)
                    capabilities |= Capabilities.CanFlyAtmosphere;
            }
            if (hasAnyIonThrusters || hasAnyHydrogenThrusters)
            {
                var h2MaxThrust = h2ThrustProfile.MaxThrust;
                var ionMaxThrust = ionThrustProfile.MaxThrust;
                float maxLoad;
                if ( ionMaxThrust.Max > h2MaxThrust.Max)
                    maxLoad = CalculateMaxLoadInG(ionThrustProfile, h2ThrustProfile, physicalMass, direction:ionMaxThrust.Direction, gravityNormal: 0.981f);
                else
                    maxLoad = CalculateMaxLoadInG(ionThrustProfile, h2ThrustProfile, physicalMass, direction: h2MaxThrust.Direction, gravityNormal: 0.981f);
                if (maxLoad > 1.0f)
                    capabilities |= Capabilities.CanFlySpace;
            }

            return true;
        }
        #endregion

       public void Shutdown()
        {
            currentTask = null;
            taskQueue.Clear();
            currentPath.Clear();
            if (welder?.Enabled == true) welder.Enabled = false;
            if (grinder?.Enabled == true) grinder.Enabled = false;
            ClearAllThrustOverrides();
            // DisableGyroscopeOverride();
            _consecutiveErrors = 0;
            currentState = State.Standby;
        }

        #region Inertial Dampening
        private void UpdateInertialDampening()
        {
            if (shipController == null) return;

            Vector3D linearVelocity = shipController.GetShipVelocities().LinearVelocity;
            MatrixD worldMatrix = shipController.WorldMatrix;

            double localForwardSpeed = Vector3D.Dot(linearVelocity, worldMatrix.Forward);   // + = moving forward, - = moving backward
            double localUpSpeed       = Vector3D.Dot(linearVelocity, worldMatrix.Up);       // + = moving up, - = moving down
            double localRightSpeed     = Vector3D.Dot(linearVelocity, worldMatrix.Right);   // + = moving right, - = moving left-
            const double GAIN = 0.2d;
            if (localForwardSpeed > 0.0d)
            {
                SetDirectionalThrustOverride(Base6Directions.Direction.Forward, 0);
                SetDirectionalThrustOverride(Base6Directions.Direction.Backward, localForwardSpeed*GAIN);
            }
            else
            {
                SetDirectionalThrustOverride(Base6Directions.Direction.Backward, 0);
                SetDirectionalThrustOverride(Base6Directions.Direction.Forward, localForwardSpeed*GAIN);
            }

            if (localUpSpeed > 0.0d)
            {
                SetDirectionalThrustOverride(Base6Directions.Direction.Up, 0);
                SetDirectionalThrustOverride(Base6Directions.Direction.Down, localUpSpeed*GAIN);
            }
            else
            {
                SetDirectionalThrustOverride(Base6Directions.Direction.Down, 0);
                SetDirectionalThrustOverride(Base6Directions.Direction.Up, localUpSpeed*GAIN);
            }

            if (localRightSpeed > 0.0d)
            {
                SetDirectionalThrustOverride(Base6Directions.Direction.Right, 0);
                SetDirectionalThrustOverride(Base6Directions.Direction.Left, localRightSpeed*GAIN);
            }
            else
            {
                SetDirectionalThrustOverride(Base6Directions.Direction.Left, 0);
                SetDirectionalThrustOverride(Base6Directions.Direction.Right, localRightSpeed*GAIN);
            }
        }
        private void SetDirectionalThrustOverride(Base6Directions.Direction direction, double thrustAmount)
        {
            var allThrusters = GetAllThrustersInDirection(direction);
            for (int i = 0; i < allThrusters.Count; i++)
            {
                IMyThrust t = allThrusters[i];
                if (!t.IsWorking || !t.IsFunctional) continue;
                t.ThrustOverridePercentage = MathHelper.Clamp((float)Math.Abs(thrustAmount), 0f, 1f);
            }
        }
        private void ClearAllThrustOverrides()
        {
            foreach (var direction in Enum.GetValues(typeof(Base6Directions.Direction)))
                foreach (var thruster in GetAllThrustersInDirection((Base6Directions.Direction)direction))
                    thruster.ThrustOverridePercentage = 0f;
        }
        private List<IMyThrust> GetAllThrustersInDirection(Base6Directions.Direction direction)
        {
            return allThrusters[direction];
        }
        private float CalculateHoverThrust()
        {

            return 0.0f;   
        }
        #endregion


        #region Debug Flight
        // private void StartDebugAutopilotFlight(ref Vector3D targetPosition, string waypointName)
        // {
        //     if (_controller == null) return;
        //     _controller.ResetDrone();
        //     var rc = _controller.GetRemoteControl();
        //     if (rc == null) return;
        //     rc.SetAutoPilotEnabled(false);
        //     rc.ClearWaypoints();
        //     rc.AddWaypoint(targetPosition, waypointName);
        //     rc.SetAutoPilotEnabled(true);
        // }


        // public void DebugFlight_AB(string gpsString)
        // {
        //     if (_controller == null) return;
        //     string reason;
        //     if (!_controller.CanRunDebugFlight(out reason))
        //     {
        //         Log.Warning("Drone {0} DebugFlight_AB blocked: {1}", Entity.EntityId, reason);
        //         return;
        //     }
        //     Vector3D? target = ParseGPSString(gpsString);
        //     if (!target.HasValue)
        //     {
        //         Log.Warning("Drone {0} DebugFlight_AB invalid GPS: {1}", Entity.EntityId, gpsString ?? "<null>");
        //         return;
        //     }
        //     Vector3D targetPosition = target.Value;
        //     StartDebugAutopilotFlight(ref targetPosition, "Debug A-B");
        //     Log.Info("Drone {0} DebugFlight_AB started: {1}", Entity.EntityId, targetPosition);
        // }

        private static string ConvertVectorToGPS(string name, Vector3D position)
        {
            return string.Format("GPS:{0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:",
                name, position.X, position.Y, position.Z);
        }

        private static Vector3D? ParseGPSString(string gpsString)
        {
            if (string.IsNullOrEmpty(gpsString)) return null;
            var parts = gpsString.Split(':');
            if (parts.Length < 5 || !parts[0].Equals("GPS", StringComparison.OrdinalIgnoreCase)) return null;
            double x, y, z;
            if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
                double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y) &&
                double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
                return new Vector3D(x, y, z);
            return null;
        }
        #endregion

        #region Terminal Properties
        public long Terminal_EntityId
        {
            get { return Entity != null ? Entity.EntityId : 0L; }
        }

        public bool Terminal_Enabled
        {
            get { return settings != null && settings.IsEnabled; }
            set { if (settings != null) { settings.IsEnabled = value; SaveSettings(); } }
        }

        public bool Terminal_EnableInertialDampening
        {
            get { return settings != null && settings.EnableInertialDampening; }
            set { if (settings != null) { settings.EnableInertialDampening = value; SaveSettings(); } } 
        }

        public long Terminal_OperationModeValue
        {
            get { return settings != null ? (long)settings.OperationMode : 0L; }
            set { if (settings != null) { settings.OperationMode = (OperationMode)value; SaveSettings(); } }
        }
        public StringBuilder Terminal_HomePositionGPS
        {
            get
            {
                if (settings == null) return new StringBuilder("");
                return new StringBuilder(ConvertVectorToGPS("Drone Home", settings.HomePosition));
            }
            set
            {
                if (settings == null) return;
                Vector3D? pos = ParseGPSString(value != null ? value.ToString() : null);
                if (pos.HasValue) { settings.HomePosition = pos.Value; SaveSettings(); }
            }
        }

        public void Terminal_SetCurrentAsHome()
        {
            if ( settings != null)
            {
                // _controller.SetHomeOrientationToCurrent();
                SaveSettings();
            }
        }

        public bool Terminal_UseTaskTypeFilters
        {
            get { return settings != null && settings.UseTaskTypeFilters; }
            set { if (settings != null) { settings.UseTaskTypeFilters = value; SaveSettings(); } }
        }

        public Orchestrator.TaskType Terminal_TaskTypeFilters
        {
            get { return settings != null ? settings.TaskTypeFilters : 0; }
            set { if (settings != null) { settings.TaskTypeFilters = value; SaveSettings(); } }
        }

        public bool Terminal_UseCapabilityFilters
        {
            get { return settings != null && settings.UseCapabilityFilters; }
            set { if (settings != null) { settings.UseCapabilityFilters = value; SaveSettings(); } }
        }

        public Capabilities Terminal_CapabilityFilters
        {
            get { return settings != null ? settings.CapabilityFilters : Capabilities.None; }
            set { if (settings != null) { settings.CapabilityFilters = value; SaveSettings(); } }
        }

        public float Terminal_WaypointTolerance
        {
            get { return settings != null ? settings.WaypointTolerance : 5f; }
            set { if (settings != null) { settings.WaypointTolerance = MathHelper.Clamp(value, 1f, 50f); SaveSettings(); } }
        }

        public float Terminal_ApproachSpeed
        {
            get { return settings != null ? settings.ApproachSpeed : 5f; }
            set { if (settings != null) { settings.ApproachSpeed = MathHelper.Clamp(value, 1f, 50f); SaveSettings(); } }
        }

        public float Terminal_SpeedLimit
        {
            get { return settings != null ? settings.SpeedLimit : 50f; }
            set { if (settings != null) { settings.SpeedLimit = MathHelper.Clamp(value, 10f, 100f); SaveSettings(); } }
        }

        public float Terminal_DockingSpeed
        {
            get { return settings != null ? settings.DockingSpeed : 2.5f; }
            set { if (settings != null) { settings.DockingSpeed = MathHelper.Clamp(value, 0.5f, 10f); SaveSettings(); } }
        }

        public bool Terminal_AlignToPGravity
        {
            get { return settings != null && settings.AlignToPGravity; }
            set { if (settings != null) { settings.AlignToPGravity = value; SaveSettings(); } }
        }

        public float Terminal_MaxPitchDegrees
        {
            get { return settings != null ? settings.PGravityAlignMaxPitchDegrees : 10f; }
            set { if (settings != null) { settings.PGravityAlignMaxPitchDegrees = MathHelper.Clamp(value, 0f, 45f); SaveSettings(); } }
        }

        public float Terminal_MaxRollDegrees
        {
            get { return settings != null ? settings.PGravityAlignMaxRollDegrees : 10f; }
            set { if (settings != null) { settings.PGravityAlignMaxRollDegrees = MathHelper.Clamp(value, 0f, 45f); SaveSettings(); } }
        }

        public StringBuilder Terminal_LCDScreenTag
        {
            get { return settings != null ? new StringBuilder(settings.LCDScreenTag) : new StringBuilder(""); }
            set { if (settings != null) { settings.LCDScreenTag = value != null ? value.ToString() : "[IAIDrone]"; SaveSettings(); } }
        }

        public bool Terminal_MonitorHydrogen
        {
            get { return settings != null && settings.MonitorHydrogenLevels; }
            set { if (settings != null) { settings.MonitorHydrogenLevels = value; SaveSettings(); } }
        }

        public float Terminal_H2RefuelThreshold
        {
            get { return settings != null ? settings.HydrogenRefuelThreshold : 25f; }
            set { if (settings != null) { settings.HydrogenRefuelThreshold = MathHelper.Clamp(value, 5f, 50f); SaveSettings(); } }
        }

        public float Terminal_H2OperationalThreshold
        {
            get { return settings != null ? settings.HydrogenOperationalThreshold : 50f; }
            set { if (settings != null) { settings.HydrogenOperationalThreshold = MathHelper.Clamp(value, 10f, 95f); SaveSettings(); } }
        }

        public bool Terminal_AlwaysRefuel
        {
            get { return settings != null && settings.AlwaysRefuelWhenDocked; }
            set { if (settings != null) { settings.AlwaysRefuelWhenDocked = value; SaveSettings(); } }
        }

        public bool Terminal_MonitorBattery
        {
            get { return settings != null && settings.MonitorBatteryLevels; }
            set { if (settings != null) { settings.MonitorBatteryLevels = value; SaveSettings(); } }
        }

        public float Terminal_BatteryRefuelThreshold
        {
            get { return settings != null ? settings.BatteryRefuelThreshold : 20f; }
            set { if (settings != null) { settings.BatteryRefuelThreshold = MathHelper.Clamp(value, 5f, 50f); SaveSettings(); } }
        }

        public float Terminal_BatteryOperationalThreshold
        {
            get { return settings != null ? settings.BatteryOperationalThreshold : 80f; }
            set { if (settings != null) { settings.BatteryOperationalThreshold = MathHelper.Clamp(value, 10f, 95f); SaveSettings(); } }
        }

        public long Terminal_ControllerForwardDirectionValue
        {
            get { return settings != null ? (long)settings.ControllerForwardDirection : (long)Base6Directions.Direction.Forward; }
            set { if (settings != null) { settings.ControllerForwardDirection = (Base6Directions.Direction)value; SaveSettings(); } }
        }
        #endregion
        #region State helpers
        private bool AnyLandingGearLocked()
        {
            foreach (var landingGear in landingGears)
                if (landingGear.IsLocked) return true;
            return false;
        }
        private bool AnyConnectorsConnected()
        {
            foreach (var connector in connectors)
                if (connector.IsConnected) return true;
            return false;
        }
        private void UpdateMass()
        {
            var massInfo = shipController.CalculateShipMass();
            physicalMass = massInfo.PhysicalMass;
            baseMass = massInfo.BaseMass;
        }
        #endregion

        #region Static Helpers
        private static bool HasAnyGyros(Dictionary<Base6Directions.Direction, List<IMyGyro>> gyroscopes)
        {
            foreach (var kvp in gyroscopes)
                if (kvp.Value.Count > 0) return true;
            return false;
        }

        private static bool HasAnyThrusters(Dictionary<Base6Directions.Direction, List<IMyThrust>> thrusters)
        {
            if (thrusters.Count == 0) return false;
            foreach (var kvp in thrusters)
                if (kvp.Value.Count > 0) return true;
            return false;
        }
        private static float CalculateMaxLoadInG(
            ThrustProfile baseThrustProfile,
            ThrustProfile h2ThrustProfile,
            float mass,
            Base6Directions.Direction direction = Base6Directions.Direction.Up,
            float gravityNormal = 9.81f
        )
        {
            float upAccel;
            switch (direction)
            {
                case Base6Directions.Direction.Up:
                    upAccel = (baseThrustProfile.Up.Max + h2ThrustProfile.Up.Max) / mass;
                    break;
                case Base6Directions.Direction.Down:
                    upAccel = (baseThrustProfile.Down.Max + h2ThrustProfile.Down.Max) / mass;
                    break;
                case Base6Directions.Direction.Left:
                    upAccel =  (baseThrustProfile.Left.Max + h2ThrustProfile.Left.Max) / mass;
                    break;
                case Base6Directions.Direction.Right:
                    upAccel =  (baseThrustProfile.Right.Max + h2ThrustProfile.Right.Max) / mass;
                    break;
                case Base6Directions.Direction.Backward:
                    upAccel =  (baseThrustProfile.Backward.Max + h2ThrustProfile.Backward.Max) / mass;
                    break;
                case Base6Directions.Direction.Forward:
                    upAccel =  (baseThrustProfile.Forward.Max + h2ThrustProfile.Forward.Max) / mass;
                    break;
                default: // up
                    upAccel =  (baseThrustProfile.Up.Max + h2ThrustProfile.Up.Max) / mass;
                    break;
            }
            if (upAccel <= gravityNormal) return 0f;
            return (upAccel - gravityNormal) * mass / gravityNormal;  
        }

        // Table dimensions: [ControllerForward, BlockForward]
        private static readonly Base6Directions.Direction[,] RelativeDirectionMatrix = new Base6Directions.Direction[6, 6]
        {
            // Controller = Forward
            { Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
            // Controller = Backward
            { Base6Directions.Direction.Backward, Base6Directions.Direction.Forward, Base6Directions.Direction.Right, Base6Directions.Direction.Left, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
            // Controller = Left
            { Base6Directions.Direction.Right, Base6Directions.Direction.Left, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
            // Controller = Right
            { Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Backward, Base6Directions.Direction.Forward, Base6Directions.Direction.Up, Base6Directions.Direction.Down },
            // Controller = Up
            { Base6Directions.Direction.Down, Base6Directions.Direction.Up, Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward },
            // Controller = Down
            { Base6Directions.Direction.Up, Base6Directions.Direction.Down, Base6Directions.Direction.Left, Base6Directions.Direction.Right, Base6Directions.Direction.Backward, Base6Directions.Direction.Forward }
        };

        private static Base6Directions.Direction GetRelativeDirection(
            Base6Directions.Direction blockForward,
            Base6Directions.Direction controllerForward)
        {
            return RelativeDirectionMatrix[(int)controllerForward, (int)blockForward];
        }
        private static ThrustProfile CalculateThrustProfile(IMyShipController shipController, float currentMass, Dictionary<Base6Directions.Direction, List<IMyThrust>> thrusters)
        {
            ThrustProfile thrustProfile = new ThrustProfile();

            if (shipController == null) return thrustProfile;
            if (currentMass < 0.1f) return thrustProfile;
            if (thrusters == null || thrusters.Count == 0) return thrustProfile;

            foreach (var kvp in thrusters)
            {
                float dirThrust = 0f;
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    IMyThrust t = kvp.Value[i];
                    if (t.IsWorking && t.IsFunctional) dirThrust += t.MaxEffectiveThrust;
                }
                switch (kvp.Key)
                {
                    case Base6Directions.Direction.Forward:
                        thrustProfile.Forward = new DirectionalValue(0.0f, dirThrust, kvp.Key);
                        break;
                    case Base6Directions.Direction.Backward:
                        thrustProfile.Backward = new DirectionalValue(0.0f, dirThrust, kvp.Key);
                        break;
                    case Base6Directions.Direction.Up:
                        thrustProfile.Up = new DirectionalValue(0.0f, dirThrust, kvp.Key);
                        break;
                    case Base6Directions.Direction.Down:
                        thrustProfile.Down = new DirectionalValue(0.0f, dirThrust, kvp.Key);
                        break;
                    case Base6Directions.Direction.Left:
                        thrustProfile.Left = new DirectionalValue(0.0f, dirThrust, kvp.Key);
                        break;
                    case Base6Directions.Direction.Right:
                        thrustProfile.Right = new DirectionalValue(0.0f, dirThrust, kvp.Key);
                        break;
                }
            }
            if (currentMass < 0.1f)
            {
                thrustProfile.Valid = false;
                return thrustProfile;
            }

            thrustProfile.Valid = true;
            return thrustProfile;
        }
        #endregion
    }
}
