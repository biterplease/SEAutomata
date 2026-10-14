using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Construction
{
    /// <summary>
    /// Build order for a whole projection, as lines (one job each), planned once from the blueprint (every projected
    /// block is known up front; mount points via the projected grid's neighbours). The lines are made of nodes: on large
    /// grids a node is one block (projected grid cells); on small grids a 3x3x3 work unit of the physical grid (the
    /// BuildPlanner's alignment, the boxes drones aim a tool position at: the welder reaches through a welded block or
    /// two, so a unit is worked from one spot) with every projected block in it.
    /// <list type="number">
    /// <item>Trunk: the seed (a block buildable now, attached to the built grid; its node) and its row along the level
    /// axis with the longest row (A): the first job. Then the row the other way.</item>
    /// <item>First plane (A x B, horizontal: normal C = the grid axis nearest "up"): from each trunk node in turn, a
    /// line along -B, then +B; then lines along A from those (shapes that aren't convex), until the plane is done.</item>
    /// <item>Second plane (A x C through the seed): a line along -C, then +C from the seed; rows along A from it.</item>
    /// <item>Layers parallel to the first plane, C-1, C+1, C-2, C+2...: each grows from the second plane's row in it
    /// (lines along -B / +B), else from the layer it rests on.</item>
    /// <item>The rest: lines along any axis from anything planned or built.</item>
    /// </list>
    /// Lines longer than a job (in blocks) are cut into segments, each attached to the end of the one before. A line
    /// becomes a job when one of its first node's blocks can be built (the node it grows from is built): the scan finds
    /// that block buildable, the rest of its line goes along (predicted). The two walls cross at the seed; everything
    /// after grows outwards from them, so no wall closes off a part still to build. Server only; C# 6.
    /// </summary>
    public sealed class ProjectionPlan
    {
        private sealed class Node
        {
            public Vector3I Min, Max, Position;   // plan lattice: projected grid cells (large) / unit coordinates (small)
            public readonly List<IMySlimBlock> Blocks = new List<IMySlimBlock>();   // large: the block; small: the unit's, in build order
            public bool HasWork;                  // something in it to build (at planning time)
            public int Line = -1, Pos;
        }

        private sealed class Line
        {
            public int Index;      // plan order
            public int Stage;      // STAGE_*
            public readonly List<Node> Nodes = new List<Node>();
            public int BlockCount;
        }

        public const int STAGE_TRUNK = 0, STAGE_FIRST_PLANE = 1, STAGE_SECOND_PLANE = 2, STAGE_LAYERS = 3, STAGE_REST = 4;
        private static readonly string[] StageNames = { "trunk", "first plane", "second plane", "layers", "rest" };

        // Identity: a change makes a new plan
        public long ProjectedId;
        public Vector3I Offset, Rotation;
        public Vector3D AreaCenter, AreaHalf;
        public int MaxLen, UpAxis;
        public int LastUsed;

        private readonly IMyCubeGrid projected, physical;
        private readonly bool units;                      // small grid: nodes are 3x3x3 units of the physical grid
        private readonly HashSet<IMySlimBlock> eligible;  // in the area, not weld-ignore colored, not built (at planning time)
        private readonly List<Node> nodes = new List<Node>();
        private readonly Dictionary<IMySlimBlock, Node> nodeOf = new Dictionary<IMySlimBlock, Node>();
        private readonly Dictionary<Vector3I, Node> unitAt = new Dictionary<Vector3I, Node>();
        private readonly List<Line> lines = new List<Line>();
        private readonly Dictionary<int, List<Node>>[] layers = new Dictionary<int, List<Node>>[3];   // by axis, then coordinate
        private Vector3I latticeMin = Vector3I.MaxValue, latticeMax = Vector3I.MinValue;
        private int plannedBlocks, eligibleBlocks;

        private struct Grow
        {
            public Node From;
            public int Axis;
            public int FirstSign;
        }
        private readonly Queue<Grow> queue = new Queue<Grow>();
        private readonly List<Node> walk = new List<Node>();
        private readonly List<Node> row = new List<Node>();
        private readonly List<IMySlimBlock> neighbours = new List<IMySlimBlock>();
        private readonly int[] stageBlocks = new int[5];
        private int stage;

        /// <summary>
        /// 'unitSeed': small grids, the physical cell the unit lattice is centred on (BuildPlanner.EnsureUnitFrame);
        /// ignored on large grids.
        /// </summary>
        public ProjectionPlan(IMyProjector projector, List<IMySlimBlock> eligibleBlocks, List<IMySlimBlock> builtBlocks, Vector3I unitSeed)
        {
            projected = projector.ProjectedGrid;
            physical = projector.CubeGrid;
            units = physical.GridSizeEnum == VRage.Game.MyCubeSize.Small;
            eligible = new HashSet<IMySlimBlock>(eligibleBlocks);
            for (int i = 0; i < eligibleBlocks.Count; i++) AddBlock(eligibleBlocks[i], unitSeed);
            for (int i = 0; i < builtBlocks.Count; i++) AddBlock(builtBlocks[i], unitSeed);
            this.eligibleBlocks = eligible.Count;
            for (int i = 0; i < nodes.Count; i++)
            {
                latticeMin = Vector3I.Min(latticeMin, nodes[i].Min);
                latticeMax = Vector3I.Max(latticeMax, nodes[i].Max);
            }
        }

        private void AddBlock(IMySlimBlock b, Vector3I unitSeed)
        {
            bool work = eligible.Contains(b);
            Node n;
            if (!units)
            {
                n = new Node { Min = b.Min, Max = b.Max, Position = b.Position };
                nodes.Add(n);
            }
            else
            {
                Vector3I cell, min, max;
                ConstructionComputer.PhysicalExtent(b, physical, out cell, out min, out max);
                Vector3I u = BuildPlanner.UnitOf(cell, unitSeed);
                if (!unitAt.TryGetValue(u, out n))
                {
                    n = new Node { Min = u, Max = u, Position = u };
                    unitAt[u] = n;
                    nodes.Add(n);
                }
            }
            n.Blocks.Add(b);
            if (work) n.HasWork = true;
            nodeOf[b] = n;
        }

        public int LineCount { get { return lines.Count; } }
        public int PlannedCount { get { return plannedBlocks; } }
        public int EligibleCount { get { return eligibleBlocks; } }
        public bool Units { get { return units; } }

        public string StageSummary()
        {
            var sb = new System.Text.StringBuilder();
            for (int s = 0; s < stageBlocks.Length; s++)
            {
                if (s > 0) sb.Append(", ");
                sb.Append(StageNames[s]).Append(' ').Append(stageBlocks[s]);
            }
            return sb.ToString();
        }

        /// <summary>A block to build that is in a planned line.</summary>
        public bool Planned(IMySlimBlock b)
        {
            Node n;
            return eligible.Contains(b) && nodeOf.TryGetValue(b, out n) && n.Line >= 0;
        }

        /// <summary>Plan order of a planned block (line, node, place in the node); -1 when not planned.</summary>
        public long Order(IMySlimBlock b)
        {
            Node n;
            if (!eligible.Contains(b) || !nodeOf.TryGetValue(b, out n) || n.Line < 0) return -1;
            return ((long)n.Line * 4096 + n.Pos) * 64 + Math.Min(63, n.Blocks.IndexOf(b));
        }

        /// <summary>The blocks after 'b' in its line still to build (large: the next nodes; small: the rest of its unit, then the
        /// next units; at most 'max').</summary>
        public int Rest(IMySlimBlock b, List<IMySlimBlock> into, int max)
        {
            Node n;
            if (max <= 0 || !nodeOf.TryGetValue(b, out n) || n.Line < 0) return 0;
            var line = lines[n.Line];
            int count = 0;
            for (int k = n.Pos; k < line.Nodes.Count && count < max; k++)
            {
                var blocks = line.Nodes[k].Blocks;
                // Its own node: every other block of it (a unit is worked from one spot, whichever block opened it)
                for (int i = k == n.Pos && !units ? blocks.IndexOf(b) + 1 : 0; i < blocks.Count && count < max; i++)
                {
                    var x = blocks[i];
                    if (x == b) continue;
                    if (!eligible.Contains(x) || IsBuilt(x)) continue;   // built meanwhile (by hand, a neighbour's sphere)
                    into.Add(x);
                    count++;
                }
            }
            return count;
        }

        public bool IsBuilt(IMySlimBlock b)
        {
            Vector3I cell, min, max;
            ConstructionComputer.PhysicalExtent(b, physical, out cell, out min, out max);
            return physical.CubeExists(cell);
        }

        #region Planning
        /// <summary>Plans everything reachable from 'seed' (a buildable block whose node isn't planned yet) in the stages above.</summary>
        public void PlanFrom(IMySlimBlock seedBlock)
        {
            Node seed;
            if (seedBlock == null || !eligible.Contains(seedBlock) || !nodeOf.TryGetValue(seedBlock, out seed) || seed.Line >= 0) return;
            if (units) SortUnit(seed, seedBlock);
            int up = UpAxis;

            // Trunk axis: the longest row from the seed, level first (a vertical one only when nothing runs level)
            int a = -1, aSign = -1, best = 0;
            for (int pass = 0; pass < 2 && best == 0; pass++)
                for (int axis = 0; axis < 3; axis++)
                {
                    if ((axis == up) != (pass == 1)) continue;
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        int len = Walk(seed, axis, sign, -1, 0);
                        if (len > best) { best = len; a = axis; aSign = sign; }
                    }
                }
            walk.Clear();
            if (a < 0) { a = up == 0 ? 2 : 0; aSign = -1; }
            int c = a != up ? up : (up + 1) % 3;   // normal of the first plane
            int b = 3 - a - c;

            // Trunk: the seed and its row in the first plane, one job (segments if longer)
            stage = STAGE_TRUNK;
            int cSeed = Get(seed.Position, c);
            Walk(seed, a, aSign, c, cSeed);
            walk.Insert(0, seed);
            int trunkFirst = lines.Count;
            AddLine(null);
            int trunkEnd = lines.Count;

            // First plane: the trunk the other way, then from each trunk node a branch along -B, then +B; and so on
            stage = STAGE_FIRST_PLANE;
            queue.Enqueue(new Grow { From = seed, Axis = a, FirstSign = -aSign });
            for (int l = trunkFirst; l < trunkEnd; l++)
                for (int i = 0; i < lines[l].Nodes.Count; i++) queue.Enqueue(new Grow { From = lines[l].Nodes[i], Axis = b, FirstSign = -1 });
            FillPlane(c, cSeed, null, 0, 0, false);

            // Second plane: from the seed along C, rows along A
            stage = STAGE_SECOND_PLANE;
            FillPlane(b, Get(seed.Position, b), seed, c, -1, false);

            // Layers off the second plane, outwards from the first: C-1, C+1, C-2, C+2...
            stage = STAGE_LAYERS;
            int c0 = Get(seed.Position, c), lo = Get(latticeMin, c), hi = Get(latticeMax, c), b0 = Get(seed.Position, b);
            for (int k = 1; c0 - k >= lo || c0 + k <= hi; k++)
                for (int side = -1; side <= 1; side += 2)
                {
                    int y = c0 + side * k;
                    if (y < lo || y > hi) continue;
                    List<Node> layer;
                    if (!Layer(c).TryGetValue(y, out layer)) continue;
                    // The second plane's row in this layer, nearest the seed first: lines along B from it
                    row.Clear();
                    for (int i = 0; i < layer.Count; i++)
                    {
                        var p = layer[i];
                        if (Get(p.Min, b) <= b0 && b0 <= Get(p.Max, b) && Placed(p)) row.Add(p);
                    }
                    int a0 = Get(seed.Position, a);
                    row.Sort((p, q) => Math.Abs(Get(p.Position, a) - a0).CompareTo(Math.Abs(Get(q.Position, a) - a0)));
                    for (int i = 0; i < row.Count; i++) queue.Enqueue(new Grow { From = row[i], Axis = b, FirstSign = -1 });
                    row.Clear();
                    FillPlane(c, y, null, 0, 0, true);
                }

            // The rest: any axis, from anything planned or built
            stage = STAGE_REST;
            for (int i = 0; i < nodes.Count; i++)
                if (Placed(nodes[i]))
                    for (int axis = 0; axis < 3; axis++) queue.Enqueue(new Grow { From = nodes[i], Axis = axis, FirstSign = -1 });
            Bfs(-1, 0);
            walk.Clear();
        }

        /// <summary>
        /// One plane (normal axis 'normal' at 'n'): lines from 'from' along 'axis' (if given) and whatever is queued, then
        /// branches off each new line; then lines from every planned / built node in it, and (layers) steps in from the
        /// layer it rests on, until nothing more joins.
        /// </summary>
        private void FillPlane(int normal, int n, Node from, int axis, int firstSign, bool stepIn)
        {
            if (from != null) queue.Enqueue(new Grow { From = from, Axis = axis, FirstSign = firstSign });
            Bfs(normal, n);
            List<Node> plane;
            if (!Layer(normal).TryGetValue(n, out plane)) return;
            for (int round = 0; round < 64; round++)
            {
                int before = plannedBlocks;
                int a1 = (normal + 1) % 3, a2 = (normal + 2) % 3;
                for (int i = 0; i < plane.Count; i++)
                    if (Placed(plane[i]))
                    {
                        queue.Enqueue(new Grow { From = plane[i], Axis = a1, FirstSign = -1 });
                        queue.Enqueue(new Grow { From = plane[i], Axis = a2, FirstSign = -1 });
                    }
                Bfs(normal, n);
                if (stepIn)
                {
                    // From the layers either side (the inner one is done by now): one node across into this one
                    for (int side = -1; side <= 1; side += 2)
                    {
                        List<Node> other;
                        if (!Layer(normal).TryGetValue(n - side, out other)) continue;
                        for (int i = 0; i < other.Count; i++)
                            if (Placed(other[i])) queue.Enqueue(new Grow { From = other[i], Axis = normal, FirstSign = side });
                    }
                    Bfs(normal, n);
                }
                if (plannedBlocks == before) break;
            }
        }

        // Lines from the queued nodes; each new line's nodes then branch across it (in the plane, or both other axes)
        private void Bfs(int normal, int n)
        {
            while (queue.Count > 0)
            {
                var g = queue.Dequeue();
                int first = g.FirstSign != 0 ? g.FirstSign : -1;
                for (int k = 0; k < 2; k++)
                {
                    int sign = k == 0 ? first : -first;
                    if (g.Axis == normal && sign != first) continue;   // a step across: one way only
                    if (Walk(g.From, g.Axis, sign, normal, n) == 0) continue;
                    int at = lines.Count;
                    AddLine(g.From);
                    for (int l = at; l < lines.Count; l++)
                    {
                        var ln = lines[l].Nodes;
                        for (int i = 0; i < ln.Count; i++)
                        {
                            if (normal >= 0)
                            {
                                if (g.Axis == normal)
                                {
                                    queue.Enqueue(new Grow { From = ln[i], Axis = (normal + 1) % 3, FirstSign = -1 });
                                    queue.Enqueue(new Grow { From = ln[i], Axis = (normal + 2) % 3, FirstSign = -1 });
                                }
                                else queue.Enqueue(new Grow { From = ln[i], Axis = 3 - g.Axis - normal, FirstSign = -1 });
                            }
                            else
                            {
                                queue.Enqueue(new Grow { From = ln[i], Axis = (g.Axis + 1) % 3, FirstSign = -1 });
                                queue.Enqueue(new Grow { From = ln[i], Axis = (g.Axis + 2) % 3, FirstSign = -1 });
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Into 'walk': the row from 'from' along 'axis' / 'sign' (on from's row), while each node has work, is not
        /// planned, is in the plane (normal &lt; 0: anywhere) and is joined to the one before by mount points. Its length.
        /// </summary>
        private int Walk(Node from, int axis, int sign, int normal, int n)
        {
            walk.Clear();
            var prev = from;
            Vector3I at = from.Position;
            for (int guard = 0; guard < 4096; guard++)
            {
                at = With(at, axis, Get(sign > 0 ? prev.Max : prev.Min, axis) + sign);
                var next = NodeAt(at);
                if (next == null || next == prev || !next.HasWork || next.Line >= 0 || walk.Contains(next)) break;
                if (normal >= 0 && (Get(next.Min, normal) > n || Get(next.Max, normal) < n)) break;
                if (!Joined(prev, next)) break;   // mount points don't meet: it wouldn't attach
                if (AllBuilt(next)) break;        // built since the plan was made
                walk.Add(next);
                prev = next;
            }
            return walk.Count;
        }

        private Node NodeAt(Vector3I at)
        {
            Node n;
            if (units) return unitAt.TryGetValue(at, out n) ? n : null;
            var b = projected.GetCubeBlock(at);
            return b != null && nodeOf.TryGetValue(b, out n) ? n : null;
        }

        // Some block of 'a' and some block of 'b' meet by mount points (b can be built onto a)
        private bool Joined(Node a, Node b)
        {
            for (int i = 0; i < a.Blocks.Count; i++)
            {
                neighbours.Clear();
                a.Blocks[i].GetNeighbours(neighbours);
                for (int j = 0; j < neighbours.Count; j++)
                {
                    Node m;
                    if (nodeOf.TryGetValue(neighbours[j], out m) && m == b) { neighbours.Clear(); return true; }
                }
            }
            neighbours.Clear();
            return false;
        }

        private bool AllBuilt(Node n)
        {
            for (int i = 0; i < n.Blocks.Count; i++)
                if (eligible.Contains(n.Blocks[i]) && !IsBuilt(n.Blocks[i])) return false;
            return true;
        }

        // 'walk' as one line, or segments of at most MaxLen blocks (each attached to the end of the one before)
        private void AddLine(Node attach)
        {
            int max = Math.Max(1, MaxLen);
            Line line = null;
            Node prev = attach;
            for (int i = 0; i < walk.Count; i++)
            {
                var n = walk[i];
                if (units && prev != null) SortUnit(n, null, prev);   // (the seed's unit: sorted from its seed block)
                int work = 0;
                for (int k = 0; k < n.Blocks.Count; k++) if (eligible.Contains(n.Blocks[k])) work++;
                if (line == null || (line.BlockCount > 0 && line.BlockCount + work > max))
                {
                    line = new Line { Index = lines.Count, Stage = stage };
                    lines.Add(line);
                }
                n.Line = line.Index;
                n.Pos = line.Nodes.Count;
                line.Nodes.Add(n);
                line.BlockCount += work;
                plannedBlocks += work;
                stageBlocks[stage] += work;
                prev = n;
            }
            walk.Clear();
        }

        /// <summary>
        /// A unit's blocks in build order: from the side it grows from (the seed block / the blocks nearest the node before),
        /// then each next one nearest those already ordered (a block attaches to its neighbours).
        /// </summary>
        private void SortUnit(Node n, IMySlimBlock first, Node from = null)
        {
            if (n.Blocks.Count < 2) return;
            Vector3D origin;
            if (first != null) origin = (Vector3D)(first.Min + first.Max) * 0.5;
            else if (from != null && from.Blocks.Count > 0) origin = Centre(from);
            else return;
            // Physical cells would match the lattice; the projected grid's are as good for distances (one rigid transform)
            n.Blocks.Sort((p, q) => Vector3D.DistanceSquared(Centre(p), origin).CompareTo(Vector3D.DistanceSquared(Centre(q), origin)));
            if (first != null && n.Blocks.Remove(first)) n.Blocks.Insert(0, first);
        }

        private static Vector3D Centre(IMySlimBlock b)
        {
            return (Vector3D)(b.Min + b.Max) * 0.5;
        }

        private static Vector3D Centre(Node n)
        {
            Vector3D s = Vector3D.Zero;
            for (int i = 0; i < n.Blocks.Count; i++) s += Centre(n.Blocks[i]);
            return s / Math.Max(1, n.Blocks.Count);
        }

        // Planned, or nothing to build in it (built / outside the work): a node lines may grow from
        private bool Placed(Node n)
        {
            return n.Line >= 0 || !n.HasWork || AllBuilt(n);
        }

        // Nodes by their coordinate on 'axis' (multi-cell blocks in each layer they reach)
        private Dictionary<int, List<Node>> Layer(int axis)
        {
            var d = layers[axis];
            if (d != null) return d;
            d = new Dictionary<int, List<Node>>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var nd = nodes[i];
                for (int v = Get(nd.Min, axis); v <= Get(nd.Max, axis); v++)
                {
                    List<Node> list;
                    if (!d.TryGetValue(v, out list)) { list = new List<Node>(); d[v] = list; }
                    list.Add(nd);
                }
            }
            layers[axis] = d;
            return d;
        }
        #endregion

        public static int Get(Vector3I v, int axis)
        {
            return axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        }

        private static Vector3I With(Vector3I v, int axis, int value)
        {
            if (axis == 0) v.X = value; else if (axis == 1) v.Y = value; else v.Z = value;
            return v;
        }

        /// <summary>The axis of 'grid' (0 X, 1 Y, 2 Z) nearest 'up', world.</summary>
        public static int UpAxisOf(IMyCubeGrid grid, Vector3D up)
        {
            MatrixD m = grid.WorldMatrix;
            double x = Math.Abs(Vector3D.Dot(m.Right, up)), y = Math.Abs(Vector3D.Dot(m.Up, up)), z = Math.Abs(Vector3D.Dot(m.Backward, up));
            return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
        }
    }
}
