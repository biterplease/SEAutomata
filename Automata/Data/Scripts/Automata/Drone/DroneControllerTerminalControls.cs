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
            // "Control", // players should be able to "pilot" the drone into positions
            "AutoPilot",
            "CollisionAvoidance",
            "DockingMode",
            "CameraList",
            "FlightMode",
            "Direction",
            "SpeedLimit",
            "WaypointList",
            "Open Toolb",
            "RemoveWaypoint",
            "MoveUp",
            "MoveDown",
            "AddWaypoint",
            "GpsList",
            "Reset",
            "Copy",
            "Paste",
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
            // "Control", // players should be able to "pilot" the drone into positions
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

        static bool HomeConnectorVisibleCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.HasHomeConnector;
        }

        // size: 2.5–25 m; offset: ±12.5 m (the block clamps again)
        private static void AddObservationSlider(string id, string title, string tooltip, int axis, bool size)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = MyStringId.GetOrCompute(title);
            c.Tooltip = MyStringId.GetOrCompute(tooltip);
            c.Visible = HomeConnectorVisibleCondition;
            if (size) c.SetLimits(2.5f, 25f);
            else c.SetLimits(-12.5f, 12.5f);
            c.Getter = (b) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return size ? 10f : 0f;
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
                float v = size ? logic.Terminal_GetObservationSize(axis) : logic.Terminal_GetObservationOffset(axis);
                sb.Append(v.ToString("F1")).Append(" m");
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
        public static void CreateControls()
        {
            // === GENERAL SETTINGS ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_GeneralSettings");
            //     c.Label = MyStringId.GetOrCompute("General Settings");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // Enabled
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_Enabled");
                c.Title = MyStringId.GetOrCompute("Enabled");
                c.Tooltip = MyStringId.GetOrCompute("Enable or disable the drone controller");
                c.OnText = MyStringId.GetOrCompute("On");
                c.OffText = MyStringId.GetOrCompute("Off");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_Enabled ?? false;
                c.Setter = (b, v) =>
                {
                    var block = GetBlock(b);
                    if (block != null) {
                        block.Terminal_Enabled = v;
                    }
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_EnableInertialDampening");
                c.Title = MyStringId.GetOrCompute("Enable Inertial Dampening");
                c.Tooltip = MyStringId.GetOrCompute("Enable or disable inertial dampening for the drone controller");
                c.OnText = MyStringId.GetOrCompute("On");
                c.OffText = MyStringId.GetOrCompute("Off");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_EnableInertialDampening ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_EnableInertialDampening = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Operation Mode
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_OperationMode");
            //     c.Title = MyStringId.GetOrCompute("Operation Mode");
            //     c.Tooltip = MyStringId.GetOrCompute("Select drone operation mode");
            //     c.Visible = CustomVisibleCondition;
            //     c.ComboBoxContent = (list) =>
            //     {
            //         list.Add(new MyTerminalControlComboBoxItem() { Key = (long)OperationMode.StandAlone, Value = MyStringId.GetOrCompute("Stand Alone") });
            //         list.Add(new MyTerminalControlComboBoxItem() { Key = (long)OperationMode.ManagedByScheduler, Value = MyStringId.GetOrCompute("Managed by Scheduler") });
            //         list.Add(new MyTerminalControlComboBoxItem() { Key = (long)OperationMode.ManagedByPlayer, Value = MyStringId.GetOrCompute("Managed by Player") });
            //     };
            //     c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeValue ?? 0;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_OperationModeValue = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // === CONTROLLER SETTINGS ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Controller");
            //     c.Label = MyStringId.GetOrCompute("Controller Settings");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // Controller Forward Direction
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_ControllerForward");
            //     c.Title = MyStringId.GetOrCompute("Controller Forward Direction");
            //     c.Tooltip = MyStringId.GetOrCompute("Which direction this controller considers forward");
            //     c.Visible = CustomVisibleCondition;
            //     c.ComboBoxContent = (list) =>
            //     {
            //         foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
            //         {
            //             list.Add(new MyTerminalControlComboBoxItem() { Key = (long)dir, Value = MyStringId.GetOrCompute(dir.ToString()) });
            //         }
            //     };
            //     c.Getter = (b) => GetBlock(b)?.Terminal_ControllerForwardDirectionValue ?? (long)Base6Directions.Direction.Forward;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_ControllerForwardDirectionValue = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);

            // === HOME POSITION ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_HomePosition");
            //     c.Label = MyStringId.GetOrCompute("Home Position");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_HomePosition");
                c.Title = MyStringId.GetOrCompute("Write to custom info");
                c.Tooltip = MyStringId.GetOrCompute("write to custom info");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_HomePositionGPS ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_HomePositionGPS = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

                        // // Set Current Position as Home
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_SetCurrentAsHome");
                c.Title = MyStringId.GetOrCompute("Set current as home");
                c.Tooltip = MyStringId.GetOrCompute("Dock the drone by hand, then press: that connector becomes home, and this exact position and orientation is used for docking.");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_SetCurrentAsHome();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Home Position GPS
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_HomePosition");
            //     c.Title = MyStringId.GetOrCompute("Home GPS");
            //     c.Tooltip = MyStringId.GetOrCompute("Enter GPS coordinates for home position");
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_HomePositionGPS ?? new StringBuilder("");
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_HomePositionGPS = v;
            //     };
            //     c.SupportsMultipleBlocks = false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Set Current Position as Home
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_SetCurrentAsHome");
            //     c.Title = MyStringId.GetOrCompute("Set Current as Home");
            //     c.Tooltip = MyStringId.GetOrCompute("Set current drone position as home");
            //     c.Visible = CustomVisibleCondition;
            //     c.Action = (b) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_SetCurrentAsHome();
            //     };
            //     c.SupportsMultipleBlocks = false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Home Is Relative To (Hidden - TODO: implement beacon search)
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_HomeIsRelativeTo");
            //     c.Title = MyStringId.GetOrCompute("Home Relative To");
            //     c.Tooltip = MyStringId.GetOrCompute("Beacon to use as reference for home position");
            //     c.Visible = (b) => false; // TODO: implement beacon search
            //     c.ComboBoxContent = (list) => { };
            //     c.Getter = (b) => 0;
            //     // c.Setter = (b, v) => { };
            //     c.SupportsMultipleBlocks = false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // === DEBUG FLIGHT TESTS ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_DebugFlightTests");
            //     c.Label = MyStringId.GetOrCompute("Debug Flight Tests");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_DebugFlightABGPS");
            //     c.Title = MyStringId.GetOrCompute("Debug A-B GPS");
            //     c.Tooltip = MyStringId.GetOrCompute("GPS destination used by DebugFlight_AB");
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_DebugFlightAbGPS ?? new StringBuilder("");
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_DebugFlightAbGPS = v;
            //     };
            //     c.SupportsMultipleBlocks = false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_DebugFlightHover");
            //     c.Title = MyStringId.GetOrCompute("Debug Hover (+10m)");
            //     c.Tooltip = MyStringId.GetOrCompute("Runs DebugFlight_Hover: move 10m above current position");
            //     c.Visible = CustomVisibleCondition;
            //     c.Action = (b) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_DebugFlight_Hover();
            //     };
            //     c.SupportsMultipleBlocks = false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_DebugFlightAB");
            //     c.Title = MyStringId.GetOrCompute("Debug Flight A-B");
            //     c.Tooltip = MyStringId.GetOrCompute("Runs DebugFlight_AB using the GPS textbox value");
            //     c.Visible = CustomVisibleCondition;
            //     c.Action = (b) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_DebugFlight_AB();
            //     };
            //     c.SupportsMultipleBlocks = false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // === TASK AND CAPABILITY FILTERS ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Filters");
            //     c.Label = MyStringId.GetOrCompute("Task and Capability Filters");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Use Task Type Filters
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_UseTaskTypeFilters");
            //     c.Title = MyStringId.GetOrCompute("Use Task Type Filters");
            //     c.Tooltip = MyStringId.GetOrCompute("Enable filtering of task types this drone can handle");
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_UseTaskTypeFilters = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Task Type Filters Label
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_TaskTypeFiltersNote");
            //     c.Label = MyStringId.GetOrCompute("Task Type Filters:");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Create checkboxes for each task type
            // foreach (Orchestrator.TaskType taskType in Enum.GetValues(typeof(Orchestrator.TaskType)))
            // {
            //     if (taskType == 0) continue; // Skip None

            //     var currentTaskType = taskType;
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_TaskFilter_" + taskType.ToString());
            //     c.Title = MyStringId.GetOrCompute(taskType.ToString());
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
            //     c.Enabled = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
            //     c.Getter = (b) =>
            //     {
            //         var logic = GetBlock(b);
            //         return logic != null && (logic.Terminal_TaskTypeFilters & currentTaskType) != 0;
            //     };
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null)
            //         {
            //             if (v)
            //                 logic.Terminal_TaskTypeFilters |= currentTaskType;
            //             else
            //                 logic.Terminal_TaskTypeFilters &= ~currentTaskType;
            //         }
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Use Capability Filters
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_UseCapabilityFilters");
            //     c.Title = MyStringId.GetOrCompute("Use Capability Filters");
            //     c.Tooltip = MyStringId.GetOrCompute("Enable filtering of capabilities reported to scheduler");
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_UseCapabilityFilters ?? false;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_UseCapabilityFilters = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Capability Filters Label
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_CapabilityFiltersNote");
            //     c.Label = MyStringId.GetOrCompute("Capability Filters:");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = (b) => GetBlock(b)?.Terminal_UseCapabilityFilters ?? false;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Create checkboxes for each capability
            // foreach (Capabilities capability in Enum.GetValues(typeof(Capabilities)))
            // {
            //     if (capability == 0) continue; // Skip None

            //     var currentCapability = capability;
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_CapFilter_" + capability.ToString());
            //     c.Title = MyStringId.GetOrCompute(capability.ToString());
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = CustomVisibleCondition;
            //     c.Enabled = (b) => 
            //     {
            //         var block = GetBlock(b);
            //         if (block != null)
            //         {
            //             return CustomVisibleCondition(b) && block.Terminal_UseCapabilityFilters;
            //         }
            //         return false;
            //     };
            //     c.Getter = (b) =>
            //     {
            //         var logic = GetBlock(b);
            //         return logic != null && (logic.Terminal_CapabilityFilters & currentCapability) != 0;
            //     };
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null)
            //         {
            //             if (v)
            //                 logic.Terminal_CapabilityFilters |= currentCapability;
            //             else
            //                 logic.Terminal_CapabilityFilters &= ~currentCapability;
            //         }
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // === NAVIGATION SETTINGS ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Navigation");
            //     c.Label = MyStringId.GetOrCompute("Navigation Settings");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Waypoint Tolerance
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_WaypointTolerance");
            //     c.Title = MyStringId.GetOrCompute("Waypoint Tolerance");
            //     c.Tooltip = MyStringId.GetOrCompute("Distance from waypoint to consider reached (meters)");
            //     c.Visible = CustomVisibleCondition;
            //     c.SetLimits(5f, 50f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_WaypointTolerance ?? 5f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_WaypointTolerance = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_WaypointTolerance.ToString("F1")).Append(" m");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Approach Speed
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_ApproachSpeed");
            //     c.Title = MyStringId.GetOrCompute("Approach Speed");
            //     c.Tooltip = MyStringId.GetOrCompute("Speed when approaching work targets (m/s)");
            //     c.Visible = CustomVisibleCondition;
            //     c.SetLimits(1f, 50f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_ApproachSpeed ?? 5f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_ApproachSpeed = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_ApproachSpeed.ToString("F1")).Append(" m/s");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Speed Limit
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_SpeedLimit");
            //     c.Title = MyStringId.GetOrCompute("Speed Limit");
            //     c.Tooltip = MyStringId.GetOrCompute("Maximum speed (m/s)");
            //     c.Visible = CustomVisibleCondition;
            //     c.SetLimits(5f, 100f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_SpeedLimit ?? 50f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_SpeedLimit = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_SpeedLimit.ToString("F1")).Append(" m/s");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Docking Speed
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_DockingSpeed");
            //     c.Title = MyStringId.GetOrCompute("Docking Speed");
            //     c.Tooltip = MyStringId.GetOrCompute("Speed when docking (m/s)");
            //     c.Visible = CustomVisibleCondition;
            //     c.SetLimits(0.5f, 10f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_DockingSpeed ?? 2.5f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_DockingSpeed = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_DockingSpeed.ToString("F1")).Append(" m/s");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // Align To Planetary Gravity
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_AlignToPGravity");
                c.Title = MyStringId.GetOrCompute("Align to P-Gravity");
                c.Tooltip = MyStringId.GetOrCompute("Keep drone aligned with planetary gravity");
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_AlignToPGravity ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_AlignToPGravity = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // // Max Pitch Degrees
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_MaxPitchDegrees");
                c.Title = MyStringId.GetOrCompute("Max Pitch Deviation");
                c.Tooltip = MyStringId.GetOrCompute("Maximum pitch deviation from gravity alignment (degrees)");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) =>
                {
                    var block = GetBlock(b);
                    if (block != null)
                    {
                        return CustomVisibleCondition(b) && block.Terminal_AlignToPGravity;
                    }
                    return false;
                };
                c.SetLimits(0f, 90f);
                c.Getter = (b) => GetBlock(b)?.Terminal_MaxPitchDegrees ?? 10f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_MaxPitchDegrees = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_MaxPitchDegrees.ToString("F1")).Append("°");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Max Roll Degrees
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_MaxRollDegrees");
                c.Title = MyStringId.GetOrCompute("Max Roll Deviation");
                c.Tooltip = MyStringId.GetOrCompute("Maximum roll deviation from gravity alignment (degrees)");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) =>
                {
                    var block = GetBlock(b);
                    if (block != null)
                    {
                        return CustomVisibleCondition(b) && block.Terminal_AlignToPGravity;
                    }
                    return false;
                };
                c.SetLimits(0f, 90f);
                c.Getter = (b) => GetBlock(b)?.Terminal_MaxRollDegrees ?? 10f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_MaxRollDegrees = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_MaxRollDegrees.ToString("F1")).Append("°");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            // // LCD Screen Tag
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_LCDScreenTag");
            //     c.Title = MyStringId.GetOrCompute("LCD Screen Tag");
            //     c.Tooltip = MyStringId.GetOrCompute("Tag for LCD screens to display drone status");
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_LCDScreenTag ?? new StringBuilder("");
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_LCDScreenTag = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // === POWER MONITORING ===
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Power");
            //     c.Label = MyStringId.GetOrCompute("Power & Fuel Monitoring");
            //     c.SupportsMultipleBlocks = true;
            //     c.Visible = CustomVisibleCondition;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Monitor Hydrogen Levels
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_MonitorHydrogen");
            //     c.Title = MyStringId.GetOrCompute("Monitor Hydrogen Levels");
            //     c.Tooltip = MyStringId.GetOrCompute("Monitor hydrogen levels and auto-refuel when low");
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_MonitorHydrogen ?? false;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_MonitorHydrogen = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Hydrogen Refuel Threshold
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_H2RefuelThreshold");
            //     c.Title = MyStringId.GetOrCompute("H2 Refuel Threshold");
            //     c.Tooltip = MyStringId.GetOrCompute("Return to base when hydrogen below this percentage");
            //     c.Visible = CustomVisibleCondition;
            //     c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorHydrogen ?? false;
            //     c.SetLimits(5f, 50f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_H2RefuelThreshold ?? 25f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_H2RefuelThreshold = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_H2RefuelThreshold.ToString("F0")).Append("%");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Hydrogen Operational Threshold
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_H2OperationalThreshold");
            //     c.Title = MyStringId.GetOrCompute("H2 Operational Threshold");
            //     c.Tooltip = MyStringId.GetOrCompute("Resume operations when hydrogen above this percentage");
            //     c.Visible = CustomVisibleCondition;
            //     c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorHydrogen ?? false;
            //     c.SetLimits(10f, 95f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_H2OperationalThreshold ?? 50f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_H2OperationalThreshold = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_H2OperationalThreshold.ToString("F0")).Append("%");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Always Refuel When Docked
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_AlwaysRefuel");
            //     c.Title = MyStringId.GetOrCompute("Always Refuel When Docked");
            //     c.Tooltip = MyStringId.GetOrCompute("Refuel every time drone is docked");
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_AlwaysRefuel ?? false;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_AlwaysRefuel = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Monitor Battery Levels
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_MonitorBattery");
            //     c.Title = MyStringId.GetOrCompute("Monitor Battery Levels");
            //     c.Tooltip = MyStringId.GetOrCompute("Monitor battery levels and auto-recharge when low");
            //     c.OnText = MySpaceTexts.SwitchText_On;
            //     c.OffText = MySpaceTexts.SwitchText_Off;
            //     c.Visible = CustomVisibleCondition;
            //     c.Getter = (b) => GetBlock(b)?.Terminal_MonitorBattery ?? false;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_MonitorBattery = v;
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Battery Refuel Threshold
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_BatteryRefuelThreshold");
            //     c.Title = MyStringId.GetOrCompute("Battery Recharge Threshold");
            //     c.Tooltip = MyStringId.GetOrCompute("Return to base when battery below this percentage");
            //     c.Visible = CustomVisibleCondition;
            //     c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorBattery ?? false;
            //     c.SetLimits(5f, 50f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_BatteryRefuelThreshold ?? 20f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_BatteryRefuelThreshold = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_BatteryRefuelThreshold.ToString("F0")).Append("%");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }

            // // Battery Operational Threshold
            // {
            //     var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_BatteryOperationalThreshold");
            //     c.Title = MyStringId.GetOrCompute("Battery Operational Threshold");
            //     c.Tooltip = MyStringId.GetOrCompute("Resume operations when battery above this percentage");
            //     c.Visible = CustomVisibleCondition;
            //     c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorBattery ?? false;
            //     c.SetLimits(10f, 95f);
            //     c.Getter = (b) => GetBlock(b)?.Terminal_BatteryOperationalThreshold ?? 80f;
            //     c.Setter = (b, v) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) logic.Terminal_BatteryOperationalThreshold = v;
            //     };
            //     c.Writer = (b, sb) =>
            //     {
            //         var logic = GetBlock(b);
            //         if (logic != null) sb.Append(logic.Terminal_BatteryOperationalThreshold.ToString("F0")).Append("%");
            //     };
            //     c.SupportsMultipleBlocks = true;
            //     MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            // }
            // === NAVIGATION SPEEDS ===
            AddSeparator("Separator_Speeds");
            AddLabel("Label_Speeds", "Speeds");
            AddSlider("Slider_MaxSpeed", "Max speed",
                "Cruise speed limit for any flight, in m/s.",
                DroneControllerBlock.MAX_SPEED_MIN, DroneControllerBlock.MAX_SPEED_MAX,
                l => l.Terminal_MaxSpeed, (l, v) => l.Terminal_MaxSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_MaxSpeed));
            AddSlider("Slider_ApproachSpeed", "Approach speed",
                "Speed over the last 10 m before a target: tool work, waypoints that stop, the start of the final approach. Never above Max speed.",
                DroneControllerBlock.APPROACH_SPEED_MIN, DroneControllerBlock.APPROACH_SPEED_MAX,
                l => l.Terminal_ApproachSpeed, (l, v) => l.Terminal_ApproachSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_ApproachSpeed));
            AddSlider("Slider_SafeSpeed", "Safe speed",
                "Speed for delicate moves: the final docking approach to a connector. Never above Approach speed.",
                DroneControllerBlock.SAFE_SPEED_MIN, DroneControllerBlock.SAFE_SPEED_MAX,
                l => l.Terminal_SafeSpeed, (l, v) => l.Terminal_SafeSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_SafeSpeed));

            // === LOAD ===
            AddSeparator("Separator_Load");
            AddLabel("Label_Load", "Load");
            AddSlider("Slider_MaxLoadGravity", "Max load (gravity)",
                "MaxLoadGravity: percent value of max load. Drone will stop collecting when this value is exceeded.\n100% = the heaviest total mass the up thrusters can hover in the current gravity (1 g when in space). The value in brackets is that mass limit.",
                0f, 200f,
                l => l.Terminal_MaxLoadGravity, (l, v) => l.Terminal_MaxLoadGravity = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, true));
            AddSlider("Slider_MaxLoadSpace", "Max load (space)",
                "MaxLoadSpace: percent value of max load. Drone will stop collecting when this value is exceeded.\n100% = the heaviest total mass the weakest thruster group can still accelerate at 0.1 g. The value in brackets is that mass limit.",
                0f, 200f,
                l => l.Terminal_MaxLoadSpace, (l, v) => l.Terminal_MaxLoadSpace = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, false));

            // === POWER ===
            AddSeparator("Separator_Power");
            AddLabel("Label_Power", "Power");
            AddCheckbox("Checkbox_AlwaysRefuel", "Refuel when docked",
                "When docked, set batteries to Recharge and hydrogen tanks to Stockpile. Restored when the drone takes off. Reactors need no setting.",
                l => l.Terminal_AlwaysRefuel, (l, v) => l.Terminal_AlwaysRefuel = v);
            AddCheckbox("Checkbox_MonitorBattery", "Monitor battery",
                "Return to the home connector when the batteries drop below the recharge threshold.",
                l => l.Terminal_MonitorBattery, (l, v) => l.Terminal_MonitorBattery = v);
            AddSlider("Slider_BatteryRefuelThreshold", "Battery: recharge below",
                "Battery charge that sends the drone home to recharge.", 5f, 50f,
                l => l.Terminal_BatteryRefuelThreshold, (l, v) => l.Terminal_BatteryRefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryRefuelThreshold));
            AddSlider("Slider_BatteryOperationalThreshold", "Battery: ready above",
                "Battery charge at which the drone counts as recharged.", 10f, 95f,
                l => l.Terminal_BatteryOperationalThreshold, (l, v) => l.Terminal_BatteryOperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryOperationalThreshold));
            AddCheckbox("Checkbox_MonitorHydrogen", "Monitor hydrogen",
                "Return to the home connector when the hydrogen tanks drop below the refuel threshold.",
                l => l.Terminal_MonitorHydrogen, (l, v) => l.Terminal_MonitorHydrogen = v);
            AddSlider("Slider_H2RefuelThreshold", "Hydrogen: refuel below",
                "Hydrogen level that sends the drone home to refuel.", 5f, 50f,
                l => l.Terminal_H2RefuelThreshold, (l, v) => l.Terminal_H2RefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2RefuelThreshold));
            AddSlider("Slider_H2OperationalThreshold", "Hydrogen: ready above",
                "Hydrogen level at which the drone counts as refueled.", 10f, 95f,
                l => l.Terminal_H2OperationalThreshold, (l, v) => l.Terminal_H2OperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2OperationalThreshold));

            // === LCD OUTPUT ===
            AddSeparator("Separator_Lcd");
            AddLabel("Label_Lcd", "LCD output");
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_LCDScreenTag");
                c.Title = MyStringId.GetOrCompute("LCD tag");
                c.Tooltip = MyStringId.GetOrCompute("LCD panels on the drone's own grid (not subgrids or docked grids) with this tag in their name show the drone's log.");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_LCDScreenTag ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_LCDScreenTag = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddColor("Color_LcdForeground", "Text color", "LCD text color.",
                l => l.Terminal_LcdForeground, (l, v) => l.Terminal_LcdForeground = v);
            AddColor("Color_LcdBackground", "Background color", "LCD background color.",
                l => l.Terminal_LcdBackground, (l, v) => l.Terminal_LcdBackground = v);
            AddSlider("Slider_LcdFontSize", "Font size",
                "LCD font size. Only as many log lines as fit are shown.", 0.1f, 2f,
                l => l.Terminal_LcdFontSize, (l, v) => l.Terminal_LcdFontSize = v, (l, sb) => sb.Append(l.Terminal_LcdFontSize.ToString("F2")));
            AddCheckbox("Checkbox_LcdShowHeader", "Show header",
                "First line shows state, battery (POW) and hydrogen (H2) levels.",
                l => l.Terminal_LcdShowHeader, (l, v) => l.Terminal_LcdShowHeader = v);

            // === HOME CONNECTOR ===
            AddSeparator("Separator_Home");
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_HomeConnector");
                c.Label = MyStringId.GetOrCompute("Home connector");
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>(IdPrefix + "Listbox_HomeConnector");
                c.Title = MyStringId.GetOrCompute("Home connector");
                c.Tooltip = MyStringId.GetOrCompute("Your own and your faction's connectors in range. The list refreshes automatically; use Scan to refresh now.");
                c.Visible = CustomVisibleCondition;
                c.Multiselect = false;
                c.VisibleRowsCount = 5;
                c.ListContent = (b, items, selected) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_HomeConnectorListContent(items, selected);
                };
                c.ItemSelected = (b, selected) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_SelectHomeConnector(selected);
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_ScanConnectors");
                c.Title = MyStringId.GetOrCompute("Scan");
                c.Tooltip = MyStringId.GetOrCompute("Refresh the connector and beacon lists now (limited by server settings).");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_ScanAnchors();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_GoHome");
                c.Title = MyStringId.GetOrCompute("Go home");
                c.Tooltip = MyStringId.GetOrCompute("Fly to the home connector, dock at Safe speed and power down (antenna, controller and LCDs stay on).");
                c.Visible = CustomVisibleCondition;
                c.Enabled = HomeConnectorVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_GoHome();
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_ConnectorNameQuery");
                c.Title = MyStringId.GetOrCompute("Connector name");
                c.Tooltip = MyStringId.GetOrCompute("Exact name of one of your (or your faction's) connectors further away than the list covers.");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Textbox_ConnectorNameQuery ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Textbox_ConnectorNameQuery = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_AddConnectorByName");
                c.Title = MyStringId.GetOrCompute("Add to list");
                c.Tooltip = MyStringId.GetOrCompute("Looks up the connector named above and adds it to the list if it is yours or your faction's.");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_AddConnectorByName();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // === OBSERVATION AREA (stand-alone construction) - only once a home connector is selected ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_ObservationAreaDraw");
                c.Title = MyStringId.GetOrCompute("Show observation area");
                c.Tooltip = MyStringId.GetOrCompute("Draws the observation area around the home connector. Switches off after 2 minutes.");
                c.OnText = MyStringId.GetOrCompute("On");
                c.OffText = MyStringId.GetOrCompute("Off");
                c.Visible = HomeConnectorVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_ObservationAreaDraw ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_ObservationAreaDraw = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddObservationSlider("Slider_ObservationSizeX", "Area width", "Width of the observation area, along the connector's right axis (m)", 0, true);
            AddObservationSlider("Slider_ObservationSizeY", "Area height", "Height of the observation area, along the connector's up axis (m)", 1, true);
            AddObservationSlider("Slider_ObservationSizeZ", "Area depth", "Depth of the observation area, along the connector's forward axis (m)", 2, true);
            AddObservationSlider("Slider_ObservationOffsetX", "Area offset right", "Moves the area along the connector's right axis (m)", 0, false);
            AddObservationSlider("Slider_ObservationOffsetY", "Area offset up", "Moves the area along the connector's up axis (m)", 1, false);
            AddObservationSlider("Slider_ObservationOffsetZ", "Area offset forward", "Moves the area along the connector's forward axis (m)", 2, false);

            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Debug");
                c.Label = MyStringId.GetOrCompute("Debug");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_Debug_SetOrientationTargetInput");
                c.Title = MyStringId.GetOrCompute("Set orientation target");
                c.Tooltip = MyStringId.GetOrCompute("Orient the drone to the target GPS coordinate.");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_Debug_SetOrientationTargetInput ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_SetOrientationTargetInput = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

                        // // Set Current Position as Home
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_Debug_SetOrientationTargetAction");
                c.Title = MyStringId.GetOrCompute("Orient");
                c.Tooltip = MyStringId.GetOrCompute("");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_SetOrientationTarget();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_Debug_NavigationTargetGPS");
                c.Title = MyStringId.GetOrCompute("Navigate to target");
                c.Tooltip = MyStringId.GetOrCompute("Navigate the drone to target.");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Textbox_Debug_NavigationTargetGPS ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Textbox_Debug_NavigationTargetGPS = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_Debug_NavigationTargetGPSApproachFrom");
                c.Title = MyStringId.GetOrCompute("Approach target from:");
                c.Tooltip = MyStringId.GetOrCompute("Optional position to approach target from");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Textbox_Debug_NavigationTargetGPSApproachFrom ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Textbox_Debug_NavigationTargetGPSApproachFrom = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

                        // // Set Current Position as Home
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_Debug_NavigationTargetGPSActionTrigger");
                c.Title = MyStringId.GetOrCompute("Navigate");
                c.Tooltip = MyStringId.GetOrCompute("");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_NavigateToTarget();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_Debug_QueueWaypoint");
                c.Title = MyStringId.GetOrCompute("Queue waypoint");
                c.Tooltip = MyStringId.GetOrCompute("Adds the target above as the next leg; the current leg passes its waypoint without stopping.");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_QueueWaypoint();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Place mount (debug)
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>(IdPrefix + "Listbox_Debug_Mount");
                c.Title = MyStringId.GetOrCompute("Mount to place");
                c.Tooltip = MyStringId.GetOrCompute("Welder / grinder / drill: the target ends up on the edge of the tool's reach. Connector: aligned with the target along gravity.");
                c.Visible = CustomVisibleCondition;
                c.Multiselect = false;
                c.VisibleRowsCount = 4;
                c.ListContent = (b, items, selected) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_MountListContent(items, selected);
                };
                c.ItemSelected = (b, selected) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_SelectMount(selected);
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_Debug_PlaceMountTargetGPS");
                c.Title = MyStringId.GetOrCompute("Mount target");
                c.Tooltip = MyStringId.GetOrCompute("GPS of the point the mount should work on / connect at.");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Textbox_Debug_PlaceMountTargetGPS ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Textbox_Debug_PlaceMountTargetGPS = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_Debug_PlaceMount");
                c.Title = MyStringId.GetOrCompute("Place mount");
                c.Tooltip = MyStringId.GetOrCompute("");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_PlaceMount();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Relative navigation (debug)
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>(IdPrefix + "Listbox_Debug_Anchor");
                c.Title = MyStringId.GetOrCompute("Relative to");
                c.Tooltip = MyStringId.GetOrCompute("Home connector or one of your / your faction's beacons in range.");
                c.Visible = CustomVisibleCondition;
                c.Multiselect = false;
                c.VisibleRowsCount = 4;
                c.ListContent = (b, items, selected) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_AnchorListContent(items, selected);
                };
                c.ItemSelected = (b, selected) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_SelectAnchor(selected);
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_Debug_RelativeOffset");
                c.Title = MyStringId.GetOrCompute("Offset (right, up, forward)");
                c.Tooltip = MyStringId.GetOrCompute("Metres in the anchor block's frame, e.g. 0, 5, 20");
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Textbox_Debug_RelativeOffset ?? new StringBuilder("");
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Textbox_Debug_RelativeOffset = v;
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_Debug_NavigateRelative");
                c.Title = MyStringId.GetOrCompute("Navigate relative");
                c.Tooltip = MyStringId.GetOrCompute("Matches the anchor's speed first, then moves to the offset.");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Debug_NavigateRelative();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
        }
    }
}
