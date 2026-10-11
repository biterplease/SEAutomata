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

namespace Automata.Construction
{
    /// <summary>Construction computer terminal: on / off, what to look for, the area, scan interval, job size, grind colour.</summary>
    public static class ConstructionComputerTerminalControls
    {
        #region UI strings
        private static readonly MyStringId StrRetryJobs = MyStringId.GetOrCompute("Retry jobs");
        private static readonly MyStringId StrRetryJobsTip = MyStringId.GetOrCompute("Retry unassigned jobs");
        private static readonly MyStringId StrEnableTip = MyStringId.GetOrCompute("Scan the area and announce construction jobs to orchestrators.");
        private static readonly MyStringId StrWeldUnfinished = MyStringId.GetOrCompute("Weld unfinished blocks");
        private static readonly MyStringId StrWeldUnfinishedTip = MyStringId.GetOrCompute("Blocks placed but not complete.");
        private static readonly MyStringId StrRepair = MyStringId.GetOrCompute("Repair damaged blocks");
        private static readonly MyStringId StrRepairTip = MyStringId.GetOrCompute("Complete blocks that took damage.");
        private static readonly MyStringId StrWeldProjected = MyStringId.GetOrCompute("Build projections");
        private static readonly MyStringId StrWeldProjectedTip = MyStringId.GetOrCompute("Blocks of projections in the area (projectors of the owner / faction).");
        private static readonly MyStringId StrGrind = MyStringId.GetOrCompute("Grind");
        private static readonly MyStringId StrGrindTip = MyStringId.GetOrCompute("Blocks painted the grind colour are ground down.");
        private static readonly MyStringId StrGrindColour = MyStringId.GetOrCompute("Grind colour");
        private static readonly MyStringId StrGrindColourTip = MyStringId.GetOrCompute("Blocks painted exactly this colour are ground (Grind mode). Default RGB(255,0,0).");
        private static readonly MyStringId StrWeldIgnoreColour = MyStringId.GetOrCompute("Weld ignore colour");
        private static readonly MyStringId StrWeldIgnoreColourTip = MyStringId.GetOrCompute("Blocks painted exactly this colour are never welded, repaired or built (projected blocks too). Default RGB(255,0,255).");
        private static readonly MyStringId StrShowArea = MyStringId.GetOrCompute("Show observation area");
        private static readonly MyStringId StrShowAreaTip = MyStringId.GetOrCompute("Draws the observation area (magenta; drone areas are green). Only for you; switches off after a while (server setting, 10 minutes by default).");
        private static readonly MyStringId StrOn = MyStringId.GetOrCompute("On");
        private static readonly MyStringId StrOff = MyStringId.GetOrCompute("Off");
        private static readonly MyStringId StrAreaWidth = MyStringId.GetOrCompute("Area width");
        private static readonly MyStringId StrAreaWidthTip = MyStringId.GetOrCompute("Observation area along the block's right axis, in blocks (1 block = 2.5 m).");
        private static readonly MyStringId StrAreaHeight = MyStringId.GetOrCompute("Area height");
        private static readonly MyStringId StrAreaHeightTip = MyStringId.GetOrCompute("Observation area along the block's up axis, in blocks (1 block = 2.5 m).");
        private static readonly MyStringId StrAreaDepth = MyStringId.GetOrCompute("Area depth");
        private static readonly MyStringId StrAreaDepthTip = MyStringId.GetOrCompute("Observation area along the block's forward axis, in blocks (1 block = 2.5 m).");
        private static readonly MyStringId StrOffsetRight = MyStringId.GetOrCompute("Area offset right");
        private static readonly MyStringId StrOffsetRightTip = MyStringId.GetOrCompute("Moves the area's centre along the block's right axis, in blocks (1 block = 2.5 m).");
        private static readonly MyStringId StrOffsetUp = MyStringId.GetOrCompute("Area offset up");
        private static readonly MyStringId StrOffsetUpTip = MyStringId.GetOrCompute("Moves the area's centre along the block's up axis, in blocks (1 block = 2.5 m).");
        private static readonly MyStringId StrOffsetForward = MyStringId.GetOrCompute("Area offset forward");
        private static readonly MyStringId StrOffsetForwardTip = MyStringId.GetOrCompute("Moves the area's centre along the block's forward axis, in blocks (1 block = 2.5 m).");
        private static readonly MyStringId StrScanInterval = MyStringId.GetOrCompute("Scan interval");
        private static readonly MyStringId StrScanIntervalTip = MyStringId.GetOrCompute("Seconds between checks of the area. Only grids that changed (blocks added, removed, damaged, welded up; projector progress; moved) are scanned again; everything is scanned every 2 minutes.");
        private static readonly MyStringId StrBlocksPerJob = MyStringId.GetOrCompute("Blocks per job");
        private static readonly MyStringId StrBlocksPerJobTip = MyStringId.GetOrCompute("Blocks announced together as one job (an orchestrator splits it into trips).");
        private static readonly MyStringId StrSeconds = MyStringId.GetOrCompute("{0} s");
        private static readonly MyStringId StrBlock = MyStringId.GetOrCompute("{0} block");
        private static readonly MyStringId StrBlocks = MyStringId.GetOrCompute("{0} blocks");
        private static readonly MyStringId StrBroadcastSends = MyStringId.GetOrCompute("nothing yet.");
        #endregion

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
                sb.AppendLoc(n == 1 ? StrBlock : StrBlocks, n);
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
                sb.AppendLoc(n == 1 || n == -1 ? StrBlock : StrBlocks, n);
            };
            Add(c);
        }

        static void CreateControls()
        {
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + "RetryJobs");
                c.Title = StrRetryJobs;
                c.Tooltip = StrRetryJobsTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Action = b => { var l = GetBlock(b); if (l != null) l.Terminal_RetryJobs(); };
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOffEnabled");
                c.Title = MySpaceTexts.BlockPropertyTitle_EnableAI;
                c.Tooltip = StrEnableTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Getter = b => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_IsEnabled = v; };
                Add(c);
            }
            Separator();
            Mode("WeldUnfinished", StrWeldUnfinished, StrWeldUnfinishedTip, WorkModes.WeldUnfinishedBlocks);
            Mode("Repair", StrRepair, StrRepairTip, WorkModes.RepairDamagedBlocks);
            Mode("WeldProjected", StrWeldProjected, StrWeldProjectedTip, WorkModes.WeldProjectedBlocks);
            Mode("Grind", StrGrind, StrGrindTip, WorkModes.Grind);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyProgrammableBlock>(IdPrefix + "WorkPriority");
                c.Title = WorkPriorities.Title;
                c.Tooltip = WorkPriorities.Tooltip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.ComboBoxContent = WorkPriorities.ComboContent;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_WorkPriority : 0; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_WorkPriority = v; };
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyProgrammableBlock>(IdPrefix + "GrindColor");
                c.Title = StrGrindColour;
                c.Tooltip = StrGrindColourTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_GrindColor : Color.Red; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_GrindColor = v; };
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyProgrammableBlock>(IdPrefix + "WeldIgnoreColor");
                c.Title = StrWeldIgnoreColour;
                c.Tooltip = StrWeldIgnoreColourTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_WeldIgnoreColor : new Color(255, 0, 255); };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_WeldIgnoreColor = v; };
                Add(c);
            }
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOff_DrawArea");
                c.Title = StrShowArea;
                c.Tooltip = StrShowAreaTip;
                c.OnText = StrOn;
                c.OffText = StrOff;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Getter = b => GetBlock(b)?.Terminal_DrawArea ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_DrawArea = v; };
                Add(c);
            }
            AreaSize(0, StrAreaWidth, StrAreaWidthTip);
            AreaSize(1, StrAreaHeight, StrAreaHeightTip);
            AreaSize(2, StrAreaDepth, StrAreaDepthTip);
            AreaOffset(0, StrOffsetRight, StrOffsetRightTip);
            AreaOffset(1, StrOffsetUp, StrOffsetUpTip);
            AreaOffset(2, StrOffsetForward, StrOffsetForwardTip);
            Slider("ScanInterval", StrScanInterval, StrScanIntervalTip, 10, 600, StrSeconds,
                   l => l.Terminal_ScanInterval, (l, v) => l.Terminal_ScanInterval = (int)v);
            Slider("BlocksPerJob", StrBlocksPerJob, StrBlocksPerJobTip, 1, 50, MyStringId.NullOrEmpty,
                   l => l.Terminal_BlocksPerJob, (l, v) => l.Terminal_BlocksPerJob = (int)v);
            NicknameControl.Add<IMyProgrammableBlock, ConstructionComputerBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[ConstructionComputer]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), false);
            BroadcastControllerControl.Add(IdPrefix + "UseBroadcastController", GetBlock, l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v, StrBroadcastSends);
        }
    }
}
