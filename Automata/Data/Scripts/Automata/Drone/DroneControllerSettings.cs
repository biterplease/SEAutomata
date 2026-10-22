using System.Collections.Generic;
using ProtoBuf;
using VRageMath;
using VRage.Game.ModAPI;
using Automata.Util;

namespace Automata.Drone
{

        /// <summary>
        /// Drone controller configurable settings.
        /// These should always be validated, and bounded by, the mod server settings.
        /// </summary>
        [ProtoContract]
        public class DroneControllerSettings
        {
                // Configuration values
                [ProtoMember(1)]
                public bool IsEnabled = false;
                [ProtoMember(2)]
                public OperationMode OperationMode = OperationMode.StandAlone;
                /// <summary>
                        /// Drone will always return to this connector when no more tasks are assigned.
                        /// WARNING: if this connector is set as home to more than one drone, may have catastrophic consequences.
                        /// </summary>
                [ProtoMember(3)]
                public Vector3D HomePosition;
                public Vector3D HomeForwardDirection = Vector3D.Forward;
                public bool EnforceHomeOrientation = true;
                // [ProtoMember(24)]
                // public IMyGps HomeIsRelativeTo;
                // ProtoMembers 4/5 were UseTaskTypeFilters / TaskTypeFilters (retired: TaskType is no longer a flags enum)
                /// <summary>
                        /// Normally, drones report all their capabilities to the Scheduler class
                        /// If enabled, it will filter out certain capabilities when reporting to its scheduler.
                        /// StandAlone
                        /// </summary>
                // ProtoMembers 6/7 were UseCapabilityFilters / CapabilityFilters (retired: see ExcludedCapabilities)
                /// <summary>
                        /// How close to the waypoint does the drone need to be, in meters.
                        /// </summary>
                [ProtoMember(8)]
                public float WaypointTolerance = 5.0f;
                /// <summary>
                /// Recommended speed when approaching targets, in m/s. Mostly for weld or grind tasks.
                /// Logistics task generally use docking speed for approach
                /// </summary>
                [ProtoMember(9)]
                public float ApproachSpeed = 10.0f;
                /// <summary>
                        /// Max speed in m/s.
                        /// </summary>
                [ProtoMember(10)]
                public float MaxSpeed = 100.0f;
                /// <summary>
                        /// Docking speed in m/s.
                        /// </summary>
                [ProtoMember(11)]
                public float SafeSpeed = 2.5f;
                [ProtoMember(12, IsRequired = true)]   // default true: false would otherwise not be saved
                public bool AlignToPGravity = true;
                [ProtoMember(13, IsRequired = true)]
                public float PGravityAlignMaxPitchDegrees = 45.0f;
                [ProtoMember(14, IsRequired = true)]
                public float PGravityAlignMaxRollDegrees = 45.0f;
                [ProtoMember(15)]
                public string LCDScreenTag = "[AutomataDrone]"; // Drone state will be sent to each LCD screen that contains this tag.

                // Power monitoring settings
                /// <summary>
                        /// Monitory hydrogen levels. Drone will auto-dock to refuel and resume on its own..
                        /// Disable if your drone is electric.
                        /// </summary>
                [ProtoMember(16)]
                public bool MonitorHydrogenLevels = false;
                /// <summary>
                        /// When hydrogen tank levels are below this threshold, percentage-wise, drone will return home
                        /// </summary>
                [ProtoMember(17)]
                public float HydrogenRefuelThreshold = 25.0f;     // Return to base when H2 below this %
                [ProtoMember(18)]
                public float HydrogenOperationalThreshold = 50.0f; // Resume ops when H2 above this %
                /// <summary>
                        /// Always refuel when drone is docked.
                        /// </summary>
                [ProtoMember(19)]
                public bool AlwaysRefuelWhenDocked = false; // Refuel everytime drone is docked

                /// <summary>
                /// Monitor battery levels. Drone will auto-dock to refuel and resume on its own.
                /// Disable if your drone has a reactor and will never run out of power.
                /// </summary>
                [ProtoMember(20)]
                public bool MonitorBatteryLevels = false;
                /// <summary>
                /// When battery storage levels are below this threshold, drone will return to home connector and recharge.
                /// </summary>
                [ProtoMember(21)]
                public float BatteryRefuelThreshold = 20.0f;      // Return to base when battery below this %
                [ProtoMember(22)]
                public float BatteryOperationalThreshold = 80.0f; // Resume ops when battery above this %
                [ProtoMember(23)]
                public Base6Directions.Direction ControllerForwardDirection = Base6Directions.Direction.Forward;
                /// <summary>
                /// If enabled, a drone that equips an ore detector, will store detected ore location 
                /// data and relay it to a mining surveyor block. Other drones, and orchestrators, will
                /// try to use this data to mine.
                /// </summary>
                [ProtoMember(25)]
                public bool SurveyForMiningData = false;
                // ProtoMember 26 was EnableInertialDampening (retired: the drone always holds itself)

                /// <summary>
                /// Home connector entity id (0 = none). Only connectors owned by the drone owner or the owner's
                /// faction can be selected; ownership is re-checked every time the id is resolved.
                /// </summary>
                [ProtoMember(27)]
                public long HomeConnectorId;

                // stand-alone construction function
                // Observation area: box around the home connector, in the connector's frame, in BLOCKS (1 = 2.5 m):
                // Size X = width (right), Y = height (up), Z = depth (forward), 1–10 blocks each.
                // Offset X/Y/Z along the connector's Right/Up/Forward, ±15 blocks (OBSERVATION_OFFSET_MAX). Even sizes are shifted half a block
                // towards +X/+Y/+Z so the box edges stay on the large-grid lattice.
                // (ProtoMembers 28/29 were the old metre-based Vector3DData size/offset - retired.)
                [ProtoMember(44, IsRequired = true)]
                public Vector3IData ObservationAreaSizeBlocks = new Vector3IData(5, 5, 5);
                [ProtoMember(45)]
                public Vector3IData ObservationAreaOffsetBlocks;
                // Runtime only (not serialized): drawing switches itself off after a while.
                public bool ObservationAreaDraw = false;
                // Stand-alone: the observation area centred on a GPS point instead of offset from the home connector
                // (sizes still along the connector's axes, in blocks; the offsets don't apply)
                [ProtoMember(71)]
                public bool ObservationAreaAtGps;
                [ProtoMember(72)]
                public Vector3DData ObservationGpsPosition;
                [ProtoMember(73)]
                public string ObservationGpsName;
                [ProtoMember(74)]
                public bool ObservationGpsSet;   // a point is selected (ObservationGpsPosition / Name)

                // Docking pose for the home connector, in the home connector's frame (Right, Up, Forward):
                // where the drone controller must be, and how it must be oriented, for its connector to lock.
                [ProtoMember(30)]
                public Vector3DData HomeDockOffset;
                [ProtoMember(31)]
                public Vector3DData HomeDockForward;
                [ProtoMember(32)]
                public Vector3DData HomeDockUp;
                /// <summary>Drone connector used to dock (entity id).</summary>
                [ProtoMember(33)]
                public long HomeDockConnectorId;
                /// <summary>True when the pose was recorded from an actual connection ("Set current as home").</summary>
                [ProtoMember(34)]
                public bool HomeDockRecorded;

                /// <summary>
                /// Max load, percent of what the thrusters can carry. 100% in gravity = the up thrusters can just hover it;
                /// in space = the weakest thruster group can still accelerate it at 0.1 g. Clamped 0–200%.
                /// </summary>
                [ProtoMember(35, IsRequired = true)]    // non-zero defaults: always written, or 0 would load as the default
                public float MaxLoadGravity = 80f;
                [ProtoMember(36, IsRequired = true)]
                public float MaxLoadSpace = 80f;

                // LCD output ([AutomataDrone] tag, drone's own grid only)
                [ProtoMember(37)]
                public uint LcdForeground = 0xFF00FF00;   // RGB(0,255,0)
                [ProtoMember(38)]
                public uint LcdBackground = 0xFF202020;   // RGB(32,32,32)
                [ProtoMember(39)]
                public float LcdFontSize = 0.35f;
                [ProtoMember(40, IsRequired = true)]
                public bool LcdShowHeader = true;

                // Blocks put to sleep / switched to refuel by the docking routine, restored on the next flight
                [ProtoMember(41)]
                public List<long> SleepingBlockIds = new List<long>();
                [ProtoMember(42)]
                public List<long> RechargingBatteryIds = new List<long>();
                [ProtoMember(43)]
                public List<long> StockpilingTankIds = new List<long>();

                // Stand-alone construction (embedded construction computer). Only used in OperationMode.StandAlone.
                [ProtoMember(46, IsRequired = true)]
                public Construction.WorkModes ConstructionWorkModes =
                        Construction.WorkModes.WeldUnfinishedBlocks | Construction.WorkModes.RepairDamagedBlocks | Construction.WorkModes.WeldProjectedBlocks;
                /// <summary>Blocks painted this color are ground down (Grind work mode). Packed RGB.</summary>
                [ProtoMember(47)]
                public uint GrindColor = 0xFF0000FF;   // RGB(255,0,0)

                /// <summary>
                /// The home connector is on something that moves (ship, carrier): the drone finds its way home through
                /// HomeBeaconId. Beacons are selectable only when owned by the drone owner / their faction.
                /// </summary>
                [ProtoMember(48)]
                public bool HomeNotStatic;
                [ProtoMember(49)]
                public long HomeBeaconId;
                /// <summary>Player-designated special behaviour (not in use yet).</summary>
                [ProtoMember(50)]
                public BehaviourProfile BehaviourProfile;
                /// <summary>
                /// Capabilities the player doesn't want this drone to bid with ("no space jobs for this one").
                /// Applied at bid time only; detection (CheckCapabilities) is unaffected.
                /// </summary>
                [ProtoMember(51)]
                public Capabilities ExcludedCapabilities;
                /// <summary>
                /// Seconds before a job task fails (navigation: this long without getting closer). 0 up to the
                /// server's Drone.TaskTimeoutSeconds, which also caps it when used.
                /// </summary>
                [ProtoMember(52)]
                public int TaskTimeoutSeconds = 30;
                /// <summary>
                /// m above the surface on planets for routes between take-off and landing; 0 = off. Player flights
                /// only (debug orders, Go home outside a job): jobs work at ground level.
                /// </summary>
                // Required: 0 (off) must be written, or a reload would give back the default
                [ProtoMember(53, IsRequired = true)]
                public float MinAltitude = 50f;

                // Combat handover (behaviour profile "Combat drone", managed by an orchestrator)
                /// <summary>m from the enemy where the AI Flight / Offensive blocks take over.</summary>
                [ProtoMember(54, IsRequired = true)]
                public float CombatHandoverDistance = 2000f;
                /// <summary>s without a target (AI Offensive) before the drone comes home.</summary>
                [ProtoMember(55, IsRequired = true)]
                public int CombatNoTargetSeconds = 15;
                /// <summary>Obsolete (was: launch radius from the home connector, 0 = any). Replaced by AutoLaunchRadius.</summary>
                [ProtoMember(56)]
                public float CombatLaunchRadius;
                /// <summary>The AI Defensive block fleeing (its flee trigger) brings the drone home.</summary>
                [ProtoMember(57, IsRequired = true)]
                public bool CombatRetreatOnFlee = true;
                /// <summary>Battery / hydrogen below the refuel thresholds brings the drone home.</summary>
                [ProtoMember(58, IsRequired = true)]
                public bool CombatRetreatOnLowPower = true;

                // Ore survey (any mode): what the drone's ore detector sees goes to mining surveyors in antenna range
                [ProtoMember(59)]
                public bool SurveyForOres;

                // (ProtoMember 60 was UseBroadcastController: only the orchestrator writes to chat)

                /// <summary>m from the drone: enemies its AI Defensive block detects within this launch it (0 = never on its
                /// own, designator jobs only). At most the vanilla detection range, 2000 m.</summary>
                [ProtoMember(61, IsRequired = true)]
                public float AutoLaunchRadius = 2000f;

                /// <summary>Escort jobs: m between the escorted grid / character and the drone's station above it.</summary>
                [ProtoMember(62, IsRequired = true)]
                public float EscortFollowDistance = 30f;

                /// <summary>Blocks painted exactly this color are never welded (stand-alone). Packed; RGB(255,0,255).</summary>
                [ProtoMember(63, IsRequired = true)]
                public uint WeldIgnoreColor = 0xFFFF00FF;

                /// <summary>Mining jobs for these ores ("Ore/Iron"...) get no bid from this drone.</summary>
                [ProtoMember(64)]
                public List<string> IgnoredOres = new List<string>();

                /// <summary>Farming (not implemented yet): crops ("SeedItem/Fruit"...) this drone leaves alone.</summary>
                [ProtoMember(65)]
                public List<string> IgnoredCrops = new List<string>();

                /// <summary>No home connector: where the drone last took off from (controller, world). Combat drones and ore
                /// scouts go back there when recalled / done (park near it, or hover there without landing gear).</summary>
                [ProtoMember(66)]
                public Vector3DData DepartureSpot;
                [ProtoMember(67)]
                public bool HasDeparture;

                /// <summary>Stone was put on the ignored ores once (the default); the player may take it off after that.</summary>
                [ProtoMember(68)]
                public bool StoneIgnoreDefaulted;

                /// <summary>Stand-alone construction: the order the weld categories are taken in (default: repair, projected, unbuilt).</summary>
                [ProtoMember(69)]
                public Construction.WorkPriority ConstructionWorkPriority;

                /// <summary>The name its logs (and other blocks' logs) show; empty: the controller's name.</summary>
                [ProtoMember(70)]
                public string Nickname = "";

                /// <summary>Economy flight: long legs in space as burn / coast / flip-and-burn with the strongest thruster group.</summary>
                [ProtoMember(75)]
                public bool EconomyFlight;

                /// <summary>Share (%) of the strongest face's thrust used to accelerate and brake (not to hold the weight).</summary>
                [ProtoMember(76, IsRequired = true)]
                public float MaxThrustPercent = 50f;

                /// <summary>m: the drone bids only on jobs this close to its base (home connector, else where it last left
                /// from, else where it is). 1 to 30 000.</summary>
                [ProtoMember(77, IsRequired = true)]
                public float LocalJobRadius = 15000f;

                /// <summary>No home connector: the grid it last took off from (docked / parked on), or the nearest friendly
                /// static grid when it left from voxel; 0 = none. It rests there again after a job.</summary>
                [ProtoMember(78)]
                public long DepartureGridId;
        }
}
