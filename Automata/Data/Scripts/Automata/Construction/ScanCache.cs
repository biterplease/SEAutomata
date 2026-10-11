using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Automata.Construction
{
    /// <summary>
    /// What the last scan found, per grid group, so a scan only looks again at the groups that changed:
    ///  - grids tell when blocks are added, removed or change integrity (events; integrity changes only count when a
    ///    block starts or stops needing work, not while a drone welds it);
    ///  - a projector's own report (blocks total / remaining / armor / buildable, offset, rotation, projected grid) says
    ///    whether its projection changed;
    ///  - a grid that moved, or whose owner / drone state changed, is looked at again.
    /// A group with the same signature as at the last scan gives its remembered targets without ScanArea. What has no
    /// event (paint: grind / weld-ignore colours; something moving into a projection's way) waits for the full rescan the
    /// owner forces every <see cref="FULL_RESCAN_SECONDS"/> (it flushes the cache).
    /// </summary>
    public sealed class ScanCache
    {
        public const int FULL_RESCAN_SECONDS = 120;

        private const double POSITION_STEP = 20.0;     // poses compared to 5 cm
        private const double ORIENTATION_STEP = 500.0; // and 0.002 of a unit vector

        #region Watched grids
        private sealed class Watched
        {
            public IMyCubeGrid Grid;
            public ScanCache Owner;
            public int Version;       // bumped by every event that can change what a scan finds
            public int LastSeen;
            public readonly HashSet<Vector3I> Known = new HashSet<Vector3I>();   // cells of the last scan's targets in this grid

            public void Subscribe()
            {
                Grid.OnBlockAdded += Added;
                Grid.OnBlockRemoved += Removed;
                Grid.OnBlockIntegrityChanged += Integrity;
                Grid.OnGridBlockDamaged += Damaged;
                Grid.OnGridSplit += Split;
                Grid.OnGridMerge += Split;
                Grid.OnGridChanged += Changed;
                Grid.OnBlockOwnershipChanged += Changed;
                Grid.OnIsStaticChanged += StaticChanged;
            }

            public void Unsubscribe()
            {
                Grid.OnBlockAdded -= Added;
                Grid.OnBlockRemoved -= Removed;
                Grid.OnBlockIntegrityChanged -= Integrity;
                Grid.OnGridBlockDamaged -= Damaged;
                Grid.OnGridSplit -= Split;
                Grid.OnGridMerge -= Split;
                Grid.OnGridChanged -= Changed;
                Grid.OnBlockOwnershipChanged -= Changed;
                Grid.OnIsStaticChanged -= StaticChanged;
            }

            private void Added(IMySlimBlock b) { Version++; }
            private void Removed(IMySlimBlock b) { Version++; }
            private void Integrity(IMySlimBlock b) { Check(b); }
            private void Damaged(IMySlimBlock b, float damage, MyHitInfo? hit, long attacker) { Check(b); }
            private void Split(IMyCubeGrid a, IMyCubeGrid b) { Version++; }
            private void Changed(IMyCubeGrid g) { Version++; }
            private void StaticChanged(IMyCubeGrid g, bool isStatic) { Version++; }

            // A block that now needs work and wasn't a target, or one that was a target and no longer needs it. A target
            // still needing work (a drone welding it, more damage on it) changes nothing a scan would find.
            private void Check(IMySlimBlock b)
            {
                if (b == null) return;
                bool needs = ConstructionComputer.NeedsWork(b, Owner.modes, Owner.grindColor, Owner.weldIgnoreColor);
                if (needs != Known.Contains(b.Position)) Version++;
            }
        }

        private readonly Dictionary<long, Watched> watched = new Dictionary<long, Watched>();
        private int scanNo;
        #endregion

        #region Groups
        private sealed class Entry
        {
            public ulong Signature;
            public int MaxJobs;          // the target limit it was scanned with
            public bool Truncated;       // it found MaxJobs: more may exist
            public int Seen;
            public readonly List<ConstructionTarget> Targets = new List<ConstructionTarget>();
        }

        private readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        private readonly List<long> removeBuffer = new List<long>();
        #endregion

        private ulong settingsSignature;
        private WorkModes modes;
        private Color grindColor, weldIgnoreColor;

        public int Groups { get { return entries.Count; } }

        /// <summary>
        /// A scan starts: with different area / modes / colours / priority than the last, nothing remembered counts.
        /// </summary>
        public void Begin(ref MatrixD area, ref Vector3D half, WorkModes workModes, Color grind, Color weldIgnore, WorkPriority priority)
        {
            scanNo++;
            ulong h = Seed;
            h = MixPose(h, ref area);
            h = Mix(h, Quantize(half.X, POSITION_STEP));
            h = Mix(h, Quantize(half.Y, POSITION_STEP));
            h = Mix(h, Quantize(half.Z, POSITION_STEP));
            h = Mix(h, (long)workModes);
            h = Mix(h, grind.PackedValue);
            h = Mix(h, weldIgnore.PackedValue);
            h = Mix(h, (long)priority);
            modes = workModes;
            grindColor = grind;
            weldIgnoreColor = weldIgnore;
            if (h != settingsSignature)
            {
                settingsSignature = h;
                entries.Clear();
            }
        }

        /// <summary>Forget every group (the full rescan).</summary>
        public void Flush()
        {
            entries.Clear();
        }

        /// <summary>
        /// The signature of a grid group (its key: the lowest grid id). 'refuse' tells grids the scan skips (ownership,
        /// working drones), which the signature includes. Also starts watching the group's grids.
        /// </summary>
        public ulong Signature(List<IMyCubeGrid> grids, bool projections, Func<IMyCubeGrid, string> refuse, out long key)
        {
            key = long.MaxValue;
            ulong sum = Seed;
            for (int i = 0; i < grids.Count; i++)
            {
                var g = grids[i];
                if (g.EntityId < key) key = g.EntityId;
                ulong h = Seed;
                h = Mix(h, g.EntityId);
                h = Mix(h, Track(g));
                h = Mix(h, (g.Physics == null ? 1 : 0) | (g.MarkedForClose ? 2 : 0) | (g.IsStatic ? 4 : 0));
                MatrixD m = g.WorldMatrix;
                h = MixPose(h, ref m);
                h = Mix(h, refuse != null && refuse(g) != null ? 1 : 0);
                if (projections)
                    foreach (var p in g.GetFatBlocks<IMyProjector>())
                        h = MixProjector(h, p);
                sum += h;   // the grids' order doesn't matter
            }
            return sum;
        }

        /// <summary>Targets remembered for the group, when its signature is unchanged (and it wasn't cut off at a lower limit).</summary>
        public bool TryGet(long key, ulong signature, int maxJobs, List<ConstructionTarget> into)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e) || e.Signature != signature) return false;
            if (e.Truncated && e.MaxJobs != maxJobs) return false;
            e.Seen = scanNo;
            int n = Math.Min(maxJobs, e.Targets.Count);
            for (int i = 0; i < n; i++) into.Add(e.Targets[i]);
            return true;
        }

        /// <summary>The group was scanned: 'result' from index 'first' on are its targets.</summary>
        public void Store(long key, ulong signature, int maxJobs, List<ConstructionTarget> result, int first)
        {
            Entry e;
            if (!entries.TryGetValue(key, out e)) { e = new Entry(); entries[key] = e; }
            e.Signature = signature;
            e.MaxJobs = maxJobs;
            e.Seen = scanNo;
            e.Targets.Clear();
            for (int i = first; i < result.Count; i++) e.Targets.Add(result[i]);
            e.Truncated = e.Targets.Count >= maxJobs;
        }

        /// <summary>
        /// The scan is over: groups and grids it didn't reach are forgotten, and the grids learn which cells are targets.
        /// </summary>
        public void End(List<ConstructionTarget> targets)
        {
            removeBuffer.Clear();
            foreach (var kv in entries) if (kv.Value.Seen != scanNo) removeBuffer.Add(kv.Key);
            for (int i = 0; i < removeBuffer.Count; i++) entries.Remove(removeBuffer[i]);
            removeBuffer.Clear();
            foreach (var kv in watched) if (kv.Value.LastSeen != scanNo) removeBuffer.Add(kv.Key);
            for (int i = 0; i < removeBuffer.Count; i++) Untrack(removeBuffer[i]);
            removeBuffer.Clear();
            foreach (var kv in watched) kv.Value.Known.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                Watched w;
                if (watched.TryGetValue(targets[i].GridEntityId, out w)) w.Known.Add(targets[i].Cell);
            }
        }

        /// <summary>Something happened on this grid that the events may not have told (a job on it ended): its groups scan again.</summary>
        public void Touch(long gridId)
        {
            Watched w;
            if (watched.TryGetValue(gridId, out w)) w.Version++;
        }

        /// <summary>Stop watching everything (the block closed).</summary>
        public void Dispose()
        {
            foreach (var kv in watched) kv.Value.Unsubscribe();
            watched.Clear();
            entries.Clear();
        }

        // Starts watching the grid (first time); its event version
        private int Track(IMyCubeGrid grid)
        {
            Watched w;
            if (!watched.TryGetValue(grid.EntityId, out w))
            {
                w = new Watched { Grid = grid, Owner = this };
                w.Subscribe();
                watched[grid.EntityId] = w;
            }
            w.LastSeen = scanNo;
            return w.Version;
        }

        private void Untrack(long id)
        {
            Watched w;
            if (!watched.TryGetValue(id, out w)) return;
            w.Unsubscribe();
            watched.Remove(id);
        }

        #region Hashing
        private const ulong Seed = 1469598103934665603UL;

        private static ulong Mix(ulong h, long v)
        {
            return unchecked((h ^ (ulong)v) * 1099511628211UL);
        }

        private static long Quantize(double v, double step)
        {
            return (long)Math.Round(v * step);
        }

        private static ulong MixPose(ulong h, ref MatrixD m)
        {
            h = Mix(h, Quantize(m.Translation.X, POSITION_STEP));
            h = Mix(h, Quantize(m.Translation.Y, POSITION_STEP));
            h = Mix(h, Quantize(m.Translation.Z, POSITION_STEP));
            h = Mix(h, Quantize(m.Forward.X, ORIENTATION_STEP));
            h = Mix(h, Quantize(m.Forward.Y, ORIENTATION_STEP));
            h = Mix(h, Quantize(m.Forward.Z, ORIENTATION_STEP));
            h = Mix(h, Quantize(m.Up.X, ORIENTATION_STEP));
            h = Mix(h, Quantize(m.Up.Y, ORIENTATION_STEP));
            h = Mix(h, Quantize(m.Up.Z, ORIENTATION_STEP));
            return h;
        }

        // The projector's own report (what its terminal shows): block counts, buildable now, offset / rotation, what it projects
        private static ulong MixProjector(ulong h, IMyProjector p)
        {
            h = Mix(h, p.EntityId);
            h = Mix(h, (p.IsWorking ? 1 : 0) | (p.IsProjecting ? 2 : 0));
            if (!p.IsProjecting) return h;
            h = Mix(h, p.TotalBlocks);
            h = Mix(h, p.RemainingBlocks);
            h = Mix(h, p.RemainingArmorBlocks);
            h = Mix(h, p.BuildableBlocksCount);
            Vector3I v = p.ProjectionOffset;
            h = Mix(h, v.X); h = Mix(h, v.Y); h = Mix(h, v.Z);
            v = p.ProjectionRotation;
            h = Mix(h, v.X); h = Mix(h, v.Y); h = Mix(h, v.Z);
            var projected = p.ProjectedGrid;
            if (projected != null)
            {
                h = Mix(h, projected.EntityId);
                MatrixD m = projected.WorldMatrix;
                h = MixPose(h, ref m);
            }
            return h;
        }
        #endregion
    }
}
