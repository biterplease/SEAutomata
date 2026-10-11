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
using Automata.Util;

namespace Automata.Drone
{
    public class DroneControllerTerminalControls
    {
        #region UI strings
        private static readonly MyStringId StrSectionTip = MyStringId.GetOrCompute("Shows this section's settings (in your terminal only).");
        private static readonly MyStringId StrCrops = MyStringId.GetOrCompute("Crops");
        private static readonly MyStringId StrOres = MyStringId.GetOrCompute("Ores");
        private static readonly MyStringId StrKnownCropsTip = MyStringId.GetOrCompute("Ignore specific crops.");
        private static readonly MyStringId StrKnownOresTip = MyStringId.GetOrCompute("Ignore specific ores.");
        private static readonly MyStringId StrIgnoreSelected = MyStringId.GetOrCompute("Ignore selected");
        private static readonly MyStringId StrIgnoreCropsTip = MyStringId.GetOrCompute("Moves the selected crops to the IGNORED list below.");
        private static readonly MyStringId StrIgnoreOresTip = MyStringId.GetOrCompute("Moves the selected ores to the IGNORED list below.");
        private static readonly MyStringId StrIgnoredCrops = MyStringId.GetOrCompute("IGNORED crops");
        private static readonly MyStringId StrIgnoredOres = MyStringId.GetOrCompute("IGNORED ores");
        private static readonly MyStringId StrIgnoredCropsTip = MyStringId.GetOrCompute("Crops in this list are IGNORED: the drone won't farm them (farming is not implemented yet).");
        private static readonly MyStringId StrIgnoredOresTip = MyStringId.GetOrCompute("Ores in this list are IGNORED: the drone bids on no mining job for them.");
        private static readonly MyStringId StrRemoveSelected = MyStringId.GetOrCompute("Remove selected");
        private static readonly MyStringId StrUnignoreCropsTip = MyStringId.GetOrCompute("Takes the selected crops off the IGNORED list.");
        private static readonly MyStringId StrUnignoreOresTip = MyStringId.GetOrCompute("Takes the selected ores off the IGNORED list.");
        private static readonly MyStringId StrAI = MyStringId.GetOrCompute("AI");
        private static readonly MyStringId StrDrawNavigationTargets = MyStringId.GetOrCompute("Draw navigation targets");
        private static readonly MyStringId StrDrawObservationArea = MyStringId.GetOrCompute("Draw observation area");
        private static readonly MyStringId StrGoHome = MyStringId.GetOrCompute("Go home");
        private static readonly MyStringId StrOn = MyStringId.GetOrCompute("On");
        private static readonly MyStringId StrOff = MyStringId.GetOrCompute("Off");
        private static readonly MyStringId StrActionToggle = MyStringId.GetOrCompute("{0} On/Off");
        private static readonly MyStringId StrActionOn = MyStringId.GetOrCompute("{0} On");
        private static readonly MyStringId StrActionOff = MyStringId.GetOrCompute("{0} Off");
        private static readonly MyStringId StrMetresPerSecond = MyStringId.GetOrCompute("{0} m/s");
        private static readonly MyStringId StrMetres = MyStringId.GetOrCompute("{0} m");
        private static readonly MyStringId StrSeconds = MyStringId.GetOrCompute("{0} s");
        private static readonly MyStringId StrDegrees = MyStringId.GetOrCompute("{0}°");
        private static readonly MyStringId StrBlockMetres = MyStringId.GetOrCompute("{0} block ({1} m)");
        private static readonly MyStringId StrBlocksMetres = MyStringId.GetOrCompute("{0} blocks ({1} m)");
        private static readonly MyStringId StrTaskTimeoutTip = MyStringId.GetOrCompute("Seconds before a job task gives up and the drone moves on: a tool task that isn't done, or a flight that stopped getting closer. The server sets the maximum.\n0 = no waiting: a task fails as soon as it stalls.");
        private static readonly MyStringId StrBroadcastSends = MyStringId.GetOrCompute("combat drones: \"Engaging target at <GPS>\" when dispatched, \"Escorting <name>\", \"Target destroyed\".");
        private static readonly MyStringId StrMaxSpeed = MyStringId.GetOrCompute("Max speed");
        private static readonly MyStringId StrMaxSpeedTip = MyStringId.GetOrCompute("Cruise speed limit for any flight, in m/s (0-1000; the game's own speed limit still applies).\nNot tied to the other speeds: odd combinations show as an error in the LCD header.");
        private static readonly MyStringId StrApproachSpeed = MyStringId.GetOrCompute("Approach speed");
        private static readonly MyStringId StrApproachSpeedTip = MyStringId.GetOrCompute("Speed over the last 10 m before a target: tool work, waypoints that stop, the start of the final approach (0-100 m/s).\nShould not be above Max speed.");
        private static readonly MyStringId StrSafeSpeed = MyStringId.GetOrCompute("Safe speed");
        private static readonly MyStringId StrSafeSpeedTip = MyStringId.GetOrCompute("Speed for delicate moves: the final docking approach to a connector (0-50 m/s).\nShould not be above Approach speed.");
        private static readonly MyStringId StrWaypointTolerance = MyStringId.GetOrCompute("Waypoint tolerance");
        private static readonly MyStringId StrWaypointToleranceTip = MyStringId.GetOrCompute("How close the drone must get to a waypoint for it to count as reached, in metres (0 = as close as it gets, up to 500: combat drones can stop well short). Player / combat routes use it as is; job legs at most 50 m; docking and tool work use their own, tighter tolerances.");
        private static readonly MyStringId StrMinAltitude = MyStringId.GetOrCompute("Min altitude");
        private static readonly MyStringId StrMinAltitudeTip = MyStringId.GetOrCompute("Planets only: routes stay at least this high above the surface between take-off and landing (0 = off, default 50 m): up after take-off, down to land. Every flight, jobs included, except hops shorter than this altitude (work at a site, moves between nearby blocks), which stay low.");
        private static readonly MyStringId StrMaxPitchDegrees = MyStringId.GetOrCompute("Max pitch deviation");
        private static readonly MyStringId StrMaxPitchDegreesTip = MyStringId.GetOrCompute("Maximum pitch away from level while aligned to gravity (degrees).");
        private static readonly MyStringId StrMaxRollDegrees = MyStringId.GetOrCompute("Max roll deviation");
        private static readonly MyStringId StrMaxRollDegreesTip = MyStringId.GetOrCompute("Maximum roll away from level while aligned to gravity (degrees).");
        private static readonly MyStringId StrMaxLoadGravity = MyStringId.GetOrCompute("Max load (gravity)");
        private static readonly MyStringId StrMaxLoadGravityTip = MyStringId.GetOrCompute("Percent of max load. The drone stops collecting when this is exceeded; construction batches are sized to it.\n100% = the heaviest total mass the up thrusters can hover in the current gravity (1 g when in space). The value in brackets is that mass limit.");
        private static readonly MyStringId StrMaxLoadSpace = MyStringId.GetOrCompute("Max load (space)");
        private static readonly MyStringId StrMaxLoadSpaceTip = MyStringId.GetOrCompute("Percent of max load. The drone stops collecting when this is exceeded; construction batches are sized to it.\n100% = the heaviest total mass the weakest thruster group can still accelerate at 0.1 g. The value in brackets is that mass limit.");
        private static readonly MyStringId StrBatteryRefuelThreshold = MyStringId.GetOrCompute("Battery: recharge below");
        private static readonly MyStringId StrBatteryRefuelThresholdTip = MyStringId.GetOrCompute("Threshold: when the batteries' total charge drops below this, the drone stops what it is doing and flies home to recharge.");
        private static readonly MyStringId StrBatteryOperationalThreshold = MyStringId.GetOrCompute("Battery: ready above");
        private static readonly MyStringId StrBatteryOperationalThresholdTip = MyStringId.GetOrCompute("Once home, the drone counts as recharged (and goes back to work) when the charge is back above this.");
        private static readonly MyStringId StrH2RefuelThreshold = MyStringId.GetOrCompute("H2: refuel below");
        private static readonly MyStringId StrH2RefuelThresholdTip = MyStringId.GetOrCompute("Threshold: when the hydrogen tanks' total fill drops below this, the drone stops what it is doing and flies home to refuel.");
        private static readonly MyStringId StrH2OperationalThreshold = MyStringId.GetOrCompute("H2: ready above");
        private static readonly MyStringId StrH2OperationalThresholdTip = MyStringId.GetOrCompute("Once home, the drone counts as refueled (and goes back to work) when the tanks are back above this.");
        private static readonly MyStringId StrLcdFontSize = MyStringId.GetOrCompute("Font size");
        private static readonly MyStringId StrLcdFontSizeTip = MyStringId.GetOrCompute("LCD font size. Only as many log lines as fit are shown.");
        private static readonly MyStringId StrCombatHandoverDistance = MyStringId.GetOrCompute("Handover distance");
        private static readonly MyStringId StrCombatHandoverDistanceTip = MyStringId.GetOrCompute("Within this distance of the enemy the AI Flight and AI Offensive blocks take over (both are switched on together). At most 2000 m: farther out the Offensive block detects nothing and the drone comes straight back.");
        private static readonly MyStringId StrCombatNoTargetSeconds = MyStringId.GetOrCompute("No target timeout");
        private static readonly MyStringId StrCombatNoTargetSecondsTip = MyStringId.GetOrCompute("Seconds without a target for the AI Offensive block before the drone takes control back and flies home.");
        private static readonly MyStringId StrAutoLaunchRadius = MyStringId.GetOrCompute("Auto-launch radius");
        private static readonly MyStringId StrAutoLaunchRadiusTip = MyStringId.GetOrCompute("Managed by orchestrator: enemies the drone's AI Defensive block detects within this distance of the drone launch it on its own. 0 = never (designator jobs only). At most 2000 m, the vanilla detection range.");
        private static readonly MyStringId StrEscortFollowDistance = MyStringId.GetOrCompute("Follow distance");
        private static readonly MyStringId StrEscortFollowDistanceTip = MyStringId.GetOrCompute("Escort jobs (hostile designator, blue): how far above the escorted grid or character the drone keeps station.");
        private static readonly MyStringId StrHomeNotStation = MyStringId.GetOrCompute("Home connector is not station");
        private static readonly MyStringId StrHomeNotStationTip = MyStringId.GetOrCompute("Tick when the home connector is on a ship or anything else that moves. The drone then finds home through the beacon selected below.");
        private static readonly MyStringId StrAlignToPGravity = MyStringId.GetOrCompute("Align to P-Gravity");
        private static readonly MyStringId StrAlignToPGravityTip = MyStringId.GetOrCompute("In planetary gravity, keep the drone level: pitch and roll stay within the limits below.");
        private static readonly MyStringId StrMonitorBattery = MyStringId.GetOrCompute("Monitor battery levels");
        private static readonly MyStringId StrMonitorBatteryTip = MyStringId.GetOrCompute("Drone will return home to recharge when battery is below threshold.");
        private static readonly MyStringId StrMonitorHydrogen = MyStringId.GetOrCompute("Monitor H2 levels");
        private static readonly MyStringId StrMonitorHydrogenTip = MyStringId.GetOrCompute("Drone will return home to refuel when H2 is below threshold.");
        private static readonly MyStringId StrAlwaysRefuel = MyStringId.GetOrCompute("Refuel when docked");
        private static readonly MyStringId StrAlwaysRefuelTip = MyStringId.GetOrCompute("When docked, recharge batteries and/or refill H2.");
        private static readonly MyStringId StrLcdShowHeader = MyStringId.GetOrCompute("Show header");
        private static readonly MyStringId StrLcdShowHeaderTip = MyStringId.GetOrCompute("Header lines: state, battery (POW) and hydrogen (H2) levels; then the current error (red) or warning (yellow).");
        private static readonly MyStringId StrSurveyForOres = MyStringId.GetOrCompute("Survey for ores");
        private static readonly MyStringId StrSurveyForOresTip = MyStringId.GetOrCompute("Needs a working ore detector on the drone. Ores it sees while flying are recorded and handed to a mining surveyor when one is in antenna range.");
        private static readonly MyStringId StrCombatRetreatOnFlee = MyStringId.GetOrCompute("Retreat when fleeing");
        private static readonly MyStringId StrCombatRetreatOnFleeTip = MyStringId.GetOrCompute("When the AI Defensive block decides to flee, the drone takes control back and flies home.");
        private static readonly MyStringId StrCombatRetreatOnLowPower = MyStringId.GetOrCompute("Retreat on low power");
        private static readonly MyStringId StrCombatRetreatOnLowPowerTip = MyStringId.GetOrCompute("Below the recharge / refuel thresholds the drone takes control back and flies home.");
        private static readonly MyStringId StrLcdForeground = MyStringId.GetOrCompute("Text color");
        private static readonly MyStringId StrLcdForegroundTip = MyStringId.GetOrCompute("LCD text color. Errors in the header are red, warnings yellow.");
        private static readonly MyStringId StrLcdBackground = MyStringId.GetOrCompute("Background color");
        private static readonly MyStringId StrLcdBackgroundTip = MyStringId.GetOrCompute("LCD background color.");
        private static readonly MyStringId StrWeldIgnoreColor = MyStringId.GetOrCompute("Weld ignore colour");
        private static readonly MyStringId StrWeldIgnoreColorTip = MyStringId.GetOrCompute("Blocks painted exactly this colour are never welded, repaired or built (projected blocks too). Default RGB(255,0,255).");
        private static readonly MyStringId StrCopySettings = MyStringId.GetOrCompute("Copy settings");
        private static readonly MyStringId StrCopySettingsTip = MyStringId.GetOrCompute("Copy settings (everything except AI on/off, home connector/beacon)");
        private static readonly MyStringId StrPasteSettings = MyStringId.GetOrCompute("Paste settings");
        private static readonly MyStringId StrPasteSettingsTip = MyStringId.GetOrCompute("Pase settings (everything except AI on/off, home connector/beacon)");
        private static readonly MyStringId StrScanConnectors = MyStringId.GetOrCompute("Scan");
        private static readonly MyStringId StrScanConnectorsTip = MyStringId.GetOrCompute("Refresh the connector and beacon lists now (limited by server settings).");
        private static readonly MyStringId StrAddConnectorByName = MyStringId.GetOrCompute("Add to list");
        private static readonly MyStringId StrAddConnectorByNameTip = MyStringId.GetOrCompute("Looks up the connector named above and adds every match that is yours or your faction's to the list.");
        private static readonly MyStringId StrSetCurrentAsHome = MyStringId.GetOrCompute("Set current as home");
        private static readonly MyStringId StrSetCurrentAsHomeTip = MyStringId.GetOrCompute("Dock the drone by hand, then press: that connector becomes home, and this exact position and orientation is used for docking.");
        private static readonly MyStringId StrGoHomeTip = MyStringId.GetOrCompute("Fly to the home connector, dock at Safe speed and power down (antenna, controller and LCDs stay on).");
        private static readonly MyStringId StrStopOrders = MyStringId.GetOrCompute("Stop");
        private static readonly MyStringId StrStopOrdersTip = MyStringId.GetOrCompute("Cancel the current flight order and construction work; the drone holds position.");
        private static readonly MyStringId StrExcludeSelected = MyStringId.GetOrCompute("Exclude selected");
        private static readonly MyStringId StrExcludeSelectedTip = MyStringId.GetOrCompute("Moves the capabilities selected in the first list to the excluded list.");
        private static readonly MyStringId StrRemoveSelectedExclusionsTip = MyStringId.GetOrCompute("Removes the capabilities selected in the excluded list.");
        private static readonly MyStringId StrClearExclusions = MyStringId.GetOrCompute("Clear all");
        private static readonly MyStringId StrClearExclusionsTip = MyStringId.GetOrCompute("Empties the excluded list.");
        private static readonly MyStringId StrSetOrientationTargetAction = MyStringId.GetOrCompute("Orient");
        private static readonly MyStringId StrSetOrientationTargetActionTip = MyStringId.GetOrCompute("Turn to face the target above.");
        private static readonly MyStringId StrNavigationTargetGPSActionTrigger = MyStringId.GetOrCompute("Navigate");
        private static readonly MyStringId StrNavigationTargetGPSActionTriggerTip = MyStringId.GetOrCompute("Fly to the target above.");
        private static readonly MyStringId StrQueueWaypoint = MyStringId.GetOrCompute("Queue waypoint");
        private static readonly MyStringId StrQueueWaypointTip = MyStringId.GetOrCompute("Adds the target above as the next leg; the current leg passes its waypoint without stopping.");
        private static readonly MyStringId StrPlaceMount = MyStringId.GetOrCompute("Place mount");
        private static readonly MyStringId StrPlaceMountTip = MyStringId.GetOrCompute("Moves the drone so the selected mount reaches the target.");
        private static readonly MyStringId StrDebugLog = MyStringId.GetOrCompute("DEBUGLOG");
        private static readonly MyStringId StrDebugLogTip = MyStringId.GetOrCompute("Writes the drone's state (position, speed, job and task, flight order, mining bore, cargo, power) to the mod log, as DEBUGLOG lines. Any mode, any moment.");
        private static readonly MyStringId StrNavigateRelative = MyStringId.GetOrCompute("Navigate relative");
        private static readonly MyStringId StrNavigateRelativeTip = MyStringId.GetOrCompute("Matches the anchor's speed first, then moves to the offset.");
        private static readonly MyStringId StrConnectorNameQuery = MyStringId.GetOrCompute("Connector name");
        private static readonly MyStringId StrConnectorNameQueryTip = MyStringId.GetOrCompute("Exact name of one of your (or your faction's) connectors further away than the list covers.");
        private static readonly MyStringId StrLCDScreenTag = MyStringId.GetOrCompute("LCD tag");
        private static readonly MyStringId StrLCDScreenTagTip = MyStringId.GetOrCompute("LCD panels on the drone's own grid (not subgrids or docked grids) with this tag in their name, whose content is set to the Automata Log app, show the drone's log.");
        private static readonly MyStringId StrSetOrientationTargetInput = MyStringId.GetOrCompute("Set orientation target");
        private static readonly MyStringId StrSetOrientationTargetInputTip = MyStringId.GetOrCompute("Orient the drone to the target GPS coordinate.");
        private static readonly MyStringId StrNavigationTargetGPS = MyStringId.GetOrCompute("Navigate to target");
        private static readonly MyStringId StrNavigationTargetGPSTip = MyStringId.GetOrCompute("Navigate the drone to target.");
        private static readonly MyStringId StrNavigationTargetGPSApproachFrom = MyStringId.GetOrCompute("Approach target from:");
        private static readonly MyStringId StrNavigationTargetGPSApproachFromTip = MyStringId.GetOrCompute("Optional position to approach target from");
        private static readonly MyStringId StrPlaceMountTargetGPS = MyStringId.GetOrCompute("Mount target");
        private static readonly MyStringId StrPlaceMountTargetGPSTip = MyStringId.GetOrCompute("GPS of the point the mount should work on.");
        private static readonly MyStringId StrRelativeOffset = MyStringId.GetOrCompute("Offset (right, up, forward)");
        private static readonly MyStringId StrRelativeOffsetTip = MyStringId.GetOrCompute("Metres in the anchor block's frame, e.g. 0, 5, 20");
        private static readonly MyStringId StrHomeConnector = MyStringId.GetOrCompute("Home connector");
        private static readonly MyStringId StrHomeConnectorTip = MyStringId.GetOrCompute("Your own and your faction's connectors in range. The list refreshes automatically; use Scan to refresh now.");
        private static readonly MyStringId StrHomeBeacon = MyStringId.GetOrCompute("Home is relative to beacon");
        private static readonly MyStringId StrHomeBeaconTip = MyStringId.GetOrCompute("Your own and your faction's beacons in range. Pick the one on the same ship as the home connector.");
        private static readonly MyStringId StrCapabilities = MyStringId.GetOrCompute("Capabilities");
        private static readonly MyStringId StrCapabilitiesTip = MyStringId.GetOrCompute("What this drone can do. Select entries and press 'Exclude selected' to keep it from bidding for jobs that need them.");
        private static readonly MyStringId StrExcluded = MyStringId.GetOrCompute("Excluded");
        private static readonly MyStringId StrExcludedTip = MyStringId.GetOrCompute("Capabilities this drone won't bid with (e.g. no jobs in space, even though it can fly there). Only used when bidding for jobs.");
        private static readonly MyStringId StrMount = MyStringId.GetOrCompute("Mount to place");
        private static readonly MyStringId StrMountTip = MyStringId.GetOrCompute("Welder / grinder / drill: the target ends up on the edge of the tool's reach.");
        private static readonly MyStringId StrAnchor = MyStringId.GetOrCompute("Relative to");
        private static readonly MyStringId StrAnchorTip = MyStringId.GetOrCompute("Home connector or one of your / your faction's beacons in range.");
        private static readonly MyStringId StrWeldUnfinished = MyStringId.GetOrCompute("Weld unfinished blocks");
        private static readonly MyStringId StrWeldUnfinishedTip = MyStringId.GetOrCompute("Finish partly built blocks inside the observation area. Stand-alone drones work inside their observation area.");
        private static readonly MyStringId StrRepairDamaged = MyStringId.GetOrCompute("Repair damaged blocks");
        private static readonly MyStringId StrRepairDamagedTip = MyStringId.GetOrCompute("Repair damaged or deformed blocks inside the observation area. Stand-alone drones work inside their observation area.");
        private static readonly MyStringId StrWeldProjected = MyStringId.GetOrCompute("Build projections");
        private static readonly MyStringId StrWeldProjectedTip = MyStringId.GetOrCompute("Build projected blocks inside the observation area, centre first. Stand-alone drones work inside their observation area.");
        private static readonly MyStringId StrGrind = MyStringId.GetOrCompute("Grind marked blocks");
        private static readonly MyStringId StrGrindTip = MyStringId.GetOrCompute("Grind down blocks painted with the grind colour inside the observation area. Stand-alone drones work inside their observation area.");
        private static readonly MyStringId StrObservationSizeX = MyStringId.GetOrCompute("Area width");
        private static readonly MyStringId StrObservationSizeXTip = MyStringId.GetOrCompute("Width of the observation area along the connector's right axis, in blocks (1 block = 2.5 m). Stand-alone drones only (the area they work in).");
        private static readonly MyStringId StrObservationSizeY = MyStringId.GetOrCompute("Area height");
        private static readonly MyStringId StrObservationSizeYTip = MyStringId.GetOrCompute("Height of the observation area along the connector's up axis, in blocks (1 block = 2.5 m). Stand-alone drones only (the area they work in).");
        private static readonly MyStringId StrObservationSizeZ = MyStringId.GetOrCompute("Area depth");
        private static readonly MyStringId StrObservationSizeZTip = MyStringId.GetOrCompute("Depth of the observation area along the connector's forward axis, in blocks (1 block = 2.5 m). Stand-alone drones only (the area they work in).");
        private static readonly MyStringId StrObservationOffsetX = MyStringId.GetOrCompute("Area offset right");
        private static readonly MyStringId StrObservationOffsetXTip = MyStringId.GetOrCompute("Moves the area along the connector's right axis, in blocks. Stand-alone drones only (the area they work in).");
        private static readonly MyStringId StrObservationOffsetY = MyStringId.GetOrCompute("Area offset up");
        private static readonly MyStringId StrObservationOffsetYTip = MyStringId.GetOrCompute("Moves the area along the connector's up axis, in blocks. Stand-alone drones only (the area they work in).");
        private static readonly MyStringId StrObservationOffsetZ = MyStringId.GetOrCompute("Area offset forward");
        private static readonly MyStringId StrObservationOffsetZTip = MyStringId.GetOrCompute("Moves the area along the connector's forward axis, in blocks. Stand-alone drones only (the area they work in).");
        private static readonly MyStringId StrExcludedCapabilities = MyStringId.GetOrCompute("Excluded capabilities");
        private static readonly MyStringId StrFlight = MyStringId.GetOrCompute("Flight");
        private static readonly MyStringId StrPower = MyStringId.GetOrCompute("Power");
        private static readonly MyStringId StrLcd = MyStringId.GetOrCompute("LCD output");
        private static readonly MyStringId StrObservation = MyStringId.GetOrCompute("Observation area");
        private static readonly MyStringId StrObservationAtGps = MyStringId.GetOrCompute("GPS observation area");
        private static readonly MyStringId StrObservationAtGpsTip = MyStringId.GetOrCompute("Select a GPS coordinate as the drone's observation area.");
        private static readonly MyStringId StrObservationGps = MyStringId.GetOrCompute("GPS point");
        private static readonly MyStringId StrObservationGpsTip = MyStringId.GetOrCompute("The observation area is centred on the selected GPS point (your GPS list). Size sliders as before, offsets off. Stand-alone drones only.");
        private static readonly MyStringId StrWelder = MyStringId.GetOrCompute("Welder");
        private static readonly MyStringId StrGrinder = MyStringId.GetOrCompute("Grinder");
        private static readonly MyStringId StrMining = MyStringId.GetOrCompute("Mining");
        private static readonly MyStringId StrFarming = MyStringId.GetOrCompute("Farming");
        private static readonly MyStringId StrCombat = MyStringId.GetOrCompute("Combat handover");
        private static readonly MyStringId StrJobs = MyStringId.GetOrCompute("Jobs");
        private static readonly MyStringId StrDebug = MyStringId.GetOrCompute("Debug");
        private static readonly MyStringId StrEnableAI = MyStringId.GetOrCompute("Enable AI");
        private static readonly MyStringId StrEnableAITip = MyStringId.GetOrCompute("Turns the drone's AI on or off.\nOff: every order and job stops at once, the controls are handed back to the game, and a flying drone hovers where it is (the game's dampeners).");
        private static readonly MyStringId StrOperationMode = MyStringId.GetOrCompute("Operation mode");
        private static readonly MyStringId StrOperationModeTip = MyStringId.GetOrCompute("Changes the drone's operation mode&#xA;Stand-alone: weld/grind in observation area.&#xA;Managed by orchestrator: drone's actions are assigned by Orchestrator.&#xA;Managed by player: only player orders and debug.&#xA;NOTE: AI must be disabled to change.");
        private static readonly MyStringId StrStandAlone = MyStringId.GetOrCompute("Stand-alone");
        private static readonly MyStringId StrManagedByOrchestrator = MyStringId.GetOrCompute("Managed by orchestrator");
        private static readonly MyStringId StrManagedByPlayer = MyStringId.GetOrCompute("Managed by player");
        private static readonly MyStringId StrBehaviourProfile = MyStringId.GetOrCompute("Behaviour profile");
        private static readonly MyStringId StrBehaviourProfileTip = MyStringId.GetOrCompute("The drone's role: orchestrators rank matching drones first for a job.\nCargo drone: logistics jobs.\nOre hauler: logistics of ores / ice only, and takes nothing else (plus mining).\nWelder:All / Grinder / Welder + grinder drone: weld / grind jobs.\nWelder:Repair / Projected / Unbuilt: any weld job, ranked first for its own kind (repair: damaged blocks; projected: projections; unbuilt: placed, not welded up) and bidding on that kind first.\nMining drone: mining jobs.\nOre scout: the industrial designator's scout jobs, and only those drones (needs an ore detector).\nCombat drone: the hostile designator's engage / escort jobs, and only those drones (needs AI Defensive, AI Flight and AI Offensive). Managed by orchestrator, it also launches on its own detections.\nFarming drone: not implemented yet. Rover: wheeled (not implemented yet).\nCombat drones, ore scouts and ore haulers may dock with small connectors; others need a standard one.\nEvery section below can be unfolded whatever the profile.");
        private static readonly MyStringId StrNone = MyStringId.GetOrCompute("None");
        private static readonly MyStringId StrCargoDrone = MyStringId.GetOrCompute("Cargo drone");
        private static readonly MyStringId StrOreScout = MyStringId.GetOrCompute("Ore scout");
        private static readonly MyStringId StrCombatDrone = MyStringId.GetOrCompute("Combat drone");
        private static readonly MyStringId StrOreHauler = MyStringId.GetOrCompute("Ore hauler");
        private static readonly MyStringId StrWelderDrone = MyStringId.GetOrCompute("Welder:All");
        private static readonly MyStringId StrWelderRepair = MyStringId.GetOrCompute("Welder:Repair");
        private static readonly MyStringId StrWelderProjected = MyStringId.GetOrCompute("Welder:Projected");
        private static readonly MyStringId StrWelderUnbuilt = MyStringId.GetOrCompute("Welder:Unbuilt");
        private static readonly MyStringId StrGrinderDrone = MyStringId.GetOrCompute("Grinder drone");
        private static readonly MyStringId StrWelderGrinderDrone = MyStringId.GetOrCompute("Welder + grinder drone");
        private static readonly MyStringId StrMiningDrone = MyStringId.GetOrCompute("Mining drone");
        private static readonly MyStringId StrFarmingDroneNotImplemented = MyStringId.GetOrCompute("Farming drone (not implemented)");
        private static readonly MyStringId StrRover = MyStringId.GetOrCompute("Rover");
        private static readonly MyStringId StrShowObservationArea = MyStringId.GetOrCompute("Show observation area");
        private static readonly MyStringId StrShowObservationAreaTip = MyStringId.GetOrCompute("Stand-alone drones only: draws the observation area around the home connector. Switches off after a while (server setting, 10 minutes by default).");
        private static readonly MyStringId StrGrindColour = MyStringId.GetOrCompute("Grind colour");
        private static readonly MyStringId StrGrindColourTip = MyStringId.GetOrCompute("Blocks painted exactly this colour are ground down when 'Grind marked blocks' is on. Default RGB(255,0,0).");
        private static readonly MyStringId StrTaskTimeout = MyStringId.GetOrCompute("Task timeout");
        private static readonly MyStringId StrDrawNavigationTargetsTip = MyStringId.GetOrCompute("Red dots on the drone's navigation targets (approach points, waypoints, final target), blue lines between them, starting at the drone. Switches off after a while (server setting, 10 minutes by default).");
        #endregion

        public static ServerConfig.DroneControllerBlockConfig BlockConfig = ServerConfig.Instance.Drone;

        const string IdPrefix = AutomataSession.MOD_NAME + "_";
        static bool Done = false;


        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;

            HideDefaultControls();
            HideDefaultActions();
            CreateControls();
            GateSections();
            CreateActions();
        }

        #region Toolbar actions
        const string ICON_TOGGLE = @"Textures\GUI\Icons\Actions\Toggle.dds";
        const string ICON_ON = @"Textures\GUI\Icons\Actions\SwitchOn.dds";
        const string ICON_OFF = @"Textures\GUI\Icons\Actions\SwitchOff.dds";
        const string ICON_START = @"Textures\GUI\Icons\Actions\Start.dds";

        /// <summary>Toolbar / button panel / PB actions. Same code paths as the terminal controls (synced there).</summary>
        static void CreateActions()
        {
            AddToggleActions("EnableAI", StrAI, l => l.Terminal_Enabled, (l, v) => l.Terminal_Enabled = v);
            AddToggleActions("DrawNavTargets", StrDrawNavigationTargets,
                l => l.Terminal_DrawNavigationTargets, (l, v) => l.Terminal_DrawNavigationTargets = v);
            AddToggleActions("DrawObservationArea", StrDrawObservationArea,
                l => l.Terminal_ObservationAreaDraw, (l, v) => l.Terminal_ObservationAreaDraw = v);
            AddAction("GoHome", Loc.T(StrGoHome), ICON_START, l => l.Terminal_GoHome(), null);
        }

        // 'id' (toggle), 'id'_On, 'id'_Off; the toolbar slot shows On / Off
        static void AddToggleActions(string id, MyStringId name, Func<DroneControllerBlock, bool> get, Action<DroneControllerBlock, bool> set)
        {
            Action<DroneControllerBlock, StringBuilder> writer = (l, sb) => sb.AppendLoc(get(l) ? StrOn : StrOff);
            AddAction(id, Loc.F(StrActionToggle, Loc.T(name)), ICON_TOGGLE, l => { set(l, !get(l)); l.Terminal_Refresh(); }, writer);
            AddAction(id + "_On", Loc.F(StrActionOn, Loc.T(name)), ICON_ON, l => { if (!get(l)) { set(l, true); l.Terminal_Refresh(); } }, writer);
            AddAction(id + "_Off", Loc.F(StrActionOff, Loc.T(name)), ICON_OFF, l => { if (get(l)) { set(l, false); l.Terminal_Refresh(); } }, writer);
        }

        static void AddAction(string id, string name, string icon, Action<DroneControllerBlock> act, Action<DroneControllerBlock, StringBuilder> writer)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>(IdPrefix + id);
            a.Name = new StringBuilder(name);
            a.ValidForGroups = true;
            a.Icon = icon;
            a.Action = (b) => { var l = GetBlock(b); if (l != null) act(l); };
            a.Writer = (b, sb) => { var l = GetBlock(b); if (l != null && writer != null) writer(l, sb); };
            a.Enabled = CustomVisibleCondition;   // drone controllers only (called per tick while in a toolbar: keep cheap)
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(a);
        }
        #endregion

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
        private static void AddSlider(string id, MyStringId title, MyStringId tooltip, float min, float max,
            Func<DroneControllerBlock, float> get, Action<DroneControllerBlock, float> set, Action<DroneControllerBlock, StringBuilder> write)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            c.SetLimits(min, max);
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : min; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) write(l, sb); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddCheckbox(string id, MyStringId title, MyStringId tooltip,
            Func<DroneControllerBlock, bool> get, Action<DroneControllerBlock, bool> set, bool multipleBlocks = true)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddColor(string id, MyStringId title, MyStringId tooltip,
            Func<DroneControllerBlock, Color> get, Action<DroneControllerBlock, Color> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : Color.White; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddLabel(string id, MyStringId text)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + id);
            c.Label = text;
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
            sb.AppendLoc(StrMetresPerSecond, v.ToString(v < 10 ? "F1" : "F0"));
        }

        private static void AppendPercent(StringBuilder sb, float v)
        {
            sb.Append(v.ToString("F0")).Append('%');
        }

        private static void AddWorkModeCheckbox(string id, MyStringId title, MyStringId tooltip, Construction.WorkModes flag)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Enabled = JobsCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null && l.Terminal_GetWorkMode(flag); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetWorkMode(flag, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        // Observation area: stand-alone drones only (others take jobs where they are sent)
        static bool ObservationCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.Terminal_OperationModeValue == (long)OperationMode.StandAlone;
        }

        // GPS observation area: the offsets don't apply (the area is centred on the point)
        static bool ObservationOffsetCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return ObservationCondition(b) && logic != null && !logic.Terminal_ObservationAtGps;
        }

        static bool ObservationGpsCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return ObservationCondition(b) && logic != null && logic.Terminal_ObservationAtGps;
        }

        // The player's GPS points (client side); the saved one first when it isn't among them (set by someone else)
        private sealed class GpsChoice { public string Name; public Vector3D Position; }
        private static readonly List<IMyGps> gpsBuffer = new List<IMyGps>();

        private static void ObservationGpsContent(DroneControllerBlock l, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            string savedName = l.Terminal_ObservationGpsName;
            Vector3D? savedPos = l.Terminal_ObservationGpsPosition;
            bool savedListed = false;
            gpsBuffer.Clear();
            var player = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            if (player != null && MyAPIGateway.Session.GPS != null) gpsBuffer.AddRange(MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId));
            for (int i = 0; i < gpsBuffer.Count; i++)
            {
                var g = gpsBuffer[i];
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(g.Name), MyStringId.GetOrCompute(FormatGps(g.Coords)),
                                                            new GpsChoice { Name = g.Name, Position = g.Coords });
                items.Add(item);
                if (savedPos.HasValue && g.Name == savedName && Vector3D.DistanceSquared(g.Coords, savedPos.Value) < 0.01)
                {
                    selected.Add(item);
                    savedListed = true;
                }
            }
            gpsBuffer.Clear();
            if (savedPos.HasValue && !savedListed)
            {
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute((savedName ?? "GPS") + " *"), MyStringId.GetOrCompute(FormatGps(savedPos.Value)),
                                                            new GpsChoice { Name = savedName, Position = savedPos.Value });
                items.Insert(0, item);
                selected.Add(item);
            }
        }

        private static string FormatGps(Vector3D p)
        {
            return string.Format("X {0:F0}  Y {1:F0}  Z {2:F0}", p.X, p.Y, p.Z);
        }

        private static void ObservationGpsSelected(DroneControllerBlock l, List<MyTerminalControlListBoxItem> selected)
        {
            if (selected.Count == 0) return;
            var choice = selected[0].UserData as GpsChoice;
            if (choice != null) l.Terminal_SetObservationGps(choice.Name, choice.Position);
        }

        // Mining / farming: the game's ores (crops) on the left, the ignored ones on the right, add / remove between them
        private static void AddIgnoreLists(bool crops)
        {
            string id = crops ? "Crops" : "Ores";
            AddListbox("Listbox_Known" + id, crops ? StrCrops : StrOres,
                crops ? StrKnownCropsTip : StrKnownOresTip,
                6, true, (l, items, sel) => l.Terminal_KnownListContent(crops, items, sel), (l, sel) => l.Terminal_SelectKnown(crops, sel), null, true);
            AddButton("Button_Ignore" + id, StrIgnoreSelected,
                crops ? StrIgnoreCropsTip : StrIgnoreOresTip, l => l.Terminal_AddToIgnore(crops), null, true);
            AddListbox("Listbox_Ignored" + id, crops ? StrIgnoredCrops : StrIgnoredOres,
                crops ? StrIgnoredCropsTip : StrIgnoredOresTip,
                4, true, (l, items, sel) => l.Terminal_IgnoredListContent(crops, items, sel), (l, sel) => l.Terminal_SelectIgnored(crops, sel), null, true);
            AddButton("Button_Unignore" + id, StrRemoveSelected,
                crops ? StrUnignoreCropsTip : StrUnignoreOresTip, l => l.Terminal_RemoveFromIgnore(crops), null, true);
        }

        static bool HomeConnectorVisibleCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.HasHomeConnector;
        }

        // in blocks of 2.5 m: size 1–10, offset ±5 (the block clamps and rounds again)
        private static void AddObservationSlider(string id, MyStringId title, MyStringId tooltip, int axis, bool size)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            c.Enabled = size ? (Func<IMyTerminalBlock, bool>)ObservationCondition : ObservationOffsetCondition;
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
                sb.AppendLoc(v == 1 || v == -1 ? StrBlockMetres : StrBlocksMetres, v, (v * DroneControllerBlock.OBSERVATION_UNIT).ToString("F1"));
            };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        /// <summary>
        /// Hides default controls in the inherited Remote Control.
        /// </summary>
        // Remote control controls the drone keeps (On / Off and the generic terminal fields); every other one is hidden
        private static readonly HashSet<string> defaultControlIdsToKeep = new HashSet<string>
        {
            "OnOff",
            "ShowInTerminal",
            "ShowInInventory",
            "ShowInToolbarConfig",
            "Name",
            "ShowOnHUD",
            "CustomData",
        };
        private static readonly HashSet<string> defaultActionIdsToKeep = new HashSet<string>
        {
            "OnOff",
            "OnOff_On",
            "OnOff_Off",
            "ShowOnHUD",
            "ShowOnHUD_On",
            "ShowOnHUD_Off",
        };

        /// <summary>
        /// Hides the remote control's own controls on drone controllers (vanilla remote controls keep them): all but
        /// On / Off and the generic terminal fields. Runs before the drone's controls are added.
        /// </summary>
        public static void HideDefaultControls()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);

            foreach (IMyTerminalControl c in controls)
            {
                if (defaultControlIdsToKeep.Contains(c.Id) || (c.Id != null && c.Id.StartsWith(IdPrefix))) continue;
                c.Visible = TerminalChainedDelegate.Create(c.Visible, AppendedCondition);
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
                if (defaultActionIdsToKeep.Contains(a.Id) || (a.Id != null && a.Id.StartsWith(IdPrefix))) continue;
                a.Enabled = TerminalChainedDelegate.Create(a.Enabled, AppendedCondition);
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

        private static void AddButton(string id, MyStringId title, MyStringId tooltip, Action<DroneControllerBlock> action,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Action = (b) => { var l = GetBlock(b); if (l != null) action(l); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddTextbox(string id, MyStringId title, MyStringId tooltip,
            Func<DroneControllerBlock, StringBuilder> get, Action<DroneControllerBlock, StringBuilder> set,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : new StringBuilder(); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddListbox(string id, MyStringId title, MyStringId tooltip, int rows, bool multiselect,
            Action<DroneControllerBlock, List<MyTerminalControlListBoxItem>, List<MyTerminalControlListBoxItem>> content,
            Action<DroneControllerBlock, List<MyTerminalControlListBoxItem>> select,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Multiselect = multiselect;
            c.VisibleRowsCount = rows;
            c.ListContent = (b, items, selected) => { var l = GetBlock(b); if (l != null) content(l, items, selected); };
            c.ItemSelected = (b, selected) => { var l = GetBlock(b); if (l != null) select(l, selected); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void SetEnabled(string id, Func<IMyTerminalBlock, bool> enabled)
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            foreach (var c in controls)
                if (c.Id == IdPrefix + id) { c.Enabled = enabled; return; }
        }

        private static void SetVisible(string id, Func<IMyTerminalBlock, bool> visible)
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            foreach (var c in controls)
                if (c.Id == IdPrefix + id) { c.Visible = visible; return; }
        }

        // Sections fold: a header checkbox shows / hides the controls after it (until the next header). Local to this
        // player's terminal (all drone controllers alike), not saved or synced. GateSections chains the visibilities.
        private const string SECTION_PREFIX = "Checkbox_Section_";
        private static readonly Dictionary<string, bool> sectionShown = new Dictionary<string, bool>();

        private static bool SectionShown(string id)
        {
            bool shown;
            return sectionShown.TryGetValue(id, out shown) && shown;
        }

        private static void AddSection(string id, MyStringId title)
        {
            AddSeparator("Separator_" + id);
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + SECTION_PREFIX + id);
            c.Title = title;
            c.Tooltip = StrSectionTip;
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => SectionShown(id);
            c.Setter = (b, v) => { sectionShown[id] = v; var l = GetBlock(b); if (l != null) l.Terminal_Refresh(); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        // Every control after a section header is visible only while that header is ticked (and its own condition holds)
        private static void GateSections()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            string section = null;
            foreach (var ctl in controls)
            {
                if (ctl.Id == null || !ctl.Id.StartsWith(IdPrefix)) continue;
                string local = ctl.Id.Substring(IdPrefix.Length);
                if (local.StartsWith(SECTION_PREFIX)) { section = local.Substring(SECTION_PREFIX.Length); continue; }
                if (section == null || local.StartsWith("Separator_")) continue;
                string s = section;
                var visible = ctl.Visible;
                ctl.Visible = b => SectionShown(s) && (visible == null || visible(b));
            }
        }
        #endregion

        /// <summary>
        /// Layout, top to bottom (the block's own On/Off comes first): Enable AI, operation mode, behaviour profile; then
        /// folding sections (a header checkbox each, any profile): home connector, flight, power, LCD output, observation
        /// area (stand-alone only), welder, grinder, mining, farming, combat handover, jobs, debug.
        /// </summary>
        public static void CreateControls()
        {
            NicknameControl.Add<IMyRemoteControl, DroneControllerBlock>(IdPrefix + "Drone", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            // --- AI ---
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_Enabled");
                c.Title = StrEnableAI;
                c.Tooltip = StrEnableAITip;
                c.OnText = StrOn;
                c.OffText = StrOff;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_Enabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_Enabled = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_OperationMode");
                c.Title = StrOperationMode;
                c.Tooltip = StrOperationModeTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = AiOffCondition;
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.StandAlone, Value = StrStandAlone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.ManagedByOrchestrator, Value = StrManagedByOrchestrator });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.ManagedByPlayer, Value = StrManagedByPlayer });
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

            // --- Behaviour profile and ore survey: right under the mode (they decide which jobs the drone takes) ---
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_BehaviourProfile");
                c.Title = StrBehaviourProfile;
                c.Tooltip = StrBehaviourProfileTip;
                c.Visible = CustomVisibleCondition;
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.None, Value = StrNone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsCargoDrone, Value = StrCargoDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsOreScout, Value = StrOreScout });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsCombatDrone, Value = StrCombatDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsOreHauler, Value = StrOreHauler });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderDrone, Value = StrWelderDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderRepair, Value = StrWelderRepair });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderProjected, Value = StrWelderProjected });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderUnbuilt, Value = StrWelderUnbuilt });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsGrinderDrone, Value = StrGrinderDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderGrinderDrone, Value = StrWelderGrinderDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsMiningDrone, Value = StrMiningDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsFarmingDrone, Value = StrFarmingDroneNotImplemented });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsRover, Value = StrRover });
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_BehaviourProfileValue ?? 0;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_BehaviourProfileValue = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            // --- Copy / paste settings (this player's clipboard) ---
            AddButton("Button_CopySettings", StrCopySettings,
                StrCopySettingsTip,
                l => l.Terminal_CopySettings());
            AddButton("Button_PasteSettings", StrPasteSettings,
                StrPasteSettingsTip,
                l => l.Terminal_PasteSettings(), b => DroneControllerBlock.Terminal_HasCopiedSettings, true);
            // --- Home connector ---
            AddSection("Home", StrHomeConnector);
            AddListbox("Listbox_HomeConnector", StrHomeConnector,
                StrHomeConnectorTip,
                5, false, (l, items, sel) => l.Terminal_HomeConnectorListContent(items, sel), (l, sel) => l.Terminal_SelectHomeConnector(sel));
            AddButton("Button_ScanConnectors", StrScanConnectors,
                StrScanConnectorsTip, l => l.Terminal_ScanAnchors());
            AddTextbox("Textbox_ConnectorNameQuery", StrConnectorNameQuery,
                StrConnectorNameQueryTip,
                l => l.Textbox_ConnectorNameQuery, (l, v) => l.Textbox_ConnectorNameQuery = v);
            AddButton("Button_AddConnectorByName", StrAddConnectorByName,
                StrAddConnectorByNameTip,
                l => l.Terminal_AddConnectorByName());
            AddButton("Button_SetCurrentAsHome", StrSetCurrentAsHome,
                StrSetCurrentAsHomeTip,
                l => l.Terminal_SetCurrentAsHome());
            AddCheckbox("Checkbox_HomeNotStation", StrHomeNotStation,
                StrHomeNotStationTip,
                l => l.Terminal_HomeNotStation, (l, v) => { l.Terminal_HomeNotStation = v; }, false);   // home: one drone at a time
            AddListbox("Listbox_HomeBeacon", StrHomeBeacon,
                StrHomeBeaconTip,
                4, false, (l, items, sel) => l.Terminal_HomeBeaconListContent(items, sel), (l, sel) => l.Terminal_SelectHomeBeacon(sel),
                b => { var l = GetBlock(b); return l != null && l.Terminal_HomeNotStation; });
            AddButton("Button_GoHome", StrGoHome,
                StrGoHomeTip,
                l => l.Terminal_GoHome(), HomeConnectorVisibleCondition, true);
            AddButton("Button_StopOrders", StrStopOrders,
                StrStopOrdersTip,
                l => l.Terminal_StopOrders(), null, true);
            // --- Flight ---
            AddSection("Flight", StrFlight);
            AddSlider("Slider_MaxSpeed", StrMaxSpeed,
                StrMaxSpeedTip,
                DroneControllerBlock.MAX_SPEED_MIN, DroneControllerBlock.MAX_SPEED_MAX,
                l => l.Terminal_MaxSpeed, (l, v) => l.Terminal_MaxSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_MaxSpeed));
            AddSlider("Slider_ApproachSpeed", StrApproachSpeed,
                StrApproachSpeedTip,
                DroneControllerBlock.APPROACH_SPEED_MIN, DroneControllerBlock.APPROACH_SPEED_MAX,
                l => l.Terminal_ApproachSpeed, (l, v) => l.Terminal_ApproachSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_ApproachSpeed));
            AddSlider("Slider_SafeSpeed", StrSafeSpeed,
                StrSafeSpeedTip,
                DroneControllerBlock.SAFE_SPEED_MIN, DroneControllerBlock.SAFE_SPEED_MAX,
                l => l.Terminal_SafeSpeed, (l, v) => l.Terminal_SafeSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_SafeSpeed));
            AddSlider("Slider_WaypointTolerance", StrWaypointTolerance,
                StrWaypointToleranceTip,
                DroneControllerBlock.WAYPOINT_TOLERANCE_MIN, DroneControllerBlock.WAYPOINT_TOLERANCE_MAX,
                l => l.Terminal_WaypointTolerance, (l, v) => l.Terminal_WaypointTolerance = v,
                (l, sb) => sb.AppendLoc(StrMetres, l.Terminal_WaypointTolerance.ToString("F1")));
            AddSlider("Slider_MinAltitude", StrMinAltitude,
                StrMinAltitudeTip,
                0f, DroneControllerBlock.MIN_ALTITUDE_MAX,
                l => l.Terminal_MinAltitude, (l, v) => l.Terminal_MinAltitude = v,
                (l, sb) => { if (l.Terminal_MinAltitude <= 0) sb.AppendLoc(StrOff); else sb.AppendLoc(StrMetres, l.Terminal_MinAltitude.ToString("F0")); });
            AddCheckbox("Checkbox_AlignToPGravity", StrAlignToPGravity,
                StrAlignToPGravityTip,
                l => l.Terminal_AlignToPGravity, (l, v) => { l.Terminal_AlignToPGravity = v; l.Terminal_Refresh(); });
            AddSlider("Slider_MaxPitchDegrees", StrMaxPitchDegrees,
                StrMaxPitchDegreesTip, 0f, 90f,
                l => l.Terminal_MaxPitchDegrees, (l, v) => l.Terminal_MaxPitchDegrees = v,
                (l, sb) => sb.AppendLoc(StrDegrees, l.Terminal_MaxPitchDegrees.ToString("F1")));
            AddSlider("Slider_MaxRollDegrees", StrMaxRollDegrees,
                StrMaxRollDegreesTip, 0f, 90f,
                l => l.Terminal_MaxRollDegrees, (l, v) => l.Terminal_MaxRollDegrees = v,
                (l, sb) => sb.AppendLoc(StrDegrees, l.Terminal_MaxRollDegrees.ToString("F1")));
            SetEnabled("Slider_MaxPitchDegrees", b => { var l = GetBlock(b); return l != null && l.Terminal_AlignToPGravity; });
            SetEnabled("Slider_MaxRollDegrees", b => { var l = GetBlock(b); return l != null && l.Terminal_AlignToPGravity; });
            AddSlider("Slider_MaxLoadGravity", StrMaxLoadGravity,
                StrMaxLoadGravityTip,
                0f, 200f,
                l => l.Terminal_MaxLoadGravity, (l, v) => l.Terminal_MaxLoadGravity = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, true));
            AddSlider("Slider_MaxLoadSpace", StrMaxLoadSpace,
                StrMaxLoadSpaceTip,
                0f, 200f,
                l => l.Terminal_MaxLoadSpace, (l, v) => l.Terminal_MaxLoadSpace = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, false));

            // --- Power monitoring ---
            AddSection("Power", StrPower);
            AddCheckbox("Checkbox_AlwaysRefuel", StrAlwaysRefuel,
                StrAlwaysRefuelTip,
                l => l.Terminal_AlwaysRefuel, (l, v) => l.Terminal_AlwaysRefuel = v);
            AddCheckbox("Checkbox_MonitorBattery", StrMonitorBattery,
                StrMonitorBatteryTip,
                l => l.Terminal_MonitorBattery, (l, v) => { l.Terminal_MonitorBattery = v; l.Terminal_Refresh(); });
            AddSlider("Slider_BatteryRefuelThreshold", StrBatteryRefuelThreshold,
                StrBatteryRefuelThresholdTip, 5f, 50f,
                l => l.Terminal_BatteryRefuelThreshold, (l, v) => l.Terminal_BatteryRefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryRefuelThreshold));
            AddSlider("Slider_BatteryOperationalThreshold", StrBatteryOperationalThreshold,
                StrBatteryOperationalThresholdTip, 10f, 95f,
                l => l.Terminal_BatteryOperationalThreshold, (l, v) => l.Terminal_BatteryOperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryOperationalThreshold));
            AddCheckbox("Checkbox_MonitorHydrogen", StrMonitorHydrogen,
                StrMonitorHydrogenTip,
                l => l.Terminal_MonitorHydrogen, (l, v) => { l.Terminal_MonitorHydrogen = v; l.Terminal_Refresh(); });
            AddSlider("Slider_H2RefuelThreshold", StrH2RefuelThreshold,
                StrH2RefuelThresholdTip, 5f, 50f,
                l => l.Terminal_H2RefuelThreshold, (l, v) => l.Terminal_H2RefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2RefuelThreshold));
            AddSlider("Slider_H2OperationalThreshold", StrH2OperationalThreshold,
                StrH2OperationalThresholdTip, 10f, 95f,
                l => l.Terminal_H2OperationalThreshold, (l, v) => l.Terminal_H2OperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2OperationalThreshold));
            SetEnabled("Slider_BatteryRefuelThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorBattery; });
            SetEnabled("Slider_BatteryOperationalThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorBattery; });
            SetEnabled("Slider_H2RefuelThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorHydrogen; });
            SetEnabled("Slider_H2OperationalThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorHydrogen; });

            // --- LCD / terminal log (player-facing; the server log is set in the mod config) ---
            AddSection("Lcd", StrLcd);
            AddTextbox("Textbox_LCDScreenTag", StrLCDScreenTag,
                StrLCDScreenTagTip,
                l => l.Terminal_LCDScreenTag, (l, v) => l.Terminal_LCDScreenTag = v, null, true);
            AddColor("Color_LcdForeground", StrLcdForeground, StrLcdForegroundTip,
                l => l.Terminal_LcdForeground, (l, v) => l.Terminal_LcdForeground = v);
            AddColor("Color_LcdBackground", StrLcdBackground, StrLcdBackgroundTip,
                l => l.Terminal_LcdBackground, (l, v) => l.Terminal_LcdBackground = v);
            AddSlider("Slider_LcdFontSize", StrLcdFontSize,
                StrLcdFontSizeTip, 0.1f, 2f,
                l => l.Terminal_LcdFontSize, (l, v) => l.Terminal_LcdFontSize = v, (l, sb) => sb.Append(l.Terminal_LcdFontSize.ToString("F2")));
            AddCheckbox("Checkbox_LcdShowHeader", StrLcdShowHeader,
                StrLcdShowHeaderTip,
                l => l.Terminal_LcdShowHeader, (l, v) => l.Terminal_LcdShowHeader = v);
            AddCheckbox("Checkbox_UseBroadcastController", Util.BroadcastControllerControl.StrUseBroadcastController,
                Util.BroadcastControllerControl.Tooltip(StrBroadcastSends),
                l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);

            // --- Observation area (stand-alone only) ---
            AddSection("Observation", StrObservation);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_ObservationAtGps");
                c.Title = StrObservationAtGps;
                c.Tooltip = StrObservationAtGpsTip;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Enabled = ObservationCondition;
                c.Getter = (b) => { var l = GetBlock(b); return l != null && l.Terminal_ObservationAtGps; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ObservationAtGps = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddListbox("Listbox_ObservationGps", StrObservationGps, StrObservationGpsTip, 5, false,
                ObservationGpsContent, ObservationGpsSelected, ObservationGpsCondition);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_ObservationAreaDraw");
                c.Title = StrShowObservationArea;
                c.Tooltip = StrShowObservationAreaTip;
                c.OnText = StrOn;
                c.OffText = StrOff;
                c.Visible = CustomVisibleCondition;
                c.Enabled = ObservationCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_ObservationAreaDraw ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ObservationAreaDraw = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            AddObservationSlider("Slider_ObservationSizeX", StrObservationSizeX, StrObservationSizeXTip, 0, true);
            AddObservationSlider("Slider_ObservationSizeY", StrObservationSizeY, StrObservationSizeYTip, 1, true);
            AddObservationSlider("Slider_ObservationSizeZ", StrObservationSizeZ, StrObservationSizeZTip, 2, true);
            AddObservationSlider("Slider_ObservationOffsetX", StrObservationOffsetX, StrObservationOffsetXTip, 0, false);
            AddObservationSlider("Slider_ObservationOffsetY", StrObservationOffsetY, StrObservationOffsetYTip, 1, false);
            AddObservationSlider("Slider_ObservationOffsetZ", StrObservationOffsetZ, StrObservationOffsetZTip, 2, false);

            // --- Welder ---
            AddSection("Welder", StrWelder);
            AddWorkModeCheckbox("Checkbox_WeldUnfinished", StrWeldUnfinished,
                StrWeldUnfinishedTip, Construction.WorkModes.WeldUnfinishedBlocks);
            AddWorkModeCheckbox("Checkbox_RepairDamaged", StrRepairDamaged,
                StrRepairDamagedTip, Construction.WorkModes.RepairDamagedBlocks);
            AddWorkModeCheckbox("Checkbox_WeldProjected", StrWeldProjected,
                StrWeldProjectedTip, Construction.WorkModes.WeldProjectedBlocks);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_WorkPriority");
                c.Title = Construction.WorkPriorities.Title;
                c.Tooltip = Construction.WorkPriorities.Tooltip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.ComboBoxContent = Construction.WorkPriorities.ComboContent;
                c.Getter = (b) => GetBlock(b)?.Terminal_ConstructionWorkPriority ?? 0;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ConstructionWorkPriority = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddColor("Color_WeldIgnoreColor", StrWeldIgnoreColor,
                StrWeldIgnoreColorTip,
                l => l.Terminal_WeldIgnoreColor, (l, v) => l.Terminal_WeldIgnoreColor = v);

            // --- Grinder ---
            AddSection("Grinder", StrGrinder);
            AddWorkModeCheckbox("Checkbox_Grind", StrGrind,
                StrGrindTip, Construction.WorkModes.Grind);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyRemoteControl>(IdPrefix + "Color_GrindColor");
                c.Title = StrGrindColour;
                c.Tooltip = StrGrindColourTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_GrindColor ?? Color.Red;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_GrindColor = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // --- Mining: ore survey, ores this drone won't mine ---
            AddSection("Mining", StrMining);
            AddCheckbox("Checkbox_SurveyForOres", StrSurveyForOres,
                StrSurveyForOresTip,
                l => l.Terminal_SurveyForOres, (l, v) => l.Terminal_SurveyForOres = v);

            AddIgnoreLists(false);

            // --- Farming (not implemented yet): crops this drone won't farm ---
            AddSection("Farming", StrFarming);
            AddIgnoreLists(true);

            // --- Combat handover (any drone can be set up; only Combat drone profiles take combat jobs / launch) ---
            AddSection("Combat", StrCombat);
            AddSlider("Slider_CombatHandoverDistance", StrCombatHandoverDistance,
                StrCombatHandoverDistanceTip,
                DroneControllerBlock.COMBAT_HANDOVER_MIN, DroneControllerBlock.COMBAT_HANDOVER_MAX,
                l => l.Terminal_CombatHandoverDistance, (l, v) => l.Terminal_CombatHandoverDistance = v,
                (l, sb) => sb.AppendLoc(StrMetres, l.Terminal_CombatHandoverDistance.ToString("F0")));
            AddSlider("Slider_CombatNoTargetSeconds", StrCombatNoTargetSeconds,
                StrCombatNoTargetSecondsTip,
                DroneControllerBlock.COMBAT_NO_TARGET_MIN, DroneControllerBlock.COMBAT_NO_TARGET_MAX,
                l => l.Terminal_CombatNoTargetSeconds, (l, v) => l.Terminal_CombatNoTargetSeconds = v,
                (l, sb) => sb.AppendLoc(StrSeconds, l.Terminal_CombatNoTargetSeconds.ToString("F0")));
            AddSlider("Slider_AutoLaunchRadius", StrAutoLaunchRadius,
                StrAutoLaunchRadiusTip,
                0f, DroneControllerBlock.AUTO_LAUNCH_RADIUS_MAX,
                l => l.Terminal_AutoLaunchRadius, (l, v) => l.Terminal_AutoLaunchRadius = v,
                (l, sb) => { if (l.Terminal_AutoLaunchRadius <= 0) sb.AppendLoc(StrOff); else sb.AppendLoc(StrMetres, l.Terminal_AutoLaunchRadius.ToString("F0")); });
            AddSlider("Slider_EscortFollowDistance", StrEscortFollowDistance,
                StrEscortFollowDistanceTip,
                DroneControllerBlock.ESCORT_FOLLOW_MIN, DroneControllerBlock.ESCORT_FOLLOW_MAX,
                l => l.Terminal_EscortFollowDistance, (l, v) => l.Terminal_EscortFollowDistance = v,
                (l, sb) => sb.AppendLoc(StrMetres, l.Terminal_EscortFollowDistance.ToString("F0")));
            AddCheckbox("Checkbox_CombatRetreatOnFlee", StrCombatRetreatOnFlee,
                StrCombatRetreatOnFleeTip,
                l => l.Terminal_CombatRetreatOnFlee, (l, v) => l.Terminal_CombatRetreatOnFlee = v);
            AddCheckbox("Checkbox_CombatRetreatOnLowPower", StrCombatRetreatOnLowPower,
                StrCombatRetreatOnLowPowerTip,
                l => l.Terminal_CombatRetreatOnLowPower, (l, v) => l.Terminal_CombatRetreatOnLowPower = v);

            // --- Jobs (any mode but "managed by player") ---
            AddSection("Jobs", StrJobs);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_TaskTimeout");
                c.Title = StrTaskTimeout;
                c.Tooltip = StrTaskTimeoutTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.SetLimits(b => 0f, b => DroneControllerBlock.MaxTaskTimeoutSeconds);
                c.Getter = (b) => GetBlock(b)?.Terminal_TaskTimeoutSeconds ?? 30f;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_TaskTimeoutSeconds = v; };
                c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) sb.AppendLoc(StrSeconds, (int)l.Terminal_TaskTimeoutSeconds); };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddLabel("Label_ExcludedCapabilities", StrExcludedCapabilities);
            AddListbox("Listbox_Capabilities", StrCapabilities,
                StrCapabilitiesTip,
                5, true, (l, items, sel) => l.Terminal_CapabilityListContent(items, sel), (l, sel) => l.Terminal_SelectCapabilities(sel), JobsCondition, true);
            AddListbox("Listbox_ExcludedCapabilities", StrExcluded,
                StrExcludedTip,
                4, true, (l, items, sel) => l.Terminal_ExcludedListContent(items, sel), (l, sel) => l.Terminal_SelectExclusions(sel), JobsCondition, true);
            AddButton("Button_ExcludeSelected", StrExcludeSelected,
                StrExcludeSelectedTip, l => l.Terminal_ExcludeSelected(), JobsCondition, true);
            AddButton("Button_RemoveSelectedExclusions", StrRemoveSelected,
                StrRemoveSelectedExclusionsTip, l => l.Terminal_RemoveSelectedExclusions(), JobsCondition, true);
            AddButton("Button_ClearExclusions", StrClearExclusions,
                StrClearExclusionsTip, l => l.Terminal_ClearExclusions(), JobsCondition, true);

            // --- Debug: views (any mode) and orders ("managed by player" only) ---
            AddSection("Debug", StrDebug);
            {
                // Always enabled: a view, not an order (any operation mode, AI on or off)
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_DrawNavigationTargets");
                c.Title = StrDrawNavigationTargets;
                c.Tooltip = StrDrawNavigationTargetsTip;
                c.OnText = StrOn;
                c.OffText = StrOff;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_DrawNavigationTargets ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_DrawNavigationTargets = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddButton("Button_Debug_Log", StrDebugLog, StrDebugLogTip, l => l.Terminal_Debug_Log());
            AddTextbox("Textbox_Debug_SetOrientationTargetInput", StrSetOrientationTargetInput,
                StrSetOrientationTargetInputTip,
                l => l.Terminal_Debug_SetOrientationTargetInput, (l, v) => l.Terminal_Debug_SetOrientationTargetInput = v, DebugCondition);
            AddButton("Button_Debug_SetOrientationTargetAction", StrSetOrientationTargetAction, StrSetOrientationTargetActionTip,
                l => l.Terminal_Debug_SetOrientationTarget(), DebugCondition);
            AddTextbox("Textbox_Debug_NavigationTargetGPS", StrNavigationTargetGPS, StrNavigationTargetGPSTip,
                l => l.Textbox_Debug_NavigationTargetGPS, (l, v) => l.Textbox_Debug_NavigationTargetGPS = v, DebugCondition);
            AddTextbox("Textbox_Debug_NavigationTargetGPSApproachFrom", StrNavigationTargetGPSApproachFrom, StrNavigationTargetGPSApproachFromTip,
                l => l.Textbox_Debug_NavigationTargetGPSApproachFrom, (l, v) => l.Textbox_Debug_NavigationTargetGPSApproachFrom = v, DebugCondition);
            AddButton("Button_Debug_NavigationTargetGPSActionTrigger", StrNavigationTargetGPSActionTrigger, StrNavigationTargetGPSActionTriggerTip,
                l => l.Terminal_Debug_NavigateToTarget(), DebugCondition);
            AddButton("Button_Debug_QueueWaypoint", StrQueueWaypoint,
                StrQueueWaypointTip,
                l => l.Terminal_Debug_QueueWaypoint(), DebugCondition);
            AddListbox("Listbox_Debug_Mount", StrMount,
                StrMountTip,
                4, false, (l, items, sel) => l.Terminal_Debug_MountListContent(items, sel), (l, sel) => l.Terminal_Debug_SelectMount(sel), DebugCondition);
            AddTextbox("Textbox_Debug_PlaceMountTargetGPS", StrPlaceMountTargetGPS, StrPlaceMountTargetGPSTip,
                l => l.Textbox_Debug_PlaceMountTargetGPS, (l, v) => l.Textbox_Debug_PlaceMountTargetGPS = v, DebugCondition);
            AddButton("Button_Debug_PlaceMount", StrPlaceMount, StrPlaceMountTip,
                l => l.Terminal_Debug_PlaceMount(), DebugCondition);
            AddListbox("Listbox_Debug_Anchor", StrAnchor,
                StrAnchorTip,
                4, false, (l, items, sel) => l.Terminal_Debug_AnchorListContent(items, sel), (l, sel) => l.Terminal_Debug_SelectAnchor(sel), DebugCondition);
            AddTextbox("Textbox_Debug_RelativeOffset", StrRelativeOffset, StrRelativeOffsetTip,
                l => l.Textbox_Debug_RelativeOffset, (l, v) => l.Textbox_Debug_RelativeOffset = v, DebugCondition);
            AddButton("Button_Debug_NavigateRelative", StrNavigateRelative, StrNavigateRelativeTip,
                l => l.Terminal_Debug_NavigateRelative(), DebugCondition);
        }
    }
}
