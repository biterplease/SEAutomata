using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Mining;

namespace Automata.Pointer
{
    /// <summary>
    /// Quarry mining (TODO/19): a straight shaft, Q x Q, along an axis (gravity up on a planet; on an asteroid the line
    /// from its box centre through the target, outward), from the highest ground over it (TopAbove: dug under a ridge,
    /// the hull ran into it) down to its bottom (2026-11-06: was a stepped pit of benches, (k + 1) x Q wide; a shaft is
    /// 25x less rock for an ore 80 m down at Q 10, and voxels don't collapse, so steps bought nothing). Several drones
    /// split one shaft into sections (Layout: as many as fit, each at least the widest hull minimum, as square as
    /// possible); QuarrySites keeps shafts dug at the same time on one grid. A section is dug in layers as thick as the
    /// drill cuts, each in nested rectangular rings from its centre out (the hull stays over rock already cleared). A
    /// fixed list of drill points, like SweepPlanner.Spiral: no path search. Rings with no rock (above the ground, dug
    /// before) are left out.
    /// </summary>
    public static class QuarryPlanner
    {
        public const double SIZE_FACTOR = 1.5;           // Q at least this times the drone's longest dimension
        public const double STEP_FACTOR = 1.3;           // ring / layer spacing per m of the drill's cut radius (lines of cut overlap)
        public const double STEP_MIN = 1;                // m
        public const int POINTS_MAX = 40000;             // drill points of one section at most
        public const int DRONES_MAX = 8;                 // drones on one shaft at most

        /// <summary>Q: the slider (0 = unset), never less than SIZE_FACTOR x the drone's longest dimension.</summary>
        public static double Size(double slider, double longest)
        {
            return Math.Max(slider > 0 ? slider : 0, SIZE_FACTOR * longest);
        }

        /// <summary>Ring and layer spacing for a drill that cuts 'cutRadius' m round its point.</summary>
        public static double Step(double cutRadius)
        {
            return Math.Max(STEP_MIN, STEP_FACTOR * cutRadius);
        }

        public const double LANE_MARGIN = 0.5;           // m: a section at least this much wider than the hull across it (both sides)

        /// <summary>
        /// Sections of a 'side' shaft for 'drones' drones: the largest cols x rows (at most the drones) whose sections hold
        /// the hull across the shaft ('narrow' x 'wide': the largest among the drones, DroneControllerBlock.QuarryFootprint)
        /// with LANE_MARGIN to spare (each drone turned square to its section, its wide side along the long one), as square
        /// as possible (lanes when only one way fits). 2026-11-07: every side was at least 1.5 x the longest hull
        /// dimension: tall drones were left out of a shaft they fit in lanes. Returns cols x rows: the drones that take part
        /// (the others are left out).
        /// </summary>
        public static int Layout(double side, double narrow, double wide, int drones, out int cols, out int rows)
        {
            drones = Math.Max(1, Math.Min(drones, DRONES_MAX));
            cols = rows = 1;
            int best = 1, bestDiff = 0;
            for (int c = 1; c <= drones; c++)
                for (int r = 1; c * r <= drones; r++)
                {
                    int k = c * r;
                    double a = side / c, b = side / r;
                    if (k > 1 && (Math.Min(a, b) < narrow + LANE_MARGIN - 1e-6 || Math.Max(a, b) < wide + LANE_MARGIN - 1e-6)) continue;
                    int diff = Math.Abs(c - r);
                    if (k > best || (k == best && diff < bestDiff)) { best = k; bestDiff = diff; cols = c; rows = r; }
                }
            return best;
        }

        /// <summary>
        /// The quarry's axis at 'target' (outward = up): against natural gravity there, else from the voxel map's box centre
        /// (PositionComp.WorldAABB.Center: one read, no sampling) through the target. False: a target at that centre.
        /// </summary>
        public static bool Axis(IMyVoxelBase voxel, Vector3D target, out Vector3D up)
        {
            float interference;
            Vector3 g = MyAPIGateway.Physics.CalculateNaturalGravityAt(target, out interference);
            if (g.LengthSquared() > 0.01f) { up = -Vector3D.Normalize(g); return true; }
            up = Vector3D.Zero;
            if (voxel == null || voxel.PositionComp == null) return false;
            up = target - voxel.PositionComp.WorldAABB.Center;
            if (up.LengthSquared() < 1) return false;
            up.Normalize();
            return true;
        }

        /// <summary>
        /// The highest ground over a section (half sizes 'hu' x 'hv' along 'u' / 'v' round 'entry', its surface point on
        /// the axis), m above 'entry' along 'up' (0: none higher). A 5 x 5 grid over it: where it is rock just under the
        /// entry's level, the surface above it (FindSurface; a hillside may read a little high: its air layers are
        /// dropped anyway).
        /// </summary>
        public static double TopAbove(IMyVoxelBase voxel, Vector3D entry, Vector3D up, Vector3D u, Vector3D v, double hu, double hv)
        {
            double top = 0;
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                {
                    Vector3D p = entry + u * (i * hu / 2) + v * (j * hv / 2) - up;
                    if (!OreScanner.Rock(voxel, p)) continue;   // the ground is lower here
                    Vector3D s;
                    if (!OreScanner.FindSurface(voxel, p, up, TOP_SEARCH, out s, 4)) { top = Math.Max(top, TOP_SEARCH); continue; }
                    top = Math.Max(top, Vector3D.Dot(s - entry, up));
                }
            return top;
        }
        private const double TOP_SEARCH = 200;   // m of rock above the entry's level looked through

        /// <summary>
        /// Drill points, world, in digging order: the section (half sizes 'hu' x 'hv' along 'u' / 'v' round 'entry', its
        /// surface point on the axis) from 'top' m above 'entry' to 'bottom' m below it. 'cut': the drill's cut radius (the
        /// outer ring stops that far inside the edge), 'step': ring / layer spacing. After each layer's rings the drill goes
        /// back to its centre (through the cut layer), then down.
        /// </summary>
        public static List<Vector3D> Points(IMyVoxelBase voxel, Vector3D entry, Vector3D up, Vector3D u, Vector3D v, double hu, double hv,
                                            double top, double bottom, double cut, double step, out int layers)
        {
            var pts = new List<Vector3D>();
            layers = 0;
            double from = -Math.Max(0, top);
            double ru = Math.Max(0, hu - cut), rv = Math.Max(0, hv - cut);
            int nl = Math.Max(1, (int)Math.Ceiling((bottom - from) / step));
            int nr = (int)Math.Ceiling(Math.Max(ru, rv) / step);
            for (int l = 0; l < nl && pts.Count < POINTS_MAX; l++)
            {
                double z = from + (l + 0.5) * (bottom - from) / nl;
                Vector3D c = entry - up * z;
                int first = pts.Count;
                pts.Add(c);
                bool rock = InRock(voxel, c, up, step);
                int rings = 0;
                for (int j = 1; j <= nr; j++)
                {
                    double a = ru * j / nr, b = rv * j / nr;
                    if (!RingInRock(voxel, c, u, v, up, a, b, step)) continue;
                    // start corner (+,+), round (-,+) (-,-) (+,-), closed at (+,+)
                    pts.Add(c + u * a + v * b);
                    pts.Add(c - u * a + v * b);
                    pts.Add(c - u * a - v * b);
                    pts.Add(c + u * a - v * b);
                    pts.Add(c + u * a + v * b);
                    rings++;
                }
                if (rings == 0 && !rock) { pts.RemoveRange(first, pts.Count - first); continue; }
                if (rings > 0) pts.Add(c);   // back to the centre through the cut layer, then down
                layers++;
            }
            return pts;
        }

        // Rock at the layer point or half a layer under it (the drill cuts down to there)
        private static bool InRock(IMyVoxelBase voxel, Vector3D p, Vector3D up, double step)
        {
            return OreScanner.Rock(voxel, p) || OreScanner.Rock(voxel, p - up * (step / 2));
        }

        // Any rock on the ring: its corners and edge middles
        private static bool RingInRock(IMyVoxelBase voxel, Vector3D c, Vector3D u, Vector3D v, Vector3D up, double a, double b, double step)
        {
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    if (i == 0 && j == 0) continue;
                    if (InRock(voxel, c + u * (i * a) + v * (j * b), up, step)) return true;
                }
            return false;
        }
    }
}
