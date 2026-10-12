using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Orchestrator;

namespace Automata.Pointer
{
    /// <summary>
    /// Task lists of scout jobs (TODO/11), server side. Spot check: a standoff point off the surface, an ore scan. Area
    /// sweep: waypoints covering a 500 m disc around the pointed spot, split into N contiguous sectors (one per drone),
    /// an ore scan at each waypoint.
    ///  - Planet (natural gravity): a serpentine over the plane tangent to the surface, each waypoint dropped onto
    ///    the terrain below it and lifted ALTITUDE above it (low passes read deeper).
    ///  - Asteroid: ASTEROID_ORBITS low orbits (great circles through the pointed spot, tilts spread by the golden angle
    ///    from a random start, directions alternating), each waypoint dropped onto the real surface along the line from
    ///    the asteroid's centre and lifted a detector-dependent height above it. Orbits longer than ORBIT_ARC_MAX are cut
    ///    to an arc centred on the spot. Height / spacing from the scouts' detector range (the smallest tagged one, else
    ///    ASSUMED_SMALL_RANGE). Several scouts: orbits shared out round-robin.
    ///  - Open space: the serpentine, facing the player.
    /// Waypoint spacing assumes a 100 m detector range (1.4 x range: scans overlap); drones with longer range overlap more.
    /// </summary>
    public static class ScoutPlanner
    {
        public const double AREA_RADIUS = 500;
        public const double ASSUMED_RANGE = 100;
        public const double SPACING = ASSUMED_RANGE * 1.4;
        public const double ALTITUDE = 20;          // m above the surface (planets: following the terrain)
        public const double SPOT_STANDOFF = 10;     // m off the pointed spot, along its surface normal
        public const int ASTEROID_ORBITS = 4;
        public const double ASSUMED_SMALL_RANGE = 50;   // m: a small ore detector (no tagged scout to read the range from)
        public const double ORBIT_ARC_MAX = 1200;       // m of arc per orbit at most (large asteroids: centred on the spot)
        private const double GOLDEN_ANGLE = 2.39996322972865332;   // rad
        private static readonly Random random = new Random();

        public static List<Task> SpotCheck(Vector3D hit, Vector3D normal, Vector3D playerPos)
        {
            Vector3D off = normal.LengthSquared() > 0.5 ? Vector3D.Normalize(normal) : Vector3D.Normalize(playerPos - hit);
            Vector3D at = hit + off * SPOT_STANDOFF;
            return new List<Task> { Task.NavigateTo(0, at, at, Gravity(at)), Task.Survey() };
        }

        /// <summary>N sector task lists (each: NavigateTo + Survey per waypoint) covering the disc around 'center'.</summary>
        public static List<List<Task>> AreaSweep(Vector3D center, Vector3D playerPos, int sectors, out string how, double detectorRange = 0)
        {
            var points = new List<Vector3D>();
            float interference;
            Vector3 g = MyAPIGateway.Physics.CalculateNaturalGravityAt(center, out interference);
            IMyVoxelBase body = g.LengthSquared() > 0.01f ? null : NearestBody(center);
            if (g.LengthSquared() > 0.01f)
            {
                Vector3D up = -Vector3D.Normalize(g);
                Serpentine(center + up * ALTITUDE, up, points);
                int followed = FollowTerrain(points);
                how = string.Format("planet serpentine, {0:F0} m above the terrain ({1} waypoint(s) on the surface)", ALTITUDE, followed);
            }
            else if (body != null)
            {
                double range = detectorRange > 0 ? detectorRange : ASSUMED_SMALL_RANGE;
                double altitude = MathHelper.Clamp(range * 0.3, 8, 30);
                double footprint = Math.Sqrt(Math.Max(1, range * range - altitude * altitude));
                double spacing = Math.Max(15, footprint * 1.4);
                var orbits = Orbits(body, center, altitude, spacing);
                how = string.Format("asteroid {0}: {1} orbit(s), {2:F0} m above the surface, {3:F0} m apart ({4:F0} m detector)",
                                    body.StorageName ?? body.EntityId.ToString(), orbits.Count, altitude, spacing, range);
                return Share(orbits, sectors, ref how);
            }
            else
            {
                Vector3D facing = playerPos - center;
                Serpentine(center, facing.LengthSquared() > 1 ? Vector3D.Normalize(facing) : Vector3D.Up, points);
                how = "open space serpentine";
            }
            sectors = Math.Max(1, Math.Min(sectors, points.Count));
            var result = new List<List<Task>>();
            int per = (points.Count + sectors - 1) / sectors;
            for (int s = 0; s < sectors; s++)
            {
                var tasks = new List<Task>();
                for (int i = s * per; i < Math.Min(points.Count, (s + 1) * per); i++)
                {
                    tasks.Add(Task.NavigateTo(0, points[i], points[i], Gravity(points[i])));
                    tasks.Add(Task.Survey());
                }
                if (tasks.Count > 0) result.Add(tasks);
            }
            how += string.Format(", {0} waypoint(s) in {1} sector(s)", points.Count, result.Count);
            return result;
        }

        // Orbits shared out round-robin, one task list per scout (NavigateTo + Survey per waypoint)
        private static List<List<Task>> Share(List<List<Vector3D>> orbits, int sectors, ref string how)
        {
            sectors = Math.Max(1, Math.Min(sectors, orbits.Count));
            var result = new List<List<Task>>();
            int total = 0;
            for (int s = 0; s < sectors; s++)
            {
                var tasks = new List<Task>();
                for (int k = s; k < orbits.Count; k += sectors)
                    foreach (var p in orbits[k])
                    {
                        tasks.Add(Task.NavigateTo(0, p, p, Gravity(p)));
                        tasks.Add(Task.Survey());
                        total++;
                    }
                if (tasks.Count > 0) result.Add(tasks);
            }
            how += string.Format(", {0} waypoint(s) in {1} sector(s)", total, result.Count);
            return result;
        }

        /// <summary>
        /// Low orbits around 'body': great circles through the spot (axis centre -> spot), tilted about that axis by the
        /// golden angle from a random start, alternate ones flown the other way. Waypoints every 'spacing' along each,
        /// dropped onto the surface (along the line from the centre) and lifted 'altitude'. Long orbits: an arc of
        /// ORBIT_ARC_MAX centred on the spot.
        /// </summary>
        private static List<List<Vector3D>> Orbits(IMyVoxelBase body, Vector3D spot, double altitude, double spacing)
        {
            var orbits = new List<List<Vector3D>>();
            Vector3D c = body.PositionComp.WorldAABB.Center;
            Vector3D axis = spot - c;
            double r0 = axis.Normalize();
            if (r0 < 1) { axis = Vector3D.Up; r0 = 1; }
            Vector3D e1 = Vector3D.CalculatePerpendicularVector(axis), e2 = Vector3D.Cross(axis, e1);
            double start;
            lock (random) start = random.NextDouble() * Math.PI;
            double radius = r0 + altitude;
            double arc = Math.Min(2 * Math.PI, ORBIT_ARC_MAX / radius);   // rad of each orbit flown
            int n = Math.Max(3, (int)Math.Ceiling(arc * radius / spacing));
            double lastR = r0;
            for (int k = 0; k < ASTEROID_ORBITS; k++)
            {
                double tilt = start + k * GOLDEN_ANGLE;
                Vector3D side = e1 * Math.Cos(tilt) + e2 * Math.Sin(tilt);   // the orbit's plane holds axis and side
                bool reverse = k % 2 == 1;
                var orbit = new List<Vector3D>();
                bool full = arc >= 2 * Math.PI - 1e-6;
                int count = full ? n : n + 1;
                for (int i = 0; i < count; i++)
                {
                    // Full orbit: from the spot round and back; arc: from one end through the spot to the other
                    double f = (double)i / (full ? n : n);
                    double ang = full ? f * 2 * Math.PI : (f - 0.5) * arc;
                    if (reverse) ang = -ang;
                    Vector3D u = axis * Math.Cos(ang) + side * Math.Sin(ang);
                    double r;
                    if (!SurfaceRadius(body, c, u, lastR, out r)) r = lastR;
                    lastR = r;
                    orbit.Add(c + u * (r + altitude));
                }
                orbits.Add(orbit);
            }
            return orbits;
        }

        /// <summary>
        /// Where the line from 'centre' along 'u' leaves the asteroid for good near 'guess' (m from the centre): from the
        /// guess outward while solid, else inward to the first solid. False: no rock along it (a hollow, past an edge).
        /// </summary>
        private static bool SurfaceRadius(IMyVoxelBase v, Vector3D centre, Vector3D u, double guess, out double r)
        {
            r = guess;
            if (v == null || v.Storage == null) return false;
            BoundingBoxD box = v.PositionComp.WorldAABB;
            double max = box.HalfExtents.Length() * 2;
            const double step = 2;
            double t = MathHelperD.Clamp(guess, 0, max);
            if (Solid(v, centre + u * t))
            {
                while (t < max && Solid(v, centre + u * (t + step))) t += step;
                r = t + step;
                return true;
            }
            // Air at the guess: rock a little further out (the guess in a dip of the orbit): out to its far side
            for (double o = step; o <= 60 && t + o < max; o += step)
                if (Solid(v, centre + u * (t + o)))
                {
                    double tt = t + o;
                    while (tt < max && Solid(v, centre + u * (tt + step))) tt += step;
                    r = tt + step;
                    return true;
                }
            int misses = 0;
            while (t > 0)
            {
                t -= step;
                if (Solid(v, centre + u * t)) { r = t + step; return true; }
                if (++misses > max / step) break;
            }
            return false;
        }

        private static readonly VRage.Voxels.MyStorageData probe = new VRage.Voxels.MyStorageData(VRage.Voxels.MyStorageDataTypeFlags.Content);

        private static bool Solid(IMyVoxelBase v, Vector3D p)
        {
            var size = v.Storage.Size;
            Vector3I local = Vector3I.Floor(p - v.PositionLeftBottomCorner);
            if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= size.X || local.Y >= size.Y || local.Z >= size.Z) return false;
            probe.Resize(Vector3I.One);
            v.Storage.ReadRange(probe, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, local, local);
            return probe.Content(0) > 127;
        }

        // Rows across the disc in the plane through 'origin' perpendicular to 'normal', alternating direction
        private static void Serpentine(Vector3D origin, Vector3D normal, List<Vector3D> points)
        {
            Vector3D a = Vector3D.CalculatePerpendicularVector(normal);
            Vector3D b = Vector3D.Cross(normal, a);
            int row = 0;
            for (double y = -AREA_RADIUS + SPACING / 2; y < AREA_RADIUS; y += SPACING, row++)
            {
                double half = Math.Sqrt(Math.Max(0, AREA_RADIUS * AREA_RADIUS - y * y));
                var line = new List<Vector3D>();
                for (double x = -half + SPACING / 2; x < half; x += SPACING) line.Add(origin + a * x + b * y);
                if (line.Count == 0) line.Add(origin + b * y);
                if (row % 2 == 1) line.Reverse();
                points.AddRange(line);
            }
        }

        // Each point onto the planet's surface below it, then ALTITUDE up from there. Returns the points moved.
        private static int FollowTerrain(List<Vector3D> points)
        {
            if (points.Count == 0) return 0;
            Vector3D first = points[0];
            var planet = Sandbox.Game.Entities.MyGamePruningStructure.GetClosestPlanet(first);
            if (planet == null) return 0;
            Vector3D centre = planet.PositionComp.GetPosition();
            int moved = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3D p = points[i];
                Vector3D surface = planet.GetClosestSurfacePointGlobal(ref p);
                Vector3D up = surface - centre;
                if (up.LengthSquared() < 1) continue;
                points[i] = surface + Vector3D.Normalize(up) * ALTITUDE;
                moved++;
            }
            return moved;
        }

        private static readonly List<IMyVoxelBase> voxels = new List<IMyVoxelBase>();

        private static IMyVoxelBase NearestBody(Vector3D at)
        {
            voxels.Clear();
            var box = new BoundingBoxD(at - AREA_RADIUS, at + AREA_RADIUS);
            MyAPIGateway.Session.VoxelMaps.GetInstances(voxels, v => v != null && v.PositionComp != null && v.PositionComp.WorldAABB.Intersects(box));
            IMyVoxelBase best = null;
            double bestD = double.MaxValue;
            for (int i = 0; i < voxels.Count; i++)
            {
                double d = Vector3D.DistanceSquared(voxels[i].PositionComp.WorldAABB.Center, at);
                // planets' physics chunks share the planet's storage; the biggest box is the body itself
                if (best != null && voxels[i].Storage == best.Storage && voxels[i].PositionComp.LocalAABB.Volume() <= best.PositionComp.LocalAABB.Volume()) continue;
                if (d < bestD || (best != null && voxels[i].Storage == best.Storage)) { bestD = d; best = voxels[i]; }
            }
            voxels.Clear();
            return best;
        }

        public static float Gravity(Vector3D at)
        {
            float interference;
            return MyAPIGateway.Physics.CalculateNaturalGravityAt(at, out interference).Length();
        }
    }
}
