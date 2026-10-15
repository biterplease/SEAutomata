using System;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// Holding on the game's dampeners. While the drone only has to stay where it is - waiting for a dock to come free,
    /// yielding a shared site to another drone, or standing by after a flight with nothing else to do - its flight tick
    /// is skipped: thrust overrides go to zero and the dampeners are switched on (the game's thrust component then
    /// holds the velocity at zero and carries the weight, with its own thrust figures), the gyros hold the attitude
    /// (override on, zero rates). Every tick only checks that the reason still holds (same order, same reason); every 10
    /// ticks the drift is checked (flight state captured there). Anything else - a new order, the reason gone, a drift
    /// past HOLD_DRIFT, a push - ends the hold and normal control takes over on the next tick.
    /// Not for orders anchored to a moving grid (the dampeners hold still in the world, not relative to it), nor during
    /// work: a welder at its spot needs the exact position the normal control keeps.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private const double HOLD_ENTER_SPEED = 0.15;    // m/s: still enough to hand over
        private const double HOLD_ENTER_SPIN = 0.02;     // rad/s
        private const double HOLD_ENTER_ALIGN = 0.05;    // rad: pointed as asked
        private const double HOLD_ENTER_DIST = 0.5;      // m from the order's target (or its arrival tolerance, if more)
        private const double HOLD_DRIFT = 1.0;           // m from where the hold began: normal control again
        private const double HOLD_EXIT_SPEED = 0.5;      // m/s: pushed / drifting
        private const double HOLD_EXIT_SPIN = 0.05;      // rad/s
        private const int HOLD_RETRY_TICKS = 120;        // after a drift: normal control at least this long

        private const string HOLD_DOCK_WAIT = "dock wait";
        private const string HOLD_YIELD = "yielding";
        private const string HOLD_STANDBY = "standby";

        private bool holdActive;
        private FlightOrder holdOrder;
        private string holdReason;
        private Vector3D holdPos;
        private int holdNextTry;

        // Why the drone may hold on the dampeners now (null: it may not)
        private string HoldReason(FlightOrder o)
        {
            if (o == null || !ReferenceEquals(o, activeFlightOrder) || settings == null || !settings.IsEnabled
                || currentState == State.Error || preflightStage != 0 || isParked || boring)
                return null;
            if (o.AnchorEntityId != 0) return null;
            if (dockWaitTarget != 0) return HOLD_DOCK_WAIT;
            if (siteYielding) return HOLD_YIELD;
            if (arrivalReported && currentState == State.Standby && currentJob == null && nextLeg == null && queuedLegs.Count == 0)
                return HOLD_STANDBY;
            return null;
        }

        /// <summary>After a normal flight tick: hands the drone to the dampeners when it may hold and is settled there.</summary>
        private void MaybeDampenerHold(FlightOrder o, double orientationError)
        {
            if (holdActive || Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter < holdNextTry) return;
            string why = HoldReason(o);
            if (why == null) return;
            if (flightState.LinearVelocity.LengthSquared() > HOLD_ENTER_SPEED * HOLD_ENTER_SPEED
                || flightState.AngularVelocity.LengthSquared() > HOLD_ENTER_SPIN * HOLD_ENTER_SPIN
                || orientationError > HOLD_ENTER_ALIGN)
                return;
            if (!ReferenceEquals(why, HOLD_YIELD))   // a yield holds wherever it stopped
            {
                Vector3D rWorld = Vector3D.TransformNormal(o.ReferenceOffset.ToVector3D(), flightState.WorldMatrix);
                double tol = Math.Max(HOLD_ENTER_DIST, o.ArrivalTolerance);
                if (Vector3D.DistanceSquared(flightState.Position + rWorld, o.Target.ToVector3D()) > tol * tol) return;
            }
            ClearAllThrustOverrides();             // overrides 0, dampeners on
            Vector3D hold = Vector3D.Zero;
            ApplyGyroscopesCommand(ref hold);      // attitude held by the gyros
            holdActive = true;
            holdOrder = o;
            holdReason = why;
            holdPos = flightState.Position;
            Log.Debug("Drone {0}: holding on the dampeners ({1})", _entityId, why);
        }

        /// <summary>Every tick while holding: true while the hold goes on (the flight tick is skipped).</summary>
        private bool DampenerHolding()
        {
            if (!holdActive) return false;
            if (ReferenceEquals(activeFlightOrder, holdOrder) && ReferenceEquals(HoldReason(activeFlightOrder), holdReason)) return true;
            EndHold("no longer holding", false);
            return false;
        }

        /// <summary>Every 10 ticks while holding (flight state just captured): drifted or pushed - normal control again.</summary>
        private void CheckHoldDrift()
        {
            if (!holdActive) return;
            double d = Vector3D.Distance(flightState.Position, holdPos);
            if (d > HOLD_DRIFT
                || flightState.LinearVelocity.LengthSquared() > HOLD_EXIT_SPEED * HOLD_EXIT_SPEED
                || flightState.AngularVelocity.LengthSquared() > HOLD_EXIT_SPIN * HOLD_EXIT_SPIN)
                EndHold(string.Format("drifted {0:F1} m, {1:F2} m/s", d, flightState.LinearVelocity.Length()), true);
        }

        private void EndHold(string why, bool drift)
        {
            if (!holdActive) return;
            Log.Debug("Drone {0}: dampener hold ends ({1}, {2}): normal control", _entityId, holdReason, why);
            holdActive = false;
            holdOrder = null;
            holdReason = null;
            if (drift) holdNextTry = Sandbox.ModAPI.MyAPIGateway.Session.GameplayFrameCounter + HOLD_RETRY_TICKS;
        }
    }
}
