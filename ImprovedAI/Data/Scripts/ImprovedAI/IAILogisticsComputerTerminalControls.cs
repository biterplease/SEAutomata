using ImprovedAI.Config;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace ImprovedAI
{
    public static class IAILogisticsComputerTerminalControls
    {
        public static ServerConfig.LogisticsComputerConfig BlockConfig = ServerConfig.Instance.LogisticsComputer;

        const string IdPrefix = IAISession.MOD_NAME + "_IAIDC_";
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
        private static IAILogisticsComputerBlock GetBlock(IMyTerminalBlock block)
        {
            if (block != null && block.GameLogic != null) return block.GameLogic.GetAs<IAILogisticsComputerBlock>();
            return null;
        }

        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<IAILogisticsComputerBlock>() != null;
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
                c.Label = MyStringId.GetOrCompute("Operation modes");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModeProvideForLogistics");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_ProvideForLogistics_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_ProvideForLogistics_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics;
                c.Enabled = (b) => BlockConfig.AllowLogistics; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeProvideForLogistics ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_OperationModeProvideForLogistics = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyGyro>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModeProvideForConstruction");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_ProvideForConstruction_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_ProvideForConstruction_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => BlockConfig.AllowLogistics; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeProvideForLogistics ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_OperationModeProvideForLogistics = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyGyro>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModePush");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_Push_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_Push_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush;
                c.Enabled = (b) => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModePush ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_OperationModePush = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyGyro>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCOpModeRequest");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_Request_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_OperationMode_Request_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests;
                c.Enabled = (b) => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeRequest ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_OperationModeRequest = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyGyro>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOffLCPushAll");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_PushAll_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_PushAll_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush;
                c.Enabled = (b) => GetBlock(b)?.Terminal_OperationModePush ?? false && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush;

                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;

                c.Getter = (b) => GetBlock(b)?.Terminal_PushAll ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_PushAll = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "SliderLCPushFrequency");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_PushFrequency_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_PushFrequency_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush;
                c.Enabled = (b) => GetBlock(b)?.Terminal_OperationModePush ?? false && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush;
                var cfgMin = TimeUtil.TickToSeconds(BlockConfig.MinPushFrequencyTicks);
                var cfgMax = TimeUtil.TickToSeconds(BlockConfig.MaxPushFrequencyTicks);

                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    // just a heads up that the given value here is not clamped by the game, a mod or PB can give lower or higher than the limits!
                    if (logic != null)
                        logic.Terminal_PushFrequencySeconds = MathHelper.Clamp(v, cfgMin, cfgMax);
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_PushFrequencySeconds ?? cfgMin;

                c.SetLimits(cfgMin, cfgMax);
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                    {
                        float val = logic.Terminal_PushFrequencySeconds;
                        sb.Append(Math.Round(val, 2)).Append(" " + MySpaceTexts.Plural_Seconds);
                    }
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
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCUseItemLimits");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_SetRequests_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_SetRequests_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests;
                c.Enabled = (b) => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeRequest ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_OperationModeRequest = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + "CheckboxLCUseItemLimits");
                c.Title = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_UseItemLimits_Title");
                c.Tooltip = MyStringId.GetOrCompute("TerminalControl_LogisticsComputer_UseItemLimits_Tooltip");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests;
                c.Enabled = (b) => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests; // to see how the grayed out ones look

                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeRequest ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                        logic.Terminal_OperationModeRequest = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyGyro>(c);
            }
        }
    }
}
