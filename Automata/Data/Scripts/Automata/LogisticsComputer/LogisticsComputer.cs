using ImprovedAI.Util;
using ProtoBuf;
using System;
using System.Collections.Generic;
using VRageMath;


namespace Automata.LogisticsComputer
{
    public interface ILogisticsComputer
    {
        bool IsInitialized();
        void Initialize();
        bool ScanComponents();
        bool MapConveyorNetworks();
        void ScanCargoContainers();
        void ReadBidRoundStartMessages();
        void SendRoundBids();
        void SetOperationMode(OperationMode operationMode);
    }

    // public enum ErrorCode : byte
    // {
    //     NONE = 0,
    //     NOT_INITIALIZED = 1
    // }

    // public class ErrorOutcome
    // {
    //     public string Code { get; }
    //     public string Message { get; }

    //     private ErrorOutcome(string code, string message)
    //     {
    //         Code = code;
    //         Message = message;
    //     }

    //     // Static instances acting as enum values
    //     public static ErrorOutcome None { get; } = new ErrorOutcome("NONE", "Operation succeeded.");
    //     public static ErrorOutcome JobNotFound { get; } = new ErrorOutcome("JOB_NOT_FOUND", "The requested job ID does not exist.");
    //     public static ErrorOutcome BidTooLow { get; } = new ErrorOutcome("BID_TOO_LOW", "Submitted bid must be higher than the current lowest bid.");
    //     public static ErrorOutcome WindowClosed { get; } = new ErrorOutcome("WINDOW_CLOSED", "Bidding for this job has already closed.");

    //     public override string ToString() => Message;
    // }

    /// <summary>
    /// Enum to signal how the the bid round inventory
    /// can be fulfilled.
    /// SatisfyFully: The inventory will be satisfied fully.
    /// SatisfyPartial: The inventory will be satisfied partially.
    /// AcceptAll: The inventory will be accepted fully, based on available volume.
    /// AcceptPartial: The inventory will be accepted partially, based on available volume.
    /// </summary>
    [Flags, ProtoContract]
    public enum InventoryFulfillment : byte
    {
        [ProtoEnum]
        None = 0,
        [ProtoEnum]
        SatisfyFully = 1,
        [ProtoEnum]
        SatisfyPartial = 2,
        [ProtoEnum]
        AcceptAll = 4,
        [ProtoEnum]
        AcceptPartial = 8,
    }
    [ProtoContract]
    public enum State : byte
    {
        [ProtoEnum]
        Initializing,
        [ProtoEnum]
        Active,
        [ProtoEnum]
        Error
    }
    public enum OperationMode : byte
    {
        None = 0,
        BuiltInToDrone = 1,
        BuiltInToOrchestrator = 2,
        LogisticsComputer = 4,
    }
    [Flags, ProtoContract]
    public enum WorkMode : byte
    {
        [ProtoEnum]
        None = 0,
        /// <summary>
        /// Provide inventory only for logistics tasks.
        /// </summary>
        [ProtoEnum]
        ProvideForLogistics = 1,
        /// <summary>
        /// Provide inventory only for construction tasks.
        /// </summary>
        [ProtoEnum]
        ProvideForConstruction = 2,
        /// <summary>
        /// Will only push inventory to the Logistic Network. 
        /// If an item quota is set, it will respect it.
        /// Will not provide inventory for any tasks.
        /// </summary>
        [ProtoEnum]
        Push = 4,
        /// <summary>
        /// Will only request inventory from the Logistic Network.
        /// If an item quota is set, it will respect it.
        /// Will not provide inventory for any tasks.
        /// </summary>
        [ProtoEnum]
        Request = 8
    }
    // [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    // public class LogisticsComputer
    // {
    //     [ProtoMember(1)]
    //     public long EntityId;
    //     [ProtoMember(2)]
    //     public OperationMode _OperationMode;
    //     /// <summary>
    //     /// Positions of available connectors.
    //     /// </summary>
    //     [ProtoIgnore]
    //     public List<MatrixD> connectors;
    //     [ProtoMember(3)]
    //     public List<MatrixDData> storedConnectors;
    //     [ProtoMember(4)]
    //     public Inventory LastKnownInventory;
    // }
}
