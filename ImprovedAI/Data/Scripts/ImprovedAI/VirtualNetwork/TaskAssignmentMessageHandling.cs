using System.Collections.Generic;

namespace ImprovedAI.VirtualNetwork
{
    /// <summary>
    /// Parses task-assignment payloads on the drone side. Isolated for unit tests.
    /// </summary>
    public static class TaskAssignmentMessageHandling
    {
        /// <summary>When false, the drone should ignore the message (wrong addressee).</summary>
        public static bool IsDirectMessageForDrone(long messageRecipientId, long droneEntityId)
        {
            return messageRecipientId == droneEntityId;
        }

        /// <summary>Enqueues all tasks from the payload; returns count added.</summary>
        public static int EnqueueFromTaskAssignment(TaskAssignment assignment, Queue<Scheduler.Task> taskQueue)
        {
            if (assignment == null || assignment.Tasks == null || taskQueue == null)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < assignment.Tasks.Count; i++)
            {
                taskQueue.Enqueue(assignment.Tasks[i]);
                count++;
            }
            return count;
        }
    }
}
