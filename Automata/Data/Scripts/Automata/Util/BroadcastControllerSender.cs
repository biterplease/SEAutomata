using System.Collections.Generic;

using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;

using Automata.Drone;

namespace Automata.Util
{
    /// <summary>
    /// Chat messages through a vanilla Broadcast Controller on the sending block's construct (its grid and what is
    /// mechanically attached to it): the controller's own terminal settings decide who reads them (Broadcast target:
    /// owner / faction / everyone), under which name, and whether its antenna is used. No controller, none working, or
    /// none of the owner / faction: nothing is sent. Server only; the game syncs the message.
    /// Used sparingly (blocks with "Use broadcast controller" on, for events worth a chat line).
    /// </summary>
    public static class BroadcastControllerSender
    {
        private static readonly List<IMyCubeGrid> grids = new List<IMyCubeGrid>();

        /// <summary>Sends 'text'; 'via' is the controller's name, or why nothing was sent.</summary>
        public static bool Send(IMyCubeBlock from, string text, out string via)
        {
            via = null;
            if (from == null || from.CubeGrid == null || string.IsNullOrEmpty(text)) { via = "no block"; return false; }
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer) { via = "not the server"; return false; }
            grids.Clear();
            MyAPIGateway.GridGroups.GetGroup(from.CubeGrid, GridLinkTypeEnum.Mechanical, grids);
            bool any = false;
            try
            {
                for (int g = 0; g < grids.Count; g++)
                {
                    foreach (var bc in grids[g].GetFatBlocks<IMyBroadcastController>())
                    {
                        any = true;
                        if (bc == null || bc.Closed || !bc.IsWorking || !AnchorDirectory.IsAllowed(from.OwnerId, bc.OwnerId)) continue;
                        var chat = ChatComponent(bc);
                        if (chat == null) continue;
                        chat.SendMessage(text);
                        via = bc.CustomName;
                        return true;
                    }
                }
            }
            finally { grids.Clear(); }
            via = any ? "no working broadcast controller of the owner / faction" : "no broadcast controller on the construct";
            return false;
        }

        // The block's chat component (the PB / mod interface of its messages)
        private static Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent ChatComponent(IMyEntity e)
        {
            if (e.Components == null) return null;
            foreach (var c in e.Components)
            {
                var chat = c as Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent;
                if (chat != null) return chat;
            }
            return null;
        }
    }
}
