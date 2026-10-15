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
    /// Directional flight lanes (TODO/20, section 6). Server only, one instance (PathfindingManager.Lanes).
    ///  - Made after the first flight between two places (a static base with a Logistics Computer, an asteroid, or an
    ///    ore hotspot on a planet: known deposits within HOTSPOT_RADIUS of each other): the
    ///    flown path is the centre line, trimmed at the ends (above the work area, short of the base); two directed
    ///    lanes, each offset to the right of its own way by the lane spacing (largest hull + clearance + 5 m). "Up":
    ///    gravity, else the base grid's up.
    ///  - No full knowledge: the offset tubes are checked only where the first flight's view trail (its sensors) covered
    ///    them, and those checks run when the pathfinder is idle. Uncovered or failed: not made / suspended.
    ///  - Crossings: a new lane near an existing one is lifted (largest hull + spacing) along up there, with ramps.
    ///  - Planning: a drone of the owning network whose start and goal are near a lane's ends flies through it (the lane
    ///    points become the route's preferred waypoints).
    ///  - Same-direction traffic: drones report their position along a lane; a follower's speed is capped so it can stop
    ///    behind its leader even if the leader brakes at full thrust.
    ///  - Incidents (a crash or loss in the lane, a near miss, a drone holding in it, a check failing twice) suspend the
    ///    lane until resumed in the Orchestrator's list; unused lanes expire (FlightLaneExpirationMinutes).
    ///  - Busy routes in general: trips whose ends aren't such places (a build site, a point in space) are counted per
    ///    pair of ends (the nearest static grid, else the spot); BUSY_TRIPS of them within BUSY_WINDOW_MINUTES make a lane
    ///    from the latest one, the same way.
    ///  - Names: "L7 Home Base → Asteroid 4821". Owned by the orchestrator whose drone made it. Saved with the world.
    /// </summary>
    public sealed class FlightLanes
    {
        public enum LaneState : byte { Pending = 0, Active = 1, Suspended = 2 }

        private const double EXTRA_SPACING = 5;          // m on top of hull + clearance
        private const double BASE_RANGE = 600;           // m: a trip end this close to a base belongs to it
        private const double ASTEROID_RANGE = 400;       // m beyond an asteroid's bounding sphere
        private const double BASE_MARGIN = 60;           // m: lanes stop this far outside a base's bounding sphere
        private const double WORK_MARGIN = 40;           // m: lanes end this far before the work point
        private const double MIN_LENGTH = 300;           // m of lane, at least
        private const double ENTRY_RANGE = 1000;         // m from start / goal to the lane's ends, at most
        private const double DETOUR_RATIO = 1.3;         // the way through a lane: at most this much longer than straight
        private const double CROSS_RAMP = 60;            // m each side of a crossing
        private const int OCCUPANT_TICKS = 60 * 3;       // a report this old: no longer on the lane
        private const int HOLD_TICKS = 60 * 30;          // holding still in a lane this long: incident
        private const int RECHECK_TICKS = 60 * 60 * 30;  // active lanes checked again (at idle) this often
        private const int LIST_EVERY_TICKS = 60 * 5;
        private const int SAVE_EVERY_TICKS = 60 * 60;
        private const double HOTSPOT_RADIUS = 500;       // m: deposits this close to each other are one hotspot (planets)
        private const double HOTSPOT_RANGE = 400;        // m beyond a hotspot's deposits: a trip end there belongs to it
        private const double HOTSPOT_CELL = 500;         // m: a hotspot's id comes from its centre on this grid
        private const double POINT_CELL = 200;           // m: a trip end in open space: its id comes from this grid
        private const int BUSY_TRIPS = 6;                // trips between the same two ends...
        private const long BUSY_WINDOW_MINUTES = 30;     // ...within this: a lane
        private const int FILE_VERSION = 1;
        private const string FILE_NAME = "Automata_Lanes.bin";

        public sealed class Lane
        {
            public int Number;
            public long Owner;                 // orchestrator entity id
            public long EndA, EndB;            // place entity ids
            public string NameA, NameB;
            public List<Vector3D> Centre = new List<Vector3D>();   // world, A to B
            public Vector3D Up;
            public float Hull;                 // largest hull radius that flew it
            public float Spacing;
            public int Trips;
            public long LastUsed, Created;     // UTC minutes
            public LaneState State;
            public string Reason = "";
            public int FailedChecks;
            public int CheckIndex;             // next tube segment to check (both directions: 2 x segments)
            public int NextCheckFrame;
            // derived
            public List<Vector3D> AB = new List<Vector3D>(), BA = new List<Vector3D>();
            public double Length;
            public string Name(bool ab) { return "L" + Number + " " + (ab ? NameA + " → " + NameB : NameB + " → " + NameA); }
            public string PairName { get { return "L" + Number + " " + NameA + " ⇄ " + NameB; } }
        }

        private enum PlaceKind : byte { Base, Asteroid, Hotspot, Grid, Point }

        // A lane end: a base (static grid with a Logistics Computer), an asteroid, a planet ore hotspot; for busy routes in
        // general also any static grid, or a spot in space
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

        private sealed class Trip
        {
            public readonly List<Vector3D> Points = new List<Vector3D>();
            public readonly List<bool> Sensed = new List<bool>();
            public double Hull;
            public long Owner;
        }

        private sealed class Occupant
        {
            public int Lane;
            public bool AB;
            public double S, V, BrakeA, FullA, Hull;
            public int Frame, StillSince;
            public Vector3D Pos;
        }

        private readonly Dictionary<int, Lane> lanes = new Dictionary<int, Lane>();
        private readonly Dictionary<long, Trip> trips = new Dictionary<long, Trip>();
        private readonly Dictionary<long, Occupant> occupants = new Dictionary<long, Occupant>();
        private readonly Dictionary<long, string> lastLaneOf = new Dictionary<long, string>();
        private readonly Dictionary<string, long> holds = new Dictionary<string, long>();   // "owner|a|b" -> UTC minute the hold ends
        private readonly Dictionary<string, List<long>> busy = new Dictionary<string, List<long>>();   // "owner|a|b" -> trip minutes (busy routes)
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

        #region Trips (lanes are made from flights)
        /// <summary>A flying drone's hull centre, every few ticks (RecordFlight10). 'sensed': it sees all around (sensor).</summary>
        public void OnRecord(long droneId, long owner, Vector3D pos, double hull, bool sensed)
        {
            Trip t;
            if (!trips.TryGetValue(droneId, out t)) { t = new Trip { Owner = owner }; trips[droneId] = t; }
            if (t.Points.Count > 0 && Vector3D.DistanceSquared(t.Points[t.Points.Count - 1], pos) < 20 * 20) return;
            t.Points.Add(pos);
            t.Sensed.Add(sensed);
            if (hull > t.Hull) t.Hull = hull;
            if (owner != 0) t.Owner = owner;
            if (t.Points.Count > 2000) { t.Points.RemoveAt(0); t.Sensed.RemoveAt(0); }
        }

        /// <summary>The drone stopped flying (arrived, docked, working): the trip may make a lane.</summary>
        public void OnTripEnd(long droneId)
        {
            Trip t;
            if (!trips.TryGetValue(droneId, out t)) return;
            trips.Remove(droneId);
            occupants.Remove(droneId);
            if (t.Owner == 0 || t.Points.Count < 3) return;
            try { FromTrip(t); }
            catch (Exception ex) { Log.Error("Lanes: trip of drone {0} not used: {1}", droneId, ex.Message); }
        }

        private void FromTrip(Trip t)
        {
            Place pa, pb;
            bool ka = FindPlace(t.Points[0], out pa), kb = FindPlace(t.Points[t.Points.Count - 1], out pb);
            bool expected = ka && kb && pa.Id != pb.Id && !(pa.Natural && pb.Natural);   // base <-> base, base <-> asteroid / hotspot
            if (!expected)
            {
                // A busy route in general: the ends that aren't places of their own, the nearest static grid or the spot
                if (!ka) pa = GenericEnd(t.Points[0]);
                if (!kb) pb = GenericEnd(t.Points[t.Points.Count - 1]);
                if (pa.Id == pb.Id || (pa.Natural && pb.Natural)) return;
            }
            // Existing pair (either way round): a trip on it
            foreach (var l in lanes.Values)
            {
                if (l.Owner != t.Owner || !((l.EndA == pa.Id && l.EndB == pb.Id) || (l.EndA == pb.Id && l.EndB == pa.Id))) continue;
                l.Trips++;
                l.LastUsed = NowMinutes;
                if (t.Hull > l.Hull) l.Hull = (float)t.Hull;
                dirty = true;
                listVersion++;
                return;
            }
            string key = HoldKey(t.Owner, pa.Id, pb.Id);
            long hold;
            if (holds.TryGetValue(key, out hold) && hold > NowMinutes) return;
            if (!expected && !BusyEnough(key)) return;
            MakeLane(t, pa, pb, expected);
        }

        // A trip between two ends that aren't places: counted; true once BUSY_TRIPS were flown within BUSY_WINDOW_MINUTES
        private bool BusyEnough(string key)
        {
            List<long> list;
            if (!busy.TryGetValue(key, out list)) { list = new List<long>(); busy[key] = list; }
            long now = NowMinutes;
            list.Add(now);
            list.RemoveAll(m => now - m > BUSY_WINDOW_MINUTES);
            if (list.Count < BUSY_TRIPS) return false;
            busy.Remove(key);
            return true;
        }

        private void MakeLane(Trip t, Place pa, Place pb, bool expected)
        {
            // The centre line: the flown points, trimmed at both ends
            var pts = new List<Vector3D>(t.Points);
            var sensed = new List<bool>(t.Sensed);
            TrimEnd(pts, sensed, pa, true);
            TrimEnd(pts, sensed, pb, false);
            if (pts.Count < 2) return;
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += Vector3D.Distance(pts[i - 1], pts[i]);
            if (len < MIN_LENGTH) return;
            var lane = new Lane
            {
                Number = nextNumber++, Owner = t.Owner, EndA = pa.Id, EndB = pb.Id, NameA = pa.Name, NameB = pb.Name, Hull = (float)t.Hull,
                Spacing = (float)(t.Hull + Clearance + EXTRA_SPACING), Trips = 1, LastUsed = NowMinutes, Created = NowMinutes,
                State = LaneState.Pending,
            };
            lane.Centre = Simplify(pts);
            lane.Up = UpFor(lane.Centre[lane.Centre.Count / 2], pa.Kind == PlaceKind.Base || pa.Kind == PlaceKind.Grid ? pa.Id : pb.Id);
            AvoidCrossings(lane);
            Derive(lane);
            // No full knowledge: both tubes must lie where the flight's sensors saw
            if (!Covered(lane.AB, pts, sensed) || !Covered(lane.BA, pts, sensed))
            {
                if (Log.DebugEnabled) Log.Debug("Lanes: {0} not made: the flight's sensors didn't cover its tubes (a later trip may)", lane.PairName);
                nextNumber--;
                return;
            }
            lanes[lane.Number] = lane;
            dirty = true;
            listVersion++;
            Log.Info("Lanes: {0} made from {1} ({2:F0} m, spacing {3:F1} m, hull {4:F1} m); tube checks queued", lane.PairName,
                     expected ? "a flight" : BUSY_TRIPS + " trips within " + BUSY_WINDOW_MINUTES + " min", lane.Length, lane.Spacing, lane.Hull);
        }

        // Trim an end of the flown line: short of a base (outside its sphere plus a margin), or above a work point at an
        // asteroid (back from it by WORK_MARGIN, and out of the rock)
        private static void TrimEnd(List<Vector3D> pts, List<bool> sensed, Place place, bool start)
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
                sensed.RemoveAt(i);
            }
        }

        // Points closer than a few metres to the line between their neighbours go (the flown line is wobbly)
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

        // The places lanes join: a static grid with a Logistics Computer, an asteroid, or (on a planet) an ore hotspot
        private bool FindPlace(Vector3D p, out Place place)
        {
            place = default(Place);
            var area = new BoundingSphereD(p, BASE_RANGE + ASTEROID_RANGE + 2000);
            var found = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref area);
            double best = double.MaxValue;
            for (int i = 0; i < found.Count; i++)
            {
                var e = found[i];
                var grid = e as IMyCubeGrid;
                if (grid != null)
                {
                    if (!grid.IsStatic) continue;
                    var s = grid.WorldVolume;
                    double d = Math.Max(0, Vector3D.Distance(p, s.Center) - s.Radius);
                    if (d > BASE_RANGE || d >= best || !HasLogisticsComputer(grid)) continue;
                    best = d;
                    place = new Place { Id = grid.EntityId, Name = Short(grid.DisplayName), Sphere = s, Kind = PlaceKind.Base };
                    continue;
                }
                var v = e as MyVoxelBase;
                if (v != null && PathfindingManager.IsAsteroid(v))
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
                Id = SyntheticId(1, planet.EntityId, centre, HOTSPOT_CELL), Name = ore + " hotspot",
                Sphere = new BoundingSphereD(centre, radius + 50), Kind = PlaceKind.Hotspot, Outward = planet.PositionComp.GetPosition(),
            };
            return true;
        }

        // A busy route's end that isn't a place: the nearest static grid within BASE_RANGE, else the spot itself
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
            return new Place { Id = id, Name = "Point " + (Math.Abs(id) % 10000).ToString("0000"), Sphere = new BoundingSphereD(p, 0), Kind = PlaceKind.Point, Outward = p };
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
        private static bool HasLogisticsComputer(IMyCubeGrid grid)
        {
            slimScratch.Clear();
            grid.GetBlocks(slimScratch, s => s.FatBlock != null && s.FatBlock.GameLogic != null
                                            && s.FatBlock.GameLogic.GetAs<Logistics.LogisticsComputerBlock>() != null);
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
        // The two directed lanes from the centre line: each offset to the right of its own direction by the spacing
        private static void Derive(Lane l)
        {
            l.AB = Offset(l.Centre, l.Up, l.Spacing, false);
            l.BA = Offset(l.Centre, l.Up, l.Spacing, true);
            l.Length = 0;
            for (int i = 1; i < l.Centre.Count; i++) l.Length += Vector3D.Distance(l.Centre[i - 1], l.Centre[i]);
        }

        private static List<Vector3D> Offset(List<Vector3D> c, Vector3D up, double off, bool reverse)
        {
            int n = c.Count;
            var r = new List<Vector3D>(n);
            for (int k = 0; k < n; k++)
            {
                int i = reverse ? n - 1 - k : k;
                int prev = reverse ? Math.Min(n - 1, i + 1) : Math.Max(0, i - 1);
                int next = reverse ? Math.Max(0, i - 1) : Math.Min(n - 1, i + 1);
                Vector3D dir = c[next] - c[prev];
                if (dir.LengthSquared() < 1e-6) dir = Vector3D.Forward;
                dir.Normalize();
                Vector3D right = Vector3D.Cross(dir, up);
                if (right.LengthSquared() < 1e-6) right = Vector3D.CalculatePerpendicularVector(dir);
                right.Normalize();
                r.Add(c[i] + right * off);
            }
            return r;
        }

        // A new lane near an existing one is lifted along its up there (with ramps), by largest hull + spacing
        private void AvoidCrossings(Lane l)
        {
            Derive(l);
            foreach (var o in lanes.Values)
            {
                if (o.State == LaneState.Suspended) continue;
                double need = 2 * Math.Max(l.Spacing, o.Spacing) + Math.Max(l.Hull, o.Hull);
                for (int i = 0; i + 1 < l.Centre.Count; i++)
                    for (int j = 0; j + 1 < o.Centre.Count; j++)
                    {
                        Vector3D pa, pb;
                        if (SegmentDistance(l.Centre[i], l.Centre[i + 1], o.Centre[j], o.Centre[j + 1], out pa, out pb) > need) continue;
                        Lift(l, pa, Math.Max(l.Hull, o.Hull) + need);
                        if (Log.DebugEnabled) Log.Debug("Lanes: new lane lifted {0:F0} m over L{1} where they cross", Math.Max(l.Hull, o.Hull) + need, o.Number);
                        Derive(l);
                        return;
                    }
            }
        }

        private static void Lift(Lane l, Vector3D at, double by)
        {
            // Insert points so the ramp has room: every centre point within CROSS_RAMP x 2 moves, fully within CROSS_RAMP
            for (int i = 0; i < l.Centre.Count; i++)
            {
                double d = Vector3D.Distance(l.Centre[i], at);
                double w = d <= CROSS_RAMP ? 1 : d >= 2 * CROSS_RAMP ? 0 : 1 - (d - CROSS_RAMP) / CROSS_RAMP;
                l.Centre[i] += l.Up * (by * w);
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

        // Every tube point within the sensor range of a trip point that had sensors on
        private bool Covered(List<Vector3D> tube, List<Vector3D> trail, List<bool> sensed)
        {
            double r2 = config.SimulatedSensorRange * config.SimulatedSensorRange;
            bool all = !config.RequireSensorsForPathfinding();
            for (int i = 0; i < tube.Count; i++)
            {
                bool ok = false;
                for (int k = 0; k < trail.Count && !ok; k++)
                    if ((all || sensed[k]) && Vector3D.DistanceSquared(tube[i], trail[k]) <= r2) ok = true;
                if (!ok) return false;
            }
            return true;
        }

        // Position along a directed lane: distance along it and off it (nearest segment)
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
        #endregion

        #region Planning
        /// <summary>
        /// A lane of 'owner' from near 'start' to near 'goal' for a hull of 'hull': its points (directed, hull centres)
        /// appended to 'waypoints', its name in 'name'. False: none fits.
        /// </summary>
        public bool FindLane(long owner, Vector3D start, Vector3D goal, double hull, List<Vector3D> waypoints, out string name)
        {
            name = null;
            if (owner == 0) return false;
            double direct = Vector3D.Distance(start, goal);
            Lane best = null;
            bool bestAB = true;
            double bestCost = double.MaxValue;
            foreach (var l in lanes.Values)
            {
                if (l.Owner != owner || l.State != LaneState.Active || l.AB.Count < 2 || hull > l.Hull + 0.5) continue;
                for (int k = 0; k < 2; k++)
                {
                    var line = k == 0 ? l.AB : l.BA;
                    Vector3D entry = line[0], exit = line[line.Count - 1];
                    double din = Vector3D.Distance(start, entry), dout = Vector3D.Distance(exit, goal);
                    if (din > ENTRY_RANGE || dout > ENTRY_RANGE) continue;
                    double cost = din + l.Length + dout;
                    if (cost > direct * DETOUR_RATIO + 200 || cost >= bestCost) continue;
                    bestCost = cost; best = l; bestAB = k == 0;
                }
            }
            if (best == null) return false;
            waypoints.AddRange(bestAB ? best.AB : best.BA);
            name = best.Name(bestAB);
            return true;
        }

        /// <summary>A lane stretch the planner found blocked (it went round): counts as a failed check.</summary>
        public void StretchBlocked(string laneName)
        {
            foreach (var l in lanes.Values)
                if (laneName != null && (laneName == l.Name(true) || laneName == l.Name(false))) { FailCheck(l, "a stretch found blocked when planning"); return; }
        }
        #endregion

        #region Traffic
        /// <summary>
        /// A flying drone's state, every few ticks: the speed it may fly at (double.MaxValue: no limit) so it can stop
        /// behind the drone ahead of it in the same lane, even if that one brakes at full thrust. 'brakeA': its own
        /// planned braking (m/s²); 'fullA': the most it can brake.
        /// </summary>
        public double Report(long droneId, long owner, Vector3D pos, Vector3D vel, double hull, double brakeA, double fullA, int now)
        {
            Occupant me;
            occupants.TryGetValue(droneId, out me);
            Lane lane = null;
            bool ab = true;
            double s = 0;
            // In a lane tube, flying its way?
            foreach (var l in lanes.Values)
            {
                if (l.Owner != owner || l.State == LaneState.Suspended || l.AB.Count < 2) continue;
                for (int k = 0; k < 2; k++)
                {
                    var line = k == 0 ? l.AB : l.BA;
                    double si, off;
                    Project(line, pos, out si, out off);
                    if (off > l.Spacing * 0.6 || si <= 0.5 || si >= l.Length - 0.5) continue;
                    Vector3D dir = line[line.Count - 1] - line[0];
                    if (vel.LengthSquared() > 1 && Vector3D.Dot(vel, dir) < 0) continue;
                    lane = l; ab = k == 0; s = si;
                    break;
                }
                if (lane != null) break;
            }
            if (lane == null) { occupants.Remove(droneId); return double.MaxValue; }
            double v = vel.Length();
            if (me == null || me.Lane != lane.Number || me.AB != ab) { me = new Occupant { Lane = lane.Number, AB = ab, StillSince = now }; occupants[droneId] = me; }
            me.S = s; me.V = v; me.BrakeA = Math.Max(0.1, brakeA); me.FullA = Math.Max(me.BrakeA, fullA); me.Hull = hull; me.Frame = now; me.Pos = pos;
            if (v > 0.5) me.StillSince = now;
            lastLaneOf[droneId] = lane.Name(ab);
            lane.LastUsed = NowMinutes;
            // Holding still inside the lane (not at its ends): an incident
            if (now - me.StillSince > HOLD_TICKS && s > 50 && s < lane.Length - 50)
            {
                Suspend(lane, "a drone held position in it");
                return double.MaxValue;
            }
            // The leader: the nearest ahead on the same lane and way
            Occupant leader = null;
            foreach (var kv in occupants)
            {
                var o = kv.Value;
                if (kv.Key == droneId || o.Lane != me.Lane || o.AB != me.AB || now - o.Frame > OCCUPANT_TICKS) continue;
                if (o.S <= me.S) continue;
                if (leader == null || o.S < leader.S) leader = o;
            }
            if (leader == null) return double.MaxValue;
            double margin = me.Hull + leader.Hull + Clearance;
            double gap = leader.S - me.S - margin;
            if (gap < 0)
            {
                Suspend(lane, "two drones came too close in it");
                return 0;
            }
            double leaderStop = leader.V * leader.V / (2 * leader.FullA);   // the leader braking as hard as it can
            double cap = Math.Sqrt(Math.Max(0, 2 * me.BrakeA * (gap + leaderStop)));
            return cap;
        }

        /// <summary>The drone crashed: the lane it was in (null: none) is suspended; its name for the broadcast.</summary>
        public string Crashed(long droneId)
        {
            Occupant o;
            if (!occupants.TryGetValue(droneId, out o)) return null;
            Lane l;
            if (!lanes.TryGetValue(o.Lane, out l)) return null;
            string name = l.Name(o.AB);
            Suspend(l, "a drone crashed in it");
            occupants.Remove(droneId);
            return name;
        }

        /// <summary>
        /// The lane a silent drone was last flying (DroneWatch: "lost in lane"), null: none recently. The lane is suspended.
        /// </summary>
        public string Lost(long droneId, int now)
        {
            Occupant o;
            if (!occupants.TryGetValue(droneId, out o) || now - o.Frame > DroneWatchWindow) return null;
            Lane l;
            if (!lanes.TryGetValue(o.Lane, out l)) return null;
            Suspend(l, "a drone was lost in it");
            occupants.Remove(droneId);
            return l.Name(o.AB);
        }
        private const int DroneWatchWindow = 60 * 90;
        #endregion

        #region State, upkeep, idle checks
        private void Suspend(Lane l, string why)
        {
            if (l.State == LaneState.Suspended) return;
            l.State = LaneState.Suspended;
            l.Reason = why;
            dirty = true;
            listVersion++;
            Log.Info("Lanes: {0} suspended: {1}", l.PairName, why);
        }

        private void FailCheck(Lane l, string why)
        {
            l.FailedChecks++;
            if (l.FailedChecks >= 2) Suspend(l, "a check failed twice (" + why + ")");
            dirty = true;
        }

        /// <summary>Every tick on the server. 'idle': the pathfinder planned nothing this tick and the simulation keeps up.</summary>
        public void Update(PathfindingManager pm, int now, bool idle)
        {
            if (now % 600 == 0) Expire();
            if (idle) IdleCheck(pm, now);
            if (dirty && now - lastSave > SAVE_EVERY_TICKS) Save(now);
        }

        private void Expire()
        {
            long minute = NowMinutes;
            var gone = new List<int>();
            foreach (var l in lanes.Values)
                if (minute - l.LastUsed > config.FlightLaneExpirationMinutes) gone.Add(l.Number);
            for (int i = 0; i < gone.Count; i++)
            {
                Log.Info("Lanes: {0} expired (unused {1} min)", lanes[gone[i]].PairName, config.FlightLaneExpirationMinutes);
                lanes.Remove(gone[i]);
            }
            if (gone.Count > 0) { dirty = true; listVersion++; }
            var oldHolds = new List<string>();
            foreach (var kv in holds) if (kv.Value <= minute) oldHolds.Add(kv.Key);
            for (int i = 0; i < oldHolds.Count; i++) holds.Remove(oldHolds[i]);
        }

        // One tube segment per idle tick: pending lanes become active once both tubes are clear; active ones are checked
        // again now and then
        private void IdleCheck(PathfindingManager pm, int now)
        {
            Lane l = null;
            foreach (var x in lanes.Values)
            {
                if (x.State == LaneState.Suspended || x.NextCheckFrame > now) continue;
                l = x;
                break;
            }
            if (l == null) return;
            int segs = l.AB.Count - 1;
            int k = l.CheckIndex;
            var line = k < segs ? l.AB : l.BA;
            int i = k < segs ? k : k - segs;
            if (i + 1 < line.Count)
            {
                Vector3D a = line[i], b = line[i + 1];
                Vector3D dir = b - a;
                if (dir.LengthSquared() > 1e-6)
                {
                    var q = new PathQuery
                    {
                        Controller = MatrixD.CreateWorld(a, Vector3D.Normalize(dir), Vector3D.CalculatePerpendicularVector(Vector3D.Normalize(dir))),
                        LocalHull = new BoundingBoxD(new Vector3D(-l.Hull * 0.577), new Vector3D(l.Hull * 0.577)),
                        OwnGrids = emptyGrids,
                        Clearance = Clearance,
                        HullCentred = true,
                        SeeAll = true,   // covered by the first flight's sensors (FromTrip)
                    };
                    Vector3D hit;
                    IMyEntity e;
                    if (pm.SegmentBlocked(ref q, a, b, 0, out hit, out e))
                    {
                        // A ship or a drone passing through: not the lane's fault, looked at again later
                        var g = e as IMyCubeGrid;
                        if (g != null && !g.IsStatic && (g.Physics == null || g.Physics.LinearVelocity.LengthSquared() > 0.25 || g.GridSizeEnum == VRage.Game.MyCubeSize.Small))
                        {
                            l.NextCheckFrame = now + 60 * 30;
                            return;
                        }
                        if (Log.DebugEnabled) Log.Debug("Lanes: {0}: tube segment {1} blocked by {2}", l.PairName, k + 1, e != null ? e.DisplayName : "?");
                        FailCheck(l, "tube blocked by " + (e != null ? e.DisplayName : "something"));
                        l.CheckIndex = 0;
                        l.NextCheckFrame = now + 60 * 60;   // again in a minute (a ship passing by)
                        return;
                    }
                }
            }
            l.CheckIndex++;
            if (l.CheckIndex >= 2 * segs)
            {
                l.CheckIndex = 0;
                l.NextCheckFrame = now + RECHECK_TICKS;
                if (l.State == LaneState.Pending)
                {
                    l.State = LaneState.Active;
                    l.FailedChecks = 0;
                    listVersion++;
                    Log.Info("Lanes: {0} active", l.PairName);
                }
                dirty = true;
            }
        }
        private static readonly List<IMyCubeGrid> emptyGrids = new List<IMyCubeGrid>();
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
                if (l.State == LaneState.Pending) sb.Append(" (checks queued)");
                else if (l.State == LaneState.Suspended) sb.Append(" (suspended: ").Append(l.Reason).Append(')');
                r.Add(sb.ToString());
            }
            return r;
        }

        public bool Delete(long owner, int number)
        {
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner) return false;
            lanes.Remove(number);
            holds[HoldKey(owner, l.EndA, l.EndB)] = NowMinutes + (long)config.FlightLaneExpirationMinutes;
            dirty = true;
            listVersion++;
            Log.Info("Lanes: {0} deleted by a player (not made again for {1} min)", l.PairName, config.FlightLaneExpirationMinutes);
            return true;
        }

        public bool Resume(long owner, int number)
        {
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner || l.State != LaneState.Suspended) return false;
            l.State = LaneState.Pending;   // checked again before use
            l.Reason = "";
            l.FailedChecks = 0;
            l.CheckIndex = 0;
            l.NextCheckFrame = 0;
            dirty = true;
            listVersion++;
            Log.Info("Lanes: {0} resumed by a player (checked again first)", l.PairName);
            return true;
        }

        public int ClearAll(long owner)
        {
            var gone = new List<int>();
            foreach (var l in lanes.Values) if (l.Owner == owner) gone.Add(l.Number);
            for (int i = 0; i < gone.Count; i++) Delete(owner, gone[i]);
            return gone.Count;
        }

        /// <summary>A lane pair's two directed lines (for the drawing). False: no such lane of 'owner'.</summary>
        public bool Geometry(long owner, int number, List<Vector3D> ab, List<Vector3D> ba, out bool pending, out string nameAB, out string nameBA)
        {
            pending = false; nameAB = nameBA = null;
            Lane l;
            if (!lanes.TryGetValue(number, out l) || l.Owner != owner) return false;
            ab.AddRange(l.AB);
            ba.AddRange(l.BA);
            pending = l.State != LaneState.Active;
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
                        w.Write(l.Hull); w.Write(l.Spacing); w.Write(l.Trips); w.Write(l.LastUsed); w.Write(l.Created);
                        w.Write((byte)l.State); w.Write(l.Reason ?? "");
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
                    if (r.ReadInt32() != FILE_VERSION) return;
                    nextNumber = r.ReadInt32();
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        var l = new Lane { Number = r.ReadInt32(), Owner = r.ReadInt64(), EndA = r.ReadInt64(), EndB = r.ReadInt64(), NameA = r.ReadString(), NameB = r.ReadString() };
                        l.Up = new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                        l.Hull = r.ReadSingle(); l.Spacing = r.ReadSingle(); l.Trips = r.ReadInt32(); l.LastUsed = r.ReadInt64(); l.Created = r.ReadInt64();
                        l.State = (LaneState)r.ReadByte(); l.Reason = r.ReadString();
                        int c = r.ReadInt32();
                        for (int i = 0; i < c; i++) l.Centre.Add(new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
                        if (l.Centre.Count < 2) continue;
                        Derive(l);
                        l.NextCheckFrame = now + 60 * 60;   // checked again soon after the load
                        lanes[l.Number] = l;
                    }
                    int h = r.ReadInt32();
                    for (int i = 0; i < h; i++) holds[r.ReadString()] = r.ReadInt64();
                }
                listVersion++;
                Log.Info("Lanes: {0} lane pair(s) loaded", lanes.Count);
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
