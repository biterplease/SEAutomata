using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// Clearing the drone's own inventories (cargo containers, and every connector, collector, welder, grinder and drill of
    /// its construct: ToolStores): components, ores and ingots go
    /// into the conveyor network of a connector it docks at, so every job starts empty. Ice stays when the drone has an
    /// O2/H2 generator (its fuel); ammo, bottles and tools always stay.
    ///  - Starting a job: cleared where it is docked; a job that doesn't begin with a dock and load gets a dock + clear
    ///    in front (the load itself clears first).
    ///  - Failing / ending a job: on the way back a drone without a home connector docks at its last cargo connector to
    ///    clear before it parks; one with a home connector clears on arrival there.
    ///  - Going to rest outside a job (orchestrator / stand-alone drones): a short housekeeping job (dock, clear, rest).
    /// Not for "managed by player" drones: they only clear when docking home at the end of a job.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrClearingCargoAt = MyStringId.GetOrCompute("Clearing its cargo at {0}");
        private static readonly MyStringId StrCargoLeftAboard = MyStringId.GetOrCompute("Cargo: no room at {0}, {1:F2} m3 left aboard");
        private static readonly MyStringId StrCargoNoPort = MyStringId.GetOrCompute("Cargo aboard ({0:F2} m3), no connector known to clear it");
        #endregion

        #region Timing
        private const int CLEAR_RETRY_TICKS = 60 * 60;       // a clear / housekeeping trip at most this often (a full drone is the exception:
                                                              // it can't bid on jobs that carry anything, so it doesn't linger)
        private const int CLEAR_EMPTY_RECHECK_TICKS = 10 * 60; // empty: looked at again after this long
        private const int CLEAR_NO_ROOM_TICKS = 5 * 60 * 60; // no room at that port last time: no trip there again for this long (docked: retried)
        #endregion
        private const double CLEAR_MIN_VOLUME = 0.0005;       // m3: less than this aboard counts as empty

        private long lastCargoPortId;        // the connector it last loaded / unloaded / cleared at (runtime)
        private int clearRetryFrame = int.MinValue / 2;
        private bool clearTripInserted;      // this job already got its dock + clear before going to rest
        private long clearNoRoomPort;        // the port that last had no room for what is aboard
        private int clearNoRoomFrame = int.MinValue / 2;

        private static readonly string INGOT_TYPE = "MyObjectBuilder_Ingot";

        // Every tool of the construct that holds items: connectors, collectors, welders, grinders, drills (refreshed per
        // clear / unload: a welder aborting with job components on it, a grinder's parts, a drill's ore and stone)
        private readonly List<IMyTerminalBlock> toolStores = new List<IMyTerminalBlock>();

        private void RefreshToolStores()
        {
            toolStores.Clear();
            RefreshDroneGrids();
            for (int g = 0; g < droneGrids.Count; g++)
            {
                var grid = droneGrids[g];
                foreach (var b in grid.GetFatBlocks<IMyShipConnector>()) toolStores.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyCollector>()) toolStores.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyShipWelder>()) toolStores.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyShipGrinder>()) toolStores.Add(b);
                foreach (var b in grid.GetFatBlocks<IMyShipDrill>()) toolStores.Add(b);
            }
        }

        // The drone's inventories that hold work items: cargo, then the tool stores (RefreshToolStores first)
        private IMyInventory ClearSource(int i)
        {
            var b = i < cargoContainers.Count ? (IMyTerminalBlock)cargoContainers[i] : toolStores[i - cargoContainers.Count];
            return b != null && !b.Closed && b.InventoryCount > 0 ? b.GetInventory(0) : null;
        }

        private string ClearSourceName(int i)
        {
            var b = i < cargoContainers.Count ? (IMyTerminalBlock)cargoContainers[i] : toolStores[i - cargoContainers.Count];
            return b != null ? b.CustomName : "?";
        }

        private int ClearSourceCount { get { return cargoContainers.Count + toolStores.Count; } }

        private bool HasGasGenerator()
        {
            RefreshDroneGrids();
            for (int g = 0; g < droneGrids.Count; g++)
                foreach (var gen in droneGrids[g].GetFatBlocks<IMyGasGenerator>())
                    if (gen.IsFunctional) return true;
            return false;
        }

        private static bool IsClearable(VRage.Game.ModAPI.Ingame.MyItemType type, bool keepIce)
        {
            string t = type.TypeId;
            if (t == COMPONENT_TYPE || t == INGOT_TYPE) return true;
            return t == ORE_TYPE && !(keepIce && type.SubtypeId == "Ice");
        }

        /// <summary>m3 of components, ores and ingots aboard (cargo and tools).</summary>
        private double ClearableVolume()
        {
            RefreshToolStores();
            bool keepIce = HasGasGenerator();
            double v = 0;
            for (int s = 0; s < ClearSourceCount; s++)
            {
                var inv = ClearSource(s);
                if (inv == null) continue;
                itemBuffer.Clear();
                inv.GetItems(itemBuffer);
                for (int i = 0; i < itemBuffer.Count; i++)
                {
                    if (!IsClearable(itemBuffer[i].Type, keepIce)) continue;
                    v += (double)itemBuffer[i].Amount * UnitVolume(itemBuffer[i].Type);
                }
            }
            itemBuffer.Clear();
            return v;
        }

        private static readonly Dictionary<string, double> unitVolumes = new Dictionary<string, double>();

        // m3 per item (physical item definition, cached)
        private static double UnitVolume(VRage.Game.ModAPI.Ingame.MyItemType type)
        {
            string key = type.TypeId + "/" + type.SubtypeId;
            double v;
            if (unitVolumes.TryGetValue(key, out v)) return v;
            VRage.Game.MyDefinitionId id;
            var def = VRage.Game.MyDefinitionId.TryParse(key, out id) ? Sandbox.Definitions.MyDefinitionManager.Static.GetPhysicalItemDefinition(id) : null;
            v = def != null ? def.Volume : 0.001;
            unitVolumes[key] = v;
            return v;
        }

        private bool HasCargoToClear()
        {
            return ClearableVolume() > CLEAR_MIN_VOLUME;
        }

        // Everything clearable into the network built by the caller (BuildHomeNetwork); false when something stayed aboard
        private bool ClearCargo()
        {
            if (homeNetwork.cargoContainers.Count == 0) return false;
            RefreshToolStores();
            bool keepIce = HasGasGenerator();
            bool left = false;
            for (int s = 0; s < ClearSourceCount; s++)
            {
                var src = ClearSource(s);
                if (src == null) continue;
                itemBuffer.Clear();
                src.GetItems(itemBuffer);
                for (int i = itemBuffer.Count - 1; i >= 0; i--)
                {
                    var type = itemBuffer[i].Type;
                    if (!IsClearable(type, keepIce)) continue;
                    // Container after container until the whole stack is gone (a transfer can be partial)
                    for (int d = 0; d < homeNetwork.cargoContainers.Count && AmountAt(src, i, type) > 0; d++)
                    {
                        var dst = homeNetwork.cargoContainers[d].GetInventory(0);
                        if (dst != null) src.TransferItemTo(dst, i, null, true, null, true);
                    }
                    if (AmountAt(src, i, type) > 0)
                    {
                        left = true;
                        if (Log.DebugEnabled)
                            Log.Debug("Drone {0}: clear: {1} {2} stayed in {3} ({4} container(s) on the network{5})", Entity.EntityId,
                                      (double)AmountAt(src, i, type), type.SubtypeId, ClearSourceName(s), homeNetwork.cargoContainers.Count,
                                      NetworkFull() ? ", all full" : ", not conveyor-connected or no room for it");
                    }
                }
            }
            itemBuffer.Clear();
            return !left;
        }

        // Every container of the network built by BuildHomeNetwork at least 99% full
        private bool NetworkFull()
        {
            for (int d = 0; d < homeNetwork.cargoContainers.Count; d++)
            {
                var inv = homeNetwork.cargoContainers[d].GetInventory(0);
                if (inv != null && (double)inv.CurrentVolume < (double)inv.MaxVolume * 0.99) return false;
            }
            return true;
        }

        /// <summary>Docked at 'at': everything clearable into its network. True when nothing is left aboard.</summary>
        private bool ClearCargoAt(IMyShipConnector at)
        {
            if (at == null) return false;
            BuildHomeNetwork(at);
            if (homeNetwork.cargoContainers.Count == 0) return !HasCargoToClear();
            double before = ClearableVolume();
            ClearCargo();
            double after = ClearableVolume();
            if (after < before) lastCargoPortId = at.EntityId;
            Log.Debug("Drone {0}: cargo cleared at {1}: {2:F3} -> {3:F3} m3", Entity.EntityId, at.CustomName, before, after);
            if (after > CLEAR_MIN_VOLUME)
            {
                Report(StrCargoLeftAboard, at.CustomName, after);
                clearNoRoomPort = at.EntityId;
                clearNoRoomFrame = MyAPIGateway.Session.GameplayFrameCounter;
                return false;
            }
            return true;
        }

        // Docked anywhere with cargo aboard: cleared there
        private void ClearIfDocked()
        {
            if (!HasCargoToClear()) return;
            var docked = DockedConnector();
            if (docked != null) ClearCargoAt(docked);
        }

        /// <summary>Where to clear: the home connector, else the connector it last moved cargo at, else the nearest free
        /// connector (allowed owner, fits a drone connector) whose network has cargo room, within CLEAR_PORT_RADIUS
        /// (a grinder without a home connector never docks during its jobs; a game load forgets the last port).</summary>
        private IMyShipConnector ClearPort()
        {
            var home = GetHomeConnector();
            if (home != null && !home.Closed && home.IsFunctional) return home;
            if (lastCargoPortId != 0)
            {
                var c = AnchorDirectory.Resolve(block, lastCargoPortId) as IMyShipConnector;
                if (c != null && !c.Closed && c.IsFunctional) return c;
            }
            var found = NearestClearPort();
            if (found != null)
            {
                lastCargoPortId = found.EntityId;
                Log.Debug("Drone {0}: no cargo connector known: nearest with room is {1} ({2:F0} m)", Entity.EntityId, found.CustomName,
                          Vector3D.Distance(found.GetPosition(), block.GetPosition()));
            }
            return found;
        }

        private const double CLEAR_PORT_RADIUS = 2000;   // m around the drone: connectors looked at when it knows none
        private readonly List<IMyCubeGrid> clearPortGroup = new List<IMyCubeGrid>();
        private readonly Dictionary<IMyCubeGrid, bool> clearPortRoom = new Dictionary<IMyCubeGrid, bool>();

        private IMyShipConnector NearestClearPort()
        {
            var faces = DockFaces();
            var smallFaces = DockFaces(true);
            if (faces.Count == 0 && smallFaces.Count == 0) return null;
            CaptureFlightState();
            RefreshDroneGrids();
            Vector3D g = flightState.InGravity ? flightState.Gravity : Vector3D.Zero;
            Vector3D at = block.GetPosition();
            var sphere = new BoundingSphereD(at, CLEAR_PORT_RADIUS);
            clearPortRoom.Clear();
            IMyShipConnector best = null;
            double bestD = double.MaxValue;
            foreach (var ent in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere))
            {
                var grid = ent as IMyCubeGrid;
                if (grid == null || grid.Physics == null || grid.MarkedForClose || droneGrids.Contains(grid) || HasDroneController(grid)) continue;
                foreach (var c in grid.GetFatBlocks<IMyShipConnector>())
                {
                    double d = Vector3D.DistanceSquared(c.GetPosition(), at);
                    if (d >= bestD || !DockableFree(c, faces, smallFaces, g) || !NetworkHasRoom(grid)) continue;
                    bestD = d;
                    best = c;
                }
            }
            clearPortRoom.Clear();
            return best;
        }

        // A cargo container with room on the grid's logical (conveyor) group; per search, cached per grid
        private bool NetworkHasRoom(IMyCubeGrid grid)
        {
            bool room;
            if (clearPortRoom.TryGetValue(grid, out room)) return room;
            clearPortGroup.Clear();
            grid.GetGridGroup(GridLinkTypeEnum.Logical).GetGrids(clearPortGroup);
            room = false;
            for (int i = 0; i < clearPortGroup.Count && !room; i++)
            {
                var gr = clearPortGroup[i];
                if (droneGrids.Contains(gr) || gr.Physics == null || HasDroneController(gr)) continue;
                foreach (var b in gr.GetFatBlocks<IMyCargoContainer>())
                {
                    var inv = b.IsFunctional ? b.GetInventory(0) : null;
                    if (inv != null && (double)inv.CurrentVolume < (double)inv.MaxVolume * 0.99) { room = true; break; }
                }
            }
            for (int i = 0; i < clearPortGroup.Count; i++) clearPortRoom[clearPortGroup[i]] = room;
            clearPortGroup.Clear();
            return room;
        }

        private static Orchestrator.Task DockTaskFor(IMyShipConnector c)
        {
            Vector3D at = c.GetPosition();
            return Orchestrator.Task.DockAt(c.EntityId, at, at + c.WorldMatrix.Forward * Construction.ConstructionComputer.APPROACH_DISTANCE);
        }

        private static Orchestrator.Task ClearCargoTask()
        {
            return new Orchestrator.Task { Type = Orchestrator.TaskType.ClearCargo };
        }

        private bool ClearsCargo { get { return settings != null && settings.OperationMode != OperationMode.ManagedByPlayer; } }

        /// <summary>A new job (not a resumed one): starts empty. Cleared where it is docked, else a dock + clear in front
        /// (a job that begins with dock + load needs none: the load clears first). Designator jobs don't wait for it.</summary>
        private void PrepareCargoForJob(Orchestrator.Job job)
        {
            clearTripInserted = false;
            if (!ClearsCargo || job == null || job.Tasks == null || Orchestrator.JobTypeExtensions.IsDesignator(job.JobType) || !HasCargoToClear()) return;
            var docked = DockedConnector();
            if (docked != null && ClearCargoAt(docked)) return;
            if (job.Tasks.Count > 1 && job.Tasks[0].Type == Orchestrator.TaskType.DockAt && job.Tasks[1].Type == Orchestrator.TaskType.LoadInventory) return;
            var port = ClearPort();
            if (port == null || (docked != null && docked.EntityId == port.EntityId)) return;   // nowhere else / no room here
            job.Tasks.Insert(0, ClearCargoTask());
            job.Tasks.Insert(0, DockTaskFor(port));
            Log.Debug("Drone {0}: job {1}: cargo aboard, clearing it at {2} first", Entity.EntityId, job.JobId, port.CustomName);
        }

        /// <summary>
        /// ReturnHome of a job, cargo aboard, no home connector (it would park with it): a dock + clear at its last cargo
        /// connector in front, once per job. True when inserted (the task runner starts the dock next).
        /// </summary>
        private bool InsertClearTripBeforeRest()
        {
            if (clearTripInserted || !ClearsCargo || HasHomeConnector || jobSource == JobSource.Housekeeping || !HasCargoToClear()) return false;
            clearTripInserted = true;
            var docked = DockedConnector();
            if (docked != null && ClearCargoAt(docked)) return false;
            var port = ClearPort();
            if (port == null || (docked != null && docked.EntityId == port.EntityId)) return false;
            currentJob.Tasks.Insert(taskIndex, ClearCargoTask());
            currentJob.Tasks.Insert(taskIndex, DockTaskFor(port));
            taskStarted = false;
            Log.Debug("Drone {0}: cargo aboard before going to rest: clearing it at {1} first", Entity.EntityId, port.CustomName);
            return true;
        }

        /// <summary>
        /// Outside a job, going to rest with cargo aboard: cleared where it is docked, else a housekeeping job (dock at the
        /// clear port, clear, rest). At most every CLEAR_RETRY_TICKS (no room, no port). True when the job started.
        /// </summary>
        private bool TryStartHousekeeping()
        {
            if (!IsServer || currentJob != null || cPhase != ConstructionPhase.Idle || !ClearsCargo || !initialized || Grounded) return false;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now < clearRetryFrame) return false;
            double volume = ClearableVolume();
            if (volume <= CLEAR_MIN_VOLUME) { clearRetryFrame = now + CLEAR_EMPTY_RECHECK_TICKS; return false; }   // (idle check: cheap enough at this rate)
            clearRetryFrame = now + CLEAR_RETRY_TICKS;
            var docked = DockedConnector();
            if (docked != null && ClearCargoAt(docked)) return false;
            var port = ClearPort();
            if (port == null)
            {
                Report(StrCargoNoPort, volume);
                return false;
            }
            if (docked != null && docked.EntityId == port.EntityId) return false;   // here already: no room
            if (port.EntityId == clearNoRoomPort && now - clearNoRoomFrame < CLEAR_NO_ROOM_TICKS) return false;   // full there a moment ago
            var job = new Orchestrator.Job { JobType = Orchestrator.JobType.None };
            job.Tasks.Add(DockTaskFor(port));
            job.Tasks.Add(ClearCargoTask());
            job.Tasks.Add(Orchestrator.Task.ReturnHome());
            Report(StrClearingCargoAt, port.CustomName);
            StartJob(job, JobSource.Housekeeping);
            return true;
        }

        /// <summary>
        /// UpdateBeforeSimulation100, AI on: a drone with nothing to do (no job, no flight, no bid out) and cargo aboard
        /// clears it (docked: where it is; else a housekeeping trip), at most every CLEAR_RETRY_TICKS. Cargo left from an
        /// earlier job (or a game load) otherwise stays until the next job, and with no free room the drone can't win one.
        /// </summary>
        private void ClearWhenIdle()
        {
            if (!IsServer || currentJob != null || cPhase != ConstructionPhase.Idle || activeFlightOrder != null || dockingHomeId != 0
                || preflightStage != 0 || pendingBidAuctionId != 0 || combatPhase != CombatPhase.Idle) return;
            TryStartHousekeeping();
        }

        // The ClearCargo task: docked, everything clearable into that network (what finds no room stays aboard)
        private TaskStatus StepClearCargo()
        {
            var at = DockedConnector();
            if (at == null) return Fail(Orchestrator.TaskFailure.TargetNotFound, TaskStatus.ToReturnHome);
            ClearCargoAt(at);
            return TaskStatus.Done;
        }
    }
}
