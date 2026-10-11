using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Inventory;
using Automata.Mining;
using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Mining and ore survey, server side.
    ///  - Mine task: the drone needs an ore detector. At the site it scans first (no ore of the job left there: the job
    ///    ends, NoOre); the job's position is only where to look, the bore aims at what its own detector finds. It bores
    ///    in terrain clearing mode (no stone in the cargo) until the drill point is MINE_COLLECT_DOWN (straight down) /
    ///    MINE_COLLECT_ANGLED (angled bore) from the nearest voxel of the ore (OreScanner.NearestOre, read live), then
    ///    collects: whatever it cuts (stone, other ores) goes in the cargo; only the job's ore counts for the surveyor.
    ///    Straight on along the bore line (extensions), so the back-out is straight back along it, attitude held. It ends
    ///    on the cargo / load limit (the drone's settings: off to deliver), or when none of the job's ore came in for
    ///    NO_ORE_TICKS (a stone crusher mod, stone collection off on the server, the ore ran out here): back out, and with
    ///    room left another bore nearby on the same ore (QueueRebore, at most MINE_REBORES_MAX), else deliver.
    ///    A deposit is depleted only when a detector scan (the drone's, at the end of each bore) no longer finds it,
    ///    never because of what was delivered.
    ///  - Natural gravity, drill facing forward / back / left / right: an angled bore (PlanMineSite), as steep as the pitch
    ///    (forward / back) or roll (left / right) limit allows, from the side where the rock is thinnest.
    ///    Its scans before and after go to the surveyors (a deposit gone is how they learn it is depleted).
    ///  - "Survey for ores": while flying, a scan every few seconds (once it moved half a scan radius); the scans wait
    ///    in the drone until a mining surveyor is in antenna range, then go to it.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrMineNoMoreOre = MyStringId.GetOrCompute("Mine: no more ore coming in");
        private static readonly MyStringId StrMineNoOreFor = MyStringId.GetOrCompute("Mine: no {0} for {1} s");
        private static readonly MyStringId StrMineStuckBackingUp = MyStringId.GetOrCompute("Mine: stuck {0:F0} m from the bore's end, backing up and trying again ({1}/{2})");
        private static readonly MyStringId StrMineCantBackUp = MyStringId.GetOrCompute("Mine: can't back up either, taking what it has");
        private static readonly MyStringId StrMineOnlyStone = MyStringId.GetOrCompute("Mine: no {0}, only stone / other ore");
        private static readonly MyStringId StrMineNothingCollected = MyStringId.GetOrCompute("Mine: nothing collected");
        private static readonly MyStringId StrMineNeedsAnOreDetector = MyStringId.GetOrCompute("Mine: needs an ore detector");
        private static readonly MyStringId StrMineNoLeftAtThe = MyStringId.GetOrCompute("Mine: no {0} left at the site");
        private static readonly MyStringId StrMineMaxLoadReached = MyStringId.GetOrCompute("Mine: max load reached");
        private static readonly MyStringId StrMineCargoFull = MyStringId.GetOrCompute("Mine: cargo full");
        private static readonly MyStringId StrMineBoreStuckMFrom = MyStringId.GetOrCompute("Mine: bore stuck {0:F0} m from its end{1}");
        private static readonly MyStringId StrTakingWhatItHas = MyStringId.GetOrCompute(", taking what it has");
        private static readonly MyStringId StrSurveyNeedsAWorkingOre = MyStringId.GetOrCompute("Survey: needs a working ore detector ({0})");
        private static readonly MyStringId StrNone = MyStringId.GetOrCompute("none");
        private static readonly MyStringId StrSwitchedOff = MyStringId.GetOrCompute("switched off");
        private static readonly MyStringId StrNotWorking = MyStringId.GetOrCompute("not working");
        private static readonly MyStringId StrSurveyOreHitSM = MyStringId.GetOrCompute("Survey: {0} ore hit(s) ({1:F0} m)");
        private static readonly MyStringId StrRecalled = MyStringId.GetOrCompute("Recalled ({0})");
        private static readonly MyStringId StrRecalledReason = MyStringId.GetOrCompute("recalled");
        private static readonly MyStringId StrSurveyForOresNeedsA = MyStringId.GetOrCompute("Survey for ores: needs a working ore detector");
        #endregion

        private const double MINE_SITE_RADIUS = 40;
        private const int MINE_SITE_MAX_CELLS = 64;
        private const double MINE_COLLECT_DOWN = 7.5;      // m from the nearest ore voxel (drill point): collecting from here on
        private const double MINE_COLLECT_ANGLED = 10;     // the same for angled bores
        private const double MINE_NEAR_RADIUS = 16;        // m around the drill point read for the nearest ore voxel
        private const double MINE_BORE_EXTEND = 8;         // m further along the bore line per extension, until full
        private const int MINE_BORE_EXTENSIONS = 12;       // at most (~100 m past the deposit): then it takes what it has
        private const double MINE_BORE_SEARCH = 150;       // m of rock an angled bore may cross to the deposit
        private const int MINE_AZIMUTHS = 16;              // angled bore directions tried around the deposit
        private const double MINE_ANGLE_MARGIN = 2;        // degrees below the pitch / roll limit
        private const double MINE_ANGLE_MAX = 85;          // degrees from level, steepest angled bore
        private const double MINE_APPROACH_OUT = 20;       // m out along the bore line: approach / back-out point
        private const int MINE_REBORES_MAX = 4;
        private const int BORE_RETRIES_MAX = 3;            // stuck: back up (a hull length) and bore on, this many times, then deliver            // other bores per job nearby once a spot gives no more of the ore
        private const double REBORE_SPACING = 10;          // m: a new bore at least this far from the drone's earlier bores
        private const float MINE_BORE_SPEED = 1f;          // m/s through rock
        #region Timing
        private const int MINE_MAX_TICKS = 20 * 3600;
        private const int BORE_STALL_TICKS = 20 * 60;   // boring: neither closer to the bore's end (0.5 m) nor cargo growing this long: stuck
        private const int SURVEY_PUBLISH_TICKS = 300;
        #endregion
        private const int SURVEY_BUFFER_MAX = 64;

        private readonly OreScanner surveyScanner = new OreScanner();
        private readonly List<OreHit> surveyHits = new List<OreHit>();
        private readonly List<OreHit> siteHits = new List<OreHit>();
        private readonly List<SurveySweep> surveyBuffer = new List<SurveySweep>();
        private Vector3D lastSurveyCenter = new Vector3D(double.MaxValue);
        private int lastSurveyFrame = int.MinValue / 2, lastSurveyPublish = int.MinValue / 2;
        private bool surveyWarned;

        // Bore in progress
        private Vector3D boreEnd, boreDir, boreDeposit;
        private MyStringHash boreOre;
        private bool boreCollecting, boreClearingSet, boreClearingWas;
        private int boreExtensions, boreGainFrame, boreProgressFrame;
        private double boreBest;
        private bool boring;   // the bore order is flying: the load limit ends the bore here, not CheckLoad
        private double boreLastCargo;
        private double boreLastOre;            // kg of the job's ore aboard at the last gain
        private int oreGainFrame;              // the job's ore last grew (stone keeps cargo growing, not this)
        private Vector3D boreStart;            // tool point where the bore began (the way back out of the hole)
        private bool boreStartSet;
        private double boreCollectAt;          // m from the nearest ore voxel: collecting
        private int boreNearCheck;             // RunMine calls until the next live ore reading
        private bool mineAngled;               // PlanMineSite planned an angled bore for this site
        private Vector3D mineAngledDir;        // its direction (into the rock)
        private readonly List<OreHit> planHits = new List<OreHit>();
        private int boreRetries;               // back-ups of this bore (stuck)
        private bool boreBackingUp;            // backing up along the bore before trying again
        private int boreBackUpFrame;
        private bool mineSiteFixed;            // the next Mine task's deposit is a rebore spot: no snapping to the nearest hit
        private int mineRebores;               // rebores queued in this job
        private readonly List<Vector3D> boreLineStarts = new List<Vector3D>();   // this job's bores (drill point start / end)
        private readonly List<Vector3D> boreLineEnds = new List<Vector3D>();

        private static Config.ServerConfig.MiningConfig MiningConfig
        {
            get { var c = AutomataSession.GetConfig(); return c != null ? c.Mining : Config.ServerConfig.Instance.Mining; }
        }

        private bool OreDetectorWorking { get { return oreDetector != null && !oreDetector.Closed && oreDetector.IsWorking; } }

        #region Mine task
        private TaskStatus RunMine(ref Orchestrator.Task task, bool starting, int elapsed)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (starting)
            {
                if (!HasTool(Orchestrator.TaskType.Mine)) return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                if (oreDetector == null || oreDetector.Closed || !oreDetector.IsFunctional)
                {
                    Report(StrMineNeedsAnOreDetector);
                    return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                }
                boreDeposit = task.Position.ToVector3D();
                boreOre = MyStringHash.NullOrEmpty;
                if (task.Inventory != null)
                    foreach (var item in task.Inventory.GetAllItems()) { boreOre = item.Key; break; }
                // Is the ore still there? (scanned in one go: a small sphere)
                siteHits.Clear();
                surveyScanner.Cancel();
                surveyScanner.RunAll(boreDeposit, MINE_SITE_RADIUS, siteHits, MINE_SITE_MAX_CELLS);
                RecordSweep(boreDeposit, MINE_SITE_RADIUS, siteHits);
                // The job's position is general: the deposit is where the drone's own detector finds the ore now
                // (a rebore spot is kept as chosen: the ore only needs to be still there)
                Vector3D live;
                bool fixedSite = mineSiteFixed;
                mineSiteFixed = false;
                if (!LiveDeposit(siteHits, boreDeposit, boreOre, out live) && !(fixedSite && OreWithin(boreDeposit, MINE_NEAR_RADIUS)))
                {
                    Report(StrMineNoLeftAtThe, boreOre.String);
                    return Fail(Orchestrator.TaskFailure.NoOre, TaskStatus.ToReturnHome);
                }
                if (!fixedSite) boreDeposit = live;
                Vector3D tool = ToolPointWorld();
                if (mineAngled) boreDir = mineAngledDir;
                else
                {
                    boreDir = boreDeposit - tool;
                    if (boreDir.LengthSquared() < 1) boreDir = flightState.WorldMatrix.Forward;
                    boreDir.Normalize();
                }
                boreEnd = tool + boreDir * (Math.Max(0, Vector3D.Dot(boreDeposit - tool, boreDir)) + 2);
                boreExtensions = 0;
                boreRetries = 0;
                boreBackingUp = false;
                boreCollectAt = mineAngled ? MINE_COLLECT_ANGLED : MINE_COLLECT_DOWN;
                boreNearCheck = 0;
                boreCollecting = OreWithin(tool, boreCollectAt);
                SetDrillClearing(!boreCollecting);
                BackOutAlongBore(tool);
                SetToolEnabled(Orchestrator.TaskType.Mine, true);
                boreLastCargo = CargoUsedVolume();
                boreGainFrame = now;
                boreLastOre = MinedOre();
                oreGainFrame = now;
                boreStart = tool;
                boreStartSet = true;
                if (!DispatchBore()) { FinishMine(); return Fail(Orchestrator.TaskFailure.Unreachable, TaskStatus.ToReturnHome); }
                Log.Debug("Drone {0}: mine {1}: boring {2:F0} m to the deposit ({3}, {4:F0}° from level), collecting within {5:F1} m of the ore{6}",
                          Entity.EntityId, boreOre, Vector3D.Distance(tool, boreDeposit), mineAngled ? "angled" : "straight",
                          flightState.InGravity ? MathHelperD.ToDegrees(Math.Asin(MathHelperD.Clamp(-Vector3D.Dot(boreDir, flightState.GravityUp), -1, 1))) : 0,
                          boreCollectAt, boreCollecting ? " (there already)" : "");
                return TaskStatus.Running;
            }
            if (!IsConstructionOrderActive()) { FinishMine(); return TaskStatus.Interrupted; }
            Vector3D at = ToolPointWorld();
            // Live: the nearest voxel of the ore from the drill point (every 3rd update), or the deposit point itself
            bool boreEndReached = constructionOrder.Arrived && !boreBackingUp;
            if (!boreCollecting && (--boreNearCheck < 0 || boreEndReached))
            {
                boreNearCheck = 2;
                if (OreWithin(at, boreCollectAt) || boreEndReached)
                {
                    boreCollecting = true;
                    SetDrillClearing(false);
                    boreGainFrame = now;
                    oreGainFrame = now;
                    Log.Debug("Drone {0}: mine: within {1:F1} m of the ore{2}: terrain clearing off, collecting", Entity.EntityId, boreCollectAt,
                              boreEndReached ? " (bore end)" : "");
                }
            }
            double cargo = CargoUsedVolume();
            if (cargo > boreLastCargo + 0.001) { boreLastCargo = cargo; boreGainFrame = now; }   // cutting (stone counts): not stuck
            double ore = MinedOre();
            if (ore > boreLastOre + 0.5) { boreLastOre = ore; oreGainFrame = now; }              // the job's ore coming in
            // Ore is heavy: in gravity the load limit usually comes long before full containers
            if (CargoFillRatio() >= 0.95 || IsLoadLimitReached)
            {
                Report(IsLoadLimitReached ? StrMineMaxLoadReached : StrMineCargoFull);
                return EndBore(true);
            }
            // None of the job's ore for a while (only stone / other ores, or nothing: crushers, stone collection off):
            // this spot is done
            if (boreCollecting && now - oreGainFrame >= NO_ORE_TICKS)
            {
                Report(StrMineNoOreFor, boreOre.String, NO_ORE_TICKS / 60);
                return EndBore(false);
            }
            // Backing up after getting stuck: once back, on along the bore again (drill on all the while)
            if (boreBackingUp)
            {
                if (constructionOrder.Arrived)
                {
                    boreBackingUp = false;
                    if (DispatchBore()) { boreGainFrame = now; return TaskStatus.Running; }
                    return EndBore(true);
                }
                if (now - boreBackUpFrame > BORE_STALL_TICKS) { Report(StrMineCantBackUp); return EndBore(true); }
                return TaskStatus.Running;
            }
            // Stuck: pressing on rock it doesn't cut (hull wider than the bore, drill off / damaged) and nothing coming in:
            // back up a hull length and try again, BORE_RETRIES_MAX times, then deliver what it has
            double toEnd = Vector3D.Distance(at, boreEnd);
            if (toEnd < boreBest - 0.5) { boreBest = toEnd; boreProgressFrame = now; }
            if (!constructionOrder.Arrived && now - Math.Max(boreProgressFrame, boreGainFrame) > BORE_STALL_TICKS)
            {
                if (boreRetries < BORE_RETRIES_MAX && DispatchBackUp())
                {
                    boreRetries++;
                    Report(StrMineStuckBackingUp, toEnd, boreRetries, BORE_RETRIES_MAX);
                    return TaskStatus.Running;
                }
                Report(StrMineBoreStuckMFrom, toEnd, MinedOre() > 0 ? Loc.T(StrTakingWhatItHas) : "");
                return EndBore(true);
            }
            if (constructionOrder.Arrived)
            {
                // On along the same line while the ore keeps coming (the back-out stays straight)
                if (boreExtensions < MINE_BORE_EXTENSIONS)
                {
                    boreExtensions++;
                    boreEnd += boreDir * MINE_BORE_EXTEND;
                    if (DispatchBore()) return TaskStatus.Running;
                }
                Report(StrMineNoMoreOre);
                return EndBore(false);
            }
            if (elapsed > MINE_MAX_TICKS) return EndBore(true);
            return TaskStatus.Running;
        }

        /// <summary>
        /// The bore is over. 'deliver' (full, stuck, too long), or no room left, or no other spot: deliver what it has
        /// (everything aboard: stone, other ores; only the job's ore counts). Otherwise another bore nearby on the same
        /// ore: back out of this one, then the new site (QueueRebore).
        /// </summary>
        private TaskStatus EndBore(bool deliver)
        {
            boreLineStarts.Add(boreStart);
            boreLineEnds.Add(ToolPointWorld());
            FinishMine();   // its scan goes to the surveyors: a deposit no longer found is how they learn it is depleted
            bool room = CargoFillRatio() < 0.95 && !IsLoadLimitReached;
            if (!deliver && room && mineRebores < MINE_REBORES_MAX && QueueRebore()) return TaskStatus.Done;   // next: back out, the new site
            bool got = MinedOre() > 0;
            if (got) { jobBlocksDone++; return TaskStatus.Done; }
            if (CargoUsedVolume() > 0.01) return TaskStatus.Done;   // stone / other ores: delivered all the same (no claim about the deposit)
            Report(StrMineNothingCollected);
            return Fail(Orchestrator.TaskFailure.NoOre, TaskStatus.ToReturnHome);
        }

        /// <summary>
        /// Another bore on the job's ore near the last one, at least REBORE_SPACING from the drone's earlier bores: an ore
        /// hit of the last scan (FinishMine's, the drone's detector), else a spot around the last deposit where the ore
        /// still is (OreScanner.NearestOre). Same bore angle as before (angled: same direction; straight: from above).
        /// Tasks inserted after the BackOut that follows: NavigateToolTo, Mine, BackOut. False: no such spot.
        /// </summary>
        private bool QueueRebore()
        {
            int back = taskIndex + 1;
            if (back >= currentJob.Tasks.Count || currentJob.Tasks[back].Type != Orchestrator.TaskType.BackOut) return false;
            var mine = currentJob.Tasks[taskIndex];
            IMyEntity e;
            var voxel = mine.TargetEntityId != 0 && MyAPIGateway.Entities.TryGetEntityById(mine.TargetEntityId, out e) ? e as IMyVoxelBase : null;
            if (voxel == null) return false;
            Vector3D outward = mineAngled ? -mineAngledDir
                             : flightState.InGravity ? flightState.GravityUp
                             : Vector3D.Normalize(boreDeposit - voxel.PositionComp.WorldAABB.Center);
            // Candidates: the scan's hits of the ore (nearest the last deposit first), then a ring around it
            var spots = new List<Vector3D>();
            var dists = new List<double>();
            for (int i = 0; i < siteHits.Count; i++)
            {
                if (siteHits[i].Ore != boreOre) continue;
                double d = Vector3D.Distance(siteHits[i].Position, boreDeposit);
                int at = dists.Count;
                while (at > 0 && dists[at - 1] > d) at--;
                dists.Insert(at, d);
                spots.Insert(at, siteHits[i].Position);
            }
            Vector3D u = Vector3D.CalculatePerpendicularVector(outward), v = Vector3D.Cross(outward, u);
            for (int ring = 1; ring <= 2; ring++)
                for (int k = 0; k < 6; k++)
                {
                    double ang = k * Math.PI / 3 + (ring - 1) * Math.PI / 6;
                    spots.Add(boreDeposit + (u * Math.Cos(ang) + v * Math.Sin(ang)) * REBORE_SPACING * ring);
                }
            for (int i = 0; i < spots.Count; i++)
            {
                Vector3D spot = spots[i];
                if (NearEarlierBore(spot)) continue;
                Vector3D ore;
                double d;
                if (!OreScanner.NearestOre(spot, MINE_NEAR_RADIUS, boreOre, out ore, out d)) continue;   // the detector: still there?
                if (NearEarlierBore(ore)) continue;
                Vector3D entry;
                if (!OreScanner.FindSurface(voxel, ore, outward, MINE_BORE_SEARCH, out entry)) continue;
                Vector3D approach = entry + outward * MINE_APPROACH_OUT;
                var nav = Orchestrator.Task.NavigateToolTo(0, entry, approach, (float)flightState.Gravity.Length());
                var next = mine;
                next.Position = Vector3DData.FromVector3D(ore);
                next.ApproachFrom = Vector3DData.FromVector3D(entry);
                currentJob.Tasks.Insert(back + 1, nav);
                currentJob.Tasks.Insert(back + 2, next);
                currentJob.Tasks.Insert(back + 3, Orchestrator.Task.BackOut(0, approach));
                mineRebores++;
                mineSiteFixed = true;
                Log.Debug("Drone {0}: mine: another bore on {1} ({2}/{3}) {4:F0} m from the last deposit, {5:F0} m of rock", Entity.EntityId, boreOre,
                          mineRebores, MINE_REBORES_MAX, Vector3D.Distance(ore, boreDeposit), Vector3D.Distance(entry, ore));
                return true;
            }
            Log.Debug("Drone {0}: mine: no other spot of {1} near the last deposit", Entity.EntityId, boreOre);
            return false;
        }

        // Within REBORE_SPACING of one of this job's bores (a segment, drill point start to end)
        private bool NearEarlierBore(Vector3D p)
        {
            for (int i = 0; i < boreLineStarts.Count; i++)
            {
                Vector3D a = boreLineStarts[i], ab = boreLineEnds[i] - a;
                double len2 = ab.LengthSquared();
                double t = len2 > 1e-6 ? MathHelperD.Clamp(Vector3D.Dot(p - a, ab) / len2, 0, 1) : 0;
                if (Vector3D.DistanceSquared(p, a + ab * t) < REBORE_SPACING * REBORE_SPACING) return true;
            }
            return false;
        }

        /// <summary>
        /// In gravity, a route starting in the ground (a bore hole) first climbs out: back along the bore to where it
        /// began when the drone is in it, then straight up until the hull clears the surface. Points appended, 'start'
        /// moved to the last one. False: already clear.
        /// </summary>
        private bool ClimbOut(ref Vector3D start, double hull, List<Vector3D> points)
        {
            if (!flightState.InGravity) return false;
            double need = hull + CONSTRUCTION_CLEARANCE;
            double h = PathfindingManager.HeightAboveTerrain(start);
            if (h == double.MaxValue || h >= need) return false;
            Vector3D up = flightState.GravityUp;
            if (boreStartSet && boreDir.LengthSquared() > 0.5)
            {
                // In the bore (near its line, past its start): out the way it came
                Vector3D tool = ToolPointWorld();
                Vector3D rel = tool - boreStart;
                double along = Vector3D.Dot(rel, boreDir);
                if (along > 0.5 && (rel - boreDir * along).Length() < hull)
                {
                    start += boreStart - tool;
                    points.Add(start);
                    h = PathfindingManager.HeightAboveTerrain(start);
                    if (h == double.MaxValue) h = need;
                }
            }
            if (h < need)
            {
                start += up * (need - h + 1);
                points.Add(start);
            }
            Log.Debug("Drone {0}: route starts in the ground: climbing out first ({1} point(s))", Entity.EntityId, points.Count);
            return true;
        }

        // The job's deposit as the drone's detector sees it: the hit of the ore nearest the job's position (within the
        // merge distance). False: none there (depleted)
        private static bool LiveDeposit(List<OreHit> hits, Vector3D jobDeposit, MyStringHash ore, out Vector3D at)
        {
            at = jobDeposit;
            double merge = MiningConfig.DepositMergeMeters, best = merge * merge;
            bool found = false;
            for (int i = 0; i < hits.Count; i++)
            {
                if (ore != MyStringHash.NullOrEmpty && hits[i].Ore != ore) continue;
                double d2 = Vector3D.DistanceSquared(hits[i].Position, jobDeposit);
                if (d2 > best) continue;
                best = d2;
                at = hits[i].Position;
                found = true;
            }
            return found;
        }

        // A voxel of the job's ore within 'distance' of 'point' (the drill point), read live from the voxels
        private bool OreWithin(Vector3D point, double distance)
        {
            Vector3D near;
            double d;
            return OreScanner.NearestOre(point, Math.Max(distance, MINE_NEAR_RADIUS), boreOre, out near, out d) && d <= distance;
        }

        // The BackOut after the bore: straight back along the bore line, as far out as it was planned (attitude held)
        private void BackOutAlongBore(Vector3D tool)
        {
            if (taskIndex + 1 >= currentJob.Tasks.Count) return;
            var back = currentJob.Tasks[taskIndex + 1];
            if (back.Type != Orchestrator.TaskType.BackOut || back.RelativeBeaconEntityId != 0) return;
            double out_ = Math.Max(MINE_APPROACH_OUT, Vector3D.Distance(tool, back.Position.ToVector3D()));
            back.Position = back.ApproachFrom = Vector3DData.FromVector3D(tool - boreDir * out_);
            currentJob.Tasks[taskIndex + 1] = back;
        }

        /// <summary>
        /// NavigateToolTo before a Mine task, natural gravity, drill facing forward / back / left / right: an angled bore.
        /// As steep as the pitch (forward / back) or roll (left / right) limit allows; of MINE_AZIMUTHS directions around
        /// the deposit (found by the drone's detector), the one crossing the least rock (nearer the drone on a tie). The
        /// site (entry point, approach out along the line), the Mine task and the BackOut follow the line. Drill facing
        /// down (or up), space, nothing found: the planned (straight) site. It is the player's part that the limits, thrust
        /// and cargo let the drone get in and back out at that angle.
        /// </summary>
        private void PlanMineSite(ref Orchestrator.Task nav, ref Orchestrator.Task mine)
        {
            mineAngled = false;
            if (!flightState.InGravity || drill == null || drillMount.LocalForward.LengthSquared() < 0.5) return;
            if (nav.RelativeBeaconEntityId != 0 || taskIndex + 1 >= currentJob.Tasks.Count) return;
            var face = Base6Directions.GetClosestDirection((Vector3)drillMount.LocalForward);
            if (face == Base6Directions.Direction.Down || face == Base6Directions.Direction.Up) return;
            IMyEntity e;
            var voxel = mine.TargetEntityId != 0 && MyAPIGateway.Entities.TryGetEntityById(mine.TargetEntityId, out e) ? e as IMyVoxelBase : null;
            if (voxel == null) return;
            MyStringHash ore = MyStringHash.NullOrEmpty;
            if (mine.Inventory != null) foreach (var item in mine.Inventory.GetAllItems()) { ore = item.Key; break; }
            Vector3D deposit = mine.Position.ToVector3D();
            if (oreDetector != null && !oreDetector.Closed && oreDetector.IsFunctional)
            {
                planHits.Clear();
                surveyScanner.Cancel();
                surveyScanner.RunAll(deposit, MINE_SITE_RADIUS, planHits, MINE_SITE_MAX_CELLS);
                Vector3D live;
                if (LiveDeposit(planHits, deposit, ore, out live)) deposit = live;
                planHits.Clear();
            }
            bool sideways = face == Base6Directions.Direction.Left || face == Base6Directions.Direction.Right;
            double limit = settings != null && settings.AlignToPGravity
                         ? (sideways ? settings.PGravityAlignMaxRollDegrees : settings.PGravityAlignMaxPitchDegrees) : MINE_ANGLE_MAX;
            limit = Math.Min(limit, maxWorkTiltDegrees);
            double angle = MathHelperD.ToRadians(MathHelperD.Clamp(limit - MINE_ANGLE_MARGIN, 0, MINE_ANGLE_MAX));
            Vector3D up = flightState.GravityUp;
            Vector3D e1 = Vector3D.CalculatePerpendicularVector(up), e2 = Vector3D.Cross(up, e1);
            Vector3D bestDir = Vector3D.Zero, bestEntry = Vector3D.Zero;
            double bestScore = double.MaxValue;
            for (int k = 0; k < MINE_AZIMUTHS; k++)
            {
                double az = k * MathHelperD.TwoPi / MINE_AZIMUTHS;
                Vector3D dir = (e1 * Math.Cos(az) + e2 * Math.Sin(az)) * Math.Cos(angle) - up * Math.Sin(angle);
                Vector3D entry;
                if (!OreScanner.FindSurface(voxel, deposit, -dir, MINE_BORE_SEARCH, out entry)) continue;
                double score = Vector3D.Distance(entry, deposit) + 0.05 * Vector3D.Distance(entry, flightState.Position);
                if (score >= bestScore) continue;
                bestScore = score;
                bestDir = dir;
                bestEntry = entry;
            }
            if (bestScore == double.MaxValue)
            {
                Log.Debug("Drone {0}: mine: no angled way in within {1:F0} m at {2:F0}° ({3} drill): the planned site", Entity.EntityId,
                          MINE_BORE_SEARCH, MathHelperD.ToDegrees(angle), face);
                return;
            }
            // Pooled miners: the orchestrator moved this drone's site (OffsetSite) away from the planned one (the Mine
            // task's ApproachFrom): a parallel bore, the same offset across the line
            Vector3D offset = nav.Position.ToVector3D() - mine.ApproachFrom.ToVector3D();
            offset -= bestDir * Vector3D.Dot(offset, bestDir);
            if (offset.LengthSquared() > 0.25)
            {
                Vector3D shiftedEntry;
                if (OreScanner.FindSurface(voxel, deposit + offset, -bestDir, MINE_BORE_SEARCH, out shiftedEntry))
                {
                    deposit += offset;
                    bestEntry = shiftedEntry;
                }
            }
            Vector3D approach = bestEntry - bestDir * MINE_APPROACH_OUT;
            nav.Position = Vector3DData.FromVector3D(bestEntry);
            nav.ApproachFrom = Vector3DData.FromVector3D(approach);
            currentJob.Tasks[taskIndex] = nav;
            mine.ApproachFrom = Vector3DData.FromVector3D(bestEntry);
            mine.Position = Vector3DData.FromVector3D(deposit);
            currentJob.Tasks[taskIndex + 1] = mine;
            if (taskIndex + 2 < currentJob.Tasks.Count && currentJob.Tasks[taskIndex + 2].Type == Orchestrator.TaskType.BackOut)
            {
                var back = currentJob.Tasks[taskIndex + 2];
                back.Position = back.ApproachFrom = Vector3DData.FromVector3D(approach);
                currentJob.Tasks[taskIndex + 2] = back;
            }
            mineAngled = true;
            mineAngledDir = bestDir;
            Log.Debug("Drone {0}: mine: angled bore {1:F0}° from level ({2} drill, limit {3:F0}°), {4:F0} m of rock from {5}", Entity.EntityId,
                      MathHelperD.ToDegrees(angle), face, limit, Vector3D.Distance(bestEntry, deposit), bestEntry);
        }

        private Vector3D ToolPointWorld()
        {
            CaptureFlightState();
            return flightState.Position + Vector3D.TransformNormal(drillMount.LocalPoint, flightState.WorldMatrix);
        }

        // Straight along the bore line from where the drill is, attitude held, slowly (it cuts its way)
        private bool DispatchBore()
        {
            Vector3D here = ToolPointWorld();
            var order = OrderGoTo(boreEnd, here);
            if (order == null) return false;
            order.TransitStart = order.ApproachFrom;
            order.HoldAttitudeInTransit = true;
            order.ReferenceOffset = Vector3DData.FromVector3D(drillMount.LocalPoint);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = Math.Min(MINE_BORE_SPEED, SafeSpeed);
            order.SpeedCap = order.FinalSpeed;   // all the way, not only the last metres: it cuts its way
            order.ArrivalTolerance = 0.5f;
            MatrixD none = MatrixD.Identity;
            SetExplicitAttitude(order, true, ref none, flightState.WorldMatrix.Forward, flightState.WorldMatrix.Up);
            constructionOrder = order;
            constructionLegs.Clear();
            boring = true;
            boreBest = Vector3D.Distance(here, boreEnd);
            boreProgressFrame = MyAPIGateway.Session.GameplayFrameCounter;
            return true;
        }

        // Stuck: straight back along the bore by a hull length (at least 4 m, not past where it began), attitude held,
        // drill on; then DispatchBore again
        private bool DispatchBackUp()
        {
            Vector3D here = ToolPointWorld();
            double back = Math.Max(4, DroneLength());
            if (boreStartSet) back = Math.Min(back, Math.Max(1, Vector3D.Dot(here - boreStart, boreDir)));
            var order = OrderGoTo(here - boreDir * back, here);
            if (order == null) return false;
            order.TransitStart = order.ApproachFrom;
            order.HoldAttitudeInTransit = true;
            order.ReferenceOffset = Vector3DData.FromVector3D(drillMount.LocalPoint);
            order.LineTolerance = CONSTRUCTION_LINE_TOLERANCE;
            order.FinalSpeed = Math.Min(MINE_BORE_SPEED * 2, SafeSpeed);
            order.SpeedCap = order.FinalSpeed;
            order.ArrivalTolerance = 0.5f;
            MatrixD none = MatrixD.Identity;
            SetExplicitAttitude(order, true, ref none, flightState.WorldMatrix.Forward, flightState.WorldMatrix.Up);
            constructionOrder = order;
            constructionLegs.Clear();
            boring = true;
            boreBackingUp = true;
            boreBackUpFrame = MyAPIGateway.Session.GameplayFrameCounter;
            return true;
        }

        // Drill off, its mode back, what it holds counted for the report, the site scanned again for the surveyors
        private void FinishMine()
        {
            boring = false;
            boreCollecting = false;   // the survey (suppressed while collecting) runs again
            SetToolEnabled(Orchestrator.TaskType.Mine, false);
            RestoreDrillClearing();
            minedOreKg = Math.Max(minedOreKg, MinedOre());
            siteHits.Clear();
            surveyScanner.Cancel();
            if (oreDetector != null && !oreDetector.Closed && oreDetector.IsFunctional)
            {
                surveyScanner.RunAll(boreDeposit, MINE_SITE_RADIUS, siteHits, MINE_SITE_MAX_CELLS);
                RecordSweep(boreDeposit, MINE_SITE_RADIUS, siteHits);
            }
            Log.Debug("Drone {0}: mine done: {1:F0} kg of {2} aboard", Entity.EntityId, minedOreKg, boreOre.String);
        }

        private void SetDrillClearing(bool on)
        {
            if (drill == null) return;
            if (!boreClearingSet) { boreClearingWas = drill.TerrainClearingMode; boreClearingSet = true; }
            for (int i = 0; i < drills.Count; i++)
                if (drills[i] != null && !drills[i].Closed && drills[i].TerrainClearingMode != on) drills[i].TerrainClearingMode = on;
        }

        private void RestoreDrillClearing()
        {
            if (!boreClearingSet) return;
            boreClearingSet = false;
            for (int i = 0; i < drills.Count; i++)
                if (drills[i] != null && !drills[i].Closed && drills[i].TerrainClearingMode != boreClearingWas) drills[i].TerrainClearingMode = boreClearingWas;
        }

        // kg of the job's ore aboard (cargo and the drill's own inventory)
        private double MinedOre()
        {
            if (boreOre == MyStringHash.NullOrEmpty) return 0;
            double kg = 0;
            for (int c = 0; c < cargoContainers.Count + drills.Count; c++)   // cargo and every drill's own inventory
            {
                var inv = c < cargoContainers.Count ? cargoContainers[c].GetInventory(0)
                        : drills[c - cargoContainers.Count] != null && !drills[c - cargoContainers.Count].Closed ? drills[c - cargoContainers.Count].GetInventory(0) : null;
                if (inv == null) continue;
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = 0; i < itemBuffer.Count; i++)
                    if (ItemKeys.Of(itemBuffer[i].Type) == boreOre) kg += (double)itemBuffer[i].Amount;
            }
            itemBuffer.Clear();
            return kg;
        }
        #endregion

        #region Survey task (scout jobs) and recall
        private readonly OreScanner taskScanner = new OreScanner();
        private readonly List<OreHit> taskHits = new List<OreHit>();
        private int surveysDone;

        // Fly-by ore scan: the scan is centred where the drone is when the task starts and runs in the background
        // (UpdateTaskScan10) while the drone flies on. A scan still running from the previous waypoint is finished first.
        private TaskStatus RunSurvey(bool starting)
        {
            if (taskScanner.Running)
            {
                StepTaskScan();
                if (taskScanner.Running) return TaskStatus.Running;
            }
            if (!OreDetectorWorking)
            {
                Report(StrSurveyNeedsAWorkingOre, oreDetector == null ? Loc.T(StrNone) : !oreDetector.Enabled ? Loc.T(StrSwitchedOff) : Loc.T(StrNotWorking));
                return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
            }
            CaptureFlightState();
            taskHits.Clear();
            double r = Math.Min(oreDetector.Range, MiningConfig.MaxScanRadius);
            if (!taskScanner.Begin(flightState.Position, r))
                RecordSweep(flightState.Position, r, taskHits);   // nothing to scan here: an empty sweep still says so
            return TaskStatus.Done;
        }

        private void StepTaskScan()
        {
            if (!taskScanner.Step(MiningConfig.ScanCellsPerUpdate, taskHits)) return;
            RecordSweep(taskScanner.Center, taskScanner.Radius, taskHits);
            surveysDone++;
            Report(StrSurveyOreHitSM, taskHits.Count, taskScanner.Radius);
            taskHits.Clear();
        }

        // UpdateBeforeSimulation10, server: the fly-by scan in the background
        private void UpdateTaskScan10()
        {
            if (taskScanner.Running) StepTaskScan();
        }

        /// <summary>Server: the scout laser's recall: the current job is dropped (reported Aborted), the drone goes home.</summary>
        public bool Recall(string by)
        {
            if (!IsServer || !initialized || settings == null) return false;
            Report(StrRecalled, by);
            EndCombat(Loc.T(StrRecalledReason));
            StopJob(Loc.T(StrRecalledReason));
            taskScanner.Cancel();
            ClearFlightOrder();
            if (!IsAtRest()) GoRest();
            return true;
        }
        #endregion

        #region Survey
        // UpdateBeforeSimulation10, server, AI on
        private void UpdateSurvey10()
        {
            if (settings == null || !settings.SurveyForOres) return;
            if (isParked || preflightStage != 0) return;   // docked: nothing new to see
            if (taskScanner.Running) return;               // a scout job's scan is under way
            if (!OreDetectorWorking)
            {
                if (!surveyWarned) { surveyWarned = true; Report(StrSurveyForOresNeedsA); }
                surveyScanner.Cancel();
                return;
            }
            surveyWarned = false;
            if (cPhase == ConstructionPhase.Running && boreCollecting) return;   // the mine task uses the scanner
            var config = MiningConfig;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (surveyScanner.Running)
            {
                if (surveyScanner.Step(config.ScanCellsPerUpdate, surveyHits))
                {
                    RecordSweep(surveyScanner.Center, surveyScanner.Radius, surveyHits);
                    surveyHits.Clear();
                }
                return;
            }
            double r = Math.Min(oreDetector.Range, config.MaxScanRadius);
            if (now - lastSurveyFrame < config.DroneSurveyIntervalSeconds * 60) return;
            if (Vector3D.DistanceSquared(flightState.Position, lastSurveyCenter) < r * r * 0.25) return;
            lastSurveyFrame = now;
            lastSurveyCenter = flightState.Position;
            surveyHits.Clear();
            surveyScanner.Begin(flightState.Position, r);
        }

        private void RecordSweep(Vector3D center, double radius, List<OreHit> hits)
        {
            // No mining surveyor of the owner / faction anywhere: nobody to hand it to, the scan is lost
            if (!Mining.MiningSurveyorBlock.AnyFor(block.OwnerId))
            {
                if (!noSurveyorLogged) Log.Debug("Drone {0}: ore scan at {1} ({2} hit(s)) dropped: no mining surveyor", Entity.EntityId, center, hits.Count);
                noSurveyorLogged = true;
                surveyBuffer.Clear();
                return;
            }
            noSurveyorLogged = false;
            var sweep = new SurveySweep
            {
                Center = Vector3DData.FromVector3D(center),
                Radius = (float)radius,
                ScannedUtcTicks = DateTime.UtcNow.Ticks,
            };
            for (int i = 0; i < hits.Count; i++)
                sweep.Hits.Add(new MiningOreDepositSample { Ore = hits[i].Ore.String, Position = Vector3DData.FromVector3D(hits[i].Position),
                                                             Voxels = hits[i].Voxels, VoxelId = hits[i].VoxelId });
            surveyBuffer.Add(sweep);
            if (surveyBuffer.Count > SURVEY_BUFFER_MAX) surveyBuffer.RemoveAt(0);
            Log.Debug("Drone {0}: ore scan at {1} ({2:F0} m): {3} hit(s), {4} scan(s) waiting for a surveyor", Entity.EntityId, center, radius,
                      hits.Count, surveyBuffer.Count);
            lastSurveyPublish = int.MinValue / 2;   // straight away if a surveyor is in range (else held until one is)
            PublishSurveys();
        }
        private bool noSurveyorLogged;

        // UpdateBeforeSimulation100: waiting scans to the surveyors in range (dropped once one got them)
        private void PublishSurveys()
        {
            if (surveyBuffer.Count == 0 || primaryAntenna == null) return;
            if (!Mining.MiningSurveyorBlock.AnyFor(block.OwnerId))
            {
                Log.Debug("Drone {0}: {1} held ore scan(s) dropped: no mining surveyor any more", _entityId, surveyBuffer.Count);
                surveyBuffer.Clear();
                return;
            }
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastSurveyPublish < SURVEY_PUBLISH_TICKS) return;
            lastSurveyPublish = now;
            var queue = AutomataSession.GetMessageQueue();
            if (queue == null || !EnsureNetworkRegistration(queue)) return;
            long nowTicks = DateTime.UtcNow.Ticks;
            var payload = new MiningSurveyDataBroadcast { DroneEntityId = _entityId };
            for (int i = 0; i < surveyBuffer.Count; i++)
            {
                surveyBuffer[i].AgeSeconds = Math.Max(0, (nowTicks - surveyBuffer[i].ScannedUtcTicks) / (double)TimeSpan.TicksPerSecond);
                payload.Sweeps.Add(surveyBuffer[i]);
            }
            var error = queue.BroadcastMessage(primaryAntenna, new Message<MiningSurveyDataBroadcast>
            {
                Payload = payload,
                MessageId = IdGenerator.GenerateId(ref jobMessageCounter, _entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = _entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.MiningSurveyor,
                Channel = Channel.MINING_DATA_FOUND_ANNOUNCEMENT,
            });
            if (error != ErrorCode.None) return;   // no surveyor in range: kept
            Log.Debug("Drone {0}: {1} ore scan(s) sent to the surveyors", _entityId, surveyBuffer.Count);
            surveyBuffer.Clear();
        }

        /// <summary>The drone's antenna registered on the virtual network (any mode: reports and surveys go out).</summary>
        private bool EnsureNetworkRegistration(MessageQueue queue)
        {
            if (primaryAntenna == null) return false;
            MessageQueue.AntennaInfo info;
            if (!queue.GetAntennaInfo(_entityId, out info) || info.Antenna != primaryAntenna)
            {
                queue.RemoveAntenna(_entityId);
                queue.RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
            }
            return true;
        }
        #endregion
    }
}
