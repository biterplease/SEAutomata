using ImprovedAI;
using ImprovedAI.VirtualNetwork;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI.Tests.VirtualNetwork
{
    [TestClass]
    public class SchedulerLogisticsUpdateMergeTests
    {
        [TestMethod]
        public void ToLogisticsComputer_Uses_EntityId_When_Set()
        {
            LogisticsUpdate update = new LogisticsUpdate
            {
                EntityId = 777L,
                OperationMode = LogisticsComputer.OperationMode.Push,
                Inventory = new Inventory(),
                Connectors = new List<Vector3D> { new Vector3D(1, 2, 3) },
                Timestamp = System.DateTime.UtcNow
            };
            LogisticsComputer lc = SchedulerLogisticsUpdateMerge.ToLogisticsComputer(update, 999L);
            Assert.AreEqual(777L, lc.EntityId);
            Assert.AreEqual(LogisticsComputer.OperationMode.Push, lc._OperationMode);
            Assert.AreEqual(1, lc.connectors.Count);
        }

        [TestMethod]
        public void ToLogisticsComputer_Falls_Back_To_Sender_When_EntityId_Zero()
        {
            LogisticsUpdate update = new LogisticsUpdate
            {
                EntityId = 0L,
                OperationMode = LogisticsComputer.OperationMode.ProvideForConstruction,
                Inventory = new Inventory(),
                Connectors = null,
                Timestamp = System.DateTime.UtcNow
            };
            LogisticsComputer lc = SchedulerLogisticsUpdateMerge.ToLogisticsComputer(update, 888L);
            Assert.AreEqual(888L, lc.EntityId);
            Assert.IsNotNull(lc.connectors);
            Assert.AreEqual(0, lc.connectors.Count);
        }
    }
}
