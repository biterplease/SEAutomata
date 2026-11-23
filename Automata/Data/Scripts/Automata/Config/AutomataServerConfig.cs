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
using Automata.Util.Strings;

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

    public class ServerConfig
    {

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
        private static readonly string CONFIG_FILENAME = "Automata.ini";                // single player / a player hosting
        private static readonly string SERVER_CONFIG_FILENAME = "Automata.server.ini";  // dedicated servers (tighter limits)

        public PathfindingConfig Pathfinding { get; private set; }
        public DroneControllerBlockConfig Drone { get; private set; }
        public MessageQueueConfig MessageQueue { get; private set; }
        public LoggingConfig Logging { get; private set; }
        public OrchestratorBlockConfig OrchestratorServerSettings { get; private set; }
        public LogisticsComputerConfig LogisticsComputer { get; private set; }
        public ConstructionComputerConfig ConstructionComputer { get; private set; }
        public MiningConfig Mining { get; private set; }

        public ServerConfig()
        {
            // Initialize all nested config objects
            Pathfinding = new PathfindingConfig();
            Drone = new DroneControllerBlockConfig();
            OrchestratorServerSettings = new OrchestratorBlockConfig();
            MessageQueue = new MessageQueueConfig();
            Logging = new LoggingConfig();
            LogisticsComputer = new LogisticsComputerConfig();
            ConstructionComputer = new ConstructionComputerConfig();
            Mining = new MiningConfig();
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



        public class DroneControllerBlockConfig
        {
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
            public int TaskTimeoutSeconds { get; internal set; } = 60;
            /// <summary>
            /// Combat handover: enemies the AI Defensive block reports farther than this (m) from the drone are ignored (vanilla block limit 2000; modded blocks can see further).
            /// Modify if you're using longer ranges.
            /// </summary>
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
            /// The Construction Computer will perform a cleanup of orphaned tasks that are no longer in its range.
            /// For whatever reason. Default is 30 minutes.
            /// </summary>
            public int MinScanRetryIntervalSeconds { get; internal set; } = 10; // 10 seconds
            public int MaxScanRetryIntervalSeconds { get; internal set; } = 3600; // 3600 seconds

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

        public class OrchestratorBlockConfig
        {
            /// <summary>Auctions open at once; further job announcements wait in the orchestrator's queue.</summary>
            public int MaxConcurrentAuctions { get; internal set; } = 10;
            /// <summary>An auction without a single bid this long: JOB_ANNOUNCEMENT_FAILED to the announcer, dropped.</summary>
            public int NoBidTimeoutSeconds { get; internal set; } = 60;
            /// <summary>How long an announcer holds a job whose auction failed (sent with the failure).</summary>
            public int FailedJobHoldSeconds { get; internal set; } = 60;


        }

        public class LoggingConfig
        {
            // Server log (Automata.log). Debug on by default while the network layer is being tested.
            public LogLevel LogLevel { get; internal set; } = LogLevel.Info|LogLevel.Debug|LogLevel.Warning|LogLevel.Error;
        }

        private VRage.Game.ModAPI.IMyModContext _modContext;

        /// <summary>Reads the config from the mod's folder ('modContext': the session component's, so a workshop copy is found too):
        /// Automata.server.ini on a dedicated server (Automata.ini when it has none), Automata.ini otherwise.</summary>
        public void LoadConfig(VRage.Game.ModAPI.IMyModContext modContext = null)
        {
            if (_configLoaded) return;
            _configLoaded = true;
            _modContext = modContext;

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
                    MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(MyStrings.Common_NoConfigFileFoundUsing));
                    return;
                }

                ParseConfig(configText);

                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(MyStrings.Common_ServerConfigurationLoadedSuccessfully));
            }
            catch (Exception ex)
            {
                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.F(MyStrings.Common_ErrorLoadingConfig, ex.Message));
                MyLog.Default.WriteLine($"{AutomataSession.MOD_NAME}: LoadConfig exception: {ex}");
            }
        }

        private string LoadConfigFile()
        {
            try
            {
                // This mod's item: from its own context (a workshop copy's folder and name are its id, "3817483229.sbm"),
                // else the local copy by name
                VRage.Game.MyObjectBuilder_Checkpoint.ModItem modItem;
                if (_modContext != null && !_modContext.IsBaseGame) modItem = _modContext.ModItem;
                else
                {
                    modItem = MyAPIGateway.Session.Mods.FirstOrDefault(m => m.Name == AutomataSession.MOD_NAME || m.FriendlyName == AutomataSession.MOD_NAME);
                    if (modItem.Name == null)
                    {
                        Log.Warning($"{AutomataSession.MOD_NAME}: Could not find mod item for config loading");
                        return null;
                    }
                }

                if (MyAPIGateway.Utilities.IsDedicated)
                {
                    string text = ReadConfigFile(SERVER_CONFIG_FILENAME, modItem);
                    if (text != null) return text;
                }
                return ReadConfigFile(CONFIG_FILENAME, modItem);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"{AutomataSession.MOD_NAME}: LoadConfigFile exception: {ex}");
                return null;
            }
        }

        /// <summary>'name' from the mod's folder, else from the world's storage; null when neither has it.</summary>
        private static string ReadConfigFile(string name, VRage.Game.MyObjectBuilder_Checkpoint.ModItem modItem)
        {
            if (MyAPIGateway.Utilities.FileExistsInModLocation(name, modItem))
            {
                using (var reader = MyAPIGateway.Utilities.ReadFileInModLocation(name, modItem))
                {
                    Log.Info($"Config: {name} (mod folder)");
                    return reader.ReadToEnd();
                }
            }

            // Fallback: try to read from world storage
            if (MyAPIGateway.Utilities.FileExistsInWorldStorage(name, typeof(AutomataSession)))
            {
                using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(name, typeof(AutomataSession)))
                {
                    Log.Info($"Config: {name} (world storage)");
                    return reader.ReadToEnd();
                }
            }
            return null;
        }

        private void ParseConfig(string configText)
        {
            var ini = new MyIni();
            if (!ini.TryParse(configText))
            {
                MyAPIGateway.Utilities.ShowMessage(AutomataSession.MOD_NAME, Loc.T(MyStrings.Common_FailedToParseConfigFile));
                return;
            }

            // Parse Pathfinding section
            Pathfinding.maxRepositionAttempts = Math.Max(1, ini.Get("Pathfinding", "MaxRepositionAttempts").ToInt32(Pathfinding.maxRepositionAttempts));
            Pathfinding.maxPathNodes = Math.Max(100, ini.Get("Pathfinding", "MaxPathNodes").ToInt32(Pathfinding.maxPathNodes));
            var maxPathfindingMs = Math.Max(10, ini.Get("Pathfinding", "MaxPathfindingTimeMs").ToInt32(50));
            Pathfinding.maxPathfindingTime = TimeSpan.FromMilliseconds(maxPathfindingMs);
            Pathfinding.requireSensorsForPathfinding = ini.Get("Pathfinding", "RequireSensorsForPathfinding").ToBoolean(Pathfinding.requireSensorsForPathfinding);
            Pathfinding.requireCamerasForPathfinding = ini.Get("Pathfinding", "RequireCamerasForPathfinding").ToBoolean(Pathfinding.requireCamerasForPathfinding);
            Pathfinding.maxSimulatedCameraRaycastMeters = (float)MathHelper.Clamp(ini.Get("Pathfinding", "CameraRange").ToDouble(Pathfinding.maxSimulatedCameraRaycastMeters), 10.0, 5000.0);
            Pathfinding.SimulatedSensorRange = MathHelper.Clamp(ini.Get("Pathfinding", "SimulatedSensorRange").ToDouble(Pathfinding.SimulatedSensorRange), 1.0, 500.0);
            Pathfinding.RaysPerTick = MathHelper.Clamp(ini.Get("Pathfinding", "RaysPerTick").ToInt32(Pathfinding.RaysPerTick), 5, 1000);
            Pathfinding.ArcMinDistance = MathHelper.Clamp(ini.Get("Pathfinding", "ArcMinDistance").ToDouble(Pathfinding.ArcMinDistance), 50.0, 100000.0);
            Pathfinding.CruiseAltitude = MathHelper.Clamp(ini.Get("Pathfinding", "CruiseAltitude").ToDouble(Pathfinding.CruiseAltitude), 0.0, 1000.0);
            Pathfinding.PathMemoryMaxNodes = MathHelper.Clamp(ini.Get("Pathfinding", "PathMemoryMaxNodes").ToInt32(Pathfinding.PathMemoryMaxNodes), 100, 100000);
            Pathfinding.PathMemoryMaxAgeHours = MathHelper.Clamp(ini.Get("Pathfinding", "PathMemoryMaxAgeHours").ToDouble(Pathfinding.PathMemoryMaxAgeHours), 1.0, 8760.0);
            Pathfinding.AllowLazyTheta = ini.Get("Pathfinding", "AllowLazyTheta").ToBoolean(Pathfinding.AllowLazyTheta);
            Pathfinding.ThetaSearchRadius = MathHelper.Clamp(ini.Get("Pathfinding", "ThetaSearchRadius").ToDouble(Pathfinding.ThetaSearchRadius), 50.0, 2000.0);
            Pathfinding.ThetaMaxExpansions = MathHelper.Clamp(ini.Get("Pathfinding", "ThetaMaxExpansions").ToInt32(Pathfinding.ThetaMaxExpansions), 100, 100000);
            Pathfinding.ThetaMaxMs = MathHelper.Clamp(ini.Get("Pathfinding", "ThetaMaxMs").ToDouble(Pathfinding.ThetaMaxMs), 1.0, 500.0);
            Pathfinding.PathSearchWorkers = MathHelper.Clamp(ini.Get("Pathfinding", "PathSearchWorkers").ToInt32(Pathfinding.PathSearchWorkers), 0, 8);
            Pathfinding.PathSearchMaxMs = MathHelper.Clamp(ini.Get("Pathfinding", "PathSearchMaxMs").ToDouble(Pathfinding.PathSearchMaxMs), 1.0, 1000.0);
            Pathfinding.RealShapeConfirm = ini.Get("Pathfinding", "RealShapeConfirm").ToBoolean(Pathfinding.RealShapeConfirm);
            Pathfinding.LatticeSearch = ini.Get("Pathfinding", "LatticeSearch").ToBoolean(Pathfinding.LatticeSearch);
            Pathfinding.LatticeMaxMs = MathHelper.Clamp(ini.Get("Pathfinding", "LatticeMaxMs").ToDouble(Pathfinding.LatticeMaxMs), 1.0, 500.0);
            Pathfinding.StraightMaxMs = MathHelper.Clamp(ini.Get("Pathfinding", "StraightMaxMs").ToDouble(Pathfinding.StraightMaxMs), 0.0, 1000.0);
            Pathfinding.MemoryMaxMs = MathHelper.Clamp(ini.Get("Pathfinding", "MemoryMaxMs").ToDouble(Pathfinding.MemoryMaxMs), 0.0, 1000.0);
            Pathfinding.FlightLaneExpirationMinutes = MathHelper.Clamp(ini.Get("Pathfinding", "FlightLaneExpirationMinutes").ToDouble(Pathfinding.FlightLaneExpirationMinutes), 5.0, 525600.0);

            // Parse Drone section
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

            // Parse MessageQueue section
            MessageQueue.messageRetentionTicks = Math.Max(600, ini.Get("MessageQueue", "MessageRetentionTicks").ToInt32(MessageQueue.messageRetentionTicks));
            MessageQueue.messageCleanupIntervalTicks = Math.Max(60, ini.Get("MessageQueue", "MessageCleanupIntervalTicks").ToInt32(MessageQueue.messageCleanupIntervalTicks));

            // Parse LogisticsComputer section
            LogisticsComputer.AllowLogistics = ini.Get("LogisticsComputer", "AllowLogistics").ToBoolean(LogisticsComputer.AllowLogistics);
            LogisticsComputer.AllowNetworkPush = ini.Get("LogisticsComputer", "AllowNetworkPush").ToBoolean(LogisticsComputer.AllowNetworkPush);
            LogisticsComputer.AllowNetworkRequests = ini.Get("LogisticsComputer", "AllowNetworkRequests").ToBoolean(LogisticsComputer.AllowNetworkRequests);
            LogisticsComputer.MinPushFrequencyTicks = Math.Max(60, ini.Get("LogisticsComputer", "MinPushFrequencyTicks").ToInt32(LogisticsComputer.MinPushFrequencyTicks));
            LogisticsComputer.MaxPushFrequencyTicks = Math.Max(LogisticsComputer.MinPushFrequencyTicks, ini.Get("LogisticsComputer", "MaxPushFrequencyTicks").ToInt32(LogisticsComputer.MaxPushFrequencyTicks));

            // Parse Logging section
            Logging.LogLevel = ParseLogLevel(ini.Get("Logging", "LogLevel").ToString("Info|Debug|Warning|Error"));
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



    }
}