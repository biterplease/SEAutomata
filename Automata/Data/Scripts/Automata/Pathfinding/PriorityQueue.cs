using System;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Base class for nodes that can be used in FastPriorityQueue.
    /// Nodes must inherit from this to track their position in the heap.
    /// </summary>
    public class FastPriorityQueueNode
    {
        /// <summary>
        /// Position in the queue. Do not modify manually.
        /// </summary>
        public int QueueIndex { get; set; }

        /// <summary>
        /// Priority of the node. Lower values = higher priority.
        /// </summary>
        public double Priority { get; set; }
    }

    /// <summary>
    /// High-performance priority queue optimized for pathfinding.
    /// Fixed-size, no safety checks for maximum speed.
    /// Based on BlueRaja's FastPriorityQueue design.
    /// </summary>
    public class FastPriorityQueue<T> where T : FastPriorityQueueNode
    {
        private readonly T[] nodes;
        private int numNodes;

        public FastPriorityQueue(int maxNodes)
        {
            nodes = new T[maxNodes + 1]; // 1-indexed for cleaner math
            numNodes = 0;
        }

        public int Count
        {
            get { return numNodes; }
        }

        public int MaxSize
        {
            get { return nodes.Length - 1; }
        }

        public void Clear()
        {
            Array.Clear(nodes, 1, numNodes);
            numNodes = 0;
        }

        public bool Contains(T node)
        {
            return node.QueueIndex > 0 && node.QueueIndex <= numNodes && nodes[node.QueueIndex] == node;
        }

        public void Enqueue(T node, double priority)
        {
            node.Priority = priority;
            numNodes++;
            nodes[numNodes] = node;
            node.QueueIndex = numNodes;
            CascadeUp(node);
        }

        private void CascadeUp(T node)
        {
            int parent;
            if (node.QueueIndex > 1)
            {
                parent = node.QueueIndex >> 1; // Divide by 2
                T parentNode = nodes[parent];
                if (HasHigherPriority(parentNode, node))
                    return;

                // Node has lower priority than parent, swap
                nodes[node.QueueIndex] = parentNode;
                parentNode.QueueIndex = node.QueueIndex;

                node.QueueIndex = parent;
            }
            else
            {
                return;
            }

            while (parent > 1)
            {
                parent >>= 1; // Divide by 2
                T parentNode = nodes[parent];
                if (HasHigherPriority(parentNode, node))
                    break;

                // Node has lower priority than parent, swap
                nodes[node.QueueIndex] = parentNode;
                parentNode.QueueIndex = node.QueueIndex;

                node.QueueIndex = parent;
            }

            nodes[node.QueueIndex] = node;
        }

        private void CascadeDown(T node)
        {
            int finalQueueIndex = node.QueueIndex;
            int childLeftIndex = 2 * finalQueueIndex;

            // If leaf node, done
            if (childLeftIndex > numNodes)
            {
                return;
            }

            int childRightIndex = childLeftIndex + 1;
            T childLeft = nodes[childLeftIndex];

            // Check if right child exists and has higher priority
            if (childRightIndex <= numNodes)
            {
                T childRight = nodes[childRightIndex];
                if (HasHigherPriority(childRight, childLeft))
                {
                    childLeftIndex = childRightIndex;
                    childLeft = childRight;
                }
            }

            // If node has higher priority than best child, done
            if (HasHigherPriority(node, childLeft))
            {
                return;
            }

            // Swap with best child
            nodes[finalQueueIndex] = childLeft;
            childLeft.QueueIndex = finalQueueIndex;

            finalQueueIndex = childLeftIndex;

            while (true)
            {
                childLeftIndex = 2 * finalQueueIndex;

                // If leaf node, done
                if (childLeftIndex > numNodes)
                {
                    node.QueueIndex = finalQueueIndex;
                    nodes[finalQueueIndex] = node;
                    break;
                }

                childRightIndex = childLeftIndex + 1;
                childLeft = nodes[childLeftIndex];

                // Check if right child exists and has higher priority
                if (childRightIndex <= numNodes)
                {
                    T childRight = nodes[childRightIndex];
                    if (HasHigherPriority(childRight, childLeft))
                    {
                        childLeftIndex = childRightIndex;
                        childLeft = childRight;
                    }
                }

                // If node has higher priority than best child, done
                if (HasHigherPriority(node, childLeft))
                {
                    node.QueueIndex = finalQueueIndex;
                    nodes[finalQueueIndex] = node;
                    break;
                }

                // Swap with best child
                nodes[finalQueueIndex] = childLeft;
                childLeft.QueueIndex = finalQueueIndex;

                finalQueueIndex = childLeftIndex;
            }
        }

        private bool HasHigherPriority(T higher, T lower)
        {
            return higher.Priority < lower.Priority;
        }

        public T Dequeue()
        {
            T returnMe = nodes[1];

            // Remove from queue
            if (numNodes == 1)
            {
                nodes[1] = null;
                numNodes = 0;
                return returnMe;
            }

            // Move last node to root
            T formerLastNode = nodes[numNodes];
            nodes[1] = formerLastNode;
            formerLastNode.QueueIndex = 1;
            nodes[numNodes] = null;
            numNodes--;

            // Cascade down
            CascadeDown(formerLastNode);
            return returnMe;
        }

        public void UpdatePriority(T node, double priority)
        {
            node.Priority = priority;
            OnNodeUpdated(node);
        }

        private void OnNodeUpdated(T node)
        {
            int parentIndex = node.QueueIndex >> 1;

            if (parentIndex > 0 && HasHigherPriority(node, nodes[parentIndex]))
            {
                CascadeUp(node);
            }
            else
            {
                CascadeDown(node);
            }
        }

        public T First
        {
            get { return nodes[1]; }
        }
    }
}