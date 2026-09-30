using ProtoBuf;
using System;
using VRageMath;
using Automata.Util;
using System.Collections.Generic;

using Automata.Inventory;

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
    [ProtoContract]
    public enum JobType : byte
    {
        [ProtoEnum]
        None = 0,
        /// <summary>
        /// Weld blocks with needed components.
        /// </summary>
        [ProtoEnum]
        Weld = 1,
        /// <summary>
        /// Grind blocks down.
        /// </summary>
        [ProtoEnum]
        Grind = 2,
        /// <summary>
        /// When a logistics Computer is missing an inventory quota, collect it somewhere and deliver it to satisfy the quota.
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
    /// One single-action step of a job. Tasks are simple by nature and compose into jobs; the drone runs them in
    /// order. The type decides which fields of <see cref="Task"/> are used (see <see cref="Task.Kind"/>).
    /// </summary>
    [ProtoContract]
    public enum TaskType : byte
    {
        [ProtoEnum] None = 0,
        /// <summary>Move the drone (controller) to Position, coming in along ApproachFrom -> Position.</summary>
        [ProtoEnum] NavigateTo = 1,
        /// <summary>
        /// Put the tool of the NEXT task (weld / grind / mine) on Position, coming in along ApproachFrom -> Position,
        /// with the tool pointing that way. Blocks: Position is the centre of one of the block's cell faces (never
        /// its centre, an edge or a vertex) and ApproachFrom lies on that face's normal. ApproachFrom is a planning
        /// hint for the side: the drone puts its approach point its own length + 2.5 m out along that line, so it has
        /// room to turn there, and writes it back (and into the BackOut that follows the tool task).
        /// </summary>
        [ProtoEnum] NavigateToolTo = 2,
        /// <summary>Connect to the connector TargetEntityId (Position / ApproachFrom: its connection point and axis).</summary>
        [ProtoEnum] DockAt = 3,
        /// <summary>Fly home and dock at the drone's home connector (the drone knows where that is).</summary>
        [ProtoEnum] ReturnHome = 4,
        /// <summary>While docked: pull Inventory from the conveyor network on the other side of the connector.</summary>
        [ProtoEnum] LoadInventory = 5,
        /// <summary>While docked: push the Inventory item types into that network. Null / empty = everything the job dealt in (construction: components).</summary>
        [ProtoEnum] UnloadInventory = 6,
        /// <summary>Weld the block TargetBlock of grid TargetEntityId until Completion.</summary>
        [ProtoEnum] WeldBlock = 7,
        /// <summary>Grind the block TargetBlock of grid TargetEntityId until Completion.</summary>
        [ProtoEnum] GrindBlock = 8,
        /// <summary>Drill where the tool was put until Completion (usually DroneInventoryFull).</summary>
        [ProtoEnum] Mine = 9,
        /// <summary>
        /// Messenger pigeon protocol. Deliver a message payload to a friendly Automata block (TargetEntityId).
        /// Used when no antenna comms are available: carries messages between isolated networks.
        /// </summary>
        [ProtoEnum] DeliverMessage = 10,
        /// <summary>
        /// After a tool task: move straight back, without turning, until the tool's work point is at Position (the
        /// approach point the preceding NavigateToolTo used; the drone fills it in when it flies that one).
        /// </summary>
        [ProtoEnum] BackOut = 11,
    }

    /// <summary>Which fields a task uses; derived from its type (not sent).</summary>
    public enum TaskKind : byte
    {
        None,
        Navigation,   // Position, ApproachFrom, RelativeBeaconEntityId, NaturalGravity (+ TargetEntityId for DockAt)
        Inventory,    // Inventory
        Tool,         // TargetEntityId, TargetBlock, IsProjected, Completion, CompletionValue
        Message,      // TargetEntityId
    }

    /// <summary>
    /// When a tool task is done. Named as the game names block states (IMySlimBlock): IsFullIntegrity (welded
    /// to full), IsFullyDismounted (ground down and removed). IsDestroyed is damage, which is not what a tool does.
    /// </summary>
    [ProtoContract]
    public enum ToolTaskCompletionTrigger : byte
    {
        /// <summary>Weld: IsFullIntegrity (and no deformation).</summary>
        [ProtoEnum] BlockFullIntegrity = 0,
        /// <summary>Grind: IsFullyDismounted, i.e. the block is gone from its grid.</summary>
        [ProtoEnum] BlockFullyDismounted = 1,
        /// <summary>CompletionValue = integrity percent (0-100).</summary>
        [ProtoEnum] BlockIntegrityPercent = 2,
        [ProtoEnum] DroneInventoryFull = 3,
        [ProtoEnum] DroneInventoryEmpty = 4,
        /// <summary>CompletionValue = seconds.</summary>
        [ProtoEnum] Timespan = 5,
    }

    /// <summary>Why a task could not be carried out. Reported by the drone; the job goes on or ends by task type.</summary>
    public enum TaskFailure : byte
    {
        None = 0,
        /// <summary>Weld: the block is already at full integrity (someone else got there first).</summary>
        AlreadyFullIntegrity,
        /// <summary>Grind: the block is already dismounted / gone. Weld: a built block is gone.</summary>
        TargetRemoved,
        /// <summary>Mine: drilling yields nothing at the location.</summary>
        NoOre,
        /// <summary>Load: the conveyor network doesn't have (all of) the items.</summary>
        InsufficientMaterials,
        /// <summary>Unload: the conveyor network has no room for (all of) the items.</summary>
        NoCargoSpace,
        /// <summary>The task took longer than the task timeout (navigation: no progress for that long).</summary>
        Timeout,
        /// <summary>No clear way to the position.</summary>
        Unreachable,
        /// <summary>The drone lacks the tool / connector the task needs.</summary>
        MissingEquipment,
        /// <summary>Beacon / connector / grid not found, or not the drone owner's or their faction's.</summary>
        TargetNotFound,
    }

    /// <summary>
    /// A task: one struct for every kind, discriminated by <see cref="Type"/>. Protobuf only writes fields that
    /// differ from their default, so the fields a type doesn't use cost nothing on the wire (unlike a
    /// polymorphic list, which needs a sub-type wrapper per item, and can't hold structs at all).
    /// Fields are ordered largest first for packing (96 bytes).
    /// Positions are world (absolute), or, when RelativeBeaconEntityId != 0, in that beacon's frame (Right, Up,
    /// Forward from its centre) so work on a moving grid follows it. Never relative to a drone's home connector:
    /// jobs are shared with orchestrators, which know nothing about a bidding drone's home.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public struct Task
    {
        [ProtoMember(3)] public Vector3DData Position;
        [ProtoMember(4)] public Vector3DData ApproachFrom;
        [ProtoMember(10)] public DiscreteInventory Inventory;
        /// <summary>Frame for Position / ApproachFrom: a beacon of the drone owner / their faction; 0 = world.</summary>
        [ProtoMember(2)] public long RelativeBeaconEntityId;
        /// <summary>Tool tasks: the grid (projections: the grid the block will be built on). DockAt: the connector.</summary>
        [ProtoMember(6)] public long TargetEntityId;
        /// <summary>Tool tasks: the block's cell in that grid (projections: the cell it will occupy).</summary>
        [ProtoMember(7)] public Vector3IData TargetBlock;
        /// <summary>Magnitude of natural gravity at Position (m/s²), for planning.</summary>
        [ProtoMember(5)] public float NaturalGravity;
        /// <summary>Tool tasks: percent or seconds, depending on Completion.</summary>
        [ProtoMember(9)] public float CompletionValue;
        [ProtoMember(1)] public TaskType Type;
        [ProtoMember(8)] public ToolTaskCompletionTrigger Completion;
        [ProtoMember(11)] public bool IsProjected;

        public TaskKind Kind { get { return KindOf(Type); } }

        public static TaskKind KindOf(TaskType type)
        {
            switch (type)
            {
                case TaskType.NavigateTo:
                case TaskType.NavigateToolTo:
                case TaskType.DockAt:
                case TaskType.BackOut:
                case TaskType.ReturnHome:      return TaskKind.Navigation;
                case TaskType.LoadInventory:
                case TaskType.UnloadInventory: return TaskKind.Inventory;
                case TaskType.WeldBlock:
                case TaskType.GrindBlock:
                case TaskType.Mine:            return TaskKind.Tool;
                case TaskType.DeliverMessage:  return TaskKind.Message;
                default:                       return TaskKind.None;
            }
        }

        public static Task NavigateTo(long beaconId, Vector3D position, Vector3D approachFrom, float naturalGravity = 0)
        {
            return new Task
            {
                Type = TaskType.NavigateTo, RelativeBeaconEntityId = beaconId, NaturalGravity = naturalGravity,
                Position = Vector3DData.FromVector3D(position), ApproachFrom = Vector3DData.FromVector3D(approachFrom),
            };
        }

        public static Task NavigateToolTo(long beaconId, Vector3D position, Vector3D approachFrom, float naturalGravity = 0)
        {
            return new Task
            {
                Type = TaskType.NavigateToolTo, RelativeBeaconEntityId = beaconId, NaturalGravity = naturalGravity,
                Position = Vector3DData.FromVector3D(position), ApproachFrom = Vector3DData.FromVector3D(approachFrom),
            };
        }

        /// <summary>Position: planned approach point (the drone replaces it with the one it actually used).</summary>
        public static Task BackOut(long beaconId, Vector3D position)
        {
            return new Task
            {
                Type = TaskType.BackOut, RelativeBeaconEntityId = beaconId,
                Position = Vector3DData.FromVector3D(position), ApproachFrom = Vector3DData.FromVector3D(position),
            };
        }

        /// <summary>Position: the connector's connection point; ApproachFrom: out along its axis.</summary>
        public static Task DockAt(long connectorId, Vector3D position, Vector3D approachFrom, long beaconId = 0)
        {
            return new Task
            {
                Type = TaskType.DockAt, TargetEntityId = connectorId, RelativeBeaconEntityId = beaconId,
                Position = Vector3DData.FromVector3D(position), ApproachFrom = Vector3DData.FromVector3D(approachFrom),
            };
        }

        public static Task ReturnHome()
        {
            return new Task { Type = TaskType.ReturnHome };
        }

        public static Task Load(DiscreteInventory inventory)
        {
            return new Task { Type = TaskType.LoadInventory, Inventory = inventory };
        }

        /// <summary>Null = everything the job dealt in.</summary>
        public static Task Unload(DiscreteInventory inventory = null)
        {
            return new Task { Type = TaskType.UnloadInventory, Inventory = inventory };
        }

        public static Task Weld(long gridId, Vector3I cell, bool projected)
        {
            return new Task
            {
                Type = TaskType.WeldBlock, TargetEntityId = gridId, TargetBlock = Vector3IData.FromVector3I(cell),
                IsProjected = projected, Completion = ToolTaskCompletionTrigger.BlockFullIntegrity,
            };
        }

        public static Task Grind(long gridId, Vector3I cell)
        {
            return new Task
            {
                Type = TaskType.GrindBlock, TargetEntityId = gridId, TargetBlock = Vector3IData.FromVector3I(cell),
                Completion = ToolTaskCompletionTrigger.BlockFullyDismounted,
            };
        }

        public static Task Mine(ToolTaskCompletionTrigger completion = ToolTaskCompletionTrigger.DroneInventoryFull, float value = 0)
        {
            return new Task { Type = TaskType.Mine, Completion = completion, CompletionValue = value };
        }
    }

    /// <summary>
    /// What one drone does in one trip: an ordered task list (load, go to each block, work it, go home, unload),
    /// plus a summary for auctions and planning. A class, not a struct: jobs are long-lived, shared between
    /// queues and dictionaries and updated in place (AssignedTime, task progress); a struct copy would silently
    /// drop those updates. Tasks are small values inside it.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Job
    {
        /// <summary>
        /// Sum of the LoadInventory tasks: what the drone must pick up. Makes it simple, when auctioning, to know
        /// which conveyor networks can fulfil the job's inventory in its totality.
        /// </summary>
        [ProtoMember(1)] public DiscreteInventory TotalInventory;
        /// <summary>Ordered list of tasks to execute.</summary>
        [ProtoMember(2)] public List<Task> Tasks = new List<Task>();
        /// <summary>For expiring stale jobs.</summary>
        [ProtoMember(3)] public DateTime CreatedTime;
        [ProtoMember(4)] public DateTime AssignedTime;
        [ProtoMember(6)] public uint JobId;
        /// <summary>Amount of individual blocks that are part of this job.</summary>
        [ProtoMember(7)] public ushort BlockCount;
        [ProtoMember(5)] public JobType JobType;

        public Job() { }

        public void CalculateTotalInventory()
        {
            TotalInventory = new DiscreteInventory();
            if (Tasks == null) return;
            for (int i = 0; i < Tasks.Count; i++)
                if (Tasks[i].Type == TaskType.LoadInventory && Tasks[i].Inventory != null)
                    TotalInventory.AddItems(Tasks[i].Inventory);
        }

        /// <summary>The first place the drone works at (first NavigateToolTo / NavigateTo), e.g. for auction distances.</summary>
        public bool TryGetSite(out Task site)
        {
            site = default(Task);
            if (Tasks == null) return false;
            for (int i = 0; i < Tasks.Count; i++)
            {
                if (Tasks[i].Type == TaskType.NavigateToolTo || Tasks[i].Type == TaskType.NavigateTo)
                {
                    site = Tasks[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>Index of the first task of 'type' at or after 'from', or -1.</summary>
        public int IndexOf(TaskType type, int from = 0)
        {
            if (Tasks == null) return -1;
            for (int i = Math.Max(0, from); i < Tasks.Count; i++)
                if (Tasks[i].Type == type) return i;
            return -1;
        }
    }
}
