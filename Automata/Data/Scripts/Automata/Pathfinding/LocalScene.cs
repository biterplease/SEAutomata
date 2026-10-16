using System;
using System.Collections.Generic;

using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// What is around the drones, shared by all of them (server, one per session, owned by the PathfindingManager).
    ///
    /// Broad phase: a MyDynamicAABBTreeD of the obstacles' world boxes (grids with physics - projections have
    /// none - and asteroids; planets are terrain). Filled lazily: a query first makes sure its area was looked up
    /// recently (<see cref="Ensure"/>: one MyGamePruningStructure top-most query per area, reused by every drone
    /// working there within REFRESH_TICKS). Entries nobody asked about for STALE_TICKS are dropped.
    ///
    /// Kept for the session, not saved: a block tree costs ~1 us per block to build (logs: 85 blocks 0.09 ms, so a
    /// 10k-block station ~11 ms once) and nothing to keep up to date (block events, O(log n) per change); reading it
    /// back from a save would insert the same proxies plus deserialize them. So entries live long (STALE_TICKS 30 min:
    /// memory over CPU, ~150 B per block) and the first query after a load rebuilds.
    ///
    /// Narrow phase: per grid, a tree of its blocks' boxes in grid coordinates (built the first time a query reaches
    /// the grid, kept up to date by the grid's block added / removed events). Grid coordinates don't change when the
    /// grid moves: queries are transformed into the grid's frame with its current matrix.
    /// </summary>
    public sealed class LocalScene : ISceneView
    {
        #region Timing
        private const int REFRESH_TICKS = 30;        // an area looked up this recently covers queries inside it
        private const int STALE_TICKS = 30 * 60 * 60;   // entries no query touched for this long are dropped (a rebuild costs ~1 us / block)
        private const int PRUNE_EVERY = 300;
        #endregion
        private const double MIN_AREA_RADIUS = 60;   // m: areas looked up at least this big, so they get reused

        public sealed class Entry
        {
            public MyEntity Entity;
            public IMyCubeGrid Grid;                 // null: voxel map (asteroid)
            public int Proxy = -1;
            public int UsedFrame;
            // Grid only: block boxes, grid coordinates (cells * grid size), user data = the slim block
            public MyDynamicAABBTreeD Blocks;
            public Dictionary<IMySlimBlock, int> BlockProxies;
            public int IndexedCount = -1;            // MyCubeGrid.BlocksCount when the tree was last right
            public bool Dirty = true;
            public int MovedFrame = -1;              // moving grids: proxy last moved to the grid's current box
            public Action<IMySlimBlock> OnAdded, OnRemoved;
            public Action<IMyCubeGrid, IMyCubeGrid> OnSplit;
            public bool Voxel { get { return Grid == null; } }
            /// <summary>
            /// The grid's matrices as of the last query that returned the entry (<see cref="Overlapping"/>) and its cell
            /// size: the checks read these, never the entity (a search on another thread has a snapshot of them).
            /// </summary>
            public MatrixD Inv, World;
            public double GridSize = 2.5;
            public bool Static;                      // the grid is static (a block of it in the way stays there)
            public int MatrixFrame = int.MinValue;   // frame the matrices were read (once per frame)
            /// <summary>
            /// Changes of the block tree (blocks added / removed, rebuilt): a check on another thread made against one
            /// version holds while it is the same (PathfindingManager.AcceptSearch).
            /// </summary>
            public int Version;
            /// <summary>Snapshot entries: the world box the entry had when it was captured.</summary>
            public BoundingBoxD Box;
            /// <summary>Snapshot entries: the live entry it was copied from.</summary>
            public Entry Source;
            // BuildStep: the blocks still to insert (the tree is built over several calls; block events meanwhile go
            // to the new tree, removed ones are skipped)
            public List<IMySlimBlock> Pending;
            public int PendingIndex;
            public HashSet<IMySlimBlock> PendingRemoved;
        }

        private struct Area
        {
            public BoundingSphereD Sphere;
            public int Frame;
        }

        private readonly MyDynamicAABBTreeD tree = new MyDynamicAABBTreeD(new Vector3D(2.0), 1.0);
        private readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        private readonly List<Area> areas = new List<Area>();
        private readonly List<MyEntity> found = new List<MyEntity>();
        private readonly List<long> removeBuffer = new List<long>();
        private int pruneCounter;

        /// <summary>For the logs: pruning structure lookups, block trees (re)built, blocks indexed by them, ms spent building.</summary>
        public int Lookups, TreesBuilt, BlocksIndexed;
        public double BuildMs;
        /// <summary>
        /// Counts what can open a way: blocks removed, grids split, obstacles closed. A "no route" remembered against an
        /// older count is stale (PathfindingManager's failed-route cache); blocks added only close ways.
        /// </summary>
        public int RemovalVersion;
        private readonly System.Diagnostics.Stopwatch buildWatch = new System.Diagnostics.Stopwatch();
        public int EntryCount { get { return entries.Count; } }

        /// <summary>
        /// Makes sure the obstacles in 'sphere' are in the tree (looked up within REFRESH_TICKS, by any drone).
        /// </summary>
        public void Ensure(BoundingSphereD sphere, int now)
        {
            // (main thread)
            for (int i = areas.Count - 1; i >= 0; i--)
            {
                var a = areas[i];
                if (now - a.Frame > REFRESH_TICKS) { areas.RemoveAt(i); continue; }
                if (Vector3D.Distance(a.Sphere.Center, sphere.Center) + sphere.Radius <= a.Sphere.Radius) return;
            }
            if (sphere.Radius < MIN_AREA_RADIUS) sphere.Radius = MIN_AREA_RADIUS;
            found.Clear();
            MyGamePruningStructure.Instance.GetTopMostEntities(ref sphere, found, MyEntityQueryType.Both);
            Lookups++;
            for (int i = 0; i < found.Count; i++)
            {
                var e = found[i];
                if (e == null || e.MarkedForClose || e.Closed) continue;
                var grid = e as IMyCubeGrid;
                if (grid != null) { if (grid.Physics == null) continue; }        // projections, ghosts
                else if (!PathfindingManager.IsAsteroid(e)) continue;             // asteroids only; planets (and their physics pieces): terrain checks
                BoundingBoxD box = BroadBox(e);
                Entry entry;
                if (!entries.TryGetValue(e.EntityId, out entry))
                {
                    entry = new Entry { Entity = e, Grid = grid };
                    entry.Proxy = tree.AddProxy(ref box, entry, 0u, true);
                    entries[e.EntityId] = entry;
                }
                else tree.MoveProxy(entry.Proxy, ref box, Vector3D.Zero);
                entry.UsedFrame = now;
                entry.MovedFrame = now;
                if (IsMoving(e) && !moving.Contains(entry)) moving.Add(entry);
            }
            found.Clear();
            areas.Add(new Area { Sphere = sphere, Frame = now });
        }

        // Grids that move (ships, a moving base): their broad-phase box follows them at every query frame, not only when
        // their area is looked up again (REFRESH_TICKS: a ship at 20 m/s was 10 m from its box and checks missed it).
        // The block trees are in grid coordinates and need nothing.
        private readonly List<Entry> moving = new List<Entry>();
        private const double MOVING_SPEED = 0.05;   // m/s (or rad/s): counts as moving

        private static bool IsMoving(MyEntity e)
        {
            var p = e.Physics;
            return p != null && (p.LinearVelocity.LengthSquared() > MOVING_SPEED * MOVING_SPEED
                                 || p.AngularVelocity.LengthSquared() > MOVING_SPEED * MOVING_SPEED);
        }

        // The world box, grown by the distance the entity covers until its area is looked up again
        internal static BoundingBoxD BroadBox(MyEntity e)
        {
            BoundingBoxD box = e.PositionComp.WorldAABB;
            var p = e.Physics;
            if (p != null)
            {
                double speed = p.LinearVelocity.Length() + p.AngularVelocity.Length() * box.HalfExtents.Length();
                if (speed > MOVING_SPEED) box.Inflate(speed * REFRESH_TICKS / 60.0);
            }
            return box;
        }

        private void UpdateMoving(int now)
        {
            for (int i = moving.Count - 1; i >= 0; i--)
            {
                var e = moving[i];
                Entry cur;
                if (e.Proxy < 0 || e.Entity.MarkedForClose || e.Entity.Closed || !entries.TryGetValue(e.Entity.EntityId, out cur) || cur != e)
                {
                    moving.RemoveAt(i);   // pruned / closed (its proxy is gone)
                    continue;
                }
                if (e.MovedFrame == now) continue;
                e.MovedFrame = now;
                BoundingBoxD box = BroadBox(e.Entity);
                tree.MoveProxy(e.Proxy, ref box, Vector3D.Zero);
                if (!IsMoving(e.Entity)) moving.RemoveAt(i);   // stopped: this box is right until it moves again (next lookup)
            }
        }

        /// <summary>Entries whose world box overlaps 'box' ('result' is cleared first).</summary>
        public void Overlapping(ref BoundingBoxD box, List<Entry> result, int now)
        {
            if (moving.Count > 0) UpdateMoving(now);
            tree.OverlapAllBoundingBox(ref box, result, 0u, true);
            for (int i = result.Count - 1; i >= 0; i--)
            {
                var e = result[i];
                if (e.Entity.MarkedForClose || e.Entity.Closed || (e.Grid != null && e.Grid.Physics == null)) { result.RemoveAt(i); continue; }
                e.UsedFrame = now;
                if (e.Grid != null && (e.MatrixFrame != now || !e.Static))
                {
                    // Static grids once per frame (the checks call this thousands of times per plan); moving ones every time
                    // (a plan in the after-simulation part of a tick sees where physics moved them)
                    e.MatrixFrame = now;
                    e.Inv = e.Grid.WorldMatrixNormalizedInv;
                    e.World = e.Grid.WorldMatrix;
                    e.GridSize = e.Grid.GridSize;
                    e.Static = e.Grid.IsStatic;
                }
            }
        }

        /// <summary>The grid's block tree, built / repaired if needed (a build in progress is finished). Null for voxels.</summary>
        public MyDynamicAABBTreeD BlocksOf(Entry e)
        {
            if (e.Grid == null) return null;
            Hook(e);
            if (e.Pending == null)
            {
                var mg = e.Grid as MyCubeGrid;
                int count = mg != null ? mg.BlocksCount : -1;
                if (!e.Dirty && (count < 0 || count == e.IndexedCount)) return e.Blocks;
                StartBuild(e);
            }
            buildWatch.Restart();
            var list = e.Pending;
            while (e.PendingIndex < list.Count) AddPending(e, list[e.PendingIndex++]);
            FinishBuild(e);
            BuildMs += buildWatch.Elapsed.TotalMilliseconds;
            return e.Blocks;
        }

        // The tree and the grid's events that keep it up to date. While a tree is built over several calls (Pending) the
        // events go to it too; a full rebuild ahead (Dirty, nothing pending) ignores them.
        private void Hook(Entry e)
        {
            if (e.Blocks != null) return;
            e.Blocks = new MyDynamicAABBTreeD(new Vector3D(0.1), 1.0);
            e.BlockProxies = new Dictionary<IMySlimBlock, int>();
            var entry = e;
            e.OnAdded = b =>
            {
                if (entry.Pending != null) { AddBlock(entry, b); entry.Version++; }
                else if (!entry.Dirty) { AddBlock(entry, b); SyncCount(entry); entry.Version++; }
            };
            e.OnRemoved = b =>
            {
                RemovalVersion++;
                if (entry.Pending != null)
                {
                    RemoveBlock(entry, b);
                    if (entry.PendingRemoved == null) entry.PendingRemoved = new HashSet<IMySlimBlock>();
                    entry.PendingRemoved.Add(b);
                    entry.Version++;
                }
                else if (!entry.Dirty) { RemoveBlock(entry, b); SyncCount(entry); entry.Version++; }
            };
            e.OnSplit = (g1, g2) =>
            {
                RemovalVersion++;
                entry.Dirty = true;
                entry.Pending = null;   // started again from the grid's blocks as they are now
                entry.PendingRemoved = null;
                entry.Version++;
            };
            e.Grid.OnBlockAdded += e.OnAdded;
            e.Grid.OnBlockRemoved += e.OnRemoved;
            e.Grid.OnGridSplit += e.OnSplit;
        }

        // A rebuild: a new tree (a search on another thread may still read the old one: never cleared under it)
        private static void StartBuild(Entry e)
        {
            e.Dirty = true;
            e.Blocks = new MyDynamicAABBTreeD(new Vector3D(0.1), 1.0);
            e.BlockProxies = new Dictionary<IMySlimBlock, int>();
            e.Pending = new List<IMySlimBlock>();
            e.Grid.GetBlocks(e.Pending);
            e.PendingIndex = 0;
            e.PendingRemoved = null;
            e.Version++;
        }

        private static void AddPending(Entry e, IMySlimBlock b)
        {
            if (e.PendingRemoved == null || !e.PendingRemoved.Contains(b)) AddBlock(e, b);
        }

        private void FinishBuild(Entry e)
        {
            BlocksIndexed += e.Pending.Count;
            TreesBuilt++;
            e.Pending = null;
            e.PendingRemoved = null;
            SyncCount(e);
            e.Dirty = false;
            e.Version++;
        }

        /// <summary>
        /// The grid's block tree built (or repaired) over several calls, 'budgetMs' each: a big station costs ~10 ms in one go
        /// (~1 us per block). True when the tree is right. Blocks added / removed meanwhile go to the tree as it is built.
        /// </summary>
        public bool BuildStep(Entry e, double budgetMs, System.Diagnostics.Stopwatch clock)
        {
            if (e.Grid == null) return true;
            Hook(e);
            if (e.Pending == null)
            {
                var mg = e.Grid as MyCubeGrid;
                int count = mg != null ? mg.BlocksCount : -1;
                if (!e.Dirty && (count < 0 || count == e.IndexedCount)) return true;
                StartBuild(e);
            }
            double t0 = clock.Elapsed.TotalMilliseconds;
            var list = e.Pending;
            while (e.PendingIndex < list.Count)
            {
                AddPending(e, list[e.PendingIndex++]);
                if ((e.PendingIndex & 63) == 0 && clock.Elapsed.TotalMilliseconds - t0 > budgetMs) { BuildMs += clock.Elapsed.TotalMilliseconds - t0; return false; }
            }
            FinishBuild(e);
            BuildMs += clock.Elapsed.TotalMilliseconds - t0;
            return true;
        }

        private static void SyncCount(Entry e)
        {
            var mg = e.Grid as MyCubeGrid;
            if (mg != null) e.IndexedCount = mg.BlocksCount;
        }

        private static void AddBlock(Entry e, IMySlimBlock b)
        {
            if (b == null || e.BlockProxies.ContainsKey(b)) return;
            BoundingBoxD box = BlockBox(b, e.Grid.GridSize);
            e.BlockProxies[b] = e.Blocks.AddProxy(ref box, b, 0u, true);
        }

        private static void RemoveBlock(Entry e, IMySlimBlock b)
        {
            int id;
            if (b == null || !e.BlockProxies.TryGetValue(b, out id)) return;
            e.Blocks.RemoveProxy(id);
            e.BlockProxies.Remove(b);
        }

        /// <summary>A block's box in grid coordinates (all its cells).</summary>
        public static BoundingBoxD BlockBox(IMySlimBlock b, double gridSize)
        {
            double h = gridSize * 0.5;
            return new BoundingBoxD((Vector3D)b.Min * gridSize - h, (Vector3D)b.Max * gridSize + h);
        }

        /// <summary>Session update: drops entries nobody used for a while and closed entities.</summary>
        public void Update(int now)
        {
            if (++pruneCounter < PRUNE_EVERY) return;
            pruneCounter = 0;
            removeBuffer.Clear();
            foreach (var kv in entries)
            {
                var e = kv.Value;
                if (e.Entity.MarkedForClose || e.Entity.Closed) { removeBuffer.Add(kv.Key); RemovalVersion++; }
                else if (now - e.UsedFrame > STALE_TICKS) removeBuffer.Add(kv.Key);
            }
            for (int i = 0; i < removeBuffer.Count; i++) Remove(removeBuffer[i]);
            removeBuffer.Clear();
        }

        private void Remove(long id)
        {
            Entry e;
            if (!entries.TryGetValue(id, out e)) return;
            if (e.Proxy >= 0) tree.RemoveProxy(e.Proxy);
            Unhook(e);
            entries.Remove(id);
        }

        private static void Unhook(Entry e)
        {
            if (e.Grid != null && e.OnAdded != null)
            {
                e.Grid.OnBlockAdded -= e.OnAdded;
                e.Grid.OnBlockRemoved -= e.OnRemoved;
                e.Grid.OnGridSplit -= e.OnSplit;
            }
            e.OnAdded = e.OnRemoved = null;
            e.OnSplit = null;
            e.Blocks = null;   // (not cleared: a search on another thread may still read it; the GC takes it)
            e.BlockProxies = null;
        }

        public void Clear()
        {
            foreach (var kv in entries) Unhook(kv.Value);
            entries.Clear();
            tree.Clear();
            areas.Clear();
        }
    }

    /// <summary>
    /// What the scene checks read: the live <see cref="LocalScene"/> (main thread) or a <see cref="SnapshotScene"/> (a
    /// search on another thread: entries, matrices and block trees captured when it was dispatched).
    /// </summary>
    public interface ISceneView
    {
        void Ensure(BoundingSphereD sphere, int now);
        void Overlapping(ref BoundingBoxD box, List<LocalScene.Entry> result, int now);
        /// <summary>The grid's block tree (grid coordinates); null for voxels.</summary>
        MyDynamicAABBTreeD BlocksOf(LocalScene.Entry e);
    }
}
