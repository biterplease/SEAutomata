using ImprovedAI.Config;
using System;

namespace ImprovedAI.Tests.TestUtil
{
    public class FakePathfindingConfig : IPathfindingConfig
    {
        public bool requireSensorsForPathfinding { get; set; } = false;
        public bool RequireSensorsForPathfinding() => requireSensorsForPathfinding;
        public bool requireCamerasForPathfinding { get; set; } = false;
        public bool RequireCamerasForPathfinding() => requireCamerasForPathfinding;
        public float maxSimulatedCameraRaycastMeters { get; set; } = 1000.0f;
        public float MaxSimulatedCameraRaycastMeters() => maxSimulatedCameraRaycastMeters;
        public bool usePlanetAwarePathfinding { get; set; } = true;
        public bool UsePlanetAwarePathfinding() => usePlanetAwarePathfinding;
        public bool allowRepathing { get; set; } = true;
        public bool AllowRepathing() => allowRepathing;
        public bool allowDirectPathfinding { get; set; } = true;
        public bool AllowDirectPathfinding() => allowDirectPathfinding;
        public bool allowAStar { get; set; } = false;
        public bool AllowAStar() => allowAStar;
        public float minWaypointDistance { get; set; } = 12.5f;
        public float MinWaypointDistance() => minWaypointDistance;
        public float maxWaypointDistance { get; set; } = 500.0f;
        public float MaxWaypointDistance() => maxWaypointDistance;
        public int maxRepositionAttempts { get; set; } = 10;
        public int MaxRepositionAttempts() => maxRepositionAttempts;
        public float minAltitudeBuffer { get; set; } = 20.0f;
        public float MinAltitudeBuffer() => minAltitudeBuffer;

        /// <summary>
        /// Max number of waypoints created when pathfinding.
        /// </summary>
        public int maxPathNodes { get; set; } = 200;
        public int MaxPathNodes() => maxPathNodes;
        public TimeSpan maxPathfindingTime { get; set; } = TimeSpan.FromMilliseconds(50);
        public TimeSpan MaxPathfindingTime() => maxPathfindingTime;
        public float raycastCooldownSeconds { get; set; } = 1.0f;
        public float RaycastCooldownSeconds() => raycastCooldownSeconds;
    }
}
