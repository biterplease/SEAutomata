//using ImprovedAI.Pathfinding;
//using ImprovedAI.Tests.TestUtil;
//using Sandbox.ModAPI;
//using VRageMath;
//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using System.Collections.Generic;


//namespace ImprovedAI.Tests.Pathfinding
//{
//    [TestClass]
//    public class DirectPathfinderTests
//    {
//        private FakePathfindingConfig _config;
//        private DirectPathfinder _pathfinder;

//        [TestInitialize]
//        public void Setup()
//        {
//            _config = new FakePathfindingConfig
//            {
//                allowDirectPathfinding = true,
//                allowRepathing = true,
//                requireSensorsForPathfinding = false,
//                requireCamerasForPathfinding = false,
//                usePlanetAwarePathfinding = true,
//                minWaypointDistance = 25.0f,
//                maxWaypointDistance = 100.0f,
//                minAltitudeBuffer = 50.0f,
//                maxRepositionAttempts = 3
//            };
//            _pathfinder = new DirectPathfinder(_config);
//        }

//        [TestMethod]
//        public void IsAvailable_WithValidConfig_ReturnsTrue()
//        {
//            var context = CreateBasicContext();
//            Assert.IsTrue(_pathfinder.IsAvailable(ref context));
//        }

//        [TestMethod]
//        public void EstimatedComplexity_ReturnsOne()
//        {
//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(100, 0, 0);
//            Assert.AreEqual(1, _pathfinder.EstimatedComplexity(ref start, ref end));
//        }

//        [TestMethod]
//        public void GetNextWaypoint_ShortDistance_ReturnsTarget()
//        {
//            var context = CreateBasicContext();
//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(20, 0, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            Assert.IsTrue(result);
//            Assert.AreEqual(end, waypoint);
//        }

//        [TestMethod]
//        public void GetNextWaypoint_LongDistance_ReturnsIntermediateWaypoint()
//        {
//            var context = CreateBasicContext();
//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(1000, 0, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            Assert.IsTrue(result);
//            double distanceToWaypoint = Vector3D.Distance(start, waypoint);
//            Assert.IsTrue(distanceToWaypoint <= context.WaypointDistance,
//                $"Waypoint distance {distanceToWaypoint} exceeds max {context.WaypointDistance}");
//            Assert.IsTrue(distanceToWaypoint > 0, "Waypoint should not be at start");
//        }

//        [TestMethod]
//        public void GetNextWaypoint_WithGravity_AppliesPlanetAwareCorrection()
//        {
//            var gravity = new Vector3(0, -9.81f, 0);
//            var context = CreateContextWithGravity(gravity);
//            var start = new Vector3D(0, 100, 0);
//            var end = new Vector3D(1000, 100, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            Assert.IsTrue(result);
//            Assert.IsTrue(waypoint.Y >= start.Y - 1.0, "Waypoint should maintain or gain altitude in gravity");
//        }

//        [TestMethod]
//        public void GetNextWaypoint_WithSensorObstacle_AttemptsRepositioning()
//        {
//            var context = CreateBasicContext();

//            // Add obstacle directly in path
//            context.SensorInfos = new List<PathfindingContext.SensorInfo>
//            {
//                new PathfindingContext.SensorInfo
//                {
//                    Sensor = SEMockFactory.CreateMockSensor(new Vector3D(50, 0, 0), 30f),
//                    Position = new Vector3D(50, 0, 0),
//                    MaxRange = 30f
//                }
//            };

//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(100, 0, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            // Should either find alternate waypoint or fail gracefully
//            Assert.IsTrue(result);
//            // Waypoint should be offset from direct path
//            double directPathDist = Vector3D.Distance(start, end);
//            double actualDist = Vector3D.Distance(start, waypoint) + Vector3D.Distance(waypoint, end);
//            Assert.IsTrue(actualDist >= directPathDist, "Repositioned path should be longer than direct");
//        }

//        [TestMethod]
//        public void GetNextWaypoint_RepathingDisabled_FailsWithObstacle()
//        {
//            _config.allowRepathing = false;
//            _pathfinder = new DirectPathfinder(_config);

//            var context = CreateBasicContext();
//            context.SensorInfos = new List<PathfindingContext.SensorInfo>
//            {
//                new PathfindingContext.SensorInfo
//                {
//                    Sensor = SEMockFactory.CreateMockSensor(new Vector3D(50, 0, 0), 50f),
//                    Position = new Vector3D(50, 0, 0),
//                    MaxRange = 50f
//                }
//            };

//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(100, 0, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            Assert.IsFalse(result, "Should fail when repathing is disabled and path is blocked");
//        }

//        [TestMethod]
//        public void CalculatePath_GeneratesCompletePathWithWaypoints()
//        {
//            var context = CreateBasicContext();
//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(500, 0, 0);
//            var pathOutput = new List<Vector3D>();

//            bool result = _pathfinder.CalculatePath(ref context, ref start, ref end, pathOutput);

//            Assert.IsTrue(result);
//            Assert.IsTrue(pathOutput.Count > 0);
//            Assert.AreEqual(start, pathOutput[0]);
//            Assert.AreEqual(end, pathOutput[pathOutput.Count - 1]);

//            // Verify waypoint spacing
//            for (int i = 1; i < pathOutput.Count; i++)
//            {
//                double distance = Vector3D.Distance(pathOutput[i - 1], pathOutput[i]);
//                Assert.IsTrue(distance <= context.WaypointDistance * 1.1, // Allow 10% tolerance
//                    $"Waypoint spacing {distance} exceeds maximum {context.WaypointDistance}");
//            }
//        }

//        [TestMethod]
//        public void GetNextWaypoint_BelowMinAltitude_LiftsWaypoint()
//        {
//            var gravity = new Vector3(0, -9.81f, 0);
//            var context = CreateContextWithGravity(gravity);

//            var start = new Vector3D(0, 30, 0); // Below min altitude buffer
//            var end = new Vector3D(100, 30, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            Assert.IsTrue(result);
//            var altitude = context.GetSurfaceAltitude();
//            if (altitude.HasValue)
//            {
//                Assert.IsTrue(waypoint.Y > start.Y, "Waypoint should be lifted when below safe altitude");
//            }
//        }

//        [TestMethod]
//        public void GetNextWaypoint_MultipleRepositionAttempts_FindsClearPath()
//        {
//            _config.maxRepositionAttempts = 10;
//            _pathfinder = new DirectPathfinder(_config);

//            var context = CreateBasicContext();

//            // Add multiple obstacles
//            context.SensorInfos = new List<PathfindingContext.SensorInfo>
//            {
//                new PathfindingContext.SensorInfo
//                {
//                    Sensor = SEMockFactory.CreateMockSensor(new Vector3D(30, 0, 0), 20f),
//                    Position = new Vector3D(30, 0, 0),
//                    MaxRange = 20f
//                },
//                new PathfindingContext.SensorInfo
//                {
//                    Sensor = SEMockFactory.CreateMockSensor(new Vector3D(30, 30, 0), 20f),
//                    Position = new Vector3D(30, 30, 0),
//                    MaxRange = 20f
//                }
//            };

//            var start = new Vector3D(0, 0, 0);
//            var end = new Vector3D(100, 0, 0);

//            Vector3D waypoint;
//            bool result = _pathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint);

//            // With enough attempts, should find a way around
//            Assert.IsTrue(result, "Should find path with sufficient reposition attempts");
//        }

//        #region Helper Methods

//        private PathfindingContext CreateBasicContext()
//        {
//            var controller = SEMockFactory.CreateMockController(new Vector3D(0, 0, 0));
//            var thrusters = CreateBasicThrusters();

//            return new PathfindingContext(
//                _config,
//                controller,
//                new List<IMySensorBlock>(),
//                new List<IMyCameraBlock>(),
//                thrusters,
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward,
//                new MockGamePruningStructureDelegate()
//            );
//        }

//        private PathfindingContext CreateContextWithGravity(Vector3 gravity)
//        {
//            var controller = SEMockFactory.CreateMockController(new Vector3D(0, 0, 0), gravity);
//            var thrusters = CreateBasicThrusters();

//            return new PathfindingContext(
//                _config,
//                controller,
//                new List<IMySensorBlock>(),
//                new List<IMyCameraBlock>(),
//                thrusters,
//                1000f,
//                5000f,
//                50f,
//                Base6Directions.Direction.Forward,
//                new MockGamePruningStructureDelegate(),
//                planetCenter: new Vector3D(0, -60000, 0),
//                planetRadius: 60000.0
//            );
//        }

//        private List<IMyThrust> CreateBasicThrusters()
//        {
//            return new List<IMyThrust>
//            {
//                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Forward, 100000f),
//                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Backward, 100000f),
//                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Up, 100000f),
//                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Down, 100000f),
//                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Left, 100000f),
//                SEMockFactory.CreateMockThruster(Base6Directions.Direction.Right, 100000f)
//            };
//        }

//        #endregion
//    }
//}