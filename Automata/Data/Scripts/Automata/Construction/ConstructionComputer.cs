using System;
using System.Collections.Generic;

using VRage.Collections;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.ModAPI;
using VRage.Utils;
using Sandbox.ModAPI;


using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Inventory;
using Automata.VirtualNetwork;
using Automata.Util.Strings;

namespace Automata.Construction
{
    public partial class ConstructionComputer
    {
        private readonly long entityId;
        private int _jobIdCounter = 0;
        /// <summary>Job id counter (kept through a world save by the construction computer block: ids stay unique).</summary>
        public int JobIdCounter { get { return _jobIdCounter; } set { _jobIdCounter = value; } }

        public ConstructionComputer(IMyEntity entity)
        {
            entityId = entity.EntityId;
        }

        /// <summary>
        /// Accumulates mounted component counts from <see cref="IMySlimBlock.ComponentStack"/> (material present on the block),
        /// keyed by <see cref="MyComponentStackInfo.ComponentName"/>. Suitable for grind job payload; merges into an existing dictionary.
        /// </summary>
        private static void AddMountedComponentsForGrind(IMySlimBlock block, Dictionary<string, int> addToDictionary)
        {
            IMyComponentStack stack = block.ComponentStack;
            if (stack == null)
            {
                return;
            }
            int groupCount = stack.GroupCount;
            for (int i = 0; i < groupCount; i++)
            {
                MyComponentStackInfo info = stack.GetComponentStackInfo(i);
                int amount = info.MountedCount;
                if (amount <= 0)
                {
                    continue;
                }
                // The item key: a component's subtype ("SteelPlate"), as for welding. ComponentName is the display name
                // ("Steel Plate", localized): it matched no item, and after a reload its hash had no text (KeyNotFound)
                string name = info.DefinitionId.SubtypeName;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                int existing;
                if (addToDictionary.TryGetValue(name, out existing))
                {
                    addToDictionary[name] = existing + amount;
                }
                else
                {
                    addToDictionary[name] = amount;
                }
            }
        }
    }
}
