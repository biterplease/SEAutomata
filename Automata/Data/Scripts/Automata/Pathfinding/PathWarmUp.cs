using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Construction;
using Automata.Logistics;
using Automata.Mining;
using Automata.Orchestrator;
using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Finds the areas expected to be busy and has the search queue prepare their scene (obstacle entries, block
    /// trees: ~1 us per block, a big station ~10 ms the first time) before the first route is planned there, in the
    /// small slices the queue spends on warm-ups (<see cref="PathSearchQueue.WarmUp"/>). Server, main thread.
    ///  - Static grids with Automata blocks (Orchestrator, Logistics Computer, Construction Computer, Mining
    ///    Surveyor): the grid and GRID_MARGIN around it (docking, departures, the lanes round the station).
    ///  - PROJECTOR_RADIUS around the projectors of those grids (the build site and what it is built onto).
    ///  - The observation area of the Construction Computers on them, AREA_MARGIN round (where the drones work).
    /// Mobile grids are left to the first search that reaches them. An area is prepared again every
    /// REFRESH_TICKS (that keeps its entries from going stale, LocalScene) and when it changed.
    /// </summary>
    internal sealed class PathWarmUp
    {
        #region Timing
        private const int SCAN_TICKS = 10 * 60;            // look for areas this often
        private const int REFRESH_TICKS = 5 * 60 * 60;     // an area is prepared again after this
        #endregion
        private const double GRID_MARGIN = 60;
        private const double PROJECTOR_RADIUS = 50;
        private const double AREA_MARGIN = 30;
        private const double MOVED = 5;                    // m: an area that moved or grew this much is prepared again
        private const int PENDING_MAX = 8;                 // no new areas while this many wait

        private struct Done
        {
            public BoundingBoxD Region;
            public int Frame;
        }

        private readonly Dictionary<long, Done> warmed = new Dictionary<long, Done>();
        private readonly HashSet<IMyCubeGrid> grids = new HashSet<IMyCubeGrid>();
        private readonly List<long> stale = new List<long>();
        private int lastScan = int.MinValue / 2;

        public void Update(int now, PathSearchQueue queue)
        {
            if (now - lastScan < SCAN_TICKS) return;
            lastScan = now;
            if (queue.WarmPending > PENDING_MAX) return;
            grids.Clear();
            for (int i = 0; i < OrchestratorBlock.All.Count; i++) AddGrid(OrchestratorBlock.All[i].Entity);
            for (int i = 0; i < MiningSurveyorBlock.All.Count; i++) AddGrid(MiningSurveyorBlock.All[i].Entity);
            for (int i = 0; i < LogisticsComputerBlock.All.Count; i++) AddGrid(LogisticsComputerBlock.All[i].Entity);
            for (int i = 0; i < ConstructionComputerBlock.All.Count; i++) AddGrid(ConstructionComputerBlock.All[i].Entity);
            foreach (var grid in grids)
            {
                Offer(queue, now, grid.EntityId, grid.WorldAABB.GetInflated(GRID_MARGIN));
                var projectors = ProjectorsOf(grid);
                for (int i = 0; i < projectors.Count; i++)
                {
                    var p = projectors[i];
                    if (p.Closed) continue;
                    Offer(queue, now, p.EntityId, BoundingBoxD.CreateFromSphere(new BoundingSphereD(p.GetPosition(), PROJECTOR_RADIUS)));
                }
            }
            for (int i = 0; i < ConstructionComputerBlock.All.Count; i++)
            {
                var cc = ConstructionComputerBlock.All[i];
                var grid = GridOf(cc.Entity);
                if (grid == null) continue;
                MatrixD m;
                Vector3D half;
                cc.GetArea(out m, out half);
                var obb = new MyOrientedBoundingBoxD(m.Translation, half, Quaternion.CreateFromRotationMatrix(m));
                Offer(queue, now, cc.Entity.EntityId, obb.GetAABB().GetInflated(AREA_MARGIN));
            }
            // Areas of things that are gone
            stale.Clear();
            foreach (var kv in warmed)
                if (now - kv.Value.Frame > 2 * REFRESH_TICKS) stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++) warmed.Remove(stale[i]);
            goneGrids.Clear();
            foreach (var kv in projectorLists) if (kv.Value.Seen != lastScan) goneGrids.Add(kv.Key);
            for (int i = 0; i < goneGrids.Count; i++) projectorLists.Remove(goneGrids[i]);
        }

        // The block's grid when it is static (a base, a platform, a station); mobile grids are not warmed
        private static IMyCubeGrid GridOf(VRage.ModAPI.IMyEntity block)
        {
            var b = block as IMyCubeBlock;
            var grid = b != null ? b.CubeGrid : null;
            return grid != null && grid.Physics != null && grid.IsStatic && !grid.MarkedForClose ? grid : null;
        }

        private void AddGrid(VRage.ModAPI.IMyEntity block)
        {
            var grid = GridOf(block);
            if (grid != null) grids.Add(grid);
        }

        // The grid's projectors, listed again only when its block count changed (a station's fat blocks are many)
        private sealed class ProjectorList
        {
            public int Blocks = -1, Seen;
            public readonly List<IMyProjector> List = new List<IMyProjector>();
        }
        private readonly Dictionary<long, ProjectorList> projectorLists = new Dictionary<long, ProjectorList>();
        private readonly List<long> goneGrids = new List<long>();

        private List<IMyProjector> ProjectorsOf(IMyCubeGrid grid)
        {
            ProjectorList pl;
            if (!projectorLists.TryGetValue(grid.EntityId, out pl)) { pl = new ProjectorList(); projectorLists[grid.EntityId] = pl; }
            var mg = grid as Sandbox.Game.Entities.MyCubeGrid;
            int count = mg != null ? mg.BlocksCount : -2;
            pl.Seen = lastScan;
            if (count == pl.Blocks && count != -2) return pl.List;
            pl.Blocks = count;
            pl.List.Clear();
            foreach (var p in grid.GetFatBlocks<IMyProjector>()) pl.List.Add(p);
            return pl.List;
        }

        private void Offer(PathSearchQueue queue, int now, long id, BoundingBoxD region)
        {
            Done d;
            if (warmed.TryGetValue(id, out d) && now - d.Frame < REFRESH_TICKS
                && Vector3D.DistanceSquared(d.Region.Center, region.Center) < MOVED * MOVED
                && Vector3D.DistanceSquared(d.Region.Max - d.Region.Min, region.Max - region.Min) < MOVED * MOVED) return;
            queue.WarmUp(region);
            warmed[id] = new Done { Region = region, Frame = now };
            if (Log.DebugEnabled) Log.Debug("Pathfinding: scene warm-up queued for {0}: {1:F0} x {2:F0} x {3:F0} m", id,
                      region.Max.X - region.Min.X, region.Max.Y - region.Min.Y, region.Max.Z - region.Min.Z);
        }
    }
}
