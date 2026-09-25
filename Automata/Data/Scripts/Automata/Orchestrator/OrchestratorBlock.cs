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

using Automata.Util.Logging;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Game logic component that bridges the Space Engineers block system with the IAIScheduler functionality
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RemoteControl), false, "ImprovedAILargeOrchestrator")]
    public class OrchestratorBlock : MyGameLogicComponent
    {
        // The actual orchestrator instance
        private Orchestrator orchestrator;
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
        /// Provides access to the internal orchestrator for terminal controls and debugging
        /// </summary>
        public Orchestrator Orchestrator => orchestrator;


        /// <summary>
        /// Gets whether the orchestrator is enabled
        /// </summary>
        public bool IsEnabled => orchestrator?.isEnabled ?? false;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            try
            {
                block = (IMyCubeBlock)Entity;
                NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
                Log.Verbose("Orchestrator block initializing: {0}", Entity.DisplayName);
            }
            catch (Exception ex)
            {
                Log.Error("Init", ex);
            }
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            OrchestratorTerminalControls.DoOnce(ModContext);
            broadcastController = (IMyBroadcastController)Entity;
            orchestrator = new Orchestrator(Entity, operationMode);

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
                InitializeOrchestrator();
                return;
            }

            orchestrator.UpdateAI();
        }
        private void InitializeOrchestrator()
        {
            try
            {
                if (_initialized)
                    return;

                // Load configuration from block storage if available
                LoadConfiguration();

                // Mark as initialized
                _initialized = true;

                MyAPIGateway.Utilities.ShowMessage("BetterAI_Orchestrator",
                    $"Orchestrator initialized: {Entity.DisplayName}");
            }
            catch (Exception ex)
            {
                Log.Error("InitializeOrchestrator", ex);
            }
        }

        private void LoadConfiguration()
        {
            try
            {
                if (Entity.Storage != null)
                {
                    var storage = Entity.Storage.GetValue(AutomataSession.MOD_GUID);
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
        /// Sets the orchestrator operation mode
        /// </summary>
        public void SetOperationMode(OperationMode mode)
        {
            operationMode = mode;
            if (orchestrator != null)
            {
                orchestrator.operationMode = mode;
            }
            SaveConfiguration();
        }

        /// <summary>
        /// Sets the work modes for the orchestrator
        /// </summary>
        public void SetWorkModes(WorkModes modes)
        {
            workModes = modes;
            if (orchestrator != null)
            {
                orchestrator.workModes = modes;
            }
            SaveConfiguration();
        }

        /// <summary>
        /// Enables or disables the orchestrator
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (orchestrator != null)
            {
                orchestrator.isEnabled = enabled;
            }
        }

        /// <summary>
        /// Gets diagnostic information about the orchestrator
        /// </summary>
        public string GetDiagnostics()
        {
            if (orchestrator != null)
            {
                return orchestrator.GetErrorDiagnostics();
            }
            return "Scheduler not initialized";
        }

        /// <summary>
        /// Resets the orchestrator to initial state
        /// </summary>
        public void Reset()
        {
            if (orchestrator != null)
            {
                orchestrator.ResetScheduler();
            }
        }

        public override void MarkForClose()
        {
            try
            {
                // Clean shutdown
                if (orchestrator != null)
                {
                    // The orchestrator should handle its own cleanup
                    orchestrator.ResetScheduler();
                }

                // Save configuration before closing
                SaveConfiguration();

                // Clear references
                orchestrator = null;

                MyAPIGateway.Utilities.ShowMessage("ImprovedAIOrchestrator", $"Orchestrator closed: {Entity?.DisplayName}");
            }
            catch (Exception ex)
            {
                Log.Error("MarkForClose", ex);
            }
        }
    }
}