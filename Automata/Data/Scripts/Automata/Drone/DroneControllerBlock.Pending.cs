using System;

using Sandbox.ModAPI;
using VRageMath;

using Automata.Pathfinding;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.Drone
{
    /// <summary>
    /// A route that needs a Lazy Theta* search is planned on a worker thread (PathSearchQueue). FlyRoute(async) and
    /// StartDocking(async) can wait for it:
    ///  - The plan runs as usual; when the planner answers "pending" (PlanPendingException, thrown where it asked) the
    ///    entry point puts back what the attempt consumed, remembers its arguments (the replan machinery: RememberFlyRoute
    ///    / RememberDocking; a docking keeps what it worked out before the route, DockPrep) and the drone holds with no
    ///    order (routePending).
    ///  - ServicePendingRoute (the task runner first; outside a job UpdateBeforeSimulation10): while the drone's search
    ///    is queued or running it only waits (PathSearchQueue.Waiting). When it is done (TakeNews) the route is planned
    ///    again from the remembered arguments (ReplanCore): the planner goes straight to the answer (the stages before it
    ///    aren't run again), checks it with the rays and doors the worker skipped, and the legs are built as always.
    ///    Another stretch asks for another search: holds on. No route: routeFailed, handled like any "no route".
    ///  - Anything that clears the flight order (a new job, Stop, a player's order) drops the wait.
    /// </summary>
    public partial class DroneControllerBlock
    {
        private bool routePending;           // holding for a route search; the plan runs again when it is done
        private int routePendingSince;       // tick it began
        private bool pendRecover, pendReplanning;   // recoverPlanning / replanning of the call that went pending
        private bool asyncPlanOk;            // FlyRoute / StartDocking (async) is planning: the planner may answer "pending"
        #region Timing
        private const int PENDING_MAX_TICKS = 30 * 60;      // no answer by then: no route
        #endregion

        /// <summary>FlyRoute went pending: the drone holds; the same route is planned again when the search is done.</summary>
        private Orchestrator.TaskFailure DeferFlyRoute(IMyTerminalBlock anchor, ref MatrixD am, Vector3D target, Vector3D approach, double hull,
                                                       Vector3D referenceOffset, ref MatrixD frame, bool checkApproachLine)
        {
            HoldForSearch();
            RememberFlyRoute(anchor, ref am, target, approach, hull, referenceOffset, ref frame, checkApproachLine);
            MarkPending();
            return Orchestrator.TaskFailure.None;
        }

        /// <summary>StartDocking went pending: the drone holds; the docking is planned again (its preparation kept) when the search is done.</summary>
        private void DeferDocking(DockPrep prep)
        {
            HoldForSearch();
            RememberDocking(prep.Target.EntityId, prep.Mi, Vector3DData.FromVector3D(prep.OrigOffset), Vector3DData.FromVector3D(prep.OrigForward),
                            Vector3DData.FromVector3D(prep.OrigUp));
            pendingDock = prep;
            MarkPending();
        }

        private void HoldForSearch()
        {
            ClearFlightOrder();
            constructionLegs.Clear();
            constructionOrder = null;
            routePoints.Clear();
            dockRoutePoints.Clear();
        }

        private void MarkPending()
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (!routePending) routePendingSince = now;
            routePending = true;
            pendRecover = recoverPlanning;
            pendReplanning = replanning;
            if (Log.DebugEnabled) Log.Debug("Drone {0}: route search queued, holding until it is done", Entity.EntityId);
        }

        /// <summary>
        /// Before the task step (RunTasks), or outside a job every 10 ticks: false while the drone still waits for its
        /// route search; true when it goes on (the route was planned - legs queued -, or there is none: routeFailed).
        /// </summary>
        private bool ServicePendingRoute()
        {
            if (!routePending) return true;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (activeFlightOrder != null) { CancelPendingRoute(); return true; }   // something else is flying it now
            taskProgressFrame = now;   // the stall clock waits too
            var pm = PathfindingManager.Instance;
            var queue = pm != null ? pm.Searches : null;
            bool news = queue != null && queue.TakeNews(Entity.EntityId);
            if (now - routePendingSince >= PENDING_MAX_TICKS)
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: route search: no answer in {1} s: no route", Entity.EntityId, PENDING_MAX_TICKS / 60);
                CancelPendingRoute();
                PendingRouteFailed();
                return true;
            }
            // Still queued or running: nothing to plan yet (the planner would only find it pending again)
            if (!news && queue != null && queue.Waiting(Entity.EntityId)) return false;
            routePending = false;   // (the plan sets it again when another search is needed)
            bool docking = routeOwner == RouteOwner.Docking;
            bool wasRecover = recoverPlanning, wasReplanning = replanning;
            recoverPlanning = pendRecover;
            replanning = pendReplanning;
            bool ok;
            try { ok = ReplanCore(); }
            finally { recoverPlanning = wasRecover; replanning = wasReplanning; }
            if (routePending) return false;
            pendingDock = null;
            if (!ok)
            {
                if (Log.DebugEnabled) Log.Debug("Drone {0}: route search done: no route", Entity.EntityId);
                PendingRouteFailed();
            }
            else if (docking && constructionOrder == null && cPhase == ConstructionPhase.Running && currentJob != null)
                constructionOrder = activeFlightOrder;   // what the task step watches (set when the docking returned at once)
            return true;
        }

        // No route after the wait: the task fails (Unreachable); outside a job the drone stops where it is
        private void PendingRouteFailed()
        {
            routeFailed = true;
            if (cPhase == ConstructionPhase.Running && currentJob != null) return;
            routeOwner = RouteOwner.None;
            ClearFlightOrder();
            if (currentState != State.Docked && preflightStage == 0) SetState(State.Standby);
        }

        /// <summary>UpdateBeforeSimulation10: a wait outside a job (going home, a replan, a docking); in a job the task runner does it.</summary>
        private void UpdatePendingRoute10()
        {
            if (!routePending || (cPhase == ConstructionPhase.Running && currentJob != null)) return;
            ServicePendingRoute();
        }

        /// <summary>The wait for a route search is over (a new order, a job ended): its answers are dropped.</summary>
        private void CancelPendingRoute()
        {
            pendingDock = null;
            if (!routePending) return;
            routePending = false;
            var pm = PathfindingManager.Instance;
            if (pm != null && pm.Searches != null) pm.Searches.EndPlan(Entity.EntityId);
        }
    }
}
