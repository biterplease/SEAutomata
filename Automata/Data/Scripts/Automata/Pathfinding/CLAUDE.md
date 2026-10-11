# Pathfinding

## Overview (since 2026-09-30)

`PathfindingManager` is a session singleton (`AutomataSession.Init` -> `PathfindingManager.Load`, `Update()` every tick
refills the look-ahead ray budget). It implements **direct pathfinding with geometric repositioning**. A* is **not
wired** (`AllowAStar` only logs a warning); the legacy planners (`AStarPathfinder`, `DirectPathfinder`,
`PathfindingContext`, `IPathfinder`, `Util`, `AdjancencyList`) still compile but nothing calls them. See
`TODO/04-pathfinding-architecture.md` for what to keep from them.

## Conversation drone <-> pathfinder

1. Dispatch (`FlyRoute` for jobs, `StartDocking` for docking / going home) -> `DroneControllerBlock.PlanPath`:
   stand-alone observation-area detour first, then `PathfindingManager.PlanRoute(ref PathQuery, start, goal, waypoints)`
   per stretch. Waypoints are controller positions (the legs hold the drone's attitude; the goal is offset by the
   reference point: tool / connector).
2. The drone flies them as legs (`QueueGoTo`), each waypoint passed per `PathfindingManager.Classify` (15° / 45°).
   Route legs are `FlightOrder.Watched` (runtime only, not serialized).
3. `UpdateRouteWatch` (UpdateBeforeSimulation10, every 20 ticks): `CheckAhead` along the current leg (a few seconds
   of flight, never past the leg end; 5 rays per check, rationed by `RaysPerTick`; grid hits confirmed by the scene
   check so planner and look-ahead agree). Hit -> `Replan()` re-invokes the remembered `FlyRoute` / `StartDocking`
   from the current position. More than `MaxRepositionAttempts` replans or no route -> `routeFailed`, the order is
   cleared, the task fails Unreachable.
4. Just in time: if the plan had stretches not in view (`routeUnseen`), once the drone has flown
   max(10 m, SimulatedSensorRange / 2) (measured in the active order's anchor frame) it `Replan()`s; not counted as
   a repath, skipped when `AllowRepathing` is off.

## Scene (2026-10-01): no planning rays

- `LocalScene` (owned by the manager): `MyDynamicAABBTreeD` of obstacle entities (grids with physics, asteroids -
  not planets or their `MyVoxelPhysics` pieces), filled lazily by `Ensure(sphere)`: one
  `MyGamePruningStructure.Instance.GetTopMostEntities` per area (>= 60 m), reused by every drone within 30 ticks.
  Entries unused for 60 s are dropped. Per grid: a tree of block boxes in grid coordinates, built on first use, kept
  by `OnBlockAdded` / `OnBlockRemoved`, rebuilt on `OnGridSplit` / BlocksCount mismatch. Queries are transformed into
  the grid frame with its current matrix (moving grids need no updates).
- `SegmentBlocked`: one oriented box per <= 20 m chunk, hull cross-section + berth across the travel direction, from
  the hull centre at `skipStart` to b (+ hull front + berth with `extendPastEnd`), limited to what the drone sees.
  Near the start / end only blocks the hull itself touches count (leaving / arriving at tight spots) unless
  `PathQuery.StrictEnds`. `hit` = nearest face of the first block along the leg. Asteroids: the old 5-ray bundle.
- `HullSpaceFree` / `TurnRoomFree`: sphere vs block boxes (seen blocks only); asteroids near: 14 rays.
- `PathQuery.Berth` (0 = ObstacleClearance), `IgnoreGrid/IgnoreMin/IgnoreMax` (the target block), `StrictEnds`.
- Work-site way in (`WorkSite`): target block ignored, 1 m berth, strict ends, hull front stopped 0.25 m off the face
  plane (neighbours flush with the face don't count).
- Logs: per stretch `Pathfinding: ... checks, blocks, rays, ms` (plan totals so far) and per route
  `Drone X: route N m, L legs: ms, scene checks, blocks tested, rays, stretches not in view yet`. Budget: 400
  checks (one per sweep chunk / sphere) + rays per route.

## PathfindingManager

- `PathQuery` (struct, built by the drone per call): controller matrix, hull box (controller frame), own grids,
  cameras, sensors, gravity up, clearance, `SleepingIds` (docked: instruments the dock switched off count as on).
- `ObservedRange(q, dir)`: simulated instruments, never real camera raycasts / sensor queries, measured from the
  controller, functional + Enabled. Sensors = short range, omnidirectional: `SimulatedSensorRange` all around (field
  settings irrelevant). Cameras = long range, directional: `CameraRange` along the camera's -Z (WorldMatrix.Forward)
  where it is the dominant axis (90-degree square frustum; 6 cameras = all directions). Blocks the dock routine
  switched off (`SleepingIds`) count as on. Not required -> simulated all around. All-around part cached per query. Beyond what the drone sees a stretch is assumed clear (checked on the way).
- `SegmentBlocked`: see Scene. Not obstacles: own grids, projections, characters, floating objects, planets.
- `PlanRoute`: points list [start, goal]; per stretch: terrain (`GetClosestSurfacePointGlobal`, 15 m end margins,
  climb to highest surface + max(`MinAltitudeBuffer`, MinAltitude) + hull clearance; with a MinAltitude a stretch must stay
  min(MinAltitude, its lower end's height) - chord sag above the terrain (cruise: the full altitude; near a job / landing /
  take-off: no higher than its ends), and rounded corners no lower than their lower end; near-vertical stretches - horizontal run < max(15 m, 0.25 x vertical), i.e. take-off / landing - are exempt: climbing them only added another vertical stretch, forever), then obstacles -> `DetourPoint` (backed off from the hit, out
  across the travel direction: up / sides / diagonals / down in space only, steps x1..x8, a -> p clear and a probe
  past the obstacle; last resort over the obstacle's box). Budgets: `MaxRepositionAttempts` detours, `MaxPathNodes`
  waypoints, 400 checks + rays per route (`BeginPlan`).
- Config: `Config/PathfindingConfig.cs`, ini `[Pathfinding]` (RequireCamerasForPathfinding, RequireSensorsForPathfinding,
  CameraRange, SimulatedSensorRange, RaysPerTick, AllowRepathing, UsePlanetAwarePathfinding,
  MinAltitudeBuffer, MaxRepositionAttempts, MaxPathNodes, AllowDirectPathfinding, AllowAStar). MinWaypointDistance /
  MaxWaypointDistance are not used by the direct planner.

## Route shape and attitude (2026-10-01)

- `AssignAttitudes`: each leg flown aligned with its direction (nose = controller forward; gravity: level heading,
  pitch within `MaxPitch` = the drone's Align to P-Gravity pitch limit, else free; local vertical per leg on planets),
  only when `TurnRoomFree` at the leg start (hull radius about the hull centre + `ObstacleClearance`) and the
  leg is clear at that attitude; else the previous attitude (if clear), else the one PlanRoute checked with.
- Ray bundle margin and turn room use `ObstacleClearance` (default 1.5 m). Look-ahead doesn't extend past the leg end;
  player goals (`LooseGoal`) get no berth beyond them; the leg-start check (`CheckLegStart`) doesn't extend past a loose final goal either (parking on a static grid replanned forever).
- Approach points (tool faces, dock) are pushed out (2.5 m steps, max 4) until there is room to turn there
  (`WithTurnRoom`, hull radius about the tool / connector point).
- Planets (`ShapeForPlanet`): stretches > `ArcMinDistance` follow the great circle (a point per `ArcStep`, capped at
  MaxPathNodes/2); drone setting Min altitude (player flights only, not jobs) adds straight up / down points.
- Asteroids are obstacles; planets are terrain checks only.
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

## Corners, planet chords, moving grids (2026-10-06)

- `Classify`: run through below `RUN_THROUGH_DEGREES` (25°, was 15°), slow approach below 45°, stop above.
- `RoundCorners` (every route, after `Shortcut`): corners of 25°+ become arcs from 40% of the shorter leg (25 m at
  most) before the corner to as far after it, in chords of 22° at most; each chord checked like a leg plus terrain
  clearance (`TerrainTooClose`, hull half height + 1 m); blocked: half the cut once, else the corner stays.
- Planet arcs (`ShapeForPlanet`): chords as long as `[Pathfinding] ArcSag` (25 m sag, length sqrt(8 r sag), at least
  ArcStep) allows instead of a point every ArcStep raised over each bump; then `LiftChords` raises chord ends until
  every sample (100 m) clears the terrain by the floor altitude (4 passes). Chords only dip between their ends: the
  route is never higher than its highest end. TerrainInTheWay still looks closer afterwards.
- `LocalScene`: moving grids (> 0.05 m/s or rad/s) get their broad-phase box moved at every query frame and grown by
  their speed x REFRESH_TICKS; before, the box was only moved when the area was looked up again (30 ticks), and a
  ship at speed was missed.
- Docking / undocking corridors: see Drone/CLAUDE.md (they use `SegmentBlocked` / `BoxFree` / `TurnSweepFree` with
  the dock clearance, no A*).

## Docking legs, way out of a bay (2026-10-11)
`CheckLegStart` treats docking route legs as loose ends (nothing past the end: the turn point sits at the station). `PlanPath` escape candidates start with the dock departure's direction (Drone/CLAUDE.md, fixes 2).

