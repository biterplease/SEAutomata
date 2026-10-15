using Automata.Util.Logging;
using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Network;
using Automata.VirtualNetwork;
using Automata.Util;

namespace Automata.Drone
{
    /// <summary>
    /// Terminal-facing state added with the control layout rework: AI on/off, operation mode side effects,
    /// "home connector is not a station" + home beacon, behaviour profile, excluded capabilities.
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrSettingsCopied = MyStringId.GetOrCompute("Drone settings copied from {0}");
        private static readonly MyStringId StrSettingsOfPastedOnto = MyStringId.GetOrCompute("Settings of {0} pasted onto {1}{2}");
        private static readonly MyStringId StrTheDrone = MyStringId.GetOrCompute("the drone");
        private static readonly MyStringId StrOperationModeKeptSwitchThe = MyStringId.GetOrCompute(" (operation mode kept: switch the AI off to change it)");
        private static readonly MyStringId StrAIOff = MyStringId.GetOrCompute("AI off");
        private static readonly MyStringId StrAIDisabled = MyStringId.GetOrCompute("AI disabled");
        private static readonly MyStringId StrHomeBeacon = MyStringId.GetOrCompute("Home beacon: {0}");
        private static readonly MyStringId StrSetLower = MyStringId.GetOrCompute("set");
        private static readonly MyStringId StrHomeBeaconCleared = MyStringId.GetOrCompute("Home beacon cleared");
        private static readonly MyStringId StrDock = MyStringId.GetOrCompute("Dock");
        private static readonly MyStringId StrWeld = MyStringId.GetOrCompute("Weld");
        private static readonly MyStringId StrGrind = MyStringId.GetOrCompute("Grind");
        private static readonly MyStringId StrDrill = MyStringId.GetOrCompute("Drill");
        private static readonly MyStringId StrScoutOre = MyStringId.GetOrCompute("Scout ore");
        private static readonly MyStringId StrWheels = MyStringId.GetOrCompute("Wheels");
        private static readonly MyStringId StrFlyInAtmosphere = MyStringId.GetOrCompute("Fly in atmosphere");
        private static readonly MyStringId StrFlyInSpace = MyStringId.GetOrCompute("Fly in space");
        private static readonly MyStringId StrCombatAIDefensiveFlightOffensive = MyStringId.GetOrCompute("Combat (AI Defensive, Flight, Offensive)");
        private static readonly MyStringId StrSensors = MyStringId.GetOrCompute("Sensors");
        private static readonly MyStringId StrCameras = MyStringId.GetOrCompute("Cameras");
        private static readonly MyStringId StrCargoNone = MyStringId.GetOrCompute("Cargo: none");
        private static readonly MyStringId StrCargo10M = MyStringId.GetOrCompute("Cargo: < 10 m³");
        private static readonly MyStringId StrCargo10100M = MyStringId.GetOrCompute("Cargo: 10-100 m³");
        private static readonly MyStringId StrCargo1001000M = MyStringId.GetOrCompute("Cargo: 100-1000 m³");
        private static readonly MyStringId StrCargo1000MOrMore = MyStringId.GetOrCompute("Cargo: 1000 m³ or more");
        private static readonly MyStringId StrLiftNone = MyStringId.GetOrCompute("Lift: none");
        private static readonly MyStringId StrLift1T = MyStringId.GetOrCompute("Lift: < 1 t");
        private static readonly MyStringId StrLift110T = MyStringId.GetOrCompute("Lift: 1-10 t");
        private static readonly MyStringId StrLift10100T = MyStringId.GetOrCompute("Lift: 10-100 t");
        private static readonly MyStringId StrLift100TOrMore = MyStringId.GetOrCompute("Lift: 100 t or more");
        private static readonly MyStringId StrNoneListItem = MyStringId.GetOrCompute("(none)");
        #endregion

        #region Copy / paste settings (terminal, per player)
        // What "Copy settings" took: one setting packet per key, as the network sends them. Local to this player's game.
        private static List<DroneSettingPacket> settingsClipboard;
        private static string settingsClipboardFrom;

        // Per drone, never copied: AI on/off, the home connector / beacon and its "not a station" flag (one drone each)
        private static bool CopiesSetting(DroneSettingKey k)
        {
            switch (k)
            {
                case DroneSettingKey.None:
                case DroneSettingKey.Enabled:
                case DroneSettingKey.Retired_EnableInertialDampening:
                case DroneSettingKey.HomeConnector:
                case DroneSettingKey.HomeBeacon:
                case DroneSettingKey.HomeNotStatic:
                case DroneSettingKey.Nickname:
                    return false;
            }
            return true;
        }

        public static bool Terminal_HasCopiedSettings { get { return settingsClipboard != null; } }
        public static string Terminal_CopiedFrom { get { return settingsClipboardFrom ?? ""; } }

        public void Terminal_CopySettings()
        {
            if (settings == null || Entity == null) return;
            var list = new List<DroneSettingPacket>();
            // Every key (the last one: keep this bound in step with DroneSettingKey)
            for (ushort v = 1; v <= (ushort)DroneSettingKey.Nickname; v++)
            {
                var k = (DroneSettingKey)v;
                if (!CopiesSetting(k)) continue;
                var p = new DroneSettingPacket { Key = k };
                FillSettingPacket(p);
                list.Add(p);
            }
            // The operation mode first: it only changes with the AI off, and the profile / sections depend on it
            list.Sort((a, b) => (a.Key == DroneSettingKey.OperationModeValue ? 0 : 1).CompareTo(b.Key == DroneSettingKey.OperationModeValue ? 0 : 1));
            settingsClipboard = list;
            settingsClipboardFrom = block is IMyTerminalBlock ? ((IMyTerminalBlock)block).CustomName : Entity.EntityId.ToString();
            MyAPIGateway.Utilities.ShowNotification(Loc.F(StrSettingsCopied, settingsClipboardFrom), 2000);
            RefreshTerminal();   // "Paste settings" becomes available
        }

        // Applied through the same setters as the terminal (clamping, side effects, sync to the server and the others)
        public void Terminal_PasteSettings()
        {
            if (settingsClipboard == null || settings == null || Entity == null) return;
            bool modeSkipped = false;
            for (int i = 0; i < settingsClipboard.Count; i++)
            {
                var src = settingsClipboard[i];
                if (src.Key == DroneSettingKey.OperationModeValue && settings.IsEnabled && (long)src.Number != (long)settings.OperationMode) { modeSkipped = true; continue; }
                var p = new DroneSettingPacket { EntityId = Entity.EntityId, Key = src.Key, Number = src.Number, Text = src.Text, Id = src.Id, VectorA = src.VectorA, VectorB = src.VectorB };
                ApplySettingPacket(p);
                SyncSetting(p.Key);
            }
            MyAPIGateway.Utilities.ShowNotification(Loc.F(StrSettingsOfPastedOnto, settingsClipboardFrom,
                block is IMyTerminalBlock ? ((IMyTerminalBlock)block).CustomName : Loc.T(StrTheDrone), modeSkipped ? Loc.T(StrOperationModeKeptSwitchThe) : ""), 3000);
            RefreshTerminal();
        }
        #endregion

        #region AI on / off, operation mode
        // Server: everything off, hands off the controls. The game's dampeners (switched back on when the
        // overrides are cleared) hold the drone where it is, hovering if it was flying.
        private void DisableAI()
        {
            EndCombat(Loc.T(StrAIOff));
            StopJob(Loc.T(StrAIOff));
            Shutdown();
            preflightStage = 0;
            // Hand the drone back flyable: blocks the dock routine put to sleep (thrusters, tools...) come back on
            // and batteries / tanks leave Recharge / Stockpile
            // and batteries / tanks go to Auto / no stockpile (always, not only those it switched)
            WakeSystems();
            isParked = IsParkedNow();
            RestoreConnectors();
            SetState(isParked ? State.Docked : State.Standby);
            Report(StrAIDisabled);
        }

        // Server: side effects of the current mode (called when the AI goes on; the mode can't change while on)
        private void ApplyOperationMode()
        {
            if (!IsServer || settings == null) return;
            var queue = AutomataSession.GetMessageQueue();
            Log.Debug("Drone {0}: mode {1} applied (AI {2}){3}", Entity.EntityId, settings.OperationMode, settings.IsEnabled ? "on" : "off",
                      settings.OperationMode == OperationMode.ManagedByOrchestrator ? (primaryAntenna != null ? ", auctions subscribed, antenna '" + primaryAntenna.CustomName + "'" : ", auctions subscribed, NO antenna") : "");
            if (queue != null)
            {
                if (settings.OperationMode == OperationMode.ManagedByOrchestrator)
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
            if (IsCombatMode)
            {
                capabilitiesDirty = true;   // the AI blocks are required now
                CombatBehavioursOff();      // ours until a detection
            }
        }

        /// <summary>Re-evaluates the terminal's enabled / visible states (after a setting other controls depend on).</summary>
        public void Terminal_Refresh()
        {
            RefreshTerminal();
        }

        public bool IsManagedByPlayer { get { return settings != null && settings.OperationMode == OperationMode.ManagedByPlayer; } }
        #endregion

        #region Home connector is not a station: home beacon
        public bool Terminal_HomeNotStatic
        {
            get { return settings != null && settings.HomeNotStatic; }
            set
            {
                if (settings == null || settings.HomeNotStatic == value) return;
                settings.HomeNotStatic = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.HomeNotStatic);
                RefreshTerminal();   // beacon list enables with this
            }
        }

        public void Terminal_HomeBeaconListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var none = new MyTerminalControlListBoxItem(StrNoneListItem, MyStringId.NullOrEmpty, 0L);
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
                Report(StrHomeBeacon, beacon != null ? beacon.CustomName : Loc.T(StrSetLower));
            }
            else Report(StrHomeBeaconCleared);
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

        #region Local-job radius
        public const float LOCAL_JOB_RADIUS_MIN = 1f, LOCAL_JOB_RADIUS_MAX = 30000f;

        /// <summary>m from its base within which the drone bids on jobs.</summary>
        public float Terminal_LocalJobRadius
        {
            get { return settings != null ? settings.LocalJobRadius : 15000f; }
            set
            {
                if (settings == null) return;
                float v = VRageMath.MathHelper.Clamp((float)Math.Round(value), LOCAL_JOB_RADIUS_MIN, LOCAL_JOB_RADIUS_MAX);
                if (v == settings.LocalJobRadius) return;
                settings.LocalJobRadius = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.LocalJobRadius);
            }
        }
        #endregion

        #region Min altitude
        public const float MIN_ALTITUDE_MAX = 1000f;

        public float Terminal_MinAltitude
        {
            get { return settings != null ? settings.MinAltitude : 50f; }
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

        #region Combat handover
        // 2000 m: the vanilla AI blocks' detection range; handing over farther out, the Offensive block finds nothing
        // and the drone comes straight back
        public const float COMBAT_HANDOVER_MIN = 200f, COMBAT_HANDOVER_MAX = 2000f;
        public const float COMBAT_NO_TARGET_MIN = 5f, COMBAT_NO_TARGET_MAX = 120f;
        public const float AUTO_LAUNCH_RADIUS_MAX = 2000f;
        public const float ESCORT_FOLLOW_MIN = 10f, ESCORT_FOLLOW_MAX = 1000f;

        /// <summary>Distance to the threat at which the vanilla AI Flight + Offensive blocks take over.</summary>
        public float Terminal_CombatHandoverDistance
        {
            get { return settings != null ? settings.CombatHandoverDistance : 2000f; }
            set
            {
                if (settings == null) return;
                float v = VRageMath.MathHelper.Clamp((float)Math.Round(value / 50f) * 50f, COMBAT_HANDOVER_MIN, COMBAT_HANDOVER_MAX);
                if (v == settings.CombatHandoverDistance) return;
                settings.CombatHandoverDistance = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.CombatHandoverDistance);
            }
        }

        /// <summary>Seconds without an Offensive target before the drone takes control back and flies home.</summary>
        public float Terminal_CombatNoTargetSeconds
        {
            get { return settings != null ? settings.CombatNoTargetSeconds : 15f; }
            set
            {
                if (settings == null) return;
                int v = (int)VRageMath.MathHelper.Clamp((float)Math.Round(value), COMBAT_NO_TARGET_MIN, COMBAT_NO_TARGET_MAX);
                if (v == settings.CombatNoTargetSeconds) return;
                settings.CombatNoTargetSeconds = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.CombatNoTargetSeconds);
            }
        }

        /// <summary>Enemies the AI Defensive block detects within this distance of the drone launch it (0 = never on its own).</summary>
        public float Terminal_AutoLaunchRadius
        {
            get { return settings != null ? settings.AutoLaunchRadius : AUTO_LAUNCH_RADIUS_MAX; }
            set
            {
                if (settings == null) return;
                float v = VRageMath.MathHelper.Clamp((float)Math.Round(value / 50f) * 50f, 0f, AUTO_LAUNCH_RADIUS_MAX);
                if (v == settings.AutoLaunchRadius) return;
                settings.AutoLaunchRadius = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.AutoLaunchRadius);
            }
        }

        /// <summary>Escort jobs: distance between the escorted grid / character and the drone's station above it.</summary>
        public float Terminal_EscortFollowDistance
        {
            get { return settings != null ? settings.EscortFollowDistance : 30f; }
            set
            {
                if (settings == null) return;
                float v = VRageMath.MathHelper.Clamp((float)Math.Round(value), ESCORT_FOLLOW_MIN, ESCORT_FOLLOW_MAX);
                if (v == settings.EscortFollowDistance) return;
                settings.EscortFollowDistance = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.EscortFollowDistance);
            }
        }

        public bool Terminal_EconomyFlight
        {
            get { return settings != null && settings.EconomyFlight; }
            set
            {
                if (settings == null || settings.EconomyFlight == value) return;
                settings.EconomyFlight = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.EconomyFlight);
            }
        }

        public bool Terminal_CombatRetreatOnFlee
        {
            get { return settings == null || settings.CombatRetreatOnFlee; }
            set
            {
                if (settings == null || settings.CombatRetreatOnFlee == value) return;
                settings.CombatRetreatOnFlee = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.CombatRetreatOnFlee);
            }
        }

        public bool Terminal_CombatRetreatOnLowPower
        {
            get { return settings == null || settings.CombatRetreatOnLowPower; }
            set
            {
                if (settings == null || settings.CombatRetreatOnLowPower == value) return;
                settings.CombatRetreatOnLowPower = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.CombatRetreatOnLowPower);
            }
        }
        #endregion

        #region Ore survey
        /// <summary>Record ores the drone's ore detector sees and hand them to a mining surveyor in range.</summary>
        public bool Terminal_SurveyForOres
        {
            get { return settings != null && settings.SurveyForOres; }
            set
            {
                if (settings == null || settings.SurveyForOres == value) return;
                settings.SurveyForOres = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.SurveyForOres);
            }
        }
        #endregion

        public bool Terminal_UseBroadcastController
        {
            get { return settings != null && settings.UseBroadcastController; }
            set
            {
                if (settings == null || settings.UseBroadcastController == value) return;
                settings.UseBroadcastController = value;
                SaveSettings();
                SyncSetting(DroneSettingKey.UseBroadcastController);
            }
        }

        private static bool IsKnownProfile(BehaviourProfile v)
        {
            switch (v)
            {
                case BehaviourProfile.None:
                case BehaviourProfile.IsCargoDrone:
                case BehaviourProfile.IsOreScout:
                case BehaviourProfile.IsRover:
                case BehaviourProfile.IsCombatDrone:
                case BehaviourProfile.IsOreHauler:
                case BehaviourProfile.IsWelderDrone:
                case BehaviourProfile.IsWelderRepair:
                case BehaviourProfile.IsWelderProjected:
                case BehaviourProfile.IsWelderUnbuilt:
                case BehaviourProfile.IsGrinderDrone:
                case BehaviourProfile.IsWelderGrinderDrone:
                case BehaviourProfile.IsMiningDrone:
                case BehaviourProfile.IsFarmingDrone:
                    return true;
                default:
                    return false;
            }
        }

        #region Behavior profile (orchestrators prefer matching drones; rover navigation not implemented yet)
        /// <summary>The drone's behaviour profile (designator tagging: ore scouts, combat drones).</summary>
        public BehaviourProfile Profile { get { return settings != null ? settings.BehaviourProfile : BehaviourProfile.None; } }

        public long Terminal_BehaviourProfileValue
        {
            get { return settings != null ? (long)settings.BehaviourProfile : 0L; }
            set
            {
                if (settings == null) return;
                var v = (BehaviourProfile)(ushort)value;
                if (!IsKnownProfile(v)) return;
                if (settings.BehaviourProfile == v) return;
                settings.BehaviourProfile = v;
                SaveSettings();
                SyncSetting(DroneSettingKey.BehaviourProfile);
                capabilitiesDirty = true;   // ore scouts need an ore detector, combat drones the three AI blocks; small connectors
                if (v == BehaviourProfile.IsOreScout && !settings.SurveyForOres)
                    Terminal_SurveyForOres = true;   // scouts record what they see on every flight
                RefreshTerminal();                   // combat drones show the combat handover settings
            }
        }
        #endregion

        #region Ignored ores / crops (two lists: known, ignored; add / remove buttons)
        public const string ORE_TYPE_ID = "MyObjectBuilder_Ore";
        public const string CROP_TYPE_ID = "MyObjectBuilder_SeedItem";
        private static readonly List<KeyValuePair<string, string>> knownItems = new List<KeyValuePair<string, string>>();
        // Listbox selections: local to this player's terminal, never synced. Static: one terminal is open at a time, and with
        // several drones selected every drone's button action must use what the player sees selected (not an older,
        // invisible selection of its own). Pruned to what the list shows, so nothing hidden is ever added / removed.
        private static readonly List<string> oreKnownSelection = new List<string>(), oreIgnoredSelection = new List<string>();
        private static readonly List<string> cropKnownSelection = new List<string>(), cropIgnoredSelection = new List<string>();

        private List<string> IgnoreList(bool crops)
        {
            if (settings == null) return new List<string>();
            if (crops) { if (settings.IgnoredCrops == null) settings.IgnoredCrops = new List<string>(); return settings.IgnoredCrops; }
            if (settings.IgnoredOres == null) settings.IgnoredOres = new List<string>();
            return settings.IgnoredOres;
        }

        /// <summary>First list: every ore / crop the game knows that is not ignored.</summary>
        public void Terminal_KnownListContent(bool crops, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var ignored = IgnoreList(crops);
            var sel = crops ? cropKnownSelection : oreKnownSelection;
            sel.RemoveAll(k => ignored.Contains(k));
            AutomataSession.ListItemsOfType(crops ? CROP_TYPE_ID : ORE_TYPE_ID, knownItems);
            for (int i = 0; i < knownItems.Count; i++)
            {
                if (ignored.Contains(knownItems[i].Key)) continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(knownItems[i].Value), MyStringId.GetOrCompute(knownItems[i].Key), knownItems[i].Key);
                items.Add(item);
                if (sel.Contains(knownItems[i].Key)) selected.Add(item);
            }
            knownItems.Clear();
        }

        /// <summary>Second list: the ignored ones.</summary>
        public void Terminal_IgnoredListContent(bool crops, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var ignored = IgnoreList(crops);
            var sel = crops ? cropIgnoredSelection : oreIgnoredSelection;
            sel.RemoveAll(k => !ignored.Contains(k));
            for (int i = 0; i < ignored.Count; i++)
            {
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(AutomataSession.ItemDisplayName(ignored[i])), MyStringId.GetOrCompute(ignored[i]), ignored[i]);
                items.Add(item);
                if (sel.Contains(ignored[i])) selected.Add(item);
            }
        }

        public void Terminal_SelectKnown(bool crops, List<MyTerminalControlListBoxItem> selected)
        {
            KeysOf(selected, crops ? cropKnownSelection : oreKnownSelection);
        }

        public void Terminal_SelectIgnored(bool crops, List<MyTerminalControlListBoxItem> selected)
        {
            KeysOf(selected, crops ? cropIgnoredSelection : oreIgnoredSelection);
        }

        private static void KeysOf(List<MyTerminalControlListBoxItem> selected, List<string> into)
        {
            into.Clear();
            if (selected == null) return;
            for (int i = 0; i < selected.Count; i++)
            {
                var key = selected[i].UserData as string;
                if (key != null) into.Add(key);
            }
        }

        /// <summary>"Add": the selected entries of the first list go to the ignored list.</summary>
        public void Terminal_AddToIgnore(bool crops)
        {
            var sel = crops ? cropKnownSelection : oreKnownSelection;
            var list = new List<string>(IgnoreList(crops));
            for (int i = 0; i < sel.Count; i++) if (!list.Contains(sel[i])) list.Add(sel[i]);
            SetIgnoreList(crops, list);
        }

        /// <summary>"Remove": the selected entries of the ignored list go back.</summary>
        public void Terminal_RemoveFromIgnore(bool crops)
        {
            var sel = crops ? cropIgnoredSelection : oreIgnoredSelection;
            var list = new List<string>(IgnoreList(crops));
            for (int i = 0; i < sel.Count; i++) list.Remove(sel[i]);
            SetIgnoreList(crops, list);
        }

        private void SetIgnoreList(bool crops, List<string> keys)
        {
            if (settings == null) return;
            if (crops) settings.IgnoredCrops = keys; else settings.IgnoredOres = keys;
            SaveSettings();
            SyncSetting(crops ? DroneSettingKey.IgnoredCrops : DroneSettingKey.IgnoredOres);
            RefreshTerminal();
        }

        private static List<string> SplitKeys(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            var parts = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) { var k = parts[i].Trim(); if (k.Length > 0 && !list.Contains(k)) list.Add(k); }
            return list;
        }

        /// <summary>The drone won't take mining jobs for these ores ('ore': item key "Ore/Iron").</summary>
        private bool IsOreIgnored(string ore)
        {
            return settings != null && settings.IgnoredOres != null && settings.IgnoredOres.Contains(ore);
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
        // Static for the same reason as the ignore-list selections (several drones selected: one visible selection)
        private static Capabilities capabilitySelection, exclusionSelection;

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
                case Capabilities.CanDock:              return Loc.T(StrDock);
                case Capabilities.CanWeld:              return Loc.T(StrWeld);
                case Capabilities.CanGrind:             return Loc.T(StrGrind);
                case Capabilities.CanDrill:             return Loc.T(StrDrill);
                case Capabilities.CanScoutOre:          return Loc.T(StrScoutOre);
                case Capabilities.HasWheels:            return Loc.T(StrWheels);
                case Capabilities.CanFlyAtmosphere:     return Loc.T(StrFlyInAtmosphere);
                case Capabilities.CanFlySpace:          return Loc.T(StrFlyInSpace);
                case Capabilities.HasDefenseSystems:    return Loc.T(StrCombatAIDefensiveFlightOffensive);
                case Capabilities.HasSensors:           return Loc.T(StrSensors);
                case Capabilities.HasCameras:           return Loc.T(StrCameras);
                case Capabilities.CargoVolumeNil:       return Loc.T(StrCargoNone);
                case Capabilities.CargoVolumeLt10m3:    return Loc.T(StrCargo10M);
                case Capabilities.CargoVolumeLt100m3:   return Loc.T(StrCargo10100M);
                case Capabilities.CargoVolumeLt1000m3:  return Loc.T(StrCargo1001000M);
                case Capabilities.CargoVolumeLt10000m3: return Loc.T(StrCargo1000MOrMore);
                case Capabilities.LiftNil:              return Loc.T(StrLiftNone);
                case Capabilities.LiftLt1t:             return Loc.T(StrLift1T);
                case Capabilities.LiftLt10t:            return Loc.T(StrLift110T);
                case Capabilities.LiftLt100t:           return Loc.T(StrLift10100T);
                case Capabilities.LiftLt1000t:          return Loc.T(StrLift100TOrMore);
                default:                                return c.ToString();
            }
        }

        // First list: detected and not excluded
        public void Terminal_CapabilityListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            capabilitySelection &= capabilities & ~Terminal_ExcludedCapabilities;   // only what is shown
            FillCapabilityList(items, selected, capabilities & ~Terminal_ExcludedCapabilities, capabilitySelection);
        }

        // Second list: excluded (kept even when the drone no longer has it, so the choice survives a refit)
        public void Terminal_ExcludedListContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            exclusionSelection &= Terminal_ExcludedCapabilities;
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
            if (add != Capabilities.None) Terminal_ExcludedCapabilities |= add;
            else RefreshTerminal();
        }

        public void Terminal_RemoveSelectedExclusions()
        {
            var remove = exclusionSelection & Terminal_ExcludedCapabilities;
            if (remove != Capabilities.None) Terminal_ExcludedCapabilities &= ~remove;
            else RefreshTerminal();
        }

        public void Terminal_ClearExclusions()
        {
            capabilitySelection = exclusionSelection = Capabilities.None;
            Terminal_ExcludedCapabilities = Capabilities.None;
        }
        #endregion

        #region Debug log (terminal button)
        /// <summary>DEBUGLOG button: asks the server to write the drone's state to the mod log (any mode, any moment).</summary>
        public void Terminal_Debug_Log()
        {
            RequestAction(DroneAction.DebugLog);
        }

        // Server: one block of Log.Debug lines, "DEBUGLOG <id>" first on each, so a grep takes them all. Positions are GPS strings
        // (paste into the game to see where). Meant for following a job (mining) step by step: press it at different points.
        private void DebugLogSnapshot(ulong requester)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            CaptureFlightState();
            long id = Entity.EntityId;
            string tag = "DEBUGLOG " + id;
            Vector3D pos = flightState.Position;
            Log.Debug("{0} @frame {1} by {2}: {3} '{4}' state {5}, mode {6}, AI {7}{8}", tag, now, requester, Entity.DisplayName, ((IMyCubeBlock)Entity).CubeGrid.DisplayName,
                      currentState, settings.OperationMode, settings.IsEnabled ? "on" : "off", string.IsNullOrEmpty(errorReason) ? "" : ", error: " + errorReason);

            // Where it is and how it moves
            double height = Pathfinding.PathfindingManager.HeightAboveTerrain(pos);
            Log.Debug("{0} pos {1} | speed {2:F2} m/s (relative {3:F2}), spin {4:F2} rad/s | gravity {5:F2} m/s2, {6} | terrain {7} | speed cap {8} {9:F1}", tag,
                      ConvertVectorToGPS("drone", pos), flightState.LinearVelocity.Length(), flightState.RelativeVelocity.Length(), flightState.AngularVelocity.Length(),
                      flightState.Gravity.Length(), flightState.InGravity ? "in gravity" : "no gravity",
                      height == double.MaxValue ? "none below" : height.ToString("F1") + " m below the hull",
                      string.IsNullOrEmpty(speedCapName) ? "-" : speedCapName, speedCapValue);

            // The workload: job, task, where in it
            if (currentJob == null || currentJob.Tasks == null)
            {
                Log.Debug("{0} job: none (queue {1} task(s))", tag, taskQueue.Count);
            }
            else
            {
                var tasks = currentJob.Tasks;
                var plan = new StringBuilder();
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (i > 0) plan.Append('>');
                    if (i == taskIndex) plan.Append('[').Append(tasks[i].Type).Append(']'); else plan.Append(tasks[i].Type);
                }
                Log.Debug("{0} job: {1} #{2}, {3} block(s), done {4}, task {5}/{6}, {7}, {8} ticks in the task, {9} since its last progress, last failure {10}", tag,
                          currentJob.JobType, currentJob.JobId, currentJob.BlockCount, jobBlocksDone, taskIndex + 1, tasks.Count,
                          taskStarted ? "running" : "not started", now - taskStartFrame, now - taskProgressFrame, taskFailure);
                Log.Debug("{0} tasks: {1}", tag, plan);
                if (taskIndex >= 0 && taskIndex < tasks.Count)
                {
                    var t = tasks[taskIndex];
                    Vector3D at = t.Position.ToVector3D();
                    Log.Debug("{0} task {1}: target {2} ({3:F1} m away), approach from {4}, target entity {5}, completion {6} {7}", tag, t.Type,
                              ConvertVectorToGPS("task", at), Vector3D.Distance(pos, at), ConvertVectorToGPS("from", t.ApproachFrom.ToVector3D()),
                              t.TargetEntityId, t.Completion, t.CompletionValue);
                }
            }

            // The flight order and its legs
            var o = activeFlightOrder;
            if (o == null) Log.Debug("{0} flight: no order (queued legs {1}, construction legs {2})", tag, queuedLegs.Count, constructionLegs.Count);
            else
            {
                Vector3D target = o.Target.ToVector3D();
                Log.Debug("{0} flight: order {1} phase {2}, target {3} ({4:F1} m), captured {5}, arrived {6}, tolerance {7:F1}, final speed {8:F1}, cap {9:F1}, behaviour {10}{11}; " +
                          "queued legs {12}, construction legs {13}, owner {14}", tag, o == constructionOrder ? "(construction)" : "", o.Phase, ConvertVectorToGPS("target", target),
                          Vector3D.Distance(pos, target), o.Captured, o.Arrived, o.ArrivalTolerance, o.FinalSpeed, o.SpeedCap, o.Behavior,
                          o.LooseArrival ? ", loose arrival" : "", queuedLegs.Count, constructionLegs.Count, routeOwner);
            }

            // Mining: the bore and what is aboard
            bool mining = currentJob != null && currentJob.JobType == Orchestrator.JobType.MineOre;
            if (mining || boring || boreOre != MyStringHash.NullOrEmpty)
            {
                Vector3D tool = drill != null ? ToolPointWorld() : pos;
                Log.Debug("{0} mine: ore {1}, boring {2}, collecting {3}, extensions {4}/{5}, tool {6}, deposit {7} ({8:F1} m from the tool), bore end {9} ({10:F1} m), " +
                          "best {11:F1} m, ticks since cargo grew {12}, ore grew {13}, bore progress {14}", tag, boreOre.String, boring, boreCollecting, boreExtensions,
                          MINE_BORE_EXTENSIONS, ConvertVectorToGPS("tool", tool), ConvertVectorToGPS("deposit", boreDeposit), Vector3D.Distance(tool, boreDeposit),
                          ConvertVectorToGPS("end", boreEnd), Vector3D.Distance(tool, boreEnd), boreBest, now - boreGainFrame, now - oreGainFrame, now - boreProgressFrame);
                int drillsOn = 0, clearing = 0;
                for (int i = 0; i < drills.Count; i++)
                {
                    if (drills[i] == null || drills[i].Closed) continue;
                    if (drills[i].Enabled) drillsOn++;
                    if (drills[i].TerrainClearingMode) clearing++;
                }
                Log.Debug("{0} mine: ore aboard {1:F0} kg of {2}, delivered {3:F0} kg, drills {4}/{5} on, {6} clearing terrain, ore detector {7}, started at {8}", tag, MinedOre(),
                          boreOre.String, minedOreKg, drillsOn, drills.Count, OreDetectorWorking ? "working" : "not working",
                          boreStartSet ? ConvertVectorToGPS("start", boreStart) : "-");
            }

            // Cargo, mass, power
            Log.Debug("{0} cargo: {1:P0} full ({2:F2} m3 in {3} container(s)), load limit {4}, mass {5:F0} kg (base {6:F0}) | battery {7:F0}%{8}, hydrogen {9:F0}%{10}", tag,
                      CargoFillRatio(), CargoUsedVolume(), cargoContainers.Count, loadLevel == 0 ? "ok" : loadLevel == 1 ? "reached" : "exceeded", physicalMass, baseMass,
                      currentBatteryLevel, needsBatteryRecharge ? " (needs recharge)" : "", currentH2Level, needsHydrogenRefuel ? " (needs refuel)" : "");
        }
        #endregion
    }
}
