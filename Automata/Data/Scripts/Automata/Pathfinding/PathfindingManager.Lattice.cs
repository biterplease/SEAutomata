using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// The block lattice stage (2026-11-13, TODO/27): indoors - tunnels, mazes, bays built on a static grid - the
    /// world-aligned search (Lazy Theta*: cell boxes, one attitude) gives up within a few cells: the hull fits the
    /// corridors only turned some ways, and only where the blocks' real shapes leave room. This stage searches on that
    /// grid's own lattice (half a block apart) with the hull's lie in the state (LatticeSearch, LatticeCore.cs: the
    /// search itself, no game calls). Here: the game side.
    ///  - The frame: the static grid in the way (else the biggest static one round start / goal), the lattice over start
    ///    and goal and LATTICE_MARGIN round them (within the grid's bounds and the margin).
    ///  - What is in the way: that grid's blocks by their real shapes (PathfindingManager.Shapes; RealShapeConfirm off:
    ///    their cells), and the other static grids' blocks there (WindowOf). Moving grids and rock aren't in it: the
    ///    answer's legs are checked against everything (SegmentBlocked, as the drone checks each leg when it starts it),
    ///    a leg blocked by something else is left out and the search runs again.
    ///  - Start and goal are joined to the lattice by checked straight lines.
    ///  - The answer: straight runs as legs; runs in one lie pulled straight where there is the route clearance (ObstacleClearance) of room; each leg
    ///    with its attitude (LegHints: the drone flies it turned that way, turning in place where the lie changes).
    /// Main thread only; LatticeMaxMs per search, LATTICE_TICK_MS per tick.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private const double LATTICE_MARGIN = 10;          // m round start and goal (and out of the grid) the lattice spans
        private const double LATTICE_FAR_OUT = 100;        // m: start or goal further out of the grid - not the lattice's
        private const double LATTICE_REACH = 200;          // m to the sides of start and goal, within the grid (the way round)
        private const long LATTICE_MAX_POINTS = 6000000;   // lattice points (the reach is halved until it fits)
        private const double LATTICE_MEASURE_MS = 60;      // ms a search's field may take more while block shapes are measured in it
        private const int LATTICE_FIELD_POINTS = 600000;   // lattice points the corridor field floods, at most
        private const int LATTICE_JOIN_TRIES = 24;         // lattice points tried for the start...
        private const int LATTICE_JOIN_KEEP = 4;           // ...and kept once joined
        private const int LATTICE_SEEDS = 8;               // lattice points by the goal the field starts from
        private const double LATTICE_TICK_MS = 80;         // all lattice searches of a tick, at most
        private const int LATTICE_RETRIES = 3;             // searched again round legs blocked by something else
        private const int LATTICE_MEASURE_CELLS = 48;      // block shapes a search may measure beyond the tick's (once per kind of block)
        private const double TURN_FREE_COS = 0.985;        // within ~10° of a lie's attitude: no turn to check
        private const int LATTICE_SKIPS_MAX = 30;          // ticks in a row a drone's lattice may wait for the tick's time
        private long latSkipOwner;
        private int latSkipCount;
        private bool latWindowWait;   // LatticeFrame: the window not ready yet (sliced build, the block tree): hold

        private struct LatLeg
        {
            public Vector3D A, B;
            public int Perm;
            public Vector3I LA, LB;     // its lattice ends (a lattice leg)
            public bool Lattice;
        }

        private readonly LatticeSearch lat = new LatticeSearch();
        private readonly List<LatticeSearch.Start> latStarts = new List<LatticeSearch.Start>();
        private readonly List<Vector3I> latSeeds = new List<Vector3I>();
        private readonly List<KeyValuePair<Vector3I, double>> latCandidates = new List<KeyValuePair<Vector3I, double>>();
        private readonly List<LocalScene.Entry> latEntries = new List<LocalScene.Entry>();
        private readonly List<LatLeg> latLegs = new List<LatLeg>(), latPulled = new List<LatLeg>();
        private readonly System.Diagnostics.Stopwatch latClock = new System.Diagnostics.Stopwatch();
        private double latTickMs;
        private int latTickFrame = -1;

        // The search frame
        private MatrixD latWorld, latInv;
        private string latGridName;
        private GridShapes latShapes;
        private Vector3D latGravityUp;

        public bool LatticeAvailable { get { return config.LatticeSearch; } }

        private static Vector3D GridAxis(ref MatrixD m, int a)
        {
            return a == 0 ? m.Right : a == 1 ? m.Up : m.Backward;
        }

        private Vector3D LatPoint(int x, int y, int z)
        {
            return Vector3D.Transform(lat.Local(x, y, z), latWorld);
        }

        private void LieAttitude(int p, Vector3D refF, Vector3D refU, Vector3D travel, out Vector3D fwd, out Vector3D up)
        {
            lat.LieAttitude(p, latWorld.Right, latWorld.Up, latWorld.Backward, refF, refU, travel, latGravityUp, out fwd, out up);
        }

        private MatrixD LieMatrix(int p)
        {
            return MatrixD.CreateWorld(Vector3D.Zero, GridAxis(ref latWorld, lat.Fa[p]), GridAxis(ref latWorld, lat.Ua[p]));
        }

        /// <summary>
        /// The lattice stage: a route from 'start' to 'goal' (hull centres) on the lattice of the static grid in the way
        /// into 'points' (start and goal included), each leg's attitude into the leg hints. False: none ('why').
        /// </summary>
        private bool LatticeStage(ref PathQuery q, Vector3D start, Vector3D goal, IMyCubeGrid blocker, out string why, out bool pending)
        {
            why = null;
            pending = false;
            latPending = false;
            if (!q.HullCentred || offThread) { why = "not for this ask"; return false; }
            if (frame != latTickFrame) { latTickFrame = frame; latTickMs = 0; }
            if (latTickMs >= LATTICE_TICK_MS)
            {
                // This tick's lattice time went on other plans: on next tick - not "no way here", for a drone that can hold
                // (it lowered its clearance for want of time: 89 m at 1.0 m went to 0.5 m, profiler build, 2026-11-18). Held
                // at most LATTICE_SKIPS_MAX ticks in a row (other drones' plans first every tick: then as before).
                if (q.AsyncOk && !q.NoSearch && q.Owner != 0)
                {
                    if (latSkipOwner != q.Owner) { latSkipOwner = q.Owner; latSkipCount = 0; }
                    if (++latSkipCount <= LATTICE_SKIPS_MAX)
                    {
                        pending = true;
                        why = "tick budget: next tick";
                        if (Log.DebugEnabled) Log.Debug("Pathfinding: lattice for drone {0}: this tick's time spent ({1:F1} ms), on next tick", q.Owner, latTickMs);
                        return false;
                    }
                }
                why = "tick budget";
                return false;
            }
            if (latSkipOwner == q.Owner) latSkipOwner = 0;
            double maxMs = Math.Min(config.LatticeMaxMs, LATTICE_TICK_MS - latTickMs);
            latClock.Restart();
            measureExtra = ShapesOn ? LATTICE_MEASURE_CELLS : 0;
            int unknown0 = ShapesUnknown, measured0 = ShapesMeasured;
            lat.Banned.Clear();
            try
            {
                int built0 = windowBuilds;
                latWindowWait = false;
                if (!LatticeFrame(ref q, start, goal, blocker, out why))
                {
                    if (latWindowWait)
                    {
                        // The window still being built (a slice a tick): the drone holds, the next plan goes on with it -
                        // one that can't hold goes without the lattice this time
                        if (q.AsyncOk && !q.NoSearch && q.Owner != 0)
                        {
                            pending = true;
                            if (Log.DebugEnabled) Log.Debug("Pathfinding: lattice of {0}: {1}; on with it next tick", latGridName, why);
                        }
                        else why += " (this plan can't wait for it)";
                    }
                    return false;
                }
                latWindowNote = string.Format("window {0} {1:F1} ms", windowBuilds != built0 ? "built" : "kept", latClock.Elapsed.TotalMilliseconds);
                bool ok = LatticeRoute(ref q, start, goal, maxMs, out why);
                if (!ok && !latPending) why += "; " + latWindowNote;
                if (latPending)
                {
                    // Not done within this tick's time: the drone holds, and the next plan goes on with it
                    pending = true;
                    latJobMs += latClock.Elapsed.TotalMilliseconds;
                    latJobFrame = frame;
                    if (Log.DebugEnabled) Log.Debug("Pathfinding: lattice of {0}: {1} ({2}); {3:F1} ms of it so far, on with it next tick", latGridName, why, latWindowNote, latJobMs);
                    return false;
                }
                latJobOwner = 0;
                int unknown = latShapes.Occ.UnknownCells.Count;
                if (!ok && (unknown > 0 || ShapesUnknown > unknown0 || ShapesMeasured > measured0)) PlanTransient = planNoCache = true;
                if (!ok && (unknown > 0 || ShapesUnknown > unknown0))
                {
                    // Blocks counted as their whole cells for want of a shape: measured in the background, asked again then
                    why += string.Format("; {0} block shape(s) not measured yet", Math.Max(unknown, ShapesUnknown - unknown0));
                    planNoCache = true;
                }
                if (ShapesMeasured > measured0 && Log.DebugEnabled) Log.Debug("Pathfinding: {0} block shape(s) measured ({1} known)", ShapesMeasured - measured0, defShapes.Count);
                return ok;
            }
            finally
            {
                measureExtra = 0;
                latTickMs += latClock.Elapsed.TotalMilliseconds;
                if (!latPending) lat.Reset();   // (a search going on next tick keeps its states)
            }
        }

        // A lattice job going on over several ticks (a big maze: the field and the search take longer than one plan's
        // time; it ran out of it on every plan, and the drone repositioned, again and again, 2026-11-16). The drone
        // holds meanwhile (PlanPendingException); its next plan for the same start, goal and clearance goes on with it.
        private bool latPending;
        private string latWindowNote;   // the window this plan: built / kept, and the time to it (the logs)
        private long latJobOwner, latJobKey;
        private Vector3D latJobStart, latJobGoal;
        private int latJobFrame;
        private bool latJobSearch;          // the search started: ContinueSearch next (else the field / the joins again)
        private double latJobMs;            // main-thread time it took so far
        private const int LATTICE_JOB_TICKS = 5 * 60;       // not asked again in this long: dropped
        private const double LATTICE_JOB_MS = 1000;         // at most this much time in all, then it gives up

        private void LatJobHold(ref PathQuery q, Vector3D start, Vector3D goal, long key, bool search)
        {
            latPending = true;
            latJobOwner = q.Owner;
            latJobStart = start;
            latJobGoal = goal;
            latJobKey = key;
            latJobSearch = search;
        }

        /// <summary>
        /// The lattice's case: the straight line was blocked by a static grid ('blocker') and start or goal is within its
        /// bounds (inside a maze, a hangar). Cheap: the grid's bounds only. (Not any grid round the start: a drone docked
        /// at its base is within that base's bounds on every flight out.)
        /// </summary>
        private bool LatticeIndoors(ref PathQuery q, Vector3D start, Vector3D goal, IMyCubeGrid blocker)
        {
            if (blocker == null) return false;
            var area = BoundingBoxD.CreateInvalid();
            area.Include(start);
            area.Include(goal);
            area = area.GetInflated(LATTICE_MARGIN);
            liveScene.Ensure(new BoundingSphereD(area.Center, area.HalfExtents.Length()), frame);
            latEntries.Clear();
            liveScene.Overlapping(ref area, latEntries, frame);
            bool inside = false;
            for (int i = 0; i < latEntries.Count && !inside; i++)
            {
                var e = latEntries[i];
                if (e.Voxel || e.Grid == null || !e.Static || IsOwn(ref q, e.Grid)) continue;
                if (e.Grid != blocker) continue;
                var b = new BoundingBoxD((Vector3D)e.Grid.Min * e.GridSize - e.GridSize, (Vector3D)e.Grid.Max * e.GridSize + e.GridSize);
                inside = b.Contains(Vector3D.Transform(start, e.Inv)) != ContainmentType.Disjoint || b.Contains(Vector3D.Transform(goal, e.Inv)) != ContainmentType.Disjoint;
            }
            latEntries.Clear();
            return inside;
        }

        // The grid to search on, its lattice, the lies allowed, what is in the way (the window)
        private bool LatticeFrame(ref PathQuery q, Vector3D start, Vector3D goal, IMyCubeGrid blocker, out string why)
        {
            why = null;
            LocalScene.Entry pick = null;
            var area = BoundingBoxD.CreateInvalid();
            area.Include(start);
            area.Include(goal);
            area = area.GetInflated(LATTICE_MARGIN);
            liveScene.Ensure(new BoundingSphereD(area.Center, area.HalfExtents.Length()), frame);
            latEntries.Clear();
            liveScene.Overlapping(ref area, latEntries, frame);
            double best = -1;
            for (int i = 0; i < latEntries.Count; i++)
            {
                var e = latEntries[i];
                if (e.Voxel || e.Grid == null || !e.Static || IsOwn(ref q, e.Grid)) continue;
                if (e.Grid == blocker) { pick = e; break; }
                double v = e.Grid.WorldAABB.Volume;
                if (v > best) { best = v; pick = e; }
            }
            // Indoors only: start or goal within the grid's bounds (round it in the open is the other stages' - flooding
            // open space is what this one is slow at)
            string outside = null;
            if (pick != null)
            {
                BoundingBoxD inside = new BoundingBoxD((Vector3D)pick.Grid.Min * pick.GridSize - pick.GridSize, (Vector3D)pick.Grid.Max * pick.GridSize + pick.GridSize);
                Vector3D sl = Vector3D.Transform(start, pick.Inv), gl = Vector3D.Transform(goal, pick.Inv);
                if (inside.Contains(sl) == ContainmentType.Disjoint && inside.Contains(gl) == ContainmentType.Disjoint)
                    outside = "start and goal outside " + pick.Grid.DisplayName;
                else
                {
                    // One end far out in the open: the field floods the open space to it (a scout's 316 m way out of its
                    // station: the field cut short after 89.5 ms, no route - 2026-11-19). The way out is the others'.
                    double far = Math.Max(Vector3D.Distance(sl, Vector3D.Clamp(sl, inside.Min, inside.Max)), Vector3D.Distance(gl, Vector3D.Clamp(gl, inside.Min, inside.Max)));
                    if (far > LATTICE_FAR_OUT)
                    {
                        latEntries.Clear();
                        why = string.Format("one end {0:F0} m out of {1} (the open: the other stages')", far, pick.Grid.DisplayName);
                        return false;
                    }
                }
            }
            // Not in a static grid: in an asteroid's rock (caves, tunnels, bores) - its own lattice (RockLattice)
            LocalScene.Entry rock = null;
            MatrixD rockWorld = MatrixD.Identity;
            Vector3I gMin, gMax;
            double gs;
            if (pick == null || outside != null)
            {
                if (!RockFrame(latEntries, start, goal, out rock, out rockWorld, out gMin, out gMax))
                {
                    latEntries.Clear();
                    why = outside ?? "no static grid or asteroid here";
                    return false;
                }
                latWorld = rockWorld;
                latInv = MatrixD.Invert(rockWorld);
                latGridName = "the rock of " + (rock.Entity.DisplayName ?? "an asteroid");
                gs = ROCK_CELL;
            }
            else
            {
                latWorld = pick.World;
                latInv = pick.Inv;
                latGridName = pick.Grid.DisplayName;
                gs = pick.GridSize;
                gMin = pick.Grid.Min;
                gMax = pick.Grid.Max;
            }
            latEntries.Clear();
            double step = gs * 0.5;
            lat.Step = step;
            lat.Half = HalfBox(ref q);
            lat.Berth = Berth(ref q);
            // The lies: the hull's forward / up along the grid's axes; level legs: its up on the grid axis nearest the vertical
            int upAxis = -1;
            latGravityUp = Vector3D.Zero;
            if (q.LevelLegs && q.GravityUp.LengthSquared() > 0.5)
            {
                for (int a = 0; a < 3; a++)
                    if (Math.Abs(Vector3D.Dot(GridAxis(ref latWorld, a), q.GravityUp)) > 0.95) upAxis = a;
                if (upAxis < 0) { why = "grid not level"; return false; }
                latGravityUp = q.GravityUp;
            }
            lat.SetPerms(upAxis >= 0, upAxis);
            // The lattice: round start and goal, within the grid's bounds (and a margin out of them)
            // The lattice: start and goal, and the grid LATTICE_REACH to their sides (the way round: a maze's other arm)
            // with a margin out of it
            Vector3D s = Vector3D.Transform(start, latInv) / step, g = Vector3D.Transform(goal, latInv) / step;
            double m = LATTICE_MARGIN / step;
            Vector3D sgLo = Vector3D.Min(s, g), sgHi = Vector3D.Max(s, g);
            Vector3D gLo = (Vector3D)gMin * 2 - m, gHi = (Vector3D)gMax * 2 + m;
            for (double wide = LATTICE_REACH / step; ; wide *= 0.5)
            {
                Vector3D lo = Vector3D.Min(sgLo - 4, Vector3D.Max(sgLo - wide, gLo)), hi = Vector3D.Max(sgHi + 4, Vector3D.Min(sgHi + wide, gHi));
                lat.Min = new Vector3I((int)Math.Floor(lo.X), (int)Math.Floor(lo.Y), (int)Math.Floor(lo.Z));
                lat.Max = new Vector3I((int)Math.Ceiling(hi.X), (int)Math.Ceiling(hi.Y), (int)Math.Ceiling(hi.Z));
                Vector3I size = lat.Max - lat.Min + Vector3I.One;
                if ((long)size.X * size.Y * size.Z <= LATTICE_MAX_POINTS) break;
                if (wide < m) { why = "area too big"; return false; }
            }
            // What is in the way there: the cells the boxes at the lattice's edge reach
            double reach = (lat.Half.Length() + lat.Berth + LatticeSearch.ROOM + 1) / gs + 1;
            var wMin = new Vector3I((int)Math.Floor(lat.Min.X * 0.5 - reach), (int)Math.Floor(lat.Min.Y * 0.5 - reach), (int)Math.Floor(lat.Min.Z * 0.5 - reach));
            var wMax = new Vector3I((int)Math.Ceiling(lat.Max.X * 0.5 + reach), (int)Math.Ceiling(lat.Max.Y * 0.5 + reach), (int)Math.Ceiling(lat.Max.Z * 0.5 + reach));
            // The window's own blocks a slice a tick, always: a drone that can hold waits for it (LatticeStage); a plan that
            // can't goes on without the lattice, the window goes on with the next ask (a scout's player route built a
            // 74 ms window in one tick, 2026-11-19)
            latShapes = rock != null ? RockWindowOf(rock, ref rockWorld, wMin, wMax, ref q) : WindowOf(pick, wMin, wMax, ref q, true);
            if (latShapes == null && windowTreeWait)
            {
                latWindowWait = true;
                why = "the grid's blocks being indexed";
                return false;
            }
            if (latShapes == null) { why = "area too big (or the grid's blocks not indexed yet)"; return false; }
            if (latShapes.Building)
            {
                latWindowWait = true;
                why = string.Format("window: {0} of {1} blocks set ({2:F1} ms so far)", latShapes.BuildIndex, latShapes.BuildSlims.Count, latShapes.BuildMs);
                return false;
            }
            latShapes.Occ.Forget();
            lat.Grid = latShapes.Occ;
            return true;
        }

        // The lattice points round 'p' (within 'steps'), nearest first, into latCandidates
        private void LatNear(Vector3D p, int steps)
        {
            latCandidates.Clear();
            Vector3D l = Vector3D.Transform(p, latInv) / lat.Step;
            int cx = (int)Math.Round(l.X), cy = (int)Math.Round(l.Y), cz = (int)Math.Round(l.Z);
            for (int dx = -steps; dx <= steps; dx++)
                for (int dy = -steps; dy <= steps; dy++)
                    for (int dz = -steps; dz <= steps; dz++)
                    {
                        int x = cx + dx, y = cy + dy, z = cz + dz;
                        if (!lat.Inside(x, y, z)) continue;
                        latCandidates.Add(new KeyValuePair<Vector3I, double>(new Vector3I(x, y, z), Vector3D.DistanceSquared(new Vector3D(x, y, z), l)));
                    }
            for (int k = 1; k < latCandidates.Count; k++)   // nearest first (insertion sort: Comparison<T> isn't allowed in mods)
            {
                var x = latCandidates[k];
                int m = k - 1;
                while (m >= 0 && latCandidates[m].Value > x.Value) { latCandidates[m + 1] = latCandidates[m]; m--; }
                latCandidates[m + 1] = x;
            }
        }

        private bool LatticeRoute(ref PathQuery q, Vector3D start, Vector3D goal, double maxMs, out string why)
        {
            // The corridor field, from the lattice points by the goal the hull's smallest cube has a straight way to it from
            Vector3D goalLocal = Vector3D.Transform(goal, latInv);
            double cube = Math.Min(lat.Half.X, Math.Min(lat.Half.Y, lat.Half.Z)) + lat.Berth + LatticeSearch.SLACK;
            LatNear(goal, 2);
            latSeeds.Clear();
            for (int pass = 0; pass < 2 && latSeeds.Count == 0; pass++)
                for (int c = 0; c < latCandidates.Count && latSeeds.Count < LATTICE_SEEDS; c++)
                {
                    var v = latCandidates[c].Key;
                    if (!lat.CubeFree(v.X, v.Y, v.Z)) continue;
                    if (pass == 0)
                    {
                        Vector3D pl = lat.Local(v.X, v.Y, v.Z);
                        var way = new BoundingBoxD(Vector3D.Min(pl, goalLocal) - cube, Vector3D.Max(pl, goalLocal) + cube);
                        if (!lat.Grid.BoxFree(ref way)) continue;
                    }
                    latSeeds.Add(v);
                }
            if (latSeeds.Count == 0) { why = "no room for the hull by the goal"; return false; }
            // (block shapes measured on the way - once per kind of block - get their own time)
            int measured0 = ShapesMeasured;
            // The field depends on the goal, the clearance, the hull and what is in the window - not on the start or the
            // drone: kept for the next plan to the same place (the drone re-plans on its way, its trip back comes again),
            // and a flood cut short by the clock goes on there (a big maze: never finished within one plan, 2026-11-16)
            long fkey = LatFieldKey();
            bool canWait = q.AsyncOk && !q.NoSearch && q.Owner != 0;   // the drone can hold while it goes on next tick
            bool cont = canWait && fkey != 0 && latJobOwner == q.Owner && latJobKey == fkey && frame - latJobFrame <= LATTICE_JOB_TICKS
                        && Vector3D.DistanceSquared(start, latJobStart) < 1 && Vector3D.DistanceSquared(goal, latJobGoal) < 1;
            if (!cont) { latJobOwner = 0; latJobMs = 0; latJobSearch = false; }
            bool moreTime = canWait && latJobMs < LATTICE_JOB_MS;
            double fieldUntil = (moreTime ? maxMs : maxMs * 0.6) + (measureExtra > 0 ? LATTICE_MEASURE_MS : 0);
            string fieldHow;
            if (fkey != 0 && lat.FieldKey == fkey && lat.FieldComplete) fieldHow = "kept";
            else if (fkey != 0 && lat.FieldKey == fkey && lat.FieldResumable)
            {
                lat.ContinueField(LATTICE_FIELD_POINTS, latClock, fieldUntil);
                fieldHow = "continued";
            }
            else if (fkey != 0 && LatFieldSetAside(fkey)) fieldHow = "kept aside";
            else
            {
                if (!lat.BuildField(latSeeds, LATTICE_FIELD_POINTS, latClock, fieldUntil)) { why = "area too big"; return false; }
                fieldHow = "built";
            }
            // (built with cells counted whole for want of their shapes: built again next time)
            lat.FieldKey = latShapes.Occ.UnknownCells.Count == 0 && ShapesMeasured == measured0 ? fkey : 0;
            if (lat.FieldKey != 0 && lat.FieldComplete && (fieldHow == "built" || fieldHow == "continued")) LatFieldSetAside();
            latFieldHow = fieldHow;
            double fieldMs = latClock.Elapsed.TotalMilliseconds;
            if (ShapesMeasured > measured0) maxMs = fieldMs + maxMs * 0.7;
            if (!lat.FieldComplete && lat.FieldResumable && moreTime)
            {
                LatJobHold(ref q, start, goal, fkey, false);
                why = string.Format("the corridor field ({0}): {1} points so far", fieldHow, lat.FieldReached);
                return false;
            }
            // Block shapes still being measured (their cells counted whole: a way through them looks closed): on with the
            // measuring next tick, the field built again then
            if (latShapes.Occ.UnknownCells.Count > 0 && moreTime)
            {
                LatJobHold(ref q, start, goal, fkey, false);
                why = string.Format("{0} cell(s) of blocks not measured yet", latShapes.Occ.UnknownCells.Count);
                return false;
            }
            bool resumeSearch = cont && latJobSearch && lat.SearchResumable;

            // The ways in: start -> the nearest lattice points the hull has a checked straight way to, in each lie it can
            // have there (a lie that needs a turn at the start: only with room to turn)
            MatrixD now = q.Controller;
            Vector3D curF = now.Forward, curU = now.Up;
            bool turnRoom = false, turnAsked = false;
            latTurns.Clear();
            latTurnsTried = latTurnsOk = 0;
            if (!resumeSearch) LatNear(start, 3);
            else latCandidates.Clear();   // (the search goes on from its open states: the starts are in them)
            if (!resumeSearch) latStarts.Clear();
            int joined = resumeSearch ? 1 : 0, tries = 0;
            for (int c = 0; c < latCandidates.Count && tries < LATTICE_JOIN_TRIES && joined < LATTICE_JOIN_KEEP; c++)
            {
                if (latClock.Elapsed.TotalMilliseconds > maxMs)
                {
                    if (moreTime)
                    {
                        LatJobHold(ref q, start, goal, fkey, false);
                        why = string.Format("joining the start ({0} point(s) tried)", tries);
                        return false;
                    }
                    why = string.Format("time budget (joining the start: {0} point(s) tried, {1} turn(s) in place; corridor field ({2}): {3} points{4}, {5:F1} ms)", tries, latTurnsTried, latFieldHow, lat.FieldReached, lat.FieldComplete ? "" : ", cut short", fieldMs);
                    return false;
                }
                var v = latCandidates[c].Key;
                int f = lat.FieldAt(v.X, v.Y, v.Z);
                if (f == -2 || (f < 0 && lat.FieldComplete)) continue;
                Vector3D pt = LatPoint(v.X, v.Y, v.Z);
                bool any = false, tried = false;
                for (int p = 0; p < lat.Perms; p++)
                {
                    if (!lat.PoseFree(v.X, v.Y, v.Z, p)) continue;
                    tried = true;
                    Vector3D lf, lu;
                    LieAttitude(p, curF, curU, pt - start, out lf, out lu);
                    bool turn = Vector3D.Dot(lf, curF) < TURN_FREE_COS || Vector3D.Dot(lu, curU) < TURN_FREE_COS;
                    // (the way there first: one sweep; the turn in place is many boxes - checked only for a way that is clear)
                    MatrixD att = MatrixD.CreateWorld(Vector3D.Zero, lf, lu);
                    Vector3D hit;
                    IMyEntity e;
                    if (SegmentBlocked(ref q, ref att, start, pt, 0.5, out hit, out e)) continue;
                    if (turn)
                    {
                        if (!turnAsked) { turnAsked = true; turnRoom = TurnRoomFree(ref q, start, TurnRadius(ref q, q.LocalHull.Center)); }
                        if (!turnRoom && !TurnInPlaceFree(ref q, start, curF, curU, lf, lu)) continue;
                    }
                    latStarts.Add(new LatticeSearch.Start { X = v.X, Y = v.Y, Z = v.Z, Perm = p, G = Vector3D.Distance(start, pt) + (turn ? lat.Step * LatticeSearch.TURN_COST : 0) });
                    any = true;
                }
                if (tried) tries++;
                if (any) joined++;
            }
            if (joined == 0)
            {
                why = lat.FieldComplete && tries == 0 ? string.Format("no way through for the hull's smallest box (the corridor field: {0} points from the goal, none by the start)", lat.FieldReached)
                    : string.Format("no lattice point reachable from the start ({0} tried{1})", tries,
                                    turnAsked && !turnRoom ? string.Format(", no room to turn there; {0} turn(s) in place into a lie tried, {1} with room", latTurnsTried, latTurnsOk) : "");
                return false;
            }

            for (int attempt = 0; ; attempt++)
            {
                var qq = q;
                Vector3D goalW = goal;
                Func<LatticeSearch.Node, bool> join = n =>
                {
                    MatrixD att = LieMatrix(n.Perm);
                    Vector3D hit;
                    IMyEntity e;
                    return !SegmentBlocked(ref qq, ref att, LatPoint(n.X, n.Y, n.Z), goalW, 0, out hit, out e, !qq.LooseGoal);
                };
                string searchWhy;
                bool found;
                if (attempt == 0 && resumeSearch) found = lat.ContinueSearch(goalLocal, join, latClock, maxMs, out searchWhy);
                else
                {
                    lat.Reset();
                    found = lat.Search(latStarts, goalLocal, join, latClock, maxMs, out searchWhy);
                }
                if (!found && lat.SearchResumable && canWait && latJobMs + latClock.Elapsed.TotalMilliseconds < LATTICE_JOB_MS)
                {
                    LatJobHold(ref q, start, goal, fkey, true);
                    why = string.Format("searching ({0} states, {1} checks so far)", lat.Opened, lat.Checks);
                    return false;
                }
                if (!found)
                {
                    why = string.Format("{0} ({1} states, {2} checks; corridor field ({6}): {3} points{4}, {5:F1} ms)", searchWhy, lat.Opened, lat.Checks, lat.FieldReached,
                                        lat.FieldComplete ? "" : ", cut short", fieldMs, latFieldHow);
                    return false;
                }
                BuildLatLegs(start, goal);
                int blocked = CheckLatLegs(ref q, attempt);
                if (blocked == 0) break;
                if (attempt >= LATTICE_RETRIES) { why = string.Format("legs blocked by other things ({0} tries)", attempt + 1); return false; }
            }
            int raw = latLegs.Count;
            PullLatLegs(ref q);
            // The answer: the legs' ends, each leg's attitude (the least turn from the one before)
            points.Clear();
            points.Add(start);
            Vector3D pf = curF, pu = curU;
            for (int i = 0; i < latLegs.Count; i++)
            {
                var leg = latLegs[i];
                Vector3D lf, lu;
                LieAttitude(leg.Perm, pf, pu, leg.B - leg.A, out lf, out lu);
                if (legHints.Count >= MAX_LEG_HINTS) legHints.RemoveAt(0);
                legHints.Add(new RouteMemory.LegHint { A = leg.A, B = leg.B, Fwd = lf, Up = lu });
                points.Add(leg.B);
                pf = lf;
                pu = lu;
            }
            if (Log.DebugEnabled) Log.Debug("Pathfinding: lattice of {0} ({1:F2} m steps, {2} lies, {3}): {4} states, {5} checks, corridor field {6} points ({7:F1} ms, {11}), {8} legs ({9} before straightening), {10:F1} ms ({12}; {13:F1} ms over earlier ticks)", latGridName, lat.Step, lat.Perms, latShapes.Boxes ? "cell boxes" : "real shapes", lat.Opened, lat.Checks, lat.FieldReached, fieldMs, latLegs.Count, raw, latClock.Elapsed.TotalMilliseconds, latFieldHow, latWindowNote, latJobMs);
            why = null;
            return true;
        }

        private readonly List<LatticeSearch.Leg> latRuns = new List<LatticeSearch.Leg>();

        // Turning in place into a lie where the turn sphere has no room (a drone stopped mid-corridor in a custom route's
        // recorded attitude, a few degrees off the grid: "no lattice point reachable from the start, no room to turn
        // there", stuck - 2026-11-14): TurnInPlaceFits. Any turn (a lie change is 90° or 120°; up to 45° left it stuck
        // by the catwalk, 2026-11-14).
        private int latTurnsTried, latTurnsOk;
        private const int LATTICE_TURNS_MAX = 6;   // turns in place tried per start
        private string latFieldHow;                // the field this plan: built / kept / continued (the log)

        // Finished fields set aside (a trip there and back alternates two goals: each plan flooded its field again, ~25 ms,
        // 2026-11-19): the latest LATTICE_FIELDS_KEPT, by key
        private const int LATTICE_FIELDS_KEPT = 4;
        private readonly List<LatticeSearch.FieldCopy> latFields = new List<LatticeSearch.FieldCopy>();

        // The field just finished, set aside (in place of the oldest)
        private void LatFieldSetAside()
        {
            LatticeSearch.FieldCopy c = null;
            for (int i = 0; i < latFields.Count; i++) if (latFields[i].Key == lat.FieldKey) { c = latFields[i]; break; }
            if (c == null && latFields.Count < LATTICE_FIELDS_KEPT) { c = new LatticeSearch.FieldCopy(); latFields.Add(c); }
            if (c == null)
            {
                c = latFields[0];
                for (int i = 1; i < latFields.Count; i++) if (latFields[i].Used < c.Used) c = latFields[i];
            }
            lat.SaveField(c);
            c.Used = frame;
        }

        // The field with key 'fkey', set aside before: back as the lattice's. False: none
        private bool LatFieldSetAside(long fkey)
        {
            for (int i = 0; i < latFields.Count; i++)
            {
                var c = latFields[i];
                if (c.Key != fkey) continue;
                if (!lat.LoadField(c)) return false;
                c.Used = frame;
                return true;
            }
            return false;
        }

        // The key of a corridor field: the window (its build, its doors' states), the lattice, the clearance, the hull, the seeds
        private long LatFieldKey()
        {
            if (latShapes == null) return 0;
            long h = latShapes.Key;
            h = h * 31 + latShapes.Built;
            h = h * 31 + latShapes.DoorsHash();
            h = h * 31 + lat.Min.GetHashCode();
            h = h * 31 + lat.Max.GetHashCode();
            h = h * 31 + (long)Math.Round(lat.Berth * 1000);
            h = h * 31 + (long)Math.Round(lat.Half.X * 1000) * 7 + (long)Math.Round(lat.Half.Y * 1000) * 11 + (long)Math.Round(lat.Half.Z * 1000) * 13;
            for (int i = 0; i < latSeeds.Count; i++) h = h * 31 + latSeeds[i].GetHashCode();
            return h == 0 ? 1 : h;
        }
        private readonly Dictionary<int, bool> latTurns = new Dictionary<int, bool>();   // this start's turns, by lie (grid axes)

        private bool TurnInPlaceFree(ref PathQuery q, Vector3D centre, Vector3D f0, Vector3D u0, Vector3D f1, Vector3D u1)
        {
            int key = LatAxisKey(f1) * 6 + LatAxisKey(u1);
            bool ok;
            if (latTurns.TryGetValue(key, out ok)) return ok;
            if (latTurnsTried >= LATTICE_TURNS_MAX) return false;   // (each is up to 72 boxes: the join's time budget went on them)
            MatrixD m0 = MatrixD.CreateWorld(Vector3D.Zero, f0, u0), m1 = MatrixD.CreateWorld(Vector3D.Zero, f1, u1);
            double deg;
            ok = TurnInPlaceFits(ref q, centre, ref m0, ref m1, out deg);
            latTurns[key] = ok;
            latTurnsTried++;
            if (ok)
            {
                latTurnsOk++;
                if (Log.DebugEnabled) Log.Debug("Pathfinding: lattice: a turn of {0:F0}° in place into a lie at the start: room", deg);
            }
            return ok;
        }

        // A lie's axis (world), as a grid axis of the lattice: 0..5
        private int LatAxisKey(Vector3D v)
        {
            Vector3D l = Vector3D.TransformNormal(v, latInv);
            double ax = Math.Abs(l.X), ay = Math.Abs(l.Y), az = Math.Abs(l.Z);
            return ax >= ay && ax >= az ? (l.X > 0 ? 0 : 1) : ay >= az ? (l.Y > 0 ? 2 : 3) : (l.Z > 0 ? 4 : 5);
        }

        // The search's answer as legs: start -> first lattice point, the straight runs in one lie, last point -> goal
        private void BuildLatLegs(Vector3D start, Vector3D goal)
        {
            latLegs.Clear();
            var path = lat.Path;
            var first = path[0];
            Vector3D at = LatPoint(first.X, first.Y, first.Z);
            if (Vector3D.DistanceSquared(start, at) > 1e-4) latLegs.Add(new LatLeg { A = start, B = at, Perm = first.Perm });
            lat.Legs(latRuns);
            for (int i = 0; i < latRuns.Count; i++)
            {
                var r = latRuns[i];
                latLegs.Add(new LatLeg { A = LatPoint(r.A.X, r.A.Y, r.A.Z), B = LatPoint(r.B.X, r.B.Y, r.B.Z), Perm = r.Perm, LA = r.A, LB = r.B, Lattice = true });
            }
            latRuns.Clear();
            var end = path[path.Count - 1];
            Vector3D endAt = LatPoint(end.X, end.Y, end.Z);
            if (Vector3D.DistanceSquared(endAt, goal) > 1e-4) latLegs.Add(new LatLeg { A = endAt, B = goal, Perm = end.Perm });
        }

        // The legs against everything (other grids, a parked drone, rock), each in its lie, as the drone checks them: how
        // many are blocked; their lattice points are left out of the next search
        // (only the poses round where it was hit: a whole leg banned - a corridor's long straight run - sent the search
        // round the maze and out of time, 2026-11-13)
        private int CheckLatLegs(ref PathQuery q, int attempt)
        {
            int blocked = 0;
            for (int i = 0; i < latLegs.Count; i++)
            {
                var leg = latLegs[i];
                if (!leg.Lattice) continue;   // (the joins were checked)
                MatrixD att = LieMatrix(leg.Perm);
                Vector3D hit;
                IMyEntity e;
                if (!SegmentBlocked(ref q, ref att, leg.A, leg.B, 0, out hit, out e)) continue;
                blocked++;
                Vector3I d = leg.LB - leg.LA;
                int steps = Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z);
                Vector3I u = steps > 0 ? new Vector3I(Math.Sign(d.X), Math.Sign(d.Y), Math.Sign(d.Z)) : Vector3I.Zero;
                Vector3D dir = leg.B - leg.A;
                double len = dir.Length();
                if (len > 1e-6) dir /= len;
                double at = Vector3D.Dot(hit - leg.A, dir);
                double reach = lat.Half.Length() + lat.Berth + lat.Step + (LastHitBlock != null ? LastHitBlock.CubeGrid.GridSize : 2.5);
                int banned = 0;
                for (int s = 0; s <= steps; s++)
                {
                    if (Math.Abs(s * lat.Step - at) > reach) continue;
                    Vector3I p = leg.LA + u * s;
                    lat.Banned.Add(lat.Key(p.X, p.Y, p.Z, 7));
                    banned++;
                }
                if (Log.DebugEnabled)
                {
                    var hb = LastHitBlock;
                    Vector3D hl = Vector3D.Transform(hit, latInv) / (lat.Step * 2);
                    Log.Debug("Pathfinding: lattice (try {0}): leg {1} of {2} ({3:F1} m, lie {4}) blocked by {5}{6} {7} at {8:F1} m (cell {9:F1} {10:F1} {11:F1} of {12}): {13} lattice point(s) left out",
                              attempt + 1, i + 1, latLegs.Count, len, leg.Perm, e != null ? e.DisplayName : "?", hb != null ? " (" + BlockName(hb) + ")" : "",
                              e is IMyCubeGrid && ((IMyCubeGrid)e).IsStatic ? "static" : "not static", at, hl.X, hl.Y, hl.Z, latGridName, banned);
                }
            }
            return blocked;
        }

        // Runs of legs in one lie: straight from a leg's start to the furthest later end of the run it has ObstacleClearance of
        // room to (the staircase the lattice makes of a slanting way, a corner it squares in the open)
        private void PullLatLegs(ref PathQuery q)
        {
            if (latLegs.Count < 3) return;
            var wide = q;
            wide.Berth = Math.Max(Berth(ref q), config.ObstacleClearance);   // (the route clearance)
            latPulled.Clear();
            int i = 0;
            while (i < latLegs.Count)
            {
                var from = latLegs[i];
                int reach = i;
                if (i < latLegs.Count - 1)   // (the last leg ends at the goal: as it is)
                {
                    MatrixD att = LieMatrix(from.Perm);
                    for (int j = i + 1; j < latLegs.Count - 1 && latLegs[j].Perm == from.Perm && j - i <= 24 && !BudgetSpent; j++)
                    {
                        Vector3D hit;
                        IMyEntity e;
                        if (!SegmentBlocked(ref wide, ref att, from.A, latLegs[j].B, i == 0 ? 0.5 : 0, out hit, out e)) reach = j;
                    }
                }
                if (reach > i) latPulled.Add(new LatLeg { A = from.A, B = latLegs[reach].B, Perm = from.Perm });
                else latPulled.Add(from);
                i = reach + 1;
            }
            latLegs.Clear();
            latLegs.AddRange(latPulled);
            latPulled.Clear();
        }
    }
}
