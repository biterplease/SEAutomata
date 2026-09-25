using ImprovedAI.Pathfinding;
using System.Collections.Generic;
using VRageMath;

public interface IPathfinder
{
    PathfindingManager.Method Method { get; }

    PathfindingResult GetNextWaypoint(
        ref PathfindingContext context,
        ref Vector3D start,
        ref Vector3D end,
        out Vector3D waypoint,
        out PathfindingRequest request);

    bool CalculatePath(ref PathfindingContext context, ref Vector3D start, ref Vector3D end, List<Vector3D> output);
    double CalculatePathComplexity(ref PathfindingContext context, ref Vector3D start, ref Vector3D end);
    double GetNextWaypointComplexity(ref PathfindingContext context, ref Vector3D start, ref Vector3D end);
}