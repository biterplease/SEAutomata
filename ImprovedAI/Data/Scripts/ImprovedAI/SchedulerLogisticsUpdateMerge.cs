using ImprovedAI.VirtualNetwork;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI
{
    /// <summary>
    /// Pure merge helpers for scheduler consumption of logistics registration/update payloads (messaging-only).
    /// </summary>
    public static class SchedulerLogisticsUpdateMerge
    {
        public static LogisticsComputer ToLogisticsComputer(LogisticsUpdate update, long messageSenderId)
        {
            long entityId = update.EntityId != 0 ? update.EntityId : messageSenderId;
            List<Vector3D> connectors = update.Connectors;
            if (connectors == null)
            {
                connectors = new List<Vector3D>();
            }
            else
            {
                connectors = new List<Vector3D>(connectors);
            }

            return new LogisticsComputer
            {
                EntityId = entityId,
                _OperationMode = update.OperationMode,
                connectors = connectors,
                LastKnownInventory = update.Inventory ?? new Inventory()
            };
        }
    }
}
