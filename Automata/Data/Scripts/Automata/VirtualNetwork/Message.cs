using ProtoBuf;
using System;
using System.Collections.Generic;
using Automata.Util;
using VRage;
using VRage.Utils;

using Automata.Config;
using Automata.Drone;
using Automata.Logistics;
using Automata.Inventory;

namespace Automata.VirtualNetwork
{
    public enum ErrorCode : ushort
    {
        None = 0,
        MessageIsNull = 1,
        RecipientNotSet = 2,
        SenderNotSet = 4,
        RecipientNotRegistered = 8,
        RecipientNotInRange = 16,
        RecipientNotValid = 32,
        RecipientNotFriendly = 64,
        NoRecipientsInRange = 128,
        NoSubscribers = 256,
        InvalidChannel = 512,
        RecipientNotFound = 1024,
        NotSubscribed = 2048,
        SubscriberAntennaNotRegistered = 4096,
        SenderAntennaNotRegistered = 8192,
        ShutdownInProgress = 16384,
        /// <summary>ReadMessages: nothing to take now (early exit, no work done): ask again later.</summary>
        TryAgainLater = 32768,
    }

    //public class MessageResponse
    //{
    //    public ErrorCode ErrorCode;
    //    public MessageResponse()
    //    {
    //        MessageId = 0;
    //        ErrorCode = ErrorCode.None;
    //    }
    //}
    public class ReadMessagesResponse
    {
        public ErrorCode ErrorCode;
        public ReadMessagesResponse()
        {
            ErrorCode = ErrorCode.None;
        }
    }

    public interface IMessagePayload { }

    [Flags]
    public enum PayloadType : ushort
    {
        None = 0,
        // Free bits (payloads that never existed or were removed; no saved message carries them): 4, 8, 64, 128.
        DesignatorOrderUpdate = 1,
        TaskAssignment = 2,
        RelayMessage = 16,
        TaskAborted = 32,
        JobAnnouncement = 256,
        MiningSurveyDataBroadcast = 512,
        Auction = 1024,
        Bid = 2048,
        AuctionWinnerAnnouncement = 4096,
        JobAnnouncementFailed = 8192,
        JobStatusReport = 16384,
        PlayerAlert = 32768,
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public enum Channel : byte
    {
        [ProtoEnum]
        NONE,
        [ProtoEnum]
        LOGISTIC_REQUEST,
        [ProtoEnum]
        LOGISTIC_PUSH,
        [ProtoEnum]
        MAILMAN_FORWARD,
        [ProtoEnum]
        DIRECT_MESSAGE,
        [ProtoEnum]
        CONSTRUCTION_COMPUTER_REGISTRATION,
        [ProtoEnum]
        CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
        [ProtoEnum]
        ORCHESTRATOR_AUCTION_START,
        [ProtoEnum]
        ORCHESTRATOR_AUCTION_BIDS,
        [ProtoEnum]
        ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT,
        [ProtoEnum]
        DEAD_LETTER_QUEUE,
        [ProtoEnum]
        MINING_DATA_FOUND_ANNOUNCEMENT,
        [ProtoEnum]
        TASKS_ABORTED,
        [ProtoEnum]
        TASKS_COMPLETED,
        /// <summary>Orchestrator -> announcer: a job's auction got no bid at all; the announcer holds the job.</summary>
        [ProtoEnum]
        JOB_ANNOUNCEMENT_FAILED,
        /// <summary>Mining surveyor -> orchestrators: MineOre jobs.</summary>
        [ProtoEnum]
        MINING_JOB_ANNOUNCEMENT,
        /// <summary>Drone -> the job's announcer (construction computer, mining surveyor): still working on it
        /// (JobStatusReport, Outcome Working), every minute. Keeps the assigned job from expiring.</summary>
        [ProtoEnum]
        JOB_KEEPALIVE,
        /// <summary>Announcer -> orchestrators: a job it no longer wants (order removed); its queue entry / running auction
        /// is dropped (JobStatusReport: AnnouncerEntityId, JobId, Reason).</summary>
        [ProtoEnum]
        JOB_WITHDRAWN,
        /// <summary>Any block -> its orchestrator (directed): something the player should hear about (PlayerAlert); the orchestrator
        /// is the only block that writes to chat.</summary>
        [ProtoEnum]
        PLAYER_ALERT,
        /// <summary>Target designator block -> orchestrators: a player's order (JobAnnouncement with the order fields, EntityType
        /// TargetDesignatorBlock). The orchestrator ingests it as a player job and answers on DESIGNATOR_ORDER_UPDATE.</summary>
        [ProtoEnum]
        DESIGNATOR_JOB_ANNOUNCEMENT,
        /// <summary>Orchestrator -> the designator block that sent an order (directed): received / confirmed / cancelled
        /// (DesignatorOrderUpdate).</summary>
        [ProtoEnum]
        DESIGNATOR_ORDER_UPDATE,
    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TaskAssignment : IMessagePayload
    {
        [ProtoMember(1)]
        public List<Orchestrator.Task> Tasks;
    }

    /// <summary>
    /// Drone gives up on an assigned task (e.g. pathfinding complexity budget exceeded).
    /// Scheduler should release the assignment and may blacklist this drone for the task temporarily.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TaskAborted : IMessagePayload
    {
        [ProtoMember(1)]
        public ushort TaskId;
        [ProtoMember(2)]
        public string Reason;
    }


    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class RelayMessage : IMessagePayload
    {
        [ProtoMember(1)]
        public long DestinationEntityId;
        [ProtoMember(2)]
        public Channel DestinationTopic;
        [ProtoMember(3)]
        public long OriginalSenderId;
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Message<T> where T : class, IMessagePayload
    {
        public uint MessageId;
        public T Payload;
        public ulong CreatedAt;
        public ulong SentAt;
        /// <summary>IAI entity (Drone, Scheduler, LC) id of recipient.</summary>
        public long SenderId;
        public long SenderOwnerId;
        public Channel Channel;
        public MessageQueue.IAIBlockType RecipientBlockType;
        /// <summary>0: every reader of RecipientBlockType in range; else only the block with this entity id (a directed message: the reader checks one id before any range work).</summary>
        public long RecipientId;
        /// <summary>The queue's entry for this send (set by BroadcastMessage; not serialized).</summary>
        public MessageQueue.TimestampedMessage Receipt;
        /// <summary>At least one reader (in range) has taken it.</summary>
        public bool Delivered { get { return Receipt != null && Receipt.Deliveries > 0; } }
    }


    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class JobAnnouncement : IMessagePayload
    {
        [ProtoMember(1)] public QuaternionDData OrientationData;
        [ProtoMember(2)] public Vector3DData PositionData;
        [ProtoMember(3)] public DiscreteInventory ComponentsInventory;
        [ProtoMember(4)] public DiscreteInventory BlocksInventory;
        [ProtoMember(5)] public DateTime CreatedTime;
        /// <summary>
        /// Normalized natural gravity vector.
        /// </summary>
        [ProtoMember(6)] public float NaturalGravity;
        [ProtoMember(7)] public uint JobId;
        [ProtoMember(8)] public AutomataEntityType EntityType;
        [ProtoMember(9)] public Orchestrator.JobType Type;
        [ProtoMember(10)] public bool IsStaticGrid;
        [ProtoMember(11)] public bool IsInSpace;
        [ProtoMember(12)] public bool IsInAtmosphere;
        /// <summary>The job's ordered tasks (world positions: announced jobs have no anchor).</summary>
        [ProtoMember(13)] public List<Orchestrator.Task> Tasks;
        [ProtoMember(14)] public ushort BlockCount;
        /// <summary>Logistics jobs: the logistics computer that announced it (requesting / pushing).</summary>
        [ProtoMember(15)] public long RequesterEntityId;
        /// <summary>Logistics jobs: where a drone docks at the announcer (a free connector of its network).</summary>
        [ProtoMember(16)] public IOLocationData Dock;
        /// <summary>Logistics jobs: every connector of the announcer's network a drone could use (TODO/14).</summary>
        [ProtoMember(17)] public Logistics.DockSite DockSite;
        /// <summary>Weld / grind jobs in gravity: how much each tool direction (Base6Directions.Direction, drone controller
        /// frame; index) suits the job's blocks, 0..1. Null: no preference (space). See ConstructionComputer.ToolFaceWeights.</summary>
        [ProtoMember(18)] public List<float> ToolFaceWeights;
        /// <summary>Weld jobs: which weld (JobTypeExtensions.WELD_*: repair, projected, unbuilt; 0 unknown). Welder profiles rank by it.</summary>
        [ProtoMember(19)] public byte WeldCategory;
        /// <summary>The blocks are built one after another (a projection line: each attaches to the one before): every
        /// trip goes to one drone, in turn, instead of several drones at once.</summary>
        [ProtoMember(20)] public bool Sequential;
        // Designator orders (Channel.DESIGNATOR_JOB_ANNOUNCEMENT; hand-held ones are put straight into the orchestrator)
        /// <summary>The player's order this job belongs to (the sender's ids; an area sweep is one order, a job per sector).</summary>
        [ProtoMember(21)] public uint OrderId;
        /// <summary>The player who pointed it (identity id).</summary>
        [ProtoMember(22)] public long Identity;
        [ProtoMember(23)] public Pointer.PointerKind Kind;
        /// <summary>The order's GPS marker name.</summary>
        [ProtoMember(24)] public string Marker;
        /// <summary>What it is, for the orchestrator's log ("Spot check 2/3").</summary>
        [ProtoMember(25)] public string What;
        /// <summary>A tagged drone's order: only that drone may take it (0: any drone of the profile).</summary>
        [ProtoMember(26)] public long RequiredDrone;
        /// <summary>The designator block that sent it (0: a hand-held one): answered on DESIGNATOR_ORDER_UPDATE; a Drone Controller
        /// on its construct never takes the job.</summary>
        [ProtoMember(27)] public long OriginBlockId;

        /// <summary>Summary fields from the job's first work site and total inventory.</summary>
        public static JobAnnouncement FromJob(Orchestrator.Job job, AutomataEntityType entityType, bool isStaticGrid)
        {
            Orchestrator.Task site;
            job.TryGetSite(out site);
            if (job.TotalInventory == null) job.CalculateTotalInventory();
            return new JobAnnouncement
            {
                JobId = job.JobId,
                Type = job.JobType,
                EntityType = entityType,
                CreatedTime = job.CreatedTime,
                ComponentsInventory = job.TotalInventory,
                PositionData = site.Position,
                NaturalGravity = site.NaturalGravity,
                IsStaticGrid = isStaticGrid,
                IsInSpace = site.NaturalGravity < SPACE_GRAVITY,
                IsInAtmosphere = site.NaturalGravity >= SPACE_GRAVITY,
                Tasks = job.Tasks != null ? new List<Orchestrator.Task>(job.Tasks) : null,   // payloads are shared by every recipient: own list
                BlockCount = job.BlockCount,
                WeldCategory = job.WeldCategory,
            };
        }

        /// <summary>m/s²: below this a site counts as "in space".</summary>
        public const float SPACE_GRAVITY = 0.2f;

        public Orchestrator.Job ToJob()
        {
            return new Orchestrator.Job
            {
                JobId = JobId,
                JobType = Type,
                CreatedTime = CreatedTime,
                TotalInventory = ComponentsInventory,
                Tasks = Tasks != null ? new List<Orchestrator.Task>(Tasks) : new List<Orchestrator.Task>(),   // the payload is shared: the job gets its own list
                BlockCount = BlockCount,
                WeldCategory = WeldCategory,
            };
        }
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Auction : IMessagePayload
    {
        [ProtoMember(1)] public QuaternionDData OrientationData;
        [ProtoMember(2)] public Vector3DData PositionData;
        [ProtoMember(3)] public DiscreteInventory ComponentsInventory;
        [ProtoMember(4)] public DiscreteInventory BlocksInventory;
        [ProtoMember(5)] public DateTime CreatedTime;
        [ProtoMember(6)] public DateTime ExpirationTime;
        /// <summary>
        /// Normalized natural gravity vector.
        /// </summary>
        [ProtoMember(7)] public float NaturalGravity;
        [ProtoMember(8)] public MyFixedPoint TotalMass;
        [ProtoMember(9)] public MyFixedPoint TotalVolume;
        [ProtoMember(10)] public uint JobId;
        [ProtoMember(11)] public uint AuctionId;
        [ProtoMember(16)] public AutomataEntityType EntityType;   // was a duplicate tag 9 (TotalVolume)
        [ProtoMember(12)] public Orchestrator.JobType JobType;
        [ProtoMember(13)] public bool IsStaticGrid;
        [ProtoMember(14)] public bool IsInSpace;
        [ProtoMember(15)] public bool IsInAtmosphere;
        /// <summary>Logistics jobs: the announcing logistics computer (it never bids on its own job).</summary>
        [ProtoMember(17)] public long RequesterEntityId;
        /// <summary>Logistics jobs: where a drone docks at the announcer.</summary>
        [ProtoMember(18)] public IOLocationData Dock;
        /// <summary>The block that announced the job (with JobId: the job's identity; orchestrators dedupe on it).</summary>
        [ProtoMember(19)] public long AnnouncerEntityId;
        /// <summary>Logistics jobs: the announcer's connectors (with Dock: its preferred one).</summary>
        [ProtoMember(20)] public Logistics.DockSite DockSite;
        /// <summary>Weld / grind jobs in gravity: the announcement's tool face weights (null: no preference).</summary>
        [ProtoMember(21)] public List<float> ToolFaceWeights;
        /// <summary>A designator order for a tagged drone: only this drone (controller entity id) bids; 0: any.</summary>
        [ProtoMember(22)] public long OnlyDroneEntityId;
        /// <summary>Weld jobs: which weld (JobTypeExtensions.WELD_*; 0 unknown).</summary>
        [ProtoMember(23)] public byte WeldCategory;
        /// <summary>See JobAnnouncement.Sequential.</summary>
        [ProtoMember(24)] public bool Sequential;
        /// <summary>A designator order sent by a target designator block: a drone on that grid's construct (mechanical group) never bids.</summary>
        [ProtoMember(25)] public long NoBidGridId;
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Bid : IMessagePayload
    {
        [ProtoMember(1)] public DiscreteInventory BidInventory;
        [ProtoMember(2)] public IOLocationData IOLocationData;
        [ProtoMember(4)] public DateTime CreatedTime;
        /// <summary>
        /// Maximum load capacity in 1G, in relation to thrust, not actual cargo capacity
        /// </summary>
        [ProtoMember(5)] public MyFixedPoint MaxLoadIn1G;
        /// <summary>
        /// Optimal load capacity in 1G. These are Player configured limits on the drone.
        /// </summary>
        [ProtoMember(6)] public MyFixedPoint OptimalLoadIn1G;
        /// <summary>
        /// Max cargo volume in m^3.
        /// </summary>
        [ProtoMember(7)] public MyFixedPoint MaxCargoVolume;
        /// <summary>
        /// EntityId of bidding entity.
        /// </summary>
        [ProtoMember(8)] public long EntityId;
        [ProtoMember(9)] public uint JobId;
        [ProtoMember(10)] public uint AuctionId;
        [ProtoMember(11)] public uint BidId;
        /// <summary>
        /// Inventory that the logicstics Computter is submitting as part of the bid.
        /// </summary>
        [ProtoMember(12)] public Drone.Capabilities Capabilities;
        [ProtoMember(13)] public AutomataEntityType EntityType;
        [ProtoMember(14)] public Drone.BehaviourProfile DroneBehaviours;

        /// <summary>
        /// This flag controls how the BidInventory field should be interpreted.
        /// When the job demands inventory space, this response should be interpreted as "Has space to take inventory"
        /// </summary>
        [ProtoMember(15)] public Logistics.InventoryFulfillment InventoryFulfillmentFlags;   // was a duplicate tag 13 (EntityType)
        /// <summary>Logistics computers: the connectors of the bidding network (IOLocationData: the preferred one).</summary>
        [ProtoMember(16)] public Logistics.DockSite DockSite;
        /// <summary>Drones: the faces (Base6Directions.Direction, controller frame) of their usable standard (large) connectors.</summary>
        [ProtoMember(17)] public List<byte> DockFaces;
        /// <summary>Drones: the same for their small connectors (combat drones, ore scouts, ore haulers only; TODO/14).</summary>
        [ProtoMember(18)] public List<byte> SmallDockFaces;
        /// <summary>Drones, weld / grind auctions: the faces (Base6Directions.Direction, controller frame) of their welders / grinders.</summary>
        [ProtoMember(19)] public List<byte> ToolFaces;
        /// <summary>Drones, weld / grind auctions: the steepest pitch / roll (degrees from level, in gravity) it works at:
        /// its P-Gravity limits (180 with Align to P-Gravity off), capped by what it learnt it can hold.</summary>
        [ProtoMember(20)] public float WorkPitchDegrees;
        [ProtoMember(21)] public float WorkRollDegrees;
        /// <summary>Drones, weld / grind auctions: can work upside down (no P-Gravity limits, and its "down" thrusters lift it).</summary>
        [ProtoMember(22)] public bool CanFlyInverted;
        /// <summary>Logistics computers, MineOre auctions: the best working refinery on the bid's network: 0 none, 1 basic,
        /// 2 refinery / industrial, 3 prototech (ores a basic one can't take go to the better ones).</summary>
        [ProtoMember(23)] public byte RefineryTier;
        /// <summary>Logistics computers, MineOre auctions: a working O2/H2 generator on the bid's network (ice).</summary>
        [ProtoMember(24)] public bool GasGenerators;
        /// <summary>The connector the drone is docked at right now (0: none). The orchestrator keeps it as the dock of a
        /// job that starts at that station: no undock / dock again to load.</summary>
        [ProtoMember(25)] public long DockedConnectorId;

    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class AuctionWinnerAnnouncement : IMessagePayload
    {
        /// <summary>
        ///  Dictonary of entity id of bid winner, to the list of jobs to perform.
        /// </summary>
        [ProtoMember(1)] public Dictionary<long, List<Orchestrator.Task>> TaskAssignments;
        [ProtoMember(5)] public DateTime CreatedTime;
        [ProtoMember(6)] public long EntityId;
        [ProtoMember(7)] public uint JobId;
        [ProtoMember(8)] public uint AuctionId;
        [ProtoMember(9)] public uint BidId;
        /// <summary>The block that announced the job (job ids are only unique per announcer).</summary>
        [ProtoMember(10)] public long AnnouncerEntityId;
        /// <summary>The logistics computers the drones dock at for this job (component sources, ore / part takers, suppliers).</summary>
        [ProtoMember(11)] public List<long> ComponentSources;
        [ProtoMember(12)] public Orchestrator.JobType JobType;
        /// <summary>Weld / grind: other drones work (or may work) the same site - the job went to several drones, or
        /// another weld / grind job awarded near it is still being worked. Drones managed by orchestrator watch the
        /// others near the site more closely (DroneControllerBlock.SharedSite.cs).</summary>
        [ProtoMember(13)] public bool SharedSite;
        /// <summary>Weld jobs: which weld (JobTypeExtensions.WELD_*; 0 unknown).</summary>
        [ProtoMember(14)] public byte WeldCategory;
        /// <summary>The block this copy of the award is for (a directed message); 0: an award sent to everybody (saved before awards were directed).</summary>
        [ProtoMember(15)] public long RecipientId;
    }

    /// <summary>
    /// Orchestrator -> announcer (construction / logistics computer): the job's auction got no bid within the
    /// orchestrator's no-bid timeout. The announcer puts the job on hold for HoldSeconds, then announces it again.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class JobAnnouncementFailed : IMessagePayload
    {
        [ProtoMember(1)] public long AnnouncerEntityId;
        [ProtoMember(2)] public uint JobId;
        [ProtoMember(3)] public uint AuctionId;
        [ProtoMember(4)] public string Reason;
        [ProtoMember(5)] public int HoldSeconds;
        [ProtoMember(6)] public long OrchestratorEntityId;
    }



    /// <summary>One ore an ore scan found (<see cref="Mining.OreScanner"/>): averaged position of its voxels in a 32 m cell.</summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class MiningOreDepositSample
    {
        [ProtoMember(1)] public Vector3DData Position;
        /// <summary>Ore inventory key ("Ore/Iron").</summary>
        [ProtoMember(2)] public string Ore;
        /// <summary>Ore voxels of LOD 2 counted: a rough size.</summary>
        [ProtoMember(3)] public int Voxels;
        [ProtoMember(4)] public long VoxelId;
    }

    /// <summary>
    /// One complete ore scan: the sphere it covered and everything it found in it. A surveyor's known deposit inside
    /// the sphere that the scan did not find again (same ore within the merge distance) is depleted.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class SurveySweep
    {
        [ProtoMember(1)] public Vector3DData Center;
        [ProtoMember(2)] public float Radius;
        /// <summary>Seconds before the message was sent (old sweeps don't undo newer knowledge).</summary>
        [ProtoMember(3)] public double AgeSeconds;
        [ProtoMember(4)] public List<MiningOreDepositSample> Hits = new List<MiningOreDepositSample>();
        /// <summary>Kept by the drone: when it was scanned (UTC ticks; AgeSeconds is computed from it when sent).</summary>
        [ProtoMember(5)] public long ScannedUtcTicks;
    }

    /// <summary>Drone -> mining surveyors (MINING_DATA_FOUND_ANNOUNCEMENT): its ore scans since the last delivery, oldest first.</summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class MiningSurveyDataBroadcast : IMessagePayload
    {
        [ProtoMember(2)] public List<SurveySweep> Sweeps = new List<SurveySweep>();
        [ProtoMember(3)] public long DroneEntityId;
    }

    public enum JobOutcome : byte
    {
        /// <summary>Every task of the drone's share done.</summary>
        Completed,
        /// <summary>Started, not finished (task failed, power, load, stopped, AI off...).</summary>
        Aborted,
        /// <summary>The award arrived while the drone could not take it (busy, AI off, mode changed).</summary>
        Declined,
        /// <summary>Keepalive (JOB_KEEPALIVE): still working on it.</summary>
        Working,
    }

    /// <summary>
    /// Drone -> announcer + orchestrator (TASKS_COMPLETED or TASKS_ABORTED): how the drone's share of an awarded job
    /// ended. The announcer closes or re-opens the job on it instead of waiting for its award to time out.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class JobStatusReport : IMessagePayload
    {
        [ProtoMember(1)] public long AnnouncerEntityId;
        [ProtoMember(2)] public uint JobId;
        [ProtoMember(3)] public uint AuctionId;
        [ProtoMember(4)] public long OrchestratorEntityId;
        [ProtoMember(5)] public long DroneEntityId;
        [ProtoMember(6)] public JobOutcome Outcome;
        [ProtoMember(7)] public string Reason;
        /// <summary>Tool tasks done / in its share (0 / 0 for logistics trips).</summary>
        [ProtoMember(8)] public int BlocksDone;
        [ProtoMember(9)] public int BlocksTotal;
        [ProtoMember(10)] public Orchestrator.JobType JobType;
        /// <summary>Mining: kg of ore unloaded (the surveyor's order counts it).</summary>
        [ProtoMember(11)] public double OreKg;
        /// <summary>Weld jobs: which weld (JobTypeExtensions.WELD_*; 0 unknown).</summary>
        [ProtoMember(12)] public byte WeldCategory;
        /// <summary>Blocks of its share the drone found no way to: grid ids, with UnreachableCells (same order); null: none.</summary>
        [ProtoMember(13)] public List<long> UnreachableGrids;
        [ProtoMember(14)] public List<Vector3IData> UnreachableCells;
        /// <summary>The drone reports its unreachable blocks (an empty list: none): the computer holds those back, not the whole job.</summary>
        [ProtoMember(15)] public bool PerBlock;
        /// <summary>Keepalives: where the drone was when it sent this (the last known position if it goes silent); null: unknown.</summary>
        [ProtoMember(16)] public Vector3DData LastPosition;
    }

    /// <summary>A chat line for the player (PLAYER_ALERT): 'Text' is complete, it starts with the name of whoever meant to say it.</summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class PlayerAlert : IMessagePayload
    {
        [ProtoMember(1)] public long SenderEntityId;
        [ProtoMember(2)] public string Text;
    }

    public enum DesignatorOrderState : byte
    {
        /// <summary>An orchestrator took the order.</summary>
        Received = 0,
        /// <summary>Every job of the order has a drone.</summary>
        Confirmed = 1,
        /// <summary>Not taken in time / no drone: the order's jobs are cancelled.</summary>
        Cancelled = 2,
    }

    /// <summary>Orchestrator -> designator block (DESIGNATOR_ORDER_UPDATE, directed): how its order is doing; 'Text' is the line for the
    /// designator's LCD (empty: none).</summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class DesignatorOrderUpdate : IMessagePayload
    {
        [ProtoMember(1)] public uint OrderId;
        /// <summary>The job id the designator gave the announcement (0: the whole order).</summary>
        [ProtoMember(2)] public uint JobId;
        [ProtoMember(3)] public DesignatorOrderState State;
        [ProtoMember(4)] public string Text;
        [ProtoMember(5)] public long OrchestratorEntityId;
    }
}
