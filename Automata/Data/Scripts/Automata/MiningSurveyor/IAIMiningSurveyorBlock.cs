using ImprovedAI.Util.Logging;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using static ImprovedAI.MiningSurveyor;

namespace Automata.MiningSurveyor
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ProgrammableBlockDefinition), false, "ImprovedAILargeMiningSurveyor")]
    public class IAIMiningSurveyorBlock : MyGameLogicComponent
    {
        private IMyCubeBlock block;
        private IMyProgrammableBlock programmableBlock;
        private IAIMiningSurveyor miningSurveyor;
        private bool initialized;
        public IAIMiningSurveyorSettings settings { get; set; }

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
            if (programmableBlock.CubeGrid == null || programmableBlock.CubeGrid.Physics == null)
            {
                return;
            }
            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
        }

        public override void UpdateAfterSimulation10()
        {
            base.UpdateAfterSimulation10();
            if (!initialized)
            {
                TryInitialize();
                return;
            }
            IMyRadioAntenna antenna = FindLargestAntennaOnMechanicalGroup(block);
            if (miningSurveyor != null)
            {
                miningSurveyor.Update(antenna);
            }
        }

        private void TryInitialize()
        {
            try
            {
                if (IAISession.Instance == null || IAISession.Instance.MessageQueue == null)
                {
                    return;
                }
                if (settings == null)
                {
                    settings = new IAIMiningSurveyorSettings
                    {
                        IsEnabled = true,
                        OperationMode = OperationMode.MiningSurveyor,
                        WorkModes = WorkModes.ScanAndPublish,
                        ShareWith = ShareWith.NoOne,
                    };
                }
                miningSurveyor = new IAIMiningSurveyor(
                    Entity,
                    IAISession.Instance.MessageQueue,
                    OperationMode.MiningSurveyor,
                    null,
                    settings);
                initialized = true;
            }
            catch (Exception ex)
            {
                Log.Error("MiningSurveyorBlock {0} init error: {1}", Entity.EntityId, ex.Message);
            }
        }

        private static IMyRadioAntenna FindLargestAntennaOnMechanicalGroup(IMyCubeBlock origin)
        {
            if (origin == null || origin.CubeGrid == null)
            {
                return null;
            }
            List<IMyCubeGrid> connectedGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(origin.CubeGrid, GridLinkTypeEnum.Mechanical, connectedGrids);
            IMyRadioAntenna best = null;
            for (int g = 0; g < connectedGrids.Count; g++)
            {
                IMyCubeGrid grid = connectedGrids[g];
                List<IMySlimBlock> blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);
                for (int i = 0; i < blocks.Count; i++)
                {
                    IMySlimBlock slim = blocks[i];
                    if (slim.FatBlock is IMyRadioAntenna)
                    {
                        IMyRadioAntenna a = (IMyRadioAntenna)slim.FatBlock;
                        if (best == null || a.Radius > best.Radius)
                        {
                            best = a;
                        }
                    }
                }
            }
            return best;
        }
    }
}
