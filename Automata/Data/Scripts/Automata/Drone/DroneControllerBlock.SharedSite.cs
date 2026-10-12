using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// Shared job sites: the orchestrator flags a weld / grind / mine award (AuctionWinnerAnnouncement.SharedSite) when the job
    /// went to several drones, or another weld / grind job it awarded is still being worked near it. Such a drone
    /// (managed by orchestrator only; stand-alone and player-managed drones never have it) watches the other drones
    /// near its site every 10 ticks (route legs are otherwise checked every 20 ticks, and the final approach, the work
    /// and the back-out not at all): when its next move would take its hull within SITE_MARGIN of another drone's hull,
    /// or another drone's move would take it into this one, it holds still (flight control keeps it in place, the
    /// stall clocks wait) until the way is clear.
    ///  - Who yields: a drone the other doesn't know about (not watching) always yields to it; between two watching
    ///    drones whose moves cross, the lower entity id yields. A drone in the way that isn't moving (working a face):
    ///    the moving one yields.
    ///  - A drone that sees a flagged drone working near its own site starts watching too (it learns the site is shared).
    ///  - Yielded SITE_YIELD_MAX_TICKS on one approach and the other drone still in the way: another face (RedispatchToolTo),
    ///    elsewhere it goes on (the route checks see a drone that stays put).
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region Timing
        private const int SITE_YIELD_MAX_TICKS = 45 * 60;
        #endregion
        private const double SITE_WATCH_RADIUS = 40;     // m from the work site: watching
        private const double SITE_NEIGHBOUR_RADIUS = 60; // m: other drones looked at / a flagged drone's site counted as shared
        private const double SITE_MARGIN = 3;            // m between the hulls
        private const double SITE_LOOKAHEAD = 10;        // m of its own next move checked
        private const double SITE_OTHER_SECONDS = 3;     // s of another drone's motion checked

        // Every initialized drone (server): the shared-site watch looks at the others
        private static readonly List<DroneControllerBlock> allDrones = new List<DroneControllerBlock>();

        private bool jobSharedSite;          // this job's site is shared (award flag, or learnt from a flagged neighbour)
        private bool siteYielding;           // holding still for another drone (flight control holds position)
        private int siteYieldStart;
        private long siteYieldFor;
        private Vector3D siteWatchCenter;    // the current work site (DispatchToolTo)
        private bool siteWatchCenterSet;

        /// <summary>
        /// Recalls every drone that is working one of the jobs 'jobIds' announced by 'announcerId' (an orchestrator's
        /// player jobs). Returns how many.
        /// </summary>
        public static int RecallJobs(long announcerId, ICollection<uint> jobIds, string by)
        {
            int n = 0;
            for (int i = allDrones.Count - 1; i >= 0; i--)
            {
                var d = allDrones[i];
                if (d.jobAnnouncerId != announcerId || d.currentJob == null || !jobIds.Contains(d.currentJob.JobId)) continue;
                if (d.Recall(by)) n++;
            }
            return n;
        }

        private static void RegisterDrone(DroneControllerBlock d)
        {
            if (!allDrones.Contains(d)) allDrones.Add(d);
        }

        private static void UnregisterDrone(DroneControllerBlock d)
        {
            allDrones.Remove(d);
        }

        /// <summary>Session unload: the registry is static.</summary>
        public static void ClearDroneRegistry()
        {
            allDrones.Clear();
        }

        // Watching right now: orchestrator job (weld / grind), site shared, near the site
        private bool WatchingSharedSite
        {
            get
            {
                return jobSharedSite && currentJob != null && cPhase == ConstructionPhase.Running && jobSource == JobSource.Orchestrator
                    && settings != null && settings.OperationMode == OperationMode.ManagedByOrchestrator
                    && IsSiteJob(currentJob.JobType)
                    && siteWatchCenterSet && Vector3D.DistanceSquared(flightState.Position, siteWatchCenter) < SITE_WATCH_RADIUS * SITE_WATCH_RADIUS;
            }
        }

        // Jobs worked at a site several drones may share: weld, grind, mine (one deposit)
        private static bool IsSiteJob(Orchestrator.JobType t)
        {
            return t == Orchestrator.JobType.Weld || t == Orchestrator.JobType.Grind || t == Orchestrator.JobType.MineOre;
        }

        private double HullRadius
        {
            get
            {
                return localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.HalfExtents.Length()
                     : block != null && block.CubeGrid != null ? block.CubeGrid.WorldVolume.Radius : 3;
            }
        }

        // The hull centre's next move (current order), at most SITE_LOOKAHEAD long; false when it isn't going anywhere
        private bool NextMove(out Vector3D from, out Vector3D to)
        {
            from = HullCentreWorld;
            to = from;
            var o = activeFlightOrder;
            if (o == null || o.Arrived) return false;
            Vector3D target = o.Target.ToVector3D() + (from - ReferencePoint(o));
            Vector3D d = target - from;
            double len = d.Length();
            if (len < 0.3) return false;
            to = from + d / len * Math.Min(len, SITE_LOOKAHEAD);
            return true;
        }

        private static double SegmentDistance(Vector3D a, Vector3D b, Vector3D p)
        {
            Vector3D ab = b - a;
            double len2 = ab.LengthSquared();
            double t = len2 > 1e-6 ? MathHelperD.Clamp(Vector3D.Dot(p - a, ab) / len2, 0, 1) : 0;
            return Vector3D.Distance(a + ab * t, p);
        }

        /// <summary>
        /// UpdateBeforeSimulation10, server, AI on: the shared-site watch. Sets / clears siteYielding; learns a shared site
        /// from a flagged neighbour.
        /// </summary>
        private void UpdateSharedSite10()
        {
            if (!jobSharedSite) LearnSharedSite();
            if (!WatchingSharedSite)
            {
                if (siteYielding) EndYield("left the site");
                return;
            }
            CaptureFlightState();
            Vector3D myFrom, myTo;
            bool moving = NextMove(out myFrom, out myTo);
            double myR = HullRadius;
            DroneControllerBlock blocker = null, holder = null;
            for (int i = 0; i < allDrones.Count; i++)
            {
                var other = allDrones[i];
                if (other == this || other.block == null || other.block.Closed || !other.initialized || other.isParked) continue;
                if (droneGrids.Contains(other.block.CubeGrid)) continue;
                Vector3D oc = other.HullCentreWorld;
                if (Vector3D.DistanceSquared(oc, myFrom) > SITE_NEIGHBOUR_RADIUS * SITE_NEIGHBOUR_RADIUS) continue;
                double clear = myR + other.HullRadius + SITE_MARGIN;
                bool mineHitsIt = moving && SegmentDistance(myFrom, myTo, oc) < clear;
                Vector3D ov = other.flightState.LinearVelocity;
                bool itHitsMe = ov.LengthSquared() > 0.25 && SegmentDistance(oc, oc + ov * SITE_OTHER_SECONDS, myFrom) < clear;
                if (!mineHitsIt && !itHitsMe) continue;
                // Who yields: to a drone that doesn't watch; between watching drones whose moves cross, the lower id;
                // moving into a drone that stays put: the mover
                bool otherWatches = other.WatchingSharedSite;
                // It holds for this one already: go on; if this move runs into where it holds, a new way (below)
                if (otherWatches && other.siteYielding && other.siteYieldFor == _entityId)
                {
                    if (mineHitsIt) holder = other;
                    continue;
                }
                bool yield = !otherWatches ? mineHitsIt
                           : mineHitsIt && itHitsMe ? _entityId < other._entityId
                           : mineHitsIt;
                if (!yield) continue;
                blocker = other;
                break;
            }
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (blocker == null)
            {
                if (siteYielding) EndYield("way clear");
                if (holder != null) WayAround(holder);
                return;
            }
            if (!siteYielding || siteYieldFor != blocker._entityId)
            {
                siteYielding = true;
                siteYieldStart = now;
                siteYieldFor = blocker._entityId;
                Log.Debug("Drone {0}: shared site: holding for drone {1} ({2:F1} m away)", _entityId, blocker._entityId,
                          Vector3D.Distance(blocker.HullCentreWorld, myFrom));
            }
            // Holding is no stall: the progress clocks wait
            taskProgressFrame = now;
            siteProgressFrame = now;
            if (now - siteYieldStart > SITE_YIELD_MAX_TICKS) YieldTooLong(blocker);
        }

        private void EndYield(string why)
        {
            if (!siteYielding) return;
            siteYielding = false;
            Log.Debug("Drone {0}: shared site: going on after {1:F0} s ({2})", _entityId,
                      (MyAPIGateway.Session.GameplayFrameCounter - siteYieldStart) / 60.0, why);
            siteYieldFor = 0;
        }

        // The other drone stays in the way: another face on the way to a site, else on (route checks see it)
        private void YieldTooLong(DroneControllerBlock blocker)
        {
            EndYield("drone " + blocker._entityId + " stays in the way");
            if (taskIndex >= currentJob.Tasks.Count || currentJob.Tasks[taskIndex].Type != Orchestrator.TaskType.NavigateToolTo
                || taskIndex + 1 >= currentJob.Tasks.Count) return;
            var nav = currentJob.Tasks[taskIndex];
            var tool = currentJob.Tasks[taskIndex + 1];
            var again = RedispatchToolTo(ref nav, ref tool, true, "drone " + blocker._entityId + " stays in the way");
            if (again != Orchestrator.TaskFailure.None) routeFailed = true;   // the task fails Unreachable on its next step
        }

        // A drone holds for this one right where this one's move goes: on the way to a site, the site chosen again from
        // here (the work-site check and the route see the drone that holds); elsewhere on (route checks see it)
        private void WayAround(DroneControllerBlock holder)
        {
            if (taskIndex >= currentJob.Tasks.Count || currentJob.Tasks[taskIndex].Type != Orchestrator.TaskType.NavigateToolTo
                || taskIndex + 1 >= currentJob.Tasks.Count) return;
            var nav = currentJob.Tasks[taskIndex];
            var tool = currentJob.Tasks[taskIndex + 1];
            var again = RedispatchToolTo(ref nav, ref tool, false, "drone " + holder._entityId + " holds in the way");
            if (again != Orchestrator.TaskFailure.None) routeFailed = true;
        }

        // A flagged drone working near this drone's site: the site is shared (it may not have been told)
        private void LearnSharedSite()
        {
            if (currentJob == null || jobSource != JobSource.Orchestrator || settings == null || settings.OperationMode != OperationMode.ManagedByOrchestrator
                || !IsSiteJob(currentJob.JobType) || !siteWatchCenterSet) return;
            for (int i = 0; i < allDrones.Count; i++)
            {
                var other = allDrones[i];
                if (other == this || !other.jobSharedSite || !other.siteWatchCenterSet || other.currentJob == null) continue;
                if (Vector3D.DistanceSquared(other.siteWatchCenter, siteWatchCenter) > SITE_NEIGHBOUR_RADIUS * SITE_NEIGHBOUR_RADIUS) continue;
                jobSharedSite = true;
                Log.Debug("Drone {0}: shared site: drone {1} works near this site, watching too", _entityId, other._entityId);
                return;
            }
        }
    }
}
