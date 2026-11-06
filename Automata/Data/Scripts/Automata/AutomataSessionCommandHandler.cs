using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Util;
using Automata.Util.Strings;

namespace Automata
{
    /// <summary>
    /// Chat commands (clients / single player / host; registered in AutomataSession.LoadData).
    /// /automata debugdraw [dronenav] [droneobs] [ccobs] [park] [lanes] [range m] [off]: this player's debug overlays on
    /// the Automata blocks they own (not faction) within the range (DEBUGDRAW_RANGE_METERS, at most DEBUGDRAW_RANGE_MAX);
    /// "lanes": the flight lanes of the orchestrators they own with a line within the range (Pathfinding.LaneOverlay: the
    /// lanes live on the server, a multiplayer client asks it). No kind = all. "off": every overlay this player has on, wherever it is (kinds and range ignored; overlays are
    /// local to the player's client: nobody else's change). The overlays switch off after the server's
    /// DebugDrawTimeoutSeconds, like the terminal toggles.
    /// </summary>
    public class AutomataSessionCommandHandler
    {

        public AutomataSessionCommandHandler() { }

        private const string CMDROOT_AUTOMATA = "/automata";
        private const string DEBUGDRAW = "debugdraw";
        private const string DD_DRONENAV = "dronenav";
        private const string DD_DRONEOBS = "droneobs";
        private const string DD_CCOBS = "ccobs";
        private const string DD_PARK = "park";
        private const string DD_LANES = "lanes";
        private const string DD_OFF = "off";
        private const double DEBUGDRAW_RANGE_METERS = 300;
        private const double DEBUGDRAW_RANGE_MAX = 2000;
        private const double DEBUGDRAW_RANGE_MIN = 10;

        [Flags]
        private enum DrawKinds { None = 0, DroneNav = 1, DroneObs = 2, CcObs = 4, Park = 8, Lanes = 16, All = 31 }

        private readonly List<IMyEntity> entityBuffer = new List<IMyEntity>();

        public void MessageEntered(string msg, ref bool sendToOthers)
        {
            if (string.IsNullOrEmpty(msg) || msg[0] != '/')
                return;
            var cmd = msg.Trim().ToLowerInvariant();
            string root = IsRoot(cmd, CMDROOT_AUTOMATA) ? CMDROOT_AUTOMATA : null;
            if (root == null)
                return;
            sendToOthers = false;
            var args = cmd.Substring(root.Length).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0)
            {
                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, GetHelpText());
                return;
            }
            switch (args[0])
            {
                case DEBUGDRAW:
                    DebugDraw(args);
                    break;
                default:
                    MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, GetHelpText());
                    break;
            }
        }

        // "/automata" or "/automata ...", not "/automatafoo"
        private static bool IsRoot(string cmd, string root)
        {
            return cmd.StartsWith(root, StringComparison.Ordinal) && (cmd.Length == root.Length || cmd[root.Length] == ' ');
        }

        #region Debug draw
        private void DebugDraw(string[] args)
        {
            var player = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            if (player == null) return;

            DrawKinds kinds = DrawKinds.None;
            bool on = true;
            double range = DEBUGDRAW_RANGE_METERS;
            for (int i = 1; i < args.Length; i++)
            {
                int metres;   // (a failed parse sets its out to 0: not 'range' itself, or a word after the number reset it)
                if (int.TryParse(args[i], out metres))
                {
                    range = metres;
                    continue;
                }
                switch (args[i])
                {
                    case DD_DRONENAV: kinds |= DrawKinds.DroneNav; break;
                    case DD_DRONEOBS: kinds |= DrawKinds.DroneObs; break;
                    case DD_CCOBS: kinds |= DrawKinds.CcObs; break;
                    case DD_PARK: kinds |= DrawKinds.Park; break;
                    case DD_LANES: kinds |= DrawKinds.Lanes; break;
                    case DD_OFF: on = false; break;
                    default:
                        MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(MyStrings.Common_UsageAutomataDebugdrawDronenavDroneobs));
                        return;
                }
            }
            if (!on)
            {
                // Everything this player draws, anywhere: overlays live on this client only
                int stopped = AutomataSession.Instance != null ? AutomataSession.Instance.StopAllDebugDraw() : 0;
                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.F(MyStrings.Common_DebugDrawOffAll, stopped));
                return;
            }
            if (kinds == DrawKinds.None) kinds = DrawKinds.All;
            Drone.DroneControllerBlock.RequestClientSettings();   // multiplayer clients: the server's draw timeout

            long me = player.IdentityId;
            Vector3D pos = player.GetPosition();
            range = MathHelperD.Clamp(range, DEBUGDRAW_RANGE_MIN, DEBUGDRAW_RANGE_MAX);
            double rangeSq = range * range;
            var sphere = new BoundingSphereD(pos, range);
            int nav = 0, obs = 0, cc = 0, park = 0;
            if ((kinds & DrawKinds.Lanes) != 0)
            {
                // The lanes are the server's: drawn here at once (single player / host), else when its answer comes
                if (MyAPIGateway.Multiplayer.IsServer) Pathfinding.LaneOverlay.Show(Pathfinding.LaneOverlay.Collect(me, pos, range), range);
                else if (AutomataSession.Instance != null && AutomataSession.Instance.Net != null)
                    AutomataSession.Instance.Net.SendToServer(new Network.LaneDrawRequestPacket { Centre = Vector3DData.FromVector3D(pos), Range = range });
                if (kinds == DrawKinds.Lanes) return;
            }

            entityBuffer.Clear();
            entityBuffer.AddRange(MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere));
            for (int e = 0; e < entityBuffer.Count; e++)
            {
                var grid = entityBuffer[e] as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose) continue;
                foreach (var tb in grid.GetFatBlocks<IMyTerminalBlock>())
                {
                    if (tb.OwnerId != me || tb.GameLogic == null) continue;
                    if (Vector3D.DistanceSquared(tb.GetPosition(), pos) > rangeSq) continue;

                    var drone = tb.GameLogic.GetAs<Drone.DroneControllerBlock>();
                    if (drone != null)
                    {
                        if ((kinds & DrawKinds.DroneNav) != 0)
                        {
                            drone.Terminal_DrawNavigationTargets = true;
                            nav++;
                        }
                        if ((kinds & DrawKinds.DroneObs) != 0)
                        {
                            drone.Terminal_ObservationAreaDraw = true;
                            if (drone.Terminal_ObservationAreaDraw) obs++;   // needs a home connector
                        }
                        continue;
                    }
                    if ((kinds & DrawKinds.CcObs) != 0)
                    {
                        var computer = tb.GameLogic.GetAs<Construction.ConstructionComputerBlock>();
                        if (computer != null)
                        {
                            computer.Terminal_DrawArea = true;
                            cc++;
                            continue;
                        }
                    }
                    if ((kinds & DrawKinds.Park) != 0)
                    {
                        var orchestrator = tb.GameLogic.GetAs<Orchestrator.OrchestratorBlock>();
                        if (orchestrator != null)
                        {
                            orchestrator.Terminal_StagingDraw = true;
                            if (orchestrator.Terminal_StagingDraw) park++;   // needs the protocol on, parking area mode
                        }
                    }
                }
            }
            entityBuffer.Clear();
            MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME,
                Loc.F(MyStrings.Common_DebugDrawOnWithinM, (int)range, nav, obs, cc, park));
        }
        #endregion

       private string GetHelpText()
        {
            return Loc.F(MyStrings.Common_AutomataCommands, AutomataSession.MOD_VERSION, DEBUGDRAW);
        }
    }
}
