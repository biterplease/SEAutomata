using System;
using System.Collections.Generic;

using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace Automata.Drone
{
    // Update scheduling: per-frame updates only while needed, block rescans driven by grid events.
    public partial class DroneControllerBlock
    {
        private bool frameUpdatesOn;          // EACH_FRAME subscribed
        private bool capabilitiesDirty;       // blocks added / removed: rescan on the next 100-tick update

        private readonly List<IMyCubeGrid> subscribedGrids = new List<IMyCubeGrid>();
        private IMyGridGroupData subscribedGroup;
        private Action<IMySlimBlock> onBlockChanged, onBlockDamaged;
        private Action<IMyGridGroupData, IMyCubeGrid, IMyGridGroupData> onGroupChanged;

        // Something needs flight control right now: subscribe immediately (UpdateFrameSubscription drops it later)
        private void WakeFrameUpdates()
        {
            if (frameUpdatesOn || !IsServer) return;   // clients never run flight control
            frameUpdatesOn = true;
            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
        }

        // UpdateBeforeSimulation10: per-frame updates only while flying, orienting, dampening or releasing overrides
        private void UpdateFrameSubscription()
        {
            bool need = activeFlightOrder != null
                || orientationTargetSet
                || preflightStage != 0
                || thrustOverridesActive
                || gyroOverrideActive;
            if (need == frameUpdatesOn) return;
            frameUpdatesOn = need;
            if (need) NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            else NeedsUpdate &= ~MyEntityUpdateEnum.EACH_FRAME;
        }

        // Called from CheckCapabilities with the drone's current mechanical grid group
        private void SubscribeGridEvents(List<IMyCubeGrid> grids, IMyGridGroupData group)
        {
            if (onBlockChanged == null) onBlockChanged = OnBlockChanged;
            if (onBlockDamaged == null) onBlockDamaged = OnBlockDamaged;
            if (onGroupChanged == null) onGroupChanged = OnGroupChanged;

            for (int i = subscribedGrids.Count - 1; i >= 0; i--)
            {
                var g = subscribedGrids[i];
                if (grids.Contains(g)) continue;
                g.OnBlockAdded -= onBlockChanged;
                g.OnBlockRemoved -= onBlockChanged;
                subscribedGrids.RemoveAt(i);
            }
            for (int i = 0; i < grids.Count; i++)
            {
                var g = grids[i];
                if (subscribedGrids.Contains(g)) continue;
                g.OnBlockAdded += onBlockChanged;
                g.OnBlockRemoved += onBlockChanged;
                g.OnBlockIntegrityChanged += onBlockDamaged;
                subscribedGrids.Add(g);
            }
            if (group != subscribedGroup)
            {
                if (subscribedGroup != null)
                {
                    subscribedGroup.OnGridAdded -= onGroupChanged;
                    subscribedGroup.OnGridRemoved -= onGroupChanged;
                }
                subscribedGroup = group;
                if (group != null)
                {
                    group.OnGridAdded += onGroupChanged;
                    group.OnGridRemoved += onGroupChanged;
                }
            }
        }

        private void UnsubscribeGridEvents()
        {
            for (int i = 0; i < subscribedGrids.Count; i++)
            {
                subscribedGrids[i].OnBlockAdded -= onBlockChanged;
                subscribedGrids[i].OnBlockRemoved -= onBlockChanged;
                subscribedGrids[i].OnBlockIntegrityChanged -= onBlockDamaged;
            }
            subscribedGrids.Clear();
            if (subscribedGroup != null)
            {
                subscribedGroup.OnGridAdded -= onGroupChanged;
                subscribedGroup.OnGridRemoved -= onGroupChanged;
                subscribedGroup = null;
            }
        }

        private void OnBlockChanged(IMySlimBlock b)
        {
            capabilitiesDirty = true;
        }

        // Damage (or repair): a functional block that stopped working (below its functional integrity) counts like a
        // removed one; the next 100-tick update rescans
        private void OnBlockDamaged(IMySlimBlock b)
        {
            if (b.FatBlock != null && !b.FatBlock.IsFunctional) capabilitiesDirty = true;
            if (combatPhase == CombatPhase.Handover) combatDamaged = true;   // "flee when taking damage"
            else if (combatPhase == CombatPhase.Scramble) combatUnderFire = true;   // engage: fight back on the way
        }

        private void OnGroupChanged(IMyGridGroupData group, IMyCubeGrid grid, IMyGridGroupData other)
        {
            capabilitiesDirty = true;
        }
    }
}
