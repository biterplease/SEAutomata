using static ImprovedAI.Drone;
using ProtoBuf;
using VRageMath;
using VRage.Game.ModAPI;
using Sandbox.Game.Screens.Helpers; // Assuming you are using ProtoBuf for serialization

namespace ImprovedAI
{

    /// <summary>
    /// Drone controller configurable settings.
    /// These should always be validated, and bounded by, the mod server settings.
    /// </summary>
    [ProtoContract]
    public class IAIDroneControllerSettings
    {
        // Configuration values
        [ProtoMember(1)]
        public bool IsEnabled = false;
        [ProtoMember(2)]
        public OperationMode OperationMode = OperationMode.StandAlone;
        public long managingScheduler; 
        /// <summary>
        /// Drone will always return to this connector when no more tasks are assigned.
        /// WARNING: if this connector is set as home to more than one drone, may have catastrophic consequences.
        /// </summary>
        [ProtoMember(3)]
        public Vector3D HomePosition;
        public Vector3D HomeForwardDirection  = Vector3D.Forward;
        public bool EnforceHomeOrientation = true;
        [ProtoMember(24)]
        public IMyGps HomeIsRelativeTo;
        /// <summary>
        /// Normally drones handle all tasks that they are capable for.
        /// If enabled, will allow the user to filter specific task types that this drone will be allowed to handle.
        /// </summary>
        [ProtoMember(4)]
        public bool UseTaskTypeFilters;
        [ProtoMember(5)]
        public Scheduler.TaskType TaskTypeFilters;
        /// <summary>
        /// Normally, drones report all their capabilities to the Scheduler class
        /// If enabled, it will filter out certain capabilities when reporting to its scheduler.
        /// StandAlone
        /// </summary>
        [ProtoMember(6)]
        public bool UseCapabilityFilters;
        [ProtoMember(7)]
        public Capabilities CapabilityFilters;
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
        public float ApproachSpeed = 5.0f;
        /// <summary>
        /// Max speed in m/s.
        /// </summary>
        [ProtoMember(10)]
        public float SpeedLimit = 50.0f;
        /// <summary>
        /// Docking speed in m/s.
        /// </summary>
        [ProtoMember(11)]
        public float DockingSpeed = 2.5f;
        [ProtoMember(12)]
        public bool AlignToPGravity = true;
        [ProtoMember(13)]
        public float PGravityAlignMaxPitchDegrees = 10.0f;
        [ProtoMember(14)]
        public float PGravityAlignMaxRollDegrees = 10.0f;
        [ProtoMember(15)]
        public string LCDScreenTag = "[IAIDrone]"; // Drone state will be sent to each LCD screen that contains this tag.

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
        public float HydrogenRefuelThreshold = 25.0f;     // Return to base when H2 below this %
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
        public float BatteryRefuelThreshold = 20.0f;      // Return to base when battery below this %
        [ProtoMember(22)]
        public float BatteryOperationalThreshold = 80.0f; // Resume ops when battery above this %
        [ProtoMember(23)]
        public Base6Directions.Direction ControllerForwardDirection = Base6Directions.Direction.Forward;
    }
}
