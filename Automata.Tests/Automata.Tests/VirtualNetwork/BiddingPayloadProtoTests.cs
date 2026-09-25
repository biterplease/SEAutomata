using ImprovedAI;
using ImprovedAI.Tests.TestUtil;
using ImprovedAI.VirtualNetwork;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VRageMath;
using static ImprovedAI.Scheduler;

namespace ImprovedAI.Tests.VirtualNetwork
{
    [TestClass]
    public class BiddingPayloadProtoTests
    {
        [TestMethod]
        public void TaskAnnouncement_RoundTrip_IncludesSchedulerAndPayload()
        {
            Inventory req = new Inventory();
            req.AddItem("SteelPlate", 4);

            TaskAnnouncement original = new TaskAnnouncement
            {
                TaskId = 42u,
                Type = TaskType.PreciseFetch,
                Destination = new Vector3D(10, 20, 30),
                RequiredCapabilities = Drone.Capabilities.HasCargoContainers,
                SchedulerEntityId = 999L,
                RequiredPayload = req,
            };

            ProtoBufMyUtilitiesDelegate util = new ProtoBufMyUtilitiesDelegate();
            byte[] bytes = util.SerializeToBinary((IMessagePayload)original);
            TaskAnnouncement roundTrip = util.SerializeFromBinary<TaskAnnouncement>(bytes);

            Assert.AreEqual(42u, roundTrip.TaskId);
            Assert.AreEqual(TaskType.PreciseFetch, roundTrip.Type);
            Assert.AreEqual(999L, roundTrip.SchedulerEntityId);
            Assert.AreEqual(Drone.Capabilities.HasCargoContainers, roundTrip.RequiredCapabilities);
            Assert.AreEqual(10.0, roundTrip.Destination.X, 1e-6);
            Assert.IsNotNull(roundTrip.RequiredPayload);
            Assert.AreEqual(4, roundTrip.RequiredPayload.GetItemCount("SteelPlate"));
        }

        [TestMethod]
        public void TaskBid_RoundTrip_BidderKindAndCargo()
        {
            TaskBid original = new TaskBid
            {
                TaskId = 7u,
                EstimatedTime = 12.5f,
                PathComplexity = 0.33f,
                BidderKind = TaskBidderKind.LogisticsComputer,
                CargoAvailability = 0.8f,
            };

            ProtoBufMyUtilitiesDelegate util = new ProtoBufMyUtilitiesDelegate();
            byte[] bytes = util.SerializeToBinary((IMessagePayload)original);
            TaskBid roundTrip = util.SerializeFromBinary<TaskBid>(bytes);

            Assert.AreEqual(7u, roundTrip.TaskId);
            Assert.AreEqual(12.5f, roundTrip.EstimatedTime, 1e-4);
            Assert.AreEqual(0.33f, roundTrip.PathComplexity, 1e-4);
            Assert.AreEqual(TaskBidderKind.LogisticsComputer, roundTrip.BidderKind);
            Assert.AreEqual(0.8f, roundTrip.CargoAvailability, 1e-4);
        }

        [TestMethod]
        public void TaskFulfillmentLost_RoundTrip()
        {
            TaskFulfillmentLost original = new TaskFulfillmentLost
            {
                TaskId = 100u,
                Reason = "stockout",
            };

            ProtoBufMyUtilitiesDelegate util = new ProtoBufMyUtilitiesDelegate();
            byte[] bytes = util.SerializeToBinary((IMessagePayload)original);
            TaskFulfillmentLost roundTrip = util.SerializeFromBinary<TaskFulfillmentLost>(bytes);

            Assert.AreEqual(100u, roundTrip.TaskId);
            Assert.AreEqual("stockout", roundTrip.Reason);
        }
    }
}
