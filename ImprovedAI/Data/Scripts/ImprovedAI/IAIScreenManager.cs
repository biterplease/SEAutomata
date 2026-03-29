using Sandbox.ModAPI;
using VRage.Collections;

namespace ImprovedAI
{
    public class ScreenManager
    {
        public enum OperationMode : byte
        {
            Drone = 1,
            Scheduler = 2,
            LogisticsComputer = 4,
        }
        private int displayIndex = 0;
        private OperationMode operationMode;
        private MyConcurrentList<IMyTextSurface> screens;
        private const string droneFormat = @"Drone [{0}] status:
State : {1}
Status: {2}

";

        //private readonly BAIController controller;
        //private readonly BAIConstructorDroneConfig config;
        //private IMyTextSurface textPanel;
        //private IMyRadioAntenna antenna;
        //private IMyBroadcastController broadcastController;
        //private DateTime lastUpdate = DateTime.MinValue;
        //private readonly TimeSpan updateInterval = TimeSpan.FromSeconds(1.0);

        //public DisplayManager(BAIController controller, BAIConstructorDroneConfig config)
        //{
        //    this.controller = controller;
        //    this.config = config;
        //    FindDisplayComponents();
        //}

        //public void FindDisplayComponents()
        //{
        //    List<IMySlimBlock> blocks = new List<IMySlimBlock> ();
        //    controller.CubeGrid.GetBlocks(blocks);
        //    textPanel = null;
        //    antenna = null;
        //    broadcastController = null;

        //    foreach (IMySlimBlock block in blocks)
        //    {
        //        var functionalBlock = block.FatBlock;

        //        // Find text panel matching the configured name
        //        var panel = functionalBlock as IMyTextPanel;
        //        if (panel != null)
        //        {
        //            if (panel.DisplayNameText.Contains(config.TextPanelMatchName))
        //            {
        //                textPanel = panel as IMyTextSurface;
        //            }
        //        }
        //        // Find first working antenna
        //        var radioAntenna = functionalBlock as IMyRadioAntenna;
        //        if (radioAntenna != null && antenna == null)
        //        {
        //            antenna = radioAntenna;
        //        }

        //        // Find first working broadcast controller
        //        var broadcast = functionalBlock as IMyBroadcastController;
        //        if (broadcast != null && broadcastController == null)
        //        {
        //            broadcastController = broadcast;
        //        }
        //    }
        //}

        //public void UpdateDisplay()
        //{
        //    // Throttle updates to prevent performance issues
        //    var now = DateTime.Now;
        //    if (now - lastUpdate < updateInterval)
        //        return;
        //    lastUpdate = now;

        //    if (textPanel != null)
        //    {
        //        UpdateTextPanel();
        //    }
        //}

        //// Add this to the UpdateTextPanel method after the capabilities section:

        //private void UpdateTextPanel()
        //{
        //    var display = new StringBuilder();
        //    display.AppendLine("=== CONSTRUCTION DRONE ===");
        //    display.AppendLine($"State: {controller.GetCurrentState()}");
        //    display.AppendLine($"Status: {controller.GetStatusMessage()}");
        //    display.AppendLine();

        //    if (controller.AreCapabilitiesValid())
        //    {
        //        display.AppendLine("=== CAPABILITIES ===");
        //        var thrustData = controller.GetThrustData();
        //        display.AppendLine($"Thrust:");
        //        display.AppendLine($"  Up   : {thrustData.Up:F0} N");
        //        display.AppendLine($"  Down : {thrustData.Down:F0} N");
        //        display.AppendLine($"  Left : {thrustData.Left:F0} N");
        //        display.AppendLine($"  Right: {thrustData.Right:F0} N");
        //        display.AppendLine($"  Fwd  : {thrustData.Forward:F0} N");
        //        display.AppendLine($"  Back : {thrustData.Backward:F0} N");
        //        display.AppendLine("Load:");
        //        display.AppendLine($"  Max    : {controller.GetMaxLoad():F0} kg");
        //        display.AppendLine($"  Current: {controller.GetCurrentLoad():F0} kg");
        //        display.AppendLine($"Thrusters: {controller.GetThrusters().Count}");
        //        display.AppendLine($"Sensors: {controller.GetSensors().Count}");
        //        display.AppendLine();

        //        // Power monitoring section
        //        var config = controller.GetConfig();
        //        if (config.MonitorHydrogenLevels || config.MonitorBatteryLevels)
        //        {
        //            display.AppendLine("=== POWER STATUS ===");

        //            if (config.MonitorHydrogenLevels)
        //            {
        //                var h2Percentage = controller.GetHydrogenPercentage();
        //                var h2Status = GetPowerStatus(h2Percentage, config.HydrogenRefuelThreshold, config.HydrogenOperationalThreshold);
        //                display.AppendLine($"Hydrogen: {h2Percentage:F1}% {h2Status}");
        //                display.AppendLine($"  Tanks: {controller.GetHydrogenTanks().Count}");
        //                display.AppendLine($"  Refuel at: {config.HydrogenRefuelThreshold:F1}%");
        //                display.AppendLine($"  Resume at: {config.HydrogenOperationalThreshold:F1}%");
        //                if (controller.NeedsHydrogenRefuel())
        //                    display.AppendLine("  STATUS: NEEDS REFUEL!");
        //            }

        //            if (config.MonitorBatteryLevels)
        //            {
        //                var batteryPercentage = controller.GetBatteryPercentage();
        //                var batteryStatus = GetPowerStatus(batteryPercentage, config.BatteryRefuelThreshold, config.BatteryOperationalThreshold);
        //                display.AppendLine($"Battery: {batteryPercentage:F1}% {batteryStatus}");
        //                display.AppendLine($"  Batteries: {controller.GetBatteries().Count}");
        //                display.AppendLine($"  Refuel at: {config.BatteryRefuelThreshold:F1}%");
        //                display.AppendLine($"  Resume at: {config.BatteryOperationalThreshold:F1}%");
        //                if (controller.NeedsBatteryRecharge())
        //                    display.AppendLine("  STATUS: NEEDS RECHARGE!");
        //            }
        //            display.AppendLine();
        //        }

        //        var targetBlocks = controller.GetTargetBlocks();
        //        var currentTargetIndex = controller.GetCurrentTargetIndex();

        //        if (targetBlocks.Count > 0)
        //        {
        //            display.AppendLine("=== TARGETS ===");
        //            display.AppendLine($"Total Targets: {targetBlocks.Count}");
        //            display.AppendLine($"Current Target: {currentTargetIndex + 1}");

        //            if (currentTargetIndex < targetBlocks.Count)
        //            {
        //                var block = targetBlocks[currentTargetIndex];
        //                display.AppendLine($"Block Type: {block.BlockDefinition.Id.SubtypeName}");
        //                display.AppendLine($"Build Progress: {block.BuildLevelRatio * 100:F1}%");

        //                if (block.CurrentDamage > 0)
        //                {
        //                    display.AppendLine($"Damage: {block.CurrentDamage:F1}");
        //                }
        //            }
        //        }

        //        // Navigation info
        //        var navManager = controller.GetNavigationManager();
        //        if (navManager != null && navManager.GetTotalWaypoints() > 0)
        //        {
        //            display.AppendLine("=== NAVIGATION ===");
        //            display.AppendLine($"Waypoint: {navManager.GetCurrentWaypointIndex() + 1}/{navManager.GetTotalWaypoints()}");
        //            display.AppendLine($"Path Complete: {(navManager.IsPathComplete() ? "Yes" : "No")}");
        //        }

        //        // Configuration summary
        //        display.AppendLine("=== CONFIG ===");
        //        display.AppendLine($"Waypoint Tolerance: {config.WaypointTolerance:F1}m");
        //        display.AppendLine($"Max Approach Speed: {config.MaxApproachSpeed:F1} m/s");
        //        display.AppendLine($"Max Travel Speed: {config.MaxTravelSpeed:F1} m/s");
        //        display.AppendLine($"Gravity Align: {(config.AlignToPGravity ? "On" : "Off")}");
        //        if (config.AlignToPGravity)
        //        {
        //            display.AppendLine($"Max Pitch/Roll: {config.PGravityAlignMaxPitch:F1}°/{config.PGravityAlignMaxRoll:F1}°");
        //        }

        //        // Power monitoring config
        //        if (config.MonitorHydrogenLevels || config.MonitorBatteryLevels)
        //        {
        //            display.AppendLine("Power Monitoring:");
        //            if (config.MonitorHydrogenLevels)
        //                display.AppendLine($"  H2: {config.HydrogenRefuelThreshold:F0}%/{config.HydrogenOperationalThreshold:F0}%");
        //            if (config.MonitorBatteryLevels)
        //                display.AppendLine($"  Battery: {config.BatteryRefuelThreshold:F0}%/{config.BatteryOperationalThreshold:F0}%");
        //        }
        //    }
        //    else
        //    {
        //        display.AppendLine("=== ERROR ===");
        //        display.AppendLine("Missing required components!");
        //        display.AppendLine("Check configuration and block setup.");
        //    }

        //    textPanel.WriteText(display.ToString());
        //}

        //private string GetPowerStatus(float currentPercentage, float refuelThreshold, float operationalThreshold)
        //{
        //    if (currentPercentage <= refuelThreshold)
        //        return "[LOW]";
        //    else if (currentPercentage < operationalThreshold)
        //        return "[REFUELING]";
        //    else if (currentPercentage >= 90.0f)
        //        return "[FULL]";
        //    else
        //        return "[OK]";
        //}

        //public void BroadcastMessage(string message, string color = MyFontEnum.White)
        //{
        //    // Always log to game
        //    MyAPIGateway.Utilities.ShowMessage($"[{controller.CubeGrid.DisplayName}]", message);

        //    // Broadcast via antenna if configured and available
        //    if (config.BroadcastUpdates && antenna?.IsWorking == true)
        //    {
        //        var broadcastMessage = $"[{DateTime.Now:HH:mm:ss}] {controller.CubeGrid.DisplayName}: {message}";
        //        // Note: Actual broadcasting would require implementing a custom message system
        //        // This is a placeholder for the broadcast functionality
        //        MyAPIGateway.Utilities.ShowMessage("BROADCAST", broadcastMessage);
        //    }

        //    // Also update the controller's status message for display
        //    controller.SetStatusMessage(message);
        //}

        //public void BroadcastStatusUpdate()
        //{
        //    if (!config.BroadcastUpdates || broadcastController?.IsWorking != true)
        //        return;

        //    var statusData = new StringBuilder();
        //    statusData.AppendLine($"State: {controller.GetCurrentState()}");
        //    statusData.AppendLine($"Status: {controller.GetStatusMessage()}");

        //    var targetBlocks = controller.GetTargetBlocks();
        //    var currentTargetIndex = controller.GetCurrentTargetIndex();
        //    statusData.AppendLine($"Targets: {currentTargetIndex + 1}/{targetBlocks.Count}");
        //    statusData.AppendLine($"Load: {controller.GetCurrentLoad():F0}/{controller.GetMaxLoad():F0} kg");

        //    // Add power status to broadcast
        //    if (config.MonitorHydrogenLevels)
        //    {
        //        var h2Percentage = controller.GetHydrogenPercentage();
        //        var h2Status = controller.NeedsHydrogenRefuel() ? " [LOW]" : "";
        //        statusData.AppendLine($"H2: {h2Percentage:F1}%{h2Status}");
        //    }

        //    if (config.MonitorBatteryLevels)
        //    {
        //        var batteryPercentage = controller.GetBatteryPercentage();
        //        var batteryStatus = controller.NeedsBatteryRecharge() ? " [LOW]" : "";
        //        statusData.AppendLine($"Battery: {batteryPercentage:F1}%{batteryStatus}");
        //    }

        //    BroadcastMessage(statusData.ToString(), MyFontEnum.Blue);
        //}
        //public void ShowPowerWarning(string powerType, float currentLevel, float threshold)
        //{
        //    var message = $"{powerType} level critical: {currentLevel:F1}% (threshold: {threshold:F1}%)";
        //    BroadcastMessage($"POWER WARNING: {message}", MyFontEnum.Red);

        //    // Also update text panel immediately with warning
        //    if (textPanel != null)
        //    {
        //        var warningDisplay = new StringBuilder();
        //        warningDisplay.AppendLine("=== POWER WARNING ===");
        //        warningDisplay.AppendLine($"Time: {DateTime.Now:HH:mm:ss}");
        //        warningDisplay.AppendLine($"Warning: {message}");
        //        warningDisplay.AppendLine();
        //        warningDisplay.AppendLine("Returning to base for refuel/recharge...");
        //        warningDisplay.AppendLine();
        //        warningDisplay.AppendLine("Current Power Status:");

        //        if (config.MonitorHydrogenLevels)
        //        {
        //            var h2Percentage = controller.GetHydrogenPercentage();
        //            warningDisplay.AppendLine($"Hydrogen: {h2Percentage:F1}%");
        //        }

        //        if (config.MonitorBatteryLevels)
        //        {
        //            var batteryPercentage = controller.GetBatteryPercentage();
        //            warningDisplay.AppendLine($"Battery: {batteryPercentage:F1}%");
        //        }

        //        textPanel.WriteText(warningDisplay.ToString());
        //    }
        //}

        //public void ShowError(string errorMessage)
        //{
        //    BroadcastMessage($"ERROR: {errorMessage}", MyFontEnum.Red);

        //    // Also display on text panel immediately
        //    if (textPanel != null)
        //    {
        //        var errorDisplay = new StringBuilder();
        //        errorDisplay.AppendLine("=== CONSTRUCTION DRONE - ERROR ===");
        //        errorDisplay.AppendLine($"Time: {DateTime.Now:HH:mm:ss}");
        //        errorDisplay.AppendLine($"Error: {errorMessage}");
        //        errorDisplay.AppendLine();
        //        errorDisplay.AppendLine("Manual intervention may be required.");
        //        errorDisplay.AppendLine("Check block configuration and try again.");

        //        textPanel.WriteText(errorDisplay.ToString());
        //    }
        //}

        //public void ShowWarning(string warningMessage)
        //{
        //    BroadcastMessage($"WARNING: {warningMessage}", MyFontEnum.White);
        //}

        //public void ShowInfo(string infoMessage)
        //{
        //    BroadcastMessage($"INFO: {infoMessage}", MyFontEnum.Blue);
        //}

        //public void ShowSuccess(string successMessage)
        //{
        //    BroadcastMessage(successMessage, MyFontEnum.Green);
        //}

        //public void ClearDisplay()
        //{
        //    if (textPanel != null)
        //    {
        //        textPanel.WriteText("", false);
        //    }
        //}

        //public bool HasTextPanel()
        //{
        //    return textPanel != null;
        //}

        //public bool HasAntenna()
        //{
        //    return antenna != null && antenna.IsWorking;
        //}

        //public bool HasBroadcastController()
        //{
        //    return broadcastController != null && broadcastController.IsWorking;
        //}

        //// Method to refresh component references if blocks are added/removed
        //public void RefreshComponents()
        //{
        //    FindDisplayComponents();
        //}
    }
}