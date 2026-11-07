using System;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Asteroid rock in a volume (a sphere, an oriented box), read from the voxel data: no physics shapes (rays miss rock
    /// whose shapes the game hasn't built yet, and start inside it), no gaps between rays. Used by the fit checks
    /// (HullSpaceFree / BoxFree: where the hull goes or turns) and as the cheap "no rock here" test before the ray bundle.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private const int ROCK_PROBE_VOXELS = 1024;   // voxels read per probe at most: the LOD is coarsened to stay under it
        private const int ROCK_PROBE_LOD_MAX = 3;      // 8 m voxels: a box still too big is "rock" when the storage says it may be
        private readonly MyStorageData rockData = new MyStorageData(MyStorageDataTypeFlags.Content);
        private const int ROCK_RADIUS_LOD = 2;        // 4 m voxels: an asteroid's outer radius along a direction

        /// <summary>
        /// Outer radius of asteroid 'v's rock along 'dir' from 'c' (any rock in a 4 m voxel counts): walked in from 'max',
        /// so tunnels and caves don't count. 0: none.
        /// </summary>
        private double RockRadius(MyVoxelBase v, Vector3D c, Vector3D dir, double max)
        {
            if (v == null || v.Closed || v.Storage == null) return 0;
            VRage.ModAPI.IMyStorage storage = v.Storage;
            Vector3D origin = v.PositionLeftBottomCorner - (Vector3D)v.StorageMin;
            double vs = 1 << ROCK_RADIUS_LOD;
            Vector3I size = storage.Size >> ROCK_RADIUS_LOD;
            rockData.Resize(Vector3I.One);
            for (double t = max; t > 0; t -= vs)
            {
                Vector3I cell = Vector3I.Floor((c + dir * t - origin) / vs);
                if (cell.X < 0 || cell.Y < 0 || cell.Z < 0 || cell.X >= size.X || cell.Y >= size.Y || cell.Z >= size.Z) continue;
                storage.ReadRange(rockData, MyStorageDataTypeFlags.Content, ROCK_RADIUS_LOD, cell, cell);
                if (rockData.Content(0) > 0) return t + vs;
            }
            return 0;
        }

        /// <summary>Outer radius of asteroid 'v's rock along 'dir' from 'c' (FlightLanes: where a lane's end goes).</summary>
        public double AsteroidRockRadius(MyVoxelBase v, Vector3D c, Vector3D dir, double max)
        {
            return RockRadius(v, c, dir, max);
        }

        /// <summary>Rock of asteroid 'v' within 'r' of 'c' (FlightLanes: lane ends clear of it).</summary>
        public bool RockNear(MyVoxelBase v, Vector3D c, double r)
        {
            return RockInSphere(v, c, r);
        }

        /// <summary>Rock of asteroid 'v' within 'r' of 'c' (a voxel counts when its centre is within r + half a voxel).</summary>
        private bool RockInSphere(MyVoxelBase v, Vector3D c, double r)
        {
            var wb = new BoundingBoxD(c - new Vector3D(r), c + new Vector3D(r));
            var none = default(MyOrientedBoundingBoxD);
            return RockIn(v, ref wb, c, r, ref none, true);
        }

        /// <summary>Rock of asteroid 'v' inside the oriented box (grown by half a voxel).</summary>
        private bool RockInBox(MyVoxelBase v, ref MyOrientedBoundingBoxD box)
        {
            BoundingBoxD wb = box.GetAABB();
            return RockIn(v, ref wb, box.Center, 0, ref box, false);
        }

        /// <summary>
        /// The storage's own test (Disjoint: no rock in the world box 'wb'), without reading voxels. Procedural asteroids
        /// answer Intersects across their whole surface band, so "maybe" is all a non-Disjoint answer means.
        /// </summary>
        private static bool RockPossible(MyVoxelBase v, ref BoundingBoxD wb)
        {
            if (v == null || v.Closed || v.Storage == null || !IsAsteroid(v)) return false;
            if (!v.PositionComp.WorldAABB.Intersects(wb)) return false;
            Vector3D origin = v.PositionLeftBottomCorner - (Vector3D)v.StorageMin;
            var sb = new BoundingBox((Vector3)(wb.Min - origin), (Vector3)(wb.Max - origin));
            return v.Storage.Intersect(ref sb, true) != ContainmentType.Disjoint;
        }

        private bool RockIn(MyVoxelBase v, ref BoundingBoxD wb, Vector3D c, double r, ref MyOrientedBoundingBoxD box, bool sphere)
        {
            if (!RockPossible(v, ref wb)) return false;
            VRage.ModAPI.IMyStorage storage = v.Storage;   // (the ModAPI interface: the game's own IMyStorage is prohibited)
            // Storage voxel j covers [j, j + 1) m from 'origin' (asteroids aren't rotated)
            Vector3D origin = v.PositionLeftBottomCorner - (Vector3D)v.StorageMin;
            Vector3I size = storage.Size;
            Vector3I lo = Vector3I.Max(Vector3I.Floor(wb.Min - origin), Vector3I.Zero);
            Vector3I hi = Vector3I.Min(Vector3I.Floor(wb.Max - origin), size - 1);
            if (hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z) return false;
            int lod = 0;
            Vector3I lmin = lo, lmax = hi, n = hi - lo + 1;
            while ((long)n.X * n.Y * n.Z > ROCK_PROBE_VOXELS && lod < ROCK_PROBE_LOD_MAX)
            {
                lod++;
                lmin = lo >> lod;
                lmax = hi >> lod;
                n = lmax - lmin + 1;
            }
            if ((long)n.X * n.Y * n.Z > ROCK_PROBE_VOXELS * 8) return true;   // too big to read: the storage's "maybe" stands
            rockData.Resize(n);
            storage.ReadRange(rockData, MyStorageDataTypeFlags.Content, lod, lmin, lmax);
            double vs = 1 << lod;
            double grow = vs * 0.5;
            double reach2 = (r + grow) * (r + grow);
            Vector3D half = sphere ? Vector3D.Zero : box.HalfExtent + new Vector3D(grow);
            Quaternion inv = sphere ? Quaternion.Identity : Quaternion.Inverse(box.Orientation);
            int i = 0;
            for (int z = 0; z < n.Z; z++)
                for (int y = 0; y < n.Y; y++)
                    for (int x = 0; x < n.X; x++, i++)
                    {
                        if (rockData.Content(i) <= 127) continue;
                        Vector3D p = origin + new Vector3D(lmin.X + x + 0.5, lmin.Y + y + 0.5, lmin.Z + z + 0.5) * vs;
                        if (sphere)
                        {
                            if (Vector3D.DistanceSquared(p, c) <= reach2) return true;
                            continue;
                        }
                        Vector3D l = Vector3D.Transform(p - c, inv);
                        if (Math.Abs(l.X) <= half.X && Math.Abs(l.Y) <= half.Y && Math.Abs(l.Z) <= half.Z) return true;
                    }
            return false;
        }
    }
}
