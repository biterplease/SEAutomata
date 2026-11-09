using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Mining;
using Automata.Pointer;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Drone
{
    /// <summary>
    /// Quarry mining (TODO/19), server side. A Mine task is a quarry when the drone's Mining method is Quarry (a MineOre
    /// job, not a pooled lane) or the task is a designator's quarry preparation (Completion QuarryPrepared, CompletionValue
    /// the order count, TargetBlock its section: X index | cols << 8 | rows << 16, Y the order's group, Z the shaft side in
    /// dm). Pointer.QuarryPlanner gives the drill points: a straight shaft along the axis, from the highest ground over it
    /// (TopAbove) down; the side Q is the Quarry dimension floored at 1.5 x the drone's longest dimension
    /// (QuarryEffectiveSize; a preparation: the largest of its drones', split into sections, QuarryPlanner.Layout).
    ///  - Site (PlanQuarryGeometry, at the NavigateToolTo): Pointer.QuarrySites puts the shaft on the shared site grid
    ///    (shafts near each other line up, never overlap) and gives this drone's section; its surface point is over the
    ///    section's centre. MineOre: down to the ore. Preparation: the target's surface, Q deeper per order. Ore in the
    ///    section (ColumnBlocked): MineOre tries the 8 neighbouring cells, once; a preparation digs down to QUARRY_GUARD
    ///    above it and keeps the ore (2026-11-07: it was refused outright).
    ///  - Digging (RunQuarry): drill on, terrain clearing on, one leg per drill point at QUARRY_SPEED, attitude held. Ore
    ///    guard: any ore but the job's within QUARRY_GUARD m of the drill point stops it ("Quarry blocked by ... at ... m",
    ///    reported in chat: the drone's own broadcast controller, else the orchestrator's, the line kept until one takes it);
    ///    a preparation is done there ("Quarry stopped above ... at ... m (the ore kept)").
    ///  - MineOre: the job's ore within MINE_COLLECT_DOWN of the drill point, or the last point dug: the bore takes over
    ///    from there (BeginBore: straight on to the deposit, collecting), no rebores. Preparation: done, back out.
    ///  - No 20 min cap (MINE_MAX_TICKS is the bore's, counted from its own start): a point not reached in 20 s is
    ///    skipped, 5 in a row end it. The claim on the site is kept while the Mine runs, dropped when it ends.
    ///  - The way out (borePath): to the section's axis at the drill's depth, up it to the top, out along it (BackOut).
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const float QUARRY_SPEED = 2f;                   // m/s of the drill point while quarrying (the ore guard reads every ~0.5 s)
        private const double QUARRY_GUARD = 7.5;                 // m: another ore this close to the drill point stops the quarry (ignored ores excepted: QuarryDigsThrough)
        private const int QUARRY_GUARD_EVERY = 2;                // RunMine calls between ore guard readings
        private const int QUARRY_STALL_TICKS = 20 * 60;          // no closer to the drill point this long: skipped
        private const int QUARRY_SKIPS_MAX = 5;                  // skipped in a row: the quarry ends
        private const double QUARRY_SEARCH = 400;                // m of rock above a target searched for the surface
        private const double QUARRY_COLUMN_STEP = 8;             // m between the column check's readings
        private const double QUARRY_COLUMN_READ_MAX = 24;        // m: the widest ore reading (OreScanner.NearestOtherOre's box)

        private bool quarryActive, quarryPrepare, quarryDug, quarryPlanned;
        private bool quarryFaceOut;    // the drill can't face down the shaft (pitch / roll limits): it faces out from the section's centre, leg by leg
        private const double QUARRY_DOWN_COS = 0.966;   // cos 15°: a drill this close to straight down digs with the attitude held
        private const double QUARRY_DOWN_DEGREES = 15;
        private const int QUARRY_PACE_MAX_TICKS = 90 * 60;      // most a section waits a layer for the ones beside it
        private int quarryPaceFrame;   // waiting for the sections beside it since (0: not)
        private double quarryStep;     // m between layers
        private readonly List<Vector3D> quarryPts = new List<Vector3D>();
        private int quarryIdx, quarrySkips, quarryGuardCheck, quarryLegFrame;
        private double quarryLegBest, quarryBottom, quarrySide;
        private int quarryBenches;     // preparation: orders (each Q deeper)
        private double quarryTop;      // m of ground over the entry's level (the section starts there)
        private double quarryHU, quarryHV;   // the section's half sizes along quarryU / quarryV
        private Vector3D quarryUp, quarryU, quarryV, quarryEntry, quarryFwd, quarryAttUp;

        private static readonly int[] QUARRY_SHIFT_I = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] QUARRY_SHIFT_J = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>Q, m: the Quarry dimension, at least 1.5 x the drone's longest dimension (unset: that).</summary>
        public double QuarryEffectiveSize
        {
            get { return QuarryPlanner.Size(settings != null ? settings.QuarryDimension : 0, DroneLength()); }
        }

        /// <summary>
        /// The hull across the shaft, m ('narrow' <= 'wide'): its two box sides square to the drill when the drill can face
        /// down the shaft (underneath, off gravity, or within the pitch / roll limits), the drone turned square to its
        /// section (QuarryAlignYaw). A drill that can't (facing out, turned leg by leg): 1.5 x the longest dimension both
        /// ways (2026-11-07: that was every drone's: tall ones were left out of lanes they fit).
        /// </summary>
        public void QuarryFootprint(bool inGravity, out double narrow, out double wide)
        {
            narrow = wide = QuarryPlanner.SIZE_FACTOR * DroneLength();
            if (localHullBox.Min.X > localHullBox.Max.X || drillMount.Block == null || drillMount.LocalForward.LengthSquared() < 0.5) return;
            Vector3D f = Vector3D.Normalize(drillMount.LocalForward);
            if (inGravity && settings != null && settings.AlignToPGravity)
            {
                // the tilt from level that points the drill straight down (controller down: -Y): StartQuarry's check, roughly
                double tilt = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(-f.Y, -1, 1)));
                if (tilt > Math.Max(settings.PGravityAlignMaxPitchDegrees, settings.PGravityAlignMaxRollDegrees) + QUARRY_DOWN_DEGREES) return;
            }
            Vector3D size = localHullBox.Size;
            double ax = Math.Abs(f.X), ay = Math.Abs(f.Y), az = Math.Abs(f.Z), s1, s2;
            if (ax >= ay && ax >= az) { s1 = size.Y; s2 = size.Z; }
            else if (ay >= az) { s1 = size.X; s2 = size.Z; }
            else { s1 = size.X; s2 = size.Y; }
            narrow = Math.Min(s1, s2);
            wide = Math.Max(s1, s2);
        }

        private readonly HashSet<MyStringHash> quarrySkipOres = new HashSet<MyStringHash>(MyStringHash.Comparer);
        private int quarrySkipCount = -1;

        /// <summary>
        /// Ores on the drone's ignore list: a quarry digs through them like rock, no guard stops it for them (2026-11-07:
        /// Europa is ice; every shaft stopped at it). The set is rebuilt when the list changes.
        /// </summary>
        private HashSet<MyStringHash> QuarryDigsThrough()
        {
            var list = settings != null ? settings.IgnoredOres : null;
            int n = list != null ? list.Count : 0;
            if (n == quarrySkipCount && (n == 0 || AllIn(list, quarrySkipOres))) return quarrySkipOres;
            quarrySkipOres.Clear();
            for (int i = 0; i < n; i++) if (!string.IsNullOrEmpty(list[i])) quarrySkipOres.Add(MyStringHash.GetOrCompute(list[i]));
            quarrySkipCount = n;
            return quarrySkipOres;
        }

        private static bool AllIn(List<string> keys, HashSet<MyStringHash> set)
        {
            for (int i = 0; i < keys.Count; i++) if (!string.IsNullOrEmpty(keys[i]) && !set.Contains(MyStringHash.GetOrCompute(keys[i]))) return false;
            return true;
        }

        // A designator's quarry preparation (no ore)
        private static bool IsQuarryPrepare(ref Orchestrator.Task mine)
        {
            return mine.Type == Orchestrator.TaskType.Mine && mine.Completion == Orchestrator.ToolTaskCompletionTrigger.QuarryPrepared;
        }

        // This Mine task digs a quarry: a preparation, or a MineOre site (not a pooled lane, not a rebore) with Mining method Quarry
        private bool QuarryWanted(ref Orchestrator.Task mine)
        {
            if (mine.Type != Orchestrator.TaskType.Mine) return false;
            if (IsQuarryPrepare(ref mine)) return true;
            // The method is read once per job, when it first sets off for the site: a change after that waits for the next job
            if (jobQuarry < 0) jobQuarry = (sbyte)(settings != null && settings.MiningMethod == MiningMethod.Quarry ? 1 : 0);
            return jobQuarry == 1 && mine.TargetBlock.X != 1 && !mineSiteFixed;
        }
        private sbyte jobQuarry = -1;   // this job's mining method: -1 not read yet, 1 quarry, 0 bore

        // A preparation's section (Orchestrator.Task.QuarryPrepare); none set: the whole shaft, the drone's own Q
        private static void QuarrySection(ref Orchestrator.Task mine, out int index, out int cols, out int rows, out long group, out double side)
        {
            int x = mine.TargetBlock.X;
            index = x & 0xFF;
            cols = Math.Max(1, (x >> 8) & 0xFF);
            rows = Math.Max(1, (x >> 16) & 0xFF);
            group = mine.TargetBlock.Y;
            side = mine.TargetBlock.Z / 10.0;
        }

        private static string OreLabel(MyStringHash ore)
        {
            string s = ore.String ?? "?";
            int slash = s.IndexOf('/');
            return slash >= 0 ? s.Substring(slash + 1) : s;
        }

        /// <summary>
        /// NavigateToolTo before a quarry: the drill to the top of its section (over the highest ground there), coming
        /// down the axis (approach MINE_APPROACH_OUT out); the Mine task's ApproachFrom is the section's surface point, the
        /// BackOut goes back out over the top. False (reported): no axis, no surface, no free spot, ore in the way.
        /// </summary>
        private bool PlanQuarrySite(ref Orchestrator.Task nav, ref Orchestrator.Task mine)
        {
            mineAngled = false;
            if (!PlanQuarryGeometry(ref mine, mine.Position.ToVector3D())) return false;
            Vector3D top = quarryEntry + quarryUp * quarryTop;
            Vector3D approach = top + quarryUp * MINE_APPROACH_OUT;
            nav.Position = Vector3DData.FromVector3D(top);
            nav.ApproachFrom = Vector3DData.FromVector3D(approach);
            currentJob.Tasks[taskIndex] = nav;
            mine.ApproachFrom = Vector3DData.FromVector3D(quarryEntry);
            currentJob.Tasks[taskIndex + 1] = mine;
            if (taskIndex + 2 < currentJob.Tasks.Count && currentJob.Tasks[taskIndex + 2].Type == Orchestrator.TaskType.BackOut)
            {
                var back = currentJob.Tasks[taskIndex + 2];
                back.Position = back.ApproachFrom = Vector3DData.FromVector3D(approach);
                currentJob.Tasks[taskIndex + 2] = back;
            }
            return true;
        }

        /// <summary>
        /// This drone's section for 'mine' near 'target' (claimed on the site grid), its surface point (quarryEntry), the
        /// highest ground over it (quarryTop) and its depth (quarryBottom); ore in it: MineOre tries the 8 neighbouring
        /// cells, once. False: reported (and the claim dropped).
        /// </summary>
        private bool PlanQuarryGeometry(ref Orchestrator.Task mine, Vector3D target)
        {
            quarryPlanned = false;
            bool prepare = IsQuarryPrepare(ref mine);
            var voxel = QuarryVoxel(ref mine, target);
            if (voxel == null) { Report(MyStrings.Drone_QuarryNoSurface); return false; }
            Vector3D up;
            if (!QuarryPlanner.Axis(voxel, target, out up)) { Report(MyStrings.Drone_QuarryNoAxis); return false; }
            mine.TargetEntityId = voxel.EntityId;
            int index, cols, rows;
            long group;
            double side;
            QuarrySection(ref mine, out index, out cols, out rows, out group, out side);
            if (!prepare || side <= 0) side = QuarryEffectiveSize;
            if (!prepare) { index = 0; cols = rows = 1; group = 0; }
            MyStringHash except = MyStringHash.NullOrEmpty;
            if (!prepare && mine.Inventory != null) foreach (var item in mine.Inventory.GetAllItems()) { except = item.Key; break; }
            // The floor: under the target's surface by Q per order (preparation), the ore (MineOre)
            Vector3D floor = target;
            if (prepare)
            {
                Vector3D surface;
                if (!OreScanner.FindSurface(voxel, target, up, QUARRY_SEARCH, out surface, MineFreeRun())) surface = target;
                floor = surface - up * (Math.Max(1, (int)mine.CompletionValue) * side);
            }
            MyStringHash firstOre = MyStringHash.NullOrEmpty;
            double firstDepth = 0;
            Vector3D shiftU = Vector3D.Zero, shiftV = Vector3D.Zero;
            for (int attempt = 0; attempt <= QUARRY_SHIFT_I.Length; attempt++)
            {
                if (attempt > 0 && prepare) break;
                Vector3D at = attempt == 0 ? target : target + (shiftU * QUARRY_SHIFT_I[attempt - 1] + shiftV * QUARRY_SHIFT_J[attempt - 1]) * side;
                Vector3D axis = up, center, u, v;
                double hu, hv;
                if (!QuarrySites.ClaimSection(_entityId, group, voxel.EntityId, at, ref axis, side, index, cols, rows, out center, out u, out v, out hu, out hv))
                {
                    if (attempt > 0) continue;
                    Report(MyStrings.Drone_QuarryNoRoom);
                    return false;
                }
                if (attempt == 0) { shiftU = u; shiftV = v; }
                Vector3D atFloor = center - axis * Vector3D.Dot(center - floor, axis);   // the section's centre at the floor's level
                Vector3D entry;
                if (!OreScanner.FindSurface(voxel, atFloor, axis, QUARRY_SEARCH, out entry, MineFreeRun()))
                {
                    if (attempt > 0) continue;
                    QuarrySites.Release(_entityId);
                    Report(MyStrings.Drone_QuarryNoSurface);
                    return false;
                }
                double bottom = Math.Max(1, Vector3D.Dot(entry - atFloor, axis));
                double top = QuarryPlanner.TopAbove(voxel, entry, axis, u, v, hu, hv);
                MyStringHash ore;
                double depth;
                if (ColumnBlocked(entry, axis, u, v, hu, hv, top, bottom, except, QuarryDigsThrough(), out ore, out depth))
                {
                    if (prepare)
                    {
                        // A preparation keeps the ore: the shaft stops QUARRY_GUARD above it (none left: nothing to dig)
                        double above = depth - QUARRY_GUARD;
                        if (above < 1)
                        {
                            QuarrySites.Release(_entityId);
                            QuarryBlockedAlert(ore, depth);
                            return false;
                        }
                        if (above < bottom)
                        {
                            Report(MyStrings.Drone_QuarryStopsAbove, OreLabel(ore), depth, above);
                            bottom = above;
                        }
                    }
                    else
                    {
                        if (attempt == 0) { firstOre = ore; firstDepth = depth; }
                        continue;
                    }
                }
                if (attempt > 0) Report(MyStrings.Drone_QuarryMoved, OreLabel(firstOre), Vector3D.Distance(at, target));
                quarryUp = axis;
                quarryU = u;
                quarryV = v;
                quarryHU = hu;
                quarryHV = hv;
                quarryEntry = entry;
                quarryTop = top;
                quarryBottom = bottom;
                quarrySide = side;
                quarryPlanned = true;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: quarry site: {1}, section {2:F1} x {3:F1} m ({4} of {5} x {6} of a {7:F1} m shaft), {8:F0} m over the entry to {9:F0} m under it", Entity.EntityId, ConvertVectorToGPS("quarry", entry), 2 * hu, 2 * hv, index + 1, cols, rows, side, top, bottom);
                return true;
            }
            QuarrySites.Release(_entityId);
            QuarryBlockedAlert(firstOre, firstDepth);
            return false;
        }

        // The Mine task's voxel map, else the one under the target (largest instance of a planet's storage)
        private static IMyVoxelBase QuarryVoxel(ref Orchestrator.Task mine, Vector3D target)
        {
            IMyEntity e;
            var voxel = mine.TargetEntityId != 0 && MyAPIGateway.Entities.TryGetEntityById(mine.TargetEntityId, out e) ? e as IMyVoxelBase : null;
            if (voxel != null) return voxel;
            var found = new List<IMyVoxelBase>();
            var box = new BoundingBoxD(target - 1, target + 1);
            MyAPIGateway.Session.VoxelMaps.GetInstances(found, v => v != null && v.Storage != null && v.PositionComp != null && v.PositionComp.WorldAABB.Intersects(box));
            for (int i = 0; i < found.Count; i++)
                if (voxel == null || found[i].PositionComp.LocalAABB.Volume() > voxel.PositionComp.LocalAABB.Volume()) voxel = found[i];
            return voxel;
        }

        /// <summary>
        /// Ore other than 'except' in the section-to-be (half sizes hu x hv along u / v round 'entry', plus QUARRY_GUARD,
        /// from 'top' m above 'entry' to 'bottom' below it), read down the axis every QUARRY_COLUMN_STEP m (as wide as a
        /// reading goes: the ore guard watches the rest while digging).
        /// </summary>
        private static bool ColumnBlocked(Vector3D entry, Vector3D up, Vector3D u, Vector3D v, double hu, double hv, double top, double bottom,
                                          MyStringHash except, HashSet<MyStringHash> skip, out MyStringHash ore, out double depth)
        {
            ore = MyStringHash.NullOrEmpty;
            depth = 0;
            double r = Math.Min(QUARRY_COLUMN_READ_MAX, Math.Sqrt(hu * hu + hv * hv) + QUARRY_GUARD);
            for (double z = -top; z <= bottom + 0.1; z += QUARRY_COLUMN_STEP)
            {
                Vector3D p = entry - up * z;
                MyStringHash o;
                Vector3D at;
                double d;
                if (!OreScanner.NearestOtherOre(p, r, except, skip, out o, out at, out d)) continue;
                double dz = Vector3D.Dot(entry - at, up);
                if (dz < -top - QUARRY_GUARD || dz > bottom + QUARRY_GUARD) continue;
                Vector3D rel = at - (entry - up * dz);
                if (Math.Abs(Vector3D.Dot(rel, u)) > hu + QUARRY_GUARD || Math.Abs(Vector3D.Dot(rel, v)) > hv + QUARRY_GUARD) continue;
                ore = o;
                depth = dz;
                return true;
            }
            return false;
        }

        /// <summary>RunMine start of a quarry: the drill points planned, the first leg flown.</summary>
        private TaskStatus StartQuarry(ref Orchestrator.Task task, int now)
        {
            quarryPrepare = IsQuarryPrepare(ref task);
            Vector3D target = quarryPrepare ? task.Position.ToVector3D() : boreDeposit;
            if (!quarryPlanned && !PlanQuarryGeometry(ref task, target)) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
            quarryPlanned = false;
            var voxel = QuarryVoxel(ref task, target);
            if (voxel == null) { Report(MyStrings.Drone_QuarryNoSurface); return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome); }
            Vector3D tool = ToolPointWorld();
            Vector3D up = quarryUp;
            if (quarryPrepare)
            {
                quarryBenches = Math.Max(1, (int)task.CompletionValue);
                boreOre = MyStringHash.NullOrEmpty;
                boreTargetKg = 0;
                boreDeposit = quarryEntry - up * Math.Max(0, quarryBottom - quarrySide / 2);   // the section's lowest part (the scans after it)
            }
            else
            {
                quarryBottom = Math.Max(1, Vector3D.Dot(quarryEntry - target, up));   // the live deposit
                quarryBenches = 0;
            }
            quarryTop = QuarryPlanner.TopAbove(voxel, quarryEntry, up, quarryU, quarryV, quarryHU, quarryHV);   // (neighbours may have dug since)
            double cut = drillMount.WorkRadius > 0 ? drillMount.WorkRadius : 2;
            int layers;
            var pts = QuarryPlanner.Points(voxel, quarryEntry, up, quarryU, quarryV, quarryHU, quarryHV, quarryTop, quarryBottom, cut, QuarryPlanner.Step(cut), out layers);
            quarryPts.Clear();
            quarryPts.AddRange(pts);
            quarryIdx = quarrySkips = quarryGuardCheck = 0;
            quarryDug = true;
            borePath.Clear();
            boreHold = false;
            boreHoldFrame = 0;
            boreAheadCheck = 0;
            boreCollecting = false;
            boreBackingUp = false;
            quarryFwd = flightState.WorldMatrix.Forward;
            quarryAttUp = flightState.WorldMatrix.Up;
            // A drill on the front / side / back that the pitch / roll limits keep from facing down the shaft dug with the
            // attitude it arrived in: one side always behind the drill, the hull ran into that wall (2026-11-07). It faces
            // out instead, towards the wall it cuts (the hull stays over the dug middle), as steep as the limits allow
            quarryFaceOut = false;
            if (flightState.InGravity && drillMount.LocalForward.LengthSquared() > 0.5)
            {
                MatrixD down = WorkFrame(ref drillMount, -quarryUp);
                Vector3D dw = Vector3D.Normalize(Vector3D.TransformNormal(drillMount.LocalForward, down));
                quarryFaceOut = Vector3D.Dot(dw, -quarryUp) < QUARRY_DOWN_COS;
                if (quarryFaceOut && Log.DebugEnabled) Log.Debug("Drone {0}: quarry: the drill can't face down ({1:F0}° from it at its limits): facing out from the section's centre leg by leg", Entity.EntityId, MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(dw, -quarryUp), -1, 1))));
            }
            quarryStep = QuarryPlanner.Step(cut);
            quarryPaceFrame = 0;
            if (quarryPts.Count > 0) QuarrySites.Progress(_entityId, Vector3D.Dot(quarryPts[0], quarryUp) + quarryStep);   // the air over its first layer
            if (!quarryFaceOut) QuarryAlignYaw();
            QuarryBackOutPath(tool);
            Report(MyStrings.Drone_QuarryStart, 2 * quarryHU, 2 * quarryHV, quarryTop + quarryBottom, quarryPts.Count);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: quarry{1}: section {2:F1} x {3:F1} m of a {4:F1} m shaft, {5:F0} m over the entry to {6:F0} m under it{7}, {8} layer(s), {9} drill point(s), cut {10:F1} m, step {11:F1} m", Entity.EntityId, quarryPrepare ? " preparation" : " to " + boreOre, 2 * quarryHU, 2 * quarryHV, quarrySide, quarryTop, quarryBottom, quarryPrepare ? " (" + quarryBenches + " order(s))" : "", layers, quarryPts.Count, cut, QuarryPlanner.Step(cut));
            if (quarryPts.Count == 0)
            {
                // Nothing left to dig (dug before): preparation done; mining bores on from here
                if (quarryPrepare) return QuarryFinished(tool);
                return BeginBore(tool, now, Vector3D.Zero, true);
            }
            SetDrillClearing(true);
            SetToolEnabled(Orchestrator.TaskType.Mine, true);
            quarryActive = true;
            if (!DispatchQuarryLeg(now)) { FinishMine(); return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome); }
            return TaskStatus.Running;
        }

        // One drill point: straight there from where the drill is, attitude as it began, slowly (it cuts its way)
        private bool DispatchQuarryLeg(int now)
        {
            Vector3D here = ToolPointWorld();
            Vector3D to = quarryPts[quarryIdx];
            var order = OrderGoTo(to, here);
            if (order == null) return false;
            siteWatchEnd = to;
            order.TransitStart = order.ApproachFrom;
            order.HoldAttitudeInTransit = true;
            order.ReferenceOffset = Vector3DData.FromVector3D(drillMount.LocalPoint);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = Math.Min(QUARRY_SPEED, SafeSpeed);
            order.SpeedCap = order.FinalSpeed;
            order.ArrivalTolerance = 0.75f;
            if (quarryFaceOut)
            {
                // Out from the section's centre across this leg (a ring side: square to it); straight down / back to the
                // middle: as it was. The order turns first (Align), then the drill moves
                Vector3D mid = (here + to) / 2;
                Vector3D c = quarryEntry - quarryUp * Vector3D.Dot(quarryEntry - mid, quarryUp);
                Vector3D outward = mid - c;
                outward -= quarryUp * Vector3D.Dot(outward, quarryUp);
                if (outward.LengthSquared() > 0.25)
                {
                    outward.Normalize();
                    MatrixD f = WorkFrame(ref drillMount, Vector3D.Normalize(-quarryUp + outward * 0.1));
                    quarryFwd = f.Forward;
                    quarryAttUp = f.Up;
                }
            }
            MatrixD none = MatrixD.Identity;
            SetExplicitAttitude(order, true, ref none, quarryFwd, quarryAttUp);
            constructionOrder = order;
            constructionLegs.Clear();
            boring = true;
            boreEnd = to;
            quarryLegBest = Vector3D.Distance(here, to);
            quarryLegFrame = now;
            QuarryBackOutPath(here);
            return true;
        }

        /// <summary>RunMine while quarrying.</summary>
        private TaskStatus RunQuarry(int now, int elapsed)
        {
            Vector3D at = ToolPointWorld();
            QuarrySites.Refresh(_entityId);
            // A grid in the way (another drone): held there, never pushed into
            if (--boreAheadCheck < 0)
            {
                boreAheadCheck = BORE_AHEAD_EVERY;
                string what;
                bool was = boreHold;
                boreHold = BoreBlockedAhead(at, quarryPts[quarryIdx], out what);
                if (boreHold && !was && Log.DebugEnabled) Log.Debug("Drone {0}: quarry: {1} in the way: holding", Entity.EntityId, what);
            }
            if (boreHold)
            {
                if (boreHoldFrame == 0) boreHoldFrame = now;
                quarryLegFrame = now;
                if (now - boreHoldFrame < BORE_HOLD_MAX_TICKS) return TaskStatus.Running;
                Report(MyStrings.Drone_MineBoreBlockedS, BORE_HOLD_MAX_TICKS / 60);
                boreHold = false;
                boreHoldFrame = 0;
                return QuarryStop(at, Orchestrator.TaskFailure.Timeout);
            }
            boreHoldFrame = 0;
            // Ore guard (any ore but the job's), and the job's ore within reach: collecting from here
            if (--quarryGuardCheck < 0)
            {
                quarryGuardCheck = QUARRY_GUARD_EVERY;
                MyStringHash other;
                Vector3D o;
                double d;
                if (OreScanner.NearestOtherOre(at, QUARRY_GUARD, boreOre, QuarryDigsThrough(), out other, out o, out d))
                {
                    if (quarryPrepare)
                    {
                        // A preparation stops at the ore and keeps it: done, as deep as it could go
                        FinishMine();
                        QuarryBackOutPath(at);
                        QuarryOreAlert(MyStrings.Drone_QuarryStoppedAtOre, other, Vector3D.Dot(quarryEntry - o, quarryUp));
                        jobBlocksDone++;
                        return TaskStatus.Done;
                    }
                    FinishMine();
                    QuarryBackOutPath(at);
                    QuarryBlockedAlert(other, Vector3D.Dot(quarryEntry - o, quarryUp));
                    return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome);
                }
                if (!quarryPrepare && OreWithin(at, MINE_COLLECT_DOWN))
                {
                    Report(MyStrings.Drone_QuarryAtOre, Vector3D.Dot(quarryEntry - at, quarryUp));
                    quarryActive = false;
                    boreElapsedStart = elapsed;
                    return BeginBore(at, now, Vector3D.Zero, true);
                }
            }
            double toPt = Vector3D.Distance(at, quarryPts[quarryIdx]);
            if (toPt < quarryLegBest - 0.5) { quarryLegBest = toPt; quarryLegFrame = now; }
            bool next = constructionOrder.Arrived || quarryPaceFrame != 0;
            if (!next && now - quarryLegFrame > QUARRY_STALL_TICKS)
            {
                if (++quarrySkips > QUARRY_SKIPS_MAX)
                {
                    Report(MyStrings.Drone_QuarryStuck, quarrySkips - 1);
                    if (quarryPrepare) return QuarryStop(at, Orchestrator.TaskFailure.Timeout);
                    quarryActive = false;
                    boreElapsedStart = elapsed;
                    return BeginBore(at, now, Vector3D.Zero, true);
                }
                if (Log.DebugEnabled) Log.Debug("Drone {0}: quarry: drill point {1}/{2} not reached in {3} s ({4:F1} m off): skipped", Entity.EntityId, quarryIdx + 1, quarryPts.Count, QUARRY_STALL_TICKS / 60, toPt);
                next = true;
            }
            else if (next) quarrySkips = 0;
            if (next)
            {
                if (QuarryPaceHold(now)) { quarryLegFrame = now; return TaskStatus.Running; }
                if (++quarryIdx >= quarryPts.Count)
                {
                    if (quarryPrepare) return QuarryFinished(at);
                    quarryActive = false;
                    boreElapsedStart = elapsed;
                    return BeginBore(at, now, Vector3D.Zero, true);
                }
                if (!DispatchQuarryLeg(now)) return QuarryStop(at, Orchestrator.TaskFailure.Unreachable);
            }
            // (no time cap: progress is watched point by point; MINE_MAX_TICKS stopped a 150 m pit after 20 min)
            return TaskStatus.Running;
        }

        // Lanes (2026-11-07): the hull is wider than the drill cuts, so it reaches over the sections beside it. Going down a
        // layer waits until the group's other sections (claimed but not started too) are dug down to the layer it leaves,
        // QUARRY_PACE_MAX_TICKS at most, then goes on anyway. True: waiting.
        private bool QuarryPaceHold(int now)
        {
            if (quarryIdx + 1 >= quarryPts.Count) { quarryPaceFrame = 0; return false; }
            double from = Vector3D.Dot(quarryPts[quarryIdx], quarryUp), to = Vector3D.Dot(quarryPts[quarryIdx + 1], quarryUp);
            if (to > from - quarryStep / 2) { quarryPaceFrame = 0; return false; }   // the same layer
            QuarrySites.Progress(_entityId, from);
            int behind = QuarrySites.GroupBehind(_entityId, from + quarryStep / 2);
            if (behind == 0) { quarryPaceFrame = 0; return false; }
            if (quarryPaceFrame == 0)
            {
                quarryPaceFrame = now;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: quarry: layer done (point {1}/{2}): waiting for {3} section(s) beside it to get down here", Entity.EntityId, quarryIdx + 1, quarryPts.Count, behind);
                return true;
            }
            if (now - quarryPaceFrame < QUARRY_PACE_MAX_TICKS) return true;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: quarry: {1} section(s) beside it still higher after {2} s: on down", Entity.EntityId, behind, QUARRY_PACE_MAX_TICKS / 60);
            quarryPaceFrame = 0;
            return false;
        }

        // Turned about the shaft so the hull lies square in its section, its wide side along the section's long one (lanes
        // hardly wider than the hull: QuarryPlanner.Layout); the attitude it arrived in otherwise (DispatchToolTo turned
        // it already: QuarrySquareFrame)
        private void QuarryAlignYaw()
        {
            double angle;
            if (QuarrySquare(ref quarryFwd, ref quarryAttUp, out angle) && Log.DebugEnabled)
                Log.Debug("Drone {0}: quarry: turned {1:F0}° to lie square in its section", Entity.EntityId, MathHelperD.ToDegrees(Math.Abs(angle)));
        }

        /// <summary>The work attitude on the way to a quarry lane (DispatchToolTo): square to the lane on arrival, before
        /// its neighbours are beside it.</summary>
        private void QuarrySquareFrame(ref MatrixD frame)
        {
            Vector3D fwd = frame.Forward, up = frame.Up;
            double angle;
            if (!QuarrySquare(ref fwd, ref up, out angle)) return;
            frame = MatrixD.CreateWorld(frame.Translation, fwd, up);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: quarry: arrives turned {1:F0}° to lie square in its section", Entity.EntityId, MathHelperD.ToDegrees(Math.Abs(angle)));
        }

        // 'fwd' / 'up' turned about the shaft's axis so the hull lies square in the section. False: no turn needed / possible
        private bool QuarrySquare(ref Vector3D fwd, ref Vector3D up, out double angle)
        {
            angle = 0;
            if (localHullBox.Min.X > localHullBox.Max.X) return false;
            MatrixD m = MatrixD.CreateWorld(Vector3D.Zero, fwd, up);
            Vector3D size = localHullBox.Size;
            Vector3D[] axes = { m.Right, m.Up, m.Backward };
            double[] len = { size.X, size.Y, size.Z };
            int vert = 0;
            for (int i = 1; i < 3; i++) if (Math.Abs(Vector3D.Dot(axes[i], quarryUp)) > Math.Abs(Vector3D.Dot(axes[vert], quarryUp))) vert = i;
            int a = vert == 0 ? 1 : 0, b = vert == 2 ? 1 : 2;
            int lng = len[a] >= len[b] ? a : b;
            Vector3D l = axes[lng] - quarryUp * Vector3D.Dot(axes[lng], quarryUp);
            if (l.LengthSquared() < 1e-4) return false;
            l.Normalize();
            Vector3D want;
            if (Math.Abs(len[a] - len[b]) < 0.5 || Math.Abs(quarryHU - quarryHV) < 0.25)
                want = Math.Abs(Vector3D.Dot(l, quarryU)) >= Math.Abs(Vector3D.Dot(l, quarryV)) ? quarryU : quarryV;   // either way: the nearest
            else want = quarryHU >= quarryHV ? quarryU : quarryV;
            if (Vector3D.Dot(l, want) < 0) want = -want;
            angle = Math.Atan2(Vector3D.Dot(Vector3D.Cross(l, want), quarryUp), Vector3D.Dot(l, want));
            if (Math.Abs(angle) < 0.01) return false;
            MatrixD r = MatrixD.CreateFromAxisAngle(quarryUp, angle);
            if (Vector3D.Dot(Vector3D.TransformNormal(l, r), want) < 0.99) r = MatrixD.CreateFromAxisAngle(quarryUp, -angle);
            fwd = Vector3D.TransformNormal(fwd, r);
            up = Vector3D.TransformNormal(up, r);
            return true;
        }

        private const double QUARRY_LANE_SLACK = 1;   // m past its lane's sides a hull centre still counts as in it

        // In this drone's lane (its section's column, up to over its top where the approach comes down)
        private bool InQuarryLane(Vector3D p)
        {
            Vector3D d = p - quarryEntry;
            if (Math.Abs(Vector3D.Dot(d, quarryU)) > quarryHU + QUARRY_LANE_SLACK || Math.Abs(Vector3D.Dot(d, quarryV)) > quarryHV + QUARRY_LANE_SLACK) return false;
            return Vector3D.Dot(d, quarryUp) <= quarryTop + MINE_APPROACH_OUT + DroneLength();
        }

        /// <summary>
        /// 'd' digs (or goes to) another section of this drone's shaft and is in its own lane (2026-11-07). Lanes are
        /// hardly wider than the hulls, so such drones sit side by side by design: no obstacle to this one's routes
        /// (PathOwnGrids), no hold at the shared site while this one stays in its own lane (UpdateSharedSite10); the
        /// drill's own check (BoreBlockedAhead) still stops a leg heading into one.
        /// </summary>
        private bool QuarryLaneMate(DroneControllerBlock d)
        {
            return d != this && (quarryPlanned || quarryActive) && d.block != null && !d.block.Closed && (d.quarryPlanned || d.quarryActive)
                && QuarrySites.SameGroup(_entityId, d._entityId) && d.InQuarryLane(d.HullCentreWorld);
        }

        private readonly List<VRage.Game.ModAPI.IMyCubeGrid> pathOwnGrids = new List<VRage.Game.ModAPI.IMyCubeGrid>();

        // The path checks' own grids (never obstacles): the drone's, and on the way to / in a quarry lane its lane mates'
        private List<VRage.Game.ModAPI.IMyCubeGrid> PathOwnGrids()
        {
            if (!(quarryPlanned || quarryActive)) return droneGrids;
            pathOwnGrids.Clear();
            pathOwnGrids.AddRange(droneGrids);
            for (int i = 0; i < allDrones.Count; i++) if (QuarryLaneMate(allDrones[i])) pathOwnGrids.AddRange(allDrones[i].droneGrids);
            return pathOwnGrids;
        }

        // Preparation done: drill off, back out the way it came
        private TaskStatus QuarryFinished(Vector3D tool)
        {
            FinishMine();
            QuarryBackOutPath(tool);
            Report(MyStrings.Drone_QuarryDone, quarryTop + quarryBottom);
            jobBlocksDone++;
            return TaskStatus.Done;
        }

        // Given up (stuck, held too long): back out, then home
        private TaskStatus QuarryStop(Vector3D tool, Orchestrator.TaskFailure why)
        {
            FinishMine();
            QuarryBackOutPath(tool);
            return Fail(why, TaskStatus.ToReturnHome);
        }

        /// <summary>
        /// The way out of the section (borePath, read backwards by the BackOut): to its axis at the drill's depth, up it to
        /// the top (over the highest ground there); the BackOut ends MINE_APPROACH_OUT out along the axis.
        /// </summary>
        private void QuarryBackOutPath(Vector3D tool)
        {
            Vector3D top = quarryEntry + quarryUp * quarryTop;
            borePath.Clear();
            borePath.Add(top);
            double depth = Vector3D.Dot(top - tool, quarryUp);
            Vector3D axis = top - quarryUp * depth;
            if (depth > 1) borePath.Add(axis);
            boreEntryDir = -quarryUp;
            boreDir = -quarryUp;
            boreStart = top;
            boreStartSet = true;
            if (currentJob == null || taskIndex + 1 >= currentJob.Tasks.Count) return;
            var back = currentJob.Tasks[taskIndex + 1];
            if (back.Type != Orchestrator.TaskType.BackOut || back.RelativeBeaconEntityId != 0) return;
            back.Position = back.ApproachFrom = Vector3DData.FromVector3D(top + quarryUp * MINE_APPROACH_OUT);
            currentJob.Tasks[taskIndex + 1] = back;
        }

        /// <summary>
        /// "Quarry blocked by ORE at DEPTH m": the drone's log / LCD, and chat: through a broadcast controller on its own
        /// construct, else to the orchestrator (kept, and saved, until one takes it).
        /// </summary>
        private void QuarryBlockedAlert(MyStringHash ore, double depth)
        {
            QuarryOreAlert(MyStrings.Drone_QuarryBlocked, ore, depth);
        }

        private void QuarryOreAlert(MyStringId format, MyStringHash ore, double depth)
        {
            string text = Loc.F(format, OreLabel(ore), Math.Max(0, depth));
            Report(text);
            if (!IsServer) return;
            string via;
            if (BroadcastControllerSender.Send(block, Nicknames.Of(_entityId) + ": " + text, out via))
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: '{1}' sent via {2}", Entity.EntityId, text, via);
                return;
            }
            Broadcast(text, true);
        }
    }
}
