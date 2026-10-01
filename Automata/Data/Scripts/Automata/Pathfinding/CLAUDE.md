# Pathfinding

## Overview (since 2026-09-30)

`PathfindingManager` is a session singleton (`AutomataSession.Init` -> `PathfindingManager.Load`, `Update()` every tick
refills the look-ahead ray budget). It implements **direct pathfinding with geometric repositioning**. A* is **not
wired** (`AllowAStar` only logs a warning); the legacy planners (`AStarPathfinder`, `DirectPathfinder`,
`PathfindingContext`, `IPathfinder`, `Util`, `AdjancencyList`) still compile but nothing calls them. See
`TODO/03-pathfinding-evaluation.md` and `TODO/04-pathfinding-architecture.md` for what to keep from them.

## Conversation drone <-> pathfinder

1. Dispatch (`FlyRoute` for jobs, `StartDocking` for docking / going home) -> `DroneControllerBlock.PlanPath`:
   stand-alone observation-area detour first, then `PathfindingManager.PlanRoute(ref PathQuery, start, goal, waypoints)`
   per stretch. Waypoints are controller positions (the legs hold the drone's attitude; the goal is offset by the
   reference point: tool / connector).
2. The drone flies them as legs (`QueueGoTo`), each waypoint passed per `PathfindingManager.Classify` (15° / 45°).
   Route legs are `FlightOrder.Watched` (runtime only, not serialized).
3. `UpdateRouteWatch` (UpdateBeforeSimulation10, every 20 ticks): `CheckAhead` along the current leg (a few seconds
   of flight, never past the leg end; 5 rays per check, rationed by `RaysPerTick`). Hit -> `Replan()` re-invokes the
   remembered `FlyRoute` / `StartDocking` from the current position. More than `MaxRepositionAttempts` replans or no
   route -> `routeFailed`, the order is cleared, the task fails Unreachable.

## PathfindingManager

- `PathQuery` (struct, built by the drone per call): controller matrix, hull box (controller frame), own grids,
  cameras, sensors, gravity up, clearance, `InstrumentsAsleep` (docked: count switched-off instruments as on).
- `ObservedRange(q, dir)`: simulated instruments, never real camera raycasts / sensor queries, measured from the
  controller, functional + Enabled. Sensors = short range, omnidirectional: `SimulatedSensorRange` all around (field
  settings irrelevant). Cameras = long range, directional: `CameraRange` along the camera's -Z (WorldMatrix.Forward)
  where it is the dominant axis (90-degree square frustum; 6 cameras = all directions). Blocks the dock routine
  switched off (`SleepingIds`) count as on. Not required -> simulated all around. All-around part cached per query. Beyond what the drone sees a stretch is assumed clear (checked on the way).
- `SegmentBlocked`: ray bundle (hull centre + 4 sides of the hull cross-section + 0.5 m), up to the hull front at the
  end point. Not obstacles: own grids, characters, floating objects, voxels (planets / asteroids).
- `PlanRoute`: points list [start, goal]; per stretch: terrain (`GetClosestSurfacePointGlobal`, 15 m end margins,
  climb to highest surface + `MinAltitudeBuffer`), then obstacles -> `DetourPoint` (backed off from the hit, out
  across the travel direction: up / sides / diagonals / down in space only, steps x1..x8, a -> p clear and a probe
  past the obstacle; last resort over the obstacle's box). Budgets: `MaxRepositionAttempts` detours, `MaxPathNodes`
  waypoints, 400 rays per route (`BeginPlan`).
- Config: `Config/PathfindingConfig.cs`, ini `[Pathfinding]` (RequireCamerasForPathfinding, RequireSensorsForPathfinding,
  CameraRange, SimulatedSensorRange, RaysPerTick, AllowRepathing, UsePlanetAwarePathfinding,
  MinAltitudeBuffer, MaxRepositionAttempts, MaxPathNodes, AllowDirectPathfinding, AllowAStar). MinWaypointDistance /
  MaxWaypointDistance are not used by the direct planner.

## Route shape and attitude (2026-10-01)

- `AssignAttitudes`: each leg flown aligned with its direction (nose = controller forward; gravity: level heading,
  pitch within `MaxPitch` = the drone's Align to P-Gravity pitch limit, else free; local vertical per leg on planets),
  only when `TurnRoomFree` at the leg start (14 rays, hull radius about the controller + `ObstacleClearance`) and the
  leg is clear at that attitude; else the previous attitude (if clear), else the one PlanRoute checked with.
- Ray bundle margin and turn room use `ObstacleClearance` (default 1.5 m). Look-ahead doesn't extend past the leg end;
  player goals (`LooseGoal`) get no berth beyond them.
- Approach points (tool faces, dock) are pushed out (2.5 m steps, max 4) until there is room to turn there
  (`WithTurnRoom`, hull radius about the tool / connector point).
- Planets (`ShapeForPlanet`): stretches > `ArcMinDistance` follow the great circle (a point per `ArcStep`, capped at
  MaxPathNodes/2); drone setting Min altitude (player flights only, not jobs) adds straight up / down points.
- Asteroids are obstacles; only planets are excluded from rays (terrain checks).
- Debug orders use the pathfinder: Navigate / Queue waypoint (`FlyPlayerRoute`, replans through the remaining
  `FlightOrder.PlayerWaypoint` legs), Place mount (`RouteWorkAt`), Navigate relative (FlyRoute, anchored).
- Route legs steer the **hull box centre** (`ReferenceOffset = localHullBox.Center`, `PathQuery.HullCentred`): route
  points are hull-centre positions; at approach points the drone turns in place about the hull centre (a zero-length
  leg with the work / dock attitude, `BuildHullLegs`), then the final order moves the tool / connector straight in.
  `ObstacleClearance` default 3 m.

## Not done yet

- A* over a sparse graph (obstacle box corners + detour points, cached confirmed edges), see TODO/04.
- Route persistence (per-drone `Entity.Storage`, shared corridor cache in world storage), see TODO/04.
- `CastRayParallel` (off-thread rays) instead of synchronous `CastRay`.
