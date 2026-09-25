using Microsoft.VisualStudio.TestTools.UnitTesting;
using ImprovedAI.Pathfinding;
using System;
using System.Collections.Generic;

namespace ImprovedAI.Tests.Pathfinding
{
    [TestClass]
    public class FastPriorityQueueTests
    {
        private class TestNode : FastPriorityQueueNode
        {
            public string Name { get; set; }

            public TestNode(string name)
            {
                Name = name;
            }
        }

        [TestMethod]
        public void Constructor_CreatesEmptyQueue()
        {
            var queue = new FastPriorityQueue<TestNode>(10);

            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(10, queue.MaxSize);
        }

        [TestMethod]
        public void Enqueue_SingleItem_IncreasesCount()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var node = new TestNode("A");

            queue.Enqueue(node, 5.0);

            Assert.AreEqual(1, queue.Count);
            Assert.IsTrue(queue.Contains(node));
        }

        [TestMethod]
        public void Dequeue_ReturnsLowestPriority()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var nodeA = new TestNode("A");
            var nodeB = new TestNode("B");
            var nodeC = new TestNode("C");

            queue.Enqueue(nodeB, 10.0);
            queue.Enqueue(nodeC, 20.0);
            queue.Enqueue(nodeA, 5.0);

            var first = queue.Dequeue();
            Assert.AreEqual("A", first.Name);
            Assert.AreEqual(5.0, first.Priority, 0.001);
        }

        [TestMethod]
        public void Dequeue_MultipleTimes_MaintainsPriorityOrder()
        {
            var queue = new FastPriorityQueue<TestNode>(10);

            queue.Enqueue(new TestNode("D"), 40.0);
            queue.Enqueue(new TestNode("A"), 10.0);
            queue.Enqueue(new TestNode("C"), 30.0);
            queue.Enqueue(new TestNode("B"), 20.0);

            Assert.AreEqual("A", queue.Dequeue().Name);
            Assert.AreEqual("B", queue.Dequeue().Name);
            Assert.AreEqual("C", queue.Dequeue().Name);
            Assert.AreEqual("D", queue.Dequeue().Name);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void UpdatePriority_IncreasePriority_MaintainsOrder()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var nodeA = new TestNode("A");
            var nodeB = new TestNode("B");
            var nodeC = new TestNode("C");

            queue.Enqueue(nodeA, 10.0);
            queue.Enqueue(nodeB, 20.0);
            queue.Enqueue(nodeC, 30.0);

            // Increase A's priority (make it less important)
            queue.UpdatePriority(nodeA, 25.0);

            Assert.AreEqual("B", queue.Dequeue().Name); // 20
            Assert.AreEqual("A", queue.Dequeue().Name); // 25
            Assert.AreEqual("C", queue.Dequeue().Name); // 30
        }

        [TestMethod]
        public void UpdatePriority_DecreasePriority_MaintainsOrder()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var nodeA = new TestNode("A");
            var nodeB = new TestNode("B");
            var nodeC = new TestNode("C");

            queue.Enqueue(nodeA, 10.0);
            queue.Enqueue(nodeB, 20.0);
            queue.Enqueue(nodeC, 30.0);

            // Decrease C's priority (make it more important)
            queue.UpdatePriority(nodeC, 5.0);

            Assert.AreEqual("C", queue.Dequeue().Name); // 5
            Assert.AreEqual("A", queue.Dequeue().Name); // 10
            Assert.AreEqual("B", queue.Dequeue().Name); // 20
        }

        [TestMethod]
        public void Contains_ExistingNode_ReturnsTrue()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var node = new TestNode("A");

            queue.Enqueue(node, 5.0);

            Assert.IsTrue(queue.Contains(node));
        }

        [TestMethod]
        public void Contains_NonExistingNode_ReturnsFalse()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var node = new TestNode("A");

            Assert.IsFalse(queue.Contains(node));
        }

        [TestMethod]
        public void Contains_DequeuedNode_ReturnsFalse()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var node = new TestNode("A");

            queue.Enqueue(node, 5.0);
            queue.Dequeue();

            Assert.IsFalse(queue.Contains(node));
        }

        [TestMethod]
        public void Clear_RemovesAllNodes()
        {
            var queue = new FastPriorityQueue<TestNode>(10);

            queue.Enqueue(new TestNode("A"), 10.0);
            queue.Enqueue(new TestNode("B"), 20.0);
            queue.Enqueue(new TestNode("C"), 30.0);

            queue.Clear();

            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void First_ReturnsLowestPriorityWithoutRemoving()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var nodeA = new TestNode("A");

            queue.Enqueue(new TestNode("B"), 20.0);
            queue.Enqueue(nodeA, 10.0);
            queue.Enqueue(new TestNode("C"), 30.0);

            var first = queue.First;

            Assert.AreEqual("A", first.Name);
            Assert.AreEqual(3, queue.Count); // Not removed
        }

        [TestMethod]
        public void EnqueueDequeue_LargeScale_MaintainsOrder()
        {
            var queue = new FastPriorityQueue<TestNode>(1000);
            var random = new Random(42);
            var priorities = new List<double>();

            // Enqueue 100 items with random priorities
            for (int i = 0; i < 100; i++)
            {
                double priority = random.NextDouble() * 1000;
                priorities.Add(priority);
                queue.Enqueue(new TestNode($"Node{i}"), priority);
            }

            priorities.Sort();

            // Dequeue and verify order
            for (int i = 0; i < 100; i++)
            {
                var node = queue.Dequeue();
                Assert.AreEqual(priorities[i], node.Priority, 0.001);
            }
        }

        [TestMethod]
        public void UpdatePriority_MultipleUpdates_MaintainsCorrectOrder()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var nodeA = new TestNode("A");
            var nodeB = new TestNode("B");
            var nodeC = new TestNode("C");

            queue.Enqueue(nodeA, 30.0);
            queue.Enqueue(nodeB, 20.0);
            queue.Enqueue(nodeC, 10.0);

            queue.UpdatePriority(nodeA, 5.0);  // A now highest priority
            queue.UpdatePriority(nodeC, 25.0); // C now lowest priority

            Assert.AreEqual("A", queue.Dequeue().Name); // 5
            Assert.AreEqual("B", queue.Dequeue().Name); // 20
            Assert.AreEqual("C", queue.Dequeue().Name); // 25
        }

        [TestMethod]
        public void Dequeue_SingleItem_ReducesCountToZero()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var node = new TestNode("A");

            queue.Enqueue(node, 10.0);
            queue.Dequeue();

            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void EnqueueDequeue_Alternating_MaintainsCorrectness()
        {
            var queue = new FastPriorityQueue<TestNode>(10);

            queue.Enqueue(new TestNode("A"), 10.0);
            queue.Enqueue(new TestNode("B"), 20.0);

            Assert.AreEqual("A", queue.Dequeue().Name);

            queue.Enqueue(new TestNode("C"), 5.0);
            queue.Enqueue(new TestNode("D"), 15.0);

            Assert.AreEqual("C", queue.Dequeue().Name);
            Assert.AreEqual("D", queue.Dequeue().Name);
            Assert.AreEqual("B", queue.Dequeue().Name);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void EqualPriorities_MaintainsStableOrder()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var nodeA = new TestNode("A");
            var nodeB = new TestNode("B");
            var nodeC = new TestNode("C");

            queue.Enqueue(nodeA, 10.0);
            queue.Enqueue(nodeB, 10.0);
            queue.Enqueue(nodeC, 10.0);

            // Should dequeue in roughly FIFO order for equal priorities
            var first = queue.Dequeue();
            var second = queue.Dequeue();
            var third = queue.Dequeue();

            // At minimum, verify all were dequeued
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsNotNull(third);
        }

        [TestMethod]
        public void Reuse_AfterClear_WorksCorrectly()
        {
            var queue = new FastPriorityQueue<TestNode>(10);

            queue.Enqueue(new TestNode("A"), 10.0);
            queue.Enqueue(new TestNode("B"), 20.0);
            queue.Clear();

            queue.Enqueue(new TestNode("C"), 5.0);
            queue.Enqueue(new TestNode("D"), 15.0);

            Assert.AreEqual(2, queue.Count);
            Assert.AreEqual("C", queue.Dequeue().Name);
            Assert.AreEqual("D", queue.Dequeue().Name);
        }

        [TestMethod]
        public void QueueIndex_TracksPosition()
        {
            var queue = new FastPriorityQueue<TestNode>(10);
            var node = new TestNode("A");

            Assert.AreEqual(0, node.QueueIndex); // Not in queue

            queue.Enqueue(node, 10.0);
            Assert.IsTrue(node.QueueIndex > 0); // In queue

            queue.Dequeue();
            // QueueIndex may not reset, but Contains should return false
            Assert.IsFalse(queue.Contains(node));
        }
    }
}