using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using System.Text;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Config;

namespace Automata.Drone
{
    public class DroneControllerTerminalControls
    {
        public static ServerConfig.DroneControllerBlockConfig BlockConfig = ServerConfig.Instance.Drone;

        const string IdPrefix = AutomataSession.MOD_NAME + "_";
        static bool Done = false;

        private static readonly HashSet<string> defaultControlIdsToHide = new HashSet<string>
        {
            "ControlThrusters",
            "ControlWheels",
            "ControlGyros",
            "HandBrake",
            "Park",
            "DampenersOverride",
            "HorizonIndicator",
            "MainCockpit",
            "TargetLocking",
            "OpenToolbar",
            "MainRemoteControl",
            "Control",
            "AutoPilot",
            "CollisionAvoidance",
            "DockingMode",
            "CameraList",
            "FlightMode",
            "Direction",
            "SpeedLimit",
            "WaypointList",
            "Open Toolbar",
            "RemoveWaypoint",
            "MoveUp",
            "MoveDown",
            "AddWaypoint",
            "GpsList",
            "Reset",
            "Copy",
            "Paste",
            "GpsList"
        };
        private static readonly HashSet<string> defaultActionIdsToHide = new HashSet<string>
        {
            "ControlThrusters",
            "ControlWheels",
            "ControlGyros",
            "HandBrake",
            "Park",
            "DampenersOverride",
            "HorizonIndicator",
            "MainCockpit",
            "TargetLocking",
            "MainRemoteControl",
            "Control",
            "AutoPilot",
            "AutoPilot_On",
            "AutoPilot_Off",
            "CollisionAvoidance",
            "CollisionAvoidance_On",
            "CollisionAvoidance_Off",
            "DockingMode",
            "DockingMode_On",
            "DockingMode_Off",
            "SetFlightMode_BlockPropertyTitle_FlightMode_Patrol",
            "SetFlightMode_BlockPropertyTitle_FlightMode_Circle",
            "SetFlightMode_BlockPropertyTitle_FlightMode_OneWay",
            "IncreaseSpeedLimit",
            "DecreaseSpeedLimit",
            "Forward",
            "Backward",
            "Left",
            "Right",
            "Up",
            "Down",
            "Reset",
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
        private static DroneControllerBlock GetBlock(IMyTerminalBlock block)
        {
            if (block != null && block.GameLogic != null) return block.GameLogic.GetAs<DroneControllerBlock>();
            return null;
        }

        static bool AppendedCondition(IMyTerminalBlock block)
        {
            // if block has this gamelogic component then return false to hide the control/action.
            return block?.GameLogic?.GetAs<DroneControllerBlock>() == null;
        }
        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<DroneControllerBlock>() != null;
        }
        private static void AddSlider(string id, string title, string tooltip, float min, float max,
            Func<DroneControllerBlock, float> get, Action<DroneControllerBlock, float> set, Action<DroneControllerBlock, StringBuilder> write)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = CustomVisibleCondition;
            c.SetLimits(min, max);
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : min; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) write(l, sb); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddCheckbox(string id, string title, string tooltip,
            Func<DroneControllerBlock, bool> get, Action<DroneControllerBlock, bool> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddColor(string id, string title, string tooltip,
            Func<DroneControllerBlock, Color> get, Action<DroneControllerBlock, Color> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : Color.White; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddLabel(string id, string text)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + id);
            c.Label = MyStringId.GetOrCompute(text);
            c.SupportsMultipleBlocks = true;
            c.Visible = CustomVisibleCondition;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddSeparator(string id)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>(IdPrefix + id);
            c.SupportsMultipleBlocks = true;
            c.Visible = CustomVisibleCondition;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AppendSpeed(StringBuilder sb, float v)
        {
            sb.Append(v.ToString(v < 10 ? "F1" : "F0")).Append(" m/s");
        }

        private static void AppendPercent(StringBuilder sb, float v)
        {
            sb.Append(v.ToString("F0")).Append('%');
        }

        private static void AddWorkModeCheckbox(string id, string title, string tooltip, Construction.WorkModes flag)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip + " Stand-alone drones work inside their observation area.");
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Enabled = JobsCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null && l.Terminal_GetWorkMode(flag); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetWorkMode(flag, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        static bool HomeConnectorVisibleCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.HasHomeConnector;
        }

        // in blocks of 2.5 m: size 1–10, offset ±5 (the block clamps and rounds again)
        private static void AddObservationSlider(string id, string title, string tooltip, int axis, bool size)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = HomeConnectorVisibleCondition;
            if (size) c.SetLimits(DroneControllerBlock.OBSERVATION_SIZE_MIN, DroneControllerBlock.OBSERVATION_SIZE_MAX);
            else c.SetLimits(-DroneControllerBlock.OBSERVATION_OFFSET_MAX, DroneControllerBlock.OBSERVATION_OFFSET_MAX);
            c.Getter = (b) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return size ? 5f : 0f;
                return size ? logic.Terminal_GetObservationSize(axis) : logic.Terminal_GetObservationOffset(axis);
            };
            c.Setter = (b, v) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return;
                if (size) logic.Terminal_SetObservationSize(axis, v);
                else logic.Terminal_SetObservationOffset(axis, v);
            };
            c.Writer = (b, sb) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return;
                int v = size ? logic.Terminal_GetObservationSize(axis) : logic.Terminal_GetObservationOffset(axis);
                sb.Append(v).Append(v == 1 || v == -1 ? " block (" : " blocks (")
                  .Append((v * DroneControllerBlock.OBSERVATION_UNIT).ToString("F1")).Append(" m)");
            };
            c.SupportsMultipleBlocks = false;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        /// <summary>
        /// Hides default controls in the inherited Remote Control.
        /// </summary>
        public static void HideDefaultControls()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);

            foreach (IMyTerminalControl c in controls)
            {
                if (defaultControlIdsToHide.Contains(c.Id))
                {
                    c.Visible = TerminalChainedDelegate.Create(c.Visible, AppendedCondition);
                }
            }
        }

        /// <summary>
        /// Hides default actions in the Programmable Block.
        /// </summary>
        public static void HideDefaultActions()
        {
            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<IMyRemoteControl>(out actions);

            foreach (IMyTerminalAction a in actions)
            {
                if (defaultActionIdsToHide.Contains(a.Id))
                {
                    a.Enabled = TerminalChainedDelegate.Create(a.Enabled, AppendedCondition);
                }
            }
        }
        #region Control helpers (conditions, compact builders)
        static bool AiOffCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && !logic.Terminal_Enabled;
        }

        // Job controls: any mode but "managed by player"
        static bool JobsCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && !logic.IsManagedByPlayer;
        }

        // Debug controls: "managed by player" only
        static bool DebugCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.IsManagedByPlayer;
        }

        static bool NeverCondition(IMyTerminalBlock b)
        {
            return false;
        }

        private static void AddButton(string id, string title, string tooltip, Action<DroneControllerBlock> action,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Action = (b) => { var l = GetBlock(b); if (l != null) action(l); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddTextbox(string id, string title, string tooltip,
            Func<DroneControllerBlock, StringBuilder> get, Action<DroneControllerBlock, StringBuilder> set,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : new StringBuilder(); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddListbox(string id, string title, string tooltip, int rows, bool multiselect,
            Action<DroneControllerBlock, List<MyTerminalControlListBoxItem>, List<MyTerminalControlListBoxItem>> content,
            Action<DroneControllerBlock, List<MyTerminalControlListBoxItem>> select,
            Func<IMyTerminalBlock, bool> enabled = null)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Multiselect = multiselect;
            c.VisibleRowsCount = rows;
            c.ListContent = (b, items, selected) => { var l = GetBlock(b); if (l != null) content(l, items, selected); };
            c.ItemSelected = (b, selected) => { var l = GetBlock(b); if (l != null) select(l, selected); };
            c.SupportsMultipleBlocks = false;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void SetEnabled(string id, Func<IMyTerminalBlock, bool> enabled)
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            foreach (var c in controls)
                if (c.Id == IdPrefix + id) { c.Enabled = enabled; return; }
        }

        private static void AddSection(string id, string title)
        {
            AddSeparator("Separator_" + id);
            AddLabel("Label_" + id, title);
        }
        #endregion

        /// <summary>
        /// Layout, top to bottom (the block's own On/Off comes first): Enable AI, operation mode, home connector,
        /// power monitoring, LCD output, flight, jobs, debug.
        /// </summary>
        public static void CreateControls()
        {
            // --- AI ---
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_Enabled");
                c.Title = MyStringId.GetOrCompute("Enable AI");
                c.Tooltip = MyStringId.GetOrCompute("Turns the drone's AI on or off.\nOff: every order and job stops at once, the controls are handed back to the game, and a flying drone hovers where it is (the game's dampeners).");
                c.OnText = MyStringId.GetOrCompute("On");
                c.OffText = MyStringId.GetOrCompute("Off");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_Enabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_Enabled = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_OperationMode");
                c.Title = MyStringId.GetOrCompute("Operation mode");
                c.Tooltip = MyStringId.GetOrCompute("AI must be disabled to change.\nStand-alone: works its observation area from its home connector.\nManaged by scheduler: takes jobs from an orchestrator.\nManaged by player: only your orders (debug section).");
                c.Visible = CustomVisibleCondition;
                c.Enabled = AiOffCondition;
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.StandAlone, Value = MyStringId.GetOrCompute("Stand-alone") });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.ManagedByScheduler, Value = MyStringId.GetOrCompute("Managed by scheduler") });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.ManagedByPlayer, Value = MyStringId.GetOrCompute("Managed by player") });
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeValue ?? 0;
                c.Setter = (b, v) =>
                {
                    var l = GetBlock(b);
                    if (l != null && !l.Terminal_Enabled) l.Terminal_OperationModeValue = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // --- Home connector ---
            AddSection("Home", "Home connector");
            AddListbox("Listbox_HomeConnector", "Home connector",
                "Your own and your faction's connectors in range. The list refreshes automatically; use Scan to refresh now.",
                5, false, (l, items, sel) => l.Terminal_HomeConnectorListContent(items, sel), (l, sel) => l.Terminal_SelectHomeConnector(sel));
            AddButton("Button_ScanConnectors", "Scan",
                "Refresh the connector and beacon lists now (limited by server settings).", l => l.Terminal_ScanAnchors());
            AddTextbox("Textbox_ConnectorNameQuery", "Connector name",
                "Exact name of one of your (or your faction's) connectors further away than the list covers.",
                l => l.Textbox_ConnectorNameQuery, (l, v) => l.Textbox_ConnectorNameQuery = v);
            AddButton("Button_AddConnectorByName", "Add to list",
                "Looks up the connector named above and adds every match that is yours or your faction's to the list.",
                l => l.Terminal_AddConnectorByName());
            AddButton("Button_SetCurrentAsHome", "Set current as home",
                "Dock the drone by hand, then press: that connector becomes home, and this exact position and orientation is used for docking.",
                l => l.Terminal_SetCurrentAsHome());
            AddCheckbox("Checkbox_HomeNotStation", "Home connector is not station",
                "Tick when the home connector is on a ship or anything else that moves. The drone then finds home through the beacon selected below.",
                l => l.Terminal_HomeNotStation, (l, v) => { l.Terminal_HomeNotStation = v; });
            AddListbox("Listbox_HomeBeacon", "Home is relative to beacon",
                "Your own and your faction's beacons in range. Pick the one on the same ship as the home connector.",
                4, false, (l, items, sel) => l.Terminal_HomeBeaconListContent(items, sel), (l, sel) => l.Terminal_SelectHomeBeacon(sel),
                b => { var l = GetBlock(b); return l != null && l.Terminal_HomeNotStation; });
            AddButton("Button_GoHome", "Go home",
                "Fly to the home connector, dock at Safe speed and power down (antenna, controller and LCDs stay on).",
                l => l.Terminal_GoHome(), HomeConnectorVisibleCondition, true);
            AddButton("Button_StopOrders", "Stop",
                "Cancel the current flight order and construction work; the drone holds position.",
                l => l.Terminal_StopOrders(), null, true);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_ObservationAreaDraw");
                c.Title = MyStringId.GetOrCompute("Show observation area");
                c.Tooltip = MyStringId.GetOrCompute("Draws the observation area around the home connector. Switches off after 2 minutes.");
                c.OnText = MyStringId.GetOrCompute("On");
                c.OffText = MyStringId.GetOrCompute("Off");
                c.Visible = HomeConnectorVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_ObservationAreaDraw ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ObservationAreaDraw = v; };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddObservationSlider("Slider_ObservationSizeX", "Area width", "Width of the observation area along the connector's right axis, in blocks (1 block = 2.5 m)", 0, true);
            AddObservationSlider("Slider_ObservationSizeY", "Area height", "Height of the observation area along the connector's up axis, in blocks (1 block = 2.5 m)", 1, true);
            AddObservationSlider("Slider_ObservationSizeZ", "Area depth", "Depth of the observation area along the connector's forward axis, in blocks (1 block = 2.5 m)", 2, true);
            AddObservationSlider("Slider_ObservationOffsetX", "Area offset right", "Moves the area along the connector's right axis, in blocks", 0, false);
            AddObservationSlider("Slider_ObservationOffsetY", "Area offset up", "Moves the area along the connector's up axis, in blocks", 1, false);
            AddObservationSlider("Slider_ObservationOffsetZ", "Area offset forward", "Moves the area along the connector's forward axis, in blocks", 2, false);

            // --- Power monitoring ---
            AddSection("Power", "Power");
            AddCheckbox("Checkbox_MonitorBattery", "Monitor battery levels",
                "Drone will return home to recharge when battery is below threshold.",
                l => l.Terminal_MonitorBattery, (l, v) => { l.Terminal_MonitorBattery = v; l.Terminal_Refresh(); });
            AddSlider("Slider_BatteryRefuelThreshold", "Battery: recharge below",
                "Threshold: when the batteries' total charge drops below this, the drone stops what it is doing and flies home to recharge.", 5f, 50f,
                l => l.Terminal_BatteryRefuelThreshold, (l, v) => l.Terminal_BatteryRefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryRefuelThreshold));
            AddSlider("Slider_BatteryOperationalThreshold", "Battery: ready above",
                "Once home, the drone counts as recharged (and goes back to work) when the charge is back above this.", 10f, 95f,
                l => l.Terminal_BatteryOperationalThreshold, (l, v) => l.Terminal_BatteryOperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryOperationalThreshold));
            AddCheckbox("Checkbox_MonitorHydrogen", "Monitor H2 levels",
                "Drone will return home to refuel when H2 is below threshold.",
                l => l.Terminal_MonitorHydrogen, (l, v) => { l.Terminal_MonitorHydrogen = v; l.Terminal_Refresh(); });
            AddSlider("Slider_H2RefuelThreshold", "H2: refuel below",
                "Threshold: when the hydrogen tanks' total fill drops below this, the drone stops what it is doing and flies home to refuel.", 5f, 50f,
                l => l.Terminal_H2RefuelThreshold, (l, v) => l.Terminal_H2RefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2RefuelThreshold));
            AddSlider("Slider_H2OperationalThreshold", "H2: ready above",
                "Once home, the drone counts as refueled (and goes back to work) when the tanks are back above this.", 10f, 95f,
                l => l.Terminal_H2OperationalThreshold, (l, v) => l.Terminal_H2OperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2OperationalThreshold));
            SetEnabled("Slider_BatteryRefuelThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorBattery; });
            SetEnabled("Slider_BatteryOperationalThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorBattery; });
            SetEnabled("Slider_H2RefuelThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorHydrogen; });
            SetEnabled("Slider_H2OperationalThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorHydrogen; });
            AddCheckbox("Checkbox_AlwaysRefuel", "Refuel when docked",
                "Whenever the drone docks, set its batteries to Recharge and its hydrogen tanks to Stockpile, even when the levels are fine. Restored when it takes off. Reactors need no setting.",
                l => l.Terminal_AlwaysRefuel, (l, v) => l.Terminal_AlwaysRefuel = v);

            // --- LCD / terminal log (player-facing; the server log is set in the mod config) ---
            AddSection("Lcd", "LCD output");
            AddTextbox("Textbox_LCDScreenTag", "LCD tag",
                "LCD panels on the drone's own grid (not subgrids or docked grids) with this tag in their name show the drone's log.",
                l => l.Terminal_LCDScreenTag, (l, v) => l.Terminal_LCDScreenTag = v, null, true);
            AddColor("Color_LcdForeground", "Text color", "LCD text color. Errors in the header are red, warnings yellow.",
                l => l.Terminal_LcdForeground, (l, v) => l.Terminal_LcdForeground = v);
            AddColor("Color_LcdBackground", "Background color", "LCD background color.",
                l => l.Terminal_LcdBackground, (l, v) => l.Terminal_LcdBackground = v);
            AddSlider("Slider_LcdFontSize", "Font size",
                "LCD font size. Only as many log lines as fit are shown.", 0.1f, 2f,
                l => l.Terminal_LcdFontSize, (l, v) => l.Terminal_LcdFontSize = v, (l, sb) => sb.Append(l.Terminal_LcdFontSize.ToString("F2")));
            AddCheckbox("Checkbox_LcdShowHeader", "Show header",
                "Header lines: state, battery (POW) and hydrogen (H2) levels; then the current error (red) or warning (yellow).",
                l => l.Terminal_LcdShowHeader, (l, v) => l.Terminal_LcdShowHeader = v);

            // --- Flight ---
            AddSection("Flight", "Flight");
            AddSlider("Slider_MaxSpeed", "Max speed",
                "Cruise speed limit for any flight, in m/s (0-1000; the game's own speed limit still applies).\nNot tied to the other speeds: odd combinations show as an error in the LCD header.",
                DroneControllerBlock.MAX_SPEED_MIN, DroneControllerBlock.MAX_SPEED_MAX,
                l => l.Terminal_MaxSpeed, (l, v) => l.Terminal_MaxSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_MaxSpeed));
            AddSlider("Slider_ApproachSpeed", "Approach speed",
                "Speed over the last 10 m before a target: tool work, waypoints that stop, the start of the final approach (0-100 m/s).\nShould not be above Max speed.",
                DroneControllerBlock.APPROACH_SPEED_MIN, DroneControllerBlock.APPROACH_SPEED_MAX,
                l => l.Terminal_ApproachSpeed, (l, v) => l.Terminal_ApproachSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_ApproachSpeed));
            AddSlider("Slider_SafeSpeed", "Safe speed",
                "Speed for delicate moves: the final docking approach to a connector (0-50 m/s).\nShould not be above Approach speed.",
                DroneControllerBlock.SAFE_SPEED_MIN, DroneControllerBlock.SAFE_SPEED_MAX,
                l => l.Terminal_SafeSpeed, (l, v) => l.Terminal_SafeSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_SafeSpeed));
            AddSlider("Slider_WaypointTolerance", "Waypoint tolerance",
                "How close the drone must get to a waypoint for it to count as reached, in metres. Docking and tool work use their own, tighter tolerances.",
                DroneControllerBlock.WAYPOINT_TOLERANCE_MIN, DroneControllerBlock.WAYPOINT_TOLERANCE_MAX,
                l => l.Terminal_WaypointTolerance, (l, v) => l.Terminal_WaypointTolerance = v,
                (l, sb) => sb.Append(l.Terminal_WaypointTolerance.ToString("F1")).Append(" m"));
            AddCheckbox("Checkbox_AlignToPGravity", "Align to P-Gravity",
                "In planetary gravity, keep the drone level: pitch and roll stay within the limits below.",
                l => l.Terminal_AlignToPGravity, (l, v) => { l.Terminal_AlignToPGravity = v; l.Terminal_Refresh(); });
            AddSlider("Slider_MaxPitchDegrees", "Max pitch deviation",
                "Maximum pitch away from level while aligned to gravity (degrees).", 0f, 90f,
                l => l.Terminal_MaxPitchDegrees, (l, v) => l.Terminal_MaxPitchDegrees = v,
                (l, sb) => sb.Append(l.Terminal_MaxPitchDegrees.ToString("F1")).Append("°"));
            AddSlider("Slider_MaxRollDegrees", "Max roll deviation",
                "Maximum roll away from level while aligned to gravity (degrees).", 0f, 90f,
                l => l.Terminal_MaxRollDegrees, (l, v) => l.Terminal_MaxRollDegrees = v,
                (l, sb) => sb.Append(l.Terminal_MaxRollDegrees.ToString("F1")).Append("°"));
            SetEnabled("Slider_MaxPitchDegrees", b => { var l = GetBlock(b); return l != null && l.Terminal_AlignToPGravity; });
            SetEnabled("Slider_MaxRollDegrees", b => { var l = GetBlock(b); return l != null && l.Terminal_AlignToPGravity; });
            AddSlider("Slider_MaxLoadGravity", "Max load (gravity)",
                "Percent of max load. The drone stops collecting when this is exceeded; construction batches are sized to it.\n100% = the heaviest total mass the up thrusters can hover in the current gravity (1 g when in space). The value in brackets is that mass limit.",
                0f, 200f,
                l => l.Terminal_MaxLoadGravity, (l, v) => l.Terminal_MaxLoadGravity = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, true));
            AddSlider("Slider_MaxLoadSpace", "Max load (space)",
                "Percent of max load. The drone stops collecting when this is exceeded; construction batches are sized to it.\n100% = the heaviest total mass the weakest thruster group can still accelerate at 0.1 g. The value in brackets is that mass limit.",
                0f, 200f,
                l => l.Terminal_MaxLoadSpace, (l, v) => l.Terminal_MaxLoadSpace = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, false));

            // --- Jobs (any mode but "managed by player") ---
            AddSection("Jobs", "Jobs");
            AddLabel("Label_WorkModes", "Work modes");
            AddWorkModeCheckbox("Checkbox_WeldUnfinished", "Weld unfinished blocks",
                "Finish partly built blocks inside the observation area.", Construction.WorkModes.WeldUnfinishedBlocks);
            AddWorkModeCheckbox("Checkbox_RepairDamaged", "Repair damaged blocks",
                "Repair damaged or deformed blocks inside the observation area.", Construction.WorkModes.RepairDamagedBlocks);
            AddWorkModeCheckbox("Checkbox_WeldProjected", "Build projections",
                "Build projected blocks inside the observation area, centre first.", Construction.WorkModes.WeldProjectedBlocks);
            AddWorkModeCheckbox("Checkbox_Grind", "Grind marked blocks",
                "Grind down blocks painted with the grind colour inside the observation area.", Construction.WorkModes.Grind);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyRemoteControl>(IdPrefix + "Color_GrindColor");
                c.Title = MyStringId.GetOrCompute("Grind colour");
                c.Tooltip = MyStringId.GetOrCompute("Blocks painted this colour are ground down when 'Grind marked blocks' is on.");
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_GrindColor ?? Color.Red;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_GrindColor = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_BehaviourProfile");
                c.Title = MyStringId.GetOrCompute("Behaviour profile");
                c.Tooltip = MyStringId.GetOrCompute("A special role for this drone (cargo carrier, scout, rover). Not available yet.");
                c.Visible = CustomVisibleCondition;
                c.Enabled = NeverCondition;   // not implemented yet
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.None, Value = MyStringId.GetOrCompute("None") });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsCargoDrone, Value = MyStringId.GetOrCompute("Cargo drone") });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsScoutDrone, Value = MyStringId.GetOrCompute("Scout drone") });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsRover, Value = MyStringId.GetOrCompute("Rover") });
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_BehaviourProfileValue ?? 0;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_BehaviourProfileValue = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddLabel("Label_ExcludedCapabilities", "Excluded capabilities");
            AddListbox("Listbox_Capabilities", "Capabilities",
                "What this drone can do. Select entries and press 'Exclude selected' to keep it from bidding for jobs that need them.",
                5, true, (l, items, sel) => l.Terminal_CapabilityListContent(items, sel), (l, sel) => l.Terminal_SelectCapabilities(sel), JobsCondition);
            AddListbox("Listbox_ExcludedCapabilities", "Excluded",
                "Capabilities this drone won't bid with (e.g. no jobs in space, even though it can fly there). Only used when bidding for jobs.",
                4, true, (l, items, sel) => l.Terminal_ExcludedListContent(items, sel), (l, sel) => l.Terminal_SelectExclusions(sel), JobsCondition);
            AddButton("Button_ExcludeSelected", "Exclude selected",
                "Moves the capabilities selected in the first list to the excluded list.", l => l.Terminal_ExcludeSelected(), JobsCondition);
            AddButton("Button_RemoveSelectedExclusions", "Remove selected",
                "Removes the capabilities selected in the excluded list.", l => l.Terminal_RemoveSelectedExclusions(), JobsCondition);
            AddButton("Button_ClearExclusions", "Clear all",
                "Empties the excluded list.", l => l.Terminal_ClearExclusions(), JobsCondition, true);

            // --- Debug ("managed by player" only) ---
            AddSection("Debug", "Debug (managed by player)");
            {
                // Always enabled: a view, not an order (any operation mode, AI on or off)
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_DrawNavigationTargets");
                c.Title = MyStringId.GetOrCompute("Draw navigation targets");
                c.Tooltip = MyStringId.GetOrCompute("Red dots on the drone's navigation targets (approach points, waypoints, final target), blue lines between them, starting at the drone. Switches off after 2 minutes.");
                c.OnText = MyStringId.GetOrCompute("On");
                c.OffText = MyStringId.GetOrCompute("Off");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_DrawNavigationTargets ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_DrawNavigationTargets = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddTextbox("Textbox_Debug_SetOrientationTargetInput", "Set orientation target",
                "Orient the drone to the target GPS coordinate.",
                l => l.Terminal_Debug_SetOrientationTargetInput, (l, v) => l.Terminal_Debug_SetOrientationTargetInput = v, DebugCondition);
            AddButton("Button_Debug_SetOrientationTargetAction", "Orient", "Turn to face the target above.",
                l => l.Terminal_Debug_SetOrientationTarget(), DebugCondition);
            AddTextbox("Textbox_Debug_NavigationTargetGPS", "Navigate to target", "Navigate the drone to target.",
                l => l.Textbox_Debug_NavigationTargetGPS, (l, v) => l.Textbox_Debug_NavigationTargetGPS = v, DebugCondition);
            AddTextbox("Textbox_Debug_NavigationTargetGPSApproachFrom", "Approach target from:", "Optional position to approach target from",
                l => l.Textbox_Debug_NavigationTargetGPSApproachFrom, (l, v) => l.Textbox_Debug_NavigationTargetGPSApproachFrom = v, DebugCondition);
            AddButton("Button_Debug_NavigationTargetGPSActionTrigger", "Navigate", "Fly to the target above.",
                l => l.Terminal_Debug_NavigateToTarget(), DebugCondition);
            AddButton("Button_Debug_QueueWaypoint", "Queue waypoint",
                "Adds the target above as the next leg; the current leg passes its waypoint without stopping.",
                l => l.Terminal_Debug_QueueWaypoint(), DebugCondition);
            AddListbox("Listbox_Debug_Mount", "Mount to place",
                "Welder / grinder / drill: the target ends up on the edge of the tool's reach.",
                4, false, (l, items, sel) => l.Terminal_Debug_MountListContent(items, sel), (l, sel) => l.Terminal_Debug_SelectMount(sel), DebugCondition);
            AddTextbox("Textbox_Debug_PlaceMountTargetGPS", "Mount target", "GPS of the point the mount should work on.",
                l => l.Textbox_Debug_PlaceMountTargetGPS, (l, v) => l.Textbox_Debug_PlaceMountTargetGPS = v, DebugCondition);
            AddButton("Button_Debug_PlaceMount", "Place mount", "Moves the drone so the selected mount reaches the target.",
                l => l.Terminal_Debug_PlaceMount(), DebugCondition);
            AddListbox("Listbox_Debug_Anchor", "Relative to",
                "Home connector or one of your / your faction's beacons in range.",
                4, false, (l, items, sel) => l.Terminal_Debug_AnchorListContent(items, sel), (l, sel) => l.Terminal_Debug_SelectAnchor(sel), DebugCondition);
            AddTextbox("Textbox_Debug_RelativeOffset", "Offset (right, up, forward)", "Metres in the anchor block's frame, e.g. 0, 5, 20",
                l => l.Textbox_Debug_RelativeOffset, (l, v) => l.Textbox_Debug_RelativeOffset = v, DebugCondition);
            AddButton("Button_Debug_NavigateRelative", "Navigate relative", "Matches the anchor's speed first, then moves to the offset.",
                l => l.Terminal_Debug_NavigateRelative(), DebugCondition);
        }
    }
}
