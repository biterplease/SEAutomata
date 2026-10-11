using Sandbox.ModAPI;
using System;
using System.Linq;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Utils;
using VRageMath;

using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Config
{
    [Flags]
    public enum LogLevel : byte
    {
        Quiet = 0,
        Verbose = 1 ,
        Info = 2,
        Debug = 4,
        Warning = 8,
        Error = 16
    }

    /// <summary>
    /// Serialization mode for the virtual-network message queue.
    /// This is how drones, orchestrators and logistics computers communicate with each other.
    /// </summary>
    public enum MessageSerializationMode : byte
    {
        /// <summary>
        /// XML serialization is slow, use only for debugging.
        /// </summary>
        XML,

        /// <summary>
        /// ProtoBuf serialization, faster, no visibility in-flight.
        /// </summary>
        ProtoBuf
    }

    

    public class ServerConfig
    {
        #region UI strings
        private static readonly MyStringId StrNoConfig = MyStringId.GetOrCompute("No config file found, using defaults");
        private static readonly MyStringId StrConfigLoaded = MyStringId.GetOrCompute("Server configuration loaded successfully");
        private static readonly MyStringId StrConfigError = MyStringId.GetOrCompute("Error loading config: {0}");
        private static readonly MyStringId StrConfigParseFailed = MyStringId.GetOrCompute("Failed to parse config file - using defaults");
        private static readonly MyStringId StrSummaryHeader = MyStringId.GetOrCompute("=== Automata Server Config ===");
        private static readonly MyStringId StrSummarySchedPlayer = MyStringId.GetOrCompute("Schedulers - Max per Player: {0}");
        private static readonly MyStringId StrSummarySchedFaction = MyStringId.GetOrCompute("Schedulers - Max per Faction: {0}");
        private static readonly MyStringId StrSummaryDronePlayer = MyStringId.GetOrCompute("Drone Controllers - Max per Player: {0}");
        private static readonly MyStringId StrSummaryDroneFaction = MyStringId.GetOrCompute("Drone Controllers - Max per Faction: {0}");
        private static readonly MyStringId StrSummaryLogistics = MyStringId.GetOrCompute("Logistics: {0}");
        private static readonly MyStringId StrUnlimited = MyStringId.GetOrCompute("Unlimited");
        private static readonly MyStringId StrEnabled = MyStringId.GetOrCompute("Enabled");
        private static readonly MyStringId StrDisabled = MyStringId.GetOrCompute("Disabled");
        #endregion

        private static ServerConfig _instance;
        public static ServerConfig Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new ServerConfig();
                return _instance;
            }
        }

        private static bool _configLoaded = false;
        private static readonly string CONFIG_FILENAME = "Automata.ini";

        public SessionConfig Session { get; private set; }
        public BlockLimitConfig BlockLimits { get; private set; }
        public PathfindingConfig Pathfinding { get; private set; }
        public DroneControllerBlockConfig Drone { get; private set; }
        public MessageQueueConfig MessageQueue { get; private set; }
        public LoggingConfig Logging { get; private set; }
        public OrchestratorBlockConfig OrchestratorServerSettings { get; private set; }
        public LogisticsComputerConfig LogisticsComputer { get; private set; }
        public ConstructionComputerConfig ConstructionComputer { get; private set; }
        public MiningConfig Mining { get; private set; }
        public ScoutPointerConfig ScoutPointer { get; private set; }

        public ServerConfig()
        {
            // Initialize all nested config objects
            Session = new SessionConfig();
            BlockLimits = new BlockLimitConfig();
            Pathfinding = new PathfindingConfig();
            Drone = new DroneControllerBlockConfig();
            OrchestratorServerSettings = new OrchestratorBlockConfig();
            MessageQueue = new MessageQueueConfig();
            Logging = new LoggingConfig();
            LogisticsComputer = new LogisticsComputerConfig();
            ConstructionComputer = new ConstructionComputerConfig();
            Mining = new MiningConfig();
            ScoutPointer = new ScoutPointerConfig();
        }

        public class SessionConfig
        {
            public int UpdateInterval { get; internal set; } = 100;
            public bool EnableLogistics { get; internal set; } = true;
            public bool EnableWeldTasks { get; internal set; } = true;
            public bool EnableGrindTasks { get; internal set; } = true;
        }

        public class LogisticsComputerConfig
        {
            /// <summary>
            /// If disabled, logistics will not be available, and the only operation mode available
            /// will be ProvideForConstruction.
            /// </summary>
            public bool AllowLogistics { get; internal set; } = true;
            public bool AllowNetworkPush { get; internal set; } = true;
            public bool AllowNetworkRequests { get; internal set; } = true;
            /// <summary>
            /// If push is enabled, minimum allowed value for block. Default 10s.
            /// </summary>
            public int MinPushFrequencyTicks { get; internal set; } = 600;
            /// <summary>
            /// If push is enabled, maximum allowed value for block. Default 30 min.
            /// </summary>
            public int MaxPushFrequencyTicks { get; internal set; } = TimeUtil.TimeSpanToTick(new TimeSpan(0, 30, 0)); // 30 minutes
        }

        public class BlockLimitConfig
        {
            public int MaxSchedulersPerPlayer { get; internal set; } = 1;
            public int MaxSchedulersPerFaction { get; internal set; } = 5;
            public int MaxDroneControllersPerPlayer { get; internal set; } = 10;
            public int MaxDroneControllersPerFaction { get; internal set; } = 50;
        }


        public class DroneControllerBlockConfig
        {
            public bool DefaultMonitorHydrogen { get; internal set; } = false;
            public bool DefaultMonitorBattery { get; internal set; } = false;
            public float DefaultHydrogenRefuelThreshold { get; internal set; } = 25.0f;
            public float DefaultHydrogenOperationalThreshold { get; internal set; } = 50.0f;
            public float DefaultBatteryRefuelThreshold { get; internal set; } = 20.0f;
            public float DefaultBatteryOperationalThreshold { get; internal set; } = 40.0f;
            public float MinPowerThreshold { get; internal set; } = 5.0f;
            public float MaxPowerThreshold { get; internal set; } = 95.0f;
            /// <summary>
            /// Minimum number of ticks between drone power checks. Maximum is clamped to min*10.
            /// </summary>
            public uint PowerCheckIntervalMinSeconds { get; private set; } = 5;
            /// <summary>
            /// Full rescan of the drone's blocks (seconds). Adding / removing blocks triggers a rescan anyway.
            /// </summary>
            public uint ComponentCheckIntervalMinSeconds {get; internal set;} = 600;
            /// <summary>
            /// How often a drone's connector / beacon list may refresh on its own (seconds). Lists are cached per drone.
            /// </summary>
            public int ConnectorScanIntervalSeconds { get; internal set; } = 60;
            /// <summary>
            /// A job task fails after this many seconds (navigation: this long without getting closer). Any mode.
            /// </summary>
            public int TaskTimeoutSeconds { get; internal set; } = 30;
            /// <summary>Combat handover: enemies the AI Defensive block reports farther than this (m) from the drone are ignored (vanilla block limit 2000; modded blocks can see further).</summary>
            public int CombatMaxDetectionRange { get; internal set; } = 2000;
            /// <summary>
            /// Debug overlays (observation area, navigation targets) switch themselves off after this many seconds.
            /// Sent to clients (they draw); they use the default until the server has answered.
            /// </summary>
            public int DebugDrawTimeoutSeconds { get; internal set; } = 600;
            /// <summary>
            /// Automatic refresh only runs if the drone moved at least this far since the last scan (metres)...
            /// </summary>
            public double ConnectorScanMinMoveMeters { get; internal set; } = 250;
            /// <summary>
            /// ...or the list is older than this (seconds), to pick up newly built connectors.
            /// </summary>
            public int ConnectorScanMaxAgeSeconds { get; internal set; } = 600;
            /// <summary>
            /// Minimum time between player-initiated scans ("Scan" button), per drone (seconds).
            /// </summary>
            public int ConnectorManualScanIntervalSeconds { get; internal set; } = 60;
            /// <summary>
            /// Scan radius around the drone controller for the automatic list (metres). Only the drone owner's and
            /// the owner's faction's connectors and beacons are ever listed.
            /// </summary>
            public double ConnectorScanRadius { get; internal set; } = 1000;
            /// <summary>
            /// "Add by name" search radius, and the furthest a selected home connector / anchor may be (metres).
            /// </summary>
            public double ConnectorNameSearchRadius { get; internal set; } = 15000;
            /// <summary>
            /// Minimum time between "Add by name" searches, per drone (seconds).
            /// </summary>
            public int ConnectorNameSearchIntervalSeconds { get; internal set; } = 10;
        }

        public class ConstructionComputerConfig{
            /// <summary>
            /// Max number of block targets when scanning for construction tasks.
            /// </summary>
            public byte BlockLimitPerScan { get; internal set; } = 100;
             /// <summary>
            /// The Construction Computer will perform a cleanup of orphaned tasks that are no longer in its range.
            /// For whatever reason. Default is 30 minutes.
            /// </summary>
            public int MaintenanceIntervalSeconds { get; internal set; } = 30; // 30 seconds
            public int MinScanRetryIntervalSeconds { get; internal set; } = 10; // 10 seconds
            public int MaxScanRetryIntervalSeconds { get; internal set; } = 3600; // 3600 seconds

            public int MaxJobAnnouncementsPerUpdate { get; internal set; } = 10;
            /// <summary>
            /// Construction computer blocks: jobs published at once (announced and not yet awarded, on hold included).
            /// </summary>
            public int MaxPublishedJobs { get; internal set; } = 10;
            /// <summary>
            /// Stand-alone drones: construction jobs taken per scan of the observation area (5x5x5 = 125).
            /// </summary>
            public int DroneMaxJobsPerScan { get; internal set; } = 125;
            /// <summary>Stand-alone drones: seconds between observation-area scans while idle.</summary>
            public int DroneScanIntervalSeconds { get; internal set; } = 30;

        }

        /// <summary>Mining surveyors and ore scans (drone survey, job site checks).</summary>
        public class MiningConfig
        {
            /// <summary>Voxel cells (32 m) one ore scan reads per update (every 10 ticks): the CPU cap of scanning.</summary>
            public int ScanCellsPerUpdate { get; internal set; } = 8;
            /// <summary>Ore scans cover at most this radius, whatever the ore detector's range.</summary>
            public int MaxScanRadius { get; internal set; } = 300;
            /// <summary>Same ore found within this distance of a known deposit: the same deposit.</summary>
            public int DepositMergeMeters { get; internal set; } = 50;
            /// <summary>Deposits a surveyor keeps (the farthest go first).</summary>
            public int MaxDepositsPerSurveyor { get; internal set; } = 500;
            /// <summary>MineOre jobs a surveyor has published (not awarded yet) at once.</summary>
            public int MaxPublishedJobs { get; internal set; } = 5;
            /// <summary>Seconds between a surveyor's scans with its own ore detector.</summary>
            public int SurveyorScanIntervalSeconds { get; internal set; } = 120;
            /// <summary>Seconds between a surveying drone's scans (it must also have moved half a scan radius).</summary>
            public int DroneSurveyIntervalSeconds { get; internal set; } = 10;
            /// <summary>Temporary GPS markers a surveyor shows (nearest deposits first).</summary>
            public int MaxGpsMarkers { get; internal set; } = 30;
        }

        /// <summary>Scout pointers (TODO/11): the jobs players order with them, and their GPS markers.</summary>
        public class ScoutPointerConfig
        {
            /// <summary>Industrial designator order marker while the orchestrator hasn't confirmed it (yellow).</summary>
            public Color PendingGpsColor { get; internal set; } = new Color(255, 210, 0);
            /// <summary>Marker once every job of the order has a drone (green).</summary>
            public Color ConfirmedGpsColor { get; internal set; } = new Color(40, 220, 60);
            /// <summary>Hostile designator markers (combat jobs, to come) (dark red).</summary>
            public Color CombatGpsColor { get; internal set; } = new Color(139, 0, 0);
            /// <summary>An order whose jobs don't all have a drone this long (seconds) is cancelled and its marker removed.</summary>
            public int ConfirmTimeoutSeconds { get; internal set; } = 10;
        }

        /// <summary>"R,G,B" (0-255) or "#RRGGBB"; anything else: the default.</summary>
        private static Color ParseColor(string text, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            var hex = System.Globalization.NumberStyles.HexNumber;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int r, g, b;
            if (text.StartsWith("#") && text.Length == 7
                && int.TryParse(text.Substring(1, 2), hex, inv, out r) && int.TryParse(text.Substring(3, 2), hex, inv, out g)
                && int.TryParse(text.Substring(5, 2), hex, inv, out b))
                return new Color(r, g, b);
            var parts = text.Split(',');
            if (parts.Length == 3 && int.TryParse(parts[0].Trim(), out r) && int.TryParse(parts[1].Trim(), out g) && int.TryParse(parts[2].Trim(), out b))
                return new Color(MathHelper.Clamp(r, 0, 255), MathHelper.Clamp(g, 0, 255), MathHelper.Clamp(b, 0, 255));
            Log.Warning("Config: colour '{0}' not understood (R,G,B or #RRGGBB), default used", text);
            return fallback;
        }

        public class OrchestratorBlockConfig
        {
            /// <summary>
            /// Maximum number of job targets when ran by a drone.
            /// </summary>
            public int StandAloneOrchestratorMaxTargetLimit { get; internal set; } = 10;
            public int OrchestratorMaxTargetLimit { get; internal set; } = 100;
            public int MaxTaskAssignmentPerBatch { get; internal set; } = 10;

            public int MaxBidRoundExpirationSeconds { get; internal set; } = 30;

            /// <summary>Auctions open at once; further job announcements wait in the orchestrator's queue.</summary>
            public int MaxConcurrentAuctions { get; internal set; } = 10;
            /// <summary>An auction without a single bid this long: JOB_ANNOUNCEMENT_FAILED to the announcer, dropped.</summary>
            public int NoBidTimeoutSeconds { get; internal set; } = 60;
            /// <summary>How long an announcer holds a job whose auction failed (sent with the failure).</summary>
            public int FailedJobHoldSeconds { get; internal set; } = 60;

            /// <summary>
            /// Update intervals different for every state
            /// </summary>
            public class StateUpdateIntervalTicksConfig
            {
                public int Initializing { get; internal set; } = 600;
                public int Error { get; internal set; } = 600;
                public int Standby { get; internal set; } = 600;
                public int Scanning { get; internal set; } = 60;
                public int Assigning { get; internal set; } = 60;
            }

            public StateUpdateIntervalTicksConfig StateUpdateIntervalTicks { get; internal set; } = new StateUpdateIntervalTicksConfig();

            /// <summary>
            /// When on standby, wait ScanDelayTicks before scanning again.
            /// </summary>
            public int ScanDelayTicks { get; internal set; } = 600;
            public int ErrorRecoveryIntervalTicks { get; internal set; } = 300;
            public int MaxConsecutiveErrors { get; internal set; } = 3;
            /// <summary>
            /// The scheduler will perform a cleanup of orphaned drones that are no longer in its range.
            /// For whatever reason. Default is 5 minutes.
            /// </summary>
            public int ManintenanceIntervalTicks { get; internal set; } = 300; // 30 seconds
            /// <summary>
            /// Gameplay frames an orchestrator collects bids before it assigns (longer while pooling drones for a big job).
            /// </summary>
            public int BidCollectionWindowTicks { get; internal set; } = 300;
            /// <summary>
            /// Gameplay frames a construction computer holds back a block after a drone aborted / declined it
            /// (Unreachable: 5 min, fixed).
            /// </summary>
            public int BidBlacklistCooldownTicks { get; internal set; } = 3600;
        }

        public class LoggingConfig
        {
            // Server log (Automata.log). Debug on by default while the network layer is being tested.
            public LogLevel LogLevel { get; internal set; } = LogLevel.Info|LogLevel.Debug|LogLevel.Warning|LogLevel.Error;
            public bool LogPathfinding { get; internal set; } = false;
            public bool LogDroneNetwork { get; internal set; } = true;
            public bool LogDroneOrders { get; internal set; } = true;
        }

        public void LoadConfig()
        {
            if (_configLoaded) return;
            _configLoaded = true;

            try
            {
                if (!MyAPIGateway.Session.IsServer)
                {
                    Log.Info("Config loading skipped - not server");
                    return;
                }

                Log.Info("Loading server configuration...");

                // Try to read the config file from mod storage
                var configText = LoadConfigFile();
                if (string.IsNullOrEmpty(configText))
                {
                    MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(StrNoConfig));
                    return;
                }

                ParseConfig(configText);

                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(StrConfigLoaded));
            }
            catch (Exception ex)
            {
                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.F(StrConfigError, ex.Message));
                MyLog.Default.WriteLine($"{AutomataSession.MOD_NAME}: LoadConfig exception: {ex}");
            }
        }

        private string LoadConfigFile()
        {
            try
            {
                // Get the current mod item
                var modItem = MyAPIGateway.Session.Mods.FirstOrDefault(m => m.GetPath().Contains("Automata"));
                if (modItem.Name != AutomataSession.MOD_NAME)
                {
                    Log.Warning($"{AutomataSession.MOD_NAME}: Could not find mod item for config loading");
                    return null;
                }

                if (MyAPIGateway.Utilities.FileExistsInModLocation(CONFIG_FILENAME, modItem))
                {
                    using (var reader = MyAPIGateway.Utilities.ReadFileInModLocation(CONFIG_FILENAME, modItem))
                    {
                        return reader.ReadToEnd();
                    }
                }

                // Fallback: try to read from world storage
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(CONFIG_FILENAME, typeof(AutomataSession)))
                {
                    using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(CONFIG_FILENAME, typeof(AutomataSession)))
                    {
                        return reader.ReadToEnd();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"{AutomataSession.MOD_NAME}: LoadConfigFile exception: {ex}");
                return null;
            }
        }

        private static int ReadIniIntPreferSections(MyIni ini, string primary, string legacy, string key, int defaultValue)
        {
            MyIniValue primaryVal = ini.Get(primary, key);
            if (!string.IsNullOrEmpty(primaryVal.ToString()))
                return primaryVal.ToInt32(defaultValue);
            return ini.Get(legacy, key).ToInt32(defaultValue);
        }

        private static int ReadIniSchedulerStateInterval(
            MyIni ini,
            string stateSection,
            string legacySection,
            string nestedKey,
            string legacyLongKey,
            int defaultValue)
        {
            MyIniValue v = ini.Get(stateSection, nestedKey);
            if (!string.IsNullOrEmpty(v.ToString()))
                return v.ToInt32(defaultValue);
            return ini.Get(legacySection, legacyLongKey).ToInt32(defaultValue);
        }

        private void ParseConfig(string configText)
        {
            var ini = new MyIni();
            if (!ini.TryParse(configText))
            {
                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(StrConfigParseFailed));
                return;
            }

            // Parse Session section
            Session.UpdateInterval = Math.Max(1, ini.Get("Session", "UpdateInterval").ToInt32(Session.UpdateInterval));
            Session.EnableLogistics = ini.Get("Session", "EnableLogistics").ToBoolean(Session.EnableLogistics);
            Session.EnableWeldTasks = ini.Get("Session", "EnableWeldTasks").ToBoolean(Session.EnableWeldTasks);
            Session.EnableGrindTasks = ini.Get("Session", "EnableGrindTasks").ToBoolean(Session.EnableGrindTasks);

            // Parse Limits section
            BlockLimits.MaxSchedulersPerPlayer = Math.Max(0, ini.Get("Limits", "MaxSchedulersPerPlayer").ToInt32(BlockLimits.MaxSchedulersPerPlayer));
            BlockLimits.MaxSchedulersPerFaction = Math.Max(0, ini.Get("Limits", "MaxSchedulersPerFaction").ToInt32(BlockLimits.MaxSchedulersPerFaction));
            BlockLimits.MaxDroneControllersPerPlayer = Math.Max(0, ini.Get("Limits", "MaxDroneControllersPerPlayer").ToInt32(BlockLimits.MaxDroneControllersPerPlayer));
            BlockLimits.MaxDroneControllersPerFaction = Math.Max(0, ini.Get("Limits", "MaxDroneControllersPerFaction").ToInt32(BlockLimits.MaxDroneControllersPerFaction));

            // Parse Pathfinding section
            Pathfinding.allowDirectPathfinding = ini.Get("Pathfinding", "AllowDirectPathfinding").ToBoolean(Pathfinding.allowDirectPathfinding);
            Pathfinding.allowAStar = ini.Get("Pathfinding", "AllowAStar").ToBoolean(Pathfinding.allowAStar);
            //Pathfinding.allowDStarLite = ini.Get("Pathfinding", "AllowDStarLite").ToBoolean(Pathfinding.AllowDStarLite);
            Pathfinding.usePlanetAwarePathfinding = ini.Get("Pathfinding", "UsePlanetAwarePathfinding").ToBoolean(Pathfinding.usePlanetAwarePathfinding);
            Pathfinding.minWaypointDistance = (float)Math.Max(1.0, ini.Get("Pathfinding", "MinWaypointDistance").ToDouble(Pathfinding.minWaypointDistance));
            Pathfinding.maxWaypointDistance = (float)Math.Max(Pathfinding.minWaypointDistance, ini.Get("Pathfinding", "MaxWaypointDistance").ToDouble(Pathfinding.maxWaypointDistance));
            Pathfinding.maxRepositionAttempts = Math.Max(1, ini.Get("Pathfinding", "MaxRepositionAttempts").ToInt32(Pathfinding.maxRepositionAttempts));
            Pathfinding.maxPathNodes = Math.Max(100, ini.Get("Pathfinding", "MaxPathNodes").ToInt32(Pathfinding.maxPathNodes));
            var maxPathfindingMs = Math.Max(10, ini.Get("Pathfinding", "MaxPathfindingTimeMs").ToInt32(50));
            Pathfinding.maxPathfindingTime = TimeSpan.FromMilliseconds(maxPathfindingMs);
            Pathfinding.requireSensorsForPathfinding = ini.Get("Pathfinding", "RequireSensorsForPathfinding").ToBoolean(Pathfinding.requireSensorsForPathfinding);
            Pathfinding.requireCamerasForPathfinding = ini.Get("Pathfinding", "RequireCamerasForPathfinding").ToBoolean(Pathfinding.requireCamerasForPathfinding);
            Pathfinding.maxSimulatedCameraRaycastMeters = (float)MathHelper.Clamp(ini.Get("Pathfinding", "CameraRange").ToDouble(Pathfinding.maxSimulatedCameraRaycastMeters), 10.0, 5000.0);
            Pathfinding.SimulatedSensorRange = MathHelper.Clamp(ini.Get("Pathfinding", "SimulatedSensorRange").ToDouble(Pathfinding.SimulatedSensorRange), 1.0, 500.0);
            Pathfinding.minAltitudeBuffer = (float)MathHelper.Clamp(ini.Get("Pathfinding", "MinAltitudeBuffer").ToDouble(Pathfinding.minAltitudeBuffer), 0.0, 1000.0);
            Pathfinding.RaysPerTick = MathHelper.Clamp(ini.Get("Pathfinding", "RaysPerTick").ToInt32(Pathfinding.RaysPerTick), 5, 1000);
            Pathfinding.allowRepathing = ini.Get("Pathfinding", "AllowRepathing").ToBoolean(Pathfinding.allowRepathing);
            Pathfinding.ObstacleClearance = MathHelper.Clamp(ini.Get("Pathfinding", "ObstacleClearance").ToDouble(Pathfinding.ObstacleClearance), 0.0, 20.0);
            Pathfinding.ArcMinDistance = MathHelper.Clamp(ini.Get("Pathfinding", "ArcMinDistance").ToDouble(Pathfinding.ArcMinDistance), 50.0, 100000.0);
            Pathfinding.ArcStep = MathHelper.Clamp(ini.Get("Pathfinding", "ArcStep").ToDouble(Pathfinding.ArcStep), 25.0, 10000.0);
            Pathfinding.CruiseAltitude = MathHelper.Clamp(ini.Get("Pathfinding", "CruiseAltitude").ToDouble(Pathfinding.CruiseAltitude), 0.0, 1000.0);
            Pathfinding.ArcSag = MathHelper.Clamp(ini.Get("Pathfinding", "ArcSag").ToDouble(Pathfinding.ArcSag), 1.0, 500.0);
            Pathfinding.DockClearance = MathHelper.Clamp(ini.Get("Pathfinding", "DockClearance").ToDouble(Pathfinding.DockClearance), 0.1, 3.0);
            Pathfinding.DockClearanceMin = MathHelper.Clamp(ini.Get("Pathfinding", "DockClearanceMin").ToDouble(Pathfinding.DockClearanceMin), 0.05, 3.0);
            Pathfinding.DockCorridorMaxLength = MathHelper.Clamp(ini.Get("Pathfinding", "DockCorridorMaxLength").ToDouble(Pathfinding.DockCorridorMaxLength), 10.0, 200.0);

            // Parse SchedulerBounds (sample Automata.ini); [Scheduler] kept as legacy alias for same keys.
            const string schedBounds = "SchedulerBounds";
            const string schedLegacy = "Scheduler";
            const string schedState = "SchedulerBounds.StateUpdateIntervalTicks";
            OrchestratorServerSettings.OrchestratorMaxTargetLimit = Math.Max(1, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaxTargetLimit", OrchestratorServerSettings.OrchestratorMaxTargetLimit));
            OrchestratorServerSettings.MaxTaskAssignmentPerBatch = Math.Max(1, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaxTaskAssignmentPerBatch", OrchestratorServerSettings.MaxTaskAssignmentPerBatch));
            OrchestratorServerSettings.ScanDelayTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "ScanDelayTicks", OrchestratorServerSettings.ScanDelayTicks));
            OrchestratorServerSettings.ErrorRecoveryIntervalTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "ErrorRecoveryIntervalTicks", OrchestratorServerSettings.ErrorRecoveryIntervalTicks));
            OrchestratorServerSettings.MaxConsecutiveErrors = Math.Max(1, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaxConsecutiveErrors", OrchestratorServerSettings.MaxConsecutiveErrors));
            OrchestratorServerSettings.ManintenanceIntervalTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaintenanceIntervalTicks", OrchestratorServerSettings.ManintenanceIntervalTicks));
            OrchestratorServerSettings.BidCollectionWindowTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "BidCollectionWindowTicks", OrchestratorServerSettings.BidCollectionWindowTicks));
            OrchestratorServerSettings.BidBlacklistCooldownTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "BidBlacklistCooldownTicks", OrchestratorServerSettings.BidBlacklistCooldownTicks));

            OrchestratorServerSettings.StateUpdateIntervalTicks.Initializing = Math.Max(60, ReadIniSchedulerStateInterval(ini, schedState, schedLegacy, "Initializing", "InitializingUpdateInterval", OrchestratorServerSettings.StateUpdateIntervalTicks.Initializing));
            OrchestratorServerSettings.StateUpdateIntervalTicks.Error = Math.Max(60, ReadIniSchedulerStateInterval(ini, schedState, schedLegacy, "Error", "ErrorUpdateInterval", OrchestratorServerSettings.StateUpdateIntervalTicks.Error));
            OrchestratorServerSettings.StateUpdateIntervalTicks.Standby = Math.Max(60, ReadIniSchedulerStateInterval(ini, schedState, schedLegacy, "Standby", "StandbyUpdateInterval", OrchestratorServerSettings.StateUpdateIntervalTicks.Standby));
            OrchestratorServerSettings.StateUpdateIntervalTicks.Scanning = Math.Max(10, ReadIniSchedulerStateInterval(ini, schedState, schedLegacy, "Scanning", "ScanningUpdateInterval", OrchestratorServerSettings.StateUpdateIntervalTicks.Scanning));
            OrchestratorServerSettings.StateUpdateIntervalTicks.Assigning = Math.Max(10, ReadIniSchedulerStateInterval(ini, schedState, schedLegacy, "Assigning", "AssigningUpdateInterval", OrchestratorServerSettings.StateUpdateIntervalTicks.Assigning));

            // Parse Drone section
            Drone.DefaultMonitorHydrogen = ini.Get("Drone", "DefaultMonitorHydrogen").ToBoolean(Drone.DefaultMonitorHydrogen);
            Drone.DefaultMonitorBattery = ini.Get("Drone", "DefaultMonitorBattery").ToBoolean(Drone.DefaultMonitorBattery);
            Drone.DefaultHydrogenRefuelThreshold = MathHelper.Clamp(ini.Get("Drone", "DefaultHydrogenRefuelThreshold").ToSingle(Drone.DefaultHydrogenRefuelThreshold), 5.0f, 95.0f);
            Drone.DefaultHydrogenOperationalThreshold = MathHelper.Clamp(ini.Get("Drone", "DefaultHydrogenOperationalThreshold").ToSingle(Drone.DefaultHydrogenOperationalThreshold), Drone.DefaultHydrogenRefuelThreshold + 5.0f, 95.0f);
            Drone.DefaultBatteryRefuelThreshold = MathHelper.Clamp(ini.Get("Drone", "DefaultBatteryRefuelThreshold").ToSingle(Drone.DefaultBatteryRefuelThreshold), 5.0f, 95.0f);
            Drone.DefaultBatteryOperationalThreshold = MathHelper.Clamp(ini.Get("Drone", "DefaultBatteryOperationalThreshold").ToSingle(Drone.DefaultBatteryOperationalThreshold), Drone.DefaultBatteryRefuelThreshold + 5.0f, 95.0f);
            Drone.MinPowerThreshold = MathHelper.Clamp(ini.Get("Drone", "MinPowerThreshold").ToSingle(Drone.MinPowerThreshold), 1.0f, 50.0f);
            Drone.MaxPowerThreshold = MathHelper.Clamp(ini.Get("Drone", "MaxPowerThreshold").ToSingle(Drone.MaxPowerThreshold), 50.0f, 99.0f);
            Drone.ComponentCheckIntervalMinSeconds = (uint)MathHelper.Clamp(ini.Get("Drone", "ComponentCheckIntervalSeconds").ToInt32((int)Drone.ComponentCheckIntervalMinSeconds), 60, 86400);
            Drone.TaskTimeoutSeconds = MathHelper.Clamp(ini.Get("Drone", "TaskTimeoutSeconds").ToInt32(Drone.TaskTimeoutSeconds), 10, 600);
            Drone.CombatMaxDetectionRange = MathHelper.Clamp(ini.Get("Drone", "CombatMaxDetectionRange").ToInt32(Drone.CombatMaxDetectionRange), 100, 50000);
            Drone.DebugDrawTimeoutSeconds = MathHelper.Clamp(ini.Get("Drone", "DebugDrawTimeoutSeconds").ToInt32(Drone.DebugDrawTimeoutSeconds), 10, 3600);
            Drone.ConnectorScanIntervalSeconds = MathHelper.Clamp(ini.Get("Drone", "ConnectorScanIntervalSeconds").ToInt32(Drone.ConnectorScanIntervalSeconds), 10, 86400);
            Drone.ConnectorScanMinMoveMeters = MathHelper.Clamp(ini.Get("Drone", "ConnectorScanMinMoveMeters").ToDouble(Drone.ConnectorScanMinMoveMeters), 0.0, 10000.0);
            Drone.ConnectorScanMaxAgeSeconds = MathHelper.Clamp(ini.Get("Drone", "ConnectorScanMaxAgeSeconds").ToInt32(Drone.ConnectorScanMaxAgeSeconds), 60, 86400);
            Drone.ConnectorManualScanIntervalSeconds = MathHelper.Clamp(ini.Get("Drone", "ConnectorManualScanIntervalSeconds").ToInt32(Drone.ConnectorManualScanIntervalSeconds), 5, 3600);
            Drone.ConnectorScanRadius = MathHelper.Clamp(ini.Get("Drone", "ConnectorScanRadius").ToDouble(Drone.ConnectorScanRadius), 100.0, 5000.0);
            Drone.ConnectorNameSearchRadius = MathHelper.Clamp(ini.Get("Drone", "ConnectorNameSearchRadius").ToDouble(Drone.ConnectorNameSearchRadius), Drone.ConnectorScanRadius, 50000.0);
            Drone.ConnectorNameSearchIntervalSeconds = MathHelper.Clamp(ini.Get("Drone", "ConnectorNameSearchIntervalSeconds").ToInt32(Drone.ConnectorNameSearchIntervalSeconds), 1, 3600);

            // Parse ConstructionComputer section (stand-alone drone construction)
            ConstructionComputer.DroneMaxJobsPerScan = MathHelper.Clamp(ini.Get("ConstructionComputer", "DroneMaxJobsPerScan").ToInt32(ConstructionComputer.DroneMaxJobsPerScan), 1, 1000);
            ConstructionComputer.DroneScanIntervalSeconds = MathHelper.Clamp(ini.Get("ConstructionComputer", "DroneScanIntervalSeconds").ToInt32(ConstructionComputer.DroneScanIntervalSeconds), 5, 3600);
            ConstructionComputer.MaxPublishedJobs = MathHelper.Clamp(ini.Get("ConstructionComputer", "MaxPublishedJobs").ToInt32(ConstructionComputer.MaxPublishedJobs), 1, 100);

            // Parse Mining section
            Mining.ScanCellsPerUpdate = MathHelper.Clamp(ini.Get("Mining", "ScanCellsPerUpdate").ToInt32(Mining.ScanCellsPerUpdate), 1, 128);
            Mining.MaxScanRadius = MathHelper.Clamp(ini.Get("Mining", "MaxScanRadius").ToInt32(Mining.MaxScanRadius), 50, 2000);
            Mining.DepositMergeMeters = MathHelper.Clamp(ini.Get("Mining", "DepositMergeMeters").ToInt32(Mining.DepositMergeMeters), 10, 500);
            Mining.MaxDepositsPerSurveyor = MathHelper.Clamp(ini.Get("Mining", "MaxDepositsPerSurveyor").ToInt32(Mining.MaxDepositsPerSurveyor), 10, 5000);
            Mining.MaxPublishedJobs = MathHelper.Clamp(ini.Get("Mining", "MaxPublishedJobs").ToInt32(Mining.MaxPublishedJobs), 1, 50);
            Mining.SurveyorScanIntervalSeconds = MathHelper.Clamp(ini.Get("Mining", "SurveyorScanIntervalSeconds").ToInt32(Mining.SurveyorScanIntervalSeconds), 10, 3600);
            Mining.DroneSurveyIntervalSeconds = MathHelper.Clamp(ini.Get("Mining", "DroneSurveyIntervalSeconds").ToInt32(Mining.DroneSurveyIntervalSeconds), 2, 600);
            Mining.MaxGpsMarkers = MathHelper.Clamp(ini.Get("Mining", "MaxGpsMarkers").ToInt32(Mining.MaxGpsMarkers), 1, 200);

            // Parse Orchestrator section (auctions)
            OrchestratorServerSettings.MaxConcurrentAuctions = MathHelper.Clamp(ini.Get("Orchestrator", "MaxConcurrentAuctions").ToInt32(OrchestratorServerSettings.MaxConcurrentAuctions), 1, 100);
            OrchestratorServerSettings.NoBidTimeoutSeconds = MathHelper.Clamp(ini.Get("Orchestrator", "NoBidTimeoutSeconds").ToInt32(OrchestratorServerSettings.NoBidTimeoutSeconds), 10, 600);
            OrchestratorServerSettings.FailedJobHoldSeconds = MathHelper.Clamp(ini.Get("Orchestrator", "FailedJobHoldSeconds").ToInt32(OrchestratorServerSettings.FailedJobHoldSeconds), 10, 7200);

            // Parse ScoutPointer section
            ScoutPointer.PendingGpsColor = ParseColor(ini.Get("ScoutPointer", "PendingGpsColor").ToString(""), ScoutPointer.PendingGpsColor);
            ScoutPointer.ConfirmedGpsColor = ParseColor(ini.Get("ScoutPointer", "ConfirmedGpsColor").ToString(""), ScoutPointer.ConfirmedGpsColor);
            ScoutPointer.CombatGpsColor = ParseColor(ini.Get("ScoutPointer", "CombatGpsColor").ToString(""), ScoutPointer.CombatGpsColor);
            ScoutPointer.ConfirmTimeoutSeconds = MathHelper.Clamp(ini.Get("ScoutPointer", "ConfirmTimeoutSeconds").ToInt32(ScoutPointer.ConfirmTimeoutSeconds), 3, 300);

            // Parse DroneNetwork section
            MessageQueue.networkUpdateRate = Math.Max(1.0f, ini.Get("MessageQueue", "NetworkUpdateRate").ToSingle(MessageQueue.networkUpdateRate));
            MessageQueue.schedulerAntennaCacheUpdateIntervalTicks = Math.Max(60, ini.Get("MessageQueue", "SchedulerAntennaCacheUpdateIntervalTicks").ToInt32(MessageQueue.schedulerAntennaCacheUpdateIntervalTicks));
            MessageQueue.schedulerMessageReadLimit = Math.Max(1, ini.Get("MessageQueue", "SchedulerMessageReadLimit").ToInt32(MessageQueue.schedulerMessageReadLimit));
            MessageQueue.messageRetentionTicks = Math.Max(600, ini.Get("MessageQueue", "MessageRetentionTicks").ToInt32(MessageQueue.messageRetentionTicks));
            MessageQueue.messageCleanupIntervalTicks = Math.Max(60, ini.Get("MessageQueue", "MessageCleanupIntervalTicks").ToInt32(MessageQueue.messageCleanupIntervalTicks));

            var serializationMode = ini.Get("MessageQueue", "MessageSerializationMode").ToString("ProtoBuf");
            MessageSerializationMode mode;
            if (Enum.TryParse<MessageSerializationMode>(serializationMode, true, out mode))
            {
                MessageQueue.messageSerializationMode = mode;
            }

            // Parse LogisticsComputer section
            LogisticsComputer.AllowLogistics = ini.Get("LogisticsComputer", "AllowLogistics").ToBoolean(LogisticsComputer.AllowLogistics);
            LogisticsComputer.AllowNetworkPush = ini.Get("LogisticsComputer", "AllowNetworkPush").ToBoolean(LogisticsComputer.AllowNetworkPush);
            LogisticsComputer.AllowNetworkRequests = ini.Get("LogisticsComputer", "AllowNetworkRequests").ToBoolean(LogisticsComputer.AllowNetworkRequests);
            LogisticsComputer.MinPushFrequencyTicks = Math.Max(60, ini.Get("LogisticsComputer", "MinPushFrequencyTicks").ToInt32(LogisticsComputer.MinPushFrequencyTicks));
            LogisticsComputer.MaxPushFrequencyTicks = Math.Max(LogisticsComputer.MinPushFrequencyTicks, ini.Get("LogisticsComputer", "MaxPushFrequencyTicks").ToInt32(LogisticsComputer.MaxPushFrequencyTicks));

            // Parse Logging section
            Logging.LogLevel = ParseLogLevel(ini.Get("Logging", "LogLevel").ToString("Info|Debug|Warning|Error"));
            Logging.LogPathfinding = ini.Get("Logging", "LogPathfinding").ToBoolean(Logging.LogPathfinding);
            Logging.LogDroneNetwork = ini.Get("Logging", "LogDroneNetwork").ToBoolean(Logging.LogDroneNetwork);
            Logging.LogDroneOrders = ini.Get("Logging", "LogDroneOrders").ToBoolean(Logging.LogDroneOrders);
        }

        public LogLevel ParseLogLevel(string logLevelStr)
        {
            LogLevel level = 0;
            if (logLevelStr.ToLower().Contains("quiet"))
                return LogLevel.Quiet;
            if (logLevelStr.ToLower().Contains("verbose"))
                level |= LogLevel.Verbose;
            if (logLevelStr.ToLower().Contains("info"))
                level |= LogLevel.Info;
            if (logLevelStr.ToLower().Contains("debug"))
                level |= LogLevel.Debug;
            if (logLevelStr.ToLower().Contains("warning"))
                level |= LogLevel.Warning;
            if (logLevelStr.ToLower().Contains("error"))
                level |= LogLevel.Error;
            return level;
        }



        public string GetConfigSummary()
        {
            string unlimited = Loc.T(StrUnlimited);
            var summary = Loc.T(StrSummaryHeader) + "\n";
            summary += Loc.F(StrSummarySchedPlayer, BlockLimits.MaxSchedulersPerPlayer > 0 ? BlockLimits.MaxSchedulersPerPlayer.ToString() : unlimited) + "\n";
            summary += Loc.F(StrSummarySchedFaction, BlockLimits.MaxSchedulersPerFaction > 0 ? BlockLimits.MaxSchedulersPerFaction.ToString() : unlimited) + "\n";
            summary += Loc.F(StrSummaryDronePlayer, BlockLimits.MaxDroneControllersPerPlayer > 0 ? BlockLimits.MaxDroneControllersPerPlayer.ToString() : unlimited) + "\n";
            summary += Loc.F(StrSummaryDroneFaction, BlockLimits.MaxDroneControllersPerFaction > 0 ? BlockLimits.MaxDroneControllersPerFaction.ToString() : unlimited) + "\n";
            // summary += $"Current Schedulers: {AutomataSession.Instance?.AIDroneSchedulers?.Count ?? 0}\n";
            // summary += $"Current Drone Controllers: {AutomataSession.Instance?.AIDroneControllers?.Count ?? 0}\n";
            summary += Loc.F(StrSummaryLogistics, Loc.T(LogisticsComputer.AllowLogistics ? StrEnabled : StrDisabled)) + "\n";
            return summary;
        }
    }
}