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
using Automata.Util.Strings;

namespace Automata.Mining
{
    /// <summary>
    /// Mining Surveyor (a programmable block subtype). Knows ore deposits: its own grid's ore detector scans
    /// (<see cref="OreScanner"/>), and drones' survey data (MINING_DATA_FOUND_ANNOUNCEMENT) when they come into antenna
    /// range. Same ore within the merge distance (50 m) of a known deposit is that deposit; a scan that covers a known
    /// deposit without finding it again marks it depleted. Ore orders ("Ore/Iron:100000", kg) turn into MineOre jobs on
    /// the nearest known deposit no other job is on (else the nearest), with more sites for pooled miners (CreateJob:
    /// other deposits of the ore first, then other ways in), each counted off its order when announced and settled by the drone's report (what
    /// it really unloaded); "keep orders constant" never counts down. Jobs go to orchestrators like a construction
    /// computer's (cached, holds, awards, drone reports). Deposits can be shown to the owner as temporary GPS markers.
    /// Server only; deposits and jobs are kept through a world save.
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, "AutomataLargeMiningSurveyor")]
    public class MiningSurveyorBlock : SettingsBlockLogic, INicknamed
    {

        #region Timing
        private const int STEP_EVERY = 10;                       // UpdateBeforeSimulation10 calls per step (100 ticks)
        private const int RETRY_TICKS = 20 * 60;                 // an announcement that reached nobody: tried again
        private const int RESPONSE_TIMEOUT_TICKS = 60 * 60;      // announced, nothing back since: announced again (was 5 min)
        private const int ASSIGNED_TTL_TICKS = 30 * 60 * 60;     // awarded, then no sign of the drone (award / keepalive every 60 s) this long: counted back
        private const double DEPLETED_MEMORY_SECONDS = 3600;     // a depleted deposit isn't brought back by older scans
        private const int GPS_REFRESH_TICKS = 600;
        private const int GPS_ON_TICKS = 180 * 60;               // "Show deposits as GPS" turns itself off after this long
        private const int KNOWN_ORES_TICKS = 600;                // the terminal's ore list sent to players at most this often
        #endregion
        private const double BORE_SEARCH_METERS = 400;
        // Sites of one job, for the miners an orchestrator pools on it (one each, in this order: other deposits of the ore,
        // then other ways in to them; past these the orchestrator bores parallel to them)
        private const int SITE_DEPOSITS_MAX = 4;                 // deposits of the ore one job offers
        private const int SITE_ENTRIES_MAX = 3;                  // ways in per deposit (space): the radial one and others
        private const int SITES_MAX = 8;
        private const double ENTRY_ANGLE_MIN = 45;               // degrees between two ways in to one deposit
        private const double ENTRY_FREE = 30;                    // m of open line out of a way in (approach point and on: no closed cave)
        private const double ENTRY_LONGER = 1.5;                 // another way in: at most this x the radial bore...
        private const double ENTRY_SLACK = 40;                   // ...plus this
        private const double MINE_APPROACH_OUT = 20;             // m out along the bore line: the approach / back-out point
        private const double DEPLETION_MARGIN = 10;              // a deposit this close to a scan's edge isn't judged by it
        private const int BAD_WAY_TICKS = 30 * 60 * 60;          // a way in a miner couldn't reach: left out of new jobs this long
        private const double BAD_WAY_NEAR = 5;                   // m: a way in starting this close to a bad one is that one

        // Ways in a miner reported unreachable (2026-11-07: the same unreachable Platinum way in was issued 10 times, a
        // 10 km round trip each): left out of new jobs for BAD_WAY_TICKS (runtime only)
        private struct BadWay { public Vector3D Surface; public int Until; }
        private readonly List<BadWay> badWays = new List<BadWay>();

        private bool IsBadWay(Vector3D surface, int now)
        {
            badWays.RemoveAll(b => b.Until <= now);
            for (int i = 0; i < badWays.Count; i++)
                if (Vector3D.DistanceSquared(badWays[i].Surface, surface) <= BAD_WAY_NEAR * BAD_WAY_NEAR) return true;
            return false;
        }
        public const int JOB_SIZE_MIN = 1000, JOB_SIZE_MAX = 200000;

        private MiningSurveyorSettings settings = new MiningSurveyorSettings();
        private long entityId;
        private MessageQueue messaging;
        private IMyRadioAntenna antenna;
        private GridAntennaReg antennaReg;
        private IMyOreDetector detector;
        private bool ready;
        private int stepCounter;
        private int lastSearch = int.MinValue / 2, lastOwnScan = int.MinValue / 2, lastGps = int.MinValue / 2, lastKnownOres = int.MinValue / 2;
        private string status = Loc.T(MyStrings.Mining_Starting);
        private readonly StringBuilder info = new StringBuilder();
        private readonly List<IMyCubeGrid> gridBuffer = new List<IMyCubeGrid>();

        private sealed class Deposit
        {
            public MyStringHash Ore;
            public Vector3D Position;
            public long VoxelId;
            public int Voxels;
            public DateTime Seen;   // last seen (deposits) / found depleted (depleted list)
            public int DistanceM = -1;   // its distance from the surveyor when learnt, to the 100 m (-1: not yet; never recalculated)
        }

        // A deposit's distance bin: from this surveyor, rounded to the 100 m
        private int DistanceBin(Vector3D p)
        {
            return (int)(Math.Round(Vector3D.Distance(p, block.GetPosition()) / 100.0) * 100);
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
            public List<Vector3D> Used;  // assigned: where the award's miners bore (runtime; null: every site the job offers)
            public Dictionary<long, List<Vector3D>> WaysOf;   // assigned: each miner's ways in (their bore start points; runtime)
            public double DeliveredKg;   // reported so far by the drones of the award (several miners on one job)
            public bool Announced;
            public bool Pending;   // an announcement is in the network: told whether it was taken once its life is up
            public string LastResult;
            public long ForLc;           // mined for this logistics computer's ore request (0: the surveyor's own order)
            public int ForLcNetwork;     // ...for this network of it (CN index)
        }
        private readonly List<MiningJob> jobs = new List<MiningJob>();
        private int jobCounter, messageCounter, deliveredCount, failedCount;
        private long lastStateSignature;
        private int lastStateWrite = int.MinValue / 2;
        private const int STATE_WRITE_TICKS = 10 * 60;           // the state written into the storage on a change, at most this often
        private MiningSurveyorState pendingState;

        private readonly OreScanner scanner = new OreScanner();
        private readonly List<OreHit> scanHits = new List<OreHit>();
        private readonly List<int> gpsHashes = new List<int>();
        private long gpsIdentity;
        private bool gpsDirty = true;
        private bool gpsCleaned;                                 // this block's markers left from before a reload were removed
        private int gpsOffAt;                                    // frame the "Show deposits as GPS" switch turns itself off (0: it is off)
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
            if (Log.DebugEnabled) Log.Debug("MS {0}: '{1}' on '{2}': {3} order(s), {4} saved deposit(s)", entityId, block.CustomName, block.CubeGrid.DisplayName, settings.Orders.Count, pendingState != null && pendingState.Deposits != null ? pendingState.Deposits.Count : 0);
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
                status = Loc.F(MyStrings.Mining_Error, ex.Message);
            }
        }

        public override void Close()
        {
            try
            {
                if (block != null) block.AppendingCustomInfo -= AppendInfo;
                All.Remove(this);
                if (IsServer) RemoveGps();
                if (messaging != null) { messaging.ReaderClosed(entityId); }
                if (Log.DebugEnabled) Log.Debug("MS {0}: closed ({1} deposit(s), {2} job(s))", entityId, deposits.Count, jobs.Count);
            }
            catch (Exception ex)
            {
                Log.Error("MS {0}: close error: {1}", entityId, ex);
            }
            base.Close();
        }

        protected override byte[] SettingsBinary()
        {
            return MyAPIGateway.Utilities.SerializeToBinary(settings);
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

        protected override bool LoadSettingsBinary(byte[] data)
        {
            var loaded = MyAPIGateway.Utilities.SerializeFromBinary<MiningSurveyorSettings>(data);
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
            CheckDeleteDepositsRequest();
            CheckDeleteSelectedRequest();
        }

        #region Delete deposits (terminal button, the selected ore / distance bin)
        private int lastDeleteSelected;
        private bool deleteSelectedSeen;

        private void CheckDeleteSelectedRequest()
        {
            if (deleteSelectedSeen && settings.DeleteSelectedRequest != lastDeleteSelected && IsServer) DeleteSelectedDeposits();
            lastDeleteSelected = settings.DeleteSelectedRequest;
            deleteSelectedSeen = true;
        }

        /// <summary>Terminal list view: deposits grouped by distance bin (this player's view only, not saved).</summary>
        public bool Terminal_ByDistance;
        /// <summary>The distance bin selected in the "by distance" view (-1: none).</summary>
        public int Terminal_SelectedBin { get; private set; } = -1;

        public void Terminal_SelectBin(MyStringHash ore, int bin)
        {
            Terminal_SelectOre(ore);
            Terminal_SelectedBin = bin;
        }

        // The selected ore's deposits (by distance: those in the selected bin) deleted by the server
        public bool Terminal_DeleteSelectedDeposits()
        {
            if (Terminal_SelectedOre == MyStringHash.NullOrEmpty) return false;
            if (Terminal_ByDistance && Terminal_SelectedBin < 0) return false;
            settings.DeleteSelectedOre = Terminal_SelectedOre.String;
            settings.DeleteSelectedBin = Terminal_ByDistance ? Terminal_SelectedBin : -1;
            settings.DeleteSelectedRequest++;
            SettingsEdited();
            Terminal_SelectedOre = MyStringHash.NullOrEmpty;
            Terminal_SelectedBin = -1;
            return true;
        }

        // The jobs no drone has taken for them go with the next pass (their deposit is no longer known)
        private void DeleteSelectedDeposits()
        {
            var ore = MyStringHash.GetOrCompute(settings.DeleteSelectedOre ?? "");
            int bin = settings.DeleteSelectedBin;
            int n = deposits.RemoveAll(d => d.Ore == ore && (bin < 0 || d.DistanceM == bin));
            if (n == 0) return;
            lastKnownOres = int.MinValue / 2;
            gpsDirty = true;
            ordersDirty = true;
            AddEvent(Loc.F(MyStrings.Mining_DeletedKnownDeposit, n));
            if (Log.DebugEnabled) Log.Debug("MS {0}: {1} deposit(s) of {2} deleted on request (bin {3})", entityId, n, ore, bin);
        }
        #endregion

        #region Retry jobs (terminal button)
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
            AddEvent(Loc.F(MyStrings.Mining_DeletedKnownDeposit, n));
            if (Log.DebugEnabled) Log.Debug("MS {0}: known deposits deleted on request: {1} deposit(s)", entityId, n);
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
            AddEvent(Loc.F(MyStrings.Mining_RetryJobAnnouncedAgain, n));
            if (Log.DebugEnabled) Log.Debug("MS {0}: retry requested: {1} unassigned job(s) announced again", entityId, n);
        }
        #endregion

        private void Normalize()
        {
            if (settings.Orders == null) settings.Orders = new List<string>();
            if (settings.KnownOres == null) settings.KnownOres = new List<string>();
            if (settings.KnownOreBins == null) settings.KnownOreBins = new List<string>();
            settings.JobSizeKg = MathHelper.Clamp(settings.JobSizeKg, JOB_SIZE_MIN, JOB_SIZE_MAX);
        }
        #endregion

        #region Loop
        private void Step()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!retrySeen) CheckRetryRequest();   // loaded: the saved request counts as seen
            if (IsServer) CheckGpsTimer(now);
            if (!deleteSeen) CheckDeleteDepositsRequest();
            if (messaging == null) messaging = AutomataSession.GetMessageQueue();
            if (messaging == null) { status = Loc.T(MyStrings.Mining_WaitingForTheMod); return; }
            if (pendingState != null)
            {
                if (!BlockSave.CanRestore) { status = Loc.T(MyStrings.Mining_Loading); return; }
                RestoreState(pendingState);
                pendingState = null;
            }
            if (IsServer) SaveStateOnChange(now);
            if (!settings.Enabled || !block.IsWorking)
            {
                status = !settings.Enabled ? Loc.T(MyStrings.Mining_OffTerminal) : Loc.T(MyStrings.Mining_NotWorkingPowerDamage);
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
                    if (Log.DebugEnabled) Log.Debug("MS {0}: own scan started ({1:F0} m)", entityId, r);
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
            droneWatch.Check(now, block, false, "MS {0}", entityId, AddEvent);
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
            status = !online ? Loc.T(MyStrings.Mining_NoAntennaFunctionalOnBroadcastingDeposits)
                   : orders.Count == 0 ? Loc.T(MyStrings.Mining_NoOreOrders)
                   : jobs.Count == 0 ? Loc.T(MyStrings.Mining_NothingToMineOrdersDone)
                   : Loc.F(MyStrings.Mining_JobPublishedOnHoldOut, open, held, jobs.Count - open - held);
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
            if (bestAntenna != antenna) if (Log.DebugEnabled) Log.Debug("MS {0}: antenna {1}", entityId, bestAntenna != null ? "'" + bestAntenna.CustomName + "'" : "none");
            if (bestDetector != detector) if (Log.DebugEnabled) Log.Debug("MS {0}: ore detector {1}", entityId, bestDetector != null ? "'" + bestDetector.CustomName + "'" : "none");
            antenna = bestAntenna;
            detector = bestDetector;
        }

        private bool EnsureAntenna()
        {
            return GridAntennaReg.Ensure(messaging, ref antennaReg, block);
        }
        #endregion

        #region Deposits
        private void OwnScanDone()
        {
            if (Log.DebugEnabled) Log.Debug("MS {0}: own scan done: {1} cell(s) read, {2} hit(s)", entityId, scanner.CellsRead, scanHits.Count);
            CountDeposits(depositsBefore);
            ApplySweep(scanner.Center, scanner.Radius, DateTime.UtcNow, scanHits, Loc.T(MyStrings.Mining_OwnScan));
            scanHits.Clear();
            string found = NewOres(depositsBefore);
            if (found != null) AddEvent(Loc.F(MyStrings.Mining_SurveyOwnScanFound, found));
        }

        private readonly List<OreHit> sweepHits = new List<OreHit>();
        private readonly Dictionary<MyStringHash, int> depositsBefore = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);

        // A job out for the ore ('ownOnly': for the player's orders, not a logistics computer's request)
        private bool HasJobFor(MyStringHash ore, bool ownOnly = false)
        {
            for (int i = 0; i < jobs.Count; i++) if (jobs[i].Ore == ore && (!ownOnly || jobs[i].ForLc == 0)) return true;
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
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.MiningSurveyor, antennaReg, Channel.MINING_DATA_FOUND_ANNOUNCEMENT, surveyInbox, 20, true, PayloadType.MiningSurveyDataBroadcast);
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
                    ApplySweep(sweep.Center.ToVector3D(), sweep.Radius, sent.AddSeconds(-Math.Max(0, sweep.AgeSeconds)), sweepHits, Loc.F(MyStrings.Mining_DroneArg0Lower, Nicknames.Of(m.SenderId)));
                }
                string found = NewOres(depositsBefore);
                if (found != null) AddEvent(Loc.F(MyStrings.Mining_SurveyDroneFound, Nicknames.Of(m.SenderId), found));
                if (Log.DebugEnabled) Log.Debug("MS {0}: survey from drone {1}: {2} scan(s), deposits {3} -> {4}", entityId, m.SenderId, m.Payload.Sweeps.Count, before, deposits.Count);
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
                AddEvent(Loc.F(MyStrings.Mining_AtMDepleted, ShortOre(d.Ore), Vector3D.Distance(d.Position, block.GetPosition()), source));
                if (Log.DebugEnabled) Log.Debug("MS {0}: deposit {1} at {2} depleted ({3})", entityId, d.Ore, d.Position, source);
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
                deposits.Add(new Deposit { Ore = hit.Ore, Position = hit.Position, VoxelId = hit.VoxelId, Voxels = hit.Voxels, Seen = scanned,
                                           DistanceM = DistanceBin(hit.Position) });
                changed = true;
                if (Log.DebugEnabled) Log.Debug("MS {0}: new deposit {1} at {2} ({3})", entityId, hit.Ore, hit.Position, source);
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

        // The nearest deposit of the ore no job is on (miners at work or offered it), else the nearest of all
        private Deposit NearestDeposit(MyStringHash ore)
        {
            Vector3D here = block.GetPosition();
            Deposit best = null, bestFree = null;
            double bestD = double.MaxValue, bestFreeD = double.MaxValue;
            for (int i = 0; i < deposits.Count; i++)
            {
                if (deposits[i].Ore != ore) continue;
                double d = Vector3D.DistanceSquared(deposits[i].Position, here);
                if (d < bestD) { bestD = d; best = deposits[i]; }
                if (d < bestFreeD && !DepositBusy(deposits[i])) { bestFreeD = d; bestFree = deposits[i]; }
            }
            return bestFree ?? best;
        }

        // A job's miners bore there (awarded), or an auction offers it (every site of the job)
        private bool DepositBusy(Deposit d)
        {
            double merge = MiningConfig.DepositMergeMeters;
            for (int j = 0; j < jobs.Count; j++)
            {
                var job = jobs[j];
                if (job.Ore != d.Ore) continue;
                if (job.Used != null)
                {
                    for (int k = 0; k < job.Used.Count; k++)
                        if (Vector3D.DistanceSquared(job.Used[k], d.Position) <= merge * merge) return true;
                    continue;
                }
                var tasks = job.Message != null && job.Message.Payload != null ? job.Message.Payload.Tasks : null;
                if (tasks == null) continue;
                for (int k = 0; k < tasks.Count; k++)
                    if (tasks[k].Type == Orchestrator.TaskType.Mine && Vector3D.DistanceSquared(tasks[k].Position.ToVector3D(), d.Position) <= merge * merge) return true;
            }
            return false;
        }

        private struct WayIn { public Vector3D Surface, Outward; }
        private static readonly Vector3D[] entryDirs = EntryDirs();

        // 26 directions: the cube's faces, edges and corners
        private static Vector3D[] EntryDirs()
        {
            var list = new List<Vector3D>();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (x != 0 || y != 0 || z != 0) list.Add(Vector3D.Normalize(new Vector3D(x, y, z)));
            return list.ToArray();
        }

        /// <summary>
        /// Ways in to a deposit: the radial one first (away from the body's centre: the shortest bore, as before); in space
        /// also others, shortest first, ENTRY_ANGLE_MIN apart, at most ENTRY_LONGER x the radial bore + ENTRY_SLACK. All
        /// with ENTRY_FREE m of open line out (not into a closed cave or a hollow mined out before) and through tunnels
        /// bored before (OreScanner.FindSurface: the radial way began at an old bore's end, its approach inside the tunnel,
        /// where no hull fits). Empty: no way in.
        /// </summary>
        private static List<WayIn> WaysIn(IMyVoxelBase voxel, Vector3D deposit, bool space, int max)
        {
            var ways = new List<WayIn>();
            Vector3D outward = deposit - voxel.PositionComp.WorldAABB.Center;
            if (outward.LengthSquared() < 1) outward = Vector3D.Up;
            outward.Normalize();
            Vector3D surface;
            if (!OreScanner.FindSurface(voxel, deposit, outward, BORE_SEARCH_METERS, out surface, ENTRY_FREE)) return ways;
            ways.Add(new WayIn { Surface = surface, Outward = outward });
            if (!space || max <= 1) return ways;
            double longest = Math.Min(BORE_SEARCH_METERS, Vector3D.Distance(surface, deposit) * ENTRY_LONGER + ENTRY_SLACK);
            var lens = new List<double>();
            var found = new List<WayIn>();
            for (int i = 0; i < entryDirs.Length; i++)
            {
                Vector3D d = entryDirs[i];
                if (Vector3D.Dot(d, outward) > Math.Cos(MathHelperD.ToRadians(ENTRY_ANGLE_MIN))) continue;
                Vector3D s;
                if (!OreScanner.FindSurface(voxel, deposit, d, longest, out s, ENTRY_FREE)) continue;
                double len = Vector3D.Distance(s, deposit);
                int at = lens.Count;
                while (at > 0 && lens[at - 1] > len) at--;
                lens.Insert(at, len);
                found.Insert(at, new WayIn { Surface = s, Outward = d });
            }
            double cosMin = Math.Cos(MathHelperD.ToRadians(ENTRY_ANGLE_MIN));
            for (int i = 0; i < found.Count && ways.Count < max; i++)
            {
                bool apart = true;
                for (int k = 0; k < ways.Count && apart; k++) apart = Vector3D.Dot(found[i].Outward, ways[k].Outward) <= cosMin;
                if (apart) ways.Add(found[i]);
            }
            return ways;
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
                    if (jobs[j].Announced || jobs[j].Pending) Withdraw(jobs[j], Loc.T(MyStrings.Mining_OrderRemoved));   // an auction may already run for it
                    jobs.RemoveAt(j);
                }
        }

        // Orchestrators drop the job's queue entry / running auction
        private void Withdraw(MiningJob job, string why)
        {
            if (messaging == null) return;
            var msg = new Message<JobStatusReport>
            {
                Payload = new JobStatusReport { AnnouncerEntityId = entityId, JobId = job.JobId, Outcome = JobOutcome.Aborted, Reason = why,
                                                JobType = Orchestrator.JobType.MineOre },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.JOB_WITHDRAWN,
            };
            var error = messaging.BroadcastMessage(antennaReg, msg, false, MessageQueue.IAIBlockType.MiningSurveyor);
            if (Log.DebugEnabled) Log.Debug("MS {0}: job {1} ({2}) withdrawn ({3}): {4}", entityId, job.JobId, job.Ore, why, error);
        }

        /// <summary>
        /// A player's order whose kg are all delivered (nothing left to publish, no job of it out): taken off the Orders list
        /// (2026-11-08: it stayed there, and in the header's count, after it was filled). "Keep orders constant": never.
        /// </summary>
        private void RemoveFulfilledOrders()
        {
            if (settings.KeepOrdersConstant) return;
            bool removed = false;
            for (int i = orders.Count - 1; i >= 0; i--)
            {
                var o = orders[i];
                OrderProgress p;
                if (!progress.TryGetValue(o.Ore, out p) || p.RemainingKg >= 1 || HasJobFor(o.Ore, true)) continue;
                for (int s = settings.Orders.Count - 1; s >= 0; s--)
                {
                    OreOrder so;
                    if (OreOrder.TryParse(settings.Orders[s], out so) && so.Ore == o.Ore) settings.Orders.RemoveAt(s);
                }
                AddEvent(Loc.F(MyStrings.Mining_OrderFulfilled, ShortOre(o.Ore), p.DeliveredKg, o.Kg));
                if (Log.DebugEnabled) Log.Debug("MS {0}: order {1} ({2} kg) fulfilled: {3:F0} kg delivered, taken off the list", entityId, o.Ore, o.Kg, p.DeliveredKg);
                removed = true;
            }
            if (!removed) return;
            ordersDirty = true;
            SettingsEdited();
            ParseOrders();
        }

        // Back to its order: a job that won't be done (or was done short / over)
        private void Settle(MiningJob job, double deliveredKg, string why)
        {
            OrderProgress p;
            if (job.ForLc == 0 && !settings.KeepOrdersConstant && progress.TryGetValue(job.Ore, out p))
                p.RemainingKg = Math.Max(0, p.RemainingKg + job.Kg - deliveredKg);
            if (Log.DebugEnabled) Log.Debug("MS {0}: job {1} ({2}, {3:F0} kg) settled: {4:F0} kg delivered, {5}", entityId, job.JobId, job.Ore, job.Kg, deliveredKg, why);
        }
        #endregion

        #region Jobs
        private void PlanJobs(int now)
        {
            RemoveFulfilledOrders();
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
            PlanLcJobs(now, ref published);
        }

        /// <summary>
        /// A MineOre job on 'deposit'. Its tasks are sites (NavigateToolTo, Mine, BackOut each), the first one as before
        /// (bored from outside, away from the body's centre: the radial way in); the others for miners an orchestrator pools
        /// on the job, one site each, in this order: other deposits of the ore no job is on (nearest the first, at most
        /// SITE_DEPOSITS_MAX), then other ways in to them (space: WaysIn), at most SITES_MAX sites.
        /// </summary>
        private MiningJob CreateJob(Deposit deposit, double kg, int now, long deliverTo = 0, int deliverToNetwork = 0)
        {
            IMyEntity e;
            var voxel = MyAPIGateway.Entities.TryGetEntityById(deposit.VoxelId, out e) ? e as IMyVoxelBase : null;
            if (voxel == null)
            {
                deposits.Remove(deposit);
                if (Log.DebugEnabled) Log.Debug("MS {0}: deposit {1} at {2}: its voxel map is gone", entityId, deposit.Ore, deposit.Position);
                return null;
            }
            float interference;
            bool space = MyAPIGateway.Physics.CalculateNaturalGravityAt(deposit.Position, out interference).Length() < JobAnnouncement.SPACE_GRAVITY;
            var first = WaysIn(voxel, deposit.Position, space, SITE_ENTRIES_MAX);
            if (first.Count == 0)
            {
                if (Log.DebugEnabled) Log.Debug("MS {0}: deposit {1} at {2}: no way in within {3:F0} m", entityId, deposit.Ore, deposit.Position, BORE_SEARCH_METERS);
                return null;
            }
            int bad = first.RemoveAll(w => IsBadWay(w.Surface, now));
            if (first.Count == 0)
            {
                // Every way in to it unreachable lately: another deposit of the ore leads (none: no job for now)
                Deposit alt = null;
                List<WayIn> altWays = null;
                double best = double.MaxValue;
                for (int i = 0; i < deposits.Count; i++)
                {
                    var o = deposits[i];
                    if (o == deposit || o.Ore != deposit.Ore || DepositBusy(o)) continue;
                    double dd = Vector3D.DistanceSquared(o.Position, deposit.Position);
                    if (dd >= best) continue;
                    IMyEntity oe;
                    var ov = MyAPIGateway.Entities.TryGetEntityById(o.VoxelId, out oe) ? oe as IMyVoxelBase : null;
                    if (ov == null) continue;
                    bool oSpace = MyAPIGateway.Physics.CalculateNaturalGravityAt(o.Position, out interference).Length() < JobAnnouncement.SPACE_GRAVITY;
                    var w = WaysIn(ov, o.Position, oSpace, SITE_ENTRIES_MAX);
                    w.RemoveAll(x => IsBadWay(x.Surface, now));
                    if (w.Count == 0) continue;
                    best = dd;
                    alt = o;
                    altWays = w;
                }
                if (alt == null)
                {
                    if (Log.DebugEnabled) Log.Debug("MS {0}: deposit {1} at {2}: its {3} way(s) in unreachable lately, no other deposit of it: no job for now", entityId, deposit.Ore, deposit.Position, bad);
                    return null;
                }
                if (Log.DebugEnabled) Log.Debug("MS {0}: deposit {1} at {2}: its {3} way(s) in unreachable lately: the deposit at {4} leads", entityId, deposit.Ore, deposit.Position, bad, alt.Position);
                deposit = alt;
                first = altWays;
                voxel = MyAPIGateway.Entities.TryGetEntityById(deposit.VoxelId, out e) ? e as IMyVoxelBase : voxel;
            }
            else if (bad > 0 && Log.DebugEnabled) Log.Debug("MS {0}: deposit {1} at {2}: {3} way(s) in left out (unreachable lately)", entityId, deposit.Ore, deposit.Position, bad);
            // Other deposits of the ore for pooled miners: nearest this one first, none another job is on
            var chosen = new List<Deposit> { deposit };
            var chosenWays = new List<List<WayIn>> { first };
            var others = new List<Deposit>();
            for (int i = 0; i < deposits.Count; i++)
                if (deposits[i] != deposit && deposits[i].Ore == deposit.Ore && !DepositBusy(deposits[i])) others.Add(deposits[i]);
            others.Sort((a, b) => Vector3D.DistanceSquared(a.Position, deposit.Position).CompareTo(Vector3D.DistanceSquared(b.Position, deposit.Position)));
            for (int i = 0; i < others.Count && chosen.Count < SITE_DEPOSITS_MAX; i++)
            {
                IMyEntity oe;
                var ov = MyAPIGateway.Entities.TryGetEntityById(others[i].VoxelId, out oe) ? oe as IMyVoxelBase : null;
                if (ov == null) continue;
                bool oSpace = MyAPIGateway.Physics.CalculateNaturalGravityAt(others[i].Position, out interference).Length() < JobAnnouncement.SPACE_GRAVITY;
                var ways = WaysIn(ov, others[i].Position, oSpace, SITE_ENTRIES_MAX);
                ways.RemoveAll(x => IsBadWay(x.Surface, now));
                if (ways.Count == 0) continue;
                chosen.Add(others[i]);
                chosenWays.Add(ways);
            }
            var items = new DiscreteInventory();
            items.AddItem(deposit.Ore, (int)Math.Ceiling(kg));
            var tasks = new List<Orchestrator.Task>();
            for (int w = 0; w < SITE_ENTRIES_MAX && tasks.Count < SITES_MAX * 3; w++)
                for (int d = 0; d < chosen.Count && tasks.Count < SITES_MAX * 3; d++)
                {
                    if (w >= chosenWays[d].Count) continue;
                    var way = chosenWays[d][w];
                    float sg = MyAPIGateway.Physics.CalculateNaturalGravityAt(way.Surface, out interference).Length();
                    Vector3D ap = way.Surface + way.Outward * MINE_APPROACH_OUT;
                    var m = Orchestrator.Task.Mine();
                    m.Position = Vector3DData.FromVector3D(chosen[d].Position);   // the bore's end
                    m.ApproachFrom = Vector3DData.FromVector3D(way.Surface);
                    m.TargetEntityId = chosen[d].VoxelId;
                    m.Inventory = items;
                    tasks.Add(Orchestrator.Task.NavigateToolTo(0, way.Surface, ap, sg));
                    tasks.Add(m);
                    tasks.Add(Orchestrator.Task.BackOut(0, ap));
                }
            Vector3D surface = first[0].Surface;
            float g = MyAPIGateway.Physics.CalculateNaturalGravityAt(surface, out interference).Length();
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
                DeliverTo = deliverTo,
                DeliverToNetwork = deliverTo != 0 ? deliverToNetwork : 0,
            };
            AddEvent(Loc.F(MyStrings.Mining_JobKgAtMBore, jobId, kg, ShortOre(deposit.Ore),
                                   Vector3D.Distance(deposit.Position, block.GetPosition()), Vector3D.Distance(surface, deposit.Position)));
            if (Log.DebugEnabled) Log.Debug("MS {0}: job {1}: {2:F0} kg {3} at {4}, bore from {5} ({6:F0} m); {7} site(s): {8} deposit(s), ways in {9}", entityId, jobId, kg, deposit.Ore, deposit.Position, surface, Vector3D.Distance(surface, deposit.Position), tasks.Count / 3, chosen.Count, WaysText(chosenWays));
            return new MiningJob { JobId = jobId, Ore = deposit.Ore, Deposit = deposit.Position, Kg = kg, Message = AnnouncementMessage(ja), ForLc = deliverTo, ForLcNetwork = deliverTo != 0 ? deliverToNetwork : 0 };
        }

        #region Logistics computers' ore requests
        private const double LC_JOB_MIN_KG = 100;   // a shortfall less than this past the jobs already out for it: no job
        private readonly List<Logistics.LogisticsComputerBlock.OreRequest> lcShort = new List<Logistics.LogisticsComputerBlock.OreRequest>();

        // Each logistics computer network's request for an ore, as shown in the header ("LC: Ice 300/1000"): what its jobs
        // delivered since it began, and what it was still short of when last read (runtime; gone when not read for a while)
        // An LC's request in the header: Total fixed when first seen (raised only if the request grows past it); delivered:
        // what drones reported, or what the request shrank by, whichever is more
        private sealed class LcOrder { public MyStringHash Ore; public double Delivered; public int Short; public int Total; public int Seen; }
        private readonly Dictionary<string, LcOrder> lcOrders = new Dictionary<string, LcOrder>();
        private const int LC_ORDER_GONE_TICKS = 60 * 60;

        private static string LcOrderKey(long lc, int network, MyStringHash ore)
        {
            return lc.ToString() + ":" + network.ToString() + ":" + ore.String;
        }

        // A logistics computer with "Ore requests mine" on that this surveyor reaches through the network (same grid group,
        // antennas: the requirement), of an owner it works for
        private bool LcInReach(Logistics.LogisticsComputerBlock lc)
        {
            return lc != null && lc.OreRequestsMine && lc.Grid != null && messaging != null
                && AnchorDirectory.IsAllowed(block.OwnerId, lc.OwnerId)
                && messaging.Relays.Reaches(lc.Grid, block.CubeGrid, lc.OwnerId);
        }

        /// <summary>
        /// LC "Ore requests mine" (2026-11-07, LogisticsComputerBlock.OreRequests): every reachable computer's ore shortfall
        /// becomes MineOre jobs here, like an order (the nearest known deposit, Job size kg at a time, one job per ore waiting
        /// for a drone at a time, Max published jobs shared), unloaded at that computer when it bids room (DeliverTo). The
        /// computer never hauls those ores. Not counted in this surveyor's own orders.
        /// A request gone (filled, Min lowered, unticked, out of reach): its jobs no drone has taken yet are withdrawn.
        /// </summary>
        private void PlanLcJobs(int now, ref int published)
        {
            var config = MiningConfig;
            for (int i = 0; i < Logistics.LogisticsComputerBlock.All.Count; i++)
            {
                var lc = Logistics.LogisticsComputerBlock.All[i];
                if (!LcInReach(lc)) continue;
                lc.OreRequests(lcShort);
                for (int k = 0; k < lcShort.Count; k++)
                {
                    var ore = lcShort[k].Ore;
                    int kg = lcShort[k].Kg, net = lcShort[k].Network;
                    if (kg <= 0 || IsIgnoredOre(ore.String)) continue;   // stone: never mined (nor hauled: the box is absolute)
                    // Its own order (one per network and ore, whichever computer): shown in the header
                    LcOrder view;
                    string key = LcOrderKey(lc.EntityId, net, ore);
                    if (!lcOrders.TryGetValue(key, out view)) { view = new LcOrder { Ore = ore, Total = kg }; lcOrders[key] = view; }
                    view.Short = kg;
                    if (kg > view.Total) view.Total = kg;
                    view.Seen = now;
                    var deposit = NearestDeposit(ore);
                    if (deposit == null) continue;   // none known: nothing for it until one is
                    double open = 0;
                    bool waiting = false;
                    for (int j = 0; j < jobs.Count; j++)
                    {
                        if (jobs[j].Ore != ore) continue;
                        if (jobs[j].ForLc == lc.EntityId && jobs[j].ForLcNetwork == net) open += Math.Max(0, jobs[j].Kg - jobs[j].DeliveredKg);
                        if (jobs[j].Assigned == 0) waiting = true;
                    }
                    double left = kg - open;
                    if (waiting || left < LC_JOB_MIN_KG || published >= config.MaxPublishedJobs) continue;
                    var job = CreateJob(deposit, Math.Min(left, settings.JobSizeKg), now, lc.EntityId, net);
                    if (job == null) continue;
                    jobs.Add(job);
                    published++;
                    AddEvent(Loc.F(MyStrings.Mining_JobForLc, Nicknames.Of(lc.EntityId), job.Kg, ShortOre(ore), job.JobId));
                    if (Log.DebugEnabled) Log.Debug("MS {0}: LC {1} short of {2:F0} kg {3} ({4:F0} kg out for it already): job {5}, {6:F0} kg", entityId, lc.EntityId, kg, ore, open, job.JobId, job.Kg);
                }
                lcShort.Clear();
            }
            for (int j = jobs.Count - 1; j >= 0; j--)
            {
                var job = jobs[j];
                if (job.ForLc == 0 || job.Assigned != 0 || LcStillShort(job.ForLc, job.ForLcNetwork, job.Ore)) continue;
                if (job.Announced || job.Pending) Withdraw(job, Loc.T(MyStrings.Mining_LcRequestGone));
                if (Log.DebugEnabled) Log.Debug("MS {0}: job {1} ({2}) dropped: LC {3}'s request filled or gone", entityId, job.JobId, job.Ore, job.ForLc);
                jobs.RemoveAt(j);
            }
            // Requests not read for a while (filled, gone): out of the header
            var stale = new List<string>();
            foreach (var kv in lcOrders) if (now - kv.Value.Seen > LC_ORDER_GONE_TICKS) stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++) lcOrders.Remove(stale[i]);
        }

        private bool LcStillShort(long lcId, int net, MyStringHash ore)
        {
            for (int i = 0; i < Logistics.LogisticsComputerBlock.All.Count; i++)
            {
                var lc = Logistics.LogisticsComputerBlock.All[i];
                if (lc.EntityId != lcId) continue;
                if (!LcInReach(lc)) return false;
                lc.OreRequests(lcShort);
                bool shortOf = false;
                for (int k = 0; k < lcShort.Count && !shortOf; k++)
                    shortOf = lcShort[k].Ore == ore && lcShort[k].Kg > 0 && (net == 0 || lcShort[k].Network == net);
                lcShort.Clear();
                return shortOf;
            }
            return false;
        }
        #endregion

        private static string WaysText(List<List<WayIn>> ways)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ways.Count; i++) sb.Append(i > 0 ? "/" : "").Append(ways[i].Count);
            return sb.ToString();
        }

        private Message<JobAnnouncement> AnnouncementMessage(JobAnnouncement ja)
        {
            return new Message<JobAnnouncement>
            {
                Payload = ja,
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
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
                if (job.Pending)
                {
                    if (job.Message.Delivered) { job.Pending = false; job.Announced = true; job.LastResult = Loc.T(MyStrings.Mining_Announced); }
                    else if (now - job.LastAnnounced >= messaging.LifetimeTicks) { job.Pending = false; job.Announced = false; job.LastResult = Loc.T(MyStrings.Mining_NoOrchestratorInRange); }
                    else continue;   // still in the network
                }
                if (now - job.LastAnnounced < (job.Announced ? RESPONSE_TIMEOUT_TICKS : RETRY_TICKS)) continue;
                job.LastAnnounced = now;
                job.Message.MessageId = IdGenerator.GenerateId(ref messageCounter, entityId);
                job.Message.CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
                var error = messaging.BroadcastMessage(antennaReg, job.Message, false, MessageQueue.IAIBlockType.MiningSurveyor);
                job.Announced = false;
                job.Pending = error == ErrorCode.None;
                if (error != ErrorCode.None) job.LastResult = error.ToString();
                if (Log.DebugEnabled) Log.Debug("MS {0}: job {1} ({2}, {3:F0} kg): {4}", entityId, job.JobId, job.Ore, job.Kg, error);
            }
        }

        private MiningJob FindJob(uint jobId)
        {
            for (int j = 0; j < jobs.Count; j++) if (jobs[j].JobId == jobId) return jobs[j];
            return null;
        }

        private void ReadAwards(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.MiningSurveyor, antennaReg, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT, awardInbox, 20, true, PayloadType.AuctionWinnerAnnouncement);
            for (int i = 0; i < awardInbox.Count; i++)
            {
                var award = awardInbox[i].Payload;
                if (award == null || award.AnnouncerEntityId != entityId || !AnchorDirectory.IsAllowed(block.OwnerId, awardInbox[i].SenderOwnerId)) continue;
                var job = FindJob(award.JobId);
                if (job == null || job.Assigned != 0) continue;
                job.Assigned = now;
                job.HeldUntil = 0;
                job.DronesOut = award.TaskAssignments != null ? award.TaskAssignments.Count : 0;
                // Where its miners bore: those deposits are taken (the job's other sites free for the next job)
                job.Used = new List<Vector3D>();
                job.WaysOf = new Dictionary<long, List<Vector3D>>();
                if (award.TaskAssignments != null)
                    foreach (var kv in award.TaskAssignments)
                    {
                        var list = kv.Value;
                        if (list == null) continue;
                        var ways = new List<Vector3D>();
                        for (int k = 0; k < list.Count; k++)
                            if (list[k].Type == Orchestrator.TaskType.Mine)
                            {
                                job.Used.Add(list[k].Position.ToVector3D());
                                ways.Add(list[k].ApproachFrom.ToVector3D());   // the bore's start (WayIn.Surface)
                            }
                        job.WaysOf[kv.Key] = ways;
                    }
                job.LastResult = Loc.T(MyStrings.Mining_Assigned);
                long drone = 0;
                if (award.TaskAssignments != null) foreach (var k in award.TaskAssignments.Keys) { drone = k; break; }
                AddEvent(Loc.F(MyStrings.Mining_JobAwardedDrone, ShortOre(job.Ore), Nicknames.Of(drone)));
            }
            awardInbox.Clear();
        }

        private void ReadFailures(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.MiningSurveyor, antennaReg, Channel.JOB_ANNOUNCEMENT_FAILED, failureInbox, 20, true, PayloadType.JobAnnouncementFailed);
            for (int i = 0; i < failureInbox.Count; i++)
            {
                var f = failureInbox[i].Payload;
                if (f == null || f.AnnouncerEntityId != entityId || !AnchorDirectory.IsAllowed(block.OwnerId, failureInbox[i].SenderOwnerId)) continue;
                var job = FindJob(f.JobId);
                if (job == null || job.Assigned != 0 || (job.HeldUntil != 0 && job.HeldUntil > now)) continue;
                job.HeldUntil = now + Math.Max(10, f.HoldSeconds) * 60;
                job.LastResult = Loc.F(MyStrings.Mining_OnHold, f.Reason);
                failedCount++;
                AddEvent(Loc.F(MyStrings.Mining_JobOnHoldS, job.JobId, f.HoldSeconds, f.Reason));
            }
            failureInbox.Clear();
        }

        private void ReadReports()
        {
            for (int c = 0; c < 2; c++)
            {
                messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.MiningSurveyor, antennaReg, c == 0 ? Channel.TASKS_COMPLETED : Channel.TASKS_ABORTED, reportInbox, 20, true, PayloadType.JobStatusReport);
                for (int i = 0; i < reportInbox.Count; i++)
                {
                    var r = reportInbox[i].Payload;
                    if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId
                        || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                    droneWatch.Done(r.DroneEntityId);
                    var job = FindJob(r.JobId);
                    if (job == null) { CountUnheldDelivery(r); continue; }
                    if (r.Preempted && r.Outcome == JobOutcome.Aborted)
                    {
                        // The drone was taken by a player's order: the orchestrator auctions the job again; kept assigned
                        // (not announced twice), its next drone's report settles it
                        job.DronesOut = Math.Max(0, job.DronesOut - 1);
                        job.Assigned = MyAPIGateway.Session.GameplayFrameCounter;
                        job.DeliveredKg += Math.Max(0, r.OreKg);
                        AddEvent(Loc.F(MyStrings.Mining_MiningDroneOre, Nicknames.Of(r.DroneEntityId), r.Outcome, ShortOre(job.Ore)) + (string.IsNullOrEmpty(r.Reason) ? "" : "\n  " + r.Reason));
                        if (Log.DebugEnabled) Log.Debug("MS {0}: job {1}: drone {2} taken by a player's order, the orchestrator auctions it again", entityId, job.JobId, r.DroneEntityId);
                        continue;
                    }
                    if (job.Assigned == 0) job.DronesOut = Math.Max(job.DronesOut, 1);
                    job.DronesOut--;
                    MarkBadWays(job, r);
                    job.DeliveredKg += Math.Max(0, r.OreKg);
                    if (r.OreKg > 0) deliveredCount += (int)r.OreKg;
                    OrderProgress op;
                    if (r.OreKg > 0 && job.ForLc == 0 && progress.TryGetValue(job.Ore, out op)) op.DeliveredKg += r.OreKg;   // the header's requests
                    LcOrder lco;
                    if (r.OreKg > 0 && job.ForLc != 0 && lcOrders.TryGetValue(LcOrderKey(job.ForLc, job.ForLcNetwork, job.Ore), out lco)) lco.Delivered += r.OreKg;
                    AddEvent(r.OreKg > 0 ? Loc.F(MyStrings.Mining_DeliveredDroneOreKg, Nicknames.Of(r.DroneEntityId), ShortOre(job.Ore), r.OreKg)
                                         : Loc.F(MyStrings.Mining_MiningDroneOre, Nicknames.Of(r.DroneEntityId), r.Outcome, ShortOre(job.Ore))
                                           + (string.IsNullOrEmpty(r.Reason) ? "" : "\n  " + r.Reason));
                    if (job.DronesOut > 0) continue;   // more miners on it: settled with the last report
                    Settle(job, job.DeliveredKg, "drones done, last " + r.Outcome + (string.IsNullOrEmpty(r.Reason) ? "" : " (" + r.Reason + ")"));
                    jobs.Remove(job);
                }
                reportInbox.Clear();
            }
        }

        // A miner aborted unreachable with nothing delivered: its ways in left out of new jobs for a while. (The reason
        // is the drone's last failure text, the TaskFailure name untranslated: "NavigateToolTo: Unreachable"; a failed
        // way home after it overwrote it once - "ReturnHome: Unreachable" - and counts too.)
        private void MarkBadWays(MiningJob job, JobStatusReport r)
        {
            if (r.Outcome != JobOutcome.Aborted || r.OreKg > 0 || string.IsNullOrEmpty(r.Reason)
                || r.Reason.IndexOf("Unreachable", StringComparison.OrdinalIgnoreCase) < 0) return;
            List<Vector3D> ways;
            if (job.WaysOf == null || !job.WaysOf.TryGetValue(r.DroneEntityId, out ways) || ways.Count == 0) return;
            int until = MyAPIGateway.Session.GameplayFrameCounter + BAD_WAY_TICKS;
            for (int i = 0; i < ways.Count; i++) badWays.Add(new BadWay { Surface = ways[i], Until = until });
            if (Log.DebugEnabled) Log.Debug("MS {0}: job {1}: drone {2} couldn't reach its {3} way(s) in: left out of new jobs for {4} min", entityId, job.JobId, r.DroneEntityId, ways.Count, BAD_WAY_TICKS / 3600);
        }

        // A report of a job not held here (settled already, or the surveyor's saved state older than its drones' after a
        // load: 47,870 kg of Platinum went uncounted): its ore still counts toward the order
        private void CountUnheldDelivery(JobStatusReport r)
        {
            if (r.OreKg <= 0 || string.IsNullOrEmpty(r.Ore)) return;
            var ore = MyStringHash.GetOrCompute(r.Ore);
            deliveredCount += (int)r.OreKg;
            OrderProgress op;
            if (progress.TryGetValue(ore, out op))
            {
                op.DeliveredKg += r.OreKg;
                if (!settings.KeepOrdersConstant) op.RemainingKg = Math.Max(0, op.RemainingKg - r.OreKg);
            }
            AddEvent(Loc.F(MyStrings.Mining_DeliveredDroneOreKg, Nicknames.Of(r.DroneEntityId), ShortOre(ore), r.OreKg));
            if (Log.DebugEnabled) Log.Debug("MS {0}: job {1} not held here: {2:F0} kg of {3} counted", entityId, r.JobId, r.OreKg, r.Ore);
        }

        // Drones still mining for an awarded job: its expiry starts again (a dead / destroyed / lost drone stops sending)
        private readonly Util.DroneWatch droneWatch = new Util.DroneWatch();

        private void ReadKeepalives(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.MiningSurveyor, antennaReg, Channel.JOB_KEEPALIVE, reportInbox, 50, true, PayloadType.JobStatusReport);
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
            // By distance bin ("Ore/Iron:1200:3"), from the bin each deposit got when learnt
            var bins = new SortedDictionary<string, SortedDictionary<int, int>>(StringComparer.Ordinal);
            for (int i = 0; i < deposits.Count; i++)
            {
                var d = deposits[i];
                if (IsIgnoredOre(d.Ore.String)) continue;
                if (d.DistanceM < 0) d.DistanceM = DistanceBin(d.Position);   // (an old save's)
                SortedDictionary<int, int> of;
                if (!bins.TryGetValue(d.Ore.String, out of)) bins[d.Ore.String] = of = new SortedDictionary<int, int>();
                int n;
                of.TryGetValue(d.DistanceM, out n);
                of[d.DistanceM] = n + 1;
            }
            var binList = new List<string>();
            foreach (var kv in bins) foreach (var b in kv.Value) binList.Add(kv.Key + ":" + b.Key + ":" + b.Value);
            if (SameLines(list, settings.KnownOres) && SameLines(binList, settings.KnownOreBins)) return;
            settings.KnownOres = list;
            settings.KnownOreBins = binList;
            SettingsEdited();   // saved and sent to the players
        }

        private static bool SameLines(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // GPS markers are added silently: created hidden, then shown (the game plays its GPS sound only for a marker that is on
        // the HUD when it is added; showing it afterwards makes no sound). Showing it also drops the game's expiry of temporary
        // markers, so this block removes its own: when they no longer match, when the switch goes off, and any left over from before a reload (tagged with this block's id in their description).
        // The markers are diffed against the deposits each refresh: only new ones are added, gone ones removed.
        private void UpdateGps(int now)
        {
            if (!gpsCleaned && block.OwnerId != 0)
            {
                gpsCleaned = true;
                gpsIdentity = block.OwnerId;
                RemoveStaleGps();
            }
            if (!settings.ShowGps || block.OwnerId == 0)
            {
                if (gpsHashes.Count > 0) RemoveGps();
                return;
            }
            if (!gpsDirty && now - lastGps < GPS_REFRESH_TICKS && gpsIdentity == block.OwnerId) return;
            lastGps = now;
            gpsDirty = false;
            if (gpsIdentity != block.OwnerId)
            {
                RemoveGps();
                gpsIdentity = block.OwnerId;   // the owner only: deposits are never shown to anyone else
            }
            var wanted = WantedGps();
            for (int i = gpsHashes.Count - 1; i >= 0; i--)
            {
                int h = gpsHashes[i];
                if (wanted.ContainsKey(h)) continue;
                MyAPIGateway.Session.GPS.RemoveGps(gpsIdentity, h);
                gpsHashes.RemoveAt(i);
            }
            int added = 0;
            foreach (var kv in wanted)
            {
                if (gpsHashes.Contains(kv.Key)) continue;
                AddGpsSilent(kv.Value);
                added++;
                gpsHashes.Add(kv.Key);
            }
            if (added > 0 && Log.DebugEnabled) Log.Debug("MS {0}: GPS: {1} marker(s) added, {2} shown", entityId, added, gpsHashes.Count);
        }

        // The switch turns itself off GPS_ON_TICKS after it was turned on (server): a changed setting reaches the players
        private void CheckGpsTimer(int now)
        {
            if (!settings.ShowGps) { gpsOffAt = 0; return; }
            if (gpsOffAt == 0) { gpsOffAt = now + GPS_ON_TICKS; return; }
            if (now < gpsOffAt) return;
            gpsOffAt = 0;
            settings.ShowGps = false;
            SettingsEdited();
            AddEvent(Loc.T(MyStrings.Mining_DepositGPSMarkersSwitchedOff));
            if (Log.DebugEnabled) Log.Debug("MS {0}: show deposits as GPS switched off (time)", entityId);
        }

        // Markers for the nearest deposits (up to MaxGpsMarkers), by the hash the game gives them (name + position)
        private Dictionary<int, IMyGps> WantedGps()
        {
            Vector3D here = block.GetPosition();
            var shown = new List<Deposit>(deposits);
            shown.Sort((a, b) => Vector3D.DistanceSquared(a.Position, here).CompareTo(Vector3D.DistanceSquared(b.Position, here)));
            int max = Math.Min(shown.Count, MiningConfig.MaxGpsMarkers);
            var wanted = new Dictionary<int, IMyGps>();
            string description = Loc.F(MyStrings.Mining_OreDepositKnownToMining, block.CustomName) + GpsTag;
            for (int i = 0; i < max; i++)
            {
                var d = shown[i];
                var gps = MyAPIGateway.Session.GPS.Create(ShortOre(d.Ore) + " (" + block.CustomName + ")", description, d.Position, false, false);
                gps.GPSColor = Color.Gold;
                wanted[gps.Hash] = gps;
            }
            return wanted;
        }

        private void AddGpsSilent(IMyGps gps)
        {
            MyAPIGateway.Session.GPS.AddGps(gpsIdentity, gps);                   // hidden: no sound
            MyAPIGateway.Session.GPS.SetShowOnHud(gpsIdentity, gps.Hash, true);   // shown: no sound either
        }

        // This block's markers in the owner's GPS list (description ends with its tag) that it no longer tracks: left from
        // before a reload (they are saved now that they don't expire)
        private void RemoveStaleGps()
        {
            var list = MyAPIGateway.Session.GPS.GetGpsList(gpsIdentity);
            if (list == null) return;
            string tag = GpsTag;
            int removed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || g.Description == null || !g.Description.EndsWith(tag, StringComparison.Ordinal)) continue;
                if (gpsHashes.Contains(g.Hash)) continue;
                MyAPIGateway.Session.GPS.RemoveGps(gpsIdentity, g.Hash);
                removed++;
            }
            if (removed > 0 && Log.DebugEnabled) Log.Debug("MS {0}: GPS: {1} marker(s) left from before removed", entityId, removed);
        }

        private string GpsTag { get { return " [MS " + entityId + "]"; } }

        private void RemoveGps()
        {
            for (int i = 0; i < gpsHashes.Count; i++) MyAPIGateway.Session.GPS.RemoveGps(gpsIdentity, gpsHashes[i]);
            gpsHashes.Clear();
        }
        #endregion

        #region Save / load
        // The state into the block's storage when it changed (jobs, orders, deliveries, deposits), not only at world save:
        // a load brought back the surveyor without its jobs and order progress while its drones held theirs (newer), so their
        // deliveries weren't counted and the job ids started over
        private void SaveStateOnChange(int now)
        {
            if (now - lastStateWrite < STATE_WRITE_TICKS) return;
            long sig = StateSignature();
            if (sig == lastStateSignature) return;
            lastStateSignature = sig;
            lastStateWrite = now;
            WriteStorage();
        }

        private long StateSignature()
        {
            unchecked
            {
                long h = jobCounter;
                h = h * 31 + deliveredCount;
                h = h * 31 + failedCount;
                h = h * 31 + deposits.Count;
                h = h * 31 + depleted.Count;
                foreach (var kv in progress)
                {
                    h = h * 31 + kv.Key.GetHashCode();
                    h = h * 31 + kv.Value.OrderKg;
                    h = h * 31 + (long)kv.Value.RemainingKg;
                    h = h * 31 + (long)kv.Value.DeliveredKg;
                }
                for (int i = 0; i < jobs.Count; i++)
                {
                    var j = jobs[i];
                    h = h * 31 + j.JobId;
                    h = h * 31 + (j.Assigned != 0 ? 1 : 0) + (j.Announced ? 2 : 0) + (j.HeldUntil != 0 ? 4 : 0);
                    h = h * 31 + j.DronesOut;
                    h = h * 31 + (long)j.DeliveredKg;
                }
                return h;
            }
        }

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
                    DronesOut = job.DronesOut, Announced = job.Announced, ForLc = job.ForLc, ForLcNetwork = job.ForLcNetwork,
                });
            }
            if (Log.DebugEnabled) Log.Debug("MS {0}: state saved: {1} deposit(s), {2} depleted, {3} order(s), {4} job(s)", entityId, s.Deposits.Count, s.Depleted.Count, s.Orders.Count, s.Jobs.Count);
            return s;
        }

        private static DepositState SaveDeposit(Deposit d, DateTime utc)
        {
            return new DepositState { Ore = d.Ore.String, Position = Vector3DData.FromVector3D(d.Position), VoxelId = d.VoxelId, Voxels = d.Voxels,
                                      AgeSeconds = Math.Max(0, (utc - d.Seen).TotalSeconds), DistanceM = d.DistanceM };
        }

        private static Deposit LoadDeposit(DepositState d, DateTime utc)
        {
            return new Deposit { Ore = MyStringHash.GetOrCompute(d.Ore), Position = d.Position.ToVector3D(), VoxelId = d.VoxelId, Voxels = d.Voxels,
                                 Seen = utc.AddSeconds(-Math.Max(0, d.AgeSeconds)), DistanceM = d.DistanceM > 0 ? d.DistanceM : -1 };   // (0 isn't saved: binned again)
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
                        HeldUntil = js.HoldTicksLeft > 0 ? now + js.HoldTicksLeft : 0, DronesOut = js.DronesOut, ForLc = js.ForLc, ForLcNetwork = js.ForLcNetwork,
                        Announced = false,   // once more soon (orchestrators de-duplicate)
                    });
                }
            ordersDirty = true;   // progress of orders edited while saved is restarted
            gpsDirty = true;
            if (Log.DebugEnabled) Log.Debug("MS {0}: state restored: {1} deposit(s), {2} depleted, {3} order(s), {4} job(s)", entityId, deposits.Count, depleted.Count, progress.Count, jobs.Count);
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
            if (IsServer) info.AppendLoc(MyStrings.Mining_MiningColonArg0, status).Append('\n');
            info.AppendLoc(MyStrings.Mining_KnownDepositsColon).Append(' ');
            if (settings.KnownOres.Count == 0) info.AppendLoc(MyStrings.Keen_None);
            for (int i = 0; i < settings.KnownOres.Count; i++) info.Append(i > 0 ? ", " : "").Append(settings.KnownOres[i].StartsWith("Ore/", StringComparison.Ordinal) ? settings.KnownOres[i].Substring(4) : settings.KnownOres[i]);
            info.Append('\n');
            if (IsServer)
            {
                for (int i = 0; i < orders.Count; i++)
                {
                    OrderProgress p;
                    progress.TryGetValue(orders[i].Ore, out p);
                    info.AppendLoc(MyStrings.Mining_DashArg0ColonArg1Newline, orders[i], settings.KeepOrdersConstant ? Loc.T(MyStrings.Mining_ConstantLower) : p != null ? Loc.F(MyStrings.Mining_KgLeft, p.RemainingKg) : "-");
                }
                info.AppendLoc(MyStrings.Mining_DeliveredKgFailedAuctions, deliveredCount, failedCount).Append('\n');
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
        public List<string> Terminal_KnownOreBins { get { return settings.KnownOreBins; } }
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
            Terminal_SelectedBin = -1;
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
            // The player's orders ("P:", pooled per ore), then every logistics computer network's request on its own ("LC:")
            lcdRequests.Clear();
            for (int i = 0; i < orders.Count; i++)
            {
                OrderProgress p;
                progress.TryGetValue(orders[i].Ore, out p);
                if (!settings.KeepOrdersConstant && p != null && p.RemainingKg < 1 && !HasJobFor(orders[i].Ore, true)) continue;   // done: not active
                double delivered = p != null ? p.DeliveredKg : 0;
                lcdRequests.Add(Loc.F(MyStrings.Mining_RequestPlayer, ShortOre(orders[i].Ore), delivered.ToString("F0"),
                                      settings.KeepOrdersConstant ? Loc.T(MyStrings.Mining_ConstantLower) : orders[i].Kg.ToString()));
            }
            foreach (var o in lcOrders.Values)
            {
                double got = Math.Min(o.Total, Math.Max(o.Delivered, o.Total - o.Short));
                lcdRequests.Add(Loc.F(MyStrings.Mining_RequestLc, ShortOre(o.Ore), got.ToString("F0"), o.Total.ToString("F0")));
            }
            int first;
            int pages = BlockLcd.Paginate(settings.Lcd, Math.Max(lcdDeposits.Count, lcdRequests.Count), HEADER_ROWS, out first);
            header.Clear();
            header.Add(Loc.F(MyStrings.Mining_MC, Nicknames.Of(Entity)));
            string title = Loc.T(MyStrings.Mining_ActiveRequestsKg);
            if (pages > 1) title += " " + Loc.F(MyStrings.Mining_Page, first / HEADER_ROWS + 1, pages);
            header.Add(BlockLcd.Columns(Loc.T(MyStrings.Mining_KnownDeposits), title, HEADER_COLUMN));
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
        #endregion
    }
}
