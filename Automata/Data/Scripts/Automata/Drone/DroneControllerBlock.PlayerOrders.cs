using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// A player's designator order for a tagged drone comes first (2026-11-06). Ore scouts and mining drones bid on a spot
    /// check, area sweep or quarry preparation auctioned for them alone (Auction.OnlyDroneEntityId) even while busy; on the
    /// award the current job is dropped (reported Aborted, Preempted: the orchestrator auctions it again) and the order
    /// starts:
    ///  - in a bore / a quarry (its Mine started, or the BackOut after it): out along the bore first (the old Mine + BackOut
    ///    go in front of the new job, which starts at that BackOut with the bore path kept);
    ///  - a mining job with anything aboard (stone, ice, ore): a dock + clear at its clear port before the order
    ///    (none reachable / no room there: straight to the order);
    ///  - anything else (scouting, a quarry preparation, housekeeping, flying home): dropped, the order starts at once.
    /// Not while out of power / fuel, in error or in combat.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private uint preemptBidAuctionId;   // the player's order bid on while busy (its award takes over)
        private bool jobPreempted;          // the job report being sent is a preempted job's
        private bool keepBorePath;          // StartJob keeps the bore path (a preempted bore backs out first)
        private bool clearForPlayerOrder;   // PrepareCargoForJob clears before this designator job too

        // Player orders that take a tagged scout / miner off its job
        private static bool IsPlayerOverrideJob(Orchestrator.JobType t)
        {
            return t == Orchestrator.JobType.DesignatorScoutPoint || t == Orchestrator.JobType.DesignatorScoutArea || t == Orchestrator.JobType.DesignatorQuarry;
        }

        // Busy, but a player's order may take it over: an ore scout / mining drone that can fly and is not short of power,
        // in error or fighting
        private bool PlayerOrderCanPreempt
        {
            get
            {
                return IsServer && initialized && settings != null && settings.IsEnabled
                    && settings.OperationMode == OperationMode.ManagedByOrchestrator
                    && (settings.BehaviourProfile == BehaviourProfile.IsOreScout || settings.BehaviourProfile == BehaviourProfile.IsMiningDrone)
                    && combatPhase == CombatPhase.Idle && designatorCombat == DesignatorCombat.None
                    && currentState != State.Error && !needsBatteryRecharge && !needsHydrogenRefuel && powerRemnant == null
                    && (capabilities & (Capabilities.CanFlyAtmosphere | Capabilities.CanFlySpace)) != 0;
            }
        }

        /// <summary>
        /// Busy (ReadAuctions): a player's order auctioned for this drone alone is bid on anyway. True when it bid.
        /// </summary>
        private bool TryPlayerOrderBid(MessageQueue queue, int now)
        {
            if (!PlayerOrderCanPreempt) return false;
            for (int i = 0; i < auctionInbox.Count; i++)
            {
                var auction = auctionInbox[i].Payload;
                if (auction == null || auction.OnlyDroneEntityId != _entityId || !IsPlayerOverrideJob(auction.JobType)) continue;
                if (auction.ExpirationTime < DateTime.UtcNow || biddedAuctions.ContainsKey(auction.AuctionId)) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, auctionInbox[i].SenderOwnerId)) continue;
                string why = WhyNotJob(auction);
                if (why != null)
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: player's order {1} ({2}): no bid: {3}", _entityId, auction.AuctionId, auction.JobType, why);
                    continue;
                }
                SendBid(queue, auction);
                biddedAuctions[auction.AuctionId] = now;
                pendingBidAuctionId = auction.AuctionId;
                pendingBidSeenFrame = now;
                pendingBidExpires = auction.ExpirationTime.AddSeconds(30);
                preemptBidAuctionId = auction.AuctionId;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: player's order {1} ({2}, job {3}): bid while busy ({4})", _entityId, auction.AuctionId, auction.JobType, auction.JobId, WhyNotAvailable());
                return true;
            }
            return false;
        }

        /// <summary>
        /// The award of a player's order bid on while busy: the current job is dropped (reported Aborted, Preempted). Returns
        /// the tasks to run first (the bore's Mine + BackOut: the new job starts at the BackOut), or null; 'clear': a mining
        /// job left something aboard to clear before the order.
        /// </summary>
        private List<Orchestrator.Task> PreemptForPlayerOrder(Orchestrator.Job order, out bool clear)
        {
            clear = false;
            List<Orchestrator.Task> prefix = null;
            string label = currentJob != null ? Orchestrator.JobTypeExtensions.Label(currentJob.JobType, currentJob.WeldCategory) : "-";
            if (currentJob != null && currentJob.Tasks != null && taskIndex < currentJob.Tasks.Count)
            {
                var cur = currentJob.Tasks[taskIndex];
                bool inMine = cur.Type == Orchestrator.TaskType.Mine && taskStarted;
                bool inBack = cur.Type == Orchestrator.TaskType.BackOut && taskStarted && taskIndex > 0
                              && currentJob.Tasks[taskIndex - 1].Type == Orchestrator.TaskType.Mine;
                int mi = inMine ? taskIndex : taskIndex - 1;
                if ((inMine || inBack) && borePath.Count > 0 && mi + 1 < currentJob.Tasks.Count
                    && currentJob.Tasks[mi + 1].Type == Orchestrator.TaskType.BackOut)
                    prefix = new List<Orchestrator.Task> { currentJob.Tasks[mi], currentJob.Tasks[mi + 1] };
                clear = currentJob.JobType == Orchestrator.JobType.MineOre && HasCargoToClear();
            }
            Report(MyStrings.Drone_PlayerOrderTakesOver, order.JobType, currentJob != null ? currentJob.JobId : 0, label);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: player's order ({1}) takes over job {2} ({3}, source {4}, task {5}){6}{7}", _entityId, order.JobType, currentJob != null ? currentJob.JobId : 0, label, jobSource, currentJob != null && taskIndex < currentJob.Tasks.Count ? currentJob.Tasks[taskIndex].Type.ToString() : "-", prefix != null ? ": backing out of the bore first" : "", clear ? ": clearing the cargo first" : "");
            // Tools off, drill mode back, the bore's state kept (its path: the way out)
            if (boring || quarryActive || boreClearingSet) FinishMine();
            ToolsOff();
            taskScanner.Cancel();
            if (parkPhase != ParkPhase.None) EndPark(Loc.T(MyStrings.Drone_PreemptedByPlayerOrder));
            if (jobSource == JobSource.Orchestrator && currentJob != null)
            {
                jobPreempted = true;
                ReportJobOutcome(JobOutcome.Aborted, Loc.T(MyStrings.Drone_PreemptedByPlayerOrder));
                jobPreempted = false;
            }
            jobAbortReason = null;
            CancelPendingRoute();
            ClearFlightOrder();
            currentJob = null;
            jobSource = JobSource.None;
            constructionOrder = null;
            constructionLegs.Clear();
            ResetStrafe();
            cPhase = ConstructionPhase.Idle;
            owesReturn = false;
            return prefix;
        }

        /// <summary>Starts the player's order after PreemptForPlayerOrder: the bore's way out and / or a clear trip first.</summary>
        private void StartPlayerOrder(Orchestrator.Job job, List<Orchestrator.Task> prefix, bool clear)
        {
            if (prefix == null)
            {
                clearForPlayerOrder = clear;
                StartJob(job, JobSource.Orchestrator);
                clearForPlayerOrder = false;
                return;
            }
            job.Tasks.InsertRange(0, prefix);
            if (clear) InsertClearTrip(job, prefix.Count);
            keepBorePath = true;
            StartJob(job, JobSource.Orchestrator, 1);   // at the BackOut: out along the bore
            keepBorePath = false;
        }

        // A dock + clear at the clear port into 'job' at 'at' (none reachable / no room there: nothing)
        private bool InsertClearTrip(Orchestrator.Job job, int at)
        {
            var port = ClearPort();
            if (port == null || ClearPortBlocked(port.EntityId)) return false;
            job.Tasks.Insert(at, ClearCargoTask());
            job.Tasks.Insert(at, DockTaskFor(port));
            if (Log.DebugEnabled) Log.Debug("Drone {0}: job {1}: cargo aboard, clearing it at {2} first", Entity.EntityId, job.JobId, port.CustomName);
            return true;
        }
    }
}
