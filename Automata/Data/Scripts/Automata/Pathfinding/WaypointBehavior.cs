namespace Automata.Pathfinding
{
    /// <summary>What a leg does at its waypoint when the next leg is known (FlightOrder.Behavior).</summary>
    public enum WaypointBehavior : byte
    {
        RunThrough,    // turn under 15°: keep the cruise speed through the waypoint
        SlowApproach,  // 15-45°: pass at approach speed
        FullStop       // over 45°, or next leg unknown: stop, then continue
    }
}
