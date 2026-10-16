using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

using Automata.Drone;
using Automata.Inventory;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;
using VRage.Utils;

namespace Automata.Construction
{
    /// <summary>
    /// Construction Computer block (a programmable block subtype): scans its observation area (a box of up to 50 x 50 x
    /// 50 blocks of 2.5 m, default 25, along the block's axes, its centre offset up to 250 m each way) for blocks to
    /// weld (unfinished, damaged, projected - projections floating in the area included) and to grind (painted the
    /// grind color), on every grid in the area that its owner / faction owns (or nobody does), working drones excepted.
    /// Targets are grouped in build order (<see cref="BuildPlanner"/>) into jobs of a few blocks and announced to
    /// orchestrators (CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT); a weld job lists the components it needs (a logistics
    /// computer supplies them), a grind job what it returns. At most MaxPublishedJobs (server setting) jobs are
    /// published (announced, not awarded yet) at a time. Jobs are cached: each is announced once (again only if the
    /// announcement reached nobody, or nothing came back for a long while), its blocks go into no other job, an award
    /// stops it, a JOB_ANNOUNCEMENT_FAILED puts it on hold; blocks the scans no longer find are done. The jobs are kept
    /// through a world save. Server only; the area can be drawn (magenta) on the client.
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, "AutomataLargeConstructionComputer")]
    public class ConstructionComputerBlock : SettingsBlockLogic, IDebugDrawable, INicknamed
    {
        #region UI strings
        private static readonly MyStringId StrStarting = MyStringId.GetOrCompute("Starting");
        private static readonly MyStringId StrError = MyStringId.GetOrCompute("Error: {0}");
        private static readonly MyStringId StrRetryEvent = MyStringId.GetOrCompute("Retry: {0} job(s) announced again");
        private static readonly MyStringId StrWaitingForMod = MyStringId.GetOrCompute("Waiting for the mod");
        private static readonly MyStringId StrLoading = MyStringId.GetOrCompute("Loading");
        private static readonly MyStringId StrOffTerminal = MyStringId.GetOrCompute("Off (terminal)");
        private static readonly MyStringId StrNotWorking = MyStringId.GetOrCompute("Not working (power / damage)");
        private static readonly MyStringId StrNoAntenna = MyStringId.GetOrCompute("No antenna (functional, on, broadcasting)");
        private static readonly MyStringId StrBlocksFoundNoJob = MyStringId.GetOrCompute("Blocks found, no job yet");
        private static readonly MyStringId StrNothingToDo = MyStringId.GetOrCompute("Nothing to do in the area");
        private static readonly MyStringId StrJobsSummary = MyStringId.GetOrCompute("{0} job(s) published, {1} on hold, {2} assigned");
        private static readonly MyStringId StrJobDone = MyStringId.GetOrCompute("Done: {1}:\n{0}");
        private static readonly MyStringId StrHeaderTitle = MyStringId.GetOrCompute("CC {0}");
        private static readonly MyStringId StrHeaderJobs = MyStringId.GetOrCompute("Active jobs: {0}, Total tasks: {1}");
        private static readonly MyStringId StrHeaderPriority = MyStringId.GetOrCompute("Weld priority: {0}");
        private static readonly MyStringId StrHeaderQueuedWeld = MyStringId.GetOrCompute("Queued Weld blocks:");
        private static readonly MyStringId StrHeaderQueuedGrind = MyStringId.GetOrCompute("Queued Grind: {0}");
        private static readonly MyStringId StrHeaderCategory = MyStringId.GetOrCompute(" {0}. {1}{2}");
        private static readonly MyStringId StrCatRepair = MyStringId.GetOrCompute("Repair:");
        private static readonly MyStringId StrCatProjected = MyStringId.GetOrCompute("Projected:");
        private static readonly MyStringId StrCatUnbuilt = MyStringId.GetOrCompute("Unbuilt:");
        private static readonly MyStringId StrLogJob = MyStringId.GetOrCompute("{0}:\n{1}");
        private static readonly MyStringId StrLogJobDrone = MyStringId.GetOrCompute("{0} Drone: {2}\n{1}");
        private static readonly MyStringId StrNotOwnerFaction = MyStringId.GetOrCompute("not owner/faction");
        private static readonly MyStringId StrWorkingDrone = MyStringId.GetOrCompute("a working drone");
        private static readonly MyStringId StrJobCreated = MyStringId.GetOrCompute("Job {0}: {1} {2} block(s)");
        private static readonly MyStringId StrAnnounced = MyStringId.GetOrCompute("announced");
        private static readonly MyStringId StrNoOrchestratorInRange = MyStringId.GetOrCompute("no orchestrator in range");
        private static readonly MyStringId StrJobOnHold = MyStringId.GetOrCompute("On hold {1} s: {0}\n  {2}");
        private static readonly MyStringId StrJobAssigned = MyStringId.GetOrCompute("Job {0}: assigned ({1} drone(s))");
        private static readonly MyStringId StrJobDroneOutcome = MyStringId.GetOrCompute("{0}: Drone {1} {2}");
        private static readonly MyStringId StrJobDroneOutcomeReason = MyStringId.GetOrCompute("{0}: Drone {1} {2}\n  {3}");
        private static readonly MyStringId StrJobExpired = MyStringId.GetOrCompute("Expired: {0}");
        private static readonly MyStringId StrInfoStatus = MyStringId.GetOrCompute("Construction: {0}");
        private static readonly MyStringId StrInfoArea = MyStringId.GetOrCompute("Area: {0} x {1} x {2} blocks ({3:F0} x {4:F0} x {5:F0} m), scan every {6} s");
        private static readonly MyStringId StrInfoLastScan = MyStringId.GetOrCompute("Last scan: {0} block(s) ({1} weld, {2} grind)");
        private static readonly MyStringId StrInfoJobs = MyStringId.GetOrCompute("Jobs: {0} cached, done {1}, failed {2}, expired {3}");
        private static readonly MyStringId StrInfoJobLine = MyStringId.GetOrCompute(" - {1}: {2} block(s), {3}");
        private static readonly MyStringId StrAssigned = MyStringId.GetOrCompute("assigned");
        private static readonly MyStringId StrOnHoldSeconds = MyStringId.GetOrCompute("on hold {0}s");
        private static readonly MyStringId StrPending = MyStringId.GetOrCompute("pending");
        private static readonly MyStringId StrWarnNoAntenna = MyStringId.GetOrCompute("Warning: no broadcasting antenna on the grid");
        #endregion

        #region Timing
        private const int RETRY_TICKS = 20 * 60;                // an announcement that reached nobody: tried again
        private const int RESPONSE_TIMEOUT_TICKS = 60 * 60;     // announced, no award / failure since: announced again (was 5 min)
        private const int ASSIGNED_TTL_TICKS = 15 * 60 * 60;    // awarded, then no sign of the drone (award / keepalive every 60 s) this long: expired
        #endregion
        private const int MAX_TARGETS_PER_SCAN = 500;
        public const double AREA_UNIT = 2.5;                    // m per area block (large grid cube)
        public const int AREA_SIZE_MIN = 1, AREA_SIZE_MAX = 50;
        public const int AREA_OFFSET_MAX = 100;                 // blocks (250 m)

        private ConstructionComputerBlockSettings settings = new ConstructionComputerBlockSettings();
        private ConstructionComputer computer;
        private long entityId;
        private MessageQueue messaging;
        private IMyRadioAntenna antenna;
        private AntennaReg antennaReg;
        private bool ready;
        private int lastScan = int.MinValue / 2, lastAntennaSearch = int.MinValue / 2;
        private int lastFullScan = int.MinValue / 2;
        private bool forceFullScan;
        private readonly ScanCache scanCache = new ScanCache();
        private string status = Loc.T(StrStarting);
        private readonly StringBuilder info = new StringBuilder();

        private sealed class OpenJob
        {
            public uint JobId;
            public Orchestrator.JobType Type;
            public WorkCategory Category;   // its blocks' (one per job): announcements go out in weld priority order
            public string Summary;          // its blocks by name, for the log
            public bool Logged;             // its first announcement is in the log
            public readonly HashSet<KeyValuePair<long, Vector3I>> Blocks = new HashSet<KeyValuePair<long, Vector3I>>();
            // Blocks of a predicted projection line (not buildable when the job was made): no scan finds them until the
            // block before is built, so they stay in the job while their cell is still empty
            public readonly HashSet<KeyValuePair<long, Vector3I>> Predicted = new HashSet<KeyValuePair<long, Vector3I>>();
            public Message<JobAnnouncement> Message;
            public int Opened, LastAnnounced = int.MinValue / 2;
            public int Assigned;      // frame of the award, 0 = not yet
            public int Failures;      // drones of the award that reported Aborted / Declined
            public string LastFailure;
            public int HeldUntil;     // its auction failed: not announced before this frame, 0 = not held
            public int DronesOut;     // awarded: drones that have not reported their share yet
            public bool Announced;    // the last announcement reached someone
            public bool Pending;      // an announcement is in the network: told whether it was taken once its life is up
            public string LastResult;
            public bool PerBlock;     // a drone reported its unreachable blocks: those are held back, not the whole job
            public readonly HashSet<KeyValuePair<long, Vector3I>> Unreachable = new HashSet<KeyValuePair<long, Vector3I>>();
        }
        private readonly List<Message<JobAnnouncementFailed>> failureInbox = new List<Message<JobAnnouncementFailed>>();
        private ConstructionComputerState pendingState;
        private int failedCount;
        // Area drawing (this player only)
        private bool drawArea;
        private int drawStart;
        private static readonly VRage.Utils.MyStringId DrawMaterial = VRage.Utils.MyStringId.GetOrCompute("Square");
        private readonly List<OpenJob> jobs = new List<OpenJob>();
        private readonly HashSet<KeyValuePair<long, Vector3I>> inJobs = new HashSet<KeyValuePair<long, Vector3I>>();
        // Blocks of a job a drone gave up on: not put in a new job before this frame (an unreachable block used to come
        // straight back as a new job id, past the orchestrator's retry memory, and kept the only grinder busy)
        private readonly Dictionary<KeyValuePair<long, Vector3I>, int> blockCooldown = new Dictionary<KeyValuePair<long, Vector3I>, int>();
        private readonly Dictionary<KeyValuePair<long, Vector3I>, int> unreachableStrikes = new Dictionary<KeyValuePair<long, Vector3I>, int>();
        #region Timing
        private static int AbortCooldownTicks()
        {
            var config = AutomataSession.GetConfig();
            return config != null ? config.OrchestratorServerSettings.BidBlacklistCooldownTicks : 3600;
        }
        private const int UNREACHABLE_COOLDOWN_TICKS = 5 * 60 * 60;   // after "Unreachable"
        private const int UNREACHABLE_COOLDOWN_MAX_TICKS = 30 * 60 * 60;   // a block reported unreachable again and again: doubled each time up to this
        // after any other abort / decline: BidBlacklistCooldownTicks (server config, 3600 = 60 s)
        #endregion

        private bool CoolingDown(KeyValuePair<long, Vector3I> key, int now)
        {
            int until;
            if (!blockCooldown.TryGetValue(key, out until)) return false;
            if (now < until) return true;
            blockCooldown.Remove(key);
            return false;
        }
        private readonly HashSet<KeyValuePair<long, Vector3I>> found = new HashSet<KeyValuePair<long, Vector3I>>();

        #region Small jobs wait
        // A group of fewer than BlocksPerJob blocks is published once its oldest block has waited this long since a scan
        // first found it: a projection's frontier opens a block or two per scan, and waiting lets them pile up into one job
        private const int SMALL_JOB_WAIT_TICKS = 30 * 60;
        private readonly Dictionary<KeyValuePair<long, Vector3I>, int> firstSeen = new Dictionary<KeyValuePair<long, Vector3I>, int>();
        private readonly List<KeyValuePair<long, Vector3I>> firstSeenGone = new List<KeyValuePair<long, Vector3I>>();

        // This scan's targets not in a job yet: since when they're known (blocks no longer found, or in a job, are forgotten)
        private void UpdateFirstSeen(int now)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var k = Key(targets[i]);
                if (!inJobs.Contains(k) && !firstSeen.ContainsKey(k)) firstSeen[k] = now;
            }
            firstSeenGone.Clear();
            foreach (var kv in firstSeen) if (!found.Contains(kv.Key) || inJobs.Contains(kv.Key)) firstSeenGone.Add(kv.Key);
            for (int i = 0; i < firstSeenGone.Count; i++) firstSeen.Remove(firstSeenGone[i]);
            firstSeenGone.Clear();
        }

        // The group may be published now: full, a predicted line (the batch itself), or its oldest block waited long enough
        private bool Ripe(int now)
        {
            if (group.Count >= settings.BlocksPerJob) return true;
            int oldest = now;
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].Line > 0) return true;
                int seen;
                if (firstSeen.TryGetValue(Key(group[i]), out seen) && seen < oldest) oldest = seen;
            }
            return now - oldest >= SMALL_JOB_WAIT_TICKS;
        }

        // A predicted block of an awarded job (or one some of whose blocks the scan still finds) whose cell is still empty
        private bool StillAhead(OpenJob job, KeyValuePair<long, Vector3I> k, bool live)
        {
            if (!live || !job.Predicted.Contains(k)) return false;
            IMyEntity e;
            var grid = MyAPIGateway.Entities.TryGetEntityById(k.Key, out e) ? e as IMyCubeGrid : null;
            return grid != null && grid.GetCubeBlock(k.Value) == null;
        }
        #endregion
        private readonly List<ConstructionTarget> targets = new List<ConstructionTarget>();
        private readonly List<ConstructionTarget> group = new List<ConstructionTarget>();
        private readonly List<IMyEntity> entityBuffer = new List<IMyEntity>();
        private readonly List<IMyCubeGrid> gridBuffer = new List<IMyCubeGrid>();
        private readonly HashSet<long> scannedGrids = new HashSet<long>();
        private readonly List<Message<AuctionWinnerAnnouncement>> awardInbox = new List<Message<AuctionWinnerAnnouncement>>();
        private readonly BlockLog log = new BlockLog();
        private readonly int[] lastCategory = new int[3];   // last scan: weld blocks per category (WorkCategory 0..2)
        private int messageCounter;
        private int lastFound, lastWeld, lastGrind, doneCount, expiredCount;

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
            ConstructionComputerTerminalControls.DoOnce(ModContext);
            LoadFromStorage();
            block.AppendingCustomInfo += AppendInfo;
            ready = true;
            if (!All.Contains(this)) All.Add(this);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
            Log.Info("CC {0}: '{1}' on '{2}': area {3}x{4}x{5} blocks, offset ({6:F0}, {7:F0}, {8:F0}) m, modes {9}, {10} saved job(s)", entityId,
                     block.CustomName, block.CubeGrid.DisplayName, settings.AreaSizeX, settings.AreaSizeY, settings.AreaSizeZ, settings.AreaOffsetBlocksX,
                     settings.AreaOffsetBlocksY, settings.AreaOffsetBlocksZ, settings.WorkModes, pendingState != null && pendingState.Jobs != null ? pendingState.Jobs.Count : 0);
        }

        public override void UpdateAfterSimulation100()
        {
            base.UpdateAfterSimulation100();
            if (!ready) return;
            try
            {
                if (IsServer) { Step(); double tl = Util.PerfStats.Start(); UpdateLcd(); Util.PerfStats.Stop(Util.PerfStats.LcdBuild, tl); }
                block.RefreshCustomInfo();
            }
            catch (Exception ex)
            {
                Log.Error("CC {0}: update error: {1}", entityId, ex);
                status = Loc.F(StrError, ex.Message);
            }
        }

        /// <summary>Construction Computers in the world (the path search's scene warm-up reads their areas).</summary>
        public static readonly List<ConstructionComputerBlock> All = new List<ConstructionComputerBlock>();

        public override void Close()
        {
            try
            {
                All.Remove(this);
                if (drawArea && AutomataSession.Instance != null) AutomataSession.Instance.RequestDraw(this, false);
                if (block != null) block.AppendingCustomInfo -= AppendInfo;
                scanCache.Dispose();
                if (messaging != null) { messaging.ReaderClosed(entityId); }
                if (Log.DebugEnabled) Log.Debug("CC {0}: closed ({1} open job(s))", entityId, jobs.Count);
            }
            catch (Exception ex)
            {
                Log.Error("CC {0}: close error: {1}", entityId, ex);
            }
            base.Close();
        }

        protected override string SettingsXml()
        {
            return MyAPIGateway.Utilities.SerializeToXML(settings);
        }

        protected override string SaveText()
        {
            var data = new ConstructionComputerSaveData { Settings = settings };
            if (IsServer) data.State = ready && pendingState == null ? CaptureState() : pendingState;
            data.EntityId = Entity.EntityId;
            return BlockSave.ToText(data);
        }

        protected override bool LoadSaveText(string text)
        {
            var data = BlockSave.FromText<ConstructionComputerSaveData>(text, Entity.EntityId);
            if (data == null) return false;
            settings = data.Settings ?? new ConstructionComputerBlockSettings();
            Clamp();
            if (IsServer && data.State != null && BlockSave.OwnState(data.EntityId, Entity.EntityId, "ConstructionComputer")) pendingState = data.State;
            return true;
        }

        protected override bool LoadSettingsXml(string xml)
        {
            var loaded = MyAPIGateway.Utilities.SerializeFromXML<ConstructionComputerBlockSettings>(xml);
            if (loaded == null) return false;
            settings = loaded;
            Clamp();
            return true;
        }

        protected override void OnSettingsChanged()
        {
            Clamp();
            string key = ScanKey();
            if (key != scanKey)
            {
                scanKey = key;
                lastScan = int.MinValue / 2;   // scan with the new settings next step (not for nickname / LCD edits)
            }
            CheckRetryRequest();
        }

        private string scanKey;

        // The settings a scan depends on
        private string ScanKey()
        {
            return string.Join("|", settings.Enabled, settings.WorkModes, settings.WorkPriority, settings.BlocksPerJob, settings.GrindColor,
                               settings.WeldIgnoreColor, settings.AreaSizeX, settings.AreaSizeY, settings.AreaSizeZ, settings.AreaOffsetBlocksX,
                               settings.AreaOffsetBlocksY, settings.AreaOffsetBlocksZ, settings.ScanIntervalSeconds);
        }

        #region Retry jobs (terminal button)
        private int lastRetryRequest;
        private bool retrySeen;

        // The button bumps RetryRequest (synced with the settings); the server retries on a change it hasn't seen
        private void CheckRetryRequest()
        {
            if (retrySeen && settings.RetryRequest != lastRetryRequest && IsServer) RetryJobs();
            lastRetryRequest = settings.RetryRequest;
            retrySeen = true;
        }

        public void Terminal_RetryJobs()
        {
            settings.RetryRequest++;
            SettingsEdited();
        }

        // Jobs no drone has: holds lifted, announced again now; the area scanned again
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
            lastScan = int.MinValue / 2;
            forceFullScan = true;
            blockCooldown.Clear();   // blocks held back after aborts come back too
            unreachableStrikes.Clear();
            AddEvent(Loc.F(StrRetryEvent, n));
            Log.Info("CC {0}: retry requested: {1} unassigned job(s) announced again, area rescanned", entityId, n);
        }
        #endregion

        private void Clamp()
        {
            settings.AreaSizeX = MathHelper.Clamp(settings.AreaSizeX, AREA_SIZE_MIN, AREA_SIZE_MAX);
            settings.AreaSizeY = MathHelper.Clamp(settings.AreaSizeY, AREA_SIZE_MIN, AREA_SIZE_MAX);
            settings.AreaSizeZ = MathHelper.Clamp(settings.AreaSizeZ, AREA_SIZE_MIN, AREA_SIZE_MAX);
            if (!settings.OffsetInBlocks)
            {
                // Older saves: the offset was in metres
                settings.AreaOffsetBlocksX = (int)Math.Round(settings.AreaOffsetX / AREA_UNIT);
                settings.AreaOffsetBlocksY = (int)Math.Round(settings.AreaOffsetY / AREA_UNIT);
                settings.AreaOffsetBlocksZ = (int)Math.Round(settings.AreaOffsetZ / AREA_UNIT);
                settings.AreaOffsetX = settings.AreaOffsetY = settings.AreaOffsetZ = 0;
                settings.OffsetInBlocks = true;
            }
            settings.AreaOffsetBlocksX = MathHelper.Clamp(settings.AreaOffsetBlocksX, -AREA_OFFSET_MAX, AREA_OFFSET_MAX);
            settings.AreaOffsetBlocksY = MathHelper.Clamp(settings.AreaOffsetBlocksY, -AREA_OFFSET_MAX, AREA_OFFSET_MAX);
            settings.AreaOffsetBlocksZ = MathHelper.Clamp(settings.AreaOffsetBlocksZ, -AREA_OFFSET_MAX, AREA_OFFSET_MAX);
            var config = AutomataSession.GetConfig();
            int minScan = config != null ? config.ConstructionComputer.MinScanRetryIntervalSeconds : 10;
            int maxScan = config != null ? config.ConstructionComputer.MaxScanRetryIntervalSeconds : 3600;
            settings.ScanIntervalSeconds = MathHelper.Clamp(settings.ScanIntervalSeconds, minScan, maxScan);
            settings.BlocksPerJob = MathHelper.Clamp(settings.BlocksPerJob, 1, 100);
        }
        #endregion

        #region Loop
        private void Step()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!retrySeen) CheckRetryRequest();   // loaded: the saved request counts as seen
            if (messaging == null) messaging = AutomataSession.GetMessageQueue();
            if (messaging == null) { status = Loc.T(StrWaitingForMod); return; }
            if (pendingState != null)
            {
                if (!BlockSave.CanRestore) { status = Loc.T(StrLoading); return; }
                RestoreState(pendingState);
                pendingState = null;
            }
            if (!settings.Enabled || !block.IsWorking)
            {
                status = Loc.T(!settings.Enabled ? StrOffTerminal : StrNotWorking);
                return;
            }
            if (antenna == null || now - lastAntennaSearch >= 600)
            {
                lastAntennaSearch = now;
                FindAntenna();
            }
            if (!EnsureAntenna()) { status = Loc.T(StrNoAntenna); return; }
            ReadAwards(now);
            ReadFailures(now);
            ReadJobReports(now);
            ReadKeepalives(now);
            droneWatch.Check(now, block, settings.UseBroadcastController, "CC {0}", entityId, AddEvent);
            if (now - lastScan >= settings.ScanIntervalSeconds * 60)
            {
                lastScan = now;
                // Grid groups that didn't change come from the cache; everything is looked at again every two minutes
                // (paint and obstructions have no event) and when asked (retry button)
                if (forceFullScan || now - lastFullScan >= Math.Max(ScanCache.FULL_RESCAN_SECONDS, settings.ScanIntervalSeconds) * 60)
                {
                    forceFullScan = false;
                    lastFullScan = now;
                    scanCache.Flush();
                }
                double t = PerfStats.Start();
                Scan(now);
                PerfStats.Stop(PerfStats.CcScan, t);
            }
            ExpireJobs(now);
            Announce(now);
            int open = 0, held = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Assigned != 0) continue;
                if (jobs[i].HeldUntil != 0) held++; else open++;
            }
            status = jobs.Count == 0 ? Loc.T(lastFound > 0 ? StrBlocksFoundNoJob : StrNothingToDo)
                                     : Loc.F(StrJobsSummary, open, held, jobs.Count - open - held);
        }

        private void FindAntenna()
        {
            var before = antenna;
            antenna = null;
            gridBuffer.Clear();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, gridBuffer);
            for (int g = 0; g < gridBuffer.Count; g++)
                foreach (var a in gridBuffer[g].GetFatBlocks<IMyRadioAntenna>())
                    if (a.IsFunctional && a.Enabled && a.EnableBroadcasting && (antenna == null || a.Radius > antenna.Radius))
                        antenna = a;
            gridBuffer.Clear();
            if (antenna != before)
                if (Log.DebugEnabled) Log.Debug("CC {0}: antenna {1}", entityId, antenna != null ? "'" + antenna.CustomName + "'" : "none");
        }

        private bool EnsureAntenna()
        {
            return AntennaReg.Ensure(messaging, ref antennaReg, antenna);
        }
        #endregion

        #region Scan
        /// <summary>The scanned box: along the block's axes, its centre offset (m) along them from the block.</summary>
        public void GetArea(out MatrixD matrix, out Vector3D halfExtents)
        {
            matrix = block.WorldMatrix;
            matrix.Translation += (matrix.Right * settings.AreaOffsetBlocksX + matrix.Up * settings.AreaOffsetBlocksY + matrix.Forward * settings.AreaOffsetBlocksZ) * AREA_UNIT;
            halfExtents = new Vector3D(settings.AreaSizeX, settings.AreaSizeY, settings.AreaSizeZ) * (AREA_UNIT * 0.5);
        }

        private void Scan(int now)
        {
            if (computer == null) computer = new ConstructionComputer(Entity, OperationMode.None);
            MatrixD area;
            Vector3D half;
            GetArea(out area, out half);
            var obb = new MyOrientedBoundingBoxD(area.Translation, half, Quaternion.CreateFromRotationMatrix(area));
            var box = obb.GetAABB();
            var grindColor = new Color(settings.GrindColor);
            var weldIgnoreColor = new Color(settings.WeldIgnoreColor);
            double watch = PerfStats.Start();
            scanCache.Begin(ref area, ref half, settings.WorkModes, grindColor, weldIgnoreColor, settings.WorkPriority);

            // Every grid group reaching the area (each scanned once, from any of its grids)
            targets.Clear();
            scannedGrids.Clear();
            refusedGrids.Clear();
            entityBuffer.Clear();
            entityBuffer.AddRange(MyAPIGateway.Entities.GetEntitiesInAABB(ref box));
            int groups = 0, cached = 0;
            bool projections = (settings.WorkModes & WorkModes.WeldProjectedBlocks) != 0;
            for (int i = 0; i < entityBuffer.Count && targets.Count < MAX_TARGETS_PER_SCAN; i++)
            {
                var grid = entityBuffer[i] as IMyCubeGrid;
                if (grid == null || grid.Physics == null || scannedGrids.Contains(grid.EntityId)) continue;
                gridBuffer.Clear();
                grid.GetGridGroup(GridLinkTypeEnum.Physical).GetGrids(gridBuffer);
                for (int g = 0; g < gridBuffer.Count; g++) scannedGrids.Add(gridBuffer[g].EntityId);
                groups++;
                int limit = MAX_TARGETS_PER_SCAN - targets.Count;
                double tg = PerfStats.Start();
                long groupKey;
                ulong signature = scanCache.Signature(gridBuffer, projections, RefuseGrid, out groupKey);
                gridBuffer.Clear();
                if (scanCache.TryGet(groupKey, signature, limit, targets))
                {
                    cached++;
                    PerfStats.Stop(PerfStats.CcGroupCached, tg);
                    continue;
                }
                int first = targets.Count;
                computer.ScanArea(grid, ref area, ref half, settings.WorkModes, grindColor, weldIgnoreColor, null, limit, targets, RefuseGrid,
                                  settings.WorkPriority, null, true, Math.Max(0, settings.BlocksPerJob - 1));
                scanCache.Store(groupKey, signature, limit, targets, first);
                PerfStats.Stop(PerfStats.CcGroup, tg);
            }
            entityBuffer.Clear();
            SortByPriority(targets);
            scanCache.End(targets);

            // Done: blocks of open jobs the scan no longer finds
            found.Clear();
            int weld = 0, grind = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                found.Add(Key(targets[i]));
                if (targets[i].Type == Orchestrator.JobType.Weld) weld++; else grind++;
            }
            // (only from a complete scan: one cut off at MAX_TARGETS_PER_SCAN didn't see every block)
            bool complete = targets.Count < MAX_TARGETS_PER_SCAN;
            for (int j = jobs.Count - 1; j >= 0 && complete; j--)
            {
                var job = jobs[j];
                bool live = job.Assigned != 0;
                if (!live) foreach (var k in job.Blocks) if (found.Contains(k)) { live = true; break; }
                job.Blocks.RemoveWhere(k => !found.Contains(k) && !refusedGrids.Contains(k.Key)   // a drone flying off isn't repaired
                                            && !StillAhead(job, k, live));
                if (job.Blocks.Count > 0) continue;
                doneCount++;
                AddEvent(Loc.F(StrJobDone, job.Summary ?? job.Blocks.Count.ToString(), LabelOf(job)));
                if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} ({2}) done: none of its blocks needs work any more", entityId, job.JobId, LabelOf(job));
                jobs.RemoveAt(j);
            }
            RebuildInJobs();

            // New jobs: targets not in a job yet, in build order, a few blocks each (one job type per job), while fewer
            // than MaxPublishedJobs are published (announced or on hold, not awarded)
            int maxOpen = AutomataSession.GetConfig() != null ? AutomataSession.GetConfig().ConstructionComputer.MaxPublishedJobs : 10;
            int created = 0;
            ShareSlots(maxOpen, now);
            UpdateFirstSeen(now);
            group.Clear();
            int waiting = 0;
            bool lineOpen = false;   // the group holds the seed of the predicted blocks that follow it
            for (int i = 0; i <= targets.Count && OpenCount() < maxOpen; i++)
            {
                bool eligible = i < targets.Count && !inJobs.Contains(Key(targets[i])) && !CoolingDown(Key(targets[i]), now);
                bool startsLine = eligible && targets[i].Line > 0;   // a line starts a job of its own
                bool flush = i == targets.Count || group.Count >= settings.BlocksPerJob || (group.Count > 0 && startsLine)
                             || (group.Count > 0 && (targets[i].Type != group[0].Type || targets[i].Category != group[0].Category));
                if (flush && group.Count > 0)
                {
                    int b = Bucket(group[0]);
                    if (openPer[b] >= slotsPer[b]) { }                     // its kind's share is full
                    else if (!Ripe(now)) waiting += group.Count;          // small: more may join it by the next scans
                    else if (CreateJob(now)) { created++; openPer[b]++; }
                    group.Clear();
                    lineOpen = false;
                }
                if (i == targets.Count) continue;
                if (!targets[i].Predicted) lineOpen = false;
                // A predicted block only right behind its seed, in the seed's job (never ahead of it, never alone)
                if (!eligible || (targets[i].Predicted && !lineOpen)) { if (targets[i].Predicted) lineOpen = false; continue; }
                group.Add(targets[i]);
                if (startsLine) lineOpen = true;
            }
            group.Clear();
            lastFound = targets.Count;
            lastWeld = weld;
            lastGrind = grind;
            for (int c = 0; c < lastCategory.Length; c++) lastCategory[c] = 0;
            for (int t = 0; t < targets.Count; t++)
                if (targets[t].Type == Orchestrator.JobType.Weld && (int)targets[t].Category < lastCategory.Length) lastCategory[(int)targets[t].Category]++;
            if (Log.DebugEnabled) Log.Debug("CC {0}: scan {1:F1} ms: {2} grid group(s) ({8} unchanged), {3} block(s) ({4} weld, {5} grind), {6} new job(s), {7} open, {9} block(s) waiting to batch",
                      entityId, PerfStats.Since(watch), groups, targets.Count, weld, grind, created, jobs.Count, cached, waiting);
            targets.Clear();
        }

        private readonly List<ConstructionTarget> sortBuffer = new List<ConstructionTarget>();

        #region Weld priority: shares of the published jobs
        // Buckets: 0..2 the weld kinds (WorkCategory Repair / Projected / Unbuilt), 3 grind
        private const int BUCKETS = 4, GRIND_BUCKET = 3;
        private readonly int[] openPer = new int[BUCKETS];    // published (unassigned) jobs per bucket
        private readonly int[] slotsPer = new int[BUCKETS];   // this scan's share of MaxPublishedJobs
        private readonly int[] demandPer = new int[BUCKETS];  // jobs a bucket could fill: published + new ones its blocks make

        private static int Bucket(ConstructionTarget t)
        {
            return t.Type == Orchestrator.JobType.Grind || (int)t.Category >= GRIND_BUCKET ? GRIND_BUCKET : (int)t.Category;
        }

        private static int Bucket(OpenJob j)
        {
            return j.Type == Orchestrator.JobType.Grind || (int)j.Category >= GRIND_BUCKET ? GRIND_BUCKET : (int)j.Category;
        }

        // Weld priority weights 3 / 2 / 1 by rank; grind weighs as the first rank (it isn't part of the order)
        private int Weight(int bucket)
        {
            return bucket == GRIND_BUCKET ? 3 : 3 - WorkPriorities.Rank(settings.WorkPriority, (WorkCategory)bucket);
        }

        /// <summary>
        /// Weld priority as shares, not a strict order: every kind with work gets published jobs at once (so welders biased
        /// to one kind - Welder:Repair / Projected / Unbuilt - find their kind of job), the slots of MaxPublishedJobs split
        /// 3 : 2 : 1 by rank (grind as the first rank) - Sainte-Laguë, so 10 slots over three kinds are 5 / 3 / 2. A kind
        /// with fewer jobs to make than its share leaves the rest to the others. Jobs already published count against
        /// their kind's share (a priority change doesn't withdraw any).
        /// </summary>
        private void ShareSlots(int maxOpen, int now)
        {
            for (int b = 0; b < BUCKETS; b++) { openPer[b] = 0; slotsPer[b] = 0; demandPer[b] = 0; }
            for (int j = 0; j < jobs.Count; j++) if (jobs[j].Assigned == 0) openPer[Bucket(jobs[j])]++;
            for (int i = 0; i < targets.Count; i++)
                if (!inJobs.Contains(Key(targets[i])) && !CoolingDown(Key(targets[i]), now)) demandPer[Bucket(targets[i])]++;   // blocks first
            int perJob = Math.Max(1, settings.BlocksPerJob);
            for (int b = 0; b < BUCKETS; b++) demandPer[b] = openPer[b] + (demandPer[b] + perJob - 1) / perJob;
            for (int n = 0; n < maxOpen; n++)
            {
                int best = -1;
                double bestQ = 0;
                for (int b = 0; b < BUCKETS; b++)
                {
                    if (slotsPer[b] >= demandPer[b]) continue;
                    double q = Weight(b) / (2.0 * slotsPer[b] + 1);
                    if (best < 0 || q > bestQ) { best = b; bestQ = q; }
                }
                if (best < 0) break;
                slotsPer[best]++;
            }
            if (Log.DebugEnabled) Log.Debug("CC {0}: published job shares (repair / projected / unbuilt / grind): slots {1}/{2}/{3}/{4}, published {5}/{6}/{7}/{8}",
                          entityId, slotsPer[0], slotsPer[1], slotsPer[2], slotsPer[3], openPer[0], openPer[1], openPer[2], openPer[3]);
        }
        #endregion

        // Every grid group's targets together, the categories in weld priority order (each group's build order kept)
        private void SortByPriority(List<ConstructionTarget> list)
        {
            sortBuffer.Clear();
            for (int rank = 0; rank < 3; rank++)
                for (int i = 0; i < list.Count; i++)
                    if (WorkPriorities.Rank(settings.WorkPriority, list[i].Category) == rank) sortBuffer.Add(list[i]);
            list.Clear();
            list.AddRange(sortBuffer);
            sortBuffer.Clear();
        }

        // Owner / faction grids (or nobody's) only: never reveal or touch anyone else's; no working drones
        private readonly HashSet<long> refusedGrids = new HashSet<long>();   // this scan: grids skipped (their jobs aren't "done")

        private string RefuseGrid(IMyCubeGrid grid)
        {
            string why = RefusalOf(grid);
            if (why != null) refusedGrids.Add(grid.EntityId);
            return why;
        }

        private string RefusalOf(IMyCubeGrid grid)
        {
            var owners = grid.BigOwners;
            if (owners != null && owners.Count > 0)
            {
                bool allowed = false;
                for (int i = 0; i < owners.Count && !allowed; i++) allowed = AnchorDirectory.IsAllowed(block.OwnerId, owners[i]);
                if (!allowed) return Loc.T(StrNotOwnerFaction);
            }
            return DroneControllerBlock.IsActiveDroneGroup(grid) ? Loc.T(StrWorkingDrone) : null;
        }

        private static KeyValuePair<long, Vector3I> Key(ConstructionTarget t)
        {
            return new KeyValuePair<long, Vector3I>(t.GridEntityId, t.Cell);
        }

        private int OpenCount()
        {
            int n = 0;
            for (int i = 0; i < jobs.Count; i++) if (jobs[i].Assigned == 0) n++;
            return n;
        }

        private void RebuildInJobs()
        {
            inJobs.Clear();
            for (int j = 0; j < jobs.Count; j++) foreach (var k in jobs[j].Blocks) inJobs.Add(k);
        }

        // For logs: "Weld:Repair" / "Weld:Projected" / "Weld:Unbuilt" / "Grind"
        private static string LabelOf(OpenJob job)
        {
            return Orchestrator.JobTypeExtensions.Label(job.Type,
                job.Type == Orchestrator.JobType.Weld ? Orchestrator.JobTypeExtensions.WeldCode(job.Category) : Orchestrator.JobTypeExtensions.WELD_ANY);
        }

        private bool CreateJob(int now)
        {
            float interference;
            float g = MyAPIGateway.Physics.CalculateNaturalGravityAt(group[0].Center, out interference).Length();
            var items = new DiscreteInventory();
            var job = computer.JobFor(group, g, items);
            if (job == null) return false;
            var open = new OpenJob { JobId = job.JobId, Type = job.JobType, Category = group[0].Category, Opened = now, Summary = Summarize(group) };
            for (int i = 0; i < group.Count; i++)
            {
                var k = Key(group[i]);
                open.Blocks.Add(k);
                if (group[i].Predicted) open.Predicted.Add(k);
                inJobs.Add(k);
            }
            var ja = JobAnnouncement.FromJob(job, AutomataEntityType.ConstructionComputerBlock, block.CubeGrid.IsStatic);
            ja.ComponentsInventory = items;
            ja.WeldCategory = job.JobType == Orchestrator.JobType.Weld ? Orchestrator.JobTypeExtensions.WeldCode(open.Category) : Orchestrator.JobTypeExtensions.WELD_ANY;
            ja.CreatedTime = DateTime.UtcNow;
            ja.RequesterEntityId = entityId;
            ja.NaturalGravity = g;
            ja.IsInSpace = g < JobAnnouncement.SPACE_GRAVITY;
            ja.IsInAtmosphere = g >= JobAnnouncement.SPACE_GRAVITY;
            if (ja.IsInAtmosphere) ja.ToolFaceWeights = ConstructionComputer.ToolFaceWeights(group);   // in space any tool orientation does
            for (int i = 0; i < group.Count && !ja.Sequential; i++) ja.Sequential = group[i].Predicted;   // a line: one drone, trip after trip
            open.Message = AnnouncementMessage(ja);
            jobs.Add(open);
            if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} ({2}): {3} block(s), first at {4}, {5}", entityId, job.JobId, LabelOf(open), job.BlockCount,
                      group[0].Cell, Describe(items));
            return true;
        }
        #endregion

        #region Announcements / awards / failures
        private Message<JobAnnouncement> AnnouncementMessage(JobAnnouncement ja)
        {
            return new Message<JobAnnouncement>
            {
                Payload = ja,
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT,
            };
        }

        // Each job once; again only if that reached no orchestrator (soon) or nothing came back (after a long while:
        // an orchestrator gone / reloaded). Awarded jobs and jobs on hold are not announced.
        private void Announce(int now)
        {
            // Weld priority: the first category's jobs go out first (orchestrators queue them in the order they come)
            for (int rank = 0; rank < 3; rank++)
            for (int j = 0; j < jobs.Count; j++)
            {
                var job = jobs[j];
                if (job.Assigned != 0 || WorkPriorities.Rank(settings.WorkPriority, job.Category) != rank) continue;
                if (job.HeldUntil != 0)
                {
                    if (now < job.HeldUntil) continue;
                    job.HeldUntil = 0;
                    job.LastAnnounced = int.MinValue / 2;
                    if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} off hold", entityId, job.JobId);
                }
                if (job.Pending)
                {
                    if (job.Message.Delivered) { job.Pending = false; job.Announced = true; job.LastResult = Loc.T(StrAnnounced); }
                    else if (now - job.LastAnnounced >= messaging.LifetimeTicks) { job.Pending = false; job.Announced = false; job.LastResult = Loc.T(StrNoOrchestratorInRange); }
                    else continue;   // still in the network
                    if (job.Announced && !job.Logged)
                    {
                        job.Logged = true;   // no drone yet: an announcement
                        AddEvent(Loc.F(StrLogJob, LabelOf(job), job.Summary));
                    }
                }
                if (now - job.LastAnnounced < (job.Announced ? RESPONSE_TIMEOUT_TICKS : RETRY_TICKS)) continue;
                bool again = job.LastAnnounced > int.MinValue / 2;
                job.LastAnnounced = now;
                job.Message.MessageId = IdGenerator.GenerateId(ref messageCounter, entityId);
                job.Message.CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
                var error = messaging.BroadcastMessage(antennaReg, job.Message, false);
                job.Announced = false;
                job.Pending = error == ErrorCode.None;
                if (error != ErrorCode.None) job.LastResult = error.ToString();
                if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} ({2}, {3} block(s)) {4}: {5}", entityId, job.JobId, LabelOf(job), job.Blocks.Count,
                          again ? "announced again" : "announced", error);
            }
        }

        // An orchestrator of the owner / faction got no bid for one of its jobs: on hold for the time it says
        private void ReadFailures(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.ConstructionComputer, antennaReg, Channel.JOB_ANNOUNCEMENT_FAILED, failureInbox, 20, true, PayloadType.JobAnnouncementFailed);
            for (int i = 0; i < failureInbox.Count; i++)
            {
                var f = failureInbox[i].Payload;
                if (f == null || f.AnnouncerEntityId != entityId || !AnchorDirectory.IsAllowed(block.OwnerId, failureInbox[i].SenderOwnerId)) continue;
                for (int j = 0; j < jobs.Count; j++)
                {
                    var job = jobs[j];
                    if (job.JobId != f.JobId || job.Assigned != 0) continue;
                    if (job.HeldUntil != 0 && job.HeldUntil > now) continue;   // the same failure read again
                    job.HeldUntil = now + Math.Max(10, f.HoldSeconds) * 60;
                    failedCount++;
                    AddEvent(Loc.F(StrJobOnHold, LabelOf(job) + ":\n" + job.Summary, f.HoldSeconds, f.Reason));
                    if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} on hold for {2} s: orchestrator {3}: {4}", entityId, job.JobId, f.HoldSeconds, f.OrchestratorEntityId, f.Reason);
                }
            }
            failureInbox.Clear();
        }

        // An orchestrator of the owner / faction awarded one of its jobs: not announced again, its blocks kept out of new jobs
        private void ReadAwards(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.ConstructionComputer, antennaReg, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT, awardInbox, 20, true,
                                   PayloadType.AuctionWinnerAnnouncement);
            for (int i = 0; i < awardInbox.Count; i++)
            {
                var award = awardInbox[i].Payload;
                if (award == null || award.AnnouncerEntityId != entityId
                    || !AnchorDirectory.IsAllowed(block.OwnerId, awardInbox[i].SenderOwnerId)) continue;
                for (int j = 0; j < jobs.Count; j++)
                {
                    var job = jobs[j];
                    if (job.JobId != award.JobId || job.Assigned != 0) continue;
                    job.Assigned = now;
                    job.HeldUntil = 0;
                    int drones = award.TaskAssignments != null ? award.TaskAssignments.Count : 0;
                    job.DronesOut = drones;
                    var names = new StringBuilder();
                    if (award.TaskAssignments != null)
                        foreach (var d in award.TaskAssignments.Keys) { if (names.Length > 0) names.Append(", "); names.Append(Nicknames.Of(d)); }
                    AddEvent(Loc.F(StrLogJobDrone, LabelOf(job), job.Summary, names));
                    if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} assigned by orchestrator {2} to {3} drone(s)", entityId, job.JobId, awardInbox[i].SenderId, drones);
                }
            }
            awardInbox.Clear();
        }

        // The drones' outcomes: when every drone of an awarded job is done, or one gave up, the job leaves the cache;
        // its blocks go back to the pool and the next scan makes new jobs of what is still to do
        private readonly List<Message<JobStatusReport>> reportInbox = new List<Message<JobStatusReport>>();

        private void ReadJobReports(int now)
        {
            bool changed = false;
            for (int c = 0; c < 2; c++)
            {
                messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.ConstructionComputer, antennaReg, c == 0 ? Channel.TASKS_COMPLETED : Channel.TASKS_ABORTED, reportInbox, 20, true,
                                       PayloadType.JobStatusReport);
                for (int i = 0; i < reportInbox.Count; i++)
                {
                    var r = reportInbox[i].Payload;
                    if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId
                        || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                    droneWatch.Done(r.DroneEntityId);
                    for (int j = jobs.Count - 1; j >= 0; j--)
                    {
                        var job = jobs[j];
                        if (job.JobId != r.JobId) continue;
                        // An award this computer never heard (out of range, lost over a load): the drone's report counts
                        if (job.Assigned == 0) { job.Assigned = now; job.DronesOut = Math.Max(job.DronesOut, 1); }
                        job.DronesOut--;
                        // Several drones on one job: it closes when the last one reports (one drone's abort doesn't free
                        // blocks the others are still on)
                        if (r.Outcome != JobOutcome.Completed) { job.Failures++; job.LastFailure = r.Reason; }
                        // Its unreachable blocks (newer drones): those held back long, the rest of an abort briefly
                        if (r.PerBlock)
                        {
                            job.PerBlock = true;
                            if (r.UnreachableGrids != null && r.UnreachableCells != null)
                                for (int u = 0; u < r.UnreachableGrids.Count && u < r.UnreachableCells.Count; u++)
                                {
                                    var key = new KeyValuePair<long, Vector3I>(r.UnreachableGrids[u], r.UnreachableCells[u].ToVector3I());
                                    if (job.Blocks.Contains(key)) job.Unreachable.Add(key);
                                }
                        }
                        bool close = job.DronesOut <= 0;
                        AddEvent(string.IsNullOrEmpty(r.Reason) ? Loc.F(StrJobDroneOutcome, LabelOf(job), Nicknames.Of(r.DroneEntityId), r.Outcome)
                                                                 : Loc.F(StrJobDroneOutcomeReason, LabelOf(job), Nicknames.Of(r.DroneEntityId), r.Outcome, r.Reason));
                        if (Log.DebugEnabled) Log.Debug("CC {0}: job {1}: drone {2} {3} ({4}), {5}/{6} block(s); {7}", entityId, job.JobId, r.DroneEntityId, r.Outcome,
                                  r.Reason, r.BlocksDone, r.BlocksTotal, close ? "closed, blocks back to the pool" : job.DronesOut + " drone(s) still out");
                        if (!close) continue;
                        if (job.Unreachable.Count > 0) HoldUnreachable(job, now);
                        if (job.Failures == 0) doneCount++;
                        else
                        {
                            // Older drones say only why the job ended: an "Unreachable" holds every block of it (the way home
                            // failing too, though its blocks were fine)
                            bool unreachable = !job.PerBlock && job.LastFailure != null && job.LastFailure.IndexOf("Unreachable", StringComparison.OrdinalIgnoreCase) >= 0;
                            int until = now + (unreachable ? UNREACHABLE_COOLDOWN_TICKS : AbortCooldownTicks());
                            int held = 0;
                            foreach (var k in job.Blocks)
                            {
                                if (job.Unreachable.Contains(k)) continue;
                                blockCooldown[k] = until;
                                held++;
                            }
                            if (held > 0)
                                if (Log.DebugEnabled) Log.Debug("CC {0}: job {1}: {2} block(s) held back {3} s before a new job", entityId, job.JobId, held, (until - now) / 60);
                        }
                        foreach (var k in job.Blocks) scanCache.Touch(k.Key);   // its grids are looked at again whatever the events said
                        jobs.RemoveAt(j);
                        changed = true;
                    }
                }
                reportInbox.Clear();
            }
            if (changed)
            {
                RebuildInJobs();
                lastScan = int.MinValue / 2;   // what is left becomes new jobs now
            }
        }

        // Blocks a drone found no way to: held back UNREACHABLE_COOLDOWN, doubled for each report in a row (the drones
        // stop trying what nobody reaches, a new scan / Retry / an area change brings them back sooner)
        private void HoldUnreachable(OpenJob job, int now)
        {
            if (unreachableStrikes.Count > 4096) unreachableStrikes.Clear();
            int longest = 0;
            foreach (var k in job.Unreachable)
            {
                int strikes;
                unreachableStrikes.TryGetValue(k, out strikes);
                strikes = Math.Min(strikes + 1, 8);
                unreachableStrikes[k] = strikes;
                long ticks = Math.Min((long)UNREACHABLE_COOLDOWN_TICKS << (strikes - 1), UNREACHABLE_COOLDOWN_MAX_TICKS);
                blockCooldown[k] = now + (int)ticks;
                longest = Math.Max(longest, (int)ticks);
            }
            if (Log.DebugEnabled) Log.Debug("CC {0}: job {1}: {2} unreachable block(s) held back up to {3} s", entityId, job.JobId, job.Unreachable.Count, longest / 60);
        }

        // Drones still working on an awarded job: its expiry starts again (a dead / destroyed / lost drone stops sending)
        private readonly Util.DroneWatch droneWatch = new Util.DroneWatch();

        private void ReadKeepalives(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.ConstructionComputer, antennaReg, Channel.JOB_KEEPALIVE, reportInbox, 50, true, PayloadType.JobStatusReport);
            for (int i = 0; i < reportInbox.Count; i++)
            {
                var r = reportInbox[i].Payload;
                if (r == null || r.AnnouncerEntityId != entityId || r.DroneEntityId != reportInbox[i].SenderId
                    || !AnchorDirectory.IsAllowed(block.OwnerId, reportInbox[i].SenderOwnerId)) continue;
                droneWatch.Heard(r.DroneEntityId, r.JobId, now, AddEvent);
                for (int j = 0; j < jobs.Count; j++)
                    if (jobs[j].JobId == r.JobId) { if (jobs[j].Assigned == 0) jobs[j].DronesOut = Math.Max(jobs[j].DronesOut, 1); jobs[j].Assigned = now; }   // unheard award: assigned now
            }
            reportInbox.Clear();
        }

        private void ExpireJobs(int now)
        {
            bool changed = false;
            for (int j = jobs.Count - 1; j >= 0; j--)
            {
                var job = jobs[j];
                // Unawarded jobs stay (cached, announced again when due); an award long ago didn't get them done
                if (job.Assigned == 0 || now - job.Assigned <= ASSIGNED_TTL_TICKS) continue;
                expiredCount++;
                if (Log.DebugEnabled) Log.Debug("CC {0}: job {1} ({2}) no drone keepalive for {3} s: {4} block(s) back to the pool", entityId, job.JobId, LabelOf(job),
                          ASSIGNED_TTL_TICKS / 60, job.Blocks.Count);
                AddEvent(Loc.F(StrJobExpired, LabelOf(job) + ":\n" + job.Summary));
                jobs.RemoveAt(j);
                changed = true;
            }
            if (changed) RebuildInJobs();
        }
        #endregion

        #region Info
        private static string Describe(DiscreteInventory items)
        {
            if (items == null || items.IsEmpty()) return "nothing";
            var sb = new StringBuilder();
            foreach (var item in items.GetAllItems())
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(item.Value).Append(' ').Append(item.Key.String);
            }
            return sb.ToString();
        }

        // A log entry (terminal and LCDs): first line at the left edge, its other lines indented
        private void AddEvent(string text)
        {
            log.Add(text);
        }

        // "  Light Armor Block: 8\n  Window: 2": a job's blocks by name, one per line (LCD width)
        private static string Summarize(List<ConstructionTarget> group)
        {
            var names = new List<string>();
            var counts = new List<int>();
            for (int i = 0; i < group.Count; i++)
            {
                string n = group[i].BlockName ?? "?";
                int k = names.IndexOf(n);
                if (k < 0) { names.Add(n); counts.Add(1); } else counts[k]++;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append("  ").Append(names[i]).Append(": ").Append(counts[i]);
            }
            return sb.ToString();
        }

        private void AppendInfo(IMyTerminalBlock b, StringBuilder sb)
        {
            if (!ready) return;
            info.Clear();
            info.AppendLoc(StrInfoStatus, status).Append('\n');
            info.AppendLoc(StrInfoArea, settings.AreaSizeX, settings.AreaSizeY,
                              settings.AreaSizeZ, settings.AreaSizeX * AREA_UNIT, settings.AreaSizeY * AREA_UNIT, settings.AreaSizeZ * AREA_UNIT,
                              settings.ScanIntervalSeconds).Append('\n');
            info.AppendLoc(StrInfoLastScan, lastFound, lastWeld, lastGrind).Append('\n');
            info.AppendLoc(StrInfoJobs, jobs.Count, doneCount, failedCount, expiredCount).Append('\n');
            int nowFrame = MyAPIGateway.Session.GameplayFrameCounter;
            for (int j = 0; j < jobs.Count && j < 10; j++)
                info.AppendLoc(StrInfoJobLine, jobs[j].JobId, LabelOf(jobs[j]), jobs[j].Blocks.Count,
                                  jobs[j].Assigned != 0 ? Loc.T(StrAssigned)
                                  : jobs[j].HeldUntil > nowFrame ? Loc.F(StrOnHoldSeconds, (jobs[j].HeldUntil - nowFrame) / 60)
                                  : jobs[j].LastResult ?? Loc.T(StrPending)).Append('\n');
            if (antenna == null) info.AppendLoc(StrWarnNoAntenna).Append('\n');
            BuildHeader();
            info.Append('\n');
            for (int i = 0; i < header.Count; i++) info.Append(header[i]).Append('\n');
            info.Append(new string('-', 40)).Append('\n');
            log.AppendTo(info);
            sb.Append(info);
        }
        #endregion

        #region Draw
        public bool DrawDebug()
        {
            if (!drawArea || block == null || block.MarkedForClose) return false;
            int timeout = Config.ServerConfig.Instance.Drone.DebugDrawTimeoutSeconds * 60;
            if (MyAPIGateway.Session.GameplayFrameCounter - drawStart > timeout) { drawArea = false; return false; }
            MatrixD m;
            Vector3D half;
            GetArea(out m, out half);
            var localBox = new BoundingBoxD(-half, half);
            Color faces = Color.Magenta * 0.15f;
            Color grid = Color.Magenta * 0.6f;
            var cells = new Vector3I(Math.Max(1, settings.AreaSizeX), Math.Max(1, settings.AreaSizeY), Math.Max(1, settings.AreaSizeZ));
            VRage.Game.MySimpleObjectDraw.DrawTransparentBox(ref m, ref localBox, ref faces, ref faces, VRage.Game.MySimpleObjectRasterizer.Solid,
                cells, 0.002f, DrawMaterial, null, false, -1, VRageRender.MyBillboard.BlendTypeEnum.Standard);
            VRage.Game.MySimpleObjectDraw.DrawTransparentBox(ref m, ref localBox, ref grid, ref grid, VRage.Game.MySimpleObjectRasterizer.Wireframe,
                cells, 0.002f, null, DrawMaterial, false, -1, VRageRender.MyBillboard.BlendTypeEnum.SDR);
            return true;
        }

        public void StopDebugDraw()
        {
            drawArea = false;
        }
        #endregion

        #region Save / load
        private ConstructionComputerState CaptureState()
        {
            var s = new ConstructionComputerState
            {
                JobCounter = computer != null ? computer.JobIdCounter : savedJobCounter,
                MessageCounter = messageCounter,
                Done = doneCount,
                Expired = expiredCount,
                Failed = failedCount,
            };
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            for (int j = 0; j < jobs.Count; j++)
            {
                var job = jobs[j];
                var js = new ConstructionJobState
                {
                    Announcement = job.Message.Payload,
                    OpenedAgeTicks = BlockSave.Age(job.Opened),
                    AssignedAgeTicks = job.Assigned != 0 ? BlockSave.Age(job.Assigned) : -1,
                    HoldTicksLeft = job.HeldUntil > now ? job.HeldUntil - now : 0,
                    Announced = job.Announced,
                    DronesOut = job.DronesOut,
                    Category = job.Category,
                    Summary = job.Summary,
                };
                foreach (var k in job.Blocks) js.Blocks.Add(new BlockRefState { Grid = k.Key, X = k.Value.X, Y = k.Value.Y, Z = k.Value.Z });
                s.Jobs.Add(js);
            }
            var frameGrids = new HashSet<long>(scannedGrids);
            for (int j = 0; j < jobs.Count; j++) foreach (var k in jobs[j].Blocks) frameGrids.Add(k.Key);
            BuildPlanner.ExportFrames(frameGrids, s.Frames);
            if (Log.DebugEnabled) Log.Debug("CC {0}: state saved: {1} job(s), {2} sweep frame(s)", entityId, s.Jobs.Count, s.Frames.Count);
            return s;
        }

        private int savedJobCounter;

        private void RestoreState(ConstructionComputerState s)
        {
            if (computer == null) computer = new ConstructionComputer(Entity, OperationMode.None);
            computer.JobIdCounter = Math.Max(computer.JobIdCounter, s.JobCounter);
            savedJobCounter = s.JobCounter;
            messageCounter = s.MessageCounter;
            doneCount = s.Done;
            expiredCount = s.Expired;
            failedCount = s.Failed;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (s.Jobs != null)
                for (int j = 0; j < s.Jobs.Count; j++)
                {
                    var js = s.Jobs[j];
                    if (js.Announcement == null || js.Blocks == null || js.Blocks.Count == 0) continue;
                    // Saved with item keys this session can't name (grind returns keyed by display name before the
                    // fix): dropped, the next scan plans those blocks again
                    if (!Inventory.ItemKeys.AllKnown(js.Announcement.ComponentsInventory))
                    {
                        Log.Info("CC {0}: saved job {1} ({2}) dropped: its items use old keys; planned again by the next scan", entityId,
                                 js.Announcement.JobId, js.Announcement.Type);
                        continue;
                    }
                    var job = new OpenJob
                    {
                        JobId = js.Announcement.JobId,
                        Type = js.Announcement.Type,
                        Category = js.Category,
                        Summary = js.Summary ?? js.Blocks.Count.ToString(),
                        Logged = true,
                        Opened = BlockSave.FrameFromAge(js.OpenedAgeTicks),
                        Assigned = js.AssignedAgeTicks >= 0 ? Math.Max(1, BlockSave.FrameFromAge(js.AssignedAgeTicks)) : 0,
                        HeldUntil = js.HoldTicksLeft > 0 ? now + js.HoldTicksLeft : 0,
                        DronesOut = js.DronesOut,
                        // Announced before the save: once more soon (orchestrators de-duplicate), in case the save lost it
                        Announced = false,
                        Message = AnnouncementMessage(js.Announcement),
                    };
                    for (int b = 0; b < js.Blocks.Count; b++)
                        job.Blocks.Add(new KeyValuePair<long, Vector3I>(js.Blocks[b].Grid, new Vector3I(js.Blocks[b].X, js.Blocks[b].Y, js.Blocks[b].Z)));
                    jobs.Add(job);
                }
            RebuildInJobs();
            BuildPlanner.ImportFrames(s.Frames);
            Log.Info("CC {0}: state restored: {1} job(s), {2} sweep frame(s)", entityId, jobs.Count,
                     s.Frames != null ? s.Frames.Count : 0);
        }
        #endregion

        #region Terminal
        public bool Terminal_IsEnabled
        {
            get { return settings.Enabled; }
            set { settings.Enabled = value; SettingsEdited(); }
        }

        public bool Terminal_GetMode(WorkModes mode) { return (settings.WorkModes & mode) != 0; }

        public long Terminal_WorkPriority
        {
            get { return (long)settings.WorkPriority; }
            set
            {
                var p = WorkPriorities.Clamp(value);
                if (settings.WorkPriority == p) return;
                settings.WorkPriority = p;
                SettingsEdited();
            }
        }

        public void Terminal_SetMode(WorkModes mode, bool on)
        {
            if (on) settings.WorkModes |= mode; else settings.WorkModes &= ~mode;
            SettingsEdited();
        }

        // axis: 0 = X (right), 1 = Y (up), 2 = Z (forward)
        public int Terminal_GetAreaSize(int axis) { return axis == 0 ? settings.AreaSizeX : axis == 1 ? settings.AreaSizeY : settings.AreaSizeZ; }
        public void Terminal_SetAreaSize(int axis, int blocks)
        {
            if (axis == 0) settings.AreaSizeX = blocks; else if (axis == 1) settings.AreaSizeY = blocks; else settings.AreaSizeZ = blocks;
            SettingsEdited();
        }
        public int Terminal_GetAreaOffset(int axis) { return axis == 0 ? settings.AreaOffsetBlocksX : axis == 1 ? settings.AreaOffsetBlocksY : settings.AreaOffsetBlocksZ; }
        public void Terminal_SetAreaOffset(int axis, int blocks)
        {
            if (axis == 0) settings.AreaOffsetBlocksX = blocks; else if (axis == 1) settings.AreaOffsetBlocksY = blocks; else settings.AreaOffsetBlocksZ = blocks;
            SettingsEdited();
        }

        /// <summary>Draws the observation area (magenta) for this player, until switched off or the debug draw timeout.</summary>
        public bool Terminal_DrawArea
        {
            get { return drawArea; }
            set
            {
                drawArea = value;
                drawStart = MyAPIGateway.Session.GameplayFrameCounter;
                if (AutomataSession.Instance != null) AutomataSession.Instance.RequestDraw(this, value);
            }
        }
        public int Terminal_ScanInterval { get { return settings.ScanIntervalSeconds; } set { settings.ScanIntervalSeconds = value; SettingsEdited(); } }
        public int Terminal_BlocksPerJob { get { return settings.BlocksPerJob; } set { settings.BlocksPerJob = value; SettingsEdited(); } }
        public Color Terminal_GrindColor { get { return new Color(settings.GrindColor); } set { settings.GrindColor = value.PackedValue; SettingsEdited(); } }
        public Color Terminal_WeldIgnoreColor { get { return new Color(settings.WeldIgnoreColor); } set { settings.WeldIgnoreColor = value.PackedValue; SettingsEdited(); } }
        #endregion
            #region Tagged LCDs
        private readonly BlockLcd lcd = new BlockLcd();
        private readonly List<string> header = new List<string>();

        /// <summary>The nickname as set (empty: logs show the block's name).</summary>
        public string Nickname { get { return settings.Nickname ?? ""; } }

        public string Terminal_Nickname
        {
            set { value = value ?? ""; if (settings.Nickname == value) return; settings.Nickname = value; SettingsEdited(); }
        }

        public LcdStyle Terminal_LcdStyle { get { if (settings.Lcd == null) settings.Lcd = new LcdStyle(); return settings.Lcd; } }
        public void Terminal_LcdEdited() { SettingsEdited(); }

        private static string PriorityCode(WorkPriority p)
        {
            var sb = new StringBuilder(3);
            for (int r = 0; r < 3; r++)
            {
                var c = WorkPriorities.At(p, r);
                sb.Append(c == WorkCategory.Repair ? 'R' : c == WorkCategory.Projected ? 'P' : 'U');
            }
            return sb.ToString();
        }

        // "CC <nickname>", awarded jobs and their tasks, the priority, the last scan's weld blocks by category (in priority
        // order) and grind blocks
        private void BuildHeader()
        {
            int active = 0, tasks = 0;
            for (int j = 0; j < jobs.Count; j++)
            {
                if (jobs[j].Assigned == 0) continue;
                active++;
                var ja = jobs[j].Message != null ? jobs[j].Message.Payload : null;
                tasks += ja != null && ja.Tasks != null ? ja.Tasks.Count : jobs[j].Blocks.Count * 3;
            }
            header.Clear();
            header.Add(Loc.F(StrHeaderTitle, Nicknames.Of(Entity)));
            header.Add(Loc.F(StrHeaderJobs, active, tasks));
            header.Add(Loc.F(StrHeaderPriority, PriorityCode(settings.WorkPriority)));
            header.Add(BlockLcd.Columns(Loc.T(StrHeaderQueuedWeld), Loc.F(StrHeaderQueuedGrind, lastGrind), 32));
            for (int r = 0; r < 3; r++)
            {
                var c = WorkPriorities.At(settings.WorkPriority, r);
                string label = Loc.T(c == WorkCategory.Repair ? StrCatRepair : c == WorkCategory.Projected ? StrCatProjected : StrCatUnbuilt);
                header.Add(Loc.F(StrHeaderCategory, r + 1, label.PadRight(11), lastCategory[(int)c]));
            }
        }

        // Server: the status text on the tagged LCDs
        private void UpdateLcd()
        {
            if (!lcd.Wanted(block, settings.LcdTag)) return;   // nobody looks at an LCD: nothing is built
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
