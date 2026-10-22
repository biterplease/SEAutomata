using System;
using System.Collections.Generic;
using System.Globalization;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;
using VRage.Utils;

namespace Automata.Drone
{
    public enum CombatPhase : byte
    {
        /// <summary>Docked / standing by; the AI behaviours are off and the AI Defensive block's detection is watched.</summary>
        Idle,
        /// <summary>Our controller flies the drone toward the threat.</summary>
        Scramble,
        /// <summary>The AI Flight + AI Offensive behaviours fly and fight; we only watch.</summary>
        Handover,
        /// <summary>Behaviours off, Offensive search locked; our controller flies home and docks.</summary>
        Recover,
    }

    /// <summary>Combat handover progress in the drone's save struct (times relative).</summary>
    [ProtoContract]
    public class DroneCombatState
    {
        [ProtoMember(1)] public CombatPhase Phase;
        [ProtoMember(2)] public long ThreatId;
        [ProtoMember(3)] public Vector3DData ThreatPosition;
        [ProtoMember(4)] public int ThreatSeenAgeTicks;
        /// <summary>-1 = it has a target.</summary>
        [ProtoMember(5, IsRequired = true)] public int NoTargetTicks = -1;
        [ProtoMember(6)] public int RescrambleTicksLeft;
        [ProtoMember(7)] public bool Damaged;
        [ProtoMember(8)] public bool SearchLocked;
    }

    /// <summary>
    /// Combat handover (TODO/09), server side: behaviour profile "Combat drone" in "Managed by orchestrator". Between jobs
    /// the AI Defensive block (on the drone, its behaviour off) detects; within the auto-launch radius our controller
    /// undocks and flies the drone out; within the handover distance (both at most 2000 m, the vanilla detection range)
    /// the AI Flight and AI Offensive behaviours are switched on together (the Offensive one errors without an active
    /// Flight block) and fight; with no target for a while, on flee or low power, both go off and our controller flies
    /// the drone home (or to park). We never search for grids ourselves: a threat is only what the player's own AI blocks
    /// report, or what the player designated (designator jobs, below).
    /// </summary>
    public partial class DroneControllerBlock
    {
        #region UI strings
        private static readonly MyStringId StrAiDefensive = MyStringId.GetOrCompute("AI Defensive");
        private static readonly MyStringId StrAiFlight = MyStringId.GetOrCompute("AI Flight");
        private static readonly MyStringId StrAiOffensive = MyStringId.GetOrCompute("AI Offensive");
        private static readonly MyStringId StrInterceptedBy = MyStringId.GetOrCompute("intercepted by {0}");
        private static readonly MyStringId StrUnderFire = MyStringId.GetOrCompute("under fire");
        private static readonly MyStringId StrEngageOrderAtPoint = MyStringId.GetOrCompute("engage order at a point");
        private static readonly MyStringId StrEngageOrderOn = MyStringId.GetOrCompute("engage order on {0}");
        private static readonly MyStringId StrEngageOrder = MyStringId.GetOrCompute("engage order");
        private static readonly MyStringId StrEscortingChat = MyStringId.GetOrCompute("Escorting {0}.");
        private static readonly MyStringId StrAIFlightSpeedLimitM = MyStringId.GetOrCompute("AI Flight speed limit {0:F0} m/s is below the drone's Max speed {1:F0} m/s");
        private static readonly MyStringId StrModeChanged = MyStringId.GetOrCompute("mode changed");
        private static readonly MyStringId StrDroneInError = MyStringId.GetOrCompute("drone in error");
        private static readonly MyStringId StrEnemyDetected = MyStringId.GetOrCompute("enemy detected");
        private static readonly MyStringId StrCombatEnemyDetectedNoHome = MyStringId.GetOrCompute("Combat: enemy detected, no home connector and can't park: staying");
        private static readonly MyStringId StrCombatEnemyDetectedPowerHydrogen = MyStringId.GetOrCompute("Combat: enemy detected, power / hydrogen too low to launch");
        private static readonly MyStringId StrCombatLaunchingM = MyStringId.GetOrCompute("Combat: {0}, launching ({1:F0} m)");
        private static readonly MyStringId StrThreatLost = MyStringId.GetOrCompute("threat lost");
        private static readonly MyStringId StrLowPower = MyStringId.GetOrCompute("low power");
        private static readonly MyStringId StrNoRouteToTheTarget = MyStringId.GetOrCompute("no route to the target");
        private static readonly MyStringId StrEngageFightingItOnThe = MyStringId.GetOrCompute("Engage: {0}, fighting it on the way");
        private static readonly MyStringId StrAIFlightAIOffensiveNot = MyStringId.GetOrCompute("AI Flight / AI Offensive not working");
        private static readonly MyStringId StrCombatAIFlightSpeedLimit = MyStringId.GetOrCompute("Combat: AI Flight speed limit {0:F0} -> {1:F0} m/s (the drone's Max speed)");
        private static readonly MyStringId StrAIFlightRefusedToActivate = MyStringId.GetOrCompute("AI Flight refused to activate");
        private static readonly MyStringId StrAIOffensiveRefusedToActivate = MyStringId.GetOrCompute("AI Offensive refused to activate");
        private static readonly MyStringId StrCombatHandedOverToThe = MyStringId.GetOrCompute("Combat: handed over to the AI ({0:F0} m from the threat)");
        private static readonly MyStringId StrAIBlockLost = MyStringId.GetOrCompute("AI block lost");
        private static readonly MyStringId StrNoTargets = MyStringId.GetOrCompute("no targets");
        private static readonly MyStringId StrFleeing = MyStringId.GetOrCompute("fleeing");
        private static readonly MyStringId StrAIBehaviourSwitchedOff = MyStringId.GetOrCompute("AI behaviour switched off");
        private static readonly MyStringId StrCombatReturningHome = MyStringId.GetOrCompute("Combat: {0}, returning home");
        private static readonly MyStringId StrCombatBackHome = MyStringId.GetOrCompute("Combat: back home");
        private static readonly MyStringId StrEnemyDetectedAgain = MyStringId.GetOrCompute("enemy detected again");
        private static readonly MyStringId StrNewOrder = MyStringId.GetOrCompute("new order");
        private static readonly MyStringId StrEngagingTargetAtGPS = MyStringId.GetOrCompute("Engaging target at GPS:{0}:{1:F0}:{2:F0}:{3:F0}:");
        private static readonly MyStringId StrEscort = MyStringId.GetOrCompute("Escort: {0}");
        private static readonly MyStringId StrTargetNotFoundYet = MyStringId.GetOrCompute("target not found yet");
        private static readonly MyStringId StrInterrupted = MyStringId.GetOrCompute("interrupted");
        private static readonly MyStringId StrDone = MyStringId.GetOrCompute("done");
        private static readonly MyStringId StrGone = MyStringId.GetOrCompute("gone");
        private static readonly MyStringId StrDisabled = MyStringId.GetOrCompute("disabled");
        private static readonly MyStringId StrInterceptorDealtWithBackTo = MyStringId.GetOrCompute("interceptor dealt with, back to the target");
        private static readonly MyStringId StrEscortFollowingAgain = MyStringId.GetOrCompute("Escort: {0}, following {1} again");
        private static readonly MyStringId StrTargetStillOutThere = MyStringId.GetOrCompute("target still out there");
        private static readonly MyStringId StrEscortedTargetGone = MyStringId.GetOrCompute("escorted target gone");
        private static readonly MyStringId StrCombatDone = MyStringId.GetOrCompute("{0}: {1}, done");
        private static readonly MyStringId StrEngage = MyStringId.GetOrCompute("Engage");
        private static readonly MyStringId StrEscortLabel = MyStringId.GetOrCompute("Escort");
        private static readonly MyStringId StrEngageTargetDone = MyStringId.GetOrCompute("Engage: target {0}, done");
        private static readonly MyStringId StrTargetDestroyed = MyStringId.GetOrCompute("Target destroyed");
        private static readonly MyStringId StrTarget = MyStringId.GetOrCompute("Target");
        private static readonly MyStringId StrEscortTargetGoneDone = MyStringId.GetOrCompute("Escort: target gone, done");
        private static readonly MyStringId StrEnemyNearTheEscort = MyStringId.GetOrCompute("enemy near the escort");
        private static readonly MyStringId StrAINotActiveAfterLoad = MyStringId.GetOrCompute("AI not active after load");
        #endregion

        private const string AI_BEHAVIOUR_PROPERTY = "ActivateBehavior";
        #region Timing
        private const int COMBAT_THREAT_LOST_TICKS = 600;        // 10 s without any AI block reporting it: give up
        private const int COMBAT_RETARGET_TICKS = 300;           // re-route toward a moving threat every 5 s
        private const int COMBAT_RESCRAMBLE_TICKS = 3600;        // at most one launch per 60 s after a return
        private const int COMBAT_HOME_RETRY_TICKS = 300;
        private const int COMBAT_BEHAVIOUR_CHECK_TICKS = 100;
        private const int COMBAT_LOW_POWER_LOG_TICKS = 3600;
        #endregion
        private const float COMBAT_RETARGET_MOVED = 100f;        // ... when it moved this far
        private const int COMBAT_ROUTE_FAILS_MAX = 6;            // routes toward the threat failed in a row (one per retarget): give up
        private const double COMBAT_GOAL_CLEARANCE = 40;          // m above the terrain, beyond the drone's size, for the route goal
        private int combatRouteFails;

        private IMyDefensiveCombatBlock aiDefensive;
        private IMyOffensiveCombatBlock aiOffensive;
        private IMyFlightMovementBlock aiFlight;

        private CombatPhase combatPhase = CombatPhase.Idle;
        private long threatId;
        private Vector3D threatPosition;
        private int threatSeenFrame;
        private int combatRetargetFrame;
        private Vector3D combatRoutedTarget;
        private int combatNoTargetSince = -1;
        private int combatRescrambleFrame;
        private int combatHomeTryFrame;
        private int combatBehaviourCheckFrame;
        private int combatLowPowerLogFrame = int.MinValue;
        private bool combatDamaged;
        private bool combatUnderFire;         // hit while scrambling (engage): fight it out before flying on
        private bool combatSearchLocked;

        /// <summary>Combat handover on its own detections: a Combat drone managed by an orchestrator.</summary>
        private bool IsCombatMode
        {
            get { return settings != null && settings.OperationMode == OperationMode.ManagedByOrchestrator && settings.BehaviourProfile == BehaviourProfile.IsCombatDrone; }
        }

        /// <summary>Behavior profile "Combat drone": takes the hostile designator's engage / escort jobs (needs the three AI blocks).</summary>
        private bool IsCombatProfile { get { return settings != null && settings.BehaviourProfile == BehaviourProfile.IsCombatDrone; } }

        /// <summary>
        /// Scramble / handover (and every phase of an engage / escort job): power, load and other automatic "go home"
        /// triggers leave the drone to this logic (a designator job ends itself on low power: the job runner stops it).
        /// </summary>
        private bool CombatHoldsControl
        {
            get
            {
                return designatorCombat != DesignatorCombat.None
                    || (IsCombatMode && (combatPhase == CombatPhase.Scramble || combatPhase == CombatPhase.Handover));
            }
        }

        #region AI block settings check (combat profile)
        private string combatAiNote;

        /// <summary>Combat profile: the AI Flight block's speed limit below the drone's Max speed; null: fine.</summary>
        private string CombatAiSettingsNote()
        {
            if (!IsCombatProfile) return null;
            string note = null;
            if (aiFlight != null && !aiFlight.Closed && aiFlight.SpeedLimit < MaxSpeed - 0.5f)
                note = Loc.F(StrAIFlightSpeedLimitM, aiFlight.SpeedLimit, MaxSpeed);
            return note;
        }

        #endregion

        // Capability scan: the first functional block of each type on the drone's construct
        private void SetCombatBlocks(IMyDefensiveCombatBlock def, IMyOffensiveCombatBlock off, IMyFlightMovementBlock flight)
        {
            aiDefensive = def;
            aiOffensive = off;
            aiFlight = flight;
        }

        /// <summary>
        /// " AI Defensive AI Flight..." when the mode needs blocks the drone lacks, else null. Out on a sortie a lost block
        /// is not an error (the drone could not fly home): the sortie ends instead and the next scan at home reports it.
        /// </summary>
        private string CombatMissingBlocks()
        {
            if (!IsCombatProfile) return null;
            if (combatPhase != CombatPhase.Idle || designatorCombat != DesignatorCombat.None) return null;
            if (initialized && !isParked) return null;   // checked at home, like the ore scout's detector
            if (aiDefensive != null && aiOffensive != null && aiFlight != null) return null;
            string s = "";
            if (aiDefensive == null) s += " " + Loc.T(StrAiDefensive);
            if (aiFlight == null) s += " " + Loc.T(StrAiFlight);
            if (aiOffensive == null) s += " " + Loc.T(StrAiOffensive);
            return s;
        }

        #region Behavior switch
        private static bool AiBlockUsable(IMyTerminalBlock b)
        {
            return b != null && !b.Closed && b.IsFunctional;
        }

        private static bool GetBehaviour(IMyTerminalBlock b)
        {
            if (b == null || b.Closed) return false;
            var p = b.GetProperty(AI_BEHAVIOUR_PROPERTY);
            var pb = p != null ? p.AsBool() : null;
            return pb != null && pb.GetValue(b);
        }

        /// <summary>Sets the AI block's "AI Behavior" switch and reads it back (the grid's AI system can refuse).</summary>
        private bool SetBehaviour(IMyTerminalBlock b, bool on)
        {
            if (b == null || b.Closed) return !on;
            var p = b.GetProperty(AI_BEHAVIOUR_PROPERTY);
            var pb = p != null ? p.AsBool() : null;
            if (pb == null)
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: combat: {1} has no '{2}' property", Entity.EntityId, b.CustomName, AI_BEHAVIOUR_PROPERTY);
                return !on;
            }
            if (pb.GetValue(b) != on) pb.SetValue(b, on);
            return pb.GetValue(b) == on;
        }

        // Off: Offensive first (it needs the Flight block), then Flight; the Defensive behaviour stays off in this mode
        private void CombatBehavioursOff()
        {
            SetBehaviour(aiOffensive, false);
            SetBehaviour(aiFlight, false);
            SetBehaviour(aiDefensive, false);
        }

        private void LockOffensiveSearch(bool locked)
        {
            if (combatSearchLocked == locked) return;
            if (aiOffensive != null && !aiOffensive.Closed) aiOffensive.SetSearchLocked(locked, this);
            combatSearchLocked = locked;
        }
        #endregion

        #region Threat
        private IMyEntity DefensiveTarget()
        {
            var c = AiBlockUsable(aiDefensive) ? aiDefensive.SearchEnemyComponent : null;
            var e = c != null ? c.FoundEnemy : null;
            return e != null && !e.Closed && !e.MarkedForClose ? e : null;
        }

        private IMyEntity OffensiveTarget()
        {
            var c = AiBlockUsable(aiOffensive) ? aiOffensive.SearchEnemyComponent : null;
            var e = c != null ? c.FoundEnemy : null;
            return e != null && !e.Closed && !e.MarkedForClose ? e : null;
        }

        private void SeeThreat(IMyEntity e, int now)
        {
            if (e.EntityId != threatId && Log.DebugEnabled) Log.Debug("Drone {0}: combat: threat {1} ({2}) at {3:F0} m", Entity.EntityId, e.EntityId, e.DisplayName, Vector3D.Distance(e.GetPosition(), flightState.Position));
            threatId = e.EntityId;
            threatPosition = e.GetPosition();
            threatSeenFrame = now;
        }

        private bool CombatLowPower { get { return needsBatteryRecharge || needsHydrogenRefuel; } }
        #endregion

        #region State machine
        // UpdateBeforeSimulation10, server, AI on
        private void UpdateCombat10()
        {
            if (designatorCombat != DesignatorCombat.None)
            {
                if (savedSave == null) UpdateDesignatorCombat10();
                return;
            }
            if (!IsCombatMode)
            {
                if (combatPhase != CombatPhase.Idle) EndCombat(Loc.T(StrModeChanged));
                return;
            }
            if (savedSave != null) return;   // saved state not applied yet
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (currentState == State.Error && combatPhase != CombatPhase.Handover)
            {
                if (combatPhase != CombatPhase.Idle) EndCombat(Loc.T(StrDroneInError));
                return;
            }
            switch (combatPhase)
            {
                case CombatPhase.Idle: CombatIdle(now); break;
                case CombatPhase.Scramble: CombatScramble(now); break;
                case CombatPhase.Handover: CombatHandover(now); break;
                case CombatPhase.Recover: CombatRecover(now); break;
            }
        }

        private void CombatIdle(int now)
        {
            if (now - combatBehaviourCheckFrame >= COMBAT_BEHAVIOUR_CHECK_TICKS)
            {
                combatBehaviourCheckFrame = now;
                if (GetBehaviour(aiOffensive) || GetBehaviour(aiFlight) || GetBehaviour(aiDefensive))
                {
                    if (Log.DebugEnabled) Log.Debug("Drone {0}: combat: AI behaviours switched off (idle)", Entity.EntityId);
                    CombatBehavioursOff();
                }
                if (combatSearchLocked && IsParkedNow()) LockOffensiveSearch(false);
            }
            // Between jobs only (an orchestrator job, or the drone flying / docking for one, comes first)
            if (currentJob != null || cPhase != ConstructionPhase.Idle) return;
            var e = DefensiveTarget();
            if (e == null || now < combatRescrambleFrame) return;
            if (!CanScramble(e, now)) return;
            SeeThreat(e, now);
            StartScramble(now, Loc.T(StrEnemyDetected));
        }

        private bool CanScramble(IMyEntity e, int now)
        {
            if (settings.AutoLaunchRadius <= 0) return false;   // designator jobs only
            if (!HasHomeConnector && !ParkMode && !ReturnsToDeparture)
            {
                if (now - combatLowPowerLogFrame >= COMBAT_LOW_POWER_LOG_TICKS)
                {
                    combatLowPowerLogFrame = now;
                    Report(StrCombatEnemyDetectedNoHome);
                }
                return false;
            }
            if (CombatLowPower)
            {
                if (now - combatLowPowerLogFrame >= COMBAT_LOW_POWER_LOG_TICKS)
                {
                    combatLowPowerLogFrame = now;
                    Report(StrCombatEnemyDetectedPowerHydrogen);
                }
                return false;
            }
            // From the drone (where its AI Defensive block detects), within the server limit
            var cfg = AutomataSession.GetConfig();
            double r = Math.Min(Math.Min(settings.AutoLaunchRadius, AUTO_LAUNCH_RADIUS_MAX), cfg != null ? cfg.Drone.CombatMaxDetectionRange : 2000);
            return Vector3D.DistanceSquared(flightState.Position, e.GetPosition()) <= r * r;
        }

        private void StartScramble(int now, string why)
        {
            combatPhase = CombatPhase.Scramble;
            combatDamaged = false;
            combatUnderFire = false;
            combatNoTargetSince = -1;
            combatRouteFails = 0;
            LockOffensiveSearch(false);
            Report(StrCombatLaunchingM, why, Vector3D.Distance(threatPosition, flightState.Position));
            if (Vector3D.DistanceSquared(HullCentreWorld, threatPosition) <= HandoverDistanceSquared)
            {
                CombatStartHandover(now);
                return;
            }
            CombatRoute(now);
        }

        private double HandoverDistanceSquared
        {
            get { double d = MathHelper.Clamp(settings.CombatHandoverDistance, COMBAT_HANDOVER_MIN, COMBAT_HANDOVER_MAX); return d * d; }
        }

        // Toward the threat, stopping a little inside the handover distance (the handover happens on the way)
        private void CombatRoute(int now)
        {
            combatRetargetFrame = now;
            combatRoutedTarget = threatPosition;
            Vector3D from = HullCentreWorld;
            Vector3D dir = threatPosition - from;
            double dist = dir.Normalize();
            double stopShort = Math.Sqrt(HandoverDistanceSquared) * 0.8;
            Vector3D goal = dist > stopShort ? threatPosition - dir * stopShort : from;
            // On a planet the straight line toward a target on the ground runs under the surface: the goal is lifted
            // above it (it was routinely underground: "no route (terrain)" every 5 s, forever)
            goal = AboveTerrain(goal, DroneRadius() + COMBAT_GOAL_CLEARANCE);
            var wps = new System.Collections.Generic.List<Vector3D> { goal };
            if (FlyPlayerRoute(wps)) { combatRouteFails = 0; return; }
            combatRouteFails++;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: combat: no route toward the threat ({1}/{2})", Entity.EntityId, combatRouteFails, COMBAT_ROUTE_FAILS_MAX);
        }

        /// <summary>'p' at least 'clear' m above the closest planet's surface (along the local vertical); unchanged in space.</summary>
        private Vector3D AboveTerrain(Vector3D p, double clear)
        {
            if (!flightState.InGravity) return p;
            double h = PathfindingManager.HeightAboveTerrain(p);
            if (h == double.MaxValue || h >= clear) return p;
            var planet = Sandbox.Game.Entities.MyGamePruningStructure.GetClosestPlanet(p);
            if (planet == null) return p;
            Vector3D up = p - planet.PositionComp.GetPosition();
            if (up.LengthSquared() < 1) return p;
            up.Normalize();
            return p + up * (clear - h);
        }

        private void CombatScramble(int now)
        {
            // Engage: the designated target, whether the AI blocks see it yet or not
            var e = designatorCombat == DesignatorCombat.Engage ? DesignatedTarget() : DefensiveTarget() ?? OffensiveTarget();
            if (e != null) SeeThreat(e, now);
            else if (designatorCombat == DesignatorCombat.Engage && designatorTargetId == 0) threatSeenFrame = now;   // a point: never lost
            else if (now - threatSeenFrame > COMBAT_THREAT_LOST_TICKS) { CombatStartRecover(now, Loc.T(StrThreatLost)); return; }
            if (settings.CombatRetreatOnLowPower && CombatLowPower) { CombatStartRecover(now, Loc.T(StrLowPower)); return; }
            if (combatRouteFails >= COMBAT_ROUTE_FAILS_MAX) { CombatStartRecover(now, Loc.T(StrNoRouteToTheTarget)); return; }
            if (preflightStage != 0) return;   // still undocking
            if (designatorCombat == DesignatorCombat.Engage && now >= combatRescrambleFrame)
            {
                // On the way: an enemy closing in (detected by the AI Defensive block) or fire taken is fought first,
                // then the drone flies on to the designated target (DesignatorCombatEnded)
                var interceptor = Interceptor();
                if (interceptor != null || combatUnderFire)
                {
                    engageDiverted = true;
                    if (interceptor != null) SeeThreat(interceptor, now);
                    Report(StrEngageFightingItOnThe, interceptor != null ? Loc.F(StrInterceptedBy, interceptor.DisplayName) : Loc.T(StrUnderFire));
                    CombatStartHandover(now);
                    return;
                }
            }
            if (Vector3D.DistanceSquared(HullCentreWorld, threatPosition) <= HandoverDistanceSquared)
            {
                CombatStartHandover(now);
                return;
            }
            if (now - combatRetargetFrame >= COMBAT_RETARGET_TICKS
                && (activeFlightOrder == null || Vector3D.DistanceSquared(threatPosition, combatRoutedTarget) > COMBAT_RETARGET_MOVED * COMBAT_RETARGET_MOVED))
                CombatRoute(now);
        }

        /// <summary>
        /// Engage, on the way: an enemy the AI Defensive block detects that isn't the designated target or part of it,
        /// and can come after the drone (a mobile grid or a character; stations and their turrets are the target's
        /// business), within the server's detection range.
        /// </summary>
        private IMyEntity Interceptor()
        {
            var e = DefensiveTarget();
            if (e == null || e.EntityId == designatorTargetId) return null;
            var grid = e as IMyCubeGrid;
            if (grid == null)
            {
                var block = e as IMyCubeBlock;
                grid = block != null ? block.CubeGrid : null;
            }
            if (grid != null)
            {
                if (grid.IsStatic) return null;
                var target = DesignatedTarget() as IMyCubeGrid;
                if (target != null && (grid == target || MyAPIGateway.GridGroups.HasConnection(grid, target, GridLinkTypeEnum.Mechanical))) return null;
            }
            else if (!(e is IMyCharacter)) return null;
            var cfg = AutomataSession.GetConfig();
            double r = cfg != null ? cfg.Drone.CombatMaxDetectionRange : 2000;
            return Vector3D.DistanceSquared(flightState.Position, e.GetPosition()) <= r * r ? e : null;
        }

        private void CombatStartHandover(int now)
        {
            if (!AiBlockUsable(aiFlight) || !AiBlockUsable(aiOffensive))
            {
                CombatStartRecover(now, Loc.T(StrAIFlightAIOffensiveNot));
                return;
            }
            // Hands off: no order, no overrides, gyros free (the per-tick update then stays idle)
            ClearFlightOrder();
            orientationTargetSet = false;
            ClearAllThrustOverrides();
            ReleaseGyroscopes();
            LockOffensiveSearch(false);
            SetBehaviour(aiDefensive, false);
            // The drone's Max speed holds in the fight too: an AI Flight limit above it is lowered to it
            if (aiFlight.SpeedLimit > MaxSpeed + 0.5f)
            {
                Report(StrCombatAIFlightSpeedLimit, aiFlight.SpeedLimit, MaxSpeed);
                aiFlight.SpeedLimit = MaxSpeed;
            }
            // Both: the Offensive behaviour errors without an active Flight block
            bool flightOn = SetBehaviour(aiFlight, true);
            bool offOn = flightOn && SetBehaviour(aiOffensive, true);
            if (!flightOn || !offOn)
            {
                CombatStartRecover(now, !flightOn ? Loc.T(StrAIFlightRefusedToActivate) : Loc.T(StrAIOffensiveRefusedToActivate));
                return;
            }
            aiOffensive.DoForceSearchEnemy();
            combatPhase = CombatPhase.Handover;
            combatNoTargetSince = -1;
            combatDamaged = false;
            combatBehaviourCheckFrame = now;
            SetState(State.Combat);
            Report(StrCombatHandedOverToThe, Vector3D.Distance(HullCentreWorld, threatPosition));
        }

        private void CombatHandover(int now)
        {
            if (!AiBlockUsable(aiFlight) || !AiBlockUsable(aiOffensive)) { CombatStartRecover(now, Loc.T(StrAIBlockLost)); return; }
            var e = OffensiveTarget() ?? DefensiveTarget();
            if (e != null)
            {
                SeeThreat(e, now);
                if (combatNoTargetSince >= 0) if (Log.DebugEnabled) Log.Debug("Drone {0}: combat: target again", Entity.EntityId);
                combatNoTargetSince = -1;
            }
            else if (combatNoTargetSince < 0)
            {
                combatNoTargetSince = now;
                if (Log.DebugEnabled) Log.Debug("Drone {0}: combat: no target, {1} s to return", Entity.EntityId, settings.CombatNoTargetSeconds);
            }
            else if (now - combatNoTargetSince >= settings.CombatNoTargetSeconds * 60) { CombatStartRecover(now, Loc.T(StrNoTargets)); return; }

            if (settings.CombatRetreatOnFlee && aiDefensive != null && !aiDefensive.Closed)
            {
                // The Defensive behaviour is off in this mode, so its own flee rarely fires: its "when taking damage"
                // trigger is evaluated here from the drone's damage events
                bool flee = aiDefensive.IsFleeing
                    || (combatDamaged && aiDefensive.FleeTrigger == VRage.Game.ModAPI.Ingame.FleeTrigger.WhenTakingDamage);
                if (flee) { CombatStartRecover(now, Loc.T(StrFleeing)); return; }
            }
            if (settings.CombatRetreatOnLowPower && CombatLowPower) { CombatStartRecover(now, Loc.T(StrLowPower)); return; }

            if (now - combatBehaviourCheckFrame >= COMBAT_BEHAVIOUR_CHECK_TICKS)
            {
                combatBehaviourCheckFrame = now;
                // Switched off by the player, a timer or the AI system: the AI is no longer flying, take back
                if (!GetBehaviour(aiFlight) || !GetBehaviour(aiOffensive)) { CombatStartRecover(now, Loc.T(StrAIBehaviourSwitchedOff)); return; }
            }
        }

        private void CombatStartRecover(int now, string why)
        {
            if (designatorCombat != DesignatorCombat.None) { DesignatorCombatEnded(now, why); return; }
            CombatBehavioursOff();
            LockOffensiveSearch(true);   // no target hunting that drags the drone back while ours flies
            combatPhase = CombatPhase.Recover;
            combatRescrambleFrame = now + COMBAT_RESCRAMBLE_TICKS;
            combatHomeTryFrame = now;
            Report(StrCombatReturningHome, why);
            ClearFlightOrder();
            if (!IsAtRest()) GoRest();
        }

        private void CombatRecover(int now)
        {
            if (IsAtRest())
            {
                combatPhase = CombatPhase.Idle;
                LockOffensiveSearch(false);
                capabilitiesDirty = true;   // a block lost out there is reported now
                Report(StrCombatBackHome);
                return;
            }
            // A new detection with enough power: back out, once a minute at most
            var e = DefensiveTarget();
            if (e != null && now >= combatRescrambleFrame && !CombatLowPower)
            {
                combatRescrambleFrame = now + COMBAT_RESCRAMBLE_TICKS;
                SeeThreat(e, now);
                StartScramble(now, Loc.T(StrEnemyDetectedAgain));
                return;
            }
            if (!IsGoingToRest && activeFlightOrder == null && preflightStage == 0 && now - combatHomeTryFrame >= COMBAT_HOME_RETRY_TICKS)
            {
                combatHomeTryFrame = now;
                GoRest();
            }
        }

        /// <summary>AI off, mode change, close: behaviours off, search unlocked, phase idle (the flight is stopped by the caller).</summary>
        private void EndCombat(string why)
        {
            if (combatPhase != CombatPhase.Idle || combatSearchLocked || designatorCombat != DesignatorCombat.None && Log.DebugEnabled) Log.Debug("Drone {0}: combat: ended ({1}) in {2}{3}", Entity.EntityId, why, combatPhase, designatorCombat != DesignatorCombat.None ? " (" + designatorCombat + " job)" : "");
            if (combatPhase == CombatPhase.Handover || IsCombatMode || designatorCombat != DesignatorCombat.None) CombatBehavioursOff();
            LockOffensiveSearch(false);
            combatPhase = CombatPhase.Idle;
            combatNoTargetSince = -1;
            designatorCombat = DesignatorCombat.None;
            designatorDone = false;
            designatorAbort = null;
            designatorTargetId = 0;
        }
        #endregion

        #region Designator jobs: engage / escort (hostile designator, behaviour profile "Combat drone")
        // An Engage / Escort task of an orchestrator job runs the same scramble / handover machinery as the operation mode,
        // in "Managed by orchestrator". Differences: the threat of an engage is the designated entity (no AI Defensive
        // detection needed); the end of the fight finishes the task (the job's ReturnHome takes the drone home, or to
        // park) instead of the mode's own Recover; an escort goes back to following after a fight.
        private enum DesignatorCombat : byte { None, Engage, Escort }

        #region Timing
        private const int DESIGNATOR_TARGET_LOST_TICKS = 600;   // escorted target not found this long: the escort ends
        private const int ESCORT_ROUTE_TICKS = 300;             // re-route toward a moving escort every 5 s...
        private const int ESCORT_RESCRAMBLE_TICKS = 600;        // after a fight: following for at least 10 s before the next one
        private const int ESCORT_UP_TICKS = 60;                 // gravity at the escort re-read once a second
        private const int ESCORT_CLEAR_CHECK_TICKS = 60;        // station keeping: the straight line re-checked once a second
        #endregion
        private const int ENGAGE_ATTEMPTS_MAX = 3;              // the AI found nothing near, the target is still out there: fly at it again
        private const double ESCORT_CLEARANCE = 5;              // m at least between the escorted entity's and the drone's bounding spheres
        private const double ESCORT_NEAR = 250;                 // m: closer to the station than this, station keeping; farther, a route
        private const double ESCORT_ROUTE_MOVED = 50;           // ...when its station moved this far
        private int escortClearFrame;

        private DesignatorCombat designatorCombat;
        private long designatorTargetId;
        private Vector3D designatorTargetPosition;
        private int designatorSeenFrame;
        private bool designatorDone;          // the fight / escort is over: the task runner finishes the task
        private string designatorAbort;       // why it ended early (null: done as ordered)
        private int engageAttempts;
        private int escortRouteFrame;
        private Vector3D escortRoutedStation;
        private Vector3D escortUp;
        private int escortUpFrame;
        #region Timing
        private const int ENGAGE_CHECK_TICKS = 100;            // the engaged target's state checked every ~1.7 s
        #endregion
        private int engageCheckFrame;
        private bool engageDiverted;                           // fighting an interceptor on the way: the target comes next
        private bool engageTargetArmed;                        // it had working weapons at the order: losing them all disables it
        private static readonly List<IMyCubeGrid> engageGrids = new List<IMyCubeGrid>();

        /// <summary>The engage / escort target: the task's entity while it exists (a dead character counts as gone).</summary>
        private IMyEntity DesignatedTarget()
        {
            if (designatorTargetId == 0) return null;
            IMyEntity e;
            if (!MyAPIGateway.Entities.TryGetEntityById(designatorTargetId, out e) || e == null || e.Closed || e.MarkedForClose) return null;
            var ch = e as IMyCharacter;
            return ch != null && ch.IsDead ? null : e;
        }

        // Task runner (Engage / Escort task): starts the fight / the escort, then waits for it to end
        private TaskStatus RunDesignatorCombat(ref Orchestrator.Task task, bool starting)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (starting)
            {
                if (!AiBlockUsable(aiDefensive) || !AiBlockUsable(aiFlight) || !AiBlockUsable(aiOffensive))
                    return Fail(Orchestrator.TaskFailure.MissingEquipment, TaskStatus.ToReturnHome);
                EndCombat(Loc.T(StrNewOrder));
                CombatBehavioursOff();   // after a load the AI blocks keep their own switch
                designatorCombat = task.Type == Orchestrator.TaskType.Engage ? DesignatorCombat.Engage : DesignatorCombat.Escort;
                designatorTargetId = task.TargetEntityId;
                designatorTargetPosition = task.Position.ToVector3D();
                designatorSeenFrame = now;
                engageAttempts = 0;
                engageDiverted = false;
                combatRescrambleFrame = now;
                escortRouteFrame = int.MinValue / 2;
                escortUpFrame = int.MinValue / 2;
                var t = DesignatedTarget();
                if (designatorCombat == DesignatorCombat.Engage)
                {
                    // Position: where the designator hit (the target's own position is tracked from its entity)
                    if (t != null) SeeThreat(t, now);
                    else { threatId = designatorTargetId; threatPosition = designatorTargetPosition; threatSeenFrame = now; }
                    var grid = t as IMyCubeGrid;
                    engageTargetArmed = grid != null && GroupHas(grid, true);
                    engageCheckFrame = now;
                    Broadcast(string.Format(CultureInfo.InvariantCulture, Loc.T(StrEngagingTargetAtGPS),
                                            GpsName(t != null ? t.DisplayName : null), designatorTargetPosition.X, designatorTargetPosition.Y,
                                            designatorTargetPosition.Z));
                    StartScramble(now, designatorTargetId == 0 ? Loc.T(StrEngageOrderAtPoint) : t != null ? Loc.F(StrEngageOrderOn, t.DisplayName) : Loc.T(StrEngageOrder));
                }
                else
                {
                    Report(StrEscort, t != null ? t.DisplayName : Loc.T(StrTargetNotFoundYet));
                    if (t != null) Broadcast(Loc.F(StrEscortingChat, t.DisplayName));
                    LockOffensiveSearch(true);   // its behaviour is off; no target picking while ours flies
                    EscortFollow(now);
                }
                return TaskStatus.Running;
            }
            if (designatorCombat == DesignatorCombat.None)
            {
                // Ended from outside (error, AI off...): home
                jobAbortReason = Loc.T(StrInterrupted);
                return TaskStatus.ToReturnHome;
            }
            if (!designatorDone) return TaskStatus.Running;
            string abort = designatorAbort;
            EndCombat(abort ?? Loc.T(StrDone));
            if (abort != null) jobAbortReason = abort;
            return TaskStatus.Done;   // the job's ReturnHome follows
        }

        // UpdateBeforeSimulation10, server, AI on: the phases of an engage / escort job
        private void UpdateDesignatorCombat10()
        {
            if (designatorDone) return;   // the task runner picks it up
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (currentState == State.Error && combatPhase != CombatPhase.Handover) { DesignatorCombatEnded(now, Loc.T(StrDroneInError)); return; }
            if (designatorCombat == DesignatorCombat.Engage && designatorTargetId != 0 && now - engageCheckFrame >= ENGAGE_CHECK_TICKS)
            {
                engageCheckFrame = now;
                var t = DesignatedTarget();
                if (t == null || TargetDisabled(t)) { EngageTargetDown(t == null ? Loc.T(StrGone) : Loc.T(StrDisabled)); return; }
            }
            switch (combatPhase)
            {
                case CombatPhase.Idle:
                    if (designatorCombat == DesignatorCombat.Escort) EscortFollow(now);
                    else DesignatorCombatEnded(now, Loc.T(StrNoTargets));   // engage between fights (not expected)
                    break;
                case CombatPhase.Scramble: CombatScramble(now); break;
                case CombatPhase.Handover: CombatHandover(now); break;
                default: DesignatorCombatEnded(now, Loc.T(StrNoTargets)); break;   // Recover is the operation mode's
            }
        }

        // A fight of a designator job ended (CombatStartRecover's reasons): AI off, then follow again (escort), fly at the
        // target again (engage, it is still out there), or done
        private void DesignatorCombatEnded(int now, string why)
        {
            CombatBehavioursOff();
            LockOffensiveSearch(true);   // no target hunting that drags the drone back while ours flies
            combatPhase = CombatPhase.Idle;
            combatNoTargetSince = -1;
            bool fightOver = why == Loc.T(StrNoTargets) || why == Loc.T(StrThreatLost);
            var target = DesignatedTarget();
            if (designatorCombat == DesignatorCombat.Engage && engageDiverted)
            {
                // The interceptor is dealt with (or gone): on to the designated target, still out there
                engageDiverted = false;
                bool point = designatorTargetId == 0;
                if (fightOver && (point || (target != null && !TargetDisabled(target))))
                {
                    combatRescrambleFrame = now + ESCORT_RESCRAMBLE_TICKS;   // a few seconds of flight before the next diversion
                    if (point) { threatId = 0; threatPosition = designatorTargetPosition; threatSeenFrame = now; }
                    else SeeThreat(target, now);
                    StartScramble(now, Loc.T(StrInterceptorDealtWithBackTo));
                    return;
                }
            }
            if (designatorCombat == DesignatorCombat.Escort && fightOver && target != null)
            {
                combatRescrambleFrame = now + ESCORT_RESCRAMBLE_TICKS;
                escortRouteFrame = int.MinValue / 2;
                ClearFlightOrder();
                Report(StrEscortFollowingAgain, why, target.DisplayName);
                return;
            }
            if (designatorCombat == DesignatorCombat.Engage && designatorTargetId != 0 && why == Loc.T(StrNoTargets) && target != null && engageAttempts < ENGAGE_ATTEMPTS_MAX
                && Vector3D.DistanceSquared(HullCentreWorld, target.WorldAABB.Center) > HandoverDistanceSquared)
            {
                engageAttempts++;
                SeeThreat(target, now);
                StartScramble(now, Loc.T(StrTargetStillOutThere));
                return;
            }
            if (designatorCombat == DesignatorCombat.Engage && designatorTargetId != 0 && fightOver && (target == null || TargetDisabled(target)))
            {
                EngageTargetDown(target == null ? Loc.T(StrGone) : Loc.T(StrDisabled));
                return;
            }
            designatorDone = true;
            designatorAbort = fightOver ? (designatorCombat == DesignatorCombat.Escort ? Loc.T(StrEscortedTargetGone) : null) : why;
            ClearFlightOrder();
            Report(StrCombatDone, designatorCombat == DesignatorCombat.Engage ? Loc.T(StrEngage) : Loc.T(StrEscortLabel), designatorAbort ?? why);
        }

        // Engage: the designated target is destroyed or disabled: AI off, the job is done (completed), the drone says so
        private void EngageTargetDown(string how)
        {
            CombatBehavioursOff();
            LockOffensiveSearch(true);
            combatPhase = CombatPhase.Idle;
            combatNoTargetSince = -1;
            designatorDone = true;
            designatorAbort = null;
            ClearFlightOrder();
            Report(StrEngageTargetDone, how);
            Broadcast(Loc.T(StrTargetDestroyed));
        }

        /// <summary>
        /// The engaged target can no longer fight: a dead character; a grid (with what is mechanically attached) with no
        /// working power source, or that had weapons at the order and has no functional one left.
        /// </summary>
        private bool TargetDisabled(IMyEntity t)
        {
            var ch = t as IMyCharacter;
            if (ch != null) return ch.IsDead;
            var grid = t as IMyCubeGrid;
            if (grid == null) return false;
            if (!GroupHas(grid, false)) return true;              // no power
            return engageTargetArmed && !GroupHas(grid, true);    // disarmed
        }

        // weapons: a functional weapon / turret on the grid's construct; else a working power producer
        private static bool GroupHas(IMyCubeGrid grid, bool weapons)
        {
            engageGrids.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, engageGrids);
            try
            {
                for (int g = 0; g < engageGrids.Count; g++)
                {
                    if (weapons)
                    {
                        foreach (var w in engageGrids[g].GetFatBlocks<IMyUserControllableGun>())
                            if (w.IsFunctional) return true;
                    }
                    else
                    {
                        foreach (var p in engageGrids[g].GetFatBlocks<IMyPowerProducer>())
                            if (p.IsWorking) return true;
                    }
                }
                return false;
            }
            finally { engageGrids.Clear(); }
        }

        // "Use broadcast controller": a chat line through a Broadcast Controller on the drone
        private void Broadcast(string text)
        {
            if (settings == null || !settings.UseBroadcastController) return;
            string via;
            bool sent = BroadcastControllerSender.Send(block, text, out via);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: broadcast '{1}' {2}: {3}", Entity.EntityId, text, sent ? "sent via" : "not sent", via);
        }

        // A GPS name for chat (no ':' allowed in it)
        private static string GpsName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Loc.T(StrTarget);
            return name.Replace(':', ' ').Trim();
        }

        // Escort, between fights: an enemy the AI Defensive block detects starts a fight; otherwise follow the target -
        // a route while far, station keeping (FlightOrder.FollowEntityId) once close
        private void EscortFollow(int now)
        {
            var t = DesignatedTarget();
            if (t == null)
            {
                if (now - designatorSeenFrame > DESIGNATOR_TARGET_LOST_TICKS)
                {
                    designatorDone = true;
                    designatorAbort = Loc.T(StrEscortedTargetGone);
                    ClearFlightOrder();
                    Report(StrEscortTargetGoneDone);
                }
                return;
            }
            designatorSeenFrame = now;
            designatorTargetPosition = t.WorldAABB.Center;
            var enemy = DefensiveTarget();
            if (enemy != null && now >= combatRescrambleFrame && !CombatLowPower)
            {
                var cfg = AutomataSession.GetConfig();
                double maxRange = cfg != null ? cfg.Drone.CombatMaxDetectionRange : 2000;
                if (Vector3D.DistanceSquared(flightState.Position, enemy.GetPosition()) <= maxRange * maxRange)
                {
                    SeeThreat(enemy, now);
                    StartScramble(now, Loc.T(StrEnemyNearTheEscort));
                    return;
                }
            }
            if (preflightStage != 0) return;   // undocking
            Vector3D offset = EscortOffset(t, now);
            Vector3D station = designatorTargetPosition + offset;
            double d2 = Vector3D.DistanceSquared(HullCentreWorld, station);
            bool following = activeFlightOrder != null && activeFlightOrder.FollowEntityId == t.EntityId;
            if (following)
            {
                activeFlightOrder.FollowOffset = offset;
                if (d2 > 4 * ESCORT_NEAR * ESCORT_NEAR) { EscortRoute(now, station); return; }   // left behind: route after it
                // Station keeping flies straight (no obstacle checks of its own): something in the line, a route instead
                if (now - escortClearFrame >= ESCORT_CLEAR_CHECK_TICKS)
                {
                    escortClearFrame = now;
                    if (!EscortLineClear(station)) EscortRoute(now, station);
                }
                return;
            }
            if (d2 < ESCORT_NEAR * ESCORT_NEAR && !IsParkedNow() && EscortLineClear(station))
            {
                escortClearFrame = now;
                StartFollow(t, offset);
                return;
            }
            if (now - escortRouteFrame >= ESCORT_ROUTE_TICKS
                && (activeFlightOrder == null || Vector3D.DistanceSquared(station, escortRoutedStation) > ESCORT_ROUTE_MOVED * ESCORT_ROUTE_MOVED))
                EscortRoute(now, station);
        }

        // The station above the escorted entity (gravity up there, else the entity's own up): the follow distance, and
        // clear of both hulls whatever the setting
        private Vector3D EscortOffset(IMyEntity t, int now)
        {
            if (now - escortUpFrame >= ESCORT_UP_TICKS || escortUp.LengthSquared() < 0.5)
            {
                escortUpFrame = now;
                float interference;
                Vector3D g = MyAPIGateway.Physics.CalculateNaturalGravityAt(designatorTargetPosition, out interference);
                escortUp = g.LengthSquared() > 1e-4 ? -Vector3D.Normalize(g) : t.WorldMatrix.Up;
            }
            double clear = t.WorldVolume.Radius + DroneRadius() + ESCORT_CLEARANCE;
            Vector3D offset = escortUp * Math.Max(settings.EscortFollowDistance, clear);
            // Never in the ground (an escorted ship low over a hill)
            return AboveTerrain(designatorTargetPosition + offset, DroneRadius() + ESCORT_CLEARANCE) - designatorTargetPosition;
        }

        // The straight line to the escort station: no block in the way (scene), no terrain closer than the hull, the
        // station itself free
        private bool EscortLineClear(Vector3D station)
        {
            var pm = PathfindingManager.Instance;
            if (pm == null) return true;
            if (droneGrids.Count == 0) RefreshDroneGrids();
            PathQuery q;
            BuildPathQuery(out q);
            Vector3D from = HullCentreWorld, hit;
            IMyEntity e;
            if (!pm.HullFits(ref q, station)) return false;
            if (pm.SegmentBlocked(ref q, ref q.Controller, from, station, 0.5, out hit, out e, false)) return false;
            return !flightState.InGravity || !PathfindingManager.TerrainTooClose(from, station, DroneRadius() + 2, out hit);
        }

        private void EscortRoute(int now, Vector3D station)
        {
            escortRouteFrame = now;
            escortRoutedStation = station;
            var wps = new System.Collections.Generic.List<Vector3D> { station };
            if (!FlyPlayerRoute(wps))
                if (Log.DebugEnabled) Log.Debug("Drone {0}: escort: no route to the escort station, retrying", Entity.EntityId);
        }

        // Station keeping on the escorted entity: a direct order whose target follows it every tick (RefreshFollowOrder)
        private void StartFollow(IMyEntity t, Vector3D offset)
        {
            var o = CreateGoTo(t.WorldAABB.Center + offset, null);
            o.FollowEntityId = t.EntityId;
            o.FollowOffset = offset;
            o.Orientation = FlightOrientationMode.LookAt;
            o.LookAtPoint = Vector3DData.FromVector3D(t.WorldAABB.Center);
            followEntity = t;
            RefreshFollowOrder(o);
            if (Log.DebugEnabled) Log.Debug("Drone {0}: escort: station keeping on {1} ({2:F0} m above)", Entity.EntityId, t.DisplayName, offset.Length());
        }

        /// <summary>Every tick (RefreshAnchoredOrder): an escort order's target and frame velocity from the followed entity.</summary>
        private bool RefreshFollowOrder(FlightOrder o)
        {
            if (followEntity == null || followEntity.EntityId != o.FollowEntityId || followEntity.Closed || followEntity.MarkedForClose)
            {
                IMyEntity e;
                followEntity = MyAPIGateway.Entities.TryGetEntityById(o.FollowEntityId, out e) ? e : null;
                if (followEntity == null || followEntity.Closed || followEntity.MarkedForClose) { followEntity = null; return false; }
            }
            Vector3D c = followEntity.WorldAABB.Center;
            var station = Vector3DData.FromVector3D(c + o.FollowOffset);
            o.Target = station;
            o.ApproachFrom = station;   // zero-length line: fly to the point and hold it
            o.TransitStart = station;
            o.LookAtPoint = Vector3DData.FromVector3D(c);
            var physics = followEntity.Physics;
            flightState.FrameVelocity = physics != null ? (Vector3D)physics.LinearVelocity : Vector3D.Zero;
            flightState.RelativeVelocity = flightState.LinearVelocity - flightState.FrameVelocity;
            return true;
        }
        #endregion

        #region Save / load
        private DroneCombatState CaptureCombatState()
        {
            if (designatorCombat != DesignatorCombat.None) return null;   // engage / escort: the job's task starts again after a load
            if (combatPhase == CombatPhase.Idle && !combatSearchLocked) return null;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            return new DroneCombatState
            {
                Phase = combatPhase,
                ThreatId = threatId,
                ThreatPosition = Vector3DData.FromVector3D(threatPosition),
                ThreatSeenAgeTicks = now - threatSeenFrame,
                NoTargetTicks = combatNoTargetSince >= 0 ? now - combatNoTargetSince : -1,
                RescrambleTicksLeft = Math.Max(0, combatRescrambleFrame - now),
                Damaged = combatDamaged,
                SearchLocked = combatSearchLocked,
            };
        }

        // After load: the AI blocks keep their own behaviour switch through the save; routes are planned afresh
        private void ResumeCombat(DroneCombatState s)
        {
            if (!IsCombatMode) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            threatId = s.ThreatId;
            threatPosition = s.ThreatPosition.ToVector3D();
            threatSeenFrame = now - Math.Max(0, s.ThreatSeenAgeTicks);
            combatNoTargetSince = s.NoTargetTicks >= 0 ? now - s.NoTargetTicks : -1;
            combatRescrambleFrame = now + s.RescrambleTicksLeft;
            combatDamaged = s.Damaged;
            combatSearchLocked = false;
            if (s.SearchLocked) LockOffensiveSearch(true);   // the lock itself is not saved by the game
            combatPhase = s.Phase;
            combatHomeTryFrame = now - COMBAT_HOME_RETRY_TICKS;
            combatRetargetFrame = now - COMBAT_RETARGET_TICKS;
            combatBehaviourCheckFrame = now;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: combat: resumed in {1} (threat {2})", Entity.EntityId, combatPhase, threatId);
            if (combatPhase == CombatPhase.Handover && (!GetBehaviour(aiFlight) || !GetBehaviour(aiOffensive)))
                CombatStartRecover(now, Loc.T(StrAINotActiveAfterLoad));
            else if (combatPhase == CombatPhase.Handover)
                SetState(State.Combat);
        }
        #endregion
    }
}
