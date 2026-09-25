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

namespace Automata.DroneController
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
            // === ENTITY ID (Read-only) ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_EntityID");
                c.Title = MyStringId.GetOrCompute("Entity ID");
                c.Tooltip = MyStringId.GetOrCompute("Unique identifier for this drone controller");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => false;
                c.Getter = (b) =>
                {
                    var logic = GetBlock(b);
                    return logic != null ? new StringBuilder(logic.Terminal_EntityId.ToString()) : new StringBuilder("N/A");
                };
                c.Setter = (b, v) => { }; // Read-only
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // === GENERAL SETTINGS ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_GeneralSettings");
                c.Label = MyStringId.GetOrCompute("General Settings");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

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
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_Enabled = v;
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
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_OperationMode");
                c.Title = MyStringId.GetOrCompute("Operation Mode");
                c.Tooltip = MyStringId.GetOrCompute("Select drone operation mode");
                c.Visible = CustomVisibleCondition;
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem() { Key = (long)OperationMode.StandAlone, Value = MyStringId.GetOrCompute("Stand Alone") });
                    list.Add(new MyTerminalControlComboBoxItem() { Key = (long)OperationMode.ManagedByScheduler, Value = MyStringId.GetOrCompute("Managed by Scheduler") });
                    list.Add(new MyTerminalControlComboBoxItem() { Key = (long)OperationMode.ManagedByPlayer, Value = MyStringId.GetOrCompute("Managed by Player") });
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeValue ?? 0;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_OperationModeValue = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // === CONTROLLER SETTINGS ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Controller");
                c.Label = MyStringId.GetOrCompute("Controller Settings");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Controller Forward Direction
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_ControllerForward");
                c.Title = MyStringId.GetOrCompute("Controller Forward Direction");
                c.Tooltip = MyStringId.GetOrCompute("Which direction this controller considers forward");
                c.Visible = CustomVisibleCondition;
                c.ComboBoxContent = (list) =>
                {
                    foreach (Base6Directions.Direction dir in Enum.GetValues(typeof(Base6Directions.Direction)))
                    {
                        list.Add(new MyTerminalControlComboBoxItem() { Key = (long)dir, Value = MyStringId.GetOrCompute(dir.ToString()) });
                    }
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_ControllerForwardDirectionValue ?? (long)Base6Directions.Direction.Forward;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_ControllerForwardDirectionValue = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // === HOME POSITION ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_HomePosition");
                c.Label = MyStringId.GetOrCompute("Home Position");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Home Position GPS
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_HomePosition");
                c.Title = MyStringId.GetOrCompute("Home GPS");
                c.Tooltip = MyStringId.GetOrCompute("Enter GPS coordinates for home position");
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

            // Set Current Position as Home
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + "Button_SetCurrentAsHome");
                c.Title = MyStringId.GetOrCompute("Set Current as Home");
                c.Tooltip = MyStringId.GetOrCompute("Set current drone position as home");
                c.Visible = CustomVisibleCondition;
                c.Action = (b) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_SetCurrentAsHome();
                };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Home Is Relative To (Hidden - TODO: implement beacon search)
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_HomeIsRelativeTo");
                c.Title = MyStringId.GetOrCompute("Home Relative To");
                c.Tooltip = MyStringId.GetOrCompute("Beacon to use as reference for home position");
                c.Visible = (b) => false; // TODO: implement beacon search
                c.ComboBoxContent = (list) => { };
                c.Getter = (b) => 0;
                // c.Setter = (b, v) => { };
                c.SupportsMultipleBlocks = false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

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
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Filters");
                c.Label = MyStringId.GetOrCompute("Task and Capability Filters");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Use Task Type Filters
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_UseTaskTypeFilters");
                c.Title = MyStringId.GetOrCompute("Use Task Type Filters");
                c.Tooltip = MyStringId.GetOrCompute("Enable filtering of task types this drone can handle");
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_UseTaskTypeFilters = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Task Type Filters Label
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_TaskTypeFiltersNote");
                c.Label = MyStringId.GetOrCompute("Task Type Filters:");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Create checkboxes for each task type
            foreach (Orchestrator.TaskType taskType in Enum.GetValues(typeof(Orchestrator.TaskType)))
            {
                if (taskType == 0) continue; // Skip None

                var currentTaskType = taskType;
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_TaskFilter_" + taskType.ToString());
                c.Title = MyStringId.GetOrCompute(taskType.ToString());
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
                c.Enabled = (b) => GetBlock(b)?.Terminal_UseTaskTypeFilters ?? false;
                c.Getter = (b) =>
                {
                    var logic = GetBlock(b);
                    return logic != null && (logic.Terminal_TaskTypeFilters & currentTaskType) != 0;
                };
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                    {
                        if (v)
                            logic.Terminal_TaskTypeFilters |= currentTaskType;
                        else
                            logic.Terminal_TaskTypeFilters &= ~currentTaskType;
                    }
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Use Capability Filters
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_UseCapabilityFilters");
                c.Title = MyStringId.GetOrCompute("Use Capability Filters");
                c.Tooltip = MyStringId.GetOrCompute("Enable filtering of capabilities reported to scheduler");
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_UseCapabilityFilters ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_UseCapabilityFilters = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Capability Filters Label
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_CapabilityFiltersNote");
                c.Label = MyStringId.GetOrCompute("Capability Filters:");
                c.SupportsMultipleBlocks = true;
                c.Visible = (b) => GetBlock(b)?.Terminal_UseCapabilityFilters ?? false;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Create checkboxes for each capability
            foreach (Capabilities capability in Enum.GetValues(typeof(Capabilities)))
            {
                if (capability == 0) continue; // Skip None

                var currentCapability = capability;
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_CapFilter_" + capability.ToString());
                c.Title = MyStringId.GetOrCompute(capability.ToString());
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => 
                {
                    var block = GetBlock(b);
                    if (block != null)
                    {
                        return CustomVisibleCondition(b) && block.Terminal_UseCapabilityFilters;
                    }
                    return false;
                };
                c.Getter = (b) =>
                {
                    var logic = GetBlock(b);
                    return logic != null && (logic.Terminal_CapabilityFilters & currentCapability) != 0;
                };
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null)
                    {
                        if (v)
                            logic.Terminal_CapabilityFilters |= currentCapability;
                        else
                            logic.Terminal_CapabilityFilters &= ~currentCapability;
                    }
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // === NAVIGATION SETTINGS ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Navigation");
                c.Label = MyStringId.GetOrCompute("Navigation Settings");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Waypoint Tolerance
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_WaypointTolerance");
                c.Title = MyStringId.GetOrCompute("Waypoint Tolerance");
                c.Tooltip = MyStringId.GetOrCompute("Distance from waypoint to consider reached (meters)");
                c.Visible = CustomVisibleCondition;
                c.SetLimits(5f, 50f);
                c.Getter = (b) => GetBlock(b)?.Terminal_WaypointTolerance ?? 5f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_WaypointTolerance = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_WaypointTolerance.ToString("F1")).Append(" m");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Approach Speed
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_ApproachSpeed");
                c.Title = MyStringId.GetOrCompute("Approach Speed");
                c.Tooltip = MyStringId.GetOrCompute("Speed when approaching work targets (m/s)");
                c.Visible = CustomVisibleCondition;
                c.SetLimits(1f, 50f);
                c.Getter = (b) => GetBlock(b)?.Terminal_ApproachSpeed ?? 5f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_ApproachSpeed = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_ApproachSpeed.ToString("F1")).Append(" m/s");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Speed Limit
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_SpeedLimit");
                c.Title = MyStringId.GetOrCompute("Speed Limit");
                c.Tooltip = MyStringId.GetOrCompute("Maximum speed (m/s)");
                c.Visible = CustomVisibleCondition;
                c.SetLimits(5f, 100f);
                c.Getter = (b) => GetBlock(b)?.Terminal_SpeedLimit ?? 50f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_SpeedLimit = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_SpeedLimit.ToString("F1")).Append(" m/s");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Docking Speed
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_DockingSpeed");
                c.Title = MyStringId.GetOrCompute("Docking Speed");
                c.Tooltip = MyStringId.GetOrCompute("Speed when docking (m/s)");
                c.Visible = CustomVisibleCondition;
                c.SetLimits(0.5f, 10f);
                c.Getter = (b) => GetBlock(b)?.Terminal_DockingSpeed ?? 2.5f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_DockingSpeed = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_DockingSpeed.ToString("F1")).Append(" m/s");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
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

            // Max Pitch Degrees
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
                c.SetLimits(0f, 45f);
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
                c.SetLimits(0f, 45f);
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
            // LCD Screen Tag
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + "Textbox_LCDScreenTag");
                c.Title = MyStringId.GetOrCompute("LCD Screen Tag");
                c.Tooltip = MyStringId.GetOrCompute("Tag for LCD screens to display drone status");
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

            // === POWER MONITORING ===
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>("");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + "Label_Power");
                c.Label = MyStringId.GetOrCompute("Power & Fuel Monitoring");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Monitor Hydrogen Levels
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_MonitorHydrogen");
                c.Title = MyStringId.GetOrCompute("Monitor Hydrogen Levels");
                c.Tooltip = MyStringId.GetOrCompute("Monitor hydrogen levels and auto-refuel when low");
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_MonitorHydrogen ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_MonitorHydrogen = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Hydrogen Refuel Threshold
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_H2RefuelThreshold");
                c.Title = MyStringId.GetOrCompute("H2 Refuel Threshold");
                c.Tooltip = MyStringId.GetOrCompute("Return to base when hydrogen below this percentage");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorHydrogen ?? false;
                c.SetLimits(5f, 50f);
                c.Getter = (b) => GetBlock(b)?.Terminal_H2RefuelThreshold ?? 25f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_H2RefuelThreshold = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_H2RefuelThreshold.ToString("F0")).Append("%");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Hydrogen Operational Threshold
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_H2OperationalThreshold");
                c.Title = MyStringId.GetOrCompute("H2 Operational Threshold");
                c.Tooltip = MyStringId.GetOrCompute("Resume operations when hydrogen above this percentage");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorHydrogen ?? false;
                c.SetLimits(10f, 95f);
                c.Getter = (b) => GetBlock(b)?.Terminal_H2OperationalThreshold ?? 50f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_H2OperationalThreshold = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_H2OperationalThreshold.ToString("F0")).Append("%");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Always Refuel When Docked
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_AlwaysRefuel");
                c.Title = MyStringId.GetOrCompute("Always Refuel When Docked");
                c.Tooltip = MyStringId.GetOrCompute("Refuel every time drone is docked");
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_AlwaysRefuel ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_AlwaysRefuel = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Monitor Battery Levels
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_MonitorBattery");
                c.Title = MyStringId.GetOrCompute("Monitor Battery Levels");
                c.Tooltip = MyStringId.GetOrCompute("Monitor battery levels and auto-recharge when low");
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_MonitorBattery ?? false;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_MonitorBattery = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Battery Refuel Threshold
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_BatteryRefuelThreshold");
                c.Title = MyStringId.GetOrCompute("Battery Recharge Threshold");
                c.Tooltip = MyStringId.GetOrCompute("Return to base when battery below this percentage");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorBattery ?? false;
                c.SetLimits(5f, 50f);
                c.Getter = (b) => GetBlock(b)?.Terminal_BatteryRefuelThreshold ?? 20f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_BatteryRefuelThreshold = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_BatteryRefuelThreshold.ToString("F0")).Append("%");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // Battery Operational Threshold
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_BatteryOperationalThreshold");
                c.Title = MyStringId.GetOrCompute("Battery Operational Threshold");
                c.Tooltip = MyStringId.GetOrCompute("Resume operations when battery above this percentage");
                c.Visible = CustomVisibleCondition;
                c.Enabled = (b) => GetBlock(b)?.Terminal_MonitorBattery ?? false;
                c.SetLimits(10f, 95f);
                c.Getter = (b) => GetBlock(b)?.Terminal_BatteryOperationalThreshold ?? 80f;
                c.Setter = (b, v) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) logic.Terminal_BatteryOperationalThreshold = v;
                };
                c.Writer = (b, sb) =>
                {
                    var logic = GetBlock(b);
                    if (logic != null) sb.Append(logic.Terminal_BatteryOperationalThreshold.ToString("F0")).Append("%");
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }


        }
    }
}
