namespace Automata.Pathfinding
{
    /// <summary>
    /// The room drones keep round their hulls below the route clearance - one set for every drone and behaviour
    /// (2026-11-20: welders, harvesters, janitors, miners, docking and the planner each had their own, 0.01 to 1 m).
    /// Above them: the server's [Pathfinding] ObstacleClearance (the route legs at full clearance, the clearance ladder
    /// halving it per failed plan down to MIN) and DockClearance / DockClearanceMin (dock corridors, defaults FINE / MIN).
    ///  - WORK: close work by the cells / voxels - a work site's way in, a bore's checks, room to turn in place, a
    ///    back-out's run.
    ///  - FINE: fine moves checked precisely (by real shapes or the real physics): a dock approach by the drone's real
    ///    shape, a custom route's recorded links, an escape spot held in its attitude, a welder snout's way in by its shape.
    ///  - MIN: the floor every lowered clearance stops at - the route ladder's last step (harvesters start there: rooms
    ///    built for people), the boxed-in search's escape, the exact confirms of a leg the voxels shut.
    ///  - TOUCH: a tool's last move onto its target, against the real physics only (a collector's beam reaches 2 m: its
    ///    origin centimetres off a plot); a custom route's link past a moving grid, checked by the hull alone.
    /// Less room costs no compute; more room fails more plans (each failure: a search, a lower clearance, a re-plan) -
    /// so these are the smallest that fly safely at each precision, the same for every drone.
    /// </summary>
    public static class Clearances
    {
        public const double WORK = 0.5;    // m
        public const double FINE = 0.25;   // m
        public const double MIN = 0.1;     // m
        public const double TOUCH = 0.03;  // m
    }
}
