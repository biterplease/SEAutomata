using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI.Tests
{
    [TestClass]
    public class OrchestratorJobPrepareForSaveTests
    {
        [TestMethod]
        public void Job_PrepareForSave_Syncs_Nested_Task_Position_And_Orientation_Data()
        {
            Vector3D pos = new Vector3D(1, 2, 3);
            Quaternion orientation = Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f);
            Orchestrator.Job job = new Orchestrator.Job
            {
                Tasks = new List<Orchestrator.Task>
                {
                    new Orchestrator.Task
                    {
                        Position = pos,
                        Orientation = orientation,
                    }
                }
            };
            job.PrepareForSave();
            Assert.AreEqual(pos.X, job.Tasks[0].PositionData.X, 1e-9);
            Assert.AreEqual(pos.Y, job.Tasks[0].PositionData.Y, 1e-9);
            Assert.AreEqual(pos.Z, job.Tasks[0].PositionData.Z, 1e-9);
            Assert.AreEqual(orientation.X, job.Tasks[0].OrientationData.X, 1e-9);
            Assert.AreEqual(orientation.Y, job.Tasks[0].OrientationData.Y, 1e-9);
            Assert.AreEqual(orientation.Z, job.Tasks[0].OrientationData.Z, 1e-9);
            Assert.AreEqual(orientation.W, job.Tasks[0].OrientationData.W, 1e-9);
        }

        [TestMethod]
        public void Job_PrepareForSave_Skips_Null_Task_Entries()
        {
            Orchestrator.Job job = new Orchestrator.Job
            {
                Tasks = new List<Orchestrator.Task> { null }
            };
            job.PrepareForSave();
        }

        [TestMethod]
        public void Job_PrepareForSave_Null_Tasks_Is_Safe()
        {
            Orchestrator.Job job = new Orchestrator.Job { Tasks = null };
            job.PrepareForSave();
        }
    }
}
