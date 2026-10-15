# Pathfinding

## Overview (since 2026-09-30)

`PathfindingManager` is a session singleton (`AutomataSession.Init` -> `PathfindingManager.Load`, `Update()` every tick
refills the look-ahead ray budget). It implements **direct pathfinding with geometric repositioning**. A* is **not
wired** (`AllowAStar` only logs a warning); the legacy planners (`AStarPathfinder`, `DirectPathfinder`,
`PathfindingContext`, `IPathfinder`, `Util`, `AdjancencyList`) still compile but nothing calls them. See
`TODO/04-pathfinding-architecture.md` for what to keep from them.

## 2026-10-13: fall-forward chain, memory, Lazy Theta*, lanes, localized sensing (TODO/20)

- `PlanRouteTimed` is a chain, simplest first, each stage only when the one before fails: 1. `StraightStage` (the
  straight line, terrain climbs, greedy detours; only `STAGE1_DETOURS` = 3 when Lazy Theta* is on), 2. `RouteMemory`
  (`Pathfinding/RouteMemory.cs`: points / links drones have flown, hull-centre positions in the order's anchor frame,
  A* over links wide enough for the hull, each link of the answer checked; saved to world storage
  `Automata_RouteMemory.bin`; ini UsePathMemory, PathMemoryMaxNodes, PathMemoryMaxAgeHours), 3. Lazy Theta*
  (`PathfindingManager.Theta.cs`: free leaves of a bricked grid - bricks of 64 / 16 / 4 cells, a free brick answers for
  all its cells; blocks via `HullSpaceFree`, asteroids via `IMyStorage.Intersect` (whitelisted); weighted heuristic x2;
  answer checked with `SegmentBlocked`; far goals searched for 2 x ThetaSearchRadius, the rest by stage 1; ini
  AllowLazyTheta, ThetaSearchRadius, ThetaMaxExpansions, ThetaMaxMs). Stage 4 is the drone's repositioning. The
  per-plan log line names the stage and each stage's ms; later stages get their own check budget (`budgetBase`).
- Recording: `DroneControllerBlock.RecordFlight10` (every 10 ticks on a watched route) feeds the memory and the lanes.
- Lanes: `Pathfinding/FlightLanes.cs` (+ `Orchestrator/OrchestratorBlock.Lanes.cs` for the terminal list / drawing).
  Made from a trip between two places (static grid with a Logistics Computer, asteroid), offset each way, tubes
  checked at idle where the trip's sensors saw, lifted at crossings; `FindLane` makes the lane points the route's
  preferred waypoints in `PlanPathFrom`; `Report` gives the safe following speed (`laneSpeedCap`); incidents suspend.
  Saved to `Automata_Lanes.bin`; ini FlightLaneExpirationMinutes.
- Localized sensing: `SeenSpan(q, a, dir, len)` (first seen stretch of a line: spheres around working sensors, 90°
  cones from working cameras at their own positions; not required: SimulatedSensorRange around the hull box /
  one simulated forward camera on the hull's front) and `Seen(q, p)` replace the controller-centred `ObservedRange`
  (kept as a wrapper from the controller). `SegmentBlocked` checks the first seen stretch.
- Defaults: SimulatedSensorRange 100, CameraRange 1500.

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
  `PathQuery.StrictEnds`. A block the hull box already overlaps where it starts (attitude held) whose centre is not
  ahead along the leg doesn't count either (leaving it; a wedged drone was blocked every way, away included;
  `StartContactsLeft` counts them). `hit` = nearest face of the first block along the leg. Asteroids: the old 5-ray bundle.
- `HullFitsOriented(q, centre, frame, margin)`: the exact box (grown by margin) instead of the any-attitude sphere.
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

## Real-shape refinement (2026-10-14)
`PathQuery.RealShapeGrid`: in `SegmentBlocked`, a block of that grid in the way by its cell box is checked with rays against the grid's physics across the swept box's cross-section (`RealShapeClear`); clear unless a ray hits inside the block's cells. Budget `RealShapeRayBudget` (caller sets / resets), stats `RealShapeClears` / `RealShapeRays`. Used by weld work sites only.

## Held attitudes in Lazy Theta* (2026-10-14)
`ThetaStage`: the any-attitude search (hull sphere) gets half the budget (`THETA_FREE_SHARE`). Nothing: `HeldCandidates`
- the drone's attitude as it is, then 90° turns about its axes (each distinct box shape along the current axes once,
least turned first, at most 6; in gravity only ups within the pitch limit, 0.7 without one; a turned one only where
`TurnSweepFree` at the start) - each searched with the hull box in that attitude (`Probe`: `BoxFree` of the box grown
by the brick / quarter cell), sharing the rest of the budget (as it is: half of it). Found: `q.Controller` keeps that
attitude, `q.HoldAttitude` set (AssignAttitudes: every leg keeps it), log "found a way holding the drone's attitude".
What the drone sees stays its real attitude (`q.Eyes` / `EyesOf`). Stage name "theta, held attitude".
- `RealShapeClear` rays end at the sweep's end (`t1`: the hull front's farthest reach), not past the block: a weld
  through a neighbour is not blocked by its collision beyond where the hull stops.
- Turned held attitudes need `TurnSweepFree` with the full berth at the start; none when `q.NoHeldTurn` (near docks).
