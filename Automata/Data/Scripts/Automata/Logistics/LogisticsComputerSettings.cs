using System.Collections.Generic;

using ProtoBuf;

namespace Automata.Logistics
{
    /// <summary>
    /// Player settings of a Logistics Computer (terminal), saved in the block's mod storage and synced.
    /// </summary>
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
        /// <summary>Bid room on mining jobs even with no refinery that takes the ore (plain ore storage).</summary>
        [ProtoMember(5)] public bool AcceptOreWithoutRefinery;
        /// <summary>LCDs whose name contains this show the computer's status.</summary>
        [ProtoMember(6)] public string LcdTag = "[LogisticsComputer]";
        /// <summary>Bumped by the terminal's "Retry held jobs" button (the server retries when it changes).</summary>
        [ProtoMember(7)] public int RetryRequest;
        // (ProtoMember 8 was UseBroadcastController: only the orchestrator writes to chat)
        /// <summary>The name its logs (and other blocks' logs) show; empty: the block's name.</summary>
        [ProtoMember(9)] public string Nickname = "";
        [ProtoMember(10)] public Util.LcdStyle Lcd = new Util.LcdStyle();
    }
}
