using ImprovedAI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImprovedAI.Tests
{
    [TestClass]
    public class InventoryContainsAtLeastTests
    {
        [TestMethod]
        public void ContainsAtLeast_EmptyRequired_ReturnsTrue()
        {
            Inventory available = new Inventory();
            available.AddItem("Iron", 10);
            Assert.IsTrue(available.ContainsAtLeast(null));
            Assert.IsTrue(available.ContainsAtLeast(new Inventory()));
        }

        [TestMethod]
        public void ContainsAtLeast_AllKeysSufficient_ReturnsTrue()
        {
            Inventory available = new Inventory();
            available.AddItem("Iron", 10);
            Inventory required = new Inventory();
            required.AddItem("Iron", 5);
            Assert.IsTrue(available.ContainsAtLeast(required));
        }

        [TestMethod]
        public void ContainsAtLeast_OneKeyInsufficient_ReturnsFalse()
        {
            Inventory available = new Inventory();
            available.AddItem("Iron", 2);
            Inventory required = new Inventory();
            required.AddItem("Iron", 5);
            Assert.IsFalse(available.ContainsAtLeast(required));
        }

        [TestMethod]
        public void ContainsAtLeast_MissingKey_ReturnsFalse()
        {
            Inventory available = new Inventory();
            available.AddItem("Iron", 99);
            Inventory required = new Inventory();
            required.AddItem("SteelPlate", 1);
            Assert.IsFalse(available.ContainsAtLeast(required));
        }
    }
}
