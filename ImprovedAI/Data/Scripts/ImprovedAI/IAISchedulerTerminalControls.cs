using ImprovedAI.Config;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SpaceEngineers.Game.ModAPI;
using System.Collections.Generic;
using VRage.Game.ModAPI;

namespace ImprovedAI
{
    public class IAISchedulerTerminalControls
    {
        public static ServerConfig.SchedulerBlockConfig BlockConfig = ServerConfig.Instance.SchedulerBounds;

        const string IdPrefix = IAISession.MOD_NAME + "_";
        static bool Done = false;

        private static readonly HashSet<string> defaultControlIdsToHide = new HashSet<string>
        {
            "BroadcastTarget",
            "BroadcastTargetEveryone",
            "BroadcastTargetOwner",
            "BroadcastTargetFaction",
            "UseAntenna",
            "CustomName",
            "Message0",
            "Transmit Message 1",
            "Message1",
            "Transmit Message 2",
            "Message2",
            "Transmit Message 3",
            "Message3",
            "Transmit Message 4",
            "Message4",
            "Transmit Message 5",
            "Message5",
            "Transmit Message 6",
            "Message6",
            "Transmit Message 7",
            "Message7",
            "Transmit Message 8",
            "TransmitRandomMessage",
            "SendGps",
        };
        private static readonly HashSet<string> defaultActionIdsToHide = new HashSet<string>
        {
            "BroadcastTargetEveryone",
            "BroadcastTargetOwner",
            "BroadcastTargetFaction",
            "Transmit Message 1",
            "Transmit Message 2",
            "Transmit Message 3",
            "Transmit Message 4",
            "Transmit Message 5",
            "Transmit Message 6",
            "Transmit Message 7",
            "Transmit Message 8",
            "TransmitRandomMessage",
            "SendGps",
        };

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;

            HideDefaultControls();
            HideDefaultActions();
            CreateControls();
        }

        /// <summary>
        /// Check an return the GameLogic object
        /// </summary>
        /// <param name="block"></param>
        /// <returns></returns>
        private static IAISchedulerBlock GetBlock(IMyTerminalBlock block)
        {
            if (block != null && block.GameLogic != null) return block.GameLogic.GetAs<IAISchedulerBlock>();
            return null;
        }

        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<IAISchedulerBlock>() != null;
        }
        /// <summary>
        /// Hides default controls in the Programmable Block.
        /// </summary>
        public static void HideDefaultControls()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyBroadcastController>(out controls);

            foreach (IMyTerminalControl c in controls)
            {
                if (defaultControlIdsToHide.Contains(c.Id))
                {
                    c.Visible = TerminalChainedDelegate.Create(c.Enabled, CustomVisibleCondition);
                }
            }
        }

        /// <summary>
        /// Hides default actions in the Programmable Block.
        /// </summary>
        public static void HideDefaultActions()
        {
            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<IMyBroadcastController>(out actions);

            foreach (IMyTerminalAction a in actions)
            {
                if (defaultActionIdsToHide.Contains(a.Id))
                {
                    a.Enabled = TerminalChainedDelegate.Create(a.Enabled, CustomVisibleCondition);
                }
            }
        }

        public static void CreateControls() { }
    }
}
