using ProtoBuf;
using System;
using VRageMath;
using Automata.Util;
using System.Collections.Generic;

using InventoryClass = Automata.Inventory.Inventory;

namespace Automata.Orchestrator
{
    [Flags, ProtoContract]
    public enum WorkModes : ushort
    {
        [ProtoEnum]
        None = 0,
        /// <summary>
        /// Only scan for available jobs, and report.
        /// </summary>
        [ProtoEnum]
        ScanOnly = 1,
        [ProtoEnum]
        WeldUnfinishedBlocks = 2,
        [ProtoEnum]
        RepairDamagedBlocks = 4,
        [ProtoEnum]
        WeldProjectedBlocks = 8,
        [ProtoEnum]
        Grind = 16,
        /// <summary>
        /// Deliver cargo to a Logistics Computer connected grid, directly via connector.
        /// </summary>
        [ProtoEnum]
        DeliverCargo = 32,
        [ProtoEnum]
        FetchCargo = 64,
        /// <summary>
        /// Drop cargo at a specific location, no connector needed.
        /// </summary>
        [ProtoEnum]
        DropCargo = 128,
        /// <summary>
        /// AirDrop cargo container with a parachute, at a specific location.
        /// </summary>
        [ProtoEnum]
        AirDropCargo = 256,
        /// <summary>
        /// Mine ores reported by drones or the player. Ore locations should be cached, and considered fuzzy by proximity, e.g. reports should indicate a 50m radius
        /// </summary>
        [ProtoEnum]
        MineOre = 512,
    }
    [ProtoContract]
    public enum State : byte
    {
        [ProtoEnum]
        Initializing,
        [ProtoEnum]
        Standby,
        [ProtoEnum]
        ScanningForJobs,
        [ProtoEnum]
        AssigningTasks,
        [ProtoEnum]
        Error,
        /// <summary>Assignment pipeline: read LC/drone bid-round messages (one tick).</summary>
        [ProtoEnum]
        AssigningTasksReadBidRoundBids,
        /// <summary>Assignment pipeline: evaluate bids and enqueue winner announcements (one tick).</summary>
        [ProtoEnum]
        AssigningTasksProcessBidRoundBids,
        /// <summary>Assignment pipeline: broadcast at most one queued winner announcement (one tick).</summary>
        [ProtoEnum]
        AssigningTasksPublishBidRoundWinner,
        /// <summary>Assignment pipeline: read direct-message task bids for the active task bid round (one tick).</summary>
        [ProtoEnum]
        AssigningTasksReadTaskBids,
        /// <summary>Assignment pipeline: close a pending bid round if its window elapsed (one tick).</summary>
        [ProtoEnum]
        AssigningTasksFinalizePendingBidRound,
        /// <summary>Assignment pipeline: start the next task bid round if idle (one tick).</summary>
        [ProtoEnum]
        AssigningTasksStartNextBidRound,
        /// <summary>Assignment pipeline: leave assigning when queues are drained (one tick).</summary>
        [ProtoEnum]
        AssigningTasksEvaluateStandbyTransition,
    }

    [Flags, ProtoContract]
    public enum OperationMode : byte
    {
        [ProtoEnum]
        None = 0,
        [ProtoEnum]
        /// <summary>
        /// This is the mode used by drones who handle their own job scanning and task assignment.
        /// Should not be set by player on terminal.
        /// </summary>
        StandAloneDroneOrchestrator = 1,
        /// <summary>
        /// Forward messages to other orchestrators. This should be default when a player already has 
        /// another Orchestrator in the network.
        /// </summary>
        [ProtoEnum]
        Repeater = 2,
        /// <summary>
        /// Standard operation mode. Assign tasks to multiple drones, receive updates from multiple drones.
        /// </summary>
        [ProtoEnum]
        Orchestrator = 4,
        /// <summary>
        /// If no drones are available to this orchestrator, forward found tasks to other orchestrators.
        /// </summary>
        [ProtoEnum]
        DelegateIfNoDrones = 8,
    }
    [Flags, ProtoContract]
    public enum JobType : byte
    {
        /// <summary>
        /// Weld a block with needed components.
        /// </summary>
        [ProtoEnum]
        WeldBlock = 1,
        /// <summary>
        /// Grind a block down until desired capacity.
        /// </summary>
        [ProtoEnum]
        GrindBlock = 2,
        /// <summary>
        /// When a logistics Computer is missing an inventory qutoa, collect it somewhere and deliver it to satisfy the quota.
        /// </summary>
        [ProtoEnum]
        DeliverMissingInventory = 3,
        /// <summary>
        /// When a logistics Computer has an inventory surplus, collect it and deliver elsewhere, ideally somewhere its needed.
        /// </summary>
        [ProtoEnum]
        CollectInventorySurplus = 4,
        /// <summary>
        /// Mine ore at a specific location, and deposit it in a Logistics Computer connected grid.
        /// </summary>
        [ProtoEnum]
        MineOre = 5,
    }
    /// <summary>
    /// Task types are the specific steps needed to complete a job.
    /// They are simple, one-step actions by nature, and should compose to larger scale jobs.
    /// Tasks always include a location, and optionally a payload, and a "thing to do", like welding, grinding, drillinc, etc.
    /// </summary>
    [Flags, ProtoContract]
    public enum TaskType : ushort
    {
        None = 0,
        /// <summary>
        /// Fetch inventory at location and orientation.
        /// </summary>
        [ProtoEnum]
        CollectInventory = 1,
        /// <summary>
        /// Deliver inventory at location and orientation.
        /// </summary>
        [ProtoEnum]
        DeliverInventory = 2,
        /// <summary>
        /// Go to a specific location, usually in preparation for the next task.
        /// </summary>
        [ProtoEnum]
        ApproachLocation = 4,
        /// <summary>
        /// Weld block at location and orientation, assumed to have partial or total inventory for welding in cargo.
        /// </summary>
        [ProtoEnum]
        WeldBlock = 8,
        /// <summary>
        /// Grind block at location and orientation, assumed to have space in cargo for partial or total components
        /// </summary>
        [ProtoEnum]
        GrindBlock = 16,
        /// <summary>
        /// Mine a specific ore, detected via Ore Detector.
        /// </summary>
        [ProtoEnum]
        MineOre = 32,
        /// <summary>
        /// When at location, search for friendly Logistics Computers, and start handshake to get directions
        /// for specific connector with required inventory..
        /// </summary>
        [ProtoEnum]
        ScanCollectInventory = 64,
        /// <summary>
        /// When at location, search for friendly Logistics Computers, and start handshake to get directions
        /// for specific connector where inventory will be delivered.
        /// </summary>
        [ProtoEnum]
        ScanDeliverInventory = 128,
        /// <summary>
        /// Order drone to return home after current task set is complete. This should be sent when a drone is
        /// expected to leave the orchestrator range.
        /// </summary>
        [ProtoEnum]
        ReturnHome = 256, // 2^8
        /// <summary>
        /// This signals the drone that, once the current task set is complete, it will decouple from the issuing Orchestrator,
        /// allowing it to be adopted by a different Orchestrator.
        /// </summary>
        [ProtoEnum]
        BecomeOrphan = 512, // 2^9
        /// <summary>
        /// Deliver a message payload to a friendy IAI block. This is used when no antenna comms are available, and would still
        /// allow async networking by carrying messages between isolated networks.
        /// </summary>
        [ProtoEnum]
        MessengerPigeon = 1024, // 2^10
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Job
    {
        /// <summary>
        /// Payload required for the task, if any.
        /// </summary>
        [ProtoMember(1)]
        public InventoryClass ComponentsInventory { get; set; }
        [ProtoMember(2)]
        public InventoryClass BlocksInventory { get; set; }
        // [ProtoIgnore]
        // public Vector3D Position { get; set; }
        [ProtoMember(3)]
        public List<Task> Tasks { get; set; }
        /// <summary>
        /// Original job position data.
        /// </summary>
        [ProtoMember(4)]
        public Vector3DData PositionData { get; set; }
        // [ProtoIgnore]
        // public QuaternionD Orientation { get; set; }
        [ProtoMember(5)]
        /// <summary>
        /// Original job orientation data.
        /// </summary>
        public QuaternionDData OrientationData { get; set; }
        [ProtoMember(6)]
        public DateTime CreatedTime { get; set; }
        [ProtoMember(7)]
        public float NaturalGravity { get; set; }
        [ProtoMember(8)]
        public JobType JobType { get; set; }
        [ProtoMember(9)]
        public uint JobId { get; set; }
        /// <summary>
        /// Amount of individual blocks that are part of this job.
        /// </summary>
        [ProtoMember(10)]
        public ushort BlockCount { get; set; }
        /// <summary>
        /// Is this job part of a static grid.
        /// </summary>
        [ProtoMember(11)]
        public bool IsStaticGrid { get; set; }
        [ProtoMember(12)]
        public bool IsInSpace { get; set; }
        [ProtoMember(13)]
        public bool IsInAtmosphere { get; set; }
        [ProtoMember(14)]
        public bool OutOfOrchestratorRange { get; set; }


        public Job() { }
        // public Job(
        //     uint jobId,
        //     JobType jobType,
        //     Inventory payload,
        //     Vector3D position,
        //     QuaternionD orientation,
        //     bool outOfOrchestratorRange,
        //     float naturalGravity,
        //     bool isStaticGrid,
        //     bool isInSpace,
        //     List<Task> tasks = null)
        // {
        //     JobId = jobId;
        //     JobType = jobType;
        //     Inventory = payload;
        //     // Position = position;
        //     PositionData = Vector3DData.FromVector3D(position);
        //     // Orientation = orientation;
        //     OrientationData = QuaternionDData.FromQuaternion(orientation);
        //     CreatedTime = DateTime.UtcNow;
        //     Tasks = tasks;
        //     NaturalGravity = naturalGravity;
        //     OutOfOrchestratorRange = outOfOrchestratorRange;
        //     IsStaticGrid = isStaticGrid;
        //     IsInSpace = isInSpace;
        // }
        /// <summary>
        /// Copies runtime fields (e.g. <see cref="Task.Position"/>) into proto members before serialization.
        /// </summary>
        public void PrepareForSave()
        {
            // if (Tasks == null)
            // {
            //     return;
            // }
            // for (int i = 0; i < Tasks.Count; i++)
            // {
            //     Task task = Tasks[i];
            //     if (task != null)
            //     {
            //         task.PrepareForSave();
            //     }
            // }
            // PositionData = Vector3DData.FromVector3D(Position);
            // OrientationData = QuaternionDData.FromQuaternion(Orientation);
        }
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Task
    {
        /// <summary>
        /// Payload required for the task, if any.
        /// </summary>
        [ProtoMember(1)]
        public InventoryClass Payload { get; set; }
        [ProtoMember(2)]
        public Vector3DData PositionData { get; set; }
        [ProtoMember(3)]
        public QuaternionDData OrientationData { get; set; }
        /// <summary>
        /// Orchestrator that assigned the task.
        /// </summary>
        [ProtoMember(4)] public long AssignedBy;
        [ProtoMember(5)] public DateTime AssignedTime { get; set; }
        [ProtoMember(6)] public DateTime CreatedTime { get; set; }

        [ProtoMember(7)] public uint JobId { get; set; }
        [ProtoMember(8)] public uint TaskId { get; set; }
        [ProtoMember(9)] public TaskType TaskType { get; set; }
        /// <summary>
        /// Is this task out of Orchestrator antenna range.
        /// </summary>
        [ProtoMember(10)] public bool OutOfOrchestratorRange { get; set; }
        public Task() { }

        public Task(
            ushort taskId,
            InventoryClass payload,
            Vector3D position,
            QuaternionD orientation,
            long assignedBy,
            DateTime assignedTime,
            TaskType taskType,
            bool outOfOrchestratorRange)
        {
            TaskId = taskId;
            Payload = payload;
            PositionData = Vector3DData.FromVector3D(position);
            OrientationData = QuaternionDData.FromQuaternionD(orientation);
            AssignedBy = assignedBy;
            AssignedTime = assignedTime;
            CreatedTime = DateTime.UtcNow;
            TaskType = taskType;
            OutOfOrchestratorRange = outOfOrchestratorRange;
        }
    }
}
