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
    /// The antenna network, as Space Engineers relays it (TODO/13). Bases A - B - C, where A reaches B and B reaches C
    /// but A doesn't reach C, are one network: a message from C gets to A through B.
    ///  - Data: one <see cref="AntennaReg"/> per antenna id (no block references). Blocks hand theirs to the queue with
    ///    every send / read that gets past the queue's early exits (<see cref="Register"/>): a mobile grid's entry
    ///    is updated (if it moved a little, nothing happens), a static grid's is left alone. Nothing else refreshes
    ///    entries but the fallback: every FULL_REBUILD_TICKS the whole world is scanned (antennas no block registered,
    ///    entries of grids that sent / read nothing, static antennas whose range or state changed, antennas gone).
    ///    Saved with the message queue.
    ///  - Nodes: logical grid groups (rotors, pistons, connectors: one node) with a working, broadcasting radio antenna or
    ///    a connected laser antenna. Drones are nodes like any other grid (they relay, as in the game).
    ///  - Edges (worked out when a reach is asked for, not stored): radio N -> M when an antenna of M is inside the radius
    ///    of an antenna of N (directed); laser N <-> M when a laser antenna of N is connected to one of M (both ways, any
    ///    distance).
    ///  - Security: an edge only counts for a sender whose owner is the owner of both antennas or in their faction; owner
    ///    0 never. Nothing relays through other players' antennas.
    /// Reach per (node, owner) is a BFS, cached until an antenna came or went / changed state (or, after an entry
    /// moved, REACH_TTL_TICKS). Server only.
    /// </summary>
    public sealed class RelayGraph
    {
        #region Timing
        public const int FULL_REBUILD_TICKS = 60 * 60;     // fallback: the world scanned again
        private const int REACH_TTL_TICKS = 2 * 60;        // a cached reach is used this long after entries moved
        private const int GROUPS_REFRESH_TICKS = 2 * 60;   // grid groups re-read this long after one changed
        private const int RESCAN_TICKS = 30 * 60;          // a grid's blocks walked again for antennas
        #endregion
        private const double LASER_MATCH = 10.0;           // m: a laser's target coordinates vs the other laser's position
        private const double MOVE_EPS_SQ = 25.0 * 25.0;    // an entry moves in the graph when it is this far (m^2) from its stored place

        private readonly Dictionary<long, AntennaReg> antennas = new Dictionary<long, AntennaReg>();   // by antenna id
        private readonly Dictionary<long, long> gridGroup = new Dictionary<long, long>();               // grid id -> its logical group's key (lowest grid id)
        private readonly Dictionary<long, List<AntennaReg>> byGroup = new Dictionary<long, List<AntennaReg>>();
        private readonly List<AntennaReg> radios = new List<AntennaReg>();
        private readonly List<AntennaReg> lasers = new List<AntennaReg>();
        private sealed class ReachEntry { public HashSet<long> Set; public int Frame, Topo, Dyn; }
        private readonly Dictionary<KeyValuePair<long, long>, ReachEntry> reach = new Dictionary<KeyValuePair<long, long>, ReachEntry>();
        private readonly Queue<long> bfs = new Queue<long>();
        private readonly Dictionary<long, bool> allowed = new Dictionary<long, bool>();
        private readonly List<IMyCubeGrid> grids = new List<IMyCubeGrid>();
        private readonly HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
        private readonly HashSet<long> seen = new HashSet<long>();
        private readonly List<long> idBuffer = new List<long>();
        private readonly AntennaReg scratch = new AntennaReg();
        private bool indexDirty = true;
        private int topo, dyn;                                  // bumped by status changes / by movement
        private int fullFrame = int.MinValue / 2, groupsFrame;
        private bool groupsDirty;

        // Group events (docking joins a drone's group to the base's)
        private readonly HashSet<IMyGridGroupData> watched = new HashSet<IMyGridGroupData>();
        private readonly Action<IMyGridGroupData, IMyCubeGrid, IMyGridGroupData> onGroupChanged;
        private readonly Action<IMyGridGroupData> onGroupReleased;

        // Antennas per grid, found by walking its blocks: done again every RESCAN_TICKS only (walking every grid's blocks
        // at every scan costs too much on big servers). Their state (on, range, position, link) is read live.
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

        public RelayGraph()
        {
            onGroupChanged = (g, grid, other) => { groupsDirty = true; };
            onGroupReleased = g => { watched.Remove(g); groupsDirty = true; };
        }

        public int Count { get { return antennas.Count; } }

        #region Registration
        /// <summary>
        /// A block's antenna, as it is now. New: added. Known: updated unless both the entry and this are static; a change
        /// of status (working, owner, link, static or not) rebuilds the node index, a move of 25 m or more only ages the
        /// cached reaches. An antenna that is not working is no part of the network: not added, or dropped.
        /// </summary>
        public void Register(AntennaReg incoming)
        {
            if (incoming == null) return;
            AntennaReg cur;
            if (!antennas.TryGetValue(incoming.AntennaId, out cur))
            {
                if (!incoming.Working) return;
                cur = incoming.Clone();
                cur.Registered = true;
                antennas[cur.AntennaId] = cur;
                topo++;
                indexDirty = true;
                if (Log.DebugEnabled)
                    Log.Debug("VNet relay: antenna {0} '{1}' on '{2}' registered ({3:F0} m{4}), {5} antenna(s)", cur.AntennaId, cur.Name, cur.GridName, cur.Range,
                              cur.GridIsStatic ? ", static" : "", antennas.Count);
                return;
            }
            cur.Registered = true;
            if (cur.GridIsStatic && incoming.GridIsStatic && incoming.Working == cur.Working) return;   // static: no updates
            Apply(cur, incoming);
        }

        /// <summary>The antenna is off / gone (a block found it so): out of the network until a block registers it again or a scan finds it.</summary>
        public void Deregister(long antennaId)
        {
            if (!antennas.Remove(antennaId)) return;
            topo++;
            indexDirty = true;
        }

        private void Apply(AntennaReg cur, AntennaReg inc)
        {
            bool status = cur.Working != inc.Working || cur.OwnerId != inc.OwnerId || cur.GridIsStatic != inc.GridIsStatic
                          || cur.GridId != inc.GridId || cur.Kind != inc.Kind || cur.LinkedId != inc.LinkedId;
            bool moved = cur.Range != inc.Range || Vector3D.DistanceSquared(cur.Pos, inc.Pos) > MOVE_EPS_SQ;
            if (!status && !moved) return;
            bool registered = cur.Registered;
            long group = cur.GroupKey;
            cur.CopyFrom(inc);
            cur.Registered = registered || inc.Registered;
            cur.GroupKey = group;
            if (status) { topo++; indexDirty = true; }
            else dyn++;
        }
        #endregion

        #region Refresh
        /// <summary>Every tick (server): groups after a change, mobile entries, the fallback rebuild.</summary>
        public void Update(int frame)
        {
            if (groupsDirty && frame - groupsFrame >= GROUPS_REFRESH_TICKS) RefreshGroups(frame);
            if (frame - fullFrame >= FULL_REBUILD_TICKS) { FullRebuild(frame); return; }
        }

        private void RefreshGroups(int frame)
        {
            groupsDirty = false;
            groupsFrame = frame;
            gridGroup.Clear();
            indexDirty = true;
            topo++;
        }

        // The world scanned for antennas (also the ones no block registered), every entry read again, the gone ones dropped
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
                for (int i = 0; i < ga.Radios.Count; i++)
                {
                    var a = ga.Radios[i];
                    if (a.Closed || a.CubeGrid != grid || !a.IsWorking || !a.EnableBroadcasting || a.OwnerId == 0) continue;
                    AntennaReg cur;
                    if (antennas.TryGetValue(a.EntityId, out cur)) cur.Refresh(a);
                    else antennas[a.EntityId] = AntennaReg.Make(a);
                    seen.Add(a.EntityId);
                }
                for (int i = 0; i < ga.Lasers.Count; i++)
                {
                    var l = ga.Lasers[i];
                    if (l.Closed || l.CubeGrid != grid || !l.IsWorking || l.OwnerId == 0 || l.Status != Sandbox.ModAPI.Ingame.MyLaserAntennaStatus.Connected) continue;
                    AntennaReg cur;
                    if (antennas.TryGetValue(l.EntityId, out cur)) cur.Refresh(l);
                    else antennas[l.EntityId] = AntennaReg.Make(l);
                    seen.Add(l.EntityId);
                }
            }
            entities.Clear();
            // Grids gone since
            staleGrids.Clear();
            foreach (var kv in gridAntennas) if (kv.Value.Seen != frame) staleGrids.Add(kv.Key);
            for (int i = 0; i < staleGrids.Count; i++) gridAntennas.Remove(staleGrids[i]);
            staleGrids.Clear();
            // Antennas not found working any more. (A block's antenna built after its grid was walked isn't in the scan
            // yet: it stays if the block is there and working.)
            idBuffer.Clear();
            foreach (var kv in antennas)
            {
                if (seen.Contains(kv.Key)) continue;
                IMyEntity e;
                var radio = kv.Value.Registered && kv.Value.Kind == AntennaKind.Radio && MyAPIGateway.Entities.TryGetEntityById(kv.Key, out e) ? e as IMyRadioAntenna : null;
                if (radio != null && AntennaUtil.Alive(radio) && radio.IsWorking && radio.EnableBroadcasting && radio.OwnerId != 0) kv.Value.Refresh(radio);
                else idBuffer.Add(kv.Key);
            }
            for (int i = 0; i < idBuffer.Count; i++) antennas.Remove(idBuffer[i]);
            idBuffer.Clear();
            seen.Clear();

            gridGroup.Clear();
            groupsDirty = false;
            groupsFrame = frame;
            indexDirty = true;
            topo++;
            watch.Stop();
            int mobile = 0, registered = 0, lz = 0;
            foreach (var a in antennas.Values) { if (!a.GridIsStatic) mobile++; if (a.Registered) registered++; if (a.Kind == AntennaKind.Laser) lz++; }
            string summary = string.Format("{0} antenna(s) ({1} registered, {2} mobile, {3} laser)", antennas.Count, registered, mobile, lz);
            if (summary != lastSummary && Log.DebugEnabled)
            {
                lastSummary = summary;
                Log.Debug("VNet relay: {0} scanned ({1:F2} ms)", summary, watch.Elapsed.TotalMilliseconds);
            }
        }
        #endregion

        #region Nodes
        // A grid's logical group (rotors, pistons, connectors) as a key: its lowest grid id. 0: the grid isn't in the world.
        private long GroupOf(long gridId)
        {
            if (gridId == 0) return 0;
            long key;
            if (gridGroup.TryGetValue(gridId, out key)) return key;
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(gridId, out e) ? e as IMyCubeGrid : null;
            if (grid == null || grid.MarkedForClose) return 0;
            var group = grid.GetGridGroup(GridLinkTypeEnum.Logical);
            if (group == null)
            {
                gridGroup[gridId] = gridId;
                return gridId;
            }
            grids.Clear();
            group.GetGrids(grids);
            key = gridId;
            for (int g = 0; g < grids.Count; g++) if (grids[g].EntityId < key) key = grids[g].EntityId;
            for (int g = 0; g < grids.Count; g++) gridGroup[grids[g].EntityId] = key;
            grids.Clear();
            if (watched.Add(group))
            {
                group.OnGridAdded += onGroupChanged;
                group.OnGridRemoved += onGroupChanged;
                group.OnReleased += onGroupReleased;
            }
            return key;
        }

        private void EnsureIndex()
        {
            if (!indexDirty) return;
            indexDirty = false;
            byGroup.Clear();
            radios.Clear();
            lasers.Clear();
            foreach (var reg in antennas.Values)
            {
                reg.GroupKey = 0;
                if (!reg.Working || reg.OwnerId == 0) continue;
                long key = GroupOf(reg.GridId);
                if (key == 0) continue;
                reg.GroupKey = key;
                List<AntennaReg> list;
                if (!byGroup.TryGetValue(key, out list)) { list = new List<AntennaReg>(); byGroup[key] = list; }
                list.Add(reg);
                if (reg.Kind == AntennaKind.Laser) lasers.Add(reg); else radios.Add(reg);
            }
        }
        #endregion

        #region Reach
        /// <summary>Can a message from 'from' (sent by 'owner') reach 'to' through the network? Same node: yes.</summary>
        public bool Reaches(IMyCubeGrid from, IMyCubeGrid to, long owner)
        {
            return from != null && to != null && Reaches(from.EntityId, to.EntityId, owner);
        }

        public bool Reaches(long fromGrid, long toGrid, long owner)
        {
            EnsureIndex();
            long a = GroupOf(fromGrid), b = GroupOf(toGrid);
            if (a == 0 || b == 0 || !byGroup.ContainsKey(a) || !byGroup.ContainsKey(b)) return false;
            if (a == b) return true;
            return ReachSet(a, owner).Contains(b);
        }

        private HashSet<long> ReachSet(long start, long owner)
        {
            int frame = MyAPIGateway.Session.GameplayFrameCounter;
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
                List<AntennaReg> list;
                if (!byGroup.TryGetValue(g, out list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (!AllowedFor(owner, a.OwnerId)) continue;
                    if (a.Kind == AntennaKind.Radio)
                    {
                        Vector3D pa = a.Pos;
                        double r2 = a.Range * a.Range;
                        for (int j = 0; j < radios.Count; j++)
                        {
                            var b = radios[j];
                            if (b.GroupKey == g || set.Contains(b.GroupKey)) continue;
                            if (Vector3D.DistanceSquared(pa, b.Pos) > r2) continue;
                            if (!AllowedFor(owner, b.OwnerId) || (a.OwnerId != b.OwnerId && !AnchorDirectory.IsAllowed(a.OwnerId, b.OwnerId))) continue;
                            set.Add(b.GroupKey);
                            bfs.Enqueue(b.GroupKey);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < lasers.Count; j++)
                        {
                            var b = lasers[j];
                            if (b.GroupKey == g || set.Contains(b.GroupKey)) continue;
                            if (!LaserLinked(a, b)) continue;
                            if (!AllowedFor(owner, b.OwnerId) || (a.OwnerId != b.OwnerId && !AnchorDirectory.IsAllowed(a.OwnerId, b.OwnerId))) continue;
                            set.Add(b.GroupKey);
                            bfs.Enqueue(b.GroupKey);
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
        private static bool LaserLinked(AntennaReg a, AntennaReg b)
        {
            if (a.LinkedId != 0 || b.LinkedId != 0) return a.LinkedId == b.AntennaId || b.LinkedId == a.AntennaId;
            return Vector3D.DistanceSquared(a.Target.ToVector3D(), b.Pos) <= LASER_MATCH * LASER_MATCH
                || Vector3D.DistanceSquared(b.Target.ToVector3D(), a.Pos) <= LASER_MATCH * LASER_MATCH;
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
        /// Is 'position' (a player) inside the range of an antenna of 'owner' / their faction whose node reaches 'to'?
        /// 'via': that antenna's name.
        /// </summary>
        public bool Covers(Vector3D position, IMyCubeGrid to, long owner, out string via)
        {
            via = null;
            if (to == null) return false;
            EnsureIndex();
            long target = GroupOf(to.EntityId);
            if (target == 0 || !byGroup.ContainsKey(target)) return false;
            for (int i = 0; i < radios.Count; i++)
            {
                var r = radios[i];
                if (!AnchorDirectory.IsAllowed(owner, r.OwnerId)) continue;
                if (Vector3D.DistanceSquared(r.Pos, position) > r.Range * r.Range) continue;
                if (r.GroupKey != target && !ReachSet(r.GroupKey, owner).Contains(target)) continue;
                via = string.Format("'{0}' on '{1}'", r.Name, r.GridName);
                return true;
            }
            return false;
        }
        #endregion

        #region Save
        /// <summary>World save: the entries (the graph itself is worked out from them).</summary>
        public List<AntennaReg> Snapshot()
        {
            return new List<AntennaReg>(antennas.Values);
        }

        /// <summary>World load: the saved entries back, the world scan due in FULL_REBUILD_TICKS instead of at once.</summary>
        public void Restore(List<AntennaReg> saved, int frame)
        {
            if (saved == null) return;
            for (int i = 0; i < saved.Count; i++)
                if (saved[i] != null && !antennas.ContainsKey(saved[i].AntennaId)) antennas[saved[i].AntennaId] = saved[i];
            fullFrame = frame;
            groupsFrame = frame;
            indexDirty = true;
            topo++;
            Log.Info("VNet relay: {0} antenna(s) restored from the save", antennas.Count);
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
            antennas.Clear();
            gridGroup.Clear();
            byGroup.Clear();
            radios.Clear();
            lasers.Clear();
            reach.Clear();
            gridAntennas.Clear();
            indexDirty = true;
        }
    }
}
