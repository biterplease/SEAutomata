using System;
using System.Collections.Generic;

using Sandbox.Game.Entities;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Stage 3 of the planner's chain (TODO/20, section 4): Lazy Theta* over a grid of hull-sized cells, built on
    /// demand inside a box around the stretch that failed.
    ///  - A cell is free when the hull, centred anywhere in it, keeps its clearance. Cells are answered through
    ///    bricks of 4, 16 and 64 cells: a free brick answers for all its cells at once, so open space costs a few
    ///    checks however big; only bricks with something in them are looked at more closely. Every answer is kept for
    ///    the search.
    ///  - Blocks: the scene's block boxes (HullSpaceFree), as far as the drone sees. Asteroids: the voxel storage's own
    ///    octree (IMyStorage.Intersect), no rays. What the drone can't see counts as free, with a cost penalty, so the
    ///    search prefers what it has seen.
    ///  - Lazy: line of sight between a cell and its grandparent is assumed when the cell is reached and checked when
    ///    it is expanded (a walk through the cells); paths are straight lines between any cells, not grid steps.
    ///  - Bounded: ThetaMaxExpansions cells and ThetaMaxMs per search. A goal further than 2 x ThetaSearchRadius is
    ///    searched for up to a point on the way; stage 1 continues from there.
    ///  - The answer's stretches are checked like any route (SegmentBlocked) before it is used.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private const double THETA_UNSEEN_COST = 1.5;     // cost factor through cells the drone can't see
        private const double THETA_CELL_MIN = 1.5;        // m
        private const double THETA_CELL_HULL = 0.75;      // cell size per m of hull radius

        private enum CellState : byte { Unknown = 0, Free = 1, Blocked = 2, Mixed = 3, FreeUnseen = 4 }

        private sealed class ThetaNode : FastPriorityQueueNode
        {
            public long Key;
            public int Level, BX, BY, BZ;   // a free leaf: the brick (4^Level cells a side) at BX, BY, BZ
            public Vector3D Pos;            // its centre (the real start / target for theirs)
            public bool Unseen;
            public double G;
            public ThetaNode Parent;
            public bool Closed;
        }

        // Search state (one search at a time: the server's main thread)
        private readonly Dictionary<long, ThetaNode> thetaNodes = new Dictionary<long, ThetaNode>();
        private readonly Dictionary<long, CellState>[] thetaLevels =
        {
            new Dictionary<long, CellState>(), new Dictionary<long, CellState>(),
            new Dictionary<long, CellState>(), new Dictionary<long, CellState>(),
        };
        private readonly List<MyVoxelBase> thetaAsteroids = new List<MyVoxelBase>();
        private readonly List<Vector3D> thetaPath = new List<Vector3D>();
        private readonly List<ThetaNode> thetaNeighbours = new List<ThetaNode>();
        private readonly HashSet<long> thetaSeenKeys = new HashSet<long>();
        private Vector3D thetaOrigin;
        private double thetaCell, thetaHull;
        private int thetaN;   // cells per axis
        private int thetaProbes, thetaLos;
        private const double THETA_WEIGHT = 2.0;   // weighted A*: far fewer cells round big obstacles; the shortcut straightens the result

        public bool ThetaAvailable { get { return config.AllowLazyTheta; } }

        // Held attitudes (the hull box as it is, or turned 90° about its axes) when the any-attitude search finds nothing
        private const double THETA_FREE_SHARE = 0.5;     // of the time / expansion budget for the any-attitude search
        private const int THETA_HELD_MAX = 6;            // attitudes tried: as it is first, then the least turned
        private bool thetaHeld;                          // the search in progress holds q.Controller's attitude
        private Quaternion thetaRot;
        private Vector3D thetaBoxHalf;                   // the hull's half size + the berth (held attitude)
        private readonly List<MatrixD> thetaHeldFrames = new List<MatrixD>();
        private readonly List<Vector3D> thetaHeldKeys = new List<Vector3D>();

        /// <summary>
        /// A way from 'start' to 'goal' found by the search, as points (start and goal included) in 'points'. False:
        /// none ('why'). First with the hull in any attitude (the sphere round it; half the budget). Nothing: the
        /// drone's box is rarely a cube - in its own attitude, then turned 90° about one axis or two, it may fit where
        /// the sphere doesn't (a bay, between beams). Those searches share the rest of the budget, its own attitude
        /// first; another attitude only where it can turn to it at the start. Found that way: q.Controller has that
        /// attitude and q.HoldAttitude is set (the whole route keeps it).
        /// </summary>
        private bool ThetaStage(ref PathQuery q, Vector3D start, Vector3D goal, out string why)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            double ms = config.ThetaMaxMs;
            int exp = config.ThetaMaxExpansions;
            bool canHold = q.HullCentred && q.LocalHull.Max.X >= q.LocalHull.Min.X;
            thetaHeld = false;
            if (ThetaSearch(ref q, start, goal, canHold ? ms * THETA_FREE_SHARE : ms,
                            canHold ? Math.Max(16, (int)(exp * THETA_FREE_SHARE)) : exp, out why)) return true;
            if (!canHold) return false;
            HeldCandidates(ref q, start);
            if (q.NoHeldTurn && thetaHeldFrames.Count > 1) thetaHeldFrames.RemoveRange(1, thetaHeldFrames.Count - 1);   // near a dock: as it is only
            MatrixD controller = q.Controller;
            bool eyesSet = q.EyesSet;
            MatrixD eyes = q.Eyes;
            if (!eyesSet) { q.Eyes = controller; q.EyesSet = true; }
            string last = null;
            int tried = 0;
            for (int i = 0; i < thetaHeldFrames.Count; i++)
            {
                double left = ms - total.Elapsed.TotalMilliseconds;
                if (left < 0.25) break;
                int remaining = thetaHeldFrames.Count - i;
                double share = i == 0 && remaining > 1 ? left * 0.5 : left / remaining;   // its own attitude: half of what is left
                MatrixD f = thetaHeldFrames[i];
                f.Translation = controller.Translation;
                q.Controller = f;
                thetaHeld = true;
                thetaRot = Quaternion.CreateFromRotationMatrix(f);
                thetaBoxHalf = q.LocalHull.HalfExtents + new Vector3D(Berth(ref q));
                string w;
                bool ok = ThetaSearch(ref q, start, goal, share, Math.Max(16, (int)(exp * share / ms)), out w);
                thetaHeld = false;
                tried++;
                if (ok)
                {
                    q.HoldAttitude = true;
                    if (Log.DebugEnabled) Log.Debug("Pathfinding: Lazy Theta* found a way holding the drone's attitude{0} (any-attitude search: {1})",
                              i == 0 ? " as it is" : string.Format(", turned {0:F0}° at the start", TurnDegrees(ref controller, ref f)), why);
                    why = null;
                    return true;
                }
                last = w;
            }
            q.Controller = controller;
            q.EyesSet = eyesSet;
            q.Eyes = eyes;
            why += string.Format("; held attitudes: {0} of {1} tried{2}", tried, thetaHeldFrames.Count, last != null ? " (last: " + last + ")" : "");
            return false;
        }

        private static double TurnDegrees(ref MatrixD a, ref MatrixD b)
        {
            double c = (Vector3D.Dot(a.Forward, b.Forward) + Vector3D.Dot(a.Up, b.Up) + Vector3D.Dot(a.Right, b.Right) - 1) * 0.5;
            return MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(c, -1, 1)));
        }

        // The attitudes to hold: as it is first, then 90° turns about its axes (each distinct box shape once, the least
        // turned), in gravity only those whose up stays within the pitch limit (0.7 without one); a turned one only
        // where the drone has room to turn to it at the start
        private void HeldCandidates(ref PathQuery q, Vector3D start)
        {
            thetaHeldFrames.Clear();
            thetaHeldKeys.Clear();
            MatrixD c = q.Controller;
            c.Translation = Vector3D.Zero;
            Vector3D h = q.LocalHull.HalfExtents;
            bool inGravity = q.GravityUp.LengthSquared() > 0.5;
            double minUp = q.MaxPitch >= 0 ? Math.Cos(q.MaxPitch) : 0.7;
            Vector3D[] axes = { c.Right, c.Up, c.Backward, c.Left, c.Down, c.Forward };
            var scored = new List<KeyValuePair<double, MatrixD>>();
            for (int fi = 0; fi < 6; fi++)
                for (int ui = 0; ui < 6; ui++)
                {
                    if (fi % 3 == ui % 3) continue;   // up must be across forward
                    MatrixD f = MatrixD.CreateWorld(Vector3D.Zero, axes[fi], axes[ui]);
                    double score = Vector3D.Dot(f.Forward, c.Forward) + Vector3D.Dot(f.Up, c.Up);
                    bool same = score > 1.999;
                    if (!same && inGravity && Vector3D.Dot(f.Up, q.GravityUp) < minUp) continue;
                    scored.Add(new KeyValuePair<double, MatrixD>(score, f));
                }
            scored.Sort((x, y) => y.Key.CompareTo(x.Key));   // as it is first, then the least turned
            for (int i = 0; i < scored.Count && thetaHeldFrames.Count < THETA_HELD_MAX; i++)
            {
                MatrixD f = scored[i].Value;
                // The box's size along the current axes: one shape per turn that changes it
                Vector3D key = new Vector3D(Extent(ref f, ref h, c.Right), Extent(ref f, ref h, c.Up), Extent(ref f, ref h, c.Backward));
                bool dup = false;
                for (int k = 0; k < thetaHeldKeys.Count && !dup; k++) dup = Vector3D.DistanceSquared(thetaHeldKeys[k], key) < 0.01;
                if (dup) continue;
                // A turn only with the full clearance around it (a bare fit turned into dock platforms)
                if (thetaHeldFrames.Count > 0 && !TurnSweepFree(ref q, start, ref f, Berth(ref q))) continue;
                thetaHeldKeys.Add(key);
                thetaHeldFrames.Add(f);
            }
        }

        // Half size along world axis 'w' of the hull box (half extents 'h') in attitude 'f'
        private static double Extent(ref MatrixD f, ref Vector3D h, Vector3D w)
        {
            return Math.Abs(Vector3D.Dot(f.Right, w)) * h.X + Math.Abs(Vector3D.Dot(f.Up, w)) * h.Y + Math.Abs(Vector3D.Dot(f.Backward, w)) * h.Z;
        }

        private bool ThetaSearch(ref PathQuery q, Vector3D start, Vector3D goal, double maxMs, int maxExpansions, out string why)
        {
            why = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            thetaProbes = thetaLos = 0;
            thetaHull = thetaHeld ? thetaBoxHalf.Length() : HullRadius(ref q) + Berth(ref q);
            thetaCell = Math.Max(THETA_CELL_MIN, HullRadius(ref q) * THETA_CELL_HULL);
            double radius = config.ThetaSearchRadius;
            Vector3D target = goal;
            double dist = Vector3D.Distance(start, goal);
            bool far = dist > 2 * radius;
            if (far) target = start + (goal - start) / dist * (2 * radius);

            // The grid: a cube around start and target with a margin each side; cells per axis a multiple of 64 (bricks)
            Vector3D mid = (start + target) * 0.5;
            double span = Vector3D.Distance(start, target) + 2 * Math.Max(radius * 0.5, 8 * thetaCell);
            thetaN = Math.Max(64, (int)Math.Ceiling(span / thetaCell / 64) * 64);
            if (thetaN > 1024) { thetaN = 1024; thetaCell = span / thetaN; }
            thetaOrigin = mid - new Vector3D(thetaN * thetaCell * 0.5);
            for (int i = 0; i < thetaLevels.Length; i++) thetaLevels[i].Clear();
            thetaNodes.Clear();

            // Asteroids in the box, once
            thetaAsteroids.Clear();
            var box = new BoundingBoxD(thetaOrigin, thetaOrigin + new Vector3D(thetaN * thetaCell));
            var sphere = BoundingSphereD.CreateFromBoundingBox(box);
            scene.Ensure(sphere, frame);
            scene.Overlapping(ref box, entryBuffer, frame);
            for (int i = 0; i < entryBuffer.Count; i++)
            {
                var v = entryBuffer[i].Entity as MyVoxelBase;
                if (entryBuffer[i].Voxel && v != null && IsAsteroid(v) && v.Storage != null) thetaAsteroids.Add(v);
            }
            entryBuffer.Clear();

            int sx, sy, sz, gx, gy, gz;
            if (!CellOf(start, out sx, out sy, out sz) || !CellOf(target, out gx, out gy, out gz)) { why = "outside the search box"; return false; }
            ThetaNode goalNode = LeafNode(ref q, gx, gy, gz);
            // A target in a blocked cell (far: the point on the way): the nearest free cell towards the start instead
            if (goalNode == null && far)
            {
                Vector3D back = (start - target) / Vector3D.Distance(start, target);
                for (int k = 1; k < 64 && goalNode == null; k++)
                {
                    Vector3D p = target + back * (k * thetaCell);
                    if (CellOf(p, out gx, out gy, out gz)) { goalNode = LeafNode(ref q, gx, gy, gz); if (goalNode != null) target = p; }
                }
            }
            if (goalNode == null && !far)
            {
                // The goal next to something (the hull's clearance is counted generously here): its own cell, as it is
                goalNode = NodeAt(ref q, 0, gx, gy, gz);
            }
            if (goalNode == null) { why = "no free space towards the goal"; return false; }
            goalNode.Pos = target;
            ThetaNode s = LeafNode(ref q, sx, sy, sz) ?? NodeAt(ref q, 0, sx, sy, sz);   // a start close to something: its own cell
            if (s == goalNode) { points.Clear(); points.Add(start); points.Add(goal); return CheckThetaPath(ref q, start, goal, far, 0, sw, out why); }
            s.Pos = start;
            s.G = 0;
            s.Parent = s;

            var open = new FastPriorityQueue<ThetaNode>(maxExpansions * 16 + 64);
            open.Enqueue(s, THETA_WEIGHT * Vector3D.Distance(start, target));
            int expansions = 0;
            ThetaNode reached = null;
            while (open.Count > 0)
            {
                if (expansions >= maxExpansions) { why = "expansion budget"; break; }
                if (sw.Elapsed.TotalMilliseconds > maxMs) { why = "time budget"; break; }
                var cur = open.Dequeue();
                if (cur.Closed) continue;
                expansions++;
                Neighbours(ref q, cur);
                // Lazy: the parent assumed when it was reached is checked now; not visible: the best closed neighbour
                if (cur.Parent != cur && !LineOfSight(ref q, cur.Parent.Pos, cur.Pos))
                {
                    ThetaNode bestP = null;
                    double bestG = double.MaxValue;
                    for (int n = 0; n < thetaNeighbours.Count; n++)
                    {
                        var nb = thetaNeighbours[n];
                        if (!nb.Closed) continue;
                        double g = nb.G + Cost(nb, cur);
                        if (g < bestG) { bestG = g; bestP = nb; }
                    }
                    if (bestP == null) continue;
                    cur.Parent = bestP;
                    cur.G = bestG;
                }
                cur.Closed = true;
                if (cur == goalNode) { reached = cur; break; }
                var p = cur.Parent;
                for (int n = 0; n < thetaNeighbours.Count; n++)
                {
                    var nb = thetaNeighbours[n];
                    if (nb.Closed) continue;
                    // Path 2 of Theta*: straight from the parent (line of sight assumed, checked when expanded)
                    double g = p.G + Cost(p, nb);
                    if (g < nb.G)
                    {
                        nb.G = g;
                        nb.Parent = p;
                        double f = g + THETA_WEIGHT * Vector3D.Distance(nb.Pos, target);
                        if (open.Contains(nb)) open.UpdatePriority(nb, f);
                        else if (open.Count < open.MaxSize) open.Enqueue(nb, f);
                    }
                }
            }
            if (reached == null)
            {
                why = (why ?? "no way through") + string.Format(" ({0} cells expanded, {1} probes, {2} sight checks, {3:F1} ms)",
                                                               expansions, thetaProbes, thetaLos, sw.Elapsed.TotalMilliseconds);
                return false;
            }
            thetaPath.Clear();
            for (var c = reached; c != null; c = c.Parent == c ? null : c.Parent) thetaPath.Add(c.Pos);
            thetaPath.Reverse();
            points.Clear();
            points.AddRange(thetaPath);
            return CheckThetaPath(ref q, target, goal, far, expansions, sw, out why);
        }

        // The answer ('points', start to target) checked stretch by stretch like any route; far: the rest to the goal
        // added by the straight stage
        private bool CheckThetaPath(ref PathQuery q, Vector3D target, Vector3D goal, bool far, int expansions,
                                    System.Diagnostics.Stopwatch sw, out string why)
        {
            why = null;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector3D hit;
                IMyEntity e;
                bool toGoal = !far && i + 2 == points.Count;
                if (SegmentBlocked(ref q, ref q.Controller, points[i], points[i + 1], 0.5, out hit, out e, !(toGoal && q.LooseGoal)))
                {
                    why = string.Format("its stretch {0} of {1} is blocked by {2} ({3} cells, {4:F1} ms)", i + 1, points.Count - 1,
                                        e != null ? e.DisplayName : "?", expansions, sw.Elapsed.TotalMilliseconds);
                    points.Clear();
                    return false;
                }
            }
            int found = points.Count;
            if (far)
            {
                var head = new List<Vector3D>(points);
                string why2;
                bool shaped;
                budgetBase = planChecks + planRays;   // the search's probes don't eat the rest's budget
                if (!StraightStage(ref q, target, goal, out why2, out shaped)) { why = "the rest after the search: " + why2; points.Clear(); return false; }
                var tail = new List<Vector3D>(points);
                points.Clear();
                points.AddRange(head);
                for (int i = 1; i < tail.Count; i++) points.Add(tail[i]);
            }
            if (Log.DebugEnabled) Log.Debug("Pathfinding: Lazy Theta* found {0} points in {1} cells expanded ({2} probes, {3} sight checks, cell {4:F1} m, {5}^3 grid), {6:F1} ms{7}",
                      found, expansions, thetaProbes, thetaLos, thetaCell, thetaN, sw.Elapsed.TotalMilliseconds, far ? ", the rest by the straight stage" : "");
            return true;
        }

        private static long Key(int x, int y, int z) { return ((long)x << 40) | ((long)y << 20) | (long)z; }

        private ThetaNode NodeAt(ref PathQuery q, int level, int bx, int by, int bz)
        {
            long k = ((long)level << 60) | Key(bx, by, bz);
            ThetaNode n;
            if (!thetaNodes.TryGetValue(k, out n))
            {
                double size = thetaCell * (1 << (2 * level));
                var pos = thetaOrigin + new Vector3D((bx + 0.5) * size, (by + 0.5) * size, (bz + 0.5) * size);
                n = new ThetaNode { Key = k, Level = level, BX = bx, BY = by, BZ = bz, Pos = pos, G = double.MaxValue, Unseen = !Seen(ref q, pos) };
                thetaNodes[k] = n;
            }
            return n;
        }

        // The free leaf (the biggest free brick) holding cell x, y, z; null: the cell is blocked
        private ThetaNode LeafNode(ref PathQuery q, int x, int y, int z)
        {
            for (int l = 3; l >= 0; l--)
            {
                var st = Probe(ref q, x, y, z, l);
                if (st == CellState.Free || st == CellState.FreeUnseen) { int sh = 2 * l; return NodeAt(ref q, l, x >> sh, y >> sh, z >> sh); }
                if (st == CellState.Blocked) return null;
            }
            return null;
        }

        private bool CellOf(Vector3D p, out int x, out int y, out int z)
        {
            Vector3D r = (p - thetaOrigin) / thetaCell;
            x = (int)Math.Floor(r.X); y = (int)Math.Floor(r.Y); z = (int)Math.Floor(r.Z);
            return x >= 0 && y >= 0 && z >= 0 && x < thetaN && y < thetaN && z < thetaN;
        }

        private bool CellFree(ref PathQuery q, int x, int y, int z)
        {
            return LeafNode(ref q, x, y, z) != null;
        }

        private static double Cost(ThetaNode a, ThetaNode b)
        {
            double d = Vector3D.Distance(a.Pos, b.Pos);
            return b.Unseen ? d * THETA_UNSEEN_COST : d;
        }

        // The free leaves next to 'n' (into thetaNeighbours): cells just outside each face, edge and corner, a few per
        // face of a big brick (smaller neighbours along it)
        private void Neighbours(ref PathQuery q, ThetaNode n)
        {
            thetaNeighbours.Clear();
            thetaSeenKeys.Clear();
            int m = 1 << (2 * n.Level);
            int x0 = n.BX * m, y0 = n.BY * m, z0 = n.BZ * m;
            int k = Math.Min(m, 4);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        int cx = dx != 0 ? 1 : k, cy = dy != 0 ? 1 : k, cz = dz != 0 ? 1 : k;
                        for (int i = 0; i < cx; i++)
                            for (int j = 0; j < cy; j++)
                                for (int l = 0; l < cz; l++)
                                {
                                    int x = dx < 0 ? x0 - 1 : dx > 0 ? x0 + m : x0 + (int)(m * (i + 0.5) / k);
                                    int y = dy < 0 ? y0 - 1 : dy > 0 ? y0 + m : y0 + (int)(m * (j + 0.5) / k);
                                    int z = dz < 0 ? z0 - 1 : dz > 0 ? z0 + m : z0 + (int)(m * (l + 0.5) / k);
                                    if (x < 0 || y < 0 || z < 0 || x >= thetaN || y >= thetaN || z >= thetaN) continue;
                                    var leaf = LeafNode(ref q, x, y, z);
                                    if (leaf == null || leaf == n || !thetaSeenKeys.Add(leaf.Key)) continue;
                                    thetaNeighbours.Add(leaf);
                                }
                    }
        }

        // The state of the brick at 'level' (0: one cell; 1..3: 4, 16, 64 cells a side) holding cell x, y, z. Kept for
        // the search. Free: the hull fits anywhere in it; Blocked: a cell with something in its reach, or all rock;
        // Mixed: look closer
        private CellState Probe(ref PathQuery q, int x, int y, int z, int level)
        {
            int shift = 2 * level;
            int bx = x >> shift, by = y >> shift, bz = z >> shift;
            long k = Key(bx, by, bz);
            var cache = thetaLevels[level];
            CellState st;
            if (cache.TryGetValue(k, out st)) return st;
            thetaProbes++;
            double size = thetaCell * (1 << shift);
            Vector3D min = thetaOrigin + new Vector3D(bx * size, by * size, bz * size);
            Vector3D centre = min + new Vector3D(size * 0.5);
            // A brick: the hull must fit anywhere in it (half diagonal plus the hull and its clearance). A single cell: the
            // hull at its centre, with a quarter cell of slack - the half diagonal made every cell of a cramped bay
            // (drones, station beams a few metres apart) blocked, and the search died at its start cell; lines between
            // cells are checked cell by cell, and the answer exactly (CheckThetaPath)
            double reach = (level == 0 ? size * 0.25 : size * 0.8660254) + thetaHull;
            bool seen = Seen(ref q, centre);
            st = CellState.Free;
            if (thetaHeld)
            {
                // The hull box in the held attitude, anywhere in the brick / near the cell's centre
                double grow = reach - thetaHull;
                var box = new MyOrientedBoundingBoxD(centre, thetaBoxHalf + new Vector3D(grow), thetaRot);
                if (!BoxFree(ref q, ref box)) st = CellState.Mixed;
            }
            else if (!HullSpaceFree(ref q, centre, reach)) st = CellState.Mixed;
            if (seen && thetaAsteroids.Count > 0)
            {
                var vox = VoxelState(min - new Vector3D(thetaHull), min + new Vector3D(size + thetaHull));
                if (vox == ContainmentType.Contains && st == CellState.Free) st = CellState.Blocked;
                else if (vox != ContainmentType.Disjoint) st = CellState.Mixed;
            }
            if (st == CellState.Mixed && level == 0) st = CellState.Blocked;
            if (st == CellState.Free && !seen && level == 0) st = CellState.FreeUnseen;
            cache[k] = st;
            return st;
        }

        // Asteroids against a world box: Disjoint (no rock), Contains (all rock) or Intersects
        private ContainmentType VoxelState(Vector3D min, Vector3D max)
        {
            var wb = new BoundingBoxD(min, max);
            ContainmentType result = ContainmentType.Disjoint;
            for (int i = 0; i < thetaAsteroids.Count; i++)
            {
                var v = thetaAsteroids[i];
                if (v.Closed || v.Storage == null || !v.PositionComp.WorldAABB.Intersects(wb)) continue;
                // Storage coordinates: from the map's corner (asteroids aren't rotated), plus its storage offset
                Vector3D a = min - v.PositionLeftBottomCorner + v.StorageMin, b = max - v.PositionLeftBottomCorner + v.StorageMin;
                var sb = new BoundingBox((Vector3)a, (Vector3)b);
                var c = v.Storage.Intersect(ref sb, true);
                if (c == ContainmentType.Contains) return ContainmentType.Contains;
                if (c == ContainmentType.Intersects) result = ContainmentType.Intersects;
            }
            return result;
        }

        // Straight line of sight: every cell the line passes through belongs to a free leaf
        private bool LineOfSight(ref PathQuery q, Vector3D pa, Vector3D pb)
        {
            thetaLos++;
            double len = Vector3D.Distance(pa, pb);
            int steps = (int)Math.Ceiling(len / (thetaCell * 0.5));
            for (int i = 1; i < steps; i++)
            {
                Vector3D p = pa + (pb - pa) * (i / (double)steps);
                int x, y, z;
                if (!CellOf(p, out x, out y, out z) || !CellFree(ref q, x, y, z)) return false;
            }
            return true;
        }
    }
}
