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
                /// <summary>
                        /// Normally drones handle all tasks that they are capable for.
                        /// If enabled, will allow the user to filter specific task types that this drone will be allowed to handle.
                        /// </summary>
                [ProtoMember(4)]
                public bool UseTaskTypeFilters;
                [ProtoMember(5)]
                public Orchestrator.TaskType TaskTypeFilters;
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
                public float MaxSpeed = 50.0f;
                /// <summary>
                        /// Docking speed in m/s.
                        /// </summary>
                [ProtoMember(11)]
                public float SafeSpeed = 0.5f;
                [ProtoMember(12, IsRequired = true)]   // default true: false would otherwise not be saved
                public bool AlignToPGravity = true;
                [ProtoMember(13, IsRequired = true)]
                public float PGravityAlignMaxPitchDegrees = 10.0f;
                [ProtoMember(14, IsRequired = true)]
                public float PGravityAlignMaxRollDegrees = 10.0f;
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
                [ProtoMember(26)]
                public bool EnableInertialDampening = false;

                /// <summary>
                /// Home connector entity id (0 = none). Only connectors owned by the drone owner or the owner's
                /// faction can be selected; ownership is re-checked every time the id is resolved.
                /// </summary>
                [ProtoMember(27)]
                public long HomeConnectorId;

                // stand-alone construction function
                // Observation area: box centred on the home connector, in the connector's frame.
                // Size X = width (right), Y = height (up), Z = depth (forward), each 2.5–25 m.
                // Offset X/Y/Z along the connector's Right/Up/Forward, each ±12.5 m.
                [ProtoMember(28)]
                public Vector3DData ObservationAreaSize = new Vector3DData { X = 10, Y = 10, Z = 10 };
                [ProtoMember(29)]
                public Vector3DData ObservationAreaOffset;
                // Runtime only (not serialized): drawing switches itself off after a while.
                public bool ObservationAreaDraw = false;

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
                public float LcdFontSize = 0.6f;
                [ProtoMember(40, IsRequired = true)]
                public bool LcdShowHeader = true;

                // Blocks put to sleep / switched to refuel by the docking routine, restored on the next flight
                [ProtoMember(41)]
                public List<long> SleepingBlockIds = new List<long>();
                [ProtoMember(42)]
                public List<long> RechargingBatteryIds = new List<long>();
                [ProtoMember(43)]
                public List<long> StockpilingTankIds = new List<long>();
        }
}
