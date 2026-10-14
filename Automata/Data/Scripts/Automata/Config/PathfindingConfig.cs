using System;

namespace Automata.Config
{
    public interface IPathfindingConfig
    {
        bool RequireSensorsForPathfinding();
        bool RequireCamerasForPathfinding();
        float MaxSimulatedCameraRaycastMeters();
        bool UsePlanetAwarePathfinding();
        bool AllowRepathing();
        bool AllowDirectPathfinding();
        bool AllowAStar();
        //bool AllowDStarLite();
        float MinAltitudeBuffer();
        float MinWaypointDistance();
        float MaxWaypointDistance();
        int MaxRepositionAttempts();
        int MaxPathNodes();
        TimeSpan MaxPathfindingTime();
        float RaycastCooldownSeconds();
    }
    public class PathfindingConfig : IPathfindingConfig
    {
        /// <summary>
        /// Sensors are simulated: a working sensor makes the drone aware of its surroundings within SimulatedSensorRange
        /// (100 m by default). If this is true and the ship has no sensor, it detects nothing close by except what its
        /// cameras see. Disable if your server does not allow sensors (the drone then senses all around as if it had one).
        /// </summary>
        public bool requireSensorsForPathfinding { get; internal set; } = true;
        public bool RequireSensorsForPathfinding() => requireSensorsForPathfinding;
        /// <summary>
        /// Camera raycasting is simulated, with a range of CameraRange (1500 m by default) over a 90° square cone
        /// along each working camera's facing (six cameras, one per side, cover every direction). The camera raycast
        /// is never invoked; a more efficient method is used. Disable if you don't want cameras on drones.
        /// </summary>
        public bool requireCamerasForPathfinding { get; internal set; } = true;
        public bool RequireCamerasForPathfinding() => requireCamerasForPathfinding;
        /// <summary>
        /// How far a simulated camera sees (ini: CameraRange).
        /// </summary>
        public float maxSimulatedCameraRaycastMeters { get; internal set; } = 1500.0f;
        public float MaxSimulatedCameraRaycastMeters() => maxSimulatedCameraRaycastMeters;
        /// <summary>
        /// This adds a small cost to pathfinding in planets, usually for longer trips. If enabled,
        /// a drone will travel an arc around the planet's center, to its target.
        /// If disabled, a drone may crash into the planet, by drawing a straight line to the target.
        /// </summary>
        public bool usePlanetAwarePathfinding { get; internal set; } = true;
        public bool UsePlanetAwarePathfinding() => usePlanetAwarePathfinding;
        /// <summary>
        /// This gives different behaviors to the different pathfinding methods.
        ///
        /// For Direct Pathfinding (default method), drones that encounter obstacles will move one or 
        /// more steps, until it either a) the obstacle is no longer in the direct line to the target,
        /// or b) MaxRepositionAttemtps is reached, at this point the drone will go into an error state.
        /// 
        /// For AStar Pathfinding, the drone will try to reroute to the target, may even retrace its steps
        /// to a known location, and attempt a different path.
        /// 
        /// If disabled, drones will simply go into error states and hover.
        /// </summary>
        public bool allowRepathing { get; internal set; } = true;
        public bool AllowRepathing() => allowRepathing;
        /// <summary>
        /// Basic direct pathfinding method. Its literally a direct line to target with no obstacle
        /// avoidance. Disabling this will render the drones unusable.
        /// </summary>
        public bool allowDirectPathfinding { get; internal set; } = true;
        public bool AllowDirectPathfinding() => allowDirectPathfinding;
        // A* is an advanced pathfinding method.
        public bool allowAStar { get; internal set; } = false;
        public bool AllowAStar() => allowAStar;
        //public bool allowDStarLite { get; internal set; } = false;
        //public bool AllowDStarLite() => allowDStarLite;
        /// <summary>
        /// Minimum distance between waypoint, in meters. If target is closer than this, a one-step move at ApproachSpeed
        /// is performed.
        /// </summary>
        public float minAltitudeBuffer { get; internal set; } = 50f;
        public float MinAltitudeBuffer() => minAltitudeBuffer;
        public float minWaypointDistance { get; internal set; } = 12.5f;
        public float MinWaypointDistance() => minWaypointDistance;
        /// <summary>
        /// Maximum distance between waypoints, in meters.
        /// </summary>
        public float maxWaypointDistance { get; internal set; } = 500.0f;
        public float MaxWaypointDistance() => maxWaypointDistance;
        public int maxRepositionAttempts { get; internal set; } = 10;
        public int MaxRepositionAttempts() => maxRepositionAttempts;

        /// <summary>
        /// Max number of waypoints created when pathfinding.
        /// </summary>
        public int maxPathNodes { get; internal set; } = 200;
        public int MaxPathNodes() => maxPathNodes;
        public TimeSpan maxPathfindingTime { get; internal set; } = TimeSpan.FromMilliseconds(50);
        public TimeSpan MaxPathfindingTime() => maxPathfindingTime;

        public float raycastCooldownSeconds { get; internal set; } = 1.0f;
        public float RaycastCooldownSeconds() => raycastCooldownSeconds;

        /// <summary>m all around a drone with a working sensor (or any drone, when sensors aren't required).</summary>
        public double SimulatedSensorRange { get; internal set; } = 100.0;
        /// <summary>Look-ahead rays per tick shared by all drones (5 per check).</summary>
        public int RaysPerTick { get; internal set; } = 60;
        /// <summary>m kept between the hull and obstacles by route checks (ray bundle margin, turn room).</summary>
        public double ObstacleClearance { get; internal set; } = 3.0;
        /// <summary>m: on planets, stretches longer than this follow the planet's curve...</summary>
        public double ArcMinDistance { get; internal set; } = 300;
        /// <summary>m: ...as straight chords at least this long (and see ArcSag).</summary>
        public double ArcStep { get; internal set; } = 200;
        /// <summary>m a chord of a planet arc may sag below its ends (sets the chord length: sqrt(8 r sag)); the chord
        /// ends are then lifted until the whole chord clears the terrain. Bigger: fewer, longer straight lines.</summary>
        public double ArcSag { get; internal set; } = 25;
        /// <summary>m kept between the hull and the hangar / dock walls in a dock's entry corridor (and when undocking
        /// through one). The rest of the route keeps ObstacleClearance.</summary>
        public double DockClearance { get; internal set; } = 0.25;
        /// <summary>m: the corridor is searched again with this clearance when none is found with DockClearance (tight doors);
        /// flown at half the corridor speed, the line held within half the clearance.</summary>
        public double DockClearanceMin { get; internal set; } = 0.1;
        /// <summary>m: how far from the connector the entry corridor is searched (to a point with room to turn).</summary>
        public double DockCorridorMaxLength { get; internal set; } = 40;
        /// <summary>m above the terrain long flights in gravity cruise at (routes longer than ArcMinDistance): take-off
        /// straight up to it, landing straight down from it.</summary>
        public double CruiseAltitude { get; internal set; } = 40;
        /// <summary>Shared memory of the links drones have flown (planner stage 2), saved with the world.</summary>
        public bool UsePathMemory { get; internal set; } = true;
        /// <summary>Most remembered points; the least recently used go first.</summary>
        public int PathMemoryMaxNodes { get; internal set; } = 4000;
        /// <summary>Hours a remembered link may go unused before it is dropped.</summary>
        public double PathMemoryMaxAgeHours { get; internal set; } = 24;
        /// <summary>Lazy Theta* search (planner stage 3) where the straight line and the memory find nothing.</summary>
        public bool AllowLazyTheta { get; internal set; } = true;
        /// <summary>m: the search covers about this far around the stretch that failed.</summary>
        public double ThetaSearchRadius { get; internal set; } = 300;
        /// <summary>Most cells one search expands.</summary>
        public int ThetaMaxExpansions { get; internal set; } = 3000;
        /// <summary>Most milliseconds one search may take.</summary>
        public double ThetaMaxMs { get; internal set; } = 15;
        /// <summary>Minutes a flight lane may go unused before it is dropped.</summary>
        public double FlightLaneExpirationMinutes { get; internal set; } = 240;
    }
}
