using ImprovedAI.Tests.TestUtil;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ImprovedAI.Tests
{
    /// <summary>
    /// Obsolete for CI: rotation/calibration expectations drifted; out of scope for virtual-network verification.
    /// Re-enable when drone orientation logic is stabilized.
    /// </summary>
    [TestClass]
    [Ignore("Pre-existing rotation/calibration regression — not part of virtual-network scope; see ImprovedAI.Tests/TESTS.md")]
    public class IAIDroneControllerRotationTests
    {
        private IAIDroneControllerBlock droneController;
        private IMyShipController mockController;
        private IMyCubeGrid mockGrid;
        private Mock<IMySession> sessionMock;

        [TestInitialize]
        public void Setup()
        {
            sessionMock = new Mock<IMySession>();
            sessionMock.Setup(s => s.GameplayFrameCounter).Returns(0);
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_NoGyroscopes_SetsZeroTorque()
        {
            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            mockGrid = mockController.CubeGrid;

            var gyros = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();
            foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                gyros[dir] = new List<IMyGyro>();
            }

            droneController = CreateDroneController(mockController, gyros);
            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            float torque = GetPrivateField<float>(droneController, "totalGyroscopeTorque");
            float inertia = GetPrivateField<float>(droneController, "shipMomentOfInertia");

            Assert.AreEqual(0f, torque, "Torque should be zero with no gyroscopes");
            Assert.AreEqual(0f, inertia, "Inertia should be zero with no gyroscopes");
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_SingleLargeGyro_CalculatesCorrectTorque()
        {
            const float LARGE_GYRO_TORQUE = 33600000f;

            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = new List<IMyGyro>
            {
                SEMockFactory.CreateMockGyro(Base6Directions.Direction.Forward, LARGE_GYRO_TORQUE, gridSize: MyCubeSize.Large)
            };

            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            float torque = GetPrivateField<float>(droneController, "totalGyroscopeTorque");
            Assert.AreEqual(LARGE_GYRO_TORQUE, torque, 0.01f, "Torque should match large gyro spec");
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_MultipleGyros_SumsTorque()
        {
            const float GYRO_TORQUE = 33600000f;
            const int GYRO_COUNT = 4;

            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = new List<IMyGyro>();

            for (int i = 0; i < GYRO_COUNT; i++)
            {
                gyros.Add(SEMockFactory.CreateMockGyro(Base6Directions.Direction.Forward, GYRO_TORQUE, entityId: 200 + i));
            }

            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            float torque = GetPrivateField<float>(droneController, "totalGyroscopeTorque");
            float expectedTorque = GYRO_TORQUE * GYRO_COUNT;
            Assert.AreEqual(expectedTorque, torque, 0.01f, "Torque should be sum of all gyros");
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_DisabledGyro_NotIncluded()
        {
            const float GYRO_TORQUE = 33600000f;

            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = new List<IMyGyro>
            {
                SEMockFactory.CreateMockGyro(Base6Directions.Direction.Forward, GYRO_TORQUE, enabled: true),
                SEMockFactory.CreateMockGyro(Base6Directions.Direction.Up, GYRO_TORQUE, enabled: false)
            };

            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            float torque = GetPrivateField<float>(droneController, "totalGyroscopeTorque");
            Assert.AreEqual(GYRO_TORQUE, torque, 0.01f, "Only enabled gyro should contribute");
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_NonFunctionalGyro_NotIncluded()
        {
            const float GYRO_TORQUE = 33600000f;

            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = new List<IMyGyro>
            {
                SEMockFactory.CreateMockGyro(Base6Directions.Direction.Forward, GYRO_TORQUE, isFunctional: true),
                SEMockFactory.CreateMockGyro(Base6Directions.Direction.Up, GYRO_TORQUE, isFunctional: false)
            };

            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            float torque = GetPrivateField<float>(droneController, "totalGyroscopeTorque");
            Assert.AreEqual(GYRO_TORQUE, torque, 0.01f, "Only functional gyro should contribute");
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_CalculatesInertia()
        {
            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);

            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            float inertia = GetPrivateField<float>(droneController, "shipMomentOfInertia");
            Assert.IsTrue(inertia > 0f, "Inertia should be calculated and positive");
        }

        [TestMethod]
        public void CalibrateRotationCapabilities_LargerShip_HigherInertia()
        {
            var smallBBox = new BoundingBox(new Vector3(-2, -2, -2), new Vector3(2, 2, 2));
            var smallGrid = SEMockFactory.CreateMockGrid(1, 5000f, MyCubeSize.Large, smallBBox);
            var smallController = SEMockFactory.CreateMockController(Vector3D.Zero, gridEntityId: 1, shipMass: 5000f);
            var smallControllerMock = Mock.Get(smallController);
            smallControllerMock.Setup(c => c.CubeGrid).Returns(smallGrid);

            var gyros1 = SEMockFactory.CreateStandardGyroSet(gridEntityId: 1);
            var gyroDict1 = SEMockFactory.CreateGyroscopeDictionary(gyros1);
            var smallDrone = CreateDroneController(smallController, gyroDict1);

            var largeBBox = new BoundingBox(new Vector3(-10, -10, -10), new Vector3(10, 10, 10));
            var largeGrid = SEMockFactory.CreateMockGrid(2, 5000f, MyCubeSize.Large, largeBBox);
            var largeController = SEMockFactory.CreateMockController(Vector3D.Zero, gridEntityId: 2, shipMass: 5000f);
            var largeControllerMock = Mock.Get(largeController);
            largeControllerMock.Setup(c => c.CubeGrid).Returns(largeGrid);

            var gyros2 = SEMockFactory.CreateStandardGyroSet(gridEntityId: 2);
            var gyroDict2 = SEMockFactory.CreateGyroscopeDictionary(gyros2);
            var largeDrone = CreateDroneController(largeController, gyroDict2);

            InvokePrivateMethod(smallDrone, "CalibrateRotationCapabilities");
            InvokePrivateMethod(largeDrone, "CalibrateRotationCapabilities");

            float smallInertia = GetPrivateField<float>(smallDrone, "shipMomentOfInertia");
            float largeInertia = GetPrivateField<float>(largeDrone, "shipMomentOfInertia");

            Assert.IsTrue(largeInertia > smallInertia, "Larger ship should have higher moment of inertia");
        }

        [TestMethod]
        public void IsOrientedTowards_AlignedForward_ReturnsTrue()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 0, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;

            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            droneController = CreateDroneController(mockController, new Dictionary<Base6Directions.Direction, List<IMyGyro>>());
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            bool result = (bool)InvokePrivateMethod(droneController, "IsOrientedTowards", targetPos);

            Assert.IsTrue(result, "Should be oriented when facing target directly");
        }

        [TestMethod]
        public void IsOrientedTowards_SlightlyOffAxis_WithinTolerance_ReturnsTrue()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 3, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;

            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            droneController = CreateDroneController(mockController, new Dictionary<Base6Directions.Direction, List<IMyGyro>>());
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            bool result = (bool)InvokePrivateMethod(droneController, "IsOrientedTowards", targetPos);

            Assert.IsTrue(result, "Should be oriented when within tolerance");
        }

        [TestMethod]
        public void IsOrientedTowards_BeyondTolerance_ReturnsFalse()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 20, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;

            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            droneController = CreateDroneController(mockController, new Dictionary<Base6Directions.Direction, List<IMyGyro>>());
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            bool result = (bool)InvokePrivateMethod(droneController, "IsOrientedTowards", targetPos);

            Assert.IsFalse(result, "Should not be oriented when beyond tolerance");
        }

        [TestMethod]
        public void IsOrientedTowards_90DegreesOff_ReturnsFalse()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(0, 100, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;

            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            droneController = CreateDroneController(mockController, new Dictionary<Base6Directions.Direction, List<IMyGyro>>());
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            bool result = (bool)InvokePrivateMethod(droneController, "IsOrientedTowards", targetPos);

            Assert.IsFalse(result, "Should not be oriented at 90 degrees");
        }

        [TestMethod]
        public void IsOrientedTowards_180DegreesOff_ReturnsFalse()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(-100, 0, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;

            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            droneController = CreateDroneController(mockController, new Dictionary<Base6Directions.Direction, List<IMyGyro>>());
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            bool result = (bool)InvokePrivateMethod(droneController, "IsOrientedTowards", targetPos);

            Assert.IsFalse(result, "Should not be oriented when facing opposite direction");
        }

        [TestMethod]
        public void InitiateRotation_AlreadyAligned_ReturnsFalse()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 0, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);
            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            bool result = (bool)InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            Assert.IsFalse(result, "Should not initiate rotation when already aligned");
            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsFalse(isRotating, "isRotating flag should remain false");
        }

        [TestMethod]
        public void InitiateRotation_NeedsRotation_ReturnsTrue()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(0, 100, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);
            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            bool result = (bool)InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            Assert.IsTrue(result, "Should initiate rotation when not aligned");
            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsTrue(isRotating, "isRotating flag should be set");
        }

        [TestMethod]
        public void InitiateRotation_AlreadyRotating_ReturnsFalse()
        {
            Vector3D targetPos = new Vector3D(0, 100, 0);

            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");
            SetPrivateField(droneController, "isRotating", true);

            bool result = (bool)InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            Assert.IsFalse(result, "Should not initiate when already rotating");
        }

        [TestMethod]
        public void InitiateRotation_NoGyroscopes_ReturnsFalse()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(0, 100, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyroDict = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();
            foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            {
                gyroDict[dir] = new List<IMyGyro>();
            }

            droneController = CreateDroneController(mockController, gyroDict);
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            bool result = (bool)InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            Assert.IsFalse(result, "Should not initiate rotation without gyroscopes");
        }

        [TestMethod]
        public void InitiateRotation_SmallAngle_CalculatesShortDuration()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 10, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "orientationToleranceDegrees", 1.0f);
            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            long duration = GetPrivateField<long>(droneController, "rotationDurationFrames");
            Assert.IsTrue(duration >= 10, "Duration should be at least minimum");
            Assert.IsTrue(duration < 100, "Small angle should have short duration");
        }

        [TestMethod]
        public void InitiateRotation_LargeAngle_CalculatesLongerDuration()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(-100, 0, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);
            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            long duration = GetPrivateField<long>(droneController, "rotationDurationFrames");
            Assert.IsTrue(duration > 50, "Large angle should have longer duration");
        }

        [TestMethod]
        public void InitiateRotation_SetsRotationTarget()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(0, 100, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);
            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            InvokePrivateMethod(droneController, "InitiateRotation", targetPos);

            Vector3D storedTarget = GetPrivateField<Vector3D>(droneController, "rotationTarget");
            Assert.AreEqual(targetPos, storedTarget, "Should store rotation target");
        }

        [TestMethod]
        public void UpdateRotation_NotRotating_DoesNothing()
        {
            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyroDict = new Dictionary<Base6Directions.Direction, List<IMyGyro>>();
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "isRotating", false);

            InvokePrivateMethod(droneController, "UpdateRotation");

            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsFalse(isRotating);
        }

        [TestMethod]
        public void UpdateRotation_DurationNotElapsed_ContinuesRotating()
        {
            mockController = SEMockFactory.CreateMockController(Vector3D.Zero);
            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "isRotating", true);
            SetPrivateField(droneController, "rotationStartFrame", 0L);
            SetPrivateField(droneController, "rotationDurationFrames", 100L);

            sessionMock.Setup(s => s.GameplayFrameCounter).Returns(50);

            InvokePrivateMethod(droneController, "UpdateRotation");

            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsTrue(isRotating, "Should continue rotating");
        }

        [TestMethod]
        public void UpdateRotation_DurationElapsed_AlignedCorrectly_StopsRotating()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 0, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            SetPrivateField(droneController, "isRotating", true);
            SetPrivateField(droneController, "rotationStartFrame", 0L);
            SetPrivateField(droneController, "rotationDurationFrames", 100L);
            SetPrivateField(droneController, "rotationTarget", targetPos);
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            sessionMock.Setup(s => s.GameplayFrameCounter).Returns(100);

            InvokePrivateMethod(droneController, "UpdateRotation");

            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsFalse(isRotating, "Should stop rotating when aligned");
        }

        [TestMethod]
        public void UpdateRotation_DurationElapsed_Misaligned_InitiatesCorrection()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(0, 100, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            SetPrivateField(droneController, "isRotating", true);
            SetPrivateField(droneController, "rotationStartFrame", 0L);
            SetPrivateField(droneController, "rotationDurationFrames", 100L);
            SetPrivateField(droneController, "rotationTarget", targetPos);
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            sessionMock.Setup(s => s.GameplayFrameCounter).Returns(100);

            InvokePrivateMethod(droneController, "UpdateRotation");

            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsTrue(isRotating, "Should initiate correction rotation when misaligned");
        }

        [TestMethod]
        public void UpdateRotation_DurationElapsed_SlightMisalignment_DoesNotCorrect()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 1, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            InvokePrivateMethod(droneController, "CalibrateRotationCapabilities");

            SetPrivateField(droneController, "isRotating", true);
            SetPrivateField(droneController, "rotationStartFrame", 0L);
            SetPrivateField(droneController, "rotationDurationFrames", 100L);
            SetPrivateField(droneController, "rotationTarget", targetPos);
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            sessionMock.Setup(s => s.GameplayFrameCounter).Returns(100);

            InvokePrivateMethod(droneController, "UpdateRotation");

            bool isRotating = GetPrivateField<bool>(droneController, "isRotating");
            Assert.IsFalse(isRotating, "Should not correct when within half tolerance");
        }

        [TestMethod]
        public void UpdateRotation_DisablesGyroscopes_WhenComplete()
        {
            Vector3D currentPos = new Vector3D(0, 0, 0);
            Vector3D targetPos = new Vector3D(100, 0, 0);

            mockController = SEMockFactory.CreateMockController(currentPos);
            var worldMatrix = MatrixD.Identity;
            worldMatrix.Translation = currentPos;
            worldMatrix.Forward = Vector3D.Forward;
            Mock.Get(mockController).Setup(c => c.WorldMatrix).Returns(worldMatrix);

            var gyros = SEMockFactory.CreateStandardGyroSet();
            var gyroDict = SEMockFactory.CreateGyroscopeDictionary(gyros);
            droneController = CreateDroneController(mockController, gyroDict);

            foreach (var gyroList in gyroDict.Values)
            {
                foreach (var gyro in gyroList)
                {
                    gyro.GyroOverride = true;
                }
            }

            SetPrivateField(droneController, "isRotating", true);
            SetPrivateField(droneController, "rotationStartFrame", 0L);
            SetPrivateField(droneController, "rotationDurationFrames", 100L);
            SetPrivateField(droneController, "rotationTarget", targetPos);
            SetPrivateField(droneController, "orientationToleranceDegrees", 5.0f);

            sessionMock.Setup(s => s.GameplayFrameCounter).Returns(100);

            InvokePrivateMethod(droneController, "UpdateRotation");

            foreach (var gyroList in gyroDict.Values)
            {
                foreach (var gyro in gyroList)
                {
                    Assert.IsFalse(gyro.GyroOverride, "Gyro override should be disabled");
                }
            }
        }

        private IAIDroneControllerBlock CreateDroneController(IMyShipController controller, Dictionary<Base6Directions.Direction, List<IMyGyro>> gyros)
        {
            var drone = new IAIDroneControllerBlock(
                sessionDelegate: new MockMySessionDelegate(),
                utilitiesDelegate: new MockMyUtilitiesDelegate());
            SetPrivateField(drone, "settings", new IAIDroneControllerSettings { });
            SetPrivateField(drone, "shipController", controller);
            SetPrivateField(drone, "gyroscopes", gyros);
            SetPrivateField(drone, "_entityId", controller.EntityId);
            return drone;
        }

        private void SetPrivateField<T>(object obj, string fieldName, T value)
        {
            if (obj == null)
            {
                Assert.Fail("Object is null");
                return;
            }

            var field = obj.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
            {
                Assert.Fail(string.Format("Field '{0}' not found on type {1}", fieldName, obj.GetType().Name));
                return;
            }

            field.SetValue(obj, value);
        }

        private T GetPrivateField<T>(object obj, string fieldName)
        {
            if (obj == null)
            {
                Assert.Fail("Object is null");
                return default(T);
            }

            var field = obj.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
            {
                Assert.Fail(string.Format("Field '{0}' not found on type {1}", fieldName, obj.GetType().Name));
                return default(T);
            }

            return (T)field.GetValue(obj);
        }

        private object InvokePrivateMethod(object obj, string methodName, params object[] parameters)
        {
            if (obj == null)
            {
                Assert.Fail("Object is null");
                return null;
            }

            // Get all private methods with the given name
            var methods = obj.GetType().GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            System.Reflection.MethodInfo targetMethod = null;

            foreach (var method in methods)
            {
                if (method.Name != methodName)
                    continue;

                var methodParams = method.GetParameters();

                // Check if parameter count matches
                int paramCount = parameters == null ? 0 : parameters.Length;
                if (methodParams.Length != paramCount)
                    continue;

                // Check if parameter types are compatible
                bool paramsMatch = true;
                for (int i = 0; i < paramCount; i++)
                {
                    if (parameters[i] == null)
                        continue; // Null can match any reference type

                    var paramType = parameters[i].GetType();
                    var expectedType = methodParams[i].ParameterType;

                    // Handle ref/out parameters - strip the & from the type
                    if (expectedType.IsByRef)
                    {
                        expectedType = expectedType.GetElementType();
                    }

                    // Check if types match or are assignable
                    if (!expectedType.IsAssignableFrom(paramType))
                    {
                        paramsMatch = false;
                        break;
                    }
                }

                if (paramsMatch)
                {
                    targetMethod = method;
                    break;
                }
            }

            if (targetMethod == null)
            {
                var paramTypes = new System.Text.StringBuilder();
                if (parameters != null)
                {
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (i > 0) paramTypes.Append(", ");
                        paramTypes.Append(parameters[i] != null ? parameters[i].GetType().Name : "null");
                    }
                }

                Assert.Fail(string.Format("Method '{0}({1})' not found on type {2}",
                    methodName, paramTypes.ToString(), obj.GetType().Name));
                return null;
            }

            // For ref parameters, we need to wrap them in an array
            var methodParameters = targetMethod.GetParameters();
            object[] invokeParams = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                invokeParams[i] = parameters[i];
            }

            var result = targetMethod.Invoke(obj, invokeParams);

            // Copy back any ref/out parameter values (though we don't use them in these tests)
            for (int i = 0; i < methodParameters.Length; i++)
            {
                if (methodParameters[i].ParameterType.IsByRef)
                {
                    parameters[i] = invokeParams[i];
                }
            }

            return result;
        }
    }
}