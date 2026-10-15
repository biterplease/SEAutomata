using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRageMath;

namespace Automata.Construction
{
    /// <summary>
    /// Weld through. A ship welder needs no line of sight: it welds (and places projected blocks) wherever its sphere
    /// overlaps the block. A block covered on every face can still be welded past a neighbour whose collision leaves
    /// room on the outside (a panel, a window, a magnetic plate on the far side): the tool comes into that cell up to
    /// the collision and the sphere reaches on through it.
    ///  - A face (cell, d) counts when the neighbouring cell holds a built block whose collision leaves at least
    ///    WELD_THROUGH_MIN_FREE m of the cell free from its outer side, and the cell beyond it is free (the way in).
    ///  - Measured with physics rays (the grid's collision, as the welder would meet it), 3 x 3 across the face, from
    ///    just outside the neighbour's cell to its inner side: the shortest free run counts. Cached a few seconds.
    ///  - Only a candidate: whether this drone's sphere reaches the block from there, the drone's own site check
    ///    decides (its real shape against the neighbour's collision); no fit: Unreachable, held back as usual.
    /// </summary>
    public partial class ConstructionComputer
    {
        public const double WELD_THROUGH_MIN_FREE = 0.75;      // m of the neighbour's cell free from its outer side
        private const int WELD_THROUGH_CACHE_TICKS = 5 * 60;
        private const double WELD_THROUGH_RAY_SPREAD = 0.4;   // rays at -0.4 / 0 / 0.4 cell across the face

        private struct ThroughEntry { public bool Ok; public int Frame; }
        private static readonly Dictionary<KeyValuePair<long, Vector3I>, ThroughEntry> _throughCache = new Dictionary<KeyValuePair<long, Vector3I>, ThroughEntry>();
        private static readonly List<IHitInfo> _throughHits = new List<IHitInfo>();
        private int _weldThroughCount;   // this scan's targets with only a weld-through face

        /// <summary>Face 'd' of 'cell' (a block's outer cell) can be welded through its built neighbour (see the class notes).</summary>
        public static bool WeldThroughFace(IMyCubeGrid grid, Vector3I cell, Vector3I d)
        {
            Vector3I n = cell + d;
            if (grid == null || grid.Physics == null || grid.GetCubeBlock(n) == null) return false;   // nothing there: an ordinary face
            if (grid.CubeExists(n + d)) return false;                                                   // covered two deep
            var key = new KeyValuePair<long, Vector3I>(grid.EntityId, new Vector3I(n.X * 4 + d.X + 1, n.Y * 4 + d.Y + 1, n.Z * 4 + d.Z + 1));
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            ThroughEntry entry;
            if (_throughCache.TryGetValue(key, out entry) && now - entry.Frame < WELD_THROUGH_CACHE_TICKS) return entry.Ok;
            if (_throughCache.Count > 4096) _throughCache.Clear();
            bool ok = FreeOuterRun(grid, n, d) >= WELD_THROUGH_MIN_FREE;
            _throughCache[key] = new ThroughEntry { Ok = ok, Frame = now };
            return ok;
        }

        /// <summary>Any face of the block min..max that can be welded through (gravity: not from below).</summary>
        public static bool HasWeldThroughFace(IMyCubeGrid grid, Vector3I min, Vector3I max, ref Vector3D gravityUp)
        {
            MatrixD m = grid.WorldMatrix;
            bool inGravity = gravityUp.LengthSquared() > 0.5;
            for (int i = 0; i < 6; i++)
            {
                Vector3I d = Base6Directions.GetIntVector((Base6Directions.Direction)i);
                if (inGravity && Vector3D.Dot(Vector3D.TransformNormal((Vector3D)d, m), gravityUp) < -0.7) continue;
                Vector3I lo = min, hi = max;
                if (d.X > 0) lo.X = max.X; else if (d.X < 0) hi.X = min.X;
                if (d.Y > 0) lo.Y = max.Y; else if (d.Y < 0) hi.Y = min.Y;
                if (d.Z > 0) lo.Z = max.Z; else if (d.Z < 0) hi.Z = min.Z;
                Vector3I c;
                for (c.X = lo.X; c.X <= hi.X; c.X++)
                    for (c.Y = lo.Y; c.Y <= hi.Y; c.Y++)
                        for (c.Z = lo.Z; c.Z <= hi.Z; c.Z++)
                            if (WeldThroughFace(grid, c, d)) return true;
            }
            return false;
        }

        // m of cell 'n' free of the grid's collision from its outer side (along -d), the shortest of 3 x 3 rays
        private static double FreeOuterRun(IMyCubeGrid grid, Vector3I n, Vector3I d)
        {
            double gs = grid.GridSize;
            MatrixD m = grid.WorldMatrix;
            Vector3D dw = Vector3D.TransformNormal((Vector3D)d, m);
            Vector3I e1 = Base6Directions.GetIntVector(Base6Directions.GetPerpendicular(Base6Directions.GetDirection(d)));
            Vector3I e2;
            Vector3I.Cross(ref d, ref e1, out e2);
            Vector3D a1 = Vector3D.TransformNormal((Vector3D)e1, m), a2 = Vector3D.TransformNormal((Vector3D)e2, m);
            Vector3D outer = grid.GridIntegerToWorld(n) + dw * (gs * 0.5 + 0.05);   // just outside the cell
            double free = gs;
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    Vector3D from = outer + a1 * (i * WELD_THROUGH_RAY_SPREAD * gs) + a2 * (j * WELD_THROUGH_RAY_SPREAD * gs);
                    Vector3D to = from - dw * (gs + 0.05);   // to the neighbour's inner side
                    _throughHits.Clear();
                    MyAPIGateway.Physics.CastRay(from, to, _throughHits);
                    for (int k = 0; k < _throughHits.Count; k++)
                    {
                        var e = _throughHits[k].HitEntity;
                        if (e == null || e.GetTopMostParent() != grid) continue;
                        double run = Vector3D.Dot(from - _throughHits[k].Position, dw) - 0.05;
                        if (run < free) free = Math.Max(0, run);
                    }
                    if (free < WELD_THROUGH_MIN_FREE) { _throughHits.Clear(); return free; }
                }
            _throughHits.Clear();
            return free;
        }
    }
}
