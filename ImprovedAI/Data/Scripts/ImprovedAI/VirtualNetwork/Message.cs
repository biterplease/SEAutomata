using ImprovedAI.Config;
using ProtoBuf;
using System;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI.VirtualNetwork
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

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public enum Channel : byte
    {
        [ProtoEnum]
        DRONE_REGISTRATION,
        [ProtoEnum]
        DRONE_REPORTS,
        [ProtoEnum]
        DRONE_PERFORMANCE,
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
        DEAD_LETTER_QUEUE,
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
        public Drone.UpdateFlags Flags;
        [ProtoMember(11)]
        public Drone.State? DroneState;
        [ProtoMember(12)]
        public Drone.Capabilities? Capabilities;
    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TaskAssignment : IMessagePayload
    {
        [ProtoMember(1)]
        public List<Scheduler.Task> Tasks;
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
    public class LogisticsUpdate : IMessagePayload
    {
        [ProtoMember(1)]
        public Inventory Inventory;
        [ProtoMember(2)]
        public List<Vector3D> Connectors;
        [ProtoMember(3)]
        public LogisticsComputer.OperationMode OperationMode;
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class InventoryRequisition : IMessagePayload
    {
        [ProtoMember(1)]
        public Inventory Inventory;
        [ProtoMember(2)]
        public Vector3D ConnectorLocation;
        [ProtoMember(3)]
        public Inventory.RequisitionType RequisitionType;
        /// <summary>
        /// Does the grid move.
        /// </summary>
        [ProtoMember(4)]
        public bool IsStatic;
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
        public long RecipientId;
        /// <summary>IAI entity (Drone, Scheduler, LC) id of sender.</summary>
        public long SenderId;
        public long SenderOwnerId;
        public bool RequiresAck;
        public MessageSerializationMode SerializationMode;
        public Channel Channel;
        public MessageQueue.IAIBlockType RecipientBlockType;
    }
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TaskAnnouncement : IMessagePayload {
        [ProtoMember(1)] public uint TaskId;
        [ProtoMember(2)] public Scheduler.TaskType Type;
        [ProtoMember(3)] public Vector3D Destination;
        [ProtoMember(4)] public Drone.Capabilities RequiredCapabilities;
    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public class TaskBid : IMessagePayload {
        [ProtoMember(1)] public uint TaskId;
        [ProtoMember(2)] public float EstimatedTime; // Seconds
        [ProtoMember(3)] public float PathComplexity; // 0.0 to 1.0
    }
    
}
