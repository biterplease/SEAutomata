using System;
using System.Collections.Generic;

using VRage.Game.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// The drone's real shape, for weld work sites (the way in between built neighbours): its blocks - a fat block by its
    /// model's box, any other by its cells - in the controller frame, rasterized at half the smallest cell size and merged
    /// into at most HULL_SHAPE_MAX_BOXES boxes (exact runs first, then the pairs that add the least empty space). A welder
    /// snout fits where the hull's bounding box doesn't. Routes, turns and every other check keep the bounding box.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const int HULL_SHAPE_MAX_BOXES = 24;
        private const int HULL_SHAPE_MAX_BLOCKS = 1500;   // more: the bounding box only
        private const int HULL_SHAPE_MAX_CELLS = 96;      // per axis; a coarser raster beyond

        private readonly List<BoundingBoxD> hullShape = new List<BoundingBoxD>();   // controller frame
        private long hullShapeSignature = long.MinValue;
        private int hullShapeFrame = int.MinValue;
        private readonly List<IMySlimBlock> shapeBlocks = new List<IMySlimBlock>();
        private readonly List<BoundingBoxD> shapeRaw = new List<BoundingBoxD>();

        /// <summary>The shape boxes (controller frame), rebuilt when the drone's blocks changed; empty: not available.</summary>
        private List<BoundingBoxD> HullShape()
        {
            int now = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter;
            if (now == hullShapeFrame) return hullShape;   // checked this tick already (many sites per choice)
            hullShapeFrame = now;
            RefreshDroneGrids();
            long sig = droneGrids.Count;
            int total = 0;
            for (int g = 0; g < droneGrids.Count; g++)
            {
                shapeBlocks.Clear();
                droneGrids[g].GetBlocks(shapeBlocks);
                total += shapeBlocks.Count;
                for (int i = 0; i < shapeBlocks.Count; i++)
                    sig = sig * 31 + shapeBlocks[i].Position.GetHashCode() + (shapeBlocks[i].FatBlock != null ? 7 : 0);
            }
            shapeBlocks.Clear();
            if (sig == hullShapeSignature) return hullShape;
            hullShapeSignature = sig;
            hullShape.Clear();
            if (total == 0 || total > HULL_SHAPE_MAX_BLOCKS) return hullShape;
            try { BuildHullShape(); }
            catch (Exception ex) { hullShape.Clear(); Log.Error("Drone {0}: hull shape: {1}", Entity.EntityId, ex); }
            return hullShape;
        }

        private void BuildHullShape()
        {
            CaptureFlightState();
            MatrixD worldToController = MatrixD.Invert(flightState.WorldMatrix);
            shapeRaw.Clear();
            BoundingBoxD all = BoundingBoxD.CreateInvalid();
            double cell = double.MaxValue;
            for (int g = 0; g < droneGrids.Count; g++)
            {
                var grid = droneGrids[g];
                double gs = grid.GridSize;
                cell = Math.Min(cell, gs);
                MatrixD gridToCtrl = grid.WorldMatrix * worldToController;
                shapeBlocks.Clear();
                grid.GetBlocks(shapeBlocks);
                for (int i = 0; i < shapeBlocks.Count; i++)
                {
                    var slim = shapeBlocks[i];
                    BoundingBoxD box;
                    var fat = slim.FatBlock;
                    if (fat != null && fat.Model != null)
                    {
                        BoundingBox mb = fat.Model.BoundingBox;
                        box = new BoundingBoxD(mb.Min, mb.Max).TransformFast(fat.WorldMatrix * worldToController);
                    }
                    else
                        box = new BoundingBoxD((Vector3D)slim.Min * gs - gs * 0.5, (Vector3D)slim.Max * gs + gs * 0.5).TransformFast(gridToCtrl);
                    shapeRaw.Add(box);
                    all = all.Include(ref box);
                }
            }
            shapeBlocks.Clear();
            if (shapeRaw.Count == 0) return;

            // Raster: a cell is solid when a block box covers more than a sliver of it (conservative, so snapped outward)
            double r = cell * 0.5;
            Vector3D size = all.Size;
            double maxAxis = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (maxAxis / r > HULL_SHAPE_MAX_CELLS) r = maxAxis / HULL_SHAPE_MAX_CELLS;
            int nx = Math.Max(1, (int)Math.Ceiling(size.X / r)), ny = Math.Max(1, (int)Math.Ceiling(size.Y / r)), nz = Math.Max(1, (int)Math.Ceiling(size.Z / r));
            var solid = new bool[nx, ny, nz];
            const double SLIVER = 0.05;
            for (int k = 0; k < shapeRaw.Count; k++)
            {
                var b = shapeRaw[k];
                int x0 = Clamp((int)Math.Floor((b.Min.X - all.Min.X) / r + SLIVER), nx), x1 = Clamp((int)Math.Ceiling((b.Max.X - all.Min.X) / r - SLIVER) - 1, nx);
                int y0 = Clamp((int)Math.Floor((b.Min.Y - all.Min.Y) / r + SLIVER), ny), y1 = Clamp((int)Math.Ceiling((b.Max.Y - all.Min.Y) / r - SLIVER) - 1, ny);
                int z0 = Clamp((int)Math.Floor((b.Min.Z - all.Min.Z) / r + SLIVER), nz), z1 = Clamp((int)Math.Ceiling((b.Max.Z - all.Min.Z) / r - SLIVER) - 1, nz);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                            solid[x, y, z] = true;
            }
            shapeRaw.Clear();

            // Exact runs: x, then y, then z
            var used = new bool[nx, ny, nz];
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (!solid[x, y, z] || used[x, y, z]) continue;
                        int x1 = x;
                        while (x1 + 1 < nx && solid[x1 + 1, y, z] && !used[x1 + 1, y, z]) x1++;
                        int y1 = y;
                        while (y1 + 1 < ny && RowFree(solid, used, x, x1, y1 + 1, z)) y1++;
                        int z1 = z;
                        while (z1 + 1 < nz && SlabFree(solid, used, x, x1, y, y1, z1 + 1)) z1++;
                        for (int a = x; a <= x1; a++)
                            for (int b = y; b <= y1; b++)
                                for (int c = z; c <= z1; c++)
                                    used[a, b, c] = true;
                        shapeRaw.Add(new BoundingBoxD(all.Min + new Vector3D(x, y, z) * r, all.Min + new Vector3D(x1 + 1, y1 + 1, z1 + 1) * r));
                    }

            // At most HULL_SHAPE_MAX_BOXES: merge the pair whose union adds the least empty space
            while (shapeRaw.Count > HULL_SHAPE_MAX_BOXES)
            {
                int bi = 0, bj = 1;
                double best = double.MaxValue;
                for (int i = 0; i < shapeRaw.Count; i++)
                    for (int j = i + 1; j < shapeRaw.Count; j++)
                    {
                        var u = shapeRaw[i];
                        var bj2 = shapeRaw[j];
                        u = u.Include(ref bj2);
                        double extra = u.Volume - shapeRaw[i].Volume - shapeRaw[j].Volume;
                        if (extra < best) { best = extra; bi = i; bj = j; }
                    }
                var m = shapeRaw[bi];
                var o = shapeRaw[bj];
                shapeRaw[bi] = m.Include(ref o);
                shapeRaw.RemoveAt(bj);
            }
            // Biggest first: the body is what usually hits (checks stop at the first box in the way)
            shapeRaw.Sort((x, y) => y.Volume.CompareTo(x.Volume));
            hullShape.AddRange(shapeRaw);
            shapeRaw.Clear();
            double vol = 0;
            for (int i = 0; i < hullShape.Count; i++) vol += hullShape[i].Volume;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: shape for weld sites: {1} boxes, {2:F1} m³ of its {3:F1} m³ bounding box", Entity.EntityId,
                      hullShape.Count, vol, localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.Volume : all.Volume);
        }

        private static int Clamp(int v, int n) { return v < 0 ? 0 : v >= n ? n - 1 : v; }

        private static bool RowFree(bool[,,] solid, bool[,,] used, int x0, int x1, int y, int z)
        {
            for (int x = x0; x <= x1; x++) if (!solid[x, y, z] || used[x, y, z]) return false;
            return true;
        }

        private static bool SlabFree(bool[,,] solid, bool[,,] used, int x0, int x1, int y0, int y1, int z)
        {
            for (int y = y0; y <= y1; y++) if (!RowFree(solid, used, x0, x1, y, z)) return false;
            return true;
        }
    }
}
