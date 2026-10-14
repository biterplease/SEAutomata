using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

using Automata.Drone;
using Automata.Inventory;
using Automata.Orchestrator;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Mining
{
    /// <summary>
    /// Mining Surveyor (a programmable block subtype). Knows ore deposits: its own grid's ore detector scans
    /// (<see cref="OreScanner"/>), and drones' survey data (MINING_DATA_FOUND_ANNOUNCEMENT) when they come into antenna
    /// range. Same ore within the merge distance (50 m) of a known deposit is that deposit; a scan that covers a known
    /// deposit without finding it again marks it depleted. Ore orders ("Ore/Iron:100000", kg) turn into MineOre jobs on
    /// the nearest known deposit, each counted off its order when announced and settled by the drone's report (what
    /// it really unloaded); "keep orders constant" never counts down. Jobs go to orchestrators like a construction
    /// computer's (cached, holds, awards, drone reports). Deposits can be shown to the owner as temporary GPS markers.
    /// Server only; deposits and jobs are kept through a world save.
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, "AutomataLargeMiningSurveyor")]
    public class MiningSurveyorBlock : SettingsBlockLogic, INicknamed
    {
        #region UI strings
        private static readonly MyStringId StrError = MyStringId.GetOrCompute("Error: {0}");
        private static readonly MyStringId StrSourceDrone = MyStringId.GetOrCompute("drone {0}");
        private static readonly MyStringId StrHeaderTitle = MyStringId.GetOrCompute("MC {0}");
        private static readonly MyStringId StrHeaderDeposits = MyStringId.GetOrCompute("Known deposits");
        private static readonly MyStringId StrHeaderRequests = MyStringId.GetOrCompute("Active requests (kg)");
        private static readonly MyStringId StrHeaderPage = MyStringId.GetOrCompute("(page {0}/{1})");
        private static readonly MyStringId StrLogSurveyDrone = MyStringId.GetOrCompute("Survey: Drone {0} found {1}");
        private static readonly MyStringId StrLogSurveyOwn = MyStringId.GetOrCompute("Survey: own scan found {0}");
        private static readonly MyStringId StrLogDelivered = MyStringId.GetOrCompute("Delivered: Drone {0}, Ore: {1}, {2:F0} kg");
        private static readonly MyStringId StrLogDroneOutcome = MyStringId.GetOrCompute("Mining: Drone {0} {1}, Ore: {2}");
        private static readonly MyStringId StrOnHoldReason = MyStringId.GetOrCompute("on hold ({0})");
        private static readonly MyStringId StrGpsDescription = MyStringId.GetOrCompute("Ore deposit known to mining surveyor {0}");
        private static readonly MyStringId StrDepositCount = MyStringId.GetOrCompute("{0} x{1}");
        private static readonly MyStringId StrOrderConstant = MyStringId.GetOrCompute("{0} (constant)");
        private static readonly MyStringId StrInfoStatus = MyStringId.GetOrCompute("Mining: {0}");
        private static readonly MyStringId StrKnownDeposits = MyStringId.GetOrCompute("Known deposits:");
        private static readonly MyStringId StrNone = MyStringId.GetOrCompute("none");
        private static readonly MyStringId StrConstant = MyStringId.GetOrCompute("constant");
        private static readonly MyStringId StrKgLeft = MyStringId.GetOrCompute("{0:N0} kg left");
        private static readonly MyStringId StrInfoDelivered = MyStringId.GetOrCompute("Delivered: {0:N0} kg, failed auctions: {1}");
        private static readonly MyStringId StrKnownDepositsTitle = MyStringId.GetOrCompute("Known deposits");
        private static readonly MyStringId StrActiveRequests = MyStringId.GetOrCompute("Active requests");
        private static readonly MyStringId StrStarting = MyStringId.GetOrCompute("Starting");
        private static readonly MyStringId StrDepositsShownAsGps = MyStringId.GetOrCompute("{0} known deposit(s) shown as GPS");
        private static readonly MyStringId StrDeletedDeposits = MyStringId.GetOrCompute("Deleted {0} known deposit(s)");
        private static readonly MyStringId StrRetryJobSAnnouncedAgain = MyStringId.GetOrCompute("Retry: {0} job(s) announced again");
        private static readonly MyStringId StrWaitingForTheMod = MyStringId.GetOrCompute("Waiting for the mod");
        private static readonly MyStringId StrLoading = MyStringId.GetOrCompute("Loading");
        private static readonly MyStringId StrOffTerminal = MyStringId.GetOrCompute("Off (terminal)");
        private static readonly MyStringId StrNotWorkingPowerDamage = MyStringId.GetOrCompute("Not working (power / damage)");
        private static readonly MyStringId StrNoAntennaFunctionalOnBroadcasting = MyStringId.GetOrCompute("No antenna (functional, on, broadcasting): deposits only");
        private static readonly MyStringId StrNoOreOrders = MyStringId.GetOrCompute("No ore orders");
        private static readonly MyStringId StrNothingToMineOrdersDone = MyStringId.GetOrCompute("Nothing to mine (orders done, or no known deposit)");
        private static readonly MyStringId StrJobSPublishedOnHold = MyStringId.GetOrCompute("{0} job(s) published, {1} on hold, {2} out");
        private static readonly MyStringId StrOwnScan = MyStringId.GetOrCompute("own scan");
        private static readonly MyStringId StrAtMDepleted = MyStringId.GetOrCompute("{0} at {1:F0} m: depleted ({2})");
        private static readonly MyStringId StrOrderRemoved = MyStringId.GetOrCompute("order removed");
        private static readonly MyStringId StrJobKgAtMBore = MyStringId.GetOrCompute("Job: {1:N0} kg {2} at {3:F0} m (bore {4:F0} m)");
        private static readonly MyStringId StrAnnounced = MyStringId.GetOrCompute("announced");
        private static readonly MyStringId StrNoOrchestratorInRange = MyStringId.GetOrCompute("no orchestrator in range");
        private static readonly MyStringId StrAssigned = MyStringId.GetOrCompute("assigned");
        private static readonly MyStringId StrJobDroneOnItsWay = MyStringId.GetOrCompute("Job awarded: {0}, Drone {1}");
        private static readonly MyStringId StrJobOnHoldS = MyStringId.GetOrCompute("Job on hold {1} s\n  {2}");
        private static readonly MyStringId StrJobDroneKgDelivered = MyStringId.GetOrCompute("Job {0}: drone {1} {2}, {3:N0} kg delivered");
        private static readonly MyStringId StrText = MyStringId.GetOrCompute(" - {0}: {1}\n");
        private static readonly MyStringId StrOrderProgress = MyStringId.GetOrCompute("{0} {1:N0}/{2:N0} kg");
        #endregion

        #region Timing
        private const int STEP_EVERY = 10;                       // UpdateBeforeSimulation10 calls per step (100 ticks)
        private const int RETRY_TICKS = 20 * 60;                 // an announcement that reached nobody: tried again
        private const int RESPONSE_TIMEOUT_TICKS = 60 * 60;      // announced, nothing back since: announced again (was 5 min)
        private const int ASSIGNED_TTL_TICKS = 30 * 60 * 60;     // awarded, then no sign of the drone (award / keepalive every 60 s) this long: counted back
        private const double DEPLETED_MEMORY_SECONDS = 3600;     // a depleted deposit isn't brought back by older scans
        private const int GPS_REFRESH_TICKS = 600;
        private const int KNOWN_ORES_TICKS = 600;                // the terminal's ore list sent to players at most this often
        #endregion
        private const double BORE_SEARCH_METERS = 400;
        private const double DEPLETION_MARGIN = 10;              // a deposit this close to a scan's edge isn't judged by it
        public const int JOB_SIZE_MIN = 1000, JOB_SIZE_MAX = 200000;

        private MiningSurveyorSettings settings = new MiningSurveyorSettings();
        private long entityId;
        private MessageQueue messaging;
        private IMyRadioAntenna antenna;
        private IMyOreDetector detector;
        private bool ready;
        private int stepCounter;
        private int lastSearch = int.MinValue / 2, lastOwnScan = int.MinValue / 2, lastGps = int.MinValue / 2, lastKnownOres = int.MinValue / 2;
        private string status = Loc.T(StrStarting);
        private readonly StringBuilder info = new StringBuilder();
        private readonly List<IMyCubeGrid> gridBuffer = new List<IMyCubeGrid>();

        private sealed class Deposit
        {
            public MyStringHash Ore;
            public Vector3D Position;
            public long VoxelId;
            public int Voxels;
            public DateTime Seen;   // last seen (deposits) / found depleted (depleted list)
        }
        private readonly List<Deposit> deposits = new List<Deposit>();
        private readonly List<Deposit> depleted = new List<Deposit>();

        private sealed class OrderProgress { public int OrderKg; public double RemainingKg; public double DeliveredKg; }
        private readonly Dictionary<MyStringHash, OrderProgress> progress = new Dictionary<MyStringHash, OrderProgress>(MyStringHash.Comparer);
        private readonly List<OreOrder> orders = new List<OreOrder>();
        private bool ordersDirty = true;

        private sealed class MiningJob
        {
            public uint JobId;
            public MyStringHash Ore;
            public Vector3D Deposit;
            public double Kg;
            public Message<JobAnnouncement> Message;
            public int LastAnnounced = int.MinValue / 2, Assigned, HeldUntil, DronesOut;
            public double DeliveredKg;   // reported so far by the drones of the award (several miners on one job)
            public bool Announced;
            public string LastResult;
        }
        private readonly List<MiningJob> jobs = new List<MiningJob>();
        private int jobCounter, messageCounter, deliveredCount, failedCount;
        private MiningSurveyorState pendingState;

        private readonly OreScanner scanner = new OreScanner();
        private readonly List<OreHit> scanHits = new List<OreHit>();
        private readonly List<int> gpsHashes = new List<int>(), requestedHashes = new List<int>();
        private long gpsIdentity;
        private bool gpsDirty = true;
        private readonly BlockLog log = new BlockLog();

        private readonly List<Message<MiningSurveyDataBroadcast>> surveyInbox = new List<Message<MiningSurveyDataBroadcast>>();
        private readonly List<Message<AuctionWinnerAnnouncement>> awardInbox = new List<Message<AuctionWinnerAnnouncement>>();
        private readonly List<Message<JobAnnouncementFailed>> failureInbox = new List<Message<JobAnnouncementFailed>>();
        private readonly List<Message<JobStatusReport>> reportInbox = new List<Message<JobStatusReport>>();

        private static Config.ServerConfig.MiningConfig MiningConfig
        {
            get { var c = AutomataSession.GetConfig(); return c != null ? c.Mining : Config.ServerConfig.Instance.Mining; }
        }

        #region Block
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            block = (IMyTerminalBlock)Entity;
            entityId = Entity.EntityId;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        /// <summary>Surveyors loaded on this machine.</summary>
        public static readonly List<MiningSurveyorBlock> All = new List<MiningSurveyorBlock>();

        /// <summary>Known deposits of every surveyor within 'radius' of 'p' (ore, position), for the flight lanes' hotspots.</summary>
        public static void DepositsNear(Vector3D p, double radius, List<KeyValuePair<MyStringHash, Vector3D>> into)
        {
            double r2 = radius * radius;
            for (int i = 0; i < All.Count; i++)
            {
                var s = All[i];
                if (!s.ready) continue;
                for (int k = 0; k < s.deposits.Count; k++)
                    if (Vector3D.DistanceSquared(s.deposits[k].Position, p) <= r2)
                        into.Add(new KeyValuePair<MyStringHash, Vector3D>(s.deposits[k].Ore, s.deposits[k].Position));
            }
        }

        /// <summary>A working mining surveyor of this owner / their faction exists (drones keep ore scans only then).</summary>
        public static bool AnyFor(long owner)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var s = All[i];
                if (s.ready && s.block != null && s.block.IsWorking && Drone.AnchorDirectory.IsAllowed(owner, s.block.OwnerId)) return true;
            }
            return false;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            if (block.CubeGrid == null || block.CubeGrid.Physics == null) return;   // projections / ghosts
            MiningSurveyorTerminalControls.DoOnce(ModContext);
            LoadFromStorage();
            block.AppendingCustomInfo += AppendInfo;
            ready = true;
            if (!All.Contains(this)) All.Add(this);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            Log.Info("MS {0}: '{1}' on '{2}': {3} order(s), {4} saved deposit(s)", entityId, block.CustomName, block.CubeGrid.DisplayName,
                     settings.Orders.Count, pendingState != null && pendingState.Deposits != null ? pendingState.Deposits.Count : 0);
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();
            if (!ready) return;
            try
            {
                if (IsServer && scanner.Running && scanner.Step(MiningConfig.ScanCellsPerUpdate, scanHits)) OwnScanDone();
                if (++stepCounter < STEP_EVERY) return;
                stepCounter = 0;
                if (IsServer) { Step(); double tl = Util.PerfStats.Start(); UpdateLcd(); Util.PerfStats.Stop(Util.PerfStats.LcdBuild, tl); }
                else if (ordersDirty) ParseOrders();   // clients: the info panel
                block.RefreshCustomInfo();
            }
            catch (Exception ex)
            {
                Log.Error("MS {0}: update error: {1}", entityId, ex);
                status = Loc.F(StrError, ex.Message);
            }
        }

        public override void Close()
        {
            try
            {
                if (block != null) block.AppendingCustomInfo -= AppendInfo;
                All.Remove(this);
                if (IsServer) { RemoveGps(); RemoveGps(requestedHashes); }
                if (messaging != null) { messaging.RemoveAntenna(entityId); messaging.UnsubscribeAll(entityId); }
                Log.Debug("MS {0}: closed ({1} deposit(s), {2} job(s))", entityId, deposits.Count, jobs.Count);
            }
            catch (Exception ex)
            {
                Log.Error("MS {0}: close error: {1}", entityId, ex);
            }
            base.Close();
        }

        protected override string SettingsXml()
        {
            return MyAPIGateway.Utilities.SerializeToXML(settings);
        }

        protected override string SaveText()
        {
            var data = new MiningSurveyorSaveData { Settings = settings, EntityId = Entity.EntityId };
            if (IsServer) data.State = ready && pendingState == null ? CaptureState() : pendingState;
            return BlockSave.ToText(data);
        }

        protected override bool LoadSaveText(string text)
        {
            var data = BlockSave.FromText<MiningSurveyorSaveData>(text, Entity.EntityId);
            if (data == null) return false;
            settings = data.Settings ?? new MiningSurveyorSettings();
            Normalize();
            if (IsServer && data.State != null && BlockSave.OwnState(data.EntityId, Entity.EntityId, "MiningSurveyor")) pendingState = data.State;
            return true;
        }

        protected override bool LoadSettingsXml(string xml)
        {
            var loaded = MyAPIGateway.Utilities.SerializeFromXML<MiningSurveyorSettings>(xml);
            if (loaded == null) return false;
            settings = loaded;
            Normalize();
            return true;
        }

        protected override void OnSettingsChanged()
        {
            Normalize();
            ordersDirty = true;
            gpsDirty = true;
            CheckRetryRequest();
            CheckShowDepositsRequest();
            CheckDeleteDepositsRequest();
        }

        #region Retry jobs (terminal button)
        private int lastShowRequest;
        private bool showSeen;

        private void CheckShowDepositsRequest()
        {
            if (showSeen && settings.ShowDepositsRequest != lastShowRequest && IsServer) ShowDepositsOnce();
            lastShowRequest = settings.ShowDepositsRequest;
            showSeen = true;
        }

        public bool Terminal_ShowDeposits()
        {
            settings.ShowDepositsRequest++;
            SettingsEdited();
            return true;
        }

        private int lastDeleteRequest;
        private bool deleteSeen;

        private void CheckDeleteDepositsRequest()
        {
            if (deleteSeen && settings.DeleteDepositsRequest != lastDeleteRequest && IsServer) DeleteDeposits();
            lastDeleteRequest = settings.DeleteDepositsRequest;
            deleteSeen = true;
        }

        public bool Terminal_DeleteDeposits()
        {
            settings.DeleteDepositsRequest++;
            SettingsEdited();
            return true;
        }

        // Every known deposit forgotten, and the depleted ones with them (a fresh start); the jobs no drone has taken for
        // them go with the next pass (their deposit is no longer known), the published ore list is renewed now
        private void DeleteDeposits()
        {
            int n = deposits.Count;
            deposits.Clear();
            depleted.Clear();
            lastKnownOres = int.MinValue / 2;
            gpsDirty = true;
            ordersDirty = true;
            AddEvent(Loc.F(StrDeletedDeposits, n));
            Log.Info("MS {0}: known deposits deleted on request: {1} deposit(s)", entityId, n);
        }

        private int lastRetryRequest;
        private bool retrySeen;

        private void CheckRetryRequest()
        {
            if (retrySeen && settings.RetryRequest != lastRetryRequest && IsServer) RetryJobs();
            lastRetryRequest = settings.RetryRequest;
            retrySeen = true;
        }

        public bool Terminal_RetryJobs()
        {
            settings.RetryRequest++;
            SettingsEdited();
            return true;
        }

        // Jobs no drone has: holds lifted, announced again now; orders planned again
        private void RetryJobs()
        {
            int n = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (job.Assigned != 0) continue;
                job.HeldUntil = 0;
                job.LastAnnounced = int.MinValue / 2;
                n++;
            }
            ordersDirty = true;
            AddEvent(Loc.F(StrRetryJobSAnnouncedAgain, n));
            Log.Info("MS {0}: retry requested: {1} unassigned job(s) announced again", entityId, n);
        }
        #endregion

        private void Normalize()
        {
            if (settings.Orders == null) settings.Orders = new List<string>();
            if (settings.KnownOres == null) settings.KnownOres = new List<string>();
            settings.JobSizeKg = MathHelper.Clamp(settings.JobSizeKg, JOB_SIZE_MIN, JOB_SIZE_MAX);
        }
        #endregion

        #region Loop
        private void Step()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!retrySeen) CheckRetryRequest();   // loaded: the saved request counts as seen
            if (!showSeen) CheckShowDepositsRequest();
            if (!deleteSeen) CheckDeleteDepositsRequest();
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
                if (gpsHashes.Count > 0) RemoveGps();
                return;
            }
            if (ordersDirty) ParseOrders();
            if (now - lastSearch >= 600)
            {
                lastSearch = now;
                FindBlocks();
            }
            // Own ore detector: a scan every interval (read over the next updates by the cell budget)
            if (detector != null && !detector.Closed && detector.IsWorking && !scanner.Running
                && now - lastOwnScan >= MiningConfig.SurveyorScanIntervalSeconds * 60)
            {
                lastOwnScan = now;
                scanHits.Clear();
                double r = Math.Min(detector.Range, MiningConfig.MaxScanRadius);
                if (scanner.Begin(detector.GetPosition(), r))
                    Log.Debug("MS {0}: own scan started ({1:F0} m)", entityId, r);
            }
            bool online = EnsureAntenna();
            if (online)
            {
                ReadSurveys();
                ReadAwards(now);
                ReadFailures(now);
                ReadReports();
                ReadKeepalives(now);
            }
            droneWatch.Check(now, block, settings.UseBroadcastController, "MS {0}", entityId, AddEvent);
            ExpireJobs(now);
            if (online)
            {
                PlanJobs(now);
                Announce(now);
            }
            PublishKnownOres(now);
            UpdateGps(now);
            int open = 0, held = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Assigned != 0) continue;
                if (jobs[i].HeldUntil != 0) held++; else open++;
            }
            status = !online ? Loc.T(StrNoAntennaFunctionalOnBroadcasting)
                   : orders.Count == 0 ? Loc.T(StrNoOreOrders)
                   : jobs.Count == 0 ? Loc.T(StrNothingToMineOrdersDone)
                   : Loc.F(StrJobSPublishedOnHold, open, held, jobs.Count - open - held);
        }

        private void FindBlocks()
        {
            IMyRadioAntenna bestAntenna = null;
            IMyOreDetector bestDetector = null;
            gridBuffer.Clear();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, gridBuffer);
            for (int g = 0; g < gridBuffer.Count; g++)
            {
                foreach (var a in gridBuffer[g].GetFatBlocks<IMyRadioAntenna>())
                    if (a.IsFunctional && a.Enabled && a.EnableBroadcasting && (bestAntenna == null || AntennaUtil.Range(a) > AntennaUtil.Range(bestAntenna))) bestAntenna = a;
                foreach (var d in gridBuffer[g].GetFatBlocks<IMyOreDetector>())
                    if (d.IsFunctional && (bestDetector == null || d.Range > bestDetector.Range)) bestDetector = d;
            }
            gridBuffer.Clear();
            if (bestAntenna != antenna) Log.Debug("MS {0}: antenna {1}", entityId, bestAntenna != null ? "'" + bestAntenna.CustomName + "'" : "none");
            if (bestDetector != detector) Log.Debug("MS {0}: ore detector {1}", entityId, bestDetector != null ? "'" + bestDetector.CustomName + "'" : "none");
            antenna = bestAntenna;
            detector = bestDetector;
        }

        private bool EnsureAntenna()
        {
            if (!AntennaUtil.Alive(antenna) || !antenna.IsWorking || !antenna.EnableBroadcasting) return false;
            MessageQueue.AntennaInfo antennaInfo;
            if (!messaging.GetAntennaInfo(entityId, out antennaInfo) || antennaInfo.Antenna != antenna)
            {
                messaging.RemoveAntenna(entityId);
                messaging.RegisterAntenna(entityId, MessageQueue.IAIBlockType.MiningSurveyor, antenna, block.CubeGrid.IsStatic);
                messaging.Subscribe(entityId, Channel.MINING_DATA_FOUND_ANNOUNCEMENT);
                messaging.Subscribe(entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
                messaging.Subscribe(entityId, Channel.JOB_ANNOUNCEMENT_FAILED);
                messaging.Subscribe(entityId, Channel.TASKS_COMPLETED);
                messaging.Subscribe(entityId, Channel.TASKS_ABORTED);
                messaging.Subscribe(entityId, Channel.JOB_KEEPALIVE);
                Log.Info("MS {0}: on the virtual network through antenna '{1}' ({2:F0} m)", entityId, antenna.CustomName, AntennaUtil.Range(antenna));
            }
            return true;
        }
        #endregion

        #region Deposits
        private void OwnScanDone()
        {
            Log.Debug("MS {0}: own scan done: {1} cell(s) read, {2} hit(s)", entityId, scanner.CellsRead, scanHits.Count);
            CountDeposits(depositsBefore);
            ApplySweep(scanner.Center, scanner.Radius, DateTime.UtcNow, scanHits, Loc.T(StrOwnScan));
            scanHits.Clear();
            string found = NewOres(depositsBefore);
            if (found != null) AddEvent(Loc.F(StrLogSurveyOwn, found));
        }

        private readonly List<OreHit> sweepHits = new List<OreHit>();
        private readonly Dictionary<MyStringHash, int> depositsBefore = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);

        private bool HasJobFor(MyStringHash ore)
        {
            for (int i = 0; i < jobs.Count; i++) if (jobs[i].Ore == ore) return true;
            return false;
        }

        private void CountDeposits(Dictionary<MyStringHash, int> into)
        {
            into.Clear();
            for (int i = 0; i < deposits.Count; i++)
            {
                int n;
                into.TryGetValue(deposits[i].Ore, out n);
                into[deposits[i].Ore] = n + 1;
            }
        }

        // The ores with more deposits than 'before' (a survey found them), "Iron, Nickel"; null: none
        private string NewOres(Dictionary<MyStringHash, int> before)
        {
            var now = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);
            CountDeposits(now);
            StringBuilder sb = null;
            foreach (var kv in now)
            {
                int was;
                before.TryGetValue(kv.Key, out was);
                if (kv.Value <= was || IsIgnoredOre(kv.Key.String)) continue;
                if (sb == null) sb = new StringBuilder(); else sb.Append(", ");
                sb.Append(ShortOre(kv.Key));
            }
            return sb != null ? sb.ToString() : null;
        }

        private void ReadSurveys()
        {
            messaging.ReadMessages(entityId, antenna, Channel.MINING_DATA_FOUND_ANNOUNCEMENT, surveyInbox, 20, true, PayloadType.MiningSurveyDataBroadcast);
            for (int i = 0; i < surveyInbox.Count; i++)
            {
                var m = surveyInbox[i];
                if (m.Payload == null || m.Payload.Sweeps == null) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, m.SenderOwnerId)) continue;   // the owner's / faction's drones only
                DateTime sent = TimeUtil.TimestampToDateTime(m.SentAt);
                int before = deposits.Count;
                CountDeposits(depositsBefore);
                for (int s = 0; s < m.Payload.Sweeps.Count; s++)
                {
                    var sweep = m.Payload.Sweeps[s];
                    if (sweep == null) continue;
                    sweepHits.Clear();
                    if (sweep.Hits != null)
                        for (int h = 0; h < sweep.Hits.Count; h++)
                        {
                            var hit = sweep.Hits[h];
                            if (hit == null || string.IsNullOrEmpty(hit.Ore)) continue;
                            sweepHits.Add(new OreHit { Ore = MyStringHash.GetOrCompute(hit.Ore), Position = hit.Position.ToVector3D(), Voxels = hit.Voxels, VoxelId = hit.VoxelId });
                        }
                    ApplySweep(sweep.Center.ToVector3D(), sweep.Radius, sent.AddSeconds(-Math.Max(0, sweep.AgeSeconds)), sweepHits, Loc.F(StrSourceDrone, Nicknames.Of(m.SenderId)));
                }
                string found = NewOres(depositsBefore);
                if (found != null) AddEvent(Loc.F(StrLogSurveyDrone, Nicknames.Of(m.SenderId), found));
                Log.Debug("MS {0}: survey from drone {1}: {2} scan(s), deposits {3} -> {4}", entityId, m.SenderId, m.Payload.Sweeps.Count, before, deposits.Count);
            }
            surveyInbox.Clear();
            sweepHits.Clear();
        }

        /// <summary>
        /// One complete scan: known deposits in it that it didn't find again are depleted (unless seen since); what
        /// it found is a known deposit (same ore within the merge distance) or a new one (unless found depleted since).
        /// </summary>
        private void ApplySweep(Vector3D center, double radius, DateTime scanned, List<OreHit> hits, string source)
        {
            double merge = MiningConfig.DepositMergeMeters, merge2 = merge * merge;
            double inner = Math.Max(0, radius - DEPLETION_MARGIN);
            bool changed = false;
            for (int i = deposits.Count - 1; i >= 0; i--)
            {
                var d = deposits[i];
                if (d.Seen > scanned || Vector3D.DistanceSquared(d.Position, center) > inner * inner) continue;
                bool found = false;
                for (int h = 0; h < hits.Count && !found; h++)
                    found = hits[h].Ore == d.Ore && Vector3D.DistanceSquared(hits[h].Position, d.Position) <= merge2;
                if (found) continue;
                deposits.RemoveAt(i);
                d.Seen = scanned;
                depleted.Add(d);
                changed = true;
                AddEvent(Loc.F(StrAtMDepleted, ShortOre(d.Ore), Vector3D.Distance(d.Position, block.GetPosition()), source));
                Log.Info("MS {0}: deposit {1} at {2} depleted ({3})", entityId, d.Ore, d.Position, source);
            }
            for (int h = 0; h < hits.Count; h++)
            {
                var hit = hits[h];
                if (IsIgnoredOre(hit.Ore.String)) continue;   // stone: never a deposit
                bool stale = false;
                for (int t = depleted.Count - 1; t >= 0; t--)
                {
                    var dd = depleted[t];
                    if (dd.Ore != hit.Ore || Vector3D.DistanceSquared(dd.Position, hit.Position) > merge2) continue;
                    if (dd.Seen > scanned) { stale = true; break; }
                    depleted.RemoveAt(t);   // found again since it was found depleted
                }
                if (stale) continue;
                Deposit known = null;
                for (int i = 0; i < deposits.Count && known == null; i++)
                    if (deposits[i].Ore == hit.Ore && Vector3D.DistanceSquared(deposits[i].Position, hit.Position) <= merge2) known = deposits[i];
                if (known != null)
                {
                    if (scanned > known.Seen) known.Seen = scanned;
                    continue;   // already known: the incoming data is dropped
                }
                deposits.Add(new Deposit { Ore = hit.Ore, Position = hit.Position, VoxelId = hit.VoxelId, Voxels = hit.Voxels, Seen = scanned });
                changed = true;
                Log.Debug("MS {0}: new deposit {1} at {2} ({3})", entityId, hit.Ore, hit.Position, source);
            }
            // Too many: the farthest go
            int max = MiningConfig.MaxDepositsPerSurveyor;
            if (deposits.Count > max)
            {
                Vector3D here = block.GetPosition();
                deposits.Sort((a, b) => Vector3D.DistanceSquared(a.Position, here).CompareTo(Vector3D.DistanceSquared(b.Position, here)));
                deposits.RemoveRange(max, deposits.Count - max);
                changed = true;
            }
            DateTime forget = DateTime.UtcNow.AddSeconds(-DEPLETED_MEMORY_SECONDS);
            depleted.RemoveAll(d => d.Seen < forget);
            if (changed) gpsDirty = true;
        }

        private Deposit NearestDeposit(MyStringHash ore)
        {
            Vector3D here = block.GetPosition();
            Deposit best = null;
            double bestD = double.MaxValue;
            for (int i = 0; i < deposits.Count; i++)
            {
                if (deposits[i].Ore != ore) continue;
                double d = Vector3D.DistanceSquared(deposits[i].Position, here);
                if (d < bestD) { bestD = d; best = deposits[i]; }
            }
            return best;
        }

        private bool DepositKnown(MyStringHash ore, Vector3D at)
        {
            double merge = MiningConfig.DepositMergeMeters;
            for (int i = 0; i < deposits.Count; i++)
                if (deposits[i].Ore == ore && Vector3D.DistanceSquared(deposits[i].Position, at) <= merge * merge) return true;
            return false;
        }

        private static string ShortOre(MyStringHash ore)
        {
            string s = ore.String;
            return s.StartsWith("Ore/", StringComparison.Ordinal) ? s.Substring(4) : s;
        }
        #endregion

        #region Orders
        private void ParseOrders()
        {
            ordersDirty = false;
            orders.Clear();
            var seen = new HashSet<MyStringHash>(MyStringHash.Comparer);
            for (int i = 0; i < settings.Orders.Count; i++)
            {
                OreOrder o;
                if (!OreOrder.TryParse(settings.Orders[i], out o) || !seen.Add(o.Ore)) continue;
                orders.Add(o);
            }
            if (!IsServer) return;
            // An order's count restarts when its amount is edited; orders removed drop their published jobs
            for (int i = 0; i < orders.Count; i++)
            {
                OrderProgress p;
                if (!progress.TryGetValue(orders[i].Ore, out p) || p.OrderKg != orders[i].Kg)
                    progress[orders[i].Ore] = new OrderProgress { OrderKg = orders[i].Kg, RemainingKg = orders[i].Kg };
            }
            var gone = new List<MyStringHash>();
            foreach (var k in progress.Keys) if (!seen.Contains(k)) gone.Add(k);
            for (int i = 0; i < gone.Count; i++) progress.Remove(gone[i]);
            for (int j = jobs.Count - 1; j >= 0; j--)
                if (jobs[j].Assigned == 0 && !seen.Contains(jobs[j].Ore))
                {
                    if (jobs[j].Announced) Withdraw(jobs[j], Loc.T(StrOrderRemoved));   // an auction may already run for it
                    jobs.RemoveAt(j);
                }
        }

        // Orchestrators drop the job's queue entry / running auction
        private void Withdraw(MiningJob job, string why)
        {
            if (messaging == null || antenna == null) return;
            var msg = new Message<JobStatusReport>
            {
                Payload = new JobStatusReport { AnnouncerEntityId = entityId, JobId = job.JobId, Outcome = JobOutcome.Aborted, Reason = why,
                                                JobType = Orchestrator.JobType.MineOre },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.JOB_WITHDRAWN,
            };
            var error = messaging.BroadcastMessage(antenna, msg, false);
            Log.Debug("MS {0}: job {1} ({2}) withdrawn ({3}): {4}", entityId, job.JobId, job.Ore, why, error);
        }

        // Back to its order: a job that won't be done (or was done short / over)
        private void Settle(MiningJob job, double deliveredKg, string why)
        {
            OrderProgress p;
            if (!settings.KeepOrdersConstant && progress.TryGetValue(job.Ore, out p))
                p.RemainingKg = Math.Max(0, p.RemainingKg + job.Kg - deliveredKg);
            Log.Debug("MS {0}: job {1} ({2}, {3:F0} kg) settled: {4:F0} kg delivered, {5}", entityId, job.JobId, job.Ore, job.Kg, deliveredKg, why);
        }
        #endregion

        #region Jobs
        private void PlanJobs(int now)
        {
            var config = MiningConfig;
            int published = 0;
            for (int i = 0; i < jobs.Count; i++) if (jobs[i].Assigned == 0) published++;
            for (int o = 0; o < orders.Count && published < config.MaxPublishedJobs; o++)
            {
                var order = orders[o];
                if (IsIgnoredOre(order.Ore.String)) continue;   // an old stone order: no jobs
                OrderProgress p;
                if (!progress.TryGetValue(order.Ore, out p)) continue;
                if (!settings.KeepOrdersConstant && p.RemainingKg < 1) continue;
                bool inAuction = false;
                for (int j = 0; j < jobs.Count && !inAuction; j++) inAuction = jobs[j].Ore == order.Ore && jobs[j].Assigned == 0;
                if (inAuction) continue;   // one job per ore waiting for a drone at a time
                var deposit = NearestDeposit(order.Ore);
                if (deposit == null) continue;
                double kg = settings.KeepOrdersConstant ? settings.JobSizeKg : Math.Min(p.RemainingKg, settings.JobSizeKg);
                var job = CreateJob(deposit, kg, now);
                if (job == null) continue;
                if (!settings.KeepOrdersConstant) p.RemainingKg -= kg;
                jobs.Add(job);
                published++;
            }
        }

        private MiningJob CreateJob(Deposit deposit, double kg, int now)
        {
            IMyEntity e;
            var voxel = MyAPIGateway.Entities.TryGetEntityById(deposit.VoxelId, out e) ? e as IMyVoxelBase : null;
            if (voxel == null)
            {
                deposits.Remove(deposit);
                Log.Debug("MS {0}: deposit {1} at {2}: its voxel map is gone", entityId, deposit.Ore, deposit.Position);
                return null;
            }
            // Bored from outside: away from the asteroid's / planet's centre, through to the deposit
            Vector3D outward = deposit.Position - voxel.PositionComp.WorldAABB.Center;
            if (outward.LengthSquared() < 1) outward = Vector3D.Up;
            outward.Normalize();
            Vector3D surface;
            if (!OreScanner.FindSurface(voxel, deposit.Position, outward, BORE_SEARCH_METERS, out surface))
            {
                Log.Debug("MS {0}: deposit {1} at {2}: no way in within {3:F0} m", entityId, deposit.Ore, deposit.Position, BORE_SEARCH_METERS);
                return null;
            }
            float interference;
            float g = MyAPIGateway.Physics.CalculateNaturalGravityAt(surface, out interference).Length();
            var items = new DiscreteInventory();
            items.AddItem(deposit.Ore, (int)Math.Ceiling(kg));
            Vector3D approach = surface + outward * 20;
            var mine = Orchestrator.Task.Mine();
            mine.Position = Vector3DData.FromVector3D(deposit.Position);   // the bore's end
            mine.ApproachFrom = Vector3DData.FromVector3D(surface);
            mine.TargetEntityId = deposit.VoxelId;
            mine.Inventory = items;
            var tasks = new List<Orchestrator.Task>
            {
                Orchestrator.Task.NavigateToolTo(0, surface, approach, g),
                mine,
                Orchestrator.Task.BackOut(0, approach),
            };
            uint jobId = IdGenerator.GenerateId(ref jobCounter, entityId);
            var ja = new JobAnnouncement
            {
                JobId = jobId,
                Type = JobType.MineOre,
                EntityType = AutomataEntityType.MiningSurveyorBlock,
                CreatedTime = DateTime.UtcNow,
                ComponentsInventory = items,
                PositionData = Vector3DData.FromVector3D(surface),
                NaturalGravity = g,
                IsStaticGrid = block.CubeGrid.IsStatic,
                IsInSpace = g < JobAnnouncement.SPACE_GRAVITY,
                IsInAtmosphere = g >= JobAnnouncement.SPACE_GRAVITY,
                Tasks = tasks,
                BlockCount = 1,
                RequesterEntityId = entityId,
            };
            AddEvent(Loc.F(StrJobKgAtMBore, jobId, kg, ShortOre(deposit.Ore),
                                   Vector3D.Distance(deposit.Position, block.GetPosition()), Vector3D.Distance(surface, deposit.Position)));
            Log.Info("MS {0}: job {1}: {2:F0} kg {3} at {4}, bore from {5} ({6:F0} m)", entityId, jobId, kg, deposit.Ore, deposit.Position, surface,
                     Vector3D.Distance(surface, deposit.Position));
            return new MiningJob { JobId = jobId, Ore = deposit.Ore, Deposit = deposit.Position, Kg = kg, Message = AnnouncementMessage(ja) };
        }

        private Message<JobAnnouncement> AnnouncementMessage(JobAnnouncement ja)
        {
            return new Message<JobAnnouncement>
            {
                Payload = ja,
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RequiresAck = false,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.MINING_JOB_ANNOUNCEMENT,
            };
        }

        // Each job once; again if that reached no orchestrator (soon) or nothing came back (after a long while)
        private void Announce(int now)
        {
            for (int j = 0; j < jobs.Count; j++)
            {
                var job = jobs[j];
                if (job.Assigned != 0) continue;
                if (job.HeldUntil != 0)
                {
                    if (now < job.HeldUntil) continue;
                    job.HeldUntil = 0;
                    job.LastAnnounced = int.MinValue / 2;
                }
                if (now - job.LastAnnounced < (job.Announced ? RESPONSE_TIMEOUT_TICKS : RETRY_TICKS)) continue;
                job.LastAnnounced = now;
                job.Message.MessageId = IdGenerator.GenerateId(ref messageCounter, entityId);
                job.Message.CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
                var error = messaging.BroadcastMessage(antenna, job.Message, false);
                job.Announced = error == ErrorCode.None;
                job.LastResult = error == ErrorCode.None ? Loc.T(StrAnnounced) : error == ErrorCode.NoSubscribers ? Loc.T(StrNoOrchestratorInRange) : error.ToString();
                Log.Debug("MS {0}: job {1} ({2}, {3:F0} kg): {4}", entityId, job.JobId, job.Ore, job.Kg, job.LastResult);
            }
        }

        private MiningJob FindJob(uint jobId)
        {
            for (int j = 0; j < jobs.Count; j++) if (jobs[j].JobId == jobId) return jobs[j];
            return null;
        }

        private void ReadAwards(int now)
        {
            messaging.ReadMessages(entityId, antenna, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT, awardInbox, 20, true, PayloadType.AuctionWinnerAnnouncement);
            for (int i = 0; i < awardInbox.Count; i++)
            {
                var award = awardInbox[i].Payload;
                if (award == null || award.AnnouncerEntityId != entityId || !AnchorDirectory.IsAllowed(block.OwnerId, awardInbox[i].SenderOwnerId)) continue;
                var job = FindJob(award.JobId);
                if (job == null || job.Assigned != 0) continue;
                job.Assigned = now;
                job.HeldUntil = 0;
                job.DronesOut = award.TaskAssignments != null ? award.TaskAssignments.Count : 0;
                job.LastResult = Loc.T(StrAssigned);
                long drone = 0;
                if (award.TaskAssignments != null) foreach (var k in award.TaskAssignments.Keys) { drone = k; break; }
                AddEvent(Loc.F(StrJobDroneOnItsWay, ShortOre(job.Ore), Nicknames.Of(drone)));
            }
            awardInbox.Clear();
        }

        private void ReadFailures(int now)
        {
            messaging.ReadMessages(entityId, antenna, Channel.JOB_ANNOUNCEMENT_FAILED, failureInbox, 20, true, PayloadType.JobAnnouncementFailed);
            for (int i = 0; i < failureInbox.Count; i++)
            {
                var f = failureInbox[i].Payload;
                if (f == null || f.AnnouncerEntityId != entityId || !AnchorDirectory.IsAllowed(block.OwnerId, failureInbox[i].SenderOwnerId)) continue;
                var job = FindJob(f.JobId);
                if (job == null || job.Assigned != 0 || (job.HeldUntil != 0 && job.HeldUntil > now)) continue;
                job.HeldUntil = now + Math.Max(10, f.HoldSeconds) * 60;
                job.LastResult = Loc.F(StrOnHoldReason, f.Reason);
                failedCount++;
                AddEvent(Loc.F(StrJobOnHoldS, job.JobId, f.HoldSeconds, f.Reason));
            }
            failureInbox.Clear();
        }

        private void ReadReports()
        {
            for (int c = 0; c < 2; c++)
            {
                messaging.ReadMessages(entityId, antenna, c == 0 ? Channel.TASKS_COMPLETED : Channel.TASKS_ABORTED, reportInbox, 20, true, PayloadType.JobStatusReport);
                for (int i = 0; i < reportInbox.Count; i++)
                {
                    var r = reportInbox[i].Payload;
                    if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId
                        || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                    droneWatch.Done(r.DroneEntityId);
                    var job = FindJob(r.JobId);
                    if (job == null) continue;   // (an award never heard here still counts: the drone did the job)
                    if (job.Assigned == 0) job.DronesOut = Math.Max(job.DronesOut, 1);
                    job.DronesOut--;
                    job.DeliveredKg += Math.Max(0, r.OreKg);
                    if (r.OreKg > 0) deliveredCount += (int)r.OreKg;
                    OrderProgress op;
                    if (r.OreKg > 0 && progress.TryGetValue(job.Ore, out op)) op.DeliveredKg += r.OreKg;   // the header's requests
                    AddEvent(r.OreKg > 0 ? Loc.F(StrLogDelivered, Nicknames.Of(r.DroneEntityId), ShortOre(job.Ore), r.OreKg)
                                         : Loc.F(StrLogDroneOutcome, Nicknames.Of(r.DroneEntityId), r.Outcome, ShortOre(job.Ore))
                                           + (string.IsNullOrEmpty(r.Reason) ? "" : "\n  " + r.Reason));
                    if (job.DronesOut > 0) continue;   // more miners on it: settled with the last report
                    Settle(job, job.DeliveredKg, "drones done, last " + r.Outcome + (string.IsNullOrEmpty(r.Reason) ? "" : " (" + r.Reason + ")"));
                    jobs.Remove(job);
                }
                reportInbox.Clear();
            }
        }

        // Drones still mining for an awarded job: its expiry starts again (a dead / destroyed / lost drone stops sending)
        private readonly Util.DroneWatch droneWatch = new Util.DroneWatch();

        private void ReadKeepalives(int now)
        {
            messaging.ReadMessages(entityId, antenna, Channel.JOB_KEEPALIVE, reportInbox, 50, true, PayloadType.JobStatusReport);
            for (int i = 0; i < reportInbox.Count; i++)
            {
                var r = reportInbox[i].Payload;
                if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId
                    || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                droneWatch.Heard(r.DroneEntityId, r.JobId, now, AddEvent);
                var job = FindJob(r.JobId);
                if (job != null) job.Assigned = now;   // an award never heard here: assigned now
            }
            reportInbox.Clear();
        }

        // Awarded, then no keepalive from the drone (lost...), or waiting on a deposit found depleted since: counted back
        private void ExpireJobs(int now)
        {
            for (int j = jobs.Count - 1; j >= 0; j--)
            {
                var job = jobs[j];
                if (job.Assigned != 0 && now - job.Assigned > ASSIGNED_TTL_TICKS) { Settle(job, 0, "no drone keepalive"); jobs.RemoveAt(j); continue; }
                if (job.Assigned == 0 && !DepositKnown(job.Ore, job.Deposit)) { Settle(job, 0, "deposit depleted"); jobs.RemoveAt(j); }
            }
        }
        #endregion

        #region Players: ore list, GPS
        // The ores of known deposits into the synced settings (every player's terminal lists them)
        private void PublishKnownOres(int now)
        {
            if (now - lastKnownOres < KNOWN_ORES_TICKS) return;
            lastKnownOres = now;
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < deposits.Count; i++)
            {
                if (IsIgnoredOre(deposits[i].Ore.String)) continue;   // stone: not listed
                int n;
                counts.TryGetValue(deposits[i].Ore.String, out n);
                counts[deposits[i].Ore.String] = n + 1;
            }
            var list = new List<string>();
            foreach (var kv in counts) list.Add(kv.Key + ":" + kv.Value);
            bool same = list.Count == settings.KnownOres.Count;
            for (int i = 0; i < list.Count && same; i++) same = list[i] == settings.KnownOres[i];
            if (same) return;
            settings.KnownOres = list;
            SettingsEdited();   // saved and sent to the players
        }

        private void UpdateGps(int now)
        {
            if (!settings.ShowGps || block.OwnerId == 0)
            {
                if (gpsHashes.Count > 0) RemoveGps();
                return;
            }
            if (!gpsDirty && now - lastGps < GPS_REFRESH_TICKS && gpsIdentity == block.OwnerId) return;
            lastGps = now;
            gpsDirty = false;
            RemoveGps();
            gpsIdentity = block.OwnerId;   // the owner only: deposits are never shown to anyone else
            AddDepositGps(gpsHashes);
        }

        /// <summary>The "Show known deposits" button: the known deposits as temporary GPS markers once (the game drops them after a few
        /// minutes), the previous ones of the button replaced. Owner only, like the checkbox.</summary>
        private void ShowDepositsOnce()
        {
            RemoveGps(requestedHashes);
            if (block.OwnerId == 0) return;
            gpsIdentity = block.OwnerId;
            int n = AddDepositGps(requestedHashes);
            AddEvent(Loc.F(StrDepositsShownAsGps, n));
            Log.Info("MS {0}: show known deposits requested: {1} marker(s) of {2} deposit(s)", entityId, n, deposits.Count);
        }

        // Temporary GPS markers (nearest deposits first, up to MaxGpsMarkers) for the owner; their hashes into 'hashes'
        private int AddDepositGps(List<int> hashes)
        {
            Vector3D here = block.GetPosition();
            var shown = new List<Deposit>(deposits);
            shown.Sort((a, b) => Vector3D.DistanceSquared(a.Position, here).CompareTo(Vector3D.DistanceSquared(b.Position, here)));
            int max = Math.Min(shown.Count, MiningConfig.MaxGpsMarkers);
            for (int i = 0; i < max; i++)
            {
                var d = shown[i];
                var gps = MyAPIGateway.Session.GPS.Create(ShortOre(d.Ore) + " (" + block.CustomName + ")",
                                                          Loc.F(StrGpsDescription, block.CustomName), d.Position, true, true);
                gps.GPSColor = Color.Gold;
                MyAPIGateway.Session.GPS.AddGps(gpsIdentity, gps);
                hashes.Add(gps.Hash);
            }
            return max;
        }

        private void RemoveGps()
        {
            RemoveGps(gpsHashes);
        }

        private void RemoveGps(List<int> hashes)
        {
            for (int i = 0; i < hashes.Count; i++) MyAPIGateway.Session.GPS.RemoveGps(gpsIdentity, hashes[i]);
            hashes.Clear();
        }
        #endregion

        #region Save / load
        private MiningSurveyorState CaptureState()
        {
            var s = new MiningSurveyorState { JobCounter = jobCounter, MessageCounter = messageCounter, Delivered = deliveredCount, Failed = failedCount };
            DateTime utc = DateTime.UtcNow;
            for (int i = 0; i < deposits.Count; i++) s.Deposits.Add(SaveDeposit(deposits[i], utc));
            for (int i = 0; i < depleted.Count; i++) s.Depleted.Add(SaveDeposit(depleted[i], utc));
            foreach (var kv in progress) s.Orders.Add(new OrderProgressState { Ore = kv.Key.String, OrderKg = kv.Value.OrderKg, RemainingKg = kv.Value.RemainingKg, DeliveredKg = kv.Value.DeliveredKg });
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            for (int j = 0; j < jobs.Count; j++)
            {
                var job = jobs[j];
                s.Jobs.Add(new MiningJobState
                {
                    Announcement = job.Message.Payload, Ore = job.Ore.String, Deposit = Vector3DData.FromVector3D(job.Deposit), Kg = job.Kg,
                    AssignedAgeTicks = job.Assigned != 0 ? BlockSave.Age(job.Assigned) : -1, HoldTicksLeft = job.HeldUntil > now ? job.HeldUntil - now : 0,
                    DronesOut = job.DronesOut, Announced = job.Announced,
                });
            }
            if (messaging != null) messaging.CollectSent(entityId, s.Outbox);
            Log.Debug("MS {0}: state saved: {1} deposit(s), {2} depleted, {3} order(s), {4} job(s)", entityId, s.Deposits.Count, s.Depleted.Count, s.Orders.Count, s.Jobs.Count);
            return s;
        }

        private static DepositState SaveDeposit(Deposit d, DateTime utc)
        {
            return new DepositState { Ore = d.Ore.String, Position = Vector3DData.FromVector3D(d.Position), VoxelId = d.VoxelId, Voxels = d.Voxels,
                                      AgeSeconds = Math.Max(0, (utc - d.Seen).TotalSeconds) };
        }

        private static Deposit LoadDeposit(DepositState d, DateTime utc)
        {
            return new Deposit { Ore = MyStringHash.GetOrCompute(d.Ore), Position = d.Position.ToVector3D(), VoxelId = d.VoxelId, Voxels = d.Voxels,
                                 Seen = utc.AddSeconds(-Math.Max(0, d.AgeSeconds)) };
        }

        private void RestoreState(MiningSurveyorState s)
        {
            jobCounter = s.JobCounter;
            messageCounter = s.MessageCounter;
            deliveredCount = s.Delivered;
            failedCount = s.Failed;
            DateTime utc = DateTime.UtcNow;
            if (s.Deposits != null) for (int i = 0; i < s.Deposits.Count; i++) if (s.Deposits[i] != null && s.Deposits[i].Ore != null) deposits.Add(LoadDeposit(s.Deposits[i], utc));
            if (s.Depleted != null) for (int i = 0; i < s.Depleted.Count; i++) if (s.Depleted[i] != null && s.Depleted[i].Ore != null) depleted.Add(LoadDeposit(s.Depleted[i], utc));
            if (s.Orders != null)
                for (int i = 0; i < s.Orders.Count; i++)
                    if (s.Orders[i] != null && s.Orders[i].Ore != null)
                        progress[MyStringHash.GetOrCompute(s.Orders[i].Ore)] = new OrderProgress { OrderKg = s.Orders[i].OrderKg, RemainingKg = s.Orders[i].RemainingKg, DeliveredKg = s.Orders[i].DeliveredKg };
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (s.Jobs != null)
                for (int j = 0; j < s.Jobs.Count; j++)
                {
                    var js = s.Jobs[j];
                    if (js == null || js.Announcement == null || js.Ore == null) continue;
                    jobs.Add(new MiningJob
                    {
                        JobId = js.Announcement.JobId, Ore = MyStringHash.GetOrCompute(js.Ore), Deposit = js.Deposit.ToVector3D(), Kg = js.Kg,
                        Message = AnnouncementMessage(js.Announcement),
                        Assigned = js.AssignedAgeTicks >= 0 ? Math.Max(1, BlockSave.FrameFromAge(js.AssignedAgeTicks)) : 0,
                        HeldUntil = js.HoldTicksLeft > 0 ? now + js.HoldTicksLeft : 0, DronesOut = js.DronesOut,
                        Announced = false,   // once more soon (orchestrators de-duplicate)
                    });
                }
            ordersDirty = true;   // progress of orders edited while saved is restarted
            gpsDirty = true;
            int sent = messaging.RestoreSent(s.Outbox);
            Log.Info("MS {0}: state restored: {1} deposit(s), {2} depleted, {3} order(s), {4} job(s), {5} message(s) back in flight", entityId,
                     deposits.Count, depleted.Count, progress.Count, jobs.Count, sent);
        }
        #endregion

        #region Info
        // A log entry (terminal and LCDs): first line at the left edge, its other lines indented
        private void AddEvent(string text)
        {
            log.Add(text);
        }

        private void AppendInfo(IMyTerminalBlock b, StringBuilder sb)
        {
            if (!ready) return;
            info.Clear();
            if (IsServer) info.AppendLoc(StrInfoStatus, status).Append('\n');
            info.AppendLoc(StrKnownDeposits).Append(' ');
            if (settings.KnownOres.Count == 0) info.AppendLoc(StrNone);
            for (int i = 0; i < settings.KnownOres.Count; i++) info.Append(i > 0 ? ", " : "").Append(settings.KnownOres[i].StartsWith("Ore/", StringComparison.Ordinal) ? settings.KnownOres[i].Substring(4) : settings.KnownOres[i]);
            info.Append('\n');
            if (IsServer)
            {
                for (int i = 0; i < orders.Count; i++)
                {
                    OrderProgress p;
                    progress.TryGetValue(orders[i].Ore, out p);
                    info.AppendLoc(StrText, orders[i], settings.KeepOrdersConstant ? Loc.T(StrConstant) : p != null ? Loc.F(StrKgLeft, p.RemainingKg) : "-");
                }
                info.AppendLoc(StrInfoDelivered, deliveredCount, failedCount).Append('\n');
                BuildHeader();
                info.Append('\n');
                for (int i = 0; i < header.Count; i++) info.Append(header[i]).Append('\n');
                info.Append(new string('-', 40)).Append('\n');
                log.AppendTo(info);
            }
            sb.Append(info);
        }
        #endregion

        #region Terminal
        public bool Terminal_IsEnabled
        {
            get { return settings.Enabled; }
            set { settings.Enabled = value; SettingsEdited(); }
        }

        public bool Terminal_KeepOrdersConstant
        {
            get { return settings.KeepOrdersConstant; }
            set { settings.KeepOrdersConstant = value; SettingsEdited(); }
        }

        public const string STONE_KEY = "Ore/Stone";
        // Stone: every voxel gives it when it isn't an ore, so it is no deposit and gets no jobs (a "mine stone" option
        // may come later; IgnoreStone is kept in the settings for it)
        private static bool IsIgnoredOre(string key) { return key == STONE_KEY; }

        public bool Terminal_ShowGps
        {
            get { return settings.ShowGps; }
            set { settings.ShowGps = value; SettingsEdited(); }
        }

        public float Terminal_JobSizeKg
        {
            get { return settings.JobSizeKg; }
            set { settings.JobSizeKg = MathHelper.Clamp((int)(Math.Round(value / 1000.0) * 1000), JOB_SIZE_MIN, JOB_SIZE_MAX); SettingsEdited(); }
        }

        public List<string> Terminal_KnownOres { get { return settings.KnownOres; } }
        public List<string> Terminal_Orders { get { return settings.Orders; } }
        public MyStringHash Terminal_SelectedOre { get; private set; }
        public int Terminal_SelectedOrder { get; private set; } = -1;
        public readonly StringBuilder Terminal_EditText = new StringBuilder();

        public HashSet<MyStringHash> Terminal_OrderKeys()
        {
            var keys = new HashSet<MyStringHash>(MyStringHash.Comparer);
            for (int i = 0; i < settings.Orders.Count; i++)
            {
                OreOrder o;
                if (OreOrder.TryParse(settings.Orders[i], out o)) keys.Add(o.Ore);
            }
            return keys;
        }

        public void Terminal_SelectOre(MyStringHash ore)
        {
            Terminal_SelectedOre = ore;
            Terminal_SelectedOrder = -1;
            Terminal_EditText.Clear().Append(OreOrder.Format(ore, settings.JobSizeKg));
        }

        public void Terminal_SelectOrder(int index)
        {
            if (index < 0 || index >= settings.Orders.Count) return;
            Terminal_SelectedOrder = index;
            Terminal_EditText.Clear().Append(settings.Orders[index]);
        }

        private bool TryEdit(out OreOrder order)
        {
            if (!OreOrder.TryParse(Terminal_EditText.ToString(), out order) || order.Kg <= 0) return false;
            Terminal_EditText.Clear().Append(order.ToString());
            return true;
        }

        public bool Terminal_AddOrder()
        {
            OreOrder o;
            if (!TryEdit(out o)) return false;
            if (IsIgnoredOre(o.Ore.String)) return false;   // stone ignored (checkbox)
            for (int i = 0; i < settings.Orders.Count; i++)
            {
                OreOrder e;
                if (OreOrder.TryParse(settings.Orders[i], out e) && e.Ore == o.Ore) { settings.Orders[i] = o.ToString(); Terminal_SelectedOrder = i; SettingsEdited(); return true; }
            }
            settings.Orders.Add(o.ToString());
            Terminal_SelectedOrder = settings.Orders.Count - 1;
            SettingsEdited();
            return true;
        }

        public bool Terminal_EditOrder()
        {
            OreOrder o;
            if (Terminal_SelectedOrder < 0 || Terminal_SelectedOrder >= settings.Orders.Count || !TryEdit(out o)) return false;
            settings.Orders[Terminal_SelectedOrder] = o.ToString();
            SettingsEdited();
            return true;
        }

        public bool Terminal_RemoveOrder()
        {
            if (Terminal_SelectedOrder < 0 || Terminal_SelectedOrder >= settings.Orders.Count) return false;
            settings.Orders.RemoveAt(Terminal_SelectedOrder);
            Terminal_SelectedOrder = -1;
            SettingsEdited();
            return true;
        }

        public void Terminal_ClearOrders()
        {
            settings.Orders.Clear();
            Terminal_SelectedOrder = -1;
            SettingsEdited();
        }
        #endregion
            #region Tagged LCDs
        private readonly BlockLcd lcd = new BlockLcd();
        private readonly List<string> header = new List<string>();
        private const int HEADER_ROWS = 5;
        private const int HEADER_COLUMN = 25;

        /// <summary>The nickname as set (empty: logs show the block's name).</summary>
        public string Nickname { get { return settings.Nickname ?? ""; } }

        public string Terminal_Nickname
        {
            set { value = value ?? ""; if (settings.Nickname == value) return; settings.Nickname = value; SettingsEdited(); }
        }

        public LcdStyle Terminal_LcdStyle { get { if (settings.Lcd == null) settings.Lcd = new LcdStyle(); return settings.Lcd; } }
        public void Terminal_LcdEdited() { SettingsEdited(); }

        // "MC <nickname>", then known deposits (left) and active requests, delivered / ordered kg (right): 5 rows a page,
        // paged by the "Header PgUp / PgDn" actions only
        private void BuildHeader()
        {
            lcdDeposits.Clear();
            for (int i = 0; i < settings.KnownOres.Count; i++)
            {
                string k = settings.KnownOres[i];
                if (k.StartsWith("Ore/", StringComparison.Ordinal)) k = k.Substring(4);
                int c = k.LastIndexOf(':');
                lcdDeposits.Add(c > 0 ? k.Substring(0, c) + "=" + k.Substring(c + 1) : k);
            }
            lcdRequests.Clear();
            for (int i = 0; i < orders.Count; i++)
            {
                OrderProgress p;
                progress.TryGetValue(orders[i].Ore, out p);
                if (!settings.KeepOrdersConstant && p != null && p.RemainingKg < 1 && !HasJobFor(orders[i].Ore)) continue;   // done: not active
                string ore = ShortOre(orders[i].Ore);
                double delivered = p != null ? p.DeliveredKg : 0;
                lcdRequests.Add(ore + "=" + delivered.ToString("F0") + "/" + (settings.KeepOrdersConstant ? Loc.T(StrConstant) : orders[i].Kg.ToString()));
            }
            int first;
            int pages = BlockLcd.Paginate(settings.Lcd, Math.Max(lcdDeposits.Count, lcdRequests.Count), HEADER_ROWS, out first);
            header.Clear();
            header.Add(Loc.F(StrHeaderTitle, Nicknames.Of(Entity)));
            string title = Loc.T(StrHeaderRequests);
            if (pages > 1) title += " " + Loc.F(StrHeaderPage, first / HEADER_ROWS + 1, pages);
            header.Add(BlockLcd.Columns(Loc.T(StrHeaderDeposits), title, HEADER_COLUMN));
            for (int r = 0; r < HEADER_ROWS; r++)
            {
                int k = first + r;
                header.Add(BlockLcd.Columns(k < lcdDeposits.Count ? lcdDeposits[k] : "", k < lcdRequests.Count ? lcdRequests[k] : "", HEADER_COLUMN));
            }
        }
        private readonly StringBuilder lcdText = new StringBuilder();

        // Server: the status text on the tagged LCDs
        private readonly List<string> lcdDeposits = new List<string>();
        private readonly List<string> lcdRequests = new List<string>();

        // Header: known deposits (left), active requests (right); below: name, status, deliveries, events
        private void UpdateLcd()
        {
            if (!ready || !lcd.Wanted(block, settings.LcdTag)) return;   // nobody looks at an LCD: nothing is built
            if (Terminal_LcdStyle.ShowHeader) BuildHeader(); else header.Clear();
            lcd.Publish(Terminal_LcdStyle, header, log);
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
