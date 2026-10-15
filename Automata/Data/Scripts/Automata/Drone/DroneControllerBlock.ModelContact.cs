using System;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// Keeping the tool on the block's model. A ship welder / grinder finds its targets by the block's cells, then, for a
    /// block with a model of its own (a fat block), keeps only those whose model triangles meet the box around its sensor
    /// sphere (MyCubeGrid.GetBlocksForTool, MyFakes.ENABLE_TRIANGLE_CHECK_FOR_SHIPTOOLS). A projected block is built by
    /// touching its cells, but a thin model (a panel lying at the far side of its cell) is never welded further from a
    /// cell face. While working, the drone checks with the same test (IMyEntity.GetIntersectionWithAABB) and, if the
    /// sphere misses the model, slides the tool straight to the nearest model point (attitude held, hull checked).
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const int CONTACT_CHECK_TICKS = 60;      // a check this often while the tool works
        private const int CONTACT_SETTLE_TICKS = 90;     // first check after the tool task starts (the drone settles)
        private const int CONTACT_MOVES_MAX = 3;         // moves per block
        private const double CONTACT_REACH = 0.6;        // the model point ends this share of the sphere radius in from its edge
        private const int CONTACT_TRIANGLES_MAX = 20000; // a model this detailed: its box instead of its triangles

        private int contactMoves, contactNextCheck;

        private void ResetModelContact()
        {
            contactMoves = 0;
            contactNextCheck = CONTACT_SETTLE_TICKS;
        }

        /// <summary>Tool task (weld / grind), every tick while it runs: the sphere on the target's model, else a move onto it.</summary>
        private void KeepToolOnModel(ref Orchestrator.Task task, int elapsed)
        {
            if (elapsed < contactNextCheck || contactMoves >= CONTACT_MOVES_MAX) return;
            contactNextCheck = elapsed + CONTACT_CHECK_TICKS;
            var o = constructionOrder;
            if (o == null || !ReferenceEquals(o, activeFlightOrder) || !o.Arrived) return;   // still getting there
            bool gridGone;
            var slim = ToolTarget(ref task, out gridGone);
            if (slim == null || slim.FatBlock == null || slim.FatBlock.Closed) return;   // no model of its own: the cells count
            var fat = slim.FatBlock;
            ToolMount mount = task.Type == Orchestrator.TaskType.GrindBlock ? grinderMount : welderMount;
            if (mount.Block == null || mount.WorkRadius <= 0) return;
            CaptureFlightState();
            Vector3D c = flightState.Position + Vector3D.TransformNormal(mount.LocalPoint, flightState.WorldMatrix);
            double r = mount.WorkRadius;
            var box = new BoundingBoxD(c - new Vector3D(r), c + new Vector3D(r));   // what the tool tests: its sphere's box
            if (fat.GetIntersectionWithAABB(ref box)) return;

            Vector3D q;
            if (!ClosestModelPoint(fat, c, out q))
            {
                contactMoves = CONTACT_MOVES_MAX;
                return;
            }
            Vector3D away = c - q;
            double dist = away.Length();
            Vector3D target = dist > 1e-3 ? q + away / dist * (r * CONTACT_REACH) : q;
            Vector3D move = target - c;
            contactMoves++;
            if (move.LengthSquared() < 0.0025) return;

            // The hull slides with the tool (attitude held): nothing in the way, the target block's own cells aside
            var pm = PathfindingManager.Instance;
            if (pm != null)
            {
                RefreshDroneGrids();
                PathQuery pq;
                BuildPathQuery(out pq);
                pq.SeeAll = true;
                pq.IgnoreGrid = slim.CubeGrid;
                pq.IgnoreMin = slim.Min;
                pq.IgnoreMax = slim.Max;
                MatrixD att = flightState.WorldMatrix;
                Vector3D hull = flightState.Position + Vector3D.TransformNormal(HullCentreLocal, flightState.WorldMatrix);
                Vector3D hit;
                VRage.ModAPI.IMyEntity hitEntity;
                if (pm.SegmentBlocked(ref pq, ref att, hull, hull + move, 0.5, out hit, out hitEntity, false, false))
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: tool sphere misses the model of {1} ({2:F1} m off), and the way onto it is blocked by {3}",
                              Entity.EntityId, slim.BlockDefinition.Id.SubtypeName, dist - r, hitEntity != null ? hitEntity.DisplayName : "something");
                    contactMoves = CONTACT_MOVES_MAX;
                    return;
                }
            }

            IMyTerminalBlock anchor = null;
            MatrixD am = MatrixD.Identity;
            if (taskIndex > 0 && currentJob != null)
            {
                var nav = currentJob.Tasks[taskIndex - 1];
                if (nav.Type == Orchestrator.TaskType.NavigateToolTo && !ResolveTaskFrame(nav.RelativeBeaconEntityId, out anchor, out am))
                    return;
            }
            MatrixD frame = flightState.WorldMatrix;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: tool sphere misses the model of {1} at {2} ({3:F1} m off the sphere): sliding the tool {4:F1} m onto it ({5}/{6})",
                      Entity.EntityId, slim.BlockDefinition.Id.SubtypeName, slim.Position, dist - r, move.Length(), contactMoves, CONTACT_MOVES_MAX);
            if (FlyStrafe(anchor, ref am, target, ref mount, ref frame))
                SetToolEnabled(task.Type, true);   // the strafe switched the tools off
        }

        /// <summary>
        /// The point of the block's model (its triangles, as the tool tests them) nearest to 'p', world. A model with
        /// too many triangles: the nearest point of its bounding box. False: no model data.
        /// </summary>
        private static bool ClosestModelPoint(IMyCubeBlock fat, Vector3D p, out Vector3D closest)
        {
            closest = p;
            var model = fat.Model;
            if (model == null) return false;
            MatrixD inv = fat.WorldMatrixNormalizedInv;
            MatrixD world = fat.WorldMatrix;
            Vector3 lp = (Vector3)Vector3D.Transform(p, inv);
            int n = model.GetTrianglesCount();
            if (n <= 0 || n > CONTACT_TRIANGLES_MAX)
            {
                BoundingBox bb = model.BoundingBox;
                if (bb.Max.X < bb.Min.X) return false;
                closest = Vector3D.Transform((Vector3D)Vector3.Clamp(lp, bb.Min, bb.Max), world);
                return true;
            }
            float best = float.MaxValue;
            Vector3 bestPoint = lp;
            for (int i = 0; i < n; i++)
            {
                var t = model.GetTriangle(i);
                if (t == null) continue;
                Vector3 a, b, c;
                model.GetVertex(t.I0, t.I1, t.I2, out a, out b, out c);
                Vector3 cp = ClosestOnTriangle(lp, a, b, c);
                float d = Vector3.DistanceSquared(lp, cp);
                if (d < best) { best = d; bestPoint = cp; }
            }
            if (best == float.MaxValue) return false;
            closest = Vector3D.Transform((Vector3D)bestPoint, world);
            return true;
        }

        // Closest point of triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5)
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cpv = p - c;
            float d5 = Vector3.Dot(ab, cpv), d6 = Vector3.Dot(ac, cpv);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            float v = vb * denom, w = vc * denom;
            return a + ab * v + ac * w;
        }
    }
}
