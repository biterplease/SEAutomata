# Pathfinding

## Overview

The pathfinding system belongs exclusively to the `DroneController`. It produces one waypoint at a time via an **iterator pattern** — never computing full paths in the hot path. All state is held in `PathfindingContext` (a `struct`), which is passed by `ref` to every pathfinder method so no heap allocations occur during normal use.

## File Map

| File | Role |
|---|---|
| `PathfindingManager.cs` | Main entry point. Owns context lifecycle, component caching, obstacle scanning, raycast loop, waypoint behavior, method selection. |
| `PathfindingContext.cs` | The single struct that carries all pathfinding state. No game-object references — primitives, `Vector3D`, `MatrixD` only. |
| `IPathfinder.cs` | Interface both pathfinders implement: `GetNextWaypoint`, `CalculatePath`, complexity estimates. |
| `DirectPathfinder.cs` | Stateless static. Straight-line approach with perpendicular reposition and gravity-altitude correction. |
| `AStarPathfinder.cs` | Stateless static. Grid-based A* with adaptive grid spacing; falls back to direct on failure. |
| `AStarNode.cs` | Heap node extending `FastPriorityQueueNode` (Position, GCost, HCost, Parent). |
| `PriorityQueue.cs` | Fixed-size binary min-heap (BlueRaja design). No allocations after construction. |
| `AdjancencyList.cs` | Sparse weighted graph. Not wired into the main flow — available for future pre-built graphs. |
| `PathfindingResult.cs` | `PathfindingResult` enum (Success / NeedRaycast / Failed) and `PathfindingRequest` struct. |
| `MyGamePruningStructureDelegate.cs` | Wraps `MyGamePruningStructure` behind `IMyGamePruningStructureDelegate` for testability. |
| `MyPlanetDelegate.cs` | Wraps planet queries behind `IMyPlanetDelegate` for testability. |
| `Util.cs` | `PathfindingUtil` — direction remapping, rotation matrices, thrust analysis helpers. |

## Key Data Types

**`PathfindingContext`** (struct, always passed by `ref`) holds:
- Config scalars: `MinWaypointDistance`, `MaxWaypointDistance`, `MinAltitudeBuffer`, `MaxPathNodes`, `MaxRepositionAttempts`
- Controller state: `ControllerPosition`, `ControllerWorldMatrix`, `ControllerForwardDirection`
- Environment: `GravityVector`, `IsInPlanetGravity`, `PlanetCenter`, `PlanetRadius`
- Ship: `ShipMass`, `MaxLoad`, `ThrustData` (per-direction thrust floats)
- A* working sets (reused per call, never reallocated): `OpenSet`, `ClosedSet`, `OpenQueue`
- Shared buffers: `PathBuffer`, `NeighborBuffer`, `TraveledNodes`
- `KnownObstacles` (AABB list from sensors), `RaycastCache` (confirmed clear directions)
- `WaypointHistory`: previous / current / next waypoint + `WaypointBehavior` + alignment angle

**`PathfindingResult`** — `byte` enum: `Success`, `NeedRaycast`, `Failed`

**`WaypointBehavior`** — `byte` enum: `RunThrough` (< 15° turn), `SlowApproach` (15–45°), `FullStop` (> 45°)

**`WaypointResponse`** — returned by `GetWaypointResponse()`: position, estimated next position, behavior, alignment angle, `IsLastWaypoint`

## Call Flow

```
DroneController
  └─ PathfindingManager.SetTarget(ref target)
  └─ PathfindingManager.GetNextWaypoint(ref currentPos, out waypoint)   ← called each tick
        1. ScanSensorsForObstacles()        — single combined AABB scan
        2. SelectPathfindingMethod()        — Direct if close; A* if sensors/cameras available and distance justifies it
        3. Loop up to 3 iterations:
             pathfinder.GetNextWaypoint()
               → Success   → cache node, return
               → NeedRaycast → PerformRaycast(), loop again
               → Failed    → return Failed
        4. Complexity budget check: NeedRaycast + reposition increments; abort if > MaxRepositionAttempts × 2
```

## Pathfinder Selection Logic (`SelectPathfindingMethod`)

1. Distance < `MinWaypointDistance * 2` → **Direct**
2. `AllowAStar` && (sensors or cameras present) && `AStarComplexity < MaxPathNodes` → **A\***
3. `AllowDirectPathfinding` → **Direct**
4. Otherwise → **None** (returns `Failed`)

## DirectPathfinder

- Projects a waypoint along the straight line to target at `WaypointDistance`.
- If in gravity and below `MinAltitudeBuffer`, lifts waypoint to safe altitude above planet center.
- Checks `KnownObstacles` (AABB bounding-box intersection). On hit, tries up to `MaxRepositionAttempts` perpendicular offsets (±perp1, ±perp2, diagonals at 30% of `WaypointDistance`).
- If the chosen direction has not been raycasted yet, returns `NeedRaycast` instead of `Success`.
- `CalculatePath()` cannot handle `NeedRaycast` — only `PathfindingManager` drives the raycast loop.

## AStarPathfinder

- Converts world positions to a discrete grid; grid spacing scales with distance:
  - < 500 m → 25 m, < 2 km → 50 m, < 5 km → 100 m, ≥ 5 km → 200 m
- Uses 26-directional neighbors, pre-computed per `ControllerForwardDirection`.
- Reuses `OpenSet`, `ClosedSet`, `OpenQueue` from context (cleared, not reallocated).
- Falls back to `DirectPathfinder` for short distances or when A* fails and `AllowRepathing` is true.
- **Known GC issue**: `AStarNode` objects are heap-allocated per search. A node pool is the correct fix.

## Component Caching in PathfindingManager

`PathfindingManager` caches per-block state (thrusters, sensors, cameras keyed by `EntityId`). The `XxxChanged()` methods diff the new list against the cache and only call `RebuildXxxContext()` when something actually changes. This prevents re-running context construction every frame.

Notify the manager when blocks change:
- `ControllerChanged(IMyShipController)`
- `ThrustersChanged(List<IMyThrust>)`
- `SensorsChanged(List<IMySensorBlock>)`
- `CamerasChanged(List<IMyCameraBlock>)`
- `GridChanged(IMyCubeGrid)`

## Waypoint Lookahead & Behavior

When the drone is within 200 m of the current waypoint, `UpdateWaypointTracking()` pre-fetches the next waypoint and stores it in `WaypointHistory.NextWaypoint`. With all three waypoints (prev / current / next) known, `CalculateWaypointBehavior()` computes the incoming-to-outgoing angle and sets the appropriate `WaypointBehavior`. Call `AdvanceToNextWaypoint()` when the drone reaches a waypoint.

## SE API Integration Points

- `MyGamePruningStructure.GetTopMostEntitiesInBox` — sensor obstacle scanning
- `MyGamePruningStructure.GetTopmostEntitiesOverlappingRay` — per-direction raycasts
- `MyGamePruningStructure.GetClosestPlanet` — gravity-aware altitude correction

Both are abstracted behind delegate interfaces for unit testing.

## A* Node Pool

All `AStarNode` objects are pre-allocated once in `InitializeContext` as `context.NodePool[MaxAStarNodes]`. `FindPath` resets `context.NodePoolIndex = 0` at the top of every search; `GetOrCreateNode` bumps the cursor and calls `node.Reset()`. Zero heap allocations per search.

The `OpenSet`, `ClosedSet`, `OpenQueue`, `NeighborBuffer`, and `PathBuffer` are also pre-allocated in `InitializeContext` and only cleared (not reallocated) per search.

## Known Gaps / TODO

- `PathfindingManager.GetCameraDirection()` is a stub — always returns `Forward`.
- `WaypointInfo.SuggestedSpeed` and `LookaheadDistance` are defined but not consumed.
