using ProtoBuf;
using System;

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
    }
    [ProtoContract]
    public enum OperationMode : byte
    {
        [ProtoEnum]
        StandAlone,
        [ProtoEnum]
        ManagedByScheduler,
        [ProtoEnum]
        ManagedByPlayer,
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
    public enum BehaviourProfile : byte
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
        /// Scout drones are usually unarmed, and extremely lightweight. Used to scout for ore or enemy activity.
        /// </summary>
        [ProtoEnum]
        IsScoutDrone = 8,
        /// <summary>
        /// Rovers are wheeled drones designed for surface navigation.
        /// They may on may not be equipped for flight, but will prefer surface
        /// pathfinding over flight. Heavy cargo operations should preferrably be assigned
        /// to rovers, as they are less limited by their fuel or battery usage.
        /// </summary>
        [ProtoEnum]
        IsRover = 16,
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
