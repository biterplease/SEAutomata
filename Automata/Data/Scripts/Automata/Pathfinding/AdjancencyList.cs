using System.Collections.Generic;
using VRageMath;

namespace Automata.Pathfinding
{
    public struct Edge
    {
        public Vector3I Target;
        public float Weight;

        public Edge(Vector3I target, float weight)
        {
            Target = target;
            Weight = weight;
        }
    }

    public class AdjacencyList
    {
        private Dictionary<Vector3I, List<Edge>> adjacencyList;
        private List<Edge> emptyEdgeList; // Reusable empty list to avoid allocations

        public AdjacencyList()
        {
            adjacencyList = new Dictionary<Vector3I, List<Edge>>();
            emptyEdgeList = new List<Edge>(0);
        }

        /// <summary>
        /// Clear cached nodes.
        /// </summary>
        public void Clear()
        {
            adjacencyList.Clear();
        }

        /// <summary>
        /// Add a unidirectional edge from source to target
        /// </summary>
        public void AddEdge(Vector3I from, Vector3I to, float cost)
        {
            List<Edge> edges;
            if (!adjacencyList.TryGetValue(from, out edges))
            {
                edges = new List<Edge>();
                adjacencyList[from] = edges;
            }

            edges.Add(new Edge(to, cost));
        }

        /// <summary>
        /// Add a bidirectional edge between two points
        /// </summary>
        public void AddBidirectionalEdge(Vector3I pos1, Vector3I pos2, float cost)
        {
            AddEdge(pos1, pos2, cost);
            AddEdge(pos2, pos1, cost);
        }

        /// <summary>
        /// Get all neighbors of a position. Returns read-only list.
        /// </summary>
        public List<Edge> GetNeighbors(Vector3I position)
        {
            List<Edge> edges;
            if (adjacencyList.TryGetValue(position, out edges))
            {
                return edges;
            }

            return emptyEdgeList; // Return cached empty list instead of allocating
        }

        /// <summary>
        /// Check if there's a direct connection between two points
        /// </summary>
        public bool HasEdge(Vector3I from, Vector3I to)
        {
            List<Edge> edges;
            if (!adjacencyList.TryGetValue(from, out edges))
                return false;

            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].Target.Equals(to))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Get the cost of a direct edge, or infinity if no connection
        /// </summary>
        public float GetEdgeCost(Vector3I from, Vector3I to)
        {
            List<Edge> edges;
            if (!adjacencyList.TryGetValue(from, out edges))
                return float.PositiveInfinity;

            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].Target.Equals(to))
                    return edges[i].Weight;
            }

            return float.PositiveInfinity;
        }

        /// <summary>
        /// Get the cost of a direct edge, or infinity if no connection
        /// </summary>
        public bool TryGetEdgeCost(Vector3I from, Vector3I to, out float cost)
        {
            List<Edge> edges;
            if (!adjacencyList.TryGetValue(from, out edges))
            {
                cost = float.PositiveInfinity;
                return false;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].Target.Equals(to))
                {
                    cost = edges[i].Weight;
                    return true;
                }
            }

            cost = float.PositiveInfinity;
            return false;
        }

        /// <summary>
        /// Remove an edge from the graph
        /// </summary>
        public void RemoveEdge(Vector3I from, Vector3I to)
        {
            List<Edge> edges;
            if (!adjacencyList.TryGetValue(from, out edges))
                return;

            edges.RemoveAll(e => e.Target.Equals(to));

            // Clean up empty lists
            if (edges.Count == 0)
            {
                adjacencyList.Remove(from);
            }
        }

        /// <summary>
        /// Get all nodes in the graph
        /// </summary>
        public IEnumerable<Vector3I> GetAllNodes()
        {
            return adjacencyList.Keys;
        }

        /// <summary>
        /// Helper: Create a standard 6-directional grid in a region
        /// </summary>
        public void CreateStandardGrid(Vector3I min, Vector3I max, float moveCost)
        {
            // Six directions: +X, -X, +Y, -Y, +Z, -Z
            Vector3I[] directions = new Vector3I[]
            {
                new Vector3I(1, 0, 0),   // Right
                new Vector3I(-1, 0, 0),  // Left
                new Vector3I(0, 1, 0),   // Up
                new Vector3I(0, -1, 0),  // Down
                new Vector3I(0, 0, 1),   // Forward
                new Vector3I(0, 0, -1)   // Back
            };

            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        Vector3I current = new Vector3I(x, y, z);

                        // Connect to all 6 adjacent cells
                        for (int d = 0; d < directions.Length; d++)
                        {
                            Vector3I neighbor = new Vector3I(
                                current.X + directions[d].X,
                                current.Y + directions[d].Y,
                                current.Z + directions[d].Z
                            );

                            // Check if neighbor is within bounds
                            if (neighbor.X >= min.X && neighbor.X <= max.X &&
                                neighbor.Y >= min.Y && neighbor.Y <= max.Y &&
                                neighbor.Z >= min.Z && neighbor.Z <= max.Z)
                            {
                                AddBidirectionalEdge(current, neighbor, moveCost);
                            }
                        }
                    }
                }
            }
        }
    }
}