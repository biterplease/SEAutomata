using System;
using System.Collections.Generic;

using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Config;
using Automata.Util.Logging;
using VRage.Utils;
using Automata.Util;

namespace Automata.Pathfinding
{
    /// <summary>What a drone tells the pathfinder about itself for one query. Built by the drone, no copies kept.</summary>
    public struct PathQuery
    {
        /// <summary>Controller world matrix (position + attitude the route is flown with: legs hold it).</summary>
        public MatrixD Controller;
        /// <summary>Hull box of the whole drone (mechanical group), controller frame.</summary>
        public BoundingBoxD LocalHull;
        /// <summary>The drone's own grids: never obstacles.</summary>
        public List<IMyCubeGrid> OwnGrids;
        public List<IMyCameraBlock> Cameras;
        public List<IMySensorBlock> Sensors;
        /// <summary>Unit, away from the planet; zero in space.</summary>
        public Vector3D GravityUp;
        /// <summary>m kept between the hull and obstacles when placing detour points.</summary>
        public double Clearance;
        /// <summary>Blocks the dock routine switched off until take-off (cameras / sensors among them count as on); may be null.</summary>
        public List<long> SleepingIds;
        /// <summary>Radians the drone may pitch away from level in gravity (Align to P-Gravity); negative = no limit.</summary>
        public double MaxPitch;
        /// <summary>Legs get the attitude of their direction (nose = controller forward), within MaxPitch.</summary>
        public bool AlignToRoute;
        /// <summary>m above the surface for the route between take-off and landing (planets; 0 = none).</summary>
        public double MinAltitude;
        /// <summary>The goal may be right next to something (a point the player picked): no berth beyond it.</summary>
        public bool LooseGoal;
        /// <summary>Route points are where the hull box centre goes (legs steer it); false: the controller.</summary>
        public bool HullCentred;
        /// <summary>Checks at a known work site (the construction computer knows it): not limited by the instruments.</summary>
        public bool SeeAll;
        /// <summary>m kept between the hull and obstacles by the checks (0: the server's ObstacleClearance).</summary>
        public double Berth;
        /// <summary>
        /// Boxed in where it starts (set by the search, ThetaStage): within EscapeRadius of EscapeCentre the search and
        /// the check of its answer keep EscapeBerth (the dock corridor's clearance) instead of the berth. 0: off.
        /// </summary>
        public double EscapeRadius;
        public Vector3D EscapeCentre;
        public double EscapeBerth;
        /// <summary>A block (its cells) that is not an obstacle: the one being worked on.</summary>
        public IMyCubeGrid IgnoreGrid;
        public Vector3I IgnoreMin, IgnoreMax;
        /// <summary>The berth counts everywhere, also where the hull starts / ends (work site way in).</summary>
        public bool StrictEnds;
        /// <summary>
        /// Blocks of this grid in the way (their cell boxes) are checked once more against their real shape - rays
        /// through the swept box against the grid's physics (slopes, panels, thin models): only a ray hitting inside the
        /// block's cells blocks. Weld work sites (built neighbours of the block worked on). Null: cell boxes.
        /// </summary>
        public IMyCubeGrid RealShapeGrid;
        /// <summary>
        /// Set by Lazy Theta* when only a held attitude found a way (Controller then has that attitude): every leg of
        /// the route keeps it (AssignAttitudes), the turn to it is at the start.
        /// </summary>
        public bool HoldAttitude;
        /// <summary>Near a dock (docking / undocking): Lazy Theta* may hold the drone's attitude, never turn it to another.</summary>
        public bool NoHeldTurn;
        // What the drone sees from (its real attitude) when Controller was turned to a held attitude
        internal bool EyesSet;
        internal MatrixD Eyes;
        /// <summary>The asking drone's entity id (0: nobody, a warm-up): a search queued for it comes back to it.</summary>
        public long Owner;
        /// <summary>
        /// The caller can wait for an answer (it handles <see cref="PlanPendingException"/>): the expensive stages go to
        /// the search queue (worker threads) instead of running at once.
        /// </summary>
        public bool AsyncOk;

        // The instruments as of Frozen(): a search on another thread must not read the blocks
        internal bool Snap;
        internal Vector3D[] SensorPos;
        internal bool[] SensorOn;
        internal MatrixD[] CameraWorld;
        internal bool[] CameraOn;

        /// <summary>A copy for a search on another thread: own grids copied, instruments (position, working) captured.</summary>
        internal PathQuery Frozen()
        {
            PathQuery f = this;
            if (OwnGrids != null) f.OwnGrids = new List<IMyCubeGrid>(OwnGrids);
            int ns = Sensors != null ? Sensors.Count : 0, nc = Cameras != null ? Cameras.Count : 0;
            f.SensorPos = new Vector3D[ns];
            f.SensorOn = new bool[ns];
            for (int i = 0; i < ns; i++)
                if (f.SensorOn[i] = PathfindingManager.InstrumentOn(Sensors[i], SleepingIds)) f.SensorPos[i] = Sensors[i].GetPosition();
            f.CameraWorld = new MatrixD[nc];
            f.CameraOn = new bool[nc];
            for (int i = 0; i < nc; i++)
                if (f.CameraOn[i] = PathfindingManager.InstrumentOn(Cameras[i], SleepingIds)) f.CameraWorld[i] = Cameras[i].WorldMatrix;
            f.Sensors = null;
            f.Cameras = null;
            f.SleepingIds = null;
            f.Snap = true;
            return f;
        }
    }

    public enum RouteResult : byte
    {
        Direct,    // nothing seen in the way: no waypoints
        Detour,    // waypoints added
        Blocked,   // no way found within the attempts: don't fly it
        Pending,   // the search is queued / running (PathQuery.AsyncOk): ask again when it is done
    }

    /// <summary>
    /// Drone routes: direct pathfinding with geometric repositioning (A* is not wired yet, see
    /// TODO/04-pathfinding-architecture.md). One instance per session (server).
    ///
    /// Conversation with a drone:
    ///  1. Dispatch: the drone asks for a route start -> goal (<see cref="PlanRoute"/>). The pathfinder checks the
    ///     straight line against the scene (<see cref="LocalScene"/>: block boxes from the game's pruning structure,
    ///     cached in AABB trees shared by all drones; no rays except for asteroids): a box the size of the hull
    ///     plus the berth swept along it, as far as the drone can "see" (simulated cameras and sensors,
    ///     <see cref="ObservedRange"/>). Blocked: a detour point beside / over the obstacle, checked the same way,
    ///     then the rest again from there. Terrain: a climb over it. Beyond what the drone sees the line is assumed
    ///     clear; the drone asks again once it can see that part (just in time). Answer: the waypoints (or Blocked).
    ///  2. Flight: the drone flies them as legs; the pathfinder sets how each waypoint is passed from the turn
    ///     angle (<see cref="Classify"/>).
    ///  3. On the way, every few ticks, the drone looks ahead along the current leg (<see cref="CheckAhead"/>,
    ///     rays rationed per tick across all drones). Something new in the way: it asks for a new route from where
    ///     it is (back to 1). Too many of those for one task: the task fails (Unreachable).
    /// The final approach to a work face / connector is the drone's own (a straight, checked line).
    /// </summary>
    public sealed partial class PathfindingManager
    {
        #region UI strings
        private static readonly MyStringId StrSomething = MyStringId.GetOrCompute("something");
        private static readonly MyStringId StrAtOf = MyStringId.GetOrCompute("{0} at {1} of {2}");
        #endregion

        /// <summary>Kept for the legacy A* / direct planners (IPathfinder), which are not wired.</summary>
        public enum Method : byte { None, Direct, AStar }

        public static PathfindingManager Instance { get; private set; }

        /// <summary>Shared memory of flown links (stage 2); null when off (UsePathMemory) or not the server.</summary>
        public RouteMemory Memory { get; private set; }
        /// <summary>Directional flight lanes; null on clients.</summary>
        public FlightLanes Lanes { get; private set; }
        internal LocalScene LiveScene { get { return liveScene; } }
        /// <summary>A worker's planner: set when the world unloads, its search stops at the next check.</summary>
        internal volatile bool CancelSearch;
        /// <summary>Queue of searches run on worker threads (Lazy Theta*); null when off (PathSearchWorkers = 0) or not the server.</summary>
        public PathSearchQueue Searches { get; private set; }
        private int lastPlanFrame = -1;

        public static void Load(PathfindingConfig config)
        {
            Instance = new PathfindingManager(config);
            if (config.UsePathMemory && (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer))
            {
                Instance.Memory = new RouteMemory(config.PathMemoryMaxNodes, config.PathMemoryMaxAgeHours);
                Instance.Memory.Load(MyAPIGateway.Session != null ? MyAPIGateway.Session.GameplayFrameCounter : 0);
            }
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer)
            {
                Instance.Lanes = new FlightLanes(config);
                Instance.Lanes.Load(MyAPIGateway.Session != null ? MyAPIGateway.Session.GameplayFrameCounter : 0);
            }
            if (config.PathSearchWorkers > 0 && config.AllowLazyTheta && (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer))
                Instance.Searches = new PathSearchQueue(Instance, config);
            if (Log.DebugEnabled) Log.Debug("Pathfinding: chain straight line > {0} > {1} > repositioning", config.UsePathMemory ? "memory" : "(memory off)", config.AllowLazyTheta ? "Lazy Theta*" : "(Lazy Theta* off)");
        }

        /// <summary>World save (session SaveData): the memory goes to world storage.</summary>
        public void SaveData()
        {
            if (Memory != null) Memory.Save(frame);
            if (Lanes != null) Lanes.Save(frame);
        }

        public static void Unload()
        {
            if (Instance != null)
            {
                if (Instance.Searches != null) Instance.Searches.Shutdown();
                Instance.liveScene.Clear();
            }
            Instance = null;
        }

        private readonly PathfindingConfig config;
        private readonly List<IHitInfo> hits = new List<IHitInfo>();
        private readonly List<Vector3D> points = new List<Vector3D>();
        private int raysLeft;           // this tick, for look-ahead checks
        private int planRays;           // rays (asteroids) spent by the current plan
        /// <summary>Rays the real-shape checks (PathQuery.RealShapeGrid) may still spend; out of them: cell boxes count.</summary>
        public int RealShapeRayBudget;
        /// <summary>Cell-box hits the real-shape check found clear / rays spent (for the log; reset by the caller).</summary>
        public int RealShapeClears, RealShapeRays;
        private readonly HashSet<IMySlimBlock> realShapeCleared = new HashSet<IMySlimBlock>();   // this sweep's blocks found clear
        private int planChecks;         // scene checks (box sweeps, spheres) of the current plan
        private int blocksTested;       // block boxes tested exactly by the current plan
        private int unseen;             // stretches of the current PlanRoute the drone couldn't fully see
        private int planUnseen;         // same, the whole plan (BeginPlan)
        private int frame;
        private readonly LocalScene liveScene;   // the game's scene (main thread); null in a worker's planner
        private readonly ISceneView scene;       // what the checks read: liveScene, or a worker's snapshot of it
        private readonly bool offThread;         // a worker's planner: no rays, no log, nothing read from game objects
        private readonly List<LocalScene.Entry> entryBuffer = new List<LocalScene.Entry>();
        private readonly List<IMySlimBlock> slimBuffer = new List<IMySlimBlock>();
        private readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        private int lookups0, trees0, indexed0;     // scene counters at BeginPlan
        private double build0;

        /// <summary>Would the hull (any attitude) fit at 'point'? From what the drone sees.</summary>
        public bool PointFree(ref PathQuery q, Vector3D point)
        {
            return HullFits(ref q, point);
        }

        /// <summary>
        /// Does the hull fit with its centre at 'centre'? Gravity: level, any heading - the box around the cylinder the
        /// hull sweeps yawing (a floor right below or a ceiling above doesn't count, unlike a sphere of the hull's
        /// half diagonal). Space: that sphere.
        /// </summary>
        public bool HullFits(ref PathQuery q, Vector3D centre)
        {
            Vector3D g = q.GravityUp;
            if (g.LengthSquared() < 0.5 || q.LocalHull.Max.X < q.LocalHull.Min.X) return HullSpaceFree(ref q, centre, HullRadius(ref q));
            MatrixD level = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.CalculatePerpendicularVector(g), g);
            double rh, hv;
            LevelReach(ref q, ref level, out rh, out hv);
            var box = new MyOrientedBoundingBoxD(centre, new Vector3D(rh, hv, rh), Quaternion.CreateFromRotationMatrix(level));
            return BoxFree(ref q, ref box);
        }

        /// <summary>The hull box at 'centre' (hull centre, world) in the exact attitude 'frame' (rotation only): blocks and drones clear.</summary>
        public bool HullFitsOriented(ref PathQuery q, Vector3D centre, ref MatrixD frame)
        {
            return HullFitsOriented(ref q, centre, ref frame, 0);
        }

        /// <summary>Same, the box grown by 'margin' m on every side.</summary>
        public bool HullFitsOriented(ref PathQuery q, Vector3D centre, ref MatrixD frame, double margin)
        {
            if (q.LocalHull.Max.X < q.LocalHull.Min.X) return HullSpaceFree(ref q, centre, HullRadius(ref q));
            var box = new MyOrientedBoundingBoxD(centre, q.LocalHull.HalfExtents + new Vector3D(margin), Quaternion.CreateFromRotationMatrix(frame));
            return BoxFree(ref q, ref box);
        }

        // The hull's horizontal reach and half height about its centre, level ('level': any heading, up = gravity up)
        private static void LevelReach(ref PathQuery q, ref MatrixD level, out double rh, out double hv)
        {
            rh = 0;
            hv = 0;
            Vector3D g = level.Up;
            Vector3D hc = q.LocalHull.Center;
            for (int k = 0; k < 8; k++)
            {
                Vector3D c = new Vector3D((k & 1) != 0 ? q.LocalHull.Max.X : q.LocalHull.Min.X,
                                          (k & 2) != 0 ? q.LocalHull.Max.Y : q.LocalHull.Min.Y,
                                          (k & 4) != 0 ? q.LocalHull.Max.Z : q.LocalHull.Min.Z);
                Vector3D w = Vector3D.TransformNormal(c - hc, level);
                double v = Vector3D.Dot(w, g);
                rh = Math.Max(rh, (w - g * v).Length());
                hv = Math.Max(hv, Math.Abs(v));
            }
        }

        /// <summary>"subtype at cell of grid name" for the logs.</summary>
        public static string Describe(IMySlimBlock b)
        {
            if (b == null) return Loc.T(StrSomething);
            return Loc.F(StrAtOf, b.BlockDefinition.Id.SubtypeName, b.Min, b.CubeGrid != null ? b.CubeGrid.DisplayName : "?");
        }

        /// <summary>The block the last SegmentBlocked / HullSpaceFree hit (null: none / an asteroid). For the logs.</summary>
        public IMySlimBlock LastHitBlock { get; private set; }
        /// <summary>What the last failed plan couldn't get past (the goal taken, no way around it); null: nothing in particular.</summary>
        public IMyEntity LastBlocker { get; private set; }

        private const double TERRAIN_END_MARGIN = 15.0; // m at each end of a leg not checked for terrain (work near ground)
        private const int MAX_PLAN_CHECKS = 400;        // scene checks + rays per route (BeginPlan)
        private const double SWEEP_CHUNK_MIN = 20.0;    // m: long sweeps are checked in pieces (tighter boxes)
        private const int DETOUR_STEPS = 4;             // offsets tried: step, 2x, 4x, 8x
        private const int STAGE1_DETOURS = 3;           // greedy detours before the chain moves on (when Lazy Theta* is there)
        /// <summary>Turns below this many degrees are flown through at speed (Classify; the drone's attitude-change rule too).</summary>
        public const double RUN_THROUGH_DEGREES = 25;
        // Corner rounding (RoundCorners): sharper turns become an arc of chords, each turning at most CORNER_SEGMENT_DEGREES;
        // its points are flown through (IsArcPoint) up to ARC_RUN_THROUGH_DEGREES, past the usual RUN_THROUGH_DEGREES
        private const double CORNER_SEGMENT_DEGREES = 44;   // per chord of the arc (was 22: twice the legs)
        public const double ARC_RUN_THROUGH_DEGREES = 45;
        private const int ARC_POINTS_MAX = 4096;            // remembered arc points (cleared when full)
        private readonly HashSet<Vector3D> arcPoints = new HashSet<Vector3D>();

        /// <summary>A point of a rounded corner's arc (every chord of it was checked clear): flown through, not stopped at.</summary>
        public bool IsArcPoint(Vector3D p) { return arcPoints.Contains(p); }
        private const double CORNER_CUT_MAX = 25;            // m from the corner where the arc starts / ends, at most
        private const double CORNER_CUT_SHARE = 0.4;         // ...and at most this share of each neighbouring leg
        private const double CORNER_LEG_MIN = 2;             // m: shorter legs keep their corner

        private PathfindingManager(PathfindingConfig config)
        {
            this.config = config;
            liveScene = new LocalScene();
            scene = liveScene;
        }

        /// <summary>A planner for searches on a worker thread: reads its snapshot and the query only (see PathSearchQueue).</summary>
        internal PathfindingManager(PathfindingConfig config, SnapshotScene snapshot)
        {
            this.config = config;
            scene = snapshot;
            offThread = true;
        }

        /// <summary>Session, every tick: refills the look-ahead ray budget shared by all drones; scene upkeep.</summary>
        public void Update()
        {
            raysLeft = config.RaysPerTick;
            frame = MyAPIGateway.Session.GameplayFrameCounter;
            liveScene.Update(frame);
            if (Searches != null) Searches.Update(frame);
            if (Memory != null) Memory.Update(frame);
            if (Lanes != null)
            {
                // Idle: nothing planned this tick (or the last), and the simulation keeps up
                bool idle = frame - lastPlanFrame > 1 && MyAPIGateway.Physics.SimulationRatio > 0.9f;
                Lanes.Update(this, frame, idle);
            }
        }

        #region Instruments (simulated)
        /// <summary>
        /// Localized sensing (TODO/20, section 2). What the drone sees is where its instruments are, not where its
        /// controller is:
        ///  - Sensors: SimulatedSensorRange around each working (functional, switched-on) sensor block. A long ship with
        ///    one sensor at an end is aware only around that end.
        ///  - Cameras: CameraRange over a 90° square cone from each working camera's own position along its facing.
        ///  - Sensors not required (servers without them): SimulatedSensorRange outward from the hull (its box).
        ///  - Cameras not required: one simulated camera, from the front of the hull along the drone's forward.
        /// Never fired as real camera raycasts / sensor queries.
        /// </summary>
        public double ObservedRange(ref PathQuery q, Vector3D dir)
        {
            if (q.SeeAll) return 1e7;
            double from, to;
            if (!SeenSpan(ref q, q.Controller.Translation, dir, 1e5, out from, out to) || from > SeenSlack(ref q)) return 0;
            return to;
        }

        // A camera ahead of the controller sees from a few metres on: a seen stretch starting within this counts as
        // starting at the drone
        private static double SeenSlack(ref PathQuery q)
        {
            return 2 * HullRadius(ref q) + 5;
        }

        /// <summary>
        /// The first seen stretch of the line a + dir t, t in [0, len] (gaps under a metre bridged): [from, to]. False:
        /// nothing of it is seen.
        /// </summary>
        public bool SeenSpan(ref PathQuery q, Vector3D a, Vector3D dir, double len, out double from, out double to)
        {
            from = 0; to = len;
            if (q.SeeAll) return true;
            spans.Clear();
            double sensorRange = config.SimulatedSensorRange, camRange = config.MaxSimulatedCameraRaycastMeters();
            if (config.RequireSensorsForPathfinding())
            {
                if (q.Snap)
                {
                    for (int i = 0; i < q.SensorPos.Length; i++)
                        if (q.SensorOn[i]) SphereSpan(a, dir, len, q.SensorPos[i], sensorRange);
                }
                else if (q.Sensors != null)
                    for (int i = 0; i < q.Sensors.Count; i++)
                        if (InstrumentOn(q.Sensors[i], q.SleepingIds)) SphereSpan(a, dir, len, q.Sensors[i].GetPosition(), sensorRange);
            }
            else HullSpan(ref q, a, dir, len, sensorRange);
            if (config.RequireCamerasForPathfinding())
            {
                if (q.Snap)
                {
                    for (int i = 0; i < q.CameraWorld.Length; i++)
                    {
                        if (!q.CameraOn[i]) continue;
                        MatrixD m = q.CameraWorld[i];
                        ConeSpan(a, dir, len, m.Translation, m.Forward, m.Right, m.Up, camRange);
                    }
                }
                else if (q.Cameras != null)
                    for (int i = 0; i < q.Cameras.Count; i++)
                    {
                        if (!InstrumentOn(q.Cameras[i], q.SleepingIds)) continue;
                        MatrixD m = q.Cameras[i].WorldMatrix;
                        ConeSpan(a, dir, len, m.Translation, m.Forward, m.Right, m.Up, camRange);
                    }
            }
            else
            {
                MatrixD c = EyesOf(ref q);
                Vector3D apex = c.Translation;
                if (q.LocalHull.Max.X >= q.LocalHull.Min.X)
                    apex = Vector3D.Transform(q.LocalHull.Center + new Vector3D(0, 0, -q.LocalHull.HalfExtents.Z), c);   // the front of the hull (forward = -Z)
                ConeSpan(a, dir, len, apex, c.Forward, c.Right, c.Up, camRange);
            }
            if (spans.Count == 0) return false;
            spans.Sort((x, y) => x.X.CompareTo(y.X));
            from = spans[0].X;
            to = spans[0].Y;
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].X > to + 1) break;
                if (spans[i].Y > to) to = spans[i].Y;
            }
            return to > from;
        }

        private readonly List<Vector2D> spans = new List<Vector2D>();

        private void AddSpan(double t0, double t1, double len)
        {
            t0 = Math.Max(0, t0);
            t1 = Math.Min(len, t1);
            if (t1 > t0) spans.Add(new Vector2D(t0, t1));
        }

        // A sphere around 'c'
        private void SphereSpan(Vector3D a, Vector3D dir, double len, Vector3D c, double r)
        {
            Vector3D w = c - a;
            double t = Vector3D.Dot(w, dir);
            double d2 = w.LengthSquared() - t * t;
            if (d2 > r * r) return;
            double h = Math.Sqrt(r * r - d2);
            AddSpan(t - h, t + h, len);
        }

        // The hull's box grown by 'r' (the corners rounded off are counted in: a little generous)
        private void HullSpan(ref PathQuery q, Vector3D a, Vector3D dir, double len, double r)
        {
            if (q.LocalHull.Max.X < q.LocalHull.Min.X) { SphereSpan(a, dir, len, q.Controller.Translation, r + 2.5); return; }
            MatrixD inv = MatrixD.Invert(EyesOf(ref q));
            Vector3D la = Vector3D.Transform(a, inv), ld = Vector3D.TransformNormal(dir, inv);
            Vector3D min = q.LocalHull.Min - new Vector3D(r), max = q.LocalHull.Max + new Vector3D(r);
            double t0 = double.MinValue, t1 = double.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                double o = k == 0 ? la.X : k == 1 ? la.Y : la.Z, d = k == 0 ? ld.X : k == 1 ? ld.Y : ld.Z;
                double lo = k == 0 ? min.X : k == 1 ? min.Y : min.Z, hi = k == 0 ? max.X : k == 1 ? max.Y : max.Z;
                if (Math.Abs(d) < 1e-9) { if (o < lo || o > hi) return; continue; }
                double u = (lo - o) / d, v = (hi - o) / d;
                if (u > v) { double x = u; u = v; v = x; }
                t0 = Math.Max(t0, u);
                t1 = Math.Min(t1, v);
            }
            if (t1 > t0) AddSpan(t0, t1, len);
        }

        // A 90° square cone from 'apex' along 'f' (sides along 'r' and 'u'), 'range' long
        private void ConeSpan(Vector3D a, Vector3D dir, double len, Vector3D apex, Vector3D f, Vector3D r, Vector3D u, double range)
        {
            Vector3D w = a - apex;
            double t0 = 0, t1 = len;
            // Each side: alpha + beta t >= 0
            if (!HalfLine(Vector3D.Dot(w, f), Vector3D.Dot(dir, f), ref t0, ref t1)) return;
            if (!HalfLine(Vector3D.Dot(w, f - r), Vector3D.Dot(dir, f - r), ref t0, ref t1)) return;
            if (!HalfLine(Vector3D.Dot(w, f + r), Vector3D.Dot(dir, f + r), ref t0, ref t1)) return;
            if (!HalfLine(Vector3D.Dot(w, f - u), Vector3D.Dot(dir, f - u), ref t0, ref t1)) return;
            if (!HalfLine(Vector3D.Dot(w, f + u), Vector3D.Dot(dir, f + u), ref t0, ref t1)) return;
            // ...within the range
            double t = -Vector3D.Dot(w, dir);
            double d2 = w.LengthSquared() - t * t;
            if (d2 > range * range) return;
            double h = Math.Sqrt(range * range - d2);
            t0 = Math.Max(t0, t - h);
            t1 = Math.Min(t1, t + h);
            AddSpan(t0, t1, len);
        }

        private static bool HalfLine(double alpha, double beta, ref double t0, ref double t1)
        {
            if (Math.Abs(beta) < 1e-12) return alpha >= 0;
            double t = -alpha / beta;
            if (beta > 0) t0 = Math.Max(t0, t); else t1 = Math.Min(t1, t);
            return t1 > t0;
        }

        // Debug: why a stretch counted as unseen (what the drone sees, from where)
        private string DescribeView(ref PathQuery q, Vector3D start, Vector3D goal)
        {
            Vector3D d = goal - start;
            double len = d.Length();
            double range = len > 1e-3 ? ObservedRange(ref q, d / len) : 0;
            int sOn = 0, sAll = q.Sensors != null ? q.Sensors.Count : 0, cOn = 0, cAll = q.Cameras != null ? q.Cameras.Count : 0;
            for (int i = 0; i < sAll; i++) if (InstrumentOn(q.Sensors[i], q.SleepingIds)) sOn++;
            for (int i = 0; i < cAll; i++) if (InstrumentOn(q.Cameras[i], q.SleepingIds)) cOn++;
            return string.Format(" [sight {0:F0} m that way, sensors {1}/{2} on, cameras {3}/{4} on, start {5:F0} m from the controller]",
                                 range, sOn, sAll, cOn, cAll, Vector3D.Distance(start, q.Controller.Translation));
        }

        private static bool AnyOn<T>(List<T> blocks, List<long> sleeping) where T : class, IMyFunctionalBlock
        {
            if (blocks == null) return false;
            for (int i = 0; i < blocks.Count; i++)
                if (InstrumentOn(blocks[i], sleeping)) return true;
            return false;
        }

        /// <summary>Functional and switched on (or switched off by the dock routine: 'sleeping', may be null).</summary>
        public static bool InstrumentOn(IMyFunctionalBlock b, List<long> sleeping)
        {
            return b != null && !b.Closed && b.IsFunctional && (b.Enabled || (sleeping != null && sleeping.Contains(b.EntityId)));
        }
        #endregion

        #region Scene checks
        /// <summary>
        /// Anything in the way of the hull flying a -> b, as far as the drone at q.Controller can see?
        /// </summary>
        /// <summary>Blocks the hull already overlapped at a segment's start that it moved away from (diagnostics).</summary>
        public int StartContactsLeft;

        public bool SegmentBlocked(ref PathQuery q, Vector3D a, Vector3D b, double skipStart, out Vector3D hit, out IMyEntity hitEntity)
        {
            return SegmentBlocked(ref q, ref q.Controller, a, b, skipStart, out hit, out hitEntity);
        }

        /// <summary>
        /// Anything in the way of the hull flying a -> b, held at 'attitude' (rotation only) on that leg, as far as the
        /// drone at q.Controller can see? Checked against the scene (<see cref="LocalScene"/>: block boxes), no rays:
        /// one box swept along the leg, the hull's size across it plus the berth on each side, from 'skipStart' (where
        /// the hull centre already is) to b - plus, with 'extendPastEnd', where the hull's front (and 'frontBerth') reaches
        /// when it is at b. Where the hull (with the berth) already is at the start, or will be at b, only what the hull
        /// itself would touch counts: leaving a work site / arriving at one isn't blocked by what is merely close.
        /// Asteroids: rays (block boxes can't describe voxels). The drone's grids, characters, floating objects and
        /// planets (terrain is handled separately) are not obstacles. 'hit': where the obstacle starts along a -> b.
        /// </summary>
        public bool SegmentBlocked(ref PathQuery q, ref MatrixD attitude, Vector3D a, Vector3D b, double skipStart,
                                   out Vector3D hit, out IMyEntity hitEntity, bool extendPastEnd = true, bool frontBerth = true)
        {
            hit = Vector3D.Zero;
            hitEntity = null;
            LastHitBlock = null;
            realShapeCleared.Clear();
            Vector3D along = b - a;
            double len = along.Length();
            if (len < 0.1) return false;
            Vector3D dir = along / len;
            // Observed part: the first stretch of the line the drone's instruments see (localized sensing)
            double seenFrom, seen;
            if (!SeenSpan(ref q, a, dir, len + 50, out seenFrom, out seen)) { unseen++; return false; }
            if (seen < len || seenFrom > SeenSlack(ref q)) unseen++;   // (partly) unobserved: assumed clear, checked again on the way
            skipStart = Math.Max(skipStart, seenFrom);
            if (seen <= skipStart) return false;

            Vector3D side1, side2;
            double s1, s2;
            CrossSection(ref q, ref attitude, dir, out side1, out s1, out side2, out s2);
            double berth = Berth(ref q);
            Vector3D halfBox = HalfBox(ref q);
            double halfAlong = HalfSizeAlong(ref attitude, ref halfBox, dir);
            // The points are hull centres, or controller positions offset to it
            Vector3D a0 = a + (q.HullCentred ? Vector3D.Zero : Vector3D.TransformNormal(q.LocalHull.Center, attitude));
            double t0 = skipStart;
            double t1 = Math.Min(len + (extendPastEnd ? halfAlong + (frontBerth ? berth : 0) : 0), seen);
            if (t1 <= t0) return false;
            double startZone = q.StrictEnds ? double.MinValue : t0 + halfAlong + berth;
            double endZone = extendPastEnd && !q.StrictEnds ? len - halfAlong - berth : double.MaxValue;

            // The hull where it is now (its centre at a0, held attitude): what it already overlaps behind / beside it
            // doesn't block the way away from it (a drone wedged in: its box over a block's cells blocked every way out)
            var startHull = new MyOrientedBoundingBoxD(a0, halfBox, Quaternion.CreateFromRotationMatrix(attitude));

            // Box axes: X = side2, Y = side1, Z = -dir
            MatrixD axes = MatrixD.CreateWorld(Vector3D.Zero, dir, side1);
            Quaternion orientation = Quaternion.CreateFromRotationMatrix(axes);
            double chunk = Math.Max(SWEEP_CHUNK_MIN, 2 * (Math.Max(s1, s2) + berth));
            double best = double.MaxValue;
            IMyEntity bestEntity = null;
            IMySlimBlock bestBlock = null;
            bool voxels = false;
            for (double c0 = t0; c0 < t1 && best == double.MaxValue; c0 += chunk)
            {
                double c1 = Math.Min(t1, c0 + chunk);
                planChecks++;
                Vector3D mid = a0 + dir * ((c0 + c1) * 0.5);
                var wide = new MyOrientedBoundingBoxD(mid, new Vector3D(s2 + berth, s1 + berth, (c1 - c0) * 0.5), orientation);
                var tight = new MyOrientedBoundingBoxD(mid, new Vector3D(s2, s1, (c1 - c0) * 0.5), orientation);
                BoundingBoxD wb = wide.GetAABB();
                scene.Ensure(new BoundingSphereD(wb.Center, wb.HalfExtents.Length()), frame);
                scene.Overlapping(ref wb, entryBuffer, frame);
                for (int i = 0; i < entryBuffer.Count; i++)
                {
                    var entry = entryBuffer[i];
                    if (entry.Voxel) { voxels = true; continue; }
                    var grid = entry.Grid;
                    if (IsOwn(ref q, grid)) continue;
                    if (trustSeg >= 0 && acceptTrust.Contains(entry)) continue;   // unchanged since the worker checked this stretch against it
                    var blocks = scene.BlocksOf(entry);
                    if (blocks == null) continue;
                    MatrixD inv = entry.Inv;
                    MyOrientedBoundingBoxD wl = ToLocal(ref wide, ref inv), tl = ToLocal(ref tight, ref inv);
                    BoundingBoxD lb = wl.GetAABB();
                    blocks.OverlapAllBoundingBox(ref lb, slimBuffer, 0u, true);
                    Vector3D aL = Vector3D.Transform(a0, inv), dL = Vector3D.TransformNormal(dir, inv);
                    for (int k = 0; k < slimBuffer.Count; k++)
                    {
                        var slim = slimBuffer[k];
                        if (Ignored(ref q, grid, slim)) continue;
                        BoundingBoxD box = LocalScene.BlockBox(slim, entry.GridSize);
                        blocksTested++;
                        if (!wl.Intersects(ref box)) continue;
                        // An open door / gate: its cells span the doorway, so its box would close it; checked with rays
                        // against its real shape (the frame) below
                        if (IsOpenDoor(slim))
                        {
                            if (!doorBlocks.Contains(slim)) { doorBlocks.Add(slim); doorGrids.Add(grid); }
                            continue;
                        }
                        Vector3D h = box.HalfExtents;
                        double reach = Math.Abs(dL.X) * h.X + Math.Abs(dL.Y) * h.Y + Math.Abs(dL.Z) * h.Z;
                        double centreAlong = Vector3D.Dot(box.Center - aL, dL);
                        double near = centreAlong - reach, far = centreAlong + reach;
                        // Where the drone already is / where it arrives: only what the hull itself touches
                        bool endsZone = near < startZone || far > endZone;
                        if (endsZone && !tl.Intersects(ref box)) continue;
                        // Already overlapping it where the drone is, and not moving into it (its centre not ahead):
                        // leaving isn't blocked by it. Not at work site way-ins (StrictEnds).
                        if (!q.StrictEnds && near < startZone && centreAlong <= 0)
                        {
                            MyOrientedBoundingBoxD sl = ToLocal(ref startHull, ref inv);
                            if (sl.Intersects(ref box)) { StartContactsLeft++; continue; }
                        }
                        double t = Math.Max(near, t0);
                        if (t < best && q.RealShapeGrid != null && grid == q.RealShapeGrid
                            && (realShapeCleared.Contains(slim) || RealShapeClear(grid, ref box, ref inv, a0, dir, near - 0.5, Math.Min(far + 0.5, t1),
                                              side1, endsZone ? s1 : s1 + berth, side2, endsZone ? s2 : s2 + berth)))
                        {
                            if (realShapeCleared.Add(slim)) RealShapeClears++;   // (a block spans several chunks: once)
                            continue;
                        }
                        if (t < best)
                        {
                            best = t;
                            bestEntity = grid;
                            bestBlock = slim;
                            if (!entry.Static || slim.FatBlock is IMyDoor) planHitDynamic = true;
                        }
                    }
                    slimBuffer.Clear();
                }
                entryBuffer.Clear();
            }
            // The doors the worker met on this stretch of its answer (in grids not looked at again here): their rays now
            if (trustSeg >= 0 && acceptResult != null && acceptResult.Doors != null)
                for (int k = 0; k < acceptResult.Doors.Count; k++)
                {
                    var d = acceptResult.Doors[k];
                    if (d.Seg == trustSeg && !doorBlocks.Contains(d.Block)) { doorBlocks.Add(d.Block); doorGrids.Add(d.Grid); }
                }
            // A worker: the doors of its answer's stretches go with the answer (rays are the main thread's)
            if (offThread && searchSeg >= 0)
                for (int k = 0; k < doorBlocks.Count; k++) searchDoors.Add(new SearchDoor { Seg = searchSeg, Block = doorBlocks[k], Grid = doorGrids[k] });
            if (doorBlocks.Count > 0)
            {
                double td;
                IMyCubeGrid dg;
                IMySlimBlock db;
                if (DoorRaysBlocked(a0, dir, t0, t1, side1, s1 + berth, side2, s2 + berth, out td, out dg, out db) && td < best)
                {
                    best = td;
                    bestEntity = dg;
                    bestBlock = db;
                    planHitDynamic = true;
                }
                doorBlocks.Clear();
                doorGrids.Clear();
            }
            if (voxels)
            {
                // Asteroids: the hull-sized ray bundle, only where there is one
                Vector3D vh;
                IMyEntity ve;
                if (RaySegmentBlocked(ref q, ref attitude, a, b, skipStart, out vh, out ve, extendPastEnd, frontBerth, true))
                {
                    double t = Vector3D.Dot(vh - a, dir);
                    if (t < best) { best = t; bestEntity = ve; bestBlock = null; }
                }
            }
            if (best == double.MaxValue) return false;
            hit = a + dir * best;
            hitEntity = bestEntity;
            LastHitBlock = bestBlock;
            return true;
        }

        /// <summary>The 5-ray bundle (hull centre + the four sides of the cross-section plus the berth): look-ahead, asteroids.</summary>
        private bool RaySegmentBlocked(ref PathQuery q, ref MatrixD attitude, Vector3D a, Vector3D b, double skipStart,
                                       out Vector3D hit, out IMyEntity hitEntity, bool extendPastEnd, bool frontBerth, bool voxelsOnly)
        {
            hit = Vector3D.Zero;
            hitEntity = null;
            if (offThread) return false;   // physics rays are for the main thread: the route is checked again there
            Vector3D along = b - a;
            double len = along.Length();
            if (len < 0.1) return false;
            Vector3D dir = along / len;
            double seenFrom, seen;
            if (!SeenSpan(ref q, a, dir, len + 50, out seenFrom, out seen)) return false;
            skipStart = Math.Max(skipStart, seenFrom);
            if (seen <= skipStart) return false;

            Vector3D side1, side2;
            double s1, s2;
            CrossSection(ref q, ref attitude, dir, out side1, out s1, out side2, out s2);
            double berth = Berth(ref q);
            Vector3D halfBox = HalfBox(ref q);
            double end = Math.Min(len + (extendPastEnd ? HalfSizeAlong(ref attitude, ref halfBox, dir) + (frontBerth ? berth : 0) : 0), seen);
            Vector3D centre = q.HullCentred ? Vector3D.Zero : Vector3D.TransformNormal(q.LocalHull.Center, attitude);
            double best = double.MaxValue;
            for (int r = 0; r < 5; r++)
            {
                Vector3D o = centre + (r == 0 ? Vector3D.Zero
                                     : r == 1 ? side1 * (s1 + berth) : r == 2 ? -side1 * (s1 + berth)
                                     : r == 3 ? side2 * (s2 + berth) : -side2 * (s2 + berth));
                Vector3D from = a + o, to = a + o + dir * end;
                hits.Clear();
                planRays++;
                MyAPIGateway.Physics.CastRay(from, to, hits);
                for (int i = 0; i < hits.Count; i++)
                {
                    var e = hits[i].HitEntity;
                    if (e == null || !IsObstacle(ref q, e)) continue;
                    if (voxelsOnly && !IsAsteroid(e.GetTopMostParent())) continue;
                    double t = Vector3D.Dot(hits[i].Position - from, dir);
                    if (t < skipStart || t >= best) continue;
                    best = t;
                    hit = a + dir * (t + Vector3D.Dot(o, dir));   // along the path of the controller
                    hitEntity = e.GetTopMostParent();
                }
            }
            hits.Clear();
            return best != double.MaxValue;
        }

        private static bool IsObstacle(ref PathQuery q, IMyEntity e)
        {
            var top = e.GetTopMostParent();
            if (top is IMyCharacter || top is IMyFloatingObject) return false;
            var voxel = top as MyVoxelBase;
            if (voxel != null && !IsAsteroid(voxel)) return false;   // planets (and their physics pieces): terrain checks
            var grid = top as IMyCubeGrid;
            if (grid != null && (grid.Physics == null || IsOwn(ref q, grid))) return false;
            return true;
        }

        /// <summary>A voxel map that isn't (part of) a planet.</summary>
        public static bool IsAsteroid(IMyEntity e)
        {
            var v = e as MyVoxelBase;
            return v != null && !(v is MyPlanet) && !(v.RootVoxel is MyPlanet);
        }

        private static bool IsOwn(ref PathQuery q, IMyCubeGrid grid)
        {
            return q.OwnGrids != null && q.OwnGrids.Contains(grid);
        }

        // The block the drone works on (its cells): not an obstacle to the way in
        /// <summary>
        /// A block whose cell box ('box', grid-local) is in the way of a swept box (axis 'dir' from 'a0'; t0..t1 along it
        /// spans the whole cell box, so no ray starts inside the block's geometry,
        /// half sizes h1 / h2 along 'side1' / 'side2', world): is it clear by its real shape? Rays along 'dir' on a grid
        /// across the cross-section, against the physics; clear when none hits this grid inside the block's cells. Out
        /// of ray budget: not clear (the cell box counts).
        /// </summary>
        private bool RealShapeClear(IMyCubeGrid grid, ref BoundingBoxD box, ref MatrixD inv, Vector3D a0, Vector3D dir, double t0, double t1,
                                    Vector3D side1, double h1, Vector3D side2, double h2)
        {
            if (t1 <= t0) return true;
            if (offThread) return false;   // rays: cell boxes count (searches with a real-shape grid aren't queued)
            int n1 = h1 > 0.75 ? 5 : h1 > 0.3 ? 4 : 3, n2 = h2 > 0.75 ? 5 : h2 > 0.3 ? 4 : 3;
            if (RealShapeRayBudget < n1 * n2) return false;
            BoundingBoxD cells = box.GetInflated(0.02);
            for (int i = 0; i < n1; i++)
            {
                double u = -1 + 2.0 * i / (n1 - 1);
                for (int j = 0; j < n2; j++)
                {
                    double v = -1 + 2.0 * j / (n2 - 1);
                    Vector3D o = a0 + side1 * (u * h1) + side2 * (v * h2);
                    hits.Clear();
                    RealShapeRayBudget--;
                    RealShapeRays++;
                    MyAPIGateway.Physics.CastRay(o + dir * t0, o + dir * t1, hits);
                    for (int k = 0; k < hits.Count; k++)
                    {
                        var e = hits[k].HitEntity;
                        if (e == null || e.GetTopMostParent() != grid) continue;
                        Vector3D p = Vector3D.Transform(hits[k].Position, inv);
                        if (cells.Contains(p) != ContainmentType.Disjoint) { hits.Clear(); return false; }
                    }
                }
            }
            hits.Clear();
            return true;
        }

        private static bool Ignored(ref PathQuery q, IMyCubeGrid grid, IMySlimBlock b)
        {
            if (q.IgnoreGrid == null || grid != q.IgnoreGrid) return false;
            Vector3I min = b.Min, max = b.Max;
            return min.X >= q.IgnoreMin.X && min.Y >= q.IgnoreMin.Y && min.Z >= q.IgnoreMin.Z
                && max.X <= q.IgnoreMax.X && max.Y <= q.IgnoreMax.Y && max.Z <= q.IgnoreMax.Z;
        }

        private static MyOrientedBoundingBoxD ToLocal(ref MyOrientedBoundingBoxD box, ref MatrixD inv)
        {
            MatrixD rot = MatrixD.CreateFromQuaternion(box.Orientation) * inv;
            rot.Translation = Vector3D.Zero;
            return new MyOrientedBoundingBoxD(Vector3D.Transform(box.Center, inv), box.HalfExtent, Quaternion.CreateFromRotationMatrix(rot));
        }

        private readonly List<IMySlimBlock> doorBlocks = new List<IMySlimBlock>();
        private readonly List<IMyCubeGrid> doorGrids = new List<IMyCubeGrid>();
        private const double DOOR_RAY_SPACING = 0.75;   // m between the rays across the hull (its edges always get one)
        private const int DOOR_RAYS_MAX = 12;           // per side

        /// <summary>A door, hangar door or gate that is fully open: passable where its model is open (rays decide).</summary>
        public static bool IsOpenDoor(IMySlimBlock slim)
        {
            var door = slim != null ? slim.FatBlock as IMyDoor : null;
            return door != null && door.Status == Sandbox.ModAPI.Ingame.DoorStatus.Open;
        }

        /// <summary>
        /// The hull's cross-section (plus the berth: half sizes 'h1' along 'side1', 'h2' along 'side2') swept from
        /// t0 to t1 along 'dir' from 'a0', as a grid of physics rays: hits on the open doors met by the box sweep count
        /// (their frame), the doorway itself doesn't. 't': where the first hit is along 'dir'.
        /// </summary>
        private bool DoorRaysBlocked(Vector3D a0, Vector3D dir, double t0, double t1, Vector3D side1, double h1, Vector3D side2, double h2,
                                     out double t, out IMyCubeGrid grid, out IMySlimBlock block)
        {
            t = double.MaxValue;
            grid = null;
            block = null;
            if (offThread) return false;   // rays: the main thread checks the doors of the finished route
            int n1 = Math.Max(2, Math.Min(DOOR_RAYS_MAX, (int)Math.Ceiling(2 * h1 / DOOR_RAY_SPACING) + 1));
            int n2 = Math.Max(2, Math.Min(DOOR_RAYS_MAX, (int)Math.Ceiling(2 * h2 / DOOR_RAY_SPACING) + 1));
            for (int i = 0; i < n1; i++)
                for (int j = 0; j < n2; j++)
                {
                    Vector3D off = side1 * (h1 * (2.0 * i / (n1 - 1) - 1)) + side2 * (h2 * (2.0 * j / (n2 - 1) - 1));
                    Vector3D from = a0 + off + dir * t0, to = a0 + off + dir * t1;
                    hits.Clear();
                    planRays++;
                    MyAPIGateway.Physics.CastRay(from, to, hits);
                    for (int k = 0; k < hits.Count; k++)
                    {
                        var g = hits[k].HitEntity != null ? hits[k].HitEntity.GetTopMostParent() as IMyCubeGrid : null;
                        if (g == null) continue;
                        int gi = doorGrids.IndexOf(g);
                        if (gi < 0) continue;
                        // The block hit: just inside the surface
                        var slim = g.GetCubeBlock(g.WorldToGridInteger(hits[k].Position + dir * 0.05));
                        if (slim == null || !doorBlocks.Contains(slim)) continue;
                        double along = Vector3D.Dot(hits[k].Position - a0, dir);
                        if (along < t) { t = along; grid = g; block = slim; }
                    }
                }
            hits.Clear();
            return t < double.MaxValue;
        }

        private double Berth(ref PathQuery q)
        {
            return q.Berth > 0 ? q.Berth : config.ObstacleClearance;
        }

        private static Vector3D HalfBox(ref PathQuery q)
        {
            return q.LocalHull.Max.X >= q.LocalHull.Min.X ? q.LocalHull.HalfExtents : new Vector3D(1);
        }

        // Is world point 'p' within what the drone sees from where it is?
        private bool Seen(ref PathQuery q, Vector3D p)
        {
            if (q.SeeAll) return true;
            var pf = config;
            double sensorRange = pf.SimulatedSensorRange, camRange = pf.MaxSimulatedCameraRaycastMeters();
            if (pf.RequireSensorsForPathfinding())
            {
                if (q.Snap)
                {
                    for (int i = 0; i < q.SensorPos.Length; i++)
                        if (q.SensorOn[i] && Vector3D.DistanceSquared(p, q.SensorPos[i]) <= sensorRange * sensorRange) return true;
                }
                else if (q.Sensors != null)
                    for (int i = 0; i < q.Sensors.Count; i++)
                        if (InstrumentOn(q.Sensors[i], q.SleepingIds) && Vector3D.DistanceSquared(p, q.Sensors[i].GetPosition()) <= sensorRange * sensorRange) return true;
            }
            else
            {
                if (q.LocalHull.Max.X < q.LocalHull.Min.X) { if (Vector3D.Distance(p, q.Controller.Translation) <= sensorRange + 2.5) return true; }
                else
                {
                    MatrixD inv = MatrixD.Invert(EyesOf(ref q));
                    var lp = Vector3D.Transform(p, inv);
                    var grown = new BoundingBoxD(q.LocalHull.Min - new Vector3D(sensorRange), q.LocalHull.Max + new Vector3D(sensorRange));
                    if (grown.Contains(lp) != ContainmentType.Disjoint) return true;
                }
            }
            if (pf.RequireCamerasForPathfinding())
            {
                if (q.Snap)
                {
                    for (int i = 0; i < q.CameraWorld.Length; i++)
                    {
                        if (!q.CameraOn[i]) continue;
                        MatrixD m = q.CameraWorld[i];
                        if (InCone(p, m.Translation, m.Forward, m.Right, m.Up, camRange)) return true;
                    }
                }
                else if (q.Cameras != null)
                    for (int i = 0; i < q.Cameras.Count; i++)
                    {
                        if (!InstrumentOn(q.Cameras[i], q.SleepingIds)) continue;
                        MatrixD m = q.Cameras[i].WorldMatrix;
                        if (InCone(p, m.Translation, m.Forward, m.Right, m.Up, camRange)) return true;
                    }
                return false;
            }
            MatrixD c = EyesOf(ref q);
            Vector3D apex = c.Translation;
            if (q.LocalHull.Max.X >= q.LocalHull.Min.X) apex = Vector3D.Transform(q.LocalHull.Center + new Vector3D(0, 0, -q.LocalHull.HalfExtents.Z), c);
            return InCone(p, apex, c.Forward, c.Right, c.Up, camRange);
        }

        // Where the drone sees from: its real attitude (Controller may have been turned to a held attitude)
        private static MatrixD EyesOf(ref PathQuery q)
        {
            return q.EyesSet ? q.Eyes : q.Controller;
        }

        private static bool InCone(Vector3D p, Vector3D apex, Vector3D f, Vector3D r, Vector3D u, double range)
        {
            Vector3D w = p - apex;
            double fw = Vector3D.Dot(w, f);
            return fw > 0 && fw >= Math.Abs(Vector3D.Dot(w, r)) && fw >= Math.Abs(Vector3D.Dot(w, u)) && w.LengthSquared() <= range * range;
        }

        // Two axes across 'dir' and the hull's half size along each (from the hull box, controller frame)
        private static void CrossSection(ref PathQuery q, ref MatrixD attitude, Vector3D dir, out Vector3D side1, out double s1,
                                         out Vector3D side2, out double s2)
        {
            // Across the leg along the hull's own axes (its up, else its right): an arbitrary perpendicular turns the
            // box's cross-section diagonally and reaches ~40% further below / beside the hull than it is
            side1 = attitude.Up - dir * Vector3D.Dot(attitude.Up, dir);
            if (side1.LengthSquared() < 0.01) side1 = attitude.Right - dir * Vector3D.Dot(attitude.Right, dir);
            side1.Normalize();
            side2 = Vector3D.Cross(dir, side1);
            Vector3D half = HalfBox(ref q);
            s1 = HalfSizeAlong(ref attitude, ref half, side1);
            s2 = HalfSizeAlong(ref attitude, ref half, side2);
        }

        private static double HalfSizeAlong(ref MatrixD m, ref Vector3D half, Vector3D w)
        {
            return Math.Abs(Vector3D.Dot(w, m.Right)) * half.X + Math.Abs(Vector3D.Dot(w, m.Up)) * half.Y
                 + Math.Abs(Vector3D.Dot(w, m.Backward)) * half.Z;
        }

        /// <summary>Half the hull's size along world direction 'w' (unit) at 'attitude'.</summary>
        public static double HullHalfSizeAlong(ref PathQuery q, ref MatrixD attitude, Vector3D w)
        {
            Vector3D half = HalfBox(ref q);
            return HalfSizeAlong(ref attitude, ref half, w);
        }

        public static double HullRadiusOf(ref PathQuery q) { return HullRadius(ref q); }

        private static double HullRadius(ref PathQuery q)
        {
            return q.LocalHull.Max.X >= q.LocalHull.Min.X ? q.LocalHull.HalfExtents.Length() : 2.5;
        }

        /// <summary>
        /// Radius the hull sweeps turning about 'pivotLocal' (controller frame: the controller is 0, a tool its
        /// mount point): the farthest hull box corner from it.
        /// </summary>
        public static double TurnRadius(ref PathQuery q, Vector3D pivotLocal)
        {
            if (q.LocalHull.Max.X < q.LocalHull.Min.X) return 5;
            double r = 0;
            for (int k = 0; k < 8; k++)
            {
                Vector3D c = new Vector3D((k & 1) != 0 ? q.LocalHull.Max.X : q.LocalHull.Min.X,
                                          (k & 2) != 0 ? q.LocalHull.Max.Y : q.LocalHull.Min.Y,
                                          (k & 4) != 0 ? q.LocalHull.Max.Z : q.LocalHull.Min.Z);
                r = Math.Max(r, Vector3D.Distance(c, pivotLocal));
            }
            return r;
        }

        /// <summary>
        /// Room to turn at 'centre': no block (that the drone sees) within 'radius' plus the berth. Asteroids near:
        /// 14 rays (axes and diagonals of the world frame) as far as the drone sees.
        /// </summary>
        /// <summary>
        /// Room to turn in place at 'centre' (the hull centre) into 'attitude' (rotation), plus 'margin' around the
        /// hull. Space: the sphere the hull sweeps (<see cref="TurnRoomFree"/>). Gravity: the drone arrives level, so
        /// a yaw at level (the box around the vertical cylinder the hull sweeps) and the pitch / roll from level to
        /// the attitude (the hull box at 4 steps of it) - a floor below or a ceiling above a side face doesn't rule
        /// out a turn that never reaches it. Asteroids near: the sphere with rays.
        /// </summary>
        public bool TurnSweepFree(ref PathQuery q, Vector3D centre, ref MatrixD attitude, double margin)
        {
            double radius = TurnRadius(ref q, q.HullCentred ? q.LocalHull.Center : Vector3D.Zero);
            Vector3D g = q.GravityUp;
            if (g.LengthSquared() < 0.5 || q.LocalHull.Max.X < q.LocalHull.Min.X || VoxelNear(centre, radius + margin))
                return TurnRoomFree(ref q, centre, radius - Berth(ref q) + margin);
            // Level, with the attitude's heading (nose straight down / up: the heading its up / down shows)
            Vector3D f = attitude.Forward - g * Vector3D.Dot(attitude.Forward, g);
            if (f.LengthSquared() < 0.01)
            {
                Vector3D u = Vector3D.Dot(attitude.Forward, g) < 0 ? attitude.Up : attitude.Down;
                f = u - g * Vector3D.Dot(u, g);
            }
            if (f.LengthSquared() < 1e-6) f = Vector3D.CalculatePerpendicularVector(g);
            MatrixD level = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(f), g);
            // Yaw at level: the hull's horizontal reach and height about its centre
            double rh, hv;
            LevelReach(ref q, ref level, out rh, out hv);
            Quaternion q0 = Quaternion.CreateFromRotationMatrix(level);
            var yaw = new MyOrientedBoundingBoxD(centre, new Vector3D(rh + margin, hv + margin, rh + margin), q0);
            if (!BoxFree(ref q, ref yaw)) return false;
            // Pitch / roll from level to the attitude
            Quaternion q1 = Quaternion.CreateFromRotationMatrix(attitude);
            Vector3D half = HalfBox(ref q) + new Vector3D(margin);
            for (int s = 1; s <= 4; s++)
            {
                var box = new MyOrientedBoundingBoxD(centre, half, Quaternion.Slerp(q0, q1, s * 0.25f));
                if (!BoxFree(ref q, ref box)) return false;
            }
            return true;
        }

        /// <summary>No block (that the drone sees, not ignored) intersects 'box'? Grids only. LastHitBlock: the first one.</summary>
        public bool BoxFree(ref PathQuery q, ref MyOrientedBoundingBoxD box)
        {
            planChecks++;
            LastHitBlock = null;
            BoundingBoxD wb = box.GetAABB();
            scene.Ensure(new BoundingSphereD(wb.Center, wb.HalfExtents.Length()), frame);
            scene.Overlapping(ref wb, entryBuffer, frame);
            bool free = true;
            for (int i = 0; i < entryBuffer.Count && free; i++)
            {
                var entry = entryBuffer[i];
                if (entry.Voxel || IsOwn(ref q, entry.Grid)) continue;
                var blocks = scene.BlocksOf(entry);
                if (blocks == null) continue;
                var grid = entry.Grid;
                MatrixD inv = entry.Inv;
                MyOrientedBoundingBoxD local = ToLocal(ref box, ref inv);
                BoundingBoxD lb = local.GetAABB();
                blocks.OverlapAllBoundingBox(ref lb, slimBuffer, 0u, true);
                for (int k = 0; k < slimBuffer.Count; k++)
                {
                    var slim = slimBuffer[k];
                    if (Ignored(ref q, grid, slim)) continue;
                    BoundingBoxD bb = LocalScene.BlockBox(slim, entry.GridSize);
                    blocksTested++;
                    if (!local.Intersects(ref bb)) continue;
                    if (!Seen(ref q, Vector3D.Transform(bb.Center, entry.World))) continue;
                    free = false;
                    LastHitBlock = slim;
                    if (!entry.Static || slim.FatBlock is IMyDoor) planHitDynamic = true;
                    break;
                }
                slimBuffer.Clear();
            }
            entryBuffer.Clear();
            return free;
        }

        public bool TurnRoomFree(ref PathQuery q, Vector3D centre, double radius)
        {
            radius += Berth(ref q);
            if (!HullSpaceFree(ref q, centre, radius)) return false;
            if (!VoxelNear(centre, radius)) return true;
            if (offThread) return true;   // the 14 rays are the main thread's: a held attitude's turn is checked there again
            for (int k = 0; k < 14; k++)
            {
                Vector3D d = k < 6 ? (Vector3D)Base6Directions.GetIntVector((Base6Directions.Direction)k)
                           : new Vector3D((k & 1) != 0 ? 1 : -1, (k & 2) != 0 ? 1 : -1, (k & 4) != 0 ? 1 : -1) * 0.57735026919;
                double sf, st;
                double seen = SeenSpan(ref q, centre, d, radius, out sf, out st) && sf <= SeenSlack(ref q) ? Math.Min(radius, st) : 0;
                if (seen <= 0.5) continue;
                hits.Clear();
                planRays++;
                MyAPIGateway.Physics.CastRay(centre, centre + d * seen, hits);
                for (int i = 0; i < hits.Count; i++)
                {
                    var e = hits[i].HitEntity;
                    if (e != null && IsAsteroid(e.GetTopMostParent())) { hits.Clear(); return false; }
                }
            }
            hits.Clear();
            return true;
        }

        private bool VoxelNear(Vector3D centre, double radius)
        {
            var sphere = new BoundingSphereD(centre, radius);
            BoundingBoxD wb = BoundingBoxD.CreateFromSphere(sphere);
            scene.Ensure(sphere, frame);
            scene.Overlapping(ref wb, entryBuffer, frame);
            bool any = false;
            for (int i = 0; i < entryBuffer.Count && !any; i++) any = entryBuffer[i].Voxel;
            entryBuffer.Clear();
            return any;
        }

        /// <summary>
        /// No block of another grid (built: projections have no physics) within 'radius' of 'centre', among what
        /// the drone sees? From the scene's block boxes. Voxels are left to the rays / terrain checks.
        /// </summary>
        public bool HullSpaceFree(ref PathQuery q, Vector3D centre, double radius)
        {
            planChecks++;
            LastHitBlock = null;
            var sphere = new BoundingSphereD(centre, radius);
            BoundingBoxD wb = BoundingBoxD.CreateFromSphere(sphere);
            scene.Ensure(sphere, frame);
            scene.Overlapping(ref wb, entryBuffer, frame);
            bool free = true;
            for (int i = 0; i < entryBuffer.Count && free; i++)
            {
                var entry = entryBuffer[i];
                if (entry.Voxel || IsOwn(ref q, entry.Grid)) continue;
                var blocks = scene.BlocksOf(entry);
                if (blocks == null) continue;
                var grid = entry.Grid;
                MatrixD inv = entry.Inv;
                var local = new BoundingSphereD(Vector3D.Transform(centre, inv), radius);
                blocks.OverlapAllBoundingSphere(ref local, slimBuffer, true);
                for (int k = 0; k < slimBuffer.Count; k++)
                {
                    var slim = slimBuffer[k];
                    if (Ignored(ref q, grid, slim)) continue;
                    BoundingBoxD box = LocalScene.BlockBox(slim, entry.GridSize);
                    blocksTested++;
                    if (!box.Intersects(ref local)) continue;
                    if (!Seen(ref q, Vector3D.Transform(box.Center, entry.World))) continue;
                    free = false;
                    LastHitBlock = slim;
                    if (!entry.Static || slim.FatBlock is IMyDoor) planHitDynamic = true;
                    break;
                }
                slimBuffer.Clear();
            }
            entryBuffer.Clear();
            return free;
        }

        /// <summary>
        /// Look-ahead on the way, the one place rays are used (rationed: a few per tick for all drones). True =
        /// something new is in the way between 'from' and 'to'. False also when the budget is spent this tick.
        /// </summary>
        public bool CheckAhead(ref PathQuery q, Vector3D from, Vector3D to, out Vector3D hit)
        {
            hit = Vector3D.Zero;
            if (raysLeft < 5) return false;
            raysLeft -= 5;
            IMyEntity e;
            if (!RaySegmentBlocked(ref q, ref q.Controller, from, to, 0.5, out hit, out e, false, false, false)) return false;
            if (!(e is IMyCubeGrid)) return true;
            // A grid: confirmed by the scene check the routes were planned with (same rules where the drone already
            // is), so a route the planner accepted isn't refused here over and over
            return SegmentBlocked(ref q, ref q.Controller, from, to, 0.5, out hit, out e, false);
        }
        #endregion

        /// <summary>
        /// Terrain (the closest planet's surface) closer than 'need' below the segment a -> b (samples every 5 m)?
        /// 'hit': the first sample too low. In flight: planets are no ray obstacles, so the look-ahead checks this.
        /// </summary>
        public static bool TerrainTooClose(Vector3D a, Vector3D b, double need, out Vector3D hit)
        {
            hit = Vector3D.Zero;
            var planet = MyGamePruningStructure.GetClosestPlanet(a);
            if (planet == null) return false;
            Vector3D c = planet.PositionComp.GetPosition();
            double len = Vector3D.Distance(a, b);
            int n = Math.Min(200, Math.Max(1, (int)Math.Ceiling(len / 5)));
            for (int i = 0; i <= n; i++)
            {
                Vector3D p = a + (b - a) * ((double)i / n);
                Vector3D s = planet.GetClosestSurfacePointGlobal(ref p);
                if (Vector3D.Distance(p, c) - Vector3D.Distance(s, c) < need) { hit = p; return true; }
            }
            return false;
        }

        /// <summary>
        /// A long leg checked once as the drone starts it: the hull-sized ray bundle along the whole leg (as far as the
        /// drone sees), not rationed like the look-ahead. Grids that aren't moving are confirmed by the scene check the
        /// route was planned with; moving ones (another drone) count as they are.
        /// </summary>
        public bool LegRaysBlocked(ref PathQuery q, Vector3D from, Vector3D to, out Vector3D hit, out IMyEntity hitEntity)
        {
            if (!RaySegmentBlocked(ref q, ref q.Controller, from, to, 0.5, out hit, out hitEntity, false, false, false)) return false;
            var grid = hitEntity as IMyCubeGrid;
            if (grid == null || (grid.Physics != null && grid.Physics.LinearVelocity.LengthSquared() > 0.25)) return true;
            return SegmentBlocked(ref q, ref q.Controller, from, to, 0.5, out hit, out hitEntity, false);
        }

        #region Query signature, failed routes, stage time limits
        /// <summary>What makes two asks the same search (besides owner, start and goal): hull, clearances, flags, ignored cells.</summary>
        internal static int QuerySignature(ref PathQuery q)
        {
            unchecked
            {
                int h = 17;
                bool hull = q.LocalHull.Max.X >= q.LocalHull.Min.X;
                Vector3D e = hull ? q.LocalHull.HalfExtents : Vector3D.Zero;
                h = h * 31 + (int)(e.X * 4);
                h = h * 31 + (int)(e.Y * 4);
                h = h * 31 + (int)(e.Z * 4);
                h = h * 31 + (int)(q.Berth * 10);
                h = h * 31 + (int)(q.Clearance * 10);
                h = h * 31 + (int)(q.MinAltitude > 0 ? 1 : 0);
                h = h * 31 + (q.MaxPitch >= 0 ? (int)(q.MaxPitch * 100) : -1);
                int bits = (q.HullCentred ? 1 : 0) | (q.LooseGoal ? 2 : 0) | (q.StrictEnds ? 4 : 0) | (q.NoHeldTurn ? 8 : 0)
                         | (q.AlignToRoute ? 16 : 0) | (q.SeeAll ? 32 : 0) | (q.GravityUp.LengthSquared() > 0.5 ? 64 : 0)
                         | (q.RealShapeGrid != null ? 128 : 0) | (q.HoldAttitude ? 256 : 0) | (q.EyesSet ? 512 : 0);
                h = h * 31 + bits;
                if (q.IgnoreGrid != null) h = h * 31 + q.IgnoreGrid.EntityId.GetHashCode() + q.IgnoreMin.GetHashCode() * 7 + q.IgnoreMax.GetHashCode() * 13;
                if (q.RealShapeGrid != null) h = h * 31 + q.RealShapeGrid.EntityId.GetHashCode();
                return h;
            }
        }

        // The drone's attitude, coarsely (held-attitude searches depend on it)
        private static int AttitudeKey(ref MatrixD m)
        {
            unchecked
            {
                Vector3D f = m.Forward, u = m.Up;
                int h = (int)Math.Round(f.X * 4) + 9 * (int)Math.Round(f.Y * 4) + 81 * (int)Math.Round(f.Z * 4);
                return h * 1031 + (int)Math.Round(u.X * 4) + 9 * (int)Math.Round(u.Y * 4) + 81 * (int)Math.Round(u.Z * 4);
            }
        }

        // "No route from here to there" (TODO/22, prerequisite 4): a drone asking the same again (a dock approach retried every
        // minute, the escape spots of a failed plan) gets the answer at once. Until FAILED_ROUTE_*_TICKS pass (short when a
        // moving grid or a door was in the way), the drone moves (START slack), or something that can open a way happens
        // (LocalScene.RemovalVersion: blocks removed, a grid split, an obstacle gone). Main thread; not for plans that ran out
        // of the shared check budget (an earlier stretch spent it).
        #region Timing
        private const int FAILED_ROUTE_STATIC_TICKS = 120 * 60;
        private const int FAILED_ROUTE_DYNAMIC_TICKS = 10 * 60;
        #endregion
        private const double FAILED_ROUTE_START_SLACK = 2;   // m
        private const double FAILED_ROUTE_GOAL_SLACK = 1;    // m
        private const int FAILED_ROUTES_MAX = 128;

        private struct FailedRoute
        {
            public long Owner;
            public int Sig, Attitude, Frame, Ttl, Version;
            public Vector3D Start, Goal;
            public string Why;
            public IMyEntity Blocker;
            public IMySlimBlock HitBlock;
        }
        private readonly List<FailedRoute> failedRoutes = new List<FailedRoute>();
        /// <summary>For the logs: plans answered from the failed-route cache.</summary>
        public int FailedRouteHits;

        private bool planHitDynamic;   // the current plan met a moving grid or a door in the way
        private bool planNoCache;      // ...or failed for want of the plan's check budget: not remembered

        private bool FindFailedRoute(ref PathQuery q, Vector3D start, Vector3D goal, out FailedRoute found)
        {
            found = default(FailedRoute);
            if (failedRoutes.Count == 0) return false;
            int sig = QuerySignature(ref q), att = AttitudeKey(ref q.Controller), version = liveScene.RemovalVersion;
            for (int i = failedRoutes.Count - 1; i >= 0; i--)
            {
                var f = failedRoutes[i];
                if (frame - f.Frame > f.Ttl || f.Version != version) { failedRoutes.RemoveAt(i); continue; }
                if (f.Owner != q.Owner || f.Sig != sig || f.Attitude != att) continue;
                if (Vector3D.DistanceSquared(f.Start, start) > FAILED_ROUTE_START_SLACK * FAILED_ROUTE_START_SLACK
                    || Vector3D.DistanceSquared(f.Goal, goal) > FAILED_ROUTE_GOAL_SLACK * FAILED_ROUTE_GOAL_SLACK) continue;
                found = f;
                return true;
            }
            return false;
        }

        private void RememberFailedRoute(ref PathQuery q, Vector3D start, Vector3D goal, string why)
        {
            if (offThread || q.Owner == 0 || planNoCache) return;
            if (failedRoutes.Count >= FAILED_ROUTES_MAX) failedRoutes.RemoveAt(0);
            failedRoutes.Add(new FailedRoute
            {
                Owner = q.Owner, Sig = QuerySignature(ref q), Attitude = AttitudeKey(ref q.Controller), Frame = frame,
                // (a stage out of time / expansions may get through another time: short too)
                Ttl = planHitDynamic || why.Contains("budget") ? FAILED_ROUTE_DYNAMIC_TICKS : FAILED_ROUTE_STATIC_TICKS, Version = liveScene.RemovalVersion,
                Start = start, Goal = goal, Why = why, Blocker = LastBlocker, HitBlock = LastHitBlock,
            });
        }

        // Stage 1 / 2 time limits (StraightMaxMs, MemoryMaxMs): the later stages (the search) take the hard cases
        private readonly System.Diagnostics.Stopwatch stageClock = new System.Diagnostics.Stopwatch();
        private double stageLimitMs;   // 0: none
        private bool StageTimeUp { get { return stageLimitMs > 0 && stageClock.Elapsed.TotalMilliseconds > stageLimitMs; } }

        // AcceptSearch: the worker's answer being checked (its doors, the grids it read unchanged), the stretch of it
        // SegmentBlocked is checking (-1: none / not one of the worker's)
        private PathSearchResult acceptResult;
        private readonly HashSet<LocalScene.Entry> acceptTrust = new HashSet<LocalScene.Entry>();
        private int trustSeg = -1;
        // A worker: the stretch of its answer its final check is on, and the doors met (to the main thread with the answer)
        private int searchSeg = -1;
        private readonly List<SearchDoor> searchDoors = new List<SearchDoor>();
        #endregion

        #region Planning
        /// <summary>
        /// Waypoints (controller positions, world) from 'start' to 'goal', both excluded: detours around what the
        /// drone sees in the way, climbs over terrain. Bounded by MaxRepositionAttempts detours and MaxPathNodes
        /// waypoints; Blocked when that is not enough.
        /// </summary>
        /// <summary>Starts a plan's budget (MAX_PLAN_CHECKS) and statistics, shared by the PlanRoute calls of one route.</summary>
        public void BeginPlan()
        {
            planRays = planChecks = blocksTested = planUnseen = budgetBase = 0;
            lastPlanFrame = MyAPIGateway.Session.GameplayFrameCounter;
            lookups0 = liveScene.Lookups;
            trees0 = liveScene.TreesBuilt;
            indexed0 = liveScene.BlocksIndexed;
            build0 = liveScene.BuildMs;
            frame = MyAPIGateway.Session.GameplayFrameCounter;
            watch.Reset();
        }

        private bool BudgetSpent { get { return planChecks + planRays - budgetBase > MAX_PLAN_CHECKS; } }
        private int budgetBase;   // a later stage of the chain gets its own budget

        /// <summary>The plan since BeginPlan: stretches not fully seen, scene checks, rays, block boxes tested, ms.</summary>
        public void PlanStats(out int notSeen, out int checks, out int rays, out int blocks, out double ms)
        {
            int lookups, trees, indexed;
            double buildMs;
            PlanStats(out notSeen, out checks, out rays, out blocks, out ms, out lookups, out trees, out indexed, out buildMs);
        }

        /// <summary>Same, plus the scene work it caused: area lookups, block trees built, blocks indexed, ms building (in 'ms').</summary>
        public void PlanStats(out int notSeen, out int checks, out int rays, out int blocks, out double ms,
                              out int lookups, out int trees, out int indexed, out double buildMs)
        {
            lookups = liveScene.Lookups - lookups0;
            trees = liveScene.TreesBuilt - trees0;
            indexed = liveScene.BlocksIndexed - indexed0;
            buildMs = liveScene.BuildMs - build0;
            notSeen = planUnseen;
            checks = planChecks;
            rays = planRays;
            blocks = blocksTested;
            ms = watch.Elapsed.TotalMilliseconds;
        }

        public RouteResult PlanRoute(ref PathQuery q, Vector3D start, Vector3D goal, List<Vector3D> waypoints)
        {
            double perf = PerfStats.Start();
            watch.Start();
            var r = PlanRouteTimed(ref q, start, goal, waypoints);
            watch.Stop();
            planUnseen += unseen;
            PerfStats.Stop(PerfStats.PathPlan, perf);
            return r;
        }

        /// <summary>
        /// The fall-forward chain (TODO/20, section 5), simplest first; the next stage only runs when the one before
        /// fails: 1. the straight line with greedy detours, 2. the shared memory of flown links, 3. Lazy Theta* (when
        /// allowed). The drone's own repositioning is the last resort (PlanPath). Stage times go to the plan's log line.
        /// </summary>
        private RouteResult PlanRouteTimed(ref PathQuery q, Vector3D start, Vector3D goal, List<Vector3D> waypoints)
        {
            unseen = 0;
            LastBlocker = null;
            planHitDynamic = planNoCache = false;
            // Min altitude is for travel: a hop shorter than the altitude itself (work between blocks, a short move at a
            // site, mining close to the ground) stays low; longer flights climb to it after take-off and land from it
            if (q.MinAltitude > 0 && Vector3D.Distance(start, goal) <= Math.Max(2 * TERRAIN_END_MARGIN, q.MinAltitude)) q.MinAltitude = 0;
            // Long flights in gravity cruise above the terrain (take-off straight up, landing straight down), instead
            // of skimming it a hull's clearance high
            if (config.UsePlanetAwarePathfinding() && q.GravityUp.LengthSquared() > 0.5 && Vector3D.Distance(start, goal) > config.ArcMinDistance)
                q.MinAltitude = Math.Max(q.MinAltitude, config.CruiseAltitude);

            // Asked before and no way then (nothing changed since): the same answer, nothing checked again
            FailedRoute failed;
            if (!offThread && q.Owner != 0 && FindFailedRoute(ref q, start, goal, out failed))
            {
                FailedRouteHits++;
                LastBlocker = failed.Blocker;
                LastHitBlock = failed.HitBlock;
                points.Clear();
                if (Log.DebugEnabled) Log.Debug("Pathfinding: no route {0:F0} m (as {1:F0} s ago: {2})", Vector3D.Distance(start, goal), (frame - failed.Frame) / 60.0, failed.Why);
                return RouteResult.Blocked;
            }
            // A goal inside something (another drone parked there, a ship) can't be reached, whatever the route
            if (!q.LooseGoal && !HullFits(ref q, goal))
            {
                LastBlocker = LastHitBlock != null ? LastHitBlock.CubeGrid : null;
                string inside = "the goal is inside " + Describe(LastHitBlock);
                RememberFailedRoute(ref q, start, goal, inside);
                return Fail(start, goal, inside);
            }

            // The search queue (worker threads) when the caller can wait for it; a real-shape check needs rays all along: here
            bool searches = q.AsyncOk && Searches != null && !offThread && ThetaAvailable && q.RealShapeGrid == null;
            PathSearchResult sr = null;
            var poll = SearchPoll.Inline;
            // A search for this stretch queued before (the stages before it failed then): straight to it, they aren't run again
            if (searches) poll = Searches.Find(ref q, start, goal, out sr);
            if (poll == SearchPoll.Pending) return RouteResult.Pending;

            double t0 = watch.Elapsed.TotalMilliseconds;
            string why1 = null, why2 = null, stage = "straight";
            bool shaped = false, ok = false;
            double t1 = t0, t2 = t0;
            if (poll == SearchPoll.Inline)
            {
                ok = StraightStage(ref q, start, goal, out why1, out shaped, true);
                t1 = t2 = watch.Elapsed.TotalMilliseconds;
                if (!ok && Memory != null && !(searches && Searches.MemoryFailedBefore(ref q, start, goal)))
                {
                    int was = budgetBase;
                    budgetBase = planChecks + planRays;   // its own budget
                    memoryPoints.Clear();
                    ok = Memory.FindRoute(this, ref q, start, goal, memoryPoints, out why2, config.MemoryMaxMs);
                    budgetBase = was;
                    if (!ok && searches) Searches.NoteMemoryFailed(ref q, start, goal);
                    if (ok)
                    {
                        points.Clear();
                        points.Add(start);
                        for (int k = 0; k < memoryPoints.Count; k++) points.Add(q.HullCentred ? memoryPoints[k] : HullToController(ref q, memoryPoints[k]));
                        points.Add(goal);
                        shaped = false;
                        stage = "memory";
                    }
                    t2 = watch.Elapsed.TotalMilliseconds;
                }
            }
            else why1 = "queued search";
            string why3 = null;
            double t3 = t2;
            if (!ok && ThetaAvailable)
            {
                if (searches && poll == SearchPoll.Inline)
                {
                    poll = Searches.Ask(ref q, start, goal, out sr);
                    if (poll == SearchPoll.Pending) return RouteResult.Pending;
                }
                if (poll == SearchPoll.Ready)
                {
                    if (sr.Found)
                    {
                        double pa = PerfStats.Start();
                        ok = AcceptSearch(ref q, start, goal, sr, out why3);
                        PerfStats.Stop(PerfStats.PathAccept, pa);
                        if (!ok) Searches.Reject(sr, why3);
                    }
                    else why3 = sr.Why;
                    if (sr.HitDynamic || !ok && sr.Found) planHitDynamic = true;
                    if (ok) { shaped = false; stage = q.HoldAttitude ? "theta (queued), held attitude" : "theta (queued)"; }
                }
                else
                {
                    ok = ThetaStage(ref q, start, goal, out why3);
                    if (ok) { shaped = false; stage = q.HoldAttitude ? "theta, held attitude" : "theta"; }
                }
                t3 = watch.Elapsed.TotalMilliseconds;
            }
            if (!ok)
            {
                string why = why1 + (why2 != null ? "; memory: " + why2 : "") + (why3 != null ? "; Lazy Theta*: " + why3 : "");
                RememberFailedRoute(ref q, start, goal, why);
                return Fail(start, goal, why + string.Format(" [straight {0:F2} ms{1}{2}]", t1 - t0, Memory != null ? string.Format(", memory {0:F2} ms", t2 - t1) : "",
                                                             ThetaAvailable ? string.Format(", Lazy Theta* {0:F2} ms", t3 - t2) : ""));
            }

            if (stage != "straight") budgetBase = planChecks + planRays;   // straightening a searched route gets its own budget
            if (!shaped && points.Count > 3) Shortcut(ref q);
            if (points.Count > 2) RoundCorners(ref q);
            for (int k = 1; k < points.Count - 1; k++) waypoints.Add(points[k]);
            int added = points.Count - 2;
            points.Clear();
            if (Log.DebugEnabled) Log.Debug("Pathfinding: {0:F0} m, {1} waypoints, {2} stretches not fully seen (assumed clear){7}, {3} checks, {4} blocks, {5} rays, {6:F2} ms; stage {8} (straight {9:F2} ms{10})", Vector3D.Distance(start, goal), added, unseen, planChecks, blocksTested, planRays, watch.Elapsed.TotalMilliseconds, unseen > 0 ? DescribeView(ref q, start, goal) : "", stage, t1 - t0, (stage != "straight" ? string.Format(", memory {0:F2} ms", t2 - t1) : "") + (stage.StartsWith("theta") ? string.Format(", Lazy Theta* {0:F2} ms", t3 - t2) : ""));
            return added > 0 ? RouteResult.Detour : RouteResult.Direct;
        }

        private readonly List<Vector3D> memoryPoints = new List<Vector3D>();

        // Memory points are hull centres; routes not hull-centred are controller positions
        private static Vector3D HullToController(ref PathQuery q, Vector3D hullCentre)
        {
            if (q.LocalHull.Max.X < q.LocalHull.Min.X) return hullCentre;
            return hullCentre - Vector3D.TransformNormal(q.LocalHull.Center, q.Controller);
        }

        /// <summary>
        /// Stage 1: the straight line, terrain climbs and greedy detours (one obstacle at a time). Fills 'points' (start,
        /// waypoints, goal); false: none found ('why'), 'points' empty. 'limited': within StraightMaxMs when the search is
        /// there to take over (not the rest of a far search's route: that is the long, plain part of the flight).
        /// </summary>
        private bool StraightStage(ref PathQuery q, Vector3D start, Vector3D goal, out string why, out bool shaped, bool limited = false)
        {
            stageLimitMs = limited && ThetaAvailable && !offThread ? config.StraightMaxMs : 0;
            stageClock.Restart();
            try { return StraightStageTimed(ref q, start, goal, out why, out shaped); }
            finally { stageLimitMs = 0; }
        }

        private bool StraightStageTimed(ref PathQuery q, Vector3D start, Vector3D goal, out string why, out bool shaped)
        {
            why = null;
            points.Clear();
            points.Add(start);
            ShapeForPlanet(ref q, start, goal, points);
            shaped = points.Count > 1;   // planet arcs / altitude points: kept as they are
            points.Add(goal);
            int detours = 0, climbs = 0;
            int maxDetours = config.MaxRepositionAttempts();
            if (ThetaAvailable) maxDetours = Math.Min(maxDetours, STAGE1_DETOURS);   // the search takes over the hard cases
            int maxPoints = Math.Max(2, config.MaxPathNodes()) + 2;
            int i = 0;
            while (i < points.Count - 1)
            {
                if (BudgetSpent || points.Count > maxPoints) { why = "check / node budget"; planNoCache |= BudgetSpent; points.Clear(); return false; }
                if (StageTimeUp) { why = "time budget"; points.Clear(); return false; }
                Vector3D a = points[i], b = points[i + 1];

                Vector3D over1, over2;
                if (TerrainInTheWay(ref q, a, b, out over1, out over2))
                {
                    // Climbs over terrain aren't detours (a long flight over hills needs many): the node budget bounds them
                    if (++climbs > maxPoints) { why = "terrain"; points.Clear(); return false; }
                    int at = i + 1;
                    if (Vector3D.DistanceSquared(over1, a) > 1) points.Insert(at++, over1);
                    if (Vector3D.DistanceSquared(over2, b) > 1 && Vector3D.DistanceSquared(over2, over1) > 1) points.Insert(at, over2);
                    continue;   // the climb itself is checked next
                }

                Vector3D hit;
                IMyEntity entity;
                bool toGoal = i + 2 == points.Count;
                if (SegmentBlocked(ref q, ref q.Controller, a, b, 0.5, out hit, out entity, !(toGoal && q.LooseGoal)))
                {
                    if (++detours > maxDetours) { why = "too many obstacles"; LastBlocker = entity; points.Clear(); return false; }
                    Vector3D p;
                    int seenBefore = unseen;   // detour candidates aren't the route: not counted as unseen stretches
                    bool found = DetourPoint(ref q, a, b, hit, entity, out p);
                    unseen = seenBefore;
                    if (!found)
                    {
                        why = StageTimeUp ? "time budget" : "no way around " + (entity != null ? entity.DisplayName : "?");
                        LastBlocker = entity;
                        points.Clear();
                        return false;
                    }
                    points.Insert(i + 1, p);
                    continue;   // a -> p checked by construction; p -> b next
                }
                i++;
            }
            return true;
        }

        /// <summary>
        /// Detours are placed one obstacle at a time, so a route round a cluttered spot zig-zags (a detour to one side,
        /// the next back across). String pulling: from each kept point, straight on to the farthest later point the
        /// hull can reach in a line; the points in between go.
        /// </summary>
        private void Shortcut(ref PathQuery q)
        {
            Vector3D h, o1, o2;
            IMyEntity e;
            int before = points.Count;
            bool reserve = false;   // half the route budget stays for the rest of the plan
            for (int k = 0; k < points.Count - 2 && !reserve; k++)
            {
                for (int j = points.Count - 1; j > k + 1; j--)
                {
                    if (planChecks + planRays - budgetBase > MAX_PLAN_CHECKS / 2) { reserve = true; break; }
                    bool toGoal = j == points.Count - 1;
                    if (TerrainInTheWay(ref q, points[k], points[j], out o1, out o2)) continue;
                    if (SegmentBlocked(ref q, ref q.Controller, points[k], points[j], 0.5, out h, out e, !(toGoal && q.LooseGoal))) continue;
                    points.RemoveRange(k + 1, j - k - 1);
                    break;
                }
            }
            // Stretches not fully in view, counted again for the route as it is now
            unseen = 0;
            for (int k = 0; k + 1 < points.Count; k++)
            {
                Vector3D d = points[k + 1] - points[k];
                double len = d.Length();
                if (len < 0.1) continue;
                double sf, st;
                if (!SeenSpan(ref q, points[k], d / len, len, out sf, out st) || sf > SeenSlack(ref q) || st < len) unseen++;
            }
            if (points.Count < before) if (Log.DebugEnabled) Log.Debug("Pathfinding: route straightened, {0} of {1} waypoints left", points.Count - 2, before - 2);
        }

        private readonly List<Vector3D> rounded = new List<Vector3D>();

        /// <summary>
        /// Corners sharper than RUN_THROUGH_DEGREES become arcs: from a point on the leg in to a point on the leg out
        /// (CORNER_CUT_SHARE of the shorter leg, CORNER_CUT_MAX at most), chords of CORNER_SEGMENT_DEGREES at most, so
        /// the drone flies through without stopping and turning in place. Each chord is checked like a route leg (scene,
        /// terrain clearance below it); blocked: half the cut once, else the corner stays. The ends stay as they are.
        /// </summary>
        private void RoundCorners(ref PathQuery q)
        {
            rounded.Clear();
            rounded.Add(points[0]);
            int arcs = 0, kept = 0;
            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3D a = points[i - 1], w = points[i], b = points[i + 1];
                Vector3D din = w - a, dout = b - w;
                double lin = din.Length(), lout = dout.Length();
                bool done = false;
                if (lin >= CORNER_LEG_MIN && lout >= CORNER_LEG_MIN && !BudgetSpent)
                {
                    din /= lin;
                    dout /= lout;
                    double turn = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(din, dout), -1, 1));
                    double deg = MathHelperD.ToDegrees(turn);
                    if (deg < RUN_THROUGH_DEGREES) { rounded.Add(w); continue; }
                    if (deg < 170)
                    {
                        double cut = Math.Min(CORNER_CUT_MAX, CORNER_CUT_SHARE * Math.Min(lin, lout));
                        for (int attempt = 0; attempt < 2 && !done; attempt++, cut *= 0.5)
                            done = TryArc(ref q, w, din, dout, turn, cut);
                        if (done) arcs++; else kept++;
                    }
                }
                if (!done) rounded.Add(w);
            }
            rounded.Add(points[points.Count - 1]);
            if (arcs > 0)
            {
                points.Clear();
                points.AddRange(rounded);
                if (Log.DebugEnabled) Log.Debug("Pathfinding: {0} corner(s) rounded, {1} kept (no room)", arcs, kept);
            }
            rounded.Clear();
        }

        // The arc for one corner, appended to 'rounded' if every chord of it is clear
        private bool TryArc(ref PathQuery q, Vector3D w, Vector3D din, Vector3D dout, double turn, double cut)
        {
            if (cut < CORNER_LEG_MIN * 0.5) return false;
            Vector3D t1 = w - din * cut, t2 = w + dout * cut;
            double radius = cut / Math.Tan(turn * 0.5);
            Vector3D toCentre = dout - din;
            if (toCentre.LengthSquared() < 1e-9) return false;
            Vector3D centre = w + Vector3D.Normalize(toCentre) * (radius / Math.Cos(turn * 0.5));
            Vector3D u1 = t1 - centre, u2 = t2 - centre;
            double l1 = u1.Normalize(), l2 = u2.Normalize();
            if (l1 < 1e-3 || l2 < 1e-3) return false;
            int n = Math.Max(2, (int)Math.Ceiling(MathHelperD.ToDegrees(turn) / CORNER_SEGMENT_DEGREES));
            double sin = Math.Sin(turn);
            if (Math.Abs(sin) < 1e-6) return false;
            int start = rounded.Count;
            Vector3D prev = t1;
            rounded.Add(t1);
            bool inGravity = q.GravityUp.LengthSquared() > 0.5;
            double need = inGravity ? HullHalfSizeAlong(ref q, ref q.Controller, q.GravityUp) + 1.0 : 0;
            // MinAltitude: the arc no lower than its lower end (or the altitude), so a cruise corner doesn't cut below it
            if (inGravity && q.MinAltitude > 0)
            {
                double h1 = HeightAboveTerrain(t1), h2 = HeightAboveTerrain(t2);
                if (h1 != double.MaxValue && h2 != double.MaxValue) need = Math.Max(need, Math.Min(q.MinAltitude, Math.Min(h1, h2)) - 1);
            }
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n;
                Vector3D u = (u1 * Math.Sin((1 - t) * turn) + u2 * Math.Sin(t * turn)) / sin;
                Vector3D p = k == n ? t2 : centre + u * radius;
                Vector3D h;
                IMyEntity e;
                Vector3D th;
                if (BudgetSpent || SegmentBlocked(ref q, ref q.Controller, prev, p, 0.5, out h, out e)
                    || (inGravity && config.UsePlanetAwarePathfinding() && TerrainTooClose(prev, p, need, out th)))
                {
                    rounded.RemoveRange(start, rounded.Count - start);
                    return false;
                }
                rounded.Add(p);
                prev = p;
            }
            if (arcPoints.Count > ARC_POINTS_MAX) arcPoints.Clear();
            for (int i = start; i < rounded.Count; i++) arcPoints.Add(rounded[i]);
            return true;
        }

        private RouteResult Fail(Vector3D start, Vector3D goal, string why)
        {
            if (Log.DebugEnabled) Log.Debug("Pathfinding: no route {0:F0} m ({1}), {2} checks, {3} rays, {4:F2} ms", Vector3D.Distance(start, goal), why, planChecks, planRays, watch.Elapsed.TotalMilliseconds);
            points.Clear();
            return RouteResult.Blocked;
        }

        /// <summary>
        /// A point to fly to instead of b, beside or over what was hit on a -> b: backed off from the hit by the
        /// hull, then out across the travel direction (up first in gravity, the sides, diagonals; never down in
        /// gravity) by growing steps until a -> point is clear and the drone would see past the obstacle from there.
        /// Last resort: over the top of the obstacle's box. The cheapest (shortest a -> point -> b) wins per step.
        /// </summary>
        private bool DetourPoint(ref PathQuery q, Vector3D a, Vector3D b, Vector3D hit, IMyEntity entity, out Vector3D point)
        {
            point = b;
            Vector3D dir = Vector3D.Normalize(b - a);
            double hull = HullRadius(ref q);
            double step = hull + q.Clearance;
            Vector3D back = hit - dir * step;
            if (Vector3D.Dot(back - a, dir) < 0) back = a;

            bool inGravity = q.GravityUp.LengthSquared() > 0.5;
            Vector3D up = inGravity ? q.GravityUp : Vector3D.CalculatePerpendicularVector(dir);
            up -= dir * Vector3D.Dot(up, dir);
            if (up.LengthSquared() < 1e-4) up = Vector3D.CalculatePerpendicularVector(dir);
            up.Normalize();
            Vector3D side = Vector3D.Cross(dir, up);
            double probe = Math.Min(Vector3D.Distance(back, b), 2 * hull + q.Clearance);

            IMyEntity e;
            Vector3D h;
            for (int k = 0; k < DETOUR_STEPS; k++)
            {
                double off = step * (1 << k);
                double bestCost = double.MaxValue;
                for (int c = 0; c < 6; c++)
                {
                    Vector3D u = c == 0 ? up : c == 1 ? side : c == 2 ? -side
                               : c == 3 ? Vector3D.Normalize(up + side) : c == 4 ? Vector3D.Normalize(up - side) : -up;
                    if (c == 5 && inGravity) continue;                 // not under it in gravity
                    if (BudgetSpent) { planNoCache = true; return false; }   // budget spent: give up (Blocked)
                    if (StageTimeUp) return false;                      // the search takes over
                    Vector3D p = back + u * off;
                    if (SegmentBlocked(ref q, a, p, 0.5, out h, out e)) continue;
                    if (!HullSpaceFree(ref q, p, hull + q.Clearance)) continue;   // the hull must fit there
                    Vector3D toB = b - p;
                    double lenB = toB.Length();
                    if (lenB > 0.5 && SegmentBlocked(ref q, p, p + toB / lenB * Math.Min(lenB, probe), 0, out h, out e)) continue;
                    double cost = Vector3D.Distance(a, p) + lenB - (inGravity ? Vector3D.Dot(u, up) * off * 0.1 : 0);
                    if (cost < bestCost) { bestCost = cost; point = p; }
                }
                if (bestCost < double.MaxValue) return true;
            }

            // Over the top of the whole obstacle (its box, the hull and the clearance above it)
            if (entity != null)
            {
                BoundingBoxD box = entity.WorldAABB;
                double top = double.MinValue;
                Vector3D ext = box.HalfExtents, ctr = box.Center;
                top = Vector3D.Dot(ctr, up) + Math.Abs(up.X) * ext.X + Math.Abs(up.Y) * ext.Y + Math.Abs(up.Z) * ext.Z;
                Vector3D p = back + up * (top + hull + q.Clearance - Vector3D.Dot(back, up));
                if (!SegmentBlocked(ref q, a, p, 0.5, out h, out e))
                {
                    point = p;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Terrain between a and b (away from the ends, where work near the ground happens)? Then two points to
        /// climb over it: above a and above b, at the highest surface seen plus MinAltitudeBuffer.
        /// </summary>
        /// <summary>Height of 'p' over the closest planet's surface, along the local vertical (MaxValue: no planet).</summary>
        public static double HeightAboveTerrain(Vector3D p)
        {
            var planet = MyGamePruningStructure.GetClosestPlanet(p);
            if (planet == null) return double.MaxValue;
            Vector3D surface = planet.GetClosestSurfacePointGlobal(ref p);
            Vector3D up = p - planet.PositionComp.GetPosition();
            if (up.LengthSquared() < 1) return double.MaxValue;
            up.Normalize();
            return Vector3D.Dot(p - surface, up);
        }

        private bool TerrainInTheWay(ref PathQuery q, Vector3D a, Vector3D b, out Vector3D over1, out Vector3D over2)
        {
            over1 = a;
            over2 = b;
            if (!config.UsePlanetAwarePathfinding() || q.GravityUp.LengthSquared() < 0.5) return false;
            double len = Vector3D.Distance(a, b);
            if (len <= 2 * TERRAIN_END_MARGIN) return false;
            Vector3D mid = (a + b) * 0.5;
            var planet = MyGamePruningStructure.GetClosestPlanet(mid);
            if (planet == null) return false;
            Vector3D up = Vector3D.Normalize(mid - planet.PositionComp.GetPosition());   // local vertical there
            // A climb or a descent (take-off, the drop onto the goal): nothing to climb over, and the ends are where the
            // caller wants them. Checked, it could never be cleared (each climb re-inserted the same point above the end
            // until the node budget ran out: "no route (terrain)" / "(check / node budget)" after 1 check)
            Vector3D d = b - a;
            double vertical = Math.Abs(Vector3D.Dot(d, up));
            double horizontal = Math.Sqrt(Math.Max(0, d.LengthSquared() - vertical * vertical));
            if (horizontal < Math.Max(TERRAIN_END_MARGIN, 0.25 * vertical)) return false;
            // Hull clearance always; with a MinAltitude, the full altitude where both ends are at it (cruise), else as high
            // as the lower end (the stretches near a job / a landing / a take-off stay low), less the chord's sag
            double hullNeed = HullRadius(ref q) + q.Clearance;
            double need = hullNeed;
            if (q.MinAltitude > 0)
            {
                double ends = Math.Min(Vector3D.Dot(a, up) - Vector3D.Dot(planet.GetClosestSurfacePointGlobal(ref a), up),
                                       Vector3D.Dot(b, up) - Vector3D.Dot(planet.GetClosestSurfacePointGlobal(ref b), up));
                double sag = len * len / (8 * Math.Max(1.0, Vector3D.Distance(mid, planet.PositionComp.GetPosition())));
                need = Math.Max(hullNeed, Math.Min(q.MinAltitude, ends) - sag - 1);
            }
            // Samples every 5..15 m (a ridge between coarse samples was flown into), 600 at most
            double step = Math.Max(Math.Max(5, Math.Min(15, len / 12)), len / 600);
            bool blocked = false;
            double highest = double.MinValue;
            for (double t = TERRAIN_END_MARGIN; t <= len - TERRAIN_END_MARGIN; t += step)
            {
                Vector3D p = a + (b - a) * (t / len);
                Vector3D surface = planet.GetClosestSurfacePointGlobal(ref p);
                double h = Vector3D.Dot(surface, up);
                if (h > highest) highest = h;
                if (Vector3D.Dot(p, up) - h < need) blocked = true;
            }
            if (!blocked) return false;
            double height = highest + Math.Max(config.MinAltitudeBuffer(), q.MinAltitude) + hullNeed;   // MinAltitude once, not twice
            if (Vector3D.Dot(a, up) < height) over1 = a + up * (height - Vector3D.Dot(a, up));
            if (Vector3D.Dot(b, up) < height) over2 = b + up * (height - Vector3D.Dot(b, up));
            return true;
        }

        /// <summary>
        /// Planets: long stretches follow the curve of the planet instead of a straight chord (points along the
        /// great circle, their height above the centre going from start's to goal's, never less than the surface
        /// plus the clearance / MinAltitude). With a MinAltitude: straight up to it after take-off and down from it
        /// before landing. Adds the points between 'start' (already in 'pts') and the goal (added by the caller).
        /// </summary>
        private void ShapeForPlanet(ref PathQuery q, Vector3D start, Vector3D goal, List<Vector3D> pts)
        {
            if (!config.UsePlanetAwarePathfinding() || q.GravityUp.LengthSquared() < 0.5) return;
            double dist = Vector3D.Distance(start, goal);
            bool arc = dist > config.ArcMinDistance;
            bool minAlt = q.MinAltitude > 0 && dist > 2 * TERRAIN_END_MARGIN;
            if (!arc && !minAlt) return;
            var planet = MyGamePruningStructure.GetClosestPlanet(start);
            if (planet == null) return;
            Vector3D c = planet.PositionComp.GetPosition();
            Vector3D u0 = start - c, u1 = goal - c;
            double r0 = u0.Normalize(), r1 = u1.Normalize();
            double floorAlt = Math.Max(q.MinAltitude, HullRadius(ref q) + q.Clearance);
            if (minAlt)
            {
                double s0 = SurfaceRadius(planet, ref c, start);
                if (r0 - s0 < q.MinAltitude - 1) { r0 = s0 + q.MinAltitude; pts.Add(c + u0 * r0); }   // take-off: straight up
            }
            double rGoal = r1;
            if (minAlt)
            {
                double s1 = SurfaceRadius(planet, ref c, goal);
                if (r1 - s1 < q.MinAltitude) r1 = s1 + q.MinAltitude;                           // landing: from above
            }
            if (arc)
            {
                // Long straight chords instead of a point every ArcStep that traces each bump of the terrain: as long as
                // the sag allowed lets them be (sag = L^2 / 8r, ArcSag), then each chord's ends lifted until the whole
                // chord clears the terrain by the floor altitude. The chords only dip between their ends, so the route is
                // never higher than its highest end (no tangent out of the atmosphere).
                double theta = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(u0, u1), -1, 1));
                double rMax = Math.Max(r0, r1);
                double chord = Math.Max(config.ArcStep, Math.Sqrt(8 * rMax * config.ArcSag));
                int n = (int)Math.Ceiling(theta * rMax / chord) - 1;
                n = Math.Min(n, Math.Max(1, config.MaxPathNodes() / 2));   // long arcs: wider steps, not a failed plan
                double sin = Math.Sin(theta);
                int first = pts.Count;   // pts[first - 1]: the start / the top of the take-off (fixed)
                for (int k = 1; k <= n && sin > 1e-6; k++)
                {
                    double t = (double)k / (n + 1);
                    Vector3D u = (u0 * Math.Sin((1 - t) * theta) + u1 * Math.Sin(t * theta)) / sin;
                    pts.Add(c + u * (r0 + (r1 - r0) * t + config.ArcSag));
                }
                // The end the chords lead to: above the goal (landing) or the goal itself (fixed)
                Vector3D end = minAlt && r1 > rGoal + 1 ? c + u1 * r1 : goal;
                LiftChords(planet, ref c, pts, first, end, floorAlt);
            }
            if (minAlt && r1 > rGoal + 1) pts.Add(c + u1 * r1);                                    // above the goal
        }

        private const int LIFT_PASSES = 4;
        private const double LIFT_SAMPLE = 100;   // m between terrain samples along a chord (TerrainInTheWay looks closer)

        // Arc points pts[first ..] (the point before and 'end' stay): each chord's ends raised (radially) by what its
        // lowest sample lacks over the terrain, a few passes (raising one chord changes its neighbours)
        private void LiftChords(MyPlanet planet, ref Vector3D c, List<Vector3D> pts, int first, Vector3D end, double floorAlt)
        {
            int last = pts.Count - 1;
            if (first < 1 || last < first) return;
            for (int pass = 0; pass < LIFT_PASSES; pass++)
            {
                bool lifted = false;
                for (int j = first - 1; j <= last; j++)
                {
                    Vector3D a = pts[j], b = j < last ? pts[j + 1] : end;
                    double len = Vector3D.Distance(a, b);
                    int m = Math.Max(2, Math.Min(60, (int)Math.Ceiling(len / LIFT_SAMPLE)));
                    double lack = 0;
                    for (int s = 1; s < m; s++)
                    {
                        Vector3D p = a + (b - a) * ((double)s / m);
                        lack = Math.Max(lack, SurfaceRadius(planet, ref c, p) + floorAlt - Vector3D.Distance(p, c));
                    }
                    if (lack <= 0.5) continue;
                    lifted = true;
                    // Both movable ends by the whole lack (a single movable end: twice, it only lifts its half)
                    bool moveA = j >= first, moveB = j < last;
                    double by = moveA && moveB ? lack : lack * 2;
                    if (moveA) pts[j] = c + Vector3D.Normalize(a - c) * (Vector3D.Distance(a, c) + by);
                    if (moveB) pts[j + 1] = c + Vector3D.Normalize(b - c) * (Vector3D.Distance(b, c) + by);
                }
                if (!lifted) break;
            }
        }

        private static double SurfaceRadius(MyPlanet planet, ref Vector3D centre, Vector3D p)
        {
            Vector3D s = planet.GetClosestSurfacePointGlobal(ref p);
            return Vector3D.Distance(s, centre);
        }

        /// <summary>
        /// The attitude each leg of 'pts' (start ... goal) is flown with: aligned with the leg when the drone may
        /// (nose along it; in gravity: level heading, pitched within MaxPitch, no roll; in space: nose along it,
        /// up kept close to the previous one), else the previous leg's. A leg is only aligned when there is room
        /// to turn where it starts (<see cref="TurnRoomFree"/>, radius: the hull about the controller) and its
        /// hull-sized ray bundle is clear at that attitude. forwards / ups: one per leg.
        /// </summary>
        public void AssignAttitudes(ref PathQuery q, List<Vector3D> pts, List<Vector3D> forwards, List<Vector3D> ups,
                                    Vector3D startForward, Vector3D startUp)
        {
            watch.Start();
            AssignAttitudesTimed(ref q, pts, forwards, ups, startForward, startUp);
            watch.Stop();
        }

        private void AssignAttitudesTimed(ref PathQuery q, List<Vector3D> pts, List<Vector3D> forwards, List<Vector3D> ups,
                                          Vector3D startForward, Vector3D startUp)
        {
            forwards.Clear();
            ups.Clear();
            Vector3D fwd = startForward, up = startUp;
            MatrixD planned = q.Controller;   // the attitude PlanRoute checked every leg with
            planned.Translation = Vector3D.Zero;
            double turnRadius = TurnRadius(ref q, q.HullCentred ? q.LocalHull.Center : Vector3D.Zero);   // turns about the leg's reference point
            bool inGravity = q.GravityUp.LengthSquared() > 0.5;
            var planet = inGravity && pts.Count > 0 ? MyGamePruningStructure.GetClosestPlanet(pts[0]) : null;
            Vector3D h;
            IMyEntity e;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector3D a = pts[i], b = pts[i + 1];
                // Local vertical at the leg (planets curve: the start's "level" isn't level far away)
                Vector3D g = planet != null ? Vector3D.Normalize(a - planet.PositionComp.GetPosition()) : q.GravityUp;
                Vector3D f, u;
                bool aligned = false;
                if (q.HoldAttitude)
                {
                    // Only that attitude found a way (Lazy Theta*, held): every leg keeps it
                    forwards.Add(planned.Forward);
                    ups.Add(planned.Up);
                    continue;
                }
                if (q.AlignToRoute && !BudgetSpent && AlignedFrame(ref q, ref g, b - a, fwd, up, inGravity, out f, out u))
                {
                    if (Vector3D.Dot(f, fwd) >= 0.996 && Vector3D.Dot(u, up) >= 0.996) { f = fwd; u = up; aligned = true; }   // no turn
                    else
                    {
                        MatrixD att = MatrixD.CreateWorld(Vector3D.Zero, f, u);
                        aligned = TurnRoomFree(ref q, a, turnRadius) && !SegmentBlocked(ref q, ref att, a, b, 0.5, out h, out e);
                    }
                    if (aligned) { fwd = f; up = u; }
                }
                if (!aligned)
                {
                    // Keep the previous attitude if this leg is clear with it; otherwise the one the route was checked with
                    MatrixD prevAtt = MatrixD.CreateWorld(Vector3D.Zero, fwd, up);
                    if (BudgetSpent || SegmentBlocked(ref q, ref prevAtt, a, b, 0.5, out h, out e))
                    {
                        fwd = planned.Forward;
                        up = planned.Up;
                    }
                }
                forwards.Add(fwd);
                ups.Add(up);
            }
        }

        private static bool AlignedFrame(ref PathQuery q, ref Vector3D gravityUp, Vector3D dir, Vector3D prevFwd, Vector3D prevUp, bool inGravity,
                                         out Vector3D fwd, out Vector3D up)
        {
            fwd = prevFwd;
            up = prevUp;
            double len = dir.Length();
            if (len < 1) return false;
            dir /= len;
            if (inGravity)
            {
                Vector3D g = gravityUp;
                Vector3D h = dir - g * Vector3D.Dot(dir, g);
                if (h.LengthSquared() < 0.01)
                {
                    // Straight up / down: keep the heading, level
                    h = prevFwd - g * Vector3D.Dot(prevFwd, g);
                    if (h.LengthSquared() < 0.01) return false;
                    h.Normalize();
                    fwd = h;
                    up = g;
                    return true;
                }
                h.Normalize();
                double pitch = Math.Atan2(Vector3D.Dot(dir, g), Vector3D.Dot(dir, h));
                if (q.MaxPitch >= 0) pitch = MathHelperD.Clamp(pitch, -q.MaxPitch, q.MaxPitch);
                fwd = h * Math.Cos(pitch) + g * Math.Sin(pitch);
                up = g * Math.Cos(pitch) - h * Math.Sin(pitch);
                return true;
            }
            fwd = dir;
            up = prevUp - dir * Vector3D.Dot(prevUp, dir);
            if (up.LengthSquared() < 1e-4) up = Vector3D.CalculatePerpendicularVector(dir);
            up.Normalize();
            return true;
        }

        /// <summary>How to pass a waypoint, from the turn between the leg in and the leg out (25° / 45°).</summary>
        public static WaypointBehavior Classify(Vector3D inDir, Vector3D outDir)
        {
            if (inDir.LengthSquared() < 1e-6 || outDir.LengthSquared() < 1e-6) return WaypointBehavior.FullStop;
            double cos = Vector3D.Dot(Vector3D.Normalize(inDir), Vector3D.Normalize(outDir));
            double angle = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(cos, -1, 1)));
            return angle < RUN_THROUGH_DEGREES ? WaypointBehavior.RunThrough : angle < 45 ? WaypointBehavior.SlowApproach : WaypointBehavior.FullStop;
        }
        #endregion
    }
}
