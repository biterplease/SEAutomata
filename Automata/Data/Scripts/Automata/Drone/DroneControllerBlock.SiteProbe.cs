using System.Collections.Generic;

using VRage.Utils;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Drone
{
    /// <summary>
    /// Weld jobs, before the dock / load: are any of its blocks reachable at all? Each block's work site search (the
    /// faces, the approaches, the way in - not the route there) runs in order until one has a site, within
    /// SITE_PROBE_MS. None: the job is given back at once, its blocks reported unreachable (the construction computer
    /// holds those back), instead of loading a full cargo, failing every block in 0 s and clearing the cargo again -
    /// over and over. Blocks found unreachable before the first good one are skipped when their turn comes (no second
    /// search). Every block the job found no way to goes into its outcome report (UnreachableGrids / Cells).
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const double SITE_PROBE_MS = 25;   // per job; past it: the job runs as before

        private readonly HashSet<KeyValuePair<long, Vector3I>> probedUnreachable = new HashSet<KeyValuePair<long, Vector3I>>();
        private readonly List<KeyValuePair<long, Vector3I>> jobUnreachable = new List<KeyValuePair<long, Vector3I>>();
        private bool siteProbing;   // ProbeJobSites running (quiet about parts not aboard yet)

        /// <summary>False: no block of the weld job has a work site (all checked, all Unreachable).</summary>
        private bool ProbeJobSites(Orchestrator.Job job)
        {
            probedUnreachable.Clear();
            if (job == null || job.JobType != Orchestrator.JobType.Weld || job.Tasks == null || !HasTool(Orchestrator.TaskType.WeldBlock)) return true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int checkedCount = 0;
            bool strafeWas = strafeNext;
            strafeNext = false;
            siteProbing = true;
            try
            {
                for (int i = 0; i + 1 < job.Tasks.Count; i++)
                {
                    if (job.Tasks[i].Type != Orchestrator.TaskType.NavigateToolTo || job.Tasks[i + 1].Kind != Orchestrator.TaskKind.Tool) continue;
                    if (sw.Elapsed.TotalMilliseconds > SITE_PROBE_MS)
                    {
                        if (Log.DebugEnabled) Log.Debug("Drone {0}: pre-load check: {1} block(s) without a site, time up ({2:F1} ms): loading as planned", Entity.EntityId, probedUnreachable.Count, sw.Elapsed.TotalMilliseconds);
                        return true;
                    }
                    var nav = job.Tasks[i];
                    var tool = job.Tasks[i + 1];
                    // Built / gone meanwhile: skipped when its turn comes anyway. (Nothing aboard yet: parts don't count here.)
                    var state = ToolTargetState(ref tool);
                    if (state != Orchestrator.TaskFailure.None && state != Orchestrator.TaskFailure.NoPartsAboard
                        && state != Orchestrator.TaskFailure.NotAttached) continue;
                    checkedCount++;
                    var failure = DispatchToolTo(ref nav, ref tool, true);
                    if (failure != Orchestrator.TaskFailure.Unreachable)
                    {
                        if (probedUnreachable.Count > 0)
                            if (Log.DebugEnabled) Log.Debug("Drone {0}: pre-load check: {1} block(s) without a site before one with ({2:F1} ms)", Entity.EntityId, probedUnreachable.Count, sw.Elapsed.TotalMilliseconds);
                        return true;   // a site (or another problem the job's own run handles)
                    }
                    var key = new KeyValuePair<long, Vector3I>(tool.TargetEntityId, tool.TargetBlock.ToVector3I());
                    probedUnreachable.Add(key);
                    NoteUnreachable(key);
                }
            }
            finally { strafeNext = strafeWas; siteProbing = false; }
            if (checkedCount == 0) return true;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: pre-load check: none of {1} block(s) has a work site ({2:F1} ms): job given back", Entity.EntityId, checkedCount, sw.Elapsed.TotalMilliseconds);
            jobAbortReason = Loc.F(MyStrings.Drone_NoneOfItsBlockReachable, checkedCount);
            return false;
        }

        /// <summary>NavigateToolTo starting: its block was found unreachable by the pre-load check.</summary>
        private bool ProbedUnreachable(ref Orchestrator.Task tool)
        {
            return probedUnreachable.Count > 0
                   && probedUnreachable.Contains(new KeyValuePair<long, Vector3I>(tool.TargetEntityId, tool.TargetBlock.ToVector3I()));
        }

        private void NoteUnreachable(KeyValuePair<long, Vector3I> key)
        {
            if (key.Key == 0 || jobUnreachable.Contains(key)) return;
            jobUnreachable.Add(key);
        }

        // The outcome report: the blocks it found no way to (the computer holds those, not the whole job)
        private void FillUnreachable(VirtualNetwork.JobStatusReport r)
        {
            r.PerBlock = true;
            if (jobUnreachable.Count == 0) return;
            r.UnreachableGrids = new List<long>(jobUnreachable.Count);
            r.UnreachableCells = new List<Vector3IData>(jobUnreachable.Count);
            for (int i = 0; i < jobUnreachable.Count; i++)
            {
                r.UnreachableGrids.Add(jobUnreachable[i].Key);
                r.UnreachableCells.Add(Vector3IData.FromVector3I(jobUnreachable[i].Value));
            }
            // The reason names the last failure only (a later "NotAttached" hid the unreachable ones)
            if (r.Reason != null && r.Reason.IndexOf("Unreachable", System.StringComparison.OrdinalIgnoreCase) < 0)
                r.Reason = Loc.F(MyStrings.Drone_BlockUnreachable, r.Reason, jobUnreachable.Count);
        }
    }
}
