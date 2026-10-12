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

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            CreateControls();
            Log.Info("Created CC terminal controls");
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

        static void AreaSize(int axis, MyStringId title, MyStringId tooltip)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "AreaSize" + axis);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.SetLimits(ConstructionComputerBlock.AREA_SIZE_MIN, ConstructionComputerBlock.AREA_SIZE_MAX);
            c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_GetAreaSize(axis) : 25; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetAreaSize(axis, (int)Math.Round(v)); };
            c.Writer = (b, sb) =>
            {
                var l = GetBlock(b);
                if (l == null) return;
                int n = l.Terminal_GetAreaSize(axis);
                sb.AppendLoc(n == 1 ? MyStrings.Block : MyStrings.Blocks, n);
            };
            Add(c);
        }

        static void AreaOffset(int axis, MyStringId title, MyStringId tooltip)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "AreaOffset" + axis);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.SetLimits(-ConstructionComputerBlock.AREA_OFFSET_MAX, ConstructionComputerBlock.AREA_OFFSET_MAX);
            c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_GetAreaOffset(axis) : 0f; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetAreaOffset(axis, (int)Math.Round(v)); };
            c.Writer = (b, sb) =>
            {
                var l = GetBlock(b);
                if (l == null) return;
                int n = l.Terminal_GetAreaOffset(axis);
                sb.AppendLoc(n == 1 || n == -1 ? MyStrings.Block : MyStrings.Blocks, n);
            };
            Add(c);
        }

        static void CreateControls()
        {
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + "RetryJobs");
                c.Title = MyStrings.RetryJobs;
                c.Tooltip = MyStrings.RetryJobsTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Action = b => { var l = GetBlock(b); if (l != null) l.Terminal_RetryJobs(); };
                Add(c);
            }
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
            Slider("BlocksPerJob", MyStrings.BlocksPerJob, MyStrings.BLANK, 1, 50, MyStringId.NullOrEmpty,
                   l => l.Terminal_BlocksPerJob, (l, v) => l.Terminal_BlocksPerJob = (int)v);
            NicknameControl.Add<IMyProgrammableBlock, ConstructionComputerBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[ConstructionComputer]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), false);
            BroadcastControllerControl.Add(IdPrefix + "UseBroadcastController", GetBlock, l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);
        }
    }
}
