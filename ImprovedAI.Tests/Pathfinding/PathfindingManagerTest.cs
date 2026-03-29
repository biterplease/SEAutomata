using Microsoft.VisualStudio.TestTools.UnitTesting;
using ImprovedAI.Pathfinding;
using ImprovedAI.Tests.TestUtil;
using Sandbox.ModAPI;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI.Tests.Pathfinding
{
    [TestClass]
    public class PathfindingManagerTests
    {
        private FakePathfindingConfig config;
        private MockGamePruningStructureDelegate pruning;
        private MockPlanetDelegate planet;

        [TestInitialize]
        public void Setup()
        {
            config = new FakePathfindingConfig();
            pruning = new MockGamePruningStructureDelegate();
            planet = new MockPlanetDelegate();
        }

        [TestMethod]
        public void Constructor_InitializesCorrectly()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            
            Assert.IsNotNull(manager);
        }

        [TestMethod]
        public void ControllerChanged_UpdatesInternalState()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(new Vector3D(100, 200, 300));

            manager.ControllerChanged(controller);

            // Should not throw and should accept subsequent operations
            var grid = SEMockFactory.CreateMockGrid();
            manager.GridChanged(grid);
        }

        [TestMethod]
        public void ThrustersChanged_SingleThruster_UpdatesContext()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = new List<IMyThrust>
            {
                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 50000f, entityId: 1)
            };

            manager.ControllerChanged(controller);
            manager.ThrustersChanged(thrusters);

            // Verify by checking recalculation flag
            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void ThrustersChanged_MultipleTimes_DetectsChanges()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);

            manager.ControllerChanged(controller);

            // First update
            var thrusters1 = new List<IMyThrust>
            {
                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 50000f, entityId: 1)
            };
            manager.ThrustersChanged(thrusters1);

            // Clear flag by setting target and getting waypoint
            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);
            Vector3D waypoint;
            manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.IsFalse(manager.ShouldRecalculateWaypoint());

            // Second update with damaged thruster
            var thrusters2 = new List<IMyThrust>
            {
                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 50000f, isWorking: false, entityId: 1)
            };
            manager.ThrustersChanged(thrusters2);

            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void ThrustersChanged_NoStateChange_DoesNotSetFlag()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = new List<IMyThrust>
            {
                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 50000f, entityId: 1)
            };

            manager.ControllerChanged(controller);
            manager.ThrustersChanged(thrusters);

            // Set target and get waypoint to clear flag
            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);
            Vector3D waypoint;
            manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.IsFalse(manager.ShouldRecalculateWaypoint());

            // Update with same state
            manager.ThrustersChanged(thrusters);

            Assert.IsFalse(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void SensorsChanged_UpdatesInternalState()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var sensors = new List<IMySensorBlock>
            {
                SEMockFactory.CreateMockSensor(new Vector3D(10, 0, 0), 50f, entityId: 1)
            };

            manager.ControllerChanged(controller);
            manager.SensorsChanged(sensors);

            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void SensorsChanged_AddAndRemove_DetectsChanges()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);

            manager.ControllerChanged(controller);

            // Add sensor
            var sensors1 = new List<IMySensorBlock>
            {
                SEMockFactory.CreateMockSensor(Vector3D.Zero, 50f, entityId: 1)
            };
            manager.SensorsChanged(sensors1);

            // Clear flag
            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);
            Vector3D waypoint;
            manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);
            Assert.IsFalse(manager.ShouldRecalculateWaypoint());

            // Remove sensor
            manager.SensorsChanged(new List<IMySensorBlock>());

            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void CamerasChanged_UpdatesInternalState()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var cameras = new List<IMyCameraBlock>
            {
                SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward, entityId: 1)
            };

            manager.ControllerChanged(controller);
            manager.CamerasChanged(cameras);

            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void GridChanged_UpdatesMass()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var grid1 = SEMockFactory.CreateMockGrid(mass: 1000f);

            manager.ControllerChanged(controller);
            manager.GridChanged(grid1);

            // Mass change should trigger recalculation
            var grid2 = SEMockFactory.CreateMockGrid(mass: 2000f);
            manager.GridChanged(grid2);

            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void SetTarget_StoresTarget()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            
            manager.ControllerChanged(controller);

            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);

            // Should be able to get waypoint
            Vector3D waypoint;
            var result = manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.AreEqual(PathfindingResult.Success, result);
        }

        [TestMethod]
        public void GetNextWaypoint_WithoutTarget_Fails()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            
            manager.ControllerChanged(controller);

            Vector3D waypoint;
            var result = manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.AreEqual(PathfindingResult.Failed, result);
        }

        [TestMethod]
        public void GetNextWaypoint_WithTarget_Succeeds()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = SEMockFactory.CreateStandardThrusterSet();

            TestHelpers.SetupManagerComponents(manager, controller, thrusters: thrusters);

            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);

            Vector3D waypoint;
            var result = manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.AreEqual(PathfindingResult.Success, result);
            Assert.AreNotEqual(Vector3D.Zero, waypoint);
        }

        [TestMethod]
        public void GetNextWaypoint_ClearsRecalculationFlag()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = SEMockFactory.CreateStandardThrusterSet();

            TestHelpers.SetupManagerComponents(manager, controller, thrusters: thrusters);

            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);

            Assert.IsFalse(manager.ShouldRecalculateWaypoint());

            Vector3D waypoint;
            manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.IsFalse(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void IsPathClear_NoObstacles_ReturnsTrue()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            
            manager.ControllerChanged(controller);

            var start = new Vector3D(0, 0, 0);
            var end = new Vector3D(100, 0, 0);

            var result = manager.IsPathClear(ref start, ref end);

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void Close_CleansUpResources()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = SEMockFactory.CreateStandardThrusterSet();

            TestHelpers.SetupManagerComponents(manager, controller, thrusters: thrusters);

            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);

            manager.Close();

            // After close, operations should fail gracefully
            Vector3D waypoint;
            var result = manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.AreEqual(PathfindingResult.Failed, result);
        }

        [TestMethod]
        public void Close_MultipleCalls_SafelyHandled()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            
            manager.Close();
            manager.Close(); // Should not throw

            Assert.IsTrue(true); // If we get here, test passes
        }

        [TestMethod]
        public void ClearCache_RemovesCachedNodes()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = SEMockFactory.CreateStandardThrusterSet();

            TestHelpers.SetupManagerComponents(manager, controller, thrusters: thrusters);

            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);

            Vector3D waypoint;
            manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            manager.ClearCache();

            // After clear, should not have recalculation flag set
            Assert.IsFalse(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void GetPathComplexity_ReturnsValidValue()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var thrusters = SEMockFactory.CreateStandardThrusterSet();

            TestHelpers.SetupManagerComponents(manager, controller, thrusters: thrusters);

            var start = new Vector3D(0, 0, 0);
            var end = new Vector3D(1000, 0, 0);

            var complexity = manager.GetPathComplexity(ref start, ref end);

            Assert.IsTrue(complexity > 0);
        }

        [TestMethod]
        public void ComponentChanges_CachedByEntityId()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);

            manager.ControllerChanged(controller);

            // Add thruster with specific entityId
            var thrusters1 = new List<IMyThrust>
            {
                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 50000f, entityId: 100)
            };
            manager.ThrustersChanged(thrusters1);

            // Clear flag
            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);
            Vector3D waypoint;
            manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);
            Assert.IsFalse(manager.ShouldRecalculateWaypoint());

            // Update same thruster (same entityId) with different thrust
            var thrusters2 = new List<IMyThrust>
            {
                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 60000f, entityId: 100)
            };
            manager.ThrustersChanged(thrusters2);

            // Should detect change
            Assert.IsTrue(manager.ShouldRecalculateWaypoint());
        }

        [TestMethod]
        public void SetupManagerComponents_Helper_WorksCorrectly()
        {
            var manager = TestHelpers.CreateMockManager(config, pruning, planet);
            var controller = SEMockFactory.CreateMockController(Vector3D.Zero);
            var grid = SEMockFactory.CreateMockGrid();
            var thrusters = SEMockFactory.CreateStandardThrusterSet();
            var sensors = new List<IMySensorBlock>
            {
                SEMockFactory.CreateMockSensor(Vector3D.Zero, 50f, entityId: 1)
            };
            var cameras = new List<IMyCameraBlock>
            {
                SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward, entityId: 1)
            };

            TestHelpers.SetupManagerComponents(manager, controller, grid, thrusters, sensors, cameras);

            // All components should be set
            var target = new Vector3D(1000, 0, 0);
            manager.SetTarget(ref target);

            Vector3D waypoint;
            var result = manager.GetNextWaypoint(ref Vector3D.Zero, out waypoint);

            Assert.AreEqual(PathfindingResult.Success, result);
        }
    }
}