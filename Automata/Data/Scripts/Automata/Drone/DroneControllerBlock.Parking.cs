using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using VRage.Utils;

namespace Automata.Drone
{
    /// <summary>
    /// Parking on landing gears (TODO/12). A drone managed by the scheduler, with home "None (park)" and at least one
    /// landing gear, has no connector of its own:
    ///  - At rest it parks on its gears: in the staging area of its network's orchestrator when it has one, else on a
    ///    friendly static grid (owner / faction only). It searches the spot itself: points over the area's floor
    ///    (or the grid's top), nearest first; a probe down finds the surface (voxel or a friendly static grid, level
    ///    enough); the hull must fit there, clear of blocks and of the spots other drones hold.
    ///  - It flies above the spot, comes straight down at approach speed with its gears on (auto-lock off) and locks
    ///    the first gear that is ready; then its systems sleep like at a dock.
    ///  - Low on power / hydrogen: the nearest free friendly connector it can dock at (orientation: TODO/14), refuel
    ///    there, then back to a parking spot. Docked at a job's connector with nothing to do: off to park too.
    /// Gears are off in flight (all drones), on only for a landing.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrGearsFacing = MyStringId.GetOrCompute("gears facing {0}");
        private static readonly MyStringId StrSpotWhere = MyStringId.GetOrCompute("{0} ({1} gear on {2})");
        private static readonly MyStringId StrGridNamed = MyStringId.GetOrCompute("grid '{0}'");
        private static readonly MyStringId StrVoxel = MyStringId.GetOrCompute("voxel");
        private static readonly MyStringId StrReturnNoRouteBackTo = MyStringId.GetOrCompute("Return: no route back to where it left from");
        private static readonly MyStringId StrReturnToWhereItLeft = MyStringId.GetOrCompute("Return: to where it left from ({0:F0} m)");
        private static readonly MyStringId StrParkNoWorkingLandingGear = MyStringId.GetOrCompute("Park: no working landing gear");
        private static readonly MyStringId StrParkNoFreeSpotFound = MyStringId.GetOrCompute("Park: no free spot found ({0})");
        private static readonly MyStringId StrParkNoRouteToThe = MyStringId.GetOrCompute("Park: no route to the spot ({0}, {1})");
        private static readonly MyStringId StrParkM = MyStringId.GetOrCompute("Park: {0} ({1:F0} m)");
        private static readonly MyStringId StrParkingAreaOf = MyStringId.GetOrCompute("parking area of '{0}'");
        private static readonly MyStringId StrOrchestrator = MyStringId.GetOrCompute("orchestrator");
        private static readonly MyStringId StrOnGrid = MyStringId.GetOrCompute("on '{0}'");
        private static readonly MyStringId StrOnVoxel = MyStringId.GetOrCompute("on voxel");
        private static readonly MyStringId StrRouteBlocked = MyStringId.GetOrCompute("route blocked");
        private static readonly MyStringId StrFlightOrderGone = MyStringId.GetOrCompute("flight order gone");
        private static readonly MyStringId StrPark = MyStringId.GetOrCompute("Park");
        private static readonly MyStringId StrTimeout = MyStringId.GetOrCompute("timeout");
        private static readonly MyStringId StrNoGearCouldLock = MyStringId.GetOrCompute("no gear could lock");
        private static readonly MyStringId StrParkedOnLandingGear = MyStringId.GetOrCompute("Parked on landing gear");
        private static readonly MyStringId StrParkFailedWaiting = MyStringId.GetOrCompute("Park: failed ({0}): waiting");
        private static readonly MyStringId StrRechargeDockingAt = MyStringId.GetOrCompute("Recharge: docking at {0}");
        #endregion

        private const int PARK_CANDIDATES_MAX = 400;        // rays probed per search (per grid)
        private const double PARK_WALL_MAX_DOT = 0.34;      // a wall: normal within ~20° of horizontal
        private const double PARK_PAD_GAP = 0.1;            // m between the pad and the surface on the spot (the final push locks it)
        private const double PARK_MARGIN = 1.5;             // m around the hull
        private const double PARK_MAX_SLOPE_COS = 0.94;     // ~20° from level
        private const double PARK_ABOVE = 12;               // m: approach point above the spot
        private const double PARK_SEARCH_RADIUS = 1500;     // m: friendly static grids considered
        private const double PARK_VOXEL_REACH = 150;        // m: voxel surfaces looked for around the search centre
        private const int PARK_VOXEL_RAYS = 256;            // directions (even spread) per search centre
        private const double PARK_VOXEL_MIN_FACING = 0.25;  // the surface faces back along the ray at least this much (cos)
        private const double PARK_VOXEL_STANDOFF = 20;      // m off the nearest voxel surface for the second search centre
        private const double CHARGE_SEARCH_RADIUS = 3000;   // m: connectors to recharge at
        #region Timing
        private const int PARK_TIMEOUT_TICKS = 3 * 3600;
        private const int PARK_LOCK_WAIT_TICKS = 10 * 60;   // at the spot without a gear ready: another spot
        #endregion
        private const double KEEP_OFF_RADIUS = 3;           // m: connectors, doors, gears, rotors, pistons
        private const double TURBINE_BLADE_REACH = 6;       // m more around wind turbines (blades)

        private enum ParkPhase : byte { None, Flying }
        private ParkPhase parkPhase;
        private FlightOrder parkOrder;
        private Vector3D parkSpot;                          // controller position, landed (world)
        private int parkStartFrame, parkArrivedFrame;
        private int parkFails;
        private readonly List<Vector3D> parkRejected = new List<Vector3D>();   // spots that failed this time
        private int lastParkTryFrame = int.MinValue / 2;

        /// <summary>Spots held by drones (parked, or flying there): drone entity id -> hull sphere.</summary>
        private static readonly Dictionary<long, BoundingSphereD> parkedSpots = new Dictionary<long, BoundingSphereD>();

        /// <summary>No home connector, landing gear fitted (any operation mode): parks on its gears.</summary>
        public bool ParkMode
        {
            get { return settings != null && settings.HomeConnectorId == 0 && landingGears.Count > 0; }
        }

        private bool IsParkedOnGears() { return ParkMode && AnyLandingGearLocked() && !AnyConnectorsConnected(); }

        /// <summary>
        /// No home connector and no landing gear (combat drones / scouts with a departure spot aside): it rests docked at
        /// any free connector it fits, the nearest (GoCharge's search) - "return home" used to fail for it every time.
        /// </summary>
        private bool RestsAtAnyConnector
        {
            get { return settings != null && settings.HomeConnectorId == 0 && landingGears.Count == 0 && !ReturnsToDeparture; }
        }

        /// <summary>At rest: docked at home, or (park mode) parked on its gears.</summary>
        private bool IsAtRest() { return IsDockedAtHome() || IsParkedOnGears() || HoveringAtDeparture() || (RestsAtAnyConnector && AnyConnectorsConnected()); }

        private bool IsGoingToRest
        {
            get
            {
                return IsGoingHome || parkPhase != ParkPhase.None || (goingToDeparture && activeFlightOrder != null)
                    || (RestsAtAnyConnector && dockingHomeId != 0);   // docking at the nearest free connector to rest
            }
        }

        /// <summary>Home connector, or (park mode) a connector to refuel at when low, else a parking spot.</summary>
        private bool GoRest()
        {
            // Outside a job with cargo aboard: dock somewhere to clear it first (a housekeeping job that rests after)
            if (currentJob == null && TryStartHousekeeping()) return true;
            if (!ParkMode)
            {
                if (ReturnsToDeparture) return GoDeparture();   // combat / scout, no connector, no gear: back over where it left from
                if (RestsAtAnyConnector) return GoCharge(true); // no home, no gear: a free connector where it left from (else the nearest)
                return GoHome();
            }
            if ((needsBatteryRecharge || needsHydrogenRefuel) && GoCharge()) return true;
            return GoPark();
        }

        #region Departure (no home connector: combat drones, ore scouts)
        private const double DEPARTURE_NEAR = 15;    // m: hovering this close to the departure spot is "back"
        private const double DEPARTURE_HOVER = 3;    // m above the spot it rested on
        private bool goingToDeparture;

        /// <summary>Combat drones and ore scouts without a home connector go back where they last took off from.</summary>
        private bool ReturnsToDeparture
        {
            get
            {
                return settings != null && !HasHomeConnector && settings.HasDeparture
                    && (settings.BehaviourProfile == BehaviourProfile.IsCombatDrone || settings.BehaviourProfile == BehaviourProfile.IsOreScout);
            }
        }

        // Where the park search looks: around where it left from (no home connector), else around the drone
        private Vector3D ParkSearchCentre
        {
            get { return settings != null && !HasHomeConnector && settings.HasDeparture ? settings.DepartureSpot.ToVector3D() : flightState.Position; }
        }

        // Pre-flight, letting go (connector / gears): this is where it came from, and the grid it was on (no home connector only)
        private void RememberDeparture()
        {
            if (settings == null || HasHomeConnector) return;
            CaptureFlightState();
            RefreshDroneGrids();
            settings.DepartureSpot = Vector3DData.FromVector3D(flightState.Position);
            settings.HasDeparture = true;
            settings.DepartureGridId = DepartureGridNow();
            SaveSettings();
        }

        private bool HoveringAtDeparture()
        {
            if (!ReturnsToDeparture || ParkMode) return false;   // gear: it parks instead
            Vector3D spot = settings.DepartureSpot.ToVector3D();
            double near = Math.Max(DEPARTURE_NEAR, WaypointTol);
            return Vector3D.DistanceSquared(flightState.Position, spot) < near * near;
        }

        // No landing gear (or no spot near it): fly back over the departure spot and hold there
        private bool GoDeparture()
        {
            if (!ReturnsToDeparture) return false;
            CaptureFlightState();
            Vector3D spot = settings.DepartureSpot.ToVector3D();
            Vector3D up = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            var wps = new List<Vector3D> { spot + up * DEPARTURE_HOVER };
            if (!FlyPlayerRoute(wps)) { Report(StrReturnNoRouteBackTo); return false; }
            goingToDeparture = true;
            if (preflightStage == 0) SetState(State.ReturningToBase);
            Report(StrReturnToWhereItLeft, Vector3D.Distance(flightState.Position, spot));
            return true;
        }
        #endregion

        #region Park
        public bool GoPark()
        {
            if (!initialized || settings == null || !ParkMode) return false;
            if (IsParkedOnGears()) return true;
            lastParkTryFrame = MyAPIGateway.Session.GameplayFrameCounter;
            CaptureFlightState();
            RefreshDroneGrids();
            var pm = PathfindingManager.Instance;
            if (pm == null) return false;
            PathQuery q;
            BuildPathQuery(out q);
            q.SeeAll = true;   // a spot is chosen from what is there, not from what the drone sees from afar

            RefreshGearPads();
            if (gearPads.Count == 0) { Report(StrParkNoWorkingLandingGear); return false; }
            // Managed by an orchestrator with its parking protocol on: its directive - Parking area: area > its grid >
            // any friendly grid > voxel; Park on grid: its grid > any friendly grid > voxel; Voxel: voxel near it only
            // (no grids). Otherwise (no protocol, or not managed by an orchestrator): any friendly grid > voxel.
            ParkSpot ps = default(ParkSpot);
            var orch = settings.OperationMode == OperationMode.ManagedByOrchestrator
                ? Orchestrator.OrchestratorBlock.StagingFor(block.CubeGrid, block.OwnerId, ParkSearchCentre) : null;
            bool found;
            if (orch != null && orch.UsesVoxel) found = FindVoxelSpot(pm, ref q, orch.ParkingCentre, out ps);
            else
            {
                found = orch != null && orch.UsesParkingArea && FindStagingSpot(pm, ref q, orch, out ps);
                if (!found && orch != null && orch.ParkingGrid != null) found = FindFriendlyGridSpot(pm, ref q, orch.ParkingGrid, out ps);
                var departed = DepartureGrid();   // the grid it left from: back there
                if (!found && departed != null && departed.IsStatic) found = FindFriendlyGridSpot(pm, ref q, departed, out ps);
                if (!found) found = FindFriendlyGridSpot(pm, ref q, null, out ps);
                if (!found) found = FindVoxelSpot(pm, ref q, ParkSearchCentre, out ps);
            }
            if (!found)
            {
                Report(StrParkNoFreeSpotFound, GearSummary());
                if (ReturnsToDeparture) return GoDeparture();   // nowhere to land near where it left from: hover there
                return false;
            }
            Vector3D spot = ps.Controller;
            MatrixD frame = ps.Frame;
            MatrixD none = MatrixD.Identity;
            Vector3D approach = spot + ps.Normal * ApproachOut(ps);
            var failure = FlyRoute(null, ref none, spot, approach, DroneRadius(), Vector3D.Zero, ref frame, false);
            if (failure != Orchestrator.TaskFailure.None)
            {
                Report(StrParkNoRouteToThe, ps.Where, failure);
                parkRejected.Add(spot);
                return false;
            }
            parkOrder = constructionOrder;
            parkOrder.FinalSpeed = SafeSpeed;
            parkOrder.ArrivalTolerance = 0.3f;
            parkPhase = ParkPhase.Flying;
            parkSpot = spot;
            parkStartFrame = MyAPIGateway.Session.GameplayFrameCounter;
            parkArrivedFrame = 0;
            parkedSpots[Entity.EntityId] = new BoundingSphereD(spot + Vector3D.TransformNormal(HullCentreLocal, frame), HullRadiusLevel());
            if (preflightStage == 0) SetState(State.ReturningToBase);
            Report(StrParkM, ps.Where, Vector3D.Distance(flightState.Position, spot));
            return true;
        }

        // Half the hull's horizontal diagonal (any heading), plus the margin
        private double HullRadiusLevel()
        {
            Vector3D s = localHullBox.Size;
            return Math.Sqrt(s.X * s.X + s.Z * s.Z) * 0.5 + PARK_MARGIN;
        }

        #region Landing gear pads (any facing)
        // A landing gear's magnetic pad, controller frame: the point it locks with and the axis it faces (snapped to the
        // controller's axes). Gears may face any way: down (floor), sideways (wall), up (ceiling); in space any of them.
        private struct GearPad
        {
            public IMyLandingGear Gear;
            public Vector3D Point;   // controller frame
            public Vector3D Dir;     // controller frame, unit axis
            public Base6Directions.Direction Facing;
        }
        private readonly List<GearPad> gearPads = new List<GearPad>();

        private void RefreshGearPads()
        {
            gearPads.Clear();
            CaptureFlightState();
            MatrixD toLocal = flightState.WorldMatrixToLocal;
            for (int i = 0; i < landingGears.Count; i++)
            {
                var g = landingGears[i];
                if (g == null || g.Closed || !g.IsFunctional) continue;
                Vector3D p, a;
                GearPadGeometry(g, out p, out a);
                Vector3D lp = Vector3D.TransformNormal(p - flightState.Position, toLocal);
                Vector3D la = Vector3D.TransformNormal(a, toLocal);
                var facing = Base6Directions.GetClosestDirection((Vector3)la);
                gearPads.Add(new GearPad { Gear = g, Point = lp, Dir = (Vector3D)Base6Directions.GetVector(facing), Facing = facing });
            }
        }

        private string GearSummary()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < gearPads.Count; i++) sb.Append(i > 0 ? "/" : "").Append(gearPads[i].Facing);
            return Loc.F(StrGearsFacing, sb);
        }

        /// <summary>
        /// The pad of a landing gear, world: the model dummy whose name contains "gear_lock" (what the game locks with),
        /// projected on the block's dominant axis from its centre; that axis, outwards, is the pad's facing. Without the
        /// dummy: the block's bottom face.
        /// </summary>
        private static void GearPadGeometry(IMyCubeBlock b, out Vector3D point, out Vector3D axis)
        {
            MatrixD w = b.WorldMatrix;
            var grid = b.CubeGrid;
            Vector3D center = Vector3D.Transform((Vector3D)((Vector3)(b.Min + b.Max) * grid.GridSize * 0.5f), grid.WorldMatrix);
            Vector3D sum = Vector3D.Zero;
            int n = 0;
            if (b.Model != null)
            {
                dummyCache.Clear();
                b.Model.GetDummies(dummyCache);
                foreach (var kv in dummyCache)
                {
                    if (kv.Key.IndexOf("gear_lock", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sum += Vector3D.Transform((Vector3D)kv.Value.Matrix.Translation, w);
                    n++;
                }
                dummyCache.Clear();
            }
            if (n > 0)
            {
                Vector3D v = sum / n - center;
                double r = Vector3D.Dot(v, w.Right), u = Vector3D.Dot(v, w.Up), f = Vector3D.Dot(v, w.Forward);
                double ar = Math.Abs(r), au = Math.Abs(u), af = Math.Abs(f);
                double along = Math.Max(af, Math.Max(ar, au));
                if (along > 1e-3)
                {
                    if (af >= ar && af >= au) axis = w.Forward * Math.Sign(f);
                    else if (au >= ar)        axis = w.Up * Math.Sign(u);
                    else                      axis = w.Right * Math.Sign(r);
                    point = center + axis * along;
                    return;
                }
            }
            Vector3I size = b.Max - b.Min + Vector3I.One;
            Vector3I di = Base6Directions.GetIntVector(b.Orientation.Up);
            double cells = Math.Abs(size.X * di.X + size.Y * di.Y + size.Z * di.Z);
            axis = w.Down;
            point = center + axis * (cells * grid.GridSize * 0.5);
        }

        /// <summary>
        /// Drone frame (forward, up; at the origin) that puts pad 'p' flat on a surface of outward normal 'n': the pad faces
        /// -n. In gravity the drone stays level: a down pad on a floor, an up pad under a ceiling, a side pad against a wall,
        /// turned about the vertical ('heading' for floor / ceiling). In space any pad on any surface.
        /// </summary>
        private MatrixD PadFrame(ref GearPad p, Vector3D n, Vector3D heading, Vector3D gUp, Vector3D spaceUpHint)
        {
            Vector3D a = p.Dir, A = -n, b, B;
            bool vertical = p.Facing == Base6Directions.Direction.Up || p.Facing == Base6Directions.Direction.Down;
            if (vertical)
            {
                b = new Vector3D(0, 0, -1);   // forward
                B = Vector3D.Reject(heading, A);
            }
            else
            {
                b = new Vector3D(0, 1, 0);    // up
                B = gUp.LengthSquared() > 0.5 ? gUp : Vector3D.Reject(spaceUpHint, A);
            }
            if (B.LengthSquared() < 1e-6) B = Vector3D.CalculatePerpendicularVector(A);
            B.Normalize();
            B = Vector3D.Normalize(B - A * Vector3D.Dot(B, A));
            Vector3D c = Vector3D.Cross(a, b), C = Vector3D.Cross(A, B);
            Vector3D fwdL = new Vector3D(0, 0, -1), upL = new Vector3D(0, 1, 0);
            Vector3D fwd = A * Vector3D.Dot(fwdL, a) + B * Vector3D.Dot(fwdL, b) + C * Vector3D.Dot(fwdL, c);
            Vector3D up = A * Vector3D.Dot(upL, a) + B * Vector3D.Dot(upL, b) + C * Vector3D.Dot(upL, c);
            return MatrixD.CreateWorld(Vector3D.Zero, fwd, up);
        }

        // Which pads may lock on a surface of normal 'n': in gravity by facing (floor / wall / ceiling), in space any
        private bool PadSuits(ref GearPad p, Vector3D n, Vector3D gUp)
        {
            if (gUp.LengthSquared() < 0.5) return true;
            double up = Vector3D.Dot(n, gUp);
            if (up >= PARK_MAX_SLOPE_COS) return p.Facing == Base6Directions.Direction.Down;     // floor
            if (up <= -PARK_MAX_SLOPE_COS) return p.Facing == Base6Directions.Direction.Up;     // ceiling
            if (Math.Abs(up) <= PARK_WALL_MAX_DOT) return p.Facing != Base6Directions.Direction.Up && p.Facing != Base6Directions.Direction.Down;   // wall
            return false;
        }
        #endregion

        #region Spot search
        private struct ParkSpot
        {
            public Vector3D Controller;   // world
            public MatrixD Frame;         // drone attitude there (origin)
            public Vector3D Normal;       // surface, outwards: the landing comes in along it
            public string Where;
            public double Approach;       // m out along the normal the final approach starts (0: ApproachOut works it out)
        }

        private struct ParkRay
        {
            public Vector3D From, Dir;
            public double Length;
        }
        private readonly List<ParkRay> parkRays = new List<ParkRay>();

        private bool HasPadFacing(bool down, bool up, bool side)
        {
            for (int i = 0; i < gearPads.Count; i++)
            {
                var f = gearPads[i].Facing;
                if (down && f == Base6Directions.Direction.Down) return true;
                if (up && f == Base6Directions.Direction.Up) return true;
                if (side && f != Base6Directions.Direction.Up && f != Base6Directions.Direction.Down) return true;
            }
            return false;
        }

        // The orchestrator's parking area, only while its parking protocol is on in area mode (TryGetStagingArea):
        // its floor (down pads, nearest the centre first), its walls and ceilings (side / up pads: static grids inside it)
        private bool FindStagingSpot(PathfindingManager pm, ref PathQuery q, Orchestrator.OrchestratorBlock orch, out ParkSpot spot)
        {
            spot = default(ParkSpot);
            if (orch == null) return false;
            MatrixD box;
            Vector3D half;
            if (!orch.TryGetStagingArea(out box, out half)) return false;
            bool gravity = flightState.InGravity;
            Vector3D gUp = gravity ? flightState.GravityUp : Vector3D.Zero;
            Vector3D up = gravity ? gUp : box.Up;
            Vector3D fwd = Vector3D.Reject(box.Forward, up);
            fwd = fwd.LengthSquared() > 1e-4 ? Vector3D.Normalize(fwd) : Vector3D.CalculatePerpendicularVector(up);
            Vector3D right = Vector3D.Cross(fwd, up);
            Vector3D centre = box.Translation;
            double hx = half.X, hy = half.Y, hz = half.Z;
            double step = Math.Max(2.0, HullRadiusLevel() * 2);
            parkRays.Clear();
            // Floor: down from the top face (in space: down pads or any pad, the area's down)
            if (!gravity || HasPadFacing(true, false, false))
            {
                candidates.Clear();
                for (double x = -hx + step / 2; x <= hx - step / 2 + 1e-3; x += step)
                    for (double z = -hz + step / 2; z <= hz - step / 2 + 1e-3; z += step)
                        candidates.Add(new Vector3D(x, 0, z));
                if (candidates.Count == 0) candidates.Add(Vector3D.Zero);
                candidates.Sort((a, b) => (a.X * a.X + a.Z * a.Z).CompareTo(b.X * b.X + b.Z * b.Z));
                for (int i = 0; i < candidates.Count; i++)
                    parkRays.Add(new ParkRay { From = centre + right * candidates[i].X + fwd * candidates[i].Z + up * hy, Dir = -up, Length = hy * 2 + 5 });
            }
            // Walls: in from the four sides, at several heights
            if (gravity && HasPadFacing(false, false, true)) AddWallRays(centre, up, fwd, right, hx, hy, hz, step, false);
            // Ceilings: up from the floor
            if (gravity && HasPadFacing(false, true, false))
            {
                for (double x = -hx + step / 2; x <= hx - step / 2 + 1e-3; x += step)
                    for (double z = -hz + step / 2; z <= hz - step / 2 + 1e-3; z += step)
                        parkRays.Add(new ParkRay { From = centre + right * x + fwd * z - up * hy, Dir = up, Length = hy * 2 });
            }
            var inv = MatrixD.Invert(box);
            string where = Loc.F(StrParkingAreaOf, orch.Entity is IMyTerminalBlock ? ((IMyTerminalBlock)orch.Entity).CustomName : Loc.T(StrOrchestrator));
            int tried;
            bool ok = TryParkRays(pm, ref q, null, gUp, fwd, box.Up, s =>
            {
                Vector3D l = Vector3D.Transform(s, inv);
                return Math.Abs(l.X) <= hx + 0.5 && Math.Abs(l.Z) <= hz + 0.5 && l.Y >= -hy - 2 && l.Y <= hy + 0.5;
            }, where, out spot, out tried);
            Log.Debug("Drone {0}: park: {1} in the parking area ({2} ray(s), {3})", Entity.EntityId, ok ? "spot" : "no free spot", tried, GearSummary());
            return ok;
        }

        // Rays in toward the box / grid from its sides (horizontal), at heights a hull apart
        private void AddWallRays(Vector3D centre, Vector3D up, Vector3D fwd, Vector3D right, double hx, double hy, double hz, double step, bool ring)
        {
            double hStep = Math.Max(2.5, localHullBox.Max.X >= localHullBox.Min.X ? localHullBox.Size.Y : 2.5);
            int heights = 0;
            for (double y = hy - hStep / 2; y >= -hy && heights < 8; y -= hStep, heights++)   // from the top: low rays meet the ground
            {
                if (ring)
                {
                    // Around a grid: every 'step' of its circumference (radius hx), toward its centre
                    int n = Math.Min(48, Math.Max(8, (int)(2 * Math.PI * hx / step)));
                    for (int k = 0; k < n; k++)
                    {
                        double ang = 2 * Math.PI * k / n;
                        Vector3D d = right * Math.Cos(ang) + fwd * Math.Sin(ang);
                        parkRays.Add(new ParkRay { From = centre + d * (hx + 5) + up * y, Dir = -d, Length = hx * 2 + 5 });
                    }
                    continue;
                }
                for (double s = -hz + step / 2; s <= hz - step / 2 + 1e-3; s += step)
                {
                    parkRays.Add(new ParkRay { From = centre - right * hx + fwd * s + up * y, Dir = right, Length = hx * 2 });
                    parkRays.Add(new ParkRay { From = centre + right * hx + fwd * s + up * y, Dir = -right, Length = hx * 2 });
                }
                for (double s = -hx + step / 2; s <= hx - step / 2 + 1e-3; s += step)
                {
                    parkRays.Add(new ParkRay { From = centre - fwd * hz + right * s + up * y, Dir = fwd, Length = hz * 2 });
                    parkRays.Add(new ParkRay { From = centre + fwd * hz + right * s + up * y, Dir = -fwd, Length = hz * 2 });
                }
            }
        }

        private readonly List<Vector3D> candidates = new List<Vector3D>();
        private readonly List<IMyEntity> nearEntities = new List<IMyEntity>();
        private readonly List<IMyCubeGrid> nearGrids = new List<IMyCubeGrid>();

        // The nearest friendly static grids ('onlyGrid': that one, the parking protocol's grid) - their tops (down pads),
        // sides (side pads), undersides (up pads)
        private bool FindFriendlyGridSpot(PathfindingManager pm, ref PathQuery q, IMyCubeGrid onlyGrid, out ParkSpot spot)
        {
            spot = default(ParkSpot);
            Vector3D here = ParkSearchCentre;   // combat / scouts: near where they left from
            nearEntities.Clear();
            nearGrids.Clear();
            if (onlyGrid != null)
            {
                if (!onlyGrid.Closed && onlyGrid.IsStatic && onlyGrid.Physics != null && !droneGrids.Contains(onlyGrid) && FriendlyGrid(onlyGrid))
                    nearGrids.Add(onlyGrid);
            }
            else
            {
                var sphere = new BoundingSphereD(here, PARK_SEARCH_RADIUS);
                nearEntities.AddRange(MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere));
                for (int i = 0; i < nearEntities.Count; i++)
                {
                    var g = nearEntities[i] as IMyCubeGrid;
                    if (g == null || !g.IsStatic || g.Physics == null || droneGrids.Contains(g) || !FriendlyGrid(g)) continue;
                    nearGrids.Add(g);
                }
                nearEntities.Clear();
            }
            nearGrids.Sort((a, b) => Vector3D.DistanceSquared(a.WorldVolume.Center, here).CompareTo(Vector3D.DistanceSquared(b.WorldVolume.Center, here)));
            bool gravity = flightState.InGravity;
            Vector3D gUp = gravity ? flightState.GravityUp : Vector3D.Zero;
            double step = Math.Max(2.0, HullRadiusLevel() * 2);
            int total = 0;
            for (int gi = 0; gi < nearGrids.Count && gi < 5 && total < PARK_CANDIDATES_MAX; gi++)
            {
                var grid = nearGrids[gi];
                BoundingSphereD vol = grid.WorldVolume;
                Vector3D up = gravity ? gUp : grid.WorldMatrix.Up;
                Vector3D fwd = Vector3D.Reject(grid.WorldMatrix.Forward, up);
                fwd = fwd.LengthSquared() > 1e-4 ? Vector3D.Normalize(fwd) : Vector3D.CalculatePerpendicularVector(up);
                Vector3D right = Vector3D.Cross(fwd, up);
                Vector3D centre = vol.Center;
                double r = vol.Radius;
                parkRays.Clear();
                if (!gravity || HasPadFacing(true, false, false))
                {
                    candidates.Clear();
                    for (double x = -r; x <= r; x += step)
                        for (double z = -r; z <= r; z += step)
                            if (x * x + z * z <= r * r) candidates.Add(new Vector3D(x, 0, z));
                    // Nearest the drone first
                    Vector3D d = here - centre;
                    double dx = Vector3D.Dot(d, right), dz = Vector3D.Dot(d, fwd);
                    candidates.Sort((a, b) => ((a.X - dx) * (a.X - dx) + (a.Z - dz) * (a.Z - dz)).CompareTo((b.X - dx) * (b.X - dx) + (b.Z - dz) * (b.Z - dz)));
                    for (int i = 0; i < candidates.Count; i++)
                        parkRays.Add(new ParkRay { From = centre + right * candidates[i].X + fwd * candidates[i].Z + up * (r + 5), Dir = -up, Length = r * 2 + 10 });
                }
                if (gravity && HasPadFacing(false, false, true)) AddWallRays(centre, up, fwd, right, r, r, r, step, true);
                if (gravity && HasPadFacing(false, true, false))
                {
                    for (double x = -r; x <= r; x += step)
                        for (double z = -r; z <= r; z += step)
                            if (x * x + z * z <= r * r)
                                parkRays.Add(new ParkRay { From = centre + right * x + fwd * z - up * (r + 2), Dir = up, Length = r * 2 + 2 });
                }
                int tried;
                bool ok = TryParkRays(pm, ref q, grid, gUp, fwd, grid.WorldMatrix.Up, null, Loc.F(StrOnGrid, grid.DisplayName), out spot, out tried);
                total += tried;
                if (ok)
                {
                    Log.Debug("Drone {0}: park: spot on friendly grid '{1}' after {2} ray(s)", Entity.EntityId, grid.DisplayName, total);
                    nearGrids.Clear();
                    return true;
                }
            }
            Log.Debug("Drone {0}: park: no free spot on {1} friendly static grid(s) ({2} ray(s), {3})", Entity.EntityId, nearGrids.Count, total, GearSummary());
            nearGrids.Clear();
            return false;
        }

        #region Voxel (planet surface, cliffs, caves, asteroids)
        private struct VoxelHit { public double Distance; public ParkRay Ray; }
        private readonly List<VoxelHit> voxelHits = new List<VoxelHit>();

        /// <summary>
        /// Voxel only (grids avoided): any voxel surface - ground, cliff, cave wall / ceiling, asteroid - with a gear that
        /// suits it (in gravity the drone stays level: floors, near-vertical walls, ceilings; in space any). Rays in all
        /// directions from 'centre' (they look past grids), the nearest surfaces first; then the same from just off the
        /// nearest voxel surface (a planet's ground below, an asteroid) when that is further away.
        /// </summary>
        private bool FindVoxelSpot(PathfindingManager pm, ref PathQuery q, Vector3D centre, out ParkSpot spot)
        {
            spot = default(ParkSpot);
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : Vector3D.Zero;
            Vector3D up = flightState.InGravity ? gUp : flightState.WorldMatrix.Up;
            Vector3D fwd = Vector3D.Reject(flightState.WorldMatrix.Forward, up);
            fwd = fwd.LengthSquared() > 1e-4 ? Vector3D.Normalize(fwd) : Vector3D.CalculatePerpendicularVector(up);
            int total = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3D c = centre;
                if (pass == 1)
                {
                    Vector3D surface, outward;
                    double d;
                    if (!NearestVoxelSurface(centre, out surface, out outward, out d) || d < PARK_VOXEL_REACH * 0.5) break;
                    c = surface + outward * PARK_VOXEL_STANDOFF;
                }
                voxelHits.Clear();
                double golden = Math.PI * (3 - Math.Sqrt(5));
                for (int i = 0; i < PARK_VOXEL_RAYS; i++)
                {
                    double y = 1 - 2 * (i + 0.5) / PARK_VOXEL_RAYS, r = Math.Sqrt(Math.Max(0, 1 - y * y)), th = golden * i;
                    var dir = new Vector3D(Math.Cos(th) * r, y, Math.Sin(th) * r);
                    Vector3D s, n;
                    IMyEntity on;
                    if (!ProbeSurface(c, dir, PARK_VOXEL_REACH, null, gUp, out s, out n, out on, true, PARK_VOXEL_MIN_FACING)) continue;
                    voxelHits.Add(new VoxelHit { Distance = Vector3D.Distance(c, s), Ray = new ParkRay { From = s - dir, Dir = dir, Length = 2 } });
                }
                voxelHits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                parkRays.Clear();
                for (int i = 0; i < voxelHits.Count; i++) parkRays.Add(voxelHits[i].Ray);
                voxelHits.Clear();
                int tried;
                bool ok = TryParkRays(pm, ref q, null, gUp, fwd, up, null, Loc.T(StrOnVoxel), out spot, out tried, true, PARK_VOXEL_MIN_FACING);
                total += tried;
                if (ok)
                {
                    Log.Debug("Drone {0}: park: voxel spot ({1} surface(s) tried, pass {2}, {3})", Entity.EntityId, total, pass + 1, GearSummary());
                    return true;
                }
            }
            Log.Debug("Drone {0}: park: no free voxel spot ({1} surface(s) tried, {2})", Entity.EntityId, total, GearSummary());
            return false;
        }

        // The nearest voxel surface within PARK_SEARCH_RADIUS: the closest planet's ground (not from inside a cave: the
        // rays from the centre cover that) or an asteroid (a ray to its centre); 'outward': away from the rock
        private bool NearestVoxelSurface(Vector3D from, out Vector3D surface, out Vector3D outward, out double distance)
        {
            surface = outward = Vector3D.Zero;
            distance = double.MaxValue;
            var planet = Sandbox.Game.Entities.MyGamePruningStructure.GetClosestPlanet(from);
            if (planet != null)
            {
                Vector3D p = planet.GetClosestSurfacePointGlobal(ref from);
                double h = PathfindingManager.HeightAboveTerrain(from);
                double d = Vector3D.Distance(from, p);
                if (h != double.MaxValue && h > 0 && d < PARK_SEARCH_RADIUS)
                {
                    distance = d;
                    surface = p;
                    outward = Vector3D.Normalize(p - planet.PositionComp.GetPosition());
                }
            }
            nearEntities.Clear();
            var sphere = new BoundingSphereD(from, PARK_SEARCH_RADIUS);
            nearEntities.AddRange(MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere));
            for (int i = 0; i < nearEntities.Count; i++)
            {
                var v = nearEntities[i] as IMyVoxelBase;
                if (v == null || v is Sandbox.Game.Entities.MyPlanet || v.Closed) continue;
                Vector3D to = v.WorldVolume.Center;
                parkHits.Clear();
                MyAPIGateway.Physics.CastRay(from, to, parkHits, 15);
                for (int k = 0; k < parkHits.Count; k++)
                {
                    var hit = parkHits[k];
                    if (hit.HitEntity == null || hit.HitEntity.GetTopMostParent() != v) continue;
                    double d = Vector3D.Distance(from, hit.Position);
                    if (d < distance) { distance = d; surface = hit.Position; outward = Vector3D.Normalize(hit.Normal); }
                    break;
                }
                parkHits.Clear();
            }
            nearEntities.Clear();
            return distance < double.MaxValue;
        }

        // Voxel spot: the space out along the normal (the approach) measured, and the hull's corners reached from there in
        // straight lines (not inside the rock, not behind an overhang); grids kept KEEP_OFF_RADIUS away. 'approach': m out.
        private bool VoxelSpotClear(Vector3D controller, MatrixD frame, Vector3D n, out double approach)
        {
            approach = 0;
            double radius = DroneRadius();
            Vector3D hullCentre = controller + Vector3D.TransformNormal(HullCentreLocal, frame);
            if (NearAnyGrid(hullCentre, HullRadiusLevel() + KEEP_OFF_RADIUS)) return false;
            double reach = PARK_ABOVE + radius + 1;
            Vector3D from = hullCentre + n * 0.5;
            double free = RayFreeLength(from, from + n * reach);
            approach = Math.Min(PARK_ABOVE, free - radius - 0.5);
            if (approach < 2) return false;   // no room to come in along the normal
            Vector3D origin = hullCentre + n * approach;
            if (localHullBox.Max.X < localHullBox.Min.X) return RayFreeLength(origin, hullCentre) >= Vector3D.Distance(origin, hullCentre) - 0.01;
            for (int k = 0; k < 9; k++)
            {
                Vector3D c = k == 8 ? HullCentreLocal
                    : new Vector3D((k & 1) != 0 ? localHullBox.Max.X : localHullBox.Min.X,
                                   (k & 2) != 0 ? localHullBox.Max.Y : localHullBox.Min.Y,
                                   (k & 4) != 0 ? localHullBox.Max.Z : localHullBox.Min.Z);
                Vector3D p = controller + Vector3D.TransformNormal(c, frame) + n * (k == 8 ? 0.5 : 1.0);   // lifted: rough / sloped rock
                if (RayFreeLength(origin, p) < Vector3D.Distance(origin, p) - 0.01) return false;
            }
            return true;
        }

        // How far a ray gets before something that isn't the drone (or a character / loose item)
        private double RayFreeLength(Vector3D from, Vector3D to)
        {
            double len = Vector3D.Distance(from, to);
            parkHits.Clear();
            MyAPIGateway.Physics.CastRay(from, to, parkHits, 15);
            double best = len;
            for (int i = 0; i < parkHits.Count; i++)
            {
                var h = parkHits[i];
                var e = h.HitEntity != null ? h.HitEntity.GetTopMostParent() : null;
                if (e == null || e is IMyCharacter || e is IMyFloatingObject) continue;
                var g = e as IMyCubeGrid;
                if (g != null && droneGrids.Contains(g)) continue;
                best = Math.Min(best, h.Fraction * len);
            }
            parkHits.Clear();
            return best;
        }

        private readonly List<IMyEntity> gridProbe = new List<IMyEntity>();
        private bool NearAnyGrid(Vector3D c, double r)
        {
            var sphere = new BoundingSphereD(c, r);
            gridProbe.Clear();
            gridProbe.AddRange(MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere));
            bool near = false;
            for (int i = 0; i < gridProbe.Count && !near; i++)
            {
                var g = gridProbe[i] as IMyCubeGrid;
                if (g == null || g.Physics == null || droneGrids.Contains(g)) continue;
                keepOffBuffer.Clear();
                keepOffBuffer.AddRange(g.GetBlocksInsideSphere(ref sphere));
                near = keepOffBuffer.Count > 0;
                keepOffBuffer.Clear();
            }
            gridProbe.Clear();
            return near;
        }
        #endregion

        /// <summary>
        /// Casts 'parkRays' in order: the first surface hit (voxel only for a floor in gravity, else a friendly static grid;
        /// 'only': that grid), facing back along the ray; then for each pad that suits it, the drone placed with that pad
        /// on it (<see cref="PadFrame"/>) must fit (hull clear, not near connectors / doors / moving parts, no other drone's
        /// spot, inside the area). The first fit wins.
        /// </summary>
        private bool TryParkRays(PathfindingManager pm, ref PathQuery q, IMyCubeGrid only, Vector3D gUp, Vector3D heading, Vector3D spaceUpHint,
                                 Func<Vector3D, bool> inArea, string where, out ParkSpot spot, out int tried,
                                 bool voxelOnly = false, double minFacing = PARK_MAX_SLOPE_COS)
        {
            spot = default(ParkSpot);
            tried = 0;
            for (int i = 0; i < parkRays.Count && tried < PARK_CANDIDATES_MAX; i++, tried++)
            {
                var ray = parkRays[i];
                Vector3D surface, n;
                IMyEntity on;
                if (!ProbeSurface(ray.From, ray.Dir, ray.Length, only, gUp, out surface, out n, out on, voxelOnly, minFacing)) continue;
                if (inArea != null && !inArea(surface)) continue;
                var grid = on as IMyCubeGrid;
                if (grid != null && NearKeepOffBlock(grid, surface)) continue;
                // In gravity the drone stays level: floors / ceilings taken as level, walls as vertical
                if (gUp.LengthSquared() > 0.5)
                {
                    double upDot = Vector3D.Dot(n, gUp);
                    if (upDot >= PARK_MAX_SLOPE_COS) n = gUp;
                    else if (upDot <= -PARK_MAX_SLOPE_COS) n = -gUp;
                    else if (Math.Abs(upDot) <= PARK_WALL_MAX_DOT) n = Vector3D.Normalize(n - gUp * upDot);
                }
                for (int p = 0; p < gearPads.Count; p++)
                {
                    var pad = gearPads[p];
                    if (!PadSuits(ref pad, n, gUp)) continue;
                    MatrixD frame = PadFrame(ref pad, n, heading, gUp, spaceUpHint);
                    Vector3D controller = surface + n * PARK_PAD_GAP - Vector3D.TransformNormal(pad.Point, frame);
                    bool voxel = on is IMyVoxelBase;
                    if (!SpotFits(pm, ref q, controller, frame, n, voxel)) continue;
                    double approach = 0;
                    if (voxel && !VoxelSpotClear(controller, frame, n, out approach)) continue;
                    spot = new ParkSpot { Controller = controller, Frame = frame, Normal = n, Approach = approach,
                                          Where = Loc.F(StrSpotWhere, where, pad.Facing, Describe(on)) };
                    return true;
                }
            }
            return false;
        }

        private bool FriendlyGrid(IMyCubeGrid g)
        {
            var owners = g.BigOwners;
            if (owners == null) return false;
            for (int i = 0; i < owners.Count; i++) if (AnchorDirectory.IsAllowed(block.OwnerId, owners[i])) return true;
            return false;
        }

        private readonly List<IHitInfo> parkHits = new List<IHitInfo>();

        // First surface along 'dir': a friendly static grid ('only': that grid) or voxel (any facing: ground, cliff, cave,
        // asteroid; the pad / gravity rules pick what suits); facing back along the ray within 'minFacing' (cosine).
        // 'voxelOnly': the first voxel hit, past any grid in front (the spot check then needs a clear line to it).
        // 'normal': the surface's, outwards.
        private bool ProbeSurface(Vector3D from, Vector3D dir, double length, IMyCubeGrid only, Vector3D gUp,
                                  out Vector3D surface, out Vector3D normal, out IMyEntity on,
                                  bool voxelOnly = false, double minFacing = PARK_MAX_SLOPE_COS)
        {
            surface = normal = Vector3D.Zero;
            on = null;
            parkHits.Clear();
            MyAPIGateway.Physics.CastRay(from, from + dir * length, parkHits, 15);
            IHitInfo best = null;
            for (int i = 0; i < parkHits.Count; i++)
            {
                var h = parkHits[i];
                var e = h.HitEntity != null ? h.HitEntity.GetTopMostParent() : null;
                if (e == null || e is IMyCharacter || e is IMyFloatingObject) continue;
                var g = e as IMyCubeGrid;
                if (g != null && droneGrids.Contains(g)) continue;
                if (voxelOnly && !(e is IMyVoxelBase)) continue;
                if (best == null || h.Fraction < best.Fraction) best = h;
            }
            parkHits.Clear();
            if (best == null) return false;
            var top = best.HitEntity.GetTopMostParent();
            var grid = top as IMyCubeGrid;
            if (only != null && grid != only) return false;
            if (grid != null && (!grid.IsStatic || !FriendlyGrid(grid))) return false;   // never on moving / other players' grids
            if (grid == null && !(top is IMyVoxelBase)) return false;
            Vector3D n = best.Normal;
            if (n.LengthSquared() < 1e-6 || Vector3D.Dot(Vector3D.Normalize(n), -dir) < minFacing) return false;
            surface = best.Position;
            normal = Vector3D.Normalize(n);
            on = top;
            return true;
        }

        // Connectors, doors, gears and moving parts: not parked on or right next to
        private readonly List<IMySlimBlock> keepOffBuffer = new List<IMySlimBlock>();
        private bool NearKeepOffBlock(IMyCubeGrid grid, Vector3D surface)
        {
            // Wind turbines: their blades sweep well beyond the block
            var wide = new BoundingSphereD(surface, HullRadiusLevel() + KEEP_OFF_RADIUS + TURBINE_BLADE_REACH);
            keepOffBuffer.Clear();
            keepOffBuffer.AddRange(grid.GetBlocksInsideSphere(ref wide));
            for (int i = 0; i < keepOffBuffer.Count; i++)
            {
                var f = keepOffBuffer[i].FatBlock;
                if (f != null && f.BlockDefinition.TypeIdString.IndexOf("WindTurbine", StringComparison.OrdinalIgnoreCase) >= 0) { keepOffBuffer.Clear(); return true; }
            }
            keepOffBuffer.Clear();
            var sphere = new BoundingSphereD(surface, HullRadiusLevel() + KEEP_OFF_RADIUS);
            keepOffBuffer.Clear();
            keepOffBuffer.AddRange(grid.GetBlocksInsideSphere(ref sphere));
            bool near = false;
            for (int i = 0; i < keepOffBuffer.Count && !near; i++)
            {
                var f = keepOffBuffer[i].FatBlock;
                if (f == null) continue;
                near = f is IMyShipConnector || f is IMyDoor || f is IMyLandingGear || f is IMyMotorBase || f is IMyPistonBase
                    || f is IMyShipWelder || f is IMyShipGrinder;
            }
            keepOffBuffer.Clear();
            return near;
        }

        // The drone at 'controller' with attitude 'frame' (pad on a surface of normal 'n'): clear of blocks, terrain and
        // other drones' spots (checked a little off the surface, so the surface itself doesn't count)
        private bool SpotFits(PathfindingManager pm, ref PathQuery q, Vector3D controller, MatrixD frame, Vector3D n, bool voxel)
        {
            Vector3D hullCentre = controller + Vector3D.TransformNormal(HullCentreLocal, frame);
            double r = HullRadiusLevel();
            foreach (var kv in parkedSpots)
            {
                if (kv.Key == Entity.EntityId) continue;
                if (Vector3D.DistanceSquared(kv.Value.Center, hullCentre) < (kv.Value.Radius + r) * (kv.Value.Radius + r)) return false;
            }
            for (int i = 0; i < parkRejected.Count; i++)
                if (Vector3D.DistanceSquared(parkRejected[i], controller) < 1) return false;
            Vector3D probe = hullCentre + n * 0.5;
            // Under the ground (grid ceilings). Voxel spots: VoxelSpotClear instead (a cave is below the planet's surface)
            if (!voxel && flightState.InGravity && PathfindingManager.HeightAboveTerrain(probe) < q.LocalHull.HalfExtents.Length() * 0.25) return false;
            return pm.HullFitsOriented(ref q, probe, ref frame);
        }

        // How far out along the surface normal the final approach starts (under a ceiling: no deeper than the ground allows)
        private double ApproachOut(ParkSpot ps)
        {
            if (ps.Approach > 0) return ps.Approach;   // voxel: measured (caves, overhangs)
            if (!flightState.InGravity || Vector3D.Dot(ps.Normal, flightState.GravityUp) > -0.5) return PARK_ABOVE;
            double h = PathfindingManager.HeightAboveTerrain(ps.Controller);
            return h == double.MaxValue ? PARK_ABOVE : MathHelperD.Clamp(h - DroneRadius() - 1, 2, PARK_ABOVE);
        }
        #endregion

        private static string Describe(IMyEntity e)
        {
            if (e == null) return "-";
            var g = e as IMyCubeGrid;
            return g != null ? Loc.F(StrGridNamed, g.DisplayName) : Loc.T(StrVoxel);
        }

        // UpdateBeforeSimulation10, server, AI on
        private void UpdateParking10()
        {
            if (!ParkMode) { if (parkPhase != ParkPhase.None) EndPark(null); return; }
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (parkPhase == ParkPhase.None)
            {
                // Docked at a connector (a job's, a recharge) with nothing left to do there: off to a parking spot
                // (not for a player-managed drone: the player may have docked it there)
                if (settings.OperationMode != OperationMode.ManagedByPlayer && AnyConnectorsConnected() && cPhase == ConstructionPhase.Idle && currentJob == null && !needsBatteryRecharge
                    && !needsHydrogenRefuel && preflightStage == 0 && activeFlightOrder == null && dockingHomeId == 0 && !CombatHoldsControl
                    && now - lastParkTryFrame >= AUTO_HOME_RETRY_TICKS)
                {
                    Log.Debug("Drone {0}: park: docked with nothing to do here", Entity.EntityId);
                    GoPark();
                }
                return;
            }
            if (AnyLandingGearLocked()) { OnParked(); return; }
            var o = activeFlightOrder;
            if (o == null && preflightStage == 0)
            {
                ParkFailed(routeFailed ? Loc.T(StrRouteBlocked) : Loc.T(StrFlightOrderGone));
                return;
            }
            if (now - parkStartFrame > PARK_TIMEOUT_TICKS) { NoteSpeedTimeout(Loc.T(StrPark)); ParkFailed(Loc.T(StrTimeout)); return; }
            // A replan (something in the way) builds a new final order: follow it
            if (constructionOrder != null && constructionOrder != parkOrder)
            {
                parkOrder = constructionOrder;
                parkOrder.FinalSpeed = SafeSpeed;
                parkOrder.ArrivalTolerance = 0.3f;
            }
            if (o != parkOrder) return;
            // The descent onto the spot: gears on, the first one ready locks
            if (o.Phase == FlightPhase.Approach || o.Arrived)
            {
                for (int i = 0; i < landingGears.Count; i++)
                {
                    var g = landingGears[i];
                    if (g.Closed || !g.IsFunctional) continue;
                    if (!g.Enabled) g.Enabled = true;
                    if (g.AutoLock) g.AutoLock = false;
                    if (g.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.ReadyToLock) g.Lock();
                }
                if (o.Arrived)
                {
                    if (parkArrivedFrame == 0) parkArrivedFrame = now;
                    else if (now - parkArrivedFrame > PARK_LOCK_WAIT_TICKS) ParkFailed(Loc.T(StrNoGearCouldLock));
                }
            }
        }

        private void OnParked()
        {
            parkPhase = ParkPhase.None;
            parkOrder = null;
            parkFails = 0;
            parkRejected.Clear();
            ClearFlightOrder();
            ClearAllThrustOverrides();
            ReleaseGyroscopes();
            ResetIntegralTerms();
            CaptureFlightState();
            parkedSpots[Entity.EntityId] = new BoundingSphereD(HullCentreWorld, HullRadiusLevel());
            SleepSystems();
            isParked = true;
            SetState(State.Docked);
            Report(StrParkedOnLandingGear);
            SaveSettings();
        }

        private void ParkFailed(string why)
        {
            parkFails++;
            parkRejected.Add(parkSpot);
            Log.Debug("Drone {0}: park: failed ({1}), attempt {2}", Entity.EntityId, why, parkFails);
            EndPark(why);
            if (parkFails < 3) { if (GoPark()) return; }
            Report(StrParkFailedWaiting, why);
            parkFails = 0;
            parkRejected.Clear();
            SetState(State.Standby);
        }

        private void EndPark(string why)
        {
            if (parkOrder != null && activeFlightOrder != null && (activeFlightOrder == parkOrder || IsConstructionOrderActive())) ClearFlightOrder();
            parkPhase = ParkPhase.None;
            parkOrder = null;
            parkedSpots.Remove(Entity.EntityId);
            for (int i = 0; i < landingGears.Count; i++)
                if (!landingGears[i].Closed && landingGears[i].Enabled && !landingGears[i].IsLocked) landingGears[i].Enabled = false;
        }

        /// <summary>Taking off: the spot is free again, gears off for the flight.</summary>
        private void OnTakeOffGears()
        {
            parkedSpots.Remove(Entity.EntityId);
            for (int i = 0; i < landingGears.Count; i++)
            {
                var g = landingGears[i];
                if (g.Closed) continue;
                if (g.IsLocked) g.Unlock();
                if (g.Enabled) g.Enabled = false;
            }
        }

        /// <summary>In flight, every 10 ticks: gears stay off (a gear that grabbed something lets go), except landing.</summary>
        private void KeepGearsOff()
        {
            if (parkPhase != ParkPhase.None && activeFlightOrder == parkOrder) return;
            for (int i = 0; i < landingGears.Count; i++)
            {
                var g = landingGears[i];
                if (g.Closed) continue;
                if (g.IsLocked) g.Unlock();
                if (g.Enabled) g.Enabled = false;
            }
        }

        /// <summary>After a load: a drone parked on its gears holds its spot again.</summary>
        private void RegisterParkedSpot()
        {
            if (ParkMode && AnyLandingGearLocked()) parkedSpots[Entity.EntityId] = new BoundingSphereD(HullCentreWorld, HullRadiusLevel());
        }

        private void ForgetParkedSpot()
        {
            parkedSpots.Remove(Entity.EntityId);
        }
        #endregion

        #region Charge at any connector it can dock at
        private readonly List<IMyShipConnector> chargeCandidates = new List<IMyShipConnector>();

        /// <summary>Park mode, low: the nearest free friendly connector the drone can dock at, to refuel there.</summary>
        // 'rest': to rest after work (no home connector, no gear): a free connector of the grid it left from first, then
        // the nearest to where it left from; otherwise (recharge on the way) the nearest to the drone
        private bool GoCharge(bool rest = false)
        {
            if (AnyConnectorsConnected()) return true;   // already at one: refuelling there
            CaptureFlightState();
            var faces = DockFaces();
            var smallFaces = DockFaces(true);
            if (faces.Count == 0 && smallFaces.Count == 0) return false;
            Vector3D here = rest && settings.HasDeparture ? settings.DepartureSpot.ToVector3D() : flightState.Position;
            var departed = rest ? DepartureGrid() : null;
            Vector3D g = flightState.InGravity ? flightState.Gravity : Vector3D.Zero;
            nearEntities.Clear();
            var sphere = new BoundingSphereD(here, CHARGE_SEARCH_RADIUS);
            nearEntities.AddRange(MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere));
            chargeCandidates.Clear();
            for (int i = 0; i < nearEntities.Count; i++)
            {
                var grid = nearEntities[i] as IMyCubeGrid;
                if (grid == null || grid.Physics == null || droneGrids.Contains(grid)) continue;
                foreach (var c in grid.GetFatBlocks<IMyShipConnector>())
                {
                    if (c.Closed || !c.IsWorking || !AnchorDirectory.IsAllowed(block.OwnerId, c.OwnerId)) continue;
                    var sized = Logistics.ConveyorNetwork.IsStandardConnector(c) ? faces : smallFaces;   // sizes mate with their own
                    if (sized.Count == 0 || ConnectorBusy(c) != null) continue;
                    Vector3D p, axis;
                    ConnectorGeometry(c, out p, out axis);
                    byte facing = Logistics.DockFit.FacingIn(axis, g);
                    bool fits = false;
                    for (int f = 0; f < sized.Count && !fits; f++) fits = Logistics.DockFit.Fits(sized[f], facing, flightState.InGravity);
                    if (fits) chargeCandidates.Add(c);
                }
            }
            nearEntities.Clear();
            chargeCandidates.Sort((a, b) =>
            {
                if (departed != null && (a.CubeGrid == departed) != (b.CubeGrid == departed)) return a.CubeGrid == departed ? -1 : 1;
                return Vector3D.DistanceSquared(a.GetPosition(), here).CompareTo(Vector3D.DistanceSquared(b.GetPosition(), here));
            });
            for (int i = 0; i < chargeCandidates.Count && i < 5; i++)
            {
                if (DockAtConnector(chargeCandidates[i]))
                {
                    Report(StrRechargeDockingAt, chargeCandidates[i].CustomName);
                    chargeCandidates.Clear();
                    return true;
                }
            }
            Log.Debug("Drone {0}: recharge: no free connector it can dock at within {1:F0} m ({2} candidate(s))", Entity.EntityId, CHARGE_SEARCH_RADIUS, chargeCandidates.Count);
            chargeCandidates.Clear();
            return false;
        }
        #endregion
    }
}
