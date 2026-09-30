using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Construction;
using Automata.Inventory;
using Automata.Logistics;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// Stand-alone construction (OperationMode.StandAlone), server side. Never goes through the virtual network.
    ///
    /// Cycle: scan the observation area (embedded ConstructionComputer, central-out) -> dock at home -> unload,
    /// read the home conveyor network, plan a batch that fits the load limit and cargo -> pull the components ->
    /// fly to each block and work on it ONE AT A TIME (tool on only once in position, off as soon as that block is
    /// done, so approaching neighbours aren't welded on the way and the drone doesn't wall itself out) -> go home,
    /// repeat until the area has nothing left to do.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private enum ConstructionPhase : byte
        {
            Idle,
            WaitingForDock,   // blocks found away from home: flying home to plan and load
            Running,          // executing currentJob's tasks
        }

        private enum TaskStatus : byte
        {
            Running,
            Done,
            SkipBlock,        // this NavigateToolTo and its tool task
            ToReturnHome,     // skip ahead to the job's ReturnHome
            Failed,           // end the job here
            Interrupted,      // someone else took the controls
        }

        private const float CONSTRUCTION_LINE_TOLERANCE = 0.5f;      // m: stay on the approach line
        private const int CONSTRUCTION_LEG_SETTLE_TICKS = 3 * 60;    // a detour leg hovering close by: good enough
        private const double CONSTRUCTION_LEG_CLOSE = 3.0;           // m: "close by" for that
        private const double CONSTRUCTION_CLEARANCE = 2.0;           // m: kept between the hull and anything when detouring
        private const int CONSTRUCTION_DETOUR_RAISES = 8;            // height raises tried before giving up on a block
        private const float CONSTRUCTION_ROUTE_TOLERANCE = 1.5f;     // m: arrival at detour / back-out points
        private const double CONSTRUCTION_BACKOUT_MIN = 5.0;         // m: back-out from the dock, at least
        public const int CONSTRUCTION_STOP_PAUSE_TICKS = 120 * 60;   // "Stop" pauses stand-alone work for 5 min

        private ConstructionComputer constructionComputer;           // embedded, BuiltInToDrone
        private readonly ConveyorNetwork homeNetwork = new ConveyorNetwork();
        private long homeNetworkLastUpdateFrame = 0;
        private int homeNetworkUpdateIntervalTicks = 600; // 10 seconds
        private readonly List<ConstructionTarget> scannedTargets = new List<ConstructionTarget>();
        private readonly List<IMyCubeGrid> droneGrids = new List<IMyCubeGrid>();
        private readonly List<IMyCubeGrid> homeGrids = new List<IMyCubeGrid>();
        private readonly List<VRage.Game.ModAPI.Ingame.MyInventoryItem> itemBuffer = new List<VRage.Game.ModAPI.Ingame.MyInventoryItem>();
        private readonly List<KVPair> kvBuffer = new List<KVPair>();
        private ConstructionPhase cPhase;
        private Orchestrator.Job currentJob;                  // one trip: the drone works its tasks in order
        private int taskIndex;
        private bool taskStarted;
        private int taskStartFrame;
        private double taskBestDistance;                      // progress watchdog for navigation tasks (per leg)
        private int taskProgressFrame;
        private FlightOrder watchedOrder;
        private int lastConstructionScanFrame = int.MinValue / 2;
        private int lastReportedJobCount = -1;
        private FlightOrder constructionOrder;          // the order construction issued; anything else means "stop"
        private readonly List<FlightOrder> constructionLegs = new List<FlightOrder>();   // detour legs flown before it
        private readonly List<IHitInfo> rayHits = new List<IHitInfo>();
        private readonly List<Vector3D> routePoints = new List<Vector3D>();
        private int constructionPausedUntil = int.MinValue / 2;
        private string lastConstructionProblem;          // reported once until it changes

        private static readonly string COMPONENT_TYPE = "MyObjectBuilder_Component";
        private static readonly string ORE_TYPE = "MyObjectBuilder_Ore";

        #region Terminal
        public bool IsStandAlone { get { return settings != null && settings.OperationMode == OperationMode.StandAlone; } }

        public Construction.WorkModes Terminal_ConstructionWorkModes
        {
            get { return settings != null ? settings.ConstructionWorkModes : Construction.WorkModes.None; }
            set
            {
                if (settings == null) return;
                settings.ConstructionWorkModes = value & (Construction.WorkModes.WeldUnfinishedBlocks | Construction.WorkModes.RepairDamagedBlocks
                                                         | Construction.WorkModes.WeldProjectedBlocks | Construction.WorkModes.Grind);
                lastConstructionScanFrame = int.MinValue / 2;   // rescan with the new modes
                SaveSettings();
                SyncSetting(DroneSettingKey.ConstructionWorkModes);
            }
        }

        public bool Terminal_GetWorkMode(Construction.WorkModes flag)
        {
            return (Terminal_ConstructionWorkModes & flag) != 0;
        }

        public void Terminal_SetWorkMode(Construction.WorkModes flag, bool on)
        {
            var modes = Terminal_ConstructionWorkModes;
            Terminal_ConstructionWorkModes = on ? modes | flag : modes & ~flag;
        }

        public Color Terminal_GrindColor
        {
            get { return settings != null ? new Color(settings.GrindColor) : Color.Red; }
            set
            {
                if (settings == null) return;
                settings.GrindColor = value.PackedValue;
                SaveSettings();
                SyncSetting(DroneSettingKey.GrindColor);
            }
        }
        #endregion

        #region Update hooks (server)
        // UpdateBeforeSimulation100: when to start a trip
        private void UpdateConstruction100()
        {
            if (!IsServer || settings == null) return;
            if (jobSource == JobSource.Orchestrator) return;   // awarded jobs: UpdateOrchestratorJobs
            bool active = settings.IsEnabled && IsStandAlone && HasHomeConnector && settings.ConstructionWorkModes != Construction.WorkModes.None;
            if (!active)
            {
                if (cPhase != ConstructionPhase.Idle) StopJob();
                return;
            }
            if (needsBatteryRecharge || needsHydrogenRefuel)
            {
                if (cPhase != ConstructionPhase.Idle)
                {
                    Report("Construction paused: low power");
                    StopJob();   // MonitorPower sends the drone home
                }
                return;
            }

            int now = MyAPIGateway.Session.GameplayFrameCounter;
            switch (cPhase)
            {
                case ConstructionPhase.Idle:
                    if (now < constructionPausedUntil) return;
                    if (now - lastConstructionScanFrame < AutomataSession.GetConfig().ConstructionComputer.DroneScanIntervalSeconds * 60) return;
                    lastConstructionScanFrame = now;
                    if (activeFlightOrder != null && dockingHomeId == 0) return;   // busy with a player's order
                    if (ScanConstructionTargets() == 0) return;
                    if (!HasToolsFor(scannedTargets)) return;
                    if (IsDockedAtHome()) StartStandAloneJob();
                    else if (dockingHomeId == 0 && GoHome()) cPhase = ConstructionPhase.WaitingForDock;
                    break;

                case ConstructionPhase.WaitingForDock:
                    if (IsDockedAtHome()) StartStandAloneJob();
                    else if (dockingHomeId == 0 && activeFlightOrder == null) cPhase = ConstructionPhase.Idle;   // docking failed / cancelled
                    break;
            }
        }

        // UpdateBeforeSimulation10: the task runner
        private void UpdateConstruction10()
        {
            if (!IsServer || cPhase != ConstructionPhase.Running || currentJob == null) return;
            RunTasks();
        }
        #endregion

        #region Scan / plan
        private int ScanConstructionTargets()
        {
            scannedTargets.Clear();
            var home = GetHomeConnector();
            MatrixD areaMatrix;
            Vector3D half;
            if (home == null || !TryGetObservationArea(out areaMatrix, out half)) return 0;
            if (constructionComputer == null)
                constructionComputer = new ConstructionComputer(Entity, Construction.OperationMode.BuiltInToDrone);
            RefreshDroneGrids();
            Vector3 grindMask = MyColorPickerConstants.HSVToHSVOffset(new Color(settings.GrindColor).ColorToHSVDX11());
            int count = constructionComputer.ScanArea(home.CubeGrid, ref areaMatrix, ref half, settings.ConstructionWorkModes, grindMask,
                                                      droneGrids, AutomataSession.GetConfig().ConstructionComputer.DroneMaxJobsPerScan, scannedTargets);
            Log.Debug("Drone {0}: construction scan found {1} blocks", Entity.EntityId, count);
            if (count != lastReportedJobCount)
            {
                lastReportedJobCount = count;
                if (count > 0) Report("Construction: {0} blocks to work on", count);
                else Report("Construction: nothing to do in the area");
            }
            return count;
        }

        /// <summary>
        /// Frame for a stand-alone trip's positions. A station: world positions. A moving base ("Home connector is
        /// not station"): the home beacon, so the work follows the base. Without a beacon a moving base can't be
        /// worked on. (Never the home connector itself: jobs are shared with orchestrators.)
        /// </summary>
        private bool TryGetStandAloneFrame(IMyShipConnector home, out IMyTerminalBlock beacon)
        {
            beacon = null;
            bool moving = settings.HomeNotStation
                || (home.CubeGrid.Physics != null && home.CubeGrid.Physics.LinearVelocity.LengthSquared() > 0.25);
            if (!moving) return true;
            beacon = settings.HomeBeaconId != 0 ? AnchorDirectory.Resolve(block, settings.HomeBeaconId) : null;
            if (beacon != null) return true;
            ReportProblem(settings.HomeNotStation
                ? "Construction: moving base, select its beacon (Home is relative to beacon)"
                : "Construction: home is moving: tick 'Home connector is not station' and select its beacon");
            return false;
        }

        // Docked at home: plan one trip from the scan (what the home network has, what the drone can lift and hold)
        private void StartStandAloneJob()
        {
            cPhase = ConstructionPhase.Idle;
            var home = GetHomeConnector();
            if (home == null) return;
            IMyTerminalBlock beacon;
            if (!TryGetStandAloneFrame(home, out beacon)) return;
            if (scannedTargets.Count == 0 && ScanConstructionTargets() == 0)
                return;

            BuildHomeNetwork(home);
            UnloadCargo(null); // leftovers back first, so the plan sees everything
            homeNetwork.UpdateInventories();

            // Capacity: load limit for where the drone is (thrusters may be asleep: use their rated thrust)
            double limitPct = flightState.InGravity ? settings.MaxLoadGravity : settings.MaxLoadSpace;
            double g = flightState.InGravity ? flightState.Gravity.Length() : 0;
            double maxTotal = flightState.InGravity
                ? RatedThrust(Base6Directions.Direction.Up) / g
                : WeakestRatedThrust() / SPACE_REFERENCE_ACCEL;
            double maxMass = maxTotal * limitPct / 100.0 - DroneOwnMass();
            double maxVolume = DroneCargoFreeVolume();

            var missing = new DiscreteInventory();
            var job = constructionComputer.PlanJob(scannedTargets, homeNetwork.GetCurrentInventory(), Math.Max(0, maxMass), maxVolume,
                                                   beacon, (float)g, missing);
            if (job == null)
            {
                ReportProblem(maxMass <= 0 ? "Construction: no load capacity (max load setting)" : "Construction: missing " + DescribeTop(missing, 3));
                return;
            }
            scannedTargets.Clear();   // rescan after this trip: neighbours become buildable as blocks are finished
            StartJob(job, JobSource.StandAlone);
        }
        #endregion

        #region Home conveyor network and cargo
        private bool IsDockedAtHome()
        {
            if (settings == null || settings.HomeConnectorId == 0) return false;
            for (int i = 0; i < connectors.Count; i++)
            {
                var c = connectors[i];
                if (c.IsConnected && c.OtherConnector != null && c.OtherConnector.EntityId == settings.HomeConnectorId) return true;
            }
            return false;
        }

        private void RefreshDroneGrids()
        {
            droneGrids.Clear();
            if (shipController != null)
                shipController.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(droneGrids);
        }

        // The conveyor-connected base behind the home connector (logical group, minus the drone)
        private void BuildHomeNetwork(IMyShipConnector home)
        {
            RefreshDroneGrids();
            homeNetwork.Clear();
            homeGrids.Clear();
            home.CubeGrid.GetGridGroup(GridLinkTypeEnum.Logical).GetGrids(homeGrids);
            for (int i = 0; i < homeGrids.Count; i++)
            {
                var grid = homeGrids[i];
                if (droneGrids.Contains(grid) || grid.Physics == null || HasDroneController(grid)) continue;   // other docked drones
                foreach (var b in grid.GetFatBlocks<IMyCargoContainer>()) if (b.IsFunctional) homeNetwork.cargoContainers.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyShipConnector>()) if (b.IsFunctional) homeNetwork.connectors.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyAssembler>()) if (b.IsFunctional) homeNetwork.assemblers.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyCollector>()) if (b.IsFunctional) homeNetwork.collectors.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyRefinery>()) if (b.IsFunctional) homeNetwork.refineries.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyReactor>()) if (b.IsFunctional) homeNetwork.reactors.Add(b);
            }
            homeGrids.Clear();
        }


        // Pulls 'load' components from the home network into the drone's cargo (conveyor rules apply)
        private int TransferToDrone(DiscreteInventory load)
        {
            int total = 0;
            kvBuffer.Clear();
            load.GetAllItems(kvBuffer);
            for (int k = 0; k < kvBuffer.Count; k++)
            {
                string subtype = kvBuffer[k].Key.String;
                int remaining = kvBuffer[k].Value;
                remaining -= PullFrom(homeNetwork.cargoContainers, subtype, remaining, 0);
                if (remaining > 0) remaining -= PullFrom(homeNetwork.connectors, subtype, remaining, 0);
                if (remaining > 0) remaining -= PullFrom(homeNetwork.assemblers, subtype, remaining, 1);   // assembler output
                total += kvBuffer[k].Value - remaining;
            }
            kvBuffer.Clear();
            return total;
        }

        private int PullFrom<T>(List<T> sources, string subtype, int wanted, int inventoryIndex) where T : IMyCubeBlock
        {
            int moved = 0;
            for (int s = 0; s < sources.Count && moved < wanted; s++)
            {
                var src = sources[s];
                if (src == null || src.Closed || src.InventoryCount <= inventoryIndex) continue;
                var inv = src.GetInventory(inventoryIndex);
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = itemBuffer.Count - 1; i >= 0 && moved < wanted; i--)
                {
                    var item = itemBuffer[i];
                    if (item.Type.TypeId != COMPONENT_TYPE || item.Type.SubtypeId != subtype) continue;
                    int amount = Math.Min(wanted - moved, (int)item.Amount);
                    if (amount <= 0) continue;
                    moved += MoveToDroneCargo(inv, i, amount);
                }
            }
            itemBuffer.Clear();
            return moved;
        }

        // What actually moved (TransferItemTo reports success even when only part of the stack fits)
        private int MoveToDroneCargo(IMyInventory src, int itemIndex, int amount)
        {
            var item = src.GetItemAt(itemIndex);
            if (!item.HasValue) return 0;
            var type = item.Value.Type;
            MyFixedPoint before = AmountAt(src, itemIndex, type);
            int moved = 0;
            for (int c = 0; c < cargoContainers.Count && moved < amount; c++)
            {
                var dst = cargoContainers[c].GetInventory(0);
                if (dst == null) continue;
                src.TransferItemTo(dst, itemIndex, null, true, (MyFixedPoint)(amount - moved), true);
                moved = (int)(before - AmountAt(src, itemIndex, type));
            }
            return moved;
        }

        private static MyFixedPoint AmountAt(IMyInventory inv, int index, VRage.Game.ModAPI.Ingame.MyItemType type)
        {
            var it = inv.GetItemAt(index);
            return it.HasValue && it.Value.Type == type ? it.Value.Amount : (MyFixedPoint)0;
        }

        // Drone cargo back into the home network (docked, conveyor-connected). 'only' lists the item subtypes to
        // unload; null / empty = all components (ice, ammo etc. stay: the drone may need them)
        // Returns false when something that should have gone stayed in the drone (no room on the other side)
        private bool UnloadCargo(DiscreteInventory only)
        {
            if (homeNetwork.cargoContainers.Count == 0) return false;   // built by the caller for the connector docked at
            bool filtered = only != null && !only.IsEmpty();
            bool left = false;
            for (int c = 0; c < cargoContainers.Count; c++)
            {
                var src = cargoContainers[c].GetInventory(0);
                if (src == null) continue;
                itemBuffer.Clear();
                src.GetItems(itemBuffer);
                for (int i = itemBuffer.Count - 1; i >= 0; i--)
                {
                    string typeId = itemBuffer[i].Type.TypeId;
                    // Filters name subtypes only: restrict them to components and ore ("Iron" ore, not ingots)
                    if (filtered ? only.GetItemCount(itemBuffer[i].Type.SubtypeId) <= 0 || (typeId != COMPONENT_TYPE && typeId != ORE_TYPE)
                                 : typeId != COMPONENT_TYPE) continue;
                    var type = itemBuffer[i].Type;
                    // Container after container until the whole stack is gone (a transfer can be partial)
                    for (int d = 0; d < homeNetwork.cargoContainers.Count && AmountAt(src, i, type) > 0; d++)
                    {
                        var dst = homeNetwork.cargoContainers[d].GetInventory(0);
                        if (dst != null) src.TransferItemTo(dst, i, null, true, null, true);
                    }
                    if (AmountAt(src, i, type) > 0) left = true;
                }
            }
            itemBuffer.Clear();
            return !left;
        }

        // Another Automata drone (its cargo is its own business)
        private static bool HasDroneController(IMyCubeGrid grid)
        {
            foreach (var rc in grid.GetFatBlocks<IMyRemoteControl>())
                if (rc.GameLogic != null && rc.GameLogic.GetAs<DroneControllerBlock>() != null) return true;
            return false;
        }

        // Drone mass alone (its own grids; a docked base is a separate physics body)
        private double DroneOwnMass()
        {
            RefreshDroneGrids();
            double m = 0;
            for (int i = 0; i < droneGrids.Count; i++)
                if (droneGrids[i].Physics != null) m += droneGrids[i].Physics.Mass;
            return m;
        }

        private double DroneCargoFreeVolume()
        {
            double v = 0;
            for (int i = 0; i < cargoContainers.Count; i++)
            {
                var inv = cargoContainers[i].GetInventory(0);
                if (inv != null) v += (double)(inv.MaxVolume - inv.CurrentVolume);
            }
            return v;
        }

        // Thrust the drone can have once awake (docked drones have their thrusters switched off)
        private double RatedThrust(Base6Directions.Direction dir)
        {
            double t = 0;
            var list = allThrusters[(int)dir];
            for (int i = 0; i < list.Count; i++)
                if (list[i].IsFunctional) t += list[i].MaxEffectiveThrust;
            return t;
        }

        private double WeakestRatedThrust()
        {
            double min = double.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                double t = RatedThrust((Base6Directions.Direction)i);
                if (t > 0 && t < min) min = t;
            }
            return min == double.MaxValue ? 0 : min;
        }

        private string DescribeTop(DiscreteInventory inv, int max)
        {
            kvBuffer.Clear();
            inv.GetAllItems(kvBuffer);
            var sb = new StringBuilder();
            for (int i = 0; i < kvBuffer.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(kvBuffer[i].Value).Append(' ').Append(kvBuffer[i].Key.String);
            }
            if (kvBuffer.Count > max) sb.Append(", ...");
            kvBuffer.Clear();
            return sb.Length > 0 ? sb.ToString() : "components";
        }
        #endregion
    }
}
