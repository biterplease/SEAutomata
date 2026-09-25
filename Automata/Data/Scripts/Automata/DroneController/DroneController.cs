using ProtoBuf;
using System;

namespace Automata.DroneController
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
        Error
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
    public enum Capabilities : ushort
    {
        [ProtoEnum]
        None = 0,
        [ProtoEnum]
        CanWeld = 1,
        [ProtoEnum]
        CanGrind = 2,
        [ProtoEnum]
        CanDrill = 4,
        [ProtoEnum]
        CanScoutOre = 8,
        [ProtoEnum]
        HasWheels = 16,
        [ProtoEnum]
        CanFlyAtmosphere = 32,
        [ProtoEnum]
        CanFlySpace = 64,
        [ProtoEnum]
        HasDefenseSystems = 128,
        [ProtoEnum]
        HasSensors = 256,
        [ProtoEnum]
        HasCameras = 512,
        /// <summary>
        /// No cargo capacity.
        /// </summary>
        [ProtoEnum]
        CargoVolumeNil = 1024,
        /// <summary>
        /// Cargo volume greater than 0m^3 and less than 10m^3.
        /// </summary>
        [ProtoEnum]
        CargoVolumeLt10m3 = 2048,
        /// <summary>
        /// Cargo volume greater than 10m^3 and less than 100m^3.
        /// </summary>
        [ProtoEnum]
        CargoVolumeLt100m3 = 4096,
        /// <summary>
        /// Cargo volume greater than 100m^3 and less than 1000m^3.
        /// </summary>
        [ProtoEnum]
        CargoVolumeLt1000m3 = 8192,
        /// <summary>
        /// Cargo volume greater than 1000m^3 and less than 10000m^3.
        /// </summary>
        [ProtoEnum]
        CargoVolumeLt10000m3 = 16384,
        /// <summary>
        /// Drone is equipped for dropping cargo containers with parachutes.
        /// </summary>
        [ProtoEnum]
        CanAirdrop = 32768,

    }
    public enum BehaviourProfile : byte
    {
        [ProtoEnum]
        None = 0,
        [ProtoEnum]
        RefuelWhenDocked = 1,
        [ProtoEnum]
        RechargeWhenDocked = 2,
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
