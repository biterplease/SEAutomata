using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Construction
{
    /// <summary>
    /// Build order (TODO/08-build-order-planning.md), shared by stand-alone drones (ScanArea, one worker) and the
    /// Construction Computer / Orchestrator (several workers: <see cref="Fronts"/>). Pure: grid geometry in, an order out;
    /// no drone state, no allocations per call beyond its reused buffers.
    ///
    /// Per physical grid:
    ///  1. Work units: blocks grouped in chunks one tool position covers - 3x3x3 cells on small grids, single cells on
    ///     large grids - aligned on the seed (multi-cell blocks go by their centre cell).
    ///  2. Order: layer by layer, bottom up; within a layer centre-out in rings around the seed column, each ring in
    ///     slice order (the slice through the seed - the plane containing "up" and the longer horizontal axis - first,
    ///     then the neighbouring slices, alternating sides). Within a unit, the block nearest its centre first (its
    ///     tool position welds the rest). (TODO/08 started slice-first; with top-face welding a column built ahead of
    ///     its neighbours blocks the room above them for any drone wider than a block.)
    ///  3. Reachability: a unit whose blocks are not built yet (projections) is held back while welding it would leave
    ///     a neighbouring unit with no free face; the next unit in order goes first. Hand-placed skeletons are already
    ///     solid: welding them changes nothing around them.
    ///  Grinding is the reverse (outer slices first, top first).
    /// </summary>
    public sealed class BuildPlanner
    {
        public struct Item
        {
            public int Tag;            // the caller's index
            public Vector3I Min, Max;  // cells in the physical grid
            public bool Projected;     // occupies its cells only once built
            public bool Grind;
        }

        private struct Unit
        {
            public Vector3I Chunk;     // (slice, level, across) chunk coordinates
            public long Key;
            public int First, Count;   // into unitItems
            public bool Projected;
            public bool Done;
        }

        private const int LOOKAHEAD = 32;   // units tried past the next one when it would wall something in

        private readonly List<Unit> units = new List<Unit>();
        private readonly List<int> unitItems = new List<int>();           // indices into the items list
        private readonly List<int> sorted = new List<int>();              // unit indices in key order
        private readonly Dictionary<Vector3I, int> unitAt = new Dictionary<Vector3I, int>();
        private readonly Dictionary<Vector3I, int> unitOfCell = new Dictionary<Vector3I, int>();   // every cell of every item
        private readonly HashSet<Vector3I> scheduled = new HashSet<Vector3I>();   // projected cells welded earlier in the order
        private readonly List<Vector3I> tentative = new List<Vector3I>();
        private readonly List<int> affected = new List<int>();
        private readonly List<bool> affectedOpen = new List<bool>();
        private readonly List<Vector3I> itemChunk = new List<Vector3I>();  // per item (this part), its unit's chunk
        private readonly List<int> sliceOfOrder = new List<int>();        // slice of each output entry since Reset (Fronts)
        private readonly List<int> partOfOrder = new List<int>();         // part (grid, weld / grind) of each entry
        private int part;

        /// <summary>Debug: one line per part (weld / grind) of the last <see cref="Plan"/> call.</summary>
        public readonly List<string> Report = new List<string>();
        private const int REPORT_ORDER_MAX = 24;   // blocks listed per part
        private static readonly string[] AXIS = { "X", "Y", "Z" };

        // The current grid's axes (indices 0..2 of grid coordinates) and the seed
        private int vAxis, lAxis, sAxis, vSign;
        private Vector3I seed;
        private int side;

        // Sweep frames (axes, direction, seed: the unit grid's alignment) kept per physical grid and weld / grind, shared
        // by every planner: a rescan works on in the same direction and unit alignment as the scans before it, instead
        // of re-deriving them from what is left (the seed / long axis moved with every scan). Dropped when unused.
        private struct Frame { public int VAxis, VSign, LAxis, SAxis, Side, LastUsed; public Vector3I Seed; }
        private static readonly Dictionary<long, Frame> frames = new Dictionary<long, Frame>();
        #region Timing
        private const int FRAME_KEEP_TICKS = 15 * 60 * 60;   // 15 min without a scan of that grid
        #endregion
        private bool frameKept;                             // this part's frame came from an earlier scan

        private static long FrameKey(long gridId, bool grind) { return grind ? ~gridId : gridId; }

        /// <summary>Forgets every sweep frame (session unload).</summary>
        public static void ClearFrames() { frames.Clear(); }

        /// <summary>World save: the sweep frames (weld and grind) of these grids, for a block's save data.</summary>
        public static void ExportFrames(IEnumerable<long> gridIds, List<SweepFrameState> into)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            foreach (var id in gridIds)
                for (int g = 0; g < 2; g++)
                {
                    Frame f;
                    long key = FrameKey(id, g == 1);
                    if (!frames.TryGetValue(key, out f) || now - f.LastUsed >= FRAME_KEEP_TICKS) continue;
                    into.Add(new SweepFrameState { Key = key, VAxis = f.VAxis, VSign = f.VSign, LAxis = f.LAxis, SAxis = f.SAxis, Side = f.Side,
                                                   SeedX = f.Seed.X, SeedY = f.Seed.Y, SeedZ = f.Seed.Z, AgeTicks = Math.Max(0, now - f.LastUsed) });
                }
        }

        /// <summary>World load: saved sweep frames back (the newest wins when several blocks saved the same grid's).</summary>
        public static void ImportFrames(List<SweepFrameState> saved)
        {
            if (saved == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            for (int i = 0; i < saved.Count; i++)
            {
                var s = saved[i];
                if (s == null) continue;
                int lastUsed = now - Math.Max(0, s.AgeTicks);
                Frame existing;
                if (frames.TryGetValue(s.Key, out existing) && existing.LastUsed >= lastUsed) continue;
                frames[s.Key] = new Frame { VAxis = s.VAxis, VSign = s.VSign, LAxis = s.LAxis, SAxis = s.SAxis, Side = s.Side,
                                            Seed = new Vector3I(s.SeedX, s.SeedY, s.SeedZ), LastUsed = lastUsed };
            }
        }

        /// <summary>
        /// The work unit (cells, inclusive) 'cell' of 'grid' belongs to in the weld frame the last scan of that grid used;
        /// false when there is none (no scan yet, or a large grid: units are single cells there).
        /// </summary>
        public static bool TryUnitBox(IMyCubeGrid grid, Vector3I cell, out Vector3I min, out Vector3I max)
        {
            min = max = cell;
            Frame f;
            if (grid == null || !frames.TryGetValue(FrameKey(grid.EntityId, false), out f) || f.Side <= 1) return false;
            Vector3I r = cell - f.Seed;
            int off = f.Side / 2;
            Vector3I chunk = new Vector3I(FloorDiv(Get(r, f.SAxis) + off, f.Side),
                                          FloorDiv(Get(r, f.VAxis) * f.VSign + off, f.Side),
                                          FloorDiv(Get(r, f.LAxis) + off, f.Side));
            Vector3I c = f.Seed;
            c = With(c, f.SAxis, Get(f.Seed, f.SAxis) + chunk.X * f.Side);
            c = With(c, f.VAxis, Get(f.Seed, f.VAxis) + chunk.Y * f.Side * f.VSign);
            c = With(c, f.LAxis, Get(f.Seed, f.LAxis) + chunk.Z * f.Side);
            min = c - new Vector3I(off);
            max = c + new Vector3I(off);
            return true;
        }

        /// <summary>
        /// Appends the Tags of 'items' (one physical grid) to 'order', in build order. 'top': world "up" for this
        /// build (gravity, else the projector's up, else the area's up). 'inGravity': no faces from below.
        /// </summary>
        public void Plan(IMyCubeGrid grid, List<Item> items, Vector3D top, bool inGravity, List<int> order)
        {
            Report.Clear();
            if (grid == null || items.Count == 0) return;
            part++;
            PlanPart(grid, items, top, inGravity, false, order);   // welding
            part++;
            PlanPart(grid, items, top, inGravity, true, order);    // grinding
        }

        private void PlanPart(IMyCubeGrid grid, List<Item> items, Vector3D top, bool inGravity, bool grind, List<int> order)
        {
            units.Clear();
            unitItems.Clear();
            sorted.Clear();
            unitAt.Clear();
            unitOfCell.Clear();
            scheduled.Clear();
            itemChunk.Clear();
            int n = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].Grind == grind) n++;
            if (n == 0) return;

            // Axes: "up" is the grid axis closest to 'top'; the slices contain it and the longer horizontal axis
            Vector3D t = Vector3D.TransformNormal(top, grid.WorldMatrixNormalizedInv);
            vAxis = Math.Abs(t.X) >= Math.Abs(t.Y) && Math.Abs(t.X) >= Math.Abs(t.Z) ? 0 : Math.Abs(t.Y) >= Math.Abs(t.Z) ? 1 : 2;
            vSign = Get(t, vAxis) >= 0 ? 1 : -1;
            int h1 = vAxis == 0 ? 1 : 0, h2 = vAxis == 2 ? 1 : 2;
            Vector3I lo = Vector3I.MaxValue, hi = Vector3I.MinValue;
            Vector3D sum = Vector3D.Zero;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Grind != grind) continue;
                Vector3I c = Centre(items[i]);
                lo = Vector3I.Min(lo, c);
                hi = Vector3I.Max(hi, c);
                sum += (Vector3D)c;
            }
            int ext1 = Get(hi, h1) - Get(lo, h1), ext2 = Get(hi, h2) - Get(lo, h2);
            lAxis = ext1 >= ext2 ? h1 : h2;
            sAxis = lAxis == h1 ? h2 : h1;

            // Seed: nearest the centre of the set across, lowest
            Vector3D mean = sum / n;
            double best = double.MaxValue;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Grind != grind) continue;
                Vector3I c = Centre(items[i]);
                double dl = Get(c, lAxis) - Get(mean, lAxis), ds = Get(c, sAxis) - Get(mean, sAxis);
                double score = (dl * dl + ds * ds) * 1000 + Get(c, vAxis) * vSign;
                if (score < best) { best = score; seed = c; }
            }
            side = grid.GridSizeEnum == MyCubeSize.Small ? 3 : 1;

            // The frame of the earlier scans of this grid, if any (kept: same sweep direction, same unit alignment)
            int now = MyAPIGateway.Session != null ? MyAPIGateway.Session.GameplayFrameCounter : 0;
            long frameKey = FrameKey(grid.EntityId, grind);
            Frame kept;
            frameKept = frames.TryGetValue(frameKey, out kept) && now - kept.LastUsed < FRAME_KEEP_TICKS && kept.Side == side;
            if (frameKept)
            {
                vAxis = kept.VAxis; vSign = kept.VSign; lAxis = kept.LAxis; sAxis = kept.SAxis; seed = kept.Seed;
            }
            frames[frameKey] = new Frame { VAxis = vAxis, VSign = vSign, LAxis = lAxis, SAxis = sAxis, Side = side, Seed = seed, LastUsed = now };

            // Units (each item's chunk worked out once)
            for (int i = 0; i < items.Count; i++)
            {
                itemChunk.Add(items[i].Grind == grind ? ChunkOf(Centre(items[i])) : Vector3I.MinValue);
                if (items[i].Grind != grind) continue;
                Vector3I chunk = itemChunk[i];
                int u;
                if (!unitAt.TryGetValue(chunk, out u))
                {
                    u = units.Count;
                    unitAt[chunk] = u;
                    units.Add(new Unit { Chunk = chunk, Key = KeyOf(chunk, grind) });
                }
                if (items[i].Projected)
                {
                    var unit = units[u];
                    unit.Projected = true;
                    units[u] = unit;
                }
                // Every cell of the item -> its unit (multi-cell blocks wall in what touches any of their cells)
                Vector3I c;
                for (c.X = items[i].Min.X; c.X <= items[i].Max.X; c.X++)
                    for (c.Y = items[i].Min.Y; c.Y <= items[i].Max.Y; c.Y++)
                        for (c.Z = items[i].Min.Z; c.Z <= items[i].Max.Z; c.Z++)
                            unitOfCell[c] = u;
            }
            // Items grouped per unit, nearest the unit centre first
            for (int u = 0; u < units.Count; u++)
            {
                var unit = units[u];
                unit.First = unitItems.Count;
                Vector3I centre = ChunkCentre(unit.Chunk);
                for (int i = 0; i < items.Count; i++)
                {
                    if (itemChunk[i] != unit.Chunk || items[i].Grind != grind) continue;
                    int d = DistSq(Centre(items[i]), centre);
                    int at = unitItems.Count;
                    while (at > unit.First && DistSq(Centre(items[unitItems[at - 1]]), centre) > d) at--;
                    unitItems.Insert(at, i);
                }
                unit.Count = unitItems.Count - unit.First;
                units[u] = unit;
            }
            // Units in key order (insertion: System.Comparison isn't whitelisted)
            for (int u = 0; u < units.Count; u++)
            {
                long key = units[u].Key;
                int at = sorted.Count;
                while (at > 0 && units[sorted[at - 1]].Key > key) at--;
                sorted.Insert(at, u);
            }

            // Greedy, keeping every unit reachable
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int heldBack = 0, forced = 0, projectedUnits = 0, emitted = 0;
            for (int u = 0; u < units.Count; u++) if (units[u].Projected) projectedUnits++;
            var orderText = new System.Text.StringBuilder();
            int head = 0;   // sorted[..head] are all done
            for (int done = 0; done < sorted.Count; done++)
            {
                while (head < sorted.Count && units[sorted[head]].Done) head++;
                int pick = -1, firstOpen = -1, tried = 0;
                for (int k = head; k < sorted.Count && tried <= LOOKAHEAD; k++)
                {
                    int u = sorted[k];
                    if (units[u].Done) continue;
                    if (firstOpen < 0) firstOpen = u;
                    tried++;
                    if (grind || !units[u].Projected || KeepsNeighboursReachable(grid, items, u, inGravity)) { pick = u; break; }
                }
                if (pick < 0) { pick = firstOpen; forced++; }   // nothing keeps everything reachable: the order as is
                else if (pick != firstOpen) heldBack++;
                var unit = units[pick];
                unit.Done = true;
                units[pick] = unit;
                int slice = unit.Chunk.X;
                for (int k = unit.First; k < unit.First + unit.Count; k++)
                {
                    var it = items[unitItems[k]];
                    order.Add(it.Tag);
                    sliceOfOrder.Add(slice);
                    partOfOrder.Add(part);
                    if (emitted++ < REPORT_ORDER_MAX)
                        orderText.Append(k == unit.First ? " | " : " ").Append(Centre(it).ToString());
                    if (!grind && it.Projected) AddCells(it.Min, it.Max, scheduled);
                }
            }
            Report.Add(string.Format("{0} '{1}': {2} blocks in {3} units ({4} cells each way, {5} projected), up {6}{7}, slices across {8} "
                                     + "(long axis {9}), seed {10}{16}; {11} held back (would wall in a neighbour), {12} forced; {13:F2} ms; order (| = new unit):{14}{15}",
                grind ? "Grind" : "Weld", grid.DisplayName, n, units.Count, side, projectedUnits, vSign > 0 ? "+" : "-", AXIS[vAxis],
                AXIS[sAxis], AXIS[lAxis], seed, heldBack, forced, sw.Elapsed.TotalMilliseconds, orderText, emitted > REPORT_ORDER_MAX ? " ..." : "",
                frameKept ? " (frame kept from earlier scans)" : ""));
        }

        /// <summary>
        /// Several workers: splits what <see cref="Plan"/> output since <see cref="Reset"/> (its first 'count' entries) into
        /// 'workers' fronts of neighbouring slices, as far apart as the set allows; 'frontOf' gets each entry's front.
        /// </summary>
        public void Fronts(int workers, int count, List<int> frontOf)
        {
            frontOf.Clear();
            if (workers < 1) workers = 1;
            count = Math.Min(count, sliceOfOrder.Count);
            // Slices are numbered per part (grid, weld / grind: each has its own seed): split each part on its own
            int start = 0;
            while (start < count)
            {
                int end = start;
                int lo = int.MaxValue, hi = int.MinValue;
                while (end < count && partOfOrder[end] == partOfOrder[start])
                {
                    lo = Math.Min(lo, sliceOfOrder[end]);
                    hi = Math.Max(hi, sliceOfOrder[end]);
                    end++;
                }
                int width = Math.Max(1, (hi - lo + workers) / workers);
                for (int i = start; i < end; i++) frontOf.Add(Math.Min(workers - 1, (sliceOfOrder[i] - lo) / width));
                start = end;
            }
        }

        /// <summary>Forgets the slices recorded for <see cref="Fronts"/> (call before planning a new set).</summary>
        public void Reset()
        {
            sliceOfOrder.Clear();
            partOfOrder.Clear();
            part = 0;
        }

        // Would welding unit 'u' (its projected cells becoming solid) take the last free face of another open unit?
        // Only units that had one before count (one already walled in doesn't hold everything around it back).
        private bool KeepsNeighboursReachable(IMyCubeGrid grid, List<Item> items, int u, bool inGravity)
        {
            var unit = units[u];
            tentative.Clear();
            for (int k = unit.First; k < unit.First + unit.Count; k++)
            {
                var it = items[unitItems[k]];
                if (!it.Projected) continue;
                Vector3I c;
                for (c.X = it.Min.X; c.X <= it.Max.X; c.X++)
                    for (c.Y = it.Min.Y; c.Y <= it.Max.Y; c.Y++)
                        for (c.Z = it.Min.Z; c.Z <= it.Max.Z; c.Z++)
                            if (!scheduled.Contains(c)) tentative.Add(c);
            }
            if (tentative.Count == 0) return true;
            // Open units with a cell next to one of the new cells: the faces that can be lost
            affected.Clear();
            affectedOpen.Clear();
            for (int t = 0; t < tentative.Count; t++)
                for (int i = 0; i < 6; i++)
                {
                    int v;
                    if (!unitOfCell.TryGetValue(tentative[t] + Base6Directions.GetIntVector((Base6Directions.Direction)i), out v)) continue;
                    if (v == u || units[v].Done || affected.Contains(v)) continue;
                    affected.Add(v);
                }
            for (int i = 0; i < affected.Count; i++) affectedOpen.Add(HasFreeFace(grid, items, affected[i], inGravity));
            for (int i = 0; i < tentative.Count; i++) scheduled.Add(tentative[i]);
            bool ok = true;
            for (int i = 0; i < affected.Count && ok; i++)
                if (affectedOpen[i] && !HasFreeFace(grid, items, affected[i], inGravity)) ok = false;
            for (int i = 0; i < tentative.Count; i++) scheduled.Remove(tentative[i]);   // added for real when picked
            tentative.Clear();
            return ok;
        }

        // Any block of unit 'v' with an outer face whose cell is free (not built, not welded earlier in the order)?
        private bool HasFreeFace(IMyCubeGrid grid, List<Item> items, int v, bool inGravity)
        {
            var unit = units[v];
            for (int k = unit.First; k < unit.First + unit.Count; k++)
            {
                var it = items[unitItems[k]];
                for (int i = 0; i < 6; i++)
                {
                    Vector3I dir = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                    if (inGravity && Get(dir, vAxis) == -vSign) continue;   // from below: through the floor
                    Vector3I lo = it.Min, hi = it.Max;
                    if (dir.X > 0) lo.X = it.Max.X; else if (dir.X < 0) hi.X = it.Min.X;
                    if (dir.Y > 0) lo.Y = it.Max.Y; else if (dir.Y < 0) hi.Y = it.Min.Y;
                    if (dir.Z > 0) lo.Z = it.Max.Z; else if (dir.Z < 0) hi.Z = it.Min.Z;
                    Vector3I c;
                    for (c.X = lo.X; c.X <= hi.X; c.X++)
                        for (c.Y = lo.Y; c.Y <= hi.Y; c.Y++)
                            for (c.Z = lo.Z; c.Z <= hi.Z; c.Z++)
                            {
                                Vector3I o = c + dir;
                                if (!grid.CubeExists(o) && !scheduled.Contains(o)) return true;
                            }
                }
            }
            return false;
        }

        private static void AddCells(Vector3I min, Vector3I max, HashSet<Vector3I> set)
        {
            Vector3I c;
            for (c.X = min.X; c.X <= max.X; c.X++)
                for (c.Y = min.Y; c.Y <= max.Y; c.Y++)
                    for (c.Z = min.Z; c.Z <= max.Z; c.Z++)
                        set.Add(c);
        }

        // Chunk coordinates (slice, level, across), the seed's chunk at 0; levels counted upwards
        private Vector3I ChunkOf(Vector3I cell)
        {
            Vector3I r = cell - seed;
            int off = side / 2;
            return new Vector3I(FloorDiv(Get(r, sAxis) + off, side),
                                FloorDiv(Get(r, vAxis) * vSign + off, side),
                                FloorDiv(Get(r, lAxis) + off, side));
        }

        private Vector3I ChunkCentre(Vector3I chunk)
        {
            Vector3I c = seed;
            c = With(c, sAxis, Get(seed, sAxis) + chunk.X * side);
            c = With(c, vAxis, Get(seed, vAxis) + chunk.Y * side * vSign);
            c = With(c, lAxis, Get(seed, lAxis) + chunk.Z * side);
            return c;
        }

        // Slice rank (0, then +1, -1, +2, -2...), level, distance across, side across
        // Layer (bottom up), ring around the seed column (centre out), slice rank (0, then +1, -1, +2, -2...),
        // distance along the slice, side. Layers first, not slices: the drone welds top faces and is wider than a
        // block; a column built ahead of its neighbours stands right next to their top faces and leaves no room above
        // them (seen in testing). Slices still order each ring and split the work into fronts.
        private static long KeyOf(Vector3I chunk, bool grind)
        {
            int s = chunk.X;
            long rank = s == 0 ? 0 : 2L * Math.Abs(s) - (s > 0 ? 1 : 0);
            long ring = Math.Max(Math.Abs(s), Math.Abs(chunk.Z));
            long key = ((((long)(chunk.Y + 4096) * 1024 + Math.Min(ring, 1023)) * 2048 + Math.Min(rank, 2047)) * 1024
                        + Math.Min(Math.Abs(chunk.Z), 1023)) * 2 + (chunk.Z < 0 ? 1 : 0);
            return grind ? -key : key;   // grinding: outside in, top down
        }

        private static Vector3I Centre(Item it)
        {
            return new Vector3I(FloorDiv(it.Min.X + it.Max.X, 2), FloorDiv(it.Min.Y + it.Max.Y, 2), FloorDiv(it.Min.Z + it.Max.Z, 2));
        }

        private static int DistSq(Vector3I a, Vector3I b)
        {
            Vector3I d = a - b;
            return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
        }

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
        }

        private static int Get(Vector3I v, int axis) { return axis == 0 ? v.X : axis == 1 ? v.Y : v.Z; }
        private static double Get(Vector3D v, int axis) { return axis == 0 ? v.X : axis == 1 ? v.Y : v.Z; }

        private static Vector3I With(Vector3I v, int axis, int value)
        {
            if (axis == 0) v.X = value; else if (axis == 1) v.Y = value; else v.Z = value;
            return v;
        }
    }
}

namespace Automata.Construction
{
    /// <summary>A planner sweep frame in a block's save data (see <see cref="BuildPlanner.ExportFrames"/>).</summary>
    [ProtoBuf.ProtoContract]
    public class SweepFrameState
    {
        [ProtoBuf.ProtoMember(1)] public long Key;
        [ProtoBuf.ProtoMember(2)] public int VAxis;
        [ProtoBuf.ProtoMember(3)] public int VSign;
        [ProtoBuf.ProtoMember(4)] public int LAxis;
        [ProtoBuf.ProtoMember(5)] public int SAxis;
        [ProtoBuf.ProtoMember(6)] public int Side;
        [ProtoBuf.ProtoMember(7)] public int SeedX;
        [ProtoBuf.ProtoMember(8)] public int SeedY;
        [ProtoBuf.ProtoMember(9)] public int SeedZ;
        [ProtoBuf.ProtoMember(10)] public int AgeTicks;
    }
}
