using ImprovedAI.Tests.TestUtil;
using ImprovedAI.VirtualNetwork;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI.Tests.VirtualNetwork
{
    [TestClass]
    public class LogisticsPayloadSerializationTests
    {
        [TestMethod]
        public void LogisticsUpdate_RoundTrip_ProtoBuf()
        {
            LogisticsUpdate original = new LogisticsUpdate
            {
                EntityId = 404L,
                Timestamp = DateTime.UtcNow,
                OperationMode = LogisticsComputer.OperationMode.Request,
                Inventory = new Inventory(),
                Connectors = new List<Vector3D> { new Vector3D(10, 0, 0) }
            };
            ProtoBufMyUtilitiesDelegate util = new ProtoBufMyUtilitiesDelegate();
            byte[] raw = util.SerializeToBinary((IMessagePayload)original);
            LogisticsUpdate copy = util.SerializeFromBinary<LogisticsUpdate>(raw);
            Assert.AreEqual(original.EntityId, copy.EntityId);
            Assert.AreEqual(original.OperationMode, copy.OperationMode);
            Assert.AreEqual(1, copy.Connectors.Count);
        }

        [TestMethod]
        public void InventoryRequisition_RoundTrip_ProtoBuf()
        {
            InventoryRequisition original = new InventoryRequisition
            {
                Inventory = new Inventory(),
                ConnectorLocation = new Vector3D(5, 5, 5),
                RequisitionType = Inventory.RequisitionType.Push,
                IsStatic = true,
                RequestingEntityId = 909L,
                Timestamp = DateTime.UtcNow
            };
            ProtoBufMyUtilitiesDelegate util = new ProtoBufMyUtilitiesDelegate();
            byte[] raw = util.SerializeToBinary((IMessagePayload)original);
            InventoryRequisition copy = util.SerializeFromBinary<InventoryRequisition>(raw);
            Assert.AreEqual(original.RequestingEntityId, copy.RequestingEntityId);
            Assert.AreEqual(original.RequisitionType, copy.RequisitionType);
            Assert.IsTrue(copy.IsStatic);
        }
    }
}
