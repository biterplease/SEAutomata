using ImprovedAI.Config;
using ImprovedAI.Util.Logging;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace ImprovedAI.Pathfinding
{
    public struct WaypointInfo
    {
        public Vector3D Position;
        public WaypointBehavior Behavior;
        public float SuggestedSpeed;
        public float LookaheadDistance; // How far ahead to request next waypoint
    }

    public enum WaypointBehavior :byte
    {
        RunThrough,    // Green - maintain speed
        SlowApproach,  // Yellow - reduce to approach speed
        FullStop       // Red - stop briefly, then continue
    }
    public class WaypointResponse
    {
        public Vector3D Position;
        public Vector3D? ApproximateNextPosition; // Estimated, even if not fully calculated
        public WaypointBehavior SuggestedBehavior;
        public float AlignmentAngle; // Degrees between prev->current->next
        public bool IsLastWaypoint;
    }
    /// <summary>
    /// Manages pathfinding operations with incremental component updates.
    /// Handles context building, caching, and obstacle avoidance.
    /// </summary>
    public class PathfindingManager
    {
        public enum Method
        {
            None = 0,
            DirectPathfinding = 1,
            AStar = 2
        }

        #region Component State Tracking

        private struct ThrusterState
        {
            public Base6Directions.Direction Direction;
            public float MaxThrust;
            public bool IsWorking;
            public bool IsFunctional;
        }

        private struct SensorState
        {
            public Vector3D Position;
            public float MaxRange;
            public bool IsFunctional;
        }

        private struct CameraState
        {
            public Vector3D Position;
            public Base6Directions.Direction Direction;
            public bool IsFunctional;
        }

        private struct ControllerState
        {
            public Vector3D Position;
            public MatrixD WorldMatrix;
            public Vector3D GravityVector;
            public bool IsWorking;
            public bool IsFunctional;
        }

        #endregion

        // Configuration and dependencies
        private readonly IPathfindingConfig config;
        private readonly IMyGamePruningStructureDelegate pruningStructure;
        private readonly IMyPlanetDelegate planetDelegate;

        // Component caches (keyed by EntityId)
        private readonly Dictionary<long, ThrusterState> thrusterCache;
        private readonly Dictionary<long, SensorState> sensorCache;
        private readonly Dictionary<long, CameraState> cameraCache;
        private ControllerState controllerState;
        private long controllerEntityId;
        private IMyCubeGrid cubeGrid;
        private float shipMass;

        // Pathfinding context (primitive data only)
        private PathfindingContext context;

        // Target and waypoint tracking
        private Vector3D targetPosition;
        private bool hasTarget;
        private Vector3D? currentWaypoint;
        private bool needsRecalculation;

        /// <summary>
        /// Accumulates cost for NeedRaycast and obstacle reposition across GetNextWaypoint calls for the current target.
        /// </summary>
        private int complexityBudget;

        /// <summary>
        /// True when the last GetNextWaypoint failed because <see cref="complexityBudget"/> exceeded
        /// <c>MaxRepositionAttempts * 2</c> (TaskAborted handshake should be sent by the drone).
        /// </summary>
        public bool LastGetNextWaypointFailedDueToComplexityBudget { get; private set; }

        // Cached nodes
        private readonly List<Vector3D> cachedNodes;

        // Reusable buffers
        private readonly List<PathfindingContext.CameraData> cameraBuffer;
        private readonly List<MyLineSegmentOverlapResult<MyEntity>> raycastBuffer;
        private readonly List<MyEntity> obstacleBuffer;

        // Obstacle detection state
        private Vector3D lastObstacleCheckPosition;
        private int framesSinceLastObstacleCheck;
        private const int OBSTACLE_CHECK_INTERVAL = 10; // frames
        /// <summary>
        /// Lookahead distance for requesting next waypoint
        /// </summary>
        private const float WAYPOINT_LOOKAHEAD_DISTANCE = 200f;

        /// <summary>
        /// Alignment angle thresholds for waypoint behavior
        /// </summary>
        private const float ALIGNMENT_RUNTHROUGH_THRESHOLD = 15.0f;
        private const float ALIGNMENT_SLOWAPPROACH_THRESHOLD = 45.0f;

        // Disposed flag
        private bool isDisposed;

        public PathfindingManager(
            IPathfindingConfig pathfindingConfig,
            IMyGamePruningStructureDelegate pruningStructure = null,
            IMyPlanetDelegate planetDelegate = null)
        {
            config = pathfindingConfig ?? IAISession.Instance?.GetConfig()?.Pathfinding;
            this.pruningStructure = pruningStructure ?? new MyGamePruningStructureDelegate();
            this.planetDelegate = planetDelegate ?? new MyPlanetDelegate();

            thrusterCache = new Dictionary<long, ThrusterState>();
            sensorCache = new Dictionary<long, SensorState>();
            cameraCache = new Dictionary<long, CameraState>();
            cachedNodes = new List<Vector3D>();
            cameraBuffer = new List<PathfindingContext.CameraData>();
            raycastBuffer = new List<MyLineSegmentOverlapResult<MyEntity>>();
            obstacleBuffer = new List<MyEntity>();

            hasTarget = false;
            needsRecalculation = false;
            isDisposed = false;

            InitializeContext();

            if (config == null)
            {
                Log.Error("PathfindingManager: Configuration not provided");
            }
        }

        #region Component Update Methods

        /// <summary>
        /// Update controller and rebuild controller-dependent context data
        /// </summary>
        public void ControllerChanged(IMyShipController controller)
        {
            if (isDisposed) return;

            if (controller == null)
            {
                Log.Warning("PathfindingManager: Controller set to null");
                controllerEntityId = 0;
                needsRecalculation = true;
                return;
            }

            long entityId = controller.EntityId;
            ControllerState newState = new ControllerState
            {
                Position = controller.GetPosition(),
                WorldMatrix = controller.WorldMatrix,
                GravityVector = controller.GetNaturalGravity(),
                IsWorking = controller.IsWorking,
                IsFunctional = controller.IsFunctional
            };

            // Check if state actually changed
            if (entityId == controllerEntityId &&
                StateEquals(ref controllerState, ref newState))
                return;

            controllerEntityId = entityId;
            controllerState = newState;
            cubeGrid = controller.CubeGrid;
            shipMass = cubeGrid?.Physics?.Mass ?? 1000f;

            RebuildControllerContext();
            RebuildEnvironmentContext();
            needsRecalculation = true;
        }

        /// <summary>
        /// Update thrusters and rebuild thrust context data
        /// </summary>
        public void ThrustersChanged(List<IMyThrust> thrusters)
        {
            if (isDisposed) return;

            if (thrusters == null)
            {
                if (thrusterCache.Count > 0)
                {
                    thrusterCache.Clear();
                    RebuildThrustContext();
                    needsRecalculation = true;
                }
                return;
            }

            HashSet<long> currentIds = new HashSet<long>();
            bool hasChanges = false;

            // Update/add thrusters
            foreach (var thruster in thrusters)
            {
                if (thruster == null) continue;

                long entityId = thruster.EntityId;
                currentIds.Add(entityId);

                ThrusterState newState = new ThrusterState
                {
                    Direction = Base6Directions.GetDirection(thruster.GridThrustDirection),
                    MaxThrust = thruster.MaxEffectiveThrust,
                    IsWorking = thruster.IsWorking,
                    IsFunctional = thruster.IsFunctional
                };

                ThrusterState oldState;
                bool isNew = !thrusterCache.TryGetValue(entityId, out oldState);

                if (isNew || !StateEquals(ref oldState, ref newState))
                {
                    thrusterCache[entityId] = newState;
                    hasChanges = true;
                }
            }

            // Remove deleted thrusters
            List<long> toRemove = new List<long>();
            foreach (var kvp in thrusterCache)
            {
                if (!currentIds.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                    hasChanges = true;
                }
            }
            foreach (var id in toRemove)
                thrusterCache.Remove(id);

            if (hasChanges)
            {
                RebuildThrustContext();
                needsRecalculation = true;
            }
        }

        /// <summary>
        /// Update sensors and rebuild sensor context data
        /// </summary>
        public void SensorsChanged(List<IMySensorBlock> sensors)
        {
            if (isDisposed) return;

            if (sensors == null)
            {
                if (sensorCache.Count > 0)
                {
                    sensorCache.Clear();
                    RebuildSensorContext();
                    needsRecalculation = true;
                }
                return;
            }

            HashSet<long> currentIds = new HashSet<long>();
            bool hasChanges = false;

            // Update/add sensors
            foreach (var sensor in sensors)
            {
                if (sensor == null) continue;

                long entityId = sensor.EntityId;
                currentIds.Add(entityId);

                SensorState newState = new SensorState
                {
                    Position = sensor.GetPosition(),
                    MaxRange = sensor.MaxRange,
                    IsFunctional = sensor.IsFunctional
                };

                SensorState oldState;
                bool isNew = !sensorCache.TryGetValue(entityId, out oldState);

                if (isNew || !StateEquals(ref oldState, ref newState))
                {
                    sensorCache[entityId] = newState;
                    hasChanges = true;
                }
            }

            // Remove deleted sensors
            List<long> toRemove = new List<long>();
            foreach (var kvp in sensorCache)
            {
                if (!currentIds.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                    hasChanges = true;
                }
            }
            foreach (var id in toRemove)
                sensorCache.Remove(id);

            if (hasChanges)
            {
                RebuildSensorContext();
                needsRecalculation = true;
            }
        }

        /// <summary>
        /// Update cameras and rebuild camera context data
        /// </summary>
        public void CamerasChanged(List<IMyCameraBlock> cameras)
        {
            if (isDisposed) return;

            if (cameras == null)
            {
                if (cameraCache.Count > 0)
                {
                    cameraCache.Clear();
                    RebuildCameraContext();
                    needsRecalculation = true;
                }
                return;
            }

            HashSet<long> currentIds = new HashSet<long>();
            bool hasChanges = false;

            // Update/add cameras
            foreach (var camera in cameras)
            {
                if (camera == null) continue;

                long entityId = camera.EntityId;
                currentIds.Add(entityId);

                CameraState newState = new CameraState
                {
                    Position = camera.GetPosition(),
                    Direction = GetCameraDirection(camera),
                    IsFunctional = camera.IsFunctional
                };

                CameraState oldState;
                bool isNew = !cameraCache.TryGetValue(entityId, out oldState);

                if (isNew || !StateEquals(ref oldState, ref newState))
                {
                    cameraCache[entityId] = newState;
                    hasChanges = true;
                }
            }

            // Remove deleted cameras
            List<long> toRemove = new List<long>();
            foreach (var kvp in cameraCache)
            {
                if (!currentIds.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                    hasChanges = true;
                }
            }
            foreach (var id in toRemove)
                cameraCache.Remove(id);

            if (hasChanges)
            {
                RebuildCameraContext();
                needsRecalculation = true;
            }
        }

        /// <summary>
        /// Update grid mass
        /// </summary>
        public void GridChanged(IMyCubeGrid grid)
        {
            if (isDisposed) return;

            cubeGrid = grid;
            float newMass = grid?.Physics?.Mass ?? 1000f;

            if (Math.Abs(shipMass - newMass) > 0.1f)
            {
                shipMass = newMass;
                context.ShipMass = shipMass;
                needsRecalculation = true;
            }
        }

        #endregion

        #region Pathfinding Interface

        /// <summary>
        /// Set the final target destination
        /// </summary>
        public void SetTarget(ref Vector3D target)
        {
            if (isDisposed) return;

            // Check if target changed significantly
            if (hasTarget && Vector3D.DistanceSquared(targetPosition, target) < 1.0)
                return;

            targetPosition = target;
            hasTarget = true;
            currentWaypoint = null;
            needsRecalculation = false;
            complexityBudget = 0;
            LastGetNextWaypointFailedDueToComplexityBudget = false;

            // Reset waypoint tracking for new path
            context.WaypointTracking.Reset();
        }

        /// <summary>
        /// Get the next waypoint (uses stored target)
        /// </summary>
        public PathfindingResult GetNextWaypoint(ref Vector3D currentPosition, out Vector3D waypoint)
        {
            waypoint = default(Vector3D);
            LastGetNextWaypointFailedDueToComplexityBudget = false;

            if (isDisposed)
            {
                Log.Error("PathfindingManager: Manager is disposed");
                return PathfindingResult.Failed;
            }

            if (!hasTarget)
            {
                Log.Error("PathfindingManager: No target set");
                return PathfindingResult.Failed;
            }

            if (config == null)
            {
                Log.Error("PathfindingManager: Configuration not loaded");
                return PathfindingResult.Failed;
            }

            int budgetLimit = Math.Max(1, context.MaxRepositionAttempts) * 2;

            // Update current position
            context.ControllerPosition = currentPosition;

            // Scan sensors for obstacles (single combined scan)
            ScanSensorsForObstacles();

            // Initialize raycast cache if needed
            if (context.RaycastCache == null)
                context.RaycastCache = new HashSet<Vector3D>();

            // Select pathfinder method
            Method method = SelectPathfindingMethod(ref context, ref currentPosition, ref targetPosition);

            // Iterator loop - max 3 attempts
            const int maxIterations = 3;
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                PathfindingResult result;
                PathfindingRequest request;

                switch (method)
                {
                    case Method.DirectPathfinding:
                        result = DirectPathfinder.GetNextWaypoint(
                            ref context, ref currentPosition, ref targetPosition,
                            out waypoint, out request);
                        break;

                    case Method.AStar:
                        result = AStarPathfinder.GetNextWaypoint(
                            ref context, ref currentPosition, ref targetPosition,
                            out waypoint, out request);
                        break;

                    default:
                        Log.Error("PathfindingManager: No pathfinder available");
                        waypoint = targetPosition;
                        return PathfindingResult.Failed;
                }

                if (result == PathfindingResult.NeedRaycast)
                    complexityBudget++;
                else if (result == PathfindingResult.Success)
                    complexityBudget += context.RepositionComplexityIncrement;

                if (complexityBudget > budgetLimit)
                {
                    LastGetNextWaypointFailedDueToComplexityBudget = true;
                    Log.Warning(
                        "PathfindingManager: Complexity budget exceeded ({0} > {1}), aborting pathfinding",
                        complexityBudget, budgetLimit);
                    return PathfindingResult.Failed;
                }

                if (result == PathfindingResult.Success)
                {
                    currentWaypoint = waypoint;
                    needsRecalculation = false;
                    CacheNode(waypoint);
                    return PathfindingResult.Success;
                }

                if (result == PathfindingResult.NeedRaycast)
                {
                    // Perform raycast and update context
                    if (PerformRaycast(ref request))
                    {
                        // Raycast completed, loop again
                        continue;
                    }
                    else
                    {
                        // Raycast failed
                        Log.Warning("PathfindingManager: Raycast failed");
                        return PathfindingResult.Failed;
                    }
                }

                // Failed
                Log.Warning("PathfindingManager: Pathfinding failed on iteration {0}", iteration);
                return PathfindingResult.Failed;
            }

            Log.Error("PathfindingManager: Max iterations reached");
            return PathfindingResult.Failed;
        }

        /// <summary>
        /// Check if current path to waypoint is clear (for mid-flight checks)
        /// </summary>
        public bool IsPathClear(ref Vector3D currentPosition, ref Vector3D waypointPosition)
        {
            if (isDisposed) return false;

            return !DetectObstacleOnPath(ref currentPosition, ref waypointPosition);
        }

        /// <summary>
        /// Check if waypoint needs recalculation due to component changes
        /// </summary>
        public bool ShouldRecalculateWaypoint()
        {
            if (isDisposed) return false;
            return needsRecalculation;
        }

        /// <summary>
        /// Generate complete path (expensive - for debugging/planning only)
        /// </summary>
        public bool GenerateCompletePath(ref Vector3D start, ref Vector3D end, List<Vector3D> pathOutput)
        {
            if (pathOutput == null)
                throw new ArgumentNullException("pathOutput");

            if (isDisposed)
            {
                Log.Error("PathfindingManager: Manager is disposed");
                return false;
            }

            if (config == null)
            {
                Log.Error("PathfindingManager: Configuration not loaded");
                return false;
            }

            context.ControllerPosition = start;
            Method method = SelectPathfindingMethod(ref context, ref start, ref end);

            bool success = false;
            switch (method)
            {
                case Method.DirectPathfinding:
                    success = DirectPathfinder.CalculatePath(ref context, ref start, ref end, pathOutput);
                    break;

                case Method.AStar:
                    success = AStarPathfinder.CalculatePath(ref context, ref start, ref end, pathOutput);
                    break;

                default:
                    Log.Error("PathfindingManager: No pathfinder available");
                    pathOutput.Clear();
                    pathOutput.Add(start);
                    pathOutput.Add(end);
                    return false;
            }

            if (success)
            {
                for (int i = 0; i < pathOutput.Count; i++)
                    CacheNode(pathOutput[i]);
            }

            return success;
        }

        /// <summary>
        /// Get estimated complexity for a path
        /// </summary>
        public double GetPathComplexity(ref Vector3D start, ref Vector3D end)
        {
            if (isDisposed) return double.MaxValue;

            Method method = SelectPathfindingMethod(ref context, ref start, ref end);

            switch (method)
            {
                case Method.DirectPathfinding:
                    return DirectPathfinder.CalculatePathComplexity(ref context, ref start, ref end);

                case Method.AStar:
                    return AStarPathfinder.CalculatePathComplexity(ref context, ref start, ref end);

                default:
                    return double.MaxValue;
            }
        }

        /// <summary>
        /// Clear cached nodes and reset waypoint
        /// </summary>
        public void ClearCache()
        {
            if (isDisposed) return;

            cachedNodes.Clear();
            currentWaypoint = null;
            needsRecalculation = false;
            complexityBudget = 0;
            LastGetNextWaypointFailedDueToComplexityBudget = false;

            // Reset waypoint tracking
            context.WaypointTracking.Reset();
        }

        /// <summary>
        /// Close and cleanup manager resources
        /// </summary>
        public void Close()
        {
            if (isDisposed) return;

            // Clear all caches
            thrusterCache.Clear();
            sensorCache.Clear();
            cameraCache.Clear();
            cachedNodes.Clear();
            cameraBuffer.Clear();
            raycastBuffer.Clear();
            obstacleBuffer.Clear();

            // Clear context buffers
            if (context.Sensors != null)
                context.Sensors.Clear();
            if (context.Cameras != null)
                context.Cameras.Clear();
            if (context.TraveledNodes != null)
                context.TraveledNodes.Clear();
            if (context.PathBuffer != null)
                context.PathBuffer.Clear();
            if (context.NeighborBuffer != null)
                context.NeighborBuffer.Clear();
            if (context.OpenSet != null)
                context.OpenSet.Clear();
            if (context.ClosedSet != null)
                context.ClosedSet.Clear();
            if (context.OpenQueue != null)
                context.OpenQueue.Clear();

            // Reset state
            controllerEntityId = 0;
            cubeGrid = null;
            hasTarget = false;
            currentWaypoint = null;
            needsRecalculation = false;
            complexityBudget = 0;
            LastGetNextWaypointFailedDueToComplexityBudget = false;

            isDisposed = true;
            Log.Verbose("PathfindingManager: Closed and cleaned up");
        }

        #endregion

        #region Obstacle Detection

        /// <summary>
        /// Scan all sensors with a single combined bounding box
        /// </summary>
        private void ScanSensorsForObstacles()
        {
            if (context.KnownObstacles == null)
                context.KnownObstacles = new List<PathfindingContext.ObstacleData>();

            context.KnownObstacles.Clear();

            if (sensorCache.Count == 0)
                return;

            // Calculate combined bounding box
            Vector3D sensorCenter = Vector3D.Zero;
            int functionalCount = 0;

            foreach (var sensor in sensorCache.Values)
            {
                if (!sensor.IsFunctional) continue;
                sensorCenter += sensor.Position;
                functionalCount++;
            }

            if (functionalCount == 0)
                return;

            sensorCenter /= functionalCount;

            // Find maximum radius
            double maxRadius = 0;
            foreach (var sensor in sensorCache.Values)
            {
                if (!sensor.IsFunctional) continue;
                double distance = Vector3D.Distance(sensorCenter, sensor.Position);
                double totalReach = distance + sensor.MaxRange;
                maxRadius = Math.Max(maxRadius, totalReach);
            }

            // Create combined bounding box
            BoundingBoxD scanBox = new BoundingBoxD(
                sensorCenter - new Vector3D(maxRadius),
                sensorCenter + new Vector3D(maxRadius)
            );

            // Single scan
            obstacleBuffer.Clear();
            pruningStructure.GetTopMostEntitiesInBox(ref scanBox, obstacleBuffer, MyEntityQueryType.Both);

            // Filter and store obstacles
            foreach (var entity in obstacleBuffer)
            {
                if (entity == null) continue;

                // Skip own grid
                IMyCubeGrid grid = entity as IMyCubeGrid;
                if (grid != null && grid.EntityId == cubeGrid?.EntityId)
                    continue;

                // Add obstacle
                context.KnownObstacles.Add(new PathfindingContext.ObstacleData
                {
                    Position = entity.PositionComp.GetPosition(),
                    BoundingBox = entity.PositionComp.WorldAABB
                });
            }

            Log.Verbose("PathfindingManager: Scanned sensors, found {0} obstacles", context.KnownObstacles.Count);
        }

        /// <summary>
        /// Perform raycast and update context
        /// </summary>
        private bool PerformRaycast(ref PathfindingRequest request)
        {
            Vector3D end = request.RaycastStart + request.RaycastDirection * request.RaycastDistance;
            LineD ray = new LineD(request.RaycastStart, end);

            raycastBuffer.Clear();
            pruningStructure.GetTopmostEntitiesOverlappingRay(ref ray, raycastBuffer);

            // Check for obstacles
            bool hasObstacle = false;
            foreach (var hit in raycastBuffer)
            {
                if (hit.Element == null) continue;

                // Skip own grid
                IMyCubeGrid grid = hit.Element as IMyCubeGrid;
                if (grid != null && grid.EntityId == cubeGrid?.EntityId)
                    continue;

                // Found obstacle
                hasObstacle = true;

                // Add to known obstacles if not already there
                bool alreadyKnown = false;
                for (int i = 0; i < context.KnownObstacles.Count; i++)
                {
                    if (Vector3D.DistanceSquared(context.KnownObstacles[i].Position, hit.Element.PositionComp.GetPosition()) < 1.0)
                    {
                        alreadyKnown = true;
                        break;
                    }
                }

                if (!alreadyKnown)
                {
                    context.KnownObstacles.Add(new PathfindingContext.ObstacleData
                    {
                        Position = hit.Element.PositionComp.GetPosition(),
                        BoundingBox = hit.Element.PositionComp.WorldAABB
                    });
                }
            }

            // Cache the raycast direction
            Vector3D normalizedDir = request.RaycastDirection;
            normalizedDir.Normalize();
            context.RaycastCache.Add(normalizedDir);

            return true;
        }

        /// <summary>
        /// Detect obstacles between current position and waypoint (for mid-flight checks)
        /// </summary>
        private bool DetectObstacleOnPath(ref Vector3D start, ref Vector3D end)
        {
            raycastBuffer.Clear();

            LineD ray = new LineD(start, end);
            pruningStructure.GetTopmostEntitiesOverlappingRay(ref ray, raycastBuffer, MyEntityQueryType.Both);

            for (int i = 0; i < raycastBuffer.Count; i++)
            {
                MyEntity entity = raycastBuffer[i].Element;
                if (entity == null) continue;

                IMyCubeGrid grid = entity as IMyCubeGrid;
                if (grid != null && grid.EntityId != cubeGrid?.EntityId)
                    return true;

                if (entity is MyVoxelBase)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Update waypoint distance tracking and check if lookahead is needed
        /// </summary>
        /// <param name="currentPosition">Current drone position</param>
        public void UpdateWaypointTracking(ref Vector3D currentPosition)
        {
            if (isDisposed) return;
            if (!context.WaypointTracking.CurrentWaypoint.HasValue) return;

            // Calculate distance to current waypoint
            Vector3D currentWaypoint = context.WaypointTracking.CurrentWaypoint.Value;
            Vector3D distanceVector;
            Vector3D.Subtract(ref currentWaypoint, ref currentPosition, out distanceVector);

            context.WaypointTracking.DistanceToCurrentWaypoint = (float)distanceVector.Length();

            // Check if we need to request next waypoint for lookahead
                if (!context.WaypointTracking.NextWaypointRequested &&
                context.WaypointTracking.DistanceToCurrentWaypoint <= WAYPOINT_LOOKAHEAD_DISTANCE)
            {
                // Request next waypoint
                Vector3D nextWaypoint;
                if (GetNextWaypoint(ref currentWaypoint, out nextWaypoint) == PathfindingResult.Success)
                {
                    context.WaypointTracking.NextWaypoint = nextWaypoint;
                    context.WaypointTracking.NextWaypointRequested = true;

                    // Calculate behavior if we have all three waypoints
                    if (context.WaypointTracking.CanCalculateBehavior())
                    {
                        CalculateWaypointBehavior(ref context);
                    }
                }
            }
        }

        /// <summary>
        /// Calculate waypoint behavior based on alignment of previous, current, and next waypoints
        /// </summary>
        /// <param name="context">Pathfinding context containing waypoint history</param>
        private void CalculateWaypointBehavior(ref PathfindingContext context)
        {
            if (!context.WaypointTracking.CanCalculateBehavior())
            {
                // Default to slow approach if we don't have all waypoints
                context.WaypointTracking.CurrentBehavior = WaypointBehavior.SlowApproach;
                context.WaypointTracking.CurrentAlignmentAngle = 90f; // Unknown
                return;
            }

            // Cache waypoint values
            Vector3D prevWaypoint = context.WaypointTracking.PreviousWaypoint.Value;
            Vector3D currentWaypoint = context.WaypointTracking.CurrentWaypoint.Value;
            Vector3D nextWaypoint = context.WaypointTracking.NextWaypoint.Value;

            // Calculate incoming vector (previous -> current)
            Vector3D incomingVector;
            Vector3D.Subtract(ref currentWaypoint, ref prevWaypoint, out incomingVector);

            // Normalize incoming vector
            double incomingLengthSq = incomingVector.LengthSquared();
            if (incomingLengthSq < 0.01)
            {
                // Waypoints too close, default to slow approach
                context.WaypointTracking.CurrentBehavior = WaypointBehavior.SlowApproach;
                context.WaypointTracking.CurrentAlignmentAngle = 0f;
                return;
            }

            double incomingLength = Math.Sqrt(incomingLengthSq);
            Vector3D incomingNormalized;
            Vector3D.Divide(ref incomingVector, incomingLength, out incomingNormalized);

            // Calculate outgoing vector (current -> next)
            Vector3D outgoingVector;
            Vector3D.Subtract(ref nextWaypoint, ref currentWaypoint, out outgoingVector);

            // Normalize outgoing vector
            double outgoingLengthSq = outgoingVector.LengthSquared();
            if (outgoingLengthSq < 0.01)
            {
                // Waypoints too close, default to slow approach
                context.WaypointTracking.CurrentBehavior = WaypointBehavior.SlowApproach;
                context.WaypointTracking.CurrentAlignmentAngle = 0f;
                return;
            }

            double outgoingLength = Math.Sqrt(outgoingLengthSq);
            Vector3D outgoingNormalized;
            Vector3D.Divide(ref outgoingVector, outgoingLength, out outgoingNormalized);

            // Calculate dot product for angle
            double dotProduct;
            Vector3D.Dot(ref incomingNormalized, ref outgoingNormalized, out dotProduct);

            // Clamp to valid range
            dotProduct = MathHelper.Clamp(dotProduct, -1.0, 1.0);

            // Calculate angle in radians, then convert to degrees
            double angleRadians = Math.Acos(dotProduct);
            double angleDegrees = angleRadians * (180.0 / Math.PI);

            context.WaypointTracking.CurrentAlignmentAngle = (float)angleDegrees;

            // Determine behavior based on angle
            if (angleDegrees <= ALIGNMENT_RUNTHROUGH_THRESHOLD)
            {
                // Nearly straight path - maintain speed
                context.WaypointTracking.CurrentBehavior = WaypointBehavior.RunThrough;
            }
            else if (angleDegrees <= ALIGNMENT_SLOWAPPROACH_THRESHOLD)
            {
                // Moderate turn - reduce to approach speed
                context.WaypointTracking.CurrentBehavior = WaypointBehavior.SlowApproach;
            }
            else
            {
                // Sharp turn - full stop at waypoint
                context.WaypointTracking.CurrentBehavior = WaypointBehavior.FullStop;
            }

            Log.Verbose("PathfindingManager: Waypoint behavior calculated - Angle: {0:F1}°, Behavior: {1}",
                angleDegrees, context.WaypointTracking.CurrentBehavior);
        }

        /// <summary>
        /// Advance to next waypoint (called when drone reaches current waypoint)
        /// </summary>
        public void AdvanceToNextWaypoint()
        {
            if (isDisposed) return;

            context.WaypointTracking.AdvanceWaypoint();

            // Recalculate behavior if we have the data
            if (context.WaypointTracking.CanCalculateBehavior())
            {
                CalculateWaypointBehavior(ref context);
            }
        }

        /// <summary>
        /// Get current waypoint response with behavior information
        /// </summary>
        public WaypointResponse GetWaypointResponse()
        {
            if (isDisposed || !context.WaypointTracking.CurrentWaypoint.HasValue)
                return null;

            return new WaypointResponse
            {
                Position = context.WaypointTracking.CurrentWaypoint.Value,
                ApproximateNextPosition = context.WaypointTracking.NextWaypoint,
                SuggestedBehavior = context.WaypointTracking.CurrentBehavior,
                AlignmentAngle = context.WaypointTracking.CurrentAlignmentAngle,
                IsLastWaypoint = !hasTarget ||
                    (Vector3D.DistanceSquared(context.WaypointTracking.CurrentWaypoint.Value, targetPosition) <
                     context.MinWaypointDistance * context.MinWaypointDistance)
            };
        }

        /// <summary>
        /// Reset waypoint tracking (call when starting new path)
        /// </summary>
        public void ResetWaypointTracking()
        {
            if (isDisposed) return;

            context.WaypointTracking.Reset();
        }

        /// <summary>
        /// Check if waypoint tracking needs update based on distance
        /// </summary>
        /// <param name="currentPosition">Current drone position</param>
        /// <returns>True if lookahead should be checked</returns>
        public bool ShouldUpdateWaypointTracking(ref Vector3D currentPosition)
        {
            if (isDisposed) return false;
            if (!context.WaypointTracking.CurrentWaypoint.HasValue) return false;

            Vector3D currentWaypoint = context.WaypointTracking.CurrentWaypoint.Value;
            double distanceSq = Vector3D.DistanceSquared(currentPosition, currentWaypoint);

            // Update if within lookahead distance
            return distanceSq <= (WAYPOINT_LOOKAHEAD_DISTANCE * WAYPOINT_LOOKAHEAD_DISTANCE);
        }


        #endregion

        #region Context Building

        private void InitializeContext()
        {
            context.MinWaypointDistance = config?.MinWaypointDistance() ?? 25f;
            context.MaxWaypointDistance = config?.MaxWaypointDistance() ?? 100f;
            context.MinAltitudeBuffer = config?.MinAltitudeBuffer() ?? 50f;
            context.MaxPathNodes = config?.MaxPathNodes() ?? 1000;
            context.MaxRepositionAttempts = config?.MaxRepositionAttempts() ?? 3;
            context.RequireSensorsForPathfinding = config?.RequireSensorsForPathfinding() ?? false;
            context.RequireCamerasForPathfinding = config?.RequireCamerasForPathfinding() ?? false;
            context.UsePlanetAwarePathfinding = config?.UsePlanetAwarePathfinding() ?? true;
            context.AllowRepathing = config?.AllowRepathing() ?? true;
            context.WaypointDistance = context.MaxWaypointDistance;

            // Initialize buffers
            context.TraveledNodes = new List<Vector3D>();
            context.KnownObstacles = new List<PathfindingContext.ObstacleData>();
            context.RaycastCache = new HashSet<Vector3D>();

            // Initialize waypoint tracking
            context.WaypointTracking = new PathfindingContext.WaypointHistory();
            context.WaypointTracking.Reset();
        }

        private void RebuildControllerContext()
        {
            if (controllerEntityId == 0) return; // No controller set

            context.ControllerPosition = controllerState.Position;
            context.ControllerWorldMatrix = controllerState.WorldMatrix;
            context.ControllerForwardDirection = Base6Directions.Direction.Forward;
        }

        private void RebuildEnvironmentContext()
        {
            if (controllerEntityId == 0) return; // No controller set

            context.GravityVector = controllerState.GravityVector;
            context.IsInPlanetGravity = context.GravityVector.LengthSquared() > 0.1;

            if (context.IsInPlanetGravity)
            {
                MyPlanet planet = planetDelegate.GetClosestPlanet(controllerState.Position);
                if (planet != null)
                {
                    context.PlanetCenter = planet.PositionComp.GetPosition();
                    context.PlanetRadius = planet.MinimumRadius;
                }
                else
                {
                    context.PlanetCenter = null;
                    context.PlanetRadius = 0;
                }
            }
            else
            {
                context.PlanetCenter = null;
                context.PlanetRadius = 0;
            }
        }

        private void RebuildThrustContext()
        {
            context.ThrustData = new ThrustData();

            foreach (var kvp in thrusterCache)
            {
                // Only count functional and working thrusters
                if (!kvp.Value.IsFunctional || !kvp.Value.IsWorking) continue;

                switch (kvp.Value.Direction)
                {
                    case Base6Directions.Direction.Forward:
                        context.ThrustData.Forward += kvp.Value.MaxThrust;
                        break;
                    case Base6Directions.Direction.Backward:
                        context.ThrustData.Backward += kvp.Value.MaxThrust;
                        break;
                    case Base6Directions.Direction.Up:
                        context.ThrustData.Up += kvp.Value.MaxThrust;
                        break;
                    case Base6Directions.Direction.Down:
                        context.ThrustData.Down += kvp.Value.MaxThrust;
                        break;
                    case Base6Directions.Direction.Left:
                        context.ThrustData.Left += kvp.Value.MaxThrust;
                        break;
                    case Base6Directions.Direction.Right:
                        context.ThrustData.Right += kvp.Value.MaxThrust;
                        break;
                }
            }

            context.ShipMass = shipMass;
            context.MaxLoad = CalculateMaxLoad();
        }

        private void RebuildSensorContext()
        {
            if (context.Sensors == null)
                context.Sensors = new List<PathfindingContext.SensorData>();

            context.Sensors.Clear();

            foreach (var kvp in sensorCache)
            {
                if (!kvp.Value.IsFunctional) continue;

                var sensorData = new PathfindingContext.SensorData
                {
                    RelativePosition = kvp.Value.Position - controllerState.Position,
                    MaxRange = kvp.Value.MaxRange
                };

                context.Sensors.Add(sensorData);
            }
        }

        private void RebuildCameraContext()
        {
            if (context.Cameras == null)
                context.Cameras = new List<PathfindingContext.CameraData>();

            context.Cameras.Clear();

            foreach (var kvp in cameraCache)
            {
                if (!kvp.Value.IsFunctional) continue;

                var cameraData = new PathfindingContext.CameraData
                {
                    RelativePosition = kvp.Value.Position - controllerState.Position,
                    Direction = kvp.Value.Direction
                };

                context.Cameras.Add(cameraData);
            }
        }

        #endregion

        #region Helper Methods

        private Method SelectPathfindingMethod(ref PathfindingContext context, ref Vector3D start, ref Vector3D end)
        {
            double distance = Vector3D.Distance(start, end);

            if (distance < context.MinWaypointDistance * 2)
                return Method.DirectPathfinding;

            if (config.AllowAStar())
            {
                bool hasSensors = !context.RequireSensorsForPathfinding || context.HasSensors();
                bool hasCameras = !context.RequireCamerasForPathfinding || context.HasCameras();

                if (hasSensors || hasCameras)
                {
                    double complexity = AStarPathfinder.CalculatePathComplexity(ref context, ref start, ref end);
                    if (complexity < context.MaxPathNodes)
                        return Method.AStar;
                }
            }

            if (config.AllowDirectPathfinding())
                return Method.DirectPathfinding;

            return Method.None;
        }

        private void CacheNode(Vector3D node)
        {
            if (!cachedNodes.Contains(node))
            {
                cachedNodes.Add(node);
                if (cachedNodes.Count > 1000)
                    cachedNodes.RemoveAt(0);
            }
        }

        private float CalculateMaxLoad()
        {
            // Simplified - would calculate from cargo capacity
            return 1.0f;
        }

        private Base6Directions.Direction GetCameraDirection(IMyCameraBlock camera)
        {
            // Simplified - would calculate actual direction relative to controller
            return Base6Directions.Direction.Forward;
        }

        #endregion

        #region State Comparison

        private bool StateEquals(ref ControllerState a, ref ControllerState b)
        {
            return a.IsWorking == b.IsWorking &&
                   a.IsFunctional == b.IsFunctional &&
                   Vector3D.DistanceSquared(a.Position, b.Position) < 0.01;
        }

        private bool StateEquals(ref ThrusterState a, ref ThrusterState b)
        {
            return a.Direction == b.Direction &&
                   Math.Abs(a.MaxThrust - b.MaxThrust) < 0.1f &&
                   a.IsWorking == b.IsWorking &&
                   a.IsFunctional == b.IsFunctional;
        }

        private bool StateEquals(ref SensorState a, ref SensorState b)
        {
            return Math.Abs(a.MaxRange - b.MaxRange) < 0.1f &&
                   a.IsFunctional == b.IsFunctional &&
                   Vector3D.DistanceSquared(a.Position, b.Position) < 0.01;
        }

        private bool StateEquals(ref CameraState a, ref CameraState b)
        {
            return a.Direction == b.Direction &&
                   a.IsFunctional == b.IsFunctional &&
                   Vector3D.DistanceSquared(a.Position, b.Position) < 0.01;
        }

        #endregion
    }
}