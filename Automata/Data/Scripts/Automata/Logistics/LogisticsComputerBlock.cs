using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

using Automata.Drone;
using Automata.Inventory;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Logistics
{
    /// <summary>
    /// Logistics Computer (a programmable block subtype): maps the conveyor networks of its grid (and what rotors /
    /// pistons attach), keeps the player's item Min / Max (terminal list) and talks to orchestrators over the virtual
    /// network (server, every 100 ticks):
    ///  - Request: an item below its Min -> a DeliverMissingInventory job announcement (the shortfall).
    ///  - Push: an item above its Max -> a CollectInventorySurplus job announcement (the excess).
    ///  - Provide for logistics: bids its items on other computers' DeliverMissingInventory auctions (never below its
    ///    own Min) and its room on CollectInventorySurplus auctions (never above its own Max).
    ///  - Provide for construction: bids its components on Weld auctions and its room on Grind auctions.
    /// A grid may hold several conveyor networks that aren't connected: each has its own items, room and ports
    /// (standard connectors; small connectors are ignored, with a warning). A bid comes from the network that serves
    /// the auction best and names one of its free ports. Min / Max apply to the main network (the one with the most
    /// cargo room and a port), where requested items are delivered and surplus is collected from.
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, "AutomataLargeLogisticsComputer")]
    public partial class LogisticsComputerBlock : SettingsBlockLogic, INicknamed
    {
        #region UI strings
        private static readonly MyStringId StrHeaderTitle = MyStringId.GetOrCompute("LC {0}");
        private static readonly MyStringId StrHeaderNetworks = MyStringId.GetOrCompute("Conveyor Networks: {0}");
        private static readonly MyStringId StrHeaderItems = MyStringId.GetOrCompute("Available items");
        private static readonly MyStringId StrHeaderPage = MyStringId.GetOrCompute("(page {0}/{1})");
        private static readonly MyStringId StrLogBid = MyStringId.GetOrCompute("Bid: {0}, port: {1}");
        private static readonly MyStringId StrLogPickup = MyStringId.GetOrCompute("Pickup: {0}, Drone: {1}, port: {2}");
        private static readonly MyStringId StrLogIncoming = MyStringId.GetOrCompute("Incoming: {0}, Drone: {1}, port: {2}");
        private static readonly MyStringId StrLogPayload = MyStringId.GetOrCompute("Payload:");
        private static readonly MyStringId StrKindConstruction = MyStringId.GetOrCompute("Construction");
        private static readonly MyStringId StrKindRequest = MyStringId.GetOrCompute("Request");
        private static readonly MyStringId StrKindPush = MyStringId.GetOrCompute("Push");
        private static readonly MyStringId StrKindPull = MyStringId.GetOrCompute("Pull");
        private static readonly MyStringId StrKindSupply = MyStringId.GetOrCompute("Supply");
        private static readonly MyStringId StrKindSurplus = MyStringId.GetOrCompute("Surplus");
        private static readonly MyStringId StrError = MyStringId.GetOrCompute("Error: {0}");
        private static readonly MyStringId StrNetworkName = MyStringId.GetOrCompute("Network {0}");
        private static readonly MyStringId StrDroneOutcome = MyStringId.GetOrCompute("drone {0}");
        private static readonly MyStringId StrOnHoldReason = MyStringId.GetOrCompute("on hold ({0})");
        private static readonly MyStringId StrInfoStatus = MyStringId.GetOrCompute("Logistics: {0}");
        private static readonly MyStringId StrInfoModes = MyStringId.GetOrCompute("Modes:");
        private static readonly MyStringId StrModeProvideLogistics = MyStringId.GetOrCompute("provide (logistics)");
        private static readonly MyStringId StrModeProvideConstruction = MyStringId.GetOrCompute("provide (construction)");
        private static readonly MyStringId StrModeRequest = MyStringId.GetOrCompute("request");
        private static readonly MyStringId StrModePush = MyStringId.GetOrCompute("push");
        private static readonly MyStringId StrInfoNetworks = MyStringId.GetOrCompute("Conveyor networks: {0}");
        private static readonly MyStringId StrInfoMin = MyStringId.GetOrCompute("Min {0}: {1} (has {2})");
        private static readonly MyStringId StrInfoMax = MyStringId.GetOrCompute("Max {0}: {1} (has {2})");
        private static readonly MyStringId StrJobRequest = MyStringId.GetOrCompute("Request");
        private static readonly MyStringId StrJobPush = MyStringId.GetOrCompute("Push");
        private static readonly MyStringId StrInfoPromised = MyStringId.GetOrCompute("Promised in bids: {0}");
        private static readonly MyStringId StrInfoLastBid = MyStringId.GetOrCompute("Last bid: {0}");
        private static readonly MyStringId StrInfoSmallConnectors = MyStringId.GetOrCompute("Small connector(s) '{0}': only combat drones, ore scouts and ore haulers dock there");
        private static readonly MyStringId StrInfoWarning = MyStringId.GetOrCompute("Warning: {0}");
        private static readonly MyStringId StrInfoJob = MyStringId.GetOrCompute("{0} job {1}: {2} ({3})");
        private static readonly MyStringId StrPending = MyStringId.GetOrCompute("pending");
        private static readonly MyStringId StrStarting = MyStringId.GetOrCompute("Starting");
        private static readonly MyStringId StrWaitingForTheMod = MyStringId.GetOrCompute("Waiting for the mod");
        private static readonly MyStringId StrLoading = MyStringId.GetOrCompute("Loading");
        private static readonly MyStringId StrOffTerminal = MyStringId.GetOrCompute("Off (terminal)");
        private static readonly MyStringId StrNotWorkingPowerDamage = MyStringId.GetOrCompute("Not working (power / damage)");
        private static readonly MyStringId StrNoAntennaFunctionalOnBroadcasting = MyStringId.GetOrCompute("No antenna (functional, on, broadcasting)");
        private static readonly MyStringId StrNoConveyorNetworkWithA = MyStringId.GetOrCompute("No conveyor network with a working standard connector");
        private static readonly MyStringId StrActive = MyStringId.GetOrCompute("Active");
        private static readonly MyStringId StrUnknownItemOrNotWhole = MyStringId.GetOrCompute("'{0}': unknown item or not whole numbers >= 0: ignored");
        private static readonly MyStringId StrMinMaxWouldLoopRequested = MyStringId.GetOrCompute("{0}: Min {1} > Max {2} would loop (requested, then pushed away): ignored");
        private static readonly MyStringId StrMinSetButRequestMode = MyStringId.GetOrCompute("Min set, but Request mode is off: nothing is requested");
        private static readonly MyStringId StrMaxSetButPushMode = MyStringId.GetOrCompute("Max set, but Push mode is off: nothing is pushed");
        private static readonly MyStringId StrNoFreeConnector = MyStringId.GetOrCompute("no free connector");
        private static readonly MyStringId StrAnnounced = MyStringId.GetOrCompute("announced");
        private static readonly MyStringId StrNoOrchestratorInRange = MyStringId.GetOrCompute("no orchestrator in range");
        private static readonly MyStringId StrSent = MyStringId.GetOrCompute("sent");
        private static readonly MyStringId StrAssigned = MyStringId.GetOrCompute("assigned");
        private static readonly MyStringId StrNothing = MyStringId.GetOrCompute("nothing");
        private static readonly MyStringId StrNone = MyStringId.GetOrCompute("none");
        private static readonly MyStringId StrContainersPortSAssemblerS = MyStringId.GetOrCompute("{0}: {1} containers, {2} port(s), {3} assembler(s), {4} refinery(ies), {5} O2/H2 generator(s), {6} O2 / {7} H2 tank(s), {8:F1} / {9:F1} m3{10}");
        private static readonly MyStringId StrMain = MyStringId.GetOrCompute(" (main)");
        private static readonly MyStringId StrRestored = MyStringId.GetOrCompute("restored");
        private static readonly MyStringId StrRetryRequested = MyStringId.GetOrCompute("retry requested");
        #endregion

        #region Timing
        private const int RESCAN_TICKS = 600;            // components / networks again (block changes mark it dirty)
        private const int DIRTY_RESCAN_TICKS = 300;      // ...at most this often while blocks keep changing (building on the grid)
        private const int INVENTORY_TICKS = 300;         // network inventories
        private const int QUOTA_CHECK_TICKS = 600;       // Min / Max against the inventory
        private const int RETRY_TICKS = 1200;            // an announcement that reached no orchestrator: tried again
        private const int RESPONSE_TIMEOUT_TICKS = 60 * 60;    // announced, no award / failure since: announced again (was 5 min)
        private const int ASSIGNED_TTL_TICKS = 5 * 3600;       // awarded and no drone report by then (drone lost / dead): announced again
        private const int JOB_STALE_TICKS = 10 * 3600;   // an open job nobody fulfilled: announced afresh
        private const int DELIVERY_SETTLE_TICKS = 3600;  // items arrived for a job and nothing changed since: it's done
        private const int COMMIT_TTL_TICKS = 5 * 3600;   // items / room promised in a bid stay promised this long
        private const int WON_COMMIT_TTL_TICKS = 20 * 3600;   // ...and once won, until the drone reports (this long at most)
        private const int AUCTION_MEMORY_TICKS = 10 * 3600;
        #endregion

        private LogisticsComputerSettings settings = new LogisticsComputerSettings();
        private long entityId;
        private MessageQueue messaging;
        private bool ready;
        private readonly StringBuilder info = new StringBuilder();

        // Grid
        private readonly List<IMyCubeGrid> grids = new List<IMyCubeGrid>();
        private readonly List<IMyCubeBlock> inventoryBlocks = new List<IMyCubeBlock>();
        private readonly List<ConveyorNetwork> networks = new List<ConveyorNetwork>();
        private readonly List<long> networkKeys = new List<long>();   // stable per network: its lowest block entity id
        private readonly HashSet<long> ownConnectors = new HashSet<long>();
        private readonly List<string> smallConnectors = new List<string>();
        private string reportedSmallConnectors;
        private ConveyorNetwork primary;
        private IMyRadioAntenna antenna;
        private AntennaReg antennaReg;
        private bool dirty = true;
        private int lastScan = int.MinValue / 2, lastInventory = int.MinValue / 2, lastQuotaCheck = int.MinValue / 2;
        private int networkCounter;

        // Min / Max (settings.Quotas, parsed)
        private bool quotasDirty = true;
        private readonly Dictionary<MyStringHash, int> minimums = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
        private readonly Dictionary<MyStringHash, int> maximums = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
        private readonly HashSet<MyStringHash> quotaKeys = new HashSet<MyStringHash>(MyStringHash.Comparer);
        private readonly List<string> quotaWarnings = new List<string>();

        // Jobs this computer announced (one request and one push open at a time)
        private sealed class OpenJob
        {
            public uint JobId;
            public Orchestrator.JobType Type;
            public DiscreteInventory Items;
            public int Opened, LastAnnounced, LastChange, LastTotal;
            public int Assigned;     // frame of the award, 0 = not yet
            public int HeldUntil;    // its auction failed: not announced before this frame, 0 = not held
            public int DronesOut;    // awarded: drones that have not reported their trips yet
            public bool Finished;    // every drone reported, or one gave up: closed at the next check (need recomputed)
            public bool Announced;   // the last announcement reached an orchestrator
            public Message<JobAnnouncement> Sent;   // the announcement in flight: told whether it was taken once its life is up
            public string LastResult;
        }
        private OpenJob requestJob, pushJob;
        private int jobCounter, messageCounter, bidCounter;

        // Bids: what was promised to which auction, from which network (so two auctions don't get the same plates)
        private sealed class Commitment { public DiscreteInventory Items; public double Volume; public int Frame; public bool Supply; public long Network; public bool Won; public uint JobId; public HashSet<long> Drones; }
        private readonly BlockLog log = new BlockLog();
        private readonly HashSet<uint> loggedAwards = new HashSet<uint>();
        private readonly Dictionary<uint, Commitment> commitments = new Dictionary<uint, Commitment>();
        private readonly Dictionary<uint, int> seenAuctions = new Dictionary<uint, int>();
        private readonly HashSet<uint> seenAwards = new HashSet<uint>();
        private readonly List<uint> cleanup = new List<uint>();
        private readonly List<Message<Auction>> auctionInbox = new List<Message<Auction>>();
        private readonly List<Message<AuctionWinnerAnnouncement>> awardInbox = new List<Message<AuctionWinnerAnnouncement>>();
        private readonly List<KVPair> kv = new List<KVPair>();
        private string status = Loc.T(StrStarting);
        private string lastBid;
        private readonly List<Message<JobAnnouncementFailed>> failureInbox = new List<Message<JobAnnouncementFailed>>();
        private LogisticsComputerState pendingState;

        #region Block
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            block = (IMyTerminalBlock)Entity;
            entityId = Entity.EntityId;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            if (block.CubeGrid == null || block.CubeGrid.Physics == null) return;   // projections / ghosts
            LogisticsComputerTerminalControls.DoOnce(ModContext);
            LoadFromStorage();
            block.AppendingCustomInfo += AppendInfo;
            SubscribeBase();   // the LC's grid until the first rescan (Triggers.cs)
            ready = true;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
            Log.Info("LC {0}: '{1}' on '{2}': {3} Min/Max entries, modes {4}", entityId, block.CustomName, block.CubeGrid.DisplayName,
                     settings.Quotas.Count, settings.WorkMode);
        }

        public override void UpdateAfterSimulation100()
        {
            base.UpdateAfterSimulation100();
            if (!ready) return;
            try
            {
                if (IsServer) { Step(); double tl = Util.PerfStats.Start(); UpdateLcd(); Util.PerfStats.Stop(Util.PerfStats.LcdBuild, tl); }
                else if (quotasDirty) ParseQuotas();   // clients: the info panel
                block.RefreshCustomInfo();
            }
            catch (Exception ex)
            {
                Log.Error("LC {0}: update error: {1}", entityId, ex);
                status = Loc.F(StrError, ex.Message);
            }
        }

        public override void Close()
        {
            try
            {
                if (block != null) block.AppendingCustomInfo -= AppendInfo;
                UnsubscribeBase();
                if (messaging != null) { messaging.ReaderClosed(entityId); }
                for (int i = 0; i < networks.Count; i++) networks[i].Detach();   // inventory content subscriptions
                Log.Debug("LC {0}: closed", entityId);
            }
            catch (Exception ex)
            {
                Log.Error("LC {0}: close error: {1}", entityId, ex);
            }
            base.Close();
        }

        protected override string SettingsXml()
        {
            return MyAPIGateway.Utilities.SerializeToXML(settings);
        }

        protected override string SaveText()
        {
            var data = new LogisticsComputerSaveData { Settings = settings };
            if (IsServer) data.State = ready && pendingState == null ? CaptureState() : pendingState;
            data.EntityId = Entity.EntityId;
            return BlockSave.ToText(data);
        }

        protected override bool LoadSaveText(string text)
        {
            var data = BlockSave.FromText<LogisticsComputerSaveData>(text, Entity.EntityId);
            if (data == null) return false;
            settings = data.Settings ?? new LogisticsComputerSettings();
            if (settings.Quotas == null) settings.Quotas = new List<string>();
            seenRetryRequest = settings.RetryRequest;   // a press saved before is not a press now
            if (IsServer && data.State != null && BlockSave.OwnState(data.EntityId, Entity.EntityId, "LogisticsComputer")) pendingState = data.State;
            return true;
        }

        protected override bool LoadSettingsXml(string xml)
        {
            var loaded = MyAPIGateway.Utilities.SerializeFromXML<LogisticsComputerSettings>(xml);
            if (loaded == null) return false;
            if (loaded.Quotas == null) loaded.Quotas = new List<string>();
            settings = loaded;
            return true;
        }

        protected override void OnSettingsChanged()
        {
            if (IsServer && settings.RetryRequest != seenRetryRequest)
            {
                seenRetryRequest = settings.RetryRequest;
                RetryHeld();
            }
            quotasDirty = true;
            lastQuotaCheck = int.MinValue / 2;
        }

        private bool Has(OperationMode mode) { return (settings.WorkMode & mode) != 0; }
        #endregion

        #region Loop
        private void Step()
        {
            if (block.CubeGrid == null || block.CubeGrid.Physics == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (messaging == null) messaging = AutomataSession.GetMessageQueue();
            if (messaging == null) { status = Loc.T(StrWaitingForTheMod); return; }
            if (pendingState != null)
            {
                if (!BlockSave.CanRestore) { status = Loc.T(StrLoading); return; }
                RestoreState(pendingState);
                pendingState = null;
            }
            if (!settings.Enabled || !block.IsWorking)
            {
                status = !settings.Enabled ? Loc.T(StrOffTerminal) : Loc.T(StrNotWorkingPowerDamage);
                return;
            }
            if (quotasDirty) ParseQuotas();
            if ((dirty && now - lastScan >= DIRTY_RESCAN_TICKS) || now - lastScan >= Math.Max(RESCAN_TICKS, settings.ConveyorNetworkUpdateIntervalSeconds * 60))
            {
                Rescan();
                lastScan = now;
                dirty = false;
                dirtyWhy = null;
                lastInventory = int.MinValue / 2;
            }
            if (!EnsureAntenna()) { status = Loc.T(StrNoAntennaFunctionalOnBroadcasting); return; }
            if (now - lastInventory >= INVENTORY_TICKS)
            {
                lastInventory = now;
                for (int i = 0; i < networks.Count; i++)
                {
                    networks[i].UpdateInventories();
                    networks[i].UpdateIOLocationData();
                }
                var before = primary;
                primary = PickPrimary();
                if (primary != before)
                    Log.Debug("LC {0}: main network: {1}", entityId, primary != null ? DescribeNetwork(networks.IndexOf(primary)) : "none (no free standard connector)");
            }
            if (primary == null) { status = Loc.T(StrNoConveyorNetworkWithA); return; }
            status = Loc.T(StrActive);
            ExpireCommitments(now);
            if (quotaKeys.Count > 0 && now - lastQuotaCheck >= QUOTA_CHECK_TICKS)
            {
                lastQuotaCheck = now;
                CheckQuotas(now);
            }
            else if (quotaKeys.Count == 0) { requestJob = null; pushJob = null; }
            ReadFailures(now);
            ReadJobReports(now);
            ReadKeepalives(now);
            droneWatch.Check(now, block, settings.UseBroadcastController, "LC {0}", entityId, null);
            RepeatAnnouncement(requestJob, now);
            RepeatAnnouncement(pushJob, now);
            ReadAuctions(now);
            ReadAwards(now);
        }
        #endregion

        #region Grid
        private void Rescan()
        {
            grids.Clear();
            inventoryBlocks.Clear();
            ownConnectors.Clear();
            smallConnectors.Clear();
            sorters.Clear();
            antenna = null;
            // The base: this grid and the large grids mechanically joined to it (rotors, pistons, hinges). Small grids in the
            // group (drones sitting on landing gear, small sub-grids) are not part of it: their connectors and cargo would show
            // up as ports and networks of the base.
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, grids);
            for (int g = grids.Count - 1; g >= 0; g--)
                if (grids[g] != block.CubeGrid && grids[g].GridSizeEnum != VRage.Game.MyCubeSize.Large) grids.RemoveAt(g);
            for (int g = 0; g < grids.Count; g++)
            {
                foreach (var fat in grids[g].GetFatBlocks<IMyTerminalBlock>())
                {
                    var sorter = fat as IMyConveyorSorter;
                    if (sorter != null) sorters.Add(sorter);   // on / off changes the networks (Triggers.cs), functional or not
                    if (fat == null || !fat.IsFunctional) continue;
                    var radio = fat as IMyRadioAntenna;
                    if (radio != null)
                    {
                        if (radio.Enabled && radio.EnableBroadcasting && (antenna == null || AntennaUtil.Range(radio) > AntennaUtil.Range(antenna))) antenna = radio;
                        continue;
                    }
                    var connector = fat as IMyShipConnector;
                    if (connector != null)
                    {
                        // Small connectors too (ports for drones with small connectors: TODO/14)
                        if (!ConveyorNetwork.IsStandardConnector(connector)) smallConnectors.Add(connector.CustomName);
                        ownConnectors.Add(fat.EntityId);
                    }
                    if (fat is IMyCargoContainer || connector != null || fat is IMyCollector || fat is IMyAssembler || fat is IMyRefinery
                        || fat is IMyGasGenerator || fat is IMyGasTank || fat is IMyReactor)
                        inventoryBlocks.Add(fat);
                }
            }
            string small = smallConnectors.Count > 0 ? string.Join("', '", smallConnectors) : null;
            if (small != reportedSmallConnectors)
            {
                reportedSmallConnectors = small;
                if (small != null)
                    Log.Debug("LC {0}: small connector(s) '{1}': ports for combat drones, ore scouts and ore haulers only", entityId, small);
            }
            MapNetworks();
            SubscribeBase();
            if (Log.DebugEnabled)
            {
                var names = new List<string>();
                for (int g = 0; g < grids.Count; g++) names.Add(grids[g].DisplayName + (grids[g].GridSizeEnum == VRage.Game.MyCubeSize.Large ? " (large)" : " (small)"));
                Log.Debug("LC {0}: rescan ({6}): {1} grid(s) [{2}], {3} inventory blocks, {4} network(s), antenna {5}", entityId, grids.Count,
                          string.Join(", ", names), inventoryBlocks.Count, networks.Count, antenna != null ? "'" + antenna.CustomName + "'" : "none",
                          dirtyWhy ?? (dirty ? "changes" : "periodic"));
            }
        }

        // Inventory blocks grouped by conveyor connectivity, connectors first. One representative per network (its first
        // block - a connector when it has one): a block joins the first network whose representative reaches it
        // (IMyInventory.IsConnectedTo), else it starts a new one. IsConnectedTo already searches the whole conveyor graph
        // from the representative, so testing it against every member (as before) found nothing more: N x networks
        // calls instead of ~N^2.
        private void MapNetworks()
        {
            for (int i = 0; i < networks.Count; i++) networks[i].Detach();
            networks.Clear();
            networkKeys.Clear();
            primary = null;
            var ordered = new List<IMyCubeBlock>(inventoryBlocks.Count);
            for (int i = 0; i < inventoryBlocks.Count; i++) if (inventoryBlocks[i] is IMyShipConnector) ordered.Add(inventoryBlocks[i]);
            for (int i = 0; i < inventoryBlocks.Count; i++) if (!(inventoryBlocks[i] is IMyShipConnector)) ordered.Add(inventoryBlocks[i]);
            var reps = new List<IMyInventory>();
            int calls = 0;
            for (int b = 0; b < ordered.Count; b++)
            {
                var blk = ordered[b];
                var inv = blk.GetInventory(0);
                int found = -1;
                if (inv != null)
                    for (int n = 0; n < reps.Count && found < 0; n++)
                    {
                        if (reps[n] == null) continue;
                        calls++;
                        if (reps[n].IsConnectedTo(inv)) found = n;
                    }
                if (found < 0)
                {
                    var network = new ConveyorNetwork();
                    network.ConveyorNetworkId = IdGenerator.GenerateId(ref networkCounter, entityId);
                    network.Nickname = Loc.F(StrNetworkName, networks.Count + 1);
                    networks.Add(network);
                    networkKeys.Add(long.MaxValue);
                    reps.Add(inv);
                    found = networks.Count - 1;
                }
                ConveyorNetwork.CategorizeBlock(networks[found], blk);
                if (blk.EntityId < networkKeys[found]) networkKeys[found] = blk.EntityId;
            }
            for (int i = 0; i < networks.Count; i++) networks[i].Attach();
            if (Log.DebugEnabled)
            {
                Log.Debug("LC {0}: {1} inventory blocks mapped with {2} connectivity checks", entityId, ordered.Count, calls);
                for (int i = 0; i < networks.Count; i++) Log.Debug("LC {0}: {1}", entityId, DescribeNetwork(i));
            }
        }

        // The main network: has a free port; the most cargo room
        private ConveyorNetwork PickPrimary()
        {
            ConveyorNetwork best = null;
            double bestVolume = -1;
            for (int i = 0; i < networks.Count; i++)
            {
                var n = networks[i];
                if (FreeDock(n) == null) continue;
                double v = (double)n.MaxVolume;
                if (v > bestVolume) { bestVolume = v; best = n; }
            }
            return best;
        }

        // Where a drone docks: a free connector of the network; else any working one (a drone docked there - often at
        // its home - loads / unloads right where it is; another ship there makes the dock fail and the job is retried)
        private static IOLocationData FreeDock(ConveyorNetwork n)
        {
            if (n == null) return null;
            // Standard before small (most drones dock at those), free before occupied
            IMyShipConnector pick = null;
            for (int pass = 0; pass < 4 && pick == null; pass++)
                for (int i = 0; i < n.connectors.Count && pick == null; i++)
                {
                    var c = n.connectors[i];
                    if (c == null || c.Closed || !c.IsWorking) continue;
                    if ((pass % 2 == 0) == c.IsConnected) continue;
                    if ((pass < 2) != ConveyorNetwork.IsStandardConnector(c)) continue;
                    pick = c;
                }
            if (pick == null) return null;
            return new IOLocationData
            {
                PositionData = Vector3DData.FromVector3D(pick.GetPosition()),
                OrientationData = QuaternionDData.FromQuaternionD(QuaternionD.CreateFromRotationMatrix(pick.WorldMatrix)),
                BlockType = IOBlockType.ShipConnector,
                EntityId = pick.EntityId,
            };
        }

        private bool EnsureAntenna()
        {
            return AntennaReg.Ensure(messaging, ref antennaReg, antenna);
        }
        #endregion

        #region Min / Max
        private void ParseQuotas()
        {
            if (AutomataSession.componentLookup.Count == 0) return;   // item definitions not loaded yet
            quotasDirty = false;
            minimums.Clear();
            maximums.Clear();
            quotaKeys.Clear();
            quotaWarnings.Clear();
            for (int i = 0; i < settings.Quotas.Count; i++)
            {
                QuotaEntry e;
                if (!QuotaEntry.TryParse(settings.Quotas[i], out e))
                {
                    quotaWarnings.Add(Loc.F(StrUnknownItemOrNotWhole, settings.Quotas[i]));
                    continue;
                }
                quotaKeys.Add(e.Key);
                // Min above Max: it would be requested and pushed away in a loop
                if (e.Min > 0 && e.Max > 0 && e.Min > e.Max)
                {
                    quotaWarnings.Add(Loc.F(StrMinMaxWouldLoopRequested, e.Key.String, e.Min, e.Max));
                    continue;
                }
                if (e.Min > 0) minimums[e.Key] = e.Min;
                if (e.Max > 0) maximums[e.Key] = e.Max;
            }
            if (minimums.Count > 0 && !Has(OperationMode.Request)) quotaWarnings.Add(Loc.T(StrMinSetButRequestMode));
            if (maximums.Count > 0 && !Has(OperationMode.Push)) quotaWarnings.Add(Loc.T(StrMaxSetButPushMode));
            Log.Debug("LC {0}: Min/Max: {1} min, {2} max, {3} warning(s)", entityId, minimums.Count, maximums.Count, quotaWarnings.Count);
            for (int i = 0; i < quotaWarnings.Count; i++) Log.Debug("LC {0}: Min/Max warning: {1}", entityId, quotaWarnings[i]);
        }
        #endregion

        #region Request / Push
        private void CheckQuotas(int now)
        {
            // Quotas count everything the network holds (ice in its generators, uranium in its reactors too); supply
            // bids only what a drone may take (GetCurrentInventory)
            var have = primary.GetHeldInventory();
            if (Has(OperationMode.Request) && minimums.Count > 0)
            {
                var missing = new DiscreteInventory();
                foreach (var m in minimums)
                {
                    int count = have.GetItemCount(m.Key);
                    if (count < m.Value) missing.AddItem(m.Key, m.Value - count);
                }
                requestJob = UpdateJob(requestJob, missing, have, Orchestrator.JobType.DeliverMissingInventory, now);
            }
            else requestJob = null;
            if (Has(OperationMode.Push) && maximums.Count > 0)
            {
                var excess = new DiscreteInventory();
                foreach (var m in maximums)
                {
                    int count = have.GetItemCount(m.Key);
                    if (count > m.Value) excess.AddItem(m.Key, count - m.Value);
                }
                pushJob = UpdateJob(pushJob, excess, have, Orchestrator.JobType.CollectInventorySurplus, now);
            }
            else pushJob = null;
        }

        // One open job per kind: kept while it isn't done; done when nothing is needed any more, when items moved for
        // it and then nothing changed for a while (a drone delivered / collected what it could), or when stale
        private OpenJob UpdateJob(OpenJob job, DiscreteInventory needed, DiscreteInventory have, Orchestrator.JobType type, int now)
        {
            if (job != null)
            {
                int total = Total(have, job.Items);
                if (total != job.LastTotal)
                {
                    Log.Debug("LC {0}: job {1} ({2}): items moved, {3} -> {4} on hand", entityId, job.JobId, job.Type, job.LastTotal, total);
                    job.LastTotal = total;
                    job.LastChange = now;
                }
                // Awarded: open until the drones report (or the award times out: RepeatAnnouncement); else: until items
                // moved and settled (a player / another system) or it went stale
                bool done = needed.IsEmpty() || job.Finished
                         || (job.Assigned == 0 && job.LastChange > job.Opened && now - job.LastChange > DELIVERY_SETTLE_TICKS)
                         || (job.Assigned == 0 && now - job.Opened > JOB_STALE_TICKS);
                if (!done) return job;
                Log.Debug("LC {0}: job {1} ({2}) closed: {3}", entityId, job.JobId, job.Type,
                          needed.IsEmpty() ? "satisfied" : job.Finished ? "drones reported, the rest is asked again"
                          : job.LastChange > job.Opened ? "items moved, the rest is asked again" : "nobody took it");
                job = null;
            }
            if (needed.IsEmpty()) return null;
            job = new OpenJob
            {
                JobId = IdGenerator.GenerateId(ref jobCounter, entityId),
                Type = type,
                Items = needed,
                Opened = now,
                LastChange = now,
                LastTotal = Total(have, needed),
                LastAnnounced = int.MinValue / 2,
            };
            Log.Debug("LC {0}: job {1} opened ({2}): {3}", entityId, job.JobId, type, DescribeItems(needed));
            return job;
        }

        private static int Total(DiscreteInventory have, DiscreteInventory items)
        {
            int total = 0;
            foreach (var item in items.GetAllItems()) total += have.GetItemCount(item.Key);
            return total;
        }

        // Each job once; again if that reached no orchestrator (soon), if nothing came back (after a long while), or if
        // an award long ago didn't get it done. Not while on hold (its auction got no bids).
        private void RepeatAnnouncement(OpenJob job, int now)
        {
            if (job == null) return;
            if (job.HeldUntil != 0)
            {
                if (now < job.HeldUntil) return;
                job.HeldUntil = 0;
                job.LastAnnounced = int.MinValue / 2;
                Log.Debug("LC {0}: job {1} off hold", entityId, job.JobId);
            }
            if (job.Assigned != 0)
            {
                if (now - job.Assigned <= ASSIGNED_TTL_TICKS) return;
                Log.Debug("LC {0}: job {1} awarded long ago and not done: announced again", entityId, job.JobId);
                job.Assigned = 0;
                job.LastAnnounced = int.MinValue / 2;
            }
            if (job.Sent != null)
            {
                if (job.Sent.Delivered) { job.Announced = true; job.Sent = null; job.LastResult = Loc.T(StrAnnounced); }
                else if (now - job.LastAnnounced >= messaging.LifetimeTicks) { job.Announced = false; job.Sent = null; job.LastResult = Loc.T(StrNoOrchestratorInRange); }
                else return;   // still in the network
            }
            if (now - job.LastAnnounced < (job.Announced ? RESPONSE_TIMEOUT_TICKS : RETRY_TICKS)) return;
            job.LastAnnounced = now;
            var dock = FreeDock(primary);
            if (dock == null) { job.LastResult = Loc.T(StrNoFreeConnector); Log.Debug("LC {0}: job {1}: no free connector to announce", entityId, job.JobId); return; }
            Vector3D at = dock.PositionData.ToVector3D();
            float interference;
            float g = MyAPIGateway.Physics.CalculateNaturalGravityAt(at, out interference).Length();
            var msg = new Message<JobAnnouncement>
            {
                Payload = new JobAnnouncement
                {
                    JobId = job.JobId,
                    Type = job.Type,
                    EntityType = AutomataEntityType.LogisticsComputerBlock,
                    CreatedTime = DateTime.UtcNow,
                    ComponentsInventory = job.Items,
                    PositionData = dock.PositionData,
                    OrientationData = dock.OrientationData,
                    NaturalGravity = g,
                    IsInSpace = g < JobAnnouncement.SPACE_GRAVITY,
                    IsInAtmosphere = g >= JobAnnouncement.SPACE_GRAVITY,
                    IsStaticGrid = block.CubeGrid.IsStatic,
                    RequesterEntityId = entityId,
                    Dock = dock,
                    DockSite = DockFit.SiteOf(primary, at),
                    Tasks = new List<Orchestrator.Task>(),
                },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = job.Type == Orchestrator.JobType.DeliverMissingInventory ? Channel.LOGISTIC_REQUEST : Channel.LOGISTIC_PUSH,
            };
            var error = messaging.BroadcastMessage(antennaReg, msg, false);
            job.Announced = false;
            job.Sent = error == ErrorCode.None ? msg : null;
            if (error != ErrorCode.None) job.LastResult = error.ToString();
            Log.Debug("LC {0}: job {1} ({2}) announced at connector {3}: {4} ({5})", entityId, job.JobId, job.Type, dock.EntityId,
                      DescribeItems(job.Items), error);
        }
        #endregion

        #region Auctions
        private void ReadAuctions(int now)
        {
            if (!Has(OperationMode.ProvideForLogistics) && !Has(OperationMode.ProvideForConstruction)) return;
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.LogisticsComputer, antennaReg, Channel.ORCHESTRATOR_AUCTION_START, auctionInbox, 20, true, PayloadType.Auction);
            for (int i = 0; i < auctionInbox.Count; i++)
            {
                var a = auctionInbox[i].Payload;
                // (a promise restored from the save counts as seen: re-reading would bid again minus its own promise)
                if (a == null || a.ExpirationTime < DateTime.UtcNow || seenAuctions.ContainsKey(a.AuctionId)) continue;
                Commitment held;
                if (commitments.TryGetValue(a.AuctionId, out held))
                {
                    if (held.JobId == a.JobId) continue;   // bid already
                    // Same auction id, another job: the orchestrator's ids started over (a reload without saving) and
                    // this promise is from the old one
                    commitments.Remove(a.AuctionId);
                    Log.Debug("LC {0}: auction {1} is a new one (job {2}, the promise was for job {3}): old promise released", entityId, a.AuctionId, a.JobId, held.JobId);
                }
                seenAuctions[a.AuctionId] = now;
                if (a.RequesterEntityId == entityId) continue;   // its own job
                if (!AnchorDirectory.IsAllowed(block.OwnerId, auctionInbox[i].SenderOwnerId))
                {
                    Log.Debug("LC {0}: auction {1} ignored: orchestrator of another owner / faction", entityId, a.AuctionId);
                    continue;
                }
                var items = a.ComponentsInventory;
                if (items == null || items.IsEmpty()) continue;
                Log.Debug("LC {0}: auction {1} ({2}, job {3}): {4}", entityId, a.AuctionId, a.JobType, a.JobId, DescribeItems(items));
                switch (a.JobType)
                {
                    case Orchestrator.JobType.Weld:
                        if (Has(OperationMode.ProvideForConstruction)) BidSupply(a, items, now);
                        break;
                    case Orchestrator.JobType.DeliverMissingInventory:
                        if (Has(OperationMode.ProvideForLogistics)) BidSupply(a, items, now);
                        break;
                    case Orchestrator.JobType.Grind:
                        if (Has(OperationMode.ProvideForConstruction)) BidRoom(a, items, now);
                        break;
                    case Orchestrator.JobType.CollectInventorySurplus:
                        if (Has(OperationMode.ProvideForLogistics)) BidRoom(a, items, now);
                        break;
                    case Orchestrator.JobType.MineOre:
                        if (Has(OperationMode.ProvideForLogistics)) BidRoom(a, items, now, true);
                        break;
                }
            }
            auctionInbox.Clear();
            cleanup.Clear();
            foreach (var s in seenAuctions) if (now - s.Value > AUCTION_MEMORY_TICKS) cleanup.Add(s.Key);
            for (int i = 0; i < cleanup.Count; i++) { seenAuctions.Remove(cleanup[i]); seenAwards.Remove(cleanup[i]); }
        }

        // From the network giving the most: what it has, less (main network) its Min, less what other bids promised
        private void BidSupply(Auction a, DiscreteInventory wanted, int now)
        {
            int best = -1, bestCount = 0;
            DiscreteInventory bestSupply = null;
            IOLocationData bestDock = null;
            bool bestAll = false;
            for (int n = 0; n < networks.Count; n++)
            {
                var dock = FreeDock(networks[n]);
                if (dock == null) continue;
                bool all;
                var supply = SupplyFrom(n, wanted, out all);
                int count = supply.GetTotalItemCount();
                Log.Debug("LC {0}: auction {1}: network {2} can give {3}", entityId, a.AuctionId, n + 1, count > 0 ? DescribeItems(supply) : "nothing");
                if (count <= bestCount) continue;
                best = n; bestCount = count; bestSupply = supply; bestDock = dock; bestAll = all;
            }
            if (best < 0) return;
            commitments[a.AuctionId] = new Commitment { Items = bestSupply, Frame = now, Supply = true, Network = networkKeys[best], JobId = a.JobId };
            SendBid(a, bestSupply, bestAll ? InventoryFulfillment.SatisfyFully : InventoryFulfillment.SatisfyPartial, 0, networks[best], bestDock);
        }

        private DiscreteInventory SupplyFrom(int n, DiscreteInventory wanted, out bool all)
        {
            var network = networks[n];
            var have = network.GetCurrentInventory();   // what a drone may take
            var held = network.GetHeldInventory();      // all of it (the minimum quota counts this)
            var supply = new DiscreteInventory();
            all = true;
            kv.Clear();
            wanted.GetAllItems(kv);
            for (int i = 0; i < kv.Count; i++)
            {
                int keep = 0;
                if (network == primary) minimums.TryGetValue(kv[i].Key, out keep);
                // Above the minimum (counted on everything held) and within what may be taken
                int spare = Math.Min(have.GetItemCount(kv[i].Key), held.GetItemCount(kv[i].Key) - keep) - Promised(kv[i].Key, networkKeys[n]);
                int give = Math.Min(kv[i].Value, Math.Max(0, spare));
                if (give < kv[i].Value) all = false;
                if (give > 0) supply.AddItem(kv[i].Key, give);
            }
            kv.Clear();
            return supply;
        }

        // To the network taking the most: free cargo room (less room other bids promised), the main one never above its Max
        private void BidRoom(Auction a, DiscreteInventory incoming, int now, bool ore = false)
        {
            // Ore: only networks with a working refinery that takes it, and not while what it refines into is at its Max
            if (ore && !WantsOre(incoming)) return;
            bool needRefinery = ore && !settings.AcceptOreWithoutRefinery;
            // Ice: to a network with a working O2/H2 generator when there is one, else anywhere with room
            bool ice = ore && IsIceOnly(incoming);
            bool genSomewhere = false;
            if (ice)
                for (int n = 0; n < networks.Count && !genSomewhere; n++) genSomewhere = networks[n].HasGasGenerators() && FreeDock(networks[n]) != null;
            int best = -1;
            double bestVolume = 0;
            DiscreteInventory bestAccept = null;
            IOLocationData bestDock = null;
            bool bestAll = false;
            int bestTier = 0;
            for (int n = 0; n < networks.Count; n++)
            {
                var dock = FreeDock(networks[n]);
                if (dock == null) continue;
                if (ice && genSomewhere && !networks[n].HasGasGenerators())
                {
                    Log.Debug("LC {0}: auction {1}: network {2} has no O2/H2 generator (another one has)", entityId, a.AuctionId, n + 1);
                    continue;
                }
                if (needRefinery && !CanRefine(networks[n], incoming))
                {
                    Log.Debug("LC {0}: auction {1}: network {2} has no refinery for {3}", entityId, a.AuctionId, n + 1, DescribeItems(incoming));
                    continue;
                }
                bool all;
                var accept = RoomIn(n, incoming, out all);
                double volume = accept != null ? (double)accept.TotalInventoryVolume() : 0;
                Log.Debug("LC {0}: auction {1}: network {2} can take {3}", entityId, a.AuctionId, n + 1, accept != null ? DescribeItems(accept) : "nothing");
                if (accept == null || volume <= 0) continue;
                // Ice with no O2/H2 generator here: the network with the best refinery first (the drone also brings
                // stone / other ores; the orchestrator may send it here for those), then the most room
                int tier = ice && !genSomewhere ? RefineryTierOf(networks[n]) : 0;
                if (best >= 0 && (tier < bestTier || (tier == bestTier && volume <= bestVolume))) continue;
                best = n; bestVolume = volume; bestAccept = accept; bestDock = dock; bestAll = all; bestTier = tier;
            }
            if (best < 0) return;
            commitments[a.AuctionId] = new Commitment { Items = bestAccept, Volume = bestVolume, Frame = now, Network = networkKeys[best], JobId = a.JobId };
            SendBid(a, bestAccept, bestAll ? InventoryFulfillment.AcceptAll : InventoryFulfillment.AcceptPartial, bestVolume, networks[best], bestDock);
        }

        /// <summary>
        /// The best working refinery on a network: 3 prototech, 2 refinery / industrial refinery (and modded ones), 1 basic
        /// (vanilla "Blast Furnace"), 0 none. Subtype names, as the definitions carry no tier.
        /// </summary>
        private static byte RefineryTierOf(ConveyorNetwork network)
        {
            byte best = 0;
            for (int r = 0; r < network.refineries.Count && best < 3; r++)
            {
                var refinery = network.refineries[r];
                if (refinery == null || refinery.Closed || !refinery.IsWorking) continue;
                string sub = refinery.BlockDefinition.SubtypeName ?? "";
                byte tier = sub.IndexOf("Prototech", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)3
                          : sub.IndexOf("Blast Furnace", StringComparison.OrdinalIgnoreCase) >= 0 || sub.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)1
                          : (byte)2;
                if (tier > best) best = tier;
            }
            return best;
        }

        // Refinery input constraints per (refinery subtype, ore): read once
        private static readonly Dictionary<string, bool> refineryAccepts = new Dictionary<string, bool>();

        private const string ICE_KEY = "Ore/Ice";
        private bool IsIceOnly(DiscreteInventory items)
        {
            kv.Clear();
            items.GetAllItems(kv);
            bool only = kv.Count > 0;
            for (int i = 0; i < kv.Count && only; i++) only = kv[i].Key.String == ICE_KEY;
            kv.Clear();
            return only;
        }

        private bool CanRefine(ConveyorNetwork network, DiscreteInventory ores)
        {
            kv.Clear();
            ores.GetAllItems(kv);
            bool ok = kv.Count > 0;
            for (int i = 0; i < kv.Count && ok; i++)
            {
                // Ice is not refined: O2/H2 generators take it from cargo, so any network with room is fine
                bool any = kv[i].Key.String == ICE_KEY;
                for (int r = 0; r < network.refineries.Count && !any; r++)
                {
                    var refinery = network.refineries[r];
                    if (refinery == null || refinery.Closed || !refinery.IsWorking) continue;
                    any = RefineryAccepts(refinery, kv[i].Key);
                }
                ok = any;
            }
            kv.Clear();
            return ok;
        }

        private static bool RefineryAccepts(IMyRefinery refinery, MyStringHash oreKey)
        {
            string key = refinery.BlockDefinition.SubtypeName + "|" + oreKey.String;
            bool accepts;
            if (refineryAccepts.TryGetValue(key, out accepts)) return accepts;
            ComponentData data;
            var def = Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(refinery.BlockDefinition) as Sandbox.Definitions.MyRefineryDefinition;
            accepts = def != null && def.InputInventoryConstraint != null && AutomataSession.componentLookup.TryGetValue(oreKey, out data)
                      && def.InputInventoryConstraint.Check(data.DefinitionId);
            refineryAccepts[key] = accepts;
            return accepts;
        }

        // Max on the ore, or on the ingot it refines into, reached on the main network: no more of it (overflow limit)
        private bool WantsOre(DiscreteInventory ores)
        {
            if (primary == null) return false;
            var have = primary.GetHeldInventory();
            kv.Clear();
            ores.GetAllItems(kv);
            bool wants = true;
            for (int i = 0; i < kv.Count && wants; i++)
            {
                string s = kv[i].Key.String;
                int max;
                if (maximums.TryGetValue(kv[i].Key, out max) && have.GetItemCount(kv[i].Key) >= max) wants = false;
                var ingot = MyStringHash.GetOrCompute("Ingot/" + (s.StartsWith("Ore/", StringComparison.Ordinal) ? s.Substring(4) : s));
                if (wants && maximums.TryGetValue(ingot, out max) && have.GetItemCount(ingot) >= max) wants = false;
            }
            kv.Clear();
            if (!wants) Log.Debug("LC {0}: ore {1} not wanted: Max reached (ore or its ingot)", entityId, DescribeItems(ores));
            return wants;
        }

        private DiscreteInventory RoomIn(int n, DiscreteInventory incoming, out bool all)
        {
            var network = networks[n];
            all = true;
            double room = (double)network.GetRemainingCargoVolume() - PromisedVolume(networkKeys[n]);
            if (room <= 0) { all = false; return null; }
            var have = network.GetHeldInventory();
            var accept = new DiscreteInventory();
            var lookup = AutomataSession.componentLookup;
            kv.Clear();
            incoming.GetAllItems(kv);
            for (int i = 0; i < kv.Count; i++)
            {
                int take = kv[i].Value;
                int max;
                if (network == primary && maximums.TryGetValue(kv[i].Key, out max)) take = Math.Min(take, Math.Max(0, max - have.GetItemCount(kv[i].Key)));
                ComponentData data;
                double unit = lookup.TryGetValue(kv[i].Key, out data) ? data.Volume : 0;
                if (unit > 0) take = (int)Math.Min(take, Math.Floor(room / unit));
                if (take < kv[i].Value) all = false;
                if (take <= 0) continue;
                accept.AddItem(kv[i].Key, take);
                room -= take * unit;
            }
            kv.Clear();
            return accept.IsEmpty() ? null : accept;
        }

        private void SendBid(Auction a, DiscreteInventory items, InventoryFulfillment fulfillment, double volume, ConveyorNetwork network, IOLocationData dock)
        {
            var msg = new Message<Bid>
            {
                Payload = new Bid
                {
                    EntityId = entityId,
                    EntityType = AutomataEntityType.LogisticsComputerBlock,
                    JobId = a.JobId,
                    AuctionId = a.AuctionId,
                    BidId = IdGenerator.GenerateId(ref bidCounter, entityId),
                    CreatedTime = DateTime.UtcNow,
                    InventoryFulfillmentFlags = fulfillment,
                    BidInventory = items,
                    MaxCargoVolume = (MyFixedPoint)Math.Max(volume, (double)network.GetRemainingCargoVolume()),
                    IOLocationData = dock,
                    DockSite = DockFit.SiteOf(network, dock.PositionData.ToVector3D()),
                    RefineryTier = a.JobType == Orchestrator.JobType.MineOre ? RefineryTierOf(network) : (byte)0,
                    GasGenerators = a.JobType == Orchestrator.JobType.MineOre && network.HasGasGenerators(),
                },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.ORCHESTRATOR_AUCTION_BIDS,
            };
            var error = messaging.BroadcastMessage(antennaReg, msg, false);
            lastBid = string.Format("{0}: {1} ({2})", a.JobType, DescribeItems(items), error == ErrorCode.None ? Loc.T(StrSent) : error.ToString());
            var entry = new StringBuilder();
            entry.AppendLoc(StrLogBid, BidKind(a), ConnectorName(dock.EntityId));
            Nicknames.AppendPayload(entry, Loc.T(StrLogPayload), items);
            log.Add(entry.ToString());
            Log.Debug("LC {0}: bid on auction {1} ({2}) from {3}, port {4} ({8}): {5} {6} ({7})", entityId, a.AuctionId, a.JobType,
                      network.Nickname, dock.EntityId, fulfillment, DescribeItems(items), error, DockFit.Describe(msg.Payload.DockSite));
        }

        // Awards: its own job awarded (not announced again for a while); a commitment stays for the auction it won (a
        // drone docks at one of its connectors), else it's released
        private void ReadAwards(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.LogisticsComputer, antennaReg, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT, awardInbox, 20, true,
                                   PayloadType.AuctionWinnerAnnouncement);
            for (int i = 0; i < awardInbox.Count; i++)
            {
                var award = awardInbox[i].Payload;
                if (award == null || !AnchorDirectory.IsAllowed(block.OwnerId, awardInbox[i].SenderOwnerId)) continue;
                LogAward(award);
                if (award.AnnouncerEntityId == entityId)
                {
                    var own = requestJob != null && requestJob.JobId == award.JobId ? requestJob : pushJob != null && pushJob.JobId == award.JobId ? pushJob : null;
                    if (own != null && own.Assigned == 0)
                    {
                        own.Assigned = now;
                        own.HeldUntil = 0;
                        own.DronesOut = award.TaskAssignments != null ? award.TaskAssignments.Count : 0;
                        own.LastResult = Loc.T(StrAssigned);
                        Log.Debug("LC {0}: job {1} ({2}) awarded by orchestrator {3}", entityId, own.JobId, own.Type, awardInbox[i].SenderId);
                    }
                }
                if (!commitments.ContainsKey(award.AuctionId) || !seenAwards.Add(award.AuctionId)) continue;
                // Several drones may share one award (and several LCs): the drones docking here hold the promise
                var drones = new HashSet<long>();
                if (award.TaskAssignments != null)
                    foreach (var kvp in award.TaskAssignments)
                        for (int t = 0; kvp.Value != null && t < kvp.Value.Count; t++)
                            if (kvp.Value[t].Type == Orchestrator.TaskType.DockAt && ownConnectors.Contains(kvp.Value[t].TargetEntityId)) { drones.Add(kvp.Key); break; }
                bool won = drones.Count > 0;
                if (!won)
                {
                    commitments.Remove(award.AuctionId);
                    Log.Debug("LC {0}: lost auction {1} (job {2}): promise released", entityId, award.AuctionId, award.JobId);
                }
                else
                {
                    // Won: the items / room stay reserved until the drone reports (or a long time passes)
                    var c = commitments[award.AuctionId];
                    c.Won = true;
                    c.Drones = drones;
                    c.Frame = now;
                    Log.Debug("LC {0}: won auction {1} (job {2}): {3} reserved", entityId, award.AuctionId, award.JobId, c.Supply ? "items" : "room");
                }
            }
            awardInbox.Clear();
        }

        // The drones' outcomes for its own jobs: all done, or one gave up -> the job closes and the need is asked again
        private readonly List<Message<JobStatusReport>> reportInbox = new List<Message<JobStatusReport>>();

        private void ReadJobReports(int now)
        {
            for (int c = 0; c < 2; c++)
            {
                messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.LogisticsComputer, antennaReg, c == 0 ? Channel.TASKS_COMPLETED : Channel.TASKS_ABORTED, reportInbox, 20, true,
                                       PayloadType.JobStatusReport);
                for (int i = 0; i < reportInbox.Count; i++)
                {
                    var r = reportInbox[i].Payload;
                    if (r == null || r.DroneEntityId != reportInbox[i].SenderId || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                    if (r.AnnouncerEntityId == entityId) droneWatch.Done(r.DroneEntityId);
                    // A job it supplies / takes in for: the drone is done with it, the reservation ends
                    Commitment held;
                    if (commitments.TryGetValue(r.AuctionId, out held) && held.Won
                        && (held.Drones == null || held.Drones.Remove(r.DroneEntityId)))
                    {
                        if (held.Drones == null || held.Drones.Count == 0)   // the last drone docking here (null: restored save)
                        {
                            commitments.Remove(r.AuctionId);
                            Log.Debug("LC {0}: auction {1}: drone {2} {3}, reservation released", entityId, r.AuctionId, r.DroneEntityId, r.Outcome);
                        }
                        else
                        {
                            held.Frame = now;
                            Log.Debug("LC {0}: auction {1}: drone {2} {3}, {4} drone(s) still hold the reservation", entityId, r.AuctionId,
                                      r.DroneEntityId, r.Outcome, held.Drones.Count);
                        }
                    }
                    if (r.AnnouncerEntityId != entityId) continue;
                    var job = requestJob != null && requestJob.JobId == r.JobId ? requestJob : pushJob != null && pushJob.JobId == r.JobId ? pushJob : null;
                    if (job == null) continue;
                    if (job.Assigned == 0) { job.Assigned = now; job.DronesOut = Math.Max(job.DronesOut, 1); }   // an award never heard here
                    job.DronesOut--;
                    if (job.DronesOut <= 0)   // the last drone of the award (several share a big delivery)
                    {
                        job.Finished = true;
                        lastQuotaCheck = int.MinValue / 2;   // closed / re-opened at once
                    }
                    job.LastResult = Loc.F(StrDroneOutcome, r.Outcome);
                    Log.Debug("LC {0}: job {1} ({2}): drone {3} {4} ({5}); {6}", entityId, job.JobId, job.Type, r.DroneEntityId, r.Outcome, r.Reason,
                              job.Finished ? "closing" : job.DronesOut + " drone(s) still out");
                }
                reportInbox.Clear();
            }
        }

        // Drones still working on an awarded job (long multi-trip deliveries): the award's expiry starts again
        private readonly Util.DroneWatch droneWatch = new Util.DroneWatch();

        private void ReadKeepalives(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.LogisticsComputer, antennaReg, Channel.JOB_KEEPALIVE, reportInbox, 50, true, PayloadType.JobStatusReport);
            for (int i = 0; i < reportInbox.Count; i++)
            {
                var r = reportInbox[i].Payload;
                if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId
                    || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                droneWatch.Heard(r.DroneEntityId, r.JobId, now);
                var job = requestJob != null && requestJob.JobId == r.JobId ? requestJob : pushJob != null && pushJob.JobId == r.JobId ? pushJob : null;
                if (job == null) continue;
                if (job.Assigned == 0) job.DronesOut = Math.Max(job.DronesOut, 1);
                job.Assigned = now;
            }
            reportInbox.Clear();
        }

        // An orchestrator got no bid for its job: on hold for the time it says
        private void ReadFailures(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.LogisticsComputer, antennaReg, Channel.JOB_ANNOUNCEMENT_FAILED, failureInbox, 20, true, PayloadType.JobAnnouncementFailed);
            for (int i = 0; i < failureInbox.Count; i++)
            {
                var f = failureInbox[i].Payload;
                if (f == null || !AnchorDirectory.IsAllowed(block.OwnerId, failureInbox[i].SenderOwnerId)) continue;
                // An auction it bid on was dropped (no / not enough bids, cancelled, expired): its promise ends now
                Commitment held;
                if (commitments.TryGetValue(f.AuctionId, out held) && !held.Won)
                {
                    commitments.Remove(f.AuctionId);
                    Log.Debug("LC {0}: auction {1} dropped ({2}): promise released", entityId, f.AuctionId, f.Reason);
                }
                if (f.AnnouncerEntityId != entityId) continue;
                var job = requestJob != null && requestJob.JobId == f.JobId ? requestJob : pushJob != null && pushJob.JobId == f.JobId ? pushJob : null;
                if (job == null || job.Assigned != 0 || (job.HeldUntil != 0 && job.HeldUntil > now)) continue;
                job.HeldUntil = now + Math.Max(10, f.HoldSeconds) * 60;
                job.LastResult = Loc.F(StrOnHoldReason, f.Reason);
                Log.Debug("LC {0}: job {1} ({2}) on hold for {3} s: orchestrator {4}: {5}", entityId, job.JobId, job.Type, f.HoldSeconds,
                          f.OrchestratorEntityId, f.Reason);
            }
            failureInbox.Clear();
        }

        private int Promised(MyStringHash item, long network)
        {
            int n = 0;
            foreach (var c in commitments.Values) if (c.Supply && c.Network == network) n += c.Items.GetItemCount(item);
            return n;
        }

        private double PromisedVolume(long network)
        {
            double v = 0;
            foreach (var c in commitments.Values) if (!c.Supply && c.Network == network) v += c.Volume;
            return v;
        }

        private void ExpireCommitments(int now)
        {
            if (commitments.Count == 0) return;
            cleanup.Clear();
            foreach (var c in commitments) if (now - c.Value.Frame > (c.Value.Won ? WON_COMMIT_TTL_TICKS : COMMIT_TTL_TICKS)) cleanup.Add(c.Key);
            for (int i = 0; i < cleanup.Count; i++)
            {
                commitments.Remove(cleanup[i]);
                Log.Debug("LC {0}: promise for auction {1} expired", entityId, cleanup[i]);
            }
        }
        #endregion

        #region Info
        private static string DescribeItems(DiscreteInventory items)
        {
            if (items == null || items.IsEmpty()) return Loc.T(StrNothing);
            var sb = new StringBuilder();
            foreach (var item in items.GetAllItems())
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(item.Value).Append(' ').Append(Inventory.ItemKeys.Name(item.Key));
            }
            return sb.ToString();
        }

        private string DescribeNetwork(int i)
        {
            if (i < 0 || i >= networks.Count) return Loc.T(StrNone);
            var n = networks[i];
            int ports = 0;
            for (int c = 0; c < n.connectors.Count; c++) if (n.connectors[c] != null && n.connectors[c].IsWorking) ports++;
            return Loc.F(StrContainersPortSAssemblerS, n.Nickname,
                                 n.cargoContainers.Count, ports, n.assemblers.Count, n.refineries.Count, n.gasGenerators.Count, n.GasTankCount(true), n.GasTankCount(false),
                                 (double)n.CurrentVolume, (double)n.MaxVolume, n == primary ? Loc.T(StrMain) : "");
        }

        /// <summary>Terminal info panel.</summary>
        private void AppendInfo(IMyTerminalBlock b, StringBuilder sb)
        {
            if (!ready) return;
            info.Clear();
            info.AppendLoc(StrInfoStatus, status).Append('\n');
            info.AppendLoc(StrInfoModes);
            if (Has(OperationMode.ProvideForLogistics)) info.Append(' ').AppendLoc(StrModeProvideLogistics);
            if (Has(OperationMode.ProvideForConstruction)) info.Append(' ').AppendLoc(StrModeProvideConstruction);
            if (Has(OperationMode.Request)) info.Append(' ').AppendLoc(StrModeRequest);
            if (Has(OperationMode.Push)) info.Append(' ').AppendLoc(StrModePush);
            info.Append('\n');
            info.AppendLoc(StrInfoNetworks, networks.Count).Append('\n');
            for (int i = 0; i < networks.Count; i++) info.Append(" ").Append(DescribeNetwork(i)).Append('\n');
            var have = primary != null ? primary.GetHeldInventory() : null;
            foreach (var m in minimums)
                info.AppendLoc(StrInfoMin, Nicknames.Item(m.Key), m.Value, have != null ? have.GetItemCount(m.Key) : 0).Append('\n');
            foreach (var m in maximums)
                info.AppendLoc(StrInfoMax, Nicknames.Item(m.Key), m.Value, have != null ? have.GetItemCount(m.Key) : 0).Append('\n');
            AppendJob(info, StrJobRequest, requestJob);
            AppendJob(info, StrJobPush, pushJob);
            if (commitments.Count > 0) info.AppendLoc(StrInfoPromised, commitments.Count).Append('\n');
            if (lastBid != null) info.AppendLoc(StrInfoLastBid, lastBid).Append('\n');
            if (reportedSmallConnectors != null)
                info.AppendLoc(StrInfoSmallConnectors, reportedSmallConnectors).Append('\n');
            for (int i = 0; i < quotaWarnings.Count; i++) info.AppendLoc(StrInfoWarning, quotaWarnings[i]).Append('\n');
            BuildHeader();
            info.Append('\n');
            for (int i = 0; i < header.Count; i++) info.Append(header[i]).Append('\n');
            info.Append(new string('-', 40)).Append('\n');
            log.AppendTo(info);
            sb.Append(info);
        }

        private static void AppendJob(StringBuilder sb, MyStringId label, OpenJob job)
        {
            if (job == null) return;
            sb.AppendLoc(StrInfoJob, Loc.T(label), job.JobId, DescribeItems(job.Items), job.LastResult ?? Loc.T(StrPending)).Append('\n');
        }
        #endregion

        #region Save / load
        private LogisticsComputerState CaptureState()
        {
            var s = new LogisticsComputerState
            {
                JobCounter = jobCounter,
                MessageCounter = messageCounter,
                BidCounter = bidCounter,
                Request = CaptureJob(requestJob),
                Push = CaptureJob(pushJob),
            };
            foreach (var c in commitments)
                s.Commitments.Add(new CommitmentState { AuctionId = c.Key, Items = c.Value.Items, Volume = c.Value.Volume, AgeTicks = BlockSave.Age(c.Value.Frame),
                                                         Supply = c.Value.Supply, Network = c.Value.Network, Won = c.Value.Won, JobId = c.Value.JobId });
            Log.Debug("LC {0}: state saved: request {1}, push {2}, {3} promise(s)", entityId,
                      requestJob != null ? requestJob.JobId : 0, pushJob != null ? pushJob.JobId : 0, s.Commitments.Count);
            return s;
        }

        private static LogisticsJobState CaptureJob(OpenJob job)
        {
            if (job == null) return null;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            return new LogisticsJobState
            {
                JobId = job.JobId,
                Type = job.Type,
                Items = job.Items,
                OpenedAgeTicks = BlockSave.Age(job.Opened),
                LastChangeAgeTicks = BlockSave.Age(job.LastChange),
                LastTotal = job.LastTotal,
                AssignedAgeTicks = job.Assigned != 0 ? BlockSave.Age(job.Assigned) : -1,
                HoldTicksLeft = job.HeldUntil > now ? job.HeldUntil - now : 0,
                DronesOut = job.DronesOut,
                Finished = job.Finished,
            };
        }

        private static OpenJob RestoreJob(LogisticsJobState js)
        {
            if (js == null || js.Items == null || js.Items.IsEmpty()) return null;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            return new OpenJob
            {
                JobId = js.JobId,
                Type = js.Type,
                Items = js.Items,
                Opened = BlockSave.FrameFromAge(js.OpenedAgeTicks),
                LastChange = BlockSave.FrameFromAge(js.LastChangeAgeTicks),
                LastTotal = js.LastTotal,
                Assigned = js.AssignedAgeTicks >= 0 ? Math.Max(1, BlockSave.FrameFromAge(js.AssignedAgeTicks)) : 0,
                HeldUntil = js.HoldTicksLeft > 0 ? now + js.HoldTicksLeft : 0,
                DronesOut = js.DronesOut,
                Finished = js.Finished,
                LastAnnounced = int.MinValue / 2,
                LastResult = Loc.T(StrRestored),
            };
        }

        private void RestoreState(LogisticsComputerState s)
        {
            jobCounter = s.JobCounter;
            messageCounter = s.MessageCounter;
            bidCounter = s.BidCounter;
            requestJob = RestoreJob(s.Request);
            pushJob = RestoreJob(s.Push);
            if (s.Commitments != null)
                for (int i = 0; i < s.Commitments.Count; i++)
                {
                    var c = s.Commitments[i];
                    if (c.Items == null) continue;
                    commitments[c.AuctionId] = new Commitment { Items = c.Items, Volume = c.Volume, Frame = BlockSave.FrameFromAge(c.AgeTicks),
                                                                Supply = c.Supply, Network = c.Network, Won = c.Won, JobId = c.JobId };
                }
            Log.Info("LC {0}: state restored: request {1}, push {2}, {3} promise(s)", entityId,
                     requestJob != null ? requestJob.JobId : 0, pushJob != null ? pushJob.JobId : 0, commitments.Count);
        }
        #endregion

        #region Terminal
        public bool Terminal_IsEnabled
        {
            get { return settings.Enabled; }
            set { settings.Enabled = value; SettingsEdited(); }
        }

        private bool GetMode(OperationMode mode) { return (settings.WorkMode & mode) != 0; }

        private void SetMode(OperationMode mode, bool on)
        {
            if (on) settings.WorkMode |= mode; else settings.WorkMode &= ~mode;
            SettingsEdited();
        }

        public bool Terminal_WorkModeProvideForLogistics { get { return GetMode(OperationMode.ProvideForLogistics); } set { SetMode(OperationMode.ProvideForLogistics, value); } }
        public bool Terminal_WorkModeProvideForConstruction { get { return GetMode(OperationMode.ProvideForConstruction); } set { SetMode(OperationMode.ProvideForConstruction, value); } }
        public bool Terminal_WorkModePush { get { return GetMode(OperationMode.Push); } set { SetMode(OperationMode.Push, value); } }
        public bool Terminal_WorkModeRequest { get { return GetMode(OperationMode.Request); } set { SetMode(OperationMode.Request, value); } }
        public bool Terminal_AcceptOreWithoutRefinery
        {
            get { return settings.AcceptOreWithoutRefinery; }
            set { if (settings.AcceptOreWithoutRefinery == value) return; settings.AcceptOreWithoutRefinery = value; SettingsEdited(); }
        }

        // Min / Max lists (UI state is this player's own: selections and the edit box)
        /// <summary>Item selected in the item list (top), or empty.</summary>
        public MyStringHash Terminal_SelectedItem = MyStringHash.NullOrEmpty;
        /// <summary>Entry selected in the Min / Max list (bottom), or -1.</summary>
        public int Terminal_SelectedQuota = -1;
        /// <summary>The edit box: "item:min:max".</summary>
        public readonly StringBuilder Terminal_EditText = new StringBuilder();

        public List<string> Terminal_Quotas { get { return settings.Quotas; } }

        /// <summary>Items the Min / Max list has an entry for.</summary>
        public HashSet<MyStringHash> Terminal_QuotaKeys
        {
            get
            {
                if (quotasDirty) ParseQuotas();
                return quotaKeys;
            }
        }

        public void Terminal_SelectItem(MyStringHash key)
        {
            Terminal_SelectedItem = key;
            Terminal_SelectedQuota = -1;
            Terminal_EditText.Clear().Append(QuotaEntry.Format(key, 0, 0));
        }

        public void Terminal_SelectQuota(int index)
        {
            if (index < 0 || index >= settings.Quotas.Count) return;
            Terminal_SelectedQuota = index;
            Terminal_SelectedItem = MyStringHash.NullOrEmpty;
            Terminal_EditText.Clear().Append(settings.Quotas[index]);
        }

        /// <summary>The edit box (for the item selected at the top) goes into the Min / Max list.</summary>
        public bool Terminal_EditAndAdd()
        {
            if (Terminal_SelectedItem == MyStringHash.NullOrEmpty) return false;
            QuotaEntry e;
            if (!QuotaEntry.TryParse(Terminal_EditText.ToString(), out e)) return false;
            Upsert(e, -1);
            Terminal_SelectedItem = MyStringHash.NullOrEmpty;
            return true;
        }

        /// <summary>The edit box replaces the entry selected in the Min / Max list.</summary>
        public bool Terminal_EditQuota()
        {
            if (Terminal_SelectedQuota < 0 || Terminal_SelectedQuota >= settings.Quotas.Count) return false;
            QuotaEntry e;
            if (!QuotaEntry.TryParse(Terminal_EditText.ToString(), out e)) return false;
            Upsert(e, Terminal_SelectedQuota);
            return true;
        }

        public bool Terminal_RemoveQuota()
        {
            if (Terminal_SelectedQuota < 0 || Terminal_SelectedQuota >= settings.Quotas.Count) return false;
            Log.Debug("LC {0}: Min/Max '{1}' removed", entityId, settings.Quotas[Terminal_SelectedQuota]);
            settings.Quotas.RemoveAt(Terminal_SelectedQuota);
            Terminal_SelectedQuota = -1;
            Terminal_EditText.Clear();
            SettingsEdited();
            return true;
        }

        /// <summary>The edit box as typed: added (or replaces that item's entry) when the item and numbers are valid.</summary>
        public bool Terminal_AddManually()
        {
            QuotaEntry e;
            if (!QuotaEntry.TryParse(Terminal_EditText.ToString(), out e)) return false;
            Upsert(e, -1);
            return true;
        }

        public void Terminal_ClearQuotas()
        {
            settings.Quotas.Clear();
            Terminal_SelectedQuota = -1;
            SettingsEdited();
        }

        // One entry per item: 'at' (an entry being edited) or the item's existing entry is replaced, else appended
        private void Upsert(QuotaEntry e, int at)
        {
            string text = e.ToString();
            int existing = IndexOfQuota(e.Key, at);
            if (at >= 0)
            {
                settings.Quotas[at] = text;
                if (existing >= 0)
                {
                    settings.Quotas.RemoveAt(existing);
                    if (existing < at) at--;
                }
                Terminal_SelectedQuota = at;
            }
            else if (existing >= 0) { settings.Quotas[existing] = text; Terminal_SelectedQuota = existing; }
            else { settings.Quotas.Add(text); Terminal_SelectedQuota = settings.Quotas.Count - 1; }
            Terminal_EditText.Clear().Append(text);
            Log.Debug("LC {0}: Min/Max '{1}' set", entityId, text);
            SettingsEdited();
        }

        private int IndexOfQuota(MyStringHash key, int skip)
        {
            for (int i = 0; i < settings.Quotas.Count; i++)
            {
                if (i == skip) continue;
                QuotaEntry e;
                if (QuotaEntry.TryParse(settings.Quotas[i], out e) && e.Key == key) return i;
            }
            return -1;
        }
        #endregion
            #region Retry held jobs
        private int seenRetryRequest;

        /// <summary>Terminal button: the server announces its held / unanswered jobs again (settings carry the press).</summary>
        public void Terminal_RetryHeld()
        {
            settings.RetryRequest++;
            SettingsEdited();
        }

        // Server: held jobs off hold, unanswered ones due, both announced at the next step
        private void RetryHeld()
        {
            int n = 0;
            foreach (var job in new[] { requestJob, pushJob })
            {
                if (job == null || job.Assigned != 0) continue;
                job.HeldUntil = 0;
                job.Announced = false;
                job.LastAnnounced = int.MinValue / 2;
                job.LastResult = Loc.T(StrRetryRequested);
                n++;
            }
            lastQuotaCheck = int.MinValue / 2;
            Log.Info("LC {0}: retry of held jobs requested: {1} job(s) to announce again", entityId, n);
        }
        #endregion

        #region Tagged LCDs, log
        private readonly BlockLcd lcd = new BlockLcd();
        private readonly List<string> header = new List<string>();
        private readonly List<KVPair> headerItems = new List<KVPair>();
        private const int HEADER_ITEM_ROWS = 5;     // two columns: 10 items a page
        private const int HEADER_LABEL_WIDTH = 17;
        private const int HEADER_ITEM_WIDTH = 21;

        // Server: header and log on the tagged LCDs
        private void UpdateLcd()
        {
            if (!lcd.Wanted(block, settings.LcdTag)) return;   // nobody looks at an LCD: nothing is built
            if (Terminal_LcdStyle.ShowHeader) BuildHeader(); else header.Clear();
            lcd.Publish(Terminal_LcdStyle, header, log);
        }

        /// <summary>The nickname as set (empty: logs show the block's name).</summary>
        public string Nickname { get { return settings.Nickname ?? ""; } }

        public string Terminal_Nickname
        {
            set { value = value ?? ""; if (settings.Nickname == value) return; settings.Nickname = value; SettingsEdited(); }
        }

        public LcdStyle Terminal_LcdStyle { get { if (settings.Lcd == null) settings.Lcd = new LcdStyle(); return settings.Lcd; } }
        public void Terminal_LcdEdited() { SettingsEdited(); }

        // "LC <nickname>", the conveyor networks, every item it holds (all its networks), 10 a page in two columns
        private void BuildHeader()
        {
            header.Clear();
            header.Add(Loc.F(StrHeaderTitle, Nicknames.Of(Entity)));
            header.Add(Loc.F(StrHeaderNetworks, networks.Count));
            var all = new DiscreteInventory();
            for (int i = 0; i < networks.Count; i++)
            {
                var inv = networks[i].GetHeldInventory();
                if (inv != null) all.AddItems(inv);
            }
            headerItems.Clear();
            all.GetAllItems(headerItems);
            for (int i = headerItems.Count - 1; i >= 0; i--) if (headerItems[i].Value <= 0) headerItems.RemoveAt(i);
            SortByName(headerItems);
            int first;
            int pages = BlockLcd.Paginate(settings.Lcd, headerItems.Count, HEADER_ITEM_ROWS * 2, out first);
            int page = first / (HEADER_ITEM_ROWS * 2);
            for (int r = 0; r < HEADER_ITEM_ROWS; r++)
            {
                string label = r == 0 ? Loc.T(StrHeaderItems) : r == 1 ? Loc.F(StrHeaderPage, page + 1, pages) : "";
                string left = ItemText(first + r), right = ItemText(first + HEADER_ITEM_ROWS + r);
                header.Add(BlockLcd.Columns(BlockLcd.Columns(label, left, HEADER_LABEL_WIDTH), right, HEADER_LABEL_WIDTH + HEADER_ITEM_WIDTH));
            }
        }

        private string ItemText(int i)
        {
            if (i < 0 || i >= headerItems.Count) return "";
            string count = headerItems[i].Value.ToString();
            string name = Nicknames.Item(headerItems[i].Key);
            int room = HEADER_ITEM_WIDTH - 3 - count.Length;
            if (name.Length > room) name = name.Substring(0, Math.Max(1, room));
            return name + ": " + count;
        }

        // By display name (insertion sort: System.Comparison isn't whitelisted)
        private static void SortByName(List<KVPair> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                var item = list[i];
                string name = Nicknames.Item(item.Key);
                int j = i - 1;
                while (j >= 0 && string.Compare(Nicknames.Item(list[j].Key), name, StringComparison.OrdinalIgnoreCase) > 0) { list[j + 1] = list[j]; j--; }
                list[j + 1] = item;
            }
        }

        private static string ConnectorName(long id)
        {
            IMyEntity e;
            if (id == 0 || !MyAPIGateway.Entities.TryGetEntityById(id, out e)) return "-";
            var tb = e as IMyTerminalBlock;
            return tb != null ? tb.CustomName : e.DisplayName;
        }

        // A job type as the logistics computer sees it
        private string BidKind(Auction a)
        {
            switch (a.JobType)
            {
                case Orchestrator.JobType.DeliverMissingInventory: return Loc.T(StrKindRequest);
                case Orchestrator.JobType.CollectInventorySurplus: return Loc.T(StrKindPush);
                default: return a.JobType.ToString();
            }
        }

        // The drones that dock at its connectors in an award: what each picks up here / brings here
        private void LogAward(AuctionWinnerAnnouncement award)
        {
            if (award.TaskAssignments == null || !loggedAwards.Add(award.AuctionId)) return;
            if (loggedAwards.Count > 200) loggedAwards.Clear();
            bool own = award.AnnouncerEntityId == entityId;
            foreach (var kvp in award.TaskAssignments)
            {
                var tasks = kvp.Value;
                for (int t = 0; tasks != null && t + 1 < tasks.Count; t++)
                {
                    if (tasks[t].Type != Orchestrator.TaskType.DockAt || !ownConnectors.Contains(tasks[t].TargetEntityId)) continue;
                    var next = tasks[t + 1];
                    bool pickup = next.Type == Orchestrator.TaskType.LoadInventory;
                    if (!pickup && next.Type != Orchestrator.TaskType.UnloadInventory) continue;
                    string kind;
                    switch (award.JobType)
                    {
                        case Orchestrator.JobType.Weld: kind = Loc.T(StrKindConstruction); break;
                        case Orchestrator.JobType.DeliverMissingInventory: kind = own ? Loc.T(StrKindPull) : Loc.T(StrKindSupply); break;
                        case Orchestrator.JobType.CollectInventorySurplus: kind = own ? Loc.T(StrKindPush) : Loc.T(StrKindSurplus); break;
                        default: kind = award.JobType.ToString(); break;
                    }
                    var entry = new StringBuilder();
                    entry.AppendLoc(pickup ? StrLogPickup : StrLogIncoming, kind, Nicknames.Of(kvp.Key), ConnectorName(tasks[t].TargetEntityId));
                    Nicknames.AppendPayload(entry, Loc.T(StrLogPayload), next.Inventory);
                    log.Add(entry.ToString());
                }
            }
        }

        public string Terminal_LcdTag
        {
            get { return settings.LcdTag ?? ""; }
            set { if (settings.LcdTag == value) return; settings.LcdTag = value; SettingsEdited(); }
        }

        public bool Terminal_UseBroadcastController
        {
            get { return settings.UseBroadcastController; }
            set { if (settings.UseBroadcastController == value) return; settings.UseBroadcastController = value; SettingsEdited(); }
        }
        #endregion
    }
}
