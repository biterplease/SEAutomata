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
using VRageMath;

using Automata.Drone;
using Automata.Inventory;
using Automata.Logistics;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;
using VRage.Utils;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Orchestrator block (a programmable block subtype). Turns job announcements into auctions and auctions into
    /// task lists, over the virtual network only: it scans nothing itself. Construction computers announce weld / grind
    /// jobs, logistics computers DeliverMissingInventory (below a Min) and CollectInventorySurplus (above a Max) jobs.
    /// Each new job gets an auction (drones and logistics computers bid); after the bid window, once the bids can carry
    /// the job out, the winners are announced with their task lists (the drone runs them: dock, load, dock, unload,
    /// return home...). A delivery / collection no single drone and supplier can carry is split into trips over several
    /// drones and suppliers. Announcements are queued and auctioned up to MaxConcurrentAuctions (server setting) at a
    /// time. Auctions are broadcast again while open (messages expire); an auction without a single bid within
    /// NoBidTimeoutSeconds is dropped and the announcer told (JOB_ANNOUNCEMENT_FAILED: it holds the job a while); one
    /// whose bids never add up to the job is dropped after 2 minutes. Queue, auctions, bids and unsent messages are
    /// kept through a world save (<see cref="OrchestratorState"/>). Server only, every 60 ticks; on / off with the
    /// block; listens through the grid's largest broadcasting antenna.
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, "AutomataLargeOrchestrator")]
    public partial class OrchestratorBlock : SettingsBlockLogic, INicknamed
    {
        #region UI strings
        private static readonly MyStringId StrError = MyStringId.GetOrCompute("Error: {0}");
        private static readonly MyStringId StrJobTypeNotHandled = MyStringId.GetOrCompute("job type {0} not handled");
        private static readonly MyStringId StrNoBidsWithin = MyStringId.GetOrCompute("no bids within {0}s");
        private static readonly MyStringId StrNoDroneBidWithin = MyStringId.GetOrCompute("no drone bid within {0}s");
        private static readonly MyStringId StrPlanDelivery = MyStringId.GetOrCompute("{0} trip(s), {1} drone(s), {2} supplier(s): {3} to LC {4}{5}");
        private static readonly MyStringId StrPlanCollection = MyStringId.GetOrCompute("{0} trip(s), {1} drone(s), {2} taker(s): {3} from LC {4}{5}");
        private static readonly MyStringId StrPlanLeft = MyStringId.GetOrCompute("; left: {0}");
        private static readonly MyStringId StrNoConnectorForRoom = MyStringId.GetOrCompute("no drone has a connector for a logistics computer with room ({0})");
        private static readonly MyStringId StrNoConnectorForOre = MyStringId.GetOrCompute("no drone has a connector for a logistics computer that takes the ore ({0})");
        private static readonly MyStringId StrNoProfileBid = MyStringId.GetOrCompute("no {0} bid");
        private static readonly MyStringId StrLeftToOrchestrator = MyStringId.GetOrCompute("left to orchestrator {0}");
        private static readonly MyStringId StrDroneOutcome = MyStringId.GetOrCompute("drone {0} {1}");
        private static readonly MyStringId StrDroneOutcomeReason = MyStringId.GetOrCompute("drone {0} {1} ({2})");
        private static readonly MyStringId StrJobDroneOutcome = MyStringId.GetOrCompute("{0}: drone {1} {2}");
        private static readonly MyStringId StrJobDroneOutcomeReason = MyStringId.GetOrCompute("{0}: drone {1} {2}\n  {3}");
        private static readonly MyStringId StrHeaderTitle = MyStringId.GetOrCompute("Orchestrator {0}");
        private static readonly MyStringId StrHeaderActive = MyStringId.GetOrCompute("Active jobs:   {0}");
        private static readonly MyStringId StrHeaderHeldLc = MyStringId.GetOrCompute("Jobs Held: No LC:    {0}");
        private static readonly MyStringId StrHeaderAuctions = MyStringId.GetOrCompute("Open auctions: {0}");
        private static readonly MyStringId StrHeaderHeldDrone = MyStringId.GetOrCompute("           No Drone: {0}");
        private static readonly MyStringId StrHeaderSeen = MyStringId.GetOrCompute("Active Drones: {0}, LC: {1}, CC: {2}, MS: {3}");
        private static readonly MyStringId StrAuctionWonOne = MyStringId.GetOrCompute("Auction won: {0} D: {1}, LC: {2}");
        private static readonly MyStringId StrAuctionWonMany = MyStringId.GetOrCompute("Auction won: {0}");
        private static readonly MyStringId StrAuctionWonColumns = MyStringId.GetOrCompute("Drones");
        private static readonly MyStringId StrAuctionWonColumnsLc = MyStringId.GetOrCompute("LC:");
        private static readonly MyStringId StrInfoStatus = MyStringId.GetOrCompute("Orchestrator: {0}");
        private static readonly MyStringId StrInfoAntenna = MyStringId.GetOrCompute("Antenna: {0} ({1:F0} m)");
        private static readonly MyStringId StrInfoNoAntenna = MyStringId.GetOrCompute("Antenna: none");
        private static readonly MyStringId StrInfoQueued = MyStringId.GetOrCompute("Queued jobs: {0}, open auctions: {1}");
        private static readonly MyStringId StrInfoAssigned = MyStringId.GetOrCompute("Assigned: {0}, failed (no bids): {1}, dropped: {2}");
        private static readonly MyStringId StrInfoReports = MyStringId.GetOrCompute("Drone reports: {0} done, {1} not");
        private static readonly MyStringId StrInfoAuction = MyStringId.GetOrCompute(" - {0}: {1} bid(s)");
        private static readonly MyStringId StrStarting = MyStringId.GetOrCompute("Starting");
        private static readonly MyStringId StrOffNotWorking = MyStringId.GetOrCompute("Off / not working");
        private static readonly MyStringId StrWaitingForTheMod = MyStringId.GetOrCompute("Waiting for the mod");
        private static readonly MyStringId StrLoading = MyStringId.GetOrCompute("Loading");
        private static readonly MyStringId StrNoAntennaFunctionalOnBroadcasting = MyStringId.GetOrCompute("No antenna (functional, on, broadcasting)");
        private static readonly MyStringId StrAuctioning = MyStringId.GetOrCompute("Auctioning");
        private static readonly MyStringId StrListeningForJobs = MyStringId.GetOrCompute("Listening for jobs");
        private static readonly MyStringId StrDormantRunsTheNetwork = MyStringId.GetOrCompute("Dormant: '{0}' runs the network");
        private static readonly MyStringId StrRunningTheNetwork = MyStringId.GetOrCompute("Running the network");
        private static readonly MyStringId StrDormantRunsThisNetwork = MyStringId.GetOrCompute("Dormant: '{0}' runs this network");
        private static readonly MyStringId StrJobNotHandledYet = MyStringId.GetOrCompute("{0}: not handled yet");
        private static readonly MyStringId StrNoItemsOrNoDock = MyStringId.GetOrCompute("no items or no dock");
        private static readonly MyStringId StrJobAuctionFor = MyStringId.GetOrCompute("Job Auction: {0}");
        private static readonly MyStringId StrNoBids = MyStringId.GetOrCompute("no bids");
        private static readonly MyStringId StrJobFailedNoBids = MyStringId.GetOrCompute("Auction failed: {0}, no bids");
        private static readonly MyStringId StrJobDropped = MyStringId.GetOrCompute("Auction dropped: {0}\n  {1}");
        private static readonly MyStringId StrNoDroneBid = MyStringId.GetOrCompute("no drone bid");
        private static readonly MyStringId StrNoLogisticsComputerOffersThe = MyStringId.GetOrCompute("no logistics computer offers the components");
        private static readonly MyStringId StrNoTripFitsTheBids = MyStringId.GetOrCompute("no trip fits the bids");
        private static readonly MyStringId StrDroneSComponentSourceS = MyStringId.GetOrCompute("{0} drone(s), {1} component source(s)");
        private static readonly MyStringId StrNoLogisticsComputerHasThe = MyStringId.GetOrCompute("no logistics computer has the items");
        private static readonly MyStringId StrNoLogisticsComputerHasRoom = MyStringId.GetOrCompute("no logistics computer has room for it");
        private static readonly MyStringId StrTheDronesCanTCarry = MyStringId.GetOrCompute("the drones can't carry any of it");
        private static readonly MyStringId StrNoLcRoomForParts = MyStringId.GetOrCompute("no logistics computer has room for the parts");
        private static readonly MyStringId StrTheJobHasNoBlocks = MyStringId.GetOrCompute("the job has no blocks");
        private static readonly MyStringId StrOfBlockSOverDrone = MyStringId.GetOrCompute("{0} of {1} block(s) over {2} drone(s), parts to {3} logistics computer(s){4}");
        private static readonly MyStringId StrTheRestNoRoomOn = MyStringId.GetOrCompute(" (the rest: no room on the bidding drones)");
        private static readonly MyStringId StrTheJobHasNoSite = MyStringId.GetOrCompute("the job has no site");
        private static readonly MyStringId StrNoLogisticsComputerTakesThe = MyStringId.GetOrCompute("no logistics computer takes the ore");
        private static readonly MyStringId StrDroneSOfM3Of = MyStringId.GetOrCompute("{0} drone(s), {1:F1} of {2:F1} m3 of ore per trip, to {3} logistics computer(s)");
        private static readonly MyStringId StrIceTwoStops = MyStringId.GetOrCompute("; {0} drone(s): ice to the O2/H2 generators, the rest to a better refinery");
        private static readonly MyStringId StrTaskS = MyStringId.GetOrCompute("{0}, {1} task(s)");
        private static readonly MyStringId StrWithdrawnByItsAnnouncer = MyStringId.GetOrCompute("withdrawn by its announcer");
        private static readonly MyStringId StrJobWithdrawn = MyStringId.GetOrCompute("Job withdrawn\n  {0}");
        private static readonly MyStringId StrJobLeftToOrchestrator = MyStringId.GetOrCompute("Job left to orchestrator {0}");
        private static readonly MyStringId StrNoReportFromTheDrone = MyStringId.GetOrCompute("no report from the drone");
        private static readonly MyStringId StrNothing = MyStringId.GetOrCompute("nothing");
        #endregion

        #region Timing
        private const int STEP_TICKS = 60;                      // one pass of the loop
        private const int CAPABILITY_TICKS = 600;               // antenna again
        // Bids are collected at least BidCollectionWindowTicks (server config, 300 = 5 s: nearest drone, not the first)
        private const int AUCTION_POOL_TICKS = 15 * 60;         // ...and up to this long while the bids can't carry the job in one trip each
        private const int AUCTION_LIFETIME_SECONDS = 120;       // no workable bids by then: dropped
        private const int AUCTION_REBROADCAST_TICKS = 20 * 60;  // messages live 30 s: broadcast again while open
        private const int SEEN_TICKS = 30 * 60 * 60;            // an entity that has not bid / announced for this long is no longer "active"
        private const int JOB_MEMORY_TICKS = 15 * 3600;         // an assigned job's announcements are ignored this long
        private const int RETRY_MEMORY_TICKS = 30 * 60;         // a failed / dropped / aborted job: its next announcement is taken after this
        private const int DROPPED_HOLD_SECONDS = 60;            // bids came, but none could carry the job: short hold
        #endregion
        private const double MIN_TRIP_SHARE = 0.05;             // a weld trip carries at least this share of the job's components
        private const int MAX_LOGISTICS_TRIPS = 12;             // trips one delivery / collection is split into, at most

        private long entityId;
        private OrchestratorBlockSettings settings = new OrchestratorBlockSettings();
        private bool ready;
        private int infoCounter;
        private readonly StringBuilder info = new StringBuilder();
        private MessageQueue messaging;
        private IMyRadioAntenna ownAntenna;
        private AntennaReg antennaReg;
        private int lastStep = int.MinValue / 2, lastCapabilities = int.MinValue / 2;
        public State currentState { get; private set; }
        private string status = Loc.T(StrStarting);

        private sealed class OpenAuction
        {
            public Auction Auction;
            public Job Job;
            public Message<Auction> Message;
            public KeyValuePair<long, uint> JobKey;
            public int Started, LastBroadcast;
            public string LastWhy;
            public readonly List<Bid> Bids = new List<Bid>();
        }
        // Entities that bid or announced a job lately (a gauge for the header; not persisted, not exact)
        private struct SeenEntity { public AutomataEntityType Type; public int Tick; }
        private readonly Dictionary<long, SeenEntity> seenEntities = new Dictionary<long, SeenEntity>();
        private readonly Dictionary<uint, OpenAuction> auctions = new Dictionary<uint, OpenAuction>();
        /// <summary>The job behind each running auction (weld trips copy its work tasks).</summary>
        private readonly Dictionary<uint, Job> _auctionJobs = new Dictionary<uint, Job>();
        /// <summary>Jobs heard of (announcer, job id) -> frame until which their announcements are ignored.</summary>
        private readonly Dictionary<KeyValuePair<long, uint>, int> knownJobs = new Dictionary<KeyValuePair<long, uint>, int>();
        private readonly List<Message<AuctionWinnerAnnouncement>> winnerOutbox = new List<Message<AuctionWinnerAnnouncement>>();
        private readonly List<Message<JobAnnouncementFailed>> failureOutbox = new List<Message<JobAnnouncementFailed>>();
        /// <summary>Announcements waiting for an auction slot (first come, first auctioned).</summary>
        private sealed class QueuedJob
        {
            public JobAnnouncement Announcement;
            public KeyValuePair<long, uint> Key;
            public long SenderOwner;
            public int Received;
        }
        private readonly List<QueuedJob> jobQueue = new List<QueuedJob>();
        private OrchestratorState pendingState;
        private int failedCount;
        private readonly List<Message<JobAnnouncement>> announcementInbox = new List<Message<JobAnnouncement>>();
        private readonly List<Message<Bid>> bidInbox = new List<Message<Bid>>();
        private readonly List<uint> closing = new List<uint>();
        private readonly List<KeyValuePair<long, uint>> forget = new List<KeyValuePair<long, uint>>();
        private readonly BlockLog log = new BlockLog();
        // Awarded jobs whose drones haven't all reported (header "Active jobs"), until the job's memory ends
        private readonly Dictionary<KeyValuePair<long, uint>, ActiveJob> activeJobs = new Dictionary<KeyValuePair<long, uint>, ActiveJob>();
        private sealed class ActiveJob { public int Drones; public int Until; public JobType Type; public Vector3D Position; }
        private const double SHARED_SITE_RADIUS = 60;   // m: weld / grind jobs this close share a site
        // Jobs whose announcer was told to hold them (header "Jobs Held"): no logistics computer / no drone, until the hold ends
        private readonly Dictionary<KeyValuePair<long, uint>, HeldJob> heldJobs = new Dictionary<KeyValuePair<long, uint>, HeldJob>();
        private sealed class HeldJob { public bool NoLc; public int Until; }
        private readonly List<KeyValuePair<long, uint>> pruneKeys = new List<KeyValuePair<long, uint>>();
        private int auctionCounter, messageCounter;
        private int assignedCount, droppedCount;

        private static readonly Channel[] AnnouncementChannels =
        {
            Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
            Channel.LOGISTIC_REQUEST,
            Channel.LOGISTIC_PUSH,
            Channel.MINING_JOB_ANNOUNCEMENT,
        };

        #region Block
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            block = (IMyTerminalBlock)Entity;
            entityId = Entity.EntityId;
            currentState = State.Initializing;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            if (block.CubeGrid == null || block.CubeGrid.Physics == null) return;   // projections / ghosts
            OrchestratorTerminalControls.DoOnce(ModContext);
            LoadFromStorage();   // settings; the server keeps the saved state for when the network is up
            block.AppendingCustomInfo += AppendInfo;
            ready = true;
            if (!All.Contains(this)) All.Add(this);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            Log.Info("Orchestrator {0}: '{1}' on '{2}'", entityId, block.CustomName, block.CubeGrid.DisplayName);
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();
            if (!ready) return;
            if (MyAPIGateway.Multiplayer.IsServer) { UpdateAI(); UpdateLanes10(); }
            if (++infoCounter >= 10)
            {
                infoCounter = 0;
                block.RefreshCustomInfo();
                if (MyAPIGateway.Multiplayer.IsServer)
                {
                    double tl = Util.PerfStats.Start();
                    UpdateLcd();
                    Util.PerfStats.Stop(Util.PerfStats.LcdBuild, tl);
                }
            }
        }

        protected override string SettingsXml()
        {
            return MyAPIGateway.Utilities.SerializeToXML(settings);
        }

        protected override bool LoadSettingsXml(string xml)
        {
            var loaded = MyAPIGateway.Utilities.SerializeFromXML<OrchestratorBlockSettings>(xml);
            if (loaded == null) return false;
            settings = loaded;
            return true;
        }

        // The save struct: settings, and on the server its state (or the saved state not applied yet)
        protected override string SaveText()
        {
            var data = new OrchestratorSaveData { Settings = settings, EntityId = Entity.EntityId };
            if (IsServer) data.State = pendingState ?? (ready ? CaptureState() : null);
            return BlockSave.ToText(data);
        }

        protected override bool LoadSaveText(string text)
        {
            var data = BlockSave.FromText<OrchestratorSaveData>(text, Entity.EntityId);
            if (data == null) return false;
            settings = data.Settings ?? new OrchestratorBlockSettings();
            if (IsServer && data.State != null && BlockSave.OwnState(data.EntityId, Entity.EntityId, "Orchestrator")) pendingState = data.State;
            return true;
        }

        protected override void OnSettingsChanged()
        {
        }

        #region Tagged LCDs
        private readonly BlockLcd lcd = new BlockLcd();
        private readonly List<string> header = new List<string>();

        private void UpdateLcd()
        {
            Prune(MyAPIGateway.Session.GameplayFrameCounter);
            UpdateDesignatorLcd();
            if (!lcd.Wanted(block, settings.LcdTag)) return;   // nobody looks at an LCD: nothing more is built
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

        // "Orchestrator <nickname>", active jobs, open auctions, jobs held (no logistics computer / no drone)
        private void BuildHeader()
        {
            int noLc = 0, noDrone = 0;
            foreach (var h in heldJobs.Values) if (h.NoLc) noLc++; else noDrone++;
            header.Clear();
            header.Add(Loc.F(StrHeaderTitle, Nicknames.Of(Entity)));
            header.Add(BlockLcd.Columns(Loc.F(StrHeaderActive, activeJobs.Count), Loc.F(StrHeaderHeldLc, noLc), 24));
            header.Add(BlockLcd.Columns(Loc.F(StrHeaderAuctions, auctions.Count), Loc.F(StrHeaderHeldDrone, noDrone), 24));
            int drones = 0, lc = 0, cc = 0, ms = 0;
            foreach (var e in seenEntities.Values)
                switch (e.Type)
                {
                    case AutomataEntityType.DroneControllerBlock: drones++; break;
                    case AutomataEntityType.LogisticsComputerBlock: lc++; break;
                    case AutomataEntityType.ConstructionComputerBlock: cc++; break;
                    case AutomataEntityType.MiningSurveyorBlock: ms++; break;
                }
            header.Add(Loc.F(StrHeaderSeen, drones, lc, cc, ms));
        }

        private void NoteSeen(long id, AutomataEntityType type, int now)
        {
            if (id != 0) seenEntities[id] = new SeenEntity { Type = type, Tick = now };
        }

        private void Prune(int now)
        {
            pruneKeys.Clear();
            foreach (var kv in activeJobs) if (now > kv.Value.Until || kv.Value.Drones <= 0) pruneKeys.Add(kv.Key);
            for (int i = 0; i < pruneKeys.Count; i++) activeJobs.Remove(pruneKeys[i]);
            pruneKeys.Clear();
            foreach (var kv in heldJobs) if (now > kv.Value.Until) pruneKeys.Add(kv.Key);
            for (int i = 0; i < pruneKeys.Count; i++) heldJobs.Remove(pruneKeys[i]);
            pruneKeys.Clear();
            var gone = new List<long>();
            foreach (var kv in seenEntities) if (now - kv.Value.Tick > SEEN_TICKS) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++) seenEntities.Remove(gone[i]);
        }

        // An auction that ends in a hold for its announcer: counted until the hold ends
        private void NoteHeld(OpenAuction open, string why, int holdSeconds, int now)
        {
            bool noLc = HasDroneBid(open) && why != Loc.T(StrNoTripFitsTheBids) && why != Loc.T(StrNoDroneBid);
            heldJobs[open.JobKey] = new HeldJob { NoLc = noLc, Until = now + Math.Max(1, holdSeconds) * 60 };
        }

        // "Auction won: Weld D: <drone>, LC: <lc>", or a table of the drones and the logistics computers
        private string WonText(OpenAuction open, Dictionary<long, List<Task>> assignments, List<long> sources)
        {
            var drones = new List<long>(assignments.Keys);
            string type = LabelOf(open.Auction);
            if (drones.Count <= 1 && sources.Count <= 1)
                return Loc.F(StrAuctionWonOne, type, drones.Count > 0 ? Nicknames.Of(drones[0]) : "-", sources.Count > 0 ? Nicknames.Of(sources[0]) : "-");
            var sb = new StringBuilder(Loc.F(StrAuctionWonMany, type));
            sb.Append("\n  ").Append(BlockLcd.Columns(Loc.T(StrAuctionWonColumns), Loc.T(StrAuctionWonColumnsLc), 22));
            for (int i = 0; i < Math.Max(drones.Count, sources.Count); i++)
                sb.Append("\n  ").Append(BlockLcd.Columns(i < drones.Count ? Nicknames.Of(drones[i]) : "", i < sources.Count ? Nicknames.Of(sources[i]) : "", 22));
            return sb.ToString();
        }

        private static readonly List<IMyCubeGrid> ownGridBuffer = new List<IMyCubeGrid>();

        // Drones whose own grids (mechanical group) hold any of the job's blocks are left out: a parked drone can be a
        // repair target, and it must not take that job itself
        private static void NotOnOwnGrids(List<Bid> drones, Job job)
        {
            if (job == null || job.Tasks == null) return;
            var targets = new HashSet<long>();
            for (int i = 0; i < job.Tasks.Count; i++)
                if (job.Tasks[i].Kind == TaskKind.Tool && job.Tasks[i].TargetEntityId != 0) targets.Add(job.Tasks[i].TargetEntityId);
            if (targets.Count == 0) return;
            for (int d = drones.Count - 1; d >= 0; d--)
            {
                IMyEntity e;
                var cube = MyAPIGateway.Entities.TryGetEntityById(drones[d].EntityId, out e) ? e as IMyCubeBlock : null;
                if (cube == null || cube.CubeGrid == null) continue;
                ownGridBuffer.Clear();
                MyAPIGateway.GridGroups.GetGroup(cube.CubeGrid, GridLinkTypeEnum.Mechanical, ownGridBuffer);
                for (int g = 0; g < ownGridBuffer.Count; g++)
                    if (targets.Contains(ownGridBuffer[g].EntityId)) { drones.RemoveAt(d); break; }
            }
            ownGridBuffer.Clear();
        }

        // The logistics computers whose connectors the drones dock at (their bids name the ports)
        private static List<long> SourcesOf(OpenAuction open, Dictionary<long, List<Task>> assignments)
        {
            var ports = new HashSet<long>();
            foreach (var tasks in assignments.Values)
                if (tasks != null)
                    for (int i = 0; i < tasks.Count; i++)
                        if (tasks[i].Type == TaskType.DockAt && tasks[i].TargetEntityId != 0) ports.Add(tasks[i].TargetEntityId);
            var result = new List<long>();
            for (int i = 0; i < open.Bids.Count; i++)
            {
                var b = open.Bids[i];
                if (b == null || b.EntityType == AutomataEntityType.DroneControllerBlock || result.Contains(b.EntityId)) continue;
                bool hit = b.IOLocationData != null && ports.Contains(b.IOLocationData.EntityId);
                if (!hit && b.DockSite != null && b.DockSite.Ports != null)
                    for (int k = 0; k < b.DockSite.Ports.Count && !hit; k++) hit = ports.Contains(b.DockSite.Ports[k].EntityId);
                if (hit) result.Add(b.EntityId);
            }
            // Deliveries / collections: the announcer's own connector is not a source
            result.Remove(open.JobKey.Key);
            return result;
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

        public override void Close()
        {
            try
            {
                if (block != null) block.AppendingCustomInfo -= AppendInfo;
                All.Remove(this);
                HideAllPlayerGps();
                if (messaging != null) { messaging.ReaderClosed(entityId); }
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: closed ({1} open auction(s), {2} queued)", entityId, auctions.Count, jobQueue.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Orchestrator {0}: close error: {1}", entityId, ex);
            }
            base.Close();
        }
        #endregion

        #region Player jobs (scout laser)
        /// <summary>Orchestrators loaded on this machine (the scout laser looks for one whose antenna covers the player).</summary>
        public static readonly List<OrchestratorBlock> All = new List<OrchestratorBlock>();
        private int playerJobCounter;

        public long OwnerId { get { return block != null ? block.OwnerId : 0; } }

        /// <summary>Its broadcasting antenna reaches 'position' (and it is working): the player is in its network's range.</summary>
        private static readonly List<IMyCubeGrid> coverGrids = new List<IMyCubeGrid>();

        /// <summary>
        /// Is 'position' (a player, 'identity') in this orchestrator's network: inside the broadcast range of a working,
        /// broadcasting antenna of its base (its grid and everything connected to it), or of any antenna of the
        /// player / their faction that reaches the base through the relay network (other bases, drones, laser links).
        /// 'antenna': the covering one (debug).
        /// </summary>
        public bool Covers(Vector3D position, long identity, out string antenna)
        {
            if (CoversDirect(position, out antenna)) return true;
            return messaging != null && messaging.Relays.Covers(position, block.CubeGrid, identity, out antenna);
        }

        private bool CoversDirect(Vector3D position, out string antenna)
        {
            antenna = null;
            if (!ready || block == null || !block.IsWorking) return false;
            coverGrids.Clear();
            block.CubeGrid.GetGridGroup(GridLinkTypeEnum.Logical).GetGrids(coverGrids);
            for (int g = 0; g < coverGrids.Count && antenna == null; g++)
                foreach (var a in coverGrids[g].GetFatBlocks<IMyRadioAntenna>())
                {
                    if (a.Closed || !a.IsWorking || !a.EnableBroadcasting) continue;
                    if (!Drone.AnchorDirectory.IsAllowed(block.OwnerId, a.OwnerId)) continue;
                    if (Vector3D.DistanceSquared(a.GetPosition(), position) > (double)AntennaUtil.Range(a) * AntennaUtil.Range(a)) continue;
                    antenna = a.CustomName;
                    break;
                }
            coverGrids.Clear();
            return antenna != null;
        }

        #endregion

        // Every 10 ticks on the server; self-throttled
        private void UpdateAI()
        {
            if (block == null || block.CubeGrid == null || block.CubeGrid.Physics == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastStep < STEP_TICKS) return;
            lastStep = now;
            try
            {
                if (!block.IsWorking)
                {
                    status = Loc.T(StrOffNotWorking);
                    currentState = State.Standby;
                    return;
                }
                if (messaging == null) messaging = AutomataSession.GetMessageQueue();
                if (messaging == null) { status = Loc.T(StrWaitingForTheMod); return; }
                if (pendingState != null)
                {
                    if (!BlockSave.CanRestore) { status = Loc.T(StrLoading); return; }
                    RestoreState(pendingState);
                    pendingState = null;
                }
                if (commissionedTicks == 0) commissionedTicks = DateTime.UtcNow.Ticks;   // new (or saved before this existed)
                if (!AntennaUtil.Alive(ownAntenna) || now - lastCapabilities >= CAPABILITY_TICKS)   // removed: the next one at once
                {
                    lastCapabilities = now;
                    FindAntenna();
                }
                if (!EnsureAntenna())
                {
                    status = Loc.T(StrNoAntennaFunctionalOnBroadcasting);
                    currentState = State.Error;
                    return;
                }
                UpdateDormancy();
                currentState = auctions.Count > 0 ? State.AssigningTasks : State.Standby;
                status = primary != null ? DormantStatus() : auctions.Count > 0 ? Loc.T(StrAuctioning) : Loc.T(StrListeningForJobs);
                if (primary == null)
                {
                    ReadAnnouncements(now);
                    ReadPeerAuctions(now);
                    StartQueuedAuctions(now);
                }
                // Dormant: auctions it had open still run to their end (bids, winners, reports); nothing new
                RebroadcastAuctions(now);
                ReadBids();
                ReadWithdrawals(now);
                CloseAuctions(now);
                ExpirePlayerJobs(now);   // designator orders not confirmed in time
                SendWinners();
                SendFailures();
                ReadJobReports(now);
                ReadKeepalives(now);
                ForgetJobs(now);
            }
            catch (Exception ex)
            {
                Log.Error("Orchestrator {0}: update error: {1}", entityId, ex);
                status = Loc.F(StrError, ex.Message);
                currentState = State.Error;
            }
        }

        #region One orchestrator per network
        private long commissionedTicks;
        private OrchestratorBlock primary;   // the orchestrator running this network (null: this one)

        /// <summary>Running (not dormant, working, ready): the one a network's jobs go to.</summary>
        public bool IsRunning { get { return ready && primary == null && block != null && block.IsWorking && commissionedTicks != 0; } }

        /// <summary>
        /// A player / faction needs one orchestrator per Automata network (bases and drones linked by antennas,
        /// relayed: TODO/13). The earliest commissioned working one in the network runs it; any other goes dormant
        /// (no new jobs; auctions it had open finish) and takes over by itself when the first one stops or leaves the
        /// network. Same network: each reaches the other through the relay graph, and their owners are the same or
        /// in one faction.
        /// </summary>
        private void UpdateDormancy()
        {
            OrchestratorBlock first = null;
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (o == this || !o.ready || o.block == null || !o.block.IsWorking || o.commissionedTicks == 0) continue;
                if (!(o.commissionedTicks < commissionedTicks || (o.commissionedTicks == commissionedTicks && o.entityId < entityId))) continue;
                if (!AnchorDirectory.IsAllowed(OwnerId, o.OwnerId) || !SameNetwork(o)) continue;
                if (first == null || o.commissionedTicks < first.commissionedTicks
                    || (o.commissionedTicks == first.commissionedTicks && o.entityId < first.entityId)) first = o;
            }
            if (first == primary) return;
            if (first != null)
            {
                Log.Info("Orchestrator {0}: dormant, orchestrator {1} ('{2}') runs this network ({3} queued job(s) dropped: their announcers repeat them)",
                         entityId, first.entityId, first.block.CustomName, jobQueue.Count);
                jobQueue.Clear();
                AddEvent(Loc.F(StrDormantRunsTheNetwork, first.block.CustomName));
            }
            else
            {
                Log.Info("Orchestrator {0}: running the network ({1})", entityId,
                         primary != null ? "orchestrator " + primary.entityId + " stopped or left it" : "first in it");
                AddEvent(Loc.T(StrRunningTheNetwork));
            }
            primary = first;
        }

        private bool SameNetwork(OrchestratorBlock o)
        {
            if (messaging == null || o.block == null) return false;
            var relays = messaging.Relays;
            return relays.Reaches(block.CubeGrid, o.block.CubeGrid, OwnerId) && relays.Reaches(o.block.CubeGrid, block.CubeGrid, o.OwnerId);
        }

        private string DormantStatus()
        {
            return Loc.F(StrDormantRunsThisNetwork, primary.block != null ? primary.block.CustomName : primary.entityId.ToString());
        }
        #endregion

        #region Antenna
        // The largest broadcasting antenna on the grid (and what's attached by rotors / pistons)
        private void FindAntenna()
        {
            var before = ownAntenna;
            ownAntenna = null;
            var grids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, grids);
            for (int g = 0; g < grids.Count; g++)
                foreach (var a in grids[g].GetFatBlocks<IMyRadioAntenna>())
                    if (a.IsFunctional && a.Enabled && a.EnableBroadcasting && (ownAntenna == null || AntennaUtil.Range(a) > AntennaUtil.Range(ownAntenna)))
                        ownAntenna = a;
            if (ownAntenna != before)
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: antenna {1} ({2} grid(s) searched)", entityId,
                          ownAntenna != null ? "'" + ownAntenna.CustomName + "'" : "none", grids.Count);
        }

        private bool EnsureAntenna()
        {
            return AntennaReg.Ensure(messaging, ref antennaReg, ownAntenna);
        }
        #endregion

        #region Announcements -> auctions
        private void ReadAnnouncements(int now)
        {
            for (int c = 0; c < AnnouncementChannels.Length; c++)
            {
                messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, AnnouncementChannels[c], announcementInbox, 50, true, PayloadType.JobAnnouncement);
                for (int i = 0; i < announcementInbox.Count; i++)
                {
                    var msg = announcementInbox[i];
                    var ja = msg.Payload;
                    if (ja == null) continue;
                    if (AnchorDirectory.IsAllowed(block.OwnerId, msg.SenderOwnerId)) NoteSeen(msg.SenderId, ja.EntityType, now);
                    var key = new KeyValuePair<long, uint>(msg.SenderId, ja.JobId);
                    int until;
                    if (knownJobs.TryGetValue(key, out until) && now < until)
                    {
                        // Repeated: running, assigned, yielded... (logged once per job)
                        if (ignoreLogged.Add(key))
                            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: job {1} from {2}: announced again, ignored ({3})", entityId, ja.JobId, msg.SenderId,
                                      until == int.MaxValue ? "queued / auctioned" : "remembered " + (until - now) / 60 + " s more");
                        continue;
                    }
                    ignoreLogged.Remove(key);
                    // Jobs of the orchestrator owner / their faction only (never bases of others)
                    if (!AnchorDirectory.IsAllowed(block.OwnerId, msg.SenderOwnerId))
                    {
                        if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: job {1} from {2} ignored: another owner / faction", entityId, ja.JobId, msg.SenderId);
                        knownJobs[key] = now + JOB_MEMORY_TICKS;
                        continue;
                    }
                    knownJobs[key] = int.MaxValue;   // queued / auctioned: until it ends
                    jobQueue.Add(new QueuedJob { Announcement = ja, Key = key, SenderOwner = msg.SenderOwnerId, Received = now });
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: queued job {1} ({2}) from {3} {4} on {5}: {6} item(s), {7} task(s); {8} queued, {9} auction(s) open",
                              entityId, ja.JobId, ja.Type.Label(ja.WeldCategory), ja.EntityType, msg.SenderId, AnnouncementChannels[c],
                              ja.ComponentsInventory != null ? ja.ComponentsInventory.GetTotalItemCount() : 0, ja.Tasks != null ? ja.Tasks.Count : 0,
                              jobQueue.Count, auctions.Count);
                }
                announcementInbox.Clear();
            }
        }

        // Queued announcements into auctions, oldest first, while auction slots are free
        private void StartQueuedAuctions(int now)
        {
            var config = AutomataSession.GetConfig();
            int max = config != null ? config.OrchestratorServerSettings.MaxConcurrentAuctions : 10;
            while (jobQueue.Count > 0 && auctions.Count < max)
            {
                var q = jobQueue[0];
                jobQueue.RemoveAt(0);
                if (!PlayerJobStillWanted(q.Key)) continue;
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: job {1} from {2} out of the queue after {3:F0} s", entityId, q.Key.Value, q.Key.Key, (now - q.Received) / 60.0);
                StartAuction(q.Announcement, q.Key, now);
            }
        }

        private void StartAuction(JobAnnouncement ja, KeyValuePair<long, uint> key, int now)
        {
            switch (ja.Type)
            {
                case JobType.Weld:
                case JobType.Grind:
                case JobType.DeliverMissingInventory:
                case JobType.CollectInventorySurplus:
                case JobType.MineOre:
                case JobType.DesignatorScoutPoint:
                case JobType.DesignatorScoutArea:
                case JobType.DesignatorEngage:
                case JobType.DesignatorEscort:
                    break;
                default:
                    AddEvent(Loc.F(StrJobNotHandledYet, ja.Type));
                    QueueFailure(key, ja.JobId, 0, Loc.F(StrJobTypeNotHandled, ja.Type), FailedHoldSeconds);
                    knownJobs[key] = now + RETRY_MEMORY_TICKS;
                    return;
            }
            var job = ja.ToJob();
            if (job.TotalInventory == null || job.TotalInventory.IsEmpty()) job.CalculateTotalInventory();
            var items = ja.ComponentsInventory != null && !ja.ComponentsInventory.IsEmpty() ? ja.ComponentsInventory : job.TotalInventory;
            if ((ja.Type == JobType.DeliverMissingInventory || ja.Type == JobType.CollectInventorySurplus) && (items == null || items.IsEmpty() || ja.Dock == null))
            {
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: job {1} ({2}) ignored: no items or no dock", entityId, ja.JobId, ja.Type);
                QueueFailure(key, ja.JobId, 0, Loc.T(StrNoItemsOrNoDock), FailedHoldSeconds);
                knownJobs[key] = now + RETRY_MEMORY_TICKS;
                return;
            }
            var auction = new Auction
            {
                JobId = ja.JobId,
                JobType = ja.Type,
                ComponentsInventory = items ?? new DiscreteInventory(),
                PositionData = ja.PositionData,
                OrientationData = ja.OrientationData,
                CreatedTime = DateTime.UtcNow,
                ExpirationTime = DateTime.UtcNow.AddSeconds(AUCTION_LIFETIME_SECONDS),
                EntityType = AutomataEntityType.OrchestratorBlock,
                NaturalGravity = ja.NaturalGravity,
                IsInSpace = ja.IsInSpace,
                IsInAtmosphere = ja.IsInAtmosphere,
                IsStaticGrid = ja.IsStaticGrid,
                TotalMass = items != null ? items.TotalInventoryMass(AutomataSession.Instance) : MyFixedPoint.Zero,
                TotalVolume = items != null ? items.TotalInventoryVolume(AutomataSession.Instance) : MyFixedPoint.Zero,
                AuctionId = IdGenerator.GenerateId(ref auctionCounter, entityId),
                RequesterEntityId = ja.RequesterEntityId,
                Dock = ja.Dock,
                DockSite = ja.DockSite,
                AnnouncerEntityId = key.Key,
                ToolFaceWeights = ja.ToolFaceWeights,
                OnlyDroneEntityId = PlayerJobDrone(key),
                WeldCategory = ja.Type == JobType.Weld ? ja.WeldCategory : JobTypeExtensions.WELD_ANY,
                Sequential = ja.Sequential,
            };
            var open = new OpenAuction
            {
                Auction = auction,
                Job = job,
                JobKey = key,
                Started = now,
                LastBroadcast = int.MinValue / 2,
                Message = AuctionMessage(auction),
            };
            auctions[auction.AuctionId] = open;
            _auctionJobs[auction.AuctionId] = job;
            string label = ja.Type.Label(auction.WeldCategory);
            AddEvent(Loc.F(StrJobAuctionFor, label));
            Log.Info("Orchestrator {0}: job {1} ({2}) from {3}: auction {4} for {5}", entityId, ja.JobId, label, key.Key, auction.AuctionId, Describe(items));
        }

        private Message<Auction> AuctionMessage(Auction auction)
        {
            return new Message<Auction>
            {
                Payload = auction,
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                // (orchestrators too: two in range of one announcer must not both auction the job)
                RecipientBlockType = MessageQueue.IAIBlockType.Drone | MessageQueue.IAIBlockType.LogisticsComputer | MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.ORCHESTRATOR_AUCTION_START,
            };
        }

        private void RebroadcastAuctions(int now)
        {
            foreach (var open in auctions.Values)
            {
                if (now - open.LastBroadcast < AUCTION_REBROADCAST_TICKS) continue;
                open.LastBroadcast = now;
                open.Message.MessageId = IdGenerator.GenerateId(ref messageCounter, entityId);
                open.Message.CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
                var error = messaging.BroadcastMessage(antennaReg, open.Message, false);
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1} (job {2}, {3}) broadcast: {4}", entityId, open.Auction.AuctionId, open.Auction.JobId,
                          LabelOf(open.Auction), error == ErrorCode.None ? "sent" : error.ToString());
            }
        }
        #endregion

        #region Bids
        private void ReadBids()
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, Channel.ORCHESTRATOR_AUCTION_BIDS, bidInbox, 100, true, PayloadType.Bid);
            for (int i = 0; i < bidInbox.Count; i++)
            {
                var bid = bidInbox[i].Payload;
                OpenAuction open;
                if (bid != null && bid.EntityId == bidInbox[i].SenderId && AnchorDirectory.IsAllowed(block.OwnerId, bidInbox[i].SenderOwnerId))
                    NoteSeen(bid.EntityId, bid.EntityType, MyAPIGateway.Session.GameplayFrameCounter);
                if (bid == null || !auctions.TryGetValue(bid.AuctionId, out open)) continue;
                if (bid.EntityId != bidInbox[i].SenderId)
                {
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: bid for {2} sent by {3} ignored", entityId, bid.AuctionId, bid.EntityId, bidInbox[i].SenderId);
                    continue;
                }
                if (!AnchorDirectory.IsAllowed(block.OwnerId, bidInbox[i].SenderOwnerId))
                {
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: bid from {2} ignored (another owner / faction)", entityId, bid.AuctionId, bid.EntityId);
                    continue;
                }
                if (!AcceptableBid(bid, open.Auction)
                    || (open.Auction.OnlyDroneEntityId != 0 && bid.EntityType == AutomataEntityType.DroneControllerBlock
                        && bid.EntityId != open.Auction.OnlyDroneEntityId))
                {
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: bid from {2} {3} not acceptable (caps {4}, {5})", entityId, bid.AuctionId, bid.EntityType,
                              bid.EntityId, bid.Capabilities, bid.InventoryFulfillmentFlags);
                    continue;
                }
                // One bid per bidder (the latest)
                bool replaced = false;
                for (int k = 0; k < open.Bids.Count; k++)
                    if (open.Bids[k].EntityId == bid.EntityId) { open.Bids[k] = bid; replaced = true; break; }
                if (!replaced) open.Bids.Add(bid);
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: {2} bid from {3} {4}: {5}{6}", entityId, bid.AuctionId, replaced ? "new" : "first",
                          bid.EntityType, bid.EntityId, bid.EntityType == AutomataEntityType.DroneControllerBlock
                              ? string.Format("cargo {0:F2} m3, lift {1:F0} kg", (double)bid.MaxCargoVolume, (double)bid.MaxLoadIn1G)
                              : bid.InventoryFulfillmentFlags + " " + Describe(bid.BidInventory),
                          bid.IOLocationData != null ? " at port " + bid.IOLocationData.EntityId : "");
            }
            bidInbox.Clear();
        }

        // The minimum a bid needs for the job (the drone / computer checked it too)
        private static bool AcceptableBid(Bid bid, Auction a)
        {
            if (bid.EntityType == AutomataEntityType.LogisticsComputerBlock)
            {
                if (bid.IOLocationData == null || bid.EntityId == a.RequesterEntityId) return false;
                var f = bid.InventoryFulfillmentFlags;
                switch (a.JobType)
                {
                    case JobType.Weld:
                    case JobType.DeliverMissingInventory:
                        return (f & (InventoryFulfillment.SatisfyFully | InventoryFulfillment.SatisfyPartial)) != 0
                               && bid.BidInventory != null && !bid.BidInventory.IsEmpty();
                    case JobType.Grind:
                    case JobType.CollectInventorySurplus:
                    case JobType.MineOre:   // room, on a network with a refinery for the ore (the computer checked)
                        return (f & (InventoryFulfillment.AcceptAll | InventoryFulfillment.AcceptPartial)) != 0;
                    default:
                        return false;
                }
            }
            if (bid.EntityType != AutomataEntityType.DroneControllerBlock || bid.IOLocationData == null) return false;
            // Jobs the player designates drones for (scout / combat pointer): only drones with that behaviour profile
            var required = RequiredProfile(a.JobType);
            if (required != Drone.BehaviourProfile.None && (bid.DroneBehaviours & required) == 0) return false;
            // Ore haulers: ores / ice only (logistics whose items are all ore, mining); one item of anything else rules them out
            if ((bid.DroneBehaviours & Drone.BehaviourProfile.IsOreHauler) != 0 && !IsOreJob(a)) return false;
            var caps = bid.Capabilities;
            if (a.IsInSpace && (caps & Drone.Capabilities.CanFlySpace) == 0) return false;
            if (a.IsInAtmosphere && (caps & Drone.Capabilities.CanFlyAtmosphere) == 0) return false;
            // Scouting and fighting carry nothing: cargo / payload only for jobs that haul (components, items, ore)
            bool hauls = !a.JobType.IsDesignator();
            if (hauls && (caps & (Drone.Capabilities.CargoVolumeNil | Drone.Capabilities.LiftNil)) != 0) return false;
            if (hauls && (bid.MaxCargoVolume <= 0 || bid.MaxLoadIn1G <= 0)) return false;
            switch (a.JobType)
            {
                case JobType.Weld:    return (caps & Drone.Capabilities.CanWeld) != 0;
                case JobType.Grind:   return (caps & Drone.Capabilities.CanGrind) != 0;
                case JobType.DeliverMissingInventory:
                case JobType.CollectInventorySurplus:
                    return (caps & Drone.Capabilities.CanDock) != 0;
                // Mining drones need an ore detector: they check at the site that the ore is still there
                case JobType.MineOre: return (caps & Drone.Capabilities.CanDrill) != 0 && (caps & Drone.Capabilities.CanScoutOre) != 0;
                case JobType.DesignatorScoutPoint:
                case JobType.DesignatorScoutArea: return (caps & Drone.Capabilities.CanScoutOre) != 0;
                // AI Defensive + AI Flight + AI Offensive on the drone (combat handover)
                case JobType.DesignatorEngage:
                case JobType.DesignatorEscort: return (caps & Drone.Capabilities.HasDefenseSystems) != 0;
                default: return false;
            }
        }
        #endregion

        #region Winners
        // The drones that bid carry the whole job in one trip each (designator jobs carry nothing: always)
        private static bool BidsCoverOneTrip(OpenAuction open)
        {
            if (open.Auction.JobType.IsDesignator()) return true;
            double need = (double)open.Auction.TotalVolume, have = 0;
            if (need <= 0) return true;
            for (int i = 0; i < open.Bids.Count; i++)
                if (open.Bids[i] != null && open.Bids[i].EntityType == AutomataEntityType.DroneControllerBlock) have += Math.Max(0.0, (double)open.Bids[i].MaxCargoVolume);
            return have >= need;
        }

        private static bool HasDroneBid(OpenAuction open)
        {
            for (int i = 0; i < open.Bids.Count; i++)
                if (open.Bids[i] != null && open.Bids[i].EntityType == AutomataEntityType.DroneControllerBlock) return true;
            return false;
        }

        private void CloseAuctions(int now)
        {
            closing.Clear();
            var config = AutomataSession.GetConfig();
            int noBidTicks = (config != null ? config.OrchestratorServerSettings.NoBidTimeoutSeconds : 60) * 60;
            foreach (var open in auctions.Values)
            {
                // Nobody bid at all: the announcer is told and holds the job (no point announcing it again soon)
                // (logistics computers' offers alone don't count: without a drone the job can't happen)
                if (!HasDroneBid(open) && now - open.Started >= noBidTicks)
                {
                    // A player's order: not auctioned again (cancelled, ExpirePlayerJobs)
                    if (RequeuePlayerJob(open, now, Loc.T(StrNoBids))) { closing.Add(open.Auction.AuctionId); continue; }
                    failedCount++;
                    QueueFailure(open.JobKey, open.Auction.JobId, open.Auction.AuctionId, Loc.F(open.Bids.Count == 0 ? StrNoBidsWithin : StrNoDroneBidWithin, noBidTicks / 60), FailedHoldSeconds);
                    heldJobs[open.JobKey] = new HeldJob { NoLc = false, Until = now + FailedHoldSeconds * 60 };
                    AddEvent(Loc.F(StrJobFailedNoBids, LabelOf(open.Auction)));
                    Log.Info("Orchestrator {0}: auction {1} (job {2}, {3}) failed: no bids within {4} s, announcer {5} told", entityId,
                             open.Auction.AuctionId, open.Auction.JobId, LabelOf(open.Auction), noBidTicks / 60, open.JobKey.Key);
                    knownJobs[open.JobKey] = now + RETRY_MEMORY_TICKS;   // announced again after the hold: a new auction
                    closing.Add(open.Auction.AuctionId);
                    continue;
                }
                bool expired = now - open.Started >= AUCTION_LIFETIME_SECONDS * 60;   // frames, like the no-bid timeout
                // Pooling: a job bigger than one trip of every drone that bid so far waits a little longer for more drones
                int window = config != null ? config.OrchestratorServerSettings.BidCollectionWindowTicks : 300;
                // (not a sequential job: one drone does every trip anyway)
                bool pooling = now - open.Started < Math.Max(AUCTION_POOL_TICKS, window) && !open.Auction.Sequential && !BidsCoverOneTrip(open);
                if (now - open.Started >= window && !pooling)
                {
                    Dictionary<long, List<Task>> assignments;
                    long droneId;
                    uint droneBid;
                    string why;
                    if (TryAssign(open, out assignments, out droneId, out droneBid, out why))
                    {
                        var sources = SourcesOf(open, assignments);
                        QueueWinner(open, assignments, droneBid, sources);
                        assignedCount++;
                        activeJobs[open.JobKey] = new ActiveJob { Drones = assignments.Count, Until = now + JOB_MEMORY_TICKS, Type = open.Auction.JobType,
                                                                  Position = open.Auction.PositionData.ToVector3D() };
                        heldJobs.Remove(open.JobKey);
                        AddEvent(WonText(open, assignments, sources));
                        Log.Info("Orchestrator {0}: auction {1} (job {2}, {3}) won by drone {4}: {5}", entityId, open.Auction.AuctionId,
                                 open.Auction.JobId, LabelOf(open.Auction), droneId, why);
                        knownJobs[open.JobKey] = now + JOB_MEMORY_TICKS;   // assigned: its repeats are ignored (the drones report back)
                        PlayerJobAssigned(open.JobKey, now, droneId);
                        closing.Add(open.Auction.AuctionId);
                        continue;
                    }
                    if (why != open.LastWhy)
                    {
                        open.LastWhy = why;
                        if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1} (job {2}) waiting: {3} ({4} bid(s))", entityId, open.Auction.AuctionId,
                                  open.Auction.JobId, why, open.Bids.Count);
                    }
                    if (expired && RequeuePlayerJob(open, now, why)) { closing.Add(open.Auction.AuctionId); continue; }
                    if (expired)
                    {
                        droppedCount++;
                        NoteHeld(open, why, DROPPED_HOLD_SECONDS, now);
                        AddEvent(Loc.F(StrJobDropped, LabelOf(open.Auction), why));
                        Log.Info("Orchestrator {0}: auction {1} (job {2}, {3}) dropped: {4}", entityId, open.Auction.AuctionId, open.Auction.JobId,
                                 LabelOf(open.Auction), why);
                        QueueFailure(open.JobKey, open.Auction.JobId, open.Auction.AuctionId, why, DROPPED_HOLD_SECONDS);
                        knownJobs[open.JobKey] = now + RETRY_MEMORY_TICKS;   // the announcer's next announcement starts it over
                        closing.Add(open.Auction.AuctionId);
                    }
                }
            }
            for (int i = 0; i < closing.Count; i++)
            {
                auctions.Remove(closing[i]);
                _auctionJobs.Remove(closing[i]);
            }
            closing.Clear();
        }

        // Task lists for the winners, per job type; false (with why) while the bids can't carry the job out
        private bool TryAssign(OpenAuction open, out Dictionary<long, List<Task>> assignments, out long droneId, out uint droneBid, out string why)
        {
            assignments = null;
            droneId = 0;
            droneBid = 0;
            var drones = new List<Bid>();
            var lcs = new List<Bid>();
            for (int i = 0; i < open.Bids.Count; i++)
                (open.Bids[i].EntityType == AutomataEntityType.DroneControllerBlock ? drones : lcs).Add(open.Bids[i]);
            var a = open.Auction;
            if (a.JobType == JobType.Weld || a.JobType == JobType.Grind) NotOnOwnGrids(drones, open.Job);   // a drone never works on itself
            if (drones.Count == 0) { why = Loc.T(StrNoDroneBid); return false; }
            switch (a.JobType)
            {
                case JobType.DeliverMissingInventory:
                    return AssignDelivery(a, drones, lcs, out assignments, out droneId, out droneBid, out why);
                case JobType.CollectInventorySurplus:
                    return AssignCollection(a, drones, lcs, out assignments, out droneId, out droneBid, out why);
                case JobType.Weld:
                {
                    // Nothing to bring (the blocks only need welding up): no component source, the drone just goes to work
                    if (a.ComponentsInventory == null || a.ComponentsInventory.IsEmpty())
                        return AssignWeldWithoutComponents(open, drones, out assignments, out droneId, out droneBid, out why);
                    if (lcs.Count == 0) { why = Loc.T(StrNoLogisticsComputerOffersThe); return false; }
                    var winningLcs = SelectWinningLCs(lcs, a);
                    var winningDrones = SelectWinningDrones(drones, a);
                    assignments = BuildWeldTaskAssignments(winningDrones, winningLcs, a);
                    if (assignments.Count == 0) { why = Loc.T(StrNoTripFitsTheBids); return false; }
                    foreach (var k in assignments.Keys) { droneId = k; break; }
                    droneBid = BidIdOf(drones, droneId);
                    why = Loc.F(StrDroneSComponentSourceS, assignments.Count, winningLcs.Count);
                    return true;
                }
                case JobType.Grind:
                    return AssignGrind(open, drones, lcs, out assignments, out droneId, out droneBid, out why);
                case JobType.MineOre:
                    return AssignMine(open, drones, lcs, out assignments, out droneId, out droneBid, out why);
                case JobType.DesignatorScoutPoint:
                case JobType.DesignatorScoutArea:
                case JobType.DesignatorEngage:
                case JobType.DesignatorEscort:
                    return AssignDesignator(open, drones, out assignments, out droneId, out droneBid, out why);
            }
            why = Loc.T(StrJobTypeNotHandled);
            return false;
        }

        // Below a Min: from the suppliers (logistics computers) to the requester
        private bool AssignDelivery(Auction a, List<Bid> drones, List<Bid> lcs, out Dictionary<long, List<Task>> assignments,
                                    out long droneId, out uint droneBid, out string why)
        {
            return AssignLogistics(a, drones, lcs, true, out assignments, out droneId, out droneBid, out why);
        }

        // Above a Max: from the requester to the logistics computers with room
        private bool AssignCollection(Auction a, List<Bid> drones, List<Bid> lcs, out Dictionary<long, List<Task>> assignments,
                                      out long droneId, out uint droneBid, out string why)
        {
            return AssignLogistics(a, drones, lcs, false, out assignments, out droneId, out droneBid, out why);
        }

        /// <summary>
        /// Trips for a delivery (supplier -> requester) or collection (requester -> taker): the other ends ranked by
        /// how much of it they cover (nearest on a tie), the drones by score. One trip = dock, load, dock, unload; the
        /// drones take trips in turn (round robin), each as much as it can carry of what that end offers. One supplier
        /// and one drone covering it all is a single trip; anything bigger is split (several trips, drones,
        /// suppliers), up to MAX_LOGISTICS_TRIPS. What no bid covers is left: the requester announces it again.
        /// </summary>
        private bool AssignLogistics(Auction a, List<Bid> drones, List<Bid> lcs, bool delivery, out Dictionary<long, List<Task>> assignments,
                                     out long droneId, out uint droneBid, out string why)
        {
            assignments = null; droneId = 0; droneBid = 0;
            Vector3D dock = a.Dock.PositionData.ToVector3D();
            var remaining = new DiscreteInventory(a.ComponentsInventory);
            var ends = RankLogisticsEnds(lcs, remaining, dock);
            if (ends.Count == 0) { why = delivery ? Loc.T(StrNoLogisticsComputerHasThe) : Loc.T(StrNoLogisticsComputerHasRoom); return false; }
            var ranked = SelectWinningDrones(drones, a);
            if (ranked.Count == 0) { why = Loc.T(StrNoDroneBid); return false; }
            var result = new Dictionary<long, List<Task>>();
            var carried = new DiscreteInventory();
            var endsUsed = new HashSet<long>();
            int trips = 0, turn = 0;
            for (int e = 0; e < ends.Count && trips < MAX_LOGISTICS_TRIPS && !remaining.IsEmpty(); e++)
            {
                var end = ends[e];
                var offer = new DiscreteInventory(end.BidInventory);
                while (trips < MAX_LOGISTICS_TRIPS)
                {
                    var portion = Intersect(remaining, offer);
                    if (portion == null) break;
                    // The next drone in turn that can carry any of it
                    DiscreteInventory load = null;
                    Bid drone = null;
                    IOLocationData endPort = null, announcerPort = null;
                    for (int k = 0; k < ranked.Count && load == null; k++)
                    {
                        drone = ranked[(turn + k) % ranked.Count];
                        // Both connectors of the trip must suit the drone's connectors
                        endPort = PortFor(end.DockSite, end.IOLocationData, drone);
                        announcerPort = PortFor(a.DockSite, a.Dock, drone);
                        if (endPort == null || announcerPort == null)
                        {
                            if (FirstFitLog(a.AuctionId, drone.EntityId))
                            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: drone {2} (connectors {3}) fits no connector at {4} ({5})", entityId, a.AuctionId,
                                      drone.EntityId, Faces(drone), endPort == null ? "LC " + end.EntityId : "the requester",
                                      DockFit.Describe(endPort == null ? end.DockSite : a.DockSite));
                            continue;
                        }
                        load = FitToDrone(portion, drone);
                        if (load != null) turn = turn + k + 1;
                    }
                    if (load == null)
                    {
                        if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: no drone can carry any of {2}", entityId, a.AuctionId, Describe(portion));
                        break;
                    }
                    List<Task> tasks;
                    if (!result.TryGetValue(drone.EntityId, out tasks)) { tasks = new List<Task>(); result[drone.EntityId] = tasks; }
                    tasks.Add(DockTask(delivery ? endPort : announcerPort));
                    tasks.Add(Task.Load(load));
                    tasks.Add(DockTask(delivery ? announcerPort : endPort));
                    tasks.Add(Task.Unload(load));
                    Subtract(remaining, load);
                    Subtract(offer, load);
                    carried.AddItems(load);
                    endsUsed.Add(end.EntityId);
                    trips++;
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: trip {2}: drone {3} (connectors {8}), {4} LC {5} (port {6}, requester port {9}) -> {7}", entityId, a.AuctionId, trips,
                              drone.EntityId, delivery ? "from" : "to", end.EntityId, endPort.EntityId, Describe(load), Faces(drone), announcerPort.EntityId);
                }
            }
            if (trips == 0) { why = Loc.T(StrTheDronesCanTCarry); return false; }
            foreach (var tasks in result.Values) tasks.Add(Task.ReturnHome());
            assignments = result;
            foreach (var k in result.Keys) { droneId = k; break; }
            droneBid = BidIdOf(drones, droneId);
            why = Loc.F(delivery ? StrPlanDelivery : StrPlanCollection, trips, result.Count, endsUsed.Count, Describe(carried), a.RequesterEntityId,
                        remaining.IsEmpty() ? "" : Loc.F(StrPlanLeft, Describe(remaining)));
            return true;
        }

        // Logistics bids covering any of 'wanted': most covered first, the nearest to 'near' on a tie
        private static List<Bid> RankLogisticsEnds(List<Bid> lcs, DiscreteInventory wanted, Vector3D near)
        {
            var ranked = new List<Bid>();
            var cover = new List<double>();
            for (int i = 0; i < lcs.Count; i++)
            {
                var bid = lcs[i];
                if (bid.IOLocationData == null) continue;
                var common = Intersect(wanted, bid.BidInventory);
                if (common == null) continue;
                // Most items first; distance breaks ties (scaled far below one item)
                double score = common.GetTotalItemCount() - 1e-9 * Vector3D.DistanceSquared(near, bid.IOLocationData.PositionData.ToVector3D());
                int at = cover.Count;
                while (at > 0 && score > cover[at - 1]) at--;
                cover.Insert(at, score);
                ranked.Insert(at, bid);
            }
            return ranked;
        }

        private static void Subtract(DiscreteInventory from, DiscreteInventory items)
        {
            foreach (var item in items.GetAllItems())
            {
                int n = Math.Min(item.Value, from.GetItemCount(item.Key));
                if (n > 0) from.RemoveItem(item.Key, n);
            }
        }

        #region Grind / mine: pooled over every drone that bid
        private const int MAX_POOLED_DRONES = 10;     // drones one grind / mine job is split over, at most
        private const double MINER_SPACING = 8.0;     // m between the bore starts of drones mining one deposit

        // A taker (logistics computer with room) and how much room is left in it for this job
        private sealed class TakerRoom { public Bid Bid; public double Room; }

        private static List<TakerRoom> Takers(List<Bid> lcs, DiscreteInventory items, Vector3D near)
        {
            var list = new List<TakerRoom>();
            var ranked = RankLogisticsEnds(lcs, items, near);   // covering the most of it first, nearest on a tie
            for (int i = 0; i < ranked.Count; i++)
            {
                var bid = ranked[i];
                double room = bid.BidInventory != null ? (double)bid.BidInventory.TotalInventoryVolume(AutomataSession.Instance) : 0;
                list.Add(new TakerRoom { Bid = bid, Room = room > 0 ? room : double.MaxValue });
            }
            return list;
        }

        // The taker with the most room left that the drone can dock at (any with a port, when none has room left)
        private static TakerRoom TakerFor(List<TakerRoom> takers, Bid drone, out IOLocationData port)
        {
            port = null;
            TakerRoom best = null, fallback = null;
            IOLocationData bestPort = null, fallbackPort = null;
            for (int i = 0; i < takers.Count; i++)
            {
                var p = PortFor(takers[i].Bid.DockSite, takers[i].Bid.IOLocationData, drone);
                if (p == null) continue;
                if (fallback == null) { fallback = takers[i]; fallbackPort = p; }
                if (takers[i].Room > 0 && (best == null || takers[i].Room > best.Room)) { best = takers[i]; bestPort = p; }
            }
            if (best != null) { port = bestPort; return best; }
            port = fallbackPort;
            return fallback;
        }

        private static double TripVolume(Bid drone)
        {
            return Math.Max(0.0, (double)drone.MaxCargoVolume);
        }

        /// <summary>
        /// Grind: the job's blocks are shared over the drones that bid, ranked (profile, tool facing, distance, fit), so the
        /// returned parts fit: each drone takes blocks while its cargo has room for their share of the job's returns; a
        /// block returning more than one drone holds goes to several drones in turn, each grinding until full. Each drone
        /// unloads at the taker with the most room left that it can dock at. Blocks no drone has room for stay: the
        /// construction computer makes new jobs of them after the reports.
        /// </summary>
        private bool AssignGrind(OpenAuction open, List<Bid> drones, List<Bid> lcs, out Dictionary<long, List<Task>> assignments,
                                 out long droneId, out uint droneBid, out string why)
        {
            assignments = null; droneId = 0; droneBid = 0;
            var a = open.Auction;
            var takers = Takers(lcs, a.ComponentsInventory, a.PositionData.ToVector3D());
            if (takers.Count == 0) { why = Loc.T(StrNoLcRoomForParts); return false; }
            // The blocks: NavigateToolTo, GrindBlock, BackOut
            var blocks = new List<Task[]>();
            if (open.Job.Tasks != null)
                for (int i = 0; i + 1 < open.Job.Tasks.Count; i++)
                {
                    if (open.Job.Tasks[i].Type != TaskType.NavigateToolTo || open.Job.Tasks[i + 1].Type != TaskType.GrindBlock) continue;
                    bool back = i + 2 < open.Job.Tasks.Count && open.Job.Tasks[i + 2].Type == TaskType.BackOut;
                    blocks.Add(back ? new[] { open.Job.Tasks[i], open.Job.Tasks[i + 1], open.Job.Tasks[i + 2] } : new[] { open.Job.Tasks[i], open.Job.Tasks[i + 1] });
                    i += back ? 2 : 1;
                }
            if (blocks.Count == 0) { why = Loc.T(StrTheJobHasNoBlocks); return false; }
            double total = (double)a.TotalVolume;
            if (total <= 0 && a.ComponentsInventory != null) total = (double)a.ComponentsInventory.TotalInventoryVolume(AutomataSession.Instance);
            double perBlock = total > 0 ? total / blocks.Count : 0;
            // Drones that can unload somewhere, best first
            var ranked = SelectWinningDrones(drones, a);
            var crew = new List<Bid>();
            var ports = new Dictionary<long, IOLocationData>();
            var unloadAt = new Dictionary<long, long>();
            for (int i = 0; i < ranked.Count && crew.Count < MAX_POOLED_DRONES; i++)
            {
                IOLocationData port;
                var taker = TakerFor(takers, ranked[i], out port);
                if (taker == null) continue;
                crew.Add(ranked[i]);
                ports[ranked[i].EntityId] = port;
                unloadAt[ranked[i].EntityId] = taker.Bid.EntityId;
                taker.Room -= TripVolume(ranked[i]);
            }
            if (crew.Count == 0) { why = Loc.F(StrNoConnectorForRoom, DockFit.Describe(takers[0].Bid.DockSite)); return false; }
            var result = new Dictionary<long, List<Task>>();
            int d = 0;
            double room = TripVolume(crew[0]);
            int b = 0, done = 0;
            double need = perBlock;
            while (b < blocks.Count && d < crew.Count)
            {
                var drone = crew[d];
                List<Task> tasks;
                if (!result.TryGetValue(drone.EntityId, out tasks)) { tasks = new List<Task>(); result[drone.EntityId] = tasks; }
                bool partial = perBlock > 0 && need > room + 1e-6;   // more than its cargo holds: grind until full, the next drone goes on
                var unit = blocks[b];
                for (int k = 0; k < unit.Length; k++)
                {
                    var t = unit[k];
                    if (t.Type == TaskType.GrindBlock && partial) t.Completion = ToolTaskCompletionTrigger.DroneInventoryFull;
                    tasks.Add(t);
                }
                if (partial)
                {
                    need -= room;
                    room = 0;
                }
                else
                {
                    room -= need;
                    b++;
                    done++;
                    need = perBlock;
                }
                // Full (or every drone so far has nothing left for the next block): the next drone
                if (room <= 1e-6 || (perBlock > 0 && room < need * 0.25 && d + 1 < crew.Count))
                {
                    d++;
                    if (d < crew.Count) room = TripVolume(crew[d]);
                }
            }
            foreach (var kv in result)
            {
                kv.Value.Add(DockTask(ports[kv.Key]));
                kv.Value.Add(Task.Unload());
                kv.Value.Add(Task.ReturnHome());
            }
            assignments = result;
            foreach (var k in result.Keys) { droneId = k; break; }
            droneBid = BidIdOf(drones, droneId);
            var lcsUsed = new HashSet<long>();
            foreach (var k in result.Keys) lcsUsed.Add(unloadAt[k]);
            why = Loc.F(StrOfBlockSOverDrone, done, blocks.Count, result.Count,
                                lcsUsed.Count, done < blocks.Count ? Loc.T(StrTheRestNoRoomOn) : "");
            return true;
        }

        /// <summary>
        /// Mine: the job's amount is an estimate, not a cap, so it goes to as many drones as it takes to carry it (each mines
        /// until full or its spots run out of the ore): the best-ranked drones until their cargo adds up to the job's ore
        /// volume, at most MAX_POOLED_DRONES. Each one bores from its own start, MINER_SPACING apart around the planned one
        /// (same deposit), and unloads everything at the taker with the most room left it can dock at (refineries take the
        /// stone). Ice: the ice to a computer with a working O2/H2 generator, the rest to the best refinery that bid
        /// (prototech, then refinery / industrial, then basic: a basic one can't take every ore); one stop when the
        /// generator's computer has as good a refinery, or no other has one.
        /// </summary>
        private bool AssignMine(OpenAuction open, List<Bid> drones, List<Bid> lcs, out Dictionary<long, List<Task>> assignments,
                                out long droneId, out uint droneBid, out string why)
        {
            assignments = null; droneId = 0; droneBid = 0;
            var a = open.Auction;
            var site = new List<Task>();
            if (open.Job.Tasks != null)
                for (int i = 0; i < open.Job.Tasks.Count; i++)
                {
                    var t = open.Job.Tasks[i];
                    if (t.Type == TaskType.NavigateToolTo || t.Type == TaskType.Mine || t.Type == TaskType.BackOut) site.Add(t);
                }
            if (site.Count == 0) { why = Loc.T(StrTheJobHasNoSite); return false; }
            var takers = Takers(lcs, a.ComponentsInventory, a.PositionData.ToVector3D());
            if (takers.Count == 0) { why = Loc.T(StrNoLogisticsComputerTakesThe); return false; }
            bool ice = IsIceJob(a);
            double total = (double)a.TotalVolume;
            if (total <= 0 && a.ComponentsInventory != null) total = (double)a.ComponentsInventory.TotalInventoryVolume(AutomataSession.Instance);
            var ranked = SelectWinningDrones(drones, a);
            var result = new Dictionary<long, List<Task>>();
            var lcsUsed = new HashSet<long>();
            int twoStops = 0;
            double carried = 0;
            int oreLeft = OreKg(a.ComponentsInventory);   // kg still to be mined: each drone stops there (or when full)
            for (int i = 0; i < ranked.Count && result.Count < MAX_POOLED_DRONES && (result.Count == 0 || carried < total); i++)
            {
                IOLocationData port, orePort = null;
                var taker = ice ? IceTakerFor(takers, ranked[i], out port) : TakerFor(takers, ranked[i], out port);
                if (taker == null) continue;
                var oreTaker = ice ? RefineryTakerFor(takers, ranked[i], taker, out orePort) : null;
                var tasks = new List<Task>();
                int slot = result.Count;
                for (int k = 0; k < site.Count; k++)
                    tasks.Add(site[k].Type == TaskType.Mine ? WithOreAmount(site[k], oreLeft) : slot == 0 || site[k].Type != TaskType.NavigateToolTo ? site[k] : OffsetSite(site[k], slot));
                tasks.Add(DockTask(port));
                tasks.Add(Task.Unload(a.ComponentsInventory));   // every ore aboard, or (another unload follows) the ice only
                if (oreTaker != null)
                {
                    tasks.Add(DockTask(orePort));
                    tasks.Add(Task.Unload());                     // the rest
                    lcsUsed.Add(oreTaker.Bid.EntityId);
                    twoStops++;
                }
                tasks.Add(Task.ReturnHome());
                result[ranked[i].EntityId] = tasks;
                lcsUsed.Add(taker.Bid.EntityId);
                carried += TripVolume(ranked[i]);
                if (total > 0) oreLeft = Math.Max(1, oreLeft - (int)Math.Min(int.MaxValue / 2, TripVolume(ranked[i]) / total * OreKg(a.ComponentsInventory)));
                taker.Room -= TripVolume(ranked[i]);
            }
            if (result.Count == 0) { why = Loc.F(StrNoConnectorForOre, DockFit.Describe(takers[0].Bid.DockSite)); return false; }
            assignments = result;
            foreach (var k in result.Keys) { droneId = k; break; }
            droneBid = BidIdOf(drones, droneId);
            why = Loc.F(StrDroneSOfM3Of, result.Count, carried, total, lcsUsed.Count) + (twoStops > 0 ? Loc.F(StrIceTwoStops, twoStops) : "");
            return true;
        }

        private static int OreKg(DiscreteInventory inv)
        {
            long kg = 0;
            if (inv != null)
                foreach (var item in inv.GetAllItems()) kg += item.Value;
            return (int)Math.Min(int.MaxValue / 2, kg);
        }

        // The Mine task with this drone's share of the ordered ore (the drone ends the bore when that much is aboard)
        private static Task WithOreAmount(Task mine, int kg)
        {
            if (mine.Inventory == null) return mine;
            var inv = new DiscreteInventory();
            foreach (var item in mine.Inventory.GetAllItems()) { inv.AddItem(item.Key, Math.Max(1, kg)); break; }
            mine.Inventory = inv;
            return mine;
        }

        // The k-th extra miner's bore start: around the planned one, across the bore line, MINER_SPACING apart (the drone
        // keeps the offset for an angled bore: PlanMineSite)
        private static Task OffsetSite(Task nav, int k)
        {
            Vector3D p = nav.Position.ToVector3D(), ap = nav.ApproachFrom.ToVector3D();
            Vector3D axis = ap - p;
            if (axis.LengthSquared() < 1e-4) axis = Vector3D.Up;
            axis.Normalize();
            Vector3D u = Vector3D.CalculatePerpendicularVector(axis), v = Vector3D.Cross(axis, u);
            double ang = (k - 1) * Math.PI / 3;   // 6 around the first ring, then the next ring
            double r = MINER_SPACING * (1 + (k - 1) / 6);
            Vector3D off = (u * Math.Cos(ang) + v * Math.Sin(ang)) * r;
            nav.Position = Vector3DData.FromVector3D(p + off);
            nav.ApproachFrom = Vector3DData.FromVector3D(ap + off);
            return nav;
        }

        private static bool IsIceJob(Auction a)
        {
            if (a.ComponentsInventory == null) return false;
            bool any = false;
            foreach (var item in a.ComponentsInventory.GetAllItems())
            {
                if (item.Key.String != "Ore/Ice") return false;
                any = true;
            }
            return any;
        }

        // Ice: a taker with a working O2/H2 generator the drone can dock at (most room); any taker when none has one
        private static TakerRoom IceTakerFor(List<TakerRoom> takers, Bid drone, out IOLocationData port)
        {
            port = null;
            TakerRoom best = null;
            for (int i = 0; i < takers.Count; i++)
            {
                if (!takers[i].Bid.GasGenerators) continue;
                var p = PortFor(takers[i].Bid.DockSite, takers[i].Bid.IOLocationData, drone);
                if (p == null) continue;
                if (best == null || takers[i].Room > best.Room) { best = takers[i]; port = p; }
            }
            return best ?? TakerFor(takers, drone, out port);
        }

        // Ice: where the rest goes - the best refinery tier among the takers the drone can dock at (most room on a tie);
        // null when the ice taker's own refinery is as good (one stop) or nobody has a refinery
        private static TakerRoom RefineryTakerFor(List<TakerRoom> takers, Bid drone, TakerRoom iceTaker, out IOLocationData port)
        {
            port = null;
            TakerRoom best = null;
            for (int i = 0; i < takers.Count; i++)
            {
                var t = takers[i];
                if (t == iceTaker || t.Bid.RefineryTier == 0 || t.Bid.RefineryTier <= iceTaker.Bid.RefineryTier) continue;
                if (best != null && (t.Bid.RefineryTier < best.Bid.RefineryTier || (t.Bid.RefineryTier == best.Bid.RefineryTier && t.Room <= best.Room))) continue;
                var p = PortFor(t.Bid.DockSite, t.Bid.IOLocationData, drone);
                if (p == null) continue;
                best = t;
                port = p;
            }
            return best;
        }

        #endregion

        // Designator jobs: one drone of the job's profile (the bids of other drones were refused: RequiredProfile), the
        // player's tasks, then home
        private bool AssignDesignator(OpenAuction open, List<Bid> drones, out Dictionary<long, List<Task>> assignments,
                                      out long droneId, out uint droneBid, out string why)
        {
            assignments = null; droneId = 0; droneBid = 0;
            string profile = DesignatorTakerName(open.Auction.JobType);
            var drone = BestDrone(drones, open.Auction.PositionData.ToVector3D(), open.Auction.JobType);
            if (drone == null) { why = Loc.F(StrNoProfileBid, profile); return false; }
            var tasks = new List<Task>(open.Job.Tasks ?? new List<Task>());
            tasks.Add(Task.ReturnHome());
            assignments = new Dictionary<long, List<Task>> { { drone.EntityId, tasks } };
            droneId = drone.EntityId;
            droneBid = drone.BidId;
            why = Loc.F(StrTaskS, profile, tasks.Count);
            return true;
        }

        private void QueueWinner(OpenAuction open, Dictionary<long, List<Task>> assignments, uint droneBid, List<long> sources = null)
        {
            winnerOutbox.Add(new Message<AuctionWinnerAnnouncement>
            {
                Payload = new AuctionWinnerAnnouncement
                {
                    TaskAssignments = assignments,
                    CreatedTime = DateTime.UtcNow,
                    EntityId = entityId,
                    JobId = open.Auction.JobId,
                    AuctionId = open.Auction.AuctionId,
                    BidId = droneBid,
                    AnnouncerEntityId = open.JobKey.Key,
                    ComponentSources = sources ?? SourcesOf(open, assignments),
                    JobType = open.Auction.JobType,
                    SharedSite = IsSharedSite(open, assignments),
                    WeldCategory = open.Auction.WeldCategory,
                },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Drone | MessageQueue.IAIBlockType.LogisticsComputer
                                     | MessageQueue.IAIBlockType.ConstructionComputer
                                     | MessageQueue.IAIBlockType.MiningSurveyor,         // the announcer stops repeating the job
                Channel = Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT,
            });
        }

        /// <summary>
        /// Weld / grind / mine: other drones work (or may work) the site - the job goes to several drones, or another job
        /// of the kind (build / mine) awarded within SHARED_SITE_RADIUS still has drones out.
        /// </summary>
        private bool IsSharedSite(OpenAuction open, Dictionary<long, List<Task>> assignments)
        {
            var type = open.Auction.JobType;
            if (type != JobType.Weld && type != JobType.Grind && type != JobType.MineOre) return false;
            if (assignments != null && assignments.Count > 1) return true;
            Vector3D at = open.Auction.PositionData.ToVector3D();
            foreach (var kv in activeJobs)
            {
                var j = kv.Value;
                if (kv.Key.Equals(open.JobKey) || j.Drones <= 0 || (j.Type != JobType.Weld && j.Type != JobType.Grind && j.Type != JobType.MineOre)) continue;
                if ((j.Type == JobType.MineOre) != (type == JobType.MineOre)) continue;   // a mine and a build site don't share
                if (Vector3D.DistanceSquared(j.Position, at) <= SHARED_SITE_RADIUS * SHARED_SITE_RADIUS) return true;
            }
            return false;
        }

        private void SendWinners()
        {
            for (int i = winnerOutbox.Count - 1; i >= 0; i--)
            {
                var msg = winnerOutbox[i];
                if (msg.Delivered) { winnerOutbox.RemoveAt(i); continue; }
                if (messaging.InFlight(msg)) continue;   // a reader may still take it
                if ((DateTime.UtcNow - msg.Payload.CreatedTime).TotalSeconds > 30)
                {
                    Log.Warning("Orchestrator {0}: winners of auction {1} never delivered", entityId, msg.Payload.AuctionId);
                    winnerOutbox.RemoveAt(i);
                    continue;
                }
                var error = messaging.BroadcastMessage(antennaReg, msg, false);
                if (error == ErrorCode.None)
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: winners of auction {1} (job {2}) sent: {3}", entityId, msg.Payload.AuctionId, msg.Payload.JobId,
                              DescribeAssignments(msg.Payload.TaskAssignments));
            }
        }

        private static int FailedHoldSeconds
        {
            get { var config = AutomataSession.GetConfig(); return config != null ? config.OrchestratorServerSettings.FailedJobHoldSeconds : 60; }
        }

        private void QueueFailure(KeyValuePair<long, uint> jobKey, uint jobId, uint auctionId, string reason, int holdSeconds)
        {
            failureOutbox.Add(new Message<JobAnnouncementFailed>
            {
                Payload = new JobAnnouncementFailed
                {
                    AnnouncerEntityId = jobKey.Key,
                    JobId = jobId,
                    AuctionId = auctionId,
                    Reason = reason,
                    HoldSeconds = holdSeconds,
                    OrchestratorEntityId = entityId,
                },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.ConstructionComputer | MessageQueue.IAIBlockType.LogisticsComputer
                                     | MessageQueue.IAIBlockType.MiningSurveyor,
                Channel = Channel.JOB_ANNOUNCEMENT_FAILED,
            });
        }

        // Failures go out until someone takes them (30 s at most, like winners); sent again once the last copy has expired
        private void SendFailures()
        {
            for (int i = failureOutbox.Count - 1; i >= 0; i--)
            {
                var msg = failureOutbox[i];
                if (msg.Delivered) { failureOutbox.RemoveAt(i); continue; }
                if (messaging.InFlight(msg)) continue;
                if (DateTime.UtcNow > TimeUtil.TimestampToDateTime(msg.CreatedAt).AddSeconds(30)) { failureOutbox.RemoveAt(i); continue; }
                var error = messaging.BroadcastMessage(antennaReg, msg, false);
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: failure of job {1} to {2}: {3}", entityId, msg.Payload.JobId, msg.Payload.AnnouncerEntityId,
                          error == ErrorCode.None ? "sent" : error.ToString());
            }
        }

        // Another orchestrator (owner / faction) auctions a job this one knows too: the one with the lower entity id keeps
        // it, the other drops its queue entry / auction quietly and ignores the job's announcements while it is assigned
        private readonly List<Message<Auction>> peerAuctionInbox = new List<Message<Auction>>();

        // An announcer took a job back (mining order removed): its queue entry / auction ends, the logistics computers
        // that bid are told (failure with the auction id) so their promises end at once
        private readonly List<Message<JobStatusReport>> withdrawInbox = new List<Message<JobStatusReport>>();

        private void ReadWithdrawals(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, Channel.JOB_WITHDRAWN, withdrawInbox, 50, true, PayloadType.JobStatusReport);
            for (int i = 0; i < withdrawInbox.Count; i++)
            {
                var w = withdrawInbox[i].Payload;
                if (w == null || w.AnnouncerEntityId != withdrawInbox[i].SenderId || !AnchorDirectory.IsAllowed(block.OwnerId, withdrawInbox[i].SenderOwnerId)) continue;
                var key = new KeyValuePair<long, uint>(w.AnnouncerEntityId, w.JobId);
                bool had = false;
                for (int q = jobQueue.Count - 1; q >= 0; q--)
                    if (jobQueue[q].Key.Equals(key)) { jobQueue.RemoveAt(q); had = true; }
                closing.Clear();
                foreach (var open in auctions.Values)
                    if (open.JobKey.Equals(key)) closing.Add(open.Auction.AuctionId);
                for (int c = 0; c < closing.Count; c++)
                {
                    QueueFailure(new KeyValuePair<long, uint>(0, w.JobId), w.JobId, closing[c], Loc.T(StrWithdrawnByItsAnnouncer), 0);
                    auctions.Remove(closing[c]);
                    _auctionJobs.Remove(closing[c]);
                    had = true;
                }
                closing.Clear();
                knownJobs.Remove(key);
                if (had)
                {
                    AddEvent(Loc.F(StrJobWithdrawn, w.Reason));
                    Log.Info("Orchestrator {0}: job {1} of {2} withdrawn: {3}", entityId, w.JobId, w.AnnouncerEntityId, w.Reason);
                }
            }
            withdrawInbox.Clear();
        }

        private void ReadPeerAuctions(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, Channel.ORCHESTRATOR_AUCTION_START, peerAuctionInbox, 50, true, PayloadType.Auction);
            for (int i = 0; i < peerAuctionInbox.Count; i++)
            {
                var m = peerAuctionInbox[i];
                var a = m.Payload;
                if (a == null || a.AnnouncerEntityId == 0 || m.SenderId == entityId || m.SenderId > entityId) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, m.SenderOwnerId)) continue;
                var key = new KeyValuePair<long, uint>(a.AnnouncerEntityId, a.JobId);
                bool had = false;
                for (int q = jobQueue.Count - 1; q >= 0; q--)
                    if (jobQueue[q].Key.Equals(key)) { jobQueue.RemoveAt(q); had = true; }
                foreach (var open in auctions.Values)
                    if (open.JobKey.Equals(key)) { closing.Add(open.Auction.AuctionId); had = true; }
                for (int c = 0; c < closing.Count; c++)
                {
                    // announcer 0: only the logistics computers that bid act on it (their promises for this auction end)
                    QueueFailure(new KeyValuePair<long, uint>(0, a.JobId), a.JobId, closing[c], Loc.F(StrLeftToOrchestrator, m.SenderId), 0);
                    auctions.Remove(closing[c]);
                    _auctionJobs.Remove(closing[c]);
                }
                closing.Clear();
                knownJobs[key] = now + JOB_MEMORY_TICKS;
                if (had)
                {
                    AddEvent(Loc.F(StrJobLeftToOrchestrator, Nicknames.Of(m.SenderId)));
                    Log.Info("Orchestrator {0}: job {1} from {2} left to orchestrator {3} (lower id, auction {4})", entityId, a.JobId,
                             a.AnnouncerEntityId, m.SenderId, a.AuctionId);
                }
            }
            peerAuctionInbox.Clear();
        }

        // How the drones' shares of awarded jobs ended: counted; an unfinished one is taken again soon when re-announced
        private readonly List<Message<JobStatusReport>> reportInbox = new List<Message<JobStatusReport>>();
        private readonly List<Message<JobStatusReport>> keepaliveInbox = new List<Message<JobStatusReport>>();
        private readonly Util.DroneWatch droneWatch = new Util.DroneWatch();
        private int completedCount, abortedCount;

        // The drones on its own jobs (designator orders): unheard for a minute, said once (DroneWatch)
        private void ReadKeepalives(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, Channel.JOB_KEEPALIVE, keepaliveInbox, 50, true, PayloadType.JobStatusReport);
            for (int i = 0; i < keepaliveInbox.Count; i++)
            {
                var r = keepaliveInbox[i].Payload;
                if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != keepaliveInbox[i].SenderId
                    || !AnchorDirectory.IsAllowed(block.OwnerId, keepaliveInbox[i].SenderOwnerId)) continue;
                droneWatch.Heard(r.DroneEntityId, r.JobId, now, AddEvent);
                NoteSeen(r.DroneEntityId, AutomataEntityType.DroneControllerBlock, now);
            }
            keepaliveInbox.Clear();
            droneWatch.Check(now, block, settings.UseBroadcastController, "Orchestrator {0}", entityId, AddEvent);
        }

        private void ReadJobReports(int now)
        {
            for (int c = 0; c < 2; c++)
            {
                messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, c == 0 ? Channel.TASKS_COMPLETED : Channel.TASKS_ABORTED, reportInbox, 50, true,
                                       PayloadType.JobStatusReport);
                for (int i = 0; i < reportInbox.Count; i++)
                {
                    var r = reportInbox[i].Payload;
                    if (r == null || r.OrchestratorEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId) continue;
                    if (!AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                    NoteSeen(r.DroneEntityId, AutomataEntityType.DroneControllerBlock, now);
                    if (r.AnnouncerEntityId == entityId) droneWatch.Done(r.DroneEntityId);
                    var key = new KeyValuePair<long, uint>(r.AnnouncerEntityId, r.JobId);
                    ActiveJob active;
                    if (activeJobs.TryGetValue(key, out active)) active.Drones--;
                    if (r.Outcome == JobOutcome.Completed) completedCount++;
                    else
                    {
                        abortedCount++;
                        int until;
                        if (knownJobs.TryGetValue(key, out until) && until != int.MaxValue) knownJobs[key] = now + RETRY_MEMORY_TICKS;
                    }
                    if (r.AnnouncerEntityId == entityId)   // a player's order: its marker goes (recalled jobs end here too)
                        EndPlayerJob(r.JobId, string.IsNullOrEmpty(r.Reason) ? Loc.F(StrDroneOutcome, r.DroneEntityId, r.Outcome) : Loc.F(StrDroneOutcomeReason, r.DroneEntityId, r.Outcome, r.Reason));
                    string label = r.JobType.Label(r.WeldCategory);
                    AddEvent(string.IsNullOrEmpty(r.Reason) ? Loc.F(StrJobDroneOutcome, label, Nicknames.Of(r.DroneEntityId), r.Outcome)
                                                             : Loc.F(StrJobDroneOutcomeReason, label, Nicknames.Of(r.DroneEntityId), r.Outcome, r.Reason));
                    Log.Info("Orchestrator {0}: job {1} ({8}) from {2}: drone {3} {4} ({5}), {6}/{7} block(s)", entityId, r.JobId, r.AnnouncerEntityId,
                             r.DroneEntityId, r.Outcome, r.Reason, r.BlocksDone, r.BlocksTotal, label);
                }
                reportInbox.Clear();
            }
        }

        private readonly HashSet<KeyValuePair<long, uint>> ignoreLogged = new HashSet<KeyValuePair<long, uint>>();

        private void ForgetJobs(int now)
        {
            forget.Clear();
            foreach (var kv in knownJobs)
            {
                // int.MaxValue = queued / auctioned "until it ends": checked every pass, so an entry whose queue entry or
                // auction vanished (dormancy, a failed start, a load) is forgotten instead of ignoring the job forever
                if (now < kv.Value && kv.Value != int.MaxValue) continue;
                bool running = false;
                foreach (var open in auctions.Values) if (open.JobKey.Equals(kv.Key)) { running = true; break; }
                for (int q = 0; q < jobQueue.Count && !running; q++) running = jobQueue[q].Key.Equals(kv.Key);
                if (!running) forget.Add(kv.Key);
            }
            for (int i = 0; i < forget.Count; i++)
            {
                knownJobs.Remove(forget[i]);
                ignoreLogged.Remove(forget[i]);
                // An awarded player job never reported (drone lost): its marker goes
                if (IsPlayerJob(forget[i])) EndPlayerJob(forget[i].Value, Loc.T(StrNoReportFromTheDrone));
            }
        }
        #endregion

        #region Save / load
        // Everything the loop needs to carry on after a world load; times relative to the save
        private OrchestratorState CaptureState()
        {
            var s = new OrchestratorState
            {
                AuctionCounter = auctionCounter,
                PlayerJobCounter = playerJobCounter,
                CommissionedTicks = commissionedTicks,
                MessageCounter = messageCounter,
                Assigned = assignedCount,
                Completed = completedCount,
                Aborted = abortedCount,
                Dropped = droppedCount,
                Failed = failedCount,
            };
            for (int i = 0; i < jobQueue.Count; i++)
                s.Queue.Add(new QueuedJobState { Sender = jobQueue[i].Key.Key, SenderOwner = jobQueue[i].SenderOwner, Announcement = jobQueue[i].Announcement,
                                                 AgeTicks = BlockSave.Age(jobQueue[i].Received) });
            foreach (var open in auctions.Values)
                s.Auctions.Add(new AuctionState
                {
                    Auction = open.Auction,
                    Job = open.Job,
                    Announcer = open.JobKey.Key,
                    JobId = open.JobKey.Value,
                    AgeTicks = BlockSave.Age(open.Started),
                    ExpiresInSeconds = BlockSave.SecondsLeft(open.Auction.ExpirationTime),
                    Bids = new List<Bid>(open.Bids),
                });
            foreach (var kv in knownJobs)
                s.KnownJobs.Add(new KnownJobState { Announcer = kv.Key.Key, JobId = kv.Key.Value,
                                                    TicksLeft = kv.Value == int.MaxValue ? int.MaxValue : Math.Max(1, kv.Value - BlockSave.Now) });
            for (int i = 0; i < winnerOutbox.Count; i++) s.Winners.Add(winnerOutbox[i].Payload);
            for (int i = 0; i < failureOutbox.Count; i++) s.Failures.Add(failureOutbox[i].Payload);
            CapturePlayerJobs(s);
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: state saved: {1} queued, {2} auction(s), {3} known job(s), {4} unsent", entityId, s.Queue.Count,
                      s.Auctions.Count, s.KnownJobs.Count, s.Winners.Count + s.Failures.Count);
            return s;
        }

        private void RestoreState(OrchestratorState s)
        {
            auctionCounter = s.AuctionCounter;
            playerJobCounter = s.PlayerJobCounter;
            if (s.CommissionedTicks != 0) commissionedTicks = s.CommissionedTicks;
            messageCounter = s.MessageCounter;
            assignedCount = s.Assigned;
            completedCount = s.Completed;
            abortedCount = s.Aborted;
            droppedCount = s.Dropped;
            failedCount = s.Failed;
            RestorePlayerJobs(s);
            if (s.KnownJobs != null)
                for (int i = 0; i < s.KnownJobs.Count; i++)
                {
                    var k = s.KnownJobs[i];
                    // TicksLeft: frames left to remember it (int.MaxValue: while queued / auctioned); older saves: age
                    int until = k.TicksLeft == int.MaxValue ? int.MaxValue
                              : k.TicksLeft > 0 ? BlockSave.Now + k.TicksLeft : BlockSave.FrameFromAge(k.AgeTicks) + JOB_MEMORY_TICKS;
                    knownJobs[new KeyValuePair<long, uint>(k.Announcer, k.JobId)] = until;
                }
            if (s.Queue != null)
                for (int i = 0; i < s.Queue.Count; i++)
                {
                    var q = s.Queue[i];
                    if (q.Announcement == null) continue;
                    jobQueue.Add(new QueuedJob { Announcement = q.Announcement, Key = new KeyValuePair<long, uint>(q.Sender, q.Announcement.JobId),
                                                 SenderOwner = q.SenderOwner, Received = BlockSave.FrameFromAge(q.AgeTicks) });
                }
            if (s.Auctions != null)
                for (int i = 0; i < s.Auctions.Count; i++)
                {
                    var a = s.Auctions[i];
                    if (a.Auction == null) continue;
                    // The clock went on while the world was saved: the auction gets the time it had left
                    a.Auction.ExpirationTime = BlockSave.FromSecondsLeft(Math.Max(a.ExpiresInSeconds, 10));
                    var open = new OpenAuction
                    {
                        Auction = a.Auction,
                        Job = a.Job ?? new Job { JobId = a.JobId, JobType = a.Auction.JobType },
                        JobKey = new KeyValuePair<long, uint>(a.Announcer, a.JobId),
                        Started = BlockSave.FrameFromAge(a.AgeTicks),
                        LastBroadcast = int.MinValue / 2,
                        Message = AuctionMessage(a.Auction),
                    };
                    if (a.Bids != null) open.Bids.AddRange(a.Bids);
                    auctions[a.Auction.AuctionId] = open;
                    _auctionJobs[a.Auction.AuctionId] = open.Job;
                }
            if (s.Winners != null)
                for (int i = 0; i < s.Winners.Count; i++)
                {
                    var w = s.Winners[i];
                    if (w == null) continue;
                    w.CreatedTime = DateTime.UtcNow;
                    winnerOutbox.Add(new Message<AuctionWinnerAnnouncement>
                    {
                        Payload = w,
                        MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                        CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                        SenderId = entityId,
                        SenderOwnerId = block.OwnerId,
                        RecipientBlockType = MessageQueue.IAIBlockType.Drone | MessageQueue.IAIBlockType.LogisticsComputer
                                             | MessageQueue.IAIBlockType.ConstructionComputer | MessageQueue.IAIBlockType.MiningSurveyor,
                        Channel = Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT,
                    });
                }
            if (s.Failures != null)
                for (int i = 0; i < s.Failures.Count; i++)
                {
                    var f = s.Failures[i];
                    if (f == null) continue;
                    failureOutbox.Add(new Message<JobAnnouncementFailed>
                    {
                        Payload = f,
                        MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                        CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                        SenderId = entityId,
                        SenderOwnerId = block.OwnerId,
                        RecipientBlockType = MessageQueue.IAIBlockType.ConstructionComputer | MessageQueue.IAIBlockType.LogisticsComputer
                                             | MessageQueue.IAIBlockType.MiningSurveyor,
                        Channel = Channel.JOB_ANNOUNCEMENT_FAILED,
                    });
                }
            Log.Info("Orchestrator {0}: state restored: {1} queued, {2} auction(s), {3} known job(s), {4} unsent",
                     entityId, jobQueue.Count, auctions.Count, knownJobs.Count, winnerOutbox.Count + failureOutbox.Count);
        }
        #endregion

        #region Helpers
        /// <summary>
        /// The connector of a logistics end the drone can dock at (TODO/14: orientation against gravity only; anything
        /// fits without natural gravity), or null. Ends without a connector list, or drones without faces (older
        /// blocks), keep the end's one connector, unchecked.
        /// </summary>
        private static IOLocationData PortFor(DockSite site, IOLocationData preferred, Bid drone)
        {
            if (site == null || site.Ports == null || site.Ports.Count == 0 || drone == null || (drone.DockFaces == null && drone.SmallDockFaces == null))
                return preferred;
            Vector3D near = drone.IOLocationData != null ? drone.IOLocationData.PositionData.ToVector3D()
                          : preferred != null ? preferred.PositionData.ToVector3D() : Vector3D.Zero;
            var p = DockFit.Pick(site, drone.DockFaces, drone.SmallDockFaces, near, drone.DockedConnectorId);
            return p != null ? p.ToIO() : null;
        }

        // The assignment is retried every step while an auction is open: a drone that fits no connector is logged once
        private static readonly HashSet<long> fitLogged = new HashSet<long>();
        private static bool FirstFitLog(uint auctionId, long droneId)
        {
            if (fitLogged.Count > 4096) fitLogged.Clear();
            return fitLogged.Add(((long)auctionId * 486187739L) ^ droneId);
        }

        private static string Faces(Bid drone)
        {
            if (drone == null) return "?";
            int std = drone.DockFaces != null ? drone.DockFaces.Count : 0;   // empty lists arrive as null
            int small = drone.SmallDockFaces != null ? drone.SmallDockFaces.Count : 0;
            if (std == 0 && small == 0) return "none";
            if (std == 0) { var s = new System.Text.StringBuilder("small "); for (int i = 0; i < small; i++) s.Append(i > 0 ? "/" : "").Append(DockFit.FaceName(drone.SmallDockFaces[i])); return s.ToString(); }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < drone.DockFaces.Count; i++) sb.Append(i > 0 ? "/" : "").Append(DockFit.FaceName(drone.DockFaces[i]));
            if (small > 0)
            {
                sb.Append(drone.DockFaces.Count > 0 ? ", small " : "small ");
                for (int i = 0; i < small; i++) sb.Append(i > 0 ? "/" : "").Append(DockFit.FaceName(drone.SmallDockFaces[i]));
            }
            return sb.ToString();
        }

        private static Task DockTask(IOLocationData io)
        {
            Vector3D at = io.PositionData.ToVector3D();
            Vector3D axis = Vector3D.Transform(Vector3D.Forward, io.OrientationData.ToQuaternionD());
            if (axis.LengthSquared() < 0.5) axis = Vector3D.Forward;
            return Task.DockAt(io.EntityId, at, at + axis * Construction.ConstructionComputer.APPROACH_DISTANCE);
        }

        // The logistics bid covering the most of 'wanted' (items it offers / room it has), the nearest to 'near' on a tie
        private static Bid BestLogisticsBid(List<Bid> lcs, DiscreteInventory wanted, Vector3D near)
        {
            Bid best = null;
            double bestCover = 0, bestDistance = double.MaxValue;
            for (int i = 0; i < lcs.Count; i++)
            {
                var bid = lcs[i];
                if (bid.IOLocationData == null) continue;
                var common = Intersect(wanted, bid.BidInventory);
                double cover = common != null ? common.GetTotalItemCount() : 0;
                if (cover <= 0) continue;
                double d = Vector3D.DistanceSquared(near, bid.IOLocationData.PositionData.ToVector3D());
                if (cover > bestCover || (cover == bestCover && d < bestDistance)) { best = bid; bestCover = cover; bestDistance = d; }
            }
            return best;
        }

        private Bid BestDrone(List<Bid> drones, Vector3D firstStop, JobType type)
        {
            Bid best = null;
            double bestScore = double.MinValue;
            var preferred = PreferredProfile(type);
            for (int i = 0; i < drones.Count; i++)
            {
                double s = ComputeDroneBidScore(drones[i], firstStop, 0, 0) + ProfileBonus(drones[i], preferred);
                if (s > bestScore) { bestScore = s; best = drones[i]; }
            }
            return best;
        }

        // BestDrone among those that can dock at one of the logistics end's connectors ('port': that connector)
        private Bid BestDockingDrone(List<Bid> drones, Vector3D firstStop, JobType type, Bid lc, out IOLocationData port, Auction a = null)
        {
            port = null;
            Bid best = null;
            double bestScore = double.MinValue;
            var preferred = PreferredProfile(type);
            for (int i = 0; i < drones.Count; i++)
            {
                var p = PortFor(lc.DockSite, lc.IOLocationData, drones[i]);
                if (p == null) continue;
                if (ToolCantReach(drones[i], a)) continue;
                double s = ComputeDroneBidScore(drones[i], firstStop, 0, 0) + (a != null ? ProfileBonus(drones[i], a) : ProfileBonus(drones[i], preferred))
                         + ToolFaceBonus(drones[i], a);
                if (s > bestScore) { bestScore = s; best = drones[i]; port = p; }
            }
            return best;
        }

        // Per item, the smaller of both (null when nothing in common)
        private static DiscreteInventory Intersect(DiscreteInventory a, DiscreteInventory b)
        {
            if (a == null || b == null) return null;
            var result = new DiscreteInventory();
            foreach (var item in a.GetAllItems())
            {
                int n = Math.Min(item.Value, b.GetItemCount(item.Key));
                if (n > 0) result.AddItem(item.Key, n);
            }
            return result.IsEmpty() ? null : result;
        }

        // What one trip of the drone carries of 'items' (its cargo room and lift); null when nothing
        private static DiscreteInventory FitToDrone(DiscreteInventory items, Bid drone)
        {
            if (items == null || drone == null) return null;
            double volume = (double)items.TotalInventoryVolume(AutomataSession.Instance);
            double mass = (double)items.TotalInventoryMass(AutomataSession.Instance);
            double share = DroneTripShare(drone, volume, mass);
            if (share >= 0.999) return items;
            if (share <= 0) return null;
            var fit = new DiscreteInventory();
            foreach (var item in items.GetAllItems())
            {
                int n = (int)Math.Floor(item.Value * share);
                if (n > 0) fit.AddItem(item.Key, n);
            }
            return fit.IsEmpty() ? null : fit;
        }

        private static uint BidIdOf(List<Bid> bids, long entity)
        {
            for (int i = 0; i < bids.Count; i++) if (bids[i].EntityId == entity) return bids[i].BidId;
            return 0;
        }

        private static string Describe(DiscreteInventory items)
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

        private static string DescribeAssignments(Dictionary<long, List<Task>> assignments)
        {
            if (assignments == null || assignments.Count == 0) return "none";
            var sb = new StringBuilder();
            foreach (var kv in assignments)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append("drone ").Append(kv.Key).Append(':');
                for (int i = 0; kv.Value != null && i < kv.Value.Count && i < 12; i++) sb.Append(' ').Append(kv.Value[i].Type);
                if (kv.Value != null && kv.Value.Count > 12) sb.Append(" ... (").Append(kv.Value.Count).Append(')');
            }
            return sb.ToString();
        }

        // A log entry (terminal and LCDs): first line at the left edge, its other lines indented by the caller
        private void AddEvent(string text)
        {
            log.Add(text);
        }

        /// <summary>Terminal info panel.</summary>
        private void AppendInfo(IMyTerminalBlock b, StringBuilder target)
        {
            if (!ready) return;
            var sb = info;
            sb.Clear();
            sb.AppendLoc(StrInfoStatus, status).Append('\n');
            if (AntennaUtil.Alive(ownAntenna)) sb.AppendLoc(StrInfoAntenna, ownAntenna.CustomName, AntennaUtil.Range(ownAntenna)).Append('\n');
            else sb.AppendLoc(StrInfoNoAntenna).Append('\n');
            sb.AppendLoc(StrInfoQueued, jobQueue.Count, auctions.Count).Append('\n');
            sb.AppendLoc(StrInfoAssigned, assignedCount, failedCount, droppedCount).Append('\n');
            sb.AppendLoc(StrInfoReports, completedCount, abortedCount).Append('\n');
            foreach (var open in auctions.Values)
                sb.AppendLoc(StrInfoAuction, open.Auction.JobType, open.Bids.Count).Append('\n');
            BuildHeader();
            sb.Append('\n');
            for (int i = 0; i < header.Count; i++) sb.Append(header[i]).Append('\n');
            sb.Append(new string('-', 40)).Append('\n');
            log.AppendTo(sb);
            target.Append(sb);
        }
        #endregion

        #region Weld trips and bid ranking
        /// <summary>
        /// Splits the job into trips that fit each drone, by volume (what fits in its cargo) AND mass (what it can
        /// lift, within its player-set max load): e.g. 1000 kg for three 350 kg drones = 3 trips of a third, the
        /// best-ranked drone taking the extra trip; a refinery's bill of materials shared by several drones.
        /// Every trip carries a slice of the whole bill of materials and gets all of the job's blocks: a drone
        /// welds block after block with what it carries (completion "inventory empty" when it carries only part of
        /// the job), skipping blocks already finished by another trip.
        /// </summary>
        private Dictionary<long, List<Task>> BuildWeldTaskAssignments(List<Bid> winningDrones, List<Bid> winningLCs, Auction bidRound)
        {
            Dictionary<long, List<Task>> assignments = new Dictionary<long, List<Task>>();
            Dictionary<long, double> remainingLcVolume = BuildRemainingLcVolumeMap(winningLCs);

            DiscreteInventory requiredInventory = bidRound.ComponentsInventory ?? new DiscreteInventory();
            if (requiredInventory.IsEmpty() || winningDrones.Count == 0)
            {
                return assignments;
            }
            double requiredVolume = (double)bidRound.TotalVolume;
            if (requiredVolume <= 0.0) requiredVolume = (double)requiredInventory.TotalInventoryVolume();
            double requiredMass = (double)bidRound.TotalMass;
            if (requiredMass <= 0.0) requiredMass = (double)requiredInventory.TotalInventoryMass(AutomataSession.Instance);

            // Sequential (a projection line, each block attached to the one before): more drones at once would only wait
            // on the first one's blocks and get in its way. The best-ranked drone that takes a useful share does every trip.
            if (bidRound.Sequential)
            {
                Bid only = null;
                for (int i = 0; i < winningDrones.Count && only == null; i++)
                    if (winningDrones[i] != null && winningDrones[i].IOLocationData != null
                        && DroneTripShare(winningDrones[i], requiredVolume, requiredMass) >= MIN_TRIP_SHARE) only = winningDrones[i];
                if (only == null) return assignments;
                winningDrones = new List<Bid> { only };
            }

            double remaining = 1.0;   // share of the job not assigned yet
            int droneIndex = 0;
            int maxIterations = bidRound.Sequential ? 64 : winningDrones.Count * 16;

            while (remaining > 0.0005 && maxIterations > 0)
            {
                Bid drone = winningDrones[droneIndex];
                droneIndex = (droneIndex + 1) % winningDrones.Count;
                maxIterations--;

                if (drone == null || drone.IOLocationData == null)
                {
                    continue;
                }

                double droneShare = DroneTripShare(drone, requiredVolume, requiredMass);
                // A drone that carries next to nothing (cargo full of what an earlier job left, say) would be sent on dozens of
                // trips with every one of the job's blocks each time: not a bid worth taking
                if (droneShare < MIN_TRIP_SHARE)
                {
                    continue;
                }

                IOLocationData lcPort;
                Bid lc = SelectBestLcForDrone(drone, winningLCs, remainingLcVolume, out lcPort);
                if (lc == null || lc.IOLocationData == null)
                {
                    if (!AnyLcVolumeLeft(remainingLcVolume)) break;
                    // Volume left, but no connector this drone can dock at: the next drone
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: drone {2} (connectors {3}) fits no component source's connector", entityId,
                              bidRound.AuctionId, drone.EntityId, Faces(drone));
                    continue;
                }

                double lcRemaining = remainingLcVolume[lc.EntityId];
                double lcShare = requiredVolume > 0.0 ? lcRemaining / requiredVolume : 1.0;
                double trip = Math.Min(remaining, Math.Min(droneShare, lcShare));
                if (trip <= 0.0)
                {
                    remainingLcVolume[lc.EntityId] = 0.0;
                    continue;
                }

                List<Task> droneTasks;
                if (!assignments.TryGetValue(drone.EntityId, out droneTasks))
                {
                    droneTasks = new List<Task>();
                    assignments[drone.EntityId] = droneTasks;
                }

                DiscreteInventory tripPayload = BuildTripPayload(requiredInventory, trip);
                AppendWeldTripTasks(droneTasks, lcPort, bidRound, tripPayload, trip < 0.999);

                remaining -= trip;
                remainingLcVolume[lc.EntityId] = Math.Max(0.0, lcRemaining - trip * requiredVolume);
            }

            foreach (KeyValuePair<long, List<Task>> kv in assignments)
            {
                List<Task> droneTasks = kv.Value;
                if (droneTasks.Count == 0)
                {
                    continue;
                }
                droneTasks.Add(Task.ReturnHome());
            }

            return assignments;
        }

        // A weld job that needs no components: the best-ranked drone takes the job's work tasks (no dock, no load), then home
        private bool AssignWeldWithoutComponents(OpenAuction open, List<Bid> drones, out Dictionary<long, List<Task>> assignments,
                                                 out long droneId, out uint droneBid, out string why)
        {
            assignments = null; droneId = 0; droneBid = 0;
            var ranked = SelectWinningDrones(drones, open.Auction);
            if (ranked.Count == 0) { why = Loc.T(StrNoDroneBid); return false; }
            var tasks = new List<Task>();
            AppendWeldWork(tasks, open.Auction, false);
            if (tasks.Count == 0) { why = Loc.T(StrNoTripFitsTheBids); return false; }
            tasks.Add(Task.ReturnHome());
            droneId = ranked[0].EntityId;
            droneBid = BidIdOf(drones, droneId);
            assignments = new Dictionary<long, List<Task>> { { droneId, tasks } };
            why = Loc.F(StrDroneSComponentSourceS, 1, 0);
            return true;
        }

        /// <summary>Share (0-1) of the job one trip of this drone can take: the tighter of cargo volume and lift.</summary>
        private static double DroneTripShare(Bid drone, double requiredVolume, double requiredMass)
        {
            double share = 1.0;
            double volume = Math.Max((double)drone.MaxCargoVolume, 0.0);
            if (requiredVolume > 0.0) share = Math.Min(share, volume / requiredVolume);
            double lift = (double)drone.OptimalLoadIn1G > 0.0 ? (double)drone.OptimalLoadIn1G : (double)drone.MaxLoadIn1G;
            if (requiredMass > 0.0) share = Math.Min(share, Math.Max(lift, 0.0) / requiredMass);
            return share;
        }

        private Dictionary<long, double> BuildRemainingLcVolumeMap(List<Bid> winningLCs)
        {
            Dictionary<long, double> remaining = new Dictionary<long, double>();
            for (int i = 0; i < winningLCs.Count; i++)
            {
                Bid bid = winningLCs[i];
                if (bid == null)
                {
                    continue;
                }

                double availableVolume = 0.0;
                if (bid.BidInventory != null && !bid.BidInventory.IsEmpty())
                {
                    availableVolume = (double)bid.BidInventory.TotalInventoryVolume();
                }
                if (availableVolume <= 0.0)
                {
                    availableVolume = Math.Max((double)bid.MaxCargoVolume, 0.0);
                }
                remaining[bid.EntityId] = availableVolume;
            }
            return remaining;
        }

        private static bool AnyLcVolumeLeft(Dictionary<long, double> remainingLcVolume)
        {
            foreach (var v in remainingLcVolume.Values) if (v > 0.0) return true;
            return false;
        }

        // The nearest component source with volume left that has a connector this drone can dock at ('port')
        private static Bid SelectBestLcForDrone(Bid drone, List<Bid> lcs, Dictionary<long, double> remainingLcVolume, out IOLocationData port)
        {
            port = null;
            Vector3D dronePosition = drone.IOLocationData.PositionData.ToVector3D();
            Bid best = null;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < lcs.Count; i++)
            {
                Bid lc = lcs[i];
                if (lc == null || lc.IOLocationData == null)
                {
                    continue;
                }

                double remainingVolume;
                if (!remainingLcVolume.TryGetValue(lc.EntityId, out remainingVolume) || remainingVolume <= 0.0)
                {
                    continue;
                }

                var fit = PortFor(lc.DockSite, lc.IOLocationData, drone);
                if (fit == null) continue;
                Vector3D lcPosition = fit.PositionData.ToVector3D();
                double d2 = Vector3D.DistanceSquared(dronePosition, lcPosition);
                if (d2 < bestDistance)
                {
                    bestDistance = d2;
                    best = lc;
                    port = fit;
                }
            }

            return best;
        }

        private static DiscreteInventory BuildTripPayload(DiscreteInventory requiredInventory, double share)
        {
            if (requiredInventory == null || requiredInventory.IsEmpty() || share <= 0.0)
            {
                return null;
            }

            double ratio = Math.Min(1.0, share);
            DiscreteInventory payload = new DiscreteInventory();
            List<KVPair> items = requiredInventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                KVPair item = items[i];
                int amount = item.Value;
                if (amount <= 0)
                {
                    continue;
                }
                // Whole items only: rounding down keeps the trip within the drone's limits (the last trip rounds up)
                int scaledAmount = ratio >= 0.999 ? amount : (int)Math.Floor(amount * ratio);
                if (scaledAmount <= 0 && ratio > 0.0) scaledAmount = 1;
                payload.AddItem(item.Key, scaledAmount);
            }

            return payload.IsEmpty() ? null : payload;
        }

        /// <summary>
        /// One trip for one drone: dock at the logistics computer's connector, load, then the job's work tasks
        /// (NavigateToolTo + WeldBlock + BackOut, central-out). A trip carrying only part of the job welds until its
        /// cargo holds nothing the block needs (DroneInventoryEmpty) instead of until the block is complete.
        /// ReturnHome is appended once per drone after its last trip.
        /// </summary>
        private void AppendWeldTripTasks(List<Task> droneTasks, IOLocationData lcPort, Auction bidRound, DiscreteInventory tripPayload, bool partial)
        {
            droneTasks.Add(DockTask(lcPort));
            droneTasks.Add(Task.Load(tripPayload));
            AppendWeldWork(droneTasks, bidRound, partial);
        }

        // The job's work tasks (NavigateToolTo + WeldBlock + BackOut per block), as they come from the construction computer
        private void AppendWeldWork(List<Task> droneTasks, Auction bidRound, bool partial)
        {
            Job job;
            if (!_auctionJobs.TryGetValue(bidRound.AuctionId, out job) || job.Tasks == null) return;
            for (int i = 0; i + 1 < job.Tasks.Count; i++)
            {
                if (job.Tasks[i].Type != TaskType.NavigateToolTo || job.Tasks[i + 1].Kind != TaskKind.Tool) continue;
                Task tool = job.Tasks[i + 1];
                if (partial && tool.Type == TaskType.WeldBlock) tool.Completion = ToolTaskCompletionTrigger.DroneInventoryEmpty;
                droneTasks.Add(job.Tasks[i]);
                droneTasks.Add(tool);
                i++;
                if (i + 1 < job.Tasks.Count && job.Tasks[i + 1].Type == TaskType.BackOut)
                {
                    droneTasks.Add(job.Tasks[i + 1]);
                    i++;
                }
            }
        }

        private List<Bid> SelectWinningLCs(List<Bid> potentialWinnerLCs, Auction bidRound)
        {
            List<Bid> selectedWinners = new List<Bid>();
            if (potentialWinnerLCs == null || potentialWinnerLCs.Count == 0 || bidRound == null)
            {
                return selectedWinners;
            }

            Vector3D jobPosition = bidRound.PositionData.ToVector3D();
            List<Bid> ranked = RankLogisticsBids(potentialWinnerLCs, jobPosition);
            if (ranked.Count == 0)
            {
                return selectedWinners;
            }

            for (int i = 0; i < ranked.Count; i++)
            {
                Bid bid = ranked[i];
                if (IsFullLogisticsFulfillment(bid))
                {
                    selectedWinners.Add(bid);
                    return selectedWinners;
                }
            }

            // No single full-fulfillment LC exists; aggregate partial bids until requirement is satisfied.
            DiscreteInventory requiredInventory = bidRound.ComponentsInventory != null ? bidRound.ComponentsInventory : new DiscreteInventory();
            DiscreteInventory aggregatedInventory = new DiscreteInventory();
            double requiredVolume = (double)bidRound.TotalVolume;
            double accumulatedVolume = 0.0;

            for (int i = 0; i < ranked.Count; i++)
            {
                Bid bid = ranked[i];
                if (!IsPartialLogisticsFulfillment(bid))
                {
                    continue;
                }

                selectedWinners.Add(bid);
                if (bid.BidInventory != null && !bid.BidInventory.IsEmpty())
                {
                    aggregatedInventory.AddItems(bid.BidInventory);
                    accumulatedVolume += (double)bid.BidInventory.TotalInventoryVolume();
                }
                else
                {
                    accumulatedVolume += Math.Max((double)bid.MaxCargoVolume, 0.0);
                }

                bool inventorySatisfied = requiredInventory.IsEmpty() || aggregatedInventory.ContainsAtLeast(requiredInventory);
                bool volumeSatisfied = requiredVolume <= 0.0 || accumulatedVolume >= requiredVolume;
                if (inventorySatisfied || volumeSatisfied)
                {
                    break;
                }
            }

            return selectedWinners;
        }

        private List<Bid> SelectWinningDrones(List<Bid> potentialWinnerDrones, Auction bidRound)
        {
            List<Bid> rankedWinners = new List<Bid>();
            if (potentialWinnerDrones == null || potentialWinnerDrones.Count == 0 || bidRound == null)
            {
                return rankedWinners;
            }

            Vector3D jobPosition = bidRound.PositionData.ToVector3D();
            double jobVolume = (double)bidRound.TotalVolume;
            double jobMass = (double)bidRound.TotalMass;

            List<double> rankedScores = new List<double>();
            for (int i = 0; i < potentialWinnerDrones.Count; i++)
            {
                Bid drone = potentialWinnerDrones[i];
                if (drone == null || drone.IOLocationData == null)
                {
                    continue;
                }
                if (ToolCantReach(drone, bidRound))
                {
                    if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: auction {1}: drone {2} left out: its tool would have to work upside down and it can't",
                              entityId, bidRound.AuctionId, drone.EntityId);
                    continue;
                }

                double score = ComputeDroneBidScore(drone, jobPosition, jobVolume, jobMass) + ProfileBonus(drone, bidRound) + ToolFaceBonus(drone, bidRound);

                int insertIndex = rankedScores.Count;
                while (insertIndex > 0 && score > rankedScores[insertIndex - 1])
                {
                    insertIndex--;
                }

                rankedScores.Insert(insertIndex, score);
                rankedWinners.Insert(insertIndex, drone);
            }

            return rankedWinners;
        }

        /// <summary>
        /// The behaviour profile a job type prefers: its drones rank above any other bid (scores are 0-1, the bonus 10).
        /// Cargo drones for logistics; ore scouts / combat drones for the designator jobs (which also require them: RequiredProfile).
        /// </summary>
        private static Drone.BehaviourProfile PreferredProfile(JobType type)
        {
            switch (type)
            {
                case JobType.DesignatorScoutPoint:
                case JobType.DesignatorScoutArea: return Drone.BehaviourProfile.IsOreScout;
                case JobType.DesignatorEngage:
                case JobType.DesignatorEscort: return Drone.BehaviourProfile.IsCombatDrone;
                case JobType.DeliverMissingInventory:
                case JobType.CollectInventorySurplus: return Drone.BehaviourProfile.IsCargoDrone;
                // Job profiles: a bit set, any of them ranks first (ProfileBonus tests with &; weld: WeldProfileBonus)
                case JobType.Weld:    return JobTypeExtensions.WELDER_PROFILES;
                case JobType.Grind:   return Drone.BehaviourProfile.IsGrinderDrone | Drone.BehaviourProfile.IsWelderGrinderDrone;
                case JobType.MineOre: return Drone.BehaviourProfile.IsMiningDrone;
                default:                              return Drone.BehaviourProfile.None;
            }
        }

        private static double ProfileBonus(Bid drone, Drone.BehaviourProfile preferred)
        {
            return preferred != Drone.BehaviourProfile.None && (drone.DroneBehaviours & preferred) != 0 ? 10.0 : 0.0;
        }

        // With the auction's items: logistics of ores only prefer ore haulers (cargo drones next), other logistics cargo drones
        private static double ProfileBonus(Bid drone, Auction a)
        {
            if (a == null) return 0;
            bool logistics = a.JobType == JobType.DeliverMissingInventory || a.JobType == JobType.CollectInventorySurplus;
            if (logistics && a.ComponentsInventory != null && a.ComponentsInventory.IsOreOnly())
            {
                if ((drone.DroneBehaviours & Drone.BehaviourProfile.IsOreHauler) != 0) return 10.0;
                if ((drone.DroneBehaviours & Drone.BehaviourProfile.IsCargoDrone) != 0) return 5.0;
                return 0;
            }
            if (a.JobType == JobType.Weld) return WeldProfileBonus(drone, a.WeldCategory);
            return ProfileBonus(drone, PreferredProfile(a.JobType));
        }

        /// <summary>
        /// Weld jobs: every welder profile takes them, the one biased to the job's weld first. The steps (20 / 10 / 5) are
        /// wider than the tool orientation bonus (0-3) and the bid score (0-1), so the ranking holds whatever those say:
        ///  20  Welder:Repair / Projected / Unbuilt on its own kind of weld
        ///  10  Welder:All, Welder + grinder (or a weld of unknown kind for any welder)
        ///   5  Welder:Repair / Projected / Unbuilt on another kind (still above drones with no welder profile, 0)
        /// </summary>
        private static double WeldProfileBonus(Bid drone, byte weld)
        {
            var p = drone.DroneBehaviours;
            if ((p & JobTypeExtensions.WELDER_PROFILES) == 0) return 0;
            byte bias = JobTypeExtensions.WeldBias(p);
            if (bias == JobTypeExtensions.WELD_ANY || weld == JobTypeExtensions.WELD_ANY) return 10.0;
            return bias == weld ? 20.0 : 5.0;
        }

        private static string LabelOf(Auction a)
        {
            return a.JobType.Label(a.WeldCategory);
        }

        #region Tool orientation ranking
        // Weld / grind in gravity: how well a drone's tool direction suits the faces of the job's blocks (the
        // auction's ToolFaceWeights: top / side / bottom access, ConstructionComputer.ToolFaceWeights). Per face kind:
        //  3  the tool points into it with the drone level (top face: tool down; sides: tool forward / back / left /
        //     right; undersides: tool up)
        //  2  it reaches it by tilting within its pitch / roll limits (top face with the tool forward: ~40° nose down)
        //  1  it needs to fly upside down (top face, tool up) and can (no P-Gravity limits, "down" thrust lifts it)
        //  0.5 it would need more tilt than its limits allow (it may still find a way: a low-mounted tool...)
        //  0  upside down needed and it can't
        // A bid's value: the faces' mix, its best tool. Scores are 0..1 and the profile bonus 10, so the kinds rank
        // above distance / cargo and below the profile. 0 (nothing of the job it could reach): the bid is left out.
        private const double TOOL_REACH_LEVEL = 3, TOOL_REACH_TILT = 2, TOOL_REACH_INVERTED = 1, TOOL_REACH_OVER_LIMITS = 0.5;
        // The tool's elevation (degrees, 0 level, -90 straight down) each face kind is worked at, as the drone chooses its
        // approach (DroneControllerBlock.Jobs: APPROACH_TILTS up to 50° from the normal, never more than ~17.5° from
        // below except undersides, UNDERSIDE_TILTS up to 80°)
        private const double TOP_ELEV_MIN = -90, TOP_ELEV_MAX = -40;
        private const double SIDE_ELEV_MIN = -50, SIDE_ELEV_MAX = 17.5;
        private const double BOTTOM_ELEV_MIN = 10, BOTTOM_ELEV_MAX = 90;

        // How well a drone's tools suit the auction's faces (0..3); -1: no preference (space, no data, not a tool job)
        private static double ToolReach(Bid drone, Auction a)
        {
            if (a == null || drone == null || !a.IsInAtmosphere || a.ToolFaceWeights == null || a.ToolFaceWeights.Count != 6
                || drone.ToolFaces == null || drone.ToolFaces.Count == 0) return -1;
            double top = a.ToolFaceWeights[(int)Base6Directions.Direction.Down];      // ToolFaceWeights: tool down = from above
            double side = a.ToolFaceWeights[(int)Base6Directions.Direction.Forward];
            double bottom = a.ToolFaceWeights[(int)Base6Directions.Direction.Up];
            double total = top + side + bottom;
            if (total <= 0) return -1;
            double best = 0;
            for (int i = 0; i < drone.ToolFaces.Count; i++)
            {
                int f = drone.ToolFaces[i];
                if (f < 0 || f >= 6) continue;
                var dir = (Base6Directions.Direction)f;
                double v = (top * FaceReach(drone, dir, TOP_ELEV_MIN, TOP_ELEV_MAX)
                          + side * FaceReach(drone, dir, SIDE_ELEV_MIN, SIDE_ELEV_MAX)
                          + bottom * FaceReach(drone, dir, BOTTOM_ELEV_MIN, BOTTOM_ELEV_MAX)) / total;
                if (v > best) best = v;
            }
            return best;
        }

        // One tool direction (controller frame) against one face kind (the tool elevations it is worked at)
        private static double FaceReach(Bid drone, Base6Directions.Direction tool, double elevMin, double elevMax)
        {
            // The tool's elevation with the drone level, and the tilt from level that brings it into the range
            double level = tool == Base6Directions.Direction.Down ? -90 : tool == Base6Directions.Direction.Up ? 90 : 0;
            double tilt = level < elevMin ? elevMin - level : level > elevMax ? level - elevMax : 0;
            if (tilt <= 0) return TOOL_REACH_LEVEL;
            if (tilt > 90) return drone.CanFlyInverted ? TOOL_REACH_INVERTED : 0;
            // Forward / back tools tilt by pitching, left / right by rolling; up / down either way (the drone turns)
            double limit = tool == Base6Directions.Direction.Forward || tool == Base6Directions.Direction.Backward ? drone.WorkPitchDegrees
                         : tool == Base6Directions.Direction.Left || tool == Base6Directions.Direction.Right ? drone.WorkRollDegrees
                         : Math.Max(drone.WorkPitchDegrees, drone.WorkRollDegrees);
            return limit + 1 >= tilt ? TOOL_REACH_TILT : TOOL_REACH_OVER_LIMITS;
        }

        // The ranking bonus (0 without a preference)
        private static double ToolFaceBonus(Bid drone, Auction a)
        {
            double r = ToolReach(drone, a);
            return r > 0 ? r : 0;
        }

        // Left out: it would have to work upside down and can't, for every face of the job
        private static bool ToolCantReach(Bid drone, Auction a)
        {
            return ToolReach(drone, a) == 0;
        }
        #endregion

        /// <summary>Jobs an ore hauler may take: mining, and logistics whose items are all ores (Ice included).</summary>
        private static bool IsOreJob(Auction a)
        {
            if (a.JobType == JobType.MineOre) return true;
            bool logistics = a.JobType == JobType.DeliverMissingInventory || a.JobType == JobType.CollectInventorySurplus;
            return logistics && a.ComponentsInventory != null && a.ComponentsInventory.IsOreOnly();
        }

        private double ComputeDroneBidScore(Bid drone, Vector3D jobPosition, double jobVolume, double jobMass)
        {
            Vector3D dronePosition = drone.IOLocationData.PositionData.ToVector3D();
            double distanceSquared = Vector3D.DistanceSquared(dronePosition, jobPosition);
            double distanceScore = 1.0 / (1.0 + distanceSquared);

            double maxCargoVolume = Math.Max((double)drone.MaxCargoVolume, 0.0001);
            double maxLoadIn1G = Math.Max((double)drone.MaxLoadIn1G, 0.0001);
            double optimalLoadIn1G = Math.Max((double)drone.OptimalLoadIn1G, 0.0001);
            optimalLoadIn1G = Math.Min(optimalLoadIn1G, maxLoadIn1G);

            // Approximate a preferred cargo volume envelope from the configured optimal load ratio.
            double optimalCargoVolume = maxCargoVolume * (optimalLoadIn1G / maxLoadIn1G);

            double volumeFitScore = ComputeVolumeFitScore(jobVolume, maxCargoVolume, optimalCargoVolume);
            double loadComfortScore = ComputeLoadComfortScore(jobMass, optimalLoadIn1G, maxLoadIn1G);

            const double distanceWeight = 0.45;
            const double volumeFitWeight = 0.40;
            const double loadComfortWeight = 0.15;

            return distanceWeight * distanceScore
                + volumeFitWeight * volumeFitScore
                + loadComfortWeight * loadComfortScore;
        }

        private static double ComputeVolumeFitScore(double jobVolume, double maxCargoVolume, double optimalCargoVolume)
        {
            if (maxCargoVolume <= 0.0)
            {
                return 0.0;
            }
            if (jobVolume > maxCargoVolume)
            {
                return 0.0;
            }

            double constrainedOptimalVolume = Math.Min(Math.Max(optimalCargoVolume, 0.0), maxCargoVolume);
            double capacityTarget = constrainedOptimalVolume > 0.0 ? constrainedOptimalVolume : maxCargoVolume;

            if (jobVolume <= capacityTarget)
            {
                // Prefer drones whose capacity is a close fit, rather than massively oversized cargo holds.
                return Clamp01(jobVolume / capacityTarget);
            }

            double aboveOptimalRange = Math.Max(maxCargoVolume - capacityTarget, 0.0001);
            double overOptimalFraction = (jobVolume - capacityTarget) / aboveOptimalRange;
            // Still allow these drones, but penalize heavily once the job exceeds the optimal envelope.
            return Clamp01(0.5 * (1.0 - overOptimalFraction));
        }

        private static double ComputeLoadComfortScore(double jobMass, double optimalLoadIn1G, double maxLoadIn1G)
        {
            if (maxLoadIn1G <= 0.0)
            {
                return 0.0;
            }

            if (jobMass <= optimalLoadIn1G)
            {
                return 1.0;
            }

            double loadRange = Math.Max(maxLoadIn1G - optimalLoadIn1G, 0.0001);
            double fractionPastOptimal = (jobMass - optimalLoadIn1G) / loadRange;
            return Clamp01(1.0 - fractionPastOptimal);
        }

        private List<Bid> RankLogisticsBids(List<Bid> bids, Vector3D jobPosition)
        {
            List<Bid> ranked = new List<Bid>();
            List<double> scores = new List<double>();

            for (int i = 0; i < bids.Count; i++)
            {
                Bid bid = bids[i];
                if (bid == null || bid.IOLocationData == null)
                {
                    continue;
                }

                double score = ComputeLogisticsBidScore(bid, jobPosition);
                int insertIndex = scores.Count;
                while (insertIndex > 0 && score > scores[insertIndex - 1])
                {
                    insertIndex--;
                }
                scores.Insert(insertIndex, score);
                ranked.Insert(insertIndex, bid);
            }

            return ranked;
        }

        private static double ComputeLogisticsBidScore(Bid bid, Vector3D jobPosition)
        {
            Vector3D bidPosition = bid.IOLocationData.PositionData.ToVector3D();
            double distanceSquared = Vector3D.DistanceSquared(bidPosition, jobPosition);
            double distanceScore = 1.0 / (1.0 + distanceSquared);
            double fulfillmentScore = GetLogisticsFulfillmentScore(bid.InventoryFulfillmentFlags);

            const double distanceWeight = 0.35;
            const double fulfillmentWeight = 0.65;
            return (distanceScore * distanceWeight) + (fulfillmentScore * fulfillmentWeight);
        }

        private static double GetLogisticsFulfillmentScore(Logistics.InventoryFulfillment flags)
        {
            if (flags.HasFlag(Logistics.InventoryFulfillment.SatisfyFully)
                || flags.HasFlag(Logistics.InventoryFulfillment.AcceptAll))
            {
                return 1.0;
            }
            if (flags.HasFlag(Logistics.InventoryFulfillment.SatisfyPartial)
                || flags.HasFlag(Logistics.InventoryFulfillment.AcceptPartial))
            {
                return 0.55;
            }
            return 0.0;
        }

        private static bool IsFullLogisticsFulfillment(Bid bid)
        {
            Logistics.InventoryFulfillment flags = bid.InventoryFulfillmentFlags;
            return flags.HasFlag(Logistics.InventoryFulfillment.SatisfyFully)
                || flags.HasFlag(Logistics.InventoryFulfillment.AcceptAll);
        }

        private static bool IsPartialLogisticsFulfillment(Bid bid)
        {
            Logistics.InventoryFulfillment flags = bid.InventoryFulfillmentFlags;
            return flags.HasFlag(Logistics.InventoryFulfillment.SatisfyPartial)
                || flags.HasFlag(Logistics.InventoryFulfillment.AcceptPartial);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }
            if (value > 1.0)
            {
                return 1.0;
            }
            return value;
        }
        #endregion
    }
}
