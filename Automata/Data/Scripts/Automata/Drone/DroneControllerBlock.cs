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
using VRageMath;

using Automata.Util.Logging;
using Automata.Pathfinding;
using Automata.Util;
using Automata.VirtualNetwork;
using Sandbox.Definitions;

namespace Automata.Drone
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RemoteControl), false,
        "AutomataSmallDroneController",
        "AutomataLargeDroneController")]
    public class DroneControllerBlock : MyGameLogicComponent
    {
        private struct FlightState
        {
            public MatrixD WorldMatrix, WorldMatrixToLocal; // WorldToLocal = transpose; use with TransformNormal only
            public Vector3D Position, LinearVelocity, AngularVelocity, Gravity, GravityUp;
            public bool InGravity;
        }
        public struct ToolMount
        {
            public IMyCubeBlock Block;
            public Vector3D LocalPoint;     // working point, controller-local
            public Vector3D LocalForward, LocalUp;
            public double WorkRadius;       // welder/grinder sphere; 0 for connectors
        }
        private ToolMount welderMount, grinderMount;
        private readonly List<ToolMount> connectorMounts = new List<ToolMount>();
        private FlightState flightState;
        private IMyUtilitiesDelegate utilitiesDelegate;
        private IMySessionDelegate sessionDelegate;


        private long _entityId;
        private int _messageCounter = 0;
        private int _bidCounter = 0;
        private IMyEntity entity;
        private IMyCubeBlock block;
        private IMyGamePruningStructureDelegate pruningStructureDelegate;
        private IMyPlanetDelegate planetDelegate;
        private readonly List<IMyCubeGrid> _gridCache = new List<IMyCubeGrid>();
        private readonly List<IMySlimBlock> _blockCache = new List<IMySlimBlock>();

        private TerminalDisplayManager terminalLogger;

        
        private DroneControllerSettings settings;
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
        private struct GyroMapping
        {
            public IMyGyro Gyro;
            public int PitchSrc, YawSrc, RollSrc;
            public int PitchSign, YawSign, RollSign;
        }
        private readonly List<GyroMapping> gyroscopes = new List<GyroMapping>();
        // Controller-local axes, in command order: pitch = Right (+X), yaw = Up (+Y), roll = Backward (+Z)
        private static readonly Base6Directions.Direction[] ControllerAxes =
        {
            Base6Directions.Direction.Right, Base6Directions.Direction.Up, Base6Directions.Direction.Backward
        };
        private readonly List<IMyThrust>[] hydrogenThrusters = new List<IMyThrust>[6];
        private readonly List<IMyThrust>[] atmoThrusters = new List<IMyThrust>[6];
        private readonly List<IMyThrust>[] ionThrusters = new List<IMyThrust>[6];
        // cache to prevent recalculating every time
        private readonly List<IMyThrust>[] allThrusters = new List<IMyThrust>[6];

        private ThrustProfile h2ThrustProfile;
        private ThrustProfile ionThrustProfile;
        private ThrustProfile atmoThrustProfile;
        private ThrustProfile combinedThrustProfile;
        private readonly List<IMyGasTank> hydrogenTanks = new List<IMyGasTank>();
        private readonly List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
        private readonly List<IMyCargoContainer> cargoContainers = new List<IMyCargoContainer>();

        private Vector3D gravityVector = Vector3D.Zero;
        private float physicalMass = 0.0f;
        private float baseMass = 0.0f;

        private Orchestrator.Job currentTask;
        private Queue<Orchestrator.Task> taskQueue = new Queue<Orchestrator.Task>();
        private ushort currentTaskId = 0;

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
        private int COMPONENT_CHECK_INTERVAL_MIN_TICKS = 3600; // 60 second default;
        private int lastComponentCheckFrame = 0;
        #endregion
        
        #region Fields - Navigation
        private Vector3D taskPosition;
        private List<Vector3D> currentPath = new List<Vector3D>();
        // private PathfindingManager pathfindingManager;
        private FlightOrder activeFlightOrder;
        private Vector3D currentWaypoint;
        private Vector3DData navigationTarget;
        private Vector3D? navDebugTarget;
        private Vector3D? navDebugTargetApproachFrom;

        private bool gyroOverrideActive = false;
        private bool thrustOverridesActive = false;
        private Vector3D naturalGravity = Vector3D.Zero;

        private const double Kp = 1.0d;
        private double integralForward = 0.0;
        private double integralUp = 0.0;
        private double integralRight = 0.0;
        private const double INTEGRAL_GAIN = 0.1d;   // Ki — start small, tune up until drift disappears without overshoot
        private const double INTEGRAL_MAX = 0.5d;     // anti-windup clamp, same units as the *GAIN term it's added to
        private const double FINAL_LEG    = 10.0;  // m: last stretch at ApproachSpeed; orientation must be settled here
        private const double XT_GAIN      = 0.5;   // 1/s: sideways correction speed per metre off the line
        private const double XT_CAPTURE   = 1.0;   // m: counts as "on the line"
        private const double TRANS_BRAKE  = 0.5;   // fraction of available accel used to plan the stop
        private const double TRANS_LINEAR = 1.0;   // m: linear zone near the target
        private const double ORIENT_GATE  = 0.05;  // rad (~3°): max orientation error allowed on the final leg
        private const double ARRIVE_SPEED = 0.1;   // m/s
        private const double MAX_DESCENT_SPEED = 10.0;   // m/s along gravity
        private const double ACCEL_FRACTION    = 0.5;    // share of available accel used to change the command
        private Vector3D commandedVelocity;              // rate-limited velocity command
        #endregion

        #region Field - rotation
        private Vector3D lastAngularVelocity;
        private double rotAlphaEstimate = 0.5;
        private double lastBoxAlpha = 0;
        private Vector3D orientationTarget;
        private Vector3D orientationTargetDebug;
        private bool orientationTargetSet;
        private const double ROT_MAX_SPEED    = 1.0;   // rad/s, keep below the gyro/world angular speed cap
        private const double ROT_BRAKE_FACTOR = 0.2;   // plan the stop using 20% of α, to absorb lag and estimation error
        private const double ROT_LINEAR_ZONE  = 0.02;  // rad (~1.1°): switch to linear near the target, since √ is too steep near zero
        private const double ROT_DONE_ANGLE   = 0.01;  // rad (~0.6°)
        private const double ROT_DONE_SPEED   = 0.02;  // rad/s
        private const double GYRO_SIGN        = -1.0;   // one global sign, see test 2
        #endregion

        public DroneControllerBlock(){
            this.utilitiesDelegate = new MyUtilitiesDelegate();
            // this.settings = settings ?? new DroneControllerSettings();
            // this.operationMode = settings.OperationMode;
            this.utilitiesDelegate = utilitiesDelegate ?? new MyUtilitiesDelegate();
            this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
            // this.pruningStructureDelegate = new MyGamePruningStructureDelegate();
            // this.planetDelegate = planetDelegate ?? new MyPlanetDelegate();

            foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                ionThrusters[(int)dir] = new List<IMyThrust>();
                atmoThrusters[(int)dir] = new List<IMyThrust>();
                hydrogenThrusters[(int)dir] = new List<IMyThrust>();
                allThrusters[(int)dir] = new List<IMyThrust>();
            }          
        }

        // public DroneControllerBlock(
        //     DroneControllerSettings settings = null,
        //     MessageQueue messaging = null,
        //     IMyUtilitiesDelegate utilitiesDelegate = null,
        //     IMySessionDelegate sessionDelegate = null,
        //     IMyGamePruningStructureDelegate pruningStructure = null,
        //     IMyPlanetDelegate planetDelegate = null)
        // {
        //     this.settings = settings ?? new DroneControllerSettings();
        //     this.messaging = messaging ?? AutomataSession.GetMessageQueue();
        //     this.operationMode = settings.OperationMode;
        //     this.utilitiesDelegate = utilitiesDelegate ?? new MyUtilitiesDelegate();
        //     this.sessionDelegate = sessionDelegate ?? new MySessionDelegate();
        //     this.pruningStructureDelegate = pruningStructure ?? new MyGamePruningStructureDelegate();
        //     this.planetDelegate = planetDelegate ?? new MyPlanetDelegate();

        //     foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
        //     {
        //         gyroscopes[dir] = new List<IMyGyro>();
        //         ionThrusters[dir] = new List<IMyThrust>();
        //         atmoThrusters[dir] = new List<IMyThrust>();
        //         hydrogenThrusters[dir] = new List<IMyThrust>();
        //         allThrusters[dir] = new List<IMyThrust>();
        //     }
        // }

        #region Core API Lifecycle
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            entity = Entity;
            if (Entity.Storage == null)
                Entity.Storage = new MyModStorageComponent();
            block = (IMyCubeBlock)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            DroneControllerTerminalControls.DoOnce(ModContext);
            
            if (block?.CubeGrid?.Physics == null)
                return;

            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            try
            {
                if (!initialized)
                    return;
                CaptureFlightState();
                if (thrustOverridesActive)
                    LearnThrustGains();
                else
                    lastLinearVelocity = flightState.LinearVelocity;
                double orientationError = 0;
                bool orienting = false;
                Vector3D fwd, up;
                if (activeFlightOrder != null)
                {
                    BuildOrderFrame(activeFlightOrder, out fwd, out up);
                    UpdateOrientation(ref fwd, ref up, out orientationError);
                    orienting = true;
                }
                else if (orientationTargetSet)
                {
                    BuildAimFrame(ref orientationTarget, out fwd, out up);
                    if (UpdateOrientation(ref fwd, ref up, out orientationError))
                    {
                        orientationTargetSet = false;
                        orientationTarget = Vector3D.Zero;
                    }
                    orienting = true;
                }
                if (!orienting && gyroOverrideActive)
                    ReleaseGyroscopes();

                if (activeFlightOrder != null)
                {
                    Vector3D v = RateLimitCommand(ComputeApproachVelocity(activeFlightOrder, orientationError));
                    UpdateTranslation(ref v);
                    AdvanceFlightPhase(activeFlightOrder, orientationError);

                    Vector3D rWorld = Vector3D.TransformNormal(activeFlightOrder.ReferenceOffset.ToVector3D(), flightState.WorldMatrix);
                    double tol = activeFlightOrder.ArrivalTolerance;
                    activeFlightOrder.Arrived = activeFlightOrder.Phase == FlightPhase.Approach
                        && Vector3D.DistanceSquared(flightState.Position + rWorld, activeFlightOrder.Target.ToVector3D()) < tol * tol
                        && flightState.LinearVelocity.LengthSquared() < ARRIVE_SPEED * ARRIVE_SPEED
                        && orientationError < ORIENT_GATE;
                }
                else if (settings.EnableInertialDampening)
                {
                    Vector3D zero = Vector3D.Zero;
                    UpdateTranslation(ref zero);
                }
                else if (thrustOverridesActive)
                {
                    ClearAllThrustOverrides();
                }
            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneControllerBlock {0}: UpdateBeforeSimulation error: {1}", Entity.EntityId, ex);
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();
            try
            {
                if (!initialized)
                {
                    Initialize();
                    return;
                }
                if (initialized)
                {
                    if (!AnyConnectorsConnected() && !AnyLandingGearLocked())
                    {
                        combinedThrustProfile = CalculateThrustProfile(shipController, physicalMass, allThrusters);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("DroneControllerBlock {0}: UpdateBeforeSimulation10 error: {1}", Entity.EntityId, ex);
            }
        }

        public override void UpdateBeforeSimulation100()
        {
            base.UpdateBeforeSimulation100();
            try
            {
                if (!initialized)
                    return;
                UpdateMass();
                ReportThrustGains();
                var currentFrame = sessionDelegate.GameplayFrameCounter;
                if (currentFrame - lastComponentCheckFrame > COMPONENT_CHECK_INTERVAL_MIN_TICKS)
                {
                    lastComponentCheckFrame = currentFrame;
                    CheckCapabilities();
                }
                // SaveSettings();
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
                SaveSettings();
                Shutdown();

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
                Log.Error("DroneControllerBlock {0}: SaveSettings error: {1}", Entity?.EntityId, ex);
            }
        }
        private void LoadSettings()
        {
            try
            {
                if (Entity?.Storage == null || utilitiesDelegate == null) 
                {
                    terminalLogger.Echo("failed to load settings");
                    return;
                }

                string base64Data;
                if (!Entity.Storage.TryGetValue(AutomataSession.MOD_GUID, out base64Data) || string.IsNullOrEmpty(base64Data))
                {
                    settings = new DroneControllerSettings(); 
                    return;
                }

                byte[] bytes = Convert.FromBase64String(base64Data);

                settings = utilitiesDelegate.SerializeFromBinary<DroneControllerSettings>(bytes);
            }
            catch (Exception ex)
            {
                Log.Error("DroneControllerBlock {0}: LoadSettings error: {1}", Entity?.EntityId, ex);
                // Fallback to safe default settings on deserialization failure to prevent null references
                settings = new DroneControllerSettings(); 
            }
        }
        #endregion

        #region Initialization
        public void Initialize()
        {
            Log.Debug("initializing drone {0}", entity.EntityId);
            if (initialized){
                terminalLogger.Echo("drone {0}: already initialized", _entityId);
                return;
            }
            _entityId = entity.EntityId;
            this.utilitiesDelegate = new MyUtilitiesDelegate();
            this.sessionDelegate = new MySessionDelegate();
            if (terminalLogger == null)
                terminalLogger = new TerminalDisplayManager(block as IMyTerminalBlock, 10);
            this.pruningStructureDelegate = new MyGamePruningStructureDelegate();
            this.planetDelegate =  new MyPlanetDelegate();
            LoadSettings();



            Log.Debug("Terminal logger assigned");
            currentState = State.Initializing;

            // STATUS_REPORT_INTERVAL_TICKS = AutomataSession.GetConfig().MessageQueue.DroneMessageThrottlingTicks();
            POWER_CHECK_INTERVAL_MIN_TICKS = TimeUtil.SecondsToTicks(AutomataSession.GetConfig().Drone.PowerCheckIntervalMinSeconds);
            COMPONENT_CHECK_INTERVAL_MIN_TICKS = TimeUtil.SecondsToTicks(AutomataSession.GetConfig().Drone.ComponentCheckIntervalMinSeconds);

            shipController = entity as IMyShipController;
            if (shipController == null)
            {
                terminalLogger.Echo("ERROR: entity is not a ship controller", _entityId);
                currentState = State.Error;
                return;
            }

            if (!CheckCapabilities())
            {
                terminalLogger.Echo("ERROR: failed capability check", _entityId);
                currentState = State.Error;
                return;
            }

            // pathfindingManager = new PathfindingManager(AutomataSession.GetConfig().Pathfinding, pruningStructureDelegate, planetDelegate);

            if (primaryAntenna != null)
            {
                AutomataSession.GetMessageQueue().RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
            }

            if (settings.OperationMode == OperationMode.ManagedByScheduler)
            {
                AutomataSession.GetMessageQueue().Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_START);
                AutomataSession.GetMessageQueue().Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
            }

            UpdateMass();
            currentState = State.Standby;
            initialized = true;
            terminalLogger.Echo("Initialized in mode: {1}", _entityId, settings.OperationMode);
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
            UpdateNaturalGravity();

            for (int i = 0; i < 6; i++) // one for each of Base6Directions.Direction
            {
                hydrogenThrusters[i].Clear();
                atmoThrusters[i].Clear();
                ionThrusters[i].Clear();
                allThrusters[i].Clear();
            }
            gyroscopes.Clear();
            landingGears.Clear();
            cargoContainers.Clear();
            sensors.Clear();
            hydrogenTanks.Clear();
            batteries.Clear();
            connectors.Clear();
            connectorMounts.Clear();
            welderMount = default(ToolMount);
            grinderMount = default(ToolMount);
            welder = null;
            grinder = null;
            primaryAntenna = null;
            lastGyroCmd = new Vector3D(double.NaN, double.NaN, double.NaN);

            CaptureFlightState();
            var controllerMatrix = shipController.Orientation;
            _gridCache.Clear();
            cubeBlock.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(_gridCache);

            foreach (var grid in _gridCache)
            {
                _blockCache.Clear();
                grid.GetBlocks(_blockCache);

                foreach (var slimBlock in _blockCache)
                {
                    var fatBlock = slimBlock.FatBlock;
                    if (fatBlock == null || !fatBlock.IsFunctional) continue;

                    if (fatBlock is IMyShipConnector)
                    {
                        connectors.Add((IMyShipConnector)fatBlock);
                        connectorMounts.Add(BuildMount((IMyShipConnector)fatBlock, ref flightState.WorldMatrixToLocal, ref flightState.Position));
                    }
                    else if (fatBlock is IMyShipWelder && welder == null)
                    {
                        welder = (IMyShipWelder)fatBlock;
                        welderMount = BuildMount(fatBlock, ref flightState.WorldMatrixToLocal, ref flightState.Position);
                    }
                    else if (fatBlock is IMyShipGrinder && grinder == null)
                    {
                        grinder = (IMyShipGrinder)fatBlock;
                        grinderMount = BuildMount(fatBlock, ref flightState.WorldMatrixToLocal, ref flightState.Position);
                    }
                    else if (fatBlock is IMyLandingGear)
                        landingGears.Add((IMyLandingGear)fatBlock);
                    else if (fatBlock is IMyGyro)
                    {
                        var gyro = (IMyGyro)fatBlock;
                        if (gyro.CubeGrid == shipController.CubeGrid) // block orientation is only comparable within one grid
                            gyroscopes.Add(BuildGyroMapping(gyro, controllerMatrix));
                    }
                    else if (fatBlock is IMyThrust)
                    {
                        var thruster = (IMyThrust)fatBlock;
                        var relDir = DetermineThrusterDirection(ref thruster, ref flightState.WorldMatrixToLocal);

                        // Classify by definition: vanilla ion subtypes (e.g. LargeBlockLargeThrust) don't contain "Ion"
                        var thrustDef = MyDefinitionManager.Static.GetCubeBlockDefinition(thruster.BlockDefinition) as MyThrustDefinition;
                        string thrusterType = thrustDef != null ? thrustDef.ThrusterType.String : string.Empty;
                        if (thrusterType == "Hydrogen")
                            hydrogenThrusters[(int)relDir].Add(thruster);
                        else if (thrusterType == "Atmospheric")
                            atmoThrusters[(int)relDir].Add(thruster);
                        else
                            ionThrusters[(int)relDir].Add(thruster);   // "Ion" and modded types

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
            bool hasMinimum = gyroscopes.Count > 0 &&
                (
                    HasAnyDirectionalComponent(hydrogenThrusters) ||
                    HasAnyDirectionalComponent(ionThrusters) ||
                    HasAnyDirectionalComponent(atmoThrusters)
                ) && connectors.Count > 0;
            for (int i = 0; i < 6; i++)
            {
                allThrusters[i].AddRange(ionThrusters[i]);
                allThrusters[i].AddRange(atmoThrusters[i]);
                allThrusters[i].AddRange(hydrogenThrusters[i]);
            }
            for (int i = 0; i < 6; i++)
                directionalThrustCache[i] = -1.0d;
            if (!hasMinimum){
                ClearAllThrustOverrides();   // lists are rebuilt already, so this reaches every current thruster
                ReleaseGyroscopes();         // and hands the drone back to the game's own dampeners
                currentState = State.Error;
                return false;
            }


            h2ThrustProfile = CalculateThrustProfile(shipController, physicalMass, hydrogenThrusters);
            atmoThrustProfile = CalculateThrustProfile(shipController, physicalMass, atmoThrusters);
            ionThrustProfile = CalculateThrustProfile(shipController, physicalMass, ionThrusters);
            combinedThrustProfile = CalculateThrustProfile(shipController, physicalMass, allThrusters);

            capabilities = Capabilities.None;
            behaviourProfile = BehaviourProfile.None;
            if (sensors.Count > 0) capabilities|= Capabilities.HasSensors;
            if (settings.AlwaysRefuelWhenDocked && connectors.Count > 0)
                behaviourProfile|= BehaviourProfile.RefuelWhenDocked | BehaviourProfile.RechargeWhenDocked;

            var hasAnyAtmoThrusters = HasAnyDirectionalComponent<IMyThrust>(atmoThrusters);
            var hasAnyHydrogenThrusters = HasAnyDirectionalComponent<IMyThrust>(hydrogenThrusters);
            var hasAnyIonThrusters = HasAnyDirectionalComponent<IMyThrust>(ionThrusters);

            CalibrateRotationCapabilities();

            if (hasAnyAtmoThrusters || hasAnyHydrogenThrusters)
            {
                var max1GLoad = CalculateMaxLoadInG(
                    atmoThrustProfile, h2ThrustProfile, physicalMass,
                    Base6Directions.Direction.Up, gravityNormal:9.81f);
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
        private void CalibrateRotationCapabilities()
        {
            if (shipController == null) return;
            double torque = 0;
            for (int i = 0; i < gyroscopes.Count; i++)
            {
                var g = gyroscopes[i].Gyro;
                if (!g.IsFunctional || !g.Enabled) continue;

                var def = MyDefinitionManager.Static.GetCubeBlockDefinition(g.BlockDefinition) as MyGyroDefinition;
                if (def != null)
                    torque += def.ForceMagnitude * g.GyroPower;
            }

            Vector3D size = shipController.CubeGrid.LocalAABB.Size;
            double a = Math.Max(size.X, Math.Max(size.Y, size.Z));
            double c = Math.Min(size.X, Math.Min(size.Y, size.Z));
            double b = size.X + size.Y + size.Z - a - c;
            double inertia = physicalMass * (a*a+b*b) / 12.0;
            if (inertia > 0 && torque > 0)
            {
                double boxAlpha = torque / inertia;
                rotAlphaEstimate = lastBoxAlpha > 0 ? rotAlphaEstimate * (boxAlpha / lastBoxAlpha) : boxAlpha;
                lastBoxAlpha = boxAlpha;
            }
        }
        private Base6Directions.Direction DetermineThrusterDirection(ref IMyThrust thruster, ref MatrixD worldToController)
        {
            Vector3D localPush = Vector3D.TransformNormal(thruster.WorldMatrix.Backward, worldToController);
            return Base6Directions.GetClosestDirection((VRageMath.Vector3)localPush);
        }
        private static GyroMapping BuildGyroMapping(IMyGyro gyro, MyBlockOrientation controllerOrientation)
        {
            var m = new GyroMapping { Gyro = gyro };
            for (int k = 0; k < 3; k++)
            {
                var gridDir = controllerOrientation.TransformDirection(ControllerAxes[k]);   // controller-local -> grid
                var gyroDir = gyro.Orientation.TransformDirectionInverse(gridDir);          // grid -> gyro-local
                Vector3I v  = Base6Directions.GetIntVector(gyroDir);                        // exactly one component is ±1
                if (v.X != 0)      { m.PitchSrc = k; m.PitchSign = v.X; }
                else if (v.Y != 0) { m.YawSrc   = k; m.YawSign   = v.Y; }
                else               { m.RollSrc  = k; m.RollSign  = v.Z; }
            }
            return m;
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
            ResetIntegralTerms();
            ReleaseGyroscopes();
            // DisableGyroscopeOverride();
            _consecutiveErrors = 0;
            currentState = State.Standby;
        }

        #region Gyro and rotation controls
        private Vector3D lastGyroCmd = new Vector3D(double.NaN, double.NaN, double.NaN);
        // cmd: rad/s in the controller's frame — X = pitch, Y = yaw, Z = roll
        private void ApplyGyroscopesCommand(ref Vector3D cmd)
        {
            if (Vector3D.DistanceSquared(cmd, lastGyroCmd) < 1e-6)
                return;   // < 0.001 rad/s change
            lastGyroCmd = cmd;
            for (int i = 0; i < gyroscopes.Count; i++)
            {
                var m = gyroscopes[i];
                if (!m.Gyro.IsWorking) continue;
                m.Gyro.Pitch = (float)(m.PitchSign * cmd.GetDim(m.PitchSrc));
                m.Gyro.Yaw   = (float)(m.YawSign   * cmd.GetDim(m.YawSrc));
                m.Gyro.Roll  = (float)(m.RollSign  * cmd.GetDim(m.RollSrc));
                if (!m.Gyro.GyroOverride)
                    m.Gyro.GyroOverride = true;
            }
            gyroOverrideActive = true;
        }

        private void ReleaseGyroscopes()
        {
            for (int i = 0; i < gyroscopes.Count; i++)
            {
                var g = gyroscopes[i].Gyro;
                g.Pitch = 0f; g.Yaw = 0f; g.Roll = 0f;
                g.GyroOverride = false;
            }
            gyroOverrideActive = false;
            lastGyroCmd = new Vector3D(double.NaN, double.NaN, double.NaN);
        }
        // gUp = normalized -gravity. fallbackFwd = current forward, used when the request points straight up/down.
        private void ClampToGravityLimits(ref Vector3D fwd, ref Vector3D up, ref Vector3D gUp, ref Vector3D fallbackFwd)
        {
            Vector3D fH = fwd - gUp * Vector3D.Dot(fwd, gUp);
            if (fH.LengthSquared() < 1e-6)
                fH = fallbackFwd - gUp * Vector3D.Dot(fallbackFwd, gUp);
            fH.Normalize();
            double maxPitch = MathHelperD.ToRadians(settings.PGravityAlignMaxPitchDegrees);
            double pitch = MathHelperD.Clamp(Math.Asin(MathHelperD.Clamp(Vector3D.Dot(fwd, gUp), -1, 1)), -maxPitch, maxPitch);
            fwd = fH * Math.Cos(pitch) + gUp * Math.Sin(pitch);

            Vector3D levelUp = Vector3D.Normalize(gUp - fwd * Vector3D.Dot(gUp, fwd));
            Vector3D levelRight = Vector3D.Cross(fwd, levelUp);
            double maxRoll = MathHelperD.ToRadians(settings.PGravityAlignMaxRollDegrees);
            double roll = MathHelperD.Clamp(Math.Atan2(Vector3D.Dot(up, levelRight), Vector3D.Dot(up, levelUp)), -maxRoll, maxRoll);
            up = levelUp * Math.Cos(roll) + levelRight * Math.Sin(roll);
        }
        private void BuildAimFrame(ref Vector3D targetPos, out Vector3D fwd, out Vector3D up)
        {
            MatrixD worldMatrix = flightState.WorldMatrix;
            Vector3D currentFwd = worldMatrix.Forward;
            fwd = Vector3D.Normalize(targetPos - flightState.Position);
            Vector3D g = flightState.Gravity;

            Vector3D gUp = flightState.GravityUp;
            up = gUp;

            FinalizeFrame(ref fwd, ref up, false);
        }
        // Aim forward at a world point. In gravity it asks for zero roll (up = gUp); in space it keeps the current roll.
        private void FinalizeFrame(ref Vector3D fwd, ref Vector3D up, bool ignoreGravityLimits)
        {
            fwd.Normalize();
            if (settings.AlignToPGravity && flightState.InGravity && !ignoreGravityLimits)
            {
                Vector3D gUp = flightState.GravityUp, curFwd = flightState.WorldMatrix.Forward;
                ClampToGravityLimits(ref fwd, ref up, ref gUp, ref curFwd);
                return;
            }
            up -= fwd * Vector3D.Dot(up, fwd);
            if (up.LengthSquared() < 1e-6) up = flightState.WorldMatrix.Backward;
            up.Normalize();
        }
        private void AdvanceFlightPhase(FlightOrder o, double orientationError)
        {
            if (o.Phase == FlightPhase.Transit)
            {
                double tol = settings.WaypointTolerance;
                if (Vector3D.DistanceSquared(flightState.Position, o.ApproachFrom.ToVector3D()) < tol * tol
                    && flightState.LinearVelocity.LengthSquared() < 0.25)            // settled below 0.5 m/s
                {
                    o.Phase = FlightPhase.Align;
                    o.Captured = false;
                }
            }
            else if (o.Phase == FlightPhase.Align
                        && orientationError < ORIENT_GATE
                        && flightState.AngularVelocity.LengthSquared() < ROT_DONE_SPEED * ROT_DONE_SPEED)
            {
                o.Phase = FlightPhase.Approach;
                o.Captured = false;
            }
        }
        private void BuildOrderFrame(FlightOrder o, out Vector3D fwd, out Vector3D up)
        {
            if (o.Phase == FlightPhase.Transit)
            {
                fwd = o.ApproachFrom.ToVector3D() - o.TransitStart.ToVector3D();
                if (fwd.LengthSquared() < 1e-6) fwd = flightState.WorldMatrix.Forward;
                up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
                FinalizeFrame(ref fwd, ref up, false);
                return;
            }
            switch (o.Orientation)
            {
                case FlightOrientationMode.LookAt:
                    fwd = o.LookAtPoint.ToVector3D() - flightState.Position;
                    up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
                    break;
                case FlightOrientationMode.Explicit:
                    fwd = o.Forward.ToVector3D();
                    up = o.Up.ToVector3D();
                    break;
                default:   // FaceTravel
                    fwd = o.Target.ToVector3D() - o.ApproachFrom.ToVector3D();
                    if (fwd.LengthSquared() < 1e-6) fwd = flightState.WorldMatrix.Forward;
                    up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
                    break;
            }
            FinalizeFrame(ref fwd, ref up, o.IgnoreGravityLimits);
        }
        // Returns true when settled on the target orientation.
        private bool UpdateOrientation(ref Vector3D targetFwd, ref Vector3D targetUp, out double angleRemaining)
        {
            MatrixD wm = flightState.WorldMatrix;
            QuaternionD qCur = QuaternionD.CreateFromRotationMatrix(wm);
            QuaternionD qTgt = QuaternionD.CreateFromForwardUp(targetFwd, targetUp);
            // Concatenate(a, b) = "a, then b"  →  the world-frame rotation still left to do
            QuaternionD qErr = QuaternionD.Concatenate(QuaternionD.Inverse(qCur), qTgt);
            if (qErr.W < 0) qErr = -qErr;                            // take the short way round

            Vector3D axis; double angle;
            qErr.GetAxisAngle(out axis, out angle);
            angleRemaining = angle;
            if (angle < 1e-6) axis = Vector3D.Zero;                   // axis is undefined at zero angle

            Vector3D angVel = flightState.AngularVelocity;   // world, rad/s

            if (angle < ROT_DONE_ANGLE && angVel.LengthSquared() < ROT_DONE_SPEED * ROT_DONE_SPEED)
            {
                Vector3D hold = Vector3D.Zero;
                ApplyGyroscopesCommand(ref hold);                     // 0 = hold; gyros only react to disturbances
                lastAngularVelocity = angVel;
                return true;
            }

            // Fastest speed that can still brake to zero within the remaining angle
            double alpha = rotAlphaEstimate * ROT_BRAKE_FACTOR;
            double speed = angle > ROT_LINEAR_ZONE
                ? Math.Sqrt(2 * alpha * angle)
                : angle * Math.Sqrt(2 * alpha / ROT_LINEAR_ZONE);      // matches the curve at the boundary
            speed = Math.Min(speed, ROT_MAX_SPEED);
            Vector3D desiredWorld = axis * speed;

            // Correct α from real behaviour: while the gyros are saturated, measured acceleration ≈ their capability
            if ((desiredWorld - angVel).LengthSquared() > 0.09)       // more than 0.3 rad/s off target → saturated
            {
                double measured = (angVel - lastAngularVelocity).Length() * TimeUtil.TICKS_PER_SECOND;
                rotAlphaEstimate += (measured - rotAlphaEstimate) * 0.05;
            }
            lastAngularVelocity = angVel;

            Vector3D cmd = Vector3D.TransformNormal(desiredWorld, flightState.WorldMatrixToLocal) * GYRO_SIGN;  // X=pitch, Y=yaw, Z=roll
            ApplyGyroscopesCommand(ref cmd);
            return false;
        }
        #endregion

        #region Navigation controls
        public FlightOrder OrderGoTo(Vector3D target, Vector3D? approachFrom = null)
        {
            commandedVelocity = flightState.LinearVelocity;
            Vector3D here = flightState.Position;
            activeFlightOrder = new FlightOrder
            {
                Target = Vector3DData.FromVector3D(target),
                ApproachFrom = Vector3DData.FromVector3D(approachFrom ?? here),
                TransitStart = Vector3DData.FromVector3D(here),
                Phase = approachFrom.HasValue ? FlightPhase.Transit : FlightPhase.Approach,
                Orientation = FlightOrientationMode.FaceTravel,
                ArrivalTolerance = settings.WaypointTolerance
            };
            return activeFlightOrder;
        }
        public FlightOrder OrderPlaceMount(ref ToolMount mount, Vector3D goal, Vector3D toolForward, Vector3D upHint,
                                   Vector3D? approachFrom = null)
        {
            toolForward.Normalize();
            Vector3D upW = upHint - toolForward * Vector3D.Dot(upHint, toolForward);
            if (upW.LengthSquared() < 1e-6) upW = Vector3D.CalculatePerpendicularVector(toolForward);
            upW.Normalize();

            // controller-to-world rotation that makes the mount face toolForward
            MatrixD toolInController  = MatrixD.CreateWorld(Vector3D.Zero, mount.LocalForward, mount.LocalUp);
            MatrixD toolInWorld       = MatrixD.CreateWorld(Vector3D.Zero, toolForward, upW);
            MatrixD controllerInWorld = MatrixD.Transpose(toolInController) * toolInWorld;

            Vector3D offsetWorld = Vector3D.TransformNormal(mount.LocalPoint, controllerInWorld);
            var order = OrderGoTo(goal - offsetWorld,
                                approachFrom.HasValue ? approachFrom.Value - offsetWorld : (Vector3D?)null);
            SetOrderOrientation(controllerInWorld.Forward, controllerInWorld.Up);
            order.IgnoreGravityLimits = true;   // new FlightOrder field: tool alignment beats the pitch/roll limits
            return order;
        }

        public void SetOrderLookAt(Vector3D point)
        {
            activeFlightOrder.Orientation = FlightOrientationMode.LookAt;
            activeFlightOrder.LookAtPoint = Vector3DData.FromVector3D(point);
        }

        public void SetOrderOrientation(Vector3D forward, Vector3D up)   // must not be parallel
        {
            activeFlightOrder.Orientation = FlightOrientationMode.Explicit;
            activeFlightOrder.Forward = Vector3DData.FromVector3D(forward);
            activeFlightOrder.Up = Vector3DData.FromVector3D(up);
        }

        // Position a tool's working point instead of the controller (e.g. welder tip 'reach' metres ahead of the block)
        public void SetOrderReferenceBlock(IMyCubeBlock tool, double reach)
        {
            MatrixD w = shipController.WorldMatrix;
            Vector3D tip = tool.WorldMatrix.Translation + tool.WorldMatrix.Forward * reach;
            activeFlightOrder.ReferenceOffset = Vector3DData.FromVector3D(
                Vector3D.TransformNormal(tip - w.Translation, MatrixD.Transpose(w)));
        }

        public void ClearFlightOrder() { activeFlightOrder = null; }
        private void UpdateTranslation(ref Vector3D desiredVelocity)
        {
            if (shipController == null) return;
            if (!thrustOverridesActive)
            {
                shipController.DampenersOverride = false;
                ResetIntegralTerms();
            }
            Vector3D velErr = desiredVelocity - flightState.LinearVelocity;
            double eF = Vector3D.Dot(velErr, flightState.WorldMatrix.Forward);
            double eU = Vector3D.Dot(velErr, flightState.WorldMatrix.Up);
            double eR = Vector3D.Dot(velErr, flightState.WorldMatrix.Right);

            UpdateIntegralTerms(eF, eU, eR);

            double forceFwdBack   = (-Vector3D.Dot(flightState.Gravity, flightState.WorldMatrix.Forward) + Kp * eF + integralForward) * physicalMass;
            double forceUpDown    = (-Vector3D.Dot(flightState.Gravity, flightState.WorldMatrix.Up)      + Kp * eU + integralUp)      * physicalMass;
            double forceLeftRight = (-Vector3D.Dot(flightState.Gravity, flightState.WorldMatrix.Right)   + Kp * eR + integralRight)   * physicalMass;

            ApplyAxisForce(forceUpDown,    Base6Directions.Direction.Up,      Base6Directions.Direction.Down,
                EffectiveMax(Base6Directions.Direction.Up),      EffectiveMax(Base6Directions.Direction.Down));
            ApplyAxisForce(forceLeftRight, Base6Directions.Direction.Right,   Base6Directions.Direction.Left,
                EffectiveMax(Base6Directions.Direction.Right),   EffectiveMax(Base6Directions.Direction.Left));
            ApplyAxisForce(forceFwdBack,   Base6Directions.Direction.Forward, Base6Directions.Direction.Backward,
                EffectiveMax(Base6Directions.Direction.Forward), EffectiveMax(Base6Directions.Direction.Backward));
            thrustOverridesActive = true;
        }
        private double AvailableAccel(Vector3D u)   // u: world unit vector
        {
            double s = double.MaxValue;
            s = AxisLimit(s, Vector3D.Dot(u, flightState.WorldMatrix.Right),   EffectiveMax(Base6Directions.Direction.Right),   EffectiveMax(Base6Directions.Direction.Left));
            s = AxisLimit(s, Vector3D.Dot(u, flightState.WorldMatrix.Up),      EffectiveMax(Base6Directions.Direction.Up),      EffectiveMax(Base6Directions.Direction.Down));
            s = AxisLimit(s, Vector3D.Dot(u, flightState.WorldMatrix.Forward), EffectiveMax(Base6Directions.Direction.Forward), EffectiveMax(Base6Directions.Direction.Backward));
            double a = s / physicalMass + Vector3D.Dot(flightState.Gravity, u);
            return MathHelperD.Clamp(a, 0.1, 50.0);
        }
        private static void GetLeg(FlightOrder o, out Vector3D a, out Vector3D t)
        {
            switch (o.Phase)
            {
                case FlightPhase.Transit:
                    a = o.TransitStart.ToVector3D();
                    t = o.ApproachFrom.ToVector3D();
                    break;
                case FlightPhase.Align:
                    a = t = o.ApproachFrom.ToVector3D();
                    break;   // zero-length line = hold the point
                default:
                    a = o.ApproachFrom.ToVector3D();
                    t = o.Target.ToVector3D();
                    break;
            }
        }
        private Vector3D ComputeApproachVelocity(FlightOrder o, double orientationError)
        {
            Vector3D rWorld = Vector3D.TransformNormal(o.ReferenceOffset.ToVector3D(), flightState.WorldMatrix);
            Vector3D p = flightState.Position + rWorld;                     // the point being positioned
            Vector3D a;
            Vector3D t;
            GetLeg(o, out a, out t);

            Vector3D line = t - a;
            double len = line.Length();
            Vector3D d = len > 1e-3 ? line / len : Vector3D.Zero;

            // Closest point on the line. Before capture it can't slide into the final leg,
            // so the drone always joins the line at least FINAL_LEG before the target.
            double sMax = o.Captured ? len : Math.Max(0.0, len - FINAL_LEG);
            double s = MathHelperD.Clamp(Vector3D.Dot(p - a, d), 0.0, sMax);
            Vector3D crossTrack = a + d * s - p;
            double xt = crossTrack.Length();
            if (!o.Captured && xt < XT_CAPTURE) o.Captured = true;

            double remaining = len - s;
            double maxSpeed = remaining < FINAL_LEG ? settings.ApproachSpeed : settings.SpeedLimit;

            double along = 0;
            if (len > 1e-3)
            {
                double brake = AvailableAccel(-d) * TRANS_BRAKE;
                double closing = Math.Max(0, Vector3D.Dot(flightState.LinearVelocity, d));
                double usable  = Math.Max(0, remaining - closing / Kp);        // distance eaten by controller lag
                along = usable > TRANS_LINEAR
                    ? Math.Sqrt(2 * brake * usable)
                    : usable * Math.Sqrt(2 * brake / TRANS_LINEAR);
                along = Math.Min(along, maxSpeed);

                if (flightState.InGravity)
                {
                    double down = -Vector3D.Dot(d, flightState.GravityUp);     // > 0 when the line descends
                    if (down > 1e-3) along = Math.Min(along, MAX_DESCENT_SPEED / down);
                }
            }

            // Don't advance while far off the line, or on the final leg while still turning
            double gate = MathHelperD.Clamp(1.0 - xt / (XT_CAPTURE * 4), 0, 1);
            if (remaining < FINAL_LEG && orientationError > ORIENT_GATE) gate = 0;

            Vector3D vCross = Vector3D.Zero;
            if (xt > 1e-3)
            {
                Vector3D xDir = crossTrack / xt;
                double xBrake = AvailableAccel(-xDir) * TRANS_BRAKE;
                vCross = xDir * Math.Min(maxSpeed, Math.Min(XT_GAIN * xt, Math.Sqrt(2 * xBrake * xt)));
            }
            Vector3D desired = d * (along * gate) + vCross;

            // The reference point also moves when the drone rotates (v_ref = v + ω × r); compensate
            return desired - Vector3D.Cross(flightState.AngularVelocity, rWorld);
        }
        private Vector3D RateLimitCommand(Vector3D target)
        {
            Vector3D delta = target - commandedVelocity;
            double len = delta.Length();
            if (len > 1e-6)
            {
                double maxStep = AvailableAccel(delta / len) * ACCEL_FRACTION / TimeUtil.TICKS_PER_SECOND;
                if (len > maxStep) delta *= maxStep / len;
            }
            commandedVelocity += delta;
            return commandedVelocity;
        }
        private static double AxisLimit(double s, double comp, double posMax, double negMax)
        {
            if (Math.Abs(comp) < 1e-3) return s;
            return Math.Min(s, (comp > 0 ? posMax : negMax) / Math.Abs(comp));
        }
        // force: Newtons, signed along the +axis (Up / Right / Forward)
        private void ApplyAxisForce(double force,
            Base6Directions.Direction positive, Base6Directions.Direction negative,
            double positiveMax, double negativeMax)
        {
            if (force >= 0)
            {
                SetDirectionalThrustOverride(negative, 0);
                SetDirectionalThrustOverride(positive, positiveMax > 0 ? force / positiveMax : 0);
            }
            else
            {
                SetDirectionalThrustOverride(positive, 0);
                SetDirectionalThrustOverride(negative, negativeMax > 0 ? -force / negativeMax : 0);
            }
        } 
        #region Thrust gain learning
        // Real acceleration per unit of estimated thrust, per thruster group (index = Base6Directions.Direction).
        // Corrects errors in MaxEffectiveThrust / mass so the braking plan and the throttle match reality.
        private const double GAIN_LEARN_RATE = 0.02;
        private readonly double[] thrustGain         = { 1, 1, 1, 1, 1, 1 };
        private readonly double[] reportedThrustGain = { 1, 1, 1, 1, 1, 1 };
        private readonly double[] groupThrottle      = new double[6];   // throttle applied last tick
        private readonly double[] groupThrottleAvg   = new double[6];   // ~0.5 s average, for "steady" detection
        private Vector3D lastLinearVelocity;

        private float ProfileMax(int dir)
        {
            switch ((Base6Directions.Direction)dir)
            {
                case Base6Directions.Direction.Forward:  return combinedThrustProfile.Forward.Max;
                case Base6Directions.Direction.Backward: return combinedThrustProfile.Backward.Max;
                case Base6Directions.Direction.Left:     return combinedThrustProfile.Left.Max;
                case Base6Directions.Direction.Right:    return combinedThrustProfile.Right.Max;
                case Base6Directions.Direction.Up:       return combinedThrustProfile.Up.Max;
                default:                                 return combinedThrustProfile.Down.Max;
            }
        }
        private double EffectiveMax(Base6Directions.Direction dir)
        {
            return ProfileMax((int)dir) * thrustGain[(int)dir];
        }
        private void LearnThrustGains()
        {
            Vector3D thrustAccel = (flightState.LinearVelocity - lastLinearVelocity) * TimeUtil.TICKS_PER_SECOND
                                 - flightState.Gravity;                         // what the thrusters produced
            lastLinearVelocity = flightState.LinearVelocity;

            for (int i = 0; i < 6; i++)
                groupThrottleAvg[i] += (groupThrottle[i] - groupThrottleAvg[i]) / 30.0;

            LearnAxis(ref thrustAccel, flightState.WorldMatrix.Forward, Base6Directions.Direction.Forward, Base6Directions.Direction.Backward);
            LearnAxis(ref thrustAccel, flightState.WorldMatrix.Up,      Base6Directions.Direction.Up,      Base6Directions.Direction.Down);
            LearnAxis(ref thrustAccel, flightState.WorldMatrix.Right,   Base6Directions.Direction.Right,   Base6Directions.Direction.Left);
        }
        private void LearnAxis(ref Vector3D thrustAccel, Vector3D axis, Base6Directions.Direction pos, Base6Directions.Direction neg)
        {
            if (physicalMass <= 0) return;
            int p = (int)pos, n = (int)neg;
            int active = groupThrottle[p] > 0 ? p : (groupThrottle[n] > 0 ? n : -1);   // ApplyAxisForce drives one side only
            if (active < 0 || groupThrottle[active] < 0.1) return;
            if (Math.Abs(groupThrottle[active] - groupThrottleAvg[active]) > 0.02) return;   // not steady yet

            double expected = groupThrottle[active] * ProfileMax(active) / physicalMass;    // nominal, before correction
            if (expected < 0.5) return;
            double measured = Vector3D.Dot(thrustAccel, axis) * (active == p ? 1 : -1);
            double ratio = MathHelperD.Clamp(measured / expected, 0.2, 1.5);
            thrustGain[active] += (ratio - thrustGain[active]) * GAIN_LEARN_RATE;
        }
        // Debug aid: echo the learned factors to the block's custom info when any moved by more than 0.05
        private void ReportThrustGains()
        {
            bool changed = false;
            for (int i = 0; i < 6; i++)
                if (Math.Abs(thrustGain[i] - reportedThrustGain[i]) > 0.05) { changed = true; break; }
            if (!changed || terminalLogger == null) return;
            for (int i = 0; i < 6; i++)
                reportedThrustGain[i] = thrustGain[i];
            terminalLogger.Echo("thrust gain F{0:F2} B{1:F2} L{2:F2} R{3:F2} U{4:F2} D{5:F2}",
                thrustGain[0], thrustGain[1], thrustGain[2], thrustGain[3], thrustGain[4], thrustGain[5]);
        }
        #endregion

        private readonly double[] directionalThrustCache = new double[6] {
            -1.0,
            -1.0,
            -1.0,
            -1.0,
            -1.0,
            -1.0,
        };
        private void SetDirectionalThrustOverride(Base6Directions.Direction direction, double thrustAmount)
        {
            float value = MathHelper.Clamp((float)thrustAmount, 0f, 1f);
            groupThrottle[(int)direction] = value;   // recorded before the write-skip, for thrust-gain learning
            if (Math.Abs(directionalThrustCache[(int)direction] - value) < 0.005)
                return;
            directionalThrustCache[(int)direction] = value;

            var thrusters = allThrusters[(int)direction];
            for (int i = 0; i < thrusters.Count; i++)
            {
                IMyThrust t = thrusters[i];
                if (!t.IsWorking || !t.IsFunctional) continue;
                t.ThrustOverridePercentage = value;
            }
        }
        private void ClearAllThrustOverrides()
        {
            for (int i = 0; i < 6; i++)
            {
                foreach(IMyThrust thruster in allThrusters[i])
                {
                    thruster.ThrustOverridePercentage = 0f;
                }
                directionalThrustCache[i] = -1.0d;
                groupThrottle[i] = 0;
            }
            thrustOverridesActive = false;
            if (shipController != null)
                shipController.DampenersOverride = true;
        }

        private void UpdateIntegralTerms(double localForwardSpeed, double localUpSpeed, double localRightSpeed)
        {
            double dt = 1.0d / TimeUtil.TICKS_PER_SECOND; // UpdateBeforeSimulation runs at a fixed 60Hz tick

            integralForward = MathHelperD.Clamp(integralForward + localForwardSpeed * INTEGRAL_GAIN * dt, -INTEGRAL_MAX, INTEGRAL_MAX);
            integralUp      = MathHelperD.Clamp(integralUp      + localUpSpeed      * INTEGRAL_GAIN * dt, -INTEGRAL_MAX, INTEGRAL_MAX);
            integralRight   = MathHelperD.Clamp(integralRight   + localRightSpeed   * INTEGRAL_GAIN * dt, -INTEGRAL_MAX, INTEGRAL_MAX);
        }
        private void ResetIntegralTerms()
        {
            integralForward = 0.0;
            integralUp = 0.0;
            integralRight = 0.0;
        }
        #endregion


        #region Debug Flight
        private static string ConvertVectorToGPS(string name, Vector3D position)
        {
            return string.Format("GPS:{0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:",
                name, position.X, position.Y, position.Z);
        }

        private static Vector3D? ParseGPSString(string gpsString)
        {
            if (string.IsNullOrEmpty(gpsString))
                return null;
            var parts = gpsString.Split(':');
            if (parts.Length < 5 || !parts[0].Equals("GPS", StringComparison.OrdinalIgnoreCase))
                return null;
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
            set
            {
                if (settings != null) {
                    settings.IsEnabled = value;
                    if (settings.IsEnabled){
                        CheckCapabilities();
                    } else {
                        Shutdown();
                    }
                    SaveSettings();
                }
            }
        }

        public bool Terminal_EnableInertialDampening
        {
            get { return settings != null && settings.EnableInertialDampening; }
            set { if (settings != null) {
                settings.EnableInertialDampening = value;
                SaveSettings();
                }
            } 
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
            set { if (settings != null) { settings.PGravityAlignMaxPitchDegrees = MathHelper.Clamp(value, 0f, 90f); SaveSettings(); } }
        }

        public float Terminal_MaxRollDegrees
        {
            get { return settings != null ? settings.PGravityAlignMaxRollDegrees : 10f; }
            set { if (settings != null) { settings.PGravityAlignMaxRollDegrees = MathHelper.Clamp(value, 0f, 90f); SaveSettings(); } }
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

        public StringBuilder Terminal_Debug_SetOrientationTargetInput
        {
            get
            {
                if (settings == null) return new StringBuilder("");
                return new StringBuilder(ConvertVectorToGPS("Orientation", orientationTarget));
            }
            set
            {
                if (settings == null) return;
                Vector3D? pos = ParseGPSString(value != null ? value.ToString() : null);
                if (pos.HasValue)
                {
                    orientationTargetDebug = pos.Value;
                }
            }
        }

        public void Terminal_Debug_SetOrientationTarget()
        {
            orientationTarget = orientationTargetDebug;
            orientationTargetSet = true;
        }
       public StringBuilder Textbox_Debug_NavigationTargetGPS
        {
            get {
                return navDebugTarget.HasValue ? new StringBuilder(ConvertVectorToGPS("DebugNavTarget", navDebugTarget.Value)) : new StringBuilder();
            }
            set { navDebugTarget = ParseGPSString(value != null ? value.ToString() : null); }
        }
       public StringBuilder Textbox_Debug_NavigationTargetGPSApproachFrom
        {
            get
            {
                return navDebugTargetApproachFrom.HasValue
                    ? new StringBuilder(ConvertVectorToGPS("DebugNavTargetApproachFrom", navDebugTargetApproachFrom.Value))
                    : new StringBuilder();
            }
            set
            {
                if (settings == null) return;
                navDebugTargetApproachFrom = ParseGPSString(value != null ? value.ToString() : null);
            }
        }

        public void Terminal_Debug_NavigateToTarget()
        {
            if (!navDebugTarget.HasValue) {
                terminalLogger.Echo("Nav: invalid target GPS");
                return;
            }
            OrderGoTo(navDebugTarget.Value, navDebugTargetApproachFrom);
            terminalLogger.Echo("Nav: {0:F0} m, {1}", Vector3D.Distance(flightState.Position, navDebugTarget.Value),
                navDebugTargetApproachFrom.HasValue ? "approach line" : "direct");
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
        private void UpdateNaturalGravity()
        {
            naturalGravity = shipController.GetNaturalGravity();
        }
        private void CaptureFlightState()
        {
            flightState.WorldMatrix = shipController.WorldMatrix;
            MatrixD.Transpose(ref flightState.WorldMatrix, out flightState.WorldMatrixToLocal);
            var v = shipController.GetShipVelocities();
            flightState.LinearVelocity  = v.LinearVelocity;
            flightState.AngularVelocity = v.AngularVelocity;
            flightState.Position = flightState.WorldMatrix.Translation;
            flightState.Gravity  = shipController.GetNaturalGravity();
            double g2 = flightState.Gravity.LengthSquared();
            flightState.InGravity = g2 > 0.01;
            flightState.GravityUp = flightState.InGravity ? -flightState.Gravity / Math.Sqrt(g2) : Vector3D.Zero;
        }
        #endregion

        #region Static Helpers
        private static ToolMount BuildMount(IMyCubeBlock b, ref MatrixD worldToController, ref Vector3D controllerPos)
        {
            var m = new ToolMount { Block = b };
            MatrixD w = b.WorldMatrix;
            Vector3D point = w.Translation;

            var tool = MyDefinitionManager.Static.GetCubeBlockDefinition(b.BlockDefinition) as MyShipToolDefinition;
            if (tool != null)
            {
                point += w.Forward * tool.SensorOffset;     // centre of the work sphere (check once in game)
                m.WorkRadius = tool.SensorRadius;
            }
            else if (b is IMyShipConnector)
            {
                Vector3I size = b.Max - b.Min + Vector3I.One;                 // grid cells
                Vector3I f = Base6Directions.GetIntVector(b.Orientation.Forward);
                double cells = Math.Abs(size.X * f.X + size.Y * f.Y + size.Z * f.Z);
                point += w.Forward * (cells * b.CubeGrid.GridSize * 0.5);    // front face
            }
            m.LocalPoint   = Vector3D.TransformNormal(point - controllerPos, worldToController);
            m.LocalForward = Vector3D.TransformNormal(w.Forward, worldToController);
            m.LocalUp      = Vector3D.TransformNormal(w.Up, worldToController);
            return m;
        }
        private static bool HasAnyDirectionalComponent<T>(List<T>[] componentArray)
        {
            if (componentArray == null) return false;
            for (int i = 0; i < 6; i++)
                if (componentArray[i].Count > 0) return true;
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
            float upAccel =  (baseThrustProfile.Up.Max + h2ThrustProfile.Up.Max) / mass;
            if (upAccel <= gravityNormal) return 0f;
            return (upAccel - gravityNormal) * mass / gravityNormal;  
        }

        private static ThrustProfile CalculateThrustProfile(IMyShipController shipController, float currentMass, List<IMyThrust>[] thrusters)
        {
            ThrustProfile thrustProfile = new ThrustProfile();

            if (shipController == null) return thrustProfile;
            if (currentMass < 0.1f) return thrustProfile;
            if (thrusters == null) return thrustProfile;

            float dirThrust;
            for (int i = 0; i < 6; i++)
            {
                dirThrust = 0.0f;
                foreach(IMyThrust thruster in thrusters[i])
                {
                    if (thruster.IsWorking && thruster.IsFunctional)
                        dirThrust += thruster.MaxEffectiveThrust;
                }
                Base6Directions.Direction direction = (Base6Directions.Direction)i;
                if(direction == Base6Directions.Direction.Forward)
                    thrustProfile.Forward = new DirectionalValue(dirThrust, direction);
                else if(direction == Base6Directions.Direction.Backward)
                    thrustProfile.Backward = new DirectionalValue(dirThrust, direction);
                else if(direction == Base6Directions.Direction.Up)
                    thrustProfile.Up = new DirectionalValue(dirThrust, direction);
                else if(direction == Base6Directions.Direction.Down)
                    thrustProfile.Down = new DirectionalValue(dirThrust, direction);
                else if(direction == Base6Directions.Direction.Left)
                    thrustProfile.Left = new DirectionalValue(dirThrust, direction);
                else if(direction == Base6Directions.Direction.Right)
                    thrustProfile.Right = new DirectionalValue(dirThrust, direction);
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
