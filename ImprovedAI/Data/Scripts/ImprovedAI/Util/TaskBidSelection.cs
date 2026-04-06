using ImprovedAI.VirtualNetwork;
using System.Collections.Generic;
using static ImprovedAI.Orchestrator;

namespace ImprovedAI.Util
{
    public struct CollectedBid
    {
        public long SenderId;
        public TaskBid Bid;
    }

    /// <summary>
    /// Heuristic bid scoring (lower is better). No simulation dependency.
    /// </summary>
    public static class TaskBidSelection
    {
        public const float WeightPathComplexity = 4f;
        public const float WeightCargoBonus = 2f;

        public static float ScoreBid(TaskBid bid)
        {
            return bid.EstimatedTime + bid.PathComplexity * WeightPathComplexity - bid.CargoAvailability * WeightCargoBonus;
        }

        /// <summary>
        /// Scheduler accepts bids only if the bidder kind may compete for this task type.
        /// </summary>
        public static bool IsBidderEligibleForTask(TaskBidderKind bidderKind, TaskType taskType)
        {
            if (bidderKind == TaskBidderKind.LogisticsComputer)
                return SchedulerTaskCapabilities.IsLogisticsTaskType(taskType);

            return true;
        }

        /// <summary>
        /// Returns index of winning bid or -1 if no eligible bids.
        /// Tie-break: lower <see cref="CollectedBid.SenderId"/>.
        /// </summary>
        public static int PickWinnerIndex(List<CollectedBid> bids, TaskType taskType)
        {
            return PickWinnerIndex(bids, taskType, null);
        }

        /// <summary>
        /// Like <see cref="PickWinnerIndex(System.Collections.Generic.List{CollectedBid},TaskType)"/> but skips
        /// any bid whose <see cref="CollectedBid.SenderId"/> is in <paramref name="blacklistedSenderIds"/>.
        /// Pass null for <paramref name="blacklistedSenderIds"/> to disable blacklist filtering.
        /// </summary>
        public static int PickWinnerIndex(List<CollectedBid> bids, TaskType taskType, HashSet<long> blacklistedSenderIds)
        {
            if (bids == null || bids.Count == 0)
                return -1;

            int bestIdx = -1;
            float bestScore = 0f;
            long bestSender = 0L;

            for (int i = 0; i < bids.Count; i++)
            {
                CollectedBid cb = bids[i];
                if (blacklistedSenderIds != null && blacklistedSenderIds.Contains(cb.SenderId))
                    continue;
                TaskBid bid = cb.Bid;
                if (bid == null)
                    continue;
                if (!IsBidderEligibleForTask(bid.BidderKind, taskType))
                    continue;

                float score = ScoreBid(bid);
                if (bestIdx < 0 || score < bestScore || (score == bestScore && cb.SenderId < bestSender))
                {
                    bestIdx = i;
                    bestScore = score;
                    bestSender = cb.SenderId;
                }
            }

            return bestIdx;
        }
    }
}
