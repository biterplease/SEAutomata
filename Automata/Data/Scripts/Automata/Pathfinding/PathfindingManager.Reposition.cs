using System;
using System.Collections.Generic;

using VRage.ModAPI;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Spots to reposition to when no route was found: escape spots a few metres round a boxed-in drone (a route is then
    /// planned from each, PlanPath), recovery spots further out (the drone flies there and plans again). Generated in
    /// order of preference in the drone's level frame; whether one is usable is SpotReachable plus the caller's fit check.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private static readonly double[] ESCAPE_DISTANCES = { 4, 8 };
        private static readonly double[] RECOVER_DISTANCES = { 15, 30 };   // m (at least 2 / 4 hull radii)

        /// <summary>The level frame at the drone: 'up' (gravity's, else the controller's), its heading on the level, 'right'. True: in gravity.</summary>
        public static bool LevelFrame(ref PathQuery q, out Vector3D up, out Vector3D fwd, out Vector3D right)
        {
            bool gravity = q.GravityUp.LengthSquared() > 0.5;
            up = gravity ? q.GravityUp : q.Controller.Up;
            fwd = q.Controller.Forward - up * Vector3D.Dot(q.Controller.Forward, up);
            if (fwd.LengthSquared() < 1e-4) fwd = Vector3D.CalculatePerpendicularVector(up);
            fwd.Normalize();
            right = Vector3D.Cross(fwd, up);
            return gravity;
        }

        /// <summary>
        /// Escape spots round 'from' (appended, with what each is to 'why'): up (away from what the drone works on; in
        /// gravity the floor stays below), up and back, back, right, left, ahead; down only in space. Near ones first.
        /// </summary>
        public static void EscapeSpots(ref PathQuery q, Vector3D from, List<Vector3D> spots, List<string> why)
        {
            Vector3D up, fwd, right;
            bool gravity = LevelFrame(ref q, out up, out fwd, out right);
            for (int k = 0; k < ESCAPE_DISTANCES.Length; k++)
            {
                double d = ESCAPE_DISTANCES[k];
                spots.Add(from + up * d); why.Add("up");
                spots.Add(from + (up - fwd) * (d * 0.7071)); why.Add("up and back");
                spots.Add(from - fwd * d); why.Add("back");
                spots.Add(from + right * d); why.Add("right");
                spots.Add(from - right * d); why.Add("left");
                spots.Add(from + fwd * d); why.Add("ahead");
                if (!gravity) { spots.Add(from - up * d); why.Add("down"); }
            }
        }

        /// <summary>
        /// Recovery spots round 'from' (appended), per distance (at least 2 / 4 x the hull radius 'radius'): up, then level
        /// all round, then the diagonals up (and down, in space); near ones first.
        /// </summary>
        public static void RecoverSpots(ref PathQuery q, Vector3D from, double radius, List<Vector3D> spots)
        {
            Vector3D up, fwd, right;
            bool gravity = LevelFrame(ref q, out up, out fwd, out right);
            for (int k = 0; k < RECOVER_DISTANCES.Length; k++)
            {
                double d = Math.Max(RECOVER_DISTANCES[k], radius * 2 * (k + 1));
                spots.Add(from + up * d);
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4;
                    Vector3D h = fwd * Math.Cos(ang) + right * Math.Sin(ang);
                    spots.Add(from + h * d);
                }
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4 + Math.PI / 8;
                    Vector3D h = fwd * Math.Cos(ang) + right * Math.Sin(ang);
                    spots.Add(from + Vector3D.Normalize(h + up) * d);
                    if (!gravity) spots.Add(from + Vector3D.Normalize(h - up) * d);
                }
                if (!gravity) spots.Add(from - up * d);
            }
        }

        /// <summary>
        /// A spot to reposition to from 'from': reachable in a straight line and, in gravity, the hull's half height plus
        /// 1 m over the terrain. (Whether the hull fits there is the caller's: its attitude rules differ.)
        /// </summary>
        public bool SpotReachable(ref PathQuery q, Vector3D from, Vector3D spot)
        {
            Vector3D hit;
            IMyEntity e;
            if (SegmentBlocked(ref q, from, spot, 0.5, out hit, out e)) return false;
            return q.GravityUp.LengthSquared() <= 0.5 || HeightAboveTerrain(spot) >= HullHalfSizeAlong(ref q, ref q.Controller, q.GravityUp) + 1;
        }
    }
}
