using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;

using Automata.Util.Logging;

namespace Automata.Logistics
{
    /// <summary>
    /// What makes the Logistics Computer map its networks again (Rescan, at most every DIRTY_RESCAN_TICKS; the periodic
    /// rescan stays as a safety net). Only changes that can move conveyor connectivity, ports or the antenna count:
    ///  - a block added / removed on any grid of the base (not only the LC's own grid), when it has an inventory, carries
    ///    conveyors (conveyor, tube, junction, sorter), is an antenna or a rotor / piston / hinge base. Armor and other
    ///    blocks without a fat block or conveyors are ignored (building on the base used to rescan every 5 s);
    ///  - such a block becoming functional or broken (OnBlockIntegrityChanged fires on every weld / grind / hit: only the
    ///    flip counts, remembered per block; the first event of a block counts as a flip);
    ///  - a grid of the base split or merged, a grid joining / leaving its mechanical group (rotors, pistons, hinges);
    ///  - a sorter switched on / off (IsWorkingChanged).
    /// Connector lock / unlock doesn't: drones dock all the time, and the base's networks are its own grids.
    /// (After BuildInfo's conveyor overlay, which listens to the same set.)
    /// </summary>
    public partial class LogisticsComputerBlock
    {
        private const int FUNCTIONAL_SEEN_MAX = 4096;

        private readonly List<IMyCubeGrid> subscribedGrids = new List<IMyCubeGrid>();
        private readonly List<IMyCubeGrid> subscribeScratch = new List<IMyCubeGrid>();
        private IMyGridGroupData subscribedGroup;
        private readonly List<IMyConveyorSorter> sorters = new List<IMyConveyorSorter>();            // filled by Rescan
        private readonly List<IMyConveyorSorter> subscribedSorters = new List<IMyConveyorSorter>();
        private readonly Dictionary<long, bool> functionalSeen = new Dictionary<long, bool>();
        private Action<IMySlimBlock> onBaseBlockChanged, onBaseIntegrity;
        private Action<IMyCubeGrid, IMyCubeGrid> onBaseSplitMerge;
        private Action<IMyGridGroupData, IMyCubeGrid, IMyGridGroupData> onBaseGroupChanged;
        private Action<IMyGridGroupData> onBaseGroupReleased;
        private Action<IMyCubeBlock> onSorterWorking;
        private string dirtyWhy;

        private void MarkDirty(string why)
        {
            if (dirty) return;
            dirty = true;
            dirtyWhy = why;
        }

        // Blocks whose coming / going / breaking can change the networks, their ports or the antenna
        private static bool ConveyorRelevant(IMyCubeBlock fat)
        {
            return fat != null && (fat.HasInventory || fat is IMyConveyor || fat is IMyConveyorTube || fat is IMyConveyorSorter
                                   || fat is IMyRadioAntenna || fat is IMyMechanicalConnectionBlock);
        }

        /// <summary>The base's grids (after Rescan; before the first one: the LC's grid), its mechanical group and its sorters.</summary>
        private void SubscribeBase()
        {
            if (onBaseBlockChanged == null)
            {
                onBaseBlockChanged = OnBaseBlockChanged;
                onBaseIntegrity = OnBaseIntegrity;
                onBaseSplitMerge = OnBaseSplitMerge;
                onBaseGroupChanged = OnBaseGroupChanged;
                onBaseGroupReleased = OnBaseGroupReleased;
                onSorterWorking = OnSorterWorking;
            }
            subscribeScratch.Clear();
            if (grids.Count > 0) subscribeScratch.AddRange(grids);
            else if (block.CubeGrid != null) subscribeScratch.Add(block.CubeGrid);
            for (int i = subscribedGrids.Count - 1; i >= 0; i--)
            {
                var g = subscribedGrids[i];
                if (subscribeScratch.Contains(g)) continue;
                UnsubscribeGrid(g);
                subscribedGrids.RemoveAt(i);
            }
            for (int i = 0; i < subscribeScratch.Count; i++)
            {
                var g = subscribeScratch[i];
                if (g == null || subscribedGrids.Contains(g)) continue;
                g.OnBlockAdded += onBaseBlockChanged;
                g.OnBlockRemoved += onBaseBlockChanged;
                g.OnBlockIntegrityChanged += onBaseIntegrity;
                g.OnGridSplit += onBaseSplitMerge;
                g.OnGridMerge += onBaseSplitMerge;
                subscribedGrids.Add(g);
            }
            subscribeScratch.Clear();

            var group = block.CubeGrid != null ? block.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical) : null;
            if (group != subscribedGroup)
            {
                UnsubscribeGroup();
                subscribedGroup = group;
                if (group != null)
                {
                    group.OnGridAdded += onBaseGroupChanged;
                    group.OnGridRemoved += onBaseGroupChanged;
                    group.OnReleased += onBaseGroupReleased;
                }
            }

            for (int i = 0; i < subscribedSorters.Count; i++) subscribedSorters[i].IsWorkingChanged -= onSorterWorking;
            subscribedSorters.Clear();
            for (int i = 0; i < sorters.Count; i++)
            {
                sorters[i].IsWorkingChanged += onSorterWorking;
                subscribedSorters.Add(sorters[i]);
            }
        }

        private void UnsubscribeBase()
        {
            for (int i = 0; i < subscribedGrids.Count; i++) UnsubscribeGrid(subscribedGrids[i]);
            subscribedGrids.Clear();
            UnsubscribeGroup();
            if (onSorterWorking != null)
                for (int i = 0; i < subscribedSorters.Count; i++) subscribedSorters[i].IsWorkingChanged -= onSorterWorking;
            subscribedSorters.Clear();
            functionalSeen.Clear();
        }

        private void UnsubscribeGrid(IMyCubeGrid g)
        {
            if (g == null || onBaseBlockChanged == null) return;
            g.OnBlockAdded -= onBaseBlockChanged;
            g.OnBlockRemoved -= onBaseBlockChanged;
            g.OnBlockIntegrityChanged -= onBaseIntegrity;
            g.OnGridSplit -= onBaseSplitMerge;
            g.OnGridMerge -= onBaseSplitMerge;
        }

        private void UnsubscribeGroup()
        {
            if (subscribedGroup == null) return;
            subscribedGroup.OnGridAdded -= onBaseGroupChanged;
            subscribedGroup.OnGridRemoved -= onBaseGroupChanged;
            subscribedGroup.OnReleased -= onBaseGroupReleased;
            subscribedGroup = null;
        }

        private void OnBaseBlockChanged(IMySlimBlock slim)
        {
            var fat = slim != null ? slim.FatBlock : null;
            if (!ConveyorRelevant(fat)) return;
            if (fat.Closed || fat.MarkedForClose) functionalSeen.Remove(fat.EntityId);
            MarkDirty("block added / removed");
        }

        private void OnBaseIntegrity(IMySlimBlock slim)
        {
            var fat = slim != null ? slim.FatBlock : null;
            if (!ConveyorRelevant(fat)) return;
            bool now = fat.IsFunctional, was;
            if (functionalSeen.TryGetValue(fat.EntityId, out was) && was == now) return;
            if (functionalSeen.Count >= FUNCTIONAL_SEEN_MAX) functionalSeen.Clear();
            functionalSeen[fat.EntityId] = now;
            MarkDirty(now ? "block functional" : "block broken");
        }

        private void OnBaseSplitMerge(IMyCubeGrid a, IMyCubeGrid b)
        {
            MarkDirty("grid split / merge");
        }

        private void OnBaseGroupChanged(IMyGridGroupData group, IMyCubeGrid grid, IMyGridGroupData other)
        {
            MarkDirty("mechanical group changed");
        }

        private void OnBaseGroupReleased(IMyGridGroupData group)
        {
            if (group == subscribedGroup) UnsubscribeGroup();
            MarkDirty("mechanical group changed");
        }

        private void OnSorterWorking(IMyCubeBlock sorter)
        {
            MarkDirty("sorter on / off");
        }
    }
}
