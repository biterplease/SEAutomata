# IAI Construction Computer

- The **Construction Computer** scans the grid or area of the vicinity its installed for construction jobs.

## Embedding

The **Construction Computer** class may be embedded inside:
- An **Orchestrator**, in order for the **Orchestrator** to fulfill the scanning of construction jobs. If embedded in an Orchestrator, it should have the `OperationMode` `BuiltInToOrchestrator`.
- A **Drone Controller**: A simple **Drone** should be able to scan for, and fulfill, simple construction tasks on its own, i.e. welding and grinding.
    - When the **Construction Computer** is embedded into a **Drone**, and its operation mode set to `BuiltInToDrone`, it should be limited to single tasks only:

## Construction job types
- **WeldBlock**: Weld a block with needed components. The block may be **Unfinished**, **Damaged**, or **Projected**.
- **GrindBlock**: The Construction Computer defines, a specific color in its setttings. Any block that has **exactly** that color (default RGB 255,0,0, no tolerance) is marked for Grinding.
- **Weld ignore color** (default RGB 255,0,255): blocks painted exactly that color are never welded or repaired, and projected blocks of that color are not built.

## Scope and build order (2026-10-01)

- Drone (stand-alone) observation area: up to 10x10x10 blocks, offset up to +-15 blocks. Scans the home grid and every
  grid **physically** linked to it (rotors, pistons, hinges, connectors, landing gear: `GridLinkTypeEnum.Physical`),
  owner / faction / unowned only (`IsGridAllowed`). Floating grids / projections not linked to it: Construction
  Computer only (50x50x50, TODO).
- `BuildPlanner` (Construction/BuildPlanner.cs, TODO/08): per physical grid, units (3x3x3 cells small grid, 1 cell
  large), vertical slice through the seed first (bottom-up, centre-out), then neighbouring slices outwards;
  projected units that would take a neighbour's last free face are held back (lookahead 32); grinding reversed;
  `Fronts(workers)` splits the order for several drones (not wired yet). `ScanArea` keeps min(4 x maxJobs, 500)
  candidates nearest the centre for it and outputs the first maxJobs of its order.
- "Top" (`ConstructionComputer.TopOf`): gravity; in space a projecting projector's up on that grid, else the area's
  up, else the grid's. Used by the scan, the planner and the drone's face choice (top face first, approach tilted
  up to 50 degrees).

## Construction Computer block (2026-10-02)
`ConstructionComputerBlock` (MyProgrammableBlock subtype `AutomataLargeConstructionComputer`): observation area along the block's axes, 1-50 blocks of 2.5 m per axis (default 25), centre offset -100..100 blocks per axis (`AreaOffsetBlocks*`; older metre offsets converted once on load, `OffsetInBlocks`), drawable (magenta, client only, debug draw timeout); scanned every `ScanIntervalSeconds` (default 30): every grid group reaching the box (`GetEntitiesInAABB`, one `ScanArea` per physical group), so floating projections / unlinked grids count. Owner / faction / unowned grids only, working drones excepted. Targets in planner order are grouped into jobs of `BlocksPerJob` (default 10, one job type each; `ConstructionComputer.JobFor`, world positions) and announced on CONSTRUCTION_COMPUTER_JOB_ANNOUNCEMENT (ComponentsInventory: weld needs / grind returns) every 20 s until an orchestrator's award names the job; at most `MaxJobAnnouncementsPerUpdate` (config) unassigned jobs. Blocks in a job aren't put in another; a job whose blocks no scan finds any more is done; unassigned for 3 min or assigned and no keepalive from its drone for 15 min (`JOB_KEEPALIVE`, every 60 s): dropped (blocks back to the pool). Weld jobs need a logistics computer with Provide for construction supplying the components.

Job publishing (2026-10-02): at most `MaxPublishedJobs` (server config [ConstructionComputer], 10) unawarded jobs (on hold included). Jobs are cached (never regrouped or re-created while their blocks need work, kept through saves): each is announced once; again after 20 s if it reached no orchestrator, after 5 min without award / failure, or when its hold ends. JOB_ANNOUNCEMENT_FAILED puts it on hold for HoldSeconds; an award stops it (dropped 15 min after the award if not done, blocks back to the pool). Done detection only from scans not cut off at 500 targets. Logistics computers handle their request / push jobs the same way (hold on failure, award stops re-announcing for 15 min).

## Retry jobs (2026-10-06)
Scan cache (`ScanCache`, 2026-10-08): each check (`ScanIntervalSeconds`) only calls `ScanArea` for grid groups whose signature changed since their last scan; the others give their remembered targets. Signature per group = per grid (id, event version, static / physics / closing flags, pose to 5 cm / 0.002, refused by ownership / working drone) + per projector (working / projecting, `TotalBlocks`, `RemainingBlocks`, `RemainingArmorBlocks`, `BuildableBlocksCount`, offset, rotation, projected grid id + pose); area, modes, colours or priority changing drops everything. The event version is bumped by `OnBlockAdded` / `OnBlockRemoved` / split / merge / grid changed / ownership / static, and by `OnBlockIntegrityChanged` / `OnGridBlockDamaged` only when a block starts or stops needing work (`ConstructionComputer.NeedsWork` against the last scan's target cells), so a drone welding a target changes nothing; a closed job `Touch`es its grids. Paint (grind / weld-ignore colours) and anything blocking a projected block have no event: every `max(120 s, ScanIntervalSeconds)` (`ScanCache.FULL_RESCAN_SECONDS`) and on the retry button the cache is flushed (full scan). `RemainingBlocksPerType` is not used (allocates a dictionary).
Debug timers (`PerfStats`, only when the server log level has Debug): LCD feed packets (computers, drones) / computers' LCD update, CC scan, CC group scanned / cached; a summary line per timer each minute ("Perf (60 s): ...") with count, total, avg, max and % of server time.

Terminal button (group capable): bumps `RetryRequest`; the server lifts holds, re-announces unassigned jobs and rescans the area.

## Audit fixes (2026-10-06)
- Blocks of an aborted / declined job are held back before a new job: 5 min after "Unreachable", 60 s otherwise (they used to come back at once under a new job id, past the orchestrator's retry memory). Retry jobs clears the hold.
- A drone report / keepalive for a job whose award this computer never heard counts (assigned then).

## Multi-drone jobs, abort cooldown (2026-10-06)

A job shared by several drones closes on the last drone's report (`Failures` / `LastFailure` kept for the event line).
The per-block cooldown after an abort / decline is server config `BidBlacklistCooldownTicks` (default 3600 = 60 s);
after Unreachable it stays 5 min.

## Work priority (2026-10-07)
"Work priority" dropdown (construction computer: `WorkPriority`, ProtoMember 23; stand-alone drones: `ConstructionWorkPriority`): the 6 orders of Repair (built, damaged), Projected, Unbuilt (placed, not welded up, not damaged); default Repair, projected, unbuilt. `ScanArea` ranks the candidate pool by category first (`SortKey` = rank x 1e12 + distance²) and outputs the categories in order, each in build order; grind ranks with the first category (keeps its place). The computer sorts all grid groups' targets the same way, never mixes categories in a job (`OpenJob.Category`, saved) and announces unawarded jobs category by category. Jobs already published stay published (`MaxPublishedJobs` can be full of a lower category until those are awarded or done).

## Projections: buildable blocks only (2026-10-11)
`ScanArea` looks only at projected blocks buildable right now, never ahead. `IMyProjector.BuildableBlocksCount` (the game's own count: `CanBuild` without physics, rechecked at most every 2 s after a block change on the projector grid, at once on an offset change) of 0: the projection is skipped; else the block loop stops once that many were found in the area. Per block: color, area, pool, then `CanBuild(b, false)` (drops most), free face, `CanBuild(b, true)` (physics) last. Log: "projections with nothing buildable", "projected obstructed". Next step (own per-projector frontier): `TODO/18-projection-frontier.md`.

## Weld priority: shares, weld kinds on the wire (2026-10-11)
"Work priority" is now "Weld priority". Construction computer: every kind with work is published at once. `ScanArea(..., shares: true)` keeps a pool part per kind (repair / projected / unbuilt / grind, 3 : 2 : 1 by rank, grind 3) and outputs each kind up to its part of maxJobs before the rest in rank order. `ShareSlots` splits `MaxPublishedJobs` over the kinds with work by the same weights (Sainte-Laguë: 10 slots, three kinds = 5 / 3 / 2), capped by what each kind can fill; published jobs count against their kind. Stand-alone drones: strict order as before. Job announcements carry `WeldCategory` (JobTypeExtensions.WELD_*); every CC log / event names the job "Weld:Repair" / "Weld:Projected" / "Weld:Unbuilt" (`LabelOf`).
