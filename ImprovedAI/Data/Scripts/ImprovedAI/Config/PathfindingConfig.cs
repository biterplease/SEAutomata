using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImprovedAI.Config
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
        /// Sensors are simulated by making the drone aware of its surroundings within 50m of the sensor.
        /// If this is true, and the ship has no sensor, it will not be able to detect obstacles.
        /// Disable if your server does not allow sensors (simulation still takes place).
        /// </summary>
        public bool requireSensorsForPathfinding { get; internal set; } = true;
        public bool RequireSensorsForPathfinding() => requireSensorsForPathfinding;
        /// <summary>
        /// Camera raycasting is simulated, with a range of 200m. Each drone requires 6 cameras
        /// (one in each direction) to be able to raycast. Note that the camera raycast is never invoked,
        /// a more efficient method is used.
        /// Disable if you don't want cameras on drones.
        /// </summary>
        public bool requireCamerasForPathfinding { get; internal set; } = true;
        public bool RequireCamerasForPathfinding() => requireCamerasForPathfinding;
        /// <summary>
        /// Max distance the camera raycast can go. It is adjusted for the length of the trip; the
        /// max value will only be used in long trips.
        /// </summary>
        public float maxSimulatedCameraRaycastMeters { get; internal set; } = 1000.0f;
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
    }
}
