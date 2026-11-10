using System;
using System.Collections.Generic;

using Sandbox.ModAPI;

using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Automata.Pathfinding
{
    /// <summary>
    /// The drone's real shape against the real physics: rays, not the scene's cell boxes (a window's cell is mostly air, a
    /// farm plot's rim sits knee high). For where the boxes are too coarse: harvesters indoors (their legs, the line in to
    /// a plot, a dock with no corridor). A sweep casts rays from the hull box's leading faces, each from the drone's own
    /// surface on its line (OwnDepths: back-cast once where the drone is, kept per drone until ResetRealShape). Hits on
    /// q.OwnGrids, characters and floating objects don't count.
    /// </summary>
    public sealed partial class PathfindingManager
    {
        private const double SWEEP_STEP = 0.4;   // m between the rays across a leading face
        private const double POSE_SHELL = 0.3;   // m: a pose is checked as the shell this deep inside the drone's surface
        private readonly List<IHitInfo> sweepHits = new List<IHitInfo>();
        // Per drone (q.Owner), per leading face and ray grid: how deep behind the box's face the drone's own surface is on
        // each line (NaN: none of it there; the margin lines as the line next to them inside)
        private readonly Dictionary<long, Dictionary<int, double[]>> ownDepths = new Dictionary<long, Dictionary<int, double[]>>();

        /// <summary>The drone's own surface is measured again at its next sweep (a new route: its blocks may have changed).</summary>
        public void ResetRealShape(long owner)
        {
            ownDepths.Remove(owner);
        }

        /// <summary>
        /// Turning in place about hull centre 'centre' from attitude 'a' to 'b': half way and at the end, the drone's real
        /// shape clear of the real physics with 'berth' (the start is the arrival, checked by its route).
        /// </summary>
        public bool RealTurnClear(ref PathQuery q, ref MatrixD a, ref MatrixD b, Vector3D centre, double berth, out string blocker)
        {
            blocker = null;
            Quaternion qa = Quaternion.CreateFromRotationMatrix(a), qb = Quaternion.CreateFromRotationMatrix(b);
            for (int s = 1; s <= 2; s++)
            {
                MatrixD att = MatrixD.CreateFromQuaternion(Quaternion.Slerp(qa, qb, s * 0.5f));
                if (!RealPoseClear(ref q, ref att, centre, berth, out blocker)) return false;
            }
            return true;
        }

        /// <summary>
        /// The drone's real shape at hull centre 'centre', attitude 'att', clear of the real physics: from each side, the
        /// shell just inside its surface swept onto the pose.
        /// </summary>
        public bool RealPoseClear(ref PathQuery q, ref MatrixD att, Vector3D centre, double berth, out string blocker)
        {
            Vector3D[] dirs = { att.Right, att.Left, att.Up, att.Down, att.Forward, att.Backward };
            for (int i = 0; i < 6; i++)
                if (!RealSweepClear(ref q, ref att, centre - dirs[i] * POSE_SHELL, centre, berth, false, out blocker)) return false;
            blocker = null;
            return true;
        }

        // The drone's own surface on each line of a leading face's ray grid, measured where it is now (its physics is there)
        private double[] OwnDepths(ref PathQuery q, int f, bool positive, int n1, int n2, double h1, double h2, double[] half)
        {
            int key = f * 2 + (positive ? 1 : 0) + 8 * ((int)Math.Round(h1 * 100) * 2048 + (int)Math.Round(h2 * 100));
            Dictionary<int, double[]> cache;
            if (!ownDepths.TryGetValue(q.Owner, out cache)) ownDepths[q.Owner] = cache = new Dictionary<int, double[]>();
            double[] depths;
            if (cache.TryGetValue(key, out depths) && depths.Length == n1 * n2) return depths;
            depths = new double[n1 * n2];
            var wm = q.Controller;
            Vector3D[] cur = { wm.Right, wm.Up, wm.Backward };
            Vector3D n = cur[f] * (positive ? 1 : -1);
            int f1 = (f + 1) % 3, f2 = (f + 2) % 3;
            Vector3D face = wm.Translation + Vector3D.TransformNormal(q.LocalHull.Center, wm) + n * half[f];
            // The lines through the box: from the drone's own surface (NaN: none of it there)
            int iLo = n1, iHi = -1, jLo = n2, jHi = -1;
            for (int i = 0; i < n1; i++) if (Math.Abs(-h1 + 2 * h1 * i / (n1 - 1)) < half[f1]) { iLo = Math.Min(iLo, i); iHi = i; }
            for (int j = 0; j < n2; j++) if (Math.Abs(-h2 + 2 * h2 * j / (n2 - 1)) < half[f2]) { jLo = Math.Min(jLo, j); jHi = j; }
            for (int i = iLo; i <= iHi; i++)
                for (int j = jLo; j <= jHi; j++)
                {
                    double u = -h1 + 2 * h1 * i / (n1 - 1), v = -h2 + 2 * h2 * j / (n2 - 1);
                    double depth;
                    if (!OwnSurface(ref q, face + cur[f1] * u + cur[f2] * v, -n, 2 * half[f], out depth)) depth = double.NaN;
                    depths[i * n2 + j] = depth;
                }
            // The margin lines round it (the berth): level with the drone's surface next to them, not the box's face (a
            // camera arm sticking out up top doesn't push the margin under the belly out to it)
            for (int i = 0; i < n1; i++)
                for (int j = 0; j < n2; j++)
                {
                    if (i >= iLo && i <= iHi && j >= jLo && j <= jHi) continue;
                    if (iHi < 0 || jHi < 0) { depths[i * n2 + j] = 0; continue; }
                    int ii = Math.Max(iLo, Math.Min(iHi, i)), jj = Math.Max(jLo, Math.Min(jHi, j));
                    depths[i * n2 + j] = depths[ii * n2 + jj];
                }
            cache[key] = depths;
            return depths;
        }

        // How far from 'from' along 'dir' (into the drone's box) its own grids start; false: none within 'max'
        private bool OwnSurface(ref PathQuery q, Vector3D from, Vector3D dir, double max, out double depth)
        {
            depth = 0;
            sweepHits.Clear();
            MyAPIGateway.Physics.CastRay(from, from + dir * max, sweepHits);
            double best = double.MaxValue;
            for (int k = 0; k < sweepHits.Count; k++)
            {
                var e = sweepHits[k].HitEntity;
                var g = e != null ? e.GetTopMostParent() as IMyCubeGrid : null;
                if (g == null || q.OwnGrids == null || !q.OwnGrids.Contains(g)) continue;
                best = Math.Min(best, Vector3D.Dot(sweepHits[k].Position - from, dir));
            }
            sweepHits.Clear();
            if (best == double.MaxValue) return false;
            depth = Math.Max(0, best);
            return true;
        }

        public bool RealSweepClear(ref PathQuery q, ref MatrixD att, Vector3D a, Vector3D b, double berth, bool ahead, out string blocker)
        {
            double along;
            return RealSweepClear(ref q, ref att, a, b, berth, ahead, out blocker, out along);
        }

        /// <summary>
        /// The hull box held at 'att', its centre moved a -> b: anything of the real physics in the way? Rays from its
        /// leading faces along the leg, a grid every SWEEP_STEP across the faces grown by 'berth', each from the drone's
        /// own surface on its line. 'ahead': the berth in front too (a goal right next to something: the box itself only).
        /// 'blocker': what is in the way; 'along': how far the drone gets along the leg before it (the leg's length when
        /// clear).
        /// </summary>
        public bool RealSweepClear(ref PathQuery q, ref MatrixD att, Vector3D a, Vector3D b, double berth, bool ahead, out string blocker, out double along)
        {
            blocker = null;
            Vector3D d = b - a;
            double len = d.Length();
            along = len;
            if (len < 0.05) return true;
            d /= len;
            Vector3D hull = q.LocalHull.HalfExtents;
            Vector3D[] axes = { att.Right, att.Up, att.Backward };   // the controller frame's X / Y / Z
            double[] half = { hull.X, hull.Y, hull.Z };
            for (int f = 0; f < 3; f++)
            {
                double s = Vector3D.Dot(axes[f], d);
                if (Math.Abs(s) < 0.02) continue;   // a face sliding along the leg: the leading faces cover the swept volume
                Vector3D n = axes[f] * Math.Sign(s);
                int f1 = (f + 1) % 3, f2 = (f + 2) % 3;
                double h1 = half[f1] + berth, h2 = half[f2] + berth;
                int n1 = Math.Max(2, (int)Math.Ceiling(2 * h1 / SWEEP_STEP) + 1), n2 = Math.Max(2, (int)Math.Ceiling(2 * h2 / SWEEP_STEP) + 1);
                // The drone's own surface along each line: back from its box's face into it (a nose above a collector, open
                // space under a wing: those lines start further back, or not at all); the margin lines from the face
                double[] depths = OwnDepths(ref q, f, s > 0, n1, n2, h1, h2, half);
                Vector3D face = a + n * half[f];
                for (int i = 0; i < n1; i++)
                    for (int j = 0; j < n2; j++)
                    {
                        double dd = depths[i * n2 + j];
                        if (double.IsNaN(dd)) continue;   // nothing of the drone on this line
                        double u = -h1 + 2 * h1 * i / (n1 - 1), v = -h2 + 2 * h2 * j / (n2 - 1);
                        Vector3D o = face + axes[f1] * u + axes[f2] * v - n * dd;
                        if (ahead) o += n * berth;
                        sweepHits.Clear();
                        MyAPIGateway.Physics.CastRay(o, o + d * len, sweepHits);
                        for (int k = 0; k < sweepHits.Count; k++)
                        {
                            var e = sweepHits[k].HitEntity;
                            if (e == null || e is IMyCharacter || e is IMyFloatingObject) continue;
                            var top = e.GetTopMostParent();
                            var g = top as IMyCubeGrid;
                            if (g != null && q.OwnGrids != null && q.OwnGrids.Contains(g)) continue;
                            if (g != null)
                            {
                                var slim = g.GetCubeBlock(g.WorldToGridInteger(sweepHits[k].Position + d * 0.02));
                                blocker = slim != null && slim.FatBlock != null ? "'" + slim.FatBlock.DisplayNameText + "'"
                                        : slim != null ? slim.BlockDefinition.Id.SubtypeName : g.DisplayName;
                            }
                            else blocker = top != null ? top.DisplayName ?? top.GetType().Name : "?";
                            along = Vector3D.Dot(sweepHits[k].Position - o, d);
                            blocker += string.Format(" ({0:F1} m along)", along);
                            sweepHits.Clear();
                            return false;
                        }
                    }
            }
            sweepHits.Clear();
            return true;
        }
    }
}
