using ImprovedAI.Config;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.ModAPI;
using System;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using static ImprovedAI.LogisticsComputer;

namespace ImprovedAI
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ProgrammableBlockDefinition), false, "ImprovedAILargeLogisticsComputer")]
    public class IAILogisticsComputerBlock : MyGameLogicComponent
    {
        IMyCubeBlock block;
        IMyProgrammableBlock programmableBlock;
        IAILogisticsComputer logisticsComputer;
        private bool _initialized = false;
        public IAILogisticsComputerSettings settings { get; set; }

        public static ServerConfig.LogisticsComputerConfig BlockConfig = ServerConfig.Instance.LogisticsComputer;


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
            logisticsComputer.Update();
        }

        private void Initialize()
        {
            try
            {
                logisticsComputer = new IAILogisticsComputer(Entity, IAISession.Instance.MessageQueue);
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
        public bool Terminal_OperationModeProvideForLogistics
        {
            get
            {
                return settings?.OperationMode.HasFlag(OperationMode.ProvideForLogistics) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.OperationMode |= OperationMode.ProvideForLogistics;
                    else
                        settings.OperationMode &= ~OperationMode.ProvideForLogistics;
                }
            }
        }
        public bool Terminal_OperationModeProvideForConstruction
        {
            get
            {
                return settings?.OperationMode.HasFlag(OperationMode.ProvideForConstruction) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.OperationMode |= OperationMode.ProvideForConstruction;
                    else
                        settings.OperationMode &= ~OperationMode.ProvideForConstruction;
                }
            }
        }
        public bool Terminal_OperationModePush
        {
            get
            {
                return settings?.OperationMode.HasFlag(OperationMode.Push) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.OperationMode |= OperationMode.Push;
                    else
                        settings.OperationMode &= ~OperationMode.Push;
                }
            }
        }
        public bool Terminal_OperationModeRequest
        {
            get
            {
                return settings?.OperationMode.HasFlag(OperationMode.Request) ?? false;
            }
            set
            {
                if (settings != null)
                {
                    if (value)
                        settings.OperationMode |= OperationMode.Request;
                    else
                        settings.OperationMode &= ~OperationMode.Request;
                }
            }
        }
        public bool Terminal_PushAll
        {
            get
            {
                return settings?.PushAllInventory ?? false;
            }
            set
            {
                if (settings != null) settings.PushAllInventory = value;
            }
        }
        public float Terminal_PushFrequencySeconds
        {
            get { return settings?.PushFrequencySeconds ?? TimeUtil.TickToSeconds(BlockConfig.MinPushFrequencyTicks); }
            set
            {
                if (settings != null) settings.PushFrequencySeconds = value;
            }
        }
        public bool Terminal_SetRequests
        {
            get
            {
                return settings?.SetRequests ?? false;
            }
            set
            {
                if (settings != null) settings.SetRequests = value;
            }
        }
        public bool Terminal_UseItemLimits
        {
            get
            {
                return settings?.UseItemLimits ?? false;
            }
            set
            {
                if (settings != null) settings.UseItemLimits = value;
            }
        }
    }
}
