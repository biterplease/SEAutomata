using ImprovedAI.Config;
using ImprovedAI.Pathfinding;
using ImprovedAI.VirtualNetwork;
using Moq;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace ImprovedAI.Tests.TestUtil
{
    public static class TestHelpers
    {
        public static PathfindingContext CreateTestContext(Vector3 gravity, Vector3D position)
        {
            var config = new FakePathfindingConfig();

            var context = new PathfindingContext
            {
                MinWaypointDistance = config.MinWaypointDistance(),
                MaxWaypointDistance = config.MaxWaypointDistance(),
                MinAltitudeBuffer = config.MinAltitudeBuffer(),
                MaxPathNodes = config.MaxPathNodes(),
                MaxRepositionAttempts = config.MaxRepositionAttempts(),
                RequireSensorsForPathfinding = config.RequireSensorsForPathfinding(),
                RequireCamerasForPathfinding = config.RequireCamerasForPathfinding(),
                UsePlanetAwarePathfinding = config.UsePlanetAwarePathfinding(),
                AllowRepathing = config.AllowRepathing(),

                ControllerPosition = position,
                ControllerWorldMatrix = MatrixD.CreateTranslation(position),
                ControllerForwardDirection = Base6Directions.Direction.Forward,

                GravityVector = gravity,
                IsInPlanetGravity = gravity.LengthSquared() > 0.1,
                PlanetCenter = null,
                PlanetRadius = 0,

                ShipMass = 1000f,
                MaxLoad = 1.0f,
                WaypointDistance = config.MaxWaypointDistance(),

                Sensors = new List<PathfindingContext.SensorData>(),
                Cameras = new List<PathfindingContext.CameraData>(),
                TraveledNodes = new List<Vector3D>(),
                KnownObstacles = new List<PathfindingContext.ObstacleData>(),
                RaycastCache = new HashSet<Vector3D>(),
                PathBuffer = new List<Vector3D>(),
                NeighborBuffer = new List<Vector3I>()
            };

            context.ThrustData.Forward = 100000f;
            context.ThrustData.Backward = 100000f;
            context.ThrustData.Up = 100000f;
            context.ThrustData.Down = 100000f;
            context.ThrustData.Left = 100000f;
            context.ThrustData.Right = 100000f;

            return context;
        }

        public static void AddPlanetToContext(ref PathfindingContext context, Vector3D planetCenter, double radius)
        {
            context.PlanetCenter = planetCenter;
            context.PlanetRadius = radius;
            context.IsInPlanetGravity = true;
        }

  
        public static PathfindingManager CreateMockManager(
            IPathfindingConfig config = null,
            MockGamePruningStructureDelegate pruning = null,
            MockPlanetDelegate planet = null)
        {
            config = config ?? new FakePathfindingConfig();
            pruning = pruning ?? new MockGamePruningStructureDelegate();
            planet = planet ?? new MockPlanetDelegate();

            return new PathfindingManager(config, pruning, planet);
        }

        public static void SetupManagerComponents(
            PathfindingManager manager,
            IMyShipController controller,
            IMyCubeGrid grid = null,
            List<IMyThrust> thrusters = null,
            List<IMySensorBlock> sensors = null,
            List<IMyCameraBlock> cameras = null)
        {
            if (controller != null)
                manager.ControllerChanged(controller);

            if (grid != null)
                manager.GridChanged(grid);

            if (thrusters != null)
                manager.ThrustersChanged(thrusters);

            if (sensors != null)
                manager.SensorsChanged(sensors);

            if (cameras != null)
                manager.CamerasChanged(cameras);
        }
    }


    public class MockGamePruningStructureDelegate : IMyGamePruningStructureDelegate
    {
        private readonly List<MyEntity> lineIntersectionEntities;
        private readonly List<MyEntity> boxEntities;

        public MockGamePruningStructureDelegate()
        {
            lineIntersectionEntities = new List<MyEntity>();
            boxEntities = new List<MyEntity>();
        }

        public void AddLineIntersectionEntity(MyEntity entity)
        {
            lineIntersectionEntities.Add(entity);
        }

        public void AddBoxEntity(MyEntity entity)
        {
            boxEntities.Add(entity);
        }

        public void ClearLineIntersectionEntities()
        {
            lineIntersectionEntities.Clear();
        }

        public void ClearBoxEntities()
        {
            boxEntities.Clear();
        }

        public void ClearAll()
        {
            lineIntersectionEntities.Clear();
            boxEntities.Clear();
        }

        public void GetTopmostEntitiesOverlappingRay(
            ref LineD line,
            List<MyLineSegmentOverlapResult<MyEntity>> result,
            MyEntityQueryType queryType = MyEntityQueryType.Both)
        {
            result.Clear();

            foreach (var entity in lineIntersectionEntities)
            {
                result.Add(new MyLineSegmentOverlapResult<MyEntity>
                {
                    Element = entity,
                    Distance = Vector3D.Distance(line.From, entity.PositionComp.GetPosition())
                });
            }
        }

        public void GetTopMostEntitiesInBox(ref BoundingBoxD boundingBox, List<MyEntity> result, MyEntityQueryType queryType)
        {
            result.Clear();

            foreach (var entity in boxEntities)
            {
                var entityPos = entity.PositionComp.GetPosition();
                if (boundingBox.Contains(entityPos) != ContainmentType.Disjoint)
                {
                    result.Add(entity);
                }
            }
        }
    }

    public class MockPlanetDelegate : IMyPlanetDelegate
    {
        private MyPlanet mockPlanet;
        private Vector3D mockPlanetCenter;
        private double mockPlanetRadius;

        public MockPlanetDelegate()
        {
            mockPlanetCenter = Vector3D.Zero;
            mockPlanetRadius = 60000.0;
        }

        public void SetMockPlanet(Vector3D center, double radius)
        {
            mockPlanetCenter = center;
            mockPlanetRadius = radius;
            mockPlanet = null;
        }

        public void ClearMockPlanet()
        {
            mockPlanet = null;
        }

        public MyPlanet GetClosestPlanet(Vector3D position)
        {
            if (mockPlanetRadius > 0)
            {
                var distance = Vector3D.Distance(position, mockPlanetCenter);
                if (distance < mockPlanetRadius * 3)
                {
                    return mockPlanet;
                }
            }

            return null;
        }

        public double GetSurfaceAltitude(Vector3D position, MyPlanet planet)
        {
            if (mockPlanetRadius > 0)
            {
                var distanceFromCenter = Vector3D.Distance(position, mockPlanetCenter);
                return distanceFromCenter - mockPlanetRadius;
            }

            return double.MaxValue;
        }
    }

    public class MockMySessionDelegate : IMySessionDelegate
    {
        public int GameplayFrameCounter { get; set; }
    }

    public class MockMyUtilitiesDelegate : IMyUtilitiesDelegate
    {
        public string returnValue { get; set; }
        public object returnObject { get; set; }

        public byte[] SerializeToBinary<T>(T obj)
        {
            return Encoding.ASCII.GetBytes(returnValue ?? string.Empty);
        }

        public T SerializeFromBinary<T>(byte[] data)
        {
            return (T)returnObject;
        }

        public string SerializeToXML<T>(T objectToSerialize)
        {
            return returnValue ?? string.Empty;
        }

        public T SerializeFromXML<T>(string buffer)
        {
            return (T)returnObject;
        }
    }

}