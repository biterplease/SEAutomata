# Drone Controller definition

- The **Drone Controller** is the first entity of the modthe player is exposed to, it should be cheap, and accessible early game.
- The **Drone Controller** is also the only entity capable of fulfilling tasks.


## Operation modes
- **ManagedByPlayer**: The player can give tasks to the drone through the CustomData of the block, in a simple format. When the player clicks the Execute button on the drone Terminal, it will attempt to parse the task list and perform it.
    - Next to the `Execute` button there should be a checkbox that reads `Repeat task list`, that, when checked, would prompt the drone to repeat the same task list.
- **ManagedByOrchestrator**: The drone will idle until it receives messages in the `ORCHESTRATOR_BID_ROUND_START` channel.
- **StandAlone**: The drone will perform simple tasks on its own, namely
    - Welding 1 block
    - Grinding 1 block
    - Mining ore

## StandAlone operation
- **Stand-alone behaviour**: A drone with a drone controller block should be able to perform simple jobs like welding, grinding, and mining.
- **Simple behavior of embedded classes**: When a drone embes a **Construction Computer**, a **MiningSurveyor**, or a **Logistics Computer**,  the classes should be purposedly limited in their scope. The gameplay reason is that the Drone has limited processing capacity.
- **Purposely dumb**: Drones performing simple tasks are likely to collide in their tasks:
    - Example 1: Drones A and B operating in solo-mode, may scan a block needing welding at the same time, and both will try to weld it, only for one of them to find that it was already welded by the other drone.
    - Example 2: Drones with mining setting and capabilities, when in solo mode, don't respond to quotas, only filters for 1 ore are allowed. They simply "see ore, mine ore", potentially overfilling containers, or crashing with other drones trying to mine the same resource.
- **Healthcheck**: Even in solo-operation mode, drones should still respect their settings for H2 and battery levels, stop, and recharge when needed

### Example flows

#### Drone finds ore to mine

- When a Drone's embedded **Mining Surveyor** caches ore location, the drone should immediately create the shortest task set that gets the ore to the grid its connected to:
    - Start at connector
    - Go to mining location
    - Mine ore
    - Deposit ore at the connector
    - Repeat

#### Scenario: Drone finds a block to build

- **Trigger conditions**: Drone must have a welder, drone must have some cargo capacity, drone performed a scan of the grids is connected to, and found a block to build.
- **Simple behaviour**: First block that needs building will get built immediately.
- **Conditions for fulfillment**: 
    - **Inventory**: Does one of the conveyor networks in the grid the drone is connected to contain the needed materials to build?
    - **Capacity**: Can I carry all or some of the materials
- Then goes and welds the block


## Thrust & Environment Logic
- **Atmospheric**: Ion thrusters = 0% effectiveness; Atmospheric thrusters = 100%.
- **Space**: Atmospheric thrusters = 0% effectiveness; Ion thrusters = 100%.
- **Hydrogen**: Works in both, but requires monitoring `H2Level`.
- **Rovers**: If `Capabilities` include `Wheeled`, navigation must prioritize surface pathfinding over 3D flight.

## Cargo & Mass Calculations
- **Planetary**: Max load is limited by thrust-to-weight ratio. Use the player-configured `MaxLoadKg` as a hard cap to respect design intent.
- **Zero-G**: Max load is limited only by `CargoVolume`.
- **Logic**: When reporting to an Orchestrator, a drone must calculate its "Current Max Cargo" based on gravity and remaining thrust.

## Capabilities and self-awareness

- **Capabilities check**: Drones should be able to perform a self asessment by inspecting the blocks in its own grid, and determinin what they can do. Refer to `Automata\Data\Scripts\Automata\DroneController\Drone.cs` for drone capablities
- **Dimensional Awareness**: drones must understand their physical "footprint" to avoid getting stuck trying to navigate in tight spaces.
- **Profile Definitions**:
    - **Player Input**: Player selects one simple geometric option (Rectangle, Triangle, Oval) for each view, via terminal for **Top, Side, and Front** views.
    - **Drone self assessment**: understand its own geometry and its faces by analyzing itself
    - **Normalization**: 
    - Rectangle: Full width/height.
    - Triangle: 50% width at top, 100% at bottom (tapered).
    - Oval: 80% of bounding box (rounded corners).

## Pathfinding

- **Pathfinding manager**: The drone must interact with the **Pathfinding Manager** to find its way to a check point.
- **Separation of concerns**: The pathfinding manager handles the heavy lifting of the pathfinding operations, the Drone should worry how to get there, and ask the **Pathfinding Manager** if it cannot reach the current waypoint.

### Pathfinding Hardware Simulation
Drones "see" the world via simulated raycasts, but only if they have the correct hardware or if server settings allow it.

#### Short-Range (Sensors: < 50m)
- **Constraint**: If `requireSensorsForPathfinding` is TRUE, drones without a functional Sensor block are "blind" to obstacles within 50m.
- **Implementation**: Raycast in 6 directions from the sensor block location, NOT the controller.

#### Long-Range (Cameras: 50m - 1000m)
- **Constraint**: If `requireCamerasForPathfinding` is TRUE, drones require cameras to "look ahead" for `DirectPathfinder` or `AStar`.
- **Logic**: Use @PathfindingContext to check `HasCameras()`. If no camera exists in the desired travel direction, the path is considered "Unknown/Blocked."

#### Server Admin Overrides
- Admins can designate specific block types to act as "Simulated Sensors" to save performance.
- Admins can toggle `allowAStar`. If FALSE, `PathfindingManager` must fallback to `DirectPathfinder`.

### Settings Hierarchy
1. **Server Bounds**: `globalMinWaypointDistance` and `globalMaxWaypointDistance` are hard limits.
2. **Player Intent**: Players define `DesiredWaypointDistance` and `MinAltitudeBuffer` in the terminal.
3. **Validation**: The `PathfindingContext` must always use the CLAMPED value: 
   `Context.WaypointDistance = Clamp(Player.Value, Server.Min, Server.Max)`.

### Removal Policy
- **Altitude**: Do NOT use server-wide altitude buffers. This is a per-drone player setting.
- **Hardware**: Always check `requireSensors` or `requireCameras` before allowing a drone to transition from `Direct` to `AStar` pathfinding.

## Combat handover (2026-10-05: a behaviour profile, no longer an operation mode)
Combat drone profile + "Managed by orchestrator" (`DroneControllerBlock.Combat.cs`, `TODO/09`); the old `CombatHandover` mode is migrated on load (`MigrateSettings`). Needs AI Defensive, AI Flight and AI Offensive (capability `HasDefenseSystems`; error at home without them). Between jobs, the Defensive block's `FoundEnemy` (it scans while docked, behaviour off) within the **auto-launch radius** of the drone (0-2000 m, default 2000; 0 = never) launches it: undock, fly toward the threat, hand over within the handover distance (200-2000 m: beyond 2000 the Offensive block detects nothing) by letting go (no order, overrides cleared, gyros released) and switching Flight then Offensive on (`ActivateBehavior`, read back). No target for N s, flee, low power, a lost AI block or a behaviour switched off: behaviours off, Offensive search locked, back home or to park (`GoRest`, no home connector needed when it can park); one re-launch per 60 s. Out on its own detection it takes no jobs. Combat orders still go to Combat drone profiles only (orchestrator rule); the combat terminal section shows for every profile.

## Designator engage / escort (2026-10-05)
Combat drones (above) take the hostile designator's jobs, and only they do:
- `Engage` task (`DesignatorEngage`; Position = where the designator hit, the entity is tracked): scramble / handover as above; done when the AI has no target (up to 3 more flights at the target while it is still out there) or when the target is **destroyed or disabled** (gone, dead character, a grid construct with no working power producer, or one that had weapons and has none functional left: "Target destroyed"). The job's ReturnHome takes it home / to park. Flee, low power, a lost AI block: Aborted with that reason.
- `Escort` task (`DesignatorEscort`): follows a friendly mobile grid or a character at the **follow distance** (10-1000 m, above it; never closer than both bounding spheres + 5 m) - a route while > 250 m from that station, station keeping once close (`FlightOrder.FollowEntityId`). Enemies the Defensive block detects (within `CombatMaxDetectionRange`) are fought, then it follows again. Ends on low power / hydrogen, recall, or the target gone for 10 s.
- "Use broadcast controller" (LCD section): "Engaging target at GPS:<name>:x:y:z:" (whole metres, the designator's hit) when an engage starts, "Escorting <name>." when an escort starts, "Target destroyed" when the engaged target is down.
- **Point engage** (2026-10-05): a red designator hit on terrain / voxel (no grid or character) is an `Engage` with TargetEntityId 0: the drone flies there and hands over; it is never "lost" and has no target-destroyed check.
- **Interceptors** (2026-10-06): while flying to an engage target, an enemy the AI Defensive block detects (a mobile grid or character, not the target or attached to it, within `CombatMaxDetectionRange`) or fire taken on the way hands over to the AI first; when that fight ends (no targets / threat lost) the drone flies on to the designated target (10 s before the next diversion). Stations / turrets are not interceptors.
- **Route safety**: combat route goals are lifted 40 m + drone radius above terrain (`AboveTerrain`; a goal on the straight line could be under the planet's surface); 6 failed routes in a row: recover ("no route to the target"). Escort station keeping starts / continues only while the straight line to the station is clear (hull fits, no scene hit, terrain clearance; re-checked every second), otherwise a route; the station is lifted above terrain.
- Not saved as combat state: after a load the task starts again.

## Connector sizes (2026-10-05)
Small connectors (`ConveyorNetwork.IsStandardConnector` false) mate with small ones only. Combat drones, ore scouts and ore haulers may use them (`SmallConnectorsAllowed`); other drones ignore them and are in error with no standard one. Bids carry `DockFaces` (standard) and `SmallDockFaces`; LC ports carry `Small`; `DockFit.Pick` matches size and orientation. Ore haulers (profile) take only ore / ice logistics (and mining) and are preferred there.

## Behavior profiles (2026-10-05)
`BehaviourProfile : ushort` bit values: Cargo, Combat, OreScout, OreHauler, Welder, Grinder, Welder + grinder, Mining, Farming (not implemented). A profile is a hard filter only for Combat (combat jobs) and OreHauler (ore / ice logistics and mining only); otherwise it adds `ProfileBonus` (+10) to bids for its preferred jobs (`OrchestratorBlock.PreferredProfile`: Weld -> Welder / Welder + grinder, Grind -> Grinder / Welder + grinder, MineOre -> Mining, ore-only logistics -> OreHauler 10 / Cargo 5).

## Terminal sections (2026-10-05)
Always visible: Enable AI, operation mode, behaviour profile. Then sections behind a header checkbox (local to the player's terminal, `GateSections`; any player can open any section whatever the profile): home connector, flight, power / H2, LCD (incl. broadcast controller), observation area (draw, size, offsets: enabled in StandAlone only), welder (weld unfinished, repair, projections, weld ignore color), grinder (grind color), mining (survey for ores + ore ignore lists), farming (crop ignore lists), combat, jobs, debug.

## Colours and ignore lists (2026-10-05)
- Grind color (default RGB 255,0,0) and weld ignore color (default RGB 255,0,255) match **exactly** (`ColorUtil.SameColor`, the painted HSV mask converted to RGB, no epsilon). Blocks of the weld ignore color are never welded / repaired, nor projected blocks of that color built. Same pair on the construction computer.
- Mining / farming sections: known ores (`MyObjectBuilder_Ore`) / seeds (`MyObjectBuilder_SeedItem`) on the left, the **IGNORED** list on the right, multi-select, add / remove buttons. `IgnoredOres`: the drone bids on no MineOre job for those ores (`WhyNotJob`: "ore ignored"). `IgnoredCrops` is stored for farming (not implemented).

## Job outcome reports, survey, mining (2026-10-02)
- Orchestrator jobs end with a `JobStatusReport` (Completed / Aborted with the reason / Declined when busy) to the announcer and orchestrator, retried up to 10 min while nobody listening is in range. A drone that lost an auction frees its bid at the winner announcement.
- Behavior profile (terminal): Cargo drone / Ore scout (needs an ore detector, error at home) / Combat drone (the three AI blocks) / Ore hauler (ore / ice logistics only) / Rover (wheels not implemented). Orchestrators rank matching drones first.
- Combat detections beyond the server's `CombatMaxDetectionRange` (2000 m) are ignored.
- "Survey for ores" and the Mine task: `DroneControllerBlock.Mining.cs` (see `Mining/CLAUDE.md`). Unload on mining jobs takes every ore aboard.

## Power, docking, keepalive, mining (2026-10-06)
- **Power:** waking from dock (`WakeSystems`), AI off and controller removal set **every** battery to Auto and every tank off stockpile (not only those the drone switched: a replaced controller lost that list and the drone undocked on Recharge batteries, losing all power). Pre-flight lets go of the connector only with power of its own (`OwnPowerProblem`), otherwise it stays docked and the job is aborted with the reason.
- **Dock lock:** on the dock pose (within 0.6 m, still) without a lock for 3 s: the reason is logged once (`DockFailDetail`: connector off, owners, gap along / across, axes, status, subtypes) and the target pressed 0.25 m further in, up to 3 times. The final "Docking failed" report carries the reason.
- **Progress watchdog:** a dock order is measured to its approach point while in transit / aligning, and each phase change counts as progress (a slow settle at the approach point used to time the job out with the connector "13 m away").
- **Keepalive:** on an orchestrator job announced by a construction computer or mining surveyor, `JOB_KEEPALIVE` every 60 s to the announcer (`SendJobKeepalive`).
- **Tool faces:** weld / grind bids carry `ToolFaces` (welder / grinder facing); see TODO/14.
- **Tool reach in bids (2026-10-11):** weld / grind bids also carry `WorkPitchDegrees` / `WorkRollDegrees` (P-Gravity limits, 180 with Align off, capped by the learnt `maxWorkTiltDegrees`) and `CanFlyInverted` (no limits and "down" thrust lifts it 1.2x). The orchestrator ranks with them (Orchestrator/CLAUDE.md, tool orientation ranking).
- **Work site retries (2026-10-11):** `NavigateToolTo` (weld and grind alike) measures progress to the work site itself (`SiteGoalStalled`: no new closest distance for 90 s), not per leg (every replan starts a new leg, which always looked like progress: a grinder circled under a platform deck for 24 min). Stalled or no route: `RedispatchToolTo` chooses the site again without that face (small-grid units: without that side), at most 3 times per block, then the block is skipped. P-Gravity limits / Align changed while on the route legs: the site is chosen again (same faces allowed). Rejects are runtime only, cleared per block and per job.
- **Logistics bids:** none without a usable connector ("no usable connector").
- **Mining:** all drills switch together (`drills`); the bore order is speed-capped all the way (`FlightOrder.SpeedCap`); the bore ends at the load limit (not only full containers) and goes on to unload (CheckLoad doesn't abort a bore); no progress (0.5 m) nor cargo growth for 20 s: "bore stuck"; nothing collected goes home instead of docking to unload nothing.

## Group settings (2026-10-06)
Every drone terminal control supports multi-block selection (settings, sections, observation area and its draw, ignore / capability lists and their buttons, Go home, Stop, debug views) except the home connector section (connector list, scan, name lookup, set current as home, "not station", home beacon) and the debug orders that fly to one point / mount. In a group, a list shows the first selected drone's entries; its buttons apply to each drone.

## Parking (2026-10-06)
See TODO/12: landing gears in any facing (floor / wall / ceiling in gravity, anything in space); staging area only when switched on (nearest); low battery / hydrogen goes to a connector first (GoRest -> GoCharge), parked drones retry it every 30 s, no bids while recharging.

## Flight limits, mining delivery, park landing (2026-10-06)
- **Waypoint tolerance:** 0-500 m (0 = as close as it gets, 0.5 m floor). Full value for player / combat route legs; job legs, docking / work approach points at most 50 m.
- **Min altitude:** every flight (jobs too). The planner drops it for a hop no longer than max(30 m, the altitude) (work at a site, nearby blocks); otherwise straight up after take-off, cruise at least that high, straight down to land.
- **Mining:** "ore coming in" counts the job's ore only (stone kept the bore extending); whatever the bore collected (stone, other ore) is delivered to the logistics computer even with none of the job's ore; a bore start within 4 m of the surface point and not moving is close enough (it used to time out on an old hole's rim). A route that starts in the ground (bore hole) climbs out first (back along the bore, then up: `ClimbOut`); legs taking off below the hull's terrain need skip the terrain check for their first 20 m (`TakingOff`: a drone in its hole was "blocked 0 m ahead" forever).
- **Park landing:** the final approach onto the park surface is not leg-start / route-watch checked (a wall park re-planned its last 13 m over and over). Wind turbines keep parked drones 6 m further away (blades).

## Return to departure (2026-10-06)
Combat drones and ore scouts without a home connector remember where they last took off (`DepartureSpot`, saved; set in pre-flight when letting go of a connector / gears). Recalled, done with a job, or recovering from a fight (`GoRest`): with landing gear they park near that spot (staging area / friendly grids searched around it, not around the drone); without gear, or with no spot there, they fly back and hover 3 m above it (`GoDeparture`; within 15 m / the waypoint tolerance counts as at rest). Auto-launch also works for them now (before: "no home connector and can't park").

## Stone (2026-10-06)
Ore/Stone is on every drone's ignored ores by default (added once on load, `StoneIgnoreDefaulted`; the player may take it off).

## Dock correction, speeds (2026-10-06)
- On the dock pose without a lock: first the target is moved by the gap measured between the two connection points (game geometry, up to 2 corrections; the time allowed and the job's stall clock restart), then the 0.25 m presses. The first miss logs where the error is (order reference vs drone connector, order target vs target connector, mount). A cargo drone (ConnectorMedium) sat 7.6 m above a large Connector on the pose.
- Defaults: Safe speed 2.5 m/s, Approach 10 m/s, Max 50 m/s (new drones; saved values stay).

## Copy / paste settings (2026-10-06)
Terminal buttons under the behaviour profile. Copy (one drone) keeps every `DroneSettingKey` as setting packets in a static, per-player clipboard, except AI on/off, home connector / beacon and 'not station'; the operation mode goes first. Paste (any selection) applies them through `ApplySettingPacket` (same setters, clamping and sync as the terminal); the operation mode is skipped while the target's AI is on. New setting keys: keep the copy loop's upper bound (`DroneSettingKey.IgnoredCrops`) in step.

## Audit fixes (2026-10-06)
- Dock target: the dock frame is built in world space (the anchor's (right, up, forward) coordinates are mirrored; CreateWorld there flipped a side connector's offset: 2 x offset beside the port). `VerifyDockPose` re-computes the pose within 40 m (new transit start from the drone, turn room).
- Watchdog progress: 0.5 m or 30% closer (min 0.05 m); thrust overrides moving off / back to zero are always written (tiny settling throttles were skipped).
- A failed ReturnHome no longer aborts finished work (FinishJob not failed; it tries home again). A skipped-everything construction trip with empty cargo goes home without the empty unload.
- Work sites: a refusal caused by the held attitude (hull past the face plane, `workSiteHullPastFace`) rejects the whole side, so other sides get tried (a non-down grinder only ever tried top faces).
- Pending bid: released when its auction isn't re-broadcast for 35 s (dropped auctions tell nobody).
- MineOre bids need free cargo; warnings: miner not managed by orchestrator / no drill / no ore detector / no cargo. New drones run `MigrateSettings`. Mined ore counts every drill.
- Keepalives go to logistics computers too.

## Terminal selections, tagged orders (2026-10-06)

- Listbox selections (ignored ores / crops, capability exclusions) are static (one terminal open per client) and pruned
  to what the list shows, so with several drones selected every drone's button uses the visible selection, never an
  older invisible one of its own. Buttons no longer clear the selection (the other drones' actions run after).
- `Auction.OnlyDroneEntityId`: a designator order for a tagged drone; other drones don't bid ("ordered for another
  (tagged) drone"). `Profile` exposes the behaviour profile (designator tagging).

## Entry corridors, back-out, precise lines (2026-10-06, TODO/16)

- `DroneControllerBlock.Corridor.cs`: `FindCorridor` works the dock pose backwards: P1 = P0 + `CORRIDOR_SLIDE` (0.75 m)
  along the target connector's axis; from P1, holding the dock attitude, straight out along the axis or out along it and
  then across along one of the target block's axes (an L), to the nearest point T with room to turn (`TurnSweepFree`).
  Checked with `[Pathfinding] DockClearance` (0.5 m), StrictEnds, SeeAll; bounded (0.25 m steps along the axis, 1 m
  across, 120 turn checks). T is moved up to 3 m further out where clear.
- Docking (`StartDocking`): route to T (normal clearance), turn there, corridor legs (`MakeCorridorLeg`: line / arrival
  0.15 m, full attitude, `SpeedCap` = `DockCorridorSpeed` 1.5 m/s capped by Safe speed, not watched), then the slide
  P1 -> P0 at that speed. All anchored to the target connector. No corridor: the old approach point (10 m+ out,
  `WithTurnRoom`). With a corridor the pose is re-checked within 40 m *before* the corridor (`ReplanDockIfMoved`:
  changed -> the docking is planned again); at its end only small corrections (same mount, < 3°, < 0.5 m).
- Undocking (`DeparturePoints`, used by FlyRoute, StartDocking, player routes): straight out by the hull's depth along
  the connector axis + 1.5 m when clear (DockClearance), at Safe speed; else the corridor backwards at corridor speed;
  else straight out anyway (logged). Back-out legs are strict (`BuildHullLegs`: `k < backOuts`).
- Strict orders (`LineTolerance` > 0, Approach phase): cross-track gain 1.2/s (was 0.5) plus a bounded integral
  (0.3 m/s) across the line, reset per order.
- `FlightOrder.SpeedCap` is saved now (ProtoMember 26).
- Waypoints: an attitude change over 25° (was 15°) means a stop (`RUN_THROUGH_COS`).
- Min altitude default 50 m (`IsRequired`: an explicit 0 survives a reload; older saves, which never wrote 0, load 50).

## Limits audit, parking fallback (2026-10-07)
- **Parking:** `ParkMode` = no home connector + landing gear, any operation mode (a player-managed drone isn't moved off a connector it sits on). Not managed by an orchestrator, or its protocol off: any friendly static grid > voxel. Managed, protocol on: Parking area: area > its grid > any friendly grid > voxel; Park on grid: its grid > any friendly grid > voxel; Voxel: voxel around the orchestrator only. Grids: static only.
- **Voxel spots (`FindVoxelSpot`):** any voxel surface (ground, cliff, cave wall / ceiling, asteroid; gravity: floors, walls within ~20° of vertical, ceilings, the drone level; space: any). 256 even rays (150 m) from the centre, first voxel hit past grids, nearest first; second pass from 20 m off the nearest voxel surface (planet ground below, asteroid). `VoxelSpotClear`: no grid block within hull + 3 m, approach room measured along the normal (2..12 m, stored in `ParkSpot.Approach`), the hull corners reachable in straight lines from there. Gap: planet routes are heightmap-based, so a route *into* a planet cave isn't planned (asteroids are scene obstacles).
- **Speed:** `ComputeApproachVelocity` clamps the whole relative vector (along + across + integral) to Max speed / the order's `SpeedCap`. Settings are always obeyed; a timeout (task stall, park, dock lock) while mostly held at a speed setting (`speedBoundShare` >= 50%, ~30 s average) says which one on the terminal / LCD and through the broadcast controller (`NoteSpeedTimeout`). Combat handover lowers an AI Flight speed limit above Max speed to it.
- **P-Gravity limits:** defaults 45° / 45°. `SetExplicitAttitude` clamps to them once (Align to P-Gravity, in gravity). A dock pose beyond them is refused (`WithinGravityLimits`, 1° slack). `OrderPlaceMount` uses `WorkFrame`.
- **Combat alert (LCD + terminal, capability scan):** AI Flight speed limit below Max speed.

## Cargo clearing, work priority (2026-10-07)
- **Clearing (`DroneControllerBlock.Cargo`):** components, ores and ingots in the cargo containers, welder, grinder and drills go into the network of a connector the drone docks at (ice stays with an O2/H2 generator aboard; ammo, bottles, tools always stay). Not for "managed by player" drones.
  - New job (not a resumed one): cleared where it is docked; else a `DockAt` + `ClearCargo` (new `TaskType`, drone-side only) in front, unless the job starts with dock + load (`LoadInventory` clears first). Designator jobs never wait for it.
  - `ReturnHome` with cargo aboard and no home connector: dock + clear at the last cargo connector first (once per job); with a home connector: cleared on arrival.
  - Outside a job (`GoRest`, end of a failed job): `TryStartHousekeeping` = a `JobSource.Housekeeping` job (dock at the home connector / last cargo connector, clear, rest), at most every 5 min (no room, no port known). `lastCargoPortId` is runtime only.
- **Work priority (stand-alone):** `ConstructionWorkPriority` (ProtoMember 69, sync key `ConstructionWorkPriority`), dropdown under Welder; passed to `ScanArea`.

## Repair eligibility, nickname (2026-10-08)
- `IsActiveDroneGroup` (construction targets): a drone is skipped only while operating (`IsOperating`: AI on and working a job, or neither on its landing gear nor docked). Drones at rest or with the AI off are repaired / built like any grid. A drone never works on its own grids (`ToolTargetState`: TargetNotFound -> skipped).
- Nickname: `DroneControllerSettings.Nickname` (ProtoMember 70, sync key `Nickname`, not copied with the settings). Awards log "bid won: <type> :: ..." with the payload, one item a line.

## Mining (2026-10-11)
Live ore reading, capacity-only completion, angled bores for sideways drills, straight back-out, drill inventories unloaded, ice-only first unload: Mining/CLAUDE.md, mining workflow.

## Inventories and busy connectors (2026-10-11)
- Clearing (`ClearCargo`) and job unloads (`UnloadCargo`) take from the cargo containers and every connector, collector, welder, grinder and drill of the drone's construct (`RefreshToolStores`): a welder aborting with job components, a grinder's parts, a drill's ore and stone. What stays aboard is logged per item (network full, or not conveyor-connected).
- DockAt a connector that is taken (another drone parked there, a ship): a free connector of the same station the drone can dock at (size, facing in gravity: `FreeSiblingConnector`) instead; none: wait as before, checking for a free one every 5 s. The DockAt target becomes the drone's clear port (`lastCargoPortId`) when the task starts, so a job failing after a game load still has somewhere to clear its cargo.

## GPS observation area (2026-10-11)
Stand-alone only. Terminal: checkbox "GPS observation area" atop the Observation section, then a list of the player's GPS points (client side; a saved point not in the viewer's list shows with *). On: the area box is centred on the GPS point (`ObservationGps`, settings 71-74 `ObservationAreaAtGps` / `ObservationGpsPosition` / `ObservationGpsName` / `ObservationGpsSet`, synced as `DroneSettingKey.ObservationGps`: Number on/off, Text "name\nx;y;z" invariant), axes still the home connector's, size sliders as before (blocks), offset sliders disabled. The stand-alone scan adds the physical groups of the grids in the area (`ScanArea(..., extraRoots)`), since a structure at the GPS point is usually not linked to the home grid.

## Shared job sites (2026-10-11)
`DroneControllerBlock.SharedSite.cs`. Orchestrator weld / grind awards carry `SharedSite` (several drones on the job, or another weld / grind job awarded within 60 m still has drones out). Such a drone (managed by orchestrator only; saved in the job state) within 40 m of its work site checks the other drones every 10 ticks (route legs: every 20; final approach, work, back-out: never otherwise): its next move (10 m) within its hull + theirs + 3 m of another drone, or another drone's motion (3 s) into it -> it holds still (flight control commands the frame velocity, the order waits, the stall clocks pause). Yields to a drone that doesn't watch; between watching drones whose moves cross, the lower id yields; the mover yields to one that stays put. A drone held for whose move runs into the holder: its site chosen again (`WayAround`). Held 45 s for the same drone: another face (`RedispatchToolTo`). A drone that sees a flagged drone working within 60 m of its site starts watching too. Registry `allDrones` (server, static, cleared on unload).

## Fixes from the 2026-10-11 test (2)
- Docking routes: the leg-start check doesn't look past a docking leg's end (`CheckLegStart`, routeOwner Docking): the staging -> turn point leg ends right at the station (a 0.5 m corridor) and the station itself "blocked" it until the drone gave up (welders never loaded their parts).
- Out of a dock through a corridor (a covered parking bay): when no route is found from the corridor's end, the escape candidates go on the way the departure was heading first (8 / 16 / 32 m, also rising), then the usual ones (`SetDepartureExit`, `DEPARTURE_ESCAPES`, 6 plans). The planner's detours (up / sides of the first obstacle) can't leave a pocket whose way out is behind.
- Idle drones (no job, flight, bid, combat) with cargo clear it every minute (`ClearWhenIdle` -> `TryStartHousekeeping`, CLEAR_RETRY 60 s; a trip to a port that had no room waits 5 min). A drone with no free cargo doesn't bid on jobs that carry something ("cargo full"): the orchestrator refused those bids anyway (grinders parked full, never cleared).
- Unreachable task: with "Use broadcast controller" a chat line with a GPS point of what it couldn't reach (site / dock / home), each 10 m spot once per 10 min (`BroadcastUnreachable`).
- Shared-site watch covers mining too (Orchestrator: MineOre awards near another mining job / split over drones).


## Welder:* profiles, cargo port fallback (2026-10-11)
- Welder:Repair / Projected / Unbuilt: `ReadAuctions` bids on an open auction of its own weld kind when there is one, else the first it can take. Award `WeldCategory` -> `Job.WeldCategory`, `jobAwardWeld` (reports, terminal log "Job n (Weld:Repair): ...").
- `ClearPort`: home connector, last cargo port, else `NearestClearPort` (free dockable connector within 2 km whose logical group has a container below 99 %), remembered as the last cargo port. Grinders without a home connector never dock in their jobs, and a game load forgets the last port ("no connector known").
- `DockableFree` (shared with `FreeSiblingConnector`): working, free, allowed owner, size and facing fit.

## Docked loads, job braking (2026-10-11)
- `DockAt` is done at once when the drone is docked at any connector of the target's network (`DockedInNetworkOf`: same logical grid group, what `BuildHomeNetwork` reads); the bid carries `DockedConnectorId` (Bid 25) and `DockFit.Pick` takes that port first (the drone's own connection no longer counts as occupied), so a load at the station it sits at needs no undock / dock again.
- Job orders (`o == constructionOrder`): the stop is planned with `JOB_BRAKE` 0.3 of the available accel (others `TRANS_BRAKE` 0.5; the command may change by `ACCEL_FRACTION` 0.5, so 40 % is reserve), `JOB_STOP_LAG` 0.3 s x the closing speed kept before the stop point (a fixed 0.75 m margin parked grinders 0.8 m short, never arriving: tools never started; proportional to speed it is 0 at a standstill), speed at most `finalSpeed + JOB_RAMP` (0.45/s) x distance left, and over the curve by `JOB_BRAKE_OVER` 0.4 m/s the command changes at `EMERGENCY_FRACTION` 0.9 (`overCurve`). `StoppableSpeed` and the leg chaining use the same fraction.

## Nicknames in logs, angled bore out of a pit (2026-10-11)
- `Log` puts the block's nickname with the id on lines that start "Drone {0}", "Orchestrator {0}", "LC {0}", "MS {0}", "CC {0}", "Scan {0}", "VNet: {0}" ("Grndr1 (1234...)"), when the first argument is the entity id. Debug-level cost only (`WithNickname` in `Logging.cs`).
- `OreScanner.FindSurface(..., minFree)`: the angled-bore planner needs `MineFreeRun()` (hull length + 6 m, 10..20 m) of open air past the entry. Three free samples (6 m) counted the open air of a pit dug by earlier straight bores as the surface ("0 m of rock"), so the approach line went through the pit's wall.

## Space: leaving a dock sideways to the cameras (2026-10-11)
H29V A / B (scouts, 3 forward cameras, no sensor) planned a 10 km straight leg out of the station while docked (cameras asleep, the stretch "not in view", assumed clear), flew it before any camera looked along it, and hit a station block ("obstacle 0.5 m ahead") with only the first two legs unchecked at their start.
- `FACE_SEEN_MIN` / `FACE_HOLD_TICKS` (`ComputeApproachVelocity`): in space, a watched route leg (not attitude-held job legs) whose direction no working camera covers (`ObservedRange` < min(leg, 30 m); checked every 10th tick, only with a camera on): the speed gate is 0 until it faces the leg (at most 6 s per leg: cameras that never face it, a blind drone, go on as before).
- `CheckLegStart` skips only the back-out legs (`undockLegs`: 1 for a straight back-out, the corridor's legs; was always 2), so the leg after a straight back-out is checked at its start.

## Economy flight, space (2026-10-11)
`DroneControllerBlock.Economy.cs` (TODO/06), setting `EconomyFlight` (ProtoMember 75; 71 was taken by `ObservationAreaAtGps`: the duplicate made every settings save throw, sync key `EconomyFlight`, checkbox in Flight). Watched FlyRoute / Docking legs >= `ECO_MIN_LEG` 1000 m, out of gravity, not attitude-held, not anchored to a moving grid, starting with < 5 m/s sideways and inside the corridor. Phases Point (strongest group = max `EffectiveMax`, turned along the leg, thrusters 0) -> Burn (normal `UpdateTranslation` to `ecoCruise`) -> Coast (`CoastThrusters`: overrides 0, dampeners off; drift corrections past max(25 m, 1 % of the rest) or 2 m/s sideways) -> Flip (group against travel; `FlipSeconds` from `rotAlphaEstimate`) -> Brake (profile with `ECO_BRAKE_FRAC` 0.8) -> Off at `ECO_HANDOVER` 150 m / `ECO_HAND_SPEED` 15 m/s (or the leg's exit speed). Flip too late, no turn in 30 s, or the leg no longer eligible: Off (normal control, `commandedVelocity` = real velocity). In the flight loop `EconomyAttitude` replaces the order's attitude before `UpdateOrientation`; with `ecoActive`, `ComputeApproachVelocity` still runs for its leg bookkeeping (output dropped) and `EconomyTranslate` drives the thrusters. The face-before-go gate is skipped while `ecoActive`.
Coast rest (2026-10-15): after a coast tick that is pointed (< 2°) and still (< 0.01 rad/s) with no flip / pre-flip check / handover due within two rests' travel, `EcoMaybeRest` sets the gyros to hold (zero rates) and `UpdateBeforeSimulation` returns at once for `ECO_REST_TICKS` 10 (`EcoResting`: same order, still Coast; a replan, another phase, AI off or Error end it). The 10-tick update captures the flight state while resting; the first tick back resets `lastLinearVelocity` / `lastAngularVelocity` (gain / α learning). Course checks (`NeedsTrim`) run on the coast ticks.

## Scout sweeps (2026-10-11)
`NavigateFailed`: an area sweep waypoint that fails (unreachable, stalled) is skipped with its Survey (`SkipBlock`), up to `SCOUT_SKIPS_MAX` 3 in a row, then home (before: the first failure ended the sweep; asteroid ring points inside rock did). `StallTimeoutTicks`: scout jobs out of natural gravity stall after max(3 x task timeout, 120 s).

## Asteroid sweeps: low orbits (2026-10-11)
`ScoutPlanner.Orbits` replaces the rings: `ASTEROID_ORBITS` 8 great circles through the aimed spot, tilts golden-angle spaced from a random start, alternate directions; arcs of `ORBIT_ARC_MAX` 1200 m centred on the spot on big asteroids. Each waypoint: `SurfaceRadius` along the centre -> point line (2 m voxel probes from the previous point's radius: out while solid, else up to 60 m out, else in) + altitude. Altitude = clamp(0.3 x range, 8, 30) m, spacing = 1.4 x the footprint radius at that height; range = smallest tagged scout's `OreDetectorRangeMeters`, else 50 m (`ASSUMED_SMALL_RANGE`). Several scouts: orbits round-robin (`Share`). Planets / open space: unchanged.

## No route: reposition and plan again (2026-10-11)
`DroneControllerBlock.Recovery.cs`. `recoverPlanning` is set by `StartDocking`, `DispatchNavigateTo` and `Replan` (wrappers around the `...Core` methods; `DispatchToolTo` keeps its own face / site retries). `PlanPath`: after the plan and the escape spots fail, a wedged drone (the hull doesn't fit at the start in any attitude) first goes back the way it came (`TryRetrace`, below), then the escape spots (fit: any attitude, or the held attitude + 0.25 m: `EscapeSpotFits`), then the way back for one that isn't wedged; then `TryRecoverRoute` puts a reposition spot (15 / 30 m or 2 / 4 hull radii; up, level, diagonals; never down in gravity; hull fits, straight way there, never within max(10 m, 1.5 hull radii) of a place already tried from) as the route's only waypoint, then a straight stretch to the goal. `UpdateRecovery10`: within 40 % of the hop from the spot -> `Replan()` (which may reposition again). `RECOVER_MAX` 3; a plan that works resets the count (`RecoverSucceeded`). Used up / no spot: `routeErrorPending` -> `EnterRouteError` (StopJob, hands off, State.Error, broadcast "Drone NAME: no route after 3 repositions, stopped..."); `routeError` keeps the error through `CheckCapabilities` until Stop / Go home / recall (`ClearRouteError`).

## Keepalives (2026-10-11)
`JOB_KEEPALIVE_TICKS` 20 s (was 60), the first at job start, to the orchestrator too. Announcers (CC, MS, LC, the orchestrator for its designator jobs) watch them with `Util.DroneWatch`: 60 s unheard -> log, event, and (Use broadcast controller) "Drone NAME has not been heard of for 60s!", once per silence; a final report ends the watch.

## Max thrust (2026-10-13)
`MaxThrustPercent` (ProtoMember 76, default 50, slider 0-100 above Max speed, effective at least 1 %): acceleration cap `AccelCap` = that share of the strongest face's thrust (`EffectiveMax`, gains included) / mass, gravity compensation aside. `UsableAccel(u)` = min(what the drone has along u, cap) drives the command rate (`RateLimitCommand`; was 50 % of what it has, `ACCEL_FRACTION` removed) and bounds the acceleration part of `UpdateTranslation`; stops are planned with `PlanBrake` = min(has x TRANS/JOB_BRAKE, cap x 0.8). Over the braking curve (overshoot) the emergency share of what it has still applies. Economy flight in space: burns at the same cap (`EcoThrottleCap`); gravity (quad) mode untouched. LCD header: name, levels, state on the left; per face the thrust the setting allows (min(face, cap), `DroneStatusDisplay.Force`: hundreds padded, 1 decimal, N / kN / MN / GN). Stalls also log a "stall detail" line (command, profile x gate, caps, yielding, economy, face thrust).

## Weld targets checked before the flight (2026-10-13)
`ToolTargetState` (nav start, tool start, strafe check): a projected block not built yet is looked up through the physical grid's projectors (`ProjectedBuildState`): `CanBuild(false) == NotConnected` -> `TaskFailure.NotAttached`; its first component not in the cargo / welder -> `NoPartsAboard` (a ship welder places a projected block only with that at hand). A built, unfinished block with nothing aboard it still needs -> `NoPartsAboard`. Both skip the block before flying to it (they welded air for `PROJECTED_APPEAR_TICKS` each: a line trip that ran out of steel plates went on along the line).

## Bends from thrust (2026-10-13)
Waypoints flown through (run-through / slow approach, rounded corners' chords) were passed at a fixed min(Max, 2 x Approach) whatever the drone could turn with. `TurnPassSpeed(a, w, b)`: the bend as a circle whose chord is the shorter leg (r = chord / (2 sin(turn/2)), a rounded corner gives its arc radius), v = sqrt(0.5 x `UsableAccel(inward)` x r). Applied in `ExitSpeedFor` (the next bend) and in `StoppableSpeed` (every bend of the look-ahead chain), so the drone slows before a bend it couldn't follow. The pathfinder's arc geometry (`RoundCorners`) is unchanged.

## Small-grid welds through welded blocks (2026-10-14)
Site choice for small-grid welds: unit pass (the 3x3x3 work unit's faces), then a reach pass: faces of the block's extent grown by `SMALL_REACH_CELLS` (2) cells, the cells inside ignored for the way in, accepted when the sphere (placed as deep as the hull allows, like the unit pass) reaches the block - a block walled in by welded neighbours is worked through them. Block faces last. Log: "(through welded blocks, min..max)".

## Rest without home or gear; route error retries; arcs (2026-10-14)
- No home connector and no landing gear (and no departure spot to go back to): `RestsAtAnyConnector` - return home docks at the nearest free connector it fits (`GoCharge`); docked anywhere counts as at rest. Return home used to fail every time for such drones (jobs reported Aborted, "trying again" loops).
- Route errors (no route after the repositions) clear by themselves: 5 s when other drones were in the way (was 20 s), 15 s otherwise (was: until Stop / Go home).
- Rounded corners: chords of up to 44° (`CORNER_SEGMENT_DEGREES`, was 22: half the legs); arc points (`PathfindingManager.IsArcPoint`) are run through up to 45° of turn / attitude change (other waypoints: 25°), at the thrust-based bend speed.

## Work approach: direct final leg, contact recovery, closer approach points (2026-10-14)
- **Final approach:** tool orders (FlyRoute's final order, strafes, back-outs) start straight in `Approach` (`FlightOrder.DirectApproach`, `MakeDirect`; anchored: after MatchSpeed). The strict line control (cross-track 1.2/s + integral) pulls the tool onto the line from the loose turn hand-over (up to 1.5 m); the advance waits until within 2x the line tolerance and aligned within 3°. Was: a zero-length transit that had to settle within 0.5 m on a weak cross-track push first - drones hovered at the approach point until the 30 s timeout and went home.
- **Dampeners** are switched off every tick while the drone flies (were only once: switched back on, they fired the thrusters left at override 0 against every small command).
- **Weld "cargo empty"** (`DroneInventoryEmpty`: small-grid welds, partial trips): no component left in the cargo or the welder (`AnyComponentAboard`), not the cargo fill ratio (a few parts in a big container are under 0.1 %: welds ended the moment they started).
- **Contact:** `WatchPush` - the command asks >= 0.2 m/s and the drone moves at < 25 % of it that way for 4 s: pressed against something (logged with direction, obstruction flag, locks). The thrust-gain obstruction flag is logged too. `NavigateToolTo` (not bores): pressed, or no progress for the stall timeout -> `AfterContact`: back out (along the line in when on it, else away from the push / back along the leg / up, 2 m) as the next route's first leg (`TakeContactBackOut`), the site chosen again without that face (3 times, then the block is skipped). Stall log: the order's real end attitude, dampeners, throttles, integrals, push, locks.
- **Contact elsewhere (2026-10-14):** `BackOut` pressed -> stopped there, a contact back-out (away from the push, not along the line: that leads back to the face) for the next route. `DockAt` / `ReturnHome` on the way (not the dock order, not parked) pressed -> `ContactOnTheWay`: contact back-out + `Replan()` (the docking route takes it too), `CONTACT_REPLANS_MAX` 3 per task.
- **Checked back-outs:** `FreeRun(hullCentre, dir, len)` sweeps the hull (held attitude) and returns how far it can go (obstacle's near face - half hull along dir - 0.5 m). `DispatchBackOut` shortens the back-out to that (< 0.5 m: none, the next route starts there); `TakeContactBackOut` likewise. (Weld07 backed 8 m blind into the shipyard frame and was pinned there.)
- **Way back (`DroneControllerBlock.Trail.cs`):** `RecordTrail10` keeps hull centres every 1.5 m while out of the dock (64, cleared docked / parked / jumps > 50 m). `TryRetrace`: back along it holding the current attitude, each stretch `SegmentBlocked` (built over: stop), at points where `HullFits` a full `PlanPathFrom` (3 at most); the way back becomes the route's first legs. Why: drones get into tight spots through precise checks (weld sites with the real shape, tilted approaches, exact-box turns) while every way out used the any-attitude sphere (escape spots, Theta* cells, reposition spots) - Weld07 / Weld08 hovered with "no way from here or from 14 spots around it", Theta* expanding 1 cell.
- **Pre-load site check (`DroneControllerBlock.SiteProbe.cs`):** `StartJob` (orchestrator / stand-alone weld jobs, from the start): `ProbeJobSites` runs each block's site search (`DispatchToolTo(..., probe: true)`: faces, approaches, way in; no route, nothing flown) in order until one has a site, 25 ms budget. None: the job ends before dock / load (`jobAbortReason`). Blocks found without a site before the first good one are skipped at their turn without a second search. Every Unreachable block of the job (`jobUnreachable`) goes into the outcome report (`FillUnreachable`: `UnreachableGrids` / `UnreachableCells`, `PerBlock`; the reason notes the count when the last failure was something else).
- **Approach points:** work sites start where the hull (work attitude) clears the face plane by the turn margin (`ClosestApproach`, at least 1 m of straight run), then move out 1 m at a time over the first 6 m, 2.5 m after, until the turn there is free (`WithTurnRoom`), up to length + 2.5 m + 10 m. Was: length + 2.5 m before any check (~10-15 m out). Face choice needs tool reach + hull radius + margin clear in front (`TurnRoomOut`), not the full manoeuvre distance. Docks and mining keep their distances. The dispatch log gives the distance out.
- **Physics wake (2026-10-14):** Havok puts a still body to sleep and the game applies thruster force only to awake bodies (`AddForce` activeOnly); it wakes the grid only on an override change or when it already moves. Under a steady small command (settled at an approach point, after a shared-site hold, docking legs) drones sat still with thrust "on" (0.01 m/s for minutes). `KeepPhysicsAwake`: every tick the controller asks for thrust or torque, the drone's grids are activated (logged once per order).

- **Power guard (2026-10-14):** out in the field (not docked, not parked, not in pre-flight) with no power of its own to fly (`OwnPowerProblem`: batteries empty / off / stuck on Recharge and no other producer) or no thruster working: stopped in State.Error with the reason, broadcast once, checked again every 30 s (`DroneControllerBlock.PowerGuard.cs`). No flight, jobs, housekeeping, stand-alone scans, bids or auto-home meanwhile; power back: Standby. An order given meanwhile (Go home, Execute) is stopped again at the next check.
- **Stall log:** connector status per drone connector (Connectable = another connector's magnet holds it).

## Weld sites by shape (2026-10-14)
- Weld work sites (any weld job) only; routes, turns, grind / mine sites keep the bounding box.
- **Drone shape** (`HullShape`, `DroneControllerBlock.HullShape.cs`): the drone's blocks (fat blocks by their model box, others by their cells) in the controller frame, rasterized at half the smallest cell, merged into exact runs, then to at most 24 boxes (least added empty space), biggest first; rebuilt when the blocks change. When the bounding box's way in is blocked, each shape box is swept straight in on its own (`ShapeWayInFree`, berth 0.3 m, front WORK_STANDOFF off the face as for the box); none in the way: the site is accepted. INI `[Drone] WeldUseDroneShape`.
- **Neighbours by real shape**: blocks of the target grid in the way by their cell box are checked again with rays along the sweep through its cross-section against the physics (`PathQuery.RealShapeGrid`, `PathfindingManager.RealShapeClear`): only a hit inside the block's cells blocks. 9-25 rays per block, 600 per site choice (then cell boxes count). INI `[Drone] WeldUseNeighbourShapes`.
- Dispatch log: "(N site(s) fit by the drone's shape, M neighbour cell(s) clear by their real shape, R rays)".

## Weld through a neighbour (2026-10-14)
`DispatchToolTo` weld site passes end with pass 3: the block's faces covered by a neighbour the welder may reach
through (`ConstructionComputer.WeldThroughFace`, passed to `ChooseFace` as `passable`); `WorkSite` decides with the
neighbour's real shape (rays end where the hull front stops). Log "(through the neighbour on that face)".
`CheckLegStart` checks a leg in its own explicit attitude (a held route turns at its start). The pre-load check
doesn't log "needs a X to be placed, none aboard" (`siteProbing`).

## No turns / contact replans near docks (2026-10-14)
`NearDockArea(p)`: within max(25 m, 2 x the room to turn) of the connector being docked at (`dockAreaSet` while
`StartDocking` plans, or `dockingHomeId`), or of the departure end. There `PathQuery.NoHeldTurn` (Lazy Theta* may hold
the attitude, never turn to another) and `ContactOnTheWay` does nothing (the docking's watch / stall timeout decide).
Drones turned 120° next to the platform (held route) and backed off / went round again from 8 m out, hitting it.

## Local-job radius, return to where it left from (2026-10-14, `DroneControllerBlock.LocalArea.cs`)
- Terminal Jobs: **Local-job radius** (`settings.LocalJobRadius`, proto 77, default 15 000 m, 1..30 000; sync key
  LocalJobRadius). `WhyNotJob` -> `OutsideLocalRadius`: the auction's site (`PositionData`, none: no check) must lie
  within it of `BasePoint()` (home connector, else the departure spot, else the drone). Tagged jobs
  (`OnlyDroneEntityId` = this drone) are exempt. (Laser-linked orbit / ground bases sent miners 70 km.)
- No home connector: `RememberDeparture` also stores `DepartureGridId` (proto 78): the grid docked / parked on, or the
  nearest friendly static grid (voxel / nothing). `ParkSearchCentre` = departure spot for every profile (was combat /
  scouts only); `GoPark` tries the departure grid after the orchestrator's directive, before any friendly grid;
  `GoCharge(rest: true)` (RestsAtAnyConnector) prefers a free connector of the departure grid, then the nearest to
  the departure spot. `FinishJob` sends RestsAtAnyConnector drones to rest too (they used to stay where the job ended).
  Drones with a home connector: unchanged (ReturnHome / GoHome).
- Not covered: the supplier LC of a logistics job may still be far (TODO/21, related gap).

## Dampener hold (2026-10-15, `DroneControllerBlock.Hold.cs`)
Hold phases hand the drone to the game's dampeners and skip the flight tick: dock wait (`dockWaitTarget`), shared-site yield (`siteYielding`), standby after a flight with no job / legs left (`arrivalReported`, State.Standby). Entered after a normal flight tick (`MaybeDampenerHold`, not during economy flight) when still (< 0.15 m/s, < 0.02 rad/s), pointed (< 0.05 rad) and, except a yield, within max(0.5 m, arrival tolerance) of the order's target: `ClearAllThrustOverrides` (overrides 0, dampeners on), gyros hold (zero rates, override on). Not for orders anchored to a grid, while parked / boring / in pre-flight. Each tick `DampenerHolding` checks same order + same reason (else ends); every 10 ticks the flight state is captured and `CheckHoldDrift` ends it past 1 m from where it began, 0.5 m/s or 0.05 rad/s (then no new hold for 120 ticks). The first tick back resets `lastLinearVelocity` / `lastAngularVelocity` / `commandedVelocity` (`flightTickSkipped`, shared with the coast rest); `UpdateTranslation` switches the dampeners off again.
