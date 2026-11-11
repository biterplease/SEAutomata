using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Orchestrator;

namespace Automata.Designator
{
    /// <summary>
    /// Task lists of scout jobs (TODO/11), server side. Spot check: a standoff point off the surface, an ore scan. Area
    /// sweep: one Sweep task per scout (sector), at most MAX_SCOUTS; the drone maps and flies its sector itself once it is
    /// near (SweepPlanner):
    ///  - Asteroid: OrbitCount(drones) full low orbits round it - max(MIN_ORBITS, drones): 1 drone 4 orbits, 2 drones 2
    ///    each, 3 drones 2 / 1 / 1, 4 and more 1 each - great circles through the pointed spot, evenly spread round it from
    ///    a random start shared by every sector, shared out round-robin.
    ///  - Planet (natural gravity): a spiral round the spot, SweepPlanner.SpiralRadius (1 drone: 100 m, 2: 175 m, 3: 250 m...),
    ///    one arm per drone (arms evenly spread, SPIRAL_TURNS turns each, from the centre out), its plane square to gravity
    ///    at the spot, each point dropped onto the heightmap.
    ///  - Open space: the same spiral in the plane facing the player.
    /// Scan spacing along the path from the smallest detector range of the tagged scouts (else ASSUMED_SMALL_RANGE).
    /// </summary>
    public static class ScoutPlanner
    {
        public const double AREA_RADIUS = 500;      // m round the spot an asteroid is looked for
        public const double SPOT_STANDOFF = 10;     // m off the pointed spot, along its surface normal
        public const int MAX_SCOUTS = 8;            // drones of one area sweep, at most (more tagged: the first 8)
        public const int MIN_ORBITS = 4;            // asteroid orbits of a sweep, at least

        /// <summary>Asteroid orbits of a sweep by its drones: max(MIN_ORBITS, drones), drones capped at MAX_SCOUTS.</summary>
        public static int OrbitCount(int drones)
        {
            return Math.Max(MIN_ORBITS, Math.Min(Math.Max(1, drones), MAX_SCOUTS));
        }
        public const double ASSUMED_SMALL_RANGE = 50;   // m: a small ore detector (no tagged scout to read the range from)
        private static readonly Random random = new Random();

        public static List<Task> SpotCheck(Vector3D hit, Vector3D normal, Vector3D playerPos)
        {
            Vector3D off = normal.LengthSquared() > 0.5 ? Vector3D.Normalize(normal) : Vector3D.Normalize(playerPos - hit);
            Vector3D at = hit + off * SPOT_STANDOFF;
            return new List<Task> { Task.NavigateTo(0, at, at, Gravity(at)), Task.Survey() };
        }

        /// <summary>
        /// A quarry preparation (an area sweep with a mining drone tagged): the drill onto the pointed surface point down the
        /// quarry's axis, one bench (more orders there add benches, the orchestrator), back out (QuarryPlanner).
        /// </summary>
        public static List<Task> QuarryPrepare(Vector3D hit, Vector3D normal, double side = 0, int index = 0, int cols = 1, int rows = 1, int group = 0)
        {
            var body = NearestBody(hit);
            Vector3D up;
            if (!QuarryPlanner.Axis(body, hit, out up)) up = normal.LengthSquared() > 0.5 ? Vector3D.Normalize(normal) : Vector3D.Up;
            Vector3D approach = hit + up * QUARRY_APPROACH;
            return new List<Task>
            {
                Task.NavigateToolTo(0, hit, approach, Gravity(hit)),
                Task.QuarryPrepare(hit, body != null ? body.EntityId : 0, 1, side, index, cols, rows, group),
                Task.BackOut(0, approach),
            };
        }
        private const double QUARRY_APPROACH = 20;   // m out along the axis: approach / back-out point

        /// <summary>The order's GPS marker: "Quarry preparation (60m)", the expected depth.</summary>
        public static string QuarryMarker(double depth)
        {
            return Util.Loc.T(Util.Strings.MyStrings.Common_QuarryPreparation) + string.Format(System.Globalization.CultureInfo.InvariantCulture, " ({0:F0}m)", depth);
        }

        /// <summary>One task list (a Sweep task) per drone (sector), at most MAX_SCOUTS.</summary>
        public static List<List<Task>> AreaSweep(Vector3D center, Vector3D playerPos, int sectors, out string how, double detectorRange = 0)
        {
            double range = detectorRange > 0 ? detectorRange : ASSUMED_SMALL_RANGE;
            float g = Gravity(center);
            IMyVoxelBase body = g > 0.01f ? null : NearestBody(center);
            double tilt;
            lock (random) tilt = random.NextDouble() * Math.PI;
            sectors = Math.Max(1, Math.Min(sectors, MAX_SCOUTS));
            var result = new List<List<Task>>();
            for (int s = 0; s < sectors; s++)
                result.Add(new List<Task> { Task.Sweep(center, playerPos, body != null ? body.EntityId : 0, s, sectors, range, tilt, g) });
            how = body != null
                ? string.Format("asteroid {0}: {1} full orbit(s) shared by {2} drone(s), mapped by the drones when near ({3:F0} m detector)",
                                body.StorageName ?? body.EntityId.ToString(), OrbitCount(sectors), sectors, range)
                : string.Format("{0} spiral, {1:F0} m radius, {2} arm(s) of {3} turns, mapped by the drones when near ({4:F0} m detector)",
                                g > 0.01f ? "planet" : "open space", SweepPlanner.SpiralRadius(sectors), sectors, SweepPlanner.SPIRAL_TURNS, range);
            return result;
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
