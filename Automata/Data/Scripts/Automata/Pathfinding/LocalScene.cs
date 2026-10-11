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
    public sealed class LocalScene
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
        private readonly List<IMySlimBlock> slimBuffer = new List<IMySlimBlock>();
        private readonly List<long> removeBuffer = new List<long>();
        private int pruneCounter;

        /// <summary>For the logs: pruning structure lookups, block trees (re)built, blocks indexed by them, ms spent building.</summary>
        public int Lookups, TreesBuilt, BlocksIndexed;
        public double BuildMs;
        private readonly System.Diagnostics.Stopwatch buildWatch = new System.Diagnostics.Stopwatch();
        public int EntryCount { get { return entries.Count; } }

        /// <summary>
        /// Makes sure the obstacles in 'sphere' are in the tree (looked up within REFRESH_TICKS, by any drone).
        /// </summary>
        public void Ensure(BoundingSphereD sphere, int now)
        {
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
        private static BoundingBoxD BroadBox(MyEntity e)
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
            }
        }

        /// <summary>The grid's block tree, built / repaired if needed. Null for voxels.</summary>
        public MyDynamicAABBTreeD BlocksOf(Entry e)
        {
            if (e.Grid == null) return null;
            var mg = e.Grid as MyCubeGrid;
            int count = mg != null ? mg.BlocksCount : -1;
            if (e.Blocks == null)
            {
                e.Blocks = new MyDynamicAABBTreeD(new Vector3D(0.1), 1.0);
                e.BlockProxies = new Dictionary<IMySlimBlock, int>();
                var entry = e;
                e.OnAdded = b => { if (!entry.Dirty) { AddBlock(entry, b); SyncCount(entry); } };
                e.OnRemoved = b => { if (!entry.Dirty) { RemoveBlock(entry, b); SyncCount(entry); } };
                e.OnSplit = (g1, g2) => entry.Dirty = true;
                e.Grid.OnBlockAdded += e.OnAdded;
                e.Grid.OnBlockRemoved += e.OnRemoved;
                e.Grid.OnGridSplit += e.OnSplit;
            }
            if (e.Dirty || (count >= 0 && count != e.IndexedCount))
            {
                buildWatch.Restart();
                e.Blocks.Clear();
                e.BlockProxies.Clear();
                slimBuffer.Clear();
                e.Grid.GetBlocks(slimBuffer);
                for (int i = 0; i < slimBuffer.Count; i++) AddBlock(e, slimBuffer[i]);
                BlocksIndexed += slimBuffer.Count;
                slimBuffer.Clear();
                e.IndexedCount = count;
                e.Dirty = false;
                TreesBuilt++;
                BuildMs += buildWatch.Elapsed.TotalMilliseconds;
            }
            return e.Blocks;
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
                if (e.Entity.MarkedForClose || e.Entity.Closed || now - e.UsedFrame > STALE_TICKS) removeBuffer.Add(kv.Key);
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
            if (e.Blocks != null) e.Blocks.Clear();
            e.Blocks = null;
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
}
