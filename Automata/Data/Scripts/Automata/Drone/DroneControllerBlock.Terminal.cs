using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Network;
using Automata.VirtualNetwork;

namespace Automata.Drone
{
    /// <summary>
    /// Terminal-facing state added with the control layout rework: AI on/off, operation mode side effects,
    /// "home connector is not a station" + home beacon, behaviour profile, excluded capabilities.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region AI on / off, operation mode
        // Server: everything off, hands off the controls. The game's dampeners (switched back on when the
        // overrides are cleared) hold the drone where it is, hovering if it was flying.
        private void DisableAI()
        {
            StopJob();
            Shutdown();
            preflightStage = 0;
            // Hand the drone back flyable: blocks the dock routine put to sleep (thrusters, tools...) come back on
            // and batteries / tanks leave Recharge / Stockpile
            if (settings.SleepingBlockIds.Count > 0 || settings.RechargingBatteryIds.Count > 0 || settings.StockpilingTankIds.Count > 0)
                WakeSystems();
            isParked = IsParkedNow();
            RestoreConnectors();
            SetState(isParked ? State.Docked : State.Standby);
            Report("AI disabled");
        }

        // Server: side effects of the current mode (called when the AI goes on; the mode can't change while on)
        private void ApplyOperationMode()
        {
            if (!IsServer || settings == null) return;
            var queue = AutomataSession.GetMessageQueue();
            if (queue != null)
            {
                if (settings.OperationMode == OperationMode.ManagedByScheduler)
                {
                    if (primaryAntenna != null) queue.RegisterAntenna(_entityId, MessageQueue.IAIBlockType.Drone, primaryAntenna, false);
                    queue.Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_START);
                    queue.Subscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
                }
                else
                {
                    queue.Unsubscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_START);
                    queue.Unsubscribe(_entityId, Channel.ORCHESTRATOR_AUCTION_WINNER_ANNOUNCEMENT);
                }
            }
            if (settings.OperationMode != OperationMode.StandAlone && cPhase != ConstructionPhase.Idle)
                StopJob();
        }

        /// <summary>Re-evaluates the terminal's enabled / visible states (after a setting other controls depend on).</summary>
        public void Terminal_Refresh()
        {
            RefreshTerminal();
        }

        public bool IsManagedByPlayer { get { return settings != null && settings.OperationMode == OperationMode.ManagedByPlayer; } }
        #endregion

        #region Home connector is not a station: home beacon
        public bool Terminal_HomeNotStation
        {
            get { return settings != null && settings.HomeNotStation; }
            set
            {
                if (settings == null || settings.HomeNotStation == value) return;
                settings.HomeNotStation = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.HomeNotStation);
                RefreshTerminal();   // beacon list enables with this
            }
        }

        public void Terminal_HomeBeaconListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var none = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute("(none)"), MyStringId.NullOrEmpty, 0L);
            items.Add(none);
            if (settings == null || AutomataSession.Instance == null) return;
            RequestAnchorListIfStale();
            bool found = false;
            var entries = AutomataSession.Instance.Anchors.Get(block);
            for (int i = 0; i < entries.Count; i++)
            {
                // Same rule as connectors: the drone owner's AND this player's own / faction beacons only
                if (entries[i].Kind != AnchorKind.Beacon || !AnchorDirectory.ViewerAllowed(entries[i].OwnerId)) continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(FormatAnchor(entries[i])), MyStringId.NullOrEmpty, entries[i].EntityId);
                items.Add(item);
                if (entries[i].EntityId == settings.HomeBeaconId) { selected.Add(item); found = true; }
            }
            if (!found && settings.HomeBeaconId == 0) selected.Add(none);
        }

        public void Terminal_SelectHomeBeacon(List<MyTerminalControlListBoxItem> selected)
        {
            if (settings == null || selected == null || selected.Count == 0 || !(selected[0].UserData is long)) return;
            long id = (long)selected[0].UserData;
            if (id == settings.HomeBeaconId) return;
            RequestAction(new DroneActionPacket { Action = DroneAction.SelectHomeBeacon, Id = id });
        }

        // Server: only ids from this drone's filtered list (drone owner AND requesting player), or none
        private void ExecuteSelectHomeBeacon(long id, long identity)
        {
            if (id != 0 && !IsListedAnchor(id, AnchorKind.Beacon, identity)) return;
            if (id == settings.HomeBeaconId) return;
            settings.HomeBeaconId = id;
            if (id != 0)
            {
                var beacon = AnchorDirectory.Resolve(block, id) as IMyTerminalBlock;
                Report("Home beacon: {0}", beacon != null ? beacon.CustomName : "set");
            }
            else Report("Home beacon cleared");
            SaveSettings();
            SyncSetting(DroneSettingKey.HomeBeacon);
            RefreshTerminal();
        }
        #endregion

        #region Task timeout
        /// <summary>Upper limit of the per-drone task timeout: the server setting (clients: as the server sent it).</summary>
        public static int MaxTaskTimeoutSeconds
        {
            get { return Automata.Config.ServerConfig.Instance.Drone.TaskTimeoutSeconds; }
        }

        public float Terminal_TaskTimeoutSeconds
        {
            get
            {
                RequestClientSettings();   // clients: the server's limit, once per session
                return settings != null ? settings.TaskTimeoutSeconds : 30;
            }
            set
            {
                if (settings == null) return;
                int v = VRageMath.MathHelper.Clamp((int)Math.Round(value), 0, MaxTaskTimeoutSeconds);
                if (v == settings.TaskTimeoutSeconds) return;
                settings.TaskTimeoutSeconds = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.TaskTimeout);
            }
        }
        #endregion

        #region Min altitude
        public const float MIN_ALTITUDE_MAX = 1000f;

        public float Terminal_MinAltitude
        {
            get { return settings != null ? settings.MinAltitude : 0f; }
            set
            {
                if (settings == null) return;
                float v = VRageMath.MathHelper.Clamp((float)Math.Round(value), 0f, MIN_ALTITUDE_MAX);
                if (v == settings.MinAltitude) return;
                settings.MinAltitude = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.MinAltitude);
            }
        }
        #endregion

        #region Behaviour profile (not in use yet)
        public long Terminal_BehaviourProfileValue
        {
            get { return settings != null ? (long)settings.BehaviourProfile : 0L; }
            set
            {
                if (settings == null) return;
                var v = (BehaviourProfile)(byte)value;
                if (v != BehaviourProfile.None && v != BehaviourProfile.IsCargoDrone && v != BehaviourProfile.IsScoutDrone
                    && v != BehaviourProfile.IsRover) return;
                if (settings.BehaviourProfile == v) return;
                settings.BehaviourProfile = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.BehaviourProfile);
            }
        }
        #endregion

        #region Excluded capabilities
        private static readonly Capabilities[] ALL_CAPABILITIES =
        {
            Capabilities.CanDock, Capabilities.CanWeld, Capabilities.CanGrind, Capabilities.CanDrill,
            Capabilities.CanScoutOre, Capabilities.HasWheels, Capabilities.CanFlyAtmosphere, Capabilities.CanFlySpace,
            Capabilities.HasDefenseSystems, Capabilities.HasSensors, Capabilities.HasCameras,
            Capabilities.CargoVolumeNil, Capabilities.CargoVolumeLt10m3, Capabilities.CargoVolumeLt100m3,
            Capabilities.CargoVolumeLt1000m3, Capabilities.CargoVolumeLt10000m3,
            Capabilities.LiftNil, Capabilities.LiftLt1t, Capabilities.LiftLt10t, Capabilities.LiftLt100t, Capabilities.LiftLt1000t,
        };

        // Listbox selections: local to this player's terminal, never synced
        private Capabilities capabilitySelection, exclusionSelection;

        /// <summary>What the drone offers when bidding for jobs: detected capabilities minus the player's exclusions.</summary>
        public Capabilities BidCapabilities
        {
            get { return settings != null ? capabilities & ~settings.ExcludedCapabilities : capabilities; }
        }

        public Capabilities Terminal_ExcludedCapabilities
        {
            get { return settings != null ? settings.ExcludedCapabilities : Capabilities.None; }
            set
            {
                if (settings == null || settings.ExcludedCapabilities == value) return;
                settings.ExcludedCapabilities = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.ExcludedCapabilities);
                RefreshTerminal();
            }
        }

        public static string CapabilityName(Capabilities c)
        {
            switch (c)
            {
                case Capabilities.CanDock:              return "Dock";
                case Capabilities.CanWeld:              return "Weld";
                case Capabilities.CanGrind:             return "Grind";
                case Capabilities.CanDrill:             return "Drill";
                case Capabilities.CanScoutOre:          return "Scout ore";
                case Capabilities.HasWheels:            return "Wheels";
                case Capabilities.CanFlyAtmosphere:     return "Fly in atmosphere";
                case Capabilities.CanFlySpace:          return "Fly in space";
                case Capabilities.HasDefenseSystems:    return "Defense systems";
                case Capabilities.HasSensors:           return "Sensors";
                case Capabilities.HasCameras:           return "Cameras";
                case Capabilities.CargoVolumeNil:       return "Cargo: none";
                case Capabilities.CargoVolumeLt10m3:    return "Cargo: < 10 m³";
                case Capabilities.CargoVolumeLt100m3:   return "Cargo: 10-100 m³";
                case Capabilities.CargoVolumeLt1000m3:  return "Cargo: 100-1000 m³";
                case Capabilities.CargoVolumeLt10000m3: return "Cargo: 1000 m³ or more";
                case Capabilities.LiftNil:              return "Lift: none";
                case Capabilities.LiftLt1t:             return "Lift: < 1 t";
                case Capabilities.LiftLt10t:            return "Lift: 1-10 t";
                case Capabilities.LiftLt100t:           return "Lift: 10-100 t";
                case Capabilities.LiftLt1000t:          return "Lift: 100 t or more";
                default:                                return c.ToString();
            }
        }

        // First list: detected and not excluded
        public void Terminal_CapabilityListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            FillCapabilityList(items, selected, capabilities & ~Terminal_ExcludedCapabilities, capabilitySelection);
        }

        // Second list: excluded (kept even when the drone no longer has it, so the choice survives a refit)
        public void Terminal_ExcludedListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            FillCapabilityList(items, selected, Terminal_ExcludedCapabilities, exclusionSelection);
        }

        private static void FillCapabilityList(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected,
                                               Capabilities shown, Capabilities selection)
        {
            for (int i = 0; i < ALL_CAPABILITIES.Length; i++)
            {
                var c = ALL_CAPABILITIES[i];
                if ((shown & c) == 0) continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(CapabilityName(c)), MyStringId.NullOrEmpty, (uint)c);
                items.Add(item);
                if ((selection & c) != 0) selected.Add(item);
            }
        }

        public void Terminal_SelectCapabilities(List<MyTerminalControlListBoxItem> selected)
        {
            capabilitySelection = SelectionOf(selected);
        }

        public void Terminal_SelectExclusions(List<MyTerminalControlListBoxItem> selected)
        {
            exclusionSelection = SelectionOf(selected);
        }

        private static Capabilities SelectionOf(List<MyTerminalControlListBoxItem> selected)
        {
            Capabilities s = Capabilities.None;
            if (selected == null) return s;
            for (int i = 0; i < selected.Count; i++)
                if (selected[i].UserData is uint) s |= (Capabilities)(uint)selected[i].UserData;
            return s;
        }

        public void Terminal_ExcludeSelected()
        {
            var add = capabilitySelection & capabilities;
            capabilitySelection = Capabilities.None;
            if (add != Capabilities.None) Terminal_ExcludedCapabilities |= add;
            else RefreshTerminal();
        }

        public void Terminal_RemoveSelectedExclusions()
        {
            var remove = exclusionSelection;
            exclusionSelection = Capabilities.None;
            if (remove != Capabilities.None) Terminal_ExcludedCapabilities &= ~remove;
            else RefreshTerminal();
        }

        public void Terminal_ClearExclusions()
        {
            capabilitySelection = exclusionSelection = Capabilities.None;
            Terminal_ExcludedCapabilities = Capabilities.None;
        }
        #endregion
    }
}
