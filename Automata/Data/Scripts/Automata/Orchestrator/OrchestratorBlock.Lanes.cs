using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;
using Automata.VirtualNetwork;

namespace Automata.Orchestrator
{
    /// <summary>
    /// The orchestrator's view of its network's flight lanes (TODO/20, section 6): the Active lanes list in the
    /// terminal (delete, resume, visualize, clear all) and the drawing of the selected lane pair. The lanes live on the
    /// server (PathfindingManager.Lanes); the list and the drawn geometry reach players through the synced settings,
    /// a player's commands come back the same way (LaneCommand).
    /// </summary>
    public partial class OrchestratorBlock
    {

        private const int LANE_LIST_EVERY = 30;   // UpdateBeforeSimulation10 calls (5 s)
        internal const float LANE_DASH = 5f, LANE_GAP = 5f, LANE_CRAWL = 3f, LANE_THICKNESS = 0.35f;
        private static readonly MyStringId LaneMaterial = MyStringId.GetOrCompute("Square");

        private int laneListCounter, laneListVersion = -1, laneListMinute, baseLaneCounter;
        private const int BASE_LANES_EVERY = 6 * 60 * 5;   // UpdateBeforeSimulation10 calls (5 min) between base lane passes
        private const int BASE_LANES_MAX = 24;             // bases a pass traces lanes between, at most
        private const double NEAR_EQUILATERAL = 0.75;      // a triangle of bases whose shortest side is this much of its longest keeps all three lanes
        private readonly HashSet<string> appliedObstructions = new HashSet<string>();
        private string laneCommandDone = "";

        // Client side: the player's selection, the drawing, the armed "Clear all"
        private int laneSelected;
        private bool laneDraw;
        private int laneDrawStart, laneDrawWanted;
        private DateTime laneDrawUntil;   // real time: frames stretch with a low simulation speed
        private int laneClearArmed = int.MinValue / 2;
        private readonly List<IMyGps> laneGps = new List<IMyGps>();
        private int laneGpsNumber;

        #region Server
        private void UpdateLanes10()
        {
            var lanes = Pathfinding.PathfindingManager.Instance != null ? Pathfinding.PathfindingManager.Instance.Lanes : null;
            if (lanes == null) return;
            // A player's command (once)
            string cmd = settings.LaneCommand ?? "";
            if (cmd.Length > 0 && cmd != laneCommandDone)
            {
                laneCommandDone = cmd;
                RunLaneCommand(lanes, cmd);
                laneListVersion = -1;
            }
            if (--baseLaneCounter <= 0) { baseLaneCounter = BASE_LANES_EVERY; TraceBaseLanes(lanes); }
            lanes.SetSafeLanes(Entity.EntityId, settings.SafeLanes);
            // On a change, and once a minute anyway ("last N min ago")
            if (++laneListCounter < LANE_LIST_EVERY) return;
            laneListCounter = 0;
            if (laneListVersion == lanes.Version && ++laneListMinute < 12) return;
            laneListMinute = 0;
            laneListVersion = lanes.Version;
            var list = lanes.Describe(Entity.EntityId);
            bool same = settings.Lanes != null && list.Count == settings.Lanes.Count;
            for (int i = 0; same && i < list.Count; i++) same = list[i] == settings.Lanes[i];
            if (same) return;
            settings.Lanes = list;
            SettingsEdited();
        }

        private void RunLaneCommand(Pathfinding.FlightLanes lanes, string cmd)
        {
            var parts = cmd.Split('|');
            if (parts.Length < 3) return;
            int number;
            int.TryParse(parts[2], out number);
            switch (parts[1])
            {
                case "delete":
                    if (lanes.Delete(Entity.EntityId, number)) AddLaneEvent(Loc.F(MyStrings.Orchestrator_LaneLDeleted, number));
                    break;
                case "resume":
                    if (lanes.Resume(Entity.EntityId, number)) AddLaneEvent(Loc.F(MyStrings.Orchestrator_LaneLResumedCheckedAgain, number));
                    break;
                case "clear":
                    AddLaneEvent(Loc.F(MyStrings.Orchestrator_LanePairCleared, lanes.ClearAll(Entity.EntityId)));
                    break;
                case "draw":
                {
                    var ab = new List<Vector3D>();
                    var ba = new List<Vector3D>();
                    var upAB = new List<Vector3D>();
                    var upBA = new List<Vector3D>();
                    bool pending;
                    string nab, nba;
                    double width, height;
                    settings.LaneDrawAB = new List<double>();
                    settings.LaneDrawBA = new List<double>();
                    settings.LaneDrawUpAB = new List<double>();
                    settings.LaneDrawUpBA = new List<double>();
                    settings.LaneDrawNumber = number;
                    if (lanes.Geometry(Entity.EntityId, number, ab, ba, upAB, upBA, out pending, out nab, out nba, out width, out height))
                    {
                        Flatten(ab, settings.LaneDrawAB);
                        Flatten(ba, settings.LaneDrawBA);
                        Flatten(upAB, settings.LaneDrawUpAB);
                        Flatten(upBA, settings.LaneDrawUpBA);
                        settings.LaneDrawPending = pending;
                        settings.LaneDrawNameAB = nab;
                        settings.LaneDrawNameBA = nba;
                        settings.LaneDrawWidth = width;
                        settings.LaneDrawHeight = height;
                    }
                    SettingsEdited();
                    break;
                }
            }
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: lane command {1} {2}", entityId, parts[1], number);
        }

        // An auction with a location: its place joins the lane graph with the bases (TraceBaseLanes' rule: lanes to the bases
        // and places it is a neighbour of); a place met before keeps its lanes (the base lane pass upholds them). A place that
        // is a base: nothing (the base lanes). Combat orders fly in formation, not in lanes. A scout job: an order lane from
        // the nearest node (dropped if its order finds nothing), its spot not a node
        private void TraceJobLane(JobAnnouncement ja)
        {
            var lanes = Pathfinding.PathfindingManager.Instance != null ? Pathfinding.PathfindingManager.Instance.Lanes : null;
            if (lanes == null || ja.Type == JobType.DesignatorEngage || ja.Type == JobType.DesignatorEscort) return;
            if (block.CubeGrid == null || !block.CubeGrid.IsStatic)   // a moving orchestrator has no base to trace from
            {
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: no lane for job {1}: its grid isn't static", entityId, ja.JobId);
                return;
            }
            Vector3D to = ja.PositionData.ToVector3D();
            if (to.LengthSquared() < 1) return;
            long id;
            bool natural, isBase;
            if (!lanes.Locate(to, out id, out natural, out isBase) || isBase) return;
            bool scout = ja.Type == JobType.DesignatorScoutArea || ja.Type == JobType.DesignatorScoutPoint;
            if (scout)
            {
                var all = LaneNodes(false);
                int near = -1;
                double best = double.MaxValue;
                for (int j = 0; j < all.Count; j++)
                {
                    if (natural && all[j].Natural) continue;
                    double d = Vector3D.DistanceSquared(all[j].Pos, to);
                    if (d < best) { best = d; near = j; }
                }
                if (near >= 0) lanes.Trace(Entity.EntityId, all[near].Pos, to, ja.JobId);
                return;
            }
            WorkPlace wp;
            bool known = workPlaces.TryGetValue(id, out wp);
            if (!known) { wp = new WorkPlace(); workPlaces[id] = wp; }
            wp.Pos = to;
            wp.Natural = natural;
            wp.Seen = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            if (known) return;
            TrimWorkPlaces(lanes);
            var nodes = LaneNodes(false);
            int me = -1;
            for (int i = 0; i < nodes.Count && me < 0; i++) if (nodes[i].Id == id) me = i;
            if (me < 0) return;
            for (int j = 0; j < nodes.Count; j++)
                if (j != me && Linked(nodes, me, j)) lanes.Trace(Entity.EntityId, nodes[j].Pos, to);
        }

        // The lane graph's nodes and lanes: a relative neighbourhood graph over this orchestrator's bases (the player's on its
        // relay network) and its job places - two get a lane unless a third is closer to both of them than they are to each
        // other (it lies between them: the way is through it), but a near-equilateral triangle (NEAR_EQUILATERAL) keeps all
        // three. A far-off base gets the lane to its nearest neighbour only; a hotspot between two bases takes their lane's
        // place. Two natural places (asteroids, hotspots) get no lane. A jump-off point (a node with a lane out of its
        // cluster) also gets a lane from every node of its cluster. Lanes between two nodes that no longer qualify go (a
        // base built in between, a place used again)
        private void TraceBaseLanes(Pathfinding.FlightLanes lanes)
        {
            TrimWorkPlaces(lanes);
            var nodes = LaneNodes(true);
            if (nodes.Count < 2) return;
            var ids = new HashSet<long>();
            var keep = new HashSet<int>();
            int c = nodes.Count;
            for (int i = 0; i < c; i++)
            {
                ids.Add(nodes[i].Id);
                if (nodes[i].Members != null) for (int m = 0; m < nodes[i].Members.Count; m++) ids.Add(nodes[i].Members[m]);   // (their old lanes go)
            }
            // The neighbourhood graph; a node with a lane out of its cluster (longer than JUMP_OFF_RANGE) is a jump-off point
            var linked = new bool[c, c];
            var jumpOff = new bool[c];
            double far2 = JUMP_OFF_RANGE * JUMP_OFF_RANGE;
            for (int i = 0; i < c; i++)
                for (int j = i + 1; j < c; j++)
                {
                    if (!Linked(nodes, i, j)) continue;
                    linked[i, j] = linked[j, i] = true;
                    if (Vector3D.DistanceSquared(nodes[i].Pos, nodes[j].Pos) > far2) jumpOff[i] = jumpOff[j] = true;
                }
            // Every node of a jump-off point's cluster (within JUMP_OFF_RANGE, room for a lane) gets a lane to it: the way
            // out of the cluster is one lane away from anywhere in it
            int added = 0;
            for (int i = 0; i < c; i++)
            {
                if (!jumpOff[i]) continue;
                for (int j = 0; j < c; j++)
                {
                    if (j == i || linked[i, j] || (nodes[i].Natural && nodes[j].Natural)) continue;
                    double d2 = Vector3D.DistanceSquared(nodes[i].Pos, nodes[j].Pos);
                    if (d2 > far2 || !Fits(nodes[i], nodes[j], d2)) continue;
                    linked[i, j] = linked[j, i] = true;
                    added++;
                }
            }
            if (added > 0 && Log.DebugEnabled) Log.Debug("Orchestrator {0}: {1} lane(s) to jump-off points (nodes with a lane out of their cluster)", entityId, added);
            for (int i = 0; i < c; i++)
                for (int j = i + 1; j < c; j++)
                {
                    if (!linked[i, j]) continue;
                    int n = lanes.Trace(Entity.EntityId, nodes[i].Pos, nodes[j].Pos);
                    if (n != 0) keep.Add(n);
                }
            lanes.DropBaseLanes(Entity.EntityId, ids, keep);
        }

        // m: a lane longer than this leaves its cluster; its ends are jump-off points, linked to every node of their cluster
        private const double JUMP_OFF_RANGE = Pathfinding.FlightLanes.LONG_LANE;

        // A lane graph node: a base (its grid) or a job's place
        private struct LaneNode
        {
            public long Id;            // the lane place's id (a base: its grid's)
            public Vector3D Pos;
            public bool Natural;       // asteroid / hotspot: no lane to another natural place
            public bool Base;
            public double Radius;      // a base: its grid's bounding sphere (lanes end outside it)
            public List<long> Members; // the grids of bases merged into one (FlightLanes.BASE_MERGE_RANGE), null: none
        }

        // Job places met in auctions (place id ->): nodes of the lane graph with the bases, while used
        private sealed class WorkPlace { public Vector3D Pos; public bool Natural; public long Seen; }
        private readonly Dictionary<long, WorkPlace> workPlaces = new Dictionary<long, WorkPlace>();
        private const int WORK_PLACES_MAX = 24;            // job places in the lane graph, at most (the most recently used)

        private static int Root(int[] group, int i)
        {
            while (group[i] != i) i = group[i] = group[group[i]];
            return i;
        }

        // Places not used for the lanes' expiry time go; WORK_PLACES_MAX at most (the oldest go)
        private void TrimWorkPlaces(Pathfinding.FlightLanes lanes)
        {
            long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            var gone = new List<long>();
            foreach (var kv in workPlaces) if (minute - kv.Value.Seen > lanes.ExpiryMinutes) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++) workPlaces.Remove(gone[i]);
            while (workPlaces.Count > WORK_PLACES_MAX)
            {
                long oldest = 0, seen = long.MaxValue;
                foreach (var kv in workPlaces) if (kv.Value.Seen < seen) { seen = kv.Value.Seen; oldest = kv.Key; }
                workPlaces.Remove(oldest);
            }
        }

        private List<LaneNode> LaneNodes(bool fresh)
        {
            var nodes = new List<LaneNode>();
            var bases = LaneBases(fresh);
            // Bases BASE_MERGE_RANGE or closer (chains of them too) are one node, as FlightLanes.FindPlace makes them one
            // place: centred between them, its radius round all of them, the place's id. No lanes among them (too short
            // anyway), one set of lanes for all of them
            var grids = new List<IMyCubeGrid>();
            for (int i = 0; i < bases.Count; i++) if (!bases[i].MarkedForClose) grids.Add(bases[i]);
            var group = new int[grids.Count];
            for (int i = 0; i < grids.Count; i++) group[i] = i;
            double merge2 = Pathfinding.FlightLanes.BASE_MERGE_RANGE * Pathfinding.FlightLanes.BASE_MERGE_RANGE;
            for (int i = 0; i < grids.Count; i++)
                for (int j = i + 1; j < grids.Count; j++)
                {
                    if (Vector3D.DistanceSquared(grids[i].WorldVolume.Center, grids[j].WorldVolume.Center) > merge2) continue;
                    int a = Root(group, i), b = Root(group, j);
                    if (a != b) group[b] = a;
                }
            for (int r = 0; r < grids.Count; r++)
            {
                if (Root(group, r) != r) continue;
                int count = 0;
                Vector3D centre = Vector3D.Zero;
                for (int i = 0; i < grids.Count; i++)
                    if (Root(group, i) == r) { centre += grids[i].WorldVolume.Center; count++; }
                if (count == 1)
                {
                    nodes.Add(new LaneNode { Id = grids[r].EntityId, Pos = grids[r].WorldVolume.Center, Base = true, Radius = grids[r].WorldVolume.Radius });
                    continue;
                }
                centre /= count;
                var node = new LaneNode { Pos = centre, Base = true, Members = new List<long>() };
                for (int i = 0; i < grids.Count; i++)
                {
                    if (Root(group, i) != r) continue;
                    node.Members.Add(grids[i].EntityId);
                    var o = grids[i].WorldVolume;
                    node.Radius = Math.Max(node.Radius, Vector3D.Distance(centre, o.Center) + o.Radius);
                }
                // The id the lanes use for it (FindPlace's: the lowest grid id of the group it finds there)
                long id;
                bool natural, isBase;
                var lanes = Pathfinding.PathfindingManager.Instance != null ? Pathfinding.PathfindingManager.Instance.Lanes : null;
                node.Id = lanes != null && lanes.Locate(centre, out id, out natural, out isBase) && isBase ? id : node.Members[0];
                if (fresh && Log.DebugEnabled)
                    Log.Debug("Orchestrator {0}: lane graph: {1} bases within {2:F0} m of each other are one node (between them, radius {3:F0} m)", entityId, count, Pathfinding.FlightLanes.BASE_MERGE_RANGE, node.Radius);
                nodes.Add(node);
            }
            foreach (var kv in workPlaces) nodes.Add(new LaneNode { Id = kv.Key, Pos = kv.Value.Pos, Natural = kv.Value.Natural });
            return nodes;
        }

        // Nodes i and j get a lane: not both natural, and no third node lies between them (closer to both than they are to
        // each other: squared distances, no roots) unless the three make a near-equilateral triangle. A natural node between
        // a natural one and another doesn't count (no lane from it to the natural one: no way through it), nor does one too
        // close to either end for a lane between them (FlightLanes.LaneFits: the way through it can't be drawn)
        private static bool Linked(List<LaneNode> nodes, int i, int j)
        {
            if (nodes[i].Natural && nodes[j].Natural) return false;
            Vector3D a = nodes[i].Pos, b = nodes[j].Pos;
            double ab = Vector3D.DistanceSquared(a, b);
            for (int k = 0; k < nodes.Count; k++)
            {
                if (k == i || k == j) continue;
                if (nodes[k].Natural && (nodes[i].Natural || nodes[j].Natural)) continue;
                Vector3D c = nodes[k].Pos;
                double ac = Vector3D.DistanceSquared(a, c), bc = Vector3D.DistanceSquared(b, c);
                if (ac >= ab || bc >= ab) continue;                                          // not between them
                if (Math.Min(ac, bc) >= NEAR_EQUILATERAL * NEAR_EQUILATERAL * ab) continue;   // near-equilateral
                if (!Fits(nodes[i], nodes[k], ac) || !Fits(nodes[k], nodes[j], bc)) continue;   // no lane through it
                return false;
            }
            return true;
        }

        // A lane between 'a' and 'b' ('d2': their squared distance) would be long enough to be drawn
        private static bool Fits(LaneNode a, LaneNode b, double d2)
        {
            return Pathfinding.FlightLanes.LaneFits(Math.Sqrt(d2), a.Base, a.Radius, b.Base, b.Radius);
        }

        // The player's bases (static grids with an Automata computer block) this orchestrator's relay network reaches (its
        // own grid first, if static); BASE_LANES_MAX at most. Found again with 'fresh' (the base lane pass) or after
        // LANE_BASES_TICKS (the job lanes use the last list: auctions come often)
        private List<IMyCubeGrid> laneBases;
        private int laneBasesFrame;
        private const int LANE_BASES_TICKS = 60 * 60;

        private List<IMyCubeGrid> LaneBases(bool fresh)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!fresh && laneBases != null && now - laneBasesFrame < LANE_BASES_TICKS) return laneBases;
            laneBasesFrame = now;
            var bases = laneBases = new List<IMyCubeGrid>();
            if (messaging == null || block.CubeGrid == null) return bases;
            if (block.CubeGrid.IsStatic) bases.Add(block.CubeGrid);
            for (int k = 0; k < 4 && bases.Count < BASE_LANES_MAX; k++)
            {
                int count = k == 0 ? Logistics.LogisticsComputerBlock.All.Count : k == 1 ? Construction.ConstructionComputerBlock.All.Count
                          : k == 2 ? Mining.MiningSurveyorBlock.All.Count : All.Count;
                for (int i = 0; i < count && bases.Count < BASE_LANES_MAX; i++)
                {
                    var logic = k == 0 ? (VRage.Game.Components.MyGameLogicComponent)Logistics.LogisticsComputerBlock.All[i]
                              : k == 1 ? Construction.ConstructionComputerBlock.All[i] : k == 2 ? (VRage.Game.Components.MyGameLogicComponent)Mining.MiningSurveyorBlock.All[i] : All[i];
                    var b = logic.Entity as IMyCubeBlock;
                    if (b == null || b.CubeGrid == null || !b.CubeGrid.IsStatic || bases.Contains(b.CubeGrid)) continue;
                    if (!Drone.AnchorDirectory.IsAllowed(block.OwnerId, b.OwnerId)) continue;
                    if (!messaging.Relays.Reaches(block.CubeGrid, b.CubeGrid, block.OwnerId)) continue;
                    bases.Add(b.CubeGrid);
                }
            }
            return bases;
        }

        // A drone's report (keepalive / job report) of lanes it found blocked: each redraws its lane once; of lanes it gave up
        // waiting at (enough of them close together: the lane grows); of lanes it flew clear end to end (trusted)
        private void ApplyLaneObstructions(JobStatusReport r)
        {
            if (r.LaneObstructions == null || r.LaneObstructions.Count == 0) return;
            var pm = Pathfinding.PathfindingManager.Instance;
            if (pm == null || pm.Lanes == null) return;
            if (appliedObstructions.Count > 512) appliedObstructions.Clear();
            for (int i = 0; i < r.LaneObstructions.Count; i++)
            {
                var o = r.LaneObstructions[i];
                if (o == null || !appliedObstructions.Add(r.DroneEntityId + ":" + o.Id)) continue;
                if (o.Kind == Pathfinding.FlightLanes.GAVE_UP)
                {
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: drone {1} gave up waiting at an entrance of lane L{2}", entityId, r.DroneEntityId, o.Lane);
                    pm.Lanes.GaveUp(Entity.EntityId, o.Lane, o.Id);
                    continue;
                }
                if (o.Kind == Pathfinding.FlightLanes.CLEAR)
                {
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: drone {1} flew lane L{2} clear end to end", entityId, r.DroneEntityId, o.Lane);
                    pm.Lanes.Verified(Entity.EntityId, o.Lane, o.Revision, pm);
                    continue;
                }
                var detour = new List<Vector3D>();
                if (o.Detour != null) for (int k = 0; k < o.Detour.Count; k++) detour.Add(o.Detour[k].ToVector3D());
                pm.Lanes.Obstructed(Entity.EntityId, o.Lane, o.AB, o.A.ToVector3D(), o.B.ToVector3D(), detour, o.What ?? "something", pm);
            }
        }

        private void AddLaneEvent(string text)
        {
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: {1}", entityId, text);
        }

        internal static void Flatten(List<Vector3D> pts, List<double> into)
        {
            for (int i = 0; i < pts.Count; i++) { into.Add(pts[i].X); into.Add(pts[i].Y); into.Add(pts[i].Z); }
        }
        #endregion

        #region Terminal (players)
        public List<string> Terminal_Lanes { get { return settings.Lanes ?? new List<string>(); } }

        public bool Terminal_SafeLanes
        {
            get { return settings.SafeLanes; }
            set { if (settings.SafeLanes == value) return; settings.SafeLanes = value; SettingsEdited(); }
        }
        public int Terminal_LaneSelected { get { return laneSelected; } set { laneSelected = value; } }

        private void SendLaneCommand(string what, int number)
        {
            settings.LaneCommand = DateTime.UtcNow.Ticks.ToString() + "|" + what + "|" + number;
            SettingsEdited();
        }

        public bool Terminal_DeleteLane()
        {
            if (laneSelected <= 0) return false;
            SendLaneCommand("delete", laneSelected);
            if (laneDrawWanted == laneSelected) StopLaneDraw();
            laneSelected = 0;
            return true;
        }

        public bool Terminal_ResumeLane()
        {
            if (laneSelected <= 0) return false;
            SendLaneCommand("resume", laneSelected);
            return true;
        }

        /// <summary>First press arms it, a second one within 5 s clears every lane of the network.</summary>
        public bool Terminal_ClearLanes(out bool armed)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - laneClearArmed > 5 * 60) { laneClearArmed = now; armed = true; return false; }
            laneClearArmed = int.MinValue / 2;
            armed = false;
            SendLaneCommand("clear", 0);
            StopLaneDraw();
            return true;
        }

        /// <summary>Draws the selected lane pair (nothing without a selection), off after DebugDrawTimeoutSeconds.</summary>
        public bool Terminal_VisualizeLane()
        {
            if (laneSelected <= 0) return false;
            SendLaneCommand("draw", laneSelected);
            laneDrawWanted = laneSelected;
            laneDraw = true;
            laneDrawStart = MyAPIGateway.Session.GameplayFrameCounter;
            laneDrawUntil = DateTime.MinValue;   // set when the lane arrives (clients: the server's timeout by then)
            Automata.Drone.DroneControllerBlock.RequestClientSettings();
            if (AutomataSession.Instance != null) AutomataSession.Instance.RequestDraw(this, true);
            return true;
        }
        #endregion

        #region Draw (clients)
        private static int DrawTimeoutTicks { get { return Config.ServerConfig.Instance.Drone.DebugDrawTimeoutSeconds * 60; } }

        private bool DrawLane()
        {
            if (!laneDraw || Entity == null || Entity.MarkedForClose) { StopLaneDraw(); return false; }
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (settings.LaneDrawNumber != laneDrawWanted || settings.LaneDrawAB == null || settings.LaneDrawAB.Count < 6)
            {
                if (now - laneDrawStart < 10 * 60) return true;   // waiting for the server (10 s at most)
                StopLaneDraw();
                return false;
            }
            if (laneDrawUntil == DateTime.MinValue) laneDrawUntil = DateTime.UtcNow.AddSeconds(Config.ServerConfig.Instance.Drone.DebugDrawTimeoutSeconds);
            if (DateTime.UtcNow > laneDrawUntil) { StopLaneDraw(); return false; }
            float alpha = settings.LaneDrawPending ? 0.35f : 0.9f;
            float phase = (now / 60f * LANE_CRAWL) % (LANE_DASH + LANE_GAP);
            DrawBox(settings.LaneDrawAB, settings.LaneDrawUpAB, settings.LaneDrawWidth, settings.LaneDrawHeight, Color.DeepSkyBlue * alpha, phase);
            DrawBox(settings.LaneDrawBA, settings.LaneDrawUpBA, settings.LaneDrawWidth, settings.LaneDrawHeight, Color.Orange * alpha, phase);
            if (laneGpsNumber != laneDrawWanted) AddLaneGps();
            return true;
        }

        private static readonly List<double> boxEdge = new List<double>();

        /// <summary>
        /// A lane's box ('width' x 'height') along its line ('ups': "up" at each point): the four long edges dashed and crawling toward its end,
        /// the cross-sections at both ends dashed and still (also Pathfinding.LaneOverlay's). No ups: the line alone.
        /// </summary>
        internal static void DrawBox(List<double> flat, List<double> ups, double width, double height, Color color, float phase)
        {
            int n = flat.Count / 3;
            if (n < 2 || ups == null || ups.Count < 3 * n) { DrawDashes(flat, color, phase); return; }
            double hw = width / 2, hh = height / 2;
            for (int c = 0; c < 4; c++)
            {
                double sx = (c & 1) != 0 ? hw : -hw, sy = (c & 2) != 0 ? hh : -hh;
                boxEdge.Clear();
                for (int i = 0; i < n; i++)
                {
                    Vector3D p, right, up;
                    BoxFrame(flat, ups, n, i, out p, out right, out up);
                    Vector3D q = p + right * sx + up * sy;
                    boxEdge.Add(q.X); boxEdge.Add(q.Y); boxEdge.Add(q.Z);
                }
                DrawDashes(boxEdge, color, phase);
            }
            for (int end = 0; end < 2; end++)
            {
                Vector3D p, right, up;
                BoxFrame(flat, ups, n, end == 0 ? 0 : n - 1, out p, out right, out up);
                for (int side = 0; side < 4; side++)
                {
                    // round the rectangle: (-,-) (+,-) (+,+) (-,+)
                    int k0 = side, k1 = (side + 1) % 4;
                    Vector3D a = p + right * (k0 == 1 || k0 == 2 ? hw : -hw) + up * (k0 >= 2 ? hh : -hh);
                    Vector3D b = p + right * (k1 == 1 || k1 == 2 ? hw : -hw) + up * (k1 >= 2 ? hh : -hh);
                    boxEdge.Clear();
                    boxEdge.Add(a.X); boxEdge.Add(a.Y); boxEdge.Add(a.Z); boxEdge.Add(b.X); boxEdge.Add(b.Y); boxEdge.Add(b.Z);
                    DrawDashes(boxEdge, color, 0);
                }
            }
        }

        // Point 'i' of a box line, its right and up (across the bend: the mean of its two segments' ways)
        private static void BoxFrame(List<double> flat, List<double> ups, int n, int i, out Vector3D p, out Vector3D right, out Vector3D up)
        {
            p = new Vector3D(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            Vector3D dir = new Vector3D(flat[3 * b], flat[3 * b + 1], flat[3 * b + 2]) - new Vector3D(flat[3 * a], flat[3 * a + 1], flat[3 * a + 2]);
            Vector3D u = new Vector3D(ups[3 * i], ups[3 * i + 1], ups[3 * i + 2]);
            if (dir.LengthSquared() < 1e-6) dir = Vector3D.CalculatePerpendicularVector(u);
            dir.Normalize();
            right = Vector3D.Cross(dir, u);
            if (right.LengthSquared() < 1e-6) right = Vector3D.CalculatePerpendicularVector(dir);
            right.Normalize();
            up = Vector3D.Cross(right, dir);
        }

        // Dashes along the line, crawling towards its end (also Pathfinding.LaneOverlay's)
        internal static void DrawDashes(List<double> flat, Color color, float phase)
        {
            int n = flat.Count / 3;
            float carry = phase;   // where along the next segment the first dash starts
            for (int i = 0; i + 1 < n; i++)
            {
                var a = new Vector3D(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
                var b = new Vector3D(flat[3 * i + 3], flat[3 * i + 4], flat[3 * i + 5]);
                Vector3D d = b - a;
                float len = (float)d.Length();
                if (len < 0.01f) continue;
                Vector3 dir = (Vector3)(d / len);
                float t = carry;
                while (t < len)
                {
                    float dash = Math.Min(LANE_DASH, len - t);
                    if (dash > 0.05f)
                        MyTransparentGeometry.AddLineBillboard(LaneMaterial, color.ToVector4(), a + dir * t, dir, dash, LANE_THICKNESS, BlendTypeEnum.SDR);
                    t += LANE_DASH + LANE_GAP;
                }
                carry = t - len;
            }
        }

        // The lane's names at its ends, as local GPS markers while it is drawn
        private void AddLaneGps()
        {
            RemoveLaneGps();
            laneGpsNumber = laneDrawWanted;
            AddEndGps(settings.LaneDrawAB, settings.LaneDrawNameAB, Color.DeepSkyBlue);
            AddEndGps(settings.LaneDrawBA, settings.LaneDrawNameBA, Color.Orange);
        }

        private void AddEndGps(List<double> flat, string name, Color color)
        {
            if (flat == null || flat.Count < 3 || MyAPIGateway.Session == null || MyAPIGateway.Session.GPS == null) return;
            var start = new Vector3D(flat[0], flat[1], flat[2]);
            var gps = MyAPIGateway.Session.GPS.Create(name ?? "lane", "", start, true, true);
            gps.GPSColor = color;
            MyAPIGateway.Session.GPS.AddLocalGps(gps);
            laneGps.Add(gps);
        }

        private void RemoveLaneGps()
        {
            if (MyAPIGateway.Session != null && MyAPIGateway.Session.GPS != null)
                for (int i = 0; i < laneGps.Count; i++) MyAPIGateway.Session.GPS.RemoveLocalGps(laneGps[i]);
            laneGps.Clear();
            laneGpsNumber = 0;
        }

        private void StopLaneDraw()
        {
            laneDraw = false;
            RemoveLaneGps();
        }
        #endregion
    }
}
