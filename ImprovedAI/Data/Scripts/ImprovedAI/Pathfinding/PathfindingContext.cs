using System.Collections.Generic;
using VRage.Game;
using VRageMath;

namespace ImprovedAI.Pathfinding
{
    /// <summary>
    /// Pathfinding context containing only primitive data needed for pathfinding.
    /// No references to blocks or in-game classes - all data is primitives, Vector3D, or MatrixD.
    /// </summary>
    public struct PathfindingContext
    {
        // Sensor information
        public struct SensorData
        {
            public Vector3D RelativePosition; // Position relative to controller
            public float MaxRange;
        }

        // Camera information
        public struct CameraData
        {
            public Vector3D RelativePosition; // Position relative to controller
            public Base6Directions.Direction Direction; // Direction relative to controller forward
        }

        // Configuration primitives (derived from PathfindingConfig)
        public float MinWaypointDistance;
        public float MaxWaypointDistance;
        public float MinAltitudeBuffer;
        public int MaxPathNodes;
        public int MaxRepositionAttempts;
        /// <summary>
        /// Set by DirectPathfinder when the last successful waypoint required obstacle reposition (not NeedRaycast).
        /// PathfindingManager merges this into its per-target complexity budget.
        /// </summary>
        public int RepositionComplexityIncrement;
        public bool RequireSensorsForPathfinding;
        public bool RequireCamerasForPathfinding;
        public bool UsePlanetAwarePathfinding;
        public bool AllowRepathing;

        // Controller state
        public Vector3D ControllerPosition;
        public MatrixD ControllerWorldMatrix;
        public Base6Directions.Direction ControllerForwardDirection;

        // Environmental data
        public Vector3D GravityVector;
        public bool IsInPlanetGravity;
        public Vector3D? PlanetCenter;
        public double PlanetRadius;

        // Ship capabilities
        public float ShipMass;
        public float MaxLoad;
        public ThrustData ThrustData; // Struct with thrust values per direction

        // Current waypoint distance (can be adjusted during pathfinding)
        public float WaypointDistance;

        // Sensor and camera data
        public List<SensorData> Sensors;
        public List<CameraData> Cameras;

        // A* pathfinding working sets (reused across calls)
        public Dictionary<Vector3I, AStarNode> OpenSet;
        public Dictionary<Vector3I, AStarNode> ClosedSet;
        public FastPriorityQueue<AStarNode> OpenQueue;
        public int MaxAStarNodes;
        public int NodePoolIndex;

        // Obstacle detection
        public struct ObstacleData
        {
            public Vector3D Position;
            public BoundingBoxD BoundingBox;
        }

        public List<ObstacleData> KnownObstacles;
        public HashSet<Vector3D> RaycastCache; // Directions already checked

        // Shared working buffers (reused across pathfinding calls)
        public List<Vector3D> PathBuffer;
        public List<Vector3I> NeighborBuffer;
        public List<Vector3D> TraveledNodes;

        // NEW: Waypoint tracking and lookahead
        public WaypointHistory WaypointTracking;

        /// <summary>
        /// Waypoint history tracking for behavior calculation
        /// </summary>
        public struct WaypointHistory
        {
            public Vector3D? PreviousWaypoint;
            public Vector3D? CurrentWaypoint;
            public Vector3D? NextWaypoint;
            public WaypointBehavior CurrentBehavior;
            public float CurrentAlignmentAngle;
            public bool NextWaypointRequested;
            public float DistanceToCurrentWaypoint;

            /// <summary>
            /// Check if we have enough waypoint data for behavior calculation
            /// </summary>
            public bool CanCalculateBehavior()
            {
                return PreviousWaypoint.HasValue &&
                       CurrentWaypoint.HasValue &&
                       NextWaypoint.HasValue;
            }

            /// <summary>
            /// Advance waypoint history (current becomes previous, next becomes current)
            /// </summary>
            public void AdvanceWaypoint()
            {
                PreviousWaypoint = CurrentWaypoint;
                CurrentWaypoint = NextWaypoint;
                NextWaypoint = null;
                NextWaypointRequested = false;
                CurrentBehavior = WaypointBehavior.RunThrough; // Default until recalculated
                CurrentAlignmentAngle = 0f;
            }

            /// <summary>
            /// Reset all waypoint tracking
            /// </summary>
            public void Reset()
            {
                PreviousWaypoint = null;
                CurrentWaypoint = null;
                NextWaypoint = null;
                CurrentBehavior = WaypointBehavior.RunThrough;
                CurrentAlignmentAngle = 0f;
                NextWaypointRequested = false;
                DistanceToCurrentWaypoint = 0f;
            }
        }

        /// <summary>
        /// Get surface altitude above planet if in gravity
        /// </summary>
        public double? GetSurfaceAltitude()
        {
            if (!IsInPlanetGravity || !PlanetCenter.HasValue)
                return null;

            double distanceFromCenter = Vector3D.Distance(ControllerPosition, PlanetCenter.Value);
            return distanceFromCenter - PlanetRadius;
        }

        /// <summary>
        /// Check if a position is below safe altitude
        /// </summary>
        public bool IsBelowSafeAltitude(ref Vector3D position)
        {
            if (!IsInPlanetGravity || !PlanetCenter.HasValue)
                return false;

            double distanceFromCenter = Vector3D.Distance(position, PlanetCenter.Value);
            double altitude = distanceFromCenter - PlanetRadius;
            return altitude < MinAltitudeBuffer;
        }

        /// <summary>
        /// Get cameras for a specific direction
        /// </summary>
        public void GetCamerasForDirection(Base6Directions.Direction direction, List<CameraData> output)
        {
            output.Clear();
            if (Cameras == null) return;

            for (int i = 0; i < Cameras.Count; i++)
            {
                if (Cameras[i].Direction == direction)
                    output.Add(Cameras[i]);
            }
        }

        /// <summary>
        /// Check if sensors are available
        /// </summary>
        public bool HasSensors()
        {
            return Sensors != null && Sensors.Count > 0;
        }

        /// <summary>
        /// Check if cameras are available
        /// </summary>
        public bool HasCameras()
        {
            return Cameras != null && Cameras.Count > 0;
        }
    }
}