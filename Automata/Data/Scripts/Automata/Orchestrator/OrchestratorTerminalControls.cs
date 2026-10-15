using System;

using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Config;
using Automata.Util;
using Automata.Util.Strings;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Orchestrator terminal: the programmable block's own controls are hidden (On / Off, the generic terminal
    /// fields and the LCD controls stay); it has no settings of its own.
    /// </summary>
    public static class OrchestratorTerminalControls
    {
        #region UI strings
        private static readonly MyStringId StrBlocksMetres = MyStringId.GetOrCompute("{0} blocks ({1} m)");
        private static readonly MyStringId StrDroneStagingArea = MyStringId.GetOrCompute("Use parking protocol");
        private static readonly MyStringId StrDroneStagingAreaTip = MyStringId.GetOrCompute("Drones with landing gears park on grids instead of connector.");
        private static readonly MyStringId StrStagingDraw = MyStringId.GetOrCompute("Show parking area");
        private static readonly MyStringId StrStagingDrawTip = MyStringId.GetOrCompute("Draws the area for you. Switches off after a short time.");
        private static readonly MyStringId StrParking = MyStringId.GetOrCompute("Parking");
        private static readonly MyStringId StrParkingTip = MyStringId.GetOrCompute("Park on grid: anywhere this orchestrator's grid.\nParking area: in the area set below.\nVoxel: on rock near this orchestrator.");
        private static readonly MyStringId StrParkOnGrid = MyStringId.GetOrCompute("Park on grid");
        private static readonly MyStringId StrParkingArea = MyStringId.GetOrCompute("Parking area");
        private static readonly MyStringId StrVoxel = MyStringId.GetOrCompute("Voxel");
        private static readonly MyStringId StrLanes = MyStringId.GetOrCompute("Active lanes");
        private static readonly MyStringId StrLanesTip = MyStringId.GetOrCompute("Flight lanes of this network's drones.");
        private static readonly MyStringId StrLaneDelete = MyStringId.GetOrCompute("Delete lane");
        private static readonly MyStringId StrLaneResume = MyStringId.GetOrCompute("Resume lane");
        private static readonly MyStringId StrLaneVisualize = MyStringId.GetOrCompute("Show lane");
        private static readonly MyStringId StrLaneVisualizeTip = MyStringId.GetOrCompute("Draws selected lane pair.");
        private static readonly MyStringId StrLanesClear = MyStringId.GetOrCompute("Clear all lanes");
        private static readonly MyStringId StrLanesClearArmed = MyStringId.GetOrCompute("Press again to clear all");
        private static readonly MyStringId StrDesignatorCamera = MyStringId.GetOrCompute("Designator camera");
        private static readonly MyStringId StrDesignatorLcdTag = MyStringId.GetOrCompute("Designator LCD tag");
        private static readonly MyStringId StrDesignatorLcdTagTip = MyStringId.GetOrCompute("Place this tag on an LCD, select the Automata Log app to see designator output.");
        private static readonly MyStringId StrDesignatorCameraTip = MyStringId.GetOrCompute("Camera the \"Designator:\" toolbar actions aim with (15 km). On this grid, or via rotor / hinge / piston.");
        #endregion

        public static ServerConfig.OrchestratorBlockConfig BlockConfig = ServerConfig.Instance.OrchestratorServerSettings;
        static bool Done = false;

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            NicknameControl.Add<IMyProgrammableBlock, OrchestratorBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(AutomataSession.MOD_NAME + "_Orchestrator_LcdTag", GetBlock,
                              l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[Orchestrator]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), false);
            BroadcastControllerControl.Add(AutomataSession.MOD_NAME + "_Orchestrator_UseBroadcastController", GetBlock,
                                           l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);
            CreateStagingControls();
            CreateLaneControls();
            CreateDesignatorControls();
        }

        static void CreateDesignatorControls()
        {
            var sep = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            sep.Visible = Visible;
            Add(sep);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "DesignatorCamera");
                c.Title = StrDesignatorCamera;
                c.Tooltip = StrDesignatorCameraTip;
                c.Visible = Visible;
                c.SupportsMultipleBlocks = false;
                c.Multiselect = false;
                c.VisibleRowsCount = 4;
                c.ListContent = (b, items, selected) => { var l = GetBlock(b); if (l != null) l.Terminal_CameraList(items, selected); };
                c.ItemSelected = (b, selected) => { var l = GetBlock(b); if (l != null) l.Terminal_SelectCamera(selected); };
                Add(c);
            }
            LcdTagControl.Add(IdPrefix + "DesignatorLcdTag", GetBlock, l => l.Terminal_DesignatorLcdTag, (l, v) => l.Terminal_DesignatorLcdTag = v,
                              "[Designator]", StrDesignatorLcdTag, StrDesignatorLcdTagTip);
            DesignatorAction("Tag", "TAG", Pointer.DesignatorOrder.Tag, "Textures\\GUI\\Icons\\Actions\\Start.dds");
            DesignatorAction("Spot", "SPOT", Pointer.DesignatorOrder.Spot, "Textures\\GUI\\Icons\\Actions\\Start.dds");
            DesignatorAction("Area", "AREA", Pointer.DesignatorOrder.Area, "Textures\\GUI\\Icons\\Actions\\Start.dds");
            DesignatorAction("Engage", "ENGAGE", Pointer.DesignatorOrder.Engage, "Textures\\GUI\\Icons\\Actions\\Start.dds");
            DesignatorAction("Escort", "ESCORT", Pointer.DesignatorOrder.Escort, "Textures\\GUI\\Icons\\Actions\\Start.dds");
            DesignatorAction("Recall", "RECALL", Pointer.DesignatorOrder.Recall, "Textures\\GUI\\Icons\\Actions\\Reverse.dds");
            DesignatorAction("Clear", "CLEAR", Pointer.DesignatorOrder.Clear, "Textures\\GUI\\Icons\\Actions\\Decrease.dds");
        }

        // Toolbar only ("Designator: TAG" ...): pressed by a player, done by the server with the designator camera
        static void DesignatorAction(string id, string name, Pointer.DesignatorOrder order, string icon)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyProgrammableBlock>(IdPrefix + "Designator" + id);
            a.Name = new System.Text.StringBuilder("Designator: " + name);
            a.Icon = icon;
            a.ValidForGroups = false;
            a.Enabled = Visible;   // orchestrators only
            a.Action = b => { var l = GetBlock(b); if (l != null) l.Terminal_Designate(order); };
            a.Writer = (b, sb) => sb.Append(name);
            MyAPIGateway.TerminalControls.AddAction<IMyProgrammableBlock>(a);
        }

        static IMyTerminalControlButton laneClearButton;

        static void CreateLaneControls()
        {
            var sep = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            sep.Visible = Visible;
            Add(sep);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "LaneList");
                c.Title = StrLanes;
                c.Tooltip = StrLanesTip;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Multiselect = false;
                c.VisibleRowsCount = 6;
                c.ListContent = (b, items, selected) =>
                {
                    var l = GetBlock(b);
                    if (l == null) return;
                    var rows = l.Terminal_Lanes;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int bar = rows[i].IndexOf('|');
                        int number;
                        if (bar <= 0 || !int.TryParse(rows[i].Substring(0, bar), out number)) continue;
                        var text = MyStringId.GetOrCompute(rows[i].Substring(bar + 1));
                        var item = new MyTerminalControlListBoxItem(text, text, number);
                        items.Add(item);
                        if (number == l.Terminal_LaneSelected) selected.Add(item);
                    }
                };
                c.ItemSelected = (b, selected) =>
                {
                    var l = GetBlock(b);
                    if (l == null || selected.Count == 0 || !(selected[0].UserData is int)) return;
                    l.Terminal_LaneSelected = (int)selected[0].UserData;
                };
                Add(c);
            }
            LaneButton("LaneVisualize", StrLaneVisualize, StrLaneVisualizeTip, l => l.Terminal_VisualizeLane());
            LaneButton("LaneDelete", StrLaneDelete, MyStrings.BLANK, l => l.Terminal_DeleteLane());
            LaneButton("LaneResume", StrLaneResume, MyStrings.BLANK, l => l.Terminal_ResumeLane());
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + "LanesClear");
                c.Title = StrLanesClear;
                c.Tooltip = MyStrings.BLANK;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Action = b =>
                {
                    var l = GetBlock(b);
                    if (l == null) return;
                    bool armed;
                    l.Terminal_ClearLanes(out armed);
                    if (laneClearButton != null) laneClearButton.Title = armed ? StrLanesClearArmed : StrLanesClear;
                    RefreshTerminal(b);
                };
                laneClearButton = c;
                Add(c);
            }
        }

        static void LaneButton(string id, MyStringId title, MyStringId tooltip, Func<OrchestratorBlock, bool> act)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = false;
            c.Visible = Visible;
            c.Action = b => { var l = GetBlock(b); if (l != null && act(l)) RefreshTerminal(b); };
            Add(c);
        }

        const string IdPrefix = AutomataSession.MOD_NAME + "_Orchestrator_";

        static OrchestratorBlock GetBlock(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null ? b.GameLogic.GetAs<OrchestratorBlock>() : null;
        }

        static bool Visible(IMyTerminalBlock b) { return GetBlock(b) != null; }
        static bool ProtocolOn(IMyTerminalBlock b) { var l = GetBlock(b); return l != null && l.Terminal_StagingArea; }
        static bool ProtocolShown(IMyTerminalBlock b) { return Visible(b) && ProtocolOn(b); }
        // The area's sliders and drawing: the protocol on, with "Parking area" selected
        static bool AreaShown(IMyTerminalBlock b)
        {
            var l = GetBlock(b);
            return l != null && l.Terminal_StagingArea && l.Terminal_ParkingMode == (long)ParkingProtocolMode.ParkingArea;
        }

        static void Add(IMyTerminalControl c)
        {
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }

        static void Checkbox(string id, MyStringId title, MyStringId tooltip, Func<OrchestratorBlock, bool> get, Action<OrchestratorBlock, bool> set, bool needsArea)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = !needsArea;
            c.Visible = needsArea ? (Func<IMyTerminalBlock, bool>)AreaShown : Visible;   // area controls fold away with the checkbox
            c.Getter = b => { var l = GetBlock(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l == null) return; set(l, v); RefreshTerminal(b); };
            Add(c);
        }

        // Re-evaluates the controls' visibility (the area controls fold with the checkbox)
        static void RefreshTerminal(IMyTerminalBlock b)
        {
            if (b == null || MyAPIGateway.Utilities.IsDedicated) return;
            bool v = b.ShowInToolbarConfig;
            b.ShowInToolbarConfig = !v;
            b.ShowInToolbarConfig = v;
        }

        static void Slider(string id, MyStringId title, MyStringId tooltip, float min, float max, Func<OrchestratorBlock, float> get, Action<OrchestratorBlock, float> set)
        {
            BlockSlider(id, title, tooltip, min, max, get, set, (l, sb) => sb.AppendLoc(MyStrings.Metres, get(l).ToString("0")));
        }

        // Blocks of the orchestrator's grid, with the metres
        static void BlockSlider(string id, MyStringId title, MyStringId tooltip, float min, float max, Func<OrchestratorBlock, float> get, Action<OrchestratorBlock, float> set)
        {
            BlockSlider(id, title, tooltip, min, max, get, set, (l, sb) =>
            {
                float v = get(l);
                sb.AppendLoc(Math.Abs(v) == 1 ? MyStrings.BlockMetres : StrBlocksMetres, v.ToString("0"), (v * l.Terminal_GridSize).ToString("0.#"));
            });
        }

        static void BlockSlider(string id, MyStringId title, MyStringId tooltip, float min, float max, Func<OrchestratorBlock, float> get, Action<OrchestratorBlock, float> set,
                                Action<OrchestratorBlock, System.Text.StringBuilder> writer)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = AreaShown;
            c.SetLimits(min, max);
            c.Getter = b => { var l = GetBlock(b); return l != null ? get(l) : min; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, (float)Math.Round(v)); };
            c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) writer(l, sb); };
            Add(c);
        }

        static void CreateStagingControls()
        {
            var sep = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            sep.Visible = Visible;
            Add(sep);
            Checkbox("DroneStagingArea", StrDroneStagingArea,
                     StrDroneStagingAreaTip,
                     l => l.Terminal_StagingArea, (l, v) => l.Terminal_StagingArea = v, false);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyProgrammableBlock>(IdPrefix + "ParkingMode");
                c.Title = StrParking;
                c.Tooltip = StrParkingTip;
                c.Visible = ProtocolShown;
                c.SupportsMultipleBlocks = true;
                c.ComboBoxContent = list =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)ParkingProtocolMode.ParkOnGrid, Value = StrParkOnGrid });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)ParkingProtocolMode.ParkingArea, Value = StrParkingArea });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)ParkingProtocolMode.Voxel, Value = StrVoxel });
                };
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_ParkingMode : 0; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l == null) return; l.Terminal_ParkingMode = v; RefreshTerminal(b); };
                Add(c);
            }
            MyStringId[] sizeTitles = { MyStrings.AreaWidth, MyStrings.AreaHeight, MyStrings.AreaDepth };
            MyStringId[] offsetTitles = { MyStrings.OffsetRight, MyStrings.OffsetUp, MyStrings.OffsetForward };
            for (int a = 0; a < 3; a++)
            {
                int axis = a;
                BlockSlider("StagingSize" + a, sizeTitles[a], MyStrings.BLANK,
                            OrchestratorBlock.STAGING_SIZE_MIN, OrchestratorBlock.STAGING_SIZE_SLIDER_MAX,
                            l => l.Terminal_GetStagingSize(axis), (l, v) => l.Terminal_SetStagingSize(axis, v));
            }
            for (int a = 0; a < 3; a++)
            {
                int axis = a;
                BlockSlider("StagingOffset" + a, offsetTitles[a], MyStrings.BLANK,
                            -OrchestratorBlock.STAGING_OFFSET_SLIDER_MAX, OrchestratorBlock.STAGING_OFFSET_SLIDER_MAX,
                            l => l.Terminal_GetStagingOffset(axis), (l, v) => l.Terminal_SetStagingOffset(axis, v));
            }
            Checkbox("StagingDraw", StrStagingDraw, StrStagingDrawTip,
                     l => l.Terminal_StagingDraw, (l, v) => l.Terminal_StagingDraw = v, true);
        }
    }
}
