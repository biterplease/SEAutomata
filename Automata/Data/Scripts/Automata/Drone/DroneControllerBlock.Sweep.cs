using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Drone
{
    /// <summary>
    /// Scout area sweep sector (Orchestrator.TaskType.Sweep). The drones of one order set off sector x SWEEP_STAGGER_TICKS
    /// apart (side by side they blocked each other's first leg). The drone flies towards the area with a normal route; within
    /// SweepPlanner.MAP_RANGE of the asteroid / the spot it maps its whole sector's path at once (SweepPlanner: coarse
    /// voxels / the heightmap, beyond its instruments, a few ms per update; asteroid: the order's orbits, mapped once for
    /// all its drones - Pathfinding.SweepCache - its own first, the rest while it flies), then flies it as one route: a planned
    /// stretch from where it is to the path's first point, then the path's points as legs straight from the map (no
    /// planner, no Min altitude, belly to the surface), passed through, an ore scan at every scan point passed.
    /// Look-ahead along the path: grids, trees and other entities only (the rock / terrain is what the path was made of);
    /// something in the way: a hop over it (straight up from here, across, down onto the path past it), else a planned
    /// way round to a point past it. Pressed against something / no progress: on from a point further on, a few times.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private enum SweepPhase : byte { None, Approach, Flying }

        #region Timing
        private const double SWEEP_MAP_MS = 3.0;            // ms of mapping per task update (every 10 ticks)
        private const int SWEEP_STAGGER_TICKS = 60;         // the drones of one sweep set off this far apart (x sector)
        private const int SWEEP_WATCH_EVERY = 2;            // task updates between look-aheads along the path (20 ticks)
        #endregion
        private const double SWEEP_APPROACH_OUT = 30;       // m above the clearance: the point flown to before the map is made
        private const double SWEEP_JOIN_DIRECT = 30;        // m: closer to the path than this, straight onto it (no planner)
        private const double SWEEP_PASS = 15;               // m: a scan point this close counts as passed
        private const double SWEEP_SPEED_MAX = 30;          // m/s along the path at most
        private const double SWEEP_LOOK_MIN = 60;           // m looked ahead along the path at least...
        private const double SWEEP_LOOK_SECONDS = 4;        // ...or this many seconds of flight
        private const double SWEEP_DETOUR_PAST = 25;        // m past an obstacle (plus the drone's size) where the path is joined again
        private static readonly double[] SWEEP_HOPS = { 15, 30, 60, 100 };   // m over the path tried to hop an obstacle
        private const int SWEEP_DETOURS_MAX = 12;           // obstacles gone round per sweep
        private const int SWEEP_RESUMES_MAX = 8;            // fresh starts further on (pressed, stalled, no route) per sweep
        private const int SWEEP_SKIP = 4;                   // path points skipped per retry of a resume

        private SweepPhase sweepPhase;
        private readonly Automata.Pointer.SweepPlanner sweepMap = new Automata.Pointer.SweepPlanner();
        private Orchestrator.Task sweepTask;
        private bool sweepMapping;
        private int sweepPassed;            // index of the last path point passed
        private bool sweepScanDue;
        private int sweepScans, sweepDetours, sweepResumes, sweepWatchCounter;
        private Pathfinding.SweepCache.Entry sweepOrbits;   // asteroid: the order's orbits (shared)
        private int sweepLaunchAt;
        private bool sweepLaunched;

        /// <summary>Flying a mapped sweep path in gravity: the drone's Min altitude doesn't apply (BuildPathQuery).</summary>
        private bool SweepIgnoresMinAltitude
        {
            get { return sweepPhase == SweepPhase.Flying && flightState.InGravity && SweepTaskRunning; }
        }

        private bool SweepTaskRunning
        {
            get
            {
                return cPhase == ConstructionPhase.Running && currentJob != null && currentJob.Tasks != null && taskIndex < currentJob.Tasks.Count
                       && currentJob.Tasks[taskIndex].Type == Orchestrator.TaskType.Sweep;
            }
        }

        // The hull centre's height above the hull's underside (the drone flies belly to the surface)
        private double SweepHullOffset()
        {
            return localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.HalfExtents.Y : DroneRadius();
        }

        // The hull's half size across / along (the larger of the two)
        private double SweepHullHalfSize()
        {
            return localHullBox.Max.X >= localHullBox.Min.X ? Math.Max(localHullBox.HalfExtents.X, localHullBox.HalfExtents.Z) : DroneRadius();
        }

        private TaskStatus RunSweep(ref Orchestrator.Task task, bool starting)
        {
            CaptureFlightState();
            if (starting)
            {
                if (!OreDetectorWorking)
                {
                    Report(MyStrings.Drone_SurveyNeedsAWorkingOre, oreDetector == null ? Loc.T(MyStrings.Keen_None) : !oreDetector.Enabled ? Loc.T(MyStrings.Drone_SwitchedOff) : Loc.T(MyStrings.Drone_NotWorking));
                    return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                }
                sweepTask = task;
                sweepPhase = SweepPhase.Approach;
                sweepMapping = false;
                sweepPassed = -1;
                sweepScanDue = false;
                sweepScans = sweepDetours = sweepResumes = sweepWatchCounter = 0;
                sweepOrbits = null;
                // One after another: side by side (pasted, parked together) each saw the next one ahead on its first leg,
                // replanned until it gave up
                sweepLaunchAt = MyAPIGateway.Session.GameplayFrameCounter + Math.Max(0, task.TargetBlock.ToVector3I().X) * SWEEP_STAGGER_TICKS;
                sweepLaunched = false;
            }
            if (!sweepLaunched)
            {
                if (MyAPIGateway.Session.GameplayFrameCounter < sweepLaunchAt) return TaskStatus.Running;
                sweepLaunched = true;
                if (SweepFocusDistance() > Automata.Pointer.SweepPlanner.MAP_RANGE)
                {
                    var nav = Orchestrator.Task.NavigateTo(0, SweepApproachPoint(), SweepApproachPoint(), task.NaturalGravity);
                    var failure = DispatchNavigateTo(ref nav);
                    if (failure != Orchestrator.TaskFailure.None) return SweepOver(Fail(failure, TaskStatus.ToReturnHome));
                    return TaskStatus.Running;
                }
            }
            if (sweepScanDue && !taskScanner.Running) StartSweepScan();
            // The order's other orbits mapped while this drone flies its own (each orbit read once, by whoever is there)
            if (sweepOrbits != null && sweepPhase == SweepPhase.Flying)
            {
                sweepOrbits.LastUsed = DateTime.UtcNow.Ticks;   // kept while its drones fly it
                var sweeps = PathfindingManager.Instance != null ? PathfindingManager.Instance.Sweeps : null;
                if (sweeps != null && !sweepOrbits.Complete) sweeps.Step(sweepOrbits, -1, 0, SWEEP_MAP_MS);
            }
            return sweepPhase == SweepPhase.Approach ? SweepApproach() : SweepFly();
        }

        // On the way: the map starts within MAP_RANGE and is made a few ms per update while the drone flies on
        private TaskStatus SweepApproach()
        {
            if (!sweepMapping && SweepFocusDistance() <= Automata.Pointer.SweepPlanner.MAP_RANGE)
            {
                var t = sweepTask;
                Vector3I sec = t.TargetBlock.ToVector3I();
                string what;
                if (t.TargetEntityId != 0)
                {
                    // Asteroid: the order's orbits, shared by its drones (mapped by the first there, or found mapped)
                    var sweeps = PathfindingManager.Instance != null ? PathfindingManager.Instance.Sweeps : null;
                    sweepOrbits = sweeps != null ? sweeps.Get(t.TargetEntityId, t.Position.ToVector3D(), t.CompletionValue, sec.Y) : null;
                    what = sweepOrbits != null ? string.Format("asteroid {0}, the order's {1} orbit(s) ({2})", sweepOrbits.Name, sweepOrbits.Orbits.Count,
                                                               sweepOrbits.Complete ? "mapped already" : "mapping") : null;
                }
                else
                {
                    sweepMap.Begin(t.Position.ToVector3D(), t.ApproachFrom.ToVector3D(), sec.X, sec.Y, sec.Z, SweepHullOffset(), SweepHullHalfSize(), SweepBubble());
                    what = sweepMap.What;
                }
                if (what == null)
                {
                    Report(MyStrings.Drone_SweepTargetGone);
                    return SweepOver(Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.ToReturnHome));
                }
                sweepMapping = true;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep: {1} m from it, {2} (sector {3}/{4}, {5} m detector)", Entity.EntityId, SweepFocusDistance().ToString("F0"), what, sec.X + 1, sec.Y, sec.Z);
            }
            if (sweepMapping)
            {
                if (sweepOrbits != null)
                {
                    // Its own orbits first (the others' next, with what is left of the budget), then its path from them
                    var sweeps = PathfindingManager.Instance != null ? PathfindingManager.Instance.Sweeps : null;
                    Vector3I sec = sweepTask.TargetBlock.ToVector3I();
                    if (sweeps != null && !sweeps.Step(sweepOrbits, sec.X, sec.Y, SWEEP_MAP_MS)) return TaskStatus.Running;   // the approach flies on meanwhile
                    sweepMap.BeginOrbits(sweepOrbits, sec.X, sec.Y, sec.Z, SweepHullOffset(), SweepBubble(), HullCentreWorld);
                }
                else if (!sweepMap.Step(SWEEP_MAP_MS)) return TaskStatus.Running;   // the approach flies on meanwhile
                if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep mapped: {1}, {2} samples ({3} reads) in {4:F1} ms -> {5} points, {6:F0} m, {7} scans", Entity.EntityId, sweepMap.What, sweepMap.SampleCount, sweepMap.Reads, sweepOrbits != null ? sweepOrbits.Ms : sweepMap.Ms, sweepMap.Points.Count, sweepMap.Length, sweepMap.ScanCount);
                Report(MyStrings.Drone_SweepMapped, sweepMap.What, sweepMap.Length, sweepMap.Points.Count, sweepMap.ScanCount);
                if (sweepMap.Points.Count == 0) return SweepEnd(Loc.T(MyStrings.Drone_SweepNoWayOn));
                sweepPhase = SweepPhase.Flying;
                if (SweepFlyFrom(0, false, null)) return TaskStatus.Running;
                return SweepResume(SWEEP_SKIP, Loc.T(MyStrings.Drone_SweepNoWayOn));
            }
            // Still flying there (the normal route, watched as usual)
            if (routeFailed) return SweepOver(Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome));
            if (activeFlightOrder != null && !IsConstructionOrderActive()) return SweepOver(TaskStatus.Interrupted);
            if (!WatchProgress()) return SweepOver(Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.ToReturnHome));
            return TaskStatus.Running;
        }

        // Along the mapped path
        private TaskStatus SweepFly()
        {
            var path = sweepMap.Points;
            int last = path.Count - 1;
            var o = activeFlightOrder;
            if (constructionOrder != null && constructionOrder.Arrived && constructionOrder.SweepIndex == last)
            {
                SweepPassTo(last);
                if (sweepScanDue && !taskScanner.Running) StartSweepScan();
                if (sweepScanDue || taskScanner.Running) return TaskStatus.Running;   // the last scan first
                Report(MyStrings.Drone_SweepDone, sweepScans);
                if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep done: {1} scans, {2} obstacle(s) gone round, {3} fresh start(s)", Entity.EntityId, sweepScans, sweepDetours, sweepResumes);
                return SweepOver(TaskStatus.Done);
            }
            if (o == null || !IsConstructionOrderActive())
            {
                if (o != null) return SweepOver(TaskStatus.Interrupted);   // a player order, Go home...
                return SweepResume(SweepNextIndex(), Loc.T(MyStrings.Drone_SweepNoWayOn));   // the route stopped (no way on)
            }
            int k = o.SweepIndex;
            if (k > 0) SweepPassTo(k - 1);
            if (k >= 0 && k > sweepPassed && Vector3D.DistanceSquared(HullCentreWorld, path[k]) < SWEEP_PASS * SWEEP_PASS) SweepPassTo(k);
            if (sweepScanDue && !taskScanner.Running) StartSweepScan();
            Vector3D away;
            if (PushBlocked(out away))
            {
                pushBlockedTicks = 0;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep: pressed against something near point {1}", Entity.EntityId, Math.Max(k, 0));
                return SweepResume(SweepNextIndex() + 1, Loc.T(MyStrings.Drone_SweepPressed));
            }
            if (!WatchProgress()) return SweepResume(SweepNextIndex() + SWEEP_SKIP, Loc.T(MyStrings.Drone_SweepNoProgress));
            if (k >= 0 && ++sweepWatchCounter >= SWEEP_WATCH_EVERY)
            {
                sweepWatchCounter = 0;
                double dist;
                IMyEntity what;
                if (SweepLookAhead(k, out dist, out what)) return SweepDetour(k, dist, what);
            }
            return TaskStatus.Running;
        }

        // Path points up to 'index' passed: a scan is due if one of them asked for it
        private void SweepPassTo(int index)
        {
            var scans = sweepMap.Scans;
            index = Math.Min(index, scans.Count - 1);
            for (int p = sweepPassed + 1; p <= index; p++) if (scans[p]) sweepScanDue = true;
            if (index > sweepPassed) sweepPassed = index;
        }

        private int SweepNextIndex()
        {
            var o = activeFlightOrder;
            int k = o != null && o.SweepIndex >= 0 ? o.SweepIndex : sweepPassed + 1;
            return MathHelper.Clamp(k, 0, Math.Max(0, sweepMap.Points.Count - 1));
        }

        private void StartSweepScan()
        {
            sweepScanDue = false;
            if (!OreDetectorWorking) return;
            taskHits.Clear();
            double r = Math.Min(oreDetector.Range, MiningConfig.MaxScanRadius);
            if (!taskScanner.Begin(flightState.Position, r)) RecordSweep(flightState.Position, r, taskHits);   // nothing to scan: an empty sweep still says so
            sweepScans++;
        }

        /// <summary>
        /// Flies the path from point 'from': 'pre' points first (a hop), then a planned stretch onto it when 'plan' or when
        /// further than SWEEP_JOIN_DIRECT from it, then the path's points straight from the map. False: no way onto it.
        /// </summary>
        private bool SweepFlyFrom(int from, bool plan, List<Vector3D> pre)
        {
            var path = sweepMap.Points;
            if (from < 0 || from >= path.Count) return false;
            CaptureFlightState();
            RefreshDroneGrids();
            var pts = new List<Vector3D>();
            var fwds = new List<Vector3D>();
            var ups = new List<Vector3D>();
            var idx = new List<int>();
            Vector3D hullNow = HullCentreWorld;
            Vector3D prev = hullNow;
            int planned = 0;
            bool joinPlanned = false;
            if (pre != null)
                for (int i = 0; i < pre.Count; i++)
                {
                    pts.Add(pre[i]);
                    fwds.Add(SweepForward(prev, pre[i], sweepMap.Ups[from]));
                    ups.Add(sweepMap.Ups[from]);
                    idx.Add(-1);
                    prev = pre[i];
                }
            if (pre == null && (plan || Vector3D.Distance(hullNow, path[from]) > SWEEP_JOIN_DIRECT))
            {
                routeScratch.Clear();   // PlanPath uses it
                if (!PlanPath(hullNow, path[from], pts, false, true, flightState.WorldMatrix.Forward, flightState.WorldMatrix.Up, true))
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep: no way onto the path at point {1}", Entity.EntityId, from);
                    return false;
                }
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3D f, u;
                    LegAttitude(i, out f, out u);
                    fwds.Add(f);
                    ups.Add(u);
                    idx.Add(-1);
                }
                planned = pts.Count;
                joinPlanned = true;
                if (pts.Count > 0) prev = pts[pts.Count - 1];
            }
            for (int i = from; i < path.Count; i++)
            {
                Vector3D up = sweepMap.Ups[i];
                Vector3D a = i > from ? path[i - 1] : prev;
                Vector3D f = SweepForward(a, path[i], up);
                if (i == from && Vector3D.DistanceSquared(a, path[i]) < 1 && i + 1 < path.Count) f = SweepForward(path[i], path[i + 1], up);
                pts.Add(path[i]);
                fwds.Add(f);
                ups.Add(up);
                idx.Add(i);
            }
            constructionLegs.Clear();
            MatrixD none = MatrixD.Identity;
            FlightOrder leg = OrderGoTo(pts[0]);
            if (leg == null) return false;
            leg.ApproachFrom = leg.TransitStart = Vector3DData.FromVector3D(hullNow);
            Vector3D legFrom = hullNow;
            double cap = Math.Min(MaxSpeed, SWEEP_SPEED_MAX);
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    WaypointBehavior pass;
                    float exit;
                    Vector3D fin = fwds[i - 1], fout = fwds[i];
                    PassWaypoint(legFrom, pts[i - 1], pts[i], ref fin, ref fout, out pass, out exit);
                    leg = QueueGoTo(pts[i], pass, exit);
                    legFrom = pts[i - 1];
                }
                leg.ReferenceOffset = Vector3DData.FromVector3D(HullCentreLocal);
                ApplyLegAttitude(leg, true, ref none, fwds[i], ups[i]);
                // The planned way onto the path, its last stretch to the path's point included: the normal route watch (a
                // straight join from far was a leg nothing watched - 1.6 km - and the flight's trip for the lanes ended
                // there, too far out for the asteroid to be its end); the path itself: SweepLookAhead
                bool join = joinPlanned && i <= planned;
                leg.Watched = pre == null && join;
                leg.SweepIndex = idx[i];
                if (!join && (idx[i] >= 0 || pre != null)) leg.SpeedCap = cap;
                constructionLegs.Add(leg);
            }
            constructionOrder = leg;
            routeOwner = RouteOwner.Sweep;
            ResetRoute();
            taskProgressFrame = MyAPIGateway.Session.GameplayFrameCounter;
            taskBestDistance = double.MaxValue;
            watchedOrder = null;
            sweepWatchCounter = 0;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep: flying the path from point {1}/{2} ({3} leg(s), {4} planned, {5} hop point(s))", Entity.EntityId, from + 1, path.Count, pts.Count, planned, pre != null ? pre.Count : 0);
            return true;
        }

        // Nose along a - b, square to 'up' (belly to the surface)
        private static Vector3D SweepForward(Vector3D a, Vector3D b, Vector3D up)
        {
            Vector3D f = b - a;
            f -= up * Vector3D.Dot(f, up);
            if (f.LengthSquared() < 1e-4) f = Vector3D.CalculatePerpendicularVector(up);
            return Vector3D.Normalize(f);
        }

        // Replan of the planned stretch onto the path (route watch): from the point it was heading for
        private bool SweepReplan()
        {
            return sweepPhase == SweepPhase.Flying && SweepTaskRunning && SweepFlyFrom(SweepNextIndex(), true, null);
        }

        // Grids, trees, other entities along the path ahead (a few seconds of flight), and asteroid rock (asteroid sweeps:
        // the map is coarse, a safety net): 'dist' along the path from here
        private bool SweepLookAhead(int k, out double dist, out IMyEntity what)
        {
            dist = 0;
            what = null;
            var pm = PathfindingManager.Instance;
            if (pm == null) return false;
            var path = sweepMap.Points;
            PathQuery q;
            BuildPathQuery(out q);
            double look = Math.Max(SWEEP_LOOK_MIN, flightState.LinearVelocity.Length() * SWEEP_LOOK_SECONDS);
            Vector3D a = HullCentreWorld;
            double run = 0;
            for (int i = k; i < path.Count && run < look; i++)
            {
                Vector3D b = path[i];
                double len = Vector3D.Distance(a, b);
                if (len < 1) { a = b; continue; }
                if (run + len > look) { b = a + (b - a) * ((look - run) / len); len = look - run; }
                Vector3D up = sweepMap.Ups[i];
                MatrixD att = MatrixD.CreateWorld(Vector3D.Zero, SweepForward(a, b, up), up);
                Vector3D hit;
                if (pm.EntitiesAhead(ref q, ref att, a, b, out hit, out what, sweepTask.TargetEntityId != 0))
                {
                    dist = run + Vector3D.Distance(a, hit);
                    return true;
                }
                run += len;
                a = b;
            }
            return false;
        }

        // Something in the way 'dist' m on along the path: hop over it, else a planned way round, to the first point past it
        private TaskStatus SweepDetour(int k, double dist, IMyEntity what)
        {
            var path = sweepMap.Points;
            if (++sweepDetours > SWEEP_DETOURS_MAX) return SweepEnd(Loc.T(MyStrings.Drone_SweepTooManyObstacles));
            double past = dist + SWEEP_DETOUR_PAST + 2 * DroneRadius();
            Vector3D a = HullCentreWorld;
            double run = 0;
            int j = k;
            for (; j < path.Count; j++)
            {
                run += Vector3D.Distance(a, path[j]);
                a = path[j];
                if (run >= past) break;
            }
            for (int p = k; p < Math.Min(j, path.Count); p++) if (sweepMap.Scans[p]) sweepScanDue = true;   // scans skipped: one on the way
            if (j >= path.Count - 1) j = path.Count - 1;
            string name = what != null ? (what.DisplayName ?? what.GetType().Name) : "?";
            if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep: {1} {2:F0} m ahead on the path, past it at point {3}", Entity.EntityId, name, dist, j + 1);
            var hop = SweepHop(j);
            if (hop != null && SweepFlyFrom(j, false, hop)) return TaskStatus.Running;
            if (SweepFlyFrom(j, true, null)) return TaskStatus.Running;
            return SweepResume(j + SWEEP_SKIP, Loc.T(MyStrings.Drone_SweepNoWayOn));
        }

        // Up from here, across, down onto point 'j': the lowest of SWEEP_HOPS clear of entities (and of the terrain / rock
        // in between); null: none
        private List<Vector3D> SweepHop(int j)
        {
            var pm = PathfindingManager.Instance;
            if (pm == null) return null;
            var path = sweepMap.Points;
            PathQuery q;
            BuildPathQuery(out q);
            Vector3D here = HullCentreWorld;
            Vector3D upHere = flightState.InGravity ? flightState.GravityUp : sweepMap.Ups[Math.Max(0, Math.Min(sweepPassed, path.Count - 1))];
            Vector3D upThere = sweepMap.Ups[j];
            for (int h = 0; h < SWEEP_HOPS.Length; h++)
            {
                Vector3D p1 = here + upHere * SWEEP_HOPS[h], p2 = path[j] + upThere * SWEEP_HOPS[h];
                if (SweepStretchBlocked(pm, ref q, here, p1) || SweepStretchBlocked(pm, ref q, p1, p2) || SweepStretchBlocked(pm, ref q, p2, path[j]))
                    continue;
                return new List<Vector3D> { p1, p2 };
            }
            return null;
        }

        private bool SweepStretchBlocked(PathfindingManager pm, ref PathQuery q, Vector3D a, Vector3D b)
        {
            if (Vector3D.DistanceSquared(a, b) < 1) return false;
            Vector3D up = flightState.InGravity ? flightState.GravityUp : q.Controller.Up;
            MatrixD att = MatrixD.CreateWorld(Vector3D.Zero, SweepForward(a, b, up), up);
            Vector3D hit;
            IMyEntity e;
            if (pm.EntitiesAhead(ref q, ref att, a, b, out hit, out e)) return true;
            if (flightState.InGravity) return PathfindingManager.TerrainTooClose(a, b, TerrainNeed(), out hit);
            return pm.SegmentBlocked(ref q, ref att, a, b, 0.5, out hit, out e, false);   // asteroid rock (and blocks) between
        }

        // A fresh start further on: from point 'from' (then a few points further each try); too many: the sweep ends here
        private TaskStatus SweepResume(int from, string why)
        {
            var path = sweepMap.Points;
            if (++sweepResumes > SWEEP_RESUMES_MAX) return SweepEnd(why);
            for (int t = 0; t < 3; t++)
            {
                int i = from + t * SWEEP_SKIP;
                if (i >= path.Count) break;
                for (int p = sweepPassed + 1; p < i; p++) if (sweepMap.Scans[p]) sweepScanDue = true;
                if (SweepFlyFrom(i, true, null))
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep: {1}: on from point {2}/{3} ({4}/{5})", Entity.EntityId, why, i + 1, path.Count, sweepResumes, SWEEP_RESUMES_MAX);
                    sweepPassed = Math.Max(sweepPassed, i - 1);
                    return TaskStatus.Running;
                }
            }
            return SweepEnd(why);
        }

        private TaskStatus SweepEnd(string why)
        {
            if (sweepScanDue && !taskScanner.Running) StartSweepScan();
            Report(MyStrings.Drone_SweepEndedEarly, why, sweepScans);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: sweep ended early ({1}) at point {2}/{3}: {4} scans", Entity.EntityId, why, sweepPassed + 1, sweepMap.Points.Count, sweepScans);
            return SweepOver(TaskStatus.Done);
        }

        private TaskStatus SweepOver(TaskStatus status)
        {
            sweepPhase = SweepPhase.None;
            sweepMapping = false;
            sweepOrbits = null;
            return status;
        }

        // The waypoint bubble the path is cut for
        private double SweepBubble()
        {
            return Math.Min(WaypointTol, TRANSIT_TOLERANCE_MAX);
        }

        // How far the drone is from what it sweeps: the asteroid's box, else the spot
        private double SweepFocusDistance()
        {
            Vector3D pos = flightState.Position;
            if (sweepTask.TargetEntityId != 0)
            {
                IMyEntity e;
                var v = MyAPIGateway.Entities.TryGetEntityById(sweepTask.TargetEntityId, out e) ? e as IMyVoxelBase : null;
                if (v != null)
                {
                    BoundingBoxD box = v.PositionComp.WorldAABB;
                    return Vector3D.Distance(pos, Vector3D.Clamp(pos, box.Min, box.Max));
                }
            }
            return Vector3D.Distance(pos, sweepTask.Position.ToVector3D());
        }

        // Where the drone flies before the map is made, a little above the sweep's clearance: asteroid: over the spot,
        // out from its centre; planet: over the start of its spiral arm (each drone its own); open space: its arm's start
        private Vector3D SweepApproachPoint()
        {
            Vector3D spot = sweepTask.Position.ToVector3D();
            double h = SweepHullOffset() + SWEEP_APPROACH_OUT;
            if (sweepTask.TargetEntityId != 0)
            {
                IMyEntity e;
                var v = MyAPIGateway.Entities.TryGetEntityById(sweepTask.TargetEntityId, out e) ? e as IMyVoxelBase : null;
                if (v != null)
                {
                    Vector3D o = spot - v.PositionComp.WorldAABB.Center;
                    if (o.LengthSquared() > 1) return spot + Vector3D.Normalize(o) * (Automata.Pointer.SweepPlanner.ASTEROID_CLEARANCE + h);
                }
                return spot;
            }
            Vector3I sec = sweepTask.TargetBlock.ToVector3I();
            var planet = Sandbox.Game.Entities.MyGamePruningStructure.GetClosestPlanet(spot);
            if (planet != null && sweepTask.NaturalGravity > 0.01f)
            {
                Vector3D planeUp = spot - planet.PositionComp.GetPosition();
                if (planeUp.LengthSquared() > 1)
                {
                    Vector3D p = Automata.Pointer.SweepPlanner.SpiralStart(spot, Vector3D.Normalize(planeUp), sec.X, sec.Y);
                    Vector3D s = planet.GetClosestSurfacePointGlobal(ref p);
                    Vector3D up = s - planet.PositionComp.GetPosition();
                    if (up.LengthSquared() > 1) return s + Vector3D.Normalize(up) * (Automata.Pointer.SweepPlanner.PLANET_CLEARANCE + h);
                }
                return spot;
            }
            Vector3D face = sweepTask.ApproachFrom.ToVector3D() - spot;
            if (face.LengthSquared() < 1) return spot;
            face.Normalize();
            return Automata.Pointer.SweepPlanner.SpiralStart(spot, face, sec.X, sec.Y) + face * h;
        }
    }
}
