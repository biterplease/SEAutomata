using System.Collections.Generic;

using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// A grid as a search on another thread saw it: its live entry, the block tree object and version it read, and its
    /// matrix. Unchanged on the main thread later, the search's box checks against it still hold (AcceptSearch).
    /// </summary>
    public struct SceneTrust
    {
        public LocalScene.Entry Live;
        public MyDynamicAABBTreeD Blocks;
        public int Version;
        public MatrixD World;
    }

    /// <summary>
    /// The part of the <see cref="LocalScene"/> a search on another thread reads, captured on the main thread when the
    /// search is dispatched: the entries in its region with their matrices and (built) block trees. The trees are the
    /// live ones (queries take their reader lock; block events on the main thread take the writer lock; a rebuild makes
    /// a new tree, so the one captured is never cleared under the search); everything else is a copy, so nothing here
    /// reads an entity from the worker.
    ///
    /// Capture is in steps (<see cref="Begin"/>, <see cref="Step"/>): building a big grid's block tree costs up to
    /// ~10 ms once (~1 us per block), so the trees of a request's grids are built within a per-tick budget.
    /// </summary>
    public sealed class SnapshotScene : ISceneView
    {
        private const int TREE_FROM = 48;   // entries: from this many, Overlapping goes through a tree (fewer: a scan is faster)

        private readonly List<LocalScene.Entry> live = new List<LocalScene.Entry>();
        private readonly List<LocalScene.Entry> entries = new List<LocalScene.Entry>();
        private readonly MyDynamicAABBTreeD tree = new MyDynamicAABBTreeD(new Vector3D(1.0), 1.0);
        private bool useTree;
        private int next;   // live entries whose tree is built so far

        /// <summary>Entries captured (after <see cref="Step"/> returned true).</summary>
        public int Count { get { return entries.Count; } }

        public void Clear()
        {
            live.Clear();
            entries.Clear();
            if (useTree) tree.Clear();
            useTree = false;
            next = 0;
        }

        /// <summary>Main thread: the entries overlapping 'region' (looked up if not yet); trees built by <see cref="Step"/>.</summary>
        public void Begin(LocalScene scene, BoundingBoxD region, int now)
        {
            Clear();
            scene.Ensure(BoundingSphereD.CreateFromBoundingBox(region), now);
            scene.Overlapping(ref region, live, now);
        }

        /// <summary>
        /// Main thread: builds the block trees of the entries (the drone's own grids are not obstacles: left out) for up
        /// to 'budgetMs'; true when all are built. 'capture': then copies them for the worker (a warm-up only builds).
        /// </summary>
        public bool Step(LocalScene scene, List<IMyCubeGrid> own, double budgetMs, System.Diagnostics.Stopwatch clock, bool capture = true)
        {
            double t0 = clock.Elapsed.TotalMilliseconds;
            while (next < live.Count)
            {
                var e = live[next];
                if (!e.Voxel && !(own != null && own.Contains(e.Grid)))
                {
                    double left = budgetMs - (clock.Elapsed.TotalMilliseconds - t0);
                    if (!scene.BuildStep(e, left > 0.05 ? left : 0.05, clock)) return false;   // (budget spent: on next tick)
                }
                next++;
            }
            if (capture)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    var e = live[i];
                    if (e.Entity.MarkedForClose || e.Entity.Closed) continue;
                    if (e.Voxel ? !PathfindingManager.IsAsteroid(e.Entity) : own != null && own.Contains(e.Grid)) continue;   // not obstacles
                    var c = new LocalScene.Entry { Entity = e.Entity, Grid = e.Grid, Blocks = e.Blocks, Box = LocalScene.BroadBox(e.Entity),
                                                   GridSize = e.GridSize, Source = e, Version = e.Version };
                    if (e.Grid != null)
                    {
                        c.Inv = e.Grid.WorldMatrixNormalizedInv;
                        c.World = e.Grid.WorldMatrix;
                        c.Static = e.Grid.IsStatic;
                    }
                    entries.Add(c);
                }
                useTree = entries.Count >= TREE_FROM;
                if (useTree)
                    for (int i = 0; i < entries.Count; i++)
                    {
                        BoundingBoxD b = entries[i].Box;
                        tree.AddProxy(ref b, entries[i], 0u);
                    }
            }
            live.Clear();
            return true;
        }

        /// <summary>The grids captured, as the search reads them (for the main thread's check of its answer).</summary>
        public SceneTrust[] Trust()
        {
            int n = 0;
            for (int i = 0; i < entries.Count; i++) if (entries[i].Grid != null && entries[i].Blocks != null) n++;
            var t = new SceneTrust[n];
            n = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var c = entries[i];
                if (c.Grid == null || c.Blocks == null) continue;
                t[n++] = new SceneTrust { Live = c.Source, Blocks = c.Blocks, Version = c.Version, World = c.World };
            }
            return t;
        }

        // ISceneView (the worker's side)
        public void Ensure(BoundingSphereD sphere, int now) { }

        public void Overlapping(ref BoundingBoxD box, List<LocalScene.Entry> result, int now)
        {
            result.Clear();
            if (useTree) { tree.OverlapAllBoundingBox(ref box, result, 0u, false); return; }
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Box.Intersects(box)) result.Add(entries[i]);
        }

        public MyDynamicAABBTreeD BlocksOf(LocalScene.Entry e)
        {
            return e.Blocks;
        }
    }
}
