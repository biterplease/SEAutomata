using System;

using VRageMath;

using Automata.Util.Logging;
using Automata.Pathfinding;

namespace Automata.Drone
{
    /// <summary>
    /// Economy flight, space (TODO/06). A long route leg (ECO_MIN_LEG) out of natural gravity is flown as: point the
    /// strongest thruster group along the leg, burn to a cruise speed, coast with every thruster at zero (dampeners off),
    /// flip so that group faces the travel direction, burn to brake. The normal controller takes the last
    /// ECO_HANDOVER metres (and anything that doesn't fit: a replan, a leg it can't start cleanly, a flip that comes
    /// too late). Gyros (power) do the pointing; thrusters (fuel) fire only in the burns and in short drift
    /// corrections while coasting.
    ///  - Only watched route legs (FlyRoute / Docking routes), not work, dock or attitude-held legs; not anchored to a
    ///    moving grid; not while the drone moves sideways to the leg (a replan mid-flight: normal control for that leg).
    ///  - Runtime only (a load starts the leg again with normal control until the next leg).
    ///
    /// Economy flight, natural gravity (TODO/07, quad-copter). A long route leg is flown with the lift group alone (the
    /// group that pushes against gravity: Up or Down): the drone tilts it with the gyros towards the direction of
    /// travel, so every other thruster stays at zero. Per tick: velocity error -> wanted acceleration -> thrust vector
    /// T = m (a - g) -> attitude (the group along T, heading kept) and throttle (T along the group). The tilt is bounded
    /// (the P-Gravity align limits, and what the group can lift), and so is the speed: the brake is planned with the
    /// horizontal acceleration that tilt gives. The last ECO_G_HANDOVER metres are flown normally.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private enum EcoPhase : byte { None, Point, Burn, Coast, Flip, Brake, Off, Quad, Trim }

        private const double ECO_G_MIN_LEG = 500;        // m: shorter legs in gravity are flown normally
        private const double ECO_G_HANDOVER = 100;       // m before the leg's end: normal control again
        private const double ECO_G_TILT_MAX = 0.61;      // rad (35°): most tilt, whatever the settings allow
        private const double ECO_G_TILT_MIN = 0.17;      // rad (10°): less than this and the group can't move the drone sideways
        private const double ECO_G_LIFT_MARGIN = 1.3;    // the lift group must carry this many times the weight
        private const double ECO_G_HOVER_SHARE = 1.1;    // weight share kept at the maximum tilt (sets the tilt limit)
        private const double ECO_G_KP = 0.6;             // 1/s: wanted acceleration per m/s of velocity error (slow: it has to tilt first)
        private const double ECO_G_BRAKE_SHARE = 0.5;    // share of the tilt's horizontal acceleration the brake is planned with
        private const double ECO_G_CARROT = 50;          // m: the point on the line the drone heads for is this far ahead of its closest point (more when far off)
        private const double ECO_G_LAG = 2.0;           // s of speed along the leg kept in front of the brake
        private const double ECO_G_LOST = 0.9;           // rad: the group this far from the thrust vector...
        private const int ECO_G_LOST_TICKS = 120;        // ...this long: normal control

        private const double ECO_MIN_LEG = 1000;         // m: shorter legs are flown normally
        private const double ECO_HANDOVER = 150;         // m before the leg's end: normal control again
        private const double ECO_HAND_SPEED = 15;        // m/s: the brake burn ends at this (or the leg's exit speed)
        private const double ECO_BRAKE_FRAC = 0.8;       // share of the group's accel the brake is planned with
        private const double ECO_ALIGN = 0.09;           // rad (~5°): pointed
        private const double ECO_START_LATERAL = 5;      // m/s sideways to the leg: not started (normal control for this leg)
        private const double ECO_CORRIDOR_MIN = 50;      // m off the line (or predicted to be at the end) before the course is reassessed...
        private const double ECO_CORRIDOR_SHARE = 0.01;  // ...or this share of what is left of the leg, if more
        private const int ECO_TRIM_MIN_TICKS = 600;      // between two course corrections
        private const int ECO_TRIM_MAX_TICKS = 30 * 60;  // a correction that never finishes: normal control
        private const double ECO_TRIM_DONE = 0.3;        // m/s of velocity error left: the correction is over
        private const double ECO_TRIM_MIN_LEFT = 600;    // m: closer than this to the end, no more corrections (the brake and the handover follow)
        private const double ECO_TILT = 0.176;           // tan 10°: most the burn / brake push is tilted off the line to cancel the drift
        private const double ECO_TILT_GAIN = 0.05;       // tilt per m/s of sideways error
        private const double ECO_THRUST_ALIGN = 0.35;    // rad (~20°): no push while the group points further off than this
        private const double ECO_FLIP_MARGIN = 30;       // m: on top of the brake distance and the coast while flipping
        private const int ECO_POINT_MAX_TICKS = 30 * 60; // a turn that never ends (gyros): normal control
        private const double ECO_RESUME_SPEED = 20;      // m/s along: a new order (a replan) at this speed carries on in economy flight, however short
        private const double ECO_PREFLIP_SECONDS = 3;    // s before the flip: the rest of the leg is checked (replanned) while the camera still looks ahead
        private const double ECO_VIEW_SHARE = 0.9;       // share of what the drone sees ahead that the brake (and the flip) may use
        private const double ECO_GROUP_TIE = 0.95;       // a group this close to the strongest counts as strongest (the camera's one wins)
        private const int ECO_STALL_TICKS = 60;          // full burn and the speed hasn't grown for this long: top speed (the world's limit)
        private const double ECO_STALL_GAIN = 0.3;       // m/s: growth that counts
        private const double ECO_BURST_ON = 2;           // m: slack (distance to the handover point less the stopping distance) below which the brake fires...
        private const double ECO_BURST_OFF = 20;         // ...and above which it stops: it arrives within this of the handover point
        private const double ECO_BURST_ACCEL = 0.95;     // share of the group's full acceleration the stopping distance is figured with

        private FlightOrder ecoOrder;
        private EcoPhase ecoPhase;
        private Base6Directions.Direction ecoGroup;
        private double ecoAccel, ecoCruise;
        private int ecoPhaseStart;
        private double ecoBestV;                         // burn: the highest speed along so far...
        private int ecoBestFrame;                        // ...and when
        private bool ecoBursting;                        // brake: the group is firing
        private Base6Directions.Direction ecoLook;       // the axis the drone sees along (a working camera's facing)
        private bool ecoPreflipAsked;                    // the rest of the leg was sent for a check before the flip
        private Vector3D ecoAim;                         // where the group points this tick (the line, tilted a little to cancel the drift)
        private FlightOrder ecoSkipOrder;                // the order whose "not started" reason was logged
        private bool ecoActive;                          // this tick: economy flight drives the drone
        private bool ecoHasCourse;                       // the leg's line was redrawn from ecoFrom (a course correction)
        private Vector3D ecoFrom;
        private Vector3D ecoTrimV;                       // the velocity a course correction burns to
        private int ecoLastTrim;
        private bool ecoQuad;                            // the flight started in gravity
        private double ecoTilt;                          // rad: the tilt limit of this flight
        private Vector3D ecoThrust;                      // N: the thrust vector wanted this tick (gravity)
        private int ecoLostTicks;

        // Economy flight owns the thrusters from the first burn to the handover: no replan may cut in
        private bool EconomyFlying()
        {
            return ecoActive && ecoPhase != EcoPhase.None && ecoPhase != EcoPhase.Off;
        }

        private bool EconomyEligible(FlightOrder o)
        {
            return settings != null && settings.EconomyFlight && o.Watched
                && (routeOwner == RouteOwner.FlyRoute || routeOwner == RouteOwner.Docking)
                && !(o.HoldAttitudeInTransit && o.Phase == FlightPhase.Transit)   // route legs set it too: only the transit holds the attitude
                && o.Phase != FlightPhase.Align && o.Phase != FlightPhase.MatchSpeed
                && (flightState.InGravity || flightState.FrameVelocity.LengthSquared() < 0.25)   // space: starts from rest
                && !boring && !siteYielding && physicalMass > 0
                && laneSpeedCap >= MaxSpeed;   // a drone ahead in its flight lane: normal control keeps the safe gap
        }

        /// <summary>
        /// Before the attitude: true when economy flight drives this tick ('fwd' / 'up' replaced by the pointing
        /// attitude); false: normal control.
        /// </summary>
        private bool EconomyAttitude(FlightOrder o, ref Vector3D fwd, ref Vector3D up)
        {
            if (!EconomyEligible(o))
            {
                if (ReferenceEquals(ecoOrder, o) && ecoPhase != EcoPhase.None && ecoPhase != EcoPhase.Off) EcoEnd(o, "no longer applies");
                else if (settings != null && settings.EconomyFlight && !ReferenceEquals(ecoSkipOrder, o)) LogEcoSkip(o);
                return false;
            }
            if (!ReferenceEquals(ecoOrder, o)) { ecoOrder = o; ecoPhase = EcoPhase.None; ecoHasCourse = false; ecoLastTrim = int.MinValue / 2; ecoPreflipAsked = false; }
            if (ecoPhase == EcoPhase.Off) return false;

            Vector3D d, crossTrack, vLat;
            double len, remaining, vAlong;
            if (!EcoLeg(o, out d, out len, out remaining, out crossTrack, out vAlong, out vLat)) return false;
            int now = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter;
            double vHand = Math.Min(MaxSpeed, Math.Max(ExitSpeedFor(o), ECO_HAND_SPEED));

            // A flight that began in gravity ends where the gravity does (and the other way round)
            if (ecoPhase != EcoPhase.None && ecoPhase != EcoPhase.Off && ecoQuad != flightState.InGravity)
            {
                EcoEnd(o, "gravity changed");
                return false;
            }
            if (ecoPhase == EcoPhase.None && flightState.InGravity)
            {
                if (!QuadStart(o, len, remaining)) { ecoPhase = EcoPhase.Off; return false; }
                ecoQuad = true;
                ecoPhase = EcoPhase.Quad;
                ecoPhaseStart = now;
                ecoLostTicks = 0;
            }
            else if (ecoPhase == EcoPhase.None)
            {
                ecoQuad = false;
                // Already fast (a replan mid-flight gave a new order): economy flight carries on, the leg's length aside
                bool fast = vAlong > ECO_RESUME_SPEED;
                if (((len < ECO_MIN_LEG || remaining < ECO_MIN_LEG * 0.5) && !fast) || remaining < ECO_HANDOVER * 1.5)
                {
                    if (len >= ECO_MIN_LEG)
                        Log.Debug("Drone {0}: economy flight not started: leg {1:F0} m, {2:F0} m left", Entity.EntityId, len, remaining);
                    ecoPhase = EcoPhase.Off;
                    return false;
                }
                // Off the line at the start: the line is drawn again from here to the leg's end
                if (crossTrack.Length() > Corridor(remaining))
                {
                    ecoFrom = ReferencePoint(o);
                    ecoHasCourse = true;
                    if (!EcoLeg(o, out d, out len, out remaining, out crossTrack, out vAlong, out vLat) || remaining < ECO_MIN_LEG * 0.5) { ecoPhase = EcoPhase.Off; return false; }
                }
                // The strongest group (learnt gains included); on a tie the one that pushes the way the camera looks, so
                // the drone sees where it goes while it burns
                ecoLook = LookAxis();
                double best = 0;
                for (int i = 0; i < 6; i++)
                {
                    double f = EffectiveMax((Base6Directions.Direction)i);
                    if (f > best) { best = f; ecoGroup = (Base6Directions.Direction)i; }
                }
                if (best <= 0) { Log.Debug("Drone {0}: economy flight not started: no thrust in any direction", Entity.EntityId); ecoPhase = EcoPhase.Off; return false; }
                if (EffectiveMax(ecoLook) >= best * ECO_GROUP_TIE) { ecoGroup = ecoLook; best = EffectiveMax(ecoLook); }
                ecoAccel = best * EcoThrottleCap(best) / physicalMass;   // the Max thrust setting bounds the burns too
                ecoCruise = EcoCruise(remaining, vHand);
                // Never faster than what can be braked inside what the drone sees ahead before it turns round (after the
                // flip it looks back): the rest of the leg is checked before the flip
                double view = EcoViewAhead();
                double cap = EcoViewSpeed(view, vHand);
                if (cap < vHand + 5 && !fast)
                {
                    Log.Debug("Drone {0}: economy flight not started: it sees only {1:F0} m ahead, too little to brake from any useful speed", Entity.EntityId, view);
                    ecoPhase = EcoPhase.Off;
                    return false;
                }
                ecoCruise = Math.Min(ecoCruise, Math.Max(cap, vHand));
                if (ecoCruise <= vAlong + 1) { ecoPhase = EcoPhase.Coast; }
                else ecoPhase = EcoPhase.Point;
                ecoPhaseStart = now;
                if (vLat.Length() > ECO_START_LATERAL) StartTrim(o, now);   // sideways at the start: that first
                Log.Debug("Drone {0}: economy flight, leg {1:F0} m: {2} group {3:F1} m/s², cruise {4:F0} m/s (sees {7:F0} m ahead along {8}), flip ~{5:F0} s, {6}",
                          Entity.EntityId, len, ecoGroup, ecoAccel, ecoCruise, FlipSeconds(), ecoPhase, view, ecoLook);
            }

            if (ecoPhase == EcoPhase.Quad) return QuadAttitude(o, ref fwd, ref up, d, remaining, crossTrack, vHand);

            Vector3D groupWorld = flightState.WorldMatrix.GetDirectionVector(ecoGroup);
            switch (ecoPhase)
            {
                case EcoPhase.Point:
                    if (Vector3D.Dot(groupWorld, d) > Math.Cos(ECO_ALIGN) && flightState.AngularVelocity.LengthSquared() < 0.01)
                        EcoSet(EcoPhase.Burn, now);
                    else if (now - ecoPhaseStart > ECO_POINT_MAX_TICKS) { EcoEnd(o, "could not point the group"); return false; }
                    if (FlipDue(remaining, vAlong, vHand)) EcoFlip(o, now); else PreflipCheck(remaining, vAlong, vHand);
                    break;
                case EcoPhase.Burn:
                    // Full thrust until the cruise speed (what the leg leaves room for, the Max Speed setting), or until the
                    // speed stops growing (the world's speed limit)
                    if (vAlong > ecoBestV + ECO_STALL_GAIN) { ecoBestV = vAlong; ecoBestFrame = now; }
                    if (vAlong >= ecoCruise - 0.5) EcoSet(EcoPhase.Coast, now);
                    else if (now - ecoBestFrame > ECO_STALL_TICKS && Vector3D.Dot(groupWorld, d) > Math.Cos(ECO_THRUST_ALIGN))
                    {
                        Log.Debug("Drone {0}: economy flight: speed stopped growing at {1:F1} m/s (speed limit): coasting", Entity.EntityId, vAlong);
                        EcoSet(EcoPhase.Coast, now);
                    }
                    if (FlipDue(remaining, vAlong, vHand)) EcoFlip(o, now); else PreflipCheck(remaining, vAlong, vHand);
                    break;
                case EcoPhase.Coast:
                    if (FlipDue(remaining, vAlong, vHand)) { EcoFlip(o, now); break; }
                    PreflipCheck(remaining, vAlong, vHand);
                    // Drifting off the line (or on course to miss it): the course is reassessed, and the same group burns to correct it
                    if (remaining > ECO_TRIM_MIN_LEFT && now - ecoLastTrim > ECO_TRIM_MIN_TICKS && NeedsTrim(remaining, vAlong, crossTrack, vLat))
                        StartTrim(o, now);
                    break;
                case EcoPhase.Trim:
                    if ((ecoTrimV - flightState.RelativeVelocity).Length() < ECO_TRIM_DONE)
                    {
                        // On the new course: its line starts here
                        ecoFrom = ReferencePoint(o);
                        ecoHasCourse = true;
                        ecoLastTrim = now;
                        EcoSet(vAlong >= ecoCruise - 1 ? EcoPhase.Coast : EcoPhase.Point, now);   // still short of the cruise speed: on with the burn
                    }
                    else if (now - ecoPhaseStart > ECO_TRIM_MAX_TICKS) { EcoEnd(o, "course correction not finished"); return false; }
                    else if (FlipDue(remaining, vAlong, vHand)) EcoFlip(o, now); else PreflipCheck(remaining, vAlong, vHand);
                    break;
                case EcoPhase.Flip:
                    if (Vector3D.Dot(groupWorld, -d) > Math.Cos(ECO_ALIGN) && flightState.AngularVelocity.LengthSquared() < 0.01)
                        EcoSet(EcoPhase.Brake, now);
                    else if (remaining < BrakeDistance(vAlong, vHand) + ECO_HANDOVER * 0.5) { EcoEnd(o, "flip too late"); return false; }
                    break;
                case EcoPhase.Brake:
                    if (remaining <= ECO_HANDOVER || vAlong <= vHand + 0.5) { EcoEnd(o, "handover"); return false; }
                    break;
            }
            // Burning and braking steer with the one face: its push is tilted (up to ECO_TILT) towards the line and
            // against the sideways speed
            Vector3D want = ecoPhase == EcoPhase.Flip || ecoPhase == EcoPhase.Brake ? -d : d;
            if (ecoPhase == EcoPhase.Coast)
            {
                // Nothing fires: the camera looks along the way (the replans, the check before the flip see what is ahead)
                ecoAim = d;
                GroupFrame(ecoLook, d, out fwd, out up);
                return true;
            }
            if (ecoPhase == EcoPhase.Trim)
            {
                Vector3D dv = ecoTrimV - flightState.RelativeVelocity;
                if (dv.LengthSquared() > 1e-6) want = Vector3D.Normalize(dv);
            }
            if (ecoPhase == EcoPhase.Burn || ecoPhase == EcoPhase.Brake)
            {
                Vector3D tilt = (crossTrack * 0.1 - vLat) * ECO_TILT_GAIN;
                double tl = tilt.Length();
                if (tl > ECO_TILT) tilt *= ECO_TILT / tl;
                want = Vector3D.Normalize(want + tilt);
            }
            ecoAim = want;
            GroupFrame(ecoGroup, want, out fwd, out up);
            return true;
        }

        #region Gravity (quad-copter)
        // The start checks: a long enough leg, a lift group that can carry the weight with room to tilt. false: normal control
        private bool QuadStart(FlightOrder o, double len, double remaining)
        {
            if (len < ECO_G_MIN_LEG || remaining < ECO_G_MIN_LEG * 0.5) return false;
            Vector3D gUp = flightState.GravityUp;
            double bestDot = 0;
            for (int i = 4; i < 6; i++)   // Up, Down: the groups a drone hovers on
            {
                double dot = Vector3D.Dot(flightState.WorldMatrix.GetDirectionVector((Base6Directions.Direction)i), gUp);
                if (dot > bestDot) { bestDot = dot; ecoGroup = (Base6Directions.Direction)i; }
            }
            double weight = physicalMass * flightState.Gravity.Length();
            double max = bestDot > 0.7 ? EffectiveMax(ecoGroup) : 0;
            if (max < weight * ECO_G_LIFT_MARGIN || weight <= 0)
            {
                Log.Debug("Drone {0}: economy flight (gravity) not started: lift group {1} {2:F0} N for a weight of {3:F0} N (needs {4:F1}x), {5:P0} level",
                          Entity.EntityId, ecoGroup, max, weight, ECO_G_LIFT_MARGIN, bestDot);
                return false;
            }
            double cfg = settings.AlignToPGravity
                ? MathHelperD.ToRadians(Math.Min(settings.PGravityAlignMaxPitchDegrees, settings.PGravityAlignMaxRollDegrees)) : ECO_G_TILT_MAX;
            ecoTilt = Math.Min(Math.Min(cfg, ECO_G_TILT_MAX), Math.Acos(Math.Min(1.0, weight * ECO_G_HOVER_SHARE / max)));
            if (ecoTilt < ECO_G_TILT_MIN)
            {
                Log.Debug("Drone {0}: economy flight (gravity) not started: tilt limit {1:F0}° is too little to fly sideways (lift group {2:F0} N, weight {3:F0} N)",
                          Entity.EntityId, MathHelperD.ToDegrees(ecoTilt), max, weight);
                return false;
            }
            Log.Debug("Drone {0}: economy flight (gravity), leg {1:F0} m: {2} group {3:F0} N for {4:F0} N of weight, tilt up to {5:F0}°",
                      Entity.EntityId, len, ecoGroup, max, weight, MathHelperD.ToDegrees(ecoTilt));
            return true;
        }

        // The attitude of one tick: the lift group along the thrust vector that follows the leg, the heading as the
        // normal control has it. True while economy flight flies.
        private bool QuadAttitude(FlightOrder o, ref Vector3D fwd, ref Vector3D up, Vector3D d, double remaining, Vector3D crossTrack, double vHand)
        {
            if (remaining <= ECO_G_HANDOVER) { EcoEnd(o, "handover"); return false; }
            Vector3D g = flightState.Gravity, gUp = flightState.GravityUp;
            double gm = g.Length();
            double max = EffectiveMax(ecoGroup);
            if (gm < 0.05 || max <= 0) { EcoEnd(o, "no gravity or no lift"); return false; }

            // The speed for the distance left: a brake planned with half the horizontal acceleration the tilt gives
            double aBrake = ECO_G_BRAKE_SHARE * gm * Math.Tan(ecoTilt);
            double vCap = o.SpeedCap > 0 ? Math.Min(MaxSpeed, o.SpeedCap) : MaxSpeed;
            double lag = Math.Max(0, Vector3D.Dot(flightState.RelativeVelocity, d)) * ECO_G_LAG;   // the tilt takes time: the brake starts earlier
            double vProfile = Math.Min(vCap, Math.Sqrt(vHand * vHand + 2 * aBrake * Math.Max(0, remaining - ECO_G_HANDOVER - lag)));
            double down = -Vector3D.Dot(d, gUp);
            if (down > 1e-3) vProfile = Math.Min(vProfile, MAX_DESCENT_SPEED / down);
            // The course is reassessed every tick: the drone heads for a point on the line ahead of its closest one (further
            // ahead the further off it is), so the leg's end is always where it is going
            double carrotAhead = Math.Min(Math.Max(ECO_G_CARROT, 2 * crossTrack.Length()), remaining);
            Vector3D toCarrot = crossTrack + d * carrotAhead;
            Vector3D vWant = (toCarrot.LengthSquared() > 1e-6 ? Vector3D.Normalize(toCarrot) : d) * vProfile;
            Vector3D aWant = (vWant - flightState.RelativeVelocity) * ECO_G_KP;

            // T = m (a - g), then the limits: the tilt, and what the group can give (the weight first, the sideways part after)
            Vector3D T = (aWant - g) * physicalMass;
            double tUp = Math.Max(Vector3D.Dot(T, gUp), physicalMass * gm * 0.3);
            Vector3D tH = T - gUp * Vector3D.Dot(T, gUp);
            double cap = max * 0.95;
            tUp = Math.Min(tUp, cap);
            double hMax = Math.Min(tUp * Math.Tan(ecoTilt), Math.Sqrt(Math.Max(0, cap * cap - tUp * tUp)));
            double hl = tH.Length();
            if (hl > hMax) tH *= hMax / hl;
            T = gUp * tUp + tH;
            ecoThrust = T;

            Vector3D tDir = Vector3D.Normalize(T);
            ecoAim = tDir;
            Vector3D groupWorld = flightState.WorldMatrix.GetDirectionVector(ecoGroup);
            if (Vector3D.Dot(groupWorld, tDir) < Math.Cos(ECO_G_LOST))
            {
                if (++ecoLostTicks > ECO_G_LOST_TICKS) { EcoEnd(o, "the lift group is not following the thrust vector"); return false; }
            }
            else ecoLostTicks = 0;

            // Lift group along T, the heading (fwd) the normal control asked for
            Vector3D h = fwd;
            Vector3D upAxis = ecoGroup == Base6Directions.Direction.Up ? tDir : -tDir;
            up = upAxis;
            fwd = Perp(h, upAxis);
            return true;
        }

        // The thrust of one tick: the lift group alone, as much as T asks along its actual axis
        private void QuadThrust()
        {
            double max = EffectiveMax(ecoGroup);
            double along = Vector3D.Dot(flightState.WorldMatrix.GetDirectionVector(ecoGroup), ecoThrust);
            EcoThrust(max > 0 ? MathHelperD.Clamp(along / max, 0, 1) : 0);
        }
        #endregion

        // The axis the drone sees along: a working camera's facing (controller frame); without cameras required, the
        // simulated one looks forward; no camera at all: the strongest group's (it sees only its sensors' range anyway)
        private Base6Directions.Direction LookAxis()
        {
            var asleep = settings != null ? settings.SleepingBlockIds : null;
            for (int i = 0; i < cameraBlocks.Count; i++)
            {
                if (!PathfindingManager.InstrumentOn(cameraBlocks[i], asleep)) continue;
                Vector3D local = Vector3D.TransformNormal(cameraBlocks[i].WorldMatrix.Forward, flightState.WorldMatrixToLocal);
                return Base6Directions.GetClosestDirection((Vector3)local);
            }
            return Base6Directions.Direction.Forward;
        }

        // How far the drone sees along its look axis: the camera range with a working camera (or cameras not required),
        // else the sensors' range
        private double EcoViewAhead()
        {
            var pf = AutomataSession.GetConfig().Pathfinding;
            double view = pf.SimulatedSensorRange;
            if (!pf.RequireCamerasForPathfinding() || AnyCameraOn()) view = Math.Max(view, pf.MaxSimulatedCameraRaycastMeters());
            return view;
        }

        // The fastest cruise whose brake, the flip and the check before it fit inside ECO_VIEW_SHARE of the view
        private double EcoViewSpeed(double view, double vHand)
        {
            double room = view * ECO_VIEW_SHARE, lo = 0, hi = MaxSpeed;
            for (int i = 0; i < 30; i++)
            {
                double v = (lo + hi) * 0.5;
                double need = BrakeDistance(v, vHand) + v * (FlipSeconds() + ECO_PREFLIP_SECONDS) + ECO_FLIP_MARGIN;
                if (need <= room) lo = v; else hi = v;
            }
            return lo;
        }

        // A few seconds before the flip, with parts of the route not seen yet: a replan now, while the camera still looks
        // ahead (after the flip it looks back, and only the sensors see where the drone goes)
        private void PreflipCheck(double remaining, double vAlong, double vHand)
        {
            if (ecoPreflipAsked || routeUnseen <= 0) return;
            if (!FlipDue(remaining - Math.Max(0, vAlong) * ECO_PREFLIP_SECONDS, vAlong, vHand)) return;
            ecoPreflipAsked = true;
            routeJitForce = true;
            Log.Debug("Drone {0}: economy flight: {1} stretch(es) of the route not seen yet, checking them before the flip ({2:F0} m left)",
                      Entity.EntityId, routeUnseen, remaining);
        }

        private void EcoFlip(FlightOrder o, int now)
        {
            if (routeUnseen > 0)
                Log.Warning("Drone {0}: economy flight: turning round with {1} stretch(es) of the route not seen (the check before the flip found no time)",
                            Entity.EntityId, routeUnseen);
            EcoSet(EcoPhase.Flip, now);
        }

        // Replans while economy flight flies: only while the drone looks ahead (not while it turns, brakes or corrects)
        private bool EconomyHoldsRoute()
        {
            return EconomyFlying() && (ecoPhase == EcoPhase.Flip || ecoPhase == EcoPhase.Brake || ecoPhase == EcoPhase.Trim);
        }

        // Will the drone be further than the corridor from the line, now or at the leg's end, if it coasts on?
        private bool NeedsTrim(double remaining, double vAlong, Vector3D crossTrack, Vector3D vLat)
        {
            double corridor = Corridor(remaining);
            if (crossTrack.Length() > corridor) return true;
            Vector3D miss = vLat * (remaining / Math.Max(1.0, vAlong)) - crossTrack;   // where it ends up relative to the line
            return miss.Length() > corridor;
        }

        // A new course: from here straight to the leg's end at the present speed. The velocity it needs is the target
        // of a burn with the same group (pointed along the difference); the leg's line is drawn again from here when done.
        private void StartTrim(FlightOrder o, int now)
        {
            Vector3D a, t;
            GetLeg(o, out a, out t);
            Vector3D c = t - ReferencePoint(o);
            double dist = c.Length();
            double speed = flightState.RelativeVelocity.Length();
            if (dist < 1 || speed < 1) return;
            ecoTrimV = c / dist * speed;
            Log.Debug("Drone {0}: economy flight: off course ({1:F0} m/s to correct), new course to the leg's end {2:F0} m on", Entity.EntityId,
                      (ecoTrimV - flightState.RelativeVelocity).Length(), dist);
            EcoSet(EcoPhase.Trim, now);
        }

        private void LogEcoSkip(FlightOrder o)
        {
            ecoSkipOrder = o;
            Vector3D d, crossTrack, vLat;
            double len, remaining, vAlong;
            if (!EcoLeg(o, out d, out len, out remaining, out crossTrack, out vAlong, out vLat) || len < (flightState.InGravity ? ECO_G_MIN_LEG : ECO_MIN_LEG)) return;
            Log.Debug("Drone {0}: economy flight not used on this {1:F0} m leg: gravity {2}, watched {3}, owner {4}, hold attitude {5}, phase {6}, " +
                      "speed {7:F2} m/s (max 0.5), boring {8}, yielding {9}, mass {10:F0}",
                      Entity.EntityId, len, flightState.InGravity, o.Watched, routeOwner, o.HoldAttitudeInTransit, o.Phase,
                      flightState.FrameVelocity.Length(), boring, siteYielding, physicalMass);
        }

        /// <summary>The velocity side of economy flight (after the attitude): burns, coasting, drift corrections.</summary>
        private void EconomyTranslate(FlightOrder o)
        {
            Vector3D d, crossTrack, vLat;
            double len, remaining, vAlong;
            if (!EcoLeg(o, out d, out len, out remaining, out crossTrack, out vAlong, out vLat)) { CoastThrusters(); return; }
            if (ecoPhase == EcoPhase.Quad) { QuadThrust(); return; }
            double vHand = Math.Min(MaxSpeed, Math.Max(ExitSpeedFor(o), ECO_HAND_SPEED));
            // The one group (ecoGroup) is the only thruster that fires: forward to the cruise speed, and after the flip
            // the same group against the motion. Everything else stays at zero.
            double throttle = 0;
            switch (ecoPhase)
            {
                case EcoPhase.Burn:
                    throttle = 1;   // full (setting) thrust: the burn ends on the speed (EconomyAttitude)
                    break;
                case EcoPhase.Trim:
                    throttle = MathHelperD.Clamp(2 * (ecoTrimV - flightState.RelativeVelocity).Length() / ecoAccel, 0, 1);
                    break;
                case EcoPhase.Brake:
                {
                    // Bursts at full thrust: the stopping distance down to the handover speed (mass, the group's thrust) against
                    // the distance to the handover point. Fires when the slack is down to ECO_BURST_ON, stops at ECO_BURST_OFF:
                    // it gets there within ECO_BURST_OFF metres
                    double groupMax = EffectiveMax(ecoGroup);
                    double a = physicalMass > 0 ? groupMax * EcoThrottleCap(groupMax) / physicalMass * ECO_BURST_ACCEL : ecoAccel;
                    double stop = Math.Max(0, vAlong * vAlong - vHand * vHand) / (2 * Math.Max(0.05, a));
                    double slack = Math.Max(0, remaining - ECO_HANDOVER) - stop;
                    if (!ecoBursting && slack < ECO_BURST_ON) ecoBursting = true;
                    else if (ecoBursting && slack > ECO_BURST_OFF) ecoBursting = false;
                    throttle = ecoBursting ? 1 : 0;
                    break;
                }
            }
            // Not pointed (yet): no push, it would go the wrong way
            if (throttle > 0 && Vector3D.Dot(flightState.WorldMatrix.GetDirectionVector(ecoGroup), ecoAim) < Math.Cos(ECO_THRUST_ALIGN))
                throttle = 0;
            EcoThrust(throttle * EcoThrottleCap(EffectiveMax(ecoGroup)));
        }

        // The group's throttle at full burn under the Max thrust setting (the cap is a share of the strongest face)
        private double EcoThrottleCap(double groupMax)
        {
            return groupMax > 0 ? MathHelperD.Clamp(ThrustFraction * StrongestFaceThrust() / groupMax, 0, 1) : 1;
        }

        // ecoGroup at 'throttle', every other thruster at zero, dampeners off
        private void EcoThrust(double throttle)
        {
            for (int i = 0; i < 6; i++) SetDirectionalThrustOverride((Base6Directions.Direction)i, i == (int)ecoGroup ? throttle : 0);
            thrustOverridesActive = true;
            if (shipController != null && shipController.DampenersOverride) shipController.DampenersOverride = false;
            ResetIntegralTerms();
            commandedVelocity = flightState.LinearVelocity;   // the normal controller takes over from the real speed
        }

        private bool EcoLeg(FlightOrder o, out Vector3D d, out double len, out double remaining, out Vector3D crossTrack,
                            out double vAlong, out Vector3D vLat)
        {
            Vector3D a, t;
            GetLeg(o, out a, out t);
            if (ecoHasCourse && ReferenceEquals(ecoOrder, o)) a = ecoFrom;
            Vector3D line = t - a;
            len = line.Length();
            d = len > 1e-3 ? line / len : Vector3D.Zero;
            Vector3D p = ReferencePoint(o);
            double s = MathHelperD.Clamp(Vector3D.Dot(p - a, d), 0, len);
            remaining = len - s;
            crossTrack = a + d * s - p;
            Vector3D vRel = flightState.RelativeVelocity;
            vAlong = Vector3D.Dot(vRel, d);
            vLat = vRel - d * vAlong;
            return len > 1e-3;
        }

        private static double Corridor(double remaining)
        {
            return Math.Max(ECO_CORRIDOR_MIN, remaining * ECO_CORRIDOR_SHARE);
        }

        // Cruise speed for what is left: accelerate with the group, coast while flipping, brake with ECO_BRAKE_FRAC of it,
        // all before the handover point
        private double EcoCruise(double remaining, double vHand)
        {
            double avail = Math.Max(0, remaining - ECO_HANDOVER - ECO_FLIP_MARGIN);
            double aB = ecoAccel * ECO_BRAKE_FRAC;
            double v = MaxSpeed;
            for (int i = 0; i < 3; i++)   // the flip's coast depends on the speed: a few rounds settle it
            {
                double room = Math.Max(0, avail - v * FlipSeconds());
                // v^2 / 2a (burn) + (v^2 - vHand^2) / 2aB (brake) = room
                v = Math.Min(MaxSpeed, Math.Sqrt(Math.Max(0, (2 * room + vHand * vHand / aB) / (1 / ecoAccel + 1 / aB))));
            }
            return v;
        }

        private double BrakeDistance(double v, double vHand)
        {
            return Math.Max(0, v * v - vHand * vHand) / (2 * ecoAccel * ECO_BRAKE_FRAC);
        }

        private bool FlipDue(double remaining, double v, double vHand)
        {
            return remaining <= BrakeDistance(v, vHand) + Math.Max(0, v) * FlipSeconds() + ECO_HANDOVER + ECO_FLIP_MARGIN;
        }

        // A half turn with the gyros: the rotation profile (ROT_MAX_SPEED, the stop planned with ROT_BRAKE_FACTOR of the
        // estimated α), with margin
        private double FlipSeconds()
        {
            double alpha = Math.Max(0.05, rotAlphaEstimate);
            double peak = Math.Min(ROT_MAX_SPEED, Math.Sqrt(alpha * ROT_BRAKE_FACTOR * Math.PI));
            return Math.PI / Math.Max(0.05, peak) * 1.5 + 2;
        }

        // Attitude that puts 'group' (the way its thrust pushes the drone) along 'w', keeping the rest of the
        // attitude as close to the present one as possible
        private void GroupFrame(Base6Directions.Direction group, Vector3D w, out Vector3D fwd, out Vector3D up)
        {
            MatrixD m = flightState.WorldMatrix;
            Vector3D right;
            switch (group)
            {
                case Base6Directions.Direction.Forward:  fwd = w;  up = Perp(m.Up, fwd); return;
                case Base6Directions.Direction.Backward: fwd = -w; up = Perp(m.Up, fwd); return;
                case Base6Directions.Direction.Up:       up = w;   fwd = Perp(m.Forward, up); return;
                case Base6Directions.Direction.Down:     up = -w;  fwd = Perp(m.Forward, up); return;
                case Base6Directions.Direction.Right:    right = w; break;
                default:                                 right = -w; break;
            }
            fwd = Perp(m.Forward, right);
            up = Vector3D.Cross(right, fwd);
        }

        private static Vector3D Perp(Vector3D v, Vector3D n)
        {
            Vector3D p = v - n * Vector3D.Dot(v, n);
            if (p.LengthSquared() < 1e-6) p = Vector3D.CalculatePerpendicularVector(n);
            return Vector3D.Normalize(p);
        }

        // Every thruster at zero, dampeners off: the drone keeps its speed
        private void CoastThrusters()
        {
            for (int i = 0; i < 6; i++) SetDirectionalThrustOverride((Base6Directions.Direction)i, 0);
            thrustOverridesActive = true;
            if (shipController != null && shipController.DampenersOverride) shipController.DampenersOverride = false;
            ResetIntegralTerms();
            commandedVelocity = flightState.LinearVelocity;
        }

        private void EcoSet(EcoPhase phase, int now)
        {
            if (phase == ecoPhase) return;
            Log.Debug("Drone {0}: economy flight: {1} -> {2} ({3:F0} m/s along)", Entity.EntityId, ecoPhase, phase,
                      Vector3D.Dot(flightState.RelativeVelocity, ecoOrder != null ? EcoDir(ecoOrder) : Vector3D.Zero));
            ecoPhase = phase;
            ecoPhaseStart = now;
            ecoBursting = false;
            if (phase == EcoPhase.Burn) { ecoBestV = Vector3D.Dot(flightState.RelativeVelocity, ecoOrder != null ? EcoDir(ecoOrder) : Vector3D.Zero); ecoBestFrame = now; }
        }

        private Vector3D EcoDir(FlightOrder o)
        {
            Vector3D a, t;
            GetLeg(o, out a, out t);
            if (ecoHasCourse && ReferenceEquals(ecoOrder, o)) a = ecoFrom;
            Vector3D l = t - a;
            return l.LengthSquared() > 1e-6 ? Vector3D.Normalize(l) : Vector3D.Zero;
        }

        private void EcoEnd(FlightOrder o, string why)
        {
            Log.Debug("Drone {0}: economy flight ends ({1}, phase {2}): normal control", Entity.EntityId, why, ecoPhase);
            ecoPhase = EcoPhase.Off;
            commandedVelocity = flightState.LinearVelocity;
            ResetIntegralTerms();
        }
    }
}
