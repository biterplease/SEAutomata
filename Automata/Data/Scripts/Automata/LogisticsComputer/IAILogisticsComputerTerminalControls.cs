using System;
using System.Collections.Generic;

using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.LogisticsComputer
{
    public static class LogisticsComputerTerminalControls
    {
        public static ServerConfig.LogisticsComputerConfig BlockConfig = ServerConfig.Instance.LogisticsComputer;

        const string IdPrefix = AutomataSession.MOD_NAME + "_LogisticsComputer_";
        static bool Done = false;

        private static readonly HashSet<string> defaultControlIdsToHide = new HashSet<string>
        {
            "Edit",
            "ConsoleCommand",
            "TerminalRun",
            "Recompile",
        };
        private static readonly HashSet<string> defaultActionIdsToHide = new HashSet<string>
        {
            "Run",
            "RunWithDefaultArgument",
        };

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;

            HideDefaultControls();
            HideDefaultActions();
            CreateControls();
            Log.Info("Created LC terminal controls");
        }

        /// <summary>
        /// Check an return the GameLogic object
        /// </summary>
        /// <param name="block"></param>
        /// <returns></returns>
        private static LogisticsComputerBlock GetBlock(IMyTerminalBlock block)
        {
            if (block != null && block.GameLogic != null) return block.GameLogic.GetAs<LogisticsComputerBlock>();
            return null;
        }

        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<LogisticsComputerBlock>() != null;
        }

        /// <summary>
        /// Hides default controls in the Programmable Block.
        /// </summary>
        public static void HideDefaultControls()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyProgrammableBlock>(out controls);

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
            MyAPIGateway.TerminalControls.GetActions<IMyProgrammableBlock>(out actions);

            foreach (IMyTerminalAction a in actions)
            {
                if (defaultActionIdsToHide.Contains(a.Id))
                {
                    a.Enabled = TerminalChainedDelegate.Create(a.Enabled, CustomVisibleCondition);
                }
            }
        }

        /// <summary>
        /// Initialize custom control definition
        /// </summary>
        public static void CreateControls()
        {
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOffEnabled");
                c.Title = MySpaceTexts.BlockPropertyTitle_EnableAI;
                c.Tooltip = MyStringId.GetOrCompute("Enable this Logistics Computer AI behaviour.");
                c.SupportsMultipleBlocks = true; // wether this control should be visible when multiple blocks are selected (as long as they all have this control).

                // callbacks to determine if the control should be visible or not-grayed-out(Enabled) depending on whatever custom condition you want, given a block instance.
                // optional, they both default to true.
                c.Visible = CustomVisibleCondition;
                //c.Enabled = CustomVisibleCondition;

                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;

                // setters and getters should both be assigned on all controls that have them, to avoid errors in mods or PB scripts getting exceptions from them.
                c.Getter = (b) => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_IsEnabled = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyProgrammableBlock>("");
                c.Label = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_ProvideForConstruction_Title");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModeProvideForLogistics");
                c.Title = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_ProvideForLogistics_Title");
                c.Tooltip = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_ProvideForLogistics_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics;
                c.Enabled = (b) => BlockConfig.AllowLogistics; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_WorkModeProvideForLogistics ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_WorkModeProvideForLogistics = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModeProvideForConstruction");
                c.Title = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_ProvideForConstruction_Title");
                c.Tooltip = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_ProvideForConstruction_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => BlockConfig.AllowLogistics; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_WorkModeProvideForConstruction ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_WorkModeProvideForConstruction = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModePush");
                c.Title = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_Push_Title");
                c.Tooltip = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_Push_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush;
                c.Enabled = (b) => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_WorkModePush ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_WorkModePush = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModeRequest");
                c.Title = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_Request_Title");
                c.Tooltip = MyStringId.GetOrCompute("Automata_LogisticsComputer_TerminalControl_WorkModes_Request_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests;
                c.Enabled = (b) => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_WorkModeRequest ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_WorkModeRequest = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyProgrammableBlock>(IdPrefix + "LCLabelOthersettings");
                c.Label = MyStringId.GetOrCompute("Requests");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
        }
    }
}
