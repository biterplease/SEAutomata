using ProtoBuf;
using System;

using VRageMath;

using Automata.Util;
using Automata.Pathfinding;

namespace Automata.Drone
{

    [ProtoContract]
    public enum State : byte
    {
        [ProtoEnum]
        Initializing,
        [ProtoEnum]
        Standby,
        [ProtoEnum]
        NavigatingToTarget,
        [ProtoEnum]
        LoadingInventory,
        [ProtoEnum]
        Welding,
        [ProtoEnum]
        Grinding,
        [ProtoEnum]
        ReturningToBase,
        [ProtoEnum]
        AligningToHome,
        [ProtoEnum]
        Docking,
        [ProtoEnum]
        RefuelingHydrogen,
        [ProtoEnum]
        RechargingBattery,
        [ProtoEnum]
        Error,
        [ProtoEnum]
        Docked,
        [ProtoEnum]
        Preflight,
        /// <summary>Combat handover: the AI Flight + AI Offensive blocks fly and fight.</summary>
        [ProtoEnum]
        Combat,
    }
    [ProtoContract]
    public enum OperationMode : byte
    {
        [ProtoEnum]
        StandAlone,
        [ProtoEnum]
        ManagedByOrchestrator,
        [ProtoEnum]
        ManagedByPlayer,
        /// <summary>
        /// Obsolete (2026-10-05): combat handover is now the "Combat drone" behaviour profile in ManagedByOrchestrator.
        /// Kept so older saves load; migrated on load (DroneControllerBlock.MigrateSettings), never offered.
        /// </summary>
        [ProtoEnum]
        CombatHandover,
    }
    [Flags, ProtoContract]
    public enum Capabilities : uint
    {
        [ProtoEnum]
        None = 0,
        [ProtoEnum]
        CanDock = 1,
        [ProtoEnum]
        CanWeld = 2,
        [ProtoEnum]
        CanGrind = 4,
        [ProtoEnum]
        CanDrill = 8,
        [ProtoEnum]
        CanScoutOre = 16,
        [ProtoEnum]
        HasWheels = 32,
        [ProtoEnum]
        CanFlyAtmosphere = 64,
        [ProtoEnum]
        CanFlySpace = 128,
        [ProtoEnum]
        HasDefenseSystems = 256,
        [ProtoEnum]
        HasSensors = 512,
        [ProtoEnum]
        HasCameras = 1024,
        /// <summary>
        /// No cargo capacity.
        /// </summary>
        [ProtoEnum]
        CargoVolumeNil = 2048,
        /// <summary>
        /// Cargo volume greater than 0m^3 and less than 10m^3.
        /// </summary>
        [ProtoEnum] CargoVolumeLt10m3 = 4096,
        /// <summary>
        /// Cargo volume greater than 10m^3 and less than 100m^3.
        /// </summary>
        [ProtoEnum] CargoVolumeLt100m3 = 8192,
        /// <summary>
        /// Cargo volume greater than 100m^3 and less than 1000m^3.
        /// </summary>
        [ProtoEnum] CargoVolumeLt1000m3 = 16384,
        /// <summary>
        /// Cargo volume of 1000m^3 or more (the top class).
        /// </summary>
        [ProtoEnum] CargoVolumeLt10000m3 = 32768,
        // Lift classes: payload (kg) the drone can carry in 1 g within its max-load setting, beyond its own mass.
        // Exactly one is set. Cargo volume says what fits in the drone, lift says what it can take off with.
        /// <summary>No payload at all (can barely lift itself, or not at all).</summary>
        [ProtoEnum] LiftNil = 65536,
        /// <summary>Payload below 1 t.</summary>
        [ProtoEnum] LiftLt1t = 131072,
        /// <summary>Payload of 1 t to 10 t.</summary>
        [ProtoEnum] LiftLt10t = 262144,
        /// <summary>Payload of 10 t to 100 t.</summary>
        [ProtoEnum] LiftLt100t = 524288,
        /// <summary>Payload of 100 t or more (the top class).</summary>
        [ProtoEnum] LiftLt1000t = 1048576,

    }
    /// <summary>
    /// A drone's role (one per drone, terminal). Values are bits so orchestrators test them with &amp;; ushort since the
    /// job profiles (2026-10-05) outgrew a byte (protobuf writes the number either way).
    /// </summary>
    public enum BehaviourProfile : ushort
    {
        [ProtoEnum]
        None = 0,
        /// <summary>
        /// Cargo drones can carry large amounts of cargo, for smaller construction drones to use
        /// as a mobile cargo container. Player should not assign this flag to drones that are expected
        /// to do anything other than carry cargo.
        /// </summary>
        [ProtoEnum]
        IsCargoDrone  = 4,
        /// <summary>
        /// Ore scouts: usually unarmed and light, preferred for scout jobs (industry pointer, TODO/11). An ore detector
        /// is required (the drone is in error without one).
        /// </summary>
        [ProtoEnum]
        IsOreScout = 8,
        /// <summary>
        /// Rovers are wheeled drones designed for surface navigation.
        /// They may on may not be equipped for flight, but will prefer surface
        /// pathfinding over flight. Heavy cargo operations should preferrably be assigned
        /// to rovers, as they are less limited by their fuel or battery usage.
        /// </summary>
        [ProtoEnum]
        IsRover = 16,
        /// <summary>Combat drones: armed, the ones selected for combat jobs (combat pointer, TODO/11).</summary>
        [ProtoEnum]
        IsCombatDrone = 32,
        /// <summary>
        /// Ore haulers: preferred for logistics jobs whose items are all ores (Ice included), and only those (one item of
        /// any other type rules them out); mining jobs too. May dock with small connectors (TODO/14).
        /// </summary>
        [ProtoEnum]
        IsOreHauler = 64,
        /// <summary>Welder:All: preferred for weld jobs (construction), any kind.</summary>
        [ProtoEnum]
        IsWelderDrone = 128,
        /// <summary>Grinder drone: preferred for grind jobs.</summary>
        [ProtoEnum]
        IsGrinderDrone = 256,
        /// <summary>Welder + grinder drone (both on one ship, as players usually build them): preferred for both.</summary>
        [ProtoEnum]
        IsWelderGrinderDrone = 512,
        /// <summary>Mining drone: preferred for mining jobs.</summary>
        [ProtoEnum]
        IsMiningDrone = 1024,
        /// <summary>Farming drone (farming not implemented yet).</summary>
        [ProtoEnum]
        IsFarmingDrone = 2048,
        /// <summary>Welder:Repair: a welder that ranks first for repair jobs (damaged blocks); still takes any weld job.</summary>
        [ProtoEnum]
        IsWelderRepair = 4096,
        /// <summary>Welder:Projected: a welder that ranks first for projected build jobs; still takes any weld job.</summary>
        [ProtoEnum]
        IsWelderProjected = 8192,
        /// <summary>Welder:Unbuilt: a welder that ranks first for unbuilt jobs (placed, not welded up); still takes any weld job.</summary>
        [ProtoEnum]
        IsWelderUnbuilt = 16384,
}
    [ProtoContract, Flags]
    public enum UpdateFlags : ushort
    {
        [ProtoEnum]
        None = 0,
        [ProtoEnum]
        Error = 1,
        [ProtoEnum]
        Registration = 2,
        [ProtoEnum]
        TaskComplete = 4,
        [ProtoEnum]
        StateChanged = 8,
        [ProtoEnum]
        CapabilitiesChanged = 16,
        [ProtoEnum]
        BatteryUpdate = 32,
        [ProtoEnum]
        H2Update = 64,
        [ProtoEnum]
        GoingOutOfRange = 128,
        [ProtoEnum]
        ReturningIntoRange = 256,
        [ProtoEnum]
        UnderAttack = 512,
    }
    [ProtoContract]
    public enum FlightOrientationMode : byte {
        [ProtoEnum]
        FaceTravel = 0,
        [ProtoEnum]
        LookAt = 1,
        [ProtoEnum]
        Explicit = 2
    }
    [ProtoContract]
    public enum FlightPhase : byte
    {
        [ProtoEnum] Approach = 0,   // flying ApproachFrom → Target (direct orders start here)
        [ProtoEnum] Transit  = 1,   // flying straight to ApproachFrom
        [ProtoEnum] Align    = 2,   // holding at ApproachFrom, turning to the final orientation
        [ProtoEnum] MatchSpeed = 3, // anchored orders: match the anchor grid's velocity before moving relative to it
    }
    [ProtoContract]
    public class FlightOrder
    {
        [ProtoMember(1)]  public Vector3DData Target;           // where the reference point must end up
        [ProtoMember(2)]  public Vector3DData ApproachFrom;     // start of the approach line
        [ProtoMember(3)] public Vector3DData TransitStart;
        [ProtoMember(4)]  public FlightOrientationMode Orientation;
        [ProtoMember(5)]  public Vector3DData LookAtPoint;      // LookAt
        [ProtoMember(6)]  public Vector3DData Forward;          // Explicit (world)
        [ProtoMember(7)]  public Vector3DData Up;               // Explicit (world)
        [ProtoMember(8)]  public Vector3DData ReferenceOffset;  // controller-local; zero = the controller itself
        [ProtoMember(9)]  public float ArrivalTolerance = 1f;   // m
        [ProtoMember(10)] public FlightPhase Phase;
        [ProtoMember(11)]  public bool Captured;                 // reached the approach line
        [ProtoMember(12)] public bool Arrived;
        [ProtoMember(13)] public bool IgnoreGravityLimits;
        [ProtoMember(14, IsRequired = true)] public WaypointBehavior Behavior = WaypointBehavior.FullStop;   // non-zero default: always written
        [ProtoMember(15)] public float ExitSpeed;               // m/s when passing Target; used only if a next leg is queued
        // Relative navigation: when AnchorEntityId != 0 the *Local fields are authoritative, expressed in the
        // anchor block's frame as (Right, Up, Forward); the world fields above are rebuilt from them every tick.
        [ProtoMember(16)] public long AnchorEntityId;
        [ProtoMember(17)] public Vector3DData TargetLocal;
        [ProtoMember(18)] public Vector3DData ApproachFromLocal;
        [ProtoMember(19)] public Vector3DData TransitStartLocal;
        [ProtoMember(20)] public Vector3DData ForwardLocal;      // Explicit orientation, anchor frame
        [ProtoMember(21)] public Vector3DData UpLocal;
        [ProtoMember(22)] public bool UseApproachLine;          // phase entered after MatchSpeed: Transit (true) or Approach
        [ProtoMember(23)] public float FinalSpeed;              // m/s on the final leg; 0 = the drone's ApproachSpeed
        [ProtoMember(24)] public float LineTolerance;           // m: strict orders (docking) - approach point / line tolerance; 0 = WaypointTolerance
        [ProtoMember(25)] public bool HoldAttitudeInTransit;    // MatchSpeed / Transit: keep the current attitude (no turning to the travel direction, no gravity levelling)
        /// <summary>Route leg the pathfinder watches on the way (look-ahead, replanning). Runtime only, not sent.</summary>
        public bool Watched;
        /// <summary>A waypoint the player gave (debug orders); detour legs in between are not. Runtime only.</summary>
        public bool PlayerWaypoint;
        /// <summary>
        /// Route leg / back-out: arrived within ~10° of its attitude and below 0.3 m/s (the order after it settles the
        /// rest), not the 3° / 0.1 m/s a work or dock position needs. Runtime only.
        /// </summary>
        public bool LooseArrival;
        /// <summary>
        /// Escort (station keeping): Target is the entity's centre + FollowOffset every tick, the drone matching the
        /// entity's velocity and looking at it (a grid or a character: no ownership / range rules as for anchors, the
        /// escort job checked the target). Runtime only: the escort task issues it again after a load.
        /// </summary>
        public long FollowEntityId;
        /// <summary>Escort: world offset of the station from the followed entity's centre (updated by the escort logic).</summary>
        public Vector3D FollowOffset;
        /// <summary>m/s along the order, all of it (0 = none; FinalSpeed only slows its last metres). Mining bores, dock
        /// corridors (saved: a drone in a hangar must not leave it at full speed after a reload).</summary>
        [ProtoMember(26)] public double SpeedCap;
    }
    [ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Drone
    {
        [ProtoMember(1)]
        public long DroneId;
        [ProtoMember(2)]
        public Capabilities _Capabilities;
        [ProtoMember(3)]
        public State _State;
        [ProtoMember(4)]
        public float BatteryLevel;
        [ProtoMember(5)]
        public float BatteryRechargeThreshold;
        [ProtoMember(6)]
        public float BatteryOperationalThreshold;
        [ProtoMember(7)]
        public float H2Level;
        [ProtoMember(8)]
        public float H2RefuelThreshold;
        [ProtoMember(9)]
        public float H2OperationalThreshold;
        [ProtoMember(10)]
        public bool IsOutOfRange;
        [ProtoMember(11)]
        public DateTime LastSeenTime;
        [ProtoMember(12)]
        public DateTime LastReportTime;
            
        public Drone()
        {
            _Capabilities = Capabilities.None;
            _State = State.Standby;
            BatteryLevel = 25.0f;
            BatteryRechargeThreshold = 20.0f;
            BatteryOperationalThreshold = 80.0f;
            H2Level = 25.0f;
            H2RefuelThreshold = 20.0f;
            H2OperationalThreshold = 80.0f;
            IsOutOfRange = false;
            LastSeenTime = DateTime.UtcNow;
            LastReportTime = DateTime.UtcNow;
        }
        public Drone(
            long droneId,
            Capabilities capabilities = Capabilities.None,
            State droneState = State.Standby,
            float batteryLevel = 25.0f,
            float batteryRechargeThreshold = 20.0f,
            float batteryOperationalThreshold = 80.0f,
            float h2Level = 25.0f,
            float h2RechargeThreshold = 20.0f,
            float h2OperationalThreshold = 80.0f,
            bool isOutOfRange = false
            )
        {
            DroneId = droneId;
            _Capabilities = capabilities;
            _State = droneState;
            BatteryLevel = batteryLevel;
            BatteryRechargeThreshold = batteryRechargeThreshold;
            BatteryOperationalThreshold = batteryOperationalThreshold;
            H2Level = h2Level;
            H2RefuelThreshold = h2RechargeThreshold;
            H2OperationalThreshold = h2OperationalThreshold;
            IsOutOfRange = isOutOfRange;
            LastSeenTime = DateTime.UtcNow;
            LastReportTime = DateTime.UtcNow;
        }
        public Drone(Drone drone)
        {
            DroneId = drone.DroneId;
            _Capabilities = drone._Capabilities;
            _State = drone._State;
            BatteryLevel = drone.BatteryLevel;
            BatteryRechargeThreshold = drone.BatteryRechargeThreshold;
            BatteryOperationalThreshold = drone.BatteryOperationalThreshold;
            H2Level = drone.H2Level;
            H2RefuelThreshold = drone.H2RefuelThreshold;
            H2OperationalThreshold = drone.H2OperationalThreshold;
            IsOutOfRange = drone.IsOutOfRange;
            LastReportTime = drone.LastReportTime;
            LastSeenTime = drone.LastSeenTime;
        }
    }
}
