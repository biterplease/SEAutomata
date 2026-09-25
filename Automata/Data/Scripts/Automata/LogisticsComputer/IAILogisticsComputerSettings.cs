
namespace Automata.LogisticsComputer
{
    /// <summary>
    /// Logistics computer configurable settings.
    /// These should always be validated, and bounded by, the mod server settings.
    /// </summary>
    public class IAILogisticsComputerSettings
    {
        /// <summary>
        /// Provide, Request, Push, or all
        /// When Provide is checked, all items are passively provided to the network, and 
        /// other LogisticsComputers may request items from this inventory.
        /// When Request is checked, enables this LogisticsComputer to request items from the network.
        /// When Push is checked, allows this LogisticsComputer to actively provide items to the network.
        /// If only Push is checked, it has the ability to get rid of unwanted items while not sharing the rest
        /// of the inventory.
        /// </summary>
        public OperationMode OperationMode;
        public WorkMode WorkMode;
        public bool Enabled;
        /// <summary>
        /// Items exceeding these values will be automatically pushed to the Logistics Network.
        /// Requires OperationMode Push
        /// </summary>
        public Inventory Limits;
        /// <summary>
        /// Ignore items in this list.
        /// </summary>
        public Inventory IgnoreList;
        /// <summary>
        /// The Logistics Computer will make sure these amounts are in storage at all times.
        /// Anything missing will be requested periodically.
        /// Requires operation mode Requester
        /// </summary>
        public Inventory Requests;
        public float RequestPeriod;

        public int ConveyorNetworkUpdateIntervalSeconds;
    }
}
