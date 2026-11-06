using System;
using System.Collections.Generic;
using System.Diagnostics;

using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace Automata.Pointer
{
    /// <summary>
    /// A scout sweep sector's flight path, mapped once the drone is within MAP_RANGE (the pathfinder "knows" the whole
    /// area then, beyond the drone's instruments): hull-centre points at a fixed clearance over the surface (the hull's
    /// underside CLEARANCE above it, the hull centre a fixed offset above that), following its shape, simplified to as few
    /// points as stay within DEVIATION_UP / DEVIATION_DOWN of it, an ore scan every 'spacing' m. Built in steps (Step) with
    /// a time budget per call, on the main thread. "The surface" under a sample is the highest of: under the hull centre,
    /// under both sides (hull half size + waypoint bubble + LATERAL_MARGIN across the track) and under the samples within
    /// hull half size + bubble + ALONG_MARGIN before / after it (steps between samples); the clearance also gets
    /// BUBBLE_CUT x the bubble (a pass-through corner cuts inside the path).
    ///  - Asteroid: ScoutPlanner.OrbitCount(sectors) full great circles through the spot, evenly spread round it (this
    ///    sector's share, round-robin), mapped once per order for all its drones (Pathfinding.SweepCache: the rock's
    ///    envelope, read at LOD 2, hull-free); BeginOrbits lifts this sector's orbits by the drone's own hull offset +
    ///    ASTEROID_CLEARANCE away from the asteroid's centre and simplifies them: no reads, ready at once.
    ///  - Planet: this sector's arm of a spiral round the spot (Spiral: SpiralRadius - 100 m, + 75 m per drone more - one arm
    ///    per drone, SPIRAL_TURNS turns each, from the centre out), drawn in the plane square to gravity at the spot and
    ///    each point dropped onto the heightmap (the generated surface: no player edits, no caves) - no path search.
    ///  - Open space: the same spiral in the plane through the spot facing the player.
    /// </summary>
    public sealed class SweepPlanner
    {
        public const double MAP_RANGE = 1500;             // m: within this of the asteroid / the spot the drone maps its sector
        public const double ASTEROID_CLEARANCE = 10.0;      // m between the hull's underside and the rock (10 scraped)
        public const double PLANET_CLEARANCE = 15.0;        // m over the heightmap (trees)
        private const double PLANET_SAMPLE = 10;          // m between heightmap samples along a row
        private const double LATERAL_MARGIN = 4;          // m past the hull's side and the bubble where the surface is read too
        private const double ALONG_MARGIN = 8;            // m before / after a sample whose surface lifts it too
        private const double BUBBLE_CUT = 0.5;            // x the waypoint bubble: how far a passed corner may cut below the path
        private const double DEVIATION_UP = 3;            // m the simplified path may run above the sampled one...
        private const double DEVIATION_DOWN = 0.5;        // ...below it...
        private const double DEVIATION_SIDE = 4;          // ...and off it altogether
        private const double SEGMENT_MAX = 60;            // m per path segment at most (look-ahead, scans)
        private const double SPACING_MIN = 15;            // m between scans / rows at least
        public const double SPIRAL_RADIUS_FIRST = 100;    // m of spiral radius for one drone (planet / open space)...
        public const double SPIRAL_RADIUS_STEP = 75;      // ...and this much more per drone
        public const double SPIRAL_TURNS = 2;             // turns of each drone's arm
        private const double SPIRAL_START = 30;           // m from the centre where the arms start (several drones: apart)
        private const double JOIN_AWAY_DEGREES = 30;      // asteroid orbits all cross over the spot and opposite it: joined at least this far from both

        public readonly List<Vector3D> Points = new List<Vector3D>();   // hull-centre positions, in flight order
        public readonly List<Vector3D> Ups = new List<Vector3D>();      // the attitude's up there (away from the surface)
        public readonly List<bool> Scans = new List<bool>();           // an ore scan when passing the point
        public bool Done { get; private set; }
        public double Length { get; private set; }
        public int ScanCount { get; private set; }
        public int SampleCount { get; private set; }
        public int Reads { get; private set; }
        public double Ms { get { return clock.Elapsed.TotalMilliseconds; } }
        public string What { get; private set; }

        private enum Kind : byte { Asteroid, Planet, Space }
        private Kind kind;
        private MyPlanet planet;
        private Vector3D centre;          // the asteroid's / planet's centre
        private double clearance, offset, spacing, lateral, along, sample;
        private readonly Stopwatch clock = new Stopwatch();

        // The raw path: one sample per step (the surface point under the hull centre, its up, how far the surface under
        // the hull's sides rises above it), lifted and simplified in Finish
        private readonly List<Vector3D> raw = new List<Vector3D>();
        private readonly List<Vector3D> rawUp = new List<Vector3D>();
        private readonly List<double> rawSide = new List<double>();
        // Planet / space: the sector's serpentine, sampled in the plane through the spot (planet: projected onto the surface)
        private readonly List<Vector3D> planePoints = new List<Vector3D>();
        private Vector3D planeUp;
        private int planeIndex;

        /// <summary>Radius of a planet / open space sweep by its drones: 100 m, 175 m, 250 m...</summary>
        public static double SpiralRadius(int sectors)
        {
            return SPIRAL_RADIUS_FIRST + SPIRAL_RADIUS_STEP * (Math.Max(1, sectors) - 1);
        }

        // The spiral's plane: 'up' is its normal (gravity at the spot / towards the player); the same axes wherever asked
        private static void PlaneAxes(Vector3D up, out Vector3D a, out Vector3D b)
        {
            a = Vector3D.CalculatePerpendicularVector(up);
            b = Vector3D.Cross(up, a);
        }

        // Arm 'sector' of 'sectors' at 't' rad along it (0 .. SPIRAL_TURNS x 2 pi): plane coordinates; 'r' its radius.
        // Arms start SPIRAL_START out (one drone: at the centre), evenly spread round it, and reach SpiralRadius together;
        // neighbouring arms are pitch / sectors apart (one drone 50 m: 100 m in 2 turns; several ~36-37 m)
        private static Vector2D SpiralAt(int sector, int sectors, double t, out double r, out double pitch)
        {
            double r0 = sectors > 1 ? SPIRAL_START : 0;
            pitch = (SpiralRadius(sectors) - r0) / SPIRAL_TURNS;   // m per turn of one arm
            r = r0 + pitch * t / MathHelperD.TwoPi;
            double ang = t + MathHelperD.TwoPi * sector / sectors;
            return new Vector2D(r * Math.Cos(ang), r * Math.Sin(ang));
        }

        /// <summary>Where arm 'sector' starts, in the plane through 'spot' square to 'up' (not dropped onto the ground).</summary>
        public static Vector3D SpiralStart(Vector3D spot, Vector3D up, int sector, int sectors)
        {
            sectors = Math.Max(1, sectors);
            sector = MathHelper.Clamp(sector, 0, sectors - 1);
            Vector3D a, b;
            PlaneAxes(up, out a, out b);
            double r, pitch;
            Vector2D q = SpiralAt(sector, sectors, 0, out r, out pitch);
            return spot + a * q.X + b * q.Y;
        }

        /// <summary>
        /// An asteroid sector's path from its order's mapped orbits (Pathfinding.SweepCache, this sector's done): each
        /// baseline point lifted by this drone's hull offset + clearance (ASTEROID_CLEARANCE + BUBBLE_CUT x the bubble) away
        /// from the asteroid's centre, simplified, its scans placed. Each orbit is flown once round from where it is joined:
        /// the first at its point nearest 'from' (the drone), the next at its point nearest where the last one closed (over a
        /// point lifted to clear the chord between them), never within JOIN_AWAY_DEGREES of the spot or the point opposite
        /// it (every orbit crosses there: all the order's drones converged on the spot, joins failed, one gave up). Ready at
        /// once (Done).
        /// </summary>
        public void BeginOrbits(Pathfinding.SweepCache.Entry e, int sector, int sectors, double range, double hullOffset, double bubble, Vector3D from)
        {
            Points.Clear(); Ups.Clear(); Scans.Clear(); raw.Clear(); rawUp.Clear(); rawSide.Clear(); planePoints.Clear();
            Done = false; Length = 0; ScanCount = 0; SampleCount = 0; Reads = e.Reads;
            clock.Reset();
            kind = Kind.Asteroid;
            centre = e.Centre;
            offset = Math.Max(0, hullOffset);
            clearance = ASTEROID_CLEARANCE + BUBBLE_CUT * Math.Max(0, bubble);
            lateral = along = 0;
            sample = Pathfinding.SweepCache.SAMPLE;
            sectors = Math.Max(1, sectors);
            sector = MathHelper.Clamp(sector, 0, sectors - 1);
            int own = 0;
            Vector3D at = from;
            double cosAway = Math.Cos(MathHelperD.ToRadians(JOIN_AWAY_DEGREES));
            for (int k = sector; k < e.Orbits.Count; k += sectors)
            {
                var env = e.Orbits[k].Envelope;
                int n = env.Count;
                if (n == 0) continue;
                int join = -1, any = 0;
                double best = double.MaxValue, bestAny = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    double d = Vector3D.DistanceSquared(env[i], at);
                    if (d < bestAny) { bestAny = d; any = i; }
                    Vector3D u = env[i] - centre;
                    if (u.Normalize() < 1e-3 || Math.Abs(Vector3D.Dot(u, e.Axis)) > cosAway) continue;
                    if (d < best) { best = d; join = i; }
                }
                if (join < 0) join = any;
                if (own > 0) AddChordLift(at, env[join]);   // from where the last orbit closed
                for (int t = 0; t <= n; t++)   // once round, closed at the join
                {
                    Vector3D p = env[(join + t) % n];
                    Vector3D up = p - centre;
                    if (up.Normalize() < 1e-3) continue;
                    AddRaw(p, up, 0);
                }
                at = env[join];
                own++;
            }
            range = Math.Max(SPACING_MIN, range);
            double h = clearance + offset;
            spacing = Math.Max(SPACING_MIN, 1.4 * Math.Sqrt(Math.Max(1, range * range - h * h)));
            What = string.Format("asteroid {0}, {1} orbit(s)", e.Name, own);
            Finish();
        }

        /// <summary>
        /// Starts mapping a planet / open space sector. 'hullOffset': the hull centre's height above its underside (the drone
        /// flies belly to the surface); 'hullHalfSize': the hull's half size across / along; 'bubble': the drone's waypoint
        /// bubble (m).
        /// </summary>
        public void Begin(Vector3D spot, Vector3D player, int sector, int sectors, double range, double hullOffset, double hullHalfSize, double bubble)
        {
            Points.Clear(); Ups.Clear(); Scans.Clear(); raw.Clear(); rawUp.Clear(); rawSide.Clear(); planePoints.Clear();
            Done = false; Length = 0; ScanCount = 0; SampleCount = 0; Reads = 0;
            clock.Reset();
            offset = Math.Max(0, hullOffset);
            range = Math.Max(SPACING_MIN, range);
            sectors = Math.Max(1, sectors);
            sector = MathHelper.Clamp(sector, 0, sectors - 1);
            {
                float interference;
                bool gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(spot, out interference).LengthSquared() > 0.0001f;
                planet = gravity ? MyGamePruningStructure.GetClosestPlanet(spot) : null;
                kind = planet != null ? Kind.Planet : Kind.Space;
                clearance = kind == Kind.Planet ? PLANET_CLEARANCE : 0;
                if (kind == Kind.Planet)
                {
                    centre = planet.PositionComp.GetPosition();
                    planeUp = Vector3D.Normalize(spot - centre);
                }
                else
                {
                    planeUp = player - spot;
                    planeUp = planeUp.LengthSquared() > 1 ? Vector3D.Normalize(planeUp) : Vector3D.Up;
                }
                Spiral(spot, sector, sectors);
                planeIndex = 0;
                What = string.Format("{0} spiral arm {1}/{2} ({3:F0} m radius)", kind == Kind.Planet ? "planet" : "open space", sector + 1, sectors, SpiralRadius(sectors));
            }
            hullHalfSize = Math.Max(0, hullHalfSize);
            bubble = Math.Max(0, bubble);
            if (kind != Kind.Space) clearance += BUBBLE_CUT * bubble;
            lateral = hullHalfSize + bubble + LATERAL_MARGIN;
            along = hullHalfSize + bubble + ALONG_MARGIN;
            sample = PLANET_SAMPLE;
            double h = clearance + offset;
            spacing = Math.Max(SPACING_MIN, 1.4 * Math.Sqrt(Math.Max(1, range * range - h * h)));
        }

        /// <summary>Maps on for up to 'maxMs' ms. True when the path is ready (Points, Ups, Scans).</summary>
        public bool Step(double maxMs)
        {
            if (Done) return true;
            clock.Start();
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.Elapsed.TotalMilliseconds < maxMs)
                    if (!StepPlane())
                    {
                        Finish();
                        return true;
                    }
                return false;
            }
            finally { clock.Stop(); }
        }

        // One serpentine sample; false when the sector's rows are done
        private bool StepPlane()
        {
            if (planeIndex >= planePoints.Count) return false;
            int i = planeIndex++;
            Vector3D p = planePoints[i];
            if (kind == Kind.Planet && planet != null && !planet.Closed)
            {
                Vector3D s = planet.GetClosestSurfacePointGlobal(ref p);
                Reads++;
                Vector3D up = s - centre;
                if (up.LengthSquared() > 1)
                {
                    up.Normalize();
                    // The heightmap under the hull's sides too, across the row
                    Vector3D across = Vector3D.Cross(up, planePoints[Math.Min(i + 1, planePoints.Count - 1)] - planePoints[Math.Max(i - 1, 0)]);
                    double side = 0;
                    if (across.Normalize() > 1e-3)
                        for (int k = -1; k <= 1; k += 2)
                        {
                            Vector3D q = p + across * (k * lateral);
                            Vector3D sl = planet.GetClosestSurfacePointGlobal(ref q);
                            Reads++;
                            side = Math.Max(side, Vector3D.Dot(sl - s, up));
                        }
                    AddRaw(s, up, side);
                    return true;
                }
            }
            AddRaw(p, planeUp, 0);
            return true;
        }

        // Between two orbits: a point over the middle of a - b, lifted (from the centre) so neither half chord dips below a's /
        // b's height
        private void AddChordLift(Vector3D a, Vector3D b)
        {
            Vector3D ua = a - centre, ub = b - centre;
            double ra = ua.Normalize(), rb = ub.Normalize();
            Vector3D mid = ua + ub;
            if (mid.Normalize() < 1e-3) return;   // opposite: no middle to lift over
            double half = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(ua, ub), -1, 1)) / 2;
            if (half < 1e-3) return;
            AddRaw(centre + mid * (Math.Max(ra, rb) / Math.Cos(half / 2)), mid, 0);
        }

        // 'surface': the surface point under the hull centre (open space: the path point itself); 'side': how far the surface
        // under the hull's sides rises above it along 'up'
        private void AddRaw(Vector3D surface, Vector3D up, double side)
        {
            raw.Add(surface);
            rawUp.Add(up);
            rawSide.Add(side);
            SampleCount++;
        }

        // This sector's spiral arm in the plane through the spot square to planeUp, from its start out, a point every
        // PLANET_SAMPLE m along it (StepPlane drops each onto the heightmap)
        private void Spiral(Vector3D spot, int sector, int sectors)
        {
            Vector3D a, b;
            PlaneAxes(planeUp, out a, out b);
            double end = SPIRAL_TURNS * MathHelperD.TwoPi, t = 0;
            while (true)
            {
                double r, pitch;
                Vector2D q = SpiralAt(sector, sectors, t, out r, out pitch);
                planePoints.Add(spot + a * q.X + b * q.Y);
                if (t >= end) break;
                double perRad = Math.Sqrt(r * r + Math.Pow(pitch / MathHelperD.TwoPi, 2));   // m along the arm per radian
                t = Math.Min(end, t + PLANET_SAMPLE / Math.Max(1, perRad));
            }
        }

        // The raw samples lifted (the highest surface beside / before / after each, then clearance + hull offset over it) and
        // simplified: from each kept point, as far on as the straight line stays within the deviation
        // of every sample it passes (above / below along its up, and off it altogether) and SEGMENT_MAX / the scan spacing;
        // then a scan every 'spacing' m of path
        private void Finish()
        {
            Done = true;
            int n = raw.Count;
            if (n == 0) return;
            int window = Math.Max(1, (int)Math.Ceiling(along / sample));
            bool envelope = kind == Kind.Asteroid;   // the order's baseline: the rock beside / before / after is in it already
            var lifted = new Vector3D[n];
            for (int i = 0; i < n; i++)
            {
                double lift = envelope ? 0 : rawSide[i];
                if (!envelope)
                    for (int j = Math.Max(0, i - window); j <= Math.Min(n - 1, i + window); j++)
                        if (j != i) lift = Math.Max(lift, Vector3D.Dot(raw[j] - raw[i], rawUp[i]) + rawSide[j]);
                lifted[i] = raw[i] + rawUp[i] * (clearance + offset + Math.Max(0, lift));
            }
            for (int i = 0; i < n; i++) raw[i] = lifted[i];
            rawSide.Clear();
            double segMax = Math.Min(SEGMENT_MAX, spacing);
            int a = 0;
            Points.Add(raw[0]);
            Ups.Add(rawUp[0]);
            while (a < n - 1)
            {
                int best = a + 1;
                for (int j = a + 2; j < n; j++)
                {
                    if (Vector3D.Distance(raw[a], raw[j]) > segMax) break;
                    bool ok = true;
                    for (int m = a + 1; m < j && ok; m++)
                    {
                        Vector3D x = raw[a] + (raw[j] - raw[a]) * ((m - a) / (double)(j - a));
                        Vector3D d = x - raw[m];
                        double along = Vector3D.Dot(d, rawUp[m]);
                        ok = along >= -DEVIATION_DOWN && along <= DEVIATION_UP && d.LengthSquared() <= DEVIATION_SIDE * DEVIATION_SIDE;
                    }
                    if (!ok) break;
                    best = j;
                }
                Points.Add(raw[best]);
                Ups.Add(rawUp[best]);
                a = best;
            }
            double run = 0, next = 0;
            for (int i = 0; i < Points.Count; i++)
            {
                if (i > 0) run += Vector3D.Distance(Points[i - 1], Points[i]);
                bool scan = run >= next;
                if (scan) { ScanCount++; while (next <= run) next += spacing; }
                Scans.Add(scan);
            }
            Length = run;
            raw.Clear();
            rawUp.Clear();
            planePoints.Clear();
        }
    }
}
