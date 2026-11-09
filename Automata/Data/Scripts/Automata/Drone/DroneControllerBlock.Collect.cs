using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Drone
{
    /// <summary>
    /// Collectors (TODO/15): trash collection (Collect tasks: floating objects) and farming (Harvest tasks: farm plots).
    /// How the game's collector works (MyCollector): server only, while on and powered.
    ///  - Intake box: the model's "collector" dummy. A floating object (bag, forageable) that enters it, or touches the
    ///    block, is taken. It sticks out only 0.1 - 0.2 m past the block: the box is brought onto the object.
    ///  - Beam: every 100 ticks one ray from the box centre along the dummy's forward, 2 m. The first thing it hits, if it is
    ///    a harvestable farm plot of another grid, is harvested into the collector (a dead plant is removed).
    /// The collector is on only at the target: in flight it would take whatever it brushes past and harvest whatever
    /// ready plot its beam points at (ToolsOff switches it off).
    /// </summary>
    public partial class DroneControllerBlock
    {
        private readonly List<IMyCollector> collectors = new List<IMyCollector>();
        private readonly List<ToolMount> collectorMounts = new List<ToolMount>();   // LocalPoint: the box centre (the beam's origin); LocalForward: the beam
        private readonly List<double> collectorDepth = new List<double>();          // half the box's depth (its centre to its outer face)
        private const double COLLECTOR_BEAM = 2.0;          // m (MyCollector)
        private const int COLLECTOR_BEAM_TICKS = 100;       // one beam cast every 100 ticks
        private const double HARVEST_TOP_CLEAR = 0.15;      // m: the beam's origin over a plot's open top (the soil is ~1.65 m down in a large plot)
        private const double HARVEST_SIDE_CLEAR = 0.6;      // m: ...off a plot's side wall
        private const double HARVEST_MAX_TILT = 12;         // degrees: the beam within this of the wanted line (pitch / roll limits)
        private const int HARVEST_AIM_TICKS = 4 * 60;       // arrived: the aim check must pass within this
        private const int HARVEST_HOLD_TICKS = 2 * COLLECTOR_BEAM_TICKS + 60;   // aimed and on: two beam casts and some
        private const double COLLECT_DEEP = 0.05;           // m: the box's outer face this far past the object's centre
        private const double COLLECT_ON_DISTANCE = 1.0;     // m: the collector goes on this close to the object
        private const double COLLECT_REDISPATCH = 1.0;      // m: the object moved this far: flown to again
        private const int COLLECT_REDISPATCH_MAX = 6;
        private const int COLLECT_HOLD_TICKS = 5 * 60;      // at the object, collector on: gone within this, else given up
        private const double COLLECT_DOWN_TILT = 35;        // degrees: in gravity a floor object needs the box facing down within this

        // Harvesters fly indoors (crops need pressure): close margins, no minimum altitude, short docking staging
        private const double COMPACT_CLEARANCE = 0.5;      // m kept from obstacles on the route (vs ObstacleClearance): a 2 m drone under a 3.5 m roof
        private const double COMPACT_STAGING = 5.0;        // m: the docking staging point at most this far out
        private const double HARVEST_LIFT_CLEAR = 1.0;     // m: after a plot, up until the beam's tip is this far off it
        private bool CompactFlight { get { return settings != null && settings.BehaviourProfile == BehaviourProfile.IsHarvesterDrone; } }
        private Vector3D harvestNormal;    // the plot face the beam went in by (world), the origin's distance off it, the beam's slope into it
        private double harvestClear, harvestInto = 1;
        private const double HARVEST_HULL_GAP = 0.1;       // m: the hull this far off the plot's face plane at the work point
        private const double HARVEST_SIDE_AIM = 0.15;      // m: a side wall aimed at this far below the plot's top (the drone stays high)
        private static readonly double[] HARVEST_LINE_REACH = { 0.4, 0.25, 0.1 };   // m: along the beam's line, its tip this far past the target (deepest first)
        private const double HARVEST_LINE_BACK = 3.0;      // m: the stand-off at most this far back along the line from the work point
        private const double HARVEST_LINE_RUN = 0.5;       // m: ...and this much further back (a straight run in along the line)
        private const double HARVEST_LINE_BERTH = 0.03;    // m: the way in along the line, swept against the real physics this clear
        private const double HARVEST_MIN_INTO = 0.25;      // the beam's slope into the face (sine), at least
        private static readonly double[] HARVEST_TILTS = { 60, 45, 35, 25, 15 };   // degrees below level: a beam that can't point down

        private readonly List<Vector3D> harvestHeadings = new List<Vector3D>();   // level headings a tilted beam is tried from
        private int collectorIndex = -1;   // the collector the current task uses
        private int collectPhase;          // 0: flying there, 1: at the target (collector on / aiming)
        private int collectPhaseFrame;
        private int collectRedispatches;
        private Vector3D collectTarget;    // Collect: where the object was when the route was planned

        private void AddCollector(IMyCollector c)
        {
            ToolMount m;
            double depth;
            BuildCollectorMount(c, ref flightState.WorldMatrixToLocal, ref flightState.Position, out m, out depth);
            collectors.Add(c);
            collectorMounts.Add(m);
            collectorDepth.Add(depth);
            collectorProfiles.Add(new CollectorProfile
            {
                Flat = c.BlockDefinition.SubtypeName.IndexOf("Flat", StringComparison.OrdinalIgnoreCase) >= 0,
                Axis = Base6Directions.GetClosestDirection((Vector3)m.LocalForward),
                Use = BeamUse.Unknown,
            });
        }

        #region Collector profiles: what each collector's beam can do on this drone
        /// <summary>
        /// Found when the drone's blocks are scanned (players mount collectors any way): its kind (flat: the beam along
        /// the plate; standard: out of its face), where the beam points on the drone, whether the drone itself is in the
        /// beam's way (the game harvests only what the beam hits first), how far the drone reaches past the beam's start
        /// along it, and so how it harvests a plot: straight down from above, tilted in from above along the beam's line
        /// (a level beam the pitch / roll limits can point down), or only into an exposed side (a level beam that can't).
        /// </summary>
        private enum BeamUse : byte { Unknown, None, Down, Tilted, Level }

        private struct CollectorProfile
        {
            public bool Flat;
            public Base6Directions.Direction Axis;   // the beam, on the drone (controller frame)
            public bool SelfBlocked;                 // the beam's first hit is the drone itself
            public double Overhang;                  // m: the drone's surface this far along the beam near its line
            public double BelowLevel;                // degrees: the steepest the beam gets below level within the limits
            public BeamUse Use;
        }

        private readonly List<CollectorProfile> collectorProfiles = new List<CollectorProfile>();
        private readonly List<IHitInfo> profileHits = new List<IHitInfo>();
        private string lastCollectorsLogged;

        private bool CollectorHarvests(int i)
        {
            return i >= 0 && i < collectorProfiles.Count && collectorProfiles[i].Use != BeamUse.None;
        }

        // Any collector that can harvest a plot (not profiled yet: assumed so)
        private bool CanHarvestPlots()
        {
            for (int i = 0; i < collectors.Count; i++) if (CollectorHarvests(i)) return true;
            return false;
        }

        // Profiles every collector (rays against the drone's own physics); the warning for a harvester none of whose
        // collectors can harvest, else null
        private string ProfileCollectors()
        {
            if (block.CubeGrid == null || block.CubeGrid.Physics == null) return lastCollectorNote;
            CaptureFlightState();
            RefreshDroneGrids();
            var sb = new System.Text.StringBuilder();
            bool any = false;
            for (int i = 0; i < collectors.Count && i < collectorProfiles.Count; i++)
            {
                var p = collectorProfiles[i];
                var m = collectorMounts[i];
                Vector3D b = Vector3D.Normalize(m.LocalForward);
                Vector3D origin = flightState.Position + Vector3D.TransformNormal(m.LocalPoint, flightState.WorldMatrix);
                Vector3D beam = Vector3D.Normalize(Vector3D.TransformNormal(m.LocalForward, flightState.WorldMatrix));
                // The drone in the beam's way: its first hit within the beam's reach is one of its own blocks (not the
                // collector itself: the ray may start inside it)
                p.SelfBlocked = false;
                profileHits.Clear();
                MyAPIGateway.Physics.CastRay(origin, origin + beam * COLLECTOR_BEAM, profileHits);
                double first = double.MaxValue;
                bool firstOwn = false;
                for (int h = 0; h < profileHits.Count; h++)
                {
                    var g = profileHits[h].HitEntity != null ? profileHits[h].HitEntity.GetTopMostParent() as IMyCubeGrid : null;
                    if (g != null && g.EntityId == collectors[i].CubeGrid.EntityId)
                    {
                        var slim = g.GetCubeBlock(g.WorldToGridInteger(profileHits[h].Position + beam * 0.01));
                        if (slim != null && slim.FatBlock != null && slim.FatBlock.EntityId == collectors[i].EntityId) continue;
                    }
                    double d = Vector3D.Dot(profileHits[h].Position - origin, beam);
                    if (d < first) { first = d; firstOwn = g != null && droneGrids.Contains(g); }
                }
                p.SelfBlocked = firstOwn;
                // How far the drone reaches past the beam's start along it, on lines next to the beam (a nose over it)
                Vector3D u = Vector3D.CalculatePerpendicularVector(beam), v = Vector3D.Cross(beam, u);
                double far = DroneLength() + 1, overhang = 0;
                for (int k = 0; k < 9; k++)
                {
                    double r = k == 0 ? 0 : k <= 4 ? 0.15 : 0.3;
                    Vector3D off = k == 0 ? Vector3D.Zero : ((k - 1) % 4 == 0 ? u : (k - 1) % 4 == 1 ? -u : (k - 1) % 4 == 2 ? v : -v) * r;
                    Vector3D from = origin + off + beam * far;
                    profileHits.Clear();
                    MyAPIGateway.Physics.CastRay(from, origin + off - beam * 0.2, profileHits);
                    for (int h = 0; h < profileHits.Count; h++)
                    {
                        var g = profileHits[h].HitEntity != null ? profileHits[h].HitEntity.GetTopMostParent() as IMyCubeGrid : null;
                        if (g == null || !droneGrids.Contains(g)) continue;
                        overhang = Math.Max(overhang, Vector3D.Dot(profileHits[h].Position - origin, beam));
                    }
                }
                profileHits.Clear();
                p.Overhang = overhang;
                // The steepest the beam gets below level: its angle from straight down on the drone, less the tilt the
                // pitch (a beam fore / aft) or roll (a beam to a side) limit allows; no limits: any
                double bd = -b.Y, bf = -b.Z, br = b.X;
                double fromDown = MathHelperD.ToDegrees(Math.Atan2(Math.Sqrt(bf * bf + br * br), bd));
                double limit = settings != null && settings.AlignToPGravity
                    ? (Math.Abs(bf) >= Math.Abs(br) ? settings.PGravityAlignMaxPitchDegrees : settings.PGravityAlignMaxRollDegrees) : 180;
                p.BelowLevel = 90 - Math.Max(0, fromDown - limit);
                p.Use = p.SelfBlocked ? BeamUse.None
                      : p.BelowLevel >= 90 - HARVEST_MAX_TILT ? BeamUse.Down
                      : p.BelowLevel >= HARVEST_TILTS[HARVEST_TILTS.Length - 1] ? BeamUse.Tilted
                      : p.BelowLevel >= -HARVEST_MAX_TILT ? BeamUse.Level
                      : BeamUse.None;
                collectorProfiles[i] = p;
                if (p.Use != BeamUse.None) any = true;
                sb.AppendFormat(" '{0}' ({1}): beam {2}, at most {3:F0}° below level, the drone reaches {4:F2} m along it{5}: {6};",
                                collectors[i].CustomName, p.Flat ? "flat" : "standard", p.Axis, p.BelowLevel, p.Overhang,
                                p.SelfBlocked ? string.Format(", its first hit the drone itself ({0:F2} m)", first) : "",
                                p.Use == BeamUse.Down ? "straight down from above" : p.Use == BeamUse.Tilted ? "tilted in from above along its line"
                                : p.Use == BeamUse.Level ? "into a plot's side only" : "can't harvest");
            }
            string text = sb.ToString();
            if (text != lastCollectorsLogged)
            {
                lastCollectorsLogged = text;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: collectors:{1}", Entity.EntityId, text);
            }
            return !any && settings != null && settings.BehaviourProfile == BehaviourProfile.IsHarvesterDrone ? Loc.T(MyStrings.Drone_NoCollectorCanHarvest) : null;
        }
        #endregion

        // As MyCollector.LoadDummies: the first dummy whose name contains "collector" is the box (its scale the size); the
        // beam starts at its centre along its forward. Fallback: the block's centre and forward.
        private static void BuildCollectorMount(IMyCubeBlock b, ref MatrixD worldToController, ref Vector3D controllerPos, out ToolMount m, out double depth)
        {
            m = new ToolMount { Block = b, WorkRadius = COLLECTOR_BEAM };
            depth = 0.1;
            MatrixD w = b.WorldMatrix;
            Vector3D point = w.Translation, forward = w.Forward, up = w.Up;
            if (b.Model != null)
            {
                dummyCache.Clear();
                b.Model.GetDummies(dummyCache);
                foreach (var kv in dummyCache)
                {
                    if (kv.Key.IndexOf("collector", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Matrix dm = kv.Value.Matrix;
                    point = Vector3D.Transform((Vector3D)dm.Translation, w);
                    Vector3D f = Vector3D.TransformNormal((Vector3D)dm.Forward, w);
                    if (f.LengthSquared() > 1e-6) { depth = f.Length() / 2; forward = Vector3D.Normalize(f); }
                    Vector3D u = Vector3D.TransformNormal((Vector3D)dm.Up, w);
                    if (u.LengthSquared() > 1e-6) up = Vector3D.Normalize(u);
                    break;
                }
                dummyCache.Clear();
            }
            up -= forward * Vector3D.Dot(up, forward);
            if (up.LengthSquared() < 1e-3) up = Vector3D.CalculatePerpendicularVector(forward);
            up.Normalize();
            m.LocalPoint = Vector3D.TransformNormal(point - controllerPos, worldToController);
            m.LocalForward = Vector3D.TransformNormal(forward, worldToController);
            m.LocalUp = Vector3D.TransformNormal(up, worldToController);
        }

        private bool collectorArmed;   // a collector was switched on for the target at hand (else they are kept off)

        // Janitors / harvesters: no collector on unless at a target (one the player left on would harvest whatever ready
        // plot it points at while parked or flying)
        private void GuardCollectors()
        {
            if (collectors.Count == 0 || collectorArmed || settings == null || !IsCollectorProfile(settings.BehaviourProfile)) return;
            CollectorsOff();
        }

        private void CollectorOn(int i)
        {
            if (!CollectorUsable(i)) return;
            collectorArmed = true;
            collectors[i].Enabled = true;
        }

        private void CollectorsOff()
        {
            collectorArmed = false;
            for (int i = 0; i < collectors.Count; i++)
                if (collectors[i] != null && !collectors[i].Closed && collectors[i].Enabled) collectors[i].Enabled = false;
        }

        private bool CollectorUsable(int i)
        {
            return i >= 0 && i < collectors.Count && collectors[i] != null && !collectors[i].Closed && collectors[i].IsFunctional;
        }

        // The collector whose beam / box the attitude limits let point closest to 'dir', with that attitude; -1: none within 'maxDeg'
        private int PickCollector(Vector3D dir, double maxDeg, out MatrixD frame, bool harvest = false)
        {
            frame = MatrixD.Identity;
            int best = -1;
            double bestCos = -2;
            for (int i = 0; i < collectors.Count; i++)
            {
                if (!CollectorUsable(i)) continue;
                if (harvest && !CollectorHarvests(i)) continue;   // its beam can't reach a plot (into the drone, up)
                var m = collectorMounts[i];
                MatrixD f = WorkFrame(ref m, dir);
                Vector3D beam = Vector3D.TransformNormal(m.LocalForward, f);
                if (beam.LengthSquared() < 1e-6) continue;
                double c = Vector3D.Dot(Vector3D.Normalize(beam), dir);
                if (c > bestCos) { bestCos = c; best = i; frame = f; }
            }
            if (best < 0 || bestCos < Math.Cos(MathHelperD.ToRadians(maxDeg))) return -1;
            return best;
        }

        // A level work attitude (a beam / box facing straight down): turned about the vertical to the drone's heading now,
        // so it doesn't turn in place at the target (a 4.5 m long drone sweeps a 2.6 m circle doing that, indoors)
        private void KeepHeading(ref MatrixD frame, Vector3D localBeam)
        {
            if (!flightState.InGravity) return;
            Vector3D up = flightState.GravityUp;
            if (Vector3D.Dot(frame.Up, up) < 0.99) return;
            Vector3D beamW = Vector3D.TransformNormal(localBeam, frame);
            if (beamW.LengthSquared() < 1e-6 || Math.Abs(Vector3D.Dot(Vector3D.Normalize(beamW), up)) < 0.95) return;   // a level beam: its heading is the aim
            Vector3D fwd = flightState.WorldMatrix.Forward - up * Vector3D.Dot(flightState.WorldMatrix.Forward, up);
            if (fwd.LengthSquared() < 1e-3) return;
            frame = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(fwd), frame.Up);
        }

        // Cargo full on a collect / harvest trip: the job's other targets of the kind are skipped (on to the unload)
        private void SkipRemainingTargets(Orchestrator.TaskType type)
        {
            int k = taskIndex + 1;
            while (k < currentJob.Tasks.Count && currentJob.Tasks[k].Type == type) k++;
            taskIndex = k - 1;   // (the Done that follows steps onto k)
        }

        #region Collect (floating objects)
        private TaskStatus RunCollect(ref Orchestrator.Task task, bool starting)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            IMyEntity obj;
            bool gone = !MyAPIGateway.Entities.TryGetEntityById(task.TargetEntityId, out obj) || obj == null || obj.MarkedForClose || obj.Closed;
            if (gone)
            {
                CollectorsOff();
                if (!starting) jobBlocksDone++;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: collect: object {1} {2}", Entity.EntityId, task.TargetEntityId, starting ? "gone already" : "collected");
                return TaskStatus.Done;
            }
            if (DroneCargoFreeVolume() < 0.01)
            {
                CollectorsOff();
                if (Log.DebugEnabled) Log.Debug("Drone {0}: collect: cargo full, the rest left", Entity.EntityId);
                SkipRemainingTargets(Orchestrator.TaskType.Collect);
                return TaskStatus.Done;
            }
            if (starting)
            {
                if (collectors.Count == 0) return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                collectRedispatches = 0;
                var f = DispatchCollect(obj);
                return f == Orchestrator.TaskFailure.None ? TaskStatus.Running : Fail(f, TaskStatus.Done);
            }
            if (routeFailed) { CollectorsOff(); return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Done); }
            if (!IsConstructionOrderActive()) { CollectorsOff(); return TaskStatus.Interrupted; }
            // Moved (rolled down a slope, drifting): flown to again
            if (Vector3D.DistanceSquared(obj.GetPosition(), collectTarget) > COLLECT_REDISPATCH * COLLECT_REDISPATCH)
            {
                CollectorsOff();
                if (++collectRedispatches > COLLECT_REDISPATCH_MAX) return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Done);
                var f = DispatchCollect(obj);
                return f == Orchestrator.TaskFailure.None ? TaskStatus.Running : Fail(f, TaskStatus.Done);
            }
            if (collectPhase == 0)
            {
                // On for the last metre: the box takes it as it arrives (only floating objects: nothing else is at risk)
                if (constructionOrder.Arrived || Vector3D.DistanceSquared(ReferencePoint(constructionOrder), constructionOrder.Target.ToVector3D()) < COLLECT_ON_DISTANCE * COLLECT_ON_DISTANCE)
                {
                    CollectorOn(collectorIndex);
                    collectPhase = 1;
                    collectPhaseFrame = now;
                    return TaskStatus.Running;
                }
                return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
            }
            if (now - collectPhaseFrame > COLLECT_HOLD_TICKS)
            {
                CollectorsOff();
                if (Log.DebugEnabled) Log.Debug("Drone {0}: collect: object {1} still there after {2} s at it: skipped", Entity.EntityId, task.TargetEntityId, COLLECT_HOLD_TICKS / 60);
                return Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
            }
            return TaskStatus.Running;
        }

        // The box onto the object: in gravity from above (box facing down), in space from the drone's side
        private Orchestrator.TaskFailure DispatchCollect(IMyEntity obj)
        {
            CaptureFlightState();
            RefreshDroneGrids();
            collectPhase = 0;
            Vector3D p = obj.GetPosition();
            collectTarget = p;
            Vector3D dir;
            double maxTilt;
            if (flightState.InGravity) { dir = -flightState.GravityUp; maxTilt = COLLECT_DOWN_TILT; }
            else
            {
                dir = p - flightState.Position;
                if (dir.LengthSquared() < 1e-4) dir = flightState.WorldMatrix.Forward;
                dir.Normalize();
                maxTilt = 180;
            }
            MatrixD frame;
            int ci = PickCollector(dir, maxTilt, out frame);
            if (ci >= 0) KeepHeading(ref frame, collectorMounts[ci].LocalForward);
            if (ci < 0)
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: collect: no collector can face the object (down within {1:F0}°)", Entity.EntityId, maxTilt);
                return Orchestrator.TaskFailure.Unreachable;
            }
            collectorIndex = ci;
            var m = collectorMounts[ci];
            Vector3D beam = Vector3D.Normalize(Vector3D.TransformNormal(m.LocalForward, frame));
            // The box centre short of the object, its outer face COLLECT_DEEP past it: the object inside the box
            Vector3D target = p - beam * Math.Max(0, collectorDepth[ci] - COLLECT_DEEP);
            Vector3D approach = target - beam * (2.5 + DroneRadius());
            MatrixD am = MatrixD.Identity;
            var failure = FlyRoute(null, ref am, target, approach, DroneRadius(), m.LocalPoint, ref frame, false, true);
            if (failure == Orchestrator.TaskFailure.None)
                if (Log.DebugEnabled) Log.Debug("Drone {0}: collect: object {1} at {2}, collector '{3}', {4}", Entity.EntityId, obj.EntityId, p, collectors[ci].CustomName, DescribeAttitude(ref frame));
            return failure;
        }
        #endregion

        #region Harvest (farm plots)
        /// <summary>A farm plot (a block with the game's farm plot logic, not an algae / oxygen farm), by entity id.</summary>
        public static bool FarmPlot(long id, out IMyCubeBlock plot, out IMyFarmPlotLogic farm)
        {
            plot = null;
            farm = null;
            IMyEntity e;
            if (id == 0 || !MyAPIGateway.Entities.TryGetEntityById(id, out e)) return false;
            plot = e as IMyCubeBlock;
            if (plot == null || plot.Closed || plot.MarkedForClose) return false;
            farm = FarmLogic(plot);
            return farm != null;
        }

        /// <summary>The farm plot logic of a block; null for none and for algae / oxygen farms (no plot to harvest).</summary>
        public static IMyFarmPlotLogic FarmLogic(IMyCubeBlock b)
        {
            if (b == null || b.Components == null || !(b is IMyFunctionalBlock) || b is SpaceEngineers.Game.ModAPI.IMyOxygenFarm) return null;
            string subtype = b.BlockDefinition.SubtypeName ?? "";
            if (subtype.IndexOf("Algae", StringComparison.OrdinalIgnoreCase) >= 0 || subtype.IndexOf("OxygenFarm", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            // (Components.Get<T> wants a component class; the logic is known by its interface: modded plots too)
            foreach (var c in b.Components)
            {
                var farm = c as IMyFarmPlotLogic;
                if (farm != null) return farm;
            }
            return null;
        }

        /// <summary>A crop ('outputKey', "ConsumableItem/Fruit") is ignored when its seed ("SeedItem/Fruit") is: the same subtype.</summary>
        public static bool CropIgnored(List<string> ignoredSeeds, string outputKey)
        {
            if (ignoredSeeds == null || ignoredSeeds.Count == 0 || string.IsNullOrEmpty(outputKey)) return false;
            string sub = Subtype(outputKey);
            for (int i = 0; i < ignoredSeeds.Count; i++)
                if (string.Equals(Subtype(ignoredSeeds[i]), sub, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Subtype(string key)
        {
            int s = key != null ? key.LastIndexOf('/') : -1;
            return s >= 0 ? key.Substring(s + 1) : key;
        }

        private bool IsCropIgnored(string outputKey)
        {
            return settings != null && CropIgnored(settings.IgnoredCrops, outputKey);
        }

        private TaskStatus RunHarvest(ref Orchestrator.Task task, bool starting)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            IMyCubeBlock plot;
            IMyFarmPlotLogic farm;
            if (!FarmPlot(task.TargetEntityId, out plot, out farm)) { CollectorsOff(); return Fail(Orchestrator.TaskFailure.TargetRemoved, TaskStatus.Done); }
            if (!farm.IsHarvestable)
            {
                // Harvested (by this drone's beam, or someone else), or nothing to do any more
                CollectorsOff();
                if (!starting) jobBlocksDone++;
                if (!starting && collectPhase == 1) LiftOffPlot();
                if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: plot '{1}' {2}", Entity.EntityId, plot.DisplayNameText, starting ? "not harvestable (any more): skipped" : "harvested");
                return TaskStatus.Done;
            }
            if (DroneCargoFreeVolume() < 0.01)
            {
                CollectorsOff();
                if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: cargo full, the other plots left", Entity.EntityId);
                SkipRemainingTargets(Orchestrator.TaskType.Harvest);
                return TaskStatus.Done;
            }
            if (starting)
            {
                if (collectors.Count == 0) return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                var f = DispatchHarvest(plot);
                return f == Orchestrator.TaskFailure.None ? TaskStatus.Running : Fail(f, TaskStatus.Done);
            }
            if (routeFailed) { CollectorsOff(); return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Done); }
            if (!IsConstructionOrderActive()) { CollectorsOff(); return TaskStatus.Interrupted; }
            if (collectPhase == 0)
            {
                if (!constructionOrder.Arrived) return WatchProgress() ? TaskStatus.Running : Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
                collectPhase = 1;
                collectPhaseFrame = now;
            }
            if (!CollectorUsable(collectorIndex)) return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
            var col = collectors[collectorIndex];
            string why;
            bool aimed = BeamOnTarget(plot, out why);
            if (!col.Enabled)
            {
                // On only once its beam hits this plot first: never a neighbour (a crop the player ignores, another
                // computer's plot)
                if (aimed)
                {
                    CollectorOn(collectorIndex);
                    collectPhaseFrame = now;
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: aimed at plot '{1}', collector '{2}' on", Entity.EntityId, plot.DisplayNameText, col.CustomName);
                }
                else if (now - collectPhaseFrame > HARVEST_AIM_TICKS)
                {
                    LiftOffPlot();
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: plot '{1}' skipped: {2}", Entity.EntityId, plot.DisplayNameText, why);
                    return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.Done);
                }
                return TaskStatus.Running;
            }
            if (!aimed)
            {
                // Drifted off it: off until it is back on
                CollectorsOff();
                collectPhaseFrame = now;
                return TaskStatus.Running;
            }
            if (now - collectPhaseFrame > HARVEST_HOLD_TICKS)
            {
                CollectorsOff();
                LiftOffPlot();
                if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: plot '{1}' not harvested after {2} s aimed: skipped", Entity.EntityId, plot.DisplayNameText, HARVEST_HOLD_TICKS / 60);
                return Fail(Orchestrator.TaskFailure.Timeout, TaskStatus.Done);
            }
            return TaskStatus.Running;
        }

        // The beam's origin close to one of the plot's faces, the beam into it: the open top first (the drone stays over the
        // plots: straight down, or tilted in along the beam's line, from any heading), else an exposed side; the first face
        // a collector can point at
        private Orchestrator.TaskFailure DispatchHarvest(IMyCubeBlock plot)
        {
            CaptureFlightState();
            RefreshDroneGrids();
            ownDepths.Clear();
            collectPhase = 0;
            var grid = plot.CubeGrid;
            Vector3I min = plot.Min, max = plot.Max, size = max - min + Vector3I.One;
            Vector3D centre = (grid.GridIntegerToWorld(min) + grid.GridIntegerToWorld(max)) * 0.5;
            Vector3I upCell = Base6Directions.GetIntVector(plot.Orientation.Up);
            var tried = new System.Text.StringBuilder();
            var plotBox = new MyOrientedBoundingBoxD(centre, (Vector3D)size * (grid.GridSize * 0.5) + new Vector3D(HARVEST_HULL_GAP),
                                                     Quaternion.CreateFromRotationMatrix(grid.WorldMatrix));
            HullBlockBoxes();
            // The plot's real top (its physics, not its cell: a large plot's soil sits ~1.65 m under the cell's top, the crops
            // are no obstacle): the soil at its centre, the rim round it. The beam has to hit one of them.
            Vector3D upW = Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)upCell, grid.WorldMatrix));
            double halfUp = Math.Abs(size.X * upCell.X + size.Y * upCell.Y + size.Z * upCell.Z) * grid.GridSize * 0.5;
            double soilUp, rimUp;
            PlotHeights(plot, centre, upW, halfUp, out soilUp, out rimUp);
            Vector3D ua = Vector3D.Abs((Vector3D)upCell), he = (Vector3D)size * (grid.GridSize * 0.5);
            var solidBox = new MyOrientedBoundingBoxD(centre + upW * ((rimUp - halfUp) * 0.5),
                                                      he * (Vector3D.One - ua) + ua * ((rimUp + halfUp) * 0.5) + new Vector3D(HARVEST_HULL_GAP),
                                                      Quaternion.CreateFromRotationMatrix(grid.WorldMatrix));
            for (int pass = 0; pass < 2; pass++)
            {
                for (int d = 0; d < 6; d++)
                {
                    Vector3I n = Base6Directions.GetIntVector((Base6Directions.Direction)d);
                    bool top = n == upCell;
                    if (n == -upCell || (pass == 0) != top) continue;   // the top first, then the sides; never the bottom
                    if (!FaceExposed(grid, min, max, n)) { tried.Append(top ? " top: covered;" : " side: covered;"); continue; }
                    Vector3D nw = Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)n, grid.WorldMatrix));
                    double half = Math.Abs(size.X * n.X + size.Y * n.Y + size.Z * n.Z) * grid.GridSize * 0.5;
                    MatrixD frame = MatrixD.Identity;
                    int ci = -1;
                    Vector3D beam = Vector3D.Zero, origin = Vector3D.Zero, standoff = Vector3D.Zero;
                    double into = 0, clear = 0;
                    bool line = false;
                    string why = null;
                    // The beam straight into the face; from above, a beam that can't point down (a flat collector's, along the
                    // drone) slanted in as steep as the pitch limits allow: from the drone's heading, then the plot's four
                    harvestHeadings.Clear();
                    if (top)
                    {
                        Vector3D h0 = flightState.WorldMatrix.Forward - nw * Vector3D.Dot(flightState.WorldMatrix.Forward, nw);
                        if (h0.LengthSquared() < 1e-3) h0 = Vector3D.CalculatePerpendicularVector(nw);
                        harvestHeadings.Add(Vector3D.Normalize(h0));
                        Vector3D[] gax = { grid.WorldMatrix.Right, grid.WorldMatrix.Up, grid.WorldMatrix.Backward };
                        for (int ax = 0; ax < 6; ax++)
                        {
                            Vector3D h = gax[ax / 2] * (ax % 2 == 0 ? 1 : -1);
                            if (Math.Abs(Vector3D.Dot(h, nw)) > 0.5) continue;
                            bool dup = false;
                            for (int k = 0; k < harvestHeadings.Count; k++) if (Vector3D.Dot(harvestHeadings[k], h) > 0.94) dup = true;
                            if (!dup) harvestHeadings.Add(h);
                        }
                    }
                    int tilts = top ? HARVEST_TILTS.Length * harvestHeadings.Count + 1 : 1;
                    for (int t = 0; t < tilts && ci < 0; t++)
                    {
                        Vector3D dir = -nw;
                        if (t > 0)
                        {
                            Vector3D h = harvestHeadings[(t - 1) / HARVEST_TILTS.Length];
                            double a = MathHelperD.ToRadians(HARVEST_TILTS[(t - 1) % HARVEST_TILTS.Length]);
                            dir = -nw * Math.Sin(a) + h * Math.Cos(a);
                        }
                        int c = PickCollector(dir, HARVEST_MAX_TILT, out frame, true);
                        if (c < 0) { why = "no collector aims there within the tilt limits"; continue; }
                        var mc = collectorMounts[c];
                        if (t == 0) KeepHeading(ref frame, mc.LocalForward);
                        beam = Vector3D.Normalize(Vector3D.TransformNormal(mc.LocalForward, frame));
                        into = -Vector3D.Dot(beam, nw);
                        if (into < HARVEST_MIN_INTO) { why = "the beam can't slope into it"; continue; }
                        // Where the beam goes in: a side wall just under the rim; from above the soil at the centre (a slanted
                        // beam may hit the rim on the way: the same plot)
                        Vector3D target;
                        double depth;
                        if (!top) { target = centre + nw * (half - 0.05) + upW * (rimUp - HARVEST_SIDE_AIM); depth = 0.05; }
                        else { target = centre + upW * soilUp; depth = half - soilUp; }
                        // As close as the drone's own blocks allow (not its bounding box: a nose over the plot's top is fine, a
                        // part of it in the plot isn't), further out in steps while the beam still reaches
                        double along = 0;
                        bool fits = false;
                        for (clear = top ? HARVEST_TOP_CLEAR : HARVEST_SIDE_CLEAR; ; clear += 0.05)
                        {
                            along = (clear + depth) / into;   // the beam's origin to the target
                            if (along > COLLECTOR_BEAM - 0.1) break;
                            origin = target - beam * along;
                            if (HullClearOf(plotBox, origin, ref frame, mc.LocalPoint)) { fits = true; break; }
                        }
                        if (!fits)
                        {
                            // Clear of the whole cell nowhere in reach: in along the beam's line instead, as a pilot does, the
                            // drone's real shape against the plot's real one (a nose over the soil, under the cell's top)
                            string lineWhy;
                            if (LineIn(target, beam, ref frame, mc.LocalPoint, solidBox, out origin, out standoff, out lineWhy))
                            {
                                line = true;
                                clear = Vector3D.Dot(origin - (top ? centre + upW * rimUp : centre + nw * half), nw);
                            }
                            else { why = string.Format("slope {0:F0}°: {1}", MathHelperD.ToDegrees(Math.Asin(Math.Min(1, into))), lineWhy); continue; }
                        }
                        ci = c;
                    }
                    if (ci < 0) { tried.Append(top ? " top: " : " side: ").Append(why ?? "?").Append(';'); continue; }
                    var m = collectorMounts[ci];
                    // In from straight out of the face, as far out as is clear (a roof over the plots: under it); along the
                    // beam's line: from its stand-off (the last leg straight, swept already)
                    string blocker = null;
                    double run = 0;
                    Vector3D approach = standoff;
                    if (!line)
                    {
                        Vector3D hullAtTarget = origin - Vector3D.TransformNormal(m.LocalPoint - HullCentreLocal, frame);
                        run = FreeRun(hullAtTarget, nw, 2.5 + DroneRadius(), out blocker);
                        approach = origin + nw * run;
                    }
                    MatrixD am = MatrixD.Identity;
                    var failure = FlyRoute(null, ref am, origin, approach, DroneRadius(), m.LocalPoint, ref frame, false, true);
                    if (failure != Orchestrator.TaskFailure.None)
                    {
                        tried.Append(top ? " top" : " side").Append(": no route (").Append(line ? "to the stand-off on the beam's line" : "approach " + run.ToString("F1") + " m out").Append(blocker != null ? ", " + blocker + " beyond" : "").Append(");");
                        continue;
                    }
                    collectorIndex = ci;
                    harvestNormal = nw;
                    harvestClear = clear;
                    harvestInto = into;
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: plot '{1}' from its {2} ({3:F2} m off it, beam {4:F0}° into it{7}), collector '{5}', {6}", Entity.EntityId, plot.DisplayNameText, top ? "top" : "side", clear, MathHelperD.ToDegrees(Math.Asin(Math.Min(1, into))), collectors[ci].CustomName, DescribeAttitude(ref frame),
                                                 line ? string.Format(", in along its line {0:F2} m", Vector3D.Distance(standoff, origin)) : "");
                    return Orchestrator.TaskFailure.None;
                }
            }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: plot '{1}': no face to harvest from:{2}", Entity.EntityId, plot.DisplayNameText, tried);
            return Orchestrator.TaskFailure.Unreachable;
        }

        // The drone's blocks as boxes in the controller frame (its real outline, not the one box round it: a collector under a
        // long nose, a beam that starts behind the bow)
        private readonly List<BoundingBoxD> hullBlocks = new List<BoundingBoxD>();
        private readonly List<IMySlimBlock> hullSlims = new List<IMySlimBlock>();

        private void HullBlockBoxes()
        {
            hullBlocks.Clear();
            for (int g = 0; g < droneGrids.Count; g++)
            {
                var grid = droneGrids[g];
                hullSlims.Clear();
                grid.GetBlocks(hullSlims);
                double gs = grid.GridSize;
                for (int i = 0; i < hullSlims.Count; i++)
                {
                    var s = hullSlims[i];
                    var lb = new BoundingBoxD(((Vector3D)s.Min - 0.5) * gs, ((Vector3D)s.Max + 0.5) * gs);
                    var box = BoundingBoxD.CreateInvalid();
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3D c = new Vector3D((k & 1) != 0 ? lb.Max.X : lb.Min.X, (k & 2) != 0 ? lb.Max.Y : lb.Min.Y, (k & 4) != 0 ? lb.Max.Z : lb.Min.Z);
                        Vector3D w = Vector3D.Transform(c, grid.WorldMatrix);
                        box.Include(Vector3D.TransformNormal(w - flightState.Position, flightState.WorldMatrixToLocal));
                    }
                    hullBlocks.Add(box);
                }
                hullSlims.Clear();
            }
        }

        // The plot's soil (centre) and rim (just inside its side edges) heights over its centre, along 'upW': rays down
        // against its physics. None hit: its cell's top for both (as a solid block).
        private readonly List<IHitInfo> plotHits = new List<IHitInfo>();

        private void PlotHeights(IMyCubeBlock plot, Vector3D centre, Vector3D upW, double halfUp, out double soilUp, out double rimUp)
        {
            soilUp = rimUp = double.NaN;
            var grid = plot.CubeGrid;
            // the plot's own level axes (a square footprint: any two will do for the centre; the edges need its axes)
            Vector3D[] axes = { grid.WorldMatrix.Right, grid.WorldMatrix.Up, grid.WorldMatrix.Backward };
            Vector3I size = plot.Max - plot.Min + Vector3I.One;
            double[] halves = { size.X * grid.GridSize * 0.5, size.Y * grid.GridSize * 0.5, size.Z * grid.GridSize * 0.5 };
            for (int s = -1; s < 4; s++)
            {
                Vector3D at = centre;
                if (s >= 0)
                {
                    int k = 0, found = -1;
                    for (int i = 0; i < 3; i++) if (Math.Abs(Vector3D.Dot(axes[i], upW)) < 0.5 && k++ == s / 2) { found = i; break; }
                    if (found < 0) continue;
                    at += axes[found] * ((s % 2 == 0 ? 1 : -1) * (halves[found] - 0.1));
                }
                Vector3D from = at + upW * (halfUp - 0.02), to = at - upW * halfUp;
                plotHits.Clear();
                MyAPIGateway.Physics.CastRay(from, to, plotHits);
                double best = double.NaN;
                for (int h = 0; h < plotHits.Count; h++)
                {
                    var g = plotHits[h].HitEntity != null ? plotHits[h].HitEntity.GetTopMostParent() as IMyCubeGrid : null;
                    if (g != grid) continue;
                    var slim = grid.GetCubeBlock(grid.WorldToGridInteger(plotHits[h].Position - upW * 0.02));
                    if (slim == null || slim.FatBlock == null || slim.FatBlock.EntityId != plot.EntityId) continue;
                    double up = Vector3D.Dot(plotHits[h].Position - centre, upW);
                    if (double.IsNaN(best) || up > best) best = up;
                }
                plotHits.Clear();
                if (double.IsNaN(best)) continue;
                if (s < 0) soilUp = best;
                else if (double.IsNaN(rimUp) || best > rimUp) rimUp = best;
            }
            if (double.IsNaN(rimUp)) rimUp = double.IsNaN(soilUp) ? halfUp : soilUp;
            if (double.IsNaN(soilUp)) soilUp = rimUp;
            rimUp = Math.Max(rimUp, soilUp);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: plot '{1}': soil {2:F2} m, rim {3:F2} m under its cell's top", Entity.EntityId, plot.DisplayNameText, halfUp - soilUp, halfUp - rimUp);
        }

        // In along the beam's line: the work point with the beam's tip a little past 'target' (deepest first); a stand-off
        // back along the line where the drone's blocks are clear over the plot's solid part ('solid'); the leg between swept
        // with the drone's real shape against the real physics (the plot's soil and rim, a neighbour, a wall).
        private bool LineIn(Vector3D target, Vector3D beam, ref MatrixD frame, Vector3D localPoint, MyOrientedBoundingBoxD solid,
                            out Vector3D work, out Vector3D standoff, out string why)
        {
            work = standoff = Vector3D.Zero;
            why = "?";
            Vector3D off = Vector3D.TransformNormal(localPoint - HullCentreLocal, frame);
            for (int k = 0; k < HARVEST_LINE_REACH.Length; k++)
            {
                work = target - beam * (COLLECTOR_BEAM - HARVEST_LINE_REACH[k]);
                double back = 0;
                while (back <= HARVEST_LINE_BACK && !HullClearOf(solid, work - beam * back, ref frame, localPoint)) back += 0.1;
                if (back > HARVEST_LINE_BACK) { why = string.Format("no stand-off on the beam's line within {0:F0} m with the drone clear over the plot", HARVEST_LINE_BACK); continue; }
                standoff = work - beam * (back + HARVEST_LINE_RUN);
                string blocker;
                if (!SweepClear(ref frame, standoff - off, work - off, HARVEST_LINE_BERTH, true, out blocker)) { why = "in along the beam's line: " + blocker + " in the way"; continue; }
                // The drone gets to the stand-off level (its direct legs checked that), then turns to the work attitude
                // there: a long drone tilting nose down swings its nose down and its tail up (a roof, the plots)
                MatrixD level = LevelAttitude();
                if (!TurnClear(ref level, ref frame, standoff - off, out blocker)) { why = "turning to the work attitude at the stand-off: " + blocker + " in the way"; continue; }
                return true;
            }
            return false;
        }

        // Harvesters, indoors, a dock with no corridor: the approach point straight out along the connector's axis as far as
        // the real physics leaves room (at most COMPACT_DOCK_OUT, at least COMPACT_DOCK_MIN), where the drone, arriving
        // level, can turn to the dock attitude (checked against the real physics too). 'dockedHull': the hull centre docked.
        private const double COMPACT_DOCK_MIN = 0.5;
        private const double COMPACT_DOCK_OUT = 3.0;

        private bool CompactDockApproach(Vector3D dockedHull, Vector3D axis, ref MatrixD dockFrame, out double outDist, out string why)
        {
            outDist = 0;
            why = null;
            if (localHullBox.Max.X < localHullBox.Min.X) return false;
            CaptureFlightState();
            RefreshDroneGrids();
            string blocker;
            double run;
            if (!SweepClear(ref dockFrame, dockedHull, dockedHull + axis * COMPACT_DOCK_OUT, DIRECT_BERTH, true, out blocker, out run))
                run = Math.Max(0, run - 0.05);
            if (run < COMPACT_DOCK_MIN) { why = "straight out along the axis: " + blocker + " in the way"; return false; }
            MatrixD level = LevelAttitude();
            for (double d = run; d >= COMPACT_DOCK_MIN - 1e-6; d -= 0.25)
            {
                if (TurnClear(ref level, ref dockFrame, dockedHull + axis * d, out blocker)) { outDist = d; return true; }
                why = string.Format("turning to the dock attitude {0:F1} m out: {1} in the way", d, blocker);
            }
            return false;
        }

        // The drone's attitude on its way (direct legs): level, its heading now
        private MatrixD LevelAttitude()
        {
            Vector3D up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            Vector3D fwd = flightState.WorldMatrix.Forward - up * Vector3D.Dot(flightState.WorldMatrix.Forward, up);
            if (fwd.LengthSquared() < 1e-3) fwd = flightState.WorldMatrix.Down - up * Vector3D.Dot(flightState.WorldMatrix.Down, up);
            return MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(fwd), up);
        }

        // Turning in place about the hull centre 'centre' from 'a' to 'b': half way and at the end, the drone's real shape
        // clear of the real physics (the start is the arrival, checked by its route)
        private const double POSE_SHELL = 0.3;   // m: a pose is checked as the shell this deep inside the drone's surface

        private bool TurnClear(ref MatrixD a, ref MatrixD b, Vector3D centre, out string blocker)
        {
            blocker = null;
            Quaternion qa = Quaternion.CreateFromRotationMatrix(a), qb = Quaternion.CreateFromRotationMatrix(b);
            for (int s = 1; s <= 2; s++)
            {
                MatrixD att = MatrixD.CreateFromQuaternion(Quaternion.Slerp(qa, qb, s * 0.5f));
                if (!PoseClear(ref att, centre, HARVEST_LINE_BERTH, out blocker)) return false;
            }
            return true;
        }

        // The drone's real shape at hull centre 'centre', attitude 'att', clear of the real physics: from each side, the
        // shell just inside its surface swept onto the pose
        private bool PoseClear(ref MatrixD att, Vector3D centre, double berth, out string blocker)
        {
            Vector3D[] dirs = { att.Right, att.Left, att.Up, att.Down, att.Forward, att.Backward };
            for (int i = 0; i < 6; i++)
                if (!SweepClear(ref att, centre - dirs[i] * POSE_SHELL, centre, berth, false, out blocker)) return false;
            blocker = null;
            return true;
        }

        // None of the drone's blocks in 'box' with its 'localPoint' (the beam's origin) at 'at', attitude 'frame'
        private bool HullClearOf(MyOrientedBoundingBoxD box, Vector3D at, ref MatrixD frame, Vector3D localPoint)
        {
            var rot = Quaternion.CreateFromRotationMatrix(frame);
            for (int i = 0; i < hullBlocks.Count; i++)
            {
                var b = hullBlocks[i];
                var ob = new MyOrientedBoundingBoxD(at + Vector3D.TransformNormal(b.Center - localPoint, frame), b.HalfExtents, rot);
                if (ob.Intersects(ref box)) return false;
            }
            return true;
        }

        // Done at a plot: straight back out along the face's normal until the beam's tip is HARVEST_LIFT_CLEAR off it (a
        // BackOut after the Harvest task), not up to the route's usual heights
        private void LiftOffPlot()
        {
            if (collectorIndex < 0 || collectorIndex >= collectorMounts.Count || harvestNormal.LengthSquared() < 0.5) return;
            CaptureFlightState();
            var m = collectorMounts[collectorIndex];
            Vector3D origin = flightState.Position + Vector3D.TransformNormal(m.LocalPoint, flightState.WorldMatrix);
            double lift = Math.Max(0.5, COLLECTOR_BEAM * harvestInto - harvestClear + HARVEST_LIFT_CLEAR);   // the beam's tip, as deep as it went, then 1 m clear
            currentJob.Tasks.Insert(taskIndex + 1, Orchestrator.Task.BackOut(0, origin + harvestNormal * lift));
            if (Log.DebugEnabled) Log.Debug("Drone {0}: harvest: backing off {1:F2} m (the beam's tip {2:F1} m clear)", Entity.EntityId, lift, HARVEST_LIFT_CLEAR);
        }

        #region Indoor legs (harvesters)
        private const double DIRECT_BERTH = 0.25;   // m kept clear on the direct legs (the drone held level, a known room)
        private const double DIRECT_LIFT_STEP = 0.5;   // m: up, across, down: the cruise height over the higher end in these steps
        private const double DIRECT_LIFT_MAX = 4.0;    // ...up to this

        /// <summary>
        /// Harvesters, indoors: before the grid search (its cells and the sphere round the hull are too coarse for a room),
        /// plain legs with the drone held level and its heading as it is: straight to the goal; level across over it, then
        /// straight down (or up); straight up (or down) to its height, then level across. Each leg swept with the drone's
        /// own box against the real physics (SweepClear), DIRECT_BERTH clear. True: 'points' has the waypoints between (goal
        /// excluded) and the legs' attitudes are set.
        /// </summary>
        private bool TryDirectLegs(Vector3D start, Vector3D goal, List<Vector3D> points, bool looseGoal)
        {
            if (localHullBox.Max.X < localHullBox.Min.X) return false;
            CaptureFlightState();
            RefreshDroneGrids();
            ownDepths.Clear();
            MatrixD att = LevelAttitude();
            Vector3D up = att.Up, fwd = att.Forward;
            Vector3D rise = up * Vector3D.Dot(goal - start, up);
            var mids = new List<Vector3D>();
            for (int shape = 0; shape < 3; shape++)
            {
                mids.Clear();
                if (shape == 1) mids.Add(goal - rise);          // level across first
                else if (shape == 2) mids.Add(start + rise);    // up / down first
                if (mids.Count > 0 && (Vector3D.DistanceSquared(mids[0], start) < 0.25 || Vector3D.DistanceSquared(mids[0], goal) < 0.25)) continue;
                bool clear = true;
                Vector3D a = start;
                string why = null;
                for (int i = 0; i <= mids.Count && clear; i++)
                {
                    Vector3D b = i < mids.Count ? mids[i] : goal;
                    bool last = i == mids.Count;
                    if (!SweepClear(ref att, a, b, DIRECT_BERTH, !(last && looseGoal), out why)) clear = false;
                    a = b;
                }
                if (!clear)
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: direct legs ({1}): blocked by {2}", Entity.EntityId, shape == 0 ? "straight" : shape == 1 ? "across, then down / up" : "down / up, then across", why);
                    continue;
                }
                legOffset = points.Count;
                legForwards.Clear();
                legUps.Clear();
                points.AddRange(mids);
                for (int i = 0; i <= mids.Count; i++) { legForwards.Add(fwd); legUps.Add(up); }
                if (Log.DebugEnabled) Log.Debug("Drone {0}: direct legs ({1}), {2:F1} m, the drone held level", Entity.EntityId, shape == 0 ? "straight" : shape == 1 ? "across, then down / up" : "down / up, then across", Vector3D.Distance(start, goal));
                return true;
            }
            // Over what is in the way (the plots between, the drone low beside one after a side harvest): up, level across
            // at a cruise height over the higher end, down
            double hGoal = Vector3D.Dot(goal - start, up), over = Math.Max(0, hGoal);
            string liftWhy = null;
            for (double lift = DIRECT_LIFT_STEP; lift <= DIRECT_LIFT_MAX + 1e-6; lift += DIRECT_LIFT_STEP)
            {
                Vector3D a1 = start + up * (over + lift), b1 = goal + up * (over + lift - hGoal);
                if (!SweepClear(ref att, start, a1, DIRECT_BERTH, true, out liftWhy)) { liftWhy = "going up: " + liftWhy; break; }   // a ceiling: no higher
                if (!SweepClear(ref att, a1, b1, DIRECT_BERTH, true, out liftWhy)) { liftWhy = "across: " + liftWhy; continue; }
                if (!SweepClear(ref att, b1, goal, DIRECT_BERTH, !looseGoal, out liftWhy)) { liftWhy = "coming down: " + liftWhy; break; }
                legOffset = points.Count;
                legForwards.Clear();
                legUps.Clear();
                points.Add(a1);
                points.Add(b1);
                for (int i = 0; i < 3; i++) { legForwards.Add(fwd); legUps.Add(up); }
                if (Log.DebugEnabled) Log.Debug("Drone {0}: direct legs (up {1:F1} m, across, down), {2:F1} m, the drone held level", Entity.EntityId, over + lift, Vector3D.Distance(start, goal));
                return true;
            }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: direct legs (up, across, down): blocked {1}", Entity.EntityId, liftWhy ?? "?");
            return false;
        }
        // Against the real physics, not cell boxes (a window's cell is mostly air, a farm plot's rim sits low): the drone's
        // box held at 'att', its centre moved a -> b, swept by rays from its leading faces along the leg, a grid every
        // SWEEP_STEP across the faces grown by the berth. 'ahead': the berth in front too (a goal right next to something:
        // the box itself only). Hits on the drone's own grids don't count. 'blocker': what is in the way; 'along': how far
        // the drone gets along the leg before it (the leg's length when clear).
        private const double SWEEP_STEP = 0.4;
        private readonly List<IHitInfo> sweepHits = new List<IHitInfo>();

        // Per leading face (axis, sign): how deep behind the box's face the drone's own surface is on each line, measured
        // where the drone is now (its physics is there; the legs keep its attitude): NaN where nothing of it is; the margin
        // lines outside the box as the line next to them inside. Cleared for each route (TryDirectLegs).
        private readonly Dictionary<int, double[]> ownDepths = new Dictionary<int, double[]>();

        private double[] OwnDepths(int f, bool positive, int n1, int n2, double h1, double h2, double[] half)
        {
            int key = f * 2 + (positive ? 1 : 0) + 8 * ((int)Math.Round(h1 * 100) * 2048 + (int)Math.Round(h2 * 100));
            double[] depths;
            if (ownDepths.TryGetValue(key, out depths) && depths.Length == n1 * n2) return depths;
            depths = new double[n1 * n2];
            var wm = flightState.WorldMatrix;
            Vector3D[] cur = { wm.Right, wm.Up, wm.Backward };
            Vector3D n = cur[f] * (positive ? 1 : -1);
            int f1 = (f + 1) % 3, f2 = (f + 2) % 3;
            Vector3D face = HullCentreWorld + n * half[f];
            // The lines through the box: from the drone's own surface (NaN: none of it there)
            int iLo = n1, iHi = -1, jLo = n2, jHi = -1;
            for (int i = 0; i < n1; i++) if (Math.Abs(-h1 + 2 * h1 * i / (n1 - 1)) < half[f1]) { iLo = Math.Min(iLo, i); iHi = i; }
            for (int j = 0; j < n2; j++) if (Math.Abs(-h2 + 2 * h2 * j / (n2 - 1)) < half[f2]) { jLo = Math.Min(jLo, j); jHi = j; }
            for (int i = iLo; i <= iHi; i++)
                for (int j = jLo; j <= jHi; j++)
                {
                    double u = -h1 + 2 * h1 * i / (n1 - 1), v = -h2 + 2 * h2 * j / (n2 - 1);
                    double depth;
                    if (!OwnSurface(face + cur[f1] * u + cur[f2] * v, -n, 2 * half[f], out depth)) depth = double.NaN;
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
            ownDepths[key] = depths;
            return depths;
        }

        // How far from 'from' along 'dir' (into the drone's box) its own grids start; false: none within 'max'
        private bool OwnSurface(Vector3D from, Vector3D dir, double max, out double depth)
        {
            depth = 0;
            sweepHits.Clear();
            MyAPIGateway.Physics.CastRay(from, from + dir * max, sweepHits);
            double best = double.MaxValue;
            for (int k = 0; k < sweepHits.Count; k++)
            {
                var e = sweepHits[k].HitEntity;
                var g = e != null ? e.GetTopMostParent() as IMyCubeGrid : null;
                if (g == null || !droneGrids.Contains(g)) continue;
                best = Math.Min(best, Vector3D.Dot(sweepHits[k].Position - from, dir));
            }
            sweepHits.Clear();
            if (best == double.MaxValue) return false;
            depth = Math.Max(0, best);
            return true;
        }

        private bool SweepClear(ref MatrixD att, Vector3D a, Vector3D b, double berth, bool ahead, out string blocker)
        {
            double along;
            return SweepClear(ref att, a, b, berth, ahead, out blocker, out along);
        }

        private bool SweepClear(ref MatrixD att, Vector3D a, Vector3D b, double berth, bool ahead, out string blocker, out double along)
        {
            blocker = null;
            Vector3D d = b - a;
            double len = d.Length();
            along = len;
            if (len < 0.05) return true;
            d /= len;
            Vector3D hull = localHullBox.HalfExtents;
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
                double[] depths = OwnDepths(f, s > 0, n1, n2, h1, h2, half);
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
                            if (g != null && droneGrids.Contains(g)) continue;
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
        #endregion

        // Every cell outside the block's face 'n' is free (no neighbour in the way of the beam / the drone)
        private static bool FaceExposed(IMyCubeGrid grid, Vector3I min, Vector3I max, Vector3I n)
        {
            for (int x = min.X; x <= max.X; x++)
                for (int y = min.Y; y <= max.Y; y++)
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        var c = new Vector3I(x, y, z);
                        var o = c + n;
                        if (o.X >= min.X && o.X <= max.X && o.Y >= min.Y && o.Y <= max.Y && o.Z >= min.Z && o.Z <= max.Z) continue;   // inside the block
                        if (grid.CubeExists(o)) return false;
                    }
            return true;
        }

        // What the collector's beam would hit first now (the game casts the same ray): this plot, else why not
        private bool BeamOnTarget(IMyCubeBlock plot, out string why)
        {
            why = null;
            CaptureFlightState();
            var m = collectorMounts[collectorIndex];
            Vector3D from = flightState.Position + Vector3D.TransformNormal(m.LocalPoint, flightState.WorldMatrix);
            Vector3D dir = Vector3D.TransformNormal(m.LocalForward, flightState.WorldMatrix);
            if (dir.LengthSquared() < 1e-6) { why = "no beam direction"; return false; }
            dir.Normalize();
            IHitInfo hit;
            if (!MyAPIGateway.Physics.CastRay(from, from + dir * COLLECTOR_BEAM, out hit, 29) || hit == null)
            {
                why = "the beam reaches nothing";
                return false;
            }
            var grid = hit.HitEntity as IMyCubeGrid;
            if (grid == null) { why = "the beam hits " + (hit.HitEntity != null ? hit.HitEntity.GetType().Name : "nothing"); return false; }
            if (droneGrids.Contains(grid)) { why = "the beam hits the drone itself"; return false; }
            var slim = grid.GetCubeBlock(grid.WorldToGridInteger(hit.Position + dir * 1e-5));
            if (slim == null || slim.FatBlock == null || slim.FatBlock.EntityId != plot.EntityId)
            {
                why = "the beam hits " + (slim != null && slim.FatBlock != null ? "'" + slim.FatBlock.DisplayNameText + "'" : "another block") + " first";
                return false;
            }
            return true;
        }
        #endregion
    }
}
