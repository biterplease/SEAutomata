using System.Collections.Generic;

using ProtoBuf;

using Automata.Util;
using Automata.VirtualNetwork;

namespace Automata.Mining
{
    /// <summary>A mining surveyor's save struct (<see cref="BlockSave"/>): terminal settings and runtime state.</summary>
    [ProtoContract]
    public class MiningSurveyorSaveData
    {
        [ProtoMember(1)] public MiningSurveyorSettings Settings;
        /// <summary>Server only; null in a client's copy.</summary>
        [ProtoMember(2)] public MiningSurveyorState State;
        /// <summary>The block that saved it (<see cref="BlockSave.OwnState"/>).</summary>
        [ProtoMember(3)] public long EntityId;
    }

    /// <summary>
    /// Operating state: the deposits it knows and the ones found depleted lately. Ongoing work: what is left of each
    /// order, its MineOre jobs (published / on hold / awarded). (No inbox: survey
    /// data, awards, failures and drone reports are acted on when read.)
    /// </summary>
    [ProtoContract]
    public class MiningSurveyorState
    {
        [ProtoMember(1)] public int JobCounter;
        [ProtoMember(2)] public int MessageCounter;
        [ProtoMember(3)] public List<DepositState> Deposits = new List<DepositState>();
        [ProtoMember(4)] public List<DepositState> Depleted = new List<DepositState>();
        [ProtoMember(5)] public List<OrderProgressState> Orders = new List<OrderProgressState>();
        [ProtoMember(6)] public List<MiningJobState> Jobs = new List<MiningJobState>();
        // ProtoMember 7 was the messages in flight (saved by the message queue now)
        [ProtoMember(8)] public int Delivered;
        [ProtoMember(9)] public int Failed;
    }

    [ProtoContract]
    public class DepositState
    {
        [ProtoMember(1)] public string Ore;
        [ProtoMember(2)] public Vector3DData Position;
        [ProtoMember(3)] public long VoxelId;
        [ProtoMember(4)] public int Voxels;
        /// <summary>Seconds since it was last seen (deposits) / found depleted (Depleted).</summary>
        [ProtoMember(5)] public double AgeSeconds;
    }

    [ProtoContract]
    public class OrderProgressState
    {
        [ProtoMember(1)] public string Ore;
        /// <summary>The order's amount when it was counted from (an edit restarts the count).</summary>
        [ProtoMember(2)] public int OrderKg;
        [ProtoMember(3)] public double RemainingKg;
        /// <summary>kg the drones delivered for it (header "Active requests").</summary>
        [ProtoMember(4)] public double DeliveredKg;
    }

    [ProtoContract]
    public class MiningJobState
    {
        [ProtoMember(1)] public JobAnnouncement Announcement;
        [ProtoMember(2)] public string Ore;
        [ProtoMember(3)] public Vector3DData Deposit;
        [ProtoMember(4)] public double Kg;
        /// <summary>Ticks since an orchestrator awarded it; -1: not awarded.</summary>
        [ProtoMember(5, IsRequired = true)] public int AssignedAgeTicks = -1;
        [ProtoMember(6)] public int HoldTicksLeft;
        [ProtoMember(7)] public int DronesOut;
        [ProtoMember(8)] public bool Announced;
    }
}
