using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Drone;
using Automata.Util.Logging;
using Automata.Util;

namespace Automata.VirtualNetwork
{
    /// <summary>
    /// The antenna network, as Space Engineers relays it (TODO/13), kept per grid. Bases A - B - C, where A reaches B and B
    /// reaches C but A doesn't reach C, are one network: a message from C gets to A through B.
    ///  - Data: one <see cref="GridAntennaReg"/> per grid id (plain data, no block references), shared by every block of
    ///    the grid. The graph refreshes it itself: when a block asks for it (<see cref="Touch"/>, at most every
    ///    REFRESH_TICKS) and in the fallback world scan (FULL_REBUILD_TICKS: grids no block asked for, grids and antennas
    ///    gone). Saved with the message queue.
    ///  - Nodes: mechanically connected grids (rotors, pistons, hinges) are one group, its key the grid with the most
    ///    blocks. A group is a node when it has a working radio antenna or a connected laser antenna. Of its radio
    ///    antennas only the one with the largest range counts; every laser antenna does (each links elsewhere).
    ///  - Edges (worked out when a reach is asked for, not stored): radio N <-> M when the two radio antennas are inside
    ///    each other's range (distance within the smaller of the two ranges, so the edge is symmetric); laser N <-> M when a laser antenna of N is
    ///    connected to one of M (both ways, any distance).
    ///  - Without antennas: blocks of the same group reach each other. A drone docked by a connector also reaches the
    ///    groups connected to it (and so their node) - the drone's antennas are switched off while docked.
    ///  - Security: an edge only counts for a sender whose owner is the owner of both antennas or in their faction; owner
    ///    0 never. Nothing relays through other players' antennas.
    /// Reach per (node, owner) is a BFS, cached until an antenna came or went / changed state (or, after a grid moved,
    /// REACH_TTL_TICKS). Server only.
    /// </summary>
    public sealed class RelayGraph
    {
        #region Timing
        public const int FULL_REBUILD_TICKS = 60 * 60;     // fallback: the world scanned again
        private const int REFRESH_TICKS = 60;              // a grid's entry is read again this often (when a block asks)
        private const int REACH_TTL_TICKS = 2 * 60;        // a cached reach is used this long after grids moved
        private const int GROUPS_REFRESH_TICKS = 2 * 60;   // groups re-read this long after one changed
        private const int RESCAN_TICKS = 30 * 60;          // a grid's blocks walked again for antennas
        #endregion
        private const double LASER_MATCH = 10.0;           // m: a laser's target coordinates vs the other laser's position
        private const double MOVE_EPS_SQ = 100.0 * 100.0;  // a grid moves in the graph when it is this far (m^2) from where it was last counted

        // What the graph keeps of a grid besides its entry: the antenna blocks found on it (state is read live from them)
        private sealed class GridState
        {
            public readonly List<IMyRadioAntenna> Radios = new List<IMyRadioAntenna>();
            public readonly List<IMyLaserAntenna> Lasers = new List<IMyLaserAntenna>();
            public int Scanned = int.MinValue / 2, Refreshed = int.MinValue / 2, Seen;
            public Vector3DData Anchor;
            public bool AnchorSet;
        }

        private sealed class LaserRef
        {
            public long Id;
            public LaserAntennaData Data;
            public Node Node;
        }

        // A group of grids that has a working antenna
        private sealed class Node
        {
            public long Key;
            public RadioAntennaData Radio;      // the working radio antenna with the largest range (null: none)
            public string RadioGrid;
            public readonly List<LaserRef> Lasers = new List<LaserRef>();
        }

        private readonly Dictionary<long, GridAntennaReg> regs = new Dictionary<long, GridAntennaReg>();   // by grid id
        private readonly Dictionary<long, GridState> states = new Dictionary<long, GridState>();
        private readonly Dictionary<long, long> gridGroup = new Dictionary<long, long>();              // grid id -> its mechanical group's key (the grid with the most blocks)
        private readonly Dictionary<long, List<long>> dockedOf = new Dictionary<long, List<long>>();   // grid id -> keys of the other groups its connectors hold
        private readonly Dictionary<long, Node> nodes = new Dictionary<long, Node>();
        private readonly List<Node> radioNodes = new List<Node>();
        private readonly List<Node> laserNodes = new List<Node>();
        private sealed class ReachEntry { public HashSet<long> Set; public int Frame, Topo, Dyn; }
        private readonly Dictionary<KeyValuePair<long, long>, ReachEntry> reach = new Dictionary<KeyValuePair<long, long>, ReachEntry>();
        private readonly Queue<long> bfs = new Queue<long>();
        private readonly Dictionary<long, bool> allowed = new Dictionary<long, bool>();
        private readonly HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
        private readonly HashSet<long> seen = new HashSet<long>();
        private readonly List<long> idBuffer = new List<long>();
        private readonly List<IMyCubeGrid> gridBuf = new List<IMyCubeGrid>();
        private readonly List<long> starts = new List<long>(), targets = new List<long>();
        private bool indexDirty = true;
        private int topo, dyn;                                  // bumped by status changes / by movement
        private int fullFrame = int.MinValue / 2, groupsFrame;
        private bool groupsDirty;
        private string lastSummary;

        // Group events (docking joins a drone's group to the base's; a rotor head attaching joins two)
        private readonly HashSet<IMyGridGroupData> watched = new HashSet<IMyGridGroupData>();
        private readonly Action<IMyGridGroupData, IMyCubeGrid, IMyGridGroupData> onGroupChanged;
        private readonly Action<IMyGridGroupData> onGroupReleased;

        public RelayGraph()
        {
            onGroupChanged = (g, grid, other) => { groupsDirty = true; };
            onGroupReleased = g => { watched.Remove(g); groupsDirty = true; };
        }

        /// <summary>Grids the graph knows (with or without antennas).</summary>
        public int Count { get { return regs.Count; } }

        private static int Frame { get { return MyAPIGateway.Session != null ? MyAPIGateway.Session.GameplayFrameCounter : 0; } }

        #region Registration
        /// <summary>
        /// The grid's entry (made if it is new), read again when it is REFRESH_TICKS old. A change of status (an antenna on /
        /// off / added / gone, range, link, owner, static or not) rebuilds the node index; a move of 100 m or more only ages
        /// the cached reaches. 'ownerHint': the asking block's owner, for a grid whose antennas tell none.
        /// </summary>
        public GridAntennaReg Touch(IMyCubeGrid grid, long ownerHint)
        {
            if (grid == null || grid.MarkedForClose) return null;
            GridAntennaReg reg;
            bool created = false;
            if (!regs.TryGetValue(grid.EntityId, out reg))
            {
                reg = new GridAntennaReg { GridId = grid.EntityId };
                regs[grid.EntityId] = reg;
                created = true;
            }
            reg.Registered = true;
            if (reg.OwnerId == 0 && ownerHint != 0) reg.OwnerId = ownerHint;
            var st = StateOf(grid.EntityId);
            int frame = Frame;
            if (created || frame - st.Refreshed >= REFRESH_TICKS) RefreshGrid(grid, reg, st, frame);
            return reg;
        }

        private GridState StateOf(long gridId)
        {
            GridState st;
            if (!states.TryGetValue(gridId, out st)) { st = new GridState(); states[gridId] = st; }
            return st;
        }

        // The grid's antenna blocks again when they were last found long ago or one of them is gone
        private static void FindAntennas(IMyCubeGrid grid, GridState st, int frame)
        {
            bool stale = frame - st.Scanned >= RESCAN_TICKS;
            for (int i = 0; i < st.Radios.Count && !stale; i++) stale = st.Radios[i].Closed || st.Radios[i].MarkedForClose;
            for (int i = 0; i < st.Lasers.Count && !stale; i++) stale = st.Lasers[i].Closed || st.Lasers[i].MarkedForClose;
            if (!stale) return;
            st.Scanned = frame;
            st.Radios.Clear();
            st.Lasers.Clear();
            foreach (var a in grid.GetFatBlocks<IMyRadioAntenna>()) st.Radios.Add(a);
            foreach (var l in grid.GetFatBlocks<IMyLaserAntenna>()) st.Lasers.Add(l);
        }

        private void RefreshGrid(IMyCubeGrid grid, GridAntennaReg reg, GridState st, int frame)
        {
            FindAntennas(grid, st, frame);
            st.Refreshed = frame;
            bool status = reg.Refresh(grid, st.Radios, st.Lasers);
            if (status)
            {
                topo++;
                indexDirty = true;
                if (Log.DebugEnabled) Log.Debug("VNet relay: grid {0} '{1}'{2}: {3} radio antenna(s), {4} laser antenna(s) ({5})", reg.GridId, reg.GridName,
                          reg.IsStatic ? " (static)" : "", reg.RadioAntennas.Count, reg.LaserAntennas.Count, reg.Working() ? "on the network" : "no working antenna");
                Vector3DData p0;
                if (reg.TryGetPosition(out p0)) { st.Anchor = p0; st.AnchorSet = true; }
                else st.AnchorSet = false;
                return;
            }
            Vector3DData p;
            if (!reg.TryGetPosition(out p)) return;
            if (!st.AnchorSet || Vector3DData.DistanceSquared(p, st.Anchor) > MOVE_EPS_SQ)
            {
                st.Anchor = p;
                st.AnchorSet = true;
                dyn++;
            }
        }
        #endregion

        #region Refresh
        /// <summary>Every tick (server): groups after a change, the fallback rebuild.</summary>
        public void Update(int frame)
        {
            if (groupsDirty && frame - groupsFrame >= GROUPS_REFRESH_TICKS) RefreshGroups(frame);
            if (frame - fullFrame >= FULL_REBUILD_TICKS) FullRebuild(frame);
        }

        private void RefreshGroups(int frame)
        {
            groupsDirty = false;
            groupsFrame = frame;
            gridGroup.Clear();
            dockedOf.Clear();
            indexDirty = true;
            topo++;
        }

        // The world scanned for grids with antennas (also the ones no block asked for), every entry read again, the gone ones dropped
        private void FullRebuild(int frame)
        {
            fullFrame = frame;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            seen.Clear();
            entities.Clear();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            foreach (var e in entities)
            {
                var grid = (IMyCubeGrid)e;
                if (grid.MarkedForClose || grid.Physics == null) continue;
                var st = StateOf(grid.EntityId);
                st.Seen = frame;
                seen.Add(grid.EntityId);
                GridAntennaReg reg;
                bool known = regs.TryGetValue(grid.EntityId, out reg);
                FindAntennas(grid, st, frame);
                if (!known && st.Radios.Count == 0 && st.Lasers.Count == 0) continue;   // nothing to say about it
                if (!known) { reg = new GridAntennaReg { GridId = grid.EntityId }; regs[grid.EntityId] = reg; }
                RefreshGrid(grid, reg, st, frame);
            }
            entities.Clear();
            // Gone grids; grids no block asked for that lost their antennas
            idBuffer.Clear();
            foreach (var kv in regs)
            {
                if (!seen.Contains(kv.Key)) idBuffer.Add(kv.Key);
                else if (!kv.Value.Registered && kv.Value.RadioAntennas.Count == 0 && kv.Value.LaserAntennas.Count == 0) idBuffer.Add(kv.Key);
            }
            for (int i = 0; i < idBuffer.Count; i++)
            {
                regs.Remove(idBuffer[i]);
                topo++;
                indexDirty = true;
            }
            idBuffer.Clear();
            foreach (var kv in states) if (!seen.Contains(kv.Key)) idBuffer.Add(kv.Key);
            for (int i = 0; i < idBuffer.Count; i++) states.Remove(idBuffer[i]);
            idBuffer.Clear();
            seen.Clear();

            gridGroup.Clear();
            dockedOf.Clear();
            groupsDirty = false;
            groupsFrame = frame;
            indexDirty = true;
            topo++;
            watch.Stop();
            int mobile = 0, onNet = 0, lz = 0;
            foreach (var r in regs.Values)
            {
                if (!r.IsStatic) mobile++;
                if (r.Working()) onNet++;
                lz += r.LaserAntennas.Count;
            }
            string summary = string.Format("{0} grid(s) ({1} on the network, {2} mobile, {3} laser antenna(s))", regs.Count, onNet, mobile, lz);
            if (summary != lastSummary && Log.DebugEnabled)
            {
                lastSummary = summary;
                Log.Debug("VNet relay: {0} scanned ({1:F2} ms)", summary, watch.Elapsed.TotalMilliseconds);
            }
        }
        #endregion

        #region Groups and nodes
        private static IMyCubeGrid FindGrid(long gridId)
        {
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(gridId, out e) ? e as IMyCubeGrid : null;
            return grid == null || grid.MarkedForClose ? null : grid;
        }

        // A grid's mechanical group (rotors, pistons, hinges) as a key: the grid in it with the most blocks (ties: the lowest
        // id). 0: the grid isn't in the world.
        private long GroupOf(long gridId)
        {
            if (gridId == 0) return 0;
            long key;
            if (gridGroup.TryGetValue(gridId, out key)) return key;
            var grid = FindGrid(gridId);
            if (grid == null) return 0;
            gridBuf.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, gridBuf);
            if (gridBuf.Count == 0) gridBuf.Add(grid);
            IMyCubeGrid lead = gridBuf[0];
            if (gridBuf.Count > 1)
            {
                int leadBlocks = BlockCount(lead);
                for (int g = 1; g < gridBuf.Count; g++)
                {
                    var c = gridBuf[g];
                    int n = BlockCount(c);
                    if (n > leadBlocks || (n == leadBlocks && c.EntityId < lead.EntityId)) { lead = c; leadBlocks = n; }
                }
            }
            key = lead.EntityId;
            for (int g = 0; g < gridBuf.Count; g++) gridGroup[gridBuf[g].EntityId] = key;
            gridBuf.Clear();
            gridGroup[gridId] = key;
            // Docking (a connector) and attaching (a rotor head) both change the grid's logical group
            var logical = grid.GetGridGroup(GridLinkTypeEnum.Logical);
            if (logical != null && watched.Add(logical))
            {
                logical.OnGridAdded += onGroupChanged;
                logical.OnGridRemoved += onGroupChanged;
                logical.OnReleased += onGroupReleased;
            }
            return key;
        }

        // Blocks on a grid (the ModAPI has no count): only asked for groups of two or more grids, when the groups are worked out again
        private readonly List<IMySlimBlock> blockBuf = new List<IMySlimBlock>();
        private int BlockCount(IMyCubeGrid grid)
        {
            blockBuf.Clear();
            grid.GetBlocks(blockBuf);
            int n = blockBuf.Count;
            blockBuf.Clear();
            return n;
        }

        // The keys of the other groups held to this grid by connectors (what a docked drone can use): empty when none
        private List<long> DockedOf(long gridId)
        {
            List<long> keys;
            if (dockedOf.TryGetValue(gridId, out keys)) return keys;
            keys = new List<long>();
            dockedOf[gridId] = keys;
            var grid = FindGrid(gridId);
            if (grid == null) return keys;
            long own = GroupOf(gridId);
            var logical = grid.GetGridGroup(GridLinkTypeEnum.Logical);
            if (logical == null) return keys;
            var list = new List<IMyCubeGrid>();
            logical.GetGrids(list);
            for (int i = 0; i < list.Count; i++)
            {
                long k = GroupOf(list[i].EntityId);
                if (k != 0 && k != own && !keys.Contains(k)) keys.Add(k);
            }
            return keys;
        }

        private void EnsureIndex()
        {
            if (!indexDirty) return;
            indexDirty = false;
            nodes.Clear();
            radioNodes.Clear();
            laserNodes.Clear();
            foreach (var reg in regs.Values)
            {
                reg.GroupKey = 0;
                if (!reg.Working()) continue;
                long key = GroupOf(reg.GridId);
                if (key == 0) continue;
                reg.GroupKey = key;
                Node n;
                if (!nodes.TryGetValue(key, out n)) { n = new Node { Key = key }; nodes[key] = n; }
                foreach (var r in reg.RadioAntennas.Values)
                    if (r.Working && (n.Radio == null || r.Range > n.Radio.Range)) { n.Radio = r; n.RadioGrid = reg.GridName; }
                foreach (var kv in reg.LaserAntennas)
                    if (kv.Value.Working) n.Lasers.Add(new LaserRef { Id = kv.Key, Data = kv.Value, Node = n });
            }
            foreach (var n in nodes.Values)
            {
                if (n.Radio != null) radioNodes.Add(n);
                if (n.Lasers.Count > 0) laserNodes.Add(n);
            }
        }

        /// <summary>The grid's group has a working antenna (it is a node of the network).</summary>
        public bool OnNetwork(IMyCubeGrid grid)
        {
            if (grid == null) return false;
            EnsureIndex();
            long key = GroupOf(grid.EntityId);
            return key != 0 && nodes.ContainsKey(key);
        }

        /// <summary>Are the two grids connected by rotors, pistons or hinges (or the same grid)?</summary>
        public bool SameGroup(long gridA, long gridB)
        {
            long a = GroupOf(gridA);
            return a != 0 && a == GroupOf(gridB);
        }
        #endregion

        #region Reach
        /// <summary>Can a message from 'from' (sent by 'owner') reach 'to' through the network? Same group: yes, antennas or not.</summary>
        public bool Reaches(IMyCubeGrid from, IMyCubeGrid to, long owner, bool dockedFrom = false, bool dockedTo = false)
        {
            return from != null && to != null && Reaches(from.EntityId, to.EntityId, owner, dockedFrom, dockedTo);
        }

        /// <summary>
        /// 'dockedFrom' / 'dockedTo': that end is a drone, which also counts as part of the groups its connectors hold (docked
        /// at a base it uses the base's node, or talks to blocks on the grid it is docked to without any antenna).
        /// </summary>
        public bool Reaches(long fromGrid, long toGrid, long owner, bool dockedFrom = false, bool dockedTo = false)
        {
            EnsureIndex();
            long a = GroupOf(fromGrid), b = GroupOf(toGrid);
            if (a == 0 || b == 0) return false;
            if (a == b) return true;
            if (ReachFrom(a, b, owner)) return true;
            if (!dockedFrom && !dockedTo) return false;
            starts.Clear();
            targets.Clear();
            starts.Add(a);
            targets.Add(b);
            if (dockedFrom) starts.AddRange(DockedOf(fromGrid));
            if (dockedTo) targets.AddRange(DockedOf(toGrid));
            bool ok = false;
            for (int i = 0; i < starts.Count && !ok; i++)
                for (int j = 0; j < targets.Count && !ok; j++)
                    ok = starts[i] == targets[j] || ReachFrom(starts[i], targets[j], owner);
            starts.Clear();
            targets.Clear();
            return ok;
        }

        private bool ReachFrom(long start, long target, long owner)
        {
            return nodes.ContainsKey(start) && nodes.ContainsKey(target) && ReachSet(start, owner).Contains(target);
        }

        private HashSet<long> ReachSet(long start, long owner)
        {
            int frame = Frame;
            var key = new KeyValuePair<long, long>(start, owner);
            ReachEntry entry;
            if (reach.TryGetValue(key, out entry) && entry.Topo == topo && (entry.Dyn == dyn || frame - entry.Frame < REACH_TTL_TICKS)) return entry.Set;
            var set = entry != null ? entry.Set : new HashSet<long>();
            set.Clear();
            set.Add(start);
            allowed.Clear();
            bfs.Clear();
            bfs.Enqueue(start);
            while (bfs.Count > 0)
            {
                long g = bfs.Dequeue();
                Node n;
                if (!nodes.TryGetValue(g, out n)) continue;
                if (n.Radio != null && AllowedFor(owner, n.Radio.OwnerId))
                {
                    Vector3D pa = n.Radio.Position.ToVector3D();
                    double nr = n.Radio.Range;
                    for (int j = 0; j < radioNodes.Count; j++)
                    {
                        var m = radioNodes[j];
                        if (set.Contains(m.Key)) continue;
                        double reach = Math.Min(nr, m.Radio.Range);      // both ranges must reach
                        if (Vector3D.DistanceSquared(pa, m.Radio.Position.ToVector3D()) > reach * reach) continue;
                        if (!AllowedFor(owner, m.Radio.OwnerId) || (n.Radio.OwnerId != m.Radio.OwnerId && !AnchorDirectory.IsAllowed(n.Radio.OwnerId, m.Radio.OwnerId))) continue;
                        set.Add(m.Key);
                        bfs.Enqueue(m.Key);
                    }
                }
                for (int i = 0; i < n.Lasers.Count; i++)
                {
                    var a = n.Lasers[i];
                    if (!AllowedFor(owner, a.Data.OwnerId)) continue;
                    for (int j = 0; j < laserNodes.Count; j++)
                    {
                        var m = laserNodes[j];
                        if (set.Contains(m.Key)) continue;
                        for (int k = 0; k < m.Lasers.Count && !set.Contains(m.Key); k++)
                        {
                            var b = m.Lasers[k];
                            if (!LaserLinked(a, b)) continue;
                            if (!AllowedFor(owner, b.Data.OwnerId) || (a.Data.OwnerId != b.Data.OwnerId && !AnchorDirectory.IsAllowed(a.Data.OwnerId, b.Data.OwnerId))) continue;
                            set.Add(m.Key);
                            bfs.Enqueue(m.Key);
                        }
                    }
                }
            }
            if (entry == null) { entry = new ReachEntry { Set = set }; reach[key] = entry; }
            entry.Frame = frame;
            entry.Topo = topo;
            entry.Dyn = dyn;
            return set;
        }

        // The game's own link (the laser's Other, set while connected) decides; the target coordinates (the other laser's
        // head) only when neither end reports it
        private static bool LaserLinked(LaserRef a, LaserRef b)
        {
            if (a.Data.LinkedId != 0 || b.Data.LinkedId != 0) return a.Data.LinkedId == b.Id || b.Data.LinkedId == a.Id;
            return Vector3DData.DistanceSquared(a.Data.TargetCoordinates, b.Data.Position) <= LASER_MATCH * LASER_MATCH
                || Vector3DData.DistanceSquared(b.Data.TargetCoordinates, a.Data.Position) <= LASER_MATCH * LASER_MATCH;
        }

        private bool AllowedFor(long owner, long other)
        {
            bool ok;
            if (allowed.TryGetValue(other, out ok)) return ok;
            ok = AnchorDirectory.IsAllowed(owner, other);
            allowed[other] = ok;
            return ok;
        }

        /// <summary>
        /// Is 'position' (a player) inside the range of the radio antenna of a node of 'owner' / their faction that reaches 'to'?
        /// 'via': that antenna's name.
        /// </summary>
        public bool Covers(Vector3D position, IMyCubeGrid to, long owner, out string via)
        {
            via = null;
            if (to == null) return false;
            EnsureIndex();
            long target = GroupOf(to.EntityId);
            if (target == 0 || !nodes.ContainsKey(target)) return false;
            for (int i = 0; i < radioNodes.Count; i++)
            {
                var n = radioNodes[i];
                if (!AnchorDirectory.IsAllowed(owner, n.Radio.OwnerId)) continue;
                if (Vector3D.DistanceSquared(n.Radio.Position.ToVector3D(), position) > n.Radio.Range * n.Radio.Range) continue;
                if (n.Key != target && !ReachSet(n.Key, owner).Contains(target)) continue;
                via = string.Format("'{0}' on '{1}'", n.Radio.Name, n.RadioGrid);
                return true;
            }
            return false;
        }
        #endregion

        #region Save
        /// <summary>World save: the entries (the graph itself is worked out from them).</summary>
        public List<GridAntennaReg> Snapshot()
        {
            return new List<GridAntennaReg>(regs.Values);
        }

        /// <summary>World load: the saved entries back, the world scan due in FULL_REBUILD_TICKS instead of at once.</summary>
        public void Restore(List<GridAntennaReg> saved, int frame)
        {
            if (saved == null) return;
            for (int i = 0; i < saved.Count; i++)
            {
                var r = saved[i];
                if (r == null || r.GridId == 0 || regs.ContainsKey(r.GridId)) continue;
                if (r.RadioAntennas == null) r.RadioAntennas = new Dictionary<long, RadioAntennaData>();
                if (r.LaserAntennas == null) r.LaserAntennas = new Dictionary<long, LaserAntennaData>();
                regs[r.GridId] = r;
            }
            fullFrame = frame;
            groupsFrame = frame;
            indexDirty = true;
            topo++;
            Log.Info("VNet relay: {0} grid(s) restored from the save", regs.Count);
        }
        #endregion

        public void Clear()
        {
            foreach (var g in watched)
            {
                g.OnGridAdded -= onGroupChanged;
                g.OnGridRemoved -= onGroupChanged;
                g.OnReleased -= onGroupReleased;
            }
            watched.Clear();
            regs.Clear();
            states.Clear();
            gridGroup.Clear();
            dockedOf.Clear();
            nodes.Clear();
            radioNodes.Clear();
            laserNodes.Clear();
            reach.Clear();
            indexDirty = true;
        }
    }
}
