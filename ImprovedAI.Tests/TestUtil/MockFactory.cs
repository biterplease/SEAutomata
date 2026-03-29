using ImprovedAI.Pathfinding;
using ImprovedAI.VirtualNetwork;
using Moq;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRageMath;

namespace ImprovedAI.Tests.TestUtil
{
    public static class SEMockFactory
    {
        public static IMyUtilitiesDelegate CreateMockUtilities<T>(string returnValue, IMessagePayload returnObject) where T : class, IMessagePayload
        {
            var mock = new Mock<IMyUtilitiesDelegate>();
            mock.Setup(m => m.SerializeFromBinary<IMessagePayload>(It.IsAny<byte[]>()))
                .Returns<byte[]>(bytes => returnObject);
            return mock.Object;
        }
        public static IMyShipController CreateMockController(
             Vector3D position,
             Vector3 gravity = default(Vector3),
             bool isWorking = true,
             bool isFunctional = true,
             long entityId = 1,
             long gridEntityId = 1,
             float shipMass = 10000f)
        {
            var gridMock = CreateMockGrid(gridEntityId, shipMass);

            var controllerMock = new Mock<IMyShipController>();
            controllerMock.Setup(c => c.GetNaturalGravity()).Returns(gravity);
            controllerMock.Setup(c => c.CubeGrid).Returns(gridMock);

            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = position;
            controllerMock.Setup(c => c.WorldMatrix).Returns(worldMatrix);

            controllerMock.Setup(c => c.GetPosition()).Returns(position);
            controllerMock.Setup(c => c.EntityId).Returns(entityId);
            controllerMock.Setup(c => c.DisplayName).Returns("Test Controller");
            controllerMock.Setup(c => c.IsWorking).Returns(isWorking);
            controllerMock.Setup(c => c.IsFunctional).Returns(isFunctional);
            controllerMock.Setup(c => c.Orientation).Returns(
                new MyBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up));

            // Workaround for readonly struct fields - use reflection to create instance
            controllerMock.Setup(c => c.CalculateShipMass()).Returns(() =>
            {
                var massType = typeof(Sandbox.ModAPI.Ingame.MyShipMass);
                var mass = System.Activator.CreateInstance(massType);

                // Use reflection to set readonly fields
                var totalMassField = massType.GetField("TotalMass",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var baseMassField = massType.GetField("BaseMass",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var physicalMassField = massType.GetField("PhysicalMass",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                totalMassField?.SetValue(mass, shipMass);
                baseMassField?.SetValue(mass, shipMass);
                physicalMassField?.SetValue(mass, shipMass);

                return (Sandbox.ModAPI.Ingame.MyShipMass)mass;
            });

            return controllerMock.Object;
        }

        public static IMyThrust CreateMockThruster(
            Base6Directions.Direction direction,
            float maxThrust,
            bool isWorking = true,
            bool isFunctional = true,
            Vector3D? position = null,
            MatrixD? worldMatrix = null,
            long entityId = 1,
            long gridEntityId = 1)
        {
            var gridMock = CreateMockGrid(gridEntityId);

            var thrustMock = new Mock<IMyThrust>();
            thrustMock.Setup(t => t.Orientation).Returns(
                new MyBlockOrientation(direction, Base6Directions.Direction.Up));
            thrustMock.Setup(t => t.GridThrustDirection).Returns(Base6Directions.GetIntVector(direction));
            thrustMock.Setup(t => t.MaxEffectiveThrust).Returns(maxThrust);
            thrustMock.Setup(t => t.MaxThrust).Returns(maxThrust);
            thrustMock.Setup(t => t.CurrentThrust).Returns(0f);
            thrustMock.Setup(t => t.IsWorking).Returns(isWorking);
            thrustMock.Setup(t => t.IsFunctional).Returns(isFunctional);
            thrustMock.Setup(t => t.CubeGrid).Returns(gridMock);
            thrustMock.Setup(t => t.EntityId).Returns(entityId);
            thrustMock.Setup(t => t.WorldMatrix).Returns(worldMatrix ?? MatrixD.Identity);
            thrustMock.Setup(t => t.GetPosition()).Returns(position ?? Vector3D.Zero);

            return thrustMock.Object;
        }

        public static IMyGyro CreateMockGyro(
          Base6Directions.Direction forwardDirection,
          float forceMagnitude,
          bool isWorking = true,
          bool isFunctional = true,
          bool enabled = true,
          long entityId = 1,
          long gridEntityId = 1,
          MyCubeSize gridSize = MyCubeSize.Large)
        {
            var gridMock = CreateMockGrid(gridEntityId, gridSize: gridSize);

            var gyroMock = new Mock<IMyGyro>();
            gyroMock.Setup(g => g.Orientation).Returns(
                new MyBlockOrientation(forwardDirection, Base6Directions.Direction.Up));
            gyroMock.Setup(g => g.IsWorking).Returns(isWorking);
            gyroMock.Setup(g => g.IsFunctional).Returns(isFunctional);
            gyroMock.Setup(g => g.Enabled).Returns(enabled);
            gyroMock.Setup(g => g.CubeGrid).Returns(gridMock);
            gyroMock.Setup(g => g.EntityId).Returns(entityId);
            gyroMock.Setup(g => g.WorldMatrix).Returns(MatrixD.Identity);
            gyroMock.Setup(g => g.GetPosition()).Returns(Vector3D.Zero);

            // Setup gyro override properties
            bool gyroOverride = false;
            float pitch = 0f;
            float yaw = 0f;
            float roll = 0f;

            gyroMock.SetupProperty(g => g.GyroOverride, gyroOverride);
            gyroMock.SetupProperty(g => g.Pitch, pitch);
            gyroMock.SetupProperty(g => g.Yaw, yaw);
            gyroMock.SetupProperty(g => g.Roll, roll);

            // Create a real MyGyroDefinition instance and set ForceMagnitude via reflection
            // since it's a concrete class with non-virtual properties
            var gyroDefType = typeof(MyGyroDefinition);
            var gyroDef = System.Activator.CreateInstance(gyroDefType, true) as MyGyroDefinition;

            if (gyroDef != null)
            {
                // Use reflection to set the ForceMagnitude field/property
                var forceMagField = gyroDefType.GetProperty("ForceMagnitude",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (forceMagField != null && forceMagField.CanWrite)
                {
                    forceMagField.SetValue(gyroDef, forceMagnitude);
                }
                else
                {
                    // Try as a field if property doesn't work
                    var forceMagFieldAlt = gyroDefType.GetField("ForceMagnitude",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (forceMagFieldAlt != null)
                    {
                        forceMagFieldAlt.SetValue(gyroDef, forceMagnitude);
                    }
                }
            }

            var slimBlockMock = new Mock<IMySlimBlock>();
            slimBlockMock.Setup(s => s.BlockDefinition).Returns(gyroDef);
            gyroMock.Setup(g => g.SlimBlock).Returns(slimBlockMock.Object);

            return gyroMock.Object;
        }


        public static IMySensorBlock CreateMockSensor(
           Vector3D position,
           float range,
           bool isWorking = true,
           bool isFunctional = true,
           long entityId = 1,
           long gridEntityId = 1)
        {
            var gridMock = CreateMockGrid(gridEntityId);

            var sensorMock = new Mock<IMySensorBlock>();
            sensorMock.Setup(s => s.GetPosition()).Returns(position);
            sensorMock.Setup(s => s.MaxRange).Returns(range);
            sensorMock.Setup(s => s.IsWorking).Returns(isWorking);
            sensorMock.Setup(s => s.IsFunctional).Returns(isFunctional);
            sensorMock.Setup(s => s.CubeGrid).Returns(gridMock);
            sensorMock.Setup(s => s.EntityId).Returns(entityId);
            sensorMock.Setup(s => s.WorldMatrix).Returns(MatrixD.CreateTranslation(position));

            return sensorMock.Object;
        }

        public static IMyCameraBlock CreateMockCamera(
           Base6Directions.Direction direction,
           bool isWorking = true,
           bool isFunctional = true,
           long entityId = 1,
           long gridEntityId = 1)
        {
            var gridMock = CreateMockGrid(gridEntityId);

            var cameraMock = new Mock<IMyCameraBlock>();
            cameraMock.Setup(c => c.Orientation).Returns(
                new MyBlockOrientation(direction, Base6Directions.Direction.Up));
            cameraMock.Setup(c => c.IsWorking).Returns(isWorking);
            cameraMock.Setup(c => c.IsFunctional).Returns(isFunctional);
            cameraMock.Setup(c => c.CubeGrid).Returns(gridMock);
            cameraMock.Setup(c => c.EntityId).Returns(entityId);

            MatrixD worldMatrix = PathfindingUtil.GetNavigationRotationMatrix(direction);
            worldMatrix.Translation = Vector3D.Zero;

            cameraMock.Setup(c => c.WorldMatrix).Returns(worldMatrix);
            cameraMock.Setup(c => c.AvailableScanRange).Returns(2000.0);
            cameraMock.Setup(c => c.CanScan(It.IsAny<double>())).Returns<double>(d => d <= 2000.0);
            cameraMock.Setup(c => c.GetPosition()).Returns(Vector3D.Zero);

            return cameraMock.Object;
        }

        public static IMyCubeGrid CreateMockGrid(
                   long entityId = 1,
                   float mass = 1000f,
                   MyCubeSize gridSize = MyCubeSize.Large,
                   BoundingBox? localAABB = null)
        {
            var gridMock = new Mock<IMyCubeGrid>();
            gridMock.Setup(g => g.EntityId).Returns(entityId);
            gridMock.Setup(g => g.DisplayName).Returns($"Test Grid {entityId}");
            gridMock.Setup(g => g.GetPosition()).Returns(Vector3D.Zero);

            float gridSizeValue = gridSize == MyCubeSize.Large ? 2.5f : 0.5f;
            gridMock.Setup(g => g.GridSize).Returns(gridSizeValue);
            gridMock.Setup(g => g.GridSizeEnum).Returns(gridSize);
            gridMock.Setup(g => g.IsStatic).Returns(false);
            gridMock.Setup(g => g.WorldMatrix).Returns(MatrixD.Identity);

            // Setup bounding box
            var bbox = localAABB ?? new BoundingBox(new Vector3(-5, -5, -5), new Vector3(5, 5, 5));
            gridMock.Setup(g => g.LocalAABB).Returns(bbox);

            var physicsMock = new Mock<MyPhysicsComponentBase>();
            physicsMock.Setup(p => p.Mass).Returns(mass);
            gridMock.Setup(g => g.Physics).Returns(physicsMock.Object);

            return gridMock.Object;
        }

        public static List<IMyThrust> CreateStandardThrusterSet(
            float thrustPerDirection = 100000f,
            long baseEntityId = 100,
            long gridEntityId = 1)
        {
            var thrusters = new List<IMyThrust>();
            var directions = new[] {
                Base6Directions.Direction.Forward,
                Base6Directions.Direction.Backward,
                Base6Directions.Direction.Up,
                Base6Directions.Direction.Down,
                Base6Directions.Direction.Left,
                Base6Directions.Direction.Right
            };

            for (int i = 0; i < directions.Length; i++)
            {
                thrusters.Add(CreateMockThruster(
                    directions[i],
                    thrustPerDirection,
                    entityId: baseEntityId + i,
                    gridEntityId: gridEntityId
                ));
            }

            return thrusters;
        }
        public static List<IMyGyro> CreateStandardGyroSet(
           float forceMagnitude = 33600000f,
           long baseEntityId = 200,
           long gridEntityId = 1,
           MyCubeSize gridSize = MyCubeSize.Large)
        {
            var gyros = new List<IMyGyro>();
            var directions = new[] {
                Base6Directions.Direction.Forward,
                Base6Directions.Direction.Backward,
                Base6Directions.Direction.Up,
                Base6Directions.Direction.Down,
                Base6Directions.Direction.Left,
                Base6Directions.Direction.Right
            };

            for (int i = 0; i < directions.Length; i++)
            {
                gyros.Add(CreateMockGyro(
                    directions[i],
                    forceMagnitude,
                    entityId: baseEntityId + i,
                    gridEntityId: gridEntityId,
                    gridSize: gridSize
                ));
            }

            return gyros;
        }

        public static Dictionary<Base6Directions.Direction, List<IMyGyro>> CreateGyroscopeDictionary(
            List<IMyGyro> gyros)
        {
            var dict = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();

            foreach (Base6Directions.Direction dir in System.Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                dict[dir] = new List<IMyGyro>();
            }

            foreach (var gyro in gyros)
            {
                var direction = gyro.Orientation.Forward;
                dict[direction].Add(gyro);
            }

            return dict;
        }
    }
}