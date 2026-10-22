using System;
using System.Text;

using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Construction
{
    /// <summary>Construction computer terminal: on / off, what to look for, the area, scan interval, job size, grind color.</summary>
    public static class ConstructionComputerTerminalControls
    {
        const string IdPrefix = AutomataSession.MOD_NAME + "_ConstructionComputer_";
        static bool Done = false;

        const string ICON_PLUS = @"Textures\GUI\Icons\HUD 2017\plus.dds";
        const string ICON_DASH = @"Textures\GUI\Icons\HUD 2017\Dash.dds";
        const string ICON_TOGGLE = @"Textures\GUI\Icons\Actions\Toggle.dds";
        const string ICON_ON = @"Textures\GUI\Icons\Actions\SwitchOn.dds";
        const string ICON_OFF = @"Textures\GUI\Icons\Actions\SwitchOff.dds";


        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            CreateControls();
            CreateActions();
            if (Log.DebugEnabled) Log.Debug("Created CC terminal controls");
        }

        static ConstructionComputerBlock GetBlock(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null ? b.GameLogic.GetAs<ConstructionComputerBlock>() : null;
        }

        static bool Visible(IMyTerminalBlock b)
        {
            return GetBlock(b) != null;
        }

        static void Add(IMyTerminalControl c)
        {
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }

        static void Separator()
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            Add(c);
        }

        static void Mode(string id, MyStringId title, MyStringId tooltip, WorkModes mode)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.Getter = b => { var l = GetBlock(b); return l != null && l.Terminal_GetMode(mode); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetMode(mode, v); };
            Add(c);
        }

        static void Slider(string id, MyStringId title, MyStringId tooltip, float min, float max, MyStringId unit,
                           Func<ConstructionComputerBlock, float> get, Action<ConstructionComputerBlock, float> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.SetLimits(min, max);
            c.Getter = b => { var l = GetBlock(b); return l != null ? get(l) : min; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, (float)Math.Round(v)); };
            c.Writer = (b, sb) => { var l = GetBlock(b); if (l == null) return; if (unit == MyStringId.NullOrEmpty) sb.Append(get(l).ToString("0")); else sb.AppendLoc(unit, get(l).ToString("0")); };
            Add(c);
        }

        static void AddAction(string id, string name, string icon, Action<ConstructionComputerBlock> action, Action<ConstructionComputerBlock, StringBuilder> writer)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyProgrammableBlock>(IdPrefix + id);
            a.Name = new StringBuilder(name);
            a.ValidForGroups = true;
            a.Icon = icon;
            a.Action = (b) => { var l = GetBlock(b); if (l != null) action(l); };
            a.Writer = (b, sb) => { var l = GetBlock(b); if (l != null && writer != null) writer(l, sb); };
            a.Enabled = Visible;
            MyAPIGateway.TerminalControls.AddAction<IMyProgrammableBlock>(a);
        }

        static void CreateActions()
        {
            AddAction(IdPrefix + "_X_Offset_Increase", Loc.Compose(MyStrings.ToolbarActionIncrease, MyStrings.ConstructionField, MyStrings.HorizontalOffset), ICON_PLUS, b => { b.Terminal_SetFieldOffset(0, b.Terminal_GetFieldOffset(0) + 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldOffset(0)); });
            AddAction(IdPrefix + "_X_Offset_Decrease", Loc.Compose(MyStrings.ToolbarActionDecrease, MyStrings.ConstructionField, MyStrings.HorizontalOffset), ICON_DASH, b => { b.Terminal_SetFieldOffset(0, b.Terminal_GetFieldOffset(0) - 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldOffset(0)); });
            AddAction(IdPrefix + "_Y_Offset_Increase", Loc.Compose(MyStrings.ToolbarActionIncrease, MyStrings.ConstructionField, MyStrings.VerticalOffset), ICON_PLUS, b => { b.Terminal_SetFieldOffset(1, b.Terminal_GetFieldOffset(1) + 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldOffset(1)); });
            AddAction(IdPrefix + "_Y_Offset_Decrease", Loc.Compose(MyStrings.ToolbarActionDecrease, MyStrings.ConstructionField, MyStrings.VerticalOffset), ICON_DASH, b => { b.Terminal_SetFieldOffset(1, b.Terminal_GetFieldOffset(1) - 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldOffset(1)); });
            AddAction(IdPrefix + "_Z_Offset_Increase", Loc.Compose(MyStrings.ToolbarActionIncrease, MyStrings.ConstructionField, MyStrings.ForwardOffset), ICON_PLUS, b => { b.Terminal_SetFieldOffset(2, b.Terminal_GetFieldOffset(2) + 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldOffset(2)); });
            AddAction(IdPrefix + "_Z_Offset_Decrease", Loc.Compose(MyStrings.ToolbarActionDecrease, MyStrings.ConstructionField, MyStrings.ForwardOffset), ICON_DASH, b => { b.Terminal_SetFieldOffset(2, b.Terminal_GetFieldOffset(2) - 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldOffset(2)); });

            AddAction(IdPrefix + "_X_Size_Increase", Loc.Compose(MyStrings.ToolbarActionIncrease, MyStrings.ConstructionField, MyStrings.Width), ICON_PLUS, b => { b.Terminal_SetFieldSize(0, b.Terminal_GetFieldSize(0) + 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldSize(0)); });
            AddAction(IdPrefix + "_X_Size_Decrease", Loc.Compose(MyStrings.ToolbarActionDecrease, MyStrings.ConstructionField, MyStrings.Width), ICON_DASH, b => { b.Terminal_SetFieldSize(0, b.Terminal_GetFieldSize(0) - 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldSize(0)); });
            AddAction(IdPrefix + "_Y_Size_Increase", Loc.Compose(MyStrings.ToolbarActionIncrease, MyStrings.ConstructionField, MyStrings.Height), ICON_PLUS, b => { b.Terminal_SetFieldSize(1, b.Terminal_GetFieldSize(1) + 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldSize(1)); });
            AddAction(IdPrefix + "_Y_Size_Decrease", Loc.Compose(MyStrings.ToolbarActionDecrease, MyStrings.ConstructionField, MyStrings.Height), ICON_DASH, b => { b.Terminal_SetFieldSize(1, b.Terminal_GetFieldSize(1) - 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldSize(1)); });
            AddAction(IdPrefix + "_Z_Size_Increase", Loc.Compose(MyStrings.ToolbarActionIncrease, MyStrings.ConstructionField, MyStrings.Depth), ICON_PLUS, b => { b.Terminal_SetFieldSize(2, b.Terminal_GetFieldSize(2) + 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldSize(2)); });
            AddAction(IdPrefix + "_Z_Size_Decrease", Loc.Compose(MyStrings.ToolbarActionDecrease, MyStrings.ConstructionField, MyStrings.Depth), ICON_DASH, b => { b.Terminal_SetFieldSize(2, b.Terminal_GetFieldSize(2) - 1); }, (b, sb) => { sb.Append(b.Terminal_GetFieldSize(2)); });

            AddAction(IdPrefix + "_ToggleEnableAI_ON", Loc.Compose(MyStrings.Toggle, MyStrings.EnableAI, MyStrings.On), ICON_ON, b => { if (!b.Terminal_IsEnabled) { b.Terminal_IsEnabled = true; } }, (b, sb) => sb.Append(Loc.T(MyStrings.On)));
            AddAction(IdPrefix + "_ToggleEnableAI_OFF", Loc.Compose(MyStrings.Toggle, MyStrings.EnableAI, MyStrings.Off), ICON_OFF, b => { if (b.Terminal_IsEnabled) { b.Terminal_IsEnabled = false; } }, (b, sb) => sb.Append(Loc.T(MyStrings.Off)));
            AddAction(IdPrefix + "_ToggleEnableAI_ON_OFF", Loc.Compose(MyStrings.Toggle, MyStrings.EnableAI, MyStrings.On_Off), ICON_TOGGLE, b => { b.Terminal_IsEnabled = !b.Terminal_IsEnabled; }, (b, sb) => sb.Append(b.Terminal_IsEnabled ? Loc.T(MyStrings.On) : Loc.T(MyStrings.Off)));

            AddAction(IdPrefix + "_ToggleConstructionField_ON", Loc.Compose(MyStrings.Toggle, MyStrings.Draw, MyStrings.ConstructionField, MyStrings.On), ICON_ON, b => { if (!b.Terminal_DrawArea) { b.Terminal_DrawArea = true; } }, (b, sb) => sb.Append(Loc.T(MyStrings.On)));
            AddAction(IdPrefix + "_ToggleConstructionField_OFF", Loc.Compose(MyStrings.Toggle, MyStrings.Draw, MyStrings.ConstructionField, MyStrings.Off), ICON_OFF, b => { if (b.Terminal_DrawArea) { b.Terminal_DrawArea = false; } }, (b, sb) => sb.Append(Loc.T(MyStrings.Off)));
            AddAction(IdPrefix + "_ToggleConstructionField_ON_OFF", Loc.Compose(MyStrings.Toggle, MyStrings.Draw, MyStrings.ConstructionField, MyStrings.On_Off), ICON_TOGGLE, b => { b.Terminal_DrawArea = !b.Terminal_DrawArea; }, (b, sb) => sb.Append(b.Terminal_DrawArea ? Loc.T(MyStrings.On) : Loc.T(MyStrings.Off)));

            AddAction(IdPrefix + "_ToggleUseBroadcastController_ON", Loc.Compose(MyStrings.Toggle, MyStrings.UseBroadcastController, MyStrings.On), ICON_ON, b => { if (!b.Terminal_UseBroadcastController) { b.Terminal_UseBroadcastController = true; } }, (b, sb) => sb.Append(Loc.T(MyStrings.On)));
            AddAction(IdPrefix + "_ToggleUseBroadcastController_OFF", Loc.Compose(MyStrings.Toggle, MyStrings.UseBroadcastController, MyStrings.Off), ICON_OFF, b => { if (b.Terminal_UseBroadcastController) { b.Terminal_UseBroadcastController = false; } }, (b, sb) => sb.Append(Loc.T(MyStrings.Off)));
            AddAction(IdPrefix + "_ToggleUseBroadcastController_ON_OFF", Loc.Compose(MyStrings.Toggle, MyStrings.UseBroadcastController, MyStrings.On_Off), ICON_TOGGLE, b => { b.Terminal_UseBroadcastController = !b.Terminal_UseBroadcastController; }, (b, sb) => sb.Append(b.Terminal_UseBroadcastController ? Loc.T(MyStrings.On) : Loc.T(MyStrings.Off)));
        }


        static void AreaSize(byte axis, MyStringId title, MyStringId tooltip)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "AreaSize" + axis);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.SetLimits(ConstructionComputerBlock.FIELD_SIZE_MIN, ConstructionComputerBlock.FIELD_SIZE_MAX);
            c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_GetFieldSize(axis) : 25; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetFieldSize(axis, (int)Math.Round(v)); };
            c.Writer = (b, sb) =>
            {
                var l = GetBlock(b);
                if (l == null) return;
                int n = l.Terminal_GetFieldSize(axis);
                sb.AppendLoc(n == 1 ? MyStrings.Block : MyStrings.Blocks, n);
            };
            Add(c);
        }

        static void AreaOffset(byte axis, MyStringId title, MyStringId tooltip)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "AreaOffset" + axis);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.SetLimits(-ConstructionComputerBlock.AREA_OFFSET_MAX, ConstructionComputerBlock.AREA_OFFSET_MAX);
            c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_GetFieldOffset(axis) : 0f; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetFieldOffset(axis, (int)Math.Round(v)); };
            c.Writer = (b, sb) =>
            {
                var l = GetBlock(b);
                if (l == null) return;
                int n = l.Terminal_GetFieldOffset(axis);
                sb.AppendLoc(n == 1 || n == -1 ? MyStrings.Block : MyStrings.Blocks, n);
            };
            Add(c);
        }

        static void CreateControls()
        {
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOffEnabled");
                c.Title = MySpaceTexts.BlockPropertyTitle_EnableAI;
                c.Tooltip = MyStrings.BLANK;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Getter = b => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_IsEnabled = v; };
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + "RetryJobs");
                c.Title = MyStrings.RetryJobs;
                c.Tooltip = MyStrings.RetryJobsTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Action = b => { var l = GetBlock(b); if (l != null) l.Terminal_RetryJobs(); };
                Add(c);
            }
            Separator();
            Mode("WeldUnfinished", MyStrings.WeldUnfinished, MyStrings.BLANK, WorkModes.WeldUnfinishedBlocks);
            Mode("Repair", MyStrings.Repair, MyStrings.BLANK, WorkModes.RepairDamagedBlocks);
            Mode("WeldProjected", MyStrings.WeldProjected, MyStrings.BLANK, WorkModes.WeldProjectedBlocks);
            Mode("Grind", MyStrings.Grind, MyStrings.BLANK, WorkModes.Grind);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyProgrammableBlock>(IdPrefix + "WorkPriority");
                c.Title = MyStrings.WeldPriority;
                c.Tooltip = MyStrings.BLANK;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.ComboBoxContent = WorkPriorities.ComboContent;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_WorkPriority : 0; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_WorkPriority = v; };
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyProgrammableBlock>(IdPrefix + "GrindColor");
                c.Title = MyStrings.GrindColor;
                c.Tooltip = MyStrings.GrindColorTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_GrindColor : Color.Red; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_GrindColor = v; };
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyProgrammableBlock>(IdPrefix + "WeldIgnoreColor");
                c.Title = MyStrings.WeldIgnoreColor;
                c.Tooltip = MyStrings.WeldIgnoreColorTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_WeldIgnoreColor : new Color(255, 0, 255); };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_WeldIgnoreColor = v; };
                Add(c);
            }
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOff_DrawArea");
                c.Title = MyStrings.ShowObservationArea;
                c.Tooltip = MyStrings.ShowObservationAreaTip;
                c.OnText = MyStrings.On;
                c.OffText = MyStrings.Off;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Getter = b => GetBlock(b)?.Terminal_DrawArea ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_DrawArea = v; };
                Add(c);
            }
            AreaSize(0, MyStrings.AreaWidth, MyStrings.BLANK);
            AreaSize(1, MyStrings.AreaHeight, MyStrings.BLANK);
            AreaSize(2, MyStrings.AreaDepth, MyStrings.BLANK);
            AreaOffset(0, MyStrings.OffsetRight, MyStrings.BLANK);
            AreaOffset(1, MyStrings.OffsetUp, MyStrings.BLANK);
            AreaOffset(2, MyStrings.OffsetForward, MyStrings.BLANK);
            Slider("ScanInterval", MyStrings.ScanInterval, MyStrings.BLANK, 10, 600, MyStrings.Seconds,
                   l => l.Terminal_ScanInterval, (l, v) => l.Terminal_ScanInterval = (int)v);
            Slider("BlocksPerJob", MyStrings.BlocksPerJob, MyStrings.BLANK, 1, 100, MyStringId.NullOrEmpty,
                   l => l.Terminal_BlocksPerJob, (l, v) => l.Terminal_BlocksPerJob = (int)v);
            NicknameControl.Add<IMyProgrammableBlock, ConstructionComputerBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[ConstructionComputer]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), false);
            BroadcastControllerControl.Add(IdPrefix + "UseBroadcastController", GetBlock, l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);
        }
    }
}
