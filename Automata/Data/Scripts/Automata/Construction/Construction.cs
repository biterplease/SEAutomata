using ProtoBuf;
using System;
using System.Collections.Generic;

using VRage.ModAPI;
using VRage.Utils;

namespace Automata.Construction
{
    public enum State : byte
    {
        Initializing,
        Standby,
        ScanningForJobs,
        PublishingJobs,
        Error
    }
    [Flags]
    public enum OperationMode : byte
    {
        None = 0,
        /// <summary>
        /// This is the mode used when its part of the Orchestrator.
        /// </summary>
        BuiltInToOrchestrator = 1,
        /// <summary>
        /// Standard operatin mode.
        /// </summary>
        ConstructionComputer = 2,
        BuiltInToDrone = 4,
    }
    [Flags, ProtoContract]
    public enum SearchModes : byte
    {
        [ProtoEnum]
        ConnectedGrids = 1,
        [ProtoEnum]
        BoundingBox = 2
    }
    [Flags, ProtoContract]
    public enum WorkModes : byte
    {
        None = 0,
        ScanOnly = 1,
        WeldUnfinishedBlocks = 2,
        RepairDamagedBlocks = 4,
        WeldProjectedBlocks = 8,
        Grind = 16,
    }

    /// <summary>What kind of weld a block needs (weld priority). Grinding is not part of the priority.</summary>
    public enum WorkCategory : byte
    {
        /// <summary>Built and damaged (integrity lost, deformed).</summary>
        Repair = 0,
        /// <summary>A projector's block, not built yet.</summary>
        Projected = 1,
        /// <summary>Placed, not welded up yet (not damaged).</summary>
        Unbuilt = 2,
        /// <summary>Grind: keeps its place in the build order, with the first category.</summary>
        Grind = 3,
    }

    /// <summary>"Weld priority" (terminal): the order of the three weld categories (construction computer: their shares of the
    /// published jobs, 3 : 2 : 1; stand-alone drone: strict order). 0 (the default): repair, projected, unbuilt.</summary>
    public enum WorkPriority : byte
    {
        RepairProjectedUnbuilt = 0,
        RepairUnbuiltProjected = 1,
        ProjectedRepairUnbuilt = 2,
        ProjectedUnbuiltRepair = 3,
        UnbuiltRepairProjected = 4,
        UnbuiltProjectedRepair = 5,
    }

    public static class WorkPriorities
    {
        public const int COUNT = 6;
        // Per priority, the categories in order
        private static readonly WorkCategory[][] ORDER =
        {
            new[] { WorkCategory.Repair, WorkCategory.Projected, WorkCategory.Unbuilt },
            new[] { WorkCategory.Repair, WorkCategory.Unbuilt, WorkCategory.Projected },
            new[] { WorkCategory.Projected, WorkCategory.Repair, WorkCategory.Unbuilt },
            new[] { WorkCategory.Projected, WorkCategory.Unbuilt, WorkCategory.Repair },
            new[] { WorkCategory.Unbuilt, WorkCategory.Repair, WorkCategory.Projected },
            new[] { WorkCategory.Unbuilt, WorkCategory.Projected, WorkCategory.Repair },
        };

        public static WorkPriority Clamp(long value)
        {
            return value >= 0 && value < COUNT ? (WorkPriority)value : WorkPriority.RepairProjectedUnbuilt;
        }

        /// <summary>0 first. Grind ranks with the first category (it keeps its place in the build order).</summary>
        public static int Rank(WorkPriority p, WorkCategory c)
        {
            if (c == WorkCategory.Grind) return 0;
            var order = ORDER[(int)Clamp((long)p)];
            for (int i = 0; i < order.Length; i++) if (order[i] == c) return i;
            return 0;
        }

        public static WorkCategory At(WorkPriority p, int rank)
        {
            return ORDER[(int)Clamp((long)p)][rank];
        }

        private static readonly MyStringId[] NAMES =
        {
            MyStringId.GetOrCompute("Repair, projected, unbuilt"),
            MyStringId.GetOrCompute("Repair, unbuilt, projected"),
            MyStringId.GetOrCompute("Projected, repair, unbuilt"),
            MyStringId.GetOrCompute("Projected, unbuilt, repair"),
            MyStringId.GetOrCompute("Unbuilt, repair, projected"),
            MyStringId.GetOrCompute("Unbuilt, projected, repair"),
        };
        public static readonly MyStringId Title = MyStringId.GetOrCompute("Weld priority");
        public static readonly MyStringId Tooltip = MyStringId.GetOrCompute("The three kinds of weld: repair (damaged blocks), projected (projections), unbuilt (placed, not welded up yet).\nConstruction computer: every kind with work is published at once, the published job slots shared 3 : 2 : 1 in this order (grinding gets the first kind's share), so Welder:Repair / Projected / Unbuilt drones always find their kind of job.\nStand-alone drone: the order it takes them in.\nWithin each kind, the build order.");

        /// <summary>The terminal dropdown's items (Key = WorkPriority).</summary>
        public static void ComboContent(List<MyTerminalControlComboBoxItem> list)
        {
            for (int i = 0; i < COUNT; i++) list.Add(new MyTerminalControlComboBoxItem { Key = i, Value = NAMES[i] });
        }
    }
}
