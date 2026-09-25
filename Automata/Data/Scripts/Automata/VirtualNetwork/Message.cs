using ProtoBuf;
using System;
using System.Collections.Generic;
using Automata.Util;
using VRage;
using VRage.Utils;

using Automata.Config;
using Automata.DroneController;
using Automata.VirtualInventory;

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
        DroneReport = 1,
        TaskAssignment = 2,
        LogisticsUpdate = 4,
        InventoryRequisition = 8,
        RelayMessage = 16,
        TaskAborted = 32,
        TaskAnnouncement = 64,
        TaskBid = 128,
        TaskFulfillmentLost = 256,
        JobAnnouncement = 512,
        MiningSurveyDataBroadcast = 8192,
        Auction = 1024,
        Bid = 2048,
        AuctionWinnerAnnouncement = 4096,
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public enum Channel : byte
    {
        [ProtoEnum]
        NONE,
        [ProtoEnum]
        DRONE_REGISTRATION,
        [ProtoEnum]
        DRONE_REPORTS,
        [ProtoEnum]
        DRONE_PERFORMANCE,
        [ProtoEnum]
        DRONE_TASK_ANNOUNCEMENT,
        [ProtoEnum]
        DRONE_TASK_ASSIGNMENT,
        [ProtoEnum]
        LOGISTIC_REGISTRATION,
        [ProtoEnum]
        LOGISTIC_UPDATE,
        [ProtoEnum]
        LOGISTIC_REQUEST,
        [ProtoEnum]
        LOGISTIC_PUSH,
        [ProtoEnum]
        SCHEDULER_FORWARD,
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
    }
    /// <summary>
    /// Drone messages sent back to the scheduler.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class DroneReport : IMessagePayload
    {
        [ProtoMember(1)]
        public string ErrorMessage;
        [ProtoMember(2)]
        public ushort? TaskId;
        [ProtoMember(3)]
        public float? BatteryChargePercent;
        [ProtoMember(4)]
        public float? BatteryRechargeThreshold;
        [ProtoMember(5)]
        public float? BatteryOperationalThreshold;
        [ProtoMember(6)]
        public float? H2Level;
        [ProtoMember(7)]
        public float? H2RefuelThreshold;
        [ProtoMember(8)]
        public float? H2OperationalThreshold;
        [ProtoMember(10)]
        public DroneController.UpdateFlags Flags;
        [ProtoMember(11)]
        public DroneController.State? DroneState;
        [ProtoMember(12)]
        public DroneController.Capabilities? Capabilities;
        /// <summary>EntityId of the drone that produced this report.</summary>
        [ProtoMember(13)]
        public long DroneEntityId;
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
        public bool RequiresAck;
        public MessageSerializationMode SerializationMode;
        public Channel Channel;
        public MessageQueue.IAIBlockType RecipientBlockType;
    }


    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class JobAnnouncement : IMessagePayload
    {
        [ProtoMember(1)] public QuaternionDData OrientationData;
        [ProtoMember(2)] public Vector3DData PositionData;
        [ProtoMember(3)] public Inventory ComponentsInventory;
        [ProtoMember(4)] public Inventory BlocksInventory;
        [ProtoMember(5)] public DateTime CreatedTime;
        /// <summary>
        /// Normalized natural gravity vector.
        /// </summary>
        [ProtoMember(6)] public float NaturalGravity;
        [ProtoMember(7)] public uint JobId;
        [ProtoMember(8)] public IAIEntityType EntityType;
        [ProtoMember(9)] public Orchestrator.JobType Type;
        [ProtoMember(10)] public bool IsStaticGrid;
        [ProtoMember(11)] public bool IsInSpace;
        [ProtoMember(12)] public bool IsInAtmosphere;
    }
    public class Auction : IMessagePayload
    {
        [ProtoMember(1)] public QuaternionDData OrientationData;
        [ProtoMember(2)] public Vector3DData PositionData;
        [ProtoMember(3)] public Inventory ComponentsInventory;
        [ProtoMember(4)] public Inventory BlocksInventory;
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
        [ProtoMember(9)] public IAIEntityType EntityType;
        [ProtoMember(12)] public Orchestrator.JobType JobType;
        [ProtoMember(13)] public bool IsStaticGrid;
        [ProtoMember(14)] public bool IsInSpace;
        [ProtoMember(15)] public bool IsInAtmosphere;
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class Bid : IMessagePayload
    {
        [ProtoMember(1)] public Inventory BidInventory;
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
        [ProtoMember(13)] public IAIEntityType EntityType;
        [ProtoMember(14)] public Drone.BehaviourProfile DroneBehaviours;

        /// <summary>
        /// This flag controls how the BidInventory field should be interpreted.
        /// When the job demands inventory space, this response should be interpreted as "Has space to take inventory"
        /// </summary>
        [ProtoMember(13)] public LogisticsComputer.InventoryFulfillment InventoryFulfillmentFlags;

    }

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
    }



    /// <summary>
    /// One ore hit from a drone ore detector (or other survey source).
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class MiningOreDepositSample
    {
        [ProtoMember(1)] public Vector3DData Position;
        [ProtoMember(2)] public MyStringHash OreSubtype;
        [ProtoMember(3)] public MyFixedPoint EstimatedMass;
    }

    /// <summary>
    /// Batch of ore samples broadcast to mining surveyors.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class MiningSurveyDataBroadcast : IMessagePayload
    {
        [ProtoMember(1)] public List<MiningOreDepositSample> Samples;
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TasksAborted
    {
        [ProtoMember(5)] public DateTime CompletedTime;
        [ProtoMember(6)] public long EntityId;
        [ProtoMember(7)] public uint JobId;
        [ProtoMember(8)] public uint BidRoundId;
        [ProtoMember(9)] public uint BidRoundBidId;

        [ProtoMember(9)] public uint CurrentTask;
        [ProtoMember(9)] public uint TotalTasks;
    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TasksCompleted
    {
        [ProtoMember(1)] public List<uint> TaskIds;
        [ProtoMember(5)] public DateTime CompletedTime;
        [ProtoMember(6)] public long EntityId;
        [ProtoMember(7)] public uint JobId;
        [ProtoMember(8)] public uint BidRoundId;
        [ProtoMember(9)] public uint BidRoundBidId;

        [ProtoMember(9)] public uint CurrentTask;
        [ProtoMember(9)] public uint TotalTasks;
    }
}
