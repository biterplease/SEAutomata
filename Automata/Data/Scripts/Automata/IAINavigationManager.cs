namespace Automata.Navigation
{
    public class NavigationManager
    {
        //private readonly IAIDroneController controller;
        //private readonly PathfindingManager pathfindingManager;
        //private readonly IAIDroneControllerSettings config;

        //// Navigation state
        //private List<Vector3D> currentPath = new List<Vector3D>();
        //private int pathIndex = 0;
        //private Vector3D targetWaypoint;

        //public NavigationManager(IAIDroneController controller, PathfindingManager pathfindingManager, IAIDroneControllerSettings config)
        //{
        //    this.controller = controller;
        //    this.pathfindingManager = pathfindingManager;
        //    this.config = config;
        //}

        //public void NavigateToPosition(Vector3D targetPosition, double maxSpeed)
        //{
        //    Vector3D currentPos = controller.GetPosition();
        //    var distance = Vector3D.Distance(currentPos, targetPosition);

        //    // If we're close enough, use direct navigation
        //    if (distance < config.WaypointTolerance)
        //    {
        //        PerformDirectNavigation(targetPosition, maxSpeed);
        //        return;
        //    }

        //    // Check if we need to recalculate path
        //    if (ShouldRecalculatePath(targetPosition))
        //    {
        //        RecalculatePathToPosition(targetPosition);
        //    }

        //    // Follow the current path
        //    FollowCurrentPath(maxSpeed);
        //}

        //public void SetPath(List<Vector3D> newPath)
        //{
        //    currentPath = new List<Vector3D>(newPath);
        //    pathIndex = 0;
        //}

        //public void ClearPath()
        //{
        //    currentPath.Clear();
        //    pathIndex = 0;
        //}

        //public bool HasReachedCurrentWaypoint()
        //{
        //    if (currentPath.Count == 0 || pathIndex >= currentPath.Count)
        //        return true;

        //    var currentPos = controller.GetPosition();
        //    var distanceToWaypoint = Vector3D.Distance(currentPos, currentPath[pathIndex]);
        //    return distanceToWaypoint < config.WaypointTolerance;
        //}

        //public bool AdvanceWaypoint()
        //{
        //    if (pathIndex < currentPath.Count - 1)
        //    {
        //        pathIndex++;
        //        return true;
        //    }
        //    return false;
        //}

        //public bool IsPathComplete()
        //{
        //    return pathIndex >= currentPath.Count;
        //}

        //public int GetCurrentWaypointIndex()
        //{
        //    return pathIndex;
        //}

        //public int GetTotalWaypoints()
        //{
        //    return currentPath.Count;
        //}

        //private bool ShouldRecalculatePath(Vector3D finalTarget)
        //{
        //    // Recalculate if we don't have a path
        //    if (currentPath.Count == 0 || pathIndex >= currentPath.Count)
        //        return true;

        //    // Recalculate if we've drifted too far from the current waypoint
        //    var currentPos = controller.GetPosition();
        //    var currentWaypoint = currentPath[pathIndex];
        //    var distanceToWaypoint = Vector3D.Distance(currentPos, currentWaypoint);

        //    if (distanceToWaypoint > controller.BlockWaypointDistance * 2.0)
        //        return true;

        //    // Recalculate if sensors detect new obstacles
        //    if (HasNewObstaclesDetected())
        //        return true;

        //    // Recalculate if final target has changed significantly
        //    if (currentPath.Count > 0)
        //    {
        //        var pathEnd = currentPath.Last();
        //        var distanceToOriginalTarget = Vector3D.Distance(pathEnd, finalTarget);
        //        if (distanceToOriginalTarget > controller.BlockWaypointDistance)
        //            return true;
        //    }

        //    return false;
        //}

        //private void RecalculatePathToPosition(Vector3D targetPosition)
        //{
        //    var startPos = controller.GetPosition();

        //    var context = new PathfindingContext
        //    {
        //        Controller = controller, // This needs to be fixed
        //        Sensors = controller.GetSensors(),
        //        GravityVector = controller.GetNaturalGravity(),
        //        ThrustData = controller.GetThrustData(),
        //        ShipMass = controller.GetCurrentMass(),
        //        MaxThrust = controller.GetMaxThrust(),
        //        MaxLoad = controller.GetMaxLoad(),
        //        WaypointDistance = controller.BlockWaypointDistance,
        //        CubeGrid = controller.CubeGrid
        //    };

        //    currentPath = pathfindingManager.CalculatePath(startPos, targetPosition, context);
        //    pathIndex = 0;
        //}

        //private void FollowCurrentPath(double maxSpeed)
        //{
        //    if (currentPath.Count == 0 || pathIndex >= currentPath.Count)
        //        return;

        //    var currentPos = controller.GetPosition();
        //    var targetWaypoint = currentPath[pathIndex];

        //    // Navigate to current waypoint
        //    PerformDirectNavigation(targetWaypoint, maxSpeed);
        //}

        //private void PerformDirectNavigation(Vector3D targetPosition, double maxSpeed)
        //{
        //    var currentPos = controller.GetPosition();
        //    var direction = Vector3D.Normalize(targetPosition - currentPos);
        //    var distance = Vector3D.Distance(currentPos, targetPosition);

        //    // Calculate optimal approach speed
        //    var optimalSpeed = CalculateOptimalApproachSpeed(direction, distance, maxSpeed);
        //    var desiredVelocity = direction * optimalSpeed;

        //    // Get current velocity
        //    var currentVelocity = controller.CubeGrid.Physics.LinearVelocity;
        //    var velocityError = desiredVelocity - currentVelocity;

        //    // Apply thrust
        //    ApplyIntelligentThrust(velocityError, direction);
        //}

        //private double CalculateOptimalApproachSpeed(Vector3D direction, double distance, double maxSpeed)
        //{
        //    var gravity = controller.GetNaturalGravity();
        //    var gravityStrength = gravity.Length();

        //    if (gravityStrength > 0.1)
        //    {
        //        var context = new PathfindingContext
        //        {
        //            Controller = controller, // This needs to be fixed
        //            GravityVector = gravity,
        //            ThrustData = controller.GetThrustData(),
        //            ShipMass = controller.GetCurrentMass()
        //        };

        //        var maxClimbAngle = context.GetMaxSafeClimbAngle();
        //        var gravityUp = Vector3D.Normalize(-gravity);
        //        var climbAngle = Math.Acos(Math.Abs(Vector3D.Dot(direction, gravityUp))) * 180.0 / Math.PI;

        //        if (climbAngle > maxClimbAngle * 0.8)
        //        {
        //            var speedReduction = 1.0 - (climbAngle - maxClimbAngle * 0.8) / (maxClimbAngle * 0.2);
        //            maxSpeed *= Math.Max(0.3, speedReduction);
        //        }
        //    }

        //    // Deceleration control
        //    var brakingDistance = CalculateBrakingDistance(maxSpeed);
        //    if (distance < brakingDistance)
        //    {
        //        var speedFactor = distance / brakingDistance;
        //        maxSpeed *= Math.Max(0.1, speedFactor);
        //    }

        //    return Math.Min(maxSpeed, distance * 2.0);
        //}

        //private double CalculateBrakingDistance(double currentSpeed)
        //{
        //    var mass = controller.GetCurrentMass();
        //    var maxDeceleration = controller.GetThrustData().GetMaxThrust() / mass;
        //    return (currentSpeed * currentSpeed) / (2.0 * maxDeceleration);
        //}

        //private void ApplyIntelligentThrust(Vector3D velocityError, Vector3D direction)
        //{
        //    var localError = Vector3D.Transform(velocityError, MatrixD.Transpose(controller.WorldMatrix));
        //    var gravity = controller.GetNaturalGravity();
        //    var localGravity = Vector3D.Transform(gravity, MatrixD.Transpose(controller.WorldMatrix));
        //    var mass = controller.GetCurrentMass();

        //    foreach (var thruster in controller.GetThrusters())
        //    {
        //        Vector3I thrustDirectionInt = thruster.GridThrustDirection;
        //        Vector3D thrustDir = new Vector3D(thrustDirectionInt.X, thrustDirectionInt.Y, thrustDirectionInt.Z);
        //        var thrustAlignment = Vector3D.Dot(localError, -thrustDir);
        //        var gravityAlignment = Vector3D.Dot(localGravity, -thrustDir);

        //        var movementThrust = Math.Max(0, thrustAlignment) * mass;
        //        var gravityThrust = Math.Max(0, gravityAlignment) * mass;
        //        var totalRequiredThrust = movementThrust + gravityThrust;

        //        var thrustPercentage = MathHelper.Clamp(totalRequiredThrust / thruster.MaxEffectiveThrust, 0, 1);
        //        thruster.ThrustOverridePercentage = (float)thrustPercentage;
        //    }
        //}

        //public void OrientTowardsTarget(Vector3D targetPosition)
        //{
        //    var currentPos = controller.GetPosition();
        //    var desiredForward = Vector3D.Normalize(targetPosition - currentPos);

        //    var gravity = controller.GetNaturalGravity();
        //    if (gravity.LengthSquared() > 0.1 && config.AlignToPGravity)
        //    {
        //        ApplyGravityAlignedOrientation(desiredForward, gravity);
        //    }
        //    else
        //    {
        //        ApplyDirectOrientation(desiredForward);
        //    }
        //}

        //private void ApplyGravityAlignedOrientation(Vector3D desiredForward, Vector3D gravity)
        //{
        //    var gravityUp = Vector3D.Normalize(-gravity);
        //    var currentMatrix = controller.WorldMatrix;

        //    var horizontalForward = desiredForward - Vector3D.Dot(desiredForward, gravityUp) * gravityUp;
        //    if (horizontalForward.LengthSquared() < 0.01)
        //    {
        //        horizontalForward = currentMatrix.Forward - Vector3D.Dot(currentMatrix.Forward, gravityUp) * gravityUp;
        //    }
        //    horizontalForward.Normalize();

        //    var pitchAngle = Math.Asin(Vector3D.Dot(desiredForward, gravityUp)) * 180.0 / Math.PI;
        //    var clampedPitchAngle = MathHelper.Clamp(pitchAngle, -config.PGravityAlignMaxPitch, config.PGravityAlignMaxPitch);

        //    var finalForward = horizontalForward + gravityUp * Math.Sin(clampedPitchAngle * Math.PI / 180.0);
        //    finalForward.Normalize();

        //    ApplyDirectOrientation(finalForward);
        //}

        //private void ApplyDirectOrientation(Vector3D desiredForward)
        //{
        //    var currentForward = controller.WorldMatrix.Forward;
        //    var rotationAxis = Vector3D.Cross(currentForward, desiredForward);
        //    var rotationAngle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(currentForward, desiredForward), -1, 1));

        //    if (rotationAngle > 0.01 && rotationAxis.LengthSquared() > 0.01)
        //    {
        //        rotationAxis.Normalize();
        //        var localRotationAxis = Vector3D.Transform(rotationAxis, MatrixD.Transpose(controller.WorldMatrix));

        //        var maxRotationSpeed = 0.5;
        //        var rotationSpeed = Math.Min(rotationAngle * 3.0, maxRotationSpeed);

        //        ApplyGyroscopeRotation(localRotationAxis, rotationSpeed);
        //    }
        //    else
        //    {
        //        StopRotation();
        //    }
        //}

        //private void ApplyGyroscopeRotation(Vector3D localRotationAxis, double rotationSpeed)
        //{
        //    foreach (var gyro in controller.GetGyroscopes())
        //    {
        //        if (!gyro.IsWorking) continue;

        //        gyro.Pitch = (float)(localRotationAxis.X * rotationSpeed);
        //        gyro.Yaw = (float)(localRotationAxis.Y * rotationSpeed);
        //        gyro.Roll = (float)(localRotationAxis.Z * rotationSpeed);
        //        gyro.GyroOverride = true;
        //    }
        //}

        //public void StopRotation()
        //{
        //    foreach (var gyro in controller.GetGyroscopes())
        //    {
        //        gyro.GyroOverride = false;
        //        gyro.Pitch = 0;
        //        gyro.Yaw = 0;
        //        gyro.Roll = 0;
        //    }
        //}

        //public void StopAllMovement()
        //{
        //    foreach (var thruster in controller.GetThrusters())
        //    {
        //        thruster.ThrustOverridePercentage = 0;
        //    }

        //    StopRotation();
        //    ClearPath();
        //}

        //private bool HasNewObstaclesDetected()
        //{
        //    if (!PathfindingConfig.AllowSensors)
        //        return false;

        //    var sensors = controller.GetSensors();
        //    foreach (var sensor in sensors.Where(s => s.IsWorking))
        //    {
        //        var detectedEntities = new List<MyDetectedEntityInfo>();
        //        sensor.DetectedEntities(detectedEntities);

        //        foreach (var entity in detectedEntities)
        //        {
        //            if (IsObstacleInPath(entity))
        //            {
        //                return true;
        //            }
        //        }
        //    }

        //    return false;
        //}

        //private bool IsObstacleInPath(MyDetectedEntityInfo entity)
        //{
        //    if (currentPath.Count == 0 || pathIndex >= currentPath.Count)
        //        return false;

        //    var obstaclePos = entity.Position;
        //    var obstacleRadius = Math.Max(5.0, entity.BoundingBox.Size.Length() * 0.5);

        //    for (int i = pathIndex; i < Math.Min(pathIndex + 3, currentPath.Count); i++)
        //    {
        //        var waypoint = currentPath[i];
        //        var distanceToObstacle = Vector3D.Distance(waypoint, obstaclePos);

        //        if (distanceToObstacle < obstacleRadius + controller.BlockWaypointDistance)
        //            return true;
        //    }

        //    return false;
        //}
    }
}