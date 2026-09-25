using ImprovedAI.VirtualNetwork;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using VRageMath;
using static ImprovedAI.Scheduler;

namespace ImprovedAI.Tests.VirtualNetwork
{
    [TestClass]
    public class TaskAssignmentMessageHandlingTests
    {
        [TestMethod]
        public void IsDirectMessageForDrone_Matches_Recipient()
        {
            Assert.IsTrue(TaskAssignmentMessageHandling.IsDirectMessageForDrone(100L, 100L));
            Assert.IsFalse(TaskAssignmentMessageHandling.IsDirectMessageForDrone(99L, 100L));
        }

        [TestMethod]
        public void EnqueueFromTaskAssignment_Enqueues_All_Tasks()
        {
            Queue<Task> q = new Queue<Task>();
            TaskAssignment ta = new TaskAssignment
            {
                Tasks = new List<Task>
                {
                    new Task { TaskId = 1, TaskType = TaskType.PreciseWelding, Position = Vector3D.Zero },
                    new Task { TaskId = 2, TaskType = TaskType.PreciseGrinding, Position = Vector3D.One }
                }
            };
            int n = TaskAssignmentMessageHandling.EnqueueFromTaskAssignment(ta, q);
            Assert.AreEqual(2, n);
            Assert.AreEqual(1u, q.Dequeue().TaskId);
            Assert.AreEqual(2u, q.Dequeue().TaskId);
        }

        [TestMethod]
        public void EnqueueFromTaskAssignment_Null_Yields_Zero()
        {
            Queue<Task> q = new Queue<Task>();
            Assert.AreEqual(0, TaskAssignmentMessageHandling.EnqueueFromTaskAssignment(null, q));
            Assert.AreEqual(0, q.Count);
        }
    }
}
