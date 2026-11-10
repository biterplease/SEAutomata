
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
