using System;
using System.Collections.Generic;
using System.Diagnostics;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>An open door the worker's final check met on stretch 'Seg' of its answer (rays are the main thread's).</summary>
    public struct SearchDoor
    {
        public int Seg;
        public IMySlimBlock Block;
        public IMyCubeGrid Grid;
    }

    /// <summary>What a worker's Lazy Theta* search found (or why not). Plain data, built on the worker, read on the main thread.</summary>
    public sealed class PathSearchResult
    {
        public bool Found;
        public string Why;
        /// <summary>Start to goal; with Far: start to Target (the rest is the main thread's straight stage).</summary>
        public List<Vector3D> Points;
        public bool Far;
        public Vector3D Target;
        /// <summary>Only a held attitude found a way: Controller is that attitude (HeldIndex 0: the drone's own).</summary>
        public bool Hold;
        public int HeldIndex;
        public MatrixD Controller;
        /// <summary>Found boxed in: the escape zone it was found with (PathQuery.EscapeRadius...; 0: none).</summary>
        public double EscapeRadius, EscapeBerth;
        public Vector3D EscapeCentre;
        /// <summary>A moving grid or a door was in the way somewhere (a "no way" holds for a short time only).</summary>
        public bool HitDynamic;
        /// <summary>The doors met along the answer, per stretch; the grids the search read (main thread checks, AcceptSearch).</summary>
        public List<SearchDoor> Doors;
        public SceneTrust[] Trust;
        public double Ms;            // search time on the worker
        public double WaitMs;        // queued until done
        public int Probes, Checks, Blocks;

        // The main thread's check passed (AcceptSearch): the route as accepted, reused by the next plan of the same stretch
        // within a short time (a route of several stretches is planned again each time one of them waits for a search)
        public bool Accepted;
        public int AcceptFrame;
        public Vector3D AcceptStart, AcceptGoal;
        public List<Vector3D> AcceptPoints;
        public bool AcceptHold;
        public MatrixD AcceptController;
    }

    /// <summary>Answer to <see cref="PathSearchQueue.Ask"/> / <see cref="PathSearchQueue.Find"/>.</summary>
    public enum SearchPoll : byte
    {
        /// <summary>Not queued (none for this ask; queue full, off): the caller searches now.</summary>
        Inline,
        /// <summary>Queued / running: the drone holds and plans again when <see cref="PathSearchQueue.TakeNews"/> says so.</summary>
        Pending,
        /// <summary>Done (found or not): the result.</summary>
        Ready,
    }

    /// <summary>
    /// Thrown by the drone's planner when a route needs a search that is still running and the caller can wait (the
    /// task runner, a replan, a docking): unwinds to the entry point, which holds the drone and plans again later.
    /// </summary>
    public sealed class PlanPendingException : Exception
    {
        public static readonly PlanPendingException Instance = new PlanPendingException();
        private PlanPendingException() : base("route search pending") { }
    }

    /// <summary>
    /// The path search queue (TODO/22 section 1; server, main thread except the searches). Stages 1 and 2 of the planner
    /// stay inline; a route that needs Lazy Theta* (and held attitudes) is queued here instead of run at once:
    ///  - FIFO, at most PER_OWNER_MAX requests per drone; a request nobody waits for for QUEUE_TTL_TICKS is dropped
    ///    (<see cref="Waiting"/> keeps it).
    ///  - Each tick: up to PathSearchWorkers requests are dispatched. A request first gets a SnapshotScene (the
    ///    entries and block trees of its region, captured on the main thread within BUILD_BUDGET_MS per tick), then
    ///    runs on a worker with its own planner (a PathfindingManager that reads the snapshot and a frozen copy of
    ///    the query only) - MyAPIGateway.Parallel.Start; the completion callback runs on the main thread.
    ///  - The answer waits here for the drone's next plan of the same stretch (<see cref="Find"/>: same owner and
    ///    signature, start within START_SLACK, goal within GOAL_SLACK), which goes straight to it (stages 1 and 2 are
    ///    not run again); the planner checks it with the rays and doors the worker skipped (PathfindingManager.AcceptSearch).
    ///    Answers live until the drone's plan ends (<see cref="EndPlan"/>) or RESULT_TTL_TICKS pass without an ask.
    ///  - Warm-up regions (<see cref="WarmUp"/>): the scene (entries, block trees) of an area expected to be busy
    ///    is built ahead, with WARM_BUDGET_MS a tick when no request waits.
    /// </summary>
    public sealed class PathSearchQueue
    {
        #region Timing
        private const int QUEUE_TTL_TICKS = 10 * 60;      // a queued request nobody waited for this long is dropped
        private const int RESULT_TTL_TICKS = 30 * 60;     // an answer nobody asked about for this long is dropped
        private const int FAILED_TTL_TICKS = 20 * 60;     // a "no way" answer is kept (the same ask gets it again) this long
        #endregion
        private const double START_SLACK = 25;      // m: an answer holds for a start this close (a drone braking while it waits)
        private const double GOAL_SLACK = 2;        // m: goals worked out again from a moving anchor
        private const int QUEUE_MAX = 48;
        private const int PER_OWNER_MAX = 4;
        private const double BUILD_BUDGET_MS = 2.0; // block trees built per tick for the queued requests
        private const double WARM_BUDGET_MS = 1.0;  // ...and for warm-ups (when no request waits)

        private enum JobState : byte { Queued, Preparing, Running, Done }

        private sealed class Worker
        {
            public PathfindingManager Planner;
            public SnapshotScene Scene;
            public Job Job;
        }

        private sealed class Job
        {
            public long Owner;
            public int Sig;
            public Vector3D Start, Goal;
            public PathQuery Q;                 // frozen
            public int LastAsk;
            public double QueuedAt;             // ms on the queue's clock
            public JobState State;
            public Worker Worker;
            public PathSearchResult Result;
            public SceneTrust[] Trust;
            public int DoneFrame;
            public bool Cancelled;
            public string Error;
        }

        private readonly PathfindingManager manager;
        private readonly PathfindingConfig config;
        private readonly Worker[] workers;
        private readonly List<Job> queue = new List<Job>();      // Queued, FIFO
        private readonly List<Job> active = new List<Job>();     // Preparing / Running
        private readonly List<Job> results = new List<Job>();    // Done
        private readonly HashSet<long> news = new HashSet<long>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int frame;
        private bool shutdown;

        // Warm-up: regions to prepare, one at a time (looked up one tick, built the next ones)
        private readonly List<BoundingBoxD> warm = new List<BoundingBoxD>();
        private readonly SnapshotScene warmScene = new SnapshotScene();
        private bool warming;
        private readonly PathWarmUp warmUp = new PathWarmUp();

        /// <summary>For the logs: searches queued / run / dropped.</summary>
        public int Queued, Run, Dropped;

        public int QueueLength { get { return queue.Count; } }
        public int ActiveCount { get { return active.Count; } }
        public int WarmPending { get { return warm.Count + (warming ? 1 : 0); } }

        public PathSearchQueue(PathfindingManager manager, PathfindingConfig config)
        {
            this.manager = manager;
            this.config = config;
            workers = new Worker[Math.Max(1, config.PathSearchWorkers)];
            for (int i = 0; i < workers.Length; i++)
            {
                var scene = new SnapshotScene();
                workers[i] = new Worker { Scene = scene, Planner = new PathfindingManager(config, scene) };
            }
            Log.Info("Pathfinding: Lazy Theta* searches on {0} worker thread(s), up to {1:F0} ms each{2}", workers.Length, config.PathSearchMaxMs,
                     config.PathWarmUp ? ", scene warm-up on" : "");
        }

        #region Asking (the drone's planner)
        /// <summary>
        /// The search for this ask if there is one, nothing queued: Ready (done, found or not: 'result'), Pending (queued or
        /// running) or Inline (none).
        /// </summary>
        public SearchPoll Find(ref PathQuery q, Vector3D start, Vector3D goal, out PathSearchResult result)
        {
            result = null;
            if (shutdown || q.Owner == 0) return SearchPoll.Inline;
            int sig = PathfindingManager.QuerySignature(ref q);
            for (int i = 0; i < results.Count; i++)
            {
                var j = results[i];
                if (!Matches(j, q.Owner, sig, start, goal)) continue;
                j.LastAsk = frame;
                result = j.Result;
                return SearchPoll.Ready;
            }
            for (int i = 0; i < queue.Count; i++)
                if (Matches(queue[i], q.Owner, sig, start, goal)) { queue[i].LastAsk = frame; return SearchPoll.Pending; }
            for (int i = 0; i < active.Count; i++)
                if (!active[i].Cancelled && Matches(active[i], q.Owner, sig, start, goal)) { active[i].LastAsk = frame; return SearchPoll.Pending; }
            return SearchPoll.Inline;
        }

        /// <summary>
        /// The search for 'start' to 'goal' (Lazy Theta*, held attitudes), queued if there is none yet. Ready: done
        /// ('result'; found or not). Pending: queued or running. Inline: not queued (full, off), the caller runs it itself.
        /// </summary>
        public SearchPoll Ask(ref PathQuery q, Vector3D start, Vector3D goal, out PathSearchResult result)
        {
            var poll = Find(ref q, start, goal, out result);
            if (poll != SearchPoll.Inline || shutdown || q.Owner == 0 || MyAPIGateway.Parallel == null) return poll;
            if (queue.Count >= QUEUE_MAX) return SearchPoll.Inline;
            // A newer request replaces the owner's oldest one past the limit
            int own = 0, oldest = -1;
            for (int i = 0; i < queue.Count; i++)
                if (queue[i].Owner == q.Owner) { own++; if (oldest < 0) oldest = i; }
            if (own >= PER_OWNER_MAX && oldest >= 0) { queue.RemoveAt(oldest); Dropped++; }
            var job = new Job
            {
                Owner = q.Owner,
                Sig = PathfindingManager.QuerySignature(ref q),
                Start = start,
                Goal = goal,
                Q = q.Frozen(),
                LastAsk = frame,
                QueuedAt = clock.Elapsed.TotalMilliseconds,
                State = JobState.Queued,
            };
            queue.Add(job);
            Queued++;
            if (Log.DebugEnabled) Log.Debug("Pathfinding: search queued for drone {0}: {1:F0} m ({2} waiting, {3} running)", q.Owner,
                      Vector3D.Distance(start, goal), queue.Count, active.Count);
            return SearchPoll.Pending;
        }

        /// <summary>
        /// The drone still waits for a search of its own (queued or running): true, and the requests are kept. False: none
        /// left (done - <see cref="TakeNews"/> - or dropped): the drone plans again.
        /// </summary>
        public bool Waiting(long owner)
        {
            bool any = false;
            for (int i = 0; i < queue.Count; i++)
                if (queue[i].Owner == owner) { queue[i].LastAsk = frame; any = true; }
            for (int i = 0; i < active.Count; i++)
                if (active[i].Owner == owner && !active[i].Cancelled) { active[i].LastAsk = frame; any = true; }
            return any;
        }

        // The memory stage (main thread, can cost tens of ms) found nothing for this ask: not run again for the same ask
        // while the drone waits for the search
        private struct MemNote
        {
            public long Owner;
            public int Sig, Frame;
            public Vector3D Start, Goal;
        }
        private readonly List<MemNote> memFailed = new List<MemNote>();

        public bool MemoryFailedBefore(ref PathQuery q, Vector3D start, Vector3D goal)
        {
            if (memFailed.Count == 0) return false;
            int sig = PathfindingManager.QuerySignature(ref q);
            for (int i = 0; i < memFailed.Count; i++)
            {
                var m = memFailed[i];
                if (m.Owner == q.Owner && m.Sig == sig && Vector3D.DistanceSquared(m.Goal, goal) <= GOAL_SLACK * GOAL_SLACK
                    && Vector3D.DistanceSquared(m.Start, start) <= START_SLACK * START_SLACK) return true;
            }
            return false;
        }

        public void NoteMemoryFailed(ref PathQuery q, Vector3D start, Vector3D goal)
        {
            if (q.Owner == 0 || memFailed.Count > 64) return;
            memFailed.Add(new MemNote { Owner = q.Owner, Sig = PathfindingManager.QuerySignature(ref q), Start = start, Goal = goal, Frame = frame });
        }

        private static bool Matches(Job j, long owner, int sig, Vector3D start, Vector3D goal)
        {
            return j.Owner == owner && j.Sig == sig && Vector3D.DistanceSquared(j.Goal, goal) <= GOAL_SLACK * GOAL_SLACK
                   && Vector3D.DistanceSquared(j.Start, start) <= START_SLACK * START_SLACK;
        }

        /// <summary>The answer was refused on the main thread (its checks): kept as a "no way" so the same ask isn't searched again.</summary>
        public void Reject(PathSearchResult r, string why)
        {
            r.Found = false;
            r.Why = why;
            r.HitDynamic = true;   // what the worker didn't see came since: it may go again
            r.Accepted = false;
            for (int i = 0; i < results.Count; i++)
                if (results[i].Result == r) { results[i].DoneFrame = frame; break; }
        }

        /// <summary>A search of this drone's finished since it last asked (true once).</summary>
        public bool TakeNews(long owner)
        {
            return news.Remove(owner);
        }

        /// <summary>The drone's plan is over (done, or given up): its answers and waiting requests go.</summary>
        public void EndPlan(long owner)
        {
            news.Remove(owner);
            for (int i = memFailed.Count - 1; i >= 0; i--)
                if (memFailed[i].Owner == owner) memFailed.RemoveAt(i);
            for (int i = results.Count - 1; i >= 0; i--)
                if (results[i].Owner == owner) results.RemoveAt(i);
            for (int i = queue.Count - 1; i >= 0; i--)
                if (queue[i].Owner == owner) queue.RemoveAt(i);
            for (int i = 0; i < active.Count; i++)
                if (active[i].Owner == owner) active[i].Cancelled = true;
        }
        #endregion

        #region Warm-up
        /// <summary>
        /// Prepares the scene (obstacle entries, block trees) in 'region' ahead of the first search there, with the
        /// per-tick budget the queued requests leave. The caller decides how often (a region is cheap once built).
        /// </summary>
        public void WarmUp(BoundingBoxD region)
        {
            if (shutdown || warm.Count > 64) return;
            for (int i = 0; i < warm.Count; i++)
                if (warm[i].Contains(region) == ContainmentType.Contains) return;
            warm.Add(region);
        }
        #endregion

        #region Every tick
        public void Update(int now)
        {
            frame = now;
            if (shutdown) return;
            if (config.PathWarmUp) warmUp.Update(now, this);
            // Expire
            for (int i = results.Count - 1; i >= 0; i--)
            {
                var j = results[i];
                int ttl = j.Result != null && j.Result.Found ? RESULT_TTL_TICKS : FAILED_TTL_TICKS;
                if (now - Math.Max(j.LastAsk, j.DoneFrame) > ttl) results.RemoveAt(i);
            }
            for (int i = queue.Count - 1; i >= 0; i--)
                if (now - queue[i].LastAsk > QUEUE_TTL_TICKS) { queue.RemoveAt(i); Dropped++; }
            for (int i = memFailed.Count - 1; i >= 0; i--)
                if (now - memFailed[i].Frame > RESULT_TTL_TICKS) memFailed.RemoveAt(i);

            if (queue.Count > 0 || active.Count > 0)
            {
                double t0 = clock.Elapsed.TotalMilliseconds;
                // Requests being prepared: their block trees within the budget
                for (int i = 0; i < workers.Length; i++)
                {
                    var w = workers[i];
                    var j = w.Job;
                    if (j == null || j.State != JobState.Preparing) continue;
                    if (j.Cancelled) { Release(w); continue; }
                    double left = BUILD_BUDGET_MS - (clock.Elapsed.TotalMilliseconds - t0);
                    if (left <= 0) break;
                    if (w.Scene.Step(manager.LiveScene, j.Q.OwnGrids, left, clock)) Dispatch(w);
                }
                // Idle workers take the next requests (looked up this tick, built from the next)
                for (int i = 0; i < workers.Length && queue.Count > 0; i++)
                {
                    var w = workers[i];
                    if (w.Job != null) continue;
                    if (BUILD_BUDGET_MS - (clock.Elapsed.TotalMilliseconds - t0) <= 0) break;
                    Job j = queue[0];
                    queue.RemoveAt(0);
                    j.Worker = w;
                    j.State = JobState.Preparing;
                    w.Job = j;
                    active.Add(j);
                    BoundingBoxD region = manager.SearchRegion(ref j.Q, j.Start, j.Goal);
                    w.Scene.Begin(manager.LiveScene, region, now);
                }
                PerfStats.Record(PerfStats.PathPrepare, clock.Elapsed.TotalMilliseconds - t0);
            }
            // Warm-up with the rest, when no request is waiting for a worker
            if (queue.Count == 0 && (warm.Count > 0 || warming)) StepWarm(now);
        }

        private void StepWarm(int now)
        {
            double t0 = clock.Elapsed.TotalMilliseconds;
            if (!warming)
            {
                BoundingBoxD region = warm[0];
                warm.RemoveAt(0);
                warmScene.Begin(manager.LiveScene, region, now);   // the lookup this tick, the trees from the next
                warming = true;
            }
            else if (warmScene.Step(manager.LiveScene, null, WARM_BUDGET_MS, clock, false))
            {
                warmScene.Clear();
                warming = false;
            }
            PerfStats.Record(PerfStats.PathWarm, clock.Elapsed.TotalMilliseconds - t0);
        }

        private void Release(Worker w)
        {
            if (w.Job != null) active.Remove(w.Job);
            w.Scene.Clear();
            w.Job = null;
        }

        private void Dispatch(Worker w)
        {
            var job = w.Job;
            job.State = JobState.Running;
            job.Trust = w.Scene.Trust();
            var planner = w.Planner;
            int now = frame;
            Action work = delegate
            {
                try { job.Result = planner.RunSearch(job.Q, job.Start, job.Goal, now); }
                catch (Exception ex)
                {
                    job.Error = ex.ToString();
                    job.Result = new PathSearchResult { Found = false, Why = "search error: " + ex.Message, HitDynamic = true };
                }
            };
            Action done = delegate
            {
                // (the main thread, start of a tick: an exception here is not caught by the engine)
                try { Complete(w, job); }
                catch (Exception ex) { Log.Error("Pathfinding: search completion error: {0}", ex); }
            };
            MyAPIGateway.Parallel.Start(work, done);
        }

        private void Complete(Worker w, Job job)
        {
            active.Remove(job);
            if (w.Job == job) { w.Job = null; w.Scene.Clear(); }
            job.State = JobState.Done;
            if (shutdown || job.Cancelled) { Dropped++; return; }
            if (job.Error != null) Log.Error("Pathfinding: search for drone {0} failed: {1}", job.Owner, job.Error);
            var r = job.Result ?? new PathSearchResult { Found = false, Why = "no answer", HitDynamic = true };
            job.Result = r;
            r.Trust = job.Trust;
            job.Trust = null;
            r.WaitMs = clock.Elapsed.TotalMilliseconds - job.QueuedAt;
            job.DoneFrame = frame;
            results.Add(job);
            news.Add(job.Owner);
            Run++;
            PerfStats.Record(PerfStats.PathSearch, r.Ms);
            PerfStats.Record(PerfStats.PathSearchWait, r.WaitMs);
            if (Log.DebugEnabled) Log.Debug("Pathfinding: search for drone {0}: {1} in {2:F1} ms on a worker ({3:F0} ms from queued; {4} probes, {5} checks, {6} blocks){7}",
                      job.Owner, r.Found ? (r.Points.Count + " points") : ("none: " + r.Why), r.Ms, r.WaitMs, r.Probes, r.Checks, r.Blocks,
                      r.Found && r.Hold ? ", holding the drone's attitude" : "");
        }

        /// <summary>World unload: nothing is queued or kept; searches in flight stop at their next check.</summary>
        public void Shutdown()
        {
            shutdown = true;
            for (int i = 0; i < workers.Length; i++) workers[i].Planner.CancelSearch = true;
            queue.Clear();
            results.Clear();
            memFailed.Clear();
            warm.Clear();
            news.Clear();
        }
        #endregion
    }
}
