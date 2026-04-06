using ImprovedAI.Config;
using ImprovedAI.Util.Logging;
using ImprovedAI.VirtualNetwork;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.IO;
using VRage;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;
using ImprovedAI.Util;

namespace ImprovedAI
{
    public enum ShareWith : byte
        {
            NoOne = 0,
            Friends = 1,
            Faction = 2,
            Neutrals = 4,
            Enemies = 8,
    }
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class IAISession : MySessionComponentBase
    {
        public const string VERSION = "v0.0.1";
        public const string MOD_NAME = "ImprovedAI";
        public static readonly Guid MOD_GUID = new Guid("1CFDA990-FD26-4950-A127-7BBC99FF1397");
        private const string MESSAGE_QUEUE_SNAPSHOT_FILE = "IAIMessageQueueSnapshot.dat";
        public static IAISession Instance;
        private static ServerConfig _serverConfig;

        // Shared message queue for all AI components
        public static MessageQueue _messageQueue;
        public MessageQueue MessageQueue => _messageQueue;

        // Mod-wide settings and management
        private bool isInitialized = false;
        private long _lastUpdateFrame = 0;
        private int _updateInterval; // DON'T initialize here - will be set in Init()
        private int updateCounter = 0;
        private const int UPDATE_INTERVAL = 60;

        // _messageQueue
        private int messageQueueCleanupIntervalTicks;
        private int lastMessageQueueCleanupFrame = 0;

        // Collection of all AI blocks in the world
        public Dictionary<long, IAIDroneControllerBlock> AIDroneControllers = new Dictionary<long, IAIDroneControllerBlock>();
        public Dictionary<long, IAISchedulerBlock> AIDroneSchedulers = new Dictionary<long, IAISchedulerBlock>();
        public Dictionary<long, IAILogisticsComputerBlock> AILogisticsComputers = new Dictionary<long, IAILogisticsComputerBlock>();

        /// <summary>
        /// All components: steel plate, metal grid, etc.
        /// For use in logistic computer list filters.
        /// </summary>
        public Dictionary<long, string> AllComponents;

        /// <summary>
        /// All blocks: steel plate, metal grid, etc.
        /// For use in scheduler grind and weld list filters.
        /// </summary>
        public Dictionary<long, string> AllBlocks;

        public static ServerConfig GetConfig() => _serverConfig;
        public static MessageQueue GetMessageQueue() => _messageQueue;

        public override void LoadData()
        {
            Instance = this;
            // Localization settings
            LoadLangOverrides();
            MyAPIGateway.Gui.GuiControlRemoved += GuiControlRemoved;
        }
        public IAISession()
        {
            try
            {
                MyLog.Default.WriteLineAndConsole("ImprovedAI: Instance constructor called");
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"ImprovedAI: Instance constructor failed: {ex}");
            }
        }

        private void Init()
        {
            try
            {
                Log.Initialize(MOD_NAME, 0, "ImprovedAI.log", typeof(IAISession));
                Log.Info("=== ImprovedAI Initializing ===");

                _serverConfig = ServerConfig.Instance;
                _serverConfig.LoadConfig();
                Log.Verbose("Server config loaded.");
                MessageQueue.Init(_serverConfig.MessageQueue);
                Log.Verbose("Message queue initialized.");
                _messageQueue = MessageQueue.Instance;
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(MESSAGE_QUEUE_SNAPSHOT_FILE, typeof(IAISession)))
                {
                    Log.Info("Found existing messages {0}; loading into MessageQueue");
                    using (BinaryReader reader = MyAPIGateway.Utilities.ReadBinaryFileInWorldStorage(MESSAGE_QUEUE_SNAPSHOT_FILE, typeof(IAISession)))
                    {
                        int length = reader.ReadInt32();
                        byte[] data = reader.ReadBytes(length);
                        _messageQueue.DeserializeAllMessages(data);
                    }
                }
                Log.Info("Log level: {0}", _serverConfig.Logging.LogLevel.ToString());

                _updateInterval = _serverConfig.Session.UpdateInterval;

                Log.Info("Mod loaded successfully");
                Log.Info("Update interval: {0} ticks", _updateInterval);
                IAILogisticsComputerTerminalControls.DoOnce(ModContext);
                IAIDroneControllerTerminalControls.DoOnce(ModContext);

                //LogAllTerminalControlClasses();


                isInitialized = true;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"ImprovedAI: Init exception: {ex}");
            }
        }


        //static void LogAllTerminalControlClasses()
        //{

        //    List<IMyTerminalControl> broadcastControllerControls;
        //    MyAPIGateway.TerminalControls.GetControls<IMyBroadcastController>(out broadcastControllerControls);
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");

        //    MyLog.Default.WriteLine($"[DEV] BROADCAST CONTROLLER TERMINAL CONTROS:");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    foreach (IMyTerminalControl c in broadcastControllerControls)
        //    {
        //        // a quick way to dump all IDs to SE's log
        //        string name = MyTexts.GetString((c as IMyTerminalControlTitleTooltip)?.Title.String ?? "N/A");
        //        string valueType = (c as ITerminalProperty)?.TypeName ?? "N/A";
        //        MyLog.Default.WriteLine($"[DEV] terminal property: id='{c.Id}'; type='{c.GetType().Name}'; valueType='{valueType}'; displayName='{name}'");
        //    }

        //    List<IMyTerminalAction> bcActions;
        //    MyAPIGateway.TerminalControls.GetActions<IMyBroadcastController>(out bcActions);
        //    foreach (IMyTerminalAction a in bcActions)
        //    {
        //        MyLog.Default.WriteLine($"[DEV] toolbar action: id='{a.Id}'; displayName='{a.Name}'");
        //    }

        //        MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"[DEV] PROGRAMMABLE BLOCK CONTROLS:");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    List<IMyTerminalControl> pbControls;
        //    MyAPIGateway.TerminalControls.GetControls<IMyProgrammableBlock>(out pbControls);

        //    foreach (IMyTerminalControl c in pbControls)
        //    {
        //        // a quick way to dump all IDs to SE's log
        //        string name = MyTexts.GetString((c as IMyTerminalControlTitleTooltip)?.Title.String ?? "N/A");
        //        string valueType = (c as ITerminalProperty)?.TypeName ?? "N/A";
        //        MyLog.Default.WriteLine($"[DEV] terminal property: id='{c.Id}'; type='{c.GetType().Name}'; valueType='{valueType}'; displayName='{name}'");
        //    }
        //    List<IMyTerminalAction> pbActions;
        //    MyAPIGateway.TerminalControls.GetActions<IMyProgrammableBlock>(out pbActions);
        //    foreach (IMyTerminalAction a in pbActions)
        //    {
        //        MyLog.Default.WriteLine($"[DEV] toolbar action: id='{a.Id}'; displayName='{a.Name}'");
        //    }

        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"[DEV] REMOTE CONTROL BLOCK CONTROLS:");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    List<IMyTerminalControl> rcControls;
        //    MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out rcControls);

        //    foreach (IMyTerminalControl c in rcControls)
        //    {
        //        // a quick way to dump all IDs to SE's log
        //        string name = MyTexts.GetString((c as IMyTerminalControlTitleTooltip)?.Title.String ?? "N/A");
        //        string valueType = (c as ITerminalProperty)?.TypeName ?? "N/A";
        //        MyLog.Default.WriteLine($"[DEV] terminal property: id='{c.Id}'; type='{c.GetType().Name}'; valueType='{valueType}'; displayName='{name}'");
        //    }
        //    List<IMyTerminalAction> rcActions;
        //    MyAPIGateway.TerminalControls.GetActions<IMyRemoteControl>(out rcActions);
        //    foreach (IMyTerminalAction a in rcActions)
        //    {
        //        MyLog.Default.WriteLine($"[DEV] toolbar action: id='{a.Id}'; displayName='{a.Name}'");
        //    }
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //    MyLog.Default.WriteLine($"");
        //}

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if (!isInitialized)
                {
                    if (MyAPIGateway.Session == null)
                        return;
                    Log.Info("Initializing IAI Session");
                    Init();
                    return;
                }

                var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
                if ((currentFrame - lastMessageQueueCleanupFrame) > messageQueueCleanupIntervalTicks)
                {
                    Log.Info("performing message queue cleanup");
                    lastMessageQueueCleanupFrame = currentFrame;
                    _messageQueue.TryPerformCleanup();
                }
                if (currentFrame - _lastUpdateFrame < _updateInterval)
                {
                    return;
                }

                _lastUpdateFrame = currentFrame;

                // Periodic cleanup and maintenance
                CleanupInvalidBlocks();

                // Optional: Log active AI block counts
                updateCounter++;
                if (updateCounter % (UPDATE_INTERVAL * 10) == 0) // Every 10 seconds
                {
                    Log.Verbose("Active AI Drone Schedulers: {0}", AIDroneSchedulers.Count);
                    Log.Verbose("Active AI Drone Controllers: {0}", AIDroneControllers.Count);
                    Log.Verbose("Active AI Logistics Computers: {0}", AILogisticsComputers.Count);
                }
            }
            catch (Exception ex)
            {
                MyAPIGateway.Utilities.ShowMessage("ImprovedAI", $"Update error: {ex.Message}");
                MyLog.Default.WriteLine($"ImprovedAI: UpdateBeforeSimulation exception: {ex}");
            }
        }

        private void CleanupInvalidBlocks()
        {
            var schedulersToRemove = new List<long>();
            foreach (var kvp in AIDroneSchedulers)
            {
                if (kvp.Value?.Entity == null || kvp.Value.Entity.MarkedForClose)
                {
                    schedulersToRemove.Add(kvp.Key);
                }
            }

            foreach (var entityId in schedulersToRemove)
            {
                AIDroneSchedulers.Remove(entityId);
            }

            var controllersToRemove = new List<long>();
            foreach (var kvp in AIDroneControllers)
            {
                if (kvp.Value?.Entity == null || kvp.Value.Entity.MarkedForClose)
                {
                    controllersToRemove.Add(kvp.Key);
                }
            }

            foreach (var entityId in controllersToRemove)
            {
                AIDroneControllers.Remove(entityId);
            }

            var logisticsToRemove = new List<long>();
            foreach (var kvp in AILogisticsComputers)
            {
                if (kvp.Value?.Entity == null || kvp.Value.Entity.MarkedForClose)
                {
                    logisticsToRemove.Add(kvp.Key);
                }
            }

            foreach (var entityId in logisticsToRemove)
            {
                AILogisticsComputers.Remove(entityId);
            }
        }

        public void RegisterScheduler(IAISchedulerBlock block)
        {
            if (block?.Entity != null)
            {
                AIDroneSchedulers[block.Entity.EntityId] = block;
                Log.Info("Registered AI Drone Scheduler: {0} {1}", block.Entity.EntityId, block.Entity.DisplayName);
            }
        }

        public void UnregisterScheduler(long entityId)
        {
            if (AIDroneSchedulers.Remove(entityId))
            {
                Log.Info("Unregistered AI Scheduler: {0}", entityId);
            }
        }

        public void RegisterDroneController(IAIDroneControllerBlock block)
        {

            if (block?.Entity != null)
            {
                AIDroneControllers[block.Entity.EntityId] = block;
                Log.Info("Registered AI Drone controller: {0} {1}", block.Entity.EntityId, block.Entity.DisplayName);
            }
        }

        public void UnregisterDroneController(long entityId)
        {
            if (AIDroneControllers.Remove(entityId))
            {
                Log.Info("Unregistered AI Drone controller: {0}", entityId);
            }
        }

        public void RegisterLogisticsComputer(IAILogisticsComputerBlock block)
        {
            if (block?.Entity != null)
            {
                AILogisticsComputers[block.Entity.EntityId] = block;
                Log.Info("Registered AI Logistics Computer: {0} {1}", block.Entity.EntityId, block.Entity.DisplayName);
            }
        }

        public void UnregisterLogisticsComputer(long entityId)
        {
            if (AILogisticsComputers.Remove(entityId))
            {
                Log.Info("Unregistered AI Logistics Computer: {0}", entityId);
            }
        }

        protected override void UnloadData()
        {
            try
            {
                // Reset message queue singleton
                if (_messageQueue != null)
                {
                    var mqData = _messageQueue.SerializeForSave();
                    using (var writer = MyAPIGateway.Utilities.WriteBinaryFileInWorldStorage(MESSAGE_QUEUE_SNAPSHOT_FILE, typeof(IAISession)))
                    {
                        writer.Write(mqData.Length);
                        writer.Write(mqData);
                    }
                    _messageQueue.Reset();
                }

                // Clean shutdown
                AIDroneControllers.Clear();
                AIDroneSchedulers.Clear();
                AILogisticsComputers.Clear();


                // Remove localization texts
                MyAPIGateway.Gui.GuiControlRemoved -= GuiControlRemoved;

                Instance = null;

                if (Log.IsInitialized)
                {
                    Log.Info("Mod unloaded");
                    Log.Close();
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"ImprovedAI: UnloadData exception: {ex}");

                var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage("shutdown_error.log", typeof(IAISession));
                if (writer != null)
                {
                    writer.Write(ex.ToString());
                    writer.Flush();
                    writer.Close();
                }
            }
        }

        void LoadLangOverrides()
        {
            string folder = Path.Combine(ModContext.ModPathData, "Localization");
            MyTexts.LoadTexts(folder, cultureName: "override", subcultureName: null);
        }

        void GuiControlRemoved(object screen)
        {
            if (screen == null)
                return;

            // detect when options menu is closed in case player changes language
            if (screen.ToString().EndsWith("ScreenOptionsSpace"))
            {
                LoadLangOverrides();
            }
        }
    }
}