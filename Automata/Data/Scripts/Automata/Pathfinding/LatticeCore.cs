using System;
using System.Collections.Generic;

using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// A block's real shape in one of its cells (grid axes): empty, the whole cell, or N x N x N voxels (bit
    /// x + N y + N N z). Measured once per block definition, orientation and cell (PathfindingManager.Shapes).
    /// </summary>
    public struct BlockShape
    {
        public const int N = 5;                 // voxels per cell edge (a large block: 0.5 m)
        public const int COUNT = N * N * N;
        public byte Kind;                       // 0 empty, 1 full, 2 voxels
        public ulong Lo, Hi;

        public static BlockShape Full { get { return new BlockShape { Kind = 1 }; } }

        public bool Solid(int bit)
        {
            return bit < 64 ? (Lo & (1UL << bit)) != 0 : (Hi & (1UL << (bit - 64))) != 0;
        }

        public void Set(int bit)
        {
            Kind = 2;
            if (bit < 64) Lo |= 1UL << bit; else Hi |= 1UL << (bit - 64);
        }

        /// <summary>Voxels all set: the whole cell; none: empty.</summary>
        public void Normalise()
        {
            if (Kind != 2) return;
            int n = 0;
            for (int b = 0; b < COUNT; b++) if (Solid(b)) n++;
            if (n == 0) { Kind = 0; Lo = Hi = 0; }
            else if (n == COUNT) { Kind = 1; Lo = Hi = 0; }
        }
    }

    /// <summary>
    /// Measures a block's real shape in one cell with rays ('cast': grid coordinates in, where the ray meets the grid's
    /// blocks out - a ray sees a surface only from outside it). Along each grid axis, N x N lines through the voxel
    /// centres; each voxel's stretch of a line is cast from both sides: where the line enters solid and where it leaves
    /// it. A voxel is solid where a line of any axis runs through solid. 6 N^3 rays.
    /// </summary>
    public sealed class ShapeMeasurer
    {
        public delegate void RayCaster(Vector3D from, Vector3D to, List<Vector3D> hits);
        public const double PAD = 0.03;          // m a voxel's ray starts / ends outside it
        public const int RAYS = 6 * BlockShape.COUNT;

        private readonly bool[] enter = new bool[BlockShape.N], leave = new bool[BlockShape.N];
        private readonly double[] enterAt = new double[BlockShape.N], leaveAt = new double[BlockShape.N];
        private readonly List<Vector3D> hits = new List<Vector3D>();

        private static double Axis(Vector3D p, int a) { return a == 0 ? p.X : a == 1 ? p.Y : p.Z; }
        private static void SetAxis(ref Vector3D p, int a, double value) { if (a == 0) p.X = value; else if (a == 1) p.Y = value; else p.Z = value; }

        /// <summary>The shape of what is in the cell whose min corner is 'c0' ('gs' wide).</summary>
        public BlockShape Measure(Vector3D c0, double gs, RayCaster cast)
        {
            const int N = BlockShape.N;
            double v = gs / N;
            var shape = new BlockShape();
            for (int a = 0; a < 3; a++)
            {
                int b = (a + 1) % 3, d = (a + 2) % 3;
                double a0 = Axis(c0, a);
                for (int i = 0; i < N; i++)
                    for (int j = 0; j < N; j++)
                    {
                        Vector3D p = c0;
                        SetAxis(ref p, b, Axis(c0, b) + (i + 0.5) * v);
                        SetAxis(ref p, d, Axis(c0, d) + (j + 0.5) * v);
                        for (int k = 0; k < N; k++) enter[k] = leave[k] = false;
                        for (int k = 0; k < N; k++)
                        {
                            Vector3D lo = p, hi = p;
                            SetAxis(ref lo, a, a0 + k * v - PAD);
                            SetAxis(ref hi, a, a0 + (k + 1) * v + PAD);
                            Cast(cast, lo, hi, a, a0, v, 1);
                            Cast(cast, hi, lo, a, a0, v, -1);
                        }
                        // Solid from where the line enters it to where it leaves it
                        bool inside = false;
                        for (int k = 0; k < N; k++)
                        {
                            bool solid = inside || enter[k] || leave[k];
                            if (enter[k]) inside = !leave[k] || enterAt[k] > leaveAt[k];
                            else if (leave[k]) inside = false;
                            if (!solid) continue;
                            int vx = a == 0 ? k : b == 0 ? i : j, vy = a == 1 ? k : b == 1 ? i : j, vz = a == 2 ? k : b == 2 ? i : j;
                            shape.Set(vx + N * vy + N * N * vz);
                        }
                    }
            }
            shape.Normalise();
            return shape;
        }

        /// <summary>
        /// Thin parts the voxel lines slip between (a railing's bars, rods, a mesh: railings measured empty, 2026-11-14).
        /// A bar is met by lines across it, close together across the bar and far apart along it: for each axis the
        /// lines run along, each of the other two in 'fine' m steps with the third at the N voxel centres, cast both ways;
        /// the voxel behind every surface met is set in 'shape'. Bars down to about 'fine' thick, 0.5 m long. Returns
        /// the rays cast.
        /// </summary>
        public int MeasureFine(Vector3D c0, double gs, RayCaster cast, double fine, ref BlockShape shape)
        {
            const int N = BlockShape.N;
            double v = gs / N;
            int k = (int)Math.Ceiling(gs / fine), rays = 0;
            double s = gs / k;
            for (int a = 0; a < 3; a++)
                for (int pass = 0; pass < 2; pass++)
                {
                    int f = (a + 1 + pass) % 3, c = (a + 2 - pass) % 3;   // fine axis, coarse axis
                    for (int i = 0; i < N; i++)
                        for (int j = 0; j < k; j++)
                        {
                            Vector3D lo = c0;
                            SetAxis(ref lo, c, Axis(c0, c) + (i + 0.5) * v);
                            SetAxis(ref lo, f, Axis(c0, f) + (j + 0.5) * s);
                            Vector3D hi = lo;
                            SetAxis(ref lo, a, Axis(c0, a) - PAD);
                            SetAxis(ref hi, a, Axis(c0, a) + gs + PAD);
                            for (int sign = 1; sign >= -1; sign -= 2)
                            {
                                hits.Clear();
                                if (sign > 0) cast(lo, hi, hits);
                                else cast(hi, lo, hits);
                                rays++;
                                for (int n = 0; n < hits.Count; n++)
                                {
                                    Vector3D h = hits[n];
                                    SetAxis(ref h, a, Axis(h, a) + sign * 0.01);   // (into the solid behind the surface)
                                    int x = (int)Math.Floor((h.X - c0.X) / v), y = (int)Math.Floor((h.Y - c0.Y) / v), z = (int)Math.Floor((h.Z - c0.Z) / v);
                                    if (x < 0 || y < 0 || z < 0 || x >= N || y >= N || z >= N) continue;
                                    shape.Set(x + N * y + N * N * z);
                                }
                                hits.Clear();
                            }
                        }
                }
            shape.Normalise();
            return rays;
        }

        // One voxel's stretch of a line, cast one way ('sign' along axis a): the surfaces it meets, each in the voxel
        // the solid behind it starts in
        private void Cast(RayCaster cast, Vector3D from, Vector3D to, int a, double a0, double v, int sign)
        {
            hits.Clear();
            cast(from, to, hits);
            for (int n = 0; n < hits.Count; n++)
            {
                double at = Axis(hits[n], a);
                int k = (int)Math.Floor((at + sign * 0.01 - a0) / v);
                if (k < 0 || k >= BlockShape.N) continue;
                if (sign > 0)
                {
                    if (!enter[k] || at < enterAt[k]) enterAt[k] = at;
                    enter[k] = true;
                }
                else
                {
                    if (!leave[k] || at > leaveAt[k]) leaveAt[k] = at;
                    leave[k] = true;
                }
            }
            hits.Clear();
        }
    }

    /// <summary>
    /// The blocks of one grid over a window of its cells, for box queries in the grid's own coordinates (metres): per
    /// cell empty / full / shaped (a BlockShape, asked from 'Resolve' the first time). Plain arrays: a query is a few
    /// dozen lookups (no tree, no rays). Main thread (Resolve may measure a shape).
    /// </summary>
    public sealed class ShapeGrid
    {
        public const double EPS = 1e-3;         // m: touching isn't overlapping
        public const byte EMPTY = 0, FULL = 1, SHAPED = 2, UNKNOWN = 3;
        public const int DYNAMIC = -2;          // ShapeIndex: asked every time (a door: its shape changes)

        public double GridSize = 2.5;
        public Vector3I Min, Size;              // the window (cells)
        public byte[] Kind;
        public int[] ShapeIndex;                // SHAPED: index into Shapes; -1: not asked yet; DYNAMIC
        public readonly List<BlockShape> Shapes = new List<BlockShape>();
        /// <summary>Cell -> its shape; false: not known yet (the cell counts as full until Forget).</summary>
        public ResolveShape Resolve;
        public delegate bool ResolveShape(int x, int y, int z, out BlockShape shape);
        /// <summary>Voxels other things (another static grid's blocks) take in SHAPED cells (cell index ->): both count.</summary>
        public Dictionary<int, BlockShape> Overlay;
        /// <summary>Cells met whose shape isn't known yet (full meanwhile), since the last Forget.</summary>
        public readonly List<int> UnknownCells = new List<int>();

        public void Init(Vector3I min, Vector3I size, double gridSize)
        {
            Min = min;
            Size = size;
            GridSize = gridSize;
            int n = size.X * size.Y * size.Z;
            Kind = new byte[n];
            ShapeIndex = new int[n];
            for (int i = 0; i < n; i++) ShapeIndex[i] = -1;
            Shapes.Clear();
            UnknownCells.Clear();
        }

        public bool Covers(Vector3I min, Vector3I max)
        {
            return Kind != null && min.X >= Min.X && min.Y >= Min.Y && min.Z >= Min.Z
                && max.X < Min.X + Size.X && max.Y < Min.Y + Size.Y && max.Z < Min.Z + Size.Z;
        }

        public int Index(int x, int y, int z)
        {
            int ix = x - Min.X, iy = y - Min.Y, iz = z - Min.Z;
            if (ix < 0 || iy < 0 || iz < 0 || ix >= Size.X || iy >= Size.Y || iz >= Size.Z) return -1;
            return (iz * Size.Y + iy) * Size.X + ix;
        }

        public void SetCell(int x, int y, int z, byte kind, bool dynamic)
        {
            int i = Index(x, y, z);
            if (i < 0) return;
            Kind[i] = kind;
            ShapeIndex[i] = dynamic ? DYNAMIC : -1;
        }

        /// <summary>The cells that counted as full for want of a shape are asked again (a new search).</summary>
        public void Forget()
        {
            for (int k = 0; k < UnknownCells.Count; k++) if (Kind[UnknownCells[k]] == UNKNOWN) Kind[UnknownCells[k]] = SHAPED;
            UnknownCells.Clear();
        }

        /// <summary>The box (grid local, metres) clear of the window's blocks, by their shapes?</summary>
        // (the hot path of the lattice: a known shape and its voxels tested here, not in calls - the game's mod profiler
        // wraps every mod method call, and a plan makes 100 000s of these: 2026-11-18, Injected Profiler on the wiki)
        public bool BoxFree(ref BoundingBoxD box)
        {
            const int N = BlockShape.N;
            double gs = GridSize, h = gs * 0.5, v = gs / N;
            int x0 = (int)Math.Floor((box.Min.X + EPS + h) / gs), x1 = (int)Math.Floor((box.Max.X - EPS + h) / gs);
            int y0 = (int)Math.Floor((box.Min.Y + EPS + h) / gs), y1 = (int)Math.Floor((box.Max.Y - EPS + h) / gs);
            int z0 = (int)Math.Floor((box.Min.Z + EPS + h) / gs), z1 = (int)Math.Floor((box.Max.Z - EPS + h) / gs);
            if (x0 < Min.X) x0 = Min.X;
            if (y0 < Min.Y) y0 = Min.Y;
            if (z0 < Min.Z) z0 = Min.Z;
            if (x1 >= Min.X + Size.X) x1 = Min.X + Size.X - 1;
            if (y1 >= Min.Y + Size.Y) y1 = Min.Y + Size.Y - 1;
            if (z1 >= Min.Z + Size.Z) z1 = Min.Z + Size.Z - 1;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    int row = ((z - Min.Z) * Size.Y + (y - Min.Y)) * Size.X - Min.X;
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = row + x;
                        byte kind = Kind[i];
                        if (kind == EMPTY) continue;
                        if (kind != SHAPED) return false;   // full, or not known yet
                        int si = ShapeIndex[i];
                        if (si < 0)
                        {
                            // (not asked yet, or a door's: asked every time)
                            if (!ShapedFree(i, x, y, z, ref box)) return false;
                            continue;
                        }
                        BlockShape s = Shapes[si];
                        if (s.Kind == 0) continue;
                        if (s.Kind == 1) return false;
                        double cx = x * gs - h, cy = y * gs - h, cz = z * gs - h;
                        int i0 = (int)Math.Floor((box.Min.X - cx + EPS) / v), i1 = (int)Math.Floor((box.Max.X - cx - EPS) / v);
                        int j0 = (int)Math.Floor((box.Min.Y - cy + EPS) / v), j1 = (int)Math.Floor((box.Max.Y - cy - EPS) / v);
                        int k0 = (int)Math.Floor((box.Min.Z - cz + EPS) / v), k1 = (int)Math.Floor((box.Max.Z - cz - EPS) / v);
                        if (i0 < 0) i0 = 0;
                        if (j0 < 0) j0 = 0;
                        if (k0 < 0) k0 = 0;
                        if (i1 > N - 1) i1 = N - 1;
                        if (j1 > N - 1) j1 = N - 1;
                        if (k1 > N - 1) k1 = N - 1;
                        for (int k = k0; k <= k1; k++)
                            for (int j = j0; j <= j1; j++)
                                for (int ii = i0; ii <= i1; ii++)
                                {
                                    int bit = ii + N * j + N * N * k;
                                    if (bit < 64 ? (s.Lo & (1UL << bit)) != 0 : (s.Hi & (1UL << (bit - 64))) != 0) return false;
                                }
                    }
                }
            return true;
        }

        private bool ShapedFree(int i, int x, int y, int z, ref BoundingBoxD box)
        {
            BlockShape s;
            int si = ShapeIndex[i];
            if (si >= 0) s = Shapes[si];
            else
            {
                if (Resolve == null || !Resolve(x, y, z, out s))
                {
                    // (a door's cell too: an open door not measured yet isn't a closed one - the lattice holds for it as for
                    // any block; Forget gives the cell back, still asked every time)
                    Kind[i] = UNKNOWN;
                    UnknownCells.Add(i);
                    return false;
                }
                BlockShape o;
                if (Overlay != null && Overlay.TryGetValue(i, out o))
                {
                    if (o.Kind == 1 || s.Kind == 1) s = BlockShape.Full;
                    else if (o.Kind == 2) { s.Kind = 2; s.Lo |= o.Lo; s.Hi |= o.Hi; }
                }
                if (si != DYNAMIC)
                {
                    ShapeIndex[i] = Shapes.Count;
                    Shapes.Add(s);
                }
            }
            if (s.Kind == 0) return true;
            if (s.Kind == 1) return false;
            return !Overlaps(ref s, new Vector3D(x, y, z) * GridSize - GridSize * 0.5, GridSize / BlockShape.N, ref box);
        }

        /// <summary>A voxel shape (its cell's min corner 'c0', voxels 'v' wide) against an axis-aligned box.</summary>
        public static bool Overlaps(ref BlockShape s, Vector3D c0, double v, ref BoundingBoxD box)
        {
            const int N = BlockShape.N;
            int i0 = Math.Max(0, (int)Math.Floor((box.Min.X - c0.X + EPS) / v)), i1 = Math.Min(N - 1, (int)Math.Floor((box.Max.X - c0.X - EPS) / v));
            int j0 = Math.Max(0, (int)Math.Floor((box.Min.Y - c0.Y + EPS) / v)), j1 = Math.Min(N - 1, (int)Math.Floor((box.Max.Y - c0.Y - EPS) / v));
            int k0 = Math.Max(0, (int)Math.Floor((box.Min.Z - c0.Z + EPS) / v)), k1 = Math.Min(N - 1, (int)Math.Floor((box.Max.Z - c0.Z - EPS) / v));
            for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int bit = i + N * j + N * N * k;
                        if (bit < 64 ? (s.Lo & (1UL << bit)) != 0 : (s.Hi & (1UL << (bit - 64))) != 0) return true;
                    }
            return false;
        }

        /// <summary>A voxel shape against an oriented box (grid local; 'reach': the box's bounds): any solid voxel in it?</summary>
        public static bool Overlaps(ref BlockShape s, Vector3D c0, double v, ref MyOrientedBoundingBoxD obb, ref BoundingBoxD reach)
        {
            const int N = BlockShape.N;
            int i0 = Math.Max(0, (int)Math.Floor((reach.Min.X - c0.X) / v)), i1 = Math.Min(N - 1, (int)Math.Floor((reach.Max.X - c0.X) / v));
            int j0 = Math.Max(0, (int)Math.Floor((reach.Min.Y - c0.Y) / v)), j1 = Math.Min(N - 1, (int)Math.Floor((reach.Max.Y - c0.Y) / v));
            int k0 = Math.Max(0, (int)Math.Floor((reach.Min.Z - c0.Z) / v)), k1 = Math.Min(N - 1, (int)Math.Floor((reach.Max.Z - c0.Z) / v));
            for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int bit = i + N * j + N * N * k;
                        if (bit < 64 ? (s.Lo & (1UL << bit)) == 0 : (s.Hi & (1UL << (bit - 64))) == 0) continue;
                        Vector3D lo = c0 + new Vector3D(i, j, k) * v;
                        var vb = new BoundingBoxD(lo, lo + v);
                        if (obb.Intersects(ref vb)) return true;
                    }
            return false;
        }
    }

    /// <summary>
    /// The block lattice search (PathfindingManager.Lattice has the game side): weighted A* over (lattice point, the
    /// hull box's lie along the grid's axes), guided by the corridor field - the distance to the goal for the hull's
    /// smallest cube, flooded from the goal over the lattice (what that cube can't pass, the hull can't: such points
    /// are never opened; a way the cube has and the hull hasn't costs the search a detour, not a flood). A move is one
    /// step along a grid axis, a turn a 90° rotation in place (the box swept round). Tight poses (not free with
    /// ROOM more clearance) cost more: the way keeps to the middle where there is room. Everything is a box query on
    /// the ShapeGrid. No game calls but the callbacks.
    /// </summary>
    public sealed class LatticeSearch
    {
        public const double SLACK = 0.02;          // m more than the berth: the legs' own checks (exact) pass with room to spare
        public const double ROOM = 0.75;           // m more than the berth: less is a tight pose
        public const double TIGHT_COST = 3;        // a step into a tight pose costs this many steps
        public const double TURN_COST = 6;         // a turn costs this many steps
        public const double BEND_COST = 0.5;       // a change of direction costs this much of a step (straighter ways)
        public const double WEIGHT = 3;            // weighted A*: greedy along the field
        private const ushort BLOCKED = 0xFFFF;

        public sealed class Node : FastPriorityQueueNode
        {
            public long Key;
            public int X, Y, Z, Perm;
            public int Move = -1;      // the step that led here (axis * 2 + (positive ? 1 : 0)); -1: none
            public double G;
            public Node Parent;
            public bool Closed, Tight;
        }

        // The frame (set by the caller)
        public ShapeGrid Grid;
        public double Step;                 // m between lattice points
        public Vector3I Min, Max;           // lattice bounds
        public Vector3D Half;               // the hull box's half sizes (right, up, back)
        public double Berth;
        public int Perms;
        public readonly int[] Fa = new int[6], Ua = new int[6], Ra = new int[6];   // per lie: the grid axis of the hull's forward / up / right
        /// <summary>Lattice points a leg of an earlier answer couldn't use (blocked by something else): not opened.</summary>
        public readonly HashSet<long> Banned = new HashSet<long>();

        // The corridor field (dense over the bounds): 0 not reached, BLOCKED: the smallest cube doesn't fit, else steps + 1
        private ushort[] field;
        private int fx, fy, fz;
        private int[] fieldQueue;
        public bool FieldComplete;          // flooded all it reaches: a point not reached can't be reached
        public int FieldReached, FieldProbes;

        private readonly Dictionary<long, Node> nodes = new Dictionary<long, Node>();
        private readonly Dictionary<long, bool> poses = new Dictionary<long, bool>();
        private FastPriorityQueue<Node> open;
        public readonly List<Node> Path = new List<Node>();
        public int Opened, Checks;
        public int MaxStates = 150000;

        public void SetPerms(bool levelOnly, int upAxis)
        {
            Perms = 0;
            for (int fa = 0; fa < 3; fa++)
                for (int ua = 0; ua < 3; ua++)
                {
                    if (fa == ua || (levelOnly && ua != upAxis)) continue;
                    Fa[Perms] = fa;
                    Ua[Perms] = ua;
                    Ra[Perms] = 3 - fa - ua;
                    Perms++;
                }
            for (int i = 0; i < Perms; i++)
                for (int j = 0; j < Perms; j++)
                    turnAxes[i * 6 + j] = i == j ? -1 : TurnAxis(i, j);
            PrepExtents();
        }

        // Per lie: the hull's half size along each grid axis (Extent, as a table: the search's inner loops read it, no calls)
        private readonly double[] ext = new double[18];
        private readonly int[] turnAxes = new int[36];
        private Vector3D extHalf = new Vector3D(-1);

        private void PrepExtents()
        {
            if (extHalf == Half) return;
            extHalf = Half;
            for (int p = 0; p < Perms; p++)
                for (int a = 0; a < 3; a++)
                    ext[p * 3 + a] = Extent(p, a);
        }

        /// <summary>The hull box's half size along grid axis 'a' in lie 'p' (no berth).</summary>
        public double Extent(int p, int a)
        {
            return a == Ra[p] ? Half.X : a == Ua[p] ? Half.Y : Half.Z;
        }

        public Vector3D Local(int x, int y, int z)
        {
            return new Vector3D(x, y, z) * Step;
        }

        public long Key(int x, int y, int z, int p)
        {
            long ix = x - Min.X, iy = y - Min.Y, iz = z - Min.Z;
            return ((ix * 4096 + iy) * 4096 + iz) * 8 + p;
        }

        public bool Inside(int x, int y, int z)
        {
            return x >= Min.X && x <= Max.X && y >= Min.Y && y <= Max.Y && z >= Min.Z && z <= Max.Z;
        }

        #region Field
        private double CubeHalf { get { return Math.Min(Half.X, Math.Min(Half.Y, Half.Z)) + Berth + SLACK; } }

        /// <summary>The hull's smallest cube at a lattice point: free?</summary>
        public bool CubeFree(int x, int y, int z)
        {
            Vector3D c = Local(x, y, z);
            double e = CubeHalf;
            var box = new BoundingBoxD(c - e, c + e);
            return Grid.BoxFree(ref box);
        }

        private int FieldIndex(int x, int y, int z)
        {
            return ((z - Min.Z) * fy + (y - Min.Y)) * fx + (x - Min.X);
        }

        /// <summary>Steps to the goal for the smallest cube from a lattice point; -1: not reached, -2: the cube doesn't fit there.</summary>
        public int FieldAt(int x, int y, int z)
        {
            if (field == null || !Inside(x, y, z)) return -1;
            ushort v = field[FieldIndex(x, y, z)];
            return v == 0 ? -1 : v == BLOCKED ? -2 : v - 1;
        }

        /// <summary>
        /// Floods the field from the seeds (lattice points by the goal where the cube fits) until done, 'maxPoints' are
        /// reached or the time is up ('clock' past 'untilMs'). False: no room for it (the bounds too big).
        /// </summary>
        /// <summary>The caller's key of the field built (0: none, or not to be reused).</summary>
        public long FieldKey;
        /// <summary>The flood was cut short by the clock (not the point cap): ContinueField goes on with it.</summary>
        public bool FieldResumable { get; private set; }
        private int fieldHead, fieldTail;

        public bool BuildField(List<Vector3I> seeds, int maxPoints, System.Diagnostics.Stopwatch clock, double untilMs)
        {
            FieldKey = 0;
            FieldResumable = false;
            fx = Max.X - Min.X + 1;
            fy = Max.Y - Min.Y + 1;
            fz = Max.Z - Min.Z + 1;
            long n = (long)fx * fy * fz;
            FieldComplete = false;
            FieldReached = FieldProbes = 0;
            if (n > 12000000 || fx > 4000 || fy > 4000 || fz > 4000) { field = null; return false; }
            if (field == null || field.Length < n) field = new ushort[n];
            else Array.Clear(field, 0, (int)n);
            if (fieldQueue == null || fieldQueue.Length < maxPoints + 8) fieldQueue = new int[maxPoints + 8];
            int tail = 0;
            for (int i = 0; i < seeds.Count && tail < maxPoints; i++)
            {
                var s = seeds[i];
                if (!Inside(s.X, s.Y, s.Z)) continue;
                int idx = FieldIndex(s.X, s.Y, s.Z);
                if (field[idx] != 0) continue;
                field[idx] = 1;
                fieldQueue[tail++] = idx;
            }
            fieldHead = 0;
            fieldTail = tail;
            return RunField(maxPoints, clock, untilMs);
        }

        /// <summary>A finished field set aside (another goal's: a drone's trip there and back alternates two).</summary>
        public sealed class FieldCopy
        {
            public long Key;
            public ushort[] Field;
            public int Fx, Fy, Fz, Reached, Probes, Used;
            public Vector3I Min, Max;
        }

        /// <summary>The finished field into 'c' (its key: FieldKey).</summary>
        public void SaveField(FieldCopy c)
        {
            int n = fx * fy * fz;
            if (c.Field == null || c.Field.Length < n) c.Field = new ushort[n];
            Array.Copy(field, c.Field, n);
            c.Key = FieldKey;
            c.Fx = fx; c.Fy = fy; c.Fz = fz;
            c.Reached = FieldReached;
            c.Probes = FieldProbes;
            c.Min = Min;
            c.Max = Max;
        }

        /// <summary>A field set aside back as this one (the same bounds). False: not for these bounds.</summary>
        public bool LoadField(FieldCopy c)
        {
            if (c.Min != Min || c.Max != Max || c.Field == null) return false;
            int n = c.Fx * c.Fy * c.Fz;
            if (field == null || field.Length < n) field = new ushort[n];
            Array.Copy(c.Field, field, n);
            fx = c.Fx; fy = c.Fy; fz = c.Fz;
            FieldReached = c.Reached;
            FieldProbes = c.Probes;
            FieldComplete = true;
            FieldResumable = false;
            FieldKey = c.Key;
            return true;
        }

        /// <summary>The flood cut short by the clock before, on from where it stopped (the same field, a later plan).</summary>
        public bool ContinueField(int maxPoints, System.Diagnostics.Stopwatch clock, double untilMs)
        {
            if (field == null || !FieldResumable || fieldQueue == null || fieldQueue.Length < maxPoints + 8) return false;
            return RunField(maxPoints, clock, untilMs);
        }

        private bool RunField(int maxPoints, System.Diagnostics.Stopwatch clock, double untilMs)
        {
            int head = fieldHead, tail = fieldTail;
            int sxy = fx * fy, sinceClock = 0;
            double e = Math.Min(Half.X, Math.Min(Half.Y, Half.Z)) + Berth + SLACK, step = Step;   // (CubeFree, inline)
            var grid = Grid;
            bool cut = false, timeUp = false;
            while (head < tail)
            {
                if (++sinceClock >= 256)
                {
                    sinceClock = 0;
                    if (clock != null && clock.Elapsed.TotalMilliseconds > untilMs) { cut = timeUp = true; break; }
                }
                int idx = fieldQueue[head++];
                ushort d = field[idx];
                if (d >= BLOCKED - 1) continue;
                int iz = idx / sxy, rem = idx - iz * sxy, iy = rem / fx, ix = rem - iy * fx;
                for (int k = 0; k < 6; k++)
                {
                    int nx = ix + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = iy + (k == 2 ? 1 : k == 3 ? -1 : 0), nz = iz + (k == 4 ? 1 : k == 5 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= fx || ny >= fy || nz >= fz) continue;
                    int ni = (nz * fy + ny) * fx + nx;
                    if (field[ni] != 0) continue;
                    FieldProbes++;
                    double px = (nx + Min.X) * step, py = (ny + Min.Y) * step, pz = (nz + Min.Z) * step;
                    var cube = new BoundingBoxD(new Vector3D(px - e, py - e, pz - e), new Vector3D(px + e, py + e, pz + e));
                    if (!grid.BoxFree(ref cube)) { field[ni] = BLOCKED; continue; }
                    if (tail >= maxPoints) { cut = true; continue; }
                    field[ni] = (ushort)(d + 1);
                    fieldQueue[tail++] = ni;
                }
            }
            FieldReached = tail;
            FieldComplete = !cut;
            FieldResumable = timeUp && tail < maxPoints;
            fieldHead = head;
            fieldTail = tail;
            return true;
        }
        #endregion

        #region Poses
        /// <summary>The hull at a lattice point in lie p, with the berth, clear of the grid (kept per search).</summary>
        public bool PoseFree(int x, int y, int z, int p)
        {
            long cell = (((long)(x - Min.X) * 4096 + (y - Min.Y)) * 4096 + (z - Min.Z)) * 8;   // (Key, inline)
            long key = cell + p;
            bool free;
            if (poses.TryGetValue(key, out free)) return free;
            if (Banned.Count > 0 && Banned.Contains(cell + 7)) free = false;
            else
            {
                Checks++;
                if (extHalf != Half) PrepExtents();
                double m = Berth + SLACK, cx = x * Step, cy = y * Step, cz = z * Step;
                double ex = ext[p * 3] + m, ey = ext[p * 3 + 1] + m, ez = ext[p * 3 + 2] + m;
                var box = new BoundingBoxD(new Vector3D(cx - ex, cy - ey, cz - ez), new Vector3D(cx + ex, cy + ey, cz + ez));
                free = Grid.BoxFree(ref box);
            }
            poses[key] = free;
            return free;
        }

        // (Roomy - the pose with ROOM more all round - and MoveFree - one step along an axis - are inline in RunSearch)

        // Which hull axis (0 right, 1 up, 2 back) lies along grid axis a in lie p
        private int HullAxis(int p, int a)
        {
            return a == Ra[p] ? 0 : a == Ua[p] ? 1 : 2;
        }

        /// <summary>The grid axis a 90° turn from lie i to lie j goes about (the one hull axis both keep on the same grid axis); -1: not one turn.</summary>
        public int TurnAxis(int i, int j)
        {
            int same = -1, n = 0;
            for (int a = 0; a < 3; a++)
                if (HullAxis(i, a) == HullAxis(j, a)) { same = a; n++; }
            return n == 1 ? same : -1;
        }

        /// <summary>The box the hull sweeps turning in place between lies i and j (about 'axis'): the half diagonal of the turn's plane on both its axes.</summary>
        public BoundingBoxD TurnBox(int x, int y, int z, int i, int axis)
        {
            int b = (axis + 1) % 3, d = (axis + 2) % 3;
            double diag = Math.Sqrt(Extent(i, b) * Extent(i, b) + Extent(i, d) * Extent(i, d));
            double more = Berth + SLACK;
            var e = Vector3D.Zero;
            if (axis == 0) e.X = Extent(i, 0) + more; else if (axis == 1) e.Y = Extent(i, 1) + more; else e.Z = Extent(i, 2) + more;
            if (b == 0) e.X = diag + more; else if (b == 1) e.Y = diag + more; else e.Z = diag + more;
            if (d == 0) e.X = diag + more; else if (d == 1) e.Y = diag + more; else e.Z = diag + more;
            Vector3D c = Local(x, y, z);
            return new BoundingBoxD(c - e, c + e);
        }

        private bool TurnFree(int x, int y, int z, int i, int j, int axis)
        {
            if (!PoseFree(x, y, z, j)) return false;
            Checks++;
            var box = TurnBox(x, y, z, i, axis);
            return Grid.BoxFree(ref box);
        }
        #endregion

        public struct Start
        {
            public int X, Y, Z, Perm;
            public double G;
        }

        public void Reset()
        {
            SearchResumable = false;
            nodes.Clear();
            poses.Clear();
            Path.Clear();
            Opened = Checks = 0;
            if (open != null) open.Clear();
        }

        // The estimate from a lattice point to the goal (m); negative: never (the cube can't be there / can't get there)
        private double H(int x, int y, int z, Vector3D goalLocal)
        {
            int f = FieldAt(x, y, z);
            if (f >= 0) return f * Step;
            if (f == -2 || FieldComplete) return -1;
            return Vector3D.Distance(Local(x, y, z), goalLocal) * 2;   // (the flood was cut short: beyond it, a guess)
        }

        /// <summary>
        /// From the start states to a state 'goalJoin' accepts (asked at the field's seed points: a straight way from
        /// there to the goal). The answer in Path (start state first). False: none ('why').
        /// </summary>
        /// <summary>The last search stopped on the clock: ContinueSearch goes on with it (nothing else touched in between).</summary>
        public bool SearchResumable { get; private set; }
        private int searchJoins;

        public bool Search(List<Start> starts, Vector3D goalLocal, Func<Node, bool> goalJoin, System.Diagnostics.Stopwatch clock, double untilMs, out string why)
        {
            why = null;
            SearchResumable = false;
            nodes.Clear();
            Path.Clear();
            if (open == null) open = new FastPriorityQueue<Node>(MaxStates + 64);
            else open.Clear();
            Opened = 0;
            PrepExtents();
            for (int i = 0; i < starts.Count; i++)
            {
                var s = starts[i];
                double h = H(s.X, s.Y, s.Z, goalLocal);
                if (h < 0) continue;
                long key = Key(s.X, s.Y, s.Z, s.Perm);
                if (nodes.ContainsKey(key)) continue;
                var n = new Node { X = s.X, Y = s.Y, Z = s.Z, Perm = s.Perm, Key = key, G = s.G };
                nodes[key] = n;
                Opened++;
                open.Enqueue(n, n.G + h * WEIGHT);
            }
            if (open.Count == 0) { why = "no way from the start for the hull's smallest box"; return false; }
            searchJoins = 0;
            return RunSearch(goalLocal, goalJoin, clock, untilMs, out why);
        }

        /// <summary>The search stopped on the clock before, on from where it was (a later tick).</summary>
        public bool ContinueSearch(Vector3D goalLocal, Func<Node, bool> goalJoin, System.Diagnostics.Stopwatch clock, double untilMs, out string why)
        {
            if (!SearchResumable || open == null) { why = "nothing to continue"; return false; }
            return RunSearch(goalLocal, goalJoin, clock, untilMs, out why);
        }

        private bool RunSearch(Vector3D goalLocal, Func<Node, bool> goalJoin, System.Diagnostics.Stopwatch clock, double untilMs, out string why)
        {
            why = null;
            SearchResumable = false;
            int sinceClock = 0;
            Node found = null;
            while (open.Count > 0)
            {
                if (++sinceClock >= 32)
                {
                    sinceClock = 0;
                    if (clock != null && clock.Elapsed.TotalMilliseconds > untilMs) { why = "time budget"; SearchResumable = true; return false; }
                }
                var cur = open.Dequeue();
                if (cur.Closed) continue;
                cur.Closed = true;
                if (FieldAt(cur.X, cur.Y, cur.Z) == 0 && searchJoins < 64)
                {
                    searchJoins++;
                    if (goalJoin(cur)) { found = cur; break; }
                }
                // Moves: a step along each grid axis, the same lie. (Inline: H, Inside, Key, MoveFree, Roomy, Push - the
                // game's mod profiler wraps every call, and this runs 10 000s of times a plan)
                int cp = cur.Perm;
                double m = Berth + SLACK;
                double ex = ext[cp * 3] + m, ey = ext[cp * 3 + 1] + m, ez = ext[cp * 3 + 2] + m;
                for (int a = 0; a < 3; a++)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        int x = cur.X + (a == 0 ? sgn : 0), y = cur.Y + (a == 1 ? sgn : 0), z = cur.Z + (a == 2 ? sgn : 0);
                        if (x < Min.X || x > Max.X || y < Min.Y || y > Max.Y || z < Min.Z || z > Max.Z) continue;
                        double h;
                        {
                            int f = -1;
                            if (field != null)
                            {
                                ushort fv = field[((z - Min.Z) * fy + (y - Min.Y)) * fx + (x - Min.X)];
                                f = fv == 0 ? -1 : fv == BLOCKED ? -2 : fv - 1;
                            }
                            if (f >= 0) h = f * Step;
                            else if (f == -2 || FieldComplete) continue;
                            else h = Vector3D.Distance(new Vector3D(x, y, z) * Step, goalLocal) * 2;
                        }
                        long cell = (((long)(x - Min.X) * 4096 + (y - Min.Y)) * 4096 + (z - Min.Z)) * 8;
                        long key = cell + cp;
                        Node nb;
                        bool known = nodes.TryGetValue(key, out nb);
                        if (known && nb.Closed) continue;
                        int move = a * 2 + (sgn > 0 ? 1 : 0);
                        double bend = cur.Move >= 0 && cur.Move != move ? BEND_COST : 0;
                        // (the least it can cost: not better than what it has - no checks)
                        if (known && cur.G + Step * (1 + bend) >= nb.G) continue;
                        if (!known)
                        {
                            if (Opened >= MaxStates) { why = "state budget"; return false; }
                            // MoveFree: the pose there (a step longer than the box: the stretch between too)
                            bool free;
                            if (!poses.TryGetValue(key, out free))
                            {
                                if (Banned.Count > 0 && Banned.Contains(cell + 7)) free = false;
                                else
                                {
                                    Checks++;
                                    double px = x * Step, py = y * Step, pz = z * Step;
                                    var pb = new BoundingBoxD(new Vector3D(px - ex, py - ey, pz - ez), new Vector3D(px + ex, py + ey, pz + ez));
                                    free = Grid.BoxFree(ref pb);
                                }
                                poses[key] = free;
                            }
                            if (!free) continue;
                            if (2 * ((a == 0 ? ex : a == 1 ? ey : ez)) < Step)
                            {
                                Checks++;
                                double ax = Math.Min(cur.X, x) * Step, ay = Math.Min(cur.Y, y) * Step, az = Math.Min(cur.Z, z) * Step;
                                double bx = Math.Max(cur.X, x) * Step, by = Math.Max(cur.Y, y) * Step, bz = Math.Max(cur.Z, z) * Step;
                                var mb = new BoundingBoxD(new Vector3D(ax - ex, ay - ey, az - ez), new Vector3D(bx + ex, by + ey, bz + ez));
                                if (!Grid.BoxFree(ref mb)) continue;
                            }
                            nb = new Node { X = x, Y = y, Z = z, Perm = cp, Key = key, G = double.MaxValue };
                            // Roomy: ROOM more all round
                            Checks++;
                            {
                                double px = x * Step, py = y * Step, pz = z * Step;
                                var rb = new BoundingBoxD(new Vector3D(px - ex - ROOM, py - ey - ROOM, pz - ez - ROOM), new Vector3D(px + ex + ROOM, py + ey + ROOM, pz + ez + ROOM));
                                nb.Tight = !Grid.BoxFree(ref rb);
                            }
                            nodes[key] = nb;
                            Opened++;
                        }
                        double g = cur.G + Step * ((nb.Tight ? TIGHT_COST : 1) + bend);
                        if (g >= nb.G) continue;
                        nb.G = g;
                        nb.Parent = cur;
                        nb.Move = move;
                        open.EnqueueOrUpdate(nb, g + h * WEIGHT);
                    }
                // Turns in place: a 90° turn to another lie
                double hHere = H(cur.X, cur.Y, cur.Z, goalLocal);
                for (int p = 0; p < Perms; p++)
                {
                    if (p == cur.Perm) continue;
                    int axis = turnAxes[cur.Perm * 6 + p];
                    if (axis < 0) continue;
                    long key = (((long)(cur.X - Min.X) * 4096 + (cur.Y - Min.Y)) * 4096 + (cur.Z - Min.Z)) * 8 + p;
                    Node nb;
                    bool known = nodes.TryGetValue(key, out nb);
                    double g = cur.G + Step * TURN_COST;
                    if (known && (nb.Closed || g >= nb.G)) continue;
                    if (!known)
                    {
                        if (Opened >= MaxStates) { why = "state budget"; return false; }
                        if (!TurnFree(cur.X, cur.Y, cur.Z, cur.Perm, p, axis)) continue;
                        nb = new Node { X = cur.X, Y = cur.Y, Z = cur.Z, Perm = p, Key = key };
                        nb.Tight = cur.Tight;
                        nodes[key] = nb;
                        Opened++;
                    }
                    nb.G = g;
                    nb.Parent = cur;
                    nb.Move = cur.Move;
                    open.EnqueueOrUpdate(nb, g + hHere * WEIGHT);
                }
            }
            if (found == null) { why = "no way through"; return false; }
            for (var n = found; n != null; n = n.Parent) Path.Add(n);
            Path.Reverse();
            return true;
        }

        /// <summary>A straight run of the answer between two lattice points, in one lie.</summary>
        public struct Leg
        {
            public Vector3I A, B;
            public int Perm;
        }

        /// <summary>The answer's moves as legs: straight runs in one lie (a turn in place: the next leg starts there in the new lie).</summary>
        public void Legs(List<Leg> into)
        {
            into.Clear();
            for (int i = 1; i < Path.Count; i++)
            {
                Node m = Path[i - 1], n = Path[i];
                if (n.Perm != m.Perm) continue;
                var a = new Vector3I(m.X, m.Y, m.Z);
                var b = new Vector3I(n.X, n.Y, n.Z);
                int last = into.Count - 1;
                if (last >= 0 && into[last].Perm == n.Perm && into[last].B == a)
                {
                    Vector3I run = into[last].B - into[last].A;
                    run = new Vector3I(Math.Sign(run.X), Math.Sign(run.Y), Math.Sign(run.Z));
                    if (run == b - a)
                    {
                        var l = into[last];   // the run goes on
                        l.B = b;
                        into[last] = l;
                        continue;
                    }
                }
                into.Add(new Leg { A = a, B = b, Perm = n.Perm });
            }
        }

        /// <summary>
        /// The attitude of lie p nearest to (refF, refU): the hull box is the same whichever way its axes point, so the
        /// least turn from the attitude before (a 90° turn where the lie changes). 'right' / 'upAxis' / 'back': the grid's
        /// axes (world). 'gravityUp' (not zero): its up that way. 'travel' (not zero): the nose that way when it makes no
        /// difference.
        /// </summary>
        public void LieAttitude(int p, Vector3D right, Vector3D upAxis, Vector3D back, Vector3D refF, Vector3D refU, Vector3D travel, Vector3D gravityUp,
                                out Vector3D fwd, out Vector3D up)
        {
            Vector3D aF = Fa[p] == 0 ? right : Fa[p] == 1 ? upAxis : back, aU = Ua[p] == 0 ? right : Ua[p] == 1 ? upAxis : back;
            Vector3D refR = Vector3D.Cross(refF, refU);
            double best = double.MinValue;
            fwd = aF;
            up = aU;
            for (int k = 0; k < 4; k++)
            {
                Vector3D f = (k & 1) != 0 ? -aF : aF, u = (k & 2) != 0 ? -aU : aU;
                if (gravityUp.LengthSquared() > 0.5 && Vector3D.Dot(u, gravityUp) < 0) continue;
                double score = Vector3D.Dot(f, refF) + Vector3D.Dot(u, refU) + Vector3D.Dot(Vector3D.Cross(f, u), refR);
                if (travel.LengthSquared() > 1e-6) score += 0.05 * Vector3D.Dot(f, Vector3D.Normalize(travel));
                if (score > best) { best = score; fwd = f; up = u; }
            }
        }
    }
}
