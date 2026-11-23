using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Asteroid rock in the lattice (2026-11-18): caves, tunnels, bores - the block lattice's search over rock too.
    ///  - A static grid's window: the cells an asteroid's box reaches are asked like a block's (ShapeGrid.Resolve, once,
    ///    when the field or the search first meets them): the block's shape there and the rock's, both.
    ///  - No static grid (out in the rock): a window of its own, world-aligned on the asteroid (RockFrame).
    ///  - The rock's shape in a cell: its 0.5 m voxels, each solid when the storage voxel (1 m, LOD 0) under its centre
    ///    is (content over the iso level, as RockIn). Cells all rock / all air: from their storage voxels at once.
    ///  - The storage is read in chunks of ROCK_CHUNK^3 voxels (one ReadRange each), kept ROCK_KEEP_TICKS or until a drone
    ///    mines near them (ForgetRock); windows with rock are built again as often (their key: RockEpoch).
    /// Main thread only.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private const int ROCK_CHUNK = 16;                  // storage voxels per chunk edge (LOD 0: 16 m)
        private const int ROCK_KEEP_TICKS = 30 * 60;        // a chunk read this long ago is read again
        private const int ROCK_CHUNKS_MAX = 2048;           // chunks kept in all (8 MB): all dropped past it
        private const double ROCK_CELL = 2.5;               // m: a rock-only window's cells (0.5 m voxels, as a large grid's)

        private sealed class RockChunk
        {
            public byte[] Solid = new byte[ROCK_CHUNK * ROCK_CHUNK * ROCK_CHUNK];
            public int Frame;
        }

        // An asteroid in a window: where its storage is, its chunks
        private sealed class RockSource
        {
            public MyVoxelBase Voxel;
            public Vector3D Origin;      // world position of storage voxel (0, 0, 0)'s corner
            public Vector3I Size;        // storage size (voxels)
            public Dictionary<int, RockChunk> Chunks;
        }

        private readonly Dictionary<long, Dictionary<int, RockChunk>> rockChunks = new Dictionary<long, Dictionary<int, RockChunk>>();
        private readonly MyStorageData rockChunkData = new MyStorageData(MyStorageDataTypeFlags.Content);
        private int rockChunkCount, rockForgets;
        /// <summary>Rock chunks read (the logs).</summary>
        public int RockChunksRead;

        // Windows with rock: built again when this changes (chunks expire; a drone mined near)
        private int RockEpoch { get { return frame / ROCK_KEEP_TICKS * 1000 + rockForgets; } }

        /// <summary>A drone mined round 'centre': the rock read there is read again, windows with rock built again.</summary>
        public void ForgetRock(Vector3D centre, double radius)
        {
            rockChunks.Clear();
            rockChunkCount = 0;
            rockForgets++;
        }

        private RockSource RockSourceOf(MyVoxelBase v)
        {
            if (v == null || v.Closed || v.Storage == null || !IsAsteroid(v)) return null;
            Dictionary<int, RockChunk> chunks;
            if (!rockChunks.TryGetValue(v.EntityId, out chunks)) rockChunks[v.EntityId] = chunks = new Dictionary<int, RockChunk>();
            return new RockSource { Voxel = v, Origin = v.PositionLeftBottomCorner - (Vector3D)v.StorageMin, Size = v.Storage.Size, Chunks = chunks };
        }

        // The chunk with storage voxel (ix, iy, iz) in it, read now if not (or long ago)
        private RockChunk RockChunkAt(RockSource r, int ix, int iy, int iz)
        {
            int cx = ix / ROCK_CHUNK, cy = iy / ROCK_CHUNK, cz = iz / ROCK_CHUNK;
            int key = cx | (cy << 10) | (cz << 20);
            RockChunk c;
            if (r.Chunks.TryGetValue(key, out c) && frame - c.Frame <= ROCK_KEEP_TICKS) return c;
            if (c == null)
            {
                if (rockChunkCount >= ROCK_CHUNKS_MAX)
                {
                    foreach (var kv in rockChunks) kv.Value.Clear();
                    rockChunkCount = 0;
                }
                c = new RockChunk();
                r.Chunks[key] = c;
                rockChunkCount++;
            }
            else Array.Clear(c.Solid, 0, c.Solid.Length);
            c.Frame = frame;
            var min = new Vector3I(cx * ROCK_CHUNK, cy * ROCK_CHUNK, cz * ROCK_CHUNK);
            var max = Vector3I.Min(min + (ROCK_CHUNK - 1), r.Size - 1);
            Vector3I n = max - min + Vector3I.One;
            VRage.ModAPI.IMyStorage storage = r.Voxel.Storage;   // (the ModAPI interface: the game's own IMyStorage is prohibited)
            rockChunkData.Resize(n);
            storage.ReadRange(rockChunkData, MyStorageDataTypeFlags.Content, 0, min, max);
            RockChunksRead++;
            int i = 0;
            for (int z = 0; z < n.Z; z++)
                for (int y = 0; y < n.Y; y++)
                    for (int x = 0; x < n.X; x++, i++)
                        if (rockChunkData.Content(i) > 127) c.Solid[x + ROCK_CHUNK * (y + ROCK_CHUNK * z)] = 1;
            return c;
        }

        /// <summary>
        /// The rock in cell (x, y, z) of window 'g' (its voxels, grid axes), from every asteroid in it. Cells all rock: Full;
        /// all air: empty. (Inline lookups: the field meets thousands of cells - the game's mod profiler wraps every call.)
        /// </summary>
        private BlockShape RockShape(GridShapes g, int x, int y, int z)
        {
            const int N = BlockShape.N;
            var s = new BlockShape();
            double gs = g.Occ.GridSize, h = gs * 0.5, v = gs / N;
            MatrixD w = g.World;
            Vector3D ax = w.Right, ay = w.Up, az = w.Backward;
            Vector3D corner = Vector3D.Transform(new Vector3D(x * gs - h, y * gs - h, z * gs - h), w);
            for (int ri = 0; ri < g.Rock.Count; ri++)
            {
                RockSource r = g.Rock[ri];
                if (r.Voxel.Closed) continue;
                Vector3D b = corner - r.Origin;   // the cell's corner in storage voxels
                // The cell's box in storage voxels
                Vector3D lo = b, hi = b;
                for (int k = 1; k < 8; k++)
                {
                    Vector3D p = b + ((k & 1) != 0 ? ax * gs : Vector3D.Zero) + ((k & 2) != 0 ? ay * gs : Vector3D.Zero) + ((k & 4) != 0 ? az * gs : Vector3D.Zero);
                    lo = Vector3D.Min(lo, p);
                    hi = Vector3D.Max(hi, p);
                }
                int x0 = Math.Max(0, (int)Math.Floor(lo.X)), y0 = Math.Max(0, (int)Math.Floor(lo.Y)), z0 = Math.Max(0, (int)Math.Floor(lo.Z));
                int x1 = Math.Min(r.Size.X - 1, (int)Math.Floor(hi.X - 1e-6)), y1 = Math.Min(r.Size.Y - 1, (int)Math.Floor(hi.Y - 1e-6)), z1 = Math.Min(r.Size.Z - 1, (int)Math.Floor(hi.Z - 1e-6));
                if (x1 < x0 || y1 < y0 || z1 < z0) continue;
                bool whole = x0 <= lo.X + 1e-6 && y0 <= lo.Y + 1e-6 && z0 <= lo.Z + 1e-6 && x1 >= hi.X - 1 - 1e-6 && y1 >= hi.Y - 1 - 1e-6 && z1 >= hi.Z - 1 - 1e-6;
                // All its storage voxels at once: none solid - nothing here; all - the cell is rock (when they cover it)
                int solid = 0, total = 0, lastKey = -1;
                RockChunk ch = null;
                for (int iz = z0; iz <= z1; iz++)
                    for (int iy = y0; iy <= y1; iy++)
                        for (int ix = x0; ix <= x1; ix++)
                        {
                            int key = (ix / ROCK_CHUNK) | ((iy / ROCK_CHUNK) << 10) | ((iz / ROCK_CHUNK) << 20);
                            if (key != lastKey) { ch = RockChunkAt(r, ix, iy, iz); lastKey = key; }
                            total++;
                            if (ch.Solid[(ix % ROCK_CHUNK) + ROCK_CHUNK * ((iy % ROCK_CHUNK) + ROCK_CHUNK * (iz % ROCK_CHUNK))] != 0) solid++;
                        }
                if (solid == 0) continue;
                if (solid == total && whole) return BlockShape.Full;
                // Each 0.5 m voxel by the storage voxel under its centre
                lastKey = -1;
                for (int k = 0; k < N; k++)
                    for (int j = 0; j < N; j++)
                        for (int i = 0; i < N; i++)
                        {
                            int bit = i + N * j + N * N * k;
                            if (bit < 64 ? (s.Lo & (1UL << bit)) != 0 : (s.Hi & (1UL << (bit - 64))) != 0) continue;
                            Vector3D p = b + ax * ((i + 0.5) * v) + ay * ((j + 0.5) * v) + az * ((k + 0.5) * v);
                            int ix = (int)Math.Floor(p.X), iy = (int)Math.Floor(p.Y), iz = (int)Math.Floor(p.Z);
                            if (ix < 0 || iy < 0 || iz < 0 || ix >= r.Size.X || iy >= r.Size.Y || iz >= r.Size.Z) continue;
                            int key = (ix / ROCK_CHUNK) | ((iy / ROCK_CHUNK) << 10) | ((iz / ROCK_CHUNK) << 20);
                            if (key != lastKey) { ch = RockChunkAt(r, ix, iy, iz); lastKey = key; }
                            if (ch.Solid[(ix % ROCK_CHUNK) + ROCK_CHUNK * ((iy % ROCK_CHUNK) + ROCK_CHUNK * (iz % ROCK_CHUNK))] == 0) continue;
                            s.Kind = 2;
                            if (bit < 64) s.Lo |= 1UL << bit; else s.Hi |= 1UL << (bit - 64);
                        }
            }
            s.Normalise();
            return s;
        }

        // Both shapes in one cell
        private static void MergeShape(ref BlockShape into, BlockShape s)
        {
            if (s.Kind == 0 || into.Kind == 1) return;
            if (s.Kind == 1 || into.Kind == 0) { into = s; return; }
            into.Lo |= s.Lo;
            into.Hi |= s.Hi;
        }

        /// <summary>
        /// The asteroids in a window (world box 'wb'): their cells (those their boxes reach) asked like a block's - the rock
        /// read when the search first meets them. Their ids and the rock's epoch into 'others' (the window's key).
        /// </summary>
        private void AddRock(GridShapes g, List<LocalScene.Entry> entries, ref BoundingBoxD lb, ref MatrixD inv)
        {
            g.Rock.Clear();
            g.RockCell = null;
            var occ = g.Occ;
            double gs = occ.GridSize, h = gs * 0.5;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!e.Voxel) continue;
                var r = RockSourceOf(e.Entity as MyVoxelBase);
                if (r == null) continue;
                // The asteroid's box in the window's coordinates
                BoundingBoxD vb = r.Voxel.PositionComp.WorldAABB;
                BoundingBoxD local = new MyOrientedBoundingBoxD(vb, inv).GetAABB();
                if (!local.Intersects(lb)) continue;
                BoundingBoxD reach = local.Intersect(lb);
                g.Rock.Add(r);
                int x0 = (int)Math.Floor((reach.Min.X + h) / gs), x1 = (int)Math.Floor((reach.Max.X + h) / gs);
                int y0 = (int)Math.Floor((reach.Min.Y + h) / gs), y1 = (int)Math.Floor((reach.Max.Y + h) / gs);
                int z0 = (int)Math.Floor((reach.Min.Z + h) / gs), z1 = (int)Math.Floor((reach.Max.Z + h) / gs);
                if (g.RockCell == null) g.RockCell = new bool[occ.Kind.Length];
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int idx = occ.Index(x, y, z);
                            if (idx < 0) continue;
                            byte kind = occ.Kind[idx];
                            if (kind == ShapeGrid.FULL) continue;
                            g.Touch(idx);
                            g.RockCell[idx] = true;
                            if (kind == ShapeGrid.EMPTY) { occ.Kind[idx] = ShapeGrid.SHAPED; occ.ShapeIndex[idx] = -1; continue; }
                            int si = occ.ShapeIndex[idx];
                            if (si >= 0)
                            {
                                // (another grid's voxels set here already: kept as the overlay, the cell asked again with the rock)
                                if (occ.Overlay == null) occ.Overlay = new Dictionary<int, BlockShape>();
                                BlockShape o;
                                BlockShape s = occ.Shapes[si];
                                if (occ.Overlay.TryGetValue(idx, out o)) MergeShape(ref s, o);
                                occ.Overlay[idx] = s;
                                occ.ShapeIndex[idx] = -1;
                            }
                        }
            }
        }

        /// <summary>
        /// Out in the rock (no static grid there, or start and goal outside it): the lattice on the asteroid start or goal
        /// is in - world axes, ROCK_CELL cells from its storage's corner. Its id, frame and bounds (cells). False: none.
        /// </summary>
        private bool RockFrame(List<LocalScene.Entry> entries, Vector3D start, Vector3D goal, out LocalScene.Entry pick, out MatrixD world, out Vector3I gMin, out Vector3I gMax)
        {
            pick = null;
            world = MatrixD.Identity;
            gMin = gMax = Vector3I.Zero;
            double best = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!e.Voxel) continue;
                var v = e.Entity as MyVoxelBase;
                if (v == null || v.Closed || v.Storage == null || !IsAsteroid(v)) continue;
                BoundingBoxD vb = v.PositionComp.WorldAABB;
                if (vb.Contains(start) == ContainmentType.Disjoint && vb.Contains(goal) == ContainmentType.Disjoint) continue;
                double vol = vb.Volume;
                if (vol > best) { best = vol; pick = e; }
            }
            if (pick == null) return false;
            var pv = (MyVoxelBase)pick.Entity;
            Vector3D origin = pv.PositionLeftBottomCorner - (Vector3D)pv.StorageMin;
            world = MatrixD.CreateTranslation(origin);
            BoundingBoxD box = pv.PositionComp.WorldAABB;
            gMin = new Vector3I((int)Math.Floor((box.Min.X - origin.X) / ROCK_CELL), (int)Math.Floor((box.Min.Y - origin.Y) / ROCK_CELL), (int)Math.Floor((box.Min.Z - origin.Z) / ROCK_CELL));
            gMax = new Vector3I((int)Math.Ceiling((box.Max.X - origin.X) / ROCK_CELL), (int)Math.Ceiling((box.Max.Y - origin.Y) / ROCK_CELL), (int)Math.Ceiling((box.Max.Z - origin.Z) / ROCK_CELL));
            return true;
        }
    }
}
