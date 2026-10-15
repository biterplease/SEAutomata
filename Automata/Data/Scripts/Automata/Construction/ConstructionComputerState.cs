using System.Collections.Generic;

using ProtoBuf;

using Automata.VirtualNetwork;

namespace Automata.Construction
{
    /// <summary>A construction computer's save struct (<see cref="Util.BlockSave"/>): terminal settings and runtime state.</summary>
    [ProtoContract]
    public class ConstructionComputerSaveData
    {
        [ProtoMember(1)] public ConstructionComputerBlockSettings Settings;
        /// <summary>Server only; null in a client's copy.</summary>
        [ProtoMember(2)] public ConstructionComputerState State;
        /// <summary>The block that saved it (<see cref="Util.BlockSave.OwnState"/>).</summary>
        [ProtoMember(3)] public long EntityId;
    }

    /// <summary>
    /// Ongoing work: its cached jobs (blocks, announcement, awarded / on hold), counters. Operating state: the planner's sweep frames of the grids it scans (rescans keep their direction). (No
    /// inbox: awards and failures are acted on when read.)
    /// </summary>
    [ProtoContract]
    public class ConstructionComputerState
    {
        [ProtoMember(1)] public int JobCounter;
        [ProtoMember(2)] public int MessageCounter;
        [ProtoMember(3)] public List<ConstructionJobState> Jobs = new List<ConstructionJobState>();
        [ProtoMember(4)] public int Done;
        [ProtoMember(5)] public int Expired;
        [ProtoMember(6)] public int Failed;
        // ProtoMember 7 was the messages in flight (saved by the message queue now)
        [ProtoMember(8)] public List<SweepFrameState> Frames = new List<SweepFrameState>();
    }

    [ProtoContract]
    public class ConstructionJobState
    {
        [ProtoMember(1)] public JobAnnouncement Announcement;
        [ProtoMember(2)] public List<BlockRefState> Blocks = new List<BlockRefState>();
        [ProtoMember(3)] public int OpenedAgeTicks;
        /// <summary>Ticks since an orchestrator awarded it; -1: not awarded.</summary>
        [ProtoMember(4, IsRequired = true)] public int AssignedAgeTicks = -1;
        /// <summary>Ticks of hold left (its auction failed); 0: not held.</summary>
        [ProtoMember(5)] public int HoldTicksLeft;
        [ProtoMember(6)] public bool Announced;
        [ProtoMember(7)] public int DronesOut;
        /// <summary>Repair / projected / unbuilt (weld priority), or grind.</summary>
        [ProtoMember(8)] public WorkCategory Category;
        /// <summary>Its blocks by name ("Light Armor Block:8, Window:2"), for the log.</summary>
        [ProtoMember(9)] public string Summary;
    }

    [ProtoContract]
    public struct BlockRefState
    {
        [ProtoMember(1)] public long Grid;
        [ProtoMember(2)] public int X;
        [ProtoMember(3)] public int Y;
        [ProtoMember(4)] public int Z;
    }
}
