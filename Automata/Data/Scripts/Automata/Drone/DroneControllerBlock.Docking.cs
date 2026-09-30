using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Definitions;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Drone
{
    // Docking, sleep/wake, pre-flight, power and load monitoring, and player-facing reporting (terminal + LCD).
    public partial class DroneControllerBlock
    {
        private const double DOCK_APPROACH_DISTANCE = 10.0;   // m: straight final approach along the home connector's axis
        private const int DOCK_TIMEOUT_TICKS = 1800;          // 30 s on top of the final approach time without locking
        private const float DOCK_LINE_TOLERANCE = 0.25f;      // m: approach point and approach line tolerance when docking
        private const int PREFLIGHT_WAKE_TICKS = 30;          // let woken thrusters come online before releasing
        private const int PREFLIGHT_SETTLE_TICKS = 3;         // after release, before the mass is measured
        private const double SPACE_REFERENCE_ACCEL = 0.981;   // m/s²: "100% load" in space = weakest group gives 0.1 g
        private const double LOAD_EXCEEDED_MARGIN = 5.0;      // percentage points above the limit = exceeded
        private const double LOAD_HYSTERESIS = 2.0;

        private readonly DroneStatusDisplay display = new DroneStatusDisplay();

        // Docking
        private long dockingHomeId;          // connector being docked to, home or not (0 = not docking)

        /// <summary>Docking at the home connector right now (not at some other connector for a job).</summary>
        private bool IsGoingHome { get { return dockingHomeId != 0 && settings != null && dockingHomeId == settings.HomeConnectorId; } }
        private long dockingConnectorId;     // drone connector used
        private int dockingWaitTicks;

        // Parked = connected / gear locked / systems asleep. No flight control while parked and idle.
        private bool isParked;
        private int preflightTicks;
        private int preflightStage;          // 0 idle, 1 waking systems, 2 released and settling

        // Power / load
        private int batteryPercent = -1, h2Percent = -1;   // -1 = no batteries / tanks
        private int lastPowerCheckFrame;
        private int lastAutoHomeFrame = int.MinValue / 2;
        private const int AUTO_HOME_RETRY_TICKS = 30 * 60;
        private int loadLevel;               // 0 ok, 1 reached, 2 exceeded
        private double lastMaxMassGravity, lastMaxMassSpace;

        #region Reporting
        /// <summary>
        /// Player-facing log line: terminal custom info and tagged LCDs. Keep it to events (orders, state
        /// changes, thresholds) - nothing that fires every tick.
        /// </summary>
        private void Report(string format, params object[] args)
        {
            if (!IsServer) return;   // simulation runs on the server; clients get these lines relayed
            string text = args != null && args.Length > 0 ? string.Format(format, args) : format;
            terminalLogger?.Echo(text);
            display.Add(text);
            Log.Debug("Drone {0}: {1}", Entity?.EntityId ?? 0, text);
            RelayLog(text);
        }

        // Local UI feedback (input validation before a request is sent). Never relayed.
        private void Feedback(string format, params object[] args)
        {
            string text = args != null && args.Length > 0 ? string.Format(format, args) : format;
            terminalLogger?.Echo(text);
        }

        private void SetState(State state)
        {
            if (state == currentState) return;
            currentState = state;
            Report("State: {0}", StateLabel(state));
            display.SetHeader(StateLabel(state), batteryPercent, h2Percent);
        }

        private static string StateLabel(State s)
        {
            switch (s)
            {
                case State.Initializing:       return "Init";
                case State.NavigatingToTarget: return "Navigating";
                case State.LoadingInventory:   return "Loading";
                case State.ReturningToBase:    return "Returning";
                case State.AligningToHome:     return "Aligning";
                case State.RefuelingHydrogen:  return "Refueling";
                case State.RechargingBattery:  return "Recharging";
                default:                       return s.ToString();
            }
        }
        #endregion

        #region Home connector: dock pose, go home, docking
        /// <summary>
        /// Connection geometry as the game computes it: the connection point is the block centre plus the
        /// dominant-axis projection of the model dummy whose name contains "connector"; the axis is that
        /// direction (outwards). Two connectors lock when these points are within ~0.59 m and the axes
        /// are opposite within 45°. Works for modded connectors; falls back to the front face.
        /// Returns false when the block has no "connector" dummy (ejector, or unknown model).
        /// </summary>
        public static bool ConnectorGeometry(IMyCubeBlock b, out Vector3D point, out Vector3D axis)
        {
            MatrixD w = b.WorldMatrix;
            var grid = b.CubeGrid;
            Vector3D center = Vector3D.Transform((Vector3D)((Vector3)(b.Min + b.Max) * grid.GridSize * 0.5f), grid.WorldMatrix);
            bool found = false;
            Vector3D dummyWorld = Vector3D.Zero;
            if (b.Model != null)
            {
                dummyCache.Clear();
                b.Model.GetDummies(dummyCache);
                foreach (var kv in dummyCache)
                {
                    // like MyShipConnector.LoadDummies: every matching dummy overwrites, so the last one wins
                    if (kv.Key.IndexOf("connector", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    dummyWorld = Vector3D.Transform((Vector3D)kv.Value.Matrix.Translation, w);
                    found = true;
                }
                dummyCache.Clear();
            }
            if (found)
            {
                Vector3D v = dummyWorld - center;
                double r = Vector3D.Dot(v, w.Right), u = Vector3D.Dot(v, w.Up), f = Vector3D.Dot(v, w.Forward);
                double ar = Math.Abs(r), au = Math.Abs(u), af = Math.Abs(f);
                if (af >= ar && af >= au)      axis = w.Forward * Math.Sign(f);
                else if (au >= ar)             axis = w.Up * Math.Sign(u);
                else                           axis = w.Right * Math.Sign(r);
                double along = Math.Max(af, Math.Max(ar, au));
                if (along < 1e-3) { axis = w.Forward; along = 0; }
                point = center + axis * along;
                return true;
            }
            Vector3I size = b.Max - b.Min + Vector3I.One;
            Vector3I fi = Base6Directions.GetIntVector(b.Orientation.Forward);
            double cells = Math.Abs(size.X * fi.X + size.Y * fi.Y + size.Z * fi.Z);
            axis = w.Forward;
            point = center + axis * (cells * grid.GridSize * 0.5);
            return false;
        }

        // Frame for the controller that makes 'mount' face 'toolForward': level frame at the current heading,
        // turned by the smallest rotation. In space "level" means the drone's current up.
        private MatrixD MountFrame(ref ToolMount mount, Vector3D toolForward)
        {
            toolForward.Normalize();
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;
            Vector3D heading = LevelHeading(ref gUp);
            MatrixD level = MatrixD.CreateWorld(Vector3D.Zero, heading, gUp);
            Vector3D f = Vector3D.TransformNormal(mount.LocalForward, level);
            Vector3D fwd = RotateOnto(heading, ref f, ref toolForward);
            Vector3D up = RotateOnto(gUp, ref f, ref toolForward);
            return MatrixD.CreateWorld(Vector3D.Zero, fwd, up);
        }

        /// <summary>Small connectors only lock to small connectors (dummy name contains "small_connector").</summary>
        public static bool IsSmallConnector(IMyCubeBlock b)
        {
            bool small = false;
            if (b.Model == null) return false;
            dummyCache.Clear();
            b.Model.GetDummies(dummyCache);
            foreach (var kv in dummyCache)
                if (kv.Key.IndexOf("connector", StringComparison.OrdinalIgnoreCase) >= 0)
                    small = kv.Key.IndexOf("small_connector", StringComparison.OrdinalIgnoreCase) >= 0;
            dummyCache.Clear();
            return small;
        }

        private int FindConnectorMount(long entityId)
        {
            for (int i = 0; i < connectorMounts.Count; i++)
                if (connectorMounts[i].Block != null && connectorMounts[i].Block.EntityId == entityId) return i;
            return -1;
        }

        /// <summary>
        /// Makes sure a dock pose exists for 'home'. A recorded pose (from an actual connection) is kept while its
        /// drone connector exists; otherwise the drone connector needing the least tilt is chosen and the pose is
        /// computed from both connectors' geometry.
        /// </summary>
        private bool EnsureDockPose(IMyShipConnector home, bool recompute)
        {
            if (!recompute && settings.HomeDockConnectorId != 0 && FindConnectorMount(settings.HomeDockConnectorId) >= 0
                && settings.HomeDockForward.ToVector3D().LengthSquared() > 0.5)
                return true;
            Vector3DData offset, forward, up;
            int mi;
            double tilt;
            if (!ComputeDockPose(home, out offset, out forward, out up, out mi, out tilt)) return false;
            settings.HomeDockOffset = offset;
            settings.HomeDockForward = forward;
            settings.HomeDockUp = up;
            settings.HomeDockConnectorId = connectorMounts[mi].Block.EntityId;
            settings.HomeDockRecorded = false;
            SaveSettings();
            if (flightState.InGravity && MathHelperD.ToDegrees(tilt) > 30)
                Report("Warning: docking tilts the drone {0:F0}°", MathHelperD.ToDegrees(tilt));
            return true;
        }

        /// <summary>
        /// Dock pose for any connector, in that connector's frame: the drone connector needing the least tilt,
        /// placed on the target's connection point, facing it.
        /// </summary>
        private bool ComputeDockPose(IMyShipConnector home, out Vector3DData offset, out Vector3DData forward, out Vector3DData upDir,
                                     out int mountIndex, out double tilt)
        {
            offset = forward = upDir = default(Vector3DData);
            mountIndex = -1;
            tilt = 0;
            if (connectorMounts.Count == 0) return false;
            CaptureFlightState();

            Vector3D pH, aH;
            ConnectorGeometry(home, out pH, out aH);
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : flightState.WorldMatrix.Up;

            bool homeSmall = IsSmallConnector(home);
            int best = -1;
            double bestTilt = double.MaxValue;
            MatrixD bestFrame = MatrixD.Identity;
            for (int i = 0; i < connectorMounts.Count; i++)
            {
                ToolMount m = connectorMounts[i];
                if (m.Block == null || !m.Block.IsFunctional || !m.CanConnect || m.SmallConnector != homeSmall) continue;
                MatrixD frame = MountFrame(ref m, -aH);
                double t = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(frame.Up, gUp), -1, 1));
                if (t < bestTilt) { bestTilt = t; best = i; bestFrame = frame; }
            }
            if (best < 0) return false;

            ToolMount mount = connectorMounts[best];
            Vector3D controllerPos = pH - Vector3D.TransformNormal(mount.LocalPoint, bestFrame);
            MatrixD hm = home.WorldMatrix;
            offset = Vector3DData.FromVector3D(WorldToAnchorPoint(ref hm, controllerPos));
            forward = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, bestFrame.Forward));
            upDir = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, bestFrame.Up));
            mountIndex = best;
            tilt = bestTilt;
            return true;
        }

        /// <summary>
        /// Records the current pose as the home dock pose, when one of the drone's connectors is locked to an
        /// allowed connector (owner / faction). That connector becomes the home connector.
        /// </summary>
        private bool RecordDockPoseFromConnection(long identity)
        {
            CaptureFlightState();
            for (int i = 0; i < connectors.Count; i++)
            {
                var c = connectors[i];
                if (!c.IsConnected || c.OtherConnector == null) continue;
                var other = c.OtherConnector;
                if (AnchorDirectory.Resolve(block, other.EntityId) == null || !AnchorDirectory.IsAllowed(identity, other.OwnerId)) continue;
                MatrixD hm = other.WorldMatrix;
                settings.HomeConnectorId = other.EntityId;
                settings.HomeDockOffset = Vector3DData.FromVector3D(WorldToAnchorPoint(ref hm, flightState.Position));
                settings.HomeDockForward = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, flightState.WorldMatrix.Forward));
                settings.HomeDockUp = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, flightState.WorldMatrix.Up));
                settings.HomeDockConnectorId = c.EntityId;
                settings.HomeDockRecorded = true;
                homeConnector = null;
                SaveSettings();
                Report("Home set: {0} (docking pose recorded)", other.CustomName);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Flies to the home connector's dock pose (relative to the connector, so moving bases work), final
        /// approach along the connector axis at SafeSpeed, then locks and powers down. Returns false if refused.
        /// </summary>
        public bool GoHome()
        {
            if (!initialized || settings == null) return false;
            CaptureFlightState();
            var home = GetHomeConnector();
            if (home == null) { Report("Home: no home connector set / in range"); return false; }
            if (!home.IsWorking) { Report("Home: {0} is not working", home.CustomName); return false; }
            if (home.IsConnected)
            {
                var other = home.OtherConnector;
                bool ours = other != null && FindConnectorMount(other.EntityId) >= 0;
                Report(ours ? "Home: already docked" : "Home: {0} is occupied", home.CustomName);
                return false;
            }
            if (!EnsureDockPose(home, false)) { Report("Home: no usable drone connector"); return false; }
            int mi = FindConnectorMount(settings.HomeDockConnectorId);
            if (!StartDocking(home, mi, settings.HomeDockOffset, settings.HomeDockForward, settings.HomeDockUp)) return false;
            if (preflightStage == 0) SetState(State.ReturningToBase);
            Report("Going home: {0} ({1:F0} m)", home.CustomName, Vector3D.Distance(flightState.Position, home.GetPosition()));
            return true;
        }

        /// <summary>
        /// Docks at any allowed connector (drone owner / faction, resolved by the caller), e.g. a logistics
        /// computer's connector for a job's load step. The pose is computed, not stored.
        /// </summary>
        public bool DockAtConnector(IMyShipConnector target)
        {
            if (!initialized || settings == null || target == null) return false;
            CaptureFlightState();
            if (!target.IsWorking) { Report("Dock: {0} is not working", target.CustomName); return false; }
            if (target.IsConnected)
            {
                var other = target.OtherConnector;
                if (other != null && FindConnectorMount(other.EntityId) >= 0) return true;   // already there
                Report("Dock: {0} is occupied", target.CustomName);
                return false;
            }
            Vector3DData offset, forward, up;
            int mi;
            double tilt;
            if (!ComputeDockPose(target, out offset, out forward, out up, out mi, out tilt)) { Report("Dock: no usable drone connector"); return false; }
            if (!StartDocking(target, mi, offset, forward, up)) return false;
            Report("Docking at {0} ({1:F0} m)", target.CustomName, Vector3D.Distance(flightState.Position, target.GetPosition()));
            return true;
        }

        // Anchored to the target connector (moving targets work): transit, align, final approach at Safe speed
        private bool StartDocking(IMyShipConnector target, int mi, Vector3DData dockOffset, Vector3DData dockForward, Vector3DData dockUp)
        {
            if (mi < 0) return false;
            var droneConnector = connectorMounts[mi].Block as IMyShipConnector;

            Vector3D pH, aH;
            ConnectorGeometry(target, out pH, out aH);
            MatrixD hm = target.WorldMatrix;
            // Position the drone's connection point, not the controller: orientation errors then don't move
            // the connector off the line (the lever arm to the connector can be several metres).
            ToolMount mount = connectorMounts[mi];
            MatrixD dockFrameLocal = MatrixD.CreateWorld(Vector3D.Zero, dockForward.ToVector3D(), dockUp.ToVector3D());
            Vector3D connectorLocal = dockOffset.ToVector3D() + Vector3D.TransformNormal(mount.LocalPoint, dockFrameLocal);
            Vector3D approachLocal = connectorLocal + WorldToAnchorDir(ref hm, aH) * DOCK_APPROACH_DISTANCE;

            var order = OrderGoToRelative(target, connectorLocal, approachLocal);
            if (order == null) { Report("Dock: order refused"); return false; }
            order.ReferenceOffset = Vector3DData.FromVector3D(mount.LocalPoint);
            order.Orientation = FlightOrientationMode.Explicit;
            order.ForwardLocal = dockForward;
            order.UpLocal = dockUp;
            order.IgnoreGravityLimits = true;
            order.FinalSpeed = SafeSpeed;
            order.LineTolerance = DOCK_LINE_TOLERANCE;   // not WaypointTolerance: connectors need ~0.59 m
            order.ArrivalTolerance = 0.2f;
            order.TransitStartLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref hm, ReferencePoint(order)));
            RefreshAnchoredOrder(order);

            if (droneConnector != null)
            {
                if (!droneConnector.Enabled) droneConnector.Enabled = true;
                EnsurePowerTransferOverride(droneConnector);
            }
            dockingHomeId = target.EntityId;   // the connector being docked at (home or not)
            dockingConnectorId = droneConnector != null ? droneConnector.EntityId : 0;
            dockingWaitTicks = 0;
            return true;
        }

        // UpdateBeforeSimulation10: lock as soon as the connector can, then power down.
        private void UpdateDocking()
        {
            if (dockingHomeId == 0 || preflightStage != 0) return;   // still undocking from somewhere else
            var o = activeFlightOrder;
            if (o == null || o.AnchorEntityId != dockingHomeId) { dockingHomeId = 0; return; }   // order replaced

            int mi = FindConnectorMount(dockingConnectorId);
            var c = mi >= 0 ? connectorMounts[mi].Block as IMyShipConnector : null;
            if (c == null || c.Closed) { Report("Docking aborted: drone connector missing"); dockingHomeId = 0; return; }

            if (o.Phase == FlightPhase.Approach && currentState != State.Docking) SetState(State.Docking);
            if (c.IsConnected)
            {
                if (c.OtherConnector != null && c.OtherConnector.EntityId == dockingHomeId) OnDocked(c);
                return;
            }
            if (c.Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable
                && (c.OtherConnector == null || c.OtherConnector.EntityId == dockingHomeId))
            {
                c.Connect();
                return;
            }
            if (o.Phase == FlightPhase.Approach)
            {
                // Time allowed: the final approach at Safe speed, plus the timeout
                dockingWaitTicks += 10;
                int limit = (int)(DOCK_APPROACH_DISTANCE / SafeSpeed * 60) + DOCK_TIMEOUT_TICKS;
                if (dockingWaitTicks >= limit)
                {
                    Report("Docking failed: connector did not lock");
                    ClearFlightOrder();
                    SetState(State.Standby);
                }
            }
        }

        private void OnDocked(IMyShipConnector c)
        {
            dockingHomeId = 0;
            ClearFlightOrder();
            ClearAllThrustOverrides();
            ReleaseGyroscopes();
            ResetIntegralTerms();
            SleepSystems();
            if (settings.AlwaysRefuelWhenDocked || needsBatteryRecharge || needsHydrogenRefuel) StartRefuel();
            isParked = true;
            SetState(State.Docked);
            Report("Docked at {0}", c.OtherConnector != null ? c.OtherConnector.CustomName : "home");
            if (needsBatteryRecharge) Report("Recharging batteries ({0}%)", batteryPercent);
            if (needsHydrogenRefuel) Report("Refueling hydrogen ({0}%)", h2Percent);
            SaveSettings();
        }

        private static void EnsurePowerTransferOverride(IMyShipConnector c)
        {
            if (!IsServer) return;
            try
            {
                if (!c.GetValueBool("PowerTransferOverride")) c.SetValueBool("PowerTransferOverride", true);
            }
            catch (Exception) { }   // modded connectors without the property
        }
        #endregion

        #region Sleep / wake / pre-flight
        private bool IsParkedNow()
        {
            return settings.SleepingBlockIds.Count > 0 || AnyConnectorsConnected() || AnyLandingGearLocked();
        }

        // Powers down everything that only matters in flight. Antennas, the controller, LCDs, batteries, tanks,
        // reactors, connectors, cargo and scripts stay on. Only blocks that were on are recorded / restored.
        private void SleepSystems()
        {
            _gridCache.Clear();
            shipController.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(_gridCache);
            foreach (var grid in _gridCache)
            {
                foreach (var b in grid.GetFatBlocks<IMyFunctionalBlock>())
                {
                    if (b == shipController || !b.Enabled) continue;
                    if (b is IMyThrust || b is IMyGyro || b is IMyLightingBlock || b is IMyShipWelder || b is IMyShipGrinder
                        || b is IMyShipDrill || b is IMySensorBlock || b is IMyCameraBlock || b is IMyOreDetector || b is IMySoundBlock)
                    {
                        b.Enabled = false;
                        settings.SleepingBlockIds.Add(b.EntityId);
                    }
                }
            }
            _gridCache.Clear();
        }

        private void StartRefuel()
        {
            for (int i = 0; i < batteries.Count; i++)
            {
                var b = batteries[i];
                if (b.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge) continue;
                b.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Recharge;
                settings.RechargingBatteryIds.Add(b.EntityId);
            }
            for (int i = 0; i < hydrogenTanks.Count; i++)
            {
                var t = hydrogenTanks[i];
                if (t.Stockpile) continue;
                t.Stockpile = true;
                settings.StockpilingTankIds.Add(t.EntityId);
            }
        }

        private void WakeSystems()
        {
            IMyEntity e;
            foreach (long id in settings.SleepingBlockIds)
            {
                var b = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyFunctionalBlock : null;
                if (b != null && !b.Closed) b.Enabled = true;
            }
            foreach (long id in settings.RechargingBatteryIds)
            {
                var b = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyBatteryBlock : null;
                if (b != null && !b.Closed && b.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge)
                    b.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
            }
            foreach (long id in settings.StockpilingTankIds)
            {
                var t = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyGasTank : null;
                if (t != null && !t.Closed) t.Stockpile = false;
            }
            settings.SleepingBlockIds.Clear();
            settings.RechargingBatteryIds.Clear();
            settings.StockpilingTankIds.Clear();
            SaveSettings();
        }

        /// <summary>
        /// Called by every order entry point. If the drone is docked / landed / asleep, wakes its systems and
        /// holds the order until thrusters are online, then releases connectors and gear and re-measures mass.
        /// </summary>
        private void BeginFlight()
        {
            if (preflightStage != 0 || !IsParkedNow()) return;
            WakeSystems();
            preflightStage = 1;
            preflightTicks = PREFLIGHT_WAKE_TICKS;
            SetState(State.Preflight);
        }

        // UpdateBeforeSimulation while preflightStage != 0. Flight control is on hold meanwhile.
        private void UpdatePreflight()
        {
            if (preflightStage == 1 && activeFlightOrder == null)
            {
                // Order cancelled before release: stay docked, back to sleep
                preflightStage = 0;
                if (AnyConnectorsConnected() || AnyLandingGearLocked())
                {
                    SleepSystems();
                    isParked = true;
                    SetState(State.Docked);
                }
                else SetState(State.Standby);
                return;
            }
            if (--preflightTicks > 0) return;
            if (preflightStage == 1)
            {
                for (int i = 0; i < connectors.Count; i++)
                    if (connectors[i].IsConnected) connectors[i].Disconnect();
                for (int i = 0; i < landingGears.Count; i++)
                    if (landingGears[i].IsLocked) landingGears[i].Unlock();
                preflightStage = 2;
                preflightTicks = PREFLIGHT_SETTLE_TICKS;
                return;
            }
            // stage 2: free of the base; measure what we actually carry
            preflightStage = 0;
            isParked = false;
            UpdateMass();
            h2ThrustProfile = CalculateThrustProfile(shipController, physicalMass, hydrogenThrusters);
            atmoThrustProfile = CalculateThrustProfile(shipController, physicalMass, atmoThrusters);
            ionThrustProfile = CalculateThrustProfile(shipController, physicalMass, ionThrusters);
            combinedThrustProfile = CalculateThrustProfile(shipController, physicalMass, allThrusters);
            commandedVelocity = flightState.LinearVelocity;
            ResetIntegralTerms();
            ResetThrustGains();
            double pct = LoadPercent();
            Report("Pre-flight: {0:N0} kg, load {1:F0}%", physicalMass, pct);
            CheckLoad();
            SetState(activeFlightOrder == null ? State.Standby
                : dockingHomeId != 0 ? State.ReturningToBase : State.NavigatingToTarget);
        }
        #endregion

        #region Power and load
        // UpdateBeforeSimulation100, throttled to the server's power-check interval
        private void MonitorPower()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (now - lastPowerCheckFrame < POWER_CHECK_INTERVAL_MIN_TICKS) return;
            lastPowerCheckFrame = now;

            double stored = 0, max = 0;
            for (int i = 0; i < batteries.Count; i++)
            {
                var b = batteries[i];
                if (b.Closed || !b.IsFunctional) continue;
                stored += b.CurrentStoredPower;
                max += b.MaxStoredPower;
            }
            batteryPercent = max > 0 ? (int)Math.Round(stored / max * 100) : -1;

            double gas = 0, capacity = 0;
            for (int i = 0; i < hydrogenTanks.Count; i++)
            {
                var t = hydrogenTanks[i];
                if (t.Closed || !t.IsFunctional) continue;
                gas += t.FilledRatio * t.Capacity;
                capacity += t.Capacity;
            }
            h2Percent = capacity > 0 ? (int)Math.Round(gas / capacity * 100) : -1;
            display.SetHeader(StateLabel(currentState), batteryPercent, h2Percent);
            if (!settings.IsEnabled) return;   // AI off: levels for the header only

            bool docked = currentState == State.Docked;
            if (settings.MonitorBatteryLevels && batteryPercent >= 0)
            {
                if (!needsBatteryRecharge && batteryPercent < settings.BatteryRefuelThreshold)
                {
                    needsBatteryRecharge = true;
                    Report("Battery low ({0}% < {1:F0}%), recharging", batteryPercent, settings.BatteryRefuelThreshold);
                }
                else if (needsBatteryRecharge && batteryPercent >= Math.Max(settings.BatteryOperationalThreshold, settings.BatteryRefuelThreshold + 5))
                {
                    needsBatteryRecharge = false;
                    Report("Battery recharged ({0}%)", batteryPercent);
                }
            }
            if (settings.MonitorHydrogenLevels && h2Percent >= 0)
            {
                if (!needsHydrogenRefuel && h2Percent < settings.HydrogenRefuelThreshold)
                {
                    needsHydrogenRefuel = true;
                    Report("Hydrogen low ({0}% < {1:F0}%), refueling", h2Percent, settings.HydrogenRefuelThreshold);
                }
                else if (needsHydrogenRefuel && h2Percent >= Math.Max(settings.HydrogenOperationalThreshold, settings.HydrogenRefuelThreshold + 5))
                {
                    needsHydrogenRefuel = false;
                    Report("Hydrogen refueled ({0}%)", h2Percent);
                }
            }

            // Level-triggered: keep trying to get home (refused, replaced by another order...) with a back-off
            // (docked somewhere else, e.g. at a job's connector, still counts as "not home")
            if ((needsBatteryRecharge || needsHydrogenRefuel) && !IsDockedAtHome() && !IsGoingHome && preflightStage == 0
                && HasHomeConnector && now - lastAutoHomeFrame >= AUTO_HOME_RETRY_TICKS)
            {
                lastAutoHomeFrame = now;
                GoHome();
            }
        }

        /// <summary>Heaviest total mass the up thrusters can hover in the current gravity (1 g in space).</summary>
        public double MaxMassGravity()
        {
            if (isParked) return lastMaxMassGravity;
            double g = flightState.InGravity ? flightState.Gravity.Length() : 9.81;
            lastMaxMassGravity = combinedThrustProfile.Up.Max / g;
            return lastMaxMassGravity;
        }

        /// <summary>Heaviest total mass the weakest (non-empty) thruster group can still push at 0.1 g.</summary>
        public double MaxMassSpace()
        {
            if (isParked) return lastMaxMassSpace;
            double min = double.MaxValue;
            for (int i = 0; i < 6; i++)
            {
                double t = ProfileMax(i);
                if (t > 0 && t < min) min = t;
            }
            lastMaxMassSpace = min == double.MaxValue ? 0 : min / SPACE_REFERENCE_ACCEL;
            return lastMaxMassSpace;
        }

        private double LoadPercent()
        {
            double max = flightState.InGravity ? MaxMassGravity() : MaxMassSpace();
            return max > 0 ? physicalMass / max * 100.0 : 0;
        }

        /// <summary>True once the load limit for the current environment is reached (collect no more).</summary>
        public bool IsLoadLimitReached { get { return loadLevel > 0; } }

        private void CheckLoad()
        {
            if (isParked || preflightStage != 0) return;
            double limit = flightState.InGravity ? settings.MaxLoadGravity : settings.MaxLoadSpace;
            double pct = LoadPercent();
            int level = pct >= limit + LOAD_EXCEEDED_MARGIN ? 2 : pct >= limit ? 1 : 0;
            if (level < loadLevel && pct >= limit - LOAD_HYSTERESIS) level = loadLevel;   // no flapping around the limit
            if (level > loadLevel)
            {
                if (level == 2)
                {
                    Report("Max load exceeded ({0:F0}% > {1:F0}%), offloading", pct, limit);
                    // Construction batches are planned to the limit: only abort one that is still collecting (grinding)
                    if (cPhase == ConstructionPhase.Running) AbortJobToHome();
                    else if (!IsGoingHome && HasHomeConnector && cPhase == ConstructionPhase.Idle) GoHome();
                }
                else Report("Max load reached ({0:F0}% of {1:F0}%)", pct, limit);
            }
            loadLevel = level;
        }
        #endregion
    }
}
