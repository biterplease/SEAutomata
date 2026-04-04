using System.Collections.Generic;
using ImprovedAI.Util;
using ImprovedAI.VirtualNetwork;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ImprovedAI.Scheduler;

namespace ImprovedAI.Tests
{
    [TestClass]
    public class TaskBidSelectionTests
    {
        [TestMethod]
        public void PickWinnerIndex_LC_Ineligible_ForWelding_PicksDrone()
        {
            List<CollectedBid> bids = new List<CollectedBid>
            {
                new CollectedBid
                {
                    SenderId = 200L,
                    Bid = new TaskBid
                    {
                        TaskId = 1,
                        EstimatedTime = 5f,
                        PathComplexity = 0.1f,
                        BidderKind = TaskBidderKind.Drone,
                        CargoAvailability = 0.5f,
                    },
                },
                new CollectedBid
                {
                    SenderId = 100L,
                    Bid = new TaskBid
                    {
                        TaskId = 1,
                        EstimatedTime = 1f,
                        PathComplexity = 0.01f,
                        BidderKind = TaskBidderKind.LogisticsComputer,
                        CargoAvailability = 0.9f,
                    },
                },
            };

            int idx = TaskBidSelection.PickWinnerIndex(bids, TaskType.PreciseWelding);
            Assert.AreEqual(0, idx);
        }

        [TestMethod]
        public void PickWinnerIndex_LogisticsTask_LowerScoreWins()
        {
            List<CollectedBid> bids = new List<CollectedBid>
            {
                new CollectedBid
                {
                    SenderId = 300L,
                    Bid = new TaskBid
                    {
                        TaskId = 2,
                        EstimatedTime = 50f,
                        PathComplexity = 0.2f,
                        BidderKind = TaskBidderKind.LogisticsComputer,
                        CargoAvailability = 0.2f,
                    },
                },
                new CollectedBid
                {
                    SenderId = 400L,
                    Bid = new TaskBid
                    {
                        TaskId = 2,
                        EstimatedTime = 10f,
                        PathComplexity = 0.1f,
                        BidderKind = TaskBidderKind.LogisticsComputer,
                        CargoAvailability = 0.5f,
                    },
                },
            };

            int idx = TaskBidSelection.PickWinnerIndex(bids, TaskType.PreciseFetch);
            Assert.AreEqual(1, idx);
        }

        [TestMethod]
        public void PickWinnerIndex_TieBreaksOnLowerSenderId()
        {
            float scoreBase = TaskBidSelection.ScoreBid(new TaskBid
            {
                TaskId = 3,
                EstimatedTime = 5f,
                PathComplexity = 0.1f,
                BidderKind = TaskBidderKind.Drone,
                CargoAvailability = 0.5f,
            });

            List<CollectedBid> bids = new List<CollectedBid>
            {
                new CollectedBid
                {
                    SenderId = 20L,
                    Bid = new TaskBid
                    {
                        TaskId = 3,
                        EstimatedTime = 5f,
                        PathComplexity = 0.1f,
                        BidderKind = TaskBidderKind.Drone,
                        CargoAvailability = 0.5f,
                    },
                },
                new CollectedBid
                {
                    SenderId = 10L,
                    Bid = new TaskBid
                    {
                        TaskId = 3,
                        EstimatedTime = 5f,
                        PathComplexity = 0.1f,
                        BidderKind = TaskBidderKind.Drone,
                        CargoAvailability = 0.5f,
                    },
                },
            };

            Assert.IsTrue(scoreBase > 0f);
            int idx = TaskBidSelection.PickWinnerIndex(bids, TaskType.PreciseWelding);
            Assert.AreEqual(1, idx);
        }

        [TestMethod]
        public void PickWinnerIndex_Empty_ReturnsMinusOne()
        {
            List<CollectedBid> bids = new List<CollectedBid>();
            int idx = TaskBidSelection.PickWinnerIndex(bids, TaskType.PreciseWelding);
            Assert.AreEqual(-1, idx);
        }

        [TestMethod]
        public void PickWinnerIndex_NullList_ReturnsMinusOne()
        {
            int idx = TaskBidSelection.PickWinnerIndex(null, TaskType.PreciseWelding);
            Assert.AreEqual(-1, idx);
        }

        [TestMethod]
        public void PickWinnerIndex_OnlyIneligible_ReturnsMinusOne()
        {
            List<CollectedBid> bids = new List<CollectedBid>
            {
                new CollectedBid
                {
                    SenderId = 100L,
                    Bid = new TaskBid
                    {
                        TaskId = 1,
                        EstimatedTime = 1f,
                        PathComplexity = 0f,
                        BidderKind = TaskBidderKind.LogisticsComputer,
                        CargoAvailability = 1f,
                    },
                },
            };

            int idx = TaskBidSelection.PickWinnerIndex(bids, TaskType.PreciseWelding);
            Assert.AreEqual(-1, idx);
        }

        [TestMethod]
        public void PickWinnerIndex_Blacklist_SkipsBetterScoringBidder()
        {
            HashSet<long> blacklist = new HashSet<long>();
            blacklist.Add(1L);

            List<CollectedBid> bids = new List<CollectedBid>
            {
                new CollectedBid
                {
                    SenderId = 1L,
                    Bid = new TaskBid
                    {
                        TaskId = 5,
                        EstimatedTime = 1f,
                        PathComplexity = 0f,
                        BidderKind = TaskBidderKind.Drone,
                        CargoAvailability = 1f,
                    },
                },
                new CollectedBid
                {
                    SenderId = 2L,
                    Bid = new TaskBid
                    {
                        TaskId = 5,
                        EstimatedTime = 50f,
                        PathComplexity = 0.5f,
                        BidderKind = TaskBidderKind.Drone,
                        CargoAvailability = 0f,
                    },
                },
            };

            int idx = TaskBidSelection.PickWinnerIndex(bids, TaskType.PreciseWelding, blacklist);
            Assert.AreEqual(1, idx);
        }

        [TestMethod]
        public void ScoreBid_MatchesWeightedFormula()
        {
            float s = TaskBidSelection.ScoreBid(new TaskBid
            {
                TaskId = 1,
                EstimatedTime = 5f,
                PathComplexity = 0.1f,
                BidderKind = TaskBidderKind.Drone,
                CargoAvailability = 0.5f,
            });
            Assert.AreEqual(4.4f, s, 0.0001f);
        }

        [TestMethod]
        public void IsBidderEligibleForTask_Drone_AlwaysEligible_ForWeldingAndFetch()
        {
            Assert.IsTrue(TaskBidSelection.IsBidderEligibleForTask(TaskBidderKind.Drone, TaskType.PreciseWelding));
            Assert.IsTrue(TaskBidSelection.IsBidderEligibleForTask(TaskBidderKind.Drone, TaskType.PreciseFetch));
        }

        [TestMethod]
        public void IsBidderEligibleForTask_LogisticsComputer_OnlyForLogisticsTypes()
        {
            Assert.IsFalse(TaskBidSelection.IsBidderEligibleForTask(TaskBidderKind.LogisticsComputer, TaskType.PreciseWelding));
            Assert.IsTrue(TaskBidSelection.IsBidderEligibleForTask(TaskBidderKind.LogisticsComputer, TaskType.PreciseFetch));
        }
    }
}
