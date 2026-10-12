using System;

using VRageMath;

using Automata.Util.Logging;

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
    /// </summary>
    public partial class DroneControllerBlock
    {
        private enum EcoPhase : byte { None, Point, Burn, Coast, Flip, Brake, Off }

        private const double ECO_MIN_LEG = 1000;         // m: shorter legs are flown normally
        private const double ECO_HANDOVER = 150;         // m before the leg's end: normal control again
        private const double ECO_HAND_SPEED = 15;        // m/s: the brake burn ends at this (or the leg's exit speed)
        private const double ECO_BRAKE_FRAC = 0.8;       // share of the group's accel the brake is planned with
        private const double ECO_ALIGN = 0.09;           // rad (~5°): pointed
        private const double ECO_START_LATERAL = 5;      // m/s sideways to the leg: not started (normal control for this leg)
        private const double ECO_CORRIDOR_MIN = 25;      // m off the line allowed at the start...
        private const double ECO_CORRIDOR_SHARE = 0.01;  // ...or this share of what is left of the leg, if more
        private const double ECO_DRIFT_END = 4;          // x the corridor, drifting while coasting: normal control for the rest of the leg
        private const double ECO_TILT = 0.176;           // tan 10°: most the burn / brake push is tilted off the line to cancel the drift
        private const double ECO_TILT_GAIN = 0.05;       // tilt per m/s of sideways error
        private const double ECO_THRUST_ALIGN = 0.35;    // rad (~20°): no push while the group points further off than this
        private const double ECO_FLIP_MARGIN = 30;       // m: on top of the brake distance and the coast while flipping
        private const int ECO_POINT_MAX_TICKS = 30 * 60; // a turn that never ends (gyros): normal control

        private FlightOrder ecoOrder;
        private EcoPhase ecoPhase;
        private Base6Directions.Direction ecoGroup;
        private double ecoAccel, ecoCruise;
        private int ecoPhaseStart;
        private Vector3D ecoAim;                         // where the group points this tick (the line, tilted a little to cancel the drift)
        private FlightOrder ecoSkipOrder;                // the order whose "not started" reason was logged
        private bool ecoActive;                          // this tick: economy flight drives the drone

        // Economy flight owns the thrusters from the first burn to the handover: no replan may cut in
        private bool EconomyFlying()
        {
            return ecoActive && ecoPhase != EcoPhase.None && ecoPhase != EcoPhase.Off;
        }

        private bool EconomyEligible(FlightOrder o)
        {
            return settings != null && settings.EconomyFlight && !flightState.InGravity && o.Watched
                && (routeOwner == RouteOwner.FlyRoute || routeOwner == RouteOwner.Docking)
                && !o.HoldAttitudeInTransit && o.Phase != FlightPhase.Align && o.Phase != FlightPhase.MatchSpeed
                && flightState.FrameVelocity.LengthSquared() < 0.25 && !boring && !siteYielding && physicalMass > 0;
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
            if (!ReferenceEquals(ecoOrder, o)) { ecoOrder = o; ecoPhase = EcoPhase.None; }
            if (ecoPhase == EcoPhase.Off) return false;

            Vector3D d, crossTrack, vLat;
            double len, remaining, vAlong;
            if (!EcoLeg(o, out d, out len, out remaining, out crossTrack, out vAlong, out vLat)) return false;
            int now = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter;
            double vHand = Math.Min(MaxSpeed, Math.Max(ExitSpeedFor(o), ECO_HAND_SPEED));

            if (ecoPhase == EcoPhase.None)
            {
                if (len < ECO_MIN_LEG || remaining < ECO_MIN_LEG * 0.5 || vLat.Length() > ECO_START_LATERAL || crossTrack.Length() > Corridor(remaining))
                {
                    if (len >= ECO_MIN_LEG)
                        Log.Debug("Drone {0}: economy flight not started: leg {1:F0} m, {2:F0} m left, {3:F1} m/s sideways (max {4:F0}), {5:F0} m off the line (max {6:F0})",
                                  Entity.EntityId, len, remaining, vLat.Length(), ECO_START_LATERAL, crossTrack.Length(), Corridor(remaining));
                    ecoPhase = EcoPhase.Off;
                    return false;
                }
                // The strongest group (learnt gains included)
                double best = 0;
                for (int i = 0; i < 6; i++)
                {
                    double f = EffectiveMax((Base6Directions.Direction)i);
                    if (f > best) { best = f; ecoGroup = (Base6Directions.Direction)i; }
                }
                if (best <= 0) { Log.Debug("Drone {0}: economy flight not started: no thrust in any direction", Entity.EntityId); ecoPhase = EcoPhase.Off; return false; }
                ecoAccel = best / physicalMass;
                ecoCruise = EcoCruise(remaining, vHand);
                if (ecoCruise <= vAlong + 1) { ecoPhase = EcoPhase.Coast; }
                else ecoPhase = EcoPhase.Point;
                ecoPhaseStart = now;
                Log.Debug("Drone {0}: economy flight, leg {1:F0} m: {2} group {3:F1} m/s², cruise {4:F0} m/s, flip ~{5:F0} s, {6}",
                          Entity.EntityId, len, ecoGroup, ecoAccel, ecoCruise, FlipSeconds(), ecoPhase);
            }

            Vector3D groupWorld = flightState.WorldMatrix.GetDirectionVector(ecoGroup);
            switch (ecoPhase)
            {
                case EcoPhase.Point:
                    if (Vector3D.Dot(groupWorld, d) > Math.Cos(ECO_ALIGN) && flightState.AngularVelocity.LengthSquared() < 0.01)
                        EcoSet(EcoPhase.Burn, now);
                    else if (now - ecoPhaseStart > ECO_POINT_MAX_TICKS) { EcoEnd(o, "could not point the group"); return false; }
                    if (FlipDue(remaining, vAlong, vHand)) EcoSet(EcoPhase.Flip, now);
                    break;
                case EcoPhase.Burn:
                    if (vAlong >= ecoCruise - 0.5) EcoSet(EcoPhase.Coast, now);
                    if (FlipDue(remaining, vAlong, vHand)) EcoSet(EcoPhase.Flip, now);
                    break;
                case EcoPhase.Coast:
                    // Only the one face fires: a drift it can't cancel while burning / braking is left to the normal control
                    if (crossTrack.Length() > Corridor(remaining) * ECO_DRIFT_END) { EcoEnd(o, "drifted off the line"); return false; }
                    if (FlipDue(remaining, vAlong, vHand)) EcoSet(EcoPhase.Flip, now);
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

        private void LogEcoSkip(FlightOrder o)
        {
            ecoSkipOrder = o;
            Vector3D d, crossTrack, vLat;
            double len, remaining, vAlong;
            if (!EcoLeg(o, out d, out len, out remaining, out crossTrack, out vAlong, out vLat) || len < ECO_MIN_LEG) return;
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
            double vHand = Math.Min(MaxSpeed, Math.Max(ExitSpeedFor(o), ECO_HAND_SPEED));
            // The one group (ecoGroup) is the only thruster that fires: forward to the cruise speed, and after the flip
            // the same group against the motion. Everything else stays at zero.
            double throttle = 0;
            switch (ecoPhase)
            {
                case EcoPhase.Burn:
                    throttle = MathHelperD.Clamp((ecoCruise - vAlong) / ecoAccel, 0, 1);
                    break;
                case EcoPhase.Brake:
                {
                    // The planned speed for the distance left (ECO_BRAKE_FRAC of the acceleration): that share as feed-forward,
                    // the speed error on top
                    double profile = Math.Sqrt(vHand * vHand + 2 * ecoAccel * ECO_BRAKE_FRAC * Math.Max(0, remaining - ECO_HANDOVER));
                    throttle = MathHelperD.Clamp(ECO_BRAKE_FRAC + (vAlong - profile) / ecoAccel, 0, 1);
                    break;
                }
            }
            // Not pointed (yet): no push, it would go the wrong way
            if (throttle > 0 && Vector3D.Dot(flightState.WorldMatrix.GetDirectionVector(ecoGroup), ecoAim) < Math.Cos(ECO_THRUST_ALIGN))
                throttle = 0;
            EcoThrust(throttle);
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
        }

        private Vector3D EcoDir(FlightOrder o)
        {
            Vector3D a, t;
            GetLeg(o, out a, out t);
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
