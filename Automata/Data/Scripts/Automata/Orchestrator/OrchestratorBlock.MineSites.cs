using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Mining;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Bores of the miners pooled on one MineOre job: one each, never crossing (two drones stacked in one bore, the one
    /// behind pinned the other in: 2026-11-03). In order: the surveyor's sites (other deposits of the ore first, then other
    /// ways in to them), then bores parallel to the first sites. Every bore (its line from the way in to the end) keeps
    /// both hulls + LANE_MARGIN from the bores before it; one whose end comes too close may stop up to LANE_PULL_MAX
    /// short (still within the drill's collecting reach), else the next candidate. Pooled bores are lanes (the Mine
    /// task's TargetBlock.X = 1): the drone bores along the line as given.
    /// </summary>
    public partial class OrchestratorBlock
    {
        private const double LANE_MARGIN = 3;           // m between the hulls of two miners' bores
        private const double LANE_PULL_MAX = 7.5;       // m a bore's end may stop short of its deposit (drills collect within 7.5 m)
        private const double LANE_RADIUS_UNKNOWN = 4;   // m: a bid without the hull's size across its drill
        private const double LANE_FREE = 30;            // m of open line out of a parallel bore's way in
        private const double LANE_SEARCH = 400;         // m of rock a parallel bore may cross
        private const double LANE_APPROACH_OUT = 20;    // m out along the bore: approach / back-out point
        private const int LANE_RINGS = 3;               // rings of parallel bores round a site
        private const int LANE_BASES = 2;               // sites parallel bores are placed round

        private struct Lane { public Vector3D Entry, End; public double Radius; }

        // The job's sites: NavigateToolTo, Mine, BackOut, in the surveyor's order
        private static List<Task[]> MineSites(List<Task> tasks)
        {
            var sites = new List<Task[]>();
            if (tasks == null) return sites;
            for (int i = 0; i + 2 < tasks.Count; i++)
            {
                if (tasks[i].Type != TaskType.NavigateToolTo || tasks[i + 1].Type != TaskType.Mine || tasks[i + 2].Type != TaskType.BackOut) continue;
                sites.Add(new[] { tasks[i], tasks[i + 1], tasks[i + 2] });
                i += 2;
            }
            return sites;
        }

        private static double BoreRadiusOf(Bid b)
        {
            return b != null && b.BoreRadius > 0.1f ? b.BoreRadius : LANE_RADIUS_UNKNOWN;
        }

        /// <summary>
        /// Each slot's site (NavigateToolTo, Mine, BackOut), in the slots' order; null: no clear bore left for it. One
        /// drone: the first site as the surveyor planned it (the drone aims at the ore it finds there, as before).
        /// </summary>
        private List<Task[]> MineLanes(List<Task[]> sites, List<Bid> slots)
        {
            var result = new List<Task[]>();
            if (slots.Count == 1)
            {
                result.Add(new[] { sites[0][0], sites[0][1], sites[0][2] });
                return result;
            }
            var lanes = new List<Lane>();
            var used = new bool[sites.Count];
            for (int s = 0; s < slots.Count; s++)
            {
                double r = BoreRadiusOf(slots[s]);
                Task[] got = null;
                // The surveyor's sites, in order
                for (int k = 0; k < sites.Count && got == null; k++)
                {
                    if (used[k]) continue;
                    Vector3D entry = sites[k][0].Position.ToVector3D(), end = sites[k][1].Position.ToVector3D();
                    double pulled;
                    if (!ClearLane(lanes, entry, ref end, r, out pulled)) continue;
                    used[k] = true;
                    got = LaneTasks(sites[k], entry, end, sites[k][0].ApproachFrom.ToVector3D(), s);
                    lanes.Add(new Lane { Entry = entry, End = end, Radius = r });
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: miner {1}: site {2} of {3}{4}", entityId, slots[s].EntityId, k + 1, sites.Count,
                                                    pulled > 0 ? string.Format(", its end {0:F1} m short (clear of another bore)", pulled) : "");
                }
                // Parallel to the first sites, rings round them
                for (int b = 0; b < Math.Min(LANE_BASES, sites.Count) && got == null; b++)
                    got = ParallelLane(sites[b], lanes, r, s, slots[s].EntityId);
                result.Add(got);
                if (got == null && Log.DebugEnabled) Log.Debug("Orchestrator {0}: miner {1}: no bore clear of the other {2}", entityId, slots[s].EntityId, lanes.Count);
            }
            return result;
        }

        // A bore beside 'site', parallel to it: LANE_RINGS rings of 6 round its line, both hulls + LANE_MARGIN apart per
        // ring; the way in found from its end back out along the line (open for LANE_FREE m)
        private Task[] ParallelLane(Task[] site, List<Lane> lanes, double r, int slot, long droneId)
        {
            Vector3D entry0 = site[0].Position.ToVector3D(), end0 = site[1].Position.ToVector3D();
            Vector3D dir = end0 - entry0;
            if (dir.LengthSquared() < 1) return null;
            dir.Normalize();
            IMyEntity e;
            var voxel = MyAPIGateway.Entities.TryGetEntityById(site[1].TargetEntityId, out e) ? e as IMyVoxelBase : null;
            if (voxel == null) return null;
            double baseR = LANE_RADIUS_UNKNOWN;
            for (int i = 0; i < lanes.Count; i++) baseR = Math.Max(baseR, lanes[i].Radius);
            double spacing = r + baseR + LANE_MARGIN;
            Vector3D u = Vector3D.CalculatePerpendicularVector(dir), v = Vector3D.Cross(dir, u);
            for (int ring = 1; ring <= LANE_RINGS; ring++)
                for (int k = 0; k < 6; k++)
                {
                    double ang = k * Math.PI / 3 + (ring - 1) * Math.PI / 6;
                    Vector3D end = end0 + (u * Math.Cos(ang) + v * Math.Sin(ang)) * spacing * ring;
                    Vector3D entry;
                    if (!OreScanner.FindSurface(voxel, end, -dir, LANE_SEARCH, out entry, LANE_FREE)) continue;
                    double pulled;
                    if (!ClearLane(lanes, entry, ref end, r, out pulled)) continue;
                    lanes.Add(new Lane { Entry = entry, End = end, Radius = r });
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: miner {1}: parallel bore, ring {2}, {3:F0} m off the site's line", entityId, droneId, ring, spacing * ring);
                    return LaneTasks(site, entry, end, entry - dir * LANE_APPROACH_OUT, slot);
                }
            return null;
        }

        // The bore entry -> end clear of every lane by both hulls + LANE_MARGIN; its end pulled back along it up to
        // LANE_PULL_MAX when only that is too close
        private static bool ClearLane(List<Lane> lanes, Vector3D entry, ref Vector3D end, double r, out double pulled)
        {
            pulled = 0;
            Vector3D dir = end - entry;
            double len = dir.Length();
            if (len < 1) return false;
            dir /= len;
            for (double back = 0; back <= LANE_PULL_MAX && back < len - 1; back += 1)
            {
                Vector3D e = end - dir * back;
                bool clear = true;
                for (int i = 0; i < lanes.Count && clear; i++)
                    clear = SegmentsDistance(entry, e, lanes[i].Entry, lanes[i].End) >= r + lanes[i].Radius + LANE_MARGIN;
                if (!clear) continue;
                end = e;
                pulled = back;
                return true;
            }
            return false;
        }

        private static Task[] LaneTasks(Task[] site, Vector3D entry, Vector3D end, Vector3D approach, int slot)
        {
            var nav = site[0];
            var mine = site[1];
            var back = site[2];
            nav.Position = Vector3DData.FromVector3D(entry);
            nav.ApproachFrom = Vector3DData.FromVector3D(approach);
            mine.Position = Vector3DData.FromVector3D(end);
            mine.ApproachFrom = Vector3DData.FromVector3D(entry);
            mine.TargetBlock = Vector3IData.FromVector3I(new Vector3I(1, slot, 0));   // a lane: bored as given
            back.Position = back.ApproachFrom = Vector3DData.FromVector3D(approach);
            return new[] { nav, mine, back };
        }

        // Closest distance between segments p1-q1 and p2-q2
        private static double SegmentsDistance(Vector3D p1, Vector3D q1, Vector3D p2, Vector3D q2)
        {
            Vector3D d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = d1.LengthSquared(), e = d2.LengthSquared(), f = Vector3D.Dot(d2, r);
            double s, t;
            if (a <= 1e-9 && e <= 1e-9) return r.Length();
            if (a <= 1e-9) { s = 0; t = MathHelperD.Clamp(f / e, 0, 1); }
            else
            {
                double c = Vector3D.Dot(d1, r);
                if (e <= 1e-9) { t = 0; s = MathHelperD.Clamp(-c / a, 0, 1); }
                else
                {
                    double b = Vector3D.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-9 ? MathHelperD.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = MathHelperD.Clamp(-c / a, 0, 1); }
                    else if (t > 1) { t = 1; s = MathHelperD.Clamp((b - c) / a, 0, 1); }
                }
            }
            return Vector3D.Distance(p1 + d1 * s, p2 + d2 * t);
        }
    }
}
