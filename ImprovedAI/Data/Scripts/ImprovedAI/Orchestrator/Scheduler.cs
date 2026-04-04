using ProtoBuf;
using System;
using VRageMath;

namespace ImprovedAI
{
    [ProtoContract(UseProtoMembersOnly = true)]
    public class Scheduler
    {
        [Flags]
        public enum ShareWith : byte
        {
            NoOne = 0,
            Friends = 1,
            Faction = 2,
            Neutrals = 4,
            Enemies = 8,
        }
        [Flags,ProtoContract]
        public enum WorkModes : ushort
        {
            [ProtoEnum]
            None = 0,
            [ProtoEnum]
            Scan = 1,
            [ProtoEnum]
            WeldUnfinishedBlocks = 2,
            [ProtoEnum]
            RepairDamagedBlocks = 4,
            [ProtoEnum]
            WeldProjectedBlocks = 8,
            [ProtoEnum]
            Grind = 16,
            [ProtoEnum]
            DeliverCargo = 32,
            [ProtoEnum]
            FetchCargo = 64,
            [ProtoEnum]
            DropCargo = 128,
            [ProtoEnum]
            AirDropCargo = 256,
        }

        [ProtoContract]
        public enum State : byte
        {
            [ProtoEnum]
            Initializing,
            [ProtoEnum]
            Standby,
            [ProtoEnum]
            ScanningForTasks,
            [ProtoEnum]
            AssigningTasks,
            [ProtoEnum]
            Error
        }
        [Flags, ProtoContract]
        public enum SearchModes : byte
        {
            [ProtoEnum]
            ConnectedGrids = 1,
            [ProtoEnum]
            BoundingBox = 2
        }

        [Flags, ProtoContract]
        public enum OperationMode : byte
        {
            [ProtoEnum]
            None = 0,
            [ProtoEnum]
            StandAloneDroneScheduler = 1,
            /// <summary>
            /// Forward messages to other schedulers.
            /// </summary>
            [ProtoEnum]
            Repeater = 2,
            /// <summary>
            /// Standard operation mode. Assign tasks to multiple drones, receive updates from multiple drones.
            /// </summary>
            [ProtoEnum]
            Orchestrator = 4,
            /// <summary>
            /// If no drones are available to this scheduler, forward found tasks to other schedulers.
            /// </summary>
            [ProtoEnum]
            DelegateIfNoDrones = 8,
        }
        [Flags, ProtoContract]
        public enum TaskType : ushort
        {
            None = 0,
            /// <summary>
            ///  Weld a block with needed components.
            /// </summary>
            [ProtoEnum]
            PreciseWelding =1,
            /// <summary>
            /// Grind a specific block down until capacity.
            /// </summary>
            [ProtoEnum]
            PreciseGrinding =2,
            /// <summary>
            /// Go mine some ore at specific location
            /// </summary>
            [ProtoEnum]
            PreciseDrilling =4,
            /// <summary>
            /// Go to specific location, connect to connector at exact location, and attempt to transfer items.
            /// </summary>
            [ProtoEnum]
            PreciseDelivery=8,
            /// <summary>
            /// Go to specific location, and drop payload at location.
            /// </summary>
            [ProtoEnum]
            PreciseDrop=16,
            /// <summary>
            /// Drop a cargo pod with a parachute at specific location.
            /// </summary>
            [ProtoEnum]
            PreciseCargoAirdrop=32,
            /// <summary>
            /// Go to specific location, connect to connector at exact location, and attempt to retrieve items.
            /// </summary>
            [ProtoEnum]
            PreciseFetch=64,
            /// <summary>
            ///  Go to specified location, find a matching block to weld, and weld it.
            /// </summary>
            [ProtoEnum]
            ScanWeld = 128,
            /// <summary>
            /// Go to specified location, find a matching block to grind, and grind it.
            /// </summary>
            [ProtoEnum]
            ScanGrind=256,
            /// <summary>
            /// Go to location, and scan for specified ore, and mine it.
            /// </summary>
            [ProtoEnum]
            ScanDrill=512,
            /// <summary>
            /// Go to location, scan for a friendly connector, connect and attempt to unload items.
            /// </summary>
            [ProtoEnum]
            ScanDelivery=1024,
            /// <summary>
            /// Go to location, scan for a friendly connector, connect and attempt to load items.
            /// </summary>
            [ProtoEnum]
            ScanFetch = 2048,
            /// <summary>
            /// Attempt to push items into the logistics network.
            /// </summary>
            [ProtoEnum]
            ActiveProvide=4096,
            // Order drone to self manage
            [ProtoEnum]
            BecomeStandAlone = 8192,
            /// <summary>
            /// Carry packed messages for other drones, logistics computers or schedulers.
            /// </summary>
            [ProtoEnum]
            MessengerPigeon = 16384,
        }
        [Serializable, ProtoContract(UseProtoMembersOnly = true)]
        public class Task
        {
            /// <summary>
            /// Payload required for the task, if any.
            /// </summary>
            [ProtoMember(1)]
            public Inventory Payload { get; set; }
            [ProtoMember(2)]
            public Vector3D Position { get; set; }
            /// <summary>
            /// Scheduler that assigned the task.
            /// </summary>
            [ProtoMember(3)]
            public long AssignedBy;
            [ProtoMember(4)]
            public DateTime AssignedTime { get; set; }
            [ProtoMember(5)]
            public DateTime CreatedTime { get; set; }
            [ProtoMember(6)]
            public uint TaskId { get; set; }
            [ProtoMember(7)]
            public TaskType TaskType { get; set; }
            /// <summary>
            /// Is this task out of scheduler antenna range.
            /// </summary>
            [ProtoMember(8)]
            public bool OutOfSchedulerRange { get; set; }
            public Task() { }

            public Task(Inventory payload, Vector3D position, long assignedBy, DateTime assignedTime, ushort taskId, TaskType taskType, bool outOfSchedulerRange)
            {
                TaskId = taskId;
                Payload = payload;
                Position = position;
                AssignedBy = assignedBy;
                AssignedTime = assignedTime;
                CreatedTime = DateTime.UtcNow;
                TaskType = taskType;
                OutOfSchedulerRange = outOfSchedulerRange;
            }
        }
    }
}
