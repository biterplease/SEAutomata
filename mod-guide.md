# Automata: Player Guide

## Current status, what's implemented, what's planned

This mod is still very early in development. While some of its features work, a lot of it still needs polishing, namely:
- Welding and grinding: works 80% of the time, there are times where drones get stuck and need manual rescue, or simply burn all their battery/fuel trying to navigate to a block that they should be able to without issue
- Mining is inefficient: because stone gets in the way, drones mine A LOT of stone before they can actually bring some useful ores
- Timing issues: in order to reduce effect on simulation speed, actions are often throttled and deferred, this leads to getting feeling that things are not happening at all.

The above said, welding, grinding, mining, logistic deliveries, ore scouting, combat, as well as the docking, takeoff, navigation, and healthcheck of the drone, work at acceptable levels, but I'm still working to make them better and more reliable.

### Still untested:
- Server stuff: multiplayer infrastructure is there, and used everywhere in the mod, but it's only ever been tested locally
- Moving grids: while the drones are expected to work with reference beacons on moving grids, has not been tested yet

### Planned features:

- Quarry mining: mining drones HAVE to be borers, i.e. their hull needs to fit the hole that their drills make. With Quarry mining, a drone with a single drill to be able to clear its own area to maneuver and mine successfully.
- Farm harvesting jobs: drone with a collector harvests farmed ores.
- Rovers: for the really massive cargo move jobs, rovers should be available.
- Mod config menu: live update server settings
- Models: LG drone controller, all computer, still using vanilla models, custom models with custom actions are underway.

## Introduction

Automata lets you create drones that work for you. Put a **Drone Controller** on a grid that has thrusters, a connector and whatever tools you want it to use (welder, grinder, drill, ore detector, cargo), and the drone figures out by itself what it is able to do. A single drone can already weld, repair and grind around its home on its own.

The mod introduces 4 blocks that lets you scale your activities, 3 of them specify on checking for a specific type of job, and the **Orchestrator** collects those jobs and hands them to the drones best suited for them. Everything talks over your antennas, so every Automata block needs a broadcasting antenna on its grid, and the blocks only hear each other within antenna range.

The mod adds five blocks and two hand-held tools:

- **Drone Controller**: The workhorse, that allows a drone to check its own capabilities, and sign up to do the work distributed by the Orchestrator. It's the only block that actually does work: weld, grind, deliveries, mining.
- **Orchestrator**: the dispatcher. It turns job announcements into auctions, picks the winning drones and gives them their orders. Usually one per base network is enough.
- **Logistics Computer**: Analyzes inventory on its grid, keeps player-configured item min/max in its memory, and dispatches jobs to keep those amounts healthy. A grid needs one to take part in logistics.
- **Construction Computer**: Scans a large area for blocks to grind, weld, or repair, and announces them as jobs.
- **Mining Surveyor**: It collects ore deposit data and turns ore orders into mining jobs.
- **Industrial Target Designator** and **Hostile Target Designator**: hand-held tools to send drones somewhere by pointing: ore scouting with the industrial one, combat with the hostile one.

## How jobs get assigned

Every job managed by an Orchestrator goes through the same four steps:

1. **Announcement**: a computer (Construction, Logistics or Mining Surveyor) finds work and announces it: "these 10 blocks need welding", "this grid needs 500 steel plates", "mine 10,000 kg of iron here".
2. **Auction**: the Orchestrator opens an auction for the job and broadcasts it to every drone and Logistics Computer in range.
3. **Bidding**: drones that can do the job bid with their position, cargo room and tools. Logistics Computers that can supply the items (or take them) bid too. Most jobs need both: a drone to do the work, and a Logistics Computer to load from or unload to.
4. **Winner selection**: after a few seconds the Orchestrator picks the best bids. Drones whose behaviour profile matches the job always rank first, then the closest and best equipped. Big jobs are split over several drones and trips. The winners get their task lists, the Logistics Computers reserve the items or room, and the announcer stops repeating the job.

If nobody bids, the job is put on hold for a few minutes and announced again later. Every computer has a **Retry jobs** button to skip the wait.

## General settings

These apply to all blocks, and provide quality of life to the mod's usage.

### Nicknames

All blocks have a "Nickname" field, that allows you to set a friendly-name to your drone or computer. This name will be shown in logs, and broadcasted messages.


### Use broadcast controller

If this is toggled, and there's a broadcast controller in their grid, Automata blocks will use the broadcast controller to message you about important updates.

### LCD output

All blocks log output to an LCD on their grid. An app called "Automata log" is added to the apps section of the LCD.

In order to see the logs, you need to both:
- Select the "Automata log" app
- Tag the LCD screen name according to the block you want to see, the defaults are
    - [AutomataDrone]
    - [LogisticsComputer]
    - [ConstructionComputer]
    - [MiningSurveyor]
    - [Orchestrator]

The logs will show relevant data to the automata block, such as jobs taken, reasons for failure, retry timers, etc.

## Drone Controller

### Behaviour profiles

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
| Ore scout | Industrial designator scouting | The **only** drones that take scouting jobs. Needs an ore detector. Can dock with small connectors. |
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

A stand-alone drone works by itself, without any computers or Orchestrator. It watches a small **observation area** around its home connector (up to 10 x 10 x 10 blocks, which you can move with the offset sliders or centre on a GPS point instead) and:

- welds unfinished blocks, repairs damaged ones, and builds projections,
- grinds down blocks painted with the **grind colour**,
- never touches blocks painted with the **weld ignore colour**.

It loads the components it needs from the conveyor network of its home connector, does as much as it can carry, comes back, and repeats. You can choose the order it works in (repair, projections, unbuilt). Turn on **Show observation area** to see the box.

Stand-alone drones are lack the capabilities to coordinate large jobs, so two stand-alone drones observing the same area may go for the same block, bump into each other, or crash. For several drones on one project, use the Orchestrator.

### Managed by Orchestrator mode

In this mode the drone waits at home and bids on jobs from Orchestrators in antenna range. This is the mode for logistics, mining, large construction projects, and designator jobs. The drone flies out, does its task list, reports back and returns home. It still watches its battery and hydrogen levels and goes home to recharge when needed.

Combat drones in this mode also launch on their own when their AI Defensive block spots an enemy within the **auto-launch radius**.

The operation mode can only be changed while the drone's AI is off.

### Which jobs a drone bids on

| Job | What the drone needs |
| --- | --- |
| Weld / repair / build | A welder and cargo room for components |
| Grind | A grinder and cargo room for the salvage |
| Item delivery / pickup | Cargo room and a standard connector (ore haulers: ore and ice only) |
| Mining | A drill, an ore detector and free cargo; the ore must not be on its ignore list |
| Scouting (industrial designator) | Ore scout profile and an ore detector |
| Engage / escort (hostile designator) | Combat drone profile and the three AI blocks |

On top of that, the drone must be able to fly where the job is (atmosphere or space), have power, and not be busy. Its **Max load** settings decide how much it agrees to carry.

## Logistics Computer

### Conveyor networks

The Logistics Computer sees its grid as one or more **conveyor networks**: groups of containers, connectors, refineries, assemblers and tanks that can move items between each other. Drones dock at a free **large connector** of a network to load or unload; small connectors are ignored, with some exceptions: drones designated as ore haulers, scouts, and combat drones can still use small. lconnectors.

Your Min / Max settings apply to the **main network**: the one with the most cargo room and a free standard connector. That is where deliveries arrive and surplus leaves.

### The four work modes

The modes are checkboxes, so you can combine them.

- **Provide for logistics**: offers this grid's items to other Logistics Computers' requests (never going below its own Min for that item), and its free room for their surplus (never going above its own Max). Also needed to receive mined ore.
- **Provide for construction**: offers components for weld jobs (drones dock here to load them) and room for what grind jobs bring back.
- **Request (below Min)**: when an item drops below its Min, the computer asks for the difference. A drone brings it from another Logistics Computer.
- **Push (above Max)**: when an item goes above its Max, the excess is announced as surplus, and a drone takes it away to another Logistics Computer.

By default a new computer has both Provide modes on.

### How to configure it

1. Place the Logistics Computer on the grid, with a broadcasting antenna and at least one free standard connector on the network you want drones to use.
2. Tick the work modes you want.
3. Set item limits in the **Min / Max** lists: select an item in the top list, edit the entry as `item:min:max` (for example `SteelPlate:100:5000` or `Ingot/Iron:0:20000`, where 0 means "not set"), then press **Edit and add**. You can also type an entry and press **Add manually**.
4. Mins only do something with **Request** on, Maxes only with **Push** on (the computer warns you otherwise).
5. For mining: by default it only accepts ore its working refineries can process. Tick **Accept ore from mining without a refinery** to use it as plain ore storage. Ice goes to grids with an O2/H2 generator.

A typical setup: the main base on **Provide for logistics + Provide for construction**, and an outpost on **Request** with Mins for the components it needs, or a mining outpost on **Push** with Maxes on its ore.

### Which jobs it bids on

| Job | What it offers | Needs mode |
| --- | --- | --- |
| Weld / repair / build | The components | Provide for construction |
| Grind | Room for the salvaged parts | Provide for construction |
| Item delivery (another computer's request) | The items, above its own Min | Provide for logistics |
| Item pickup (another computer's surplus) | Room, below its own Max | Provide for logistics |
| Mining | Room for the ore, with a refinery that takes it (or the "without a refinery" option); ore and its ingot below Max | Provide for logistics |

A computer never bids on its own requests.

## Construction Computer

The Construction Computer watches a large box around itself (up to 50 x 50 x 50 blocks, movable with the offset sliders) and looks for:

- unfinished blocks, damaged blocks and projections to weld (each can be turned on or off),
- blocks painted the grind colour to grind,
- skipping anything painted the weld ignore colour.

It sees every grid in the box that you, your faction, or nobody owns, including floating projections that aren't attached to anything.

**Many drones at once.** This is where the Construction Computer shines. Instead of one block at a time, it groups the work into jobs of several blocks (10 by default) and keeps several jobs open at the same time. Each job can be split over several drones, and different jobs go to different drones, so a whole swarm can build one projection together. Projections are built centre-first and bottom-up so the structure grows sensibly, and drones working close to each other watch out for one another and wait or pick another side instead of colliding.

Useful settings: **Weld priority**, **Blocks per job**, scan interval, and **Show observation area** to see the box.

**Weld priority.** Weld jobs come in three kinds: **Repair** (damaged blocks), **Projected** (projections) and **Unbuilt** (placed blocks not welded up yet). The Construction Computer publishes jobs of every kind that has work at the same time, so drones set to Welder:Repair, Welder:Projected or Welder:Unbuilt always find their own kind of job. The order you pick sets each kind's share of the open jobs (MaxPublishedJobs): 3 : 2 : 1, first to last, with grinding getting the same share as the first kind. With 10 slots and all three kinds waiting that is 5 / 3 / 2. A kind with less work than its share leaves the rest to the others. The logs name the kind of every weld job (Weld:Repair, Weld:Projected, Weld:Unbuilt).

A stand-alone drone's own Weld priority is a strict order: it finishes one kind before starting the next.

### Which bids its jobs need

| Job | Drone | Logistics Computer |
| --- | --- | --- |
| Weld / repair / build | Has a welder and cargo room. Welder:Repair / Projected / Unbuilt rank first for their own kind of weld, then Welder:All and Welder + grinder, then the other Welder:* profiles. | Has the components, with **Provide for construction** on |
| Grind | Has a grinder and cargo room. Grinder and Welder + grinder drones rank first. | Has room for the parts, with **Provide for construction** on |

Drones whose welder or grinder can easily reach the blocks (for example, a downward-facing welder for the top of a structure) also rank higher. A weld job is never given out without a Logistics Computer that has the components.

## Mining Surveyor

### Collecting ore surveys

The Mining Surveyor keeps a list of known ore deposits. They come from two sources:

- **Its own ore detector**: an ore detector on the surveyor's grid is scanned regularly.
- **Survey drones**: any drone with a working ore detector and **Survey for ores** ticked records the ore it sees while flying, and hands the data to a Mining Surveyor once it is back in antenna range. Ore scouts sent out with the industrial designator are the quickest way to explore.

Deposits that a later scan no longer finds are marked as depleted. Stone is never listed. Press **Show known deposits** to put them on your GPS for a few minutes.

### Configuring mining jobs

1. The **Known ores** list shows the ores of the deposits found so far.
2. Select an ore, enter an order as `ore:kg` (for example `Ore/Iron:100000` or `Iron:50000`) and press **Add / update**.
3. **Job size** sets how many kg each mining job asks for (one drone trip; default 10,000 kg).
4. Each finished job is counted off the order; at 0 the jobs stop. Tick **Keep orders constant** to keep mining as long as deposits are known.
5. Removing an order stops new jobs for it; drones already out finish their trip.

The surveyor always sends drones to the nearest known deposit. Large jobs are shared by several miners boring side by side.

### Which bids its jobs need

| Bidder | Requirement |
| --- | --- |
| Drone | A drill, an ore detector and free cargo; the ore not on its ignore list. Mining drones rank first. |
| Logistics Computer | **Provide for logistics** on, room for the ore, a refinery that processes it (or "accept ore without a refinery"), and the ore / its ingot below Max. Ice prefers grids with an O2/H2 generator. |

Without a Logistics Computer to take the ore, no mining job is given out: your drones never mine ore with nowhere to put it.

## Manual jobs with the target designators

The two designators are hand-held tools. Press **Reload** to cycle modes, **Fire** to point. You must be within antenna range of one of your Orchestrators, and the drones you want to use must be **Managed by Orchestrator**. The designator's lights and sounds tell you whether the order was sent (yellow), accepted by a drone (green / blue), or refused (red). If no suitable drone takes the order within about 10 seconds, it is cancelled.

Both designators share two extra modes:

- **Tag**: point at your own drones to tag them (up to 32). The next order goes to every tagged drone, one job each. Without tags, one suitable drone is picked for you.
- **Recall**: point at one of your drones to call it home. It drops its current job.

### Scouting (Industrial Target Designator)

Only drones with the **Ore scout** profile and an ore detector take scouting jobs.

- **Spot check**: point at the ground or an asteroid. A scout flies there, scans for ore and comes back. With several tagged scouts, they spread around the spot.
- **Area sweep**: point at an area. A scout sweeps it (low passes over a planet, rings around an asteroid). With several tagged scouts, the area is split between them.

A GPS marker shows the order: yellow while waiting for a drone, green once taken. Everything the scouts find goes to your Mining Surveyors, ready to be ordered.

### Combat (Hostile Target Designator)

Only drones with the **Combat drone** profile and AI Defensive, AI Flight and AI Offensive blocks take combat jobs.

- **Engage**: point at an enemy or neutral grid or character, or at a spot on the ground. The drone flies there, and once within its **handover distance** switches on its AI Flight and AI Offensive blocks to fight. It returns home when the target is destroyed or disabled, or when it has had no target for a while. Pull the trigger again to send another drone.
- **Escort**: point at one of your own moving grids or characters. The drone follows above it at the **follow distance**, fights any enemy that comes close, then resumes following. Point at the ground right next to yourself to have it escort you.

Combat drones come home on their own when power or hydrogen runs low, when their AI Defensive block decides to flee, or when an AI block is lost. You can tune all of this in the drone's **Combat** section.

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
| AllowAStar | false | *Not available yet.* Turning it on only logs a warning. |
| AllowSensors, AllowObstacleAvoidance, AllowSimpleRepositioning, AllowDStarLite, UseGravityAwarePathing, RepositionStep | | *Not used yet.* |
| UsePlanetAwarePathfinding | true | On planets, long routes follow the planet's curve and climb over terrain. Off: straight lines, which can run into hills and mountains. |
| MinWaypointDistance, MaxWaypointDistance | 5, 15 | *Not used yet.* |
| MaxRepositionAttempts | 10 | How many detours one route may take around obstacles, and how many times a drone re-plans a blocked route before the task fails. |
| MinAltitudeBuffer | 20 | Metres kept above the highest terrain when a route climbs over a hill (the drone's own **Min altitude** is used when higher). 0 to 1000. |
| MaxPathNodes | 1000 | Most points in one route (at least 100). Very long planet routes use wider steps to stay within it. |
| MaxPathfindingTimeMs | 50 | *Not used yet.* |
| RequireCamerasForPathfinding | true | On: drones only see far-off obstacles through working, switched-on cameras, each looking along its facing (six cameras cover every direction). Off: every drone sees as if it had cameras all around. |
| RequireSensorsForPathfinding | true | On: drones only see close obstacles all around them through a working, switched-on sensor block (the sensor's own range settings don't matter). Drones without one get a warning before take-off. Off: every drone has that close-range sight. |
| CameraRange | 1000 | How far a camera sees, in metres (10 to 5000). |
| SimulatedSensorRange | 50 | How far a sensor sees, in metres (1 to 500). |
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
| DebugDrawTimeoutSeconds | 3600 | The observation area and navigation target overlays switch themselves off after this many seconds (10 to 3600). |
| DefaultMonitorHydrogen, DefaultMonitorBattery, DefaultHydrogenRefuelThreshold, DefaultHydrogenOperationalThreshold, DefaultBatteryRefuelThreshold, DefaultBatteryOperationalThreshold, MinPowerThreshold, MaxPowerThreshold | | *Not used yet.* Each drone uses its own terminal settings. |
| ComponentCheckIntervalSeconds | 600 | How often a drone re-checks all of its own blocks (60 to 86400). Adding or removing blocks triggers a check anyway. |
| ConnectorScanIntervalSeconds | 60 | How often a drone's home connector / beacon lists may refresh on their own. |
| ConnectorScanMinMoveMeters | 250 | ...but only if the drone moved at least this far since the last refresh... |
| ConnectorScanMaxAgeSeconds | 600 | ...or the list is older than this. |
| ConnectorManualScanIntervalSeconds | 60 | Shortest time between two presses of **Scan** on one drone (5 to 3600). |
| ConnectorScanRadius | 1000 | Radius of the automatic connector / beacon lists, in metres (100 to 5000). |
| ConnectorNameSearchRadius | 15000 | Radius of **Add to list** (search by name), and the farthest a home connector or beacon may be from the drone, in metres. |
| ConnectorNameSearchIntervalSeconds | 10 | Shortest time between two name searches on one drone. |
| CombatMaxDetectionRange | 2000 | Enemies the AI Defensive block reports farther away than this (metres) are ignored (100 to 50000). The vanilla block sees 2000 m; raise it only for modded AI blocks. |

### [ConstructionComputer]

| Setting | Default | What it does |
| --- | --- | --- |
| DroneMaxJobsPerScan | 125 | Stand-alone drones: most blocks taken from one scan of the observation area (1 to 1000). |
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

The virtual radio network the Automata blocks talk over. The shipped file calls this section **[DroneNetwork]**, but the server reads these settings from **[MessageQueue]**.

| Setting | Default | What it does |
| --- | --- | --- |
| MessageRetentionTicks | 1800 | How long a message waits on the network for its readers before it expires (at least 600). |
| MessageCleanupIntervalTicks | 600 | How often expired messages are cleaned up (at least 60). |
| MessageSerializationMode | ProtoBuf | Message format. `XML` is only for debugging; keep `ProtoBuf`. |
| AllowNetworkBroadcasting, DefaultAntennaRange, NetworkUpdateRate, SchedulerAntennaCacheUpdateIntervalTicks, SchedulerMessageReadLimit | | *Not used yet.* Antenna ranges always come from the actual antennas. |

### [Logging]

| Setting | Default | What it does |
| --- | --- | --- |
| LogLevel | Info, Verbose, Debug, Error, Warning | What goes into the mod's log file: any combination of `Verbose`, `Info`, `Debug`, `Warning`, `Error`, or `Quiet` for nothing. `Debug` logs every auction, bid and drone decision: useful to find out why a drone doesn't take a job, but the file grows fast. |
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
