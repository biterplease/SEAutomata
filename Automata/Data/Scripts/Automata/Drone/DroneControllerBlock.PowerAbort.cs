using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Low power short circuit (2026-11-07: drones ran flat in the field). Under its battery / hydrogen refuel threshold a
    /// drone drops whatever job it runs, every source (an orchestrator's - reported Aborted, auctioned again -, stand-alone,
    /// a player's order, its own errand), and goes to charge (MonitorPower: home, else any free connector).
    ///  - In a block / bore (a tool task with its BackOut next, or that BackOut under way): the job is cut after the
    ///    BackOut and runs on as the drone's own errand (Housekeeping: nothing more reported, no bids), so the drone backs
    ///    out first. Anywhere else the job just ends (StopJob).
    ///  - Its cargo: the job's dock + unload pairs still ahead (powerRemnant, memory only: a game load forgets it), when
    ///    anything is aboard. Once the levels are back over the operational thresholds the drone flies them (an errand:
    ///    dock, unload, home) before it bids or scans again; nothing aboard by then: dropped. No pairs ahead: the cargo
    ///    is cleared as any leftover (TryStartHousekeeping, after the charge).
    /// </summary>
    public partial class DroneControllerBlock
    {
        private List<Orchestrator.Task> powerRemnant;      // dock + unload pairs of a job dropped for power (null: none)
        private Orchestrator.JobType powerRemnantType;
        private uint powerRemnantJobId;
        private Orchestrator.Job powerAbortJob;            // the job cut to its back-out (not dropped twice)

        private bool LowPower { get { return needsBatteryRecharge || needsHydrogenRefuel; } }

        /// <summary>Under a threshold (MonitorPower, UpdateOrchestratorJobs, stand-alone construction): the job dropped.</summary>
        private void PowerShortCircuit()
        {
            if (!IsServer) return;
            string reason = Loc.T(MyStrings.Drone_LowPower);
            var job = currentJob;
            if (job == null)
            {
                if (cPhase != ConstructionPhase.Idle) StopJob(reason);   // (a stand-alone scan)
                return;
            }
            if (ReferenceEquals(job, powerAbortJob)) return;   // backing out already
            CapturePowerRemnant(job);
            Report(MyStrings.Drone_JobAbandonedLowPower);
            if (jobSource == JobSource.Orchestrator) ReportJobOutcome(JobOutcome.Aborted, reason);   // the orchestrator re-auctions it
            Automata.Designator.QuarrySites.Release(_entityId);
            // In a block / bore: straight back out first, the job cut after that
            int keep = BackOutIndex();
            jobSource = JobSource.Housekeeping;   // its own errand from here: nothing more reported, no clear trip
            if (keep < 0)
            {
                StopJob(reason);
                return;
            }
            powerAbortJob = job;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: low power: job {1} dropped, backing out first", _entityId, job.JobId);
            CutJobToBackOut(keep, false);
        }

        /// <summary>The current job's BackOut to run before it is dropped: the tool is in a block / bore / quarry shaft (a
        /// tool task with its BackOut next) or that BackOut is under way. -1: none.</summary>
        private int BackOutIndex()
        {
            var job = currentJob;
            if (job == null || job.Tasks == null || cPhase != ConstructionPhase.Running) return -1;
            var cur = taskIndex >= 0 && taskIndex < job.Tasks.Count ? job.Tasks[taskIndex].Type : Orchestrator.TaskType.None;
            if (cur == Orchestrator.TaskType.BackOut && taskStarted) return taskIndex;
            if (Orchestrator.Task.KindOf(cur) == Orchestrator.TaskKind.Tool && IsBackOutAt(taskIndex + 1)) return taskIndex + 1;
            return -1;
        }

        /// <summary>
        /// The job cut after its BackOut at 'keep' (BackOutIndex) and run on as the drone's own errand (Housekeeping: the
        /// caller has reported it; nothing more is), 'home': a ReturnHome after it (a recall), else nothing (low power:
        /// MonitorPower sends it to charge once it is out).
        /// </summary>
        private void CutJobToBackOut(int keep, bool home)
        {
            var job = currentJob;
            jobSource = JobSource.Housekeeping;
            if (boring || quarryActive || boreClearingSet) FinishMine();   // drill mode back, the bore's way out kept
            ToolsOff();
            if (keep + 1 < job.Tasks.Count) job.Tasks.RemoveRange(keep + 1, job.Tasks.Count - keep - 1);
            if (home) job.Tasks.Add(Orchestrator.Task.ReturnHome());
            noFieldContinuation = true;
            homeAfterBackOut = false;
            if (keep == taskIndex) return;   // under way
            NextTask(1);
            RunTasks();
        }

        // The job's dock + unload pairs from the current task on, when anything is aboard
        private void CapturePowerRemnant(Orchestrator.Job job)
        {
            if (job.Tasks == null || !HasCargoToClear()) return;
            var rest = new List<Orchestrator.Task>();
            for (int i = Math.Max(1, taskIndex); i < job.Tasks.Count; i++)
                if (job.Tasks[i].Type == Orchestrator.TaskType.UnloadInventory && job.Tasks[i - 1].Type == Orchestrator.TaskType.DockAt)
                {
                    rest.Add(job.Tasks[i - 1]);
                    rest.Add(job.Tasks[i]);
                }
            if (rest.Count == 0) return;   // (an earlier remnant, if any, stays)
            powerRemnant = rest;
            powerRemnantType = job.JobType;
            powerRemnantJobId = job.JobId;
            Report(MyStrings.Drone_PowerCargoKept, job.JobId, RemnantPortName());
            if (Log.DebugEnabled) Log.Debug("Drone {0}: low power: job {1} ({2}): {3} unload(s) kept for after the recharge", _entityId, job.JobId, job.JobType, rest.Count / 2);
        }

        /// <summary>Levels back (MonitorPower): the dropped job's cargo delivered, as the drone's own errand.</summary>
        private void StartPowerRemnant()
        {
            if (powerRemnant == null || !IsServer || currentJob != null || cPhase != ConstructionPhase.Idle || preflightStage != 0
                || Grounded || combatPhase != CombatPhase.Idle || currentState == State.Error) return;
            if (!HasCargoToClear())
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: job {1}'s cargo: nothing aboard any more, dropped", _entityId, powerRemnantJobId);
                powerRemnant = null;
                return;
            }
            var job = new Orchestrator.Job { JobType = powerRemnantType, JobId = powerRemnantJobId };
            job.Tasks.AddRange(powerRemnant);
            job.Tasks.Add(Orchestrator.Task.ReturnHome());
            Report(MyStrings.Drone_PowerCargoDelivering, powerRemnantJobId, RemnantPortName());
            powerRemnant = null;
            if (parkPhase != ParkPhase.None) EndPark(null);
            StartJob(job, JobSource.Housekeeping);
        }

        private string RemnantPortName()
        {
            if (powerRemnant == null || powerRemnant.Count == 0) return "?";
            IMyEntity e;
            var t = powerRemnant[0];
            return t.TargetEntityId != 0 && MyAPIGateway.Entities.TryGetEntityById(t.TargetEntityId, out e) && e is IMyTerminalBlock
                ? ((IMyTerminalBlock)e).CustomName : t.TargetEntityId.ToString();
        }
    }
}
