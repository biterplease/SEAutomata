## Drone Controller

### Behavior profiles

The behaviour profile tells Orchestrators what this drone *prefers* to do. A drone still bids on any job its equipment allows, but drones with a matching profile always win over the others.

| Profile | Preferred jobs | Notes |
| --- | --- | --- |
| Cargo drone | Item deliveries and pickups between Logistics Computers | |
| Ore hauler | Ore and ice deliveries | Takes **only** ore / ice deliveries (and mining, if it has a drill). Can dock with small connectors. |
| Welder:All | Weld jobs of every kind | |
| Welder:Repair | Weld jobs, repair first | Ranks first for repair jobs (damaged blocks), and bids on them first when several auctions are open. Still takes the other weld jobs, ranked below Welder:All. |
| Welder:Projected | Weld jobs, projections first | The same, for projected build jobs. |
| Welder:Unbuilt | Weld jobs, unbuilt blocks first | The same, for unbuilt jobs (placed blocks not welded up yet). |
| Grinder drone | Grind jobs | |
| Welder + grinder drone | Weld and grind jobs | |
| Mining drone | Mining jobs | Needs a drill and an ore detector. |
| Ore scout | Industrial designator scouting | The **only** drones that take area sweeps, and the first choice for spot checks (any drone with an ore detector can take those). Needs an ore detector. Can dock with small connectors. |
| Combat drone | Hostile designator engage / escort | The **only** drones that take combat jobs. Needs AI Defensive, AI Flight and AI Offensive blocks. Can dock with small connectors. |
| Farming drone, Rover | | Not implemented yet. |

You can also stop a drone from bidding with some of its abilities in the **Jobs** section (for example, "no jobs in space" even though it can fly there), and tell mining drones to ignore specific ores.

### Home connector and home beacon

Drones can be configured with a **home connector**. When set, this drone will always return there to dock, recharge, refuel, and drop off leftover cargo.

- Pick one of your (or your faction's) connectors from the **Home connector** list. Use **Scan** to refresh the list, or type a connector's name to find one farther away.
- Best results: dock the drone by hand, then press **Set current as home**. The drone remembers that exact docking position and orientation.
- Give each drone its own home connector. Two drones sharing one will fight over it.
- If the home connector is on something that moves (a carrier, a mining ship), tick **Home connector is not station** and select a **home beacon** on the same ship. The drone uses the beacon to find its way back to the moving home.

Drones with landing gear and no home connector can still park on nearby grids or the ground, and combat drones and ore scouts return to where they took off.

A drone without a home connector clears leftover cargo (grinder salvage, a welder's unused components) at the last connector it loaded or unloaded at, or, if it knows none, at the nearest free connector within 2 km whose conveyor network has cargo room.

### Stand-alone mode

A stand-alone drone works by itself, without any computers or Orchestrator. It watches a small **construction field** around its home connector (up to 10 x 10 x 10 blocks, which you can move with the offset sliders or centre on a GPS point instead) and:

- welds unfinished blocks, repairs damaged ones, and builds projections,
- grinds down blocks painted with the **grind color**,
- never touches blocks painted with the **weld ignore color**.

It loads the components it needs from the conveyor network of its home connector, does as much as it can carry, comes back, and repeats. You can choose the order it works in (repair, projections, unbuilt). Turn on **Show construction field** to see the box.

Stand-alone drones are lack the capabilities to coordinate large jobs, so two stand-alone drones observing the same area may go for the same block, bump into each other, or crash. For several drones on one project, use the Orchestrator.

### Managed by Orchestrator mode

In this mode the drone waits at home and bids on jobs from Orchestrators in antenna range. This is the mode for logistics, mining, large construction projects, and designator jobs. The drone flies out, does its task list, reports back and returns home. It still watches its battery and hydrogen levels and goes home to recharge when needed.

**Low power** (Monitor battery / hydrogen levels, on by default): under the refuel threshold every drone drops its job at once, whatever it was doing (an orchestrator's job is reported aborted and auctioned again), backs out of a block or bore first, then docks to charge: at home, else at the nearest free connector. Cargo of the dropped job (ore, a haul) stays aboard and goes where that job meant to unload it, once the levels are back over the operational thresholds and before the drone takes new work.

Combat drones in this mode also launch on their own when their AI Defensive block spots an enemy within the **auto-launch radius**.

The operation mode can only be changed while the drone's AI is off.

### Economy Flight

A checkbox in the drone's **Flight** section, off by default. In space, legs of 1 km or more are flown to save fuel: the drone turns its strongest thruster group along the way, burns up to a cruise speed, coasts, turns around and burns to brake. That group does the pushing; the other thrusters only correct drift off the line (while burning, coasting and braking), and a drift of more than 50 m (or a course that would miss the end by that much) makes the drone work out a new course to the leg's end. The last 150 m of each leg are flown normally. Short legs, work, docking and anything that changes the route mid-leg fall back to normal flight. In natural gravity, legs of 500 m or more are flown like a quad-copter: the lift group (Up or Down) carries and moves the drone, the other thrusters only correct drift, and the drone tilts it, within the Align to P-Gravity limits, to move, and keeps heading for a point on the line ahead of it, so a drone pushed off course turns back towards the leg's end. It needs a lift group that carries 1.3 times the drone's weight. Otherwise a drone in natural gravity travels level, its down thrust along gravity; the Align to P-Gravity limits are only for its work, for tight corridors and for this tilt. The last 100 m of each leg are flown normally. Coasting, the drone turns its camera along the way; its cruise speed is capped so the brake fits inside what it sees ahead, and the rest of the route is checked again before it turns round.

### Flight lanes

Drones managed by an Orchestrator fly in lanes, like air corridors. The Orchestrator traces a lane as soon as it puts a job up for auction (from its base to the job: another base, an asteroid, a planet ore hotspot - known deposits within 500 m of each other - or the spot itself), when an area sweep or spot check is ordered, and between your bases on its antenna network that have an Automata computer block. A lane is a pair of boxes 40 m wide and 40 m high, one above the other: one for each way, so drones going opposite ways never meet head-on. Lanes end short of bases and above work areas (at an asteroid, beside the rock over the work, never inside it), run straight over the terrain on planets (from ridge to ridge, in as few legs as they can, climbing straight up where an end starts low), and are lifted where they would cross another one. Each drone flies in a slot of its own across the box (one and a half times its width, with four times its length clear before and after it), so a drone can pass one that stops; when no slot is free at the entrance, it waits outside for a short while. Drones wider than 25 m fly beside the lane instead. In its slot a drone keeps a gap it can stop in behind the drone ahead, even if that one brakes as hard as it can. When a drone finds rock, a tree or a station in a lane, it flies round it and tells the Orchestrator (as soon as its antenna reaches it): the lane is moved up to 50 m to a clear straight line, or else redrawn along the drone's way round. A crash in a lane or two drones too close suspends it for 2 minutes; a drone with a broadcast controller says "NAME: crashed in lane L7 Home Base → Asteroid 4821". A lane made for a sweep or spot check is dropped if the scouts find nothing. The Orchestrator's terminal lists the lanes (**Active lanes**) to show, delete, resume or clear them all; `/automata debugdraw lanes` shows the ones near you. Unused lanes are dropped after FlightLaneExpirationMinutes.

### When a drone finds no route

When a straight line (with a few detours) finds no way, the planner tries the ways drones have already flown (a memory shared by all drones and saved with the world), then a Lazy Theta* search around the obstacle (out of asteroid pockets and caves, through cluttered bases). If none of that finds a way, on a trip home, to a dock or to a waypoint (or when the route ahead gets blocked), a drone flies to a free spot close by, a different one each time, and plans again from there. After 3 tries it stops in an error state and broadcasts it. Press **Stop** or **Go home**, or recall it with the designator, once the way is clear.

### Which jobs a drone bids on

| Job | What the drone needs |
| --- | --- |
| Weld / repair / build | A welder and cargo room for components |
| Grind | A grinder and cargo room for the salvage |
| Item delivery / pickup | Cargo room and a standard connector (ore haulers: ore and ice only) |
| Mining | A drill, an ore detector and free cargo; the ore must not be on its ignore list |
| Scouting (industrial designator) | Spot check: an ore detector. Area sweep: Ore scout profile and an ore detector |
| Engage / escort (hostile designator) | Combat drone profile and the three AI blocks |

On top of that, the drone must be able to fly where the job is (atmosphere or space), have power, and not be busy. Its **Max load** settings decide how much it agrees to carry.


## Construction Computer

The Construction Computer watches a large box around itself (up to 50 x 50 x 50 blocks, movable with the offset sliders) and looks for:

- unfinished blocks, damaged blocks and projections to weld (each can be turned on or off),
- blocks painted the grind color to grind,
- skipping anything painted the weld ignore color.

It sees every grid in the box that you, your faction, or nobody owns, including floating projections that aren't attached to anything.

**Many drones at once.** This is where the Construction Computer shines. Instead of one block at a time, it groups the work into jobs of several blocks (10 by default) and keeps several jobs open at the same time. Each job can be split over several drones, and different jobs go to different drones, so a whole swarm can build one projection together. Projections are built centre-first and bottom-up so the structure grows sensibly, and drones working close to each other watch out for one another and wait or pick another side instead of colliding.

Useful settings: **Weld priority**, **Blocks per job**, scan interval, and **Show construction field** to see the box.

**Weld priority.** Weld jobs come in three kinds: **Repair** (damaged blocks), **Projected** (projections) and **Unbuilt** (placed blocks not welded up yet). The Construction Computer publishes jobs of every kind that has work at the same time, so drones set to Welder:Repair, Welder:Projected or Welder:Unbuilt always find their own kind of job. The order you pick sets each kind's share of the open jobs (MaxPublishedJobs): 3 : 2 : 1, first to last, with grinding getting the same share as the first kind. With 10 slots and all three kinds waiting that is 5 / 3 / 2. A kind with less work than its share leaves the rest to the others. The logs name the kind of every weld job (Weld:Repair, Weld:Projected, Weld:Unbuilt).

A stand-alone drone's own Weld priority is a strict order: it finishes one kind before starting the next.

### Which bids its jobs need

| Job | Drone | Logistics Computer |
| --- | --- | --- |
| Weld / repair / build | Has a welder and cargo room. Welder:Repair / Projected / Unbuilt rank first for their own kind of weld, then Welder:All and Welder + grinder, then the other Welder:* profiles. | Has the components, with **Provide for construction** on |
| Grind | Has a grinder and cargo room. Grinder and Welder + grinder drones rank first. | Has room for the parts, with **Provide for construction** on |

Drones whose welder or grinder can easily reach the blocks (for example, a downward-facing welder for the top of a structure) also rank higher. A weld job is never given out without a Logistics Computer that has the components.


## Server settings

Server admins can tune the mod in **Automata.ini**, in the mod's own folder (for a Workshop install, the mod's folder under `steamapps\workshop\content\244850\`, or the dedicated server's `content` folder). Keep a copy of your edited file: a Workshop update replaces it.

- Only the server reads the file, once, when the world loads. Restart the world after editing it.
- Times in **ticks** are game frames: 60 ticks = 1 second.
- Values outside the allowed range are clamped to it. Missing or unreadable values use the default.
- When the world loads, the chat says "Server configuration loaded successfully", or "No config file found, using defaults".
- Settings marked *Not used yet* are read (or reserved) but don't change anything in this version.

### [Pathfinding]

How drones plan their routes and see obstacles. These are the main CPU settings for flying.

| Setting | Default | What it does |
| --- | --- | --- |
| AllowDirectPathfinding | true | The drones' route planner. Off: drones fly straight lines to their targets without checking for obstacles. Leave it on. |
| AllowLazyTheta | true | Route planner stage 3: when the straight line (with a few detours) and the route memory find no way, a Lazy Theta* search looks for one around the obstacle (out of asteroid pockets and caves, through cluttered bases). Off: those routes fail and the drone repositions. (Replaces AllowAStar, which is no longer read.) |
| ThetaSearchRadius | 300 | Metres the search covers around the stretch that failed (50 to 2000). Further goals are searched for part of the way. |
| ThetaMaxExpansions | 3000 | Most cells one search looks at (100 to 100000). |
| ThetaMaxMs | 15 | Most milliseconds one search may take (1 to 500). |
| PathSearchWorkers | 2 | Lazy Theta* searches run on this many worker threads (0 to 8; 0 = on the main thread, as before). |
| PathSearchMaxMs | 50 | Most milliseconds one worker search may take (1 to 1000). |
| PathWarmUp | true | Prepare the obstacle data of busy areas (bases with Automata blocks, projector surroundings, construction fields) ahead of the first route there. |
| StraightMaxMs | 10 | Most milliseconds the straight-line stage (detours) may take before the later stages take over (0 to 1000; 0 = no limit). |
| MemoryMaxMs | 10 | Most milliseconds the route-memory stage may take (0 to 1000; 0 = no limit). |
| UsePathMemory | true | Route planner stage 2: the server remembers where drones have flown (shared by all drones, saved with the world) and plans through those known-free ways when the straight line fails. |
| PathMemoryMaxNodes | 4000 | Most remembered points (100 to 100000); the least recently used are forgotten first. |
| PathMemoryMaxAgeHours | 24 | Hours a remembered way may go unused before it is forgotten (1 to 8760). |
| FlightLaneExpirationMinutes | 240 | Minutes a flight lane may go unused before it is dropped (5 to 525600). Lanes are always on. |
| AllowSensors, AllowObstacleAvoidance, AllowSimpleRepositioning, AllowDStarLite, UseGravityAwarePathing, RepositionStep | | *Not used yet.* |
| UsePlanetAwarePathfinding | true | On planets, long routes follow the planet's curve and climb over terrain. Off: straight lines, which can run into hills and mountains. |
| MinWaypointDistance, MaxWaypointDistance | 5, 15 | *Not used yet.* |
| MaxRepositionAttempts | 10 | How many detours one route may take around obstacles, and how many times a drone re-plans a blocked route before the task fails. |
| MinAltitudeBuffer | 20 | Metres kept above the highest terrain when a route climbs over a hill (the drone's own **Min altitude** is used when higher). 0 to 1000. |
| MaxPathNodes | 1000 | Most points in one route (at least 100). Very long planet routes use wider steps to stay within it. |
| MaxPathfindingTimeMs | 50 | *Not used yet.* |
| RequireCamerasForPathfinding | true | On: drones only see far-off obstacles through working, switched-on cameras, each looking along its facing from where it is mounted (six cameras cover every direction). Off: every drone sees as if it had one camera on the front of its hull, looking forward. |
| RequireSensorsForPathfinding | true | On: drones only see close obstacles within range of a working, switched-on sensor block, around where that sensor is mounted (the sensor's own range settings don't matter): a long ship with one sensor at an end only senses around that end. Drones without one get a warning in their header. Off: every drone senses that range all around its hull. |
| CameraRange | 1500 | How far a camera sees, in metres (10 to 5000). |
| SimulatedSensorRange | 100 | How far a sensor sees, in metres (1 to 500). |
| RaysPerTick | 60 | Obstacle checks per tick, shared by all drones on the server (5 to 1000). Lower uses less CPU, but drones notice obstacles later when many are flying. |
| AllowRepathing | true | Something blocks the route: the drone plans a way around it. Off: the drone stops and the task fails. |
| ObstacleClearance | 3 | Metres kept between the hull and obstacles when routes are checked (0 to 20). Higher is safer, but tight spaces may become unreachable. |
| CruiseAltitude | 40 | (Not in the shipped file; add it to change it.) On planets, long flights cruise at least this many metres above the terrain: straight up after take-off, straight down to land. 0 to 1000. |
| ArcMinDistance | 300 | On planets, routes longer than this (metres) follow the planet's curve (50 to 100000). |
| ArcStep | 200 | Shortest straight piece of such a curved route, in metres (25 to 10000). |
| ArcSag | 25 | How far a straight piece may dip below its ends, in metres (1 to 500). Bigger means fewer, longer straight pieces. |
| DockClearance | 0.25 | Metres kept around the hull when flying in and out of hangars and docking bays (0.1 to 3). |
| DockClearanceMin | 0.1 | Tried when no way in fits with DockClearance (tight doors). The drone then flies in at half speed (0.05 to 3). |
| DockCorridorMaxLength | 40 | How far from a connector, in metres, the drone looks for a straight way in with room to turn (10 to 200). |

### [Drone]

| Setting | Default | What it does |
| --- | --- | --- |
| TaskTimeoutSeconds | 30 | The highest **Task timeout** a player can set on a drone (10 to 600). A task that stops making progress for that long fails. |
| DebugDrawTimeoutSeconds | 3600 | The debug overlays (drone construction field and navigation targets, construction computer field, orchestrator parking area and lanes, `/automata debugdraw`) switch themselves off after this many seconds (10 to 3600). |
| DefaultMonitorHydrogen, DefaultMonitorBattery, DefaultHydrogenRefuelThreshold, DefaultHydrogenOperationalThreshold, DefaultBatteryRefuelThreshold, DefaultBatteryOperationalThreshold, MinPowerThreshold, MaxPowerThreshold | | *Not used yet.* Each drone uses its own terminal settings. |
| ComponentCheckIntervalSeconds | 600 | How often a drone re-checks all of its own blocks (60 to 86400). Adding or removing blocks triggers a check anyway. |
| ConnectorScanIntervalSeconds | 60 | How often a drone's home connector / beacon lists may refresh on their own. |
| ConnectorScanMinMoveMeters | 250 | ...but only if the drone moved at least this far since the last refresh... |
| ConnectorScanMaxAgeSeconds | 600 | ...or the list is older than this. |
| ConnectorManualScanIntervalSeconds | 60 | Shortest time between two presses of **Scan** on one drone (5 to 3600). |
| ConnectorScanRadius | 1000 | Radius of the automatic connector / beacon lists, in metres (100 to 5000). |
| ConnectorNameSearchRadius | 15000 | Radius of **Add to list** (search by name), and the farthest a home connector or beacon may be from the drone, in metres. |
| ConnectorNameSearchIntervalSeconds | 10 | Shortest time between two name searches on one drone. |
| WeldUseDroneShape | true | Weld jobs: where the drone's bounding box doesn't fit the way in to a block, its real shape is checked (its blocks merged into a few boxes), so a welder snout can reach between built neighbours. |
| WeldUseNeighbourShapes | true | Weld jobs: built blocks next to the target that are in the way are checked against their real shape (slopes, panels), not as full cubes. |
| CombatMaxDetectionRange | 2000 | Enemies the AI Defensive block reports farther away than this (metres) are ignored (100 to 50000). The vanilla block sees 2000 m; raise it only for modded AI blocks. |

### [ConstructionComputer]

| Setting | Default | What it does |
| --- | --- | --- |
| DroneMaxJobsPerScan | 125 | Stand-alone drones: most blocks taken from one scan of the construction field (1 to 1000). |
| DroneScanIntervalSeconds | 30 | Stand-alone drones: seconds between scans while idle, and the pause after a failed trip (5 to 3600). |
| MaxPublishedJobs | 10 | Construction Computers: jobs open at once that no drone has taken yet (1 to 100). Higher lets more drones start at the same time, at the cost of more auctions. |

### [Orchestrator]

| Setting | Default | What it does |
| --- | --- | --- |
| MaxConcurrentAuctions | 10 | Auctions one Orchestrator runs at once (1 to 100). Further jobs wait in its queue. |
| NoBidTimeoutSeconds | 60 | An auction with no drone bid for this long is dropped, and the computer that announced it is told (10 to 600). |
| FailedJobHoldSeconds | 60 | How long that computer then waits before announcing the job again (10 to 7200). The **Retry jobs** buttons skip the wait. |

### [ScoutPointer]

The target designators.

| Setting | Default | What it does |
| --- | --- | --- |
| PendingGpsColor | 255,210,0 | Colour of an industrial designator order's GPS marker while it waits for a drone. `R,G,B` or `#RRGGBB`. |
| ConfirmedGpsColor | 40,220,60 | Colour of the marker once every job of the order has a drone. |
| CombatGpsColor | 139,0,0 | *Not used yet.* Combat orders have no marker, since the target moves. |
| ConfirmTimeoutSeconds | 10 | A designator order that no suitable drone takes within this many seconds is cancelled (3 to 300). |

### [Mining]

| Setting | Default | What it does |
| --- | --- | --- |
| ScanCellsPerUpdate | 8 | How much of the voxel world one ore scan reads at a time (32 m cells, every 10 ticks; 1 to 128). The CPU cap of ore scanning: higher finishes scans faster but costs more. |
| MaxScanRadius | 300 | Ore scans reach at most this far, in metres, whatever the ore detector's range (50 to 2000). |
| DepositMergeMeters | 50 | The same ore found within this distance of a known deposit counts as that deposit (10 to 500). |
| MaxDepositsPerSurveyor | 500 | Deposits one Mining Surveyor remembers; the farthest are forgotten first (10 to 5000). |
| MaxPublishedJobs | 5 | Mining jobs one surveyor has open at once that no drone has taken yet (1 to 50). |
| SurveyorScanIntervalSeconds | 120 | Seconds between a Mining Surveyor's scans with the ore detector on its own grid (10 to 3600). |
| DroneSurveyIntervalSeconds | 10 | Seconds between a surveying drone's scans; it must also have moved half a scan radius (2 to 600). |
| MaxGpsMarkers | 30 | Most deposit markers **Show known deposits** puts on the GPS, nearest first (1 to 200). |

### [SchedulerBounds]

| Setting | Default | What it does |
| --- | --- | --- |
| BidCollectionWindowTicks | 300 | How long an Orchestrator collects bids before picking the winners (at least 60). Longer gives better picks but slower starts. Big jobs wait a bit longer for more drones. |
| BidBlacklistCooldownTicks | 3600 | How long a Construction Computer holds a block back after a drone gave it up (at least 60). Blocks a drone couldn't reach are always held 5 minutes. |
| MaxTargetLimit, PerScanLimit, MaxTaskAssignmentPerBatch, ScanDelayTicks, ErrorRecoveryIntervalTicks, MaxConsecutiveErrors, MaintenanceIntervalTicks | | *Not used yet.* |

The **[SchedulerBounds.StateUpdateIntervalTicks]** section (Initializing, Error, Standby, Scanning, Assigning) is *not used yet*.

### [MessageQueue]

The virtual radio network the Automata blocks talk over.

| Setting | Default | What it does |
| --- | --- | --- |
| MessageRetentionTicks | 600 | How long a message lives on the network: readers in range that look within this time get it (at least 600). |
| MessageCleanupIntervalTicks | 600 | How often expired messages are cleaned up (at least 60). |

### [Logging]

| Setting | Default | What it does |
| --- | --- | --- |
| LogLevel | Error, Warning | What goes into the mod's log file: any combination of `Verbose`, `Info`, `Debug`, `Warning`, `Error`, or `Quiet` for nothing. `Debug` logs every auction, bid and drone decision: useful to find out why a drone doesn't take a job, but the file grows fast. |
| LogPathfinding, LogDroneNetwork, LogDroneOrders | | *Not used yet.* |

The log is **Automata.log**, in `%AppData%\SpaceEngineers\Storage\<mod id>.sbm_Automata\` (on a dedicated server, the same `Storage` folder of the server's instance).

### [LogisticsComputer]

Not in the shipped file; add the section to use it.

| Setting | Default | What it does |
| --- | --- | --- |
| AllowLogistics | true | Off: Logistics Computers lose their **Provide for logistics**, **Push** and **Request** options, and the other work mode options are locked. |
| AllowNetworkPush | true | Off: the **Push** option is hidden. |
| AllowNetworkRequests | true | Off: the **Request** option is hidden. |
| MinPushFrequencyTicks, MaxPushFrequencyTicks | | *Not used yet.* |

These settings only change the terminal: computers already set to a mode keep it.

### Sections not used yet

- **[Session]**: UpdateInterval (and the unlisted EnableLogistics, EnableWeldTasks, EnableGrindTasks) don't change anything yet.
- **[BlockLimits]**: block limits per player and faction are not enforced yet.
- **[Performance]** and **[Safety]**: old settings, no longer read.
