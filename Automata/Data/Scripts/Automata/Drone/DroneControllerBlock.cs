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
using Automata.Network;
using Sandbox.Definitions;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Utils;
using VRage.Game.ObjectBuilders.ComponentSystem;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

namespace Automata.Drone
{
    // No [MyComponentType]: the saved component is keyed "MyGameLogicComponent" and must resolve to the entity's GameLogic
    // slot on load. Mapping it to this type made MyComponentContainer.Deserialize look for a component that doesn't
    // exist, create a null one and crash the world load.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RemoteControl), false,
        "AutomataSmallDroneController",
        "AutomataLargeDroneController")]
    public partial class DroneControllerBlock : MyGameLogicComponent
    {
        private struct FlightState
        {
            public MatrixD WorldMatrix, WorldMatrixToLocal; // WorldToLocal = transpose; use with TransformNormal only
            public Vector3D Position, LinearVelocity, AngularVelocity, Gravity, GravityUp;
            public Vector3D FrameVelocity;      // velocity of the order's anchor grid at the drone (zero when not anchored)
            public Vector3D RelativeVelocity;   // LinearVelocity - FrameVelocity
            public bool InGravity;
        }
        public struct ToolMount
        {
            public IMyCubeBlock Block;
            public Vector3D LocalPoint;     // working point, controller-local
            public Vector3D LocalForward, LocalUp;
            public double WorkRadius;       // welder/grinder/drill sphere; 0 for connectors
            public bool CanConnect;         // connector with a "connector" dummy (ejectors have none)
            public bool SmallConnector;     // small connectors only lock to small connectors
        }
        private ToolMount welderMount, grinderMount, drillMount;
        public const int MOUNT_NONE = int.MinValue, MOUNT_WELDER = -1, MOUNT_GRINDER = -2, MOUNT_DRILL = -3;
        private const double TOOL_REACH_FRACTION = 0.9;   // target sits this far out on the work sphere (1 = surface)
        private static readonly Dictionary<string, IMyModelDummy> dummyCache = new Dictionary<string, IMyModelDummy>();
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
        private string errorReason;                        // why the drone is in State.Error (LCD alert line)
        private readonly StringBuilder sb = new StringBuilder(64);
        private BehaviourProfile behaviourProfile;
        private State currentState = State.Initializing;
        private bool initialized = false;
        private int _consecutiveErrors = 0;

        private IMyShipController shipController;
        private IMyShipWelder welder;
        private IMyShipGrinder grinder;
        private IMyShipDrill drill;
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

        // Leg chaining (pathfinding hand-over): the manager answers NextLegRequested by calling EnqueueLeg().
        public event Action<DroneControllerBlock> NextLegRequested;
        private FlightOrder nextLeg;
        private bool nextLegRequested;
        private readonly Queue<FlightOrder> queuedLegs = new Queue<FlightOrder>();
        private FlightOrder lastQueuedLeg;
        private Vector3D legDir;          // current leg, written by ComputeApproachVelocity
        private double legRemaining;
        private bool legPassed;
        private bool arrivalReported;     // "arrived" logged once per order

        // Relative navigation
        private IMyTerminalBlock anchorBlock;   // resolved anchor of the active order

        // Hull box of the whole mechanical grid group, controller frame (for pathfinding ray bundles / clearance)
        private BoundingBoxD localHullBox = BoundingBoxD.CreateInvalid();
        public BoundingBoxD LocalHullBox { get { return localHullBox; } }

        // Home connector / observation area
        private IMyShipConnector homeConnector;
        private int homeConnectorCheckedFrame;
        private int observationDrawStartFrame;
        private const int OBSERVATION_DRAW_TICKS = 120 * 60;          // draw switches itself off after 120 s
        public const double OBSERVATION_UNIT = 2.5;                    // m per observation-area block (large grid cube)
        public const int OBSERVATION_SIZE_MIN = 1;
        public const int OBSERVATION_SIZE_MAX = 10;
        public const int OBSERVATION_OFFSET_MAX = 10;   // blocks
        private const float OBSERVATION_GRID_CELL = 2.5f;              // m, grid spacing drawn on the area faces
        private static readonly MyStringId DrawMaterial = MyStringId.GetOrCompute("Square");

        // Debug: place mount / relative navigation
        private int debugMountSelection = MOUNT_NONE;
        private Vector3D? debugMountTarget;
        private long debugAnchorId;
        private Vector3D? debugRelativeOffset;

        private bool gyroOverrideActive = false;
        private bool thrustOverridesActive = false;

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

            // EACH_FRAME is switched on only while flying / holding (UpdateFrameSubscription)
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();
            try
            {
                if (!initialized || !IsServer)     // flight control is server-side only
                    return;
                if (!settings.IsEnabled)
                {
                    // AI off: hands off; the game's own dampeners hold the drone where it is
                    if (thrustOverridesActive) ClearAllThrustOverrides();
                    if (gyroOverrideActive) ReleaseGyroscopes();
                    return;
                }
                CaptureFlightState();
                if (activeFlightOrder != null && !RefreshAnchoredOrder(activeFlightOrder))
                {
                    Report("Nav: anchor lost, order cancelled");
                    ClearFlightOrder();
                }
                if (preflightStage != 0)
                {
                    UpdatePreflight();                 // waking up from dock / landing: flight on hold
                    if (preflightStage != 0) return;
                }
                if (activeFlightOrder == null && isParked)
                {
                    // Docked / landed and idle: hands off, systems may be asleep
                    if (thrustOverridesActive) ClearAllThrustOverrides();
                    if (gyroOverrideActive) ReleaseGyroscopes();
                    return;
                }
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
                    FlightOrder o = activeFlightOrder;
                    Vector3D v = RateLimitCommand(ComputeApproachVelocity(o, orientationError));
                    UpdateTranslation(ref v);
                    AdvanceFlightPhase(o, orientationError);

                    Vector3D rWorld = Vector3D.TransformNormal(o.ReferenceOffset.ToVector3D(), flightState.WorldMatrix);
                    double tol = o.ArrivalTolerance;
                    o.Arrived = o.Phase == FlightPhase.Approach
                        && Vector3D.DistanceSquared(flightState.Position + rWorld, o.Target.ToVector3D()) < tol * tol
                        && flightState.RelativeVelocity.LengthSquared() < ARRIVE_SPEED * ARRIVE_SPEED
                        && orientationError < ORIENT_GATE;
                    if (o.Arrived && !arrivalReported && nextLeg == null && queuedLegs.Count == 0 && dockingHomeId == 0)
                    {
                        arrivalReported = true;
                        Report("Nav: arrived");
                        SetState(State.Standby);
                    }
                    UpdateLegChaining(o);
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
                    if (!initialized && IsServer && settings != null) display.Flush(settings);   // show why (header alert)
                    return;
                }
                FlushSettingSync();
                if (IsServer) SendNavigationRoutes();
                if (!IsServer)
                {
                    CaptureFlightState();   // clients: UI only (load writer, draw)
                    return;
                }
                if (preflightStage == 0)
                    isParked = IsParkedNow();
                if (!frameUpdatesOn)
                    CaptureFlightState();          // keep a recent snapshot for UI / monitoring while idle
                if (settings.IsEnabled)
                {
                    // Landing gear with auto-lock can grab again right after take-off: keep it released while flying
                    if (activeFlightOrder != null && preflightStage == 0 && dockingHomeId == 0)
                        for (int i = 0; i < landingGears.Count; i++)
                            if (landingGears[i].IsLocked) landingGears[i].Unlock();
                    UpdateDocking();
                    UpdateConstruction10();
                }
                UpdateFrameSubscription();
                display.Flush(settings);
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
                UpdateGravity();
                if (!IsServer)
                {
                    // clients: keep the block lists (terminal) and thrust figures (load labels) current, nothing else
                    combinedThrustProfile = CalculateThrustProfile(shipController, physicalMass, allThrusters);
                    if (capabilitiesDirty || sessionDelegate.GameplayFrameCounter - lastComponentCheckFrame > COMPONENT_CHECK_INTERVAL_MIN_TICKS)
                    {
                        lastComponentCheckFrame = sessionDelegate.GameplayFrameCounter;
                        capabilitiesDirty = false;
                        CheckCapabilities();
                    }
                    return;
                }
                bool flying = frameUpdatesOn && !isParked && preflightStage == 0;
                if (flying)
                    UpdateMass();                   // cargo can change while working; idle / docked drones are re-measured at pre-flight
                if (!isParked)
                    combinedThrustProfile = CalculateThrustProfile(shipController, physicalMass, allThrusters);
                MonitorPower();                     // levels for the header always; acts only with the AI on
                if (settings.IsEnabled)
                {
                    ReportThrustGains();
                    CheckLoad();
                    UpdateConstruction100();
                    UpdateOrchestratorJobs();
                    // Ownership / range can change mid-flight: re-validate the anchor at a low rate
                    if (anchorBlock != null && activeFlightOrder != null && activeFlightOrder.AnchorEntityId != 0
                        && AnchorDirectory.Resolve(block, activeFlightOrder.AnchorEntityId) == null)
                    {
                        Report("Nav: anchor no longer available, order cancelled");
                        ClearFlightOrder();
                    }
                }
                UpdateAlert();
                // Full block rescan: when blocks were added / removed (event-driven), else every ComponentCheckInterval
                var currentFrame = sessionDelegate.GameplayFrameCounter;
                if (capabilitiesDirty || currentFrame - lastComponentCheckFrame > COMPONENT_CHECK_INTERVAL_MIN_TICKS)
                {
                    lastComponentCheckFrame = currentFrame;
                    capabilitiesDirty = false;
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
                if (IsServer) Shutdown();
                UnsubscribeGridEvents();
                if (AutomataSession.Instance != null)
                {
                    AutomataSession.Instance.Anchors.Forget(Entity.EntityId);
                    AutomataSession.Instance.RequestDraw(this, false);
                }

            }
            catch (Exception ex)
            {
                Log.Error("IAIDroneControllerBlock MarkForClose error: {0}", ex);
            }
            base.MarkForClose();
        }
        #endregion

        #region State load/save
        // Settings are serialized only when the game builds this block's object builder (world save, streaming,
        // blueprint copy): setters just mark them dirty. SaveSettings() keeps its name so call sites don't change.
        private bool settingsDirty;
        private string loadedSettingsData;   // from Deserialize, consumed by LoadSettings

        private void SaveSettings()
        {
            settingsDirty = true;
        }

        private string SerializeSettings()
        {
            return Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(settings));
        }

        // Called before Serialize whenever the object builder is built. Also mirrors the data into Entity.Storage:
        // the documented streamed-to-clients path, and a fallback if the custom component is ever dropped
        // (e.g. several mods' gamelogic merged into a composite component).
        public override bool IsSerialized()
        {
            try
            {
                if (settings != null && settingsDirty)
                {
                    if (Entity.Storage == null)
                        Entity.Storage = new MyModStorageComponent();
                    Entity.Storage.SetValue(AutomataSession.MOD_GUID, SerializeSettings());
                    settingsDirty = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("DroneControllerBlock {0}: IsSerialized error: {1}", Entity?.EntityId, ex);
            }
            // Only when this is the block's sole gamelogic: a composite (another mod's gamelogic on the same block)
            // cannot be matched back on load. Entity.Storage carries the settings then.
            return Entity != null && Entity.GameLogic == this;
        }

        public override MyObjectBuilder_ComponentBase Serialize(bool copy = false)
        {
            string data = null;
            try
            {
                // Not initialized yet (e.g. never had physics): hand back what was loaded, don't lose it
                data = settings != null ? SerializeSettings() : loadedSettingsData;
            }
            catch (Exception ex)
            {
                Log.Error("DroneControllerBlock {0}: Serialize error: {1}", Entity?.EntityId, ex);
            }
            return new MyObjectBuilder_ModCustomComponent
            {
                CustomModData = data ?? "",
                ComponentType = nameof(DroneControllerBlock),
                SubtypeName = nameof(DroneControllerBlock),
                RemoveExistingComponentOnNewInsert = false,
            };
        }

        public override void Deserialize(MyObjectBuilder_ComponentBase ob)
        {
            var custom = ob as MyObjectBuilder_ModCustomComponent;
            if (custom != null && !string.IsNullOrEmpty(custom.CustomModData))
                loadedSettingsData = custom.CustomModData;
        }

        // Custom component data first, then Entity.Storage (older saves), then defaults
        private void LoadSettings()
        {
            try
            {
                string base64Data = loadedSettingsData;
                if (string.IsNullOrEmpty(base64Data) && Entity?.Storage != null)
                    Entity.Storage.TryGetValue(AutomataSession.MOD_GUID, out base64Data);
                if (string.IsNullOrEmpty(base64Data))
                {
                    settings = new DroneControllerSettings();
                    return;
                }
                settings = MyAPIGateway.Utilities.SerializeFromBinary<DroneControllerSettings>(Convert.FromBase64String(base64Data))
                           ?? new DroneControllerSettings();
                loadedSettingsData = null;
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
                Report("drone {0}: already initialized", _entityId);
                return;
            }
            _entityId = entity.EntityId;
            this.utilitiesDelegate = new MyUtilitiesDelegate();
            this.sessionDelegate = new MySessionDelegate();
            if (terminalLogger == null)
                terminalLogger = new TerminalDisplayManager(block as IMyTerminalBlock, 10);
            this.pruningStructureDelegate = new MyGamePruningStructureDelegate();
            this.planetDelegate =  new MyPlanetDelegate();
            if (settings == null) LoadSettings();   // Initialize retries every 10 ticks: keep what was loaded / received



            Log.Debug("Terminal logger assigned");
            if (currentState != State.Error) currentState = State.Initializing;   // retried every 10 ticks: log an error once

            // STATUS_REPORT_INTERVAL_TICKS = AutomataSession.GetConfig().MessageQueue.DroneMessageThrottlingTicks();
            POWER_CHECK_INTERVAL_MIN_TICKS = TimeUtil.SecondsToTicks(AutomataSession.GetConfig().Drone.PowerCheckIntervalMinSeconds);
            COMPONENT_CHECK_INTERVAL_MIN_TICKS = TimeUtil.SecondsToTicks(AutomataSession.GetConfig().Drone.ComponentCheckIntervalMinSeconds);

            shipController = entity as IMyShipController;
            if (shipController == null)
            {
                if (currentState != State.Error) Report("ERROR: entity is not a ship controller");
                errorReason = "Not a ship controller";
                SetState(State.Error);
                UpdateAlert();
                return;
            }

            if (!CheckCapabilities())
            {
                if (currentState != State.Error) Report("ERROR: failed capability check ({0})", errorReason);
                SetState(State.Error);
                UpdateAlert();
                return;
            }

            // pathfindingManager = new PathfindingManager(AutomataSession.GetConfig().Pathfinding, pruningStructureDelegate, planetDelegate);

            if (primaryAntenna != null && IsServer)   // the virtual network is simulated on the server only
            {
                AutomataSession.GetMessageQueue().RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
            }

            if (settings.OperationMode == OperationMode.ManagedByScheduler && IsServer)
            {
                AutomataSession.GetMessageQueue().Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_START);
                AutomataSession.GetMessageQueue().Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
            }

            UpdateMass();
            initialized = true;
            isParked = IsParkedNow();
            Report("Initialized in mode: {0}", settings.OperationMode);
            SetState(isParked ? State.Docked : State.Standby);
        }
        #endregion

        #region Capability Detection
        public bool CheckCapabilities()
        {
            var cubeBlock = entity as IMyCubeBlock;
            if (cubeBlock == null) return false;

            var shipController = entity as IMyShipController;
            if (shipController?.CubeGrid == null) return false;

            this.shipController = shipController;
            UpdateMass();
            UpdateGravity();

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
            drillMount = default(ToolMount);
            welder = null;
            grinder = null;
            drill = null;
            primaryAntenna = null;
            lastGyroCmd = new Vector3D(double.NaN, double.NaN, double.NaN);

            CaptureFlightState();
            var controllerMatrix = shipController.Orientation;
            int oreDetectors = 0, cameras = 0, wheels = 0;
            _gridCache.Clear();
            var gridGroup = cubeBlock.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical);
            gridGroup.GetGrids(_gridCache);
            SubscribeGridEvents(_gridCache, gridGroup);   // blocks added / removed trigger the next rescan

            // Hull box of the whole group, in the controller's frame (conservative: transformed AABBs)
            MatrixD worldToController = MatrixD.Invert(flightState.WorldMatrix);
            localHullBox = BoundingBoxD.CreateInvalid();

            foreach (var grid in _gridCache)
            {
                var lb = grid.LocalAABB;
                var gridBox = new BoundingBoxD(lb.Min, lb.Max).TransformFast(grid.WorldMatrix * worldToController);
                localHullBox = localHullBox.Include(ref gridBox);

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
                    else if (fatBlock is IMyShipDrill && drill == null)
                    {
                        drill = (IMyShipDrill)fatBlock;
                        drillMount = BuildMount(fatBlock, ref flightState.WorldMatrixToLocal, ref flightState.Position);
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
                    else if (fatBlock is IMyOreDetector)
                        oreDetectors++;
                    else if (fatBlock is IMyCameraBlock)
                        cameras++;
                    else if (fatBlock is IMyMotorSuspension)
                        wheels++;
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
            // Drones always transfer power through their connectors (needed to recharge when docked)
            for (int i = 0; i < connectors.Count; i++)
                EnsurePowerTransferOverride(connectors[i]);
            display.FindPanels(shipController.CubeGrid, settings.LCDScreenTag);   // own grid only

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
            if (hasMinimum) errorReason = null;
            else
            {
                capabilities = Capabilities.None;   // nothing to offer; don't keep what a removed block gave
                sb.Clear();
                sb.Append("Missing:");
                if (gyroscopes.Count == 0) sb.Append(" gyroscope");
                if (!HasAnyDirectionalComponent(hydrogenThrusters) && !HasAnyDirectionalComponent(ionThrusters)
                    && !HasAnyDirectionalComponent(atmoThrusters)) sb.Append(" thrusters");
                if (connectors.Count == 0) sb.Append(" connector");
                errorReason = sb.ToString();
            }
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
            if (connectors.Count > 0) capabilities |= Capabilities.CanDock;
            if (welder != null) capabilities |= Capabilities.CanWeld;
            if (grinder != null) capabilities |= Capabilities.CanGrind;
            if (drill != null) capabilities |= Capabilities.CanDrill;
            if (oreDetectors > 0) capabilities |= Capabilities.CanScoutOre;
            if (sensors.Count > 0) capabilities |= Capabilities.HasSensors;
            if (cameras > 0) capabilities |= Capabilities.HasCameras;
            if (wheels > 0) capabilities |= Capabilities.HasWheels;
            capabilities |= CargoVolumeCapability(TotalCargoVolume());
            capabilities |= LiftCapability(PayloadIn1G());

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
        // m³ of all cargo containers (not tools, connectors or tanks)
        private double TotalCargoVolume()
        {
            double volume = 0;
            for (int i = 0; i < cargoContainers.Count; i++)
            {
                var inv = cargoContainers[i].GetInventory(0);
                if (inv != null) volume += (double)inv.MaxVolume;
            }
            return volume;
        }

        /// <summary>Exactly one size class, for quick job pre-filtering (anything from 1000 m³ up is the top class).</summary>
        public static Capabilities CargoVolumeCapability(double volume)
        {
            if (volume <= 0) return Capabilities.CargoVolumeNil;
            if (volume < 10) return Capabilities.CargoVolumeLt10m3;
            if (volume < 100) return Capabilities.CargoVolumeLt100m3;
            if (volume < 1000) return Capabilities.CargoVolumeLt1000m3;
            return Capabilities.CargoVolumeLt10000m3;
        }

        /// <summary>
        /// kg the drone can carry in 1 g beyond its own mass, within its max-load (gravity) setting. Rated thrust:
        /// counts thrusters switched off by docking.
        /// </summary>
        public double PayloadIn1G()
        {
            double limitPct = settings != null ? settings.MaxLoadGravity : 100.0;
            return Math.Max(0, RatedThrust(Base6Directions.Direction.Up) / 9.81 * limitPct / 100.0 - DroneOwnMass());
        }

        /// <summary>Exactly one lift class, for quick job pre-filtering (100 t and up is the top class).</summary>
        public static Capabilities LiftCapability(double payloadKg)
        {
            if (payloadKg <= 1) return Capabilities.LiftNil;
            if (payloadKg < 1000) return Capabilities.LiftLt1t;
            if (payloadKg < 10000) return Capabilities.LiftLt10t;
            if (payloadKg < 100000) return Capabilities.LiftLt100t;
            return Capabilities.LiftLt1000t;
        }

        /// <summary>Upper bound of a lift class in kg (for planning from capabilities alone).</summary>
        public static double LiftClassMaxKg(Capabilities caps)
        {
            if ((caps & Capabilities.LiftLt1000t) != 0) return double.MaxValue;
            if ((caps & Capabilities.LiftLt100t) != 0) return 100000;
            if ((caps & Capabilities.LiftLt10t) != 0) return 10000;
            if ((caps & Capabilities.LiftLt1t) != 0) return 1000;
            return 0;
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
            if (drill?.Enabled == true) drill.Enabled = false;
            ClearFlightOrder();
            orientationTargetSet = false;
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
            if (!IsServer) return;   // block properties are synced from the server
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
            if (o.Phase == FlightPhase.MatchSpeed)
            {
                if (flightState.RelativeVelocity.LengthSquared() < 0.25 && anchorBlock != null)   // within 0.5 m/s of the anchor
                {
                    // Start the relative legs from wherever the speed match left us
                    MatrixD m = anchorBlock.WorldMatrix;
                    Vector3DData here = Vector3DData.FromVector3D(WorldToAnchorPoint(ref m, ReferencePoint(o)));
                    o.TransitStartLocal = here;
                    if (!o.UseApproachLine) o.ApproachFromLocal = here;
                    o.Phase = o.UseApproachLine ? FlightPhase.Transit : FlightPhase.Approach;
                    o.Captured = false;
                    RefreshAnchoredOrder(o);
                }
            }
            else if (o.Phase == FlightPhase.Transit)
            {
                double tol = o.LineTolerance > 0 ? o.LineTolerance : settings.WaypointTolerance;
                if (Vector3D.DistanceSquared(ReferencePoint(o), o.ApproachFrom.ToVector3D()) < tol * tol
                    && flightState.RelativeVelocity.LengthSquared() < 0.25)            // settled below 0.5 m/s
                {
                    o.Phase = FlightPhase.Align;
                    o.Captured = false;
                }
            }
            else if (o.Phase == FlightPhase.Align
                        && orientationError < ORIENT_GATE
                        && (o.LineTolerance <= 0
                            || Vector3D.DistanceSquared(ReferencePoint(o), o.ApproachFrom.ToVector3D()) < o.LineTolerance * o.LineTolerance)
                        && flightState.AngularVelocity.LengthSquared() < ROT_DONE_SPEED * ROT_DONE_SPEED)
            {
                o.Phase = FlightPhase.Approach;
                o.Captured = false;
            }
        }
        // The point the order positions (controller, or ReferenceOffset such as a connector), world
        private Vector3D ReferencePoint(FlightOrder o)
        {
            return flightState.Position + Vector3D.TransformNormal(o.ReferenceOffset.ToVector3D(), flightState.WorldMatrix);
        }
        private void BuildOrderFrame(FlightOrder o, out Vector3D fwd, out Vector3D up)
        {
            if (o.Phase == FlightPhase.Transit || o.Phase == FlightPhase.MatchSpeed)
            {
                fwd = o.Phase == FlightPhase.Transit
                    ? o.ApproachFrom.ToVector3D() - o.TransitStart.ToVector3D()
                    : (o.UseApproachLine ? o.ApproachFrom.ToVector3D() : o.Target.ToVector3D()) - flightState.Position;
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
            var o = CreateGoTo(target, approachFrom);
            Report("Nav: {0:F0} m{1}", Vector3D.Distance(flightState.Position, target), approachFrom.HasValue ? ", approach line" : "");
            return o;
        }

        // Effective speeds: Safe <= Approach <= Max
        // Not clamped to each other (see SpeedSettingsProblem); the floor only keeps the maths finite
        private const float MIN_FLIGHT_SPEED = 0.05f;
        private float MaxSpeed { get { return MathHelper.Clamp(settings.MaxSpeed, MIN_FLIGHT_SPEED, MAX_SPEED_MAX); } }
        private float ApproachSpeed { get { return MathHelper.Clamp(settings.ApproachSpeed, MIN_FLIGHT_SPEED, APPROACH_SPEED_MAX); } }
        private float SafeSpeed { get { return MathHelper.Clamp(settings.SafeSpeed, MIN_FLIGHT_SPEED, SAFE_SPEED_MAX); } }

        /// <summary>
        /// The LCD header's alert line: the most important current problem. Errors (red) stop the drone or make
        /// its settings unusable; warnings (yellow) are conditions it is handling or waiting on.
        /// </summary>
        private void UpdateAlert()
        {
            if (settings == null) return;
            var level = DisplayAlertLevel.None;
            string text = null;
            string speed = SpeedSettingsProblem();
            if (currentState == State.Error) { level = DisplayAlertLevel.Error; text = errorReason ?? "Error"; }
            else if (speed != null) { level = DisplayAlertLevel.Error; text = speed; }
            else if (!settings.IsEnabled) { level = DisplayAlertLevel.Warning; text = "AI disabled"; }
            else if (thrustObstructedDir >= 0 && activeFlightOrder != null)
            {
                level = DisplayAlertLevel.Warning;
                text = "Stuck: " + (Base6Directions.Direction)thrustObstructedDir + " thrust has no effect";
            }
            else if (loadLevel == 2) { level = DisplayAlertLevel.Warning; text = "Max load exceeded"; }
            else if (needsBatteryRecharge) { level = DisplayAlertLevel.Warning; text = "Battery low"; }
            else if (needsHydrogenRefuel) { level = DisplayAlertLevel.Warning; text = "Hydrogen low"; }
            else if (lastConstructionProblem != null) { level = DisplayAlertLevel.Warning; text = lastConstructionProblem; }
            display.SetAlert(level, text);
        }

        /// <summary>Speed settings that make no sense together, or null.</summary>
        private string SpeedSettingsProblem()
        {
            if (settings.MaxSpeed <= 0) return "Max speed is 0";
            if (settings.ApproachSpeed <= 0) return "Approach speed is 0";
            if (settings.SafeSpeed <= 0) return "Safe speed is 0";
            if (settings.ApproachSpeed > settings.MaxSpeed) return "Approach speed > max speed";
            if (settings.SafeSpeed > settings.ApproachSpeed) return "Safe speed > approach speed";
            return null;
        }
        private double FinalSpeedFor(FlightOrder o)
        {
            // FinalSpeed is for the final approach only; the transit to the approach point ends at ApproachSpeed
            return o.FinalSpeed > 0 && o.Phase == FlightPhase.Approach ? Math.Min(o.FinalSpeed, ApproachSpeed) : ApproachSpeed;
        }

        private FlightOrder CreateGoTo(Vector3D target, Vector3D? approachFrom)
        {
            ResetOrderState();
            BeginFlight();
            if (preflightStage == 0) SetState(State.NavigatingToTarget);
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

        /// <summary>
        /// Relative navigation: target (and optional approach point) are offsets in the anchor block's frame,
        /// as (Right, Up, Forward) metres. The drone first matches the anchor grid's velocity, then flies the
        /// order in the anchor's moving frame. The anchor must pass the owner/faction check.
        /// </summary>
        public FlightOrder OrderGoToRelative(IMyTerminalBlock anchor, Vector3D localTarget, Vector3D? localApproachFrom = null)
        {
            if (anchor == null || AnchorDirectory.Resolve(block, anchor.EntityId) == null) return null;
            ResetOrderState();
            BeginFlight();
            if (preflightStage == 0) SetState(State.NavigatingToTarget);
            MatrixD m = anchor.WorldMatrix;
            Vector3DData here = Vector3DData.FromVector3D(WorldToAnchorPoint(ref m, flightState.Position));
            var o = new FlightOrder
            {
                AnchorEntityId = anchor.EntityId,
                TargetLocal = Vector3DData.FromVector3D(localTarget),
                ApproachFromLocal = localApproachFrom.HasValue ? Vector3DData.FromVector3D(localApproachFrom.Value) : here,
                TransitStartLocal = here,
                UseApproachLine = localApproachFrom.HasValue,
                Phase = FlightPhase.MatchSpeed,
                Orientation = FlightOrientationMode.FaceTravel,
                ArrivalTolerance = settings.WaypointTolerance
            };
            anchorBlock = anchor;
            activeFlightOrder = o;
            RefreshAnchoredOrder(o);
            return o;
        }

        /// <summary>
        /// Tools (welder, grinder, drill): puts the target on the far side of the work sphere, as far from
        /// the target as the tool still reaches, with the drone level. The heading is chosen so the tool's
        /// horizontal facing points at the target; a tool facing straight down makes the drone hover above it.
        /// </summary>
        public FlightOrder OrderWorkAt(ref ToolMount mount, Vector3D target)
        {
            if (mount.Block == null || mount.WorkRadius <= 0) return null;
            CaptureFlightState();
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            Vector3D toTarget = target - flightState.Position;
            Vector3D dir = toTarget - gUp * Vector3D.Dot(toTarget, gUp);
            if (dir.LengthSquared() < 1e-4) dir = LevelHeading(ref gUp);
            dir.Normalize();

            // Mount facing in controller axes: a along Right, b along Forward
            double a = mount.LocalForward.X, b = -mount.LocalForward.Z;
            double h = Math.Sqrt(a * a + b * b);
            Vector3D heading = dir;
            if (h > 0.1)
            {
                a /= h; b /= h;
                heading = b * dir - a * Vector3D.Cross(dir, gUp);   // gives a·Right + b·Forward == dir
            }
            MatrixD frame = MatrixD.CreateWorld(Vector3D.Zero, heading, gUp);
            Vector3D toolFwd = Vector3D.TransformNormal(mount.LocalForward, frame);
            Vector3D sphereCentre = target - toolFwd * (mount.WorkRadius * TOOL_REACH_FRACTION);
            Vector3D goal = sphereCentre - Vector3D.TransformNormal(mount.LocalPoint, frame);

            var order = CreateGoTo(goal, null);
            SetOrderOrientation(frame.Forward, frame.Up);
            order.ArrivalTolerance = (float)Math.Max(0.25, mount.WorkRadius * 0.25);
            return order;
        }

        /// <summary>
        /// Places a mount's working point (connector: its detector dummy) at 'goal', facing 'toolForward'.
        /// The drone starts from a level frame at its current heading and turns by the smallest rotation that
        /// lines the mount up, so a belly connector pointed down keeps the drone level. The final approach runs
        /// along the mount's axis from 'approachDistance' metres out.
        /// </summary>
        public FlightOrder OrderPlaceMount(ref ToolMount mount, Vector3D goal, Vector3D toolForward, double approachDistance = 10.0)
        {
            if (mount.Block == null || toolForward.LengthSquared() < 1e-6) return null;
            CaptureFlightState();
            toolForward.Normalize();
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            MatrixD frame = MountFrame(ref mount, toolForward);

            Vector3D offsetWorld = Vector3D.TransformNormal(mount.LocalPoint, frame);
            Vector3D controllerGoal = goal - offsetWorld;
            Vector3D approach = controllerGoal - toolForward * approachDistance;
            var order = CreateGoTo(controllerGoal, approachDistance > 0 ? approach : (Vector3D?)null);
            SetOrderOrientation(frame.Forward, frame.Up);
            order.IgnoreGravityLimits = true;   // mount alignment beats the pitch/roll limits
            order.ArrivalTolerance = 0.2f;

            if (flightState.InGravity)
            {
                double tilt = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(frame.Up, gUp), -1, 1)));
                if (tilt > 30)
                    Report("Warning: mount alignment tilts the drone {0:F0}°", tilt);
            }
            return order;
        }

        // Current forward flattened onto the plane normal to 'up' (falls back to other axes when vertical)
        private Vector3D LevelHeading(ref Vector3D up)
        {
            Vector3D h = flightState.WorldMatrix.Forward - up * Vector3D.Dot(flightState.WorldMatrix.Forward, up);
            if (h.LengthSquared() < 1e-4)
                h = flightState.WorldMatrix.Down - up * Vector3D.Dot(flightState.WorldMatrix.Down, up);
            if (h.LengthSquared() < 1e-4)
                h = Vector3D.CalculatePerpendicularVector(up);
            return Vector3D.Normalize(h);
        }

        // Applies to v the smallest rotation taking unit vector 'from' onto unit vector 'to' (Rodrigues)
        private static Vector3D RotateOnto(Vector3D v, ref Vector3D from, ref Vector3D to)
        {
            double cos = MathHelperD.Clamp(Vector3D.Dot(from, to), -1, 1);
            Vector3D k = Vector3D.Cross(from, to);
            double sin = k.Length();
            if (sin < 1e-6)
            {
                if (cos > 0) return v;                                   // already aligned
                k = Vector3D.CalculatePerpendicularVector(from);         // opposite: any perpendicular axis, 180°
                sin = 0;
            }
            else k /= sin;
            return v * cos + Vector3D.Cross(k, v) * sin + k * (Vector3D.Dot(k, v) * (1 - cos));
        }

        public void SetOrderLookAt(Vector3D point)
        {
            activeFlightOrder.Orientation = FlightOrientationMode.LookAt;
            activeFlightOrder.LookAtPoint = Vector3DData.FromVector3D(point);
        }

        public void SetOrderOrientation(Vector3D forward, Vector3D up)   // must not be parallel
        {
            var o = activeFlightOrder;
            o.Orientation = FlightOrientationMode.Explicit;
            o.Forward = Vector3DData.FromVector3D(forward);
            o.Up = Vector3DData.FromVector3D(up);
            if (o.AnchorEntityId != 0 && (anchorBlock == null || anchorBlock.EntityId != o.AnchorEntityId))
                anchorBlock = AnchorDirectory.Resolve(block, o.AnchorEntityId);
            if (o.AnchorEntityId != 0 && anchorBlock != null)
            {
                MatrixD m = anchorBlock.WorldMatrix;
                o.ForwardLocal = Vector3DData.FromVector3D(WorldToAnchorDir(ref m, forward));
                o.UpLocal = Vector3DData.FromVector3D(WorldToAnchorDir(ref m, up));
            }
        }

        // Position a tool's working point instead of the controller (e.g. welder tip 'reach' metres ahead of the block)
        public void SetOrderReferenceBlock(IMyCubeBlock tool, double reach)
        {
            MatrixD w = shipController.WorldMatrix;
            Vector3D tip = tool.WorldMatrix.Translation + tool.WorldMatrix.Forward * reach;
            activeFlightOrder.ReferenceOffset = Vector3DData.FromVector3D(
                Vector3D.TransformNormal(tip - w.Translation, MatrixD.Transpose(w)));
        }

        /// <summary>
        /// Adds a leg after the current route. The leg that currently ends the route will pass its waypoint
        /// with 'passBehavior' / 'exitSpeed' once this leg is handed over. Without an active order this is OrderGoTo.
        /// </summary>
        public FlightOrder QueueGoTo(Vector3D target, WaypointBehavior passBehavior, float exitSpeed)
        {
            FlightOrder last = lastQueuedLeg ?? nextLeg ?? activeFlightOrder;
            if (last == null) return OrderGoTo(target);
            last.Behavior = passBehavior;
            last.ExitSpeed = exitSpeed;
            Vector3DData from = last.Target;
            var leg = new FlightOrder
            {
                Target = Vector3DData.FromVector3D(target),
                ApproachFrom = from,
                TransitStart = from,
                Phase = FlightPhase.Approach,
                Orientation = FlightOrientationMode.FaceTravel,
                ArrivalTolerance = settings.WaypointTolerance
            };
            if (last.AnchorEntityId != 0)
            {
                // Stay in the anchor's moving frame: the new leg starts at the previous leg's local target
                IMyTerminalBlock anchor = anchorBlock != null && anchorBlock.EntityId == last.AnchorEntityId
                    ? anchorBlock : AnchorDirectory.Resolve(block, last.AnchorEntityId);
                if (anchor != null)
                {
                    MatrixD m = anchor.WorldMatrix;
                    leg.AnchorEntityId = last.AnchorEntityId;
                    leg.TargetLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref m, target));
                    leg.ApproachFromLocal = last.TargetLocal;
                    leg.TransitStartLocal = last.TargetLocal;
                }
            }
            EnqueueLeg(leg);
            return leg;
        }

        /// <summary>
        /// Hand-over point for the pathfinding manager: legs queued here are taken when the active leg asks for
        /// its successor (NextLegRequested), before it has to commit to braking.
        /// </summary>
        public void EnqueueLeg(FlightOrder leg)
        {
            if (leg == null) return;
            if (activeFlightOrder == null)
            {
                ResetOrderState();
                BeginFlight();
                activeFlightOrder = leg;     // nothing to chain onto: fly it now
                return;
            }
            queuedLegs.Enqueue(leg);
            lastQueuedLeg = leg;
        }

        public void ClearFlightOrder()
        {
            activeFlightOrder = null;
            ResetOrderState();
        }

        private void ResetOrderState()
        {
            CaptureFlightState();            // idle drones don't refresh it every frame
            WakeFrameUpdates();
            commandedVelocity = flightState.LinearVelocity;
            nextLeg = null;
            nextLegRequested = false;
            queuedLegs.Clear();
            lastQueuedLeg = null;
            anchorBlock = null;
            legPassed = false;
            dockingHomeId = 0;
            arrivalReported = false;
        }

        // Anchored orders: rebuild world fields from the anchor frame. Also sets Frame/RelativeVelocity.
        // Returns false when the anchor can no longer be used.
        private bool RefreshAnchoredOrder(FlightOrder o)
        {
            if (o.AnchorEntityId == 0) return true;
            if (anchorBlock == null || anchorBlock.EntityId != o.AnchorEntityId || anchorBlock.Closed)
            {
                anchorBlock = AnchorDirectory.Resolve(block, o.AnchorEntityId);   // ownership re-checked here
                if (anchorBlock == null) return false;
            }
            var physics = anchorBlock.CubeGrid?.Physics;
            if (physics == null) return false;

            MatrixD m = anchorBlock.WorldMatrix;
            o.Target       = Vector3DData.FromVector3D(AnchorToWorldPoint(ref m, o.TargetLocal.ToVector3D()));
            o.ApproachFrom = Vector3DData.FromVector3D(AnchorToWorldPoint(ref m, o.ApproachFromLocal.ToVector3D()));
            o.TransitStart = Vector3DData.FromVector3D(AnchorToWorldPoint(ref m, o.TransitStartLocal.ToVector3D()));
            if (o.Orientation == FlightOrientationMode.Explicit)
            {
                if (o.ForwardLocal.ToVector3D().LengthSquared() < 1e-6 || o.UpLocal.ToVector3D().LengthSquared() < 1e-6)
                {
                    // Set in world space only (e.g. by a leg provider): capture it in the anchor frame once
                    o.ForwardLocal = Vector3DData.FromVector3D(WorldToAnchorDir(ref m, o.Forward.ToVector3D()));
                    o.UpLocal = Vector3DData.FromVector3D(WorldToAnchorDir(ref m, o.Up.ToVector3D()));
                }
                o.Forward = Vector3DData.FromVector3D(AnchorToWorldDir(ref m, o.ForwardLocal.ToVector3D()));
                o.Up      = Vector3DData.FromVector3D(AnchorToWorldDir(ref m, o.UpLocal.ToVector3D()));
            }
            flightState.FrameVelocity = physics.GetVelocityAtPoint(flightState.Position);
            flightState.RelativeVelocity = flightState.LinearVelocity - flightState.FrameVelocity;
            return true;
        }

        // Anchor frame convention: (X, Y, Z) = (Right, Up, Forward) of the anchor block
        private static Vector3D AnchorToWorldPoint(ref MatrixD m, Vector3D local)
        {
            return m.Translation + m.Right * local.X + m.Up * local.Y + m.Forward * local.Z;
        }
        private static Vector3D AnchorToWorldDir(ref MatrixD m, Vector3D local)
        {
            return m.Right * local.X + m.Up * local.Y + m.Forward * local.Z;
        }
        private static Vector3D WorldToAnchorPoint(ref MatrixD m, Vector3D world)
        {
            return WorldToAnchorDir(ref m, world - m.Translation);
        }
        private static Vector3D WorldToAnchorDir(ref MatrixD m, Vector3D w)
        {
            return new Vector3D(Vector3D.Dot(w, m.Right), Vector3D.Dot(w, m.Up), Vector3D.Dot(w, m.Forward));
        }

        // Asks for the next leg before braking has to start; hands over when the waypoint is passed
        // (RunThrough / SlowApproach) or reached (FullStop).
        /// <summary>
        /// Hands over to the next queued leg now, e.g. when a leg can't settle within its tolerance (stalled close
        /// to its waypoint). False when there is no next leg.
        /// </summary>
        private bool ForceLegHandover()
        {
            FlightOrder next = nextLeg;
            if (next == null && queuedLegs.Count > 0)
            {
                next = queuedLegs.Dequeue();
                if (queuedLegs.Count == 0) lastQueuedLeg = null;
            }
            if (next == null) return false;
            nextLeg = null;
            nextLegRequested = false;
            legPassed = false;
            if (activeFlightOrder != null && next.AnchorEntityId != activeFlightOrder.AnchorEntityId) anchorBlock = null;
            activeFlightOrder = next;
            return true;
        }

        private void UpdateLegChaining(FlightOrder o)
        {
            if (o.Phase != FlightPhase.Approach) return;
            if (nextLeg == null)
            {
                if (!nextLegRequested)
                {
                    bool request = o.Arrived || legDir.LengthSquared() < 0.5;   // zero-length leg or already there
                    if (!request)
                    {
                        double v = Math.Max(0, Vector3D.Dot(flightState.RelativeVelocity, legDir));
                        double brakeDist = v * v / (2 * AvailableAccel(-legDir) * TRANS_BRAKE);
                        request = legRemaining < brakeDist * 1.5 + 20.0;
                    }
                    if (request)
                    {
                        nextLegRequested = true;
                        var handler = NextLegRequested;
                        if (handler != null) handler(this);   // manager answers via EnqueueLeg; until then this leg brakes to a stop
                    }
                }
                if (nextLegRequested && queuedLegs.Count > 0)
                {
                    nextLeg = queuedLegs.Dequeue();
                    if (queuedLegs.Count == 0) lastQueuedLeg = null;
                }
                return;
            }
            bool handOver = o.Behavior == WaypointBehavior.FullStop || legDir.LengthSquared() < 0.5 ? o.Arrived : legPassed;
            if (!handOver) return;
            FlightOrder next = nextLeg;
            nextLeg = null;
            nextLegRequested = false;
            legPassed = false;
            if (next.AnchorEntityId != o.AnchorEntityId) anchorBlock = null;   // re-resolved on the next refresh
            activeFlightOrder = next;
        }
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
        // Largest acceleration the drone can hold along u (world unit vector), gravity included.
        // Gravity is split per controller axis: a tilted drone spends part of each group just holding
        // its own weight (e.g. pitched 10° nose-down, the Backward group must also cancel g·sin10°).
        private double AvailableAccel(Vector3D u)
        {
            if (physicalMass <= 0) return 0.1;
            double invMass = 1.0 / physicalMass;
            double s = double.MaxValue;
            s = AxisLimitG(s, Vector3D.Dot(u, flightState.WorldMatrix.Right),   Vector3D.Dot(flightState.Gravity, flightState.WorldMatrix.Right),
                EffectiveMax(Base6Directions.Direction.Right) * invMass,   EffectiveMax(Base6Directions.Direction.Left) * invMass);
            s = AxisLimitG(s, Vector3D.Dot(u, flightState.WorldMatrix.Up),      Vector3D.Dot(flightState.Gravity, flightState.WorldMatrix.Up),
                EffectiveMax(Base6Directions.Direction.Up) * invMass,      EffectiveMax(Base6Directions.Direction.Down) * invMass);
            s = AxisLimitG(s, Vector3D.Dot(u, flightState.WorldMatrix.Forward), Vector3D.Dot(flightState.Gravity, flightState.WorldMatrix.Forward),
                EffectiveMax(Base6Directions.Direction.Forward) * invMass, EffectiveMax(Base6Directions.Direction.Backward) * invMass);
            if (s == double.MaxValue) s = 0.1;
            return MathHelperD.Clamp(s, 0.1, 50.0);
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
            Vector3D frameVel = flightState.FrameVelocity;
            if (o.Phase == FlightPhase.MatchSpeed)
            {
                legPassed = false;
                legDir = Vector3D.Zero;
                return frameVel;      // hold still relative to the anchor until the speeds match
            }
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
            bool strict = o.LineTolerance > 0 && o.Phase == FlightPhase.Approach;   // final approach of a strict order
            double capture = strict ? o.LineTolerance : XT_CAPTURE;
            if (!o.Captured && xt < capture) o.Captured = true;

            double remaining = len - s;
            legDir = d;
            legRemaining = remaining;
            legPassed = len > 1e-3 && Vector3D.Dot(p - t, d) >= 0;     // crossed the plane through the waypoint

            // Brake to the exit speed when a next leg is known, else to a stop (final-leg rules apply only then)
            double vExit = ExitSpeedFor(o);
            bool stopping = vExit <= 0;
            double maxSpeed = stopping && remaining < FINAL_LEG ? FinalSpeedFor(o) : MaxSpeed;

            double along = 0;
            if (len > 1e-3)
            {
                double brake = AvailableAccel(-d) * TRANS_BRAKE;
                double closing = Math.Max(0, Vector3D.Dot(flightState.RelativeVelocity, d));
                double usable  = Math.Max(0, remaining - closing / Kp);        // distance eaten by controller lag
                if (stopping)
                    along = usable > TRANS_LINEAR
                        ? Math.Sqrt(2 * brake * usable)
                        : usable * Math.Sqrt(2 * brake / TRANS_LINEAR);
                else
                    along = Math.Sqrt(vExit * vExit + 2 * brake * usable);
                along = Math.Min(along, maxSpeed);

                if (flightState.InGravity)
                {
                    double down = -Vector3D.Dot(d, flightState.GravityUp);     // > 0 when the line descends
                    if (down > 1e-3) along = Math.Min(along, MAX_DESCENT_SPEED / down);
                }
            }

            // Don't advance while far off the line, or on the final leg while still turning
            double gate = strict
                ? MathHelperD.Clamp(2.0 - xt / o.LineTolerance, 0, 1)          // strict: full speed within tolerance, stop at 2x
                : MathHelperD.Clamp(1.0 - xt / (XT_CAPTURE * 4), 0, 1);
            if (stopping && remaining < FINAL_LEG && orientationError > ORIENT_GATE) gate = 0;

            Vector3D vCross = Vector3D.Zero;
            if (xt > 1e-3)
            {
                Vector3D xDir = crossTrack / xt;
                double xBrake = AvailableAccel(-xDir) * TRANS_BRAKE;
                vCross = xDir * Math.Min(maxSpeed, Math.Min(XT_GAIN * xt, Math.Sqrt(2 * xBrake * xt)));
            }
            Vector3D desired = d * (along * gate) + vCross;

            // The reference point also moves when the drone rotates (v_ref = v + ω × r); compensate.
            // Anchored orders fly in the anchor's moving frame: add its velocity as feed-forward.
            return frameVel + desired - Vector3D.Cross(flightState.AngularVelocity, rWorld);
        }
        private double ExitSpeedFor(FlightOrder o)
        {
            if (nextLeg == null || o.Phase != FlightPhase.Approach || o.Behavior == WaypointBehavior.FullStop) return 0;
            double v = Math.Max(0, o.ExitSpeed);
            if (o.Behavior == WaypointBehavior.SlowApproach) v = Math.Min(v, ApproachSpeed);
            return Math.Min(v, MaxSpeed);
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
        // Net acceleration a along u needs thrust accel (a·comp − gAxis) on this axis, which must stay
        // inside [−negCap, posCap]. Returns the tighter of s and this axis' limit on a.
        private static double AxisLimitG(double s, double comp, double gAxis, double posCap, double negCap)
        {
            if (Math.Abs(comp) < 1e-3) return s;
            double limit = comp > 0 ? (posCap + gAxis) / comp : (negCap - gAxis) / -comp;
            return Math.Min(s, limit);
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
        private const double GAIN_MIN = 0.5, GAIN_MAX = 1.5;
        private const double GAIN_OBSTRUCTED_RATIO = 0.25;   // below this share of the expected push: obstructed, not learned
        private const int GAIN_OBSTRUCTED_TICKS = 180;       // 3 s of that: flag it (LCD warning)
        private readonly int[] obstructedTicks = new int[6];
        private int thrustObstructedDir = -1;
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
            // The obstruction flag needs consecutive samples of the group actually pushing
            if (active != p) ClearObstructed(p);
            if (active != n) ClearObstructed(n);
            if (active < 0) return;
            if (groupThrottle[active] < 0.1 || Math.Abs(groupThrottle[active] - groupThrottleAvg[active]) > 0.02)
            {
                ClearObstructed(active);   // not pushing hard / not steady yet
                return;
            }

            double expected = groupThrottle[active] * ProfileMax(active) / physicalMass;    // nominal, before correction
            if (expected < 0.5) return;
            double measured = Vector3D.Dot(thrustAccel, axis) * (active == p ? 1 : -1);
            double ratio = measured / expected;
            if (ratio < GAIN_OBSTRUCTED_RATIO)
            {
                // Next to no effect: something is in the way (or pushing back), not a weak thruster group.
                // Learning from this would cripple the drone for the rest of the flight.
                if (++obstructedTicks[active] > GAIN_OBSTRUCTED_TICKS) thrustObstructedDir = active;
                return;
            }
            obstructedTicks[active] = 0;
            if (thrustObstructedDir == active) thrustObstructedDir = -1;
            ratio = MathHelperD.Clamp(ratio, GAIN_MIN, GAIN_MAX);
            thrustGain[active] += (ratio - thrustGain[active]) * GAIN_LEARN_RATE;
        }

        private void ClearObstructed(int dir)
        {
            obstructedTicks[dir] = 0;
            if (thrustObstructedDir == dir) thrustObstructedDir = -1;
        }

        // Each take-off starts from the thrusters' own figures: gains learned on another load / in another place
        // (or while pushing against something) don't carry over
        private void ResetThrustGains()
        {
            for (int i = 0; i < 6; i++)
            {
                thrustGain[i] = 1;
                obstructedTicks[i] = 0;
            }
            thrustObstructedDir = -1;
        }
        // Debug aid: echo the learned factors to the block's custom info when any moved by more than 0.05
        private void ReportThrustGains()
        {
            bool changed = false;
            for (int i = 0; i < 6; i++)
                if (Math.Abs(thrustGain[i] - reportedThrustGain[i]) > 0.05) { changed = true; break; }
            if (!changed) return;
            for (int i = 0; i < 6; i++)
                reportedThrustGain[i] = thrustGain[i];
            Log.Debug("Drone {6}: thrust gain F{0:F2} B{1:F2} L{2:F2} R{3:F2} U{4:F2} D{5:F2}",
                thrustGain[0], thrustGain[1], thrustGain[2], thrustGain[3], thrustGain[4], thrustGain[5], _entityId);
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
            if (!IsServer) return;
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
                    if (settings.IsEnabled == value) return;
                    settings.IsEnabled = value;
                    if (IsServer && initialized)   // clients only mirror the flag; the server acts on it
                    {
                        if (settings.IsEnabled)
                        {
                            CheckCapabilities();
                            ApplyOperationMode();
                            Report("AI enabled ({0})", settings.OperationMode);
                        }
                        else DisableAI();
                    }
                    SaveSettings();
                    SyncSetting(DroneSettingKey.Enabled);
                    RefreshTerminal();   // controls that depend on the AI being off (operation mode)
                }
            }
        }

        // Changed only with the AI off (terminal); the server re-applies the mode's side effects when the AI goes on
        public long Terminal_OperationModeValue
        {
            get { return settings != null ? (long)settings.OperationMode : 0L; }
            set
            {
                if (settings == null || value < 0 || value > (long)OperationMode.ManagedByPlayer) return;
                if ((long)settings.OperationMode == value) return;
                settings.OperationMode = (OperationMode)value;
                SaveSettings();
                SyncSetting(DroneSettingKey.OperationModeValue);
                RefreshTerminal();   // job / debug controls enable by mode
            }
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
            // Docked to one of our / our faction's connectors: that connector becomes home, with this exact pose
            RequestAction(DroneAction.SetCurrentAsHome);
        }


        public const float WAYPOINT_TOLERANCE_MIN = 1f, WAYPOINT_TOLERANCE_MAX = 50f;
        public float Terminal_WaypointTolerance
        {
            get { return settings != null ? settings.WaypointTolerance : 5f; }
            set
            {
                if (settings == null) return;
                settings.WaypointTolerance = MathHelper.Clamp(value, WAYPOINT_TOLERANCE_MIN, WAYPOINT_TOLERANCE_MAX);
                SaveSettings();
                SyncSetting(DroneSettingKey.WaypointTolerance);
            }
        }

        // Each clamped on its own; odd combinations (approach > max...) are flagged in the LCD header, not corrected
        public const float MAX_SPEED_MIN = 0f, MAX_SPEED_MAX = 1000f;
        public const float APPROACH_SPEED_MIN = 0f, APPROACH_SPEED_MAX = 100f;
        public const float SAFE_SPEED_MIN = 0f, SAFE_SPEED_MAX = 50f;

        public float Terminal_MaxSpeed
        {
            get { return settings != null ? settings.MaxSpeed : 50f; }
            set { if (settings != null) { settings.MaxSpeed = MathHelper.Clamp(value, MAX_SPEED_MIN, MAX_SPEED_MAX); SaveSettings(); SyncSetting(DroneSettingKey.MaxSpeed); } }
        }

        public float Terminal_ApproachSpeed
        {
            get { return settings != null ? settings.ApproachSpeed : 5f; }
            set { if (settings != null) { settings.ApproachSpeed = MathHelper.Clamp(value, APPROACH_SPEED_MIN, APPROACH_SPEED_MAX); SaveSettings(); SyncSetting(DroneSettingKey.ApproachSpeed); } }
        }

        public float Terminal_SafeSpeed
        {
            get { return settings != null ? settings.SafeSpeed : 0.5f; }
            set { if (settings != null) { settings.SafeSpeed = MathHelper.Clamp(value, SAFE_SPEED_MIN, SAFE_SPEED_MAX); SaveSettings(); SyncSetting(DroneSettingKey.SafeSpeed); } }
        }

        // Max load, percent (0–200). Writer text shows the resulting mass limit.
        public float Terminal_MaxLoadGravity
        {
            get { return settings != null ? settings.MaxLoadGravity : 80f; }
            set { if (settings != null) { settings.MaxLoadGravity = MathHelper.Clamp(value, 0f, 200f); loadLevel = 0; SaveSettings(); SyncSetting(DroneSettingKey.MaxLoadGravity); } }
        }
        public float Terminal_MaxLoadSpace
        {
            get { return settings != null ? settings.MaxLoadSpace : 80f; }
            set { if (settings != null) { settings.MaxLoadSpace = MathHelper.Clamp(value, 0f, 200f); loadLevel = 0; SaveSettings(); SyncSetting(DroneSettingKey.MaxLoadSpace); } }
        }
        public void Terminal_WriteMaxLoad(StringBuilder sb, bool gravity)
        {
            if (settings == null) return;
            float pct = gravity ? settings.MaxLoadGravity : settings.MaxLoadSpace;
            double mass = (gravity ? MaxMassGravity() : MaxMassSpace()) * pct / 100.0;
            sb.Append(pct.ToString("F0")).Append("% (");
            if (mass >= 10000) sb.Append((mass / 1000).ToString("F1")).Append(" t)");
            else sb.Append(mass.ToString("F0")).Append(" kg)");
        }

        // LCD output
        public Color Terminal_LcdForeground
        {
            get { return settings != null ? new Color(settings.LcdForeground) : new Color(0, 255, 0); }
            set { if (settings != null) { settings.LcdForeground = value.PackedValue; display.MarkStyleDirty(); SaveSettings(); SyncSetting(DroneSettingKey.LcdForeground); } }
        }
        public Color Terminal_LcdBackground
        {
            get { return settings != null ? new Color(settings.LcdBackground) : new Color(32, 32, 32); }
            set { if (settings != null) { settings.LcdBackground = value.PackedValue; display.MarkStyleDirty(); SaveSettings(); SyncSetting(DroneSettingKey.LcdBackground); } }
        }
        public float Terminal_LcdFontSize
        {
            get { return settings != null ? settings.LcdFontSize : 0.6f; }
            set { if (settings != null) { settings.LcdFontSize = MathHelper.Clamp(value, 0.1f, 2f); display.MarkStyleDirty(); SaveSettings(); SyncSetting(DroneSettingKey.LcdFontSize); } }
        }
        public bool Terminal_LcdShowHeader
        {
            get { return settings != null && settings.LcdShowHeader; }
            set { if (settings != null) { settings.LcdShowHeader = value; display.MarkStyleDirty(); SaveSettings(); SyncSetting(DroneSettingKey.LcdShowHeader); } }
        }

        public void Terminal_GoHome()
        {
            RequestAction(DroneAction.GoHome);
        }

        public void Terminal_StopOrders()
        {
            RequestAction(DroneAction.StopOrders);
        }

        public bool Terminal_AlignToPGravity
        {
            get { return settings != null && settings.AlignToPGravity; }
            set { if (settings != null) { settings.AlignToPGravity = value; SaveSettings(); SyncSetting(DroneSettingKey.AlignToPGravity); } }
        }

        public float Terminal_MaxPitchDegrees
        {
            get { return settings != null ? settings.PGravityAlignMaxPitchDegrees : 10f; }
            set { if (settings != null) { settings.PGravityAlignMaxPitchDegrees = MathHelper.Clamp(value, 0f, 90f); SaveSettings(); SyncSetting(DroneSettingKey.MaxPitchDegrees); } }
        }

        public float Terminal_MaxRollDegrees
        {
            get { return settings != null ? settings.PGravityAlignMaxRollDegrees : 10f; }
            set { if (settings != null) { settings.PGravityAlignMaxRollDegrees = MathHelper.Clamp(value, 0f, 90f); SaveSettings(); SyncSetting(DroneSettingKey.MaxRollDegrees); } }
        }

        public StringBuilder Terminal_LCDScreenTag
        {
            get { return settings != null ? new StringBuilder(settings.LCDScreenTag) : new StringBuilder(""); }
            set
            {
                if (settings == null) return;
                string tag = value != null ? value.ToString().Trim() : "";
                settings.LCDScreenTag = tag.Length > 0 ? tag : "[AutomataDrone]";
                if (shipController != null) display.FindPanels(shipController.CubeGrid, settings.LCDScreenTag);
                SaveSettings(); SyncSetting(DroneSettingKey.LCDScreenTag);
            }
        }

        public bool Terminal_MonitorHydrogen
        {
            get { return settings != null && settings.MonitorHydrogenLevels; }
            set { if (settings != null) { settings.MonitorHydrogenLevels = value; SaveSettings(); SyncSetting(DroneSettingKey.MonitorHydrogen); } }
        }

        public float Terminal_H2RefuelThreshold
        {
            get { return settings != null ? settings.HydrogenRefuelThreshold : 25f; }
            set { if (settings != null) { settings.HydrogenRefuelThreshold = MathHelper.Clamp(value, 5f, 50f); SaveSettings(); SyncSetting(DroneSettingKey.H2RefuelThreshold); } }
        }

        public float Terminal_H2OperationalThreshold
        {
            get { return settings != null ? settings.HydrogenOperationalThreshold : 50f; }
            set { if (settings != null) { settings.HydrogenOperationalThreshold = MathHelper.Clamp(value, 10f, 95f); SaveSettings(); SyncSetting(DroneSettingKey.H2OperationalThreshold); } }
        }

        public bool Terminal_AlwaysRefuel
        {
            get { return settings != null && settings.AlwaysRefuelWhenDocked; }
            set { if (settings != null) { settings.AlwaysRefuelWhenDocked = value; SaveSettings(); SyncSetting(DroneSettingKey.AlwaysRefuel); } }
        }

        public bool Terminal_MonitorBattery
        {
            get { return settings != null && settings.MonitorBatteryLevels; }
            set { if (settings != null) { settings.MonitorBatteryLevels = value; SaveSettings(); SyncSetting(DroneSettingKey.MonitorBattery); } }
        }

        public float Terminal_BatteryRefuelThreshold
        {
            get { return settings != null ? settings.BatteryRefuelThreshold : 20f; }
            set { if (settings != null) { settings.BatteryRefuelThreshold = MathHelper.Clamp(value, 5f, 50f); SaveSettings(); SyncSetting(DroneSettingKey.BatteryRefuelThreshold); } }
        }

        public float Terminal_BatteryOperationalThreshold
        {
            get { return settings != null ? settings.BatteryOperationalThreshold : 80f; }
            set { if (settings != null) { settings.BatteryOperationalThreshold = MathHelper.Clamp(value, 10f, 95f); SaveSettings(); SyncSetting(DroneSettingKey.BatteryOperationalThreshold); } }
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
            RequestAction(new DroneActionPacket { Action = DroneAction.Orient, VectorA = Vector3DData.FromVector3D(orientationTargetDebug), HasVectorA = true });
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
            if (!navDebugTarget.HasValue) { Feedback("Nav: invalid target GPS"); return; }
            var p = new DroneActionPacket { Action = DroneAction.NavigateTo, VectorA = Vector3DData.FromVector3D(navDebugTarget.Value), HasVectorA = true };
            if (navDebugTargetApproachFrom.HasValue)
            {
                p.VectorB = Vector3DData.FromVector3D(navDebugTargetApproachFrom.Value);
                p.HasVectorB = true;
            }
            RequestAction(p);
        }

        public void Terminal_Debug_QueueWaypoint()
        {
            if (!navDebugTarget.HasValue) { Feedback("Nav: invalid target GPS"); return; }
            RequestAction(new DroneActionPacket { Action = DroneAction.QueueWaypoint, VectorA = Vector3DData.FromVector3D(navDebugTarget.Value), HasVectorA = true });
        }

        private void ExecuteQueueWaypoint(Vector3D target)
        {
            bool chained = activeFlightOrder != null;
            QueueGoTo(target, WaypointBehavior.RunThrough, ApproachSpeed * 2f);
            if (chained) Report("Nav: waypoint queued (run-through at {0:F0} m/s)", ApproachSpeed * 2f);
        }

        #region Terminal - place mount (debug)
        public void Terminal_Debug_MountListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            AddMountItem(items, selected, welder, MOUNT_WELDER, "Welder");
            AddMountItem(items, selected, grinder, MOUNT_GRINDER, "Grinder");
            AddMountItem(items, selected, drill, MOUNT_DRILL, "Drill");
            // Connectors: use "Go home" (docking)
        }
        private void AddMountItem(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected,
                                  IMyCubeBlock b, int code, string text)
        {
            if (b == null) return;
            var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(text), MyStringId.NullOrEmpty, code);
            items.Add(item);
            if (code == debugMountSelection) selected.Add(item);
        }
        public void Terminal_Debug_SelectMount(List<MyTerminalControlListBoxItem> selected)
        {
            object data = selected != null && selected.Count > 0 ? selected[0].UserData : null;
            debugMountSelection = data is int ? (int)data : MOUNT_NONE;
        }
        public StringBuilder Textbox_Debug_PlaceMountTargetGPS
        {
            get { return debugMountTarget.HasValue ? new StringBuilder(ConvertVectorToGPS("DebugMountTarget", debugMountTarget.Value)) : new StringBuilder(); }
            set { debugMountTarget = ParseGPSString(value != null ? value.ToString() : null); }
        }
        public void Terminal_Debug_PlaceMount()
        {
            if (!debugMountTarget.HasValue) { Feedback("Mount: invalid target GPS"); return; }
            if (debugMountSelection == MOUNT_NONE) { Feedback("Mount: select a mount first"); return; }
            RequestAction(new DroneActionPacket { Action = DroneAction.PlaceMount, Code = debugMountSelection,
                VectorA = Vector3DData.FromVector3D(debugMountTarget.Value), HasVectorA = true });
        }

        private void ExecutePlaceMount(int code, Vector3D target)
        {
            FlightOrder order = null;
            string name;
            switch (code)
            {
                case MOUNT_WELDER:  name = "welder";  order = OrderWorkAt(ref welderMount, target);  break;
                case MOUNT_GRINDER: name = "grinder"; order = OrderWorkAt(ref grinderMount, target); break;
                case MOUNT_DRILL:   name = "drill";   order = OrderWorkAt(ref drillMount, target);   break;
                default:
                    Report("Mount: select a mount first");
                    return;
            }
            if (order == null) { Report("Mount: {0} not available", name); return; }
            Report("Mount: {0} -> {1:F0} m", name, Vector3D.Distance(flightState.Position, order.Target.ToVector3D()));
        }
        #endregion

        #region Terminal - home connector & observation area
        public bool HasHomeConnector { get { return settings != null && settings.HomeConnectorId != 0; } }

        // Owner/faction check is repeated at a low rate (ownership can change); cached in between.
        public IMyShipConnector GetHomeConnector()
        {
            if (settings == null || settings.HomeConnectorId == 0) return null;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (homeConnector != null && !homeConnector.Closed && homeConnector.EntityId == settings.HomeConnectorId
                && now - homeConnectorCheckedFrame < 100)
                return homeConnector;
            homeConnector = AnchorDirectory.Resolve(block, settings.HomeConnectorId) as IMyShipConnector;
            homeConnectorCheckedFrame = now;
            return homeConnector;
        }

        public void Terminal_HomeConnectorListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var none = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("(none)"), MyStringId.NullOrEmpty, 0L);
            items.Add(none);
            if (settings == null || AutomataSession.Instance == null) return;
            RequestAnchorListIfStale();
            bool found = false;
            var entries = AutomataSession.Instance.Anchors.Get(block);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Kind != AnchorKind.Connector || !AnchorDirectory.ViewerAllowed(entries[i].OwnerId)) continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(FormatAnchor(entries[i])), MyStringId.NullOrEmpty, entries[i].EntityId);
                items.Add(item);
                if (entries[i].EntityId == settings.HomeConnectorId) { selected.Add(item); found = true; }
            }
            if (!found && settings.HomeConnectorId == 0) selected.Add(none);
        }

        public void Terminal_SelectHomeConnector(List<MyTerminalControlListBoxItem> selected)
        {
            if (settings == null || selected == null || selected.Count == 0 || !(selected[0].UserData is long)) return;
            long id = (long)selected[0].UserData;
            if (id == settings.HomeConnectorId) return;
            RequestAction(new DroneActionPacket { Action = DroneAction.SelectHomeConnector, Id = id });
        }

        // Server: only ids from this drone's filtered list (drone owner AND requesting player), or none
        private void ExecuteSelectHomeConnector(long id, long identity)
        {
            if (id != 0 && !IsListedAnchor(id, AnchorKind.Connector, identity)) return;
            if (id == settings.HomeConnectorId) return;
            settings.HomeConnectorId = id;
            settings.HomeDockConnectorId = 0;
            settings.HomeDockRecorded = false;
            homeConnector = null;
            if (id == 0 && settings.ObservationAreaDraw) Terminal_ObservationAreaDraw = false;
            var home = GetHomeConnector();
            if (home != null && initialized)
            {
                if (EnsureDockPose(home, true)) Report("Home set: {0}", home.CustomName);
                else Report("Home set: {0} (no drone connector can dock)", home.CustomName);
            }
            SaveSettings();
            SyncSetting(DroneSettingKey.HomeConnector);   // clients: selection + observation-area controls
            RefreshTerminal();
        }

        public void Terminal_ScanAnchors()
        {
            RequestAction(DroneAction.ScanAnchors);
        }

        private void ExecuteScanAnchors(ulong steamId, long identity)
        {
            if (AutomataSession.Instance == null) return;
            int wait;
            if (!AutomataSession.Instance.Anchors.TryManualScan(block, out wait))
            {
                Report("Scan: wait {0} s", wait);
                return;
            }
            Report("Scan: {0} connectors/beacons in range", AutomataSession.Instance.Anchors.Get(block).Count);
            SendAnchorList(steamId, identity);
        }

        // "Add by name": exact connector name, searched within the server's name-search radius
        private string connectorNameQuery = "";
        public StringBuilder Textbox_ConnectorNameQuery
        {
            get { return new StringBuilder(connectorNameQuery); }
            set { connectorNameQuery = value != null ? value.ToString() : ""; }
        }

        public void Terminal_AddConnectorByName()
        {
            if (string.IsNullOrWhiteSpace(connectorNameQuery)) { Feedback("Add: enter a connector name"); return; }
            RequestAction(new DroneActionPacket { Action = DroneAction.AddConnectorByName, Text = connectorNameQuery.Trim() });
        }

        private void ExecuteAddConnectorByName(string name, ulong steamId, long identity)
        {
            if (AutomataSession.Instance == null || block == null || string.IsNullOrWhiteSpace(name)) return;
            int wait;
            int added = AutomataSession.Instance.Anchors.TryAddByName(block, name, identity, out wait);
            if (added < 0) { Report("Add: wait {0} s", wait); return; }
            if (added == 0) { Report("Add: connector \"{0}\" not found", name); return; }
            Report(added == 1 ? "Added {0} matching connector" : "Added {0} matching connectors", added);
            SendAnchorList(steamId, identity);
        }

        // viewerIdentity: the player asking (server side); 0 = the local player (UI)
        private bool IsListedAnchor(long id, AnchorKind kind, long viewerIdentity = 0)
        {
            if (AutomataSession.Instance == null) return false;
            var entries = AutomataSession.Instance.Anchors.Get(block);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].EntityId != id || entries[i].Kind != kind) continue;
                return viewerIdentity != 0 ? AnchorDirectory.IsAllowed(viewerIdentity, entries[i].OwnerId)
                                           : AnchorDirectory.ViewerAllowed(entries[i].OwnerId);
            }
            return false;
        }

        private static string FormatAnchor(AnchorEntry e)
        {
            return e.Distance >= 1000
                ? string.Format("{0} ({1:F1} km)", e.Name, e.Distance / 1000.0)
                : string.Format("{0} ({1:F0} m)", e.Name, e.Distance);
        }

        public bool Terminal_ObservationAreaDraw
        {
            get { return settings != null && settings.ObservationAreaDraw; }
            set
            {
                if (settings == null) return;
                bool on = value && GetHomeConnector() != null;
                settings.ObservationAreaDraw = on;
                observationDrawStartFrame = MyAPIGateway.Session.GameplayFrameCounter;
                UpdateDrawRequest();
            }
        }

        // axis: 0 = X (width, right), 1 = Y (height, up), 2 = Z (depth, forward)
        // axis: 0 = X, 1 = Y, 2 = Z (Vector3I.AxisValue uses Base6Directions.Axis, where 0 is Z)
        private static int GetAxis(Vector3I v, int axis)
        {
            return axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        }

        // Whole observation area at once (received from the network): clamped like the sliders
        private void SetObservationArea(Vector3I size, Vector3I offset)
        {
            if (settings == null) return;
            settings.ObservationAreaSizeBlocks = Vector3IData.FromVector3I(Vector3I.Clamp(size, new Vector3I(OBSERVATION_SIZE_MIN), new Vector3I(OBSERVATION_SIZE_MAX)));
            settings.ObservationAreaOffsetBlocks = Vector3IData.FromVector3I(Vector3I.Clamp(offset, new Vector3I(-OBSERVATION_OFFSET_MAX), new Vector3I(OBSERVATION_OFFSET_MAX)));
            SaveSettings();
            SyncSetting(DroneSettingKey.ObservationArea);
        }

        private static Vector3I WithAxis(Vector3I v, int axis, int value)
        {
            if (axis == 0) v.X = value; else if (axis == 1) v.Y = value; else v.Z = value;
            return v;
        }

        // in blocks (1 block = OBSERVATION_UNIT metres)
        public int Terminal_GetObservationSize(int axis)
        {
            return settings != null ? GetAxis(settings.ObservationAreaSizeBlocks.ToVector3I(), axis) : 5;
        }
        public void Terminal_SetObservationSize(int axis, float value)
        {
            if (settings == null) return;
            Vector3I v = settings.ObservationAreaSizeBlocks.ToVector3I();
            v = WithAxis(v, axis, MathHelper.Clamp((int)Math.Round(value), OBSERVATION_SIZE_MIN, OBSERVATION_SIZE_MAX));
            settings.ObservationAreaSizeBlocks = Vector3IData.FromVector3I(v);
            SaveSettings();
            SyncSetting(DroneSettingKey.ObservationArea);
        }
        public int Terminal_GetObservationOffset(int axis)
        {
            return settings != null ? GetAxis(settings.ObservationAreaOffsetBlocks.ToVector3I(), axis) : 0;
        }
        public void Terminal_SetObservationOffset(int axis, float value)
        {
            if (settings == null) return;
            Vector3I v = settings.ObservationAreaOffsetBlocks.ToVector3I();
            v = WithAxis(v, axis, MathHelper.Clamp((int)Math.Round(value), -OBSERVATION_OFFSET_MAX, OBSERVATION_OFFSET_MAX));
            settings.ObservationAreaOffsetBlocks = Vector3IData.FromVector3I(v);
            SaveSettings();
            SyncSetting(DroneSettingKey.ObservationArea);
        }

        /// <summary>
        /// Observation area in world space: box matrix (home connector orientation, box centre) and half extents.
        /// </summary>
        public bool TryGetObservationArea(out MatrixD boxMatrix, out Vector3D halfExtents)
        {
            boxMatrix = MatrixD.Identity;
            halfExtents = Vector3D.Zero;
            var home = GetHomeConnector();
            if (home == null || settings == null) return false;
            MatrixD m = home.WorldMatrix;
            Vector3I size = Vector3I.Clamp(settings.ObservationAreaSizeBlocks.ToVector3I(),
                new Vector3I(OBSERVATION_SIZE_MIN), new Vector3I(OBSERVATION_SIZE_MAX));
            Vector3I offset = Vector3I.Clamp(settings.ObservationAreaOffsetBlocks.ToVector3I(),
                new Vector3I(-OBSERVATION_OFFSET_MAX), new Vector3I(OBSERVATION_OFFSET_MAX));
            // Even sizes: shift half a block so the faces land on block boundaries around the connector's cell
            Vector3D centerBlocks = new Vector3D(
                offset.X + ((size.X & 1) == 0 ? 0.5 : 0),
                offset.Y + ((size.Y & 1) == 0 ? 0.5 : 0),
                offset.Z + ((size.Z & 1) == 0 ? 0.5 : 0));
            boxMatrix = m;
            boxMatrix.Translation = AnchorToWorldPoint(ref m, centerBlocks * OBSERVATION_UNIT);
            halfExtents = new Vector3D(size.X, size.Y, size.Z) * (OBSERVATION_UNIT * 0.5);   // symmetric: Z sign irrelevant
            return true;
        }

        /// <summary>
        /// Called from the session's Draw() (clients only). Returns false to stop being drawn.
        /// </summary>
        public void StopDebugDraw()
        {
            if (settings != null) settings.ObservationAreaDraw = false;
            StopNavigationDraw();
        }

        public bool DrawDebug()
        {
            if (settings == null || Entity == null || Entity.MarkedForClose)
                return false;
            bool area = DrawObservationArea();
            bool nav = DrawNavigationTargets();
            return area || nav;
        }

        // Keeps the session drawing this drone while any overlay is on
        private void UpdateDrawRequest()
        {
            if (AutomataSession.Instance != null)
                AutomataSession.Instance.RequestDraw(this, (settings != null && settings.ObservationAreaDraw) || navDrawOn);
        }

        private bool DrawObservationArea()
        {
            if (!settings.ObservationAreaDraw) return false;
            if (MyAPIGateway.Session.GameplayFrameCounter - observationDrawStartFrame > OBSERVATION_DRAW_TICKS)
            {
                settings.ObservationAreaDraw = false;
                return false;
            }
            MatrixD boxMatrix;
            Vector3D half;
            if (!TryGetObservationArea(out boxMatrix, out half))
            {
                settings.ObservationAreaDraw = false;
                return false;
            }
            var localBox = new BoundingBoxD(-half, half);
            // Translucent faces, plus a grid of ~2.5 m cells on every face (like the tool-sphere overlay)
            Color faces = Color.GreenYellow * 0.15f;
            Color grid = Color.GreenYellow * 0.6f;
            var cells = new Vector3I(
                Math.Max(1, (int)Math.Round(half.X * 2 / OBSERVATION_GRID_CELL)),
                Math.Max(1, (int)Math.Round(half.Y * 2 / OBSERVATION_GRID_CELL)),
                Math.Max(1, (int)Math.Round(half.Z * 2 / OBSERVATION_GRID_CELL)));
            // keep line size 0.002, makes an elegant box
            MySimpleObjectDraw.DrawTransparentBox(ref boxMatrix, ref localBox, ref faces, ref faces, MySimpleObjectRasterizer.Solid, cells,
                0.002f, DrawMaterial, null, false, -1, BlendTypeEnum.Standard);
            MySimpleObjectDraw.DrawTransparentBox(ref boxMatrix, ref localBox, ref grid, ref grid, MySimpleObjectRasterizer.Wireframe, cells,
                0.002f, null, DrawMaterial, false, -1, BlendTypeEnum.SDR);
            return true;
        }
        #endregion

        #region Terminal - relative navigation (debug)
        public void Terminal_Debug_AnchorListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            if (settings == null || AutomataSession.Instance == null) return;
            RequestAnchorListIfStale();
            var home = GetHomeConnector();
            if (home != null && AnchorDirectory.ViewerAllowed(home.OwnerId))
            {
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("Home: " + home.CustomName), MyStringId.NullOrEmpty, home.EntityId);
                items.Add(item);
                if (debugAnchorId == home.EntityId) selected.Add(item);
            }
            var entries = AutomataSession.Instance.Anchors.Get(block);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Kind != AnchorKind.Beacon || !AnchorDirectory.ViewerAllowed(entries[i].OwnerId)) continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("Beacon: " + FormatAnchor(entries[i])), MyStringId.NullOrEmpty, entries[i].EntityId);
                items.Add(item);
                if (entries[i].EntityId == debugAnchorId) selected.Add(item);
            }
        }
        public void Terminal_Debug_SelectAnchor(List<MyTerminalControlListBoxItem> selected)
        {
            debugAnchorId = selected != null && selected.Count > 0 && selected[0].UserData is long ? (long)selected[0].UserData : 0;
        }
        public StringBuilder Textbox_Debug_RelativeOffset
        {
            get
            {
                return debugRelativeOffset.HasValue
                    ? new StringBuilder(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}, {1:F1}, {2:F1}",
                        debugRelativeOffset.Value.X, debugRelativeOffset.Value.Y, debugRelativeOffset.Value.Z))
                    : new StringBuilder();
            }
            set { debugRelativeOffset = ParseVectorString(value != null ? value.ToString() : null); }
        }
        public void Terminal_Debug_NavigateRelative()
        {
            if (!debugRelativeOffset.HasValue) { Feedback("Rel: offset must be \"right, up, forward\""); return; }
            if (debugAnchorId == 0) { Feedback("Rel: select an anchor"); return; }
            RequestAction(new DroneActionPacket { Action = DroneAction.NavigateRelative, Id = debugAnchorId,
                VectorA = Vector3DData.FromVector3D(debugRelativeOffset.Value), HasVectorA = true });
        }

        private void ExecuteNavigateRelative(long anchorId, Vector3D offset, long identity)
        {
            IMyTerminalBlock anchor = AnchorDirectory.Resolve(block, anchorId);
            if (anchor == null || !AnchorDirectory.IsAllowed(identity, anchor.OwnerId)) { Report("Rel: anchor not available"); return; }
            var order = OrderGoToRelative(anchor, offset);
            if (order == null) { Report("Rel: order refused"); return; }
            Report("Rel: {0} + ({1:F0}, {2:F0}, {3:F0})", anchor.CustomName, offset.X, offset.Y, offset.Z);
        }
        #endregion

        // Rebuilds the terminal controls (list contents, Visible/Enabled) - standard ShowInToolbarConfig toggle
        private void RefreshTerminal()
        {
            var tb = block as IMyTerminalBlock;
            if (tb == null || MyAPIGateway.Utilities.IsDedicated) return;
            bool v = tb.ShowInToolbarConfig;
            tb.ShowInToolbarConfig = !v;
            tb.ShowInToolbarConfig = v;
        }

        // "x, y, z" (also accepts ';' or spaces as separators) or a GPS string
        private static Vector3D? ParseVectorString(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.StartsWith("GPS:", StringComparison.OrdinalIgnoreCase)) return ParseGPSString(text);
            var parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return null;
            double x, y, z;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Float;
            if (double.TryParse(parts[0], ns, ci, out x) && double.TryParse(parts[1], ns, ci, out y) && double.TryParse(parts[2], ns, ci, out z))
                return new Vector3D(x, y, z);
            return null;
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
            if (shipController == null) return;
            var massInfo = shipController.CalculateShipMass();
            physicalMass = massInfo.PhysicalMass;
            baseMass = massInfo.BaseMass;
        }
        // Natural gravity changes slowly with position: refreshed every 100 ticks (and at init / rescans)
        private void UpdateGravity()
        {
            if (shipController == null) return;
            flightState.Gravity = shipController.GetNaturalGravity();
            double g2 = flightState.Gravity.LengthSquared();
            flightState.InGravity = g2 > 0.01;
            flightState.GravityUp = flightState.InGravity ? -flightState.Gravity / Math.Sqrt(g2) : Vector3D.Zero;
        }
        private void CaptureFlightState()
        {
            if (shipController == null) return;
            flightState.WorldMatrix = shipController.WorldMatrix;
            MatrixD.Transpose(ref flightState.WorldMatrix, out flightState.WorldMatrixToLocal);
            var v = shipController.GetShipVelocities();
            flightState.LinearVelocity  = v.LinearVelocity;
            flightState.AngularVelocity = v.AngularVelocity;
            flightState.Position = flightState.WorldMatrix.Translation;
            flightState.FrameVelocity = Vector3D.Zero;             // RefreshAnchoredOrder overrides for anchored orders
            flightState.RelativeVelocity = flightState.LinearVelocity;
        }
        #endregion

        #region Static Helpers
        private static ToolMount BuildMount(IMyCubeBlock b, ref MatrixD worldToController, ref Vector3D controllerPos)
        {
            var m = new ToolMount { Block = b };
            MatrixD w = b.WorldMatrix;
            Vector3D point = w.Translation;

            Vector3D forward = w.Forward;

            var def = MyDefinitionManager.Static.GetCubeBlockDefinition(b.BlockDefinition);
            var tool = def as MyShipToolDefinition;
            var drillDef = def as MyShipDrillDefinition;
            if (tool != null)
            {
                // As MyShipToolBase does: the sphere hangs off the model dummy "detector_shiptool",
                // SensorOffset metres along the dummy's forward. Fallback: block centre.
                bool found = false;
                if (b.Model != null)
                {
                    dummyCache.Clear();
                    b.Model.GetDummies(dummyCache);
                    foreach (var kv in dummyCache)
                    {
                        if (kv.Key.IndexOf("detector_shiptool", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        // like MyShipToolBase.LoadDummies: the dummy's forward is used unnormalised (its scale applies)
                        Matrix dm = kv.Value.Matrix;
                        Vector3D dummyPos = Vector3D.Transform((Vector3D)dm.Translation, w);
                        Vector3D dummyFwd = Vector3D.TransformNormal((Vector3D)dm.Forward, w);
                        if (dummyFwd.LengthSquared() < 1e-6) dummyFwd = w.Forward;
                        point = dummyPos + dummyFwd * tool.SensorOffset;
                        forward = Vector3D.Normalize(dummyFwd);
                        found = true;
                        break;
                    }
                    dummyCache.Clear();
                }
                if (!found) point += w.Forward * tool.SensorOffset;
                m.WorkRadius = tool.SensorRadius;
            }
            else if (drillDef != null)
            {
                point += w.Forward * drillDef.CutOutOffset;  // MyShipDrill: cut-out sphere from the block centre
                m.WorkRadius = drillDef.CutOutRadius;
            }
            else if (b is IMyShipConnector)
            {
                // Connection point and axis exactly as the game computes them (see ConnectorGeometry)
                m.CanConnect = ConnectorGeometry(b, out point, out forward);
                m.SmallConnector = IsSmallConnector(b);
            }
            Vector3D up = w.Up - forward * Vector3D.Dot(w.Up, forward);
            if (up.LengthSquared() < 1e-3) up = w.Backward - forward * Vector3D.Dot(w.Backward, forward);
            up.Normalize();
            m.LocalPoint   = Vector3D.TransformNormal(point - controllerPos, worldToController);
            m.LocalForward = Vector3D.TransformNormal(forward, worldToController);
            m.LocalUp      = Vector3D.TransformNormal(up, worldToController);
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
