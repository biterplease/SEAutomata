using System;
using System.Collections.Generic;
using System.IO;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Pathfinding
{
    /// <summary>
    /// Shared pathfinding memory (TODO/20, section 3): the places drones have flown, as a graph of points and the links
    /// between them. A link is proven free for the hull radius stored on it (a drone that size flew it). Server only,
    /// one instance (PathfindingManager.Memory).
    ///  - Record: a flying drone drops a point every RECORD_STEP metres or on a turn of more than RECORD_TURN degrees;
    ///    consecutive points of one flight are linked. Points close to an existing one are merged into it.
    ///  - Frame: points of a flight anchored to a grid (the order's anchor block) are kept in that block's frame, so
    ///    they move with it; the others in world coordinates.
    ///  - Use: stage 2 of the planner's chain (FindRoute): start and goal joined to nearby points in a checked straight
    ///    line, A* over the links big enough for the hull, each link of the result checked again (a blocked one is
    ///    dropped and the search runs again).
    ///  - Upkeep: a link is dropped on a failed check, or when unused for PathMemoryMaxAgeHours; the least recently
    ///    used points go first when there are more than PathMemoryMaxNodes. Saved in world storage (SaveData, and at
    ///    most once a minute while changed); points whose anchor no longer exists are dropped once the world runs.
    /// </summary>
    public sealed class RouteMemory
    {
        private const double RECORD_STEP = 25;        // m between points
        private const double RECORD_TURN_COS = 0.9659; // cos 15°: a turn this sharp drops a point (once RECORD_MIN_STEP flown)
        private const double RECORD_MIN_STEP = 5;     // m
        private const double MERGE = 10;              // m: a point this close to a known one is that one
        private const double CHAIN_JUMP = 100;        // m between two records of one drone: not one flight (no link)
        private const int CHAIN_TIMEOUT_TICKS = 60 * 60; // a minute without a record: the next one starts a new flight
        private const double JOIN_RANGE = 1000;       // m: points this close to the start / goal are tried as ways in / out
        private const int JOIN_TRIES = 8;             // nearest points checked per end...
        private const int JOIN_KEEP = 3;              // ...and kept once joined
        private const int SEARCH_RETRIES = 4;         // A* again after dropping blocked links
        private const int FILE_VERSION = 1;
        private const string FILE_NAME = "Automata_RouteMemory.bin";
        private const int SAVE_EVERY_TICKS = 60 * 60;
        private const int PRUNE_EVERY_TICKS = 60 * 60 * 5;
        private const int ANCHOR_GRACE_TICKS = 60 * 60 * 2;   // after the load: anchors may still be streaming in

        private sealed class Node
        {
            public int Id;
            public long Anchor;            // 0: world
            public Vector3D Pos;           // in the anchor's frame (world when Anchor is 0)
            public float Radius;
            public long LastUsed;          // UTC minutes
            public readonly List<int> Links = new List<int>();
        }

        private sealed class Link
        {
            public int Id, A, B;
            public float Radius;
            public int Score;
            public long LastUsed;
            public int UsesAB, UsesBA;
        }

        private struct Recorder
        {
            public int LastNode;
            public Vector3D LastWorld, LastDir;
            public long Anchor;
            public int Frame;
        }

        private readonly Dictionary<int, Node> nodes = new Dictionary<int, Node>();
        private readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
        private readonly Dictionary<long, Recorder> recorders = new Dictionary<long, Recorder>();
        private readonly Dictionary<int, Vector3D> worldCache = new Dictionary<int, Vector3D>();
        private readonly Dictionary<long, MatrixD> anchorCache = new Dictionary<long, MatrixD>();
        private readonly List<int> scratch = new List<int>();
        private int nextId = 1;
        private bool dirty;
        private int lastSave, lastPrune, loadFrame;
        private readonly int maxNodes;
        private readonly double maxAgeMinutes;

        public int NodeCount { get { return nodes.Count; } }
        public int LinkCount { get { return links.Count; } }

        public RouteMemory(int maxNodes, double maxAgeHours)
        {
            this.maxNodes = Math.Max(100, maxNodes);
            this.maxAgeMinutes = Math.Max(1, maxAgeHours) * 60;
        }

        private static long NowMinutes { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute; } }

        #region Frames
        private bool AnchorMatrix(long anchor, out MatrixD m)
        {
            if (anchor == 0) { m = MatrixD.Identity; return true; }
            if (anchorCache.TryGetValue(anchor, out m)) return true;
            IMyEntity e;
            if (MyAPIGateway.Entities == null || !MyAPIGateway.Entities.TryGetEntityById(anchor, out e) || e == null || e.MarkedForClose)
                return false;
            m = e.WorldMatrix;
            anchorCache[anchor] = m;
            return true;
        }

        private static Vector3D ToLocal(ref MatrixD m, Vector3D world)
        {
            Vector3D w = world - m.Translation;
            return new Vector3D(Vector3D.Dot(w, m.Right), Vector3D.Dot(w, m.Up), Vector3D.Dot(w, m.Forward));
        }

        private static Vector3D ToWorld(ref MatrixD m, Vector3D local)
        {
            return m.Translation + m.Right * local.X + m.Up * local.Y + m.Forward * local.Z;
        }

        private bool World(Node n, out Vector3D w)
        {
            if (worldCache.TryGetValue(n.Id, out w)) return true;
            MatrixD m;
            if (!AnchorMatrix(n.Anchor, out m)) return false;
            w = ToWorld(ref m, n.Pos);
            worldCache[n.Id] = w;
            return true;
        }

        // Positions move with their grids: the caches hold for one query / one recording only
        private void NewQuery()
        {
            worldCache.Clear();
            anchorCache.Clear();
        }
        #endregion

        #region Recording
        /// <summary>
        /// A flying drone's hull centre (world) and hull radius, every few ticks while it flies a route. 'anchor': the
        /// entity id of the order's anchor block (0: world).
        /// </summary>
        public void Record(long droneId, long anchor, Vector3D world, double hullRadius, int now)
        {
            Recorder r;
            bool chained = recorders.TryGetValue(droneId, out r) && r.Anchor == anchor && now - r.Frame < CHAIN_TIMEOUT_TICKS
                           && nodes.ContainsKey(r.LastNode) && Vector3D.DistanceSquared(r.LastWorld, world) < CHAIN_JUMP * CHAIN_JUMP;
            if (chained)
            {
                Vector3D step = world - r.LastWorld;
                double len = step.Length();
                if (len < RECORD_MIN_STEP) { r.Frame = now; recorders[droneId] = r; return; }
                Vector3D dir = step / len;
                bool turned = r.LastDir.LengthSquared() > 0.5 && Vector3D.Dot(dir, r.LastDir) < RECORD_TURN_COS;
                if (len < RECORD_STEP && !turned) return;
                NewQuery();
                int id = PointAt(anchor, world, (float)hullRadius);
                if (id < 0) { recorders.Remove(droneId); return; }
                if (id != r.LastNode) Join(r.LastNode, id, (float)hullRadius);
                r.LastNode = id;
                r.LastDir = dir;
                r.LastWorld = world;
                r.Frame = now;
                recorders[droneId] = r;
                return;
            }
            NewQuery();
            int first = PointAt(anchor, world, (float)hullRadius);
            if (first < 0) { recorders.Remove(droneId); return; }
            recorders[droneId] = new Recorder { LastNode = first, LastWorld = world, LastDir = Vector3D.Zero, Anchor = anchor, Frame = now };
        }

        /// <summary>The drone stopped flying a route (docked, working, stopped): its next record starts a new flight.</summary>
        public void BreakChain(long droneId)
        {
            recorders.Remove(droneId);
        }

        // The point at 'world' (merged into a close one of the same frame), -1: the anchor can't be resolved
        private int PointAt(long anchor, Vector3D world, float radius)
        {
            MatrixD m;
            if (!AnchorMatrix(anchor, out m)) return -1;
            Vector3D local = ToLocal(ref m, world);
            long minute = NowMinutes;
            Node best = null;
            double bestD = MERGE * MERGE;
            foreach (var n in nodes.Values)
            {
                if (n.Anchor != anchor) continue;
                double d = Vector3D.DistanceSquared(n.Pos, local);
                if (d < bestD) { bestD = d; best = n; }
            }
            if (best != null)
            {
                best.LastUsed = minute;
                if (radius > best.Radius) best.Radius = radius;
                dirty = true;
                return best.Id;
            }
            if (nodes.Count >= maxNodes) EvictOldest();
            var node = new Node { Id = nextId++, Anchor = anchor, Pos = local, Radius = radius, LastUsed = minute };
            nodes[node.Id] = node;
            dirty = true;
            return node.Id;
        }

        private void Join(int a, int b, float radius)
        {
            Node na, nb;
            if (!nodes.TryGetValue(a, out na) || !nodes.TryGetValue(b, out nb)) return;
            Link l = Between(na, b);
            long minute = NowMinutes;
            if (l == null)
            {
                l = new Link { Id = nextId++, A = a, B = b, Radius = radius, Score = 1, LastUsed = minute };
                links[l.Id] = l;
                na.Links.Add(l.Id);
                nb.Links.Add(l.Id);
            }
            else
            {
                if (radius > l.Radius) l.Radius = radius;
                l.Score = Math.Min(l.Score + 1, 1000);
                l.LastUsed = minute;
            }
            if (l.A == a) l.UsesAB++; else l.UsesBA++;
            dirty = true;
        }

        private Link Between(Node n, int other)
        {
            for (int i = 0; i < n.Links.Count; i++)
            {
                Link l;
                if (links.TryGetValue(n.Links[i], out l) && (l.A == other || l.B == other)) return l;
            }
            return null;
        }

        private void EvictOldest()
        {
            Node oldest = null;
            foreach (var n in nodes.Values)
                if (oldest == null || n.LastUsed < oldest.LastUsed) oldest = n;
            if (oldest != null) RemoveNode(oldest);
        }

        private void RemoveNode(Node n)
        {
            scratch.Clear();
            scratch.AddRange(n.Links);
            for (int i = 0; i < scratch.Count; i++) RemoveLink(scratch[i]);
            nodes.Remove(n.Id);
            dirty = true;
        }

        private void RemoveLink(int id)
        {
            Link l;
            if (!links.TryGetValue(id, out l)) return;
            links.Remove(id);
            Node n;
            if (nodes.TryGetValue(l.A, out n)) n.Links.Remove(id);
            if (nodes.TryGetValue(l.B, out n)) n.Links.Remove(id);
            dirty = true;
        }
        #endregion

        #region Planning (stage 2)
        private sealed class SearchNode : FastPriorityQueueNode
        {
            public int Id;            // memory node, or START / GOAL
            public double G;
            public SearchNode Parent;
            public bool Closed;
        }
        private const int START = -1, GOAL = -2;
        private readonly Dictionary<int, SearchNode> search = new Dictionary<int, SearchNode>();
        private readonly List<KeyValuePair<int, double>> joinStart = new List<KeyValuePair<int, double>>();
        private readonly List<KeyValuePair<int, double>> joinGoal = new List<KeyValuePair<int, double>>();
        private readonly List<KeyValuePair<int, double>> near = new List<KeyValuePair<int, double>>();
        private readonly System.Diagnostics.Stopwatch findClock = new System.Diagnostics.Stopwatch();
        private double findMaxMs;
        private bool TimeUp { get { return findMaxMs > 0 && findClock.Elapsed.TotalMilliseconds > findMaxMs; } }
        private FastPriorityQueue<SearchNode> open;   // reused (sized for the memory, grown with it)

        /// <summary>
        /// A way from 'start' to 'goal' over remembered links (hull-centre points, world), appended to 'waypoints' (start
        /// and goal excluded). False: none ('why' says what was missing). Every straight stretch of the answer was checked.
        /// 'maxMs' (> 0): given up after that long (the search takes the hard cases).
        /// </summary>
        public bool FindRoute(PathfindingManager pm, ref PathQuery q, Vector3D start, Vector3D goal, List<Vector3D> waypoints, out string why,
                              double maxMs = 0)
        {
            why = null;
            if (nodes.Count == 0) { why = "no remembered points"; return false; }
            findClock.Restart();
            findMaxMs = maxMs;
            NewQuery();
            double hull = PathfindingManager.HullRadiusOf(ref q);
            double reach = Math.Max(JOIN_RANGE, Vector3D.Distance(start, goal) * 0.5);
            Vector3D mid = (start + goal) * 0.5;
            double area = Vector3D.Distance(start, goal) * 0.5 + reach;

            Nearest(start, Math.Min(reach, JOIN_RANGE), mid, area, near);
            Joinable(pm, ref q, start, true, joinStart);
            if (TimeUp) { why = "time budget"; return false; }
            if (joinStart.Count == 0) { why = "no remembered point reachable from the start"; return false; }
            Nearest(goal, Math.Min(reach, JOIN_RANGE), mid, area, near);
            Joinable(pm, ref q, goal, false, joinGoal);
            if (TimeUp) { why = "time budget"; return false; }
            if (joinGoal.Count == 0) { why = "no remembered point reaches the goal"; return false; }

            for (int attempt = 0; attempt <= SEARCH_RETRIES; attempt++)
            {
                if (TimeUp) { why = "time budget"; return false; }
                var path = AStar(hull, start, goal);
                if (path == null) { why = attempt == 0 ? "the remembered points don't connect" : "remembered links blocked"; return false; }
                // Every link of the answer checked (the joins already were): a blocked one goes, and the search runs again
                bool ok = true;
                for (int i = 1; i + 2 < path.Count && ok; i++)
                {
                    if (TimeUp) { why = "time budget"; return false; }
                    Vector3D a, b;
                    Node na = nodes[path[i]], nb = nodes[path[i + 1]];
                    if (!World(na, out a) || !World(nb, out b)) { ok = false; break; }
                    Vector3D hit;
                    IMyEntity e;
                    if (pm.SegmentBlocked(ref q, a, b, 0.5, out hit, out e))
                    {
                        Link l = Between(na, nb.Id);
                        if (l != null)
                        {
                            if (Log.DebugEnabled) Log.Debug("Pathfinding memory: link {0} -> {1} ({2:F0} m) blocked by {3}: dropped", na.Id, nb.Id, Vector3D.Distance(a, b),
                                      e != null ? e.DisplayName : "?");
                            RemoveLink(l.Id);
                        }
                        ok = false;
                    }
                }
                if (!ok) continue;
                for (int i = 1; i + 1 < path.Count; i++)
                {
                    Vector3D w;
                    if (World(nodes[path[i]], out w)) waypoints.Add(w);
                }
                return true;
            }
            why = "remembered links blocked";
            return false;
        }

        // Points of the memory near 'p' (within 'range'), nearest first, inside the search area (into 'near')
        private void Nearest(Vector3D p, double range, Vector3D mid, double area, List<KeyValuePair<int, double>> result)
        {
            result.Clear();
            double r2 = range * range, a2 = area * area;
            foreach (var n in nodes.Values)
            {
                Vector3D w;
                if (!World(n, out w)) continue;
                double d = Vector3D.DistanceSquared(w, p);
                if (d > r2 || Vector3D.DistanceSquared(w, mid) > a2) continue;
                result.Add(new KeyValuePair<int, double>(n.Id, d));
            }
            result.Sort((x, y) => x.Value.CompareTo(y.Value));
        }

        // The nearest points (from 'near') joined to 'p' by a checked straight line: kept with the length
        private void Joinable(PathfindingManager pm, ref PathQuery q, Vector3D p, bool fromStart, List<KeyValuePair<int, double>> result)
        {
            result.Clear();
            for (int i = 0; i < near.Count && i < JOIN_TRIES && result.Count < JOIN_KEEP && !TimeUp; i++)
            {
                Vector3D w;
                if (!World(nodes[near[i].Key], out w)) continue;
                Vector3D hit;
                IMyEntity e;
                bool blocked = fromStart
                    ? pm.SegmentBlocked(ref q, p, w, 0.5, out hit, out e)
                    : pm.SegmentBlocked(ref q, ref q.Controller, w, p, 0.5, out hit, out e, !q.LooseGoal);
                if (!blocked) result.Add(new KeyValuePair<int, double>(near[i].Key, Math.Sqrt(near[i].Value)));
            }
        }

        // A* from START over the links wide enough for 'hull' to GOAL: the node ids, START and GOAL included; null: none
        private List<int> AStar(double hull, Vector3D start, Vector3D goal)
        {
            search.Clear();
            if (open == null || open.MaxSize < nodes.Count + 4) open = new FastPriorityQueue<SearchNode>(nodes.Count * 2 + 16);
            else open.Clear();
            var s = new SearchNode { Id = START, G = 0 };
            search[START] = s;
            open.Enqueue(s, Vector3D.Distance(start, goal));
            float need = (float)(hull - 0.5);
            while (open.Count > 0)
            {
                var cur = open.Dequeue();
                if (cur.Closed) continue;
                cur.Closed = true;
                if (cur.Id == GOAL)
                {
                    var path = new List<int>();
                    for (var c = cur; c != null; c = c.Parent) path.Add(c.Id);
                    path.Reverse();
                    return path;
                }
                Vector3D here = cur.Id == START ? start : Pos(cur.Id);
                if (cur.Id == START)
                {
                    for (int i = 0; i < joinStart.Count; i++) Relax(open, cur, joinStart[i].Key, joinStart[i].Value, goal);
                    continue;
                }
                for (int i = 0; i < joinGoal.Count; i++)
                    if (joinGoal[i].Key == cur.Id) Relax(open, cur, GOAL, joinGoal[i].Value, goal);
                Node n = nodes[cur.Id];
                for (int i = 0; i < n.Links.Count; i++)
                {
                    Link l;
                    if (!links.TryGetValue(n.Links[i], out l) || l.Radius < need) continue;
                    int other = l.A == cur.Id ? l.B : l.A;
                    Node no;
                    Vector3D w;
                    if (!nodes.TryGetValue(other, out no) || !World(no, out w)) continue;
                    Relax(open, cur, other, Vector3D.Distance(here, w), goal);
                }
            }
            return null;
        }

        private Vector3D Pos(int id)
        {
            Vector3D w;
            World(nodes[id], out w);
            return w;
        }

        private void Relax(FastPriorityQueue<SearchNode> open, SearchNode from, int id, double cost, Vector3D goal)
        {
            double g = from.G + cost;
            SearchNode sn;
            if (search.TryGetValue(id, out sn))
            {
                if (sn.Closed || g >= sn.G) return;
                sn.G = g;
                sn.Parent = from;
                double h = id == GOAL ? 0 : Vector3D.Distance(Pos(id), goal);
                if (open.Contains(sn)) open.UpdatePriority(sn, g + h);
                else open.Enqueue(sn, g + h);
                return;
            }
            if (open.Count >= open.MaxSize) return;
            sn = new SearchNode { Id = id, G = g, Parent = from };
            search[id] = sn;
            open.Enqueue(sn, g + (id == GOAL ? 0 : Vector3D.Distance(Pos(id), goal)));
        }
        #endregion

        #region Upkeep and persistence
        /// <summary>Every tick on the server (cheap): pruning now and then, saving at most once a minute while changed.</summary>
        public void Update(int now)
        {
            if (now - lastPrune > PRUNE_EVERY_TICKS) { lastPrune = now; Prune(now); }
            if (dirty && now - lastSave > SAVE_EVERY_TICKS) Save(now);
        }

        private void Prune(int now)
        {
            long minute = NowMinutes;
            scratch.Clear();
            foreach (var l in links.Values)
                if (minute - l.LastUsed > maxAgeMinutes) scratch.Add(l.Id);
            for (int i = 0; i < scratch.Count; i++) RemoveLink(scratch[i]);
            int aged = scratch.Count;
            // Points with no link left that are old too, and points whose grid is gone (once the world had time to load)
            var gone = new List<Node>();
            anchorCache.Clear();
            foreach (var n in nodes.Values)
            {
                MatrixD m;
                bool lost = now - loadFrame > ANCHOR_GRACE_TICKS && !AnchorMatrix(n.Anchor, out m);
                if (lost || (n.Links.Count == 0 && minute - n.LastUsed > maxAgeMinutes)) gone.Add(n);
            }
            for (int i = 0; i < gone.Count; i++) RemoveNode(gone[i]);
            if (aged > 0 || gone.Count > 0)
                if (Log.DebugEnabled) Log.Debug("Pathfinding memory: {0} link(s) unused too long, {1} point(s) dropped; {2} points, {3} links", aged, gone.Count, nodes.Count, links.Count);
        }

        public void Save(int now)
        {
            lastSave = now;
            if (!dirty) return;
            try
            {
                using (BinaryWriter w = MyAPIGateway.Utilities.WriteBinaryFileInWorldStorage(FILE_NAME, typeof(RouteMemory)))
                {
                    w.Write(FILE_VERSION);
                    w.Write(nextId);
                    w.Write(nodes.Count);
                    foreach (var n in nodes.Values)
                    {
                        w.Write(n.Id); w.Write(n.Anchor);
                        w.Write(n.Pos.X); w.Write(n.Pos.Y); w.Write(n.Pos.Z);
                        w.Write(n.Radius); w.Write(n.LastUsed);
                    }
                    w.Write(links.Count);
                    foreach (var l in links.Values)
                    {
                        w.Write(l.Id); w.Write(l.A); w.Write(l.B); w.Write(l.Radius); w.Write(l.Score);
                        w.Write(l.LastUsed); w.Write(l.UsesAB); w.Write(l.UsesBA);
                    }
                }
                dirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("Pathfinding memory: save failed: {0}", ex.Message);
            }
        }

        public void Load(int now)
        {
            loadFrame = now;
            lastSave = now;
            lastPrune = now;
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(FILE_NAME, typeof(RouteMemory))) return;
                using (BinaryReader r = MyAPIGateway.Utilities.ReadBinaryFileInWorldStorage(FILE_NAME, typeof(RouteMemory)))
                {
                    int version = r.ReadInt32();
                    if (version != FILE_VERSION) { Log.Warning("Pathfinding memory: file version {0}, expected {1}: not loaded", version, FILE_VERSION); return; }
                    nextId = r.ReadInt32();
                    int nc = r.ReadInt32();
                    for (int i = 0; i < nc; i++)
                    {
                        var n = new Node { Id = r.ReadInt32(), Anchor = r.ReadInt64() };
                        n.Pos = new Vector3D(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                        n.Radius = r.ReadSingle();
                        n.LastUsed = r.ReadInt64();
                        nodes[n.Id] = n;
                    }
                    int lc = r.ReadInt32();
                    for (int i = 0; i < lc; i++)
                    {
                        var l = new Link { Id = r.ReadInt32(), A = r.ReadInt32(), B = r.ReadInt32(), Radius = r.ReadSingle(), Score = r.ReadInt32(),
                                           LastUsed = r.ReadInt64(), UsesAB = r.ReadInt32(), UsesBA = r.ReadInt32() };
                        Node a, b;
                        if (!nodes.TryGetValue(l.A, out a) || !nodes.TryGetValue(l.B, out b)) continue;
                        links[l.Id] = l;
                        a.Links.Add(l.Id);
                        b.Links.Add(l.Id);
                    }
                }
                Log.Info("Pathfinding memory: {0} points, {1} links loaded", nodes.Count, links.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Pathfinding memory: load failed ({0}): starting empty", ex.Message);
                nodes.Clear();
                links.Clear();
            }
        }
        #endregion
    }
}
