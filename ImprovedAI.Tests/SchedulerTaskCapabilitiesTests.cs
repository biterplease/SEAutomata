using ImprovedAI.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ImprovedAI.Drone;
using static ImprovedAI.Scheduler;

namespace ImprovedAI.Tests
{
    [TestClass]
    public class SchedulerTaskCapabilitiesTests
    {
        [TestMethod]
        public void IsLogisticsTaskType_FetchAndDelivery_True()
        {
            Assert.IsTrue(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.PreciseFetch));
            Assert.IsTrue(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.PreciseDelivery));
            Assert.IsTrue(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.ScanFetch));
            Assert.IsTrue(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.ScanDelivery));
            Assert.IsTrue(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.ActiveProvide));
        }

        [TestMethod]
        public void IsLogisticsTaskType_WeldAndGrind_False()
        {
            Assert.IsFalse(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.PreciseWelding));
            Assert.IsFalse(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.PreciseGrinding));
            Assert.IsFalse(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.ScanGrind));
            Assert.IsFalse(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.ScanWeld));
        }

        [TestMethod]
        public void IsLogisticsTaskType_None_False()
        {
            Assert.IsFalse(SchedulerTaskCapabilities.IsLogisticsTaskType(TaskType.None));
        }

        [TestMethod]
        public void RequiredCapabilitiesFor_Welding_IncludesCanWeld()
        {
            Capabilities c = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.PreciseWelding);
            Assert.AreEqual(Capabilities.CanWeld, c & Capabilities.CanWeld);
        }

        [TestMethod]
        public void RequiredCapabilitiesFor_ScanGrind_IncludesCanGrind()
        {
            Capabilities c = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.ScanGrind);
            Assert.AreEqual(Capabilities.CanGrind, c & Capabilities.CanGrind);
        }

        [TestMethod]
        public void RequiredCapabilitiesFor_Logistics_IncludesCargoContainers()
        {
            Capabilities c = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.PreciseFetch);
            Assert.AreEqual(Capabilities.HasCargoContainers, c & Capabilities.HasCargoContainers);
        }

        [TestMethod]
        public void RequiredCapabilitiesFor_DropAndAirdrop_IncludesSensors()
        {
            Capabilities drop = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.PreciseDrop);
            Assert.AreEqual(Capabilities.HasSensors, drop & Capabilities.HasSensors);

            Capabilities air = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.PreciseCargoAirdrop);
            Assert.AreEqual(Capabilities.CanAirDrop | Capabilities.HasSensors,
                air & (Capabilities.CanAirDrop | Capabilities.HasSensors));
        }

        [TestMethod]
        public void RequiredCapabilitiesFor_MessengerPigeon_IncludesSensors()
        {
            Capabilities c = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.MessengerPigeon);
            Assert.AreEqual(Capabilities.HasSensors, c & Capabilities.HasSensors);
        }

        [TestMethod]
        public void RequiredCapabilitiesFor_UnknownNonEmpty_DefaultsToCargoContainers()
        {
            Capabilities c = SchedulerTaskCapabilities.RequiredCapabilitiesFor(TaskType.BecomeStandAlone);
            Assert.AreEqual(Capabilities.HasCargoContainers, c);
        }
    }
}
