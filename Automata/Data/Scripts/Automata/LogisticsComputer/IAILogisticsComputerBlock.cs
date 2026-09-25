using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.ModAPI;
using System;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;

using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.LogisticsComputer
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ProgrammableBlockDefinition), false, "ImprovedAILargeLogisticsComputer")]
    public class LogisticsComputerBlock : MyGameLogicComponent
    {
        IMyCubeBlock block;
        IMyProgrammableBlock programmableBlock;
        LogisticsComputer logisticsComputer;
        private bool _initialized = false;
        public IAILogisticsComputerSettings settings { get; set; }

        private static ServerConfig.LogisticsComputerConfig serverConfig = ServerConfig.Instance.LogisticsComputer;


        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            block = (IMyCubeBlock)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }
        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();

            block = (IMyCubeBlock)Entity;
            programmableBlock = (IMyProgrammableBlock)Entity;

            if (programmableBlock.CubeGrid?.Physics == null)
                return; // ignore ghost/projected grids

            // the bonus part, enforcing it to stay a specific value.
            if (MyAPIGateway.Multiplayer.IsServer) // serverside only to avoid network spam
            {
            }
            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
        }

        public override void UpdateAfterSimulation()
        {
            base.UpdateAfterSimulation();
        }

        public override void UpdateAfterSimulation10()
        {
            base.UpdateAfterSimulation10();
            if (!_initialized)
            {
                Initialize();
                return;
            }
        }

        private void Initialize()
        {
            try
            {
                logisticsComputer = new LogisticsComputer(
                    Entity,
                    AutomataSession.Instance.MessageQueue,
                    settings);
                logisticsComputer.Initialize();
                _initialized = true;
            }
            catch (Exception ex)
            {
                Log.Error("LogisticsComputerBlock {0} Initialize error: {1}", Entity.EntityId, ex.Message);
            }
        }

        public bool Terminal_IsEnabled
        {
            get
            {
                return settings?.Enabled ?? false;
            }
            set
            {
                if (settings != null) settings.Enabled = value;
            }
        }
        public bool Terminal_WorkModeProvideForLogistics
        {
            get
            {
                return settings?.OperationMode.HasFlag(WorkMode.ProvideForLogistics) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.WorkMode |= WorkMode.ProvideForLogistics;
                    else
                        settings.WorkMode &= ~WorkMode.ProvideForLogistics;
                }
            }
        }
        public bool Terminal_WorkModeProvideForConstruction
        {
            get
            {
                return settings?.OperationMode.HasFlag(WorkMode.ProvideForConstruction) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.WorkMode |= WorkMode.ProvideForConstruction;
                    else
                        settings.WorkMode &= ~WorkMode.ProvideForConstruction;
                }
            }
        }
        public bool Terminal_WorkModePush
        {
            get
            {
                return settings?.OperationMode.HasFlag(WorkMode.Push) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.WorkMode |= WorkMode.Push;
                    else
                        settings.WorkMode &= ~WorkMode.Push;
                }
            }
        }
        public bool Terminal_WorkModeRequest
        {
            get
            {
                return settings?.WorkMode.HasFlag(WorkMode.Request) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.WorkMode |= WorkMode.Request;
                    else
                        settings.WorkMode &= ~WorkMode.Request;
                }
            }
        }

    }
}
