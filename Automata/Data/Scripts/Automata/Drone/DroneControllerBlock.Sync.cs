using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Network;
using Automata.Util;
using Automata.Util.Logging;
using Digi.NetworkLib;

namespace Automata.Drone
{
    /// <summary>Drone settings that players change from the terminal (one packet per changed key).</summary>
    public enum DroneSettingKey : ushort
    {
        None = 0,
        Enabled,
        Retired_EnableInertialDampening,   // keeps the numbering; the drone always holds itself now
        OperationModeValue,
        MaxSpeed,
        ApproachSpeed,
        SafeSpeed,
        MaxLoadGravity,
        MaxLoadSpace,
        LcdForeground,
        LcdBackground,
        LcdFontSize,
        LcdShowHeader,
        LCDScreenTag,
        AlignToPGravity,
        MaxPitchDegrees,
        MaxRollDegrees,
        MonitorHydrogen,
        H2RefuelThreshold,
        H2OperationalThreshold,
        AlwaysRefuel,
        MonitorBattery,
        BatteryRefuelThreshold,
        BatteryOperationalThreshold,
        ObservationArea,          // size + offset, in blocks
        HomeConnector,            // server -> clients only (selection is an action, validated on the server)
        ConstructionWorkModes,
        GrindColor,
        WaypointTolerance,
        ExcludedCapabilities,
        HomeNotStation,
        HomeBeacon,               // server -> clients only (selection is an action, validated on the server)
        BehaviourProfile,
        TaskTimeout,
    }

    /// <summary>Things a player can order the drone to do from the terminal.</summary>
    public enum DroneAction : byte
    {
        None = 0,
        NavigateTo,               // VectorA target, VectorB optional approach point
        QueueWaypoint,            // VectorA
        Orient,                   // VectorA point to face
        PlaceMount,               // Code = mount code, VectorA target
        NavigateRelative,         // Id = anchor, VectorA offset (right, up, forward)
        GoHome,
        ScanAnchors,
        AddConnectorByName,       // Text
        SelectHomeConnector,      // Id (0 = none)
        SetCurrentAsHome,
        RequestAnchorList,
        StopOrders,
        SelectHomeBeacon,         // Id (0 = none)
        DrawNavigation,           // Code 1 = send me the route (debug draw timeout), 0 = stop
    }

    /// <summary>
    /// Client/server wiring. The server (or single player / host) owns all simulation: flight, docking, construction,
    /// block state. Clients mirror settings for the terminal UI and send requests.
    ///
    /// - Settings: every terminal setter applies locally and calls SyncSetting(key). Changes are batched and flushed
    ///   every 10 ticks (slider drags become one packet per 10 ticks). Client -> server: validated (terminal access),
    ///   applied through the same setter, relayed to the other clients. Server -> everyone for changes made there.
    /// - Actions: terminal buttons build a DroneActionPacket and always call Net.SendToServer. On the server (and in
    ///   single player / host) that short-circuits to a direct call, so there is one code path for every mode.
    /// - Log lines: Report() runs on the server only; lines are relayed to clients (DroneLogPacket).
    /// - Connector lists: scanned on the server only, sent to the asking player (DroneAnchorListPacket).
    /// Late joiners get the settings from the block's saved data, which is streamed with the entity.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const int ANCHOR_LIST_REFRESH_TICKS = 5 * 60;   // clients re-request a shown list at most every 5 s

        private bool applyingRemote;          // applying a received setting: don't send it again
        private readonly Dictionary<ulong, int> anchorListRequests = new Dictionary<ulong, int>();
        private readonly List<IMyPlayer> playerBuffer = new List<IMyPlayer>();
        private const int MAX_QUEUED_LEGS = 50;
        private readonly List<DroneSettingKey> pendingSettings = new List<DroneSettingKey>();
        private int lastAnchorListRequestFrame = int.MinValue / 2;
        private readonly List<AnchorEntry> anchorListBuffer = new List<AnchorEntry>();

        private static bool IsServer { get { return MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer; } }
        private static bool IsMultiplayer { get { return MyAPIGateway.Multiplayer != null && MyAPIGateway.Multiplayer.MultiplayerActive; } }
        private static Digi.NetworkLib.Network Net { get { return AutomataSession.Instance != null ? AutomataSession.Instance.Net : null; } }

        internal static DroneControllerBlock Find(long entityId)
        {
            IMyEntity e;
            if (!MyAPIGateway.Entities.TryGetEntityById(entityId, out e) || e.GameLogic == null) return null;
            return e.GameLogic.GetAs<DroneControllerBlock>();
        }

        #region Settings
        private void SyncSetting(DroneSettingKey key)
        {
            if (applyingRemote || !IsMultiplayer) return;
            // Latest change last: "AI off, change mode, AI on" arrives at the server in that order
            pendingSettings.Remove(key);
            pendingSettings.Add(key);
        }

        // UpdateBeforeSimulation10 (clients and server)
        private void FlushSettingSync()
        {
            if (pendingSettings.Count == 0) return;
            var net = Net;
            if (net == null || settings == null || Entity == null) { pendingSettings.Clear(); return; }
            for (int i = 0; i < pendingSettings.Count; i++)
            {
                var packet = new DroneSettingPacket { EntityId = Entity.EntityId, Key = pendingSettings[i] };
                FillSettingPacket(packet);
                if (IsServer)
                {
                    Log.Debug("Net: drone {0} setting {1} -> clients", Entity.EntityId, packet.Key);
                    net.SendToEveryone(packet);
                }
                else
                {
                    Log.Debug("Net: drone {0} setting {1} -> server", Entity.EntityId, packet.Key);
                    net.SendToServer(packet);
                }
            }
            pendingSettings.Clear();
        }

        private void FillSettingPacket(DroneSettingPacket p)
        {
            var s = settings;
            switch (p.Key)
            {
                case DroneSettingKey.Enabled:                     p.Number = s.IsEnabled ? 1 : 0; break;
                case DroneSettingKey.OperationModeValue:          p.Number = (double)s.OperationMode; break;
                case DroneSettingKey.MaxSpeed:                    p.Number = s.MaxSpeed; break;
                case DroneSettingKey.ApproachSpeed:               p.Number = s.ApproachSpeed; break;
                case DroneSettingKey.SafeSpeed:                   p.Number = s.SafeSpeed; break;
                case DroneSettingKey.MaxLoadGravity:              p.Number = s.MaxLoadGravity; break;
                case DroneSettingKey.MaxLoadSpace:                p.Number = s.MaxLoadSpace; break;
                case DroneSettingKey.LcdForeground:               p.Number = s.LcdForeground; break;
                case DroneSettingKey.LcdBackground:               p.Number = s.LcdBackground; break;
                case DroneSettingKey.LcdFontSize:                 p.Number = s.LcdFontSize; break;
                case DroneSettingKey.LcdShowHeader:               p.Number = s.LcdShowHeader ? 1 : 0; break;
                case DroneSettingKey.LCDScreenTag:                p.Text = s.LCDScreenTag; break;
                case DroneSettingKey.AlignToPGravity:             p.Number = s.AlignToPGravity ? 1 : 0; break;
                case DroneSettingKey.MaxPitchDegrees:             p.Number = s.PGravityAlignMaxPitchDegrees; break;
                case DroneSettingKey.MaxRollDegrees:              p.Number = s.PGravityAlignMaxRollDegrees; break;
                case DroneSettingKey.MonitorHydrogen:             p.Number = s.MonitorHydrogenLevels ? 1 : 0; break;
                case DroneSettingKey.H2RefuelThreshold:           p.Number = s.HydrogenRefuelThreshold; break;
                case DroneSettingKey.H2OperationalThreshold:      p.Number = s.HydrogenOperationalThreshold; break;
                case DroneSettingKey.AlwaysRefuel:                p.Number = s.AlwaysRefuelWhenDocked ? 1 : 0; break;
                case DroneSettingKey.MonitorBattery:              p.Number = s.MonitorBatteryLevels ? 1 : 0; break;
                case DroneSettingKey.BatteryRefuelThreshold:      p.Number = s.BatteryRefuelThreshold; break;
                case DroneSettingKey.BatteryOperationalThreshold: p.Number = s.BatteryOperationalThreshold; break;
                case DroneSettingKey.ObservationArea:
                    p.VectorA = s.ObservationAreaSizeBlocks;
                    p.VectorB = s.ObservationAreaOffsetBlocks;
                    break;
                case DroneSettingKey.HomeConnector:               p.Id = s.HomeConnectorId; break;
                case DroneSettingKey.ConstructionWorkModes:       p.Number = (double)s.ConstructionWorkModes; break;
                case DroneSettingKey.GrindColor:                  p.Number = s.GrindColor; break;
                case DroneSettingKey.WaypointTolerance:           p.Number = s.WaypointTolerance; break;
                case DroneSettingKey.ExcludedCapabilities:        p.Number = (double)s.ExcludedCapabilities; break;
                case DroneSettingKey.HomeNotStation:              p.Number = s.HomeNotStation ? 1 : 0; break;
                case DroneSettingKey.HomeBeacon:                  p.Id = s.HomeBeaconId; break;
                case DroneSettingKey.BehaviourProfile:            p.Number = (double)s.BehaviourProfile; break;
                case DroneSettingKey.TaskTimeout:                 p.Number = s.TaskTimeoutSeconds; break;
            }
        }

        public void ReceiveSetting(DroneSettingPacket p, ref PacketInfo info, ulong senderSteamId)
        {
            if (settings == null) LoadSettings();   // not initialized yet: start from the saved data, then apply
            if (settings == null) return;
            if (double.IsNaN(p.Number) || double.IsInfinity(p.Number))
            {
                Log.Warning("Net: drone {0} setting {1} from {2} rejected (not a number)", Entity.EntityId, p.Key, senderSteamId);
                return;
            }
            if (IsServer)
            {
                long identity;
                if (!SenderHasAccess(senderSteamId, out identity))
                {
                    Log.Warning("Net: drone {0} setting {1} from {2} rejected (no terminal access)", Entity.EntityId, p.Key, senderSteamId);
                    return;
                }
                // Clients select these through actions (validated against the owner/faction filtered lists)
                if (p.Key == DroneSettingKey.HomeConnector || p.Key == DroneSettingKey.HomeBeacon) return;
                // Operation mode only changes with the AI off (as the terminal enforces)
                if (p.Key == DroneSettingKey.OperationModeValue && settings.IsEnabled)
                {
                    Log.Warning("Net: drone {0} operation mode from {1} rejected (AI is on)", Entity.EntityId, senderSteamId);
                    // The sender already shows its value: put the real one back
                    var current = new DroneSettingPacket { EntityId = Entity.EntityId, Key = DroneSettingKey.OperationModeValue };
                    FillSettingPacket(current);
                    Net?.SendToPlayer(current, senderSteamId);
                    return;
                }
                info.Relay = RelayMode.ToOthers;
            }
            Log.Debug("Net: drone {0} setting {1} received from {2}", Entity.EntityId, p.Key, senderSteamId);
            applyingRemote = true;
            try { ApplySettingPacket(p); }
            finally { applyingRemote = false; }
        }

        // Goes through the same setters as the terminal, so clamping and side effects are identical
        private void ApplySettingPacket(DroneSettingPacket p)
        {
            bool flag = p.Number != 0;
            float f = (float)p.Number;
            switch (p.Key)
            {
                case DroneSettingKey.Enabled:                     Terminal_Enabled = flag; break;
                case DroneSettingKey.OperationModeValue:          Terminal_OperationModeValue = (long)p.Number; break;
                case DroneSettingKey.MaxSpeed:                    Terminal_MaxSpeed = f; break;
                case DroneSettingKey.ApproachSpeed:               Terminal_ApproachSpeed = f; break;
                case DroneSettingKey.SafeSpeed:                   Terminal_SafeSpeed = f; break;
                case DroneSettingKey.MaxLoadGravity:              Terminal_MaxLoadGravity = f; break;
                case DroneSettingKey.MaxLoadSpace:                Terminal_MaxLoadSpace = f; break;
                case DroneSettingKey.LcdForeground:               Terminal_LcdForeground = new Color((uint)p.Number); break;
                case DroneSettingKey.LcdBackground:               Terminal_LcdBackground = new Color((uint)p.Number); break;
                case DroneSettingKey.LcdFontSize:                 Terminal_LcdFontSize = f; break;
                case DroneSettingKey.LcdShowHeader:               Terminal_LcdShowHeader = flag; break;
                case DroneSettingKey.LCDScreenTag:                Terminal_LCDScreenTag = new StringBuilder(p.Text ?? ""); break;
                case DroneSettingKey.AlignToPGravity:             Terminal_AlignToPGravity = flag; break;
                case DroneSettingKey.MaxPitchDegrees:             Terminal_MaxPitchDegrees = f; break;
                case DroneSettingKey.MaxRollDegrees:              Terminal_MaxRollDegrees = f; break;
                case DroneSettingKey.MonitorHydrogen:             Terminal_MonitorHydrogen = flag; break;
                case DroneSettingKey.H2RefuelThreshold:           Terminal_H2RefuelThreshold = f; break;
                case DroneSettingKey.H2OperationalThreshold:      Terminal_H2OperationalThreshold = f; break;
                case DroneSettingKey.AlwaysRefuel:                Terminal_AlwaysRefuel = flag; break;
                case DroneSettingKey.MonitorBattery:              Terminal_MonitorBattery = flag; break;
                case DroneSettingKey.BatteryRefuelThreshold:      Terminal_BatteryRefuelThreshold = f; break;
                case DroneSettingKey.BatteryOperationalThreshold: Terminal_BatteryOperationalThreshold = f; break;
                case DroneSettingKey.ObservationArea:
                    SetObservationArea(p.VectorA.ToVector3I(), p.VectorB.ToVector3I());
                    break;
                case DroneSettingKey.HomeConnector:   // server -> clients
                    settings.HomeConnectorId = p.Id;
                    homeConnector = null;
                    if (p.Id == 0 && settings.ObservationAreaDraw) Terminal_ObservationAreaDraw = false;
                    RefreshTerminal();
                    break;
                case DroneSettingKey.ConstructionWorkModes:       Terminal_ConstructionWorkModes = (Construction.WorkModes)(byte)p.Number; break;
                case DroneSettingKey.GrindColor:                  Terminal_GrindColor = new Color((uint)p.Number); break;
                case DroneSettingKey.WaypointTolerance:           Terminal_WaypointTolerance = f; break;
                case DroneSettingKey.ExcludedCapabilities:        Terminal_ExcludedCapabilities = (Capabilities)(uint)p.Number; break;
                case DroneSettingKey.HomeNotStation:              Terminal_HomeNotStation = flag; break;
                case DroneSettingKey.HomeBeacon:   // server -> clients
                    settings.HomeBeaconId = p.Id;
                    RefreshTerminal();
                    break;
                case DroneSettingKey.BehaviourProfile:            Terminal_BehaviourProfileValue = (long)p.Number; break;
                case DroneSettingKey.TaskTimeout:                 Terminal_TaskTimeoutSeconds = f; break;
            }
        }
        #endregion

        #region Actions
        private void RequestAction(DroneActionPacket p)
        {
            var net = Net;
            if (net == null || Entity == null) return;
            p.EntityId = Entity.EntityId;
            Log.Debug("Net: drone {0} action {1} -> server", Entity.EntityId, p.Action);
            net.SendToServer(p);   // on the server / in single player this calls ReceiveAction directly
        }

        private void RequestAction(DroneAction action)
        {
            RequestAction(new DroneActionPacket { Action = action });
        }

        public void ReceiveAction(DroneActionPacket p, ulong senderSteamId)
        {
            if (!IsServer) return;
            long identity;
            if (!SenderHasAccess(senderSteamId, out identity))
            {
                Log.Warning("Net: drone {0} action {1} from {2} rejected (no terminal access)", Entity.EntityId, p.Action, senderSteamId);
                return;
            }
            if (!initialized || settings == null)
            {
                Log.Debug("Net: drone {0} action {1} ignored (not initialized)", Entity.EntityId, p.Action);
                return;
            }
            if ((p.HasVectorA && !IsFinite(p.VectorA)) || (p.HasVectorB && !IsFinite(p.VectorB)))
            {
                Log.Warning("Net: drone {0} action {1} from {2} rejected (bad coordinates)", Entity.EntityId, p.Action, senderSteamId);
                return;
            }
            if (p.Action == DroneAction.RequestAnchorList)
            {
                int now = MyAPIGateway.Session.GameplayFrameCounter;
                int last;
                if (anchorListRequests.TryGetValue(senderSteamId, out last) && now - last < 60) return;   // 1 per second per player
                anchorListRequests[senderSteamId] = now;
            }
            Log.Debug("Net: drone {0} action {1} from {2}", Entity.EntityId, p.Action, senderSteamId);
            try
            {
                ExecuteAction(p, senderSteamId, identity);
            }
            catch (Exception ex)
            {
                Log.Error("Drone {0}: action {1} failed: {2}", Entity.EntityId, p.Action, ex);
            }
        }

        private void ExecuteAction(DroneActionPacket p, ulong senderSteamId, long identity)
        {
            if (!settings.IsEnabled && IsMovementAction(p.Action))
            {
                Report("AI is disabled");
                return;
            }
            if (settings.OperationMode != OperationMode.ManagedByPlayer && IsDebugAction(p.Action))
            {
                Report("Debug orders need 'Managed by player' mode");
                return;
            }
            switch (p.Action)
            {
                case DroneAction.NavigateTo:
                    OrderGoTo(p.VectorA.ToVector3D(), p.HasVectorB ? p.VectorB.ToVector3D() : (Vector3D?)null);
                    break;
                case DroneAction.QueueWaypoint:
                    if (queuedLegs.Count >= MAX_QUEUED_LEGS) { Report("Nav: waypoint queue full"); break; }
                    ExecuteQueueWaypoint(p.VectorA.ToVector3D());
                    break;
                case DroneAction.Orient:
                    orientationTarget = p.VectorA.ToVector3D();
                    orientationTargetSet = true;
                    WakeFrameUpdates();
                    break;
                case DroneAction.PlaceMount:
                    ExecutePlaceMount(p.Code, p.VectorA.ToVector3D());
                    break;
                case DroneAction.NavigateRelative:
                    ExecuteNavigateRelative(p.Id, p.VectorA.ToVector3D(), identity);
                    break;
                case DroneAction.GoHome:
                    GoHome();
                    break;
                case DroneAction.ScanAnchors:
                    ExecuteScanAnchors(senderSteamId, identity);
                    break;
                case DroneAction.AddConnectorByName:
                    ExecuteAddConnectorByName(p.Text, senderSteamId, identity);
                    break;
                case DroneAction.SelectHomeConnector:
                    ExecuteSelectHomeConnector(p.Id, identity);
                    break;
                case DroneAction.SetCurrentAsHome:
                    if (RecordDockPoseFromConnection(identity))
                        SyncSetting(DroneSettingKey.HomeConnector);
                    else
                        Report("Home: dock the drone to a connector first");
                    break;
                case DroneAction.RequestAnchorList:
                    SendAnchorList(senderSteamId, identity);
                    break;
                case DroneAction.SelectHomeBeacon:
                    ExecuteSelectHomeBeacon(p.Id, identity);
                    break;
                case DroneAction.DrawNavigation:
                    SubscribeNavigationRoute(senderSteamId, p.Code != 0);
                    break;
                case DroneAction.StopOrders:
                    ClearFlightOrder();
                    orientationTargetSet = false;
                    StopJob();
                    constructionPausedUntil = int.MinValue / 2;   // a new Stop restarts the countdown
                    PauseConstruction(CONSTRUCTION_STOP_PAUSE_TICKS);
                    stopResumeFrame = constructionPausedUntil;
                    Report("Orders cleared");
                    break;
            }
        }

        // Orders that make the drone move (refused with the AI off)
        private static bool IsMovementAction(DroneAction a)
        {
            return a == DroneAction.NavigateTo || a == DroneAction.QueueWaypoint || a == DroneAction.Orient
                || a == DroneAction.PlaceMount || a == DroneAction.NavigateRelative || a == DroneAction.GoHome;
        }

        // The terminal's debug section (only enabled in ManagedByPlayer)
        private static bool IsDebugAction(DroneAction a)
        {
            return a == DroneAction.NavigateTo || a == DroneAction.QueueWaypoint || a == DroneAction.Orient
                || a == DroneAction.PlaceMount || a == DroneAction.NavigateRelative;
        }

        private static bool IsFinite(Vector3DData v)
        {
            return !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)
                  || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));
        }

        // The player behind a packet, and whether they may use this block's terminal
        private bool SenderHasAccess(ulong steamId, out long identity)
        {
            identity = MyAPIGateway.Players.TryGetIdentityId(steamId);
            if (identity == 0 && !MyAPIGateway.Multiplayer.MultiplayerActive && MyAPIGateway.Session.Player != null)
                identity = MyAPIGateway.Session.Player.IdentityId;   // single player
            var tb = block as IMyTerminalBlock;
            return identity != 0 && tb != null && tb.HasPlayerAccess(identity);
        }
        #endregion

        #region Anchor lists
        // Server: the list for one player (drone owner rule + that player's rule), to that player
        private void SendAnchorList(ulong steamId, long identity)
        {
            if (AutomataSession.Instance == null) return;
            if (!IsMultiplayer || steamId == MyAPIGateway.Multiplayer.MyId)
            {
                RefreshTerminal();   // local player on the server: the terminal reads the server's list directly
                return;
            }
            AutomataSession.Instance.Anchors.GetForViewer(block, identity, anchorListBuffer);
            var packet = new DroneAnchorListPacket { EntityId = Entity.EntityId, Entries = new List<AnchorEntry>(anchorListBuffer) };
            anchorListBuffer.Clear();
            Log.Debug("Net: drone {0} anchor list ({1}) -> {2}", Entity.EntityId, packet.Entries.Count, steamId);
            Net?.SendToPlayer(packet, steamId);
        }

        // Client: list received
        public void ReceiveAnchorList(List<AnchorEntry> entries)
        {
            if (IsServer || AutomataSession.Instance == null) return;
            AutomataSession.Instance.Anchors.SetRemoteEntries(block, entries);
            RefreshTerminal();
        }

        // Client: called from list content callbacks; asks the server for a fresh list now and then
        private void RequestAnchorListIfStale()
        {
            if (IsServer || AutomataSession.Instance == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastAnchorListRequestFrame < ANCHOR_LIST_REFRESH_TICKS) return;
            if (now - AutomataSession.Instance.Anchors.LastReceivedFrame(block) < ANCHOR_LIST_REFRESH_TICKS) return;
            lastAnchorListRequestFrame = now;
            RequestAction(DroneAction.RequestAnchorList);
        }
        #endregion

        #region Log relay
        // Server: send a player-facing line to the clients that may use this drone's terminal
        private void RelayLog(string line)
        {
            if (!IsMultiplayer || !IsServer || Entity == null) return;
            var net = Net;
            var tb = block as IMyTerminalBlock;
            if (net == null || tb == null) return;
            byte[] data = null;
            playerBuffer.Clear();
            MyAPIGateway.Players.GetPlayers(playerBuffer);
            for (int i = 0; i < playerBuffer.Count; i++)
            {
                var player = playerBuffer[i];
                if (player.SteamUserId == MyAPIGateway.Multiplayer.ServerId || player.IsBot) continue;
                if (!tb.HasPlayerAccess(player.IdentityId)) continue;
                if (data == null)
                    data = MyAPIGateway.Utilities.SerializeToBinary<PacketBase>(new DroneLogPacket { EntityId = Entity.EntityId, Line = line });
                net.SendToPlayer(null, player.SteamUserId, data);
            }
            playerBuffer.Clear();
        }

        // Client: line from the server
        public void ReceiveLog(string line)
        {
            if (IsServer || string.IsNullOrEmpty(line)) return;
            terminalLogger?.Echo(line);
        }
        #endregion
    }
}
