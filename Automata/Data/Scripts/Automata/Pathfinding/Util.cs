using System;
using VRageMath;

namespace Automata.Pathfinding
{
    public class PathfindingUtil
    {
        /// <summary>
        /// Directional mappings for controllers with a different forward direction.
        /// Indexed as [controllerForward, objectDirection]
        /// </summary>
        private static readonly Base6Directions.Direction[,] NavigationMappings = InitializeMappings();
        private static Base6Directions.Direction[,] InitializeMappings()
        {
            var mappings = new Base6Directions.Direction[6, 6];

            mappings[0, 0] = Base6Directions.Direction.Forward;  // Forward -> Backward
            mappings[0, 1] = Base6Directions.Direction.Backward;   // Backward -> Forward
            mappings[0, 2] = Base6Directions.Direction.Left;     // Left -> Right
            mappings[0, 3] = Base6Directions.Direction.Right;      // Right -> Left
            mappings[0, 4] = Base6Directions.Direction.Up;        // Up -> Up
            mappings[0, 5] = Base6Directions.Direction.Down;      // Down -> Down


            // Backward (1)
            mappings[1, 0] = Base6Directions.Direction.Backward;  // Forward -> Backward
            mappings[1, 1] = Base6Directions.Direction.Forward;   // Backward -> Forward
            mappings[1, 2] = Base6Directions.Direction.Right;     // Left -> Right
            mappings[1, 3] = Base6Directions.Direction.Left;      // Right -> Left
            mappings[1, 4] = Base6Directions.Direction.Up;        // Up -> Up
            mappings[1, 5] = Base6Directions.Direction.Down;      // Down -> Down

            // Left (2)
            mappings[2, 0] = Base6Directions.Direction.Right;     // Forward -> Right
            mappings[2, 1] = Base6Directions.Direction.Left;      // Backward -> Left
            mappings[2, 2] = Base6Directions.Direction.Forward;   // Left -> Forward
            mappings[2, 3] = Base6Directions.Direction.Backward;  // Right -> Backward
            mappings[2, 4] = Base6Directions.Direction.Up;        // Up -> Up
            mappings[2, 5] = Base6Directions.Direction.Down;      // Down -> Down

            // Right (3)
            mappings[3, 0] = Base6Directions.Direction.Left;      // Forward -> Left
            mappings[3, 1] = Base6Directions.Direction.Right;     // Backward -> Right
            mappings[3, 2] = Base6Directions.Direction.Backward;  // Left -> Backward
            mappings[3, 3] = Base6Directions.Direction.Forward;   // Right -> Forward
            mappings[3, 4] = Base6Directions.Direction.Up;        // Up -> Up
            mappings[3, 5] = Base6Directions.Direction.Down;      // Down -> Down

            // Up (4)
            mappings[4, 0] = Base6Directions.Direction.Down;      // Forward -> Down
            mappings[4, 1] = Base6Directions.Direction.Up;        // Backward -> Up
            mappings[4, 2] = Base6Directions.Direction.Left;      // Left -> Left
            mappings[4, 3] = Base6Directions.Direction.Right;     // Right -> Right
            mappings[4, 4] = Base6Directions.Direction.Forward;   // Up -> Forward
            mappings[4, 5] = Base6Directions.Direction.Backward;  // Down -> Backward

            // Down (5)
            mappings[5, 0] = Base6Directions.Direction.Up;        // Forward -> Up
            mappings[5, 1] = Base6Directions.Direction.Down;      // Backward -> Down
            mappings[5, 2] = Base6Directions.Direction.Left;      // Left -> Left
            mappings[5, 3] = Base6Directions.Direction.Right;     // Right -> Right
            mappings[5, 4] = Base6Directions.Direction.Backward;  // Up -> Backward
            mappings[5, 5] = Base6Directions.Direction.Forward;   // Down -> Forward

            return mappings;
        }
        public static Base6Directions.Direction MapToNavigationDirection(
            Base6Directions.Direction objectDirection,
            Base6Directions.Direction controllerForward)
        {
            return NavigationMappings[(int)controllerForward, (int)objectDirection];
        }


        /// <summary>
        /// Helper method to get rotation matrix, adjusted for controller forward direction.
        /// </summary>
        /// <param name="controllerForward"></param>
        /// <returns></returns>
        public static MatrixD GetNavigationRotationMatrix(Base6Directions.Direction controllerForward)
        {
            switch (controllerForward)
            {
                case Base6Directions.Direction.Forward:
                    return MatrixD.Identity; // No rotation needed
                case Base6Directions.Direction.Backward:
                    return MatrixD.CreateRotationY(Math.PI); // 180° around Y
                case Base6Directions.Direction.Up:
                    return MatrixD.CreateRotationX(Math.PI / 2); // +90° around X
                                                                 // This rotates -Z to +Y (forward becomes up)
                case Base6Directions.Direction.Down:
                    return MatrixD.CreateRotationX(-Math.PI / 2); // -90° around X
                                                                  // This rotates -Z to -Y (forward becomes down)
                case Base6Directions.Direction.Left:
                    return MatrixD.CreateRotationY(Math.PI / 2); // +90° around Y
                                                                 // This rotates -Z to -X (forward becomes left)
                case Base6Directions.Direction.Right:
                    return MatrixD.CreateRotationY(-Math.PI / 2); // -90° around Y
                                                                  // This rotates -Z to +X (forward becomes right)
                default:
                    return MatrixD.Identity;
            }
        }
    }
}