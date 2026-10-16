using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Definitions;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;
using VRage.Utils;

namespace Automata.Drone
{
    // Docking, sleep/wake, pre-flight, power and load monitoring, and player-facing reporting (terminal + LCD).
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrOccupiedBy = MyStringId.GetOrCompute("occupied by {0}");
        private static readonly MyStringId StrAnotherGrid = MyStringId.GetOrCompute("another grid");
        private static readonly MyStringId StrHeadingAlternative = MyStringId.GetOrCompute(" || heading +{0}°: {1}");
        private static readonly MyStringId StrDroneConnectorOff = MyStringId.GetOrCompute("drone connector {0} off / unpowered");
        private static readonly MyStringId StrOwnersNotFriendly = MyStringId.GetOrCompute("owners not friendly ({0})");
        private static readonly MyStringId StrInUseByDrone = MyStringId.GetOrCompute("in use by drone {0}");
        private static readonly MyStringId StrState = MyStringId.GetOrCompute("State: {0}");
        private static readonly MyStringId StrInit = MyStringId.GetOrCompute("Init");
        private static readonly MyStringId StrNavigating = MyStringId.GetOrCompute("Navigating");
        private static readonly MyStringId StrLoading = MyStringId.GetOrCompute("Loading");
        private static readonly MyStringId StrReturning = MyStringId.GetOrCompute("Returning");
        private static readonly MyStringId StrAligning = MyStringId.GetOrCompute("Aligning");
        private static readonly MyStringId StrRefueling = MyStringId.GetOrCompute("Refueling");
        private static readonly MyStringId StrRecharging = MyStringId.GetOrCompute("Recharging");
        private static readonly MyStringId StrWarningDockingTiltsTheDrone = MyStringId.GetOrCompute("Warning: docking tilts the drone {0:F0}°");
        private static readonly MyStringId StrHomeSetDockingPoseRecorded = MyStringId.GetOrCompute("Home set: {0} (docking pose recorded)");
        private static readonly MyStringId StrHomeNoHomeConnectorSet = MyStringId.GetOrCompute("Home: no home connector set / in range");
        private static readonly MyStringId StrHomeIsNotWorking = MyStringId.GetOrCompute("Home: {0} is not working");
        private static readonly MyStringId StrHomeAlreadyDocked = MyStringId.GetOrCompute("Home: already docked");
        private static readonly MyStringId StrHomeIsOccupied = MyStringId.GetOrCompute("Home: {0} is occupied");
        private static readonly MyStringId StrHomeNoUsableDroneConnector = MyStringId.GetOrCompute("Home: no usable drone connector");
        private static readonly MyStringId StrGoingHomeM = MyStringId.GetOrCompute("Going home: {0} ({1:F0} m)");
        private static readonly MyStringId StrDockIsNotWorking = MyStringId.GetOrCompute("Dock: {0} is not working");
        private static readonly MyStringId StrDockIsOccupied = MyStringId.GetOrCompute("Dock: {0} is occupied");
        private static readonly MyStringId StrDockNoUsableDroneConnector = MyStringId.GetOrCompute("Dock: no usable drone connector");
        private static readonly MyStringId StrDockingAtM = MyStringId.GetOrCompute("Docking at {0} ({1:F0} m)");
        private static readonly MyStringId StrDockNeedsPGravityAlign = MyStringId.GetOrCompute("Dock: {0} needs {1} (P-Gravity align limits): not docking");
        private static readonly MyStringId StrDockNoWayInCorrectedPose = MyStringId.GetOrCompute("Dock: no way in to {0}: the straight approach is blocked by {1}, and no corridor ({2})");
        private static readonly MyStringId StrDockNoRouteTo = MyStringId.GetOrCompute("Dock: no route to {0}");
        private static readonly MyStringId StrDockOrderRefused = MyStringId.GetOrCompute("Dock: order refused");
        private static readonly MyStringId StrDockingAbortedDroneConnectorMissing = MyStringId.GetOrCompute("Docking aborted: drone connector missing");
        private static readonly MyStringId StrDocking = MyStringId.GetOrCompute("Docking");
        private static readonly MyStringId StrDockingFailedConnectorDidNot = MyStringId.GetOrCompute("Docking failed: connector did not lock ({0})");
        private static readonly MyStringId StrDockNoWayInStraightBlocked = MyStringId.GetOrCompute("Dock: no way in to {0} with the corrected pose");
        private static readonly MyStringId StrTargetConnectorGone = MyStringId.GetOrCompute("target connector gone");
        private static readonly MyStringId StrTargetConnectorOffUnpowered = MyStringId.GetOrCompute("target connector off / unpowered");
        private static readonly MyStringId StrGapMAlongAcrossAxes = MyStringId.GetOrCompute("gap {0:F2} m (along {1:F2}, across {2:F2}), axes {3:F0}° off, status {4} / {5}, {6} / {7}");
        private static readonly MyStringId StrDockedAt = MyStringId.GetOrCompute("Docked at {0}");
        private static readonly MyStringId StrHome = MyStringId.GetOrCompute("home");
        private static readonly MyStringId StrRechargingBatteries = MyStringId.GetOrCompute("Recharging batteries ({0}%)");
        private static readonly MyStringId StrRefuelingHydrogen = MyStringId.GetOrCompute("Refueling hydrogen ({0}%)");
        private static readonly MyStringId StrPreFlightThrustersOn = MyStringId.GetOrCompute("Pre-flight: {0} thruster(s) that were off switched on");
        private static readonly MyStringId StrPreFlightNoWorkingGyroscope = MyStringId.GetOrCompute("Pre-flight: no working gyroscope ({0} damaged, {1} without power): it can't turn");
        private static readonly MyStringId StrPreFlightNoSensorOn = MyStringId.GetOrCompute("Pre-flight: no sensor on ({0} fitted): obstacles only seen ahead of {1} working camera(s)");
        private static readonly MyStringId StrNoBatteryOrPowerProducer = MyStringId.GetOrCompute("no battery or power producer of its own");
        private static readonly MyStringId StrBatteriesStuckOnRecharge = MyStringId.GetOrCompute("batteries stuck on Recharge");
        private static readonly MyStringId StrBatteriesEmpty = MyStringId.GetOrCompute("batteries empty");
        private static readonly MyStringId StrBatteriesSwitchedOff = MyStringId.GetOrCompute("batteries switched off");
        private static readonly MyStringId StrPreFlightStayingDocked = MyStringId.GetOrCompute("Pre-flight: {0}: staying docked");
        private static readonly MyStringId StrPreFlightKgLoad = MyStringId.GetOrCompute("Pre-flight: {0:N0} kg, load {1:F0}%");
        private static readonly MyStringId StrBatteryLowRecharging = MyStringId.GetOrCompute("Battery low ({0}% < {1:F0}%), recharging");
        private static readonly MyStringId StrBatteryRecharged = MyStringId.GetOrCompute("Battery recharged ({0}%)");
        private static readonly MyStringId StrHydrogenLowRefueling = MyStringId.GetOrCompute("Hydrogen low ({0}% < {1:F0}%), refueling");
        private static readonly MyStringId StrHydrogenRefueled = MyStringId.GetOrCompute("Hydrogen refueled ({0}%)");
        private static readonly MyStringId StrMaxLoadExceededOffloading = MyStringId.GetOrCompute("Max load exceeded ({0:F0}% > {1:F0}%), offloading");
        private static readonly MyStringId StrMaxLoadReachedOf = MyStringId.GetOrCompute("Max load reached ({0:F0}% of {1:F0}%)");
        #endregion

        private const double DOCK_APPROACH_DISTANCE = 10.0;   // m: straight final approach along the home connector's axis (at least)
        // Long drones: their own length + 2.5 m, so they turn clear of the connector's grid
        private double DockApproachDistance { get { return Math.Max(DOCK_APPROACH_DISTANCE, ManoeuvreDistance()); } }
        #region Timing
        private const int DOCK_TIMEOUT_TICKS = 1800;          // 30 s on top of the final approach time without locking
        private const int PREFLIGHT_WAKE_TICKS = 30;          // let woken thrusters come online before releasing
        private const int PREFLIGHT_SETTLE_TICKS = 3;         // after release, before the mass is measured
        #endregion
        private const float DOCK_LINE_TOLERANCE = 0.25f;      // m: approach point and approach line tolerance when docking
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

        // Connectors drones are docking at / docked to (server). Validated when read: a claim holds while its drone is
        // still on the way to that connector or docked there, so nothing has to release it.
        private static readonly Dictionary<long, DroneControllerBlock> dockClaims = new Dictionary<long, DroneControllerBlock>();

        private void ClaimConnector(long connectorId)
        {
            dockClaims[connectorId] = this;
        }

        /// <summary>Another drone docking at / docked to this connector, or null.</summary>
        private DroneControllerBlock ConnectorClaimant(long connectorId)
        {
            DroneControllerBlock d;
            if (!dockClaims.TryGetValue(connectorId, out d) || d == this) return null;
            if (d == null || d.Entity == null || d.Entity.MarkedForClose || d.Entity.Closed
                || !(d.dockingHomeId == connectorId || d.IsDockedAt(connectorId)))
            {
                dockClaims.Remove(connectorId);
                return null;
            }
            return d;
        }

        private static readonly List<long> claimBuffer = new List<long>();
        private void ReleaseConnectorClaims()
        {
            claimBuffer.Clear();
            foreach (var kv in dockClaims) if (kv.Value == this) claimBuffer.Add(kv.Key);
            for (int i = 0; i < claimBuffer.Count; i++) dockClaims.Remove(claimBuffer[i]);
            claimBuffer.Clear();
        }

        /// <summary>Small connectors are usable by these behaviour profiles only (TODO/14); others need a standard one.</summary>
        private bool SmallConnectorsAllowed
        {
            get
            {
                var p = settings != null ? settings.BehaviourProfile : BehaviourProfile.None;
                return p == BehaviourProfile.IsCombatDrone || p == BehaviourProfile.IsOreScout || p == BehaviourProfile.IsOreHauler;
            }
        }

        /// <summary>The faces (controller frame) of the drone's usable connectors of one size, for bids (TODO/14).</summary>
        private List<byte> DockFaces(bool small = false)
        {
            var faces = new List<byte>();
            for (int i = 0; i < connectorMounts.Count; i++)
            {
                var m = connectorMounts[i];
                if (m.Block == null || !m.Block.IsFunctional || !m.CanConnect || m.SmallConnector != small) continue;
                byte f = (byte)Base6Directions.GetClosestDirection((Vector3)m.LocalForward);
                if (!faces.Contains(f)) faces.Add(f);
            }
            return faces;
        }

        /// <summary>Why this drone can't dock at the connector right now (another drone's, or another ship's), or null.</summary>
        private string ConnectorBusy(IMyShipConnector target)
        {
            var other = ConnectorClaimant(target.EntityId);
            if (other != null) return Loc.F(StrInUseByDrone, other.Entity.EntityId);
            if (target.IsConnected && target.OtherConnector != null && FindConnectorMount(target.OtherConnector.EntityId) < 0)
                return Loc.F(StrOccupiedBy, target.OtherConnector.CubeGrid != null ? target.OtherConnector.CubeGrid.DisplayName : Loc.T(StrAnotherGrid));
            return null;
        }
        private FlightOrder dockingOrder;    // the final (connector) order; legs before it go around the observation area
        private readonly List<Vector3D> dockRoutePoints = new List<Vector3D>();
        private readonly List<FlightOrder> dockLegs = new List<FlightOrder>();

        // Parked = connected / gear locked / systems asleep. No flight control while parked and idle.
        private bool isParked;
        private int preflightTicks;
        private int preflightStage;          // 0 idle, 1 waking systems, 2 released and settling

        // Power / load
        private int batteryPercent = -1, h2Percent = -1;   // -1 = no batteries / tanks
        private int lastPowerCheckFrame;
        private int lastAutoHomeFrame = int.MinValue / 2;
        #region Timing
        private const int AUTO_HOME_RETRY_TICKS = 30 * 60;
        #endregion
        private int loadLevel;               // 0 ok, 1 reached, 2 exceeded
        private double lastMaxMassGravity, lastMaxMassSpace;

        #region Reporting
        /// <summary>
        /// Player-facing log line: terminal custom info and tagged LCDs. Keep it to events (orders, state
        /// changes, thresholds) - nothing that fires every tick.
        /// </summary>
        private void Report(MyStringId format, params object[] args)
        {
            if (!IsServer) return;
            Report(args != null && args.Length > 0 ? Loc.F(format, args) : Loc.T(format));
        }

        /// <summary>A log line already in the player's language.</summary>
        private void Report(string text)
        {
            if (!IsServer) return;   // simulation runs on the server; clients get these lines relayed
            terminalLogger?.Echo(text);
            display.Add(text);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: {1}", Entity?.EntityId ?? 0, text);
            RelayLog(text);
        }

        // Local UI feedback (input validation before a request is sent). Never relayed.
        private void Feedback(MyStringId format, params object[] args)
        {
            string text = args != null && args.Length > 0 ? Loc.F(format, args) : Loc.T(format);
            terminalLogger?.Echo(text);
        }

        private void SetState(State state)
        {
            if (state == currentState) return;
            currentState = state;
            Report(StrState, StateLabel(state));
            display.SetHeader(StateLabel(state), batteryPercent, h2Percent);
        }

        private static string StateLabel(State s)
        {
            switch (s)
            {
                case State.Initializing:       return Loc.T(StrInit);
                case State.NavigatingToTarget: return Loc.T(StrNavigating);
                case State.LoadingInventory:   return Loc.T(StrLoading);
                case State.ReturningToBase:    return Loc.T(StrReturning);
                case State.AligningToHome:     return Loc.T(StrAligning);
                case State.RefuelingHydrogen:  return Loc.T(StrRefueling);
                case State.RechargingBattery:  return Loc.T(StrRecharging);
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
            // Only a pose the player recorded ("Set current as home") is kept; computed ones are recomputed, so
            // they follow the current rules (grid-aligned roll) and the drone's current connectors
            if (!recompute && settings.HomeDockRecorded && settings.HomeDockConnectorId != 0
                && FindConnectorMount(settings.HomeDockConnectorId) >= 0 && settings.HomeDockForward.ToVector3D().LengthSquared() > 0.5)
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
                Report(StrWarningDockingTiltsTheDrone, MathHelperD.ToDegrees(tilt));
            return true;
        }

        /// <summary>
        /// Dock pose for any connector, in that connector's frame: the drone connector needing the least tilt,
        /// placed on the target's connection point, facing it, rolled about the connector axis so the drone lines
        /// up with the target's grid (<see cref="AlignDockRoll"/>).
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
            MatrixD hm = home.WorldMatrix;
            int best = -1;
            double bestTilt = double.MaxValue;
            MatrixD bestFrame = MatrixD.Identity;
            for (int i = 0; i < connectorMounts.Count; i++)
            {
                ToolMount m = connectorMounts[i];
                if (m.Block == null || !m.Block.IsFunctional || !m.CanConnect || m.SmallConnector != homeSmall) continue;
                MatrixD frame = AlignDockRoll(MountFrame(ref m, -aH), aH, ref hm);
                double t = Math.Acos(MathHelperD.Clamp(Vector3D.Dot(frame.Up, gUp), -1, 1));
                if (t < bestTilt) { bestTilt = t; best = i; bestFrame = frame; }
            }
            if (best < 0) return false;

            ToolMount mount = connectorMounts[best];
            Vector3D controllerPos = pH - Vector3D.TransformNormal(mount.LocalPoint, bestFrame);
            offset = Vector3DData.FromVector3D(WorldToAnchorPoint(ref hm, controllerPos));
            forward = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, bestFrame.Forward));
            upDir = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, bestFrame.Up));
            mountIndex = best;
            tilt = bestTilt;
            return true;
        }

        /// <summary>
        /// Rolls a dock frame about the connector axis 'axis' (world) in 90° steps so the drone's axes line up
        /// with the target connector's grid: connectors meet face to face, and the drone sits square to the base
        /// instead of at whatever heading it had. Of the four, the least tilted in gravity; otherwise (and on ties)
        /// the smallest roll from 'frame'.
        /// </summary>
        private MatrixD AlignDockRoll(MatrixD frame, Vector3D axis, ref MatrixD hm)
        {
            Vector3D a = Vector3D.Normalize(axis);
            // A drone axis lying in the connector plane, and one of the target grid's
            Vector3D r = MostPerpendicular(frame.Right, frame.Up, frame.Forward, ref a);
            Vector3D h = MostPerpendicular(hm.Right, hm.Up, hm.Forward, ref a);
            r -= a * Vector3D.Dot(r, a);
            h -= a * Vector3D.Dot(h, a);
            if (r.LengthSquared() < 1e-6 || h.LengthSquared() < 1e-6) return frame;
            r.Normalize();
            h.Normalize();
            Vector3D h90 = Vector3D.Cross(a, h);
            Vector3D gUp = flightState.GravityUp;
            MatrixD best = frame;
            double bestCost = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                Vector3D target = k == 0 ? h : k == 1 ? h90 : k == 2 ? -h : -h90;
                double angle = Math.Atan2(Vector3D.Dot(Vector3D.Cross(r, target), a), Vector3D.Dot(r, target));
                MatrixD rot = MatrixD.CreateFromAxisAngle(a, angle);
                if (Vector3D.Dot(Vector3D.TransformNormal(r, rot), target) < 0.99)
                    rot = MatrixD.CreateFromAxisAngle(a, -angle);                  // other handedness convention
                Vector3D fwd = Vector3D.TransformNormal(frame.Forward, rot), up = Vector3D.TransformNormal(frame.Up, rot);
                double cost = Math.Abs(angle) * 0.01;
                if (flightState.InGravity) cost += Math.Acos(MathHelperD.Clamp(Vector3D.Dot(up, gUp), -1, 1));
                if (cost < bestCost) { bestCost = cost; best = MatrixD.CreateWorld(Vector3D.Zero, fwd, up); }
            }
            return best;
        }

        private static Vector3D MostPerpendicular(Vector3D x, Vector3D y, Vector3D z, ref Vector3D a)
        {
            double dx = Math.Abs(Vector3D.Dot(x, a)), dy = Math.Abs(Vector3D.Dot(y, a)), dz = Math.Abs(Vector3D.Dot(z, a));
            return dx <= dy && dx <= dz ? x : dy <= dz ? y : z;
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
                Report(StrHomeSetDockingPoseRecorded, other.CustomName);
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
            if (home == null) { Report(StrHomeNoHomeConnectorSet); return false; }
            if (!home.IsWorking) { Report(StrHomeIsNotWorking, home.CustomName); return false; }
            if (home.IsConnected)
            {
                var other = home.OtherConnector;
                bool ours = other != null && FindConnectorMount(other.EntityId) >= 0;
                Report(ours ? StrHomeAlreadyDocked : StrHomeIsOccupied, home.CustomName);
                return false;
            }
            if (!EnsureDockPose(home, false)) { Report(StrHomeNoUsableDroneConnector); return false; }
            ResetRoute();
            int mi = FindConnectorMount(settings.HomeDockConnectorId);
            if (!StartDocking(home, mi, settings.HomeDockOffset, settings.HomeDockForward, settings.HomeDockUp, true)) return false;
            if (preflightStage == 0) SetState(State.ReturningToBase);
            Report(StrGoingHomeM, home.CustomName, Vector3D.Distance(flightState.Position, home.GetPosition()));
            return true;
        }

        /// <summary>
        /// Docks at any allowed connector (drone owner / faction, resolved by the caller), e.g. a logistics
        /// computer's connector for a job's load step. The pose is computed, not stored.
        /// </summary>
        public bool DockAtConnector(IMyShipConnector target, bool async = true)
        {
            if (!initialized || settings == null || target == null) return false;
            CaptureFlightState();
            if (!target.IsWorking) { Report(StrDockIsNotWorking, target.CustomName); return false; }
            if (target.IsConnected)
            {
                var other = target.OtherConnector;
                if (other != null && FindConnectorMount(other.EntityId) >= 0) return true;   // already there
                Report(StrDockIsOccupied, target.CustomName);
                return false;
            }
            Vector3DData offset, forward, up;
            int mi;
            double tilt;
            if (!ComputeDockPose(target, out offset, out forward, out up, out mi, out tilt)) { Report(StrDockNoUsableDroneConnector); return false; }
            ResetRoute();
            if (!StartDocking(target, mi, offset, forward, up, async)) return false;
            Report(StrDockingAtM, target.CustomName, Vector3D.Distance(flightState.Position, target.GetPosition()));
            return true;
        }

        // Anchored to the target connector (moving targets work): transit, align, final approach at Safe speed
        // A dock / home trip that finds no route repositions and tries again (DroneControllerBlock.Recovery)
        // 'async': the route may wait for a search on a worker thread (PathSearchQueue): the drone holds meanwhile and the
        // docking is planned again when the answer is in (DroneControllerBlock.Pending); returns true then.
        private bool StartDocking(IMyShipConnector target, int mi, Vector3DData dockOffset, Vector3DData dockForward, Vector3DData dockUp,
                                  bool async = false)
        {
            bool was = recoverPlanning, wasAsync = asyncPlanOk;
            recoverPlanning = true;
            var pm = Pathfinding.PathfindingManager.Instance;
            async = async && pm != null && pm.Searches != null;
            asyncPlanOk = async;
            bool pending = false;
            bool contactSet = contactBackOutSet;   // (consumed on the way: put back when the plan is made again later)
            try { return StartDockingCore(target, mi, dockOffset, dockForward, dockUp); }
            catch (Pathfinding.PlanPendingException)
            {
                if (!async || dockPrepCurrent == null) throw;
                pending = true;
                if (contactSet) { contactBackOutSet = true; contactBackOutFrame = MyAPIGateway.Session.GameplayFrameCounter; }
                DeferDocking(dockPrepCurrent);
                return true;
            }
            finally
            {
                recoverPlanning = was;
                asyncPlanOk = wasAsync;
                dockPrepCurrent = null;
                if (async && !pending) pm.Searches.EndPlan(Entity.EntityId);   // the plan is over: its answers go
            }
        }

        /// <summary>
        /// What a docking works out before its route (the dock frame, the entry corridor - the expensive part -, the
        /// approach point): kept while the route waits for a search, and used again when it is planned again soon after,
        /// for the same connector and pose, the connector where it was.
        /// </summary>
        private sealed class DockPrep
        {
            public IMyShipConnector Target;
            public int Mi, Frame;
            public Vector3D OrigOffset, OrigForward, OrigUp;      // what StartDocking was called with
            public Vector3DData DockOffset, DockForward, DockUp;  // the pose (a corridor may turn its heading)
            public MatrixD Hm, DockFrame;
            public Vector3D AH, ConnectorLocal, ApproachLocal, HullFromConnector, ExitDir;
            public bool Corridor;
            public double Berth;
            public readonly List<Vector3D> CorridorPts = new List<Vector3D>();
        }
        #region Timing
        private const int DOCK_PREP_KEEP_TICKS = 10 * 60;   // a docking's preparation is used again this long
        #endregion
        private DockPrep pendingDock;      // the docking waiting for its route search
        private DockPrep dockPrepCurrent;  // the docking being planned (StartDocking)

        private static bool SameDir(Vector3D a, Vector3D b) { return Vector3D.DistanceSquared(a, b) < 1e-8; }

        private bool PrepStillGood(DockPrep p, IMyShipConnector target, int mi, Vector3DData offset, Vector3DData forward, Vector3DData up)
        {
            if (p == null || p.Target != target || p.Mi != mi || target.Closed) return false;
            if (MyAPIGateway.Session.GameplayFrameCounter - p.Frame > DOCK_PREP_KEEP_TICKS) return false;
            if (!SameDir(p.OrigOffset, offset.ToVector3D()) || !SameDir(p.OrigForward, forward.ToVector3D()) || !SameDir(p.OrigUp, up.ToVector3D())) return false;
            MatrixD hm = target.WorldMatrix;
            return Vector3D.DistanceSquared(hm.Translation, p.Hm.Translation) < 0.0025
                   && Vector3D.Dot(hm.Forward, p.Hm.Forward) > 0.99995 && Vector3D.Dot(hm.Up, p.Hm.Up) > 0.99995;
        }

        private bool StartDockingCore(IMyShipConnector target, int mi, Vector3DData dockOffset, Vector3DData dockForward, Vector3DData dockUp)
        {
            if (mi < 0) return false;
            DockPrep prep = pendingDock;
            pendingDock = null;
            if (PrepStillGood(prep, target, mi, dockOffset, dockForward, dockUp))
            {
                prep.Frame = MyAPIGateway.Session.GameplayFrameCounter;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: docking at {1}: its corridor / approach as worked out before", Entity.EntityId, target.CustomName);
            }
            else prep = PrepareDock(target, mi, dockOffset, dockForward, dockUp);
            if (prep == null) return false;
            dockPrepCurrent = prep;
            return FinishDock(prep);
        }

        // The dock frame, the entry corridor (or the approach point out along the axis), the approach point. Null: refused (reported)
        private DockPrep PrepareDock(IMyShipConnector target, int mi, Vector3DData dockOffset, Vector3DData dockForward, Vector3DData dockUp)
        {
            var prep = new DockPrep
            {
                Target = target, Mi = mi, Frame = MyAPIGateway.Session.GameplayFrameCounter,
                OrigOffset = dockOffset.ToVector3D(), OrigForward = dockForward.ToVector3D(), OrigUp = dockUp.ToVector3D(),
            };
            Vector3D pH, aH;
            ConnectorGeometry(target, out pH, out aH);
            MatrixD hm = target.WorldMatrix;
            // Position the drone's connection point, not the controller: orientation errors then don't move
            // the connector off the line (the lever arm to the connector can be several metres).
            ToolMount mount = connectorMounts[mi];
            // The dock frame is built in world space: the anchor coordinates (right, up, FORWARD) are mirrored against the
            // game's (right, up, backward), so CreateWorld there flips Right and with it a side connector's offset (a drone
            // docking on a Left connector aimed 2 x its offset, 7.6 m, beside the port)
            MatrixD dockFrame = MatrixD.CreateWorld(Vector3D.Zero, AnchorToWorldDir(ref hm, dockForward.ToVector3D()), AnchorToWorldDir(ref hm, dockUp.ToVector3D()));
            // Align to P-Gravity: a connector the drone can only meet tilted past its pitch / roll limits is refused
            string tiltNeed;
            if (!WithinGravityLimits(ref dockFrame, out tiltNeed))
            {
                Report(StrDockNeedsPGravityAlign, target.CustomName, tiltNeed);
                return null;
            }
            Vector3D connectorLocal = dockOffset.ToVector3D() + WorldToAnchorDir(ref hm, Vector3D.TransformNormal(mount.LocalPoint, dockFrame));
            // The entry corridor (DroneControllerBlock.Corridor): from the slide's start out to where the drone can turn,
            // holding the dock attitude, with the dock clearance (then the minimum one). Connectors meet at any of four
            // headings about their axis: each one that keeps the drone as level is tried (a long drone fits a door
            // lengthwise), unless the player recorded the home pose.
            Vector3D p0World = AnchorToWorldPoint(ref hm, connectorLocal);
            Vector3D gUp = flightState.InGravity ? flightState.GravityUp : Vector3D.Zero;
            corridorIgnore = target;   // the connector itself is where the drone goes, not an obstacle
            bool recorded = settings.HomeDockRecorded && target.EntityId == settings.HomeConnectorId;
            double up0 = Vector3D.Dot(dockFrame.Up, gUp);
            string corridorHow = null;
            double corridorBerth = 0;
            bool corridor = false;
            int heading = 0;
            for (int k = 0; k < (recorded ? 1 : 4) && !corridor; k++)
            {
                MatrixD turn = MatrixD.CreateFromAxisAngle(Vector3D.Normalize(aH), k * MathHelperD.PiOver2);
                MatrixD frameK = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.TransformNormal(dockFrame.Forward, turn), Vector3D.TransformNormal(dockFrame.Up, turn));
                if (k > 0 && Vector3D.Dot(frameK.Up, gUp) < up0 - 0.05) continue;   // would tilt the drone (a side connector)
                Vector3D p0HullK = p0World + Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frameK);
                string howK;
                double berthK;
                if (FindCorridorAny(p0HullK, aH, ref frameK, ref hm, prep.CorridorPts, out howK, out berthK))
                {
                    corridor = true;
                    corridorHow = howK;
                    corridorBerth = berthK;
                    heading = k;
                    if (k > 0)
                    {
                        dockFrame = frameK;
                        dockForward = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, frameK.Forward));
                        dockUp = Vector3DData.FromVector3D(WorldToAnchorDir(ref hm, frameK.Up));
                        dockOffset = Vector3DData.FromVector3D(connectorLocal - WorldToAnchorDir(ref hm, Vector3D.TransformNormal(mount.LocalPoint, frameK)));
                    }
                }
                else corridorHow = corridorHow == null ? howK : corridorHow + Loc.F(StrHeadingAlternative, k * 90, howK);
            }
            // The corridor's way out (read now: the departure's corridor, worked out after, overwrites corridorExitDir)
            Vector3D exitDir = corridorExitDir;
            Vector3D hullFromConnector = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, dockFrame);
            Vector3D approachLocal;
            if (corridor)
            {
                approachLocal = connectorLocal + WorldToAnchorDir(ref hm, aH) * CORRIDOR_SLIDE;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: dock corridor at {1}{2}: {3}", Entity.EntityId, target.CustomName,
                          heading > 0 ? string.Format(" (heading turned {0}°)", heading * 90) : "", corridorHow);
            }
            else
            {
                // The old way: the approach point out along the axis, further out until there is room to turn there -
                // only when that line is clear (it used to be flown through a hangar's roof)
                approachLocal = connectorLocal + WorldToAnchorDir(ref hm, aH) * DockApproachDistance;
                approachLocal = WorldToAnchorPoint(ref hm, WithTurnRoom(AnchorToWorldPoint(ref hm, approachLocal), aH, hullFromConnector, ref dockFrame));
                string lineBlocker;
                if (!ApproachLineClear(p0World + hullFromConnector, AnchorToWorldPoint(ref hm, approachLocal) + hullFromConnector, ref dockFrame, out lineBlocker))
                {
                    corridorIgnore = null;
                    Report(StrDockNoWayInCorrectedPose, target.CustomName, lineBlocker, corridorHow);
                    return null;
                }
                if (Log.DebugEnabled) Log.Debug("Drone {0}: no dock corridor at {1} ({2}): approach point {3:F1} m out", Entity.EntityId, target.CustomName,
                          corridorHow, Vector3D.Distance(approachLocal, connectorLocal));
            }
            corridorIgnore = null;
            prep.DockOffset = dockOffset;
            prep.DockForward = dockForward;
            prep.DockUp = dockUp;
            prep.Hm = hm;
            prep.DockFrame = dockFrame;
            prep.AH = aH;
            prep.ConnectorLocal = connectorLocal;
            prep.ApproachLocal = approachLocal;
            prep.HullFromConnector = hullFromConnector;
            prep.ExitDir = exitDir;
            prep.Corridor = corridor;
            prep.Berth = corridorBerth;
            if (!corridor) prep.CorridorPts.Clear();
            return prep;
        }

        // The route to the approach point (back out of a dock first), the corridor legs, the dock order
        private bool FinishDock(DockPrep prep)
        {
            var target = prep.Target;
            int mi = prep.Mi;
            var droneConnector = connectorMounts[mi].Block as IMyShipConnector;
            ToolMount mount = connectorMounts[mi];
            MatrixD hm = prep.Hm;
            MatrixD dockFrame = prep.DockFrame;
            Vector3D connectorLocal = prep.ConnectorLocal, approachLocal = prep.ApproachLocal, hullFromConnector = prep.HullFromConnector;
            Vector3DData dockOffset = prep.DockOffset, dockForward = prep.DockForward, dockUp = prep.DockUp;
            dockCorridor = prep.Corridor;
            dockCorridorPts.Clear();
            dockCorridorPts.AddRange(prep.CorridorPts);
            CorridorFlight(prep.Berth, out dockCorridorSpeed, out dockLineTol);

            // Stand-alone, going home: around the observation area, not through what the drone just built there.
            // Every stretch through the pathfinder (obstacles the drone sees, terrain).
            dockRoutePoints.Clear();
            bool areaDetour = settings.OperationMode == OperationMode.StandAlone && target.EntityId == settings.HomeConnectorId;
            // The route steers the hull centre; at the approach point, in the docking attitude, it sits here:
            Vector3D approachWorld = AnchorToWorldPoint(ref hm, approachLocal);
            // ...or, with a corridor, the turn point at its outer end
            Vector3D hullAtApproach = dockCorridor ? dockCorridorPts[0] : approachWorld + hullFromConnector;
            Vector3D start = HullCentreWorld;
            var docked = DockedConnector();
            int backOuts = 0;
            backOutSpeedCap = 0;
            // Docked elsewhere: out of that dock first (straight out, or its corridor), then the route
            if (docked != null) backOuts = DeparturePoints(docked, ref start, dockRoutePoints);
            else
            {
                TakeContactBackOut(ref start, dockRoutePoints);         // pressed against something on the way: away from it first
                ClimbOut(ref start, DroneRadius(), dockRoutePoints);   // in a bore hole: out of it first
                backOuts = dockRoutePoints.Count;                      // (those legs hold the attitude, like FlyRoute's)
            }
            undockLegs = docked != null ? backOuts : 2;
            // The 4th endpoint: the route ends up to 50 m out from the turn point; in from there at Approach speed
            Vector3D staging = DockStagingPoint(hullAtApproach, dockCorridor ? prep.ExitDir : prep.AH);
            bool staged = Vector3D.DistanceSquared(staging, hullAtApproach) > 4;
            dockAreaSet = true;   // planned near the platform: no attitude turns there (NearDockArea)
            dockAreaCentre = target.GetPosition();
            bool planned;
            try { planned = PlanPath(start, staged ? staging : hullAtApproach, dockRoutePoints, areaDetour, false); }
            finally { dockAreaSet = false; }
            if (!planned)
            {
                Report(StrDockNoRouteTo, target.CustomName);
                return false;
            }
            if (staged)
            {
                dockRoutePoints.Add(staging);
                if (Log.DebugEnabled) Log.Debug("Drone {0}: docking staging point {1:F0} m out from the turn point", Entity.EntityId, Vector3D.Distance(staging, hullAtApproach));
            }
            // Legs anchored to the connector (moving bases) with the pathfinder's attitudes, a turn in place at the
            // approach point to the docking attitude, then the dock order (zero-length transit, straight in)
            dockLegs.Clear();
            if (BuildHullLegs(target, ref hm, dockRoutePoints, backOuts, hullAtApproach, dockFrame.Forward, dockFrame.Up, dockLegs) == null)
            {
                Report(StrDockOrderRefused);
                return false;
            }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: docking route, {1} waypoints", Entity.EntityId, dockRoutePoints.Count);
            // Staging point -> turn point: Approach speed (the leg before the turn in place)
            if (staged && dockLegs.Count >= 2) dockLegs[dockLegs.Count - 2].SpeedCap = ApproachSpeed;
            dockRoutePoints.Clear();
            dockLegs.Clear();
            // The corridor: in the dock attitude, precise and slow, a full stop at each corner
            dockCorridorLegs.Clear();
            float slow = dockCorridorSpeed;
            if (dockCorridor)
                for (int i = 1; i < dockCorridorPts.Count; i++)
                {
                    FlightOrder leg = QueueGoTo(dockCorridorPts[i], Pathfinding.WaypointBehavior.FullStop, 0);
                    leg.ReferenceOffset = Vector3DData.FromVector3D(HullCentreLocal);
                    SetExplicitAttitude(leg, false, ref hm, dockFrame.Forward, dockFrame.Up);
                    MakeCorridorLeg(leg, slow, dockLineTol);
                    dockCorridorLegs.Add(leg);
                }
            FlightOrder order = QueueGoTo(AnchorToWorldPoint(ref hm, connectorLocal), Pathfinding.WaypointBehavior.FullStop, 0);
            order.TargetLocal = Vector3DData.FromVector3D(connectorLocal);
            order.ApproachFromLocal = order.TransitStartLocal = Vector3DData.FromVector3D(approachLocal);
            order.UseApproachLine = true;
            order.Phase = FlightPhase.Transit;
            order.HoldAttitudeInTransit = true;
            order.ReferenceOffset = Vector3DData.FromVector3D(mount.LocalPoint);
            order.Orientation = FlightOrientationMode.Explicit;
            order.ForwardLocal = dockForward;
            order.UpLocal = dockUp;
            order.IgnoreGravityLimits = true;
            order.FinalSpeed = SafeSpeed;
            order.LineTolerance = DOCK_LINE_TOLERANCE;   // not WaypointTolerance: connectors need ~0.59 m
            order.ArrivalTolerance = 0.2f;
            if (dockCorridor)
            {
                // The slide: short and slow, on a tight line
                order.FinalSpeed = slow;
                order.SpeedCap = slow;
                order.LineTolerance = dockLineTol;
            }
            dockingOrder = order;
            RememberDocking(target.EntityId, mi, dockOffset, dockForward, dockUp);

            if (droneConnector != null)
            {
                EnsurePowerTransferOverride(droneConnector);
            }
            dockingHomeId = target.EntityId;   // the connector being docked at (home or not)
            ClaimConnector(target.EntityId);
            dockingConnectorId = droneConnector != null ? droneConnector.EntityId : 0;
            dockingWaitTicks = 0;
            dockStuckTicks = dockNudges = dockCorrections = 0;
            dockPoseVerified = false;   // computed again once close (VerifyDockPose)
            return true;
        }

        /// <summary>
        /// Waypoints (controller, world) that take the drone from 'start' to 'goal' around the observation area
        /// (inflated by the hull + clearance) instead of through it: out through one face of the box, along that
        /// face's plane, back in only if the goal is inside. The cheapest face wins; never under the area in
        /// gravity. Nothing added when the straight line already misses the area.
        /// Box only, no obstacle search: the pathfinder will do that (see PathfindingManager).
        /// 'onlyFromOutside': nothing when the start is in the area (moving between blocks inside it).
        /// Returns true when waypoints were added.
        /// </summary>
        private bool PlanObservationAreaDetour(Vector3D start, Vector3D goal, List<Vector3D> points, bool onlyFromOutside)
        {
            MatrixD box;
            Vector3D half;
            if (!TryGetObservationArea(out box, out half)) return false;
            Vector3D h = half + new Vector3D(DroneRadius() + CONSTRUCTION_CLEARANCE);
            Vector3D c = box.Translation;
            Vector3D s = WorldToAnchorDir(ref box, start - c), g = WorldToAnchorDir(ref box, goal - c);
            if (!SegmentHitsBox(s, g, h)) return false;

            Vector3D gUp = flightState.InGravity ? WorldToAnchorDir(ref box, flightState.GravityUp) : Vector3D.Zero;
            bool sInside = InsideBox(s, h), gInside = InsideBox(g, h);
            if (sInside && onlyFromOutside) return false;
            double best = double.MaxValue;
            Vector3D bestS = s, bestG = g;
            for (int i = 0; i < 6; i++)
            {
                Vector3D n = (Vector3D)Base6Directions.GetIntVector((Base6Directions.Direction)i);
                double upDot = Vector3D.Dot(n, gUp);
                if (upDot < -0.7) continue;                            // under the area: through the ground / floor
                // Out onto that face's plane (a little past it); points already beyond it stay
                double plane = Math.Abs(Vector3D.Dot(h, n)) + 0.5;
                Vector3D s2 = s + n * Math.Max(0, plane - Vector3D.Dot(s, n));
                Vector3D g2 = g + n * Math.Max(0, plane - Vector3D.Dot(g, n));
                // Out of the area (start inside) and back in (goal inside) are the only crossings allowed
                if (!sInside && SegmentHitsBox(s, s2, h)) continue;
                if (!gInside && SegmentHitsBox(g2, g, h)) continue;
                double len = Vector3D.Distance(s, s2) + Vector3D.Distance(s2, g2) + Vector3D.Distance(g2, g);
                len -= upDot * 2.0;                                    // slight preference for over the top
                if (len < best) { best = len; bestS = s2; bestG = g2; }
            }
            if (best == double.MaxValue) return false;
            int before = points.Count;
            if (Vector3D.DistanceSquared(bestS, s) > 0.25) points.Add(c + AnchorToWorldDir(ref box, bestS));
            if (Vector3D.DistanceSquared(bestG, g) > 0.25 && Vector3D.DistanceSquared(bestG, bestS) > 0.25)
                points.Add(c + AnchorToWorldDir(ref box, bestG));
            return points.Count > before;
        }

        private static bool InsideBox(Vector3D p, Vector3D h)
        {
            return Math.Abs(p.X) < h.X && Math.Abs(p.Y) < h.Y && Math.Abs(p.Z) < h.Z;
        }

        // Segment a-b against the box -h..h (slab test)
        private static bool SegmentHitsBox(Vector3D a, Vector3D b, Vector3D h)
        {
            Vector3D d = b - a;
            double t0 = 0, t1 = 1;
            for (int axis = 0; axis < 3; axis++)
            {
                double o = axis == 0 ? a.X : axis == 1 ? a.Y : a.Z;
                double v = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                double e = axis == 0 ? h.X : axis == 1 ? h.Y : h.Z;
                if (Math.Abs(v) < 1e-9)
                {
                    if (o <= -e || o >= e) return false;
                    continue;
                }
                double ta = (-e - o) / v, tb = (e - o) / v;
                if (ta > tb) { double t = ta; ta = tb; tb = t; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 >= t1) return false;
            }
            return true;
        }

        #region Connector power
        // Connectors the AI switched off (handed back on when the AI is disabled)
        private readonly HashSet<long> connectorsOffByAI = new HashSet<long>();

        /// <summary>
        /// UpdateBeforeSimulation10, AI on: drone connectors are off unless docking. An enabled connector's magnet
        /// grabs any connector it passes close to, and the drone may not have the thrust to pull free. Only the
        /// connector used for docking comes on, once the dock order is at its approach point (Align / Approach).
        /// Parked drones and connected connectors are left alone.
        /// </summary>
        private void UpdateConnectorPower()
        {
            if (isParked || preflightStage == 1) return;
            var o = activeFlightOrder;
            bool docking = dockingHomeId != 0 && o != null && o == dockingOrder
                           && (o.Phase == FlightPhase.Align || o.Phase == FlightPhase.Approach);
            SetConnectorsPower(docking ? dockingConnectorId : 0);
        }

        // All connecting connectors off except 'onId' (0 = none); connected ones untouched
        private void SetConnectorsPower(long onId)
        {
            for (int i = 0; i < connectors.Count; i++)
            {
                var c = connectors[i];
                if (c == null || c.Closed || c.IsConnected) continue;
                if (i < connectorMounts.Count && !connectorMounts[i].CanConnect) continue;   // ejectors
                bool on = c.EntityId == onId;
                if (c.Enabled == on) continue;
                c.Enabled = on;
                if (on) connectorsOffByAI.Remove(c.EntityId);
                else connectorsOffByAI.Add(c.EntityId);
            }
        }

        // AI off: the player gets the connectors back as they were
        private void RestoreConnectors()
        {
            IMyEntity e;
            foreach (long id in connectorsOffByAI)
            {
                var c = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyShipConnector : null;
                if (c != null && !c.Closed && !c.Enabled) c.Enabled = true;
            }
            connectorsOffByAI.Clear();
        }
        #endregion

        // UpdateBeforeSimulation10: lock as soon as the connector can, then power down.
        private void UpdateDocking()
        {
            if (dockingHomeId == 0 || preflightStage != 0) return;   // still undocking from somewhere else
            var o = activeFlightOrder;
            if (o == null || o.AnchorEntityId != dockingHomeId) { dockingHomeId = 0; return; }   // order replaced

            int mi = FindConnectorMount(dockingConnectorId);
            var c = mi >= 0 ? connectorMounts[mi].Block as IMyShipConnector : null;
            if (c == null || c.Closed) { Report(StrDockingAbortedDroneConnectorMissing); dockingHomeId = 0; return; }

            bool final = o == dockingOrder;   // not the legs around the observation area
            if (++dockDiagCalls >= DOCK_DIAG_CALLS)
            {
                dockDiagCalls = 0;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: docking at {1}: {2} {3}, {4:F1} m to its target, {5:F2} m/s relative, connector {6}, {7} leg(s) queued, wait {8} s",
                          Entity.EntityId, dockingHomeId, final ? "final order" : "route leg", o.Phase, Vector3D.Distance(ReferencePoint(o), o.Target.ToVector3D()),
                          flightState.RelativeVelocity.Length(), c.Status, queuedLegs.Count, dockingWaitTicks / 60);
            }
            // With a corridor the pose is checked before the drone goes in (the corridor depends on it), not at its end
            if (dockCorridor && !dockPoseVerified && !final && !dockCorridorLegs.Contains(o) && ReplanDockIfMoved()) return;
            if (final && !dockPoseVerified) { VerifyDockPose(o, ref c); if (c == null) return; }
            if (final && o.Phase == FlightPhase.Approach && currentState != State.Docking) SetState(State.Docking);
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
            if (final && o.Phase == FlightPhase.Approach)
            {
                // On the dock pose and still no lock: say why once, then press in a little (0.25 m steps)
                if (Vector3D.DistanceSquared(ReferencePoint(o), o.Target.ToVector3D()) < 0.36 && flightState.RelativeVelocity.LengthSquared() < 0.01)
                {
                    dockStuckTicks += 10;
                    if (dockStuckTicks >= DOCK_NUDGE_TICKS && dockNudges < DOCK_NUDGES_MAX)
                    {
                        if (dockNudges == 0 && dockCorrections == 0)
                            if (Log.DebugEnabled) Log.Debug("Drone {0}: on the dock pose, no lock: {1}; {2}", Entity.EntityId, DockFailDetail(c), DockPoseDetail(o, c));
                        if (!CorrectDock(o, c)) NudgeDock(o);
                    }
                }
                // Time allowed: the final approach at Safe speed, plus the timeout
                dockingWaitTicks += 10;
                int limit = (int)((dockCorridor ? CORRIDOR_SLIDE : DockApproachDistance) / SafeSpeed * 60) + DOCK_TIMEOUT_TICKS;
                if (dockingWaitTicks >= limit)
                {
                    NoteSpeedTimeout(Loc.T(StrDocking));
                    Report(StrDockingFailedConnectorDidNot, DockFailDetail(c));
                    ClearFlightOrder();
                    SetState(State.Standby);
                }
            }
        }

        #region Timing
        private const int DOCK_NUDGE_TICKS = 180;             // on the dock pose this long without a lock: press in
        #endregion
        private const int DOCK_NUDGES_MAX = 3;
        private const int DOCK_DIAG_CALLS = 90;              // UpdateDocking calls (every 10 ticks): a progress line every 15 s
        private int dockDiagCalls;
        private const double DOCK_NUDGE_STEP = 0.25;          // m along the target connector's axis
        private int dockStuckTicks, dockNudges;

        private const double DOCK_VERIFY_DISTANCE = 40;   // m: the pose is computed again this close (the full model is loaded)
        private bool dockPoseVerified;

        /// <summary>
        /// The dock pose is computed when the order is given, often from far away, where the target connector's model may
        /// lack its "connector" dummy (a far LOD): the fallback axis can be wrong, and the pose with it (a side connector
        /// picked for a floor port, the target 7.5 m off). Once close, it is computed again from the live geometry; a
        /// different mount or pose updates the dock order in place (back to the approach point when it changed).
        /// </summary>
        private void VerifyDockPose(FlightOrder o, ref IMyShipConnector c)
        {
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(dockingHomeId, out e) ? e as IMyShipConnector : null;
            if (target == null) { dockPoseVerified = true; return; }
            // A home pose the player recorded ("Set current as home") is what it is
            if (settings.HomeDockRecorded && dockingHomeId == settings.HomeConnectorId) { dockPoseVerified = true; return; }
            if (Vector3D.DistanceSquared(HullCentreWorld, target.GetPosition()) > DOCK_VERIFY_DISTANCE * DOCK_VERIFY_DISTANCE) return;
            dockPoseVerified = true;
            Vector3DData offset, forward, up;
            int mi;
            double tilt;
            if (!ComputeDockPose(target, out offset, out forward, out up, out mi, out tilt)) return;
            var mount = connectorMounts[mi];
            MatrixD hm = target.WorldMatrix;
            MatrixD frameWorld = MatrixD.CreateWorld(Vector3D.Zero, AnchorToWorldDir(ref hm, forward.ToVector3D()), AnchorToWorldDir(ref hm, up.ToVector3D()));
            Vector3D connectorLocal = offset.ToVector3D() + WorldToAnchorDir(ref hm, Vector3D.TransformNormal(mount.LocalPoint, frameWorld));
            double moved = Vector3D.Distance(connectorLocal, o.TargetLocal.ToVector3D());
            double turned = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(forward.ToVector3D(), o.ForwardLocal.ToVector3D()), -1, 1)));
            bool sameMount = mount.Block != null && mount.Block.EntityId == dockingConnectorId;
            if (sameMount && moved < 0.2 && turned < 2) return;
            Vector3D pH, aH;
            ConnectorGeometry(target, out pH, out aH);
            Vector3D approachLocal;
            if (dockCorridor)
            {
                // At the end of the corridor: only a small correction fits (no turning, no other connector); the heading
                // the corridor was planned with is kept
                double turnedFree = HeadingFreeTurn(forward.ToVector3D(), up.ToVector3D(), o.ForwardLocal.ToVector3D(), o.UpLocal.ToVector3D());
                if (!sameMount || turnedFree > 3 || moved > 0.5)
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: dock pose re-computed in the corridor of {1}: {2:F2} m / {3:F0}° off, mount {4} -> {5}: kept the planned one",
                              Entity.EntityId, target.CustomName, moved, turned, FindConnectorMount(dockingConnectorId), mi);
                    return;
                }
                approachLocal = connectorLocal + WorldToAnchorDir(ref hm, aH) * CORRIDOR_SLIDE;
                forward = o.ForwardLocal;
                up = o.UpLocal;
                MatrixD planned = MatrixD.CreateWorld(Vector3D.Zero, AnchorToWorldDir(ref hm, forward.ToVector3D()), AnchorToWorldDir(ref hm, up.ToVector3D()));
                offset = Vector3DData.FromVector3D(connectorLocal - WorldToAnchorDir(ref hm, Vector3D.TransformNormal(mount.LocalPoint, planned)));
            }
            else
            {
                approachLocal = connectorLocal + WorldToAnchorDir(ref hm, aH) * DockApproachDistance;
                Vector3D hullFromConnector = Vector3D.TransformNormal(HullCentreLocal - mount.LocalPoint, frameWorld);
                approachLocal = WorldToAnchorPoint(ref hm, WithTurnRoom(AnchorToWorldPoint(ref hm, approachLocal), aH, hullFromConnector, ref frameWorld));
            }
            if (Log.DebugEnabled) Log.Debug("Drone {0}: dock pose re-computed close to {1}: target {2:F2} m off, attitude {3:F0}° off, mount {4} -> {5}",
                      Entity.EntityId, target.CustomName, moved, turned, FindConnectorMount(dockingConnectorId), mi);
            o.TargetLocal = Vector3DData.FromVector3D(connectorLocal);
            o.ApproachFromLocal = Vector3DData.FromVector3D(approachLocal);
            o.ReferenceOffset = Vector3DData.FromVector3D(mount.LocalPoint);
            o.ForwardLocal = forward;
            o.UpLocal = up;
            if (o.Phase != FlightPhase.Transit && o.Phase != FlightPhase.MatchSpeed)
            {
                o.Phase = FlightPhase.Transit;   // to the new approach point from here, then straight in
                o.TransitStartLocal = Vector3DData.FromVector3D(WorldToAnchorPoint(ref hm, ReferencePoint(o)));
                o.Captured = false;
            }
            o.Arrived = false;
            RefreshAnchoredOrder(o);
            RememberDocking(target.EntityId, mi, offset, forward, up);
            if (!sameMount)
            {
                SetConnectorsPower(0);
                dockingConnectorId = mount.Block.EntityId;
                var dc = mount.Block as IMyShipConnector;
                if (dc != null) EnsurePowerTransferOverride(dc);
                c = dc;
            }
            dockingWaitTicks = 0;
            dockStuckTicks = dockNudges = dockCorrections = 0;
            taskProgressFrame = MyAPIGateway.Session.GameplayFrameCounter;
            watchedOrder = null;
        }

        /// <summary>
        /// Corridor docking, on the way in (route legs), once within DOCK_VERIFY_DISTANCE: the dock pose computed again
        /// from the live geometry (far LODs can give a wrong one). Changed: the whole docking is planned again from here,
        /// corridor included (true). Unchanged / too far / recorded home pose: false.
        /// </summary>
        private bool ReplanDockIfMoved()
        {
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(dockingHomeId, out e) ? e as IMyShipConnector : null;
            var o = dockingOrder;
            if (target == null || o == null) { dockPoseVerified = true; return false; }
            if (settings.HomeDockRecorded && dockingHomeId == settings.HomeConnectorId) { dockPoseVerified = true; return false; }
            if (Vector3D.DistanceSquared(HullCentreWorld, target.GetPosition()) > DOCK_VERIFY_DISTANCE * DOCK_VERIFY_DISTANCE) return false;
            dockPoseVerified = true;
            Vector3DData offset, forward, up;
            int mi;
            double tilt;
            if (!ComputeDockPose(target, out offset, out forward, out up, out mi, out tilt)) return false;
            var mount = connectorMounts[mi];
            MatrixD hm = target.WorldMatrix;
            MatrixD frameWorld = MatrixD.CreateWorld(Vector3D.Zero, AnchorToWorldDir(ref hm, forward.ToVector3D()), AnchorToWorldDir(ref hm, up.ToVector3D()));
            Vector3D connectorLocal = offset.ToVector3D() + WorldToAnchorDir(ref hm, Vector3D.TransformNormal(mount.LocalPoint, frameWorld));
            double moved = Vector3D.Distance(connectorLocal, o.TargetLocal.ToVector3D());
            // Connectors meet at any of four headings about their axis (the corridor may have picked another)
            double turned = HeadingFreeTurn(forward.ToVector3D(), up.ToVector3D(), o.ForwardLocal.ToVector3D(), o.UpLocal.ToVector3D());
            bool sameMount = mount.Block != null && mount.Block.EntityId == dockingConnectorId;
            if (sameMount && moved < 0.1 && turned < 1) return false;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: dock pose re-computed close to {1} before its corridor: {2:F2} m / {3:F0}° off, mount {4} -> {5}: planned again",
                      Entity.EntityId, target.CustomName, moved, turned, FindConnectorMount(dockingConnectorId), mi);
            ResetRoute();
            if (!StartDocking(target, mi, offset, forward, up, true))
            {
                Report(StrDockNoWayInStraightBlocked, target.CustomName);
                ClearFlightOrder();
                SetState(State.Standby);
                return true;
            }
            dockPoseVerified = true;
            return true;
        }

        // Degrees between two dock attitudes (anchor frame); with the same up, quarter turns about it don't count
        private static double HeadingFreeTurn(Vector3D fwdA, Vector3D upA, Vector3D fwdB, Vector3D upB)
        {
            double turned = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(Vector3D.Dot(fwdA, fwdB), -1, 1)));
            if (Vector3D.Dot(upA, upB) < 0.99) return turned;
            double m = turned % 90;
            return Math.Min(m, 90 - m);
        }

        private const int DOCK_CORRECTIONS_MAX = 2;
        private const double DOCK_CORRECT_MIN = 0.3;    // m: a measured gap above this is flown off first
        private int dockCorrections;

        // On the pose, still apart: move the target by the gap the game's own geometry shows between the two connection
        // points (whatever made the pose wrong: a model seen from afar, a stale mount), then try again
        private bool CorrectDock(FlightOrder o, IMyShipConnector c)
        {
            if (dockCorrections >= DOCK_CORRECTIONS_MAX) return false;
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(dockingHomeId, out e) ? e as IMyShipConnector : null;
            if (target == null) return false;
            Vector3D pd, ad, pt, at;
            ConnectorGeometry(c, out pd, out ad);
            ConnectorGeometry(target, out pt, out at);
            Vector3D gap = pt - pd;
            if (gap.Length() < DOCK_CORRECT_MIN) return false;
            dockCorrections++;
            dockStuckTicks = 0;
            dockingWaitTicks = 0;                                         // the time allowed starts again
            taskProgressFrame = MyAPIGateway.Session.GameplayFrameCounter; // and the job's stall clock
            watchedOrder = null;
            MatrixD hm = target.WorldMatrix;
            o.TargetLocal = Vector3DData.FromVector3D(o.TargetLocal.ToVector3D() + WorldToAnchorDir(ref hm, gap));
            o.Arrived = false;
            RefreshAnchoredOrder(o);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: dock correction {1}/{2}: target moved {3:F2} m (measured gap between the connection points)",
                      Entity.EntityId, dockCorrections, DOCK_CORRECTIONS_MAX, gap.Length());
            return true;
        }

        // Where the error is: the order's reference point vs the drone connector's live connection point (stale mount),
        // the order's target vs the target connector's live connection point (wrong pose)
        private string DockPoseDetail(FlightOrder o, IMyShipConnector c)
        {
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(dockingHomeId, out e) ? e as IMyShipConnector : null;
            if (target == null) return "";
            Vector3D pd, ad, pt, at;
            ConnectorGeometry(c, out pd, out ad);
            ConnectorGeometry(target, out pt, out at);
            int mi = FindConnectorMount(c.EntityId);
            return string.Format("reference vs drone connector {0:F2} m, order target vs target connector {1:F2} m, mount {2} facing {3}",
                Vector3D.Distance(ReferencePoint(o), pd), Vector3D.Distance(o.Target.ToVector3D(), pt), mi,
                mi >= 0 ? Base6Directions.GetClosestDirection((Vector3)connectorMounts[mi].LocalForward).ToString() : "?");
        }

        private void NudgeDock(FlightOrder o)
        {
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(dockingHomeId, out e) ? e as IMyShipConnector : null;
            dockStuckTicks = 0;
            dockNudges++;
            if (target == null) return;
            Vector3D p, a;
            ConnectorGeometry(target, out p, out a);
            MatrixD hm = target.WorldMatrix;
            o.TargetLocal = Vector3DData.FromVector3D(o.TargetLocal.ToVector3D() - WorldToAnchorDir(ref hm, a) * DOCK_NUDGE_STEP);
            o.Arrived = false;
            RefreshAnchoredOrder(o);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: dock nudge {1}/{2}: {3:F2} m into {4}", Entity.EntityId, dockNudges, DOCK_NUDGES_MAX, dockNudges * DOCK_NUDGE_STEP, target.CustomName);
        }

        // Why two connectors on the dock pose don't lock: power, owners, or the geometry (gap / axes) as the game sees it
        private string DockFailDetail(IMyShipConnector c)
        {
            IMyEntity e;
            var target = MyAPIGateway.Entities.TryGetEntityById(dockingHomeId, out e) ? e as IMyShipConnector : null;
            if (target == null || target.Closed) return Loc.T(StrTargetConnectorGone);
            if (!c.Enabled || !c.IsWorking) return Loc.F(StrDroneConnectorOff, c.CustomName);
            if (!target.Enabled || !target.IsWorking) return Loc.T(StrTargetConnectorOffUnpowered);
            var rel = target.GetUserRelationToOwner(c.OwnerId);
            if (rel == MyRelationsBetweenPlayerAndBlock.Enemies || rel == MyRelationsBetweenPlayerAndBlock.Neutral)
                return Loc.F(StrOwnersNotFriendly, rel);
            Vector3D pd, ad, pt, at;
            ConnectorGeometry(c, out pd, out ad);
            ConnectorGeometry(target, out pt, out at);
            double angle = MathHelperD.ToDegrees(Math.Acos(MathHelperD.Clamp(-Vector3D.Dot(ad, at), -1, 1)));
            return Loc.F(StrGapMAlongAcrossAxes,
                Vector3D.Distance(pd, pt), Vector3D.Dot(pd - pt, at), ((pd - pt) - at * Vector3D.Dot(pd - pt, at)).Length(), angle,
                c.Status, target.Status, c.BlockDefinition.SubtypeId, target.BlockDefinition.SubtypeId);
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
            Report(StrDockedAt, c.OtherConnector != null ? c.OtherConnector.CustomName : Loc.T(StrHome));
            if (needsBatteryRecharge) Report(StrRechargingBatteries, batteryPercent);
            if (needsHydrogenRefuel) Report(StrRefuelingHydrogen, h2Percent);
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
        /// <summary>
        /// Pre-flight: gyroscopes it can't turn without. Functional ones switched off (by hand, or left off) go back on;
        /// what still doesn't work (no power, damaged) is logged, and reported when none works.
        /// </summary>
        private void CheckFlightSystems()
        {
            int working = 0, switchedOn = 0, damaged = 0, unpowered = 0;
            for (int i = 0; i < gyroscopes.Count; i++)
            {
                var gy = gyroscopes[i].Gyro;
                if (gy == null || gy.Closed) continue;
                if (!gy.IsFunctional) { damaged++; continue; }
                if (!gy.Enabled) { gy.Enabled = true; switchedOn++; }
                if (gy.IsWorking) working++; else unpowered++;
            }
            int thrustersWorking = 0, thrustersTotal = 0, thrustersOn = 0;
            for (int d = 0; d < 6; d++)
                for (int i = 0; i < allThrusters[d].Count; i++)
                {
                    var t = allThrusters[d][i];
                    thrustersTotal++;
                    // Functional but switched off (by hand, or left off): a managed drone flies on all of them
                    if (t.IsFunctional && !t.Enabled) { t.Enabled = true; thrustersOn++; }
                    if (t.IsWorking) thrustersWorking++;
                }
            if (thrustersOn > 0) Report(StrPreFlightThrustersOn, thrustersOn);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: pre-flight systems: gyros {1}/{2} working ({3} switched back on, {4} damaged, {5} without power), thrusters {6}/{7} working",
                      Entity.EntityId, working, gyroscopes.Count, switchedOn, damaged, unpowered, thrustersWorking, thrustersTotal);
            if (working == 0) Report(StrPreFlightNoWorkingGyroscope, damaged, unpowered);
            // Obstacles are only seen through working sensors (all around) / cameras (their facing): flying without
            // is flying into what nothing saw
            if (AutomataSession.GetConfig().Pathfinding.RequireSensorsForPathfinding() && CountOn(sensors) == 0)
                Report(StrPreFlightNoSensorOn, sensors.Count, CountOn(cameraBlocks));
        }

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
                        || b is IMyShipDrill || b is IMySensorBlock || b is IMyCameraBlock || b is IMySoundBlock)   // ore detectors stay on (surveys, scout bids)
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

        // Every battery back to Auto and no tank stockpiling, whoever set them (the player, a previous controller
        // whose list is lost): undocking on Recharge batteries cuts the drone's power the moment the connector lets go
        private void ReleasePowerStorage()
        {
            for (int i = 0; i < batteries.Count; i++)
            {
                var b = batteries[i];
                if (b != null && !b.Closed && b.ChargeMode != Sandbox.ModAPI.Ingame.ChargeMode.Auto)
                    b.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
            }
            for (int i = 0; i < hydrogenTanks.Count; i++)
            {
                var t = hydrogenTanks[i];
                if (t != null && !t.Closed && t.Stockpile) t.Stockpile = false;
            }
        }

        // null when the drone can power itself off the connector: a charged, working battery (Auto / Discharge)
        // or another working power producer (reactor, hydrogen engine...)
        private string OwnPowerProblem()
        {
            int fitted = 0, onRecharge = 0, empty = 0;
            for (int i = 0; i < batteries.Count; i++)
            {
                var b = batteries[i];
                if (b == null || b.Closed || !b.IsFunctional) continue;
                fitted++;
                if (!b.Enabled) continue;
                if (b.ChargeMode == Sandbox.ModAPI.Ingame.ChargeMode.Recharge) { onRecharge++; continue; }
                if (b.CurrentStoredPower <= 0.001f) { empty++; continue; }
                return null;
            }
            _gridCache.Clear();
            shipController.CubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(_gridCache);
            foreach (var grid in _gridCache)
                foreach (var p in grid.GetFatBlocks<IMyPowerProducer>())
                    if (!(p is IMyBatteryBlock) && !(p is IMySolarPanel) && p.Enabled && p.IsWorking) { _gridCache.Clear(); return null; }
            _gridCache.Clear();
            if (fitted == 0) return Loc.T(StrNoBatteryOrPowerProducer);
            if (onRecharge > 0) return Loc.T(StrBatteriesStuckOnRecharge);
            if (empty > 0) return Loc.T(StrBatteriesEmpty);
            return Loc.T(StrBatteriesSwitchedOff);
        }

        private void WakeSystems()
        {
            IMyEntity e;
            ReleasePowerStorage();
            foreach (long id in settings.SleepingBlockIds)
            {
                var b = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyFunctionalBlock : null;
                if (b != null && !b.Closed) b.Enabled = true;
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
                // Still on the base's power: let go only with power of its own, or it drops (and shuts down) at once
                string noPower = OwnPowerProblem();
                if (noPower != null)
                {
                    Report(StrPreFlightStayingDocked, noPower);
                    if (currentJob != null) StopJob(noPower);
                    ClearFlightOrder();
                    return;   // next update: order gone -> back to sleep, docked
                }
                RememberDeparture();   // no home connector: where to come back to (combat drones, ore scouts)
                goingToDeparture = false;
                for (int i = 0; i < connectors.Count; i++)
                    if (connectors[i].IsConnected) connectors[i].Disconnect();
                SetConnectorsPower(0);   // released: magnets off before the drone moves
                OnTakeOffGears();   // unlocked, and off for the flight
                preflightStage = 2;
                preflightTicks = PREFLIGHT_SETTLE_TICKS;
                return;
            }
            // stage 2: free of the base; measure what we actually carry
            preflightStage = 0;
            legsSinceUndock = 0;   // the first two legs (backing out of the dock) aren't checked at their start
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
            Report(StrPreFlightKgLoad, physicalMass, pct);
            CheckFlightSystems();
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
            RefreshThrustTable();
            if (!settings.IsEnabled) return;   // AI off: levels for the header only

            bool docked = currentState == State.Docked;
            if (settings.MonitorBatteryLevels && batteryPercent >= 0)
            {
                if (!needsBatteryRecharge && batteryPercent < settings.BatteryRefuelThreshold)
                {
                    needsBatteryRecharge = true;
                    Report(StrBatteryLowRecharging, batteryPercent, settings.BatteryRefuelThreshold);
                }
                else if (needsBatteryRecharge && batteryPercent >= Math.Max(settings.BatteryOperationalThreshold, settings.BatteryRefuelThreshold + 5))
                {
                    needsBatteryRecharge = false;
                    Report(StrBatteryRecharged, batteryPercent);
                }
            }
            if (settings.MonitorHydrogenLevels && h2Percent >= 0)
            {
                if (!needsHydrogenRefuel && h2Percent < settings.HydrogenRefuelThreshold)
                {
                    needsHydrogenRefuel = true;
                    Report(StrHydrogenLowRefueling, h2Percent, settings.HydrogenRefuelThreshold);
                }
                else if (needsHydrogenRefuel && h2Percent >= Math.Max(settings.HydrogenOperationalThreshold, settings.HydrogenRefuelThreshold + 5))
                {
                    needsHydrogenRefuel = false;
                    Report(StrHydrogenRefueled, h2Percent);
                }
            }

            // Level-triggered: keep trying to get home (refused, replaced by another order...) with a back-off
            // (docked somewhere else, e.g. at a job's connector, still counts as "not home")
            if ((needsBatteryRecharge || needsHydrogenRefuel) && !Grounded && !CombatHoldsControl && preflightStage == 0 && now - lastAutoHomeFrame >= AUTO_HOME_RETRY_TICKS)
            {
                if (HasHomeConnector && !IsDockedAtHome() && !IsGoingHome)
                {
                    lastAutoHomeFrame = now;
                    GoHome();
                }
                // Park mode: any connector it can dock at (not while a job runs: the job ends at low power itself)
                else if (ParkMode && !AnyConnectorsConnected() && dockingHomeId == 0 && cPhase == ConstructionPhase.Idle && currentJob == null)
                {
                    lastAutoHomeFrame = now;
                    if (parkPhase != ParkPhase.None) EndPark(null);
                    GoCharge();
                }
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
                    Report(StrMaxLoadExceededOffloading, pct, limit);
                    // Construction batches are planned to the limit: only abort one that is still collecting (grinding)
                    if (cPhase == ConstructionPhase.Running && !boring) AbortJobToHome();   // boring: RunMine ends the bore and delivers
                    else if (!IsGoingToRest && (HasHomeConnector || ParkMode) && cPhase == ConstructionPhase.Idle && !CombatHoldsControl) GoRest();
                }
                else Report(StrMaxLoadReachedOf, pct, limit);
            }
            loadLevel = level;
        }
        #endregion
    }
}
