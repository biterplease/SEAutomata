using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Asteroid area sweeps' orbits, mapped once per sweep order and shared by its drones. Server only, one instance
    /// (PathfindingManager.Sweeps).
    ///  - Key: what every sector task of one order shares - the asteroid, the pointed spot, the order's random tilt and its
    ///    sector count (the orbit count follows from it). Each drone has its own job: a job id can't be the key.
    ///  - The first drone within SweepPlanner.MAP_RANGE creates the entry; every drone of the order that asks maps on with
    ///    its own time budget, its own orbits first, then the others' (Step), and keeps mapping the rest while it flies its
    ///    path: each orbit is read once (8 drones each mapping their own orbit read the rock 8 times, 140-240 ms each).
    ///  - An orbit's baseline is hull-free: the rock's envelope along its great circle through the spot (the surface under
    ///    the centre line, raised to the highest surface within BASE_LATERAL across and BASE_ALONG along it), a point every
    ///    SAMPLE m, read at LOD 2 (4 m voxels). Each drone lifts it by its own hull offset + clearance away from the
    ///    asteroid's centre (SweepPlanner.BeginOrbits). Hulls wider than BASE_LATERAL see less of the rock beside them.
    ///  - Kept KEEP_MINUTES after the entry was last asked for (refreshed by every drone that asks), saved with the world:
    ///    drones still on a long way there (a designator order relayed by laser antenna) find their orbits after a reload.
    ///    Mapping not finished isn't saved (mapped again).
    /// </summary>
    public sealed class SweepCache
    {
        public const int KEEP_MINUTES = 15;
        public const double SAMPLE = 8;                  // m between samples along an orbit
        private const int LOD = 2;                       // 4 m voxels
        private const double SEARCH_OUT = 60;            // m: rock looked for beyond the last radius (a dip in the orbit)
        private const double BASE_LATERAL = 12;          // m across the track where the rock is read too (hull side + bubble + margin)
        private const double BASE_ALONG = 16;            // m before / after a sample whose rock lifts it too
        private const double BASE_FLIGHT = 15;           // m over the rock the samples are spaced for (clearance + a small hull)
        private const int FILE_VERSION = 2;              // 2: orbits round the rock's centre (1: the box's; not loaded)
        private const string FILE_NAME = "Automata_Sweeps.bin";
        private const int CENTRE_CELLS = 32;             // voxels a side read for the rock's centre, at most (the LOD coarsened to it)

        public sealed class Orbit
        {
            public Vector3D Side;                        // across the axis: the great circle's second direction
            public bool Reverse;
            public bool Done;
            public readonly List<Vector3D> Envelope = new List<Vector3D>();   // done: the baseline, in flight order
            internal readonly List<Vector3D> Raw = new List<Vector3D>();
            internal readonly List<Vector3D> RawUp = new List<Vector3D>();
            internal readonly List<double> RawSide = new List<double>();
            internal double Theta, LastR;
        }

        public sealed class Entry
        {
            public string Key;
            public long AsteroidId;
            public string Name;
            public Vector3D Centre, Axis;                // the asteroid's centre, centre -> spot
            public double SpotR, MaxR;
            public readonly List<Orbit> Orbits = new List<Orbit>();
            public long LastUsed;                        // UTC ticks
            public int Reads;
            public double Ms;
            public bool Loaded;                          // from the world's save

            public bool Complete
            {
                get
                {
                    for (int i = 0; i < Orbits.Count; i++) if (!Orbits[i].Done) return false;
                    return true;
                }
            }

            /// <summary>The orbits of 'sector' of 'sectors' mapped (round-robin share).</summary>
            public bool SectorDone(int sector, int sectors)
            {
                for (int k = sector; k < Orbits.Count; k += Math.Max(1, sectors)) if (!Orbits[k].Done) return false;
                return true;
            }
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly MyStorageData probe = new MyStorageData(MyStorageDataTypeFlags.Content);
        private readonly List<string> expired = new List<string>();

        public static string KeyOf(long asteroidId, Vector3D spot, float tilt, int sectors)
        {
            return asteroidId + "|" + spot.X.ToString("R") + "|" + spot.Y.ToString("R") + "|" + spot.Z.ToString("R") + "|" + tilt.ToString("R") + "|" + sectors;
        }

        /// <summary>
        /// The order's entry (made on the first call: the orbits laid out, nothing read yet), its keep-alive refreshed.
        /// Null: the asteroid is gone.
        /// </summary>
        public Entry Get(long asteroidId, Vector3D spot, float tilt, int sectors)
        {
            Purge();
            string key = KeyOf(asteroidId, spot, tilt, sectors);
            Entry e;
            if (entries.TryGetValue(key, out e))
            {
                e.LastUsed = DateTime.UtcNow.Ticks;
                return e;
            }
            IMyEntity ent;
            var v = MyAPIGateway.Entities.TryGetEntityById(asteroidId, out ent) ? ent as IMyVoxelBase : null;
            if (v == null || v.Storage == null || v.Closed) return null;
            e = new Entry { Key = key, AsteroidId = asteroidId, Name = v.StorageName ?? asteroidId.ToString(), LastUsed = DateTime.UtcNow.Ticks };
            e.Centre = RockCentre(v, out e.MaxR);
            e.Axis = spot - e.Centre;
            e.SpotR = e.Axis.Normalize();
            if (e.SpotR < 1) { e.Axis = Vector3D.Up; e.SpotR = 1; }
            Vector3D e1 = Vector3D.CalculatePerpendicularVector(e.Axis), e2 = Vector3D.Cross(e.Axis, e1);
            int orbits = Automata.Designator.ScoutPlanner.OrbitCount(sectors);
            for (int k = 0; k < orbits; k++)
            {
                double t = tilt + k * Math.PI / orbits;   // great circles through the spot, evenly round it
                e.Orbits.Add(new Orbit { Side = e1 * Math.Cos(t) + e2 * Math.Sin(t), Reverse = k % 2 == 1, LastR = e.SpotR });
            }
            entries[key] = e;
            if (Log.DebugEnabled) Log.Debug("Sweeps: order on asteroid {0}: {1} orbit(s) to map ({2} kept)", e.Name, orbits, entries.Count);
            return e;
        }

        /// <summary>
        /// Maps on for up to 'maxMs' ms: the orbits of 'sector' first (sector &lt; 0: none in particular), then any not done.
        /// True when the sector's orbits are done (sector &lt; 0: all of them).
        /// </summary>
        public bool Step(Entry e, int sector, int sectors, double maxMs)
        {
            if (e.Complete) return true;
            IMyEntity ent;
            var v = MyAPIGateway.Entities.TryGetEntityById(e.AsteroidId, out ent) ? ent as IMyVoxelBase : null;
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.Elapsed.TotalMilliseconds < maxMs)
                {
                    Orbit o = Next(e, sector, sectors);
                    if (o == null) break;
                    if (v == null || v.Closed || v.Storage == null) { o.Done = true; continue; }   // gone: what was read stands
                    StepOrbit(e, o, v);
                }
            }
            finally { e.Ms += sw.Elapsed.TotalMilliseconds; }
            if (e.Complete && Log.DebugEnabled) LogDone(e);
            return sector >= 0 ? e.SectorDone(sector, sectors) : e.Complete;
        }

        private readonly HashSet<string> logged = new HashSet<string>();

        private void LogDone(Entry e)
        {
            if (!logged.Add(e.Key)) return;
            int pts = 0;
            for (int i = 0; i < e.Orbits.Count; i++) pts += e.Orbits[i].Envelope.Count;
            Log.Debug("Sweeps: asteroid {0}: {1} orbit(s) mapped, {2} points, {3} reads in {4:F1} ms", e.Name, e.Orbits.Count, pts, e.Reads, e.Ms);
        }

        /// <summary>
        /// The rock's centre: the mean of its voxels weighted by their content, read in one go at the LOD that keeps the read
        /// to CENTRE_CELLS a side (a 512 m box: LOD 4, 16 m voxels, 32^3 values: a few ms once per order, against ~90,000
        /// single reads per orbit). 'maxR': to the box's farthest corner. The box's centre lay off the rock: where no rock
        /// was found the orbits kept their last radius and swung round empty space.
        /// </summary>
        private static Vector3D RockCentre(IMyVoxelBase v, out double maxR)
        {
            BoundingBoxD box = v.PositionComp.WorldAABB;
            Vector3D centre = box.Center;
            VRage.ModAPI.IMyStorage storage = v.Storage;
            Vector3I size = storage.Size;
            int lod = 0;
            while (lod < 8 && Math.Max(size.X, Math.Max(size.Y, size.Z)) >> lod > CENTRE_CELLS) lod++;
            Vector3I n = Vector3I.Max(size >> lod, Vector3I.One);
            var data = new MyStorageData(MyStorageDataTypeFlags.Content);
            data.Resize(n);
            storage.ReadRange(data, MyStorageDataTypeFlags.Content, lod, Vector3I.Zero, n - 1);
            double vs = 1 << lod, weight = 0;
            Vector3D sum = Vector3D.Zero;
            int i = 0;
            for (int z = 0; z < n.Z; z++)
                for (int y = 0; y < n.Y; y++)
                    for (int x = 0; x < n.X; x++, i++)
                    {
                        byte c = data.Content(i);
                        if (c == 0) continue;
                        sum += new Vector3D(x + 0.5, y + 0.5, z + 0.5) * (vs * c);
                        weight += c;
                    }
            if (weight > 0) centre = v.PositionLeftBottomCorner + sum / weight;
            maxR = 0;
            for (int k = 0; k < 8; k++)
            {
                Vector3D corner = new Vector3D((k & 1) != 0 ? box.Max.X : box.Min.X, (k & 2) != 0 ? box.Max.Y : box.Min.Y, (k & 4) != 0 ? box.Max.Z : box.Min.Z);
                maxR = Math.Max(maxR, Vector3D.Distance(centre, corner));
            }
            if (Log.DebugEnabled) Log.Debug("Sweeps: rock centre {0:F0} m off the box's centre (LOD {1}, {2} values)", Vector3D.Distance(centre, box.Center), lod, n.X * n.Y * n.Z);
            return centre;
        }

        // The next orbit to map: the sector's own first
        private static Orbit Next(Entry e, int sector, int sectors)
        {
            if (sector >= 0)
                for (int k = sector; k < e.Orbits.Count; k += Math.Max(1, sectors)) if (!e.Orbits[k].Done) return e.Orbits[k];
            for (int k = 0; k < e.Orbits.Count; k++) if (!e.Orbits[k].Done) return e.Orbits[k];
            return null;
        }

        // One sample of orbit 'o' (the surface under the centre line and under both sides); the orbit's envelope once round
        private void StepOrbit(Entry e, Orbit o, IMyVoxelBase v)
        {
            double ang = o.Reverse ? -o.Theta : o.Theta;
            Vector3D u = e.Axis * Math.Cos(ang) + o.Side * Math.Sin(ang);
            double r;
            if (!SurfaceRadius(e, v, u, o.LastR, out r)) r = o.LastR;
            o.LastR = r;
            Vector3D n = Vector3D.Cross(e.Axis, o.Side);   // the orbit plane's normal
            double flight = r + BASE_FLIGHT, side = 0;
            for (int k = -1; k <= 1; k += 2)
            {
                Vector3D ul = Vector3D.Normalize(u * flight + n * (k * BASE_LATERAL));
                double rl;
                if (SurfaceRadius(e, v, ul, r, out rl)) side = Math.Max(side, Vector3D.Dot(ul * rl - u * r, u));
            }
            o.Raw.Add(e.Centre + u * r);
            o.RawUp.Add(u);
            o.RawSide.Add(side);
            o.Theta += SAMPLE / Math.Max(1, flight);
            if (o.Theta < 2 * Math.PI) return;
            // Round: each sample raised to the highest rock beside it and within BASE_ALONG before / after it
            int count = o.Raw.Count, window = Math.Max(1, (int)Math.Ceiling(BASE_ALONG / SAMPLE));
            for (int i = 0; i < count; i++)
            {
                double lift = o.RawSide[i];
                for (int j = Math.Max(0, i - window); j <= Math.Min(count - 1, i + window); j++)
                    if (j != i) lift = Math.Max(lift, Vector3D.Dot(o.Raw[j] - o.Raw[i], o.RawUp[i]) + o.RawSide[j]);
                o.Envelope.Add(o.Raw[i] + o.RawUp[i] * Math.Max(0, lift));
            }
            o.Raw.Clear();
            o.RawUp.Clear();
            o.RawSide.Clear();
            o.Done = true;
        }

        /// <summary>
        /// Where the line from the centre along 'u' leaves the rock near 'guess' (m from the centre), at LOD 2: out from the
        /// guess while solid; air there: rock further out (a dip of the orbit) out to its far side; else in to the first
        /// solid. False: no rock along it.
        /// </summary>
        private bool SurfaceRadius(Entry e, IMyVoxelBase v, Vector3D u, double guess, out double r)
        {
            r = guess;
            double step = 1 << LOD, max = e.MaxR;
            double t = MathHelperD.Clamp(guess, 0, max);
            if (Solid(e, v, e.Centre + u * t))
            {
                while (t < max && Solid(e, v, e.Centre + u * (t + step))) t += step;
                r = t + step;
                return true;
            }
            for (double o = step; o <= SEARCH_OUT && t + o < max; o += step)
                if (Solid(e, v, e.Centre + u * (t + o)))
                {
                    double tt = t + o;
                    while (tt < max && Solid(e, v, e.Centre + u * (tt + step))) tt += step;
                    r = tt + step;
                    return true;
                }
            while (t > 0)
            {
                t -= step;
                if (Solid(e, v, e.Centre + u * t)) { r = t + step; return true; }
            }
            return false;
        }

        // Rock at 'p', read at LOD 2 (a 4 m voxel more than half full)
        private bool Solid(Entry e, IMyVoxelBase v, Vector3D p)
        {
            VRage.ModAPI.IMyStorage storage = v.Storage;
            var size = storage.Size;
            Vector3I local = Vector3I.Floor(p - v.PositionLeftBottomCorner);
            if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= size.X || local.Y >= size.Y || local.Z >= size.Z) return false;
            Vector3I cell = local >> LOD;
            probe.Resize(Vector3I.One);
            storage.ReadRange(probe, MyStorageDataTypeFlags.Content, LOD, cell, cell);
            e.Reads++;
            return probe.Content(0) > 127;
        }

        // Entries not asked for within KEEP_MINUTES go
        private void Purge()
        {
            long now = DateTime.UtcNow.Ticks, keep = TimeSpan.FromMinutes(KEEP_MINUTES).Ticks;
            expired.Clear();
            foreach (var kv in entries) if (now - kv.Value.LastUsed > keep) expired.Add(kv.Key);
            for (int i = 0; i < expired.Count; i++)
            {
                entries.Remove(expired[i]);
                logged.Remove(expired[i]);
            }
            if (expired.Count > 0 && Log.DebugEnabled) Log.Debug("Sweeps: {0} order(s) expired, {1} kept", expired.Count, entries.Count);
        }

        #region Persistence
        /// <summary>World save: the complete orders still kept.</summary>
        public void Save()
        {
            Purge();
            try
            {
                using (BinaryWriter w = MyAPIGateway.Utilities.WriteBinaryFileInWorldStorage(FILE_NAME, typeof(SweepCache)))
                {
                    w.Write(FILE_VERSION);
                    int n = 0;
                    foreach (var e in entries.Values) if (e.Complete) n++;
                    w.Write(n);
                    foreach (var e in entries.Values)
                    {
                        if (!e.Complete) continue;
                        w.Write(e.Key); w.Write(e.AsteroidId); w.Write(e.Name ?? ""); w.Write(e.LastUsed);
                        w.Write(e.Centre.X); w.Write(e.Centre.Y); w.Write(e.Centre.Z);
                        w.Write(e.Axis.X); w.Write(e.Axis.Y); w.Write(e.Axis.Z);
                        w.Write(e.SpotR); w.Write(e.MaxR);
                        w.Write(e.Orbits.Count);
                        for (int k = 0; k < e.Orbits.Count; k++)
                        {
                            var o = e.Orbits[k];
                            w.Write(o.Side.X); w.Write(o.Side.Y); w.Write(o.Side.Z); w.Write(o.Reverse);
                            w.Write(o.Envelope.Count);
                            for (int i = 0; i < o.Envelope.Count; i++) { w.Write(o.Envelope[i].X); w.Write(o.Envelope[i].Y); w.Write(o.Envelope[i].Z); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Sweeps: save failed: {0}", ex.Message);
            }
        }

        public void Load()
        {
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(FILE_NAME, typeof(SweepCache))) return;
                using (BinaryReader r = MyAPIGateway.Utilities.ReadBinaryFileInWorldStorage(FILE_NAME, typeof(SweepCache)))
                {
                    if (r.ReadInt32() != FILE_VERSION) return;
                    int n = r.ReadInt32();
                    for (int j = 0; j < n; j++)
                    {
                        var e = new Entry { Key = r.ReadString(), AsteroidId = r.ReadInt64(), Name = r.ReadString(), LastUsed = r.ReadInt64(), Loaded = true };
                        e.Centre = new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                        e.Axis = new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                        e.SpotR = r.ReadDouble();
                        e.MaxR = r.ReadDouble();
                        int orbits = r.ReadInt32();
                        for (int k = 0; k < orbits; k++)
                        {
                            var o = new Orbit { Side = new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()), Reverse = r.ReadBoolean(), Done = true };
                            int c = r.ReadInt32();
                            for (int i = 0; i < c; i++) o.Envelope.Add(new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
                            e.Orbits.Add(o);
                        }
                        entries[e.Key] = e;
                    }
                }
                Purge();
                if (Log.DebugEnabled) Log.Debug("Sweeps: {0} mapped order(s) loaded", entries.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Sweeps: load failed: {0}", ex.Message);
                entries.Clear();
            }
        }
        #endregion
    }
}
