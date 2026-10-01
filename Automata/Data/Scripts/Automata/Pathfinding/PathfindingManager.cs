using System;
using System.Collections.Generic;

using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Config;
using Automata.Util.Logging;

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
        // ObservedRange: the all-around part (sensors / instruments not required), worked out once per query
        internal bool ViewKnown;
        internal double View;
    }

    public enum RouteResult : byte
    {
        Direct,    // nothing seen in the way: no waypoints
        Detour,    // waypoints added
        Blocked,   // no way found within the attempts: don't fly it
    }

    /// <summary>
    /// Drone routes: direct pathfinding with geometric repositioning (A* is not wired yet, see
    /// TODO/04-pathfinding-architecture.md). One instance per session (server).
    ///
    /// Conversation with a drone:
    ///  1. Dispatch: the drone asks for a route start -> goal (<see cref="PlanRoute"/>). The pathfinder checks the
    ///     straight line with ray bundles the size of the hull, as far as the drone can "see" (simulated cameras and
    ///     sensors, <see cref="ObservedRange"/>). Blocked: a detour point beside / over the obstacle, ray-checked,
    ///     then the rest again from there. Terrain: a climb over it (planets are not obstacles to the rays).
    ///     Beyond what the drone sees the line is assumed clear. Answer: the waypoints (or Blocked).
    ///  2. Flight: the drone flies them as legs; the pathfinder sets how each waypoint is passed from the turn
    ///     angle (<see cref="Classify"/>).
    ///  3. On the way, every few ticks, the drone looks ahead along the current leg (<see cref="CheckAhead"/>,
    ///     rays rationed per tick across all drones). Something new in the way: it asks for a new route from where
    ///     it is (back to 1). Too many of those for one task: the task fails (Unreachable).
    /// The final approach to a work face / connector is the drone's own (a straight, checked line).
    /// </summary>
    public sealed class PathfindingManager
    {
        /// <summary>Kept for the legacy A* / direct planners (IPathfinder), which are not wired.</summary>
        public enum Method : byte { None, Direct, AStar }

        public static PathfindingManager Instance { get; private set; }

        public static void Load(PathfindingConfig config)
        {
            Instance = new PathfindingManager(config);
            if (config.AllowAStar()) Log.Warning("Pathfinding: A* is not available yet, direct pathfinding is used");
        }

        public static void Unload()
        {
            Instance = null;
        }

        private readonly PathfindingConfig config;
        private readonly List<IHitInfo> hits = new List<IHitInfo>();
        private readonly List<Vector3D> points = new List<Vector3D>();
        private int raysLeft;           // this tick, for look-ahead checks
        private int planRays;           // spent by the current PlanRoute
        private int unseen;             // stretches of the current plan the drone couldn't fully see
        private readonly List<IMySlimBlock> blockBuffer = new List<IMySlimBlock>();

        private const double TERRAIN_END_MARGIN = 15.0; // m at each end of a leg not checked for terrain (work near ground)
        private const int MAX_PLAN_RAYS = 400;          // per route (BeginPlan)
        private const int DETOUR_STEPS = 4;             // offsets tried: step, 2x, 4x, 8x

        private PathfindingManager(PathfindingConfig config)
        {
            this.config = config;
        }

        /// <summary>Session, every tick: refills the look-ahead ray budget shared by all drones.</summary>
        public void Update()
        {
            raysLeft = config.RaysPerTick;
        }

        #region Instruments (simulated)
        /// <summary>
        /// How far the drone "sees" along world direction 'dir' (m), measured from the controller. Short range,
        /// omnidirectional: any functional, switched-on sensor gives SimulatedSensorRange all around (its own field
        /// settings don't matter). Long range, directional: a functional, switched-on camera gives the camera range
        /// along its facing (-Z), over the square frustum where -Z is the dominant axis (90 degrees across, so six
        /// cameras, one per side, cover every direction). An instrument that isn't required (server settings) is
        /// simulated all around, even without the block. Never fired as real camera raycasts / sensor queries.
        /// </summary>
        public double ObservedRange(ref PathQuery q, Vector3D dir)
        {
            if (q.SeeAll) return 1e7;
            double camRange = config.MaxSimulatedCameraRaycastMeters();
            if (!q.ViewKnown)
            {
                // All-around part, once per query
                double all = 0;
                if (!config.RequireCamerasForPathfinding()) all = camRange;
                if (!config.RequireSensorsForPathfinding() || AnyOn(q.Sensors, q.SleepingIds))
                    all = Math.Max(all, config.SimulatedSensorRange);
                q.View = all;
                q.ViewKnown = true;
            }
            if (q.View >= camRange || q.Cameras == null) return q.View;
            for (int i = 0; i < q.Cameras.Count; i++)
            {
                var c = q.Cameras[i];
                if (!InstrumentOn(c, q.SleepingIds)) continue;
                MatrixD m = c.WorldMatrix;
                double f = Vector3D.Dot(dir, m.Forward);
                if (f > 0 && f >= Math.Abs(Vector3D.Dot(dir, m.Right)) && f >= Math.Abs(Vector3D.Dot(dir, m.Up)))
                    return camRange;
            }
            return q.View;
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

        #region Rays
        /// <summary>
        /// Anything in the way of the hull flying a -> b, as far as the drone at q.Controller can see? A bundle of
        /// rays: the hull centre and the four sides of its cross-section across the travel direction. Hits closer
        /// than 'skipStart' to a are ignored (where the drone already is). The drone's grids, characters, floating
        /// objects and planets (terrain is handled separately) are not obstacles; asteroids are.
        /// </summary>
        public bool SegmentBlocked(ref PathQuery q, Vector3D a, Vector3D b, double skipStart, out Vector3D hit, out IMyEntity hitEntity)
        {
            return SegmentBlocked(ref q, ref q.Controller, a, b, skipStart, out hit, out hitEntity);
        }

        /// <summary>
        /// Same, with the hull held at 'attitude' (rotation only) on that leg. 'extendPastEnd': also where the hull's
        /// front and the berth reach when the drone is at b (stops, detour points); not for look-ahead / a goal the
        /// player picked next to something.
        /// </summary>
        public bool SegmentBlocked(ref PathQuery q, ref MatrixD attitude, Vector3D a, Vector3D b, double skipStart,
                                   out Vector3D hit, out IMyEntity hitEntity, bool extendPastEnd = true)
        {
            hit = Vector3D.Zero;
            hitEntity = null;
            Vector3D along = b - a;
            double len = along.Length();
            if (len < 0.1) return false;
            Vector3D dir = along / len;
            // Observed part: within the range the drone sees in that direction, measured from the drone
            double seen = ObservedRange(ref q, dir) - Vector3D.Distance(a, q.Controller.Translation);
            if (seen < len) unseen++;               // (partly) unobserved: assumed clear, checked again on the way
            if (seen <= skipStart) return false;

            Vector3D side1, side2;
            double s1, s2;
            CrossSection(ref q, ref attitude, dir, out side1, out s1, out side2, out s2);
            double berth = config.ObstacleClearance;
            // Up to where the hull's front ends when it is at b
            Vector3D halfBox = q.LocalHull.Max.X >= q.LocalHull.Min.X ? q.LocalHull.HalfExtents : new Vector3D(1);
            double end = Math.Min(len + (extendPastEnd ? HalfSizeAlong(ref attitude, ref halfBox, dir) + berth : 0), seen);
            // Rays from the hull centre: the points are hull centres, or controller positions offset to it
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
            if (top is IMyCharacter || top is IMyFloatingObject || top is MyPlanet) return false;   // planets: terrain checks
            var grid = top as IMyCubeGrid;
            if (grid != null && q.OwnGrids != null && q.OwnGrids.Contains(grid)) return false;
            return true;
        }

        // Two axes across 'dir' and the hull's half size along each (from the hull box, controller frame)
        private static void CrossSection(ref PathQuery q, ref MatrixD attitude, Vector3D dir, out Vector3D side1, out double s1,
                                         out Vector3D side2, out double s2)
        {
            side1 = Vector3D.CalculatePerpendicularVector(dir);
            side2 = Vector3D.Cross(dir, side1);
            Vector3D half = q.LocalHull.Max.X >= q.LocalHull.Min.X ? q.LocalHull.HalfExtents : new Vector3D(1);
            s1 = HalfSizeAlong(ref attitude, ref half, side1);
            s2 = HalfSizeAlong(ref attitude, ref half, side2);
        }

        private static double HalfSizeAlong(ref MatrixD m, ref Vector3D half, Vector3D w)
        {
            return Math.Abs(Vector3D.Dot(w, m.Right)) * half.X + Math.Abs(Vector3D.Dot(w, m.Up)) * half.Y
                 + Math.Abs(Vector3D.Dot(w, m.Backward)) * half.Z;
        }

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
        /// Room to turn at 'centre': nothing within 'radius' plus the berth, probed with 14 rays (axes and diagonals
        /// of the world frame). Unobserved directions count as free (checked again when the drone gets there).
        /// </summary>
        public bool TurnRoomFree(ref PathQuery q, Vector3D centre, double radius)
        {
            radius += config.ObstacleClearance;
            if (!HullSpaceFree(ref q, centre, radius)) return false;   // rays miss what they start inside
            for (int k = 0; k < 14; k++)
            {
                Vector3D d = k < 6 ? (Vector3D)Base6Directions.GetIntVector((Base6Directions.Direction)k)
                           : new Vector3D((k & 1) != 0 ? 1 : -1, (k & 2) != 0 ? 1 : -1, (k & 4) != 0 ? 1 : -1) * 0.57735026919;
                double seen = Math.Min(radius, ObservedRange(ref q, d) - Vector3D.Distance(centre, q.Controller.Translation));
                if (seen <= 0.5) continue;
                hits.Clear();
                planRays++;
                MyAPIGateway.Physics.CastRay(centre, centre + d * seen, hits);
                for (int i = 0; i < hits.Count; i++)
                {
                    var e = hits[i].HitEntity;
                    if (e != null && IsObstacle(ref q, e)) { hits.Clear(); return false; }
                }
            }
            hits.Clear();
            return true;
        }

        /// <summary>
        /// No block of another grid (built, with physics) within 'radius' of 'centre'? Catches what rays can't: a
        /// point inside something (a parked drone, a ship) - rays that start inside a body don't hit it. Voxels are
        /// left to the rays / terrain checks.
        /// </summary>
        public bool HullSpaceFree(ref PathQuery q, Vector3D centre, double radius)
        {
            var sphere = new BoundingSphereD(centre, radius);
            var entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            for (int i = 0; i < entities.Count; i++)
            {
                var grid = entities[i] as IMyCubeGrid;
                if (grid == null || grid.Physics == null || grid.MarkedForClose) continue;   // projections have no physics
                if (q.OwnGrids != null && q.OwnGrids.Contains(grid)) continue;
                blockBuffer.Clear();
                blockBuffer.AddRange(grid.GetBlocksInsideSphere(ref sphere));
                if (blockBuffer.Count > 0)
                {
                    blockBuffer.Clear();
                    return false;
                }
            }
            blockBuffer.Clear();
            return true;
        }

        /// <summary>
        /// Look-ahead on the way (rationed: a few rays per tick for all drones). True = something new is in the
        /// way between 'from' and 'to'. False also when the budget is spent this tick: try again later.
        /// </summary>
        public bool CheckAhead(ref PathQuery q, Vector3D from, Vector3D to, out Vector3D hit)
        {
            hit = Vector3D.Zero;
            if (raysLeft < 5) return false;
            raysLeft -= 5;
            IMyEntity e;
            return SegmentBlocked(ref q, ref q.Controller, from, to, 0.5, out hit, out e, false);
        }
        #endregion

        #region Planning
        /// <summary>
        /// Waypoints (controller positions, world) from 'start' to 'goal', both excluded: detours around what the
        /// drone sees in the way, climbs over terrain. Bounded by MaxRepositionAttempts detours and MaxPathNodes
        /// waypoints; Blocked when that is not enough.
        /// </summary>
        /// <summary>Starts a plan's ray budget (MAX_PLAN_RAYS), shared by the PlanRoute calls of one route.</summary>
        public void BeginPlan()
        {
            planRays = 0;
        }

        public RouteResult PlanRoute(ref PathQuery q, Vector3D start, Vector3D goal, List<Vector3D> waypoints)
        {
            unseen = 0;
            // A goal inside something (another drone parked there, a ship) can't be reached, whatever the route
            if (!q.LooseGoal && !HullSpaceFree(ref q, goal, HullRadius(ref q)))
                return Fail(start, goal, "the goal is inside something");
            points.Clear();
            points.Add(start);
            ShapeForPlanet(ref q, start, goal, points);
            points.Add(goal);
            int detours = 0;
            int maxDetours = config.MaxRepositionAttempts();
            int maxPoints = Math.Max(2, config.MaxPathNodes()) + 2;
            int i = 0;
            while (i < points.Count - 1)
            {
                if (planRays > MAX_PLAN_RAYS || points.Count > maxPoints) return Fail(start, goal, "ray / node budget");
                Vector3D a = points[i], b = points[i + 1];

                Vector3D over1, over2;
                if (TerrainInTheWay(ref q, a, b, out over1, out over2))
                {
                    if (++detours > maxDetours) return Fail(start, goal, "terrain");
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
                    if (++detours > maxDetours) return Fail(start, goal, "too many obstacles");
                    Vector3D p;
                    if (!DetourPoint(ref q, a, b, hit, entity, out p)) return Fail(start, goal, "no way around " + (entity != null ? entity.DisplayName : "?"));
                    points.Insert(i + 1, p);
                    continue;   // a -> p checked by construction; p -> b next
                }
                i++;
            }
            for (int k = 1; k < points.Count - 1; k++) waypoints.Add(points[k]);
            int added = points.Count - 2;
            points.Clear();
            Log.Debug("Pathfinding: {0:F0} m, {1} waypoints, {2} stretches not fully seen (assumed clear), {3} rays",
                      Vector3D.Distance(start, goal), added, unseen, planRays);
            return added > 0 ? RouteResult.Detour : RouteResult.Direct;
        }

        private RouteResult Fail(Vector3D start, Vector3D goal, string why)
        {
            Log.Debug("Pathfinding: no route {0:F0} m ({1}), {2} rays", Vector3D.Distance(start, goal), why, planRays);
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
                    if (planRays > MAX_PLAN_RAYS) return false;         // budget spent: give up (Blocked)
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
            double need = HullRadius(ref q) + q.Clearance;
            double step = Math.Max(10, len / 12);
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
            double height = highest + Math.Max(config.MinAltitudeBuffer(), q.MinAltitude) + need;
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
                double theta = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(u0, u1), -1, 1));
                int n = (int)Math.Ceiling(theta * Math.Max(r0, r1) / config.ArcStep) - 1;
                n = Math.Min(n, Math.Max(1, config.MaxPathNodes() / 2));   // long arcs: wider steps, not a failed plan
                double sin = Math.Sin(theta);
                for (int k = 1; k <= n && sin > 1e-6; k++)
                {
                    double t = (double)k / (n + 1);
                    Vector3D u = (u0 * Math.Sin((1 - t) * theta) + u1 * Math.Sin(t * theta)) / sin;
                    double r = r0 + (r1 - r0) * t;
                    Vector3D p = c + u * r;
                    r = Math.Max(r, SurfaceRadius(planet, ref c, p) + floorAlt);
                    pts.Add(c + u * r);
                }
            }
            if (minAlt && r1 > rGoal + 1) pts.Add(c + u1 * r1);                                    // above the goal
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
                if (q.AlignToRoute && planRays <= MAX_PLAN_RAYS && AlignedFrame(ref q, ref g, b - a, fwd, up, inGravity, out f, out u))
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
                    if (planRays > MAX_PLAN_RAYS || SegmentBlocked(ref q, ref prevAtt, a, b, 0.5, out h, out e))
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

        /// <summary>How to pass a waypoint, from the turn between the leg in and the leg out (15° / 45°).</summary>
        public static WaypointBehavior Classify(Vector3D inDir, Vector3D outDir)
        {
            if (inDir.LengthSquared() < 1e-6 || outDir.LengthSquared() < 1e-6) return WaypointBehavior.FullStop;
            double cos = Vector3D.Dot(Vector3D.Normalize(inDir), Vector3D.Normalize(outDir));
            double angle = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(cos, -1, 1)));
            return angle < 15 ? WaypointBehavior.RunThrough : angle < 45 ? WaypointBehavior.SlowApproach : WaypointBehavior.FullStop;
        }
        #endregion
    }
}
