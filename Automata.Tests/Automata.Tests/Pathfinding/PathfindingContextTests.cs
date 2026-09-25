//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using ImprovedAI.Pathfinding;
//using Sandbox.ModAPI;
//using VRageMath;
//using ImprovedAI.Tests.TestUtil;
//using System.Collections.Generic;
//using System;


//namespace ImprovedAI.Tests.Pathfinding
//{
//    /// <summary>
//    /// Unit tests for PathfindingContext
//    /// These tests mock the SE API dependencies to verify context creation and properties
//    /// </summary>
//    [TestClass]
//    public class PathfindingContextTests
//    {
//        [TestMethod]
//        public void TestBasicContextCreation()
//        {
//            var controller = SEMockFactory.CreateMockController(new Vector3D(0, 0, 0));
//            var context = new PathfindingContext(
//                new FakePathfindingConfig(),
//                controller,
//                new List<IMySensorBlock>(),
//                new List<IMyCameraBlock>(),
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward,
//                new MockGamePruningStructureDelegate()
//            );

//            Assert.AreEqual(controller, context.Controller, "Controller not set");
//            Assert.AreEqual(1000f, context.ShipMass, "Mass not set correctly");
//            Assert.AreEqual(5000f, context.MaxLoad, "MaxLoad not set correctly");
//            Assert.AreEqual(50f, context.WaypointDistance, "WaypointDistance not set correctly");

//            Console.WriteLine("PASSED");
//        }

//        [TestMethod]
//        public void TestGravityDetection()
//        {
//            Console.Write("Test: Gravity Detection... ");
//            var position = new Vector3D(0, 0, 0);
//            var gravity = new Vector3D(0, -9.81f, 0);
//            var controller = SEMockFactory.CreateMockController(position, gravity);

//            var context = new PathfindingContext(
//                new FakePathfindingConfig(),
//                controller,
//                new List<IMySensorBlock>(),
//                new List<IMyCameraBlock>(),
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward,
//                planetCenter: new Vector3D(0, -6000, 0),
//                planetRadius: 60000.0
//            );

//            Assert.IsGreaterThan(0, context.GravityVector.LengthSquared(), "Gravity not detected");
//            Assert.IsTrue(context.IsInPlanetGravity(), "Should detect planet gravity");
//        }

//        [TestMethod]
//        public void TestThrustCapabilityCalculation()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var gravity = new Vector3D(0, -9.81f, 0);
//            var controller = SEMockFactory.CreateMockController(position, gravity);
//            var thrusters = new List<IMyThrust>
//                    {
//                        SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 100000f),
//                        SEMockFactory.CreateMockThruster(Base6Directions.Direction.Backward, 50000f),
//                        SEMockFactory.CreateMockThruster(Base6Directions.Direction.Up, 75000f)
//                    };

//            var context = new PathfindingContext(
//                new FakePathfindingConfig(),
//                controller,
//                new List<IMySensorBlock>(),
//                new List<IMyCameraBlock>(),
//                thrusters,
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward
//            );

//            Assert.IsNotNull(context.ThrustData, "ThrustData not created");
//        }

//        [TestMethod]
//        public void TestSensorConfiguration()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            var sensors = new List<IMySensorBlock>
//                    {
//                        SEMockFactory.CreateMockSensor(new Vector3D(0, 0, 0), 50f),
//                        SEMockFactory.CreateMockSensor(new Vector3D(10, 0, 0), 100f)
//                    };

//            var config = new FakePathfindingConfig { requireSensorsForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                sensors,
//                new List<IMyCameraBlock>(),
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward
//            );

//            Assert.IsNotNull(context.SensorInfos, "SensorInfos not created");
//            Assert.HasCount(2, context.SensorInfos, "Sensor count mismatch");
//        }

//        [TestMethod]
//        public void TestCameraConfiguration()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            var cameras = new List<IMyCameraBlock>
//                    {
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Backward),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                    };
//            var config = new FakePathfindingConfig { requireCamerasForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                new List<IMySensorBlock>(),
//                cameras,
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                controllerForwardDirection: Base6Directions.Direction.Forward
//            );

//            Assert.IsNotNull(context.CamerasByDirection, "CamerasByDirection not created");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Backward), "Direction.Backward");
//            Assert.HasCount(1, context.CamerasByDirection[Base6Directions.Direction.Backward], "Direction.Backward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Forward), "Direction.Forward");
//            Assert.HasCount(2, context.CamerasByDirection[Base6Directions.Direction.Forward], "Direction.Forward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Right), "Direction.Right");
//            Assert.HasCount(3, context.CamerasByDirection[Base6Directions.Direction.Right], "Direction.Right");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Left), "Direction.Left");
//            Assert.HasCount(4, context.CamerasByDirection[Base6Directions.Direction.Left], "Direction.Left");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Up), "Direction.Up");
//            Assert.HasCount(5, context.CamerasByDirection[Base6Directions.Direction.Up], "Direction.Up");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Down), "Direction.Down");
//            Assert.HasCount(6, context.CamerasByDirection[Base6Directions.Direction.Down], "Direction.Down");
//        }

//        public TestContext TestContext { get; set; }
//        [TestMethod]
//        public void TestCameraConfiguration_ControllerBackward()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            // Same camera setup as original test
//            var cameras = new List<IMyCameraBlock>
//                {
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Backward),    // 1
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),     // 2
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),       // 3
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),        // 4
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),          // 5
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),        // 6
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                    SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//                };
//            var config = new FakePathfindingConfig { requireCamerasForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                new List<IMySensorBlock>(),
//                cameras,
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                controllerForwardDirection: Base6Directions.Direction.Backward
//            );

//            Assert.IsNotNull(context.CamerasByDirection, "CamerasByDirection not created");
//            // When controller faces Backward: Forward↔Backward swap, Left↔Right swap
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Forward), "Direction.Forward");
//            Assert.HasCount(1, context.CamerasByDirection[Base6Directions.Direction.Forward], "Direction.Forward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Backward), "Direction.Backward");
//            Assert.HasCount(2, context.CamerasByDirection[Base6Directions.Direction.Backward], "Direction.Backward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Left), "Direction.Left");
//            Assert.HasCount(3, context.CamerasByDirection[Base6Directions.Direction.Left], "Direction.Left");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Right), "Direction.Right");
//            Assert.HasCount(4, context.CamerasByDirection[Base6Directions.Direction.Right], "Direction.Right");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Up), "Direction.Up");
//            Assert.HasCount(5, context.CamerasByDirection[Base6Directions.Direction.Up], "Direction.Up");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Down), "Direction.Down");
//            Assert.HasCount(6, context.CamerasByDirection[Base6Directions.Direction.Down], "Direction.Down");
//        }

//        [TestMethod]
//        public void TestCameraConfiguration_ControllerUp()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            var cameras = new List<IMyCameraBlock>
//    {
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Backward),    // 1
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),     // 2
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),       // 3
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),        // 4
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),          // 5
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),        // 6
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//    };
//            var config = new FakePathfindingConfig { requireCamerasForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                new List<IMySensorBlock>(),
//                cameras,
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                controllerForwardDirection: Base6Directions.Direction.Up
//            );

//            Assert.IsNotNull(context.CamerasByDirection, "CamerasByDirection not created");
//            // When controller faces Up: Down→Forward, Forward→Up, Up→Backward, Backward→Down
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Down), "Direction.Down");
//            Assert.HasCount(2, context.CamerasByDirection[Base6Directions.Direction.Down], "Direction.Down");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Forward), "Direction.Forward");
//            Assert.HasCount(5, context.CamerasByDirection[Base6Directions.Direction.Forward], "Direction.Forward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Right), "Direction.Right");
//            Assert.HasCount(3, context.CamerasByDirection[Base6Directions.Direction.Right], "Direction.Right");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Left), "Direction.Left");
//            Assert.HasCount(4, context.CamerasByDirection[Base6Directions.Direction.Left], "Direction.Left");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Backward), "Direction.Backward");
//            Assert.HasCount(6, context.CamerasByDirection[Base6Directions.Direction.Backward], "Direction.Backward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Up), "Direction.Up");
//            Assert.HasCount(1, context.CamerasByDirection[Base6Directions.Direction.Up], "Direction.Up");
//        }

//        [TestMethod]
//        public void TestCameraConfiguration_ControllerDown()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            var cameras = new List<IMyCameraBlock>
//    {
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Backward),    // 1
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),     // 2
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),       // 3
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),        // 4
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),          // 5
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),        // 6
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//    };
//            var config = new FakePathfindingConfig { requireCamerasForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                new List<IMySensorBlock>(),
//                cameras,
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                controllerForwardDirection: Base6Directions.Direction.Down
//            );

//            Assert.IsNotNull(context.CamerasByDirection, "CamerasByDirection not created");
//            // When controller faces Down: Up→Forward, Backward→Up, Down→Backward, Forward→Down
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Up), "Direction.Up");
//            Assert.HasCount(2, context.CamerasByDirection[Base6Directions.Direction.Up], "Direction.Up");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Backward), "Direction.Backward");
//            Assert.HasCount(5, context.CamerasByDirection[Base6Directions.Direction.Backward], "Direction.Backward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Right), "Direction.Right");
//            Assert.HasCount(3, context.CamerasByDirection[Base6Directions.Direction.Right], "Direction.Right");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Left), "Direction.Left");
//            Assert.HasCount(4, context.CamerasByDirection[Base6Directions.Direction.Left], "Direction.Left");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Forward), "Direction.Forward");
//            Assert.HasCount(6, context.CamerasByDirection[Base6Directions.Direction.Forward], "Direction.Forward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Down), "Direction.Down");
//            Assert.HasCount(1, context.CamerasByDirection[Base6Directions.Direction.Down], "Direction.Down");
//        }

//        [TestMethod]
//        public void TestCameraConfiguration_ControllerLeft()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            var cameras = new List<IMyCameraBlock>
//    {
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Backward),    // 1
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),     // 2
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),       // 3
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),        // 4
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),          // 5
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),        // 6
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//    };
//            var config = new FakePathfindingConfig { requireCamerasForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                new List<IMySensorBlock>(),
//                cameras,
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                controllerForwardDirection: Base6Directions.Direction.Left
//            );

//            Assert.IsNotNull(context.CamerasByDirection, "CamerasByDirection not created");
//            // When controller faces Left: Right→Forward, Forward→Left, Left→Backward, Backward→Right
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Right), "Direction.Right");
//            Assert.HasCount(2, context.CamerasByDirection[Base6Directions.Direction.Right], "Direction.Right");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Forward), "Direction.Forward");
//            Assert.HasCount(4, context.CamerasByDirection[Base6Directions.Direction.Forward], "Direction.Forward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Backward), "Direction.Backward");
//            Assert.HasCount(3, context.CamerasByDirection[Base6Directions.Direction.Backward], "Direction.Backward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Left), "Direction.Left");
//            Assert.HasCount(1, context.CamerasByDirection[Base6Directions.Direction.Left], "Direction.Left");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Up), "Direction.Up");
//            Assert.HasCount(5, context.CamerasByDirection[Base6Directions.Direction.Up], "Direction.Up");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Down), "Direction.Down");
//            Assert.HasCount(6, context.CamerasByDirection[Base6Directions.Direction.Down], "Direction.Down");
//        }

//        [TestMethod]
//        public void TestCameraConfiguration_ControllerRight()
//        {
//            var position = new Vector3D(0, 0, 0);
//            var controller = SEMockFactory.CreateMockController(position);
//            var cameras = new List<IMyCameraBlock>
//    {
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Backward),    // 1
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),     // 2
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Forward),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),       // 3
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Right),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),        // 4
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Left),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),          // 5
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Up),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),        // 6
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//        SEMockFactory.CreateMockCamera(Base6Directions.Direction.Down),
//    };
//            var config = new FakePathfindingConfig { requireCamerasForPathfinding = true };
//            var context = new PathfindingContext(
//                config,
//                controller,
//                new List<IMySensorBlock>(),
//                cameras,
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                controllerForwardDirection: Base6Directions.Direction.Right
//            );

//            Assert.IsNotNull(context.CamerasByDirection, "CamerasByDirection not created");
//            // When controller faces Right: Left→Forward, Backward→Left, Right→Backward, Forward→Right
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Left), "Direction.Left");
//            Assert.HasCount(2, context.CamerasByDirection[Base6Directions.Direction.Left], "Direction.Left");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Backward), "Direction.Backward");
//            Assert.HasCount(4, context.CamerasByDirection[Base6Directions.Direction.Backward], "Direction.Backward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Forward), "Direction.Forward");
//            Assert.HasCount(3, context.CamerasByDirection[Base6Directions.Direction.Forward], "Direction.Forward");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Right), "Direction.Right");
//            Assert.HasCount(1, context.CamerasByDirection[Base6Directions.Direction.Right], "Direction.Right");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Up), "Direction.Up");
//            Assert.HasCount(5, context.CamerasByDirection[Base6Directions.Direction.Up], "Direction.Up");
//            Assert.IsTrue(context.CamerasByDirection.ContainsKey(Base6Directions.Direction.Down), "Direction.Down");
//            Assert.HasCount(6, context.CamerasByDirection[Base6Directions.Direction.Down], "Direction.Down");
//        }

//        [TestMethod]
//        public void TestPlanetAltitudeCalculation()
//        {
//            var planetCenter = new Vector3D(0, 0, 0);
//            var planetRadius = 60000.0;

//            var position = new Vector3D(0, planetRadius + 1000, 0);
//            var gravity = new Vector3D(0, -9.81f, 0);
//            var controller = SEMockFactory.CreateMockController(position, gravity);

//            var context = new PathfindingContext(
//                new FakePathfindingConfig(),
//                controller,
//                new List<IMySensorBlock>(),
//                new List<IMyCameraBlock>(),
//                new List<IMyThrust>(),
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward,
//                new MockGamePruningStructureDelegate(),
//                planetCenter,
//                planetRadius
//            );

//            var altitude = context.GetSurfaceAltitude();
//            Assert.IsNotNull(altitude, "Altitude not calculated");
//            Assert.IsLessThan(1.0, Math.Abs(altitude.Value - 1000.0),
//                $"Altitude incorrect: expected ~1000, got {altitude.Value}");
//        }

//        //[TestMethod]
//        //public void TestClimbCapability()
//        //{
//        //        var position = new Vector3D(0, 0, 0);
//        //        var gravity = new Vector3D(0, -9.81f, 0);
//        //        var controller = SEMockFactory.CreateMockController(position, gravity);

//        //        // Create thrusters with upward thrust
//        //        var thrusters = new List<IMyThrust>
//        //            {
//        //                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Up, 200000f), // Strong upward
//        //                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Down, 50000f)
//        //            };

//        //        var context = new PathfindingContext(
//        //            new FakePathfindingConfig(),
//        //            controller,
//        //            new List<IMySensorBlock>(),
//        //            new List<IMyCameraBlock>(),
//        //            thrusters,
//        //            1000f,
//        //            5000f,
//        //            50f,
//        //            Base6Directions.Direction.Forward,
//        //            pruningStructureDelegate: null,
//        //            planetCenter: new Vector3D(0, -60000, 0),
//        //            planetRadius: 60000.0
//        //        );

//        //        // Test climbing upward
//        //        var upDirection = new Vector3D(0, 100, 0);
//        //        Assert.IsTrue(context.CanClimbInDirection(ref upDirection), "Should be able to climb with strong thrust");

//        //        // Test descending (should always work with gravity)
//        //        var downDirection = new Vector3D(0, -100, 0);
//        //        Assert.IsTrue(context.CanClimbInDirection(ref downDirection), "Should be able to descend");

//        //}

//        //    public void TestNullInputHandling()
//        //    {
//        //        Console.Write("Test: Null Input Handling... ");
//        //        try
//        //        {
//        //            var position = new Vector3D(0, 0, 0);
//        //            var controller = SEMockFactory.CreateMockController(position);

//        //            // Should handle null lists gracefully
//        //            var context = new PathfindingContext(
//        //                new FakePathfindingConfig(),
//        //                controller,
//        //                null,
//        //                null,
//        //                null,
//        //                1000f,
//        //                5000f,
//        //                50f,
//        //                Base6Directions.Direction.Forward
//        //            );

//        //            Assert(context.SensorInfos == null || context.SensorInfos.Count == 0,
//        //                "Should handle null sensors");
//        //            Assert(context.CamerasByDirection == null || context.CamerasByDirection.Count == 0,
//        //                "Should handle null cameras");

//        //            Console.WriteLine("PASSED");
//        //            passed++;
//        //        }
//        //        catch (Exception ex)
//        //        {
//        //            Console.WriteLine($"FAILED: {ex.Message}");
//        //            failed++;
//        //        }
//        //    }

//        //    public void TestZeroThrustScenario()
//        //    {
//        //        Console.Write("Test: Zero Thrust Scenario... ");
//        //        try
//        //        {
//        //            var position = new Vector3D(0, 0, 0);
//        //            var controller = SEMockFactory.CreateMockController(position);

//        //            var context = new PathfindingContext(
//        //                new FakePathfindingConfig(),
//        //                controller,
//        //                new List<IMySensorBlock>(),
//        //                new List<IMyCameraBlock>(),
//        //                new List<IMyThrust>(), // No thrusters
//        //                1000f,
//        //                5000f,
//        //                50f,
//        //                Base6Directions.Direction.Forward
//        //            );

//        //            // Should not crash, but climbing should fail
//        //            var upDirection = new Vector3D(0, 100, 0);
//        //            Assert(!context.CanClimbInDirection(upDirection),
//        //                "Should not be able to climb with no thrust");

//        //            Console.WriteLine("PASSED");
//        //            passed++;
//        //        }
//        //        catch (Exception ex)
//        //        {
//        //            Console.WriteLine($"FAILED: {ex.Message}");
//        //            failed++;
//        //        }
//        //    }

//        //    public void Assert(bool condition, string message)
//        //    {
//        //        if (!condition)
//        //            throw new Exception(message);
//        //    }
//        //}

//    }
//}