using Microsoft.VisualStudio.TestTools.UnitTesting;
using ImprovedAI.Pathfinding;
using VRageMath;
using System.Collections.Generic;
using System;

namespace ImprovedAI.Tests.Pathfinding
{
    [TestClass]
    public class PathfindingUtilTests
    {
        [TestMethod]
        [DynamicData(nameof(GetNavigationRotationMatrixTestCases))]
        public void GetNavigationRotationMatrix_TransformsDirectionsCorrectly(
            Base6Directions.Direction input,
            Vector3D expectedForward,
            Vector3D expectedUp,
            Vector3D expectedRight,
            string description)
        {
            // Arrange & Act
            MatrixD result = PathfindingUtil.GetNavigationRotationMatrix(input);

            // Assert - Check that the matrix's basis vectors match expected directions
            AssertVectorsEqual(expectedForward, result.Forward, $"{description} - Forward");
            AssertVectorsEqual(expectedUp, result.Up, $"{description} - Up");
            AssertVectorsEqual(expectedRight, result.Right, $"{description} - Right");
        }

        public static IEnumerable<object[]> GetNavigationRotationMatrixTestCases()
        {
            // Standard coordinate system: X = Right, Y = Up, Z = Backward (Forward = -Z)

            yield return new object[]
            {
            Base6Directions.Direction.Forward,
            new Vector3D(0, 0, -1),  // Forward stays forward (-Z)
            new Vector3D(0, 1, 0),   // Up stays up (+Y)
            new Vector3D(1, 0, 0),   // Right stays right (+X)
            "Forward: Identity transformation"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Backward,
            new Vector3D(0, 0, 1),   // Backward (+Z) becomes forward
            new Vector3D(0, 1, 0),   // Up stays up (+Y)
            new Vector3D(-1, 0, 0),  // Left (-X) becomes right
            "Backward: 180° rotation around Y"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Up,
            new Vector3D(0, 1, 0),   // Up (+Y) becomes forward
            new Vector3D(0, 0, 1),   // Backward (+Z) becomes up
            new Vector3D(1, 0, 0),   // Right stays right (+X)
            "Up: -90° rotation around X"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Down,
            new Vector3D(0, -1, 0),  // Down (-Y) becomes forward
            new Vector3D(0, 0, -1),  // Forward (-Z) becomes up
            new Vector3D(1, 0, 0),   // Right stays right (+X)
            "Down: 90° rotation around X"
            };

            yield return new object[]
           {
            Base6Directions.Direction.Left,
            new Vector3D(-1, 0, 0),  // Left (-X) becomes forward
            new Vector3D(0, 1, 0),   // Up stays up (+Y)
            new Vector3D(0, 0, -1),  // Forward (-Z) becomes right (CORRECTED)
            "Left: 90° rotation around Y"
           };

            yield return new object[]
            {
            Base6Directions.Direction.Right,
            new Vector3D(1, 0, 0),   // Right (+X) becomes forward
            new Vector3D(0, 1, 0),   // Up stays up (+Y)
            new Vector3D(0, 0, 1),   // Backward (+Z) becomes right (CORRECTED)
            "Right: -90° rotation around Y"
            };
        }

        [TestMethod]
        [DynamicData(nameof(GetOrthogonalityTestCases))]
        public void GetNavigationRotationMatrix_ProducesOrthogonalMatrix(
            Base6Directions.Direction direction)
        {
            // Arrange & Act
            MatrixD result = PathfindingUtil.GetNavigationRotationMatrix(direction);

            // Assert - Verify matrix is orthogonal (rotation matrices must be orthogonal)
            Assert.IsTrue(IsOrthogonal(result),
                $"Matrix for {direction} should be orthogonal");
        }

        public static IEnumerable<object[]> GetOrthogonalityTestCases()
        {
            yield return new object[] { Base6Directions.Direction.Forward };
            yield return new object[] { Base6Directions.Direction.Backward };
            yield return new object[] { Base6Directions.Direction.Up };
            yield return new object[] { Base6Directions.Direction.Down };
            yield return new object[] { Base6Directions.Direction.Left };
            yield return new object[] { Base6Directions.Direction.Right };
        }

        [TestMethod]
        [DynamicData(nameof(GetTransformationTestCases))]
        public void GetNavigationRotationMatrix_TransformsVectorsCorrectly(
            Base6Directions.Direction direction,
            Vector3D inputVector,
            Vector3D expectedOutput,
            string description)
        {
            // Arrange
            MatrixD rotation = PathfindingUtil.GetNavigationRotationMatrix(direction);

            // Act
            Vector3D result = Vector3D.Transform(inputVector, rotation);

            // Assert
            AssertVectorsEqual(expectedOutput, result, description);
        }

        public static IEnumerable<object[]> GetTransformationTestCases()
        {
            // Test that forward vector (-Z) gets rotated to the correct direction
            Vector3D forwardVector = new Vector3D(0, 0, -1);

            yield return new object[]
            {
            Base6Directions.Direction.Forward,
            forwardVector,
            new Vector3D(0, 0, -1),
            "Forward: -Z stays -Z"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Backward,
            forwardVector,
            new Vector3D(0, 0, 1),
            "Backward: -Z becomes +Z"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Up,
            forwardVector,
            new Vector3D(0, 1, 0),
            "Up: -Z becomes +Y"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Down,
            forwardVector,
            new Vector3D(0, -1, 0),
            "Down: -Z becomes -Y"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Left,
            forwardVector,
            new Vector3D(-1, 0, 0),
            "Left: -Z becomes -X"
            };

            yield return new object[]
            {
            Base6Directions.Direction.Right,
            forwardVector,
            new Vector3D(1, 0, 0),
            "Right: -Z becomes +X"
            };
        }

        private static void AssertVectorsEqual(Vector3D expected, Vector3D actual, string message)
        {
            const double tolerance = 1e-10;
            Assert.IsTrue(
                Math.Abs(expected.X - actual.X) < tolerance &&
                Math.Abs(expected.Y - actual.Y) < tolerance &&
                Math.Abs(expected.Z - actual.Z) < tolerance,
                $"{message}\nExpected: {expected}\nActual: {actual}");
        }

        private static bool IsOrthogonal(MatrixD matrix)
        {
            const double tolerance = 1e-10;

            // Check that basis vectors are unit length
            if (Math.Abs(matrix.Right.Length() - 1.0) > tolerance) return false;
            if (Math.Abs(matrix.Up.Length() - 1.0) > tolerance) return false;
            if (Math.Abs(matrix.Forward.Length() - 1.0) > tolerance) return false;

            // Check that basis vectors are perpendicular
            if (Math.Abs(Vector3D.Dot(matrix.Right, matrix.Up)) > tolerance) return false;
            if (Math.Abs(Vector3D.Dot(matrix.Right, matrix.Forward)) > tolerance) return false;
            if (Math.Abs(Vector3D.Dot(matrix.Up, matrix.Forward)) > tolerance) return false;

            return true;
        }
    }

}