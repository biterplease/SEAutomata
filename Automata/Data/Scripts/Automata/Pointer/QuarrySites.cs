using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pointer
{
    /// <summary>
    /// Quarry shafts dug at the same time near each other share a site (server, runtime): the first one's axis and a grid
    /// in its plane, cells as wide as its shaft. Every later shaft within SITE_RANGE takes that axis and snaps to the grid;
    /// one that would overlap another's moves to the nearest free cell next to it. Shafts never overlap and always line
    /// up, so neighbouring shafts make one bigger shaft dug side by side (tiles). A shaft is claimed in sections (the
    /// drones of one designator order: a cols x rows split, QuarryPlanner.Layout); the sections of one group share its
    /// shaft. Claims are dropped when their drone releases them (its Mine ends) or is not heard of for CLAIM_TTL_TICKS; a
    /// site goes with its last claim.
    /// </summary>
    public static class QuarrySites
    {
        public const double SITE_RANGE = 300;        // m: a shaft this close to a site's origin joins it
        private const int SEARCH_CELLS = 3;          // cells searched each way for a free spot
        private const int CLAIM_TTL_TICKS = 10 * 60 * 60;
        private const double EPS = 0.01;

        private sealed class Claim
        {
            public long Owner, Group;
            public double GU0, GU1, GV0, GV1;        // the group's shaft, site coordinates
            public double Done = double.MaxValue;    // dug down to this height (Dot(point, Up)); MaxValue: not started
            public int Seen;
        }

        private sealed class Site
        {
            public long Voxel;
            public Vector3D Origin, Up, U, V;
            public double Pitch;
            public readonly List<Claim> Claims = new List<Claim>();
        }

        private static readonly List<Site> sites = new List<Site>();

        /// <summary>
        /// Section 'index' of a cols x rows split of a 'side' x 'side' shaft near 'target' for 'owner' (its earlier claims
        /// dropped). 'group': the drones of one order (they share the shaft; 0: the owner alone). Out: the axis (the site's),
        /// the plane's basis, the section's centre at the target's level and its half sizes. False: no free spot.
        /// </summary>
        public static bool ClaimSection(long owner, long group, long voxelId, Vector3D target, ref Vector3D up, double side, int index, int cols, int rows,
                                        out Vector3D center, out Vector3D u, out Vector3D v, out double halfU, out double halfV)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            Expire(now);
            Release(owner);
            if (group == 0) group = owner;
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            Site site = null;
            for (int i = 0; i < sites.Count && site == null; i++)
                if (sites[i].Voxel == voxelId && Vector3D.Distance(sites[i].Origin, target) <= SITE_RANGE) site = sites[i];
            if (site == null)
            {
                Vector3D su = Vector3D.CalculatePerpendicularVector(up);
                site = new Site { Voxel = voxelId, Origin = target, Up = up, U = su, V = Vector3D.Cross(up, su), Pitch = side };
                sites.Add(site);
                if (Log.DebugEnabled) Log.Debug("Quarry sites: new site at {0} ({1:F1} m grid)", target, side);
            }
            up = site.Up;
            u = site.U;
            v = site.V;
            Vector3D rel = target - site.Origin;
            double tu = Vector3D.Dot(rel, u), tv = Vector3D.Dot(rel, v), tz = Vector3D.Dot(rel, up);
            double gu0 = 0, gv0 = 0;
            bool found = false;
            for (int i = 0; i < site.Claims.Count && !found; i++)
            {
                var c = site.Claims[i];
                if (c.Group != group) continue;
                gu0 = c.GU0; gv0 = c.GV0;
                found = true;
            }
            if (!found)
            {
                // Its centre snapped to the grid (the site's first shaft: centred on its target), then the nearest free cell
                double p = site.Pitch;
                double bu = Math.Round(tu / p) * p - side / 2, bv = Math.Round(tv / p) * p - side / 2;
                double best = double.MaxValue;
                for (int i = -SEARCH_CELLS; i <= SEARCH_CELLS; i++)
                    for (int j = -SEARCH_CELLS; j <= SEARCH_CELLS; j++)
                    {
                        double u0 = bu + i * p, v0 = bv + j * p;
                        double du = u0 + side / 2 - tu, dv = v0 + side / 2 - tv;
                        double d = du * du + dv * dv;
                        if (d >= best || Overlaps(site, group, u0, u0 + side, v0, v0 + side)) continue;
                        best = d;
                        gu0 = u0; gv0 = v0;
                        found = true;
                    }
                if (!found)
                {
                    if (site.Claims.Count == 0) sites.Remove(site);
                    center = target; halfU = halfV = side / 2;
                    return false;
                }
            }
            site.Claims.Add(new Claim { Owner = owner, Group = group, GU0 = gu0, GU1 = gu0 + side, GV0 = gv0, GV1 = gv0 + side, Seen = now });
            double w = side / cols, h = side / rows;
            int ci = index % cols, ri = Math.Min(rows - 1, index / cols);
            double cu = gu0 + (ci + 0.5) * w, cv = gv0 + (ri + 0.5) * h;
            center = site.Origin + u * cu + v * cv + up * tz;
            halfU = w / 2;
            halfV = h / 2;
            if (Log.DebugEnabled) Log.Debug("Quarry sites: drone {0} (group {1}): section {2} of {3} x {4} of a {5:F1} m shaft at ({6:F1}, {7:F1}) on the site grid, {8:F1} m from the target", owner, group, index, cols, rows, side, gu0, gv0, Math.Sqrt((cu - tu) * (cu - tu) + (cv - tv) * (cv - tv)));
            return true;
        }

        // Another group's shaft overlaps [u0, u1] x [v0, v1]
        private static bool Overlaps(Site site, long group, double u0, double u1, double v0, double v1)
        {
            for (int i = 0; i < site.Claims.Count; i++)
            {
                var c = site.Claims[i];
                if (c.Group == group) continue;
                if (u0 < c.GU1 - EPS && c.GU0 < u1 - EPS && v0 < c.GV1 - EPS && c.GV0 < v1 - EPS) return true;
            }
            return false;
        }

        /// <summary>The owner is still at it (its claims kept).</summary>
        public static void Refresh(long owner)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            for (int s = 0; s < sites.Count; s++)
                for (int i = 0; i < sites[s].Claims.Count; i++)
                    if (sites[s].Claims[i].Owner == owner) sites[s].Claims[i].Seen = now;
        }

        /// <summary>The owner's section is dug down to 'height' (m along the site's axis: Vector3D.Dot(point, up)).</summary>
        public static void Progress(long owner, double height)
        {
            for (int s = 0; s < sites.Count; s++)
                for (int i = 0; i < sites[s].Claims.Count; i++)
                    if (sites[s].Claims[i].Owner == owner) sites[s].Claims[i].Done = Math.Min(sites[s].Claims[i].Done, height);
        }

        /// <summary>'a' and 'b' hold sections of one shaft (the drones of one quarry order).</summary>
        public static bool SameGroup(long a, long b)
        {
            for (int s = 0; s < sites.Count; s++)
            {
                var claims = sites[s].Claims;
                long ga = 0, gb = 0;
                for (int i = 0; i < claims.Count; i++)
                {
                    if (claims[i].Owner == a) ga = claims[i].Group;
                    if (claims[i].Owner == b) gb = claims[i].Group;
                }
                if (ga != 0 && ga == gb) return true;
            }
            return false;
        }

        /// <summary>The other sections of the owner's group not dug down to 'height' yet (claimed but not started too).</summary>
        public static int GroupBehind(long owner, double height)
        {
            int n = 0;
            for (int s = 0; s < sites.Count; s++)
            {
                var claims = sites[s].Claims;
                long group = 0;
                for (int i = 0; i < claims.Count && group == 0; i++) if (claims[i].Owner == owner) group = claims[i].Group;
                if (group == 0) continue;
                for (int i = 0; i < claims.Count; i++)
                    if (claims[i].Group == group && claims[i].Owner != owner && claims[i].Done > height + EPS) n++;
            }
            return n;
        }

        /// <summary>The owner's claims dropped (its Mine ended); a site with none left goes.</summary>
        public static void Release(long owner)
        {
            for (int s = sites.Count - 1; s >= 0; s--)
            {
                sites[s].Claims.RemoveAll(c => c.Owner == owner);
                if (sites[s].Claims.Count == 0) sites.RemoveAt(s);
            }
        }

        private static void Expire(int now)
        {
            for (int s = sites.Count - 1; s >= 0; s--)
            {
                sites[s].Claims.RemoveAll(c => now - c.Seen > CLAIM_TTL_TICKS);
                if (sites[s].Claims.Count == 0) sites.RemoveAt(s);
            }
        }

        /// <summary>World unload.</summary>
        public static void Clear()
        {
            sites.Clear();
        }
    }
}
