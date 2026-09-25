using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// A* pathfinding node that can be used in FastPriorityQueue
    /// </summary>
    public class AStarNode : FastPriorityQueueNode
    {
        public Vector3I Position;
        public double GCost;
        public double HCost;
        public AStarNode Parent;

        public double FCost
        {
            get { return GCost + HCost; }
        }

        public void Reset()
        {
            Position = default(Vector3I);
            GCost = 0;
            HCost = 0;
            Parent = null;
            QueueIndex = 0;
            Priority = 0;
        }
    }
}