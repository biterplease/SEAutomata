using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Orchestrator
{
    /// <summary>
    /// The orchestrator's list of its owner's custom routes (routes players recorded with a drone: RouteMemory): show the
    /// selected one (orange, off after DebugDrawTimeoutSeconds), delete one, delete all (pressed twice). The routes live on
    /// the server (PathfindingManager.Memory); the list and the drawn points reach players through the synced settings, a
    /// player's commands come back the same way (CustomRouteCommand). Deleting has no cooldown: players make them by hand.
    /// The shared memory links are the pathfinder's own: not listed, they expire unused.
    /// </summary>
    public partial class OrchestratorBlock
    {
        private const int ROUTE_LIST_EVERY = 30;   // UpdateBeforeSimulation10 calls (5 s)

        private int routeListCounter, routeListVersion = -1, routeListMinute;
        private string routeCommandDone;   // null: not read yet (the saved command was done before the world was saved)

        // Client side: the player's selection, the drawing, the armed "Delete all"
        private int routeSelected;
        private bool routeDraw;
        private int routeDrawStart, routeDrawWanted;
        private DateTime routeDrawUntil;
        private int routeClearArmed = int.MinValue / 2;
        private VRage.Game.ModAPI.IMyGps routeGps;

        #region Server
        private void UpdateCustomRoutes10()
        {
            var mem = Pathfinding.PathfindingManager.Instance != null ? Pathfinding.PathfindingManager.Instance.Memory : null;
            if (mem == null) return;
            string cmd = settings.CustomRouteCommand ?? "";
            // The command in the saved settings was carried out before the save: not again on load (a "delete all"
            // deleted every route recorded since, at each load)
            if (routeCommandDone == null) routeCommandDone = cmd;
            if (cmd.Length > 0 && cmd != routeCommandDone)
            {
                routeCommandDone = cmd;
                RunRouteCommand(mem, cmd);
                routeListVersion = -1;
                routeListCounter = ROUTE_LIST_EVERY;   // the list at once
            }
            // On a change, and once a minute anyway ("used N min ago")
            if (++routeListCounter < ROUTE_LIST_EVERY) return;
            routeListCounter = 0;
            if (routeListVersion == mem.RoutesVersion && ++routeListMinute < 12) return;
            routeListMinute = 0;
            routeListVersion = mem.RoutesVersion;
            var list = DescribeRoutes(mem);
            bool same = settings.CustomRoutes != null && list.Count == settings.CustomRoutes.Count;
            for (int i = 0; same && i < list.Count; i++) same = list[i] == settings.CustomRoutes[i];
            if (same) return;
            settings.CustomRoutes = list;
            SettingsEdited();
        }

        private void RouteOwner(out long player, out long faction)
        {
            Pathfinding.RouteMemory.OwnerOf(Entity.EntityId, out player, out faction);
        }

        private List<string> DescribeRoutes(Pathfinding.RouteMemory mem)
        {
            long player, faction;
            RouteOwner(out player, out faction);
            var routes = new List<Pathfinding.RouteMemory.CustomRoute>();
            mem.RoutesOf(player, faction, routes);
            var rows = new List<string>(routes.Count);
            var pts = new List<Vector3D>();
            long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            for (int i = 0; i < routes.Count; i++)
            {
                double length;
                mem.RoutePoints(routes[i].Id, pts, out length);
                rows.Add(routes[i].Id + "|" + Loc.F(MyStrings.Orchestrator_CustomRouteRow, Pathfinding.CustomRouteOverlay.Label(routes[i]), pts.Count,
                                                   (int)Math.Round(length), Math.Max(0, minute - routes[i].LastUsed)));
            }
            return rows;
        }

        private void RunRouteCommand(Pathfinding.RouteMemory mem, string cmd)
        {
            var parts = cmd.Split('|');
            if (parts.Length < 3) return;
            int id;
            int.TryParse(parts[2], out id);
            long player, faction;
            RouteOwner(out player, out faction);
            switch (parts[1])
            {
                case "delete":
                    mem.DeleteRoute(id, player, faction);
                    break;
                case "clear":
                    mem.DeleteRoutes(player, faction);
                    break;
                case "draw":
                {
                    settings.CustomRouteDraw = new List<double>();
                    settings.CustomRouteDrawFwd = new List<double>();
                    settings.CustomRouteDrawUp = new List<double>();
                    settings.CustomRouteDrawId = id;
                    settings.CustomRouteDrawName = "";
                    var routes = new List<Pathfinding.RouteMemory.CustomRoute>();
                    mem.RoutesOf(player, faction, routes);
                    for (int i = 0; i < routes.Count; i++)
                    {
                        if (routes[i].Id != id) continue;
                        var pts = new List<Vector3D>();
                        var fwds = new List<Vector3D>();
                        var ups = new List<Vector3D>();
                        double length;
                        if (mem.RoutePoints(id, pts, fwds, ups, out length))
                        {
                            Flatten(pts, settings.CustomRouteDraw);
                            Flatten(fwds, settings.CustomRouteDrawFwd);
                            Flatten(ups, settings.CustomRouteDrawUp);
                        }
                        settings.CustomRouteDrawName = Pathfinding.CustomRouteOverlay.Label(routes[i]);
                    }
                    SettingsEdited();
                    break;
                }
            }
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: custom route command {1} {2}", entityId, parts[1], id);
        }
        #endregion

        #region Terminal (players)
        public List<string> Terminal_CustomRoutes { get { return settings.CustomRoutes ?? new List<string>(); } }
        public int Terminal_RouteSelected { get { return routeSelected; } set { routeSelected = value; } }

        private void SendRouteCommand(string what, int id)
        {
            settings.CustomRouteCommand = DateTime.UtcNow.Ticks.ToString() + "|" + what + "|" + id;
            SettingsEdited();
        }

        public bool Terminal_DeleteRoute()
        {
            if (routeSelected <= 0) return false;
            SendRouteCommand("delete", routeSelected);
            if (routeDrawWanted == routeSelected) StopRouteDraw();
            routeSelected = 0;
            return true;
        }

        /// <summary>First press arms it, a second one within 5 s deletes every custom route of the owner.</summary>
        public bool Terminal_ClearRoutes(out bool armed)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - routeClearArmed > 5 * 60) { routeClearArmed = now; armed = true; return false; }
            routeClearArmed = int.MinValue / 2;
            armed = false;
            SendRouteCommand("clear", 0);
            routeSelected = 0;
            StopRouteDraw();
            return true;
        }

        /// <summary>Draws the selected route (nothing without a selection), off after DebugDrawTimeoutSeconds.</summary>
        public bool Terminal_VisualizeRoute()
        {
            if (routeSelected <= 0) return false;
            SendRouteCommand("draw", routeSelected);
            routeDrawWanted = routeSelected;
            routeDraw = true;
            routeDrawStart = MyAPIGateway.Session.GameplayFrameCounter;
            routeDrawUntil = DateTime.MinValue;   // set when the route arrives
            RemoveRouteGps();
            Automata.Drone.DroneControllerBlock.RequestClientSettings();
            if (AutomataSession.Instance != null) AutomataSession.Instance.RequestDraw(this, true);
            return true;
        }
        #endregion

        #region Draw (clients)
        private bool DrawCustomRoute()
        {
            if (!routeDraw || Entity == null || Entity.MarkedForClose) { StopRouteDraw(); return false; }
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (settings.CustomRouteDrawId != routeDrawWanted || settings.CustomRouteDraw == null || settings.CustomRouteDraw.Count < 3)
            {
                if (now - routeDrawStart < 10 * 60) return true;   // waiting for the server (10 s at most)
                StopRouteDraw();
                return false;
            }
            if (routeDrawUntil == DateTime.MinValue) routeDrawUntil = DateTime.UtcNow.AddSeconds(Config.ServerConfig.Instance.Drone.DebugDrawTimeoutSeconds);
            if (DateTime.UtcNow > routeDrawUntil) { StopRouteDraw(); return false; }
            Pathfinding.CustomRouteOverlay.DrawRoute(settings.CustomRouteDraw, settings.CustomRouteDrawFwd, settings.CustomRouteDrawUp);
            if (routeGps == null) routeGps = Pathfinding.CustomRouteOverlay.StartGps(settings.CustomRouteDraw, settings.CustomRouteDrawName);
            return true;
        }

        private void RemoveRouteGps()
        {
            if (routeGps != null && MyAPIGateway.Session != null && MyAPIGateway.Session.GPS != null) MyAPIGateway.Session.GPS.RemoveLocalGps(routeGps);
            routeGps = null;
        }

        private void StopRouteDraw()
        {
            routeDraw = false;
            RemoveRouteGps();
        }
        #endregion
    }
}
