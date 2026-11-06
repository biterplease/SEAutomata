using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Config;
using Automata.Util.Logging;
using VRage.Utils;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Directional flight lanes (TODO/25). Server only, one store (PathfindingManager.Lanes): written by the orchestrators,
    /// read by them (list, drawing) and by the pathfinder (routes through them).
    ///  - Traced by an orchestrator (Trace), a best estimate straight away (the coordinates are known, no flight needed): when
    ///    it publishes an auction with a location (its base to the job's place: a base, an asteroid, a planet ore hotspot, else
    ///    the nearest static grid or the spot), and between the player bases on its relay network. The straight line between
    ///    the two places, ends outside them (BASE_MARGIN off a base's sphere, WORK_MARGIN short of a work point); in gravity
    ///    over the heightmap (TERRAIN_ALTITUDE to the centre line), sampled every TRACE_SAMPLE m.
    ///  - A pair of boxes BOX_WIDTH x BOX_HEIGHT along the centre line, one above the other ("up": gravity, else the base's up):
    ///    A -> B above, B -> A below, BOX_GAP apart. A new lane where another one is gets lifted over it (with ramps).
    ///  - Sub-lanes (FindLane, for the pathfinder): a drone takes a slot across its box SLOT_WIDTH x its cross-section wide,
    ///    SLOT_CLEAR x its length clear before and after it, the first from the box's left edge (its own left) free at the
    ///    entrance; none free: it waits outside (LANE_WAIT_MAX_TICKS at most, then no lane). The drone only gets the slot's
    ///    line as waypoints. Hulls wider than HULL_MAX fly beside the box, outside it.
    ///  - Traffic (Report): a drone is slowed so it can stop behind the one ahead in its slot band.
    ///  - Obstructions: rock, trees or static grids a drone's route check found in a lane, with the points it flew round
    ///    them (the drone reports them through its job reports, held until it is in antenna range): the lane is redrawn
    ///    (Obstructed): a clear straight stretch within REDRAW_OFFSET_MAX m of the old one, else along the drone's way round.
    ///  - Suspended by a near miss or a crash in it; resumes on its own after RESUME_MINUTES (drones sort themselves out).
    ///  - Order lanes (made for an area sweep / spot check): dropped when every scout job of the order reported nothing
    ///    found, kept (as any lane) once one found ore. Unused lanes expire after FlightLaneExpirationMinutes.
    ///  - Names: "L7 Home Base → Asteroid 4821". Owned by the orchestrator that traced it. Saved with the world.
    /// </summary>
    public sealed class FlightLanes
    {
        public enum LaneState : byte { Pending = 0, Active = 1, Suspended = 2 }   // (Pending: version 1 files only)
        public enum Join : byte { None, Through, Beside, Wait }

        public const double BOX_WIDTH = 40;              // m across a lane's box
        public const double BOX_HEIGHT = 40;             // m up / down
        public const double BOX_GAP = 5;                 // m between the A -> B box (above) and the B -> A box (below)
        public const double HULL_MAX = 25;               // m of cross-section at most for a slot; wider: beside the box
        private const double SLOT_WIDTH = 1.5;           // x the drone's cross-section: its slot's width
        private const double SLOT_CLEAR = 4;             // x the drone's length clear before and after it in its slot
        private const double SIDE_MARGIN = 5;            // m between the box and a hull flying beside it
        private const int LANE_WAIT_MAX_TICKS = 20 * 60; // waiting for a slot at the entrance at most, then no lane
        private const int RESERVATION_TICKS = 120 * 60;  // a slot given but not flown into: free again after this
        private const int OCCUPANT_TICKS = 60 * 3;       // a report this old: no longer in the lane
        private const double RESUME_MINUTES = 2;         // a suspended lane resumes on its own after this
        private const double REDRAW_OFFSET_MAX = 50;     // m: a clear straight stretch looked for this far from the old one
        private const double REDRAW_STEP = 10;
        private const double TRACE_SAMPLE = 20;          // m between the points of a traced line (trimmed at its ends, over the heightmap)
        private const double TERRAIN_ALTITUDE = 40 + BOX_HEIGHT + BOX_GAP / 2;   // m: the centre line over the heightmap (40 m under the lower box)
        private const double TERRAIN_SAG = 20;          // m a merged leg may dip under TERRAIN_ALTITUDE (20 m under the lower box left)
        private const double CROSS_MARGIN = 10;          // m between two lanes' boxes where one crosses the other
        private const double BASE_RANGE = 600;           // m: a place this close to a base belongs to it
        private const double ASTEROID_RANGE = 400;       // m beyond an asteroid's bounding sphere
        private const double BASE_MARGIN = 60;           // m: lanes stop this far outside a base's bounding sphere
        private const double WORK_MARGIN = 40;           // m: lanes end this far before the work point
        private const double MIN_LENGTH = 300;           // m of lane, at least
        private const double ENTRY_RANGE = 1000;         // m from start / goal to the lane's ends, at most
        private const double DETOUR_RATIO = 1.3;         // the way through a lane: at most this much longer than straight
        private const double CROSS_RAMP = 60;            // m each side of a crossing
        private const int SAVE_EVERY_TICKS = 60 * 60;
        private const double LANE_DROP_BACK = 5;         // m/s slower than the drone level with it: a follower drops back into line
        private const double HOTSPOT_RADIUS = 500;       // m: deposits this close to each other are one hotspot (planets)
        private const double HOTSPOT_RANGE = 400;        // m beyond a hotspot's deposits: a trip end there belongs to it
        private const double HOTSPOT_CELL = 500;         // m: a hotspot's id comes from its centre on this grid
        private const double POINT_CELL = 200;           // m: an end in open space: its id comes from this grid
        private const int FILE_VERSION = 2;              // 2: traced boxes, order lanes (1: flown lines, still read)
        private const string FILE_NAME = "Automata_Lanes.bin";

        public sealed class Lane
        {
            public int Number;
            public long Owner;                 // orchestrator entity id
            public long EndA, EndB;            // place entity ids
            public string NameA, NameB;
            public List<Vector3D> Centre = new List<Vector3D>();   // world, A to B
            public Vector3D Up;
            public int Trips;                  // routes given through it
            public long LastUsed, Created;     // UTC minutes
            public LaneState State;
            public string Reason = "";
            public long SuspendedAt;           // UTC ticks
            public bool Order;                 // made for an area sweep / spot check that found nothing yet
            public bool OrderFound;
            public List<uint> OrderJobs = new List<uint>();   // its order's scout jobs still out
            // derived
            public List<Vector3D> AB = new List<Vector3D>(), BA = new List<Vector3D>();   // the boxes' centre lines, each its way
            public List<Vector3D> UpAB = new List<Vector3D>(), UpBA = new List<Vector3D>(); // "up" at each point
            public double Length;
            public string Name(bool ab) { return "L" + Number + " " + (ab ? NameA + " → " + NameB : NameB + " → " + NameA); }
            public string PairName { get { return "L" + Number + " " + NameA + " ⇄ " + NameB; } }
        }

        private enum PlaceKind : byte { Base, Asteroid, Hotspot, Grid, Point }

        // A lane end: a base (static grid with an Automata computer block), an asteroid, a planet ore hotspot; else the
        // nearest static grid, or a spot in space
        private struct Place
        {
            public long Id;
            public string Name;
            public BoundingSphereD Sphere;
            public PlaceKind Kind;
            public Vector3D Outward;       // work places: lanes end above the work, away from this (rock / planet centre)
            public bool Work { get { return Kind == PlaceKind.Asteroid || Kind == PlaceKind.Hotspot || Kind == PlaceKind.Point; } }
            public bool Natural { get { return Kind == PlaceKind.Asteroid || Kind == PlaceKind.Hotspot; } }
        }

        // A drone given a slot (FindLane) and, once inside its box, flying it (Report)
        private sealed class Rider
        {
            public int Lane;
            public bool AB, In;
            public double Lat, W, Clear;       // its slot: across the box (+: right), width, m clear before / after
            public double S, V, BrakeA, FullA, Hull;
            public int Frame, WaitSince;
            public Vector3D Pos;
        }

        private readonly Dictionary<int, Lane> lanes = new Dictionary<int, Lane>();
        private readonly Dictionary<long, Rider> riders = new Dictionary<long, Rider>();
        private readonly Dictionary<long, int> waiting = new Dictionary<long, int>();   // drone -> tick its wait at an entrance began
        private readonly Dictionary<string, long> holds = new Dictionary<string, long>();   // "owner|a|b" -> UTC minute the hold ends
        private readonly PathfindingConfig config;
        private int nextNumber = 1;
        private bool dirty;
        private int lastSave, listVersion;

        public int Version { get { return listVersion; } }

        public FlightLanes(PathfindingConfig config)
        {
            this.config = config;
        }

        private static long NowMinutes { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute; } }
        private double Clearance { get { return config.ObstacleClearance; } }
        private static double DirOffset { get { return BOX_HEIGHT / 2 + BOX_GAP / 2; } }   // m from the centre line to a box's centre line

        #region Tracing (the orchestrators make lanes)
        /// <summary>
        /// A lane of 'owner' between the places of 'from' and 'to' (made if there is none; either way round counts). 'orderJob':
        /// a scout job of an area sweep / spot check order (the lane stays an order lane until one of its jobs finds ore).
        /// 0: no lane (too short, the same place, two natural places, held after a player deleted it).
        /// </summary>
        public int Trace(long owner, Vector3D from, Vector3D to, uint orderJob = 0)
        {
            if (owner == 0) return 0;
            try
            {
                Place pa, pb;
                if (!FindPlace(from, out pa)) pa = GenericEnd(from);
                if (!FindPlace(to, out pb)) pb = GenericEnd(to);
                if (pa.Id == pb.Id || (pa.Natural && pb.Natural))
                {
                    if (Log.DebugEnabled) Log.Debug("Lanes: no lane {0} - {1}: {2}", pa.Name, pb.Name, pa.Id == pb.Id ? "the same place" : "two natural places");
                    return 0;
                }
                foreach (var l in lanes.Values)
                {
                    if (l.Owner != owner || !((l.EndA == pa.Id && l.EndB == pb.Id) || (l.EndA == pb.Id && l.EndB == pa.Id))) continue;
                    l.LastUsed = NowMinutes;
                    if (orderJob != 0 && l.Order && !l.OrderJobs.Contains(orderJob)) l.OrderJobs.Add(orderJob);
                    dirty = true;
                    return l.Number;
                }
                long hold;
                if (holds.TryGetValue(HoldKey(owner, pa.Id, pb.Id), out hold) && hold > NowMinutes)
                {
                    if (Log.DebugEnabled) Log.Debug("Lanes: no lane {0} - {1}: a player deleted it ({2} min left)", pa.Name, pb.Name, hold - NowMinutes);
                    return 0;
                }
                return MakeLane(owner, pa, pb, from, to, orderJob);
            }
            catch (Exception ex)
            {
                Log.Error("Lanes: no lane traced for {0}: {1}", owner, ex.Message);
                return 0;
            }
        }

        private int MakeLane(long owner, Place pa, Place pb, Vector3D from, Vector3D to, uint orderJob)
        {
            // The straight line from place to place, a point every TRACE_SAMPLE m, trimmed at both ends; in gravity over the
            // heightmap
            Vector3D a = pa.Kind == PlaceKind.Base || pa.Kind == PlaceKind.Grid ? pa.Sphere.Center : from;
            Vector3D b = pb.Kind == PlaceKind.Base || pb.Kind == PlaceKind.Grid ? pb.Sphere.Center : to;
            double dist = Vector3D.Distance(a, b);
            if (dist < MIN_LENGTH) { if (Log.DebugEnabled) Log.Debug("Lanes: no lane {0} - {1}: {2:F0} m apart", pa.Name, pb.Name, dist); return 0; }
            int n = Math.Max(2, (int)Math.Ceiling(dist / TRACE_SAMPLE) + 1);
            var pts = new List<Vector3D>(n);
            for (int i = 0; i < n; i++) pts.Add(a + (b - a) * (i / (double)(n - 1)));
            TrimEnd(pts, pa, true);
            TrimEnd(pts, pb, false);
            if (pts.Count < 2) { if (Log.DebugEnabled) Log.Debug("Lanes: no lane {0} - {1}: nothing left between them", pa.Name, pb.Name); return 0; }
            pts = OverTerrain(pts);
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += Vector3D.Distance(pts[i - 1], pts[i]);
            if (len < MIN_LENGTH) { if (Log.DebugEnabled) Log.Debug("Lanes: no lane {0} - {1}: {2:F0} m between their ends", pa.Name, pb.Name, len); return 0; }
            var lane = new Lane
            {
                Number = nextNumber++, Owner = owner, EndA = pa.Id, EndB = pb.Id, NameA = pa.Name, NameB = pb.Name,
                LastUsed = NowMinutes, Created = NowMinutes, State = LaneState.Active, Order = orderJob != 0,
            };
            if (orderJob != 0) lane.OrderJobs.Add(orderJob);
            lane.Centre = Simplify(pts);
            lane.Up = UpFor(lane.Centre[lane.Centre.Count / 2], pa.Kind == PlaceKind.Base || pa.Kind == PlaceKind.Grid ? pa.Id : pb.Id);
            AvoidCrossings(lane);
            lanes[lane.Number] = lane;
            dirty = true;
            listVersion++;
            if (Log.DebugEnabled) Log.Debug("Lanes: {0} traced ({1:F0} m, {2} point(s){3})", lane.PairName, lane.Length, lane.Centre.Count, lane.Order ? ", for an order" : "");
            return lane.Number;
        }

        // Trim an end of the line: short of a base (outside its sphere plus a margin), or above a work point (back from it by
        // WORK_MARGIN, and out of the rock)
        private static void TrimEnd(List<Vector3D> pts, Place place, bool start)
        {
            Vector3D work = start ? pts[0] : pts[pts.Count - 1];
            while (pts.Count > 1)
            {
                int i = start ? 0 : pts.Count - 1;
                Vector3D p = pts[i];
                bool keep = place.Work
                    ? Vector3D.Distance(p, work) >= WORK_MARGIN && (place.Kind == PlaceKind.Point
                                                                   || Vector3D.Distance(p, place.Outward) > Vector3D.Distance(work, place.Outward))
                    : Vector3D.Distance(p, place.Sphere.Center) > place.Sphere.Radius + BASE_MARGIN;
                if (keep) break;
                pts.RemoveAt(i);
            }
        }

        // In gravity: as straight as the terrain lets it, in the fewest legs. Every sample of the straight line needs
        // TERRAIN_ALTITUDE over the highest heightmap point under it and BOX_WIDTH / 2 to either side; in the plane through
        // the planet's centre and both ends the line becomes the taut string over that (the outer hull: ridge to ridge,
        // straight up at an end that starts low), then legs are merged while they sag at most TERRAIN_SAG under it
        // (the planet's curve on long lanes). Out of gravity: the line as it is.
        private static List<Vector3D> OverTerrain(List<Vector3D> pts)
        {
            var planet = MyGamePruningStructure.GetClosestPlanet(pts[pts.Count / 2]);
            if (planet == null) return pts;
            float interference;
            bool any = false;
            for (int i = 0; i < pts.Count && !any; i++) any = MyAPIGateway.Physics.CalculateNaturalGravityAt(pts[i], out interference).LengthSquared() > 0.01f;
            if (!any) return pts;
            Vector3D c = planet.PositionComp.GetPosition();
            Vector3D a = pts[0] - c, b = pts[pts.Count - 1] - c;
            Vector3D u = Vector3D.Normalize(a);
            Vector3D w = b - u * Vector3D.Dot(b, u);
            if (w.Normalize() < 1) return pts;   // one above the other: the straight line is the climb
            Vector3D side = Vector3D.Cross(u, w);
            int n = pts.Count;
            var need = new Vector2D[n];   // per sample: (cos θ, sin θ) * the radius it needs
            var dirs = new Vector2D[n];
            for (int i = 0; i < n; i++)
            {
                Vector3D p = pts[i] - c;
                var d = new Vector2D(Vector3D.Dot(p, u), Vector3D.Dot(p, w));
                double r = d.Length();
                d /= r;
                dirs[i] = d;
                double g = 0;
                for (int k = -1; k <= 1; k++)
                {
                    Vector3D q = pts[i] + side * (k * BOX_WIDTH / 2);
                    g = Math.Max(g, Vector3D.Distance(planet.GetClosestSurfacePointGlobal(ref q), c));
                }
                need[i] = d * (g + TERRAIN_ALTITUDE);
                if ((i == 0 || i == n - 1) && r > g + TERRAIN_ALTITUDE) need[i] = d * r;   // the ends: where they are, if high enough
            }
            // Outer hull, start to end (θ grows: the centre is on the left of the way)
            var hull = new List<int>(n) { 0 };
            for (int i = 1; i < n; i++)
            {
                while (hull.Count >= 2 && LeftOrOn(need[hull[hull.Count - 2]], need[i], need[hull[hull.Count - 1]], 0)) hull.RemoveAt(hull.Count - 1);
                hull.Add(i);
            }
            // Fewest legs: from each kept point, the farthest hull point every sample in between stays under (with TERRAIN_SAG)
            var keep = new List<int> { 0 };
            int at = 0;
            while (hull[at] != n - 1)
            {
                int next = at + 1;
                for (int j = at + 2; j < hull.Count; j++)   // (the first that dips too far ends it: cost ~ leg samples²)
                {
                    bool clear = true;
                    for (int m = hull[at] + 1; m < hull[j] && clear; m++)
                        clear = LeftOrOn(need[hull[at]], need[hull[j]], need[m] - dirs[m] * TERRAIN_SAG, 1e-6);
                    if (!clear) break;
                    next = j;
                }
                at = next;
                keep.Add(hull[at]);
            }
            var r2 = new List<Vector3D>(keep.Count);
            for (int i = 0; i < keep.Count; i++) r2.Add(c + u * need[keep[i]].X + w * need[keep[i]].Y);
            return r2;
        }

        // 'p' left of (or on) the way from 'a' to 'b' (towards the planet's centre)
        private static bool LeftOrOn(Vector2D a, Vector2D b, Vector2D p, double eps)
        {
            Vector2D d = b - a, e = p - a;
            return d.X * e.Y - d.Y * e.X >= -eps * d.Length() * e.Length();
        }

        // Points closer than a few metres to the line between their neighbours go
        private static List<Vector3D> Simplify(List<Vector3D> pts)
        {
            var r = new List<Vector3D> { pts[0] };
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                Vector3D a = r[r.Count - 1], b = pts[i + 1], p = pts[i];
                Vector3D ab = b - a;
                double l2 = ab.LengthSquared();
                double t = l2 > 1e-6 ? MathHelperD.Clamp(Vector3D.Dot(p - a, ab) / l2, 0, 1) : 0;
                if (Vector3D.DistanceSquared(p, a + ab * t) > 3 * 3) r.Add(p);
            }
            r.Add(pts[pts.Count - 1]);
            return r;
        }

        private static Vector3D UpFor(Vector3D p, long baseId)
        {
            float interference;
            Vector3 g = MyAPIGateway.Physics.CalculateNaturalGravityAt(p, out interference);
            if (g.LengthSquared() > 0.01f) return -Vector3D.Normalize(g);
            IMyEntity e;
            if (MyAPIGateway.Entities.TryGetEntityById(baseId, out e) && e != null) return e.WorldMatrix.Up;
            return Vector3D.Up;
        }

        // Up at 'p': gravity there, else the lane's
        private static Vector3D UpAt(Vector3D p, Vector3D lane)
        {
            float interference;
            Vector3 g = MyAPIGateway.Physics.CalculateNaturalGravityAt(p, out interference);
            return g.LengthSquared() > 0.01f ? -Vector3D.Normalize(g) : lane;
        }
        // The places lanes join: a static grid with an Automata computer block, an asteroid, or (on a planet) an ore hotspot
        private bool FindPlace(Vector3D p, out Place place)
        {
            place = default(Place);
            var area = new BoundingSphereD(p, BASE_RANGE + ASTEROID_RANGE + 2000);
            var found = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref area);
            double best = double.MaxValue;
            // In a planet's gravity the work is on the planet: no asteroid (a voxel map nearby made a planet sweep's lane
            // "Base B ⇄ Asteroid 3053")
            float interference;
            bool gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(p, out interference).LengthSquared() > 0.01f;
            for (int i = 0; i < found.Count; i++)
            {
                var e = found[i];
                var grid = e as IMyCubeGrid;
                if (grid != null)
                {
                    if (!grid.IsStatic) continue;
                    var s = grid.WorldVolume;
                    double d = Math.Max(0, Vector3D.Distance(p, s.Center) - s.Radius);
                    if (d > BASE_RANGE || d >= best || !HasAutomataComputer(grid)) continue;
                    best = d;
                    place = new Place { Id = grid.EntityId, Name = Short(grid.DisplayName), Sphere = s, Kind = PlaceKind.Base };
                    continue;
                }
                var v = e as MyVoxelBase;
                if (v != null && !gravity && PathfindingManager.IsAsteroid(v))
                {
                    var s = v.PositionComp.WorldVolume;
                    double d = Math.Max(0, Vector3D.Distance(p, s.Center) - s.Radius);
                    if (d > ASTEROID_RANGE || d >= best) continue;
                    best = d;
                    place = new Place { Id = v.EntityId, Name = "Asteroid " + (v.EntityId % 10000).ToString("0000"), Sphere = s,
                                        Kind = PlaceKind.Asteroid, Outward = s.Center };
                }
            }
            if (place.Id != 0) return true;
            return FindHotspot(p, out place);
        }

        private readonly List<KeyValuePair<MyStringHash, Vector3D>> depositScratch = new List<KeyValuePair<MyStringHash, Vector3D>>();
        private readonly Dictionary<MyStringHash, int> oreCount = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);

        // On a planet: the known deposits near 'p' (the deposit nearest to it, and every one within HOTSPOT_RADIUS of that)
        private bool FindHotspot(Vector3D p, out Place place)
        {
            place = default(Place);
            var planet = MyGamePruningStructure.GetClosestPlanet(p);
            if (planet == null) return false;
            float interference;
            if (MyAPIGateway.Physics.CalculateNaturalGravityAt(p, out interference).LengthSquared() < 0.01f) return false;
            depositScratch.Clear();
            Mining.MiningSurveyorBlock.DepositsNear(p, HOTSPOT_RANGE + HOTSPOT_RADIUS, depositScratch);
            if (depositScratch.Count == 0) return false;
            int seed = -1;
            double best = double.MaxValue;
            for (int i = 0; i < depositScratch.Count; i++)
            {
                double d = Vector3D.DistanceSquared(depositScratch[i].Value, p);
                if (d < best) { best = d; seed = i; }
            }
            if (Math.Sqrt(best) > HOTSPOT_RANGE) { depositScratch.Clear(); return false; }
            Vector3D seedPos = depositScratch[seed].Value, centre = Vector3D.Zero;
            int n = 0;
            oreCount.Clear();
            for (int i = 0; i < depositScratch.Count; i++)
            {
                if (Vector3D.Distance(depositScratch[i].Value, seedPos) > HOTSPOT_RADIUS) continue;
                centre += depositScratch[i].Value;
                n++;
                int c;
                oreCount.TryGetValue(depositScratch[i].Key, out c);
                oreCount[depositScratch[i].Key] = c + 1;
            }
            centre /= n;
            double radius = 0;
            for (int i = 0; i < depositScratch.Count; i++)
                if (Vector3D.Distance(depositScratch[i].Value, seedPos) <= HOTSPOT_RADIUS)
                    radius = Math.Max(radius, Vector3D.Distance(depositScratch[i].Value, centre));
            MyStringHash main = MyStringHash.NullOrEmpty;
            int most = 0;
            foreach (var kv in oreCount) if (kv.Value > most) { most = kv.Value; main = kv.Key; }
            depositScratch.Clear();
            oreCount.Clear();
            string ore = Inventory.ItemKeys.Name(main);
            if (ore.StartsWith("Ore/", StringComparison.Ordinal)) ore = ore.Substring(4);
            place = new Place
            {
                Id = SyntheticId(1, planet.EntityId, centre, HOTSPOT_CELL), Name = PlanetName(planet) + " " + ore + " hotspot",
                Sphere = new BoundingSphereD(centre, radius + 50), Kind = PlaceKind.Hotspot, Outward = planet.PositionComp.GetPosition(),
            };
            return true;
        }

        // An end that isn't a place: the nearest static grid within BASE_RANGE, else the spot itself
        private Place GenericEnd(Vector3D p)
        {
            var area = new BoundingSphereD(p, BASE_RANGE + 2000);
            var found = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref area);
            IMyCubeGrid bestGrid = null;
            double best = double.MaxValue;
            for (int i = 0; i < found.Count; i++)
            {
                var grid = found[i] as IMyCubeGrid;
                if (grid == null || !grid.IsStatic) continue;
                var s = grid.WorldVolume;
                double d = Math.Max(0, Vector3D.Distance(p, s.Center) - s.Radius);
                if (d > BASE_RANGE || d >= best) continue;
                best = d;
                bestGrid = grid;
            }
            if (bestGrid != null)
                return new Place { Id = bestGrid.EntityId, Name = Short(bestGrid.DisplayName), Sphere = bestGrid.WorldVolume, Kind = PlaceKind.Grid };
            long id = SyntheticId(2, 0, p, POINT_CELL);
            // On a planet: named after it
            var planet = MyGamePruningStructure.GetClosestPlanet(p);
            float interference;
            bool onPlanet = planet != null && MyAPIGateway.Physics.CalculateNaturalGravityAt(p, out interference).LengthSquared() > 0.01f;
            return new Place { Id = id, Name = (onPlanet ? PlanetName(planet) : "Point") + " " + (Math.Abs(id) % 10000).ToString("0000"),
                               Sphere = new BoundingSphereD(p, 0), Kind = PlaceKind.Point, Outward = p };
        }

        // A planet's name: its storage name up to the seed ("EarthLike-1234567" -> "EarthLike")
        private static string PlanetName(MyPlanet planet)
        {
            string n = planet.StorageName ?? "";
            int dash = n.IndexOf('-');
            if (dash > 0) n = n.Substring(0, dash);
            return string.IsNullOrEmpty(n) ? "Planet" : n;
        }

        // A stable negative id for a place without an entity: its kind, its parent (planet), its cell on a 'cell' grid
        private static long SyntheticId(int kind, long parent, Vector3D p, double cell)
        {
            long x = (long)Math.Floor(p.X / cell), y = (long)Math.Floor(p.Y / cell), z = (long)Math.Floor(p.Z / cell);
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ kind) * 1099511628211L;
                h = (h ^ parent) * 1099511628211L;
                h = (h ^ x) * 1099511628211L;
                h = (h ^ y) * 1099511628211L;
                h = (h ^ z) * 1099511628211L;
                return -(h & 0x3FFFFFFFFFFFFFFFL) - 1;
            }
        }

        private static readonly List<IMySlimBlock> slimScratch = new List<IMySlimBlock>();
        // A base: an Automata computer block on the grid (logistics, construction, mining surveyor, orchestrator)
        private static bool HasAutomataComputer(IMyCubeGrid grid)
        {
            slimScratch.Clear();
            grid.GetBlocks(slimScratch, s => s.FatBlock != null && s.FatBlock.GameLogic != null
                                            && (s.FatBlock.GameLogic.GetAs<Logistics.LogisticsComputerBlock>() != null
                                                || s.FatBlock.GameLogic.GetAs<Construction.ConstructionComputerBlock>() != null
                                                || s.FatBlock.GameLogic.GetAs<Mining.MiningSurveyorBlock>() != null
                                                || s.FatBlock.GameLogic.GetAs<Orchestrator.OrchestratorBlock>() != null));
            bool any = slimScratch.Count > 0;
            slimScratch.Clear();
            return any;
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Length > 24 ? s.Substring(0, 23) + "…" : s;
        }

        private static string HoldKey(long owner, long a, long b)
        {
            return owner + "|" + Math.Min(a, b) + "|" + Math.Max(a, b);
        }
        #endregion

        #region Geometry
        // The two directed boxes' centre lines: A -> B above the centre line, B -> A below it (reversed); "up" at each point
        private static void Derive(Lane l)
        {
            l.AB.Clear(); l.BA.Clear(); l.UpAB.Clear(); l.UpBA.Clear();
            int n = l.Centre.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3D up = UpAt(l.Centre[i], l.Up);
                l.AB.Add(l.Centre[i] + up * DirOffset);
                l.UpAB.Add(up);
            }
            for (int i = n - 1; i >= 0; i--)
            {
                l.BA.Add(l.Centre[i] - l.UpAB[i] * DirOffset);
                l.UpBA.Add(l.UpAB[i]);
            }
            l.Length = 0;
            for (int i = 1; i < n; i++) l.Length += Vector3D.Distance(l.Centre[i - 1], l.Centre[i]);
        }

        // A new lane whose boxes come within reach of another lane's is lifted over it there (with ramps): the two pairs one
        // over the other, CROSS_MARGIN apart
        private void AvoidCrossings(Lane l)
        {
            Derive(l);
            double need = 2 * (BOX_HEIGHT + BOX_GAP / 2) + CROSS_MARGIN;
            double near = Math.Max(need, BOX_WIDTH + CROSS_MARGIN);
            foreach (var o in lanes.Values)
            {
                if (o.Number == l.Number) continue;
                for (int i = 0; i + 1 < l.Centre.Count; i++)
                    for (int j = 0; j + 1 < o.Centre.Count; j++)
                    {
                        Vector3D pa, pb;
                        if (SegmentDistance(l.Centre[i], l.Centre[i + 1], o.Centre[j], o.Centre[j + 1], out pa, out pb) > near) continue;
                        double by = need - Vector3D.Dot(pa - pb, UpAt(pa, l.Up));   // (already partly above: less)
                        if (by <= 0) continue;
                        Lift(l, pa, by);
                        if (Log.DebugEnabled) Log.Debug("Lanes: new lane lifted {0:F0} m over L{1} where they cross", by, o.Number);
                        Derive(l);
                        return;
                    }
            }
        }

        private static void Lift(Lane l, Vector3D at, double by)
        {
            // Every centre point within CROSS_RAMP x 2 moves, fully within CROSS_RAMP; points added on long segments so the
            // ramps have room
            var dense = new List<Vector3D> { l.Centre[0] };
            for (int i = 1; i < l.Centre.Count; i++)
            {
                Vector3D a = l.Centre[i - 1], b = l.Centre[i];
                int k = (int)Math.Ceiling(Vector3D.Distance(a, b) / CROSS_RAMP);
                for (int j = 1; j < k; j++)
                {
                    Vector3D p = a + (b - a) * (j / (double)k);
                    if (Vector3D.Distance(p, at) < 3 * CROSS_RAMP) dense.Add(p);
                }
                dense.Add(b);
            }
            l.Centre = dense;
            for (int i = 0; i < l.Centre.Count; i++)
            {
                double d = Vector3D.Distance(l.Centre[i], at);
                double w = d <= CROSS_RAMP ? 1 : d >= 2 * CROSS_RAMP ? 0 : 1 - (d - CROSS_RAMP) / CROSS_RAMP;
                l.Centre[i] += UpAt(l.Centre[i], l.Up) * (by * w);
            }
        }

        private static double SegmentDistance(Vector3D p1, Vector3D q1, Vector3D p2, Vector3D q2, out Vector3D c1, out Vector3D c2)
        {
            Vector3D d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = d1.LengthSquared(), e = d2.LengthSquared(), f = Vector3D.Dot(d2, r);
            double s, t;
            if (a < 1e-9 && e < 1e-9) { c1 = p1; c2 = p2; return Vector3D.Distance(p1, p2); }
            if (a < 1e-9) { s = 0; t = MathHelperD.Clamp(f / e, 0, 1); }
            else
            {
                double c = Vector3D.Dot(d1, r);
                if (e < 1e-9) { t = 0; s = MathHelperD.Clamp(-c / a, 0, 1); }
                else
                {
                    double b = Vector3D.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-9 ? MathHelperD.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = MathHelperD.Clamp(-c / a, 0, 1); }
                    else if (t > 1) { t = 1; s = MathHelperD.Clamp((b - c) / a, 0, 1); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
            return Vector3D.Distance(c1, c2);
        }

        // Position along a line: distance along it and off it (nearest segment)
        private static void Project(List<Vector3D> line, Vector3D p, out double s, out double off)
        {
            s = 0; off = double.MaxValue;
            double acc = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector3D a = line[i], b = line[i + 1], ab = b - a;
                double len = ab.Length();
                double t = len > 1e-6 ? MathHelperD.Clamp(Vector3D.Dot(p - a, ab) / (len * len), 0, 1) : 0;
                double d = Vector3D.Distance(p, a + ab * t);
                if (d < off) { off = d; s = acc + t * len; }
                acc += len;
            }
        }

        // Position in a directed box: along its line, across it (+: right of its way) and up / down
        private static void ProjectBox(List<Vector3D> line, List<Vector3D> ups, Vector3D p, out double s, out double lat, out double vert)
        {
            s = 0; lat = vert = double.MaxValue;
            double acc = 0, best = double.MaxValue;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector3D a = line[i], ab = line[i + 1] - a;
                double len = ab.Length();
                if (len < 1e-6) continue;
                Vector3D dir = ab / len;
                double t = MathHelperD.Clamp(Vector3D.Dot(p - a, dir), 0, len);
                Vector3D d = p - (a + dir * t);
                double dd = d.LengthSquared();
                if (dd < best)
                {
                    best = dd;
                    s = acc + t;
                    Vector3D right = Right(dir, ups[i]), up = Vector3D.Cross(right, dir);
                    lat = Vector3D.Dot(d, right);
                    vert = Vector3D.Dot(d, up);
                }
                acc += len;
            }
        }

        private static Vector3D Right(Vector3D dir, Vector3D up)
        {
            Vector3D r = Vector3D.Cross(dir, up);
            if (r.LengthSquared() < 1e-6) r = Vector3D.CalculatePerpendicularVector(dir);
            return Vector3D.Normalize(r);
        }

        // The point 's' m along a directed line (clamped to its ends); 'seg' its segment
        private static Vector3D PointAt(List<Vector3D> line, double s, out int seg)
        {
            double acc = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                double len = Vector3D.Distance(line[i], line[i + 1]);
                seg = i;
                if (s <= acc + len) return len > 1e-6 ? line[i] + (line[i + 1] - line[i]) * (Math.Max(0, s - acc) / len) : line[i];
                acc += len;
            }
            seg = Math.Max(0, line.Count - 2);
            return line[line.Count - 1];
        }

        // The point 's' m along a directed box line, 'lat' m across it (+: right of its way)
        private static Vector3D Across(List<Vector3D> line, List<Vector3D> ups, double s, double lat)
        {
            int seg;
            Vector3D p = PointAt(line, s, out seg);
            if (lat == 0 || line.Count < 2) return p;
            return p + Right(line[seg + 1] - line[seg], ups[seg]) * lat;
        }

        // Vertex 'i' of a directed box line, 'lat' m across it (across the bend: the mean of its two segments' ways)
        private static Vector3D AcrossVertex(List<Vector3D> line, List<Vector3D> ups, int i, double lat)
        {
            if (lat == 0) return line[i];
            Vector3D dir = line[Math.Min(line.Count - 1, i + 1)] - line[Math.Max(0, i - 1)];
            return line[i] + Right(dir, ups[i]) * lat;
        }
        #endregion

        #region Planning (the pathfinder)
        /// <summary>
        /// A lane of 'owner' from near 'start' to near 'goal' for drone 'droneId' ('half': its hull's half extents, X across,
        /// Y up, Z along): Through - its slot's line appended to 'waypoints'; Beside - a line beside the box (a hull wider than
        /// HULL_MAX); Wait - a lane fits but no slot is free at its entrance (the drone holds, asks again); None - no lane fits
        /// (or the wait was LANE_WAIT_MAX_TICKS long).
        /// </summary>
        public Join FindLane(long owner, long droneId, Vector3D start, Vector3D goal, Vector3D half, int now, List<Vector3D> waypoints,
                             out string name, out int number, out bool ab)
        {
            name = null;
            number = 0;
            ab = true;
            if (owner == 0) return Join.None;
            double direct = Vector3D.Distance(start, goal);
            Lane best = null;
            bool bestAB = true;
            double bestCost = double.MaxValue, bestIn = 0, bestOut = 0, bestOffIn = 0;
            foreach (var l in lanes.Values)
            {
                if (l.Owner != owner || l.State != LaneState.Active || l.AB.Count < 2) continue;
                for (int k = 0; k < 2; k++)
                {
                    // Joined where the start is along it and left where the goal is (not at its ends: a drone part way
                    // along - a replan in flight - flew back to the entry, behind it, and out the same way again)
                    var line = k == 0 ? l.AB : l.BA;
                    double sIn, offIn, sOut, offOut;
                    Project(line, start, out sIn, out offIn);
                    Project(line, goal, out sOut, out offOut);
                    if (offIn > ENTRY_RANGE || offOut > ENTRY_RANGE || sOut - sIn < MIN_RIDE) continue;   // its way, far enough
                    double cost = offIn + (sOut - sIn) + offOut;
                    if (cost > direct * DETOUR_RATIO + 200 || cost >= bestCost) continue;
                    bestCost = cost; best = l; bestAB = k == 0; bestIn = sIn; bestOut = sOut; bestOffIn = offIn;
                }
            }
            if (best == null) { waiting.Remove(droneId); return Join.None; }
            number = best.Number;
            ab = bestAB;
            var chosen = bestAB ? best.AB : best.BA;
            var ups = bestAB ? best.UpAB : best.UpBA;
            double cross = 2 * Math.Max(half.X, half.Y), length = 2 * half.Z;
            double lat;
            Join join;
            if (cross > HULL_MAX)
            {
                lat = BOX_WIDTH / 2 + cross / 2 + SIDE_MARGIN;   // beside the box, right of its way, outside it
                riders.Remove(droneId);
                join = Join.Beside;
                best.Trips++;
            }
            else
            {
                double w = Math.Min(BOX_WIDTH, SLOT_WIDTH * cross), clear = SLOT_CLEAR * length;
                Rider me;
                if (riders.TryGetValue(droneId, out me) && me.Lane == best.Number && me.AB == bestAB) lat = me.Lat;   // a replan on its way: the same slot
                else if (FreeSlot(best.Number, bestAB, droneId, bestIn, w, clear, out lat))
                {
                    riders[droneId] = new Rider { Lane = best.Number, AB = bestAB, Lat = lat, W = w, Clear = clear, S = bestIn, Frame = now };
                    best.Trips++;
                }
                else
                {
                    int since;
                    if (!waiting.TryGetValue(droneId, out since)) { since = now; waiting[droneId] = now; }
                    if (now - since < LANE_WAIT_MAX_TICKS)
                    {
                        if (since == now && Log.DebugEnabled) Log.Debug("Lanes: drone {0} waits at the entrance of {1}: no slot free", droneId, best.Name(bestAB));
                        name = best.Name(bestAB);
                        return Join.Wait;
                    }
                    waiting.Remove(droneId);
                    if (Log.DebugEnabled) Log.Debug("Lanes: drone {0}: no slot free in {1} for {2} s: not through it", droneId, best.Name(bestAB), LANE_WAIT_MAX_TICKS / 60);
                    return Join.None;
                }
                waiting.Remove(droneId);
                join = Join.Through;
            }
            // The slot's (or the side's) line between where it is joined and where it is left
            if (bestOffIn > JOIN_MIN) waypoints.Add(Across(chosen, ups, bestIn, lat));   // (already on it: from where it is)
            double acc = 0;
            for (int i = 0; i < chosen.Count; i++)
            {
                if (i > 0) acc += Vector3D.Distance(chosen[i - 1], chosen[i]);
                if (acc > bestIn + POINT_SLACK && acc < bestOut - POINT_SLACK) waypoints.Add(AcrossVertex(chosen, ups, i, lat));
            }
            waypoints.Add(Across(chosen, ups, bestOut, lat));
            best.LastUsed = NowMinutes;
            dirty = true;
            name = best.Name(bestAB);
            return join;
        }

        private const double MIN_RIDE = 100;      // m of lane at least between where it is joined and where it is left
        private const double POINT_SLACK = 5;     // m: a lane point this close to the join / leave point is left out
        private const double JOIN_MIN = 20;       // m off the lane at least for a join point (closer: straight to its next point)

        // The first slot from the box's left edge free at the entrance 's': no other drone's slot band overlaps it within
        // both their clearances along the box. 'lat': its centre across the box (+: right)
        private bool FreeSlot(int lane, bool ab, long droneId, double s, double w, double clear, out double lat)
        {
            for (lat = -BOX_WIDTH / 2 + w / 2; lat + w / 2 <= BOX_WIDTH / 2 + 1e-6; lat += w)
            {
                bool free = true;
                foreach (var kv in riders)
                {
                    var o = kv.Value;
                    if (kv.Key == droneId || o.Lane != lane || o.AB != ab) continue;
                    if (Math.Abs(o.Lat - lat) >= (o.W + w) / 2 || Math.Abs(o.S - s) >= o.Clear + clear) continue;
                    free = false;
                    break;
                }
                if (free) return true;
            }
            lat = 0;
            return false;
        }

        /// <summary>The drone stopped flying (arrived, docked, working): its slot is free.</summary>
        public void OnTripEnd(long droneId)
        {
            riders.Remove(droneId);
            waiting.Remove(droneId);
        }
        #endregion

        #region Traffic
        /// <summary>
        /// A flying drone's state, every few ticks: the speed it may fly at (double.MaxValue: no limit) so it can stop
        /// behind the drone ahead of it in its slot band, even if that one brakes at full thrust. 'brakeA': its own planned
        /// braking (m/s²); 'fullA': the most it can brake. Out of the box after being in it: its slot is free.
        /// </summary>
        public double Report(long droneId, long owner, Vector3D pos, Vector3D vel, double hull, double brakeA, double fullA, int now)
        {
            Rider me;
            if (!riders.TryGetValue(droneId, out me)) return double.MaxValue;
            Lane lane;
            if (!lanes.TryGetValue(me.Lane, out lane) || lane.Owner != owner) { riders.Remove(droneId); return double.MaxValue; }
            double s, lat, vert;
            ProjectBox(me.AB ? lane.AB : lane.BA, me.AB ? lane.UpAB : lane.UpBA, pos, out s, out lat, out vert);
            bool inside = s > 0.5 && s < lane.Length - 0.5 && Math.Abs(lat) <= BOX_WIDTH / 2 + 2 && Math.Abs(vert) <= BOX_HEIGHT / 2 + 2;
            if (!inside)
            {
                if (me.In) riders.Remove(droneId);   // through it
                return double.MaxValue;
            }
            me.In = true; me.S = s; me.V = vel.Length(); me.BrakeA = Math.Max(0.1, brakeA); me.FullA = Math.Max(me.BrakeA, fullA);
            me.Hull = hull; me.Frame = now; me.Pos = pos;
            lane.LastUsed = NowMinutes;
            // The leader: the nearest ahead in its slot band
            Rider leader = null;
            foreach (var kv in riders)
            {
                var o = kv.Value;
                if (kv.Key == droneId || !o.In || o.Lane != me.Lane || o.AB != me.AB || now - o.Frame > OCCUPANT_TICKS) continue;
                if (o.S <= me.S || Math.Abs(o.Lat - me.Lat) >= (o.W + me.W) / 2) continue;
                if (leader == null || o.S < leader.S) leader = o;
            }
            if (leader == null) return double.MaxValue;
            double margin = me.Hull + leader.Hull + Clearance;
            double gap = leader.S - me.S - margin;
            if (gap < 0)
            {
                // Really that close (hulls + clearance): a near miss. Otherwise level with each other along the lane - the
                // one behind drops back
                if (Vector3D.Distance(me.Pos, leader.Pos) < margin)
                {
                    Suspend(lane, "two drones came too close in it");
                    return 0;
                }
                return Math.Max(0, leader.V - LANE_DROP_BACK);
            }
            double leaderStop = leader.V * leader.V / (2 * leader.FullA);   // the leader braking as hard as it can
            return Math.Sqrt(Math.Max(0, 2 * me.BrakeA * (gap + leaderStop)));
        }

        /// <summary>The drone crashed: the lane it was in (null: none) is suspended; its name for the broadcast.</summary>
        public string Crashed(long droneId)
        {
            Rider o;
            if (!riders.TryGetValue(droneId, out o) || !o.In) return null;
            Lane l;
            if (!lanes.TryGetValue(o.Lane, out l)) return null;
            Suspend(l, "a drone crashed in it");
            riders.Remove(droneId);
            return l.Name(o.AB);
        }
        #endregion

        #region Obstructions, orders
        /// <summary>
        /// What a drone's route check found in a lane: 0 - not reported (a moving grid may get out of the way), 1 rock, 2 a
        /// tree, 3 a static grid.
        /// </summary>
        public static byte ObstacleKind(IMyEntity e)
        {
            if (e == null) return 0;
            if (e is MyVoxelBase) return 1;
            var g = e as IMyCubeGrid;
            if (g != null) return g.IsStatic ? (byte)3 : (byte)0;
            string type = e.GetType().Name;
            return type.Contains("Environment") || type.Contains("Tree") ? (byte)2 : (byte)0;
        }

        /// <summary>
        /// A drone found the stretch 'a' -> 'b' of lane 'number' ('ab': its way) blocked and flew 'detour' round it (its
        /// points, none if it found no way). The lane is redrawn: the same stretch moved up to REDRAW_OFFSET_MAX m across,
        /// up or down where both boxes are clear, else along the drone's way round. False: nothing changed.
        /// </summary>
        public bool Obstructed(long owner, int number, bool ab, Vector3D a, Vector3D b, List<Vector3D> detour, string what, PathfindingManager pm)
        {
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner || l.Centre.Count < 2 || pm == null) return false;
            // To the centre line (the drone flew its box: half the pair above / below it)
            Vector3D shift = UpAt((a + b) / 2, l.Up) * (ab ? DirOffset : -DirOffset);
            double sa, sb, off;
            Project(l.Centre, a - shift, out sa, out off);
            Project(l.Centre, b - shift, out sb, out off);
            if (sb < sa) { double t = sa; sa = sb; sb = t; }
            if (sb - sa < 1) return false;
            int seg;
            Vector3D pa = PointAt(l.Centre, sa, out seg), pb = PointAt(l.Centre, sb, out seg);
            // The lane points the moved stretch joins (kept by Replace)
            Vector3D before = pa, after = pb;
            bool hasBefore = false, hasAfter = false;
            double acc = 0;
            for (int i = 0; i < l.Centre.Count; i++)
            {
                if (i > 0) acc += Vector3D.Distance(l.Centre[i - 1], l.Centre[i]);
                if (acc < sa - 0.5) { before = l.Centre[i]; hasBefore = true; }
                else if (acc > sb + 0.5 && !hasAfter) { after = l.Centre[i]; hasAfter = true; }
            }
            // 1) a clear straight stretch near the old one
            pm.BeginPlan();
            Vector3D dir = Vector3D.Normalize(pb - pa), up = UpAt(pa, l.Up);
            Vector3D e1 = Right(dir, up), e2 = Vector3D.Cross(e1, dir);
            for (double d = REDRAW_STEP; d <= REDRAW_OFFSET_MAX + 1e-6; d += REDRAW_STEP)
                for (int k = 0; k < 8; k++)
                {
                    double ang = k * Math.PI / 4;
                    Vector3D o = (e1 * Math.Cos(ang) + e2 * Math.Sin(ang)) * d;
                    Vector3D qa = pa + o, qb = pb + o;
                    if (BoxesBlocked(pm, l, qa, qb) || (hasBefore && BoxesBlocked(pm, l, before, qa)) || (hasAfter && BoxesBlocked(pm, l, qb, after))) continue;
                    Replace(l, sa, sb, new List<Vector3D> { qa, qb });
                    if (Log.DebugEnabled) Log.Debug("Lanes: {0}: {1} in it, a stretch moved {2:F0} m clear of it", l.PairName, what, d);
                    return true;
                }
            // 2) the drone's way round it
            if (detour == null || detour.Count == 0)
            {
                if (Log.DebugEnabled) Log.Debug("Lanes: {0}: {1} in it, no clear stretch within {2:F0} m and no way round flown: kept", l.PairName, what, REDRAW_OFFSET_MAX);
                return false;
            }
            var pts = new List<Vector3D> { pa };
            for (int i = 0; i < detour.Count; i++) pts.Add(detour[i] - shift);
            pts.Add(pb);
            Replace(l, sa, sb, pts);
            if (Log.DebugEnabled) Log.Debug("Lanes: {0}: {1} in it, redrawn along a drone's way round ({2} point(s))", l.PairName, what, detour.Count);
            return true;
        }

        // Both boxes swept from 'a' to 'b' (centre line points) hit rock or a static / still grid
        private bool BoxesBlocked(PathfindingManager pm, Lane l, Vector3D a, Vector3D b)
        {
            Vector3D d = b - a;
            if (d.LengthSquared() < 1) return false;
            var q = new PathQuery
            {
                Controller = MatrixD.CreateWorld(a, Vector3D.Normalize(d), UpAt(a, l.Up)),
                LocalHull = new BoundingBoxD(new Vector3D(-BOX_WIDTH / 2, -(BOX_HEIGHT + BOX_GAP / 2), -1), new Vector3D(BOX_WIDTH / 2, BOX_HEIGHT + BOX_GAP / 2, 1)),
                OwnGrids = emptyGrids,
                Clearance = 0,
                HullCentred = true,
                SeeAll = true,   // the server's knowledge (rock, grids): a drone found it blocked, this is the way it looks
            };
            Vector3D hit;
            IMyEntity e;
            if (!pm.SegmentBlocked(ref q, a, b, 0, out hit, out e)) return false;
            var g = e as IMyCubeGrid;
            return !(g != null && !g.IsStatic && (g.Physics == null || g.Physics.LinearVelocity.LengthSquared() > 0.25));   // a ship passing by
        }
        private static readonly List<IMyCubeGrid> emptyGrids = new List<IMyCubeGrid>();

        // The centre line between 'sa' and 'sb' m along it replaced by 'pts'
        private void Replace(Lane l, double sa, double sb, List<Vector3D> pts)
        {
            var r = new List<Vector3D>();
            double acc = 0;
            for (int i = 0; i < l.Centre.Count; i++)
            {
                if (i > 0) acc += Vector3D.Distance(l.Centre[i - 1], l.Centre[i]);
                if (acc < sa - 0.5) r.Add(l.Centre[i]);
            }
            r.AddRange(pts);
            acc = 0;
            for (int i = 0; i < l.Centre.Count; i++)
            {
                if (i > 0) acc += Vector3D.Distance(l.Centre[i - 1], l.Centre[i]);
                if (acc > sb + 0.5) r.Add(l.Centre[i]);
            }
            l.Centre = r;
            Derive(l);
            dirty = true;
            listVersion++;
        }

        /// <summary>
        /// A scout job ended: its order's lane (if it was made for it) is kept once one of the order's jobs found ore, dropped
        /// when the last of them found nothing.
        /// </summary>
        public void OrderResult(long owner, uint jobId, bool found)
        {
            Lane hit = null;
            foreach (var l in lanes.Values)
                if (l.Owner == owner && l.Order && l.OrderJobs.Remove(jobId)) { hit = l; break; }
            if (hit == null) return;
            if (found) hit.OrderFound = true;
            dirty = true;
            if (hit.OrderJobs.Count > 0) return;
            if (hit.OrderFound)
            {
                hit.Order = false;   // a lane like any other now
                if (Log.DebugEnabled) Log.Debug("Lanes: {0} kept: its order found ore", hit.PairName);
                return;
            }
            lanes.Remove(hit.Number);
            listVersion++;
            if (Log.DebugEnabled) Log.Debug("Lanes: {0} dropped: its order found nothing", hit.PairName);
        }
        #endregion

        #region State, upkeep
        private void Suspend(Lane l, string why)
        {
            if (l.State == LaneState.Suspended) return;
            l.State = LaneState.Suspended;
            l.Reason = why;
            l.SuspendedAt = DateTime.UtcNow.Ticks;
            dirty = true;
            listVersion++;
            if (Log.DebugEnabled) Log.Debug("Lanes: {0} suspended: {1} (resumes in {2} min)", l.PairName, why, RESUME_MINUTES);
        }

        /// <summary>Every tick on the server.</summary>
        public void Update(int now)
        {
            if (now % 600 == 0) Expire();
            if (now % 60 == 0) Upkeep(now);
            if (dirty && now - lastSave > SAVE_EVERY_TICKS) Save(now);
        }

        // Suspended lanes resume on their own; slots given but not flown into (or not reported in a while) are free again
        private readonly List<long> riderScratch = new List<long>();
        private void Upkeep(int now)
        {
            long ticks = DateTime.UtcNow.Ticks, resume = (long)(RESUME_MINUTES * TimeSpan.TicksPerMinute);
            foreach (var l in lanes.Values)
            {
                if (l.State != LaneState.Suspended || ticks - l.SuspendedAt < resume) continue;
                l.State = LaneState.Active;
                l.Reason = "";
                dirty = true;
                listVersion++;
                if (Log.DebugEnabled) Log.Debug("Lanes: {0} resumed ({1} min after it was suspended)", l.PairName, RESUME_MINUTES);
            }
            riderScratch.Clear();
            foreach (var kv in riders)
                if (now - kv.Value.Frame > (kv.Value.In ? 10 * OCCUPANT_TICKS : RESERVATION_TICKS)) riderScratch.Add(kv.Key);
            for (int i = 0; i < riderScratch.Count; i++) riders.Remove(riderScratch[i]);
            riderScratch.Clear();
            foreach (var kv in waiting) if (now - kv.Value > 2 * LANE_WAIT_MAX_TICKS) riderScratch.Add(kv.Key);
            for (int i = 0; i < riderScratch.Count; i++) waiting.Remove(riderScratch[i]);
        }

        private void Expire()
        {
            long minute = NowMinutes;
            var gone = new List<int>();
            foreach (var l in lanes.Values)
                if (minute - l.LastUsed > config.FlightLaneExpirationMinutes) gone.Add(l.Number);
            for (int i = 0; i < gone.Count; i++)
            {
                if (Log.DebugEnabled) Log.Debug("Lanes: {0} expired (unused {1} min)", lanes[gone[i]].PairName, config.FlightLaneExpirationMinutes);
                lanes.Remove(gone[i]);
            }
            if (gone.Count > 0) { dirty = true; listVersion++; }
            var oldHolds = new List<string>();
            foreach (var kv in holds) if (kv.Value <= minute) oldHolds.Add(kv.Key);
            for (int i = 0; i < oldHolds.Count; i++) holds.Remove(oldHolds[i]);
        }
        #endregion

        #region Orchestrator list
        /// <summary>The owner's lanes for its terminal list: "number|text".</summary>
        public List<string> Describe(long owner)
        {
            var r = new List<string>();
            var list = new List<Lane>();
            foreach (var l in lanes.Values) if (l.Owner == owner) list.Add(l);
            list.Sort((x, y) => x.Number.CompareTo(y.Number));
            long minute = NowMinutes;
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                var sb = new StringBuilder();
                sb.Append(l.Number).Append('|').Append(l.PairName).Append(", ").Append((l.Length / 1000).ToString("0.0")).Append(" km, ")
                  .Append(l.Trips).Append(l.Trips == 1 ? " trip" : " trips").Append(", last ").Append(Math.Max(0, minute - l.LastUsed)).Append(" min ago");
                if (l.Order) sb.Append(" (order)");
                if (l.State == LaneState.Suspended) sb.Append(" (suspended: ").Append(l.Reason).Append(')');
                r.Add(sb.ToString());
            }
            return r;
        }

        /// <summary>
        /// A player deleted one lane: not traced again for FlightLaneExpirationMinutes ('hold'; Clear all holds nothing: the
        /// lanes are traced again with the next auctions).
        /// </summary>
        public bool Delete(long owner, int number, bool hold = true)
        {
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner) return false;
            lanes.Remove(number);
            if (hold) holds[HoldKey(owner, l.EndA, l.EndB)] = NowMinutes + (long)config.FlightLaneExpirationMinutes;
            dirty = true;
            listVersion++;
            if (Log.DebugEnabled) Log.Debug("Lanes: {0} deleted by a player{1}", l.PairName, hold ? " (not traced again for " + config.FlightLaneExpirationMinutes + " min)" : "");
            return true;
        }

        public bool Resume(long owner, int number)
        {
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner || l.State != LaneState.Suspended) return false;
            l.State = LaneState.Active;
            l.Reason = "";
            dirty = true;
            listVersion++;
            if (Log.DebugEnabled) Log.Debug("Lanes: {0} resumed by a player", l.PairName);
            return true;
        }

        public int ClearAll(long owner)
        {
            var gone = new List<int>();
            foreach (var l in lanes.Values) if (l.Owner == owner) gone.Add(l.Number);
            for (int i = 0; i < gone.Count; i++) Delete(owner, gone[i], false);
            // A fresh start: the owner's held pairs (lanes deleted one by one) may be traced again too
            string prefix = owner + "|";
            var held = new List<string>();
            foreach (var k in holds.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) held.Add(k);
            for (int i = 0; i < held.Count; i++) holds.Remove(held[i]);
            if (held.Count > 0) dirty = true;
            return gone.Count;
        }

        /// <summary>Lanes with a box line (either way) within 'r' of 'c', any owner (the debug drawing): into 'found'.</summary>
        public void Near(Vector3D c, double r, List<Lane> found)
        {
            foreach (var l in lanes.Values)
                if (LineWithin(l.AB, c, r + BOX_WIDTH) || LineWithin(l.BA, c, r + BOX_WIDTH)) found.Add(l);
        }

        private static bool LineWithin(List<Vector3D> line, Vector3D c, double r)
        {
            double r2 = r * r;
            if (line.Count == 1) return Vector3D.DistanceSquared(line[0], c) <= r2;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector3D a = line[i], ab = line[i + 1] - a;
                double len2 = ab.LengthSquared();
                double t = len2 > 1e-6 ? MathHelperD.Clamp(Vector3D.Dot(c - a, ab) / len2, 0, 1) : 0;
                if (Vector3D.DistanceSquared(a + ab * t, c) <= r2) return true;
            }
            return false;
        }

        /// <summary>A lane pair's two directed box lines and their "up"s (for the drawing). False: no such lane of 'owner'.</summary>
        public bool Geometry(long owner, int number, List<Vector3D> ab, List<Vector3D> ba, List<Vector3D> upAB, List<Vector3D> upBA,
                             out bool suspended, out string nameAB, out string nameBA)
        {
            suspended = false; nameAB = nameBA = null;
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner) return false;
            ab.AddRange(l.AB);
            ba.AddRange(l.BA);
            upAB.AddRange(l.UpAB);
            upBA.AddRange(l.UpBA);
            suspended = l.State != LaneState.Active;
            nameAB = l.Name(true);
            nameBA = l.Name(false);
            return true;
        }
        #endregion

        #region Persistence
        public void Save(int now)
        {
            lastSave = now;
            try
            {
                using (BinaryWriter w = MyAPIGateway.Utilities.WriteBinaryFileInWorldStorage(FILE_NAME, typeof(FlightLanes)))
                {
                    w.Write(FILE_VERSION);
                    w.Write(nextNumber);
                    w.Write(lanes.Count);
                    foreach (var l in lanes.Values)
                    {
                        w.Write(l.Number); w.Write(l.Owner); w.Write(l.EndA); w.Write(l.EndB);
                        w.Write(l.NameA ?? ""); w.Write(l.NameB ?? "");
                        w.Write(l.Up.X); w.Write(l.Up.Y); w.Write(l.Up.Z);
                        w.Write(l.Trips); w.Write(l.LastUsed); w.Write(l.Created);
                        w.Write((byte)l.State); w.Write(l.Reason ?? ""); w.Write(l.SuspendedAt);
                        w.Write(l.Order); w.Write(l.OrderFound);
                        w.Write(l.OrderJobs.Count);
                        for (int i = 0; i < l.OrderJobs.Count; i++) w.Write(l.OrderJobs[i]);
                        w.Write(l.Centre.Count);
                        for (int i = 0; i < l.Centre.Count; i++) { w.Write(l.Centre[i].X); w.Write(l.Centre[i].Y); w.Write(l.Centre[i].Z); }
                    }
                    w.Write(holds.Count);
                    foreach (var kv in holds) { w.Write(kv.Key); w.Write(kv.Value); }
                }
                dirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("Lanes: save failed: {0}", ex.Message);
            }
        }

        public void Load(int now)
        {
            lastSave = now;
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(FILE_NAME, typeof(FlightLanes))) return;
                using (BinaryReader r = MyAPIGateway.Utilities.ReadBinaryFileInWorldStorage(FILE_NAME, typeof(FlightLanes)))
                {
                    int version = r.ReadInt32();
                    if (version != 1 && version != FILE_VERSION) return;
                    nextNumber = r.ReadInt32();
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        var l = new Lane { Number = r.ReadInt32(), Owner = r.ReadInt64(), EndA = r.ReadInt64(), EndB = r.ReadInt64(), NameA = r.ReadString(), NameB = r.ReadString() };
                        l.Up = new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                        if (version == 1) { r.ReadSingle(); r.ReadSingle(); }   // (hull, spacing of a flown lane)
                        l.Trips = r.ReadInt32(); l.LastUsed = r.ReadInt64(); l.Created = r.ReadInt64();
                        l.State = (LaneState)r.ReadByte(); l.Reason = r.ReadString();
                        if (version >= 2)
                        {
                            l.SuspendedAt = r.ReadInt64();
                            l.Order = r.ReadBoolean(); l.OrderFound = r.ReadBoolean();
                            int jobs = r.ReadInt32();
                            for (int i = 0; i < jobs; i++) l.OrderJobs.Add(r.ReadUInt32());
                        }
                        if (l.State != LaneState.Suspended) l.State = LaneState.Active;   // (version 1: pending, checked at idle)
                        else if (l.SuspendedAt == 0) l.SuspendedAt = DateTime.UtcNow.Ticks;
                        int c = r.ReadInt32();
                        for (int i = 0; i < c; i++) l.Centre.Add(new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
                        if (l.Centre.Count < 2) continue;
                        Derive(l);
                        lanes[l.Number] = l;
                    }
                    int h = r.ReadInt32();
                    for (int i = 0; i < h; i++) holds[r.ReadString()] = r.ReadInt64();
                }
                listVersion++;
                if (Log.DebugEnabled) Log.Debug("Lanes: {0} lane pair(s) loaded", lanes.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Lanes: load failed ({0}): starting without lanes", ex.Message);
                lanes.Clear();
            }
        }
        #endregion
    }
}
