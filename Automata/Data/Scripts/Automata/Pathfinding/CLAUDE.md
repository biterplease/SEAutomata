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
- Search queue (TODO/22 section 1; ini PathSearchWorkers, PathSearchMaxMs, PathWarmUp): a route that reaches stage 3 is queued in
  `PathSearchQueue` instead of searched inline. The request carries a frozen `PathQuery` (`Frozen()`); a `SnapshotScene`
  (entries with copied matrices + the live block trees; a rebuild makes a new tree, never clears one a worker reads) is captured
  on the main thread within a per-tick budget; the search runs on a worker with its own `PathfindingManager` (`RunSearch`)
  reading only that snapshot - no rays, no entity reads, no logging on the worker. The drone holds (`PlanPendingException` ->
  `DeferFlyRoute` / `DeferDocking`, `DroneControllerBlock.Pending.cs`); `ServicePendingRoute` only waits while
  `PathSearchQueue.Waiting`, then replans: `PlanRouteTimed` looks the job up first (`Find`) and goes straight to it (stages 1-2
  not run again). `AcceptSearch` checks the answer on the main thread: rays, doors (the worker hands over the doors per
  stretch), turn sweep, and box checks only against grids that changed since the worker read them (`SceneTrust`: tree object,
  `Entry.Version`, matrix); an accepted answer is reused for 3 s (routes of several stretches). Async callers: FlyRoute from the
  task runner / replans in a job, StartDocking (GoHome, DockAtConnector, docking replans; `DockPrep` keeps the corridor while
  waiting). Synchronous: player routes, parking, the GoCharge connector loop, RealShapeGrid queries, the memory stage.
  `PathWarmUp` offers areas (static grids with Automata blocks, 50 m round their projectors, construction fields) to
  `PathSearchQueue.WarmUp`, which only builds the scene ahead (entries, block trees).
- Failed-route cache (`PathfindingManager`, main thread): a plan that found no route is answered at once when asked again
  (owner, `QuerySignature`, coarse attitude, start within 2 m, goal within 1 m) for 120 s, 10 s when a moving grid / door / a
  budget was involved, until `LocalScene.RemovalVersion` changes (block removed, split, obstacle closed). Not for plans that ran
  out of the shared check budget.
- Stage time limits: ini StraightMaxMs (straight stage incl. detours, only when Lazy Theta* is on; not the tail of a far search)
  and MemoryMaxMs (RouteMemory.FindRoute), default 10 ms each: the later stages take the hard cases.
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
  pitch 0 with `LevelLegs` (drones in gravity, 2026-11-06: travel is level, down thrust along gravity), else within
  `MaxPitch` = the drone's Align to P-Gravity pitch limit, else free; local vertical per leg on planets; `MaxPitch` still
  bounds Theta held attitudes),
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

## Asteroids: rock in fit checks, rock ray bundle, Theta goal (2026-10-26, branch biter/pathfinding-improvements)
- `PathfindingManager.Rock.cs`: `RockInSphere` / `RockInBox` read the voxel content (`IMyStorage.ReadRange`, LOD coarsened to <= 1024 voxels; a voxel counts when its centre is within the volume grown by half a voxel) after the storage's own test (`RockPossible`: `Storage.Intersect` lazy, Disjoint = none; procedural asteroids answer Intersects across their whole surface band). No physics shapes needed, any thread.
- `HullSpaceFree` / `BoxFree` / `HullFitsOriented` count asteroid rock when the centre is seen (`voxels` param, default true): goal-inside, `tight`, escape / recovery / retrace / detour spots, turn room. Not counted: Lazy Theta* probes (own `VoxelState`), dock corridors (Corridor.cs), park spots (Parking.cs SpotFits: rock is the floor, VoxelSpotClear).
- `RaySegmentBlocked`: grids keep the 5-ray bundle (look-ahead, long legs); asteroids get the rock bundle (centre + hull edges/corners + the same plus the berth, 17 rays; 9 for hulls under 1 m) only where `RockNearSweep` (storage test along the sweep, 50 m pieces, 8 at most) says rock may be. Berth rays (outer ring) don't count in the start / end zones (skipStart + half hull + berth; len - half hull - berth with extendPastEnd), as blocks in SegmentBlocked. `CheckAhead` (reserve 22 rays, charged as cast) and `LegRaysBlocked` take the leg's attitude.
- Lazy Theta*: a goal in a blocked cell is offered by `Neighbours` from the free cells next to it (`thetaForcedGoal`; it was never queued, every such search spent its budget); rock is tested where any corner of a brick is seen (`SeenCorner`), not only its centre; the escape zone's tight clearance applies to rock too.

## Lanes joined where the drone is (2026-11-02)
`FlightLanes.FindLane`: a lane direction is used from the start's projection on it to the goal's (`Project`), not end to end: join point (only when > 20 m off the lane), the lane points in between, the leave point; at least 100 m of lane (`MIN_RIDE`) and its way (leave after join); cost = off at join + ridden length + off at leave. Before, a drone part way along (any replan in flight, just-in-time planning) was sent back to the lane's entry behind it and out along the same line again.

## Look-ahead along mapped paths (2026-11-02)
`EntitiesAhead(q, attitude, from, to)`: the 5-ray bundle along a stretch as far as the drone sees, grids / trees / any non-voxel entity (asteroid and planet voxels skipped; with `rock`, a second bundle for asteroid rock: 10 rays), rationed by the look-ahead ray budget. Used by scout sweeps (Drone/CLAUDE.md "Area sweeps: mapped paths"), whose paths come from `Pointer/SweepPlanner.cs` (coarse voxel reads / the heightmap), not from this planner.

## Rock the hull already touches; NoVoxels (2026-11-03)
`RaySegmentBlocked` / `BundleRay`: a rock hit whose face looks along the ray (the ray started inside rock: the drone's bore, a face its box overlaps) never counts; a hit inside the hull's box where it starts (+0.5 m) and not ahead of its centre counts only at work site way-ins (`StrictEnds`), as blocks in `SegmentBlocked`. (A large miner after a 13 m bore: every route, retrace and escape spot "blocked" by rock at 0 m, Error loop: project doc `claude/large-miner-stuck-after-bore-2026-11-02.md`.) `PathQuery.NoVoxels`: `SegmentBlocked` skips rock (a drone checking its own bore for grids).

## Lanes: side by side isn't a near miss (2026-11-03)
`FlightLanes.Report`: along the lane closer than hulls + clearance but really further apart (two drones of one job entering together, level with each other): the one behind gets a speed cap 5 m/s under the other's (`LANE_DROP_BACK`) to drop back into line; only a real distance under hulls + clearance suspends the lane ("two drones came too close in it": L7 was suspended the moment two pooled miners entered it). Lanes to scouted asteroids: see Drone/CLAUDE.md "Sweep join watched to the path".

## Round an asteroid (2026-11-05, audit item 6)
`StraightStageTimed`: a stretch blocked by an asteroid whose body lies across it (the line's point nearest the centre inside the rock's outer radius that way, `RockRadius`: walked in from the box's corner at LOD 2, any rock counts) gets `AsteroidArc`: points round the centre (`WorldAABB.Center`) in the plane of a, b and the centre, every `ARC_STEP_DEGREES` 15, each at the outer radius there and at its neighbours + hull + clearance + `ARC_MARGIN` 20 (chords included), first over a and last over b radially when they are lower. Once per asteroid per stage, one detour of the budget; `Shortcut` drops the points not needed. A bump beside the line still goes to `DetourPoint`. Was: the rest of a far search (> 600 m) to a mining site on the far side failed ("no way around"): detours of a few hull widths and the box's top can't round a few-hundred-metre asteroid.

## Lanes in `/automata debugdraw` (2026-11-05)
`/automata debugdraw lanes [range]` (also in "all"): the lanes of orchestrators the player owns with a line (either way) within the range of the player (`FlightLanes.Near`), drawn by `LaneOverlay` (client) like the orchestrator's Visualize: `OrchestratorBlock.DrawDashes`, names at the line starts as local GPS, off after DebugDrawTimeoutSeconds or `debugdraw off`. Lanes are the server's: single player / host collect them at once; a multiplayer client sends `LaneDrawRequestPacket` (tag 28), the server answers `LaneDrawPacket` (tag 29, `LaneShape` list). String `Common_DebugDrawLanesWithinM` (resx 999).

## Sweep orbits mapped once per order (2026-11-05)
`SweepCache` (`PathfindingManager.Sweeps`, server): an asteroid area sweep's orbits, keyed by what every sector task of the order shares (asteroid id, spot, tilt, sector count; each drone has its own job id). The first drone within MAP_RANGE creates the entry (`Get`); each drone maps with its own budget (`Step`: its own orbits first, then the others'), and keeps mapping the rest while it flies: each orbit read once (was: 8 drones x 140-240 ms / 86k-157k reads, each its own). Baseline per orbit, hull-free: the rock's envelope along the great circle (surface under the centre line raised to the highest rock within `BASE_LATERAL` 12 m across / `BASE_ALONG` 16 m along), a point every 8 m, LOD 2. `SweepPlanner.BeginOrbits`: the drone lifts it by its hull offset + `ASTEROID_CLEARANCE` (10) + half the bubble away from the centre, simplifies, places its scans (no reads). Kept `KEEP_MINUTES` 15 after last use (refreshed while its drones fly), complete entries saved with the world (`Automata_Sweeps.bin`). Planet / open-space spirals are still mapped per drone (heightmap: cheap).
`AsteroidArc` now also rounds lines that graze the body (within rock radius + hull + clearance): four scouts got "no way around" joining their orbits with no arc made.

## Sweep orbits round the rock, joined away from the spot (2026-11-05)
`SweepCache.RockCentre`: orbits go round the mean of the rock's voxels (content-weighted, one ReadRange at the LOD that keeps it to 32 a side: a 512 m box is LOD 4, 32^3 values), not the box's centre (off the rock: orbits kept their last radius and swung round empty space). File version 2 (version 1 entries aren't loaded). `SweepPlanner.BeginOrbits(.., from)`: each orbit is flown once round from its point nearest the drone (the next orbit: nearest where the last closed, over a point lifted to clear the chord), never within `JOIN_AWAY_DEGREES` 30 of the spot or its opposite (every orbit crosses there: all 8 scouts converged on the spot, joins failed, one gave up). No "back over the spot" point any more.

## Flight lanes v2 (2026-11-05, TODO/25 "As built")
Lanes are traced by the orchestrators (`FlightLanes.Trace`: at each auction with a location, for sweep / spot check orders as order lanes, between the bases on the relay network), not made from flights; boxes 40 x 40 m, the two ways one above the other; the pathfinder gives each drone a slot (`PathfindingManager.LaneRoute` -> `FindLane`: Through / Beside for hulls over 25 m / Wait at the entrance up to 20 s); drones report rock, trees and static grids found in a lane (`LaneObstruction` in their job reports) and the orchestrator redraws it (`Obstructed`); suspended lanes (near miss, crash) resume after 2 min; order lanes are dropped when the order found nothing. Tube checks, lanes from flights / busy routes, "held still" and "lost in lane" are gone. File version 2.
