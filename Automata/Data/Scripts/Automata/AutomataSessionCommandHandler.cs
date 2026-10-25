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
    /// /automata debugdraw [dronenav] [droneobs] [ccobs] [park] [off]: this player's debug overlays on the Automata
    /// blocks they own (not faction) within 1000 m. No kind = all. The overlays switch off after the server's
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
        private const string DD_OFF = "off";
        private const double DEBUGDRAW_RANGE_METERS = 300;

        [Flags]
        private enum DrawKinds { None = 0, DroneNav = 1, DroneObs = 2, CcObs = 4, Park = 8, All = 15 }

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
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case DD_DRONENAV: kinds |= DrawKinds.DroneNav; break;
                    case DD_DRONEOBS: kinds |= DrawKinds.DroneObs; break;
                    case DD_CCOBS: kinds |= DrawKinds.CcObs; break;
                    case DD_PARK: kinds |= DrawKinds.Park; break;
                    case DD_OFF: on = false; break;
                    default:
                        MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(MyStrings.Common_UsageAutomataDebugdrawDronenavDroneobs));
                        return;
                }
            }
            if (kinds == DrawKinds.None) kinds = DrawKinds.All;
            if (on) Drone.DroneControllerBlock.RequestClientSettings();   // multiplayer clients: the server's draw timeout

            long me = player.IdentityId;
            Vector3D pos = player.GetPosition();
            double rangeSq = DEBUGDRAW_RANGE_METERS * DEBUGDRAW_RANGE_METERS;
            var sphere = new BoundingSphereD(pos, DEBUGDRAW_RANGE_METERS);
            int nav = 0, obs = 0, cc = 0, park = 0;

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
                            drone.Terminal_DrawNavigationTargets = on;
                            nav++;
                        }
                        if ((kinds & DrawKinds.DroneObs) != 0)
                        {
                            drone.Terminal_ObservationAreaDraw = on;
                            if (!on || drone.Terminal_ObservationAreaDraw) obs++;   // needs a home connector
                        }
                        continue;
                    }
                    if ((kinds & DrawKinds.CcObs) != 0)
                    {
                        var computer = tb.GameLogic.GetAs<Construction.ConstructionComputerBlock>();
                        if (computer != null)
                        {
                            computer.Terminal_DrawArea = on;
                            cc++;
                            continue;
                        }
                    }
                    if ((kinds & DrawKinds.Park) != 0)
                    {
                        var orchestrator = tb.GameLogic.GetAs<Orchestrator.OrchestratorBlock>();
                        if (orchestrator != null)
                        {
                            orchestrator.Terminal_StagingDraw = on;
                            if (!on || orchestrator.Terminal_StagingDraw) park++;   // needs the protocol on, parking area mode
                        }
                    }
                }
            }
            entityBuffer.Clear();
            MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME,
                Loc.F(on ? MyStrings.Common_DebugDrawOnWithinM : MyStrings.Common_DebugDrawOffWithinM, (int)DEBUGDRAW_RANGE_METERS, nav, obs, cc, park));
        }
        #endregion

       private string GetHelpText()
        {
            return Loc.F(MyStrings.Common_AutomataCommands, AutomataSession.MOD_VERSION, DEBUGDRAW);
        }
    }
}
