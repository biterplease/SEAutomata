using ImprovedAI.Config;
using ImprovedAI.Util.Logging;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ImprovedAI.Pathfinding
{
    public static class AStarPathfinder
    {
        // Pre-computed direction arrays for different forward orientations
        private static readonly Vector3I[][] DIRECTIONS_BY_FORWARD = new Vector3I[6][];

        static AStarPathfinder()
        {
            InitializeDirectionArrays();
        }

        /// <summary>
        /// Calculate the complete path from start to end.
        /// Populates output list with waypoints representing the entire path.
        /// </summary>
        public static bool CalculatePath(ref PathfindingContext context, ref Vector3D start, ref Vector3D end, List<Vector3D> output)
        {
            if (output == null)
                throw new ArgumentNullException("output");

            output.Clear();

            // For short distances, use direct pathfinding
            double distance = Vector3D.Distance(start, end);
            if (distance < context.WaypointDistance * 2)
            {
                return DirectPathfinder.CalculatePath(ref context, ref start, ref end, output);
            }

            // Run A* algorithm
            if (FindPath(ref context, ref start, ref end, output))
            {
                return true;
            }

            // Fallback to direct if allowed
            if (context.AllowRepathing)
            {
                Log.Warning("AStarPathfinder: Failed to find path, falling back to direct");
                return DirectPathfinder.CalculatePath(ref context, ref start, ref end, output);
            }

            return false;
        }

        /// <summary>
        /// Get the next waypoint using iterator pattern.
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

            double distance = Vector3D.Distance(start, end);

            // For short distances, use direct pathfinding
            if (distance < context.WaypointDistance * 2)
            {
                return DirectPathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint, out request);
            }

            // Try A* path
            if (context.PathBuffer == null)
                context.PathBuffer = new List<Vector3D>();

            if (FindPath(ref context, ref start, ref end, context.PathBuffer))
            {
                if (context.PathBuffer.Count > 1)
                {
                    waypoint = context.PathBuffer[1];
                    return PathfindingResult.Success;
                }
            }

            // A* failed, fallback if allowed
            if (context.AllowRepathing)
            {
                return DirectPathfinder.GetNextWaypoint(ref context, ref start, ref end, out waypoint, out request);
            }

            return PathfindingResult.Failed;
        }

        /// <summary>
        /// Calculate estimated complexity of the full path without building it.
        /// </summary>
        public static double CalculatePathComplexity(ref PathfindingContext context, ref Vector3D start, ref Vector3D end)
        {
            double distance = Vector3D.Distance(start, end);
            double gridSpacing = CalculateOptimalGridSpacing(distance);

            // Estimate based on distance and grid spacing
            double estimatedNodes = (distance / gridSpacing) * 1.5;
            return Math.Min(estimatedNodes, context.MaxPathNodes);
        }

        /// <summary>
        /// Get estimated complexity to reach next waypoint.
        /// </summary>
        public static double GetNextWaypointComplexity(ref PathfindingContext context, ref Vector3D start, ref Vector3D end)
        {
            double distance = Vector3D.Distance(start, end);

            // Short distances use direct pathfinding
            if (distance < context.WaypointDistance * 2)
                return 1.0;

            // Estimate nodes needed for next waypoint
            double gridSpacing = CalculateOptimalGridSpacing(distance);
            double estimatedNodes = (context.WaypointDistance / gridSpacing) * 1.2;
            return Math.Min(estimatedNodes, context.MaxPathNodes * 0.1);
        }

        #region Core A* Algorithm

        private static bool FindPath(ref PathfindingContext context, ref Vector3D start, ref Vector3D end, List<Vector3D> pathOutput)
        {
            // Calculate grid spacing
            double distance = Vector3D.Distance(start, end);
            double gridSpacing = CalculateOptimalGridSpacing(distance);

            // Initialize working sets
            if (context.OpenSet == null)
                context.OpenSet = new Dictionary<Vector3I, AStarNode>();
            if (context.ClosedSet == null)
                context.ClosedSet = new Dictionary<Vector3I, AStarNode>();
            if (context.OpenQueue == null)
                context.OpenQueue = new FastPriorityQueue<AStarNode>(context.MaxAStarNodes);

            context.OpenSet.Clear();
            context.ClosedSet.Clear();
            context.OpenQueue.Clear();

            // Convert world positions to grid coordinates
            Vector3I startGrid = WorldToGrid(ref start, gridSpacing);
            Vector3I endGrid = WorldToGrid(ref end, gridSpacing);

            // Initialize start node
            AStarNode startNode = GetOrCreateNode(ref context);
            startNode.Position = startGrid;
            startNode.GCost = 0;
            startNode.HCost = CalculateHeuristic(ref startGrid, ref endGrid);
            startNode.Parent = null;

            context.OpenSet[startGrid] = startNode;
            context.OpenQueue.Enqueue(startNode, startNode.FCost);

            int nodesProcessed = 0;

            while (context.OpenQueue.Count > 0 && nodesProcessed < context.MaxPathNodes)
            {
                AStarNode currentNode = context.OpenQueue.Dequeue();
                Vector3I currentPos = currentNode.Position;

                // Check if reached goal
                if (currentPos == endGrid)
                {
                    return ReconstructPath(ref context, currentNode, gridSpacing, pathOutput);
                }

                context.OpenSet.Remove(currentPos);
                context.ClosedSet[currentPos] = currentNode;
                nodesProcessed++;

                // Get neighbors
                if (context.NeighborBuffer == null)
                    context.NeighborBuffer = new List<Vector3I>();

                GetNeighbors(ref context, ref currentPos, context.NeighborBuffer);

                // Process each neighbor
                for (int i = 0; i < context.NeighborBuffer.Count; i++)
                {
                    Vector3I neighborPos = context.NeighborBuffer[i];

                    if (context.ClosedSet.ContainsKey(neighborPos))
                        continue;

                    Vector3D neighborWorld = GridToWorld(ref neighborPos, gridSpacing);

                    // Check if neighbor is valid (not in obstacles, safe altitude, etc.)
                    if (!IsValidPosition(ref context, ref neighborWorld))
                        continue;

                    double moveCost = Vector3.Distance(currentPos, neighborPos);
                    double newGCost = currentNode.GCost + moveCost;

                    AStarNode neighborNode;
                    bool isInOpenSet = context.OpenSet.TryGetValue(neighborPos, out neighborNode);

                    if (!isInOpenSet || newGCost < neighborNode.GCost)
                    {
                        if (!isInOpenSet)
                        {
                            neighborNode = GetOrCreateNode(ref context);
                            neighborNode.Position = neighborPos;
                        }

                        neighborNode.GCost = newGCost;
                        neighborNode.HCost = CalculateHeuristic(ref neighborPos, ref endGrid);
                        neighborNode.Parent = currentNode;

                        if (!isInOpenSet)
                        {
                            context.OpenSet[neighborPos] = neighborNode;
                            context.OpenQueue.Enqueue(neighborNode, neighborNode.FCost);
                        }
                        else
                        {
                            context.OpenQueue.UpdatePriority(neighborNode, neighborNode.FCost);
                        }
                    }
                }
            }

            Log.Warning("AStarPathfinder: No path found to target");
            return false;
        }

        private static bool ReconstructPath(ref PathfindingContext context, AStarNode endNode, double gridSpacing, List<Vector3D> output)
        {
            output.Clear();

            AStarNode current = endNode;
            while (current != null)
            {
                Vector3D worldPos = GridToWorld(ref current.Position, gridSpacing);
                output.Add(worldPos);
                current = current.Parent;
            }

            output.Reverse();
            return output.Count > 1;
        }

        #endregion

        #region Helper Methods

        private static double CalculateOptimalGridSpacing(double distance)
        {
            if (distance < 500)
                return 25.0;
            if (distance < 2000)
                return 50.0;
            if (distance < 5000)
                return 100.0;
            return 200.0;
        }

        private static Vector3I WorldToGrid(ref Vector3D worldPos, double gridSpacing)
        {
            return new Vector3I(
                (int)Math.Round(worldPos.X / gridSpacing),
                (int)Math.Round(worldPos.Y / gridSpacing),
                (int)Math.Round(worldPos.Z / gridSpacing)
            );
        }

        private static Vector3D GridToWorld(ref Vector3I gridPos, double gridSpacing)
        {
            return new Vector3D(
                gridPos.X * gridSpacing,
                gridPos.Y * gridSpacing,
                gridPos.Z * gridSpacing
            );
        }

        private static double CalculateHeuristic(ref Vector3I a, ref Vector3I b)
        {
            return Vector3.Distance(a, b);
        }

        private static void GetNeighbors(ref PathfindingContext context, ref Vector3I position, List<Vector3I> output)
        {
            output.Clear();

            // Use direction array based on controller forward direction
            int forwardIndex = (int)context.ControllerForwardDirection;
            Vector3I[] directions = DIRECTIONS_BY_FORWARD[forwardIndex];

            for (int i = 0; i < directions.Length; i++)
            {
                output.Add(position + directions[i]);
            }
        }

        private static bool IsValidPosition(ref PathfindingContext context, ref Vector3D position)
        {
            // Check altitude if in gravity
            if (context.IsInPlanetGravity && context.IsBelowSafeAltitude(ref position))
                return false;

            // Additional validation could be done here with sensor/camera data
            return true;
        }

        private static AStarNode GetOrCreateNode(ref PathfindingContext context)
        {
            // Simple allocation - in production you'd use a node pool
            return new AStarNode();
        }

        private static void InitializeDirectionArrays()
        {
            // Initialize all 26 directions for each forward orientation
            // This is a simplified version - in production you'd pre-compute all 6 orientations
            Vector3I[] baseDirections = new Vector3I[26];
            int idx = 0;

            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                        baseDirections[idx++] = new Vector3I(x, y, z);
                    }
                }
            }

            // Initialize all 6 forward directions with the base set
            for (int i = 0; i < 6; i++)
            {
                DIRECTIONS_BY_FORWARD[i] = baseDirections;
            }
        }

        #endregion
    }
}