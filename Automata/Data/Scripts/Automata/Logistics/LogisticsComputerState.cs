using System.Collections.Generic;

using ProtoBuf;

using Automata.Inventory;
using Automata.VirtualNetwork;

namespace Automata.Logistics
{
    /// <summary>A logistics computer's save struct (<see cref="Util.BlockSave"/>): terminal settings and runtime state.</summary>
    [ProtoContract]
    public class LogisticsComputerSaveData
    {
        [ProtoMember(1)] public LogisticsComputerSettings Settings;
        /// <summary>Server only; null in a client's copy.</summary>
        [ProtoMember(2)] public LogisticsComputerState State;
        /// <summary>The block that saved it (<see cref="Util.BlockSave.OwnState"/>).</summary>
        [ProtoMember(3)] public long EntityId;
    }

    /// <summary>
    /// Ongoing work: its open request / push jobs, the promises of its bids, id counters. (No inbox: what it reads is acted on at once.)
    /// </summary>
    [ProtoContract]
    public class LogisticsComputerState
    {
        [ProtoMember(1)] public int JobCounter;
        [ProtoMember(2)] public int MessageCounter;
        [ProtoMember(3)] public int BidCounter;
        [ProtoMember(4)] public LogisticsJobState Request;
        [ProtoMember(5)] public LogisticsJobState Push;
        [ProtoMember(6)] public List<CommitmentState> Commitments = new List<CommitmentState>();
        // ProtoMember 7 was the messages in flight (saved by the message queue now)
    }

    [ProtoContract]
    public class LogisticsJobState
    {
        [ProtoMember(1)] public uint JobId;
        [ProtoMember(2)] public Orchestrator.JobType Type;
        [ProtoMember(3)] public DiscreteInventory Items;
        [ProtoMember(4)] public int OpenedAgeTicks;
        [ProtoMember(5)] public int LastChangeAgeTicks;
        [ProtoMember(6)] public int LastTotal;
        /// <summary>Ticks since an orchestrator awarded it; -1: not awarded.</summary>
        [ProtoMember(7, IsRequired = true)] public int AssignedAgeTicks = -1;
        /// <summary>Ticks of hold left (its auction failed); 0: not held.</summary>
        [ProtoMember(8)] public int HoldTicksLeft;
        [ProtoMember(9)] public int DronesOut;
        [ProtoMember(10)] public bool Finished;
    }

    [ProtoContract]
    public class CommitmentState
    {
        [ProtoMember(1)] public uint AuctionId;
        [ProtoMember(2)] public DiscreteInventory Items;
        [ProtoMember(3)] public double Volume;
        [ProtoMember(4)] public int AgeTicks;
        [ProtoMember(5)] public bool Supply;
        [ProtoMember(6)] public long Network;
        /// <summary>Its bid won: reserved until the drone reports.</summary>
        [ProtoMember(7)] public bool Won;
        /// <summary>The job the auction was for (auction ids repeat after a reload without saving).</summary>
        [ProtoMember(8)] public uint JobId;
    }
}
