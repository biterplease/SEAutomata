using System;
using System.Collections.Generic;

using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Real shapes (2026-11-13, TODO/27 option 3; config.RealShapeConfirm): what a block really takes of its cells, as
    /// N x N x N voxels per cell (BlockShape), measured with rays once per block definition, orientation and cell and
    /// kept for the session - every block of that kind and orientation, on any grid, reads the same mask. Used by the
    /// box checks (SegmentBlocked, BoxFree: a static grid's block in the way by its cell box counts only if its voxels
    /// are) and by the block lattice (ShapeGrid). No rays per check: a check is a few box tests.
    ///  - Measuring a cell: along each grid axis, N x N lines through the voxel centres, each voxel's stretch cast from
    ///    both sides (rays see a surface only from outside it): where a line enters and leaves solid. A voxel is solid
    ///    where a line of any axis runs through solid. 6 N^3 rays a cell (750), MEASURE_RAYS_PER_TICK at most: a shape
    ///    not measured yet counts as its whole cell, and is measured in the background (ShapesUpdate).
    ///  - Full armor cubes are their cell (their collision is merged with their neighbours': no rays). Doors: closed -
    ///    their cells; open - measured once per kind of door (definition, orientation, the cell's place in it: every open
    ///    door of a kind is the same shape), queued like any block while out of rays. Blocks not
    ///    built up yet: their cells.
    ///  - Thinner than a voxel and between two lines (a railing's bar): can be missed; the leg checks' own rays (long
    ///    legs, the look-ahead) and the push watch are what is left for those.
    /// Main thread only.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private const int MEASURE_RAYS = ShapeMeasurer.RAYS;         // rays to measure one cell
        private const int MEASURE_RAYS_PER_TICK = 6 * MEASURE_RAYS;  // every check of a tick together
        private const int LATTICE_MEASURE_RAYS_MAX = 20000;          // rays a tick beyond the budget for a lattice (~25 ms)
        private const int MEASURE_FINE_BELOW = BlockShape.COUNT / 10;  // fewer solid voxels: measured again for thin parts
        private const double MEASURE_FINE_STEP = 0.04;               // m between those lines (bars about this thick)
        private const int MEASURE_BACKGROUND_CELLS = 2;              // per tick, queued cells
        private const int SHAPE_QUEUE_MAX = 4096;

        private struct ShapeDefKey
        {
            public MyDefinitionId Id;
            public int Code;   // orientation and the cell's offset in the block
        }

        private sealed class ShapeDefKeyComparer : IEqualityComparer<ShapeDefKey>
        {
            public bool Equals(ShapeDefKey a, ShapeDefKey b) { return a.Code == b.Code && a.Id == b.Id; }
            public int GetHashCode(ShapeDefKey k) { return k.Id.GetHashCode() * 397 ^ k.Code; }
        }

        private struct QueuedShape
        {
            public IMyCubeGrid Grid;
            public IMySlimBlock Slim;
            public Vector3I Cell;
        }

        private readonly Dictionary<ShapeDefKey, BlockShape> defShapes = new Dictionary<ShapeDefKey, BlockShape>(new ShapeDefKeyComparer());
        private readonly HashSet<ShapeDefKey> shapeQueued = new HashSet<ShapeDefKey>(new ShapeDefKeyComparer());
        private readonly List<QueuedShape> shapeQueue = new List<QueuedShape>();
        private readonly Dictionary<MyDefinitionId, bool> fullCubeDefs = new Dictionary<MyDefinitionId, bool>(MyDefinitionId.Comparer);
        // Open doors' cells, per kind of door (DefKey): the frame, the folded leaves
        private readonly Dictionary<ShapeDefKey, BlockShape> openDoorShapes = new Dictionary<ShapeDefKey, BlockShape>(new ShapeDefKeyComparer());
        private readonly List<IHitInfo> measureHits = new List<IHitInfo>();
        private int measureFrame = -1, measureRaysLeft, measureExtra;
        /// <summary>Shapes measured / met before they were measured (their cells counted), for the logs.</summary>
        public int ShapesMeasured, ShapesUnknown;

        private bool ShapesOn { get { return config.RealShapeConfirm && !offThread && !coarseOnly; } }

        private static ShapeDefKey DefKey(IMySlimBlock slim, Vector3I cell)
        {
            Vector3I off = cell - slim.Min;
            var o = slim.Orientation;
            return new ShapeDefKey { Id = slim.BlockDefinition.Id, Code = ((off.X & 63) | ((off.Y & 63) << 6) | ((off.Z & 63) << 12)) * 36 + (int)o.Forward * 6 + (int)o.Up };
        }

        // A full armor cube (its collision merges with its neighbours': rays can't see its faces inside a wall)
        private bool FullCube(IMySlimBlock slim)
        {
            if (slim.FatBlock != null) return false;
            bool full;
            MyDefinitionId id = slim.BlockDefinition.Id;
            if (fullCubeDefs.TryGetValue(id, out full)) return full;
            var def = slim.BlockDefinition as MyCubeBlockDefinition;
            full = def != null && def.CubeDefinition != null && def.CubeDefinition.CubeTopology == MyCubeTopology.Box;
            fullCubeDefs[id] = full;
            return full;
        }

        private bool TakeMeasureRays()
        {
            if (frame != measureFrame) { measureFrame = frame; measureRaysLeft = MEASURE_RAYS_PER_TICK; }
            if (measureRaysLeft >= MEASURE_RAYS) { measureRaysLeft -= MEASURE_RAYS; return true; }
            // (a lattice search's own allowance - within LATTICE_MEASURE_RAYS_MAX a tick: the thin parts' fine pass is ~6x a
            // measure, and 48 of them took 388 ms in one tick, 2026-11-16; the rest is measured as the lattice goes on)
            if (measureExtra > 0 && measureRaysLeft > -LATTICE_MEASURE_RAYS_MAX) { measureExtra--; measureRaysLeft -= MEASURE_RAYS; return true; }
            return false;
        }

        /// <summary>
        /// The shape of 'cell' of block 'slim' of 'grid' ('world' / 'inv': the grid's matrices, 'gs' its cell size). False:
        /// not measured yet and out of rays for now - 'shape' is the whole cell meanwhile, the cell is queued.
        /// </summary>
        private bool ShapeOfCell(IMyCubeGrid grid, ref MatrixD world, ref MatrixD inv, double gs, IMySlimBlock slim, Vector3I cell, out BlockShape shape)
        {
            var door = slim.FatBlock as IMyDoor;
            if (door != null) return DoorShape(grid, ref world, ref inv, gs, slim, door, cell, out shape);
            if (slim.BuildLevelRatio < 1f) { shape = BlockShape.Full; return true; }   // being built: its cells
            ShapeDefKey key = DefKey(slim, cell);
            if (defShapes.TryGetValue(key, out shape)) return true;
            if (slim.Min == slim.Max && FullCube(slim))
            {
                shape = BlockShape.Full;
                defShapes[key] = shape;
                return true;
            }
            if (!TakeMeasureRays())
            {
                ShapesUnknown++;
                if (shapeQueue.Count < SHAPE_QUEUE_MAX && shapeQueued.Add(key)) shapeQueue.Add(new QueuedShape { Grid = grid, Slim = slim, Cell = cell });
                shape = BlockShape.Full;
                return false;
            }
            shape = MeasureCell(grid, ref world, ref inv, cell, gs);
            defShapes[key] = shape;
            LogShape(slim, cell, ref shape);
            return true;
        }

        // A measured shape, for the logs (once per kind of block: what the planner takes it for)
        private static void LogShape(IMySlimBlock slim, Vector3I cell, ref BlockShape shape)
        {
            if (!Log.DebugEnabled) return;
            int solid = shape.Kind == 1 ? BlockShape.COUNT : 0;
            if (shape.Kind == 2) for (int b = 0; b < BlockShape.COUNT; b++) if (shape.Solid(b)) solid++;
            Vector3I off = cell - slim.Min;
            // (the mask, grid axes, bit = x + 5y + 25z: the lattice can be replayed offline with the game's shapes)
            Log.Debug("Pathfinding: shape of {0} (forward {1}, up {2}{3}): {4} of {5} voxels solid [{6:X16} {7:X16}]", BlockName(slim), slim.Orientation.Forward, slim.Orientation.Up,
                      slim.Min != slim.Max ? string.Format(", cell {0} {1} {2} of it", off.X, off.Y, off.Z) : "", solid, BlockShape.COUNT, shape.Kind == 2 ? shape.Lo : 0, shape.Kind == 2 ? shape.Hi : 0);
        }

        // A door's cell: closed (or moving) - the cell; open - its kind's open shape (measured from the first door of the
        // kind met open; out of rays: queued, the cell counts whole meanwhile and the lattice waits for it)
        private bool DoorShape(IMyCubeGrid grid, ref MatrixD world, ref MatrixD inv, double gs, IMySlimBlock slim, IMyDoor door, Vector3I cell, out BlockShape shape)
        {
            if (door.Status != Sandbox.ModAPI.Ingame.DoorStatus.Open) { shape = BlockShape.Full; return true; }
            ShapeDefKey key = DefKey(slim, cell);
            if (openDoorShapes.TryGetValue(key, out shape)) return true;
            if (!TakeMeasureRays())
            {
                ShapesUnknown++;
                if (shapeQueue.Count < SHAPE_QUEUE_MAX && shapeQueued.Add(key)) shapeQueue.Add(new QueuedShape { Grid = grid, Slim = slim, Cell = cell });
                shape = BlockShape.Full;
                return false;
            }
            shape = MeasureCell(grid, ref world, ref inv, cell, gs);
            openDoorShapes[key] = shape;
            LogShape(slim, cell, ref shape);
            return true;
        }

        /// <summary>
        /// A static grid's block against a box in its grid's coordinates, by its real shape: does any of it reach into the
        /// box? (Its cell box is known to.) Not measured yet: yes (its cells count).
        /// </summary>
        private bool ShapeBlocks(LocalScene.Entry entry, IMySlimBlock slim, ref MyOrientedBoundingBoxD box)
        {
            double gs = entry.GridSize, h = gs * 0.5, v = gs / BlockShape.N;
            Vector3I min = slim.Min, max = slim.Max;
            bool single = min == max;
            MatrixD world = entry.World, inv = entry.Inv;
            BoundingBoxD reach = box.GetAABB();
            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        var cell = new Vector3I(x, y, z);
                        Vector3D c0 = (Vector3D)cell * gs - h;
                        if (!single)
                        {
                            var cb = new BoundingBoxD(c0, c0 + gs);
                            if (!box.Intersects(ref cb)) continue;
                        }
                        BlockShape s;
                        bool known = ShapeOfCell(entry.Grid, ref world, ref inv, gs, slim, cell, out s);
#if PERF
                        if (!known) RouteStats.ShapeOutOfRays();
#endif
                        if (!known || s.Kind == 1) return true;
                        if (s.Kind == 2 && ShapeGrid.Overlaps(ref s, c0, v, ref box, ref reach)) return true;
                    }
            return false;
        }

        #region Measuring
        private readonly ShapeMeasurer measurer = new ShapeMeasurer();
        private ShapeMeasurer.RayCaster measureCast;
        private IMyCubeGrid measureGrid;
        private MatrixD measureWorld, measureInv;

        // The real shape of the block in 'cell' (ShapeMeasurer: the rays are cast here)
        private BlockShape MeasureCell(IMyCubeGrid grid, ref MatrixD world, ref MatrixD inv, Vector3I cell, double gs)
        {
            if (measureCast == null) measureCast = MeasureRay;
            measureGrid = grid;
            measureWorld = world;
            measureInv = inv;
            Vector3D c0 = (Vector3D)cell * gs - gs * 0.5;
            BlockShape shape = measurer.Measure(c0, gs, measureCast);
            // (next to nothing found: thin parts the voxel lines slip between - railings measured empty, 2026-11-14)
            int solid = 0;
            if (shape.Kind == 2) for (int b = 0; b < BlockShape.COUNT; b++) if (shape.Solid(b)) solid++;
            if (shape.Kind == 0 || (shape.Kind == 2 && solid < MEASURE_FINE_BELOW))
                measureRaysLeft -= measurer.MeasureFine(c0, gs, measureCast, MEASURE_FINE_STEP, ref shape);
            measureGrid = null;
            ShapesMeasured++;
            return shape;
        }

        // One ray (grid coordinates): where it meets the grid being measured
        private void MeasureRay(Vector3D from, Vector3D to, List<Vector3D> into)
        {
            measureHits.Clear();
#if PERF
            long rayT0 = RouteStats.Now();
#endif
            MyAPIGateway.Physics.CastRay(Vector3D.Transform(from, measureWorld), Vector3D.Transform(to, measureWorld), measureHits);
#if PERF
            RouteStats.Ray(RouteStats.RAY_SHAPE, rayT0);
#endif
            for (int n = 0; n < measureHits.Count; n++)
            {
                var e = measureHits[n].HitEntity;
                if (e == null || e.GetTopMostParent() != measureGrid) continue;
                into.Add(Vector3D.Transform(measureHits[n].Position, measureInv));
            }
            measureHits.Clear();
        }

        /// <summary>Session update: the cells met before they were measured, a few per tick.</summary>
        private void ShapesUpdate()
        {
            measureFrame = frame;
            measureRaysLeft = MEASURE_RAYS_PER_TICK;
            // Windows nobody asked for in a while (and closed grids') go
            if (gridShapes.Count > 0 && frame % WINDOW_PRUNE_TICKS == 0)
            {
                long gone = 0;
                foreach (var kv in gridShapes)
                    if ((kv.Value.Grid == null ? kv.Value.Rock.Count == 0 || kv.Value.Rock[0].Voxel.Closed : kv.Value.Grid.MarkedForClose)
                        || frame - kv.Value.UsedFrame > WINDOW_KEEP_TICKS) { gone = kv.Key; break; }
                if (gone != 0) { UnhookWindow(gridShapes[gone]); gridShapes.Remove(gone); }
            }
            for (int n = 0; n < MEASURE_BACKGROUND_CELLS && shapeQueue.Count > 0; n++)
            {
                var qd = shapeQueue[shapeQueue.Count - 1];
                shapeQueue.RemoveAt(shapeQueue.Count - 1);
                if (qd.Grid == null || qd.Grid.MarkedForClose || qd.Slim == null || qd.Slim.IsDestroyed || qd.Slim.CubeGrid != qd.Grid) continue;
                ShapeDefKey key = DefKey(qd.Slim, qd.Cell);
                shapeQueued.Remove(key);
                var door = qd.Slim.FatBlock as IMyDoor;
                if (door != null)
                {
                    // (an open door's kind: only while it is still open - closed or moving, it isn't that shape)
                    if (openDoorShapes.ContainsKey(key) || door.Status != Sandbox.ModAPI.Ingame.DoorStatus.Open) continue;
                }
                else if (defShapes.ContainsKey(key) || qd.Slim.BuildLevelRatio < 1f) continue;
                measureRaysLeft -= MEASURE_RAYS;
                MatrixD world = qd.Grid.WorldMatrix, inv = MatrixD.Invert(world);
                BlockShape measured = MeasureCell(qd.Grid, ref world, ref inv, qd.Cell, qd.Grid.GridSize);
                if (door != null) openDoorShapes[key] = measured;
                else defShapes[key] = measured;
                LogShape(qd.Slim, qd.Cell, ref measured);
            }
        }
        #endregion

        #region A grid's window for the lattice
        private sealed class GridShapes
        {
            public IMyCubeGrid Grid;     // null: a rock-only window (RockFrame)
            public long Key;             // the grid's id (a rock-only window: the asteroid's)
            public readonly List<RockSource> Rock = new List<RockSource>();   // the asteroids in it (AddRock)
            public bool[] RockCell;      // cells rock may be in: asked with the block's shape (RockShape); null: none
            // Its own blocks (the costly part: kept while none is added / removed - its own hooks, not LocalScene's entry,
            // which is dropped after a minute unused and comes back with another version) and what the others (other
            // grids, rock) set over them: the cells as they were before (kind << 32 | shape index), put back when only
            // the others changed (2026-11-19: the window was built again on every trip, ~20 ms; 130-280 ms profiled)
            public bool OwnDirty = true;
            public Action<IMySlimBlock> OnBlock;
            public Action<IMyCubeGrid, IMyCubeGrid> OnSplit;
            public readonly Dictionary<int, long> Saved = new Dictionary<int, long>();
            public long OthersContent;   // what the others set (ApplyOthers): the same again - a field over the window still holds
            // Its own blocks being set a slice a tick (BuildWindow 'sliced': the lattice holds meanwhile, 2026-11-19 - 20 ms
            // in one tick, 130-280 ms profiled); whoever asks for the window next goes on with it
            public bool Building;
            public readonly List<IMySlimBlock> BuildSlims = new List<IMySlimBlock>();
            public int BuildIndex;
            public double BuildMs;

            public void Touch(int idx)
            {
                if (!Saved.ContainsKey(idx)) Saved[idx] = ((long)Occ.Kind[idx] << 32) | (uint)Occ.ShapeIndex[idx];
            }
            public long Others;          // the other static grids in the window (ids and versions)
            public bool Boxes;           // built without shapes (RealShapeConfirm off)
            public readonly ShapeGrid Occ = new ShapeGrid();
            public MatrixD World, Inv;
            public int UsedFrame;
            public int Built;            // which build of a window this is (windowBuilds): a field on it is kept with it
            public readonly List<IMyDoor> Doors = new List<IMyDoor>();   // its doors (their cells are asked every time)

            /// <summary>The doors' states (a field built with a door closed doesn't hold once it opens).</summary>
            public long DoorsHash()
            {
                long h = 7;
                for (int i = 0; i < Doors.Count; i++) h = h * 31 + (Doors[i].Closed ? 9 : (int)Doors[i].Status);
                return h;
            }
        }
        private int windowBuilds;

        private const int WINDOW_MAX_CELLS = 4000000;
        private const int WINDOW_PRUNE_TICKS = 10 * 60, WINDOW_KEEP_TICKS = 5 * 60 * 60;
        private readonly Dictionary<long, GridShapes> gridShapes = new Dictionary<long, GridShapes>();
        private readonly List<LocalScene.Entry> windowEntries = new List<LocalScene.Entry>();
        private readonly List<IMySlimBlock> windowSlims = new List<IMySlimBlock>();
        private readonly Dictionary<int, BlockShape> windowOther = new Dictionary<int, BlockShape>();

        /// <summary>
        /// The window 'min' .. 'max' (cells of 'entry's grid) as a ShapeGrid: the grid's blocks (full, or shaped: asked
        /// when first met), and the other static grids' blocks in it by their cell boxes, as voxels. Kept while nothing in
        /// it changes. Null: too big.
        /// </summary>
        private const double PARKED_SPEED = 0.5;   // m/s: a grid slower than this (docked, parked) is in the lattice's window

        // Another grid in the lattice window: static, or a parked / docked one (a drone at a connector in the corridor:
        // left out, the lattice routed through it, the leg check refused the leg, and the search round it ran out of
        // time - 2026-11-13). Not the drone's own; moving ones are the flight's to dodge.
        private static bool InWindow(ref PathQuery q, LocalScene.Entry e, IMyCubeGrid grid)
        {
            if (e.Voxel || e.Grid == grid || IsOwn(ref q, e.Grid)) return false;
            if (e.Static) return true;
            var ph = e.Grid.Physics;
            return ph == null || ph.LinearVelocity.LengthSquared() < PARKED_SPEED * PARKED_SPEED;
        }

        private GridShapes WindowOf(LocalScene.Entry entry, Vector3I min, Vector3I max, ref PathQuery q, bool sliced)
        {
            MatrixD world = entry.World, inv = entry.Inv;
            return BuildWindow(entry.Grid.EntityId, entry, entry.GridSize, ref world, ref inv, min, max, ref q, sliced);
        }

        /// <summary>A rock-only window (RockFrame): 'world' the frame on the asteroid ('rock'), ROCK_CELL cells.</summary>
        private GridShapes RockWindowOf(LocalScene.Entry rock, ref MatrixD world, Vector3I min, Vector3I max, ref PathQuery q)
        {
            MatrixD inv = MatrixD.Invert(world);
            return BuildWindow(rock.Entity.EntityId, null, ROCK_CELL, ref world, ref inv, min, max, ref q, false);
        }

        // The window of 'entry's grid (null: a rock-only one, keyed by 'key')
        private const double WINDOW_SLICE_MS = 8;   // a window's own blocks set this long a tick, at most (sliced)
        private readonly System.Diagnostics.Stopwatch windowClock = new System.Diagnostics.Stopwatch();
        /// <summary>The last BuildWindow (sliced) returned null for the grid's block tree being built: asked again next tick.</summary>
        private bool windowTreeWait;

        // 'sliced': its own blocks a slice a tick (the window comes back Building until done; the caller holds) - else all now
        private GridShapes BuildWindow(long key, LocalScene.Entry entry, double gs, ref MatrixD world, ref MatrixD inv, Vector3I min, Vector3I max, ref PathQuery q, bool sliced)
        {
            windowTreeWait = false;
            windowClock.Restart();
            var grid = entry != null ? entry.Grid : null;
            double h = gs * 0.5;
            // The other grids there (static, parked)
            var lb = new BoundingBoxD((Vector3D)min * gs - h, (Vector3D)max * gs + h);
            var ob = new MyOrientedBoundingBoxD(lb, world);
            BoundingBoxD wb = ob.GetAABB();
            liveScene.Ensure(new BoundingSphereD(wb.Center, wb.HalfExtents.Length()), frame);
            windowEntries.Clear();
            liveScene.Overlapping(ref wb, windowEntries, frame);
            long others = 0;
            for (int i = 0; i < windowEntries.Count; i++)
            {
                var e = windowEntries[i];
                if (e.Voxel && IsAsteroid(e.Entity)) { others = others * 31 + e.Entity.EntityId * 5 + RockEpoch; continue; }   // (rock: read again now and then)
                if (!InWindow(ref q, e, grid)) continue;
                others = others * 31 + e.Grid.EntityId * 7 + e.Version;
                if (!e.Static)   // (where it is parked: 0.25 m steps)
                {
                    Vector3D p = e.Grid.WorldMatrix.Translation;
                    others = others * 31 + (long)Math.Round(p.X * 4) * 3 + (long)Math.Round(p.Y * 4) * 5 + (long)Math.Round(p.Z * 4) * 7;
                }
            }
            GridShapes g;
            bool boxes = !ShapesOn;
            bool have = gridShapes.TryGetValue(key, out g) && g.Grid == grid;
            bool ownKept = have && !g.Building && !g.OwnDirty && g.Boxes == boxes && g.Occ.Covers(min, max);
            if (ownKept && g.Others == others)
            {
                windowEntries.Clear();
                g.UsedFrame = frame;
                return g;
            }
            if (ownKept)
            {
                // Only the others changed (a drone parked, another grid's blocks, the rock read again): their part again
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var kv in g.Saved)
                {
                    g.Occ.Kind[kv.Key] = (byte)(kv.Value >> 32);
                    g.Occ.ShapeIndex[kv.Key] = (int)(uint)(kv.Value & 0xFFFFFFFFL);
                }
                g.Saved.Clear();
                g.Occ.Overlay = null;
                g.Others = others;
                g.World = world;
                g.Inv = inv;
                g.UsedFrame = frame;
                long was = g.OthersContent;
                g.OthersContent = ApplyOthers(g, ref lb, ref world, ref inv, ref q);
                if (g.OthersContent != was) g.Built = ++windowBuilds;   // (a field over it: flooded again - not when they set the same cells)
                if (Log.DebugEnabled) Log.Debug("Pathfinding: window of {0}: other grids / rock changed, their part again ({1:F1} ms; its own blocks kept{2})", grid != null ? grid.DisplayName : "the rock",
                                                (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                                                g.OthersContent == was ? ", the same cells: fields kept" : "");
                return g;
            }
            Vector3I size = max - min + Vector3I.One;
            // Being built already (an earlier tick, another drone's plan): on with it - unless what it is built from changed
            if (have && g.Building && !g.OwnDirty && g.Boxes == boxes && g.Occ.Min == min && g.Occ.Size == size)
                return BuildOwn(g, ref lb, ref world, ref inv, ref q, others, sliced);
            if ((long)size.X * size.Y * size.Z > WINDOW_MAX_CELLS) { windowEntries.Clear(); return null; }
            // The grid's block tree first (built a slice a tick too: a big grid's ~1 us a block)
            if (sliced && entry != null && !liveScene.BuildStep(entry, WINDOW_SLICE_MS, windowClock)) { windowEntries.Clear(); windowTreeWait = true; return null; }
            var tree = entry != null ? liveScene.BlocksOf(entry) : null;
            if (entry != null && tree == null) { windowEntries.Clear(); return null; }
            if (Log.DebugEnabled && have)
                Log.Debug("Pathfinding: window of {0}: built again ({1})", grid != null ? grid.DisplayName : "the rock",
                          g.OwnDirty ? "its blocks changed" : g.Boxes != boxes ? "real shapes on / off" : "another area");
            if (!have)
            {
                if (g != null) UnhookWindow(g);
                gridShapes[key] = g = new GridShapes { Grid = grid, Key = key };
                if (grid != null)
                {
                    var gh = g;
                    g.OnBlock = b => gh.OwnDirty = true;
                    g.OnSplit = (a, b) => gh.OwnDirty = true;
                    grid.OnBlockAdded += g.OnBlock;
                    grid.OnBlockRemoved += g.OnBlock;
                    grid.OnGridSplit += g.OnSplit;
                }
            }
            g.OwnDirty = false;
            g.Saved.Clear();
            g.UsedFrame = frame;
            g.Others = others;
            g.Boxes = boxes;
            g.World = world;
            g.Inv = inv;
            var occ = g.Occ;
            occ.Init(min, size, gs);
            g.Built = ++windowBuilds;
            g.Doors.Clear();
            occ.Overlay = null;
            // Its own blocks: set below, a slice a tick when sliced
            g.BuildSlims.Clear();
            if (tree != null) tree.OverlapAllBoundingBox(ref lb, g.BuildSlims, 0u, true);
            g.BuildIndex = 0;
            g.BuildMs = 0;
            g.Building = true;
            var gref = g;
            occ.Resolve = (int x, int y, int z, out BlockShape shape) =>
            {
                var slim = gref.Grid != null ? gref.Grid.GetCubeBlock(new Vector3I(x, y, z)) : null;
                if (slim == null) shape = new BlockShape();
                else if (!ShapeOfCell(gref.Grid, ref gref.World, ref gref.Inv, gref.Occ.GridSize, slim, new Vector3I(x, y, z), out shape)) return false;
                if (gref.RockCell != null)
                {
                    int idx = gref.Occ.Index(x, y, z);
                    if (idx >= 0 && gref.RockCell[idx]) MergeShape(ref shape, RockShape(gref, x, y, z));
                }
                return true;
            };
            return BuildOwn(g, ref lb, ref world, ref inv, ref q, others, sliced);
        }

        // The window's own blocks into its cells (from BuildSlims, on from BuildIndex), then the others'. Sliced: out of
        // this tick's slice - back with it Building (the caller holds and asks again)
        private GridShapes BuildOwn(GridShapes g, ref BoundingBoxD lb, ref MatrixD world, ref MatrixD inv, ref PathQuery q, long others, bool sliced)
        {
            var occ = g.Occ;
            bool boxes = g.Boxes;
            double t0 = windowClock.Elapsed.TotalMilliseconds;
            g.UsedFrame = frame;
            var slims = g.BuildSlims;
            while (g.BuildIndex < slims.Count)
            {
                if (sliced && (g.BuildIndex & 63) == 0 && g.BuildIndex > 0 && windowClock.Elapsed.TotalMilliseconds > WINDOW_SLICE_MS)
                {
                    g.BuildMs += windowClock.Elapsed.TotalMilliseconds - t0;
                    windowEntries.Clear();
                    return g;   // (still Building)
                }
                var slim = slims[g.BuildIndex++];
                bool door = slim.FatBlock is IMyDoor;
                if (door) g.Doors.Add((IMyDoor)slim.FatBlock);
                byte kind = door ? ShapeGrid.SHAPED : boxes || FullCube(slim) ? ShapeGrid.FULL : ShapeGrid.SHAPED;
                Vector3I a = slim.Min, b = slim.Max;
                for (int x = a.X; x <= b.X; x++)
                    for (int y = a.Y; y <= b.Y; y++)
                        for (int z = a.Z; z <= b.Z; z++) occ.SetCell(x, y, z, kind, door);
            }
            slims.Clear();
            g.Others = others;
            g.OthersContent = ApplyOthers(g, ref lb, ref world, ref inv, ref q);
            g.Building = false;
            g.BuildMs += windowClock.Elapsed.TotalMilliseconds - t0;
            return g;
        }

        // The others' part of a window (windowEntries: what is in it): other grids' cell boxes as voxels of this grid, the
        // asteroids' cells (asked when met: the block's shape there and the rock's). Every cell it changes saved first.
        private long ApplyOthers(GridShapes g, ref BoundingBoxD lb, ref MatrixD world, ref MatrixD inv, ref PathQuery q)
        {
            var occ = g.Occ;
            long content = 0;   // (order-free: a sum of each cell's)
            windowOther.Clear();
            for (int i = 0; i < windowEntries.Count; i++)
            {
                var e = windowEntries[i];
                if (!InWindow(ref q, e, g.Grid)) continue;
                AddOtherGrid(occ, e, ref lb, ref world, ref inv);
            }
            foreach (var kv in windowOther)
            {
                BlockShape s = kv.Value;
                s.Normalise();
                if (s.Kind == 0) continue;
                byte kind = occ.Kind[kv.Key];
                if (kind == ShapeGrid.FULL) continue;
                content += ((long)kv.Key * 1000003 + s.Kind) * 31 + (long)(s.Lo * 0x9E3779B97F4A7C15UL) + (long)(s.Hi * 0xC2B2AE3D27D4EB4FUL);
                g.Touch(kv.Key);
                if (kind == ShapeGrid.EMPTY)
                {
                    if (s.Kind == 1) { occ.Kind[kv.Key] = ShapeGrid.FULL; continue; }
                    occ.Kind[kv.Key] = ShapeGrid.SHAPED;
                    occ.ShapeIndex[kv.Key] = occ.Shapes.Count;
                    occ.Shapes.Add(s);
                    continue;
                }
                // (a shaped block of its own there too: both - asked again with it)
                if (occ.Overlay == null) occ.Overlay = new Dictionary<int, BlockShape>();
                occ.Overlay[kv.Key] = s;
                if (occ.ShapeIndex[kv.Key] >= 0) occ.ShapeIndex[kv.Key] = -1;
            }
            windowOther.Clear();
            AddRock(g, windowEntries, ref lb, ref inv);
            for (int i = 0; i < g.Rock.Count; i++) content += g.Rock[i].Voxel.EntityId * 17 + RockEpoch;   // (rock read again: may differ)
            windowEntries.Clear();
            return content;
        }

        private static void UnhookWindow(GridShapes g)
        {
            if (g.Grid == null || g.OnBlock == null) return;
            g.Grid.OnBlockAdded -= g.OnBlock;
            g.Grid.OnBlockRemoved -= g.OnBlock;
            g.Grid.OnGridSplit -= g.OnSplit;
            g.OnBlock = null;
        }

        // Another grid's blocks in the window: each one's cell box set in the voxels (of the window's grid) it reaches
        private void AddOtherGrid(ShapeGrid occ, LocalScene.Entry e, ref BoundingBoxD windowLocal, ref MatrixD world, ref MatrixD inv)
        {
            var tree = liveScene.BlocksOf(e);
            if (tree == null) return;
            MatrixD toOther = world * e.Inv, toWindow = e.World * inv;   // window grid -> the other grid, and back
            var inOther = new MyOrientedBoundingBoxD(windowLocal, toOther);
            BoundingBoxD ab = inOther.GetAABB();
            windowSlims.Clear();
            tree.OverlapAllBoundingBox(ref ab, windowSlims, 0u, true);
            const int N = BlockShape.N;
            double gs = occ.GridSize, h = gs * 0.5, v = gs / N;
            for (int i = 0; i < windowSlims.Count; i++)
            {
                BoundingBoxD bb = LocalScene.BlockBox(windowSlims[i], e.GridSize);
                var box = new MyOrientedBoundingBoxD(bb, toWindow);   // the block's cells, in the window's coordinates
                BoundingBoxD reach = box.GetAABB();
                int x0 = (int)Math.Floor((reach.Min.X + h) / gs), x1 = (int)Math.Floor((reach.Max.X + h) / gs);
                int y0 = (int)Math.Floor((reach.Min.Y + h) / gs), y1 = (int)Math.Floor((reach.Max.Y + h) / gs);
                int z0 = (int)Math.Floor((reach.Min.Z + h) / gs), z1 = (int)Math.Floor((reach.Max.Z + h) / gs);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            int idx = occ.Index(x, y, z);
                            if (idx < 0 || occ.Kind[idx] == ShapeGrid.FULL) continue;
                            Vector3D c0 = new Vector3D(x, y, z) * gs - h;
                            // (inside by a hair: a block that only touches the cell / a voxel doesn't take it)
                            var cb = new BoundingBoxD(c0 + ShapeGrid.EPS, c0 + gs - ShapeGrid.EPS);
                            if (!box.Intersects(ref cb)) continue;
                            BlockShape s;
                            windowOther.TryGetValue(idx, out s);
                            if (s.Kind == 1) continue;
                            if (box.Contains(ref cb) == ContainmentType.Contains) { windowOther[idx] = BlockShape.Full; continue; }
                            for (int k = 0; k < N; k++)
                                for (int j = 0; j < N; j++)
                                    for (int ii = 0; ii < N; ii++)
                                    {
                                        int bit = ii + N * j + N * N * k;
                                        if (s.Kind == 2 && s.Solid(bit)) continue;
                                        Vector3D lo = c0 + new Vector3D(ii, j, k) * v;
                                        var vb = new BoundingBoxD(lo + ShapeGrid.EPS, lo + v - ShapeGrid.EPS);
                                        if (box.Intersects(ref vb)) s.Set(bit);
                                    }
                            windowOther[idx] = s;
                        }
            }
            windowSlims.Clear();
        }
        #endregion
    }
}
