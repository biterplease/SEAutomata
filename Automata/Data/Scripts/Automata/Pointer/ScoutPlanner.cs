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
    ///  - Asteroid: rings ("low orbits") around the pointed spot's axis, on a sphere around the asteroid's centre at
    ///    ALTITUDE above the spot.
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

        public static List<Task> SpotCheck(Vector3D hit, Vector3D normal, Vector3D playerPos)
        {
            Vector3D off = normal.LengthSquared() > 0.5 ? Vector3D.Normalize(normal) : Vector3D.Normalize(playerPos - hit);
            Vector3D at = hit + off * SPOT_STANDOFF;
            return new List<Task> { Task.NavigateTo(0, at, at, Gravity(at)), Task.Survey() };
        }

        /// <summary>N sector task lists (each: NavigateTo + Survey per waypoint) covering the disc around 'center'.</summary>
        public static List<List<Task>> AreaSweep(Vector3D center, Vector3D playerPos, int sectors, out string how)
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
                Vector3D c = body.PositionComp.WorldAABB.Center;
                Rings(c, center, points);
                how = string.Format("asteroid rings around {0}", body.StorageName ?? body.EntityId.ToString());
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

        // Rings around the axis centre -> spot, on a sphere ALTITUDE above the spot, as far as AREA_RADIUS from it
        private static void Rings(Vector3D bodyCenter, Vector3D spot, List<Vector3D> points)
        {
            Vector3D axis = spot - bodyCenter;
            double r = axis.Normalize() + ALTITUDE;
            Vector3D a = Vector3D.CalculatePerpendicularVector(axis);
            Vector3D b = Vector3D.Cross(axis, a);
            double thetaMax = Math.Min(Math.PI, AREA_RADIUS / r);
            double dTheta = SPACING / r;
            points.Add(bodyCenter + axis * r);
            int ring = 0;
            for (double t = dTheta; t <= thetaMax + 1e-6; t += dTheta, ring++)
            {
                double ringRadius = r * Math.Sin(t);
                int n = Math.Max(3, (int)Math.Ceiling(2 * Math.PI * ringRadius / SPACING));
                double start = ring % 2 == 0 ? 0 : Math.PI / n;
                for (int i = 0; i < n; i++)
                {
                    double phi = start + (ring % 2 == 0 ? 1 : -1) * 2 * Math.PI * i / n;
                    points.Add(bodyCenter + (axis * Math.Cos(t) + (a * Math.Cos(phi) + b * Math.Sin(phi)) * Math.Sin(t)) * r);
                }
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
