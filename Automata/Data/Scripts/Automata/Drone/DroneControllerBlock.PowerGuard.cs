using Sandbox.ModAPI;
using VRage.Utils;

using Automata.Util;
using Automata.Util.Strings;

namespace Automata.Drone
{
    // Power guard: a drone out in the field (not docked, not parked) with no power of its own to fly - batteries empty /
    // off / stuck on Recharge and no other producer, or not a single thruster working - can't do anything: every order
    // it flies is thrust that isn't there (Miner1: empty battery 73 km out, a dock attempt every 15 s for hours). It is
    // stopped (State.Error, hands off) and checked again at a low rate; flight, jobs, bids, auto-home wait until power
    // is back. Docked / parked drones are left to pre-flight, which checks own power before letting go.
    public partial class DroneControllerBlock
    {
        private const int GROUNDED_RECHECK_TICKS = 30 * 60;   // grounded: power checked again this often
        private string groundedReason;                         // non-null: grounded (in State.Error) for want of power
        private int groundedNextCheck;
        private const int GROUNDED_ALERT_TICKS = 5 * 60 * 60;   // stalled this long without power: the player is told (once)
        private int groundedSince;
        private bool groundedAlerted;

        private bool Grounded { get { return groundedReason != null; } }

        // UpdateBeforeSimulation100, server, AI on
        private void UpdatePowerGuard()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (Grounded && now < groundedNextCheck && currentState == State.Error) return;
            if (!Grounded && currentState == State.Error) return;   // another error owns the drone
            string why = GroundedReason();
            if (why == null)
            {
                if (!Grounded) return;
                groundedReason = null;
                groundedAlerted = false;
                errorReason = null;
                Report(MyStrings.Drone_PowerBackFlyingAgain);
                if (currentState == State.Error) SetState(IsParkedNow() ? State.Docked : State.Standby);
                UpdateAlert();
                return;
            }
            groundedNextCheck = now + GROUNDED_RECHECK_TICKS;
            bool already = Grounded;
            groundedReason = why;
            errorReason = Loc.F(MyStrings.Drone_NoPowerToFlyStopped, why, GROUNDED_RECHECK_TICKS / 60);
            if (already && !groundedAlerted && now - groundedSince >= GROUNDED_ALERT_TICKS)
            {
                groundedAlerted = true;
                var at = block.GetPosition();
                Broadcast(Loc.F(MyStrings.Drone_NoPowerToFlyStalled, why, GROUNDED_ALERT_TICKS / 3600, at.X, at.Y, at.Z));
            }
            if (already && currentState == State.Error) return;   // still stopped: nothing new to say
            if (!already)
            {
                Report(errorReason);
                groundedSince = now;
                groundedAlerted = false;
            }
            // (also when an order given meanwhile - Go home, Execute - took it out of the error: it can't fly it)
            StopJob(errorReason);
            ClearFlightOrder();
            ClearAllThrustOverrides();
            ReleaseGyroscopes();
            SetState(State.Error);
            UpdateAlert();
        }

        // Why the drone can't fly now, or null. Docked / parked / waking: not judged here (pre-flight does).
        private string GroundedReason()
        {
            if (shipController == null || preflightStage != 0 || IsParkedNow() || DockedConnector() != null) return null;
            string power = OwnPowerProblem();
            if (power != null) return power;
            for (int d = 0; d < 6; d++)
            {
                var list = allThrusters[d];
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t != null && !t.Closed && t.IsWorking) return null;
                }
            }
            return Loc.T(MyStrings.Drone_NoThrusterWorkingOffUnpowered);
        }
    }
}
