using ImprovedAI.Config;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using Sandbox.ModAPI;
using System;
using System.Linq;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Utils;
using VRageMath;

namespace ImprovedAI.Config
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
        private static readonly string CONFIG_FILENAME = "ImprovedAI.ini";

        public SessionConfig Session { get; private set; }
        public BlockLimitConfig BlockLimits { get; private set; }
        public PathfindingConfig Pathfinding { get; private set; }
        public DroneControllerBlockConfig Drone { get; private set; }
        public MessageQueueConfig MessageQueue { get; private set; }
        public LoggingConfig Logging { get; private set; }
        public OrchestratorBlockConfig OrchestratorServerSettings { get; private set; }
        public LogisticsComputerConfig LogisticsComputer { get; private set; }
        public ConstructionComputerConfig ConstructionComputer { get; private set; }

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
            public int PowerCheckIntervalTicks { get; private set; } = 300;
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

        }

        public class OrchestratorBlockConfig
        {
            /// <summary>
            /// Maximum number of job targets when ran by a drone.
            /// </summary>
            public int StandAloneOrchestratorMaxTargetLimit { get; internal set; } = 10;
            public int OrchestratorMaxTargetLimit { get; internal set; } = 100;
            public int MaxTaskAssignmentPerBatch { get; internal set; } = 10;

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
            /// Gameplay frames to collect TaskBid responses after a TaskAnnouncement.
            /// </summary>
            public int BidCollectionWindowTicks { get; internal set; } = 90;
            /// <summary>
            /// Gameplay frames a bidder stays blacklisted for a task after abort / fulfillment loss.
            /// </summary>
            public int BidBlacklistCooldownTicks { get; internal set; } = 3600;
        }

        public class LoggingConfig
        {
            public LogLevel LogLevel { get; internal set; } = LogLevel.Info|LogLevel.Warning|LogLevel.Error;
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
                    MyAPIGateway.Utilities.ShowMessage(IAISession.MOD_NAME, "No config file found, using defaults");
                    return;
                }

                ParseConfig(configText);

                MyAPIGateway.Utilities.ShowMessage(IAISession.MOD_NAME, "Server configuration loaded successfully");
            }
            catch (Exception ex)
            {
                MyAPIGateway.Utilities.ShowMessage(IAISession.MOD_NAME, $"Error loading config: {ex.Message}");
                MyLog.Default.WriteLine($"{IAISession.MOD_NAME}: LoadConfig exception: {ex}");
            }
        }

        private string LoadConfigFile()
        {
            try
            {
                // Get the current mod item
                var modItem = MyAPIGateway.Session.Mods.FirstOrDefault(m => m.GetPath().Contains("ImprovedAI"));
                if (modItem.Name != IAISession.MOD_NAME)
                {
                    Log.Warning($"{IAISession.MOD_NAME}: Could not find mod item for config loading");
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
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(CONFIG_FILENAME, typeof(IAISession)))
                {
                    using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(CONFIG_FILENAME, typeof(IAISession)))
                    {
                        return reader.ReadToEnd();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"{IAISession.MOD_NAME}: LoadConfigFile exception: {ex}");
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
                MyAPIGateway.Utilities.ShowMessage(IAISession.MOD_NAME, "Failed to parse config file - using defaults");
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

            // Parse SchedulerBounds (sample ImprovedAI.ini); [Scheduler] kept as legacy alias for same keys.
            const string schedBounds = "SchedulerBounds";
            const string schedLegacy = "Scheduler";
            const string schedState = "SchedulerBounds.StateUpdateIntervalTicks";
            OrchestratorServerSettings.OrchestratorMaxTargetLimit = Math.Max(1, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaxTargetLimit", OrchestratorServerSettings.OrchestratorMaxTargetLimit));
            OrchestratorServerSettings.PerScanLimit = Math.Max(10, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "PerScanLimit", OrchestratorServerSettings.PerScanLimit));
            OrchestratorServerSettings.MaxTaskAssignmentPerBatch = Math.Max(1, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaxTaskAssignmentPerBatch", OrchestratorServerSettings.MaxTaskAssignmentPerBatch));
            OrchestratorServerSettings.ScanDelayTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "ScanDelayTicks", OrchestratorServerSettings.ScanDelayTicks));
            OrchestratorServerSettings.ErrorRecoveryIntervalTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "ErrorRecoveryIntervalTicks", OrchestratorServerSettings.ErrorRecoveryIntervalTicks));
            OrchestratorServerSettings.MaxConsecutiveErrors = Math.Max(1, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaxConsecutiveErrors", OrchestratorServerSettings.MaxConsecutiveErrors));
            OrchestratorServerSettings.ManintenanceIntervalTicks = Math.Max(60, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "MaintenanceIntervalTicks", OrchestratorServerSettings.ManintenanceIntervalTicks));
            OrchestratorServerSettings.BidCollectionWindowTicks = Math.Max(10, ReadIniIntPreferSections(ini, schedBounds, schedLegacy, "BidCollectionWindowTicks", OrchestratorServerSettings.BidCollectionWindowTicks));
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

            // Parse DroneNetwork section
            MessageQueue.networkUpdateRate = Math.Max(1.0f, ini.Get("MessageQueue", "NetworkUpdateRate").ToSingle(MessageQueue.networkUpdateRate));
            MessageQueue.schedulerAntennaCacheUpdateIntervalTicks = Math.Max(60, ini.Get("MessageQueue", "SchedulerAntennaCacheUpdateIntervalTicks").ToInt32(MessageQueue.schedulerAntennaCacheUpdateIntervalTicks));
            MessageQueue.schedulerMessageThrottlingTicks = Math.Max(1, ini.Get("MessageQueue", "SchedulerMessageThrottlingTicks").ToInt32(MessageQueue.schedulerMessageThrottlingTicks));
            MessageQueue.schedulerMessageReadLimit = Math.Max(1, ini.Get("MessageQueue", "SchedulerMessageReadLimit").ToInt32(MessageQueue.schedulerMessageReadLimit));
            MessageQueue.droneMessageThrottlingTicks = Math.Max(1, ini.Get("MessageQueue", "DroneMessageThrottlingTicks").ToInt32(MessageQueue.droneMessageThrottlingTicks));
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
            Logging.LogLevel = ParseLogLevel(ini.Get("Logging", "LogLevel").ToString("Info|Warning|Error"));
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
            if (logLevelStr.ToLower().Contains("warning"))
                level |= LogLevel.Warning;
            if (logLevelStr.ToLower().Contains("error"))
                level |= LogLevel.Error;
            return level;
        }

        public bool CanPlayerCreateDroneController(long playerId)
        {
            if (BlockLimits.MaxDroneControllersPerPlayer < 0) return true;

            int playerConstructorCount = 0;
            if (IAISession.Instance?.AIDroneControllers != null)
            {
                foreach (var constructor in IAISession.Instance.AIDroneControllers.Values)
                {
                    var entity = constructor.Entity as IMyCubeBlock;
                    if (entity != null && entity.OwnerId == playerId)
                        playerConstructorCount++;
                }
            }

            return playerConstructorCount < BlockLimits.MaxDroneControllersPerPlayer;
        }

        public bool CanPlayerCreateScheduler(long playerId)
        {
            if (BlockLimits.MaxSchedulersPerPlayer < 0) return true;

            int playerSchedulerCount = 0;
            if (IAISession.Instance?.AIDroneSchedulers != null)
            {
                foreach (var scheduler in IAISession.Instance.AIDroneSchedulers.Values)
                {
                    var entity = scheduler.Entity as IMyCubeBlock;
                    if (entity != null && entity.OwnerId == playerId)
                        playerSchedulerCount++;
                }
            }

            return playerSchedulerCount < BlockLimits.MaxSchedulersPerPlayer;
        }

        public bool CanFactionCreateScheduler(long factionId)
        {
            if (BlockLimits.MaxSchedulersPerFaction <= 0) return true;

            int factionSchedulerCount = 0;
            if (IAISession.Instance?.AIDroneSchedulers != null)
            {
                foreach (var scheduler in IAISession.Instance.AIDroneSchedulers.Values)
                {
                    var entity = scheduler.Entity as IMyCubeBlock;
                    if (entity != null)
                    {
                        var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(entity.OwnerId);
                        if (faction?.FactionId == factionId)
                            factionSchedulerCount++;
                    }
                }
            }

            return factionSchedulerCount < BlockLimits.MaxSchedulersPerFaction;
        }

        public bool CanFactionCreateDroneController(long factionId)
        {
            if (BlockLimits.MaxDroneControllersPerFaction <= 0) return true;

            int factionConstructorCount = 0;
            if (IAISession.Instance?.AIDroneControllers != null)
            {
                foreach (var constructor in IAISession.Instance.AIDroneControllers.Values)
                {
                    var entity = constructor.Entity as IMyCubeBlock;
                    if (entity != null)
                    {
                        var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(entity.OwnerId);
                        if (faction?.FactionId == factionId)
                            factionConstructorCount++;
                    }
                }
            }

            return factionConstructorCount < BlockLimits.MaxDroneControllersPerFaction;
        }

        public string GetConfigSummary()
        {
            var summary = "=== ImprovedAI Server Config ===\n";
            summary += $"Schedulers - Max per Player: {(BlockLimits.MaxSchedulersPerPlayer > 0 ? BlockLimits.MaxSchedulersPerPlayer.ToString() : "Unlimited")}\n";
            summary += $"Schedulers - Max per Faction: {(BlockLimits.MaxSchedulersPerFaction > 0 ? BlockLimits.MaxSchedulersPerFaction.ToString() : "Unlimited")}\n";
            summary += $"Drone Controllers - Max per Player: {(BlockLimits.MaxDroneControllersPerPlayer > 0 ? BlockLimits.MaxDroneControllersPerPlayer.ToString() : "Unlimited")}\n";
            summary += $"Drone Controllers - Max per Faction: {(BlockLimits.MaxDroneControllersPerFaction > 0 ? BlockLimits.MaxDroneControllersPerFaction.ToString() : "Unlimited")}\n";
            summary += $"Current Schedulers: {IAISession.Instance?.AIDroneSchedulers?.Count ?? 0}\n";
            summary += $"Current Drone Controllers: {IAISession.Instance?.AIDroneControllers?.Count ?? 0}\n";
            summary += $"Logistics: {(LogisticsComputer.AllowLogistics ? "Enabled" : "Disabled")}\n";
            return summary;
        }
    }
}