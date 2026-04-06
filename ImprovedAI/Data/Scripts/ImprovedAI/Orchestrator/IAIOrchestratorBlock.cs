using ImprovedAI.Util.Logging;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using System;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;

using static ImprovedAI.Orchestrator;

namespace ImprovedAI
{
    /// <summary>
    /// Game logic component that bridges the Space Engineers block system with the IAIScheduler functionality
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RemoteControl), false, "ImprovedAILargeScheduler")]
    public class IAISchedulerBlock : MyGameLogicComponent
    {
        // The actual scheduler instance
        private IAIOrchestrator scheduler;
        private IMyBroadcastController broadcastController;
        private IMyCubeBlock block;

        // Configuration
        private OperationMode operationMode = OperationMode.Orchestrator;
        private WorkModes workModes = WorkModes.WeldUnfinishedBlocks | WorkModes.RepairDamagedBlocks;

        // State tracking
        private bool _initialized = false;
        private int initializationTicks = 0;
        private const int INITIALIZATION_DELAY = 60; // Wait 1 second for grid to stabilize

        /// <summary>
        /// Provides access to the internal scheduler for terminal controls and debugging
        /// </summary>
        public IAIOrchestrator Scheduler => scheduler;


        /// <summary>
        /// Gets whether the scheduler is enabled
        /// </summary>
        public bool IsEnabled => scheduler?.isEnabled ?? false;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            try
            {
                block = (IMyCubeBlock)Entity;
                NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
                Log.Verbose("Scheduler block initializing: {0}", Entity.DisplayName);
            }
            catch (Exception ex)
            {
                Log.Error("Init", ex);
            }
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            IAISchedulerTerminalControls.DoOnce(ModContext);
            broadcastController = (IMyBroadcastController)Entity;
            scheduler = new IAIOrchestrator(Entity, operationMode);

            if (broadcastController.CubeGrid?.Physics == null)
                return; // ignore ghost/projected grids

            if (MyAPIGateway.Multiplayer.IsServer)
            {
            }
            NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
        }

        public void UpdateBeforeSimulation10(MyGameLogicComponent objectBuilder)
        {
            base.UpdateBeforeSimulation10();
            if (!_initialized)
            {
                InitializeScheduler();
                return;
            }

            scheduler.UpdateBeforeSimulation10();
        }
        private void InitializeScheduler()
        {
            try
            {
                if (_initialized)
                    return;

                // Load configuration from block storage if available
                LoadConfiguration();

                // Mark as initialized
                _initialized = true;

                MyAPIGateway.Utilities.ShowMessage("BetterAI_Scheduler",
                    $"Scheduler initialized: {Entity.DisplayName}");
            }
            catch (Exception ex)
            {
                Log.Error("InitializeScheduler", ex);
            }
        }

        private void LoadConfiguration()
        {
            try
            {
                if (Entity.Storage != null)
                {
                    var storage = Entity.Storage.GetValue(IAISession.MOD_GUID);
                    if (!string.IsNullOrEmpty(storage))
                    {
                        // Parse configuration from storage
                        // This would load operationMode, workModes, etc.
                        ParseConfiguration(storage);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("LoadConfiguration", ex);
                // Use default configuration on error
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                if (Entity.Storage == null)
                {
                    Entity.Storage = new MyModStorageComponent();
                }

                var configString = SerializeConfiguration();
                Entity.Storage.SetValue(Guid.Parse("12345678-1234-1234-1234-123456789012"), configString);
            }
            catch (Exception ex)
            {
                Log.Error("SaveConfiguration", ex);
            }
        }

        private void ParseConfiguration(string config)
        {
            // Parse configuration string
            // Format: "OperationMode=Orchestrator;WorkModes=WeldUnfinishedBlocks,RepairDamagedBlocks"
            var parts = config.Split(';');
            foreach (var part in parts)
            {
                var kvp = part.Split('=');
                if (kvp.Length != 2)
                    continue;

                var key = kvp[0].Trim();
                var value = kvp[1].Trim();

                switch (key)
                {
                    case "OperationMode":
                        Enum.TryParse<OperationMode>(value, out operationMode);
                        break;
                    case "WorkModes":
                        ParseWorkModes(value);
                        break;
                }
            }
        }

        private void ParseWorkModes(string value)
        {
            WorkModes workModes = 0;
            var modes = value.Split(',');
            foreach (var mode in modes)
            {
                WorkModes parsedMode;
                if (Enum.TryParse<WorkModes>(mode.Trim(), out parsedMode))
                {
                    workModes |= parsedMode;
                }
            }
        }

        private string SerializeConfiguration()
        {
            return $"OperationMode={operationMode};WorkModes={workModes}";
        }

        /// <summary>
        /// Sets the scheduler operation mode
        /// </summary>
        public void SetOperationMode(OperationMode mode)
        {
            operationMode = mode;
            if (scheduler != null)
            {
                scheduler.operationMode = mode;
            }
            SaveConfiguration();
        }

        /// <summary>
        /// Sets the work modes for the scheduler
        /// </summary>
        public void SetWorkModes(WorkModes modes)
        {
            workModes = modes;
            if (scheduler != null)
            {
                scheduler.workModes = modes;
            }
            SaveConfiguration();
        }

        /// <summary>
        /// Enables or disables the scheduler
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (scheduler != null)
            {
                scheduler.isEnabled = enabled;
            }
        }

        /// <summary>
        /// Gets diagnostic information about the scheduler
        /// </summary>
        public string GetDiagnostics()
        {
            if (scheduler != null)
            {
                return scheduler.GetErrorDiagnostics();
            }
            return "Scheduler not initialized";
        }

        /// <summary>
        /// Resets the scheduler to initial state
        /// </summary>
        public void Reset()
        {
            if (scheduler != null)
            {
                scheduler.ResetScheduler();
            }
        }

        public override void MarkForClose()
        {
            try
            {
                // Clean shutdown
                if (scheduler != null)
                {
                    // The scheduler should handle its own cleanup
                    scheduler.ResetScheduler();
                }

                // Save configuration before closing
                SaveConfiguration();

                // Clear references
                scheduler = null;

                MyAPIGateway.Utilities.ShowMessage("ImprovedAIScheduler", $"Scheduler closed: {Entity?.DisplayName}");
            }
            catch (Exception ex)
            {
                Log.Error("MarkForClose", ex);
            }
        }
    }
}