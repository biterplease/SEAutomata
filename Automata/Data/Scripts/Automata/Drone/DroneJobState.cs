using System.Collections.Generic;

using ProtoBuf;

using Automata.Construction;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// A drone controller's save struct (<see cref="Util.BlockSave"/>): terminal settings, ongoing work, operating state
    /// and its messages still in flight (outbox). Runtime parts are the server's only (null in a client's copy). (No
    /// inbox: awards and auctions are acted on when read.)
    /// </summary>
    [ProtoContract]
    public class DroneSaveData
    {
        [ProtoMember(1)] public DroneControllerSettings Settings;
        [ProtoMember(2)] public DroneJobState Job;
        [ProtoMember(3)] public DroneOperatingState Operating;
        [ProtoMember(4)] public List<MessageQueue.TimestampedMessage> Outbox;
        [ProtoMember(5)] public DroneCombatState Combat;
        /// <summary>The block that saved it (<see cref="Util.BlockSave.OwnState"/>).</summary>
        [ProtoMember(6)] public long EntityId;
    }

    /// <summary>
    /// The job it is working (task list and where it is in it) and its outstanding bid. After load the drone resumes at
    /// the start of the current task group (the fly-to of a block it was working on, the dock before a load / unload);
    /// its route there is planned afresh (measured 0.08 ms median, 0.8 ms p95: cheaper than restoring flight state).
    /// </summary>
    [ProtoContract]
    public class DroneJobState
    {
        [ProtoMember(1)] public Orchestrator.Job Job;
        [ProtoMember(2)] public int TaskIndex;
        [ProtoMember(3)] public byte Source;
        [ProtoMember(4)] public int BlocksDone;
        [ProtoMember(5)] public int FieldContinuations;
        [ProtoMember(6)] public uint PendingBidAuctionId;
        [ProtoMember(7)] public double PendingBidSecondsLeft;
        [ProtoMember(8)] public int BidCounter;
        [ProtoMember(9)] public int MessageCounter;
        /// <summary>The award behind Job (its outcome is reported to them).</summary>
        [ProtoMember(10)] public long Announcer;
        [ProtoMember(11)] public uint AuctionId;
        [ProtoMember(12)] public long Orchestrator;
        [ProtoMember(13)] public List<VirtualNetwork.JobStatusReport> UnsentReports = new List<VirtualNetwork.JobStatusReport>();
        [ProtoMember(14)] public double MinedOreKg;
        /// <summary>Orchestrator weld / grind job whose site other drones share (award flag): the shared-site watch.</summary>
        [ProtoMember(15)] public bool SharedSite;
    }

    /// <summary>What the drone learnt or was in the middle of, that can't be recomputed from the world after a load.</summary>
    [ProtoContract]
    public class DroneOperatingState
    {
        /// <summary>Steepest work attitude it managed (learnt).</summary>
        [ProtoMember(1)] public double MaxWorkTiltDegrees = 180;
        /// <summary>Thrusters per direction as built / last docked (damage detection).</summary>
        [ProtoMember(2)] public List<int> ThrusterBaseline = new List<int>();
        [ProtoMember(3)] public bool NeedsBatteryRecharge;
        [ProtoMember(4)] public bool NeedsHydrogenRefuel;
        /// <summary>Blocks found unreachable lately (kept out of plans until the time is up).</summary>
        [ProtoMember(5)] public List<UnreachableState> Unreachable = new List<UnreachableState>();
        /// <summary>Planner sweep frames of the grids it works on (rescans keep their direction).</summary>
        [ProtoMember(6)] public List<SweepFrameState> Frames = new List<SweepFrameState>();
        /// <summary>Ore scans not delivered to a mining surveyor yet.</summary>
        [ProtoMember(7)] public List<VirtualNetwork.SurveySweep> Surveys = new List<VirtualNetwork.SurveySweep>();
    }

    [ProtoContract]
    public class UnreachableState
    {
        [ProtoMember(1)] public long Grid;
        [ProtoMember(2)] public int X;
        [ProtoMember(3)] public int Y;
        [ProtoMember(4)] public int Z;
        [ProtoMember(5)] public int TicksLeft;
    }
}
