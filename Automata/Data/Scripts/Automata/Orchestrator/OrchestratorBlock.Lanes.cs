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
        #region UI strings
        private static readonly MyStringId StrLaneDeleted = MyStringId.GetOrCompute("Lane L{0} deleted");
        private static readonly MyStringId StrLaneResumed = MyStringId.GetOrCompute("Lane L{0} resumed (checked again before use)");
        private static readonly MyStringId StrLanesCleared = MyStringId.GetOrCompute("{0} lane pair(s) cleared");
        #endregion

        private const int LANE_LIST_EVERY = 30;   // UpdateBeforeSimulation10 calls (5 s)
        private const float LANE_DASH = 5f, LANE_GAP = 5f, LANE_CRAWL = 3f, LANE_THICKNESS = 0.35f;
        private static readonly MyStringId LaneMaterial = MyStringId.GetOrCompute("Square");

        private int laneListCounter, laneListVersion = -1, laneListMinute;
        private string laneCommandDone = "";

        // Client side: the player's selection, the drawing, the armed "Clear all"
        private int laneSelected;
        private bool laneDraw;
        private int laneDrawStart, laneDrawWanted;
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
                    if (lanes.Delete(Entity.EntityId, number)) AddLaneEvent(Loc.F(StrLaneDeleted, number));
                    break;
                case "resume":
                    if (lanes.Resume(Entity.EntityId, number)) AddLaneEvent(Loc.F(StrLaneResumed, number));
                    break;
                case "clear":
                    AddLaneEvent(Loc.F(StrLanesCleared, lanes.ClearAll(Entity.EntityId)));
                    break;
                case "draw":
                {
                    var ab = new List<Vector3D>();
                    var ba = new List<Vector3D>();
                    bool pending;
                    string nab, nba;
                    settings.LaneDrawAB = new List<double>();
                    settings.LaneDrawBA = new List<double>();
                    settings.LaneDrawNumber = number;
                    if (lanes.Geometry(Entity.EntityId, number, ab, ba, out pending, out nab, out nba))
                    {
                        Flatten(ab, settings.LaneDrawAB);
                        Flatten(ba, settings.LaneDrawBA);
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

        private void AddLaneEvent(string text)
        {
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: {1}", entityId, text);
        }

        private static void Flatten(List<Vector3D> pts, List<double> into)
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
            if (now - laneDrawStart > DrawTimeoutTicks) { StopLaneDraw(); return false; }
            if (settings.LaneDrawNumber != laneDrawWanted || settings.LaneDrawAB == null || settings.LaneDrawAB.Count < 6)
                return now - laneDrawStart < 10 * 60;   // waiting for the server (10 s at most)
            float alpha = settings.LaneDrawPending ? 0.35f : 0.9f;
            float phase = (now / 60f * LANE_CRAWL) % (LANE_DASH + LANE_GAP);
            DrawDashes(settings.LaneDrawAB, Color.DeepSkyBlue * alpha, phase);
            DrawDashes(settings.LaneDrawBA, Color.Orange * alpha, phase);
            if (laneGpsNumber != laneDrawWanted) AddLaneGps();
            return true;
        }

        // Dashes along the line, crawling towards its end
        private static void DrawDashes(List<double> flat, Color color, float phase)
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
