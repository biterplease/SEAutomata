using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Result of a pathfinding operation
    /// </summary>
    public enum PathfindingResult : byte
    {
        Success,      // Waypoint found successfully
        NeedRaycast,  // Need raycast in specified direction
        Failed        // No valid path found
    }

    /// <summary>
    /// Request for additional data from manager
    /// </summary>
    public struct PathfindingRequest
    {
        public Vector3D RaycastStart;
        public Vector3D RaycastDirection;
        public double RaycastDistance;

        public PathfindingRequest(Vector3D start, Vector3D direction, double distance)
        {
            RaycastStart = start;
            RaycastDirection = direction;
            RaycastDistance = distance;
        }
    }
}