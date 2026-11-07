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
        private const int BASE_LANES_MAX = 8;              // bases a pass traces lanes between, at most
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
                    settings.LaneDrawAB = new List<double>();
                    settings.LaneDrawBA = new List<double>();
                    settings.LaneDrawUpAB = new List<double>();
                    settings.LaneDrawUpBA = new List<double>();
                    settings.LaneDrawNumber = number;
                    if (lanes.Geometry(Entity.EntityId, number, ab, ba, upAB, upBA, out pending, out nab, out nba))
                    {
                        Flatten(ab, settings.LaneDrawAB);
                        Flatten(ba, settings.LaneDrawBA);
                        Flatten(upAB, settings.LaneDrawUpAB);
                        Flatten(upBA, settings.LaneDrawUpBA);
                        settings.LaneDrawPending = pending;
                        settings.LaneDrawNameAB = nab;
                        settings.LaneDrawNameBA = nba;
                    }
                    SettingsEdited();
                    break;
                }
            }
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: lane command {1} {2}", entityId, parts[1], number);
        }

        // An auction with a location: a lane from this orchestrator's base to the job's place (combat orders fly in formation,
        // not in lanes); a scout job's lane is an order lane (dropped if its order finds nothing)
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
            bool scout = ja.Type == JobType.DesignatorScoutArea || ja.Type == JobType.DesignatorScoutPoint;
            lanes.Trace(Entity.EntityId, block.CubeGrid.WorldVolume.Center, to, scout ? ja.JobId : 0u);
        }

        // Lanes between the player's bases (static grids with an Automata computer block) this orchestrator's relay network
        // reaches, pair by pair
        private void TraceBaseLanes(Pathfinding.FlightLanes lanes)
        {
            if (messaging == null || block.CubeGrid == null) return;
            var bases = new List<IMyCubeGrid>();
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
            for (int i = 0; i < bases.Count; i++)
                for (int j = i + 1; j < bases.Count; j++)
                    lanes.Trace(Entity.EntityId, bases[i].WorldVolume.Center, bases[j].WorldVolume.Center);
        }

        // A drone's report (keepalive / job report) of lanes it found blocked: each redraws its lane once
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

        public bool Terminal_LaneClearArmed { get { return MyAPIGateway.Session.GameplayFrameCounter - laneClearArmed <= 5 * 60; } }

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
            DrawBox(settings.LaneDrawAB, settings.LaneDrawUpAB, Color.DeepSkyBlue * alpha, phase);
            DrawBox(settings.LaneDrawBA, settings.LaneDrawUpBA, Color.Orange * alpha, phase);
            if (laneGpsNumber != laneDrawWanted) AddLaneGps();
            return true;
        }

        private static readonly List<double> boxEdge = new List<double>();

        /// <summary>
        /// A lane's box along its line ('ups': "up" at each point): the four long edges dashed and crawling toward its end,
        /// the cross-sections at both ends dashed and still (also Pathfinding.LaneOverlay's). No ups: the line alone.
        /// </summary>
        internal static void DrawBox(List<double> flat, List<double> ups, Color color, float phase)
        {
            int n = flat.Count / 3;
            if (n < 2 || ups == null || ups.Count < 3 * n) { DrawDashes(flat, color, phase); return; }
            double hw = Pathfinding.FlightLanes.BOX_WIDTH / 2, hh = Pathfinding.FlightLanes.BOX_HEIGHT / 2;
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
