using ImprovedAI.Util.Logging;
using System;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI.Pathfinding
{
    /// <summary>
    /// Stateless direct pathfinding implementation.
    /// All state is passed via PathfindingContext.
    /// </summary>
    public static class DirectPathfinder
    {
        /// <summary>
        /// Calculate the complete path from start to end.
        /// Note: This doesn't use iterator pattern - manager should use GetNextWaypoint instead.
        /// </summary>
        public static bool CalculatePath(ref PathfindingContext context, ref Vector3D start, ref Vector3D end, List<Vector3D> output)
        {
            if (output == null)
                throw new ArgumentNullException("output");

            output.Clear();
            output.Add(start);

            Vector3D currentPos = start;
            int maxIterations = 100;
            int iteration = 0;

            while (Vector3D.DistanceSquared(currentPos, end) > context.WaypointDistance * context.WaypointDistance)
            {
                if (++iteration > maxIterations)
                {
                    Log.Warning("DirectPathfinder: Max iterations reached");
                    break;
                }

                Vector3D nextWaypoint;
                PathfindingRequest request;
                PathfindingResult result = GetNextWaypoint(ref context, ref currentPos, ref end, out nextWaypoint, out request);

                if (result == PathfindingResult.NeedRaycast)
                {
                    // Can't proceed without manager doing raycasts
                    Log.Warning("DirectPathfinder: CalculatePath cannot handle raycast requests");
                    output.Add(end);
                    return false;
                }

                if (result == PathfindingResult.Failed)
                {
                    output.Add(end);
                    return false;
                }

                output.Add(nextWaypoint);
                currentPos = nextWaypoint;
            }

            output.Add(end);
            return true;
        }

        /// <summary>
        /// Get the next waypoint in the path using iterator pattern.
        /// Returns quickly with the first viable waypoint or requests raycast.
        /// </summary>
        public static PathfindingResult GetNextWaypoint(
            ref PathfindingContext context,
            ref Vector3D start,
            ref Vector3D end,
            out Vector3D waypoint,
            out PathfindingRequest request)
        {
            waypoint = default(Vector3D);
            request = default(PathfindingRequest);
            context.RepositionComplexityIncrement = 0;

            double distanceToTarget = Vector3D.Distance(start, end);

            // If close enough, return target
            if (distanceToTarget < context.WaypointDistance)
            {
                waypoint = end;
                return PathfindingResult.Success;
            }

            // Calculate direction to target
            Vector3D direction = end - start;
            direction.Normalize();

            // Calculate waypoint at max distance
            Vector3D proposedWaypoint = start + direction * context.WaypointDistance;

            // Adjust for gravity
            if (context.IsInPlanetGravity && context.UsePlanetAwarePathfinding)
            {
                if (!AdjustForGravity(ref context, ref start, ref proposedWaypoint, out proposedWaypoint))
                    return PathfindingResult.Failed;
            }

            // Check known obstacles first
            if (HasObstacleInPath(ref context, ref start, ref proposedWaypoint))
            {
                // Try repositioning
                PathfindingResult result = TryRepositionWaypoint(
                    ref context, ref start, ref end, ref proposedWaypoint,
                    out waypoint, out request);
                if (result == PathfindingResult.Success)
                    context.RepositionComplexityIncrement = 1;
                return result;
            }

            // Check if we've raycasted this direction
            Vector3D normalizedDir = direction;
            normalizedDir.Normalize();

            if (context.RaycastCache != null && !context.RaycastCache.Contains(normalizedDir))
            {
                // Need raycast verification
                request = new PathfindingRequest(start, normalizedDir, context.WaypointDistance);
                return PathfindingResult.NeedRaycast;
            }

            waypoint = proposedWaypoint;
            return PathfindingResult.Success;
        }

        /// <summary>
        /// Calculate estimated complexity of the full path.
        /// For direct pathfinding, complexity is always minimal.
        /// </summary>
        /// <param name="context">Pathfinding context</param>
        /// <param name="start">Starting position</param>
        /// <param name="end">Target position</param>
        /// <returns>Estimated complexity value (always 1 for direct pathfinding)</returns>
        public static double CalculatePathComplexity(ref PathfindingContext context, ref Vector3D start, ref Vector3D end)
        {
            return 1.0; // Direct pathfinding has minimal complexity
        }

        /// <summary>
        /// Get estimated complexity to reach next waypoint.
        /// </summary>
        /// <param name="context">Pathfinding context</param>
        /// <param name="start">Starting position</param>
        /// <param name="end">Target position</param>
        /// <returns>Estimated complexity (always 1 for direct pathfinding)</returns>
        public static double GetNextWaypointComplexity(ref PathfindingContext context, ref Vector3D start, ref Vector3D end)
        {
            return 1.0; // Direct pathfinding has minimal complexity
        }

        #region Helper Methods

        /// <summary>
        /// Adjust waypoint for gravity to maintain safe altitude
        /// </summary>
        private static bool AdjustForGravity(ref PathfindingContext context, ref Vector3D start, ref Vector3D proposedWaypoint, out Vector3D adjustedWaypoint)
        {
            adjustedWaypoint = proposedWaypoint;

            if (!context.PlanetCenter.HasValue)
                return true;

            // Check if waypoint is below safe altitude
            if (context.IsBelowSafeAltitude(ref proposedWaypoint))
            {
                // Calculate direction away from planet center
                Vector3D toPlanet = context.PlanetCenter.Value - proposedWaypoint;
                toPlanet.Normalize();

                // Calculate safe position
                double safeDistance = context.PlanetRadius + context.MinAltitudeBuffer;
                adjustedWaypoint = context.PlanetCenter.Value - toPlanet * safeDistance;

                Log.Verbose("DirectPathfinder: Adjusted waypoint for safe altitude");
                return true;
            }

            return true;
        }

        /// <summary>
        /// Check if there are known obstacles in the path
        /// </summary>
        private static bool HasObstacleInPath(ref PathfindingContext context, ref Vector3D start, ref Vector3D end)
        {
            if (context.KnownObstacles == null || context.KnownObstacles.Count == 0)
                return false;

            LineD path = new LineD(start, end);

            for (int i = 0; i < context.KnownObstacles.Count; i++)
            {
                if (path.GetBoundingBox().Intersects(context.KnownObstacles[i].BoundingBox))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Try to reposition waypoint around obstacles
        /// </summary>
        private static PathfindingResult TryRepositionWaypoint(
            ref PathfindingContext context,
            ref Vector3D start,
            ref Vector3D end,
            ref Vector3D proposedWaypoint,
            out Vector3D repositionedWaypoint,
            out PathfindingRequest request)
        {
            repositionedWaypoint = proposedWaypoint;
            request = default(PathfindingRequest);

            if (context.MaxRepositionAttempts == 0)
                return PathfindingResult.Failed;

            Vector3D toTarget = end - start;
            toTarget.Normalize();

            // Generate perpendicular directions
            Vector3D perp1 = Vector3D.CalculatePerpendicularVector(toTarget);
            Vector3D perp2 = Vector3D.Cross(toTarget, perp1);
            perp1.Normalize();
            perp2.Normalize();

            double offset = context.WaypointDistance * 0.3;
            Vector3D[] offsets = new Vector3D[]
            {
                perp1 * offset,
                -perp1 * offset,
                perp2 * offset,
                -perp2 * offset,
                (perp1 + perp2) * offset * 0.707,
                (-perp1 - perp2) * offset * 0.707
            };

            for (int i = 0; i < offsets.Length && i < context.MaxRepositionAttempts; i++)
            {
                Vector3D testWaypoint = proposedWaypoint + offsets[i];

                // Check known obstacles
                if (!HasObstacleInPath(ref context, ref start, ref testWaypoint))
                {
                    // Check if raycasted
                    Vector3D testDir = testWaypoint - start;
                    testDir.Normalize();

                    if (context.RaycastCache != null && !context.RaycastCache.Contains(testDir))
                    {
                        // Need raycast
                        request = new PathfindingRequest(start, testDir, Vector3D.Distance(start, testWaypoint));
                        return PathfindingResult.NeedRaycast;
                    }

                    repositionedWaypoint = testWaypoint;
                    return PathfindingResult.Success;
                }
            }

            return PathfindingResult.Failed;
        }

        #endregion
    }
}