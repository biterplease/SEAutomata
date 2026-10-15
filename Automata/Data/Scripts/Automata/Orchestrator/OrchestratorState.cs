using System.Collections.Generic;

using ProtoBuf;

using Automata.VirtualNetwork;

namespace Automata.Orchestrator
{
    /// <summary>An orchestrator's save struct (<see cref="Util.BlockSave"/>; it has no terminal settings of its own).</summary>
    [ProtoContract]
    public class OrchestratorSaveData
    {
        [ProtoMember(1)] public OrchestratorState State;
        /// <summary>The block that saved it (<see cref="Util.BlockSave.OwnState"/>).</summary>
        [ProtoMember(2)] public long EntityId;
        [ProtoMember(3)] public OrchestratorBlockSettings Settings;
    }

    /// <summary>
    /// Inbox: job announcements waiting for an auction (Queue) and the bids collected by open auctions. Ongoing work:
    /// open auctions, jobs it knows (repeats ignored), counters. Outbox: winners / failures not delivered yet (Winners,
    /// Failures). (Messages in flight are the virtual network's: MessageQueue.Serialize.)
    /// </summary>
    [ProtoContract]
    public class OrchestratorState
    {
        [ProtoMember(1)] public int AuctionCounter;
        [ProtoMember(2)] public int MessageCounter;
        [ProtoMember(3)] public List<QueuedJobState> Queue = new List<QueuedJobState>();
        [ProtoMember(4)] public List<AuctionState> Auctions = new List<AuctionState>();
        [ProtoMember(5)] public List<KnownJobState> KnownJobs = new List<KnownJobState>();
        [ProtoMember(6)] public List<AuctionWinnerAnnouncement> Winners = new List<AuctionWinnerAnnouncement>();
        [ProtoMember(7)] public List<JobAnnouncementFailed> Failures = new List<JobAnnouncementFailed>();
        [ProtoMember(8)] public int Assigned;
        [ProtoMember(9)] public int Dropped;
        [ProtoMember(10)] public int Failed;
        // ProtoMember 11 was the messages in flight (saved by the message queue now)
        [ProtoMember(12)] public int Completed;
        [ProtoMember(13)] public int Aborted;
        [ProtoMember(14)] public int PlayerJobCounter;
        /// <summary>UTC ticks this orchestrator first ran (the earliest in a network runs it; the others go dormant).
        /// Kept in the state, not the settings: a pasted copy gets its own (newer) one.</summary>
        [ProtoMember(15)] public long CommissionedTicks;
        /// <summary>Jobs players ordered with a scout pointer, not over yet (<see cref="OrchestratorBlock"/>.PlayerJobs).</summary>
        [ProtoMember(16)] public List<PlayerJobState> PlayerJobs = new List<PlayerJobState>();
    }

    [ProtoContract]
    public class QueuedJobState
    {
        [ProtoMember(1)] public long Sender;
        [ProtoMember(2)] public long SenderOwner;
        [ProtoMember(3)] public JobAnnouncement Announcement;
        [ProtoMember(4)] public int AgeTicks;
    }

    [ProtoContract]
    public class AuctionState
    {
        [ProtoMember(1)] public Auction Auction;
        [ProtoMember(2)] public Job Job;
        [ProtoMember(3)] public long Announcer;
        [ProtoMember(4)] public uint JobId;
        [ProtoMember(5)] public int AgeTicks;
        [ProtoMember(6)] public double ExpiresInSeconds;
        [ProtoMember(7)] public List<Bid> Bids = new List<Bid>();
    }

    [ProtoContract]
    public class KnownJobState
    {
        [ProtoMember(1)] public long Announcer;
        [ProtoMember(2)] public uint JobId;
        [ProtoMember(3)] public int AgeTicks;
        /// <summary>Ticks it is still remembered (int.MaxValue: queued / auctioned).</summary>
        [ProtoMember(4)] public int TicksLeft;
    }
}
