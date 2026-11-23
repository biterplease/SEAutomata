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
    ///  - Record: a flying drone drops a point every RECORD_STEP metres, and where the straight line from its last point
    ///    would stray more than RECORD_DEVIATION from where it flew (a corner: the point goes where the line last held);
    ///    consecutive points of one flight are linked. A new point merges into a known one within MERGE_FREE, or within
    ///    MERGE when the link to it is clear; every new link is checked for the drone's hull box when recorded (no link
    ///    through a wall: the chain starts again there). Indoors (2026-11-13): the old 15° turn rule dropped the point
    ///    metres past a corner and the 10 m merge reached through walls (a parallel tunnel, the far side of a corner);
    ///    the links cut the corners and were dropped the first time they were checked.
    ///  - Frame: points of a flight anchored to a grid (the order's anchor block) are kept in that block's frame, so
    ///    they move with it; the others in world coordinates.
    ///  - Use: stage 2 of the planner's chain (FindRoute): start and goal joined to nearby points in a checked straight
    ///    line, A* over the links big enough for the hull, each link of the result checked again (a blocked one is
    ///    dropped and the search runs again).
    ///  - Whose (2026-11-13): a link belongs to the faction of the drone's owner that flew it, else to that player; only
    ///    their drones use it (in PvP a drone doesn't know another side's ways in). Points are shared: they hold no way.
    ///    Links flown as a player's custom nav route (routeOwner Player) cost CUSTOM_COST of their length: preferred.
    ///  - Checked in use: a link blocked for the drone as it is turned now but clear in another attitude (lined up with it,
    ///    or the one it was recorded with) is flown in that one (LegHints); otherwise it is left out of this plan only.
    ///  - Custom routes (2026-11-13): a player's recorded route (custom nav, Commit) is kept as a route - its points and
    ///    links, its owner, the drone - listed by the orchestrator (delete one / all, draw), never expiring, its points
    ///    never evicted. Upkeep is for the shared links only (the pathfinder's own optimisation): dropped when unused
    ///    for PathMemoryMaxAgeHours (last use saved), never on a failed check; the least recently used points go first
    ///    when there are more than PathMemoryMaxNodes. Saved in world storage (SaveData, and at most once a minute while
    ///    changed); points whose anchor no longer exists are dropped once the world runs.
    /// </summary>
    public sealed class RouteMemory
    {
        private const double RECORD_STEP = 25;        // m between points
        private const double RECORD_DEVIATION = 0.5;  // m the line from the last point may stray from the flown path
        private const double RECORD_SAMPLE_MIN = 0.5; // m between the flown positions kept (hovering adds none)
        private const int RECORD_SAMPLES_MAX = 120;   // flown positions kept since the last point (more: a point)
        private const double MERGE = 10;              // m: a point this close to a known one is that one (link to it clear)...
        private const double MERGE_FREE = 1.0;        // ...this close: without a check (the hull was there)
        private const double CUSTOM_MERGE = 0.1;      // a custom route's point: only onto one (almost) exactly there
        private const double CHAIN_JUMP = 100;        // m between two records of one drone: not one flight (no link)
        private const int CHAIN_TIMEOUT_TICKS = 60 * 60; // a minute without a record: the next one starts a new flight
        private const double JOIN_RANGE = 1000;       // m: points this close to the start / goal are tried as ways in / out
        private const int JOIN_TRIES = 8;             // nearest points checked per end...
        private const int JOIN_KEEP = 3;              // ...and kept once joined
        private const int SEARCH_RETRIES = 4;         // A* again after dropping blocked links
        private const int FILE_VERSION = 4;   // 2: links' owner (player, faction) and custom flag; 3: links' recorded attitude; 4: custom routes
        private const double CUSTOM_COST = 0.5;   // a custom route's link costs this much per metre (others: 1)
        /// <summary>The last FindRoute's answer has custom route links checked with Clearances.FINE, less than the
        /// plan's: its legs are flown and checked with that (PathfindingManager.PlanBerthCap).</summary>
        public bool UsedRouteBerth { get; private set; }
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
            public long Player, Faction;   // whose: the drone owner's faction (0: none), the owner
            public bool Custom;            // flown as a custom nav route
            public Vector3 Fwd, Up;        // a recorded custom route: the attitude it was flown with (zero: none)
            public float Radius;
            public int Score;
            public long LastUsed;
            public int UsesAB, UsesBA;
        }

        /// <summary>A player's recorded route: its points and links in order (world points), whose, which drone.</summary>
        public sealed class CustomRoute
        {
            public int Id;
            public long Player, Faction, DroneId;
            public string Name = "";
            public long Created, LastUsed;   // UTC minutes
            public readonly List<int> Nodes = new List<int>();
            public readonly List<int> Links = new List<int>();
        }

        private readonly Dictionary<int, CustomRoute> routes = new Dictionary<int, CustomRoute>();
        private readonly Dictionary<int, int> routeLinkRefs = new Dictionary<int, int>();   // link -> routes using it
        private int nextRouteId = 1;

        /// <summary>Changes when a custom route is added or deleted (the orchestrators' lists).</summary>
        public int RoutesVersion { get; private set; }

        private struct Recorder
        {
            public int LastNode;
            public Vector3D LastWorld;     // the last point's position (world)
            public long Anchor;
            public int Frame;
            public List<Vector3D> Samples; // where the drone flew since the last point (world)
            public float Radius;           // the hull's (for the point where the flight ends: BreakChain)
            public Func<Vector3D, Vector3D, bool> Clear;
            public long Player, Faction;   // the drone's owner and their faction, at the flight's start
            public bool Custom;            // a custom nav route
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
        /// entity id of the order's anchor block (0: world). 'clear': is the straight line between two hull centres
        /// clear for this drone's hull box (null: no merges beyond MERGE_FREE, links unchecked).
        /// </summary>
        public void Record(long droneId, long anchor, Vector3D world, double hullRadius, int now, Func<Vector3D, Vector3D, bool> clear,
                           bool custom)
        {
            Recorder r;
            bool had = recorders.TryGetValue(droneId, out r);
            bool chained = had && r.Anchor == anchor && now - r.Frame < CHAIN_TIMEOUT_TICKS
                           && nodes.ContainsKey(r.LastNode) && Vector3D.DistanceSquared(r.LastWorld, world) < CHAIN_JUMP * CHAIN_JUMP;
            float radius = (float)hullRadius;
            if (chained)
            {
                r.Radius = radius;
                r.Clear = clear;
                r.Custom = custom;
                if (r.Samples.Count > 0 && Vector3D.DistanceSquared(r.Samples[r.Samples.Count - 1], world) < RECORD_SAMPLE_MIN * RECORD_SAMPLE_MIN)
                {
                    r.Frame = now;
                    recorders[droneId] = r;
                    return;
                }
                // A corner: the line from the last point to here no longer follows the flight - a point where it last did
                if (r.Samples.Count > 0 && Strays(r.LastWorld, world, r.Samples))
                {
                    if (!AddPoint(ref r, anchor, r.Samples[r.Samples.Count - 1], radius, clear)) { recorders.Remove(droneId); return; }
                }
                if (Vector3D.DistanceSquared(r.LastWorld, world) >= RECORD_STEP * RECORD_STEP || r.Samples.Count >= RECORD_SAMPLES_MAX)
                {
                    if (!AddPoint(ref r, anchor, world, radius, clear)) { recorders.Remove(droneId); return; }
                }
                else r.Samples.Add(world);
                r.Frame = now;
                recorders[droneId] = r;
                return;
            }
            NewQuery();
            int first = PointAt(anchor, world, radius, world, false, clear);
            if (first < 0) { recorders.Remove(droneId); return; }
            var samples = had && r.Samples != null ? r.Samples : new List<Vector3D>();
            samples.Clear();
            long player, faction;
            OwnerOf(droneId, out player, out faction);
            recorders[droneId] = new Recorder { LastNode = first, LastWorld = PositionOf(first, world), Anchor = anchor, Frame = now, Samples = samples,
                                                Radius = radius, Clear = clear, Player = player, Faction = faction, Custom = custom };
        }

        // A point at 'world' linked from the recorder's last one (the link checked: none through a wall); the recorder
        // moves on to it. False: the anchor can't be resolved.
        private bool AddPoint(ref Recorder r, long anchor, Vector3D world, float radius, Func<Vector3D, Vector3D, bool> clear)
        {
            NewQuery();
            Node last;
            Vector3D from = Vector3D.Zero;
            bool hasFrom = nodes.TryGetValue(r.LastNode, out last) && World(last, out from);
            bool linkChecked;
            int id = PointAt(anchor, world, radius, from, hasFrom, clear, out linkChecked);
            if (id < 0) return false;
            Vector3D to = PositionOf(id, world);
            if (id != r.LastNode)
            {
                if (linkChecked || !hasFrom || clear == null || clear(from, to)) Join(r.LastNode, id, radius, r.Player, r.Faction, r.Custom);
                else if (Log.DebugEnabled) Log.Debug("Pathfinding memory: no link {0} -> {1} ({2:F0} m): blocked for the hull", r.LastNode, id, Vector3D.Distance(from, to));
            }
            r.LastNode = id;
            r.LastWorld = to;
            r.Samples.Clear();
            return true;
        }

        // A point's position now (world); 'fallback' when its anchor can't be resolved
        private Vector3D PositionOf(int id, Vector3D fallback)
        {
            Node n;
            Vector3D w;
            return nodes.TryGetValue(id, out n) && World(n, out w) ? w : fallback;
        }

        // Some flown position further than RECORD_DEVIATION from the segment a -> b
        private static bool Strays(Vector3D a, Vector3D b, List<Vector3D> samples)
        {
            Vector3D ab = b - a;
            double len2 = ab.LengthSquared();
            for (int i = 0; i < samples.Count; i++)
            {
                Vector3D s = samples[i];
                double t = len2 > 1e-6 ? MathHelperD.Clamp(Vector3D.Dot(s - a, ab) / len2, 0, 1) : 0;
                if (Vector3D.DistanceSquared(s, a + ab * t) > RECORD_DEVIATION * RECORD_DEVIATION) return true;
            }
            return false;
        }

        /// <summary>
        /// A player's recorded route (custom nav, Commit; hull-centre points, world, in order) as links of the drone's owner /
        /// faction, custom (preferred). Not checked here: the player flew it; each link is checked when a plan uses it.
        /// Returns the links added or strengthened.
        /// </summary>
        public int AddRoute(long droneId, string name, List<Vector3D> points, List<Vector3D> fwds, List<Vector3D> ups, float radius)
        {
            long player, faction;
            OwnerOf(droneId, out player, out faction);
            NewQuery();
            long minute = NowMinutes;
            var route = new CustomRoute { Id = nextRouteId++, Player = player, Faction = faction, DroneId = droneId, Name = name ?? "",
                                          Created = minute, LastUsed = minute };
            int prev = -1, prevIdx = -1, joined = 0;
            Vector3D prevPos = Vector3D.Zero;
            for (int i = 0; i < points.Count; i++)
            {
                int id = PointAt(0, points[i], radius, prevPos, prev >= 0, null, CUSTOM_MERGE);   // as flown: not moved onto a known point
                if (id < 0) continue;
                if (prev >= 0 && id != prev)
                {
                    Join(prev, id, radius, player, faction, true);
                    joined++;
                    // The attitude the player flew it with: the one at its start
                    Node np;
                    Link l = nodes.TryGetValue(prev, out np) ? Between(np, id, player, faction) : null;
                    if (l != null && fwds != null && prevIdx < fwds.Count && fwds[prevIdx].LengthSquared() > 0.5)
                    {
                        l.Fwd = fwds[prevIdx];
                        l.Up = ups[prevIdx];
                    }
                    if (l != null) route.Links.Add(l.Id);
                }
                if (prev != id) route.Nodes.Add(id);
                prev = id;
                prevIdx = i;
                prevPos = PositionOf(id, points[i]);
            }
            dirty = true;
            if (route.Links.Count > 0)
            {
                routes[route.Id] = route;
                for (int i = 0; i < route.Links.Count; i++) RefLink(route.Links[i], 1);
                RoutesVersion++;
            }
            if (Log.DebugEnabled) Log.Debug("Pathfinding memory: custom route R{5} of drone {0}: {1} points, {2} links (player {3}, faction {4})", droneId, points.Count, joined, player, faction, route.Id);
            return joined;
        }

        private void RefLink(int link, int by)
        {
            int c;
            routeLinkRefs.TryGetValue(link, out c);
            c += by;
            if (c > 0) routeLinkRefs[link] = c; else routeLinkRefs.Remove(link);
        }

        // A point of a custom route (never evicted)
        private bool OnRoute(Node n)
        {
            for (int i = 0; i < n.Links.Count; i++) if (routeLinkRefs.ContainsKey(n.Links[i])) return true;
            return false;
        }

        /// <summary>The custom routes a drone of 'player' (of 'faction') may use, oldest first.</summary>
        public void RoutesOf(long player, long faction, List<CustomRoute> into)
        {
            into.Clear();
            foreach (var r in routes.Values)
                if (r.Faction != 0 ? r.Faction == faction || r.Player == player : r.Player == player) into.Add(r);
            for (int k = 1; k < into.Count; k++)   // by id (insertion sort: a handful)
            {
                var x = into[k];
                int m = k - 1;
                while (m >= 0 && into[m].Id > x.Id) { into[m + 1] = into[m]; m--; }
                into[m + 1] = x;
            }
        }

        /// <summary>A custom route's points (world), in order; its length (m). False: no such route.</summary>
        public bool RoutePoints(int id, List<Vector3D> into, out double length)
        {
            return RoutePoints(id, into, null, null, out length);
        }

        /// <summary>Same, with each point's recorded attitude ('fwds' / 'ups': its link on's, the last point its link in's;
        /// zero: none).</summary>
        public bool RoutePoints(int id, List<Vector3D> into, List<Vector3D> fwds, List<Vector3D> ups, out double length)
        {
            into.Clear();
            if (fwds != null) fwds.Clear();
            if (ups != null) ups.Clear();
            length = 0;
            CustomRoute r;
            if (!routes.TryGetValue(id, out r)) return false;
            NewQuery();
            for (int i = 0; i < r.Nodes.Count; i++)
            {
                Node n;
                Vector3D w;
                if (!nodes.TryGetValue(r.Nodes[i], out n) || !World(n, out w)) continue;
                if (into.Count > 0) length += Vector3D.Distance(into[into.Count - 1], w);
                into.Add(w);
                if (fwds == null || ups == null) continue;
                Link l = RouteLinkAt(r, i);
                fwds.Add(l != null ? (Vector3D)l.Fwd : Vector3D.Zero);
                ups.Add(l != null ? (Vector3D)l.Up : Vector3D.Zero);
            }
            return into.Count > 0;
        }

        // The route's link from its point i on (the last point: the link into it)
        private Link RouteLinkAt(CustomRoute r, int i)
        {
            int a = r.Nodes[i], b = i + 1 < r.Nodes.Count ? r.Nodes[i + 1] : (i > 0 ? r.Nodes[i - 1] : -1);
            for (int k = 0; k < r.Links.Count; k++)
            {
                Link l;
                if (links.TryGetValue(r.Links[k], out l) && ((l.A == a && l.B == b) || (l.A == b && l.B == a))) return l;
            }
            return null;
        }

        /// <summary>Deletes a custom route of 'player' / 'faction' (its links no other route uses, and the points left
        /// without links). False: none such.</summary>
        public bool DeleteRoute(int id, long player, long faction)
        {
            CustomRoute r;
            if (!routes.TryGetValue(id, out r) || !(r.Faction != 0 ? r.Faction == faction || r.Player == player : r.Player == player)) return false;
            routes.Remove(id);
            for (int i = 0; i < r.Links.Count; i++)
            {
                RefLink(r.Links[i], -1);
                if (!routeLinkRefs.ContainsKey(r.Links[i])) RemoveLink(r.Links[i]);
            }
            for (int i = 0; i < r.Nodes.Count; i++)
            {
                Node n;
                if (nodes.TryGetValue(r.Nodes[i], out n) && n.Links.Count == 0) nodes.Remove(n.Id);
            }
            RoutesVersion++;
            dirty = true;
            if (Log.DebugEnabled) Log.Debug("Pathfinding memory: custom route R{0} deleted", id);
            return true;
        }

        /// <summary>Deletes every custom route of 'player' / 'faction': how many.</summary>
        public int DeleteRoutes(long player, long faction)
        {
            var mine = new List<CustomRoute>();
            RoutesOf(player, faction, mine);
            int n = 0;
            for (int i = 0; i < mine.Count; i++) if (DeleteRoute(mine[i].Id, player, faction)) n++;
            return n;
        }

        // A plan used these links: their last use (expiry), and their routes'
        private void MarkUsed(List<int> linkIds)
        {
            long minute = NowMinutes;
            for (int i = 0; i < linkIds.Count; i++)
            {
                Link l;
                if (linkIds[i] < 0 || !links.TryGetValue(linkIds[i], out l)) continue;
                l.LastUsed = minute;
                if (!routeLinkRefs.ContainsKey(l.Id)) continue;
                foreach (var r in routes.Values) if (r.LastUsed != minute && r.Links.Contains(l.Id)) r.LastUsed = minute;
            }
            dirty = true;
        }

        /// <summary>The drone stopped flying a route (docked, working, stopped): its next record starts a new flight.</summary>
        public void BreakChain(long droneId)
        {
            // A point where the flight ended (its last flown position), linked: the next flight starts there and merges into
            // it. Without it the last stretch (up to RECORD_STEP) was lost: a route flown leg by leg (stops at waypoints)
            // left gaps of up to 25 m between its flights, and the remembered points didn't connect (2026-11-13)
            Recorder r;
            if (recorders.TryGetValue(droneId, out r) && r.Samples != null && r.Samples.Count > 0 && nodes.ContainsKey(r.LastNode))
                AddPoint(ref r, r.Anchor, r.Samples[r.Samples.Count - 1], r.Radius, r.Clear);
            recorders.Remove(droneId);
        }

        private int PointAt(long anchor, Vector3D world, float radius, Vector3D from, bool hasFrom, Func<Vector3D, Vector3D, bool> clear,
                            double mergeFree = MERGE_FREE)
        {
            bool linkChecked;
            return PointAt(anchor, world, radius, from, hasFrom, clear, out linkChecked, mergeFree);
        }

        private readonly List<KeyValuePair<Node, double>> mergeCandidates = new List<KeyValuePair<Node, double>>();

        // The point at 'world' (merged into a close one of the same frame), -1: the anchor can't be resolved. A close one
        // within MERGE_FREE as it is; further (up to MERGE) only when the link that would end there is clear (from the last
        // point 'from', or from 'world' for a flight's first point). 'linkChecked': merged after that check.
        private int PointAt(long anchor, Vector3D world, float radius, Vector3D from, bool hasFrom, Func<Vector3D, Vector3D, bool> clear,
                            out bool linkChecked, double mergeFree = MERGE_FREE)
        {
            linkChecked = false;
            MatrixD m;
            if (!AnchorMatrix(anchor, out m)) return -1;
            Vector3D local = ToLocal(ref m, world);
            long minute = NowMinutes;
            Node best = null;
            mergeCandidates.Clear();
            foreach (var n in nodes.Values)
            {
                if (n.Anchor != anchor) continue;
                double d = Vector3D.DistanceSquared(n.Pos, local);
                if (d < MERGE * MERGE) mergeCandidates.Add(new KeyValuePair<Node, double>(n, d));
            }
            mergeCandidates.Sort((x, y) => x.Value.CompareTo(y.Value));
            for (int i = 0; i < mergeCandidates.Count && best == null; i++)
            {
                var c = mergeCandidates[i];
                if (c.Value <= mergeFree * mergeFree) { best = c.Key; break; }
                if (clear == null) break;
                Vector3D cw = ToWorld(ref m, c.Key.Pos);
                if (clear(hasFrom ? from : world, cw)) { best = c.Key; linkChecked = hasFrom; }
                if (i >= 2) break;   // the nearest few: each is a box sweep
            }
            mergeCandidates.Clear();
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

        private void Join(int a, int b, float radius, long player, long faction, bool custom)
        {
            Node na, nb;
            if (!nodes.TryGetValue(a, out na) || !nodes.TryGetValue(b, out nb)) return;
            Link l = Between(na, b, player, faction);
            long minute = NowMinutes;
            if (l == null)
            {
                l = new Link { Id = nextId++, A = a, B = b, Radius = radius, Score = 1, LastUsed = minute, Player = player, Faction = faction, Custom = custom };
                links[l.Id] = l;
                na.Links.Add(l.Id);
                nb.Links.Add(l.Id);
            }
            else
            {
                if (radius > l.Radius) l.Radius = radius;
                l.Score = Math.Min(l.Score + 1, 1000);
                l.LastUsed = minute;
                if (custom) l.Custom = true;
            }
            if (l.A == a) l.UsesAB++; else l.UsesBA++;
            dirty = true;
        }

        // The link between n and 'other' that belongs to this owner (the faction's when there is one, else the player's)
        private Link Between(Node n, int other, long player, long faction)
        {
            for (int i = 0; i < n.Links.Count; i++)
            {
                Link l;
                if (!links.TryGetValue(n.Links[i], out l) || (l.A != other && l.B != other)) continue;
                if (faction != 0 ? l.Faction == faction : l.Faction == 0 && l.Player == player) return l;
            }
            return null;
        }

        // A drone owned by 'player' (of 'faction', 0: none) may use the link: its faction's, or its owner's own
        private static bool Usable(Link l, long player, long faction)
        {
            return l.Faction != 0 ? l.Faction == faction || l.Player == player : l.Player == player;
        }

        private bool HasUsable(Node n)
        {
            for (int i = 0; i < n.Links.Count; i++)
            {
                Link l;
                if (links.TryGetValue(n.Links[i], out l) && Usable(l, qPlayer, qFaction) && (!qCustomOnly || routeLinkRefs.ContainsKey(l.Id))) return true;
            }
            return false;
        }

        /// <summary>The owner of block 'blockId' (a drone controller) and that player's faction (0: none / unknown).</summary>
        public static void OwnerOf(long blockId, out long player, out long faction)
        {
            player = 0;
            faction = 0;
            IMyEntity e;
            var b = blockId != 0 && MyAPIGateway.Entities.TryGetEntityById(blockId, out e) ? e as VRage.Game.ModAPI.IMyCubeBlock : null;
            if (b == null) return;
            player = b.OwnerId;
            var f = player != 0 && MyAPIGateway.Session != null && MyAPIGateway.Session.Factions != null ? MyAPIGateway.Session.Factions.TryGetPlayerFaction(player) : null;
            if (f != null) faction = f.FactionId;
        }

        private void EvictOldest()
        {
            Node oldest = null;
            foreach (var n in nodes.Values)
                if ((oldest == null || n.LastUsed < oldest.LastUsed) && !OnRoute(n)) oldest = n;
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
            public int Via = -1;      // the link from Parent (-1: a join)
            public bool Closed;
        }
        private long qPlayer, qFaction;   // the asking drone's owner and faction (FindRoute)
        private bool qCustomOnly;         // FindRoute over players' custom routes only
        private readonly HashSet<int> skipLinks = new HashSet<int>();   // blocked as the drone is turned now: not this plan
        /// <summary>The last FindRoute's legs (links, the join from the start) clear only in another of the drone's
        /// profiles (PathfindingManager.LegProfile): from, to (hull-centre points, world) and that attitude. The legs fly
        /// in it. Recorded attitudes aren't used: a route is shared by drones of every shape (2026-11-14).</summary>
        public readonly List<LegHint> LegHints = new List<LegHint>();
        public struct LegHint { public Vector3D A, B, Fwd, Up; }
        private readonly List<int> pathLinks = new List<int>();
        private const int START = -1, GOAL = -2;
        private readonly Dictionary<int, SearchNode> search = new Dictionary<int, SearchNode>();
        private readonly List<KeyValuePair<int, double>> joinStart = new List<KeyValuePair<int, double>>();
        private readonly List<KeyValuePair<int, double>> joinGoal = new List<KeyValuePair<int, double>>();
        private readonly Dictionary<int, MatrixD> joinStartAtt = new Dictionary<int, MatrixD>();   // joins clear only in another profile
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
                              double maxMs = 0, bool customOnly = false)
        {
            why = null;
            if (nodes.Count == 0) { why = "no remembered points"; return false; }
            UsedRouteBerth = false;
            if (customOnly && routes.Count == 0) { why = "no custom routes"; return false; }
            qCustomOnly = customOnly;
            findClock.Restart();
            findMaxMs = maxMs;
            NewQuery();
            OwnerOf(q.Owner, out qPlayer, out qFaction);
            skipLinks.Clear();
            LegHints.Clear();
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
                bool reduced = false;
                LegHints.Clear();
                // Each link in the attitude of the leg before it where the hull's box fits; in a tight spot, the profile
                // that does (PathfindingManager.LegProfile)
                MatrixD prevAtt = q.Controller;
                prevAtt.Translation = Vector3D.Zero;
                MatrixD ja;
                Vector3D j0;
                if (path.Count > 2 && joinStartAtt.TryGetValue(path[1], out ja) && World(nodes[path[1]], out j0))
                {
                    LegHints.Add(new LegHint { A = start, B = j0, Fwd = ja.Forward, Up = ja.Up });
                    prevAtt = ja;
                }
                for (int i = 1; i + 2 < path.Count && ok; i++)
                {
                    if (TimeUp) { why = "time budget"; return false; }
                    Vector3D a, b;
                    Node na = nodes[path[i]], nb = nodes[path[i + 1]];
                    if (!World(na, out a) || !World(nb, out b)) { ok = false; break; }
                    Vector3D hit;
                    IMyEntity e;
                    // A custom route's link: checked at the clearance it was recorded with (the player flew it there),
                    // whatever the plan's (halved down to it per failed plan)
                    int linkId = pathLinks[i + 1];
                    var lq = q;
                    if (linkId >= 0 && routeLinkRefs.ContainsKey(linkId) && (lq.Berth <= 0 || lq.Berth > Clearances.FINE)) { lq.Berth = Clearances.FINE; reduced = true; }
                    MatrixD att;
                    bool tight;
                    if (pm.LegProfile(ref lq, a, b, 0.5, ref prevAtt, true, out att, out tight))
                    {
                        if (!SameAttitude(ref att, ref q.Controller)) LegHints.Add(new LegHint { A = a, B = b, Fwd = att.Forward, Up = att.Up });
                        if (!SameAttitude(ref att, ref prevAtt) && Log.DebugEnabled) Log.Debug("Pathfinding memory: link {0} -> {1} ({2:F0} m): a tight spot, flown on another side of the hull", na.Id, nb.Id, Vector3D.Distance(a, b));
                        prevAtt = att;
                        continue;
                    }
                    if (pm.SegmentBlocked(ref lq, ref prevAtt, a, b, 0.5, out hit, out e))   // (what is in the way, for the log / the rule below)
                    {
                        var hb = pm.LastHitBlock;
                        string where = hb != null ? string.Format(" ({0} at {1}, {2:F1} m along)", PathfindingManager.BlockName(hb), hb.Position, Vector3D.Distance(a, hit)) : "";
                        Link bl;
                        if (linkId >= 0 && links.TryGetValue(linkId, out bl))
                        {
                            // A player's custom route past something that moves (a drone parked by it): the player flew
                            // past it - the hull alone, no berth (the look-ahead watches it on the way, slow: tight)
                            var bg0 = e != null ? e.GetTopMostParent() as VRage.Game.ModAPI.IMyCubeGrid : null;
                            if (bg0 != null && !bg0.IsStatic && routeLinkRefs.ContainsKey(linkId))
                            {
                                var bare = lq;
                                bare.Berth = Clearances.TOUCH;
                                if (pm.LegProfile(ref bare, a, b, 0.5, ref prevAtt, true, out att, out tight))
                                {
                                    if (!SameAttitude(ref att, ref q.Controller)) LegHints.Add(new LegHint { A = a, B = b, Fwd = att.Forward, Up = att.Up });
                                    if (Log.DebugEnabled) Log.Debug("Pathfinding memory: link {0} -> {1} ({2:F0} m) past {3} (it moves): the hull alone", na.Id, nb.Id, Vector3D.Distance(a, b), bg0.DisplayName);
                                    prevAtt = att;
                                    continue;
                                }
                            }
                            // Left out of this plan only: never dropped here (a custom route's goes with its route; a
                            // shared one when unused long enough - a blocked one isn't used)
                            skipLinks.Add(linkId);
                            if (Log.DebugEnabled) Log.Debug("Pathfinding memory: link {0} -> {1} ({2:F0} m) blocked by {3}{4}: not used this time", na.Id, nb.Id, Vector3D.Distance(a, b), e != null ? e.DisplayName : "?", where);
                        }
                        ok = false;
                    }
                }
                if (!ok) continue;
                UsedRouteBerth = reduced;
                MarkUsed(pathLinks);
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

        private static bool SameAttitude(ref MatrixD a, ref MatrixD b)
        {
            return Vector3D.Dot(a.Forward, b.Forward) > 0.985 && Vector3D.Dot(a.Up, b.Up) > 0.985;
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
                if (!HasUsable(n)) continue;   // only other owners' links there
                result.Add(new KeyValuePair<int, double>(n.Id, d));
            }
            result.Sort((x, y) => x.Value.CompareTo(y.Value));
        }

        // The nearest points (from 'near') joined to 'p' by a checked straight line: kept with the length
        private void Joinable(PathfindingManager pm, ref PathQuery q, Vector3D p, bool fromStart, List<KeyValuePair<int, double>> result)
        {
            result.Clear();
            if (fromStart) joinStartAtt.Clear();
            for (int i = 0; i < near.Count && i < JOIN_TRIES && result.Count < JOIN_KEEP && !TimeUp; i++)
            {
                Vector3D w;
                if (!World(nodes[near[i].Key], out w)) continue;
                Vector3D hit;
                IMyEntity e;
                bool blocked;
                if (fromStart)
                {
                    // (from where the drone is, as it is turned; a tight spot: another profile, turned to in place)
                    MatrixD now = q.Controller, att;
                    bool tight;
                    blocked = !pm.LegProfile(ref q, p, w, 0.5, ref now, true, out att, out tight);
                    if (!blocked && !SameAttitude(ref att, ref now)) joinStartAtt[near[i].Key] = att;
                }
                else blocked = pm.SegmentBlocked(ref q, ref q.Controller, w, p, 0.5, out hit, out e, !q.LooseGoal);
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
            open.Enqueue(s, Vector3D.Distance(start, goal) * CUSTOM_COST);
            float need = (float)(hull - 0.5);
            while (open.Count > 0)
            {
                var cur = open.Dequeue();
                if (cur.Closed) continue;
                cur.Closed = true;
                if (cur.Id == GOAL)
                {
                    var path = new List<int>();
                    pathLinks.Clear();
                    for (var c = cur; c != null; c = c.Parent) { path.Add(c.Id); pathLinks.Add(c.Via); }
                    path.Reverse();
                    pathLinks.Reverse();   // pathLinks[i]: the link into path[i]
                    return path;
                }
                Vector3D here = cur.Id == START ? start : Pos(cur.Id);
                if (cur.Id == START)
                {
                    for (int i = 0; i < joinStart.Count; i++) Relax(open, cur, joinStart[i].Key, joinStart[i].Value, goal, -1);
                    continue;
                }
                for (int i = 0; i < joinGoal.Count; i++)
                    if (joinGoal[i].Key == cur.Id) Relax(open, cur, GOAL, joinGoal[i].Value, goal, -1);
                Node n = nodes[cur.Id];
                for (int i = 0; i < n.Links.Count; i++)
                {
                    Link l;
                    if (!links.TryGetValue(n.Links[i], out l) || l.Radius < need || !Usable(l, qPlayer, qFaction) || skipLinks.Contains(l.Id)
                        || (qCustomOnly && !routeLinkRefs.ContainsKey(l.Id))) continue;
                    int other = l.A == cur.Id ? l.B : l.A;
                    Node no;
                    Vector3D w;
                    if (!nodes.TryGetValue(other, out no) || !World(no, out w)) continue;
                    Relax(open, cur, other, Vector3D.Distance(here, w) * (l.Custom ? CUSTOM_COST : 1), goal, l.Id);
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

        // (the heuristic scaled by CUSTOM_COST: a custom route's links cost less than their length, it must not overestimate)
        private void Relax(FastPriorityQueue<SearchNode> open, SearchNode from, int id, double cost, Vector3D goal, int via)
        {
            double g = from.G + cost;
            SearchNode sn;
            if (search.TryGetValue(id, out sn))
            {
                if (sn.Closed || g >= sn.G) return;
                sn.G = g;
                sn.Parent = from;
                sn.Via = via;
                double h = id == GOAL ? 0 : Vector3D.Distance(Pos(id), goal) * CUSTOM_COST;
                if (open.Contains(sn)) open.UpdatePriority(sn, g + h);
                else open.Enqueue(sn, g + h);
                return;
            }
            if (open.Count >= open.MaxSize) return;
            sn = new SearchNode { Id = id, G = g, Parent = from, Via = via };
            search[id] = sn;
            open.Enqueue(sn, g + (id == GOAL ? 0 : Vector3D.Distance(Pos(id), goal) * CUSTOM_COST));
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
                if (minute - l.LastUsed > maxAgeMinutes && !routeLinkRefs.ContainsKey(l.Id)) scratch.Add(l.Id);
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
                        w.Write(l.Player); w.Write(l.Faction); w.Write(l.Custom);
                        w.Write(l.Fwd.X); w.Write(l.Fwd.Y); w.Write(l.Fwd.Z); w.Write(l.Up.X); w.Write(l.Up.Y); w.Write(l.Up.Z);
                    }
                    w.Write(nextRouteId);
                    w.Write(routes.Count);
                    foreach (var r in routes.Values)
                    {
                        w.Write(r.Id); w.Write(r.Player); w.Write(r.Faction); w.Write(r.DroneId); w.Write(r.Name ?? "");
                        w.Write(r.Created); w.Write(r.LastUsed);
                        w.Write(r.Nodes.Count);
                        for (int i = 0; i < r.Nodes.Count; i++) w.Write(r.Nodes[i]);
                        w.Write(r.Links.Count);
                        for (int i = 0; i < r.Links.Count; i++) w.Write(r.Links[i]);
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
                    if (version < 2 || version > FILE_VERSION) {
                        Log.Warning("Pathfinding memory: file version {0}, expected {1}: not loaded", version, FILE_VERSION);
                        return;
                    }
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
                        l.Player = r.ReadInt64();
                        l.Faction = r.ReadInt64();
                        l.Custom = r.ReadBoolean();
                        if (version >= 3)
                        {
                            l.Fwd = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            l.Up = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        }
                        Node a, b;
                        if (!nodes.TryGetValue(l.A, out a) || !nodes.TryGetValue(l.B, out b)) continue;
                        links[l.Id] = l;
                        a.Links.Add(l.Id);
                        b.Links.Add(l.Id);
                    }
                    if (version >= 4)
                    {
                        nextRouteId = r.ReadInt32();
                        int rc = r.ReadInt32();
                        for (int i = 0; i < rc; i++)
                        {
                            var cr = new CustomRoute { Id = r.ReadInt32(), Player = r.ReadInt64(), Faction = r.ReadInt64(), DroneId = r.ReadInt64(),
                                                       Name = r.ReadString(), Created = r.ReadInt64(), LastUsed = r.ReadInt64() };
                            int k = r.ReadInt32();
                            for (int j = 0; j < k; j++) { int id = r.ReadInt32(); if (nodes.ContainsKey(id)) cr.Nodes.Add(id); }
                            k = r.ReadInt32();
                            for (int j = 0; j < k; j++) { int id = r.ReadInt32(); if (links.ContainsKey(id)) cr.Links.Add(id); }
                            if (cr.Links.Count == 0) continue;
                            routes[cr.Id] = cr;
                            for (int j = 0; j < cr.Links.Count; j++) RefLink(cr.Links[j], 1);
                        }
                    }
                }
                if (Log.DebugEnabled) Log.Debug("Pathfinding memory: {0} points, {1} links loaded", nodes.Count, links.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Pathfinding memory: load failed ({0}): starting empty", ex.Message);
                nodes.Clear();
                links.Clear();
                routes.Clear();
                routeLinkRefs.Clear();
            }
        }
        #endregion
    }
}
