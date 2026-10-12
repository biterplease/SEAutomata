using System;
using System.Collections.Generic;

using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ModAPI;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;

using Automata.Inventory;

namespace Automata.Mining
{
    /// <summary>One ore found by a scan: the averaged position of one ore's voxels in one 32 m cell.</summary>
    public struct OreHit
    {
        /// <summary>Inventory key of the ore item ("Ore/Iron").</summary>
        public MyStringHash Ore;
        public Vector3D Position;
        /// <summary>Ore voxels counted at LOD 2 (4 m each): a rough size.</summary>
        public int Voxels;
        public long VoxelId;
    }

    /// <summary>
    /// What a vanilla ore detector marks, read straight from the voxel storage (ModAPI's ore detector has no markers):
    /// 32 m cells (8 voxels of LOD 2) inside a sphere; a cell with solid voxels has its material read with precise ore
    /// positions; each rare material's voxels give one hit at their average position. Spread over updates by a cell
    /// budget (main thread). Only voxels: grids are never looked at.
    /// </summary>
    public sealed class OreScanner
    {
        private const int CELL_SHIFT = 5;          // LOD 0 voxels per cell: 32
        private const int CELL_LOD2 = 8;           // LOD 2 voxels per cell side
        private const double CELL_HALF_DIAGONAL = 27.8;

        // Material index -> ore key (NullOrEmpty: not an ore); filled lazily
        private static readonly MyStringHash[] oreOfMaterial = new MyStringHash[256];
        private static readonly bool[] materialKnown = new bool[256];

        private sealed class MapScan
        {
            public IMyVoxelBase Map;
            public IMyStorage Storage;
            public Vector3D Corner;      // world position of voxel (0,0,0)
            public Vector3I Min, Max, Cursor;
        }

        private readonly List<MapScan> maps = new List<MapScan>();
        private readonly List<IMyVoxelBase> found = new List<IMyVoxelBase>();
        private readonly Dictionary<IMyStorage, IMyVoxelBase> byStorage = new Dictionary<IMyStorage, IMyVoxelBase>();
        private readonly MyStorageData cache = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
        private readonly Dictionary<MyStringHash, Vector3I> sums = new Dictionary<MyStringHash, Vector3I>(MyStringHash.Comparer);
        private readonly Dictionary<MyStringHash, int> counts = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
        private readonly List<MyStringHash> keys = new List<MyStringHash>();
        private int mapIndex;

        public Vector3D Center { get; private set; }
        public double Radius { get; private set; }
        public bool Running { get; private set; }
        public DateTime Started { get; private set; }
        /// <summary>Cells read in the current scan (solid or not).</summary>
        public int CellsRead { get; private set; }

        /// <summary>Starts a scan of the sphere (any running one is dropped). False when no voxel map is near.</summary>
        public bool Begin(Vector3D center, double radius)
        {
            maps.Clear();
            Running = false;
            Center = center;
            Radius = radius;
            Started = DateTime.UtcNow;
            CellsRead = 0;
            mapIndex = 0;
            var box = new BoundingBoxD(center - radius, center + radius);
            found.Clear();
            byStorage.Clear();
            MyAPIGateway.Session.VoxelMaps.GetInstances(found, v => v != null && v.Storage != null && v.PositionComp != null
                                                                    && v.PositionComp.WorldAABB.Intersects(box));
            // A planet's physics chunks share its storage: keep the instance that covers the whole storage
            for (int i = 0; i < found.Count; i++)
            {
                var v = found[i];
                IMyVoxelBase other;
                if (byStorage.TryGetValue(v.Storage, out other) && other.PositionComp.LocalAABB.Volume() >= v.PositionComp.LocalAABB.Volume()) continue;
                byStorage[v.Storage] = v;
            }
            foreach (var v in byStorage.Values)
            {
                var size = v.Storage.Size;
                Vector3D corner = v.PositionLeftBottomCorner;
                Vector3I lo = Vector3I.Floor(center - radius - corner), hi = Vector3I.Floor(center + radius - corner);
                lo = Vector3I.Max(lo, Vector3I.Zero);
                hi = Vector3I.Min(hi, size - 1);
                if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) continue;
                var m = new MapScan { Map = v, Storage = v.Storage, Corner = corner, Min = lo >> CELL_SHIFT, Max = hi >> CELL_SHIFT };
                m.Cursor = m.Min;
                maps.Add(m);
            }
            found.Clear();
            byStorage.Clear();
            Running = maps.Count > 0;
            return Running;
        }

        public void Cancel()
        {
            maps.Clear();
            Running = false;
        }

        /// <summary>Reads up to 'cellBudget' cells; hits go into 'hits'. True when the scan is complete.</summary>
        public bool Step(int cellBudget, List<OreHit> hits)
        {
            if (!Running) return true;
            int read = 0;
            while (mapIndex < maps.Count)
            {
                var m = maps[mapIndex];
                if (m.Map.Closed || m.Map.MarkedForClose) { mapIndex++; continue; }
                while (m.Cursor.Z <= m.Max.Z)
                {
                    if (read >= cellBudget) return false;
                    var cell = m.Cursor;
                    // advance x, y, z
                    m.Cursor.X++;
                    if (m.Cursor.X > m.Max.X) { m.Cursor.X = m.Min.X; m.Cursor.Y++; if (m.Cursor.Y > m.Max.Y) { m.Cursor.Y = m.Min.Y; m.Cursor.Z++; } }
                    Vector3D cellCenter = m.Corner + (Vector3D)(cell << CELL_SHIFT) + 16;
                    double reach = Radius + CELL_HALF_DIAGONAL;
                    if (Vector3D.DistanceSquared(cellCenter, Center) > reach * reach) continue;
                    read++;
                    CellsRead++;
                    ReadCell(m, cell, hits);
                }
                mapIndex++;
            }
            Running = false;
            maps.Clear();
            return true;
        }

        /// <summary>Whole scan at once (small spheres: a job site). Stops at 'maxCells'; false if it had to.</summary>
        public bool RunAll(Vector3D center, double radius, List<OreHit> hits, int maxCells)
        {
            if (!Begin(center, radius)) return true;
            return Step(maxCells, hits);
        }

        private void ReadCell(MapScan m, Vector3I cell, List<OreHit> hits)
        {
            Vector3I lo = cell * CELL_LOD2, hi = lo + (CELL_LOD2 - 1);
            cache.Resize(new Vector3I(CELL_LOD2));
            m.Storage.ReadRange(cache, MyStorageDataTypeFlags.Content, 2, lo, hi);
            int n = cache.SizeLinear;
            bool solid = false;
            for (int i = 0; i < n && !solid; i++) solid = cache.Content(i) > 127;
            if (!solid) return;
            var flags = MyVoxelRequestFlags.PreciseOrePositions;
            m.Storage.ReadRange(cache, MyStorageDataTypeFlags.Material, 2, lo, hi, ref flags);
            sums.Clear();
            counts.Clear();
            keys.Clear();
            for (int i = 0; i < n; i++)
            {
                if (cache.Content(i) <= 127) continue;
                var ore = OreOf(cache.Material(i));
                if (ore == MyStringHash.NullOrEmpty) continue;
                Vector3I p;
                cache.ComputePosition(i, out p);
                Vector3I sum;
                if (!sums.TryGetValue(ore, out sum)) { keys.Add(ore); counts[ore] = 0; }
                sums[ore] = sum + p;
                counts[ore]++;
            }
            for (int k = 0; k < keys.Count; k++)
            {
                var ore = keys[k];
                int c = counts[ore];
                Vector3D avg = (Vector3D)sums[ore] / c;
                Vector3D local = (avg + lo) * 4 + 2;   // LOD 2 voxel -> LOD 0 metres, voxel centre
                Vector3D world = m.Corner + local;
                if (Vector3D.DistanceSquared(world, Center) > Radius * Radius) continue;
                hits.Add(new OreHit { Ore = ore, Position = world, Voxels = c, VoxelId = m.Map.EntityId });
            }
        }

        private static readonly MyStorageData nearCache = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
        private static readonly List<IMyVoxelBase> nearMaps = new List<IMyVoxelBase>();
        private static readonly Dictionary<IMyStorage, IMyVoxelBase> nearByStorage = new Dictionary<IMyStorage, IMyVoxelBase>();
        private const int NEAR_MAX_SIDE = 24;   // LOD 1 voxels per side read at most (48 m)

        /// <summary>
        /// The ore detector close up: the nearest voxel of 'ore' to 'point' within 'radius' (LOD 1, 2 m voxels; at most
        /// NEAR_MAX_SIDE per side). False when there is none.
        /// </summary>
        public static bool NearestOre(Vector3D point, double radius, MyStringHash ore, out Vector3D at, out double distance)
        {
            at = point;
            distance = double.MaxValue;
            if (ore == MyStringHash.NullOrEmpty) return false;
            var box = new BoundingBoxD(point - radius, point + radius);
            nearMaps.Clear();
            nearByStorage.Clear();
            MyAPIGateway.Session.VoxelMaps.GetInstances(nearMaps, v => v != null && v.Storage != null && v.PositionComp != null
                                                                       && v.PositionComp.WorldAABB.Intersects(box));
            for (int i = 0; i < nearMaps.Count; i++)
            {
                var v = nearMaps[i];
                IMyVoxelBase other;
                if (nearByStorage.TryGetValue(v.Storage, out other) && other.PositionComp.LocalAABB.Volume() >= v.PositionComp.LocalAABB.Volume()) continue;
                nearByStorage[v.Storage] = v;
            }
            nearMaps.Clear();
            double best = double.MaxValue;
            foreach (var v in nearByStorage.Values)
            {
                Vector3D corner = v.PositionLeftBottomCorner;
                Vector3I size1 = v.Storage.Size >> 1;
                Vector3I lo = Vector3I.Floor((point - radius - corner) * 0.5), hi = Vector3I.Floor((point + radius - corner) * 0.5);
                lo = Vector3I.Max(lo, Vector3I.Zero);
                hi = Vector3I.Min(Vector3I.Min(hi, size1 - 1), lo + (NEAR_MAX_SIDE - 1));
                if (lo.X > hi.X || lo.Y > hi.Y || lo.Z > hi.Z) continue;
                nearCache.Resize(hi - lo + 1);
                v.Storage.ReadRange(nearCache, MyStorageDataTypeFlags.Content, 1, lo, hi);
                int n = nearCache.SizeLinear;
                bool solid = false;
                for (int i = 0; i < n && !solid; i++) solid = nearCache.Content(i) > 127;
                if (!solid) continue;
                var flags = MyVoxelRequestFlags.PreciseOrePositions;
                v.Storage.ReadRange(nearCache, MyStorageDataTypeFlags.Material, 1, lo, hi, ref flags);
                for (int i = 0; i < n; i++)
                {
                    if (nearCache.Content(i) <= 127 || OreOf(nearCache.Material(i)) != ore) continue;
                    Vector3I p;
                    nearCache.ComputePosition(i, out p);
                    Vector3D world = corner + (Vector3D)(p + lo) * 2 + 1;   // LOD 1 voxel -> LOD 0 metres, voxel centre
                    double d2 = Vector3D.DistanceSquared(world, point);
                    if (d2 < best) { best = d2; at = world; }
                }
            }
            nearByStorage.Clear();
            if (best > radius * radius) return false;
            distance = Math.Sqrt(best);
            return true;
        }

        private static readonly MyStorageData probe = new MyStorageData(MyStorageDataTypeFlags.Content);

        /// <summary>
        /// Where a bore from 'inside' going 'outward' leaves the voxel map: the first of three solid-free samples 2 m
        /// apart (a cave or the surface), or 'minFree' metres of it when asked for more (a bore line leaving a pit, a
        /// shaft or a cave: the open air of the pit isn't the surface). False when still solid after 'maxDistance'.
        /// </summary>
        public static bool FindSurface(IMyVoxelBase v, Vector3D inside, Vector3D outward, double maxDistance, out Vector3D surface, double minFree = 6)
        {
            surface = inside;
            if (v == null || v.Storage == null) return false;
            var storage = v.Storage;
            var size = storage.Size;
            Vector3D corner = v.PositionLeftBottomCorner;
            probe.Resize(Vector3I.One);
            int air = 0, need = Math.Max(3, (int)Math.Ceiling(minFree / 2));
            Vector3D firstAir = inside;
            for (double t = 0; t <= maxDistance; t += 2)
            {
                Vector3D p = inside + outward * t;
                Vector3I local = Vector3I.Floor(p - corner);
                bool solid = false;
                if (local.X >= 0 && local.Y >= 0 && local.Z >= 0 && local.X < size.X && local.Y < size.Y && local.Z < size.Z)
                {
                    storage.ReadRange(probe, MyStorageDataTypeFlags.Content, 0, local, local);
                    solid = probe.Content(0) > 127;
                }
                if (solid) { air = 0; continue; }
                if (air == 0) firstAir = p;
                if (++air >= need) { surface = firstAir; return true; }
            }
            return false;
        }

        /// <summary>The ore item key a voxel material gives when mined, if it is an ore the detectors mark (rare).</summary>
        public static MyStringHash OreOf(byte material)
        {
            if (materialKnown[material]) return oreOfMaterial[material];
            materialKnown[material] = true;
            MyVoxelMaterialDefinition def = null;
            try { def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(material); }
            catch (Exception) { }
            oreOfMaterial[material] = def != null && def.IsRare && !string.IsNullOrEmpty(def.MinedOre)
                ? ItemKeys.Of("MyObjectBuilder_Ore", def.MinedOre) : MyStringHash.NullOrEmpty;
            return oreOfMaterial[material];
        }

        /// <summary>Material table again (definitions reloaded, e.g. a new session in the same process).</summary>
        public static void ResetMaterials()
        {
            Array.Clear(materialKnown, 0, materialKnown.Length);
        }
    }
}
