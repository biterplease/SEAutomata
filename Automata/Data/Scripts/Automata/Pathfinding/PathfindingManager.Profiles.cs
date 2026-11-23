using System;

using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// The hull's profiles in a tight spot (2026-11-14). A tight spot is where the hull's box doesn't fit by the cheap,
    /// coarse check (blocks as their whole cells). There the drone may present another side of itself along the way: the
    /// six profiles - each of its three axes along the leg, either of the other two up - are tried, first again by the
    /// coarse check, then by real shapes. A route's points are shared by every drone; the attitude is each drone's own.
    /// </summary>
    public partial class PathfindingManager
    {
        private bool coarseOnly;                         // checks by cell boxes only (ShapesOn false)
        private const double PROFILE_SAME_COS = 0.985;   // ~10°: the same attitude (no turn)
        private const double TURN_FIT_STEP_DEG = 2.5;    // a turn in place: the hull box every this much of it...
        private const double TURN_FIT_MARGIN = 0.1;      // ...this much bigger (covers the box between two steps)
        private readonly MatrixD[] profiles = new MatrixD[7];
        private readonly double[] profileScore = new double[7];
        private readonly sbyte[] profileTurn = new sbyte[7];   // this call's turn checks: 0 not asked, 1 room, -1 none

        /// <summary>
        /// The attitude for the hull flying a -> b, coming in 'prefer' (rotation only): 'prefer' if its box fits by the
        /// coarse check (no tight spot); else the first that does of 'prefer' and the six profiles (least turn from
        /// 'prefer' first), then the same by real shapes. Another profile than 'prefer' needs room to turn into it in
        /// place at a ('turnAt'). False: none fits. 'tight': the coarse check failed in 'prefer'.
        /// </summary>
        public bool LegProfile(ref PathQuery q, Vector3D a, Vector3D b, double skipStart, ref MatrixD prefer, bool turnAt, out MatrixD att, out bool tight)
        {
            att = prefer;
            att.Translation = Vector3D.Zero;
            tight = false;
            Vector3D d = b - a;
            double len = d.Length();
            int n = 1;
            profiles[0] = att;
            profileTurn[0] = 1;
            if (len >= 0.1) n += Profiles(ref q, d / len, ref att, 1);
            for (int k = 1; k < n; k++) profileTurn[k] = 0;
            bool shapes = config.RealShapeConfirm && !offThread;
            Vector3D h;
            IMyEntity e;
            for (int pass = 0; pass < (shapes ? 2 : 1); pass++)
                for (int k = 0; k < n; k++)
                {
                    MatrixD c = profiles[k];
                    bool was = coarseOnly, blocked;
                    coarseOnly = pass == 0 && shapes;
                    try { blocked = SegmentBlocked(ref q, ref c, a, b, skipStart, out h, out e); }
                    finally { coarseOnly = was; }
                    if (blocked)
                    {
                        if (pass == 0 && k == 0) tight = true;
                        continue;
                    }
                    if (k > 0 && turnAt)
                    {
                        if (profileTurn[k] == 0) profileTurn[k] = (sbyte)(TurnInPlaceFits(ref q, a, ref profiles[0], ref c) ? 1 : -1);
                        if (profileTurn[k] < 0) continue;
                    }
                    att = c;
                    return true;
                }
            return false;
        }

        // The six profiles for direction 'd' (unit) into profiles[at..], least turn from 'from' first (the one 'from'
        // already is left out): the hull's forward, up or side along d, either of the other two across it - the "up"
        // across it as near the drone's up as can be (gravity: only those with up near the vertical, AddProfile)
        private int Profiles(ref PathQuery q, Vector3D d, ref MatrixD from, int at)
        {
            Vector3D w1 = from.Up - d * Vector3D.Dot(from.Up, d);
            if (w1.LengthSquared() < 1e-4) w1 = from.Forward - d * Vector3D.Dot(from.Forward, d);
            if (w1.LengthSquared() < 1e-4) w1 = Vector3D.CalculatePerpendicularVector(d);
            w1.Normalize();
            Vector3D w2 = Vector3D.Cross(d, w1);
            int n = 0;
            n += AddProfile(ref q, d, w1, ref from, at + n);
            n += AddProfile(ref q, d, w2, ref from, at + n);
            n += AddProfile(ref q, w1, d, ref from, at + n);
            n += AddProfile(ref q, w2, d, ref from, at + n);
            n += AddProfile(ref q, w1, Vector3D.Cross(d, w1), ref from, at + n);   // (side along d: right = fwd x up = d)
            n += AddProfile(ref q, w2, Vector3D.Cross(d, w2), ref from, at + n);
            for (int i = at + 1; i < at + n; i++)   // least turn first (insertion sort)
            {
                MatrixD m = profiles[i];
                double s = profileScore[i];
                int j = i - 1;
                while (j >= at && profileScore[j] < s) { profiles[j + 1] = profiles[j]; profileScore[j + 1] = profileScore[j]; j--; }
                profiles[j + 1] = m;
                profileScore[j + 1] = s;
            }
            return n;
        }

        private int AddProfile(ref PathQuery q, Vector3D f, Vector3D u, ref MatrixD from, int at)
        {
            // (in gravity: up no further from the vertical than the drone may pitch / roll; level legs: up the vertical)
            bool grav = q.GravityUp.LengthSquared() > 0.5;
            double minUp = !grav ? -2 : q.LevelLegs ? 0.95 : q.MaxPitch >= 0 ? Math.Cos(q.MaxPitch) - 1e-3 : -2;
            bool level = minUp > -2;
            if (level && Math.Abs(Vector3D.Dot(u, q.GravityUp)) < minUp) return 0;
            // (the box is the same either way round: the signs nearest 'from')
            Vector3D fromR = Vector3D.Cross(from.Forward, from.Up);
            double best = double.MinValue;
            Vector3D bf = f, bu = u;
            for (int k = 0; k < 4; k++)
            {
                Vector3D cf = (k & 1) != 0 ? -f : f, cu = (k & 2) != 0 ? -u : u;
                if (level && Vector3D.Dot(cu, q.GravityUp) < 0) continue;
                double s = Vector3D.Dot(cf, from.Forward) + Vector3D.Dot(cu, from.Up) + Vector3D.Dot(Vector3D.Cross(cf, cu), fromR);
                if (s > best) { best = s; bf = cf; bu = cu; }
            }
            if (Vector3D.Dot(bf, from.Forward) > PROFILE_SAME_COS && Vector3D.Dot(bu, from.Up) > PROFILE_SAME_COS) return 0;   // that's 'from'
            profiles[at] = MatrixD.CreateWorld(Vector3D.Zero, bf, bu);
            profileScore[at] = best;
            return 1;
        }

        /// <summary>
        /// A turn in place about 'centre' (hull centre) from attitude 'from' to 'to': the hull box (TURN_FIT_MARGIN bigger)
        /// fits at every TURN_FIT_STEP_DEG of it.
        /// </summary>
        public bool TurnInPlaceFits(ref PathQuery q, Vector3D centre, ref MatrixD from, ref MatrixD to)
        {
            double deg;
            return TurnInPlaceFits(ref q, centre, ref from, ref to, out deg);
        }

        public bool TurnInPlaceFits(ref PathQuery q, Vector3D centre, ref MatrixD from, ref MatrixD to, out double deg)
        {
            MatrixD m0 = MatrixD.CreateWorld(Vector3D.Zero, from.Forward, from.Up), m1 = MatrixD.CreateWorld(Vector3D.Zero, to.Forward, to.Up);
            QuaternionD a = QuaternionD.CreateFromRotationMatrix(m0), b = QuaternionD.CreateFromRotationMatrix(m1);
            double cos = Math.Min(1, Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W));
            deg = MathHelperD.ToDegrees(2 * Math.Acos(cos));
            int steps = (int)Math.Ceiling(deg / TURN_FIT_STEP_DEG);
            if (steps == 0) return true;
            // The end pose first, then the half-way one, the quarters, ... (a turn that doesn't fit mostly fails at its end
            // or half-way: found after a box or two, not after all of them)
            if (!TurnStepFits(ref q, centre, ref a, ref b, steps, steps)) return false;
            int stride = 1;
            while (stride * 2 <= steps - 1) stride *= 2;
            for (; stride >= 1; stride /= 2)
                for (int i = stride; i < steps; i += 2 * stride)
                    if (!TurnStepFits(ref q, centre, ref a, ref b, i, steps)) return false;
            return true;
        }

        private bool TurnStepFits(ref PathQuery q, Vector3D centre, ref QuaternionD a, ref QuaternionD b, int i, int steps)
        {
            MatrixD m = MatrixD.CreateFromQuaternion(QuaternionD.Slerp(a, b, (double)i / steps));
            return HullFitsOriented(ref q, centre, ref m, TURN_FIT_MARGIN);
        }
    }
}
