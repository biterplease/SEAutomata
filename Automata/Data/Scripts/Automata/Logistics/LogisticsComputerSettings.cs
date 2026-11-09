using System.Collections.Generic;

using ProtoBuf;

namespace Automata.Logistics
{
    /// <summary>
    /// Player settings of a Logistics Computer (terminal), saved in the block's mod storage and synced.
    /// </summary>
    /// <summary>
    /// A conveyor network's anchor: the lowest cell of its blocks on the computer's grid (else, OnSubgrid, the cell on that
    /// grid of one on another grid). Copied with the grid, unlike block ids: a pasted copy finds its networks' indexes.
    /// </summary>
    [ProtoContract]
    public class NetworkAnchor
    {
        [ProtoMember(1)] public int Index;
        [ProtoMember(2)] public Util.Vector3IData Cell;
        [ProtoMember(3)] public bool OnSubgrid;
    }

    /// <summary>A conveyor network's own Min / Max list ("item:min:max" lines, as settings.Quotas).</summary>
    [ProtoContract]
    public class NetworkQuotaList
    {
        [ProtoMember(1)] public int Index;
        [ProtoMember(2)] public List<string> Quotas = new List<string>();
    }

    [ProtoContract]
    public class LogisticsComputerSettings
    {
        [ProtoMember(1, IsRequired = true)] public bool Enabled = true;
        /// <summary>
        /// Provide for logistics / construction: bid with this grid's items (or room) on other blocks' jobs.
        /// Request: below a Min, ask for the difference. Push: above a Max, have the excess taken away.
        /// </summary>
        [ProtoMember(2, IsRequired = true)] public OperationMode WorkMode = OperationMode.ProvideForLogistics | OperationMode.ProvideForConstruction;
        /// <summary>How often the conveyor networks are mapped again even without block changes.</summary>
        [ProtoMember(3)] public int ConveyorNetworkUpdateIntervalSeconds = 60;
        /// <summary>
        /// Min / Max per item: "item:min:max" (item = inventory key: "SteelPlate", "Ingot/Iron", "Ore/Ice"...);
        /// 0 = not set. See <see cref="QuotaEntry"/>.
        /// </summary>
        [ProtoMember(4)] public List<string> Quotas = new List<string>();
        /// <summary>
        /// "Accept ores": bid room on mining jobs for ore (and a job's other ores) even with no refinery that takes it (plain
        /// storage), and whatever its Min / Max. Off: only where one takes it, within Max. (Mining jobs for this computer's
        /// own ore requests: always, see OreRequestsMine.)
        /// </summary>
        [ProtoMember(5)] public bool AcceptMinedOre;
        /// <summary>"Accept ice": as AcceptMinedOre, for ice with no O2/H2 generator or irrigation system.</summary>
        [ProtoMember(14)] public bool AcceptMinedIce;
        /// <summary>Saved since "Accept mined ores/ice" was split (else AcceptMinedIce is taken from AcceptMinedOre on load).</summary>
        [ProtoMember(15, IsRequired = true)] public bool AcceptSplit;
        /// <summary>LCDs whose name contains this show the computer's status.</summary>
        [ProtoMember(6)] public string LcdTag = "[LogisticsComputer]";
        /// <summary>Bumped by the terminal's "Retry held jobs" button (the server retries when it changes).</summary>
        [ProtoMember(7)] public int RetryRequest;
        // (ProtoMember 8 was UseBroadcastController: only the orchestrator writes to chat)
        /// <summary>The name its logs (and other blocks' logs) show; empty: the block's name.</summary>
        [ProtoMember(9)] public string Nickname = "";
        [ProtoMember(10)] public Util.LcdStyle Lcd = new Util.LcdStyle();
        /// <summary>Ores under their Min are mined for this computer by a Mining Surveyor in reach, not hauled (LogisticsComputerBlock.OreRequests).</summary>
        [ProtoMember(11)] public bool OreRequestsMine;
        /// <summary>Min / Max lists of conveyor networks other than the main one, by CN index (LogisticsComputerBlock.Networks).</summary>
        [ProtoMember(12)] public List<NetworkQuotaList> NetworkQuotas = new List<NetworkQuotaList>();
        /// <summary>Each conveyor network's anchor cell, by CN index (server-written; a pasted copy reads it).</summary>
        [ProtoMember(13)] public List<NetworkAnchor> NetworkAnchors = new List<NetworkAnchor>();
    }
}
