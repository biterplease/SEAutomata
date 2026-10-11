using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.VirtualNetwork
{
    /// <summary>
    /// The antenna network, as Space Engineers relays it (TODO/13). Bases A - B - C, where A reaches B and B reaches C
    /// but A doesn't reach C, are one network: a message from C gets to A through B.
    ///  - Nodes: logical grid groups (rotors, pistons, connectors: one node) with a working, broadcasting radio antenna or
    ///    a connected laser antenna. Drones are nodes like any other grid (they relay, as in the game).
    ///  - Edges: radio N -> M when an antenna of M is inside the radius of an antenna of N (directed); laser N <-> M when
    ///    a laser antenna of N is connected to one of M (both ways, any distance).
    ///  - Security: an edge only counts for a sender whose owner is the owner of both antennas or in their faction; owner
    ///    0 never. Nothing relays through other players' antennas.
    /// Rebuilt every REBUILD_TICKS (antennas move with drones, links come and go); reach per (node, owner) is computed
    /// on demand (BFS) and cached until the next rebuild. Server only.
    /// </summary>
    public sealed class RelayGraph
    {
        #region Timing
        public const int REBUILD_TICKS = 120;
        #endregion
        private const double LASER_MATCH = 10.0;   // m: a laser's target coordinates vs the other laser's position

        private sealed class Edge
        {
            public int To;
            public long OwnerA, OwnerB;   // the two antennas' owners (an edge per owner pair)
        }
        private sealed class Node
        {
            public readonly List<IMyRadioAntenna> Radios = new List<IMyRadioAntenna>();
            public readonly List<IMyLaserAntenna> Lasers = new List<IMyLaserAntenna>();
            public readonly List<Edge> Out = new List<Edge>();
            public string Name;
        }

        private readonly List<Node> nodes = new List<Node>();
        private readonly Dictionary<long, int> gridNode = new Dictionary<long, int>();   // grid entity id -> node
        private readonly Dictionary<IMyGridGroupData, int> groupNode = new Dictionary<IMyGridGroupData, int>();
        private readonly Dictionary<KeyValuePair<int, long>, HashSet<int>> reach = new Dictionary<KeyValuePair<int, long>, HashSet<int>>();
        private readonly List<IMyCubeGrid> grids = new List<IMyCubeGrid>();
        private readonly HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
        private readonly Queue<int> bfs = new Queue<int>();
        private int builtFrame = int.MinValue / 2;

        // Antennas per grid, found by walking its blocks: done again every RESCAN_TICKS only (walking every grid's
        // blocks every rebuild costs too much on big servers). Their state (on, range, position, link) is read live.
        #region Timing
        private const int RESCAN_TICKS = 30 * 60;
        #endregion
        private sealed class GridAntennas
        {
            public readonly List<IMyRadioAntenna> Radios = new List<IMyRadioAntenna>();
            public readonly List<IMyLaserAntenna> Lasers = new List<IMyLaserAntenna>();
            public int Scanned;
            public int Seen;
        }
        private readonly Dictionary<long, GridAntennas> gridAntennas = new Dictionary<long, GridAntennas>();
        private readonly List<long> staleGrids = new List<long>();
        private string lastSummary;

        public int Version { get; private set; }

        public void Update(int frame)
        {
            if (frame - builtFrame < REBUILD_TICKS) return;
            builtFrame = frame;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Rebuild();
            watch.Stop();
            Version++;
            int edges = 0;
            for (int i = 0; i < nodes.Count; i++) edges += nodes[i].Out.Count;
            string summary = string.Format("{0} node(s), {1} edge(s)", nodes.Count, edges);
            if (summary != lastSummary && Log.DebugEnabled)
            {
                lastSummary = summary;
                Log.Debug("VNet relay: {0} ({1:F2} ms)", summary, watch.Elapsed.TotalMilliseconds);
            }
        }

        private void Rebuild()
        {
            nodes.Clear();
            gridNode.Clear();
            groupNode.Clear();
            reach.Clear();
            entities.Clear();
            int frame = builtFrame;
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            foreach (var e in entities)
            {
                var grid = (IMyCubeGrid)e;
                if (grid.MarkedForClose || grid.Physics == null) continue;
                GridAntennas ga;
                if (!gridAntennas.TryGetValue(grid.EntityId, out ga)) { ga = new GridAntennas { Scanned = int.MinValue / 2 }; gridAntennas[grid.EntityId] = ga; }
                ga.Seen = frame;
                if (frame - ga.Scanned >= RESCAN_TICKS)
                {
                    ga.Scanned = frame;
                    ga.Radios.Clear();
                    ga.Lasers.Clear();
                    foreach (var a in grid.GetFatBlocks<IMyRadioAntenna>()) ga.Radios.Add(a);
                    foreach (var l in grid.GetFatBlocks<IMyLaserAntenna>()) ga.Lasers.Add(l);
                }
                Node node = null;
                for (int i = 0; i < ga.Radios.Count; i++)
                {
                    var a = ga.Radios[i];
                    if (!a.Closed && a.CubeGrid == grid && a.IsWorking && a.EnableBroadcasting && a.OwnerId != 0) (node ?? (node = NodeOf(grid))).Radios.Add(a);
                }
                for (int i = 0; i < ga.Lasers.Count; i++)
                {
                    var l = ga.Lasers[i];
                    if (!l.Closed && l.CubeGrid == grid && l.IsWorking && l.OwnerId != 0 && l.Status == Sandbox.ModAPI.Ingame.MyLaserAntennaStatus.Connected)
                        (node ?? (node = NodeOf(grid))).Lasers.Add(l);
                }
            }
            entities.Clear();
            // Grids gone since
            staleGrids.Clear();
            foreach (var kv in gridAntennas) if (kv.Value.Seen != frame) staleGrids.Add(kv.Key);
            for (int i = 0; i < staleGrids.Count; i++) gridAntennas.Remove(staleGrids[i]);
            staleGrids.Clear();

            for (int i = 0; i < nodes.Count; i++)
            {
                var ni = nodes[i];
                for (int j = 0; j < nodes.Count; j++)
                {
                    if (i == j) continue;
                    var nj = nodes[j];
                    // Radio: an antenna of j inside the range of one of i
                    for (int a = 0; a < ni.Radios.Count; a++)
                    {
                        var ra = ni.Radios[a];
                        Vector3D pa = ra.GetPosition();
                        double r2 = (double)ra.Radius * ra.Radius;
                        for (int b = 0; b < nj.Radios.Count; b++)
                        {
                            var rb = nj.Radios[b];
                            if (Vector3D.DistanceSquared(pa, rb.GetPosition()) > r2) continue;
                            if (!Drone.AnchorDirectory.IsAllowed(ra.OwnerId, rb.OwnerId)) continue;
                            AddEdge(ni, j, ra.OwnerId, rb.OwnerId);
                        }
                    }
                    // Laser: connected to a laser of j (the link works both ways: j adds its own edge back)
                    for (int a = 0; a < ni.Lasers.Count; a++)
                    {
                        var la = ni.Lasers[a];
                        for (int b = 0; b < nj.Lasers.Count; b++)
                        {
                            var lb = nj.Lasers[b];
                            if (Vector3D.DistanceSquared(la.TargetCoords, lb.GetPosition()) > LASER_MATCH * LASER_MATCH
                                && Vector3D.DistanceSquared(lb.TargetCoords, la.GetPosition()) > LASER_MATCH * LASER_MATCH) continue;
                            if (!Drone.AnchorDirectory.IsAllowed(la.OwnerId, lb.OwnerId)) continue;
                            AddEdge(ni, j, la.OwnerId, lb.OwnerId);
                        }
                    }
                }
            }
        }

        private Node NodeOf(IMyCubeGrid grid)
        {
            int index;
            var group = grid.GetGridGroup(GridLinkTypeEnum.Logical);
            if (group == null || !groupNode.TryGetValue(group, out index))
            {
                index = nodes.Count;
                nodes.Add(new Node { Name = grid.DisplayName });
                if (group != null)
                {
                    groupNode[group] = index;
                    grids.Clear();
                    group.GetGrids(grids);
                    for (int g = 0; g < grids.Count; g++) gridNode[grids[g].EntityId] = index;
                    grids.Clear();
                }
                gridNode[grid.EntityId] = index;
            }
            return nodes[index];
        }

        private static void AddEdge(Node from, int to, long ownerA, long ownerB)
        {
            for (int k = 0; k < from.Out.Count; k++)
            {
                var e = from.Out[k];
                if (e.To == to && e.OwnerA == ownerA && e.OwnerB == ownerB) return;
            }
            from.Out.Add(new Edge { To = to, OwnerA = ownerA, OwnerB = ownerB });
        }

        /// <summary>The node of a grid (its logical group), or -1 when it has no antenna in the graph (yet).</summary>
        public int NodeIndex(IMyCubeGrid grid)
        {
            int n;
            return grid != null && gridNode.TryGetValue(grid.EntityId, out n) ? n : -1;
        }

        /// <summary>Can a message from 'from' (sent by 'owner') reach 'to' through the network? Same node: yes.</summary>
        public bool Reaches(IMyCubeGrid from, IMyCubeGrid to, long owner)
        {
            int a = NodeIndex(from), b = NodeIndex(to);
            if (a < 0 || b < 0) return false;
            if (a == b) return true;
            return ReachSet(a, owner).Contains(b);
        }

        private HashSet<int> ReachSet(int start, long owner)
        {
            var key = new KeyValuePair<int, long>(start, owner);
            HashSet<int> set;
            if (reach.TryGetValue(key, out set)) return set;
            set = new HashSet<int> { start };
            bfs.Clear();
            bfs.Enqueue(start);
            while (bfs.Count > 0)
            {
                var n = nodes[bfs.Dequeue()];
                for (int k = 0; k < n.Out.Count; k++)
                {
                    var e = n.Out[k];
                    if (set.Contains(e.To)) continue;
                    if (!Drone.AnchorDirectory.IsAllowed(owner, e.OwnerA) || !Drone.AnchorDirectory.IsAllowed(owner, e.OwnerB)) continue;
                    set.Add(e.To);
                    bfs.Enqueue(e.To);
                }
            }
            reach[key] = set;
            return set;
        }

        /// <summary>
        /// Is 'position' (a player) inside the range of an antenna of 'owner' / their faction whose node reaches 'to'?
        /// 'via': that antenna's name.
        /// </summary>
        public bool Covers(Vector3D position, IMyCubeGrid to, long owner, out string via)
        {
            via = null;
            int target = NodeIndex(to);
            if (target < 0) return false;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                for (int a = 0; a < n.Radios.Count; a++)
                {
                    var r = n.Radios[a];
                    if (!Drone.AnchorDirectory.IsAllowed(owner, r.OwnerId)) continue;
                    if (Vector3D.DistanceSquared(r.GetPosition(), position) > (double)r.Radius * r.Radius) continue;
                    if (i != target && !ReachSet(i, owner).Contains(target)) continue;
                    via = string.Format("'{0}' on '{1}'", r.CustomName, n.Name);
                    return true;
                }
            }
            return false;
        }

        public void Clear()
        {
            nodes.Clear();
            gridNode.Clear();
            groupNode.Clear();
            reach.Clear();
            gridAntennas.Clear();
        }
    }
}
