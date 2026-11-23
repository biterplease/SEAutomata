#if PERF
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Route planning measurements for TODO/27 (real-shape navigation): the same scene before / after, compared.
    /// PERF builds only: every use is in an `#if PERF` block, which tools\PerfStripper.cs removes from the published mod
    /// (deploy_steam / deploy_modio) and deploy_local keeps (except with -LogLevel Shipped). Server, main thread.
    ///
    /// Two CSV files in the mod's local storage (next to Automata.log), named by the session's start:
    ///  - RouteStats_[time]_plans.csv: a row per finished route plan (a PlanRoute that isn't Pending): who, from / to,
    ///    the hull and its attitude, the query's flags (enough to replay it), the outcome and what it cost. Also a row per
    ///    dock way-in worked out (PrepareDock: result DockOk / DockFailed, stage: corridor / compact / approach line;
    ///    from the drone to the connector; checks and blocks -1: not counted there).
    ///  - RouteStats_[time]_minutes.csv: a row a minute: the plans by outcome and stage, the box refusals by what refused
    ///    them, the queued searches and the checks of their answers, the physics rays (count, time), routes failing again.
    /// Not logged: the log level doesn't matter, nothing goes to Automata.log.
    /// </summary>
    public static class RouteStats
    {
        // Rays: the pathfinder's (asteroid bundles, look-ahead, doors), the real-shape sweeps (harvesters, welders)
        public const int RAY_PLAN = 0, RAY_SHAPE = 1;
        private const int RAY_KINDS = 2;
        // A leg's box sweep refused by: a static grid's block, anything else of a grid (a ship, a drone, a door), rock
        public const int BY_STATIC = 0, BY_OTHER = 1, BY_ROCK = 2;
        private const int BY_KINDS = 3;

        private const int MINUTE_TICKS = 60 * 60;
        private const double REPEAT_SECONDS = 120;   // the same route failing again within this: a repeat
        private const double REPEAT_FORGET = 300;
        private const double REPEAT_GRID = 2;        // m: start / goal rounded to this for "the same route"

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly double TicksToUs = 1000000.0 / Stopwatch.Frequency;
        private static readonly Stopwatch session = Stopwatch.StartNew();

        private static TextWriter plansCsv, minutesCsv;
        private static bool failed;
        private static int periodStart = -1, lastFrame;

        // ---- The minute ----
        private static int plans, direct, detour, fails, failsCached, polls;
        private static double planMs, planMsMax;
        private static int checks, blocks;
        private static readonly int[] refused = new int[BY_KINDS];
        private static int staticOnly, staticOnlyFailed;
        private static int stageStraight, stageMemory, stageThetaQueued, stageTheta;
        private static int searchesQueued, searchesDone, searchesFound, searchesNone, searchesOutOfTime;
        private static double searchMs, searchMsMax;
        private static long searchProbes;
        private static int accepts, acceptRejects;
        private static double acceptMs;
        private static readonly int[] rays = new int[RAY_KINDS];
        private static readonly double[] rayUs = new double[RAY_KINDS];
        private static double rayUsMax;
        private static int repeatFails, repeatMax;
        private static int docks, docksFailed, docksStaticOnlyFailed;
        private static int shapeCleared, shapeConfirmed, shapeCached, shapeOutOfRays;   // real-shape navigation: blocks
        private static double dockMs, dockMsMax;

        // ---- The plan in progress (the outermost PlanRoute) ----
        private static int depth;
        private static readonly int[] planRefused = new int[BY_KINDS];
        private static int planRays;
        private static double planRayUs;
        private static int planShapeCleared, planShapeConfirmed, planShapeOut;   // real-shape navigation, this plan's blocks

        private struct Repeat { public int Count; public double Last; }
        private static readonly Dictionary<long, Repeat> repeats = new Dictionary<long, Repeat>();
        private static readonly List<long> forget = new List<long>();
        private static readonly StringBuilder row = new StringBuilder(512);

        public static long Now() { return Stopwatch.GetTimestamp(); }

        /// <summary>A PlanRoute starts: true for the outermost one (its row is written by <see cref="EndPlan"/>).</summary>
        public static bool BeginPlan()
        {
            if (depth++ > 0) return false;
            for (int i = 0; i < BY_KINDS; i++) planRefused[i] = 0;
            planRays = 0;
            planRayUs = 0;
            planShapeCleared = planShapeConfirmed = planShapeOut = 0;
            return true;
        }

        /// <summary>
        /// The PlanRoute 'BeginPlan' was true for has its answer. 'q0': the query as it was asked; 'stage': the stage
        /// that found the route (null: none); 'cached': answered from the failed-route memory; the counts: its own scene
        /// checks and block boxes (its rays are counted here: every ray timed while it ran).
        /// </summary>
        public static void EndPlan(bool outer, long t0, long owner, ref PathQuery q0, Vector3D start, Vector3D goal, RouteResult r,
                                   string stage, bool cached, int planChecks, int planBlocks, string blocker)
        {
            depth = Math.Max(0, depth - 1);
            if (!outer) return;
            double ms = (Now() - t0) * TicksToMs;
            planMs += ms;
            planMsMax = Math.Max(planMsMax, ms);
            if (r == RouteResult.Pending) { polls++; return; }
            plans++;
            checks += planChecks;
            blocks += planBlocks;
            int refusals = 0;
            for (int i = 0; i < BY_KINDS; i++) { refused[i] += planRefused[i]; refusals += planRefused[i]; }
            bool ok = r == RouteResult.Direct || r == RouteResult.Detour;
            if (r == RouteResult.Direct) direct++;
            else if (r == RouteResult.Detour) detour++;
            else { fails++; if (cached) failsCached++; }
            if (refusals > 0 && planRefused[BY_STATIC] == refusals)
            {
                staticOnly++;
                if (!ok) staticOnlyFailed++;
            }
            if (ok && stage != null)
            {
                if (stage.StartsWith("theta (queued)", StringComparison.Ordinal)) stageThetaQueued++;
                else if (stage.StartsWith("theta", StringComparison.Ordinal)) stageTheta++;
                else if (stage == "memory") stageMemory++;
                else stageStraight++;
            }
            if (!ok) NoteFailure(owner, start, goal);
            WritePlan(owner, ref q0, start, goal, r.ToString(), ok ? stage : cached ? "cached" : "failed", ms, planChecks, planBlocks, ok ? null : blocker);
        }

        /// <summary>A dock way-in starts (PrepareDock): true for the outermost scope, like <see cref="BeginPlan"/>.</summary>
        public static bool BeginDock() { return BeginPlan(); }

        /// <summary>
        /// The dock way-in 'BeginDock' was true for is worked out ('ok': a way in; 'how': corridor / compact / approach
        /// line), from the drone at 'from' to the connector at 'connector'. 'q': the drone's query (its hull, its flags).
        /// </summary>
        public static void EndDock(bool outer, long t0, ref PathQuery q, Vector3D from, Vector3D connector, bool ok, string how)
        {
            depth = Math.Max(0, depth - 1);
            if (!outer) return;
            double ms = (Now() - t0) * TicksToMs;
            docks++;
            dockMs += ms;
            dockMsMax = Math.Max(dockMsMax, ms);
            int refusals = 0;
            for (int i = 0; i < BY_KINDS; i++) { refused[i] += planRefused[i]; refusals += planRefused[i]; }
            if (!ok)
            {
                docksFailed++;
                if (refusals > 0 && planRefused[BY_STATIC] == refusals) docksStaticOnlyFailed++;
            }
            WritePlan(q.Owner, ref q, from, connector, ok ? "DockOk" : "DockFailed", how ?? "", ms, -1, -1, null);
        }

        /// <summary>A leg's box sweep (main thread) refused: by what (BY_*).</summary>
        public static void Refused(int by)
        {
            if (depth > 0) planRefused[by]++;
            else refused[by]++;
        }

        /// <summary>One physics ray of kind RAY_*, started at 't0' (<see cref="Now"/>).</summary>
        public static void Ray(int kind, long t0)
        {
            double us = (Now() - t0) * TicksToUs;
            rays[kind]++;
            rayUs[kind] += us;
            rayUsMax = Math.Max(rayUsMax, us);
            if (depth > 0) { planRays++; planRayUs += us; }
        }

        public static void SearchQueued() { searchesQueued++; }

        /// <summary>Real-shape navigation: a block in the way by its cell box, checked with rays ('cached': an answer kept).</summary>
        public static void Shape(bool clear, bool cached)
        {
            if (cached) shapeCached++;
            if (clear) shapeCleared++;
            else shapeConfirmed++;
            if (depth > 0) { if (clear) planShapeCleared++; else planShapeConfirmed++; }
        }

        public static void ShapeOutOfRays()
        {
            shapeOutOfRays++;
            if (depth > 0) planShapeOut++;
        }

        /// <summary>A queued search's answer is back (main thread).</summary>
        public static void SearchDone(PathSearchResult r)
        {
            searchesDone++;
            if (r.Found) searchesFound++;
            else if (r.Why != null && r.Why.Contains("budget")) searchesOutOfTime++;
            else searchesNone++;
            searchMs += r.Ms;
            searchMsMax = Math.Max(searchMsMax, r.Ms);
            searchProbes += r.Probes;
        }

        /// <summary>A found search's route checked on the main thread (rays, doors) since 't0': accepted or not.</summary>
        public static void Accepted(bool ok, long t0)
        {
            accepts++;
            if (!ok) acceptRejects++;
            acceptMs += (Now() - t0) * TicksToMs;
        }

        // The same drone failing the same route again (start and goal within REPEAT_GRID) within REPEAT_SECONDS
        private static void NoteFailure(long owner, Vector3D start, Vector3D goal)
        {
            long key = owner;
            key = key * 31 + Cell(start);
            key = key * 31 + Cell(goal);
            double now = session.Elapsed.TotalSeconds;
            Repeat rep;
            if (repeats.TryGetValue(key, out rep) && now - rep.Last <= REPEAT_SECONDS) { rep.Count++; repeatFails++; }
            else rep.Count = 1;
            rep.Last = now;
            repeats[key] = rep;
            repeatMax = Math.Max(repeatMax, rep.Count);
        }

        private static long Cell(Vector3D p)
        {
            long x = (long)Math.Floor(p.X / REPEAT_GRID), y = (long)Math.Floor(p.Y / REPEAT_GRID), z = (long)Math.Floor(p.Z / REPEAT_GRID);
            return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
        }

        /// <summary>Server, every tick: the minute's row, once a minute.</summary>
        public static void Update(int frame)
        {
            lastFrame = frame;
            if (periodStart < 0) { periodStart = frame; return; }
            if (frame - periodStart < MINUTE_TICKS) return;
            double seconds = (frame - periodStart) / 60.0;
            periodStart = frame;
            WriteMinute(seconds);
            Reset();
            double now = session.Elapsed.TotalSeconds;
            forget.Clear();
            foreach (var kv in repeats) if (now - kv.Value.Last > REPEAT_FORGET) forget.Add(kv.Key);
            for (int i = 0; i < forget.Count; i++) repeats.Remove(forget[i]);
        }

        /// <summary>World unload: the files closed (the minute in progress written).</summary>
        public static void Close()
        {
            if (periodStart >= 0 && lastFrame > periodStart) WriteMinute((lastFrame - periodStart) / 60.0);   // (the counter at unload is no use)
            try
            {
                if (plansCsv != null) { plansCsv.Flush(); plansCsv.Close(); }
                if (minutesCsv != null) { minutesCsv.Flush(); minutesCsv.Close(); }
            }
            catch (Exception e) { Log.Warning("RouteStats: closing the files failed: {0}", e.Message); }
            plansCsv = minutesCsv = null;
            periodStart = -1;
            depth = 0;
            repeats.Clear();
            Reset();
        }

        private static void Reset()
        {
            plans = direct = detour = fails = failsCached = polls = 0;
            planMs = planMsMax = 0;
            checks = blocks = 0;
            for (int i = 0; i < BY_KINDS; i++) refused[i] = 0;
            staticOnly = staticOnlyFailed = 0;
            stageStraight = stageMemory = stageThetaQueued = stageTheta = 0;
            searchesQueued = searchesDone = searchesFound = searchesNone = searchesOutOfTime = 0;
            searchMs = searchMsMax = 0;
            searchProbes = 0;
            accepts = acceptRejects = 0;
            acceptMs = 0;
            for (int i = 0; i < RAY_KINDS; i++) { rays[i] = 0; rayUs[i] = 0; }
            rayUsMax = 0;
            repeatFails = repeatMax = 0;
            docks = docksFailed = docksStaticOnlyFailed = 0;
            dockMs = dockMsMax = 0;
            shapeCleared = shapeConfirmed = shapeCached = shapeOutOfRays = 0;
        }

        // ---- Files ----
        private const string PLANS_HEADER = "time_s,frame,owner,result,stage,dist_m,ms,checks,blocks,rays,ray_us,refused_static,refused_other,refused_rock,"
            + "start_x,start_y,start_z,goal_x,goal_y,goal_z,hull_cx,hull_cy,hull_cz,hull_x,hull_y,hull_z,fwd_x,fwd_y,fwd_z,up_x,up_y,up_z,"
            + "clearance,berth,min_alt,max_pitch,async,hull_centred,loose_goal,strict_ends,see_all,no_voxels,real_shape,hold_attitude,align_to_route,level_legs,"
            + "shape_cleared,shape_confirmed,shape_out_of_rays,blocker";
        private const string MINUTES_HEADER = "time_s,period_s,plans,direct,detour,failed,failed_cached,polls,plan_ms,plan_ms_max,checks,blocks,"
            + "refused_static,refused_other,refused_rock,static_only_plans,static_only_failed,stage_straight,stage_memory,stage_theta_queued,stage_theta,"
            + "searches_queued,searches_done,searches_found,searches_none,searches_out_of_time,search_ms,search_ms_max,search_probes,"
            + "accepts,accept_rejects,accept_ms,rays_plan,rays_plan_us,rays_shape,rays_shape_us,ray_us_max,repeat_fails,repeat_max,"
            + "docks,docks_failed,docks_static_only_failed,dock_ms,dock_ms_max,shape_cleared,shape_confirmed,shape_cached,shape_out_of_rays";

        private static bool Open()
        {
            if (plansCsv != null) return true;
            if (failed) return false;
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                plansCsv = MyAPIGateway.Utilities.WriteFileInLocalStorage("RouteStats_" + stamp + "_plans.csv", typeof(RouteStats));
                minutesCsv = MyAPIGateway.Utilities.WriteFileInLocalStorage("RouteStats_" + stamp + "_minutes.csv", typeof(RouteStats));
                plansCsv.WriteLine(PLANS_HEADER);
                minutesCsv.WriteLine(MINUTES_HEADER);
                Log.Info("RouteStats: writing RouteStats_{0}_plans.csv and RouteStats_{0}_minutes.csv (PERF build)", stamp);
                return true;
            }
            catch (Exception e)
            {
                failed = true;
                plansCsv = minutesCsv = null;
                Log.Warning("RouteStats: can't write the files: {0}", e.Message);
                return false;
            }
        }

        private static void WritePlan(long owner, ref PathQuery q, Vector3D start, Vector3D goal, string result, string stage, double ms,
                                      int planChecks, int planBlocks, string blocker)
        {
            if (!Open()) return;
            row.Length = 0;
            Num(session.Elapsed.TotalSeconds, "F2");
            row.Append(MyAPIGateway.Session != null ? MyAPIGateway.Session.GameplayFrameCounter : 0).Append(',');
            row.Append(owner).Append(',').Append(result).Append(',').Append(stage ?? "").Append(',');
            Num(Vector3D.Distance(start, goal), "F1");
            Num(ms, "F3");
            row.Append(planChecks).Append(',').Append(planBlocks).Append(',').Append(planRays).Append(',');
            Num(planRayUs, "F1");
            for (int i = 0; i < BY_KINDS; i++) row.Append(planRefused[i]).Append(',');
            Vec(start, "F2");
            Vec(goal, "F2");
            Vec(q.LocalHull.Center, "F2");
            Vec(q.LocalHull.Size, "F2");
            Vec(q.Controller.Forward, "F4");
            Vec(q.Controller.Up, "F4");
            Num(q.Clearance, "F2");
            Num(q.Berth, "F2");
            Num(q.MinAltitude, "F1");
            Num(q.MaxPitch, "F3");
            Flag(q.AsyncOk); Flag(q.HullCentred); Flag(q.LooseGoal); Flag(q.StrictEnds); Flag(q.SeeAll); Flag(q.NoVoxels);
            Flag(q.RealShapeGrid != null); Flag(q.HoldAttitude); Flag(q.AlignToRoute); Flag(q.LevelLegs);
            Ints(planShapeCleared, planShapeConfirmed, planShapeOut);
            row.Append(blocker != null ? blocker.Replace(',', ';').Replace('"', '\'') : "");   // what refused a failed plan last (block and where)
            plansCsv.WriteLine(row.ToString());
        }

        private static void WriteMinute(double seconds)
        {
            if (!Open()) return;
            row.Length = 0;
            Num(session.Elapsed.TotalSeconds, "F1");
            Num(seconds, "F1");
            Ints(plans, direct, detour, fails, failsCached, polls);
            Num(planMs, "F2");
            Num(planMsMax, "F2");
            Ints(checks, blocks, refused[BY_STATIC], refused[BY_OTHER], refused[BY_ROCK], staticOnly, staticOnlyFailed,
                 stageStraight, stageMemory, stageThetaQueued, stageTheta,
                 searchesQueued, searchesDone, searchesFound, searchesNone, searchesOutOfTime);
            Num(searchMs, "F1");
            Num(searchMsMax, "F1");
            row.Append(searchProbes).Append(',');
            Ints(accepts, acceptRejects);
            Num(acceptMs, "F2");
            row.Append(rays[RAY_PLAN]).Append(',');
            Num(rayUs[RAY_PLAN], "F0");
            row.Append(rays[RAY_SHAPE]).Append(',');
            Num(rayUs[RAY_SHAPE], "F0");
            Num(rayUsMax, "F1");
            Ints(repeatFails, repeatMax, docks, docksFailed, docksStaticOnlyFailed);
            Num(dockMs, "F2");
            Num(dockMsMax, "F2");
            Ints(shapeCleared, shapeConfirmed, shapeCached, shapeOutOfRays);
            row.Length--;
            minutesCsv.WriteLine(row.ToString());
            minutesCsv.Flush();
            plansCsv.Flush();
        }

        private static void Num(double v, string format) { row.Append(v.ToString(format, CultureInfo.InvariantCulture)).Append(','); }

        private static void Vec(Vector3D v, string format) { Num(v.X, format); Num(v.Y, format); Num(v.Z, format); }

        private static void Flag(bool b) { row.Append(b ? '1' : '0').Append(','); }

        private static void Ints(params int[] values) { for (int i = 0; i < values.Length; i++) row.Append(values[i]).Append(','); }
    }
}
#endif
